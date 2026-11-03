using System.Collections.Concurrent;
using Ro3Emu.Bot;
using Ro3Emu.LoadTest;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;
using Romsg;

namespace Ro3Emu.FakePlayers;

/// <summary>
/// A fake player in the guild war (FakeBehavior.Warrior; the GM's "!gvg", GvgSimService; docs/guide/fake-players.md
/// "Guerra simulada"). Through the real protocol it joins its side's guild (the founder creates it and opens it to
/// applications), waits in Alberta until the server opens the siege ("wargo"), enters the castle siege (30400) and
/// fights it as its camp: attackers march from their camp to the gate, then the Knight Guardians, then the crystal;
/// defenders hold the city wall and the gate, then the guardians, then the crystal. On the way it picks its targets
/// (enemy players before castle units, hurt ones and its camp's focus first), casts its class's skills (a third class
/// prefers its own, area skills on crowds), keeps its distance as a ranged class or healer, heals hurt allies as an
/// Archbishop, steps back when low, revives, and works the war machines its guild's founder gave it: cannons (built,
/// boarded and fired on a ballistic aim) and vehicles (boarded and driven with their seat skills). It walks around the
/// siege's standing air walls (var 133), as the real client cannot pass them.
/// </summary>
public sealed partial class FakePlayer
{
    private const long SiegeScene = 2230101;
    private const uint DungeonBaseInfo = 30401, GlobalObjects = 30115;
    private const int AirWallListVar = 133;
    private const int PrepareStage = 0, GateStage = 1, StrongholdStage = 2, CrystalStage = 3;
    private const long GateNpc = 4900005, CrystalNpc = 4900009, KnightGuardianNpc = 4901011, BowGuardianNpc = 4901012,
        BarricadeNpc = 4900022, ArcherNpc = 4901001, RamNpc = 4900002, LadderNpc = 4900000, LeftLadderNpc = 4900012;
    /// <summary>Siege NPCs nobody fights: guide arrows, building markers, the revive markers and the revive point.</summary>
    private static readonly HashSet<long> Scenery = [4903001, 4903002, 4901021, 4901022, 4900010, 4900017];
    /// <summary>The cannons the gunners build, in turn: Arm Cannon (mortar), Flying Raven, Rocket Launcher, Anti-Aircraft Gun.</summary>
    private static readonly int[] CannonItems = [1006001, 1006002, 1006004, 1006003];
    /// <summary>The vehicles the drivers take from the warehouse when no vehicle of their camp stands free.</summary>
    private static readonly int[] VehicleItems = [1006005, 1006006, 1006007, 1006008, 1006009];

    private static readonly string[] WarLines =
    [
        "avante!!", "segura o portao!", "foco no curandeiro deles", "todos juntos agora", "bora quebrar esse cristal",
        "ninguem passa!", "cura aqui pfv", "vem pro portao", "olha os canhoes!", "kkkkk fugiram", "defende o guardiao!",
        "mais um caiu", "gg", "segura a escada", "vamo vamo vamo", "recua e cura", "pelo castelo!",
    ];

    private static readonly string[] DeathLines = ["me revivam!", "volto ja", "ai ai", "renascendo...", "morri kkk", "foco em mim, que isso"];

    private enum WarPhase { Guild, Waiting, Entering, Battle }

    private sealed record WarUnit(ulong Uid, float X, float Y, float Z, bool Player, long Npc, float Hp);

    /// <summary>What a war player sees in one step: enemy players, own-camp players, enemy castle units and machines.</summary>
    private sealed class WarView
    {
        public List<WarUnit> Enemies { get; } = [];
        public List<WarUnit> Allies { get; } = [];
        public List<WarUnit> Targets { get; } = [];
        public List<EntityBase> Machines { get; } = [];
        public float NearestEnemy { get; set; } = float.MaxValue;
    }

    /// <summary>The guild-war order of this player (null for an ordinary fake player).</summary>
    public WarOrder? War { get; set; }

    /// <summary>The role id once logged in (the founder gives war machine permissions by it).</summary>
    public long RoleId => _roleId;

    /// <summary>The guild of the last guild info the server sent (0: none).</summary>
    public long GuildId => Interlocked.Read(ref _guildId);

    public bool Online => State is FakeState.Town or FakeState.Hunting or FakeState.Dead or FakeState.Fighting;

    /// <summary>In the guild its order names (for a founder: any guild it founded).</summary>
    public bool InWarGuild => War is { } order && GuildId is var guild and not 0 && (order.Leader || guild == WarTargetGuild(order));

    public bool InSiege => _warPhase == WarPhase.Battle;

    private long _guildId, _switchedScene;
    /// <summary>The own entity's camp in the scene: in the siege 1 for the attackers and 2 for the defenders.</summary>
    private long _camp;
    private WarPhase _warPhase;
    private DateTime _warNextTry, _assignNext, _retreatNext, _machineNext;
    private bool _autoAcceptSet, _healAllyBroken, _moving, _spread;
    private int _guildNameTry, _stuck, _vehicleTries, _cannonIdle, _cannonShift, _distanceMisses;
    private volatile int _siegeStage = -1;
    private volatile long[] _walls = [];
    private volatile bool _sceneSwitched, _switching, _onCannon, _riding;
    private readonly ConcurrentDictionary<long, GlobalObjectData> _objectives = new();
    private readonly ConcurrentDictionary<ulong, (long Hp, long Max)> _vitals = new();
    private readonly ConcurrentDictionary<ulong, DateTime> _fallen = new();
    private readonly ConcurrentDictionary<ulong, DateTime> _hurt = new();
    private readonly Dictionary<int, WartimeToolType> _assigned = [];
    private ulong _foe;
    /// <summary>Where the server put this player (31081 reset or teleport: the siege's born point after the load, a
    /// refused walk), taken over at the next step.</summary>
    private Vec3? _teleport;
    /// <summary>The own camp's Battering Ram as last seen (the attackers in the middle lane escort it to the gate).</summary>
    private (float X, float Z, DateTime At)? _ram;
    private List<(float X, float Y, float Z)>? _route;
    private float _routeGoalX, _routeGoalZ;
    private DateTime _routeAt;
    private WarCannon? _cannon;
    private long _cannonUid, _vehicleUid;
    private IReadOnlyList<FakeSkill> _seat = [];
    private readonly Dictionary<int, DateTime> _seatReady = [];
    private readonly HashSet<int> _seatBroken = [];

    private int WarSlot => Profile.Index % 1000;
    private bool IsHealer => Profile.Job == 4400;
    private bool IsRanged => Profile.Job is 2400 or 3400;
    /// <summary>Players 2-9 of a side build and fire cannons, 12-19 drive vehicles (Firearm and Vehicle slots 1-8: the
    /// last two of each type stay for real players, "!gvg entrar" / "!gvg maquina").</summary>
    private bool IsGunner => War?.Side is 'A' or 'D' && WarSlot is >= 2 and <= 9;
    private bool IsDriver => War?.Side is 'A' or 'D' && WarSlot is >= 12 and <= 19;

    private long WarTargetGuild(WarOrder order) => order.GuildId != 0 ? order.GuildId : Host.WarGuild(order.Side);

    /// <summary>A line for map chat, picked for the war.</summary>
    private string WarChatLine() => WarLines[_r.Next(WarLines.Length)];

    // ------------------------------------------------------------------ what the server sends (off the bot's loop)

    private void ObserveWar(BotEvent e)
    {
        if (e.Kind != BotEventKind.Game) return;
        // A body this player reads differently from what the server sent must not cost it its connection.
        try { ObserveWarMessage(e); }
        catch (Google.Protobuf.InvalidProtocolBufferException) { }
    }

