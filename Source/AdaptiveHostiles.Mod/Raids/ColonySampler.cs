using AdaptiveHostiles.Core;
using RimWorld;
using Verse;

namespace AdaptiveHostiles.Mod.Raids;

/// <summary>Builds the bounded Core snapshot. Must run on the game thread.</summary>
public static class ColonySampler
{
    public static ColonySnapshot Sample(Map map, Faction raider)
    {
        var colonists = map.mapPawns.FreeColonistsSpawned;
        var combatReady = 0;
        var ranged = 0;
        var injured = 0;
        foreach (var pawn in colonists)
        {
            if (!pawn.Downed && !pawn.WorkTagIsDisabled(WorkTags.Violent)) combatReady++;
            if (pawn.equipment?.Primary?.def.IsRangedWeapon == true) ranged++;
            if (pawn.health.hediffSet.hediffs.Any(h => h is Hediff_Injury)) injured++;
        }

        var turrets = 0;
        var defensive = 0;
        foreach (var building in map.listerBuildings.allBuildingsColonist)
        {
            if (building is Building_Turret) turrets++;
            // Candidate definition (SPECS.md leaves it TBD): solid, cover-giving edifices.
            else if (building.def.passability == Traversability.Impassable && building.def.fillPercent >= 0.5f)
                defensive++;
        }

        return new ColonySnapshot(
            map.wealthWatcher.WealthTotal,
            colonists.Count,
            combatReady,
            ranged,
            turrets,
            defensive,
            injured,
            raider.def.defName,
            raider.def.techLevel.ToString(),
            RecentFailedRaids: 0); // TODO: persisted raid_history
    }
}
