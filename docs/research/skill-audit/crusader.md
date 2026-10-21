# Findings: Crusader / Paladin / Royal Guard (1210, 1310, 1410)

JSON: `D:\ro3emu-server\src\Ro3Emu.Server\Reconstructed\skills\crusader.json` (12 entries). Keys are `_iSkillID`, so one entry covers every `_iMode` row. Several groups here have modes that differ a lot, so some fixes could not go in (see "Per-mode entries").

## Fixed in JSON
- 11411 Holy Cross: maxTargets 1. The row is an area selector with `_kRange [0]` and 5 targets, but the description says "designated ST enemy".
- 11601 Banishing Thrust: rect 5 x 3 from the caster ("«3» * «5»-meter area in front"). kRange makes it 6.5 m long.
- 11602 Heavy Cannon Shot: rect 15 x 5 ("«5» * «15»-meter area"). kRange makes it 16 m long.
- 11603 Pinpoint Attack: anchor origin with rect 9 x 4. The 8 m caster dash happens before the hit, so a caster anchor measured the strip from the landing spot.
- 11608 Shield Press: hits use branch 1 ("Against Monsters ... increases by «100»%", P1 [20000, 40000]). This also covers mode 1, Enhanced Shield Press.
- 11611 Earth Drive: branch 1 (700% vs monsters) and a 5 m circle (kRange 6.5).
- 11613 Shield Defense: 10 pulses (points 1-10, every 0.5 s over the 5 s action), each a 3 m deep x 4 m rect around the caster's current position with a 3 m knockback. Before this, it dealt a single hit.
- 11621 Spear Auto Counter: fan 7 m / 180° (kRange is 8 m). The proc trigger itself is still missing (see below).
- 11624 Fearless Charge: first strike 50% at point 0, then the landing strike at point 2 using branch 1 (200%). Before this, only the 50% was dealt.
- 11628 Spear Formation - Crescent Moon: 3 hits of 50% (three attack points; the sibling EatChicken_2006 row confirms 3 x P1) and a 5 m circle.
- 11636 Powerful Shield Bash: branch 1 (+50% vs monsters). This also covers mode 1, Ultra Shield Bash.
- 11638 Shield Block family: branch 1 for Shield Auto Counter (modes 4/6, 200%→400%) and Powerful Auto Counter (modes 7/9, 400%→800%). Modes 0/3 deal no damage, so nothing changes for them.

## Needs engine work

### A. Per-mode mechanic entries (`"<group>:<mode>"` keys, falling back to the group)
- **11610 Shield Cast.** Mode 0 has P1 [10000, 20000] ("Against Monsters ... «100»%") and wants branch 1 with a 5 m circle. Mode 1 has P1 [40000] (one entry, so branch 1 falls back to 100%). Mode 2 is **Holy Light Suppression**, a Rect360 "«4» * «8»-meter area in front" with a move toward the target. A group-wide circle or branch would break modes 1 and 2, so the group was left out.
- **11604 Spear Formation - Blood Cross.** Modes 0/2 say «5» m (kRange 6 m). Modes 4/5 (OverBrand2) say «6» m (kRange 6.5/7). Left at kRange.
- **11624.** Modes 2/3 are **Earthshaker** ("Continuously spins ... a total of PATK * «350»% ... within a «8»-meter radius ... The final HIT inflicts Knockback"). P1 is [1000, 25000], and there are 11 attack points: 10 ticks x 10% plus a final 250% at point 10. They need `hits {points:[0..9], final:{point:10, branch:1}}` and knockback on the final hit only. They currently share Fearless Charge's two moments.
- **11624 modes 0/1 Fearless Charge shape.** The description says "«8» * «12»-meter area in front" (rect [12, 8]), but kRange is 950x400 (9.5 x 4). This could not be set because the Earthshaker modes are circles.
- **11628 mode 1** has 4 attack points ([0.1, 0.42, 0.67, 1.07]). It may be 4 hits; the group entry gives 3.

### B. Caster movement for more scripts (Displacements)
- **11624 Fearless Charge** (`PinpointAttack2`): "Move ${12} meters in that direction". `_kDescData[3]` = 12. Proposal: add `["PinpointAttack2"] = (CasterForward, 3)`. The first hit should stay anchored at the origin and the second at the caster.
- **11607 Twilight** (`HesperusLit`/`HesperusLits1`): "Leap to a selected area within «15» meters". The damage already lands on the dragged point (DragCircle 6 m, correct), but the caster never moves. Proposal: schema field `"leap": "point"`, meaning move the caster to `TargetPos` at the first attack point (same as the High-Energy Impact special case).
- **11610 mode 2 Holy Light Suppression** (`LightPress`): "Move toward the target".

