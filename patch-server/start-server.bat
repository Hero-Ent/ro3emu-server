@echo off
title Hero Saga - Servidor de Patches (Porta 8790)
cd /d "%~dp0"
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0start-server.ps1"
pause
