# Findings: Thief line (Thief 5100 / Assassin 5200 / Assassin Cross 5300 / Guillotine Cross 5400)

JSON: `D:\ro3emu-server\src\Ro3Emu.Server\Reconstructed\skills\thief.json` (10 entries + one mode override).
Audit: `audit.py 5` (groups 15000-15341, 162021-162023; the EatChicken_* event rows 151xxx/158xxx/159xxx are skipped).

## Fixed in JSON

- 15303 Rolling Cutter: _iRangeType 1 limited it to the locked target; desc «3» enemies within a «5»-meter radius of You → caster, circle 5, maxTargets 3.
- 15303:1 Rolling Cutter (mode 1, row 11530320): «6»-meter radius → caster, circle 6, maxTargets 3.
- 15306 Evil Spirit Threat: _iRangeType 1; «5» enemies within a «5»-meter radius → caster, circle 5, maxTargets 5.
- 15321 Flowing Shadow: row is a 1 m FixCircle (_kRange [100]); desc «3»-meter-wide, «9»-meter-long strip in the dash direction → origin, rect [9, 3]; Endure 903101 for _iActionTime 500 ms.
- 15326 Piercing Shadow: «300»% total = P1 [15000, 15000] at points 0 (8 slashes) + 1 (finisher, branch 1); generic dealt 150%. Covers mode 1 too.
- 15327 Enhance Cloaking (disc ring proc): _iRangeType 1; «3» enemies within a «10»-meter radius → caster, circle 10, maxTargets 3.
- 15320 Shadow Loop / 15322 Thousand Shadows / 15323 Shadow Guard: "[Endure] while casting this skill" → selfBuffs Endure 903101 for _iActionTime (700 / 500 / 500 ms). Damage and areas were already right.
- 15301 Dark Illusion: `dash: {stop: 1.5}` ("Charges to a point «1.5» meters in front of the target"). The loader parses `dash` (DashStop) but nothing in CombatService reads it, so this does nothing today (see "Charges/dashes").

## Needs engine work

### Delayed detonation from a debuff (buff expiry → skill on the holder)
- 15110 Venom Splasher: "Plants timed Dynamite on a designated ST enemy ... After «2» seconds, deals Physical ATK*«110»%+«160» ... to the designated enemy and up to «4» other enemies within a «3»-meter radius, and inflicts [Poisoned]". _kBuff2 [[1511910, 10000, 2000]]; buff 1511910 says "When the effect ends, deals ..." and its _kLogicData [1511310] is SkillConfig row 1511310 = group 15113 (VenomSplasher2: DragCircle _kRange [300], _iTargetMax 4, logic [1511610] poison). Today 15110 hits the target alone immediately and 15113 never fires.
  - Proposal: a generic rule "a hostile buff whose description starts 'When the effect ends' casts its _kLogicData[0] skill row at the holder on expiry", or a schema field `"onBuffExpire": {"buff": 1511910, "skill": 1511310}` on 15110 with `"hits": {"none": true}`. The detonation (15113) then needs `anchor: "target"`, circle 3, maxTargets 5 (1 + «4»), so `followUp` must also be able to carry the original TargetId (`followUp.keepTarget: true`); today followUp casts at the caster's position without a target.
  - Note: buff 1511910's own text says MATK «150»%, «5» m, «6» enemies (_kDescData ['5', '6', '150']), disagreeing with the skill text; follow the skill row.

### Poison DoT from _kLogicData (skill-row ticks)
- 15005 Envenom ("applies [Poisoned] damage", logic [1511610]), 15106 Virus Spread ("inflicts [Poisoned] damage on the target", logic [1511610]), 15113 Venom Splasher detonation (logic [1511610]). 1511610 is the SkillConfig row of 15116 "Poisoned" (lv10: "Physical ATK*«20»%" Poison). None of the rows has a poison _kBuff2, so no poison is applied today. The generic status 903001 "Poisoned: Every @{1} sec, loses HP equal to @{2}% of the applier's Poison Physical Damage" exists but the rows give no duration/tick.
  - Proposal: `"dot": {"skill": 1511610, "buff": 903001, "intervalMs": ?, "durationMs": ?}`: periodically cast the tick row against the target while the status lasts. Duration/interval are not in any thief description; take them from 903001 _kData1 [500] or the server original if found. Not added to JSON (no numbers in evidence).
- 15111 Envenom (EnchantPoison buff 1512010, logic [3000, 1511610]: «30»% chance on Basic Attack) and 15211 Deadly Poison (buff 1521110, logic [5000, 1523010]: «50»% chance to cast 15230 Venom Sting): on-hit proc buffs → need an "on basic attack, chance bp, cast skill row" buff rule (`buffLogic: procSkill`).

### Bounce / chain
- 15105 Envenomed Blade: "Throws a poisoned blade ... PATK * «60»%+«40» to the designated ST enemy ... Then Launches a poisoned blade at a random enemy within «3» meters of the target, dealing PATK * «60»%+«40», up to «3» times." Row: _iTargetMax 10, logic [4]. Today: one hit on the locked target.
  - Proposal: `"bounce": {"radius": 3, "count": 3, "random": true, "repeatTarget": false, "delayMs": n}`: after the main hit, pick a random enemy within radius of the last one and deal the same branch again.

