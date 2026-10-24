using System.Net.Http;
using System.Text.Json;

namespace AdaptiveHostiles.Core;

public enum SquadTactic { FocusFire, Flank, SuppressAndPush, Regroup }

/// <summary>One controllable raider as sampled on the game thread; the Operative is flagged.</summary>
public sealed record SquadMember(OperativeSnapshot Snapshot, bool IsOperative, bool IsMelee = false);

/// <summary>
/// A place the colony is weakly defended, scored locally so Jev picks where to break through
/// instead of walking into the open kill zone. Weakness is 0..1, higher is easier to break.
/// </summary>
public sealed record BreachPoint(string Id, int X, int Z, float Weakness, int Defenders, int Turrets);

public sealed record SquadSnapshot(
    string SquadId, IReadOnlyList<SquadMember> Members, int Sent, int Down, int Retreated,
    IReadOnlyList<BreachPoint>? BreachPoints = null)
{
    public IReadOnlyList<BreachPoint> Breaches => BreachPoints ?? Array.Empty<BreachPoint>();

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
    IReadOnlyDictionary<string, OperativeDecision> Members, bool IsModelDecision,
    string? BreachId = null);

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
        if (squad.Breaches.Count > 0)
            questions["breach"] = new
            {                type = "choice",
                instructions = "Pick where the whole squad breaks through. Choose the weakest point of the colony's defence (fewest defenders and turrets covering it), never the open kill zone in front of the defenders. Melee raiders will push this point and ranged raiders will support it from behind, together.",
                criteria = BreachOptions(squad.Breaches)
            };

        for (var i = 0; i < members.Count; i++)
        {
            var m = members[i];
            var role = (m.Member.IsOperative ? "the elite Operative, who never flees" : "a raider") + (m.Member.IsMelee ? ", MELEE" : ", RANGED");
            questions[$"move_{i}"] = new
            {
                type = "choice",
                instructions = $"Destination for member {i} ({role}). Coordinate with the others: melee members take the chosen breach point's breach_ cell and ranged members its support_ cell, so melee push in front while ranged back them up from behind on the same side. Every member MUST get a different destination, spread over different approaches, use cover, and match the squad plan. Only select a listed ID.",
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
                breachPoints = squad.Breaches,
                members = members.Select((m, i) => new
                {
                    index = i, isOperative = m.Member.IsOperative, isMelee = m.Member.IsMelee,
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
            var breach = JevAnswers.Choice(root, "breach");
            if (!Enum.IsDefined(tactic) || focus is null || (focus != "none" && !targets.ContainsKey(focus)))
                return fallback;
            if (squad.Breaches.Count > 0 &&
                (breach is null || (breach != "none" && squad.Breaches.All(b => b.Id != breach))))
                return fallback;
            var breachId = breach is null or "none" ? null : breach;
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
                var role = move is not null && m.Moves.TryGetValue(move, out var picked) ? picked.Purpose : "";
                var valid = move is not null && stance is not null && RoleAllows(m.Member.IsMelee, role, breachId) &&
                            (move == "hold" || (m.Moves.ContainsKey(move) && !taken.Contains(move)));
                if (valid && move != "hold") taken.Add(move!);
                var chosen = valid
                    ? new OperativeDecision(move!, TacticalHeuristics.PickTarget(m.Member.Snapshot.Targets, focusId), stance!.Value, true)
                    : fallback.Members[id];
                if (!valid && chosen.DestinationId != "hold") taken.Add(chosen.DestinationId);
                decisions[id] = chosen;
            }
            return new SquadDecision(tactic, focusId, decisions, true, breachId);
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

    /// <summary>
    /// Role cells are named "breach_{id}" (melee push) and "support_{id}" (ranged backup). Everyone must use
    /// the cells of the chosen breach point, and ranged raiders may not take the melee front.
    /// </summary>
    private static bool RoleAllows(bool isMelee, string purpose, string? breachId)
    {
        if (purpose.StartsWith("breach_", StringComparison.Ordinal) || purpose.StartsWith("support_", StringComparison.Ordinal))
        {
            if (breachId is null || !purpose.EndsWith("_" + breachId, StringComparison.Ordinal)) return false;
            return isMelee == purpose.StartsWith("breach_", StringComparison.Ordinal);
        }
        return true;
    }

    private static Dictionary<string, string> BreachOptions(IReadOnlyList<BreachPoint> points)
    {
        var o = new Dictionary<string, string> { ["none"] = "No breach point; attack where the enemy is." };
        foreach (var b in points)
            o[b.Id] = $"Breach at ({b.X},{b.Z}): weakness {b.Weakness:0.00}, {b.Defenders} defenders and {b.Turrets} turrets cover it.";
        return o;
    }

    private static Dictionary<string, string> TargetOptions(IReadOnlyDictionary<string, TargetCandidate> targets)
    {
        var o = new Dictionary<string, string> { ["none"] = "No focus target." };
        foreach (var t in targets.Values)
            o[t.Id] = $"Visible {(t.Kind == "turret" ? $"turret (range {t.Range:0})" : "colonist")} at ({t.X},{t.Z}), threat {t.Threat:0.00}.";
        return o;
    }

    private static Dictionary<string, string> MoveOptions(IReadOnlyDictionary<string, MovementCandidate> moves)
    {
        var o = new Dictionary<string, string> { ["hold"] = "Remain at the current cell." };
        foreach (var c in moves.Values)
            o[c.Id] = $"Go to cell ({c.X},{c.Z}), {c.Purpose}; cover {c.Cover:0.00}, exposure {c.Exposure:0.00}, distance {c.Distance:0.0}{(c.InTurretRange ? ", IN RANGE OF A HOSTILE TURRET (avoid unless attacking it)" : "")}.";
        return o;
    }

    private static SquadDecision Fallback(
        SquadSnapshot squad, IReadOnlyList<Entry> members, IReadOnlyDictionary<string, TargetCandidate> targets)
    {
        var focus = TacticalHeuristics.PickTarget(targets.Values.ToList(), null);
        var breach = squad.Breaches.OrderByDescending(b => b.Weakness).ThenBy(b => b.Id, StringComparer.Ordinal)
            .FirstOrDefault()?.Id;
        var decisions = new Dictionary<string, OperativeDecision>(StringComparer.Ordinal);
        for (var i = 0; i < members.Count; i++)
        {
            var m = members[i];
            decisions[m.Member.Snapshot.PawnId] = TacticalHeuristics.Decide(
                m.Moves.Values.ToList(), m.Member.Snapshot.Targets, m.Member.Snapshot.HealthFraction,
                slot: i, focus, canFlee: !m.Member.IsOperative, lossFraction: squad.LossFraction,
                breachId: breach, isMelee: m.Member.IsMelee);
        }
        return new SquadDecision(SquadTactic.FocusFire, focus, decisions, false, breach);
    }

    private sealed record Entry(SquadMember Member, Dictionary<string, MovementCandidate> Moves);
}
