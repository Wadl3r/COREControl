using HarmonyLib;
using NuclearOption.Networking;
using UnityEngine;

namespace GroundControlRts;

/// <summary>
/// While RTS mode is active the mod owns map input: the left button pans the map and clicks
/// icons, the box-select modifier turns a left drag into a selection box, the middle button
/// also pans, and the Basegame zoom and keyboard-pan bindings keep working.
/// </summary>
[HarmonyPatch(typeof(DynamicMap), "MapControls")]
internal static class CommanderTacticalMapControlsPatch
{
    private const float MapIconPickRadiusPixels = 32f;
    private const float ClickSlopPixels = 6f;

    private static bool leftButtonHeld;
    private static Vector2 leftButtonDownPosition;
    private static bool leftButtonMoved;
    private static Vector2 lastMousePosition;
    private static bool dragTracking;

    private static readonly AccessTools.FieldRef<DynamicMap, bool> FollowingCamera =
        AccessTools.FieldRefAccess<DynamicMap, bool>("followingCamera");
    private static readonly AccessTools.FieldRef<DynamicMap, Vector2> PositionOffset =
        AccessTools.FieldRefAccess<DynamicMap, Vector2>("positionOffset");
    private static readonly AccessTools.FieldRef<DynamicMap, Vector2> StationaryOffset =
        AccessTools.FieldRefAccess<DynamicMap, Vector2>("stationaryOffset");
    private static readonly AccessTools.FieldRef<DynamicMap, float> MapMoveMaxJumpSpeed =
        AccessTools.FieldRefAccess<DynamicMap, float>("mapMoveMaxJumpSpeed");

    private static bool Prefix(DynamicMap __instance)
    {
        try
        {
            if (CommanderPlugin.Instance?.IsCommanderModeActive != true)
            {
                return true;
            }

            if (CommanderOverlayUi.Instance?.ContainsScreenPoint(Input.mousePosition) != true
                && __instance.IsCursorInMapRectangle())
            {
                CommanderMapControls(__instance);
            }
            else
            {
                // Dragging off the map and back must not arrive as one huge jump.
                dragTracking = false;
            }

            UpdateCameraTracking(__instance);
            return false;
        }
        catch (System.Exception exception)
        {
            CommanderFaults.Report("DynamicMap.MapControls prefix", exception);
            return true;
        }
    }

    private static void CommanderMapControls(DynamicMap map)
    {
        float zoomAxis = CommanderGameInput.GetAxis("Zoom View") * 0.05f;
        if (zoomAxis != 0f)
        {
            map.SetZoomLevel(Mathf.Clamp(map.mapScaleCenter.transform.localScale.x * (zoomAxis + 1f), 1f, 40f));
        }

        // Screen pixels per map unit, which is what the Basegame uses for its own cursor maths.
        // The old code divided only by the zoom level and so ignored the fact that the compact
        // Tactical Map is a scaled-down copy of the fullscreen one - which is exactly why
        // dragging the modded map crawled while the fullscreen map felt normal.
        float pixelsPerMapUnit = Mathf.Max(map.mapImage.transform.lossyScale.x, 0.0001f);
        float keyboardHorizontal = CommanderGameInput.GetAxis("Move Map Horizontal");
        float keyboardVertical = CommanderGameInput.GetAxis("Move Map Vertical");
        if (keyboardHorizontal != 0f || keyboardVertical != 0f)
        {
            float speed = 600f * Time.unscaledDeltaTime / pixelsPerMapUnit;
            PositionOffset(map) += new Vector2(keyboardHorizontal * speed, keyboardVertical * speed);
        }

        // A plain left drag pans, exactly like the Basegame map. The middle button keeps
        // panning too, and a left drag with the box-select modifier belongs to the box.
        bool boxDragging = CommanderBoxSelectService.Instance?.DraggingOnMap == true;
        Vector2 mousePosition = Input.mousePosition;
        if (Input.GetMouseButton(2) || (Input.GetMouseButton(0) && !boxDragging))
        {
            // Real cursor pixels, not Unity's smoothed mouse axis, so the map sticks to the
            // cursor at a speed of 1 no matter the zoom, the window size or the framerate.
            if (dragTracking)
            {
                PositionOffset(map) -= (mousePosition - lastMousePosition)
                    * (CommanderSettings.MapDragSpeed / pixelsPerMapUnit);
            }

            lastMousePosition = mousePosition;
            dragTracking = true;
        }
        else
        {
            dragTracking = false;
        }

        FollowingCamera(map) = PositionOffset(map) == Vector2.zero;

        if (CommanderGameInput.JumpMapDown && map.TryGetCursorCoordinates(out GlobalPosition jumpTarget))
        {
            CommanderTacticalMapService.Instance?.JumpCameraToPosition(jumpTarget);
        }

        TrackIconClick(map);
    }

