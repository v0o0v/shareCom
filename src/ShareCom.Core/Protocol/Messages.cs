namespace ShareCom.Core.Protocol;

/// <summary>화면 한 칸(모니터 하나 또는 전체 가상 화면). 실제 픽셀 좌표.</summary>
public readonly record struct ScreenRect(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;
    public int Bottom => Y + Height;
    public bool Contains(int x, int y) => x >= X && x < Right && y >= Y && y < Bottom;

    public static ScreenRect Union(IReadOnlyList<ScreenRect> rects)
    {
        if (rects.Count == 0) return default;
        int l = rects.Min(r => r.X), t = rects.Min(r => r.Y);
        int r = rects.Max(r => r.Right), b = rects.Max(r => r.Bottom);
        return new ScreenRect(l, t, r - l, b - t);
    }
}

public enum MouseButtonKind : byte { Left = 1, Right = 2, Middle = 3, X1 = 4, X2 = 5 }

/// <summary>내 기준으로 상대 PC가 놓인 방향.</summary>
public enum NeighborSide : byte { Left = 1, Right = 2 }

public static class NeighborSideExtensions
{
    public static NeighborSide Opposite(this NeighborSide s) => s == NeighborSide.Left ? NeighborSide.Right : NeighborSide.Left;
}

public enum MessageType : byte
{
    // 연결 수립
    AuthHello = 1,
    AuthProof = 2,
    PairDecision = 3,

    // 연결 유지
    Ping = 10,
    Pong = 11,

    // 배치·신뢰
    Layout = 22,
    Trust = 23,

    // 입력
    EnterControl = 30,
    LeaveControl = 31,
    MouseMove = 32,
    MouseButton = 33,
    MouseWheel = 34,
    Key = 35,
    TakeOver = 36,

    // 큰 데이터(클립보드·파일) 조각
    BlobChunk = 40,

    // 파일 전송
    FileOpen = 50,
    FileChunk = 51,
    FileAck = 52,
    FileError = 53,
    FileCancel = 54,
    FileSendResponse = 55,
    FileSendResult = 56,
}

public abstract record Message(MessageType Type);

/// <summary>연결 직후 서로 신원을 알린다. Nonce 는 평문 모드 서명 확인에 쓴다.</summary>
public sealed record AuthHelloMessage(Guid Id, string Name, int ProtocolVersion, int ListenPort, byte[] Nonce) : Message(MessageType.AuthHello);

/// <summary>평문 모드에서 인증서 개인 키로 서명한 신원 증명.</summary>
public sealed record AuthProofMessage(byte[] Signature) : Message(MessageType.AuthProof);

public sealed record PairDecisionMessage(bool Accepted) : Message(MessageType.PairDecision);

public sealed record PingMessage(long Ticks) : Message(MessageType.Ping);
public sealed record PongMessage(long Ticks) : Message(MessageType.Pong);

/// <summary>PC 한 대의 화면: 모니터들(실제 픽셀, 그 PC의 가상 화면 좌표)과 배율(%, 주 모니터 기준).</summary>
public sealed record PcScreen(ScreenRect[] Monitors, int ScalePercent)
{
    public bool SameAs(PcScreen? other) =>
        other is not null && ScalePercent == other.ScalePercent && Monitors.AsSpan().SequenceEqual(other.Monitors);
}

/// <summary>배치 속 PC 하나. (X, Y) 는 모든 PC가 함께 쓰는 평면(논리 픽셀 = 실제 픽셀 ÷ 배율)에서 이 PC 화면 전체의 왼쪽 위.</summary>
public sealed record LayoutEntry(Guid Id, string Name, int X, int Y, PcScreen Screen);

/// <summary>
/// 모든 PC가 나눠 갖는 화면 배치. 더 새 판(Version 이 크고, 같으면 Editor 가 큰 쪽)이 이긴다.
/// 각 PC는 자기 항목(이름·화면)만 넣고 고치며, 배치 편집기는 위치를 고친다.
/// </summary>
public sealed record LayoutDoc(long Version, Guid Editor, LayoutEntry[] Entries)
{
    public static readonly LayoutDoc Empty = new(0, Guid.Empty, []);

