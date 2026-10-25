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

        var path = PathCells(pawn, defender);
        foreach (var c in path)
        {
            var edifice = BlockerAt(pawn, c);
            if (edifice is null) continue;
            blocker = edifice;
            return true;
        }
        return false;
    }

    /// <summary>The shortest path (passing destroyable things) from the pawn to the target, as cells from the pawn outward.</summary>
    public static List<IntVec3> PathCells(Pawn pawn, Thing target)
    {
        var cells = new List<IntVec3>();
        var parms = TraverseParms.For(pawn, Danger.Deadly, TraverseMode.PassAllDestroyableThings);
        var path = pawn.Map.pathFinder.FindPathNow(pawn.Position, target, parms, null, PathEndMode.Touch);
        try
        {
            if (!path.Found) return cells;
            // NodesReversed runs from the destination back to the start; walk it from the pawn outward.
            for (var i = path.NodesReversed.Count - 1; i >= 0; i--) cells.Add(path.NodesReversed[i]);
        }
        finally
        {
            path.ReleaseToPool();
        }
        return cells;
    }

    /// <summary>The wall or door at this cell that stops the pawn, or null.</summary>
    public static Thing? BlockerAt(Pawn pawn, IntVec3 cell)
    {
        var edifice = cell.GetEdifice(pawn.Map);
        if (edifice is null || edifice.Destroyed || edifice.Faction == pawn.Faction) return null;
        return edifice.def.passability == Traversability.Impassable || edifice is Building_Door ? edifice : null;
    }
}
