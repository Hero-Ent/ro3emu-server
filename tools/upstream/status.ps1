<#
.SYNOPSIS
  Hero Saga - verifica o upstream (aethai/ro3emu-server) SEM aplicar nada.

.DESCRIPTION
  1. Garante o remote "upstream" e faz git fetch.
  2. Conta os commits pendentes (HEAD..upstream/main) e simula o merge (GitOut merge-tree) para listar conflitos.
  3. Gera o dossie .upstream-sync\dossie.md (ignorado pelo git) com cada commit pendente: mensagem completa,
     PR de origem, arquivos tocados e se colide com arquivos que o Hero Saga alterou desde o fork.
     O agente usa esse dossie para escrever o relatorio em portugues (skill .agents/skills/upstream-sync).

  Nao faz merge, stash, checkout nem push. Pode rodar quantas vezes quiser.

.EXAMPLE
  powershell -NoProfile -ExecutionPolicy Bypass -File tools\upstream\status.ps1 -SemPausa
#>
param(
    [switch]$SemFetch,
    [switch]$SemPausa,
    [string]$Alvo = 'upstream/main',
    [int]$MaxArquivosPorCommit = 30
)

$ErrorActionPreference = 'Continue'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$OutputEncoding = [System.Text.Encoding]::UTF8

$raiz = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
Set-Location $raiz
$upstreamUrl = 'https://github.com/aethai/ro3emu-server.git'

function Sair([int]$codigo) {
    if (-not $SemPausa) { Write-Host ''; Read-Host 'Pressione ENTER para fechar' | Out-Null }
    exit $codigo
}

function GitOut {
    # Roda git e devolve as linhas de stdout; stderr vai para o console.
    $saida = & git.exe @args
    return $saida
}

Write-Host '============================================================'
Write-Host '   HERO SAGA - VERIFICAR UPSTREAM (somente leitura)'
Write-Host "   $upstreamUrl"
Write-Host '============================================================'

if (-not (Get-Command git -ErrorAction SilentlyContinue)) {
    Write-Host '[ERRO] git nao encontrado no PATH.' -ForegroundColor Red
    Sair 1
}

# --- 1. remote + fetch -------------------------------------------------------------------------------------------
& git remote get-url upstream *> $null
if ($LASTEXITCODE -ne 0) {
    Write-Host "Configurando remote upstream -> $upstreamUrl"
    & git remote add upstream $upstreamUrl
}

if (-not $SemFetch) {
    Write-Host '[1/3] git fetch upstream ...'
    & git fetch upstream main --prune --quiet
    if ($LASTEXITCODE -ne 0) {
        Write-Host '[ERRO] Falha no fetch (internet/GitHub).' -ForegroundColor Red
        Sair 1
    }
}

$base = (GitOut merge-base HEAD $Alvo | Select-Object -First 1)
if (-not $base) {
    Write-Host "[ERRO] Sem ancestral comum entre HEAD e $Alvo." -ForegroundColor Red
    Sair 1
}

$pendentes = [int](GitOut rev-list --count "HEAD..$Alvo")
$semMerge = [int](GitOut rev-list --count --no-merges "HEAD..$Alvo")
$nossos = [int](GitOut rev-list --count "$Alvo..HEAD")
$branch = (GitOut rev-parse --abbrev-ref HEAD)
$sujo = @(GitOut status --porcelain --untracked-files=no)

Write-Host ''
Write-Host "Branch atual         : $branch"
Write-Host "Ultimo sync (base)   : $($base.Substring(0,8))  $(GitOut log -1 --format='%ad %s' --date=short $base)"
Write-Host "Upstream             : $(GitOut log -1 --format='%h %ad %s' --date=short $Alvo)"
Write-Host "Commits pendentes    : $pendentes ($semMerge sem contar merges)"
Write-Host "Commits so nossos    : $nossos"
if ($sujo.Count -gt 0) {
    Write-Host "Arquivos modificados : $($sujo.Count) (commit antes de integrar)" -ForegroundColor Yellow
}

