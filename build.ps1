# Builds the app in Debug and launches it. Handy while developing.
$ErrorActionPreference = "Stop"
$dotnet = "$env:ProgramFiles\dotnet\dotnet.exe"
if (-not (Test-Path $dotnet)) { $dotnet = "dotnet" }

$proj = Join-Path $PSScriptRoot "src\RobloxAccountManager.csproj"

Write-Host "Building (Debug)..." -ForegroundColor Cyan
& $dotnet build $proj -c Debug
if ($LASTEXITCODE -ne 0) { Write-Host "Build failed." -ForegroundColor Red; exit 1 }

$exe = Join-Path $PSScriptRoot "src\bin\Debug\net8.0-windows\Roblox Account Manager.exe"
Write-Host "Launching $exe" -ForegroundColor Green
Start-Process $exe
