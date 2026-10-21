using Ro3Emu.Server.Maps;

namespace Ro3Emu.FakePlayers;

/// <summary>
/// The walkable cells of one map that are reachable from its born point (a flood fill over the nav grid), so random
/// destinations never fall into a closed pocket. Shared by every fake player on the map.
/// </summary>
public sealed class MapNav
{
    private readonly NavGrid? _nav;
    private readonly (short X, short Z, float Y)[] _cells;
    public long SceneEntry { get; }
    public int ReachableCells => _cells.Length;

    public MapNav(MapWorld map)
    {
        SceneEntry = map.SceneEntry;
        _nav = map.Nav;
        var start = map.Born ?? map.Revive;
        _cells = _nav == null || start == null ? [] : Flood(_nav, start.Value);
    }

    private static (short, short, float)[] Flood(NavGrid nav, MapPoint start)
    {
        int w = nav.Width, h = nav.Height;
        var seen = new bool[w * h];
        var result = new List<(short, short, float)>();
        var queue = new Queue<(int X, int Z, float Y)>();
        int sx = (int)MathF.Floor(start.X), sz = (int)MathF.Floor(start.Z);
        if (sx < 0 || sz < 0 || sx >= w || sz >= h || nav.WalkHeight(sx + .5f, sz + .5f, start.Y) is not { } sy) return [];
        seen[sz * w + sx] = true;
        queue.Enqueue((sx, sz, sy));
        while (queue.Count > 0)
        {
            var (x, z, y) = queue.Dequeue();
            result.Add(((short)x, (short)z, y));
            foreach (var (dx, dz) in Neighbours)
            {
                int nx = x + dx, nz = z + dz;
                if (nx < 0 || nz < 0 || nx >= w || nz >= h || seen[nz * w + nx]) continue;
                if (nav.WalkHeight(nx + .5f, nz + .5f, y) is not { } ny || MathF.Abs(ny - y) > 1f) continue;
                seen[nz * w + nx] = true;
                queue.Enqueue((nx, nz, ny));
            }
        }
        return result.ToArray();
    }

    private static readonly (int, int)[] Neighbours = [(1, 0), (-1, 0), (0, 1), (0, -1)];

    /// <summary>Ground height at x,z near <paramref name="yHint"/>, or null off the walkable area.</summary>
    public float? Height(float x, float z, float yHint) => _nav == null ? yHint : _nav.WalkHeight(x, z, yHint);

    /// <summary>A straight walk from a to b stays on walkable ground.</summary>
    public bool CanWalk(float ax, float ay, float az, float bx, float bz) => _nav?.CanWalk(ax, ay, az, bx, bz) ?? true;

    /// <summary>A reachable point within <paramref name="radius"/> of x,z that a straight walk reaches, else any reachable point.</summary>
    public (float X, float Y, float Z)? Destination(Random r, float x, float y, float z, float radius)
    {
        if (_cells.Length == 0) return null;
        for (int attempt = 0; attempt < 48; attempt++)
        {
            var c = _cells[r.Next(_cells.Length)];
            float cx = c.X + .2f + (float)r.NextDouble() * .6f, cz = c.Z + .2f + (float)r.NextDouble() * .6f;
            float dx = cx - x, dz = cz - z;
            if (attempt < 40 && dx * dx + dz * dz > radius * radius) continue;
            if (dx * dx + dz * dz < 4) continue;
            if (CanWalk(x, y, z, cx, cz)) return (cx, c.Y, cz);
        }
        return null;
    }

    /// <summary>A random reachable point anywhere on the map (a place to put an idle player).</summary>
    public (float X, float Y, float Z)? Anywhere(Random r)
    {
        if (_cells.Length == 0) return null;
        var c = _cells[r.Next(_cells.Length)];
        return (c.X + .5f, c.Y, c.Z + .5f);
    }
}
