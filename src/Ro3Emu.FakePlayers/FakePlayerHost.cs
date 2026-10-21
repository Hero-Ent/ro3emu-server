using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Ro3Emu.Server;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.FakePlayers;

/// <param name="ServerUrl">The server's HTTP base, e.g. http://127.0.0.1:5380.</param>
/// <param name="DataDir">The game data folder (config/, nav/, sceneeditor/), e.g. Client\RO3Emu\data.</param>
/// <param name="Prefix">Account name prefix; player n logs in as prefix + n (5 digits).</param>
/// <param name="Password">Password of every fake account (the server's FakePlayerService registers them).</param>
public sealed record FakeHostOptions(string ServerUrl, string DataDir, string Prefix, string Password)
{
    /// <summary>Host dialled for the gates instead of the announced one (loopback avoids the per-IP limits).</summary>
    public string? GateHost { get; init; } = "127.0.0.1";

    /// <summary>Logins in flight at once; the rest wait their turn (each login is a password hash on the server).</summary>
    public int LoginConcurrency { get; init; } = 12;

    /// <summary>Chat pace multiplier: 1 = people (a line every few minutes), higher = more often (tests).</summary>
    public double ChatSpeed { get; init; } = 1;

    /// <summary>
    /// Players each fake player asks to see (SceneLoadedReq.viewMaxCount). Low on purpose: the server fans every step out
    /// to everyone who sees the walker, so 1000 fake players seeing 149 each saturated the game loop. Real players still
    /// see the fake players; monsters are not limited by it.
    /// </summary>
    public int ViewCount { get; init; } = 20;
}

/// <summary>A castable skill row: row id, group, aim (_iRelease 1 unit, 2 self, 7/8 ground), reach, cast and cooldown.</summary>
public sealed record FakeSkill(int RowId, int Group, int Release, float Range, int ChantMs, int CooldownMs, bool Normal, bool Buff = false)
{
    /// <summary>Hurts a monster (aimed, on the ground or around the caster).</summary>
    public bool Attack => !Normal && !Buff;
}

/// <summary>
/// Keeps the requested number of fake players online: starts the missing ones (lowest free index first), stops the
/// extra ones (highest index first) and reports counters for the GM command and for stress tests.
/// </summary>
public sealed class FakePlayerHost : IAsyncDisposable
{
    private readonly Action<string> _output;
    private readonly GameData _data;
    private readonly WorldData _world;
    private readonly ConcurrentDictionary<long, Lazy<MapNav?>> _navs = new();
    private readonly Dictionary<int, (FakePlayer Player, Task Run, CancellationTokenSource Stop)> _bots = new();
    private readonly List<Task> _stopping = new();
    private readonly object _sync = new();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _manager;
    private volatile int _target;
    private long _deaths, _revives, _attacks, _skillCasts, _rests, _chats, _failures, _logs;

    public FakeHostOptions Options { get; }
    public string Pem { get; }
    public SemaphoreSlim LoginGate { get; }

    private FakePlayerHost(FakeHostOptions options, string pem, Action<string> output)
    {
        Options = options;
        Pem = pem;
        _output = output;
        LoginGate = new SemaphoreSlim(Math.Max(1, options.LoginConcurrency));
        var gameOptions = Microsoft.Extensions.Options.Options.Create(new ServerOptions { GameDataDir = options.DataDir, DataDir = options.DataDir });
        _data = new GameData(gameOptions, NullLogger<GameData>.Instance);
        _world = new WorldData(gameOptions, _data, NullLogger<WorldData>.Instance);
        _manager = Task.Run(ManageAsync);
    }

    public static async Task<FakePlayerHost> CreateAsync(FakeHostOptions options, Action<string> output, CancellationToken ct = default)
    {
        using var http = new HttpClient { BaseAddress = new Uri(options.ServerUrl), Timeout = TimeSpan.FromSeconds(15) };
        string pem = await http.GetStringAsync("/ro3emu/gate-key.pem", ct);
        return new FakePlayerHost(options, pem, output);
    }

    public int Target => _target;

    public void SetTarget(int count) => _target = Math.Max(0, count);

    public IReadOnlyList<FakePlayer> Players()
    {
        lock (_sync) return _bots.Values.Select(b => b.Player).ToArray();
    }

    private async Task ManageAsync()
    {
        while (!_shutdown.IsCancellationRequested)
        {
            lock (_sync)
            {
                int target = _target;
                for (int index = 1; _bots.Count < target; index++)
                {
                    if (_bots.ContainsKey(index)) continue;
                    var player = new FakePlayer(this, FakeProfile.For(index));
                    var stop = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
                    _bots[index] = (player, Task.Run(() => player.RunAsync(stop.Token)), stop);
                }
                foreach (int index in _bots.Keys.OrderByDescending(i => i).Take(Math.Max(0, _bots.Count - target)).ToArray())
                {
                    var bot = _bots[index];
                    _bots.Remove(index);
                    bot.Stop.Cancel();
                    _stopping.Add(bot.Run.ContinueWith(_ => bot.Stop.Dispose(), TaskScheduler.Default));
                }
                _stopping.RemoveAll(t => t.IsCompleted);
            }
            SweepClaims();
            try { await Task.Delay(1000, _shutdown.Token); } catch (OperationCanceledException) { }
        }
    }

