@echo off
chcp 65001 >nul
title Atualizador RO3Emu Server - Aethai Upstream
cd /d "%~dp0"

echo ============================================================
echo          ATUALIZADOR AUTOMATICO - RO3EMU SERVER
echo       Upstream: https://github.com/aethai/ro3emu-server
echo ============================================================
echo.

where git >nul 2>&1
if errorlevel 1 (
    echo [ERRO] O Git nao foi encontrado no PATH do sistema.
    echo Instale o Git para Windows ou adicione-o ao PATH.
    echo.
    pause
    exit /b 1
)

echo [1/4] Verificando configuracao do repositorio upstream...
git remote get-url upstream >nul 2>&1
if errorlevel 1 (
    echo Configurando remote upstream para https://github.com/aethai/ro3emu-server.git ...
    git remote add upstream https://github.com/aethai/ro3emu-server.git
) else (
    echo Remote upstream ja configurado.
)

echo.
echo [2/4] Buscando atualizacoes e correcoes mais recentes...
git fetch upstream main --prune
if errorlevel 1 (
    echo [AVISO] Falha ao conectar ao GitHub. Verifique sua conexao com a internet.
    echo.
    pause
    exit /b 1
)

for /f %%c in ('git rev-list HEAD..upstream/main --count') do set NOVOS_COMMITS=%%c

if "%NOVOS_COMMITS%"=="" set NOVOS_COMMITS=0

echo.
if "%NOVOS_COMMITS%"=="0" (
    echo [STATUS] O seu emulador ja esta 100%% atualizado com o upstream!
    echo Nenhuma correcao nova pendente no repositorio aethai/ro3emu-server.
    echo.
    echo ============================================================
    echo Ultimas correcoes do aethai ja integradas no seu emulador:
    echo ============================================================
    git log upstream/main -n 3 --oneline
    echo ============================================================
    echo.
    goto SYNC_GITHUB
)

echo ============================================================
echo Encontradas %NOVOS_COMMITS% novas atualizacoes ou correcoes:
echo ============================================================
git log HEAD..upstream/main --oneline -n 10
echo ============================================================
echo.

set HAS_STASH=0
git diff --quiet >nul 2>&1
if errorlevel 1 (
    echo [3/4] Salvando alteracoes locais temporarias...
    git stash push -m "Auto-stash antes de atualizar" >nul 2>&1
    set HAS_STASH=1
) else (
    echo [3/4] Diretorio de trabalho limpo.
)

echo [4/4] Aplicando correcoes do upstream...
git merge upstream/main -m "Merge upstream: atualizacao automatica de correcoes (aethai/ro3emu-server)"
if errorlevel 1 (
    echo.
    echo [AVISO] Ocorreu um conflito ao tentar mesclar automaticamente.
    echo O Git tentou mesclar, mas alguns arquivos podem precisar de revisao.
    if "%HAS_STASH%"=="1" git stash pop >nul 2>&1
    pause
    exit /b 1
)

if "%HAS_STASH%"=="1" (
    echo Restaurando alteracoes locais salvas...
    git stash pop >nul 2>&1
)

echo.
echo ============================================================
echo [BUILD] Recompilando o emulador com as novas correcoes...
echo ============================================================

set "PATH=%PATH%;C:\Program Files\dotnet"
where dotnet >nul 2>&1
if not errorlevel 1 (
    echo Compilando servidor [Ro3Emu.Server]...
    dotnet build src/Ro3Emu.Server -c Release -v q -nologo -clp:ErrorsOnly
    echo Compilando setup/wrapper [Ro3Emu.Setup]...
    dotnet build src/Ro3Emu.Setup -c Release -v q -nologo -clp:ErrorsOnly
    echo Compilando launcher [HeroLauncher]...
    dotnet publish src/Ro3Emu.Launcher -c Release -o Launcher-Dist -v q -nologo -clp:ErrorsOnly
    copy /y "Launcher-Dist\HeroLauncher.exe" "%~dp0HeroLauncher.exe" >nul 2>&1
    copy /y "Launcher-Dist\HeroLauncher.exe" "%~dp0Launcher.exe" >nul 2>&1
    rem O launcher local e o mesmo exe: so o launcher.json ao lado muda - localhost:8790, canal test.
    if not exist "Launcher-Local" mkdir "Launcher-Local"
    copy /y "Launcher-Dist\HeroLauncher.exe" "Launcher-Local\HeroLauncher.exe" >nul 2>&1
    echo Launcher atualizado em Launcher-Dist e Launcher-Local.
    echo.
    echo [SUCESSO] Emulador recompilado com sucesso com as novas correcoes!
) else (
    echo [INFO] .NET SDK nao encontrado no PATH imediato. O codigo foi atualizado via Git.
)

:SYNC_GITHUB
echo.
set PUBLICAR=S
set /p PUBLICAR="Gerar atualizacao do cliente/launcher para PRODUCAO e LOCAL agora (so publica se algo mudou)? [S/N, padrao S]: "
if /i "%PUBLICAR%"=="S" (
    call "%~dp0PUBLICAR_ATUALIZACAO.cmd" -SemPausa
)

echo.
set ENVIAR_GITHUB=N
set /p ENVIAR_GITHUB="Deseja sincronizar seus commits com seu GitHub origin agora? [S/N, padrao N]: "
if /i "%ENVIAR_GITHUB%"=="S" (
    echo Enviando para o GitHub: git push origin main...
    git push origin main
)

:FIM
echo.
echo ============================================================
echo          PROCESSO DE ATUALIZACAO CONCLUIDO!
echo ============================================================
echo.
pause