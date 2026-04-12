# Build script for Beeper Sidecar
# Requires: Go 1.22+, GCC (MinGW-w64 ucrt64)

param(
    [switch]$Debug,
    [switch]$Run,
    [int]$Port = 29110
)

$ErrorActionPreference = "Stop"

# Use MinGW UCRT64 GCC for CGO (SQLite)
$env:CC = "E:\Programs\msys64\ucrt64\bin\gcc.exe"
$env:CGO_ENABLED = "1"

Write-Host "Building Beeper Sidecar..." -ForegroundColor Cyan

# Build with goolm tag (pure Go Olm, no libolm C dependency)
go build -tags goolm -o beeper-sidecar.exe .

if ($LASTEXITCODE -ne 0) {
    Write-Host "Build failed!" -ForegroundColor Red
    exit 1
}

$size = (Get-Item beeper-sidecar.exe).Length / 1MB
Write-Host "Built beeper-sidecar.exe ($([math]::Round($size, 1)) MB)" -ForegroundColor Green

if ($Run) {
    $args = @("-port", $Port)
    if ($Debug) {
        $args += "-debug"
    }
    Write-Host "Starting sidecar on port $Port..." -ForegroundColor Yellow
    & .\beeper-sidecar.exe @args
}
