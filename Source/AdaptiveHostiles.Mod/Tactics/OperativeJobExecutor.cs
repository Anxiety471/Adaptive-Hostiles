using AdaptiveHostiles.Core;
using RimWorld;
using Verse;
using Verse.AI;

namespace AdaptiveHostiles.Mod.Tactics;

/// <summary>
/// Turns an already revalidated <see cref="OperativeOrder"/> into native jobs. The destination
/// is Jev's cell; vanilla pathing only walks there. When the order is unusable the Operative's
/// own job is ended so the vanilla think tree resumes at once.
/// </summary>
public static class OperativeJobExecutor
{
    public static void Apply(Pawn pawn, OperativeOrder order, OperativeRuntimeState state)
    {
        if (!pawn.Spawned || pawn.Dead || pawn.Downed) return;

        switch (order.Kind)
        {
            case OrderKind.None:
                return;
            case OrderKind.VanillaFallback:
                ReleaseToVanilla(pawn, state);
                return;
            case OrderKind.Flee:
                Flee(pawn, state);
                return;
        }

        var target = ResolveTarget(pawn, order.TargetId);
        state.Target = target;

        if (order.Kind == OrderKind.MoveTo)
        {
            var dest = new IntVec3(order.X, 0, order.Z);
            var go = JobMaker.MakeJob(JobDefOf.Goto, dest);
            go.locomotionUrgency = order.Stance == TacticalStance.Defensive
                ? LocomotionUrgency.Jog : LocomotionUrgency.Sprint;
            go.checkOverrideOnExpire = true;
            Start(pawn, go, state);
            state.Destination = dest;
            // A melee raider pushing a breach point of a sealed base has to break the wall or door in its way.
            if (order.Reason.StartsWith("breach_", StringComparison.Ordinal) && order.Stance != TacticalStance.Withdraw &&
                IsMelee(pawn) && WallBreaker.TryFindBlocker(pawn, out var wall))
                pawn.jobs.jobQueue.EnqueueLast(MakeAttack(pawn, wall));
            if (target != null && order.Stance != TacticalStance.Withdraw)
                pawn.jobs.jobQueue.EnqueueLast(MakeAttack(pawn, target));
            // Never leave the queue empty: when it runs out vanilla assault AI would walk the raider elsewhere.
            pawn.jobs.jobQueue.EnqueueLast(MakeHold());
            return;
        }

        // Hold: stay put, shooting only if the chosen target is actually hittable from here.
        state.Destination = null;
        if (target != null && order.Stance != TacticalStance.Withdraw && CanAttackFromHere(pawn, target))
        {
            Start(pawn, MakeAttack(pawn, target), state);
            pawn.jobs.jobQueue.EnqueueLast(MakeHold());
        }
        else
        {
            Start(pawn, MakeHold(), state);
        }
    }

    /// <summary>Stand and shoot whatever comes into view; keeps vanilla movement AI from taking over between orders.</summary>
    private static Job MakeHold()
    {
        var wait = JobMaker.MakeJob(JobDefOf.Wait_Combat);
        wait.expiryInterval = 900;
        wait.checkOverrideOnExpire = true;
        return wait;
    }

    /// <summary>Jev-ordered exit: walk to the best map edge spot and leave. Never used for the Operative.</summary>
    private static void Flee(Pawn pawn, OperativeRuntimeState state)
    {
        if (!RCellFinder.TryFindBestExitSpot(pawn, out var spot, TraverseMode.ByPawn)) return;
        var job = JobMaker.MakeJob(JobDefOf.Goto, spot);
        job.exitMapOnArrival = true;
        job.locomotionUrgency = LocomotionUrgency.Sprint;
        Start(pawn, job, state);
        state.Destination = null;
        state.Target = null;
        state.Fleeing = true;
    }

    private static void Start(Pawn pawn, Job job, OperativeRuntimeState state)
    {
        pawn.jobs.ClearQueuedJobs();
        pawn.jobs.StartJob(job, JobCondition.InterruptForced);
        state.Job = job;
        state.Fleeing = false;
    }

    private static void ReleaseToVanilla(Pawn pawn, OperativeRuntimeState state)
    {
        if (state.Job != null && pawn.CurJob == state.Job)
            pawn.jobs.EndCurrentJob(JobCondition.InterruptForced);
        state.Job = null;
        state.Destination = null;
        state.Target = null;
    }

    private static Job MakeAttack(Pawn pawn, Thing target)
    {
        var melee = IsMelee(pawn);
        var job = JobMaker.MakeJob(melee ? JobDefOf.AttackMelee : JobDefOf.AttackStatic, target);
        job.expiryInterval = 600;
        return job;
    }

    private static bool IsMelee(Pawn pawn) => pawn.equipment?.Primary == null || pawn.equipment.Primary.def.IsMeleeWeapon;

    private static bool CanAttackFromHere(Pawn pawn, Thing target)
    {
        var verb = pawn.equipment?.PrimaryEq?.PrimaryVerb;
        return verb == null ? pawn.Position.AdjacentTo8WayOrInside(target.Position) : verb.CanHitTarget(target);
    }

    /// <summary>Maps an offered target ID back to a live hostile pawn or turret; anything else is rejected.</summary>
    private static Thing? ResolveTarget(Pawn self, string? targetId)
    {
        if (targetId is null) return null;
        foreach (var p in self.Map.mapPawns.AllPawnsSpawned)
        {
            if (OperativeSampler.TargetIdFor(p) != targetId) continue;
            if (p.Dead || p.Downed || p.Faction is null || !self.Faction.HostileTo(p.Faction)) return null;
            return p;
        }
        foreach (var t in OperativeSampler.HostileTurrets(self))
            if (OperativeSampler.TargetIdFor(t) == targetId) return t;
        return null;
    }
}

/// <summary>Runtime-only bookkeeping per Operative. Deliberately not saved.</summary>
public sealed class OperativeRuntimeState
{
    public Job? Job;
    public IntVec3? Destination;
    public Thing? Target;
    public float LastHealth = 1f;
    public bool HadVisibleTarget;
    /// <summary>True while a Jev-ordered exit job is running; the pawn is left alone until it ends.</summary>
    public bool Fleeing;
}