    private void ObserveWarMessage(BotEvent e)
    {
        switch (e.MsgId)
        {
            case GuildService.Full:
                {
                    // The own guild (0: the empty info that follows leaving or being removed).
                    Interlocked.Exchange(ref _guildId, GuildFullInfoNtf.Parser.ParseFrom(e.Body).Guid);
                    break;
                }
            case GuildService.Leave:
                {
                    if (GuildMemberLeaveResp.Parser.ParseFrom(e.Body).Result == 0) Interlocked.Exchange(ref _guildId, 0);
                    break;
                }
            case DungeonBaseInfo:
                {
                    var info = DungeonBaseInfoNotify.Parser.ParseFrom(e.Body);
                    if (info.DungeonEntry != SiegeScene) break;
                    _siegeStage = info.Stage;
                    foreach (var v in info.Vars)
                        if (v.VarId == AirWallListVar) _walls = [.. v.Vi64.Order()];
                    break;
                }
            case GlobalObjects:
                {
                    foreach (var (uid, data) in GlobalObjectDataNotify.Parser.ParseFrom(e.Body).GlobalDatas)
                        if (data.Hp <= 0) _objectives.TryRemove(uid, out _);
                        else _objectives[uid] = data;
                    break;
                }
            case CombatService.SkillHit:
                {
                    if (_warPhase != WarPhase.Battle) break;
                    var now = DateTime.UtcNow;
                    foreach (var dst in SkillHitInfo.Parser.ParseFrom(e.Body).Dsts)
                    {
                        if (dst.IsDead) _fallen[(ulong)dst.DstID] = now;
                        else if (dst.Damage > 0 && dst.DstID is > 0 and < UnitUidBase) _hurt[(ulong)dst.DstID] = now;
                    }
                    break;
                }
            case CharacterService.AoiAttrChange:
                {
                    if (_warPhase != WarPhase.Battle) break;
                    var change = AOIAttrChange.Parser.ParseFrom(e.Body);
                    UpdateVitals(change.EntityUuid, change.Property);
                    break;
                }
            case CharacterService.EntityBaseChange:
                {
                    if (_warPhase != WarPhase.Battle) break;
                    foreach (var entity in EntityChange.Parser.ParseFrom(e.Body).Bases)
                        if (entity.State == EntityState.Dead) _fallen[entity.Uuid] = DateTime.UtcNow;
                        else if (entity.State == EntityState.Alive) _fallen.TryRemove(entity.Uuid, out _);
                    break;
                }
            case WorldInteractionService.Change:
                {
                    var change = InteractChangeNtf.Parser.ParseFrom(e.Body);
                    if (change.Uid == _roleId && (change.DataInfo == null || change.DataInfo.EType == InteractInfo.Types.EState.Idle))
                    {
                        _onCannon = false;
                        _riding = false;
                    }
                    break;
                }
            case GameAoiSink.MovementChange:
                {
                    var move = MovementChangeNtf.Parser.ParseFrom(e.Body);
                    if (move.Uid == _roleId && move.TransType is MovementTransitionType.MoveTransResetPos or MovementTransitionType.MoveTransForceTeleport
                        && move.MovementInfo?.Pos is { } at)
                        Interlocked.Exchange(ref _teleport, at.Clone());
                    break;
                }
            case MsgId.SwitchSceneLine:
                {
                    // A switch this player did not ask for (the siege ended and sent it back): followed by the war loop.
                    if (_switching) break;
                    Interlocked.Exchange(ref _switchedScene, SwitchSceneLineResp.Parser.ParseFrom(e.Body).SceneEntry);
                    _sceneSwitched = true;
                    break;
                }
        }
    }

    private void UpdateVitals(ulong uid, IEnumerable<AoIProperty> properties)
    {
        long hp = -1, max = -1;
        foreach (var p in properties)
            if (p.Idx == 2000) hp = p.Data;
            else if (p.Idx == 101) max = p.Data;
        if (hp < 0 && max <= 0) return;
        var (oldHp, oldMax) = _vitals.GetValueOrDefault(uid, (-1L, 0L));
        _vitals[uid] = (hp >= 0 ? hp : oldHp, max > 0 ? max : oldMax);
    }

    /// <summary>HP share of a unit (1 when not known): its AOI updates, else the properties of its entity.</summary>
    private float HpShare(EntityBase e)
    {
        if (!_vitals.ContainsKey(e.Uuid)) UpdateVitals(e.Uuid, e.Property);
        var (hp, max) = _vitals.GetValueOrDefault(e.Uuid, (-1L, 0L));
        return max > 0 && hp >= 0 ? Math.Clamp(hp / (float)max, 0f, 1f) : 1f;
    }

    /// <summary>Seen dying a moment ago (players revive within seconds; castle units come back later or never).</summary>
    private bool Fallen(ulong uid, DateTime now) =>
        _fallen.TryGetValue(uid, out var at) && now - at < TimeSpan.FromSeconds(uid < UnitUidBase ? 12 : 25);

    // ------------------------------------------------------------------ the war loop

    private async Task WarStepAsync(BotClient bot, WorldReducer reducer, CancellationToken ct)
    {
        if (Interlocked.Exchange(ref _teleport, null) is { } moved)
        {
            SetPosition(moved);
            _route = null;
            _moving = false;
        }
        if (_sceneSwitched)
        {
            await FollowSwitchAsync(bot, reducer, ct);
            return;
        }
        if (War is not { } order)
        {
            await PauseAsync(bot, TimeSpan.FromSeconds(1), ct);
            return;
        }
        switch (_warPhase)
        {
            case WarPhase.Guild:
                await WarGuildAsync(bot, order, ct);
                break;
            case WarPhase.Waiting:
                await WarWaitAsync(bot, reducer, order, ct);
                break;
            case WarPhase.Entering:
                await WarEnterAsync(bot, reducer, ct);
                break;
            default:
                await BattleAsync(bot, reducer, ct);
                break;
        }
    }

    /// <summary>The server moved this player (the siege ended): load the new scene; out of the castle the war waits again.</summary>
    private async Task FollowSwitchAsync(BotClient bot, WorldReducer reducer, CancellationToken ct)
    {
        _sceneSwitched = false;
        long scene = Interlocked.Read(ref _switchedScene);
        using var self = bot.Inbox.Expect(e => e.IsGame(MsgId.EntitySelf));
        using var end = bot.Inbox.Expect(e => e.IsGame(MsgId.SelfInfoEndFrameNotify));
        _switching = true;
        try { await FinishSceneAsync(bot, reducer, scene, self, end, ct); }
        finally { _switching = false; }
        if (scene != SiegeScene && _warPhase == WarPhase.Battle)
        {
            LeaveBattle();
            _spread = false;
            _warPhase = WarPhase.Waiting;
            _warNextTry = DateTime.UtcNow.AddSeconds(_r.Next(20, 60));
        }
    }

    // ------------------------------------------------------------------ the guild

    private async Task WarGuildAsync(BotClient bot, WarOrder order, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        long current = GuildId;
        if (now < _warNextTry)
        {
            await PauseAsync(bot, TimeSpan.FromSeconds(2), ct);
            return;
        }
        if (order.Leader)
        {
            if (current == 0)
            {
                int created = await CreateGuildAsync(bot, order.GuildName, ct);
                if (created == ErrorCodes.GuildNameTaken) _guildNameTry++;
                else if (created != 0)
                {
                    Host.WarFailed(Account, "fundar-guilda", created);
                    _warNextTry = now.AddSeconds(30);
                }
                return;
            }
            if (!_autoAcceptSet)
            {
                int edited = await EditAutoAcceptAsync(bot, ct);
                if (edited != 0)
                {
                    Host.WarFailed(Account, "auto-aceitar", edited);
                    _warNextTry = now.AddSeconds(30);
                    return;
                }
                _autoAcceptSet = true;
            }
            Host.WarGuildReady(order.Side, current);
            _warPhase = WarPhase.Waiting;
            return;
        }
        long want = WarTargetGuild(order);
        if (want == 0)
        {
            await PauseAsync(bot, TimeSpan.FromSeconds(2), ct);
            return;
        }
        if (current == want)
        {
            _warPhase = WarPhase.Waiting;
            return;
        }
        if (current != 0)
        {
            await LeaveGuildAsync(bot, ct);
            _warNextTry = now.AddSeconds(2);
            return;
        }
        int applied = await ApplyGuildAsync(bot, want, ct);
        if (applied != 0)
        {
            Host.WarFailed(Account, "entrar-guilda", applied);
            _warNextTry = now.AddSeconds(_r.Next(30, 60));
            return;
        }
        // Auto-accept joins inside the apply (32213 follows); otherwise the GM who asked for this player approves it.
        for (int i = 0; i < 10 && GuildId != want; i++) await PauseAsync(bot, TimeSpan.FromSeconds(1), ct);
        if (GuildId != want)
        {
            Host.WarApplied(_roleId, want);
            _warNextTry = DateTime.UtcNow.AddSeconds(90);
        }
    }

    /// <summary>The result code of a reply, or -1 when it did not come in time (the step is tried again later).</summary>
    private static async Task<int> ResultAsync(BotExpectation reply, Func<byte[], int> result, TimeSpan timeout, CancellationToken ct)
    {
        try { return result((await reply.WaitAsync(timeout, ct)).Body); }
        catch (TimeoutException) { return -1; }
    }

    private async Task<int> CreateGuildAsync(BotClient bot, string baseName, CancellationToken ct)
    {
        // GuildNameNumNew allows 2-7 characters: a taken name gets a digit.
        string name = _guildNameTry == 0 ? baseName : baseName[..Math.Min(baseName.Length, 6)] + (char)('1' + _guildNameTry % 9);
        using var reply = bot.Inbox.Expect(e => e.IsGame(GuildService.Create));
        await bot.SendGameAsync(GuildService.Create, new GuildCreateReq
        {
            Name = name, Icon = new GuildHeadIcon { BackgroundType = 100101, IconType = 200101 },
        }).WaitAsync(SendTimeout, ct);
        int result = await ResultAsync(reply, b => GuildCreateResp.Parser.ParseFrom(b).Result, TimeSpan.FromSeconds(15), ct);
        for (int i = 0; result == 0 && i < 10 && GuildId == 0; i++) await PauseAsync(bot, TimeSpan.FromMilliseconds(500), ct);
        return result;
    }

    /// <summary>The recruit settings (GuildEditReq type 10): no level limit and auto-accept, so applications join at once.</summary>
    private async Task<int> EditAutoAcceptAsync(BotClient bot, CancellationToken ct)
    {
        using var reply = bot.Inbox.Expect(e => e.IsGame(GuildService.Edit) && GuildEditResp.Parser.ParseFrom(e.Body).Tp == 10);
        await bot.SendGameAsync(GuildService.Edit, new GuildEditReq
        {
            Tp = (GuildEditReq.Types.EditType)10, Announce = "Guerra simulada", JoinLevelLimit = 0, AutoAcceptApply = true,
        }).WaitAsync(SendTimeout, ct);
        return await ResultAsync(reply, b => GuildEditResp.Parser.ParseFrom(b).Result, TimeSpan.FromSeconds(15), ct);
    }

