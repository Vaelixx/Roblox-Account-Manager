# =====================================================================
#  release.ps1 - one-shot release flow:
#    1. Bump <Version> in src\RobloxAccountManager.csproj
#    2. Build via publish.ps1  ->  dist\Roblox Account Manager.exe
#    3. git add / commit / push
#    4. Create GitHub release vX.Y.Z and upload the exe as
#       RobloxAccountManager.exe (the auto-updater expects an asset
#       whose name ends in .exe on /releases/latest)
#
#  Usage:  double-click publish.bat
#     or:  powershell -NoProfile -ExecutionPolicy Bypass -File release.ps1
# =====================================================================
$ErrorActionPreference = "Stop"

# Always operate from the repo root (the folder containing this script).
Set-Location $PSScriptRoot

$Owner      = "Vaelixx"
$Repo       = "Roblox-Account-Manager"
$ApiBase    = "https://api.github.com/repos/$Owner/$Repo"
$UploadBase = "https://uploads.github.com/repos/$Owner/$Repo"
$CsprojPath = Join-Path $PSScriptRoot "src\RobloxAccountManager.csproj"
$ExePath    = Join-Path $PSScriptRoot "dist\Roblox Account Manager.exe"
$AssetName  = "RobloxAccountManager.exe"   # required by the app's auto-updater

function Fail([string]$msg) {
    Write-Host ""
    Write-Host "ERROR: $msg" -ForegroundColor Red
    exit 1
}

function Get-ApiErrorText($err) {
    # Best-effort "HTTP <status> - <short body>" from a web exception.
    # Works on both Windows PowerShell 5.1 and PowerShell 7+.
    $status = ""
    $body   = ""
    try {
        $resp = $err.Exception.Response
        if ($resp -and $resp.StatusCode) { $status = [int]$resp.StatusCode }
    } catch { }
    if ($err.ErrorDetails -and $err.ErrorDetails.Message) {
        $body = $err.ErrorDetails.Message
    } else {
        try {
            $stream = $err.Exception.Response.GetResponseStream()
            $reader = New-Object System.IO.StreamReader($stream)
            $body   = $reader.ReadToEnd()
            $reader.Dispose()
        } catch {
            $body = $err.Exception.Message
        }
    }
    if ($body -and $body.Length -gt 500) { $body = $body.Substring(0, 500) + "..." }
    return "HTTP $status - $body"
}

