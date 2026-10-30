namespace Ro3Emu.Panel;

/// <summary>
/// The public item and monster databases (from the server's game data, built once and kept in memory) and the admin's
/// economy log (the server's economy_entry ledger, read on the read pool).
/// </summary>
public partial interface IPanelBackend
{
    /// <summary>Items matching the query, by name then id, one page; the filter choices come with it.</summary>
    Task<PanelItemPage> FindItemsAsync(PanelItemQuery query);

    /// <summary>One item with its stats, the monsters that drop it and the shops that sell it; null when unknown.</summary>
    Task<PanelItem?> ItemAsync(long id);

    /// <summary>Monsters matching the query, by level then name, one page; the filter choices come with it.</summary>
    Task<PanelMonsterPage> FindMonstersAsync(PanelMonsterQuery query);

    /// <summary>One monster with its stats, drops and spawns; null when unknown.</summary>
    Task<PanelMonster?> MonsterAsync(long id);

    /// <summary>A page of the economy ledger, newest first, with the totals of everything the filter matches.</summary>
    Task<PanelEconomyPage> EconomyAsync(PanelEconomyQuery query);
}

/// <summary>A choice of a filter: its value in the URL and its name.</summary>
public sealed record PanelOption(long Id, string Name);

/// <param name="Text">Part of the name, or an exact id.</param>
/// <param name="Type">ItemConfig _iType; 0 = any.</param>
/// <param name="Quality">1 (white) to 6 (red); 0 = any.</param>
/// <param name="MinLevel">Required level from; 0 = any.</param>
/// <param name="MaxLevel">Required level to; 0 = any.</param>
public sealed record PanelItemQuery(string Text, int Type, int Quality, int MinLevel, int MaxLevel, int Page, int PageSize);

/// <param name="Quality">1 (white) to 6 (red), as the game colours an item's name.</param>
public sealed record PanelItemSummary(long Id, string Name, int Type, string TypeName, string SubType, int Quality, string QualityName, int Level);

public sealed record PanelItemPage(IReadOnlyList<PanelItemSummary> Items, int Total, int Page, int PageSize,
    IReadOnlyList<PanelOption> Types, IReadOnlyList<PanelOption> Qualities);

/// <summary>One readable line of an item's or a monster's numbers: "PATK", "+420-570".</summary>
public sealed record PanelStat(string Name, string Value);

/// <param name="Trade">How it changes hands: "Exchange", "Stalls" or "Not tradeable".</param>
/// <param name="Classes">The classes that may use it; empty = every class.</param>
public sealed record PanelItem(PanelItemSummary Item, string Description, int StackLimit, string Trade, bool Usable,
    IReadOnlyList<string> Classes, IReadOnlyList<PanelStat> Stats, IReadOnlyList<PanelItemDrop> DroppedBy, IReadOnlyList<PanelShopOffer> Shops);

/// <param name="Chance">The chance per kill that at least one drops, 0 to 1.</param>
/// <param name="Kind">Empty for a kill's loot; an MVP reward's kind ("First strike", "Final blow", "Top 10 parties", "Participation",
/// "Rare winner").</param>
public sealed record PanelItemDrop(long MonsterId, string Monster, int Level, string Rank, double Chance, int Min, int Max, string Kind);

/// <param name="Count">Items per purchase.</param>
public sealed record PanelShopOffer(string Shop, long CurrencyId, string Currency, long Price, int Count);

/// <param name="Rank">"normal", "mini", "boss" or "mvp"; empty = any.</param>
public sealed record PanelMonsterQuery(string Text, int Element, int Race, int Size, string Rank, int MinLevel, int MaxLevel, int Page, int PageSize);

/// <param name="Rank">"normal", "mini", "boss" or "mvp".</param>
public sealed record PanelMonsterSummary(long Id, string Name, int Level, long Hp, int Element, string ElementName, string Race,
    string Size, string Rank);

public sealed record PanelMonsterPage(IReadOnlyList<PanelMonsterSummary> Monsters, int Total, int Page, int PageSize,
    IReadOnlyList<PanelOption> Elements, IReadOnlyList<PanelOption> Races, IReadOnlyList<PanelOption> Sizes);

/// <param name="Speed">Move speed (attribute 113, else NPCConfig _iSpeed).</param>
public sealed record PanelMonster(PanelMonsterSummary Monster, IReadOnlyList<PanelStat> Stats, long Speed,
    IReadOnlyList<PanelMonsterDrop> Drops, IReadOnlyList<PanelSpawn> Spawns);

/// <param name="Chance">The chance per kill that at least one drops, 0 to 1.</param>
/// <param name="Maps">The maps the row is limited to; empty = everywhere.</param>
/// <param name="Kind">Empty for a kill's loot; an MVP reward's kind, as on <see cref="PanelItemDrop"/>.</param>
public sealed record PanelMonsterDrop(long ItemId, string Item, int Quality, double Chance, int Min, int Max, IReadOnlyList<string> Maps,
    string Kind);

/// <param name="Count">How many the map keeps alive at most.</param>
/// <param name="RespawnSeconds">Seconds from a corpse's removal to the next spawn.</param>
public sealed record PanelSpawn(long MapId, string Map, int Count, int RespawnSeconds);

/// <param name="Asset">An item or currency id; 0 = any.</param>
/// <param name="AssetText">An item or currency name instead of an id (any letter case); empty = none.</param>
/// <param name="From">Recorded from this instant on.</param>
/// <param name="To">Recorded before this instant.</param>
public sealed record PanelEconomyQuery(string Account, string Character, long Asset, string AssetText, DateTimeOffset? From,
    DateTimeOffset? To, int Page, int PageSize);

/// <param name="Delta">What the owner gained (positive) or gave up (negative).</param>
/// <param name="Owner">Who holds the asset: a character's name, a guild, the system.</param>
public sealed record PanelEconomyEntry(long Id, DateTimeOffset Utc, string Account, string Owner, string Command, string Reason,
    string Leg, long AssetId, string Asset, long Delta, string Place, string Counterparty, string Operation);

/// <param name="In">The sum of the positive deltas the filter matches.</param>
/// <param name="Out">The sum of the negative deltas (a negative number).</param>
/// <param name="Assets">The assets named by the asset filter (empty: every asset).</param>
public sealed record PanelEconomyPage(IReadOnlyList<PanelEconomyEntry> Entries, int Total, long In, long Out,
    IReadOnlyList<PanelOption> Assets, int Page, int PageSize);
