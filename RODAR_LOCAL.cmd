@echo off
chcp 65001 >nul
title RO3Emu - Ambiente Local (localhost)
cd /d "%~dp0"

rem Desenvolvimento local: compila o servidor (Debug), sobe em 127.0.0.1 numa janela propria e abre o cliente
rem Client\Ragnarok3 (ja patcheado para http://127.0.0.1:5380 com a chave de Client\RO3Emu\data).
rem Diferencas para producao: ambiente Development, registro automatico de conta no login e GM para todas as contas.
rem uso: RODAR_LOCAL.cmd [semcompilar] [semcliente] [servidor]   (servidor = so o servidor, nesta janela)

set "PATH=%PATH%;C:\Program Files\dotnet"
set "DATADIR=%~dp0Client\RO3Emu\data"
set "SERVER_EXE=%~dp0src\Ro3Emu.Server\bin\Debug\net10.0\Ro3Emu.Server.exe"
set "CLIENT_DIR=%~dp0Client\Ragnarok3"
rem ro3.exe sai com codigo 70 sem a launcher proof (RO3_LAUNCH_PIPE_HANDLE): a ClientTools "launch" entrega a prova.
set "CLIENTTOOLS_EXE=%~dp0src\Ro3Emu.ClientTools\bin\Debug\net10.0\win-x64\Ro3Emu.ClientTools.exe"
set "SERVER_ARGS=--environment Development --Ro3Emu:DataDir="%DATADIR%" --Ro3Emu:PublicHost=127.0.0.1 --Ro3Emu:PreloadGameData=true --Ro3Emu:AllowInGameRegistration=true"

set "BUILD=1"
set "CLIENT=1"
set "INLINE=0"
for %%A in (%*) do (
    if /i "%%~A"=="semcompilar" set "BUILD=0"
    if /i "%%~A"=="semcliente" set "CLIENT=0"
    if /i "%%~A"=="servidor" set "INLINE=1" & set "CLIENT=0"
)

if not exist "%~dp0Client\Ragnarok3\ro3_Data\StreamingAssets\VFS" (
    echo [Error]: Client\Ragnarok3\ro3_Data\StreamingAssets\VFS nao existe ^(24 GB, fora do git^).
    echo [Notice]: Crie a junction para um cliente completo, por exemplo:
    echo   mklink /J "%~dp0Client\Ragnarok3\ro3_Data\StreamingAssets\VFS" "D:\Patch Hero Saga RO3\Client\Ragnarok3\ro3_Data\StreamingAssets\VFS"
    pause
    exit /b 1
)

if "%BUILD%"=="1" (
    echo [Status]: Compilando Ro3Emu.Server ^(Debug^)...
    dotnet build src/Ro3Emu.Server -c Debug -v q -nologo -clp:ErrorsOnly
    if errorlevel 1 (
        echo [Error]: Falha na compilacao.
        pause
        exit /b 1
    )
    if "%CLIENT%"=="1" (
        echo [Status]: Compilando Ro3Emu.ClientTools ^(Debug^)...
        dotnet build src/Ro3Emu.ClientTools -c Debug -v q -nologo -clp:ErrorsOnly
        if errorlevel 1 (
            echo [Error]: Falha na compilacao.
            pause
            exit /b 1
        )
    )
)

if not exist "%SERVER_EXE%" (
    echo [Error]: %SERVER_EXE% nao encontrado. Rode sem "semcompilar".
    pause
    exit /b 1
)

if "%INLINE%"=="1" (
    "%SERVER_EXE%" %SERVER_ARGS%
    pause
    exit /b %ERRORLEVEL%
)

echo [Status]: Subindo o servidor em outra janela ^(HTTP 5380, micro gate 20000, game gate 33100^)...
start "RO3Emu Server (local)" cmd /k ""%SERVER_EXE%" %SERVER_ARGS%"

if "%CLIENT%"=="0" exit /b 0

echo [Status]: Aguardando o servidor responder...
powershell -NoProfile -Command "for ($i = 0; $i -lt 180; $i++) { try { Invoke-WebRequest -UseBasicParsing -TimeoutSec 2 'http://127.0.0.1:5380/serverlist/v2/168/serverlist.json' | Out-Null; exit 0 } catch { Start-Sleep 1 } }; exit 1"
if errorlevel 1 (
    echo [Error]: O servidor nao respondeu em 3 minutos. Veja a janela do servidor.
    pause
    exit /b 1
)

echo [Status]: Abrindo o cliente. Login com qualquer conta e senha: a conta e criada no primeiro login.
start "RO3 Client (local)" /min "%CLIENTTOOLS_EXE%" launch "%CLIENT_DIR%"
exit /b 0
