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
    private const int BreachKeepTicks = 6000;
    private static readonly Dictionary<string, (int Tick, List<BreachPoint> Points)> BreachMemory = new();
    /// <summary>Where raiders went down, shared by every raid until the game is reloaded.</summary>
    private static readonly DangerMemory Danger = new();
    private static readonly Dictionary<string, RaidWatch> Watches = new(StringComparer.Ordinal);

    /// <summary>Per-raid bookkeeping for danger recording and the damage-dealt measure.</summary>
    private sealed class RaidWatch
    {
        public readonly HashSet<string> Fallen = new(StringComparer.Ordinal);
        public readonly Dictionary<string, (Pawn Pawn, float Health)> Defenders = new(StringComparer.Ordinal);
        public readonly Dictionary<string, float> Walls = new(StringComparer.Ordinal);
    }

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
        BreachMemory.Clear();
        Watches.Clear();
        Danger.Clear();
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
            // Until someone sees the colony the raid stays on vanilla assault AI and keeps marching. From then on every
            // raider that can be ordered is in the squad, including ones that lost sight of the enemy, so none roams alone.
            var ordered = members.Where(p => BlockOf(p.ThingID) == PawnBlock.None)
                .Select(p => (Pawn: p, IsOp: registry.TryGet(p.ThingID, out _),
                    Dist: OperativeSampler.ColonyPawns(p).FirstOrDefault()?.Position.DistanceTo(p.Position) ?? float.MaxValue,
                    Turrets: OperativeSampler.HostileTurrets(p).Count > 0))
                .ToList();
            raid.Status(out var sent, out var down, out var retreated);
            if (!ordered.Any(x => x.Dist < float.MaxValue || x.Turrets))
                return new SquadSnapshot(raid.Id, new List<SquadMember>(), sent, down, retreated);
            var ready = ordered.OrderByDescending(x => x.IsOp).ThenBy(x => x.Pawn.thingIDNumber).Take(SquadPlanner.MaxMembers)
                .Select(x => (x.Pawn, x.IsOp, x.Dist)).ToList();

            // Where the colony is weakest, found once for the whole squad from the lead raider's map view.
            var lead = ready.OrderBy(x => x.Dist).First().Pawn;
            var defenders = ready.SelectMany(x => OperativeSampler.ColonyPawns(x.Pawn)).Distinct().Take(16).ToList();
            var hostiles = ready.SelectMany(x => OperativeSampler.HostilePawns(x.Pawn)).Distinct().Take(16).ToList();
            var turrets = OperativeSampler.HostileTurrets(lead);
            var centre = BreachFinder.DefenderCentre(defenders, turrets);
            var leadMap = OperativeSampler.MapFor(lead, hostiles);
            // The breach point is chosen once per raid and kept for a while: recomputing it from whichever defenders
            // happen to be visible each cycle made the squad chase a different breach every order.
            var now = Find.TickManager.TicksGame;
            if (!Watches.TryGetValue(raid.Id, out var watch)) Watches[raid.Id] = watch = new RaidWatch();
            var lostNew = RecordLosses(watch, now);
            if (!BreachMemory.TryGetValue(raid.Id, out var kept) || now - kept.Tick > BreachKeepTicks || kept.Points.Count == 0 || lostNew || WallFell(kept.Points, lead.Map))
                BreachMemory[raid.Id] = kept = (now, BreachFinder.Find(lead, leadMap, defenders, turrets, centre, Danger, now));
            var breaches = kept.Points;
            if (kept.Tick == now)
                Log.Message($"[Adaptive Hostiles] breach points for raid {raid.Id}: " + string.Join("; ", breaches.Select(b =>
                    $"{b.Id} stand ({b.X},{b.Z}) {(b.WallX >= 0 ? $"wall ({b.WallX},{b.WallZ})" : "open")} weakness {b.Weakness:0.00}")));

            // A squad with no melee raider has nobody to push the breach, so its ranged raiders take that role.
            var noMelee = ready.All(x => x.Pawn.equipment?.Primary is { } w && !w.def.IsMeleeWeapon);
            var used = new HashSet<(int, int)>();
            var squad = new List<SquadMember>();
            for (var i = 0; i < ready.Count; i++)
            {
                var (pawn, isOp, _) = ready[i];
                var melee = noMelee || pawn.equipment?.Primary is null || pawn.equipment.Primary.def.IsMeleeWeapon;
                var snap = OperativeSampler.Sample(pawn);
                if (breaches.Count > 0)
                {
                    var range = Math.Max(pawn.equipment?.PrimaryEq?.PrimaryVerb?.verbProps.range ?? 0f, 2f);
                    var roleCells = BreachFinder.RoleCells(
                        pawn, OperativeSampler.MapFor(pawn, hostiles), breaches, centre, melee, range, i, used);
                    snap = snap with { Destinations = roleCells.Concat(snap.Destinations).ToList() };
                }
                snap = snap with
                {
                    Destinations = snap.Destinations.Select(d => d with { Danger = Danger.Penalty(d.X, d.Z, now) }).ToList()
                };
                squad.Add(new SquadMember(snap, isOp, melee));
            }
            return new SquadSnapshot(raid.Id, squad, sent, down, retreated, breaches,
                DangerZones: Danger.Hotspots(now, 5), DamageDealt: MeasureDamage(watch, defenders, breaches), Defenders: defenders.Count);
        }

        // A pinned breach whose wall or door has been destroyed is stale: the squad has to find the next one on the path.
        private static bool WallFell(List<BreachPoint> points, Map map) =>
            points.Any(b => b.WallX >= 0 && new IntVec3(b.WallX, 0, b.WallZ).GetEdifice(map) is null);

        // Raiders that went down or died since the last cycle mark their cell as dangerous. True when there was one.
        private bool RecordLosses(RaidWatch watch, int now)
        {
            var any = false;
            foreach (var p in raid.Raiders)
            {
                if (p is null || !(p.Dead || p.Downed) || !watch.Fallen.Add(p.ThingID)) continue;
                var cell = p.PositionHeld;
                Danger.Record(cell.x, cell.z, now);
                Log.Message($"[Adaptive Hostiles] danger: {p.LabelShort} {(p.Dead ? "died" : "went down")} at ({cell.x},{cell.z}).");
                any = true;
            }
            return any;
        }

        // Defender health lost plus breach wall damage since the last cycle, as a sum of fractions; 0 means no progress.
        private static double MeasureDamage(RaidWatch watch, List<Pawn> defenders, List<BreachPoint> breaches)
        {
            double damage = 0;
            foreach (var (_, (pawn, health)) in watch.Defenders.ToList())
                if (pawn.Dead || pawn.Downed) damage += health;
            foreach (var d in defenders)
            {
                var hp = d.health.summaryHealth.SummaryHealthPercent;
                if (watch.Defenders.TryGetValue(d.ThingID, out var prev)) damage += Math.Max(0, prev.Health - hp);
                watch.Defenders[d.ThingID] = (d, hp);
            }
            foreach (var key in watch.Defenders.Where(kv => kv.Value.Pawn.Dead || kv.Value.Pawn.Downed).Select(kv => kv.Key).ToList())
                watch.Defenders.Remove(key);
            foreach (var bp in breaches.Where(b => b.WallX >= 0))
            {
                var wall = bp.WallX < 0 || Find.CurrentMap is null
                    ? null : new IntVec3(bp.WallX, 0, bp.WallZ).GetEdifice(Find.CurrentMap);
                var id = $"{bp.WallX},{bp.WallZ}";
                var hp = wall is null || wall.Destroyed ? 0f : wall.HitPoints / (float)Math.Max(1, wall.MaxHitPoints);
                if (watch.Walls.TryGetValue(id, out var before) && hp < before) damage += before - hp;
                watch.Walls[id] = hp;
            }
            return damage;
        }

        public void Callout(string text) => Log.Message($"[Adaptive Hostiles] intent (raid {raid.Id}): {text}");

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
            Log.Message($"[Adaptive Hostiles] t{Find.TickManager.TicksGame} squad {pawn.LabelShort} [{(pawn.equipment?.Primary is null || pawn.equipment.Primary.def.IsMeleeWeapon ? "melee" : "ranged")} at ({pawn.Position.x},{pawn.Position.z}) hp {pawn.health.summaryHealth.SummaryHealthPercent:0.00}]: {order.Kind} ({order.X},{order.Z}) {order.Stance} - {order.Reason}");
            OperativeJobExecutor.Apply(pawn, order, StateOf(pawn), PinnedWall(pawn, order.Reason));
        }

        // The wall or door of the breach point named in a breach_/support_ order, while it still stands.
        private Thing? PinnedWall(Pawn pawn, string reason)
        {
            var us = reason.IndexOf('_');
            if (us < 0 || !BreachMemory.TryGetValue(raid.Id, out var kept)) return null;
            var id = reason[(us + 1)..];
            var bp = kept.Points.FirstOrDefault(p => p.Id == id);
            if (bp is null || bp.WallX < 0) return null;
            return WallBreaker.BlockerAt(pawn, new IntVec3(bp.WallX, 0, bp.WallZ));
        }
    }
}
