namespace WinTools.Core.Capabilities;

/// <summary>
/// Programs the user installs and WinTools never ships or downloads (ADR-0033). A command that needs one names it in
/// <see cref="CapabilityDescriptor.Requires"/>; the host looks for them when it starts and when the settings change, and
/// the wheel offers such a command only when its program was found.
/// </summary>
public static class ExternalPrograms
{
    /// <summary>LibreOffice (MPL-2.0), for Office documents to PDF and a PDF's import.</summary>
    public const string LibreOffice = "libreoffice";

    /// <summary>The user's Microsoft Word, Excel and PowerPoint, run through their own automation (ADR-0033 addendum).</summary>
    public const string Word = "msword", Excel = "msexcel", PowerPoint = "mspowerpoint";

    /// <summary>Microsoft Edge, part of Windows: prints a web page to PDF, headless, with scripts and the network off.</summary>
    public const string Edge = "msedge";

    /// <summary>To PDF: LibreOffice or any Microsoft Office program.</summary>
    public const string AnyOffice = "office-any";

    /// <summary>PDF to Word: LibreOffice or Microsoft Word.</summary>
    public const string PdfImporter = "pdf-importer";

    /// <summary>The set the resolver takes, from what was found: the programs, and what they make possible together.</summary>
    public static IReadOnlySet<string> Set(string? libreOffice, IEnumerable<string>? microsoft = null)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        if (libreOffice is not null) set.Add(LibreOffice);
        foreach (var app in microsoft ?? []) if (app is Word or Excel or PowerPoint or Edge) set.Add(app);
        if (set.Contains(LibreOffice) || set.Contains(Word) || set.Contains(Excel) || set.Contains(PowerPoint)) set.Add(AnyOffice);
        if (set.Contains(LibreOffice) || set.Contains(Word)) set.Add(PdfImporter);
        return set;
    }
}

/// <summary>
/// Which Microsoft Office programs the user has: each one's App Paths entry (the current user's, then the machine's; 64-
/// then 32-bit view), counted only as a fully qualified local path to that program that exists. A few reads, no search.
/// </summary>
public static class MicrosoftOfficeLocator
{
    public static IReadOnlyDictionary<string, string> Executables { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [ExternalPrograms.Word] = "winword.exe",
        [ExternalPrograms.Excel] = "excel.exe",
        [ExternalPrograms.PowerPoint] = "powerpnt.exe",
        [ExternalPrograms.Edge] = "msedge.exe",
    };

    public static string AppPathKey(string executable) => @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\" + executable;

    public static IReadOnlySet<string> Find(Func<RegistryRoot, bool, string, string?> readDefault, Func<string, bool> exists)
    {
        var found = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (app, exe) in Executables)
            foreach (var root in new[] { RegistryRoot.CurrentUser, RegistryRoot.LocalMachine })
                foreach (var wow32 in new[] { false, true })
                    if (readDefault(root, wow32, AppPathKey(exe)) is { } value && LibreOfficeLocator.Unquoted(value) is { } path
                        && LibreOfficeLocator.IsLocalProgram(path, exe) && exists(path))
                        found.Add(app);
        return found;
    }
}

/// <summary>A registry root the locator reads (the reading itself is Windows code outside Core).</summary>
public enum RegistryRoot { CurrentUser, LocalMachine }

/// <summary>
/// Where the user's LibreOffice is (ADR-0033): its <c>soffice.exe</c>, from the places its installer writes, in this order:
/// <list type="number">
/// <item>App Paths (the current user's, then the machine's; 64- then 32-bit view): the key Windows itself uses to find
/// "soffice.exe";</item>
/// <item>LibreOffice's own <c>SOFTWARE\LibreOffice\UNO\InstallPath</c> (its "program" folder);</item>
/// <item>the default folders, <c>%ProgramFiles%\LibreOffice\program</c> and the 32-bit one.</item>
/// </list>
/// A candidate counts only as a fully qualified local path named <c>soffice.exe</c> with <c>soffice.bin</c> beside it (the
/// real program the launcher starts): a value pointing at anything else, or at a network share, is ignored. Nothing is
/// searched beyond these few reads.
/// </summary>
public static class LibreOfficeLocator
{
    public const string AppPathKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\soffice.exe";
    public const string UnoInstallPathKey = @"SOFTWARE\LibreOffice\UNO\InstallPath";

    /// <param name="readDefault">A key's default value (root, 32-bit view, key), or null.</param>
    /// <param name="exists">Whether a file exists.</param>
    /// <param name="programFolders">The Program Files folders.</param>
    public static string? Find(Func<RegistryRoot, bool, string, string?> readDefault, Func<string, bool> exists, IEnumerable<string?> programFolders)
    {
        IEnumerable<string?> Candidates()
        {
            foreach (var root in new[] { RegistryRoot.CurrentUser, RegistryRoot.LocalMachine })
                foreach (var wow32 in new[] { false, true })
                    yield return readDefault(root, wow32, AppPathKey);
            foreach (var wow32 in new[] { false, true })
                if (readDefault(RegistryRoot.LocalMachine, wow32, UnoInstallPathKey) is { } program && Unquoted(program) is { } folder)
                    yield return SafeCombine(folder, "soffice.exe");
            foreach (var folder in programFolders)
                if (!string.IsNullOrEmpty(folder)) yield return SafeCombine(folder, @"LibreOffice\program\soffice.exe");
        }
        foreach (var candidate in Candidates())
            if (candidate is not null && Unquoted(candidate) is { } path && IsAcceptable(path) && exists(path)
                && exists(Path.Combine(Path.GetDirectoryName(path)!, "soffice.bin")))
                return path;
        return null;
    }

    /// <summary>A fully qualified local path (a drive letter, no share, no device path, no "..") named soffice.exe.</summary>
    public static bool IsAcceptable(string path) => IsLocalProgram(path, "soffice.exe");

    /// <summary>A fully qualified local path (a drive letter, no share, no device path, no "..") named <paramref name="executable"/>.</summary>
    public static bool IsLocalProgram(string path, string executable) =>
        path.Length is > 3 and < 260 && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] == '\\'
        && path.IndexOfAny([.. Path.GetInvalidPathChars(), '*', '?', '"', '/']) < 0 && !path.Contains(@"\..\", StringComparison.Ordinal)
        && !path.Contains(@"\.\", StringComparison.Ordinal) && string.Equals(Path.GetFileName(path), executable, StringComparison.OrdinalIgnoreCase);

    internal static string? Unquoted(string value)
    {
        var v = value.Trim();
        if (v.Length >= 2 && v[0] == '"' && v[^1] == '"') v = v[1..^1].Trim();
        return v.Length == 0 ? null : v;
    }

    private static string? SafeCombine(string folder, string relative)
    {
        try { return Path.Combine(folder, relative); }
        catch (ArgumentException) { return null; }
    }
}
