namespace AdaptiveHostiles.Core;

public sealed record DecisionLogEntry(
    long Tick, string DestinationId, int X, int Z, string? TargetId, TacticalStance Stance, bool IsModelDecision);

/// <summary>
/// Persistable per-Operative state. Contains no HTTP tasks, credentials or path searches.
/// </summary>
public sealed class OperativeRecord(string pawnId, string factionDefName)
{
    public const int MaxLog = 64;

    public string PawnId { get; } = pawnId;
    public string FactionDefName { get; } = factionDefName;
    public long NextDecisionTick { get; set; }
    public long CommitUntilTick { get; set; }
    public List<DecisionLogEntry> Log { get; set; } = new();

    public void Append(DecisionLogEntry entry)
    {
        Log.Add(entry);
        if (Log.Count > MaxLog) Log.RemoveRange(0, Log.Count - MaxLog);
    }
}

[Flags]
public enum TacticalTrigger { None = 0, Damaged = 1, TargetLost = 2, RouteBlocked = 4 }

[Flags]
public enum PawnBlock
{
    None = 0, Downed = 1, Stunned = 2, MentalBreak = 4, ScriptedDuty = 8, ForcedJob = 16, Dead = 32
}

public sealed class OperativeRegistry
{
    private readonly Dictionary<string, OperativeRecord> _records = new(StringComparer.Ordinal);

    public IReadOnlyCollection<OperativeRecord> All => _records.Values;

    public OperativeRecord Register(string pawnId, string factionDefName)
    {
        if (!_records.TryGetValue(pawnId, out var record))
            _records[pawnId] = record = new OperativeRecord(pawnId, factionDefName);
        return record;
    }

    public bool TryGet(string pawnId, out OperativeRecord record) => _records.TryGetValue(pawnId, out record!);

    public bool Remove(string pawnId) => _records.Remove(pawnId);

    /// <summary>
    /// A new request is due once the cooldown elapsed, or earlier on a meaningful change
    /// once the minimum commitment has passed. Blocked pawns are never interrupted.
    /// </summary>
    public static bool ShouldRequest(
        OperativeRecord record, long tick, TacticalTrigger trigger, PawnBlock block)
    {
        if (block != PawnBlock.None) return false;
        if (tick >= record.NextDecisionTick) return true;
        return trigger != TacticalTrigger.None && tick >= record.CommitUntilTick;
    }

    public static void ScheduleNext(
        OperativeRecord record, long tick, ulong seed, AdaptiveHostilesSettings? settings = null)
    {
        settings ??= new AdaptiveHostilesSettings();
        var rng = new DeterministicRng(seed ^ (ulong)tick);
        record.CommitUntilTick = tick + settings.MinCommitmentTicks;
        record.NextDecisionTick = tick + rng.NextInt(settings.MinDecisionCooldownTicks, settings.MaxDecisionCooldownTicks);
    }
}
