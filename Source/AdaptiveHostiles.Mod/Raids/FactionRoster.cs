using AdaptiveHostiles.Core;
using RimWorld;
using Verse;

namespace AdaptiveHostiles.Mod.Raids;

/// <summary>Finds a faction's usable combat PawnKindDefs and classifies them for the Core roster generator.</summary>
public static class FactionRoster
{
    public static Dictionary<string, PawnKindDef> Collect(Faction faction)
    {
        var result = new Dictionary<string, PawnKindDef>(StringComparer.Ordinal);
        foreach (var maker in faction.def.pawnGroupMakers ?? new List<PawnGroupMaker>())
        {
            if (maker.kindDef != PawnGroupKindDefOf.Combat) continue;
            foreach (var option in maker.options)
            {
                var kind = option.kind;
                if (kind?.RaceProps is { Humanlike: true } && kind.isFighter && !kind.factionLeader)
                    result[kind.defName] = kind;
            }
        }
        return result;
    }

    public static List<RosterKind> Classify(IReadOnlyDictionary<string, PawnKindDef> kinds)
    {
        var maxPower = kinds.Count == 0 ? 0f : kinds.Values.Max(k => k.combatPower);
        var list = new List<RosterKind>(kinds.Count);
        foreach (var kind in kinds.Values)
        {
            var tags = kind.weaponTags;
            var melee = tags is { Count: > 0 } && tags.All(t => t.Contains("Melee"));
            var role = melee ? RosterRole.Melee
                : kinds.Count > 2 && kind.combatPower >= maxPower * 0.8f ? RosterRole.Heavy
                : RosterRole.Ranged;
            list.Add(new RosterKind(kind.defName, role));
        }
        return list;
    }
}
