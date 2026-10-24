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
    private const float MaxDefenderDistance = 20f;
    private const float DefenderSightRange = 25f;
    private static readonly int[] Radii = { 6, 10, 14, 18 };

    public static IntVec3 DefenderCentre(IReadOnlyList<Pawn> defenders, IReadOnlyList<Building_Turret> turrets)
    {
        var cells = defenders.Select(p => p.Position).Concat(turrets.Select(t => t.Position)).ToList();
        if (cells.Count == 0) return IntVec3.Invalid;
        return new IntVec3((int)cells.Average(c => c.x), 0, (int)cells.Average(c => c.z));
    }

    public static List<BreachPoint> Find(
        Pawn reference, RimWorldTacticalMap map, IReadOnlyList<Pawn> defenders,
        IReadOnlyList<Building_Turret> turrets, IntVec3 centre)
    {
        var scored = new List<(BreachPoint Point, float Dist)>();
        if (!centre.IsValid) return new List<BreachPoint>();
        for (var ri = 0; ri < Radii.Length; ri++)
        for (var deg = 0; deg < 360; deg += 30)
        {
            var rad = deg * Math.PI / 180.0;
            int x = centre.x + (int)Math.Round(Radii[ri] * Math.Cos(rad));
            int z = centre.z + (int)Math.Round(Radii[ri] * Math.Sin(rad));
            if (!map.IsStandable(x, z) || !map.IsReachable(x, z)) continue;
            var cell = new IntVec3(x, 0, z);
            if (!defenders.Any(d => d.Position.DistanceTo(cell) <= MaxDefenderDistance) &&
                !turrets.Any(t => t.Position.DistanceTo(cell) <= MaxDefenderDistance)) continue;

            var sees = defenders.Count(d => d.Position.DistanceTo(cell) <= DefenderSightRange &&
                                            GenSight.LineOfSight(d.Position, cell, reference.Map));
            var covered = turrets.Count(t => cell.DistanceTo(t.Position) <= OperativeSampler.TurretRange(t) + 1f);
            var weakness = 1f / (1 + sees + 2 * covered);
            scored.Add((new BreachPoint("", x, z, weakness, sees, covered), cell.DistanceTo(reference.Position)));
        }

        var picked = new List<BreachPoint>();
        foreach (var (point, _) in scored.OrderByDescending(s => s.Point.Weakness).ThenBy(s => s.Dist)
                     .ThenBy(s => s.Point.X).ThenBy(s => s.Point.Z))
        {
            if (picked.Any(p => Math.Abs(p.X - point.X) + Math.Abs(p.Z - point.Z) < 8)) continue;
            picked.Add(point with { Id = $"B{picked.Count + 1}" });
            if (picked.Count >= MaxPoints) break;
        }
        return picked;
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
            var anchorX = bp.X;
            var anchorZ = bp.Z;
            if (!isMelee)
            {
                // Behind the melee push: continue the defenders-to-breach line outward.
                var dx = bp.X - centre.x;
                var dz = bp.Z - centre.z;
                var len = Math.Max(1f, (float)Math.Sqrt(dx * dx + dz * dz));
                var back = Math.Clamp(range * 0.5f, 4f, 12f);
                anchorX = bp.X + (int)Math.Round(dx / len * back);
                anchorZ = bp.Z + (int)Math.Round(dz / len * back);
            }

            var cell = FindCell(map, anchorX, anchorZ, slot, used);
            if (cell is null) continue;
            used.Add(cell.Value);
            var (x, z) = cell.Value;
            result.Add(new MovementCandidate(
                CandidateEnumerator.IdFor(x, z), x, z, map.CoverAt(x, z), map.ExposureAt(x, z),
                (float)new IntVec3(x, 0, z).DistanceTo(pawn.Position),
                (isMelee ? "breach_" : "support_") + bp.Id, true, map.InTurretRange(x, z)));
        }
        return result;
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
