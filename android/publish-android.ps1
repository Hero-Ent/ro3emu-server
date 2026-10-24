# Publishes the Android app's game data (and optionally the APK) into patch-server\repo, from the PC release already
# there: its StreamingAssets chunks are reused as they are (nothing is re-read), the bundles retargeted to Android
# (build\vfs-android) are added, the VFS videos left out (no video player on Android), and the channel is signed.
#   test:       .\android\publish-android.ps1                       (channel android-test, from the -local PC release)
#   production: .\android\publish-android.ps1 -Channel android-live -Apk -ApkVersion 1.0.1 [-ApkMin 1.0.1]
#               then ENVIAR_ATUALIZACAO.cmd uploads the new chunks, the manifest, the APK and the channel to R2.
param(
  [string]$Channel = 'android-test',
  # PC release to take StreamingAssets from; default: the last published client (release-state.json), "-local" for test.
  [string]$Base,
  [switch]$Apk,
  [string]$ApkVersion,
  [string]$ApkMin,
  [string]$ApkFile = (Join-Path $PSScriptRoot 'out\ro3emu.apk'),
  [string]$Repo = (Join-Path (Split-Path $PSScriptRoot -Parent) 'patch-server\repo'),
  [string]$Key = (Join-Path (Split-Path $PSScriptRoot -Parent) 'android-signing-key.pem')
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$test = $Channel -ne 'android-live'
if (-not $Base) {
  $Base = (Get-Content (Join-Path $root 'patch-server\release-state.json') -Raw | ConvertFrom-Json).clientVersion
  if ($test) { $Base += '-local' }
}
if (-not (Test-Path (Join-Path $Repo "manifests\$Base.json.zst"))) { throw "no PC release $Base in $Repo" }

# Releases are immutable: the next free <pc version>-android<n> (test ones end in -local, which upload_to_r2.py skips).
$stem = ($Base -replace '-local$', '') + '-android'
$n = 1
do { $Version = "$stem$n$(if ($test) { '-local' })"; $n++ } while (Test-Path (Join-Path $Repo "manifests\$Version.json.zst"))

dotnet build (Join-Path $root 'src\Ro3Emu.ClientTools') -v q | Out-Null
if ($LASTEXITCODE) { throw 'build ClientTools failed' }
$tools = Join-Path $root 'src\Ro3Emu.ClientTools\bin\Debug\net10.0\win-x64\Ro3Emu.ClientTools.exe'
if (-not (Test-Path $Key)) {
  & $tools android-key $Key | Out-Null
  Write-Warning "created the signing key ${Key}: back it up; rerun setup-project.ps1 -PatchRepo so the app carries its public half"
}

$toolArgs = @('release-android', $Repo, $Base, $Version, $Channel, '--key', $Key)
$overlay = Join-Path $PSScriptRoot 'build\vfs-android'
if (Test-Path $overlay) { $toolArgs += '--overlay', $overlay } else { Write-Warning "no ${overlay}: the phone retargets the bundles at runtime" }
if ($Apk) {
  if (-not $ApkVersion) { throw '-Apk needs -ApkVersion (the -Version build-apk.ps1 was given)' }
  if (-not (Test-Path $ApkFile)) { throw "no APK at $ApkFile" }
  $toolArgs += '--apk', $ApkFile, '--apk-version', $ApkVersion
}
if ($ApkMin) { $toolArgs += '--apk-min', $ApkMin }
& $tools @toolArgs
if ($LASTEXITCODE) { throw 'release-android failed' }
Write-Host "published $Version on $Channel$(if (-not $test) { ': run ENVIAR_ATUALIZACAO.cmd to upload' })"
