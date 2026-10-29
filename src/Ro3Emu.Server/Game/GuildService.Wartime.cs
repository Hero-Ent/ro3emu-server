using Romsg;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Data.Config;

namespace Ro3Emu.Server.Game;

/// <summary>
/// The war warehouse and the Tactics panel's war machine permissions (DBGuildData guildWartimeInfo,
/// docs/systems/territory-wars.md "War warehouse and machine permissions").
/// 32273 is the warehouse (machine item and count; LC_DAL_Guild.ReceiveWartimeStoreItemNtf feeds the War Manager page and
/// the siege battle bag). 32284 lists both machine types' operator slots, 32282 sets or clears one slot (roleGuildId 0)
/// and 32283 swaps two slots of a type (LC_SiegeHelper SendGetPermissionListReq / SendModifyPermissionReq /
/// SendExchangePermissionReq, UI_Sys_Siege_PermissionManageWidgetCtrl). Each change goes to the online members as 32286
/// (LC_DAL_Siege keys it by bagIndex, the list's 1-based position), and a member whose own machine type changed gets 32285
/// (SiegePermissionSelfData, which filters the battle bag to that type). Editing needs GuildPermissionConfig 13
/// War Machine Management, as the panel's Management button.
/// </summary>
public sealed partial class GuildService
{
    public const uint WartimeStore = 32273, ToolAssign = 32282, ToolExchange = 32283, ToolList = 32284,
        ToolPermission = 32285, ToolChange = 32286;
    public const uint Prepay = 32270, CancelPrepay = 32272;

    /// <summary>
    /// Set by TerritoryWarsService: the side (1 attack, 2 defense) the role fights for in an active siege it is loaded
    /// into, else 0. Machines are reserved and built only there (inferred: the battle bag that offers them is the
    /// siege's, LC_WartimeItemHelper.TryBuildCannon).
    /// </summary>
    public Func<GameSession, int>? WarSideOf { get; set; }
    private const int MachineRight = 13;

    /// <summary>(inferred) Operator slots per machine type: no config row sets them, and the panel shows as many as it
    /// receives (UI_Sys_Siege_PermissionManagePanelModel counts the list).</summary>
    internal const int FirearmSlots = 10, VehicleSlots = 10;

    private static readonly WartimeToolType[] ToolTypes =
        [WartimeToolType.Firearm, WartimeToolType.Vehicle];

    private static int SlotCount(WartimeToolType type) => type switch
    {
        WartimeToolType.Firearm => FirearmSlots,
        WartimeToolType.Vehicle => VehicleSlots,
        _ => 0,
    };

    private static bool ValidSlot(WartimeToolType type, int index) => index >= 1 && index <= SlotCount(type);

    /// <summary>The role in a slot; a member who left holds nothing.</summary>
    private static long Holder(GuildStore.State g, WartimeToolType type, int index) =>
        g.WartimeSlots.TryGetValue((int)type, out var slots) && slots.TryGetValue(index, out long role)
            && g.Members.ContainsKey(role) ? role : 0;

    /// <summary>The machine type a member operates (a member holds at most one slot), or None.</summary>
    internal static WartimeToolType ToolOf(GuildStore.State g, long role) => role == 0 || !g.Members.ContainsKey(role)
        ? WartimeToolType.None
        : ToolTypes.FirstOrDefault(type => g.WartimeSlots.TryGetValue((int)type, out var slots)
            && slots.Any(p => p.Value == role && ValidSlot(type, p.Key)));

    /// <summary>
    /// The warehouse: the guild's own stock once something changed it, else PublicParamConfig SiegeInitItemList
    /// [[item, count], ...] (inferred: a guild starts with that list; the name and the client say no more).
    /// </summary>
    internal IReadOnlyDictionary<int, int> WartimeStock(GuildStore.State g)
    {
        if (g.WartimeStocked) return g.WartimeItems;
        var stock = new SortedDictionary<int, int>();
        if (data.Table("PublicParamConfig").TryGetValue("SiegeInitItemList", out var list)
            && list.ValueKind == System.Text.Json.JsonValueKind.Array)
            foreach (var pair in list.EnumerateArray())
                if (pair.ValueKind == System.Text.Json.JsonValueKind.Array && pair.GetArrayLength() == 2
                    && GameData.Number(pair[0]) is { } item && GameData.Number(pair[1]) is { } count && count > 0)
                    stock[(int)item] = stock.GetValueOrDefault((int)item) + (int)count;
        return stock;
    }

