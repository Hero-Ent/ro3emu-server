using Romsg;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

/// <summary>
/// Roster-to-world projection.
/// The selected setup plan, not ownership alone, determines a visible type-7 actor (inferred).
/// </summary>
public sealed class PetFollowerService(World world, GameData data, FunctionService functions, WorldData? worldData = null)
{
    private long _nextActorId = 1L << 52; // Separate from role IDs and monster IDs (1L << 41).

    /// <summary>Refresh after login or a successful lineup/growth change; pre-world login only prepares the actor.</summary>
    public Task RefreshAsync(GameSession session)
    {
        PetFollowerActor? next = null;
        var pets = session.Pets;
        // (inferred) The tower's separate battle lineup replaces the passive follower projection, otherwise
        // the same pet can appear twice. Its ordinary 5017 plan is preserved and projected again on return.
        if (session.TowerAttempt == null && session.Role is { } role && pets != null
            && pets.SetupPlans.TryGetValue(pets.CurrentPlan, out var plan))
        {
            // A support slot grants passive effects, not a world combat actor (inferred); the lowest valid owned
            // setup slot wins until multi-pet follow behavior is evidenced (inferred). See §5.
            foreach (var slot in plan.SetUpPets.OrderBy(p => p.Key))
            {
                if (!pets.ListPet.ContainsKey(slot.Value)) continue;
                // Function 33 opens on delivered quest 10104330.
                // A persisted lineup cannot bypass the live function gate.
                if (session.Progress == null || !functions.IsOpen(session, 33)) break;
                if (data.Row("PetBaseConfig", slot.Value) is not { } pet) continue;
                int npcId = (int)GameData.Int(pet, "_iMonster");
                if (data.Row("NPCConfig", npcId) is not { } npc || GameData.Int(npc, "_iNPCType") != 7) continue;
                if (session.PetActor is { } old && old.OwnerId == role.Id && old.PetId == slot.Value && old.NpcTemplateId == npcId)
                    next = old;
                else
                    next = new PetFollowerActor(
                        Interlocked.Increment(ref _nextActorId), role.Id, slot.Value, npcId,
                        Math.Max(1, GameData.Int(npc, "_iCamp")), session.Position.Clone(), session.Face, // friendly fallback (inferred)
                        GameData.Int(npc, "_iSpeed"), (int)GameData.Int(npc, "_iElement"),
                        worldData == null ? null : scene => worldData.Map(scene)?.Nav);
                break;
            }
        }
        return world.SetPetAsync(session, next);
    }
}

