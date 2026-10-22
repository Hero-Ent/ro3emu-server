using System.Text.Json;
using Romsg;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

/// <summary>An owned skill NPC/trigger. NPCConfig identity and MG_Entity.owner survive AOI re-entry.</summary>
public sealed class SkillObject : WorldUnit
{
    public required GameSession OwnerSession { get; init; }
    public required long OwnerId { get; init; }
    public required int NpcId { get; init; }
    public required int EntityType { get; init; }
    public required ActiveSkillCast Cast { get; init; }
    public required long ExpiresMs { get; init; }
    public long NextTickMs { get; set; }
    public int IntervalMs { get; init; }
    public float Radius { get; init; }
    public bool Trap { get; init; }
    public bool Busy { get; set; }
    public bool Removing { get; set; }
    public int Charges { get; set; }
    public int ProtectionDistanceType { get; init; }
    public HashSet<int> Skills { get; } = new();
    /// <summary>Damage ticks left (Meteor Storm's meteor count, _kLogicData[5]); null = until it expires.</summary>
    public int? TicksLeft { get; set; }
    /// <summary>Enemy touches left before it vanishes (Fire Wall "disappears after being touched ${7} times").</summary>
    public int? TouchesLeft { get; set; }
    /// <summary>Knockback away from the object on each hit, metres (Fire Wall ${5}).</summary>
    public float Knockback { get; init; }
    /// <summary>Targets per tick when the row's _iTargetMax is not the described count (traps "up to 5 enemies").</summary>
    public int? MaxTargets { get; init; }
    /// <summary>Enemies the last tick hit.</summary>
    public int LastHits { get; set; }

    public override EntityBase Entity() => new()
    {
        Uuid = (ulong)Uid, EntityTemplateID = (ulong)NpcId, EntityType = EntityType, Owner = OwnerId,
        AiMasterEntityId = (ulong)OwnerId, EntityCamp = 1, State = EntityState.Alive, BornTimeMs = Cast.StartMs,
        Face = Face, MovementInfo = new MovementInfo { Pos = Pos, Face = Face, ServerTime = MonsterService.NowMs },
        NotifyType = EntityBase.Types.NotifyType.All,
    };
}

public sealed partial class CombatService
{
    private long _skillObjectIds = 0x600000000000L;

    /// <summary>
    /// One damage moment of a cast. Ordinary casts have one (null = defaults); a spin (Axe Tornado) has several and a
    /// leap (High-Energy Impact) lands later. Branch selects _kDamageParam1/_kShowDmg, Split overrides the segments,
    /// First owns the one-time cast work (handlers, fields, caster displacement), SelfEffects the _kBuff1 grant and
    /// AtCaster centres a ground shape on where the caster is now. Forced hits exactly that enemy (a bounce).
    /// </summary>
    private sealed record Impact(int Branch, int? Split, bool First, bool SelfEffects, bool AtCaster, bool NoDamage = false, bool Detached = false,
        MonsterUnit? Forced = null);

    private async Task ResolveCastAsync(GameSession s, ActiveSkillCast action, Impact? impact = null)
    {
        var row = action.Row;
        if (!CurrentCastMap(s, action)) return;
        if (impact is not { First: false })
        {
            foreach (var handler in CastLanded)
            {
                await handler(s, row);
                if (!CurrentCastMap(s, action)) return;
            }
            if (Script(row) == "Resurrection")
            {
                var target = FindPlayer(s, action.Request.TargetId, dead: true);
                if (target == null || target.SceneLoading || buffs.HasForbidden(target, 16)
                    || s.WorldState.ResurrectionCastId != action.Id
                    || Distance(s.Position, target.Position) > Range(row) + 1.5f) return;
                s.WorldState.ResurrectionCastId = 0;
                if (action.Request.SkillId == PublicSkill("WorldTreeSkillId"))
                {
                    if (ReviveItemSkill != null && await ReviveItemSkill(s, target, row))
                        await buffs.NotifyWorldTreeReviveAsync(s, target.Role!.Name);
                }
                else if (ReviveOffer != null) await ReviveOffer(s, target, row);
                return;
            }
            if (await CreateSkillObjectAsync(s, action)) return;
            if (!CurrentCastMap(s, action)) return;
            // SkillConfig._bMovement is enum_SkillLockMoveType (may the caster walk while casting), not a displacement.
            await MoveCasterAsync(s, action);
            if (!CurrentCastMap(s, action)) return;
            await DashAsync(s, action);
            if (!CurrentCastMap(s, action)) return;
        }
        var center = impact?.AtCaster == true ? s.Position.Clone() : action.Request.TargetPos ?? action.Origin;
        await ApplyEffectsAsync(s, action, center, impact: impact);
        if (impact is not { First: false } && SkillMechanics.For(row) is { ResetCooldown: { Count: > 0 } resets } && CurrentCastMap(s, action))
            await ResetCooldownsAsync(s, resets);
        if (impact is not { First: false } && SkillMechanics.For(row) is { Chain: { } chain })
            await BounceAsync(s, action, chain, impact?.Branch ?? 0);
    }

    /// <summary>The main hit jumps on to random enemies near the last one hit (SkillMechanics bounce).</summary>
    private async Task BounceAsync(GameSession s, ActiveSkillCast action, SkillMechanics.Bounce chain, int branch)
    {
        var row = action.Row;
        var last = FindMonster(s.WorldScene, action.Request.TargetId);
        if (last == null) return;
        var struck = new HashSet<long> { last.Uid };
        var at = last.Pos.Clone();
        for (int jump = 0; jump < chain.Count; jump++)
        {
            if (chain.DelayMs > 0) await Task.Delay(chain.DelayMs);
            if (!CurrentCastMap(s, action) || s.Dead) return;
            var from = at;
            var candidates = world.Map(s.WorldScene).Units.OfType<MonsterUnit>()
                .Where(m => m.Hp > 0 && !struck.Contains(m.Uid) && CanHitMonster(s, m) && Relation(row, false, race: m.Template.Race)
                    && Distance(from, m.Pos) <= chain.Radius + 0.1f)
                .OrderBy(m => m.Uid).ToList();
            if (candidates.Count == 0) return;
            var next = candidates[Rolls.Proc(candidates.Count)];
            struck.Add(next.Uid);
            at = next.Pos.Clone();
            await ApplyEffectsAsync(s, action, at, impact: new Impact(branch, null, First: false, SelfEffects: false, AtCaster: false, Detached: true, Forced: next));
        }
    }

