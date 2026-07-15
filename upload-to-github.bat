@echo off
setlocal enabledelayedexpansion
title Roblox Account Manager - GitHub Upload
color 0f
cd /d "%~dp0"

echo ==========================================================
echo    Roblox Account Manager  -  Upload zu GitHub
echo ==========================================================
echo.

REM ---------------------------------------------------------------
REM  1) Ist Git installiert?
REM ---------------------------------------------------------------
git --version >nul 2>&1
if errorlevel 1 (
    echo [FEHLER] Git ist nicht installiert.
    echo.
    echo Bitte installiere Git zuerst:  https://git-scm.com/download/win
    echo Danach dieses Skript nochmal starten.
    echo.
    start https://git-scm.com/download/win
    pause
    exit /b 1
)
echo [OK] Git gefunden.
echo.

REM ---------------------------------------------------------------
REM  2) Git-Identitaet gesetzt? (Name + E-Mail fuer Commits)
REM ---------------------------------------------------------------
set "GITNAME="
for /f "delims=" %%i in ('git config --global user.name 2^>nul') do set "GITNAME=%%i"
if "!GITNAME!"=="" (
    echo Git braucht einmalig deinen Namen und E-Mail fuer Commits.
    set /p "NEWNAME=  Dein Name (z.B. dein GitHub-Benutzername): "
    set /p "NEWMAIL=  Deine E-Mail: "
    git config --global user.name "!NEWNAME!"
    git config --global user.email "!NEWMAIL!"
    echo [OK] Identitaet gespeichert.
    echo.
)

REM ---------------------------------------------------------------
REM  3) Repository initialisieren (nur beim ersten Mal)
REM ---------------------------------------------------------------
if not exist ".git" (
    echo [INFO] Neues Git-Repository wird angelegt...
    git init
    git branch -M main
    echo.
)

REM ---------------------------------------------------------------
REM  4) Dateien vormerken
REM ---------------------------------------------------------------
echo [INFO] Dateien werden vorgemerkt (deine .gitignore schuetzt data/, dist/, bin/)...
git add -A

REM ---- Sicherheitscheck: keine Cookies / privaten Daten hochladen ----
set "LEAK="
for /f "delims=" %%f in ('git diff --cached --name-only 2^>nul ^| findstr /I /C:"data/" /C:".dat" /C:"error.log"') do set "LEAK=%%f"
if not "!LEAK!"=="" (
    echo.
    echo ==========================================================
    echo  [ABBRUCH]  Es wuerden private Dateien hochgeladen:
    echo    !LEAK!
    echo  Das darf NICHT passieren ^(enthaelt evtl. deine Cookies^).
    echo  Pruefe die .gitignore. Nichts wurde hochgeladen.
    echo ==========================================================
    pause
    exit /b 1
)

echo.
echo Diese Dateien werden hochgeladen:
echo ----------------------------------------------------------
git status --short
echo ----------------------------------------------------------
echo.

REM ---------------------------------------------------------------
REM  5) Commit-Text abfragen
REM ---------------------------------------------------------------
set "MSG="
set /p "MSG=Commit-Beschreibung (Enter = 'Update'): "
if "!MSG!"=="" set "MSG=Update"

git commit -m "!MSG!" 2>nul
if errorlevel 1 (
    echo [INFO] Nichts Neues zum Committen ^(oder schon aktuell^).
) else (
    echo [OK] Commit erstellt.
)
echo.

REM ---------------------------------------------------------------
REM  6) GitHub-Repo verbinden (nur beim ersten Mal)
REM ---------------------------------------------------------------
set "ORIGIN="
for /f "delims=" %%i in ('git remote get-url origin 2^>nul') do set "ORIGIN=%%i"
if "!ORIGIN!"=="" (
    echo Noch kein GitHub-Repo verbunden.
    echo.
    echo   1. Gehe auf  https://github.com/new
    echo   2. Gib einen Namen ein, lass es LEER ^(kein README/gitignore^), klick "Create repository"
    echo   3. Kopiere die URL, z.B.  https://github.com/DEINNAME/RobloxAccountManager.git
    echo.
    set /p "REPOURL=Repo-URL hier einfuegen: "
    if "!REPOURL!"=="" (
        echo [ABBRUCH] Keine URL angegeben.
        pause
        exit /b 1
    )
    git remote add origin "!REPOURL!"
    echo [OK] Verbunden.
    echo.
)

REM ---------------------------------------------------------------
REM  7) Hochladen (Push)
REM ---------------------------------------------------------------
echo [INFO] Wird hochgeladen... ^(evtl. oeffnet sich ein Browser-Login fuer GitHub^)
git push -u origin main
if errorlevel 1 (
    echo.
    echo [FEHLER] Push fehlgeschlagen.
    echo  - Falls Login noetig: melde dich im Browser/Fenster bei GitHub an und starte nochmal.
    echo  - Falls das Repo auf GitHub nicht leer war: sag Bescheid.
    pause
    exit /b 1
)

echo.
echo ==========================================================
echo   [FERTIG]  Alles hochgeladen!  Schau auf GitHub nach.
echo ==========================================================
echo.
pause
