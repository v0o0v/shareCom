using System.Diagnostics;
using System.Runtime.InteropServices;
using ShareCom.Core.Update;

namespace ShareCom.App.Infra;

/// <summary>받아 두었고 검증을 마친 업데이트.</summary>
internal sealed record ReadyUpdate(UpdateCandidate Candidate, string File)
{
    public string VersionText => UpdateVersion.Display(Candidate.Version);
}

/// <summary>
/// GitHub 정식 릴리즈를 주기적으로 확인해 새 버전을 받아 두고, 이 PC가 쉬고 있을 때 적용하라고 알린다.
/// 확인·받기는 스레드 풀에서, 적용(<see cref="ApplyRequested"/>)은 호출한 쪽이 UI 스레드로 넘긴다.
/// </summary>
internal sealed class UpdateService : IDisposable
{
    private readonly UpdateChecker _checker;
    private readonly HttpClient _http;
    private readonly Version _current;
    private readonly Func<bool> _enabled;
    private readonly Func<string?> _skipped;
    private readonly Func<UpdateConditions> _conditions;
    private readonly System.Threading.Timer _checkTimer;
    private readonly System.Threading.Timer _applyTimer;
    private readonly SemaphoreSlim _checking = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private DateTime _lastPeerTrigger = DateTime.MinValue;
    private ReadyUpdate? _ready;
    private string? _lastBlockReason;

    public static string DownloadDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ShareCom", "update");

    /// <summary>설정 창에 보여 줄 마지막 상태.</summary>
    public string Status { get; private set; } = "아직 확인하지 않았습니다.";

    /// <summary>받아 둔 업데이트를 지금 적용해도 된다(스레드 풀에서 불린다).</summary>
    public event Action<ReadyUpdate>? ApplyRequested;

