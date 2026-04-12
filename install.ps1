# Buzzr Installer
# Right-click → Run with PowerShell (requires Admin for cert trust)

$ErrorActionPreference = "Stop"
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path

$cert = Get-ChildItem "$ScriptDir\*.pfx" | Select-Object -First 1
$msix = Get-ChildItem "$ScriptDir\*.msix" | Select-Object -First 1

if (-not $cert) { Write-Host "No .pfx certificate found" -ForegroundColor Red; pause; exit 1 }
if (-not $msix) { Write-Host "No .msix package found" -ForegroundColor Red; pause; exit 1 }

Write-Host "`n  Buzzr Installer" -ForegroundColor Cyan
Write-Host "  Unofficial Windows client for Beeper`n"

# check admin
$isAdmin = ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    Write-Host "  Restarting as Administrator..." -ForegroundColor Yellow
    Start-Process powershell.exe "-ExecutionPolicy Bypass -File `"$($MyInvocation.MyCommand.Path)`"" -Verb RunAs
    exit
}

Write-Host "  [1/3] Installing certificate..." -ForegroundColor White
$pw = ConvertTo-SecureString -String "Buzzr-Dev" -Force -AsPlainText
$pfx = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2($cert.FullName, $pw)
$store = New-Object System.Security.Cryptography.X509Certificates.X509Store("TrustedPeople", "LocalMachine")
$store.Open("ReadWrite")
$store.Add($pfx)
$store.Close()
Write-Host "  Done" -ForegroundColor Green

Write-Host "  [2/3] Installing Buzzr..." -ForegroundColor White
Add-AppxPackage -Path $msix.FullName
Write-Host "  Done" -ForegroundColor Green

Write-Host "  [3/3] Cleaning up..." -ForegroundColor White
Write-Host "  Done" -ForegroundColor Green

Write-Host "`n  Buzzr installed! Find it in the Start Menu.`n" -ForegroundColor Cyan
pause
