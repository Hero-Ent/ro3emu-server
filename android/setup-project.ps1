# Builds (or refreshes) the Unity Android project in android\Ro3Android from your PC client: copies the client's managed
# DLLs to a staging client, patches them for -Server (an address the PHONE reaches: the PC's LAN IP or the VPS), then
# installs them as precompiled plugins next to the shell scripts from android\unity-template. Nothing of the game is
# committed: Assets\Plugins\Game and the staging folder are gitignored.
param(
  [Parameter(Mandatory)][string]$Server,
  [string]$Client = (Join-Path (Split-Path $PSScriptRoot -Parent) 'Client\Ragnarok3'),
  [string]$Pem = (Join-Path (Split-Path $PSScriptRoot -Parent) 'Client\RO3Emu\data\gate_rsa_public.pem'),
  [string]$Project = (Join-Path $PSScriptRoot 'Ro3Android'),
  [string]$Unity = 'C:\Dev\Unity\Editors\2022.3.62f3\Editor\Unity.exe',
  # AssetRipper export of the PC player (see README): its assets go into the project and its DLL GUIDs into our metas.
  [string]$Export = (Join-Path $PSScriptRoot 'build\arexport\ExportedProject')
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
if ($Server -match '://(127\.0\.0\.1|localhost)') { Write-Warning "$Server is the phone itself on Android; use the PC's LAN IP or the VPS" }
foreach ($p in $Client, $Pem) { if (-not (Test-Path $p)) { throw "not found: $p" } }

# Unity provides these itself: the engine modules and its own Mono class libraries.
$engine = '^(UnityEngine(\.[A-Za-z0-9]+Module)?|mscorlib|netstandard|System|System\.(Configuration|Core|Data|Drawing|IO\.Compression(\.FileSystem)?|Net\.Http|Numerics|Runtime\.Serialization|Transactions|Xml(\.Linq)?)|Mono\.Security)\.dll$'

Write-Host '== building ClientHooks, Loc and ClientTools'
foreach ($p in 'Ro3Emu.ClientHooks', 'Ro3Emu.Loc') {
  dotnet build (Join-Path $root "src\$p") -v q | Out-Null
  if ($LASTEXITCODE) { throw "build $p failed" }
}
dotnet build (Join-Path $root 'src\Ro3Emu.ClientTools') --no-incremental -v q | Out-Null
if ($LASTEXITCODE) { throw 'build ClientTools failed' }
$tools = Join-Path $root 'src\Ro3Emu.ClientTools\bin\Debug\net10.0\win-x64\Ro3Emu.ClientTools.exe'

Write-Host "== staging and patching the client DLLs for $Server"
$stage = Join-Path $PSScriptRoot 'build\stage'
if (Test-Path $stage) { Remove-Item -Recurse -Force $stage }
New-Item -ItemType Directory -Force (Join-Path $stage 'ro3_Data') | Out-Null
Copy-Item -Recurse (Join-Path $Client 'ro3_Data\Managed') (Join-Path $stage 'ro3_Data\Managed')
$backup = Join-Path $Client 'ro3emu-backup'
if (Test-Path $backup) { Copy-Item -Recurse $backup (Join-Path $stage 'ro3emu-backup') }
New-Item -ItemType File (Join-Path $stage 'ro3.exe') | Out-Null  # the patcher only checks that it is a client folder
# --verbose: GS_Debug.ShowLog always on, so the game's C# and Lua logs reach logcat while the port is young.
& $tools patch $stage $Server.TrimEnd('/') $Pem --verbose
if ($LASTEXITCODE) { throw 'patch failed' }

Write-Host '== Android rewrites (android\tools\AndroidPatch.cs)'
dotnet build (Join-Path $PSScriptRoot 'hooks') -v q | Out-Null
if ($LASTEXITCODE) { throw 'build Ro3Android.Hooks failed' }
$hooks = Join-Path $PSScriptRoot 'hooks\bin\Debug\netstandard2.1\Ro3Android.Hooks.dll'
dotnet run (Join-Path $PSScriptRoot 'tools\AndroidPatch.cs') -- (Join-Path $stage 'ro3_Data\Managed') $hooks
if ($LASTEXITCODE) { throw 'android patch failed' }

Write-Host "== project $Project"
$template = Join-Path $PSScriptRoot 'unity-template'
foreach ($f in Get-ChildItem $template -Recurse -File) {
  $target = Join-Path $Project $f.FullName.Substring($template.Length + 1)
  New-Item -ItemType Directory -Force (Split-Path $target) | Out-Null
  Copy-Item -Force $f.FullName $target
}
# A lock left by a Unity-made default manifest would bring its packages (TextMeshPro, uGUI...) back; the game ships its own.
Remove-Item -Force (Join-Path $Project 'Packages\packages-lock.json') -ErrorAction SilentlyContinue
$game = Join-Path $Project 'Assets\Plugins\Game'
if (Test-Path $game) { Remove-Item -Recurse -Force $game }
New-Item -ItemType Directory -Force $game | Out-Null

$md5 = [System.Security.Cryptography.MD5]::Create()
$haveExport = Test-Path (Join-Path $Export 'Assets')
# Loaded all together into the editor's own Mono domain, the game's DLLs crash it (MonoManager ReloadAssembly), so they
# are Android-only - except the ones the exported assets need (the UI and TextMeshPro scripts of Resources, and what
# those DLLs reference), which the editor must load to import them.
$editorDlls = @{}
if ($haveExport) {
  $closure = dotnet run (Join-Path $PSScriptRoot 'tools\DllClosure.cs') -- (Join-Path $stage 'ro3_Data\Managed') UnityEngine.UI.dll Unity.TextMeshPro.dll
  if ($LASTEXITCODE) { throw 'DllClosure failed' }
  foreach ($n in $closure) { $editorDlls[$n.Trim()] = $true }
  Write-Host "editor loads $($editorDlls.Count) of the game DLLs"
}
$meta = @'
fileFormatVersion: 2
guid: {0}
PluginImporter:
  externalObjects: {{}}
  serializedVersion: 2
  iconMap: {{}}
  executionOrder: {{}}
  defineConstraints: []
  isPreloaded: 0
  isOverridable: 0
  isExplicitlyReferenced: 0
  validateReferences: 0
  platformData:
  - first:
      Android: Android
    second:
      enabled: 1
      settings: {{}}
  - first:
      Any:
    second:
      enabled: 0
      settings: {{}}
  - first:
      Editor: Editor
    second:
      enabled: {1}
      settings:
        DefaultValueInitialized: true
  userData:
  assetBundleName:
  assetBundleVariant:
'@
$names = @()
foreach ($dll in Get-ChildItem (Join-Path $stage 'ro3_Data\Managed') -Filter *.dll) {
  if ($dll.Name -match $engine) { continue }
  Copy-Item $dll.FullName $game
  # The export's GUID when there is one (its assets reference the scripts through it), else a stable one of our own.
  $exportMeta = Join-Path $Export "Assets\Plugins\$($dll.Name).meta"
  $guid = if ($haveExport -and (Test-Path $exportMeta)) { ((Select-String -Path $exportMeta -Pattern '^guid: ([0-9a-f]{32})').Matches[0].Groups[1].Value) }
          else { -join ($md5.ComputeHash([Text.Encoding]::UTF8.GetBytes("ro3android:" + $dll.Name)) | ForEach-Object { $_.ToString('x2') }) }
  $inEditor = if ($editorDlls.ContainsKey($dll.Name)) { 1 } else { 0 }
  [IO.File]::WriteAllText((Join-Path $game ($dll.Name + '.meta')), ($meta -f $guid, $inEditor) + "`n")
  $names += $dll.BaseName
}

# The PC player's own assets (Resources with TextMeshPro's settings and fonts, and what they reference), as exported
# by AssetRipper. Its Plugins are ours above; its scenes are the PC boot scenes the shell replaces. Shaders are
# AssetRipper's placeholders until the shader work (M4) replaces them.
$imported = Join-Path $Project 'Assets\PC'
if (Test-Path $imported) { Remove-Item -Recurse -Force $imported }
if ($haveExport) {
  New-Item -ItemType Directory -Force $imported | Out-Null
  foreach ($d in Get-ChildItem (Join-Path $Export 'Assets') -Directory) {
    if ($d.Name -in 'Plugins', 'Scenes', 'Scripts') { continue }
    Copy-Item -Recurse $d.FullName (Join-Path $imported $d.Name)
  }
  Write-Host "imported the PC player assets into Assets\PC"
  # The PC project's tags and layers (cameras and UI cull by layer; Lua sets tags like TransparentUI), physics, time,
  # input, navmesh areas and audio. Player, graphics and quality settings stay ours (BuildAndroid.cs sets them).
  foreach ($s in 'TagManager', 'DynamicsManager', 'Physics2DSettings', 'TimeManager', 'InputManager', 'NavMeshAreas', 'AudioManager') {
    Copy-Item -Force (Join-Path $Export "ProjectSettings\$s.asset") (Join-Path $Project 'ProjectSettings')
  }
}

# IL2CPP strips what nothing references statically; the game reaches most of its code through Lua and reflection.
$link = "<linker>`n" + (($names | Sort-Object | ForEach-Object { "  <assembly fullname=`"$_`" preserve=`"all`"/>" }) -join "`n") + "`n</linker>`n"
[IO.File]::WriteAllText((Join-Path $game 'link.xml'), $link)
Write-Host "installed $($names.Count) game assemblies in $game"

# Replacement shaders (Vulkan/GLES3) for the game's D3D11-only ones, under a Resources folder so they ship and
# Ro3Android.ShaderFixer / Ro3Android.Hooks.Shaders find them by name: Unity's built-in shaders listed in
# android\shaders\builtin.txt and TextMeshPro's (shaders\fetch-shaders.ps1 puts their sources in build\third_party).
$ro3Shaders = Join-Path $Project 'Assets\Ro3Shaders\Resources\Ro3Shaders'
if (Test-Path (Split-Path (Split-Path $ro3Shaders))) { Remove-Item -Recurse -Force (Split-Path (Split-Path $ro3Shaders)) }
New-Item -ItemType Directory -Force $ro3Shaders | Out-Null
$builtinSrc = Join-Path $PSScriptRoot 'build\third_party\builtin_shaders\src'
$tmpSrc = Join-Path $PSScriptRoot 'build\third_party\tmpess-out\Assets\TextMesh Pro\Shaders'
if ((Test-Path $builtinSrc) -and (Test-Path $tmpSrc)) {
  $wanted = @{}
  Get-Content (Join-Path $PSScriptRoot 'shaders\builtin.txt') | Where-Object { $_ -and -not $_.StartsWith('#') } | ForEach-Object { $wanted[$_.Trim()] = $true }
  $copied = 0
  foreach ($f in Get-ChildItem $builtinSrc -Recurse -Filter *.shader) {
    $m = Select-String -Path $f.FullName -Pattern '^\s*Shader\s+"([^"]+)"' | Select-Object -First 1
    if ($m -and $wanted.ContainsKey($m.Matches[0].Groups[1].Value)) { Copy-Item $f.FullName (Join-Path $ro3Shaders $f.Name); $copied++ }
  }
  New-Item -ItemType Directory -Force (Join-Path $ro3Shaders 'TMP') | Out-Null
  Copy-Item (Join-Path $tmpSrc '*') (Join-Path $ro3Shaders 'TMP')
  Write-Host "replacement shaders: $copied built-in + $(@(Get-ChildItem (Join-Path $ro3Shaders 'TMP') -Filter *.shader).Count) TextMeshPro"
} else { Write-Warning 'no shader sources in build\third_party: run .\android\shaders\fetch-shaders.ps1' }

# URP 14.0.12's own utility and post-processing shaders (the editor ships the package sources): their .hlsl libraries
# go to Assets\Ro3Shaders\Lib with the Packages/ include paths rewritten, the shaders to the Resources folder above,
# plus a copy under the game's name for those its URP fork renamed (android\shaders\urp-aliases.txt).
$packages = Join-Path (Split-Path $Unity) 'Data\Resources\PackageManager\BuiltInPackages'
$lib = Join-Path $Project 'Assets\Ro3Shaders\Lib'
function Copy-ShaderSource([string]$from, [string]$to) {
  $text = [IO.File]::ReadAllText($from) -replace 'Packages/com\.unity\.render-pipelines\.(universal-config|universal|core)/', 'Assets/Ro3Shaders/Lib/$1/'
  New-Item -ItemType Directory -Force (Split-Path $to) | Out-Null
  [IO.File]::WriteAllText($to, $text)
}
foreach ($pkg in 'core', 'universal', 'universal-config') {
  $root = Join-Path $packages "com.unity.render-pipelines.$pkg"
  foreach ($f in Get-ChildItem $root -Recurse -Include *.hlsl, *.cginc) {
    Copy-ShaderSource $f.FullName (Join-Path $lib "$pkg\$($f.FullName.Substring($root.Length + 1))")
  }
}
$aliases = @{}
Get-Content (Join-Path $PSScriptRoot 'shaders\urp-aliases.txt') | Where-Object { $_ -and -not $_.StartsWith('#') } | ForEach-Object { $a = $_.Split('|'); $aliases[$a[0]] = $a[1] }
$urpShaders = 0
foreach ($dir in 'Utils', 'PostProcessing') {
  # Some shaders include their folder's .hlsl by relative path (SMAA's SubpixelMorphologicalAntialiasingBridge.hlsl):
  # they go next to the shader copies, and next to the alias copies.
  foreach ($h in Get-ChildItem (Join-Path $packages "com.unity.render-pipelines.universal\Shaders\$dir") -Filter *.hlsl) {
    Copy-ShaderSource $h.FullName (Join-Path $ro3Shaders "URP\$dir\$($h.Name)")
    Copy-ShaderSource $h.FullName (Join-Path $ro3Shaders "URP\Aliases\$($h.Name)")
  }
  foreach ($f in Get-ChildItem (Join-Path $packages "com.unity.render-pipelines.universal\Shaders\$dir") -Filter *.shader) {
    # Under Resources every keyword combination is compiled (UberPost alone runs to tens of thousands of variants). The
    # game's post stack is its own (PostFX/PostUber, BloomPost...), so from PostProcessing only the shaders it uses
    # under a renamed alias come along.
    $packageName = (Select-String -Path $f.FullName -Pattern '^\s*Shader\s+"([^"]+)"' | Select-Object -First 1).Matches[0].Groups[1].Value
    if ($dir -eq 'PostProcessing' -and -not $aliases.ContainsKey($packageName)) { continue }
    # The deferred renderer's lighting (~30k variants per graphics API); the game renders forward.
    if ($f.Name -eq 'StencilDeferred.shader') { continue }
    $target = Join-Path $ro3Shaders "URP\$dir\$($f.Name)"
    Copy-ShaderSource $f.FullName $target
    $urpShaders++
    $name = (Select-String -Path $target -Pattern '^\s*Shader\s+"([^"]+)"' | Select-Object -First 1).Matches[0].Groups[1].Value
    if ($aliases.ContainsKey($name)) {
      $alias = Join-Path $ro3Shaders "URP\Aliases\$($f.BaseName).shader"
      Copy-ShaderSource $target $alias
      $text = [IO.File]::ReadAllText($alias)
      [IO.File]::WriteAllText($alias, $text.Replace("Shader `"$name`"", "Shader `"$($aliases[$name])`""))
      # The game only asks for these under its own name: the package-named copy would double their variants.
      if ($dir -eq 'PostProcessing') { Remove-Item $target; $urpShaders-- }
      $urpShaders++
    }
  }
}
Write-Host "replacement shaders: $urpShaders from URP 14 (Utils, PostProcessing, aliases)"

