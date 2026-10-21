# Findings — Swordsman / Knight / Lord Knight / Rune Knight (+ Novice 1000)

JSON: `D:\ro3emu-server\src\Ro3Emu.Server\Reconstructed\skills\knight.json` (21 entries, including 10 `group:mode` keys).
Talent values come from SpecialEffectConfig `_kDescData` (the `${n}` of the 10800100xxx talent texts).

## Fixed in JSON
- 11002 Magnum Break: circle 5 m around the caster («5»-meter radius; _kRange 550 = 5.5 m).
- 11105 Auto Counter: the cast deals no damage. Now followUp 1110610 (the counter row named by parry buff 1110510 _kLogicData) 1000 ms later ("When Parry ends, perform an Auto Counter ... «3»m * «5»m").
- 11106 Auto Counter (counter strike): rect 5 x 3 m from the caster (was _iRangeType 1 = locked target only; «5» enemies in a «3»m*«5»m area).
- 11211 Auto Counter (Parry counter, 30%+20): rect 5 x 3 m from the caster, same reason.
- 11108:1 Brandish Spear talent: target + «2» others within «4» m (circle 4 around target, max 3).
- 11109 Spear Stab: knockback 1 m ("[Knockback] for «1» meters"; no displacement rule existed).
- 11110:1 Spear Throw talent "one Extra Spear ... PATK*«50»%+«220»": branch 0 at point 0 + branch 1 at point 1 (generic dealt only branch 0).
- 11110:2 Spear Throw explosive talent: circle 4 around target, max 3.
- 11213:1 Spiral Pierce talent: circle 4 around target, max 3.
- 11221:1 Joint Beat talent (16130011 «4» m, «2» others): circle 4 around target, max 3.
- 11122 Piercing Blast (Pierce proc, 16120003): circle 4 around target, max 3.
- 11124 Heavy Blow (Basic Attack proc, 16120020): circle 4 around target, max 3 (see "exclude target" below).
- 11301 Rift Impact: damage at landing (attack point 1, 0.53 s) instead of take-off (point 0), circle 5 m around the requested point (_kRange 600 = 6 m).
- 11302 Cleaving Slash: circle 5 m (_kRange 600); selfBuff 1130201 "Dragon Wing" (MSPD +20%, attr 1131 +2000) 5000 ms — description "Increases your MSPD by «20»% for «5» seconds" while _kBuff1 is empty.
- 11310 Rain of Spears (modes 0/2): rect 10 x 5 m from the caster (was a 0 m splash around the locked target).
- 11310:1 / 11310:3 Hundred Spear - Enhance, 11310:4 / 11310:5 Ultimate Hundred Spear: rect 20 x 10 m from the caster.
- 11315:1 Precision Thrust talent: split 5 («5» hits; _kShowDmg [2]).
- 11316 Sonic Burst: rect 15 x 6 m from the caster (was a 0 m splash around the locked target).

## Needs engine work

### Level-aware follow-up (`followUp.sameLevel: true` or `followUp.group` = SkillConfig _iSkillID resolved at the caster's level)
- 11105 Auto Counter: the follow-up is a fixed row id (1110610 = lv10, P1 100% + 150). Lower levels should release their own 11106 level row. Proposal: `"followUp": { "group": 11106, "level": "caster", "delayMs": 1000 }`.

### Conditional damage bonus
- 11105 Auto Counter: "If you take melee Physical damage while parrying, Auto Counter damage increases by «50»%". Proposal: `"bonusIfHitDuring": { "buff": 1110510, "percent": 50 }` — counter damage x1.5 if the caster took melee damage while the buff was up.
- 11104:1 Bowling Bash talent 16120005: "When [Bowling Bash] hits only «1» enemy, it Deals ... an Extra PATK * «50»% + «400»" = branch 1 (P1 [18000, 5000], P2 [300, 400]). Proposal: `"extraIfSingleTarget": { "branch": 1, "point": 1 }`.

### Reactive procs (on damage taken / on cast of another skill)
- 11106 / Enhance Auto Counter buff 1110601 ("Taking melee Physical damage triggers Auto Counter"), 11210 Parry → 11211 (buff 1121010 _kLogicData [1121110], "When You take melee Physical damage, casts [Auto Counter] in front of You. Internal cooldown: «1» seconds"). Proposal: buff-level `onMeleeHitTaken: { skillRow, icdMs }` registry.
- 11311 Death Bound → 11381 (buff 1131105 _kLogicData [1138105]): "taking melee damage ... reflects the damage to nearby enemies, dealing ... «20»% of the damage taken to up to «5» enemies within a «5»-meter radius. Can trigger once every «0.2» sec". Needs damage-taken reflect: `onMeleeHitTaken` + `damageFromTaken: percent`.
- Talent procs on cast: 11121 Rapid Pierce (Pierce, «30»%, 16120002), 11122 Piercing Blast (Pierce, «30»%, cd «0.8» s), 11123 Power Slash (Basic Attack, icd «0.6» s), 11124 Heavy Blow (Basic Attack splash), 11125 Triple Slash (Basic Attack during Parry, cd «3» s), 11126 Greatsword Sweep (Basic Attack during Parry, cd «3» s), 11127 Double Counter (Bowling Bash «60»% extra cast). Their rows are correct; the triggering (SpecialEffect type 2 buffs 1112101/1112201/1112601/1112701/1123901/1124001/1112301) must be checked to exist in the engine.

### Exclude the locked target from a splash
- 11124 Heavy Blow: "dealing Physical ATK*«50»% ... to «2» other enemies within «4» meters of the target" — the Basic Attack already hit the target. Proposal `"excludeTarget": true` (then maxTargets 2).

