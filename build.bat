@echo off
rem Double-click to build the single self-contained exe into dist\ (no git, no release).
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0publish.ps1"
pause
