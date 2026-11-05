using System.Runtime.InteropServices;
using ShareCom.App.Infra;
using ShareCom.Core.Protocol;
using static ShareCom.App.Native.Win32;

namespace ShareCom.App.Input;

/// <summary>다른 PC에서 온 입력을 이 PC에서 SendInput 으로 재현한다. 눌러 둔 키·버튼을 기억했다가 한꺼번에 뗄 수 있다.</summary>
internal sealed class InputInjector
{
    private readonly object _gate = new();
    private readonly Dictionary<ushort, KeyMessage> _keysDown = new();
    private readonly HashSet<MouseButtonKind> _buttonsDown = new();
    private static readonly int InputSize = Marshal.SizeOf<INPUT>();

    // 스캔 코드로 재현하면 잘못되는 키는 가상 키 코드로 보낸다.
    private static readonly HashSet<ushort> VirtualKeyOnly =
    [
        0x13, // Pause (스캔 코드가 NumLock 과 겹친다)
        0x90, // NumLock
        0x15, // 한/영 (VK_HANGUL)
        0x19, // 한자 (VK_HANJA)
    ];

    public void MoveTo(int x, int y)
    {
        int vx = GetSystemMetrics(SM_XVIRTUALSCREEN), vy = GetSystemMetrics(SM_YVIRTUALSCREEN);
        int vw = Math.Max(2, GetSystemMetrics(SM_CXVIRTUALSCREEN)), vh = Math.Max(2, GetSystemMetrics(SM_CYVIRTUALSCREEN));
        int nx = (int)Math.Round((x - vx) * 65535.0 / (vw - 1));
        int ny = (int)Math.Round((y - vy) * 65535.0 / (vh - 1));
        SendMouse(new MOUSEINPUT { dx = nx, dy = ny, dwFlags = MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK });
    }

    public void Button(MouseButtonKind button, bool down)
    {
        lock (_gate) { if (down) _buttonsDown.Add(button); else _buttonsDown.Remove(button); }
        var mi = button switch
        {
            MouseButtonKind.Left => new MOUSEINPUT { dwFlags = down ? MOUSEEVENTF_LEFTDOWN : MOUSEEVENTF_LEFTUP },
            MouseButtonKind.Right => new MOUSEINPUT { dwFlags = down ? MOUSEEVENTF_RIGHTDOWN : MOUSEEVENTF_RIGHTUP },
            MouseButtonKind.Middle => new MOUSEINPUT { dwFlags = down ? MOUSEEVENTF_MIDDLEDOWN : MOUSEEVENTF_MIDDLEUP },
            MouseButtonKind.X1 => new MOUSEINPUT { dwFlags = down ? MOUSEEVENTF_XDOWN : MOUSEEVENTF_XUP, mouseData = XBUTTON1 },
            MouseButtonKind.X2 => new MOUSEINPUT { dwFlags = down ? MOUSEEVENTF_XDOWN : MOUSEEVENTF_XUP, mouseData = XBUTTON2 },
            _ => (MOUSEINPUT?)null,
        };
        if (mi is { } m) SendMouse(m);
    }

    public void Wheel(int delta, bool horizontal) =>
        SendMouse(new MOUSEINPUT { mouseData = unchecked((uint)delta), dwFlags = horizontal ? MOUSEEVENTF_HWHEEL : MOUSEEVENTF_WHEEL });

    public void Key(KeyMessage k)
    {
        lock (_gate) { if (k.Down) _keysDown[k.VirtualKey] = k; else _keysDown.Remove(k.VirtualKey); }
        var ki = new KEYBDINPUT();
        uint flags = k.Down ? 0 : KEYEVENTF_KEYUP;
        if (k.Extended) flags |= KEYEVENTF_EXTENDEDKEY;
        if (k.ScanCode != 0 && !VirtualKeyOnly.Contains(k.VirtualKey))
        {
            ki.wScan = k.ScanCode;
            flags |= KEYEVENTF_SCANCODE;
        }
        else
        {
            ki.wVk = k.VirtualKey;
            ki.wScan = (ushort)MapVirtualKey(k.VirtualKey, MAPVK_VK_TO_VSC);
        }
        ki.dwFlags = flags;
        Send(new INPUT { type = INPUT_KEYBOARD, u = new InputUnion { ki = ki } });
    }

    /// <summary>재현하느라 눌러 둔 키·버튼을 모두 뗀다(연결 끊김, 주인 되찾기, 조작 끝).</summary>
    public void ReleaseAll()
    {
        List<KeyMessage> keys;
        List<MouseButtonKind> buttons;
        lock (_gate)
        {
            keys = _keysDown.Values.ToList();
            buttons = _buttonsDown.ToList();
        }
        foreach (var k in keys) Key(k with { Down = false });
        foreach (var b in buttons) Button(b, false);
    }

    private static void SendMouse(MOUSEINPUT mi) => Send(new INPUT { type = INPUT_MOUSE, u = new InputUnion { mi = mi } });

    private static void Send(INPUT input)
    {
        if (SendInput(1, [input], InputSize) == 0)
            Log.Debug($"SendInput 실패 (오류 {Marshal.GetLastWin32Error()}) - 보안 화면(UAC·잠금)일 수 있음");
    }
}
