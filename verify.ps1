#requires -Version 5.1
<#
    Static check that every Harmony target and every game member the plugin touches actually
    exists in the shipped Assembly-CSharp.dll, with the signature the plugin assumes.
    Reads metadata with Mono.Cecil (shipped inside BepInEx) - the game is never launched.
#>
param(
    [string]$GameDir = "C:\Program Files (x86)\Steam\steamapps\common\How to Fish\How to Fish",
    [string]$Cecil   = "C:\Users\Maxime\AppData\Local\Temp\htfdec\bepinex\BepInEx\core\Mono.Cecil.dll"
)

$ErrorActionPreference = "Stop"
[void][System.Reflection.Assembly]::LoadFrom($Cecil)

$asmPath = Join-Path $GameDir "How to Fish_Data\Managed\Assembly-CSharp.dll"
$asm = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($asmPath)
$module = $asm.MainModule

function Get-Type([string]$name) {
    $t = $module.GetType($name)
    if (-not $t) { throw "TYPE MISSING: $name" }
    return $t
}

$fail = 0

function Check-Method([string]$typeName, [string]$method, [string[]]$paramTypes) {
    $t = Get-Type $typeName
    $matches = @($t.Methods | Where-Object { $_.Name -eq $method })
    if ($matches.Count -eq 0) {
        Write-Host "  FAIL  $typeName.$method  -- no method by that name" -ForegroundColor Red
        $script:fail++; return
    }
    foreach ($m in $matches) {
        $actual = @($m.Parameters | ForEach-Object { $_.ParameterType.Name })
        if ($null -eq $paramTypes) {
            Write-Host "  ok    $typeName.$method($($actual -join ', '))" -ForegroundColor Green
            return
        }
        if (($actual -join ',') -eq ($paramTypes -join ',')) {
            Write-Host "  ok    $typeName.$method($($actual -join ', '))" -ForegroundColor Green
            return
        }
    }
    $seen = ($matches | ForEach-Object { "($(($_.Parameters | ForEach-Object { $_.ParameterType.Name }) -join ', '))" }) -join ' | '
    Write-Host "  FAIL  $typeName.$method  -- wanted ($($paramTypes -join ', ')), found $seen" -ForegroundColor Red
    $script:fail++
}

function Check-Field([string]$typeName, [string]$field, [string]$expectPublic) {
    $t = Get-Type $typeName
    $f = $t.Fields | Where-Object { $_.Name -eq $field } | Select-Object -First 1
    if (-not $f) {
        Write-Host "  FAIL  $typeName.$field  -- field missing" -ForegroundColor Red
        $script:fail++; return
    }
    $vis = if ($f.IsPublic) { "public" } else { "non-public" }
    if ($expectPublic -and $vis -ne $expectPublic) {
        Write-Host "  FAIL  $typeName.$field  -- expected $expectPublic, is $vis" -ForegroundColor Red
        $script:fail++; return
    }
    Write-Host "  ok    $typeName.$field : $($f.FieldType.Name) ($vis)" -ForegroundColor Green
}

Write-Host "`n-- Harmony patch targets --"
Check-Method "BirdManager" "OnStartServer" @()
Check-Method "BirdManager" "SimulateBird" @("Bird")
Check-Method "Bird"        "OnDeath"      @()
Check-Method "BossManager" "UpdateBossMaxHp" @()

Write-Host "`n-- Damage path --"
Check-Method "PlayerVitals" "TakeDamage"  @("Int32", "Vector3", "Vector3", "Boolean")
Check-Method "PlayerVitals" "ObserverHit" @("Player", "Vector3", "Vector3", "Int32", "DamageType")

Write-Host "`n-- Boss bar plumbing --"
Check-Field  "Creature"    "_hp"         "public"
Check-Field  "BossManager" "_bossMaxHp"  "public"
Check-Method "BossManager" "ToggleImmortal" @("Boolean")

Write-Host "`n-- Spawning & bird control --"
Check-Method "ItemManager" "SpawnNewItem" @("Item", "Vector3", "Quaternion")
Check-Method "GameInfo"    "GetSpawnable" @("String")
Check-Method "Bird"        "SetAnimState" @("Byte")
Check-Method "Bird"        "SetSpeed"     @("Single")
Check-Method "Item"        "DestroyItem"  @("Byte", "Byte")

