namespace AdaptiveHostiles.Core;

/// <summary>
/// Decides whether the squad presses the attack or pulls back, from plain conditions instead of leaving it to the model.
/// The squad is losing only when it has lost at least <see cref="LosingLossFraction"/> of its raiders and has fewer
/// healthy raiders left than the colonists it can see. Otherwise it presses: healthy raiders never walk away, a Regroup
/// plan is dropped, and a wounded raider with an enemy at arm's length keeps fighting instead of leaving.
/// </summary>
public static class SquadCommitment
{
    public const double LosingLossFraction = 0.5;
    public const float HealthyAbove = 0.5f;
    /// <summary>A wounded raider with an enemy this close fights on; running from there only gets it shot.</summary>
    public const float CorneredWithin = 5f;

    public static bool Losing(SquadSnapshot squad)
    {
        if (squad.LossFraction < LosingLossFraction) return false;
        var healthy = squad.Members.Count(m => m.Snapshot.HealthFraction >= HealthyAbove);
        var defenders = squad.Members.Select(m => m.Snapshot.Targets.Count(t => t.Visible && t.Kind == "pawn")).DefaultIfEmpty(0).Max();
        return healthy < defenders;
    }

    public static SquadDecision Apply(SquadDecision decision, SquadSnapshot squad)
    {
        if (decision.BreachId is null || decision.Phase == SquadPhase.Gather || Losing(squad)) return decision;
        var members = new Dictionary<string, OperativeDecision>(decision.Members, StringComparer.Ordinal);
        foreach (var m in squad.Members)
        {
            if (!members.TryGetValue(m.Snapshot.PawnId, out var d)) continue;
            var id = m.GroupBreachId ?? decision.BreachId;
            var dest = m.Snapshot.Destinations.FirstOrDefault(c => c.Id == d.DestinationId);
            var role = dest?.Purpose ?? "";
            var onRole = role.StartsWith("breach_", StringComparison.Ordinal) || role.StartsWith("support_", StringComparison.Ordinal) ||
                         role.StartsWith("press_", StringComparison.Ordinal) || role.StartsWith("hop_", StringComparison.Ordinal);
            var withdrawing = d.Stance is TacticalStance.Withdraw or TacticalStance.Flee;
            var cornered = m.Snapshot.Targets.Any(t => t.Visible && t.Kind != "turret" &&
                Math.Sqrt(Math.Pow(m.Snapshot.X - t.X, 2) + Math.Pow(m.Snapshot.Z - t.Z, 2)) <= CorneredWithin);
            if (withdrawing && !cornered) continue; // a wounded raider away from the fight may still pull back
            if (onRole && !withdrawing) continue;

            string[] order = m.IsMelee ? ["press_", "breach_"] : ["support_", "press_"];
            var cell = order.Select(p => m.Snapshot.Destinations.FirstOrDefault(c => c.Reachable && c.Purpose == p + id))
                .FirstOrDefault(c => c is not null);
            if (cell is null) continue;
            members[m.Snapshot.PawnId] = d with { DestinationId = cell.Id, Stance = TacticalStance.Aggressive };
        }
        return decision with { Tactic = decision.Tactic == SquadTactic.Regroup ? SquadTactic.SuppressAndPush : decision.Tactic, Members = members };
    }
}