    private async Task<int> ApplyGuildAsync(BotClient bot, long guild, CancellationToken ct)
    {
        using var reply = bot.Inbox.Expect(e => e.IsGame(GuildService.Apply));
        await bot.SendGameAsync(GuildService.Apply, new GuildEnterApplyReq { GuildIds = { guild } }).WaitAsync(SendTimeout, ct);
        return await ResultAsync(reply, b =>
        {
            var response = GuildEnterApplyResp.Parser.ParseFrom(b);
            return response.Result != 0 ? response.Result : response.ApplyResult.FirstOrDefault()?.Result ?? -1;
        }, TimeSpan.FromSeconds(15), ct);
    }

    private async Task LeaveGuildAsync(BotClient bot, CancellationToken ct)
    {
        using var reply = bot.Inbox.Expect(e => e.IsGame(GuildService.Leave));
        await bot.SendGameBytesAsync(GuildService.Leave, []).WaitAsync(SendTimeout, ct);
        int result = await ResultAsync(reply, b => GuildMemberLeaveResp.Parser.ParseFrom(b).Result, TimeSpan.FromSeconds(15), ct);
        if (result != 0) Host.WarFailed(Account, "sair-guilda", result);
    }

    /// <summary>
    /// The founder's Tactics panel (32282): the side's players numbered 2-9 get the Firearm slots 1-8 and 12-19 the
    /// Vehicle slots 1-8, so they may build cannons and drive vehicles (slots 9 and 10 stay for real players).
    /// </summary>
    private async Task AssignMachinesAsync(BotClient bot, CancellationToken ct)
    {
        _assignNext = DateTime.UtcNow.AddSeconds(20);
        if (War is not { } order) return;
        long guild = GuildId;
        foreach (var player in Host.WarPlayers())
        {
            int index = player.Profile.Index;
            if (player.War?.Side != order.Side || player.RoleId == 0 || player.GuildId != guild || _assigned.ContainsKey(index)) continue;
            int slot = index % 1000;
            var (type, bag) = slot is >= 2 and <= 9 ? (WartimeToolType.Firearm, slot - 1)
                : slot is >= 12 and <= 19 ? (WartimeToolType.Vehicle, slot - 11) : (WartimeToolType.None, 0);
            if (type == WartimeToolType.None)
            {
                _assigned[index] = type;
                continue;
            }
            using var reply = bot.Inbox.Expect(e => e.IsGame(GuildService.ToolAssign));
            await bot.SendGameAsync(GuildService.ToolAssign, new GuildWartimeToolBelongOperReq
            {
                ToolType = type, BagIndex = bag, RoleGuildId = player.RoleId, DungeonId = (int)SiegeScene,
            }).WaitAsync(SendTimeout, ct);
            int result = await ResultAsync(reply, b => GuildWartimeToolBelongOperRsp.Parser.ParseFrom(b).Result, TimeSpan.FromSeconds(30), ct);
            if (result is 0 or ErrorCodes.Parameter) _assigned[index] = type; // Parameter: it holds a slot already
            else
            {
                Host.WarFailed(Account, "permissao-maquina", result);
                return;
            }
        }
    }

    // ------------------------------------------------------------------ waiting and entering

    private async Task WarWaitAsync(BotClient bot, WorldReducer reducer, WarOrder order, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        long guild = GuildId;
        if (guild == 0 || (!order.Leader && guild != WarTargetGuild(order)))
        {
            _warPhase = WarPhase.Guild;
            return;
        }
        if (order.Leader && now >= _assignNext)
        {
            await AssignMachinesAsync(bot, ct);
            return;
        }
        if (reducer.Location.SceneEntry == SiegeScene)
        {
            StartBattle();
            return;
        }
        if (Host.WarCity == 0 || now < _warNextTry)
        {
            // Waiting in town: first a spot of its own up to 60 m away (a whole war standing on the born point floods
            // every client near it with walks and models), then mostly still, a few steps now and then.
            if (!_spread)
            {
                _spread = true;
                if (_nav?.Destination(_r, _x, _y, _z, 60) is { } spot) await WalkToAsync(bot, spot, ct);
                return;
            }
            if (_r.NextDouble() < 0.1 && _nav?.Destination(_r, _x, _y, _z, 4) is { } step) await WalkToAsync(bot, step, ct);
            else if (_r.NextDouble() < 0.2) await TurnAsync(bot, _r.Next(0, 360), ct);
            await PauseAsync(bot, TimeSpan.FromSeconds(_r.Next(5, 12)), ct);
            return;
        }
        _warPhase = WarPhase.Entering;
    }

    private async Task WarEnterAsync(BotClient bot, WorldReducer reducer, CancellationToken ct)
    {
        int city = Host.WarCity;
        int result = -1;
        if (city > 0)
        {
            await Host.WarEntryGate.WaitAsync(ct);
            try { result = await EnterSiegeAsync(bot, reducer, city, ct); }
            finally { Host.WarEntryGate.Release(); }
        }
        if (result == 0)
        {
            Host.CountWarEntry();
            StartBattle();
            return;
        }
        if (city > 0) Host.WarFailed(Account, "entrar-cerco", result);
        _warPhase = WarPhase.Waiting;
        _warNextTry = DateTime.UtcNow.AddSeconds(_r.Next(20, 45));
    }

    /// <summary>30400 for the siege of <paramref name="city"/>, then the castle scene's load (as the S5 load test does).</summary>
    private async Task<int> EnterSiegeAsync(BotClient bot, WorldReducer reducer, int city, CancellationToken ct)
    {
        _switching = true;
        try
        {
            using var reply = bot.Inbox.Expect(e => e.IsGame(DungeonService.Enter));
            using var switched = bot.Inbox.Expect(e => e.IsGame(MsgId.SwitchSceneLine));
            using var self = bot.Inbox.Expect(e => e.IsGame(MsgId.EntitySelf));
            using var end = bot.Inbox.Expect(e => e.IsGame(MsgId.SelfInfoEndFrameNotify));
            await bot.SendGameAsync(DungeonService.Enter, new EnterDungeonReq
            {
                SceneEntry = SiegeScene, InitData = new DungeonInitData { SiegeInitData = new SiegeInitData { CityId = city } },
            }).WaitAsync(SendTimeout, ct);
            // An answer that comes too late (a busy server) is a try for later; a switch that still comes is followed
            // (FollowSwitchAsync).
            int result = await ResultAsync(reply, b => EnterDungeonRsp.Parser.ParseFrom(b).Result, TimeSpan.FromSeconds(30), ct);
            if (result != 0) return result;
            var scene = SwitchSceneLineResp.Parser.ParseFrom((await switched.WaitAsync(TimeSpan.FromSeconds(30), ct)).Body);
            await FinishSceneAsync(bot, reducer, scene.SceneEntry, self, end, ct);
            return 0;
        }
        finally { _switching = false; }
    }

    private void StartBattle()
    {
        var now = DateTime.UtcNow;
        _warPhase = WarPhase.Battle;
        _route = null;
        _foe = 0;
        _stuck = 0;
        _onCannon = _riding = false;
        _cannonUid = _vehicleUid = 0;
        _vehicleTries = 0;
        _cannonIdle = 0;
        _distanceMisses = 0;
        _machineNext = now.AddSeconds(_r.Next(5, 25));
        _nextBuff = now.AddSeconds(_r.Next(2, 10));
        _retreatNext = now;
        State = FakeState.Fighting;
    }

    private void LeaveBattle()
    {
        _objectives.Clear();
        _vitals.Clear();
        _fallen.Clear();
        _hurt.Clear();
        _walls = [];
        _siegeStage = -1;
        _route = null;
        _foe = 0;
        _ram = null;
        _onCannon = _riding = false;
        _cannonUid = _vehicleUid = 0;
        _ignored.Clear();
    }

    // ------------------------------------------------------------------ the battle

