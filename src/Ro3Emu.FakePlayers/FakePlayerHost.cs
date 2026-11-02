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

    /// <summary>
    /// Milliseconds between two logins (0: none). Each login loads, the first time creates, a character on the server's game
    /// loop; a burst of a hundred left the real players' requests unanswered until their clients reconnected.
    /// </summary>
    public int LoginGapMs { get; init; } = 1000;
}

/// <summary>
/// A castable skill row: row id, group, aim (_iRelease 1 unit, 2 self, 7/8 ground), reach, cast and cooldown; whether it
/// is a self buff or a heal, and the job rank it belongs to (5 = a third class's own skill; 0 when not known).
/// </summary>
public sealed record FakeSkill(int RowId, int Group, int Release, float Range, int ChantMs, int CooldownMs, bool Normal, bool Buff = false,
    bool Heal = false, int Rank = 0)
{
    /// <summary>Hurts a monster (aimed, on the ground or around the caster).</summary>
    public bool Attack => !Normal && !Buff && !Heal;
}

/// <summary>
/// A guild-war order from the server ("warbot", GvgSimService): the side (A the attackers, D the defenders, P a player
/// pulled into a chosen guild), whether this player founds the side's guild, and the guild: its name for a founder, its
/// id otherwise (0 = the guild the side's founder reports).
/// </summary>
public sealed record WarOrder(char Side, bool Leader, string GuildName, long GuildId);

