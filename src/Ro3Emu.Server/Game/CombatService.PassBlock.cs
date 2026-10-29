using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

public sealed partial class CombatService
{
    /// <summary>
    /// A unit-targeted cast (_iRelease 1) at a monster on a public map skips the wall-piercing (_iPassBlock 2, 3026
    /// "Invalid skill wall-piercing level") approximation (inferred). The client tests a skill line with
    /// PF_AStar.CheckRayLine(..., _iPassBlock) (Lua_SceneSpace.CheckDirectPass), a block-level ray the server cannot
    /// read, so it stands in with the walkability segment (NavGrid.CanWalk); in the field that also refuses lines over
    /// rocks, trees or water. The client's auto-battle never reads the refusal (LC_AutoAttack_AttackSubState
    /// OnCastSkillReq only marks the request answered and the next tick casts again), so a ranged character stood still
    /// re-casting at the same monster forever. Instances (dungeons, siege, events) keep the check.
    /// </summary>
    private static bool FieldMonsterTarget(GameSession s, int release, MonsterUnit? monster) =>
        release == 1 && monster != null && s.InstanceId == 0;
}
