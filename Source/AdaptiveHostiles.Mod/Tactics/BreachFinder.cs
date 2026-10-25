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
        IReadOnlyList<Building_Turret> turrets, IntVec3 centre, DangerMemory danger, long now)
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
            var fell = danger.Penalty(s.StandX, s.StandZ, now);
            var weakness = BreachRoute.Weakness(sees, covered, fell);
            candidates.Add(new BreachPoint("", s.StandX, s.StandZ, weakness, sees, covered, s.WallX, s.WallZ, fell));
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
            // Role cells stay on the raiders' side of the breach wall; a cell inside the base would send them around it.
            var wallSide = bp.WallX >= 0 ? (Func<int, int, bool>)((x, z) => BreachRoute.OutsideWall(bp.WallX, bp.WallZ, bp.X, bp.Z, x, z)) : null;
            if (!isMelee)
            {
                // Beside the opening, hugging the wall, rather than straight behind it in the defenders' line of fire.
                (anchorX, anchorZ) = BreachRoute.SupportAnchor(bp.X, bp.Z, fromX, fromZ, range, slot, bp.WallX >= 0);
            }

            var role = isMelee ? FindCell(map, anchorX, anchorZ, slot, used, wallSide) : FindCover(map, anchorX, anchorZ, slot, used, wallSide);
            Add(result, pawn, map, role, (isMelee ? "breach_" : "support_") + bp.Id, used);
            // A long way from the role cell: the next stop is a covered cell part of the way there, not one run across open ground.
            if (role is { } rc)
            {
                var hx = rc.Item1 - pawn.Position.x;
                var hz = rc.Item2 - pawn.Position.z;
                var hlen = (float)Math.Sqrt(hx * hx + hz * hz);
                // Far out the first stop is the edge of the exposed zone (25 cells from the role cell), so the walk in is not slowed.
                var step = hlen > BreachRoute.HopFar + BreachRoute.HopStep ? hlen - BreachRoute.HopFar : BreachRoute.HopStep;
                if (hlen > BreachRoute.HopTrigger)
                    Add(result, pawn, map, FindCover(map, pawn.Position.x + (int)Math.Round(hx / hlen * step),
                        pawn.Position.z + (int)Math.Round(hz / hlen * step), slot, used, null), "hop_" + bp.Id, used);
            }
            // An open breach has no wall to break: melee also get a press cell past the opening toward the defenders, one per raider.
            if (isMelee && bp.WallX < 0 && centre.IsValid)
            {
                var px = centre.x - bp.X;
                var pz = centre.z - bp.Z;
                var plen = Math.Max(1f, (float)Math.Sqrt(px * px + pz * pz));
                var ahead = Math.Min(3f + slot, Math.Max(3f, plen - 1f));
                Add(result, pawn, map, FindCell(map, bp.X + (int)Math.Round(px / plen * ahead), bp.Z + (int)Math.Round(pz / plen * ahead), slot, used, null),
                    "press_" + bp.Id, used);
            }
            // A stalled assault sends ranged raiders to open a wall too, so they are offered the breach cell as well.
            if (!isMelee)
                Add(result, pawn, map, FindCell(map, bp.X, bp.Z, slot, used, wallSide), "breach_" + bp.Id, used);

            // Staging cell: further out on the raiders' side, where the squad gathers until the go signal.
            var sx = bp.X - fromX;
            var sz = bp.Z - fromZ;
            var slen = Math.Max(1f, (float)Math.Sqrt(sx * sx + sz * sz));
            Add(result, pawn, map,
                FindCover(map, bp.X + (int)Math.Round(sx / slen * StageDistance), bp.Z + (int)Math.Round(sz / slen * StageDistance), slot, used, wallSide),
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

    // The best free cell within 3 of the anchor by BreachRoute.CellScore: out of the defenders' sight, hugging a wall or corner.
    private static (int, int)? FindCover(
        RimWorldTacticalMap map, int ax, int az, int slot, HashSet<(int, int)> used, Func<int, int, bool>? allowed)
    {
        (int, int)? best = null;
        var bestScore = float.MaxValue;
        for (var r = 0; r <= 3; r++)
        for (var dx = -r; dx <= r; dx++)
        for (var dz = -r; dz <= r; dz++)
        {
            if (Math.Max(Math.Abs(dx), Math.Abs(dz)) != r) continue;
            int x = ax + dx, z = az + dz;
            if (used.Contains((x, z)) || (allowed is not null && !allowed(x, z)) || !map.IsStandable(x, z) || !map.IsReachable(x, z)) continue;
            var score = BreachRoute.CellScore(map.ExposureAt(x, z), map.HugsWall(x, z), r);
            if (score >= bestScore) continue;
            best = (x, z);
            bestScore = score;
        }
        return best ?? FindCell(map, ax, az, slot, used, allowed);
    }

    // Nearest free standable, reachable cell around the anchor; slot rotates the search so members spread.
    private static (int, int)? FindCell(
        RimWorldTacticalMap map, int ax, int az, int slot, HashSet<(int, int)> used, Func<int, int, bool>? allowed = null)
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
                if (used.Contains(c) || (allowed is not null && !allowed(c.Item1, c.Item2)) || !map.IsStandable(c.Item1, c.Item2) || !map.IsReachable(c.Item1, c.Item2)) continue;
                return c;
            }
        }
        return null;
    }
}
