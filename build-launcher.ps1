param(
    [string]$Version,
    [switch]$Run
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot

Write-Host "== Compilando Hero Ragnarok Launcher ==" -ForegroundColor Cyan

$csproj = Join-Path $root 'src\Ro3Emu.Launcher\Ro3Emu.Launcher.csproj'
if ($Version) {
    Write-Host "-> Atualizando versao para $Version no .csproj..." -ForegroundColor Yellow
    (Get-Content $csproj) -replace '<Version>.*?</Version>', "<Version>$Version</Version>" | Set-Content $csproj
}

$tempPublish = Join-Path $root 'bin-temp-launcher'
Write-Host "-> Gerando executavel standalone (Release, win-x64)..." -ForegroundColor Yellow
& dotnet publish $csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o $tempPublish -nologo -v q
if ($LASTEXITCODE -ne 0) {
    Write-Host "Erro na compilacao!" -ForegroundColor Red
    if (Test-Path $tempPublish) { Remove-Item -Recurse -Force $tempPublish }
    exit 1
}

# Fecha HeroLauncher se estiver em execucao para liberar o arquivo
Get-Process -Name "HeroLauncher" -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 500

$exeSource = Join-Path $tempPublish 'HeroLauncher.exe'
if (Test-Path $exeSource) {
    Copy-Item $exeSource (Join-Path $root 'HeroLauncher.exe') -Force
    $distDir = Join-Path $root 'Launcher-Dist'
    if (Test-Path $distDir) {
        Copy-Item $exeSource (Join-Path $distDir 'HeroLauncher.exe') -Force
    }
    Write-Host "-> HeroLauncher.exe atualizado com sucesso!" -ForegroundColor Green
}

if (Test-Path $tempPublish) {
    Remove-Item -Recurse -Force $tempPublish
}

if ($Run) {
    Write-Host "-> Iniciando HeroLauncher.exe..." -ForegroundColor Cyan
    Start-Process (Join-Path $root 'HeroLauncher.exe')
}
