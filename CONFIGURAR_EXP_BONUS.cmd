@echo off
chcp 65001 >nul
title Hero Saga - Configurador de Bônus de EXP
cd /d "%~dp0"

echo ============================================================
echo         HERO SAGA RO3 - CONFIGURADOR DE BÔNUS DE EXP
echo ============================================================
echo.

if not exist "scripts\exp_bonus.json" (
    if not exist "scripts" mkdir "scripts"
    echo {"multiplier": 50, "description": "Multiplicador de bônus de EXP"} > "scripts\exp_bonus.json"
)

powershell -NoProfile -Command ^
  "$json = Get-Content 'scripts\exp_bonus.json' -Raw | ConvertFrom-Json; Write-Host 'Multiplicador atual de bônus de EXP:' $json.multiplier 'x'"

echo.
echo Exemplos de valores a digitar:
echo   0   = Sem bônus (taxa normal 1x padrão)
echo   1   = +1x bônus (2x total)
echo   10  = +10x bônus (11x total)
echo   50  = +50x bônus (51x total) - Para 50x de bônus de EXP
echo   100 = +100x bônus (101x total) - Para 100x de bônus de EXP
echo.
set /p NOVO_VALOR="Digite o novo multiplicador desejado (ex: 50) [ou Enter para cancelar]: "

if "%NOVO_VALOR%"=="" (
    echo Operação cancelada.
    echo.
    pause
    exit /b 0
)

powershell -NoProfile -Command ^
  "$val = [long]'%NOVO_VALOR%';" ^
  "$obj = [ordered]@{ multiplier = $val; description = 'Multiplicador de bônus de EXP para o servidor Hero Saga (fase Beta). Exemplo: 0 = sem bônus (1x normal), 1 = +1x bônus (2x total), 10 = +10x bônus (11x total), 50 = +50x bônus (51x total), 100 = +100x bônus (101x total).' };" ^
  "$json = $obj | ConvertTo-Json;" ^
  "$json | Set-Content 'scripts\exp_bonus.json' -Encoding UTF8;" ^
  "if (Test-Path 'Client\RO3Emu\server') { if (-not (Test-Path 'Client\RO3Emu\server\scripts')) { New-Item -ItemType Directory -Path 'Client\RO3Emu\server\scripts' -Force | Out-Null }; $json | Set-Content 'Client\RO3Emu\server\scripts\exp_bonus.json' -Encoding UTF8 };" ^
  "if (Test-Path 'src\Ro3Emu.Server\bin\Release\net10.0') { if (-not (Test-Path 'src\Ro3Emu.Server\bin\Release\net10.0\scripts')) { New-Item -ItemType Directory -Path 'src\Ro3Emu.Server\bin\Release\net10.0\scripts' -Force | Out-Null }; $json | Set-Content 'src\Ro3Emu.Server\bin\Release\net10.0\scripts\exp_bonus.json' -Encoding UTF8 };" ^
  "if (Test-Path 'src\Ro3Emu.Server\bin\Debug\net10.0') { if (-not (Test-Path 'src\Ro3Emu.Server\bin\Debug\net10.0\scripts')) { New-Item -ItemType Directory -Path 'src\Ro3Emu.Server\bin\Debug\net10.0\scripts' -Force | Out-Null }; $json | Set-Content 'src\Ro3Emu.Server\bin\Debug\net10.0\scripts\exp_bonus.json' -Encoding UTF8 };" ^
  "Write-Host ''; Write-Host '>>> Bônus de EXP atualizado com sucesso para:' $val 'x! <<<'"

echo.
echo Se o servidor já estiver ligado, a alteração é aplicada AUTOMATICAMENTE em tempo real (hot-reload)!
echo.
pause
