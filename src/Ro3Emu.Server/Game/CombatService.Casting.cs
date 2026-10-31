using System.Text.Json;
using Romsg;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Data.Config;
using Ro3Emu.Server.Maps;
using Ro3Emu.Server.Game.Ownership;

namespace Ro3Emu.Server.Game;

/// <summary>A single accepted cast, bound to its role, scene and generation (LC_Scene_Sync._skillEndNotify).</summary>
public sealed class ActiveSkillCast
{
    public required long Id { get; init; }
    public required long OwnerId { get; init; }
    public required long WorldScene { get; init; }
    public long SceneGeneration { get; init; }
    public Ro3Emu.Server.Game.Ownership.AttachmentLease? Attachment { get; init; }
    public required CastSkillReq Request { get; init; }
    public required JsonElement Row { get; init; }
    public required Vec3 Origin { get; set; }
    public required long StartMs { get; init; }
    public required int ChantMs { get; init; }
    public required int ActionMs { get; init; }
    public bool Cancelled { get; set; }
    public bool Resolved { get; set; }
    public SkillObject? Operator { get; init; }
    /// <summary>A cast the server releases on its own (a follow-up): not the session's cast slot, so it is not
    /// interrupted by the player's next cast or by walking.</summary>
    public bool Independent { get; init; }
    /// <summary>Extra effects remain bound to their equipped source after asynchronous publication.</summary>
    internal Func<bool>? SourceEligible { get; init; }
    internal Dictionary<long, long>? GuardianDamage { get; init; }
    internal SemaphoreSlim? GuardianGate { get; init; }
    internal HashSet<int>? GuardianOnce { get; init; }
    /// <summary>A turret's shot (CombatService.TurretFired): its projectile and its effects.</summary>
    public TurretShot? Turret { get; init; }
    public long SourceId => Operator?.Uid ?? OwnerId;
}

/// <summary>
/// How a turret's shot is shown (CombatService.TurretFired): its projectile leaves the turret unit
/// <paramref name="SourceId"/> (at <paramref name="Origin"/>), and its hit and projectile effects are those of the skill
/// template <paramref name="Show"/> instead of the row's _iScriptShow. A hit shows <paramref name="Shown"/>, give or take
/// <see cref="CombatService.TurretShownSpread"/>, as its damage number (0: its own damage).
/// </summary>
public sealed record TurretShot(long SourceId, Vec3 Origin, long Show, long Shown = 0);

public sealed partial class CombatService
{
    public const uint SkillMove = 31112, TrapDamage = 31128;

    /// <summary>
    /// 31102: row identity, ownership, selector, mode, all costs and cooldowns are checked before the cast commits.
    /// Target/position come from CastSkillReq; m_kSelectInfo uses relation masks, never _iDmgType as a selector.
    /// Evidence: LC_Skill_Function.SendSkill, SkillConfig.GetCost*, MG_Skill.proto.
    /// </summary>
    public async Task CastAsync(GameSession s, CastSkillReq request)
    {
        LegacyOwnerScope? admission = null;
        OwnerNotices? notices = null;
        if (s.Owners is { } owners && s.Role is { } role)
            admission = await owners.EnterWithGateAsync("combat/cast", s.SkillCastGate, [ReservationKey.Role(role.Id)]);
        else await s.SkillCastGate.WaitAsync();
        try
        {
            using var state = await CharacterService.Lock(s);
            // Complete the runtime admission before any socket send can suspend. Frozen packets
            // retain the original order; an obsolete attachment cannot start a committed cast.
            if (s.Owners?.Current is { Notices: null } context) notices = new OwnerNotices(context);
            if (IsGuardianRequest(request)) await CastGuardianAsync(s, request.Clone());
            else await CastCoreAsync(s, request);
        }
        finally { notices?.Dispose(); s.SkillCastGate.Release(); admission?.Dispose(); }
        if (notices != null) await notices.FlushAsync();
    }

    /// <summary>A pet wrapper or its child effect: the guardian path validates and runs it (31102).</summary>
    private bool IsGuardianRequest(CastSkillReq request) => _data.Row("SkillConfig", request.SkillId) is { } row
        && GameData.Int(row, "_iSystemType") is GuardianPetRules.WrapperType or GuardianPetRules.EffectType;

