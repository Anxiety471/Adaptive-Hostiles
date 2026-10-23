using System.Net.Http;
using System.Text.Json;

namespace AdaptiveHostiles.Core;

/// <summary>
/// Raid size is derived from a named Jev choice and a bounded local policy;
/// vanilla raid points are never an input.
/// </summary>
public sealed class RaidPlanner(IJevClient? client = null)
{
    public static int PawnCountFor(RaidScale scale, int colonistCount)
    {
        var baseline = scale switch
        {
            RaidScale.Recon => 2,
            RaidScale.Skirmish => 4,
            RaidScale.Assault => 8,
            RaidScale.Invasion => 12,
            _ => 2
        };
        return Math.Clamp(baseline + Math.Clamp(colonistCount, 0, 40) / 5, 2, 24);
    }

    public async Task<RaidPlan> PlanAsync(
        ColonySnapshot colony, double eliteRoll, CancellationToken cancellationToken = default)
    {
        // Randomness belongs to the caller (seeded RimWorld RNG in production).
        // This keeps tests and save/load replays deterministic.
        var elite = eliteRoll is >= 0 and < 0.05;
        var fallback = Fallback(colony, elite);
        if (client is null)
            return fallback;

        var questions = new Dictionary<string, object>
        {
            ["scale"] = new
            {
                type = "choice",
                instructions = "Given the colony defenses, armed population, wealth and faction capability, what force size category is appropriate for this hostile raid?",
                criteria = new Dictionary<string, string>
                {
                    ["recon"] = "A small probing or scouting force.",
                    ["skirmish"] = "A modest armed encounter.",
                    ["assault"] = "A substantial hostile assault.",
                    ["invasion"] = "A major coordinated attack on a strongly defended colony."
                }
            },
            ["approach"] = new
            {
                type = "choice",
                instructions = "Which initial approach is most appropriate given this colony and faction's resources? The game will verify that the approach is actually supported.",
                criteria = new Dictionary<string, string>
                {
                    ["direct"] = "Approach and attack the colony directly.",
                    ["flank"] = "Approach from an alternate defensive angle.",
                    ["breach"] = "Attempt a breach through defensive structures.",
                    ["siege"] = "Prepare for a longer ranged siege."
                }
            }
        };

        try
        {
            using var result = await client.DecideAsync(colony, questions, cancellationToken)
                .ConfigureAwait(false);
            var scaleText = JevAnswers.Choice(result.RootElement, "scale");
            var approachText = JevAnswers.Choice(result.RootElement, "approach");
            if (!ParseScale(scaleText, out var scale) || !ParseApproach(approachText, out var approach))
                return fallback;

            return new RaidPlan(scale, approach, PawnCountFor(scale, colony.ColonistCount), elite, true);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return fallback; // request deadline expired
        }
        catch (HttpRequestException) { return fallback; }
        catch (JsonException) { return fallback; }
        // An explicit caller cancellation must propagate.
    }

    private static RaidPlan Fallback(ColonySnapshot s, bool elite)
    {
        var strength = s.CombatReadyCount + s.TurretCount * 2 + s.EquippedRangedWeapons / 2;
        var scale = strength switch
        {
            < 3 => RaidScale.Recon,
            < 9 => RaidScale.Skirmish,
            < 20 => RaidScale.Assault,
            _ => RaidScale.Invasion
        };
        return new RaidPlan(scale, RaidApproach.Direct, PawnCountFor(scale, s.ColonistCount), elite, false);
    }

    private static bool ParseScale(string? value, out RaidScale scale)
    {
        scale = value switch
        {
            "recon" => RaidScale.Recon,
            "skirmish" => RaidScale.Skirmish,
            "assault" => RaidScale.Assault,
            "invasion" => RaidScale.Invasion,
            _ => (RaidScale)(-1)
        };
        return Enum.IsDefined(scale);
    }

    private static bool ParseApproach(string? value, out RaidApproach approach)
    {
        approach = value switch
        {
            "direct" => RaidApproach.Direct,
            "flank" => RaidApproach.Flank,
            "breach" => RaidApproach.Breach,
            "siege" => RaidApproach.Siege,
            _ => (RaidApproach)(-1)
        };
        return Enum.IsDefined(approach);
    }
}
