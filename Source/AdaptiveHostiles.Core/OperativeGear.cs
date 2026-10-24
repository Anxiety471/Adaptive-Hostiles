namespace AdaptiveHostiles.Core;

public enum GearSlot { RangedWeapon, MeleeWeapon, BodyArmor, Helmet }

/// <summary>Quality tiers mirroring RimWorld's QualityCategory above "Normal".</summary>
public enum GearQuality { Good, Excellent, Masterwork, Legendary }

/// <summary>
/// A candidate item sampled from the game's defs. TechLevel uses RimWorld's TechLevel values
/// (Neolithic 2, Medieval 3, Industrial 4, Spacer 5, Ultratech 6); Power is a relative strength
/// (weapon DPS or summed armor rating) used only for ranking within a slot.
/// </summary>
public sealed record GearCandidate(string DefName, GearSlot Slot, int TechLevel, float Power);

public sealed record GearPick(string DefName, GearQuality Quality);

public sealed record OperativeLoadout(
    GearPick Weapon,
    GearPick? BodyArmor,
    GearPick? Helmet);

/// <summary>Seeded, game-independent selection of Operative equipment within faction tech bounds.</summary>
public static class OperativeGear
{
    /// <summary>How many of the strongest candidates per slot the roll may choose from.</summary>
    public const int PoolSize = 3;

    public static OperativeLoadout? Roll(
        IReadOnlyList<GearCandidate> candidates, int factionTechLevel, ulong seed, bool preferRanged = true)
    {
        var rng = new DeterministicRng(seed ^ 0x6EA2_10AD_0000_0003UL);

        var weaponSlot = preferRanged && Eligible(candidates, GearSlot.RangedWeapon, factionTechLevel).Count > 0
            ? GearSlot.RangedWeapon
            : GearSlot.MeleeWeapon;
        var weapon = Choose(candidates, weaponSlot, factionTechLevel, rng);
        if (weapon is null)
            return null;

        return new OperativeLoadout(
            weapon,
            Choose(candidates, GearSlot.BodyArmor, factionTechLevel, rng),
            Choose(candidates, GearSlot.Helmet, factionTechLevel, rng));
    }

    /// <summary>
    /// Items at or below the faction's tech level, restricted to the highest tech tier available
    /// in that slot so a spacer faction does not field club-and-rags.
    /// </summary>
    private static List<GearCandidate> Eligible(
        IReadOnlyList<GearCandidate> candidates, GearSlot slot, int factionTechLevel)
    {
        var inSlot = candidates.Where(c => c.Slot == slot && c.TechLevel <= factionTechLevel).ToList();
        if (inSlot.Count == 0)
            return inSlot;
        var top = inSlot.Max(c => c.TechLevel);
        return inSlot.Where(c => c.TechLevel == top).ToList();
    }

    private static GearPick? Choose(
        IReadOnlyList<GearCandidate> candidates, GearSlot slot, int factionTechLevel, DeterministicRng rng)
    {
        var pool = Eligible(candidates, slot, factionTechLevel)
            .OrderByDescending(c => c.Power)
            .ThenBy(c => c.DefName, StringComparer.Ordinal)
            .Take(PoolSize)
            .ToList();
        if (pool.Count == 0)
            return null;
        var item = pool[rng.NextInt(0, pool.Count - 1)];
        return new GearPick(item.DefName, RollQuality(rng));
    }

    /// <summary>Good 25%, Excellent 45%, Masterwork 25%, Legendary 5%.</summary>
    public static GearQuality RollQuality(DeterministicRng rng)
    {
        var roll = rng.NextDouble();
        return roll < 0.25 ? GearQuality.Good
            : roll < 0.70 ? GearQuality.Excellent
            : roll < 0.95 ? GearQuality.Masterwork
            : GearQuality.Legendary;
    }
}
