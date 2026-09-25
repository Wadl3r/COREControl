using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Newtonsoft.Json;
using NuclearOption.SavedMission;
using UnityEngine;

namespace GroundControlRts;

/// <summary>
/// The developer's explicit strategic save: stop a long match, restart the mission to clear the
/// frame-rate decay, then carry on with the same points held, the same forward bases and a war
/// chest holding the money plus the cash value of the army that was standing.
/// </summary>
/// <remarks>
/// <para>
/// Why this is a second store beside <see cref="CommanderStateStore"/> rather than a widening of
/// it. The hot-reload snapshot is read once per run and only if its recorded level time is earlier
/// than and within ten minutes of the current one
/// (<see cref="CommanderStateStore.PassesSessionGuard"/>) — a window, not a decision. A save the
/// developer took an hour ago fails it outright, which is why replacing the gate was the first task
/// of this track and not the file format. The two stores share the file format, the JSON round
/// trip, the writer/reader wrappers and the registry fan-out; what differs is the gate, the file
/// name and the records.
/// </para>
/// <para>
/// <b>What arms a restore is <c>MissionManager.onMissionLoad</c>, and nothing else.</b> Two other
/// signals were tried and both were wrong, each proved so by the developer's own log of
/// 2026-09-17. <c>IsHotReloadLoad</c> is "this build was loaded from bytes", which is true for the
/// whole session whenever the mod lives in <c>BepInEx\scripts\</c> — so it refused every run and the
/// save was never even read. The level clock is no better here: that same log shows the hot-reload
/// snapshot being accepted on the restarted mission, which its guard only permits if
/// <c>timeSinceLevelLoad</c> did not go back to zero. The mission-load event has neither problem.
/// </para>
/// <para>
/// The restore runs in two phases, and the split is not tidiness. The MONEY goes back on the very
/// first tick, because the enemy commander's first review fires on its own first tick and opens its
/// treasury there; the WORLD is rebuilt
/// <see cref="StrategicRestoreSettleSeconds"/> later, because spawning bases and vehicles must not
/// race the game placing the mission's own units.
/// </para>
/// <para>
/// The hot-reload records are not merely unnecessary across a restart, they are dangerous. Air
/// missions and upgrade levels are keyed by <c>PersistentID</c>, a bare counter that
/// <c>UnitRegistry.Clear()</c> resets to zero, so a saved id would resolve to a DIFFERENT unit in
/// the new run and quietly set some unrelated building's upgrade level.
/// <see cref="CarriesNoUnitIdentifier"/> is the rule that keeps identifiers out of this file, and
/// <see cref="CarriesNoReplayedClock"/> is the rule that keeps clocks out of it.
/// </para>
/// <para>
/// Scope, decided by the developer on 2026-09-17: the strategic layer only. Units are refunded at
/// their value and never recreated; forward bases are rebuilt where they stood; a held control
/// point is given a garrison on load, paid for out of the war chest at the commander's own prices.
/// Nothing in flight, no positions, no damage, no ammunition, no loadouts.
/// </para>
/// </remarks>
internal sealed class CommanderStrategicSaveStore : ICommanderTickPersistent, ICommanderResetSession
{
    /// <summary>
    /// The layout of the strategic save file. Bumped when a field's MEANING changes, not when one
    /// is added — a save whose version does not match is refused whole rather than half-read, which
    /// is the difference between a load that fails loudly and one that corrupts a match quietly.
    /// 1: the first shipped layout.
    /// </summary>
    internal const int StrategicFormatVersion = 1;

    /// <summary>
    /// How long after a mission starts the restore waits before it runs, in seconds of level time:
    /// 15. The rebuild spawns forward bases and garrisons into the world, so it must not run while
    /// the game is still placing the mission's own units; strategic point discovery also needs to
    /// have got going, because the restore overwrites its answer rather than racing it. Fifteen
    /// seconds is three of the five-second hold ticks and comfortably inside the sixty-second hold
    /// window, so a point cannot change hands on its own before the garrison lands.
    /// </summary>
    internal const float StrategicRestoreSettleSeconds = 15f;

    /// <summary>
    /// How long after the rebuild the hold tick stays held off, in seconds: 20. A restored owner is
    /// lost on the first hold tick that finds nothing standing on the point
    /// (<c>CommanderStrategicPointService.Step</c> drops the owner the moment no faction qualifies),
    /// and a freshly spawned vehicle needs a frame or two to register with its faction and be
    /// counted. Twenty seconds is four hold ticks of slack against a rebuild that finishes in one
    /// frame, and it is capped rather than open-ended so a rebuild that fails can never freeze the
    /// hold machine for the rest of the match.
    /// </summary>
    internal const float StrategicHoldGraceSeconds = 20f;

    /// <summary>The file name suffix for the strategic save, beside the hot-reload snapshot's plain
    /// <c>.json</c> in the same folder.</summary>
    private const string StrategicSuffix = ".strategic.json";

    /// <summary>What a consumed save is renamed to. Renamed rather than deleted (the hot-reload
    /// store deletes) because a strategic save is expensive to recreate — the match it came from is
    /// over — and the developer reloading the same mission twice in a row is a normal thing to do
    /// while testing. The rename is still what makes a double-apply impossible.</summary>
    private const string StrategicConsumedSuffix = ".strategic.loaded.json";

