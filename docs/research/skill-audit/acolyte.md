# Findings: Acolyte line (Acolyte 4100 / Priest 4200 / High Priest 4300 / Archbishop 4400)

Audit prefix `4`. No Monk rows exist under job prefix 4 (the Monk branch is not in this data set).
JSON: `D:\ro3emu-server\src\Ro3Emu.Server\Reconstructed\skills\acolyte.json` (7 entries, validated, no duplicate keys across the folder).

Talent procs 142xx have empty descriptions; their text is in SpecialEffectConfig (`_kSpecialEffect [[2, buff]]`, buff `_kLogicData [chance, SkillConfig row]`). Mapping used:
16420002 -> 1421101 -> 14214 · 16420003 -> 1421201 -> 14208 · 16420007 -> 1421601 -> 14212 · 16420005 -> 1421501 -> 14216 · 16420010 -> 1422401 -> 14209 · 16420013 -> 1422101 -> 14215 · 16420012... · 16430012 -> 1423901 -> buff 1424101 (3 s) -> 14211 · 16420015 -> 1422201 -> 1422301 -> 14210 · 16420022 -> 1422701 -> 14213.
**No server code casts these talent procs today** (no trigger for buff `_kLogicData [chance, skill]`), so their JSON entries only take effect once that exists.

## Fixed in JSON
- 14109 Magnus Exorcismus — area is «7»m*«7»m on the dragged point (kRange 8x8): `anchor point, rect [7,7]` (cross approximated by its bounding square). Ticking still missing, see below.
- 14314 Holy Stigma — «5»-meter radius vs kRange 7 m: `anchor point, circle 5`.
- 14117 Kyrie Eleison — "yourself and a designated ST ally", `_iTargetMax` 2: `selfBuffs [[1411705,10000,10000]]`.
- 14113 Aspersio — "your Basic Attack element and that of a selected ST ally", `_iTargetMax` 2: `selfBuffs [[1411301,10000,60000]]`.
- 14208 Radiant Resurrection (Turn Undead proc) — "the enemy and up to «3» enemies within a «4»-meter radius": `anchor target, circle 4, maxTargets 4`.
- 14213 Holy Light Bludgeon (Mace Basic Attack proc) — same wording, «4» m, 1+3: `anchor target, circle 4, maxTargets 4`.
- 14209 Holy Cross Judgment (Magnus proc) — "up to «4» enemies within the area" (the Magnus 7x7 area): `anchor point, rect [7,7], maxTargets 4`.

## Needs engine work

### Engine bug: Cleanse never cleanses
- 14014 Healing Skills ("Removes [Silence] and [Blind] from the designated ST ally") and 11406 share `_kScriptLogic` **"Cleanse"**, but CombatService.Effects.cs:332 checks `script is "Detoxify" or "HealingSkills"`. No row has script "HealingSkills", so the [9023, 9025] removal never runs. Fix: match `"Cleanse"` (keep groups 9023 Silence, 9025 Blind; row `_kLogicData [6, 5]` may be the status category ids).

### Periodic damaging/healing zone (not in the field list) — proposed `"zone": { "durationMs", "intervalMs", "ticks", "shape", "maxTargets", "heal": bool, "enemyBranch" }`
Reuse CreateSkillObjectAsync: a SkillObject at the anchor that runs the row's damage (and/or ally heal) every interval, with the cast's damage snapshot.
- 14109 Magnus Exorcismus — "For the next «4» sec., it deals Magic ATK*«10»%+«60» ... to up to «4» enemies in the area every «0.5» sec." P1 1000 is per tick; 8 ticks (`_kLogicData [22000009, 4, 8, 10]`: 22000009 looks like the NPC/skill object, 4 targets, **8 ticks**). Today: one tick only. Hits/times cannot express it: ImpactOffset clamps template times past m_kState end (2.5 s) to the cast end, and Live() drops impacts after the cast slot is released.
- 14115 Sanctuary — "For the next «4» seconds, every «0.6» seconds, it restores Life equal to your Magic ATK*«10»%+«100» points to up to «4» allies within the area. Undead-property and Demon Race monsters within the area take ... Magic ATK*«10»%+«100»." ~6-7 ticks, heal + damage. Today: one heal + one hit.
- 14215 Purifying Light Orbit / 14211 Radiant Sanctuary (Sanctuary talents, below) attach to the same zone.

