using System;
using System.Collections.Generic;
using UnityEngine;

namespace GroundControlRts;

/// <summary>
/// Hot-reload persistence for the discovered map: every strategic point and the retained road
/// polylines (developer quality-of-life, off by default — see <see cref="CommanderStateStore"/>).
/// </summary>
/// <remarks>
/// <para>
/// Why this exists at all, when the class remark on <see cref="CommanderStrategicPointService"/>
/// says owners are deliberately not persisted across a MISSION reload: a hot reload is not a
/// mission reload. The world, every building and every vehicle in it are untouched; only this
/// mod's own in-memory lists are wiped. Re-running discovery against that untouched world does not
/// reproduce the same answer, because the site fill and the hilltop scan both sample randomly — the
/// last three reloads of one match produced 13, 16 and 14 resource sites at different places. Every
/// mission aimed at a point that no longer exists is then thrown away and the platoons holding it
/// dissolve, so the developer watches the objectives move every time the mod rebuilds.
/// </para>
/// <para>
/// Seeding the random sampler with a fixed seed was considered and rejected (DECISION-022): the
/// candidate set itself changes as buildings are destroyed during the match, so the same seed
/// would still yield a different map later in a match. Carrying the answer across is the only
/// thing that actually holds the objectives still.
/// </para>
/// <para>
/// The record types live in this file rather than in <c>Core/CommanderStateModels.cs</c> beside
/// the economy and Air Command records purely to keep this track's edit to that shared file down
/// to the two snapshot properties themselves; they are plain public-property classes serialised by
/// the same <c>Newtonsoft.Json</c> round trip as every other record.
/// </para>
/// </remarks>
internal sealed partial class CommanderStrategicPointService : ICommanderPersistState, ICommanderPersistStrategic
{
    /// <summary>
    /// How long discovery waits, on a hot-reload run only, before it starts sampling the map — the
    /// window in which <see cref="CommanderStateStore"/> gets its one chance to restore the
    /// previous run's points instead. Needed because this service is registered BEFORE the store
    /// (<see cref="CommanderModeController"/>), so its first <c>TickPersistent</c> of the reloaded
    /// run happens before the store's, and without the wait discovery could start the very frame
    /// before the restore arrives. 2 s: a hot reload settles in about 3 s and the store restores on
    /// its first tick with a live HQ, so one or two seconds is already ample; the cost when there
    /// is nothing to restore is a two-second later start to a pass that takes several seconds
    /// anyway, and it is paid only on a reload run, never on a normal launch.
    /// </summary>
    private const float RestoreGraceSeconds = 2f;

    /// <summary>
    /// The same wait, for a STRATEGIC save waiting on a fresh mission run: the strategic restore
    /// deliberately lets the mission settle first
    /// (<see cref="CommanderStrategicSaveStore.StrategicRestoreSettleSeconds"/>), so the two-second
    /// hot-reload grace would expire long before it arrives. The settle plus the hold grace plus
    /// ten seconds of slack, measured on the WALL clock against a settle measured on the game clock,
    /// which is why the slack is there at all: at half speed the game clock lags. Long enough that
    /// discovery never finishes on a map the restore is about to replace — otherwise every platoon,
    /// picket mission and forward-base plan made in those seconds would be aimed at point objects
    /// the restore then throws away.
    /// </summary>
    private const float StrategicRestoreGraceSeconds =
        CommanderStrategicSaveStore.StrategicRestoreSettleSeconds
            + CommanderStrategicSaveStore.StrategicHoldGraceSeconds
            + 10f;

    /// <summary>When the hot-reload restore grace expires, on <c>Time.realtimeSinceStartup</c> —
    /// the same clock <c>discoveryRetryAt</c> uses, because this is a one-shot wall-clock wait for
    /// another system to load, not periodic game logic. Negative means "not started yet".</summary>
    private float restoreGraceUntil = -1f;

    /// <summary>True once <see cref="Restore"/> has run for this mission, whether or not the
    /// snapshot actually carried any points. Ends the grace above immediately.</summary>
    private bool restoreHandled;

    /// <summary>True once discovery has finished and the point list is the final one. Read by the
    /// snapshot (a half-finished list must never be saved) and by the self-check.</summary>
    internal bool DiscoveryComplete => discovery == DiscoveryState.Done;

