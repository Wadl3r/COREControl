using System;
using System.IO;
using Newtonsoft.Json;
using UnityEngine;

namespace GroundControlRts;

/// <summary>
/// Owns the hot-reload snapshot file and drives when the rest of the mod writes and reads it. See
/// the design (<c>conductor/tracks/persist-reload-state_20260913/design.md</c>) for the full
/// mechanism; in short: write periodically and on shutdown while
/// <see cref="CommanderSettings.PersistStateAcrossReload"/> is on and a mission is running, read
/// at most once per mission run and only on the run that loaded from a BepInEx ScriptEngine hot
/// reload, gated by a session guard so a stale file from an earlier match can never be replayed.
/// </summary>
/// <remarks>
/// This is not itself a game-state service — it holds no gameplay data of its own — so instead of
/// implementing <see cref="ICommanderPersistState"/> it is the thing that calls
/// <see cref="CommanderServiceRegistry.SnapshotState"/> and
/// <see cref="CommanderServiceRegistry.RestoreState"/> on everyone who did. It is still registered
/// like any other service (<see cref="ICommanderTickPersistent"/>, <see cref="ICommanderResetSession"/>)
/// so its own per-mission state (the "have I tried to restore yet" flag, the snapshot clock) resets
/// the same way everyone else's does.
/// </remarks>
internal sealed class CommanderStateStore : ICommanderTickPersistent, ICommanderResetSession
{
    /// <summary>
    /// How often a safety snapshot is written while a mission runs and the toggle is on. 20 s:
    /// often enough that a crash between saves loses at most one Air Command relaunch cycle's
    /// worth of state, cheap enough next to the 15 s economy income tick it shares a review
    /// cadence with (design §Section 1).
    /// </summary>
    private const float SnapshotIntervalSeconds = 20f;

    /// <summary>
    /// How stale a hot-reload snapshot may be and still be trusted. The session guard (design
    /// §Decisions 4) rejects a file whose recorded level time is not within this many seconds of
    /// the current one, so a leftover file from an earlier match on the same map is never
    /// replayed into a new one. 600 s (10 minutes) is generous next to the 20 s save cadence above
    /// — a hot reload is a few seconds, not minutes — while still covering a developer who steps
    /// away from the keyboard between a save and the reload key.
    /// </summary>
    private const float SessionGuardWindowSeconds = 600f;

    private readonly CommanderServiceRegistry services;
    private bool restoreAttempted;
    private float nextSnapshotAt;

    internal CommanderStateStore(CommanderServiceRegistry services)
    {
        this.services = services;
    }

    /// <summary>True only for the run that loaded from bytes rather than from the plugins\ folder
    /// on disk — BepInEx ScriptEngine's hot reload. The same detector
    /// <see cref="CommanderMissionInstaller"/> already uses for the same reason. Internal rather
    /// than private since 2026-09-14 so the strategic-points service can hold discovery back for
    /// the restore grace on exactly the runs this is true for, instead of defining the check a
    /// third time.</summary>
    internal static bool IsHotReloadLoad => string.IsNullOrEmpty(typeof(CommanderStateStore).Assembly.Location);

    public void ResetSession()
    {
        restoreAttempted = false;
        nextSnapshotAt = 0f;
    }

    public void TickPersistent()
    {
        if (!CommanderSettings.PersistStateAcrossReload)
        {
            return;
        }

        string missionName = CommanderFeatureGate.MissionName;
        FactionHQ? hq = CommanderGameAccess.GetLocalHq();
        if (string.IsNullOrEmpty(missionName) || hq == null)
        {
            return;
        }

        if (!restoreAttempted && IsHotReloadLoad)
        {
            restoreAttempted = true;
            TryRestore(missionName);
        }

        if (CommanderScheduler.IsDue(ref nextSnapshotAt, SnapshotIntervalSeconds))
        {
            WriteSnapshot(missionName);
        }
    }

    /// <summary>
    /// Called from <see cref="CommanderModeController.OnDestroy"/>, which BepInEx ScriptEngine
    /// calls on the outgoing plugin instance before installing the reloaded one — the same hook
    /// <see cref="CommanderModeController"/> already relied on for its own camera/cursor teardown
    /// before this track existed.
    /// </summary>
    internal void WriteSnapshotOnDestroy()
    {
        if (!CommanderSettings.PersistStateAcrossReload)
        {
            return;
        }

        string missionName = CommanderFeatureGate.MissionName;
        if (string.IsNullOrEmpty(missionName) || CommanderGameAccess.GetLocalHq() == null)
        {
            return;
        }

        WriteSnapshot(missionName);
    }

