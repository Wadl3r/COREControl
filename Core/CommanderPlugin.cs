using System;
using System.Reflection;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace GroundControlRts;

[BepInPlugin(PluginInfo.Guid, PluginInfo.Name, PluginInfo.Version)]
[BepInIncompatibility(PluginInfo.GroundControlGuid)]
[BepInIncompatibility(PluginInfo.NuclearOptionCommanderGuid)]
public sealed class CommanderPlugin : BaseUnityPlugin
{
    internal static CommanderPlugin? Instance { get; private set; }
    // Kept after OnDestroy clears Instance, so a component torn down later can still log.
    private static ManualLogSource? log;

    internal static ManualLogSource Log => log ??= BepInEx.Logging.Logger.CreateLogSource(PluginInfo.Name);
    internal bool IsCommanderModeActive => modeController != null && modeController.IsActive;

    private Harmony? harmony;
    private CommanderModeController? modeController;

    private void Awake()
    {
        Instance = this;
        log = Logger;
        CommanderSettings.Initialize(Config);
        if (!CommanderSettings.ModEnabled)
        {
            Logger.LogInfo($"{PluginInfo.Name} {PluginInfo.Version} is disabled in configuration. No patches were installed.");
            return;
        }
        harmony = new Harmony(PluginInfo.Guid);
        int patchFailures = PatchEachClass(harmony);

        // Everything between here and the mode controller is guarded one call at a time: a throw
        // anywhere in this list used to abort Awake before AddComponent, which left the patches
        // installed with no services behind them.
        Guarded("Mission install", CommanderMissionInstaller.InstallShippedMissions);
        Guarded("Service registry self-check", CommanderServiceRegistryCheck.Run);
        Guarded("Scheduler self-check", CommanderScheduler.SelfCheck);
        Guarded("Faction roster self-check", CommanderFactionRoster.SelfCheck);
        Guarded("Enemy commander self-check", CommanderEnemyCommanderService.SelfCheck);
        Guarded("Player commander self-check", CommanderPlayerCommanderService.SelfCheck);
        Guarded("Economy self-check", CommanderEconomyService.SelfCheck);
        Guarded("Faction vehicle self-check", CommanderFactionVehicleService.SelfCheck);
        Guarded("Downed pilot self-check", CommanderDownedPilotService.SelfCheck);
        Guarded("Supply helicopter self-check", CommanderSupplyHeliService.SelfCheck);
        Guarded("Capture self-check", CommanderCaptureService.SelfCheck);
        Guarded("Strategic point self-check", CommanderStrategicPointService.SelfCheck);
        Guarded("Operations self-check", CommanderOperationsService.SelfCheck);
        Guarded("Air command self-check", CommanderAirCommandService.SelfCheck);
        Guarded("Air launch facility self-check", CommanderAirLaunchFacility.SelfCheck);
        Guarded("Build preview self-check", CommanderBuildPreview.SelfCheck);
        Guarded("Camera tuning self-check", CommanderCameraTuning.SelfCheck);
        Guarded("UI scale self-check", CommanderUiScale.SelfCheck);
        Guarded("UI theme self-check", CommanderUiTheme.SelfCheck);
        Guarded("AI log self-check", CommanderAiLog.SelfCheck);
        Guarded("AI log UI self-check", CommanderAiLogUi.SelfCheck);
        Guarded("State store self-check", CommanderStateStore.SelfCheck);
        Guarded("Strategic save self-check", CommanderStrategicSaveStore.SelfCheck);
        Guarded("Health diagnostics self-check", CommanderHealthDiagnostics.SelfCheck);
        modeController = gameObject.AddComponent<CommanderModeController>();
        Logger.LogInfo(patchFailures == 0
            ? $"{PluginInfo.Name} {PluginInfo.Version} loaded"
            : $"{PluginInfo.Name} {PluginInfo.Version} loaded with {patchFailures} patch class(es) not applied; see the errors above");
    }

    /// <summary>
    /// What <c>Harmony.PatchAll</c> does, one patch class at a time. PatchAll stops at the first
    /// class whose target is gone, which after a game update left every later class unpatched and
    /// the mod dead; this skips only the broken class and logs which one it was.
    /// </summary>
    private int PatchEachClass(Harmony instance)
    {
        int failures = 0;
        foreach (Type type in AccessTools.GetTypesFromAssembly(Assembly.GetExecutingAssembly()))
        {
            try
            {
                instance.CreateClassProcessor(type).Patch();
            }
            catch (Exception exception)
            {
                failures++;
                Logger.LogError($"Harmony patch class {type.FullName} could not be applied and is skipped: {exception}");
            }
        }

        return failures;
    }

    private void Guarded(string step, Action action)
    {
        try
        {
            action();
        }
        catch (Exception exception)
        {
            Logger.LogError($"{step} threw during load and was skipped: {exception}");
        }
    }

    private void OnDestroy()
    {
        if (harmony != null)
        {
            harmony.UnpatchSelf();
            harmony = null;
        }

        Instance = null;
    }
}
