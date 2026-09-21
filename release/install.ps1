#requires -Version 5.1
<#
    Installs How to Fish: Expanded into your How to Fish (Steam) folder.

    Copies BepInEx (the mod loader), the mod and its models next to the game. Your own game files
    and saves are not touched; the mod keeps its progress in a separate file. Run it again after
    pulling a new version - it overwrites the mod, keeps your mod settings.

    Usage:  double-click "Install.bat", or:  powershell -ExecutionPolicy Bypass -File release\install.ps1
            Optional:  -GameDir "D:\SteamLibrary\steamapps\common\How to Fish\How to Fish"
#>
param([string]$GameDir)

$ErrorActionPreference = "Stop"
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$payload = Join-Path $here "game-files"

function Find-Game {
    $candidates = New-Object System.Collections.Generic.List[string]
    $steam = $null
    try { $steam = (Get-ItemProperty "HKCU:\Software\Valve\Steam" -ErrorAction Stop).SteamPath } catch { }
    if ($steam) {
        $candidates.Add((Join-Path $steam "steamapps\common\How to Fish\How to Fish"))
        $vdf = Join-Path $steam "steamapps\libraryfolders.vdf"
        if (Test-Path $vdf) {
            foreach ($m in [regex]::Matches((Get-Content $vdf -Raw), '"path"\s+"([^"]+)"')) {
                $lib = $m.Groups[1].Value -replace '\\\\', '\'
                $candidates.Add((Join-Path $lib "steamapps\common\How to Fish\How to Fish"))
            }
        }
    }
    $candidates.Add("C:\Program Files (x86)\Steam\steamapps\common\How to Fish\How to Fish")
    foreach ($c in $candidates) { if (Test-Path (Join-Path $c "How to Fish.exe")) { return $c } }
    return $null
}

if (-not $GameDir) { $GameDir = Find-Game }
if (-not $GameDir -or -not (Test-Path (Join-Path $GameDir "How to Fish.exe"))) {
    Write-Host "Could not find How to Fish. Run again with -GameDir `"<folder that contains How to Fish.exe>`"." -ForegroundColor Red
    exit 1
}
if (Get-Process | Where-Object { $_.ProcessName -like "How to Fish*" }) {
    Write-Host "Close the game first, then run the installer again." -ForegroundColor Yellow
    exit 1
}

Write-Host "Installing into: $GameDir"
Copy-Item (Join-Path $payload "*") $GameDir -Recurse -Force

# Make sure the mod loader is switched on (Toggle Mods.bat can turn it off).
$ini = Join-Path $GameDir "doorstop_config.ini"
(Get-Content $ini) -replace '^enabled = false', 'enabled = true' | Set-Content $ini -Encoding ascii

Write-Host ""
Write-Host "Done. Start How to Fish from Steam as usual." -ForegroundColor Green
Write-Host "Everyone in a multiplayer game needs this same version installed."
Write-Host "'Toggle Mods.bat' in the game folder switches the mod off/on for a plain game."
