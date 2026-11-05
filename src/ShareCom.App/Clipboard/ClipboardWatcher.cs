using System.Diagnostics;
using System.Runtime.InteropServices;
using ShareCom.App.Infra;

namespace ShareCom.App.Clipboard;

/// <summary>
/// 클립보드가 바뀌면 알린다(WM_CLIPBOARDUPDATE). 메시지 전용 창으로 받으며 UI 스레드에서 만들어야 한다.
/// 내 프로세스가 쓴 변경은 알리지 않는다. 짧은 시간에 여러 번 바뀌면 마지막 한 번만 알린다.
/// </summary>
internal sealed class ClipboardWatcher : NativeWindow, IDisposable
{
    private const int WM_CLIPBOARDUPDATE = 0x031D;
    private static readonly IntPtr HWND_MESSAGE = new(-3);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool AddClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetClipboardOwner();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    private readonly System.Windows.Forms.Timer _debounce = new() { Interval = 150 };
    private readonly uint _myPid = (uint)Environment.ProcessId;
    private long _ignoreUntil;

    public event Action? Changed;

    public ClipboardWatcher()
    {
        CreateHandle(new CreateParams { Caption = "ShareCom 클립보드", Parent = HWND_MESSAGE });
        if (!AddClipboardFormatListener(Handle))
            Log.Error($"클립보드 감시 시작 실패 (오류 {Marshal.GetLastWin32Error()})");
        _debounce.Tick += (_, _) =>
        {
            _debounce.Stop();
            Changed?.Invoke();
        };
    }

    /// <summary>내가 클립보드에 쓰기 직전에 부른다. 잠깐 동안 생기는 변경 알림을 무시한다.</summary>
    public void MarkSelfWrite() => Interlocked.Exchange(ref _ignoreUntil, Stopwatch.GetTimestamp() + Stopwatch.Frequency / 2);

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_CLIPBOARDUPDATE)
        {
            if (!IsOwnChange())
            {
                _debounce.Stop();
                _debounce.Start();
            }
            return;
        }
        base.WndProc(ref m);
    }

    private bool IsOwnChange()
    {
        if (Stopwatch.GetTimestamp() < Interlocked.Read(ref _ignoreUntil)) return true;
        var owner = GetClipboardOwner();
        if (owner == IntPtr.Zero) return false;
        GetWindowThreadProcessId(owner, out var pid);
        return pid == _myPid;
    }

    public void Dispose()
    {
        _debounce.Dispose();
        if (Handle != IntPtr.Zero)
        {
            RemoveClipboardFormatListener(Handle);
            DestroyHandle();
        }
    }
}
