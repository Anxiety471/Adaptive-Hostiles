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
    /// Defenders the reference raider can walk up to with no wall, door or other blocker on the way: standing in the open, not
    /// inside a building. Checked for the nearest few only.
    /// </summary>
    public static List<Pawn> ExposedColonists(Pawn reference, IReadOnlyList<Pawn> defenders)
    {
        var result = new List<Pawn>();
        foreach (var d in defenders.Take(MaxPathTargets * 2))
        {
            var cells = WallBreaker.PathCells(reference, d);
            if (cells.Count == 0) continue;
            if (cells.All(c => WallBreaker.BlockerAt(reference, c) is null)) result.Add(d);
        }
        return result;
    }

    /// <summary>
    /// One candidate per defender or turret the reference raider can path to: the stand cell and the first wall or door
    /// on that shortest path (the same route vanilla sappers dig along). Open bases have no wall, so the candidate is a
    /// cell a few steps short of the target.
    /// </summary>
    public static List<BreachPoint> Find(
        Pawn reference, RimWorldTacticalMap map, IReadOnlyList<Pawn> defenders,
        IReadOnlyList<Building_Turret> turrets, IntVec3 centre, DangerMemory danger, long now, IntVec3? visionWall = null,
        IReadOnlyList<Pawn>? exposed = null)
    {
        var candidates = new List<BreachPoint>();
        if (!centre.IsValid) return candidates;
        // Colonists in the open come first: nothing stands between them and the raiders, so they are the best point to attack.
        var open = exposed ?? Array.Empty<Pawn>();
        var targets = open.Cast<Thing>().Take(MaxPathTargets)
            .Concat(defenders.Where(d => !open.Contains(d)).Cast<Thing>().Take(MaxPathTargets))
            .Concat(turrets.Take(MaxPathTargets));
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
            // An open way to a colonist outside the walls outranks every wall: no digging, and the colonist is cut off from the base.
            if (target is Pawn p && open.Contains(p) && s.WallX < 0) weakness += 1f;
            candidates.Add(new BreachPoint("", s.StandX, s.StandZ, weakness, sees, covered, s.WallX, s.WallZ, fell));
        }
        if (visionWall is { } hinted && VisionCandidate(reference, map, hinted) is { } extra)
        {
            var cell = new IntVec3(extra.StandX, 0, extra.StandZ);
            var sees = defenders.Count(d => d.Position.DistanceTo(cell) <= DefenderSightRange &&
                                            GenSight.LineOfSight(d.Position, cell, reference.Map));
            var covered = turrets.Count(t => cell.DistanceTo(t.Position) <= OperativeSampler.TurretRange(t) + 1f);
            var fell = danger.Penalty(extra.StandX, extra.StandZ, now);
            candidates.Add(new BreachPoint("", extra.StandX, extra.StandZ, BreachRoute.Weakness(sees, covered, fell), sees, covered,
                extra.WallX, extra.WallZ, fell));
            Log.Message($"[Adaptive Hostiles] Jev image candidate: wall ({extra.WallX},{extra.WallZ}), stand ({extra.StandX},{extra.StandZ}).");
        }
        var picked = BreachRoute.Pick(candidates, MaxPoints, MinSpacing);
        // Every path ran into the same wall: offer a second wall further along it, so part of the squad can flank there.
        if (picked.Count == 1 && picked[0].WallX >= 0 && AlternateCandidate(reference, map, defenders, turrets, picked[0], centre, danger, now) is { } alt)
            picked = BreachRoute.Pick(candidates.Append(alt).ToList(), MaxPoints, MinSpacing);
        return picked;
    }

    /// <summary>
    /// A second wall or door at least <see cref="MinSpacing"/> cells from the main one, within 16 cells of it, with a standable
    /// cell on the raiders' side (reachable from where they are), the one nearest the defenders. Null when there is none.
    /// </summary>
    private static BreachPoint? AlternateCandidate(
        Pawn reference, RimWorldTacticalMap map, IReadOnlyList<Pawn> defenders, IReadOnlyList<Building_Turret> turrets,
        BreachPoint main, IntVec3 centre, DangerMemory danger, long now)
    {
        var mainWall = new IntVec3(main.WallX, 0, main.WallZ);
        (IntVec3 Wall, IntVec3 Stand)? best = null;
        var bestScore = float.MaxValue;
        foreach (var c in GenRadial.RadialCellsAround(mainWall, 16f, false))
        {
            if (!c.InBounds(reference.Map) || Math.Abs(c.x - main.WallX) + Math.Abs(c.z - main.WallZ) < MinSpacing) continue;
            if (WallBreaker.BlockerAt(reference, c) is null) continue;
            var stand = GenAdj.CellsAdjacent8Way(new TargetInfo(c, reference.Map))
                .Where(s => map.IsStandable(s.x, s.z) && map.IsReachable(s.x, s.z))
                .OrderBy(s => s.DistanceToSquared(reference.Position)).Cast<IntVec3?>().FirstOrDefault();
            if (stand is not { } st) continue;
            var score = centre.IsValid ? st.DistanceToSquared(centre) : 0f;
            if (score >= bestScore) continue;
            best = (c, st);
            bestScore = score;
        }
        if (best is not { } b) return null;
        var sees = defenders.Count(d => d.Position.DistanceTo(b.Stand) <= DefenderSightRange && GenSight.LineOfSight(d.Position, b.Stand, reference.Map));
        var covered = turrets.Count(t => b.Stand.DistanceTo(t.Position) <= OperativeSampler.TurretRange(t) + 1f);
        var fell = danger.Penalty(b.Stand.x, b.Stand.z, now);
        Log.Message($"[Adaptive Hostiles] alternate breach candidate: wall ({b.Wall.x},{b.Wall.z}), stand ({b.Stand.x},{b.Stand.z}).");
        return new BreachPoint("", b.Stand.x, b.Stand.z, BreachRoute.Weakness(sees, covered, fell), sees, covered, b.Wall.x, b.Wall.z, fell);
    }

    // The wall or door nearest the cell Jev pointed at, with the raiders' side stand cell next to it; null when none is within reach.
    private static BreachSite? VisionCandidate(Pawn reference, RimWorldTacticalMap map, IntVec3 hint)
    {
        var found = GenRadial.RadialCellsAround(hint, 4f, true).Where(c => c.InBounds(reference.Map))
            .Cast<IntVec3?>().FirstOrDefault(c => WallBreaker.BlockerAt(reference, c!.Value) is not null);
        if (found is not { } wall) return null;
        var stand = GenAdj.CellsAdjacent8Way(new TargetInfo(wall, reference.Map))
            .Where(c => map.IsStandable(c.x, c.z) && map.IsReachable(c.x, c.z))
            .OrderBy(c => c.DistanceToSquared(reference.Position)).Cast<IntVec3?>().FirstOrDefault();
        return stand is { } s ? new BreachSite(s.x, s.z, wall.x, wall.z) : null;
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
                // A hop always closes in: it must lie at least 60% of the step nearer the role cell, so the cover it picks to the
                // side of the line still carries the raider forward instead of sideways or back.
                Func<int, int, bool> closer = (x, z) => Math.Sqrt(Math.Pow(x - rc.Item1, 2) + Math.Pow(z - rc.Item2, 2)) <= hlen - 0.6f * step;
                if (hlen > BreachRoute.HopTrigger)
                    Add(result, pawn, map, Pinned(pawn, map, "hop_" + bp.Id, () => FindCover(map, pawn.Position.x + (int)Math.Round(hx / hlen * step),
                        pawn.Position.z + (int)Math.Round(hz / hlen * step), slot, used, closer)), "hop_" + bp.Id, used);
            }
            // An open breach has no wall to break: melee also get a press cell past the opening toward the defenders, one per raider.
            // The press cell is measured from the raider itself once it is through, so the push keeps going each cycle until
            // the raiders stand on the defenders instead of idling a few cells past the opening. Ranged stop short of them.
            if (bp.WallX < 0 && centre.IsValid)
            {
                var pdx = centre.x - pawn.Position.x;
                var pdz = centre.z - pawn.Position.z;
                var bdx = centre.x - bp.X;
                var bdz = centre.z - bp.Z;
                var through = pdx * pdx + pdz * pdz < bdx * bdx + bdz * bdz;
                var ox = through ? pawn.Position.x : bp.X;
                var oz = through ? pawn.Position.z : bp.Z;
                var px = centre.x - ox;
                var pz = centre.z - oz;
                var plen = Math.Max(1f, (float)Math.Sqrt(px * px + pz * pz));
                var stop = isMelee ? 2f : 7f;
                var ahead = Math.Min((through ? 8f : 3f) + slot, Math.Max(2f, plen - stop));
                // Straight toward the defenders can land inside a sealed room the raider cannot reach; then the press cell is the
                // reachable cell nearest the defenders, so the raider still has somewhere to press to instead of idling at the breach.
                Add(result, pawn, map, Pinned(pawn, map, "press_" + bp.Id, () =>
                        FindCell(map, ox + (int)Math.Round(px / plen * ahead), oz + (int)Math.Round(pz / plen * ahead), slot, used, null) ??
                        FindCell(map, centre.x, centre.z, slot, used, null, maxRing: 16)),
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

    // Hop and press cells stay pinned to a raider until it gets there, so a one-second cadence does not make them drift each cycle.
    private static readonly Dictionary<string, (int X, int Z, int Tick)> Pins = new();
    private const int PinTicks = 600;

    private static (int, int)? Pinned(Pawn pawn, RimWorldTacticalMap map, string purpose, Func<(int, int)?> pick)
    {
        var key = pawn.ThingID + purpose;
        var now = Verse.Find.TickManager.TicksGame;
        if (Pins.TryGetValue(key, out var pin) && now - pin.Tick < PinTicks && map.IsStandable(pin.X, pin.Z) &&
            new IntVec3(pin.X, 0, pin.Z).DistanceTo(pawn.Position) > 2.5f)
            return (pin.X, pin.Z);
        var cell = pick();
        if (cell is { } c) Pins[key] = (c.Item1, c.Item2, now); else Pins.Remove(key);
        return cell;
    }

    public static void ResetPins() => Pins.Clear();

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
        RimWorldTacticalMap map, int ax, int az, int slot, HashSet<(int, int)> used, Func<int, int, bool>? allowed = null, int maxRing = 4)
    {
        for (var r = 0; r <= maxRing; r++)
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