    /// <summary>The cannon items GVGCannonConfig builds (_iItemID): the machines 32270 reserves.</summary>
    private bool IsCannonItem(int item) => item > 0
        && data.Table("GVGCannonConfig").Values.Any(r => new GVGCannonConfig(r).ItemId == item);

    /// <summary>The guild's own stock from now on: the initial list becomes its starting stock (inferred).</summary>
    private void Stock(GuildStore.State g)
    {
        if (g.WartimeStocked) return;
        g.WartimeItems = new Dictionary<int, int>(WartimeStock(g));
        g.WartimeStocked = true;
    }

    /// <summary>A member's reserved machine back into the warehouse; false when there was none.</summary>
    private static bool ReturnPrepay(GuildStore.State g, long role)
    {
        if (!g.WartimePrepay.Remove(role, out int item)) return false;
        g.WartimeItems[item] = g.WartimeItems.GetValueOrDefault(item) + 1;
        return true;
    }

    /// <summary>32273 with the current warehouse to every online member.</summary>
    private async Task PushStoreAsync(GuildStore.State g)
    {
        var ntf = StoreOf(g);
        foreach (var m in g.Members.Values)
            if (Online(m.RoleId) is { } peer) await peer.SendGameAsync(WartimeStore, ntf);
    }

    public Task PrepayAsync(GameSession s, GuildWartimeStorePrepayReq req) =>
        RunGuildAsync(s, [], [], () => PrepayCoreAsync(s, req));
    public Task CancelPrepayAsync(GameSession s) => RunGuildAsync(s, [], [], () => CancelPrepayCoreAsync(s));

    /// <summary>
    /// 32270, the cannon item clicked in the siege battle bag: one machine leaves the warehouse for the member, and the
    /// answer's prepayItem starts the blueprint (LC_DAL_Guild.ReceiveWartimeStoreItemPrepay -> DoPrepayAction ->
    /// LC_GVGBuildCannon.StartBuild); a refusal shows its code instead (LC_AssetManager DecodeError). (inferred) The
    /// member needs the Firearm permission of the Tactics panel (the battle bag only lists that type, 32285), the item a
    /// cannon of GVGCannonConfig, stock left (535 "Not enough guild supplies") and a running siege; a reservation it
    /// already holds goes back first, and it always reserves one machine.
    /// </summary>
    private async Task PrepayCoreAsync(GameSession s, GuildWartimeStorePrepayReq req)
    {
        var g = Own(s);
        int item = req.Item?.ItemId ?? 0;
        int code = g == null ? ErrorCodes.TerritoryGuild
            : WarSideOf?.Invoke(s) is not > 0 ? ErrorCodes.FeatureUnavailable
            : !IsCannonItem(item) ? ErrorCodes.Parameter
            : ToolOf(g, s.Role!.Id) != WartimeToolType.Firearm ? ErrorCodes.GuildPermission : 0;
        if (code == 0)
        {
            Stock(g!);
            long role = s.Role!.Id;
            int available = g!.WartimeItems.GetValueOrDefault(item) + (g.WartimePrepay.GetValueOrDefault(role) == item ? 1 : 0);
            if (available <= 0) code = ErrorCodes.GuildSupplies;
            else
            {
                ReturnPrepay(g, role);
                g.WartimeItems[item]--;
                g.WartimePrepay[role] = item;
            }
        }
        if (code != 0)
        {
            await s.SendGameAsync(Prepay, new GuildWartimeStorePrepayResp { Result = code });
            return;
        }
        await SaveGuildAsync(g!);
        await s.SendGameAsync(Prepay, new GuildWartimeStorePrepayResp
        { PrepayItem = new GuildWartimeItem { ItemId = item, ItemCount = 1 } });
        await PushStoreAsync(g!);
    }

    /// <summary>
    /// 32272, Esc on the blueprint (LC_GVGBuildCannon.onClickEsc -> LC_WartimeItemHelper.CancelPrepay): the reserved
    /// machine goes back. Nothing reserved is not an error (inferred): the client sends it without checking and has no
    /// handler for the answer, so a refusal would only pop an error up.
    /// </summary>
    private async Task CancelPrepayCoreAsync(GameSession s)
    {
        var g = Own(s);
        if (g == null)
        {
            await s.SendGameAsync(CancelPrepay, new GuildWartimeStoreItemCancelUseResp { Result = ErrorCodes.TerritoryGuild });
            return;
        }
        int item = g.WartimePrepay.GetValueOrDefault(s.Role!.Id);
        if (!ReturnPrepay(g, s.Role.Id))
        {
            await s.SendGameAsync(CancelPrepay, new GuildWartimeStoreItemCancelUseResp());
            return;
        }
        await SaveGuildAsync(g);
        await s.SendGameAsync(CancelPrepay, new GuildWartimeStoreItemCancelUseResp
        { Item = new GuildWartimeItem { ItemId = item, ItemCount = 1 } });
        await PushStoreAsync(g);
    }

