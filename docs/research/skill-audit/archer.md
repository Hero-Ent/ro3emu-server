# Archer line audit (Archer 3100 / Hunter 3200 / Sniper 3300 / Ranger 3400)

JSON: `D:\ro3emu-server\src\Ro3Emu.Server\Reconstructed\skills\archer.json` (6 entries). Numbers in «» are filled description parameters (max level, mode 0 unless stated).

## Fixed in JSON
- 13003 Double Strafe: "«2» hits of PATK * «100»% + «75»" is per hit (P1 10000 / P2 75 = one hit; lv1 «55»/«66» = P1 5500 / P2 66) -> `hits.points [0,0]`, split 1 (was one 100%+75 roll shown as 2).
- 13102 Blitz Beat: "ST enemy ... and up to «2» Other enemies within «3» meters of the target" -> anchor target, circle 3 (_iTargetMax 3 already 1+2). **Has no effect yet: _iRangeType 1** (see engine gap A).
- 13110 Blitz Beat (auto-cast row, «5»%+«5»): same 3 m splash around the target. **Same rangeType-1 caveat.**
- 13204 Sharp Shooting: "the target and up to «3» enemies in a line within Attack Range" (18 m) -> anchor caster, rect [18, 1.5] (width from _kLogicData [3, 150], inferred), maxTargets 4. **Same rangeType-1 caveat.**
- 13320 Myriad Stars Chase the Moon: "«18»-meter by «4»-meter area, continuously dealing PATK * «1400»%": 7 x 200% at attack points 1-7 (lv1 7 x 160 = «1120»), rect [18, 4] (kRange says 18 x 6), anchor origin (caster may walk, "MSPD increases by «30»%").
- 13327 Warg Companion (Warg Strike): from 13322 "«4» Extra hits ... totaling Physical ATK*«320»%": P1 8000 per follower x 4 attack points = 320% (lv1 4 x 72 = «288») -> `hits.points [0,1,2,3]`.

## Needs engine work

### A. Mechanic shape ignored for `_iRangeType` != 2 (blocks 13102, 13110, 13204; would also fix 13305/113302/113303)
`SelectEnemies` returns `[lockedTarget]` when `_iRangeType != 2` **before** `SkillMechanics.For(row)` is read, so anchor/shape/maxTargets in archer.json for Blitz Beat and Sharp Shooting do nothing today.
Proposal: if a mechanic entry has `shape` (or `maxTargets`), skip the early return and use the area path; always include the locked target first, then fill up to `max` by distance from the anchor ("the target and up to N others").

### B. Hunter traps (13106 Anklesnare, 13107 Claymore Trap, 13108 Freezing Trap, 13109 Flasher) – checked against the trap objects
Description (all four): "Places a ... within «5»m. When an enemy enters the Trap's «1.5»m trigger radius, it explodes, dealing ... to up to «5» enemies within a «3»m radius ... The Trap lasts «20» sec."
- OK: placed at the dragged point (DragCircle, dist 500); trigger 1.5 m (hard-coded); blast radius 3 m (hard-coded); lifetime = last _kDescData = «20» s; single use; statuses come from _kBuff2: Anklesnare 902401 Immobilize «1» s, Freezing 902001 Frozen «1» s, Flasher 902501 Blind «2» s, Claymore none — all match.
- **Wrong: max targets.** _iTargetMax is 1 on all four rows, and the field path (`SelectEnemies(..., radiusOverride, field: true)`) uses `_iTargetMax` and never reads SkillMechanics, so each blast hits only 1 enemy instead of «5». Proposal: for `Trap` objects take the target cap from the description (_kDescData[3] = "5") or let `maxTargets` from SkillMechanics apply to fields (`field: true` currently forces `mechanic = null`).
- Minor: trigger check only looks for `MonsterUnit`s; event/PvP players walking in never trigger (the blast itself does include `EventAreaTargets`). The trigger radius/blast radius are hard-coded rather than read from _kDescData[1]/[2] (values agree today).

### C. Per-mode mechanics (affects 13004, 13102, 13110, 13320, 13323, 13327, 13305)
Mechanics are keyed by `_iSkillID`, but talent modes change the geometry/hit count: 13004 mode 1 "«4» enemies ... radius of «4» meters"; 13102/13110 mode 1 "«3» Other enemies within «4» meters"; 13320 mode 1 12 attack points, «2000»% = 10 x 200%; 13323 mode 1 "«5» hits ... totaling «310»%"; 13327 mode 1 5 attack points.
Proposal: allow keys `"<group>.<mode>"` (or a `"modes": {"1": {...overrides}}` object) that override the group entry for that _iMode.

