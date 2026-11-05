using System.Buffers.Binary;
using System.Text;

namespace ShareCom.Core.Protocol;

/// <summary>
/// 메시지 틀: [u32 LE 길이][u8 종류][본문]. 길이는 종류 바이트부터 센다.
/// </summary>
public static class MessageCodec
{
    public const int ProtocolVersion = 1;
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
            case ScreenInfoMessage x:
                w.Write(x.Monitors.Length);
                foreach (var s in x.Monitors) { w.Write(s.X); w.Write(s.Y); w.Write(s.Width); w.Write(s.Height); }
                break;
            case LayoutSideMessage x: w.Write((byte)x.SenderIsOnYour); break;
            case EnterControlMessage x: w.Write(x.X); w.Write(x.Y); break;
            case LeaveControlMessage: break;
            case MouseMoveMessage x: w.Write(x.X); w.Write(x.Y); break;
            case MouseButtonMessage x: w.Write((byte)x.Button); w.Write(x.Down); break;
            case MouseWheelMessage x: w.Write(x.Delta); w.Write(x.Horizontal); break;
            case KeyMessage x: w.Write(x.VirtualKey); w.Write(x.ScanCode); w.Write(x.Extended); w.Write(x.Down); break;
            case TakeOverMessage: break;
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
        MessageType.ScreenInfo => ReadScreenInfo(r),
        MessageType.LayoutSide => new LayoutSideMessage(ReadSide(r)),
        MessageType.EnterControl => new EnterControlMessage(r.ReadInt32(), r.ReadInt32()),
        MessageType.LeaveControl => new LeaveControlMessage(),
        MessageType.MouseMove => new MouseMoveMessage(r.ReadInt32(), r.ReadInt32()),
        MessageType.MouseButton => new MouseButtonMessage((MouseButtonKind)r.ReadByte(), r.ReadBoolean()),
        MessageType.MouseWheel => new MouseWheelMessage(r.ReadInt32(), r.ReadBoolean()),
        MessageType.Key => new KeyMessage(r.ReadUInt16(), r.ReadUInt16(), r.ReadBoolean(), r.ReadBoolean()),
        MessageType.TakeOver => new TakeOverMessage(),
        _ => throw new InvalidDataException($"알 수 없는 메시지 종류: {(byte)type}"),
    };

    private static NeighborSide ReadSide(BinaryReader r)
    {
        var side = (NeighborSide)r.ReadByte();
        if (side is not (NeighborSide.Left or NeighborSide.Right)) throw new InvalidDataException("잘못된 방향");
        return side;
    }

    private static ScreenInfoMessage ReadScreenInfo(BinaryReader r)
    {
        int n = r.ReadInt32();
        if (n < 0 || n > 64) throw new InvalidDataException("모니터 수가 잘못됨");
        var list = new ScreenRect[n];
        for (int i = 0; i < n; i++) list[i] = new ScreenRect(r.ReadInt32(), r.ReadInt32(), r.ReadInt32(), r.ReadInt32());
        return new ScreenInfoMessage(list);
    }

    private static void WriteBytes(BinaryWriter w, byte[] data) { w.Write(data.Length); w.Write(data); }

    private static byte[] ReadBytes(BinaryReader r)
    {
        int n = r.ReadInt32();
        if (n < 0 || n > MaxFrameBytes) throw new InvalidDataException("바이트 길이가 잘못됨");
        return r.ReadBytes(n);
    }
}
