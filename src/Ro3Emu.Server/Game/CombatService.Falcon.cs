using Romsg;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

/// <summary>
/// Blitz Beat's falcon flight. The skill's projectile EffectData (13102200 for _iScriptShow 13102, used by Blitz Beat
/// 13102 and its auto-cast row 13110) is a m_kBulletData with m_eBulletType 3 (enum_BulletTrackType.Falcon; 7 is
/// FalconArc) and an empty m_kBulletName: it is the owner's Falcon pendant flying to the target and back, not a
/// projectile. The client plays it only from a SkillEffectDisplayNotify of type 5 (enum_DisplayEffectType.PendantShow):
/// LC_PerformEffect_Mgr.PlayPendantShow -> LC_Pendant_Manager.PendantPerform (picks the pendant of type Falcon when
/// m_kDamage.m_kBulletData.m_eBulletType is Falcon or FalconArc) -> LC_Pendant_Falcon.ChangeState(Perform), which flies
/// to the target unit's m_kTargetSocket in m_kBulletFalcon.m_iToTargetTime ms and returns in m_iFalconReturnTime ms.
/// Sent as a SkillFixTimeBulletNotify it drew nothing: LC_BulletEmit_Mgr has no Falcon track, so the monster only
/// took damage.
/// </summary>
public sealed partial class CombatService
{
    private const int FalconTrack = 3, FalconArcTrack = 7, WolfTrack = 8, PendantShowEffect = 5;

    /// <summary>A Falcon/FalconArc-track effect's flight time to the target in ms, null when it is not a falcon flight
    /// (CombatService.PendantRunMs; the speed's unit is (inferred) metres per second, as for the wolf).</summary>
    private int? FalconFlightMs(int effectId, float distance) => PendantRunMs(effectId, distance, FalconTrack, FalconArcTrack);

    /// <summary>Sends the falcon to the cast's target and waits for it to arrive before the hit is applied. Returns
    /// false when the caster can no longer apply the cast's effects after the wait.</summary>
    private async Task<bool> FalconFlightAsync(GameSession s, ActiveSkillCast action, int skillId, bool highLevel, int effectId,
        IReadOnlyList<MonsterUnit> targets, GameSession[] players, Func<bool> canApplyMoreEffects)
    {
        if (s.Role == null) return false;
        // One falcon: a second PendantShow restarts its flight (LC_Pendant_Falcon.ChangeState -> _resetPerformAll), so
        // it dives on the locked target and the others are the splash around it (inferred: the first one otherwise).
        if (FalconTarget(action.Request.TargetId, targets, players) is not { } target) return true;
        var (targetId, targetPos) = target;
        await SendSkillEffectAsync(s, new SkillEffectDisplayNotify
        {
            SourceId = s.Role.Id, MasterId = s.Role.Id, TargetId = targetId, TargetPos = targetPos.Clone(),
            EffectId = effectId, EffectType = PendantShowEffect, SkillId = skillId,
        }, highLevel);
        // The damage lands when the falcon reaches the target (inferred from m_kBulletFalcon.m_iToTargetTime).
        int flight = FalconFlightMs(effectId, Distance(s.Position, targetPos)) ?? 0;
        if (flight <= 50) return true;
        await Task.Delay(Math.Min(flight, 1000));
        return canApplyMoreEffects();
    }

    private static (long Id, Vec3 Pos)? FalconTarget(long requested, IReadOnlyList<MonsterUnit> targets,
        GameSession[] players)
    {
        var monster = targets.FirstOrDefault(m => m.Uid == requested) ?? targets.FirstOrDefault();
        if (monster != null) return (monster.Uid, monster.Pos);
        var player = players.FirstOrDefault(p => p.Role?.Id == requested)
            ?? players.FirstOrDefault(p => p.Role != null);
        return player?.Role is { } role ? (role.Id, player.Position) : null;
    }
}
