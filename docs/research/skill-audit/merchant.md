# Findings: Merchant line (6100 Merchant / 6200 Blacksmith / 6300 Whitesmith / 6400 Mechanic)

JSON: `D:\ro3emu-server\src\Ro3Emu.Server\Reconstructed\skills\merchant.json` (17 entries: 10 kept hand-made, 1 rewritten, 6 new). Validated with `json.load`.

## Fixed in JSON
- 16006 Cart Revolution: kept (2 m around the target, 1+3 targets).
- 16010 Gunpowder Throw: **rewritten**. Its own text is a copy of Cart Revolution's ("Ram enemies with your cart ... Neutral ... «5» other enemies within «2» meters") but `_iEleType` is 4 (Fire). The talent text 10220300051 (buff 1640301, descData ['4','5','10','40','1.5']) matches the row: "dealing Fire melee Physical damage equal to PATK * «10»% + «40» to up to «5» enemies within «4» meters". Now 4 m around the target, 5 targets (was 2 m, 6).
- 16201 Cart Boost: `cleanse` with every BuffConfig group named "Slow" (11606, 12111, 12309, 12367, 12484, 14016, 15009, 16306, 23362, 23434, 49009, 50049, 111652, 115326, 215307): "removes [Slow] from you for «5» sec".
- 16205 Explosive Strike (High Speed Cart Ram talent proc, no text of its own): talent 10220300067 (buff 1646301 ['4','5','20','100','1.5']) "to up to «5» enemies within «4» meters": 4 m around the target, 5 targets (generic: 3 m, `_iTargetMax` 1).
- 16305 Heavy Axe Smash: kept (also covers modes 1 and 2, same text).
- 16306 Spinning Axe, 16314 Pile Bunker, 16315 Flame Launcher, 16317/16352 Energy Release, 16318 Aerial Bombardment: kept.
- 16308 Crescent Wheel: 7 m circle around the caster ("within a «7»-meter radius"; kRange 7.5).
- 16312 Arm Cannon: kept for mode 0. New `16312:1`, `16312:3` (8 shells, «160»%: P1 40% x 4 volleys, `_kShowDmg` [2]) and `16312:4` Melody Cannon - Enhance (12 rounds, «208»%: P1 52% x 4 volleys, `_kShowDmg` [3]) on attack points 0-3 of the 5-point templates 163121/1631212. Without them the mode rows fell back to the group entry's 2 points = half damage.
- 16350 High-Energy Pulse: 8 m circle around the caster ("within a «8» m radius"; kRange 7.5).

## Needs engine work

### A. Axe Tornado / High-Energy Impact mode rows miss the special code (highest impact)
`CastImpacts` compares `_kScriptLogic` with `==` against `"AxeTornado"` and `"AxeLeapAttack"`. Every talent/enhanced row has another script name, so it falls to the generic single hit at the first attack point:
- 16304 modes 1-5: `AxeTornadoPro`, `AxeTornadoS1BD1`, `AxeTornadoProS1BD1`, `AxeTornadoS1BD2`, `AxeTornadoProS1BD2`. E.g. mode 1 lv5 "total ... PATK * «2400»%" = P1 [19200, 48000] -> 10 ticks x 192% + 480%; generic deals 192% once. Templates 163041/163043/163045 have 4-5 attack points (`[0.13, 0.27, 3.5, 3.53]`, `[0.27, 0.35, 0.43, 3.5, 3.53]`), so the tick window (points[1]..points[^2]) still works once matched.
- 16307 modes 1-5: `AxeLeapAttack2`, `AxeLeapAttackS1BD1`, `AxeLeapAttack2S1BD1`, `AxeLeapAttackS1BD2`, `AxeLeapAttack2S1BD2` (templates 163071/163073/163075 are 2 s long with 2-3 points; mode 1 cd 1000 vs base 15000: probably the "tap again" follow-up row - check).
- Proposal: match with `StartsWith(SpinScript)` / `StartsWith(LeapSlamScript)`, or a mechanic field `"special": "spin" | "leapSlam"` usable on `group:mode` keys.
- Also from descriptions, not modelled: mode 5 "The final hit's radius increases to «11» meters" (proposal: `final.circle`); modes 3/5 "periodically pulling them toward the tornado's center" (proposal: `pullEveryTick` m toward the caster); "While Enhance is Active ... grants [Immunity]" and "+«10»% MSPD" (no buff rows in `_kBuff1`); 16307 "For every «1» enemies hit ..., gain stacks of Shield equal to «4»% of your Max HP, stacking up to «5» times" and "Enhances your next Axe Tornado, Launching [High-Energy Pulse] a number of times based on the number of enemies hit" (proposal: `perTargetHitSelfBuff {buff, maxStacks}` + `chargeNext {group, skill 16350, countFromHits}`).