### Conditional target filter by race/element — proposed `"enemyFilter": { "races": [..], "elements": [..] }` and `"bonusVs": { "races", "elements", "branch" }`
- 14115 Sanctuary — damage only to "Undead-property and Demon Race monsters"; its selector is `m_kSelectType 4` with **no race mask**, so today it damages every enemy in the area.
- 14109 Magnus Exorcismus — "Damage dealt to enemies of the Undead Race or Demon Race, or with the Shadow property or Undead property, is increased by «30»%": P1 [1000, 1300] branch 1 = the +30% branch; the engine always uses branch 0. Proposal: pick `bonusVs.branch` (1) when the target matches.
- 14007 Heal — enemy branch: description says "Undead-property enemy", selector race mask 16 = Undead **race**. Element (property) check missing (see Unsure).

### Ally-only heal procs / heals with enemy selectors — proposed `"healOnly": true` (+ `"allyShape"` so friendly selection uses the mechanic shape/anchor instead of `_kRange[0]` around the centre)
- 14215 Purifying Light Orbit (16420013 "When [Sanctuary] is cast, it restores MATK*«5»%+«100» HP to up to «4» allies within the area") — row is Lock / rangeType 2 / select type 4: friendly path heals only the locked ally (release 1), and the enemy path would ALSO deal MATK*5%+100 to 4 enemies around the target.
- 14211 Radiant Sanctuary (16430012 "After [Sanctuary] has been active for a time, it restores MATK*«5»%+«500» HP to up to «5» allies within «15» meters of you"; buff 1424101 3000 ms delay) — row selector is enemy (1,0,-1), rangeType 1: would damage the locked enemy instead of healing 5 allies in 15 m.
- 14216 Sacred Divine Light (16420005 "[Heal] restores an Extra MATK*«5»%+«60» HP to targets below «80»% HP", buff `_kLogicData [80, 1421601]`) — row selector is enemy (1,0,-1); needs heal-on-ally + HP-threshold condition.
- 14303 Radiant Holy Light — "up to «5» allies within a «20»-meter radius, prioritizing Party Members": ally selection uses `_kRange[0]` 2300 = 23 m and nearest-first; needs radius 20 and party priority.
- 114303 Melodic Flow — "Restores Life equal to Magic ATK*«30»%. If the unit is a party Member, restores ... «60»%": P1 [3000, 6000], engine uses P1[0] for everyone. Proposed `"partyBranch": 1`.
- 14304 Sacred Lily / 14305 Prayer of the Holy Spirit / 14315 Hymn of Devotion / 14302 Hymn of the Lamb — "Party Members within ..." but the friendly path picks any player in range. Proposed `"partyOnly": true`.

### Talent proc dispatcher — proposed engine feature: buffs whose `_kLogicData` is `[chanceBp, skillRow]` (or `[hpThreshold, skillRow]`, `[skillRow, chanceBp]` for 1422201) fire that row as an independent cast on the triggering hit's target/point, honouring the ICD in the talent text
- 14214 Holy Light Resurrection ("When [Turn Undead] hits an enemy ... «1» Extra hit ... MATK * «5»% + «30». ICD «1.5» sec") — generic row is fine once cast.
- 14208 / 14213 / 14209 — shapes already in JSON.
- 14212 Divine Light Strike ("When [Basic Attack] hits ... «1» hits of MATK*«10»%+«120» ... ICD «3» s") — generic fine.
- 14210 Turn Undead [Extra Trigger] ("«50»% Chance to trigger Turn Undead one Extra time. ICD «2.5» s", buff 1422201 `[1422301, 500]`) — generic fine.
- 14316 / 14317 Double Holy Light (Duple Light, 30% phys/magic) — triggered by Judex; 14312 Holy Spirit Surge makes the chance «100»% for 10 s.
- 114313 Song of Suffering - Murmur ("On HIT, deals an Extra instance ... «5»%"), 114317-114319 Musical Phrase (Sacrament launches 1-3 phrases per Judex, Voice Part stacks) — generic damage fine; the Sacrament/Voice Part/Low Hum resource system is missing.

### Reveal hidden enemies — proposed `"reveal": { "radius": m, "buffGroups": [...], "followUp": row }`
- 14005 / 14006 Ruwach — "locates one enemy within a «20»-meter radius who is under the [Disguise] Status Effect. Removes their [Disguise] ... deals ... MATK * «120»%. The holy spirit lasts «10» seconds and disappears after Discovering a target." Buff 1400501 (`_kLogicData [1400601]`) should watch for hidden units and cast 14006 (P1 12000) on them. Today: only the buff is applied; 14006 is never cast.

