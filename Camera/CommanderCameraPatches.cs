using HarmonyLib;
using NuclearOption.MissionEditorScripts;
using System.Reflection;
using UnityEngine;

namespace GroundControlRts;

[HarmonyPatch(typeof(CameraStateManager), nameof(CameraStateManager.SetFollowingUnit))]
internal static class CommanderCameraFollowingPatch
{
    private static bool Prefix(Unit unit)
    {
        try
        {
            if (CommanderPlugin.Instance?.IsCommanderModeActive != true || !DynamicMap.mapMaximized)
            {
                return true;
            }

            if (CommanderTacticalMapService.Instance?.SuppressMapFollow == true)
            {
                return false;
            }

            return !CommanderGameAccess.ShouldAllowCommanderSelection(unit, CommanderGameAccess.GetLocalHq());
        }
        catch (System.Exception exception)
        {
            CommanderFaults.Report("CameraStateManager.SetFollowingUnit prefix", exception);
            return true;
        }
    }
}

[HarmonyPatch(typeof(SonicBoomManager), nameof(SonicBoomManager.ManageSonicBooms))]
internal static class CommanderFollowSonicBoomPatch
{
    private static void Prefix(ref Vector3 __state)
    {
        try
        {
            CameraStateManager? camera = SceneSingleton<CameraStateManager>.i;
            __state = camera != null ? camera.cameraVelocity : Vector3.zero;
            Aircraft? followed = CommanderCameraFollowService.Instance?.FollowedAircraft;
            if (camera != null && followed?.rb != null)
            {
                // SonicBoomManager only needs the listener velocity during this call.
                // Restoring it immediately avoids feeding aircraft speed into FreeCam.
                camera.cameraVelocity = followed.rb.velocity;
            }
        }
        catch (System.Exception exception)
        {
            CommanderFaults.Report("SonicBoomManager.ManageSonicBooms prefix", exception);
        }
    }

    private static void Postfix(Vector3 __state)
    {
        try
        {
            CameraStateManager? camera = SceneSingleton<CameraStateManager>.i;
            if (camera != null)
            {
                camera.cameraVelocity = __state;
            }
        }
        catch (System.Exception exception)
        {
            CommanderFaults.Report("SonicBoomManager.ManageSonicBooms postfix", exception);
        }
    }
}

[HarmonyPatch(typeof(CameraFreeState), nameof(CameraFreeState.UpdateState))]
internal static class CommanderFreeCameraInputPatch
{
    private const float PovMovementSpeed = 2f;
    private const float PovBoostedMovementSpeed = 10f;
    private const float BoostMultiplier = 3f;
    private const float MouseLookScale = 2f;
    private const float GroundProbeHeight = 5000f;
    private static readonly FieldInfo? PanViewField = AccessTools.Field(typeof(CameraFreeState), "panView");
    private static readonly FieldInfo? TiltViewField = AccessTools.Field(typeof(CameraFreeState), "tiltView");
    private static readonly FieldInfo? FovAdjustmentField = AccessTools.Field(typeof(CameraFreeState), "FOVAdjustment");
    private static Vector3 customVelocity;
    private static Vector3 orbitPivot;
    private static bool orbiting;
    private static float lastGroundHeight;
    private static bool groundHeightValid;

    /// <summary>
    /// True when the player drove the camera this frame. The follow service uses it to abandon
    /// an auto-frame glide the moment the player takes the camera back.
    /// </summary>
    internal static bool PlayerMovedCameraThisFrame { get; private set; }

    private struct CameraInputState
    {
        internal bool Active;
        internal bool AllowInputs;
        internal float Pan;
        internal float Tilt;
        internal float FieldOfView;
        internal object? FovAdjustment;
        internal bool SnapRotation;
        internal Quaternion Rotation;
        internal Vector3 ReportedVelocity;
    }

    private static void Prefix(CameraFreeState __instance, CameraStateManager cam, out CameraInputState __state)
    {
        __state = default;
        try
        {
            ApplyCommanderInput(__instance, cam, ref __state);
        }
        catch (System.Exception exception)
        {
            CommanderFaults.Report("CameraFreeState.UpdateState prefix", exception);
            // Put the game's input switch back and skip the postfix's restore: the state is only
            // partly captured, and restoring from it would snap the camera to a zero rotation.
            if (__state.Active)
            {
                cam.allowInputs = __state.AllowInputs;
            }

            __state.Active = false;
        }
    }

