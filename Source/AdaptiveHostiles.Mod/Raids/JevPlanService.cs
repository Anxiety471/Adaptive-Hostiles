using AdaptiveHostiles.Core;

namespace AdaptiveHostiles.Mod.Raids;

/// <summary>
/// Supplies a RaidPlan without ever blocking the game thread. A fresh cached Jev plan is
/// used when present; otherwise the bounded local fallback is returned immediately and a
/// background request warms the cache for the next raid. Without TYPESAFE_API_KEY only the
/// fallback is used.
/// </summary>
public static class JevPlanService
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan MaxResultAge = TimeSpan.FromMinutes(5);

    private static readonly DecisionCache<RaidPlan> Cache = new(CacheTtl);
    private static readonly JevScheduler<RaidPlan> Scheduler = new(isModelResult: p => p.IsModelDecision);
    private static readonly RaidPlanner Fallback = new();
    private static readonly RaidPlanner? Remote = CreateRemote();

    private static RaidPlanner? CreateRemote()
    {
        var key = Environment.GetEnvironmentVariable("TYPESAFE_API_KEY");
        if (string.IsNullOrWhiteSpace(key)) return null;
        return new RaidPlanner(new JevClient(new HttpClient { Timeout = TimeSpan.FromSeconds(10) }, key));
    }

    public static bool JevEnabled => Remote is not null;

    public static RaidPlan GetPlan(string key, ColonySnapshot colony)
    {
        if (Scheduler.TryTake(key, MaxResultAge, out var fresh))
            Cache.Put(key, fresh);

        if (Cache.TryGet(key, out var cached))
            return cached;

        if (Remote is not null)
            Scheduler.TryBegin(key, ct => Remote.PlanAsync(colony, 1.0, ct));

        // eliteRoll 1.0 = no elite; the caller applies the seeded Operative roll. With no client
        // PlanAsync completes synchronously, so this does not block.
        return Fallback.PlanAsync(colony, 1.0).GetAwaiter().GetResult();
    }

    /// <summary>Call on save load / map change so stale in-flight answers are discarded.</summary>
    public static void Reset() => Scheduler.Reset();
}
