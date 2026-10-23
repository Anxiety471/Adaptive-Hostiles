namespace AdaptiveHostiles.Core;

/// <summary>Game-thread view of one Operative, supplied by the RimWorld layer.</summary>
public interface IOperativeHost
{
    PawnBlock Block { get; }
    TacticalTrigger Trigger { get; }
    /// <summary>Samples the pawn and builds the candidate set offered to Jev.</summary>
    OperativeSnapshot Sample();
    /// <summary>A fresh map view used to revalidate Jev's answer.</summary>
    ITacticalMap CurrentMap { get; }
    void Apply(OperativeOrder order);
}

/// <summary>
/// Per-tick tactical driver, free of RimWorld types. Everything here runs on the game thread;
/// only the planner call is handed to <see cref="JevScheduler{T}"/> and runs off-thread.
/// Without a remote planner the local fallback decision is used synchronously.
/// </summary>
public sealed class OperativeTacticalLoop
{
    private readonly OperativePlanner _planner;
    private readonly JevScheduler<OperativeDecision>? _scheduler;
    private readonly AdaptiveHostilesSettings _settings;
    private readonly TimeSpan _maxResultAge;
    private readonly Dictionary<string, (OperativeSnapshot Offered, long Tick)> _pending = new(StringComparer.Ordinal);

    public OperativeTacticalLoop(
        OperativePlanner planner, JevScheduler<OperativeDecision>? scheduler, AdaptiveHostilesSettings settings)
    {
        _planner = planner;
        _scheduler = scheduler;
        _settings = settings;
        _maxResultAge = settings.NetworkDeadline + TimeSpan.FromSeconds(2);
    }

    public bool HasPending(string pawnId) => _pending.ContainsKey(pawnId);

    /// <summary>Drops all in-flight work (save/load, map change). Answers that arrive later are discarded.</summary>
    public void Reset()
    {
        _pending.Clear();
        _scheduler?.Reset();
    }

    public void Tick(OperativeRecord record, IOperativeHost host, long tick, ulong seed)
    {
        var block = host.Block;
        if (block != PawnBlock.None)
        {
            // A dead, downed or otherwise occupied pawn must not keep a stale request alive.
            _pending.Remove(record.PawnId);
            return;
        }

        if (_pending.TryGetValue(record.PawnId, out var pending))
        {
            if (_scheduler!.TryTake(record.PawnId, _maxResultAge, out var decision))
            {
                _pending.Remove(record.PawnId);
                Finish(record, host, decision, pending.Offered, tick, seed);
            }
            else if (tick - pending.Tick > TimeoutTicks())
            {
                // Request lost (timeout, 429, stale generation): give up and replan later.
                _pending.Remove(record.PawnId);
                OperativeRegistry.ScheduleNext(record, tick, seed, _settings);
            }
            return;
        }

        if (!OperativeRegistry.ShouldRequest(record, tick, host.Trigger, block)) return;

        var snapshot = host.Sample();
        if (_scheduler is null)
        {
            var local = _planner.DecideAsync(snapshot).GetAwaiter().GetResult();
            Finish(record, host, local, snapshot, tick, seed);
        }
        else if (_scheduler.TryBegin(record.PawnId, ct => _planner.DecideAsync(snapshot, ct)))
        {
            _pending[record.PawnId] = (snapshot, tick);
        }
        // else: backing off or at the concurrency cap; stay due and retry on a later tick.
    }

    private void Finish(
        OperativeRecord record, IOperativeHost host, OperativeDecision decision,
        OperativeSnapshot offered, long tick, ulong seed)
    {
        // Re-read the block: the pawn may have been downed or stunned during network latency.
        var order = OperativeOrderResolver.Resolve(decision, offered, host.CurrentMap, host.Block, record, tick);
        host.Apply(order);
        OperativeRegistry.ScheduleNext(record, tick, seed, _settings);
    }

    private long TimeoutTicks() => (long)(_settings.NetworkDeadline.TotalSeconds * 60) + 120;
}
