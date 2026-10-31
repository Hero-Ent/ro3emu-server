#!/bin/bash
# Hero Saga - painel web do servidor so no loopback, atras do site (Painel da Staff em herosaga.com.br/staff).
# O painel abre por padrao em todas as interfaces numa VPS publica (porta 5381). O site (mesma VPS) fala com ele em
# 127.0.0.1 e repassa o IP real do jogador em X-Forwarded-For, que o painel so aceita de um proxy confiavel.
# Uso na VPS: bash patch-panel.sh   (depois: sv desligar && sv ligar)
set -euo pipefail
SV=/usr/local/bin/sv
[ -f /usr/local/bin/sv.bak-panel ] || cp -a "$SV" /usr/local/bin/sv.bak-panel
ARGS='--Ro3Emu:Panel:BindAddress=127.0.0.1 --Ro3Emu:Panel:TrustedProxies:0=127.0.0.1 --Ro3Emu:Panel:RequireAdmin2FA=true'
grep -q 'Panel:BindAddress' "$SV" || sed -i "s|--Ro3Emu:GmAccounts:1=admin|--Ro3Emu:GmAccounts:1=admin $ARGS|" "$SV"
bash -n "$SV"
grep -n 'Panel:BindAddress' "$SV" | cut -c1-200
# A porta 5381 nao precisa (nem deve) ficar aberta para fora.
if command -v ufw >/dev/null 2>&1; then ufw deny 5381/tcp >/dev/null 2>&1 || true; fi
echo "painel ajustado: reinicie o servidor (sv desligar && sv ligar)"