# Names of the game's shaders (its shader bundle and the PC player's own, from the AssetRipper exports): they only have
# D3D11 programs, and many still claim isSupported on Android, so ShaderFixer goes by this list.
$pcShaderNames = foreach ($dir in (Join-Path $PSScriptRoot 'build\arshaders'), $Export) {
  if (Test-Path $dir) {
    Get-ChildItem $dir -Recurse -Filter *.shader | Select-String -Pattern '^\s*Shader\s+"([^"]+)"' | ForEach-Object { $_.Matches[0].Groups[1].Value }
  }
}
$pcShaderNames = @($pcShaderNames | Sort-Object -Unique)
[IO.File]::WriteAllText((Join-Path $Project 'Assets\Shell\Resources\Ro3D3DShaders.txt'), ($pcShaderNames -join "`n") + "`n")
Write-Host "PC-only shader names: $($pcShaderNames.Count)"

# Left over by setup runs that generated the game's initializers themselves (Unity registers them from the plugins).
Remove-Item -Force (Join-Path $Project 'Assets\Shell\GameInit.g.cs*') -ErrorAction SilentlyContinue

# Our ARM64 replacements of the client's Windows natives: android\native\<lib>\*.c -> lib<lib>.so, built with the NDK
# that came with Unity's Android module.
$clang = Join-Path (Split-Path $Unity) 'Data\PlaybackEngines\AndroidPlayer\NDK\toolchains\llvm\prebuilt\windows-x86_64\bin\clang.exe'
if (-not (Test-Path $clang)) { throw "NDK clang not found at $clang (install Android Build Support with the NDK, or pass -Unity)" }
$natives = Join-Path $Project 'Assets\Plugins\Android\arm64-v8a'
New-Item -ItemType Directory -Force $natives | Out-Null
$nativeMeta = @'
fileFormatVersion: 2
guid: {0}
PluginImporter:
  externalObjects: {{}}
  serializedVersion: 2
  iconMap: {{}}
  executionOrder: {{}}
  defineConstraints: []
  isPreloaded: 0
  isOverridable: 0
  isExplicitlyReferenced: 0
  validateReferences: 1
  platformData:
  - first:
      Android: Android
    second:
      enabled: 1
      settings:
        CPU: ARM64
  - first:
      Any:
    second:
      enabled: 0
      settings: {{}}
  userData:
  assetBundleName:
  assetBundleVariant:
