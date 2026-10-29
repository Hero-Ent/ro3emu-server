using Romsg;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Data;

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
