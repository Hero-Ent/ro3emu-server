using Romsg;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

/// <summary>
/// A scripted ride (ScriptedStep.RideAfter): the actor a player used to complete a step walks a guide route of the scene
/// editor (m_kGuidePoints) and the participants move with it, before the step's teleport and the next step. The client
/// has no ride rule: the route and the scene's camera animation are data without a trigger, and the original's server
/// script is not shipped. So (inferred) the carrier walks at its own UnitConfig._fSpeed, both it and the players are
/// moved in short forced-position steps, and the camera animation is not played.
/// </summary>
public sealed partial class DungeonService
{
    private const int RideTickMs = 100;

    private sealed record Ride(IReadOnlyList<(float X, float Y, float Z)> Path, SceneActorUnit Carrier, float Length,
        int Generation, long StartMs, double DurationMs);

    /// <summary>The carrier's walking speed in metres per second: UnitConfig._fSpeed (cm/s) of its NPC's _iUnitID, 0 if none.</summary>
    private float RideSpeed(SceneActorUnit carrier) =>
        _data.Row("NPCConfig", carrier.NpcId) is { } npc && _data.Row("UnitConfig", GameData.Int(npc, "_iUnitID")) is { } unit
        && unit.TryGetProperty("_fSpeed", out var speed) ? (float)(GameData.Number(speed) ?? 0) / 100f : 0f;

    /// <summary>The horizontal length of a route in metres, as the monsters' walks count distance.</summary>
    internal static float RideLength(IReadOnlyList<(float X, float Y, float Z)> path)
    {
        float length = 0;
        for (int i = 1; i < path.Count; i++) length += MonsterService.Distance(path[i - 1].X, path[i - 1].Z, path[i].X, path[i].Z);
        return length;
    }

    /// <summary>The position and heading after <paramref name="metres"/> along a route (clamped to its ends).</summary>
    internal static (float X, float Y, float Z, float Face) RideAt(IReadOnlyList<(float X, float Y, float Z)> path, float metres)
    {
        float left = Math.Max(0, metres);
        for (int i = 1; i < path.Count; i++)
        {
            var (a, b) = (path[i - 1], path[i]);
            float segment = MonsterService.Distance(a.X, a.Z, b.X, b.Z);
            float face = MonsterService.Angle(a.X, a.Z, b.X, b.Z);
            if (left <= segment || i == path.Count - 1)
            {
                float t = segment <= 0 ? 1 : Math.Min(1, left / segment);
                return (a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t, a.Z + (b.Z - a.Z) * t, face);
            }
            left -= segment;
        }
        return (path[0].X, path[0].Y, path[0].Z, 0);
    }

    /// <summary>
    /// Starts the step's ride when it has one and the player used an actor that can carry (a route with at least two
    /// points, a positive speed). <paramref name="arrived"/> then runs when the route is walked; false means no ride
    /// starts and the caller goes on at once.
    /// </summary>
    private bool TryStartRide(Attempt a, ReconstructedDungeons.ScriptedStep step, SceneActorUnit? carrier, Func<Task> arrived)
    {
        if (step.RideAfter == 0 || carrier == null || _catalog.GuidePath(a.Plan.SceneEntry, step.RideAfter) is not { } path) return false;
        float speed = RideSpeed(carrier), length = RideLength(path);
        if (speed <= 0 || length <= 0) return false;
        var ride = new Ride(path, carrier, length, a.StepGeneration, MonsterService.NowMs, Paced((int)(length / speed * 1000)).TotalMilliseconds);
        _loop.PostLegacy(() => RideTickAsync(a, ride, arrived), "dungeon ride");
        return true;
    }

    private async Task RideTickAsync(Attempt a, Ride ride, Func<Task> arrived)
    {
        if (a.Settled || a.Room.Closed || a.StepGeneration != ride.Generation) return;
        double done = Math.Min(1.0, (MonsterService.NowMs - ride.StartMs) / Math.Max(1.0, ride.DurationMs));
        var at = RideAt(ride.Path, (float)(done * ride.Length));
        ride.Carrier.X = at.X; ride.Carrier.Y = at.Y; ride.Carrier.Z = at.Z; ride.Carrier.Face = at.Face;
        await _world.MoveUnitAsync(ride.Carrier, new MovementChangeNtf
        {
            Uid = ride.Carrier.Uid, TransType = MovementTransitionType.MoveTransForceTeleport,
            MovementInfo = new MovementInfo { Pos = new Vec3 { X = at.X, Y = at.Y, Z = at.Z }, Face = at.Face, ServerTime = MonsterService.NowMs },
        });
        foreach (var member in Present(a).Where(m => a.Loaded.Contains(m) && !m.SceneLoading))
            await _scenes.TeleportInMapAsync(member, new MapPoint(at.X, at.Y, at.Z, at.Face));
        if (done >= 1) await arrived();
        else _loop.ScheduleLegacy(Paced(RideTickMs), () => RideTickAsync(a, ride, arrived), "dungeon ride");
    }
}
