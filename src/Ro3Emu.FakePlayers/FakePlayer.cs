using System.Collections.Concurrent;
using Ro3Emu.Bot;
using Ro3Emu.LoadTest;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;
using Romsg;

namespace Ro3Emu.FakePlayers;

public enum FakeState { Starting, Town, Hunting, Dead, Retrying, Stopped }

/// <summary>
/// One fake player: a real protocol client (login, role, scene, walk, chat, combat) driven by its <see cref="FakeProfile"/>.
/// It reconnects with a growing pause after any failure until it is stopped.
/// </summary>
public sealed class FakePlayer(FakePlayerHost host, FakeProfile profile)
{
    /// <summary>3 m steps every 0.6 s (5 m/s): viewers project each step for up to 1 s, so the walk stays smooth with fewer
    /// walk messages than 2 m / 0.4 s, and the server's speed check allows it even when steps arrive bunched.</summary>
    private const float StepMeters = 3f;
    private static readonly TimeSpan StepInterval = TimeSpan.FromMilliseconds(600);
    /// <summary>Role ids are small; monster and NPC unit ids start at 1 &lt;&lt; 40 (WorldData/MonsterService).</summary>
    private const long UnitUidBase = 1L << 40;
    private static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(15);

    private readonly Random _r = new(profile.Index * 31 + Environment.TickCount);
    private readonly Dictionary<ulong, DateTime> _ignored = new();
    private volatile bool _dead, _disconnected;
    private float _x, _y, _z;
    /// <summary>Facing in degrees (0 = +z, clockwise): where the character looks and walks.</summary>
    private float _face;
    /// <summary>Walked pace / job speed: viewers project each step with it, so the walk looks continuous.</summary>
    private float _speedRate = 1;
    private long _roleId;
    private int _normalSkill = 1000001;
    private MapNav? _nav;
    private string _chatGroup = "";
    private DateTime _nextHeartbeat, _nextChat;
    private bool _placed;
    /// <summary>The map this player is on now (hunters move between fields).</summary>
    private long _map;
    /// <summary>Monsters another player is fighting (seen hitting or being hit), until when: never stolen (no KS).</summary>
    private readonly ConcurrentDictionary<ulong, long> _engaged = new();
    private DateTime _noTargetSince = DateTime.MaxValue, _nextBuff;
    private readonly Queue<DateTime> _recentDeaths = new();
    private bool _targetHit;
    /// <summary>Own HP from 31008 (property 2000); the highest seen stands in for the maximum (full after login/revive).</summary>
    private long _hp, _maxHp;

    public FakeProfile Profile => profile;
    public string Account { get; } = host.Options.Prefix + profile.Index.ToString("D5");
    public string? Name { get; private set; }
    public FakeState State { get; private set; } = FakeState.Starting;

