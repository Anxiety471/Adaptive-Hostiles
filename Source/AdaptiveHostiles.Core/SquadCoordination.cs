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
        // MeleeHold on the returned decision means the hold is in effect, not just asked for (the intent log reads it).
        if (!state.HasBoth || state.RangedIsReady || state.BreachCycles >= MaxCoverWaitCycles || SquadCommitment.Losing(squad)) return decision with { MeleeHold = false };

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
        // With no melee to wait for (or none ranged), a ranged raider that sees nobody still goes looking for a firing angle.
        if ((state.HasBoth && !state.MeleeIsThrough) || SquadCommitment.Losing(squad)) return decision;
        var enemies = squad.Targets.Where(t => t.Kind != "turret").ToList();
        if (enemies.Count == 0) return decision;

        var members = new Dictionary<string, OperativeDecision>(decision.Members, StringComparer.Ordinal);
        var slot = 0;
        foreach (var m in squad.Members.Where(m => !m.IsMelee || (!state.HasBoth && CarriesRangedWeapon(m))))
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

    /// <summary>A melee enemy is treated as reaching this far, since it closes the last cells in a moment.</summary>
    public const float MeleeReach = 3f;
    /// <summary>A kite spot should lie at least this many cells outside every enemy's weapon range.</summary>
    public const float KiteMargin = 1.5f;
    public const float KiteMinMove = 4f;
    public const float KiteMaxMove = 12f;

    /// <summary>
    /// The cell a ranged raider steps to so it outranges the enemies near it: outside every visible enemy's weapon range and still
    /// within its own, a few cells away (up to half its range, 4 to 12 cells). Null when nothing is gained: it is already outside
    /// every enemy's reach, an enemy outranges it, or no offered cell is safer than where it stands.
    /// </summary>
    public static bool CarriesRangedWeapon(SquadMember m) =>
        m.Snapshot.WeaponRange > 2.5f || (m.Snapshot.WeaponRange <= 0f && !m.IsMelee);

    public static MovementCandidate? KiteCell(SquadMember m)
    {
        // A raider behind a shield belt shrugs off ranged fire: it presses in instead of backing off.
        if (m.Snapshot.HasShield) return null;
        var mine = m.Snapshot.WeaponRange > 0 ? m.Snapshot.WeaponRange : 20f;
        var threats = m.Snapshot.Targets.Where(t => t.Visible && t.Kind != "turret")
            .Select(t => (T: t, Reach: t.Range > 2f ? t.Range : MeleeReach)).ToList();
        if (threats.Count == 0) return null;
        if (threats.Any(e => e.Reach >= mine)) return null;

        double Margin(int x, int z) => threats.Min(e => Math.Sqrt(Math.Pow(x - e.T.X, 2) + Math.Pow(z - e.T.Z, 2)) - e.Reach);
        double Nearest(int x, int z) => threats.Min(e => Math.Sqrt(Math.Pow(x - e.T.X, 2) + Math.Pow(z - e.T.Z, 2)));
        var here = Margin(m.Snapshot.X, m.Snapshot.Z);
        if (here > KiteMargin) return null;

        var move = Math.Clamp(mine * 0.5f, KiteMinMove, KiteMaxMove);
        return m.Snapshot.Destinations
            .Where(c => c.Reachable && !c.InTurretRange && c.Purpose is "kite" or "cover" or "firing_position" or "retreat" &&
                        Math.Sqrt(Math.Pow(c.X - m.Snapshot.X, 2) + Math.Pow(c.Z - m.Snapshot.Z, 2)) <= move &&
                        Nearest(c.X, c.Z) <= mine && Margin(c.X, c.Z) > here + 1)
            .OrderByDescending(c => Math.Min(Margin(c.X, c.Z), KiteMargin * 3))
            .ThenBy(c => Math.Pow(c.X - m.Snapshot.X, 2) + Math.Pow(c.Z - m.Snapshot.Z, 2))
            .ThenBy(c => c.Id, StringComparer.Ordinal).FirstOrDefault();
    }

    /// <summary>
    /// The raider already outranges every enemy it sees: outside all their weapon reach and with one of them inside its own range.
    /// It then stays and shoots; any other order (advance, press, hop, support) would walk it back into their range just to kite out again.
    /// Returns the enemy to shoot, nearest first.
    /// </summary>
    public static TargetCandidate? OutrangingTarget(SquadMember m)
    {
        if (m.Snapshot.HasShield) return null;
        var mine = m.Snapshot.WeaponRange > 0 ? m.Snapshot.WeaponRange : 20f;
        var threats = m.Snapshot.Targets.Where(t => t.Visible && t.Kind != "turret")
            .Select(t => (T: t, Reach: t.Range > 2f ? t.Range : MeleeReach, Dist: Math.Sqrt(Math.Pow(m.Snapshot.X - t.X, 2) + Math.Pow(m.Snapshot.Z - t.Z, 2)))).ToList();
        if (threats.Count == 0 || m.Snapshot.Targets.Any(t => t.Visible && t.Kind != "turret" && t.Range <= 0f) || threats.Any(e => e.Reach >= mine) || threats.Any(e => e.Dist - e.Reach <= KiteMargin)) return null;
        return threats.Where(e => e.Dist <= mine).OrderBy(e => e.Dist).ThenBy(e => e.T.Id, StringComparer.Ordinal).Select(e => e.T).FirstOrDefault();
    }

    /// <summary>A colonist in sight this close is attacked at once, whatever the plan for the base says.</summary>
    public const float OpenEngageWithin = 30f;

    /// <summary>
    /// Melee raiders do not walk past colonists standing in the open to stage at a breach: any colonist in sight within
    /// <see cref="OpenEngageWithin"/> cells is the target, and the raider heads for the cell nearest it. Withdrawing raiders,
    /// kiting ones and a losing squad are left alone.
    /// </summary>
    public static SquadDecision EngageOpen(SquadDecision decision, SquadSnapshot squad)
    {
        if (SquadCommitment.Losing(squad)) return decision;
        var members = new Dictionary<string, OperativeDecision>(decision.Members, StringComparer.Ordinal);
        foreach (var m in squad.Members.Where(m => m.IsMelee && !CarriesRangedWeapon(m)))
        {
            if (!members.TryGetValue(m.Snapshot.PawnId, out var d) || d.Kite || d.Stance is TacticalStance.Withdraw or TacticalStance.Flee) continue;
            // Only a raider that is staging or wandering is redirected; one on its breach, press or hop cell, or holding for cover on purpose, keeps the plan.
            var purpose = m.Snapshot.Destinations.FirstOrDefault(c => c.Id == d.DestinationId)?.Purpose ?? "";
            if (d.DestinationId == "hold" || purpose.StartsWith("breach_", StringComparison.Ordinal) || purpose.StartsWith("press_", StringComparison.Ordinal) ||
                purpose.StartsWith("hop_", StringComparison.Ordinal) || purpose.StartsWith("support_", StringComparison.Ordinal)) continue;
            var near = m.Snapshot.Targets.Where(t => t.Visible && t.Kind == "pawn" && Dist(m, t.X, t.Z) <= OpenEngageWithin)
                .OrderBy(t => Dist(m, t.X, t.Z)).ThenBy(t => t.Id, StringComparer.Ordinal).FirstOrDefault();
            if (near is null) continue;
            var cell = m.Snapshot.Destinations
                .Where(c => c.Reachable && c.Purpose is "advance" or "firing_position" or "cover" or "press_" or "kite")
                .OrderBy(c => c.InTurretRange).ThenBy(c => (c.X - near.X) * (c.X - near.X) + (c.Z - near.Z) * (c.Z - near.Z))
                .ThenBy(c => c.Id, StringComparer.Ordinal).FirstOrDefault();
            members[m.Snapshot.PawnId] = d with { DestinationId = cell?.Id ?? "hold", TargetId = near.Id, Stance = TacticalStance.Aggressive };
        }
        return decision with { Members = members };
    }

    /// <summary>A squad needs at least this many raiders before a single opening limits who goes through.</summary>
    public const int FunnelMinSquad = 5;
    public const int FunnelDefault = 3;

    /// <summary>
    /// A building with one opening: only a small group goes through it (melee first, a ranged raider or two to cover the door); the
    /// rest do not queue behind them in the defenders' fire but hold their own spots, the least exposed cover or firing cell nearby,
    /// shooting what they can see. The group is the raiders nearest the opening each cycle, so a fallen one is replaced from the holders.
    /// Does not apply while the squad is losing, while it gathers, or to a raider kiting or splitting off to another breach.
    /// </summary>
    public static SquadDecision Funnel(SquadDecision decision, SquadSnapshot squad)
    {
        if (squad.Openings != 1 || squad.Members.Count < FunnelMinSquad || decision.FunnelSize < 0) return decision;
        if (decision.Phase == SquadPhase.Gather || decision.BreachId is null || SquadCommitment.Losing(squad)) return decision;
        if (squad.Breaches.FirstOrDefault(b => b.Id == decision.BreachId) is not { WallX: < 0 }) return decision;

        var size = decision.FunnelSize > 0 ? decision.FunnelSize : FunnelDefault;
        var coverSlots = size >= 3 ? Math.Max(1, size / 3) : 0;
        double ToOpening(SquadMember m)
        {
            var c = Cell(m, "breach_", decision.BreachId);
            return c is null ? double.MaxValue : Dist(m, c.X, c.Z);
        }
        var candidates = squad.Members.Where(m => m.GroupBreachId is null &&
            decision.Members.TryGetValue(m.Snapshot.PawnId, out var d) && d.Stance is not (TacticalStance.Withdraw or TacticalStance.Flee)).ToList();
        var group = candidates.Where(m => m.IsMelee).OrderBy(ToOpening).ThenBy(m => m.Snapshot.PawnId, StringComparer.Ordinal).Take(size - coverSlots)
            .Concat(candidates.Where(m => !m.IsMelee).OrderBy(ToOpening).ThenBy(m => m.Snapshot.PawnId, StringComparer.Ordinal).Take(Math.Max(coverSlots, 0)))
            .Select(m => m.Snapshot.PawnId).ToHashSet(StringComparer.Ordinal);
        // A group short of melee is made up with the nearest others, so a ranged-only squad still sends someone.
        foreach (var m in candidates.OrderBy(ToOpening).ThenBy(m => m.Snapshot.PawnId, StringComparer.Ordinal))
        {
            if (group.Count >= size) break;
            group.Add(m.Snapshot.PawnId);
        }

        var members = new Dictionary<string, OperativeDecision>(decision.Members, StringComparer.Ordinal);
        foreach (var m in candidates)
        {
            if (group.Contains(m.Snapshot.PawnId)) continue;
            var d = members[m.Snapshot.PawnId];
            if (d.Kite) continue;
            var spot = m.Snapshot.Destinations
                .Where(c => c.Reachable && !c.InTurretRange && c.Purpose is "cover" or "firing_position")
                .OrderBy(c => c.Exposure).ThenBy(c => c.Distance).ThenBy(c => c.Id, StringComparer.Ordinal).FirstOrDefault();
            if (spot is null) continue;
            // Holders keep shooting what they see; melee holders wait in their spot instead of running into the door.
            members[m.Snapshot.PawnId] = d with { DestinationId = spot.Id, Stance = m.IsMelee ? TacticalStance.Defensive : TacticalStance.Aggressive };
        }
        return decision with { Members = members, FunnelSize = size };
    }

    private static MovementCandidate? Cell(SquadMember m, string prefix, string? id) =>
        id is null ? null : m.Snapshot.Destinations.FirstOrDefault(c => c.Reachable && c.Purpose == prefix + id);

    private static double Dist(SquadMember m, int x, int z) =>
        Math.Sqrt(Math.Pow(m.Snapshot.X - x, 2) + Math.Pow(m.Snapshot.Z - z, 2));
}
