using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace GroundControlRts;

[HarmonyPatch(typeof(Repairer), "SearchForRepair")]
internal static class CommanderRepairPatches
{
    private static readonly FieldInfo? AttachedUnitField = AccessTools.Field(typeof(Repairer), "attachedUnit");
    private static readonly FieldInfo? LastRepairCheckField = AccessTools.Field(typeof(Repairer), "lastRepairCheck");
    private static readonly FieldInfo? UnitToRepairField = AccessTools.Field(typeof(Repairer), "unitToRepair");
    private static readonly FieldInfo? RepairInProgressField = AccessTools.Field(typeof(Repairer), "repairInProgress");

    [HarmonyPrefix]
    private static bool SearchForRepairPrefix(Repairer __instance)
    {
        try
        {
            Unit? repairerUnit = AttachedUnitField?.GetValue(__instance) as Unit;
            if (CommanderSamSiteService.IsReservedConstructionJacknife(repairerUnit))
            {
                UnitToRepairField?.SetValue(__instance, null);
                RepairInProgressField?.SetValue(__instance, null);
                return false;
            }

            // A crew the commander paid for goes to the building it was paid for, ahead of both the
            // nearest-target override and the Basegame priority scoring. That is the whole product.
            CommanderRepairService? repairService = CommanderRepairService.Instance;
            Unit? assignedTarget = null;
            if (repairService != null && repairService.TryGetAssignedTarget(repairerUnit, out Unit paidTarget))
            {
                assignedTarget = paidTarget;
            }

            if (repairerUnit == null
                || (assignedTarget == null && repairService?.ShouldUseNearestTarget(repairerUnit) != true)
                || LastRepairCheckField == null
                || UnitToRepairField == null
                || RepairInProgressField == null)
            {
                return true;
            }

            IRepairable? activeRepair = RepairInProgressField.GetValue(__instance) as IRepairable;
            if (activeRepair != null && activeRepair.NeedsRepair())
            {
                return false;
            }
            if (activeRepair != null)
            {
                RepairInProgressField.SetValue(__instance, null);
                UnitToRepairField.SetValue(__instance, null);
            }

            float lastCheck = (float)LastRepairCheckField.GetValue(__instance);
            if (Time.timeSinceLevelLoad - lastCheck < 30f)
            {
                return false;
            }
            LastRepairCheckField.SetValue(__instance, Time.timeSinceLevelLoad);

            Unit? previous = UnitToRepairField.GetValue(__instance) as Unit;
            Unit? nearest = assignedTarget ?? FindNearestRepairTarget(repairerUnit);
            UnitToRepairField.SetValue(__instance, nearest);
            if (nearest != null && !ReferenceEquals(previous, nearest) && repairerUnit is ICommandable commandable)
            {
                Vector3 direction = nearest.GlobalPosition() - repairerUnit.GlobalPosition();
                direction.y = 0f;
                if (direction.sqrMagnitude > 0.01f)
                {
                    GlobalPosition waypoint = nearest.startPosition - nearest.maxRadius * 2f * direction.normalized;
                    commandable.UnitCommand?.SetDestination(waypoint, playerCommand: false);
                }
            }
            return false;
        }
        catch (System.Exception exception)
        {
            CommanderFaults.Report("Repairer.SearchForRepair prefix", exception);
            return true;
        }
    }

    internal static void RequestImmediateSearch(Repairer repairer)
    {
        LastRepairCheckField?.SetValue(repairer, Time.timeSinceLevelLoad - 30f);
        UnitToRepairField?.SetValue(repairer, null);
        RepairInProgressField?.SetValue(repairer, null);
    }

    private static Unit? FindNearestRepairTarget(Unit repairerUnit)
    {
        FactionHQ? hq = repairerUnit.NetworkHQ;
        if (hq == null)
        {
            return null;
        }

        Unit? nearest = null;
        float nearestDistance = float.MaxValue;
        foreach (PersistentID id in hq.factionUnits)
        {
            if (!id.TryGetUnit(out Unit candidate)
                || candidate == null
                || candidate is not IRepairable repairable
                || !repairable.NeedsRepair())
            {
                continue;
            }

            float distance = FastMath.SquareDistance(repairerUnit.GlobalPosition(), candidate.startPosition);
            if (distance < nearestDistance)
            {
                nearestDistance = distance;
                nearest = candidate;
            }
        }
        return nearest;
    }
}
