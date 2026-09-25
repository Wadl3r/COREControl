using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using UnityEngine;

namespace GroundControlRts;

internal sealed partial class CommanderOverlayUi
{
    /// <summary>
    /// UI scale the player is dragging towards but has not let go of yet; NaN when the slider is
    /// at rest. Applying the scale live rescaled the slider under the cursor mid-drag, so the
    /// thumb ran away from the mouse; the value is now committed on release only.
    /// </summary>
    private float pendingUiScale = float.NaN;

    private void DrawSettingsWindowIfVisible()
    {
        if (settingsVisible)
        {
            settingsWindowRect = GUI.Window(
                SettingsWindowId,
                settingsWindowRect,
                DrawSettingsWindow,
                "COMMANDER SETTINGS",
                CommanderUiTheme.Window);
        }
    }

    private void DrawSettingsWindow(int windowId)
    {
        CaptureBindingInput();
        if (CommanderUiTheme.DrawHelpButton(settingsWindowRect.width, ref settingsHelpVisible))
        {
            CommanderUiTheme.DrawHelpOverlay(
                new Rect(12f, 34f, settingsWindowRect.width - 24f, 74f),
                "Settings are saved in the BepInEx configuration. RTS camera bindings are read only while RTS mode is active and do not alter aircraft controls. "
                    + "Opposition money is the difficulty slider: it multiplies what every faction other than yours earns and what it opens the match holding. Your own faction is never scaled.");
        }
        if (GUI.Button(new Rect(settingsWindowRect.width - 34f, 3f, 26f, 22f), "X", CommanderUiTheme.Button))
        {
            settingsVisible = false;
            bindingCapture = null;
        }

        float y = settingsHelpVisible ? 118f : 38f;
        float tabWidth = (settingsWindowRect.width - 30f) / 6f;
        DrawSettingsTab(new Rect(12f, y, tabWidth, 32f), "GAMEPLAY", 0);
        DrawSettingsTab(new Rect(12f + tabWidth, y, tabWidth, 32f), "UI / HIDE", 1);
        DrawSettingsTab(new Rect(12f + tabWidth * 2f, y, tabWidth, 32f), "CONTROLS", 2);
        DrawSettingsTab(new Rect(12f + tabWidth * 3f, y, tabWidth, 32f), "SHORTCUTS", 3);
        DrawSettingsTab(new Rect(12f + tabWidth * 4f, y, tabWidth, 32f), "CAMERA", 4);
        DrawSettingsTab(new Rect(12f + tabWidth * 5f, y, tabWidth, 32f), "POINTS", 5);
        y += 44f;

        if (settingsTab == 0)
        {
            DrawGameplaySettings(y);
        }
        else if (settingsTab == 1)
        {
            DrawUiSettings(y);
        }
        else if (settingsTab == 3)
        {
            DrawShortcutList(y);
        }
        else if (settingsTab == 4)
        {
            DrawCameraSettings(y);
        }
        else if (settingsTab == 5)
        {
            DrawPointsSettings(y);
        }
        else
        {
            DrawControlSettings(y);
        }

        GUI.DragWindow(new Rect(0f, 0f, settingsWindowRect.width - 72f, 28f));
    }

    /// <summary>
    /// Read-only reference of every shortcut, including the ones that are not remappable
    /// (control groups, camera bookmarks, double-click). Rebuilt on layout events only, so the
    /// per-entry strings are not rebuilt twice a frame while the tab sits open.
    /// </summary>
    private void DrawShortcutList(float y)
    {
        if (Event.current.type == EventType.Layout)
        {
            CommanderShortcutReference.Collect(shortcutEntries);
        }

        float width = settingsWindowRect.width - 24f;
        float height = settingsWindowRect.height - y - 16f;
        GUI.Box(new Rect(12f, y, width, height), string.Empty, CommanderUiTheme.Panel);

        Rect view = new(16f, y + 8f, width - 8f, height - 16f);
        float contentHeight = 0f;
        for (int i = 0; i < shortcutEntries.Count; i++)
        {
            contentHeight += shortcutEntries[i].IsSection ? 34f : 30f;
        }

        Rect inner = new(0f, 0f, view.width - 20f, Mathf.Max(view.height, contentHeight + 4f));
        float keyColumn = inner.width * 0.34f;
        float noteColumn = inner.width * 0.30f;
        shortcutScroll = GUI.BeginScrollView(view, shortcutScroll, inner);
        float rowY = 0f;
        for (int i = 0; i < shortcutEntries.Count; i++)
        {
            CommanderShortcutReference.Entry entry = shortcutEntries[i];
            if (entry.IsSection)
            {
                GUI.Label(new Rect(4f, rowY + 8f, inner.width - 8f, 24f), entry.Action, CommanderUiTheme.Header);
                rowY += 34f;
                continue;
            }

            float actionWidth = inner.width - keyColumn - noteColumn - 16f;
            GUI.Label(new Rect(8f, rowY, actionWidth, 28f), entry.Action, CommanderUiTheme.Label);
            GUI.Label(new Rect(8f + actionWidth, rowY, keyColumn, 28f), entry.Keys, CommanderUiTheme.Header);
            if (entry.Note.Length > 0)
            {
                GUI.Label(new Rect(12f + actionWidth + keyColumn, rowY, noteColumn, 28f), entry.Note, CommanderUiTheme.MutedLabel);
            }
            rowY += 30f;
        }
        GUI.EndScrollView();
    }

    private void DrawSettingsTab(Rect rect, string label, int tab)
    {
        if (GUI.Button(rect, label, settingsTab == tab ? CommanderUiTheme.SelectedButton : CommanderUiTheme.Button))
        {
            settingsTab = tab;
            bindingCapture = null;
        }
    }

