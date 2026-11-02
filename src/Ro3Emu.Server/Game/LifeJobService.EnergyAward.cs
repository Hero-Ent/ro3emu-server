using Ro3Emu.Server.Data.Config;

namespace Ro3Emu.Server.Game;

/// <summary>
/// Vigor awarded as an item. The To-Do chests (GenericStageRewardsConfig type 7, AwardConfig 10080001..10080003) pay
/// item 407 "Vigor", a currency row (_iType 1) of _iSubType 125, enum_ItemSubTypeEnum.LifeJobEnergy in
/// Config/Define/Lua_GameDefine.lua. Its balance is not DBItemsInfo.currency: LC_LifeJobEnergyShowFunc.GetCount reads
/// LC_RestrictHelper with PublicParamConfig.LifeEnergy (restriction 1004), and RestrictConfig[1004]._iDes says the
/// Vigor that gathering and crafting consume is obtained from those chests. A credited balance therefore moves into
/// restriction 1004 in one commit, the currency paying for it.
/// </summary>
public sealed partial class LifeJobService
{
    /// <summary>enum_ItemSubTypeEnum.LifeJobEnergy (Lua_GameDefine.lua).</summary>
    public const long LifeEnergySubType = 125;

    private int[]? _energyCurrencies;
    private readonly HashSet<GameSession> _energyQueued = [];

    private int[] EnergyCurrencies() => _energyCurrencies ??= ItemConfig.All(_data)
        .Where(item => item.Type == CharacterService.ItemTypeCurrency && item.SubType == LifeEnergySubType)
        .Select(item => (int)item.Id).ToArray();

    /// <summary>
    /// RestrictConfig[1004]._iDes shows VitalityExtraLimit as the most Vigor a role can accumulate, the cap the Vivify
    /// Potion also stops at. (inferred) A balance past that cap stays as currency until spending makes room.
    /// </summary>
    private long EnergyRoom(GameSession s) =>
        Param("VitalityExtraLimit", 5000) - s.Restrictions!.RestrictCount[VigorId].Count;

    private bool EnergyPending(GameSession s) =>
        s.Progress != null && s.LifeJob != null && s.Restrictions?.RestrictCount.ContainsKey(VigorId) == true
        && EnergyRoom(s) > 0 && EnergyCurrencies().Any(id => s.Progress.Currency.GetValueOrDefault(id) > 0);

    /// <summary>CharacterService.StateChanged (any award, exchange or GM grant) and role entry.</summary>
    private void QueueEnergyCredit(GameSession s)
    {
        if (!EnergyPending(s) || !_energyQueued.Add(s)) return;
        _loop.PostOwned(s, async () =>
        {
            _energyQueued.Remove(s);
            await CreditEnergyAsync(s);
        }, "life energy credit", discarded: () => _energyQueued.Remove(s));
    }

    private async Task CreditEnergyAsync(GameSession s)
    {
        using var gate = await CharacterService.Lock(s);
        if (!EnergyPending(s)) return;
        var restrict = s.Restrictions!.Clone();
        var energy = restrict.RestrictCount[VigorId];
        long room = EnergyRoom(s);
        var cost = new List<(int Entry, long Count)>();
        foreach (int id in EnergyCurrencies())
        {
            long take = Math.Min(room, s.Progress!.Currency.GetValueOrDefault(id));
            if (take <= 0) continue;
            cost.Add((id, take));
            energy.Count = checked(energy.Count + take);
            room -= take;
        }
        if (cost.Count == 0) return;
        await CommitAsync(s, "cd13c/life-energy-award", s.LifeJob!.Clone(), restrict, Copy(s.LifeRuntime), cost, [],
            result: new { Credited = cost.Select(c => new { c.Entry, c.Count }).ToArray() });
    }
}
