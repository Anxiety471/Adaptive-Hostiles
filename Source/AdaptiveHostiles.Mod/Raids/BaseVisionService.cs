using AdaptiveHostiles.Core;
using Verse;

namespace AdaptiveHostiles.Mod.Raids;

/// <summary>
/// Reads the colony base picture with Jev in the background and keeps the answer. The squad calls <see cref="WeakWall"/>
/// every cycle: it only ever returns the cached reading and, when none is fresh, starts a request without waiting for it.
/// A failed request backs off (JevScheduler), and the squad simply goes on without the extra candidate.
/// </summary>
public static class BaseVisionService
{
    private const int RefreshTicks = 15000;

    private static readonly JevScheduler<BaseVisionReading> Scheduler = new(
        new AdaptiveHostilesSettings { MaxConcurrentRequests = 1 });
    private static readonly Dictionary<string, (BaseVisionReading Reading, int Tick)> Latest = new();

    /// <summary>Bumped whenever a new reading arrives, so a squad can recompute its breach points to include it.</summary>
    public static int Version { get; private set; }

    /// <summary>The weakest outer wall cell in Jev's latest reading of this map, or null while there is none.</summary>
    public static IntVec3? WeakWall(Map map)
    {
        if (!AdaptiveHostilesMod.BaseImagesEnabled || JevPlanService.SharedClient is not IImageDecisionClient client) return null;
        var key = map.uniqueID.ToString();
        var now = Find.TickManager.TicksGame;

        if (Scheduler.TryTake(key, TimeSpan.FromMinutes(5), out var fresh))
        {
            Latest[key] = (fresh, now);
            Version++;
            Log.Message(fresh.TryWallCell(out var wx, out var wz)
                ? $"[Adaptive Hostiles] Jev base image: sealed {fresh.Result.Sealed:0.00}, weakest wall near ({wx},{wz})."
                : $"[Adaptive Hostiles] Jev base image: sealed {fresh.Result.Sealed:0.00}, no wall hint.");
        }

        var stale = !Latest.TryGetValue(key, out var kept) || now - kept.Tick > RefreshTicks;
        if (stale && Scheduler.CanBegin(key) && BaseMapRenderer.Render(map) is { } image)
            Scheduler.TryBegin(key, ct => BaseVision.AskAsync(client, image, ct));

        return Latest.TryGetValue(key, out var reading) && reading.Reading.TryWallCell(out var x, out var z)
            ? new IntVec3(x, 0, z)
            : null;
    }

    public static void Reset()
    {
        Scheduler.Reset();
        Latest.Clear();
    }
}
