namespace GroundControlRts;

internal static class PluginInfo
{
    /// <summary>BepInEx GUID. Also the Harmony id and the config file name
    /// (<c>BepInEx\config\com.wadl3r.corecontrol.cfg</c>). Distinct from upstream's so the two mods
    /// never share a config file or a Harmony id.</summary>
    public const string Guid = "com.wadl3r.corecontrol";
    public const string Name = "CORE Control";
    public const string Version = "0.7.6.0";

    /// <summary>GUID of Ground Control (RTS), the upstream this fork was renamed from. It patches the
    /// same game methods, so BepInEx is told never to load this plugin beside it: with two different
    /// GUIDs both would load and every Harmony patch would run twice.</summary>
    public const string GroundControlGuid = "com.groundcontrol.rts";

    /// <summary>GUID of Nuclear Option Commander, the original mod Ground Control was renamed from.
    /// Same reason as <see cref="GroundControlGuid"/>.</summary>
    public const string NuclearOptionCommanderGuid = "com.nuclearoption.commander";

    /// <summary>Name of the duel mission the mod ships. It is the mission file name, the folder the
    /// installer writes under the game's user missions and the name the duel rules match on, so it must
    /// differ from upstream's "Ground Control Duel" or the two installs overwrite each other's mission.
    /// The far-start variant starts with it, so a substring match covers both.</summary>
    public const string DuelMissionName = "CORE Control Duel";

    /// <summary>The far-start variant of <see cref="DuelMissionName"/>.</summary>
    public const string DuelFarMissionName = DuelMissionName + " Far";

    /// <summary>Folder under <c>Application.persistentDataPath</c> for the hot-reload snapshot and the
    /// strategic save. Upstream writes "CommanderState"; sharing it would let one mod replay the other's
    /// save into a match.</summary>
    public const string StateFolderName = "COREControlState";
}
