using HarmonyLib;

namespace GroundControlRts;

[HarmonyPatch(typeof(UnitCommand), nameof(UnitCommand.SetDestination))]
internal static class CommanderMobileEmplacementDestinationPatch
{
    private static bool Prefix(UnitCommand __instance)
    {
        try
        {
            return !CommanderMobileEmplacementService.ShouldBlockDestination(__instance)
                && !CommanderSamSiteService.ShouldBlockConstructionDestination(__instance);
        }
        catch (System.Exception exception)
        {
            CommanderFaults.Report("UnitCommand.SetDestination prefix", exception);
            return true;
        }
    }
}
