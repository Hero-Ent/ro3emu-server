# Guia de Instalação na VPS de Produção (Linux)

Este guia explica como configurar o servidor de patches do Hero Saga em uma VPS Linux (Ubuntu 20.04/22.04/24.04 ou Debian 11/12).

---

## Opção 1: Instalação Automática com Nginx (Recomendado)

### Passo 1: Transferir os arquivos de configuração para a VPS
Copie a pasta `vps` para a VPS ou clone o repositório:
```bash
scp -r patch-server/vps root@IP_DA_SUA_VPS:/root/
```

### Passo 2: Executar o script de configuração
Acesse o terminal da sua VPS via SSH e rode:
```bash
cd /root/vps
sudo bash setup-vps-patchserver.sh
```

O script irá:
- Instalar e configurar o Nginx com headers otimizados (CORS, cache, etc.).
- Criar `/var/www/hero-patches/repo`.
- Habilitar o site na porta `8790`.
- Liberar a porta no firewall `ufw` automaticamente.

### Passo 3: Enviar os arquivos de patches para a VPS
No seu computador Windows, abra o PowerShell no diretório do projeto e execute:
```powershell
.\patch-server\sync-to-vps.ps1 -VpsHost "IP_DA_SUA_VPS" -User "root"
```

---

## Opção 2: Usando Docker Compose

Se a sua VPS já tiver o Docker instalado, você pode rodar simplesmente com:
```bash
cd patch-server/vps
docker compose up -d
```
O servidor estará ativo imediatamente na porta `8790`.

---

## Passo 4: Configurar o Launcher para Produção

1. Edite `src/Ro3Emu.Launcher/launcher.json`:
   ```json
   {
     "repository": "http://IP_DA_SUA_VPS:8790/",
     "channel": "test",
     "server": "http://IP_DA_SUA_VPS:5380",
     "serverName": "Hero Saga"
   }
   ```
2. Compile o executável final:
   ```powershell
   .\build-launcher.ps1 -Version "1.0.0"
   ```
3. Distribua o arquivo `HeroLauncher.exe` gerado para seus jogadores!
