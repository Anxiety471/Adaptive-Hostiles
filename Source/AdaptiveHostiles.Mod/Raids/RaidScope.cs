using Verse;

namespace AdaptiveHostiles.Mod.Raids;

/// <summary>
/// Marks the span of one eligible hostile raid's TryExecuteWorker so the pawn-generation
/// patch only replaces composition for that raid and never for quests or other incidents.
/// </summary>
public sealed class RaidScope(RimWorld.IncidentParms parms, Map map)
{
    [ThreadStatic] private static RaidScope? _current;

    public RimWorld.IncidentParms Parms { get; } = parms;
    public Map Map { get; } = map;
    public RaidScope? Previous { get; private set; }

    public static RaidScope? Current => _current;

    public static RaidScope Enter(RimWorld.IncidentParms parms, Map map)
    {
        var scope = new RaidScope(parms, map) { Previous = _current };
        _current = scope;
        return scope;
    }

    public void Exit() => _current = Previous;
}
