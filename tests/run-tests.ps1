#requires -Version 5.1
<#
    Compiles and runs the headless tests: the quest engine, the story content and the cannon
    ballistics - everything in the mod that is deliberately free of Unity.

    Note: Windows Smart App Control / application control can block freshly compiled unsigned
    executables ("Eine Anwendungssteuerungsrichtlinie hat diese Datei blockiert", 0x800711C7).
    If that happens the tests did not fail - they did not run. Do not weaken the policy for this;
    run them on a machine without it, or once the policy allows the file.
#>
param(
    [string]$Csc = "C:\Users\Maxime\AppData\Local\Temp\htfdec\roslyn\tasks\net472\csc.exe"
)

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$out = Join-Path $PSScriptRoot "bin"
New-Item -ItemType Directory -Force $out | Out-Null
$exe = Join-Path $out "QuestTests.exe"

# Only Unity-free sources may be listed here; adding a Unity-dependent file fails the build, which
# is intentional - it keeps the testable core honest.
$sources = @(
    (Join-Path $root "src\Quests\QuestModel.cs"),
    (Join-Path $root "src\Quests\QuestEngine.cs"),
    (Join-Path $root "src\Content\PirateStory.cs"),
    (Join-Path $root "src\Content\StoryNpcs.cs"),
    (Join-Path $root "src\Pirates\Ballistics.cs"),
    (Join-Path $PSScriptRoot "QuestEngineTests.cs"),
    (Join-Path $PSScriptRoot "ContentTests.cs")
)
foreach ($s in $sources) { if (-not (Test-Path $s)) { throw "missing source: $s" } }

& $Csc -nologo -target:exe -langversion:9.0 -out:$exe $sources
if ($LASTEXITCODE -ne 0) { throw "test compile failed ($LASTEXITCODE)" }

& $exe
exit $LASTEXITCODE
