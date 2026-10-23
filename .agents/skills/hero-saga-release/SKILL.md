---
name: hero-saga-release
description: >-
  Runbook do Hero Saga (D:\ro3emu-server) para gerar e subir atualizações do cliente e do launcher (patcher)
  para produção e local. Use quando o usuário pedir para "subir/publicar/gerar a atualização", "mandar att para os
  jogadores", "atualizar o patcher/launcher", mudar versão do cliente/launcher, fazer rollback, ou rodar o ambiente
  local (servidor de patches 8790, Launcher-Local, Launcher-Dist).
---

# Hero Saga — gerar e subir atualizações

Guia humano completo: [docs/GUIA-DESENVOLVEDOR.md](../../../docs/GUIA-DESENVOLVEDOR.md). Este arquivo é o roteiro
do agente. Fale com o usuário em português.

## Como o sistema funciona (o essencial)

- Um único repositório de patches: `patch-server/repo` (produção = Cloudflare R2 `https://patch.herosaga.com.br/`,
  local = `patch-server/server.py` na porta 8790).
- Toda versão do cliente é publicada **duas vezes** (o endereço do servidor fica dentro das DLLs):
  - `producao` → canal `live`, versão `X.Y.Z`, servidor `http://45.130.137.89:5380`, pasta `Launcher-Dist`
  - `local` → canal `test`, versão `X.Y.Z-local`, servidor `http://127.0.0.1:5380`, pasta `Launcher-Local`
  - Configuração: `patch-server/release-config.json`. Última versão publicada: `patch-server/release-state.json`.
- Os dois launchers usam a mesma pasta do cliente (`Client\Ragnarok3`, salva em `%APPDATA%\RO3Emu`). Do launcher
  1.0.9 em diante, trocar de um para o outro só troca as DLLs (não re-hash dos 21 GB).
- `patch-server/.publish-cache.json.zst` evita reler o jogo: 1ª publicação ~2 min, depois ~30 s.

## Roteiro: "sobe a atualização"

1. **Estado do git**: `git status --short`. Avise se houver mudanças não commitadas relevantes (elas entram no build).
   Se `patch-server/repo/channels/test.json` estiver modificado por um `-SoLocal` antigo, tudo bem: o PUBLICAR sobrescreve.
2. **Plano (dry-run)**:
   `powershell -NoProfile -ExecutionPolicy Bypass -File tools\release\publicar.ps1 -Simular -SemPausa`
   Mostre o plano ao usuário (versões do cliente/launcher). Se disser "Nada mudou" e o usuário quiser mesmo assim,
   use `-Cliente`, `-Launcher` ou `-Forcar`. Versão específica: `-Versao 0.1.0` / `-VersaoLauncher 1.1.0`.
3. **Publicar** (demora; rode em background e espere a notificação):
   `powershell -NoProfile -ExecutionPolicy Bypass -File tools\release\publicar.ps1 -SemPausa [opções]`
   Sucesso = exit code 0 e a linha `PRONTO`. Confira:
   - `patch-server/repo/channels/live.json` e `test.json` com a versão nova (`X.Y.Z` e `X.Y.Z-local`);
   - `patch-server/release-state.json` atualizado;
   - se o launcher mudou: `<Version>` novo em `src/Ro3Emu.Launcher/Ro3Emu.Launcher.csproj` e exe copiado para
     `Launcher-Dist`, `Launcher-Local`, raiz e `patch-server/repo/HeroLauncher.exe`.
   Nada foi para a internet ainda.
4. **Teste local** (pergunte se o usuário quer testar antes): `runserver.bat`, `Launcher-Local\iniciar-patch-server.bat`
   (ou `python patch-server\server.py`), `Launcher-Local\HeroLauncher.exe`. Log: `Launcher-Local\launcher.log`.
5. **Enviar para produção — SÓ com confirmação explícita do usuário** (afeta todos os jogadores):
   - Precisa de `r2-credentials.txt` na raiz (2 linhas: access key id, secret) ou `R2_ACCESS_KEY_ID`/`R2_SECRET_ACCESS_KEY`.
     Se faltar, peça ao usuário; nunca grave as chaves em arquivo versionado nem as repita no chat.
   - Dry-run: `python upload_to_r2.py --simular` (mostra quantos arquivos e quais canais).
   - Envio: `python upload_to_r2.py` (ou `ENVIAR_ATUALIZACAO.cmd`, que tem `pause` no fim — prefira o python direto).
     Ele envia chunks/manifestos/launchers primeiro e `channels/live.json` por último, só se nada falhou; ignora
     `channels/test.json` e manifestos `*-local` / `*-dev.*`. Se falhar no meio, é só rodar de novo.
   - Verificação: `Invoke-WebRequest https://patch.herosaga.com.br/channels/live.json` deve mostrar a versão nova
     (pode haver cache do Cloudflare por alguns minutos).
