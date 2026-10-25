using System.Globalization;
using Romsg;
using Ro3Emu.Server.Data.Config;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

/// <summary>
/// What a scripted step does on its own clock and how its fight comes and goes (ReconstructedDungeons.Cue, Rider and
/// Spawn). The original's scene scripts are not in the client; what is, is the material they used: the ChatConfig lines
/// nothing in the client sends, the NPCConfig units nothing in the client creates (an empty model carrying a marker
/// buff, an explosion's NPC whose _kRespawnSkill plays the blast, barrels, an fx-only unit), the scene editor's patrol
/// paths (kPatrolList) and the Lua_Config_EffectData effects of the scene's skill templates. Their order and timing are
/// (inferred), each in the scene's json. A cue unit is a scene unit like the step's actors: shown through the world AOI,
/// removed after its life, at its path's end or with the room. A fight's monster with a patrol path flies it, driven by
/// the scene (never engaged), carrying its riders; a monster with a _kRespawnSkill plays it as it appears and holds
/// still meanwhile; a monster's end effects play where it fell, and its riders leave with it.
/// </summary>
public sealed partial class DungeonService
{
    /// <summary>Session ids of the skill timelines a scene's units play (31100 needs one): far from the casts'.</summary>
    private long _cueSession = 1L << 50;

    /// <summary>The step's cues, each on the step's clock (StepLater: paced like its other timers, dropped with it).</summary>
    private void StartCues(Attempt a, ReconstructedDungeons.ScriptedStep step)
    {
        int generation = a.StepGeneration;
        foreach (var cue in step.Cues ?? [])
            StepLater(a, generation, cue.HpBelow > 0 ? 0 : cue.AtMs, () => CueAsync(a, generation, cue), whileFighting: true);
    }

    /// <summary>How often a cue waiting for a share of a monster's HP looks at it, in ms of the step's clock.</summary>
    private const int HpCuePollMs = 250;

    private async Task CueAsync(Attempt a, int generation, ReconstructedDungeons.Cue cue)
    {
        if (cue.HpBelow > 0)
        {
            // Once, when the step's fight monster is down to its share of HP; it waits while that one has not come yet.
            var boss = a.Room.Monsters.FirstOrDefault(m => m.Template.NpcId == cue.Of && !m.Dead && m.Hp > 0);
            if (boss != null && (double)boss.Hp * 100 <= (double)boss.Template.MaxHp * cue.HpBelow)
                StepLater(a, generation, cue.AtMs, () => PlayCueAsync(a, cue), whileFighting: true);
            else if (boss != null || !a.Room.Monsters.Any(m => m.Template.NpcId == cue.Of))
                StepLater(a, generation, HpCuePollMs, () => CueAsync(a, generation, cue), whileFighting: true);
            return;
        }
        if (cue.EveryMs > 0) StepLater(a, generation, cue.EveryMs, () => CueAsync(a, generation, cue), whileFighting: true);
        await PlayCueAsync(a, cue);
    }

    private async Task PlayCueAsync(Attempt a, ReconstructedDungeons.Cue cue)
    {
        if (cue.Chat != 0)
            await BroadcastAsync(a, SystemBroadcast, new SystemBoardCastMsgNtf
            {
                Source = MsgSource.Npc, ChatId = cue.Chat,
                // A Bubbling row (type 2) shows over its unit: LC_SystemMessageDecodeFunc.PushBubbling takes playerId.
                PlayerId = cue.Speaker == 0 ? 0 : a.SceneActors.FirstOrDefault(u => u.NpcId == cue.Speaker)?.Uid ?? 0,
            });
        else if (cue.Bubble != 0) await CueBubbleAsync(a, cue);
        else if (cue.Dismiss != 0) await DismissAsync(a, cue.Dismiss);
        else if (cue.Walk != 0) await CueWalkAsync(a, cue);
        else if (cue.Cast != 0) await CueCastAsync(a, cue);
        else if (cue.Effect != 0) await CueEffectAsync(a, cue);
        else await CueUnitAsync(a, cue);
    }

