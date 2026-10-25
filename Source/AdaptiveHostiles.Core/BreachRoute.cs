namespace AdaptiveHostiles.Core;

/// <summary>Where a squad stands to break in: the cell it walks to, and the wall or door it must destroy there (if any).</summary>
public readonly record struct BreachSite(int StandX, int StandZ, int WallX, int WallZ)
{
    public bool HasWall => WallX >= 0;
}

/// <summary>
/// Pure breach-site logic over a path from a raider to a defender. The breach is the first wall or door the
/// path runs into, exactly where vanilla sappers dig, not a cell around the defenders.
/// </summary>
public static class BreachRoute
{
    /// <summary>Farther than this from its role cell, a raider advances through a cover hop cell first.</summary>
    public const float HopTrigger = 14f;
    /// <summary>A cover hop is this many cells along the way to the role cell.</summary>
    public const float HopStep = 10f;
    /// <summary>Beyond this distance from the role cell the first hop ends here; short hops start from this zone.</summary>
    public const float HopFar = 25f;

    /// <summary>Open bases have no wall; the squad approaches to this many cells short of the path's end.</summary>
    public const int OpenSetback = 6;
    /// <summary>Ranged support stands this close behind the breach cell: within a few cells of the melee and in weapon range of the wall.</summary>
    public static float SupportDistance(float range) => Math.Clamp(range * 0.3f, 3f, 6f);

    /// <summary>How far to the side of the opening ranged support stands, hugging the wall face.</summary>
    public const float SideOffset = 2f;

    /// <summary>
    /// Corner ambush: ranged support stands beside the opening, one step out and two cells along the wall face, not
    /// straight behind it in the defenders' line of fire. Alternate raiders take alternate sides. Without a wall there is
    /// nothing to hug, so the anchor is <see cref="SupportDistance"/> back and to the side.
    /// </summary>
    public static (int X, int Z) SupportAnchor(int standX, int standZ, int fromX, int fromZ, float range, int slot, bool hasWall)
    {
        var dx = standX - fromX;
        var dz = standZ - fromZ;
        var len = Math.Max(1f, (float)Math.Sqrt(dx * dx + dz * dz));
        float ox = dx / len, oz = dz / len;
        float lx = -oz, lz = ox;
        var side = slot % 2 == 0 ? 1 : -1;
        var back = hasWall ? 1f : SupportDistance(range);
        return (standX + (int)Math.Round(ox * back + lx * side * SideOffset),
                standZ + (int)Math.Round(oz * back + lz * side * SideOffset));
    }

    /// <summary>
    /// Lower is better: a cell the defenders cannot see (exposure 0..1), hugging a wall or corner, close to the anchor.
    /// </summary>
    public static float CellScore(float exposure, bool hugsWall, int ring) => exposure + 0.1f * ring - (hugsWall ? 0.15f : 0f);

    /// <summary>True when (x,z) is on the raiders' side of the breach wall: further out than the wall along the stand direction.</summary>
    public static bool OutsideWall(int wallX, int wallZ, int standX, int standZ, int x, int z) =>
        (x - wallX) * (standX - wallX) + (z - wallZ) * (standZ - wallZ) > 0;

    /// <summary>
    /// <paramref name="path"/> runs from the raider outward to the defender. A blocker's neighbour on the raider's side
    /// is the stand cell; with no blocker the stand cell is <see cref="OpenSetback"/> cells before the end. A stand
    /// cell the raiders cannot use moves back along the path. Returns null when no usable cell exists.
    /// </summary>
    public static BreachSite? Resolve(
        IReadOnlyList<(int X, int Z)> path, Func<int, int, bool> isBlocker, Func<int, int, bool> canStand)
    {
        if (path.Count == 0) return null;
        var wall = -1;
        for (var i = 0; i < path.Count; i++)
        {
            if (!isBlocker(path[i].X, path[i].Z)) continue;
            wall = i;
            break;
        }

        var from = wall >= 0 ? wall - 1 : Math.Max(0, path.Count - 1 - OpenSetback);
        for (var i = from; i >= 0; i--)
        {
            if (!canStand(path[i].X, path[i].Z)) continue;
            return wall >= 0
                ? new BreachSite(path[i].X, path[i].Z, path[wall].X, path[wall].Z)
                : new BreachSite(path[i].X, path[i].Z, -1, -1);
        }
        return null;
    }

    /// <summary>Weakness 0..1: falls with defenders who see the cell and turrets covering it, and with raider losses nearby.</summary>
    public static float Weakness(int sees, int covered, float danger) =>
        1f / (1 + sees + 2 * covered) * (1f - 0.8f * Math.Clamp(danger, 0f, 1f));

    /// <summary>
    /// Best candidates first by weakness (ties by position, so the order is stable), skipping any site closer than
    /// <paramref name="minSpacing"/> (Manhattan) to one already chosen or standing at the same wall.
    /// </summary>
    public static List<BreachPoint> Pick(IEnumerable<BreachPoint> candidates, int max, int minSpacing)
    {
        var picked = new List<BreachPoint>();
        foreach (var c in candidates.OrderByDescending(c => c.Weakness).ThenBy(c => c.X).ThenBy(c => c.Z))
        {
            if (picked.Any(p => Math.Abs(p.X - c.X) + Math.Abs(p.Z - c.Z) < minSpacing ||
                                (c.WallX >= 0 && p.WallX == c.WallX && p.WallZ == c.WallZ))) continue;
            picked.Add(c with { Id = $"B{picked.Count + 1}" });
            if (picked.Count >= max) break;
        }
        return picked;
    }
}
