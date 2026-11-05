using System.Runtime.InteropServices;

namespace ShareCom.App.Infra;

/// <summary>
/// ShareCom 은 관리자 권한(높은 무결성 수준)으로 돌고 탐색기는 보통 권한(중간 수준)으로 돈다.
/// Windows 는 기본적으로 낮은 수준 프로세스가 높은 수준 프로세스의 COM 객체를 부르지 못하게 막아서,
/// 탐색기가 우리 가상 파일(클립보드 객체)의 내용을 요청할 수 없게 된다.
/// 그래서 시작할 때 '이 PC에 로그인한 사용자의 중간 수준 이상 프로세스'까지만 부를 수 있게 연다(낮은 수준은 계속 막힌다).
/// COM 마샬링이 일어나기 전(Main 맨 앞)에 한 번만 불러야 한다.
/// </summary>
internal static class ComSecurity
{
    // DACL: 로그인한 사용자(IU)·SYSTEM·관리자에게 COM 실행 권한(로컬). SACL: 무결성 하한 = 중간(ME).
    private const string Sddl = "O:BAG:BAD:(A;;0x3;;;IU)(A;;0x3;;;SY)(A;;0x3;;;BA)S:(ML;;NX;;;ME)";

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptor(string sddl, uint revision, out IntPtr sd, out uint size);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool MakeAbsoluteSD(IntPtr selfRelative, IntPtr absolute, ref uint absoluteSize,
        IntPtr dacl, ref uint daclSize, IntPtr sacl, ref uint saclSize, IntPtr owner, ref uint ownerSize, IntPtr group, ref uint groupSize);

    [DllImport("ole32.dll")]
    private static extern int CoInitializeSecurity(IntPtr pSecDesc, int cAuthSvc, IntPtr asAuthSvc, IntPtr pReserved1,
        uint dwAuthnLevel, uint dwImpLevel, IntPtr pAuthList, uint dwCapabilities, IntPtr pReserved3);

    private const uint RPC_C_AUTHN_LEVEL_DEFAULT = 0, RPC_C_IMP_LEVEL_IDENTIFY = 2, EOAC_NONE = 0;

    public static void AllowMediumIntegrityCallers()
    {
        if (!ConvertStringSecurityDescriptorToSecurityDescriptor(Sddl, 1, out var relative, out _))
        {
            Log.Error($"COM 보안 설명자 만들기 실패 (오류 {Marshal.GetLastWin32Error()})");
            return;
        }
        // CoInitializeSecurity 는 '절대 형식' 설명자만 받는다. 크기를 먼저 묻고 나눠 담는다.
        uint sdSize = 0, daclSize = 0, saclSize = 0, ownerSize = 0, groupSize = 0;
        MakeAbsoluteSD(relative, IntPtr.Zero, ref sdSize, IntPtr.Zero, ref daclSize, IntPtr.Zero, ref saclSize,
            IntPtr.Zero, ref ownerSize, IntPtr.Zero, ref groupSize);
        // 아래 메모리는 프로세스가 끝날 때까지 COM 이 쓰므로 풀지 않는다.
        IntPtr sd = Alloc(sdSize), dacl = Alloc(daclSize), sacl = Alloc(saclSize), owner = Alloc(ownerSize), group = Alloc(groupSize);
        if (!MakeAbsoluteSD(relative, sd, ref sdSize, dacl, ref daclSize, sacl, ref saclSize, owner, ref ownerSize, group, ref groupSize))
        {
            Log.Error($"COM 보안 설명자 변환 실패 (오류 {Marshal.GetLastWin32Error()})");
            return;
        }
        int hr = CoInitializeSecurity(sd, -1, IntPtr.Zero, IntPtr.Zero, RPC_C_AUTHN_LEVEL_DEFAULT, RPC_C_IMP_LEVEL_IDENTIFY, IntPtr.Zero, EOAC_NONE, IntPtr.Zero);
        if (hr != 0) Log.Error($"COM 보안 설정 실패 (0x{hr:X8}) - 관리자 권한일 때 탐색기 붙여넣기가 안 될 수 있습니다");
    }

    private static IntPtr Alloc(uint size) => size == 0 ? IntPtr.Zero : Marshal.AllocHGlobal((int)size);
}
