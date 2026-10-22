using System.Globalization;
using Microsoft.Extensions.Options;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

/// <summary>
/// Development aid, not original content: training dummies standing on public maps (ServerOptions.FieldDummies, empty by
/// default; appsettings.Development.json puts one at Prontera South Gate) to try skills on without hunting. The dummy is
/// the Training Ground's NPC 3500000 as TrainingService spawns it: stationary, never retaliates, and its HP is restored
/// inside every hit's settlement (EventDamageRecorded runs before the lethality check), so no damage path can kill it.
/// Every few seconds a missing dummy is spawned again and a damaged one healed, as a backstop.
/// </summary>
public sealed class FieldDummyService : IGameTicker
{
    public const int DummyNpc = 3500000;
    private const int CheckMs = 5000;

    private readonly MonsterService monsters;
    private readonly World world;
    private readonly GameLoop loop;
    private readonly ILogger<FieldDummyService> log;
    private readonly List<(long Scene, float X, float Z)> spots = new();
    private readonly Dictionary<int, MonsterUnit> live = new();
    private readonly HashSet<int> spawning = new();
    private long nextCheckMs;

    public FieldDummyService(IOptions<ServerOptions> options, MonsterService monsters, World world, CombatService combat,
        GameLoop loop, ILogger<FieldDummyService> log)
    {
        this.monsters = monsters; this.world = world; this.loop = loop; this.log = log;
        foreach (string entry in options.Value.FieldDummies)
        {
            var parts = entry.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 3 && long.TryParse(parts[0], out long scene)
                && float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float x)
                && float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float z))
                spots.Add((scene, x, z));
            else log.LogWarning("Ro3Emu:FieldDummies entry '{Entry}' is not '<sceneEntry> <x> <z>'", entry);
        }
        combat.EventDamageRecorded.Add((_, m, _) =>
        {
            if (IsDummy(m)) { m.Hp = m.Template.MaxHp; m.Target = null; }
            return Task.CompletedTask;
        });
    }

    public bool IsDummy(MonsterUnit m) => live.ContainsValue(m);

    public void Tick(GameTime time)
    {
        long now = MonsterService.NowMs;
        if (spots.Count == 0 || now < nextCheckMs) return;
        nextCheckMs = now + CheckMs;
        for (int i = 0; i < spots.Count; i++)
        {
            if (live.TryGetValue(i, out var dummy) && !dummy.Dead && world.Map(dummy.WorldScene).Units.Contains(dummy))
            {
                if (dummy.Hp < dummy.Template.MaxHp)
                {
                    dummy.Hp = dummy.Template.MaxHp;
                    loop.Post(() => world.ToUnitViewersAsync(dummy, CombatService.AoiAttrChange, MonsterService.Hp(dummy)));
                }
                continue;
            }
            if (!spawning.Add(i)) continue;
            int index = i;
            loop.Post(async () =>
            {
                try { await SpawnAsync(index); }
                finally { spawning.Remove(index); }
            });
        }
    }

    private async Task SpawnAsync(int index)
    {
        var (scene, x, z) = spots[index];
        if (monsters.Template(DummyNpc) is not { } template) { log.LogWarning("No NPC {Npc} for field dummies", DummyNpc); return; }
        // TrainingService's dummy: speed 0 and no attack range, so Engage ignores it and it never moves or swings.
        template = template with { SpeedCm = 0, AttackRange = 0, Aggressive = false, RoamDistance = 0 };
        var dummy = await monsters.SpawnSingleAsync(scene, DummyNpc, new MapPoint(x, 0, z, 0), customTemplate: template);
        if (dummy == null) { log.LogWarning("Field dummy at {Scene} {X} {Z} could not spawn", scene, x, z); return; }
        live[index] = dummy;
        log.LogInformation("Field dummy {Uid} at {Scene} {X} {Z}", dummy.Uid, scene, x, z);
    }
}
