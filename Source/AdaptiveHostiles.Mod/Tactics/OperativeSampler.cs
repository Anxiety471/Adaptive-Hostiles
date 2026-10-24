using AdaptiveHostiles.Core;
using RimWorld;
using Verse;

namespace AdaptiveHostiles.Mod.Tactics;

/// <summary>Builds the snapshot Jev sees: self, visible hostiles, allies and legal destinations.</summary>
public static class OperativeSampler
{
    public const float ThreatScanRadius = 45f;
    public const float AllyRadius = 15f;
    public const int MaxThreats = 8;
    private const string TargetPrefix = "pawn_";

    public static string TargetIdFor(Pawn p) => TargetPrefix + p.ThingID;

    public static List<Pawn> HostilePawns(Pawn self)
    {
        var result = new List<Pawn>();
        if (!self.Spawned || self.Faction is null) return result;
        foreach (var p in self.Map.mapPawns.AllPawnsSpawned)
        {
            if (p == self || p.Dead || p.Downed || p.Faction is null || p.IsPrisoner || p.IsSlave) continue;
            if (!self.Faction.HostileTo(p.Faction)) continue;
            if (!p.Position.InHorDistOf(self.Position, ThreatScanRadius)) continue;
            result.Add(p);
        }
        result.Sort((a, b) => a.Position.DistanceToSquared(self.Position).CompareTo(b.Position.DistanceToSquared(self.Position)));
        return result;
    }

    public static RimWorldTacticalMap MapFor(Pawn self, List<Pawn> hostiles) =>
        new(self, hostiles.Take(MaxThreats).Select(p => p.Position).ToList());

    public static OperativeSnapshot Sample(Pawn self)
    {
        var hostiles = HostilePawns(self);
        var map = MapFor(self, hostiles);
        var range = Math.Max(self.equipment?.PrimaryEq?.PrimaryVerb?.verbProps.range ?? 0f, 2f);
        var threatCells = hostiles.Take(MaxThreats).Select(p => (p.Position.x, p.Position.z)).ToList();

        var destinations = CandidateEnumerator.Enumerate(
            map, self.Position.x, self.Position.z, threatCells, weaponRange: range);

        var targets = new List<TargetCandidate>();
        foreach (var p in hostiles)
        {
            if (targets.Count >= OperativePlanner.MaxTargets - 1) break;
            var visible = GenSight.LineOfSight(self.Position, p.Position, self.Map);
            if (!visible) continue;
            var closeness = 1f - Math.Min(1f, p.Position.DistanceTo(self.Position) / ThreatScanRadius);
            var armed = p.equipment?.Primary != null ? 0.5f : 0.2f;
            targets.Add(new TargetCandidate(TargetIdFor(p), p.Position.x, p.Position.z,
                armed + closeness * 0.5f, true));
        }

        var allies = self.Map.mapPawns.AllPawnsSpawned.Count(p =>
            p != self && !p.Dead && !p.Downed && p.Faction == self.Faction &&
            p.Position.InHorDistOf(self.Position, AllyRadius));

        return new OperativeSnapshot(
            self.ThingID, self.Position.x, self.Position.z,
            self.health.summaryHealth.SummaryHealthPercent,
            self.equipment?.Primary?.def.defName ?? "Unarmed",
            allies, destinations, targets);
    }

    /// <summary>States the Operative must not be pulled out of.</summary>
    public static PawnBlock BlockFor(Pawn p)
    {
        var block = PawnBlock.None;
        if (p.Dead || p.Destroyed) block |= PawnBlock.Dead;
        if (p.Downed) block |= PawnBlock.Downed;
        if (p.stances?.stunner?.Stunned == true) block |= PawnBlock.Stunned;
        if (p.InMentalState) block |= PawnBlock.MentalBreak;

        // Name checks keep this robust against duty/job defs renamed by DLC or other mods.
        // Fleeing is deliberately not a block: the base game's flee state is ignored and Jev
        // decides whether a raider leaves, so a vanilla exit duty must not freeze the tactical loop.
        var duty = p.mindState?.duty?.def?.defName ?? "";
        if (duty.Contains("Steal") || duty.Contains("Kidnap"))
            block |= PawnBlock.ScriptedDuty;
        var job = p.CurJob;
        if (job != null && job.playerForced)
            block |= PawnBlock.ForcedJob;
        return block;
    }
}