    /// <summary>
    /// An actor's head bubble on the scene's clock: the 32700 reply of its bubble interaction, as if the player had walked
    /// into its range. LC_InteractManager._onInteractResponse looks the interaction and the unit up and runs the
    /// interaction's client-hosted actions (its 4001 bubble); nothing there asks whether a request was pending.
    /// </summary>
    private async Task CueBubbleAsync(Attempt a, ReconstructedDungeons.Cue cue)
    {
        if (a.SceneActors.FirstOrDefault(u => u.NpcId == cue.Speaker) is not { } actor) return;
        await BroadcastAsync(a, SceneService.NpcInteraction, new NpcInteractionRsp
        {
            Result = 0,
            Req = new NpcInteractionReq { NpcUid = actor.Uid, InteractionId = cue.Bubble, SceneConfigId = a.Plan.SceneEntry },
        });
    }

    /// <summary>A shown actor or ally walks the cue's route from where it stands, at its NPC speed.</summary>
    private async Task CueWalkAsync(Attempt a, ReconstructedDungeons.Cue cue)
    {
        if (a.SceneActors.Concat(a.Allies).FirstOrDefault(u => u.NpcId == cue.Walk && !u.Removed) is not { } unit
            || _catalog.SceneRoute(a.Plan.SceneEntry, cue.Patrol, cue.Path) is not { Count: > 0 } route) return;
        int ms = await WalkPathAsync(a, unit, [new MapPoint(unit.X, unit.Y, unit.Z, unit.Face), .. route]);
        a.AllyBusyUntil[unit.Uid] = MonsterService.NowMs + ms; // an ally does not wander off meanwhile
    }

    /// <summary>A shown unit plays a skill's show (31100 from it): an actor, an ally, a cue unit or a rider.</summary>
    private async Task CueCastAsync(Attempt a, ReconstructedDungeons.Cue cue)
    {
        foreach (var unit in a.SceneActors.Concat(a.Allies).Concat(a.CueUnits).Concat(a.Riders.Values.SelectMany(c => c))
                .Where(u => u.NpcId == cue.Speaker && !u.Removed).ToArray())
            await SkillShowAsync(unit, cue.Cast);
    }

    /// <summary>How often a burning or fused cue unit looks around, in ms.</summary>
    private const int BurnTickMs = 1000, FuseTickMs = 250;

    /// <summary>
    /// A burning cue unit (the deck fire 5300057, whose NPCConfig _kSkills is 5365201, _iActionTime and _iCD 1000): every
    /// second, whoever stands within its radius takes that skill, cast as the fight's monster (the Evil Boss) while it
    /// lives (inferred: the fire is the Boss's doing, and the official video shows ticks on the player standing in it).
    /// </summary>
    private async Task BurnAsync(Attempt a, SceneActorUnit fire, ReconstructedDungeons.Cue cue)
    {
        if (fire.Removed || a.Settled || a.Room.Closed) return;
        if (a.Room.Monsters.FirstOrDefault(m => m.Template.NpcId == cue.Of && !m.Dead && m.Hp > 0) is not { } caster) return;
        _loop.ScheduleLegacy(TimeSpan.FromMilliseconds(BurnTickMs), () => BurnAsync(a, fire, cue));
        if (NPCConfig.Get(_data, cue.Npc) is not { Skills: [> 0 and var skill, ..] } || _data.Row("SkillConfig", skill) is not { } row) return;
        var burnt = Present(a).Where(m => a.Loaded.Contains(m) && !m.SceneLoading && !m.Dead
            && MonsterService.Distance(m.Position.X, m.Position.Z, fire.X, fire.Z) <= cue.Burn).ToArray();
        if (burnt.Length > 0) await _combat.MonsterSkillHitAsync(caster, row, burnt, fire.Uid);
    }

