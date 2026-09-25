using UnityEngine;
using UnityEngine.SceneManagement;

namespace GroundControlRts;

/// <summary>
/// The RTS mode itself: it owns the camera/cursor takeover, the draw pass, and a
/// <see cref="CommanderServiceRegistry"/> holding everything else.
/// <para>
/// Adding a feature means writing a service, implementing the lifecycle interfaces it
/// actually needs, and adding one <c>Register</c> line in <see cref="Awake"/>. Nothing else
/// in this file should have to change. The order of those Register calls is the per-frame
/// schedule, so read <see cref="Awake"/> top to bottom to see what runs when.
/// </para>
/// </summary>
internal sealed class CommanderModeController : MonoBehaviour
{
    private readonly CommanderServiceRegistry services = new();

    // The camera and cursor takeover brackets every service, so it is driven by hand rather
    // than registered.
    private CommanderCameraController? cameraController;
    private CommanderCursorController? cursorController;

    // Registered services this file still talks to directly, because something about them is
    // conditional: physics-step camera work, the map window's open/close, the draw pass, and
    // the scene-change teardown that has to run even when the mode was never entered.
    private CommanderCameraFollowService? cameraFollowService;
    private CommanderTacticalMapService? tacticalMapService;
    private CommanderBoxSelectService? boxSelectService;
    private CommanderOverlayUi? overlayUi;

    // Not a per-frame service like the rest: it owns the hot-reload snapshot file and needs a
    // direct call from OnDestroy, which a plain Register entry cannot give it. See its own doc
    // comment for why it is still registered for TickPersistent/ResetSession.
    private CommanderStateStore? stateStore;

    // Same again: it holds the game's mission-load event, which is what arms a strategic
    // restore, and a static event holding a dead instance across a hot reload is a leak that
    // would also leave two stores answering. Detached by hand in OnDestroy.
    private CommanderStrategicSaveStore? strategicSaveStore;

    // Draw-only and input-only surfaces: no lifecycle of their own.
    private CommanderPovCrewUi? povCrewUi;
    private CommanderAlertUi? alertUi;
    private CommanderInputController? inputController;

    private float nextInactiveEntryProbeAt;
    private bool aircraftSelectionMenuPresent;

    internal bool IsActive { get; private set; }

