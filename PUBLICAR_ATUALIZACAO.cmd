@echo off
rem Hero Saga - publica cliente e/ou launcher para PRODUCAO e LOCAL de uma vez (veja tools\release\publicar.ps1).
rem Exemplos: PUBLICAR_ATUALIZACAO.cmd            (detecta sozinho o que mudou)
rem           PUBLICAR_ATUALIZACAO.cmd -Forcar     (publica cliente e launcher)
rem           PUBLICAR_ATUALIZACAO.cmd -Simular    (so mostra o plano)
chcp 65001 >nul
title Hero Saga - Publicar Atualizacao
cd /d "%~dp0"
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\release\publicar.ps1" %*
exit /b %ERRORLEVEL%
