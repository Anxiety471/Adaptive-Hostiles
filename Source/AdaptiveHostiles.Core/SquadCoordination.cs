namespace AdaptiveHostiles.Core;

/// <summary>What the ranged raiders do while the melee push: keep firing from the support cells, or move up behind the melee.</summary>
public enum RangedRole { Cover, Follow }

/// <summary>Facts about how melee and ranged stand relative to each other, computed locally and shown to the model.</summary>
public sealed record CoordinationState(
    int Melee, int Ranged, int MeleeThrough, int RangedInPosition, int RangedFiring,
    double MeleeNearestToBreach, bool BreachOpen, int DefendersVisible, int BreachCycles)
{
    public bool HasBoth => Melee > 0 && Ranged > 0;

    /// <summary>At least half the melee are through the opening (or there is no melee to wait for).</summary>
    public bool MeleeIsThrough => Melee == 0 || MeleeThrough * 2 >= Melee;

    /// <summary>At least half the ranged stand at their support cells (or there is no ranged to wait for).</summary>
    public bool RangedIsReady => Ranged == 0 || RangedInPosition * 2 >= Ranged;
}

/// <summary>
/// Keeps ranged and melee in step. Jev answers two plain judgments (melee push timing and the ranged role); the rules that
/// turn them into orders live here: melee do not make the final dash before the ranged cover them, ranged keep the
/// defenders pinned while the melee close in, and ranged only move up through the opening once the melee are through it.
/// </summary>
public static class SquadCoordination
{
    /// <summary>A ranged raider this close to its support cell counts as in position.</summary>
    public const float InPositionWithin = 6f;
    /// <summary>Melee holding for cover stop this far short of the breach cell; closer than this they are committed and go on.</summary>
    public const float MeleeCommitWithin = 6f;
    /// <summary>Melee hold for cover for at most this many breach cycles (about two seconds each, about 12 s); then they go regardless.</summary>
    public const int MaxCoverWaitCycles = 6;
    /// <summary>A melee raider with an enemy pawn in sight this close is fighting and counts as through the opening.</summary>
    public const float EngagedWithin = 8f;

    public static CoordinationState Measure(SquadSnapshot squad, string? breachId)
    {
        int melee = 0, ranged = 0, through = 0, inPosition = 0, firing = 0;
        var nearest = double.MaxValue;
        var open = true;
        foreach (var m in squad.Members)
        {
            var id = m.GroupBreachId ?? breachId;
            var breachPoint = squad.Breaches.FirstOrDefault(b => b.Id == id);
            if (breachPoint is { WallX: >= 0 }) open = false;
            if (m.IsMelee)
            {
                melee++;
                var breach = Cell(m, "breach_", id);
                var press = Cell(m, "press_", id);
                if (breach is not null) nearest = Math.Min(nearest, Dist(m, breach.X, breach.Z));
                if (Engaged(m) ||
                    (press is not null && Dist(m, press.X, press.Z) <= SquadPlanner.PressStick) ||
                    (press is not null && breach is not null && Dist(m, breach.X, breach.Z) <= SquadPlanner.PressWithin))
                    through++;
            }
            else
            {
                ranged++;
                var support = Cell(m, "support_", id);
                if (support is null || Dist(m, support.X, support.Z) <= InPositionWithin) inPosition++;
                if (m.Snapshot.Targets.Any(t => t.Visible && t.Kind != "turret")) firing++;
            }
        }
        var defenders = squad.Members.Select(m => m.Snapshot.Targets.Count(t => t.Visible && t.Kind == "pawn")).DefaultIfEmpty(0).Max();
        return new CoordinationState(melee, ranged, through, inPosition, firing,
            nearest == double.MaxValue ? -1 : Math.Round(nearest, 1), open, defenders, squad.BreachCycles);
    }

    /// <summary>The rule used when the model does not answer: melee make the final dash only once the ranged are in position.</summary>
    public static bool LocalHoldForCover(CoordinationState s) => s.HasBoth && !s.RangedIsReady;

    /// <summary>
    /// Melee that are still short of the opening hold in their last cover while the decision asks for it (<see cref="SquadDecision.MeleeHold"/>), the ranged are not
    /// yet in position and the wait has not run out. A melee raider with an enemy at arm's length, a withdrawing one, or one
    /// already near the opening is never held.
    /// </summary>
    public static SquadDecision MeleeTiming(SquadDecision decision, SquadSnapshot squad)
    {
        if (!decision.MeleeHold || decision.Phase != SquadPhase.Breach || decision.BreachId is null) return decision;
        var state = Measure(squad, decision.BreachId);
        if (!state.HasBoth || state.RangedIsReady || state.BreachCycles >= MaxCoverWaitCycles || SquadCommitment.Losing(squad)) return decision;

        var members = new Dictionary<string, OperativeDecision>(decision.Members, StringComparer.Ordinal);
        foreach (var m in squad.Members.Where(m => m.IsMelee))
        {
            if (!members.TryGetValue(m.Snapshot.PawnId, out var d) || d.Stance is TacticalStance.Withdraw or TacticalStance.Flee) continue;
            var id = m.GroupBreachId ?? decision.BreachId;
            var breach = Cell(m, "breach_", id);
            if (breach is null || Dist(m, breach.X, breach.Z) <= MeleeCommitWithin) continue;
            if (m.Snapshot.Targets.Any(t => t.Visible && t.Kind != "turret" && Dist(m, t.X, t.Z) <= SquadCommitment.CorneredWithin)) continue;
            var dest = m.Snapshot.Destinations.FirstOrDefault(c => c.Id == d.DestinationId);
            if (dest is null || !(dest.Purpose.StartsWith("breach_", StringComparison.Ordinal) || dest.Purpose.StartsWith("press_", StringComparison.Ordinal))) continue;
            // Far out the cover hops still move the raider; only the last stretch is held.
            if (Dist(m, breach.X, breach.Z) > BreachRoute.HopTrigger) continue;
            members[m.Snapshot.PawnId] = d with { DestinationId = "hold", Stance = TacticalStance.Defensive };
        }
        return decision with { Members = members };
    }

