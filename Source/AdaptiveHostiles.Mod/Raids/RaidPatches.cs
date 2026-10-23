using AdaptiveHostiles.Core;
using HarmonyLib;
using RimWorld;
using Verse;

namespace AdaptiveHostiles.Mod.Raids;

/// <summary>
/// Raid adapter. Eligibility is decided when the incident starts; composition is replaced only
/// while that eligible incident is executing, so quests and other pawn-group requests are untouched.
/// IncidentParms.points still drives vanilla arrival/strategy resolution but never roster composition.
/// </summary>
public static class RaidAdapter
{
    public static readonly AdaptiveHostilesSettings Settings = new();

    // Exact type match excludes friendly raids, mech clusters, and custom/modded raid workers.
    public static bool IsEligible(IncidentWorker worker, IncidentParms parms) =>
        worker.GetType() == typeof(IncidentWorker_RaidEnemy)
        && parms.target is Map
        && parms.faction is { IsPlayer: false } faction
        && faction.HostileTo(Faction.OfPlayer)
        && !faction.def.hidden
        && parms.quest == null;

    public static ulong SeedFor(Map map, Faction faction)
    {
        // Deterministic from save state: replays identically for the same map, tick and faction.
        ulong h = 1469598103934665603UL;
        foreach (var v in new[] { map.uniqueID, Find.TickManager.TicksGame, faction.loadID })
            h = (h ^ (uint)v) * 1099511628211UL;
        return h;
    }

    public static bool TryCompose(RaidScope scope, PawnGroupMakerParms groupParms, out List<Pawn> pawns)
    {
        pawns = new List<Pawn>();
        var faction = groupParms.faction;
        var map = scope.Map;
        var seed = SeedFor(map, faction);

        var kinds = FactionRoster.Collect(faction);
        var colony = ColonySampler.Sample(map, faction);
        var plan = JevPlanService.GetPlan($"{faction.def.defName}:{map.uniqueID}", colony);
        plan = plan with { IncludeOperative = RosterGenerator.RollElite(seed) < Settings.OperativeChance };

        var roster = RosterGenerator.Generate(plan, FactionRoster.Classify(kinds), seed, Settings);
        if (roster.Bypassed)
        {
            Log.Warning($"[Adaptive Hostiles] {roster.Warning} Raid from {faction.Name} is a vanilla raid, not a Jev raid.");
            return false;
        }

        foreach (var entry in roster.Entries)
        {
            var request = new PawnGenerationRequest(
                kinds[entry.KindDefName], faction, PawnGenerationContext.NonPlayer, tile: map.Tile,
                mustBeCapableOfViolence: true);
            var pawn = PawnGenerator.GeneratePawn(request);
            if (entry.IsOperative)
                OperativeUpgrader.Upgrade(pawn, faction, seed, Settings);
            pawns.Add(pawn);
        }

        Log.Message($"[Adaptive Hostiles] {faction.Name} raid: {plan.Scale}/{plan.Approach}, {pawns.Count} raiders" +
                    $"{(plan.IsModelDecision ? " (Jev plan)" : " (local fallback plan)")}.");
        return true;
    }
}

[HarmonyPatch]
public static class Patch_IncidentWorker_Raid_TryExecuteWorker
{
    public static System.Reflection.MethodBase TargetMethod() =>
        AccessTools.Method(typeof(IncidentWorker_Raid), "TryExecuteWorker");

    public static void Prefix(IncidentWorker __instance, IncidentParms parms, out RaidScope? __state)
    {
        __state = RaidAdapter.IsEligible(__instance, parms) ? RaidScope.Enter(parms, (Map)parms.target) : null;
    }

    // Finalizer, not Postfix, so the scope is cleared even if the incident throws.
    public static void Finalizer(RaidScope? __state) => __state?.Exit();
}

[HarmonyPatch(typeof(PawnGroupMakerUtility), nameof(PawnGroupMakerUtility.GeneratePawns))]
public static class Patch_PawnGroupMakerUtility_GeneratePawns
{
    public static bool Prefix(PawnGroupMakerParms parms, ref IEnumerable<Pawn> __result)
    {
        var scope = RaidScope.Current;
        if (scope is null || parms.groupKind != PawnGroupKindDefOf.Combat || parms.faction != scope.Parms.faction)
            return true;
        try
        {
            if (!RaidAdapter.TryCompose(scope, parms, out var pawns)) return true;
            __result = pawns;
            return false;
        }
        catch (Exception e)
        {
            Log.Error($"[Adaptive Hostiles] Raid composition failed, using vanilla: {e}");
            return true;
        }
    }
}
