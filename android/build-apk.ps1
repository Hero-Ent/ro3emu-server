# Builds android\out\ro3emu.apk from android\Ro3Android with Unity in batchmode (run setup-project.ps1 first). The full
# Unity log goes to android\out\build.log; -Install also installs it on the phone connected over adb.
# Gradle's and the Package Manager's caches (several GB) go to -Cache instead of the user profile on C:.
param(
  [string]$Unity = 'C:\Dev\Unity\Editors\2022.3.62f3\Editor\Unity.exe',
  [string]$Project = (Join-Path $PSScriptRoot 'Ro3Android'),
  [string]$Cache = 'C:\Dev\Unity\cache',
  [switch]$Install,
  # Only C# changed since the last full build (shell scripts, hooks, game DLLs): Unity skips scenes and assets.
  [switch]$ScriptsOnly,
  # A release player: no "Development Build" mark (screenshots, players). Switching between the two needs a full build.
  [switch]$Release,
  # GLES3 as well as Vulkan, first: the test APK for the PC's Android emulator (its Vulkan does not work there). Players'
  # APKs stay Vulkan only (half the shader build time).
  [switch]$Gles,
  # x86_64 instead of ARM64: the emulator's test APK (run setup-project.ps1 -X64 first). Never for players.
  [switch]$X64,
  # The app version (PlayerSettings.bundleVersion): publish-android.ps1 -ApkMin forces phones below it to update.
  [string]$Version = '1.0.0',
  # Release signing key (android\new-keystore.ps1 makes it; never committed). Players can only update an app signed by
  # the same key, so every published APK must use it. Absent: Unity's per-PC debug key (fine for tests).
  [string]$Keystore = (Join-Path (Split-Path $PSScriptRoot -Parent) 'android-release.keystore')
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
if ($Release) { $unityArgs += '-ro3Release' }
if ($Gles) { $unityArgs += '-ro3Gles' }
if ($X64) { $unityArgs += '-ro3X64' }
$unityArgs += '-ro3Version', $Version
if (Test-Path $Keystore) { $unityArgs += '-ro3Keystore', "`"$Keystore`"", '-ro3KeystorePass', "`"$Keystore.pass`"" }
elseif ($Release) { Write-Warning "no ${Keystore}: this release is signed with the debug key, which players cannot update from later" }
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