    /// <summary>
    /// Icon selection resolves on release, and only when the cursor barely moved: the same
    /// button now pans the map, so clicking on press would select an icon every time the
    /// player grabbed the map to drag it.
    /// </summary>
    private static void TrackIconClick(DynamicMap map)
    {
        if (Input.GetMouseButtonDown(0))
        {
            leftButtonHeld = true;
            leftButtonMoved = false;
            leftButtonDownPosition = Input.mousePosition;
        }

        if (!leftButtonHeld)
        {
            return;
        }

        if (Vector2.Distance(leftButtonDownPosition, Input.mousePosition) > ClickSlopPixels)
        {
            leftButtonMoved = true;
        }

        if (!Input.GetMouseButtonUp(0))
        {
            return;
        }

        leftButtonHeld = false;
        if (leftButtonMoved
            || CommanderBoxSelectService.Instance?.Dragging == true
            || AnyPlacementArmed())
        {
            return;
        }

        // A strategic point takes the click first — the game's own icon list knows nothing about
        // villages, hilltops or resource sites, so without this the click always fell through to
        // whatever unit icon happened to be nearest.
        if (CommanderStrategicPointService.Instance?.TryFocusPointAt(Input.mousePosition) != true)
        {
            ClickNearestIcon(map);
        }
    }

    /// <summary>
    /// True while any Commander placement owns the next click: an air mission area, a building, a
    /// supply helicopter target, an emplacement destination, a depot or naval rally point. While one
    /// is armed a map click is a placement, full stop — it must not also pick the unit icon under the
    /// cursor, which selected that unit and flew the camera to it.
    /// </summary>
    /// <summary>
    /// Frames after a placement completes during which map clicks are still treated as spoken for.
    /// The placement resolves on mouse-up in whichever Update runs first; the mod's icon pick, the
    /// Unity pointer click on the icon and the camera-jump tracker all see the same mouse-up later
    /// in that frame or the next. Two frames is enough for all of them and short enough that the
    /// player's next deliberate click is never eaten.
    /// </summary>
    private const int SwallowClickFrames = 2;
    private static int swallowClicksUntilFrame = -1;

    /// <summary>Called when a placement finishes so the click that finished it goes no further.</summary>
    internal static void SwallowMapClicks()
    {
        swallowClicksUntilFrame = Time.frameCount + SwallowClickFrames;
    }

    internal static bool AnyPlacementArmed()
    {
        return Time.frameCount <= swallowClicksUntilFrame
            || CommanderSpawnService.Instance?.AwaitingRallyPointSelection == true
            || CommanderNavalPurchaseService.Instance?.AwaitingRallySelection == true
            || CommanderAirCommandService.Instance?.AwaitingAreaSelection == true
            || CommanderEconomyService.Instance?.AwaitingPlacement == true
            || CommanderSupplyHeliService.Instance?.AwaitingTargetSelection == true
            || CommanderMobileEmplacementService.Instance?.AwaitingDestination == true;
    }

