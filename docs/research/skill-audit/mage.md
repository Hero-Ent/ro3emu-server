# Findings — Mage line (Mage 2100, Wizard 2200, High Wizard 2300, Warlock 2400)

JSON: `D:\ro3emu-server\src\Ro3Emu.Server\Reconstructed\skills\mage.json` (12 entries). Audit prefix `2`.
Skipped: passives, EatChicken_* (153001-153011), 153000 Mage Normal Attack (generic is fine), rows with no description (12335, 12336, 12345, 12348, 12352: see Unsure).

## Fixed in JSON
- 12101 Sightrasher: `knockback 2` («2» m push; no displacement rule or move EffectData).
- 12102 Jupitel Thunder: `knockback 3` («3» m push; _kBuff2 1210101 is only the 1 s stun effect).
- 12302 Soul Expansion / 12328 copy: `hits.points [0,1]`: «2» bursts at «85»% each (P1 [8500, 8500], no _kShowDmg); before this the cast dealt one 85% hit.
- 12313 Chain Lightning: `anchor target, circle 5`: _kRange [0] made the splash radius 0, so only the locked target got hit; text says «4» others within «5» m.
- 12315 Comet / 12325 copy: `maxTargets 5` (text says up to «5»; _iTargetMax 10).
- 12334 Meteor (Comet extra): `circle 7` (text says «7» m; _kRange 800).
- 12342 Gravitational Impact / 12347 copy: `hits points [0] + final {point 6, branch 1}`: the «340»% Burst (P1[1], show 1) was never dealt; it now lands at the last attack point (1.48 s).
- 12423 Fireball Burst: `circle 3` (text «3» m; _kRange 400). Note: this talent proc is not triggered by the engine yet.
- 12482 Spatial Distortion: `circle 6` (text «6» m; _kRange 500). Same note as above.

## Needs engine work

### A. Ground-field number mismatches (CreateSkillObjectAsync, CombatService.Effects.cs ~L539)
- **12011 Safety Wall: wrong hit count.** The code reads `charges = logic[1]`, which is always 1. The text says 'can block up to «N» hits', and per-level `_kLogicData[3]` is 1..5 (lv1 [1,1,5000,**1**,...] … lv5 [1,1,5000,**5**,...]), matching desc index 4. Fix: `charges = (int)logic[3]` for Safety_Wall. Right now every level blocks 1 hit instead of up to 5.
- **12106 Meteor Storm: too many waves.** The field ticks every logic[1] for logic[2] ms. lv10 [1200019, 450, 4200, 550, 550, **7**] gives 10 ticks, but the text says 'strikes in «7» waves'. lv1 [1200014, 300, 1600, …, **2**] gives 6 ticks vs «2» waves. Fix: cap the ticks at `logic[5]` (wave count). Proposed field property `MaxTicks`.
- **12010 Fire Wall: no touch limit, no knockback.** The text says 'Each time an enemy touches a Fire Wall, it deals … and knocks the enemy back «2» meters … disappear early after being touched by enemies «5» times'. The code ticks every 200 ms (logic[1]) with no knockback and no limit, so a monster standing in the wall takes up to 25 hits in 5 s. Fix: give fields `Charges = 5` (desc idx 6), decremented per enemy hit, and a field knockback of 2 m (desc idx 4). Field ticks pass `field != null`, so SkillMechanics `knockback` is ignored for them. Proposed schema: `"field": {"knockback": m, "charges": n, "maxTicks": n, "maxActive": n}`, read by CreateSkillObjectAsync/TickSkillObjectAsync.
- **Instance limit not enforced:** 'Up to «1» Fire Walls / pillars / barriers can exist at once' (12010, 12105, 12011). CreateSkillObjectAsync adds a new object with no limit. Proposed: `field.maxActive` (remove the oldest of the same NpcId owned by the caster).
- **12104 Quagmire: slow lasts 0 ms.** _kBuff2 [[1210405, 10000, **0**]] is reapplied every 500 ms with a 0 ms duration, so the 'reduce ASPD «20»% / MSPD «20»%' slow probably never shows. Proposal: a 0 ms buff from a field means 'while inside'; refresh it with duration = interval + ~100 ms each tick. 'Units entering the mud pit have their Movement Speed and ASPD buffs removed' is also missing (needs a dispel by buff category).
- Checked and correct: 12105 Fire Pillar (1000 ms × 5 s = 5 ticks, 1 m, 1 target), 12107 Lord of Vermilion (350 ms × 2400 ms = 7 ticks = «7» strikes, 5 m, 5 targets), 12108 Storm Gust (1000 ms × 5000 ms = 5 ticks = «5» strikes; slow 1211101 3 s + 2% Frozen per tick).