    private async Task BattleAsync(BotClient bot, WorldReducer reducer, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        if (reducer.Location.SceneEntry != SiegeScene)
        {
            LeaveBattle();
            _warPhase = WarPhase.Waiting;
            _warNextTry = now.AddSeconds(_r.Next(10, 30));
            return;
        }
        ulong[] expired = [.. _ignored.Where(x => x.Value < now).Select(x => x.Key)];
        foreach (var key in expired) _ignored.Remove(key);
        if (_dead)
        {
            await WarReviveAsync(bot, reducer, ct);
            return;
        }
        if (War is { Leader: true } && now >= _assignNext)
        {
            await AssignMachinesAsync(bot, ct);
            return;
        }
        var view = Look(reducer, now);
        if (_onCannon)
        {
            await CannonStepAsync(bot, view, now, ct);
            return;
        }
        if (_riding)
        {
            await RideStepAsync(bot, view, now, ct);
            return;
        }
        if (IsGunner && now >= _machineNext && (_camp == 2 || _siegeStage >= GateStage) && await GunnerSetupAsync(bot, view, now, ct)) return;
        if (IsDriver && now >= _machineNext && (_camp == 2 || _siegeStage >= GateStage) && await DriverSetupAsync(bot, view, now, ct)) return;
        if (IsHealer && await HealAsync(bot, view, now, ct)) return;
        // Low on HP with an enemy close: step back towards the own lines (healers are there) before going on.
        if (_maxHp > 0 && _hp > 0 && _hp < _maxHp * 30 / 100 && view.NearestEnemy < 10 && now >= _retreatNext)
        {
            _retreatNext = now.AddSeconds(15);
            if (_r.NextDouble() < 0.2) await SayAsync(bot, "recuando, me curem", ct);
            await StepBackAsync(bot, view, 10f, ct);
            return;
        }
        if (now >= _nextBuff && await BuffAsync(bot, now, ct)) return;
        if (PickTarget(view) is { } target)
        {
            await EngageAsync(bot, view, target, now, ct);
            return;
        }
        await AdvanceAsync(bot, ct);
    }

    private WarView Look(WorldReducer reducer, DateTime now)
    {
        var view = new WarView();
        foreach (var p in reducer.Players())
        {
            if (p.State == EntityState.Dead || Fallen(p.Uuid, now)) continue;
            var pos = p.MovementInfo.Pos;
            var unit = new WarUnit(p.Uuid, pos.X, pos.Y, pos.Z, true, 0, HpShare(p));
            if (p.EntityCamp != _camp && p.EntityCamp != 0)
            {
                if (_ignored.ContainsKey(p.Uuid)) continue;
                view.Enemies.Add(unit);
                view.NearestEnemy = MathF.Min(view.NearestEnemy, Distance(pos));
            }
            else view.Allies.Add(unit);
        }
        int stage = _siegeStage;
        foreach (var m in reducer.Monsters())
        {
            long npc = (long)m.EntityTemplateID;
            if (m.State == EntityState.Dead || Fallen(m.Uuid, now)) continue;
            if (npc == RamNpc && m.EntityCamp == _camp) _ram = (m.MovementInfo.Pos.X, m.MovementInfo.Pos.Z, now);
            if (Scenery.Contains(npc) || m.EntityCamp == _camp || m.EntityCamp == 0 || _ignored.ContainsKey(m.Uuid)) continue;
            // The crystal can be hit in the Crystal stage only, the gate once Prepare is over.
            if ((npc == CrystalNpc && stage != CrystalStage) || (npc == GateNpc && stage < GateStage)) continue;
            var pos = m.MovementInfo.Pos;
            view.Targets.Add(new WarUnit(m.Uuid, pos.X, pos.Y, pos.Z, false, npc, HpShare(m)));
        }
        foreach (var n in reducer.Npcs())
        {
            long npc = (long)n.EntityTemplateID;
            if (!Host.IsVehicleNpc(npc) && !Host.IsCannonNpc(npc)) continue;
            view.Machines.Add(n);
            if (n.State == EntityState.Dead || Fallen(n.Uuid, now) || n.EntityCamp == _camp || n.EntityCamp is not (1 or 2)
                || _ignored.ContainsKey(n.Uuid)) continue;
            var pos = n.MovementInfo.Pos;
            view.Targets.Add(new WarUnit(n.Uuid, pos.X, pos.Y, pos.Z, false, npc, HpShare(n)));
        }
        return view;
    }

    /// <summary>
    /// The enemy to fight: players within 24 m before castle units within 30 m, nearer and more hurt ones first, the
    /// camp's focus and the current enemy preferred. A hurt enemy player becomes the camp's focus for a few seconds.
    /// </summary>
    private WarUnit? PickTarget(WarView view)
    {
        ulong focus = Host.WarFocus(_camp);
        WarUnit? best = null;
        float bestScore = float.MaxValue;
        foreach (var enemy in view.Enemies)
        {
            float d = DistanceTo(enemy);
            if (d > 24) continue;
            float score = d - (1 - enemy.Hp) * 8 - (enemy.Uid == focus ? 8 : 0) - (enemy.Uid == _foe ? 5 : 0);
            if (score < bestScore) { bestScore = score; best = enemy; }
        }
        foreach (var unit in view.Targets)
        {
            float d = DistanceTo(unit);
            if (d > 30) continue;
            float score = d + UnitBias(unit.Npc) - (unit.Uid == _foe ? 5 : 0);
            if (score < bestScore) { bestScore = score; best = unit; }
        }
        if (best is { Player: true, Hp: < 0.45f }) Host.SetWarFocus(_camp, best.Uid);
        return best;
    }

    /// <summary>How much less a castle unit matters than an enemy player at the same distance (metres added).</summary>
    private float UnitBias(long npc) => npc switch
    {
        CrystalNpc => -12,                                   // the crystal decides the siege
        RamNpc or LadderNpc or LeftLadderNpc => -2,          // the defenders stop the ram and the ladders
        KnightGuardianNpc => 0,
        BowGuardianNpc or BarricadeNpc => 3,
        ArcherNpc => 5,
        GateNpc => 14,                                       // millions of HP: the ram's work, hit when nothing else is near
        _ => Host.IsVehicleNpc(npc) || Host.IsCannonNpc(npc) ? 2 : 6,
    };

    /// <summary>
    /// One step against <paramref name="target"/>: closer while the way leads closer, else the chosen skill or normal attack
    /// (the server measures from the target's edge, so a castle unit or a defender on the wall may be in reach from where the
    /// ground ends). Out of reach three times in a row: another target for a few seconds.
    /// </summary>
    private async Task EngageAsync(BotClient bot, WarView view, WarUnit target, DateTime now, CancellationToken ct)
    {
        bool newFoe = target.Uid != _foe;
        _foe = target.Uid;
        if (newFoe) _distanceMisses = 0;
        // Ranged classes and healers keep their distance from the enemy front line.
        if ((IsRanged || IsHealer) && view.NearestEnemy < 4f)
        {
            await StepBackAsync(bot, view, 6f, ct);
            return;
        }
        int crowd = view.Enemies.Count(e => Dist2(e.X, e.Z, target.X, target.Z) < 25) + view.Targets.Count(t => Dist2(t.X, t.Z, target.X, target.Z) < 25);
        var action = ChooseWarAction(now, crowd);
        float reach = action.Release == 2 ? 3f : action.Range;
        if (DistanceTo(target) > reach - 0.3f && await ApproachAsync(bot, target.X, target.Z, Math.Max(1.2f, reach - 1f), ct)) return;
        await StopAsync(bot, ct);
        await TurnAsync(bot, BotClient.FaceTowards(_x, _z, target.X, target.Z), ct);
        if (newFoe) await bot.SendGameAsync(CombatService.LockEnemy, new LockEnemyReq { EnemyId = (long)target.Uid }).WaitAsync(SendTimeout, ct);
        int result = await CastAsync(bot, action, target.Uid, target.X, target.Y, target.Z, ct);
        var pause = TimeSpan.FromMilliseconds(_r.Next(700, 1100));
        if (result == ErrorCodes.SkillDistance && ++_distanceMisses >= 3)
        {
            _ignored[target.Uid] = now.AddSeconds(target.Player ? 6 : 12);
            _foe = 0;
            _distanceMisses = 0;
        }
        if (result == 0)
        {
            _distanceMisses = 0;
            Host.CountWarCast();
            if (!action.Normal)
            {
                _skillReady[action.Group] = now.AddMilliseconds(Math.Max(action.CooldownMs, 1200) + action.ChantMs + _r.Next(0, 1500));
                pause = TimeSpan.FromMilliseconds(action.ChantMs + _r.Next(500, 900));
            }
        }
        else if (!action.Normal)
        {
            switch (result)
            {
                case ErrorCodes.SkillCooldown: _skillReady[action.Group] = now.AddMilliseconds(Math.Max(action.CooldownMs, 2000)); break;
                case ErrorCodes.SkillDistance: break; // closer next step
                case ErrorCodes.SkillNotLearned or ErrorCodes.SkillUnknown or ErrorCodes.SkillPassive or ErrorCodes.SkillRequirements:
                    _skillBroken.Add(action.Group);
                    break;
                case ErrorCodes.SkillTargetInvalid:
                    _ignored[target.Uid] = now.AddSeconds(6);
                    break;
                default: _skillsRestUntil = now.AddSeconds(10); break; // out of SP or similar: normal hits for a while
            }
            pause = TimeSpan.FromMilliseconds(_r.Next(200, 400));
        }
        else if (result != ErrorCodes.SkillDistance)
        {
            _ignored[target.Uid] = now.AddSeconds(6); // dead, gone or not a valid target now
            _foe = 0;
        }
        await PauseAsync(bot, pause, ct);
    }

