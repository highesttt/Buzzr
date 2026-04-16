# Usage: .\build-msi.ps1 [-Version "0.2.0.0"] [-Platform "x64"] [-SourceDir "release\portable"] [-SkipBuild]

param(
    [string]$Version = "0.2.0.0",
    [string]$Platform = "x64",
    [string]$SourceDir = "",
    [switch]$SkipBuild
)

$ErrorActionPreference = "Stop"
$Root = Split-Path -Parent $MyInvocation.MyCommand.Path
$Out = Join-Path $Root "release"

function Step($msg) { Write-Host "`n>> $msg" -ForegroundColor Cyan }
function Ok($msg)   { Write-Host "   $msg" -ForegroundColor Green }
function Warn($msg) { Write-Host "   $msg" -ForegroundColor Yellow }
function Fail($msg) { Write-Host "   $msg" -ForegroundColor Red; exit 1 }

Step "Checking WiX toolset"
$wixCmd = Get-Command wix -ErrorAction SilentlyContinue
if (-not $wixCmd) {
    Warn "WiX CLI not found, installing via dotnet tool"
    dotnet tool install --global wix
    if ($LASTEXITCODE -ne 0) { Fail "Failed to install WiX. Run manually: dotnet tool install --global wix" }

    $env:PATH = [System.Environment]::GetEnvironmentVariable("PATH", "User") + ";" + $env:PATH
    $wixCmd = Get-Command wix -ErrorAction SilentlyContinue
    if (-not $wixCmd) { Fail "WiX installed but not found in PATH. Restart your terminal and try again." }
    Ok "WiX installed"
} else {
    Ok "WiX found: $($wixCmd.Source)"
}

wix extension add WixToolset.UI.wixext 2>$null

if (-not $SourceDir) {
    $SourceDir = Join-Path $Out "portable"
}

if (-not $SkipBuild -or -not (Test-Path (Join-Path $SourceDir "Buzzr.exe"))) {
    Step "Building portable publish ($Platform, v$Version)"

    $sidecarDir = Join-Path $Root "sidecar"
    $sidecarExe = Join-Path $sidecarDir "buzzr-sidecar.exe"
    if (-not (Test-Path $sidecarExe)) {
        Step "Building sidecar"
        Push-Location $sidecarDir
        $env:CGO_ENABLED = "1"
        go build -tags goolm -ldflags "-s -w" -o buzzr-sidecar.exe .
        if ($LASTEXITCODE -ne 0) { Pop-Location; Fail "Sidecar build failed" }
        Pop-Location
        Ok "Sidecar built"
    }

    Step "Building native taskbar badge DLL"
    $nativeDir = Join-Path $Root "Buzzr\Native"
    $nativeDll = Join-Path $nativeDir "taskbar_badge.dll"
    $gccPath = "E:\Programs\msys64\ucrt64\bin\gcc.exe"
    Push-Location $nativeDir
    & $gccPath -shared -O2 -o taskbar_badge.dll taskbar_badge.c -lole32 -luuid -lgdi32 -luser32 -lm
    Pop-Location
    if ($LASTEXITCODE -ne 0) {
        Warn "Native DLL build failed (badge notifications will be disabled)"
    } elseif (Test-Path $nativeDll) {
        $dllSize = (Get-Item $nativeDll).Length / 1KB
        Ok "Built taskbar_badge.dll ($([math]::Round($dllSize, 1)) KB)"
    }

    $proj = Join-Path $Root "Buzzr\Buzzr.csproj"
    dotnet publish $proj `
        --configuration Release `
        --runtime "win-$Platform" `
        --self-contained true `
        --output $SourceDir `
        -p:Platform=$Platform `
        -p:WindowsPackageType=None `
        -p:PublishSingleFile=false `
        -p:Version=$Version
    if ($LASTEXITCODE -ne 0) { Fail "Portable build failed" }
    Ok "Portable build ready: $SourceDir"
}

if (-not (Test-Path (Join-Path $SourceDir "Buzzr.exe"))) {
    Fail "Buzzr.exe not found in $SourceDir. Build portable first or specify -SourceDir"
}

Step "Generating License.rtf"
$msiDir = Join-Path $Root "msi"
$licenseRtf = Join-Path $msiDir "License.rtf"
$licensePath = Join-Path $Root "LICENSE"

if (-not (Test-Path $licensePath)) { Fail "LICENSE file not found at $licensePath" }

$licenseText = Get-Content $licensePath -Raw
$escaped = $licenseText `
    -replace '\\', '\\' `
    -replace '\{', '\{' `
    -replace '\}', '\}' `
    -replace "`r`n", "\par`r`n" `
    -replace "(?<!`r)`n", "\par`n"
$rtf = "{\rtf1\ansi\deff0{\fonttbl{\f0\fswiss Segoe UI;}}{\colortbl;\red0\green0\blue0;}`r`n\viewkind4\uc1\pard\f0\fs18 $escaped\par}"
Set-Content -Path $licenseRtf -Value $rtf -Encoding ASCII
Ok "License.rtf generated"

$arch = switch ($Platform) {
    "x64"   { "x64" }
    "x86"   { "x86" }
    "arm64" { "arm64" }
    default { "x64" }
}

Step "Building MSI ($Platform, v$Version)"
$wxs      = Join-Path $msiDir "Buzzr.wxs"
$iconPath = Join-Path $Root "Buzzr\Assets\buzzr.ico"
$msiOut   = Join-Path $Out "Buzzr-$Version-$Platform.msi"

if (-not (Test-Path $iconPath)) { Fail "Icon not found: $iconPath" }
if (-not (Test-Path $wxs))      { Fail "WiX source not found: $wxs" }

New-Item -ItemType Directory -Path $Out -Force | Out-Null

wix build $wxs `
    -ext WixToolset.UI.wixext `
    -d SourceDir="$SourceDir" `
    -d Version="$Version" `
    -d IconPath="$iconPath" `
    -d LicenseRtf="$licenseRtf" `
    -arch $arch `
    -o $msiOut

if ($LASTEXITCODE -ne 0) { Fail "MSI build failed" }

$size = (Get-Item $msiOut).Length / 1MB
Ok ("MSI created: $msiOut ({0:N1} MB)" -f $size)

Step "Done"
Write-Host ""
Get-ChildItem $Out -Filter "*.msi" | ForEach-Object {
    Write-Host ("   {0:N1} MB  {1}" -f ($_.Length / 1MB), $_.Name) -ForegroundColor White
}
Write-Host "`nOutput: $Out" -ForegroundColor Cyan
