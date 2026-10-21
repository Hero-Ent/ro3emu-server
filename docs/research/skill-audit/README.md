# Skill audit (class by class)

Each file here is one class line's audit. The auditors compared every active skill's own description and timeline
with what the server does, and recorded the differences. The fixes the data can express are in
`src/Ro3Emu.Server/Reconstructed/skills/<line>.json`, which `Game/SkillMechanics.cs` reads. Everything else is listed
under "Needs engine work".

| Line | Report | Mechanics file |
|---|---|---|
| Swordsman / Knight / Lord Knight / Rune Knight | [knight.md](knight.md) | `knight.json` |
| Crusader / Paladin / Royal Guard | [crusader.md](crusader.md) | `crusader.json` |
| Mage / Wizard / High Wizard / Warlock | [mage.md](mage.md) | `mage.json` |
| Archer / Hunter / Sniper / Ranger | [archer.md](archer.md) | `archer.json` |
| Acolyte / Priest / High Priest / Archbishop | [acolyte.md](acolyte.md) | `acolyte.json` |
| Thief / Assassin / Assassin Cross / Guillotine Cross | [thief.md](thief.md) | `thief.json` |
| Merchant / Blacksmith / Whitesmith / Mechanic | [merchant.md](merchant.md) | `merchant.json` |

## Re-running the audit

1. Decode the client Lua with `tools/LuaDecode`. Then decompile `Localization_en.luac` with unluac
   (https://sourceforge.net/projects/unluac/, `java -jar unluac.jar Localization_en.luac > lua/Localization_en.lua`).
2. Run `python tools/skill-loc-ids.py`, which writes `loc_en_ids.json` (text id -> English). Set `RO3_AUDIT_DIR` to the
   folder holding `lua/Localization_en.lua`, or keep everything next to the script.
3. Run `python tools/skill-audit.py <job prefixes>`, for example `64` or `1210,1310,1410`. It prints every skill with
   its filled description, release/shape, damage, hits, buffs and timeline attack points. Both scripts read the game
   data from `Client/RO3Emu/data` (edit `data =` at the top if yours is elsewhere).

`SkillCombatTests.SkillMechanicsLoadAndReferenceRealData` checks that every entry loads and points at real rows,
buffs, attack points and modes.

## Resolved after the first audit (phase 2)

These "Needs engine work" items now run. The reports above still describe them as open.

- **Buff triggers** (`buffTriggers` in each JSON, run by `CombatService.Triggers.cs`): a buff that releases a skill
  when it expires, when its holder is hit (`hitTaken` / `meleeHitTaken`, with `maxRange`, `front` and `whileBuff`
  checks) or when its holder deals damage (`damageDealt`, `consume`).
  - Knight: Parry -> Auto Counter (11211).
  - Crusader: Shield Reflect -> 11419, Recoil -> 11420 (needs Auto Guard), Reflect Damage and Casting Shield -> Thorn
    11618, Charge Up -> Charge Up Strike 11622, Shield Defense -> Spear Auto Counter 11621 (front only).
  - Thief: Venom Splasher plants the bomb (no damage at the cast); the detonation (15113) happens when the bomb buff
    expires, 3 m around the target, 5 targets.
- **Chance to cast on hit** (`onHitCast`): Dragon Breath (Fire) has a 50% chance to Burn (1138201) each enemy it hits.
- **Periodic zones** (`hits.everyMs` + `afterPoint`, ticks counted from the attack point where the zone appears):
  Magnus Exorcismus (8 x 0.5 s), Sanctuary (7 x 0.6 s; heals allies, damages only Undead/Demon), Gravitation Field
  (8 waves on the 5 m circle the client draws; the text says 6 m), Ember Zone (8 x 0.5 s).
- **Bonus branch vs race/element** (`branchVs`): Soul Strike (+50% vs Undead), Magnus (+30% vs Undead/Demon/Shadow).
  Monster race ids, read from NPCConfig (inferred): 3 = Demon (Baphomet, Deviruchi), 5 = Undead (Ghoul, Mummy, Drake).
  Element ids: 8 = Shadow, 10 = Undead.

Still open after phase 2: Death Bound (it reflects a % of the damage taken, and only from players).

## Resolved in phase 3

- **Attribute scaling** (`_kDamageParamExtra`, `CombatService.StatScale`, applied to every player skill and heal):
  `[bp, 115, 10000, cap]` = "(1 + ASPD x n%)" up to the 500% ASPD cap (227 rows: Rolling Cutter, Blood Cross, Heavy
  Cannon Shot...), and `[1, bp, attr, ...]` = n% per base level (attr 0, Heal) or per point of STR/AGI/VIT/INT/DEX/LUK
  (the "Resonance" texts; Blitz Beat "Every «20» AGI ... «1»%" = 5 bp per AGI). Other layouts are ignored.
- **Bounce** (`bounce`): Envenomed Blade jumps 3 times to random enemies within 3 m of the last one hit.
- **Conditional branch** (`branchIf`): Sonic Blow +20% at or below 50% HP; Steal uses its 140% branch from Hiding.
- **Area offset** (`offset`): Shadow Break's circle is 1 m in front of the caster.
- **Mixed or swapped stats** (`matkPercent`, `stat: "matk"`): Soul Destroyer / Advanced Soul Destroyer add MATK x 50% /
  85%; Magic Crasher's physical hit scales with MATK.
- **Cleanse** (`cleanse`): Antidote removes Poisoned (group 9030).
- **Cooldown reset** (`resetCooldown`): Warg Bite resets Warg Hunt.
- Archer: Fear Breeze (30% piercing arrows on hit, `buffTriggers`), Focused Arrow Strike (18 m line, 1 + 5 targets),
  Warg Hunt shows its 3 hits.
- Charges and leaps (`dash`, `dash.minDistance`): Charge Attack, Warg Strike and Warg Hunt (only from farther than
  5 m), Rift Impact and Twilight move the caster; Fearless Charge moves 12 m (PinpointAttack2).
- Crusader per-mode entries: Shield Cast mode 0 (monster branch, 5 m), Earthshaker (10 ticks + final), Crescent Moon pull.
- Mechanic: every Axe Tornado / High-Energy Impact mode row (AxeTornadoPro, ...S1BD1/2, AxeLeapAttack2...) now uses
  the spin / leap schedule (they fell back to one hit); Vulcan Arm grants Firepower Loaded only at 15 stacks, with
  3 uses that Arm Cannon - Enhance spends (`stackGrant`, `consumePiles`).
- Ranger wargs: the following wolf is the mount lease's pendant 54001, now hidden while that wolf is ridden; the
  mount's `_kBindBuff` (1331001: +10% MSPD and Warg Strike / Arrow Smash mode 1) is granted while riding; unmounted
  Warg Strike makes the pendant wolf run to the target (SkillEffectDisplayNotify type 5, PendantShow); mounted Warg
  Charge dashes; Warg Hunt releases its melee / ranged row (`dispatch`), which carry the three wolves; Warg Charge and
  Warg Hunt reset Swift Warg. Still open: King of the Wargs' extra companions, Wolf's Fang, Wolf Taming's howl.
- Thief: Double Attack procs on dagger basic attacks; Cloaking / Enhance Cloaking hide from monsters and end after
  their hits. Hiding (15003) is only a speed buff in RO3's data.

Still open: blade stacks (Cross Ripper Slasher), poison DoT rows, Reading Spellbook extra casts, per-hit elements and
pulls (Tetra Vortex), dispatch by distance (Warg Hunt melee/ranged), proc tables for talents that cannot be learned,
and the other items marked open in each report.