    public UpdateService(Version current, Func<bool> enabled, Func<string?> skipped, Func<UpdateConditions> conditions)
    {
        _current = UpdateVersion.Normalize(current);
        _enabled = enabled;
        _skipped = skipped;
        _conditions = conditions;
        _http = UpdateChecker.CreateHttpClient();
        _checker = new UpdateChecker(_http, UpdateKeys.All);
        _checkTimer = new System.Threading.Timer(_ => _ = SafeCheckAsync(manual: false), null, Timeout.Infinite, Timeout.Infinite);
        _applyTimer = new System.Threading.Timer(_ => ApplyTick(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public void Start()
    {
        CleanDownloads(keep: null);
        _checkTimer.Change(UpdatePolicy.FirstCheckDelay, UpdatePolicy.CheckInterval);
    }

    /// <summary>상대 PC가 더 높은 버전이라 연결하지 못했다. 30분에 한 번까지 곧바로 확인한다.</summary>
    public void OnNewerPeer(int peerProtocol)
    {
        if (!_enabled()) return;
        lock (_cts)
        {
            if (DateTime.UtcNow - _lastPeerTrigger < UpdatePolicy.PeerTriggerInterval) return;
            _lastPeerTrigger = DateTime.UtcNow;
        }
        Log.Info($"상대 PC의 통신 버전({peerProtocol})이 더 높아 업데이트를 확인합니다.");
        _ = SafeCheckAsync(manual: false);
    }

    private async Task SafeCheckAsync(bool manual)
    {
        try { await CheckAsync(manual).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Status = $"확인하지 못했습니다({DateTime.Now:HH:mm}): {ex.Message}";
            Log.Info("업데이트 확인 실패: " + ex.Message);
        }
    }

    /// <summary>
    /// 확인하고, 새 버전이 있으면 받아 둔다. manual 이면 설정이 꺼져 있거나 종량제 연결이거나 건너뛴 버전이어도 확인한다.
    /// 받을 업데이트가 없으면 null. 실패하면 예외.
    /// </summary>
    public async Task<ReadyUpdate?> CheckAsync(bool manual)
    {
        if (!manual && !_enabled()) return null;
        if (!manual && NetworkCost.IsMetered())
        {
            Status = $"데이터 통신(종량제) 연결이라 확인을 미뤘습니다({DateTime.Now:HH:mm}).";
            Log.Info("종량제 연결이라 업데이트 확인을 미룹니다.");
            return null;
        }
        await _checking.WaitAsync(_cts.Token).ConfigureAwait(false);
        try
        {
            Status = "확인하는 중…";
            var candidate = await _checker.CheckAsync(_current, _cts.Token).ConfigureAwait(false);
            if (candidate is null || !UpdatePolicy.ShouldOffer(candidate.Version, _current, _skipped(), manual))
            {
                Status = candidate is null
                    ? $"최신 버전입니다(확인 {DateTime.Now:HH:mm})."
                    : $"{UpdateVersion.Display(candidate.Version)} 은 지난번에 시작하지 못해 건너뜁니다(확인 {DateTime.Now:HH:mm}).";
                return null;
            }
            if (_ready is { } r && r.Candidate.Version == candidate.Version && File.Exists(r.File)) return r;

            var path = Path.Combine(DownloadDirectory, $"ShareCom-{UpdateVersion.Display(candidate.Version)}.exe");
            bool have = false;
            if (File.Exists(path))
            {
                try { UpdateManifest.VerifyFile(path, candidate.Payload); have = true; }
                catch (UpdateException) { }
            }
            if (!have)
            {
                Status = $"{UpdateVersion.Display(candidate.Version)} 을 받는 중…";
                Log.Info($"업데이트 받기 시작: {candidate.Payload.Tag} ({candidate.Payload.Size} 바이트)");
                await _checker.DownloadAsync(candidate, path, _cts.Token).ConfigureAwait(false);
            }
            CleanDownloads(keep: path);
            var ready = new ReadyUpdate(candidate, path);
            _ready = ready;
            Status = $"{ready.VersionText} 을 받아 두었습니다. 이 PC를 쓰지 않을 때 바꿉니다.";
            Log.Info($"업데이트 준비됨: {candidate.Payload.Tag}, 서명 확인, SHA-256 {candidate.Payload.Sha256[..16]}…");
            _applyTimer.Change(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
            return ready;
        }
        finally
        {
            _checking.Release();
        }
    }

    private void ApplyTick()
    {
        try
        {
            if (_ready is not { } ready || !_enabled()) return;
            var reason = UpdatePolicy.BlockReason(_conditions(), manual: false);
            if (reason is not null)
            {
                if (reason != _lastBlockReason) Log.Info($"업데이트 {ready.VersionText} 적용을 미룹니다: {reason}");
                _lastBlockReason = reason;
                return;
            }
            _applyTimer.Change(Timeout.Infinite, Timeout.Infinite);
            ApplyRequested?.Invoke(ready);
        }
        catch (Exception ex) { Log.Error("업데이트 적용 판단 실패", ex); }
    }

    /// <summary>지금은 적용할 수 없었다. 받아 둔 것을 그대로 두고 조금 뒤 다시 따져 본다.</summary>
    public void RetryLater() =>
        _applyTimer.Change(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));

    /// <summary>파일을 바꾸지 못했다. 받아 둔 것을 버리고 다음 확인 때 다시 시도한다.</summary>
    public void ApplyFailed(string reason)
    {
        Status = $"바꾸지 못했습니다: {reason}";
        _ready = null;
    }

    private static void CleanDownloads(string? keep)
    {
        try
        {
            if (!Directory.Exists(DownloadDirectory)) return;
            foreach (var f in Directory.GetFiles(DownloadDirectory))
                if (keep is null || !f.Equals(keep, StringComparison.OrdinalIgnoreCase))
                    try { File.Delete(f); } catch { }
        }
        catch { }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _checkTimer.Dispose();
        _applyTimer.Dispose();
        _http.Dispose();
    }
}

/// <summary>지금 인터넷 연결이 데이터 통신(종량제) 연결인지. 알 수 없으면 false.</summary>
internal static class NetworkCost
{
    [ComImport, Guid("DCB00C01-570F-4A9B-8D69-199FDBA5723B")]
    private class NetworkListManager;

    [ComImport, Guid("DCB00008-570F-4A9B-8D69-199FDBA5723B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface INetworkCostManager
    {
        void GetCost(out uint cost, IntPtr destIPAddr);
    }

    private const uint Unrestricted = 0x1;
    private const uint Fixed = 0x2;
    private const uint Variable = 0x4;
    private const uint OverDataLimit = 0x10000;
    private const uint Roaming = 0x40000;

    public static bool IsMetered()
    {
        object? manager = null;
        try
        {
            manager = new NetworkListManager();
            ((INetworkCostManager)manager).GetCost(out var cost, IntPtr.Zero);
            return (cost & (Fixed | Variable | OverDataLimit | Roaming)) != 0 && (cost & Unrestricted) == 0;
        }
        catch (Exception ex)
        {
            Log.Info("연결 요금 종류를 읽지 못했습니다: " + ex.Message);
            return false;
        }
        finally
        {
            if (manager is not null) Marshal.ReleaseComObject(manager);
        }
    }
}

/// <summary>앱을 끈 뒤 실제로 exe 를 넘겨주는 일. Program.Main 이 부른다.</summary>
internal sealed record PendingUpdate(string Exe, string Version, Uri PageUrl);

internal static class UpdateHandoff
{
    public const string AfterUpdateArg = "--after-update";
    public const string UpdateFailedArg = "--update-failed";

    /// <summary>
    /// 바꿔 둔 새 exe 를 띄우고 정상 시작 신호를 기다린다. 오지 않으면 되돌리고 이전 exe 를 다시 띄운다.
    /// 앱을 정리하고 단일 실행 잠금을 놓은 뒤에 불러야 한다.
    /// </summary>
    public static void Run(PendingUpdate p, Func<string, bool>? skipVersion = null)
    {
        var eventName = $@"Local\ShareCom-Updated-{Guid.NewGuid():N}";
        using var started = new EventWaitHandle(false, EventResetMode.ManualReset, eventName);
        bool ok = false;
        string why = "";
        Process? child = null;
        try
        {
            child = Process.Start(new ProcessStartInfo(p.Exe)
            {
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(p.Exe)!,
                ArgumentList = { AfterUpdateArg, eventName, p.Version },
            });
            if (child is null) why = "새 버전을 띄우지 못했습니다.";
            else
            {
                var until = DateTime.UtcNow + UpdatePolicy.StartupTimeout;
                while (DateTime.UtcNow < until)
                {
                    if (started.WaitOne(200)) { ok = true; break; }
                    if (child.HasExited) { why = $"새 버전이 바로 꺼졌습니다(종료 코드 {child.ExitCode})."; break; }
                }
                if (!ok && why.Length == 0) why = $"새 버전이 {UpdatePolicy.StartupTimeout.TotalSeconds:0}초 안에 시작하지 않았습니다.";
            }
        }
        catch (Exception ex) { why = "새 버전을 띄우지 못했습니다: " + ex.Message; }

        if (ok)
        {
            Log.Info($"새 버전 {p.Version} 이 시작했습니다. 이전 버전을 끝냅니다.");
            return;
        }

        Log.Info($"업데이트 {p.Version} 실패, 되돌립니다: {why}");
        try { if (child is { HasExited: false }) { child.Kill(entireProcessTree: true); child.WaitForExit(5000); } }
        catch (Exception ex) { Log.Error("새 버전 프로세스를 끝내지 못했습니다", ex); }
        try { UpdateInstaller.Rollback(p.Exe); }
        catch (Exception ex) { Log.Error("업데이트 되돌리기 실패", ex); }
        try { skipVersion?.Invoke(p.Version); }
        catch (Exception ex) { Log.Error("건너뛸 버전 기록 실패", ex); }
        try
        {
            Process.Start(new ProcessStartInfo(p.Exe)
            {
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(p.Exe)!,
                ArgumentList = { UpdateFailedArg, p.Version },
            });
        }
        catch (Exception ex) { Log.Error("이전 버전을 다시 띄우지 못했습니다", ex); }
    }

    /// <summary>새 exe 쪽: 시작을 마쳤다고 이전 프로세스에 알린다.</summary>
    public static void SignalStarted(string eventName)
    {
        try
        {
            using var e = EventWaitHandle.OpenExisting(eventName);
            e.Set();
        }
        catch (Exception ex) { Log.Info("업데이트 시작 신호를 보내지 못했습니다: " + ex.Message); }
    }
}