    private async Task CastCoreAsync(GameSession s, CastSkillReq request)
    {
        var req = request.Clone();
        bool turret = TurretSkill?.Invoke(s, req.SkillId) == true;
        if (turret) TurretAim?.Invoke(s, req);
        var row = EffectiveSkillRow(s, req.SkillId);
        var owned = req.OpUnitId == 0 || req.OpUnitId == s.Role?.Id ? null
            : s.SkillObjects.FirstOrDefault(u => u.Uid == req.OpUnitId && u.WorldScene == s.WorldScene && u.ExpiresMs > Now);
        int error = s.Role == null || s.Progress == null || s.Dead || s.SceneLoading ? ErrorCodes.SkillBadState
            : row == null ? ErrorCodes.SkillUnknown
            : req.OpUnitId != 0 && req.OpUnitId != s.Role.Id && owned == null ? ErrorCodes.SkillRequirements
            : !float.IsFinite(req.Face) || req.TargetPos is { } pos && !Finite(pos) ? ErrorCodes.Parameter : 0;
        int? own = null;
        if (error == 0)
        {
            req.SkillId = (int)GameData.Int(row!.Value, "_iID");
            own = owned == null ? OwnSkillAdmit?.Invoke(s, req.SkillId) : null;
            if (own is { } ownError && ownError != 0) error = ownError;
            else if (own == null && (owned != null ? !owned.Skills.Contains(req.SkillId)
                : !Learned(s, row!.Value) && s.GmCastSkill != request.SkillId && !GrantedSkill.Exists(granted => granted(s, req.SkillId))))
                error = ErrorCodes.SkillNotLearned;
            else if (!SkillModeAllowed(s, row!.Value)) error = ErrorCodes.SkillRequirements;
            else if (GameData.Int(row!.Value, "_iBasicType") != 1) error = ErrorCodes.SkillPassive;
            else if (_buffs.HasForbidden(s, new SkillConfig(row.Value).IsNormalAttack(_data)
                ? ForbiddenState.NormalAttack : ForbiddenState.CastSkill)) error = ErrorCodes.SkillBadState;
            else if (req.SkillId == PublicSkill("WorldTreeSkillId") && _buffs.HasForbidden(s, ForbiddenState.UseItem)) error = ErrorCodes.ItemUseForbidden;
            else if (req.SkillId == PublicSkill("WorldTreeSkillId") && !ItemAllowedInMap(s, (int)PublicSkill("WorldTreeItemId"))) error = ErrorCodes.ItemUseAreaForbidden;
            else if (req.IsExtraSkill && owned == null && GameData.Int(row.Value, "_iExtraType") == 0) error = ErrorCodes.SkillRequirements;
        }
        var origin = owned?.Pos ?? s.Position;
        if (error == 0)
        {
            int group = (int)GameData.Int(row!.Value, "_iSkillID"), mode = (int)GameData.Int(row.Value, "_iMode");
            if (own == null && Math.Max(s.SkillCds.GetValueOrDefault(group), s.SkillModeCds.GetValueOrDefault((group, mode))) > Now) error = ErrorCodes.SkillCooldown;
            else if (s.SkillCast is { Cancelled: false } active && InterruptibleAt(active, req.SkillId) is var free && free > Now)
            {
                if (free - Now <= MaxQueuedCastMs) { QueueCast(s, request, free - Now); return; }
                error = ErrorCodes.SkillBusy;
            }
            else if (s.SkillCast is { Cancelled: false } activeSwing && Now < activeSwing.StartMs + activeSwing.ChantMs + activeSwing.ActionMs
                && (!new SkillConfig(row.Value).CancelCast || SwingInFlight(activeSwing, row.Value, req))) error = ErrorCodes.SkillBusy;
            else error = ValidateTarget(s, row.Value, req, origin, owned == null ? WalkSlack(s) : 0,
                _data.Row("SkillConfig", request.SkillId) is { } askedRow ? Range(askedRow) : 0, turret);
        }
        // GmUnlimit's "no consumption" (inferred): no HP, SP, item, buff pile or energy cost.
        bool unlimited = (s.GmCombat & GmCombatSwitches.Unlimited) != 0;
        var costs = row is { } value && !unlimited ? ResolveCosts(s, value) : new CastCosts();
        long errorParam = 0;
        if (error == 0) (error, errorParam) = ValidateCosts(s, costs);
        if (error == 0 && !unlimited && !_buffs.CanSpendEnergy(s, row!.Value)) error = ErrorCodes.SkillEnergyShort;
        if (error != 0)
        {
            await RefuseCastAsync(s, req, error, errorParam);
            return;
        }

        long id = ++_sessionIds;
        var lease = s.Attachment;
        long scene = s.SceneGeneration, worldScene = s.WorldScene;
        // E05c: the receipt is the paid action's admission. Repeating a protocol request
        // is a new intent and pays again; a lost writer ACK resolves this exact source.
        if (!await AdmitCastCostAsync(s, row!.Value, req, costs, id))
        {
            await RefuseCastAsync(s, req, ErrorCodes.SkillCostShort, costs.Items[0].Entry);
            return;
        }
        if (lease?.Cancelled == true || !ReferenceEquals(s.Attachment, lease) ||
            s.SceneGeneration != scene || s.WorldScene != worldScene || s.SceneLoading) return;
        // A cast whose action already ended only frees the slot: its lingering ticks (Magnus Exorcismus) keep going and
        // no interrupt (which clears its effects on the client) is sent. One still running is cut by this new cast.
        if (s.SkillCast is { } running && Now >= running.StartMs + running.ChantMs + running.ActionMs) s.SkillCast = null;
        await CancelCastAsync(s, SkillInterruptedInfo.Types.SkillInterruptType.Skill);
        _eventAttacks.Remove(s);
        await PayCostsAsync(s, costs);
        if (!unlimited) await _buffs.SpendEnergyAsync(s, row!.Value);
        int lead = owned == null && BeforeCast is { } before ? await before(s) : 0;
        var shot = turret ? TurretFired?.Invoke(s, req) : null;
        await s.SendGameAsync(UseSkill, new CastSkillRsp { SessionId = id, IsAutoCast = req.IsAutoCast });
        if (shot != null) await TurretMuzzleAsync(s, req, shot);
        var target = FindMonster(s.WorldScene, req.TargetId);
        var playerTarget = EventTarget(s, req.TargetId);
        // _iIfAutoAtk 2 (enum_AutoNormalAttackType.AutoNormalAttack) follows the cast with the career's normal attack
        // (LC_AutoAttackAppendState: GetNormalSkill). Repeating the cast skill itself bypassed its cooldown: Blade
        // Smash and Sonic Slash fired every swing.
        int loopSkill = new SkillConfig(row!.Value).IsNormalAttack(_data) ? req.SkillId : _skills.NormalAttack(s.Progress!.Career);
        if (GameData.Int(row!.Value, "_iIfAutoAtk") == 2 && owned == null && target != null && CanHitMonster(s, target))
        {
            s.Attack = new AttackLoop
            {
                Target = target, SkillId = loopSkill,
                NextSwingMs = Now + lead + SkillMs(s, row.Value) + ChantMs(s, row.Value) + 1,
                StartX = s.Position.X, StartZ = s.Position.Z, StartedWalking = s.Walking, StartMs = Now,
                ArrivalSpeed = s.Speed / 100f, WalkFromUtc = s.LastWalkUtc
            };
        }
        else s.Attack = null;
        if (GameData.Int(row.Value, "_iIfAutoAtk") == 2 && owned == null && playerTarget != null)
            _eventAttacks[s] = new EventAttack(playerTarget, loopSkill,
                Now + lead + SkillMs(s, row.Value) + ChantMs(s, row.Value) + 1,
                s.Position.X, s.Position.Z);
        if (own != null) await OwnSkillCast!(s, req.SkillId);
        else await StartCooldownAsync(s, row.Value, SkillConfig.Get(_data, request.SkillId) is { } asked ? (int)asked.Mode : null);
        await StartCastAsync(s, row.Value, req, id, owned, lead, turret: shot);
    }

    /// <summary>A skill template's m_kShows entry [effect, kind] of kind 2 is a Gfx point; enum_DisplayEffectType.Gfx is 2.</summary>
    private const int ShowKindGfx = 2, ShowKindAudio = 3, ShowKindShake = 4, DisplayGfx = 2, DisplayShake = 3, DisplayAudio = 4;
    /// <summary>How far a turret hit's shown number strays from TurretShot.Shown either way (the video's four hits:
    /// 9,885,311 to 9,962,385).</summary>
    internal const int TurretShownSpread = 45_000;

    /// <summary>
    /// A turret's fire as its template shows it (inferred: the cannon's fire row 5353001 plays the empty template 61098,
    /// while its own template 53530 has the muzzle flash 5353004 in m_kShows, the ball 5353005 and the blast 5353003):
    /// the template's first Gfx point plays from the turret unit (31119 SkillEffectDisplayNotify of type Gfx); a rapid
    /// template's later flashes repeat the same prefab within 0.15 s, so one flash stands for them.
    /// </summary>
    private async Task TurretMuzzleAsync(GameSession s, CastSkillReq req, TurretShot shot)
    {
        if (s.Role == null || _data.SkillTemplate(shot.Show) is not { } template) return;
        foreach (var (effect, kind, src) in SkillShows.ShowEffects(template, ShowKindGfx).Take(1).Select(e => (e, DisplayGfx, shot.SourceId))
            .Concat(SkillShows.ShowEffects(template, ShowKindShake).Take(1).Select(e => (e, DisplayShake, s.Role.Id)))
            .Concat(SkillShows.ShowEffects(template, ShowKindAudio).Take(1).Select(e => (e, DisplayAudio, shot.SourceId))))
            await ToPlayerAndViewersAsync(s, SkillEffect, new SkillEffectDisplayNotify
            {
                SourceId = src, MasterId = s.Role.Id, TargetId = req.TargetId,
                TargetPos = req.TargetPos?.Clone() ?? shot.Origin.Clone(), EffectId = effect, EffectType = kind,
                SkillId = req.SkillId,
            });
    }