    private void DrawGameplaySettings(float y)
    {
        GUI.Box(new Rect(12f, y, settingsWindowRect.width - 24f, 84f), string.Empty, CommanderUiTheme.Panel);
        GUI.Label(new Rect(24f, y + 10f, settingsWindowRect.width - 48f, 22f), "SPAWNING", CommanderUiTheme.Header);
        // Three third-width toggles on one row, because the tab below is already at the window's
        // height limit. The cannon toggle is the AIR window's INTERNAL CANNONS switch (one setting,
        // two places): it applies to every commander's aircraft, and a player who never opens the
        // AIR window could not find it (user, 2026-09-14).
        float spawnThird = (settingsWindowRect.width - 72f) / 3f;
        CommanderSettings.LimitToFactionRoster = GUI.Toggle(
            new Rect(24f, y + 42f, spawnThird, 30f),
            CommanderSettings.LimitToFactionRoster,
            "Own faction roster only",
            CommanderUiTheme.Toggle);
        CommanderSettings.AiAircraftLaunchFromHangar = GUI.Toggle(
            new Rect(36f + spawnThird, y + 42f, spawnThird, 30f),
            CommanderSettings.AiAircraftLaunchFromHangar,
            "AI aircraft from hangars",
            CommanderUiTheme.Toggle);
        // Written through the AIR service, not the setting, so its loadouts re-sort the way the
        // AIR window's own toggle makes them.
        bool stripCannons = GUI.Toggle(
            new Rect(48f + spawnThird * 2f, y + 42f, spawnThird, 30f),
            !CommanderSettings.AirIncludeInternalCannons,
            "Strip internal cannons",
            CommanderUiTheme.Toggle);
        CommanderAirCommandService? air = CommanderAirCommandService.Instance;
        if (air != null)
        {
            air.IncludeInternalCannons = !stripCannons;
        }
        else
        {
            CommanderSettings.AirIncludeInternalCannons = !stripCannons;
        }

        // The COMMAND box carries two commander buttons now. Its rows are on a 32 px pitch rather
        // than 34 so the whole box still fits under the settings window with the help overlay open:
        // the tab starts at y = 162 then, and 162 + 96 + 526 = 784 against a 790-tall window.
        float commandY = y + 96f;
        GUI.Box(new Rect(12f, commandY, settingsWindowRect.width - 24f, 526f), string.Empty, CommanderUiTheme.Panel);
        GUI.Label(new Rect(24f, commandY + 10f, settingsWindowRect.width - 48f, 22f), "COMMAND", CommanderUiTheme.Header);
        CommanderSettings.GroupHotkeys = GUI.Toggle(
            new Rect(24f, commandY + 40f, settingsWindowRect.width - 48f, 30f),
            CommanderSettings.GroupHotkeys,
            "Control group hotkeys 1-9",
            CommanderUiTheme.Toggle);
        CommanderSettings.AttackMoveIntoRange = GUI.Toggle(
            new Rect(24f, commandY + 72f, settingsWindowRect.width - 48f, 30f),
            CommanderSettings.AttackMoveIntoRange,
            "Attack orders stop at weapon range",
            CommanderUiTheme.Toggle);
        CommanderSettings.RetargetAfterKill = GUI.Toggle(
            new Rect(24f, commandY + 104f, settingsWindowRect.width - 48f, 30f),
            CommanderSettings.RetargetAfterKill,
            "Keep attacking after the target dies, then hold the ground",
            CommanderUiTheme.Toggle);
        CommanderSettings.CameraBookmarks = GUI.Toggle(
            new Rect(24f, commandY + 136f, settingsWindowRect.width - 48f, 30f),
            CommanderSettings.CameraBookmarks,
            "Camera bookmarks F1-F4 (assign key + F1-F4 stores)",
            CommanderUiTheme.Toggle);
        CommanderSettings.OrderFeedback = GUI.Toggle(
            new Rect(24f, commandY + 168f, settingsWindowRect.width - 48f, 30f),
            CommanderSettings.OrderFeedback,
            "Flash a marker where an order was given",
            CommanderUiTheme.Toggle);
        CommanderSettings.AttackMoveRoutes = GUI.Toggle(
            new Rect(24f, commandY + 200f, settingsWindowRect.width - 48f, 30f),
            CommanderSettings.AttackMoveRoutes,
            "Attack-move: Free Fire units engage hostiles they pass",
            CommanderUiTheme.Toggle);
        CommanderSettings.GuardOrders = GUI.Toggle(
            new Rect(24f, commandY + 232f, settingsWindowRect.width - 48f, 30f),
            CommanderSettings.GuardOrders,
            "Right click a friendly unit to guard it",
            CommanderUiTheme.Toggle);
        CommanderSettings.AutoRetreatDamaged = GUI.Toggle(
            new Rect(24f, commandY + 264f, settingsWindowRect.width - 48f, 30f),
            CommanderSettings.AutoRetreatDamaged,
            $"Retreat to repair below {CommanderSettings.RetreatConditionPercent:0}% condition",
            CommanderUiTheme.Toggle);
        CommanderSettings.CombatAlerts = GUI.Toggle(
            new Rect(24f, commandY + 296f, settingsWindowRect.width - 48f, 30f),
            CommanderSettings.CombatAlerts,
            "Alert when units are attacked or lost",
            CommanderUiTheme.Toggle);

        int enemySetting = CommanderSettings.EnemyCommanderMode;
        int enemyMode = CommanderEnemyCommanderService.EffectiveMode;
        if (GUI.Button(
            new Rect(24f, commandY + 334f, settingsWindowRect.width - 48f, 32f),
            $"ENEMY COMMANDER: {CommanderEnemyCommanderService.GetModeLabel(enemyMode)}"
                + (enemySetting == CommanderEnemyCommanderService.ModeOff && enemyMode != CommanderEnemyCommanderService.ModeOff
                    ? "  (SET BY MISSION)"
                    : string.Empty)
                + (enemyMode > 0 ? $"   ({CommanderEnemyCommanderService.Instance?.TotalPurchases ?? 0} bought)" : string.Empty),
            enemyMode > 0 ? CommanderUiTheme.DangerButton : CommanderUiTheme.Button))
        {
            CommanderSettings.EnemyCommanderMode =
                enemySetting >= CommanderEnemyCommanderService.ModeMission ? 0 : enemySetting + 1;
        }

        // Host only, exactly like the enemy commander: every loop the switch unlocks is guarded by
        // hq.IsServer, so a client that had the setting saved as on does nothing with it. Showing
        // the button disabled says that, where a button that did nothing would not.
        FactionHQ? hq = CommanderGameAccess.GetLocalHq();
        bool host = hq == null || hq.IsServer;
        bool playerCommander = CommanderSettings.PlayerCommanderEnabled;
        bool oldEnabled = GUI.enabled;
        GUI.enabled = oldEnabled && host;
        if (GUI.Button(
            new Rect(24f, commandY + 372f, settingsWindowRect.width - 48f, 32f),
            $"PLAYER COMMANDER: {(playerCommander ? "ON" : "OFF")}"
                + (playerCommander
                    ? $"   ({CommanderEnemyCommanderService.Instance?.PlayerPurchases ?? 0} bought)"
                    : string.Empty)
                + (host ? string.Empty : "   (HOST ONLY)"),
            playerCommander ? CommanderUiTheme.DangerButton : CommanderUiTheme.Button))
        {
            CommanderPlayerCommanderService.Instance?.Toggle();
        }
        GUI.enabled = oldEnabled;

        // Both radii are here rather than only in the config file because both are map-dependent:
        // how tight a base perimeter feels, and whether a faction can reach the coast at all, are
        // answers you only get by looking at the map you are on.
        CommanderSettings.BuildRadiusKm = DrawRadiusSlider(
            commandY + 412f,
            "Build radius",
            CommanderSettings.BuildRadiusKm,
            1f,
            15f);
        CommanderSettings.NavalDockRadiusKm = DrawRadiusSlider(
            commandY + 450f,
            "Naval dock radius",
            CommanderSettings.NavalDockRadiusKm,
            1f,
            25f);

        // Two balance knobs share this last row. The box above is already at the window's height
        // limit with the help overlay open (see the comment on commandY), so a tenth full-width row
        // does not fit; both of these are worth-and-money numbers, so they pair up at half width.
        //
        // Aircraft capture: how much an aircraft parked in a capture ring is worth. It is a balance
        // number the mod invents - the base game gives an aeroplane no capture strength at all - so
        // it belongs where it can be turned down, or off, without editing a config file.
        //
        // Opposition money: the difficulty knob (user instruction, 2026-09-16). It multiplies both
        // what every faction that is not yours earns and what it starts the match with; your own
        // faction is never scaled, whoever commands it. It reads as a plain multiplier with the
        // number shown rather than as named steps, on the user's instruction. IMGUI has no tooltip
        // here, so the window's help overlay carries the explanation in words.
        float balanceRowY = commandY + 488f;
        float balanceColumn = (settingsWindowRect.width - 60f) * 0.5f;
        float capture = CommanderSettings.AircraftCaptureStrength;
        CommanderSettings.AircraftCaptureStrength = Mathf.Round(
            DrawBalanceSlider(24f, balanceRowY, balanceColumn, "Aircraft capture", capture, 0f, 10f, "0.#", string.Empty) * 2f) * 0.5f;

        float difficulty = CommanderSettings.EnemyIncomeMultiplier;
        CommanderSettings.EnemyIncomeMultiplier = Mathf.Round(
            DrawBalanceSlider(
                36f + balanceColumn,
                balanceRowY,
                balanceColumn,
                "Opposition money",
                difficulty,
                CommanderEconomyService.EnemyDifficultyMin,
                CommanderEconomyService.EnemyDifficultyMax,
                "0.00",
                "x") * 20f) / 20f;
    }

