namespace AdaptiveHostiles.Core;

/// <summary>Game-thread view of one raid squad, supplied by the RimWorld layer.</summary>
public interface ISquadHost
{
    /// <summary>Squad-wide cooldown and commitment; the Operative keeps its own record for the decision log.</summary>
    OperativeRecord Timing { get; }
    TacticalTrigger Trigger { get; }
    /// <summary>Samples every currently controllable member; empty when none can be ordered.</summary>
    SquadSnapshot Sample();
    PawnBlock BlockOf(string pawnId);
    /// <summary>Fresh map view from this member's position, used to revalidate its destination.</summary>
    ITacticalMap CurrentMap(string pawnId);
    OperativeRecord? RecordOf(string pawnId);
    void Apply(string pawnId, OperativeOrder order);
    /// <summary>A short intent line for the mod log only, once per phase change.</summary>
    void Callout(string text) { }
}

/// <summary>
/// Per-tick driver for a whole squad. Mirrors <see cref="OperativeTacticalLoop"/> but spends one
/// planner call on the squad and resolves each member's answer against its own offered snapshot.
/// </summary>
public sealed class SquadLoop
{
    private readonly SquadPlanner _planner;
    private readonly JevScheduler<SquadDecision>? _scheduler;
    private readonly AdaptiveHostilesSettings _settings;
    private readonly TimeSpan _maxResultAge;
    private readonly Dictionary<string, (SquadSnapshot Offered, long Tick)> _pending = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (SquadPhase Phase, int GatherCycles, int BreachCycles)> _phase = new(StringComparer.Ordinal);
    private readonly Dictionary<string, StallTracker> _stall = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Dictionary<string, long>> _withdrawn = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _gatherStart = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _callStart = new(StringComparer.Ordinal);
    private readonly List<double> _latencyMs = new();
    private readonly Dictionary<string, string> _sync = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _chosen = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (IReadOnlyList<string> PawnIds, string BreachKey)> _split = new(StringComparer.Ordinal);

    public SquadLoop(SquadPlanner planner, JevScheduler<SquadDecision>? scheduler, AdaptiveHostilesSettings settings)
    {
        _planner = planner;
        _scheduler = scheduler;
        _settings = settings;
        _maxResultAge = settings.NetworkDeadline + TimeSpan.FromSeconds(2);
    }

    public bool HasPending(string squadId) => _pending.ContainsKey(squadId);

    /// <summary>Drops the in-flight request after an error but keeps the squad's phase.</summary>
    public void Abort(string squadId) => _pending.Remove(squadId);

    public void Forget(string squadId)
    {
        _pending.Remove(squadId);
        _phase.Remove(squadId);
        _gatherStart.Remove(squadId);
        _stall.Remove(squadId);
        _chosen.Remove(squadId);
        _sync.Remove(squadId);
        _sync.Remove(squadId + "/flank");
        _withdrawn.Remove(squadId);
        _split.Remove(squadId);
    }

    public void Reset()
    {
        _pending.Clear();
        _phase.Clear();
        _gatherStart.Clear();
        _stall.Clear();
        _chosen.Clear();
        _sync.Clear();
        _withdrawn.Clear();
        _split.Clear();
        _scheduler?.Reset();
    }

    public void Tick(string squadId, ISquadHost host, long tick, ulong seed)
    {
        var timing = host.Timing;
        if (_pending.TryGetValue(squadId, out var pending))
        {
            if (_scheduler!.TryTake(squadId, _maxResultAge, out var decision))
            {
                _pending.Remove(squadId);
                RecordLatency(host, squadId, decision.IsModelDecision);
                Finish(squadId, host, decision, pending.Offered, tick, seed);
            }
            else if (tick - pending.Tick > (long)(_settings.NetworkDeadline.TotalSeconds * 60) + 120)
            {
                _pending.Remove(squadId);
                OperativeRegistry.ScheduleNext(timing, tick, seed, _settings);
            }
            return;
        }

        if (!OperativeRegistry.ShouldRequest(timing, tick, host.Trigger, PawnBlock.None)) return;

        var snapshot = host.Sample();
        if (snapshot.Members.Count == 0) return;
        if (_phase.TryGetValue(squadId, out var known))
            snapshot = snapshot with { PreviousPhase = known.Phase, GatherCycles = GatherSeconds(squadId, tick), BreachCycles = known.BreachCycles };
        snapshot = Tactics(squadId, host, snapshot, _phase.ContainsKey(squadId), known.Phase);
        if (_scheduler is null)
        {
            var local = _planner.DecideAsync(snapshot).GetAwaiter().GetResult();
            Finish(squadId, host, local, snapshot, tick, seed);
        }
        else if (_scheduler.TryBegin(squadId, ct => _planner.DecideAsync(snapshot, ct)))
        {
            _pending[squadId] = (snapshot, tick);
            _callStart[squadId] = System.Diagnostics.Stopwatch.GetTimestamp();
        }
    }

