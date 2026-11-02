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

    /// <summary>A straight walk from a to b stays on walkable ground and enters no blocked cell (a's own cell may be one).</summary>
    public bool CanWalkClear(float ax, float ay, float az, float bx, float bz, Func<int, int, bool>? blocked)
    {
        if (!CanWalk(ax, ay, az, bx, bz)) return false;
        if (blocked == null) return true;
        int startX = (int)MathF.Floor(ax), startZ = (int)MathF.Floor(az);
        float dx = bx - ax, dz = bz - az;
        int steps = Math.Max(1, (int)MathF.Ceiling(MathF.Sqrt(dx * dx + dz * dz) / 0.25f));
        for (int i = 1; i <= steps; i++)
        {
            float t = (float)i / steps;
            int cx = (int)MathF.Floor(ax + dx * t), cz = (int)MathF.Floor(az + dz * t);
            if ((cx != startX || cz != startZ) && blocked(cx, cz)) return false;
        }
        return true;
    }

    /// <summary>
    /// The points to walk to from a to b (the last one b, or the reachable cell nearest b when b itself is shut off),
    /// avoiding the nav grid's closed cells and the <paramref name="blocked"/> ones (a siege's standing air walls): A* over
    /// the 1 m cells as NavGrid.FindPath, each leg then stretched as far as a clear straight walk goes. Null when nothing
    /// within <paramref name="maxNodes"/> cells leads closer.
    /// </summary>
    public List<(float X, float Y, float Z)>? Route(float ax, float ay, float az, float bx, float bz, Func<int, int, bool>? blocked,
        int maxNodes = 40000)
    {
        if (_nav == null) return [(bx, ay, bz)];
        int w = _nav.Width, h = _nav.Height;
        int sx = (int)MathF.Floor(ax), sz = (int)MathF.Floor(az), gx = (int)MathF.Floor(bx), gz = (int)MathF.Floor(bz);
        if (sx < 0 || sz < 0 || sx >= w || sz >= h || gx < 0 || gz < 0 || gx >= w || gz >= h) return null;
        if (CanWalkClear(ax, ay, az, bx, bz, blocked) && Height(bx, bz, ay) is { } direct) return [(bx, direct, bz)];
        int start = sz * w + sx, goal = gz * w + gx;
        var parent = new Dictionary<int, int> { [start] = -1 };
        var cost = new Dictionary<int, float> { [start] = 0 };
        var height = new Dictionary<int, float> { [start] = ay };
        var closed = new HashSet<int>();
        var open = new PriorityQueue<int, float>();
        float Heuristic(int x, int z)
        {
            int dx = Math.Abs(x - gx), dz = Math.Abs(z - gz);
            return Math.Max(dx, dz) + 0.41421357f * Math.Min(dx, dz);
        }
        bool Step(int x, int z, float y, out float found)
        {
            found = 0;
            if (x < 0 || z < 0 || x >= w || z >= h || blocked?.Invoke(x, z) == true) return false;
            if (_nav.WalkHeight(x + 0.5f, z + 0.5f, y) is not { } ny || MathF.Abs(ny - y) > NavGrid.WalkHeightTolerance) return false;
            found = ny;
            return true;
        }
        int best = start;
        float bestLeft = Heuristic(sx, sz);
        open.Enqueue(start, bestLeft);
        while (open.TryDequeue(out int cell, out _))
        {
            if (!closed.Add(cell)) continue;
            int cx = cell % w, cz = cell / w;
            float left = Heuristic(cx, cz);
            if (left < bestLeft) { bestLeft = left; best = cell; }
            if (cell == goal || closed.Count > maxNodes) break;
            float g = cost[cell], y = height[cell];
            for (int dx = -1; dx <= 1; dx++)
                for (int dz = -1; dz <= 1; dz++)
                {
                    if (dx == 0 && dz == 0) continue;
                    bool diagonal = dx != 0 && dz != 0;
                    if (diagonal && (!Step(cx + dx, cz, y, out _) || !Step(cx, cz + dz, y, out _))) continue;
                    if (!Step(cx + dx, cz + dz, y, out float ny)) continue;
                    int next = (cz + dz) * w + cx + dx;
                    float ng = g + (diagonal ? 1.41421357f : 1f);
                    if (closed.Contains(next) || cost.TryGetValue(next, out float old) && old <= ng) continue;
                    cost[next] = ng;
                    parent[next] = cell;
                    height[next] = ny;
                    open.Enqueue(next, ng + Heuristic(cx + dx, cz + dz));
                }
        }
        if (best == start) return null;
        // The chain of cell centres back to the start (the goal itself when reached), then stretched legs.
        var cells = new List<(float X, float Y, float Z)>();
        for (int cell = best; cell >= 0 && parent[cell] >= 0; cell = parent[cell])
            cells.Add(cell == goal && best == goal ? (bx, height[cell], bz) : (cell % w + 0.5f, height[cell], cell / w + 0.5f));
        cells.Reverse();
        var legs = new List<(float X, float Y, float Z)>();
        var from = (X: ax, Y: ay, Z: az);
        for (int i = 0; i < cells.Count;)
        {
            int j = i;
            while (j + 1 < cells.Count && CanWalkClear(from.X, from.Y, from.Z, cells[j + 1].X, cells[j + 1].Z, blocked)) j++;
            legs.Add(cells[j]);
            from = cells[j];
            i = j + 1;
        }
        return legs;
    }
}
