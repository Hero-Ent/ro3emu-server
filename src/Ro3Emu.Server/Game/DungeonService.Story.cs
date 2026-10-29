using Romsg;
using Ro3Emu.Server.Data.Config;
using Ro3Emu.Server.Maps;
using Kind = Ro3Emu.Server.Game.ReconstructedDungeons.TriggerKind;

namespace Ro3Emu.Server.Game;

/// <summary>
/// What a story chain does around its scripted steps (Reconstructed/scripted-scenes.json), as the novice Ant Queen's Nest
/// (1000201 → 1000301 → 1000401) needs it:
/// <list type="bullet">
/// <item>A slide (ScriptedStep.Slide): the player's custom data Player_Slide* (Lua_CustomDataDefine 5400001-5400008,
/// Lua_CustomDataConfigExt → LC_CustomDataHandler_Slide.SetSlideData → LC_Movement_Controller_Slide.OnCustomData).
/// State 1 enters the slide once the speeds are known (_tryApplySlideData), 0 leaves it. Meanwhile the client runs
/// downhill on its own, steers with A/D only (LC_State_Unit_Slide._disableInteraction) and reports where it is every
/// 0.1 s (m_fSlideSyncInterval → ForceSendPos).</item>
/// <item>A ride: an actor whose interaction enters GameplayStatusConfig type 3 (the vehicle driver; NPCInteractConfig
/// _iStatusID 3 on the Deniro's "Ride the Deniro" 5300005, NPCConfig _iNPCSubType 103 with VehicleConfig 5300005)
/// seats the player the way WorldInteractionService seats the world's vehicles (32702 InteractChangeNtf, then 32704
/// Change3CNtf with the seat's _iChange3C). The step's completion dismounts (32702 Idle).</item>
/// <item>A region cue (Cue.Region): it plays when a participant walks into its region.</item>
/// <item>The next scene (ScriptedScene.Next): a won scene goes straight on into the next one (inferred: DungeonConfig
/// 1000301 _iSameDupLoadingType 2 with _kSameDupLoadingTypeParam [1000201] is the client's own loading for entering
/// 1000301 from inside 1000201, and the official run goes from one scene into the next without leaving).</item>
/// <item>A restart (ScriptedScene.RestartOnDeath): a death starts the scene over from its beginning (inferred).</item>
/// </list>
/// </summary>
public sealed partial class DungeonService
{
    /// <summary>Lua_CustomDataDefine Player_SlideState, Player_SlideSpeed, Player_SlideViewMode, Player_SlideCameraId.</summary>
    internal const long SlideStateData = 5400001, SlideSpeedData = 5400003, SlideViewModeData = 5400004, SlideCameraData = 5400005;
    /// <summary>GameplayStatusConfig._iStatusType 3: the vehicle driver (进入载具司机状态).</summary>
    private const int VehicleStatusType = 3;
    /// <summary>NPCConfig._iNPCSubType 103: a vehicle, its _iSubTypeValue a VehicleConfig row.</summary>
    private const int VehicleSubType = 103;
    /// <summary>(inferred) The fall before the next scene, the pause before a restart, and the longest ride: a ride whose
    /// end region the client never reports still ends.</summary>
    private const int NextSceneDelayMs = 1500, RestartDelayMs = 3000, RideLimitMs = 60000;
    /// <summary>PublicParamConfig.FallDownData [0 initial speed, 9.8 gravity, ...]: the client's fall (LC_Scene_Sync,
    /// enum_PosAnimsType.Fall) drops from rest at 9.8 m/s².</summary>
    private const double FallGravity = 9.8;
    /// <summary>(inferred) The pause after the landing before the next scene loads.</summary>
    private const int LandingMs = 500;

    private static CustomData LongData(long id, long value) =>
        new() { Id = id, CustomParam = new CustomParam { Tp = CustomParam.Types.ParamType.Long, LongParam = value } };

    /// <summary>The player's own custom data change (31018), as WorldCoreService.SetCustomAsync sends it; not kept in the
    /// world state, so no later snapshot starts a slide again.</summary>
    private static Task SendCustomAsync(GameSession s, params CustomData[] values) =>
        s.Role == null ? Task.CompletedTask
            : s.SendGameAsync(WorldCoreService.CustomChange, new CustomDataChange { EntityId = s.Role.Id, CustomDatas = { values } });

    private static IEnumerable<GameSession> Playing(Attempt a) =>
        Present(a).Where(m => a.Loaded.Contains(m) && !m.SceneLoading && !m.Dead);

    /// <summary>Every participant slides: view, camera and speeds first, the state last.</summary>
    private static async Task StartSlideAsync(Attempt a, ReconstructedDungeons.Slide slide)
    {
        var speeds = new CustomData
        {
            Id = SlideSpeedData,
            CustomParam = new CustomParam { Tp = CustomParam.Types.ParamType.ListLong, ListLongParam = { slide.SideCm, slide.DownCm } },
        };
        foreach (var member in Playing(a).ToArray())
        {
            a.Sliding.Add(member);
            await SendCustomAsync(member, LongData(SlideViewModeData, slide.ViewMode), LongData(SlideCameraData, slide.CameraId),
                speeds, LongData(SlideStateData, 1));
        }
    }