    private void Awake()
    {
        CommanderUiScale.ApplyResolutionPreset();
        cameraController = new CommanderCameraController();
        cursorController = new CommanderCursorController();

        CommanderSelectionService selectionService = services.Register(new CommanderSelectionService());
        CommanderGroupService groupService = services.Register(new CommanderGroupService(selectionService));
        cameraFollowService = services.Register(new CommanderCameraFollowService(selectionService));
        CommanderMarkerService markerService = services.Register(new CommanderMarkerService(selectionService));
        boxSelectService = services.Register(new CommanderBoxSelectService(selectionService, markerService));
        tacticalMapService = services.Register(new CommanderTacticalMapService(cameraFollowService));
        // Alerts run first among the persistent ticks and outside the feature gate: their whole
        // point is telling the player what happened while they were flying.
        CommanderAlertService alertService = services.Register(new CommanderAlertService());

        CommanderRadarService radarService =
            services.Register(new CommanderRadarService(selectionService), CommanderTier.Advanced);
        CommanderMobileEmplacementService mobileEmplacementService =
            services.Register(new CommanderMobileEmplacementService(selectionService), CommanderTier.Advanced);
        CommanderDirectPathService directPathService =
            services.Register(new CommanderDirectPathService(selectionService), CommanderTier.Advanced);
        CommanderSupplyHeliService supplyHeliService =
            services.Register(new CommanderSupplyHeliService(), CommanderTier.Advanced);
        CommanderAirCommandService airCommandService =
            services.Register(new CommanderAirCommandService(tacticalMapService), CommanderTier.Advanced);
        CommanderNavalPurchaseService navalPurchaseService =
            services.Register(new CommanderNavalPurchaseService(tacticalMapService), CommanderTier.Advanced);
        CommanderSamSiteAnalyzerService samSiteAnalyzerService =
            services.Register(new CommanderSamSiteAnalyzerService(), CommanderTier.Advanced);
        CommanderSamSiteService samSiteService = services.Register(
            new CommanderSamSiteService(samSiteAnalyzerService, supplyHeliService),
            CommanderTier.Advanced);
        // BEFORE every service that reads or spends money, and in particular before the enemy
        // commander below. Its first job on a restored mission is to put the war chest back and mark
        // which treasuries have already been opened, and the commander's first review fires on its
        // own first tick and opens its treasury there — so ticking after it would apply the save one
        // frame too late, which is exactly what went wrong the first time this shipped. Core tier:
        // its own file is what gates it, not the feature gate.
        strategicSaveStore = services.Register(new CommanderStrategicSaveStore(services));
        // After the SAM analyzer, whose strategic height map discovery waits on; before the
        // economy, so the hold state is fresh by the time PayIncome reads it.
        services.Register(new CommanderStrategicPointService(), CommanderTier.Advanced);
        // After the point service, whose list is the operations service's objective list; before
        // the enemy commander (below) so the buyer reads this review's order book, not last
        // review's.
        services.Register(new CommanderOperationsService(), CommanderTier.Advanced);
        CommanderFactionVehicleService factionVehicleService = services.Register(new CommanderFactionVehicleService());
        CommanderSpawnService spawnService = services.Register(
            new CommanderSpawnService(selectionService, factionVehicleService, tacticalMapService),
            CommanderTier.Advanced);
        CommanderEconomyService economyService =
            services.Register(new CommanderEconomyService(), CommanderTier.Advanced);
        services.Register(new CommanderEnemyCommanderService(), CommanderTier.Advanced);
        // After the enemy commander on purpose: the hotkey flips the switch here, so the review
        // that reads it is next frame's, never a half-toggled one inside this frame's loop.
        services.Register(new CommanderPlayerCommanderService(), CommanderTier.Advanced);
        // Core tier: the round has to be able to end whether or not RTS mode is open.
        services.Register(new CommanderVictoryService());
        // Core tier: pilots pile up whether or not the RTS view is open, and only the server may
        // recover them.
        services.Register(new CommanderDownedPilotService());
        // Core tier and unconditional on purpose: the toggle inside CommanderStateStore itself is
        // what gates all of this off by default, not the feature gate — a developer hot-reloading
        // on an unsupported mission still gets nothing written because there is nothing to write.
        stateStore = services.Register(new CommanderStateStore(services));

        CommanderRepairService repairService = services.Register(new CommanderRepairService());
        // Routes tick last so orders act on this frame's spawns and kills.
        CommanderMoveService moveService = services.Register(new CommanderMoveService(selectionService));
        // Capture ticks after the move service because a capture order is a move order: it hands
        // the destination to that service rather than issuing its own for the player's units.
        services.Register(new CommanderCaptureService(selectionService));
        // Route lines live inside the map's icon layer, so they tick after the routes they draw.
        services.Register(new CommanderMapRouteRenderer(selectionService, moveService));

        // The overlay reads every service's state, so it ticks after all of them.
        overlayUi = services.Register(new CommanderOverlayUi(
            selectionService,
            moveService,
            groupService,
            spawnService,
            radarService,
            mobileEmplacementService,
            repairService,
            directPathService,
            supplyHeliService,
            airCommandService,
            navalPurchaseService,
            samSiteAnalyzerService,
            samSiteService,
            economyService,
            UnlockAdvancedFeatures,
            () => Deactivate()));

        // Dead last, so the counts it reads are this frame's finished state rather than a picture
        // taken half way through the schedule. Core tier and persistent-tick: the decay it measures
        // is present while the player is flying with RTS mode closed, so it must keep running there.
        services.Register(new CommanderHealthDiagnostics());

        povCrewUi = new CommanderPovCrewUi(cameraFollowService);
        alertUi = new CommanderAlertUi(alertService, selectionService, () => IsActive);
        inputController = new CommanderInputController(
            overlayUi,
            selectionService,
            spawnService,
            markerService,
            moveService,
            tacticalMapService,
            supplyHeliService,
            mobileEmplacementService,
            airCommandService,
            economyService,
            boxSelectService);
        inputController.SetPovCrewUi(povCrewUi);
        SceneManager.activeSceneChanged += OnActiveSceneChanged;
    }

    private void Update()
    {
        CommanderUiScale.RefreshResolutionPreset();

        // The persistent tick runs before RTS mode is ever opened, and the enemy commander has to
        // know which mission it is in to decide whether the duel rules apply, so the mission name
        // is refreshed here rather than only on activation.
        CommanderFeatureGate.RefreshMission();
        services.TickPersistent(CommanderFeatureGate.AdvancedFeaturesEnabled);
        if (!IsActive)
        {
            return;
        }

        if (CommanderShortcutInput.IsDown(CommanderSettings.ToggleUi))
        {
            overlayUi?.ToggleScreenshotUi();
        }

        if (IsPlayerInOperationalAircraft())
        {
            Deactivate(restorePreviousCamera: false);
            return;
        }

        cursorController?.TickActive();
        services.TickActive(CommanderFeatureGate.AdvancedFeaturesEnabled);
        inputController?.Tick();
    }

    private void FixedUpdate()
    {
        if (IsActive)
        {
            cameraFollowService?.FixedTick();
        }
    }

    private void OnGUI()
    {
        Matrix4x4 previousMatrix = CommanderUiScale.Begin();
        CommanderUiTheme.Ensure();
        // Themed scrollbars/sliders come from the skin, so install it for our draw pass only.
        GUISkin previousSkin = GUI.skin;
        GUI.skin = CommanderUiTheme.Skin;
        try
        {
            if (!IsActive)
            {
                alertUi?.Draw();
                if (ShouldShowCommanderEntry())
                {
                    overlayUi?.DrawInactiveLauncher(Activate);
                }
                return;
            }

            overlayUi?.Draw();
            if (overlayUi?.CommanderUiHidden != true)
            {
                alertUi?.Draw();
                povCrewUi?.Draw();
                boxSelectService?.Draw();
            }
            if (overlayUi?.ShowTacticalMapUi == true)
            {
                tacticalMapService?.DrawControls();
            }
        }
        finally
        {
            GUI.skin = previousSkin;
            CommanderUiScale.End(previousMatrix);
        }
    }