    private List<MonsterUnit> SelectEnemies(GameSession s, ActiveSkillCast action, Vec3 center, float? radiusOverride = null, bool field = false, int? maxOverride = null)
    {
        var row = action.Row;
        int max = maxOverride ?? (int)Math.Max(1, GameData.Int(row, "_iTargetMax"));
        var areaMechanic = field || radiusOverride != null ? null : SkillMechanics.For(row);
        // A mechanic shape turns a single-target row into an area around its anchor (Blitz Beat's splash, Sharp Shooting's line).
        bool shaped = areaMechanic is { Circle: not null } or { Rect: not null } or { Fan: not null };
        if (GameData.Int(row, "_iRangeType") != 2 && radiusOverride == null && !shaped)
        {
            var target = FindMonster(s.WorldScene, action.Request.TargetId);
            return target != null && target.Hp > 0 && CanHitMonster(s, target) && Relation(row, false, race: target.Template.Race) ? [target] : [];
        }
        // enum_SkillReleaseType (Lua_CFG_SkillConfig): 1 Lock, 2 FixCircle, 3 FixRect, 4 Fan, 5 Arrow, 6 Rect360,
        // 7 DragRect, 8 DragCircle. The caster-anchored shapes (2-6) are measured from where the caster is at the
        // impact, not where the cast started: Axe Tornado is cast while walking and keeps hitting around the caster.
        int release = (int)GameData.Int(row, "_iRelease");
        var mechanic = field || radiusOverride != null ? null : SkillMechanics.For(row);
        Vec3 CasterNow() => action.Operator?.Pos ?? (s.WorldScene == action.WorldScene ? s.Position : action.Origin);
        Vec3 LockedTarget() => FindMonster(s.WorldScene, action.Request.TargetId)?.Pos ?? action.Request.TargetPos ?? action.Origin;
        if (release is not (1 or 7 or 8) && radiusOverride == null && !field) center = CasterNow();
        // A locked area skill ("the selected enemy and up to N others within X m of them": Cart Revolution, Arm Cannon,
        // Pile Bunker) splashes around the locked target, not around a requested ground point.
        else if (release == 1 && radiusOverride == null && !field && action.Request.TargetId != 0) center = LockedTarget();
        if (mechanic != null)
            center = mechanic.At switch
            {
                SkillMechanics.Anchor.Caster => CasterNow(),
                SkillMechanics.Anchor.Origin => action.Origin,
                SkillMechanics.Anchor.Target => LockedTarget(),
                SkillMechanics.Anchor.Point => action.Request.TargetPos ?? action.Origin,
                _ => center,
            };
        if (mechanic?.MaxTargets is int cap) max = cap;
        // "within a «5»-meter radius centered «1» meters in front of You" (Shadow Break): the anchor moved along the facing.
        if (mechanic is { Offset: not 0 } shifted)
        {
            double facing = action.Request.Face * Math.PI / 180;
            center = new Vec3 { X = center.X + (float)(Math.Sin(facing) * shifted.Offset), Y = center.Y, Z = center.Z + (float)(Math.Cos(facing) * shifted.Offset) };
        }
        var range = Numbers(row, "_kRange");
        float radius = radiusOverride ?? mechanic?.Circle ?? mechanic?.Fan?.Radius ?? (float)(range.FirstOrDefault(300) / 100);
        float height = GameData.Int(row, "_iAreaHeightRange") / 100f;
        // (verified) _kRange[0] is the length along the facing and [1] the width: GetIndicatorRange returns
        // {_kRange[0], _kRange[1]} / 100 for these, and the descriptions agree (Pinpoint Attack 950x400
        // "9m-long, 4m-wide", Heavy Cannon Shot 1600x500 "5 * 15-meter area in front", Fire Wall 100x300
        // "each 3*1"). A dragged rectangle (7) is centred on the chosen point; the others start at the caster.
        (float Length, float Width)? rect = mechanic?.Rect
            ?? (radiusOverride == null && mechanic?.Circle == null && mechanic?.Fan == null && release is 3 or 5 or 6 or 7
                ? (radius, range.Length > 1 ? (float)(range[1] / 100) : radius) : null);
        bool centred = mechanic?.At == SkillMechanics.Anchor.Point || mechanic?.At is null or SkillMechanics.Anchor.Default && release == 7;
        double fanDegrees = mechanic?.Fan?.Degrees ?? (range.Length > 1 ? range[1] : 90);
        bool fan = mechanic?.Fan != null || radiusOverride == null && mechanic?.Circle == null && release == 4;
        bool InShape(MonsterUnit m)
        {
            if (height > 0 && MathF.Abs(m.Y - center.Y) > height) return false;
            float dx = m.X - center.X, dz = m.Z - center.Z;
            double radians = action.Request.Face * Math.PI / 180;
            double forward = dx * Math.Sin(radians) + dz * Math.Cos(radians), side = dx * Math.Cos(radians) - dz * Math.Sin(radians);
            if (rect is var (length, width))
            {
                if (Math.Abs(side) > width / 2) return false;
                return centred ? Math.Abs(forward) <= length / 2 : forward >= -0.5 && forward <= length;
            }
            if (Distance(center, m.Pos) > radius + 0.1f) return false;
            return !fan || Math.Atan2(Math.Abs(side), forward) <= fanDegrees * Math.PI / 360;
        }
        bool Allowed(MonsterUnit m) => (mechanic is not { ExcludeTarget: true } || m.Uid != action.Request.TargetId) // "2 other enemies near the target"
            && (mechanic is not { } only || (only.OnlyRaces.Count == 0 && only.OnlyElements.Count == 0)
                || only.OnlyRaces.Contains(m.Template.Race) || only.OnlyElements.Contains(m.Template.Element));
        return world.Map(s.WorldScene).Units.OfType<MonsterUnit>().Where(m => m.Hp > 0 && CanHitMonster(s, m) && Relation(row, false, race: m.Template.Race) && Allowed(m) && InShape(m))
            .OrderBy(m => Distance(center, m.Pos)).ThenBy(m => m.Uid).Take(max).ToList();
    }

