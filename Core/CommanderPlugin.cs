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
    internal static ManualLogSource Log => Instance!.Logger;
    internal bool IsCommanderModeActive => modeController != null && modeController.IsActive;

    private Harmony? harmony;
    private CommanderModeController? modeController;

    private void Awake()
    {
        Instance = this;
        CommanderSettings.Initialize(Config);
        if (!CommanderSettings.ModEnabled)
        {
            Logger.LogInfo($"{PluginInfo.Name} {PluginInfo.Version} is disabled in configuration. No patches were installed.");
            return;
        }
        harmony = new Harmony(PluginInfo.Guid);
        harmony.PatchAll();

        CommanderMissionInstaller.InstallShippedMissions();
        CommanderServiceRegistryCheck.Run();
        CommanderScheduler.SelfCheck();
        CommanderFactionRoster.SelfCheck();
        CommanderEnemyCommanderService.SelfCheck();
        CommanderPlayerCommanderService.SelfCheck();
        CommanderEconomyService.SelfCheck();
        CommanderFactionVehicleService.SelfCheck();
        CommanderDownedPilotService.SelfCheck();
        CommanderSupplyHeliService.SelfCheck();
        CommanderCaptureService.SelfCheck();
        CommanderStrategicPointService.SelfCheck();
        CommanderOperationsService.SelfCheck();
        CommanderAirCommandService.SelfCheck();
        CommanderAirLaunchFacility.SelfCheck();
        CommanderBuildPreview.SelfCheck();
        CommanderCameraTuning.SelfCheck();
        CommanderUiScale.SelfCheck();
        CommanderUiTheme.SelfCheck();
        CommanderAiLog.SelfCheck();
        CommanderAiLogUi.SelfCheck();
        CommanderStateStore.SelfCheck();
        CommanderStrategicSaveStore.SelfCheck();
        CommanderHealthDiagnostics.SelfCheck();
        modeController = gameObject.AddComponent<CommanderModeController>();
        Logger.LogInfo($"{PluginInfo.Name} {PluginInfo.Version} loaded");
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
