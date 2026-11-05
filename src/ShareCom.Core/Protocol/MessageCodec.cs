using System.Buffers.Binary;
using System.Text;

namespace ShareCom.Core.Protocol;

/// <summary>
/// 메시지 틀: [u32 LE 길이][u8 종류][본문]. 길이는 종류 바이트부터 센다.
/// </summary>
public static class MessageCodec
{
    public const int ProtocolVersion = 2;
    public const int MaxFrameBytes = 16 * 1024 * 1024;

    public static byte[] Encode(Message message)
    {
        using var ms = new MemoryStream();
        ms.Write(stackalloc byte[4]); // 길이 자리
        ms.WriteByte((byte)message.Type);
        using (var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
        {
            WriteBody(w, message);
        }
        var bytes = ms.ToArray();
        BinaryPrimitives.WriteInt32LittleEndian(bytes, bytes.Length - 4);
        return bytes;
    }

    public static Message Decode(ReadOnlySpan<byte> frame)
    {
        if (frame.Length < 1) throw new InvalidDataException("빈 메시지");
        var type = (MessageType)frame[0];
        using var ms = new MemoryStream(frame[1..].ToArray());
        using var r = new BinaryReader(ms, Encoding.UTF8);
        return ReadBody(r, type);
    }

    public static async Task WriteAsync(Stream stream, Message message, CancellationToken ct)
    {
        var bytes = Encode(message);
        await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    /// <summary>메시지 하나를 읽는다. 상대가 연결을 닫았으면 null.</summary>
    public static async Task<Message?> ReadAsync(Stream stream, CancellationToken ct)
    {
        var header = new byte[4];
        if (!await ReadExactAsync(stream, header, ct).ConfigureAwait(false)) return null;
        int length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length < 1 || length > MaxFrameBytes) throw new InvalidDataException($"메시지 길이가 잘못됨: {length}");
        var body = new byte[length];
        if (!await ReadExactAsync(stream, body, ct).ConfigureAwait(false))
            throw new EndOfStreamException("메시지 중간에 연결이 끊김");
        return Decode(body);
    }

    private static async Task<bool> ReadExactAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        int read = 0;
        while (read < buffer.Length)
        {
            int n = await stream.ReadAsync(buffer.AsMemory(read), ct).ConfigureAwait(false);
            if (n == 0)
            {
                if (read == 0) return false;
                throw new EndOfStreamException("메시지 중간에 연결이 끊김");
            }
            read += n;
        }
        return true;
    }

    private static void WriteBody(BinaryWriter w, Message m)
    {
        switch (m)
        {
            case AuthHelloMessage x:
                w.Write(x.Id.ToByteArray()); w.Write(x.Name); w.Write(x.ProtocolVersion); w.Write(x.ListenPort); WriteBytes(w, x.Nonce); break;
            case AuthProofMessage x: WriteBytes(w, x.Signature); break;
            case PairDecisionMessage x: w.Write(x.Accepted); break;
            case PingMessage x: w.Write(x.Ticks); break;
            case PongMessage x: w.Write(x.Ticks); break;
            case LayoutMessage x: WriteLayout(w, x.Doc); break;
            case TrustMessage x:
                w.Write(x.Entries.Length);
                foreach (var e in x.Entries)
                {
                    w.Write(e.Id.ToByteArray()); w.Write(e.Name); WriteBytes(w, e.Certificate);
                    w.Write(e.Address ?? ""); w.Write(e.Port); w.Write(e.AtUtcTicks); w.Write(e.Trusted);
                }
                break;
            case EnterControlMessage x: w.Write(x.X); w.Write(x.Y); break;
            case LeaveControlMessage: break;
            case MouseMoveMessage x: w.Write(x.X); w.Write(x.Y); break;
            case MouseButtonMessage x: w.Write((byte)x.Button); w.Write(x.Down); break;
            case MouseWheelMessage x: w.Write(x.Delta); w.Write(x.Horizontal); break;
            case KeyMessage x: w.Write(x.VirtualKey); w.Write(x.ScanCode); w.Write(x.Extended); w.Write(x.Down); break;
            case TakeOverMessage: break;
            case FileOpenMessage x: w.Write(x.RequestId); w.Write(x.OfferId.ToByteArray()); w.Write(x.Index); w.Write(x.Offset); break;
            case FileChunkMessage x: w.Write(x.RequestId); WriteBytes(w, x.Data); w.Write(x.Last); break;
            case FileAckMessage x: w.Write(x.RequestId); w.Write(x.Consumed); break;
            case FileErrorMessage x: w.Write(x.RequestId); w.Write(x.Error); break;
            case FileCancelMessage x: w.Write(x.RequestId); break;
            case FileSendResponseMessage x: w.Write(x.OfferId.ToByteArray()); w.Write(x.Accepted); break;
            case FileSendResultMessage x: w.Write(x.OfferId.ToByteArray()); w.Write(x.Success); w.Write(x.Error); break;
            case BlobChunkMessage x:
                w.Write(x.BlobId); w.Write((byte)x.Kind); w.Write(x.TotalLength); w.Write(x.Offset); WriteBytes(w, x.Data); break;
            default: throw new NotSupportedException($"알 수 없는 메시지: {m.GetType().Name}");
        }
    }