### B. Talent procs are never triggered (16009, 16010, 16108, 16205, 16206)
Their rows are fine once cast, but nothing casts them. The talent is a passive buff (BuffConfig group) with `_kDescData`; no `_kLogicData`.
- 16009 Money Throw - buff group 16402: "[Cart Revolution] hits its primary target, triggering [Money Throw] «1» Extra times ... «3»-second Cooldown".
- 16010 Gunpowder Throw - 16403: "After using [Cart Revolution], immediately trigger «1» instances of [Gunpowder Throw] ... «1.5»-second Cooldown".
- 16108 Earth Spike - 16422: "[Hammer Fall] strikes «1» enemies within range with [Earth Spike] ... «100»% Chance to inflict [Stun] for «1» seconds ... «1.5»-second Cooldown".
- 16205 Explosive Strike - 16463: "[High Speed Cart Ram] triggers [Explosive Strike] an Extra «1» times when cast ... «1.5»-second Cooldown".
- 16206 Cart Clash - 16462: "[High Speed Cart Ram] performs «1» Extra [Cart Clash] attacks against targets below «50»% HP ... «1.5»-second Cooldown".
- Proposal: buffTriggers `on: "skillHit" | "skillCast"` with `fromGroups: [16006]`, `targetHpBelowBp`, `maxPerCast`, at the hit target (skillHit) or the cast's target (skillCast). E.g. `"group:16402": {on:"skillHit", fromGroups:[16006], skill:1600901, cooldownMs:3000, primaryOnly:true}`.

### C. Stack-threshold buffs: Vulcan Arm / Firepower Loaded (16313, 16312, 16315)
- 16313 `_kBuff1` = [[1631301, 10000, 15000], [1631701, 10000, -1]] (modes: 1631501). 1631301 "Firepower Ready: At «15» stacks, triggers [Firepower Loading]" (`_iMaxStack` 15). The generic path grants **1631701 Firepower Loaded on every cast**, which switches Arm Cannon to its enhanced mode permanently. "Every «15» casts of [Vulcan Arm] trigger [Firepower Loaded] «1» times."
- Firepower Loaded "Cannon Enhance lasts for «3» uses" (`_iMaxStack` 3, stack type 6): nothing consumes a stack per Arm Cannon cast.
- Proposal: `buffTriggers` `on: "stacks"` with `atStacks: 15`, `grant: buffId`, `consume: true`; and a skill field `consumeBuffStack: [buffGroup...]` (Arm Cannon consumes one 16317/16315 stack per cast).

### D. Conditional buff recipients
- 16105 Adrenaline Rush: "Increases the Attack Speed ... by «10»%. If the target is a Blacksmith class, grants an Extra «10»%". `_kBuff1` [1610505, 1610805] are both +10% ASPD and both go to every ally (non-Blacksmiths get 20%). Mode 1 ("regardless of Job Restriction") is correct. Proposal: `buffJobs: {"1610805": [6200, 6300, ...]}` (buff only for those job lines).
- 16107 Maximize Power: `_kBuff1` also grants 1611001 "+20 PATK (Weapon Polishing)" and 1611101 "+10 PATK (Weapon Enhance)", not in the description ("increasing your Physical DMG by «2»%"); probably conditional on other skills/talents. Unsure what gates them.