    /// <summary>
    /// The skill to cast now: a ready attack skill most of the time, a third-class (rank 5) one four times as likely as a
    /// first-class one, and an area skill three times as likely when the target stands in a crowd; else the normal attack.
    /// </summary>
    private FakeSkill ChooseWarAction(DateTime now, int crowd)
    {
        var normal = Host.NormalAttack(Profile.Job);
        if (now < _skillsRestUntil || _r.NextDouble() < 0.15) return normal;
        List<FakeSkill> ready = [.. Learned.Where(s => s.Attack && !_skillBroken.Contains(s.Group) && _skillReady.GetValueOrDefault(s.Group) <= now)];
        if (ready.Count == 0) return normal;
        double Weight(FakeSkill s) => (s.Rank >= 5 ? 4 : s.Rank == 4 ? 2 : 1) * (crowd >= 3 && s.Release is 2 or 7 or 8 ? 3 : 1);
        double roll = _r.NextDouble() * ready.Sum(Weight);
        foreach (var skill in ready)
        {
            roll -= Weight(skill);
            if (roll <= 0) return skill;
        }
        return ready[^1];
    }

    private async Task<int> CastAsync(BotClient bot, FakeSkill action, ulong target, float x, float y, float z, CancellationToken ct)
    {
        var cast = new CastSkillReq { SkillId = action.RowId, Face = _face };
        if (action.Release is 7 or 8) cast.TargetPos = new Vec3 { X = x, Y = y, Z = z }; // on the ground under it
        if (action.Release != 2) cast.TargetId = (long)target;                            // 2: around the caster
        using var reply = bot.Inbox.Expect(e => e.IsGame(CombatService.UseSkill));
        await bot.SendGameAsync(CombatService.UseSkill, cast).WaitAsync(SendTimeout, ct);
        try { return CastSkillRsp.Parser.ParseFrom((await reply.WaitAsync(TimeSpan.FromSeconds(5), ct)).Body).Result; }
        catch (TimeoutException) { return -1; }
    }

    /// <summary>
    /// An Archbishop heals: itself below 70%, else the most hurt ally within 20 m (an ally hit in the last 3 s counts
    /// as hurt when its HP is not shown). True when it acted.
    /// </summary>
    private async Task<bool> HealAsync(BotClient bot, WarView view, DateTime now, CancellationToken ct)
    {
        List<FakeSkill> heals = [.. Learned.Where(s => s.Heal && !_skillBroken.Contains(s.Group) && _skillReady.GetValueOrDefault(s.Group) <= now)];
        if (heals.Count == 0) return false;
        float worst = 0.7f;
        bool self = _maxHp > 0 && _hp > 0 && _hp < _maxHp * 7 / 10;
        if (self) worst = _hp / (float)_maxHp;
        WarUnit? patient = null;
        if (!_healAllyBroken)
            foreach (var ally in view.Allies)
            {
                if (DistanceTo(ally) > 20) continue;
                float hp = ally.Hp < 1 ? ally.Hp : _hurt.TryGetValue(ally.Uid, out var hit) && now - hit < TimeSpan.FromSeconds(3) ? 0.69f : 1f;
                if (hp >= worst) continue;
                worst = hp;
                patient = ally;
                self = false;
            }
        if (!self && patient == null) return false;
        var heal = heals.FirstOrDefault(h => h.Release == 1) ?? heals[0];
        float tx = patient?.X ?? _x, ty = patient?.Y ?? _y, tz = patient?.Z ?? _z;
        float need = heal.Release == 2 ? 4f : heal.Range - 0.5f;
        if (patient != null && DistanceTo(patient) > need && await ApproachAsync(bot, tx, tz, Math.Max(1.5f, need - 1f), ct)) return true;
        await StopAsync(bot, ct);
        if (patient != null) await TurnAsync(bot, BotClient.FaceTowards(_x, _z, tx, tz), ct);
        ulong who = patient?.Uid ?? (ulong)_roleId;
        int result = await CastAsync(bot, heal, who, tx, ty, tz, ct);
        if (result == 0)
        {
            Host.CountWarHeal();
            _skillReady[heal.Group] = now.AddMilliseconds(Math.Max(heal.CooldownMs, 1500) + heal.ChantMs + _r.Next(0, 800));
            await PauseAsync(bot, TimeSpan.FromMilliseconds(heal.ChantMs + _r.Next(500, 800)), ct);
            return true;
        }
        if (result == ErrorCodes.SkillTargetInvalid && patient != null) _healAllyBroken = true;
        else if (result is ErrorCodes.SkillNotLearned or ErrorCodes.SkillUnknown or ErrorCodes.SkillPassive or ErrorCodes.SkillRequirements)
            _skillBroken.Add(heal.Group);
        else if (result == ErrorCodes.SkillCooldown) _skillReady[heal.Group] = now.AddSeconds(2);
        return false;
    }

    // ------------------------------------------------------------------ moving in the castle

    /// <summary>The standing air walls as a cell test for the route search and each step (null: none stands).</summary>
    private Func<int, int, bool>? Walls() => Host.WallTest(SiegeScene, _walls);

    private static float Dist2(float ax, float az, float bx, float bz) => (ax - bx) * (ax - bx) + (az - bz) * (az - bz);

    private float DistanceTo(WarUnit unit) => MathF.Sqrt(Dist2(unit.X, unit.Z, _x, _z));

    /// <summary>Ends a walk with a stop step (before a cast or an interaction), once.</summary>
    private async Task StopAsync(BotClient bot, CancellationToken ct)
    {
        if (!_moving) return;
        _moving = false;
        await SendWalkAsync(bot, _x, _y, _z, true, ct);
    }

    /// <summary>
    /// Towards a point by the route search (nav grid and standing walls), a few steps per call so the player looks
    /// around again between them; a route is kept for 5 s while its goal stays put. Stuck three times: a new route
    /// after a step aside. False when there is nothing to walk: no way at all, or the way ends short of the point (a wall
    /// stands in it, or the point is up on the city wall) and the player already stands at its end.
    /// </summary>
    private async Task<bool> ApproachAsync(BotClient bot, float tx, float tz, float stopShort, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var walls = Walls();
        if (_route == null || now - _routeAt > TimeSpan.FromSeconds(5) || Dist2(tx, tz, _routeGoalX, _routeGoalZ) > 16)
        {
            _route = _nav?.Route(_x, _y, _z, tx, tz, walls);
            _routeAt = now;
            _routeGoalX = tx;
            _routeGoalZ = tz;
        }
        if (_route is not { Count: > 0 } route)
        {
            await StopAsync(bot, ct);
            return false;
        }
        var end = route[^1];
        float reach = stopShort + 2f;
        if (Dist2(end.X, end.Z, tx, tz) > reach * reach && Dist2(end.X, end.Z, _x, _z) < 2.25f)
        {
            await StopAsync(bot, ct);
            return false;
        }
        while (route.Count > 1 && Dist2(route[0].X, route[0].Z, _x, _z) < 1f) route.RemoveAt(0);
        var next = route[0];
        bool last = route.Count == 1;
        float fromX = _x, fromZ = _z;
        await WarWalkAsync(bot, next, 3, last ? stopShort : 0, ct);
        if (!last && Dist2(next.X, next.Z, _x, _z) < 1f) route.RemoveAt(0);
        if (Dist2(fromX, fromZ, _x, _z) < 0.25f && ++_stuck >= 3)
        {
            _stuck = 0;
            _route = null;
            float angle = (float)(_r.NextDouble() * Math.PI * 2);
            float sx = _x + MathF.Sin(angle) * 3, sz = _z + MathF.Cos(angle) * 3;
            if (_nav?.Height(sx, sz, _y) is { } sy && (_nav?.CanWalkClear(_x, _y, _z, sx, sz, walls) ?? false))
                await WarWalkAsync(bot, (sx, sy, sz), 1, 0, ct);
        }
        return true;
    }

    /// <summary>Up to <paramref name="maxSteps"/> 3 m steps towards a point, each on walkable ground and clear of the walls.</summary>
    private async Task WarWalkAsync(BotClient bot, (float X, float Y, float Z) target, int maxSteps, float stopShort, CancellationToken ct)
    {
        var walls = Walls();
        for (int i = 0; i < maxSteps && !ct.IsCancellationRequested && !_dead; i++)
        {
            float dx = target.X - _x, dz = target.Z - _z, total = MathF.Sqrt(dx * dx + dz * dz), distance = total - stopShort;
            if (distance <= 0.05f || total < 0.01f)
            {
                await StopAsync(bot, ct);
                return;
            }
            bool last = distance <= StepMeters;
            float step = MathF.Min(StepMeters, distance);
            float x = _x + dx / total * step, z = _z + dz / total * step;
            if (_nav != null && (_nav.Height(x, z, _y) is not { } ground || !_nav.CanWalkClear(_x, _y, _z, x, z, walls)))
            {
                await StopAsync(bot, ct);
                return;
            }
            float y = _nav?.Height(x, z, _y) ?? _y;
            _face = BotClient.FaceTowards(_x, _z, x, z);
            await SendWalkAsync(bot, x, y, z, last, ct);
            _x = x;
            _y = y;
            _z = z;
            _moving = !last;
            await KeepAliveAsync(bot, ct);
            if (last) return;
            await Task.Delay(StepInterval, ct);
        }
    }

