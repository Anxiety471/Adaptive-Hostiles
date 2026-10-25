using RimWorld;
using Verse;
using Verse.AI;

namespace AdaptiveHostiles.Mod.Tactics;

/// <summary>
/// When a defended base is sealed, melee raiders must break in. Finds the first wall, door or other
/// destroyable obstacle on the shortest path to the defenders that is allowed to pass destroyable things.
/// </summary>
public static class WallBreaker
{
    public static bool TryFindBlocker(Pawn pawn, out Thing blocker)
    {
        blocker = null!;
        if (!pawn.Spawned) return false;
        var defender = OperativeSampler.HostilePawns(pawn).FirstOrDefault();
        if (defender is null) return false;

        var parms = TraverseParms.For(pawn, Danger.Deadly, TraverseMode.PassAllDestroyableThings);
        var path = pawn.Map.pathFinder.FindPathNow(pawn.Position, defender, parms, null, PathEndMode.Touch);
        try
        {
            if (!path.Found) return false;
            // NodesReversed runs from the destination back to the start; walk it from the pawn outward.
            for (var i = path.NodesReversed.Count - 1; i >= 0; i--)
            {
                var edifice = path.NodesReversed[i].GetEdifice(pawn.Map);
                if (edifice is null || edifice.Destroyed || edifice.Faction == pawn.Faction) continue;
                if (edifice.def.passability != Traversability.Impassable && edifice is not Building_Door) continue;
                blocker = edifice;
                return true;
            }
            return false;
        }
        finally
        {
            path.ReleaseToPool();
        }
    }
}