    /// <summary>
    /// A fused cue unit (a barrel): when a fighting monster or a participant comes within its radius, it blows up where it
    /// stands (its blast effect, as 31119 from it) and leaves (inferred: in the official video the barrels burst where the
    /// fight reaches them).
    /// </summary>
    private async Task FuseAsync(Attempt a, SceneActorUnit barrel, ReconstructedDungeons.Cue cue)
    {
        if (barrel.Removed || a.Settled || a.Room.Closed) return;
        bool near = a.Room.Monsters.Any(m => !m.Dead && m.Hp > 0 && !m.ScriptDriven
                && MonsterService.Distance(m.X, m.Z, barrel.X, barrel.Z) <= cue.Fuse)
            || Present(a).Any(m => a.Loaded.Contains(m) && !m.Dead
                && MonsterService.Distance(m.Position.X, m.Position.Z, barrel.X, barrel.Z) <= cue.Fuse);
        if (!near)
        {
            _loop.ScheduleLegacy(TimeSpan.FromMilliseconds(FuseTickMs), () => FuseAsync(a, barrel, cue));
            return;
        }
        foreach (int kind in DisplayKinds(cue.Blast, 0))
            await BroadcastAsync(a, CombatService.SkillEffect, new SkillEffectDisplayNotify
            {
                SourceId = barrel.Uid, TargetId = barrel.Uid, TargetPos = barrel.Pos, Is3DPosition = true,
                EffectId = cue.Blast, EffectType = kind, SkillId = OwnerSkill(cue.Blast),
            });
        await RemoveCueUnitAsync(a, barrel);
    }

    /// <summary>The crews of the cue units of an NPC leave them (the pirates jump over to board).</summary>
    private async Task DismissAsync(Attempt a, int npcId)
    {
        foreach (var unit in a.CueUnits.Where(u => u.NpcId == npcId).ToArray())
            if (a.Riders.Remove(unit.Uid, out var crew))
                foreach (var rider in crew) await _instances.RemovePropAsync(a.Room, rider);
    }

    /// <summary>A cue unit leaves, with its riders.</summary>
    private async Task RemoveCueUnitAsync(Attempt a, SceneActorUnit unit)
    {
        a.CueUnits.Remove(unit);
        if (a.Riders.Remove(unit.Uid, out var crew))
            foreach (var rider in crew) await _instances.RemovePropAsync(a.Room, rider);
        await _instances.RemovePropAsync(a.Room, unit);
    }

    /// <summary>
    /// A cue's effect, to every participant from their own unit: a camera shake moves only the camera of the unit it plays
    /// on, unless the effect syncs it to others (LC_PerformEffect_Mgr.ShakeCamera: IsControlledUnit or m_isSyncThird).
    /// A positional one plays at the cue's point.
    /// </summary>
    private async Task CueEffectAsync(Attempt a, ReconstructedDungeons.Cue cue)
    {
        var at = cue.Point != 0 ? _catalog.ScenePoint(a.Plan.SceneEntry, cue.Point) : null;
        var kinds = DisplayKinds(cue.Effect, cue.EffectType);
        foreach (var member in Present(a).Where(m => a.Loaded.Contains(m) && !m.SceneLoading && m.Role != null))
        {
            long me = member.Role!.Id;
            foreach (int kind in kinds)
                await SendQuietAsync(member, CombatService.SkillEffect, new SkillEffectDisplayNotify
                {
                    SourceId = me, MasterId = me, TargetId = me,
                    TargetPos = at is { } p ? new Vec3 { X = p.X, Y = p.Y, Z = p.Z } : member.Position.Clone(),
                    EffectId = cue.Effect, EffectType = kind, SkillId = OwnerSkill(cue.Effect),
                });
        }
    }

