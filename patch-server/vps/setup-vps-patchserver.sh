#!/usr/bin/env bash
# ==============================================================================
# Hero Saga - Configuração Automatizada do Servidor de Patches na VPS Linux
# ==============================================================================
set -euo pipefail

echo "=================================================================="
echo "  Hero Saga - Instalador do Servidor de Patches (Linux VPS)"
echo "=================================================================="

# 1. Verifica privilégios de root
if [ "$EUID" -ne 0 ]; then
  echo "Por favor, execute como root ou com sudo: sudo bash $0"
  exit 1
fi

# 2. Instala Nginx se não estiver instalado
if ! command -v nginx >/dev/null 2>&1; then
  echo "Instalando Nginx..."
  apt-get update -y
  apt-get install -y nginx
fi

# 3. Cria diretório do repositório
REPO_DIR="/var/www/hero-patches/repo"
echo "Criando diretório $REPO_DIR..."
mkdir -p "$REPO_DIR"/{channels,manifests,chunks,launcher}
chown -R www-data:www-data /var/www/hero-patches
chmod -R 755 /var/www/hero-patches

# 4. Copia configuração do Nginx
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
CONF_SRC="$SCRIPT_DIR/nginx-patchserver.conf"

if [ -f "$CONF_SRC" ]; then
  cp "$CONF_SRC" /etc/nginx/sites-available/hero-patches.conf
else
  echo "Aviso: nginx-patchserver.conf não encontrado no diretório do script."
fi

# 5. Ativa site no Nginx
ln -sf /etc/nginx/sites-available/hero-patches.conf /etc/nginx/sites-enabled/
nginx -t
systemctl reload nginx || systemctl restart nginx
systemctl enable nginx

# 6. Abre a porta no firewall UFW se estiver ativo
if command -v ufw >/dev/null 2>&1 && ufw status | grep -q "Status: active"; then
  echo "Abrindo porta 8790 no firewall UFW..."
  ufw allow 8790/tcp comment "Hero Saga Patch Server"
fi

echo "=================================================================="
echo "  Instalação concluída com sucesso!"
echo "  Repositório: $REPO_DIR"
echo "  Porta: 8790 (HTTP)"
echo "=================================================================="
echo "Para sincronizar os arquivos do seu PC para esta VPS, use o script:"
echo "  patch-server/sync-to-vps.ps1"
echo "=================================================================="