    /// <summary>Basegame icon picking, reimplemented because the original lives inside MapControls.</summary>
    private static void ClickNearestIcon(DynamicMap map)
    {
        Vector3 mousePosition = Input.mousePosition;
        float bestDistance = MapIconPickRadiusPixels * MapIconPickRadiusPixels;
        MapIcon? best = null;
        System.Collections.Generic.List<MapIcon> icons = map.mapIcons;
        for (int i = 0; i < icons.Count; i++)
        {
            MapIcon icon = icons[i];
            if (icon == null
                || !icon.gameObject.activeInHierarchy
                || icon.iconImage == null
                || !icon.iconImage.raycastTarget)
            {
                continue;
            }

            if (icon is UnitMapIcon unitIcon
                && (unitIcon.unit == null
                    || SceneSingleton<TargetListSelector>.i?.CheckExclusions(unitIcon.unit) == true))
            {
                continue;
            }

            float distance = ((Vector2)icon.transform.position - (Vector2)mousePosition).sqrMagnitude;
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = icon;
            }
        }

        best?.ClickIcon(MapIcon.ClickSource.Controller);
    }

    private static void UpdateCameraTracking(DynamicMap map)
    {
        CameraStateManager? camera = SceneSingleton<CameraStateManager>.i;
        if (camera == null)
        {
            return;
        }

        Vector3 cameraPosition = camera.transform.position.ToGlobalPosition().AsVector3() * map.mapDisplayFactor;
        ref Vector2 positionOffset = ref PositionOffset(map);
        ref Vector2 stationaryOffset = ref StationaryOffset(map);
        if (FollowingCamera(map))
        {
            Vector2 target = new(cameraPosition.x, cameraPosition.z);
            Aircraft? aircraft = SceneSingleton<CombatHUD>.i?.aircraft;
            if (aircraft != null && !aircraft.disabled)
            {
                Vector3 forward = aircraft.transform.forward;
                stationaryOffset = target + 5000f * map.mapDisplayFactor * new Vector2(forward.x, forward.z);
            }
            else
            {
                stationaryOffset = Vector2.MoveTowards(stationaryOffset, target, MapMoveMaxJumpSpeed(map));
            }
        }

        map.mapImage.transform.localEulerAngles = Vector3.zero;
        map.mapScaleCenter.transform.localEulerAngles = Vector3.zero;
        Vector2 mapPosition = -stationaryOffset - positionOffset;
        ((RectTransform)map.mapBackground.transform).rect.ClampPos(ref mapPosition, 2f);
        positionOffset = -stationaryOffset - mapPosition;
        map.mapImage.transform.localPosition = mapPosition * map.mapImage.transform.localScale.x;
        map.viewIndicator.transform.localPosition = new Vector3(cameraPosition.x, cameraPosition.z, 0f);
        map.viewIndicator.transform.eulerAngles = new Vector3(
            0f,
            0f,
            map.mapImage.transform.eulerAngles.z - camera.transform.eulerAngles.y);
    }
}

[HarmonyPatch(typeof(DynamicMap), "JumpCameraTo")]
internal static class CommanderDisableBaseMapJumpPatch
{
    private static bool Prefix()
    {
        try
        {
            return CommanderPlugin.Instance?.IsCommanderModeActive != true
                || CommanderTacticalMapService.AllowCommanderMapJump;
        }
        catch (System.Exception exception)
        {
            CommanderFaults.Report("DynamicMap.JumpCameraTo prefix", exception);
            return true;
        }
    }
}

[HarmonyPatch(typeof(ExtraUiInput), "Update")]
internal static class CommanderKeepTacticalMapOpenPatch
{
    private static bool Prefix()
    {
        try
        {
            if (CommanderPlugin.Instance?.IsCommanderModeActive != true)
            {
                return true;
            }

            if (CommanderTacticalMapService.Instance?.SuppressExtraUiThisFrame == true)
            {
                return false;
            }

            if (!CommanderGameInput.MapDown)
            {
                return true;
            }

            if (CommanderAirCommandUi.Instance?.HandleMapKey() == true)
            {
                return false;
            }

            return CommanderTacticalMapService.Instance?.HandleMapKey() != true;
        }
        catch (System.Exception exception)
        {
            CommanderFaults.Report("ExtraUiInput.Update prefix", exception);
            return true;
        }
    }
}

