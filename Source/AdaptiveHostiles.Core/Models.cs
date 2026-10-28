namespace AdaptiveHostiles.Core;

// These DTOs deliberately have no dependency on RimWorld assemblies. All game state
// must be sampled on the game thread before passing snapshots to a background task.
public sealed record ColonySnapshot(
    double TotalWealth,
    int ColonistCount,
    int CombatReadyCount,
    int EquippedRangedWeapons,
    int TurretCount,
    int DefensiveStructureCount,
    int InjuredColonistCount,
    string FactionDefName,
    string FactionTechLevel,
    int RecentFailedRaids,
    int RecentSuccessfulRaids = 0);

public enum RaidScale { Recon, Skirmish, Assault, Invasion }
public enum RaidApproach { Direct, Flank, Breach, Siege }

public sealed record RaidPlan(
    RaidScale Scale,
    RaidApproach Approach,
    int RaiderCount,
    bool IncludeOperative,
    bool IsModelDecision);

public sealed record MovementCandidate(
    string Id,
    int X,
    int Z,
    float Cover,
    float Exposure,
    float Distance,
    string Purpose,
    bool Reachable,
    bool InTurretRange = false,
    float Danger = 0f);

public sealed record TargetCandidate(
    string Id,
    int X,
    int Z,
    float Threat,
    bool Visible,
    string Kind = "pawn",
    float Range = 0,
    float Health = 1f);

public sealed record OperativeSnapshot(
    string PawnId,
    int X,
    int Z,
    float HealthFraction,
    string EquippedWeapon,
    int NearbyAllies,
    IReadOnlyList<MovementCandidate> Destinations,
    IReadOnlyList<TargetCandidate> Targets,
    bool NeedsTending = false);

/// <summary>Flee leaves the map and is only ever available to ordinary raiders, never the Operative.</summary>
public enum TacticalStance { Defensive, Aggressive, Withdraw, Flee }

// DestinationId references a locally generated MovementCandidate, or "hold".
// TargetId references a locally generated TargetCandidate, or null.
public sealed record OperativeDecision(
    string DestinationId,
    string? TargetId,
    TacticalStance Stance,
    bool IsModelDecision);