'@
foreach ($dir in Get-ChildItem (Join-Path $PSScriptRoot 'native') -Directory) {
  $so = Join-Path $natives "lib$($dir.Name).so"
  $sources = @((Get-ChildItem $dir.FullName -Filter *.c).FullName)
  # 16 KB pages: Android 15+ warns about (and may refuse) libraries aligned for 4 KB only.
  & $clang --target=aarch64-linux-android26 -shared -fPIC -O2 -Wall -fvisibility=hidden '-Wl,-z,max-page-size=16384' $sources -o $so -llog
  if ($LASTEXITCODE) { throw "native $($dir.Name) failed" }
  $guid = -join ($md5.ComputeHash([Text.Encoding]::UTF8.GetBytes("ro3android-native:" + $dir.Name)) | ForEach-Object { $_.ToString('x2') })
  [IO.File]::WriteAllText("$so.meta", ($nativeMeta -f $guid) + "`n")
  Write-Host "built lib$($dir.Name).so"
}
# Natives with their own build (android\xlua\build-xlua.ps1 -> libxlua.so).
foreach ($so in Get-ChildItem (Join-Path $PSScriptRoot 'build\native-prebuilt') -Filter *.so -ErrorAction SilentlyContinue) {
  Copy-Item -Force $so.FullName $natives
  $guid = -join ($md5.ComputeHash([Text.Encoding]::UTF8.GetBytes("ro3android-native:" + $so.BaseName)) | ForEach-Object { $_.ToString('x2') })
  [IO.File]::WriteAllText((Join-Path $natives "$($so.Name).meta"), ($nativeMeta -f $guid) + "`n")
  Write-Host "installed $($so.Name)"
}
if (-not (Test-Path (Join-Path $natives 'libxlua.so'))) { Write-Warning 'libxlua.so missing: run .\android\xlua\build-xlua.ps1 (the game cannot run Lua without it)' }
Write-Host 'next: .\android\build-apk.ps1'