    /// <summary>
    /// A cue's unit: shown at its point or at the start of its route (its patrol path or its own) with its NPCConfig born
    /// buffs (a marker's ring is its buff's keep effect), seen from the cue's sight, its riders aboard, playing its
    /// _kRespawnSkill (a blast's boom) and walking its route at its NPC speed. It leaves after its life, at its route's
    /// end when it vanishes there, or with the room.
    /// </summary>
    private async Task CueUnitAsync(Attempt a, ReconstructedDungeons.Cue cue)
    {
        if (a.Settled || a.Room.Closed || _catalog.ScenePlacement(a.Plan.SceneEntry, cue.Point, cue.Patrol, cue.Path) is not { } at
            || SceneUnit(a, cue.Npc, 0, at) is not { } unit) return;
        AddBornBuffs(unit);
        if (!await _instances.AddPropAsync(a.Room, unit)) return;
        a.CueUnits.Add(unit);
        if (cue.Sight > 0) await _world.WidenSightAsync(unit, cue.Sight);
        foreach (var rider in cue.Riders ?? []) await BoardAsync(a, unit, rider);
        await EntranceAsync(unit, cue.Npc);
        int travel = _catalog.SceneRoute(a.Plan.SceneEntry, cue.Patrol, cue.Path) is { } path ? await WalkPathAsync(a, unit, path) : 0;
        int life = cue.LifeMs > 0 ? cue.LifeMs : cue.Vanish ? travel : 0;
        if (life > 0) _loop.ScheduleLegacy(TimeSpan.FromMilliseconds(life), () => RemoveCueUnitAsync(a, unit));
        if (cue.Burn > 0) _loop.ScheduleLegacy(TimeSpan.FromMilliseconds(BurnTickMs), () => BurnAsync(a, unit, cue));
        if (cue.Fuse > 0) _loop.ScheduleLegacy(TimeSpan.FromMilliseconds(FuseTickMs), () => FuseAsync(a, unit, cue));
    }

    /// <summary>NPCConfig _kBornEventBuff [buff, ms, count, chance]: the buffs a cue unit or a rider is born with.</summary>
    private void AddBornBuffs(SceneActorUnit unit)
    {
        if (NPCConfig.Get(_data, unit.NpcId) is not { } npc) return;
        foreach (var tuple in npc.BornEventBuff)
            if (tuple.Length > 0 && BuffConfig.Get(_data, tuple[0]) is { BuffId: > 0 })
                unit.Buffs.Add(ActorBuff(unit, (int)tuple[0]));
    }

    /// <summary>A unit walks its route at its NPC speed, one straight segment after the other from the first point, its
    /// riders keeping their seats; the whole route's time in ms.</summary>
    private async Task<int> WalkPathAsync(Attempt a, SceneActorUnit unit, IReadOnlyList<MapPoint> path)
    {
        float speed = unit.SpeedCm / 100f;
        if (speed <= 0) return 0;
        int total = 0;
        for (int i = 1; i < path.Count; i++)
        {
            var to = path[i];
            int ms = (int)Math.Max(1, MonsterService.Distance(path[i - 1].X, path[i - 1].Z, to.X, to.Z) / speed * 1000);
            if (total == 0) await WalkSegmentAsync(a, unit, to, ms);
            else _loop.ScheduleLegacy(TimeSpan.FromMilliseconds(total), () => WalkSegmentAsync(a, unit, to, ms));
            total += ms;
        }
        return total;
    }

    private async Task WalkSegmentAsync(Attempt a, SceneActorUnit unit, MapPoint to, int ms)
    {
        if (unit.Removed) return;
        long now = MonsterService.NowMs;
        await _world.MoveUnitAsync(unit, unit.Walk(to.X, to.Y, to.Z, now, now + ms));
        foreach (var rider in a.Riders.GetValueOrDefault(unit.Uid) ?? [])
        {
            if (rider.Seat is not { } seat || rider.Removed) continue;
            var (x, z) = Seat(to.X, to.Z, unit.Face, seat);
            await _world.MoveUnitAsync(rider, rider.Walk(x, to.Y + seat.Dy, z, now, now + ms));
        }
    }

