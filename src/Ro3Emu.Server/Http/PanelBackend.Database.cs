using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Ro3Emu.Panel;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Data.Config;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Http;

/// <summary>
/// The panel's item and monster databases: built once from the game data on first use (off the request thread) and kept
/// in memory; the spawn index, which reads every map's scene editor, is built on its own the first time a monster page
/// asks. Numbers come from the server's own rules: a monster's HP from MonsterService.TemplateAttributes, its drops
/// from RewardService.KillDropRows and the pools they roll.
/// </summary>
public sealed partial class PanelBackend
{
    private const int DetailRows = 200;
    /// <summary>Items 100 and 101 are the Base and Job EXP a kill grants, not loot.</summary>
    private const long BaseExpItem = 100, JobExpItem = 101;

    /// <summary>The game data, for the catalog (a nested class cannot see the constructor's parameter).</summary>
    private GameData Tables => data;

    private readonly object _databaseGate = new();
    private Task<PanelCatalog>? _catalog;
    private Task<Dictionary<long, List<PanelSpawn>>>? _spawnIndex;

    public async Task<PanelItemPage> FindItemsAsync(PanelItemQuery query)
    {
        var catalog = await CatalogAsync();
        long id = long.TryParse(query.Text, NumberStyles.None, CultureInfo.InvariantCulture, out long n) ? n : -1;
        var found = catalog.Items.Where(i =>
                (query.Text.Length == 0 || i.Id == id || i.Name.Contains(query.Text, StringComparison.OrdinalIgnoreCase))
                && (query.Type == 0 || i.Type == query.Type) && (query.Quality == 0 || i.Quality == query.Quality)
                && (query.MinLevel == 0 || i.Level >= query.MinLevel) && (query.MaxLevel == 0 || i.Level <= query.MaxLevel))
            .ToList();
        return new PanelItemPage(Page(found, query.Page, query.PageSize), found.Count, query.Page, query.PageSize, catalog.Types, catalog.Qualities);
    }

    public async Task<PanelItem?> ItemAsync(long id) => (await CatalogAsync()).Item(id);

    public async Task<PanelMonsterPage> FindMonstersAsync(PanelMonsterQuery query)
    {
        var catalog = await CatalogAsync();
        long id = long.TryParse(query.Text, NumberStyles.None, CultureInfo.InvariantCulture, out long n) ? n : -1;
        var found = catalog.Monsters.Where(m =>
                (query.Text.Length == 0 || m.Summary.Id == id || m.Summary.Name.Contains(query.Text, StringComparison.OrdinalIgnoreCase))
                && (query.Element == 0 || m.Summary.Element == query.Element) && (query.Race == 0 || m.RaceId == query.Race)
                && (query.Size == 0 || m.SizeId == query.Size) && (query.Rank.Length == 0 || m.Summary.Rank == query.Rank)
                && (query.MinLevel == 0 || m.Summary.Level >= query.MinLevel) && (query.MaxLevel == 0 || m.Summary.Level <= query.MaxLevel))
            .Select(m => m.Summary).ToList();
        return new PanelMonsterPage(Page(found, query.Page, query.PageSize), found.Count, query.Page, query.PageSize,
            catalog.Elements, catalog.Races, catalog.Sizes);
    }

    public async Task<PanelMonster?> MonsterAsync(long id)
    {
        var catalog = await CatalogAsync();
        if (!catalog.MonstersById.TryGetValue(id, out var monster)) return null;
        var spawns = (await SpawnIndexAsync()).GetValueOrDefault(id) ?? [];
        return new PanelMonster(monster.Summary, monster.Stats, monster.Speed, monster.Drops, spawns);
    }

    private static List<T> Page<T>(List<T> rows, int page, int size) => [.. rows.Skip((page - 1) * size).Take(size)];

    /// <summary>The catalog, built once; a failed build is tried again by the next request.</summary>
    private Task<PanelCatalog> CatalogAsync()
    {
        lock (_databaseGate)
        {
            if (_catalog == null || _catalog.IsFaulted || _catalog.IsCanceled) _catalog = Task.Run(() => new PanelCatalog(this));
            return _catalog;
        }
    }