    /// <summary>
    /// A labelled slider that shares its row with a second one, positioned against a caller-supplied
    /// column instead of the whole window. Same shape as <see cref="DrawCameraSlider"/>; half width
    /// because the COMMAND box is already at the settings window's height limit with the help
    /// overlay open, so a further full-width row would push it off the bottom.
    /// </summary>
    private static float DrawBalanceSlider(
        float x, float y, float columnWidth, string label, float value, float min, float max, string format, string suffix)
    {
        float labelWidth = columnWidth * 0.56f;
        GUI.Label(
            new Rect(x, y, labelWidth, 24f),
            $"{label}   {value.ToString(format)}{suffix}",
            CommanderUiTheme.Label);
        float slid = GUI.HorizontalSlider(
            new Rect(x + labelWidth + 6f, y + 6f, columnWidth - labelWidth - 6f, 20f),
            value,
            min,
            max);
        return Mathf.Clamp(slid, min, max);
    }

    /// <summary>A labelled kilometre slider, snapped to a half kilometre so the readout is honest.</summary>
    private float DrawRadiusSlider(float y, string label, float value, float min, float max)
    {
        GUI.Label(
            new Rect(24f, y, 220f, 24f),
            $"{label}   {value:0.#} km",
            CommanderUiTheme.Label);
        float slid = GUI.HorizontalSlider(
            new Rect(250f, y + 6f, settingsWindowRect.width - 274f, 20f),
            value,
            min,
            max);
        return Mathf.Round(Mathf.Clamp(slid, min, max) * 2f) * 0.5f;
    }

    /// <summary>
    /// Everything about how the camera feels. It is a whole tab rather than a few config lines
    /// because a camera is tuned by moving it, not by reading numbers: every value here wants to
    /// be dragged while the game is running.
    /// </summary>
    private void DrawCameraSettings(float y)
    {
        float width = settingsWindowRect.width - 24f;
        GUI.Box(new Rect(12f, y, width, 268f), string.Empty, CommanderUiTheme.Panel);
        GUI.Label(new Rect(24f, y + 10f, width - 24f, 22f), "MOVEMENT", CommanderUiTheme.Header);
        CommanderSettings.CameraPanSpeed = DrawCameraSlider(
            y + 40f, "Pan speed", CommanderSettings.CameraPanSpeed, 50f, 1200f, "0", " m/s");
        CommanderSettings.CameraZoomSpeed = DrawCameraSlider(
            y + 78f, "Zoom speed", CommanderSettings.CameraZoomSpeed, 0.2f, 3f, "0.0#", "x");
        CommanderSettings.CameraLookSensitivity = DrawCameraSlider(
            y + 116f, "Look sensitivity", CommanderSettings.CameraLookSensitivity, 0.1f, 4f, "0.0#", "x");
        CommanderSettings.CameraSmoothing = DrawCameraSlider(
            y + 154f, "Smoothing", CommanderSettings.CameraSmoothing, 0f, 0.4f, "0.00", " s");
        CommanderSettings.CameraHeightScaledSpeed = GUI.Toggle(
            new Rect(24f, y + 190f, width - 48f, 30f),
            CommanderSettings.CameraHeightScaledSpeed,
            "Pan speed scales with height above ground",
            CommanderUiTheme.Toggle);
        CommanderSettings.CameraEdgeScroll = GUI.Toggle(
            new Rect(24f, y + 224f, width - 48f, 30f),
            CommanderSettings.CameraEdgeScroll,
            "Edge scrolling (push the cursor into a screen edge)",
            CommanderUiTheme.Toggle);

        float lookY = y + 280f;
        GUI.Box(new Rect(12f, lookY, width, 116f), string.Empty, CommanderUiTheme.Panel);
        GUI.Label(new Rect(24f, lookY + 10f, width - 24f, 22f), "LOOKING AROUND", CommanderUiTheme.Header);
        CommanderSettings.CameraOrbitLook = GUI.Toggle(
            new Rect(24f, lookY + 40f, width - 48f, 30f),
            CommanderSettings.CameraOrbitLook,
            "Hold look to orbit the point under the cursor",
            CommanderUiTheme.Toggle);
        GUI.Label(
            new Rect(24f, lookY + 74f, width - 48f, 34f),
            $"Hold {CommanderSettings.CameraFreeLook} and move the mouse. Off, the camera turns in place and "
                + "whatever you were watching slides off screen. The wheel zooms toward the cursor.",
            CommanderUiTheme.MutedLabel);

        float followY = lookY + 128f;
        GUI.Box(new Rect(12f, followY, width, 152f), string.Empty, CommanderUiTheme.Panel);
        GUI.Label(new Rect(24f, followY + 10f, width - 24f, 22f), "SELECTION AND FOLLOW", CommanderUiTheme.Header);
        CommanderSettings.AutoFollowSelection = GUI.Toggle(
            new Rect(24f, followY + 40f, width - 48f, 30f),
            CommanderSettings.AutoFollowSelection,
            "Follow the camera on the selected unit",
            CommanderUiTheme.Toggle);
        CommanderSettings.AutoFrameSelection = GUI.Toggle(
            new Rect(24f, followY + 74f, width - 48f, 30f),
            CommanderSettings.AutoFrameSelection,
            "Travel to a selected unit only when it is off screen",
            CommanderUiTheme.Toggle);
        GUI.Label(
            new Rect(24f, followY + 108f, width - 48f, 34f),
            $"Selecting something you can already see leaves the camera alone. {CommanderSettings.CameraCenterFollow} "
                + "always centres on the selection immediately.",
            CommanderUiTheme.MutedLabel);

        float mapY = followY + 164f;
        GUI.Box(new Rect(12f, mapY, width, 82f), string.Empty, CommanderUiTheme.Panel);
        GUI.Label(new Rect(24f, mapY + 10f, width - 24f, 22f), "TACTICAL MAP", CommanderUiTheme.Header);
        CommanderSettings.MapDragSpeed = DrawCameraSlider(
            mapY + 40f, "Map drag speed", CommanderSettings.MapDragSpeed, 0.25f, 4f, "0.0#", "x");
    }

