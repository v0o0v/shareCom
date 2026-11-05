using System.Security.Cryptography;
using System.Text;
using ShareCom.Core.Protocol;

namespace ShareCom.Core.Clipboard;

/// <summary>PC끼리 주고받는 클립보드 형식. Windows 형식 번호가 아니라 이름으로 주고받는다.</summary>
public static class ClipboardFormats
{
    /// <summary>UTF-8 글자.</summary>
    public const string Text = "text";
    /// <summary>CF_HTML 원문(UTF-8, Version: 머리말 포함).</summary>
    public const string Html = "html";
    /// <summary>RTF 원문.</summary>
    public const string Rtf = "rtf";
    /// <summary>PNG 이미지.</summary>
    public const string Png = "png";
    /// <summary>파일 제안(FileOffer). 내용은 붙여넣을 때 받는다.</summary>
    public const string Files = "files";
}

public sealed record ClipboardItem(string Format, byte[] Data);

/// <summary>클립보드 내용 하나(형식 여러 개). 같은 내용인지는 해시로 비교한다.</summary>
public sealed class ClipboardContent
{
    public IReadOnlyList<ClipboardItem> Items { get; }
    public string Hash { get; }

    public ClipboardContent(IEnumerable<ClipboardItem> items)
    {
        Items = items.OrderBy(i => i.Format, StringComparer.Ordinal).ToList();
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var i in Items)
        {
            sha.AppendData(Encoding.UTF8.GetBytes(i.Format + "\0"));
            sha.AppendData(BitConverter.GetBytes(i.Data.LongLength));
            sha.AppendData(i.Data);
        }
        Hash = Convert.ToHexString(sha.GetHashAndReset());
    }

    public bool IsEmpty => Items.Count == 0;
    public long TotalBytes => Items.Sum(i => i.Data.LongLength);
    public ClipboardItem? Get(string format) => Items.FirstOrDefault(i => i.Format == format);

    public byte[] Encode()
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms, Encoding.UTF8);
        w.Write((byte)1); // 형식 버전
        w.Write(Items.Count);
        foreach (var i in Items)
        {
            w.Write(i.Format);
            w.Write(i.Data.Length);
            w.Write(i.Data);
        }
        w.Flush();
        return ms.ToArray();
    }

    public static ClipboardContent Decode(byte[] data)
    {
        using var r = new BinaryReader(new MemoryStream(data), Encoding.UTF8);
        if (r.ReadByte() != 1) throw new InvalidDataException("알 수 없는 클립보드 형식 버전");
        int n = r.ReadInt32();
        if (n < 0 || n > 32) throw new InvalidDataException("클립보드 형식 수가 잘못됨");
        var items = new List<ClipboardItem>(n);
        for (int k = 0; k < n; k++)
        {
            var format = r.ReadString();
            int len = r.ReadInt32();
            if (len < 0) throw new InvalidDataException("클립보드 길이가 잘못됨");
            items.Add(new ClipboardItem(format, r.ReadBytes(len)));
        }
        return new ClipboardContent(items);
    }
}

/// <summary>ClipboardSync 가 바깥에 시키는 일.</summary>
public interface IClipboardTransport
{
    IReadOnlyList<Guid> ConnectedPeers { get; }
    void SendToPeer(Guid peer, byte[] payload);
    /// <summary>받은 내용을 이 PC 클립보드에 쓴다. 파일 제안이면 붙여넣을 때 fromPeer 에서 내용을 받는다.</summary>
    void WriteLocal(Guid fromPeer, ClipboardContent content);
}

/// <summary>
/// 클립보드 자동 공유. 이 PC에서 복사하면 연결된 모든 PC로 보내고, 받으면 이 PC 클립보드에 쓴다.
/// 받은 내용을 쓰면 다시 '바뀜' 알림이 오므로, 마지막으로 주고받은 내용과 같으면 보내지 않는다.
/// 한 사람이 쓰는 PC들이라 동시에 복사하는 일은 드물어 마지막에 받은 것이 이긴다.
/// </summary>
public sealed class ClipboardSync
{
    private readonly IClipboardTransport _transport;
    private readonly object _gate = new();
    private string? _currentHash;

    public ClipboardSync(IClipboardTransport transport) => _transport = transport;

    public bool Enabled { get; set; } = true;

    /// <summary>보낸 횟수(진단·시험용).</summary>
    public int SentCount { get; private set; }

    /// <summary>이 PC 클립보드가 바뀌었다(내 프로세스가 쓴 것은 App 이 미리 걸러 낸다).</summary>
    public void OnLocalChanged(ClipboardContent content)
    {
        if (!Enabled || content.IsEmpty) return;
        lock (_gate)
        {
            if (content.Hash == _currentHash) return;
            _currentHash = content.Hash;
        }
        var payload = content.Encode();
        foreach (var peer in _transport.ConnectedPeers) _transport.SendToPeer(peer, payload);
        SentCount++;
    }

    /// <summary>다른 PC에서 클립보드 내용이 왔다.</summary>
    public void OnRemote(Guid peer, byte[] payload)
    {
        if (!Enabled) return;
        var content = ClipboardContent.Decode(payload);
        if (content.IsEmpty) return;
        lock (_gate)
        {
            if (content.Hash == _currentHash) return;
            _currentHash = content.Hash;
        }
        _transport.WriteLocal(peer, content);
    }
}