    private GuildWartimeStoreItemNtf StoreOf(GuildStore.State? g)
    {
        var ntf = new GuildWartimeStoreItemNtf();
        if (g != null)
            ntf.WartimeItems.AddRange(WartimeStock(g).Where(p => p.Value > 0).OrderBy(p => p.Key)
                .Select(p => new GuildWartimeItem { ItemId = p.Key, ItemCount = p.Value }));
        return ntf;
    }

    private static GetGuildWartimeToolBelongListRsp ToolListOf(GuildStore.State g)
    {
        var rsp = new GetGuildWartimeToolBelongListRsp();
        foreach (var type in ToolTypes)
        {
            var page = new GuildWartimeItemPermissionClient { ToolType = type };
            for (int index = 1; index <= SlotCount(type); index++)
                page.InfoList.Add(new GuildWartimeToolBelongInfo { RoleGuildId = Holder(g, type, index) });
            rsp.ItemPermissionAllData.Add(page);
        }
        return rsp;
    }

    /// <summary>A member's warehouse view and own permission, with the full guild info (login, join, refresh).</summary>
    private async Task SendWartimeAsync(GameSession s, GuildStore.State g)
    {
        await s.SendGameAsync(WartimeStore, StoreOf(g));
        if (s.Role != null && ToolOf(g, s.Role.Id) is var type && type != WartimeToolType.None)
            await s.SendGameAsync(ToolPermission, new GuildWartimeToolRolePermissionNtf { ToolType = type });
    }

    /// <summary>A former member: an empty warehouse and no permission (the battle bag empties).</summary>
    private static async Task ClearWartimeAsync(GameSession s)
    {
        await s.SendGameAsync(WartimeStore, new GuildWartimeStoreItemNtf());
        await s.SendGameAsync(ToolPermission, new GuildWartimeToolRolePermissionNtf());
    }

    public Task ToolListAsync(GameSession s) => RunGuildAsync(s, [], [], () => ToolListCoreAsync(s));
    public Task ToolAssignAsync(GameSession s, GuildWartimeToolBelongOperReq req) =>
        RunGuildAsync(s, [], [], () => ToolAssignCoreAsync(s, req));
    public Task ToolExchangeAsync(GameSession s, GuildWartimeToolExchangeOperReq req) =>
        RunGuildAsync(s, [], [], () => ToolExchangeCoreAsync(s, req));

    /// <summary>32284: both pages (any member may read them; the guild window's War Machine page does too).</summary>
    private Task ToolListCoreAsync(GameSession s) => s.SendGameAsync(ToolList,
        Own(s) is { } g ? ToolListOf(g) : new GetGuildWartimeToolBelongListRsp { Result = ErrorCodes.TerritoryGuild });

    /// <summary>
    /// 32282: put a member in a slot or clear it (roleGuildId 0). dungeonId is the siege's (enum_DungeonIdMap) and does not
    /// change the guild-wide slots (inferred). The client offers only members without a slot; anyone else is 82 (inferred).
    /// </summary>
    private async Task ToolAssignCoreAsync(GameSession s, GuildWartimeToolBelongOperReq req)
    {
        var g = Own(s);
        long target = req.RoleGuildId;
        int code = g == null ? ErrorCodes.TerritoryGuild : !HasRight(g, s.Role!.Id, MachineRight) ? ErrorCodes.GuildPermission
            : !ValidSlot(req.ToolType, req.BagIndex) ? ErrorCodes.Parameter
            : target != 0 && !g.Members.ContainsKey(target) ? ErrorCodes.GuildMemberMissing
            : target != 0 && target != Holder(g, req.ToolType, req.BagIndex)
                && ToolOf(g, target) != WartimeToolType.None ? ErrorCodes.Parameter : 0;
        var rsp = new GuildWartimeToolBelongOperRsp
        { Result = code, ToolType = req.ToolType, BagIndex = req.BagIndex, RoleGuildId = target };
        long before = code == 0 ? Holder(g!, req.ToolType, req.BagIndex) : 0;
        if (code != 0 || before == target)
        {
            await s.SendGameAsync(ToolAssign, rsp);
            return;
        }
        if (!g!.WartimeSlots.TryGetValue((int)req.ToolType, out var slots)) g.WartimeSlots[(int)req.ToolType] = slots = [];
        if (target == 0) slots.Remove(req.BagIndex); else slots[req.BagIndex] = target;
        await SaveGuildAsync(g);
        await s.SendGameAsync(ToolAssign, rsp);
        await PushToolChangesAsync(g, [(req.ToolType, req.BagIndex)]);
        await PushToolPermissionAsync(g, before);
        await PushToolPermissionAsync(g, target);
    }