    /// <summary>A labelled slider that shows the value it is about to write.</summary>
    private float DrawCameraSlider(float y, string label, float value, float min, float max, string format, string suffix)
    {
        GUI.Label(
            new Rect(24f, y, 230f, 24f),
            $"{label}   {value.ToString(format)}{suffix}",
            CommanderUiTheme.Label);
        float slid = GUI.HorizontalSlider(
            new Rect(260f, y + 6f, settingsWindowRect.width - 284f, 20f),
            value,
            min,
            max);
        return Mathf.Clamp(slid, min, max);
    }

    /// <summary>
    /// Departure 2: the design asks for these on the Gameplay tab, but that tab's COMMAND box
    /// already reaches 784 px of a 790 px window with the help overlay open (see the comment on
    /// <see cref="DrawGameplaySettings"/>), and six more 38 px rows do not fit. One tab further
    /// right, same sliders, same behaviour.
    /// </summary>
    /// <remarks>
    /// Arithmetic (help closed): box top at <c>y</c>, header 10 + 32, nine 38 px slider rows (three
    /// more since the outpost/crossroads/roadside incomes joined base/village/hilltop/gold mine), a
    /// 30 px footnote — 10 + 32 + 9*38 + 30 = 414 px tall.
    /// <para>
    /// Departure 7: a second OPERATIONS box joins it below an 8 px gap — 414 + 8 + 300 = 722 px of
    /// content (the box grew one row for the insertion off-road gate, 10 + 32 + 6*38 + 30) — which
    /// no longer fits inside the window at every help state, so the tab's content now lives inside
    /// a scroll view (the <c>DrawShortcutList</c> shape), and only scrolls when it has to.
    /// </para>
    /// </remarks>
    private void DrawPointsSettings(float y)
    {
        const float pointsBoxHeight = 414f;
        const float strategicSaveBoxHeight = 190f;
        const float gap = 8f;
        float contentHeight = pointsBoxHeight + gap + OperationsBoxHeight + gap + strategicSaveBoxHeight;

        float width = settingsWindowRect.width - 24f;
        float height = settingsWindowRect.height - y - 16f;
        GUI.Box(new Rect(12f, y, width, height), string.Empty, CommanderUiTheme.Panel);

        Rect view = new(16f, y + 8f, width - 8f, height - 16f);
        Rect inner = new(0f, 0f, view.width - 20f, Mathf.Max(view.height, contentHeight + 4f));
        pointsSettingsScroll = GUI.BeginScrollView(view, pointsSettingsScroll, inner);

        DrawStrategicPointsBox(0f, inner.width);
        DrawOperationsBox(pointsBoxHeight + gap, inner.width);
        DrawStrategicSaveBox(pointsBoxHeight + gap + OperationsBoxHeight + gap, inner.width);

        GUI.EndScrollView();
    }

    /// <summary>
    /// The strategic save (track <c>strategic-save_20260917</c>): stop a long match, restart the
    /// mission to clear the frame-rate decay, then carry on with the same points held, the same
    /// forward bases and a war chest holding the money plus the cash value of the army that was
    /// standing.
    /// </summary>
    /// <remarks>
    /// Buttons rather than a key binding, because this is a rare, deliberate action that wants its
    /// status line next to it — the developer needs to see that a save exists and when it was taken
    /// before restarting the mission, and a key binding has nowhere to say that. The save itself is
    /// only REQUESTED here: writing a file from inside IMGUI would stall the frame the button is
    /// drawn on, so the write happens on the mod's own persistent tick
    /// (<see cref="CommanderStrategicSaveStore.RequestSave"/>).
    /// </remarks>
    private void DrawStrategicSaveBox(float y, float width)
    {
        GUI.Box(new Rect(4f, y, width - 8f, 190f), string.Empty, CommanderUiTheme.Panel);
        GUI.Label(new Rect(16f, y + 10f, width - 32f, 22f), "STRATEGIC SAVE", CommanderUiTheme.Header);

        GUI.Label(
            new Rect(16f, y + 36f, width - 32f, 40f),
            "Saves who holds what, the forward bases, and each faction's money plus the cash value of "
                + "everything standing. Restart the mission to load it. Units are refunded, never rebuilt.",
            CommanderUiTheme.MutedLabel);

        float buttonWidth = (width - 44f) / 2f;
        if (GUI.Button(new Rect(16f, y + 82f, buttonWidth, 30f), "SAVE STRATEGIC STATE", CommanderUiTheme.Button))
        {
            CommanderStrategicSaveStore.Instance?.RequestSave();
        }

        if (GUI.Button(new Rect(28f + buttonWidth, y + 82f, buttonWidth, 30f), "DISCARD SAVE", CommanderUiTheme.Button))
        {
            CommanderStrategicSaveStore.DiscardSave();
        }

        CommanderSettings.StrategicGarrisonPaid = GUI.Toggle(
            new Rect(16f, y + 118f, width - 32f, 30f),
            CommanderSettings.StrategicGarrisonPaid,
            "Garrisons on load are paid for out of the war chest",
            CommanderUiTheme.Toggle);

        string status = CommanderStrategicSaveStore.StatusText;
        if (string.IsNullOrEmpty(status))
        {
            status = CommanderStrategicSaveStore.HasSaveFor(CommanderFeatureGate.MissionName)
                ? "A save is waiting: it loads when this mission next starts."
                : "No save for this mission.";
        }

        GUI.Label(new Rect(16f, y + 152f, width - 32f, 24f), status, CommanderUiTheme.MutedLabel);
    }

