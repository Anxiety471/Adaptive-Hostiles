namespace AdaptiveHostiles.Core;

/// <summary>Tunable limits. Defaults follow SPECS.md; the game layer maps mod settings onto this.</summary>
public sealed record AdaptiveHostilesSettings
{
    public double OperativeChance { get; init; } = 0.05;
    public int MaxRaiders { get; init; } = 24;
    public double CountMultiplier { get; init; } = 1.0;
    public int MinDecisionCooldownTicks { get; init; } = 120;
    public int MaxDecisionCooldownTicks { get; init; } = 300;
    public int MinCommitmentTicks { get; init; } = 90;
    public TimeSpan NetworkDeadline { get; init; } = TimeSpan.FromSeconds(8);
    public int MaxConcurrentRequests { get; init; } = 2;
    public int OperativeShootingMin { get; init; } = 16;
    public int OperativeShootingMax { get; init; } = 20;
    public int OperativeMeleeMin { get; init; } = 14;
    public int OperativeMeleeMax { get; init; } = 20;
}

/// <summary>
/// SplitMix64: a tiny RNG whose output is fixed by seed on every runtime, so raid rosters
/// and Operative rolls replay identically from a save seed.
/// </summary>
public sealed class DeterministicRng(ulong seed)
{
    private ulong _state = seed;

    public ulong NextUInt64()
    {
        var z = _state += 0x9E3779B97F4A7C15UL;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    /// <summary>Uniform in [0, 1).</summary>
    public double NextDouble() => (NextUInt64() >> 11) * (1.0 / (1UL << 53));

    /// <summary>Uniform in [min, max] inclusive.</summary>
    public int NextInt(int min, int max) =>
        max <= min ? min : min + (int)(NextUInt64() % (ulong)(max - min + 1));
}
