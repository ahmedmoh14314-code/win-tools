using System.Buffers.Binary;
using SharpCompress.Archives;
using SharpCompress.Archives.Rar;
using SharpCompress.Archives.SevenZip;
using SharpCompress.Common;
using SharpCompress.Common.Rar;
using SharpCompress.Readers;
using WinTools.Core.Files;
using WinTools.Core.Jobs;

namespace WinTools.Worker.Archive;

internal static partial class ArchiveJobs
{
    /// <summary>
    /// A 7z or RAR (RAR 4 or RAR 5) archive, read with SharpCompress (ADR-0030). The library only parses and
    /// decompresses: every entry still goes through <see cref="ReadAll"/> (names, links, the one budget, sizes counted as
    /// they come out, the staging folder), as a ZIP's or a TAR's does. What the library does not do is done here:
    /// <list type="bullet">
    /// <item>A 7z's CRC-32s are checked by <see cref="ReadAll"/>: the library does not check them (measured, 0.50.7).</item>
    /// <item>A RAR's check value is checked by the library only for files without a password. RAR 4 keeps the plain
    /// CRC-32 of a protected file, which <see cref="ReadAll"/> checks. RAR 5 keeps a keyed form of it (or a BLAKE2
    /// hash) that only its key derivation can make, so a protected RAR 5 file whose value does not match is reported as
    /// not checked rather than failed (<see cref="Entry.CrcUnreliable"/>). A wrong password is still caught by RAR 5's
    /// own password check.</item>
    /// <item>Every declared size, kept or not, is held to the size and whole-archive rules before anything is written,
    /// so a solid block that would have to be decompressed to skip a refused entry cannot run past them unseen.</item>
    /// <item>An archive in several parts is refused, never guessed at: only the dropped file is ever opened.</item>
    /// <item>Whatever the library throws on a hostile file is a damaged archive (or a wrong password, or too much
    /// memory), never a crash.</item>
    /// </list>
    /// A 7z, and a solid RAR, is read from start to end through one reader, so a solid block is decompressed once.
    /// </summary>
    private sealed class LibraryArchive : IDisposable
    {
        /// <summary>The values a fact may hold (static, so the tables can translate them).</summary>
        public const string ContentsProtected = "Contents protected with a password";
        public const string NamesProtected = "Names and contents protected with a password";
        public const string NoErrorsWhereChecked = "No errors in the files that could be checked";
        public static JobFact UncheckedNotice { get; } = new("Not checked", "Password-protected RAR 5 files cannot all be checked for damage", Notice: true);

        private static ReadOnlySpan<byte> SevenZipSignature => [0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C];
        private static ReadOnlySpan<byte> Rar4Signature => [0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x00];
        private static ReadOnlySpan<byte> Rar5Signature => [0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x01, 0x00];
        private const int ReparsePoint = 0x400, SevenZipUnixExtension = 0x8000;
        private const int UnixTypeMask = 0xF000, UnixLink = 0xA000, UnixRegular = 0x8000, UnixFolder = 0x4000;

        private readonly IArchive? _archive;
        private readonly Kind _kind;
        private readonly bool _rar5, _hasPassword;
        private readonly long _fileLength;
        private bool _prepared;

        /// <summary>The names are encrypted and no password was given: nothing can be listed (Info says so).</summary>
        public bool NamesHidden { get; }

        /// <summary>One part of a RAR in several parts (".part1.rar", ".r00"): refused (ADR-0030).</summary>
        public bool IsMultiPart { get; }

        public bool IsSolid { get; }
        private bool AnyEncrypted { get; }
        private long DeclaredBytes { get; }

        public bool IsLocked => !_hasPassword && (NamesHidden || AnyEncrypted);

        public string TypeName => (_kind == Kind.SevenZip ? "7z archive" : _rar5 ? "RAR 5 archive" : "RAR 4 archive") + (IsSolid ? ", solid" : "");

