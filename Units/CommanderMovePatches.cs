using HarmonyLib;
using NuclearOption.Networking;

namespace GroundControlRts;

[HarmonyPatch(typeof(UnitCommand), "ServerSetDestination")]
internal static class CommanderMoveDestinationPatch
{
    private static void Postfix(UnitCommand __instance, GlobalPosition waypoint, Player player)
    {
        try
        {
            if (CommanderPlugin.Instance?.IsCommanderModeActive == true)
            {
                CommanderMoveService.NotifyPlayerDestination(__instance, waypoint, player);
            }
        }
        catch (System.Exception exception)
        {
            CommanderFaults.Report("UnitCommand.ServerSetDestination postfix", exception);
        }
    }
}

/// <summary>
/// Focus fire: once a unit has been given an explicit attack order, its turrets keep the
/// commanded target as long as the Basegame assessment considers it engageable at all.
/// </summary>
[HarmonyPatch(typeof(Turret), "AssessTargetPriority")]
internal static class CommanderFocusFirePatch
{
    private const float ForcedPriority = 1e9f;

    private static readonly AccessTools.FieldRef<Turret, Unit> TurretTarget =
        AccessTools.FieldRefAccess<Turret, Unit>("target");
    private static readonly AccessTools.FieldRef<Turret, Unit> TurretAttachedUnit =
        AccessTools.FieldRefAccess<Turret, Unit>("attachedUnit");

    private static void Postfix(Turret __instance, Unit targetCandidate, ref float priorityThreshold)
    {
        try
        {
            if (CommanderMoveService.Instance?.HasAttackOrders != true
                || !ReferenceEquals(TurretTarget(__instance), targetCandidate)
                || !CommanderMoveService.IsOrderedTarget(TurretAttachedUnit(__instance), targetCandidate))
            {
                return;
            }

            // The candidate was just accepted by the Basegame scoring; lock it in so no later
            // candidate in this scan outbids the commanded target.
            priorityThreshold = ForcedPriority;
        }
        catch (System.Exception exception)
        {
            CommanderFaults.Report("Turret.AssessTargetPriority postfix (focus fire)", exception);
        }
    }
}

/// <summary>
/// Hold Fire: a unit told to stay dark rejects every target candidate, so its turrets acquire
/// nothing and never open up. Radar-guided batteries fed by a FireControl still need the radar
/// switched off as well, which the unit systems window does.
/// </summary>
[HarmonyPatch(typeof(Turret), "AssessTargetPriority")]
internal static class CommanderHoldFirePatch
{
    private static readonly AccessTools.FieldRef<Turret, Unit> TurretAttachedUnit =
        AccessTools.FieldRefAccess<Turret, Unit>("attachedUnit");

    private static bool Prefix(Turret __instance)
    {
        try
        {
            return CommanderMoveService.Instance?.HasHoldFireUnits != true
                || !CommanderMoveService.IsHoldingFire(TurretAttachedUnit(__instance));
        }
        catch (System.Exception exception)
        {
            CommanderFaults.Report("Turret.AssessTargetPriority prefix (hold fire)", exception);
            return true;
        }
    }
}
