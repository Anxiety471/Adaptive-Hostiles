namespace AdaptiveHostiles.Core;

/// <summary>Main-thread view of the map supplied by the game layer.</summary>
public interface ITacticalMap
{
    bool IsStandable(int x, int z);
    bool IsReachable(int x, int z);
    float CoverAt(int x, int z);
    float ExposureAt(int x, int z);
    /// <summary>True when a hostile turret can shoot this cell.</summary>
    bool InTurretRange(int x, int z) => false;
}

public static class CandidateEnumerator
{
    public static string IdFor(int x, int z) => $"cell_{x}_{z}";

    private static readonly string[] PurposeOrder = { "cover", "firing_position", "flank", "advance", "retreat" };

    /// <summary>
    /// Enumerates up to <paramref name="max"/> standable, reachable cells around the pawn,
    /// mixed across purposes. Output order is deterministic for identical input.
    /// </summary>
    public static IReadOnlyList<MovementCandidate> Enumerate(
        ITacticalMap map, int selfX, int selfZ, IReadOnlyList<(int X, int Z)> threats,
        float weaponRange = 25f, int radius = 12, int step = 2,
        int max = OperativePlanner.MaxDestinations - 1)
    {
        var buckets = PurposeOrder.ToDictionary(p => p, _ => new List<(double Score, MovementCandidate C)>());
        var d0 = NearestThreat(selfX, selfZ, threats);

        for (var dx = -radius; dx <= radius; dx += step)
        for (var dz = -radius; dz <= radius; dz += step)
        {
            if (dx == 0 && dz == 0) continue;
            int x = selfX + dx, z = selfZ + dz;
            if (!map.IsStandable(x, z) || !map.IsReachable(x, z)) continue;

            var cover = Math.Clamp(map.CoverAt(x, z), 0f, 1f);
            var exposure = Math.Clamp(map.ExposureAt(x, z), 0f, 1f);
            var d1 = NearestThreat(x, z, threats);
            var purpose = Classify(cover, d0, d1, weaponRange, threats.Count > 0);
            var distance = (float)Math.Sqrt(dx * dx + dz * dz);
            var turret = map.InTurretRange(x, z);
            buckets[purpose].Add((cover - exposure - (turret ? TacticalHeuristics.TurretPenalty : 0), new MovementCandidate(
                IdFor(x, z), x, z, cover, exposure, distance, purpose, true, turret)));
        }

        foreach (var list in buckets.Values)
            list.Sort((a, b) =>
            {
                var c = b.Score.CompareTo(a.Score);
                if (c != 0) return c;
                c = a.C.Distance.CompareTo(b.C.Distance);
                return c != 0 ? c : string.CompareOrdinal(a.C.Id, b.C.Id);
            });

        // Round-robin across purposes so one category cannot crowd out the others.
        var result = new List<MovementCandidate>(max);
        for (var round = 0; result.Count < max; round++)
        {
            var added = false;
            foreach (var p in PurposeOrder)
            {
                if (round < buckets[p].Count && result.Count < max)
                {
                    result.Add(buckets[p][round].C);
                    added = true;
                }
            }
            if (!added) break;
        }
        return result;
    }

    private static string Classify(float cover, double d0, double d1, float range, bool hasThreats)
    {
        if (!hasThreats) return cover >= 0.5f ? "cover" : "advance";
        if (d1 > d0 + 2) return "retreat";
        // Accuracy falls off fast with distance, so firing positions are short to medium range, not the edge of it.
        if (d1 <= range * 0.65 && d1 >= range * 0.3) return "firing_position";
        if (cover >= 0.5f) return "cover";
        if (d1 < d0 - 2) return "advance";
        return "flank";
    }

    private static double NearestThreat(int x, int z, IReadOnlyList<(int X, int Z)> threats)
    {
        if (threats.Count == 0) return double.MaxValue / 4;
        var best = double.MaxValue;
        foreach (var (tx, tz) in threats)
            best = Math.Min(best, Math.Sqrt((double)(tx - x) * (tx - x) + (double)(tz - z) * (tz - z)));
        return best;
    }
}
