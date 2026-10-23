#!/bin/bash
# Hero Saga - instala na VPS um servidor ja compilado (tools/vps/deploy.ps1 envia o pacote e chama este script).
# Compilar na VPS (2 nucleos, 4 GB) e lento e o disco e pequeno: o build e feito no PC e so os binarios vem para ca.
# Uso: install.sh <pacote.tar.gz>
set -euo pipefail
ROOT=/opt/ro3emu-server
BIN="$ROOT/bin/Release"
PKG="$1"

systemctl stop ro3emu 2>/dev/null || true
pkill -f "$BIN/Ro3Emu.Server" 2>/dev/null || true
sleep 2

# Guarda o servidor anterior para voltar com: systemctl stop ro3emu; rm -rf bin/Release; mv bin/Release.prev bin/Release
rm -rf "$ROOT/bin/Release.prev"
cp -a "$BIN" "$ROOT/bin/Release.prev"
# Por cima do anterior: appsettings*.json e a pasta fakeplayers da VPS ficam.
tar -xzf "$PKG" -C "$BIN" --exclude='appsettings*.json'
chmod +x "$BIN/Ro3Emu.Server"
rm -f "$PKG"

if [ -f /tmp/ro3emu.service ]; then SERVICE=/tmp/ro3emu.service; else SERVICE="$ROOT/tools/vps/ro3emu.service"; fi
mkdir -p "$ROOT/logs"
install -m 0644 "$SERVICE" /etc/systemd/system/ro3emu.service
systemctl daemon-reload
systemctl enable ro3emu >/dev/null 2>&1
systemctl start ro3emu
echo "ro3emu iniciado"
