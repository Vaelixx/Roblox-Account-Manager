# =====================================================================
#  Builds a single self-contained .exe you can copy anywhere and run.
#  No .NET install needed on the target machine.
#
#  Usage:   right-click -> Run with PowerShell
#      or:  powershell -ExecutionPolicy Bypass -File publish.ps1
#
#  Output:  <this folder>\dist\Roblox Account Manager.exe
# =====================================================================
$ErrorActionPreference = "Stop"

$dotnet = "$env:ProgramFiles\dotnet\dotnet.exe"
if (-not (Test-Path $dotnet)) { $dotnet = "dotnet" }

$proj = Join-Path $PSScriptRoot "src\RobloxAccountManager.csproj"
$out  = Join-Path $PSScriptRoot "dist"

# A running instance locks "Roblox Account Manager.exe" and makes the build fail with
# "Access to the path ... denied". Stop any instance that runs from our dist\ folder first.
$running = Get-Process -Name "Roblox Account Manager" -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -and $_.Path.StartsWith($out, [System.StringComparison]::OrdinalIgnoreCase) }
if ($running) {
    Write-Host "Stopping running instance (locks the exe)..." -ForegroundColor Yellow
    $running | Stop-Process -Force
    Start-Sleep -Milliseconds 500
}

# Clean previous build output but NEVER touch the user's "data" folder (accounts/settings live there).
if (Test-Path $out) {
    Get-ChildItem $out -Force | Where-Object { $_.Name -ne "data" } | Remove-Item -Recurse -Force
}

Write-Host "Publishing single-file build (win-x64, self-contained)..." -ForegroundColor Cyan

& $dotnet publish $proj `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:DebugType=none `
    -p:SatelliteResourceLanguages=en `
    -o $out

if ($LASTEXITCODE -ne 0) {
    Write-Host "Publish failed." -ForegroundColor Red
    exit 1
}

$exe = Join-Path $out "Roblox Account Manager.exe"
if (Test-Path $exe) {
    $sizeMb = [math]::Round((Get-Item $exe).Length / 1MB, 1)
    Write-Host ""
    Write-Host "Done. Executable ($sizeMb MB):" -ForegroundColor Green
    Write-Host "  $exe" -ForegroundColor Green
    Write-Host ""
    Write-Host "Copy that single file wherever you like and double-click to run." -ForegroundColor Gray
} else {
    Write-Host "Publish finished but the .exe was not found in $out" -ForegroundColor Yellow
}
