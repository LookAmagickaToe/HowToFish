@echo off
title How to Fish - Mod Switch
cd /d "%~dp0"
powershell -NoProfile -ExecutionPolicy Bypass -Command "$f='doorstop_config.ini'; $c=Get-Content $f; if ($c -match '^enabled = true') { $c=$c -replace '^enabled = true','enabled = false'; $s='OFF  -  plain vanilla game' } else { $c=$c -replace '^enabled = false','enabled = true'; $s='ON   -  How to Fish: Expanded active' }; Set-Content $f $c -Encoding ascii; Write-Host ''; Write-Host ('   Mods are now ' + $s); Write-Host ''; Write-Host '   Takes effect the next time you start the game.'"
echo.
pause
