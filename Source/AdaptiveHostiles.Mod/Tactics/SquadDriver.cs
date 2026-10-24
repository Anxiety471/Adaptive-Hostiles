using AdaptiveHostiles.Core;
using AdaptiveHostiles.Mod.Raids;
using RimWorld;
using Verse;
using Verse.AI.Group;

namespace AdaptiveHostiles.Mod.Tactics;

/// <summary>
/// Game-thread entry point for squad control. Every raid that contains a registered Operative is
/// commanded as one squad: a single decision per cycle orders each spawned raider, so Jev
/// coordinates the whole attack. The base game's flee check is suppressed for these raiders
/// (see FleePatches); only a Jev "flee" order sends an ordinary raider off the map.
/// </summary>
public static class SquadDriver
{
    private const int PollInterval = 10;
    /// <summary>After this long the vanilla exit logic is allowed again so a raid can never hang forever.</summary>
    private const int MaxSuppressTicks = 30000;

    // Longer cooldown and commitment than a lone Operative so raiders reach their cells before the next order.
    private static readonly AdaptiveHostilesSettings Settings = RaidAdapter.Settings with
    {
        MinDecisionCooldownTicks = 360,
        MaxDecisionCooldownTicks = 600,
        MinCommitmentTicks = 360
    };
    private static readonly SquadLoop Loop = new(
        new SquadPlanner(JevPlanService.SharedClient),
        JevPlanService.SharedClient is null ? null : new JevScheduler<SquadDecision>(Settings, d => d.IsModelDecision),
        Settings);
    private static readonly Dictionary<string, OperativeRuntimeState> States = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, OperativeRecord> Timings = new(StringComparer.Ordinal);
    /// <summary>Pawn id -> start tick of the raid that controls it.</summary>
    private static readonly Dictionary<string, long> Controlled = new(StringComparer.Ordinal);

    public static bool Controls(string pawnId) => Controlled.ContainsKey(pawnId);

    public static bool IsControlled(Pawn pawn) => Controlled.ContainsKey(pawn.ThingID);

    /// <summary>True when vanilla may not send this lord's pawns away; Jev decides who flees.</summary>
    public static bool SuppressesFlee(Lord lord)
    {
        var now = Find.TickManager.TicksGame;
        foreach (var p in lord.ownedPawns)
            if (Controlled.TryGetValue(p.ThingID, out var start) && now - start < MaxSuppressTicks)
                return true;
        return false;
    }

    public static void Reset()
    {
        Loop.Reset();
        States.Clear();
        Timings.Clear();
        Controlled.Clear();
    }

    public static void Tick(OperativeRegistry registry, IReadOnlyList<TrackedRaid> raids)
    {
        var tick = Find.TickManager.TicksGame;
        if (tick % PollInterval != 0) return;

        Controlled.Clear();
        foreach (var raid in raids)
        {
            var members = raid.Raiders
                .Where(p => p is { Spawned: true, Dead: false } && p.Faction != null && p.HostileTo(Faction.OfPlayer))
                .Cast<Pawn>().ToList();
            if (!members.Any(p => registry.TryGet(p.ThingID, out _))) continue;
            foreach (var p in members) Controlled[p.ThingID] = raid.StartTick;

            if (!Timings.TryGetValue(raid.Id, out var timing))
                Timings[raid.Id] = timing = new OperativeRecord(raid.Id, "squad");
            try
            {
                Loop.Tick(raid.Id, new Host(raid, members, registry, timing), tick, SeedFor(raid.Id));
            }
            catch (Exception e)
            {
                // A tactical bug must never break the raid; its pawns fall back to vanilla AI.
                Log.Warning($"[Adaptive Hostiles] Squad loop failed for raid {raid.Id}: {e.Message}");
                Loop.Abort(raid.Id);
            }
        }
    }

    /// <summary>Nothing is standable or reachable, so every destination is rejected and the pawn is left alone.</summary>
    private sealed class ClosedMap : ITacticalMap
    {
        public static readonly ClosedMap Instance = new();
        public bool IsStandable(int x, int z) => false;
        public bool IsReachable(int x, int z) => false;
        public float CoverAt(int x, int z) => 0f;
        public float ExposureAt(int x, int z) => 0f;
    }

    private static ulong SeedFor(string id)
    {
        var h = 14695981039346656037UL; // FNV-1a: stable across runs
        foreach (var ch in id) { h ^= ch; h *= 1099511628211UL; }
        return h;
    }

    private static OperativeRuntimeState StateOf(Pawn p)
    {
        if (!States.TryGetValue(p.ThingID, out var s))
            States[p.ThingID] = s = new OperativeRuntimeState { LastHealth = p.health.summaryHealth.SummaryHealthPercent };
        return s;
    }

