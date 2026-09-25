using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace GroundControlRts;

internal sealed class CommanderDirectPathService : ICommanderDeactivate, ICommanderResetSession
{
    private static readonly FieldInfo? PathfindingUnitField = AccessTools.Field(typeof(PathfindingAgent), "unit");

    private readonly CommanderSelectionService selectionService;
    private readonly HashSet<GroundVehicle> directRouteVehicles = new();

    internal static CommanderDirectPathService? Instance { get; private set; }

    internal CommanderDirectPathService(CommanderSelectionService selectionService)
    {
        this.selectionService = selectionService;
        Instance = this;
    }

    internal bool CanConfigure(Unit? unit)
    {
        return unit is GroundVehicle vehicle
            && !vehicle.disabled
            && CommanderGameAccess.IsFriendlyUnit(vehicle, CommanderGameAccess.GetLocalHq())
            && !CommanderSamSiteService.IsReservedConstructionJacknife(vehicle)
            && !CommanderGameAccess.IsTrailerVehicleDefinition(vehicle.definition as VehicleDefinition);
    }

    internal bool IsEnabled(Unit? unit)
    {
        return unit is GroundVehicle vehicle && directRouteVehicles.Contains(vehicle);
    }

    internal void ToggleFocusedUnit()
    {
        if (!CanConfigure(selectionService.FocusedSelection)
            || selectionService.FocusedSelection is not GroundVehicle vehicle)
        {
            return;
        }

        if (!directRouteVehicles.Remove(vehicle))
        {
            directRouteVehicles.Add(vehicle);
        }

        ReapplyCurrentDestination(vehicle);
    }

    public void ResetSession()
    {
        directRouteVehicles.Clear();
    }

    public void Deactivate()
    {
        directRouteVehicles.Clear();
    }

    internal static bool TryApplyShortcut(PathfindingAgent pathfinder, GlobalPosition target)
    {
        if (Instance == null
            || PathfindingUnitField?.GetValue(pathfinder) is not GroundVehicle vehicle
            || !Instance.directRouteVehicles.Contains(vehicle))
        {
            return false;
        }

        pathfinder.Shortcut(target);
        return true;
    }

    internal static void ForceEnabled(GroundVehicle? vehicle)
    {
        if (Instance != null && vehicle != null && !vehicle.disabled)
        {
            Instance.directRouteVehicles.Add(vehicle);
        }
    }

    internal static void Forget(GroundVehicle? vehicle)
    {
        if (Instance != null && vehicle != null)
        {
            Instance.directRouteVehicles.Remove(vehicle);
        }
    }

    private static void ReapplyCurrentDestination(GroundVehicle vehicle)
    {
        UnitCommand? command = vehicle.UnitCommand;
        if (command == null)
        {
            return;
        }

        UnitCommand.Command current = command.GetCommandCached();
        if (current.time > 0f || current.player != null || !current.position.Equals(default(GlobalPosition)))
        {
            command.SetDestination(current.position, current.FromPlayer);
        }
    }
}

[HarmonyPatch(typeof(PathfindingAgent), nameof(PathfindingAgent.Pathfind))]
internal static class CommanderDirectPathPatch
{
    private static bool Prefix(PathfindingAgent __instance, GlobalPosition targetPos)
    {
        try
        {
            return !CommanderDirectPathService.TryApplyShortcut(__instance, targetPos);
        }
        catch (System.Exception exception)
        {
            CommanderFaults.Report("PathfindingAgent.Pathfind prefix", exception);
            return true;
        }
    }
}