6. **Commit** (o usuário costuma pedir "pode commitar"). Antes, faça o checklist de versão abaixo
   ("OBRIGATÓRIO: sempre que a versão ... mudar"):
   `git add patch-server/release-state.json patch-server/repo/channels patch-server/repo/manifests patch-server/repo/news.json docs/GUIA-DESENVOLVEDOR.md src/Ro3Emu.Launcher/Ro3Emu.Launcher.csproj`
   Mensagem: `release: cliente X.Y.Z, launcher A.B.C`. Push só se o usuário pedir.

## Regras de versão

- Desde a 1.1.0, **cliente e launcher têm sempre a mesma versão** (pedido do usuário): mudou um dos dois, o PUBLICAR
  publica os dois juntos com o mesmo número (sobe o último número da maior das duas: 1.1.0 → 1.1.1).
- `MAIOR.MENOR.CORREÇÃO`: qualquer mudança no cliente ou no launcher → CORREÇÃO (automático); jogo oficial
  atualizou / conteúdo grande → MENOR (`-Versao 1.2.0`); marco → MAIOR. Nunca reaproveitar número (manifestos são
  imutáveis; o script recusa). Local = mesma versão + `-local`; `-dev.<data>` = teste pessoal (`-SoLocal`).
- O launcher do jogador só se atualiza se a versão for **maior**.
- A versão na tela de login do jogo vem de `ro3emu/version.txt` (o ReleaseBuilder grava em cada versão publicada;
  `{version}` em `src/Ro3Emu.Loc/en_overrides.tsv`). Não editar números de versão à mão nesse arquivo.
- Mudança só no servidor (emulador) não precisa de PUBLICAR/ENVIAR, mas o servidor de produção precisa ser atualizado.
  Recursos que mexem nos dois lados (ex.: slots 7-9 da barra) só funcionam com o servidor novo rodando.

### OBRIGATÓRIO: sempre que a versão do cliente ou do launcher (patch) mudar, atualizar a versão em todo lugar

Pedido fixo do usuário. Depois de um PUBLICAR que trocou a versão do cliente (`X.Y.Z`) e/ou do launcher (`A.B.C`),
antes de enviar e commitar, confira e atualize:

1. `patch-server/repo/news.json`: o card de patch notes da versão nova no topo, com `"tag": "PATCH X.Y.Z"` e
   `"title": "Patch Notes vX.Y.Z - ..."` (mesma versão do cliente publicado). Se o launcher mudou, cite a versão
   dele (`Atualizador A.B.C`) nas notas. Nunca deixe o card com a versão anterior.
2. `docs/GUIA-DESENVOLVEDOR.md`: a linha `| Versão | X.Y.Z | X.Y.Z-local |` da tabela produção/local e os exemplos
   que citam a versão atual.
3. Confirme que o PUBLICAR gravou: `patch-server/release-state.json` (`clientVersion` / `launcherVersion`),
   `channels/live.json` e `channels/test.json`, e `<Version>` em `src/Ro3Emu.Launcher/Ro3Emu.Launcher.csproj`
   (quando o launcher mudou).
4. Envie (`python upload_to_r2.py`, com confirmação) e confira em `https://patch.herosaga.com.br/news.json` e
   `channels/live.json` que as duas versões aparecem.
5. Inclua `patch-server/repo/news.json` e `docs/GUIA-DESENVOLVEDOR.md` no commit `release: cliente X.Y.Z, launcher A.B.C`.

## Rollback

`git log --oneline -- patch-server/repo/channels/live.json` → `git checkout <commit> -- patch-server/repo/channels/live.json`
→ `python upload_to_r2.py` (com confirmação do usuário).

## Outras coisas úteis

- `-SoLocal`: publica só o canal `test` com versão `X.Y.Z-dev.<data>` (não altera `release-state.json`). Não commitar
  o `channels/test.json` resultante.
- `-SemCache`: relê o jogo inteiro (se desconfiar do cache).
- `ATUALIZAR RO3EMUSERVER.cmd`: só verifica o upstream (aethai/ro3emu-server); a integração é a skill `upstream-sync`.
- Site (repositório separado `D:\ro3emu-site`): `iniciar-site.bat` → http://localhost:3000.
- Arquivos `.cs`/`.md` do repo ficam em CRLF no disco (autocrlf); para editar com ferramentas de substituição exata,
  converta o arquivo para LF antes (o git normaliza, o diff não muda).
- Testes do motor de atualização: `dotnet test tests/Ro3Emu.Patcher.Tests`.
