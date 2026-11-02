using Romsg;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

public sealed partial class SceneService
{
    private const int TreasureInteraction = 4;
    private const int TreasureNpcId = 21070002;
    /// <summary>
    /// Lua_CustomDataDefine.ScenePointId. NPCInteractConfig 21070002's NPC condition 7100041
    /// (LC_InteractCondition.CheckTreasureIsHunt) offers the dig only when it equals the hunt's 5064 pointId.
    /// </summary>
    internal const long ScenePointIdData = 105;
    /// <summary>Treasure hunt NPC UIDs: 2^46 + role id, below 2^53 (the client keeps unit ids in a double array, see
    /// DungeonService.SceneActorUidBase).</summary>
    internal const long TreasureUidPrefix = 1L << 46;
    internal Func<GameSession, Task<int>>? TreasureComplete { get; set; }

    private static long TreasureUid(GameSession session) => TreasureUidPrefix | session.Role!.Id;

    /// <summary>The hunt's configured map point must exist in the actual scene-editor map export.</summary>
    internal (long Scene, MapPoint Position)? TreasurePoint(long pointId)
    {
        if (data.Row("TreasureHuntMapPointConfig", pointId) is not { } row) return null;
        long mapId = Data.GameData.Int(row, "_iMapId"), mapPoint = Data.GameData.Int(row, "_iMapPointId");
        if (worldData.Map(mapId) is not { } map || !map.Points.TryGetValue(mapPoint, out var position)) return null;
        return (mapId, position);
    }

    internal MapPoint? ConfiguredPoint(long sceneEntry, long pointId) =>
        worldData.Map(sceneEntry) is { } map && map.Points.TryGetValue(pointId, out var point) ? point : null;

    /// <summary>On scene load or map use, make this player's active hunt target visible as a real NPC entity.</summary>
    internal async Task SpawnTreasureNpcAsync(GameSession session)
    {
        if (session.Role == null || session.SceneLoading) return;
        var hunt = session.ActivityRuntime.AdventureTreasure;
        if (hunt.PointId == 0 || hunt.Began + 10800 <= clock.GetUtcNow().ToUnixTimeSeconds()
            || TreasurePoint(hunt.PointId) is not { } target || target.Scene != session.SceneEntry
            || data.Row("NPCConfig", TreasureNpcId) is not { } npcRow) return;
        var npc = new WorldNpc(TreasureUid(session), target.Scene, WorldNpcKind.Npc, TreasureNpcId,
            data.TextOf(npcRow, "_iName"), (int)Data.GameData.Int(npcRow, "_iCamp"),
            target.Position.X, target.Position.Y, target.Position.Z, target.Position.Face, default);
        var entity = NpcEntity(npc);
        entity.CustomDatas.Add(new CustomData
        {
            Id = ScenePointIdData, CustomParam = new CustomParam { Tp = CustomParam.Types.ParamType.Long, LongParam = hunt.PointId },
        });
        var view = new AoIViewChangeData();
        view.Bases.Add(entity);
        await session.SendGameAsync(GameAoiSink.SyncAoiView, view);
    }

    /// <summary>
    /// The owner's hunt NPC on its map. It is not one of the AoI's units, so an unview request (31038) must not
    /// take it away: nothing would send it again before the player reaches the point.
    /// </summary>
    internal bool IsOwnTreasureNpc(GameSession session, long uid)
    {
        if (session.Role == null || uid != TreasureUid(session)) return false;
        var hunt = session.ActivityRuntime.AdventureTreasure;
        return hunt.PointId != 0 && hunt.Began + 10800 > clock.GetUtcNow().ToUnixTimeSeconds()
            && TreasurePoint(hunt.PointId) is { } target && target.Scene == session.SceneEntry;
    }

    internal Task RemoveTreasureNpcAsync(GameSession session)
    {
        var view = new AoIViewChangeData();
        view.LeaveAoi.Add((ulong)TreasureUid(session));
        return session.SendGameAsync(GameAoiSink.SyncAoiView, view);
    }

    internal Task<MonsterUnit?> SpawnTreasureMonsterAsync(GameSession session, int npcId, MapPoint at) =>
        monsters.SpawnSingleAsync(session.SceneEntry, npcId, at);

    internal Task RemoveTreasureMonsterAsync(MonsterUnit monster) => world.RemoveUnitAsync(monster);

    internal bool HasTreasureMonster(long uid) => world.AllUnits().Any(u => u.Uid == uid && u is MonsterUnit { Dead: false });

    /// <summary>
    /// The dig the client sends: LC_InteractManager passes the NPCInteractConfig id it offered (NPCConfig 21070002
    /// _iInteractID, a single id there, not a list) to LC_DAL_Interact.SendNPCInteractionReq as interaction_id.
    /// </summary>
    private bool IsTreasureNpcInteraction(int id) =>
        data.Row("NPCConfig", TreasureNpcId) is { } npc && new Data.Config.NPCConfig(npc).InteractIdValue is { } ids
        && (ids.ValueKind == System.Text.Json.JsonValueKind.Array
            ? ids.EnumerateArray().Any(x => Data.GameData.Number(x) == id)
            : ids.ValueKind == System.Text.Json.JsonValueKind.Number && Data.GameData.Number(ids) == id);

    /// <summary>Only the owner's dynamic UID takes this branch; static interaction id 4 keeps its normal route.</summary>
    private async Task<bool> TryTreasureInteractAsync(GameSession session, NpcInteractionReq req)
    {
        if (session.Role == null || req.NpcUid != TreasureUid(session)
            || req.InteractionId != TreasureInteraction && !IsTreasureNpcInteraction(req.InteractionId)) return false;
        var hunt = session.ActivityRuntime.AdventureTreasure;
        int result = hunt.PointId == 0 || TreasurePoint(hunt.PointId) is not { } target
            ? ErrorCodes.NpcMissing
            : session.SceneLoading ? ErrorCodes.SwitchingScenes
            : target.Scene != session.SceneEntry ? ErrorCodes.TreasurePointElsewhere
            : MonsterService.Distance(session.Position.X, session.Position.Z, target.Position.X, target.Position.Z) > 4f + InteractSlack
                ? ErrorCodes.TreasurePointTooFar : TreasureComplete == null ? ErrorCodes.FeatureUnavailable
                : await TreasureComplete(session);
        await session.SendGameAsync(NpcInteraction, new NpcInteractionRsp { Result = result, Req = req });
        return true;
    }
}
