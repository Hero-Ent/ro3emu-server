---
name: upstream-sync
description: >-
  Roteiro do Hero Saga (D:\ro3emu-server) para acompanhar e integrar os commits do upstream aethai/ro3emu-server.
  Use quando o usuario pedir "analisa o upstream", "o que tem de novo no aethai/ro3emu", "puxa os commits deles",
  "atualiza o emulador com o upstream", "integra/aplica os commits aprovados", ou rodar o
  "ATUALIZAR RO3EMUSERVER.cmd". Cobre: relatorio em portugues por grupo, aprovacao do usuario, merge em branch,
  resolucao de conflitos preservando as customizacoes do Hero Saga, build, testes e registro do sync.
---

# Hero Saga - sincronizar com o upstream (aethai/ro3emu-server)

Fale com o usuario em portugues. O Hero Saga e um fork com historico compartilhado: `upstream` =
`https://github.com/aethai/ro3emu-server.git`, `origin` = `Hero-Ent/ro3emu-server`. O ultimo sync e sempre o
`git merge-base HEAD upstream/main`. Historico de syncs e rejeicoes: [docs/UPSTREAM-SYNC.md](../../../docs/UPSTREAM-SYNC.md).

## Regras de ouro

- **Nunca integrar sem aprovacao explicita** do usuario sobre o relatorio.
- **Merge, nunca cherry-pick/rebase.** O merge mantem a ancestralidade: o proximo sync so mostra o que e novo.
  Commit/grupo rejeitado = merge de tudo + `git revert` do rejeitado (o revert fica registrado e nao volta a aparecer).
  Para "so ate tal ponto", faca merge do hash daquele ponto em vez de `upstream/main`.
- **Nunca** `git stash` automatico, `push`, `reset --hard`, nem publicar release sem o usuario pedir
  (publicacao e a skill `hero-saga-release`).
- Trabalhe sempre na branch `sync/upstream-AAAAMMDD`; a `main` so recebe o resultado depois de build + testes verdes.

## Fase 1 - Analise ("analisa o upstream")

1. Rode o verificador (somente leitura):
   `powershell -NoProfile -ExecutionPolicy Bypass -File tools\upstream\status.ps1 -SemPausa`
   Ele faz fetch, simula o merge e gera `.upstream-sync\dossie.md` (mensagem completa, PR de origem e arquivos de
   cada commit, marcados `[CONFLITO]` ou `[NOSSO]`).
2. Leia o dossie. Economia de custo:
   - Commits sem `[CONFLITO]`/`[NOSSO]`: a mensagem + lista de arquivos bastam (as mensagens deles sao detalhadas).
   - Commits com `[CONFLITO]`/`[NOSSO]`: leia o diff (`git show <hash> -- <arquivo>`) e compare com o que o Hero Saga
     mudou no mesmo arquivo (`git log --oneline <base>..HEAD -- <arquivo>` e `git diff <base> HEAD -- <arquivo>`).
   - Lotes grandes (>40 commits): divida por PR/tema entre subagentes `research`, cada um devolvendo o resumo no
     formato abaixo.
3. Escreva o relatorio como artifact `upstream-relatorio-AAAAMMDD.md`, **agrupado por tema/PR**
   (ex.: Voz da guild, Combate/Habilidades, Profissoes, Qualidade/CI, Dependencias, Docs). Para cada grupo:
   - **O que e**: correcao / funcionalidade / refatoracao / ferramenta-CI / dependencia / docs.
   - **Explicacao em portugues** do efeito no jogo ou no servidor (nao so traduzir o titulo).
   - **Commits** (hash curto + titulo).
   - **Impacto no Hero Saga**: conflita? com qual customizacao nossa? muda balanceamento/rates/protocolo/cliente?
     exige atualizar DLLs do cliente (`src/Ro3Emu.ClientHooks`, `src/Ro3Emu.Loc`, `prebuilt/client`) e portanto uma
     nova versao do cliente?
   - **Recomendacao**: Aplicar / Aplicar com ajuste / Pular (e por que).
   No topo: totais, conflitos previstos e riscos especiais. No fim: tabela de aprovacao (grupo -> recomendacao).
4. Pergunte ao usuario quais grupos aprova (padrao: por grupo; commit a commit so se ele pedir).

### Riscos conhecidos a sempre checar

- `Directory.Build.props` com `TreatWarningsAsErrors`, `AnalysisLevel` e `EnforceCodeStyleInBuild`: codigo nosso
  com warnings passa a quebrar o build. Corrija os warnings no nosso codigo (preferivel) em vez de desligar o gate.
- Commits de `dotnet format` (so espacamento): aumentam conflitos; na resolucao, mantenha a logica nossa e aceite
  a formatacao deles; depois rode `dotnet format whitespace` nos arquivos resolvidos.
