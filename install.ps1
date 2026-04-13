$ErrorActionPreference = "Stop"
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path

$cert = Get-ChildItem "$ScriptDir\*.pfx" | Select-Object -First 1
$msix = Get-ChildItem "$ScriptDir\*.msix" | Select-Object -First 1

if (-not $cert) { Write-Host "No .pfx certificate found" -ForegroundColor Red; pause; exit 1 }
if (-not $msix) { Write-Host "No .msix package found" -ForegroundColor Red; pause; exit 1 }

Write-Host "`n  Buzzr Installer" -ForegroundColor Cyan
Write-Host "  Unofficial Windows client for Beeper`n"

$isAdmin = ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    Write-Host "  Restarting as Administrator..." -ForegroundColor Yellow
    Start-Process powershell.exe "-ExecutionPolicy Bypass -File `"$($MyInvocation.MyCommand.Path)`"" -Verb RunAs
    exit
}

Write-Host "  [1/4] Installing certificate..." -ForegroundColor White
$pw = ConvertTo-SecureString -String "Buzzr-Dev" -Force -AsPlainText
$pfxObj = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2($cert.FullName, $pw)
$store = New-Object System.Security.Cryptography.X509Certificates.X509Store("TrustedPeople", "LocalMachine")
$store.Open("ReadWrite")
$store.Add($pfxObj)
$store.Close()
Write-Host "  Done" -ForegroundColor Green

Write-Host "  [2/4] Signing package..." -ForegroundColor White
$signtool = $null
$sdkPaths = @(
    "${env:ProgramFiles(x86)}\Windows Kits\10\bin",
    "$env:USERPROFILE\.nuget\packages\microsoft.windows.sdk.buildtools"
)
foreach ($base in $sdkPaths) {
    if (Test-Path $base) {
        $found = Get-ChildItem -Path $base -Recurse -Filter "signtool.exe" -ErrorAction SilentlyContinue |
                 Where-Object { $_.FullName -match "x64" } |
                 Sort-Object FullName -Descending |
                 Select-Object -First 1
        if ($found) { $signtool = $found.FullName; break }
    }
}

if ($signtool) {
    & $signtool sign /fd SHA256 /a /f $cert.FullName /p "Buzzr-Dev" $msix.FullName 2>&1 | Out-Null
    if ($LASTEXITCODE -eq 0) {
        Write-Host "  Done" -ForegroundColor Green
    } else {
        Write-Host "  Signing failed, enabling Developer Mode instead..." -ForegroundColor Yellow
        Set-ItemProperty -Path "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\AppModelUnlock" -Name "AllowDevelopmentWithoutDevLicense" -Value 1 -Type DWord -Force
    }
} else {
    Write-Host "  signtool.exe not found, enabling Developer Mode..." -ForegroundColor Yellow
    Set-ItemProperty -Path "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\AppModelUnlock" -Name "AllowDevelopmentWithoutDevLicense" -Value 1 -Type DWord -Force
    Write-Host "  Done" -ForegroundColor Green
}

Write-Host "  [3/4] Installing Buzzr..." -ForegroundColor White
try {
    Add-AppxPackage -Path $msix.FullName -ForceApplicationShutdown -ForceUpdateFromAnyVersion
    Write-Host "  Done" -ForegroundColor Green
} catch {
    Write-Host "  Error: $($_.Exception.Message)" -ForegroundColor Red
    Write-Host ""
    Write-Host "  Troubleshooting:" -ForegroundColor Yellow
    Write-Host "    1. Open Settings > System > For developers" -ForegroundColor White
    Write-Host "    2. Enable 'Developer Mode'" -ForegroundColor White
    Write-Host "    3. Run this installer again" -ForegroundColor White
    Write-Host ""
    pause
    exit 1
}

Write-Host "  [4/4] Done!`n" -ForegroundColor Green
Write-Host "  Buzzr installed! Find it in the Start Menu.`n" -ForegroundColor Cyan
pause
