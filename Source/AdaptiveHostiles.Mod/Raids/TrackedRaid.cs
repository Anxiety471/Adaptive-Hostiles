using AdaptiveHostiles.Core;
using RimWorld;
using Verse;

namespace AdaptiveHostiles.Mod.Raids;

/// <summary>
/// A raid we composed and are waiting to see the end of. Holds pawn references only; the outcome
/// is reduced to plain counts for Core's <see cref="RaidHistory"/> once the raid is over.
/// </summary>
public sealed class TrackedRaid : IExposable
{
    // Drop pods and arrival edges need a moment before raiders are spawned; don't judge earlier.
    private const int SettleTicks = 2500;
    private const int GiveUpTicks = 60000;

    private string factionDefName = "";
    private long startTick;
    private List<Pawn?> raiders = new();
    private List<Pawn?> colonists = new();

    public TrackedRaid() { }

    public TrackedRaid(string factionDefName, long startTick, List<Pawn> raiders, List<Pawn> colonists)
    {
        this.factionDefName = factionDefName;
        this.startTick = startTick;
        this.raiders = raiders.Cast<Pawn?>().ToList();
        this.colonists = colonists.Cast<Pawn?>().ToList();
    }

    public string Id => $"{factionDefName}:{startTick}";
    public long StartTick => startTick;
    public IReadOnlyList<Pawn?> Raiders => raiders;

    /// <summary>Counts for the squad planner; same classification as <see cref="TryFinish"/>.</summary>
    public void Status(out int sent, out int down, out int retreated)
    {
        sent = raiders.Count;
        down = 0;
        retreated = 0;
        foreach (var pawn in raiders)
        {
            if (pawn is null || pawn.Destroyed || pawn.Dead || pawn.Downed || pawn.IsPrisonerOfColony) down++;
            else if (!pawn.Spawned) retreated++;
        }
    }

    public bool TryFinish(out RaidOutcome outcome)
    {
        outcome = null!;
        var now = Find.TickManager.TicksGame;
        var age = now - startTick;
        if (age < SettleTicks) return false;

        var down = 0;
        var retreated = 0;
        var active = 0;
        foreach (var pawn in raiders)
        {
            // A discarded or destroyed pawn counts as down; a live pawn off the map left the fight.
            if (pawn is null || pawn.Destroyed || pawn.Dead || pawn.Downed || pawn.IsPrisonerOfColony) down++;
            else if (!pawn.Spawned) retreated++;
            else active++;
        }
        if (active > 0 && age < GiveUpTicks) return false;

        var colonistsLost = colonists.Count(c => c is { Dead: true });
        outcome = new RaidHistory().Record(
            factionDefName, now, raiders.Count, down, retreated, colonistsLost);
        return true;
    }

    public void ExposeData()
    {
        Scribe_Values.Look(ref factionDefName, "faction", "");
        Scribe_Values.Look(ref startTick, "start");
        Scribe_Collections.Look(ref raiders, "raiders", LookMode.Reference);
        Scribe_Collections.Look(ref colonists, "colonists", LookMode.Reference);
        raiders ??= new List<Pawn?>();
        colonists ??= new List<Pawn?>();
    }
}