    /// <summary>Steps away from the nearest enemy (towards the own side when none is shown).</summary>
    private async Task StepBackAsync(BotClient bot, WarView view, float distance, CancellationToken ct)
    {
        var nearest = view.Enemies.OrderBy(DistanceTo).FirstOrDefault();
        // Without a visible enemy: back towards the own side (attackers came from the east, defenders hold the west).
        float fromX = nearest?.X ?? (_camp == 1 ? _x - 10 : _x + 10), fromZ = nearest?.Z ?? _z;
        float away = BotClient.FaceTowards(fromX, fromZ, _x, _z) * MathF.PI / 180f;
        var walls = Walls();
        for (int attempt = 0; attempt < 6; attempt++)
        {
            float angle = away + (float)(_r.NextDouble() - 0.5) * (attempt + 1) * 0.5f;
            float tx = _x + MathF.Sin(angle) * distance, tz = _z + MathF.Cos(angle) * distance;
            if (_nav?.Height(tx, tz, _y) is { } ty && (_nav?.CanWalkClear(_x, _y, _z, tx, tz, walls) ?? false))
            {
                await WarWalkAsync(bot, (tx, ty, tz), 4, 0, ct);
                return;
            }
        }
        await PauseAsync(bot, TimeSpan.FromMilliseconds(500), ct);
    }

    /// <summary>Nothing to fight in reach: to the side's rally point of the stage (each player keeps its own spot in it).</summary>
    private async Task AdvanceAsync(BotClient bot, CancellationToken ct)
    {
        var (gx, gz) = RallyPoint();
        if (Dist2(gx, gz, _x, _z) < 9 || !await ApproachAsync(bot, gx, gz, 1f, ct))
        {
            // There (or as near as the way goes): facing the enemy side, like people waiting for the next push.
            await StopAsync(bot, ct);
            if (_r.NextDouble() < 0.25) await TurnAsync(bot, _camp == 1 ? 270f : 90f, ct);
            await PauseAsync(bot, TimeSpan.FromMilliseconds(_r.Next(700, 1500)), ct);
        }
    }

    /// <summary>
    /// Where the player stands when nothing is in reach (siege 2230101 scene editor points: attackers' camp 75 at x 758,
    /// ladder ends 18/30 at the city wall (613, z 93/207), ram end 35 (610, 150) before the gate 36 (600, 150), the wall
    /// walk 304-307 (x 587, z 88-211), the Knight Guardians 41-43 (x 367-380, z 68/150/226), the crystal 44 (152, 120) and
    /// the defenders' born point 73 (134, 130)). Lanes north, middle and south by the player's number; ranged classes and
    /// healers a few metres behind the front line. The attackers' middle lane walks with the Battering Ram until it reaches
    /// its end: it only moves while an attacker stands within 32 m of it.
    /// </summary>
    private (float X, float Z) RallyPoint()
    {
        int slot = WarSlot, lane = slot % 3;
        float jx = slot * 37 % 9 - 4, jz = slot * 53 % 11 - 5;
        bool back = IsRanged || IsHealer;
        float laneZ = lane switch { 0 => 100, 2 => 200, _ => 150 };
        int stage = Math.Max(_siegeStage, PrepareStage);
        if (_camp == 1)
        {
            switch (stage)
            {
                case PrepareStage:
                    return (738 + jx, 150 + jz * 2);
                case GateStage:
                    if (lane == 1 && _ram is { } ram && DateTime.UtcNow - ram.At < TimeSpan.FromSeconds(15) && ram.X > 616)
                        return (ram.X + (back ? 12 : 5) + jx / 2, ram.Z + jz);
                    return ((back ? 640 : 621) + jx, laneZ + jz);
                case StrongholdStage:
                    {
                        // The Knight Guardian of the lane, else any standing one: the next stage opens when all three fall.
                        var guardian = _objectives.Values.Where(o => o.ConfigId == KnightGuardianNpc && o.Pos != null)
                            .OrderBy(o => MathF.Abs(o.Pos.Z - laneZ)).FirstOrDefault();
                        return guardian != null ? (guardian.Pos.X + (back ? 14 : 5) + jx, guardian.Pos.Z + jz) : (440 + jx, laneZ + jz);
                    }
                default:
                    return ((back ? 176 : 160) + jx, 120 + jz);
            }
        }
        switch (stage)
        {
            case PrepareStage or GateStage:
                // Ranged classes and healers on the wall walk above the ladder lanes and the gate, the others behind the gate.
                return back ? (587, lane switch { 0 => 100, 2 => 200, _ => 150 } + jz / 2) : (578 + jx / 2, 150 + jz * 2);
            case StrongholdStage:
                return ((back ? 350 : 362) + jx, lane switch { 0 => 70, 2 => 226, _ => 150 } + jz);
            default:
                return ((back ? 140 : 168) + jx, 120 + jz);
        }
    }

    // ------------------------------------------------------------------ death

    private async Task WarReviveAsync(BotClient bot, WorldReducer reducer, CancellationToken ct)
    {
        State = FakeState.Dead;
        _onCannon = _riding = false;
        _route = null;
        _foe = 0;
        _moving = false;
        if (Host.Talkative(Profile) && _r.NextDouble() < 0.15) await SayAsync(bot, DeathLines[_r.Next(DeathLines.Length)], ct);
        await PauseAsync(bot, TimeSpan.FromSeconds(_r.Next(4, 9)), ct);
        if (!_dead)
        {
            State = FakeState.Fighting;
            return;
        }
        using var ack = bot.Inbox.Expect(e => e.IsGame(CombatService.Relive));
        using var teleport = bot.Inbox.Expect(e => e.IsGame(GameAoiSink.MovementChange) && MovementChangeNtf.Parser.ParseFrom(e.Body).Uid == _roleId);
        await bot.SendGameAsync(CombatService.Relive, new EntityReliveReq()).WaitAsync(SendTimeout, ct);
        int result;
        try { result = EntityReliveAck.Parser.ParseFrom((await ack.WaitAsync(TimeSpan.FromSeconds(10), ct)).Body).Result; }
        catch (TimeoutException) { result = -1; }
        if (result != 0)
        {
            await PauseAsync(bot, TimeSpan.FromSeconds(3), ct);
            return;
        }
        _dead = false;
        _hp = _maxHp;
        try { SetPosition(MovementChangeNtf.Parser.ParseFrom((await teleport.WaitAsync(TimeSpan.FromSeconds(5), ct)).Body).MovementInfo?.Pos); }
        catch (TimeoutException) { SetPosition(reducer.SelfPosition()); }
        State = FakeState.Fighting;
    }

    // ------------------------------------------------------------------ cannons

    /// <summary>
    /// A gunner's cannon: board its standing one, else build one at its spot (32270 reserve, 32271 build; the build takes
    /// _iBuildTime 3 s). True when it acted; false hands the step back to the fight.
    /// </summary>
    private async Task<bool> GunnerSetupAsync(BotClient bot, WarView view, DateTime now, CancellationToken ct)
    {
        _cannon ??= Host.Cannon(CannonItems[WarSlot % CannonItems.Length]);
        if (_cannon is not { } spec || spec.FireButton == 0 || spec.Interaction == 0)
        {
            _machineNext = DateTime.MaxValue;
            return false;
        }
        if (view.NearestEnemy < 12) return false; // enemies close: fight first
        var mine = view.Machines.FirstOrDefault(m => m.Owner == _roleId && (long)m.EntityTemplateID == spec.NpcId && m.State != EntityState.Dead);
        if (mine != null && !CannonOutOfPlace(mine, spec))
        {
            _cannonUid = (long)mine.Uuid;
            var pos = mine.MovementInfo.Pos;
            if (Distance(pos) > 2.5f && await ApproachAsync(bot, pos.X, pos.Z, 1.5f, ct)) return true;
            int code = await InteractAsync(bot, _cannonUid, spec.Interaction, ct);
            if (code == 0)
            {
                _onCannon = true;
                _cannonIdle = 0;
                Host.CountWarRide();
                return true;
            }
            _machineNext = now.AddSeconds(20);
            return false;
        }
        if (CannonSpot(spec) is not { } spot)
        {
            _machineNext = now.AddSeconds(30);
            return false;
        }
        if (Dist2(spot.X, spot.Z, _x, _z) > 9 && await ApproachAsync(bot, spot.X, spot.Z, 0.5f, ct)) return true;
        int reserved = await PrepayAsync(bot, spec.Item, ct);
        if (reserved != 0)
        {
            Host.WarFailed(Account, "reservar-canhao", reserved);
            _machineNext = now.AddSeconds(60);
            return false;
        }
        float face = _camp == 1 ? 270f : 90f; // the attackers aim west at the castle, the defenders east at the camp
        float rad = face * MathF.PI / 180f;
        float bx = _x + MathF.Sin(rad) * 2f, bz = _z + MathF.Cos(rad) * 2f;
        float by = _nav?.Height(bx, bz, _y) ?? _y;
        int built = await BuildCannonAsync(bot, bx, by, bz, face, ct);
        if (built != 0)
        {
            await bot.SendGameBytesAsync(GuildService.CancelPrepay, []).WaitAsync(SendTimeout, ct);
            Host.WarFailed(Account, "montar-canhao", built);
            _cannonShift++; // another spot next time (530: too close to another cannon, or off the ground)
            _machineNext = now.AddSeconds(30);
            return false;
        }
        await PauseAsync(bot, TimeSpan.FromMilliseconds(3600), ct);
        return true;
    }

