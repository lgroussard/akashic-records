@echo off
rem Runs build-mobile.ps1 without changing the system execution policy (bypass for this run only).
rem Usage: build-mobile.cmd [-SkipApk] [-SkipData] [-OutDir <dossier>]   (or double-click)
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0build-mobile.ps1" %*
echo.
pause