    private static void ApplyCommanderInput(CameraFreeState __instance, CameraStateManager cam, ref CameraInputState __state)
    {
        PlayerMovedCameraThisFrame = false;
        if (!CommanderCameraController.CustomInputActive)
        {
            return;
        }

        __state.Active = true;
        __state.AllowInputs = cam.allowInputs;
        __state.Rotation = cam.transform.rotation;
        __state.Pan = GetAngle(PanViewField, __instance, cam.transform.eulerAngles.y);
        __state.Tilt = GetAngle(TiltViewField, __instance, cam.transform.eulerAngles.x);
        // The RTS camera keeps a fixed lens. Left alone the Basegame maps the wheel to an FOV
        // axis, which warps the view and silently rescales look sensitivity along with it.
        __state.FieldOfView = cam.mainCamera.fieldOfView;
        __state.FovAdjustment = FovAdjustmentField?.GetValue(__instance);

        // Keep the Basegame FreeCam update for terrain collision and state handling, but prevent
        // Rewired's aircraft bindings from moving it.
        cam.allowInputs = false;
        cam.cameraVelocity = Vector3.zero;
        if (CommanderTacticalMapService.Instance?.IsFullscreenOpen == true
            || InputFieldChecker.InsideInputField)
        {
            customVelocity = Vector3.zero;
            orbiting = false;
            return;
        }

        if (CommanderCameraFollowService.IsPovActive)
        {
            PovMotion(cam, ref __state);
            return;
        }

        // Speed and zoom size themselves off last frame's ground height, which is invisibly
        // stale; the clamp re-probes after the move, because at boost speed the camera can cross
        // a whole hillside in one frame and a stale floor would let it through the terrain.
        if (!groundHeightValid)
        {
            lastGroundHeight = GroundHeightBelow(cam.transform.position);
            groundHeightValid = true;
        }

        float aboveGround = cam.transform.position.y - lastGroundHeight;
        float speedScale = CommanderSettings.CameraHeightScaledSpeed
            ? CommanderCameraTuning.SpeedScale(aboveGround)
            : 1f;

        GroundMotion(cam, speedScale);
        WheelZoom(cam, aboveGround);
        LookInput(cam, ref __state);
        lastGroundHeight = GroundHeightBelow(cam.transform.position);
        ClampToGround(cam, lastGroundHeight);

        __state.ReportedVelocity = customVelocity;
    }

    /// <summary>
    /// Pan across the ground, not along the view axis. A pitched-down camera moving on its own
    /// forward vector dives into the terrain and is then shoved back up by the ground clamp,
    /// which is most of what the old camera felt like. Up/down stay the only way to change height.
    /// </summary>
    private static void GroundMotion(CameraStateManager cam, float speedScale)
    {
        float longitudinal = Axis(CommanderSettings.CameraForward, CommanderSettings.CameraBackward);
        float lateral = Axis(CommanderSettings.CameraRight, CommanderSettings.CameraLeft);
        float vertical = VerticalAxis();
        ApplyEdgeScroll(ref longitudinal, ref lateral);

        Vector3 flatForward = Vector3.ProjectOnPlane(cam.transform.forward, Vector3.up);
        if (flatForward.sqrMagnitude < 0.0001f)
        {
            // Looking straight down: the top of the screen is the direction that reads as forward.
            flatForward = Vector3.ProjectOnPlane(cam.transform.up, Vector3.up);
        }
        flatForward = flatForward.sqrMagnitude < 0.0001f ? Vector3.forward : flatForward.normalized;
        Vector3 flatRight = Vector3.Cross(Vector3.up, flatForward);

        Vector3 direction = flatForward * longitudinal + flatRight * lateral + Vector3.up * vertical;
        bool hasMovementInput = direction.sqrMagnitude > 0.001f;
        if (direction.sqrMagnitude > 1f)
        {
            direction.Normalize();
        }

        bool boost = CommanderShortcutInput.IsPressed(CommanderSettings.CameraBoost);
        float speed = CommanderSettings.CameraPanSpeed * speedScale * (boost ? BoostMultiplier : 1f);
        Vector3 targetVelocity = direction * cam.desiredTransSpeed * speed;
        float blend = CommanderCameraTuning.SmoothBlend(CommanderSettings.CameraSmoothing, Time.unscaledDeltaTime);
        customVelocity = Vector3.Lerp(customVelocity, targetVelocity, blend);
        if (!hasMovementInput && customVelocity.sqrMagnitude < 0.01f)
        {
            customVelocity = Vector3.zero;
        }

        cam.transform.position += customVelocity * Time.unscaledDeltaTime;
        PlayerMovedCameraThisFrame |= hasMovementInput;
    }