    /// <summary>Cast item cost and frozen intent share one owner receipt; HP/SP/buffs remain runtime resources.
    /// The admission uses the same scheduled cast and lethal pipeline as every ordinary skill.</summary>
    private async Task<bool> AdmitCastCostAsync(GameSession s, JsonElement row, CastSkillReq request, CastCosts costs, long id)
    {
        if (costs.Items.Count == 0) return true;
        var mutation = _character.ProgressionEconomy(s, "cd13c/cast-admit", costs.Items, [],
            new
            {
                SessionId = id, Skill = row.GetRawText(), Request = Convert.ToBase64String(Google.Protobuf.MessageExtensions.ToByteArray(request)),
                Plan = s.SkillPlans?.CurrentPlanIndex, Attachment = s.Attachment?.Generation,
                s.WorldScene, s.SceneGeneration, AttributeCosts = costs.Attrs, BuffCosts = costs.Buffs,
                EnergyCosts = costs.Energy, Outcome = "admitted"
            });
        return await _character.ExchangeLockedAsync(s, costs.Items, [], popup: false,
            persist: tx => tx.WithEconomy(mutation), asyncCommit: true) != null;
    }

    private Task RefuseCastAsync(GameSession s, CastSkillReq req, int error, long parameter) => RefuseCoreAsync(s, req, error, parameter);

    // Refusal must not interrupt a different accepted cast: the client does not compare 31103.sessionId.
    private async Task RefuseCoreAsync(GameSession s, CastSkillReq req, int error, long parameter)
    {
        var reply = new CastSkillRsp { Result = error, IsAutoCast = req.IsAutoCast };
        if (parameter != 0) reply.Params.Add(parameter);
        await s.SendGameAsync(UseSkill, reply);
        if (s.SkillCast is not { Cancelled: false })
        {
            var end = new SkillEndNotify { SrcID = s.Role?.Id ?? 0, Type = SkillEndNotify.Types.SkillEndType.Interrupted, Result = error };
            if (parameter != 0) end.Params.Add(parameter);
            await s.SendGameAsync(SkillEnd, end);
        }
    }

    /// <summary>
    /// A normal attack (_bCancelCast 1) may cut a skill's action short, but not the swing before it at the same target:
    /// otherwise a burst of clicks restarts the swing each time and hits at click speed (issue #121). Turning to another
    /// target (the last one died) is left to the client, which plays the swing out anyway. The client paces swings at the same
    /// ASPD-scaled action time (the normal attack shows' m_fInterruptTime is their whole length) from its own send, so
    /// network jitter can bring a legitimate next swing a little early: <see cref="SwingJitterMs"/> of slack (inferred).
    /// </summary>
    private bool SwingInFlight(ActiveSkillCast active, JsonElement row, CastSkillReq req) =>
        req.TargetId != 0 && req.TargetId == active.Request.TargetId
        && new Data.Config.SkillConfig(row).IsNormalAttack(_data) && new Data.Config.SkillConfig(active.Row).IsNormalAttack(_data)
        && Now < active.StartMs + active.ChantMs + active.ActionMs - SwingJitterMs;

    internal const int SwingJitterMs = 150;

    private bool Learned(GameSession s, JsonElement row)
    {
        int id = (int)GameData.Int(row, "_iID");
        if (id == PublicSkill("WorldTreeSkillId")) return true; // paid item skill, LC_ReviveHelper.
        if (TurretSkill?.Invoke(s, id) == true) return true; // the turret's fire button
        // A Survivor Battle career owns the match skill set (TimedBattleService.Chicken), not the job tree.
        if (MatchSkillRule?.Invoke(s, id) is { } granted) return granted;
        if (id == _skills.NormalAttack(s.Progress!.Career)) return true;
        return _skills.Level(s.Progress, (int)GameData.Int(row, "_iSkillID")) >= GameData.Int(row, "_iLevel");
    }

    private MonsterUnit? FindMonster(long sceneId, long uid) => _world.Map(sceneId).Units.OfType<MonsterUnit>().FirstOrDefault(m => m.Uid == uid && !m.Dead);
    private GameSession? FindPlayer(GameSession s, long uid, bool dead = false) => _world.PlayersIn(s.WorldScene).FirstOrDefault(p => p.Role?.Id == uid && p.Dead == dead);
    private long PublicSkill(string key) => _data.Table("PublicParamConfig").TryGetValue(key, out var value) ? (long)(GameData.Number(value) ?? 0) : 0;
    private static bool Finite(Vec3 p) => float.IsFinite(p.X) && float.IsFinite(p.Y) && float.IsFinite(p.Z);
    private static float Distance(Vec3 a, Vec3 b) => MonsterService.Distance(a.X, a.Z, b.X, b.Z);
    private static float Range(JsonElement row) => Math.Max(0, GameData.Int(row, "_iDistanceMax") / 100f);
    private static string Script(JsonElement row) => row.TryGetProperty("_kScriptLogic", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
    private static double[] Numbers(JsonElement row, string key) => row.TryGetProperty(key, out var list) && list.ValueKind == JsonValueKind.Array
        ? list.EnumerateArray().Select(v => GameData.Number(v) ?? 0).ToArray() : [];
    private static IEnumerable<JsonElement> Tuples(JsonElement row, string key) => row.TryGetProperty(key, out var list) && list.ValueKind == JsonValueKind.Array
        ? list.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.Array) : [];

    private bool Relation(JsonElement row, bool friendly, bool self = false, int race = 0, bool dead = false)
    {
        var template = _data.SkillTemplate(GameData.Int(row, "_iScriptShow"));
        if (template is not { } t || !t.TryGetProperty("m_kSelectInfo", out var selectors) || selectors.ValueKind != JsonValueKind.Array)
            return friendly == (GameData.Int(row, "_iDmgType") == 3); // (inferred) typed recovery fallback for rows without a visual selector.
        foreach (var select in selectors.EnumerateArray())
        {
            int relation = (int)GameData.Int(select, "m_kSelectType"), subtype = (int)GameData.Int(select, "m_kSelectSubType");
            if ((relation & (dead ? friendly ? 32 : 16 : friendly ? 2 : 1)) == 0 && (relation & (dead ? 64 : 4)) == 0) continue;
            if (subtype == 2 && !self || subtype == 16 && self) continue;
            long raceMask = GameData.Int(select, "m_kSelectRaceType");
            if (!friendly && raceMask > 0 && (race <= 0 || (raceMask & (1L << (race - 1))) == 0)) continue;
            return true;
        }
        return false;
    }

    // The client casts on reaching range while it still walks; its stop arrives just after the cast (in-game 11.10:
    // 13 ms later, refused 3024 "invalid skill distance" on most approaches). The cast carries no caster position, so
    // a walking caster's range allows the distance it can have covered since its last walk. The client sends a walk
    // only when its direction changes (in-game 22.10: straight legs of 0.86 s and 0.9 s), so the time is not capped
    // below the 5 s SceneService.AcceptWalkAsync allows.
    internal const double MaxWalkSlackSeconds = 5;
    // How long after an auto attack started on the move its first stop still counts as its start (inferred: the client
    // stops right after the cast; upstream allows 300 ms for a cast's).
    private const long AttackStopGraceMs = 500;
    private static double WalkedSeconds(DateTime since) => Math.Clamp((DateTime.UtcNow - since).TotalSeconds, 0, MaxWalkSlackSeconds);
    private static float WalkSlack(GameSession s) => !s.Walking ? 0 : (float)(s.Speed / 100.0 * WalkedSeconds(s.LastWalkUtc));