    /// <summary>
    /// True while discovery should hold off because a hot-reload restore may still be coming. Only
    /// ever true on a run that loaded from memory with the persistence toggle on, and only until
    /// <see cref="RestoreGraceSeconds"/> have passed or <see cref="Restore"/> has run.
    /// </summary>
    private bool IsWaitingForRestore()
    {
        if (restoreHandled)
        {
            return false;
        }

        // Two reasons to wait, and both are "another system is about to hand this service its map".
        // The hot reload is the original one. The second is a strategic save waiting for this
        // mission: the restore runs on a fresh run, and without the wait discovery would finish and
        // the hold tick would start judging points before the restore had said who owns them.
        bool hotReloadPending = CommanderSettings.PersistStateAcrossReload && CommanderStateStore.IsHotReloadLoad;
        bool strategicPending = CommanderStrategicSaveStore.HasPendingSave();
        if (!hotReloadPending && !strategicPending)
        {
            return false;
        }

        if (restoreGraceUntil < 0f)
        {
            restoreGraceUntil = Time.realtimeSinceStartup
                + (strategicPending ? StrategicRestoreGraceSeconds : RestoreGraceSeconds);
        }

        return Time.realtimeSinceStartup < restoreGraceUntil;
    }

    /// <summary>Clears the hot-reload restore bookkeeping; called from <c>ResetSession</c> beside
    /// every other per-mission reset.</summary>
    private void ResetPersistState()
    {
        restoreGraceUntil = -1f;
        restoreHandled = false;
    }

    public void Snapshot(CommanderStateWriter w)
    {
        // A discovery pass part way through holds only the points found so far. Saving that and
        // restoring it next reload would skip the rest of the pass for good, so nothing is written
        // until the pass has finished.
        if (!DiscoveryComplete)
        {
            return;
        }

        List<string> factionNames = CollectFactionNames();
        for (int i = 0; i < points.Count; i++)
        {
            w.Snapshot.StrategicPoints.Add(ToRecord(points[i], factionNames));
        }

        for (int i = 0; i < roadPointLists.Count; i++)
        {
            w.Snapshot.StrategicRoads.Add(ToRecord(roadPointLists[i]));
        }
    }

    public void Restore(CommanderStateReader r)
    {
        ApplyPointRecords(r.Snapshot.StrategicPoints, r.Snapshot.StrategicRoads, attachMines: true, "across hot reload");
    }

    /// <summary>
    /// The strategic save's write side: the same points, the same roads and the same
    /// owner-by-faction-name discipline as the hot-reload snapshot, with the mine identifier
    /// suppressed. A <c>PersistentID</c> is a counter a mission load resets, so carrying one across
    /// a restart would attach the record to whatever unit happens to get that number next; the
    /// store refuses a save that carries one
    /// (<see cref="CommanderStrategicSaveStore.CarriesNoUnitIdentifier"/>).
    /// </summary>
    public void SnapshotStrategic(CommanderStrategicWriter w)
    {
        // Same rule as the hot-reload snapshot and for the same reason: a discovery pass part way
        // through holds only the points found so far, and saving that would skip the rest for good.
        if (!DiscoveryComplete)
        {
            return;
        }

        List<string> factionNames = CollectFactionNames();
        for (int i = 0; i < points.Count; i++)
        {
            CommanderStrategicPointRecord record = ToRecord(points[i], factionNames);
            record.MinePersistentId = 0u;
            w.Snapshot.Points.Add(record);
        }

        for (int i = 0; i < roadPointLists.Count; i++)
        {
            w.Snapshot.Roads.Add(ToRecord(roadPointLists[i]));
        }
    }

    /// <summary>
    /// The strategic save's read side. Load-only, as
    /// <see cref="ICommanderPersistStrategic"/> requires: the points, their owners and the roads go
    /// into this service's own lists and nothing is written to the world. There are no mines to
    /// re-attach, because a mine is a building the commander built and buildings are cashed into
    /// the war chest rather than recreated (user decision 2026-09-17).
    /// </summary>
    public void RestoreStrategic(CommanderStrategicReader r)
    {
        ApplyPointRecords(r.Snapshot.Points, r.Snapshot.Roads, attachMines: false, "from the strategic save");
    }