### Conditional multipliers (choose a branch by condition)
- 15102 Sonic Blow: "If the target's HP is at or below «50»%, increases the percentage damage multiplier ... by «20»%": P1 [8500, 10200, 10625] (10200 = 85% x 1.2), logic [2000, 101, 5000] (attr 2000 HP vs 101 MaxHP, 5000 bp). Proposal: `"branchIf": {"targetHpBelowBp": 5000, "branch": 1}`. (Branch 2 10625 = 85% x 1.25 is presumably a talent value.)
- 15004 Steal ("Ambush"): "If cast while [Hiding], deals «140»% Extra damage and [Slows] the target by «20»% for «2» sec": P1 [7000, 14000]. Proposal: `"branchIf": {"casterBuffGroup": 15007, "branch": 1, "addBranch0": true, "targetBuffs": [[slow, 10000, 2000]]}` (extra damage = branch 0 + branch 1? the text says "Extra", so 70% + 140%). Hiding (1500710/1500610) is _iBuffID 15007/15006; the slow buff id is not named, needs a lookup.
- 15302 Cross Ripper Slasher: P1 [4000, 4400] = 40% and 40% x 1.1, matching Dark Illusion's "increases each hit's damage by «10»%" (mode 1 [5000, 5500] is also +10%, although Dark Illusion mode 1 says «20»%): branch 1 should be used while the Dark Illusion buff (1530101 "Black Phantom Damage Increase") is on the caster. Proposal: `"branchIf": {"casterBuff": 1530101, "branch": 1, "consume": true}`.

### Stack resource (blades)
- 15302 Cross Ripper Slasher: "dealing blade count (minimum @{1}) * PATK * «40»% * («1» + ASPD * «0.12»%) ... At «4» blades, resets this skill's cooldown." 15303 Rolling Cutter "adds «1» blades ..., up to «4»"; 15301 Dark Illusion "Immediately gains «4» Blade stacks, multiplies the blade count of the next Cross Ripper Slasher by «3»" (buff1 1530101, _iMaxStack 2, -1 ms). Today Cross Ripper deals 40% once.
  - Proposal: `"stacks": {"buff": <blade buff>, "gain": 1, "max": 4}` on Rolling Cutter/Dark Illusion and `"hitsPerStack": {"buff": <blade buff>, "min": 1, "multiplierBuff": 1530101, "factor": 3, "consume": true, "resetCooldownAt": 4}` on Cross Ripper. The blade-count buff id is not obvious in BuffConfig (1530101 is the damage-increase/x3 marker); needs a search of 1530x/1540x buffs.

### ASPD scaling (_kDamageParamExtra)
- "(«1»+ASPD*«0.5»%)" on 15303, 15320, 15322, 15323, 15324, 15326 (extra [[50, 115, 10000, 500]]), "«0.12»%" on 15302 ([[12, 115, 10000, 500]]), Steal "Every «1»% Attack Speed increases this damage by «0.2»%" ([[1, 10, 114, 1]]). The skill damage path ignores _kDamageParamExtra (only buffs/pets/revive read it). Proposal: in Damage(), multiply by (1 + stat[extra[1]] * extra[0] / 10000) for tuples whose [2] is 10000 (format to confirm against the client's damage Lua).

### Mixed PATK + MATK
- 15210 Soul Destroyer "Physical ATK*«50»%+Magic ATK*«50»%+«60»" (P1 [5000], P2 [60], logic [1531002, 1531003]) and 15240 Advanced Soul Destroyer "Physical ATK*«85»%+Magic ATK*«85»%" (P1 [8500], P2 [115]). Today only PATK x P1. Proposal: `"matkPercent": 50` / `85` (adds MATK x n% to the same roll), or read logic [1531002, 1531003] if those are formula ids.

### Charges / dashes (caster moves to the target)
- 15301 Dark Illusion: "Charges to a point «1.5» meters in front of the target ST enemy within «12» meters" (JSON has `dash.stop` 1.5, unused).
- 15204 Shadow Step (script DarkIllusion): "Lock onto an enemy unit within «9» meters, quickly dash «7» meters toward the target, and slow up to «6» enemies within a «3»-meter radius of the target" (the slow is generic-correct).
- 15321 Flowing Shadow: "Choose a direction to dash «7» meters as a phantom" (the strip is fixed in JSON; the caster does not move).
  - Proposal: wire `dash`: `{"stop": m}` = move the caster to `stop` m short of the locked target before the first hit (SkillStartMove with the show's movement effect), `{"distance": m}` = move m along the facing (or add "yingliu" = CasterForward desc index 0 and "DarkIllusion" to Displacements; for Dark Illusion the show 15401's movement effect must exist).

### Recast return / teleport back
- 15329 / 15330 / 115332 Move back Shadow Echo and 162021 / 162022 Move back Afterimage: "Leaves a shadow in place ... Cast again within «6» seconds to return to the shadow." 15329's buff 1532905 has _kLogicData [1533005] = the 15330 (RetreatingShadows2) row: the recast. Proposal: `"returnTo": "origin"` on 15330/115332/162022 (teleport to the stored cast origin of the buff's skill, consume the buff). 162021 (script Guishu) and 162023 Easy Movement (Chongfeng) are not in Displacements at all (no movement today); proposal: add "Guishu" = CasterBack desc 0 (the «10» m back) and check Chongfeng's description (its text is a copy of Afterimage).