    private static Message ReadBody(BinaryReader r, MessageType type) => type switch
    {
        MessageType.AuthHello => new AuthHelloMessage(new Guid(r.ReadBytes(16)), r.ReadString(), r.ReadInt32(), r.ReadInt32(), ReadBytes(r)),
        MessageType.AuthProof => new AuthProofMessage(ReadBytes(r)),
        MessageType.PairDecision => new PairDecisionMessage(r.ReadBoolean()),
        MessageType.Ping => new PingMessage(r.ReadInt64()),
        MessageType.Pong => new PongMessage(r.ReadInt64()),
        MessageType.Layout => new LayoutMessage(ReadLayout(r)),
        MessageType.Trust => ReadTrust(r),
        MessageType.EnterControl => new EnterControlMessage(r.ReadInt32(), r.ReadInt32()),
        MessageType.LeaveControl => new LeaveControlMessage(),
        MessageType.MouseMove => new MouseMoveMessage(r.ReadInt32(), r.ReadInt32()),
        MessageType.MouseButton => new MouseButtonMessage((MouseButtonKind)r.ReadByte(), r.ReadBoolean()),
        MessageType.MouseWheel => new MouseWheelMessage(r.ReadInt32(), r.ReadBoolean()),
        MessageType.Key => new KeyMessage(r.ReadUInt16(), r.ReadUInt16(), r.ReadBoolean(), r.ReadBoolean()),
        MessageType.TakeOver => new TakeOverMessage(),
        MessageType.FileOpen => new FileOpenMessage(r.ReadUInt32(), new Guid(r.ReadBytes(16)), r.ReadInt32(), r.ReadInt64()),
        MessageType.FileChunk => new FileChunkMessage(r.ReadUInt32(), ReadBytes(r), r.ReadBoolean()),
        MessageType.FileAck => new FileAckMessage(r.ReadUInt32(), r.ReadInt64()),
        MessageType.FileError => new FileErrorMessage(r.ReadUInt32(), r.ReadString()),
        MessageType.FileCancel => new FileCancelMessage(r.ReadUInt32()),
        MessageType.FileSendResponse => new FileSendResponseMessage(new Guid(r.ReadBytes(16)), r.ReadBoolean()),
        MessageType.FileSendResult => new FileSendResultMessage(new Guid(r.ReadBytes(16)), r.ReadBoolean(), r.ReadString()),
        MessageType.BlobChunk => new BlobChunkMessage(r.ReadUInt32(), (BlobKind)r.ReadByte(), r.ReadInt64(), r.ReadInt64(), ReadBytes(r)),
        _ => throw new InvalidDataException($"알 수 없는 메시지 종류: {(byte)type}"),
    };

    public const int MaxLayoutEntries = 16, MaxMonitors = 16, MaxNameLength = 256, MaxCertificateBytes = 16 * 1024;

    private static void WriteLayout(BinaryWriter w, LayoutDoc d)
    {
        w.Write(d.Version); w.Write(d.Editor.ToByteArray()); w.Write(d.Entries.Length);
        foreach (var e in d.Entries)
        {
            w.Write(e.Id.ToByteArray()); w.Write(e.Name); w.Write(e.X); w.Write(e.Y);
            w.Write(e.Screen.ScalePercent); w.Write(e.Screen.Monitors.Length);
            foreach (var s in e.Screen.Monitors) { w.Write(s.X); w.Write(s.Y); w.Write(s.Width); w.Write(s.Height); }
        }
    }

    private static LayoutDoc ReadLayout(BinaryReader r)
    {
        long version = r.ReadInt64();
        var editor = new Guid(r.ReadBytes(16));
        int n = r.ReadInt32();
        if (n < 0 || n > MaxLayoutEntries) throw new InvalidDataException("배치 항목 수가 잘못됨");
        var entries = new LayoutEntry[n];
        for (int i = 0; i < n; i++)
        {
            var id = new Guid(r.ReadBytes(16));
            var name = ReadName(r);
            int x = r.ReadInt32(), y = r.ReadInt32(), scale = r.ReadInt32();
            if (scale < 50 || scale > 500) throw new InvalidDataException("배율이 잘못됨");
            int m = r.ReadInt32();
            if (m < 1 || m > MaxMonitors) throw new InvalidDataException("모니터 수가 잘못됨");
            var monitors = new ScreenRect[m];
            for (int k = 0; k < m; k++)
            {
                monitors[k] = new ScreenRect(r.ReadInt32(), r.ReadInt32(), r.ReadInt32(), r.ReadInt32());
                if (monitors[k].Width <= 0 || monitors[k].Height <= 0 || monitors[k].Width > 100_000 || monitors[k].Height > 100_000)
                    throw new InvalidDataException("모니터 크기가 잘못됨");
            }
            entries[i] = new LayoutEntry(id, name, x, y, new PcScreen(monitors, scale));
        }
        return new LayoutDoc(version, editor, entries);
    }

    private static TrustMessage ReadTrust(BinaryReader r)
    {
        int n = r.ReadInt32();
        if (n < 0 || n > MaxLayoutEntries * 4) throw new InvalidDataException("신뢰 목록 수가 잘못됨");
        var list = new TrustEntry[n];
        for (int i = 0; i < n; i++)
        {
            var id = new Guid(r.ReadBytes(16));
            var name = ReadName(r);
            var cert = ReadBytes(r);
            if (cert.Length > MaxCertificateBytes) throw new InvalidDataException("인증서가 너무 큼");
            var address = r.ReadString();
            list[i] = new TrustEntry(id, name, cert, address.Length == 0 ? null : address, r.ReadInt32(), r.ReadInt64(), r.ReadBoolean());
        }
        return new TrustMessage(list);
    }

    private static string ReadName(BinaryReader r)
    {
        var s = r.ReadString();
        if (s.Length > MaxNameLength) throw new InvalidDataException("이름이 너무 김");
        return s;
    }

    private static void WriteBytes(BinaryWriter w, byte[] data) { w.Write(data.Length); w.Write(data); }

    private static byte[] ReadBytes(BinaryReader r)
    {
        int n = r.ReadInt32();
        if (n < 0 || n > MaxFrameBytes) throw new InvalidDataException("바이트 길이가 잘못됨");
        return r.ReadBytes(n);
    }
}
