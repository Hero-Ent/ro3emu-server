using System.Text.Json;
using Romsg;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Data.Config;

namespace Ro3Emu.Server.Game;

/// <summary>
/// A scripted step's turret (the airship's cannons, 1000501). 'Board the Cannon' (NPCInteractConfig 5300006) names
/// GameplayStatusConfig 12 新手教程上炮 (tutorial: board the cannon), of type 10 UpperCannon. The client consumes 32702
/// InteractChangeNtf of that type in LC_GamePlayStatusMgr.CommonCannon: the player enters its Interact state on
/// upperCannonInfo.cannonGuId and gets the status's GamePlayStatusExtraConfig abilities (status 12: 3C 19, crosshair 3,
/// pitch) and buttons {27, 28}. Button 27 is a skill button: it casts 5353001 'Fire - Rapid' through the ordinary skill
/// request (LC_Actor_GamePlayStatusInputBase sends only action buttons as 32703), which CombatService admits for the gunner
/// (TurretSkill, TurretAim, TurretFired). Button 28 is an action button: 32703 runs QuitStatus (ActionConfig 5004).
/// The original's cannon script is not in the client: every fight monster sinks after the step's Cannon.Shots fire hits
/// (inferred), and the end of the step, the settlement, a death or leaving the room takes the gunner off the cannon.
/// A shot shows a template with effects instead of the fire skill's empty _iScriptShow 61098 (inferred): the step's
/// Cannon.Show, else the fire skill's own template (SkillConfig _iSkillID 53530, script beginnercannon). The airship's
/// is 61202, the GVE turret show with the same muzzle flash, ball and blast prefabs as 53530 (fx_ylmatarlively_gvepotshot
/// _03_fire/_bullet/_boom) and their sounds, which 53530 lacks: the flash 6120204 from the cannon, the ball 6120205 from
/// its DSpecial1 at 200 m/s (S01_GVE_INTERATIVE_6120205_BULLET) and the blast 6120203 on the ship with its hit
/// (S01_GVE_INTERATIVE_6120203_BEHIT). One shot, one ball, at the ship aimed at.
/// </summary>
public sealed partial class DungeonService
{
    /// <summary>GameplayStatusConfig _iStatusType 10: UpperCannon (InteractInfo.EState.UpperCannon).</summary>
    internal const int UpperCannonStatusType = 10;
    /// <summary>GameplayStatusButtonConfig _iButtonFuncType (LC_GamePlayStatusMgr.enum_ButtonFunc): 0 Action, 1 Skill.</summary>
    internal const int ActionButton = 0, SkillButton = 1;
    /// <summary>ActionConfig 5004: QuitStatus.</summary>
    private const int QuitStatusAction = 5004;
    /// <summary>
    /// (inferred) How far a cannon step's targets are seen, in m: the editor puts the enemy airships 100-105 m from the
    /// cannons, beyond World.ViewRadius (60 m), so the client never got them and fired at nothing it could see.
    /// </summary>
    private const float CannonTargetSight = 150f;

    /// <summary>A participant on a cannon: the actor it sits on, the status it entered and its buttons.</summary>
    internal sealed class Gunner
    {
        public required SceneActorUnit Cannon { get; init; }
        public required long Interaction { get; init; }
        public required long Status { get; init; }
        public required int FireSkill { get; init; }
        public required int Quit { get; init; }
        public required int Shots { get; init; }
        /// <summary>The template a shot shows (Cannon.Show, else the fire skill's _iSkillID): its muzzle flash, ball and blast.</summary>
        public required long Show { get; init; }
        /// <summary>The damage number a hit shows (Cannon.Shown; 0: its own).</summary>
        public long Shown { get; init; }
    }

    /// <summary>A GameplayStatusButtonConfig _kAction list that runs QuitStatus ([["5004"]]).</summary>
    internal static bool RunsQuitStatus(JsonElement actions) => actions.ValueKind == JsonValueKind.Array
        && actions.EnumerateArray().Any(action => action.ValueKind == JsonValueKind.Array && action.GetArrayLength() > 0
            && GameData.Number(action[0]) == QuitStatusAction);

    /// <summary>
    /// The refusal of a 32700 on a cannon of the step (0: allowed, also for any other interaction): a dead participant,
    /// one already on a cannon, or a cannon somebody sits on cannot be boarded.
    /// </summary>
    private int CannonBoardError(Attempt a, GameSession s, SceneActorUnit actor, long interaction) =>
        Step(a) is not { Cannon: { } cannon } || cannon.Interaction != interaction ? 0
        : s.Dead || a.Gunners.ContainsKey(s) || a.Gunners.Values.Any(g => g.Cannon == actor)
            || NPCInteractConfig.Get(_data, interaction) is not { StatusId: > 0 }
            || GameplayStatusButtonConfig.Get(_data, cannon.Fire) is not { Skill: > 0 } ? ErrorCodes.NpcParameter : 0;

