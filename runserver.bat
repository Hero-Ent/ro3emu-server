@echo off
chcp 65001 >nul
title RO3Emu Server - rAthena Console Mode
cd /d "%~dp0"

set "PATH=%PATH%;C:\Program Files\dotnet"

rem 1. Localiza a pasta de dados do emulador (Client\RO3Emu\data ou data)
set "DATADIR="
if exist "%~dp0Client\RO3Emu\data" (
    set "DATADIR=%~dp0Client\RO3Emu\data"
) else if exist "%~dp0data" (
    set "DATADIR=%~dp0data"
)

rem 2. Caminhos dos executaveis
set "DEBUG_EXE=%~dp0src\Ro3Emu.Server\bin\Debug\net10.0\Ro3Emu.Server.exe"
set "RELEASE_EXE=%~dp0src\Ro3Emu.Server\bin\Release\net10.0\Ro3Emu.Server.exe"
set "PREBUILT_EXE=%~dp0Client\RO3Emu\server\Ro3Emu.Server.exe"

set "SERVER_EXE="
set "SERVER_MODE="

rem Verificacao por argumento manual (%1)
if /i "%~1"=="debug" (
    if exist "%DEBUG_EXE%" (
        set "SERVER_EXE=%DEBUG_EXE%"
        set "SERVER_MODE=DEBUG (Forçado via argumento)"
        shift
        goto INICIAR
    ) else (
        echo [Warning]: Executavel Debug nao encontrado. Alternando para deteccao automatica.
    )
)
if /i "%~1"=="release" (
    if exist "%RELEASE_EXE%" (
        set "SERVER_EXE=%RELEASE_EXE%"
        set "SERVER_MODE=NORMAL / RELEASE (Forçado via argumento)"
        shift
        goto INICIAR
    ) else (
        echo [Warning]: Executavel Release nao encontrado. Alternando para deteccao automatica.
    )
)
if /i "%~1"=="normal" (
    if exist "%RELEASE_EXE%" (
        set "SERVER_EXE=%RELEASE_EXE%"
        set "SERVER_MODE=NORMAL / RELEASE (Forçado via argumento)"
        shift
        goto INICIAR
    ) else (
        echo [Warning]: Executavel Release nao encontrado. Alternando para deteccao automatica.
    )
)

rem Deteccao Automatica:
rem Se ambos existirem, verifica qual foi compilado mais recentemente:
if exist "%DEBUG_EXE%" (
    if exist "%RELEASE_EXE%" (
        for /f "delims=" %%M in ('powershell -NoProfile -Command "if ((Get-Item '%DEBUG_EXE%').LastWriteTimeUtc -ge (Get-Item '%RELEASE_EXE%').LastWriteTimeUtc) { 'DEBUG' } else { 'RELEASE' }"') do set "AUTO_CHOICE=%%M"
        if "%AUTO_CHOICE%"=="DEBUG" (
            set "SERVER_EXE=%DEBUG_EXE%"
            set "SERVER_MODE=DEBUG (Compilacao mais recente)"
        ) else (
            set "SERVER_EXE=%RELEASE_EXE%"
            set "SERVER_MODE=NORMAL / RELEASE (Compilacao mais recente)"
        )
        goto INICIAR
    )
)

rem Se apenas um deles existir:
if exist "%DEBUG_EXE%" (
    set "SERVER_EXE=%DEBUG_EXE%"
    set "SERVER_MODE=DEBUG"
    goto INICIAR
)

if exist "%RELEASE_EXE%" (
    set "SERVER_EXE=%RELEASE_EXE%"
    set "SERVER_MODE=NORMAL / RELEASE"
    goto INICIAR
)

rem Se nenhum foi compilado em src/, tenta o pacote pre-compilado:
if exist "%PREBUILT_EXE%" (
    set "SERVER_EXE=%PREBUILT_EXE%"
    set "SERVER_MODE=NORMAL / RELEASE (Pre-compilado)"
    goto INICIAR
)

echo [Error]: Ro3Emu.Server.exe nao encontrado!
echo [Notice]: Compile o servidor primeiro usando COMPILAR_DEBUG.cmd ou dotnet build.
echo.
pause
exit /b 1

:INICIAR
cls
echo ============================================================
echo         RO3EMU SERVER - MODO CONSOLE (Estilo rAthena)
echo ============================================================
echo [Status]: Modo selecionado : %SERVER_MODE%
echo [Status]: Executavel       : %SERVER_EXE%
if not "%DATADIR%"=="" (
    echo [Status]: Pasta de Dados   : %DATADIR%
)
echo ============================================================
echo.

:RUN_LOOP
if not "%DATADIR%"=="" (
    "%SERVER_EXE%" --Ro3Emu:DataDir="%DATADIR%" --Ro3Emu:PreloadGameData=true %*
) else (
    "%SERVER_EXE%" --Ro3Emu:PreloadGameData=true %*
)

set "EXIT_CODE=%ERRORLEVEL%"
echo.
echo ============================================================
if "%EXIT_CODE%"=="0" (
    echo [Status]: Servidor finalizado normalmente (Codigo: 0).
    echo ============================================================
    pause
    exit /b 0
) else (
    echo [Error]: O servidor parou ou travou com codigo de erro: %EXIT_CODE%
    echo ============================================================
    echo.
    set "RESTART=N"
    set /p RESTART="[Notice]: Deseja reiniciar o servidor automaticamente? [S/N, padrao S]: "
    if "%RESTART%"=="" set "RESTART=S"
    if /i "%RESTART%"=="S" (
        echo.
        echo [Status]: Reiniciando servidor...
        echo.
        goto RUN_LOOP
    )
    pause
    exit /b %EXIT_CODE%
)