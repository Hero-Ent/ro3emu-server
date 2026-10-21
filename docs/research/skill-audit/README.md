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
| Merchant / Blacksmith / Whitesmith / Mechanic | done by hand, see the "why" of each entry | `merchant.json` |

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
  (8 waves, 6 m), Ember Zone (8 x 0.5 s).
- **Bonus branch vs race/element** (`branchVs`): Soul Strike (+50% vs Undead), Magnus (+30% vs Undead/Demon/Shadow).
  Monster race ids, read from NPCConfig (inferred): 3 = Demon (Baphomet, Deviruchi), 5 = Undead (Ghoul, Mummy, Drake).
  Element ids: 8 = Shadow, 10 = Undead.

Still open: Death Bound (it reflects a % of the damage taken, and only from players), and the other engine items
listed in each report (chains/bounces, conditional branches, ASPD scaling, stat overrides, stacks).
