using System.Net.Http;
using System.Text.Json;

namespace AdaptiveHostiles.Core;

public enum SquadTactic { FocusFire, Flank, SuppressAndPush, Regroup }

/// <summary>Gather at staging cells and hold; Breach on the go signal with melee in front; Press the assault.</summary>
public enum SquadPhase { Gather, Breach, Press }

/// <summary>One controllable raider as sampled on the game thread; the Operative is flagged.</summary>
public sealed record SquadMember(OperativeSnapshot Snapshot, bool IsOperative, bool IsMelee = false);

/// <summary>
/// A place the colony is weakly defended, scored locally so Jev picks where to break through
/// instead of walking into the open kill zone. Weakness is 0..1, higher is easier to break.
/// </summary>
public sealed record BreachPoint(
    string Id, int X, int Z, float Weakness, int Defenders, int Turrets, int WallX = -1, int WallZ = -1);

public sealed record SquadSnapshot(
    string SquadId, IReadOnlyList<SquadMember> Members, int Sent, int Down, int Retreated,
    IReadOnlyList<BreachPoint>? BreachPoints = null,
    SquadPhase PreviousPhase = SquadPhase.Gather, int GatherCycles = 0, int BreachCycles = 0)
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
    string? BreachId = null, SquadPhase Phase = SquadPhase.Breach);

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

    /// <summary>Raiders count as gathered when this fraction stands at their staging cell.</summary>
    public const double ReadyFraction = 0.75;
    public const float StagedWithin = 4f;
    /// <summary>After this many gather cycles the squad goes anyway, so a blocked member cannot stall the raid.</summary>
    public const int MaxGatherCycles = 4;
    /// <summary>Ranged raiders wait for the melee push to reach the breach, at most this many cycles.</summary>
    public const int MaxMeleeLeadCycles = 4;
    public const float AtBreachWithin = 5f;

    public async Task<SquadDecision> DecideAsync(SquadSnapshot squad, CancellationToken cancellationToken = default) =>
        Sync(await DecideCoreAsync(squad, cancellationToken).ConfigureAwait(false), squad);

    private async Task<SquadDecision> DecideCoreAsync(SquadSnapshot squad, CancellationToken cancellationToken)
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

        if (squad.Breaches.Count > 0)
            questions["phase"] = new
            {
                type = "choice",
                instructions = "Choose the assault phase. The squad must not attack piecemeal: gather everyone at the staging cells first, then go together. Pick gather until most raiders are staged; breach to send melee through the breach point with ranged support behind; press once the breach is open.",
                criteria = new Dictionary<string, string>
                {
                    ["gather"] = "Everyone moves to the staging cell and holds. Nobody attacks until the go signal.",
                    ["breach"] = "Go signal: melee push through the breach, ranged support from behind, together.",
                    ["press"] = "The breach is open: the whole squad presses the attack."
                }
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
            var phase = JevAnswers.Choice(root, "phase") switch
            {
                "gather" => SquadPhase.Gather,
                "press" => SquadPhase.Press,
                _ => SquadPhase.Breach
            };
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
            return new SquadDecision(tactic, focusId, decisions, true, breachId, phase);
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
    /// Melee first, ranged backup: after the go signal ranged raiders stay at their staging cell until
    /// enough melee stand at the breach (or none are left, or the melee lead timed out).
    /// </summary>
    private static SquadDecision HoldRangedBehindMelee(SquadDecision decision, SquadSnapshot squad, string breachId)
    {
        var melee = squad.Members.Where(m => m.IsMelee).ToList();
        if (melee.Count == 0 || squad.BreachCycles >= MaxMeleeLeadCycles) return decision;
        var atBreach = melee.Count(m =>
        {
            var cell = m.Snapshot.Destinations.FirstOrDefault(c => c.Reachable && c.Purpose == "breach_" + breachId);
            if (cell is null) return true;
            var dx = m.Snapshot.X - cell.X;
            var dz = m.Snapshot.Z - cell.Z;
            return Math.Sqrt(dx * dx + dz * dz) <= AtBreachWithin;
        });
        if (atBreach >= Math.Ceiling(melee.Count / 2.0)) return decision;

        var held = new Dictionary<string, OperativeDecision>(decision.Members, StringComparer.Ordinal);
        foreach (var m in squad.Members.Where(m => !m.IsMelee))
        {
            var stage = StageCell(m, breachId);
            if (stage is null || !held.TryGetValue(m.Snapshot.PawnId, out var d)) continue;
            if (d.Stance is TacticalStance.Withdraw or TacticalStance.Flee) continue;
            held[m.Snapshot.PawnId] = d with { DestinationId = stage.Id, Stance = TacticalStance.Aggressive };
        }
        return decision with { Members = held };
    }

    private static MovementCandidate? StageCell(SquadMember m, string breachId) =>
        m.Snapshot.Destinations.FirstOrDefault(c => c.Reachable && c.Purpose == "stage_" + breachId);

    /// <summary>
    /// The go signal. Breach and Press are only allowed once enough raiders stand at their staging cells
    /// (or the squad already went, or waited too long); until then everyone gathers and holds.
    /// </summary>
    private static SquadDecision Sync(SquadDecision decision, SquadSnapshot squad)
    {
        var breachId = decision.BreachId;
        if (breachId is null || squad.Breaches.Count == 0) return decision with { Phase = decision.Phase == SquadPhase.Gather ? SquadPhase.Breach : decision.Phase };

        var staged = 0;
        foreach (var m in squad.Members)
        {
            var cell = StageCell(m, breachId);
            var dx = cell is null ? 0 : m.Snapshot.X - cell.X;
            var dz = cell is null ? 0 : m.Snapshot.Z - cell.Z;
            if (cell is null || Math.Sqrt(dx * dx + dz * dz) <= StagedWithin) staged++;
        }
        var ready = squad.Members.Count == 0 || staged >= Math.Ceiling(squad.Members.Count * ReadyFraction)
                    || squad.PreviousPhase != SquadPhase.Gather || squad.GatherCycles >= MaxGatherCycles;
        var phase = ready ? (decision.Phase == SquadPhase.Gather ? SquadPhase.Breach : decision.Phase) : SquadPhase.Gather;
        if (phase != SquadPhase.Gather) return HoldRangedBehindMelee(decision with { Phase = phase }, squad, breachId);

        // Gathering: every healthy raider goes to its staging cell and holds fire discipline until the signal.
        var held = new Dictionary<string, OperativeDecision>(StringComparer.Ordinal);
        foreach (var m in squad.Members)
        {
            if (!decision.Members.TryGetValue(m.Snapshot.PawnId, out var d)) continue;
            var stage = StageCell(m, breachId);
            held[m.Snapshot.PawnId] = d.Stance is TacticalStance.Withdraw or TacticalStance.Flee || stage is null
                ? d
                : d with { DestinationId = stage.Id, Stance = TacticalStance.Defensive };
        }
        return decision with { Phase = SquadPhase.Gather, Members = held };
    }

    /// <summary>
    /// Role cells are named "breach_{id}" (melee push) and "support_{id}" (ranged backup). Everyone must use
    /// the cells of the chosen breach point, and ranged raiders may not take the melee front.
    /// </summary>
    private static bool RoleAllows(bool isMelee, string purpose, string? breachId)
    {
        if (purpose.StartsWith("stage_", StringComparison.Ordinal))
            return breachId is not null && purpose.EndsWith("_" + breachId, StringComparison.Ordinal);
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
            o[b.Id] = $"Breach at ({b.X},{b.Z}){(b.WallX >= 0 ? $" through the wall or door at ({b.WallX},{b.WallZ})" : "")}: " +
                      $"weakness {b.Weakness:0.00}, {b.Defenders} defenders and {b.Turrets} turrets cover it.";
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
