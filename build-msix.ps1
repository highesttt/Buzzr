# .\build-msix.ps1 [-Version "1.0.0.0"] [-Platform "x64"] [-Configuration "Release"] [-SkipSign]

param(
    [string]$Version = "1.0.0.0",
    [string]$Platform = "x64",
    [string]$Configuration = "Release",
    [switch]$SkipSign
)

$ErrorActionPreference = "Stop"
$Root = $PSScriptRoot
$Proj = Join-Path $Root "BeeperWinUI\BeeperWinUI.csproj"
$Out = Join-Path $Root "release"
$CertDir = Join-Path $Root ".certs"
$CertPath = Join-Path $CertDir "BeeperWinUI.pfx"
$CertPw = "BeeperWinUI-Dev"
$CertSubject = "CN=BeeperWinUI"

function Step($msg) { Write-Host "`n>> $msg" -ForegroundColor Cyan }
function Ok($msg)   { Write-Host "   $msg" -ForegroundColor Green }
function Warn($msg) { Write-Host "   $msg" -ForegroundColor Yellow }

Step "Preparing release directory"
if (Test-Path $Out) { Remove-Item $Out -Recurse -Force }
New-Item -ItemType Directory -Path $Out -Force | Out-Null

# certificate for signing (required for msix, optional for portable)
if (-not $SkipSign) {
    Step "Code signing certificate"
    if (-not (Test-Path $CertDir)) { New-Item -ItemType Directory -Path $CertDir -Force | Out-Null }

    if (Test-Path $CertPath) {
        Ok "Exists: $CertPath"
    } else {
        Warn "Creating self-signed cert..."
        $cert = New-SelfSignedCertificate `
            -Type Custom -Subject $CertSubject -KeyUsage DigitalSignature `
            -FriendlyName "BeeperWinUI Development" `
            -CertStoreLocation "Cert:\CurrentUser\My" `
            -TextExtension @("2.5.29.37={text}1.3.6.1.5.5.7.3.3", "2.5.29.19={text}")

        $pw = ConvertTo-SecureString -String $CertPw -Force -AsPlainText
        Export-PfxCertificate -Cert "Cert:\CurrentUser\My\$($cert.Thumbprint)" -FilePath $CertPath -Password $pw | Out-Null
        Ok "Created: $CertPath"

        # trust locally so msix installs without prompts
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

# placeholder assets
Step "Visual assets"
$assetsDir = Join-Path $Root "BeeperWinUI\Assets"
if (-not (Test-Path $assetsDir)) { New-Item -ItemType Directory -Path $assetsDir -Force | Out-Null }

$png = [Convert]::FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVQIHWNgYPj/HwADBwF/OMBo3AAAAABJRU5ErkJggg==")
@("Square44x44Logo.png","Square71x71Logo.png","Square150x150Logo.png","Square310x310Logo.png",
  "Wide310x150Logo.png","SplashScreen.png","StoreLogo.png","LockScreenLogo.png") | ForEach-Object {
    $p = Join-Path $assetsDir $_
    if (-not (Test-Path $p)) { [IO.File]::WriteAllBytes($p, $png); Ok "Created: $_" }
}

# msix
Step "Building MSIX ($Platform $Configuration)"
$msixPub = Join-Path $Out "msix-publish"
& dotnet publish $Proj --configuration $Configuration --runtime "win-$Platform" --self-contained true --output $msixPub `
    /p:Platform=$Platform /p:WindowsPackageType=MSIX /p:AppxPackageDir="$Out\msix\" `
    /p:AppxBundle=Never /p:AppxPackageSigningEnabled=false /p:GenerateAppxPackageOnBuild=true /p:Version=$Version

if ($LASTEXITCODE -ne 0) { Write-Host "MSIX build failed" -ForegroundColor Red; exit 1 }

$msixFile = Get-ChildItem -Path $Out -Filter "*.msix" -Recurse -ErrorAction SilentlyContinue | Select-Object -First 1
if ($msixFile) { Ok "Built: $($msixFile.FullName)" }
else { Warn "MSIX not found in output" }

# sign
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

    if ($signtool) {
        & $signtool sign /fd SHA256 /a /f $CertPath /p $CertPw $msixFile.FullName
        if ($LASTEXITCODE -eq 0) { Ok "Signed" } else { Warn "Signing failed ($LASTEXITCODE)" }
    } else {
        Warn "signtool.exe not found, install Windows SDK"
    }

    $final = Join-Path $Out "Beeper-$Version-$Platform.msix"
    Copy-Item $msixFile.FullName $final -Force
    Ok "Output: $final"
}

# build portable zip
Step "Building portable zip ($Platform $Configuration)"
$portDir = Join-Path $Out "portable"
& dotnet publish $Proj --configuration $Configuration --runtime "win-$Platform" --self-contained true --output $portDir `
    /p:Platform=$Platform /p:WindowsPackageType=None /p:PublishSingleFile=false /p:Version=$Version

if ($LASTEXITCODE -ne 0) { Write-Host "Portable build failed" -ForegroundColor Red; exit 1 }

$zip = Join-Path $Out "Beeper-$Version-$Platform-portable.zip"
Compress-Archive -Path "$portDir\*" -DestinationPath $zip -Force
Ok "Output: $zip"

Step "Done"
Write-Host ""
Get-ChildItem $Out -File | ForEach-Object {
    Write-Host "   $($_.Name)  ($([math]::Round($_.Length / 1MB, 2)) MB)" -ForegroundColor White
}
Write-Host "`nOutput: $Out" -ForegroundColor Cyan
