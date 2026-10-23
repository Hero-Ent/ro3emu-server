# Fetches the public shader sources the port compiles for Vulkan/GLES3 into android\build\third_party:
# Unity's built-in shaders for 2022.3.62f3 and TextMeshPro 3.0.9's (inside its "TMP Essential Resources" package).
$ErrorActionPreference = 'Stop'
$third = Join-Path (Split-Path $PSScriptRoot -Parent) 'build\third_party'
New-Item -ItemType Directory -Force $third | Out-Null

$zip = Join-Path $third 'builtin_shaders.zip'
Invoke-WebRequest -UseBasicParsing 'https://download.unity3d.com/download_unity/96770f904ca7/builtin_shaders-2022.3.62f3.zip' -OutFile $zip
$outer = Join-Path $third 'builtin_shaders'
if (Test-Path $outer) { Remove-Item -Recurse -Force $outer }
Expand-Archive $zip $outer
Expand-Archive (Join-Path $outer 'build\BuiltinShaders\builtin_shaders.zip') (Join-Path $outer 'src')

$tgz = Join-Path $third 'tmp.tgz'
Invoke-WebRequest -UseBasicParsing 'https://download.packages.unity.com/com.unity.textmeshpro/-/com.unity.textmeshpro-3.0.9.tgz' -OutFile $tgz
foreach ($d in 'tmp', 'tmpess', 'tmpess-out') { $p = Join-Path $third $d; if (Test-Path $p) { Remove-Item -Recurse -Force $p }; New-Item -ItemType Directory $p | Out-Null }
tar -xzf $tgz -C (Join-Path $third 'tmp')
tar -xzf (Join-Path $third 'tmp\package\Package Resources\TMP Essential Resources.unitypackage') -C (Join-Path $third 'tmpess')
# A .unitypackage is one folder per asset: "pathname" holds its project path, "asset" its content.
foreach ($d in Get-ChildItem (Join-Path $third 'tmpess') -Directory) {
  $pathname = Join-Path $d.FullName 'pathname'
  if (-not (Test-Path $pathname)) { continue }
  $p = (Get-Content $pathname -TotalCount 1).Trim()
  if ($p -notmatch '\.(shader|cginc)$') { continue }
  $target = Join-Path (Join-Path $third 'tmpess-out') $p
  New-Item -ItemType Directory -Force (Split-Path $target) | Out-Null
  Copy-Item (Join-Path $d.FullName 'asset') $target
}
Write-Host "shader sources ready in $third"
