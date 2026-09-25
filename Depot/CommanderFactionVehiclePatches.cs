using System;
using HarmonyLib;

namespace GroundControlRts;

[HarmonyPatch]
internal static class CommanderFactionVehiclePatches
{
    [HarmonyPatch(typeof(FactionHQ), "DeployVehicles")]
    [HarmonyPrefix]
    private static void DeployVehiclesPrefix(FactionHQ __instance)
    {
        CommanderFactionVehicleService.AutomaticDeploymentHq = __instance;
    }

    [HarmonyPatch(typeof(FactionHQ), "DeployVehicles")]
    [HarmonyPostfix]
    private static void DeployVehiclesPostfix()
    {
        CommanderFactionVehicleService.AutomaticDeploymentHq = null;
    }

    [HarmonyPatch(typeof(FactionHQ), "DeployVehicles")]
    [HarmonyFinalizer]
    private static Exception? DeployVehiclesFinalizer(Exception? __exception)
    {
        CommanderFactionVehicleService.AutomaticDeploymentHq = null;
        return __exception;
    }

    [HarmonyPatch(typeof(VehicleDepot), nameof(VehicleDepot.TrySpawnVehicle))]
    [HarmonyPrefix]
    private static bool TrySpawnVehiclePrefix(VehicleDepot __instance, VehicleDefinition vehicleDefinition, ref bool __result)
    {
        try
        {
            CommanderFactionVehicleService? service = CommanderFactionVehicleService.Instance;
            if (service == null || !service.ShouldBlockAutomaticDeployment(__instance, vehicleDefinition))
            {
                return true;
            }

            __result = false;
            return false;
        }
        catch (System.Exception exception)
        {
            CommanderFaults.Report("VehicleDepot.TrySpawnVehicle prefix", exception);
            return true;
        }
    }

    [HarmonyPatch(typeof(Factory), "set_NetworkproductionUnit")]
    [HarmonyPostfix]
    private static void ProductionUnitChangedPostfix(Factory __instance)
    {
        try
        {
            CommanderSpawnService.NotifyFactoryChanged(__instance);
        }
        catch (System.Exception exception)
        {
            CommanderFaults.Report("Factory.set_NetworkproductionUnit postfix", exception);
        }
    }
}
