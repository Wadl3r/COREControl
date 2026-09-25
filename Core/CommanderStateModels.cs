using System.Collections.Generic;

namespace GroundControlRts;

/// <summary>
/// The whole hot-reload snapshot, one JSON object per mission
/// (<c>Application.persistentDataPath/COREControlState/&lt;mission&gt;.json</c>). Plain public
/// properties throughout so <c>Newtonsoft.Json</c> can round-trip it with no attributes; see
/// <see cref="CommanderStateStore"/> for the file itself and the session guard.
/// </summary>
internal sealed class CommanderStateSnapshot
{
    /// <summary>The mission this snapshot was taken in, from <see cref="CommanderFeatureGate.MissionName"/>.
    /// Part of the session guard (design §Decisions 4): a restore only ever applies to the same
    /// mission it was written for.</summary>
    public string Mission { get; set; } = string.Empty;

    /// <summary><c>UnityEngine.Time.timeSinceLevelLoad</c> at the moment this snapshot was written.
    /// The other half of the session guard: a restore is only accepted if this is earlier than,
    /// and within the guard window of, the current level time.</summary>
    public float LevelTime { get; set; }

    /// <summary>One entry per live Air Command mission that was launched from a recipe (an adopted
    /// airframe with no recipe is not persisted; see design §2).</summary>
    public List<CommanderAirMissionRecord> AirMissions { get; set; } = new();

    /// <summary>Recipes waiting for a free airbase and the funds to relaunch
    /// (<c>CommanderAirCommandService.relaunchQueue</c>).</summary>
    public List<CommanderAirRecipeRecord> AirRelaunchQueue { get; set; } = new();

    /// <summary>Gold mine upgrade levels, keyed by the mine building's <c>PersistentID</c>.</summary>
    public List<CommanderEconomyLevelRecord> MineLevels { get; set; } = new();

    /// <summary>Factory upgrade levels, keyed by the factory building's <c>PersistentID</c>.</summary>
    public List<CommanderEconomyLevelRecord> FactoryLevels { get; set; } = new();

    /// <summary>Naval dock upgrade levels, keyed by the dock building's <c>PersistentID</c>.</summary>
    public List<CommanderEconomyLevelRecord> DockLevels { get; set; } = new();

    /// <summary>Every strategic point discovery found — sites, villages, hilltops, outposts,
    /// crossroads, road points and bases — so a hot reload does not re-roll a randomly sampled map
    /// and move every objective. Record type and the reasoning in
    /// <c>Points/CommanderStrategicPointPersist.cs</c>.</summary>
    public List<CommanderStrategicPointRecord> StrategicPoints { get; set; } = new();

    /// <summary>The road polylines discovery retained, which the road-distance and insertion rules
    /// read; restoring points without them would fail every road test.</summary>
    public List<CommanderRoadPolylineRecord> StrategicRoads { get; set; } = new();
}

/// <summary>An upgrade level keyed by the building's stable, reload-surviving unit id.</summary>
internal sealed class CommanderEconomyLevelRecord
{
    public uint PersistentId { get; set; }
    public int Level { get; set; }
}

/// <summary>
/// Everything needed to launch (or relaunch) one Air Command mission again, independent of any
/// live aircraft: the airframe and loadout by <c>jsonKey</c> rather than by object reference (an
/// object reference does not survive a hot reload; a <c>jsonKey</c> is the catalogue's own stable
/// id), the departure base by name, and the mission area/settings. Mirrors
/// <c>CommanderAirCommandService.AirMissionRecipe</c> field for field (design §2).
/// </summary>
internal sealed class CommanderAirRecipeRecord
{
    public string AircraftJsonKey { get; set; } = string.Empty;
    public string OriginAirbaseName { get; set; } = string.Empty;
    public string Mode { get; set; } = string.Empty;
    public float AreaX { get; set; }
    public float AreaY { get; set; }
    public float AreaZ { get; set; }
    public float Radius { get; set; }
    public float TargetAltitude { get; set; }
    public bool TargetOrdnance { get; set; }
    public bool SaturationAttack { get; set; }

    /// <summary>One mount <c>jsonKey</c> per hardpoint index (empty string for an unarmed
    /// hardpoint), i.e. <c>Loadout.weapons[i]?.jsonKey ?? ""</c> — the loadout as it was actually
    /// built at launch, not the live hardpoint picker (design §2).</summary>
    public List<string> MountJsonKeys { get; set; } = new();
}

/// <summary>One live, in-flight Air Command mission: the airframe by <c>PersistentID</c> (the one
/// reference that does survive a hot reload) plus everything not re-derivable from the aircraft's
/// own live state.</summary>
internal sealed class CommanderAirMissionRecord
{
    public uint PersistentId { get; set; }
    public bool PurchasedWithFunds { get; set; }
    public float PurchaseCost { get; set; }
    public bool AutoRecreate { get; set; }
    public CommanderAirRecipeRecord Recipe { get; set; } = new();
}

/// <summary>Thin wrapper an <see cref="ICommanderPersistState"/> service writes into. A wrapper
/// rather than passing <see cref="CommanderStateSnapshot"/> directly so the write side has a seam
/// to grow into (e.g. a shared "was anything written" flag) without every service's signature
/// changing again.</summary>
internal sealed class CommanderStateWriter
{
    internal CommanderStateWriter(CommanderStateSnapshot snapshot)
    {
        Snapshot = snapshot;
    }

    internal CommanderStateSnapshot Snapshot { get; }
}

/// <summary>Thin wrapper an <see cref="ICommanderPersistState"/> service reads from. Handed out
/// only after <see cref="CommanderStateStore"/> has already checked the session guard, so a
/// service's <c>Restore</c> never has to re-check it.</summary>
internal sealed class CommanderStateReader
{
    internal CommanderStateReader(CommanderStateSnapshot snapshot)
    {
        Snapshot = snapshot;
    }

    internal CommanderStateSnapshot Snapshot { get; }
}