### B. Periodic damage zones not in the field list (proposal: generic `"field": {"intervalMs", "durationMs", "radius"}` that creates a SkillObject without an NPC id, or with a hidden one)
- 12212 Gravitation Field (ZhongLiYuanYe): 'lasts «4» seconds and strikes a total of «8» times'; logic [500, 3900, 550] → 8 ticks. Today it is one 10%+200 hit. Hits can't be scheduled past the 1 s template with `hits.every`, because ImpactOffset clamps to the action end. Its _kBuff2 902401 Immobilize 0.5 s should go on every tick.
- 12443 Ember Zone (YuJinDiDai, talent proc): 'Creates an «4»-second [Ember Zone] that deals … every «0.5» seconds'; logic [500, 4000, 550] → 8 ticks. Today it is one hit.

### C. Reading Spellbook extra casts (Warlock)
- 12317 / 12319 Reading Spellbook, plus 12308/12310/12312/12314/12315/12316/12303/12342/12302: 'While in the [Reading] state, consumes «N» stacks of [Book Page] to cast this skill 1 Extra time.' The data already wires this up. Each skill's _kBuff1 holds a 'Spellbook - X' buff whose _kLogicData names the copy skill: 1231501 → 12325 Comet copy, 1231701 → 12323 Crimson Rock copy, 1232101 → 12326 Tetra Vortex copy, 1236301 → 12347 Gravitational Impact copy, 1231301 → 1231901 (Jack Frost/…). Proposal: when such a buff is applied and the caster has [Reading] 1231001 and enough Book Page stacks, consume the stacks and cast the copy skill as an independent follow-up (`followUp` with a condition: `"followUp": {"skill": id, "delayMs": ms, "requiresBuff": 1231001, "consumeStacks": {"buff": <BookPage>, "count": N}}`). The Book Page passive regen (12319, 'gain 1 Floor every @{1} s') is also missing.

### D. Conditional damage branch
- 12008 Soul Strike: 'Deals an Extra «50»% … to Undead enemies'. Branch 1 holds it (P1 24000 = 160×1.5, P2 330 = 220×1.5). Proposal: `"branchVs": {"race": <Undead race id>, "branch": 1}`, picking the damage branch from the target's race or element.
- 12201 Magic Crasher: P1 [6000, 8000] / P2 [10, 20]. Branch 1 probably applies under Mystical Amplification (buff 1246501 'Magic Cannon Energy Removal' links skill 1220310). Proposal: `"branchIfBuff": {"buff": id, "branch": 1}`.

### E. Stat source differs from _iDmgType
- 12201 Magic Crasher: 'Deals Neutral Ranged **Physical** damage equal to **MATK** * «60»%+«10»'. _iDmgType 1 makes the server use PATK. Proposal: `"stat": "matk"` (use MATK for the ATK part while keeping physical defence and result).

### F. Pull / per-hit element / chain
- 12316 Tetra Vortex / 12326 copy: 'Sequentially deals Water, Fire, Earth, and Wind … over @{1} hits … while applying [Pull]'. The hits need different elements, and a pull toward the centre is missing. Proposal: `"hits": {"elements": [5,4,3,2]}` per moment, plus `"pull": {"to": "point", "meters": n}`.
- 12342/12347 Gravitational Impact: 'continuously [pulling them in]' toward the locked target. Proposal: `"pull": {"to": "target"}`.
- 12313 Chain Lightning: the name and visual suggest a bounce chain. The JSON approximates it as a 5 m splash around the target, which matches the text, but a `"chain": {"jumps": 4, "radius": 5}` would match the visual.