    private void Finish(string squadId, ISquadHost host, SquadDecision decision, SquadSnapshot offered, long tick, ulong seed)
    {
        foreach (var member in offered.Members)
        {
            var id = member.Snapshot.PawnId;
            if (!decision.Members.TryGetValue(id, out var memberDecision)) continue; // beyond the cap: vanilla AI
            memberDecision = KeepWithdrawn(squadId, member, memberDecision, tick);
            var order = OperativeOrderResolver.Resolve(
                memberDecision, member.Snapshot, host.CurrentMap(id), host.BlockOf(id),
                member.IsOperative ? host.RecordOf(id) : null, tick, canFlee: !member.IsOperative);
            host.Apply(id, order);
        }
        if (offered.Flank is { } offer && !_split.ContainsKey(squadId))
        {
            // Logged on change only, so a run shows whether a flank was possible and what the model answered.
            var line = $"flank offered at {offer.BreachId} for {offer.MinSize}-{offer.MaxSize} raiders; {(decision.FlankSize > 0 ? $"{decision.FlankSize} sent" : "none sent")}" +
                       (decision.IsModelDecision ? "" : " (local default)");
            if (!_sync.TryGetValue(squadId + "/flank", out var lastLine) || lastLine != line)
            {
                _sync[squadId + "/flank"] = line;
                host.Callout(line);
            }
        }
        if (decision.FlankSize > 0 && offered.Flank is not null && decision.BreachId is not null && !_split.ContainsKey(squadId) &&
            SquadSplit.Choose(offered.Members, offered.Breaches, decision.BreachId, offered.Defenders, decision.FlankSize) is { } split)
        {
            _split[squadId] = (split.PawnIds, BreachKey(offered.Breaches.First(b => b.Id == split.BreachId)));
            host.Callout($"{split.PawnIds.Count} raiders split off to breach {split.BreachId} to find an opening while the rest keep the defenders busy");
        }
        _phase.TryGetValue(squadId, out var prior);
        if (decision.BreachId is not null) _chosen[squadId] = decision.BreachId;
        if (!_phase.ContainsKey(squadId) || prior.Phase != decision.Phase)
            host.Callout(IntentLog.Describe(decision.Phase, decision, offered));
        if (decision.BreachId is not null && decision.Phase != SquadPhase.Gather)
        {
            // Melee/ranged sync, logged whenever it changes so a run shows when melee held and what the ranged were told.
            var c = SquadCoordination.Measure(offered, decision.BreachId);
            if (c.HasBoth)
            {
                var key = $"{decision.MeleeHold}/{decision.RangedRole}/{c.MeleeIsThrough}/{c.RangedIsReady}";
                if (!_sync.TryGetValue(squadId, out var last) || last != key)
                {
                    _sync[squadId] = key;
                    host.Callout($"sync: melee {(decision.MeleeHold ? "asked to hold for cover" : "go")}, ranged {decision.RangedRole}; " +
                                 $"{c.MeleeThrough}/{c.Melee} melee through, {c.RangedInPosition}/{c.Ranged} ranged in position, " +
                                 $"nearest melee {c.MeleeNearestToBreach:0.#} from the breach cell");
                }
            }
        }
        if (decision.Phase == SquadPhase.Gather) _gatherStart.TryAdd(squadId, tick); else _gatherStart.Remove(squadId);
        var gather = decision.Phase == SquadPhase.Gather ? prior.GatherCycles + 1 : 0;
        var breach = decision.Phase == SquadPhase.Gather ? 0 : prior.BreachCycles + 1;
        _phase[squadId] = (decision.Phase, gather, breach);
        OperativeRegistry.ScheduleNext(host.Timing, tick, seed, _settings);
    }

    // Gathering is timed in seconds of game time (60 ticks) from the first gather order, not in cycles, so the one-second cadence
    // and a slow model answer both force the go after the same wait.
    private int GatherSeconds(string squadId, long tick) =>
        _gatherStart.TryGetValue(squadId, out var start) ? (int)((tick - start) / 60) : 0;

    // Request start to answer applied, in milliseconds; the median and the 95th percentile go to the mod log every 20 calls.
    private void RecordLatency(ISquadHost host, string squadId, bool model)
    {
        if (!_callStart.Remove(squadId, out var start)) return;
        _latencyMs.Add(System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds);
        if (_latencyMs.Count < 20) return;
        var sorted = _latencyMs.OrderBy(x => x).ToList();
        host.Callout($"decision latency over {sorted.Count} calls: median {sorted[sorted.Count / 2]:0} ms, p95 {sorted[(int)(sorted.Count * 0.95)]:0} ms" +
                     (model ? "" : " (local fallback answered the last call)"));
        _latencyMs.Clear();
    }