### C. Proc / trigger skills fired by buffs (`BuffConfig._kLogicData` names the skill)
Each of these needs: when the buff holder is hit or deals damage, cast skill X (rate limit and chance from the description). Proposal: a generic `BuffTrigger` table keyed by buff id, with `{on: "hitTaken"|"meleeHitTaken"|"damageDealt", skill, cooldownMs, chance, consume}`.
- **11410 Shield Reflect** → buff 1140401 → **11419** Shield Reflect Auto Counter. "When you take Melee Physical DMG from an attacker within «5» meters ... PATK * «20»% + «50» ... up to «1» times per second". Row 11419's P1 2000 / P2 50 matches.
- **11416 Recoil** → buff 1140901 → **11420** Recoil Auto Counter. "While [Auto Guard] is active ... «1»% Chance to inflict [Stun] ... «1» sec (Cooldown «3» sec)". 11420's _kBuff2 is Stun 901601.
- **11609 Reflect Damage** → buff 1161005 (`_kLogicData [11618, 11404]`) → **11618 Thorn** (40%, 6 m, 5 targets). "can trigger up to «1» times every «0.5» seconds". **11610 Casting Shield** buff 1161101 also names 11618 ("triggers an Extra Auto Counter").
- **11602 Heavy Cannon Shot** → buff 1160405 `[11604, 11622, ...]` → **11622 Charge Up Strike** (50% PATK). "The next time you Deals DMG, removes [Charge Up] and triggers [Charge Up Strike] against the primary target" (consume on trigger).
- **11613 Shield Defense** → buff 1161610 `[11621, ...]` → **11621 Spear Auto Counter**. "When an enemy in front of you Deals DMG to you" (front check, 0.5 s cooldown). It should also reduce Shield Defense's CD by «0.5» s.
- **11612 King's Grace** → buff 1161201.. `[11619]` → **11619 Shield Break** (no description: 100%, 5 m, 5 targets, CD 1 s).
- Talent "Triggers" buffs (no descriptions, damage 25% P1): 1151201 "Holy Cross Triggers AoE Damage" → **11422**; 1151301 "Grand Cross Triggers ST" → **11423**; 1151401 "Grand Cross Triggers AoE" → **11424**; 1151501 "Shield Boomerang Triggers ST" → **11425**; 1151601 "Divine Suppression Triggers AoE" → **11426**; 1151701 "Divine Suppression Triggers ST" → **11427**; 1155301 "Rapid Smiting Triggers ST" → **11428**. The AoE ones are DragCircle 5 m. When the engine fires them, it must pass the triggering hit's target as TargetId/TargetPos, otherwise they centre on (0,0)/the origin.
- **11638 Shield Block → Shield Auto Counter / Powerful Auto Counter.** "Tap this skill again while blocking or wait until the block ends to use [Shield Auto Counter]. If you are attacked while blocking, [Shield Auto Counter] is Enhanced into [Powerful Auto Counter]". This needs a stance: re-cast or a 1 s timeout casts mode 4 (or mode 7 if hit during the block). 11641 Block Impact has no damage params and no attack points (likely client-only).

### D. "Against Monsters" damage branch as a general rule
Shield Press, Earth Drive, Powerful Shield Bash, the Shield Auto Counters and Shield Cast all store `[PvP value, monster value]` in `_kDamageParam1`. Proposal: a SkillConfig-level rule. When the description has "Against Monster(s) ... multiplier increases by" and P1 has 2 entries, use branch 1 against monsters and branch 0 against players. That would replace the per-skill `branch: 1` entries and fix 11610 mode 0.

### E. Conditional damage
- ASPD scaling (`_kDamageParamExtra [[30|50, 115, 10000, 500]]`): "For every «1»% ASPD, this DMG increases by «0.5»%". Affects 11601, 11602, 11604, 11607, 11624, 11628. Proposal: implement the extra-param formula type 115 (per-1% ASPD bonus, capped at the 4th value).
- Weapon condition: 11411 Holy Cross "When the caster has a two-handed spear equipped, the damage multiplier is increased by an Extra «5»%" (P1 branch 1 = 6300). 11616 Divine Insight has the same kind of two-handed spear condition on its buffs. Proposal: `"branchIfWeapon": {"twoHandedSpear": 1}`.
- 11604 Blood Cross: "If [Over Brand] is Active ... increasing the DMG to «82.5»%". This needs `"branchIfSelfBuff": {"buff": 11601 group, "branch": n}`, or the engine swapping to mode 4 (OverBrand2, P1 8250) while Over Brand is on.
- 11611 Earth Drive: "(Vitality Resonance «0.1»%)" (`_kDamageParamExtra [[1, 10, 3, 1, 100]]`), so it scales with VIT.