    private async Task ApplyEffectsAsync(GameSession s, ActiveSkillCast action, Vec3 center, SkillObject? field = null, Impact? impact = null)
    {
        if (s.Dead || s.Role?.Id != action.OwnerId || s.WorldScene != action.WorldScene || s.SceneLoading
            || s.SceneGeneration != action.SceneGeneration || field?.Removing == true
            || !ReferenceEquals(s.Attachment, action.Attachment) || action.Attachment?.Cancelled == true) return;
        var row = action.Row;
        var castingRole = s.Role;
        bool CurrentCaster() => ReferenceEquals(s.Role, castingRole) && !s.SceneLoading
            && s.SceneGeneration == action.SceneGeneration && (field != null ? !field.Removing : !action.Cancelled)
            && s.WorldScene == action.WorldScene && sessions.All().Contains(s);
        bool CanApplyMoreEffects() => CurrentCaster() && ReferenceEquals(s.Attachment, action.Attachment)
            && action.Attachment?.Cancelled != true;
        // A published death still owes its callbacks after the cast/field is cancelled.
        // A cancelled but unchanged lease can finish committed consequences under its admitted owner.
        bool CurrentKillRecipient() => ReferenceEquals(s.Role, castingRole) && !s.SceneLoading
            && s.SceneGeneration == action.SceneGeneration && s.WorldScene == action.WorldScene
            && ReferenceEquals(s.Attachment, action.Attachment)
            && sessions.All().Contains(s);
        string script = Script(row);
        // Heal's enemy branch is [1] for damage, and so is its segment count; a spin's final hit is branch [1] too.
        int branch = impact?.Branch ?? (script == "Heal" ? 1 : 0);
        int split = impact?.Split ?? HitSplit(row, branch);
        var targets = impact?.Forced is { } forced
            ? (forced.Hp > 0 && !forced.Dead && CanHitMonster(s, forced) ? [forced] : new List<MonsterUnit>())
            : SelectEnemies(s, action, center, field?.Trap == true ? field.Radius : null, field: field != null, maxOverride: field?.MaxTargets);
        var playerTargets = impact?.Forced != null ? []
            : GameData.Int(row, "_iRangeType") == 2 ? EventAreaTargets(s, row)
            : EventTarget(s, action.Request.TargetId) is { } eventTarget
                && PlayerDistance(s, eventTarget) <= Range(row) + 1.5f ? new[] { eventTarget } : [];
        if (playerTargets.Length > 0)
        {
            int maxTargets = checked((int)Math.Max(1, GameData.Int(row, "_iTargetMax")));
            var selected = targets.Select(m => (Id: m.Uid, Distance: MonsterService.Distance(m, s)))
                .Concat(playerTargets.Select(p => (Id: p.Role!.Id, Distance: PlayerDistance(s, p))))
                .OrderBy(p => p.Distance).ThenBy(p => p.Id).Take(maxTargets).Select(p => p.Id).ToHashSet();
            targets.RemoveAll(m => !selected.Contains(m.Uid));
            await HitEventPlayersAsync(s, playerTargets.Where(p => selected.Contains(p.Role!.Id)).ToArray(),
                row, action.Request.SkillId, HitEffect(GameData.Int(row, "_iScriptShow")), action.Id, CanApplyMoreEffects);
        }
        if (!CanApplyMoreEffects()) return; // player publication can yield before any monster hit is committed
        int bulletEffect = BulletEffect(GameData.Int(row, "_iScriptShow"));
        if (bulletEffect > 0 && field == null && WolfRunMs(bulletEffect, 0) != null)
        {
            // A Wolf-track "bullet" (m_eBulletType 8) is the owner's Wolf pendant charging (verified in the client:
            // LC_PerformEffect_Mgr.PlayPendantShow -> LC_Pendant_Manager.PendantPerform -> LC_Pendant_Wolf Perform). It is a
            // SkillEffectDisplayNotify of type 5 (enum_DisplayEffectType.PendantShow); as a SkillFixTimeBulletNotify it
            // drew nothing (Wolf has no bullet track), so unmounted Warg Strike showed no wolf running to the target.
            bool highLevel = GameData.Int(row, "_iSkillEffectLevel") > 0;
            int run = 0;
            foreach (var target in targets)
            {
                await SendSkillEffectAsync(s, new SkillEffectDisplayNotify
                {
                    SourceId = s.Role.Id, MasterId = s.Role.Id, TargetId = target.Uid, TargetPos = target.Pos.Clone(),
                    EffectId = bulletEffect, EffectType = 5, SkillId = (int)GameData.Int(row, "_iID"),
                }, highLevel);
                run = Math.Max(run, WolfRunMs(bulletEffect, Distance(s.Position, target.Pos)) ?? 0);
            }
            // The damage lands when the wolf reaches the target (inferred from m_kBulletWolf's speed/time).
            if (run > 50 && targets.Count > 0)
            {
                await Task.Delay(Math.Min(run, 1000));
                if (!CanApplyMoreEffects()) return;
            }
        }
        else if (bulletEffect > 0 && field == null)
        {
            float maxDist = targets.Select(t => Distance(action.Origin, t.Pos))
                .Concat(playerTargets.Select(p => Distance(action.Origin, p.Position)))
                .DefaultIfEmpty(10f).Max();
            int flyTime = BulletLifeTime(bulletEffect, maxDist);
            bool highLevel = GameData.Int(row, "_iSkillEffectLevel") > 0;
            foreach (var target in targets)
            {
                var bullet = new SkillFixTimeBulletNotify
                {
                    SourceId = action.SourceId,
                    TargetId = target.Uid,
                    EffectId = bulletEffect,
                    FixedTime = flyTime,
                    Split = split,
                    TargetPosition = target.Pos,
                    SkillConfigId = (int)GameData.Int(row, "_iID"),
                    MasterId = action.SourceId,
                    SourcePosition = action.Origin,
                    StartTimestampMs = Now,
                };
                await SendSkillBulletAsync(s, bullet, highLevel: highLevel);
            }
            foreach (var pTarget in playerTargets)
            {
                var bullet = new SkillFixTimeBulletNotify
                {
                    SourceId = action.SourceId,
                    TargetId = pTarget.Role!.Id,
                    EffectId = bulletEffect,
                    FixedTime = flyTime,
                    Split = split,
                    TargetPosition = pTarget.Position,
                    SkillConfigId = (int)GameData.Int(row, "_iID"),
                    MasterId = action.SourceId,
                    SourcePosition = action.Origin,
                    StartTimestampMs = Now,
                };
                await SendSkillBulletAsync(s, bullet, highLevel: highLevel);
            }
            if (flyTime > 50 && (targets.Count > 0 || playerTargets.Length > 0))
            {
                await Task.Delay(flyTime);
                if (!CanApplyMoreEffects()) return;
            }
        }
        bool friendly = Relation(row, true, true) || Relation(row, true);
        var allies = new List<GameSession>();
        if (friendly)
        {
            if (GameData.Int(row, "_iRelease") == 1)
            {
                var ally = action.Request.TargetId == s.Role.Id ? s : FindPlayer(s, action.Request.TargetId);
                if (ally != null && Relation(row, true, ally == s)) allies.Add(ally);
            }
            else
            {
                float radius = field?.Radius ?? (float)(Numbers(row, "_kRange").FirstOrDefault(0) / 100);
                allies.AddRange(world.PlayersIn(s.WorldScene).Where(p => !p.Dead && Relation(row, true, p == s)
                    && (p == s || radius > 0 && Distance(p.Position, center) <= radius))
                    .OrderBy(p => Distance(p.Position, center)).Take((int)Math.Max(1, GameData.Int(row, "_iTargetMax"))));
                if (allies.Count == 0 && Relation(row, true, true)) allies.Add(s);
            }
        }
        var stats = character.Stats(s);
        var attacker = new Fighter(stats[103], stats[104], stats[109], stats[111], s.Progress!.Level, 1) { Stats = stats };
        var hit = new SkillHitInfo
        {
            SrcID = field?.Uid ?? action.SourceId, SkillId = action.Request.SkillId, SessionId = action.Id,
            RootCtxGuid = action.Id, ComboIndex = action.Request.ComboIndex, ShowPoint = HitEffect(GameData.Int(row, "_iScriptShow")),
            SrcElement = (int)Math.Max(1, GameData.Int(row, "_iEleType")),
            // Heals on allies are one number; damage segments only when an enemy is hit.
            Split = targets.Count > 0 ? split : 1,
        };
        var killed = new List<MonsterUnit>();
        var doubles = new List<(MonsterUnit Target, long Amount, DamageHitResult Result)>();
        var processed = new List<MonsterUnit>();
        bool damage = impact?.NoDamage != true && (Numbers(row, "_kDamageParam1").Any(v => v > 0) || Numbers(row, "_kDamageParam2").Any(v => v > 0));
        var mechanic = field == null ? SkillMechanics.For(row) : null;
        bool casterCondition = mechanic?.BranchIf is { CasterBuffGroup: int needGroup } && HasBuffGroup(s, needGroup);
        try
        {
            foreach (var target in targets)
            {
                if (!CanApplyMoreEffects()) break; // a previous target's awaited callback may have invalidated the caster
                if (target.Dead || target.Hp <= 0) continue; // another awaited cast may already own this death source
                processed.Add(target);
                if (damage)
                {
                    var defender = new Fighter(0, 0, target.Template.Flee, 0, target.Template.Level, target.Template.Element)
                    { Def = target.Template.Def, Mdef = target.Template.Mdef, Flee = target.Template.Flee };
                    // A bonus branch against named races/elements (Soul Strike +50% vs Undead, Magnus vs Undead/Demon).
                    int targetBranch = mechanic is { } bm && (bm.BonusRaces.Contains(target.Template.Race) || bm.BonusElements.Contains(target.Template.Element))
                        ? bm.BonusBranch : branch;
                    // A conditional branch: the target's HP low enough (Sonic Blow) or a caster buff (Steal from Hiding).
                    if (mechanic?.BranchIf is { } cond && (casterCondition
                            || cond.TargetHpBelowBp > 0 && target.Hp * 10000 <= target.Template.MaxHp * cond.TargetHpBelowBp))
                        targetBranch = cond.Branch;
                    var (amount, result) = Damage(attacker, defender, row, targetBranch);
                    if (amount > 0)
                    {
                        foreach (var modifier in DamageAdjusters) amount = Math.Max(0, modifier(s, target, row, result, amount));
                        foreach (var extra in ExtraHitDamage) amount += Math.Max(0, extra(s, target, row, amount));
                        // Double Attack: once per swing, an extra hit lands with the first one, shown as its own normal number.
                        if (field == null && impact is not { First: false } && DoubleAttackRolls(s, row) is { } second)
                        {
                            var (extraAmount, extraResult) = Damage(attacker, defender, second);
                            if (extraAmount > 0) { amount += extraAmount; doubles.Add((target, extraAmount, extraResult)); }
                        }
                    }
                    long beforeHp = target.Hp;
                    target.Hp = Math.Max(0, beforeHp - amount);
                    long effective = beforeHp - target.Hp;
                    if (target.Hp == 0) killed.Add(target); // reserve before awaited damage callbacks
                    await SettleMonsterHitAsync(s, target, beforeHp, amount);
                    if (CanApplyMoreEffects())
                        foreach (var handler in DealtAsync)
                        {
                            if (!CanApplyMoreEffects()) break;
                            _dealingIndependent = action.Independent;
                            await handler(s, target, row, amount);
                        }
                    _dealingIndependent = false;
                    // The hit is already committed; an unchanged (even cancelled) lease
                    // still owns its damage callback, while a replacement lease does not.
                    if (effective > 0 && CurrentKillRecipient())
                        foreach (var handler in MonsterDamageApplied)
                        {
                            if (!CurrentKillRecipient()) break;
                            try { await handler(s, target, effective); }
                            catch (Exception ex) { log.LogError(ex, "Monster damage callback failed for {Uid}", target.Uid); }
                        }
                    long extraShown = doubles.Where(d => d.Target == target).Sum(d => d.Amount);
                    hit.Dsts.Add(new DamageInfo { DstID = target.Uid, Damage = amount - extraShown, HitResult = result, DstElement = target.Template.Element, IsDead = target.Hp == 0 && extraShown == 0 });
                }
                if (CanApplyMoreEffects())
                    foreach (var tuple in Tuples(row, "_kBuff2"))
                        if (CanApplyMoreEffects() && RollHostileBuff(tuple, s, null, null, target, out int id, out int duration))
                            await buffs.ShowOnUnitAsync(target, id, duration, castingRole.Id);
                // Statuses a description names that the row's _kBuff2 lacks (Pile Bunker's 0.5 s Stun).
                if (mechanic != null && CanApplyMoreEffects())
                    foreach (var (buff, chance, ms) in mechanic.TargetBuffs)
                        if (CanApplyMoreEffects() && Rolls.Proc(10000) < chance) await buffs.ShowOnUnitAsync(target, buff, ms, castingRole.Id);
                if (CanApplyMoreEffects() && script is ("Provoke" or "Taunt")) monsters.Engage(target, s);
                if (CanApplyMoreEffects() && !target.Dead && target.Hp > 0) await DisplaceTargetAsync(s, action, target, mechanic?.Knockback ?? field?.Knockback ?? 0, mechanic?.Pull ?? 0, center, field?.Pos);
            }
            // Buff1 is self for hostile selectors, but actual friend recipients for ally selectors (Detoxify, Blessing).
            foreach (var recipient in friendly ? allies : new List<GameSession> { s })
            {
                if (!CanApplyMoreEffects()) break;
                if (field == null && impact?.SelfEffects != false)
                {
                    foreach (var tuple in Tuples(row, "_kBuff1"))
                    {
                        if (!CanApplyMoreEffects() || !RollBuff(tuple, out int id, out int duration)) continue;
                        if (mechanic?.AtStacks is { } gate && recipient == s && gate.Grants.Contains(id)) continue; // held for the counter
                        await buffs.AddAsync(recipient, id, duration, castingRole.Id);
                    }
                    if (mechanic?.AtStacks is { } stack && recipient == s && CanApplyMoreEffects()
                        && s.Buffs.FirstOrDefault(b => b.ConfigId == stack.Counter) is { } counter && counter.PileCount >= stack.At)
                    {
                        await buffs.RemoveAsync(s, counter);
                        foreach (var tuple in Tuples(row, "_kBuff1"))
                            if (tuple.GetArrayLength() >= 3 && (int)(GameData.Number(tuple[0]) ?? 0) is var grant && stack.Grants.Contains(grant))
                                await buffs.SetPileCountAsync(s, grant, (int)(GameData.Number(tuple[2]) ?? 0), castingRole.Id, stack.Piles);
                    }
                }
                if (friendly && damage)
                {
                    double multiplier = Numbers(row, "_kDamageParam1").FirstOrDefault() * StatScale(row, id => stats[id], s.Progress!.Level), flat = Numbers(row, "_kDamageParam2").FirstOrDefault();
                    // Heal's friendly branch is P1[0]/P2[0]; its enemy branch is [1] (description + select race mask).
                    long amount = (long)Math.Max(0, Math.Round(stats[104] * multiplier / 10000 + flat));
                    foreach (var modifier in ExtraHeal) amount = modifier(s, amount);
                    var before = character.Stats(recipient);
                    long actual = Math.Min(amount, Math.Max(0, before[101] - before[2000]));
                    await character.SetHpSpAsync(recipient, before[2000] + amount, null);
                    hit.Dsts.Add(new DamageInfo { DstID = recipient.Role!.Id, Damage = actual, DamageType = DamageType.Heal, DstElement = 1 });
                    foreach (var handler in Healed) handler(s, actual);
                    foreach (var handler in HealedAsync) await handler(s, actual);
                }
                // Healing Skills (14014) and Cleanse (11406) both use the script "Cleanse": "Removes [Silence] and [Blind]".
                // A mechanic "cleanse" names the BuffConfig _iBuffID groups itself (Antidote: Poisoned 9030).
                if (mechanic?.Cleanse is { Count: > 0 } || script is "Detoxify" or "HealingSkills" or "Cleanse")
                {
                    // (inferred) cleanse exact described groups rather than removing every beneficial/hostile status.
                    IReadOnlyList<int> groups = mechanic?.Cleanse is { Count: > 0 } named ? named : script == "Detoxify" ? [9030] : [9023, 9025];
                    foreach (var buff in recipient.Buffs.Where(b => data.Row("BuffConfig", b.ConfigId) is { } br && groups.Contains((int)GameData.Int(br, "_iBuffID"))).ToArray())
                        await buffs.RemoveAsync(recipient, buff);
                }
            }
            if (hit.Dsts.Count > 0)
            {
                foreach (var result in hit.Dsts)
                {
                    if (!CurrentCaster()) break;
                    await RecordDamageStatisticAsync(s, new DamageStatistics
                    {
                        DamageSourceId = hit.SrcID, DamageTargetId = result.DstID, DamageValue = result.Damage,
                        DamageBeShield = result.SheildDamage, DamageType = result.DamageType, HitResult = result.HitResult, SkillId = hit.SkillId,
                    });
                }
                if (CurrentCaster() && hit.Dsts.Any(d => d.HitResult != DamageHitResult.Miss)) await buffs.RecoverEnergyOnHitAsync(s, row);
                var sourceViewers = CurrentCaster() ? world.Viewers(s).Append(s) : Enumerable.Empty<GameSession>();
                foreach (var viewer in processed.SelectMany(t => t.Viewers).Concat(sourceViewers).Distinct()) await viewer.SendGameAsync(SkillHit, hit);
                if (doubles.Count > 0)
                {
                    // The Double Attack hit: the same swing's message again, its own number in the basic attack's font.
                    var twice = hit.Clone();
                    twice.Dsts.Clear();
                    twice.Split = 1;
                    foreach (var (target, extraAmount, extraResult) in doubles)
                        twice.Dsts.Add(new DamageInfo { DstID = target.Uid, Damage = extraAmount, HitResult = extraResult, DstElement = target.Template.Element, IsDead = target.Hp == 0 });
                    foreach (var viewer in processed.SelectMany(t => t.Viewers).Concat(sourceViewers).Distinct()) await viewer.SendGameAsync(SkillHit, twice);
                }
                if (CurrentCaster() && field?.Trap == true)
                    await ToPlayerAndViewersAsync(s, TrapDamage, new TrapDamageNtf { SkillConfigId = action.Request.SkillId, DamageValue = hit.Dsts.Sum(d => d.Damage), Pos = center.Clone() });
            }
            foreach (var target in processed)
            {
                if (!damage) continue;
                if (target.Hp == 0 && !target.DeathDurable) continue; // another cast is still committing that death
                await world.ToUnitViewersAsync(target, AoiAttrChange, MonsterService.Hp(target));
                await FightingAsync(target);
                if (target.Hp > 0 && CurrentCaster()) monsters.Engage(target, s);
            }
            if (processed.Count > 0 && CurrentCaster()) await FightingAsync(s);
            if (casterCondition && mechanic?.BranchIf is { Consume: true, CasterBuffGroup: int spent } && processed.Count > 0 && CurrentCaster())
                foreach (var buff in s.Buffs.Where(b => data.Row("BuffConfig", b.ConfigId) is { } br && GameData.Int(br, "_iBuffID") == spent).ToArray())
                    await buffs.RemoveAsync(s, buff);
            if (field != null) field.LastHits = damage ? processed.Count : 0;
            // "You then lose N% of your Max HP. This HP loss cannot be fatal." (Energy Release), once per cast.
            if (mechanic is { SelfHpLossPercent: > 0 } && impact is not { First: false } && CurrentCaster())
            {
                var self = character.Stats(s);
                long loss = self[101] * mechanic.SelfHpLossPercent / 100;
                await character.SetHpSpAsync(s, Math.Max(1, self[2000] - loss), null);
            }
            foreach (var target in killed.Where(target => target.Hp == 0 && target.DeathDurable))
            {
                log.LogInformation("{Name} killed {Monster} ({Npc})", castingRole.Name, target.Uid, target.Template.NpcId);
                await monsters.KillAsync(target);
                foreach (var other in sessions.All().Where(o => o.Attack?.Target == target))
                {
                    other.Attack = null;
                    await other.SendGameAsync(SkillEnd, new SkillEndNotify
                    {
                        SrcID = other.Role!.Id, SessionId = action.Id, Type = SkillEndNotify.Types.SkillEndType.Interrupted,
                        InterruptedInfo = new SkillInterruptedInfo { Type = SkillInterruptedInfo.Types.SkillInterruptType.LockerInvalid },
                    });
                }
                await PublishDurableKillAsync(s, target, CurrentKillRecipient);
                await FinalizeLethalPublicationAsync(target);
            }
        }
        finally
        {
            foreach (var target in killed.Where(target => target.Hp == 0 && target.DeathDurable))
            {
                if (!target.Dead)
                    try { await monsters.KillAsync(target); }
                    catch (Exception ex) { log.LogError(ex, "Could not finalize reserved monster death for {Uid}", target.Uid); }
                // A later target/effect can fail after this source committed. Its room observers must still run.
                await PublishDurableKillAsync(s, target, CurrentKillRecipient);
                await FinalizeLethalPublicationAsync(target);
            }
        }
    }

