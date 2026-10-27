namespace AdaptiveHostiles.Core;

/// <summary>
/// Local decision rules used when no model answers: press the attack from good cover, spread
/// squad members over different approaches, pull back when badly hurt. Deterministic.
/// </summary>
public static class TacticalHeuristics
{
    /// <summary>Score cost of standing where a hostile turret can shoot.</summary>
    public const float TurretPenalty = 0.5f;
    /// <summary>Score cost of a cell where raiders recently fell (scaled by its 0..1 danger).</summary>
    public const float DangerPenalty = 0.8f;
    public const float WoundedBelow = 0.35f;
    public const float FleeBelow = 0.2f;
    public const double FleeLossFraction = 0.5;

    private static readonly string[] LeadOrder = { "firing_position", "advance", "cover", "flank", "retreat" };
    private static readonly string[] FlankOrder = { "flank", "firing_position", "advance", "cover", "retreat" };
    private static readonly string[] SafeOrder = { "retreat", "cover" };
    /// <summary>A badly hurt raider that withdrew keeps withdrawing until it is back above this health.</summary>
    public const float WithdrawUntil = GuardHealth;
    /// <summary>An enemy below this health is finished off before the focus target.</summary>
    public const float FinishBelow = 0.35f;

    public static OperativeDecision Decide(
        IReadOnlyList<MovementCandidate> moves, IReadOnlyList<TargetCandidate> targets,
        float health, int slot, string? focusTargetId = null, bool canFlee = false, double lossFraction = 0,
        string? breachId = null, bool isMelee = false, bool preferTurrets = false)
    {
        var stance = StanceFor(health, canFlee, lossFraction);
        var dest = stance == TacticalStance.Flee ? null : PickDestination(moves, stance, slot, breachId, isMelee);
        return new OperativeDecision(dest?.Id ?? "hold", PickTarget(targets, focusTargetId, preferTurrets), stance, false);
    }

    /// <summary>Below this health a model-ordered withdraw or flee is believed.</summary>
    public const float GuardHealth = 0.5f;
    public const double GuardLossFraction = 0.5;

    /// <summary>
    /// Keeps the squad aggressive: a model withdraw or flee for a healthy raider in a squad that is
    /// not losing is downgraded to aggressive; flee needs health under the flee line or a lost raid.
    /// </summary>
    public static TacticalStance? Guard(TacticalStance? stance, float health, double lossFraction)
    {
        if (stance is not (TacticalStance.Withdraw or TacticalStance.Flee)) return stance;
        if (health >= GuardHealth && lossFraction < GuardLossFraction) return TacticalStance.Aggressive;
        // Fleeing means leaving the map for good: only a nearly dead raider in a raid that is being destroyed.
        if (stance == TacticalStance.Flee && !(health < FleeBelow && lossFraction >= GuardLossFraction))
            return TacticalStance.Withdraw;
        return stance;
    }

    public static TacticalStance StanceFor(float health, bool canFlee, double lossFraction)
    {
        if (canFlee && health < FleeBelow && lossFraction >= FleeLossFraction) return TacticalStance.Flee;
        return health < WoundedBelow ? TacticalStance.Withdraw : TacticalStance.Aggressive;
    }

    public static MovementCandidate? PickDestination(
        IReadOnlyList<MovementCandidate> moves, TacticalStance stance, int slot,
        string? breachId = null, bool isMelee = false)
    {
        var usable = moves.Where(c => c.Reachable).ToList();
        if (usable.Count == 0) return null;
        if (stance == TacticalStance.Withdraw)
        {
            // Only safe cells: never the breach, support or any other attack cell. No safe cell means hold.
            foreach (var purpose in SafeOrder)
            {
                var safe = Best(usable.Where(c => c.Purpose == purpose), slot);
                if (safe is not null) return safe;
            }
            return Best(usable.Where(c => c.Purpose.StartsWith("stage_", StringComparison.Ordinal)), slot);
        }
        if (breachId is not null)
        {
            // Role cells keep the squad in sync: melee push the breach, ranged back them up behind.
            var role = (isMelee ? "breach_" : "support_") + breachId;
            var cell = Best(usable.Where(c => c.Purpose == role), slot);
            if (cell is not null) return cell;
        }
        var order = slot % 2 == 0 ? LeadOrder : FlankOrder;
        foreach (var purpose in order)
        {
            var best = Best(usable.Where(c => c.Purpose == purpose), slot);
            if (best is not null) return best;
        }
        return Best(usable, slot);
    }

    // Slot offsets the pick so members with the same preference don't stack on one cell.
    private static MovementCandidate? Best(IEnumerable<MovementCandidate> candidates, int slot)
    {
        var ranked = candidates
            .OrderByDescending(c => c.Cover - c.Exposure - (c.InTurretRange ? TurretPenalty : 0) - c.Danger * DangerPenalty)
            .ThenBy(c => c.Distance)
            .ThenBy(c => c.Id, StringComparer.Ordinal)
            .ToList();
        return ranked.Count == 0 ? null : ranked[Math.Min(slot / 2, ranked.Count - 1)];
    }

    /// <summary>
    /// The focus target when visible, else the strongest threat. Ranged raiders prefer turrets: shooting them from range
    /// lowers the colony's fire and makes room for the melee push.
    /// </summary>
    public static string? PickTarget(IReadOnlyList<TargetCandidate> targets, string? focusTargetId, bool preferTurrets = false)
    {
        var visible = targets.Where(t => t.Visible).ToList();
        if (preferTurrets)
        {
            var turrets = visible.Where(t => t.Kind == "turret").ToList();
            if (turrets.Count > 0)
            {
                if (focusTargetId is not null && turrets.Any(t => t.Id == focusTargetId)) return focusTargetId;
                return turrets.OrderByDescending(t => t.Threat).ThenBy(t => t.Id, StringComparer.Ordinal).First().Id;
            }
        }
        // A badly hurt enemy is finished before the focus moves on.
        var weak = visible.Where(t => t.Kind != "turret" && t.Health < FinishBelow).OrderBy(t => t.Health).ThenBy(t => t.Id, StringComparer.Ordinal).FirstOrDefault();
        if (weak is not null) return weak.Id;
        if (focusTargetId is not null && visible.Any(t => t.Id == focusTargetId)) return focusTargetId;
        return visible.OrderByDescending(t => t.Threat).ThenBy(t => t.Id, StringComparer.Ordinal)
            .FirstOrDefault()?.Id;
    }
}
