using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using ShareCom.App.Infra;
using ShareCom.Core.Files;
using ComIDataObject = System.Runtime.InteropServices.ComTypes.IDataObject;
using FILETIME = System.Runtime.InteropServices.ComTypes.FILETIME;
using STATSTG = System.Runtime.InteropServices.ComTypes.STATSTG;

namespace ShareCom.App.Clipboard;

/// <summary>
/// 탐색기에 넘기는 '가상 파일' 클립보드 객체. 목록(FileGroupDescriptorW)만 먼저 주고,
/// 탐색기가 붙여넣을 때 파일마다 내용(FileContents, lindex = 순번)을 스트림으로 요청하면 그때 원본 PC에서 받아온다.
/// </summary>
[ComVisible(true)]
[ClassInterface(ClassInterfaceType.None)]
internal sealed class VirtualFileDataObject : ComIDataObject
{
    private const int DV_E_FORMATETC = unchecked((int)0x80040064);
    private const int DV_E_LINDEX = unchecked((int)0x80040068);
    private const int OLE_E_ADVISENOTSUPPORTED = unchecked((int)0x80040003);
    private const int E_NOTIMPL = unchecked((int)0x80004001);
    private const int S_OK = 0, S_FALSE = 1;
    private const int DATA_S_SAMEFORMATETC = 0x00040130;

    private const uint FD_ATTRIBUTES = 0x4, FD_WRITESTIME = 0x20, FD_FILESIZE = 0x40, FD_PROGRESSUI = 0x4000, FD_UNICODE = 0x80000000;
    private const uint FILE_ATTRIBUTE_DIRECTORY = 0x10, FILE_ATTRIBUTE_NORMAL = 0x80;
    private const int DescriptorSize = 592;

    public static readonly short CfFileDescriptorW = Format("FileGroupDescriptorW");
    public static readonly short CfFileContents = Format("FileContents");
    public static readonly short CfPreferredDropEffect = Format("Preferred DropEffect");

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterClipboardFormat(string name);

    [DllImport("ole32.dll")]
    private static extern void ReleaseStgMedium(ref STGMEDIUM medium);

    [DllImport("shell32.dll")]
    private static extern int SHCreateStdEnumFmtEtc(uint cfmt, [MarshalAs(UnmanagedType.LPArray)] FORMATETC[] afmt, out IEnumFORMATETC ppenumFormatEtc);

    private static short Format(string name) => unchecked((short)RegisterClipboardFormat(name));

    private readonly FileOffer _offer;
    private readonly Func<int, Stream> _open;
    private readonly string _sourceName;

    /// <param name="open">순번으로 파일 내용 스트림을 연다(원본 PC에서 받아오는 스트림).</param>
    public VirtualFileDataObject(FileOffer offer, Func<int, Stream> open, string sourceName)
    {
        _offer = offer;
        _open = open;
        _sourceName = sourceName;
    }

    public FileOffer Offer => _offer;

    private static FORMATETC Fmt(short cf, TYMED tymed, int lindex = -1) => new()
    {
        cfFormat = cf, dwAspect = DVASPECT.DVASPECT_CONTENT, lindex = lindex, ptd = IntPtr.Zero, tymed = tymed,
    };

    private static FORMATETC[] Formats =>
    [
        Fmt(CfFileDescriptorW, TYMED.TYMED_HGLOBAL),
        Fmt(CfFileContents, TYMED.TYMED_ISTREAM),
        Fmt(CfPreferredDropEffect, TYMED.TYMED_HGLOBAL),
    ];

