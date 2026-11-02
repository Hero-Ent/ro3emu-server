using Romsg;

namespace Ro3Emu.Server.Game;

/// <summary>
/// What the GM's guild-war simulation (GvgSimService, "!gvg", a test aid, not original) reads of the guilds and the war
/// warehouse it fills. Reads are made on the game loop; the stock change is an ordinary guild operation.
/// </summary>
public sealed partial class GuildService
{
    /// <summary>The guild a role belongs to, or 0.</summary>
    internal long GuildOf(long role) => store.ForRole(role)?.Id ?? 0;

    /// <summary>A guild's name, member count, member cap and auto-accept; null when there is no such guild.</summary>
    internal (string Name, int Members, long Max, bool AutoAccept)? GuildFacts(long guildId) =>
        store.Get(guildId) is { } g ? (g.Name, g.Members.Count, MaxMembers(g), g.AutoAccept) : null;

    /// <summary>The roles waiting in a guild's application list.</summary>
    internal long[] Applicants(long guildId) => store.Get(guildId) is { } g ? [.. g.Applications.Keys] : [];

    /// <summary>
    /// Raises the war warehouse of a guild to at least <paramref name="stock"/> (machine item to count): cannons,
    /// vehicles and repair kits for a simulated siege. The warehouse becomes the guild's own stock (SiegeInitItemList
    /// first, as the first reservation would) and the online members get the new 32273.
    /// </summary>
    internal Task TopUpWartimeStockAsync(long guildId, IReadOnlyDictionary<int, int> stock) =>
        RunGuildAsync(null, [guildId], [], async () =>
        {
            if (store.Get(guildId) is not { } g) return;
            bool changed = !g.WartimeStocked;
            Stock(g);
            foreach (var (item, count) in stock)
                if (g.WartimeItems.GetValueOrDefault(item) < count)
                {
                    g.WartimeItems[item] = count;
                    changed = true;
                }
            if (!changed) return;
            await SaveGuildAsync(g);
            await PushStoreAsync(g);
        });

    /// <summary>
    /// Puts a member in a war machine slot of <paramref name="type"/>, as the guild's Tactics panel does (32282) but
    /// without the leader: <paramref name="bag"/>, or the last free one when 0 (the simulated war's fake gunners and
    /// drivers hold the first eight of each type), or the last one when all are taken. The slot it held of the other type
    /// is cleared, and the member and the slot's former holder lose their machines (as the panel does). Returns the slot
    /// given (the one it holds already when it has one of that type), 0 when the role is not a member.
    /// </summary>
    internal async Task<int> GiveWarSlotAsync(long guildId, long role, WartimeToolType type, int bag = 0)
    {
        int given = 0;
        await RunGuildAsync(null, [guildId], [], async () =>
        {
            if (store.Get(guildId) is not { } g || !g.Members.ContainsKey(role)) return;
            List<(WartimeToolType Type, int Index)> held = [.. SlotsOf(g, role)];
            int current = held.Where(h => h.Type == type).Select(h => h.Index).FirstOrDefault();
            if (current > 0)
            {
                given = current;
                return;
            }
            int count = SlotCount(type);
            if (bag < 1 || bag > count)
            {
                bag = count;
                for (int i = count; i >= 1; i--)
                    if (Holder(g, type, i) == 0)
                    {
                        bag = i;
                        break;
                    }
            }
            long before = Holder(g, type, bag);
            foreach (var (t, i) in held) g.WartimeSlots[(int)t].Remove(i);
            bool stock = await TakeMachinesAsync(g, role);
            if (before != 0) stock |= await TakeMachinesAsync(g, before);
            if (!g.WartimeSlots.TryGetValue((int)type, out var slots)) g.WartimeSlots[(int)type] = slots = [];
            slots[bag] = role;
            await SaveGuildAsync(g);
            await PushToolChangesAsync(g, [.. held, (type, bag)]);
            await PushToolPermissionAsync(g, role);
            await PushToolPermissionAsync(g, before);
            if (stock) await PushStoreAsync(g);
            given = bag;
        });
        return given;
    }
}
