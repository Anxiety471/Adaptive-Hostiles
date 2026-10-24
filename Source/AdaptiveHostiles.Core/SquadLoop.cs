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
    private readonly Dictionary<string, (SquadPhase Phase, int GatherCycles)> _phase = new(StringComparer.Ordinal);

    public SquadLoop(SquadPlanner planner, JevScheduler<SquadDecision>? scheduler, AdaptiveHostilesSettings settings)
    {
        _planner = planner;
        _scheduler = scheduler;
        _settings = settings;
        _maxResultAge = settings.NetworkDeadline + TimeSpan.FromSeconds(2);
    }

    public bool HasPending(string squadId) => _pending.ContainsKey(squadId);

    public void Forget(string squadId)
    {
        _pending.Remove(squadId);
        _phase.Remove(squadId);
    }

    public void Reset()
    {
        _pending.Clear();
        _phase.Clear();
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
            snapshot = snapshot with { PreviousPhase = known.Phase, GatherCycles = known.GatherCycles };
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
        var cycles = decision.Phase == SquadPhase.Gather && _phase.TryGetValue(squadId, out var prior) ? prior.GatherCycles + 1 : 0;
        _phase[squadId] = (decision.Phase, cycles);
        OperativeRegistry.ScheduleNext(host.Timing, tick, seed, _settings);
    }
}