    private bool RollBuff(JsonElement tuple, out int id, out int duration)
    {
        id = duration = 0;
        if (tuple.GetArrayLength() < 3) return false;
        id = (int)(GameData.Number(tuple[0]) ?? 0);
        duration = (int)(GameData.Number(tuple[2]) ?? 0);
        return id > 0 && Rolls.Proc(10000) < (GameData.Number(tuple[1]) ?? 0);
    }

    internal enum DisplaceKind { CasterForward, CasterBack, TargetPush, TargetPull }

    /// <summary>
    /// Skills whose show has a displacement EffectData (m_kPoint.m_bMovement) and whose description names the
    /// distance: _kDescData index, or -1 for a pull "in front of you". Who moves and which way comes from the
    /// description and the effect's curve name (击退 knockback, 拉人 pull); the server choice of skills is (inferred).
    /// ShieldsProtection's 1161341 knockback belongs to its periodic aura, which is not simulated.
    /// </summary>
    internal static readonly IReadOnlyDictionary<string, (DisplaceKind Kind, int DescIndex)> Displacements =
        new Dictionary<string, (DisplaceKind, int)>
        {
            ["BackSlide"] = (DisplaceKind.CasterBack, 0),
            ["RetreatingShadows"] = (DisplaceKind.CasterBack, 0),
            ["RetreatingShadowsS1"] = (DisplaceKind.CasterBack, 0),
            ["WolvesDash"] = (DisplaceKind.CasterForward, 0),
            ["HighspeedSlide"] = (DisplaceKind.CasterForward, 0),
            ["HighspeedSlideMecha"] = (DisplaceKind.CasterForward, 0),
            ["PinpointAttack"] = (DisplaceKind.CasterForward, 3),
            ["PinpointAttack2"] = (DisplaceKind.CasterForward, 3), // Fearless Charge "Move «12» meters in that direction"
            ["ArrowRepel"] = (DisplaceKind.TargetPush, 3),
            ["PileBunker"] = (DisplaceKind.TargetPush, 5),
            ["PhantomThrust"] = (DisplaceKind.TargetPull, -1),
        };

