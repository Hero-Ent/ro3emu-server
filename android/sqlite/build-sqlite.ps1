# Builds libsqlite3.so (arm64-v8a) from SQLite's official amalgamation. The client's Mono.Data.Sqlite P/Invokes
# "sqlite3" (SqliteAccess, the game's local database: skill plans and settings); the PC client ships sqlite3.dll, the
# Android player had nothing, so every query failed with "Database is not open" (the skill window's options tab, its
# close button). The zip is checked against the SHA3-256 sqlite.org publishes on its download page; the sources go to
# android\build\third_party (gitignored), the library to android\build\native-prebuilt, where setup-project.ps1 picks it up.
# -X64: x86_64 for the PC's Android emulator, into build\native-prebuilt\x86_64.
param([string]$Unity = 'C:\Dev\Unity\Editors\2022.3.62f3\Editor\Unity.exe', [switch]$X64)
$ErrorActionPreference = 'Stop'
$android = Split-Path $PSScriptRoot -Parent
$version = '3540100'
$url = "https://www.sqlite.org/2026/sqlite-amalgamation-$version.zip"
$sha3 = '92281c86e2957f9b9305890f9ac04204caa7b677cc868dc326d942689df28e30'

$ndk = Join-Path (Split-Path $Unity) 'Data\PlaybackEngines\AndroidPlayer\NDK'
$clang = Join-Path $ndk 'toolchains\llvm\prebuilt\windows-x86_64\bin\clang.exe'
$python = Join-Path $ndk 'toolchains\llvm\prebuilt\windows-x86_64\python3\python.exe'
if (-not (Test-Path $clang)) { throw "NDK clang not found at $clang" }

$third = Join-Path $android 'build\third_party\sqlite'
New-Item -ItemType Directory -Force $third | Out-Null
$zip = Join-Path $third "sqlite-amalgamation-$version.zip"
if (-not (Test-Path $zip)) {
  [Net.ServicePointManager]::SecurityProtocol = 'Tls12'
  Invoke-WebRequest $url -UseBasicParsing -OutFile $zip
}
# Windows PowerShell has no SHA3: the NDK's Python computes it.
$hash = & $python -I -c "import hashlib,sys;print(hashlib.sha3_256(open(sys.argv[1],'rb').read()).hexdigest())" $zip
if ($hash -ne $sha3) { Remove-Item $zip; throw "SHA3-256 of $zip is $hash, expected $sha3" }
$src = Join-Path $third "sqlite-amalgamation-$version"
if (-not (Test-Path (Join-Path $src 'sqlite3.c'))) { Expand-Archive -Force $zip $third }

$target = if ($X64) { 'x86_64-linux-android26' } else { 'aarch64-linux-android26' }
$out = Join-Path $android 'build\native-prebuilt'
if ($X64) { $out = Join-Path $out 'x86_64' }
New-Item -ItemType Directory -Force $out | Out-Null
# Column metadata: Mono.Data.Sqlite's schema calls (sqlite3_column_database_name, ...) need it. 16 KB pages: Android 15+.
& $clang "--target=$target" -shared -fPIC -O2 -DSQLITE_THREADSAFE=1 -DSQLITE_ENABLE_COLUMN_METADATA `
  -DSQLITE_DEFAULT_FOREIGN_KEYS=0 -DSQLITE_OMIT_LOAD_EXTENSION '-Wl,-z,max-page-size=16384' `
  (Join-Path $src 'sqlite3.c') -o (Join-Path $out 'libsqlite3.so') -lm -llog
if ($LASTEXITCODE) { throw 'clang failed' }
Write-Host "built $out\libsqlite3.so (SQLite $version)"
