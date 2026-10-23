# Installs android\out\ro3emu.apk on the phone, starts it, waits -Seconds and saves the Unity log (out\run.log), the
# distinct non-stack-trace lines (out\run-summary.txt) and a screenshot (out\run.png). Reconnects wireless debugging
# first: Unity's batch build restarts the adb server, which drops wireless devices. -Device ip:port is remembered in
# build\device.txt for the next runs.
param(
  [string]$Device,
  [int]$Seconds = 60,
  [switch]$NoInstall,
  [string]$Unity = 'E:\Editors\2022.3.62f3\Editor\Unity.exe'
)
$ErrorActionPreference = 'Continue'
$adb = Join-Path (Split-Path $Unity) 'Data\PlaybackEngines\AndroidPlayer\SDK\platform-tools\adb.exe'
$out = Join-Path $PSScriptRoot 'out'
$remembered = Join-Path $PSScriptRoot 'build\device.txt'
if ($Device) { Set-Content -Path $remembered -Value $Device -Encoding ascii }
elseif (Test-Path $remembered) { $Device = (Get-Content $remembered -Raw).Trim() }

if ($Device -match ':') {
  $state = (& $adb -s $Device get-state 2>$null)
  if ($state -ne 'device') { & $adb disconnect $Device 2>&1 | Out-Null; & $adb connect $Device 2>&1 | Write-Host }
  $target = @('-s', $Device)
} else { $target = @() }
if ((& $adb @target get-state 2>$null) -ne 'device') { throw "phone not reachable over adb ($Device); check the IP:port on the Wireless debugging screen" }

if (-not $NoInstall) {
  Write-Host '== install'
  & $adb @target install -r (Join-Path $out 'ro3emu.apk') 2>&1 | Select-Object -Last 1 | Write-Host
}
Write-Host "== run ($Seconds s)"
# A sleeping screen pauses the player and makes black screenshots.
& $adb @target shell input keyevent KEYCODE_WAKEUP
& $adb @target shell am force-stop com.ro3emu.client
& $adb @target logcat -c
& $adb @target shell monkey -p com.ro3emu.client -c android.intent.category.LAUNCHER 1 2>&1 | Out-Null
Start-Sleep -Seconds $Seconds
$log = Join-Path $out 'run.log'
& $adb @target logcat -d -v brief Unity:V fsni:V AndroidRuntime:E DEBUG:V libc:F CRASH:V '*:S' 2>&1 | Out-File -Encoding utf8 $log
$alive = (& $adb @target shell pidof com.ro3emu.client)
& $adb @target shell input keyevent KEYCODE_WAKEUP
Start-Sleep -Seconds 2
& $adb @target shell screencap -p /sdcard/ro3shot.png
& $adb @target pull /sdcard/ro3shot.png (Join-Path $out 'run.png') 2>&1 | Out-Null

# Distinct messages, without stack frames, Unity's source locations and the per-frame repeats.
$seen = @{}
$summary = foreach ($line in Get-Content $log) {
  $m = $line -replace '^[VDIWEF]/\S+\s*\(\s*\d+\):\s?', '' -replace '<color=#[0-9A-Fa-f]+>|</color>', ''
  if ($m -match '^\s*$|^\s+at |^\(Filename:|^\[\./|^[\w.<>`\[\],$]+:[\w<>$]+\(|Vulkan init') { continue }
  $key = $m -replace '^\d\d/\d\d/\d{4} [\d:]+', ''
  if ($seen.ContainsKey($key)) { $seen[$key]++; continue }
  $seen[$key] = 1
  $m
}
$summary | Set-Content -Encoding utf8 (Join-Path $out 'run-summary.txt')
Write-Host "== process $(if ($alive) { "alive (pid $alive)" } else { 'NOT running' }); $(@($summary).Count) distinct messages in out\run-summary.txt"
$seen.GetEnumerator() | Where-Object { $_.Value -gt 20 } | Sort-Object Value -Descending | Select-Object -First 5 | ForEach-Object { Write-Host "   repeated x$($_.Value): $($_.Key.Substring(0, [Math]::Min(150, $_.Key.Length)))" }
