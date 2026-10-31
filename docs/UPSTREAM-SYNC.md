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
| 2026-10-27 | `bbc9e3b2..7122af4f` (38 commits, PRs #85-#117) | tudo | nenhum revertido | ver "Sync 2026-10-27" |
| 2026-10-31 | `7122af4f..077e59e9` (176 commits, PRs #118-#214) | tudo com preservação dos pontos críticos | nenhum revertido; launcher e identidades mantidos | ver "Sync 2026-10-31" |

## Sync 2026-10-31

Faixa `7122af4f..077e59e9` (176 commits de conteúdo, cobrindo PRs #118 a #214): painel web e wiki integrados (`Ro3Emu.Panel`),
dungeons scriptadas oficiais completas (Culvert, Portão Norte, Pirâmides, etc.), combate e mira aprimorados (atraso de animação,
flechas de arqueiro, cooldowns), sistema de códigos de presente (Gift Codes), nível e dicas de cap (`LevelCapTip`), AI e surround de monstros,
máquinas de cerco e plataformas de guerra de território (Siege), banco de dados SQLite aprimorado com métricas e backup, e economia S3.

Decisões do usuário e pontos preservados:

- **Identidade Visual e Launcher (Crítico):** Mantida 100% a identidade do Hero Saga (`HeroSaga.png` / `logo-hero.png`, temas, interface personalizada, `ClusterName = "Hero Saga Ro3"`). Layout e arquivos invasivos do upstream em `Ro3Emu.Launcher` foram descartados.
- **Combate e Auto-ataque:** Incorporada a lógica do upstream com atraso de animação de auto-ataque e flechas elementais de arqueiro, preservando a nossa tolerância testada de 500 ms (`AttackStopGraceMs = 500`), a regra de movimento não cancelar cast, o empurrão da Defesa com Escudo (`Shield Defense` / `MechanicDisplacement`) e a carga da Investida Destemida (`CasterCharges`).
- **Barra de Atalhos:** Preservados os 9 slots (`ShortcutSlotLimit = 9`, `NormalizeShortcutBar`) do Hero Saga.
- **Site e Painel/Wiki:** Servidor inclui o backend e frontend do `Ro3Emu.Panel` do upstream com branding dinâmico do Hero Saga.
- **README da Raiz:** Mantido o README customizado do Hero Saga.
- **Hooks e DLLs do cliente:** Atualizados `Ro3Emu.ClientHooks.dll`, `Ro3Emu.Loc.dll` e `source.sha256`.

Como os conflitos foram resolvidos:

- **Dungeons Scriptadas:** Adotada a reconstrução oficial de dungeons do upstream, removendo rascunhos locais provisórios anteriores (`DungeonService.Cues.cs`, `DungeonService.Cannon.cs`, etc.).
- **Contas e Banimento:** Desduplicado `AccountPlans` e `AccountStore` mantendo planos do Hero Saga como `WebAccountBanRead`.
- **Testes Unitários:** 1.027 testes aprovados (0 falhas). Atualizados `ConfigViewTests` (2732 campos), `EconomySnapshotReader.ExpectedFormatManifestHash`, `AccountBanTests` (mensagens amigáveis em português) e `PanelTests`/`PanelCharacterTests` (branding Hero Saga).

## Sync 2026-10-27

Faixa `bbc9e3b2..7122af4f` (38 commits, PRs #85 a #117), todos os grupos aplicados: correcoes (Ladrao de Ovos, reviver,
Kafra, profissoes, roleta Dodo, pocoes automaticas, leilao), guilda (tarefas de eventos, destituir lider, fundir
guildas), Survivor Battle, presentes da Loja de Moda, empurroes e puxoes, dungeons de historia (Culvert, Portao Norte),
historico da Batalha Simulada, docs e dependencias. Sem mudanca no cliente.

Decisoes do usuario: empurroes e puxoes do upstream, o nosso so como reserva; auto-ataque do upstream com a nossa
tolerancia de 500 ms.

Como os conflitos foram resolvidos:

- **Auto-ataque ao clicar de longe** (`0ebcf773`): `AttackLoop` e `PlayerMoved` do upstream, com
  `AttackStopGraceMs` = 500 ms. O cast continua sem ser interrompido nem movido por andar; o `ApproachAttackTests` do
  upstream foi ajustado para isso.
- **Empurroes e puxoes** (`0840592c`): tabela `Displacements` do upstream. O nosso `SkillMechanics` knockback/pull
  ficou so como reserva (`MechanicDisplacement`, hoje so a Defesa com Escudo), com os efeitos emprestados 1631405 e
  1234212. Sairam das nossas JSON os duplicados: Golpe de Lanca, Sightrasher, Jupiter Trovejante e o puxao da Lua
  Crescente. A Investida Destemida continua movendo o personagem 12 m (`CasterCharges`) e empurra a partir de onde a
  investida comecou. Mantidos `ConfirmSkillMoveEnd`, o `DashAsync` e a busca do efeito de movimento em show*1000.
- **Ladrao de Ovos** (`ae6790ac`): o template das ondas foi para o `PlaceFightAsync` (`WaveTemplate`), que agora
  atende tambem as chegadas roteirizadas.
- **README**: o nosso. O `publicar.ps1` passou a citar o `scripts\start.cmd`.

Pendencias:

- `ConfigViewTests.FieldNamesOutsideViews` = 2691 do upstream + 70 nossas.
- Testes com dados reais que falham por ambiente: os de EXP (bonus do servidor), FirstHeart, Mapa, Dracula, Daily.
## Sync 2026-10-23

Faixa `9f3c04d8..bbc9e3b2` (29 commits, PRs #62 a #84). Decisoes do usuario: D1 furtividade do upstream
(`StealthEffects` + `CombatService.Stealth`), a nossa removida; D2 Nivel Astral, Ritual do Dragao e Titulos com os
drops padrao; D3 aplicado com o bonus de EXP: a EXP acima do nivel maximo alimenta o Nivel Astral (se encher rapido
demais, o bonus deixa de contar para ele).

Como os conflitos foram resolvidos:

- **Furtividade**: removidos o nosso `RevealRule`/`WireStealth`, o bit de status 2 (`BuffService.IsStealth`/`Hidden`)
  e o `MonsterService.Unseen`. Agora vale a regra do upstream: nenhum monstro mira um jogador escondido (o nosso
  deixava os chefes verem).
- **Barra de atalhos**: mantidos os 9 slots (`ShortcutSlotLimit = 9`, `NormalizeShortcutBar`); o `FullShortcutBar`
  do upstream usa o nosso limite. Os testes de barra do upstream foram ajustados para 9.
- **Cast**: a nossa fila de casts mais o alcance e o cooldown da linha pedida (fac8b3f6, issue #62).
- **prebuilt/client**: regenerado (o `TitleButton` e novo nos hooks); a proxima versao do cliente leva os hooks novos.

Pendencias:

- `ConfigViewTests.FieldNamesOutsideViews` = 2712 do upstream + 75 nossas.
- `TowerTests.ATeamBelowTheRecommendedPowerMeetsASuppressedFloor` falha com o nosso export de dados, tambem no
  `upstream/main` puro (por ambiente).
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