    private void WriteSnapshot(string missionName)
    {
        CommanderStateSnapshot snapshot = new()
        {
            Mission = missionName,
            LevelTime = Time.timeSinceLevelLoad,
        };
        services.SnapshotState(new CommanderStateWriter(snapshot));

        try
        {
            string path = GetSnapshotPath(missionName);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonConvert.SerializeObject(snapshot));
            int levels = snapshot.MineLevels.Count + snapshot.FactoryLevels.Count + snapshot.DockLevels.Count;
            CommanderPlugin.Log.LogInfo($"Snapshot written: {snapshot.AirMissions.Count} missions, {levels} levels");
        }
        catch (Exception e)
        {
            // A locked or read-only profile folder is a lost safety snapshot, not a reason to take
            // the mod down mid-mission — same stance as CommanderMissionInstaller.
            CommanderPlugin.Log.LogWarning($"Could not write the hot-reload state snapshot: {e.Message}");
        }
    }

    private void TryRestore(string missionName)
    {
        string path = GetSnapshotPath(missionName);
        if (!File.Exists(path))
        {
            return;
        }

        CommanderStateSnapshot? snapshot;
        try
        {
            snapshot = JsonConvert.DeserializeObject<CommanderStateSnapshot>(File.ReadAllText(path));
        }
        catch (Exception e)
        {
            CommanderPlugin.Log.LogWarning($"Could not read the hot-reload state snapshot: {e.Message}");
            DeleteSnapshot(path);
            return;
        }

        // Read once, then gone, whether or not the guard below accepts it — a snapshot must never
        // be applied twice, and a rejected one must never be retried on the next reload either.
        DeleteSnapshot(path);

        if (snapshot == null || !PassesSessionGuard(snapshot, missionName, Time.timeSinceLevelLoad))
        {
            return;
        }

        services.RestoreState(new CommanderStateReader(snapshot));
    }

    /// <summary>
    /// Design §Decisions 4: a snapshot may only be replayed into the same mission run that wrote
    /// it. The mission name must match, and the snapshot's recorded level time must be strictly
    /// earlier than <paramref name="currentLevelTime"/> (a reload never travels forward in time —
    /// equal or later means this is not a reload of the run that wrote it) and within
    /// <see cref="SessionGuardWindowSeconds"/> of it, so a stale file left over from an earlier
    /// match on the same map fails the window instead of being replayed into a new one.
    /// </summary>
    internal static bool PassesSessionGuard(CommanderStateSnapshot snapshot, string currentMission, float currentLevelTime)
    {
        return string.Equals(snapshot.Mission, currentMission, StringComparison.Ordinal)
            && snapshot.LevelTime < currentLevelTime
            && currentLevelTime - snapshot.LevelTime <= SessionGuardWindowSeconds;
    }

    private static void DeleteSnapshot(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception)
        {
            // Best-effort: a locked file is not fatal, it just risks one stale replay attempt that
            // the guard above will very likely still reject on mission name or the time window.
        }
    }

    private static string GetSnapshotPath(string missionName)
    {
        return StatePathFor(missionName, ".json");
    }

    /// <summary>
    /// Where a per-mission state file lives: one folder, one sanitized mission name, one suffix.
    /// Extracted from <see cref="GetSnapshotPath"/> behaviour-neutrally when the strategic save
    /// became the second file written there (Reuse rule 5), so the folder and the name sanitiser
    /// have one definition and the two stores can never disagree about where they put things.
    /// </summary>
    internal static string StatePathFor(string missionName, string suffix)
    {
        return Path.Combine(
            Path.Combine(Application.persistentDataPath, PluginInfo.StateFolderName),
            SanitizeFileName(missionName) + suffix);
    }

    private static string SanitizeFileName(string name)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        char[] chars = name.ToCharArray();
        for (int i = 0; i < chars.Length; i++)
        {
            if (Array.IndexOf(invalid, chars[i]) >= 0)
            {
                chars[i] = '_';
            }
        }
        return new string(chars);
    }

    /// <summary>
    /// Two pure checks, run at plugin load next to the other self-checks: a round trip of the DTOs
    /// through the exact serializer used at runtime with synthetic values (no game objects
    /// involved), and every corner of the session guard's time window.
    /// </summary>
    internal static void SelfCheck()
    {
        CommanderStateSnapshot original = new()
        {
            Mission = "Self-Check Mission",
            LevelTime = 123.5f,
        };
        original.AirMissions.Add(new CommanderAirMissionRecord
        {
            PersistentId = 42u,
            PurchasedWithFunds = true,
            PurchaseCost = 1500f,
            AutoRecreate = true,
            Recipe = new CommanderAirRecipeRecord
            {
                AircraftJsonKey = "f22a",
                OriginAirbaseName = "Home Field",
                Mode = "AirGuard",
                AreaX = 100f,
                AreaY = 20f,
                AreaZ = -300.25f,
                Radius = 30000f,
                TargetAltitude = 6000f,
                TargetOrdnance = true,
                SaturationAttack = false,
                MountJsonKeys = { "aim120", string.Empty, "aim9x" },
            },
        });
        original.AirRelaunchQueue.Add(new CommanderAirRecipeRecord { AircraftJsonKey = "ah64", Mode = "Cas" });
        original.MineLevels.Add(new CommanderEconomyLevelRecord { PersistentId = 7u, Level = 2 });
        original.FactoryLevels.Add(new CommanderEconomyLevelRecord { PersistentId = 8u, Level = 3 });
        original.DockLevels.Add(new CommanderEconomyLevelRecord { PersistentId = 9u, Level = 1 });

        CommanderStateSnapshot? roundTripped;
        try
        {
            string json = JsonConvert.SerializeObject(original);
            roundTripped = JsonConvert.DeserializeObject<CommanderStateSnapshot>(json);
        }
        catch (Exception e)
        {
            CommanderPlugin.Log.LogError($"State store self-check FAILED: the snapshot did not serialize at all ({e.Message}).");
            return;
        }

        if (roundTripped == null
            || roundTripped.Mission != original.Mission
            || roundTripped.LevelTime != original.LevelTime
            || roundTripped.AirMissions.Count != 1
            || roundTripped.AirMissions[0].PersistentId != 42u
            || roundTripped.AirMissions[0].Recipe.AircraftJsonKey != "f22a"
            || roundTripped.AirMissions[0].Recipe.MountJsonKeys.Count != 3
            || roundTripped.AirMissions[0].Recipe.MountJsonKeys[1] != string.Empty
            || roundTripped.AirRelaunchQueue.Count != 1
            || roundTripped.MineLevels.Count != 1
            || roundTripped.MineLevels[0].Level != 2
            || roundTripped.FactoryLevels.Count != 1
            || roundTripped.DockLevels.Count != 1)
        {
            CommanderPlugin.Log.LogError("State store self-check FAILED: the snapshot did not round-trip through JSON intact.");
            return;
        }

        CommanderStateSnapshot guardSnapshot = new() { Mission = "Guard Mission", LevelTime = 100f };
        if (!PassesSessionGuard(guardSnapshot, "Guard Mission", 105f))
        {
            CommanderPlugin.Log.LogError("State store self-check FAILED: a snapshot inside the guard window was rejected.");
            return;
        }
        if (PassesSessionGuard(guardSnapshot, "A Different Mission", 105f))
        {
            CommanderPlugin.Log.LogError("State store self-check FAILED: a mission-name mismatch was accepted.");
            return;
        }
        if (PassesSessionGuard(guardSnapshot, "Guard Mission", 100f))
        {
            CommanderPlugin.Log.LogError("State store self-check FAILED: a snapshot at the current level time (not earlier) was accepted.");
            return;
        }
        if (PassesSessionGuard(guardSnapshot, "Guard Mission", 95f))
        {
            CommanderPlugin.Log.LogError("State store self-check FAILED: a snapshot from later than the current level time was accepted.");
            return;
        }
        if (PassesSessionGuard(guardSnapshot, "Guard Mission", 100f + SessionGuardWindowSeconds + 1f))
        {
            CommanderPlugin.Log.LogError("State store self-check FAILED: a snapshot outside the guard window was accepted.");
        }
    }
}