    /// <summary>The walkable area of a map, flood-filled once and shared.</summary>
    public MapNav? Nav(long sceneEntry) =>
        _navs.GetOrAdd(sceneEntry, scene => new Lazy<MapNav?>(() => _world.Map(scene) is { } map ? new MapNav(map) : null)).Value;

    /// <summary>The job's normal attack (JobConfig._kSkillNormalID[0]); the server always counts it as learned.</summary>
    public int NormalSkill(int job) =>
        _data.Row("JobConfig", job) is { } row && row.TryGetProperty("_kSkillNormalID", out var ids) && ids.ValueKind == JsonValueKind.Array
            && ids.GetArrayLength() > 0 && ids[0].TryGetInt32(out int id) && id > 0
            ? id
            : 1000001;

    /// <summary>The job's walking speed in m/s (JobConfig._kAttri Speed 113, in cm/s; Novice 700 = 7 m/s).</summary>
    public float WalkSpeed(int job) =>
        _data.JobAttributes(job).TryGetValue(113, out long speed) && speed > 0 ? speed / 100f : 7f;

    // ------------------------------------------------------------------ skills

    private Dictionary<(int Group, int Level), FakeSkill>? _skillRows;
    private readonly ConcurrentDictionary<int, FakeSkill> _skillById = new();

    /// <summary>A skill row as a fake player needs it: how it is aimed, its reach, cast time and cooldown.</summary>
    public FakeSkill? Skill(int group, int level)
    {
        _skillRows ??= _data.Table("SkillConfig").Values
            .Where(r => GameData.Int(r, "_iMode") == 0)
            .GroupBy(r => ((int)GameData.Int(r, "_iSkillID"), (int)GameData.Int(r, "_iLevel")))
            .ToDictionary(g => g.Key, g => ToSkill(g.First()));
        return _skillRows.GetValueOrDefault((group, level));
    }

    /// <summary>The job's normal attack as a skill (its reach decides how close the player stands).</summary>
    public FakeSkill NormalAttack(int job)
    {
        int id = NormalSkill(job);
        return _skillById.GetOrAdd(id, i => _data.Row("SkillConfig", i) is { } row ? ToSkill(row) : new FakeSkill(i, 0, 1, 2.5f, 0, 0, true));
    }

    private static FakeSkill ToSkill(JsonElement row) => new(
        (int)GameData.Int(row, "_iID"), (int)GameData.Int(row, "_iSkillID"), (int)GameData.Int(row, "_iRelease"),
        Math.Max(1.5f, GameData.Int(row, "_iDistanceMax") / 100f),
        (int)Math.Max(0, GameData.Int(row, "_iChantFix") + GameData.Int(row, "_iChantVar")),
        (int)Math.Max(0, GameData.Int(row, "_iCD")), GameData.Int(row, "_iIsNormalAtk") == 1, IsSelfBuff(row));

    /// <summary>A skill cast on oneself that applies a buff (Blessing, Increase AGI, Endure...): no damage, _kBuff1 set.</summary>
    public static bool IsSelfBuff(JsonElement row) =>
        GameData.Int(row, "_iBasicType") == 1 && GameData.Int(row, "_iRelease") == 2 && GameData.Int(row, "_iDmgType") == 0
        && row.TryGetProperty("_kBuff1", out var buffs) && buffs.ValueKind == JsonValueKind.Array && buffs.GetArrayLength() > 0;

    /// <summary>"skills &lt;account&gt; group:level,...": the attack skills the server taught a fake player.</summary>
    public void SetSkills(string account, string spec)
    {
        var player = Players().FirstOrDefault(p => p.Account == account);
        if (player == null) return;
        var skills = new List<FakeSkill>();
        foreach (var part in spec.Split(',', StringSplitOptions.RemoveEmptyEntries))
            if (part.Split(':') is [var g, var l] && int.TryParse(g, out int group) && int.TryParse(l, out int level) && Skill(group, level) is { } skill)
                skills.Add(skill);
        player.Learned = skills;
    }

    // ------------------------------------------------------------------ maps

    private readonly ConcurrentDictionary<long, int> _hunters = new();

    /// <summary>
    /// A field for a hunter: the one in the list with the fewest fake hunters (ties at random), so a crowd spreads over
    /// the maps instead of piling on one; <paramref name="avoid"/> is the map it is leaving.
    /// </summary>
    public long PickHuntMap(Random r, long[] maps, long avoid = 0)
    {
        var choices = maps.Where(m => m != avoid).DefaultIfEmpty(maps[0]).Distinct().ToArray();
        return choices.OrderBy(m => _hunters.GetValueOrDefault(m) + r.NextDouble()).First();
    }