Write-Host "`n-- Testing & diagnostics --"
Check-Method "ChatManager"  "ChatMessage"       @("String")
Check-Method "ChatManager"  "get_IsTyping"      @()
Check-Method "Player"       "get_BlockInputs"   @()
Check-Method "BossManager"  "get_BossLeavesTick" @()
Check-Method "PlayerVitals" "get_Health"        @()

Write-Host "`n-- Cover, water, leader, audio --"
Check-Method "GameInfo"     "get_LevelLayer"   @()
Check-Method "GameInfo"     "get_BoatLayer"    @()
Check-Method "GameInfo"     "get_CurCamera"    @()
Check-Method "WaterManager" "get_WaterHeight"  @()
Check-Method "BossManager"  "GetBossMaxHp"     @("Int32", "Single")
Check-Method "AudioManager" "PlayRandomClipAt" @("String", "Int32", "Int32", "Vector3", "Boolean", "AudioDistance", "Single", "Single")
Check-Field  "PlayerVitals" "_player" $null

# Harmony binds injected arguments by NAME, so the parameter names matter, not just the types.
Write-Host "`n-- Quests & story --"
Check-Method "Creature"            "OnDeath"           @()
Check-Method "OnlineIslandManager" "get_CurIsland"     @()
Check-Method "MoneyManager"        "AddMoney"          @("Int32", "Player")
Check-Method "Item"                "get_RandomizedWeight" @()
Check-Field  "Item"                "_weight" $null   # protected on Item, inherited by Creature
Check-Field  "ItemManager"         "Instance" "public"

Write-Host "`n-- Pirates: combat --"
Check-Method "ExplosionManager"  "ServerExplode"      @("Item", "ExplosionInfo")
Check-Method "ProjectileManager" "Hit"                @("Projectile", "ProjectileType", "RaycastHit")
Check-Method "Item"              "GetExplosionInfo"   @()
Check-Field  "ExplosionInfo"     "_underwaterFishMinMax" $null
Check-Method "ExplosionInfo"     "get_DamageRadius"   @()
Check-Method "ExplosionInfo"     "get_Damage"         @()
Check-Method "ExplosionInfo"     "get_HasExploded"    @()
Check-Method "ExplosionInfo"     "get_OnlyExplodeOnce" @()
Check-Field  "Projectile"        "IsLocal" "public"
Check-Field  "Projectile"        "Damage"  "public"
Check-Field  "Projectile"        "FromNpc" "public"
Check-Method "GameInfo"          "get_ProjectileHitLayer"    @()
Check-Method "GameInfo"          "get_NpcProjectileHitLayer" @()
Check-Method "ParticleManager"   "Play"               @("String", "Vector3", "Vector3")
Check-Method "AudioManager"      "PlayClipAt"         @("String", "Vector3", "Boolean", "AudioDistance", "Single", "Single")
Check-Method "PlayerManager"     "GetPlayerFromBodyPart" @("Transform")
Check-Method "MoneyManager"      "get_Money"          @()

Write-Host "`n-- Pirates: boat & islands --"
Check-Method "BoatManager"       "get_Boat"           @()
Check-Field  "SpawnManager"      "PlayerSpawnPos" "public"
Check-Field  "SpawnManager"      "PlayerSpawnRot" "public"
Check-Field  "SpawnManager"      "BoatSpawnPos"   "public"
if ($module.GetType("BoatMotor")) { Write-Host "  ok    type BoatMotor (used to find the bow)" -ForegroundColor Green }
else { Write-Host "  FAIL  type BoatMotor missing - bow detection falls back to +Z" -ForegroundColor Red; $fail++ }

Write-Host "`n-- Shop cannon (subclasses the game's Purchasable) --"
if ($module.GetType("MotorPurchasable")) { Write-Host "  ok    type MotorPurchasable (the shop anchor)" -ForegroundColor Green }
else { Write-Host "  FAIL  type MotorPurchasable missing - no shop to sell the gun in" -ForegroundColor Red; $fail++ }
Check-Field  "Interactable" "_interactCol"     $null
Check-Field  "Interactable" "_textTarget"      $null
Check-Field  "Interactable" "_modelsToOutline" $null
Check-Field  "Purchasable"  "_customCost"      $null
Check-Field  "Purchasable"  "_hoverString"     $null
Check-Field  "Purchasable"  "_customCanBuy"    $null
Check-Method "Purchasable"  "Hover"            @()
Check-Method "Interactable" "Interact"         @("Player")
Check-Method "SpriteManager" "GetPickUpInput"  @()
Check-Method "MoneyManager" "CanAfford"        @("Int32")
Check-Method "MoneyManager" "RemoveMoney"      @("Int32", "Player")