    private sealed class Host(TrackedRaid raid, List<Pawn> members, OperativeRegistry registry, OperativeRecord timing)
        : ISquadHost
    {
        private readonly Dictionary<string, Pawn> byId = members.ToDictionary(p => p.ThingID, StringComparer.Ordinal);

        public OperativeRecord Timing => timing;

        public TacticalTrigger Trigger
        {
            get
            {
                var trigger = TacticalTrigger.None;
                foreach (var p in members)
                {
                    var state = StateOf(p);
                    var health = p.health.summaryHealth.SummaryHealthPercent;
                    if (health < state.LastHealth - 0.05f) trigger |= TacticalTrigger.Damaged;
                    state.LastHealth = health;
                }
                return trigger;
            }
        }

        public PawnBlock BlockOf(string pawnId)
        {
            if (!byId.TryGetValue(pawnId, out var pawn)) return PawnBlock.Dead;
            var block = OperativeSampler.BlockFor(pawn);
            var state = StateOf(pawn);
            if (state.Fleeing)
            {
                // A Jev-ordered exit is left alone until the job ends.
                if (pawn.CurJob == state.Job) block |= PawnBlock.ScriptedDuty;
                else state.Fleeing = false;
            }
            return block;
        }

        public SquadSnapshot Sample()
        {
            // Raiders with no enemy in range stay on vanilla assault AI so they keep marching on the colony.
            var ready = members.Where(p => BlockOf(p.ThingID) == PawnBlock.None)
                .Where(p => OperativeSampler.HostilePawns(p).Count > 0 || OperativeSampler.HostileTurrets(p).Count > 0)
                .Select(p => (Pawn: p, IsOp: registry.TryGet(p.ThingID, out _),
                    Dist: OperativeSampler.HostilePawns(p).FirstOrDefault()?.Position.DistanceTo(p.Position) ?? float.MaxValue))
                .OrderByDescending(x => x.IsOp).ThenBy(x => x.Dist)
                .Take(SquadPlanner.MaxMembers)
                .ToList();
            raid.Status(out var sent, out var down, out var retreated);
            if (ready.Count == 0) return new SquadSnapshot(raid.Id, new List<SquadMember>(), sent, down, retreated);

            // Where the colony is weakest, found once for the whole squad from the lead raider's map view.
            var lead = ready[0].Pawn;
            var defenders = ready.SelectMany(x => OperativeSampler.HostilePawns(x.Pawn)).Distinct().Take(16).ToList();
            var turrets = OperativeSampler.HostileTurrets(lead);
            var centre = BreachFinder.DefenderCentre(defenders, turrets);
            var leadMap = OperativeSampler.MapFor(lead, defenders);
            var breaches = BreachFinder.Find(lead, leadMap, defenders, turrets, centre);

            var used = new HashSet<(int, int)>();
            var squad = new List<SquadMember>();
            for (var i = 0; i < ready.Count; i++)
            {
                var (pawn, isOp, _) = ready[i];
                var melee = pawn.equipment?.Primary is null || pawn.equipment.Primary.def.IsMeleeWeapon;
                var snap = OperativeSampler.Sample(pawn);
                if (breaches.Count > 0)
                {
                    var range = Math.Max(pawn.equipment?.PrimaryEq?.PrimaryVerb?.verbProps.range ?? 0f, 2f);
                    var roleCells = BreachFinder.RoleCells(
                        pawn, OperativeSampler.MapFor(pawn, defenders), breaches, centre, melee, range, i, used);
                    snap = snap with { Destinations = roleCells.Concat(snap.Destinations).ToList() };
                }
                squad.Add(new SquadMember(snap, isOp, melee));
            }
            return new SquadSnapshot(raid.Id, squad, sent, down, retreated, breaches);
        }

        public ITacticalMap CurrentMap(string pawnId)
        {
            // A raider can die or be removed while Jev's answer was in flight; revalidation must not throw.
            if (!byId.TryGetValue(pawnId, out var pawn) || !pawn.Spawned) return ClosedMap.Instance;
            return OperativeSampler.MapFor(pawn, OperativeSampler.HostilePawns(pawn));
        }

        public OperativeRecord? RecordOf(string pawnId) => registry.TryGet(pawnId, out var r) ? r : null;

        public void Apply(string pawnId, OperativeOrder order)
        {
            if (!byId.TryGetValue(pawnId, out var pawn)) return;
            Log.Message($"[Adaptive Hostiles] squad {pawn.LabelShort}: {order.Kind} ({order.X},{order.Z}) {order.Stance} - {order.Reason}");
            OperativeJobExecutor.Apply(pawn, order, StateOf(pawn));
        }
    }
}
