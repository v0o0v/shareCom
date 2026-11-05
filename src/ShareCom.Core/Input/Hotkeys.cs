namespace ShareCom.Core.Input;

[Flags]
public enum HotkeyModifiers
{
    None = 0,
    Ctrl = 1,
    Alt = 2,
    Shift = 4,
}

/// <summary>
/// 바꿀 수 있는 단축키. 긴급 복귀(Ctrl+Alt+Esc)는 안전장치라 바꿀 수 없다.
/// </summary>
/// <param name="LockKey">가장자리 넘기기 잠금 키(가상 키 코드). 0 이면 쓰지 않는다.</param>
/// <param name="JumpModifiers">PC 바로 이동(수정키 + 숫자 1~4)의 수정키. None 이면 쓰지 않는다. 정확히 이 수정키만 눌려야 한다.</param>
public sealed record HotkeySettings(ushort LockKey, HotkeyModifiers JumpModifiers)
{
    public const ushort VkPause = 0x13;

    public static readonly HotkeySettings Default = new(InputRouter.VkScrollLock, HotkeyModifiers.Ctrl | HotkeyModifiers.Alt);

    /// <summary>설정 화면에서 고를 수 있는 잠금 키.</summary>
    public static readonly IReadOnlyList<(ushort Key, string Name)> LockKeyChoices =
    [
        (InputRouter.VkScrollLock, "Scroll Lock"),
        (VkPause, "Pause"),
        (0, "쓰지 않음"),
    ];

    /// <summary>설정 화면에서 고를 수 있는 바로 이동 수정키.</summary>
    public static readonly IReadOnlyList<HotkeyModifiers> JumpModifierChoices =
    [
        HotkeyModifiers.Ctrl | HotkeyModifiers.Alt,
        HotkeyModifiers.Ctrl | HotkeyModifiers.Shift,
        HotkeyModifiers.Ctrl | HotkeyModifiers.Alt | HotkeyModifiers.Shift,
        HotkeyModifiers.None,
    ];

    public static string LockKeyName(ushort key) =>
        LockKeyChoices.FirstOrDefault(c => c.Key == key).Name ?? $"키 0x{key:X2}";

    public static string ModifiersName(HotkeyModifiers m)
    {
        if (m == HotkeyModifiers.None) return "쓰지 않음";
        var parts = new List<string>();
        if (m.HasFlag(HotkeyModifiers.Ctrl)) parts.Add("Ctrl");
        if (m.HasFlag(HotkeyModifiers.Alt)) parts.Add("Alt");
        if (m.HasFlag(HotkeyModifiers.Shift)) parts.Add("Shift");
        return string.Join("+", parts);
    }

    /// <summary>저장된 값이 고를 수 있는 값이 아니면 기본값으로 되돌린다.</summary>
    public HotkeySettings Normalize() => new(
        LockKeyChoices.Any(c => c.Key == LockKey) ? LockKey : Default.LockKey,
        JumpModifierChoices.Contains(JumpModifiers) ? JumpModifiers : Default.JumpModifiers);
}
