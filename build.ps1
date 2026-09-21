#requires -Version 5.1
<#
    Builds SeagullSwarm.dll without a .NET SDK, by invoking the Roslyn compiler directly
    against the game's own Managed assemblies and the BepInEx 5 core libraries.
#>
param(
    [string]$GameDir = "C:\Program Files (x86)\Steam\steamapps\common\How to Fish\How to Fish",
    [string]$Csc     = "C:\Users\Maxime\AppData\Local\Temp\htfdec\roslyn\tasks\net472\csc.exe",
    [string]$BepInEx = "C:\Users\Maxime\AppData\Local\Temp\htfdec\bepinex\BepInEx\core",
    [string]$OutDir  = "$PSScriptRoot\dist"
)

$ErrorActionPreference = "Stop"
$managed = Join-Path $GameDir "How to Fish_Data\Managed"

foreach ($p in @($managed, $Csc, $BepInEx)) {
    if (-not (Test-Path $p)) { throw "Missing required path: $p" }
}

$refNames = @(
    "mscorlib.dll", "System.dll", "System.Core.dll", "netstandard.dll",
    "Assembly-CSharp.dll", "FishNet.Runtime.dll", "GameKit.Dependencies.dll",
    "com.rlabrecque.steamworks.net.dll", "Newtonsoft.Json.dll",
    "UnityEngine.dll", "UnityEngine.CoreModule.dll", "UnityEngine.PhysicsModule.dll",
    "UnityEngine.AnimationModule.dll", "UnityEngine.InputLegacyModule.dll",
    "UnityEngine.IMGUIModule.dll", "UnityEngine.TextRenderingModule.dll",
    "UnityEngine.AssetBundleModule.dll", "UnityEngine.ParticleSystemModule.dll", "UnityEngine.AudioModule.dll"
)

$refs = @()
foreach ($n in $refNames) {
    $p = Join-Path $managed $n
    if (Test-Path $p) { $refs += $p } else { Write-Warning "skipping missing reference $n" }
}
foreach ($n in @("BepInEx.dll", "0Harmony.dll")) {
    $p = Join-Path $BepInEx $n
    if (-not (Test-Path $p)) { throw "Missing BepInEx reference: $p" }
    $refs += $p
}

New-Item -ItemType Directory -Force $OutDir | Out-Null
$out = Join-Path $OutDir "HowToFishExpanded.dll"
$sources = Get-ChildItem (Join-Path $PSScriptRoot "src") -Recurse -Filter *.cs | ForEach-Object { $_.FullName }

$cscArgs = @(
    "-nologo", "-target:library", "-optimize+", "-langversion:9.0",
    "-nostdlib+", "-noconfig", "-warn:4",
    "-out:$out"
)
$cscArgs += ($refs | ForEach-Object { "-r:$_" })
$cscArgs += $sources

Write-Host "Compiling $($sources.Count) files -> $out"
& $Csc $cscArgs
if ($LASTEXITCODE -ne 0) { throw "csc failed with exit code $LASTEXITCODE" }

Write-Host "Built: $out ($((Get-Item $out).Length) bytes)"
