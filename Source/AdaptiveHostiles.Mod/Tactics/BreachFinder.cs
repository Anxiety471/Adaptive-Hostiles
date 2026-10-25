using AdaptiveHostiles.Core;
using RimWorld;
using Verse;

namespace AdaptiveHostiles.Mod.Tactics;

/// <summary>
/// Finds where the colony is weakly defended and builds the role cells for it. A breach point is a
/// reachable cell near the defenders; its weakness falls with every defender who sees it and every
/// turret that covers it, so open kill zones in front of the defenders score low. Game thread only.
/// </summary>
public static class BreachFinder
{
    public const int MaxPoints = 3;
    private const float StageDistance = 16f;
    private const float DefenderSightRange = 25f;
    private const int MaxPathTargets = 5;
    private const int MinSpacing = 8;

    public static IntVec3 DefenderCentre(IReadOnlyList<Pawn> defenders, IReadOnlyList<Building_Turret> turrets)
    {
        var cells = defenders.Select(p => p.Position).Concat(turrets.Select(t => t.Position)).ToList();
        if (cells.Count == 0) return IntVec3.Invalid;
        return new IntVec3((int)cells.Average(c => c.x), 0, (int)cells.Average(c => c.z));
    }

    /// <summary>
    /// One candidate per defender or turret the reference raider can path to: the stand cell and the first wall or door
    /// on that shortest path (the same route vanilla sappers dig along). Open bases have no wall, so the candidate is a
    /// cell a few steps short of the target.
    /// </summary>
    public static List<BreachPoint> Find(
        Pawn reference, RimWorldTacticalMap map, IReadOnlyList<Pawn> defenders,
        IReadOnlyList<Building_Turret> turrets, IntVec3 centre)
    {
        var candidates = new List<BreachPoint>();
        if (!centre.IsValid) return candidates;
        var targets = defenders.Cast<Thing>().Take(MaxPathTargets).Concat(turrets.Take(MaxPathTargets));
        foreach (var target in targets)
        {
            var cells = WallBreaker.PathCells(reference, target);
            var nodes = cells.Select(c => (c.x, c.z)).ToList();
            Log.Message($"[Adaptive Hostiles] breach path to {target.LabelShort} at ({target.Position.x},{target.Position.z}): " +
                        $"{nodes.Count} cells" + (nodes.Count > 0 ? $" from ({nodes[0].x},{nodes[0].z}) to ({nodes[^1].x},{nodes[^1].z})" : ""));
            var site = BreachRoute.Resolve(
                nodes,
                (x, z) => WallBreaker.BlockerAt(reference, new IntVec3(x, 0, z)) is not null,
                (x, z) => map.IsStandable(x, z) && map.IsReachable(x, z));
            if (site is not { } s) continue;

            var cell = new IntVec3(s.StandX, 0, s.StandZ);
            var sees = defenders.Count(d => d.Position.DistanceTo(cell) <= DefenderSightRange &&
                                            GenSight.LineOfSight(d.Position, cell, reference.Map));
            var covered = turrets.Count(t => cell.DistanceTo(t.Position) <= OperativeSampler.TurretRange(t) + 1f);
            var weakness = 1f / (1 + sees + 2 * covered);
            candidates.Add(new BreachPoint("", s.StandX, s.StandZ, weakness, sees, covered, s.WallX, s.WallZ));
        }
        return BreachRoute.Pick(candidates, MaxPoints, MinSpacing);
    }

    /// <summary>
    /// One candidate per breach point for this raider: melee get a push cell at the breach, ranged get a
    /// support cell behind it (on the raiders' side), so the two roles converge on the same side together.
    /// </summary>
    public static List<MovementCandidate> RoleCells(
        Pawn pawn, RimWorldTacticalMap map, IReadOnlyList<BreachPoint> points, IntVec3 centre,
        bool isMelee, float range, int slot, HashSet<(int, int)> used)
    {
        var result = new List<MovementCandidate>();
        foreach (var bp in points)
        {
            // Outward direction: from the wall (or, in an open base, the defenders) through the breach cell.
            var fromX = bp.WallX >= 0 ? bp.WallX : centre.x;
            var fromZ = bp.WallX >= 0 ? bp.WallZ : centre.z;
            var anchorX = bp.X;
            var anchorZ = bp.Z;
            if (!isMelee)
            {
                // Behind the melee push: continue the line outward.
                var dx = bp.X - fromX;
                var dz = bp.Z - fromZ;
                var len = Math.Max(1f, (float)Math.Sqrt(dx * dx + dz * dz));
                var back = Math.Clamp(range * 0.5f, 4f, 12f);
                anchorX = bp.X + (int)Math.Round(dx / len * back);
                anchorZ = bp.Z + (int)Math.Round(dz / len * back);
            }

            Add(result, pawn, map, FindCell(map, anchorX, anchorZ, slot, used), (isMelee ? "breach_" : "support_") + bp.Id, used);

            // Staging cell: further out on the raiders' side, where the squad gathers until the go signal.
            var sx = bp.X - fromX;
            var sz = bp.Z - fromZ;
            var slen = Math.Max(1f, (float)Math.Sqrt(sx * sx + sz * sz));
            Add(result, pawn, map,
                FindCell(map, bp.X + (int)Math.Round(sx / slen * StageDistance), bp.Z + (int)Math.Round(sz / slen * StageDistance), slot, used),
                "stage_" + bp.Id, used);
        }
        return result;
    }

    private static void Add(
        List<MovementCandidate> result, Pawn pawn, RimWorldTacticalMap map, (int, int)? cell, string purpose,
        HashSet<(int, int)> used)
    {
        if (cell is null) return;
        used.Add(cell.Value);
        var (x, z) = cell.Value;
        result.Add(new MovementCandidate(
            CandidateEnumerator.IdFor(x, z), x, z, map.CoverAt(x, z), map.ExposureAt(x, z),
            (float)new IntVec3(x, 0, z).DistanceTo(pawn.Position), purpose, true, map.InTurretRange(x, z)));
    }

    // Nearest free standable, reachable cell around the anchor; slot rotates the search so members spread.
    private static (int, int)? FindCell(RimWorldTacticalMap map, int ax, int az, int slot, HashSet<(int, int)> used)
    {
        for (var r = 0; r <= 4; r++)
        {
            var ring = new List<(int, int)>();
            for (var dx = -r; dx <= r; dx++)
            for (var dz = -r; dz <= r; dz++)
                if (Math.Max(Math.Abs(dx), Math.Abs(dz)) == r) ring.Add((ax + dx, az + dz));
            for (var i = 0; i < ring.Count; i++)
            {
                var c = ring[(i + slot) % ring.Count];
                if (used.Contains(c) || !map.IsStandable(c.Item1, c.Item2) || !map.IsReachable(c.Item1, c.Item2)) continue;
                return c;
            }
        }
        return null;
    }
}