# Older PowerShell defaults to TLS 1.0/1.1; GitHub requires TLS 1.2+.
try {
    [Net.ServicePointManager]::SecurityProtocol = `
        [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
} catch { }

# ---------------------------------------------------------------------
# 1. Version bump
# ---------------------------------------------------------------------
if (-not (Test-Path $CsprojPath)) { Fail "csproj not found: $CsprojPath" }

$csprojText = [System.IO.File]::ReadAllText($CsprojPath)
if ($csprojText -notmatch '<Version>(\d+)\.(\d+)\.(\d+)</Version>') {
    Fail "Could not find <Version>X.Y.Z</Version> in $CsprojPath"
}
$currentVersion = "$($Matches[1]).$($Matches[2]).$($Matches[3])"
$suggested      = "$($Matches[1]).$($Matches[2]).$([int]$Matches[3] + 1)"

Write-Host "Current version: $currentVersion" -ForegroundColor Cyan

$newVersion = $null
while (-not $newVersion) {
    $answer = Read-Host "New version [$suggested]"
    if ([string]::IsNullOrWhiteSpace($answer)) { $answer = $suggested }
    $answer = $answer.Trim()
    if ($answer -match '^\d+\.\d+\.\d+$') {
        $newVersion = $answer
    } else {
        Write-Host "Invalid version '$answer' - expected X.Y.Z (e.g. 1.0.1)" -ForegroundColor Yellow
    }
}
$tag = "v$newVersion"

# Only the first <Version> tag — never touch other Version-like entries in the csproj.
$csprojText = [regex]::new('<Version>\d+\.\d+\.\d+</Version>').Replace($csprojText, "<Version>$newVersion</Version>", 1)
[System.IO.File]::WriteAllText($CsprojPath, $csprojText)
Write-Host "Version set to $newVersion in csproj." -ForegroundColor Green

# ---------------------------------------------------------------------
# 2. Build (publish.ps1)
# ---------------------------------------------------------------------
Write-Host ""
Write-Host "=== Building (publish.ps1) ===" -ForegroundColor Cyan
try {
    & (Join-Path $PSScriptRoot "publish.ps1")
} catch {
    Fail "publish.ps1 threw an error: $($_.Exception.Message)"
}
if ($LASTEXITCODE -ne 0) { Fail "publish.ps1 failed (exit code $LASTEXITCODE)." }
if (-not (Test-Path $ExePath)) { Fail "Build finished but the exe was not found: $ExePath" }

# ---------------------------------------------------------------------
# 3. Git commit & push
# ---------------------------------------------------------------------
Write-Host ""
Write-Host "=== Git commit & push ===" -ForegroundColor Cyan

$branch = (git branch --show-current | Out-String).Trim()
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($branch)) {
    Fail "Could not determine the current git branch. Is this a git repo?"
}
Write-Host "Branch: $branch"

$defaultMsg = "Release v$newVersion"
$commitMsg  = Read-Host "Commit message [$defaultMsg]"
if ([string]::IsNullOrWhiteSpace($commitMsg)) { $commitMsg = $defaultMsg }

git add -A
if ($LASTEXITCODE -ne 0) { Fail "git add failed." }

# "nothing to commit" is not an error - just continue to the release.
git diff --cached --quiet
if ($LASTEXITCODE -eq 0) {
    Write-Host "Nothing to commit - continuing." -ForegroundColor Yellow
} else {
    git commit -m $commitMsg
    if ($LASTEXITCODE -ne 0) { Fail "git commit failed." }
}

git push origin $branch
if ($LASTEXITCODE -ne 0) { Fail "git push origin $branch failed." }

# ---------------------------------------------------------------------
# 4. GitHub token via Git Credential Manager
# ---------------------------------------------------------------------
Write-Host ""
Write-Host "=== GitHub release ===" -ForegroundColor Cyan

$credLines = "protocol=https`nhost=github.com`n" | git credential fill
$token = $null
foreach ($line in @($credLines)) {
    if ("$line" -like "password=*") { $token = "$line".Substring(9); break }
}
if ([string]::IsNullOrWhiteSpace($token)) {
    Fail "No GitHub token returned by 'git credential fill'. Sign in via Git Credential Manager (e.g. run 'git fetch' once and complete the login) and retry."
}
Write-Host "Token acquired from Git Credential Manager." -ForegroundColor Green
# NOTE: never print the token itself.

$changelog = Read-Host "Release notes [Update]"
if ([string]::IsNullOrWhiteSpace($changelog)) { $changelog = "Update" }

$headers = @{
    "Authorization" = "Bearer $token"
    "User-Agent"    = "RAM-Release"
    "Accept"        = "application/vnd.github+json"
}

# ---------------------------------------------------------------------
# 5. Create the release
# ---------------------------------------------------------------------
$payload = @{
    tag_name               = $tag
    target_commitish       = $branch
    name                   = "Roblox Account Manager v$newVersion"
    body                   = $changelog
    generate_release_notes = $false
} | ConvertTo-Json
$payloadBytes = [System.Text.Encoding]::UTF8.GetBytes($payload)

$release = $null
try {
    $release = Invoke-RestMethod -Method Post -Uri "$ApiBase/releases" `
        -Headers $headers -Body $payloadBytes -ContentType "application/json; charset=utf-8" `
        -TimeoutSec 120
} catch {
    Fail "Failed to create release $tag : $(Get-ApiErrorText $_)"
}
Write-Host "Release $tag created (id $($release.id))." -ForegroundColor Green

# ---------------------------------------------------------------------
# 6. Upload the exe asset (streamed from disk - do not load into memory)
# ---------------------------------------------------------------------
$sizeMb = [math]::Round((Get-Item $ExePath).Length / 1MB, 1)
Write-Host "Uploading $AssetName ($sizeMb MB)... this can take a few minutes." -ForegroundColor Cyan

$asset = $null
try {
    $asset = Invoke-RestMethod -Method Post `
        -Uri "$UploadBase/releases/$($release.id)/assets?name=$AssetName" `
        -Headers $headers -ContentType "application/octet-stream" `
        -InFile $ExePath -TimeoutSec 1800
} catch {
    Fail "Asset upload failed: $(Get-ApiErrorText $_)`nThe release $tag exists without an asset - delete it on GitHub before retrying, or upload the exe manually."
}
Write-Host "Asset uploaded: $($asset.name)" -ForegroundColor Green

# ---------------------------------------------------------------------
# Done
# ---------------------------------------------------------------------
Write-Host ""
Write-Host "SUCCESS: $($release.html_url)" -ForegroundColor Green
exit 0
