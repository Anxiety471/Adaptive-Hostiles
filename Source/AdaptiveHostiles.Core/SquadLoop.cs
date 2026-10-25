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
        _stall.Remove(squadId);
        _chosen.Remove(squadId);
        _split.Remove(squadId);
    }

    public void Reset()
    {
        _pending.Clear();
        _phase.Clear();
        _stall.Clear();
        _chosen.Clear();
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
            snapshot = snapshot with { PreviousPhase = known.Phase, GatherCycles = known.GatherCycles, BreachCycles = known.BreachCycles };
        snapshot = Tactics(squadId, host, snapshot, _phase.ContainsKey(squadId), known.Phase);
        if (_scheduler is null)
        {
            var local = _planner.DecideAsync(snapshot).GetAwaiter().GetResult();
            Finish(squadId, host, local, snapshot, tick, seed);
        }
        else if (_scheduler.TryBegin(squadId, ct => _planner.DecideAsync(snapshot, ct)))
        {
            _pending[squadId] = (snapshot, tick);
        }
    }

    private void Finish(string squadId, ISquadHost host, SquadDecision decision, SquadSnapshot offered, long tick, ulong seed)
    {
        foreach (var member in offered.Members)
        {
            var id = member.Snapshot.PawnId;
            if (!decision.Members.TryGetValue(id, out var memberDecision)) continue; // beyond the cap: vanilla AI
            var order = OperativeOrderResolver.Resolve(
                memberDecision, member.Snapshot, host.CurrentMap(id), host.BlockOf(id),
                member.IsOperative ? host.RecordOf(id) : null, tick, canFlee: !member.IsOperative);
            host.Apply(id, order);
        }
        _phase.TryGetValue(squadId, out var prior);
        if (decision.BreachId is not null) _chosen[squadId] = decision.BreachId;
        if (!_phase.ContainsKey(squadId) || prior.Phase != decision.Phase)
            host.Callout(IntentLog.Describe(decision.Phase, decision, offered));
        var gather = decision.Phase == SquadPhase.Gather ? prior.GatherCycles + 1 : 0;
        var breach = decision.Phase == SquadPhase.Gather ? 0 : prior.BreachCycles + 1;
        _phase[squadId] = (decision.Phase, gather, breach);
        OperativeRegistry.ScheduleNext(host.Timing, tick, seed, _settings);
    }

    // Stall replanning, the second breach group and ranged opening a wall, applied to the sampled snapshot.
    private SquadSnapshot Tactics(string squadId, ISquadHost host, SquadSnapshot snapshot, bool known, SquadPhase phase)
    {
        if (snapshot.Breaches.Count == 0) return snapshot;
        if (!_stall.TryGetValue(squadId, out var stall)) _stall[squadId] = stall = new StallTracker();
        static string Key(BreachPoint b) => $"{b.X},{b.Z}";
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

        if (!_split.ContainsKey(squadId) && known && phase != SquadPhase.Gather &&
            SquadSplit.Choose(members, snapshot.Breaches, main.Id, snapshot.Defenders) is { } split)
        {
            _split[squadId] = (split.PawnIds, Key(snapshot.Breaches.First(b => b.Id == split.BreachId)));
            host.Callout($"{split.PawnIds.Count} raiders split off to breach {split.BreachId}; defence is thin");
        }
        if (_split.TryGetValue(squadId, out var group) && snapshot.Breaches.FirstOrDefault(b => Key(b) == group.BreachKey) is { } gb)
            members = members.Select(m => group.PawnIds.Contains(m.Snapshot.PawnId) ? m with { GroupBreachId = gb.Id } : m).ToList();

        if (stall.RangedOpensWall)
        {
            // The wall the squad is not already pushing: the weakest other point.
            var other = snapshot.Breaches.Where(b => b.Id != main.Id)
                .OrderByDescending(b => b.Weakness).ThenBy(b => b.Id, StringComparer.Ordinal).FirstOrDefault() ?? main;
            members = members.Select(m => !m.IsMelee && m.GroupBreachId is null
                ? m with { IsMelee = true, GroupBreachId = other.Id } : m).ToList();
        }
        return snapshot with { BreachPoints = points, Members = members };
    }
}
