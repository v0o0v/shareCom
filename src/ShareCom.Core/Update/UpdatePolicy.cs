namespace ShareCom.Core.Update;

/// <summary>업데이트를 지금 적용해도 되는지 판단하는 데 쓰는 현재 상태.</summary>
public readonly record struct UpdateConditions(
    bool Controlling,
    bool Controlled,
    bool Transferring,
    TimeSpan SinceLastInput,
    TimeSpan SinceControlEnded);

public static class UpdatePolicy
{
    /// <summary>켜고 처음 확인할 때까지.</summary>
    public static readonly TimeSpan FirstCheckDelay = TimeSpan.FromMinutes(2);
    /// <summary>그 뒤 확인 간격.</summary>
    public static readonly TimeSpan CheckInterval = TimeSpan.FromHours(6);
    /// <summary>상대가 더 높은 버전이라 연결이 거절됐을 때 다시 확인하기까지 최소 간격.</summary>
    public static readonly TimeSpan PeerTriggerInterval = TimeSpan.FromMinutes(30);
    /// <summary>자동 적용 전에 이 PC가 쉬고 있어야 하는 시간.</summary>
    public static readonly TimeSpan IdleRequired = TimeSpan.FromMinutes(5);
    /// <summary>새 버전이 정상 시작 신호를 보내야 하는 시간.</summary>
    public static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// candidate 를 받아야 하는지. 지금보다 높아야 하고, 자동 확인일 때는 건너뛰기로 한 버전이 아니어야 한다.
    /// 건너뛴 버전보다 더 높은 버전이 나오면 다시 받는다.
    /// </summary>
    public static bool ShouldOffer(Version candidate, Version current, string? skippedVersion, bool manual)
    {
        candidate = UpdateVersion.Normalize(candidate);
        if (candidate <= UpdateVersion.Normalize(current)) return false;
        if (manual) return true;
        return !(UpdateVersion.TryParse(skippedVersion, out var skipped) && candidate <= skipped);
    }

    /// <summary>지금 적용하면 안 되는 이유. 적용해도 되면 null.</summary>
    public static string? BlockReason(UpdateConditions c, bool manual)
    {
        if (c.Transferring) return "파일을 주고받는 중입니다.";
        if (manual) return null; // 사용자가 직접 [업데이트]를 눌렀다.
        if (c.Controlling) return "다른 PC를 조작하는 중입니다.";
        if (c.Controlled) return "다른 PC가 이 PC를 조작하는 중입니다.";
        if (c.SinceLastInput < IdleRequired) return "이 PC를 쓰는 중입니다.";
        if (c.SinceControlEnded < IdleRequired) return "조작이 끝난 지 얼마 안 됐습니다.";
        return null;
    }
}
