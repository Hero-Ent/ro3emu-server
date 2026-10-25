# Ordinary dungeon implementation

Status: partial implementation; not a complete 1:1 encounter implementation.

Evidence: the referenced client Lua and protobufs,
`DungeonEntranceConfig`, `TenGameplayConfig`, `TenGameplayBossConfig`, `TenGameplayRefreshTimeConfig`,
`DungeonConfig`, scene editors, `NpcLevelPointRespawnConfig`, `NpcLevelRegionRespawnConfig`,
`NpcRespawnGroupConfig`, `MultiverseGroupConfig`, `AwardConfig`, and `DropPoolConfig`.
The `data/` and `re/` exports are read only. Season selection uses `SeasonService.PlayBook` and `Start`.

Implemented:

- 5062 `DBMultiDungeonInfo` before 5056, menu unlocks, one-based boss marks, personal progress and reward counters.
  The menu is client-configured; no invented menu request. Condition 3031 now uses elapsed server days from `Start`.
- 30400 strict scene/boss selection, configured player limits and eligibility; 42033 readiness and 30406 responses
  for a preformed team. Timeout, decline, changed roster, offline/ineligible members and changed season cancel entry.
  Scene 1022001's condition 1000000 now checks 1101 (`CheckRoleHaveTask`) against a running task 10102200,
  following the client condition evaluator; a delivered or absent task does not open it.
- Shared `InstanceService` for TowerService and DungeonService: persistent room identities, 70010/30005 transfers,
  saved outside positions, tracked non-respawning monsters and cleanup after the last participant leaves.
- 30401/30404 state/events, 30441 roster, 30106 surrender/reset, 30124 reset notification, 30112 used revivals,
  30403 result and 82584 personal award notification. Forged client clears and grants cannot settle a room.
- OnClickRevive option `[[0]]` permits an immediate normal 31013 request; option `[[2,5]]` uses a five-second
  auto-revive deadline in 30112 and refuses early requests. The client defines option 0 as `Immediately`, sends
  the normal-button request, and sends option 2's request when its countdown expires
  (`Lua_GameDefine.lua:8936-8952`; `LC_SceneCom_Revive.lua:597-709,748-758,1165-1182,1213-1224`). Of 65
  ordinary scenes, 33 have `[[0]]`, 22 have `[[2,5]]`, one has unrecognized `[[4]]`, and nine are NoRevive.
  These two recognized options prevent an immediate all-dead loss; the room's existing timeout still applies.
- Actual combat counters through 30110/30114/30116/30117; participant-owned persisted history and 30128 metadata.
- 82590 job-position preference and 82592 guide state; 30402 exit, disconnect and role-switch cleanup.
- Configured personal fixed items plus the existing drop-pool roll, preserving item binding and generated attributes.
  Currency, items, counters and history commit together for each role; a failed transaction does not consume rewards.
  All participants settle independently before result sends, so one failed connection cannot prevent another payout.
- Daily/weekly **reward allowances**, not hard entry limits: the client supports joining to help without a reward.
  Normal/Hard raw counters remain separate; the client may count Hard extras toward Normal eligibility.

Realm of the Gods extras (`DungeonService.RealmOfGods`, TenGameplayConfig 100101-100103):

- 82583 hall of fame: the realm's first team clear of each boss (`DBMultiDungeonHallOfFame`: leader, time, every
  participant's `RoleDetailInfo` at the clear), kept in `server_state` `multi_dungeon_server`. The client asks for it on
  every boss the group widget shows and says "No Team First Clear Yet" (UIWords 56287) without one. Later clears never
  replace it.
- Sacred Blessings: bosses with `_kBlessingID` unlock when the condition `_kBlessingBuffUnlock` ([3034, boss, days],
  "Unlocks on day N after the Current Server's First Clear") is met by that first clear. The level is the highest
  `TenGameplayBuffConfig` row of the group whose `_iUnlockDay` has passed since the unlock (inferred: counted from the
  unlock, not the clear). 5062 carries it in `sacredBlessings`; its buffs go on the run's members and `_iAddReviveNum`
  adds to the revive count. 82588 acknowledges the tip (isNeedTip until the next level).