    private readonly System.Collections.Concurrent.ConcurrentDictionary<long, (int Id, int Ms)> _movementEffects = new();

    /// <summary>
    /// The show's displacement EffectData: the lowest id in [scriptShow*100, scriptShow*100+99] with m_kPoint.m_bMovement
    /// (BackSlide 15104 -> 1510403, PileBunker 16314 -> 1631405 knockback). The client's SkillStartMove ignores a 31112
    /// without it (LC_PerformEffect_Agent.lua SkillStartMove returns on effectId nil/0).
    /// </summary>
    internal (int Id, int Ms) MovementEffect(long scriptShow) => _movementEffects.GetOrAdd(scriptShow, show =>
    {
        var (id, ms, _) = FindMovementEffect(data, show);
        return (id, ms);
    });

    /// <summary>The show's displacement EffectData (see <see cref="MovementEffect"/>), with its move curve's name.</summary>
    internal static (int Id, int Ms, string Curve) FindMovementEffect(GameData data, long show)
    {
        var effects = data.SkillTemplateTable("Lua_Config_EffectData");
        for (long id = show * 100; id < show * 100 + 100; id++)
            if (effects.TryGetValue(id.ToString(), out var e) && e.TryGetProperty("m_kPoint", out var point)
                && point.TryGetProperty("m_bMovement", out var moves) && moves.ValueKind == JsonValueKind.True)
                return ((int)id, point.TryGetProperty("m_iMovementTime", out var ms) && ms.TryGetInt32(out int t) ? t : 0,
                    point.TryGetProperty("m_kMoveCurveName", out var curve) && curve.ValueKind == JsonValueKind.String ? curve.GetString() ?? "" : "");
        return (0, 0, "");
    }