    private Task<Dictionary<long, List<PanelSpawn>>> SpawnIndexAsync()
    {
        lock (_databaseGate)
        {
            if (_spawnIndex == null || _spawnIndex.IsFaulted || _spawnIndex.IsCanceled) _spawnIndex = Task.Run(BuildSpawnIndex);
            return _spawnIndex;
        }
    }

    /// <summary>
    /// Where each monster lives: every open-world map's scene-editor spawn groups (WorldData.LoadSpawnGroups, the groups
    /// MonsterService spawns), how many a map keeps alive and the respawn; field MVPs from MVPConfig.
    /// </summary>
    private Dictionary<long, List<PanelSpawn>> BuildSpawnIndex()
    {
        string dir = Path.Combine(options.Value.GameDataDir ?? options.Value.DataDir, "sceneeditor");
        var found = new System.Collections.Concurrent.ConcurrentBag<(long Npc, long Map, int Count, int Respawn)>();
        Parallel.ForEach(MapConfig.All(data).Where(m => m.ResSceneEditorName.Length > 0), map =>
        {
            if (DataFiles.Find(dir, map.ResSceneEditorName + ".json") is not { } path) return;
            using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
            foreach (var group in WorldData.LoadSpawnGroups(doc.RootElement))
            {
                if (group.Pool)
                {
                    foreach (var (npc, _, max) in group.Limits.Where(l => l.Max > 0))
                        found.Add((npc, map.Id, max, group.RespawnSeconds));
                }
                else
                {
                    foreach (var points in group.Points.Where(p => p.ConfigId > 0).GroupBy(p => p.ConfigId))
                        found.Add((points.Key, map.Id, Math.Min(points.Count(), Math.Max(1, group.Total)), group.RespawnSeconds));
                }
            }
        });
        foreach (var mvp in MVPConfig.All(data).Where(m => m.NPCId > 0 && m.MapId > 0))
            found.Add((mvp.NPCId, mvp.MapId, 1, (int)mvp.RefreshTime));
        return found.GroupBy(f => f.Npc).ToDictionary(g => g.Key, g => g.GroupBy(f => f.Map)
            .Select(m => new PanelSpawn(m.Key, MapName(m.Key), m.Sum(f => f.Count), m.Min(f => f.Respawn)))
            .OrderByDescending(s => s.Count).ThenBy(s => s.Map, StringComparer.OrdinalIgnoreCase).ToList());
    }

    // ---------------------------------------------------------------- names the client gives its enums

    /// <summary>
    /// The client's own English for its enums: Localization_en 10100000010.. holds the quality colours, then races,
    /// elements, sizes and weapon kinds in their id order (TypeConfig names them by these ids). A data set without the
    /// texts falls back to the same English.
    /// </summary>
    private string EnumText(long textId, string fallback) => data.Text(textId) is { Length: > 0 } text ? text : fallback;

    private static readonly string[] QualityNames = ["White", "Green", "Blue", "Purple", "Orange", "Red"];
    private static readonly string[] RaceNames = ["Brute", "Demi-Human", "Demon", "Plant", "Undead", "Formless", "Fish", "Angel", "Insect", "Dragon"];
    private static readonly string[] ElementNames = ["Neutral", "Wind", "Earth", "Fire", "Water", "Poison", "Holy", "Shadow", "Ghost", "Undead"];
    private static readonly string[] SizeNames = ["Small", "Medium", "Large"];
    private static readonly string[] WeaponNames = ["Dagger", "One-Handed Sword", "Two-Handed Sword", "One-Handed Spear", "Two-Handed Spear",
        "One-Handed Axe", "Two-Handed Axe", "Mace", "Wand", "Staff", "Bow", "Katar"];

    private string Named(string[] names, long firstText, long value) =>
        value >= 1 && value <= names.Length ? EnumText(firstText + value - 1, names[value - 1]) : "";

