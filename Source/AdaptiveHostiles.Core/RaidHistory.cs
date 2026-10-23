namespace AdaptiveHostiles.Core;

public enum RaidResult
{
    /// <summary>Raiders were beaten: at least half were killed, downed or captured.</summary>
    RaidersFailed,
    /// <summary>The colony took losses and the raiders were not beaten.</summary>
    RaidersSucceeded,
    Inconclusive
}

/// <summary>Aggregated outcome of one finished raid.</summary>
public sealed record RaidOutcome(
    string FactionDefName, long Tick, int RaidersSent, int RaidersDown, int RaidersRetreated,
    int ColonistsLost, RaidResult Result);

/// <summary>
/// Persistable memory of how raids went, per hostile faction. Plain data only (no pawns, tasks or
/// credentials) so the game layer can serialize it and Core can reason about it in tests.
/// </summary>
public sealed class RaidHistory
{
    public const int MaxEntries = 50;
    public const int RecentWindow = 5;

    private readonly List<RaidOutcome> _entries = new();

    public IReadOnlyList<RaidOutcome> Entries => _entries;

    public static RaidResult Classify(int raidersSent, int raidersDown, int colonistsLost)
    {
        if (raidersSent <= 0) return RaidResult.Inconclusive;
        if (raidersDown * 2 >= raidersSent) return RaidResult.RaidersFailed;
        return colonistsLost > 0 ? RaidResult.RaidersSucceeded : RaidResult.Inconclusive;
    }

    public RaidOutcome Record(
        string factionDefName, long tick, int raidersSent, int raidersDown, int raidersRetreated, int colonistsLost)
    {
        var outcome = new RaidOutcome(
            factionDefName, tick, raidersSent, raidersDown, raidersRetreated, colonistsLost,
            Classify(raidersSent, raidersDown, colonistsLost));
        Add(outcome);
        return outcome;
    }

    /// <summary>Restores a saved entry; also used by <see cref="Record"/>. Oldest entries are dropped past the cap.</summary>
    public void Add(RaidOutcome outcome)
    {
        _entries.Add(outcome);
        if (_entries.Count > MaxEntries) _entries.RemoveRange(0, _entries.Count - MaxEntries);
    }

    public int RecentFailed(string factionDefName) => Recent(factionDefName, RaidResult.RaidersFailed);

    public int RecentSucceeded(string factionDefName) => Recent(factionDefName, RaidResult.RaidersSucceeded);

    // Counts results among the faction's last RecentWindow raids.
    private int Recent(string factionDefName, RaidResult result)
    {
        var count = 0;
        var seen = 0;
        for (var i = _entries.Count - 1; i >= 0 && seen < RecentWindow; i--)
        {
            if (!string.Equals(_entries[i].FactionDefName, factionDefName, StringComparison.Ordinal)) continue;
            seen++;
            if (_entries[i].Result == result) count++;
        }
        return count;
    }
}