    private (DisplaceKind Kind, float Distance, int EffectId, int Ms)? Displacement(JsonElement row)
    {
        if (!Displacements.TryGetValue(Script(row), out var rule)) return null;
        var (effect, ms) = MovementEffect(GameData.Int(row, "_iScriptShow"));
        if (effect == 0) return null;
        float distance = rule.DescIndex < 0 ? 1 // (inferred) "pulling them in front of you": 1 m ahead of the caster
            : (float)Numbers(row, "_kDescData").ElementAtOrDefault(rule.DescIndex);
        return distance > 0 ? (rule.Kind, distance, effect, ms) : null;
    }

    private async Task MoveCasterAsync(GameSession s, ActiveSkillCast action)
    {
        if (Displacement(action.Row) is not { Kind: DisplaceKind.CasterForward or DisplaceKind.CasterBack } move) return;
        float face = action.Request.Face + (move.Kind == DisplaceKind.CasterBack ? 180 : 0);
        double angle = face * Math.PI / 180;
        var end = WalkableEnd(s.SceneEntry, s.Position, (float)Math.Sin(angle) * move.Distance, (float)Math.Cos(angle) * move.Distance);
        var start = s.Position.Clone();
        await ToPlayerAndViewersAsync(s, SkillMove, new SkillStartMoveNotify
        {
            EntityID = (ulong)s.Role!.Id, StartPos = start, EndPos = end, SkillConfigId = action.Request.SkillId,
            EffectId = move.EffectId, StartTimestampMs = Now, MoveType = 1, Face = action.Request.Face,
            MoveTimeMs = move.Ms > 0 ? move.Ms : action.ActionMs,
        });
        // MoveAsync updates persistent position and AOI membership from the same authoritative destination.
        s.PreviousPosition = start;
        s.Position = end;
        await world.MoveAsync(s, new WalkReq { Pos = end, Dir = s.Face, Face = s.Face, BStop = true }, forceTeleport: true);
        ConfirmSkillMoveEnd(s, end, move.Ms > 0 ? move.Ms : action.ActionMs);
    }

    /// <summary>
    /// A SkillStartMove only animates the caster's model; the local player's movement controller kept the cast spot, so
    /// its next walk started from there and the character snapped back. When the move ends the server pins the
    /// controlled unit to the destination with MOVEMENT_CHANGE transType 1 ResetPos (LC_Unit_Player.ResetPos: StopMove +
    /// m_kMovement:SetPos, applied to the controlled unit in every scene; SceneService.TeleportInMapAsync). Skipped when
    /// the player has already moved on (inferred timing: right after the authored move time).
    /// </summary>
    private void ConfirmSkillMoveEnd(GameSession s, Vec3 end, int moveMs)
    {
        var role = s.Role;
        long scene = s.WorldScene, generation = s.SceneGeneration;
        var at = end.Clone();
        ScheduleRuntime(scene, TimeSpan.FromMilliseconds(Math.Max(50, moveMs) + 30), async () =>
        {
            if (role == null || !ReferenceEquals(s.Role, role) || s.WorldScene != scene || s.SceneGeneration != generation || s.SceneLoading || s.Dead) return;
            if (s.Walking) return; // already walking on from wherever it is
            if (Distance(s.Position, at) > 0.05f)
            {
                // A stale position sync from the cast spot pulled the server back too: the dash end stands.
                s.PreviousPosition = s.Position.Clone();
                s.Position = at.Clone();
                await world.MoveAsync(s, new WalkReq { Pos = at.Clone(), Dir = s.Face, Face = s.Face, BStop = true }, forceTeleport: true);
            }
            await s.SendGameAsync(GameAoiSink.MovementChange, new MovementChangeNtf
            {
                Uid = role.Id, TransType = MovementTransitionType.MoveTransResetPos,
                MovementInfo = new MovementInfo { Pos = at.Clone(), Face = s.Face, ServerTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() },
            });
        });
    }

