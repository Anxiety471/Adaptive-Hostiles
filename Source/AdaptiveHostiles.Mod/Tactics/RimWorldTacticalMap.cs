using AdaptiveHostiles.Core;
using RimWorld;
using Verse;
using Verse.AI;

namespace AdaptiveHostiles.Mod.Tactics;

/// <summary>
/// <see cref="ITacticalMap"/> over a live RimWorld map from one Operative's point of view.
/// Game thread only. Cover is the block chance against the nearest threat; exposure is the
/// share of sampled threats with a clear line to the cell.
/// </summary>
public sealed class RimWorldTacticalMap(
    Pawn pawn, IReadOnlyList<IntVec3> threats, IReadOnlyList<(IntVec3 Pos, float Range)> turrets) : ITacticalMap
{
    private readonly Map map = pawn.Map;

    public bool IsStandable(int x, int z)
    {
        var c = new IntVec3(x, 0, z);
        return c.InBounds(map) && c.Standable(map) && !c.Fogged(map) && !c.IsForbidden(pawn);
    }

    public bool IsReachable(int x, int z)
    {
        var c = new IntVec3(x, 0, z);
        return c.InBounds(map) &&
               map.reachability.CanReach(pawn.Position, c, PathEndMode.OnCell, TraverseParms.For(pawn));
    }

    public bool InTurretRange(int x, int z)
    {
        var c = new IntVec3(x, 0, z);
        foreach (var (pos, range) in turrets)
            if (c.DistanceTo(pos) <= range + 1f) return true;
        return false;
    }

    public float CoverAt(int x, int z)
    {
        if (threats.Count == 0) return 0f;
        var c = new IntVec3(x, 0, z);
        return CoverUtility.CalculateOverallBlockChance(c, Nearest(c), map);
    }

    public float ExposureAt(int x, int z)
    {
        if (threats.Count == 0) return 0f;
        var c = new IntVec3(x, 0, z);
        var seen = 0;
        foreach (var t in threats)
            if (GenSight.LineOfSight(t, c, map)) seen++;
        return seen / (float)threats.Count;
    }

    private IntVec3 Nearest(IntVec3 c)
    {
        var best = threats[0];
        foreach (var t in threats)
            if (t.DistanceToSquared(c) < best.DistanceToSquared(c)) best = t;
        return best;
    }
}