Write-Host "`n-- Story character voice (borrowed from vanilla NPCs) --"
Check-Field  "NPC" "_mouthSource" $null
Check-Field  "NPC" "_mouthVol"    $null

Write-Host "`n-- Story characters talk and eat like vanilla NPCs --"
if ($module.GetType("NPCInteractable")) { Write-Host "  ok    type NPCInteractable (base of the talk point)" -ForegroundColor Green }
else { Write-Host "  FAIL  type NPCInteractable missing" -ForegroundColor Red; $fail++ }
Check-Method "NPCInteractable" "Interact"        @("Player")
Check-Method "PlayerUI"        "SetNpcText"      @("String", "Transform")
Check-Field  "PlayerUI"        "_instance"       $null
Check-Field  "PlayerUI"        "_npcUI"          $null
Check-Field  "NpcUI"           "_showNpcTextTime" $null
Check-Method "Item"            "DestroyByNpc"    @("Byte")
Check-Method "Item"            "DespawnItemOnServer" @()
Check-Method "Item"            "DestroyItem"     @("Byte", "Byte")
Check-Method "Item"            "get_ExtraRigs"   @()
Check-Method "Item"            "get_RigidbodySync" @()
Check-Method "Item"            "get_HasBeenHeld" @()
Check-Method "Item"            "get_LastHolder"  @()
Check-Method "Item"            "get_Holder"      @()
Check-Method "Item"            "get_Creature"    @()
Check-Method "ItemManager"     "Get"             @("Collider")
Check-Method "RigidbodySync"   "SetKinematic"    @("Boolean")
Check-Method "Creature"        "get_IsDead"      @()
$dbn = (Get-Type "Item").Methods | Where-Object { $_.Name -eq "DestroyByNpc" } | Select-Object -First 1
if ($dbn -and ($dbn.Parameters | Where-Object { $_.Name -eq "npcID" })) { Write-Host "  ok    Item.DestroyByNpc has parameter 'npcID'" -ForegroundColor Green }
else { Write-Host "  FAIL  Item.DestroyByNpc lacks parameter 'npcID'" -ForegroundColor Red; $fail++ }

Write-Host "`n-- Manned deck gun (holds the player like the boat's driver) --"
Check-Method "PlayerMovement" "Move"            @()
Check-Method "PlayerMovement" "FixedUpdate"     @()
Check-Method "PlayerMovement" "LateUpdate"      @()
Check-Field  "PlayerMovement" "_rig"            $null
Check-Field  "PlayerMovement" "_player"         $null
Check-Method "PlayerCamera"   "MouseMovement"   @()
Check-Method "PlayerCamera"   "SetRot"          @("Single")
Check-Field  "PlayerCamera"   "_rot"            $null
Check-Field  "PlayerCamera"   "_player"         $null
foreach ($m in @("PickUpInput", "PrimaryInput", "PrimaryInputCanceled", "SecondaryInput", "ReloadInput")) {
    Check-Method "PlayerHolding" $m @("CallbackContext")
}
Check-Method "PlayerPunching" "PunchInput"      @("CallbackContext")
Check-Method "Boat"           "get_IsDrivingLocally" @()

Write-Host "`n-- Human cannonball, gull-pirate raids, island gating --"
Check-Method "PlayerMovement" "Teleport"        @("Vector3", "Boolean")
Check-Method "PlayerMovement" "SetVel"          @("Vector3")
Check-Method "Item"           "get_Cookness"    @()
Check-Method "RigidbodySync"  "get_OnBoat"      @()
Check-Method "ItemManager"    "get_Items"       @()
Check-Method "OnlineIslandManager" "get_CurIsland" @()

