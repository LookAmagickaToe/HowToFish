#requires -Version 5.1
<#
    Compiles and runs the headless tests. These cover the parts of the mod that are deliberately
    free of Unity - the quest engine and save serialisation - so they run without the game.
#>
param(
    [string]$Csc = "C:\Users\Maxime\AppData\Local\Temp\htfdec\roslyn\tasks\net472\csc.exe"
)

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$out = Join-Path $PSScriptRoot "bin"
New-Item -ItemType Directory -Force $out | Out-Null
$exe = Join-Path $out "QuestTests.exe"

# Only Unity-free sources may be listed here; adding a Unity-dependent file will fail the build,
# which is intentional - it keeps the testable core honest.
$sources = @(
    (Join-Path $root "src\Quests\QuestModel.cs"),
    (Join-Path $root "src\Quests\QuestEngine.cs"),
    (Join-Path $PSScriptRoot "QuestEngineTests.cs")
)
foreach ($s in $sources) { if (-not (Test-Path $s)) { throw "missing source: $s" } }

& $Csc -nologo -target:exe -langversion:9.0 -out:$exe $sources
if ($LASTEXITCODE -ne 0) { throw "test compile failed ($LASTEXITCODE)" }

& $exe
exit $LASTEXITCODE