    /// <summary>
    /// A scripted fight's monster has appeared (DungeonCatalog.Spawn.Script): its riders board it and its end effects are
    /// kept. One with a patrol path flies it, driven by the scene: false, it is not engaged. Any other plays its
    /// NPCConfig _kRespawnSkill and holds still while it lasts (the boarders' FallDown, the Evil Boss's entrance).
    /// </summary>
    private async Task<bool> FightArrivedAsync(Attempt a, MonsterUnit monster, DungeonCatalog.Spawn spawn)
    {
        if (spawn.Script is not { } script) return true;
        if (script.Sink is { Count: > 0 } sink) a.Sinks[monster.Uid] = sink;
        foreach (var rider in script.Riders ?? []) await BoardAsync(a, monster, rider);
        if (_catalog.SceneRoute(a.Plan.SceneEntry, script.Patrol, script.Path) is { Count: > 1 } path)
        {
            monster.ScriptDriven = true;
            await FlyAsync(a, monster, path, 1, script.Face);
            return false;
        }
        int hold = await EntranceAsync(monster, monster.Template.NpcId);
        long readyAt = MonsterService.NowMs + Math.Max(0, hold);
        if (hold > 0) monster.BusyUntilMs = readyAt;
        _monsters.ArmScriptedSkills(monster, readyAt);
        return true;
    }

    /// <summary>A rider takes its seat on the monster or cue unit, seen as far as it is.</summary>
    private async Task BoardAsync(Attempt a, WorldUnit ship, ReconstructedDungeons.Rider rider)
    {
        var (x, z) = Seat(ship.X, ship.Z, ship.Face, rider);
        if (SceneUnit(a, rider.NpcId, 0, new MapPoint(x, ship.Y + rider.Dy, z, ship.Face)) is not { } unit) return;
        unit.Seat = rider;
        AddBornBuffs(unit);
        if (!await _instances.AddPropAsync(a.Room, unit)) return;
        if (!a.Riders.TryGetValue(ship.Uid, out var crew)) a.Riders[ship.Uid] = crew = [];
        crew.Add(unit);
        if (ship.SightRadius > 0) await _world.WidenSightAsync(unit, ship.SightRadius);
    }

    /// <summary>A seat's place: its offset turned by the facing (degrees clockwise from +z), z ahead and x to the right.</summary>
    internal static (float X, float Z) Seat(float x, float z, float face, ReconstructedDungeons.Rider rider)
    {
        float turn = face * MathF.PI / 180f, sin = MathF.Sin(turn), cos = MathF.Cos(turn);
        return (x + rider.Dx * cos + rider.Dz * sin, z - rider.Dx * sin + rider.Dz * cos);
    }

    /// <summary>The monster flies its path from point <paramref name="next"/> on, one straight segment after the other,
    /// its riders keeping their seats over the same time; at its end it turns to <paramref name="face"/>, if given.</summary>
    private async Task FlyAsync(Attempt a, MonsterUnit ship, IReadOnlyList<MapPoint> path, int next, float? face)
    {
        if (a.Settled || a.Room.Closed || ship.Dead || next >= path.Count) return;
        var to = path[next];
        int ms = _monsters.Fly(ship, to.X, to.Y, to.Z);
        if (ms <= 0) return;
        foreach (var rider in a.Riders.GetValueOrDefault(ship.Uid) ?? [])
        {
            if (rider.Seat is not { } seat || rider.Removed) continue;
            var (x, z) = Seat(to.X, to.Z, ship.Face, seat);
            await _world.MoveUnitAsync(rider, rider.Walk(x, to.Y + seat.Dy, z, ship.StartMs, ship.EndMs));
        }
        if (next + 1 < path.Count) _loop.ScheduleLegacy(TimeSpan.FromMilliseconds(ms), () => FlyAsync(a, ship, path, next + 1, face));
        else if (face is { } turn) _loop.ScheduleLegacy(TimeSpan.FromMilliseconds(ms), () => TurnAsync(a, ship, turn));
    }

    /// <summary>Riders take their seats again around a ship that turned in place.</summary>
    private const int ReseatMs = 800;

