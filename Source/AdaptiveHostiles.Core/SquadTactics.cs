namespace AdaptiveHostiles.Core;

/// <summary>A place where raiders recently went down, 0..1 strength.</summary>
public sealed record DangerZone(int X, int Z, float Strength);

/// <summary>
/// Remembers where raiders were downed or killed. A mark fades linearly over one in-game day (60000 ticks) and
/// penalises nearby candidate cells and breach points so the squad stops walking into the same kill zone.
/// </summary>
public sealed class DangerMemory
{
    public const long DecayTicks = 60000;
    public const int Radius = 6;
    private readonly List<(int X, int Z, long Tick)> _marks = new();

    public int Count => _marks.Count;

    public void Record(int x, int z, long tick) => _marks.Add((x, z, tick));

    public void Clear() => _marks.Clear();

    /// <summary>0..1: summed strength of marks within <see cref="Radius"/>, nearer and fresher counting more.</summary>
    public float Penalty(int x, int z, long now)
    {
        var sum = 0f;
        foreach (var (mx, mz, tick) in _marks)
        {
            var age = now - tick;
            if (age >= DecayTicks) continue;
            var d = Math.Sqrt((x - mx) * (double)(x - mx) + (z - mz) * (double)(z - mz));
            if (d > Radius) continue;
            sum += (float)((1.0 - age / (double)DecayTicks) * (1.0 - d / (Radius + 1.0)));
        }
        return Math.Min(1f, sum);
    }

    /// <summary>The strongest distinct danger zones, for the model; nearby marks merge into the freshest.</summary>
    public IReadOnlyList<DangerZone> Hotspots(long now, int max)
    {
        _marks.RemoveAll(m => now - m.Tick >= DecayTicks);
        var zones = new List<DangerZone>();
        foreach (var (x, z, tick) in _marks.OrderByDescending(m => m.Tick))
        {
            if (zones.Any(zn => Math.Abs(zn.X - x) + Math.Abs(zn.Z - z) <= Radius)) continue;
            zones.Add(new DangerZone(x, z, Math.Max(Penalty(x, z, now), 0.01f)));
        }
        return zones.OrderByDescending(zn => zn.Strength).Take(max).ToList();
    }
}

/// <summary>
/// Notices a stalled assault: no damage dealt and no distance gained to the chosen breach for
/// <see cref="StallCycles"/> cycles. The stalled breach is set aside so the next point is chosen; when every point has
/// stalled, ranged raiders are told to open a second wall.
/// </summary>
public sealed class StallTracker
{
    public const int StallCycles = 15; // about 1500 ticks at the default one-second cadence
    public const double MinDamage = 0.001;
    public const double MinDistanceGain = 1.5;
    private readonly HashSet<string> _excluded = new(StringComparer.Ordinal);
    private string? _key;
    private double _best;
    private int _idle;

    /// <summary>True once every breach point has stalled; stays set for the rest of the raid.</summary>
    public bool RangedOpensWall { get; private set; }

    public bool IsExcluded(string key) => _excluded.Contains(key);

    /// <summary>Breach points that have not stalled; all of them when none are left.</summary>
    public IReadOnlyList<T> Available<T>(IReadOnlyList<T> points, Func<T, string> keyOf)
    {
        var open = points.Where(p => !_excluded.Contains(keyOf(p))).ToList();
        return open.Count == 0 ? points : open;
    }

    /// <summary>One assault cycle. Returns true when this cycle stalled the breach <paramref name="key"/>.</summary>
    public bool Observe(string key, double damage, double closest, IReadOnlyCollection<string> allKeys)
    {
        if (key != _key)
        {
            _key = key;
            _best = closest;
            _idle = 0;
            return false;
        }
        var progressed = damage > MinDamage || closest < _best - MinDistanceGain;
        if (closest < _best) _best = closest;
        if (progressed)
        {
            _idle = 0;
            return false;
        }
        if (++_idle < StallCycles) return false;
        _idle = 0;
        _excluded.Add(key);
        if (allKeys.All(_excluded.Contains))
        {
            _excluded.Clear();
            RangedOpensWall = true;
        }
        return true;
    }
}

/// <summary>With enough raiders against a thin defence, a small group breaks in somewhere else. Decided once per raid.</summary>
public static class SquadSplit
{
    public const int MinRaiders = 6;
    public const int GroupSize = 2;

    /// <summary>
    /// The raiders to send and the breach point they take: the weakest point other than the main one. Needs 6+ raiders,
    /// a second breach point and at most one defender per two raiders. The Operative stays with the main group, and the
    /// group is one melee and one ranged raider when possible.
    /// </summary>
    public static (IReadOnlyList<string> PawnIds, string BreachId)? Choose(
        IReadOnlyList<SquadMember> members, IReadOnlyList<BreachPoint> breaches, string mainBreachId, int defenders)
    {
        if (members.Count < MinRaiders || defenders * 2 > members.Count) return null;
        var other = breaches.Where(b => b.Id != mainBreachId)
            .OrderByDescending(b => b.Weakness).ThenBy(b => b.Id, StringComparer.Ordinal).FirstOrDefault();
        if (other is null) return null;

        var pool = members.Where(m => !m.IsOperative).ToList();
        var group = new List<SquadMember>();
        var melee = pool.LastOrDefault(m => m.IsMelee);
        var ranged = pool.LastOrDefault(m => !m.IsMelee);
        if (melee is not null) group.Add(melee);
        if (ranged is not null) group.Add(ranged);
        foreach (var m in pool.AsEnumerable().Reverse())
        {
            if (group.Count >= GroupSize) break;
            if (!group.Contains(m)) group.Add(m);
        }
        return group.Count < GroupSize ? null : (group.Select(m => m.Snapshot.PawnId).ToList(), other.Id);
    }
}

/// <summary>One short sentence per phase change for the mod log. Never shown in game.</summary>
public static class IntentLog
{
    public static string Describe(SquadPhase phase, SquadDecision decision, SquadSnapshot squad)
    {
        var melee = squad.Members.Count(m => m.IsMelee);
        var ranged = squad.Members.Count - melee;
        var breach = squad.Breaches.FirstOrDefault(b => b.Id == decision.BreachId);
        var where = breach is null
            ? "no breach point, attacking where the enemy is"
            : $"breach {breach.Id} at ({breach.X},{breach.Z})" + (breach.WallX >= 0 ? $" through the wall at ({breach.WallX},{breach.WallZ})" : "");
        var verb = phase switch
        {
            SquadPhase.Gather => "gathering at the staging cells for",
            SquadPhase.Breach => decision.MeleeHold ? "melee pushing (holding the last stretch until ranged are in position), ranged covering at" : "melee pushing, ranged covering the push at",
            _ => "pressing the assault through"
        };
        return $"{squad.Members.Count} raiders ({melee} melee, {ranged} ranged) {verb} {where}; {squad.Down}/{squad.Sent} down";
    }
}
