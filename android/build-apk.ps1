# Builds android\out\ro3emu.apk from android\Ro3Android with Unity in batchmode (run setup-project.ps1 first). The full
# Unity log goes to android\out\build.log; -Install also installs it on the phone connected over adb.
# Gradle's and the Package Manager's caches (several GB) go to -Cache instead of the user profile on C:.
param(
  [string]$Unity = 'E:\Editors\2022.3.62f3\Editor\Unity.exe',
  [string]$Project = (Join-Path $PSScriptRoot 'Ro3Android'),
  [string]$Cache = 'E:\Unity\cache',
  [switch]$Install,
  # Only C# changed since the last full build (shell scripts, hooks, game DLLs): Unity skips scenes and assets.
  [switch]$ScriptsOnly
)
$ErrorActionPreference = 'Stop'
if (-not (Test-Path $Unity)) { throw "Unity 2022.3.62f3 not found at $Unity (install it with Android Build Support, or pass -Unity)" }
$env:GRADLE_USER_HOME = Join-Path $Cache 'gradle'
$env:UPM_CACHE_ROOT = Join-Path $Cache 'upm'
if (-not (Test-Path (Join-Path $Project 'Assets\Plugins\Game'))) { throw 'run .\android\setup-project.ps1 first' }
$out = Join-Path $PSScriptRoot 'out'
New-Item -ItemType Directory -Force $out | Out-Null
$log = Join-Path $out 'build.log'

$unityArgs = @('-batchmode', '-nographics', '-quit', '-buildTarget', 'Android',
  '-projectPath', "`"$Project`"", '-executeMethod', 'Ro3Android.Editor.BuildAndroid.Build', '-logFile', "`"$log`"")
if ($ScriptsOnly) { $unityArgs += '-ro3ScriptsOnly' }
$p = Start-Process -FilePath $Unity -Wait -PassThru -NoNewWindow -ArgumentList $unityArgs
Select-String -Path $log -Pattern '\[Ro3Android\]|error CS|Il2Cpp.*(error|failed)|BuildFailedException' | Select-Object -Last 40 | ForEach-Object { $_.Line }
if ($p.ExitCode) { throw "Unity build failed (exit $($p.ExitCode)); see $log" }

$apk = Join-Path $out 'ro3emu.apk'
Write-Host "APK: $apk"
if ($Install) {
  $adb = Join-Path (Split-Path $Unity) 'Data\PlaybackEngines\AndroidPlayer\SDK\platform-tools\adb.exe'
  & $adb install -r $apk
  Write-Host 'logs: adb logcat -s Unity'
}