- Final Trial honors (`_kFinalTrialID`): a won run finishes the goals whose `LC_ConditionList` holds: 7005 revives left
  (the team pool, inferred), 7004 seconds left before enrage (`Ro3Emu:RaidEnrageSeconds`, default 600, inferred),
  7006 another goal done. 82584 lists the goals the run finished, 5062 their states, 82589 pays `_iPersonAward` once per
  goal (event receipt `event/dungeon/honor-award`).
- 30125 Start Battle: the raid scenes 3020101-3020104/3020108 open in a preparation phase with dungeon var 1001 = [1]
  (DungeonLua `FunctionButtons`); the leader's button or `Ro3Emu:RaidPreparationSeconds` (default 120, inferred) starts
  the boss fight.
- 82591 Hidden Realm (50199): the entry draws a boss of the `_iRandom` 1 rows that every member has unlocked (inferred:
  uniform), sends `RandomDungeonNotify` with the countdown `FiveGamePlay_RandomDuration`, then runs that boss with the
  Hidden Realm as `MultiDungeonInitData.origin`. The clear counts as the Hidden Realm's (its awards, allowance and
  record, inferred).
- Test aids: `!dungeonsolo on` also admits a lone 30400 entry to a team boss; `!firstclear <boss> [daysAgo] | clear`
  sets the realm's first clear of a boss.

Policies below remain **(inferred)** except for explicitly identified client contracts; they do not establish
original-server encounter parity:

1. Boss placement uses a uniquely named `BOSS` scene-editor point (scene 3020101 point 93), otherwise the first
   ungrouped server point. Generic story waves group explicit server NPC
   points by ascending `GroupId`, with immediate advancement after every actor in the current group dies. A point
   respawn row can resolve a zero-ID editor point to a real NPC. A region respawn row supplies its real NPC group and
   count; spreading that group over authored inner points is inferred. Client-only actors and zero-template mechanics
   are not converted to enemies. Solo scenes without a resolved spawn source refuse entry.
2. Every remaining participant must finish scene loading before the clock starts. Loading expires after 60 seconds.
   The initiator is already ready; 30-second readiness comes from the configured public parameter.
3. Stage reporting uses one level and sequential stage numbers. The client confirms map states 0/1/2 as
   initializing/running/settling and the four stage/map timestamps as Unix milliseconds. Pending deadlines stay
   zero until all participants load. Variable 145 carries the actual participant list at startup (inferred).
   A preformed team is represented as squad 1.
4. Story-wave enemies immediately engage an available living participant through the existing AI/combat service.
   Multi-dungeon bosses retain their template AI without a forced target on entry. For Culvert's non-roaming
   scripted AI 25000001, waiting for a player hit/taunt is an (inferred) fallback: the original RaidFive server
   script is absent and no five-player preparation countdown/start-button contract was recovered.
   Killing all configured actors before the time limit wins; timeout, surrender or defeat loses.
5. For finite OnClickRevive counts, the boss's initial count is per participant. The existing map resurrection point,
   full HP and all-dead loss when no revival option remains are inferred. Scene 3120401 is explicitly NoRevive.
   The top-level automatic mode appears only in other gameplay types here; no script-only mode is given a generic
   self-revive bypass.
6. Only the leader may surrender or reset. Reset requires settlement, repeats admission/readiness and creates a new
   room. Continuations retain the original outside return point; they cannot combine unrelated private rooms.
7. Disconnect/exit detaches that member and cancels their pending ready queue. The remaining team may continue below
   the admission minimum. Reconnect returns outside without rejoining. The final departure destroys room actors.
8. Reward balances are keyed by group, boss-family tuple and difficulty. Refresh type 1 is daily; type 2 is weekly
   using `[weekday,hour]`, local server time (the shipped `[1,5]` means Monday 05:00). A new balance gets one configured
   increment, and later periods accumulate increments up to the configured cap. No allowance is spent on entry.
9. A first personal clear grants the same configured personal reward without spending a recurring balance. The
   independent first-pass state changes directly to Finish=2 after automatic settlement; no separate bonus is invented.
10. Normal spends its own extra balance first, then an unlocked same-type Hard balance, paying the selected Normal
    boss's reward. When Lua's same-type lookup has multiple Hard candidates, the lowest config id wins.
