using System.Runtime.InteropServices;
using ShareCom.App.Infra;
using ShareCom.App.Native;
using ShareCom.Core.Input;
using ShareCom.Core.Protocol;
using static ShareCom.App.Native.Win32;

namespace ShareCom.App.Input;

/// <summary>
/// 저수준 마우스·키보드 훅. 전용 스레드에서 메시지 루프를 돌린다.
/// 훅 처리기는 빨리 끝나야 한다(오래 걸리면 Windows 가 훅을 조용히 떼어 버린다).
/// </summary>
internal sealed class InputHooks : IDisposable
{
    private readonly InputRouter _router;
    private readonly HookProc _mouseProc;
    private readonly HookProc _keyboardProc;
    private Thread? _thread;
    private uint _threadId;
    private IntPtr _mouseHook, _keyboardHook;

    public InputHooks(InputRouter router)
    {
        _router = router;
        _mouseProc = MouseProc;       // 대리자가 GC 로 사라지지 않게 필드에 둔다
        _keyboardProc = KeyboardProc;
    }

    public void Start()
    {
        using var ready = new ManualResetEventSlim();
        Exception? error = null;
        _thread = new Thread(() =>
        {
            try
            {
                _threadId = GetCurrentThreadId();
                var module = GetModuleHandle(null);
                _mouseHook = SetWindowsHookEx(WH_MOUSE_LL, _mouseProc, module, 0);
                _keyboardHook = SetWindowsHookEx(WH_KEYBOARD_LL, _keyboardProc, module, 0);
                if (_mouseHook == IntPtr.Zero || _keyboardHook == IntPtr.Zero)
                    throw new InvalidOperationException($"입력 훅 설치 실패 (오류 {Marshal.GetLastWin32Error()})");
            }
            catch (Exception ex) { error = ex; }
            ready.Set();
            if (error is not null) return;

            while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0) { }

            UnhookWindowsHookEx(_mouseHook);
            UnhookWindowsHookEx(_keyboardHook);
        })
        {
            IsBackground = true,
            Name = "ShareCom 입력 훅",
            Priority = ThreadPriority.Highest,
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        ready.Wait();
        if (error is not null) throw error;
    }

    private IntPtr MouseProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            try
            {
                var data = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                bool injected = (data.flags & (LLMHF_INJECTED | LLMHF_LOWER_IL_INJECTED)) != 0;
                int msg = (int)wParam;
                bool swallow = msg switch
                {
                    WM_MOUSEMOVE => _router.OnMouseMove(data.pt.X, data.pt.Y, injected),
                    WM_LBUTTONDOWN => _router.OnMouseButton(MouseButtonKind.Left, true, injected),
                    WM_LBUTTONUP => _router.OnMouseButton(MouseButtonKind.Left, false, injected),
                    WM_RBUTTONDOWN => _router.OnMouseButton(MouseButtonKind.Right, true, injected),
                    WM_RBUTTONUP => _router.OnMouseButton(MouseButtonKind.Right, false, injected),
                    WM_MBUTTONDOWN => _router.OnMouseButton(MouseButtonKind.Middle, true, injected),
                    WM_MBUTTONUP => _router.OnMouseButton(MouseButtonKind.Middle, false, injected),
                    WM_XBUTTONDOWN => _router.OnMouseButton(XButton(data.mouseData), true, injected),
                    WM_XBUTTONUP => _router.OnMouseButton(XButton(data.mouseData), false, injected),
                    WM_MOUSEWHEEL => _router.OnMouseWheel((short)(data.mouseData >> 16), false, injected),
                    WM_MOUSEHWHEEL => _router.OnMouseWheel((short)(data.mouseData >> 16), true, injected),
                    _ => false,
                };
                if (swallow) return 1;
            }
            catch (Exception ex) { Log.Error("마우스 훅 오류", ex); }
        }
        return CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }

    private IntPtr KeyboardProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            try
            {
                var data = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
                bool injected = (data.flags & (LLKHF_INJECTED | LLKHF_LOWER_IL_INJECTED)) != 0;
                int msg = (int)wParam;
                bool down = msg is WM_KEYDOWN or WM_SYSKEYDOWN;
                bool up = msg is WM_KEYUP or WM_SYSKEYUP;
                if (down || up)
                {
                    bool extended = (data.flags & LLKHF_EXTENDED) != 0;
                    if (_router.OnKey((ushort)data.vkCode, (ushort)data.scanCode, extended, down, injected)) return 1;
                }
            }
            catch (Exception ex) { Log.Error("키보드 훅 오류", ex); }
        }
        return CallNextHookEx(_keyboardHook, nCode, wParam, lParam);
    }

    private static MouseButtonKind XButton(uint mouseData) =>
        (mouseData >> 16) == XBUTTON2 ? MouseButtonKind.X2 : MouseButtonKind.X1;

    public void Dispose()
    {
        if (_threadId != 0) PostThreadMessage(_threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        _thread?.Join(1000);
    }
}
