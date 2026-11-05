using ShareCom.Core.Input;
using ShareCom.Core.Protocol;

namespace ShareCom.Core.Tests;

public class KeyInjectionPlanTests
{
    // 이 PC(한국어 배열 Windows)에서 MapVirtualKey(코드, MAPVK_VSC_TO_VK_EX) 로 잰 값
    private static uint Map(uint code) => code switch
    {
        0x2A => 0xA0,   // 왼쪽 Shift
        0x36 => 0xA1,   // 오른쪽 Shift
        0xE036 => 0,    // 확장 + 오른쪽 Shift 자리: 어떤 키도 아니다
        0x34 => 0xBE,   // .
        0xE04B => 0x25, // 왼쪽 화살표
        0x4B => 0x64,   // 숫자 패드 4
        _ => 0,
    };

    [Fact]
    public void 확장_표시가_붙은_오른쪽_Shift_는_표시를_떼고_스캔_코드로_누른다()
    {
        var p = KeyInjectionPlan.For(new KeyMessage(0xA1, 0x36, true, true), Map, false);
        Assert.Equal(new KeyInjectionPlan(true, 0x36, false, 0), p);
    }

    [Fact]
    public void 오른쪽_Shift_가_확장_표시_없이_오면_그대로_누른다()
    {
        var p = KeyInjectionPlan.For(new KeyMessage(0xA1, 0x36, false, true), Map, false);
        Assert.Equal(new KeyInjectionPlan(true, 0x36, false, 0), p);
    }

    [Fact]
    public void 화살표처럼_확장_표시로_바뀌는_키는_표시를_지킨다()
    {
        // 확장 표시를 떼면 숫자 패드 4 가 되므로 떼면 안 된다
        var p = KeyInjectionPlan.For(new KeyMessage(0x25, 0x4B, true, true), Map, false);
        Assert.Equal(new KeyInjectionPlan(true, 0x4B, true, 0), p);
    }

    [Fact]
    public void 보통_키는_스캔_코드로_누른다()
    {
        var p = KeyInjectionPlan.For(new KeyMessage(0xBE, 0x34, false, true), Map, false);
        Assert.Equal(new KeyInjectionPlan(true, 0x34, false, 0), p);
    }

    [Fact]
    public void 스캔_코드가_어떤_키로도_안_바뀌면_가상_키로_누른다()
    {
        var p = KeyInjectionPlan.For(new KeyMessage(0xB3, 0x22, true, true), Map, false);
        Assert.False(p.ByScanCode);
        Assert.Equal(0xB3, p.VirtualKey);
    }

    [Fact]
    public void 스캔_코드가_없거나_가상_키_전용이면_가상_키로_누른다()
    {
        Assert.False(KeyInjectionPlan.For(new KeyMessage(0x41, 0, false, true), Map, false).ByScanCode);
        Assert.False(KeyInjectionPlan.For(new KeyMessage(0x15, 0x38, true, true), Map, true).ByScanCode);
    }
}