    /// <summary>A cannon left far behind the front (the stage moved on): out of its range of the stage's fight.</summary>
    private bool CannonOutOfPlace(EntityBase cannon, WarCannon spec) =>
        CannonSpot(spec) is { } spot && Dist2(spot.X, spot.Z, cannon.MovementInfo.Pos.X, cannon.MovementInfo.Pos.Z) > 40 * 40;

    /// <summary>
    /// Where a gunner builds: the middle of the cannon's range (GVGCannonConfig's aim indicator: the Arm Cannon 50-110 m,
    /// the Flying Raven up to 50, the Anti-Aircraft Gun 20-85, the Rocket Launcher 90-140) from the stage's fight (the gate
    /// and the wall walk, the guardians, the crystal for the attackers; the ram and the ladders, the gateway, the way to
    /// the crystal for the defenders), spread over z by the player's number, on the own side of the city wall before
    /// the gate falls.
    /// </summary>
    private (float X, float Z)? CannonSpot(WarCannon spec)
    {
        var (min, max) = CannonRange(spec);
        float standoff = Math.Clamp((min + max) / 2f, 20f, 100f);
        int stage = Math.Max(_siegeStage, PrepareStage);
        float x;
        if (_camp == 1)
        {
            float zone = stage <= GateStage ? 585 : stage == StrongholdStage ? 400 : 155;
            x = zone + standoff;
            if (stage <= GateStage) x = Math.Clamp(x, 615, 700);
        }
        else
        {
            float zone = stage <= GateStage ? 630 : stage == StrongholdStage ? 470 : 250;
            x = zone - standoff;
            if (stage <= GateStage) x = Math.Min(x, 575);
            x = Math.Max(x, 140);
        }
        float z = 110 + (WarSlot * 13 + _cannonShift * 9) % 80;
        // The spot, else the nearest walkable one on a spiral around it (the same every time: a walk keeps its goal).
        for (int attempt = 0; attempt < 16; attempt++)
        {
            float tx = x + MathF.Cos(attempt * 2.4f) * attempt * 1.5f, tz = z + MathF.Sin(attempt * 2.4f) * attempt * 1.5f;
            if (_nav?.Height(tx, tz, 15) is not null) return (tx, tz);
        }
        return null;
    }

    /// <summary>The cannon's reach (m) by its aim indicator, 60 m when the row gives none.</summary>
    private static (float Min, float Max) CannonRange(WarCannon spec)
    {
        float max = spec.MaxRange > 1 ? spec.MaxRange : 60f;
        return (Math.Clamp(spec.MinRange, 0f, max / 2), max);
    }

    /// <summary>
    /// On the cannon: a visible enemy crowd or castle unit in the cannon's range, else a standing castle object of the
    /// other camp (30115), else where the other side gathers in this stage; fired on a ballistic aim (one per bullet of the
    /// volley, a little spread). Off it when its bullets are spent, an enemy comes close or nothing was in range for long.
    /// </summary>
    private async Task CannonStepAsync(BotClient bot, WarView view, DateTime now, CancellationToken ct)
    {
        if (_cannon is not { } spec || view.Machines.FirstOrDefault(m => m.Uuid == (ulong)_cannonUid) is not { } cannon
            || cannon.State == EntityState.Dead)
        {
            _onCannon = false;
            _cannonUid = 0;
            _machineNext = now.AddSeconds(15);
            return;
        }
        if (view.NearestEnemy < 6f)
        {
            await QuitAsync(bot, spec.QuitButton, ct);
            _onCannon = false;
            _machineNext = now.AddSeconds(25);
            return;
        }
        var at = cannon.MovementInfo.Pos;
        var (minRange, maxRange) = CannonRange(spec);
        bool InRange(float x, float z)
        {
            float d = MathF.Sqrt(Dist2(x, z, at.X, at.Z));
            return d >= minRange && d <= maxRange * 0.95f;
        }
        (float X, float Y, float Z)? aim = null;
        int bestCount = 0;
        foreach (var t in view.Enemies.Concat(view.Targets))
        {
            if (!InRange(t.X, t.Z)) continue;
            int count = view.Enemies.Count(e => Dist2(e.X, e.Z, t.X, t.Z) < 25) + (t.Player ? 0 : 1);
            if (count <= bestCount) continue;
            bestCount = count;
            aim = (t.X, t.Y, t.Z);
        }
        if (aim == null)
            foreach (var o in _objectives.Values)
                if (o.Camp != _camp && o.Camp is 1 or 2 && o.Pos != null && InRange(o.Pos.X, o.Pos.Z)
                    && (o.ConfigId != CrystalNpc || _siegeStage == CrystalStage) && (o.ConfigId != GateNpc || _siegeStage >= GateStage))
                {
                    aim = (o.Pos.X, o.Pos.Y, o.Pos.Z);
                    break;
                }
        if (aim == null && HotZone() is { } zone && InRange(zone.X, zone.Z)) aim = zone;
        if (aim is not { } target)
        {
            if (++_cannonIdle > 20)
            {
                await QuitAsync(bot, spec.QuitButton, ct);
                _onCannon = false;
                _machineNext = now.AddSeconds(40);
                return;
            }
            await PauseAsync(bot, TimeSpan.FromSeconds(1), ct);
            return;
        }
        _cannonIdle = 0;
        int result = await FireAsync(bot, spec, at, target.X, target.Y, target.Z, ct);
        if (result == 0)
        {
            Host.CountWarShot();
            await PauseAsync(bot, TimeSpan.FromMilliseconds(Math.Max(spec.ReloadMs, 1500) + _r.Next(200, 900)), ct);
        }
        else if (result == ErrorCodes.SkillCooldown) await PauseAsync(bot, TimeSpan.FromSeconds(1), ct);
        else
        {
            // Out of bullets (SkillCostShort) or refused: off the cannon; a new one is built later (building another
            // replaces it).
            await QuitAsync(bot, spec.QuitButton, ct);
            _onCannon = false;
            _cannonUid = 0;
            _machineNext = now.AddSeconds(result == ErrorCodes.SkillCostShort ? 20 : 45);
        }
    }

    /// <summary>Where the other side gathers in this stage (a cannon fires there when it sees nobody in its range).</summary>
    private (float X, float Y, float Z)? HotZone()
    {
        int stage = Math.Max(_siegeStage, PrepareStage);
        int lane = _r.Next(3);
        float z = lane switch { 0 => 100, 2 => 200, _ => 150 } + _r.Next(-6, 7);
        if (_camp == 1) return stage switch
        {
            PrepareStage or GateStage => (580 + _r.Next(-6, 7), 15, z),   // behind the gate and on the wall walk
            StrongholdStage => (375 + _r.Next(-8, 9), 15, z),            // around the Knight Guardians
            _ => (165 + _r.Next(-6, 7), 28, 120 + _r.Next(-6, 7)),        // around the crystal
        };
        return stage switch
        {
            PrepareStage or GateStage => (625 + _r.Next(-8, 30), 15, z),  // the ram, the ladders and the camp's exit
            StrongholdStage => (470 + _r.Next(-10, 11), 15, z),          // the attackers coming through the gateway
            _ => (260 + _r.Next(-10, 11), 15, z),                        // the attackers on the way to the crystal
        };
    }

    /// <summary>
    /// One volley (32703 with cannonUseSkill): per bullet the gravity (cm/s^2, as LC_GVGFireCannon sends _fGravity x 100),
    /// the speed (cm/s) and the direction that land it on the target from the muzzle (the server starts the bullet at
    /// _kFireOffset along the aim): the lowest angle from 45 degrees (within the cannon's vertical limits) that reaches.
    /// </summary>
    private async Task<int> FireAsync(BotClient bot, WarCannon spec, Vec3 cannon, float tx, float ty, float tz, CancellationToken ct)
    {
        var use = new CannonUseSkill { UseSkillParam = new CastSkillReq { SkillId = spec.SkillId } };
        for (int i = 0; i < spec.Shots; i++)
        {
            float jx = spec.Shots > 1 ? (float)(_r.NextDouble() - 0.5) * 6 : 0, jz = spec.Shots > 1 ? (float)(_r.NextDouble() - 0.5) * 6 : 0;
            if (Aim(spec, cannon, tx + jx, ty, tz + jz) is { } shot) use.BulletExtraParam.Add(shot);
        }
        if (use.BulletExtraParam.Count == 0) return ErrorCodes.SkillDistance;
        using var reply = bot.Inbox.Expect(e => e.IsGame(WorldInteractionService.Action));
        await bot.SendGameAsync(WorldInteractionService.Action, new UseInteractActionReq { ButtonId = spec.FireButton, CannonUseSkill = use })
            .WaitAsync(SendTimeout, ct);
        try { return UseInteractActionRes.Parser.ParseFrom((await reply.WaitAsync(TimeSpan.FromSeconds(5), ct)).Body).Result; }
        catch (TimeoutException) { return -1; }
    }