/// <summary>One server-driven AOI projection; no inferred pet combat, skill AI or damage.</summary>
public sealed class PetFollowerActor(long uid, long ownerId, int petId, int npcTemplateId, long camp, Vec3 position, float face,
    long npcSpeed = 0, int element = 0, Func<long, NavGrid?>? nav = null)
{
    // Config/ComplexConfig/PublicParamConfig.lua:8896-8901: PetConfigFollow = 300.
    // Centimetres and the diagonal side/back placement are (inferred);
    // neither the original server's placement nor its follow cadence is available in the client scripts.
    private const float FollowDistance = 3f;
    // Offset scales tried on the chosen side, then on the two sides turned by 90 degrees (inferred).
    private static readonly float[] OffsetScales = [1f, 0.5f];
    private float _offsetX, _offsetZ;

    public long Uid { get; } = uid;
    public long OwnerId { get; } = ownerId;
    public int PetId { get; } = petId;
    public int NpcTemplateId { get; } = npcTemplateId;
    public long Camp { get; } = camp;
    public long NpcSpeed { get; } = npcSpeed;
    public int Element { get; } = element;
    public Vec3 Position { get; private set; } = position;
    public Vec3 PreviousPosition { get; private set; } = position.Clone();
    public float Face { get; private set; } = face;
    public bool Teleported { get; private set; }
    public bool HasUpdate { get; private set; }
    public bool IsMoving { get; private set; }

    /// <summary>Viewers that received this actor; guarded by World's lock.</summary>
    public HashSet<GameSession> Viewers { get; } = new();

    /// <summary>Translate the side/back offset with accepted owner movement; reversal stays on the same side (inferred).</summary>
    public void Follow(GameSession owner, WalkReq walk, bool forceTeleport = false)
    {
        float dx = owner.Position.X - owner.PreviousPosition.X, dz = owner.Position.Z - owner.PreviousPosition.Z;
        PreviousPosition = Position.Clone();
        Teleported = forceTeleport || dx * dx + dz * dz > 100f; // same 10-unit transition as player AOI
        if (Teleported) SetOffset(walk.Face);
        Position = Place(owner, PreviousPosition);
        bool wasMoving = IsMoving;
        IsMoving = !walk.BStop && !Teleported && owner.Speed > 0 && walk.SpeedRate > 0f;
        HasUpdate = Teleported || Position.X != PreviousPosition.X || Position.Y != PreviousPosition.Y
            || Position.Z != PreviousPosition.Z || Face != walk.Face || IsMoving || wasMoving;
        Face = walk.Face;
    }

    public void AtOwner(GameSession owner)
    {
        SetOffset(owner.Face);
        Position = Place(owner, null);
        PreviousPosition = Position.Clone();
        Face = owner.Face;
        Teleported = false;
        HasUpdate = false;
        IsMoving = false;
    }

    public bool Turn(float face)
    {
        bool changed = Face != face;
        Face = face;
        return changed;
    }

    /// <summary>
    /// The owner's position plus the follow offset, kept on the map's walkable ground (#bug-reports: the pet clipped into
    /// slopes and walls). With the map's nav grid (the client's *_dynamicmapdata walkability, NavGrid), a spot counts
    /// only when the straight line from the owner to it is walkable (NavGrid.CanWalk, the client's direct-walk check) and
    /// its y is that cell's walkable layer, not the owner's y. When no offset spot qualifies, the last spot stays while it
    /// is still walkable and near, else the pet stands on the owner's spot (inferred). Without a nav grid, or when the owner's
    /// own cell has no walkable layer, the plain offset at the owner's height is kept.
    /// </summary>
    private Vec3 Place(GameSession owner, Vec3? previous)
    {
        float ox = owner.Position.X, oy = owner.Position.Y, oz = owner.Position.Z;
        // Without a nav grid, or with an owner on a cell the grid has no walkable layer for (a GM jump, a spot off the
        // baked layers), the plain offset at the owner's height is kept.
        if (nav?.Invoke(owner.SceneEntry) is not { } grid || grid.WalkHeight(ox, oz, oy) == null)
            return new Vec3 { X = ox + _offsetX, Y = oy, Z = oz + _offsetZ };
        (float X, float Z)[] sides = [(_offsetX, _offsetZ), (_offsetZ, -_offsetX), (-_offsetZ, _offsetX)];
        foreach (var (sx, sz) in sides)
        {
            foreach (float scale in OffsetScales)
            {
                float x = ox + sx * scale, z = oz + sz * scale;
                if (grid.CanWalk(ox, oy, oz, x, z) && grid.WalkHeight(x, z, oy) is { } y) return new Vec3 { X = x, Y = y, Z = z };
            }
        }
        if (previous != null)
        {
            float dx = previous.X - ox, dz = previous.Z - oz;
            if (dx * dx + dz * dz <= 4f * FollowDistance * FollowDistance && grid.IsWalkable(previous.X, previous.Y, previous.Z))
                return previous.Clone();
        }
        return new Vec3 { X = ox, Y = grid.WalkHeight(ox, oz, oy) ?? oy, Z = oz };
    }

    private void SetOffset(float face)
    {
        float angle = (face + 135f) * (MathF.PI / 180f);
        _offsetX = FollowDistance * MathF.Sin(angle);
        _offsetZ = FollowDistance * MathF.Cos(angle);
    }

    /// <summary>31037 type 7, NPCConfig template and positive owner.</summary>
    public EntityBase Entity()
    {
        var entity = new EntityBase
        {
            Uuid = (ulong)Uid,
            EntityTemplateID = (ulong)NpcTemplateId,
            Owner = OwnerId,
            EntityType = 7,
            EntityCamp = Camp,
            ElementType = Element,
            State = EntityState.Alive,
            Name = "",
            Face = Face,
            MovementInfo = new MovementInfo
            {
                Pos = Position.Clone(), Face = Face, ServerTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                MoveType = MovementType.MoveTypeNone,
            },
            NotifyType = EntityBase.Types.NotifyType.All,
        };
        // LC_UnitManager.CreateMoveData reads head-info Speed (113) for pet movement animation
        // (re/clientB/lua-src/Manager/LC_UnitManager.lua:2746-2757).
        if (NpcSpeed > 0) entity.Property.Add(new AoIProperty { Idx = 113, Data = NpcSpeed, BaseData = NpcSpeed });
        return entity;
    }
}