    /// <summary>
    /// Covering fire: while the melee are not yet through, each ranged raider shoots what threatens the melee, the visible target
    /// nearest to the melee (a turret first, as before), so the defenders are kept busy during the advance. Once the melee are
    /// through, ranged keep their own targets.
    /// </summary>
    public static SquadDecision CoverFire(SquadDecision decision, SquadSnapshot squad)
    {
        if (decision.Phase == SquadPhase.Gather || decision.BreachId is null) return decision;
        var state = Measure(squad, decision.BreachId);
        if (!state.HasBoth || state.MeleeIsThrough) return decision;
        var melee = squad.Members.Where(m => m.IsMelee).ToList();
        var cx = melee.Average(m => (double)m.Snapshot.X);
        var cz = melee.Average(m => (double)m.Snapshot.Z);

        var members = new Dictionary<string, OperativeDecision>(decision.Members, StringComparer.Ordinal);
        foreach (var m in squad.Members.Where(m => !m.IsMelee))
        {
            if (!members.TryGetValue(m.Snapshot.PawnId, out var d) || d.Stance is TacticalStance.Withdraw or TacticalStance.Flee) continue;
            var visible = m.Snapshot.Targets.Where(t => t.Visible).ToList();
            if (visible.Count == 0 || visible.Any(t => t.Kind == "turret")) continue; // turret focus keeps priority
            if (visible.Any(t => Dist(m, t.X, t.Z) <= SquadPlanner.PointBlank)) continue; // point-blank shot stays
            var threat = visible.OrderBy(t => (t.X - cx) * (t.X - cx) + (t.Z - cz) * (t.Z - cz))
                .ThenByDescending(t => t.Threat).ThenBy(t => t.Id, StringComparer.Ordinal).First();
            members[m.Snapshot.PawnId] = d with { TargetId = threat.Id };
        }
        return decision with { Members = members };
    }

    /// <summary>A melee raider fighting: an enemy pawn in sight within <see cref="EngagedWithin"/> cells counts as through the opening, wall or no wall.</summary>
    private static bool Engaged(SquadMember m) =>
        m.Snapshot.Targets.Any(t => t.Visible && t.Kind != "turret" && Dist(m, t.X, t.Z) <= EngagedWithin);

    /// <summary>
    /// Ranged raiders do not wait outside while the melee fight inside: once the melee are through or engaged, a ranged raider that
    /// has nothing in sight moves to the firing or advance cell nearest the enemies the squad can see, so it goes in behind the
    /// melee and supports them. Sealed or open breach alike; raiders still gathering, withdrawing, or with a target in sight stay.
    /// </summary>
    public static SquadDecision FollowUp(SquadDecision decision, SquadSnapshot squad)
    {
        if (decision.Phase == SquadPhase.Gather || decision.BreachId is null) return decision;
        var state = Measure(squad, decision.BreachId);
        if (!state.HasBoth || !state.MeleeIsThrough || SquadCommitment.Losing(squad)) return decision;
        var enemies = squad.Targets.Where(t => t.Kind != "turret").ToList();
        if (enemies.Count == 0) return decision;

        var members = new Dictionary<string, OperativeDecision>(decision.Members, StringComparer.Ordinal);
        var slot = 0;
        foreach (var m in squad.Members.Where(m => !m.IsMelee))
        {
            if (!members.TryGetValue(m.Snapshot.PawnId, out var d) || d.Stance is TacticalStance.Withdraw or TacticalStance.Flee) continue;
            // Turrets are always listed as visible, so only an enemy pawn in sight keeps a ranged raider where it is.
            if (m.Snapshot.Targets.Any(t => t.Visible && t.Kind != "turret")) continue;
            var near = enemies.OrderBy(t => Dist(m, t.X, t.Z)).ThenBy(t => t.Id, StringComparer.Ordinal).First();
            var cells = m.Snapshot.Destinations
                .Where(c => c.Reachable && !c.InTurretRange && (c.Purpose == "firing_position" || c.Purpose == "advance"))
                .OrderBy(c => (c.X - near.X) * (c.X - near.X) + (c.Z - near.Z) * (c.Z - near.Z)).ThenBy(c => c.Id, StringComparer.Ordinal).ToList();
            if (cells.Count == 0 || cells[0].Id == d.DestinationId) continue;
            // Spread over the nearest few cells so the ranged do not stack on one.
            var pick = cells[Math.Min(slot++ % 3, cells.Count - 1)];
            members[m.Snapshot.PawnId] = d with { DestinationId = pick.Id, TargetId = near.Id, Stance = TacticalStance.Aggressive };
        }
        return decision with { Members = members };
    }

    private static MovementCandidate? Cell(SquadMember m, string prefix, string? id) =>
        id is null ? null : m.Snapshot.Destinations.FirstOrDefault(c => c.Reachable && c.Purpose == prefix + id);

    private static double Dist(SquadMember m, int x, int z) =>
        Math.Sqrt(Math.Pow(m.Snapshot.X - x, 2) + Math.Pow(m.Snapshot.Z - z, 2));
}
