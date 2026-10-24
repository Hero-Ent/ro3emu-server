# Creates the key every published APK is signed with (android-release.keystore, its password in
# android-release.keystore.pass, both at the repository root and never committed). Android only installs an update
# signed by the same key as the installed app: lose it and every player has to uninstall (and download the 20 GB again).
# Back both files up somewhere safe, next to android-signing-key.pem.
param(
  [string]$Keystore = (Join-Path (Split-Path $PSScriptRoot -Parent) 'android-release.keystore'),
  [string]$Unity = 'C:\Dev\Unity\Editors\2022.3.62f3\Editor\Unity.exe'
)
$ErrorActionPreference = 'Stop'
if (Test-Path $Keystore) { throw "$Keystore already exists; a new key would stop players' updates" }
$keytool = Join-Path (Split-Path $Unity) 'Data\PlaybackEngines\AndroidPlayer\OpenJDK\bin\keytool.exe'
$bytes = New-Object byte[] 24
[Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes)
$password = [Convert]::ToBase64String($bytes) -replace '[+/=]', 'x'
[IO.File]::WriteAllText("$Keystore.pass", $password)
& $keytool -genkeypair -keystore $Keystore -storetype PKCS12 -alias herosaga -keyalg RSA -keysize 4096 -validity 20000 `
  -storepass $password -keypass $password -dname 'CN=Hero Saga, O=RO3Emu'
if ($LASTEXITCODE) { throw 'keytool failed' }
Write-Host "created $Keystore (password in $Keystore.pass): back both up"