    /// <param name="askedRange">The range of the row the client asked for: it walks into that range, not into the
    /// range of a mode row a talent or a mount selects (Arrow Smash 18 m, its wolf row 5 m; issue #62).</param>
    /// <param name="turret">A turret's shot (DungeonService.Cannon): the row's height window and walkable line describe a
    /// cast on foot, not a cannon fired from a deck at an airship below it, so neither applies (inferred).</param>
    private int ValidateTarget(GameSession s, JsonElement row, CastSkillReq req, Vec3 origin, float slack = 0, float askedRange = 0,
        bool turret = false)
    {
        int release = (int)GameData.Int(row, "_iRelease");
        var monster = FindMonster(s.WorldScene, req.TargetId);
        bool revive = Script(row) == "Resurrection";
        var player = req.TargetId == s.Role!.Id ? s : FindPlayer(s, req.TargetId, revive);
        var enemyPlayer = EventTarget(s, req.TargetId);
        Vec3 targetPosition;
        if (release == 1)
        {
            if (monster != null && CanHitMonster(s, monster) && Relation(row, false, race: monster.Template.Race)) targetPosition = monster.Pos;
            else if (!revive && enemyPlayer != null) targetPosition = enemyPlayer.Position;
            else if (player != null && player.Dead == revive && Relation(row, true, player == s, dead: revive)
                && (!revive || !_buffs.HasForbidden(player, ForbiddenState.Revive))) targetPosition = player.Position;
            else if (req.TargetId == 0 && Relation(row, true, true)) { req.TargetId = s.Role.Id; targetPosition = s.Position; }
            else return ErrorCodes.SkillTargetInvalid;
            req.TargetPos = targetPosition.Clone();
            if (revive)
            {
                if (player == s) return ErrorCodes.SkillTargetInvalid;
                if (req.SkillId == PublicSkill("WorldTreeSkillId") ? ReviveItemSkill == null : ReviveOffer == null)
                    return ErrorCodes.SkillReviveNotAllowed;
                if (ReviveCheck?.Invoke(player!) is { } reviveError && reviveError != 0) return reviveError;
            }
        }
        else if (release is 7 or 8)
        {
            if (req.TargetPos == null) return ErrorCodes.SkillTargetInvalid;
            targetPosition = req.TargetPos;
        }
        else { req.TargetPos ??= origin.Clone(); return 0; }
        if (!_buffs.CanSeePosition(s, targetPosition)) return ErrorCodes.SkillTargetInvalid;
        float d = Distance(origin, targetPosition);
        if (d < GameData.Int(row, "_iDistanceMin") / 100f - slack || d > Math.Max(Range(row), askedRange) + 0.5f + slack)
            return ErrorCodes.SkillDistance;
        if (turret) return 0;
        float height = GameData.Int(row, "_iHeightRange") / 100f;
        if (height > 0 && MathF.Abs(origin.Y - targetPosition.Y) > height) return ErrorCodes.SkillHeight;
        // The client's skill ray (RoSpace -> SceneSpaceDynamic.CheckInDirectPassFromTo with _iPassBlock as pass level) is
        // stopped only by an active air wall whose gridLevel exceeds that level: static cells never stop it, because
        // RoSpace builds the scene space without grid levels (RoSpace.LoadSpace -> SceneSpaceStatic.AddHeight, so
        // SceneSpaceStatic.IsBlocked compares level 0). Air walls are switched on by dungeon variable 133 CAirWallList,
        // which this server sets nowhere, so on an open map a tree or a wall corner between caster and target refuses
        // nothing (auto-combat kept re-casting at a target the client saw as free). A siege keeps its (inferred) walls
        // through SkillLineBlocked.
        if (new SkillConfig(row).PassBlock == 2
            && SkillLineBlocked?.Invoke(s, release == 1 ? monster : null, origin, targetPosition) == true)
            return ErrorCodes.SkillBlocked;
        return 0;
    }

    /// <summary>LC_ItemHelper.ItemCanUseInCurrentMap: MapConfig type/subtype blacklist (1) or whitelist (2).</summary>
    private bool ItemAllowedInMap(GameSession s, int entry)
    {
        if (_data.Row("MapConfig", s.SceneEntry) is not { } map || _data.Row("ItemConfig", entry) is not { } item) return false;
        int mode = (int)GameData.Int(map, "_iLimitType");
        if (mode is not (1 or 2)) return true;
        bool listed = Tuples(map, "_kLimitItem").Any(t => t.GetArrayLength() >= 2 && GameData.Number(t[1]) ==
            GameData.Int(item, GameData.Number(t[0]) == 1 ? "_iType" : "_iSubType"));
        return mode == 1 ? !listed : listed;
    }

    private sealed class CastCosts
    {
        public Dictionary<int, long> Attrs { get; } = new();
        public List<(int Entry, long Count)> Items { get; } = new();
        public Dictionary<int, long> Buffs { get; } = new();
        public Dictionary<int, long> Energy { get; } = new();
        public bool Invalid { get; set; }
    }

    private CastCosts ResolveCosts(GameSession s, JsonElement row)
    {
        var result = new CastCosts();
        var stats = _character.Stats(s);
        foreach (var tuple in Tuples(row, "_kCost"))
        {
            var c = tuple.EnumerateArray().Select(v => (long)(GameData.Number(v) ?? 0)).ToArray();
            // (inferred) retained opaque type-4 markers follow the live client checker, which skips them.
            // Lua_GameDefine.enum_SkillCostType / LC_Skill_Function.CheckSkillCost only consume 1/2/3/6/7/8.
            if (c.Length == 2 && c[0] == 4) continue;
            if (c.Length < 3) { result.Invalid = true; continue; }
            int key = (int)c[1];
            long amount = c.Length > 3 ? c[3] : c[2];
            switch (c[0])
            {
                case 1 when c.Length >= 4 && key is 2000 or 2001:
                    amount += stats[key == 2000 ? 101 : 102] * c[2] / 10000;
                    result.Attrs[key] = result.Attrs.GetValueOrDefault(key) + Math.Max(0, amount);
                    break;
                case 7 when c.Length >= 4 && key is 2000 or 2001:
                    // AttrConvert [7, resource, basis-property, ratio], LC_Skill_Function cost checker.
                    result.Attrs[key] = result.Attrs.GetValueOrDefault(key) + Math.Max(0, stats[(int)c[2]] * c[3] / 10000);
                    break;
                case 2: result.Items.Add((key, Math.Max(0, c[2]))); break;
                case 3: result.Buffs[key] = result.Buffs.GetValueOrDefault(key) + Math.Max(0, c[2]); break;
                case 8: result.Energy[key] = result.Energy.GetValueOrDefault(key) + Math.Max(0, amount); break;
                default: result.Invalid = true; break; // reject an unsupported cost rather than grant a free cast.
            }
        }
        if (result.Attrs.TryGetValue(AttributeId.Sp, out long sp)) result.Attrs[AttributeId.Sp] = ModifiedSpCost(_buffs.GuardianAttributes(s), sp);
        return result;
    }

    private (int Error, long Parameter) ValidateCosts(GameSession s, CastCosts costs)
    {
        if (costs.Invalid) return (ErrorCodes.SkillRequirements, 0);
        var stats = _character.Stats(s);
        foreach (var (attr, cost) in costs.Attrs)
            if (cost > stats[attr] || attr == 2000 && cost >= stats[attr]) return (ErrorCodes.SkillCostShort, attr);
        foreach (var (group, count) in costs.Buffs)
            if (!_buffs.HasBuffPiles(s, group, (int)count))
                return (ErrorCodes.SkillBuffCostShort, group);
        return (0, 0);
    }