        /// <summary>The 7z or RAR a file's first bytes name, or null.</summary>
        public static (Kind Kind, bool Rar5)? KindOf(ReadOnlySpan<byte> head) =>
            head.StartsWith(SevenZipSignature) ? (Kind.SevenZip, false)
            : head.StartsWith(Rar5Signature) ? (Kind.Rar, true)
            : head.StartsWith(Rar4Signature) ? (Kind.Rar, false)
            : null;

        /// <summary>Whether the file at a path starts like a 7z or RAR (for the password retry, which only they need).</summary>
        public static bool IsOne(string path)
        {
            try
            {
                using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
                Span<byte> head = stackalloc byte[8];
                return KindOf(head[..file.ReadAtLeast(head, head.Length, throwOnEndOfStream: false)]) is not null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
        }

        public LibraryArchive(FileStream file, Kind kind, bool rar5, string? password)
        {
            (_kind, _rar5, _hasPassword, _fileLength) = (kind, rar5, password is not null, file.Length);
            if (kind == Kind.SevenZip) CheckSevenZipEnd(file);
            file.Position = 0;
            var options = new ReaderOptions { Password = password, LeaveStreamOpen = true, LookForHeader = false };
            try
            {
                _archive = Guard(() => kind == Kind.SevenZip ? SevenZipArchive.OpenArchive(file, options) : (IArchive)RarArchive.OpenArchive(file, options), encrypted: true);
                // Only the dropped file is opened, so a RAR in several parts is one part: its entries cross into files
                // this job was not given. The library marks every part of such a set.
                if (kind == Kind.Rar && Guard(() => _archive.Volumes.OfType<Volume>().Any(v => v.IsMultiVolume), encrypted: true))
                {
                    IsMultiPart = true;
                    return;
                }
                IsSolid = Guard(() => _archive.IsSolid, encrypted: true);
                // The headers only, nothing decompressed: how many entries, whether any is encrypted, and every declared size.
                (DeclaredBytes, AnyEncrypted) = Guard(() =>
                {
                    int n = 0;
                    long bytes = 0;
                    var locked = false;
                    foreach (var e in _archive.Entries)
                    {
                        if (++n > ArchiveLimits.MaxEntries) throw new ArchiveFailure(ErrorCode.LimitExceeded, $"The archive has more than {ArchiveLimits.MaxEntries:N0} entries.");
                        if (e.Size < 0) throw new ArchiveFailure(ErrorCode.DecodeFailed, "The archive is damaged (an entry declares a negative size).");
                        if (!e.IsDirectory) bytes = bytes > long.MaxValue - e.Size ? long.MaxValue : bytes + e.Size;
                        locked |= e.IsEncrypted;
                    }
                    return (bytes, locked);
                }, encrypted: true);
            }
            catch (ArchiveFailure ex) when (ex.Code == ErrorCode.PasswordRequired)
            {
                // The names themselves are encrypted (7z's encrypted header, RAR's "-hp"): nothing to list without the password.
                _archive?.Dispose();
                _archive = null;
                NamesHidden = true;
            }
            catch
            {
                _archive?.Dispose();
                throw;
            }
        }

        /// <summary>
        /// A 7z names where its directory is in a 32-byte start header. One that points past the end of the file is cut
        /// short, or is the first file of an archive split into several (".7z.001"). Checked before the library reads it,
        /// which also keeps its allocation of the directory to what the file holds (GHSA-7jcq-g98g-83v5).
        /// </summary>
        private static void CheckSevenZipEnd(FileStream file)
        {
            Span<byte> start = stackalloc byte[32];
            file.Position = 0;
            if (file.ReadAtLeast(start, start.Length, throwOnEndOfStream: false) < start.Length)
                throw new ArchiveFailure(ErrorCode.DecodeFailed, "The archive is cut short.");
            var offset = BinaryPrimitives.ReadUInt64LittleEndian(start[12..]);
            var size = BinaryPrimitives.ReadUInt64LittleEndian(start[20..]);
            var room = (ulong)file.Length - 32;
            if (offset > room || size > room - offset)
                throw new ArchiveFailure(ErrorCode.DecodeFailed, "The archive is cut short, or is the first part of an archive split into several files, which WinTools does not read.");
        }

        /// <summary>For Info: one fact instead of a listing when there is nothing to list, or null.</summary>
        public JobFact? Summary() =>
            NamesHidden ? new JobFact("Protection", NamesProtected, Notice: true)
            : IsMultiPart ? new JobFact("Parts", "One part of an archive split into several files, which WinTools does not extract yet", Notice: true)
            : null;

        /// <summary>
        /// Before Extract or Test writes anything: a part of several is refused, and the declared sizes of every entry
        /// meet the limits (TAR's first pass does the same, ADR-0023). Sizes are still counted as they come out.
        /// </summary>
        public void Prepare()
        {
            if (_prepared) return;
            if (NamesHidden) throw new ArchiveFailure(ErrorCode.PasswordRequired, "The archive's names and contents are protected with a password.");
            if (IsMultiPart) throw new ArchiveFailure(ErrorCode.UnsupportedContent, "The archive is one part of an archive split into several files, which WinTools does not extract yet.");
            if (DeclaredBytes > ArchiveLimits.MaxTotalBytes) throw new ArchiveFailure(ErrorCode.LimitExceeded, $"The archive declares more than {ArchiveLimits.MaxTotalBytes >> 30} GB.");
            if (ArchiveLimits.IsArchiveBomb(DeclaredBytes, _fileLength)) throw new ArchiveFailure(ErrorCode.LimitExceeded, "The archive declares far more than its own size; it looks like an archive bomb.");
            _prepared = true;
        }

        public IEnumerable<Entry> Entries(bool listing)
        {
            if (_archive is null || IsMultiPart)
                throw new ArchiveFailure(IsMultiPart ? ErrorCode.UnsupportedContent : ErrorCode.PasswordRequired, "The archive cannot be listed.");
            if (listing) return _archive.Entries.Select(e => Map(e, () => throw new InvalidOperationException("A listing is not read.")));
            Prepare();
            // A non-solid RAR opens each file on its own; a 7z or a solid RAR is read once, in order, through one reader.
            return _kind == Kind.Rar && !IsSolid
                ? _archive.Entries.Select(e => Map(e, () => Guard(e.OpenEntryStream, e.IsEncrypted)))
                : Sequential(_archive);
        }

        private IEnumerable<Entry> Sequential(IArchive archive)
        {
            using var reader = Guard(archive.ExtractAllEntries, encrypted: AnyEncrypted);
            while (Guard(reader.MoveToNextEntry, encrypted: AnyEncrypted))
            {
                var e = reader.Entry;
                yield return Map(e, () => Guard(reader.OpenEntryStream, e.IsEncrypted));
            }
        }

        private Entry Map(IEntry e, Func<Stream> open)
        {
            var name = e.Key ?? "";
            if (e.IsDirectory && !name.EndsWith('/') && !name.EndsWith('\\')) name += "/";
            var encrypted = e.IsEncrypted;
            return new Entry(name, e.IsDirectory, IsLink(e), IsSpecial(e), encrypted, encrypted && !_hasPassword,
                () => new GuardedStream(open(), this, encrypted),
                e.IsDirectory ? 0 : e.Size,
                // 7z: the CRC-32, which the library does not check. RAR without a password: the library checks it (CRC-32
                // or BLAKE2). RAR 4 with one: the plain CRC-32. RAR 5 with one: a keyed value, checked when it matches.
                _kind == Kind.SevenZip || encrypted ? (uint)e.Crc : null,
                // A solid block's packed bytes are shared: only a non-solid RAR has a packed size per file.
                _kind == Kind.Rar && !IsSolid ? e.CompressedSize : -1,
                e.LastModifiedTime ?? DateTime.Now,
                CrcUnreliable: _rar5 && encrypted);
        }

        /// <summary>
        /// A link of any kind is never created: RAR 5's redirections (symbolic links, junctions, hard links, file copies),
        /// a Windows reparse point, a 7z entry with a Unix link mode, or a RAR 4 entry made on Unix with one.
        /// </summary>
        private bool IsLink(IEntry e)
        {
            if (e is RarEntry { IsRedir: true }) return true;
            if (AttributesOf(e) is not { } a) return false;
            if ((a & ReparsePoint) != 0) return true;
            return _kind == Kind.SevenZip ? (a & SevenZipUnixExtension) != 0 && ((a >>> 16) & UnixTypeMask) == UnixLink
                : !_rar5 && (a & UnixTypeMask) == UnixLink;
        }

        /// <summary>A 7z entry with a Unix mode that is neither a file, a folder nor a link (a device, a pipe, a socket).</summary>
        private bool IsSpecial(IEntry e)
        {
            if (_kind != Kind.SevenZip || AttributesOf(e) is not { } a || (a & SevenZipUnixExtension) == 0) return false;
            var type = (a >>> 16) & UnixTypeMask;
            return type is not (0 or UnixRegular or UnixFolder or UnixLink);
        }

        /// <summary>All 32 bits of an entry's attributes: SharpCompress gives a 7z's Unix mode (the high 16 bits) only in
        /// <see cref="SharpCompress.Common.SevenZip.SevenZipEntry.ExtendedAttrib"/>.</summary>
        private static int? AttributesOf(IEntry e) => e is SharpCompress.Common.SevenZip.SevenZipEntry { ExtendedAttrib: { } all } ? all : e.Attrib;

        /// <summary>Runs a library call; what it throws on a hostile file becomes a failure the user can read.</summary>
        private T Guard<T>(Func<T> call, bool encrypted)
        {
            try { return call(); }
            catch (Exception ex) when (ex is not ArchiveFailure) { throw Translate(ex, encrypted); }
        }

        private ArchiveFailure Translate(Exception ex, bool encrypted)
        {
            var crypto = ex is SharpCompress.Common.CryptographicException or System.Security.Cryptography.CryptographicException;
            if (ex is OutOfMemoryException or InsufficientMemoryException)
                return new(ErrorCode.LimitExceeded, "Reading the archive needs more memory than WinTools allows one job.");
            if (crypto && !_hasPassword)
                return new(ErrorCode.PasswordRequired, "The archive is protected with a password.");
            // RAR 5 keeps a password check value: a wrong password is told apart from damage.
            if (crypto && _rar5 && ex.Message.Contains("password did not match", StringComparison.OrdinalIgnoreCase))
                return new(ErrorCode.PasswordIncorrect, "The password does not open the archive.") { PasswordMayBeWrong = true };
            if (_hasPassword && (crypto || encrypted))
                return new(ErrorCode.DecodeFailed, "The archive is damaged, or the password is not its own.") { PasswordMayBeWrong = true };
            if (ex is NotSupportedException or NotImplementedException)
                return new(ErrorCode.UnsupportedContent, "The archive uses a method WinTools does not read.");
            if (ex is EndOfStreamException or IncompleteArchiveException)
                return new(ErrorCode.DecodeFailed, "The archive is cut short.");
            return new(ErrorCode.DecodeFailed, "The archive is damaged.");
        }

        /// <summary>An entry's data as the library decompresses it, with its exceptions translated.</summary>
        private sealed class GuardedStream(Stream inner, LibraryArchive owner, bool encrypted) : Stream
        {
            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

            public override int Read(byte[] buffer, int offset, int count) => owner.Guard(() => inner.Read(buffer, offset, count), encrypted);
            public override int Read(Span<byte> buffer)
            {
                try { return inner.Read(buffer); }
                catch (Exception ex) when (ex is not ArchiveFailure) { throw owner.Translate(ex, encrypted); }
            }

            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

            protected override void Dispose(bool disposing)
            {
                if (disposing) owner.Guard(() => { inner.Dispose(); return 0; }, encrypted);
                base.Dispose(disposing);
            }
        }

        public void Dispose() => _archive?.Dispose();
    }
}
