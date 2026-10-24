namespace AdaptiveHostiles.Core;

public enum OrderKind
{
    /// <summary>Pawn is in a state we must not interrupt; issue nothing.</summary>
    None,
    /// <summary>Stay put under the chosen stance.</summary>
    Hold,
    /// <summary>Go to the Jev-chosen, revalidated cell.</summary>
    MoveTo,
    /// <summary>Leave the map. Only issued to raiders that are allowed to flee.</summary>
    Flee,
    /// <summary>Decision unusable; let vanilla AI / safe movement take over.</summary>
    VanillaFallback
}

public sealed record OperativeOrder(
    OrderKind Kind, int X, int Z, string? TargetId, TacticalStance Stance, string Reason);

/// <summary>
/// Last gate before native jobs: the decision's destination ID is mapped back to the exact
/// candidate originally offered to Jev, then rechecked against the map as it is now.
/// </summary>
public static class OperativeOrderResolver
{
    public static OperativeOrder Resolve(
        OperativeDecision decision,
        OperativeSnapshot offered,
        ITacticalMap currentMap,
        PawnBlock block,
        OperativeRecord? record = null,
        long tick = 0,
        bool canFlee = true)
    {
        if (block != PawnBlock.None)
            return new OperativeOrder(OrderKind.None, offered.X, offered.Z, null, decision.Stance, $"blocked: {block}");

        if (decision.Stance == TacticalStance.Flee)
        {
            if (!canFlee) decision = decision with { Stance = TacticalStance.Withdraw };
            else
            {
                record?.Append(new DecisionLogEntry(
                    tick, "flee", offered.X, offered.Z, null, TacticalStance.Flee, decision.IsModelDecision));
                return new OperativeOrder(OrderKind.Flee, offered.X, offered.Z, null, TacticalStance.Flee, "flee");
            }
        }

        var targetId = decision.TargetId is not null &&
            offered.Targets.Any(t => t.Visible && t.Id == decision.TargetId)
            ? decision.TargetId : null;

        OperativeOrder order;
        if (decision.DestinationId == "hold")
        {
            order = new OperativeOrder(OrderKind.Hold, offered.X, offered.Z, targetId, decision.Stance, "hold");
        }
        else
        {
            var cell = offered.Destinations.FirstOrDefault(c => c.Reachable && c.Id == decision.DestinationId);
            if (cell is null)
                return new OperativeOrder(OrderKind.VanillaFallback, offered.X, offered.Z, targetId,
                    decision.Stance, "destination was not an offered candidate");
            if (!currentMap.IsStandable(cell.X, cell.Z) || !currentMap.IsReachable(cell.X, cell.Z))
                return new OperativeOrder(OrderKind.VanillaFallback, offered.X, offered.Z, targetId,
                    decision.Stance, "destination no longer reachable");
            order = new OperativeOrder(OrderKind.MoveTo, cell.X, cell.Z, targetId, decision.Stance, cell.Purpose);
        }

        record?.Append(new DecisionLogEntry(
            tick, decision.DestinationId, order.X, order.Z, order.TargetId, order.Stance, decision.IsModelDecision));
        return order;
    }
}