    private async Task PayCostsAsync(GameSession s, CastCosts costs)
    {
        if (costs.Attrs.Count > 0)
        {
            var stats = _character.Stats(s);
            await _character.SetHpSpAsync(s, stats[2000] - costs.Attrs.GetValueOrDefault(2000), stats[2001] - costs.Attrs.GetValueOrDefault(2001));
        }
        foreach (var (group, count) in costs.Buffs)
            await _buffs.ConsumeBuffPilesAsync(s, group, (int)count);
    }

    /// <summary>Ends a skill group's running cooldowns now, each under the mode it was shown (a talent's reset).</summary>
    internal static async Task ResetCooldownAsync(GameSession s, int group)
    {
        long now = Now;
        var notice = new SkillCdNotify { Uuid = s.Role!.Id };
        foreach (var (key, end) in s.SkillModeCds.Where(c => c.Key.Group == group && c.Value > now).ToArray())
        {
            s.SkillModeCds.Remove(key);
            notice.Infos.Add(new SkillCdInfo { SkillId = group, SkillMode = key.Mode, CdStartTimestampMs = now, CdEndTimestampMs = now });
        }
        if (s.SkillCds.Remove(group) && notice.Infos.Count == 0)
            notice.Infos.Add(new SkillCdInfo { SkillId = group, CdStartTimestampMs = now, CdEndTimestampMs = now });
        if (notice.Infos.Count > 0) await s.SendGameAsync(SkillCdUpdate, notice);
    }

    private async Task StartCooldownAsync(GameSession s, JsonElement row, int? askedMode = null)
    {
        if ((s.GmCombat & GmCombatSwitches.NoCooldown) != 0) return; // GmNoCD / GmUnlimit 1: no skill cooldown starts
        int group = (int)GameData.Int(row, "_iSkillID"), mode = (int)GameData.Int(row, "_iMode");
        long now = Now, cd = GameData.Int(row, "_iCD");
        var notice = new SkillCdNotify { Uuid = s.Role!.Id };
        void Set(int g, int m, long duration, bool shared, int shownMode)
        {
            if (duration <= 0) return;
            long end = now + duration;
            s.SkillModeCds[(g, m)] = Math.Max(end, s.SkillModeCds.GetValueOrDefault((g, m)));
            if (shared) s.SkillCds[g] = Math.Max(end, s.SkillCds.GetValueOrDefault(g));
            notice.Infos.Add(new SkillCdInfo { SkillId = g, SkillMode = shownMode, CdStartTimestampMs = now, CdEndTimestampMs = end });
        }
        // The client files a 31113 under group * 10000 + its mode (LC_Skill_Function.GetSkillCDInfoKey) and its bar reads
        // the key of the row it keeps, the row it asks to cast. A mode a talent or a mount selects (type 15: Joint Beat,
        // Spear Throw, Arrow Smash on the wolf) casts another row, and a cooldown sent under that row's mode never showed
        // (issue #62): it goes under the asked row's mode. Without one, a mode row's _iCdModeGroup (0: mode 0's) decides.
        int cdModeGroup = (int)new SkillConfig(row).CdModeGroup;
        Set(group, mode, cd, cdModeGroup < 0, askedMode ?? (cdModeGroup >= 0 ? cdModeGroup : mode));
        var shared = Numbers(row, "_iPubCD");
        var groups = Numbers(row, "_iPubCDGroup");
        // Parallel arrays, e.g. BackSlide [15104,15330,15329] / [1000,15000,0] (inferred server application).
        for (int i = 0; i < Math.Min(shared.Length, groups.Length); i++)
        {
            int sibling = (int)groups[i];
            if (_data.Row("SkillConfig", sibling) is { } siblingRow) sibling = (int)GameData.Int(siblingRow, "_iSkillID");
            if (sibling > 0) Set(sibling, 0, (long)shared[i], true, 0);
        }
        if (notice.Infos.Count > 0) await s.SendGameAsync(SkillCdUpdate, notice);
    }

    /// <summary>Ends the cooldowns of skill groups (Warg Bite "resetting the Cooldown of [Warg Hunt]").</summary>
    private async Task ResetCooldownsAsync(GameSession s, IReadOnlyList<int> groups)
    {
        long now = Now;
        var notice = new SkillCdNotify { Uuid = s.Role!.Id };
        foreach (int g in groups)
        {
            s.SkillCds.Remove(g);
            foreach (var key in s.SkillModeCds.Keys.Where(k => k.Item1 == g).ToList())
            {
                if (s.SkillModeCds[key] > now)
                    notice.Infos.Add(new SkillCdInfo { SkillId = g, SkillMode = key.Item2, CdStartTimestampMs = now, CdEndTimestampMs = now });
                s.SkillModeCds.Remove(key);
            }
        }
        if (notice.Infos.Count > 0) await s.SendGameAsync(SkillCdUpdate, notice);
    }

    /// <summary>Legacy auto-attack entry point, using the same cancellation-safe cast owner as manual skills.</summary>
    private async Task SwingAsync(GameSession s, MonsterUnit? target, int skillId, long id)
    {
        if (s.Role == null || s.Progress == null) return;
        using var held = await CharacterService.Lock(s);
        if (target != null && !CanHitMonster(s, target)) return;
        if (EffectiveSkillRow(s, skillId) is not { } row || !Learned(s, row) || !SkillModeAllowed(s, row)
            || _buffs.HasForbidden(s, ForbiddenState.NormalAttack) || s.SkillCast is { Cancelled: false }) return;
        var request = new CastSkillReq
        {
            SkillId = (int)GameData.Int(row, "_iID"), TargetId = target?.Uid ?? 0,
            TargetPos = target?.Pos ?? s.Position.Clone(), Face = s.Face
        };
        if (!await AdmitAutomaticCostAsync(s, row, request, id)) { s.Attack = null; return; }
        await StartCastAsync(s, row, request, id);
    }

    private async Task<bool> AdmitAutomaticCostAsync(GameSession s, JsonElement row, CastSkillReq request, long id)
    {
        var costs = ResolveCosts(s, row);
        if (costs.Items.Count == 0) return true;
        if (ValidateCosts(s, costs).Error != 0 || !_buffs.CanSpendEnergy(s, row)) return false;
        var lease = s.Attachment;
        long scene = s.SceneGeneration, worldScene = s.WorldScene;
        bool Current() => lease?.Cancelled != true && ReferenceEquals(s.Attachment, lease) &&
            s.SceneGeneration == scene && s.WorldScene == worldScene && !s.SceneLoading && !s.Dead;
        if (!await AdmitCastCostAsync(s, row, request, costs, id) || !Current()) return false;
        await PayCostsAsync(s, costs);
        await _buffs.SpendEnergyAsync(s, row);
        return Current();
    }