Write-Host "`n-- The chart handed out as the game's map --"
if ($module.GetType("Map")) { Write-Host "  ok    type Map (the handheld map with a radar)" -ForegroundColor Green }
else { Write-Host "  FAIL  type Map missing - nobody gets a map for the chart's mark" -ForegroundColor Red; $fail++ }

Write-Host "`n-- Swarm on the game's boss bar --"
Check-Field  "PlayerUI" "_bossUI" $null
foreach ($f in @("_bossCanvasLerped", "_bossNameText", "_bossHealth", "_bossHealthLerped", "_timeLeftImage", "_countdownGroup", "_timeCountdownText")) {
    Check-Field "BossUI" $f $null
}
Check-Method "BossManager" "get_Boss" @()

Write-Host "`n-- Chart mark on the radar --"
foreach ($f in @("_islandDots", "_isOn", "_localPlayerPos", "_mapScale", "_zoomMultiplier", "_maxPosDist", "_radarSweepDir", "_angleForPing")) {
    Check-Field "RadarUI" $f $null
}
Check-Field  "MapDot" "_dot" $null
Check-Method "MapDot" "Move" @("Vector2", "Vector2", "Single", "Single", "Single")
Check-Method "MapDot" "TriggerPing" @()
Check-Method "Boat"   "get_BoatRadarUnlocked" @()

Write-Host "`n-- Pirate hull (reaches into the game's boat) --"
foreach ($f in @("_dynamicObjectColsHolder", "_itemColsHolder", "_dynamicObjectCols", "_steeringWheel", "_throttle", "_boatInteractable")) {
    Check-Field "Boat" $f $null
}
Check-Method "Boat" "get_VisualBoat"   @()
Check-Method "Boat" "get_DriverPos"    @()
Check-Method "Boat" "get_BoatTrigger"  @()
Check-Field  "BoatManager" "ColToBoat" "public"
Check-Method "GameInfo" "get_NpcLayer" @()

Write-Host "`n-- Megalodon & wakeboard --"
Check-Method "PlayerCamera"      "Update"               @()
Check-Method "PlayerCamera"      "SetFov"               @()
Check-Field  "PlayerCamera"      "_cam"                 $null
Check-Field  "PlayerMovement"    "_origColHeight"       $null
Check-Method "PlayerMovement"    "get_Input"            @()
Check-Method "PlayerMovement"    "get_OnBoat"           @()
Check-Method "PlayerMovement"    "RPCKnockback"         @("NetworkConnection", "Vector3")
Check-Method "PlayerCamera"      "SetMoveValues"        @("Single", "Single", "Single")
Check-Method "ProjectileManager" "UpdateProjectileScan" @("Projectile", "ProjectileType")
Check-Method "ProjectileManager" "AddToRemoveQueue"     @("Projectile")
Check-Field  "ProjectileType"    "ProjectilesToRemove"  "public"
Check-Field  "ProjectileType"    "WidthRadius"          "public"
Check-Field  "Projectile"        "Position"             "public"
Check-Field  "Projectile"        "Velocity"             "public"
Check-Field  "Projectile"        "CatchingUpToDo"       "public"
Check-Method "Boat"              "ApplyInputForce"      @()
Check-Method "Boat"              "ApplyReturnForce"     @()
Check-Method "Boat"              "FixedUpdate"          @()
Check-Method "Boat"              "get_HiddenPhysicsRig" @()
Check-Method "Boat"              "get_Velocity"         @()
Check-Method "Boat"              "get_Driver"           @()
Check-Field  "Boat"              "_curMotor"            $null
Check-Method "BoatMotor"         "get_Force"            @()
Check-Method "BoatMotor"         "get_Propeller"        @()
Check-Method "WaterManager"      "GetWaterHeight"       @("Vector3")
Check-Method "WaterManager"      "IsUnderWater"         @("Vector3")
Check-Method "Player"            "LocalTeleport"        @("Vector3", "Single", "Boolean")
Check-Method "Server"            "HitPlayer"            @("Player", "Int32", "Vector3", "Vector3", "Byte", "Player")
Check-Method "VFXManager"        "Play"                 @("String", "Vector3", "Vector3")
Check-Method "AudioManager"      "PlayGlobalClip"       @("String", "Boolean", "Single", "Single", "Boolean")
Check-Method "AudioManager"      "PlayRandomGlobalClip" @("String", "Int32", "Int32", "Boolean", "Single", "Single")
Check-Method "DazedUtils"        "PlayCreatureHitEffects" @("Vector3", "Vector3", "Int32", "Single", "Boolean", "Boolean", "Int32", "Player")
Check-Method "PlayerScreenShake" "Shake"                @("Single", "Int32", "Vector2")
Check-Method "Item"              "get_HasPlayerHolder"  @()
Check-Method "PlayerInventory"   "ServerDropAll"        @("Vector3", "Quaternion")
Check-Field  "PlayerInventory"   "_player"              $null
Check-Method "BoatManager"       "TryMoveBoat"          @("Vector3", "Quaternion")
Check-Field  "Island"            "IslandPos"            "public"
Check-Field  "Island"            "IslandSize"           "public"
Check-Field  "SpawnManager"      "BoatSpawnRot"         "public"
$sda = (Get-Type "PlayerInventory").Methods | Where-Object { $_.Name -eq "ServerDropAll" } | Select-Object -First 1
if ($sda -and ($sda.Parameters | Where-Object { $_.Name -eq "pos" })) { Write-Host "  ok    PlayerInventory.ServerDropAll has parameter 'pos'" -ForegroundColor Green }
else { Write-Host "  FAIL  PlayerInventory.ServerDropAll lacks parameter 'pos'" -ForegroundColor Red; $fail++ }
$ups = (Get-Type "ProjectileManager").Methods | Where-Object { $_.Name -eq "UpdateProjectileScan" } | Select-Object -First 1
$upn = @($ups.Parameters | ForEach-Object { $_.Name })
if (($upn -contains "projectile") -and ($upn -contains "type")) { Write-Host "  ok    ProjectileManager.UpdateProjectileScan has (projectile, type)" -ForegroundColor Green }
else { Write-Host "  FAIL  ProjectileManager.UpdateProjectileScan parameters are ($($upn -join ', ')), patch expects (projectile, type)" -ForegroundColor Red; $fail++ }