    /// <summary>
    /// SkillMechanics.dash: the caster rushes to the locked target (stopping <c>stop</c> m short) or to the chosen point
    /// before the hit (Heavy Axe Smash "Move rapidly in front of the target", Charge Attack, Rift Impact's dive).
    /// </summary>
    private async Task DashAsync(GameSession s, ActiveSkillCast action)
    {
        if (action.Operator != null || SkillMechanics.For(action.Row) is not { DashStop: float stop } mechanic) return;
        var target = mechanic.DashToPoint ? null : FindMonster(s.WorldScene, action.Request.TargetId);
        var destination = target?.Pos ?? action.Request.TargetPos;
        if (destination == null) return;
        float dx = destination.X - s.Position.X, dz = destination.Z - s.Position.Z, length = MathF.Sqrt(dx * dx + dz * dz);
        if (length < mechanic.DashMinDistance) return; // "If you are more than «5» meters from the target, you also Charge"
        float travel = length - (target != null ? stop : 0);
        if (travel <= 0.2f) return;
        var start = s.Position.Clone();
        var end = WalkableEnd(s.SceneEntry, start, dx / length * travel, dz / length * travel);
        var (effect, ms) = MovementEffect(GameData.Int(action.Row, "_iScriptShow"));
        await ToPlayerAndViewersAsync(s, SkillMove, new SkillStartMoveNotify
        {
            EntityID = (ulong)s.Role!.Id, StartPos = start, EndPos = end, SkillConfigId = action.Request.SkillId,
            EffectId = effect, StartTimestampMs = Now, MoveType = 1, Face = action.Request.Face, MoveTimeMs = ms > 0 ? ms : 200,
        });
        s.PreviousPosition = start;
        s.Position = end;
        await world.MoveAsync(s, new WalkReq { Pos = end, Dir = s.Face, Face = s.Face, BStop = true }, forceTeleport: true);
        ConfirmSkillMoveEnd(s, end, ms > 0 ? ms : 200);
    }

    private async Task DisplaceTargetAsync(GameSession s, ActiveSkillCast action, MonsterUnit target, float knockback = 0, float pull = 0, Vec3? toward = null, Vec3? pushFrom = null)
    {
        var rule = Displacement(action.Row);
        if (rule is null && pull > 0 && toward != null)
        {
            // SkillMechanics.pull: drawn toward the area's centre (Gravitational Impact, Tetra Vortex), stopping 1 m short.
            float px = toward.X - target.X, pz = toward.Z - target.Z, gap = MathF.Sqrt(px * px + pz * pz);
            float step = Math.Min(pull, gap - 1);
            if (step <= 0.05f) return;
            var (pullEffect, pullMs) = MovementEffect(GameData.Int(action.Row, "_iScriptShow"));
            var pullStart = target.Pos;
            var to = WalkableEnd(s.SceneEntry, pullStart, px / gap * step, pz / gap * step);
            target.Moving = false;
            target.X = to.X; target.Y = to.Y; target.Z = to.Z;
            await world.ToUnitViewersAsync(target, SkillMove, new SkillStartMoveNotify
            {
                EntityID = (ulong)target.Uid, StartPos = pullStart, EndPos = to, SkillConfigId = action.Request.SkillId,
                EffectId = pullEffect, MoveType = 2, StartTimestampMs = Now, Face = target.Face, MoveTimeMs = pullMs > 0 ? pullMs : 300,
            });
            await world.MoveUnitAsync(target, new MovementChangeNtf { Uid = target.Uid, MovementInfo = target.CurrentMovement(Now) });
            return;
        }
        if (rule is null && knockback > 0)
        {
            // A knockback a description names without a scripted displacement rule (SkillMechanics.knockback).
            var (effect, ms) = MovementEffect(GameData.Int(action.Row, "_iScriptShow"));
            rule = (DisplaceKind.TargetPush, knockback, effect, ms);
        }
        if (rule is not { Kind: DisplaceKind.TargetPush or DisplaceKind.TargetPull } move) return;
        var from = pushFrom ?? s.Position; // a wall pushes away from itself, a skill away from its caster
        float dx = target.X - from.X, dz = target.Z - from.Z;
        float length = MathF.Sqrt(dx * dx + dz * dz);
        if (length < 0.001f) return;
        // A push goes away from the caster; a pull stops the given distance in front of it.
        float distance = move.Kind == DisplaceKind.TargetPush ? move.Distance : -(length - move.Distance);
        if (move.Kind == DisplaceKind.TargetPull && length <= move.Distance) return;
        var start = target.Pos;
        var end = WalkableEnd(s.SceneEntry, start, dx / length * distance, dz / length * distance);
        target.Moving = false;
        target.X = end.X; target.Y = end.Y; target.Z = end.Z;
        await world.ToUnitViewersAsync(target, SkillMove, new SkillStartMoveNotify
        {
            EntityID = (ulong)target.Uid, StartPos = start, EndPos = end, SkillConfigId = action.Request.SkillId,
            EffectId = move.EffectId, MoveType = 2, StartTimestampMs = Now, Face = target.Face,
            MoveTimeMs = move.Ms > 0 ? move.Ms : 300,
        });
        await world.MoveUnitAsync(target, new MovementChangeNtf { Uid = target.Uid, MovementInfo = target.CurrentMovement(Now) });
    }

    private Vec3 WalkableEnd(long sceneId, Vec3 start, float dx, float dz)
    {
        var nav = worldData.Map(sceneId)?.Nav;
        var end = start.Clone();
        int steps = Math.Max(1, (int)Math.Ceiling(Math.Sqrt(dx * dx + dz * dz) / 0.25));
        for (int i = 1; i <= steps; i++)
        {
            float x = start.X + dx * i / steps, z = start.Z + dz * i / steps;
            if (nav != null && !nav.CanWalk(end.X, end.Y, end.Z, x, z)) break;
            end = new Vec3 { X = x, Y = nav?.WalkHeight(x, z, end.Y) ?? end.Y, Z = z };
        }
        return end;
    }