if ($pendentes -eq 0) {
    Write-Host ''
    Write-Host '[OK] Ja estamos 100% sincronizados com o upstream.' -ForegroundColor Green
    Sair 0
}

# --- 2. simulacao de merge ---------------------------------------------------------------------------------------
Write-Host ''
Write-Host '[2/3] Simulando o merge (nada e alterado) ...'
$mt = @(GitOut merge-tree --write-tree --name-only --no-messages HEAD $Alvo)
$conflitos = @()
if ($LASTEXITCODE -eq 1 -and $mt.Count -gt 1) { $conflitos = @($mt | Select-Object -Skip 1 | Where-Object { $_ }) }
$setConflitos = @{}
foreach ($c in $conflitos) { $setConflitos[$c] = $true }

# Arquivos que o Hero Saga mudou desde o ultimo sync.
$setNossos = @{}
foreach ($f in (GitOut diff --name-only $base HEAD)) { $setNossos[$f] = $true }

# --- 3. dossie -------------------------------------------------------------------------------------------------
Write-Host '[3/3] Montando o dossie dos commits ...'

# Mapa commit -> PR, a partir dos merges "Merge pull request #N from dono/branch".
$prDe = @{}
# So os merges da linha principal (--first-parent) e do mais antigo ao mais novo: o primeiro PR que levou o commit
# para a main fica com ele (merges internos "origin/main into feat/..." nao contam).
foreach ($linha in (GitOut log --merges --first-parent --reverse --format='%H%x09%P%x09%s' "HEAD..$Alvo")) {
    $p = $linha -split "`t"
    if ($p.Count -lt 3) { continue }
    $pais = $p[1] -split ' '
    if ($pais.Count -lt 2) { continue }
    $rotulo = $p[2]
    if ($rotulo -match 'Merge pull request (#\d+) from (\S+)') { $rotulo = "PR $($Matches[1]) ($($Matches[2]))" }
    foreach ($h in (GitOut rev-list --no-merges "$($pais[0])..$($pais[1])")) {
        if (-not $prDe.ContainsKey($h)) { $prDe[$h] = $rotulo }
    }
}

$sep = [char]0x1e
$commits = @(GitOut log --reverse --no-merges --format="%H$sep%h$sep%ad$sep%an$sep%s" --date=short "HEAD..$Alvo")

$sb = New-Object System.Text.StringBuilder
[void]$sb.AppendLine('# Dossie upstream (gerado por tools/upstream/status.ps1)')
[void]$sb.AppendLine('')
[void]$sb.AppendLine("- Gerado em: $(Get-Date -Format 'yyyy-MM-dd HH:mm')")
[void]$sb.AppendLine("- Base (ultimo sync): $base")
[void]$sb.AppendLine("- Alvo: $Alvo = $(GitOut rev-parse $Alvo)")
[void]$sb.AppendLine("- Pendentes: $pendentes ($semMerge sem merges); nossos commits desde a base: $nossos")
[void]$sb.AppendLine('')
[void]$sb.AppendLine("## Conflitos previstos no merge ($($conflitos.Count))")
[void]$sb.AppendLine('')
if ($conflitos.Count -eq 0) { [void]$sb.AppendLine('Nenhum: o merge entra limpo.') }
foreach ($c in $conflitos) { [void]$sb.AppendLine("- ``$c``") }
[void]$sb.AppendLine('')
[void]$sb.AppendLine('## Commits (ordem cronologica, sem merges)')
[void]$sb.AppendLine('')
[void]$sb.AppendLine('Legenda: [CONFLITO] arquivo com conflito previsto; [NOSSO] arquivo que o Hero Saga tambem alterou.')