    /// <summary>A withdrawn raider that is still bleeding or untreated stays out this long (game ticks) before it is released anyway.</summary>
    public const long MaxTendWaitTicks = 3600;

    // A raider that withdrew stays withdrawn until it has recovered and treated itself, so a hurt melee raider does not walk back
    // and forth, and nobody goes back into the fight untreated. The wait for treatment is capped so a raider that cannot tend itself
    // (no way to) is not held out forever.
    private OperativeDecision KeepWithdrawn(string squadId, SquadMember member, OperativeDecision decision, long tick)
    {
        if (!_withdrawn.TryGetValue(squadId, out var set)) _withdrawn[squadId] = set = new Dictionary<string, long>(StringComparer.Ordinal);
        var id = member.Snapshot.PawnId;
        if (decision.Stance is TacticalStance.Withdraw or TacticalStance.Flee)
        {
            set.TryAdd(id, tick);
            return decision;
        }
        if (!set.TryGetValue(id, out var since)) return decision;
        var healed = member.Snapshot.HealthFraction >= TacticalHeuristics.WithdrawUntil;
        var treated = !member.Snapshot.NeedsTending || tick - since >= MaxTendWaitTicks;
        if (healed && treated)
        {
            set.Remove(id);
            return decision;
        }
        var safe = TacticalHeuristics.PickDestination(member.Snapshot.Destinations, TacticalStance.Withdraw, 0);
        return decision with { DestinationId = safe?.Id ?? "hold", Stance = TacticalStance.Withdraw };
    }

    private static string BreachKey(BreachPoint b) => $"{b.X},{b.Z}";

    // Stall replanning, the second breach group and ranged opening a wall, applied to the sampled snapshot.
    private SquadSnapshot Tactics(string squadId, ISquadHost host, SquadSnapshot snapshot, bool known, SquadPhase phase)
    {
        if (snapshot.Breaches.Count == 0) return snapshot;
        if (!_stall.TryGetValue(squadId, out var stall)) _stall[squadId] = stall = new StallTracker();
        static string Key(BreachPoint b) => BreachKey(b);
        var keys = snapshot.Breaches.Select(Key).ToList();

        if (known && phase != SquadPhase.Gather && _chosen.TryGetValue(squadId, out var chosenId) &&
            snapshot.Breaches.FirstOrDefault(b => b.Id == chosenId) is { } chosen)
        {
            var pushers = snapshot.Members.Where(m => m.IsMelee).ToList();
            if (pushers.Count == 0) pushers = snapshot.Members.ToList();
            var closest = pushers.Min(m => Math.Sqrt(Math.Pow(m.Snapshot.X - chosen.X, 2) + Math.Pow(m.Snapshot.Z - chosen.Z, 2)));
            if (stall.Observe(Key(chosen), snapshot.DamageDealt, closest, keys))
                host.Callout($"assault on breach {chosen.Id} stalled (no damage, no progress); " +
                             (stall.RangedOpensWall ? "every point stalled, ranged will open a second wall" : "switching breach point"));
        }

        var points = stall.Available(snapshot.Breaches, Key);
        var members = snapshot.Members;
        var main = points.OrderByDescending(b => b.Weakness).ThenBy(b => b.Id, StringComparer.Ordinal).First();

        // The game state decides whether a flank is possible; the model decides how many raiders go (see Finish).
        FlankOffer? flank = null;
        // Offered from the first cycle after the squad is known, gathering included: later the opening may already be breached
        // and only one breach point is left, so the flank group has to take its own staging cell from the start.
        if (!_split.ContainsKey(squadId) && known)
            flank = SquadSplit.Offer(members, snapshot.Breaches, main.Id, snapshot.Defenders);
        if (_split.TryGetValue(squadId, out var group) && snapshot.Breaches.FirstOrDefault(b => Key(b) == group.BreachKey) is { } gb)
            members = members.Select(m => group.PawnIds.Contains(m.Snapshot.PawnId) ? m with { GroupBreachId = gb.Id } : m).ToList();

        if (stall.RangedOpensWall)
        {
            // The wall the squad is not already pushing: the weakest other point.
            // Only a point with a wall needs opening; an open point has nothing for ranged to break.
            var other = snapshot.Breaches.Where(b => b.Id != main.Id && b.WallX >= 0)
                .OrderByDescending(b => b.Weakness).ThenBy(b => b.Id, StringComparer.Ordinal).FirstOrDefault();
            if (other is not null)
                members = members.Select(m => !m.IsMelee && m.GroupBreachId is null
                    ? m with { IsMelee = true, GroupBreachId = other.Id } : m).ToList();
        }
        return snapshot with { BreachPoints = points, Members = members, Flank = flank };
    }
}