    /// <summary>
    /// How long the "is there a save waiting?" answer is reused before the file is looked at again,
    /// in unscaled seconds: 0.5. Both callers ask every frame, so this is a UI-cadence cache and not
    /// game logic — half a second is imperceptible in the settings window and still hundreds of
    /// times cheaper than a disk probe per frame.
    /// </summary>
    private const float SaveProbeIntervalSeconds = 0.5f;

    private static string saveProbeMission = string.Empty;
    private static float saveProbeAt = float.NegativeInfinity;
    private static bool saveProbeResult;

    private readonly CommanderServiceRegistry services;

    /// <summary>
    /// The game's own "a mission has just loaded" event, held so it can be detached again. This is
    /// what arms a restore, and getting that signal right is the whole of the fix for the bug the
    /// developer found on 2026-09-17.
    /// </summary>
    /// <remarks>
    /// The first version gated the restore on <c>CommanderStateStore.IsHotReloadLoad</c>, which is
    /// <c>string.IsNullOrEmpty(Assembly.Location)</c> — "this build was loaded from bytes". That is
    /// true for the WHOLE SESSION whenever the mod is installed into <c>BepInEx\scripts\</c>, which
    /// is how the developer runs it every day. It does not mean "this run is a reload"; it means
    /// "this copy lives in scripts". So the restore refused every run, silently, and the save sat on
    /// disk untouched.
    /// <para>
    /// The level clock was considered as the replacement and rejected on the same evidence: the
    /// developer's log shows the HOT-RELOAD snapshot being restored on the restarted mission, which
    /// can only happen if <c>timeSinceLevelLoad</c> did NOT go back to zero — its guard requires the
    /// saved time to be earlier than the current one and within ten minutes. So the level clock
    /// survives a mission restart here and cannot tell one apart from a reload.
    /// </para>
    /// <para>
    /// <c>MissionManager.onMissionLoad</c> has neither problem. It is public, static, and fires
    /// exactly when a mission loads — on a restart, and never on a hot reload, which loads no
    /// mission. A mod instance that comes up mid-match misses it and correctly stands down.
    /// </para>
    /// </remarks>
    private readonly Action<Mission> missionLoadHandler;

    /// <summary>True once <see cref="MissionManager.onMissionLoad"/> has fired for the run in
    /// progress. A restore is armed by this and by nothing else.</summary>
    private bool missionLoadSeen;

    private bool saveRequested;

    /// <summary>Where this mission run has got to. Every transition is logged, including the ones
    /// that do nothing — a silent no-op is what let the restore fail unnoticed for a whole play
    /// session.</summary>
    private StrategicRestorePhase phase = StrategicRestorePhase.NotStarted;

    /// <summary>True once the "waiting for a mission load" line has been written this run, so it is
    /// said once rather than every frame.</summary>
    private bool waitingLogged;

    /// <summary>The save read in phase A, held until phase B rebuilds the world from it.</summary>
    private CommanderStrategicSnapshot? claimed;

    /// <summary>Scaled <c>Time.time</c> at which phase B runs, or negative while nothing is
    /// claimed.</summary>
    private float rebuildAt = -1f;

    /// <summary>Scaled <c>Time.time</c> until which the strategic restore is holding the control
    /// point hold tick off, or negative while it is not. Read by
    /// <see cref="CommanderStrategicPointService"/>.</summary>
    private float holdPointsUntil = -1f;

    internal CommanderStrategicSaveStore(CommanderServiceRegistry services)
    {
        this.services = services;
        Instance = this;
        missionLoadHandler = OnMissionLoad;
        MissionManager.onMissionLoad += missionLoadHandler;
    }

    /// <summary>
    /// Lets go of the game's mission-load event. Called from
    /// <see cref="CommanderModeController.OnDestroy"/> — which BepInEx ScriptEngine calls on the
    /// outgoing instance before installing a reloaded one — the same hook the hot-reload store
    /// already needed a direct call from. A static event holding a dead instance is a leak, and
    /// worse, two instances would both answer.
    /// </summary>
    internal void DetachMissionHook()
    {
        MissionManager.onMissionLoad -= missionLoadHandler;
    }

    /// <summary>
    /// A mission has loaded: this run is a fresh one, so the store starts over and arms itself. The
    /// mod's ordinary <c>ResetSession</c> hangs off an active SCENE change, which a mission restart
    /// does not necessarily produce, so this store resets its own state from the signal that is
    /// actually about missions.
    /// </summary>
    private void OnMissionLoad(Mission mission)
    {
        missionLoadSeen = true;
        ResetSession();
        CommanderPlugin.Log.LogInfo(
            "Strategic load: a mission has loaded, so a strategic restore is armed for this run.");
    }

    internal static CommanderStrategicSaveStore? Instance { get; private set; }

    /// <summary>What the last save or load did, for the settings window. Empty until something has
    /// happened this run.</summary>
    internal static string StatusText { get; private set; } = string.Empty;