11. Team-boss world loot is suppressed because the personal award is paid with its allowance. Story results report
    actual inventory/currency deltas from existing NPC/task rewards; they do not synthesize a personal award. A won
    story scene (DungeonConfig _iGamePlayType 6) returns its participants 5 s after the result, as their own 30402 exit
    would: the client offers no exit countdown and _iExitAction is empty (inferred delay).
12. A changed `(PlayBook, Start)` resets seasonal progress, allowances and report history, retaining role job/guide
    preferences. An attempt from a previous season ID or playbook cannot grant its old-season reward, including a new
    cycle with the same playbook. A session from the old cycle must relog before admission.
13. Keep 20 reports per participating role, including participant stats, with area/server id 1. Private room ids are
    persistent monotonically allocated integers and also identify stored results. Unknown/foreign results stay hidden.
14. Recovered error texts determine refusals, but the exact original-server mapping to each check is unproven.

Reconstructed story encounters (`ReconstructedDungeons`, switch `Ro3Emu:ReconstructedDungeons`, on by default):

- [dungeon-inventory.md](dungeon-inventory.md) (generated from the client data) lists every eligible scene with its
  spawn points, labels, quest stages and base map: 28 story scenes, 7 tutorial, 4 minigames, 29 Territory War zones
  and 4 developer test maps. The story scenes' combat templates (2000xxxx) exist only on the original server.
- `src/Ro3Emu.Server/Reconstructed/dungeon-monsters.json` (embedded) rebuilds 10 of them as waves of client-known
  NPCConfig monsters at authored points, one wave per quest stage, each spawn with its reason (point label, quest
  text, base map) and each dungeon with a confidence: 8 high, 5 medium, 2 low. Bosses use the client's story
  templates (530xxxx: Golden Thief Bug, Baphomet, Pharaoh, Maya, the airship's Evil Warriors/Archers/Boss,
  Elusive Pecopeco); minions use the base map's field monsters. No elite stat scaling, phase or mechanic is invented.
- Story templates have no normal attack in the client (their server scripts chose skills); `monsterAttacks` names
  the monster whose normal attack each borrows (its field MVP, or a human warrior/archer for the airship crews).
  This also gives Payon Forest's story Eddga (5300001) an attack.
- Not reconstructed as monster waves (listed in the file): pure scripted scenes without a fight (ant nest
  exploration, Deniro ride, Louie's game, Moonlight Flower's hide-and-seek), the MVP party event, the Ring Test map and
  scenes without points. The first three are rebuilt as scripted scenes (below).
  Allied NPC companions and the Goblin Forest transfer are not reproduced (the airship's cannons and the Professor's
  rescue are scripted steps, below).
- The task widget: each reconstructed wave names the DungeonQuestConfig quest it plays. 30401 carries var 10001
  `PDungeonQuestIdList` (the scene's quests up to the current one; the widget shows the last one's title and goals)
  and var 10003 `PDungeonQuestGoalParamList` (the wave's kills and size, the goal's "(done/total)"). Seen live: the
  widget reads vars from 30401 only, not from 30404, so every wave and every kill republishes 30401. Without these
  the widget showed its prefab placeholder ("Quest Name / Defend the Base").
- Quest target 101018 [scene, n] "dungeon pass" now counts a committed successful clear (`DungeonService.Completed`
  to `TaskService.OnDungeonClearedAsync`); entering is not a pass. Story steps waiting on a reconstructed scene
  (Payon Forest, the airship, Desert Crossroad, the sphinx, the Baphomet maze, Southern Payon) can now complete.
  Steps waiting on a scene that is still not reconstructed stay blocked as before: Louie's game in the Pharaoh
  branch story (3120703) and Moonlight Flower (3120802).
- Live check with the real client: Culvert waves 1-3 spawn and render with names and models, the
  tracker follows the Culvert quests with its counter, the story Golden Thief Bug fights back with its borrowed
  attack, the elite Scorpion run ends in DungeonResSuccess, leaving returns to the field. The client shows no result
  window for these Plane scenes (as with Payon Forest).
