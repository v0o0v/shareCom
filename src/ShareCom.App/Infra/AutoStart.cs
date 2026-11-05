using System.Diagnostics;
using System.Security;
using System.Security.Principal;
using System.Text;

namespace ShareCom.App.Infra;

/// <summary>
/// 로그인할 때 자동 시작. 작업 스케줄러에 '가장 높은 권한'으로 등록해 UAC 창 없이 관리자 권한으로 켠다.
/// 등록·해제에는 관리자 권한이 필요하다(이 앱은 원래 관리자 권한으로 실행된다).
/// </summary>
internal static class AutoStart
{
    public const string Argument = "--autostart";

    /// <summary>사용자마다 따로 등록한다.</summary>
    public static string TaskName => $"ShareCom ({Environment.UserName})";

    public static bool IsRegistered() => Run(["/Query", "/TN", TaskName], out _) == 0;

    /// <summary>등록(이미 있으면 덮어쓴다). 성공하면 null, 실패하면 오류 문구.</summary>
    public static string? Register(string exePath)
    {
        if (!SystemSetup.IsAdministrator()) return "관리자 권한으로 실행해야 자동 시작을 등록할 수 있습니다.";
        var xmlPath = Path.Combine(Path.GetTempPath(), $"sharecom-task-{Guid.NewGuid():N}.xml");
        try
        {
            // schtasks /XML 은 UTF-16 파일을 읽는다.
            File.WriteAllText(xmlPath, BuildXml(exePath), Encoding.Unicode);
            int code = Run(["/Create", "/TN", TaskName, "/XML", xmlPath, "/F"], out var output);
            if (code == 0)
            {
                Log.Info($"자동 시작 등록: {exePath}");
                return null;
            }
            Log.Info($"자동 시작 등록 실패 (코드 {code}): {output}");
            return $"작업 스케줄러 등록에 실패했습니다(코드 {code}).";
        }
        catch (Exception ex)
        {
            Log.Error("자동 시작 등록 실패", ex);
            return ex.Message;
        }
        finally
        {
            try { File.Delete(xmlPath); } catch { }
        }
    }

    /// <summary>해제. 성공하거나 원래 없으면 null.</summary>
    public static string? Unregister()
    {
        if (!IsRegistered()) return null;
        if (!SystemSetup.IsAdministrator()) return "관리자 권한으로 실행해야 자동 시작을 해제할 수 있습니다.";
        int code = Run(["/Delete", "/TN", TaskName, "/F"], out var output);
        if (code == 0)
        {
            Log.Info("자동 시작 해제");
            return null;
        }
        Log.Info($"자동 시작 해제 실패 (코드 {code}): {output}");
        return $"작업 스케줄러에서 지우지 못했습니다(코드 {code}).";
    }

    internal static string BuildXml(string exePath)
    {
        using var id = WindowsIdentity.GetCurrent();
        string user = SecurityElement.Escape(id.Name);
        string exe = SecurityElement.Escape(exePath);
        string dir = SecurityElement.Escape(Path.GetDirectoryName(exePath) ?? "");
        // 노트북 배터리에서도 켜지고, 시간 제한 없이, 보통 우선순위(입력 훅이 늦지 않게)로 돈다.
        return $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo>
                <Description>ShareCom: 로그인할 때 자동으로 시작합니다.</Description>
              </RegistrationInfo>
              <Triggers>
                <LogonTrigger>
                  <Enabled>true</Enabled>
                  <UserId>{user}</UserId>
                  <Delay>PT3S</Delay>
                </LogonTrigger>
              </Triggers>
              <Principals>
                <Principal id="Author">
                  <UserId>{user}</UserId>
                  <LogonType>InteractiveToken</LogonType>
                  <RunLevel>HighestAvailable</RunLevel>
                </Principal>
              </Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <AllowHardTerminate>true</AllowHardTerminate>
                <StartWhenAvailable>false</StartWhenAvailable>
                <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
                <IdleSettings>
                  <StopOnIdleEnd>false</StopOnIdleEnd>
                  <RestartOnIdle>false</RestartOnIdle>
                </IdleSettings>
                <AllowStartOnDemand>true</AllowStartOnDemand>
                <Enabled>true</Enabled>
                <Hidden>false</Hidden>
                <RunOnlyIfIdle>false</RunOnlyIfIdle>
                <WakeToRun>false</WakeToRun>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <Priority>4</Priority>
              </Settings>
              <Actions Context="Author">
                <Exec>
                  <Command>{exe}</Command>
                  <Arguments>{Argument}</Arguments>
                  <WorkingDirectory>{dir}</WorkingDirectory>
                </Exec>
              </Actions>
            </Task>
            """;
    }

    private static int Run(string[] args, out string output)
    {
        output = "";
        try
        {
            var psi = new ProcessStartInfo("schtasks.exe")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi);
            if (p is null) return -1;
            var err = p.StandardError.ReadToEndAsync();
            output = p.StandardOutput.ReadToEnd();
            output += err.Result;
            if (!p.WaitForExit(15_000)) { try { p.Kill(); } catch { } return -1; }
            return p.ExitCode;
        }
        catch (Exception ex)
        {
            Log.Error("schtasks 실행 실패", ex);
            return -1;
        }
    }
}