    /// <param name="leadMs">How long the cast waits before it starts (<see cref="BeforeCast"/>): its timeline is sent and its
    /// impact lands that much later, and an interrupt in between cancels it like any cast.</param>
    private async Task StartCastAsync(GameSession s, JsonElement row, CastSkillReq req, long id, SkillObject? actor = null, int leadMs = 0, bool independent = false,
        TurretShot? turret = null)
    {
        var origin = actor?.Pos ?? s.Position.Clone();
        var target = FindMonster(s.WorldScene, req.TargetId);
        float face = target != null ? MonsterService.Angle(origin.X, origin.Z, target.X, target.Z) : req.Face;
        var action = new ActiveSkillCast
        {
            Id = id, OwnerId = s.Role!.Id, WorldScene = s.WorldScene, SceneGeneration = s.SceneGeneration,
            Attachment = s.Attachment, Request = req, Row = row, Origin = origin,
            StartMs = Now + leadMs, ChantMs = ChantMs(s, row), ActionMs = SkillMs(s, row), Operator = actor,
            Independent = independent, Turret = turret,
        };
        if (!independent) s.SkillCast = action;
        // A chanted skill that may not be cast on the move (_bMovement 0 = enum_SkillLockMoveType.Lock) holds the caster
        // still from the press until the cast ends; the client stops the walk and resumes the player's path afterwards.
        if (!independent && actor == null && action.ChantMs > 0 && GameData.Int(row, "_bMovement") == 0)
        {
            await _buffs.SetCastWalkLockAsync(s, id);
            ScheduleRuntime(action.WorldScene, TimeSpan.FromMilliseconds(action.ChantMs + action.ActionMs), async () =>
            {
                if (s.CastWalkLock == id) await _buffs.SetCastWalkLockAsync(s, 0);
            });
        }
        else if (!independent && s.CastWalkLock != 0) await _buffs.SetCastWalkLockAsync(s, 0);
        s.WorldState.ResurrectionCastId = Script(row) == "Resurrection" ? id : 0;
        s.Face = face;
        req.Face = face;
        Task Timeline() => SendCastTimelineAsync(s, new UseInSkillInfo
        {
            SrcID = action.SourceId, SkillID = req.SkillId, TargetId = req.TargetId, TargetPos = req.TargetPos ?? origin,
            Face = face, SessionId = id, ChantMs = action.ChantMs, SkillMs = action.ActionMs, StartTime = action.StartMs, PlayAct = true,
        }, highLevel: GameData.Int(row, "_iSkillEffectLevel") > 0, extra: actor != null || req.IsExtraSkill);
        if (leadMs <= 0) await Timeline();
        else ScheduleRuntime(action.WorldScene, TimeSpan.FromMilliseconds(leadMs), () => Live(s, action) ? Timeline() : Task.CompletedTask);
        if (SkillMechanics.For(row) is { } mechanic) await StartMechanicAsync(s, action, mechanic);
        var template = _data.SkillTemplate(GameData.Int(row, "_iScriptShow"));
        var points = template is { } t ? Numbers(t, "m_kAtkPoints") : [];
        int Ms(double p) => ImpactOffset(template, p, action.ChantMs, action.ActionMs);
        int last = 0;
        foreach (var (at, impact) in CastImpacts(row, points, Ms, action))
        {
            if (impact is not { Detached: true }) last = Math.Max(last, at); // a lingering zone does not hold the cast slot
            ScheduleRuntime(action.WorldScene, TimeSpan.FromMilliseconds(leadMs + at), async () =>
            {
                // A detached moment (a lingering zone's tick) survives the cast's end and the next cast; an interrupt still stops it.
                if (!Live(s, action) && !(impact is { Detached: true } && !action.Cancelled && CurrentCastMap(s, action))) return;
                if (impact is not { First: false }) action.Resolved = true;
                await ResolveCastAsync(s, action, impact);
            });
        }
        // A leap grants its _kBuff1 (High-Energy Impact's flight shield) on take-off; the slam comes at landing.
        if (IsLeapSlam(row) && points.Length > 1)
            ScheduleRuntime(action.WorldScene, TimeSpan.FromMilliseconds(leadMs + Ms(points[0])), async () =>
            {
                if (!Live(s, action)) return;
                foreach (var tuple in Tuples(row, "_kBuff1"))
                    if (Live(s, action) && RollBuff(tuple, out int buff, out int duration)) await _buffs.AddAsync(s, buff, duration, s.Role!.Id);
            });
        ScheduleRuntime(action.WorldScene, TimeSpan.FromMilliseconds(leadMs + Math.Max(last, action.ChantMs + action.ActionMs) + 1), () =>
        {
            if (ReferenceEquals(s.SkillCast, action)) s.SkillCast = null;
            return Task.CompletedTask;
        });
    }

    /// <summary>
    /// What a <see cref="SkillMechanics"/> entry does when the cast starts: the HP it spends (turned into a shield, never
    /// fatal), the buffs it grants its caster, and the skill it releases later (Energy Release's countdown explosion).
    /// </summary>
    private async Task StartMechanicAsync(GameSession s, ActiveSkillCast action, SkillMechanics.Mechanic mechanic)
    {
        var role = s.Role!;
        if (mechanic.SelfHpCostPercent > 0)
        {
            var stats = _character.Stats(s);
            long amount = stats[101] * mechanic.SelfHpCostPercent / 100;
            // A row whose _kCost already charges HP (type 7 on attribute 2000: Energy Release [7, 2000, 101, 5000]) has
            // paid it in the cost step; otherwise the HP is spent here, never fatally.
            bool paid = Tuples(action.Row, "_kCost").Any(t => t.GetArrayLength() > 1 && GameData.Number(t[1]) == 2000);
            if (!paid)
            {
                amount = Math.Min(amount, Math.Max(0, stats[2000] - 1));
                await _character.SetHpSpAsync(s, stats[2000] - amount, null);
            }
            if (mechanic.ShieldBuff is int shield)
                await _buffs.AddSupportAsync(s, shield, mechanic.ShieldMs > 0 ? mechanic.ShieldMs : Math.Max(1000, mechanic.FollowUpDelayMs), role.Id, amount);
        }
        foreach (var (buff, chance, ms) in mechanic.SelfBuffs)
            if (Rolls.Proc(10000) < chance) await _buffs.AddAsync(s, buff, ms, role.Id);
        foreach (int group in mechanic.ConsumePiles ?? [])
            if (await _buffs.ConsumeBuffPilesAsync(s, group, 1)) break;
        // Warg Hunt: "If you are more than «5»m from the target, you also dash in front of it" - its _kLogicData names
        // the near (13329) and far (13330) rows, whose shows hold the three wolves, the hits and the dash movement.
        if (mechanic.DispatchDistance is float near && FindMonster(s.WorldScene, action.Request.TargetId) is { } prey)
        {
            var pair = Numbers(action.Row, "_kLogicData");
            int pick = pair.Length >= 2 ? (int)pair[Distance(s.Position, prey.Pos) > near ? 1 : 0] : pair.Length == 1 ? (int)pair[0] : 0;
            if (pick > 0 && _data.Row("SkillConfig", pick) is { } sub)
                await StartCastAsync(s, sub, new CastSkillReq { SkillId = pick, TargetId = prey.Uid, TargetPos = prey.Pos.Clone(), Face = s.Face, IsExtraSkill = true },
                    ++_sessionIds, independent: true);
        }
        // A fixed row, or the named group at the cast's own level (Auto Counter lv3 releases the lv3 counter).
        int? followId = mechanic.FollowUpSkill
            ?? (mechanic.FollowUpGroup is int fg ? FollowUpRow(fg, (int)GameData.Int(action.Row, "_iLevel")) : null);
        if (followId is int follow && _data.Row("SkillConfig", follow) is { } followRow)
            ScheduleRuntime(action.WorldScene, TimeSpan.FromMilliseconds(mechanic.FollowUpDelayMs), async () =>
            {
                if (!CurrentCastMap(s, action) || !ReferenceEquals(s.Role, role)) return;
                // Its own cast (timeline, hits) that does not take the session's cast slot: the player may already be
                // casting something else when the countdown ends.
                await StartCastAsync(s, followRow, new CastSkillReq { SkillId = follow, Face = s.Face, TargetPos = s.Position.Clone() },
                    ++_sessionIds, independent: true);
            });
    }

