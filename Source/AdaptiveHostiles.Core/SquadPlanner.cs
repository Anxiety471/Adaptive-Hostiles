using System.Net.Http;
using System.Text.Json;

namespace AdaptiveHostiles.Core;

public enum SquadTactic { FocusFire, Flank, SuppressAndPush, Regroup }

/// <summary>One controllable raider as sampled on the game thread; the Operative is flagged.</summary>
public sealed record SquadMember(OperativeSnapshot Snapshot, bool IsOperative);

public sealed record SquadSnapshot(
    string SquadId, IReadOnlyList<SquadMember> Members, int Sent, int Down, int Retreated)
{
    public double LossFraction => Sent <= 0 ? 0 : (Down + Retreated) / (double)Sent;

    /// <summary>Union of what members can see, strongest threat per target.</summary>
    public IReadOnlyList<TargetCandidate> Targets => Members
        .SelectMany(m => m.Snapshot.Targets).Where(t => t.Visible)
        .GroupBy(t => t.Id, StringComparer.Ordinal)
        .Select(g => g.OrderByDescending(t => t.Threat).First())
        .OrderByDescending(t => t.Threat).ThenBy(t => t.Id, StringComparer.Ordinal)
        .ToList();
}

/// <summary>Coordinated orders for a whole squad. Members map to the offered snapshots by pawn id.</summary>
public sealed record SquadDecision(
    SquadTactic Tactic, string? FocusTargetId,
    IReadOnlyDictionary<string, OperativeDecision> Members, bool IsModelDecision);

/// <summary>
/// One model call per squad per cycle. Jev sees every member's exact candidate cells and answers a
/// squad tactic, one focus target and a move and stance for each member, so flanks, suppression
/// and focus fire are coordinated. Invalid answers fall back per member to local heuristics.
/// </summary>
public sealed class SquadPlanner(IJevClient? client = null)
{
    public const int MaxMembers = 12;
    public const int MaxDestinationsPerMember = 12;
    public const int MaxTargets = 16;

