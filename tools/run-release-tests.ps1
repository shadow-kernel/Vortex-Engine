# Vortex v2.7 Release-Regressionslauf: startet jede Headless-Testszene im Player, wartet auf die
# Ergebnisdatei, sammelt PASS/FAIL. Danach (Schalter -Visual) werden die F12-Sichtpruefszenen
# nacheinander gestartet (manuell ansehen, Fenster schliesst nach TimeoutSec automatisch).
#
#   powershell -ExecutionPolicy Bypass -File _baseline\run-release-tests.ps1            # nur automatisch
#   powershell -ExecutionPolicy Bypass -File _baseline\run-release-tests.ps1 -Visual    # + Sichtszenen
#
# Voraussetzung: WaveTest-Projekt unter %USERPROFILE%\VortexEngineProjects\WaveTest und ein
# frischer Release-Build (x64\Release\Vortex Engine.exe).
param([switch]$Visual, [int]$TimeoutSec = 90)

$exe = Join-Path $PSScriptRoot "..\x64\Release\Vortex Engine.exe"
$wt  = "$env:USERPROFILE\VortexEngineProjects\WaveTest"
if (-not (Test-Path $exe)) { Write-Host "FEHLT: $exe (Release bauen!)" -ForegroundColor Red; exit 1 }
if (-not (Test-Path $wt))  { Write-Host "FEHLT: $wt (WaveTest-Projekt)" -ForegroundColor Red; exit 1 }

# Szene -> Ergebnisdatei in %TEMP% (jedes Harness schreibt PASS/FAIL pro Zeile und beendet sich)
$auto = [ordered]@{
  "WaveA"         = "wavetest_results.txt"      # Scripting-Welle: Raycast/Instantiate/Coroutines/Events/Save (34)
  "SetActiveTest" = "setactive_results.txt"     # SetActive + Renderer/Collider-Toggles (11)
  "StairTest"     = "stairtest_results.txt"     # Treppen/Slope/Ground-Snap (7)
  "FieldTest"     = "fieldtest_results.txt"     # Script-Feld-Serialisierung (8)
  "SocketTest"    = "sockettest_results.txt"    # Bone-Sockets + Attach-API (13)
  "LayerTest"     = "layertest_results.txt"     # Bone-Masken-Layer (9)
  "SyncTest"      = "synctest_results.txt"      # Synced-Playback-Gruppen (8)
  "CamFxTest"     = "camfxtest_results.txt"     # CameraFX Kick/Sway/Spring (7)
}

$totalPass = 0; $totalFail = 0; $failedScenes = @()
foreach ($scene in $auto.Keys) {
  $res = Join-Path $env:TEMP $auto[$scene]
  if (Test-Path $res) { Remove-Item $res -Force }
  Write-Host ("--- " + $scene + " ...") -NoNewline
  $p = Start-Process -FilePath $exe -ArgumentList "--project=`"$wt`"", "--scene=`"$scene`"" -PassThru
  $deadline = (Get-Date).AddSeconds($TimeoutSec)
  while ((Get-Date) -lt $deadline -and -not (Test-Path $res)) { Start-Sleep -Seconds 2 }
  Start-Sleep -Seconds 1
  Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
  if (-not (Test-Path $res)) {
    Write-Host " KEINE ERGEBNISDATEI (Timeout/Crash)" -ForegroundColor Red
    $totalFail++; $failedScenes += $scene
    continue
  }
  $lines = Get-Content $res
  $pass = @($lines | Where-Object { $_ -like "PASS*" }).Count
  $fail = @($lines | Where-Object { $_ -like "FAIL*" }).Count
  $totalPass += $pass; $totalFail += $fail
  if ($fail -gt 0) {
    Write-Host (" " + $pass + " PASS / " + $fail + " FAIL") -ForegroundColor Red
    $lines | Where-Object { $_ -like "FAIL*" } | ForEach-Object { Write-Host ("      " + $_) -ForegroundColor Red }
    $failedScenes += $scene
  } else {
    Write-Host (" " + $pass + " PASS") -ForegroundColor Green
  }
}

Write-Host ""
if ($totalFail -eq 0) {
  Write-Host ("ALLE AUTOMATISCHEN TESTS GRUEN: " + $totalPass + " Assertions.") -ForegroundColor Green
} else {
  Write-Host ("ROT: " + $totalFail + " Fehler in: " + ($failedScenes -join ", ")) -ForegroundColor Red
}

if ($Visual) {
  # Sichtpruefungen: jede Szene oeffnet sich, ansehen (Sollbild siehe docs/wiki/Release-Test-Plan-v2.7.md),
  # Fenster schliesst nach 20 s selbst. F12 speichert jederzeit ein Vergleichsbild nach ~\Pictures.
  $visualScenes = @(
    "BloomTest","BloomOff","GradeTest","AoTest","AoOff","CsmTest","PointShadowTest",
    "GlassTest","MipTest","VmTest","VmOff","VuiTest"
  )
  foreach ($scene in $visualScenes) {
    Write-Host ("[Sichtpruefung] " + $scene + " (20 s) ...")
    $p = Start-Process -FilePath $exe -ArgumentList "--project=`"$wt`"", "--scene=`"$scene`"" -PassThru
    Start-Sleep -Seconds 20
    Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
  }
}

exit ([int]($totalFail -gt 0))