    /// <summary>The group's mode-0 row at <paramref name="level"/>, or its highest level below it.</summary>
    private int? FollowUpRow(int group, int level) => _data.Table("SkillConfig").Values
        .Where(r => GameData.Int(r, "_iSkillID") == group && GameData.Int(r, "_iMode") == 0 && GameData.Int(r, "_iLevel") <= Math.Max(1, level))
        .OrderByDescending(r => GameData.Int(r, "_iLevel")).Select(r => (int?)(int)GameData.Int(r, "_iID")).FirstOrDefault();

    /// <summary>Axe Tornado: spins while the caster walks, hitting around it, then a final blow.</summary>
    internal const string SpinScript = "AxeTornado";

    /// <summary>High-Energy Impact: flies (the caster moves) and slams the ground where it lands.</summary>
    internal const string LeapSlamScript = "AxeLeapAttack";

    // The enhanced and talent mode rows carry suffixed scripts (AxeTornadoPro, AxeTornadoProS1BD2, AxeLeapAttack2,
    // AxeLeapAttackS1BD1...) with the same [per tick, final] / take-off + slam layout, so they share the schedule.
    internal static bool IsSpin(JsonElement row) => Script(row).StartsWith(SpinScript, StringComparison.Ordinal);
    internal static bool IsLeapSlam(JsonElement row) => Script(row).StartsWith(LeapSlamScript, StringComparison.Ordinal);

    /// <summary>
    /// When a cast deals its damage. Ordinarily once, at the template's first attack point (attack-point count is not a
    /// hit count: Basic Attack has [0.15, 0.15]). Two scripts are scheduled from their descriptions instead:
    /// - Axe Tornado "spins, dealing total PATK * ${3}%": _kDamageParam1 = [per tick, final]
    ///   (lv1 [10000, 15000], ${3}=750 = 6 x 100 + 150; enhanced 1438 = 10 x 115 + 288). The ticks are spread between
    ///   the second and the next-to-last attack points (16304 [0.017, 0.2, 2.4]: 6 ticks over 0.2-2.4 s, final at
    ///   2.4 s), each around where the caster is then, and the final blow uses branch [1] (_kShowDmg [3, 1]).
    /// - High-Energy Impact takes off at its first attack point (0) and "attacks below" at its last (3.37 s), around
    ///   where the caster has flown to.
    /// </summary>
    private static List<(int At, Impact? Impact)> CastImpacts(JsonElement row, double[] points, Func<double, int> ms, ActiveSkillCast action)
    {
        string script = Script(row);
        if (script == "Resurrection") return [(action.ChantMs, null)];
        if (IsSpin(row) && points.Length >= 2 && SpinTicks(row) is var ticks and > 0)
        {
            double start = points.Length >= 3 ? points[1] : points[0];
            double end = points.Length >= 4 ? points[^2] : points[^1];
            var impacts = new List<(int, Impact?)>();
            for (int i = 0; i < ticks; i++)
                impacts.Add((ms(start + (end - start) * i / ticks), new Impact(0, 1, First: i == 0, SelfEffects: i == 0, AtCaster: true)));
            impacts.Add((ms(points[^1]), new Impact(1, null, First: false, SelfEffects: false, AtCaster: true)));
            return impacts;
        }
        if (IsLeapSlam(row) && points.Length > 1)
            return [(ms(points[^1]), new Impact(0, null, First: true, SelfEffects: false, AtCaster: true))];
        if (SkillMechanics.For(row) is { } mechanic)
        {
            if (mechanic.NoDamage) return [(ms(points.FirstOrDefault(0.2)), new Impact(0, null, First: true, SelfEffects: mechanic.ShieldBuff == null, AtCaster: false, NoDamage: true))];
            if (mechanic.Hits.Count > 0)
                return mechanic.Hits.Select((hit, i) => (hit.Ms is int after
                        ? after + (hit.AfterPoint is int ap && ap < points.Length ? ms(points[ap]) : 0)
                        : ms(hit.Point is int p && p < points.Length ? points[p] : hit.Time ?? points.FirstOrDefault(0.2)),
                    (Impact?)new Impact(hit.Branch, hit.Split, First: i == 0, SelfEffects: i == 0, AtCaster: false, Detached: hit.Ms != null))).ToList();
        }
        // A basic attack whose template strikes at distinct attack points hits at each (the Thief's dual-dagger swing
        // 1500001 [0.217, 0.667]: two numbers per swing in the official footage). Duplicated points ([0.15, 0.15]) are
        // one strike.
        if (GameData.Int(row, "_iIsNormalAtk") == 1 && points.Distinct().Count() > 1 && SkillMechanics.For(row) is null)
            return points.Distinct().Order().Select((p, i) => (ms(p), (Impact?)new Impact(0, null, First: i == 0, SelfEffects: i == 0, AtCaster: false))).ToList();
        return [(ms(points.FirstOrDefault(0.2)), null)];
    }

    /// <summary>Spin ticks so that ticks x _kDamageParam1[0] + _kDamageParam1[1] is the description's total (${3}).</summary>
    internal static int SpinTicks(JsonElement row)
    {
        var damage = Numbers(row, "_kDamageParam1");
        var desc = Numbers(row, "_kDescData");
        if (damage.Length < 2 || damage[0] <= 0 || desc.Length < 3) return 0;
        double ticks = (desc[2] - damage[1] / 100) / (damage[0] / 100);
        return ticks is > 0 and <= 60 ? (int)Math.Round(ticks) : 0;
    }

    /// <summary>Piecewise template time mapping; same scale as LC_Skill_RunTimeContext (inferred for attack points).</summary>
    internal static int ImpactOffset(JsonElement? template, double point, int chantMs, int actionMs)
    {
        double chant = 0, end = 1;
        if (template is { } t && t.TryGetProperty("m_kState", out var state))
        {
            var times = Numbers(state, "Time");
            if (times.Length >= 3) { chant = times[1]; end = times[2]; }
        }
        double ms = point < chant && chant > 0 ? point / chant * chantMs : chantMs + Math.Clamp((point - chant) / Math.Max(0.001, end - chant), 0, 1) * actionMs;
        return (int)Math.Max(0, Math.Round(ms));
    }

    private static bool Live(GameSession s, ActiveSkillCast action) => !action.Cancelled && (action.Independent || ReferenceEquals(s.SkillCast, action))
        && !s.Dead && !s.SceneLoading && s.Role?.Id == action.OwnerId && s.WorldScene == action.WorldScene
        && (action.Operator == null || !action.Operator.Removing && action.Operator.ExpiresMs > Now);