    private void DrawStrategicPointsBox(float y, float width)
    {
        GUI.Box(new Rect(4f, y, width - 8f, 414f), string.Empty, CommanderUiTheme.Panel);
        GUI.Label(new Rect(16f, y + 10f, width - 32f, 22f), "STRATEGIC POINTS", CommanderUiTheme.Header);

        float rowY = y + 42f;
        // "Garrison per point" since unit-economy_20260918 §2.2: the number is the standing garrison
        // AND the ownership threshold, and "minimum" read as though something else decided the real
        // size. One vehicle is the default now.
        CommanderSettings.PointsMinGarrison = Mathf.RoundToInt(DrawPointsSlider(
            rowY, width, "Garrison per point", CommanderSettings.PointsMinGarrison, 1f, 6f, "0", " vehicles"));
        rowY += 38f;

        CommanderSettings.PointsHoldSeconds = Mathf.Round(DrawPointsSlider(
            rowY, width, "Hold seconds", CommanderSettings.PointsHoldSeconds, 15f, 300f, "0", " s") / 5f) * 5f;
        rowY += 38f;

        CommanderSettings.PointsBaseIncomePerMinute = Mathf.Round(DrawPointsSlider(
            rowY, width, "Base income", CommanderSettings.PointsBaseIncomePerMinute, 0f, 100f, "0", " /min"));
        rowY += 38f;

        CommanderSettings.PointsVillageIncomePerMinute = Mathf.Round(DrawPointsSlider(
            rowY, width, "Village income", CommanderSettings.PointsVillageIncomePerMinute, 0f, 50f, "0", " /min"));
        rowY += 38f;

        CommanderSettings.PointsHilltopIncomePerMinute = Mathf.Round(DrawPointsSlider(
            rowY, width, "Hilltop income", CommanderSettings.PointsHilltopIncomePerMinute, 0f, 50f, "0", " /min"));
        rowY += 38f;

        CommanderSettings.PointsOutpostIncomePerMinute = Mathf.Round(DrawPointsSlider(
            rowY, width, "Outpost income", CommanderSettings.PointsOutpostIncomePerMinute, 0f, 50f, "0", " /min"));
        rowY += 38f;

        CommanderSettings.PointsCrossroadsIncomePerMinute = Mathf.Round(DrawPointsSlider(
            rowY, width, "Crossroads income", CommanderSettings.PointsCrossroadsIncomePerMinute, 0f, 50f, "0", " /min"));
        rowY += 38f;

        CommanderSettings.PointsRoadsideIncomePerMinute = Mathf.Round(DrawPointsSlider(
            rowY, width, "Roadside income", CommanderSettings.PointsRoadsideIncomePerMinute, 0f, 50f, "0", " /min"));
        rowY += 38f;

        CommanderSettings.GoldMineIncomePerMinute = Mathf.Round(DrawPointsSlider(
            rowY, width, "Gold mine income", CommanderSettings.GoldMineIncomePerMinute, 0f, 100f, "0", " /min"));
        rowY += 38f;

        GUI.Label(
            new Rect(16f, rowY + 4f, width - 32f, 22f),
            "Discovery spacing lives in the config file, Points section.",
            CommanderUiTheme.MutedLabel);
    }

    /// <summary>
    /// Departure 7: platoon size, FOB share, front range, pressure interval and offensive spend,
    /// plus the insertion off-road gate. Platoon recipe (armour/carrier/air-defence slot counts) and
    /// the insertion limit/cooldown stay config-file-only, as the footnote here says — they are
    /// balance decisions, not taste knobs (see <c>Core/CommanderSettings.cs</c>'s own comment on
    /// the section).
    /// </summary>
    /// <summary>
    /// How tall the OPERATIONS box is: the 10 px top inset plus a 32 px header, thirteen 38 px slider
    /// rows and a 34 px footnote — 570. The twelfth and thirteenth rows are the attack allowance
    /// (concurrent-attacks_20260918), which sits beside the ground ceiling because both are about how
    /// much army the commander commits and where. A named constant since unit-economy_20260918 added three
    /// rows to it, because the height was written out twice, once here and once in the scroll view's
    /// content measurement, and the two had already drifted apart by a row. The eleventh row is the
    /// air patrol reserve (air-ceiling_20260918), which sits with the ground ceiling because the two
    /// are the same idea applied to the two halves of the army.
    /// </summary>
    private const float OperationsBoxHeight = 570f;

    private void DrawOperationsBox(float y, float width)
    {
        GUI.Box(new Rect(4f, y, width - 8f, OperationsBoxHeight), string.Empty, CommanderUiTheme.Panel);
        GUI.Label(new Rect(16f, y + 10f, width - 32f, 22f), "OPERATIONS", CommanderUiTheme.Header);

        float rowY = y + 42f;
        CommanderSettings.OperationsPlatoonSize = Mathf.RoundToInt(DrawPointsSlider(
            rowY, width, "Platoon size", CommanderSettings.OperationsPlatoonSize, 2f, 10f, "0", " vehicles"));
        rowY += 38f;

        CommanderSettings.OperationsFobShare = DrawPointsSlider(
            rowY, width, "Forward-base share", CommanderSettings.OperationsFobShare, 0f, 1f, "0.00", string.Empty);
        rowY += 38f;

        CommanderSettings.OperationsFrontRangeMeters = Mathf.Round(DrawPointsSlider(
            rowY, width, "Front range", CommanderSettings.OperationsFrontRangeMeters / 1000f, 5f, 40f, "0", " km") * 1000f);
        rowY += 38f;

        CommanderSettings.OperationsPressureIntervalMinutes = DrawPointsSlider(
            rowY, width, "Pressure interval", CommanderSettings.OperationsPressureIntervalMinutes, 4f, 30f, "0", " min");
        rowY += 38f;

        CommanderSettings.OperationsOffensiveSpendFraction = DrawPointsSlider(
            rowY, width, "Offensive spend", CommanderSettings.OperationsOffensiveSpendFraction, 0.1f, 1f, "0.00", string.Empty);
        rowY += 38f;

        CommanderSettings.OperationsHeliInsertionOffRoadMeters = Mathf.Round(DrawPointsSlider(
            rowY, width, "Insertion off-road", CommanderSettings.OperationsHeliInsertionOffRoadMeters / 1000f, 0f, 10f, "0", " km") * 1000f);
        rowY += 38f;

        CommanderSettings.OperationsMaxPreemptiveAirObjectives = Mathf.RoundToInt(DrawPointsSlider(
            rowY, width, "Pre-emptive air cover", CommanderSettings.OperationsMaxPreemptiveAirObjectives, 0f, 12f, "0", " marches"));
        rowY += 38f;

        // The unit economy (design.md, unit-economy_20260918 §4), all three tunable in play because
        // the defaults are starting points rather than conclusions. The fourth setting of that
        // design, the garrison per point, is one box up with the rest of the points rules — it is
        // the ownership threshold as well, and it belongs beside the hold time it works with.
        CommanderSettings.GroundUnitCeiling = Mathf.RoundToInt(DrawPointsSlider(
            rowY, width, "Ground ceiling", CommanderSettings.GroundUnitCeiling, 0f, 300f, "0", " vehicles"));
        rowY += 38f;

        // Beside the ground ceiling on purpose: this is the same rule for the other half of the army,
        // and the number a reader wants when they have just moved the one above it.
        CommanderSettings.AirPatrolReserve = Mathf.RoundToInt(DrawPointsSlider(
            rowY, width, "Air patrol reserve", CommanderSettings.AirPatrolReserve, 0f, 20f, "0", " slots"));
        rowY += 38f;

        CommanderSettings.MaxAttacks = Mathf.RoundToInt(DrawPointsSlider(
            rowY, width, "Attacks at once", CommanderSettings.MaxAttacks, 0f, 8f, "0", " attacks"));
        rowY += 38f;

        CommanderSettings.AttacksPerPlatoon = DrawPointsSlider(
            rowY, width, "Attacks per platoon", CommanderSettings.AttacksPerPlatoon, 0f, 1f, "0.00", string.Empty);
        rowY += 38f;

        CommanderSettings.IdleReserveMinutes = Mathf.Round(DrawPointsSlider(
            rowY, width, "Idle reserve timeout", CommanderSettings.IdleReserveMinutes, 0f, 15f, "0", " min"));
        rowY += 38f;

        CommanderSettings.QuietGroundMinutes = Mathf.Round(DrawPointsSlider(
            rowY, width, "Quiet ground timeout", CommanderSettings.QuietGroundMinutes, 0f, 30f, "0", " min"));
        rowY += 38f;

        GUI.Label(
            new Rect(16f, rowY + 4f, width - 32f, 22f),
            "Platoon recipe, insertion limit and cooldown live in the config file, Operations section.",
            CommanderUiTheme.MutedLabel);
    }

