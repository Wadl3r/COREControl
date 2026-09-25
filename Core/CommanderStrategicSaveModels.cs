using System.Collections.Generic;

namespace GroundControlRts;

/// <summary>
/// The whole strategic save, one JSON object per mission
/// (<c>Application.persistentDataPath/COREControlState/&lt;mission&gt;.strategic.json</c>). This is
/// the SECOND file the mod writes, deliberately separate from the hot-reload snapshot
/// (<see cref="CommanderStateSnapshot"/>): the two are read on different runs, accepted by
/// different gates and carry different things, and folding them together would mean a mission
/// restart replaying hot-reload records that are actively harmful across one (see
/// <see cref="CommanderStrategicSaveStore.CarriesNoUnitIdentifier"/>).
/// </summary>
/// <remarks>
/// Plain public properties throughout so <c>Newtonsoft.Json</c> can round-trip it with no
/// attributes, the same shape <see cref="CommanderStateSnapshot"/> uses. The point and road record
/// types are NOT redeclared here — they are
/// <see cref="CommanderStrategicPointRecord"/> and <see cref="CommanderRoadPolylineRecord"/>, the
/// ones the hot-reload path already writes, so there is one definition of "a saved control point"
/// and one owner-by-faction-name discipline (Reuse rule 4).
/// </remarks>
internal sealed class CommanderStrategicSnapshot
{
    /// <summary>The mission this save was taken in, from <see cref="CommanderFeatureGate.MissionName"/>.
    /// Half the strategic gate: a save only ever applies to the mission it was written for.</summary>
    public string Mission { get; set; } = string.Empty;

    /// <summary>The layout of this file. The other half of the gate: a save written by a different
    /// version of the record set is refused rather than half-read. Bumped whenever a field's
    /// MEANING changes, not when one is added.</summary>
    public int FormatVersion { get; set; }

    /// <summary>When the save was taken, for the settings window's status line and the log. A
    /// display string only — nothing reads it back as a number.</summary>
    public string SavedAtUtc { get; set; } = string.Empty;

    /// <summary>
    /// <c>UnityEngine.Time.timeSinceLevelLoad</c> when the save was taken, for the log only.
    /// DELIBERATELY never replayed: after a mission restart the level clock is back near zero, so
    /// any saved stamp reads as far in the future and would silently pause whatever reads it for
    /// the rest of the match. Every other clock in the mod is left out of this file entirely —
    /// <see cref="CommanderStrategicSaveStore.CarriesNoReplayedClock"/> is the check that says so.
    /// </summary>
    public float SavedLevelTime { get; set; }

    /// <summary>Every discovered strategic point and, for a control point, who held it — as a
    /// faction NAME, because an owner index is only meaningful against the faction order it was
    /// taken with.</summary>
    public List<CommanderStrategicPointRecord> Points { get; set; } = new();

    /// <summary>The road polylines discovery retained. Restoring points without them would fail
    /// every road-distance test the insertion and forward-base rules run.</summary>
    public List<CommanderRoadPolylineRecord> Roads { get; set; } = new();

    /// <summary>One war chest per faction: the money it held plus the cash value of everything of
    /// its own that was standing when the save was taken.</summary>
    public List<CommanderStrategicTreasuryRecord> Treasuries { get; set; } = new();

    /// <summary>One record per forward base that was ONLINE. A base still being delivered is
    /// CANCELLED rather than restored or refunded (study, 2026-09-17): there is no refund anywhere
    /// on the delivery path — "the structures were charged at order time, which is the stake" — and
    /// a restored half-built order would sit stranded until its own fifteen-minute stall timeout
    /// killed it, because the transport it was waiting on no longer exists.</summary>
    public List<CommanderStrategicForwardBaseRecord> ForwardBases { get; set; } = new();

    /// <summary>One record per mine, factory and naval dock the commander built, with its upgrade
    /// level. These are rebuilt rather than left as money, because a mine is what owns a resource
    /// site: site ownership follows the building standing on it, so without the mine the site is
    /// neutral however much cash the faction holds.</summary>
    public List<CommanderStrategicEconomyBuildingRecord> EconomyBuildings { get; set; } = new();

    /// <summary>Money each commander was holding OUTSIDE the treasury — the naval, air, radar-watch
    /// and picket pots, and the structure bank. Not extra money (they are earmarks against future
    /// income, not a second balance), but dropping them resets every saving-up decision the
    /// commander had made.</summary>
    public List<CommanderStrategicSavingsRecord> Savings { get; set; } = new();

    /// <summary>The factions whose commander had already opened its treasury when the save was
    /// taken, by faction name. Without this every computer faction is handed a SECOND opening
    /// balance on load and nothing logs it — the nastiest of the three quiet corruptions the
    /// feasibility study found.</summary>
    public List<string> PreparedFactions { get; set; } = new();
}