    private static async Task StopSlideAsync(Attempt a, GameSession? only = null)
    {
        foreach (var member in a.Sliding.Where(m => only == null || m == only).ToArray())
        {
            a.Sliding.Remove(member);
            await SendCustomAsync(member, LongData(SlideStateData, 0));
        }
    }

    /// <summary>
    /// After a scene actor's interaction was accepted: one that enters the vehicle driver's state seats the player. Its
    /// seat is the interaction's _iStatusLocation (one-based) in VehicleConfig._kSeats, as WorldInteractionService reads it.
    /// </summary>
    private async Task TryRideAsync(Attempt a, GameSession s, SceneActorUnit actor, long interactionId)
    {
        if (a.Riding.ContainsKey(s) || s.Role == null
            || NPCInteractConfig.Get(_data, interactionId) is not { StatusId: > 0 } interaction
            || GameplayStatusConfig.Get(_data, interaction.StatusId) is not { StatusType: VehicleStatusType }
            || NPCConfig.Get(_data, actor.NpcId) is not { NPCSubType: VehicleSubType } npc
            || VehicleConfig.Get(_data, npc.SubTypeValue) is not { } vehicle) return;
        int seat = (int)interaction.StatusLocation - 1;
        if (seat < 0 || seat >= vehicle.Seats.Length || VehicleSeatConfig.Get(_data, vehicle.Seats[seat]) is not { } seatRow) return;
        a.Riding[s] = actor;
        await s.SendGameAsync(WorldInteractionService.Change, new InteractChangeNtf
        {
            Uid = s.Role.Id,
            DataInfo = new InteractInfo
            {
                EType = (InteractInfo.Types.EState)VehicleStatusType, StateId = (int)interaction.StatusId,
                InteractConfigId = (int)interactionId, ActiveObjId = actor.Uid,
            },
        });
        await s.SendGameAsync(WorldInteractionService.Change3C, new Change3CNtf { CfgId = (int)seatRow.Change3C });
        if (Step(a) is { } step && step.Complete.FirstOrDefault(t => t.Kind == Kind.Region) is { } end)
            StepLater(a, a.StepGeneration, RideLimitMs, () => StepEventAsync(a, null, [(Kind.Region, end.Id)]));
    }

    private static async Task DismountRidersAsync(Attempt a, GameSession? only = null)
    {
        foreach (var s in a.Riding.Keys.Where(m => only == null || m == only).ToArray())
        {
            a.Riding.Remove(s);
            if (s.Role == null || s.SceneLoading) continue;
            await s.SendGameAsync(WorldInteractionService.Change, new InteractChangeNtf
            {
                Uid = s.Role.Id, DataInfo = new InteractInfo { EType = InteractInfo.Types.EState.Idle },
            });
        }
    }

    /// <summary>A completed step's own end: the slide and the ride stop, and its show plays on every participant.</summary>
    private async Task EndStepAsync(Attempt a, ReconstructedDungeons.ScriptedStep step)
    {
        if (step.Slide != null) await StopSlideAsync(a);
        await DismountRidersAsync(a);
        if (step.ShowAfter != 0)
            foreach (var member in Playing(a).ToArray()) await PlayerShowAsync(a, member, step.ShowAfter);
        if (step.FallAfter != 0 && _catalog.ScenePoint(a.Plan.SceneEntry, step.FallAfter) is { } bottom)
            foreach (var member in Playing(a).ToArray()) await FallAsync(a, member, bottom);
    }

    /// <summary>
    /// A participant falls to a point below it: 5363201 MainStory_Falldown's server part at the passage's end (inferred
    /// from the official run, where the player drops into the hole below the slope and lands before the next scene). A
    /// 31081 MovementChangeNtf of MoveTransFallDown makes LC_Scene_Sync play the unit's "Fall" position animation
    /// (PublicParamConfig.FallDownData: its speed, gravity, effects and sounds) down to the given place; the observers
    /// get the same move.
    /// </summary>
    private async Task FallAsync(Attempt a, GameSession s, MapPoint to)
    {
        if (s.Role == null) return;
        var pos = new Vec3 { X = to.X, Y = to.Y, Z = to.Z };
        var fall = new MovementChangeNtf
        {
            Uid = s.Role.Id, TransType = MovementTransitionType.MoveTransFallDown,
            MovementInfo = new MovementInfo { Pos = pos, Face = s.Face, ServerTime = MonsterService.NowMs },
        };
        double drop = Math.Max(0, s.Position.Y - to.Y);
        a.FallMs = Math.Max(a.FallMs, (int)Math.Ceiling(Math.Sqrt(2 * drop / FallGravity) * 1000));
        await s.SendGameAsync(GameAoiSink.MovementChange, fall);
        s.PreviousPosition = s.Position;
        s.Position = pos;
        s.LastWalkUtc = DateTime.UtcNow;
        s.Walking = false;
        await _world.MoveAsync(s, new WalkReq { Pos = pos, Face = s.Face, BStop = true, SpeedRate = 1 }, movement: fall);
    }

