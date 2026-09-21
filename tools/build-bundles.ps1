#requires -Version 5.1
<#
    Builds the asset bundle the mod loads at runtime.

    Drives Unity in batch mode against a throwaway project, so nothing has to be clicked and the
    build is reproducible. The project is rebuilt from scratch on demand; only this script and the
    editor script are kept in the repo.

    Usage:  .\tools\build-bundles.ps1            # incremental
            .\tools\build-bundles.ps1 -Clean     # discard the scratch project first
#>
param(
    [string]$Unity   = "C:\Program Files\Unity 6000.4.4f1\Editor\Unity.exe",
    [string]$Project = "C:\Users\Maxime\AppData\Local\Temp\htfdec\unityproj",
    [switch]$Clean,
    [switch]$Install          # also copy the result next to the installed plugin
)

$ErrorActionPreference = "Stop"
$root    = Split-Path $PSScriptRoot -Parent
$raw     = Join-Path $root "assets\raw"
$outDir  = Join-Path $root "dist\bundles"
$log     = Join-Path $env:TEMP "htfdec\unity-bundle.log"

if (-not (Test-Path $Unity)) { throw "Unity not found at $Unity" }

if ($Clean -and (Test-Path $Project)) {
    Write-Host "Removing scratch project..."
    Remove-Item $Project -Recurse -Force
}

if (-not (Test-Path (Join-Path $Project "Assets"))) {
    Write-Host "Creating scratch Unity project (first run takes a few minutes)..."
    $p = Start-Process $Unity -ArgumentList @("-batchmode","-quit","-nographics","-createProject",$Project,"-logFile",$log) -PassThru -Wait
    if ($p.ExitCode -ne 0) { throw "Project creation failed ($($p.ExitCode)). See $log" }
}

# --- stage the source models -------------------------------------------------
$kit = Join-Path $Project "Assets\PirateKit"
New-Item -ItemType Directory -Force $kit | Out-Null

$kenneyFbx = Join-Path $raw "kenney_pirate-kit\Models\FBX format"
if (-not (Test-Path $kenneyFbx)) { throw "Kenney models missing at $kenneyFbx" }

Write-Host "Staging models..."
Copy-Item (Join-Path $kenneyFbx "*.fbx") $kit -Force
$texDir = Join-Path $kit "Textures"
New-Item -ItemType Directory -Force $texDir | Out-Null
Copy-Item (Join-Path $kenneyFbx "Textures\*.png") $texDir -Force

$staged = (Get-ChildItem $kit -Filter *.fbx).Count
Write-Host "  $staged model(s), $((Get-ChildItem $texDir -Filter *.png).Count) texture(s)"

# --- Quaternius characters (optional) -----------------------------------------
$quat = Join-Path $raw "quaternius_pirate-kit"
$chars = Join-Path $Project "Assets\Characters"
if (Test-Path $chars) { Get-ChildItem $chars -Filter *.fbx | ForEach-Object { [IO.File]::Delete($_.FullName) } }
if (Test-Path $quat) {
    New-Item -ItemType Directory -Force $chars | Out-Null
    # Characters, plus the handful of props the story hands to them or scatters as loot.
    $want = '^(Characters_.*|Prop_Chest_Gold|Prop_Coins|Prop_GoldBag|Prop_Bottle_1|Weapon_Cutlass|Weapon_Pistol)\.fbx$'
    Get-ChildItem $quat -Recurse -Filter *.fbx | Where-Object { $_.Name -match $want } |
        ForEach-Object { Copy-Item $_.FullName $chars -Force }
    Write-Host "  $((Get-ChildItem $chars -Filter *.fbx).Count) character/prop model(s) from Quaternius"

    # The palette every Quaternius model samples. Searched anywhere under assets\raw so it works
    # whichever download it came from.
    $charTex = Join-Path $chars "Textures"
    New-Item -ItemType Directory -Force $charTex | Out-Null
    $atlas = Get-ChildItem $raw -Recurse -Filter "Atlas_Pirate*.png" -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($atlas) {
        Copy-Item $atlas.FullName (Join-Path $charTex "Atlas_Pirate.png") -Force
        Write-Host "  colour atlas: $($atlas.FullName)"
    } else {
        Write-Host "  colour atlas: MISSING - characters will be grey (see ASSETS.md)"
    }
} else {
    Write-Host "  (no Quaternius kit found - characters bundle skipped)"
}

# --- editor script -----------------------------------------------------------
$editorDir = Join-Path $Project "Assets\Editor"
New-Item -ItemType Directory -Force $editorDir | Out-Null
Copy-Item (Join-Path $PSScriptRoot "unity\BundleBuilder.cs") $editorDir -Force

# --- build -------------------------------------------------------------------
New-Item -ItemType Directory -Force $outDir | Out-Null
if (Test-Path $log) { Remove-Item $log -Force }

Write-Host "Building bundle (Unity batch mode)..."
$args = @("-batchmode","-nographics","-projectPath",$Project,
          "-executeMethod","BundleBuilder.Build","-bundleOut",$outDir,"-logFile",$log)
$p = Start-Process $Unity -ArgumentList $args -PassThru -Wait

Write-Host "--- BundleBuilder output ---"
if (Test-Path $log) {
    Select-String $log -Pattern '\[BundleBuilder\]' | ForEach-Object { $_.Line.Trim() }
}
if ($p.ExitCode -ne 0) {
    Write-Host "--- errors ---"
    if (Test-Path $log) { Select-String $log -Pattern 'error CS|Exception|FAILED' | Select-Object -First 15 | ForEach-Object { $_.Line.Trim() } }
    throw "Bundle build failed ($($p.ExitCode)). Full log: $log"
}

$bundle = Join-Path $outDir "pirates"
if (-not (Test-Path $bundle)) { throw "Bundle missing after build. Log: $log" }
Write-Host "Built: $bundle ($((Get-Item $bundle).Length) bytes)"

if ($Install) {
    $plugins = "C:\Program Files (x86)\Steam\steamapps\common\How to Fish\How to Fish\BepInEx\plugins"
    $dest = Join-Path $plugins "ExpandedAssets"
    New-Item -ItemType Directory -Force $dest | Out-Null
    foreach ($f in @("pirates", "characters", "characters.json")) {
        $src = Join-Path $outDir $f
        if (Test-Path $src) { Copy-Item $src $dest -Force; Write-Host "Installed $f" }
    }
    Write-Host "Installed to $dest"
}
