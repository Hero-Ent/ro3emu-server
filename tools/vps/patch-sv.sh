#!/bin/bash
# Hero Saga - ajustes no menu sv (feito pelo agente em 2026-10-22). Backup: /usr/local/bin/sv.bak-agente
set -euo pipefail
SV=/usr/local/bin/sv
[ -f /usr/local/bin/sv.bak-agente ] || cp -a "$SV" /usr/local/bin/sv.bak-agente

# 1) Servidor sempre com no maximo 4 backups do banco (cada um tem o tamanho do ro3emu.db).
grep -q 'BackupKeep=4' "$SV" || sed -i 's|--Ro3Emu:GmAccounts:1=admin"|--Ro3Emu:GmAccounts:1=admin --Ro3Emu:BackupKeep=4"|' "$SV"

# 2) Compilacao com menos memoria: compilar este servidor pede ~4,5 GB (a VPS tem 4 GB).
LOWMEM='-p:UseSharedCompilation=false -maxcpucount:1 -p:AnalysisLevel=none -p:EnforceCodeStyleInBuild=false -p:TreatWarningsAsErrors=false'
grep -q 'UseSharedCompilation=false' "$SV" || sed -i "s|^    dotnet publish src/Ro3Emu.Server -c |    DOTNET_gcServer=0 DOTNET_GCConserveMemory=7 dotnet publish src/Ro3Emu.Server $LOWMEM -c |" "$SV"
grep -q 'Compilar na VPS pede' "$SV" || sed -i 's|^compilar() {$|compilar() {\n    echo -e "${C_YELLOW}[AVISO]${C_RESET} Compilar na VPS pede ~4,5 GB de RAM e pode levar muitos minutos. Mais rapido: no PC, tools\\\\vps\\\\deploy.ps1"|' "$SV"

# 3) Convivencia com o servico systemd "ro3emu" (usado so por um deploy antigo).
grep -q 'systemctl stop ro3emu' "$SV" || sed -i 's|^desligar() {$|desligar() {\n    systemctl stop ro3emu 2>/dev/null \|\| true|' "$SV"
grep -q 'servico systemd ro3emu' "$SV" || sed -i 's|^ligar() {$|ligar() {\n    if systemctl is-active --quiet ro3emu 2>/dev/null; then echo -e "${C_YELLOW}[AVISO]${C_RESET} O servidor ja esta rodando (servico systemd ro3emu). Use sv desligar antes."; return 0; fi|' "$SV"

bash -n "$SV"
echo "sv ajustado"
grep -n 'BackupKeep=4\|UseSharedCompilation=false\|systemctl stop ro3emu\|servico systemd ro3emu\|Compilar na VPS pede' "$SV" | cut -c1-160