### F. Costs, shields, links, auras
- 11412 Grand Cross: "Consumes «150» HP". Proposal: `selfHpCost.flat`.
- 11502 Sacrifice: "each Normal Attack consumes «30» HP" while the buff is on. This needs an on-basic-attack HP cost in the buff engine.
- 11413 Devotion: "you take «20»% of the damage they receive ... If the Distance between you exceeds «10» meters ... the effect ends". The friendly Lock path gives both 1150405 (Devotion, logic [20]) and 1150901 ("Devotion Self-Link") to the ally, but the self-link belongs on the caster. It needs damage redirection plus a leash. 11612 King's Grace has the same redirect: "redirecting «50»% of the DMG taken by up to «4» allies within a «10»-meter radius to you", plus 20% lifesteal on damage taken and debuff immunity.
- 11612 King's Grace enemy part: "Up to «5» enemies within range gain «10»% [Slow] every «0.5» seconds, stacking up to «5» times. At «5» stacks ... [Stun] for «1» seconds". This is a periodic enemy aura around the caster (rangeType 0, so no enemy selection today). Proposal: `"pulse": {"everyMs": 500, "count": 10, "circle": 10, "targetBuffs": [...] }`.
- 11503 Battle Chant: "reduces the Physical Attack and Magic Attack of up to «10» enemies within «10» m by «50»". Both _kBuff1 entries (1150510, 1150610 "Battle Chant Orbit - Allies") go to allies; their `_kLogicData` (1150210 / 1150310) look like halo ids. The enemy debuff needs the halo/aura system.
- 11607 Twilight: "For each enemy hit, gain 1 stack of Shield equal to «3»% of your Max HP". 1160710 is given once via _kBuff1. Proposal: `"selfBuffPerHit": [buff, ms]`, and the shield value from `_kDamageParamExtra [[0, 300, 101, 3]]` (% max HP).
- 11610 Casting Shield / 11607 Twilight shields: the buffs carry `T_AXadd0_etc [[0, 500|300, 101, 3]]` (shield = % of stat 101, Max HP). Check that the buff engine turns these into shield amounts.
- 11613 Shield Defense: "protecting you and up to «4» allies within a «10»-meter radius and reducing DMG taken by «5»%". Only the caster gets 1161610, so the ally DR needs an ally recipient list on a hostile skill. "Can only be used in Gryphon State" is not checked.

### G. Pull toward caster
- 11628 Crescent Moon: "Pulls the targets closer". Only the `PhantomThrust` script pulls today. Proposal: schema `"pull": meters` (TargetPull to N m in front of the caster), applied on the first hit only.
- 11624 Fearless Charge knockback ("inflicts Knockback", no distance given) and Knock-Up: not set, because the description gives no number.

### H. Cleanse script not handled
- 11406 Healing Skills (`_kScriptLogic` "Cleanse"): "Removes [Silence] and [Blind] from a designated ST ally". `ApplyEffectsAsync` only cleanses for `Detoxify` / `HealingSkills`, and "Cleanse" appears nowhere in the code, so this skill does nothing. Fix: add `"Cleanse"` to the `HealingSkills` branch (groups 9023 Silence, 9025 Blind; Holy Cross's Blind 902501 is group 9025).

## Looks correct as is
11400 Basic Attack; 11405 Heal; 11408 Auto Guard; 11409 Smite; 11412 Grand Cross (5 m circle, P1 120% + 120 = description, `_kShowDmg [3]`; only the HP cost is missing); 11414 Providence; 11415 Defending Aura; 11418 Shield Boomerang; 11501 Gloria Domini (SP drain via buff2 1150810); 11504 Rapid Smiting; 11607 Twilight (damage/shape/Immobilize; the leap is missing); 11615 Piety; 11616 Divine Insight; 11618 Thorn / 11619 Shield Break / 11622 Charge Up Strike (correct once triggered); 11410/11416/11609/11612/11502/11503 buff application (their proc/aura parts are listed above).

## Unsure
- 11605 Moon Slasher: the text uses unfilled @{n} placeholders that do not line up with `_kDescData ['5', '1', '360', '3']` ("Deals @{2}% Physical ATK" would read «1»%). P1 is 12000 + 100 and the timeline has one attack point. Possibly 3 x 120% = 360%, but there is nothing to confirm it. Likely a legacy skill replaced by 11628. Left out.
- 11625 Earthshaker (standalone group, lv1 only): the description says "«100»% Earth Physical damage" but `_iEleType` is 1 (Neutral), and the debuff "for «3»s" uses buff2 1161301 at 5000 ms. The schema has no element or buff-duration override; possibly a legacy row. 11 attack points but a single described number, so it was kept generic.
- 11628 Crescent Moon: _kBuff2 is Stun 901601 1 s, which the description does not mention. With 3 hits it is re-applied 3 times.
- 11418 Shield Boomerang mode 1 (`ShieldBoomerangall`): the description is identical to mode 0 ("selected ST enemy"); "all" may mean a bounce in the original script.
- 11613 Shield Defense pulse timing: I picked points 1-10 (0.5 s … 5.0 s). Points 0-9 are equally plausible; the template has 11 points.
- kRange vs description deltas of 0.5-1 m (Twilight 6/6, Spear Auto Counter 8/7, Moon Slasher 6/5, Thorn 6/6) may be deliberate indicator padding. I only overrode where the description gives a different number and all modes agree.