    private string QualityName(long quality) => Named(QualityNames, 10100000010, quality);
    private string RaceName(long race) => Named(RaceNames, 10100000022, race);
    private string ElementName(long element) => Named(ElementNames, 10100000032, element);
    private string SizeName(long size) => Named(SizeNames, 10100000042, size);

    /// <summary>
    /// ItemConfig _iType as the bag sorts it (inferred from the items of each type; the client's names where it has
    /// them: Currency 10100000062, Consumable ..080, Item Pack ..098, Card ..007, Equip ..006).
    /// </summary>
    private string TypeName(long type) => type switch
    {
        1 => EnumText(10100000062, "Currency"),
        2 => EnumText(10100000080, "Consumable"),
        3 => EnumText(10100000098, "Item Pack"),
        4 => EnumText(10100000007, "Card"),
        5 => EnumText(10100000006, "Equip"),
        7 => "Mount",
        8 => "Treasure",
        9 => "Material",
        14 => "Vehicle Part",
        _ => "Other",
    };

    /// <summary>An equipment's kind: weapons 1-12, then shield 50, clothes 55, muffler 60, shoes 65, accessory 70.</summary>
    private string SubTypeName(long type, long subType) => type != 5 ? "" : subType switch
    {
        >= 1 and <= 12 => Named(WeaponNames, 10100000045, subType),
        50 => EnumText(10100000057, "Shield"),
        55 => EnumText(10100000058, "Clothes"),
        60 => EnumText(10100000059, "Muffler"),
        65 => EnumText(10100000060, "Shoes"),
        70 => EnumText(10100000061, "Accessory"),
        _ => "",
    };

    /// <summary>
    /// NPCConfig _iNPCSubType: 3 mini boss, 4 field MVP (MVPConfig lists them), 6 boss of a dungeon or story; the
    /// server treats 3, 4 and 6 as bosses (TitleService, ParagonService). The words are (inferred).
    /// </summary>
    private static string Rank(long subType, bool mvp) => mvp || subType == 4 ? "mvp" : subType switch { 3 => "mini", 6 => "boss", _ => "normal" };

    /// <summary>A name field [text id, param...] read through a view: the text with @{n} replaced by the n-th value.</summary>
    private string Name(long[] text)
    {
        if (text.Length == 0) return "";
        string name = data.Text(text[0]);
        for (int i = 1; i < text.Length; i++) name = name.Replace("@{" + i.ToString(CultureInfo.InvariantCulture) + "}", text[i].ToString(CultureInfo.InvariantCulture));
        return name;
    }

    /// <summary>The colour marks the client draws in a description (^{n}, &lt;color=...&gt;) are left out.</summary>
    [GeneratedRegex(@"\^\{\d+\}|</?color[^>]*>")]
    private static partial Regex ColourMark();

    /// <summary>(inferred) The developers' own monster rows ("Test Death", "3D Bounding Box Test", "Magic Circle_Empty",
    /// "Blank NPC"): no player meets them, so the database leaves them out, as it does rows of level 0.</summary>
    [GeneratedRegex(@"\btest\b|_|\bblank npc\b", RegexOptions.IgnoreCase)]
    private static partial Regex DeveloperRow();

    /// <summary>An attribute's value as the client writes it (Lua_CFG_AttributeConfig): data type 1 a percentage of
    /// value/100, else a whole number; data type 2 (attack speed) as value/100 is (inferred).</summary>
    private string AttributeValue(long attribute, double value)
    {
        long type = AttributeConfig.Get(data, attribute)?.DataType ?? 0;
        return type switch
        {
            1 => (value / 100).ToString("0.##", CultureInfo.InvariantCulture) + "%",
            2 => (value / 100).ToString("0.##", CultureInfo.InvariantCulture),
            _ => Math.Round(value).ToString("N0", CultureInfo.InvariantCulture),
        };
    }

