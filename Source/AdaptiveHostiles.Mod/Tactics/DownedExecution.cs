using RimWorld;
using Verse;
using Verse.AI;

namespace AdaptiveHostiles.Mod.Tactics;

/// <summary>
/// Raiders finish off downed colonists instead of walking past them. A downed colonist is not a combatant, so the squad planner
/// never offers one as a target; this picks one locally when a raider has nothing better to attack.
/// </summary>
public static class DownedExecution
{
    /// <summary>Downed colonists farther than this from the raider are left for later.</summary>
    public const float Reach = 20f;
    private const float RangedReach = 15f;

    /// <summary>The nearest downed colonist this raider can hit (ranged) or walk to (melee), or null.</summary>
    public static Pawn? Pick(Pawn raider)
    {
        if (!raider.Spawned || raider.Faction is null) return null;
        Pawn? best = null;
        var bestDist = float.MaxValue;
        foreach (var p in raider.Map.mapPawns.AllPawnsSpawned)
        {
            if (!p.Downed || p.Dead || p.Faction != Faction.OfPlayer || p.IsPrisoner || p.IsSlave) continue;
            if (!raider.Faction.HostileTo(p.Faction)) continue;
            var d = raider.Position.DistanceTo(p.Position);
            if (d > Reach || d >= bestDist) continue;
            if (!Reachable(raider, p, d)) continue;
            best = p;
            bestDist = d;
        }
        return best;
    }

    private static bool Reachable(Pawn raider, Pawn target, float distance)
    {
        var verb = raider.equipment?.PrimaryEq?.PrimaryVerb;
        var melee = raider.equipment?.Primary is null || raider.equipment.Primary.def.IsMeleeWeapon;
        if (melee) return raider.CanReach(target, PathEndMode.Touch, Danger.Deadly);
        return distance <= RangedReach && verb != null && verb.CanHitTarget(target);
    }
}
