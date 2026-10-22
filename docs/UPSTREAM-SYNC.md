# Sincronizacao com o upstream (aethai/ro3emu-server)

O Hero Saga e um fork de [aethai/ro3emu-server](https://github.com/aethai/ro3emu-server) com historico
compartilhado. Integramos as novidades deles por **merge** (nunca cherry-pick), depois de revisar e aprovar.

- Verificar o que ha de novo (somente leitura): `ATUALIZAR RO3EMUSERVER.cmd` ou
  `tools\upstream\status.ps1` (gera `.upstream-sync\dossie.md`).
- Analisar e integrar: pedir ao agente "analisa o upstream" (roteiro em `.agents/skills/upstream-sync/SKILL.md`).
- Commits rejeitados entram no merge e sao revertidos logo em seguida; ficam listados abaixo para nao
  serem reavaliados a toa.

## Historico

| Data | Faixa (base..alvo) | Aplicado | Rejeitado (hash - motivo) | Observacoes |
|------|--------------------|----------|---------------------------|-------------|
| 2026-10-12 | fork inicial ..`6befb4b` | tudo | - | import do projeto |