- Solo tuning: the story bosses were built for the original's allied NPC
  companions (not reproduced); untuned, a level-matched player died in 3-36 s and needed 3-38 minutes to win.
  `soloTuning` scales each story template's HP and attack (basis points) so a Swordman at the boss level, every stat
  point spent STR:VIT 2:1, no equipment, normal attacks only, kills it in about 90 s and would fall in about 1.4 times
  that. `CombatService.Duel` computes both sides' expected damage per second from the same formula as a real hit
  (its roll-free part was extracted, rolls unchanged). The airship's nine boarders come in three groups of three, their
  attack at 20%. `DungeonTests.StoryBossTuning` keeps every boss and a boarder group inside the band. Live: the tuned
  Culvert Golden Thief Bug (4126 HP, hits of about 47) fell to a test character and the tracker showed (1/1).
- `DungeonTests.Reconstructed` resolves every reconstructed plan, clears each dungeon wave by wave to a win with the
  tracker vars checked, counts a clear toward its 101018 quest, and switches off.

Reconstructed scripted scenes (`Reconstructed/scripted-scenes.json`, same switch):

- A step may carry a fight: client-known monsters at scene-editor points (`fight`), spawned with the step; it then
  waits for every kill and, when it also lists events, for its event too. A fight-only step ends on its kills. A step
  with `all` needs every listed event, not any one, and its tracker counter follows them. A Journey scene (gameplay
  type 24) with a scripted reconstruction plays it instead of its bare point respawns. A step's `cannon` makes an
  actor's interaction a turret the player boards (the airship's cannons, below).
- A fight's monster may fly a scene-editor patrol path instead of standing at a point (`patrol`: kPatrolList, loaded as
  MapWorld.Patrols; it appears at the path's first point, MonsterService.Fly moves it one straight segment after the
  other at its NPC speed, and as MonsterUnit.ScriptDriven it is never engaged), come later than the step's start (`at`;
  the wave waits for it), carry `riders` (scene units at an offset turned with its facing, moving with it, gone when it
  falls) and show its end with `sink` effects (Lua_Config_EffectData, as 31119 where it fell). A scripted fight's
  monster with an NPCConfig `_kRespawnSkill` plays it as it appears (31100 from it: the show only) and holds still while
  it lasts. A step's `cues` run on its clock (`at`, repeated `every` while the step lasts): a ChatConfig line (1022; a
  Bubbling row over the actor `speaker`), an effect each participant sees from their own unit (31119 of each
  enum_DisplayEffectType the effect has; a camera shake moves only the camera of the unit it plays on), or a unit shown
  at a point or walking a patrol path (or a route of the scene's own, `path`) for a `life`, until its path ends
  (`vanish`) or with the room, born with its NPCConfig buffs, carrying `riders` and playing its `_kRespawnSkill`
  (`DungeonService.Cues`); also an actor's head bubble (`bubble`: the 32700 reply of its bubble interaction, unasked)
  or a cue unit's crew sent away (`dismiss`), a shown unit walking a route from where it stands (`walk`) or playing a
  skill's show (`cast`); a cue unit may burn whoever stands near it with its NPCConfig skill (`burn`) or burst when the
  fight reaches it (`fuse`, `blast`). A cue with `hpBelow` waits until the fight's monster `of` is down to that share of
  its HP. A step may also complete on its own clock (`after`, ms, paced like its cues) and bring `allies`, scene units
  that fight its monsters beside the participants (DungeonService.Allies). Their timing is inferred, scene by scene.
- Moonlight Flower's hide-and-seek (3120802) is played as a game, inferred as a whole from its ChatConfig lines (a step
  `seek`, DungeonService.HideAndSeek): her three opening bubbles over her head, then rounds of a warning
  (5310005), the 5 s countdown tip (5310010) and a look. A player within 3 m of a group 27 point (the darkness that
  rings the room) passes the round; anyone else is found (5310014) and the game starts over. A passed round searches
  the darkness within 8 m of the hiding place, so the second round needs another side of the room; it wins (5310007)
  and completes the step. The lines go out as 1022 with her uid, which the client's Bubbling rows need for the head
  bubble. `ScriptedScenePacePercent` shortens these timers for tests.