- `prebuilt/client/*.dll` binarios: nunca resolver escolhendo um lado as cegas; regenere apos o merge
  (`pack.ps1 -UpdatePrebuiltClientDlls`, ver README) e lembre que isso implica nova versao do cliente.
- Mudancas de protocolo/cliente (Lua payload, ClientHooks): avisar que exigem publicar cliente novo.

## Licoes do sync de 2026-10-22 (145 commits)

- **Merge em etapas**: corte nos merges da linha principal (`git log --first-parent`) e isole PRs mecanicos
  grandes (renomeacao, formatacao) numa etapa so deles. Build + testes verdes ao fim de cada etapa.
- **Antes de um PR de formatacao**: rode `dotnet format whitespace --folder` no nosso codigo e commite; os
  conflitos de espaco somem.
- **Antes de um PR de renomeacao em massa**: aplique a mesma renomeacao do nosso lado com Roslyn, so nos tipos
  que alteramos, usando a arvore do upstream como referencia (nome novo existe la e o antigo nao); depois o
  merge funde as linhas iguais sozinho. Referencias a campos renomeados que sobrarem viram CS0103 no build.
- Lado "deles" de um conflito igual a base (so renomeado/formatado) = fique com o nosso trecho, nunca com o
  arquivo inteiro (`--ours` apagaria as mudancas deles fora do conflito).
- **Nunca** crie junction/symlink dentro de um worktree temporario: `git worktree remove --force` apaga o conteudo
  do alvo (aconteceu com `Client/RO3Emu/data`; restaurado por `git checkout -- Client/RO3Emu/data`).
- Testes `[ClientDataFact]` precisam de `data/config` (dados reais em `Client/RO3Emu/data`); falhas de EXP vem do
  `scripts/exp_bonus.json` (multiplicador do servidor) e nao do merge: compare sempre com a `main`.
- `pwsh` nao esta instalado: `client-stamp.ps1` nao roda no Windows PowerShell; calcule o carimbo com
  `git hash-object -- <arquivos>` (nunca por pipe: o PowerShell 5 poe BOM no stdin do git).

## Fase 2 - Integracao (so depois do "aprovado")

1. `git status --short`: se houver mudancas nao commitadas, pare e pergunte (commitar ou adiar). Nao faca stash.
2. `git switch main` e `git switch -c sync/upstream-AAAAMMDD`.
3. `git merge --no-ff upstream/main -m "Merge upstream aethai/ro3emu-server ate <hash curto>"`
   (ou o hash de corte combinado).
4. Conflitos: resolva arquivo por arquivo, entendendo os dois lados:
   - **Intencao do Hero Saga prevalece** (balanceamento, rates, skills customizadas, traducoes, launcher/patcher,
     ferramentas de release, branding); **correcoes e funcionalidades do upstream entram por cima** dessa intencao.
   - Se o upstream reescreveu/renomeou algo que alteramos, porte a nossa mudanca para a estrutura nova.
   - `.csproj` / `.gitignore` / `Directory.Packages.props`: uniao dos dois lados.
   - Duvida real de regra de jogo: pergunte ao usuario em vez de adivinhar.
   - `git diff --check` e procure marcadores `<<<<<<<` antes do commit.
5. Grupos rejeitados: depois do commit de merge, reverta os commits deles em ordem cronologica inversa
   (`git revert --no-edit <hash>...`; para um PR inteiro, `git revert -m 1 <merge do PR>`).
6. Build e testes (dotnet em `C:\Program Files\dotnet` se nao estiver no PATH):
   - `dotnet build Ro3Emu.slnx -c Release -nologo`
   - `dotnet test Ro3Emu.slnx -c Release -nologo`
   - se `src/Ro3Emu.ClientHooks` ou `src/Ro3Emu.Loc` mudaram: atualizar `prebuilt/client` e avisar sobre versao nova.
   Corrija ate ficar verde. Testes nossos que quebraram por mudanca legitima do upstream: ajuste e explique.
7. Mostre ao usuario um resumo do que entrou, como cada conflito foi resolvido e o que foi revertido.
   Com o ok dele: `git switch main` e `git merge --ff-only sync/upstream-AAAAMMDD`, depois apague a branch.
8. Registre em [docs/UPSTREAM-SYNC.md](../../../docs/UPSTREAM-SYNC.md): data, faixa `base..alvo`, grupos aplicados,
   grupos rejeitados (com hash e motivo), conflitos relevantes. Commit: `docs: registro do sync upstream AAAA-MM-DD`.
9. Ofereca (sem executar): `git push origin main` e, se cliente/launcher mudou, publicar via `hero-saga-release`.
