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
    /// <param name="pinnedWall">The wall or door of the breach point the order targets, if it still stands.</param>
    public static void Apply(Pawn pawn, OperativeOrder order, OperativeRuntimeState state, Thing? pinnedWall = null)
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
            // Like a drafted colonist: anything in weapon range is attacked where the raider stands, the order's cell comes after.
            if (order.Stance != TacticalStance.Withdraw && EngageInRange(pawn, target, state)) return;
            // A melee raider that can reach an enemy goes for it; the ordered target may be far off or walled in.
            if (IsMelee(pawn) && order.Stance != TacticalStance.Withdraw && !MeleeCanGo(pawn, target))
                target = state.Target = NearestReachableEnemy(pawn);
            // A retreated raider that reached its safe cell treats its own wounds before anything else.
            if (order.Stance == TacticalStance.Withdraw && pawn.Position.DistanceTo(dest) <= 2f && TendSelf(pawn, state)) return;
            // Re-issuing the same order every cycle would restart the aim; let a walk or a shoot run on.
            // A raider that only waits at its cell while an enemy is now in reach attacks instead of idling there.
            if (AlreadyCarryingOut(pawn, dest, state) && !(pawn.CurJob?.def == JobDefOf.Wait_Combat && order.Stance != TacticalStance.Withdraw &&
                                                          target != null && (IsMelee(pawn) ? MeleeCanGo(pawn, target) : CanAttackFromHere(pawn, target))))
                return;
            var go = JobMaker.MakeJob(JobDefOf.Goto, dest);
            go.locomotionUrgency = order.Stance == TacticalStance.Defensive || pawn.Position.DistanceTo(dest) < 15f
                ? LocomotionUrgency.Jog : LocomotionUrgency.Sprint;
            go.checkOverrideOnExpire = true;
            Start(pawn, go, state);
            state.Destination = dest;
            // A raider pushing a breach point of a sealed base has to break the wall or door in its way.
            // The raid's pinned breach wall comes first; once it is down, the next wall on the path takes over.
            if (order.Reason.StartsWith("breach_", StringComparison.Ordinal) && order.Stance != TacticalStance.Withdraw &&
                (pinnedWall ?? (WallBreaker.TryFindBlocker(pawn, out var wall) ? wall : null)) is { } breachWall)
                pawn.jobs.jobQueue.EnqueueLast(MakeAttack(pawn, breachWall));
            // Ranged raiders backing a breach shoot the wall too instead of idling behind it, unless they can see their target.
            else if (order.Reason.StartsWith("support_", StringComparison.Ordinal) &&
                     !(target != null && GenSight.LineOfSight(dest, target.Position, pawn.Map)) &&
                     order.Stance != TacticalStance.Withdraw && !IsMelee(pawn) &&
                     (pinnedWall ?? (WallBreaker.TryFindBlocker(pawn, out var cover) ? cover : null)) is { } coverWall)
                pawn.jobs.jobQueue.EnqueueLast(MakeAttack(pawn, coverWall));
            // A melee raider only goes for a target it can actually reach and is close to: chasing a colonist behind a wall
            // or across the base pulled raiders off their cells and left them stuck against the wall.
            if (target != null && order.Stance != TacticalStance.Withdraw && (!IsMelee(pawn) || MeleeCanGo(pawn, target)))
                pawn.jobs.jobQueue.EnqueueLast(MakeAttack(pawn, target));
            // A raider walking back to safety tends itself on arrival.
            if (order.Stance == TacticalStance.Withdraw && pawn.health.HasHediffsNeedingTend())
                pawn.jobs.jobQueue.EnqueueLast(MakeTendSelf(pawn));
            // Never leave the queue empty: when it runs out vanilla assault AI would walk the raider elsewhere.
            pawn.jobs.jobQueue.EnqueueLast(MakeHold());
            return;
        }

        // Hold: stay put, shooting only if the chosen target is actually hittable from here.
        state.Destination = null;
        if (order.Stance == TacticalStance.Withdraw && TendSelf(pawn, state)) return;
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

    private static Job MakeTendSelf(Pawn pawn)
    {
        var job = JobMaker.MakeJob(JobDefOf.TendPatient, pawn);
        job.expiryInterval = 600;
        return job;
    }

    /// <summary>Starts self-treatment when the raider has wounds needing it, no enemy is close and it is not already tending.</summary>
    private static bool TendSelf(Pawn pawn, OperativeRuntimeState state)
    {
        if (pawn.CurJob?.def == JobDefOf.TendPatient) return true;
        if (!pawn.health.HasHediffsNeedingTend()) return false;
        if (OperativeSampler.HostilePawns(pawn).Any(p => pawn.Position.DistanceTo(p.Position) <= 8f)) return false;
        Log.Message($"[Adaptive Hostiles] {pawn.LabelShort} withdrew and tends its own wounds at ({pawn.Position.x},{pawn.Position.z}).");
        Start(pawn, MakeTendSelf(pawn), state);
        pawn.jobs.jobQueue.EnqueueLast(MakeHold());
        return true;
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
        if (target is Building b && b.def.category == ThingCategory.Building)
            Log.Message($"[Adaptive Hostiles] {pawn.LabelShort} attacks {b.LabelShort} at ({b.Position.x},{b.Position.z}).");
        var job = JobMaker.MakeJob(melee ? JobDefOf.AttackMelee : JobDefOf.AttackStatic, target);
        job.expiryInterval = 600;
        return job;
    }

    private static bool AlreadyCarryingOut(Pawn pawn, IntVec3 dest, OperativeRuntimeState state)
    {
        var job = pawn.CurJob;
        if (job is null) return false;
        if (state.Destination == dest && job == state.Job) return true; // still walking to the same cell
        if (pawn.Position.DistanceTo(dest) > 2f) return false;
        var def = job.def;
        return def == JobDefOf.Wait_Combat || def == JobDefOf.AttackStatic || def == JobDefOf.AttackMelee;
    }

    private const float MeleeEngage = 5f;

    /// <summary>
    /// Draft-mode behaviour: a ranged raider shoots the ordered target, else the nearest enemy it can hit from where it stands;
    /// a melee raider fights the nearest reachable enemy within a few cells. The attack runs on across cycles and the raider
    /// resumes its order once nothing is left in range. Returns true when an attack is running.
    /// </summary>
    private static bool EngageInRange(Pawn pawn, Thing? ordered, OperativeRuntimeState state)
    {
        Thing? pick = null;
        if (IsMelee(pawn))
            pick = OperativeSampler.HostilePawns(pawn).FirstOrDefault(p =>
                pawn.Position.DistanceTo(p.Position) <= MeleeEngage && pawn.CanReach(p, PathEndMode.Touch, Danger.Deadly));
        else if (ordered != null && CanAttackFromHere(pawn, ordered)) pick = ordered;
        else
        {
            pick = OperativeSampler.HostilePawns(pawn).Take(6).FirstOrDefault(p => CanAttackFromHere(pawn, p)) as Thing
                   ?? OperativeSampler.HostileTurrets(pawn).Take(4).FirstOrDefault(t => CanAttackFromHere(pawn, t));
        }
        // Nothing live to fight: finish off a downed colonist instead of walking past it.
        pick ??= DownedExecution.Pick(pawn);
        if (pick is null) return false;

        var cur = pawn.CurJob;
        if (cur != null && (cur.def == JobDefOf.AttackStatic || cur.def == JobDefOf.AttackMelee) && cur.targetA.Thing == pick) return true;
        state.Target = pick;
        state.Destination = null;
        Start(pawn, MakeAttack(pawn, pick), state);
        pawn.jobs.jobQueue.EnqueueLast(MakeHold());
        return true;
    }

    /// <summary>Starts an attack on a target outside the squad's orders (mopping up downed colonists), then waits in combat.</summary>
    public static void AttackNow(Pawn pawn, Thing target, OperativeRuntimeState state)
    {
        var cur = pawn.CurJob;
        if (cur != null && (cur.def == JobDefOf.AttackStatic || cur.def == JobDefOf.AttackMelee) && cur.targetA.Thing == target) return;
        state.Target = target;
        state.Destination = null;
        Start(pawn, MakeAttack(pawn, target), state);
        pawn.jobs.jobQueue.EnqueueLast(MakeHold());
    }

    private static bool MeleeCanGo(Pawn pawn, Thing? target) =>
        target != null && pawn.Position.DistanceTo(target.Position) <= 12f && pawn.CanReach(target, PathEndMode.Touch, Danger.Deadly);

    private static Pawn? NearestReachableEnemy(Pawn pawn) =>
        OperativeSampler.HostilePawns(pawn).FirstOrDefault(p => MeleeCanGo(pawn, p));

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
