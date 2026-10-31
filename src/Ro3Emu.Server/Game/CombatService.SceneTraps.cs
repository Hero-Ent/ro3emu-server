using System.Runtime.CompilerServices;
using System.Text.Json;
using Romsg;

namespace Ro3Emu.Server.Game;

/// <summary>
/// The novice passage's ant eggs (Ant Queen's Nest, DungeonConfig 1000201): client-only units (scene editor
/// m_kMonsterSpawnPoints of monsterType 1 with clientMonsterConfigKey MainStoryAntEggExploseParam, created by
/// LC_Scene_Sync.InitLocalMonster from LC_DunGeon_Base.InitModules). Each carries an LC_LocalTriggerComponent built from
/// PublicParamConfig.MainStoryAntEggExploseParam [1 TriggerSkillMonster, 300 (radius in cm), 5363001 (skill),
/// 5363101 (damageSkillID)]: when the player first comes within 3 m the egg plays 5363001 locally, and
/// LC_LocalSkillComponent._checkDamage sends USE_SKILL_REQ {skillId 5363101, targetId = the local player,
/// isExtraSkill = true} at each attack point of that skill. The player hits itself with the egg.
/// 5363101 (_kScriptLogic MainStory_AntEggBomb, _iExtraType 1) is never learned, so the ordinary cast path refused it
/// with "Skill has not been learned" and the eggs did nothing. Its damage was the missing server script's
/// (_kDamageParam1 [0]): here each egg takes a third of the player's maximum HP, so the third egg ends the run, which the
/// scene then starts over (inferred, from the official run: three eggs and the passage restarts).
/// </summary>
public sealed partial class CombatService
{
    private const string AntEggBombLogic = "MainStory_AntEggBomb";
    /// <summary>(inferred) One egg sends its hit once per attack point; two hits closer than this are the same egg.</summary>
    private const long SceneTrapIntervalMs = 500;
    private const int SceneTrapShare = 3;

    private sealed class TrapClock { public long LastMs; }
    private readonly ConditionalWeakTable<GameSession, TrapClock> _sceneTraps = new();

    /// <summary>True when the request is a scene trap's self-hit, which is handled here and never cast.</summary>
    private bool SceneTrap(GameSession s, CastSkillReq req, JsonElement row)
    {
        if (!req.IsExtraSkill || Script(row) != AntEggBombLogic) return false;
        // Only a player in a dungeon run, on itself (targetId is LC_UnitManager.GetLocalPlayerID).
        if (s.DungeonAttempt == null || s.Dead || s.Role is not { } role || (req.TargetId != 0 && req.TargetId != role.Id)) return true;
        var clock = _sceneTraps.GetOrCreateValue(s);
        long now = Now;
        if (now - clock.LastMs < SceneTrapIntervalMs) return true;
        clock.LastMs = now;
        long damage = Math.Max(1, (_character.Stats(s)[101] + SceneTrapShare - 1) / SceneTrapShare);
        // After the cast handler has released the role's locks: the hit takes them again.
        _loop.TrackRuntimeDetached("scene trap", async () =>
        {
            if (ReferenceEquals(s.Role, role) && !s.Dead) await EnvironmentDamageAsync(s, damage);
        });
        return true;
    }
}