    /// <summary>The run is over, or a participant left it: nobody stays sliding or seated.</summary>
    private static async Task EndStoryStateAsync(Attempt a, GameSession? only = null)
    {
        await StopSlideAsync(a, only);
        await DismountRidersAsync(a, only);
    }

    /// <summary>The step's region cues a participant has just walked into, once each.</summary>
    private async Task RegionCuesAsync(Attempt a, GameSession s, ReconstructedDungeons.ScriptedStep step)
    {
        var cues = step.Cues ?? [];
        for (int i = 0; i < cues.Count; i++)
        {
            var cue = cues[i];
            if (cue.Region == 0 || a.FiredCues.Contains(i)
                || _catalog.SceneRegion(a.Plan.SceneEntry, cue.Region) is not { } region || !region.Contains(s.Position.X, s.Position.Z))
                continue;
            a.FiredCues.Add(i);
            if (cue.AtMs == 0) await PlayCueAsync(a, cue);
            else StepLater(a, a.StepGeneration, cue.AtMs, () => PlayCueAsync(a, cue), whileFighting: true);
        }
    }

    /// <summary>A cue for the participants themselves: their own unit plays a skill's show, and they get a buff.</summary>
    private async Task PlayerCueAsync(Attempt a, ReconstructedDungeons.Cue cue)
    {
        foreach (var member in Playing(a).ToArray())
        {
            if (cue.PlayerCast != 0) await PlayerShowAsync(a, member, cue.PlayerCast);
            if (cue.Buff != 0) await _buffs.AddAsync(member, cue.Buff, cue.BuffMs, member.Role!.Id);
        }
    }

    /// <summary>A participant's own unit plays a skill's show (31100 from it): the fall at the passage's end.</summary>
    private async Task PlayerShowAsync(Attempt a, GameSession s, long skill)
    {
        if (s.Role == null || SkillConfig.Get(_data, skill) is not { } row) return;
        await BroadcastAsync(a, CombatService.UseInSkill, new UseInSkillInfo
        {
            SrcID = s.Role.Id, SkillID = (int)skill, TargetId = s.Role.Id, TargetPos = s.Position.Clone(), Face = s.Face,
            SessionId = Interlocked.Increment(ref _cueSession), SkillMs = (int)Math.Clamp(row.ActionTime, 0, 60000),
            StartTime = MonsterService.NowMs, PlayAct = true,
        });
    }

    /// <summary>A shown actor leaves the scene (Bobo, sucked into the vortex).</summary>
    private async Task RemoveActorAsync(Attempt a, int npcId)
    {
        foreach (var unit in a.SceneActors.Where(u => u.NpcId == npcId).ToArray())
        {
            a.SceneActors.Remove(unit);
            await _instances.RemovePropAsync(a.Room, unit);
        }
    }

    /// <summary>A won scene of a chain: its participants go on into the next scene after the fall.</summary>
    private void ScheduleNextScene(Attempt a, long next) =>
        _loop.ScheduleLegacy(TimeSpan.FromMilliseconds(Math.Max(NextSceneDelayMs, a.FallMs + LandingMs)), async () =>
        {
            if (a.Room.Closed || a.Continued) return;
            var members = Present(a).Where(m => !m.SceneLoading && !Preparing(m)).ToArray();
            if (members.Length == 0) return;
            if (_catalog.Resolve(new EnterDungeonReq { SceneEntry = next }) is not { } plan
                || members.Any(m => Admission(m, plan, continuation: true) != 0))
            {
                // The next scene cannot be entered: the participants leave as from any won story scene.
                foreach (var s in members) await LeaveAsync(s);
                return;
            }
            a.Continued = true;
            await BeginAsync(plan, members, a.Leader, a.TeamId);
        });

    /// <summary>A scene that starts over on a death: the dead participant comes back to life and enters it again.</summary>
    private void ScheduleRestart(Attempt a, GameSession s) =>
        _loop.ScheduleLegacy(TimeSpan.FromMilliseconds(RestartDelayMs), async () =>
        {
            if (a.Continued || a.Room.Closed || s.Role is not { } role || s.InstanceId != a.Room.Id || s.SceneLoading || Preparing(s)) return;
            if (s.Dead)
            {
                if (!_combat.TryClaimRevive(s)) return;
                try { _combat.CommitReviveClaim(s); }
                finally { _combat.ReleaseReviveClaim(s); }
                await _combat.PublishReviveAsync(s, null);
            }
            if (!ReferenceEquals(s.Role, role) || s.Dead) return;
            if (!a.Settled) await SettleAsync(a, false);
            var plan = _catalog.Resolve(new EnterDungeonReq { SceneEntry = a.Plan.SceneEntry }) ?? a.Plan;
            if (Admission(s, plan, continuation: true) != 0) return;
            a.Continued = true;
            await BeginAsync(plan, [s], role.Id, a.TeamId);
        });
}