    /// <summary>
    /// Owned ground triggers use NPCConfig identities, not WidgetConfig. Fire_Wall/Safety_Wall/Pneuma tuples explicitly
    /// reference NPCs; Hunter trap NPCs match their localized skill names. Timing/limits below are inferred from the
    /// corresponding descriptions and _kLogicData, documented per family in docs/systems/skills-rest.md.
    /// </summary>
    private async Task<bool> CreateSkillObjectAsync(GameSession s, ActiveSkillCast action)
    {
        if (!CurrentCastMap(s, action)) return true;
        var row = action.Row;
        string script = Script(row);
        var logic = Numbers(row, "_kLogicData");
        int npc = 0, duration = 0, interval = 1000, protection = 0, charges = 0;
        bool trap = false;
        float radius = (float)(Numbers(row, "_kRange").FirstOrDefault(150) / 100);
        switch (script)
        {
            case "Fire_Wall" or "Quagmire" or "Fire_Pillar" or "Meteor_Storm" or "Lord_of_Vermilion" or "Storm_Gust" when logic.Length >= 3:
                npc = (int)logic[0]; interval = (int)logic[1]; duration = (int)logic[2]; break;
            case "Safety_Wall" or "Pneuma" when logic.Length >= 8:
                npc = (int)logic[5]; duration = (int)logic[2]; radius = (float)(logic[7] / 100);
                // Safety Wall's hit count is _kLogicData[3] (1..5 by level, the description's ${5}); [1] is always 1.
                protection = script == "Safety_Wall" ? 1 : 2; charges = script == "Safety_Wall" ? (int)logic[3] : int.MaxValue; break;
            case "LandMine": npc = 22000016; trap = true; break;
            case "ClaymoreTrap": npc = 22000017; trap = true; break;
            case "FreezingTrap": npc = 22000018; trap = true; break;
            case "BlastMine": npc = 22000019; trap = true; break;
            default:
                if (script.Contains("Summon", StringComparison.OrdinalIgnoreCase))
                {
                    npc = logic.Select(v => (int)v).FirstOrDefault(id => data.Row("NPCConfig", id) != null);
                    duration = 30000; // (inferred) bounded lifecycle for a named summon without an explicit lifetime tuple.
                }
                break;
        }
        if (npc == 0 || data.Row("NPCConfig", npc) is not { } npcRow) return false;
        if (trap)
        {
            var desc = row.TryGetProperty("_kDescData", out var d) && d.ValueKind == JsonValueKind.Array ? d.EnumerateArray().ToArray() : [];
            duration = desc.Length > 0 && double.TryParse(desc[^1].ToString(), System.Globalization.CultureInfo.InvariantCulture, out double seconds) ? (int)(seconds * 1000) : 20000;
            radius = 3; interval = 100;
        }
        var described = Numbers(row, "_kDescData");
        // Described limits (inferred from each family's text): Fire Wall vanishes after ${7} touches and knocks back
        // ${5} m; Meteor Storm drops _kLogicData[5] meteors; at most ${8}/${7}/${6} walls/pillars at once.
        int? touches = script == "Fire_Wall" && described.Length > 6 ? (int)described[6] : null;
        float knockback = script == "Fire_Wall" && described.Length > 4 ? (float)described[4] : 0;
        int? ticks = script == "Meteor_Storm" && logic.Length > 5 ? (int)logic[5] : null;
        int limit = script switch
        {
            "Fire_Wall" when described.Length > 7 => (int)described[7],
            "Fire_Pillar" when described.Length > 6 => (int)described[6],
            "Safety_Wall" when described.Length > 5 => (int)described[5],
            _ => 0,
        };
        if (limit > 0)
            foreach (var old in s.SkillObjects.Where(o => !o.Removing && Script(o.Cast.Row) == script).OrderBy(o => o.Uid).SkipLast(limit - 1).ToArray())
                await RemoveSkillObjectAsync(old);
        var point = action.Request.TargetPos ?? action.Origin;
        var unit = new SkillObject
        {
            Uid = ++_skillObjectIds, SceneEntry = s.SceneEntry, InstanceId = s.InstanceId, RuntimeMap = s.RuntimeMap,
            OwnerSession = s, OwnerId = s.Role!.Id,
            NpcId = npc, EntityType = (int)GameData.Int(npcRow, "_iNPCType"), Cast = action,
            X = point.X, Y = point.Y, Z = point.Z, Face = action.Request.Face, ExpiresMs = Now + Math.Max(1, duration),
            IntervalMs = Math.Max(50, interval), NextTickMs = Now, Radius = radius, Trap = trap,
            ProtectionDistanceType = protection, Charges = charges, TouchesLeft = touches, TicksLeft = ticks, Knockback = knockback,
            MaxTargets = trap ? 5 : null, // (verified) the four traps say "up to 5 enemies"; their rows have _iTargetMax 1
        };
        foreach (int skill in Numbers(npcRow, "_kSkills").Select(v => (int)v)) unit.Skills.Add(skill);
        s.SkillObjects.Add(unit);
        await world.AddUnitAsync(unit);
        if (!CurrentCastMap(s, action)) { await RemoveSkillObjectAsync(unit); return true; }
        foreach (var tuple in Tuples(row, "_kBuff1"))
            if (RollBuff(tuple, out int buffId, out int buffDuration)) await buffs.AddAsync(s, buffId, buffDuration, s.Role.Id);
        return true;
    }

    private async Task TickSkillObjectAsync(SkillObject unit)
    {
        if (unit.Busy || unit.Removing) return;
        unit.Busy = true;
        try
        {
            var owner = unit.OwnerSession;
            if (Now >= unit.ExpiresMs || owner.Role?.Id != unit.OwnerId || owner.SceneLoading || owner.Dead
                || owner.WorldScene != unit.WorldScene || owner.SceneGeneration != unit.Cast.SceneGeneration
                || !ReferenceEquals(owner.Attachment, unit.Cast.Attachment) || unit.Cast.Attachment?.Cancelled == true)
            {
                await RemoveSkillObjectAsync(unit);
                return;
            }
            if (Now < unit.NextTickMs) return;
            unit.NextTickMs = Now + unit.IntervalMs;
            if (unit.ProtectionDistanceType != 0) return;
            if (unit.Trap && !world.Map(unit.WorldScene).Near(unit.X, unit.Z, 1.5f).OfType<MonsterUnit>().Any(m => !m.Dead && Distance(m.Pos, unit.Pos) <= 1.5f)) return;
            await ApplyEffectsAsync(owner, unit.Cast, unit.Pos, unit);
            if (unit.Trap) await RemoveSkillObjectAsync(unit);
            else if (unit.LastHits > 0 && unit.TouchesLeft is int left && (unit.TouchesLeft = left - unit.LastHits) <= 0) await RemoveSkillObjectAsync(unit);
            else if (unit.LastHits > 0 && unit.TicksLeft is int remaining && (unit.TicksLeft = remaining - 1) <= 0) await RemoveSkillObjectAsync(unit);
        }
        finally { unit.Busy = false; }
    }

    private async Task RemoveSkillObjectAsync(SkillObject unit)
    {
        if (unit.Removing) return;
        unit.Removing = true;
        unit.OwnerSession.SkillObjects.Remove(unit);
        await world.RemoveUnitAsync(unit);
    }

    /// <summary>Scene/role/disconnect teardown invalidates callbacks before despawning owned skill entities.</summary>
    public async Task ClearSkillsAsync(GameSession s)
    {
        eventAttacks.Remove(s);
        s.WorldState.ResurrectionCastId = 0;
        s.Attack = null;
        if (s.SkillCast is { } cast) cast.Cancelled = true;
        s.SkillCast = null;
        foreach (var unit in s.SkillObjects.ToArray()) await RemoveSkillObjectAsync(unit);
    }

    private async Task<bool> GroundProtectsAsync(GameSession target, JsonElement? skill)
    {
        int distanceType = skill is { } row ? (int)GameData.Int(row, "_iDistanceType") : 1;
        var field = world.Map(target.WorldScene).Units.OfType<SkillObject>().FirstOrDefault(u => !u.Removing && u.ExpiresMs > Now
            && u.ProtectionDistanceType == distanceType && u.Charges > 0 && Distance(u.Pos, target.Position) <= u.Radius);
        if (field == null) return false;
        if (--field.Charges == 0) await RemoveSkillObjectAsync(field);
        return true;
    }
}
