using System;
using System.IO;
using UnityEngine;

namespace GroundControlRts;

/// <summary>
/// Copies the missions shipped next to the DLL into the game's own user mission folder, so a map
/// that comes with the mod shows up in the mission list without the player moving files by hand.
/// </summary>
/// <remarks>
/// A shipped mission is mod content, not player content: if the file on disk differs from the one
/// in the plugin folder it is overwritten, so a fix to the map arrives with the next mod update.
/// Anyone who wants to edit it should save a copy under a different name.
/// </remarks>
internal static class CommanderMissionInstaller
{
    /// <summary>Mission file names shipped beside the plugin DLL, without the extension.</summary>
    private static readonly string[] ShippedMissions =
    {
        PluginInfo.DuelMissionName,
        PluginInfo.DuelFarMissionName,
    };

    internal static void InstallShippedMissions()
    {
        // An assembly loaded from bytes (BepInEx ScriptEngine hot reload) has an empty Location,
        // and Path.GetDirectoryName("") throws on Mono rather than returning null. Without this
        // check the whole plugin aborted in Awake under hot reload and the CMD button never
        // appeared. The missions were installed by the normal plugins\ load, so skipping is safe.
        string location = typeof(CommanderMissionInstaller).Assembly.Location;
        if (string.IsNullOrEmpty(location))
        {
            CommanderPlugin.Log.LogInfo(
                "Plugin was loaded from memory (hot reload), so shipped missions were not re-installed.");
            return;
        }

        string? pluginFolder = Path.GetDirectoryName(location);
        if (string.IsNullOrEmpty(pluginFolder))
        {
            return;
        }

        for (int i = 0; i < ShippedMissions.Length; i++)
        {
            Install(pluginFolder!, ShippedMissions[i]);
        }
    }

    private static void Install(string pluginFolder, string missionName)
    {
        string source = Path.Combine(pluginFolder, missionName + ".json");
        if (!File.Exists(source))
        {
            CommanderPlugin.Log.LogWarning(
                $"Mission '{missionName}' was not found next to the plugin DLL, so it was not installed. "
                    + "Copy the whole plugin folder from the release, not just the DLL.");
            return;
        }

        // The game reads user missions from <persistentDataPath>/Missions/<name>/<name>.json, with a
        // meta.json alongside naming the file.
        string folder = Path.Combine(Path.Combine(Application.persistentDataPath, "Missions"), missionName);
        string destination = Path.Combine(folder, missionName + ".json");
        try
        {
            string json = File.ReadAllText(source);

            // Without an objective by this exact name the game throws out of StartMission and the
            // mission's own units never spawn, which reads in-game as an empty map plus one load
            // warning. Cheap to check here, expensive to diagnose in the log.
            if (json.IndexOf("\"Mission Start\"", StringComparison.Ordinal) < 0)
            {
                CommanderPlugin.Log.LogWarning(
                    $"Mission '{missionName}' has no 'Mission Start' objective; the game will not start it.");
            }

            if (File.Exists(destination)
                && string.Equals(File.ReadAllText(destination), json, StringComparison.Ordinal))
            {
                return;
            }

            Directory.CreateDirectory(folder);
            File.WriteAllText(destination, json);
            File.WriteAllText(Path.Combine(folder, "meta.json"), "{\"FileName\":\"" + missionName + "\"}");
            CommanderPlugin.Log.LogInfo($"Installed mission '{missionName}' to {folder}.");
        }
        catch (Exception e)
        {
            // A locked or read-only profile folder is a bad install, not a reason to take the mod down.
            CommanderPlugin.Log.LogWarning($"Could not install mission '{missionName}': {e.Message}");
        }
    }
}