    /// <summary>
    /// True while a strategic restore is holding the control point hold tick off. The named rule:
    /// no point is judged before its garrison is down. Bounded by
    /// <see cref="StrategicHoldGraceSeconds"/>, so a rebuild that failed releases the hold machine
    /// instead of freezing it.
    /// </summary>
    internal static bool IsHoldingPoints =>
        Instance != null && HoldsPoints(Time.time, Instance.holdPointsUntil);

    /// <summary>
    /// The hold-off rule itself, pure. A restore stamps <paramref name="holdUntil"/> and the hold
    /// tick stands down until it passes; a negative stamp means no restore has happened, so the
    /// hold machine runs as it always did. The cap is what stops a failed rebuild freezing the hold
    /// machine for the rest of the match, which would be a worse bug than the one it is preventing.
    /// </summary>
    internal static bool HoldsPoints(float now, float holdUntil)
    {
        return holdUntil > 0f && now < holdUntil;
    }

    /// <summary>
    /// True when a strategic save is waiting for this mission, so strategic point discovery holds
    /// off exactly as it does for a hot-reload restore. Without it discovery would finish and the
    /// hold tick would start judging points before the restore had said who owns them.
    /// </summary>
    internal static bool HasPendingSave()
    {
        CommanderStrategicSaveStore? store = Instance;
        if (store == null || store.phase == StrategicRestorePhase.Standby || store.phase == StrategicRestorePhase.Done)
        {
            return false;
        }

        return store.phase == StrategicRestorePhase.Claimed || HasSaveFor(CommanderFeatureGate.MissionName);
    }

    /// <summary>
    /// True while a strategic save is waiting for this mission and has NOT yet been read. The enemy
    /// commander's opening balance is held off while it is, which is the belt to the prepared
    /// flag's braces: the commander's first review fires on its very first tick
    /// (<c>nextReviewAt</c> starts at zero), so relying on the restore winning a race inside one
    /// frame is not good enough. Holding the opener costs one review of delay and removes the
    /// ordering question entirely. The moment the store reaches any settled state — restored,
    /// refused, or nothing to do — this goes false and the opener runs as it always did.
    /// </summary>
    internal static bool IsOpeningBalanceHeld
    {
        get
        {
            CommanderStrategicSaveStore? store = Instance;
            return store != null
                && (store.phase == StrategicRestorePhase.NotStarted || store.phase == StrategicRestorePhase.Claimed)
                && HasSaveFor(CommanderFeatureGate.MissionName);
        }
    }

    /// <summary>
    /// Whether this run should take the save, pure. Both halves are needed and neither alone is
    /// enough: a mission must actually have loaded on this run, which is what separates a restart
    /// from a mod reload into a match already running, and there must be a save to take.
    /// </summary>
    internal static bool ShouldClaimSave(bool missionLoadSeen, bool saveExists)
    {
        return missionLoadSeen && saveExists;
    }

    /// <summary>
    /// Whether a strategic save exists on disk for a mission, for the settings window's status line
    /// and for <see cref="HasPendingSave"/>.
    /// </summary>
    /// <remarks>
    /// Cached, because both callers ask every frame: the settings window redraws in IMGUI and the
    /// strategic points service asks once per <c>TickPersistent</c> while it is holding discovery
    /// off. An unconditional <c>File.Exists</c> on that path is a disk hit per frame for nothing.
    /// Refreshed on the realtime clock at the UI cadence (<see cref="SaveProbeIntervalSeconds"/>),
    /// so the answer is at worst that many seconds stale — which matters to nothing here, since the
    /// only thing that changes it mid-mission is the developer pressing SAVE or DISCARD, and both
    /// of those write the answer straight into the cache.
    /// </remarks>
    internal static bool HasSaveFor(string missionName)
    {
        if (string.IsNullOrEmpty(missionName))
        {
            return false;
        }

        if (string.Equals(saveProbeMission, missionName, StringComparison.Ordinal)
            && Time.unscaledTime < saveProbeAt + SaveProbeIntervalSeconds)
        {
            return saveProbeResult;
        }

        saveProbeMission = missionName;
        saveProbeAt = Time.unscaledTime;
        try
        {
            saveProbeResult = File.Exists(StrategicPathFor(missionName));
        }
        catch (Exception)
        {
            saveProbeResult = false;
        }

        return saveProbeResult;
    }

    /// <summary>Records what a save or discard just did, so the cache above never reports a file
    /// that was deleted a moment ago or misses one just written.</summary>
    private static void NoteSaveFilePresence(string missionName, bool present)
    {
        saveProbeMission = missionName;
        saveProbeAt = Time.unscaledTime;
        saveProbeResult = present;
    }

    /// <summary>Asks for a save on the next tick. The settings window calls this from IMGUI, where
    /// writing a file would stall the frame the button is drawn on; the write happens on the mod's
    /// own persistent tick instead.</summary>
    internal void RequestSave()
    {
        saveRequested = true;
    }

    /// <summary>Throws the pending save away, so the next start of this mission is an ordinary
    /// fresh match. The settings window's DISCARD button.</summary>
    internal static void DiscardSave()
    {
        string missionName = CommanderFeatureGate.MissionName;
        if (string.IsNullOrEmpty(missionName))
        {
            return;
        }

        try
        {
            string path = StrategicPathFor(missionName);
            if (File.Exists(path))
            {
                File.Delete(path);
                NoteSaveFilePresence(missionName, present: false);
                StatusText = "Strategic save discarded.";
                CommanderPlugin.Log.LogInfo($"Strategic save discarded for {missionName}.");
            }
        }
        catch (Exception e)
        {
            StatusText = $"Could not discard the save: {e.Message}";
        }
    }