    /// <summary>Classic RTS screen-edge push, off by default because the mod's windows crowd the edges.</summary>
    private static void ApplyEdgeScroll(ref float longitudinal, ref float lateral)
    {
        if (!CommanderSettings.CameraEdgeScroll || !Application.isFocused)
        {
            return;
        }

        Vector3 mouse = Input.mousePosition;
        if (mouse.x < 0f || mouse.y < 0f || mouse.x > Screen.width || mouse.y > Screen.height)
        {
            return;
        }

        if (CommanderOverlayUi.Instance?.ContainsScreenPoint(mouse) == true
            || CommanderTacticalMapService.Instance?.ContainsScreenPoint(mouse) == true)
        {
            return;
        }

        float margin = CommanderCameraTuning.EdgeScrollMargin;
        if (mouse.x <= margin)
        {
            lateral -= 1f;
        }
        else if (mouse.x >= Screen.width - margin)
        {
            lateral += 1f;
        }

        if (mouse.y <= margin)
        {
            longitudinal -= 1f;
        }
        else if (mouse.y >= Screen.height - margin)
        {
            longitudinal += 1f;
        }
    }

    /// <summary>
    /// The wheel dollies toward whatever the cursor is over, so zooming in also recentres on the
    /// thing you were pointing at. Each notch covers a quarter of the height above the ground,
    /// which keeps the step readable from the deck to the stratosphere.
    /// </summary>
    private static void WheelZoom(CameraStateManager cam, float aboveGround)
    {
        float wheel = Input.mouseScrollDelta.y;
        if (Mathf.Approximately(wheel, 0f))
        {
            return;
        }

        Vector3 mouse = Input.mousePosition;
        if (CommanderOverlayUi.Instance?.ContainsScreenPoint(mouse) == true
            || CommanderTacticalMapService.Instance?.ContainsScreenPoint(mouse) == true)
        {
            return;
        }

        Vector3 position = cam.transform.position;
        Vector3 direction = cam.transform.forward;
        float available = float.MaxValue;
        if (TryGetCursorGroundPoint(cam, out Vector3 groundPoint))
        {
            Vector3 toPoint = groundPoint - position;
            if (toPoint.sqrMagnitude > 1f)
            {
                available = toPoint.magnitude;
                direction = toPoint / available;
            }
        }

        float step = wheel * Mathf.Max(aboveGround, 20f) * CommanderCameraTuning.ZoomStepFraction
            * Mathf.Max(CommanderSettings.CameraZoomSpeed, 0.05f);
        // Never let a zoom-in punch through the point it is aiming at.
        step = Mathf.Min(step, Mathf.Max(available - CommanderCameraTuning.GroundClearance, 0f));
        Vector3 moved = position + direction * step;
        moved.y = Mathf.Min(moved.y, CommanderCameraTuning.MaxAltitude);
        cam.transform.position = moved;
        PlayerMovedCameraThisFrame = true;
    }