### Area centre offset
- 15324 Shadow Break: "within a «5»-meter radius centered «1» meters in front of You", _kRange [500, 0, 0, 100]. Generic: 5 m around the caster. Proposal: `"offset": 1` (metres along the facing applied to the anchor), or read _kRange[3]/100 as a forward offset for FixCircle.

### Talent / passive procs (trigger not simulated; row itself is fine)
- 15231 Blade Dance (Normal Attack chance, AoE 5 m, 3 targets), 15232 Opening (extra ST Normal Attack), 15233 Chain Reaction (on Sonic Blow, 2 targets within 5 m, show 10), 15235 Sneak Attack (first hit from Camouflage), 15236 Triple Slash (on Double Attack), 15237 Poisoned to the Core and 15238 Toxic Rampage (on Virus Spread; "@{1} more times at the target location"), 15239 Spreading Shockwave (on Soul Destroyer hit), 15241 Hidden Blade (after Meteor Assault), 15221 Shadow Strike (stacks "«5»% Attack Speed/«5» CRIT"), 15327 disc ring (on Enhance Camouflage removal, buff 1530410), 15116 Poisoned / 15230 Venom Sting (DoT/proc tick rows). Proposal: `"trigger": {"on": "basicAttack|skill:<group>|buffRemoved:<buff>", "chanceBp": n, "icdMs": n}` on the proc row.
- 15112 Disguise passive: "Using Back Slide also grants [Camouflage] for «10» seconds" → a passive that adds 1500610 to 15104's self buffs.

### Cleanse
- 15314 Antidote (script jieduji): "Removes the [Poisoned] status from yourself." The 9030-group cleanse is hard-coded only for script "Detoxify". Proposal: `"cleanse": [9030]` (BuffConfig _iBuffID groups removed from the recipients).

### Invincible while casting
- 15326 Piercing Shadow: "You remain [Invincible] during this time. Immediately refreshes [Shadow Guard]." Several "Invincible" buffs exist (4909801, 4910501, 4912201, 4930902); none is clearly the player-skill one, so not added. "Refreshes [Shadow Guard]" is ambiguous (Shadow Guard's buff 1532210 'Thousand Shadows Status' or its cooldown). Proposal once confirmed: selfBuffs [[<invincible>, 10000, 1000 (_iActionTime)]] and `"resetCooldown": [15323]`.

### Damage number disagreements (left untouched)
- 15327 disc ring: desc «450»% but P1 3750 (37.5%). 450 / 37.5 = 12: possibly 12 discs each 37.5% (but _kShowDmg is empty and the template has one attack point). Needs the original script.

## Looks correct as is

15000 Normal Attack, 15003 Hiding, 15006 Sand Puff, 15007 Detoxify, 15099 Blade Smash, 15104 Back Slide (BackSlide displacement «10» m), 15106 Virus Spread (area; poison missing, see above), 15109 Grimtooth (3 m default around the locked target = «3», 1 + «4» = _iTargetMax 5, slow in _kBuff2), 15111 Envenom (buff), 15112 Disguise, 15114 Assassin Normal Attack (25%; its text is a Venom Splasher copy), 15116 Poisoned, 15204 Shadow Step (slow area; dash missing), 15211 Deadly Poison, 15221 Shadow Strike, 15222 Cross Impact, 15229 Meteor Assault (7 m / 5 targets; Stun and Blind rolled independently, the text says "or"), 15230 Venom Sting, 15231, 15232, 15233, 15235, 15236, 15237, 15238, 15241 (rows fine, triggers missing), 15304 Enhance Cloaking, 15305 Fatal Claw Marks, 15329 Move back Shadow Echo (RetreatingShadows displacement).

## Unsure

- 15209 Assassin Cross Normal Attack: desc «50»% but P1 [1675, 1675] (+3), two attack points; 3 x 16.75% ≈ 50% suggests a 3-hit basic attack, but nothing confirms the count. Left as is (16.75% per attack).
- 15115 Double Attack: no description; P1 5000, attack points [0.5, 0] (unordered). Probably the 2-hit Double Attack proc; left as is.
- 15234 Shadow Fang: no description; DragCircle 5 m, 3 targets, P1 60%+120. Left as is.
- 15239 Spreading Shockwave: "Launch two small spherical waves at each of @{2} targets near the target": P1 20%+50 with no _kShowDmg; whether P1 is per wave (2 hits) and the centre (the Soul Destroyer target, not a ground point) are unknown.
- 15302 Cross Ripper Slasher attack points [0, 0.333] (mode 1: [0, 0.167, 0.333]): may be throws per blade; depends on the blade system.
- 15110 / 15113: whether 15113 is also a player-castable variant (dist 0, DragCircle) or only the detonation row.