    /// <summary>A labelled slider inside the POINTS tab's scroll view — <see cref="DrawCameraSlider"/>'s
    /// shape, but positioned against a caller-supplied width instead of the settings window's own,
    /// since it draws inside scrolled content rather than directly in the window.</summary>
    private static float DrawPointsSlider(
        float y, float width, string label, float value, float min, float max, string format, string suffix)
    {
        GUI.Label(new Rect(16f, y, 230f, 24f), $"{label}   {value.ToString(format)}{suffix}", CommanderUiTheme.Label);
        float slid = GUI.HorizontalSlider(new Rect(252f, y + 6f, width - 276f, 20f), value, min, max);
        return Mathf.Clamp(slid, min, max);
    }

    private void DrawUiSettings(float y)
    {
        GUI.Box(new Rect(12f, y, settingsWindowRect.width - 24f, 386f), string.Empty, CommanderUiTheme.Panel);
        float left = 28f;
        float right = settingsWindowRect.width * 0.5f + 10f;
        float width = settingsWindowRect.width * 0.5f - 40f;
        showCommandButton = GUI.Toggle(new Rect(left, y + 16f, width, 28f), showCommandButton, "Command button", CommanderUiTheme.Toggle);
        showFactionMoney = GUI.Toggle(new Rect(right, y + 16f, width, 28f), showFactionMoney, "Faction funds", CommanderUiTheme.Toggle);
        showTacticalMap = GUI.Toggle(new Rect(left, y + 50f, width, 28f), showTacticalMap, "Tactical map", CommanderUiTheme.Toggle);
        showSelectionBar = GUI.Toggle(new Rect(right, y + 50f, width, 28f), showSelectionBar, "Selection bar", CommanderUiTheme.Toggle);
        showPinnedUnits = GUI.Toggle(new Rect(left, y + 84f, width, 28f), showPinnedUnits, "Unit / mission list", CommanderUiTheme.Toggle);
        showUnitSystems = GUI.Toggle(new Rect(right, y + 84f, width, 28f), showUnitSystems, "Unit systems", CommanderUiTheme.Toggle);
        showDepotUi = GUI.Toggle(new Rect(left, y + 118f, width, 28f), showDepotUi, "Depot UI", CommanderUiTheme.Toggle);
        showSupplyUi = GUI.Toggle(new Rect(right, y + 118f, width, 28f), showSupplyUi, "Supply UI", CommanderUiTheme.Toggle);
        showAirCommandUi = GUI.Toggle(new Rect(left, y + 152f, width, 28f), showAirCommandUi, "Air Command UI", CommanderUiTheme.Toggle);
        showNavalUi = GUI.Toggle(new Rect(right, y + 152f, width, 28f), showNavalUi, "Naval UI", CommanderUiTheme.Toggle);
        showWorldMarkers = GUI.Toggle(new Rect(left, y + 186f, width, 28f), showWorldMarkers, "World markers", CommanderUiTheme.Toggle);
        showSamAnalyzerUi = GUI.Toggle(new Rect(right, y + 186f, width, 28f), showSamAnalyzerUi, "SAM analyzer UI", CommanderUiTheme.Toggle);
        showUnitListUi = GUI.Toggle(new Rect(left, y + 220f, width, 28f), showUnitListUi, "Order of battle", CommanderUiTheme.Toggle);
        showBuildUi = GUI.Toggle(new Rect(right, y + 220f, width, 28f), showBuildUi, "Build UI", CommanderUiTheme.Toggle);

        SaveUiVisibilitySettings();

        float effectiveUiScale = CommanderSettings.UiScale;
        bool dragging = !float.IsNaN(pendingUiScale);
        // While the mouse is down the slider shows the value being dragged to, not the one in
        // force, so the readout follows the thumb but the UI itself holds still.
        float requestedUiScale = DrawCameraSlider(
            y + 260f,
            "UI scale",
            dragging ? pendingUiScale : effectiveUiScale,
            CommanderUiScale.MinOverride,
            CommanderUiScale.MaxOverride,
            "0.00",
            "x");
        if (!Mathf.Approximately(requestedUiScale, dragging ? pendingUiScale : effectiveUiScale))
        {
            pendingUiScale = requestedUiScale;
            dragging = true;
        }

        // Commit on release. Writing only when the player actually moved the slider matters too:
        // an untouched slider hands back the value it was given, and storing that would silently
        // convert an automatic scale into a manual override that no window resize could ever
        // update again. BepInEx also writes the config file whenever an entry's Value changes.
        if (dragging && !Input.GetMouseButton(0))
        {
            if (!Mathf.Approximately(pendingUiScale, effectiveUiScale))
            {
                CommanderSettings.UiScaleOverride = pendingUiScale;
            }

            pendingUiScale = float.NaN;
        }

        bool automaticUiScaleActive = CommanderSettings.UiScaleOverride <= 0f;
        if (GUI.Button(new Rect(28f, y + 292f, 110f, 26f), "AUTO", CommanderUiTheme.Button))
        {
            CommanderSettings.UiScaleOverride = 0f;
        }

        string automaticSuffix = automaticUiScaleActive ? " (active)" : string.Empty;
        GUI.Label(
            new Rect(148f, y + 292f, settingsWindowRect.width - 176f, 26f),
            $"Automatic for {Screen.width} x {Screen.height}: {CommanderSettings.AutomaticUiScale:0.##}x{automaticSuffix}",
            CommanderUiTheme.MutedLabel);
        GUI.Label(
            new Rect(28f, y + 326f, settingsWindowRect.width - 56f, 20f),
            $"{CommanderSettings.ToggleUi} cycles visible, RTS UI hidden, and all UI hidden.",
            CommanderUiTheme.MutedLabel);
        if (GUI.Button(new Rect(28f, y + 350f, settingsWindowRect.width - 56f, 30f), "RESET UI LAYOUT", CommanderUiTheme.Button))
        {
            ResetUiLayout();
        }
    }

    private void SaveUiVisibilitySettings()
    {
        CommanderSettings.ShowCommandButton = showCommandButton;
        CommanderSettings.ShowFactionMoney = showFactionMoney;
        CommanderSettings.ShowTacticalMap = showTacticalMap;
        CommanderSettings.ShowSelectionBar = showSelectionBar;
        CommanderSettings.ShowPinnedUnits = showPinnedUnits;
        CommanderSettings.ShowUnitSystems = showUnitSystems;
        CommanderSettings.ShowDepotUi = showDepotUi;
        CommanderSettings.ShowSupplyUi = showSupplyUi;
        CommanderSettings.ShowAirCommandUi = showAirCommandUi;
        CommanderSettings.ShowNavalUi = showNavalUi;
        CommanderSettings.ShowSamAnalyzerUi = showSamAnalyzerUi;
        CommanderSettings.ShowWorldMarkers = showWorldMarkers;
        CommanderSettings.ShowUnitListUi = showUnitListUi;
        CommanderSettings.ShowBuildUi = showBuildUi;
    }