    private bool ShouldShowCommanderEntry()
    {
        if (IsPlayerInOperationalAircraft()
            || (GameManager.gameState != GameState.SinglePlayer && GameManager.gameState != GameState.Multiplayer))
        {
            return false;
        }

        if (DynamicMap.mapMaximized)
        {
            return true;
        }

        if (Time.unscaledTime >= nextInactiveEntryProbeAt)
        {
            nextInactiveEntryProbeAt = Time.unscaledTime + 0.75f;
            aircraftSelectionMenuPresent = UnityEngine.Object.FindObjectOfType<AircraftSelectionMenu>() != null;
        }
        return aircraftSelectionMenuPresent;
    }

    private void OnDisable()
    {
        Deactivate();
    }

    private void OnDestroy()
    {
        // BepInEx ScriptEngine destroys this component along with the rest of the outgoing plugin
        // before installing the reloaded assembly (CommanderModeController already relied on this
        // same OnDestroy for its own camera/cursor teardown, long before this track existed), so
        // this is the shutdown snapshot's one chance to run.
        stateStore?.WriteSnapshotOnDestroy();
        strategicSaveStore?.DetachMissionHook();
        SceneManager.activeSceneChanged -= OnActiveSceneChanged;
        Deactivate(restorePreviousCamera: false);
    }

    private void OnApplicationQuit()
    {
        Deactivate();
    }

    internal void Toggle()
    {
        if (IsActive)
        {
            Deactivate();
            return;
        }

        Activate();
    }

    private void Activate()
    {
        if (IsActive)
        {
            return;
        }

        if (IsPlayerInOperationalAircraft())
        {
            CommanderPlugin.Log.LogWarning("RTS mode is only available while the player is outside an aircraft.");
            return;
        }

        AircraftSelectionMenu? aircraftSelectionMenu = UnityEngine.Object.FindObjectOfType<AircraftSelectionMenu>();
        if (aircraftSelectionMenu != null && aircraftSelectionMenu.gameObject.activeInHierarchy)
        {
            aircraftSelectionMenu.ReturnToMap();
        }

        if (cameraController == null || !cameraController.TryActivate())
        {
            CommanderPlugin.Log.LogWarning("RTS mode could not start because the free camera is not available yet.");
            return;
        }

        CommanderFeatureGate.RefreshMission();
        cursorController?.Activate();
        IsActive = true;
        services.Activate(CommanderFeatureGate.AdvancedFeaturesEnabled);
        OpenTacticalMapIfRequested();
        CommanderPlugin.Log.LogInfo(
            $"RTS mode enabled: mission={CommanderFeatureGate.MissionName}, features={(CommanderFeatureGate.AdvancedFeaturesEnabled ? "full" : "core")}.");
    }

    private void UnlockAdvancedFeatures()
    {
        if (CommanderFeatureGate.AdvancedFeaturesEnabled)
        {
            return;
        }

        CommanderFeatureGate.UnlockAdvancedFeatures();
        if (IsActive)
        {
            services.ActivateAdvanced();
            OpenTacticalMapIfRequested();
        }
        CommanderPlugin.Log.LogWarning(
            $"Advanced RTS features manually unlocked for mission '{CommanderFeatureGate.MissionName}'.");
    }

    private void OpenTacticalMapIfRequested()
    {
        if (CommanderFeatureGate.AdvancedFeaturesEnabled && overlayUi?.ShowTacticalMapUi == true)
        {
            tacticalMapService?.Open();
        }
    }

    private void Deactivate(bool restorePreviousCamera = true)
    {
        if (!IsActive)
        {
            return;
        }

        IsActive = false;
        services.Deactivate();
        tacticalMapService?.Close();
        cursorController?.Deactivate();
        cameraController?.Deactivate(restorePreviousCamera);
        CommanderPlugin.Log.LogInfo("RTS mode disabled.");
    }

    private static bool IsPlayerInOperationalAircraft()
    {
        return GameManager.GetLocalAircraft(out Aircraft aircraft)
            && aircraft != null
            && !aircraft.disabled;
    }

    private void OnActiveSceneChanged(Scene previousScene, Scene newScene)
    {
        try
        {
            Deactivate(restorePreviousCamera: false);
            // Deactivate() is a no-op when the mode was never entered, so these two still have to
            // be nudged by hand: a stale camera follow or drag would otherwise survive the load.
            cameraFollowService?.Deactivate();
            boxSelectService?.Cancel();
        }
        catch (System.Exception exception)
        {
            // The reset below must still run, or the last mission's units and clocks carry over.
            CommanderFaults.Report("Scene change teardown", exception);
        }

        CommanderFeatureGate.ResetSession();
        services.ResetSession();
        aircraftSelectionMenuPresent = false;
        nextInactiveEntryProbeAt = 0f;
    }
}
