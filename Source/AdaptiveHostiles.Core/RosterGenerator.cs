namespace AdaptiveHostiles.Core;

public enum RosterRole { Melee, Ranged, Heavy }

/// <summary>A faction PawnKindDef the game layer found usable (humanlike, hostile-capable).</summary>
public sealed record RosterKind(string DefName, RosterRole Role);

public sealed record RosterEntry(string KindDefName, RosterRole Role, bool IsOperative);

/// <summary>
/// Roster result. <see cref="Bypassed"/> means the faction offered nothing usable and the
/// caller must defer to the vanilla raid (and mark it as not a Jev raid).
/// </summary>
public sealed record RosterResult(IReadOnlyList<RosterEntry> Entries, bool Bypassed, string? Warning);

public sealed record OperativeProfile(int Shooting, int Melee);

/// <summary>
/// Maps a validated <see cref="RaidPlan"/> onto faction pawn kinds. Inputs are the plan,
/// the faction's kinds and a seed; vanilla IncidentParms.points is deliberately absent.
/// </summary>
public static class RosterGenerator
{
    public static RosterResult Generate(
        RaidPlan plan, IReadOnlyList<RosterKind> kinds, ulong seed, AdaptiveHostilesSettings? settings = null)
    {
        settings ??= new AdaptiveHostilesSettings();
        var melee = kinds.Where(k => k.Role == RosterRole.Melee).OrderBy(k => k.DefName, StringComparer.Ordinal).ToList();
        var ranged = kinds.Where(k => k.Role == RosterRole.Ranged).OrderBy(k => k.DefName, StringComparer.Ordinal).ToList();
        var heavy = kinds.Where(k => k.Role == RosterRole.Heavy).OrderBy(k => k.DefName, StringComparer.Ordinal).ToList();
        if (melee.Count + ranged.Count + heavy.Count == 0)
            return new RosterResult(Array.Empty<RosterEntry>(), true, "Faction has no usable pawn kinds; deferring to vanilla raid.");

        var count = Math.Clamp(
            (int)Math.Round(plan.RaiderCount * Math.Max(0.1, settings.CountMultiplier)),
            1, Math.Max(1, settings.MaxRaiders));

        var (meleeShare, heavyShare) = plan.Approach switch
        {
            RaidApproach.Direct => (0.30, 0.05),
            RaidApproach.Flank => (0.20, 0.05),
            RaidApproach.Breach => (0.40, 0.30),
            RaidApproach.Siege => (0.10, 0.30),
            _ => (0.30, 0.05)
        };

        var rng = new DeterministicRng(seed);
        var entries = new List<RosterEntry>(count);
        for (var i = 0; i < count; i++)
        {
            var roll = rng.NextDouble();
            var wanted = roll < heavyShare ? RosterRole.Heavy
                : roll < heavyShare + meleeShare ? RosterRole.Melee
                : RosterRole.Ranged;
            var pool = PoolFor(wanted, ranged, melee, heavy);
            var kind = pool[(int)(rng.NextUInt64() % (ulong)pool.Count)];
            entries.Add(new RosterEntry(kind.DefName, kind.Role, false));
        }

        if (plan.IncludeOperative)
        {
            var index = ChooseOperativeIndex(entries);
            if (index >= 0)
                entries[index] = entries[index] with { IsOperative = true };
        }
        return new RosterResult(entries, false, null);
    }

    // Prefer the requested role, then fall back to whatever the faction does have.
    private static List<RosterKind> PoolFor(
        RosterRole wanted, List<RosterKind> ranged, List<RosterKind> melee, List<RosterKind> heavy)
    {
        var order = wanted switch
        {
            RosterRole.Heavy => new[] { heavy, ranged, melee },
            RosterRole.Melee => new[] { melee, ranged, heavy },
            _ => new[] { ranged, melee, heavy }
        };
        return order.First(p => p.Count > 0);
    }

    // One Operative at most: first ranged-capable pawn, otherwise the first pawn.
    private static int ChooseOperativeIndex(IReadOnlyList<RosterEntry> entries)
    {
        for (var i = 0; i < entries.Count; i++)
            if (entries[i].Role != RosterRole.Melee) return i;
        return entries.Count > 0 ? 0 : -1;
    }

    public static OperativeProfile RollOperativeProfile(ulong seed, AdaptiveHostilesSettings? settings = null)
    {
        settings ??= new AdaptiveHostilesSettings();
        var rng = new DeterministicRng(seed ^ 0xA5A5A5A5A5A5A5A5UL);
        return new OperativeProfile(
            Math.Clamp(rng.NextInt(settings.OperativeShootingMin, settings.OperativeShootingMax), 0, 20),
            Math.Clamp(rng.NextInt(settings.OperativeMeleeMin, settings.OperativeMeleeMax), 0, 20));
    }

    /// <summary>Seeded elite roll for a raid, so it replays from the save.</summary>
    public static double RollElite(ulong seed) => new DeterministicRng(seed ^ 0x0E11_7E00_0000_0001UL).NextDouble();
}
