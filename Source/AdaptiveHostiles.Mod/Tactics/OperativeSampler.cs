using AdaptiveHostiles.Core;
using RimWorld;
using Verse;

namespace AdaptiveHostiles.Mod.Tactics;

/// <summary>Builds the snapshot Jev sees: self, visible hostiles, allies and legal destinations.</summary>
public static class OperativeSampler
{
    public const float ThreatScanRadius = 60f;
    public const float AllyRadius = 15f;
    public const int MaxThreats = 8;
    /// <summary>Base threat of a non-colony hostile; at least any colonist, so it is engaged first.</summary>
    public const float EnemyThreat = 1.0f;
    private const string TargetPrefix = "pawn_";

    public static string TargetIdFor(Thing t) => TargetPrefix + t.ThingID;

    public static List<Pawn> HostilePawns(Pawn self)
    {
        var result = new List<Pawn>();
        if (!self.Spawned || self.Faction is null) return result;
        foreach (var p in self.Map.mapPawns.AllPawnsSpawned)
        {
            if (p == self || p.Dead || p.Downed || p.Faction is null || p.IsPrisoner || p.IsSlave) continue;
            if (!self.Faction.HostileTo(p.Faction)) continue;
            // The colony's pets are not defenders: raiders shot a husky outside the wall instead of supporting the push.
            if (p.RaceProps.Animal && p.Faction == Faction.OfPlayer) continue;
            if (p.GetComp<CompCanBeDormant>() is { Awake: false }) continue; // a sleeping mech cluster is not a threat yet
            if (!p.Position.InHorDistOf(self.Position, ThreatScanRadius)) continue;
            result.Add(p);
        }
        result.Sort((a, b) => a.Position.DistanceToSquared(self.Position).CompareTo(b.Position.DistanceToSquared(self.Position)));
        return result;
    }

    /// <summary>The colony's own pawns among the hostiles: these are what a raid breaches towards. Mechs and other factions are not.</summary>
    public static List<Pawn> ColonyPawns(Pawn self) => HostilePawns(self).Where(IsColony).ToList();

    public static bool IsColony(Pawn p) => p.Faction == Faction.OfPlayer;

    /// <summary>Armed hostile turrets within scan range; a turret is a threat that is not a pawn.</summary>
    public static List<Building_Turret> HostileTurrets(Pawn self)
    {
        var result = new List<Building_Turret>();
        if (!self.Spawned || self.Faction is null) return result;
        foreach (var t in self.Map.listerThings.ThingsInGroup(ThingRequestGroup.BuildingArtificial))
        {
            if (t is not Building_Turret turret || !turret.Spawned || turret.Faction is null) continue;
            if (!self.Faction.HostileTo(turret.Faction)) continue;
            if (!turret.Position.InHorDistOf(self.Position, ThreatScanRadius)) continue;
            result.Add(turret);
        }
        return result;
    }

    public static float TurretRange(Building_Turret t) => t.AttackVerb?.verbProps.range ?? 0f;

    /// <summary>How far a pawn can hit: its ranged weapon's range, or 0 when it fights in melee (so kiting can tell them apart).</summary>
    public static float WeaponReach(Pawn p) =>
        p.equipment?.Primary is { } w && !w.def.IsMeleeWeapon ? p.equipment.PrimaryEq?.PrimaryVerb?.verbProps.range ?? 0f : 0f;

    public static RimWorldTacticalMap MapFor(Pawn self, List<Pawn> hostiles) =>
        new(self, hostiles.Take(MaxThreats).Select(p => p.Position).ToList(),
            HostileTurrets(self).Select(t => (t.Position, TurretRange(t))).ToList());

    public static OperativeSnapshot Sample(Pawn self)
    {
        var hostiles = HostilePawns(self);
        var map = MapFor(self, hostiles);
        var range = Math.Max(self.equipment?.PrimaryEq?.PrimaryVerb?.verbProps.range ?? 0f, 2f);
        var threatCells = hostiles.Take(MaxThreats).Select(p => (p.Position.x, p.Position.z)).ToList();

        var destinations = CandidateEnumerator.Enumerate(
            map, self.Position.x, self.Position.z, threatCells, weaponRange: range);

        var targets = new List<TargetCandidate>();
        // Turrets are always known once in scan range, in line of sight or not, and listed first so the cap never drops them.
        foreach (var t in HostileTurrets(self))
        {
            if (targets.Count >= OperativePlanner.MaxTargets - 1) break;
            var closeness = 1f - Math.Min(1f, t.Position.DistanceTo(self.Position) / ThreatScanRadius);
            targets.Add(new TargetCandidate(TargetPrefix + t.ThingID, t.Position.x, t.Position.z,
                0.7f + closeness * 0.3f, true, "turret", TurretRange(t)));
        }

        foreach (var p in hostiles)
        {
            if (targets.Count >= OperativePlanner.MaxTargets - 1) break;
            var visible = GenSight.LineOfSight(self.Position, p.Position, self.Map);
            if (!visible) continue;
            var closeness = 1f - Math.Min(1f, p.Position.DistanceTo(self.Position) / ThreatScanRadius);
            if (!IsColony(p))
            {
                // Mechs, manhunters and other factions' pawns shoot at the raid too: engage them before they close in.
                targets.Add(new TargetCandidate(TargetIdFor(p), p.Position.x, p.Position.z,
                    EnemyThreat + closeness * 0.2f, true, "enemy", WeaponReach(p)));
                continue;
            }
            var armed = p.equipment?.Primary != null ? 0.5f : 0.2f;
            targets.Add(new TargetCandidate(TargetIdFor(p), p.Position.x, p.Position.z,
                armed + closeness * 0.5f, true, "pawn", WeaponReach(p), p.health.summaryHealth.SummaryHealthPercent));
        }

        var allies = self.Map.mapPawns.AllPawnsSpawned.Count(p =>
            p != self && !p.Dead && !p.Downed && p.Faction == self.Faction &&
            p.Position.InHorDistOf(self.Position, AllyRadius));

        return new OperativeSnapshot(
            self.ThingID, self.Position.x, self.Position.z,
            self.health.summaryHealth.SummaryHealthPercent,
            self.equipment?.Primary?.def.defName ?? "Unarmed",
            allies, destinations, targets, self.health.HasHediffsNeedingTend(), range);
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