    private static BulletExtraParam? Aim(WarCannon spec, Vec3 cannon, float tx, float ty, float tz)
    {
        float dx = tx - cannon.X, dz = tz - cannon.Z, flat = MathF.Sqrt(dx * dx + dz * dz);
        if (flat < 1) return null;
        float fx = dx / flat, fz = dz / flat;
        float sx = cannon.X + fx * spec.Forward + fz * spec.Side, sz = cannon.Z + fz * spec.Forward - fx * spec.Side, sy = cannon.Y + spec.Up;
        float d = MathF.Sqrt(Dist2(tx, tz, sx, sz)), h = ty - sy, g = spec.Gravity > 0 ? spec.Gravity : 9.8f;
        // The cannon's vertical aim limits, within 5-80 degrees whatever order the row gives them in.
        float low = Math.Clamp(MathF.Min(spec.MinAngle, spec.MaxAngle), 5f, 80f), high = Math.Clamp(MathF.Max(spec.MinAngle, spec.MaxAngle), 5f, 80f);
        for (float deg = Math.Clamp(45f, low, high); deg <= high + 0.01f; deg += 5f)
        {
            float th = deg * MathF.PI / 180f, cos = MathF.Cos(th), sin = MathF.Sin(th), lift = d * MathF.Tan(th) - h;
            if (lift <= 0.5f || cos < 0.01f) continue;
            float v = MathF.Sqrt(g * d * d / (2 * cos * cos * lift));
            if (!float.IsFinite(v) || v <= 0) continue;
            return new BulletExtraParam { Gravity = g * 100, Speed = (int)(v * 100), FaceVector3D = new Vec3 { X = fx * cos, Y = sin, Z = fz * cos } };
        }
        return null;
    }

    private async Task QuitAsync(BotClient bot, int button, CancellationToken ct)
    {
        if (button == 0) return;
        using var reply = bot.Inbox.Expect(e => e.IsGame(WorldInteractionService.Action));
        await bot.SendGameAsync(WorldInteractionService.Action, new UseInteractActionReq { ButtonId = button }).WaitAsync(SendTimeout, ct);
        try { await reply.WaitAsync(TimeSpan.FromSeconds(5), ct); }
        catch (TimeoutException) { }
    }

    private async Task<int> InteractAsync(BotClient bot, long npcUid, int interaction, CancellationToken ct)
    {
        await StopAsync(bot, ct);
        using var reply = bot.Inbox.Expect(e => e.IsGame(SceneService.NpcInteraction));
        await bot.SendGameAsync(SceneService.NpcInteraction, new NpcInteractionReq
        {
            NpcUid = npcUid, InteractionId = interaction, Pos = new Vec3 { X = _x, Y = _y, Z = _z },
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        }).WaitAsync(SendTimeout, ct);
        try { return NpcInteractionRsp.Parser.ParseFrom((await reply.WaitAsync(TimeSpan.FromSeconds(5), ct)).Body).Result; }
        catch (TimeoutException) { return -1; }
    }

    private async Task<int> PrepayAsync(BotClient bot, int item, CancellationToken ct)
    {
        using var reply = bot.Inbox.Expect(e => e.IsGame(GuildService.Prepay));
        await bot.SendGameAsync(GuildService.Prepay, new GuildWartimeStorePrepayReq { Item = new GuildWartimeItem { ItemId = item, ItemCount = 1 } })
            .WaitAsync(SendTimeout, ct);
        return await ResultAsync(reply, b => GuildWartimeStorePrepayResp.Parser.ParseFrom(b).Result, TimeSpan.FromSeconds(10), ct);
    }

    private async Task<int> BuildCannonAsync(BotClient bot, float x, float y, float z, float face, CancellationToken ct)
    {
        using var reply = bot.Inbox.Expect(e => e.IsGame(GuildService.BuildCannon));
        await bot.SendGameAsync(GuildService.BuildCannon, new GuildAttackCityBuildReq { Pos = new Vec3 { X = x, Y = y, Z = z }, Face = face })
            .WaitAsync(SendTimeout, ct);
        return await ResultAsync(reply, b => GuildAttackCityBuildResp.Parser.ParseFrom(b).Result, TimeSpan.FromSeconds(10), ct);
    }

    // ------------------------------------------------------------------ vehicles

    /// <summary>
    /// A driver's vehicle: a free vehicle of the camp in sight (its own, a fixed Madogear of the castle, or one nobody
    /// drives; the camp's drivers share which one each goes for), else one from the war warehouse (32281, raised 2 m in
    /// front of its owner). True when it acted.
    /// </summary>
    private async Task<bool> DriverSetupAsync(BotClient bot, WarView view, DateTime now, CancellationToken ct)
    {
        if (view.NearestEnemy < 10) return false;
        var free = view.Machines
            .Where(m => Host.IsVehicleNpc((long)m.EntityTemplateID) && m.EntityCamp == _camp && m.State != EntityState.Dead
                && (m.Owner == 0 || m.Owner == _roleId) && (m.CarryInfo == null || m.CarryInfo.AllCarries.Count == 0)
                && !_ignored.ContainsKey(m.Uuid) && !Host.ClaimedByOther(-SiegeScene, m.Uuid, Profile.Index))
            .OrderBy(m => Distance(m.MovementInfo.Pos))
            .FirstOrDefault();
        if (free != null)
        {
            Host.Claim(-SiegeScene, free.Uuid, Profile.Index);
            var pos = free.MovementInfo.Pos;
            if (Distance(pos) > 2.5f && await ApproachAsync(bot, pos.X, pos.Z, 1.5f, ct)) return true;
            int interaction = Host.DriverInteraction((long)free.EntityTemplateID);
            int code = interaction == 0 ? -1 : await InteractAsync(bot, (long)free.Uuid, interaction, ct);
            if (code == 0)
            {
                _riding = true;
                _vehicleUid = (long)free.Uuid;
                _seat = Host.DriverSkills((long)free.EntityTemplateID);
                _seatReady.Clear();
                _seatBroken.Clear();
                Host.CountWarRide();
                return true;
            }
            _ignored[free.Uuid] = now.AddSeconds(60);
            Host.Unclaim(-SiegeScene, free.Uuid, Profile.Index);
            return false;
        }
        if (_vehicleTries >= 2)
        {
            _vehicleTries = 0;
            _machineNext = now.AddSeconds(90);
            return false;
        }
        _vehicleTries++;
        int item = VehicleItems[WarSlot % VehicleItems.Length];
        using var reply = bot.Inbox.Expect(e => e.IsGame(GuildService.VehicleUse));
        await bot.SendGameAsync(GuildService.VehicleUse, new GuildVehicleUseReq { VehicleItem = new GuildWartimeItem { ItemId = item, ItemCount = 1 } })
            .WaitAsync(SendTimeout, ct);
        int used = await ResultAsync(reply, b => GuildVehicleUseRsp.Parser.ParseFrom(b).Result, TimeSpan.FromSeconds(10), ct);
        if (used != 0)
        {
            Host.WarFailed(Account, "usar-veiculo", used);
            _machineNext = now.AddSeconds(90);
            return false;
        }
        await PauseAsync(bot, TimeSpan.FromMilliseconds(1500), ct);
        return true;
    }

    /// <summary>
    /// Driving: the driver's walks move the vehicle (the server carries it along), so the same route search leads it; it
    /// fights with its seat's skills (VehicleSeatConfig _kMainSkills / _kSkills, cast as any skill) and stays on it until
    /// it is destroyed (or the driver dies).
    /// </summary>
    private async Task RideStepAsync(BotClient bot, WarView view, DateTime now, CancellationToken ct)
    {
        if (view.Machines.FirstOrDefault(m => m.Uuid == (ulong)_vehicleUid) is { State: EntityState.Dead })
        {
            _riding = false;
            _vehicleUid = 0;
            _machineNext = now.AddSeconds(30);
            return;
        }
        if (PickTarget(view) is not { } target)
        {
            await AdvanceAsync(bot, ct);
            return;
        }
        var skill = _seat.Where(s => !_seatBroken.Contains(s.RowId) && _seatReady.GetValueOrDefault(s.RowId) <= now && !s.Buff && !s.Heal)
            .OrderBy(_ => _r.Next()).FirstOrDefault();
        float reach = skill == null ? 6f : skill.Release == 2 ? 4f : skill.Range;
        if (DistanceTo(target) > reach - 0.3f && await ApproachAsync(bot, target.X, target.Z, Math.Max(1.5f, reach - 1f), ct)) return;
        if (skill == null)
        {
            await PauseAsync(bot, TimeSpan.FromMilliseconds(400), ct);
            return;
        }
        await StopAsync(bot, ct);
        await TurnAsync(bot, BotClient.FaceTowards(_x, _z, target.X, target.Z), ct);
        int result = await CastAsync(bot, skill, target.Uid, target.X, target.Y, target.Z, ct);
        if (result == 0)
        {
            Host.CountWarCast();
            _seatReady[skill.RowId] = now.AddMilliseconds(Math.Max(skill.CooldownMs, 1000) + skill.ChantMs);
            await PauseAsync(bot, TimeSpan.FromMilliseconds(skill.ChantMs + _r.Next(500, 900)), ct);
            return;
        }
        if (result == ErrorCodes.SkillCooldown) _seatReady[skill.RowId] = now.AddSeconds(2);
        else if (result is ErrorCodes.SkillNotLearned or ErrorCodes.SkillUnknown or ErrorCodes.SkillPassive or ErrorCodes.SkillRequirements
            or ErrorCodes.SkillTargetInvalid) _seatBroken.Add(skill.RowId);
        await PauseAsync(bot, TimeSpan.FromMilliseconds(300), ct);
    }
}