### D. Line pierce (13305 Focused Arrow Strike, 113302 Enhanced FAS, 113303 Enhanced FAS (Unlimited Shooting), 155007 skipped)
"dealing PATK * «60»% ... to the target and up to «5» enemies in a line within Attack Range" (18 m). No width in data (_kLogicData empty, unlike Sharp Shooting's [3, 150]); left out of the JSON. After gap A: `anchor caster, shape rect [18, w], maxTargets 6`; w = 1.5 m if Sharp Shooting's width is accepted as the family value. 113303/13305 mode 4 have `_kDamageParam1 [x, 1500]` (a 15% branch 1) whose use is unknown.

### E. Procs / auto-casts (no server handler found)
- 13102/13110 Blitz Beat: "When using a Basic Attack, the Falcon has a «10»% Chance to cast [Blitz Beat] ... dealing «5»% of the original skill's DMG" -> 13110 is that row. Proposal `procOnBasicAttack: {skill: 13110 row, chanceBp: 1000}`.
- 13303 Fear Breeze: buff 1330105 "when a targeted bow skill hits, the target has a «30»% Chance to be struck ... «3» hits ... totaling Physical ATK*«50»%" -> 13304 (P1 5000, _kShowDmg [3], already correct as a cast). Proposal `procWhileBuff: {buff: 1330105, on: "bowSkillHit", skill: 13304 row, chanceBp: 3000}`.
- 13312 Warg Strike mode 0 (WugStrike_, P1 3000): "every «2.5» seconds, using Focused Arrow Strike casts this skill on the target, dealing PATK * «30»% ... [Slow] by «20»% for «3» seconds" -> `procOnSkill: {skills: [13305], skill: 13312 mode-0 row, cooldownMs: 2500}`. Its _kBuff2 1331803 lasts 2000 ms vs «3» s in the text (data discrepancy, left alone).
- 13322 King of the Wargs (buff 1332605, «6» s): while active, Warg Charge (13312) also casts 13327 (4 followers, «320»%), Warg Bite (13325) also casts 13328 (3 hits «240»%, P1 24000 / _kShowDmg [3] already correct), and Warg Hunt (13323) becomes mode 1 "Enhanced Warg Hunt". Proposal `followUp.ifBuff: buffId` (conditional follow-up) + `replaceWhileBuff: {buff, mode}`.

### F. Dispatch by distance + dash (13323 Warg Hunt -> 13329 Melee / 13330 Ranged)
"Commands «3» wolf followers ... «3» hits ... totaling Physical ATK*«150»%. If you are more than «5»m from the target, you also dash in front of it." _kLogicData [1332905, 1333005] names the melee row (13329, P1 15000, _kShowDmg [3]) and the ranged row (13330, same damage, attack points 0.13/0.53). Today 13323 deals one 150% number (_kShowDmg empty -> 1 segment). Proposal `dispatch: {nearSkill: logic[0], farSkill: logic[1], distance: desc[4]}` and for the far branch `dash: {stop: 1}` (loader already parses `dash.stop`, unused) moving the caster to 1 m in front of the target before the hit.

### G. Cooldown resets / movement-triggered casts
- 13325 Warg Bite: "resetting the Cooldown of [Warg Hunt]" -> `resetCooldown: [13323]`.
- 13326 Swift Warg: "Casting [Warg Charge] or [Warg Hunt] resets this skill's Cooldown ... your next movement instead casts this skill in the movement direction" -> `resetCooldown` on 13312/13323 + an "armed next move" state. The 10 m dash itself (WolvesDash, desc[0]) is already correct.
- 113311/113312 talent rows: "Every @{7} hits dealt by [Warg Strike] refreshes this skill's cooldown" (unfilled).

### H. Charges / mount conditions
- 13312 Warg Strike mode 1: "Commands the Demonic Wolf to Charge the designated ST target within «15» meters" (riding: caster charges too); damage/Stun 901601 1 s/heal-down 1333503/self 1333401 are in the row and correct, but no caster displacement. Proposal `dash: {stop: 1}` when mounted.
- Mount/falcon requirements ("Can only be cast while the Falcon is in Follow mode", "while mounted on the Demonic Wolf") are not checked.

### I. Stat scaling ("resonance")
- 13102/13110 "Every «20» AGI («10» for 13110) increases this DMG by «1»%" (_kDamageParamExtra [[1, 5, 2, 1]]); 13302 Arrow Storm and 13312 mode 1 "(Agility Resonance «0.1»%)" (_kDamageParamExtra [[1, 10, 2, 1, 100]]). Proposal: implement `_kDamageParamExtra` type 1 = +X% per point of stat (2 = AGI) in `Damage()`.

### J. Ally count
- 13201 Wind Walker: "yourself and up to «5» allies within a «10»m radius"; ally selection takes `_iTargetMax` (5) **including** the caster -> self + 4. Proposal: exclude the caster from the cap for ally-area buffs (or `maxTargets` applying to allies).

## Looks correct as is
13004 Arrow Shower (see Unsure), 13005 Charge Arrow (ArrowRepel push «6» m = desc[3]), 13006 Steady Focus, 13099 Arrow Smash (both modes), 13103 Detecting, 13202 Falcon Assault, 13203 True Sight, 13302 Arrow Storm (5 m DragCircle, 10 targets, «420»%), 13304 Fear Breeze proc row (3 hits totaling 50%), 13307 Unlimited Shooting (buffs), 13309 Keen Senses, 13312 Warg Strike damage/statuses, 13321 Warg Howl, 13322 King of the Wargs (buff only), 13325 Warg Bite damage, 13326 Swift Warg (10 m dash), 13328 Warg Companion (Warg Bite), 13329 Warg Hunt (Melee), 13330 Warg Hunt (Ranged) damage (first point 0.13 s; see Unsure). Skipped: 155000-155007 (EatChicken/event rows).

## Unsure
- 13004 Arrow Shower: description "radius of «3» meters" but kRange 350 (mode 1: «4» m vs 450). The +0.5 m is consistent across modes, possibly a deliberate hit-box margin; not overridden.
- 13330 Warg Hunt (Ranged): damage lands at attack point 0 (0.13 s); with a dash first the hit is probably at point 1 (0.53 s). No movement EffectData in the show to confirm.
- 113305 Myriad Stars Chase the Moon (BD4, no damage params, no description) and 13320 mode 1's branch 1 (15000): purpose unknown.
- 113311 Dance Move: Wolf Pack / 113312 Coordinated Hunt: descriptions unfilled (@{n}); P1 10000 show [5] / 5000 show [2] — cannot verify.
- 13303 Fear Breeze timeline has two attack points but the cast is a pure self-buff — fine.