    public void GetData(ref FORMATETC format, out STGMEDIUM medium)
    {
        medium = default;
        if (format.cfFormat == CfFileDescriptorW && (format.tymed & TYMED.TYMED_HGLOBAL) != 0)
        {
            medium = HGlobal(BuildDescriptor());
            return;
        }
        if (format.cfFormat == CfPreferredDropEffect && (format.tymed & TYMED.TYMED_HGLOBAL) != 0)
        {
            medium = HGlobal(BitConverter.GetBytes(1)); // DROPEFFECT_COPY
            return;
        }
        if (format.cfFormat == CfFileContents && (format.tymed & TYMED.TYMED_ISTREAM) != 0)
        {
            int index = format.lindex;
            if (index < 0 || index >= _offer.Entries.Count || _offer.Entries[index].IsDirectory)
                throw new COMException("잘못된 파일 순번", DV_E_LINDEX);
            var entry = _offer.Entries[index];
            Log.Info($"붙여넣기: '{_sourceName}' 에서 {entry.RelativePath} 받는 중 ({entry.Size:N0}바이트)");
            var stream = new ComStream(_open(index), entry.Size, Path.GetFileName(entry.RelativePath));
            medium = new STGMEDIUM
            {
                tymed = TYMED.TYMED_ISTREAM,
                unionmember = Marshal.GetComInterfaceForObject(stream, typeof(IStream)),
                pUnkForRelease = null!,
            };
            return;
        }
        throw new COMException("지원하지 않는 형식", DV_E_FORMATETC);
    }

    public void GetDataHere(ref FORMATETC format, ref STGMEDIUM medium) => throw new COMException("", E_NOTIMPL);

    public int QueryGetData(ref FORMATETC format)
    {
        if (format.cfFormat == CfFileDescriptorW || format.cfFormat == CfPreferredDropEffect)
            return (format.tymed & TYMED.TYMED_HGLOBAL) != 0 ? S_OK : DV_E_FORMATETC;
        if (format.cfFormat == CfFileContents)
            return (format.tymed & TYMED.TYMED_ISTREAM) != 0 ? S_OK : DV_E_FORMATETC;
        return DV_E_FORMATETC;
    }

    public int GetCanonicalFormatEtc(ref FORMATETC formatIn, out FORMATETC formatOut)
    {
        formatOut = formatIn;
        formatOut.ptd = IntPtr.Zero;
        return DATA_S_SAMEFORMATETC;
    }

    public void SetData(ref FORMATETC formatIn, ref STGMEDIUM medium, bool release)
    {
        // 탐색기는 붙여넣은 뒤 "Performed DropEffect" 등을 알려 온다. 받아 두기만 한다.
        if (release) ReleaseStgMedium(ref medium);
    }

    public IEnumFORMATETC EnumFormatEtc(DATADIR direction)
    {
        if (direction != DATADIR.DATADIR_GET) throw new COMException("", E_NOTIMPL);
        var formats = Formats;
        int hr = SHCreateStdEnumFmtEtc((uint)formats.Length, formats, out var e);
        if (hr != 0) Marshal.ThrowExceptionForHR(hr);
        return e;
    }

    public int DAdvise(ref FORMATETC pFormatetc, ADVF advf, IAdviseSink adviseSink, out int connection)
    {
        connection = 0;
        return OLE_E_ADVISENOTSUPPORTED;
    }

    public void DUnadvise(int connection) => throw new COMException("", OLE_E_ADVISENOTSUPPORTED);

    public int EnumDAdvise(out IEnumSTATDATA enumAdvise)
    {
        enumAdvise = null!;
        return OLE_E_ADVISENOTSUPPORTED;
    }