    public async Task RunAsync(CancellationToken ct)
    {
        int failures = 0;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await SessionAsync(ct);
                failures = 0;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception e)
            {
                failures++;
                host.Failed(this, e);
            }
            if (ct.IsCancellationRequested) break;
            State = FakeState.Retrying;
            try { await Task.Delay(TimeSpan.FromSeconds(Math.Min(120, 5 * failures) + _r.Next(0, 10)), ct); }
            catch (OperationCanceledException) { break; }
        }
        State = FakeState.Stopped;
    }

    private async Task SessionAsync(CancellationToken ct)
    {
        State = FakeState.Starting;
        _dead = _disconnected = false;
        _placed = false;
        var reducer = new WorldReducer();
        var bot = new BotClient(host.Options.ServerUrl, host.Pem)
        {
            Timeout = TimeSpan.FromSeconds(60), GateHostOverride = host.Options.GateHost, ViewMaxCount = (ulong)host.Options.ViewCount,
        };
        await using var owned = bot;
        bot.EnableLoadMode(256, e => { reducer.Observe(e); Observe(e); });

        await host.LoginGate.WaitAsync(ct);
        try
        {
            await bot.LoginAsync(Account, host.Options.Password).WaitAsync(TimeSpan.FromSeconds(120), ct);
            await bot.EnterGateAsync().WaitAsync(TimeSpan.FromSeconds(120), ct);
            var role = bot.Roles.FirstOrDefault() ?? await CreateRoleAsync(bot, ct);
            _roleId = role.Id;
            Name = role.Name;
            var self = await bot.EnterWorldAsync(_roleId).WaitAsync(TimeSpan.FromSeconds(120), ct);
            SetPosition(self.MovementInfo.Pos);
            _face = self.Face;
            // The server gave the role its class, levels, skills and stats while loading it (FakePlayerService) and sent
            // the castable skills on the "skills" line.
        }
        finally { host.LoginGate.Release(); }

        _normalSkill = host.NormalSkill(profile.Job);
        _speedRate = Math.Clamp(StepMeters / (float)StepInterval.TotalSeconds / host.WalkSpeed(profile.Job), 0.1f, 1f);
        // Hunters take the quietest field of their level band; town players their own town.
        long home = profile.Behavior == FakeBehavior.Hunter
            ? host.PickHuntMap(_r, FakeProfile.HuntBand(profile.BaseLevel))
            : profile.TownMap;
        if (profile.Behavior == FakeBehavior.Hunter) host.MoveHunter(0, home);
        _map = home;
        try
        {
            if (reducer.Location.SceneEntry != home) await TravelAsync(bot, reducer, home, ct);
            else await EnterSceneAsync(bot, reducer, ct);
            _nextHeartbeat = DateTime.UtcNow;
            _nextChat = DateTime.UtcNow + ChatGap() * _r.NextDouble();
            _nextBuff = DateTime.UtcNow.AddSeconds(_r.Next(5, 30));

            while (!ct.IsCancellationRequested)
            {
                if (_disconnected) throw new IOException("connection closed by the server");
                if (profile.Behavior == FakeBehavior.Hunter) await HuntAsync(bot, reducer, ct);
                else await TownAsync(bot, ct);
            }
        }
        finally
        {
            if (profile.Behavior == FakeBehavior.Hunter) host.MoveHunter(_map, 0);
            ReleaseTarget();
        }
    }

    private void Observe(BotEvent e)
    {
        if (e.Kind == BotEventKind.Disconnect) { _disconnected = true; return; }
        if (profile.Behavior == FakeBehavior.Hunter && e.IsGame(CombatService.SkillHit))
        {
            NoteFight(SkillHitInfo.Parser.ParseFrom(e.Body));
            return;
        }
        if (profile.Behavior == FakeBehavior.Hunter && e.IsGame(CharacterService.OnePropertyChange))
        {
            foreach (var property in OnePropertyChange.Parser.ParseFrom(e.Body).Property?.Propertys ?? [])
                if (property.PropertyId == 2000)
                {
                    _hp = property.Data;
                    _maxHp = Math.Max(_maxHp, property.Data);
                }
            return;
        }
        if (!e.IsGame(CombatService.ReviveInfo)) return;
        bool dead = SyncClientPlayerReviveInfoNtf.Parser.ParseFrom(e.Body).BDead;
        if (dead && !_dead) host.CountDeath();
        _dead = dead;
    }

    /// <summary>
    /// A hit seen nearby: a player hitting a monster, or a monster hitting a player, marks that monster as someone
    /// else's fight for a while (this player's own hits do not count).
    /// </summary>
    private void NoteFight(SkillHitInfo hit)
    {
        long until = DateTime.UtcNow.AddSeconds(12).Ticks;
        bool fromPlayer = hit.SrcID is > 0 and < UnitUidBase;
        foreach (var dst in hit.Dsts)
        {
            bool toPlayer = dst.DstID is > 0 and < UnitUidBase;
            if (fromPlayer && !toPlayer && hit.SrcID != _roleId) _engaged[(ulong)dst.DstID] = until;
            else if (!fromPlayer && toPlayer && dst.DstID != _roleId) _engaged[(ulong)hit.SrcID] = until;
            if (!toPlayer && dst.IsDead) _engaged.TryRemove((ulong)dst.DstID, out _);
        }
    }

    private async Task<RoleBaseInfo> CreateRoleAsync(BotClient bot, CancellationToken ct)
    {
        foreach (var name in FakeNames.Candidates(profile.Index))
        {
            try
            {
                var created = await bot.CreateRoleAsync(name, profile.Sex, profile.Hair).WaitAsync(TimeSpan.FromSeconds(60), ct);
                if (created.Result == 0) return created.RoleBaseInfo;
            }
            catch (BotRefusedException e) when (e.Result is "2" or "32") { } // name taken or too long: next candidate
        }
        throw new InvalidOperationException("no free character name");
    }

    // ------------------------------------------------------------------ scenes

    private async Task TravelAsync(BotClient bot, WorldReducer reducer, long target, CancellationToken ct)
    {
        using var reply = bot.Inbox.Expect(e => e.IsGame(SceneService.SceneTeleport));
        using var switched = bot.Inbox.Expect(e => e.IsGame(MsgId.SwitchSceneLine));
        using var self = bot.Inbox.Expect(e => e.IsGame(MsgId.EntitySelf));
        using var end = bot.Inbox.Expect(e => e.IsGame(MsgId.SelfInfoEndFrameNotify));
        await bot.SendGameAsync(SceneService.SceneTeleport, new SceneTeleportReq { TargetMapId = target }).WaitAsync(SendTimeout, ct);
        var response = SceneTeleportRsp.Parser.ParseFrom((await reply.WaitAsync(TimeSpan.FromSeconds(30), ct)).Body);
        if (response.Result != 0) throw new InvalidOperationException($"travel to {target} refused ({response.Result})");
        var scene = SwitchSceneLineResp.Parser.ParseFrom((await switched.WaitAsync(TimeSpan.FromSeconds(30), ct)).Body);
        using var loaded = bot.Inbox.Expect(e => e.IsGame(MsgId.SceneLoaded));
        await bot.SendGameAsync(MsgId.SceneLoaded, new SceneLoadedReq { SceneId = scene.SceneEntry, ViewMaxCount = (ulong)host.Options.ViewCount }).WaitAsync(SendTimeout, ct);
        var ready = SceneLoadedResp.Parser.ParseFrom((await loaded.WaitAsync(TimeSpan.FromSeconds(30), ct)).Body);
        if (ready.Result != 0) throw new InvalidOperationException($"scene {scene.SceneEntry} load refused ({ready.Result})");
        var arrived = EntitySelf.Parser.ParseFrom((await self.WaitAsync(TimeSpan.FromSeconds(30), ct)).Body).Base;
        SetPosition(arrived.MovementInfo.Pos);
        _face = arrived.Face;
        await end.WaitAsync(TimeSpan.FromSeconds(30), ct);
        await EnterSceneAsync(bot, reducer, ct);
    }

    /// <summary>After a scene is ready: its walkable cells and its map chat group (per line: 4_&lt;runtime scene&gt;).</summary>
    private async Task EnterSceneAsync(BotClient bot, WorldReducer reducer, CancellationToken ct)
    {
        var location = reducer.Location;
        _nav = host.Nav(location.SceneEntry);
        State = profile.Behavior == FakeBehavior.Hunter ? FakeState.Hunting : FakeState.Town;
        _chatGroup = $"4_{(location.SceneId != 0 ? location.SceneId : location.SceneEntry)}";
        try { await bot.JoinChatAsync(_chatGroup).WaitAsync(SendTimeout, ct); }
        catch (Exception e) when (e is not OperationCanceledException) { _chatGroup = ""; host.Log($"{Account}: chat join failed: {e.Message}"); }
    }

    private void SetPosition(Vec3? pos)
    {
        if (pos == null) return;
        _x = pos.X; _y = pos.Y; _z = pos.Z;
    }

    /// <summary>Turns in place (a stop step with the new facing), e.g. towards a monster or to look around.</summary>
    private async Task TurnAsync(BotClient bot, float face, CancellationToken ct)
    {
        float diff = MathF.Abs((face - _face + 540f) % 360f - 180f);
        if (diff < 15f) return;
        _face = face;
        await bot.WalkAsync(_x, _y, _z, true, _face, _speedRate).WaitAsync(SendTimeout, ct);
    }

    // ------------------------------------------------------------------ town

    private async Task TownAsync(BotClient bot, CancellationToken ct)
    {
        switch (profile.Behavior)
        {
            case FakeBehavior.Idle:
                if (!_placed && _nav?.Destination(_r, _x, _y, _z, 40) is { } spot) await WalkToAsync(bot, spot, ct);
                _placed = true;
                await PauseAsync(bot, TimeSpan.FromSeconds(_r.Next(20, 90)), ct);
                // Now and then an idle player looks around, or takes a couple of steps and stops again.
                if (_r.NextDouble() < 0.5) await TurnAsync(bot, _r.Next(0, 360), ct);
                else if (_r.NextDouble() < 0.5 && _nav?.Destination(_r, _x, _y, _z, 4) is { } step) await WalkToAsync(bot, step, ct);
                break;
            case FakeBehavior.Wanderer:
                if (_nav?.Destination(_r, _x, _y, _z, 30) is { } next) await WalkToAsync(bot, next, ct);
                await PauseAsync(bot, TimeSpan.FromSeconds(_r.Next(8, 50)), ct);
                break;
            default:
                if (_nav?.Destination(_r, _x, _y, _z, 40) is { } target) await WalkToAsync(bot, target, ct);
                await PauseAsync(bot, TimeSpan.FromMilliseconds(_r.Next(1000, 6000)), ct);
                break;
        }
    }

    /// <summary>Straight walk in 2 m steps every 0.4 s (5 m/s, inside the server's speed check), ending with a stop.</summary>
    private async Task WalkToAsync(BotClient bot, (float X, float Y, float Z) target, CancellationToken ct, float stopShort = 0)
    {
        for (int i = 0; i < 80 && !ct.IsCancellationRequested && !_dead; i++)
        {
            float dx = target.X - _x, dz = target.Z - _z, distance = MathF.Sqrt(dx * dx + dz * dz) - stopShort;
            bool last = distance <= StepMeters;
            float step = MathF.Min(StepMeters, MathF.Max(0, distance));
            float total = MathF.Sqrt(dx * dx + dz * dz);
            float x = total > 0 ? _x + dx / total * step : _x, z = total > 0 ? _z + dz / total * step : _z;
            // Every step stays on walkable ground as the server checks it (cell and straight segment); else stop here.
            if (_nav != null && (_nav.Height(x, z, _y) is not { } ground || !_nav.CanWalk(_x, _y, _z, x, z)))
            {
                await bot.WalkAsync(_x, _y, _z, true, _face, _speedRate).WaitAsync(SendTimeout, ct);
                return;
            }
            float y = _nav?.Height(x, z, _y) ?? _y;
            // Face where the step goes: viewers turn the model and animate the walk along this direction.
            if (step > 0.01f) _face = BotClient.FaceTowards(_x, _z, x, z);
            await bot.WalkAsync(x, y, z, last, _face, _speedRate).WaitAsync(SendTimeout, ct);
            _x = x; _y = y; _z = z;
            await KeepAliveAsync(bot, ct);
            if (last) return;
            await Task.Delay(StepInterval, ct);
        }
    }

    /// <summary>Stand still for a while, still heartbeating and chatting like a person at the keyboard.</summary>
    private async Task PauseAsync(BotClient bot, TimeSpan time, CancellationToken ct)
    {
        var until = DateTime.UtcNow + time;
        while (DateTime.UtcNow < until && !ct.IsCancellationRequested && !_disconnected)
        {
            await KeepAliveAsync(bot, ct);
            await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(1000, Math.Max(1, (until - DateTime.UtcNow).TotalMilliseconds))), ct);
        }
    }

    // ------------------------------------------------------------------ hunting

    private ulong _target;
    private long _targetScene;
    private readonly Dictionary<int, DateTime> _skillReady = new();
    private readonly HashSet<int> _skillBroken = new();
    private DateTime _skillsRestUntil;

    /// <summary>Attack skills the server taught this player (FakePlayerService "skills" line); cast between normal hits.</summary>
    public IReadOnlyList<FakeSkill> Learned { get; set; } = [];

    /// <summary>
    /// One hunting step: keep the current monster while it lives, else pick a nearby one other fake players are not
    /// already on (a little randomness, so a crowd spreads over the spawns); close in to the chosen action's reach,
    /// face the monster and hit it with a class skill or the normal attack.
    /// </summary>
    private async Task HuntAsync(BotClient bot, WorldReducer reducer, CancellationToken ct)
    {
        if (_dead)
        {
            ReleaseTarget();
            await ReviveAsync(bot, reducer, ct);
            return;
        }
        var now = DateTime.UtcNow;
        foreach (var key in _ignored.Where(x => x.Value < now).Select(x => x.Key).ToArray()) _ignored.Remove(key);
        var location = reducer.Location;
        long scene = location.SceneId != 0 ? location.SceneId : location.SceneEntry;
        // Low on HP: break off, step away from the monsters and wait for regeneration, like a player avoiding a death.
        if (_maxHp > 0 && _hp > 0 && _hp < _maxHp * 35 / 100)
        {
            await RestAsync(bot, reducer, ct);
            return;
        }
        if (now >= _nextBuff && await BuffAsync(bot, now, ct)) return;
        long nowTicks = now.Ticks;
        // Someone else's fight: a player or another fake player is on it, or it is chasing someone else. Never stolen.
        bool Busy(EntityBase m) =>
            (_engaged.TryGetValue(m.Uuid, out long until) && until > nowTicks)
            || (m.HatredTargetId != 0 && m.HatredTargetId != _roleId)
            || host.ClaimedByOther(scene, m.Uuid, profile.Index);
        var monsters = reducer.Monsters();
        // Keep the current monster while it lives, unless someone else got to it before this player's first hit.
        var target = monsters.FirstOrDefault(m => m.Uuid == _target && !_ignored.ContainsKey(m.Uuid)
            && Distance(m.MovementInfo.Pos) < 30 && (_targetHit || !Busy(m)));
        if (target == null)
        {
            ReleaseTarget();
            target = monsters
                .Where(m => !_ignored.ContainsKey(m.Uuid) && !Busy(m))
                .Select(m => (Monster: m, Distance: Distance(m.MovementInfo.Pos)))
                .Where(x => x.Distance < 25)
                .OrderBy(x => x.Distance + (float)_r.NextDouble() * 6f)
                .Select(x => x.Monster)
                .FirstOrDefault();
            if (target == null)
            {
                // A field with nothing free for a while (crowded, or emptied by others): go to a quieter one.
                if (_noTargetSince == DateTime.MaxValue) _noTargetSince = now;
                if (now - _noTargetSince > TimeSpan.FromSeconds(45))
                {
                    await ChangeMapAsync(bot, reducer, easier: false, "aqui ta lotado, vou pra outro mapa", ct);
                    return;
                }
                if (_nav?.Destination(_r, _x, _y, _z, 25) is { } roam) await WalkToAsync(bot, roam, ct);
                await PauseAsync(bot, TimeSpan.FromMilliseconds(_r.Next(500, 2000)), ct);
                return;
            }
            _target = target.Uuid;
            _targetScene = scene;
            _targetHit = false;
        }
        _noTargetSince = DateTime.MaxValue;
        host.Claim(scene, target.Uuid, profile.Index);
        var pos = target.MovementInfo.Pos;
        var action = ChooseAction(now);
        // Area skills around the caster need the monster close; everything else uses its own reach.
        float reach = action.Release == 2 ? 2.5f : action.Range;
        if (Distance(pos) > reach - 0.3f)
        {
            if (!(_nav?.CanWalk(_x, _y, _z, pos.X, pos.Z) ?? true))
            {
                _ignored[target.Uuid] = now.AddSeconds(30);
                ReleaseTarget();
                return;
            }
            await WalkToAsync(bot, (pos.X, pos.Y, pos.Z), ct, stopShort: Math.Max(1.5f, reach - 1f));
            return;
        }
        await TurnAsync(bot, BotClient.FaceTowards(_x, _z, pos.X, pos.Z), ct); // face the monster before swinging
        await bot.SendGameAsync(CombatService.LockEnemy, new LockEnemyReq { EnemyId = (long)target.Uuid }).WaitAsync(SendTimeout, ct);
        var cast = new CastSkillReq { SkillId = action.RowId, TargetId = (long)target.Uuid, Face = _face };
        if (action.Release is 7 or 8) cast.TargetPos = new Vec3 { X = pos.X, Y = pos.Y, Z = pos.Z }; // on the ground under it
        if (action.Release == 2) cast.TargetId = 0; // around the caster
        using var reply = bot.Inbox.Expect(e => e.IsGame(CombatService.UseSkill));
        await bot.SendGameAsync(CombatService.UseSkill, cast).WaitAsync(SendTimeout, ct);
        int result;
        try { result = CastSkillRsp.Parser.ParseFrom((await reply.WaitAsync(TimeSpan.FromSeconds(5), ct)).Body).Result; }
        catch (TimeoutException) { result = -1; }
        var pause = TimeSpan.FromMilliseconds(_r.Next(800, 1400));
        if (result == 0)
        {
            _targetHit = true;
            if (action.Normal) host.CountAttack();
            else
            {
                host.CountSkill();
                _skillReady[action.Group] = now.AddMilliseconds(Math.Max(action.CooldownMs, 1500) + action.ChantMs + _r.Next(0, 3000));
                pause = TimeSpan.FromMilliseconds(action.ChantMs + _r.Next(600, 1000));
            }
        }
        else if (!action.Normal)
        {
            switch (result)
            {
                case ErrorCodes.SkillCooldown: _skillReady[action.Group] = now.AddMilliseconds(Math.Max(action.CooldownMs, 2000)); break;
                case ErrorCodes.SkillDistance: break; // closer next step
                case ErrorCodes.SkillNotLearned or ErrorCodes.SkillTargetInvalid or ErrorCodes.SkillUnknown
                    or ErrorCodes.SkillPassive or ErrorCodes.SkillRequirements:
                    _skillBroken.Add(action.Group); // not usable on monsters for this player
                    break;
                default: _skillsRestUntil = now.AddSeconds(15); break; // out of SP or similar: normal hits for a while
            }
            pause = TimeSpan.FromMilliseconds(_r.Next(200, 500));
        }
        else if (result != ErrorCodes.SkillDistance)
        {
            _ignored[target.Uuid] = now.AddSeconds(10); // dead, gone or not a valid target
            ReleaseTarget();
        }
        await PauseAsync(bot, pause, ct);
    }

    /// <summary>The class skill to use now (a ready learned one, about two hits in three), else the normal attack.</summary>
    private FakeSkill ChooseAction(DateTime now)
    {
        var normal = host.NormalAttack(profile.Job);
        if (now < _skillsRestUntil || Learned.Count == 0 || _r.NextDouble() < 0.35) return normal;
        var ready = Learned.Where(s => s.Attack && !_skillBroken.Contains(s.Group) && _skillReady.GetValueOrDefault(s.Group) <= now).ToList();
        return ready.Count == 0 ? normal : ready[_r.Next(ready.Count)];
    }

    private void ReleaseTarget()
    {
        if (_target != 0) host.Unclaim(_targetScene, _target, profile.Index);
        _target = 0;
        _targetHit = false;
    }

    /// <summary>
    /// Leaves the fight: walks 12-20 m away from the nearest monster and stands until HP is back to 80% (out of combat
    /// the server regenerates every 5 s, starting 10 s after the last hit), at most about a minute.
    /// </summary>
    private async Task RestAsync(BotClient bot, WorldReducer reducer, CancellationToken ct)
    {
        ReleaseTarget();
        host.CountRest();
        var nearest = reducer.Monsters().OrderBy(m => Distance(m.MovementInfo.Pos)).FirstOrDefault();
        if (nearest != null && _nav != null)
        {
            var from = nearest.MovementInfo.Pos;
            float away = BotClient.FaceTowards(from.X, from.Z, _x, _z) * MathF.PI / 180f;
            for (int attempt = 0; attempt < 6; attempt++)
            {
                float distance = 12 + _r.Next(0, 9), angle = away + (float)(_r.NextDouble() - 0.5);
                float tx = _x + MathF.Sin(angle) * distance, tz = _z + MathF.Cos(angle) * distance;
                if (_nav.Height(tx, tz, _y) is { } ty && _nav.CanWalk(_x, _y, _z, tx, tz))
                {
                    await WalkToAsync(bot, (tx, ty, tz), ct);
                    break;
                }
            }
        }
        if (_r.NextDouble() < 0.2) await SayAsync(bot, _r.NextDouble() < 0.5 ? "pera, vou recuperar hp" : "sem pot, descansando", ct);
        var until = DateTime.UtcNow.AddSeconds(60);
        while (DateTime.UtcNow < until && !_dead && !ct.IsCancellationRequested && _hp < _maxHp * 80 / 100)
            await PauseAsync(bot, TimeSpan.FromSeconds(2), ct);
    }

    /// <summary>Casts one ready self buff of the class (Blessing, Increase AGI, Endure...) now and then; true when cast.</summary>
    private async Task<bool> BuffAsync(BotClient bot, DateTime now, CancellationToken ct)
    {
        _nextBuff = now.AddSeconds(_r.Next(45, 120));
        var buff = Learned.Where(s => s.Buff && !_skillBroken.Contains(s.Group) && _skillReady.GetValueOrDefault(s.Group) <= now)
            .OrderBy(_ => _r.Next()).FirstOrDefault();
        if (buff == null) return false;
        using var reply = bot.Inbox.Expect(e => e.IsGame(CombatService.UseSkill));
        await bot.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = buff.RowId, TargetId = _roleId, Face = _face })
            .WaitAsync(SendTimeout, ct);
        int result;
        try { result = CastSkillRsp.Parser.ParseFrom((await reply.WaitAsync(TimeSpan.FromSeconds(5), ct)).Body).Result; }
        catch (TimeoutException) { result = -1; }
        if (result == 0)
        {
            host.CountSkill();
            _skillReady[buff.Group] = now.AddMilliseconds(Math.Max(buff.CooldownMs, 10_000));
            await PauseAsync(bot, TimeSpan.FromMilliseconds(buff.ChantMs + _r.Next(600, 1000)), ct);
            return true;
        }
        if (result is ErrorCodes.SkillNotLearned or ErrorCodes.SkillTargetInvalid or ErrorCodes.SkillUnknown
            or ErrorCodes.SkillPassive or ErrorCodes.SkillRequirements) _skillBroken.Add(buff.Group);
        return false;
    }

    /// <summary>Moves a hunter to another field of its band (or the easier band after dying too often).</summary>
    private async Task ChangeMapAsync(BotClient bot, WorldReducer reducer, bool easier, string? say, CancellationToken ct)
    {
        var maps = easier ? FakeProfile.EasierBand(profile.BaseLevel) : FakeProfile.HuntBand(profile.BaseLevel);
        long next = host.PickHuntMap(_r, maps, avoid: _map);
        _noTargetSince = DateTime.MaxValue;
        if (next == _map) return;
        if (say != null && _r.NextDouble() < 0.3) await SayAsync(bot, say, ct);
        ReleaseTarget();
        _ignored.Clear();
        _engaged.Clear();
        host.MoveHunter(_map, next);
        _map = next;
        await TravelAsync(bot, reducer, next, ct);
    }

    /// <summary>Lies dead a few seconds like a person reading the screen, then revives at the map's revive point.</summary>
    private async Task ReviveAsync(BotClient bot, WorldReducer reducer, CancellationToken ct)
    {
        State = FakeState.Dead;
        await PauseAsync(bot, TimeSpan.FromSeconds(_r.Next(3, 12)), ct);
        using var ack = bot.Inbox.Expect(e => e.IsGame(CombatService.Relive));
        using var teleport = bot.Inbox.Expect(e => e.IsGame(GameAoiSink.MovementChange) && MovementChangeNtf.Parser.ParseFrom(e.Body).Uid == _roleId);
        await bot.SendGameAsync(CombatService.Relive, new EntityReliveReq()).WaitAsync(SendTimeout, ct);
        var relive = EntityReliveAck.Parser.ParseFrom((await ack.WaitAsync(TimeSpan.FromSeconds(10), ct)).Body);
        if (relive.Result == 0)
        {
            host.CountRevive();
            _dead = false;
            _hp = _maxHp;
            // The revive point comes as a force teleport (31081) for this role.
            try { SetPosition(MovementChangeNtf.Parser.ParseFrom((await teleport.WaitAsync(TimeSpan.FromSeconds(5), ct)).Body).MovementInfo?.Pos); }
            catch (TimeoutException) { SetPosition(reducer.SelfPosition()); }
            if (_r.NextDouble() < 0.3) await SayAsync(bot, _r.NextDouble() < 0.5 ? "morri kkkk" : "putz, morri", ct);
            // Dying over and over: these monsters are too strong for now, go somewhere easier.
            var now = DateTime.UtcNow;
            _recentDeaths.Enqueue(now);
            while (_recentDeaths.Count > 0 && now - _recentDeaths.Peek() > TimeSpan.FromMinutes(10)) _recentDeaths.Dequeue();
            if (_recentDeaths.Count >= 3)
            {
                _recentDeaths.Clear();
                State = FakeState.Hunting;
                await ChangeMapAsync(bot, reducer, easier: true, "muito forte aqui, vou upar em outro lugar", ct);
                return;
            }
        }
        State = FakeState.Hunting;
    }

    private float Distance(Vec3 pos)
    {
        float dx = pos.X - _x, dz = pos.Z - _z;
        return MathF.Sqrt(dx * dx + dz * dz);
    }

    // ------------------------------------------------------------------ keep-alive and chat

    private async Task KeepAliveAsync(BotClient bot, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        if (now >= _nextHeartbeat)
        {
            _nextHeartbeat = now.AddSeconds(5);
            await bot.SendGameAsync(MsgId.Heartbeat, new Ping()).WaitAsync(SendTimeout, ct);
        }
        if (now >= _nextChat)
        {
            _nextChat = now + ChatGap();
            if (host.Talkative(profile))
                await SayAsync(bot, FakeChat.Line(_r, profile.Behavior == FakeBehavior.Hunter, FakePlayerHost.JobName(profile.Job), profile.BaseLevel), ct);
        }
    }

    private TimeSpan ChatGap() => TimeSpan.FromSeconds(profile.Behavior switch
    {
        FakeBehavior.Idle => _r.Next(45, 150),
        FakeBehavior.Hunter => _r.Next(180, 600),
        _ => _r.Next(90, 300),
    } / Math.Max(0.01, host.Options.ChatSpeed));

    private async Task SayAsync(BotClient bot, string text, CancellationToken ct)
    {
        if (_chatGroup.Length == 0) return;
        await bot.SendChatAsync(_chatGroup, 2, text, channelIndex: 4).WaitAsync(SendTimeout, ct);
        host.CountChat();
    }
}
