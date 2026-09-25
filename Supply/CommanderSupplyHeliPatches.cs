using System;
using HarmonyLib;
using UnityEngine;

namespace GroundControlRts;

[HarmonyPatch]
internal static class CommanderSupplyHeliPatches
{
    [HarmonyPatch(typeof(CameraStateManager), "LateUpdate")]
    [HarmonyPostfix]
    private static void CameraLateUpdatePostfix(CameraStateManager __instance)
    {
        try
        {
            CommanderCameraFollowService.ApplyCommanderLatePose(__instance);
        }
        catch (System.Exception exception)
        {
            CommanderFaults.Report("CameraStateManager.LateUpdate postfix", exception);
        }
    }

    [HarmonyPatch(typeof(FactionHQ), nameof(FactionHQ.RegisterFactionUnit))]
    [HarmonyPostfix]
    private static void RegisterFactionUnitPostfix(FactionHQ __instance, Unit unit)
    {
        try
        {
            CommanderSupplyHeliService.NotifyFactionUnitRegistered(__instance, unit);
        }
        catch (System.Exception exception)
        {
            CommanderFaults.Report("RegisterFactionUnit postfix (supply)", exception);
        }
    }

    [HarmonyPatch(typeof(AIHeloTransportState), "SearchForLandingSpot")]
    [HarmonyPrefix]
    private static bool SearchForLandingSpotPrefix(AIHeloTransportState __instance)
    {
        try
        {
            return !CommanderSupplyHeliService.TryOverrideTransportTarget(__instance);
        }
        catch (System.Exception exception)
        {
            CommanderFaults.Report("AIHeloTransportState.SearchForLandingSpot prefix", exception);
            return true;
        }
    }

    [HarmonyPatch(typeof(AIHeloTransportState), nameof(AIHeloTransportState.FixedUpdateState))]
    [HarmonyPrefix]
    private static void TransportFixedUpdatePrefix(AIHeloTransportState __instance)
    {
        try
        {
            CommanderSupplyHeliService.TryOverrideTransportTarget(__instance);
        }
        catch (System.Exception exception)
        {
            CommanderFaults.Report("AIHeloTransportState.FixedUpdateState prefix", exception);
        }
    }

    [HarmonyPatch(typeof(AIHeloTransportState), "EjectionCheck")]
    [HarmonyPrefix]
    private static bool EjectionCheckPrefix(AIHeloTransportState __instance)
    {
        try
        {
            return !CommanderSupplyHeliService.ShouldSuppressAssignedEjection(__instance);
        }
        catch (System.Exception exception)
        {
            CommanderFaults.Report("AIHeloTransportState.EjectionCheck prefix", exception);
            return true;
        }
    }

    [HarmonyPatch(typeof(AIHeloTransportState), nameof(AIHeloTransportState.LeaveState))]
    [HarmonyPostfix]
    private static void LeaveStatePostfix(AIHeloTransportState __instance)
    {
        try
        {
            CommanderSupplyHeliService.NotifyTransportStateLeft(__instance);
        }
        catch (System.Exception exception)
        {
            CommanderFaults.Report("AIHeloTransportState.LeaveState postfix", exception);
        }
    }

    [HarmonyPatch(typeof(AIHeloTransportState), "DeployCargo")]
    [HarmonyPrefix]
    private static bool DeployCargoPrefix(AIHeloTransportState __instance)
    {
        try
        {
            return !CommanderSupplyHeliService.TryDeployAssignedCargo(__instance);
        }
        catch (System.Exception exception)
        {
            CommanderFaults.Report("AIHeloTransportState.DeployCargo prefix", exception);
            return true;
        }
    }

    [HarmonyPatch(typeof(Pilot), nameof(Pilot.SwitchState))]
    [HarmonyPrefix]
    private static bool SwitchStatePrefix(Pilot __instance, PilotBaseState state)
    {
        try
        {
            return !CommanderSupplyHeliService.ShouldDelayCargoTakeoff(__instance, state);
        }
        catch (System.Exception exception)
        {
            CommanderFaults.Report("Pilot.SwitchState prefix", exception);
            return true;
        }
    }

    /// <summary>The insertion shield (Supply/CommanderSupplyHeliShield.cs): a shielded vehicle's parts
    /// take no damage of any kind — this is the one entry every kind passes through.</summary>
    [HarmonyPatch(typeof(UnitPart), nameof(UnitPart.TakeDamage))]
    [HarmonyPrefix]
    private static bool UnitPartTakeDamagePrefix(UnitPart __instance)
    {
        try
        {
            return !CommanderSupplyHeliService.ShieldsDamage(__instance);
        }
        catch (System.Exception exception)
        {
            CommanderFaults.Report("UnitPart.TakeDamage prefix", exception);
            return true;
        }
    }