    public void ResetSession()
    {
        saveRequested = false;
        // missionLoadSeen is deliberately NOT cleared here. This runs on an active SCENE change,
        // and nothing guarantees that fires before the mission-load event rather than after it —
        // clearing the flag here would let a scene change arriving second wipe the very signal that
        // arms the restore, which is the class of bug this whole fix is about. Leaving it set is
        // harmless: a run with no mission and no HQ never reaches the claim, a fresh mission fires
        // the event again, and a mod instance that comes up mid-match starts with it false.
        phase = StrategicRestorePhase.NotStarted;
        waitingLogged = false;
        claimed = null;
        rebuildAt = -1f;
        holdPointsUntil = -1f;
    }

    public void TickPersistent()
    {
        string missionName = CommanderFeatureGate.MissionName;
        FactionHQ? hq = CommanderGameAccess.GetLocalHq();
        if (string.IsNullOrEmpty(missionName) || hq == null)
        {
            return;
        }

        if (saveRequested)
        {
            saveRequested = false;
            WriteSave(missionName);
        }

        switch (phase)
        {
            case StrategicRestorePhase.NotStarted:
                // Phase A, and it runs on the FIRST tick with a live HQ rather than after a settle,
                // because the enemy commander's first review fires on its own first tick and takes
                // the duel head start there. The money has to be on the table before that, not
                // fifteen seconds later when it has already been spent.
                ClaimSave(missionName);
                break;

            case StrategicRestorePhase.Claimed when Time.time >= rebuildAt:
                RebuildWorld();
                break;
        }
    }

    private void WriteSave(string missionName)
    {
        CommanderStrategicSnapshot snapshot = new()
        {
            Mission = missionName,
            FormatVersion = StrategicFormatVersion,
            SavedAtUtc = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss'Z'"),
            SavedLevelTime = Time.timeSinceLevelLoad,
        };
        services.SnapshotStrategicState(new CommanderStrategicWriter(snapshot));
        StripUnitIdentifiers(snapshot);

        if (!CarriesNoUnitIdentifier(snapshot))
        {
            StatusText = "Save refused: the record set carries a unit identifier.";
            CommanderPlugin.Log.LogError(
                "Strategic save refused: a record carried a unit identifier, which does not survive a "
                    + "mission restart and would resolve to a different unit. Nothing was written.");
            return;
        }

        try
        {
            string path = StrategicPathFor(missionName);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonConvert.SerializeObject(snapshot, Formatting.Indented));
            NoteSaveFilePresence(missionName, present: true);
        }
        catch (Exception e)
        {
            StatusText = $"Could not write the save: {e.Message}";
            CommanderPlugin.Log.LogWarning($"Could not write the strategic save: {e.Message}");
            return;
        }