- Five story dungeons that were monster waves (or bare point respawns) now play their scripted stages around the
  fights, each with its evidence and guessed parts in the file:
  - Maya's nest (1000401): Maya, then 'Find the Professor' (within 10 m of the egg point), 'Free the Ant Egg' (5300056,
    interaction 5300021, message 5300022), 'Help the Professor Up' (5300026, message 5300023) and the chest (5300052,
    message 5300024), each interaction opened by buff group 53014.
  - The airship (1000501), rebuilt from the official video (Ragnarok 3 Genesis, 33:05-37:05; the scene loads at about
    33:19). Stage 1 runs on the scene's clock (`after`): 13 s of calm with the crew (5300031-5300035, their gated bubbles
    open) and the Captain's 5300540/5300541; then a shell bursts on the deck at mark 59 (blast 5300037, shake 5353201,
    SFX DECKEXPLOSION, its fire 5300057 burning on), the startled crew (5300061-5300065) take their places and their
    bubbles play on the clock (cue `bubble`: the 32700 reply of their bubble interactions 5300048-5300052, which the
    client plays as if the player had walked up), the Chief comes down from the prow (cue `walk`), the lines
    5300550-5300554 follow, and at 33:40 two pirate ships come up to port (5300039 with the Evil Boss and three pirates
    on deck, 5300053 further out), the Boss showing off with 5351901 (cue `cast`); 25 s later stage 2 begins. Stage 2,
    the cannons: 'Board the Cannon' (5300006) on one of the three Cannons 5300022 enters its GameplayStatusConfig 12, of
    type 10 UpperCannon (DungeonService.Cannon): 32702 with upperCannonInfo.cannonGuId, which
    LC_GamePlayStatusMgr.CommonCannon turns into the cannon's camera (3C 19), crosshair and buttons from the client's Lua
    GamePlayStatusExtraConfig, {27, 28}. Button 27 casts 5353001 'Fire - Rapid' through the ordinary skill request:
    CombatService admits it for the gunner (TurretSkill) without the row's height window and walkable line. Button 28
    sends 32703 QuitStatus. The cannon's camera (Camera 5300000) turns only 45-135 degrees, to starboard: two ships come
    up at once and a third later from astern (5300053 and two 5300015, pirates on deck), sailing along the deck's heading
    to lie parallel to it 30-50 m off (routes of the scene's own, `path`), shown from 150 m (World.WidenSightAsync). A
    shot is one ball at the ship aimed at, shown with template 61201 (the cannon's `show`; CombatService.TurretShot): the
    flash 6120105 (`_02_fire`), the arcing ball 6120102 (`_02_bullet01`, its fire tail) and the fireball blast 6120103
    (`_02_boom01`), with the template's camera shake 6120106 ('开炮') and sound, which the video shows rather than the flat
    `_03` ball of 61202 or the fire
    skill's own template 53530, which has no sounds. A hit shows about 9.9 million (`shown`, the video's numbers) while
    three hits sink a ship, which breaks up where it fell (5353102/5353103, 5353104/5353105 for the small one) and takes
    its crew with it; the enemy's fire lands on the deck every 12 s. Stage 3 is one fight of nine (0/9): the ships
    collide, the pirates leave their deck (cue `dismiss`), five warriors drop in with their FallDown at the Boss's
    'Charge!' and four archers 13 s later, each opening with its once-only skill as its FallDown ends (the warriors' dash
    5350301-5350601 along their spawn facing, the archers' volley 5351301-5351601). A monster skill whose show has an
    m_kPoint.m_kWarningRange not scheduled in its m_kShows (5352807, 53503023, 53513019) is telegraphed as 31119
    EffectType 6 (enum_DisplayEffectType.WarningRange) and hits when the telegraph ends; a lock row with a placeholder
    _kRange takes the telegraph's shape and size. The Evil Boss's '击飞' effects (53523015, 53526015) knock the players
    hit back. While the ships sail, their archers loose volleys at the deck (5351801, cue `cast`).
    Wule, Boya and the Eden Group Chief (5300086-5300088) fight beside the player
    through the boarding and the Boss (`allies`, DungeonService.Allies). The Evil Boss enters at mark 66; at 29 % of its HP
    (cue `hpBelow`) a blast shakes the ship, its fire burns whoever stands within 4.5 m every second (cue `burn`: the
    fire's 5365201, cast as the Boss) and two rows of barrels appear, five rolling along patrols 69-72 and 98 and five
    across the deck, each bursting (5352903) when the fight reaches it (cue `fuse`). Stage 5 ends at the prow or after
    15 s while the crew walk back to their posts; stage 6 waits on the injured captain's dialogue (530003802, whose
    option triggers interaction 5300039 and message 5300039); then the tracker goes back to stage 5 while four shells
    burst around the Captain ('reinforcements?'), and the closing cutscene (PlotInterpretation 130161, its QTE, then
    130162 or 130163) ends the scene. **Inferred**: where the ships lie and the pirates stand on a deck, the allies' hits
    (1 % of a monster's HP, 3 % for a special; the monsters never turn on them), the fire's and the barrels' radii, that
    the bombardment and the barrels hurt nobody, three hits sinking a ship at any level, a shot aimed at nothing going
    to the ship nearest the cannon, and the end of the stage, the settlement, a death or leaving the room putting the
    gunner back to Idle. The rope stakes (5300040) and the Sharpshooter (5300054) are not shown.
  - Payon Forest (1022001): 'Cover Boya and Wule' (reach the standoff point 22 between them and Eddga), then Eddga.
    Boya and Wule (5300086/5300087) are shown, not fighting.
  - The Peco Peco nest (1051001): the Elusive Pecopeco with the Ancient Nest and Bragis shown.
  - Pharaoh (main story, 3120702): Pharaoh with the Relic he leaps out of, his coffin 5300013 at point 24, and the
    Pyramid 5300011 beside him (6 m east, 7 m north of point 20: the scene has no point for it). Its use is **inferred**
    from the raid Pharaoh's tip 4370002 'Pharaoh is unleashing Cleansing Light! Quickly, hide behind the Pyramid!': every
    20 s of the fight the tip goes out, Pharaoh holds still for 4 s, then his strongest skill 5340301 (whose effect the
    client draws as a cross of light) hits everyone outside the Pyramid's shadow (a step `shelter`, DungeonService.Shelter).


- The last three main-story blockers of the story walk: the ant nest vortex and egg passage (1000201, task 10104290),
  Bobo and the Deniro (1000301, task 10104300) and Louie's game in the Culvert (3120402, task 10106270). The original
  scene script is not in the client; each scene is rebuilt as steps, one per stage, every decision with its evidence
  and the scene's guessed parts listed in the file (confidence: medium for all three).
- A step names the DungeonQuestConfig quest the tracker shows (var 10001) and its goal counter (var 10003), the scene
  actors the server shows (client NPCConfig ids at scene-editor points, entity type 1 or 2 from `_iNPCType`, with the
  buffs that open their interactions), an optional story, the client events that complete it and an optional teleport
  after it. The engine is generic (`DungeonService.Scripted`): nothing knows a scene, task or NPC id.
- Client events: 32700 on a scene actor (its own UID namespace) with one of the actor's `_iInteractID`, also counted as
  the dungeon message of that interaction's server-hosted action 210 `SendDungeonMessage` (the client never runs it);
  a walk into a server region (regionType 2; no client consumer registers them, so the client never reports them;
  `LC_AreaHelper.CheckInArea` geometry) or near a point; 84011 at a story's end. The 32700 request is always echoed:
  the client runs the interaction's client-hosted actions (e.g. 53 opens a dialogue) from the reply.
- Interaction gates: an interaction's `_iNPCCondition` 1004 CheckHaveBuff asks for a buff group on the actor; the step
  that waits on it gives its actor that buff (in the entity's AoIBuffInfo, and 31104/31105 + 31048 when it changes on
  a kept actor). In 1000301 the five interactions are gated by five different buffs, so exactly one is open per step.
- Stories: dungeon var 100020016 (`DungeonVariableFunctionConfig`, start function PlayCutSceneStory) with [plot] plays
  a PlotInterpretation run-time story; at its end (also when skipped) action 601 sends 84011 and action 150 sends 84012
  (PlotActingTrigger); either completes the step, neither is answered (fire-and-forget in the client). The var is sent
  once in a 30404 var-change event and
  never kept in 30401 (the client calls the function on every push). Louie's game plays stories 1013 and 1014 (plots
  140010 and 140020), which no task triggers and which show this Culvert, Louie's gang and dialogues 1010627001-004.
- The next step (or the settlement) after a completing event runs in its own owner scope (`GameLoop.PostLegacy`): the
  route of the message that completed it does not reserve the settlement's role locks. Scene actors are room props
  (`InstanceService.AddPropAsync`), removed at settlement and before the room's final cleanup.
- Guessed (see each scene's "guessed" list): the corridor region that ends 1000201's first stage, Bobo's place there,
  the jump and the ride as teleports, no exploding ant eggs, no seamless 1000201 to 1000301 transfer, which bell hides
  Katie (from both stories), a wrong guess changing nothing, the instrument retrieved within 4 m of mark point 37.
- `DungeonTests.Scripted` resolves each scene against the client data, plays each one through its client events (wrong
  and premature events included) to a clear, and checks that the story step is delivered and the next one offered.
  The story walk plays the three scenes with the same events and has no known-open blocker left.
- Offering an interaction is the client's own (LC_InteractManager, every 0.2 s from the player's position, so a teleport
  is enough): the unit registers when NPCConfig _iInteractID[1] is set, is in range within its _iInteractDistance (3D;
  Louie's bell rings 2 m, Bobo 2 m, the Deniro 4 m, the fluorescent substance and the vortex 5 m), and of the units in
  range the highest _iInteractRank, then the nearest, is possessed; mode 2 (the rings, the vortex) acts by itself on
  entering range, mode 3 shows a skill button. DungeonTests.ScriptedClient replays the wire data the client received
  (31037, 31104/31105) against these rules for every step. GM test aid: !actors lists the step and its actors, !actors
  <npcId> puts the player on one.

Not implemented or unqualified:

- Of 65 eligible ordinary dungeon config rows (64 have an editor file, representing 60 distinct exports), 1022001 joins editor point 20
  to NPC 5300001 through `NpcLevelPointRespawnConfig`; 4400001 joins region 52 to NPC 4400001 and a configured
  count of 50 through the region and group respawn tables. The remaining 63 rows have no resolved combat spawn source
  in the exported tables. `DungeonStageConfig` is empty. The available `DungeonLua_1000201` stage/skill modules are
  empty; `monsterType=1` ant eggs are client actors. Those unresolved entries return DungeonDataMissing unless a
  reconstructed encounter (above) covers the scene. See
  [dungeon-inventory.md](dungeon-inventory.md) for the per-scene inventory.
- Exported team-boss identity and scene are used, but boss phases, scripted waves/delays, mechanics and task scores
  are not recovered from UI phase descriptions. The team test qualifies a basic boss combat loop, not its encounter.
- Matchmaking, tactical-team changes, auction/party rewards, honor and an independently mapped first-pass bonus.
  82587 refuses with RewardMissing; `_iPartAward` and preview rewards do not invent an auction payout.
- 30123 belongs to Dragon Island and 30132 to Dragon Island/LeisureForty client paths, so ordinary dungeons do not
  emit those mode-specific pushes. The 30126 alias/scoring formula is unverified; no fabricated scoring push is sent.
- Full client UI behavior, especially empty auction/first-pass repeated fields in 82584. This has not been checked
  in the original client; protobuf/wire tests do not prove screen rendering.

Tests (`DungeonTests`, `TowerTests`, `TowerShopTests`):

- The revive case moves the player with the authoritative in-map teleport before its wire kill; the server rejects
  a 33 m walk immediately after a revive as a speed violation.
- The real-config team case uses a leader plus two bots (50101 requires at least three), completes readiness and
  scene loading, sets the configured boss to 1 HP, then kills it with a client combat request. It verifies one payout
  per role and scoped statistics/exit; this is not full-health encounter qualification.
- Other cases cover ready cancellation, live season changes, forged requests/results, NoRevive, disconnect during
  loading, reset, reward exhaustion, daily accrual/cap and transaction rollback. Weekly refresh boundaries are checked.
- The 1022001 test proves refusal before its running story task, accepts task 10102200 through the persisted task
  service, enters the real point-respawn scene, kills its NPC through the wire and receives a result. The 4400001
  test spawns its source-backed 50-actor region plan. Separate two-wave execution uses a synthetic host-local
  editor fixture. The exported data is never modified; these tests do not claim complete solo script parity.
