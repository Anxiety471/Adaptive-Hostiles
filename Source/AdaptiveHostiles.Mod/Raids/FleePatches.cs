using AdaptiveHostiles.Mod.Tactics;
using HarmonyLib;
using Verse;
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
        if (!name.StartsWith("LordToil_ExitMap") && !name.Contains("Flee")) return true;
        return !SquadDriver.SuppressesFlee(__instance);
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
