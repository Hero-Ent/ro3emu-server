@echo off
rem Hero Saga - envia o repositorio de patches (patch-server\repo) para a producao (Cloudflare R2).
rem Chunks, manifestos e launchers primeiro; channels\live.json por ultimo. Veja upload_to_r2.py.
rem Credenciais: arquivo r2-credentials.txt na raiz (nao vai para o git) ou variaveis R2_ACCESS_KEY_ID / R2_SECRET_ACCESS_KEY.
chcp 65001 >nul
title Hero Saga - Enviar Atualizacao para Producao
cd /d "%~dp0"

where python >nul 2>&1
if errorlevel 1 (
    echo [ERRO] Python nao encontrado no PATH. Instale o Python 3.10+ ^(marque "Add to PATH"^).
    pause
    exit /b 1
)
python -c "import boto3" >nul 2>&1
if errorlevel 1 (
    echo Instalando a biblioteca boto3...
    python -m pip install boto3
)

python "%~dp0upload_to_r2.py" %*
set "CODE=%ERRORLEVEL%"
echo.
pause
exit /b %CODE%