    /// <summary>
    /// Hands each saved AIRBASE back to the faction that held it, through the game's own capture
    /// state rather than through the mod's hold machine. A base's owner is
    /// <c>Airbase.CurrentHQ</c>, driven by the game's capture ring, so writing the mod's own hold
    /// field would change nothing — and a base cannot be garrisoned into ownership the way a
    /// village can.
    /// </summary>
    /// <remarks>
    /// <b>Unproven in play.</b> <c>Capture.ForceCapture</c> is public and server-only but is called
    /// nowhere else in this mod (study, 2026-09-17), so this is the one step of the restore with no
    /// prior art behind it. It also reassigns every building on the base, which can leave a
    /// knocked-out building attributed to the wrong faction; the study's verdict was that this is
    /// worth a log line rather than a guard, so each forced capture says what it did.
    /// <para>
    /// Driven by <see cref="CommanderStrategicSaveStore"/> during the ordered apply rather than
    /// from <see cref="RestoreStrategic"/>, which writes nothing to the world by contract.
    /// </para>
    /// </remarks>
    internal int ApplyStrategicBaseOwnership()
    {
        int forced = 0;
        for (int i = 0; i < points.Count; i++)
        {
            CommanderStrategicPoint point = points[i];
            if (point.Kind != StrategicPointKind.Base || point.Airbase == null || point.Airbase.disabled)
            {
                continue;
            }

            FactionHQ? owner = HqAt(point.SavedBaseOwnerIndex);
            point.SavedBaseOwnerIndex = -1;
            if (owner == null || !owner.IsServer)
            {
                // Spawning and capture are both server-side; a pure multiplayer client throws.
                continue;
            }

            if (ReferenceEquals(point.Airbase.CurrentHQ, owner))
            {
                continue;
            }

            Capture? capture = point.Airbase.capture;
            if (capture == null)
            {
                CommanderPlugin.Log.LogWarning(
                    $"Strategic load: {point.Label} has no capture ring, so its owner could not be restored.");
                continue;
            }

            capture.ForceCapture(owner);
            forced++;
            CommanderAiLog.Note(hq: owner, $"strategic load: {point.Label} handed back by force capture.");
            CommanderPlugin.Log.LogInfo(
                $"Strategic load: forced {point.Label} to {owner.faction?.factionName}. Every building on it "
                    + "is reassigned with the base, so a knocked-out one may now read as theirs.");
        }

        return forced;
    }

    /// <summary>
    /// The one restore body, shared by the hot-reload path and the strategic save (Reuse rule 4:
    /// the two differ only in whether a mine identifier is worth resolving, so the difference is a
    /// parameter and not a second copy).
    /// </summary>
    private void ApplyPointRecords(
        List<CommanderStrategicPointRecord> records,
        List<CommanderRoadPolylineRecord> roads,
        bool attachMines,
        string what)
    {
        restoreHandled = true;

        if (records.Count == 0)
        {
            // Nothing was saved (an older snapshot, or one taken mid-discovery). Leave the service
            // exactly as it was so discovery runs normally.
            return;
        }

        points.Clear();
        roadPointLists.Clear();

        // The hold indices below are indices into hqOrder, so the snapshot has to be populated
        // BEFORE they are resolved — otherwise the first hold tick would see the membership
        // "change" from empty to full and reset every restored owner back to neutral.
        RefreshHqOrder();
        List<string> factionNames = CollectFactionNames();

        int held = 0;
        for (int i = 0; i < records.Count; i++)
        {
            CommanderStrategicPoint? point = FromRecord(records[i], factionNames);
            if (point == null)
            {
                CommanderPlugin.Log.LogInfo(
                    $"Strategic points restore: dropped a point of unknown kind '{records[i].Kind}'.");
                continue;
            }

            if (point.Kind == StrategicPointKind.Base)
            {
                point.Airbase = FindAirbaseByLabel(records[i].AirbaseName);
                if (point.Airbase == null)
                {
                    CommanderPlugin.Log.LogInfo(
                        $"Strategic points restore: dropped base '{records[i].AirbaseName}' (no airbase of that name).");
                    continue;
                }
            }

            // Only the hot-reload path resolves a mine: its world is untouched, so the identifier
            // still names the building it named. A strategic save carries none, because a mission
            // restart re-issues every identifier from zero.
            if (attachMines && records[i].MinePersistentId != 0u)
            {
                PersistentID id = new() { Id = records[i].MinePersistentId };
                if (id.TryGetUnit(out Unit mine) && mine != null && !mine.disabled)
                {
                    point.Mine = mine;
                }
            }

            points.Add(point);
            if (StrategicPointKinds.IsControlPoint(point.Kind) && point.Hold.OwnerIndex >= 0)
            {
                held++;
            }
        }

        for (int i = 0; i < roads.Count; i++)
        {
            List<GlobalPosition> polyline = FromRecord(roads[i]);
            if (polyline.Count >= 2)
            {
                roadPointLists.Add(polyline);
            }
        }

        // The tail of StepBases, minus the parts that are already empty on a fresh load: discovery
        // is finished, and it counts as the attempt that produced this map so the no-sites retry
        // does not fire against a perfectly good restored set.
        discoveryAttempts = Mathf.Max(discoveryAttempts, 1);
        discovery = DiscoveryState.Done;

        CommanderPlugin.Log.LogInfo(
            $"Strategic points restored {what}: {points.Count} points, "
                + $"{roadPointLists.Count} road polylines, {held} held.");
    }