        int held = CountHeld(snapshot.Points);
        StatusText =
            $"Saved {held} held point{(held == 1 ? string.Empty : "s")}, {snapshot.ForwardBases.Count} "
                + $"forward base{(snapshot.ForwardBases.Count == 1 ? string.Empty : "s")}, "
                + $"{snapshot.Treasuries.Count} treasur{(snapshot.Treasuries.Count == 1 ? "y" : "ies")}.";
        CommanderPlugin.Log.LogInfo(
            $"Strategic save written for {missionName}: {snapshot.Points.Count} points ({held} held), "
                + $"{snapshot.Roads.Count} road polylines, {snapshot.ForwardBases.Count} forward bases, "
                + $"{snapshot.Treasuries.Count} treasuries, {snapshot.PreparedFactions.Count} factions "
                + "already prepared.");
        for (int i = 0; i < snapshot.Treasuries.Count; i++)
        {
            CommanderPlugin.Log.LogInfo(
                $"Strategic save: {snapshot.Treasuries[i].Faction} war chest "
                    + $"{snapshot.Treasuries[i].Funds:0}.");
        }
    }

    /// <summary>
    /// Phase A: decide whether this run gets the save, and if it does, take the money side of it
    /// immediately. Every branch says what it decided — the restore failing in total silence is
    /// what cost the developer a play session to notice.
    /// </summary>
    private void ClaimSave(string missionName)
    {
        if (!ShouldClaimSave(missionLoadSeen, HasSaveFor(missionName)))
        {
            if (!missionLoadSeen)
            {
                // Not settled: a mission load may still arrive. Said once, so the developer can tell
                // "waiting" apart from "decided to do nothing".
                if (!waitingLogged)
                {
                    waitingLogged = true;
                    CommanderPlugin.Log.LogInfo(
                        $"Strategic load: a save is waiting for {missionName}, but this run has not seen a "
                            + "mission load, so it is a reload into a match already running and the save is "
                            + "left alone.");
                }

                return;
            }

            Settle(
                StrategicRestorePhase.Standby,
                $"Strategic load: nothing to restore for {missionName} — no save file at "
                    + $"{StrategicPathFor(missionName)}.");
            return;
        }

        string path = StrategicPathFor(missionName);
        CommanderStrategicSnapshot? snapshot;
        try
        {
            snapshot = JsonConvert.DeserializeObject<CommanderStrategicSnapshot>(File.ReadAllText(path));
        }
        catch (Exception e)
        {
            Settle(StrategicRestorePhase.Standby, $"Strategic load REFUSED: the save could not be read ({e.Message}).");
            StatusText = "The strategic save could not be read.";
            return;
        }

        if (snapshot == null || !PassesStrategicGuard(snapshot, missionName))
        {
            Settle(
                StrategicRestorePhase.Standby,
                $"Strategic load REFUSED for {missionName}: the save was written for "
                    + $"'{snapshot?.Mission}' at format {snapshot?.FormatVersion}, and this build reads "
                    + $"format {StrategicFormatVersion}.");
            StatusText = "The strategic save was refused: wrong mission or format.";
            return;
        }

        if (!CarriesNoUnitIdentifier(snapshot))
        {
            Settle(
                StrategicRestorePhase.Standby,
                "Strategic load REFUSED: the save carries a unit identifier, which resolves to a "
                    + "different unit after a mission restart. Nothing was restored.");
            StatusText = "The strategic save was refused: it carries a unit identifier.";
            return;
        }

        // Consumed before anything is applied, so a rebuild that throws part way cannot be replayed
        // on the next start of the same mission.
        ConsumeSaveFile(path, missionName);
        NoteSaveFilePresence(missionName, present: false);

        claimed = snapshot;
        phase = StrategicRestorePhase.Claimed;
        rebuildAt = Time.time + StrategicRestoreSettleSeconds;
        holdPointsUntil = Time.time + StrategicRestoreSettleSeconds + StrategicHoldGraceSeconds;

        CommanderPlugin.Log.LogInfo(
            $"Strategic load STARTED from a save taken {snapshot.SavedAtUtc} (mission clock "
                + $"{snapshot.SavedLevelTime:0} s): {snapshot.Points.Count} points, "
                + $"{snapshot.ForwardBases.Count} forward bases, {snapshot.EconomyBuildings.Count} economy "
                + $"buildings, {snapshot.Treasuries.Count} treasuries. Money first; the world is rebuilt in "
                + $"{StrategicRestoreSettleSeconds:0} s.");

        // The money side, NOW. The prepared flags first of all, because the very next thing the
        // enemy commander does on its own first review is open its treasury, and a restored war
        // chest with a second opening balance on top of it is the corruption this whole track is
        // built to prevent.
        CommanderEnemyCommanderService.Instance?.ApplyStrategicPrepared(claimed.PreparedFactions);
        CommanderEconomyService? economy = CommanderEconomyService.Instance;
        economy?.LoadStrategicMoney(claimed);
        int funded = economy?.ApplyStrategicTreasuries() ?? 0;
        economy?.ApplyStrategicSavings();
        CommanderPlugin.Log.LogInfo(
            $"Strategic load: {funded} of {claimed.Treasuries.Count} war chests set, "
                + $"{claimed.PreparedFactions.Count} factions marked as already having opened their treasury.");
        StatusText = $"Restoring from {snapshot.SavedAtUtc}: {funded} treasuries set.";
    }

    /// <summary>Records a settled outcome and says so once. Every path out of phase A goes through
    /// here, so there is no way to finish silently.</summary>
    private void Settle(StrategicRestorePhase settled, string line)
    {
        phase = settled;
        claimed = null;
        rebuildAt = -1f;
        CommanderPlugin.Log.LogInfo(line);
    }

    /// <summary>
    /// Phase B: rebuild the world from the save claimed in phase A. Deliberately later than the
    /// money, because this spawns bases, buildings and vehicles and must not run while the game is
    /// still placing the mission's own units.
    /// </summary>
    private void RebuildWorld()
    {
        CommanderStrategicSnapshot? snapshot = claimed;
        if (snapshot == null)
        {
            Settle(StrategicRestorePhase.Done, "Strategic load: nothing was claimed, so there is nothing to rebuild.");
            return;
        }

        // Settled BEFORE anything is applied. The apply below spawns and charges; if any step
        // threw with the phase still Claimed, the next frame ran the whole rebuild again, and
        // again every frame after, duplicating buildings and charges each time.
        claimed = null;
        phase = StrategicRestorePhase.Done;
        rebuildAt = -1f;

        // Load-only fan-out. Nothing below writes to the world; the ordered apply that follows does,
        // and it has to be ordered because registration order puts operations before economy while
        // the war chest has to be on the table before a garrison can be bought out of it.
        services.RestoreStrategicState(new CommanderStrategicReader(snapshot));

        // The ordered apply, and every step depends on the one before it:
        //   1. the airbases, forced through the game's own capture state;
        //   2. the mines, factories and docks, because a mine may only be built on a site its owner
        //      holds — which the points restore above has just re-established — and because the
        //      garrison step must see those sites already owned so it does not garrison them;
        //   3. the forward bases and then the garrisons.
        // Each step is guarded on its own and a failed one does not stop the next: the save is
        // already consumed, so whatever can still be rebuilt is better than nothing.
        CommanderEconomyService? economy = CommanderEconomyService.Instance;
        int bases = 0;
        int failedSteps = 0;
        failedSteps += RunRebuildStep("airbase ownership", () =>
            bases = CommanderStrategicPointService.Instance?.ApplyStrategicBaseOwnership() ?? 0);
        failedSteps += RunRebuildStep("economy buildings", () => economy?.ApplyStrategicEconomyBuildings());
        failedSteps += RunRebuildStep("forward bases and garrisons",
            () => CommanderOperationsService.Instance?.ApplyStrategicRebuild());

        // Re-stamped so the grace is measured from the moment the garrisons were actually placed.
        holdPointsUntil = Time.time + StrategicHoldGraceSeconds;

        CommanderPlugin.Log.LogInfo(
            (failedSteps > 0 ? $"Strategic load finished with {failedSteps} failed step(s): " : "Strategic load COMPLETE: ")
                + $"{bases} airbases handed back, "
                + $"{economy?.StrategicEconomyRefund ?? 0f:0} credited back for economy buildings that "
                + $"could not be rebuilt, {CommanderOperationsService.Instance?.StrategicGarrisonSpend ?? 0f:0} "
                + "spent on garrisons.");
        StatusText = $"Restored from {snapshot.SavedAtUtc}.";
    }

    private static int RunRebuildStep(string step, Action apply)
    {
        try
        {
            apply();
            return 0;
        }
        catch (Exception exception)
        {
            CommanderPlugin.Log.LogError($"Strategic load: rebuilding {step} failed and was skipped: {exception}");
            return 1;
        }
    }

    private static void ConsumeSaveFile(string path, string missionName)
    {
        try
        {
            string consumed = StatePathForSuffix(missionName, StrategicConsumedSuffix);
            if (File.Exists(consumed))
            {
                File.Delete(consumed);
            }

            File.Move(path, consumed);
        }
        catch (Exception e)
        {
            // Best effort, and then belt and braces: if the rename failed, delete instead, because
            // a save that is applied twice is far worse than a save that is lost.
            CommanderPlugin.Log.LogWarning($"Could not set the strategic save aside: {e.Message}");
            try
            {
                File.Delete(path);
            }
            catch (Exception)
            {
                // Nothing further to do: the guard below is the last line of defence, and a file
                // that cannot be written to cannot be re-read into a second match either.
            }
        }
    }

    /// <summary>
    /// The strategic gate, and the whole reason this track exists. Two tests, and deliberately NO
    /// time window: an explicit save the developer took does not expire, which is exactly what
    /// <see cref="CommanderStateStore.PassesSessionGuard"/> could not say. The mission must match,
    /// because a save is a picture of one map; the format must match, because a record set read
    /// half way is worse than one refused. Pure, for the self-check.
    /// </summary>
    internal static bool PassesStrategicGuard(CommanderStrategicSnapshot snapshot, string currentMission)
    {
        return snapshot != null
            && !string.IsNullOrEmpty(currentMission)
            && string.Equals(snapshot.Mission, currentMission, StringComparison.Ordinal)
            && snapshot.FormatVersion == StrategicFormatVersion;
    }

    /// <summary>
    /// The no-identifier rule, pure. A <c>PersistentID</c> is a bare counter that
    /// <c>UnitRegistry.Clear()</c> resets to zero on a mission load, so a saved id does not name the
    /// unit it named when it was written — it names whatever unit happens to get that number next.
    /// The point record type is shared with the hot-reload path, which legitimately stores a mine's
    /// id, so this is a VALUE test on the strategic snapshot rather than a claim about the type.
    /// </summary>
    internal static bool CarriesNoUnitIdentifier(CommanderStrategicSnapshot snapshot)
    {
        if (snapshot == null)
        {
            return false;
        }

        for (int i = 0; i < snapshot.Points.Count; i++)
        {
            if (snapshot.Points[i].MinePersistentId != 0u)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Zeroes every identifier on the way out, so a record type that legitimately carries
    /// one for the hot-reload path can still be reused here (Reuse rule 4: one definition of "a
    /// saved control point", not two).</summary>
    private static void StripUnitIdentifiers(CommanderStrategicSnapshot snapshot)
    {
        for (int i = 0; i < snapshot.Points.Count; i++)
        {
            snapshot.Points[i].MinePersistentId = 0u;
        }
    }

    /// <summary>
    /// The no-replayed-clock rule, structural rather than by value. Every cooldown, hysteresis and
    /// pause in the mod is a <c>Time.time</c> stamp; after a mission restart the clock is back near
    /// zero, so a saved value in the thousands reads as far in the future and silently pauses that
    /// behaviour for the rest of the match. The defence is to keep clocks out of the record set
    /// entirely, and this walks the strategic record types to say so: no numeric field whose name
    /// reads like a clock, with exactly one allowed exception —
    /// <c>SavedLevelTime</c>, which is written for the log and never read back as a number.
    /// </summary>
    internal static bool CarriesNoReplayedClock(out string offender)
    {
        offender = string.Empty;
        Type[] recordTypes =
        {
            typeof(CommanderStrategicSnapshot),
            typeof(CommanderStrategicTreasuryRecord),
            typeof(CommanderStrategicForwardBaseRecord),
        };

        for (int t = 0; t < recordTypes.Length; t++)
        {
            PropertyInfo[] properties = recordTypes[t].GetProperties(BindingFlags.Public | BindingFlags.Instance);
            for (int p = 0; p < properties.Length; p++)
            {
                PropertyInfo property = properties[p];
                if (property.PropertyType != typeof(float) && property.PropertyType != typeof(double))
                {
                    continue;
                }

                if (string.Equals(property.Name, nameof(CommanderStrategicSnapshot.SavedLevelTime), StringComparison.Ordinal))
                {
                    continue;
                }

                if (ReadsLikeAClock(property.Name))
                {
                    offender = $"{recordTypes[t].Name}.{property.Name}";
                    return false;
                }
            }
        }

        return true;
    }

    /// <summary>The naming convention every clock in this mod follows — <c>...At</c>,
    /// <c>...Until</c>, <c>...Since</c>, or a name carrying "Cooldown" or "Time". Pure, and the
    /// half of <see cref="CarriesNoReplayedClock"/> that is worth checking on its own.</summary>
    internal static bool ReadsLikeAClock(string propertyName)
    {
        if (string.IsNullOrEmpty(propertyName))
        {
            return false;
        }

        return propertyName.EndsWith("At", StringComparison.Ordinal)
            || propertyName.EndsWith("Until", StringComparison.Ordinal)
            || propertyName.EndsWith("Since", StringComparison.Ordinal)
            || propertyName.IndexOf("Cooldown", StringComparison.Ordinal) >= 0
            || propertyName.IndexOf("Time", StringComparison.Ordinal) >= 0;
    }

    private static int CountHeld(List<CommanderStrategicPointRecord> points)
    {
        int held = 0;
        for (int i = 0; i < points.Count; i++)
        {
            if (!string.IsNullOrEmpty(points[i].OwnerFaction))
            {
                held++;
            }
        }

        return held;
    }

    internal static string StrategicPathFor(string missionName)
    {
        return StatePathForSuffix(missionName, StrategicSuffix);
    }

    private static string StatePathForSuffix(string missionName, string suffix)
    {
        return CommanderStateStore.StatePathFor(missionName, suffix);
    }

    /// <summary>
    /// The pure half of the strategic save, run at plugin load next to the other self-checks: the
    /// gate in every corner, the no-identifier rule, the no-clock rule over the real record types,
    /// and a round trip of the save through the exact serializer used at runtime.
    /// </summary>
    internal static void SelfCheck()
    {
        List<string> failures = new();

        CommanderStrategicSnapshot save = new()
        {
            Mission = "Ground Control Duel",
            FormatVersion = StrategicFormatVersion,
            SavedAtUtc = "2026-09-17 12:00:00Z",
            SavedLevelTime = 4210.5f,
        };

        Expect(failures, "a strategic save is accepted on a fresh mission run",
            PassesStrategicGuard(save, "Ground Control Duel"), true);
        Expect(failures, "a strategic save from another mission is refused",
            PassesStrategicGuard(save, "Some Other Mission"), false);
        Expect(failures, "a strategic save with no mission to compare against is refused",
            PassesStrategicGuard(save, string.Empty), false);

        CommanderStrategicSnapshot olderFormat = new()
        {
            Mission = "Ground Control Duel",
            FormatVersion = StrategicFormatVersion - 1,
        };
        Expect(failures, "a strategic save of an older format is refused",
            PassesStrategicGuard(olderFormat, "Ground Control Duel"), false);

        CommanderStrategicSnapshot newerFormat = new()
        {
            Mission = "Ground Control Duel",
            FormatVersion = StrategicFormatVersion + 1,
        };
        Expect(failures, "a strategic save of a newer format is refused",
            PassesStrategicGuard(newerFormat, "Ground Control Duel"), false);

        // The whole point of the new gate: the hot-reload guard refuses exactly this file, because a
        // mission restart puts the level clock back to zero. This save is hours old and still good.
        CommanderStateSnapshot asHotReload = new() { Mission = save.Mission, LevelTime = save.SavedLevelTime };
        Expect(failures, "the hot-reload guard would have refused a save taken hours ago",
            CommanderStateStore.PassesSessionGuard(asHotReload, save.Mission, 12f), false);
        Expect(failures, "a strategic save never expires",
            PassesStrategicGuard(save, save.Mission), true);

        Expect(failures, "a strategic save with no unit identifier is accepted",
            CarriesNoUnitIdentifier(save), true);
        save.Points.Add(new CommanderStrategicPointRecord { Kind = "Village", MinePersistentId = 41u });
        Expect(failures, "a strategic save carrying a unit identifier is refused",
            CarriesNoUnitIdentifier(save), false);
        StripUnitIdentifiers(save);
        Expect(failures, "a strategic save has its unit identifiers stripped on the way out",
            CarriesNoUnitIdentifier(save), true);

        Expect(failures, "a strategic save carries no clock stamp that is replayed",
            CarriesNoReplayedClock(out string offender), true);
        if (!string.IsNullOrEmpty(offender))
        {
            failures.Add($"a strategic save carries no clock stamp that is replayed: found {offender}");
        }

        Expect(failures, "a field named for a deadline reads like a clock", ReadsLikeAClock("LastContactAt"), true);
        Expect(failures, "a field named for a window reads like a clock", ReadsLikeAClock("HoldUntil"), true);
        Expect(failures, "a field named for an elapsed span reads like a clock", ReadsLikeAClock("IdleSince"), true);
        Expect(failures, "a plain money field does not read like a clock", ReadsLikeAClock("Funds"), false);
        Expect(failures, "a plain faction field does not read like a clock", ReadsLikeAClock("Faction"), false);

        Expect(failures, "a run that has seen a mission load and has a save takes it",
            ShouldClaimSave(missionLoadSeen: true, saveExists: true), true);
        Expect(failures, "a run that has seen no mission load leaves the save alone",
            ShouldClaimSave(missionLoadSeen: false, saveExists: true), false);
        Expect(failures, "a run with no save to take does nothing",
            ShouldClaimSave(missionLoadSeen: true, saveExists: false), false);
        Expect(failures, "a run with neither does nothing",
            ShouldClaimSave(missionLoadSeen: false, saveExists: false), false);

        Expect(failures, "the hold tick is held off while a rebuild is outstanding",
            HoldsPoints(now: 100f, holdUntil: 100f + StrategicHoldGraceSeconds), true);
        Expect(failures, "the hold tick resumes once the hold-off grace expires",
            HoldsPoints(now: 100f + StrategicHoldGraceSeconds, holdUntil: 100f + StrategicHoldGraceSeconds), false);
        Expect(failures, "the hold tick runs normally when no strategic restore has happened",
            HoldsPoints(now: 100f, holdUntil: -1f), false);
        Expect(failures, "the hold-off grace outlasts a control point hold tick",
            StrategicHoldGraceSeconds > 5f, true);
        Expect(failures, "the restore settles before it runs, so the world is placed first",
            StrategicRestoreSettleSeconds > 0f, true);

        save.Treasuries.Add(new CommanderStrategicTreasuryRecord { Faction = "Boscali", Funds = 125000.5f });
        save.ForwardBases.Add(new CommanderStrategicForwardBaseRecord
        {
            Faction = "Boscali",
            PointLabel = "VILLAGE 4",
            X = 1200.5f,
            Y = 34f,
            Z = -870.25f,
        });
        save.PreparedFactions.Add("Primeva");
        save.Roads.Add(new CommanderRoadPolylineRecord { Coordinates = { 0f, 1f, 2f, 3f, 4f, 5f } });

        CommanderStrategicSnapshot? roundTripped;
        try
        {
            roundTripped = JsonConvert.DeserializeObject<CommanderStrategicSnapshot>(
                JsonConvert.SerializeObject(save));
        }
        catch (Exception e)
        {
            failures.Add($"a strategic save serializes at all: threw {e.Message}");
            roundTripped = null;
        }

        if (roundTripped == null)
        {
            failures.Add("a strategic save round trip: the restored save was null");
        }
        else
        {
            Expect(failures, "a strategic save round trip keeps its mission", roundTripped.Mission, save.Mission);
            Expect(failures, "a strategic save round trip keeps its format", roundTripped.FormatVersion, StrategicFormatVersion);
            Expect(failures, "a strategic save round trip keeps a war chest", roundTripped.Treasuries.Count, 1);
            Expect(failures, "a strategic save round trip keeps the war chest to the penny", roundTripped.Treasuries[0].Funds, 125000.5f);
            Expect(failures, "a strategic save round trip keeps a forward base", roundTripped.ForwardBases.Count, 1);
            Expect(failures, "a strategic save round trip keeps the forward base's northing", roundTripped.ForwardBases[0].Z, -870.25f);
            Expect(failures, "a strategic save round trip keeps the prepared factions", roundTripped.PreparedFactions.Count, 1);
            Expect(failures, "a strategic save round trip keeps the roads", roundTripped.Roads.Count, 1);
            Expect(failures, "a strategic save round trip keeps a point", roundTripped.Points.Count, 1);
            Expect(failures, "a strategic save round trip still carries no unit identifier",
                CarriesNoUnitIdentifier(roundTripped), true);
        }

        Report(failures);
    }

    private static void Report(List<string> failures)
    {
        if (failures.Count == 0)
        {
            CommanderPlugin.Log.LogInfo("Strategic save self-check passed.");
            return;
        }

        for (int i = 0; i < failures.Count; i++)
        {
            CommanderPlugin.Log.LogError($"Strategic save self-check FAILED: {failures[i]}");
        }
    }

    private static void Expect(List<string> failures, string name, bool actual, bool expected)
    {
        if (actual != expected)
        {
            failures.Add($"{name}: expected {expected}, got {actual}");
        }
    }

    private static void Expect(List<string> failures, string name, int actual, int expected)
    {
        if (actual != expected)
        {
            failures.Add($"{name}: expected {expected}, got {actual}");
        }
    }

    private static void Expect(List<string> failures, string name, float actual, float expected)
    {
        if (!Mathf.Approximately(actual, expected))
        {
            failures.Add($"{name}: expected {expected}, got {actual}");
        }
    }

    private static void Expect(List<string> failures, string name, string actual, string expected)
    {
        if (!string.Equals(actual, expected, StringComparison.Ordinal))
        {
            failures.Add($"{name}: expected '{expected}', got '{actual}'");
        }
    }
}
