using ShareCom.App.Infra;
using ShareCom.App.Input;

namespace ShareCom.App;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        // 탐색기(보통 권한)가 관리자 권한인 이 프로세스의 가상 파일을 읽을 수 있게 한다. 가장 먼저 불러야 한다.
        ComSecurity.AllowMediumIntegrityCallers();

        if (args.Length == 2 && args[0] == "--selftest-clipboard")
        {
            ApplicationConfiguration.Initialize();
            return SelfTest.RunClipboard(args[1]);
        }
        if (args.Length == 3 && args[0] == "--selftest-files")
        {
            ApplicationConfiguration.Initialize();
            return SelfTest.ServeFiles(args[1], int.Parse(args[2]));
        }
        if (args.Length == 2 && args[0] == "--selftest-layout")
        {
            ApplicationConfiguration.Initialize();
            return SelfTest.RenderLayoutEditor(args[1]);
        }
        if (args.Length == 2 && args[0] == "--selftest-settings")
        {
            ApplicationConfiguration.Initialize();
            return SelfTest.RenderSettings(args[1]);
        }
        if (args.Length == 2 && args[0] == "--selftest-autostart")
            return SelfTest.RunAutoStart(args[1]);
        if (args.Length == 4 && args[0] == "--selftest-update")
            return SelfTest.RunUpdate(args[1], args[2], fail: args[3] == "fail");
        if ((args.Contains(UpdateHandoff.AfterUpdateArg) || args.Contains(UpdateHandoff.UpdateFailedArg))
            && Environment.GetEnvironmentVariable(SelfTest.UpdateChildVariable) is { Length: > 0 } childMode)
            return SelfTest.RunUpdateChild(childMode, args);

        var startup = ParseStartup(args);
        // 업데이트 직후에는 이전 프로세스가 잠금을 놓을 때까지 잠깐 기다린다.
        var mutex = new Mutex(false, @"Global\ShareCom-SingleInstance-v1");
        bool first;
        try { first = mutex.WaitOne(startup.UpdatedEvent is not null || startup.FailedVersion is not null ? 15000 : 0); }
        catch (AbandonedMutexException) { first = true; }
        if (!first)
        {
            mutex.Dispose();
            MessageBox.Show("ShareCom 이 이미 실행 중입니다. 작업 표시줄 오른쪽 트레이 아이콘을 확인하세요.",
                "ShareCom", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 0;
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
        PendingUpdate? pending = null;
        try
        {
            using var app = new TrayApp(startup);
            Application.Run(app);
            pending = app.PendingUpdate;
        }
        catch (Exception ex)
        {
            Log.Error("시작 실패", ex);
            if (startup.UpdatedEvent is not null) return 3; // 업데이트 직후 실패: 이전 프로세스가 되돌린다
            MessageBox.Show($"ShareCom 을 시작하지 못했습니다.\n\n{ex.Message}", "ShareCom", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            CursorHider.RestoreNow();
            mutex.ReleaseMutex();
            mutex.Dispose();
            if (pending is not null)
                UpdateHandoff.Run(pending, SkipVersion);
            Log.Flush();
        }
        return 0;
    }

    private static StartupOptions ParseStartup(string[] args)
    {
        string? Arg(string name, int offset = 1)
        {
            int i = Array.IndexOf(args, name);
            return i >= 0 && i + offset < args.Length ? args[i + offset] : null;
        }
        return new StartupOptions(
            AtLogon: args.Contains(AutoStart.Argument),
            UpdatedEvent: Arg(UpdateHandoff.AfterUpdateArg),
            UpdatedVersion: Arg(UpdateHandoff.AfterUpdateArg, 2),
            FailedVersion: Arg(UpdateHandoff.UpdateFailedArg));
    }

    /// <summary>시작하지 못해 되돌린 버전을 기록한다. 그 버전은 자동으로 다시 받지 않는다.</summary>
    private static bool SkipVersion(string version)
    {
        var storage = new AppStorage();
        storage.Settings.SkippedUpdateVersion = version;
        storage.Save();
        return true;
    }
}