    /// <summary>The faction names behind the current <c>hqOrder</c> snapshot, in the same order —
    /// the bridge between a hold index (valid only within one run) and the faction name a snapshot
    /// stores instead.</summary>
    private List<string> CollectFactionNames()
    {
        List<string> names = new(hqOrder.Count);
        for (int i = 0; i < hqOrder.Count; i++)
        {
            names.Add(hqOrder[i]?.faction?.factionName ?? string.Empty);
        }

        return names;
    }

    /// <summary>The airbase whose label (<see cref="CommanderCaptureService.GetAirbaseLabel"/> —
    /// the same one discovery labels a base point with) matches, or null. Iterates the lookup
    /// rather than indexing it because the lookup's key is not the display name.</summary>
    private static Airbase? FindAirbaseByLabel(string label)
    {
        if (string.IsNullOrEmpty(label) || FactionRegistry.airbaseLookup == null)
        {
            return null;
        }

        foreach (KeyValuePair<string, Airbase> entry in FactionRegistry.airbaseLookup)
        {
            Airbase airbase = entry.Value;
            if (airbase != null && !airbase.disabled
                && string.Equals(CommanderCaptureService.GetAirbaseLabel(airbase), label, StringComparison.Ordinal))
            {
                return airbase;
            }
        }

        return null;
    }

    /// <summary>
    /// One point as saved: everything discovery decided about it, plus the owner as a faction NAME
    /// rather than the hold index, because an index is only meaningful within the run that set it.
    /// Pure — no live game object is read here, so the self-check can drive it.
    /// </summary>
    internal static CommanderStrategicPointRecord ToRecord(CommanderStrategicPoint point, IReadOnlyList<string> factionNames)
    {
        return new CommanderStrategicPointRecord
        {
            Kind = point.Kind.ToString(),
            X = point.Position.x,
            Y = point.Position.y,
            Z = point.Position.z,
            Radius = point.Radius,
            Label = point.Label,
            Wooded = point.Wooded,
            AirbaseName = point.Kind == StrategicPointKind.Base ? point.Label : string.Empty,
            // A base's owner is the airbase's own HQ; its hold state is never run and reads neutral,
            // which is why every captured base used to come back to its mission-authored owner.
            OwnerFaction = point.Kind == StrategicPointKind.Base
                ? BaseOwnerName(point)
                : FactionNameAt(factionNames, point.Hold.OwnerIndex),
            MinePersistentId = point.Mine != null && !point.Mine.disabled ? point.Mine.persistentID.Id : 0u,
        };
    }

    /// <summary>
    /// The inverse of <see cref="ToRecord(CommanderStrategicPoint, IReadOnlyList{string})"/>, minus
    /// the airbase and mine links, which need live objects and are attached by the caller. Null for
    /// a record whose kind no longer parses (a kind removed by a later mod version). The candidate
    /// and progress halves of the hold state are deliberately NOT restored: they are re-derived
    /// from the ring within one 5 s hold tick, so saving them would only risk handing a point over
    /// on stale presence.
    /// </summary>
    internal static CommanderStrategicPoint? FromRecord(CommanderStrategicPointRecord record, IReadOnlyList<string> factionNames)
    {
        if (!Enum.TryParse(record.Kind, out StrategicPointKind kind) || !Enum.IsDefined(typeof(StrategicPointKind), kind))
        {
            return null;
        }

        CommanderStrategicPoint point = new(kind, new GlobalPosition(record.X, record.Y, record.Z), record.Radius, record.Label)
        {
            Wooded = record.Wooded,
        };
        int owner = OwnerIndexByName(factionNames, record.OwnerFaction);
        if (kind == StrategicPointKind.Base)
        {
            point.SavedBaseOwnerIndex = owner;
            owner = -1;
        }

        point.Hold = new HoldState
        {
            OwnerIndex = owner,
            CandidateIndex = -1,
            Progress = 0f,
            Contested = false,
        };
        return point;
    }

