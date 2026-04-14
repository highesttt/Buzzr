# .\build-msix.ps1 [-Version "1.0.0.0"] [-Platform "x64"] [-Configuration "Release"] [-SkipSign]

param(
    [string]$Version = "1.0.0.0",
    [string]$Platform = "x64",
    [string]$Configuration = "Release",
    [switch]$SkipSign
)

$ErrorActionPreference = "Stop"
$Root = $PSScriptRoot
$Proj = Join-Path $Root "Buzzr\Buzzr.csproj"
$Out = Join-Path $Root "release"
$CertDir = Join-Path $Root ".certs"
$CertPath = Join-Path $CertDir "Buzzr.pfx"
$CertPw = "Buzzr-Dev"
$CertSubject = "CN=dev.highest.buzzr"

function Step($msg) { Write-Host "`n>> $msg" -ForegroundColor Cyan }
function Ok($msg)   { Write-Host "   $msg" -ForegroundColor Green }
function Warn($msg) { Write-Host "   $msg" -ForegroundColor Yellow }

Step "Preparing release directory"
if (Test-Path $Out) { Remove-Item $Out -Recurse -Force }
New-Item -ItemType Directory -Path $Out -Force | Out-Null

if (-not $SkipSign) {
    Step "Code signing certificate"
    if (-not (Test-Path $CertDir)) { New-Item -ItemType Directory -Path $CertDir -Force | Out-Null }

    if (Test-Path $CertPath) {
        Ok "Exists: $CertPath"
    } else {
        Warn "Creating self-signed cert..."
        $cert = New-SelfSignedCertificate `
            -Type Custom -Subject $CertSubject -KeyUsage DigitalSignature `
            -FriendlyName "Buzzr Development" `
            -CertStoreLocation "Cert:\CurrentUser\My" `
            -TextExtension @("2.5.29.37={text}1.3.6.1.5.5.7.3.3", "2.5.29.19={text}")

        $pw = ConvertTo-SecureString -String $CertPw -Force -AsPlainText
        Export-PfxCertificate -Cert "Cert:\CurrentUser\My\$($cert.Thumbprint)" -FilePath $CertPath -Password $pw | Out-Null
        Ok "Created: $CertPath"

        try {
            $pfx = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2($CertPath, $CertPw)
            $store = New-Object System.Security.Cryptography.X509Certificates.X509Store(
                [System.Security.Cryptography.X509Certificates.StoreName]::TrustedPeople,
                [System.Security.Cryptography.X509Certificates.StoreLocation]::LocalMachine)
            $store.Open([System.Security.Cryptography.X509Certificates.OpenFlags]::ReadWrite)
            $store.Add($pfx)
            $store.Close()
            Ok "Added to Trusted People store"
        } catch {
            Warn "Couldn't trust cert locally (run as Admin)"
        }
    }
}