    private int SkillMs(GameSession s, JsonElement row)
    {
        double aspd = Math.Clamp(_character.Stats(s)[115] / 100.0, 0.1, 5);
        double influence = GameData.Int(row, "_iActionTimeInfluence") / 100.0;
        return (int)Math.Round(Math.Max(1, GameData.Int(row, "_iActionTime")) / Math.Max(0.1, 1 + (aspd - 1) * influence));
    }

    public async Task InterruptAsync(GameSession s, SkillInterruptReq? req = null)
    {
        // Releasing a key whose request is still pending sends 31124 for that skill (UI_Sys_Main_PlaySkillWidgetCtrl):
        // it withdraws the held request, not the skill already running.
        if (req is { NxtSkill: null } && req.InterruptSkillId != 0 && _queuedCasts.TryGetValue(s, out var queued)
            && queued.Request.SkillId == req.InterruptSkillId && _queuedCasts.TryRemove(new(s, queued)))
        {
            await s.SendGameAsync(SkillInterrupt, new SkillInterruptResp { IsInterruptSucc = true });
            return;
        }
        // High-Energy Impact: "tap the skill again or wait until the Flying duration ends to attack below" (inferred:
        // the early tap arrives as the player's own interrupt). The slam lands now, where the caster is, instead of
        // cancelling the flight; the scheduled landing then finds the cast finished.
        if (req != null && s.SkillCast is { Resolved: false, Cancelled: false } leap && IsLeapSlam(leap.Row)
            && (req.InterruptSkillId == 0 || req.InterruptSkillId == leap.Request.SkillId) && Live(s, leap))
        {
            leap.Resolved = true;
            s.SkillCast = null;
            await ResolveCastAsync(s, leap, new Impact(0, null, First: true, SelfEffects: false, AtCaster: true));
            await s.SendGameAsync(SkillInterrupt, new SkillInterruptResp { IsInterruptSucc = true });
            if (req.NxtSkill is { } after) await CastAsync(s, after);
            return;
        }
        bool valid = req == null || req.InterruptSkillId == 0 || req.InterruptSkillId == s.SkillCast?.Request.SkillId
            || req.InterruptSkillId == s.Attack?.SkillId || _eventAttacks.TryGetValue(s, out var eventAttack) && req.InterruptSkillId == eventAttack.SkillId;
        bool interrupted = valid && (s.SkillCast != null || s.Attack != null || _eventAttacks.ContainsKey(s));
        if (interrupted)
        {
            s.Attack = null;
            _eventAttacks.Remove(s);
            await CancelCastAsync(s, req?.NxtSkill != null ? SkillInterruptedInfo.Types.SkillInterruptType.Skill : SkillInterruptedInfo.Types.SkillInterruptType.Move);
        }
        await s.SendGameAsync(SkillInterrupt, new SkillInterruptResp { IsInterruptSucc = interrupted });
        if (interrupted && req?.NxtSkill is { } next) await CastAsync(s, next);
    }

    /// <summary>The longest wait a press is held for (PublicParamConfig MaxCacheSkillTime is 2 s on the client).</summary>
    private const int MaxQueuedCastMs = 2000;

    private sealed record QueuedCast(CastSkillReq Request, long Serial);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<GameSession, QueuedCast> _queuedCasts = new();
    private long _queueSerial;

    /// <summary>Holds the latest press and retries it when the running skill allows it (latest press wins).</summary>
    private void QueueCast(GameSession s, CastSkillReq request, long delayMs)
    {
        var entry = new QueuedCast(request.Clone(), Interlocked.Increment(ref _queueSerial));
        _queuedCasts[s] = entry;
        ScheduleRuntime(s.WorldScene, TimeSpan.FromMilliseconds(Math.Max(1, delayMs)), async () =>
        {
            if (!_queuedCasts.TryRemove(new(s, entry))) return; // replaced, withdrawn or already cast
            await CastAsync(s, entry.Request);
        });
    }

    /// <summary>
    /// When the running skill lets <paramref name="nextSkillId"/> cut it (LC_Skill_RunTimeContext.GetSkillCanInterrupted,
    /// verified): _iInterruptedType 1 lets another skill in at once but not itself, 2 from the template's
    /// m_fInterruptTime (mapped like an attack point; after the hit in 585 of 603 templates), and 0 / others only when
    /// its action ends (inferred for the values the client does not handle).
    /// </summary>
    private long InterruptibleAt(ActiveSkillCast active, int nextSkillId)
    {
        long end = active.StartMs + active.ChantMs + active.ActionMs;
        switch ((int)GameData.Int(active.Row, "_iInterruptedType"))
        {
            case 1: return nextSkillId != active.Request.SkillId ? active.StartMs : end;
            case 2:
                var template = _data.SkillTemplate(GameData.Int(active.Row, "_iScriptShow"));
                return template is { } t && t.TryGetProperty("m_fInterruptTime", out var v) && v.ValueKind == JsonValueKind.Number && v.GetDouble() > 0
                    ? Math.Min(end, active.StartMs + ImpactOffset(template, v.GetDouble(), active.ChantMs, active.ActionMs)) : end;
            default: return end;
        }
    }

    private async Task CancelCastAsync(GameSession s, SkillInterruptedInfo.Types.SkillInterruptType cause)
    {
        s.WorldState.ResurrectionCastId = 0;
        if (s.SkillCast is not { } action) return;
        action.Cancelled = true;
        s.SkillCast = null;
        if (s.CastWalkLock == action.Id) await _buffs.SetCastWalkLockAsync(s, 0);
        await ToPlayerAndViewersAsync(s, SkillEnd, new SkillEndNotify
        {
            SrcID = action.SourceId, SessionId = action.Id, Type = SkillEndNotify.Types.SkillEndType.Interrupted,
            ClearEffect = true, InterruptedInfo = new SkillInterruptedInfo { Type = cause },
        });
    }

    public void PlayerMoved(GameSession s)
    {
        if (_eventAttacks.TryGetValue(s, out var eventAttack)
            && MonsterService.Distance(eventAttack.StartX, eventAttack.StartZ, s.Position.X, s.Position.Z) > 1.5f) _eventAttacks.Remove(s);
        if (s.Attack is { } a && MonsterService.Distance(a.StartX, a.StartZ, s.Position.X, s.Position.Z) > 0.1f)
        {
            // Clicking an enemy from afar: the client attacks on reaching range and stops a few ms later (in-game
            // 11.10), so that first stop is where the auto-attack starts, not a step away that ends it. Hero Saga: the
            // grace is AttackStopGraceMs (500 ms, tested in game 22.10) instead of upstream's 300 ms (inferred).
            if (a.StartedWalking && !s.Walking && Now - a.StartMs <= AttackStopGraceMs
                && MonsterService.Distance(a.StartX, a.StartZ, s.Position.X, s.Position.Z) <= a.ArrivalSpeed * WalkedSeconds(a.WalkFromUtc) + 0.5)
            {
                a.StartX = s.Position.X; a.StartZ = s.Position.Z;
                a.StartedWalking = false;
            }
            else s.Attack = null;
        }
        // A walk never interrupts a cast here (verified in the client): moving when a skill starts plays it on the move
        // (LC_Skill_RunTimeContext skillBlockMove / PlayMoveCacheAnimName), and the client interrupts by movement itself
        // only once the template's m_fInterruptTime_move has passed (canInterruptedByMove -> SkillInterruptServer),
        // which arrives as 31124 (InterruptAsync). Cancelling on every walk packet dropped skills cast while walking.
    }
}
