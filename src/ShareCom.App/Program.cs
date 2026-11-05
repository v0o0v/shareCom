using ShareCom.App.Infra;
using ShareCom.App.Input;

namespace ShareCom.App;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        using var mutex = new Mutex(true, @"Global\ShareCom-SingleInstance-v1", out bool first);
        if (!first)
        {
            MessageBox.Show("ShareCom 이 이미 실행 중입니다. 작업 표시줄 오른쪽 트레이 아이콘을 확인하세요.",
                "ShareCom", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        Log.Init(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ShareCom"));
        // 지난번에 커서를 숨긴 채 비정상 종료했어도 커서가 보이게 되돌린다.
        CursorHider.RestoreNow();

        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => Log.Error("처리하지 못한 오류(UI)", e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            Log.Error("처리하지 못한 오류", e.ExceptionObject as Exception);
            CursorHider.RestoreNow();
            Log.Flush();
        };
        AppDomain.CurrentDomain.ProcessExit += (_, _) => { CursorHider.RestoreNow(); Log.Flush(); };
        TaskScheduler.UnobservedTaskException += (_, e) => { Log.Error("관찰하지 않은 작업 오류", e.Exception); e.SetObserved(); };

        ApplicationConfiguration.Initialize();
        try
        {
            using var app = new TrayApp();
            Application.Run(app);
        }
        catch (Exception ex)
        {
            Log.Error("시작 실패", ex);
            MessageBox.Show($"ShareCom 을 시작하지 못했습니다.\n\n{ex.Message}", "ShareCom", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            CursorHider.RestoreNow();
            Log.Flush();
        }
    }
}
