using AdaptiveHostiles.Core;
using RimWorld;
using Verse;

namespace AdaptiveHostiles.Mod.Raids;

/// <summary>
/// Marks a generated raider as the Jev Operative, raises its combat skills within the configured
/// range and records it. Gear upgrades and tactical control are not implemented yet.
/// </summary>
public static class OperativeUpgrader
{
    public static void Upgrade(Pawn pawn, Faction faction, ulong seed, AdaptiveHostilesSettings settings)
    {
        var profile = RosterGenerator.RollOperativeProfile(seed, settings);
        Raise(pawn, SkillDefOf.Shooting, profile.Shooting);
        Raise(pawn, SkillDefOf.Melee, profile.Melee);
        AdaptiveHostilesComponent.Instance?.Registry.Register(pawn.ThingID, faction.def.defName);
        Log.Message($"[Adaptive Hostiles] Operative {pawn.LabelShort} ({faction.def.defName}): shooting {profile.Shooting}, melee {profile.Melee}.");
    }

    private static void Raise(Pawn pawn, SkillDef def, int level)
    {
        var skill = pawn.skills?.GetSkill(def);
        if (skill is null || skill.TotallyDisabled) return;
        skill.Level = Math.Max(skill.Level, level);
    }
}
