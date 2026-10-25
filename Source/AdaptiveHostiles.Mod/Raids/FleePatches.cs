using AdaptiveHostiles.Mod.Tactics;
using HarmonyLib;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace AdaptiveHostiles.Mod.Raids;

/// <summary>
/// Turns off the base game's flee logic for raiders under squad control. Their lord is not
/// allowed to switch to an exit toil and they cannot enter panic/flee mental states; only a
/// Jev "flee" order (a plain exit job) sends an ordinary raider away. The Operative never flees.
/// </summary>
public static class RetreatGuard
{
    private static readonly HashSet<int> Logged = new();

    /// <summary>Leaving, fleeing, or "steal what they can and leave": all are retreats Jev must decide.</summary>
    public static bool IsRetreat(LordToil? toil)
    {
        if (toil is null) return false;
        var name = toil.GetType().Name;
        return name.StartsWith("LordToil_ExitMap") || name.StartsWith("LordToil_Steal") ||
               name.StartsWith("LordToil_Kidnap") || name.Contains("Flee") || name.Contains("Leave");
    }

    public static bool Blocks(Lord lord, LordToil? toil)
    {
        if (!IsRetreat(toil) || !SquadDriver.SuppressesFlee(lord)) return false;
        if (Logged.Add(lord.loadID))
            Log.Message($"[Adaptive Hostiles] Blocking vanilla lord retreat to {toil!.GetType().Name}; Jev decides retreats.");
        return true;
    }
}

// Blocking the whole transition (not just GotoToil) also suppresses its "are leaving" message.
[HarmonyPatch(typeof(Transition), nameof(Transition.Execute))]
public static class Patch_Transition_Execute
{
    public static bool Prefix(Transition __instance, Lord lord) => !RetreatGuard.Blocks(lord, __instance.target);
}

[HarmonyPatch(typeof(Lord), nameof(Lord.GotoToil))]
public static class Patch_Lord_GotoToil
{
    public static bool Prefix(Lord __instance, LordToil newLordToil) => !RetreatGuard.Blocks(__instance, newLordToil);
}

[HarmonyPatch]
public static class Patch_MentalStateHandler_TryStartMentalState
{
    public static System.Reflection.MethodBase TargetMethod() =>
        AccessTools.Method(typeof(MentalStateHandler), "TryStartMentalState");

    public static bool Prefix(MentalStateDef stateDef, Pawn ___pawn)
    {
        if (stateDef is null || ___pawn is null || !SquadDriver.IsControlled(___pawn)) return true;
        var name = stateDef.defName;
        return !(name.Contains("Flee") || name.Contains("Panic"));
    }
}
