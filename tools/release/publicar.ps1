# =====================================================================================================================
#  Hero Saga - PUBLICAR ATUALIZACAO (cliente + launcher) para PRODUCAO e LOCAL de uma vez so
# ---------------------------------------------------------------------------------------------------------------------
#  Atalho: PUBLICAR_ATUALIZACAO.cmd (raiz do repositorio).
#
#  O que faz, sempre nesta ordem:
#    1. Detecta o que mudou desde a ultima publicacao (patch-server/release-state.json):
#         - cliente : src/Ro3Emu.ClientHooks, src/Ro3Emu.Loc, src/Ro3Emu.ClientTools, data/loc, a chave do gate,
#                     a lista oficial de arquivos do cliente e os enderecos dos servidores;
#         - launcher: src/Ro3Emu.Launcher e src/Ro3Emu.Patcher.
#    2. Escolhe a versao se voce nao informar. Cliente e launcher tem sempre a MESMA versao e sao publicados juntos
#       quando qualquer um dos dois mudou (sobe o ultimo numero da maior das duas: 1.1.0 -> 1.1.1). O launcher do
#       jogador so se atualiza para uma versao MAIOR que a dele.
#    3. Compila o que precisa.
#    4. Publica o cliente para CADA alvo de patch-server/release-config.json, no mesmo repositorio de patches:
#         producao -> canal "live", versao X.Y.Z       (cliente patchado para o IP de producao)
#         local    -> canal "test", versao X.Y.Z-local (cliente patchado para 127.0.0.1)
#       Os ~20 GB do jogo sao iguais nas duas e sao lidos uma vez so (cache de publicacao); so as DLLs mudam.
#    5. Publica o launcher nos dois canais e copia o exe para Launcher-Dist e Launcher-Local
#       (cada pasta recebe tambem o seu launcher.json correto).
#    6. Salva patch-server/release-state.json.
#
#  Nada vai para a internet aqui. Teste com o Launcher-Local e depois rode ENVIAR_ATUALIZACAO.cmd.
#
#  Uso:
#    PUBLICAR_ATUALIZACAO.cmd                      detecta sozinho o que publicar
#    PUBLICAR_ATUALIZACAO.cmd -Cliente             publica o cliente mesmo sem mudanca detectada
#    PUBLICAR_ATUALIZACAO.cmd -Launcher            publica o launcher mesmo sem mudanca detectada
#    PUBLICAR_ATUALIZACAO.cmd -Forcar              publica os dois
#    PUBLICAR_ATUALIZACAO.cmd -Versao 1.2.0        escolhe a versao (cliente e launcher)
#    PUBLICAR_ATUALIZACAO.cmd -VersaoLauncher 1.1.0
#    PUBLICAR_ATUALIZACAO.cmd -SoLocal             so a versao local (para devs que nao publicam em producao)
#    PUBLICAR_ATUALIZACAO.cmd -Simular             mostra o que faria, sem compilar nem publicar
#    PUBLICAR_ATUALIZACAO.cmd -SemCache            rele todos os arquivos do cliente (mais lento)
# =====================================================================================================================
param(
    [string]$Versao,
    [string]$VersaoLauncher,
    [switch]$Cliente,
    [switch]$Launcher,
    [switch]$Forcar,
    [switch]$SoLocal,
    [switch]$Simular,
    [switch]$SemCache,
    [switch]$SemPausa
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
Set-Location $root

function Info($text)  { Write-Host $text -ForegroundColor Cyan }
function Ok($text)    { Write-Host $text -ForegroundColor Green }
function Warn($text)  { Write-Host $text -ForegroundColor Yellow }
function Fail($text) {
    Write-Host ''
    Write-Host "[ERRO] $text" -ForegroundColor Red
    if (-not $SemPausa) { Read-Host 'Pressione Enter para fechar' | Out-Null }
    exit 1
}
function Run([string]$exe, [string[]]$arguments) {
    Write-Host "  > $exe $($arguments -join ' ')" -ForegroundColor DarkGray
    & $exe @arguments
    if ($LASTEXITCODE -ne 0) { Fail "Falhou (codigo $LASTEXITCODE): $exe $($arguments -join ' ')" }
}
function Full([string]$relative) { return [IO.Path]::GetFullPath((Join-Path $root $relative)) }
function WriteUtf8([string]$path, [string]$text) { [IO.File]::WriteAllText($path, $text, (New-Object Text.UTF8Encoding($false))) }

function Bump([string]$version) {
    $parts = @($version.Split('-')[0].Split('.'))
    while ($parts.Count -lt 3) { $parts += '0' }
    $parts[2] = [string]([int]$parts[2] + 1)
    return ($parts[0..2] -join '.')
}
function VersionGreater([string]$a, [string]$b) {
    try { return ([version]$a.Split('-')[0]) -gt ([version]$b.Split('-')[0]) } catch { return $false }
}

# Impressao digital de pastas/arquivos: git hash-object normaliza CRLF/LF, entao o mesmo codigo da o mesmo hash em
# qualquer maquina (o release-state.json vai para o git).
function Get-InputsHash([string[]]$paths, [string]$extra) {
    $files = New-Object System.Collections.Generic.List[string]
    $lines = New-Object System.Collections.Generic.List[string]
    foreach ($p in $paths) {
        $full = Full $p
        if (Test-Path -LiteralPath $full -PathType Container) {
            Get-ChildItem -LiteralPath $full -Recurse -File |
                Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' } |
                Sort-Object FullName | ForEach-Object { $files.Add($_.FullName) }
        } elseif (Test-Path -LiteralPath $full -PathType Leaf) {
            $files.Add($full)
        } else {
            $lines.Add("$p|ausente")
        }
    }
    if ($files.Count -gt 0) {
        # Em lotes pela linha de comando (stdin do PowerShell 5 pode inserir BOM no primeiro caminho).
        $hashes = New-Object System.Collections.Generic.List[string]
        for ($i = 0; $i -lt $files.Count; $i += 80) {
            $last = [Math]::Min($i + 79, $files.Count - 1)
            $batch = @($files[$i..$last] | ForEach-Object { $_.Substring($root.Length).TrimStart('\') })
            $out = @(& git hash-object -- @batch)
            if ($LASTEXITCODE -ne 0 -or $out.Count -ne $batch.Count) { Fail 'git hash-object falhou (o Git esta instalado?)' }
            foreach ($h in $out) { $hashes.Add([string]$h) }
        }
        for ($i = 0; $i -lt $files.Count; $i++) {
            $lines.Add($files[$i].Substring($root.Length).Replace('\', '/').ToLowerInvariant() + '|' + $hashes[$i])
        }
    }
    $lines.Add("extra|$extra")
    $sha = [Security.Cryptography.SHA256]::Create()
    $bytes = [Text.Encoding]::UTF8.GetBytes(($lines -join "`n"))
    return ([BitConverter]::ToString($sha.ComputeHash($bytes)) -replace '-', '').ToLowerInvariant()
}

Write-Host ''
Write-Host '=================================================================' -ForegroundColor Cyan
Write-Host '   HERO SAGA - PUBLICAR ATUALIZACAO (PRODUCAO + LOCAL)' -ForegroundColor Cyan
Write-Host '=================================================================' -ForegroundColor Cyan

# --- Configuracao ----------------------------------------------------------------------------------------------------
$configPath = Full 'patch-server\release-config.json'
$statePath = Full 'patch-server\release-state.json'
if (-not (Test-Path $configPath)) { Fail "Nao encontrei $configPath" }
$config = Get-Content -Raw -Encoding UTF8 $configPath | ConvertFrom-Json
$state = if (Test-Path $statePath) { Get-Content -Raw -Encoding UTF8 $statePath | ConvertFrom-Json } else { $null }
if (-not $state) { $state = [pscustomobject]@{ clientVersion = '0.0.0'; clientInputsHash = ''; launcherVersion = '1.0.0'; launcherInputsHash = ''; updated = ''; by = '' } }

$clientDir = Full $config.clientDir
$repoDir = Full $config.repoDir
$gateKey = Full $config.gateKey
$targets = @($config.targets)
if ($SoLocal) { $targets = @($targets | Where-Object { $_.name -eq 'local' }) }
if ($targets.Count -eq 0) { Fail 'Nenhum alvo em release-config.json.' }

if (-not (Get-Command git -ErrorAction SilentlyContinue)) { Fail 'Git nao encontrado no PATH.' }
$env:PATH = "$env:PATH;C:\Program Files\dotnet"
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { Fail '.NET SDK nao encontrado (instale o .NET 10 SDK).' }
if (-not (Test-Path (Join-Path $clientDir 'ro3.exe'))) { Fail "Cliente nao encontrado em $clientDir (falta ro3.exe)." }
if (-not (Test-Path (Join-Path $clientDir 'ro3emu-backup'))) { Fail "Falta $clientDir\ro3emu-backup (DLLs originais). Rode o jogo uma vez pelo scripts\start.cmd ou tools\patch-client.ps1." }
if (-not (Test-Path $gateKey)) { Fail "Chave do gate nao encontrada: $gateKey" }

# --- O que mudou? ----------------------------------------------------------------------------------------------------
$serversKey = ($config.targets | ForEach-Object { "$($_.name)=$($_.server)" }) -join ';'
$clientHash = Get-InputsHash @('src\Ro3Emu.ClientHooks', 'src\Ro3Emu.Loc', 'src\Ro3Emu.ClientTools', 'data\loc', $config.gateKey,
    (Join-Path $config.clientDir '.ro3-update\verified-baseline.txt'), (Join-Path $config.clientDir 'ro3emu\en_overrides.tsv')) $serversKey
$launcherHash = Get-InputsHash @('src\Ro3Emu.Launcher', 'src\Ro3Emu.Patcher') ''

$doClient = $Cliente -or $Forcar -or $Versao -or ($clientHash -ne $state.clientInputsHash)
$doLauncher = $Launcher -or $Forcar -or $VersaoLauncher -or ($launcherHash -ne $state.launcherInputsHash)
# Hero Saga: cliente e launcher tem sempre a MESMA versao (1.1.0, 1.1.1, ...): mudou um, publica os dois juntos.
if (-not $SoLocal -and ($doClient -or $doLauncher)) { $doClient = $true; $doLauncher = $true }
if ($SoLocal) { $doClient = $true; $doLauncher = $false }

# --- Versoes ---------------------------------------------------------------------------------------------------------
$csprojPath = Full 'src\Ro3Emu.Launcher\Ro3Emu.Launcher.csproj'
$csproj = [IO.File]::ReadAllText($csprojPath)
$csprojVersion = ([regex]::Match($csproj, '<Version>([^<]+)</Version>')).Groups[1].Value

$clientVersion = $null
if ($doClient) {
    if ($SoLocal) {
        $clientVersion = "$($state.clientVersion)-dev.$(Get-Date -Format 'yyyyMMdd.HHmmss')"
    } elseif ($Versao) {
        $clientVersion = $Versao
    } else {
        # A maior das duas versoes atuais, +1 (o launcher do jogador so se atualiza para uma versao MAIOR).
        $base = $state.clientVersion
        foreach ($other in @($state.launcherVersion, $csprojVersion)) { if ($other -and (VersionGreater $other $base)) { $base = $other } }
        $clientVersion = Bump $base
        # Nunca reaproveita um numero: releases sao imutaveis.
        while ($targets | Where-Object { Test-Path (Join-Path $repoDir "manifests\$clientVersion$($_.versionSuffix).json.zst") }) { $clientVersion = Bump $clientVersion }
    }
    foreach ($t in $targets) {
        $v = if ($SoLocal) { $clientVersion } else { "$clientVersion$($t.versionSuffix)" }
        if (Test-Path (Join-Path $repoDir "manifests\$v.json.zst")) { Fail "A versao $v ja existe em $repoDir\manifests. Escolha outra com -Versao." }
    }
}

$launcherVersion = $null
if ($doLauncher) {
    if ($VersaoLauncher) { $launcherVersion = $VersaoLauncher }
    elseif ($clientVersion -and -not $SoLocal) { $launcherVersion = $clientVersion }   # mesma versao do cliente
    elseif (VersionGreater $csprojVersion $state.launcherVersion) { $launcherVersion = $csprojVersion }   # ja subiram a mao no .csproj
    else { $launcherVersion = Bump $(if (VersionGreater $state.launcherVersion $csprojVersion) { $state.launcherVersion } else { $csprojVersion }) }
    try { [void][version]$launcherVersion } catch { Fail "Versao do launcher invalida: $launcherVersion (use 1.2.3)" }
}

Write-Host ''
Info 'Plano:'
if ($doClient) {
    foreach ($t in $targets) {
        $v = if ($SoLocal) { $clientVersion } else { "$clientVersion$($t.versionSuffix)" }
        Write-Host ("  Cliente  {0,-9} canal {1,-5} versao {2,-14} servidor {3}" -f $t.name, $t.channel, $v, $t.server)
    }
} else { Write-Host "  Cliente  sem mudancas (continua $($state.clientVersion))" }
if ($doLauncher) { Write-Host "  Launcher $csprojVersion -> $launcherVersion (canais: $(($targets | ForEach-Object { $_.channel }) -join ', '))" }
else { Write-Host "  Launcher sem mudancas (continua $($state.launcherVersion))" }

if (-not $doClient -and -not $doLauncher) {
    Write-Host ''
    Ok 'Nada mudou desde a ultima publicacao. Use -Cliente, -Launcher ou -Forcar para publicar mesmo assim.'
    if (-not $SemPausa) { Read-Host 'Pressione Enter para fechar' | Out-Null }
    exit 0
}
if ($Simular) { Write-Host ''; Warn '-Simular: nada foi compilado nem publicado.'; exit 0 }

$sw = [Diagnostics.Stopwatch]::StartNew()

# --- Compilacao ------------------------------------------------------------------------------------------------------
Write-Host ''
Info '[1/4] Compilando...'
if ($doClient) {
    Run dotnet @('build', 'src\Ro3Emu.ClientHooks', '-c', 'Debug', '-v', 'q', '-nologo', '-clp:ErrorsOnly')
    Run dotnet @('build', 'src\Ro3Emu.Loc', '-c', 'Debug', '-v', 'q', '-nologo', '-clp:ErrorsOnly')
}
# --no-incremental: o MSBuild nao percebe quando a DLL embutida (hooks/loc) muda.
Run dotnet @('build', 'src\Ro3Emu.ClientTools', '-c', 'Debug', '--no-incremental', '-v', 'q', '-nologo', '-clp:ErrorsOnly')
$tools = Full 'src\Ro3Emu.ClientTools\bin\Debug\net10.0\win-x64\Ro3Emu.ClientTools.exe'
if (-not (Test-Path $tools)) {
    $found = Get-ChildItem (Full 'src\Ro3Emu.ClientTools\bin\Debug') -Recurse -Filter 'Ro3Emu.ClientTools.exe' | Select-Object -First 1
    if (-not $found) { Fail 'Ro3Emu.ClientTools.exe nao foi gerado.' }
    $tools = $found.FullName
}

$launcherExe = $null
if ($doLauncher) {
    if ($launcherVersion -ne $csprojVersion) {
        $csproj = [regex]::Replace($csproj, '<Version>[^<]+</Version>', "<Version>$launcherVersion</Version>", 1)
        WriteUtf8 $csprojPath $csproj
        Write-Host "  Ro3Emu.Launcher.csproj: versao $csprojVersion -> $launcherVersion"
    }
    $publishDir = Full 'bin-temp-launcher'
    if (Test-Path $publishDir) { Remove-Item -Recurse -Force $publishDir }
    Run dotnet @('publish', 'src\Ro3Emu.Launcher', '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', '-p:PublishSingleFile=true', '-o', $publishDir, '-v', 'q', '-nologo', '-clp:ErrorsOnly')
    $launcherExe = Join-Path $publishDir 'HeroLauncher.exe'
    if (-not (Test-Path $launcherExe)) { Fail 'HeroLauncher.exe nao foi gerado.' }
}

# --- Cliente ---------------------------------------------------------------------------------------------------------
Write-Host ''
if ($doClient) {
    Info '[2/4] Publicando o cliente (a primeira vez le o jogo inteiro; as proximas usam o cache)...'
    $pemText = $gateKey
    foreach ($t in $targets) {
        $v = if ($SoLocal) { $clientVersion } else { "$clientVersion$($t.versionSuffix)" }
        Write-Host ''
        Info "  -> $($t.name): canal $($t.channel), versao $v, servidor $($t.server)"
        $releaseArgs = @('release', $clientDir, $t.server, $pemText, $repoDir, $v, $t.channel)
        if ($SemCache) { $releaseArgs += '--no-cache' }
        # A lista de arquivos e enorme: mostra so o resumo.
        Write-Host "  > Ro3Emu.ClientTools $($releaseArgs -join ' ')" -ForegroundColor DarkGray
        & $tools @releaseArgs | Where-Object { $_ -notmatch '^\s{2}\S' }
        if ($LASTEXITCODE -ne 0) { Fail "Falhou ao publicar o cliente para $($t.name)." }
    }
} else { Info '[2/4] Cliente: nada a publicar.' }

# --- Launcher --------------------------------------------------------------------------------------------------------
Write-Host ''
if ($doLauncher) {
    Info '[3/4] Publicando o launcher...'
    foreach ($t in $targets) {
        if (-not (Test-Path (Join-Path $repoDir "channels\$($t.channel).json"))) { Warn "  Canal $($t.channel) ainda nao tem cliente; launcher nao publicado nele."; continue }
        Run $tools @('release-launcher', $repoDir, $t.channel, $launcherExe, $launcherVersion)
    }
    # Download direto para jogadores novos.
    Copy-Item $launcherExe (Join-Path $repoDir 'HeroLauncher.exe') -Force
    Copy-Item $launcherExe (Full 'HeroLauncher.exe') -Force
} else { Info '[3/4] Launcher: nada a publicar.' }

# --- Pastas dos launchers (sempre: garante exe + launcher.json certos) ------------------------------------------------
Write-Host ''
Info '[4/4] Atualizando as pastas dos launchers...'
Get-Process -Name 'HeroLauncher' -ErrorAction SilentlyContinue | Where-Object {
    $p = $_.Path; $p -and ($targets | Where-Object { $p.StartsWith((Full $_.launcherDir), [StringComparison]::OrdinalIgnoreCase) })
} | ForEach-Object { Warn "  Fechando o launcher aberto: $($_.Path)"; Stop-Process -Id $_.Id -Force }
Start-Sleep -Milliseconds 500
foreach ($t in $targets) {
    $dir = Full $t.launcherDir
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
    $exeTarget = Join-Path $dir 'HeroLauncher.exe'
    if ($launcherExe) { Copy-Item $launcherExe $exeTarget -Force }
    elseif (-not (Test-Path $exeTarget) -and (Test-Path (Full 'HeroLauncher.exe'))) { Copy-Item (Full 'HeroLauncher.exe') $exeTarget -Force }
    $json = [ordered]@{ repository = $t.repository; channel = $t.channel; server = $t.server; serverName = $t.serverName } | ConvertTo-Json
    WriteUtf8 (Join-Path $dir 'launcher.json') $json
    Write-Host "  $($t.launcherDir): canal $($t.channel) em $($t.repository)"
}

# --- Estado ----------------------------------------------------------------------------------------------------------
if (-not $SoLocal) {
    if ($doClient) { $state.clientVersion = $clientVersion; $state.clientInputsHash = $clientHash }
    if ($doLauncher) { $state.launcherVersion = $launcherVersion; $state.launcherInputsHash = $launcherHash }
    $state.updated = (Get-Date).ToString('yyyy-MM-dd HH:mm:ss')
    $state.by = $env:USERNAME
    WriteUtf8 $statePath ($state | ConvertTo-Json)
}
if ($doLauncher -and (Test-Path (Full 'bin-temp-launcher'))) { Remove-Item -Recurse -Force (Full 'bin-temp-launcher') }

Write-Host ''
Write-Host '=================================================================' -ForegroundColor Green
Ok "  PRONTO em $([int]$sw.Elapsed.TotalMinutes) min $($sw.Elapsed.Seconds) s"
if ($doClient) { Ok "  Cliente:  $clientVersion" }
if ($doLauncher) { Ok "  Launcher: $launcherVersion" }
Write-Host '=================================================================' -ForegroundColor Green
Write-Host ''
Write-Host 'Proximos passos:'
Write-Host '  1. Teste local: Launcher-Local\iniciar-patch-server.bat, runserver.bat e Launcher-Local\HeroLauncher.exe'
if (-not $SoLocal) {
    Write-Host '  2. Tudo certo? Envie para producao: ENVIAR_ATUALIZACAO.cmd'
    Write-Host '  3. Faca commit: patch-server/release-state.json, patch-server/repo/channels, patch-server/repo/manifests'
    Write-Host '     e src/Ro3Emu.Launcher/Ro3Emu.Launcher.csproj (se o launcher mudou).'
} else {
    Write-Host '  (-SoLocal: versao so sua. NAO faca commit de patch-server/repo/channels/test.json;'
    Write-Host '   para desfazer: git checkout -- patch-server/repo/channels/test.json)'
}
Write-Host ''
if (-not $SemPausa) { Read-Host 'Pressione Enter para fechar' | Out-Null }
