@echo off
chcp 65001 >nul
title Compilar RO3Emu Server - Modo Debug
cd /d "%~dp0"

echo ============================================================
echo          COMPILANDO RO3EMU SERVER NO MODO DEBUG
echo ============================================================
echo.

set "PATH=%PATH%;C:\Program Files\dotnet"
where dotnet >nul 2>&1
if errorlevel 1 (
    echo [ERRO] O .NET SDK nao foi encontrado no PATH do sistema.
    echo Certifique-se de que o .NET 10 SDK esta instalado.
    echo.
    pause
    exit /b 1
)

echo [1/4] Compilando Servidor (Ro3Emu.Server) em Debug...
dotnet build src/Ro3Emu.Server -c Debug -v q -nologo -clp:ErrorsOnly
if errorlevel 1 goto FALHA

echo [2/4] Compilando Setup (Ro3Emu.Setup) em Debug...
dotnet build src/Ro3Emu.Setup -c Debug -v q -nologo -clp:ErrorsOnly
if errorlevel 1 goto FALHA

echo [3/4] Compilando ClientHooks e Loc em Debug...
dotnet build src/Ro3Emu.ClientHooks -c Debug -v q -nologo -clp:ErrorsOnly
dotnet build src/Ro3Emu.Loc -c Debug -v q -nologo -clp:ErrorsOnly

echo [4/4] Compilando Fake Players (Ro3Emu.FakePlayers) em Release...
dotnet build src/Ro3Emu.FakePlayers -c Release -v q -nologo -clp:ErrorsOnly
if errorlevel 1 goto FALHA

echo.
echo ============================================================
echo [SUCESSO] Compilacao no Modo Debug concluida com sucesso!
echo.
echo Executavel do Servidor gerado em:
echo   src\Ro3Emu.Server\bin\Debug\net10.0\Ro3Emu.Server.exe
echo.
echo Executavel do Setup/Launcher gerado em:
echo   src\Ro3Emu.Setup\bin\Debug\net10.0\RO3Emu.exe
echo ============================================================
echo.
pause
exit /b 0

:FALHA
echo.
echo [ERRO] Ocorreu uma falha durante a compilacao em modo Debug.
echo.
pause
exit /b 1