    public bool IsNewerThan(LayoutDoc other) =>
        Version != other.Version ? Version > other.Version : Editor.CompareTo(other.Editor) > 0;

    public LayoutEntry? Find(Guid id) => Entries.FirstOrDefault(e => e.Id == id);
}

public sealed record LayoutMessage(LayoutDoc Doc) : Message(MessageType.Layout);

/// <summary>
/// 신뢰 목록 한 줄. Trusted 면 "이 PC를 믿는다(인증서 포함, 자동 소개)", 아니면 "이 PC와 짝을 풀었다".
/// 같은 PC에 대한 줄이 둘이면 At(UTC ticks)이 늦은 쪽이 이긴다.
/// </summary>
public sealed record TrustEntry(Guid Id, string Name, byte[] Certificate, string? Address, int Port, long AtUtcTicks, bool Trusted);

public sealed record TrustMessage(TrustEntry[] Entries) : Message(MessageType.Trust);

/// <summary>주인 PC가 이 PC를 조작하기 시작한다. 커서를 (X,Y) 로 옮긴다.</summary>
public sealed record EnterControlMessage(int X, int Y) : Message(MessageType.EnterControl);
public sealed record LeaveControlMessage() : Message(MessageType.LeaveControl);
public sealed record MouseMoveMessage(int X, int Y) : Message(MessageType.MouseMove);
public sealed record MouseButtonMessage(MouseButtonKind Button, bool Down) : Message(MessageType.MouseButton);
public sealed record MouseWheelMessage(int Delta, bool Horizontal) : Message(MessageType.MouseWheel);

/// <summary>키 하나. 스캔 코드로 재현하고, 스캔 코드로 안 되는 키는 VirtualKey 로 재현한다.</summary>
public sealed record KeyMessage(ushort VirtualKey, ushort ScanCode, bool Extended, bool Down) : Message(MessageType.Key);

/// <summary>조작당하던 PC에서 실제 입력이 생겨 그 PC가 주인을 되찾는다.</summary>
public sealed record TakeOverMessage() : Message(MessageType.TakeOver);

/// <summary>
/// 큰 데이터의 한 조각. 같은 BlobId 의 조각을 Offset 순서대로 모으면 TotalLength 바이트가 된다.
/// 마우스·키보드보다 늦게 보내는 줄(bulk)로 간다.
/// </summary>
public sealed record BlobChunkMessage(uint BlobId, BlobKind Kind, long TotalLength, long Offset, byte[] Data) : Message(MessageType.BlobChunk);

public enum BlobKind : byte { Clipboard = 1, FileSendRequest = 2 }

/// <summary>받는 쪽이 제안(OfferId)의 Index 번째 파일을 Offset 부터 보내 달라고 한다.</summary>
public sealed record FileOpenMessage(uint RequestId, Guid OfferId, int Index, long Offset) : Message(MessageType.FileOpen);
/// <summary>파일 내용 한 조각(대량 줄). Last 면 파일 끝.</summary>
public sealed record FileChunkMessage(uint RequestId, byte[] Data, bool Last) : Message(MessageType.FileChunk);
/// <summary>받는 쪽이 지금까지 읽은 바이트 수. 보내는 쪽은 확인받지 못한 양이 일정량을 넘지 않게 보낸다.</summary>
public sealed record FileAckMessage(uint RequestId, long Consumed) : Message(MessageType.FileAck);
public sealed record FileErrorMessage(uint RequestId, string Error) : Message(MessageType.FileError);
public sealed record FileCancelMessage(uint RequestId) : Message(MessageType.FileCancel);
/// <summary>트레이로 보낸 파일을 받을지 답한다.</summary>
public sealed record FileSendResponseMessage(Guid OfferId, bool Accepted) : Message(MessageType.FileSendResponse);
/// <summary>받는 쪽이 다 받았는지(또는 실패했는지) 알린다.</summary>
public sealed record FileSendResultMessage(Guid OfferId, bool Success, string Error) : Message(MessageType.FileSendResult);