### On-hit chance to cast a DoT skill
- 11304 Dragon Breath (Fire): "with a «50»% Chance to Burn damaged enemies, dealing Fire Ranged Physical damage totaling PATK * «120»% over @{1} hits" → row 11382 Burn (P1 12000, _kShowDmg [3]). _kBuff2 is empty. Proposal: `"onHitCast": { "skill": 1138201, "chance": 5000 }` (or a periodic-damage buff).

### Caster movement (dash / leap / charge)
- 10032 Warg Strike: "If you are more than «5» meters from the target, you also Charge directly in front of it". Proposal `"dash": { "toTarget": true, "minDistance": 5, "stop": 1 }`.
- 11112 Charge Attack: "Charge at a designated ST enemy within «12» meters" — caster should end in front of the target. Same `dash` proposal (the loader already parses `dash.stop`, but nothing reads it).
- 11301 Rift Impact: "Dives toward a designated location within «15» meters" — the caster should move to the requested point before the landing hit (like High-Energy Impact). Proposal: `"dash": { "toPoint": true }`.

### Knock-up
- 11316 Sonic Burst: "inflicts Knock-Up and [Immobilize] for «2.5» seconds" — Immobilize is in _kBuff2; Knock-Up (901801) has no stated duration, so it was not added. Proposal `"knockUp": ms` if a duration can be sourced.

### Per-enemy self shield
- 11301 Rift Impact: "For each enemy hit, You gain a [Shield] equal to «10»% of Max HP for «5» seconds, up to «30»%". _kBuff1 grants 1130505 (max 3 stacks, 10% Max HP) once per cast. Proposal `"selfBuffPerHit": { "buff": 1130505, "ms": 5000, "max": 3 }`.

### Resource / charge systems
- 11310 Rain of Spears: "Consumes @{1} charges of [Hundred Spears] ... Restores @{2} charges ... every «10» seconds"; 11315 Precision Thrust "Gain @{2} stack(s) of [Hundred Spear Pierce] charge for every @{1} HIT(s)"; 11316 Sonic Burst [Furious Storm] (buff 1132401) "Clears all current [Rain of Spears] charges and grants @{1} charges of [Hundred Spear - Enhance]" (mode switch 11310 → modes 1/3). Needs a charge counter per caster + mode switching.
- 10032 Warg Strike: "After casting this skill @{1} times, the next [Warg Strike] becomes [Empowered Warg Strike]" (mode 1 row 1003211, 80%).

### Sequential stacking debuff
- 11221 Joint Beat: "Each hit against the same enemy shatters the target's body parts in sequence ([Shattered Ankle] MSPD -«20»%, [Shattered Wrist] ASPD -«20»%, [Shattered Lumbar] PDEF -«5»%, [Shattered Neck] DMG Reduction -«5»%)". Buff 1122101 has _iMaxStack 4 / stack type 7; whether each stack enables the next effect depends on the buff engine.

### Mount requirement
- 11304/11305/11306: "Can only be cast while riding a dragon" — no mount check (not a damage problem).

## Looks correct as is
10000 Normal Attack, 10002 Smite, 10003 First Aid (friendly heal 150), 10010–10014 Basic Attack, 10050–10052 Basic Attack, 13000 Arrow Attack, 13400/13401 Warg Strike (wolf), 10032 Warg Strike (damage only), 11000 Basic Attack, 11001 Bash, 11004 Provoke (5 m, 10 targets, matches), 11005 Endure, 11099 Sword Smash, 11104 Bowling Bash (mode 0; DragCircle 5 m, 3 hits = P1 total with _kShowDmg [3]), 11108 Brandish Spear (mode 0), 11110 Spear Throw (mode 0), 11112 Charge Attack (damage + stun), 11120 Pierce, 11121 Rapid Pierce (row = talent «20»%+«40» x3), 11123 Power Slash («5»%+«25»), 11125 Triple Slash («50»%+«220» over 3), 11126 Greatsword Sweep (FixCircle 5 m, 3 targets = talent «5» m «3»), 11127 Double Counter (Bowling Bash copy), 11201 Aura Blade, 11202 Head Crush, 11207 Tension Relax, 11208 Concentration, 11210 Parry (buff), 11213 Spiral Pierce (mode 0), 11214 Berserk, 11221 Joint Beat (modes 0/2 damage), 11303 Wind-Pressure Blade, 11304/11305 Dragon Breath (Rect360 rule), 11306 Dragon Howling, 11308 Phantom Thrust (pull rule + buffs), 11309 Sonic Wave (Rect360 rule), 11311 Death Bound (buff), 11314 Rune Mastery, 11315 Precision Thrust (mode 0). Skipped: 94001–94003 test skills, 150001–159113 EatChicken/battle-royale rows, 152000–152004.

## Unsure
- Rect dims read as width x length («5»m*«10»m → 10 long, 5 wide), consistent with the crusader convention and with the rows' own _kRange/_kLogicData (always length > width with ~1 m margin). Dragon Breath kRange [1100, 500] vs description «4» x «10» m is the same ~1 m margin; left to the Rect360 rule as instructed.
- 11309 Sonic Wave «3» hits "equal to Physical ATK * «300»%" — ambiguous per hit or total; P1 30000 with _kShowDmg [3] treated as total (generic).
- 11105 follow-up direction: uses the caster's current facing (s.Face) at release time.
- 11302 MSPD buff 1130201 says "When [Slashing Strike] hits a target" — applied at cast start via selfBuffs (the description says it simply increases MSPD).
- 11381 Death Bound reflect row and 11382 Burn row have no description; only reachable through the procs above.