    /// <summary>
    /// Hold the look key to swing the view. By default the camera orbits the point under the
    /// cursor, so whatever you were inspecting stays on screen; turning that off leaves the old
    /// turn-in-place behaviour, which throws the subject off the edge as soon as you move.
    /// </summary>
    private static void LookInput(CameraStateManager cam, ref CameraInputState state)
    {
        if (!CommanderSettings.IsFreeLookHeld)
        {
            orbiting = false;
            return;
        }

        float fovScale = Mathf.Min(cam.mainCamera.fieldOfView / 20f, 1f);
        float sensitivity = MouseLookScale * Mathf.Max(CommanderSettings.CameraLookSensitivity, 0.05f);
        float pitchDirection = PlayerSettings.viewInvertPitch ? 1f : -1f;
        float oldPan = state.Pan;
        float oldTilt = CommanderCameraTuning.ClampPitch(state.Tilt);
        state.Pan += fovScale * Input.GetAxisRaw("Mouse X") * sensitivity;
        state.Tilt = CommanderCameraTuning.ClampPitch(
            oldTilt + pitchDirection * fovScale * Input.GetAxisRaw("Mouse Y") * sensitivity);

        bool moved = !Mathf.Approximately(state.Pan, oldPan) || !Mathf.Approximately(state.Tilt, oldTilt);
        PlayerMovedCameraThisFrame |= moved;
        if (!CommanderSettings.CameraOrbitLook)
        {
            return;
        }

        if (!orbiting)
        {
            // The pivot is taken once, when the key goes down. Re-picking it every frame would
            // let it slide across the terrain as the view swings.
            orbiting = TryGetCursorGroundPoint(cam, out orbitPivot);
            if (!orbiting)
            {
                return;
            }
        }

        if (!moved)
        {
            return;
        }

        Quaternion delta = Quaternion.Euler(state.Tilt, state.Pan, 0f)
            * Quaternion.Inverse(Quaternion.Euler(oldTilt, oldPan, 0f));
        cam.transform.position = orbitPivot + delta * (cam.transform.position - orbitPivot);
        // The pivot only stays put if the rotation lands this frame, so orbiting skips smoothing.
        state.SnapRotation = true;
    }

    /// <summary>
    /// Keeps clearance over the terrain without the Basegame's hard snap, which jolts every time
    /// the camera crosses a ridge. It eases toward the comfortable clearance and only stops dead
    /// at the much lower hard limit, which also keeps the Basegame's own clamp from ever firing.
    /// </summary>
    private static void ClampToGround(CameraStateManager cam, float groundHeight)
    {
        Vector3 position = cam.transform.position;
        float soft = groundHeight + CommanderCameraTuning.GroundClearance;
        if (position.y < soft)
        {
            float blend = CommanderCameraTuning.SmoothBlend(0.08f, Time.unscaledDeltaTime);
            position.y = Mathf.Lerp(position.y, soft, blend);
        }

        position.y = Mathf.Clamp(
            position.y,
            groundHeight + CommanderCameraTuning.HardGroundClearance,
            CommanderCameraTuning.MaxAltitude);
        cam.transform.position = position;
    }

    /// <summary>POV rides inside a unit, so it keeps the old view-relative nudge and no RTS aids.</summary>
    private static void PovMotion(CameraStateManager cam, ref CameraInputState state)
    {
        float longitudinal = Axis(CommanderSettings.CameraForward, CommanderSettings.CameraBackward);
        float lateral = Axis(CommanderSettings.CameraRight, CommanderSettings.CameraLeft);
        float vertical = VerticalAxis();
        Vector3 direction = cam.transform.forward * longitudinal
            + cam.transform.right * lateral
            + Vector3.up * vertical;
        if (direction.sqrMagnitude > 1f)
        {
            direction.Normalize();
        }

        bool boost = CommanderShortcutInput.IsPressed(CommanderSettings.CameraBoost);
        customVelocity = direction * (boost ? PovBoostedMovementSpeed : PovMovementSpeed);
        cam.transform.position += customVelocity * Time.unscaledDeltaTime;
        state.ReportedVelocity = Vector3.zero;
    }

    private static void Postfix(CameraFreeState __instance, CameraStateManager cam, CameraInputState __state)
    {
        try
        {
            RestoreAfterUpdate(__instance, cam, __state);
        }
        catch (System.Exception exception)
        {
            CommanderFaults.Report("CameraFreeState.UpdateState postfix", exception);
            if (__state.Active)
            {
                cam.allowInputs = __state.AllowInputs;
            }
        }
    }