### G. Talent procs never triggered
- 12403 Elemental Pursuit, 12404 Elemental Shock, 12423 Fireball Burst, 12432 Ice Spike, 12433 Thunderbolt, 12442 Fire Explosion, 12443 Ember Zone, 12452 Frost Explosion, 12482 Spatial Distortion: no server code references them. Their DragCircle rows also have no TargetPos when proc'd (they would centre on action.Origin = the caster). Proposal: a proc table (trigger skill/talent → proc row, chance) that casts with the hit target's position as TargetPos.

### H. Other
- 12213 Ganbantein: 'Removes up to «15» ground effects from a … «3»-meter radius'. No effect today. Proposal: remove SkillObjects within radius (hostile fields).
- 12304 Intense Telekinesis: 'Unleash Soul Buster' + Soul Expansion CD −«50»% + Astral Body gain: buff-side conditional effects (cooldown modifier) are not simulated.
- 12312 Crimson Rock / 12323 copy: '«60»% Chance to trigger Meteorite'. _kBuff2 is empty (logic [6000]). Meteorite looks like Burn 902701 (Tetra Vortex lists it as [[902701, 5000, 1500]] in the Meteorite slot). The duration is not in the text, so it is left out. Candidate: `targetBuffs [[902701, 6000, 1500]]`.

## Looks correct as is
12002 Sight, 12004 Fire Bolt, 12005 Lightning Bolt, 12006 Cold Bolt, 12012 Fire Ball (3 m default around target, 1+4), 12013 Thunderstorm, 12014 Frost Diver, 12015 Stone Curse, 12016 Mind Blast, 12099 Spell Smash, 12100 Earth Spike, 12103 Frost Nova, 12105 Fire Pillar, 12107 Lord of Vermilion, 12108 Storm Gust, 12109 Heaven's Drive, 12203 Mystical Amplification, 12210 Napalm Vulcan, 12303 White Imprison (kRange [0] → only the target, as described), 12305 Magic Frost, 12307 Ice Fog, 12308 Jack Frost, 12309 Petrify Curse, 12310 Earth Strain (15.5×7.5 rect from caster), 12311 Destructive Flame, 12314 Thunderclap, 12321/12322/12324 copies, 12350 Meteor, 12353/12354/12355 Blasts, 12403 Elemental Pursuit, 12404 Elemental Shock, 12433 Thunderbolt, 12442 Fire Explosion, 12452 Frost Explosion, 12999 Basic Attack, 153000.

## Unsure
- 12016 Mind Blast / 12210 Napalm Vulcan: 'the designated ST enemy … and up to «5» enemies within «N» m of it' with _iTargetMax 5. This could mean 1+5 = 6, but Fire Ball says 'other' explicitly and uses 1+4 = 5, so I kept 5.
- 12212 Gravitation Field: the text says a «6»-meter radius, but _kRange 500, logic 550 and the timeline indicator [5, 5] all say 5 m. Not changed.
- 12103 Frost Nova / 12101 Sightrasher: _kRange 550 vs «5» m in the text. This is the same +0.5 m tolerance the field logic uses (550), so I kept it.
- 12432 Ice Spike: attack points [0, 0.7], so damage lands at 0 s. The visual probably lands at 0.7 s; the text gives no timing, so not changed.
- 12316 Tetra Vortex: P1 [14000 ×4] but the text says 'totaling MATK * «140»% over @{1} hits'. Generic = one 140% roll (matches the total). If 140% were per hit it would be 560%. Left generic.
- 12342 Gravitational Impact: whether «170»% is per hit (×@{1}) or a total shown in 3 (_kShowDmg [3]). I used the total, consistent with the split convention.
- 12335 Meteor (Copy), 12336 White Imprison Trigger, 12345/12348 Psychokinetic Astral Body, 12352 Remix Resonance: no description. These are triggered sub-skills (White Imprison read, Astral Body orbs, Resonance). Not covered.
