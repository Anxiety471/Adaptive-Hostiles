using AdaptiveHostiles.Core;
using RimWorld;
using Verse;
using Verse.AI;

namespace AdaptiveHostiles.Mod.Tactics;

/// <summary>
/// Raiders execute downed colonists. A raider that is not in a fight walks up to the nearest downed colonist nobody else has
/// claimed and finishes it with one killing blow. A downed colonist is not a combatant, so the squad planner never offers one as a
/// target; this is local code. A raider on its way to an execution is left out of the squad's orders until it is done.
/// </summary>
public static class DownedExecution
{
    /// <summary>Downed colonists farther than this from the raider are left for later.</summary>
    public const float Reach = 20f;
    /// <summary>A raider with a live enemy this close is in a fight and does not go for an execution.</summary>
    public const float ThreatNear = 12f;
    private const float StrikeRange = 1.5f;
    private static readonly Dictionary<string, Pawn> Assigned = new(StringComparer.Ordinal);

    public static bool IsExecuting(string pawnId) => Assigned.ContainsKey(pawnId);

    public static void Reset() => Assigned.Clear();

    public static void Tick(IReadOnlyList<Pawn> raiders)
    {
        var claimed = new HashSet<Pawn>(Assigned.Values);
        foreach (var raider in raiders)
        {
            var id = raider.ThingID;
            if (!raider.Spawned || raider.Dead || raider.Downed)
            {
                Assigned.Remove(id);
                continue;
            }
            if (Assigned.TryGetValue(id, out var current))
            {
                Step(raider, current);
                continue;
            }
            if (OperativeSampler.BlockFor(raider) != PawnBlock.None) continue;
            if (OperativeSampler.HostilePawns(raider).Any(p => raider.Position.DistanceTo(p.Position) <= ThreatNear)) continue;
            if (Pick(raider, claimed) is not { } target) continue;
            Assigned[id] = target;
            claimed.Add(target);
            Log.Message($"[Adaptive Hostiles] {raider.LabelShort} goes to execute downed {target.LabelShort} at ({target.Position.x},{target.Position.z}).");
            Step(raider, target);
        }
    }

    private static Pawn? Pick(Pawn raider, HashSet<Pawn> claimed)
    {
        if (raider.Faction is null) return null;
        Pawn? best = null;
        var bestDist = float.MaxValue;
        foreach (var p in raider.Map.mapPawns.AllPawnsSpawned)
        {
            if (!p.Downed || p.Dead || p.Faction != Faction.OfPlayer || p.IsPrisoner || p.IsSlave || claimed.Contains(p)) continue;
            if (!raider.Faction.HostileTo(p.Faction)) continue;
            var d = raider.Position.DistanceTo(p.Position);
            if (d > Reach || d >= bestDist || !raider.CanReach(p, PathEndMode.Touch, Danger.Deadly)) continue;
            best = p;
            bestDist = d;
        }
        return best;
    }

    private static void Step(Pawn raider, Pawn target)
    {
        var id = raider.ThingID;
        if (target.Dead || !target.Downed || !target.Spawned || target.Map != raider.Map)
        {
            Assigned.Remove(id);
            return;
        }
        if (raider.Position.DistanceTo(target.Position) <= StrikeRange)
        {
            Execute(raider, target);
            Assigned.Remove(id);
            return;
        }
        var goal = GenAdj.CellsAdjacent8Way(target).Where(c => c.InBounds(raider.Map) && c.Standable(raider.Map) && raider.CanReach(c, PathEndMode.OnCell, Danger.Deadly))
            .OrderBy(c => c.DistanceTo(raider.Position)).Cast<IntVec3?>().FirstOrDefault();
        if (goal is not { } cell)
        {
            Assigned.Remove(id);
            return;
        }
        var cur = raider.CurJob;
        if (cur != null && cur.def == JobDefOf.Goto && cur.targetA.Cell == cell) return;
        var go = JobMaker.MakeJob(JobDefOf.Goto, cell);
        go.locomotionUrgency = LocomotionUrgency.Sprint;
        raider.jobs.ClearQueuedJobs();
        raider.jobs.StartJob(go, JobCondition.InterruptForced);
    }

    private static void Execute(Pawn raider, Pawn target)
    {
        var blow = new DamageInfo(DamageDefOf.ExecutionCut, 9999f, 999f, -1f, raider);
        target.TakeDamage(blow);
        if (!target.Dead) target.Kill(blow);
        Log.Message($"[Adaptive Hostiles] {raider.LabelShort} executed downed {target.LabelShort}.");
    }
}