$i = 0
$resumo = @()
foreach ($linha in $commits) {
    $i++
    $p = $linha -split [regex]::Escape([string]$sep)
    $full, $curto, $data, $autor, $assunto = $p
    $corpo = (GitOut log -1 --format='%b' $full) -join "`n"
    $stat = (GitOut show --shortstat --format='' $full | Where-Object { $_ }) -join ' '
    $arquivos = @(GitOut show --name-status --format='' $full | Where-Object { $_ })

    $nConf = 0; $nNosso = 0
    $linhasArq = @()
    foreach ($a in $arquivos) {
        $partes = $a -split "`t"
        $caminho = $partes[-1]
        $tag = ''
        if ($setConflitos.ContainsKey($caminho)) { $tag = ' [CONFLITO]'; $nConf++ }
        elseif ($setNossos.ContainsKey($caminho)) { $tag = ' [NOSSO]'; $nNosso++ }
        $linhasArq += "  - $($partes[0]) ``$caminho``$tag"
    }

    $pr = if ($prDe.ContainsKey($full)) { $prDe[$full] } else { 'direto na main' }
    $flag = ''
    if ($nConf -gt 0) { $flag = " - CONFLITO($nConf)" } elseif ($nNosso -gt 0) { $flag = " - toca nosso($nNosso)" }

    [void]$sb.AppendLine('')
    [void]$sb.AppendLine("### $i. $curto - $assunto")
    [void]$sb.AppendLine('')
    [void]$sb.AppendLine("- $data, $autor, $pr$flag")
    [void]$sb.AppendLine("- $stat")
    if ($corpo.Trim()) {
        [void]$sb.AppendLine('')
        foreach ($l in ($corpo.Trim() -split "`n")) { [void]$sb.AppendLine("> $l") }
    }
    [void]$sb.AppendLine('')
    [void]$sb.AppendLine('Arquivos:')
    # Mostra primeiro os que colidem; corta o resto para nao inchar o dossie.
    $ordenados = @($linhasArq | Sort-Object { if ($_ -match '\[CONFLITO\]') { 0 } elseif ($_ -match '\[NOSSO\]') { 1 } else { 2 } })
    $ordenados | Select-Object -First $MaxArquivosPorCommit | ForEach-Object { [void]$sb.AppendLine($_) }
    if ($ordenados.Count -gt $MaxArquivosPorCommit) {
        [void]$sb.AppendLine("  - ... +$($ordenados.Count - $MaxArquivosPorCommit) arquivos")
    }

    $resumo += [pscustomobject]@{ N = $i; Hash = $curto; Data = $data; Flag = $flag.Trim(' -'); Assunto = $assunto }
}

$pasta = Join-Path $raiz '.upstream-sync'
New-Item -ItemType Directory -Force -Path $pasta | Out-Null
$dossie = Join-Path $pasta 'dossie.md'
[System.IO.File]::WriteAllText($dossie, $sb.ToString(), (New-Object System.Text.UTF8Encoding($false)))

# --- console ---------------------------------------------------------------------------------------------------
Write-Host ''
Write-Host '============================================================'
Write-Host " Commits pendentes do upstream ($semMerge):"
Write-Host '============================================================'
foreach ($r in $resumo) {
    $cor = if ($r.Flag -like 'CONFLITO*') { 'Yellow' } elseif ($r.Flag) { 'Cyan' } else { 'Gray' }
    $txt = '{0,3}. {1} {2} {3}' -f $r.N, $r.Hash, $r.Data, $r.Assunto
    if ($r.Flag) { $txt += "  [$($r.Flag)]" }
    Write-Host $txt -ForegroundColor $cor
}
Write-Host ''
Write-Host "Conflitos previstos: $($conflitos.Count) arquivo(s)" -ForegroundColor $(if ($conflitos.Count) { 'Yellow' } else { 'Green' })
foreach ($c in $conflitos) { Write-Host "   - $c" -ForegroundColor Yellow }
Write-Host ''
Write-Host "Dossie: $dossie"
Write-Host ''
Write-Host 'Nada foi aplicado. Para analisar e integrar, peca ao agente:' -ForegroundColor Green
Write-Host '   "analisa o upstream"   (skill .agents/skills/upstream-sync)' -ForegroundColor Green
Sair 0
