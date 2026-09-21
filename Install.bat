@echo off
title How to Fish: Expanded - Install
cd /d "%~dp0"
powershell -NoProfile -ExecutionPolicy Bypass -File "release\install.ps1" %*
echo.
pause
