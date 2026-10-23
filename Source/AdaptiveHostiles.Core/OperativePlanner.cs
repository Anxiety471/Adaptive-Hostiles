using System.Net.Http;
using System.Text.Json;

namespace AdaptiveHostiles.Core;

/// <summary>
/// Jev chooses an exact move destination by ID from known reachable cells.
/// Never accept coordinates or target IDs that were not in the local candidate set.
/// </summary>
public sealed class OperativePlanner(IJevClient? client = null)
{
    public const int MaxDestinations = 32;
    public const int MaxTargets = 16;

    public async Task<OperativeDecision> DecideAsync(
        OperativeSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        var moves = snapshot.Destinations
            .Where(c => c.Reachable && c.Id != "hold" && !string.IsNullOrWhiteSpace(c.Id))
            .GroupBy(c => c.Id, StringComparer.Ordinal)
            .Select(g => g.First())
            .Take(MaxDestinations - 1)
            .ToDictionary(c => c.Id, StringComparer.Ordinal);
        var targets = snapshot.Targets
            .Where(t => t.Visible && t.Id != "none" && !string.IsNullOrWhiteSpace(t.Id))
            .GroupBy(t => t.Id, StringComparer.Ordinal)
            .Select(g => g.First())
            .Take(MaxTargets - 1)
            .ToDictionary(t => t.Id, StringComparer.Ordinal);

        var fallback = Fallback(moves, targets);
        if (client is null)
            return fallback;

        var moveOptions = new Dictionary<string, string> { ["hold"] = "Remain at the current cell." };
        foreach (var candidate in moves.Values)
            moveOptions[candidate.Id] = $"Go to cell ({candidate.X},{candidate.Z}), {candidate.Purpose}; cover {candidate.Cover:0.00}, exposure {candidate.Exposure:0.00}, distance {candidate.Distance:0.0}.";

        var targetOptions = new Dictionary<string, string> { ["none"] = "No target; avoid firing." };
        foreach (var target in targets.Values)
            targetOptions[target.Id] = $"Visible enemy at ({target.X},{target.Z}), threat {target.Threat:0.00}.";

        var questions = new Dictionary<string, object>
        {
            ["movement"] = new
            {
                type = "choice",
                instructions = "Choose the exact safest tactically useful destination for this Operative now. Only select one listed destination ID, considering cover, exposure, distance, weapon and threats.",
                criteria = moveOptions
            },
            ["target"] = new
            {
                type = "choice",
                instructions = "Choose the best visible hostile target for this Operative, or none if no shot should be taken now.",
                criteria = targetOptions
            },
            ["stance"] = new
            {
                type = "choice",
                instructions = "Should the Operative press the attack, defend cautiously, or withdraw given this battlefield state?",
                criteria = new Dictionary<string, string>
                {
                    ["defensive"] = "Keep cover and avoid unnecessary exposure.",
                    ["aggressive"] = "Press an attack from a favorable position.",
                    ["withdraw"] = "Disengage toward a safer position."
                }
            }
        };

        try
        {
            // Anonymous state omits filtered-out invalid candidates and keeps the
            // question criteria and state consistent.
            var state = new
            {
                snapshot.PawnId,
                snapshot.X,
                snapshot.Z,
                snapshot.HealthFraction,
                snapshot.EquippedWeapon,
                snapshot.NearbyAllies,
                destinations = moves.Values.ToArray(),
                targets = targets.Values.ToArray()
            };
            using var result = await client.DecideAsync(state, questions, cancellationToken)
                .ConfigureAwait(false);
            var move = JevAnswers.Choice(result.RootElement, "movement");
            var target = JevAnswers.Choice(result.RootElement, "target");
            var stance = JevAnswers.Choice(result.RootElement, "stance");

            if (move is null || (move != "hold" && !moves.ContainsKey(move)) ||
                target is null || (target != "none" && !targets.ContainsKey(target)))
                return fallback;
            var parsedStance = stance switch
            {
                "defensive" => TacticalStance.Defensive,
                "aggressive" => TacticalStance.Aggressive,
                "withdraw" => TacticalStance.Withdraw,
                _ => (TacticalStance)(-1)
            };
            if (!Enum.IsDefined(parsedStance))
                return fallback;

            return new OperativeDecision(move, target == "none" ? null : target, parsedStance, true);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return fallback;
        }
        catch (HttpRequestException) { return fallback; }
        catch (JsonException) { return fallback; }
    }

    private static OperativeDecision Fallback(
        IReadOnlyDictionary<string, MovementCandidate> moves,
        IReadOnlyDictionary<string, TargetCandidate> targets)
    {
        var bestMove = moves.Values
            .OrderByDescending(c => c.Cover - c.Exposure)
            .ThenBy(c => c.Distance)
            .FirstOrDefault();
        var bestTarget = targets.Values.OrderByDescending(t => t.Threat).FirstOrDefault();
        return new OperativeDecision(bestMove?.Id ?? "hold", bestTarget?.Id, TacticalStance.Defensive, false);
    }
}
