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
| 2026-11-03 | `077e59e9..ff0ad473` (152 commits, PRs #215-#366) | Grupos A, C, D, E, F, G | Grupo B (Dungeons/Raids/Maya DG - já corrigidas no Hero Saga) | ver "Sync 2026-11-03" |
| 2026-11-15 | `ff0ad473..8d21277f` (264 commits, 156 sem merges, PRs #367-#453) | tudo, com precedência das mecânicas e cenas do Hero Saga (ver abaixo) | nenhum revertido; cenas scriptadas (Aeronave, Formiga-Rainha, Maya) e o canhão client-side do upstream ficaram de fora por decisão do usuário | ver "Sync 2026-11-15" |

## Sync 2026-11-15

Faixa `ff0ad473..8d21277f` (264 commits, 156 de conteúdo, PRs #367 a #453): fórmulas de dano reescritas (DEF escala com o
nível do atacante, atributos de dano/cura/penetração/crítico, `_kDamageFormulaExtra` por nome, cooldown 205/206), efeitos de
cartas (dois lotes: Baphomet, GTB, Maya, Orc Lord, Phen, Eddga, Doppelganger, Osiris, família de monstros, elementos de
armadura...), reações de classe (Parry, Shield Reflect, Reflect Damage, Shrink, Death Bound, Devotion), imunidades e escudos
de monstros, talentos de skills travadas, áreas nomeadas pelo texto, loops de classe (Warg Strike, Guillotine, Firepower,
Joint Beat, Precision Thrust, King's Grace), skills de duas etapas (High-Energy Impact, Shadow Echo, Ultra Shield Bash),
campos no chão (Sanctuary, Magnus, Gravitation Field, Meteor Storm, Fire Wall, barreiras), comportamento de monstros (leash,
obstáculos, corpos), conteúdo dos forks (Spirit Tower, Hatchery, star-up, Growth Classroom, troféus, sinos do Louie,
recompensas de primeira conclusão, baú cooperativo, Fly Wing), buffs offline (`_iOffline 2`), pet (eclosão S, cura, pet do
mundo), movimento clássico por mouse (PR #419), ids de role acima dos NPCs, Arrow Storm, Deniro/Formiga-Rainha e canhões da
Aeronave (PR #406/#451), SDK 10.0.403 e Microsoft.Data.Sqlite 11.0.0.

Decisões do usuário:

- **Cenas scriptadas (1000201/1000301/1000401/1000501):** mantidas as do Hero Saga (reconstrução pelo vídeo oficial, cues,
  canhão server-side `DungeonService.Cannon`, `CombatService.Turret*`). Do upstream entraram só as partes genéricas: contador
  cheio ao completar um passo (`GoalFullWave`), `ContinueAfterMs`/`Guides` por passo (lidos do JSON, sem uso nas nossas cenas),
  o rider não sentado que caminha sozinho (#416, portado para o nosso `DungeonService.Ride`), a checagem da perna de um
  slide/ride rápido cruzando uma região estreita (`Reached`, portado para o nosso slide/ride). Ficaram de fora
  `DungeonService.Slide.cs`, `DungeonTests.Airship.cs`, a `Drive`/`RideAlone` do Deniro e o `SeatOnCannonAsync` só está
  declarado (ninguém chama).
- **Skills:** "nossa mecânica manda, upstream preenche o resto". Uma linha com entrada no `SkillMechanics` (ou Axe Tornado /
  High-Energy Impact) resolve pelo nosso `CastImpacts`/`ApplyEffects` (`CombatService.HeroMechanic`); sem entrada, valem os
  caminhos novos do upstream (`EnemiesByText`, `SkillAreaContains`, `LocksItsTarget`, `ChainRadius`, Venom Splasher, Energy
  Release, Cross Ripper). Entraram por cima, para todas as skills: fórmula de DEF por nível, `DamageFormula`/`DamageAttributes`
  (o nosso `StatScale` saiu), `HealRatio`, `DamageBranch` (depois do nosso branch de mecânica), `JobAllowsBuff`,
  `ComesFromALoop`, `PilesFor`, escudos de monstro (`AbsorbAsync`/`SheildDamage`), status garantido, `ResistsDisplacement`,
  `atOnce`/`WavesLeft`/`TouchesLeft` dos campos, follow-ups que gastam o status, `arrivedMs` no `CastYields`.
- **Onde o upstream era melhor, a nossa entrada saiu:** Auto Counter (bônus de dano quando aparou), Parry/Shield Reflect/
  Recoil/Reflect Damage (reações do `HitReactions`, com cooldowns do texto), Vulcan Arm/Arm Cannon (Firepower Loaded ->
  1632001 pelo `_kLogicData`; o `ClassLoops` gasta as pilhas), Sanctuary/Magnus/Gravitation Field (campos reais no chão).
  Ficaram as nossas: Energy Release, Venom Splasher, Rolling Cutter, Envenomed Blade (bounce), Rift Impact, High-Energy
  Impact, Chain Lightning, Shadow Guard/Loop, Blitz Beat, Warg Hunt, armadilhas etc. O Charge Up continua pelo nosso gatilho
  (`group:11604`), agora no nível que o buff nomeia.
- **Movimento clássico por mouse:** adotada a versão final do PR #419 (repara só quando não há `KeyBoardInfo`; um save real,
  inclusive com mouse desligado, é do jogador). O nosso `c6f3263e` foi substituído.
- **Falcão/lobo/Arrow Storm:** os shows do upstream (captura do servidor Genesis, rotas HIGH_LEVEL 31106/31108/31109)
  valem para Blitz Beat, Warg Strike e Arrow Storm; o nosso pendant genérico fica para as outras linhas (Warg Bite).
- **Dash:** andar durante um 31112 é descartado (`SkillMovedUntilMs`, também no nosso `DashAsync`); o nosso pin (ResetPos no
  fim, 0845ec85) ficou só para moves ao longo da face (Back Slide); um mergulho num ponto só corrige deriva.

Como os conflitos foram resolvidos (26 arquivos):

- `CombatService.Effects.cs` (15 blocos): `ResolveCastAsync` reescrito sem a duplicação de `MoveCasterAsync`/
  `CreateSkillObjectAsync` que vinha do sync anterior (um impacto posterior só aplica o seu hit; uma linha canalizada não
  aplica nada mais). `SelectEnemies` com as duas geometrias (mecânica nossa, senão `SkillAreaContains`). `SkillObject` sem
  `TicksLeft`/`LastHits` (viraram `WavesLeft`/`TouchesLeft`); `Knockback`/`MaxTargets` nossos mantidos.
- `CombatService.Casting.cs`: fila de casts nossa + `arrivedMs`; `ResetCooldownsAsync` chama o `ResetCooldownAsync` do
  upstream; `IsResurrection` cobre AcolyteBD4 no nosso `CastImpacts`.
- `CombatService.cs`: `Fighter.Stats`/`StatScale` removidos (teste `DamageParamExtraScalesTheRatioWithAspdAndLevel` saiu;
  `SkillCombatTests.DamageFormula` do upstream cobre).
- `EconomySnapshotReader.TypeManifest.cs`: hash `A3B9A5A255023364DEE38442BB6C94FF0BA8F18DABE3451296A4B3C1A66F47A5`
  (FirstPassPaid + offline_buffs do upstream junto com GmResets/gifts nossos).
- `ConfigViewTests.FieldNamesOutsideViews` = 2714 (2650 do upstream + 64 nossas).
- `GmAccessTests`: os dois testes (nosso group_id do site + o do upstream sobre maiúsculas do login).
- `PetFollowerService`: construtor com `IOptions<ServerOptions>` (upstream) e `WorldData?` (nosso).
- `World.cs`/`WorldData.cs`: `FarSight`/`SightBeyondView` nossos + `Far`/`SeenFromAfar` e `PatrolPaths` do upstream.
- Testes do upstream que modelam skills que ficaram com a nossa mecânica saíram: `EnergyReleaseGoesOffAfterItsCountdown`,
  os dois de High-Energy Impact em `SkillCombatTests.Stages`, a linha Envenomed Blade de `AnAreaNamedByTheTextReachesItsTargets`.
  Testes nossos que viraram do upstream: `ParryReleasesAutoCounterOnMeleeHitsWithItsCooldown` (coberto por
  `AClassBuffStrikesBackOnceWithinItsCooldown`); `WargStrikeOnFootSendsTheWolfToItsTarget` e `BlitzBeatSendsTheFalconToItsTarget`
  passaram a esperar as rotas HIGH_LEVEL.
- Build e testes rodados numa VM Linux (SDK 10.0.404, sem os analisadores por falta de memória: rodar
  `dotnet build Ro3Emu.slnx -c Release` no Windows antes de publicar): 3.343 testes do `Ro3Emu.Tests` com dados do cliente,
  todos verdes (as 4 falhas de tempo/ordem passaram isoladas). `QuestWalkTests` (Slow) não entrou. `Ro3Emu.Patcher.Tests`
  (referência a `GitHubReleases.cs`) e `PanelLinkTests` (`IsRo3EmuMode`) já não compilavam na `main` antes deste sync.

Pendências:

- Testar no jogo: Auto Counter (bônus ao aparar), Parry/Shield Reflect/Reflect Damage/Recoil pelas reações novas, Vulcan Arm ->
  Arm Cannon Enhance (1632001), Sanctuary/Magnus/Gravitation Field como campos, Rift Impact (sem pin), Back Slide (com pin),
  Swift Warg Lv2 após Warg Hunt, falcão do Blitz Beat e lobo do Warg Strike (uma vez só), movimento por mouse no Clássico.
- Cliente/launcher/patcher: nada mudou no upstream; não precisa de versão nova.

## Sync 2026-11-03

Faixa `077e59e9..ff0ad473` (152 commits de conteúdo, cobrindo PRs #215 a #366): refinamento de combate e animações,
novas fórmulas e mecânicas de arqueiro/flechas elementais, segurança de endpoints de operação (`OperatorToken`),
sistemas de facção (`FactionDay`/`FactionPoints`), suporte a montarias e veículos, correções no sistema de refino
(sync 5007), e modernização das coleções para C# 12.

Decisões do usuário e pontos preservados:

- **Grupo B (Dungeons, Raids, Maya DG / cenários reconstruídos):** Pulado / rejeitado intencionalmente a pedido do usuário ("B pode pular, se for da DG da maya, essa já corrigimos"). As correções e mecânicas customizadas das dungeons do Hero Saga foram mantidas intactas.
- **Identidade Visual e Launcher (Crítico):** Mantida 100% a identidade do Hero Saga (`HeroLauncher`), rotas de patcher e contagem de censo (`ActiveAccounts` + in-game).
- **Combate e Auto-ataque:** Incorporadas as melhorias do upstream (atraso de animação de auto-ataque, elemento de flecha para arqueiros, alvos nulos seguros no Falcon), preservando estritamente:
  1. Tolerância de 500 ms de auto-ataque (`AttackStopGraceMs = 500`).
  2. Movimento não cancelar cast do jogador.
  3. Empurrão da Defesa com Escudo (`Shield Defense` / `MechanicDisplacement`).
  4. Avanço da Investida Destemida (`CasterCharges`).
- **Barra de Atalhos:** Preservado o suporte e normalização aos 9 slots (`ShortcutSlotLimit = 9`, `BarSlots = 9`) do Hero Saga.
- **README da Raiz:** Mantido o README customizado do Hero Saga.

Como os conflitos foram resolvidos:

- **Segurança e OpsEndpoints:** Upstream introduziu `OperatorToken` com `.RequireOperator()` para rotas sensíveis (`/ro3emu/online`, `/ro3emu/gm`, `/ro3emu/reports`, `/ro3emu/shutdown`). Foram eliminados mappings duplicados em `ServerHost.cs` que causavam `AmbiguousMatchException` e os testes legados foram compatibilizados para autenticação de operador (`Operator.Client()`).
- **Economia S3 (`ExpectedFormatManifestHash`):** Compatibilizado `TaskRuntimeState` unindo `FactionDay` e `FactionPoints` do upstream com `GmResets` do Hero Saga. O hash do manifesto foi atualizado para `"C9BD18647272BD159C9DD658F6036A42D4FE681E8D7944A8129EB6EBDF99AA9C"`.
- **Registro no Jogo vs RefuseUnknownAccounts:** Ajustada a checagem em `SdkEndpoints.cs` para validar nomes contra caracteres ilegais e espaços (`AccountNames.Problem(identity) == null`) respeitando a flag `AllowInGameRegistration` e `RefuseUnknownAccounts`.
- **Testes Unitários:** 1.306 testes rápidos passando com 0 falhas (`tests/Ro3Emu.Tests -c Release --filter "Category!=Slow"`).


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