    /// <summary>32283: swap two slots of one type (dragging a name onto another slot); an empty slot moves the name.</summary>
    private async Task ToolExchangeCoreAsync(GameSession s, GuildWartimeToolExchangeOperReq req)
    {
        var g = Own(s);
        int code = g == null ? ErrorCodes.TerritoryGuild : !HasRight(g, s.Role!.Id, MachineRight) ? ErrorCodes.GuildPermission
            : !ValidSlot(req.ToolType, req.SrcBagIndex) || !ValidSlot(req.ToolType, req.TargetBagIndex)
                || req.SrcBagIndex == req.TargetBagIndex ? ErrorCodes.Parameter : 0;
        var rsp = new GuildWartimeToolExchangeOperRsp
        { Result = code, ToolType = req.ToolType, SrcBagIndex = req.SrcBagIndex, TargetBagIndex = req.TargetBagIndex };
        long source = code == 0 ? Holder(g!, req.ToolType, req.SrcBagIndex) : 0;
        long destination = code == 0 ? Holder(g!, req.ToolType, req.TargetBagIndex) : 0;
        if (code != 0 || source == destination)
        {
            await s.SendGameAsync(ToolExchange, rsp);
            return;
        }
        if (!g!.WartimeSlots.TryGetValue((int)req.ToolType, out var slots)) g.WartimeSlots[(int)req.ToolType] = slots = [];
        Place(slots, req.SrcBagIndex, destination);
        Place(slots, req.TargetBagIndex, source);
        await SaveGuildAsync(g);
        await s.SendGameAsync(ToolExchange, rsp);
        await PushToolChangesAsync(g, [(req.ToolType, req.SrcBagIndex), (req.ToolType, req.TargetBagIndex)]);
    }

    private static void Place(Dictionary<int, long> slots, int index, long role)
    {
        if (role == 0) slots.Remove(index); else slots[index] = role;
    }

    /// <summary>A member left: every slot they held is freed. Returns the freed slots for <see cref="PushToolChangesAsync"/>.</summary>
    private static List<(WartimeToolType Type, int Index)> DropWartime(GuildStore.State g, long role)
    {
        var freed = new List<(WartimeToolType, int)>();
        foreach (var (type, slots) in g.WartimeSlots)
        {
            int[] held = [.. slots.Where(p => p.Value == role).Select(p => p.Key)];
            foreach (int index in held)
            {
                slots.Remove(index);
                if (ValidSlot((WartimeToolType)type, index)) freed.Add(((WartimeToolType)type, index));
            }
        }
        return freed;
    }

    /// <summary>32286 with the slots' current holders to every online member.</summary>
    private async Task PushToolChangesAsync(GuildStore.State g, List<(WartimeToolType Type, int Index)> changed)
    {
        if (changed.Count == 0) return;
        var ntf = new GuildWartimeToolBelongInfoChangeNtf();
        ntf.InfoList.AddRange(changed.Select(c => new GuildWartimeToolBelongDetailInfo
        {
            ToolType = c.Type, BagIndex = c.Index,
            ToolBelongInfo = new GuildWartimeToolBelongInfo { RoleGuildId = Holder(g, c.Type, c.Index) },
        }));
        foreach (var m in g.Members.Values)
            if (Online(m.RoleId) is { } peer) await peer.SendGameAsync(ToolChange, ntf);
    }

    /// <summary>32285 to an online member with the machine type they may now use (None when revoked).</summary>
    private async Task PushToolPermissionAsync(GuildStore.State g, long role)
    {
        if (role == 0 || Online(role) is not { } peer) return;
        await peer.SendGameAsync(ToolPermission, new GuildWartimeToolRolePermissionNtf { ToolType = ToolOf(g, role) });
    }
}