    /// <summary>32700 'Board the Cannon', admitted by <see cref="CannonBoardError"/>: the participant enters its status.</summary>
    private async Task BoardCannonAsync(Attempt a, GameSession s, SceneActorUnit actor, long interaction)
    {
        if (Step(a) is not { Cannon: { } cannon } || cannon.Interaction != interaction || a.Settled
            || NPCInteractConfig.Get(_data, interaction) is not { StatusId: > 0 and var status }
            || GameplayStatusButtonConfig.Get(_data, cannon.Fire) is not { Skill: > 0 and var skill }
            || _data.Row("SkillConfig", skill) is not { } fire) return;
        var gunner = new Gunner
        {
            Cannon = actor, Interaction = interaction, Status = status, FireSkill = (int)skill, Quit = cannon.Quit,
            Shots = cannon.Shots, Show = cannon.Show != 0 ? cannon.Show : new SkillConfig(fire).SkillId, Shown = cannon.Shown,
        };
        a.Gunners[s] = gunner;
        await GunnerChangedAsync(a, s, gunner, interrupt: false);
    }

    /// <summary>The gunner leaves its cannon (32702 with the Idle state, to the gunner and those who see it).</summary>
    private static async Task DismountAsync(Attempt a, GameSession s, bool interrupt)
    {
        if (!a.Gunners.Remove(s) || s.Role == null) return;
        await GunnerChangedAsync(a, s, null, interrupt);
    }

    private static async Task DismountAllAsync(Attempt a, bool interrupt)
    {
        foreach (var gunner in a.Gunners.Keys.ToArray()) await DismountAsync(a, gunner, interrupt); // their sessions
    }

    /// <summary>32702 InteractChangeNtf for a gunner's status (null: back to Idle), as WorldInteractionService sends it.</summary>
    private static async Task GunnerChangedAsync(Attempt a, GameSession s, Gunner? gunner, bool interrupt)
    {
        var info = gunner == null ? new InteractInfo { EType = InteractInfo.Types.EState.Idle } : new InteractInfo
        {
            EType = InteractInfo.Types.EState.UpperCannon, StateId = gunner.Status, InteractConfigId = (int)gunner.Interaction,
            ActiveObjId = gunner.Cannon.Uid, UpperCannonInfo = new UpperCannonState { CannonGuId = gunner.Cannon.Uid },
        };
        var message = new InteractChangeNtf { Uid = s.Role!.Id, DataInfo = info, Interrupt = interrupt };
        var viewers = Present(a).Where(m => m != s && a.Loaded.Contains(m) && !m.SceneLoading).ToArray();
        if (!s.SceneLoading) await s.SendGameAsync(WorldInteractionService.Change, message);
        foreach (var viewer in viewers) await viewer.SendGameAsync(WorldInteractionService.Change, message);
    }

    /// <summary>32703 from a gunner (false: not one, WorldInteractionService answers): its quit button leaves the cannon.</summary>
    public static async Task<bool> TryCannonActionAsync(GameSession s, UseInteractActionReq req)
    {
        if (s.DungeonAttempt is not { } a || !a.Gunners.TryGetValue(s, out var gunner)) return false;
        bool quit = req.ButtonId == gunner.Quit;
        if (quit) await DismountAsync(a, s, interrupt: false);
        await s.SendGameAsync(WorldInteractionService.Action, new UseInteractActionRes { Result = quit ? 0 : ErrorCodes.NpcParameter });
        return true;
    }

    /// <summary>CombatService.TurretSkill: the fire button's skill of the cannon the participant sits on.</summary>
    private static bool CannonSkill(GameSession s, int skill) =>
        Active(s) is { } a && a.Gunners.TryGetValue(s, out var gunner) && gunner.FireSkill == skill;

    /// <summary>CombatService.TurretAim: a shot aimed at no living monster of the room goes to the one nearest the cannon.</summary>
    private static void AimCannon(GameSession s, CastSkillReq req)
    {
        if (Active(s) is not { } a || !a.Gunners.TryGetValue(s, out var gunner)) return;
        var living = a.Room.Monsters.Where(m => !m.Dead && m.Hp > 0).ToArray();
        var target = living.FirstOrDefault(m => m.Uid == req.TargetId)
            ?? living.OrderBy(m => MonsterService.Distance(gunner.Cannon.X, gunner.Cannon.Z, m.X, m.Z)).FirstOrDefault();
        if (target == null) return;
        req.TargetId = target.Uid;
        req.TargetPos = target.Pos.Clone();
    }

    /// <summary>
    /// CombatService.TurretFired: an admitted shot first brings its target down to its (shots - k) / shots share of the
    /// maximum HP, so the k-th shot leaves it there and Shots of them sink it at any level, also when a far ship resets
    /// between them (inferred, as the original's rapid fire stood for a volley). A refused cast never gets here. The shot
    /// leaves the cannon with its template's effects.
    /// </summary>
    private static TurretShot? CannonFired(GameSession s, CastSkillReq req)
    {
        if (Active(s) is not { } a || !a.Gunners.TryGetValue(s, out var gunner)) return null;
        var shot = new TurretShot(gunner.Cannon.Uid, gunner.Cannon.Pos, gunner.Show, gunner.Shown);
        if (a.Room.Monsters.FirstOrDefault(m => m.Uid == req.TargetId && !m.Dead && m.Hp > 0) is not { } target) return shot;
        int struck = a.Struck[target.Uid] = a.Struck.GetValueOrDefault(target.Uid) + 1;
        target.Hp = Math.Min(target.Hp, target.Template.MaxHp * Math.Max(0, gunner.Shots - struck) / gunner.Shots + 1);
        return shot;
    }
}
