@echo off
rem Double-click to build, commit, push and publish a GitHub release.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0release.ps1"
pause
