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

    // 화면·배치
    ScreenInfo = 20,
    LayoutSide = 21,

    // 입력
    EnterControl = 30,
    LeaveControl = 31,
    MouseMove = 32,
    MouseButton = 33,
    MouseWheel = 34,
    Key = 35,
    TakeOver = 36,
}

public abstract record Message(MessageType Type);

/// <summary>연결 직후 서로 신원을 알린다. Nonce 는 평문 모드 서명 확인에 쓴다.</summary>
public sealed record AuthHelloMessage(Guid Id, string Name, int ProtocolVersion, int ListenPort, byte[] Nonce) : Message(MessageType.AuthHello);

/// <summary>평문 모드에서 인증서 개인 키로 서명한 신원 증명.</summary>
public sealed record AuthProofMessage(byte[] Signature) : Message(MessageType.AuthProof);

public sealed record PairDecisionMessage(bool Accepted) : Message(MessageType.PairDecision);

public sealed record PingMessage(long Ticks) : Message(MessageType.Ping);
public sealed record PongMessage(long Ticks) : Message(MessageType.Pong);

/// <summary>보내는 PC의 모니터 목록.</summary>
public sealed record ScreenInfoMessage(ScreenRect[] Monitors) : Message(MessageType.ScreenInfo);

/// <summary>"보내는 PC는 받는 PC의 <see cref="SenderIsOnYour"/> 쪽에 있다."</summary>
public sealed record LayoutSideMessage(NeighborSide SenderIsOnYour) : Message(MessageType.LayoutSide);

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
