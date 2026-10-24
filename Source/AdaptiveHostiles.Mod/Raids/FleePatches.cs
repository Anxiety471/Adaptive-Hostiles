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
[HarmonyPatch(typeof(Lord), nameof(Lord.GotoToil))]
public static class Patch_Lord_GotoToil
{
    public static bool Prefix(Lord __instance, LordToil newLordToil)
    {
        if (newLordToil is null) return true;
        var name = newLordToil.GetType().Name;
        // Leaving, fleeing, or "steal what they can and leave": all are retreats Jev must decide.
        var retreat = name.StartsWith("LordToil_ExitMap") || name.StartsWith("LordToil_Steal") ||
                      name.StartsWith("LordToil_Kidnap") || name.Contains("Flee") || name.Contains("Leave");
        if (!retreat) return true;
        if (!SquadDriver.SuppressesFlee(__instance)) return true;
        Log.Message($"[Adaptive Hostiles] Blocked vanilla lord transition to {name}; Jev decides retreats.");
        return false;
    }
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
