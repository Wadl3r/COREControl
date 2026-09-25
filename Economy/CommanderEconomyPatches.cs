using System;
using HarmonyLib;

namespace GroundControlRts;

[HarmonyPatch]
internal static class CommanderEconomyPatches
{
    /// <summary>
    /// A factory's production cadence is a <c>SlowUpdate</c> registered once with the interval it
    /// was authored with, so raising <c>productionInterval</c> later reschedules nothing. The only
    /// lever that moves is the batch size, so an upgraded factory tops up the reserve here, right
    /// after the Basegame added its single unit.
    /// </summary>
    /// <summary>Factories switched off produce nothing (user, 2026-09-14): the Basegame's own
    /// production tick is skipped outright, so no supply is added for the depot loop to spawn.
    /// The switch belongs to the mod's economy, so it only applies where that economy runs; in any
    /// other mission the mission's own factories produce as the mission was authored.</summary>
    [HarmonyPatch(typeof(Factory), "ProduceUnit")]
    [HarmonyPrefix]
    private static bool ProduceUnitPrefix()
    {
        return FactoriesProduce;
    }

    internal static bool FactoriesProduce =>
        CommanderSettings.FactoriesEnabled || !CommanderFeatureGate.AdvancedFeaturesEnabled;

    [HarmonyPatch(typeof(Factory), "ProduceUnit")]
    [HarmonyPostfix]
    private static void ProduceUnitPostfix(Factory __instance)
    {
        try
        {
            if (!CommanderSettings.FactoriesEnabled)
            {
                return;
            }

            Unit attached = __instance.attachedUnit;
            if (attached == null || __instance.ProductionUnit == null)
            {
                return;
            }

            int bonus = CommanderEconomyService.GetFactoryOutput(attached) - 1;
            FactionHQ hq = attached.NetworkHQ;
            // AddSupplyUnit is [Server]: it throws rather than no-ops on a pure client.
            if (bonus > 0 && hq != null && hq.IsServer)
            {
                hq.AddSupplyUnit(__instance.ProductionUnit, bonus);
            }
        }
        catch (Exception exception)
        {
            CommanderFaults.Report("Factory.ProduceUnit postfix", exception);
        }
    }
}
