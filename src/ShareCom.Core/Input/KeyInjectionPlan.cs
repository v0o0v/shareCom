using ShareCom.Core.Protocol;

namespace ShareCom.Core.Input;

/// <summary>받은 키 하나를 이 PC에서 어떻게 누를지: 스캔 코드로(확장 표시 포함 여부) 또는 가상 키로.</summary>
public readonly record struct KeyInjectionPlan(bool ByScanCode, ushort ScanCode, bool Extended, ushort VirtualKey)
{
    /// <summary>
    /// 스캔 코드로 누르는 것이 기본이다(키보드 배열이 달라도 같은 자리의 키가 눌린다).
    /// 스캔 코드가 이 PC에서 아무 키로도 바뀌지 않으면 그대로 보내도 Windows 가 버린다.
    /// - 오른쪽 Shift 는 낮은 수준 훅에 확장 표시가 붙어 오는데, 확장 + 0x36 은 어떤 키도 아니다.
    ///   그래서 오른쪽 Shift 가 빠져 Shift+. 이 . 으로 들어갔다. 확장 표시를 떼면 오른쪽 Shift 가 된다.
    /// - 그래도 안 바뀌면 가상 키로 누른다.
    /// </summary>
    /// <param name="scanToVk">스캔 코드(확장 키는 0xE0 을 앞에 붙인 값)를 가상 키로 바꾼다. 없으면 0.</param>
    /// <param name="virtualKeyOnly">스캔 코드로 누르면 잘못되는 키(Pause, NumLock, 한/영, 한자).</param>
    public static KeyInjectionPlan For(KeyMessage k, Func<uint, uint> scanToVk, bool virtualKeyOnly)
    {
        if (k.ScanCode == 0 || virtualKeyOnly) return ByVirtualKey(k);
        if (scanToVk(Code(k.ScanCode, k.Extended)) != 0) return new(true, k.ScanCode, k.Extended, 0);
        if (k.Extended && scanToVk(k.ScanCode) != 0) return new(true, k.ScanCode, false, 0);
        return ByVirtualKey(k);
    }

    private static KeyInjectionPlan ByVirtualKey(KeyMessage k) => new(false, k.ScanCode, k.Extended, k.VirtualKey);

    private static uint Code(ushort scanCode, bool extended) => extended ? 0xE000u | scanCode : scanCode;
}
