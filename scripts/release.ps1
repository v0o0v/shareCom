# ShareCom 릴리즈 만들기: 시험 -> 빌드 -> 서명 -> 명세 -> GitHub 릴리즈.
# 콘솔 출력은 코드 페이지와 무관하게 읽히도록 ASCII 만 쓴다. 한글은 주석에만 둔다.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File scripts\release.ps1 -Version 0.6.0 -NotesFile notes.md
#   -DryRun : GitHub 에 올리지 않고 만들기·서명·검증까지만 한다.
#
# 서명 키는 이 PC의 %APPDATA%\ShareCom-release\primary.key (DPAPI) 를 쓴다.
param(
    [Parameter(Mandatory = $true)][string]$Version,
    [Parameter(Mandatory = $true)][string]$NotesFile,
    [string]$Key = 'primary',
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

function Step($text) { Write-Host "[..] $text" }
function Ok($text) { Write-Host "[OK] $text" }
function Fail($text) { Write-Host "[FAIL] $text"; exit 1 }
function Run($exe, [string[]]$argv) {
    & $exe @argv
    if ($LASTEXITCODE -ne 0) { Fail "$exe $($argv -join ' ') (exit $LASTEXITCODE)" }
}

if ($Version -notmatch '^\d+\.\d+\.\d+$') { Fail "version must look like 0.6.0: $Version" }
$tag = "v$Version"
$notes = Resolve-Path $NotesFile -ErrorAction SilentlyContinue
if (-not $notes) { Fail "notes file not found: $NotesFile" }

# 1. 버전과 저장소 상태
Step 'checking version and git state'
$csproj = Get-Content 'src\ShareCom.App\ShareCom.App.csproj' -Raw
if ($csproj -notmatch "<Version>$([regex]::Escape($Version))</Version>") { Fail "ShareCom.App.csproj <Version> is not $Version" }
if ($DryRun) {
    Ok "version $Version (dry run: git state not checked)"
} else {
    $branch = (git rev-parse --abbrev-ref HEAD).Trim()
    if ($branch -ne 'main') { Fail "not on main (on $branch)" }
    if (git status --porcelain) { Fail 'working tree is not clean' }
    git fetch -q origin main
    if ((git rev-parse HEAD).Trim() -ne (git rev-parse origin/main).Trim()) { Fail 'main is not the same as origin/main' }
    if (git tag --list $tag) { Fail "tag $tag already exists" }
    & gh release view $tag *> $null
    if ($LASTEXITCODE -eq 0) { Fail "release $tag already exists" }
    Ok "version $Version, main is clean and pushed"
}

# 2. 시험
Step 'running tests'
Run 'dotnet' @('test', '--nologo', '-v', 'q')
Ok 'tests passed'

# 3. 빌드
$out = Join-Path $env:TEMP "sharecom-release-$Version"
if (Test-Path $out) { Remove-Item -Recurse -Force $out }
Step "publishing to $out"
Run 'dotnet' @('publish', 'src\ShareCom.App', '-c', 'Release', '-o', "$out\app", '--nologo', '-v', 'q')
Run 'dotnet' @('build', 'tools\ShareCom.ReleaseTool', '-c', 'Release', '-o', "$out\tool", '--nologo', '-v', 'q')
$exe = Join-Path $out 'app\ShareCom.exe'
$fileVersion = (Get-Item $exe).VersionInfo.ProductVersion
if (-not $fileVersion.StartsWith($Version)) { Fail "built exe version is $fileVersion" }
Ok "built ShareCom.exe $fileVersion"

# 4. 서명과 명세
$tool = Join-Path $out 'tool\ShareCom.ReleaseTool.exe'
$manifest = Join-Path $out 'app\ShareCom.update.json'
Run $tool @('sign', '--exe', $exe, '--version', $Version, '--out', $manifest, '--key', $Key)
Run $tool @('verify', '--manifest', $manifest, '--exe', $exe)
$hash = (Get-FileHash $exe -Algorithm SHA256).Hash
$shaFile = Join-Path $out 'app\ShareCom.exe.sha256'
[IO.File]::WriteAllText($shaFile, "$hash  ShareCom.exe`n", (New-Object Text.UTF8Encoding($false)))
Ok "sha256 $hash"

if ($DryRun) {
    Ok "dry run: files are in $out\app (not uploaded)"
    exit 0
}

# 5. GitHub 릴리즈(정식, 최신)
Step "creating GitHub release $tag"
Run 'gh' @('release', 'create', $tag, $exe, $shaFile, $manifest, '--title', "ShareCom $tag", '--notes-file', "$notes", '--latest', '--target', 'main')
Ok "released $tag"

# 6. 앱과 같은 방식으로 받아 본다
Step 'checking the release the way the app does'
Run $tool @('check', '--current', '0.0.1')
Ok 'done'
