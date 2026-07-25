#!/usr/bin/env pwsh
# Build and publish AquariumSaver on Windows
$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

Write-Host "=== AquariumSaver Build ===" -ForegroundColor Cyan

Write-Host "[1/2] Publishing for win-x64 (self-contained)..." -ForegroundColor Yellow
$publishDir = Join-Path $PSScriptRoot "publish"
if (Test-Path $publishDir) { Remove-Item -Recurse -Force $publishDir }

dotnet publish AquariumSaver.csproj `
  -c Release `
  -r win-x64 `
  --self-contained true `
  -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:PublishReadyToRun=true `
  -o $publishDir `
  --verbosity quiet

Write-Host "[2/2] Renaming to .scr..." -ForegroundColor Yellow
$exe = Join-Path $publishDir "AquariumSaver.exe"
$scr = Join-Path $publishDir "AquariumSaver.scr"

if (Test-Path $exe) {
    Rename-Item $exe "AquariumSaver.scr"
    Write-Host "  Created: $scr" -ForegroundColor Green
} else {
    Write-Host "  ERROR: AquariumSaver.exe not found." -ForegroundColor Red
    exit 1
}

Write-Host "=== Build complete ===" -ForegroundColor Cyan
Write-Host "Screensaver: $scr"