    private void DrawControlSettings(float y)
    {
        GUI.Box(new Rect(12f, y, settingsWindowRect.width - 24f, 628f), string.Empty, CommanderUiTheme.Panel);
        GUI.Label(
            new Rect(24f, y + 8f, settingsWindowRect.width - 48f, 32f),
            "Bindings are active only in RTS mode. Click one, then press a keyboard or mouse button. Escape cancels.",
            CommanderUiTheme.MutedLabel);

        float columnWidth = (settingsWindowRect.width - 66f) * 0.5f;
        float left = 24f;
        float right = 42f + columnWidth;
        GUI.Label(new Rect(left, y + 42f, columnWidth, 22f), "CAMERA", CommanderUiTheme.Header);
        GUI.Label(new Rect(right, y + 42f, columnWidth, 22f), "COMMANDER ACTIONS", CommanderUiTheme.Header);
        float rowY = y + 68f;
        DrawBinding(new Rect(left, rowY, columnWidth, 30f), "Forward", "forward");
        DrawBinding(new Rect(left, rowY + 32f, columnWidth, 30f), "Backward", "backward");
        DrawBinding(new Rect(left, rowY + 64f, columnWidth, 30f), "Move left", "left");
        DrawBinding(new Rect(left, rowY + 96f, columnWidth, 30f), "Move right", "right");
        DrawBinding(new Rect(left, rowY + 128f, columnWidth, 30f), "Move up", "up");
        DrawBinding(new Rect(left, rowY + 160f, columnWidth, 30f), "Move down", "down");
        DrawBinding(new Rect(left, rowY + 192f, columnWidth, 30f), "Free look", "look");
        DrawBinding(new Rect(left, rowY + 224f, columnWidth, 30f), "Speed boost", "boost");
        Rect centerFollowRect = new(left, rowY + 256f, columnWidth, 30f);
        DrawBinding(centerFollowRect, "Center / follow", "center_follow");
        DrawBinding(new Rect(left, rowY + 288f, columnWidth, 30f), "Free look (2nd)", "look_alt");

        DrawBinding(new Rect(right, rowY, columnWidth, 30f), "Select / place", "primary");
        DrawBinding(new Rect(right, rowY + 32f, columnWidth, 30f), "Move / order", "secondary");
        DrawBinding(new Rect(right, rowY + 64f, columnWidth, 30f), "Add selection", "add_selection");
        DrawBinding(new Rect(right, rowY + 96f, columnWidth, 30f), "Repeat deploy", "repeat_deploy");
        DrawBinding(new Rect(right, rowY + 128f, columnWidth, 30f), "Delete modifier", "delete_modifier");
        DrawBinding(new Rect(right, rowY + 160f, columnWidth, 30f), "UI cycle", "toggle_ui");
        DrawBinding(new Rect(right, rowY + 192f, columnWidth, 30f), "Queue point", "queue_waypoint");
        DrawBinding(new Rect(right, rowY + 224f, columnWidth, 30f), "Assign group", "assign_group");
        DrawBinding(new Rect(right, rowY + 256f, columnWidth, 30f), "Stop order", "stop_order");
        DrawBinding(new Rect(right, rowY + 288f, columnWidth, 30f), "Same type", "same_type");
        DrawBinding(new Rect(right, rowY + 320f, columnWidth, 30f), "Cycle idle", "cycle_idle");
        DrawBinding(new Rect(right, rowY + 352f, columnWidth, 30f), "Map box select", "map_box_select");
        DrawBinding(new Rect(right, rowY + 384f, columnWidth, 30f), "Player commander", "toggle_player_commander");
        DrawBinding(new Rect(right, rowY + 416f, columnWidth, 30f), "Rotate build left", "rotate_left");
        DrawBinding(new Rect(right, rowY + 448f, columnWidth, 30f), "Rotate build right", "rotate_right");
        DrawBinding(new Rect(right, rowY + 480f, columnWidth, 30f), "Sit on slope", "conform_ground");

        if (centerFollowRect.Contains(Event.current.mousePosition))
        {
            CommanderUiTheme.DrawHelpOverlay(
                new Rect(left, rowY + 324f, columnWidth, 68f),
                "Press briefly to center on the selected unit. Hold to center and follow it.");
        }

        if (GUI.Button(new Rect(left, y + 588f, columnWidth, 32f), "RESET CAMERA", CommanderUiTheme.Button))
        {
            ResetCameraBindings();
            bindingCapture = null;
        }
        if (GUI.Button(new Rect(right, y + 588f, columnWidth, 32f), "RESET ACTIONS", CommanderUiTheme.Button))
        {
            ResetActionBindings();
            bindingCapture = null;
        }
    }

    private void DrawBinding(Rect rect, string label, string binding)
    {
        float labelWidth = Mathf.Min(94f, rect.width * 0.36f);
        GUI.Label(new Rect(rect.x, rect.y, labelWidth, rect.height), label, CommanderUiTheme.Label);
        string buttonText = bindingCapture == binding ? "PRESS KEY..." : GetBinding(binding).ToString();
        if (GUI.Button(
            new Rect(rect.x + labelWidth, rect.y, rect.width - labelWidth - 34f, rect.height),
            buttonText,
            bindingCapture == binding ? CommanderUiTheme.SelectedButton : CommanderUiTheme.Button))
        {
            bindingCapture = binding;
        }
        if (GUI.Button(new Rect(rect.xMax - 28f, rect.y, 28f, rect.height), "X", CommanderUiTheme.Button))
        {
            SetBinding(binding, new KeyboardShortcut(KeyCode.None));
            bindingCapture = null;
        }
    }

    private void CaptureBindingInput()
    {
        if (bindingCapture == null)
        {
            return;
        }

        Event current = Event.current;
        KeyCode key;
        if (current.type == EventType.KeyDown)
        {
            if (current.keyCode == KeyCode.Escape)
            {
                bindingCapture = null;
                current.Use();
                return;
            }
            key = current.keyCode;
            if (key == KeyCode.None)
            {
                return;
            }
        }
        else if (current.type == EventType.MouseDown)
        {
            key = (KeyCode)((int)KeyCode.Mouse0 + current.button);
        }
        else
        {
            return;
        }

        List<KeyCode> modifiers = new();
        if (current.shift && key != KeyCode.LeftShift && key != KeyCode.RightShift) modifiers.Add(KeyCode.LeftShift);
        if (current.control && key != KeyCode.LeftControl && key != KeyCode.RightControl) modifiers.Add(KeyCode.LeftControl);
        if (current.alt && key != KeyCode.LeftAlt && key != KeyCode.RightAlt) modifiers.Add(KeyCode.LeftAlt);
        SetBinding(bindingCapture, new KeyboardShortcut(key, modifiers.ToArray()));
        bindingCapture = null;
        current.Use();
    }

