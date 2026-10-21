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