    private static void RestoreAfterUpdate(CameraFreeState __instance, CameraStateManager cam, CameraInputState __state)
    {
        if (!__state.Active)
        {
            return;
        }

        cam.allowInputs = __state.AllowInputs;
        cam.cameraVelocity = __state.ReportedVelocity;
        PanViewField?.SetValue(__instance, __state.Pan);
        TiltViewField?.SetValue(__instance, __state.Tilt);
        cam.mainCamera.fieldOfView = __state.FieldOfView;
        if (__state.FovAdjustment != null)
        {
            FovAdjustmentField?.SetValue(__instance, __state.FovAdjustment);
        }

        // Erase any Basegame Free Look input applied by the original method. POV rebuilds its
        // complete attached rotation in the camera LateUpdate postfix.
        if (!CommanderCameraFollowService.IsPovActive)
        {
            float blend = __state.SnapRotation
                ? 1f
                : CommanderCameraTuning.SmoothBlend(CommanderSettings.CameraSmoothing, Time.unscaledDeltaTime);
            cam.transform.rotation = Quaternion.Lerp(
                __state.Rotation,
                Quaternion.Euler(__state.Tilt, __state.Pan, 0f),
                blend);
        }
    }

    /// <summary>World point the cursor is over, used as the zoom target and the orbit pivot.</summary>
    private static bool TryGetCursorGroundPoint(CameraStateManager cam, out Vector3 point)
    {
        point = Vector3.zero;
        if (cam.mainCamera == null)
        {
            return false;
        }

        Ray ray = cam.mainCamera.ScreenPointToRay(Input.mousePosition);
        if (Physics.Raycast(ray, out RaycastHit hit, 60000f, PhysicsLayers.StaticsMask))
        {
            point = hit.point;
            return true;
        }

        // No terrain under the cursor (open water, or off the map edge): use the sea plane.
        if (ray.direction.y >= -0.01f)
        {
            return false;
        }

        point = ray.origin + ray.direction * ((Datum.LocalSeaY - ray.origin.y) / ray.direction.y);
        return true;
    }

    /// <summary>Terrain height under the camera, the same probe the Basegame uses for its own clamp.</summary>
    private static float GroundHeightBelow(Vector3 position)
    {
        if (Physics.Linecast(
            position + Vector3.up * GroundProbeHeight,
            position - Vector3.up * GroundProbeHeight,
            out RaycastHit hit,
            PhysicsLayers.StaticsMask))
        {
            return Mathf.Max(hit.point.y, Datum.LocalSeaY);
        }

        return Datum.LocalSeaY;
    }

    private static float Axis(BepInEx.Configuration.KeyboardShortcut positive, BepInEx.Configuration.KeyboardShortcut negative)
    {
        return (CommanderShortcutInput.IsPressed(positive) ? 1f : 0f)
            - (CommanderShortcutInput.IsPressed(negative) ? 1f : 0f);
    }

    /// <summary>
    /// Rise and descend, minus whichever of the two keys the armed build ghost has taken over. The
    /// rotate keys default to Q and E, which are also these, and a player holding a building on the
    /// cursor means "turn it" — so the camera stops climbing for as long as the placement is armed.
    /// </summary>
    private static float VerticalAxis()
    {
        return Axis(Unclaimed(CommanderSettings.CameraUp), Unclaimed(CommanderSettings.CameraDown));
    }

    /// <summary>The binding itself, or an unbound one while the build ghost is using that key.</summary>
    private static BepInEx.Configuration.KeyboardShortcut Unclaimed(
        BepInEx.Configuration.KeyboardShortcut shortcut)
    {
        return CommanderBuildPreview.IsPlacementRotationKey(shortcut)
            ? new BepInEx.Configuration.KeyboardShortcut(UnityEngine.KeyCode.None)
            : shortcut;
    }

    private static float GetAngle(FieldInfo? field, CameraFreeState state, float fallback)
    {
        return field?.GetValue(state) is float value ? value : fallback;
    }

    internal static void ResetCustomMotion()
    {
        customVelocity = Vector3.zero;
        orbiting = false;
        groundHeightValid = false;
        PlayerMovedCameraThisFrame = false;
    }
}