    private static readonly Dictionary<long, string> AttributeFallback = new()
    {
        [101] = "HP", [102] = "SP", [103] = "PATK", [104] = "MATK", [105] = "PDEF", [106] = "MDEF", [109] = "HIT", [110] = "FLEE",
        [111] = "CRIT", [113] = "MSPD",
    };

    private string AttributeName(long attribute) =>
        AttributeConfig.Get(data, attribute) is { } row && Name(row.Name) is { Length: > 0 } name ? name
        : AttributeFallback.GetValueOrDefault(attribute) ?? "Attribute " + attribute.ToString(CultureInfo.InvariantCulture);

    // ---------------------------------------------------------------- the catalog

    private sealed record MonsterEntry(PanelMonsterSummary Summary, long RaceId, long SizeId, IReadOnlyList<PanelStat> Stats, long Speed,
        IReadOnlyList<PanelMonsterDrop> Drops);

    private sealed record ItemEntry(PanelItemSummary Summary, ItemConfig Row);

    /// <summary>Everything the two databases show, from the game data, in memory.</summary>
    private sealed class PanelCatalog
    {
        private readonly PanelBackend _backend;
        private readonly Dictionary<long, ItemEntry> _items = [];
        private readonly Dictionary<long, List<PanelItemDrop>> _droppedBy = [];
        private readonly Dictionary<long, List<PanelShopOffer>> _shops = [];
        private readonly Dictionary<long, JsonElement> _cards = [];

        public List<PanelItemSummary> Items { get; }
        public List<MonsterEntry> Monsters { get; } = [];
        public Dictionary<long, MonsterEntry> MonstersById { get; } = [];
        public IReadOnlyList<PanelOption> Types { get; }
        public IReadOnlyList<PanelOption> Qualities { get; }
        public IReadOnlyList<PanelOption> Elements { get; }
        public IReadOnlyList<PanelOption> Races { get; }
        public IReadOnlyList<PanelOption> Sizes { get; }

        public PanelCatalog(PanelBackend backend)
        {
            _backend = backend;
            var data = backend.Tables;
            foreach (var row in ItemConfig.All(data))
            {
                string name = backend.Name(row.Name);
                if (name.Length == 0) continue; // a row without a name is not shown in the game either
                _items[row.Id] = new ItemEntry(new PanelItemSummary(row.Id, name, (int)row.Type, backend.TypeName(row.Type),
                    backend.SubTypeName(row.Type, row.SubType), (int)row.Quality, backend.QualityName(row.Quality), (int)row.LevelNeed), row);
            }
            Items = [.. _items.Values.Select(i => i.Summary).OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase).ThenBy(i => i.Id)];
            Types = [.. _items.Values.Select(i => (long)i.Summary.Type).Distinct().Order().Select(t => new PanelOption(t, backend.TypeName(t)))];
            Qualities = [.. Enumerable.Range(1, QualityNames.Length).Select(q => new PanelOption(q, backend.QualityName(q)))];
            Elements = [.. Enumerable.Range(1, ElementNames.Length).Select(e => new PanelOption(e, backend.ElementName(e)))];
            Races = [.. Enumerable.Range(1, RaceNames.Length).Select(r => new PanelOption(r, backend.RaceName(r)))];
            Sizes = [.. Enumerable.Range(1, SizeNames.Length).Select(s => new PanelOption(s, backend.SizeName(s)))];

            foreach (var card in CardConfig.All(data).OrderBy(c => c.Level))
                _cards.TryAdd(card.CardId, card.Row);
            ReadShops(data);