/// <summary>
/// A siege cannon as a gunner uses it (GVGCannonConfig by _iItemID): its NPC and skill, the fire and quit buttons of its
/// gameplay status, the NPC's boarding interaction, the ballistics the client sends (_fGravity m/s^2, _iSingleShotNum
/// bullets per volley, _kFireOffset muzzle cm), its bullets and reload, the indicator's range (m) and the vertical aim
/// limits (degrees).
/// </summary>
public sealed record WarCannon(int Item, int NpcId, int SkillId, int FireButton, int QuitButton, int Interaction, float Gravity, int Shots,
    int Bullets, int ReloadMs, float Side, float Up, float Forward, float MinRange, float MaxRange, float MinAngle, float MaxAngle);

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
    // The guild war: its players (apart from the target-managed ones), the guild each side's founder reported, the city
    // whose siege they enter (0: none open), what both sides focus on, and the counters of "@warstatus".
    private readonly Dictionary<int, (FakePlayer Player, Task Run, CancellationTokenSource Stop)> _war = new();
    private readonly ConcurrentDictionary<char, long> _warGuilds = new();
    private readonly ConcurrentDictionary<long, (ulong Target, DateTime Until)> _warFocus = new();
    private volatile int _warCity;
    private long _warDeaths, _warCasts, _warHeals, _warShots, _warRides, _warEntries;

    public FakeHostOptions Options { get; }
    public string Pem { get; }
    public SemaphoreSlim LoginGate { get; }
    private readonly SemaphoreSlim _loginPace = new(1, 1);
    private DateTime _nextLogin;

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

    /// <summary>Waits for this login's turn: logins start at most one per <see cref="FakeHostOptions.LoginGapMs"/>.</summary>
    public async Task PaceLoginAsync(CancellationToken ct)
    {
        if (Options.LoginGapMs <= 0) return;
        await _loginPace.WaitAsync(ct);
        try
        {
            var wait = _nextLogin - DateTime.UtcNow;
            if (wait > TimeSpan.Zero) await Task.Delay(wait, ct);
            _nextLogin = DateTime.UtcNow.AddMilliseconds(Options.LoginGapMs);
        }
        finally { _loginPace.Release(); }
    }

    public void SetTarget(int count) => _target = Math.Max(0, count);

    public IReadOnlyList<FakePlayer> Players()
    {
        lock (_sync) return _bots.Values.Select(b => b.Player).ToArray();
    }

    /// <summary>The guild-war players (GvgSimService's "warbot" lines).</summary>
    public IReadOnlyList<FakePlayer> WarPlayers()
    {
        lock (_sync) return _war.Values.Select(b => b.Player).ToArray();
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
        (int)Math.Max(0, GameData.Int(row, "_iCD")), GameData.Int(row, "_iIsNormalAtk") == 1, IsSelfBuff(row), IsHeal(row));

    /// <summary>A heal (Heal, Highness Heal, Sanctuary, Radiant Holy Light...): skill tag 301 on a damage-type-2 row.</summary>
    public static bool IsHeal(JsonElement row) =>
        GameData.Int(row, "_iDmgType") == 2 && row.TryGetProperty("_kSkilltag", out var tags) && tags.ValueKind == JsonValueKind.Array
        && tags.EnumerateArray().Any(t => t.ValueKind == JsonValueKind.Number && t.GetDouble() == 301);

    /// <summary>A skill cast on oneself that applies a buff (Blessing, Increase AGI, Endure...): no damage, _kBuff1 set.</summary>
    public static bool IsSelfBuff(JsonElement row) =>
        GameData.Int(row, "_iBasicType") == 1 && GameData.Int(row, "_iRelease") == 2 && GameData.Int(row, "_iDmgType") == 0
        && row.TryGetProperty("_kBuff1", out var buffs) && buffs.ValueKind == JsonValueKind.Array && buffs.GetArrayLength() > 0;

    /// <summary>"skills &lt;account&gt; group:level,...": the attack skills the server taught a fake player.</summary>
    public void SetSkills(string account, string spec)
    {
        var player = Players().Concat(WarPlayers()).FirstOrDefault(p => p.Account == account);
        if (player == null) return;
        var skills = new List<FakeSkill>();
        foreach (var part in spec.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            // group:level, or group:level:rank (the job rank the skill belongs to).
            var bits = part.Split(':');
            if (bits.Length < 2 || !int.TryParse(bits[0], out int group) || !int.TryParse(bits[1], out int level)
                || Skill(group, level) is not { } skill) continue;
            skills.Add(bits.Length > 2 && int.TryParse(bits[2], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int rank) ? skill with { Rank = rank } : skill);
        }
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

    // ------------------------------------------------------------------ guild war (GvgSimService, "!gvg")

    /// <summary>"warbot &lt;index&gt; &lt;A|D|P&gt; &lt;leader|member&gt; &lt;name|guildId&gt;": starts a war player, or gives a running one its new order.</summary>
    public void StartWarBot(int index, char side, bool leader, string guild)
    {
        if (!FakeProfile.IsWar(index)) return;
        long id = !leader && long.TryParse(guild, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out long parsed) ? parsed : 0;
        var order = new WarOrder(side, leader, leader ? guild : "", id);
        lock (_sync)
        {
            if (_war.TryGetValue(index, out var running))
            {
                running.Player.War = order;
                return;
            }
            var player = new FakePlayer(this, FakeProfile.For(index)) { War = order };
            var stop = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
            _war[index] = (player, Task.Run(() => player.RunAsync(stop.Token)), stop);
        }
    }

    /// <summary>"wargo &lt;city&gt;": the siege of that city is open; every war player in a guild enters it.</summary>
    public void WarGo(int city) => _warCity = Math.Max(0, city);

    /// <summary>The city whose siege the war players enter, 0 while none is open.</summary>
    public int WarCity => _warCity;

    /// <summary>"warstop": every war player leaves (it logs out) and the sides are forgotten.</summary>
    public void WarStop()
    {
        _warCity = 0;
        lock (_sync)
        {
            foreach (var bot in _war.Values)
            {
                bot.Stop.Cancel();
                _stopping.Add(bot.Run.ContinueWith(_ => bot.Stop.Dispose(), TaskScheduler.Default));
            }
            _war.Clear();
        }
        _warGuilds.Clear();
        _warFocus.Clear();
    }

    /// <summary>The guild a side's founder reported, or 0.</summary>
    public long WarGuild(char side) => _warGuilds.GetValueOrDefault(side);

    /// <summary>A side's founder has its guild open to applications: "@guild &lt;side&gt; &lt;guildId&gt;" for the server.</summary>
    public void WarGuildReady(char side, long guild)
    {
        _warGuilds[side] = guild;
        Output($"@guild {side} {guild}");
    }

    /// <summary>A war player applied to a guild that does not auto-accept: the GM who asked for it approves it.</summary>
    public void WarApplied(long role, long guild) => Output($"@applied {role} {guild}");

    /// <summary>"@warfail &lt;account&gt; &lt;step&gt; &lt;code&gt;": the server tells the GM (once a minute per kind).</summary>
    public void WarFailed(string account, string step, int code) => Output($"@warfail {account} {step} {code}");

    /// <summary>The enemy a camp's war players focus on, while it is fresh.</summary>
    public ulong WarFocus(long camp) => _warFocus.TryGetValue(camp, out var focus) && focus.Until > DateTime.UtcNow ? focus.Target : 0;

    /// <summary>Marks a hurt enemy for the whole camp for a few seconds (focus fire).</summary>
    public void SetWarFocus(long camp, ulong target) => _warFocus[camp] = (target, DateTime.UtcNow.AddSeconds(6));

    /// <summary>At most this many war players load into the siege at once.</summary>
    public SemaphoreSlim WarEntryGate { get; } = new(6);

    public void CountWarDeath() => Interlocked.Increment(ref _warDeaths);
    public void CountWarCast() => Interlocked.Increment(ref _warCasts);
    public void CountWarHeal() => Interlocked.Increment(ref _warHeals);
    public void CountWarShot() => Interlocked.Increment(ref _warShots);
    public void CountWarRide() => Interlocked.Increment(ref _warRides);
    public void CountWarEntry() => Interlocked.Increment(ref _warEntries);

    /// <summary>One "@warstatus" line: per side the players online, in a guild and in the siege, and the war counters.</summary>
    public string WarStatus()
    {
        var players = WarPlayers();
        string Side(char side)
        {
            var mine = players.Where(p => p.War?.Side == side).ToArray();
            return $"{side}={mine.Count(p => p.Online)}/{mine.Length} guilda={mine.Count(p => p.InWarGuild)} cerco={mine.Count(p => p.InSiege)}";
        }
        return $"@warstatus {Side('A')} {Side('D')} {Side('P')} cidade={_warCity} entradas={Interlocked.Read(ref _warEntries)} " +
            $"mortes={Interlocked.Read(ref _warDeaths)} skills={Interlocked.Read(ref _warCasts)} curas={Interlocked.Read(ref _warHeals)} " +
            $"tiros={Interlocked.Read(ref _warShots)} veiculos={Interlocked.Read(ref _warRides)}";
    }

    // ------------------------------------------------------------------ war data (config rows the war players read)

    private readonly ConcurrentDictionary<string, HashSet<(int X, int Z)>> _wallCells = new();
    private readonly ConcurrentDictionary<long, int> _npcSubTypes = new();
    private readonly ConcurrentDictionary<long, int> _driverInteractions = new();
    private readonly ConcurrentDictionary<long, IReadOnlyList<FakeSkill>> _seatSkills = new();
    private readonly ConcurrentDictionary<int, WarCannon?> _cannons = new();
    private readonly ConcurrentDictionary<int, FakeSkill?> _rowSkills = new();

    /// <summary>
    /// The cells of a scene's standing air walls (the siege's var 133 list; scene editor m_kAirWalls) as a test for the
    /// route search, or null when none stands. Shared by every war player that sees the same set.
    /// </summary>
    public Func<int, int, bool>? WallTest(long scene, IReadOnlyList<long> walls)
    {
        if (walls.Count == 0 || _world.Map(scene) is not { } map) return null;
        string key = scene + ":" + string.Join(',', walls);
        var cells = _wallCells.GetOrAdd(key, _ =>
        {
            var set = new HashSet<(int X, int Z)>();
            foreach (long id in walls)
                if (map.AirWalls.TryGetValue(id, out var wall)) set.UnionWith(wall.Cells);
            return set;
        });
        if (cells.Count == 0) return null;
        return (x, z) => cells.Contains((x, z));
    }

    /// <summary>NPCConfig _iNPCSubType: 102 a siege cannon, 103 a war vehicle.</summary>
    public int NpcSubType(long npcId) => _npcSubTypes.GetOrAdd(npcId, id =>
        _data.Row("NPCConfig", id) is { } row ? (int)GameData.Int(row, "_iNPCSubType") : 0);

    public bool IsVehicleNpc(long npcId) => NpcSubType(npcId) == 103;

    public bool IsCannonNpc(long npcId) => NpcSubType(npcId) == 102;

    /// <summary>
    /// The interaction that takes a vehicle's driver seat: one of NPCConfig _iInteractID whose NPCInteractConfig sets a
    /// vehicle gameplay status (GameplayStatusConfig _iStatusType 3) at _iStatusLocation 1; 0 when the NPC has none.
    /// </summary>
    public int DriverInteraction(long npcId) => _driverInteractions.GetOrAdd(npcId, id =>
    {
        if (_data.Row("NPCConfig", id) is not { } npc) return 0;
        foreach (int interaction in Ints(npc, "_iInteractID"))
            if (_data.Row("NPCInteractConfig", interaction) is { } row && GameData.Int(row, "_iStatusLocation") == 1
                && _data.Row("GameplayStatusConfig", GameData.Int(row, "_iStatusID")) is { } status && GameData.Int(status, "_iStatusType") == 3)
                return interaction;
        return 0;
    });

    /// <summary>The skills of a vehicle's driver seat (VehicleConfig _kSeats[0] -> VehicleSeatConfig _kMainSkills and _kSkills).</summary>
    public IReadOnlyList<FakeSkill> DriverSkills(long npcId) => _seatSkills.GetOrAdd(npcId, id =>
    {
        if (_data.Row("NPCConfig", id) is not { } npc || _data.Row("VehicleConfig", GameData.Int(npc, "_iSubTypeValue")) is not { } vehicle
            || Ints(vehicle, "_kSeats") is not [var seat, ..] || _data.Row("VehicleSeatConfig", seat) is not { } row) return [];
        return [.. Ints(row, "_kMainSkills").Concat(Ints(row, "_kSkills")).Distinct().Select(SkillRow).OfType<FakeSkill>()];
    });

    /// <summary>A SkillConfig row by its id as a fake player casts it, or null.</summary>
    public FakeSkill? SkillRow(int rowId) => _rowSkills.GetOrAdd(rowId, id => _data.Row("SkillConfig", id) is { } row ? ToSkill(row) : null);

    /// <summary>The cannon a war item builds (GVGCannonConfig _iItemID), or null.</summary>
    public WarCannon? Cannon(int item) => _cannons.GetOrAdd(item, BuildCannon);

    private WarCannon? BuildCannon(int item)
    {
        foreach (var row in _data.Table("GVGCannonConfig").Values)
        {
            if (GameData.Int(row, "_iItemID") != item) continue;
            int npc = (int)GameData.Int(row, "_iNPCID"), skill = (int)GameData.Int(row, "_iSkillID"), fire = 0, quit = 0;
            foreach (int button in Ints(row, "_kButtonActionIDs"))
            {
                if (_data.Row("GameplayStatusButtonConfig", button) is not { } b) continue;
                long type = GameData.Int(b, "_iButtonFuncType");
                if (type == 2 && GameData.Int(b, "_iSkill") == skill && fire == 0) fire = button;
                else if (type == 0 && quit == 0 && b.TryGetProperty("_kAction", out var action)
                    && action.ToString().Contains("5004", StringComparison.Ordinal)) quit = button;
            }
            int interaction = _data.Row("NPCConfig", npc) is { } n && Ints(n, "_iInteractID") is [var first, ..] ? first : 0;
            var offset = Floats(row, "_kFireOffset");
            var angles = Floats(row, "_fFireVerticalAngle");
            return new WarCannon(item, npc, skill, fire, quit, interaction, Float(row, "_fGravity"),
                Math.Max(1, (int)GameData.Int(row, "_iSingleShotNum")), Math.Max(1, (int)GameData.Int(row, "_iBulletNumLimit")),
                (int)GameData.Int(row, "_iReloadTime"),
                offset.Length > 0 ? offset[0] / 100f : 0, offset.Length > 1 ? offset[1] / 100f : 0, offset.Length > 2 ? offset[2] / 100f : 0,
                GameData.Int(row, "_iIndicatorRadiusMin") / 100f, Float(row, "_fIndicatorRadius") / 100f,
                angles.Length > 0 ? angles[0] : 10, angles.Length > 1 ? angles[1] : 60);
        }
        return null;
    }

    private static int[] Ints(JsonElement row, string field) =>
        row.TryGetProperty(field, out var v) && v.ValueKind == JsonValueKind.Array
            ? [.. v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Number).Select(x => (int)x.GetDouble())]
            : [];

    private static float[] Floats(JsonElement row, string field) =>
        row.TryGetProperty(field, out var v) && v.ValueKind == JsonValueKind.Array
            ? [.. v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Number).Select(x => (float)x.GetDouble())]
            : [];

    private static float Float(JsonElement row, string field) =>
        row.TryGetProperty(field, out var v) && v.ValueKind == JsonValueKind.Number ? (float)v.GetDouble() : 0f;

    /// <summary>About 60% of the fake players talk at all.</summary>
    public bool Talkative(FakeProfile profile) => profile.Index % 5 < 3;

    public static string JobName(int job) => (job / 100) switch
    {
        10 => "Aprendiz",
        11 => "Espadachim",
        12 => job == 1210 ? "Templario" : "Cavaleiro",
        13 => job == 1310 ? "Paladino" : "Lorde",
        21 => "Mago",
        22 => job == 2210 ? "Sabio" : "Bruxo",
        23 => "Arquimago",
        31 => "Arqueiro",
        32 => job == 3210 ? "Bardo" : job == 3211 ? "Odalisca" : "Cacador",
        33 => "Atirador de Elite",
        41 => "Novico",
        42 => job == 4210 ? "Monge" : "Sacerdote",
        43 => "Sumo Sacerdote",
        51 => "Gatuno",
        52 => job == 5210 ? "Arruaceiro" : "Mercenario",
        53 => "Algoz",
        61 => "Mercador",
        62 => "Ferreiro",
        63 => "Mestre Ferreiro",
        14 => job == 1410 ? "Guardiao Real" : "Cavaleiro Runico",
        24 => "Arcano",
        34 => "Sentinela",
        44 => "Arcebispo",
        54 => "Sicario",
        64 => "Mecanico",
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
            running = _bots.Values.Select(b => b.Run).Concat(_war.Values.Select(b => b.Run)).Concat(_stopping).ToArray();
            foreach (var bot in _bots.Values) bot.Stop.Cancel();
            foreach (var bot in _war.Values) bot.Stop.Cancel();
            _bots.Clear();
            _war.Clear();
        }
        try { await Task.WhenAll(running).WaitAsync(TimeSpan.FromSeconds(20)); } catch { }
        _shutdown.Dispose();
    }
}