    /// <summary>Counts a hunter on a map (and off the previous one).</summary>
    public void MoveHunter(long from, long to)
    {
        if (from != 0) _hunters.AddOrUpdate(from, 0, (_, n) => Math.Max(0, n - 1));
        if (to != 0) _hunters.AddOrUpdate(to, 1, (_, n) => n + 1);
    }

    // ------------------------------------------------------------------ shared targets

    private readonly ConcurrentDictionary<(long Scene, ulong Monster), (int Owner, DateTime Until)> _claims = new();

    /// <summary>Another fake player is already on this monster (its claim is refreshed on every swing).</summary>
    public bool ClaimedByOther(long scene, ulong monster, int me) =>
        _claims.TryGetValue((scene, monster), out var claim) && claim.Owner != me && claim.Until > DateTime.UtcNow;

    public void Claim(long scene, ulong monster, int me) => _claims[(scene, monster)] = (me, DateTime.UtcNow.AddSeconds(8));

    public void Unclaim(long scene, ulong monster, int me)
    {
        if (_claims.TryGetValue((scene, monster), out var claim) && claim.Owner == me) _claims.TryRemove((scene, monster), out _);
    }

    private void SweepClaims()
    {
        var now = DateTime.UtcNow;
        foreach (var (key, claim) in _claims)
            if (claim.Until < now) _claims.TryRemove(key, out _);
    }

    /// <summary>About 60% of the fake players talk at all.</summary>
    public bool Talkative(FakeProfile profile) => profile.Index % 5 < 3;

    public static string JobName(int job) => (job / 100) switch
    {
        10 => "Aprendiz", 11 => "Espadachim", 12 => job == 1210 ? "Templario" : "Cavaleiro", 13 => job == 1310 ? "Paladino" : "Lorde",
        21 => "Mago", 22 => job == 2210 ? "Sabio" : "Bruxo", 23 => "Arquimago",
        31 => "Arqueiro", 32 => job == 3210 ? "Bardo" : job == 3211 ? "Odalisca" : "Cacador", 33 => "Atirador de Elite",
        41 => "Novico", 42 => job == 4210 ? "Monge" : "Sacerdote", 43 => "Sumo Sacerdote",
        51 => "Gatuno", 52 => job == 5210 ? "Arruaceiro" : "Mercenario", 53 => "Algoz",
        61 => "Mercador", 62 => "Ferreiro", 63 => "Mestre Ferreiro",
        14 => job == 1410 ? "Guardiao Real" : "Cavaleiro Runico", 24 => "Arcano", 34 => "Sentinela", 44 => "Arcebispo",
        54 => "Sicario", 64 => "Mecanico",
        _ => "aventureiro",
    };

    public void CountDeath() => Interlocked.Increment(ref _deaths);
    public void CountRevive() => Interlocked.Increment(ref _revives);
    public void CountAttack() => Interlocked.Increment(ref _attacks);
    public void CountSkill() => Interlocked.Increment(ref _skillCasts);
    public void CountRest() => Interlocked.Increment(ref _rests);
    public void CountChat() => Interlocked.Increment(ref _chats);

    public void Failed(FakePlayer player, Exception e)
    {
        long n = Interlocked.Increment(ref _failures);
        if (n <= 20 || n % 100 == 0) Log($"{player.Account}: {e.GetType().Name}: {e.Message} (failures so far: {n})");
    }

    public void Log(string text)
    {
        long n = Interlocked.Increment(ref _logs);
        if (n <= 200 || n % 100 == 0) Output("@log " + text.Replace('\n', ' ').Replace('\r', ' '));
    }

    public void Output(string line) => _output(line);

    /// <summary>One line with every counter: "@status target=... online=...".</summary>
    public string Status()
    {
        var players = Players();
        int town = players.Count(p => p.State == FakeState.Town), hunting = players.Count(p => p.State == FakeState.Hunting);
        int dead = players.Count(p => p.State == FakeState.Dead), starting = players.Count(p => p.State == FakeState.Starting);
        int retrying = players.Count(p => p.State == FakeState.Retrying);
        return $"@status target={_target} online={town + hunting + dead} starting={starting} town={town} hunting={hunting} dead={dead} " +
            $"retrying={retrying} deaths={Interlocked.Read(ref _deaths)} revives={Interlocked.Read(ref _revives)} " +
            $"attacks={Interlocked.Read(ref _attacks)} skills={Interlocked.Read(ref _skillCasts)} rests={Interlocked.Read(ref _rests)} chats={Interlocked.Read(ref _chats)} failures={Interlocked.Read(ref _failures)}";
    }

    public async ValueTask DisposeAsync()
    {
        _target = 0;
        _shutdown.Cancel();
        try { await _manager; } catch (OperationCanceledException) { }
        Task[] running;
        lock (_sync)
        {
            running = _bots.Values.Select(b => b.Run).Concat(_stopping).ToArray();
            foreach (var bot in _bots.Values) bot.Stop.Cancel();
            _bots.Clear();
        }
        try { await Task.WhenAll(running).WaitAsync(TimeSpan.FromSeconds(20)); } catch { }
        _shutdown.Dispose();
    }
}
