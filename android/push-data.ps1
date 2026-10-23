# Copies the PC client's StreamingAssets (the 24 GB VFS included) to the phone, where the Android port reads it:
# /sdcard/Android/data/com.ro3emu.client/files/StreamingAssets (Ro3Android.Hooks.Paths). Install and open the APK once
# first, so Android creates the app's folder. Pushes use --sync (only missing or newer files) and retry after a dropped
# connection (wireless debugging drops when the phone sleeps), so rerunning resumes where it stopped.
param(
  [string]$Client = (Join-Path (Split-Path $PSScriptRoot -Parent) 'Client\Ragnarok3'),
  [string]$Unity = 'E:\Editors\2022.3.62f3\Editor\Unity.exe',
  [string]$Package = 'com.ro3emu.client',
  [switch]$SmallOnly
)
$ErrorActionPreference = 'Stop'
$adb = Join-Path (Split-Path $Unity) 'Data\PlaybackEngines\AndroidPlayer\SDK\platform-tools\adb.exe'
if (-not (Test-Path $adb)) { throw "adb not found at $adb" }
$source = Join-Path $Client 'ro3_Data\StreamingAssets'
$target = "/sdcard/Android/data/$Package/files/StreamingAssets"

$ErrorActionPreference = 'Continue'   # adb reports progress on stderr
& $adb get-state | Out-Null
if ($LASTEXITCODE) { throw 'no phone over adb (enable USB debugging and accept the prompt on the phone)' }
$serial = (& $adb get-serialno).Trim()

function Push([string]$from, [string]$to) {
  for ($try = 1; $try -le 20; $try++) {
    & $adb push --sync $from $to
    if ($LASTEXITCODE -eq 0) { return }
    Write-Warning "push $from failed (try $try); reconnecting"
    if ($serial -match ':') { & $adb disconnect $serial | Out-Null; Start-Sleep 3; & $adb connect $serial | Out-Null }
    Start-Sleep 5
  }
  throw "push $from failed 20 times"
}

& $adb shell mkdir -p $target
foreach ($item in Get-ChildItem $source) {
  if ($SmallOnly -and $item.Name -eq 'VFS') { Write-Host 'skipping VFS (-SmallOnly)'; continue }
  Write-Host "== $($item.Name)"
  Push $item.FullName "$target/"
}
# The PC bundles target StandaloneWindows64, which the Android player refuses: their retargeted copies
# (tools\BundleTool.cs retarget <VFS> build\vfs-android) go over the originals.
$overlay = Join-Path $PSScriptRoot 'build\vfs-android'
if (-not $SmallOnly -and (Test-Path $overlay)) {
  Write-Host '== VFS overlay (retargeted bundles)'
  # Not --sync: the originals pushed above are newer than these copies on the phone, so force them over.
  foreach ($dir in Get-ChildItem $overlay -Directory) {
    & $adb push $dir.FullName "$target/VFS/"
    if ($LASTEXITCODE) { throw "push overlay $($dir.Name) failed" }
  }
  foreach ($f in Get-ChildItem $overlay -File) { & $adb push $f.FullName "$target/VFS/" | Out-Null }
} elseif (-not $SmallOnly) {
  Write-Warning 'no build\vfs-android: run "dotnet run android\tools\BundleTool.cs -- retarget <VFS> android\build\vfs-android" first'
}
Write-Host "done: $target"
