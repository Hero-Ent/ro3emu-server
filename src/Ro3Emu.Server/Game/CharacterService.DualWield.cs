using Ro3Emu.Server.Data.Config;

namespace Ro3Emu.Server.Game;

public sealed partial class CharacterService
{
    /// <summary>enum_WeaponFromType (Lua_GameDefine): JobConfig._kWeaponForm values.</summary>
    private const long WeaponFormSingleHand = 1;

    /// <summary>
    /// The grids an item of EquipPosType <paramref name="part"/> may go to for this role. LC_EquipmentHelper
    /// HaveGearEquipByEquipPosType: when the career's weapon status is TwoHandAndDoubleHand (JobConfig._kWeaponForm
    /// [2, 3] = {TwoHand, DoubleHand}: careers 5200, 5300 and 5400 in the shipped table), a MainHand weapon may go
    /// to either hand (MainHand first, then ViceHand, else the lower scored one) and TakeOnEquip sends that grid;
    /// every other career keeps the static table (<see cref="GridsFor"/>).
    /// </summary>
    private int[] EquipGridsFor(GameSession session, int part) =>
        part == 2 && DualWields(session.Progress?.Career ?? 0) ? [1, 2] : GridsFor(part);

    /// <summary>LC_EquipmentHelper GetWeaponStatusType == enum_WeaponStatusType.TwoHandAndDoubleHand: more than one
    /// weapon form, and SingleHand is not one of them (Lua_CFG_JobConfig GetWeaponForm).</summary>
    private bool DualWields(long career) =>
        JobConfig.Get(data, career)?.WeaponForm is { Length: > 1 } forms && !forms.Contains(WeaponFormSingleHand);
}
