# Equipment grids

`31400 EquipItemOpr` places equipment using `ItemConfig._iEquipPart`; the selected grid is persisted in the active
equipment plan and is also used by plan switching and weapon-card sockets.

Daggers (`ItemConfig._iEquipPart = 2`, `_iSubType = 1`) may use both weapon grids for the Assassin career line:
Assassin `5200`, Assassin Cross `5300`, and Guillotine Cross `5400` (`JobConfig`), **and only once the role has
learned Dual-Wield Mastery** (`SkillTreeConfig` 5201's `15107`, `RoleProgress.Skills`): the career alone does not
open the second grid. Allowing that dagger in the second grid once the skill is learned is inferred from those
client fields; other careers and other one-handed weapon subtypes remain restricted to grid 1.

Equipment-plan validation and weapon-card sockets use the same second-hand rule, so saved plans and cards remain valid
when an Assassin equips a dagger in each hand.