[HarmonyPatch(typeof(UnitMapIcon), nameof(UnitMapIcon.UpdateIcon))]
internal static class CommanderTacticalMapIconScalePatch
{
    private static void Postfix(UnitMapIcon __instance)
    {
        try
        {
            if (CommanderPlugin.Instance?.IsCommanderModeActive == true
                && CommanderTacticalMapService.Instance?.IsOpen == true
                && __instance.iconImage != null)
            {
                __instance.iconImage.transform.localScale *= 1.4f;
            }
        }
        catch (System.Exception exception)
        {
            CommanderFaults.Report("UnitMapIcon.UpdateIcon postfix", exception);
        }
    }
}

/// <summary>
/// The icon-pick guard above only covers clicks the mod itself resolves. Every map icon is also a
/// Unity UI click target (<c>MapIcon.OnPointerClick</c> calls <c>ClickIcon</c> straight from the
/// event system), so a click landing exactly on a unit's icon reached the Basegame selection
/// without ever passing through <c>MapControls</c>. Same rule, applied at the icon.
/// </summary>
[HarmonyPatch(typeof(UnitMapIcon), nameof(UnitMapIcon.ClickIcon))]
internal static class CommanderUnitMapClickPatch
{
    private static bool Prefix()
    {
        try
        {
            return CommanderPlugin.Instance?.IsCommanderModeActive != true
                || !CommanderTacticalMapControlsPatch.AnyPlacementArmed();
        }
        catch (System.Exception exception)
        {
            CommanderFaults.Report("UnitMapIcon.ClickIcon prefix", exception);
            return true;
        }
    }
}

[HarmonyPatch(typeof(AirbaseMapIcon), nameof(AirbaseMapIcon.ClickIcon))]
internal static class CommanderAirbaseMapClickPatch
{
    private static bool Prefix(AirbaseMapIcon __instance)
    {
        try
        {
            if (CommanderPlugin.Instance?.IsCommanderModeActive != true)
            {
                return true;
            }

            // An armed placement owns the click; see CommanderUnitMapClickPatch. The one exception is
            // Air Command picking its departure base, which is a placement step in its own right.
            if (CommanderTacticalMapControlsPatch.AnyPlacementArmed()
                && CommanderAirCommandService.Instance?.IsUiVisible != true)
            {
                return false;
            }

            CommanderAirCommandService? airCommand = CommanderAirCommandService.Instance;
            if (airCommand?.IsUiVisible == true)
            {
                airCommand.TrySelectAirbaseFromMap(__instance.airbase);
                return false;
            }

            // The compact Tactical Map is for command interaction and should never open
            // the Basegame aircraft-selection panel.
            return CommanderTacticalMapService.Instance?.IsOpen != true;
        }
        catch (System.Exception exception)
        {
            CommanderFaults.Report("AirbaseMapIcon.ClickIcon prefix", exception);
            return true;
        }
    }
}

[HarmonyPatch(typeof(AirbaseMapIcon), nameof(AirbaseMapIcon.UpdateIcon))]
internal static class CommanderAirCommandAirbaseIconPatch
{
    private static void Postfix(AirbaseMapIcon __instance)
    {
        try
        {
            CommanderAirCommandService? airCommand = CommanderAirCommandService.Instance;
            if (CommanderPlugin.Instance?.IsCommanderModeActive != true
                || airCommand?.IsUiVisible != true)
            {
                return;
            }

            bool selectable = airCommand.IsSelectableAirbase(__instance.airbase);
            __instance.gameObject.SetActive(DynamicMap.mapMaximized && selectable);
            if (selectable && __instance.iconImage != null && airCommand.IsSelectedAirbase(__instance.airbase))
            {
                __instance.iconImage.color = GameAssets.i.HUDFriendlySelected;
            }
        }
        catch (System.Exception exception)
        {
            CommanderFaults.Report("AirbaseMapIcon.UpdateIcon postfix", exception);
        }
    }
}