            var mvps = MVPConfig.All(data).Select(m => m.NPCId).ToHashSet();
            foreach (var npc in NPCConfig.All(data).Where(n => n.NPCType == 1))
            {
                string name = backend.Name(npc.Name);
                if (name.Length == 0 || npc.Level <= 0 || DeveloperRow().IsMatch(name)) continue;
                var monster = Monster(npc, name, mvps.Contains(npc.Id));
                Monsters.Add(monster);
                MonstersById[npc.Id] = monster;
                foreach (var drop in monster.Drops.Where(d => d.ItemId is not (BaseExpItem or JobExpItem)))
                {
                    if (!_droppedBy.TryGetValue(drop.ItemId, out var list)) _droppedBy[drop.ItemId] = list = [];
                    list.Add(new PanelItemDrop(npc.Id, name, monster.Summary.Level, monster.Summary.Rank, drop.Chance, drop.Min, drop.Max));
                }
            }
            Monsters.Sort((a, b) => a.Summary.Level != b.Summary.Level ? a.Summary.Level.CompareTo(b.Summary.Level)
                : string.Compare(a.Summary.Name, b.Summary.Name, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>Shop goods from ShopCommodityConfig and the shop of their _iClassID, except Survivor Battle's match-only
        /// shops (type 8); a fashion row without an item is a look, not an item.</summary>
        private void ReadShops(GameData data)
        {
            var shops = ShopConfig.All(data).GroupBy(s => s.ClassId).ToDictionary(g => g.Key, g => g.First());
            foreach (var goods in ShopCommodityConfig.All(data).Where(c => c.ItemId > 0))
            {
                if (!shops.TryGetValue(goods.ClassId, out var shop) || shop.ShopType == 8) continue;
                long currency = goods.ItemPerPrice.Length > 0 ? goods.ItemPerPrice[0] : 0, price = goods.ItemPerPrice.Length > 1 ? goods.ItemPerPrice[1] : 0;
                if (!_shops.TryGetValue(goods.ItemId, out var list)) _shops[goods.ItemId] = list = [];
                var offer = new PanelShopOffer(_backend.Name(shop.ShopName) is { Length: > 0 } n ? n : "Shop " + shop.ClassId.ToString(CultureInfo.InvariantCulture),
                    currency, currency > 0 ? data.ItemName(currency) : "", price, (int)Math.Max(1, goods.ItemPerMount));
                if (!list.Contains(offer)) list.Add(offer);
            }
        }

        private MonsterEntry Monster(NPCConfig npc, string name, bool mvp)
        {
            var data = _backend.Tables;
            int level = (int)npc.Level;
            var attr = MonsterService.TemplateAttributes(data, npc.Row, level);
            long A(long id) => (long)Math.Round(attr.GetValueOrDefault(id));
            long speed = attr.ContainsKey(113) ? A(113) : npc.Speed;
            var summary = new PanelMonsterSummary(npc.Id, name, level, Math.Max(1, A(101)), (int)npc.Element, _backend.ElementName(npc.Element),
                _backend.RaceName(npc.Race), _backend.SizeName(npc.Size), Rank(npc.NPCSubType, mvp));
            // HIT and FLEE default to level + 1 and level as MonsterService.Build reads them.
            var stats = new List<PanelStat>();
            foreach (long id in new long[] { 101, 102, 103, 104, 105, 106, 109, 110, 111 })
            {
                double value = id switch
                {
                    109 when !attr.ContainsKey(109) => level + 1,
                    110 when !attr.ContainsKey(110) => level,
                    101 => Math.Max(1, A(101)),
                    _ => attr.GetValueOrDefault(id),
                };
                if (id is 101 or 103 or 104 or 105 or 106 or 109 or 110 || value != 0)
                    stats.Add(new PanelStat(_backend.AttributeName(id), _backend.AttributeValue(id, value)));
            }
            return new MonsterEntry(summary, npc.Race, npc.Size, stats, speed, Drops(npc));
        }

        /// <summary>
        /// A kill's loot as RewardService rolls it (bonus time off): every pool slot [group, chance per million, rolls,
        /// picks] of each drop row, each DropGroupConfig row weighted among its group's candidates. The chance shown is
        /// that at least one drops per kill; the player's level and class limits of a group row are not applied
        /// (inferred: the page has no player).
        /// </summary>
        private List<PanelMonsterDrop> Drops(NPCConfig npc)
        {
            var data = _backend.Tables;
            var merged = new Dictionary<(long Item, string Maps), (double Miss, int Min, int Max, IReadOnlyList<long> Maps)>();
            foreach (var (row, maps) in RewardService.KillDropRows(data, npc.Row, bonus: false))
            {
                foreach (long poolId in RewardService.DropPools(row))
                {
                    if (data.Row("DropPoolConfig", poolId) is not { } pool) continue;
                    foreach (var (group, chance, rolls, picks) in RewardService.PoolSlots(pool))
                    {
                        var candidates = DropGroupConfig.Get(data, group).Where(r => r.Weights > 0 && r.QuestLimit == 0 && r.Activityid == 0).ToList();
                        double total = candidates.Sum(r => (double)r.Weights);
                        if (total <= 0) continue;
                        double hit = Math.Clamp(chance / 1_000_000, 0, 1);
                        foreach (var candidate in candidates)
                        {
                            long item = candidate.DropItem;
                            if (!_items.ContainsKey(item)) continue;
                            double share = candidate.Weights / total;
                            double missRoll = 1 - hit + hit * Math.Pow(1 - share, picks);
                            double miss = Math.Pow(missRoll, rolls);
                            var key = (item, string.Join(',', maps));
                            int min = (int)Math.Max(1, candidate.MinNumber), max = (int)Math.Max(min, candidate.MaxNumber);
                            merged[key] = merged.TryGetValue(key, out var seen)
                                ? (seen.Miss * miss, Math.Min(seen.Min, min), Math.Max(seen.Max, max), maps)
                                : (miss, min, max, maps);
                        }
                    }
                }
            }
            return [.. merged.Select(m => new PanelMonsterDrop(m.Key.Item, _items[m.Key.Item].Summary.Name, _items[m.Key.Item].Summary.Quality,
                    1 - m.Value.Miss, m.Value.Min, m.Value.Max, [.. m.Value.Maps.Select(_backend.MapName)]))
                .OrderByDescending(d => d.Chance).ThenBy(d => d.Item, StringComparer.OrdinalIgnoreCase).Take(DetailRows)];
        }

        public PanelItem? Item(long id)
        {
            if (!_items.TryGetValue(id, out var entry)) return null;
            var row = entry.Row;
            var data = _backend.Tables;
            string description = ColourMark().Replace(data.TextOf(row.Description), "").Trim();
            var classes = row.JobNeed.Where(j => j > 0).Select(j => _backend.ClassName(j)).ToList();
            string trade = row.Trade switch { 1 => "Exchange", 2 => "Stalls", _ => "Not tradeable" };
            var dropped = (_droppedBy.GetValueOrDefault(id) ?? []).OrderByDescending(d => d.Chance).ThenBy(d => d.Level).Take(DetailRows).ToList();
            return new PanelItem(entry.Summary, description, (int)row.StackLimit, trade, row.IsUse == 1, classes, Stats(row), dropped,
                _shops.GetValueOrDefault(id) ?? []);
        }

        /// <summary>Equipment: EquipConfig _kBasicAttribute [attribute, min, max]; a card: the lowest level's
        /// CardConfig _kBasicAttribute [attribute, value].</summary>
        private List<PanelStat> Stats(ItemConfig item)
        {
            var data = _backend.Tables;
            var stats = new List<PanelStat>();
            if (item.EquipId > 0 && EquipConfig.Get(data, item.EquipId) is { } equip)
            {
                foreach (var a in equip.BasicAttribute.Where(a => a.Length >= 2))
                {
                    string low = _backend.AttributeValue(a[0], a[1]), high = a.Length > 2 && a[2] != a[1] ? _backend.AttributeValue(a[0], a[2]) : "";
                    stats.Add(new PanelStat(_backend.AttributeName(a[0]), "+" + low + (high.Length > 0 ? "-" + high : "")));
                }
            }
            if (_cards.TryGetValue(item.Id, out var cardRow))
                foreach (var a in new CardConfig(cardRow).BasicAttribute.Where(a => a.Length >= 2))
                    stats.Add(new PanelStat(_backend.AttributeName(a[0]), "+" + _backend.AttributeValue(a[0], a[1])));
            return stats;
        }
    }
}