    public async Task<SquadDecision> DecideAsync(SquadSnapshot squad, CancellationToken cancellationToken = default)
    {
        var members = squad.Members
            .OrderByDescending(m => m.IsOperative)
            .Take(MaxMembers)
            .Select(m => new Entry(m, m.Snapshot.Destinations
                .Where(c => c.Reachable && c.Id != "hold" && !string.IsNullOrWhiteSpace(c.Id))
                .GroupBy(c => c.Id, StringComparer.Ordinal).Select(g => g.First())
                .Take(MaxDestinationsPerMember).ToDictionary(c => c.Id, StringComparer.Ordinal)))
            .ToList();
        var targets = squad.Targets.Where(t => t.Id != "none" && !string.IsNullOrWhiteSpace(t.Id))
            .Take(MaxTargets - 1).ToDictionary(t => t.Id, StringComparer.Ordinal);

        var fallback = Fallback(squad, members, targets);
        if (client is null || members.Count == 0) return fallback;

        var questions = new Dictionary<string, object>
        {
            ["tactic"] = new
            {
                type = "choice",
                instructions = "Pick the squad's overall plan. The squad is aggressive and wants to destroy the colonists, but each raider also wants to stay alive.",
                criteria = new Dictionary<string, string>
                {
                    ["focus_fire"] = "Everyone concentrates on one target.",
                    ["flank"] = "Some raiders pin the defenders while others circle to a flank.",
                    ["suppress_push"] = "Part of the squad suppresses from cover while the rest advance.",
                    ["regroup"] = "Pull back together to reorganise; only if the attack is failing."
                }
            },
            ["focus"] = new
            {
                type = "choice",
                instructions = "Choose the one visible colonist the squad should focus fire on, or none.",
                criteria = TargetOptions(targets)
            }
        };
        for (var i = 0; i < members.Count; i++)
        {
            var m = members[i];
            var role = m.Member.IsOperative ? "the elite Operative, who never flees" : "a raider";
            questions[$"move_{i}"] = new
            {
                type = "choice",
                instructions = $"Destination for member {i} ({role}). Coordinate with the others: spread over different approaches, do not share a cell, use cover, and match the squad plan. Only select a listed ID.",
                criteria = MoveOptions(m.Moves)
            };
            var stances = new Dictionary<string, string>
            {
                ["aggressive"] = "Press the attack.",
                ["defensive"] = "Hold cover and avoid exposure for now.",
                ["withdraw"] = "Pull back toward safety to survive."
            };
            if (!m.Member.IsOperative)
                stances["flee"] = "Leave the map; only when the fight is lost for this raider.";
            questions[$"stance_{i}"] = new
            {
                type = "choice",
                instructions = $"Stance for member {i} ({role}, health {m.Member.Snapshot.HealthFraction:0.00}). Almost always aggressive. Withdraw only if health is below {TacticalHeuristics.GuardHealth:0.00} or the squad is clearly being destroyed; flee only if nearly dead and the raid is lost. Healthy members must stay aggressive.",
                criteria = stances
            };
        }

        try
        {
            var state = new
            {
                squad.Sent, squad.Down, squad.Retreated,
                members = members.Select((m, i) => new
                {
                    index = i, isOperative = m.Member.IsOperative,
                    m.Member.Snapshot.X, m.Member.Snapshot.Z,
                    health = m.Member.Snapshot.HealthFraction, weapon = m.Member.Snapshot.EquippedWeapon,
                    destinations = m.Moves.Values.ToArray()
                }).ToArray(),
                targets = targets.Values.ToArray()
            };
            using var result = await client.DecideAsync(state, questions, cancellationToken).ConfigureAwait(false);
            var root = result.RootElement;

            var tactic = JevAnswers.Choice(root, "tactic") switch
            {
                "focus_fire" => SquadTactic.FocusFire,
                "flank" => SquadTactic.Flank,
                "suppress_push" => SquadTactic.SuppressAndPush,
                "regroup" => SquadTactic.Regroup,
                _ => (SquadTactic)(-1)
            };
            var focus = JevAnswers.Choice(root, "focus");
            if (!Enum.IsDefined(tactic) || focus is null || (focus != "none" && !targets.ContainsKey(focus)))
                return fallback;
            var focusId = focus == "none" ? null : focus;

            var decisions = new Dictionary<string, OperativeDecision>(StringComparer.Ordinal);
            var taken = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < members.Count; i++)
            {
                var m = members[i];
                var id = m.Member.Snapshot.PawnId;
                var move = JevAnswers.Choice(root, $"move_{i}");
                var stance = TacticalHeuristics.Guard(
                    ParseStance(JevAnswers.Choice(root, $"stance_{i}"), m.Member.IsOperative),
                    m.Member.Snapshot.HealthFraction, squad.LossFraction);
                var valid = move is not null && stance is not null &&
                            (move == "hold" || (m.Moves.ContainsKey(move) && !taken.Contains(move)));
                if (valid && move != "hold") taken.Add(move!);
                var chosen = valid
                    ? new OperativeDecision(move!, TacticalHeuristics.PickTarget(m.Member.Snapshot.Targets, focusId), stance!.Value, true)
                    : fallback.Members[id];
                if (!valid && chosen.DestinationId != "hold") taken.Add(chosen.DestinationId);
                decisions[id] = chosen;
            }
            return new SquadDecision(tactic, focusId, decisions, true);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return fallback; }
        catch (HttpRequestException) { return fallback; }
        catch (JsonException) { return fallback; }
    }

    private static TacticalStance? ParseStance(string? s, bool isOperative) => s switch
    {
        "aggressive" => TacticalStance.Aggressive,
        "defensive" => TacticalStance.Defensive,
        "withdraw" => TacticalStance.Withdraw,
        "flee" when !isOperative => TacticalStance.Flee,
        _ => null
    };

    private static Dictionary<string, string> TargetOptions(IReadOnlyDictionary<string, TargetCandidate> targets)
    {
        var o = new Dictionary<string, string> { ["none"] = "No focus target." };
        foreach (var t in targets.Values)
            o[t.Id] = $"Visible colonist at ({t.X},{t.Z}), threat {t.Threat:0.00}.";
        return o;
    }

    private static Dictionary<string, string> MoveOptions(IReadOnlyDictionary<string, MovementCandidate> moves)
    {
        var o = new Dictionary<string, string> { ["hold"] = "Remain at the current cell." };
        foreach (var c in moves.Values)
            o[c.Id] = $"Go to cell ({c.X},{c.Z}), {c.Purpose}; cover {c.Cover:0.00}, exposure {c.Exposure:0.00}, distance {c.Distance:0.0}.";
        return o;
    }

    private static SquadDecision Fallback(
        SquadSnapshot squad, IReadOnlyList<Entry> members, IReadOnlyDictionary<string, TargetCandidate> targets)
    {
        var focus = TacticalHeuristics.PickTarget(targets.Values.ToList(), null);
        var decisions = new Dictionary<string, OperativeDecision>(StringComparer.Ordinal);
        for (var i = 0; i < members.Count; i++)
        {
            var m = members[i];
            decisions[m.Member.Snapshot.PawnId] = TacticalHeuristics.Decide(
                m.Moves.Values.ToList(), m.Member.Snapshot.Targets, m.Member.Snapshot.HealthFraction,
                slot: i, focus, canFlee: !m.Member.IsOperative, lossFraction: squad.LossFraction);
        }
        return new SquadDecision(SquadTactic.FocusFire, focus, decisions, false);
    }

    private sealed record Entry(SquadMember Member, Dictionary<string, MovementCandidate> Moves);
}
