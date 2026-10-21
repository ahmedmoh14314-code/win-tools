using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace WinTools.Worker.Imaging;

/// <summary>
/// The encoders' output stream: a COM IStream over a MemoryStream, whose capacity grows by doubling. An HGLOBAL stream
/// grows by exactly the bytes written, so an encoder that makes many small writes (TIFF) copies the buffer over and over:
/// 25 MP to TIFF took 132 s with it and is linear with this one.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001", Justification = "A MemoryStream holds no unmanaged resources; the COM wrapper owns this object.")]
internal sealed class GrowableStream : IStream
{
    private readonly MemoryStream _data = new();

    public byte[] ToArray() => _data.ToArray();

    public void Read(byte[] pv, int cb, nint pcbRead)
    {
        var n = _data.Read(pv, 0, cb);
        if (pcbRead != 0) Marshal.WriteInt32(pcbRead, n);
    }

    public void Write(byte[] pv, int cb, nint pcbWritten)
    {
        _data.Write(pv, 0, cb);
        if (pcbWritten != 0) Marshal.WriteInt32(pcbWritten, cb);
    }

    public void Seek(long dlibMove, int dwOrigin, nint plibNewPosition)
    {
        var pos = _data.Seek(dlibMove, (SeekOrigin)dwOrigin); // STREAM_SEEK_SET/CUR/END match SeekOrigin
        if (plibNewPosition != 0) Marshal.WriteInt64(plibNewPosition, pos);
    }

    public void SetSize(long libNewSize) => _data.SetLength(libNewSize);

    public void Stat(out STATSTG pstatstg, int grfStatFlag) => pstatstg = new STATSTG { type = 2 /* STGTY_STREAM */, cbSize = _data.Length };

    public void Commit(int grfCommitFlags) { }
    public void Revert() { }
    public void CopyTo(IStream pstm, long cb, nint pcbRead, nint pcbWritten) => throw new NotSupportedException();
    public void LockRegion(long libOffset, long cb, int dwLockType) => throw new NotSupportedException();
    public void UnlockRegion(long libOffset, long cb, int dwLockType) => throw new NotSupportedException();
    public void Clone(out IStream ppstm) => throw new NotSupportedException();
}
