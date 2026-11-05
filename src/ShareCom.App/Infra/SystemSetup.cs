using System.Diagnostics;
using System.Security.Principal;

namespace ShareCom.App.Infra;

internal static class SystemSetup
{
    private const string RuleName = "ShareCom";

    public static bool IsAdministrator()
    {
        using var id = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
    }

    /// <summary>
    /// 이 exe 로 들어오는 연결(TCP·UDP)을 모든 망 종류(공용 와이파이 포함)에서 허용하는 방화벽 규칙을 둔다.
    /// exe 위치가 바뀌었을 수 있으니 매번 지우고 다시 만든다.
    /// </summary>
    public static void EnsureFirewallRule()
    {
        if (!IsAdministrator())
        {
            Log.Info("관리자 권한이 아니라 방화벽 규칙을 추가하지 않습니다.");
            return;
        }
        var exe = Environment.ProcessPath;
        if (exe is null) return;
        Run("netsh", $"advfirewall firewall delete rule name=\"{RuleName}\"");
        var code = Run("netsh", $"advfirewall firewall add rule name=\"{RuleName}\" dir=in action=allow program=\"{exe}\" enable=yes profile=any");
        Log.Info(code == 0 ? $"방화벽 규칙 추가: {exe}" : $"방화벽 규칙 추가 실패 (코드 {code})");
    }

    private static int Run(string file, string args)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(file, args)
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            if (p is null) return -1;
            p.StandardOutput.ReadToEnd();
            p.StandardError.ReadToEnd();
            p.WaitForExit(10_000);
            return p.ExitCode;
        }
        catch (Exception ex)
        {
            Log.Error($"{file} 실행 실패", ex);
            return -1;
        }
    }
}