### Buff interactions on hit
- 14011 Holy Light — "removes the target's [Kyrie Eleison] effect" (`_kLogicData [14117]`). Proposed `"stripTargetBuffs": [14117]` (buff group).
- 14102 Lex Aeterna — "disappears after the target takes damage" (buff script Buff_Lex_Aeterna, attr 1216 -2000). Needs consume-on-next-hit.
- 14118 Turn Undead — "If the target is a Normal Undead Monster, there is a «5»% Chance to kill it instantly" (`_kLogicData [1101, 500]`). Proposed `"instantKill": { "chance": 500, "races": [undead], "normalOnly": true }`.

### Costs / charges
- 14010 Increase AGI — "Consumes «30» points of Life": selfHpCost is percent-only. Proposed `selfHpCost.flat`.
- 14314 Holy Stigma — "Uses: +1 every «10» seconds, up to 2" (cd 200 ms): charges system missing. Proposed `"charges": { "max": 2, "rechargeMs": 10000 }`.

### Death prevention / shields / HoT handled only if BuffService implements them (not verified)
- 14305 Prayer of the Holy Spirit (buff 1430501: "[Respawn] once upon taking lethal damage, returning with «30»% of Max Life"), 14117 Kyrie shield (MATK*«80»%), 14303 shield (MATK*«100»%, buff 1430310), 14315 Hymn of Devotion HoT ("MATK * «50»% + «50» every second ... «10» seconds", buff 1432504 has no logic data), 14302 Hymn of the Lamb immunity («3» charges, buff 1430201) + cleanse (`_kLogicData [37, 33]`; no cleanse code for script "Praefatio").

## Looks correct as is
14001 Blessing, 14015 Angelus, 14016 Slow, 14017 Pneuma (field), 14050 Temporary Revive / 14112 Resurrection (Resurrection script), 14099 Staff Smash, 14105 Magnificat, 14106 Gloria, 14119 Impositio Manus, 14121 Lex Divina (Silence in _kBuff2), 14122 Safety Wall (field), 14123 Suffragium, 14201 Assumptio, 14206 Basilica, 14007 Heal (branch 0 ally / 1 enemy), 14301 Highness Heal, 14309 Judex (170% shown in 2), 14310 Holy Reckoning (7 m DragCircle, 5 targets, Immobilize+Blind), 14312 Holy Spirit Surge, 14313 Divine Retribution, 14998/14999 Basic Attack, 114305 Battle Chant Harmony - Resonance (60%, 5 allies, 20 m), 114311 Organum (80% shown 3, 5 targets), 114317-114319 Musical Phrase (P1 = total, split 1/2/3), 114313 Murmur, 14210/14212/14214 procs (single target, generic row correct once cast). Skipped: EatChicken_* 154001-154010, 154000 event normal attack, 14308/14395 Respawn (tree spirit), 114302 (buff only, no text).

## Unsure
- 14007 Heal / 14301 Highness Heal — "for you and the designated ST ally" / "to You and one designated ally": `_iTargetMax` 1, so read as "you OR an ally" (not added to JSON). Kyrie/Aspersio have `_iTargetMax` 2 and were treated as "both".
- 14007 Heal enemy branch: selector race mask 16 (bit 5 = Undead race) vs description "Undead-property" (element). Which one the original server used is unknown.
- 14109 Magnus `_kShowDmg [3, 3, 2, 2]` (4 entries for 2 damage branches) — the per-tick display split is unclear; left as is.
- 214390 Solace of Eternal Silence (Soul Burst) — placeholder description "The Soul Burst skill is so cool!", friendly selector, FixCircle 20 m, 15 targets, P1 [21000, 140000], attack points [0.07, 0.8, 4.7], buffs 21439001 (`_kLogicData [50, 3000, 50, 3000]`) + 903202. Today it would heal up to 15 allies for MATK*210% once; no evidence for what it should do.
- 14303 Radiant Holy Light — "Instantly calls down" but chant 2000 ms and attack point 4.17 s; ally radius 23 m (kRange) vs «20» m. Left alone (shape fields do not affect ally selection).
- 14215 / 14211 / 14216 — the proc rows' selectors contradict their texts (enemy or both vs heal-only); kept out of JSON until a heal-only path exists.