### E. On-hit status procs from a buff
- 16203 Melt Down: buff 1620310 `_kLogicData` [200, 10000, 1620510, 3000]: "attacking an Enemy Team player has a «2»% Chance to inflict [Weapon Damage] for «10» seconds ... attacking a Monster ... «10»% Chance to reduce its PATK and DEF by «5»% for «3» seconds" (1620510). buffTriggers can only cast skills. Proposal: buffTriggers `on: "damageDealt"` with `applyBuff: [buffId, chanceBp, ms]` and `vs: "monster" | "player"`.

### F. Cleanse / control
- 16317 Energy Release: "Immediately removes all Control effects from you ... You have [Immunity] during the countdown". No list of control groups exists (only Stun has category 13). Proposal: `cleanseCategories: [..]` once the control categories are identified, plus `selfImmunityMs`.
- 16201 Cart Boost: the "for «5» sec" Slow immunity is not modelled (one-shot cleanse only). Proposal: `immuneGroups {groups, ms}`. Better long-term: `cleanseCategories: [33]` (all "Slow" rows carry category 33).

### G. Knock-Up and other CC with no duration
- 16317 "deal ... PATK * «2200»% ... and inflict Knock-Up" (buff 910701 Knock-Up exists, but no duration is given; 16352's own text omits it). Proposal: a `knockUp` field (displacement-style CC with a default duration), or confirm a duration.

### H. Hit-count / cooldown side effects
- 16308 Crescent Wheel: "For every @{1} enemies hit, reduce Axe Tornado's Cooldown by «2» seconds and gain [Immunity] while casting your next unenhanced Axe Tornado". Proposal: `reduceCooldownPerHit {group: 16304, ms: 2000, per: n}`.
- 16312 mode 2 / 16313 modes 2-3 Melody: "[Melody Bubble] ... At «20» stacks, the Bubble explodes, Deals DMG «1» times ... PATK*«200»% ... it will explode ... when its duration ends"; Vulcan "Damage dealt to enemies carrying [Melody Bubble] is increased by an Extra «4»%". Needs a target debuff with hit-count stacks + expire/threshold release (buffTriggers `on: "stacks"`/`expire`) and a `bonusVsTargetBuff {group, percent}`.

## Looks correct as is
16000 Basic Attack, 16007 Mammonite (+mode 1), 16008 Crazy Uproar, 16009 Money Throw (row), 16099 Battle Axe Smash, 16104 Hammer Fall (DragCircle kRange 5 m = "«5»-meter radius", 30% Stun in `_kBuff2`), 16106 Power Thrust, 16108 Earth Spike (row: single target, 100% Stun 1 s), 16202 High Speed Cart Ram (the "«40»% more damage" under [Cart Boost] is mode 1 = base x1.4, selected by Cart Boost's 1620201 `_kSkillAttributeList` [0,16202,15,1,1]), 16204 Maximum Over Thrust (+mode 1), 16206 Cart Clash (row), 16310 Normal Punch, 16311 Knuckle Boost, 16313 Vulcan Arm damage (P1 = 2 x per-hit, `_kShowDmg` [2]), 16316 High-Speed Propulsion and 16399 Fast Travel (displacement rules). Skipped: 156000-156004 (EatChicken).

## Unsure
- 16312 mode 2 Melody Cannon: "Launches «6» Cannon shots ... Deals DMG equal to a total of PATK*«160»%" with P1 4000 (40%), `_kShowDmg` [3] and only 2 attack points (template 1631211) = 6 shots but 80%. Matching 160% would need 4 x 40% (e.g. points [0,0,1,1], 12 displayed shots) or a per-hit multiplier. Left on the group entry (80%).
- 16312:1/3/4 use attack points 0-3 of five; which four the shells really use is a guess (the fifth, 0.28 s, may be a recoil/end marker). Damage total is unaffected.
- 16010 Gunpowder Throw anchor: the talent says "to up to «5» enemies within «4» meters" without saying of what; kept on the locked target (Lock, `_iRangeType` 2). Could be around the caster.
- 16104 Hammer Fall: "hammer the ground within a «3»-meter radius ... to up to «5» enemies within a «5»-meter radius": the 3 m may be cast distance (`_iDistanceMax` 500 = 5 m) or the inner hammer area; left generic (5 m DragCircle).
- 16308/16350: description radius vs kRange (7 vs 7.5, 8 vs 7.5); followed the description, as the Knight file does.