    private async Task TurnAsync(Attempt a, MonsterUnit ship, float face)
    {
        if (a.Settled || a.Room.Closed || ship.Dead) return;
        _monsters.Turn(ship, face);
        long now = MonsterService.NowMs;
        foreach (var rider in a.Riders.GetValueOrDefault(ship.Uid) ?? [])
        {
            if (rider.Seat is not { } seat || rider.Removed) continue;
            var (x, z) = Seat(ship.X, ship.Z, face, seat);
            await _world.MoveUnitAsync(rider, rider.Walk(x, ship.Y + seat.Dy, z, now, now + ReseatMs));
        }
    }

    /// <summary>A scripted fight's monster is down: its end effects play where it fell, and its riders leave with it.</summary>
    private async Task FightEndedAsync(Attempt a, MonsterUnit monster)
    {
        if (a.Sinks.Remove(monster.Uid, out var sink))
            foreach (int effect in sink)
                foreach (int kind in DisplayKinds(effect, 0))
                    await BroadcastAsync(a, CombatService.SkillEffect, new SkillEffectDisplayNotify
                    {
                        SourceId = monster.Uid, TargetId = monster.Uid, TargetPos = monster.Pos, Is3DPosition = true,
                        EffectId = effect, EffectType = kind, SkillId = OwnerSkill(effect),
                    });
        if (a.Riders.Remove(monster.Uid, out var crew))
            foreach (var rider in crew) await _instances.RemovePropAsync(a.Room, rider);
    }

    /// <summary>
    /// A unit plays its NPCConfig _kRespawnSkill as it appears (31100 from it, so the client runs the skill's show: the
    /// boarders' FallDown 5364701/5364601, the Evil Boss's entrance 5351901, a blast's boom 5353101), the way the
    /// original's units entered (inferred). Only the show: nothing is hit. Its _iActionTime in ms (0: none).
    /// </summary>
    private Task<int> EntranceAsync(WorldUnit unit, int npcId) =>
        NPCConfig.Get(_data, npcId) is { RespawnSkill: > 0 and var skill } ? SkillShowAsync(unit, skill) : Task.FromResult(0);

    /// <summary>A unit plays a skill's show (31100 from it, at itself); the skill's _iActionTime in ms (0: no such skill).</summary>
    private async Task<int> SkillShowAsync(WorldUnit unit, long skill)
    {
        if (SkillConfig.Get(_data, skill) is not { } row) return 0;
        int ms = (int)Math.Clamp(row.ActionTime, 0, 60000);
        await _world.ToUnitViewersAsync(unit, CombatService.UseInSkill, new UseInSkillInfo
        {
            SrcID = unit.Uid, SkillID = (int)skill, TargetId = unit.Uid, TargetPos = unit.Pos,
            Face = unit is SceneActorUnit { Seat: not null } ? (unit.X > 245 ? 270f : 90f) : unit.Face,
            SessionId = Interlocked.Increment(ref _cueSession), SkillMs = ms, StartTime = MonsterService.NowMs, PlayAct = true,
        });
        return ms;
    }

    /// <summary>
    /// The enum_DisplayEffectType kinds a Lua_Config_EffectData effect has content for, in its m_kPoint or its
    /// m_kDamage.m_kActionDamge: AnimGroup 1 (m_kAni), Gfx 2 (m_kGfxList), ShakeCamera 3 (m_kCameraShake), Audio 4
    /// (m_kAudioList); or the one kind asked for.
    /// </summary>
    private int[] DisplayKinds(int effect, int asked)
    {
        if (asked != 0) return [asked];
        return _data.SkillTemplateTable("Lua_Config_EffectData").TryGetValue(effect.ToString(CultureInfo.InvariantCulture), out var e)
            ? SkillShows.DisplayKinds(e) : [];
    }

    /// <summary>The skill whose template holds an effect (effect ids are the template's id and a two-digit index, the
    /// skill's the template's and 01): 5353102 belongs to 5353101. 0 when there is no such skill.</summary>
    private long OwnerSkill(int effect) =>
        _data.Row("SkillConfig", effect / 100 * 100 + 1) != null ? effect / 100 * 100 + 1 : 0;
}
