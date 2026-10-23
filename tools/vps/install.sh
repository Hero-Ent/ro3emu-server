#!/bin/bash
# Hero Saga - instala na VPS um servidor ja compilado (tools/vps/deploy.ps1 envia o pacote e chama este script).
# Compilar este servidor pede ~4,5 GB de RAM; a VPS tem 4 GB, entao o build e feito no PC e so os binarios vem para ca.
# O servidor continua sendo controlado pelo menu "sv" (sessao screen "ro3emu").
# Uso: install.sh <pacote.tar.gz>
set -euo pipefail
ROOT=/opt/ro3emu-server
BIN="$ROOT/bin/Release"
PKG="$1"

sv desligar || true
systemctl stop ro3emu 2>/dev/null || true
# Servico systemd de um deploy antigo: o servidor volta a ser so do sv.
if [ -f /etc/systemd/system/ro3emu.service ]; then
  systemctl disable ro3emu >/dev/null 2>&1 || true
  rm -f /etc/systemd/system/ro3emu.service
  systemctl daemon-reload
fi
pkill -f "$BIN/Ro3Emu.Server" 2>/dev/null || true
sleep 2

# Guarda o servidor anterior para voltar com: sv desligar; rm -rf bin/Release; mv bin/Release.prev bin/Release; sv ligar
rm -rf "$ROOT/bin/Release.prev"
cp -a "$BIN" "$ROOT/bin/Release.prev"
# Por cima do anterior: appsettings*.json e a pasta fakeplayers da VPS ficam.
tar -xzf "$PKG" -C "$BIN" --exclude='appsettings*.json'
chmod +x "$BIN/Ro3Emu.Server"
rm -f "$PKG"

sv ligar