Step "Building Go sidecar"
$sidecarDir = Join-Path $Root "sidecar"
$sidecarExe = Join-Path $sidecarDir "buzzr-sidecar.exe"
$savedCC = $env:CC
$savedCGO = $env:CGO_ENABLED
try {
    $env:CC = "E:\Programs\msys64\ucrt64\bin\gcc.exe"
    $env:CGO_ENABLED = "1"
    Push-Location $sidecarDir
    & go build -tags goolm -ldflags "-s -w" -o buzzr-sidecar.exe .
    Pop-Location
    if ($LASTEXITCODE -ne 0) {
        Warn "Sidecar build failed (continuing without it)"
    } elseif (Test-Path $sidecarExe) {
        $sidecarSize = (Get-Item $sidecarExe).Length / 1MB
        Ok "Built buzzr-sidecar.exe ($([math]::Round($sidecarSize, 1)) MB)"
    }
} finally {
    $env:CC = $savedCC
    $env:CGO_ENABLED = $savedCGO
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

Step "Visual assets"
$assetsDir = Join-Path $Root "Buzzr\Assets"
if (-not (Test-Path $assetsDir)) { New-Item -ItemType Directory -Path $assetsDir -Force | Out-Null }

$png = [Convert]::FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVQIHWNgYPj/HwADBwF/OMBo3AAAAABJRU5ErkJggg==")
@("Square44x44Logo.png","Square71x71Logo.png","Square150x150Logo.png","Square310x310Logo.png",
  "Wide310x150Logo.png","SplashScreen.png","StoreLogo.png","LockScreenLogo.png") | ForEach-Object {
    $p = Join-Path $assetsDir $_
    if (-not (Test-Path $p)) { [IO.File]::WriteAllBytes($p, $png); Ok "Created: $_" }
}

Step "Building MSIX ($Platform $Configuration)"
$msixPub = Join-Path $Out "msix-publish"
& dotnet publish $Proj --configuration $Configuration --runtime "win-$Platform" --self-contained true --output $msixPub `
    /p:Platform=$Platform /p:WindowsPackageType=MSIX /p:AppxPackageDir="$Out\msix\" `
    /p:AppxBundle=Never /p:AppxPackageSigningEnabled=false /p:GenerateAppxPackageOnBuild=true /p:Version=$Version

if ($LASTEXITCODE -ne 0) { Write-Host "MSIX build failed" -ForegroundColor Red; exit 1 }

$msixFile = Get-ChildItem -Path $Out -Filter "*.msix" -Recurse -ErrorAction SilentlyContinue | Select-Object -First 1
if ($msixFile) { Ok "Built: $($msixFile.FullName)" }
else { Warn "MSIX not found in output" }

if ($msixFile -and -not $SkipSign) {
    Step "Signing MSIX"
    $signtool = $null
    $sdkRoot = "${env:ProgramFiles(x86)}\Windows Kits\10\bin"
    if (Test-Path $sdkRoot) {
        Get-ChildItem $sdkRoot -Directory | Sort-Object Name -Descending | ForEach-Object {
            if (-not $signtool) {
                $c = Join-Path $_.FullName "x64\signtool.exe"
                if (Test-Path $c) { $signtool = $c }
            }
        }
    }

    # also check NuGet cache for signtool
    if (-not $signtool) {
        $nugetPath = Join-Path $env:USERPROFILE ".nuget\packages\microsoft.windows.sdk.buildtools"
        if (Test-Path $nugetPath) {
            $signtool = Get-ChildItem $nugetPath -Recurse -Filter "signtool.exe" |
                Where-Object { $_.FullName -like "*x64*" } |
                Sort-Object FullName -Descending |
                Select-Object -First 1 -ExpandProperty FullName
        }
    }

    if ($signtool) {
        & $signtool sign /fd SHA256 /a /f $CertPath /p $CertPw $msixFile.FullName
        if ($LASTEXITCODE -eq 0) { Ok "Signed" } else { Warn "Signing failed ($LASTEXITCODE)" }
    } else {
        Warn "signtool.exe not found (install Windows SDK or restore NuGet packages)"
    }

    $final = Join-Path $Out "Buzzr-$Version-$Platform.msix"
    Copy-Item $msixFile.FullName $final -Force
    Ok "Output: $final"
}

# bundle installer (msix + cert + install script)
if ($msixFile -and (Test-Path $CertPath)) {
    Step "Creating installer bundle"
    $bundleDir = Join-Path $Out "Buzzr-$Version-$Platform-installer"
    New-Item -ItemType Directory -Path $bundleDir -Force | Out-Null
    Copy-Item (Join-Path $Out "Buzzr-$Version-$Platform.msix") $bundleDir -ErrorAction SilentlyContinue
    if (-not (Test-Path (Join-Path $bundleDir "*.msix"))) {
        Copy-Item $msixFile.FullName (Join-Path $bundleDir "Buzzr-$Version-$Platform.msix")
    }
    Copy-Item $CertPath $bundleDir
    Copy-Item (Join-Path $Root "install.ps1") $bundleDir
    $bundleZip = Join-Path $Out "Buzzr-$Version-$Platform-installer.zip"
    Compress-Archive -Path "$bundleDir\*" -DestinationPath $bundleZip -Force
    Remove-Item $bundleDir -Recurse -Force
    Ok "Installer: $bundleZip"
}

Step "Building portable zip ($Platform $Configuration)"
$portDir = Join-Path $Out "portable"
& dotnet publish $Proj --configuration $Configuration --runtime "win-$Platform" --self-contained true --output $portDir `
    /p:Platform=$Platform /p:WindowsPackageType=None /p:PublishSingleFile=false /p:Version=$Version

if ($LASTEXITCODE -ne 0) { Write-Host "Portable build failed" -ForegroundColor Red; exit 1 }

$zip = Join-Path $Out "Buzzr-$Version-$Platform-portable.zip"
Compress-Archive -Path "$portDir\*" -DestinationPath $zip -Force
Ok "Output: $zip"

Step "Done"
Write-Host ""
Get-ChildItem $Out -File | ForEach-Object {
    Write-Host "   $($_.Name)  ($([math]::Round($_.Length / 1MB, 2)) MB)" -ForegroundColor White
}
Write-Host "`nOutput: $Out" -ForegroundColor Cyan
