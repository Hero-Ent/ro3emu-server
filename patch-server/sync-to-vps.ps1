param(
    [Parameter(Mandatory = $true, HelpMessage = "IP ou domínio da VPS")]
    [string]$VpsHost,

    [Parameter(Mandatory = $false)]
    [string]$User = "root",

    [Parameter(Mandatory = $false)]
    [int]$Port = 22,

    [Parameter(Mandatory = $false)]
    [string]$RemoteDir = "/var/www/hero-patches/repo"
)

$ErrorActionPreference = "Stop"
$repoDir = Join-Path $PSScriptRoot "repo"

Write-Host "==================================================================" -ForegroundColor Cyan
Write-Host "  Hero Saga - Sincronizador de Patches para VPS" -ForegroundColor Cyan
Write-Host "  Destino: $User@${VpsHost}:${RemoteDir}" -ForegroundColor Gray
Write-Host "==================================================================" -ForegroundColor Cyan

# Ordem correta: chunks, manifests e launcher PRIMEIRO; channels por ÚLTIMO.
# Isso garante que nenhum jogador tente baixar arquivos que ainda não terminaram o upload.

$scpCmd = "scp"
if (-not (Get-Command $scpCmd -ErrorAction SilentlyContinue)) {
    throw "O comando 'scp' não foi encontrado. Certifique-se de que o cliente OpenSSH está instalado no Windows."
}

# 1. Enviar manifests, chunks, launcher, news e executável
Write-Host "`n[1/3] Enviando manifestos, chunks e executáveis..." -ForegroundColor Yellow
$itemsToUpload = @("manifests", "chunks", "launcher", "news.json", "HeroLauncher.exe")

foreach ($item in $itemsToUpload) {
    $localPath = Join-Path $repoDir $item
    if (Test-Path $localPath) {
        Write-Host "  -> Enviando $item..." -ForegroundColor Gray
        & scp -P $Port -r "$localPath" "${User}@${VpsHost}:${RemoteDir}/"
        if ($LASTEXITCODE -ne 0) {
            throw "Falha ao enviar $item para a VPS."
        }
    }
}

# 2. Enviar channels por último
Write-Host "`n[2/3] Enviando descritores de canal (channels/)..." -ForegroundColor Yellow
$channelsPath = Join-Path $repoDir "channels"
if (Test-Path $channelsPath) {
    & scp -P $Port -r "$channelsPath" "${User}@${VpsHost}:${RemoteDir}/"
    if ($LASTEXITCODE -ne 0) {
        throw "Falha ao enviar channels para a VPS."
    }
}

# 3. Permissões no servidor remoto
Write-Host "`n[3/3] Ajustando permissões na VPS..." -ForegroundColor Yellow
$sshCmd = "ssh -p $Port ${User}@${VpsHost} 'chown -R www-data:www-data $RemoteDir 2>/dev/null || true; chmod -R 755 $RemoteDir'"
Invoke-Expression $sshCmd | Out-Null

Write-Host "`n==================================================================" -ForegroundColor Green
Write-Host "  Upload concluído com sucesso!" -ForegroundColor Green
Write-Host "  Os clientes agora podem baixar os patches diretamente da sua VPS." -ForegroundColor Green
Write-Host "==================================================================" -ForegroundColor Green