    /// <summary>FILEGROUPDESCRIPTORW: 항목 수(4바이트) + FILEDESCRIPTORW(592바이트) × 항목 수.</summary>
    private byte[] BuildDescriptor()
    {
        using var ms = new MemoryStream(4 + DescriptorSize * _offer.Entries.Count);
        using var w = new BinaryWriter(ms);
        w.Write((uint)_offer.Entries.Count);
        foreach (var e in _offer.Entries)
        {
            w.Write(FD_ATTRIBUTES | FD_FILESIZE | FD_WRITESTIME | FD_PROGRESSUI | FD_UNICODE);
            w.Write(new byte[16]); // clsid
            w.Write(new byte[8]);  // sizel
            w.Write(new byte[8]);  // pointl
            w.Write(e.IsDirectory ? FILE_ATTRIBUTE_DIRECTORY : FILE_ATTRIBUTE_NORMAL);
            w.Write(0L); // ftCreationTime
            w.Write(0L); // ftLastAccessTime
            long ft = 0;
            if (e.LastWriteUtcTicks > 0)
                try { ft = new DateTime(e.LastWriteUtcTicks, DateTimeKind.Utc).ToFileTimeUtc(); } catch { }
            w.Write(ft); // ftLastWriteTime
            w.Write((uint)(e.Size >> 32));
            w.Write((uint)(e.Size & 0xFFFFFFFF));
            var name = new char[260];
            var path = e.RelativePath.Length > 259 ? e.RelativePath[..259] : e.RelativePath;
            path.CopyTo(0, name, 0, path.Length);
            foreach (var c in name) w.Write((ushort)c);
        }
        w.Flush();
        return ms.ToArray();
    }

    private static STGMEDIUM HGlobal(byte[] data)
    {
        var h = Marshal.AllocHGlobal(data.Length);
        Marshal.Copy(data, 0, h, data.Length);
        return new STGMEDIUM { tymed = TYMED.TYMED_HGLOBAL, unionmember = h, pUnkForRelease = null! };
    }
}

/// <summary>
/// .NET 스트림(앞에서부터만 읽기)을 COM IStream 으로 감싼다. 탐색기가 크기를 알려고
/// 끝으로 갔다가 처음으로 돌아오는 것처럼, 실제로 읽지 않은 자리로 옮기는 것은 받아 준다.
/// </summary>
[ComVisible(true)]
[ClassInterface(ClassInterfaceType.None)]
internal sealed class ComStream : IStream
{
    private const int STG_E_INVALIDFUNCTION = unchecked((int)0x80030001);
    private const int STG_E_ACCESSDENIED = unchecked((int)0x80030005);
    private const int STG_E_READFAULT = unchecked((int)0x8003001E);
    private const int E_NOTIMPL = unchecked((int)0x80004001);

    private readonly Stream _inner;
    private readonly long _size;
    private readonly string _name;
    private long _actual;   // 실제로 읽은 위치
    private long _position; // 탐색기가 보는 위치
    private bool _closed, _failed;

    public ComStream(Stream inner, long size, string name)
    {
        _inner = inner;
        _size = size;
        _name = name;
    }

    private void Close()
    {
        _closed = true;
        _inner.Dispose();
    }

    public void Read(byte[] pv, int cb, IntPtr pcbRead)
    {
        int total = 0;
        if (_failed) throw new COMException("이전 읽기가 실패했습니다.", STG_E_READFAULT);
        if (_closed)
        {
            // 다 읽은 뒤에도 탐색기는 끝을 확인하려고 한 번 더 읽는다. 0바이트로 답한다.
            if (_position < _actual) throw new COMException("되감기는 지원하지 않습니다.", STG_E_INVALIDFUNCTION);
            if (pcbRead != IntPtr.Zero) Marshal.WriteInt32(pcbRead, 0);
            return;
        }
        try
        {
            if (_position < _actual) throw new COMException("되감기는 지원하지 않습니다.", STG_E_INVALIDFUNCTION);
            // 앞으로 건너뛴 자리까지는 읽고 버린다.
            var skip = new byte[64 * 1024];
            while (_actual < _position)
            {
                int n = _inner.Read(skip, 0, (int)Math.Min(skip.Length, _position - _actual));
                if (n == 0) break;
                _actual += n;
            }
            while (total < cb)
            {
                int n = _inner.Read(pv, total, cb - total);
                if (n == 0) break;
                total += n;
            }
            _actual += total;
            _position = _actual;
            if (total == 0 || _actual >= _size) Close(); // 다 읽었으면 바로 정리
        }
        catch (COMException) { throw; }
        catch (Exception ex)
        {
            Log.Error($"붙여넣기 중 '{_name}' 받기 실패", ex);
            _failed = true;
            Close();
            throw new COMException(ex.Message, STG_E_READFAULT);
        }
        if (pcbRead != IntPtr.Zero) Marshal.WriteInt32(pcbRead, total);
    }

