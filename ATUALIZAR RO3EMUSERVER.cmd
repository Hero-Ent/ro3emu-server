@echo off
rem Hero Saga - verifica o upstream aethai/ro3emu-server SEM aplicar nada (somente leitura).
rem Mostra os commits pendentes, os conflitos previstos e gera .upstream-sync\dossie.md.
rem A integracao e feita pelo agente apos sua aprovacao: peca "analisa o upstream"
rem (roteiro em .agents\skills\upstream-sync\SKILL.md).
chcp 65001 >nul
title Hero Saga - Verificar Upstream (aethai/ro3emu-server)
cd /d "%~dp0"
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\upstream\status.ps1" %*
exit /b %ERRORLEVEL%
