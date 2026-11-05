namespace ShareCom.Core.Update;

/// <summary>
/// 업데이트 서명을 확인하는 공개 키. 개인 키는 릴리즈를 만드는 PC에만 있다(tools/ShareCom.ReleaseTool).
/// 평소에는 primary 로 서명하고, primary 를 잃거나 새면 spare 로 서명한 업데이트에서 이 목록을 바꾼다.
/// </summary>
public static class UpdateKeys
{
    public static readonly IReadOnlyList<UpdateKey> All =
    [
        new("primary", "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEp9ErstULIRYvIPuhRVIZ9VAikPcbzAvZ9j0WDL/NB0M+XsBgfzY1mo60+8MK9h5d0z33SZ30bdx3Ay/n8fhtcA=="),
        new("spare", "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEBhuq0qlLrBarM+11bGyoQmmEw2aRPnTSbxV+fV+7Iob19yD1AkEFiigVX8w6QZI3UPIcGbdzzETiuJMlHi4a4g=="),
    ];
}