    [HarmonyPatch(typeof(Aircraft), nameof(Aircraft.ReturnToInventory))]
    [HarmonyPostfix]
    private static void ReturnToInventoryPostfix(Aircraft __instance)
    {
        try
        {
            CommanderSupplyHeliService.NotifyAircraftReturned(__instance);
        }
        catch (System.Exception exception)
        {
            CommanderFaults.Report("Aircraft.ReturnToInventory postfix (supply)", exception);
        }
    }

    [HarmonyPatch(typeof(AIHeloLandingState), nameof(AIHeloLandingState.EnterState))]
    [HarmonyPostfix]
    private static void HeloLandingEnterStatePostfix(AIHeloLandingState __instance)
    {
        try
        {
            CommanderSupplyHeliService.TryOverrideAssignedReturnAirbase(__instance);
        }
        catch (System.Exception exception)
        {
            CommanderFaults.Report("AIHeloLandingState.EnterState postfix", exception);
        }
    }

    [HarmonyPatch(typeof(AIHeloCombatState), nameof(AIHeloCombatState.EnterState))]
    [HarmonyPostfix]
    private static void HeloCombatEnterStatePostfix(AIHeloCombatState __instance)
    {
        try
        {
            CommanderSupplyHeliService.TryOverrideAssignedNearestAirbase(__instance);
        }
        catch (System.Exception exception)
        {
            CommanderFaults.Report("AIHeloCombatState.EnterState postfix", exception);
        }
    }

    [HarmonyPatch(typeof(PilotBaseState), "FindNearestAirbase")]
    [HarmonyPostfix]
    private static void FindNearestAirbasePostfix(PilotBaseState __instance)
    {
        try
        {
            CommanderSupplyHeliService.TryOverrideAssignedNearestAirbase(__instance);
        }
        catch (System.Exception exception)
        {
            CommanderFaults.Report("PilotBaseState.FindNearestAirbase postfix", exception);
        }
    }

    [HarmonyPatch(typeof(MountedCargo), nameof(MountedCargo.ActivateCargoVehicle))]
    [HarmonyPostfix]
    private static void ActivateCargoVehiclePostfix(
        MountedCargo __instance,
        Unit cargoUnit,
        Collider cargoCollider,
        PhysicMaterial cargoColliderMaterial)
    {
        try
        {
            if (__instance.attachedUnit is Aircraft aircraft && cargoUnit != null)
            {
                CommanderSupplyHeliService.NotifyCargoActivated(aircraft, cargoUnit);
            }
        }
        catch (System.Exception exception)
        {
            CommanderFaults.Report("MountedCargo.ActivateCargoVehicle postfix", exception);
        }
    }

    [HarmonyPatch(
        typeof(AutopilotHelo),
        nameof(AutopilotHelo.AutoAim),
        new Type[] { typeof(GlobalPosition), typeof(float), typeof(Vector3), typeof(Vector3), typeof(bool) })]
    [HarmonyPrefix]
    private static void HeloAutoAimPrefix(
        AutopilotHelo __instance,
        ref float altitudeHold,
        bool followTerrain)
    {
        try
        {
            CommanderSupplyHeliService.PrepareAssignedTerrainFlight(
                __instance,
                ref altitudeHold,
                followTerrain);
        }
        catch (System.Exception exception)
        {
            CommanderFaults.Report("AutopilotHelo.AutoAim prefix", exception);
        }
    }

    [HarmonyPatch(
        typeof(AutopilotTiltwing),
        nameof(AutopilotTiltwing.AutoAim),
        new Type[] { typeof(GlobalPosition), typeof(float), typeof(Vector3), typeof(Vector3), typeof(bool) })]
    [HarmonyPrefix]
    private static void TiltwingAutoAimPrefix(
        AutopilotTiltwing __instance,
        ref float altitudeHold,
        bool followTerrain)
    {
        try
        {
            CommanderSupplyHeliService.PrepareAssignedTerrainFlight(
                __instance,
                ref altitudeHold,
                followTerrain);
        }
        catch (System.Exception exception)
        {
            CommanderFaults.Report("AutopilotTiltwing.AutoAim prefix", exception);
        }
    }

    [HarmonyPatch(typeof(SwivelDuctSystem), "FixedUpdate")]
    [HarmonyPrefix]
    private static void SwivelDuctFixedUpdatePrefix(SwivelDuctSystem __instance)
    {
        try
        {
            CommanderSupplyHeliService.ForceAssignedVerticalTakeoff(__instance);
        }
        catch (System.Exception exception)
        {
            CommanderFaults.Report("SwivelDuctSystem.FixedUpdate prefix", exception);
        }
    }
}
