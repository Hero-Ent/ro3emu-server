using System.Globalization;
using Romsg;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Data.Config;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Tests;

/// <summary>
/// The ways a player is sent into a story dungeon by 30400 Enter: a quest's progress target names the dungeon
/// (TaskConfig _kProgressTarget), or a map region runs action 613 EnterDungeon_Sever ("613|scene|...", see
/// DungeonService.EnterFromRegionAsync). Each of them must resolve, or the client shows 3720 "Dungeon data not found".
/// </summary>
public sealed partial class DungeonTests
{
    [ClientDataFact]
    public void EveryDungeonAQuestOrARegionSendsThePlayerIntoResolves()
    {
        var data = Service<GameData>();
        var tasks = data.Table("TaskConfig");
        // A task is reachable when every task its _kPreTask chain names exists: the older Novice route starts after
        // 999999, which is no task, so its dungeons (101000's 1010001, 101010's 1010101) are never offered.
        var reachable = new Dictionary<string, bool>();
        bool Reachable(string id, int depth = 0)
        {
            if (reachable.TryGetValue(id, out bool known)) return known;
            bool yes = depth < 64 && tasks.TryGetValue(id, out var row) && new TaskConfig(row).PreTask
                .All(p => Reachable(p.ToString(CultureInfo.InvariantCulture), depth + 1));
            return reachable[id] = yes;
        }
        var entrances = new Dictionary<long, string>();
        foreach (var (id, row) in tasks)
            if (Reachable(id))
                foreach (var target in new TaskConfig(row).ProgressTarget)
                    foreach (long scene in target.Where(v => DungeonConfig.Get(data, v) != null))
                        entrances.TryAdd(scene, $"task {id}");
        foreach (var map in data.Table("MapConfig").Keys)
            if (Service<WorldData>().Map(long.Parse(map, CultureInfo.InvariantCulture)) is { } world)
                foreach (var region in world.Regions.Values)
                {
                    var parts = region.Actions.Split('|');
                    if (parts.Length >= 2 && parts[0] == DungeonService.RegionEnterDungeonAction.ToString(CultureInfo.InvariantCulture)
                        && long.TryParse(parts[1], CultureInfo.InvariantCulture, out long scene))
                        entrances.TryAdd(scene, $"map {map} region {region.Id}");
                }
        Assert.NotEmpty(entrances);
        var missing = entrances.Where(e => Service<DungeonCatalog>().Resolve(new EnterDungeonReq { SceneEntry = e.Key }) == null)
            .Select(e => $"{e.Key} ({e.Value})");
        Assert.Empty(missing);
    }
}
