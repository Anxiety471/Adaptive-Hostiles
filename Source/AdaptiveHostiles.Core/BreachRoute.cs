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
    /// <summary>Open bases have no wall; the squad approaches to this many cells short of the path's end.</summary>
    public const int OpenSetback = 6;

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
