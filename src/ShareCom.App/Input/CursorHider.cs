using System.Collections.Concurrent;
using static ShareCom.App.Native.Win32;

namespace ShareCom.App.Input;

/// <summary>
/// 다른 PC를 조작하는 동안 내 PC의 커서를 숨긴다. 시스템 커서를 투명한 커서로 바꿨다가 되돌린다.
/// 훅 스레드를 붙잡지 않게 별도 스레드에서 처리한다.
/// </summary>
internal static class CursorHider
{
    private static readonly uint[] CursorIds =
        [32512, 32513, 32514, 32515, 32516, 32642, 32643, 32644, 32645, 32646, 32648, 32649, 32650, 32651];

    private static readonly BlockingCollection<bool> Requests = new();
    private static bool _hidden;

    static CursorHider()
    {
        var t = new Thread(() =>
        {
            foreach (var hide in Requests.GetConsumingEnumerable())
            {
                // 밀린 요청은 마지막 것만 따른다.
                var want = hide;
                while (Requests.TryTake(out var next)) want = next;
                Apply(want);
            }
        }) { IsBackground = true, Name = "ShareCom 커서" };
        t.Start();
    }

    public static void Hide() => Requests.Add(true);
    public static void Show() => Requests.Add(false);

    /// <summary>프로그램이 끝날 때·시작할 때 바로 되돌린다(지난번에 비정상 종료했어도 커서가 보이게).</summary>
    public static void RestoreNow()
    {
        SystemParametersInfo(SPI_SETCURSORS, 0, IntPtr.Zero, 0);
        _hidden = false;
    }

    private static void Apply(bool hide)
    {
        if (hide == _hidden) return;
        if (hide)
        {
            var and = Enumerable.Repeat((byte)0xFF, 32 * 32 / 8).ToArray();
            var xor = new byte[32 * 32 / 8];
            foreach (var id in CursorIds)
            {
                var blank = CreateCursor(IntPtr.Zero, 0, 0, 32, 32, and, xor);
                if (blank != IntPtr.Zero) SetSystemCursor(blank, id); // SetSystemCursor 가 커서를 가져간다
            }
            _hidden = true;
        }
        else
        {
            RestoreNow();
        }
    }
}