    private static KeyboardShortcut GetBinding(string binding)
    {
        return binding switch
        {
            "forward" => CommanderSettings.CameraForward,
            "backward" => CommanderSettings.CameraBackward,
            "left" => CommanderSettings.CameraLeft,
            "right" => CommanderSettings.CameraRight,
            "up" => CommanderSettings.CameraUp,
            "down" => CommanderSettings.CameraDown,
            "look" => CommanderSettings.CameraFreeLook,
            "look_alt" => CommanderSettings.CameraFreeLookAlt,
            "boost" => CommanderSettings.CameraBoost,
            "primary" => CommanderSettings.PrimaryAction,
            "secondary" => CommanderSettings.SecondaryAction,
            "add_selection" => CommanderSettings.AddToSelection,
            "repeat_deploy" => CommanderSettings.RepeatDeployment,
            "delete_modifier" => CommanderSettings.DeleteUnitModifier,
            "center_follow" => CommanderSettings.CameraCenterFollow,
            "toggle_ui" => CommanderSettings.ToggleUi,
            "queue_waypoint" => CommanderSettings.QueueWaypoint,
            "assign_group" => CommanderSettings.AssignGroupModifier,
            "stop_order" => CommanderSettings.StopOrder,
            "same_type" => CommanderSettings.SelectSameType,
            "cycle_idle" => CommanderSettings.CycleIdleUnit,
            "map_box_select" => CommanderSettings.MapBoxSelect,
            "toggle_player_commander" => CommanderSettings.TogglePlayerCommander,
            "rotate_left" => CommanderSettings.PlacementRotateLeft,
            "rotate_right" => CommanderSettings.PlacementRotateRight,
            "conform_ground" => CommanderSettings.PlacementConformGround,
            _ => new KeyboardShortcut(KeyCode.None)
        };
    }

    private static void SetBinding(string binding, KeyboardShortcut shortcut)
    {
        switch (binding)
        {
            case "forward": CommanderSettings.CameraForward = shortcut; break;
            case "backward": CommanderSettings.CameraBackward = shortcut; break;
            case "left": CommanderSettings.CameraLeft = shortcut; break;
            case "right": CommanderSettings.CameraRight = shortcut; break;
            case "up": CommanderSettings.CameraUp = shortcut; break;
            case "down": CommanderSettings.CameraDown = shortcut; break;
            case "look": CommanderSettings.CameraFreeLook = shortcut; break;
            case "look_alt": CommanderSettings.CameraFreeLookAlt = shortcut; break;
            case "boost": CommanderSettings.CameraBoost = shortcut; break;
            case "primary": CommanderSettings.PrimaryAction = shortcut; break;
            case "secondary": CommanderSettings.SecondaryAction = shortcut; break;
            case "add_selection": CommanderSettings.AddToSelection = shortcut; break;
            case "repeat_deploy": CommanderSettings.RepeatDeployment = shortcut; break;
            case "delete_modifier": CommanderSettings.DeleteUnitModifier = shortcut; break;
            case "center_follow": CommanderSettings.CameraCenterFollow = shortcut; break;
            case "toggle_ui": CommanderSettings.ToggleUi = shortcut; break;
            case "queue_waypoint": CommanderSettings.QueueWaypoint = shortcut; break;
            case "assign_group": CommanderSettings.AssignGroupModifier = shortcut; break;
            case "stop_order": CommanderSettings.StopOrder = shortcut; break;
            case "same_type": CommanderSettings.SelectSameType = shortcut; break;
            case "cycle_idle": CommanderSettings.CycleIdleUnit = shortcut; break;
            case "map_box_select": CommanderSettings.MapBoxSelect = shortcut; break;
            case "toggle_player_commander": CommanderSettings.TogglePlayerCommander = shortcut; break;
            case "rotate_left": CommanderSettings.PlacementRotateLeft = shortcut; break;
            case "rotate_right": CommanderSettings.PlacementRotateRight = shortcut; break;
            case "conform_ground": CommanderSettings.PlacementConformGround = shortcut; break;
        }
    }

    private static void ResetCameraBindings()
    {
        CommanderSettings.CameraForward = new KeyboardShortcut(KeyCode.W);
        CommanderSettings.CameraBackward = new KeyboardShortcut(KeyCode.S);
        CommanderSettings.CameraLeft = new KeyboardShortcut(KeyCode.A);
        CommanderSettings.CameraRight = new KeyboardShortcut(KeyCode.D);
        CommanderSettings.CameraUp = new KeyboardShortcut(KeyCode.Q);
        CommanderSettings.CameraDown = new KeyboardShortcut(KeyCode.E);
        CommanderSettings.CameraFreeLook = new KeyboardShortcut(KeyCode.Mouse2);
        CommanderSettings.CameraFreeLookAlt = new KeyboardShortcut(KeyCode.RightAlt);
        CommanderSettings.CameraBoost = new KeyboardShortcut(KeyCode.LeftShift);
        CommanderSettings.CameraCenterFollow = new KeyboardShortcut(KeyCode.Space);
    }

    private static void ResetActionBindings()
    {
        CommanderSettings.PrimaryAction = new KeyboardShortcut(KeyCode.Mouse0);
        CommanderSettings.SecondaryAction = new KeyboardShortcut(KeyCode.Mouse1);
        CommanderSettings.AddToSelection = new KeyboardShortcut(KeyCode.LeftShift);
        CommanderSettings.RepeatDeployment = new KeyboardShortcut(KeyCode.LeftShift);
        CommanderSettings.DeleteUnitModifier = new KeyboardShortcut(KeyCode.LeftAlt);
        CommanderSettings.ToggleUi = new KeyboardShortcut(KeyCode.H);
        CommanderSettings.QueueWaypoint = new KeyboardShortcut(KeyCode.LeftShift);
        CommanderSettings.AssignGroupModifier = new KeyboardShortcut(KeyCode.LeftControl);
        CommanderSettings.StopOrder = new KeyboardShortcut(KeyCode.X);
        CommanderSettings.SelectSameType = new KeyboardShortcut(KeyCode.LeftControl);
        CommanderSettings.CycleIdleUnit = new KeyboardShortcut(KeyCode.Period);
        CommanderSettings.MapBoxSelect = new KeyboardShortcut(KeyCode.LeftControl);
        // Unbound by default: handing your own faction to the AI is not something a stray key
        // press should do.
        CommanderSettings.TogglePlayerCommander = new KeyboardShortcut(KeyCode.None);
        // The RTS convention, and the same keys the camera rises and descends on: the build ghost
        // only borrows them while a placement is armed.
        CommanderSettings.PlacementRotateLeft = new KeyboardShortcut(KeyCode.Q);
        CommanderSettings.PlacementRotateRight = new KeyboardShortcut(KeyCode.E);
        CommanderSettings.PlacementConformGround = new KeyboardShortcut(KeyCode.R);
    }

    private void ResetUiLayout()
    {
        positionsInitialized = false;
        supplyHeliUi.ResetPosition();
        airCommandUi.ResetPosition();
        navalPurchaseUi.ResetPosition();
        samSiteAnalyzerUi.ResetPosition();
        economyUi.ResetPosition();
        depotUi.ResetPosition();
        unitListUi.ResetPosition();
        CommanderAlertUi.Instance?.ResetPosition();
        CommanderTacticalMapService.Instance?.ResetLayoutPosition();
    }
}
