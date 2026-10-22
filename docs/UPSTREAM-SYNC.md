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
| 2026-10-22 | `6befb4b..9f3c04d8` (145 commits, PRs #10-#61) | tudo, em 4 etapas (`de99453e`, `9cd23f33`, `249a72be`, `9f3c04d8`) | nenhum revertido; ajustes abaixo | ver "Sync 2026-10-22" |

## Sync 2026-10-22

Decisoes do usuario: D1 Double Attack do upstream (o nosso desligado: `CombatService.LegacyDoubleAttack`);
D2 chefes imunes a controle e born buffs em todos os monstros aplicados; D3 balanceamento de talentos/passivas
aplicado; D4 testes do CI mantidos, dependabot removido e release do GitHub so roda no upstream.

Como os conflitos foram resolvidos (o que o Hero Saga manteve):

- **Geometria de area** (`SelectEnemies`): a nossa (88b91f1e, SkillMechanics) para skills comuns; a do upstream
  (a8635010) so para efeitos de pet guardiao.
- **Laco de dano**: `MonsterHitDamage` do upstream recebe os nossos branches (bonus de raca/elemento, branchIf);
  sem branch nosso, vale o do upstream (Heal, Sonic Blow com Supersonic Blow). O `branchIf` do Sonic Blow saiu
  do `thief.json`.
- **HitSplit**: o nosso (por branch, projeteis, cura em aliado); o `HitSplit(row)` do upstream foi removido.
- **Procs (CastResolved)**: uma vez por cast (primeiro golpe dos multi-hits) e nunca em follow-ups `Independent`
  (inferred).
- **Andar nao cancela cast**: mantido o nosso `PlayerMoved`; sem `StartedWalking`/`WalkFromUtc` do upstream.
- **Barra**: `BarGroup`/ataque da carreira do upstream dentro dos nossos 9 slots (`WheelKey`), barra padrao 6.
- **Furtividade**: a nossa generica (categoria 31) + estados de guardiao do upstream.
- **/ro3emu/status**: contagem do launcher (contas no jogo + micro) lida do censo (`SessionCensus.Accounts`).
- **prebuilt/client**: regenerado a partir do nosso cliente; a proxima versao do cliente leva os hooks novos.

Pendencias:

- Migrar as 81 leituras de campo de config escritas como texto no nosso combate para as views tipadas
  (`ConfigViewTests.FieldNamesOutsideViews` = 2714 do upstream + 81).
- Testar no jogo: Thief de adaga (Double Attack do upstream), Gravitation Field + Gravity Trap, Arm Cannon,
  Ember Zone (sem ticks dobrados), Ranger montado (Warg Strike/velocidade), 1-2 dungeons com chefes, troca de
  classe com barra de 7-9 slots.
- Testes com dados reais que falham por ambiente (tambem na main): EXP com `scripts/exp_bonus.json` = 10000 e
  testes do upstream escritos para o cliente de 20/10 (nosso export e de 18/10).