    /// <summary>The faction name of the HQ holding a base point's airbase, or empty. Explicit
    /// null tests rather than <c>?.</c>, which would skip Unity's destroyed-object check.</summary>
    private static string BaseOwnerName(CommanderStrategicPoint point)
    {
        Airbase? airbase = point.Airbase;
        if (airbase == null)
        {
            return string.Empty;
        }

        FactionHQ? owner = airbase.CurrentHQ;
        if (owner == null || owner.faction == null)
        {
            return string.Empty;
        }

        return owner.faction.factionName ?? string.Empty;
    }

    /// <summary>The faction name at a hold index, or empty for neutral (-1) or an index the
    /// snapshot no longer covers. Pure, for the self-check.</summary>
    internal static string FactionNameAt(IReadOnlyList<string> factionNames, int index)
    {
        return index >= 0 && index < factionNames.Count ? factionNames[index] : string.Empty;
    }

    /// <summary>The hold index for a saved owner name, or -1 when it is empty (neutral) or names a
    /// faction that is not in this run's registry. Pure, for the self-check.</summary>
    internal static int OwnerIndexByName(IReadOnlyList<string> factionNames, string ownerName)
    {
        if (string.IsNullOrEmpty(ownerName))
        {
            return -1;
        }

        for (int i = 0; i < factionNames.Count; i++)
        {
            if (string.Equals(factionNames[i], ownerName, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>One retained road polyline flattened to x/y/z triples — a list of floats survives
    /// the JSON round trip without a record type per point.</summary>
    internal static CommanderRoadPolylineRecord ToRecord(List<GlobalPosition> polyline)
    {
        CommanderRoadPolylineRecord record = new();
        for (int i = 0; i < polyline.Count; i++)
        {
            record.Coordinates.Add(polyline[i].x);
            record.Coordinates.Add(polyline[i].y);
            record.Coordinates.Add(polyline[i].z);
        }

        return record;
    }

    /// <summary>The inverse of <see cref="ToRecord(List{GlobalPosition})"/>. A trailing partial
    /// triple (a truncated file) is ignored rather than read past the end.</summary>
    internal static List<GlobalPosition> FromRecord(CommanderRoadPolylineRecord record)
    {
        List<float> values = record.Coordinates;
        List<GlobalPosition> polyline = new(values.Count / 3);
        for (int i = 0; i + 2 < values.Count; i += 3)
        {
            polyline.Add(new GlobalPosition(values[i], values[i + 1], values[i + 2]));
        }

        return polyline;
    }

    /// <summary>
    /// The hot-reload round trip, with no live game objects involved: one point through
    /// <see cref="ToRecord(CommanderStrategicPoint, IReadOnlyList{string})"/> and back, one road
    /// polyline through its own pair, the owner-name lookup in both directions, and a restore from
    /// a snapshot with no points at all, which must leave discovery to run as it always did.
    /// </summary>
    private static void CheckPersistRoundTrip(List<string> failures)
    {
        List<string> factionNames = new() { "Primeva", "Boscali" };
        CommanderStrategicPoint original =
            new(StrategicPointKind.Hilltop, new GlobalPosition(1200.5f, 340f, -870.25f), 450f, "HILLTOP 3")
            {
                Wooded = true,
            };
        original.Hold = new HoldState { OwnerIndex = 1, CandidateIndex = 0, Progress = 12f, Contested = true };

        CommanderStrategicPointRecord record = ToRecord(original, factionNames);
        Expect(failures, "a saved point keeps its owner as a faction name", record.OwnerFaction, "Boscali");

        CommanderStrategicPoint? restored = FromRecord(record, factionNames);
        if (restored == null)
        {
            failures.Add("a point round trip: the restored point was null");
            return;
        }

        Expect(failures, "a point round trip keeps its kind", restored.Kind == StrategicPointKind.Hilltop, true);
        Expect(failures, "a point round trip keeps its easting", restored.Position.x, 1200.5f);
        Expect(failures, "a point round trip keeps its height", restored.Position.y, 340f);
        Expect(failures, "a point round trip keeps its northing", restored.Position.z, -870.25f);
        Expect(failures, "a point round trip keeps its radius", restored.Radius, 450f);
        Expect(failures, "a point round trip keeps its label", restored.Label, "HILLTOP 3");
        Expect(failures, "a point round trip keeps its wooded mark", restored.Wooded, true);
        Expect(failures, "a point round trip keeps its owner", restored.Hold.OwnerIndex, 1);
        Expect(failures, "a point round trip clears the capture progress", restored.Hold.Progress, 0f);

        CommanderStrategicPointRecord baseRecord = ToRecord(
            new CommanderStrategicPoint(StrategicPointKind.Base, new GlobalPosition(0f, 0f, 0f), 900f, "Ridgeline AB"),
            factionNames);
        Expect(failures, "a saved base keeps its airbase name", baseRecord.AirbaseName, "Ridgeline AB");

        CommanderStrategicPoint? restoredBase = FromRecord(
            new CommanderStrategicPointRecord
            {
                Kind = nameof(StrategicPointKind.Base),
                Label = "Ridgeline AB",
                AirbaseName = "Ridgeline AB",
                OwnerFaction = "Boscali",
            },
            factionNames);
        Expect(failures, "a restored base carries its saved owner for the force capture",
            restoredBase?.SavedBaseOwnerIndex ?? -2, 1);
        Expect(failures, "a restored base keeps a neutral hold, which income and reach read",
            restoredBase?.Hold.OwnerIndex ?? -2, -1);

        CommanderStrategicPointRecord unknownKind = new() { Kind = "Fortress" };
        Expect(failures, "a point of an unknown kind is dropped", FromRecord(unknownKind, factionNames) == null, true);

        List<GlobalPosition> road = new()
        {
            new GlobalPosition(0f, 10f, 0f),
            new GlobalPosition(500f, 12f, 250f),
            new GlobalPosition(1000f, 14f, 500f),
        };
        List<GlobalPosition> roadBack = FromRecord(ToRecord(road));
        Expect(failures, "a road polyline round trip keeps every point", roadBack.Count, 3);
        Expect(failures, "a road polyline round trip keeps the last easting", roadBack[2].x, 1000f);
        Expect(failures, "a road polyline round trip keeps the last northing", roadBack[2].z, 500f);

        CommanderRoadPolylineRecord truncated = new() { Coordinates = { 1f, 2f, 3f, 4f } };
        Expect(failures, "a truncated road polyline drops the partial point", FromRecord(truncated).Count, 1);

        Expect(failures, "a known owner name resolves to its index", OwnerIndexByName(factionNames, "Boscali"), 1);
        Expect(failures, "an unknown owner name resolves to neutral", OwnerIndexByName(factionNames, "Kolyma"), -1);
        Expect(failures, "an empty owner name resolves to neutral", OwnerIndexByName(factionNames, string.Empty), -1);
        Expect(failures, "a neutral owner index saves as an empty name", FactionNameAt(factionNames, -1), string.Empty);

        // A snapshot with no points must leave the service untouched so discovery still runs. The
        // singleton is put back afterwards because constructing a service claims it.
        CommanderStrategicPointService? previousInstance = Instance;
        CommanderStrategicPointService probe = new();
        probe.Restore(new CommanderStateReader(new CommanderStateSnapshot()));
        Expect(failures, "an empty snapshot restores no points", probe.Points.Count, 0);
        Expect(failures, "an empty snapshot leaves discovery still to run", probe.DiscoveryComplete, false);
        Instance = previousInstance;
    }
}

/// <summary>One discovered strategic point as saved across a hot reload. The kind is its enum name
/// and the owner is a faction name, both so a snapshot survives a change to the underlying
/// numbering; the airbase and the mine are named by the only ids that outlive a reload (the
/// airbase's display name, the mine's <c>PersistentID</c>).</summary>
internal sealed class CommanderStrategicPointRecord
{
    public string Kind { get; set; } = string.Empty;
    public float X { get; set; }
    public float Y { get; set; }
    public float Z { get; set; }
    public float Radius { get; set; }
    public string Label { get; set; } = string.Empty;
    public bool Wooded { get; set; }
    public string AirbaseName { get; set; } = string.Empty;
    public string OwnerFaction { get; set; } = string.Empty;
    public uint MinePersistentId { get; set; }
}

/// <summary>One retained road polyline, flattened to x/y/z triples.</summary>
internal sealed class CommanderRoadPolylineRecord
{
    public List<float> Coordinates { get; set; } = new();
}
