using AdaptiveHostiles.Core;
using AdaptiveHostiles.Mod.Raids;
using RimWorld;
using Verse;

namespace AdaptiveHostiles.Mod.Tactics;

/// <summary>
/// Game-thread entry point for the Operative tactical loop. Called from the game component;
/// finds each registered Operative's live pawn, adapts it to <see cref="IOperativeHost"/> and
/// lets <see cref="OperativeTacticalLoop"/> decide. Network work never runs here.
/// </summary>
public static class OperativeDriver
{
    private const int PollInterval = 10;

    private static readonly AdaptiveHostilesSettings Settings = RaidAdapter.Settings;
    private static readonly OperativeTacticalLoop Loop = new(
        new OperativePlanner(JevPlanService.SharedClient),
        JevPlanService.SharedClient is null ? null : new JevScheduler<OperativeDecision>(
            Settings, d => d.IsModelDecision),
        Settings);
    private static readonly Dictionary<string, OperativeRuntimeState> States = new(StringComparer.Ordinal);

    public static void Reset()
    {
        Loop.Reset();
        States.Clear();
    }

    public static void Tick(OperativeRegistry registry)
    {
        var tick = Find.TickManager.TicksGame;
        if (tick % PollInterval != 0 || registry.All.Count == 0) return;

        var live = new Dictionary<string, Pawn>(StringComparer.Ordinal);
        foreach (var map in Find.Maps)
            foreach (var p in map.mapPawns.AllPawnsSpawned)
                if (registry.TryGet(p.ThingID, out _)) live[p.ThingID] = p;

        foreach (var record in registry.All.ToList())
        {
            if (!live.TryGetValue(record.PawnId, out var pawn)) continue;
            if (!States.TryGetValue(record.PawnId, out var state))
                States[record.PawnId] = state = new OperativeRuntimeState { LastHealth = pawn.health.summaryHealth.SummaryHealthPercent };
            try
            {
                Loop.Tick(record, new Host(pawn, state), tick, SeedFor(record.PawnId));
            }
            catch (Exception e)
            {
                // A tactical bug must never break the raid; the pawn falls back to vanilla AI.
                Log.Warning($"[Adaptive Hostiles] Operative loop failed for {pawn.LabelShort}: {e.Message}");
                Loop.Reset();
                state.Job = null;
            }
        }
    }

    private static ulong SeedFor(string id)
    {
        var h = 14695981039346656037UL; // FNV-1a: stable across runs, unlike string.GetHashCode
        foreach (var ch in id) { h ^= ch; h *= 1099511628211UL; }
        return h;
    }

    private sealed class Host(Pawn pawn, OperativeRuntimeState state) : IOperativeHost
    {
        public PawnBlock Block => OperativeSampler.BlockFor(pawn);

        public TacticalTrigger Trigger
        {
            get
            {
                var trigger = TacticalTrigger.None;
                var health = pawn.health.summaryHealth.SummaryHealthPercent;
                if (health < state.LastHealth - 0.05f) trigger |= TacticalTrigger.Damaged;
                state.LastHealth = health;

                if (state.Target is { Dead: true } or { Downed: true } or { Spawned: false })
                {
                    trigger |= TacticalTrigger.TargetLost;
                    state.Target = null;
                }

                if (state.Destination is { } dest && state.Job != null && pawn.CurJob != state.Job &&
                    pawn.Position.DistanceTo(dest) > 2f)
                    trigger |= TacticalTrigger.RouteBlocked;
                if (pawn.CurJob != state.Job) state.Destination = null;
                return trigger;
            }
        }

        public OperativeSnapshot Sample() => OperativeSampler.Sample(pawn);

        public ITacticalMap CurrentMap => OperativeSampler.MapFor(pawn, OperativeSampler.HostilePawns(pawn));

        public void Apply(OperativeOrder order)
        {
            Log.Message($"[Adaptive Hostiles] {pawn.LabelShort}: {order.Kind} ({order.X},{order.Z}) {order.Stance} - {order.Reason}");
            OperativeJobExecutor.Apply(pawn, order, state);
        }
    }
}