Write-Host "`n-- Harmony argument names (pirates) --"
foreach ($spec in @(@("ExplosionManager","ServerExplode",@("item","info")), @("ProjectileManager","Hit",@("projectile","hit")))) {
    $mm = (Get-Type $spec[0]).Methods | Where-Object { $_.Name -eq $spec[1] } | Select-Object -First 1
    $have = @($mm.Parameters | ForEach-Object { $_.Name })
    $missing = @($spec[2] | Where-Object { $have -notcontains $_ })
    if ($missing.Count -eq 0) { Write-Host "  ok    $($spec[0]).$($spec[1]) has ($($spec[2] -join ', '))" -ForegroundColor Green }
    else { Write-Host "  FAIL  $($spec[0]).$($spec[1]) lacks ($($missing -join ', '))" -ForegroundColor Red; $fail++ }
}

Write-Host "`n-- Harmony argument names --"
$td = (Get-Type "PlayerVitals").Methods | Where-Object { $_.Name -eq "TakeDamage" } | Select-Object -First 1
$pn = @($td.Parameters | ForEach-Object { $_.Name })
if ($pn -contains "amount") { Write-Host "  ok    PlayerVitals.TakeDamage has parameter 'amount' ($($pn -join ', '))" -ForegroundColor Green }
else { Write-Host "  FAIL  PlayerVitals.TakeDamage parameters are ($($pn -join ', ')), patch expects 'amount'" -ForegroundColor Red; $fail++ }
$sb = (Get-Type "BirdManager").Methods | Where-Object { $_.Name -eq "SimulateBird" } | Select-Object -First 1
$pn = @($sb.Parameters | ForEach-Object { $_.Name })
if ($pn -contains "bird") { Write-Host "  ok    BirdManager.SimulateBird has parameter 'bird'" -ForegroundColor Green }
else { Write-Host "  FAIL  BirdManager.SimulateBird parameters are ($($pn -join ', ')), patch expects 'bird'" -ForegroundColor Red; $fail++ }

Write-Host ""
if ($fail -gt 0) { Write-Host "$fail check(s) FAILED" -ForegroundColor Red; exit 1 }
Write-Host "All patch targets and game members verified." -ForegroundColor Green