    public void Write(byte[] pv, int cb, IntPtr pcbWritten) => throw new COMException("", STG_E_ACCESSDENIED);

    public void Seek(long dlibMove, int dwOrigin, IntPtr plibNewPosition)
    {
        long target = dwOrigin switch
        {
            0 => dlibMove,             // STREAM_SEEK_SET
            1 => _position + dlibMove, // STREAM_SEEK_CUR
            2 => _size + dlibMove,     // STREAM_SEEK_END
            _ => throw new COMException("", STG_E_INVALIDFUNCTION),
        };
        if (target < 0) throw new COMException("", STG_E_INVALIDFUNCTION);
        _position = target;
        if (plibNewPosition != IntPtr.Zero) Marshal.WriteInt64(plibNewPosition, _position);
    }

    public void SetSize(long libNewSize) => throw new COMException("", E_NOTIMPL);

    public void CopyTo(IStream pstm, long cb, IntPtr pcbRead, IntPtr pcbWritten)
    {
        var buffer = new byte[64 * 1024];
        long done = 0;
        var readPtr = Marshal.AllocHGlobal(4);
        try
        {
            while (done < cb)
            {
                int want = (int)Math.Min(buffer.Length, cb - done);
                Read(buffer, want, readPtr);
                int n = Marshal.ReadInt32(readPtr);
                if (n == 0) break;
                pstm.Write(buffer, n, IntPtr.Zero);
                done += n;
            }
        }
        finally { Marshal.FreeHGlobal(readPtr); }
        if (pcbRead != IntPtr.Zero) Marshal.WriteInt64(pcbRead, done);
        if (pcbWritten != IntPtr.Zero) Marshal.WriteInt64(pcbWritten, done);
    }

    public void Commit(int grfCommitFlags) { }
    public void Revert() => throw new COMException("", E_NOTIMPL);
    public void LockRegion(long libOffset, long cb, int dwLockType) => throw new COMException("", E_NOTIMPL);
    public void UnlockRegion(long libOffset, long cb, int dwLockType) => throw new COMException("", E_NOTIMPL);
    public void Clone(out IStream ppstm) => throw new COMException("", E_NOTIMPL);

    public void Stat(out STATSTG pstatstg, int grfStatFlag)
    {
        pstatstg = new STATSTG
        {
            type = 2, // STGTY_STREAM
            cbSize = _size,
            pwcsName = (grfStatFlag & 1) != 0 ? null! : _name, // STATFLAG_NONAME
            grfMode = 0,
            mtime = default(FILETIME),
        };
    }
}

/// <summary>OLE 클립보드에 가상 파일 객체를 올리고 내린다. UI(STA) 스레드에서만 부른다.</summary>
internal static class OleClipboard
{
    [DllImport("ole32.dll")]
    private static extern int OleSetClipboard(ComIDataObject? pDataObj);

    [DllImport("ole32.dll")]
    private static extern int OleIsCurrentClipboard(ComIDataObject pDataObj);

    private static VirtualFileDataObject? _current;

    public static void Set(VirtualFileDataObject obj)
    {
        for (int attempt = 0; attempt < 10; attempt++)
        {
            int hr = OleSetClipboard(obj);
            if (hr == 0)
            {
                _current = obj; // 클립보드에 있는 동안 살려 둔다
                return;
            }
            Thread.Sleep(100); // 다른 프로그램이 클립보드를 잡고 있음
        }
        Log.Error("가상 파일을 클립보드에 올리지 못했습니다.");
    }

    /// <summary>프로그램이 끝날 때: 내 가상 파일이 아직 클립보드에 있으면 내린다(끝난 뒤 붙여넣기 실패를 막는다).</summary>
    public static void ReleaseIfOwned()
    {
        if (_current is not null && OleIsCurrentClipboard(_current) == 0) OleSetClipboard(null);
        _current = null;
    }
}
