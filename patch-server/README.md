# Servidor de Patches e Atualizações - Hero Saga

Este diretório contém toda a estrutura para hospedar o servidor de patches do Hero Saga (Ragnarok 3), tanto localmente para testes quanto em uma VPS de produção.

---

## 1. Testar Localmente

Para rodar o servidor de patches na sua máquina local:

1. Dê dois cliques em `start-patchserver.bat` (na raiz do projeto) ou execute:
   ```powershell
   .\start-patchserver.ps1
   ```
2. O servidor iniciará em `http://localhost:8790/`.
3. Abra o launcher (`HeroLauncher.exe`). Ele irá conectar automaticamente, verificar as atualizações e exibir a mensagem:
   > **"Tudo atualizado. Bom jogo!"** com o botão **JOGAR** ativo e as notícias carregadas do `news.json`!

---

## 2. Estrutura do Repositório (`repo/`)

- `channels/test.json`: Arquivo dinâmico que aponta para o manifesto e launcher atuais daquele canal.
- `manifests/`: Manifestos versionados e compactados em zstd (`1.0.0.json.zst`), com hashes SHA-256 de cada arquivo.
- `chunks/`: Blocos de arquivos compactados com FastCDC e zstd, identificados por hash SHA-256.
- `launcher/<versão>/HeroLauncher.exe`: Binários do launcher para auto-atualização transparente.
- `HeroLauncher.exe`: Cópia do executável para download direto de novos jogadores.
- `news.json`: Feed de avisos e novidades exibido diretamente no card do launcher.

---

## 3. Como Gerar ou Publicar Atualizações

### Criar uma nova versão do launcher:
```powershell
.\build-launcher.ps1 -Version "1.0.1"
dotnet run --project src/Ro3Emu.ClientTools -- release-launcher patch-server/repo test HeroLauncher.exe 1.0.1
```

### Criar uma nova versão do cliente com modificações:
```powershell
dotnet run --project src/Ro3Emu.ClientTools -- release "Client/Ragnarok3" "http://SEU_IP:5380" "Client/RO3Emu/data/gate_rsa_public.pem" "patch-server/repo" "1.0.1" "test"
```

---

## 4. Subir para a VPS de Produção

Veja as instruções detalhadas em [`vps/README-VPS.md`](vps/README-VPS.md).

De forma rápida:
1. No terminal da VPS, execute:
   ```bash
   sudo bash setup-vps-patchserver.sh
   ```
2. No seu computador local, sincronize os patches para a VPS:
   ```powershell
   .\patch-server\sync-to-vps.ps1 -VpsHost "IP_DA_SUA_VPS" -User "root"
   ```
3. No arquivo `launcher.json` do launcher, altere `repository` para `http://IP_DA_SUA_VPS:8790/` e gere a versão de produção.
