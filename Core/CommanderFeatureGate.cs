using System;

namespace GroundControlRts;

internal static class CommanderFeatureGate
{
    private static readonly string[] SupportedMissionNames =
    {
        "Altercation",
        "Confrontation",
        "Domination",
        "Escalation",
        PluginInfo.DuelMissionName,
        "Terminal Control"
    };

    private static string missionName = string.Empty;
    private static bool initialized;
    private static bool supportedMission;
    private static bool manuallyUnlocked;

    internal static string MissionName => missionName;
    internal static bool IsSupportedMission => supportedMission;
    internal static bool AdvancedFeaturesEnabled => supportedMission || manuallyUnlocked;

    internal static void RefreshMission()
    {
        string currentName = MissionManager.CurrentMission?.Name?.Trim() ?? string.Empty;
        if (initialized && string.Equals(missionName, currentName, StringComparison.Ordinal))
        {
            return;
        }

        missionName = currentName;
        supportedMission = IsSupportedName(currentName);
        manuallyUnlocked = false;
        initialized = true;
    }

    internal static void UnlockAdvancedFeatures()
    {
        manuallyUnlocked = true;
    }

    internal static void ResetSession()
    {
        missionName = string.Empty;
        supportedMission = false;
        manuallyUnlocked = false;
        initialized = false;
    }

    private static bool IsSupportedName(string name)
    {
        for (int i = 0; i < SupportedMissionNames.Length; i++)
        {
            if (name.IndexOf(SupportedMissionNames[i], StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }
        }
        return false;
    }
}