/// <summary>
/// Where a strategic restore has got to on this mission run. Every transition is logged, including
/// the ones that do nothing: the restore failing in total silence is what cost a whole play session
/// to notice (developer's log, 2026-09-17).
/// </summary>
internal enum StrategicRestorePhase
{
    /// <summary>Nothing decided yet. The store has not had a tick with a live mission and HQ.</summary>
    NotStarted,

    /// <summary>Decided to do nothing, and said why: no save for this mission, a save refused by the
    /// gate, or a run that joined a match already in progress.</summary>
    Standby,

    /// <summary>The save has been read, consumed and its MONEY applied. The world rebuild follows
    /// once the mission has settled.</summary>
    Claimed,

    /// <summary>The world has been rebuilt. Nothing further happens this run.</summary>
    Done,
}

/// <summary>One faction's war chest as saved: its funds plus the cash value of its live force.</summary>
internal sealed class CommanderStrategicTreasuryRecord
{
    /// <summary>The faction's own <c>factionName</c> — the same identity discipline the point
    /// records use, and the only one that survives a mission restart.</summary>
    public string Faction { get; set; } = string.Empty;

    /// <summary>Money on load: what was in the treasury, plus what every live aircraft, ground
    /// vehicle and commander-built building was worth, plus the stake of any forward-base order
    /// still being delivered.</summary>
    public float Funds { get; set; }
}

/// <summary>
/// One online forward base as saved: whose it was and where it stood. No buildings, no delivery
/// counters and no clocks — the base is rebuilt from scratch through the same
/// <c>CommanderEconomyService.TryBuildFob</c> path that built it the first time, so the recipe is
/// whatever the recipe is now.
/// </summary>
internal sealed class CommanderStrategicForwardBaseRecord
{
    public string Faction { get; set; } = string.Empty;

    /// <summary>The label of the control point the base stood on, used to name the rebuilt base and
    /// to re-attach the order to the point after the points restore.</summary>
    public string PointLabel { get; set; } = string.Empty;

    public float X { get; set; }
    public float Y { get; set; }
    public float Z { get; set; }
}

/// <summary>
/// One mine, factory or naval dock the commander built, as saved: whose it was, what it was, where
/// it stood and how far it had been upgraded. Rebuilt on load and BOUGHT BACK at the same sunk cost
/// it was cashed in for, so a save-and-reload leaves the faction no richer and no poorer.
/// </summary>
internal sealed class CommanderStrategicEconomyBuildingRecord
{
    public string Faction { get; set; } = string.Empty;

    /// <summary>The <c>CommanderBuildKind</c> name — <c>Mine</c>, <c>Factory</c> or
    /// <c>NavalDock</c>. Stored as its enum NAME, the same discipline the point records use for
    /// their kind, so a save survives a change to the underlying numbering.</summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>Upgrade level, 1 to <c>CommanderEconomyService.MaxLevel</c>. The sunk cost of the
    /// upgrades paid to reach it is part of what the building is cashed in for and part of what it
    /// costs to buy back.</summary>
    public int Level { get; set; } = 1;

    public float X { get; set; }
    public float Y { get; set; }
    public float Z { get; set; }

    /// <summary>For a factory only: the <c>jsonKey</c> of the vehicle it was producing. A factory
    /// whose production cannot be resolved again is not rebuilt, and its money stays in the
    /// treasury.</summary>
    public string ProductionJsonKey { get; set; } = string.Empty;
}

/// <summary>One commander's money held outside the treasury, by faction name.</summary>
internal sealed class CommanderStrategicSavingsRecord
{
    public string Faction { get; set; } = string.Empty;
    public float NavalFund { get; set; }
    public float AirFund { get; set; }
    public float AwacsSavings { get; set; }
    public float PicketSavings { get; set; }
    public float StructureSavings { get; set; }
}

/// <summary>Thin wrapper an <see cref="ICommanderPersistStrategic"/> service writes into — the
/// strategic twin of <see cref="CommanderStateWriter"/>, and a wrapper for the same reason: the
/// write side keeps a seam to grow into without every service's signature changing again.</summary>
internal sealed class CommanderStrategicWriter
{
    internal CommanderStrategicWriter(CommanderStrategicSnapshot snapshot)
    {
        Snapshot = snapshot;
    }

    internal CommanderStrategicSnapshot Snapshot { get; }
}

/// <summary>Thin wrapper an <see cref="ICommanderPersistStrategic"/> service reads from. Handed out
/// only after <see cref="CommanderStrategicSaveStore"/> has already checked the strategic gate, so
/// a service's <c>RestoreStrategic</c> never has to re-check it.</summary>
internal sealed class CommanderStrategicReader
{
    internal CommanderStrategicReader(CommanderStrategicSnapshot snapshot)
    {
        Snapshot = snapshot;
    }

    internal CommanderStrategicSnapshot Snapshot { get; }
}
