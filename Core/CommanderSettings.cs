using BepInEx.Configuration;
using System.Collections.Generic;
using UnityEngine;

namespace GroundControlRts;

internal static class CommanderSettings
{
    private static ConfigFile? config;
    private static readonly Dictionary<string, ConfigEntryBase> entries = new();

    // The one plain static here: derived from the screen size by CommanderUiScale, not from the
    // config file, so it is recomputed every launch and on every window resize.
    internal static float AutomaticUiScale { get; set; } = 1.5f;
    internal static float UiScale => CommanderUiScale.Resolve(UiScaleOverride, AutomaticUiScale);
    internal static bool ModEnabled { get => Get("General", "Enabled", true); set => Set("General", "Enabled", value); }
    // Off by default: this writes a JSON file to disk on every mission tick and is only worth the
    // cost while actively developing the mod, hot-reloading mid-match with build-dev.bat. See
    // CommanderStateStore for the write/read cycle and the session guard that keeps a stale file
    // from an earlier match from ever being replayed into a new one.
    // Key renamed from PersistStateAcrossReload when the default flipped to on: BepInEx keeps the
    // value already in the cfg, so a default change under the old key would never reach anyone.
    internal static bool PersistStateAcrossReload { get => Get("Developer", "KeepStateAcrossHotReload", true); set => Set("Developer", "KeepStateAcrossHotReload", value); }
    // On by default, and the key is renamed from LimitToFactoryVehicles because both the meaning
    // and the default changed: it now narrows the depot window to the FACTION ROSTER
    // (CommanderFactionRoster.MayFieldVehicle), the same question every computer commander is
    // asked, rather than to whatever the nearest factories happened to be producing. BepInEx
    // keeps whatever value is already in the cfg, so a default change under the old key would
    // never reach anyone who has run the mod before (the same reason MapDragSpeed was renamed).
    internal static bool LimitToFactionRoster { get => Get("Gameplay", "LimitToFactionRoster", true); set => Set("Gameplay", "LimitToFactionRoster", value); }
    internal static bool ShowCommandButton { get => Get("UI", "ShowCommandButton", true); set => Set("UI", "ShowCommandButton", value); }
    internal static bool ShowFactionMoney { get => Get("UI", "ShowFactionMoney", true); set => Set("UI", "ShowFactionMoney", value); }
    internal static bool ShowTacticalMap { get => Get("UI", "ShowTacticalMap", true); set => Set("UI", "ShowTacticalMap", value); }
    internal static bool ShowSelectionBar { get => Get("UI", "ShowSelectionBar", true); set => Set("UI", "ShowSelectionBar", value); }
    internal static bool ShowPinnedUnits { get => Get("UI", "ShowPinnedUnits", true); set => Set("UI", "ShowPinnedUnits", value); }
    internal static bool ShowUnitSystems { get => Get("UI", "ShowUnitSystems", true); set => Set("UI", "ShowUnitSystems", value); }
    internal static bool ShowDepotUi { get => Get("UI", "ShowDepotUi", true); set => Set("UI", "ShowDepotUi", value); }
    internal static bool ShowSupplyUi { get => Get("UI", "ShowSupplyUi", true); set => Set("UI", "ShowSupplyUi", value); }
    internal static bool ShowAirCommandUi { get => Get("UI", "ShowAirCommandUi", true); set => Set("UI", "ShowAirCommandUi", value); }
    internal static bool ShowNavalUi { get => Get("UI", "ShowNavalUi", true); set => Set("UI", "ShowNavalUi", value); }
    internal static bool ShowSamAnalyzerUi { get => Get("UI", "ShowSamAnalyzerUi", true); set => Set("UI", "ShowSamAnalyzerUi", value); }
    internal static bool ShowWorldMarkers { get => Get("UI", "ShowWorldMarkers", true); set => Set("UI", "ShowWorldMarkers", value); }
    internal static bool ShowUnitListUi { get => Get("UI", "ShowUnitListUi", true); set => Set("UI", "ShowUnitListUi", value); }
    internal static bool ShowBuildUi { get => Get("UI", "ShowBuildUi", true); set => Set("UI", "ShowBuildUi", value); }
    internal static float TacticalMapSize { get => Get("UI", "TacticalMapSize", 675f); set => Set("UI", "TacticalMapSize", value); }
    // Renamed from MapDragSensitivity because the meaning changed: the pan is now driven by real
    // cursor pixels against the map's on-screen scale, so 1.0 means the map sticks to the cursor.
    // A rename is the only way to reissue a default, since BepInEx keeps whatever is in the file.
    internal static float MapDragSpeed { get => Get("UI", "MapDragSpeed", 1f); set => Set("UI", "MapDragSpeed", value); }
    // 0 means automatic: the resolution preset in CommanderUiScale decides. Any positive value is
    // a manual multiplier the player set on the UI scale slider, and it wins over the preset so a
    // window resize can never undo a choice the player made by hand.
    internal static float UiScaleOverride { get => Get("UI", "UiScaleOverride", 0f); set => Set("UI", "UiScaleOverride", value); }
    internal static bool AutoFollowSelection { get => Get("Camera", "AutoFollowSelection", true); set => Set("Camera", "AutoFollowSelection", value); }
    // Selecting a unit attaches the follow but must not yank a camera the player just aimed, so
    // the camera only travels when the unit is off screen, near an edge, or too far to read.
    internal static bool AutoFrameSelection { get => Get("Camera", "AutoFrameOffscreenSelection", true); set => Set("Camera", "AutoFrameOffscreenSelection", value); }
    internal static float CameraPanSpeed { get => Get("Camera", "PanSpeed", 300f); set => Set("Camera", "PanSpeed", value); }
    internal static float CameraZoomSpeed { get => Get("Camera", "ZoomSpeed", 1f); set => Set("Camera", "ZoomSpeed", value); }
    // The RTS camera keeps its own look feel instead of borrowing PlayerSettings.viewSensitivity
    // and viewSmoothing, which are tuned for a pilot's head in a cockpit and read as lag here.
    internal static float CameraLookSensitivity { get => Get("Camera", "LookSensitivity", 1f); set => Set("Camera", "LookSensitivity", value); }
    internal static float CameraSmoothing { get => Get("Camera", "Smoothing", 0.05f); set => Set("Camera", "Smoothing", value); }
    internal static bool CameraHeightScaledSpeed { get => Get("Camera", "HeightScaledSpeed", true); set => Set("Camera", "HeightScaledSpeed", value); }
    internal static bool CameraEdgeScroll { get => Get("Camera", "EdgeScroll", false); set => Set("Camera", "EdgeScroll", value); }
    internal static bool CameraOrbitLook { get => Get("Camera", "OrbitLook", true); set => Set("Camera", "OrbitLook", value); }
    // Follow copies a smoothed anchor rather than the unit's exact per-frame movement, so an
    // aircraft's jitter does not become camera shake. Lead is off by default: it is a taste knob.
    internal static float FollowSmoothing { get => Get("Camera", "FollowSmoothing", 0.1f); set => Set("Camera", "FollowSmoothing", value); }
    internal static float FollowLeadSeconds { get => Get("Camera", "FollowLeadSeconds", 0f); set => Set("Camera", "FollowLeadSeconds", value); }
    internal static bool GroupHotkeys { get => Get("Gameplay", "GroupHotkeys", true); set => Set("Gameplay", "GroupHotkeys", value); }
    internal static bool CameraBookmarks { get => Get("Gameplay", "CameraBookmarks", true); set => Set("Gameplay", "CameraBookmarks", value); }
    internal static bool OrderFeedback { get => Get("Gameplay", "OrderFeedback", true); set => Set("Gameplay", "OrderFeedback", value); }
    internal static bool RetargetAfterKill { get => Get("Gameplay", "RetargetAfterKill", true); set => Set("Gameplay", "RetargetAfterKill", value); }
    internal static bool AttackMoveIntoRange { get => Get("Gameplay", "AttackMoveIntoRange", true); set => Set("Gameplay", "AttackMoveIntoRange", value); }
    internal static bool AttackMoveRoutes { get => Get("Gameplay", "AttackMoveRoutes", true); set => Set("Gameplay", "AttackMoveRoutes", value); }
    internal static bool GuardOrders { get => Get("Gameplay", "GuardOrders", true); set => Set("Gameplay", "GuardOrders", value); }
    internal static bool AutoRetreatDamaged { get => Get("Gameplay", "AutoRetreatDamaged", false); set => Set("Gameplay", "AutoRetreatDamaged", value); }
    internal static float RetreatConditionPercent { get => Get("Gameplay", "RetreatConditionPercent", 40f); set => Set("Gameplay", "RetreatConditionPercent", value); }
    internal static float WaypointHoldSeconds { get => Get("Gameplay", "WaypointHoldSeconds", 60f); set => Set("Gameplay", "WaypointHoldSeconds", value); }
    internal static int FormationShape { get => Get("Gameplay", "FormationShape", 0); set => Set("Gameplay", "FormationShape", value); }
    internal static float FormationCohesionMeters { get => Get("Gameplay", "FormationCohesionMeters", 300f); set => Set("Gameplay", "FormationCohesionMeters", value); }
    internal static bool CombatAlerts { get => Get("Gameplay", "CombatAlerts", true); set => Set("Gameplay", "CombatAlerts", value); }
    internal static int EnemyCommanderMode { get => Get("Gameplay", "EnemyCommanderMode", 0); set => Set("Gameplay", "EnemyCommanderMode", value); }

    /// <summary>The difficulty knob for the computer opposition, exposed as the GAMEPLAY tab's
    /// "Opposition money" slider. It multiplies two things and only two: every income an opposing
    /// faction earns (points, bases and mines), and the balance every opposing faction opens the
    /// match on. 1.0 is a fair fight and is exactly the behaviour the mod had before the slider
    /// existed; 1.3 hands the opposition a third more money a minute and a third more to start
    /// with. The handicap knob for a map or a player that leaves the opposition losing every match
    /// (user, 2026-09-15: "BDF are winning too handsomely and its not because of my actions - need
    /// to buff the PALA somehow"; slider added on the user's instruction 2026-09-16). Never applied
    /// to the player's own faction, whoever commands it. Moving it mid-match changes income from
    /// the next payout on and never re-opens a balance that has already been opened
    /// (<c>CommanderEnemyCommanderService.ShouldOpenTreasury</c>).</summary>
    internal static float EnemyIncomeMultiplier { get => Get("Gameplay", "EnemyIncomeMultiplier", 1f); set => Set("Gameplay", "EnemyIncomeMultiplier", value); }
    // Off by default: the same commander AI that runs the enemy also runs your own faction, which
    // is a different game from the one the player opened the mission expecting. You keep command
    // while it is on - see CommanderPlayerCommanderService.
    internal static bool PlayerCommanderEnabled { get => Get("Gameplay", "PlayerCommanderEnabled", false); set => Set("Gameplay", "PlayerCommanderEnabled", value); }
    // How long the player commander keeps its hands off a unit after the player gives it an order,
    // in game minutes. "Until it arrives" was not enough: a platoon parked on a hill by hand was
    // being re-recruited into the home guard the moment it stopped moving.
    internal static float PlayerCommanderHandsOffMinutes { get => Get("Gameplay", "PlayerCommanderHandsOffMinutes", 10f); set => Set("Gameplay", "PlayerCommanderHandsOffMinutes", value); }
    // Economy prices are mission-relative: faction balances are authored per mission (about 1000
    // at the start of Escalation), so these are knobs, not constants.
    internal static float GoldMineCost { get => Get("Economy", "GoldMineCost", 250f); set => Set("Economy", "GoldMineCost", value); }
    internal static float GoldMineIncomePerMinute { get => Get("Economy", "GoldMineIncomePerMinute", 20f); set => Set("Economy", "GoldMineIncomePerMinute", value); }
    internal static float FactoryUpgradeCost { get => Get("Economy", "FactoryUpgradeCost", 300f); set => Set("Economy", "FactoryUpgradeCost", value); }
    internal static float FactoryBuildCost { get => Get("Economy", "FactoryBuildCost", 500f); set => Set("Economy", "FactoryBuildCost", value); }
    // Factories off by default (user, 2026-09-14: "I'd actually quite like factories disabled"):
    // with the order book and depot reach deciding what is bought, a factory's free stream of
    // vehicles only fills the idle pool. Off means no commander builds one and no factory — the
    // player's included — produces; a factory standing on the map stays as scenery.
    internal static bool FactoriesEnabled { get => Get("Economy", "FactoriesEnabled", false); set => Set("Economy", "FactoriesEnabled", value); }
    internal static float FactoryProductionSeconds { get => Get("Economy", "FactoryProductionSeconds", 240f); set => Set("Economy", "FactoryProductionSeconds", value); }
    // A catalogue building is priced off its own encyclopedia value, so a radar costs what a
    // radar is worth without the mod carrying a price table that a game patch would invalidate.
    internal static float BuildingCostMultiplier { get => Get("Economy", "BuildingCostMultiplier", 1f); set => Set("Economy", "BuildingCostMultiplier", value); }
    internal static float RepairCrewCost { get => Get("Economy", "RepairCrewCost", 150f); set => Set("Economy", "RepairCrewCost", value); }
    // Both commanders may only build within this distance of an airbase their faction holds.
    internal static float BuildRadiusKm { get => Get("Economy", "BuildRadiusKm", 2.5f); set => Set("Economy", "BuildRadiusKm", value); }
    // A naval dock has to reach the coast, which is usually further out than the base perimeter,
    // so it gets its own (larger) radius instead of loosening the rule for every building. 12 km
    // because 7 was not enough on the duel map: the nearest usable shoreline to a duel base is
    // further out than the stock missions' sea-level objects suggested, so the dock could not be
    // placed at all while standing on the beach.
    internal static float NavalDockRadiusKm { get => Get("Economy", "NavalDockRadiusKm", 12f); set => Set("Economy", "NavalDockRadiusKm", value); }
    // How far from the water's edge a dock may sit. A shoreline is a band, not a line.
    internal static float NavalDockShoreMeters { get => Get("Economy", "NavalDockShoreMeters", 90f); set => Set("Economy", "NavalDockShoreMeters", value); }
    internal static float NavalDockCost { get => Get("Economy", "NavalDockCost", 400f); set => Set("Economy", "NavalDockCost", value); }
    internal static float NavalDockUpgradeCost { get => Get("Economy", "NavalDockUpgradeCost", 450f); set => Set("Economy", "NavalDockUpgradeCost", value); }
    // What one aircraft parked inside a capture ring is worth. Aircraft carry no capture strength
    // of their own unless they are holding a troop pod, so this is the mod granting it - roughly a
    // light vehicle's worth, so a base still wants a few airframes or a ground squad.
    internal static float AircraftCaptureStrength { get => Get("Gameplay", "AircraftCaptureStrength", 2f); set => Set("Gameplay", "AircraftCaptureStrength", value); }
    internal static int SamScanQueriesPerFrame { get => Get("SAM Analyzer", "RaycastsPerFrame", 64); set => Set("SAM Analyzer", "RaycastsPerFrame", value); }

    // Discovery spacing (config-file-only: retuning these is a map-authoring decision, not a
    // player taste knob, so there is no slider for them).
    // One generated resource site per cell this large that has no existing industrial building in
    // it, so a map with no industry at all still gets an even spread instead of nothing at all.
    internal static float PointsFillGridMeters { get => Get("Points", "FillGridMeters", 8000f); set => Set("Points", "FillGridMeters", value); }
    // Two resource sites (existing or generated) never sit closer than this; the later one in
    // discovery order is dropped so the earlier (existing-building) site always wins a conflict.
    internal static float PointsSiteMinSpacingMeters { get => Get("Points", "SiteMinSpacingMeters", 2000f); set => Set("Points", "SiteMinSpacingMeters", value); }
    // Civilian buildings within this of another cluster member chain into the same village.
    internal static float PointsVillageClusterMeters { get => Get("Points", "VillageClusterMeters", 400f); set => Set("Points", "VillageClusterMeters", value); }
    // A cluster smaller than this is a farmstead, not a village worth fighting over.
    internal static int PointsVillageMinBuildings { get => Get("Points", "VillageMinBuildings", 3); set => Set("Points", "VillageMinBuildings", value); }
    // Control ring radius around a village's building centroid.
    internal static float PointsVillageRadiusMeters { get => Get("Points", "VillageRadiusMeters", 400f); set => Set("Points", "VillageRadiusMeters", value); }
    // Height-map sample spacing for the hilltop scan; finer than this buys little (the strategic
    // height map itself is 20 m/px) and costs more per-frame samples.
    internal static float PointsHilltopGridMeters { get => Get("Points", "HilltopGridMeters", 1000f); set => Set("Points", "HilltopGridMeters", value); }
    // A hilltop sample must be the highest point within this ring to count as a local high point.
    internal static float PointsHilltopRingMeters { get => Get("Points", "HilltopRingMeters", 1500f); set => Set("Points", "HilltopRingMeters", value); }
    // …and at least this far above the ring's mean height, or every gentle rise on a map would
    // qualify as a hilltop. Lowered from 15 to 8 (2026-09-13, alongside outposts/crossroads/roadside
    // points): hilltops were already the rarest kind of point, and adding three more kinds to the
    // same spacing budget only made a high threshold worse.
    internal static float PointsHilltopProminenceMeters { get => Get("Points", "HilltopMinProminenceMeters", 8f); set => Set("Points", "HilltopMinProminenceMeters", value); }
    // Control ring radius around a hilltop.
    internal static float PointsHilltopRadiusMeters { get => Get("Points", "HilltopRadiusMeters", 300f); set => Set("Points", "HilltopRadiusMeters", value); }
    // No hilltop within this of a village: the village is already the point of interest there.
    internal static float PointsHilltopVillageExclusionMeters { get => Get("Points", "HilltopVillageExclusionMeters", 1000f); set => Set("Points", "HilltopVillageExclusionMeters", value); }
    // Control ring radius around an outpost — a civilian cluster too small to be a village.
    internal static float PointsOutpostRadiusMeters { get => Get("Points", "OutpostRadiusMeters", 300f); set => Set("Points", "OutpostRadiusMeters", value); }
    // Control ring radius around a road-network junction.
    internal static float PointsCrossroadsRadiusMeters { get => Get("Points", "CrossroadsRadiusMeters", 300f); set => Set("Points", "CrossroadsRadiusMeters", value); }
    // Control ring radius around a roadside point — smaller than the others: it is a waypoint on an
    // otherwise empty stretch of road, not a place with much to stand around in.
    internal static float PointsRoadsideRadiusMeters { get => Get("Points", "RoadsideRadiusMeters", 250f); set => Set("Points", "RoadsideRadiusMeters", value); }
    // Distance along a road between generated roadside points. Long enough that a road already
    // carrying a village, hilltop or crossroads every few kilometres does not also collect a
    // roadside point on top of them.
    internal static float PointsRoadsideSpacingMeters { get => Get("Points", "RoadsideSpacingMeters", 6000f); set => Set("Points", "RoadsideSpacingMeters", value); }
    // A road-network junction node needs at least this many roads meeting (or passing through) it
    // to be worth calling a crossroads rather than an ordinary bend or a dead end.
    internal static int PointsCrossroadsMinRoads { get => Get("Points", "CrossroadsMinRoads", 3); set => Set("Points", "CrossroadsMinRoads", value); }
    // Retuned and renamed for the 48-point cap (reach-and-points, 2026-09-14): at 24 crossroads
    // and a 30-slot road-point reserve the old ceilings filled all 48 slots before the hilltop
    // stage ran, and the first restart produced a map with no hilltops at all. 12 + 12 + 8 leaves
    // the hilltops at least 16 slots on a full map. Keys renamed so the new defaults take effect.
    // Per-kind ceilings inside MaxControlPoints. Without them the first stage ate the whole
    // allowance: the duel map has 263 road junctions, so crossroads took 44 slots, hilltops got 15
    // and road points none. Hilltops take whatever these leave; a kind with few candidates on a map
    // simply hands its share on.
    internal static int PointsMaxCrossroads { get => Get("Points", "CrossroadsCap", 12); set => Set("Points", "CrossroadsCap", value); }
    internal static int PointsMaxOutposts { get => Get("Points", "OutpostCap", 8); set => Set("Points", "OutpostCap", value); }
    internal static int PointsMaxRoadPoints { get => Get("Points", "RoadPointCap", 12); set => Set("Points", "RoadPointCap", value); }
    // Junctions are dense wherever roads are, so crossroads keep a wider spacing than other control
    // points or every hamlet's T-junction becomes one; 2.5 km reads as "the next crossroads along".
    internal static float PointsCrossroadsSpacingMeters { get => Get("Points", "CrossroadsSpacingMeters", 2500f); set => Set("Points", "CrossroadsSpacingMeters", value); }
    // Road endpoints (or a road segment passing near another road's endpoint) within this of each
    // other merge into the same junction node — wide enough that a junction authored as two
    // close-together forks in the road data still merges into one crossroads candidate.
    internal static float PointsRoadJunctionMergeMeters { get => Get("Points", "RoadJunctionMergeMeters", 60f); set => Set("Points", "RoadJunctionMergeMeters", value); }
    // Levelness of the ground under a point (2026-09-14). Every non-road point — resource site,
    // village, hilltop, outpost — is probed on a ring this wide before it is accepted; roads and
    // crossroads are exempt, because a road goes where the level authored it, up a hillside
    // included. 60 m is the footprint the things that stand on a point actually need: a mine
    // building plus the two vehicles of a minimum garrison parked around it.
    internal static float PointsLevelnessProbeRadiusMeters { get => Get("Points", "LevelnessProbeRadiusMeters", 60f); set => Set("Points", "LevelnessProbeRadiusMeters", value); }
    // Highest minus lowest terrain height across that ring (and its centre). 6 m over a 120 m span
    // is the most a mine foundation and a picket ring can straddle and still read as one level
    // patch of ground; past it the point sits on a slope, and the vehicles sent to hold it slide
    // off it or lose line of sight to half their own ring.
    internal static float PointsMaxPointHeightSpreadMeters { get => Get("Points", "MaxPointHeightSpreadMeters", 6f); set => Set("Points", "MaxPointHeightSpreadMeters", value); }
    // …and the mean of the slopes from the centre out to each probe. The spread rule alone accepts
    // a consistent 6 m tilt across a narrow ring; 8 degrees is roughly the steepest ground a ground
    // vehicle holds station on without creeping downhill.
    internal static float PointsMaxPointSlopeDegrees { get => Get("Points", "MaxPointSlopeDegrees", 8f); set => Set("Points", "MaxPointSlopeDegrees", value); }
    // Cap on control points (villages, hilltops, outposts, crossroads, roadside points) per map, so
    // a huge map does not drown the tactical map (or the AI's garrison review) in markers. Resource
    // sites have their own cap below: one shared cap let 30 sites use up the whole allowance and
    // every control point was dropped. Raised from 60 to 120 (2026-09-13): three more kinds now
    // share the same allowance, and 60 left the lowest-priority stage (roadside points) with
    // nothing to spend.
    // Key renamed from MaxNonBasePoints (and ControlPointSpacingMeters, HilltopMinProminenceMeters
    // likewise) on 2026-09-13 so the new defaults reach existing installs: BepInEx keeps whatever
    // value is already in the file, and a hot reload was re-saving the old numbers over hand edits.
    // Cut from 120 to 48 and renamed again to ControlPointCap (reach-and-points, user decision
    // 2026-09-14, "reduce number of control points but raise their funding impact"): a 2026-09-14
    // match discovered 148 points and the player side ended it holding 22 forward bases, 43 pickets
    // and 36 platoons, most of them sitting on ground no enemy would ever come near. Forty-eight is
    // about one point per two kilometres of front on the duel map, which is as many as one
    // commander's ground force can actually garrison. The income rates below are tripled to match,
    // so a full map pays about what it paid before.
    internal static int PointsMaxNonBasePoints { get => Get("Points", "ControlPointCap", 48); set => Set("Points", "ControlPointCap", value); }
    internal static int PointsMaxResourceSites { get => Get("Points", "MaxResourceSites", 30); set => Set("Points", "MaxResourceSites", value); }
    // No two non-base points closer than this; the earlier one in discovery order wins. Lowered
    // from 1500 to 800 (2026-09-13, alongside outposts/crossroads/roadside points): the old spacing
    // was tuned for a map with only villages and hilltops on it, and left flat farmland almost as
    // empty as before once three more kinds were competing for the same allowance.
    // Raised to 3000 and renamed to ControlPointMinSpacingMeters (reach-and-points, user decision
    // 2026-09-14): at 800 m the cut-down cap above would have spent its whole allowance inside the
    // first few valleys discovery walked. Three kilometres spreads forty-eight points over a whole
    // 82 km map, which is what makes each one worth a platoon's drive. The key changed with the
    // default because BepInEx keeps the value already saved in the config file.
    internal static float PointsPointMinSpacingMeters { get => Get("Points", "ControlPointMinSpacingMeters", 3000f); set => Set("Points", "ControlPointMinSpacingMeters", value); }

    // How far a control point may stand from the nearest road and still be worth having: 2 km (user
    // decision 2026-09-14, "limit to within some km of a road"). A point farther than this from any
    // road cannot be reached by a road picket or a driving platoon at all, so it is dead weight on
    // the map and in the commander's review — the only way to staff it would be a helicopter, and
    // a commander that spends flights on unreachable ground has none left for the fighting. Applied
    // at discovery, alongside the woodland test, before the cap picks among what is left.
    internal static float PointsMaxRoadDistanceMeters { get => Get("Points", "PointMaxRoadDistanceMeters", 2000f); set => Set("Points", "PointMaxRoadDistanceMeters", value); }
    // No non-base point within this of an airbase centre — a base is already worth holding on its
    // own and a point crowding it would be redundant and hard to read on the map.
    internal static float PointsAirbaseExclusionMeters { get => Get("Points", "AirbaseExclusionMeters", 2000f); set => Set("Points", "AirbaseExclusionMeters", value); }

    // Garrison, hold and income (POINTS settings tab).
    /// <summary>
    /// The standing garrison on a held control point, and — the same number, deliberately — the
    /// ground vehicles a single faction needs inside the ring, alone, to count as present at all.
    /// One since unit-economy_20260918 §2.2: around forty picket points each held several vehicles,
    /// and ground vehicles were two-thirds of the growth that took a match from 8.7 ms a frame to
    /// 120. A point needs something standing on it, not a crowd; the cost of one is that a point is
    /// easier to take, which the user accepted.
    /// <para>
    /// ONE number for both jobs, and this is load-bearing rather than tidy. Ownership is re-derived
    /// every five seconds from what stands in the ring, so a garrison target BELOW the ownership
    /// threshold would station too few vehicles to hold the ground it was sent to and hand every
    /// point away. Keeping the fill rule and the ownership rule on one setting makes that
    /// impossible by construction, and is what lets the quiet-ground retirement prove it can never
    /// empty a held point (<c>Operations/CommanderOperationsUnitEconomy.cs</c>,
    /// <c>HeldPointKeepFloor</c>).
    /// </para>
    /// Key renamed from MinGarrison so the new default takes: BepInEx keeps whatever is already in
    /// the player's cfg, the same reason MapDragSpeed was renamed.
    /// </summary>
    internal static int PointsMinGarrison { get => Get("Points", "GarrisonPerPoint", 1); set => Set("Points", "GarrisonPerPoint", value); }
    // Conservation of value, on by default (user decision 2026-09-17). A strategic save banks the
    // cash value of everything that was standing, so the garrisons the load places on held points
    // must be bought out of that same war chest or every save prints money. Off is a deliberate
    // cheat for testing: the garrisons appear free and the chest is untouched. See
    // CommanderOperationsStrategicPersist.
    internal static bool StrategicGarrisonPaid { get => Get("Developer", "StrategicGarrisonPaid", true); set => Set("Developer", "StrategicGarrisonPaid", value); }
    // One "Health ..." line into the BepInEx log every 30 s carrying frame time, live unit counts,
    // the game's own contact and strategic-target tables, the mod's own largest tables and managed
    // memory (Core/CommanderHealthDiagnostics.cs). OFF by default: it exists to settle the
    // long-match frame-rate question by comparing one early line with one late one
    // (conductor/designs/2026-09-17-frame-rate-investigation.md), not to run for ever. While it is
    // off the service does no per-frame work at all.
    internal static bool HealthDiagnosticLine { get => Get("Developer", "HealthDiagnosticLine", false); set => Set("Developer", "HealthDiagnosticLine", value); }
    // Cumulative seconds a faction must hold a point alone with at least MinGarrison before it
    // flips; short enough to reward a fast platoon, long enough that a driving-through raid does
    // not flip it by accident.
    internal static float PointsHoldSeconds { get => Get("Points", "HoldSeconds", 60f); set => Set("Points", "HoldSeconds", value); }
    // The six income rates below were all tripled on 2026-09-14 (reach-and-points, user decision:
    // "reduce number of control points but raise their funding impact") because the control-point
    // cap above was cut from 120 to 48 at the same time. A map with roughly two fifths as many
    // points paying three times as much per point pays a commander about what it used to, so the
    // retune is a change to what each point is WORTH, not to how rich the match is. Every key
    // gained the shared Income prefix at the same time: BepInEx keeps whatever number is already in
    // a player's config file, so the only way a changed default reaches an existing install is a
    // changed key.
    // Per airbase held, paid on the shared 15 s income tick.
    internal static float PointsBaseIncomePerMinute { get => Get("Points", "IncomeBasePerMinute", 90f); set => Set("Points", "IncomeBasePerMinute", value); }
    // Per village held. Below a base's rate: a village is worth less than the airbase that lets you
    // build there, but still worth a platoon's time.
    internal static float PointsVillageIncomePerMinute { get => Get("Points", "IncomeVillagePerMinute", 30f); set => Set("Points", "IncomeVillagePerMinute", value); }
    // Per hilltop held; lowest of the three because a hilltop has no buildings to defend, only the
    // ground itself.
    internal static float PointsHilltopIncomePerMinute { get => Get("Points", "IncomeHilltopPerMinute", 15f); set => Set("Points", "IncomeHilltopPerMinute", value); }
    // Per outpost held; same rate as a hilltop — a cluster too small to be a village has no more to
    // defend than open ground does.
    internal static float PointsOutpostIncomePerMinute { get => Get("Points", "IncomeOutpostPerMinute", 15f); set => Set("Points", "IncomeOutpostPerMinute", value); }
    // Per crossroads held; same rate as a village — a junction is worth fighting over on its own,
    // not merely as a shortcut through it.
    internal static float PointsCrossroadsIncomePerMinute { get => Get("Points", "IncomeCrossroadsPerMinute", 30f); set => Set("Points", "IncomeCrossroadsPerMinute", value); }
    // Per roadside point held; the lowest rate of the six — a generated waypoint with nothing else
    // to recommend it, there only so an empty stretch of road is not empty of anything to fight over.
    internal static float PointsRoadsideIncomePerMinute { get => Get("Points", "IncomeRoadsidePerMinute", 9f); set => Set("Points", "IncomeRoadsidePerMinute", value); }
    // How far the mine-placement ghost snaps to the nearest free resource site. Design (§2) does
    // not give a number for this; without one the ghost could jump to a site many kilometres away.
    // 1 km keeps the snap feeling local while still forgiving imprecise clicking near a site.
    internal static float PointsMineSnapMeters { get => Get("Points", "MineSnapMeters", 1000f); set => Set("Points", "MineSnapMeters", value); }

    // Platoon recipe (config-file-only: a map-authoring / balance decision, not a player taste
    // knob, so there is no slider — see Operations/CommanderPlatoon.cs "Which vehicle is which
    // role"). The three slot counts must add up to PlatoonSize; the operations self-check says so
    // at load if a hand edit breaks that.
    // One BepInEx log line per commander per 30 s review describing the whole operations state
    // (pool, every platoon and its state, every mission, front/rear counts, pressure, order book),
    // plus a line per platoon state change and per depot claim. On while the doctrine is being
    // tuned so a match can be debugged from LogOutput.log alone; the COMMANDER LOG window shows
    // decisions, not this machinery.
    internal static bool OperationsDebugLog { get => Get("Operations", "DebugLog", true); set => Set("Operations", "DebugLog", value); }
    // Full platoons a commander keeps wanting with no job assigned. This is what keeps the order
    // book open (and the buyer forming platoons) before the first mission exists. One since the
    // pickets-first doctrine (2026-09-14): a platoon now forms only for a forward base on a front
    // point, an attack, or this reserve, so the reserve is the whole of the commander's spare
    // ground force rather than a floor under a force that grew on its own. Two kept a second full
    // platoon parked at the base that the picket line would rather have had as six vehicles.
    // Key renamed from ReservePlatoons when the default dropped 2 -> 1 (pickets-first, 2026-09-14):
    // BepInEx keeps the value already in the cfg, so a default change under the old key never lands.
    internal static int OperationsReservePlatoons { get => Get("Operations", "ReservePlatoonCount", 1); set => Set("Operations", "ReservePlatoonCount", value); }
    // Operations/MaxPlatoons was deleted with the pickets-first doctrine (DECISION-013). The cap it
    // held never bound — any open requisition lifted it and the order book never emptied — and what
    // replaced it is GroundBuyingBookOnly: a commander buys ground vehicles only for lines on its
    // order book. The key is left orphaned in existing config files on purpose; nothing reads it.
    internal static int OperationsPlatoonSize { get => Get("Operations", "PlatoonSize", 6); set => Set("Operations", "PlatoonSize", value); }
    internal static int OperationsRecipeArmour { get => Get("Operations", "RecipeArmour", 3); set => Set("Operations", "RecipeArmour", value); }
    internal static int OperationsRecipeCarrier { get => Get("Operations", "RecipeCarrier", 1); set => Set("Operations", "RecipeCarrier", value); }
    internal static int OperationsRecipeAirDefence { get => Get("Operations", "RecipeAirDefence", 2); set => Set("Operations", "RecipeAirDefence", value); }

    // Front line, forward bases and the pressure clock (POINTS tab, OPERATIONS box).
    // A point within this of the nearest enemy-held point or base is front line; equal to the home
    // guard's ThreatRadiusMeters on purpose (Ai/CommanderEnemyCommanderDefence.cs:60) — the same
    // "it can see it" range.
    internal static float OperationsFrontRangeMeters { get => Get("Operations", "FrontRangeMeters", 15000f); set => Set("Operations", "FrontRangeMeters", value); }
    // At most this share of a commander's platoons sit in forward bases; the rest are reserve or
    // offensive. Guards against a commander that only ever garrisons.
    internal static float OperationsFobShare { get => Get("Operations", "FobShare", 0.5f); set => Set("Operations", "FobShare", value); }

    /// <summary>
    /// Fewest attacks a commander runs at once (concurrent-attacks_20260918). Two, because ONE was
    /// what the match of 2026-09-18 ran and it produced a mission board of 48 pickets, 11 forward
    /// bases and a single attack — a static picket line with one push crawling across it, one of ten
    /// platoons attacking, and a wing with nothing to support flying 68% fighters.
    /// <para>Zero or less means one attack: the behaviour before that track, not no attacks at all.
    /// A commander that never attacks is not a commander.</para>
    /// </summary>
    internal static int MaxAttacks { get => Get("Operations", "MaxAttacks", 2); set => Set("Operations", "MaxAttacks", value); }

    /// <summary>
    /// How many more attacks a commander earns per platoon it fields (concurrent-attacks_20260918).
    /// 0.2 is one more per five platoons, so the ten-platoon commanders of the measured match get
    /// two and a twenty-platoon one gets four. Clamped to 0..1 by the rule that reads it, so a
    /// mis-typed value thins the attacks rather than multiplying the army.
    /// </summary>
    internal static float AttacksPerPlatoon { get => Get("Operations", "AttacksPerPlatoon", 0.2f); set => Set("Operations", "AttacksPerPlatoon", value); }
    // Minutes of pressure before the commander attacks with whatever it has. Guards against a
    // commander that never attacks.
    internal static float OperationsPressureIntervalMinutes { get => Get("Operations", "PressureIntervalMinutes", 12f); set => Set("Operations", "PressureIntervalMinutes", value); }
    // How many platoons under way may hold a PRE-EMPTIVE air sortie at once, the ones nearest the
    // enemy first. Pre-emptive cover opens one sortie per marching platoon, so a ten-platoon front
    // asked for more air in one review than a whole match's income could buy: the 2026-09-14
    // `Ground Control Duel Far` log shows CAS demand running to 15 sorties and CAP to 28 against a
    // wing that could afford one or two airframes a review, so every objective got a fraction of an
    // aeroplane and none got cover. Contact and attack sorties are never capped by this.
    // Bound as a float on both sides (fix, 2026-09-15): the setter used to bind the same key as an
    // int, and the cast of the float entry threw InvalidCastException on every frame the settings
    // window was open — 17,030 exceptions and 16,000 unbalanced-GUI errors in one night's Player.log.
    internal static int OperationsMaxPreemptiveAirObjectives { get => (int)Get("Operations", "MaxPreemptiveAirObjectives", 4f); set => Set("Operations", "MaxPreemptiveAirObjectives", (float)value); }
    // Share of the pot the buy review spends while an attack requisition is open, in place of the
    // 0.25 / 0.45 tempo knob the buyer normally uses.
    internal static float OperationsOffensiveSpendFraction { get => Get("Operations", "OffensiveSpendFraction", 0.5f); set => Set("Operations", "OffensiveSpendFraction", value); }
    // Minutes after losing an airframe over an objective before the commander buys another for that
    // same objective (doubled while the objective's ring shows at least two tracked hostile
    // air-defence units). Stops the commander feeding CAS one airframe at a time into a SAM line.
    internal static float CasLossCooldownMinutes { get => Get("Operations", "CasLossCooldownMinutes", 2f); set => Set("Operations", "CasLossCooldownMinutes", value); }
    // Aircraft a commander's faction may have in the world at once, on every mission: the bought
    // wing (CAP and CAS), the wing's transports, the picket-insertion helicopters and anything the
    // player or a stock mission put up — it counts every faction aircraft, so insertion flights and
    // the player's own AIR-window launches eat into it too. Was the duel-only DuelAirborneLimit
    // (8); raised to 12 on 2026-09-13 because 8 was the binding limiter in play (the commander sat
    // at it with money in hand), and a CAP-first wing — one fighter per active objective plus CAS —
    // does not fit under eight once three objectives are live.
    // 20 (user decision 2026-09-13): a safety stop, not the limiter — the air fund, hull prices and
    // rearm cycles are what should size the wing. Counts every live aircraft of the faction.
    // Floor raised 20 -> 30 and key renamed (user, 2026-09-14: "raise that ceiling to 30+"); the
    // income scaling above it is unchanged, so a rich commander still climbs toward AirborneCeilingMax.
    // Floor lowered 30 -> 16 and the key renamed again (air-ceiling_20260918 §4 decision A). The 30
    // was set on the belief that aircraft were cheap, and the frame-rate investigation of 2026-09-17
    // shared it: it measured 5 to 7 live aircraft and wrote the whole air side out of scope on that
    // basis. Measurement on 2026-09-18 killed the belief — the health line read 66 live aircraft with
    // BOTH commanders sitting on this floor, not on the income scaling and not on the maximum below,
    // so this number and not the money was what sized the wing. Sixteen, with the maximum at 24,
    // puts the map near 36. The rename is what makes BepInEx deliver the new default to a config
    // file that already carries the old one.
    internal static int AirborneCeiling { get => Get("Operations", "AirFloorPerCommander", 16); set => Set("Operations", "AirFloorPerCommander", value); }
    // "Economy limited, not a hard cap" (user, 2026-09-13 and 2026-09-14): the ceiling above is the
    // FLOOR of what a commander may keep airborne; every AirborneIncomePerAirframe of income per
    // minute allows one more, up to AirborneCeilingMax. Fifteen per minute per airframe puts a
    // 460/min commander at 30 aircraft and a 130/min one at the floor. Sixty is the point past which
    // the scheduler and the strips, not the money, are what limit a wing.
    internal static float AirborneIncomePerAirframe { get => Get("Operations", "AirborneIncomePerAirframe", 15f); set => Set("Operations", "AirborneIncomePerAirframe", value); }
    // 60 -> 24 with the floor above, and renamed for the same reason. Sixty was "the point past which
    // the scheduler and the strips, not the money, are what limit a wing" — true, and irrelevant once
    // the thing being limited is frame cost rather than the wing's own plumbing.
    internal static int AirborneCeilingMax { get => Get("Operations", "AirCeilingMax", 24); set => Set("Operations", "AirCeilingMax", value); }
    // Frame-rate guards (user report 2026-09-14: "frame-rate has slowly decayed"; the enemy pool
    // held 238 idle vehicles and the ground was littered with pilots waiting for rescue).
    // PoolIdleCap: the game's own depot loop turns factory supply into vehicles whether or not
    // anything wants them; past this many idle vehicles in a commander's pool the loop is held and
    // the supply banks at the depot instead. Twelve is two platoons' worth of instant replacements.
    internal static int PoolIdleCap { get => Get("Operations", "PoolIdleCap", 12); set => Set("Operations", "PoolIdleCap", value); }
    /// <summary>
    /// The idle-reserve sale (user, 2026-09-14: "heaps of idle units around the airbase — we need a
    /// periodic task that either re-assigns them, re-tasks them, or sells them", and
    /// unit-economy_20260918 §2.1). A vehicle assigned to no platoon, no picket and no order for
    /// this long is despawned and <see cref="PoolSellRefundFraction"/> of its price refunded. Three
    /// minutes is six reviews of the picket fill, platoon formation and reinforcement passes all
    /// declining to take it, which is long enough to be sure nothing on the map wants it and short
    /// enough that a match's idle reserve never reaches the forty-five vehicles measured on
    /// 2026-09-17.
    /// <para>
    /// The pool CAP no longer gates this sale: the cap said "keep twelve idle vehicles whatever
    /// happens", and those twelve cost frame time in the game's pairwise unit work exactly as the
    /// thirteenth does. <see cref="PoolIdleCap"/> keeps its other job, holding the game's own depot
    /// deployment loop and the buyer while the pool is full.
    /// </para>
    /// Key renamed from PoolIdleSellAfterMinutes so the new default takes.
    /// </summary>
    internal static float IdleReserveMinutes { get => Get("Operations", "IdleReserveMinutes", 3f); set => Set("Operations", "IdleReserveMinutes", value); }
    internal static float PoolSellRefundFraction { get => Get("Operations", "PoolSellRefundFraction", 0.5f); set => Set("Operations", "PoolSellRefundFraction", value); }

    /// <summary>
    /// The live ground-vehicle ceiling one faction may field (unit-economy_20260918 §2.3). At or
    /// above it the commander replaces losses but never grows: the buy gate is simply "live ground
    /// vehicles below the ceiling", so a vehicle lost opens exactly one purchase. Eighty per faction
    /// lands both sides near 160 rather than the 214 measured at the worst point on 2026-09-17,
    /// which by the measured square law is roughly half the frame cost. Zero or less switches the
    /// ceiling off entirely.
    /// <para>
    /// A ceiling rather than a spending limit because frame cost tracks the number of live units and
    /// nothing else — a commander that buys dearer vehicles instead of more of them costs the same
    /// to run. It also forces the commander to choose where its strength goes rather than
    /// accumulating everywhere.
    /// </para>
    /// </summary>
    internal static int GroundUnitCeiling { get => Get("Operations", "GroundUnitCeiling", 80); set => Set("Operations", "GroundUnitCeiling", value); }

    /// <summary>
    /// How many slots under the air ceiling a standing patrol may NOT take, so that transport
    /// escorts, strike packages, the radar aeroplane, anti-radiation sorties and air support over a
    /// ground fight always have room (air-ceiling_20260918 §4 decision C).
    /// <para>
    /// Six because that is two escorted lifts of two fighters each plus a two-aeroplane package —
    /// the largest set of protected work the commander has been observed running at once. The reason
    /// it is needed at all: one commander's log of 2026-09-18 carried 33 standing air requests and 27
    /// of them were patrols, so without a reservation the patrols take the whole sky and every lift
    /// holds at its form-up point waiting for an escort that will never be bought — the same outage
    /// as grounding the transports, reached by a different road.
    /// </para>
    /// <para>Zero switches the reservation off and puts patrols on the same line as everything else,
    /// the convention every other rule in the mod uses for a setting that can be turned off without
    /// deleting its caller. A reserve at or above the ceiling grounds standing patrols entirely,
    /// which is a legitimate setting rather than an error.</para>
    /// </summary>
    internal static int AirPatrolReserve { get => Get("Operations", "AirPatrolReserve", 6); set => Set("Operations", "AirPatrolReserve", value); }

    /// <summary>
    /// How long a held point must go uncontested before its garrison is thinned back to the
    /// standing garrison and the surplus cashed in (unit-economy_20260918 §2.4). Five minutes is ten
    /// reviews with no tracked hostile inside the contact range and no vehicle lost — the mod's
    /// existing definition of contact, not a second one — so ground that goes quiet because the war
    /// moved elsewhere is released while ground that is merely between attacks is not. The
    /// commander re-raises there the moment the front comes back, because the point stays on the
    /// ranked list and the picket fill runs every review. Zero or less switches the retirement off.
    /// </summary>
    internal static float QuietGroundMinutes { get => Get("Operations", "QuietGroundMinutes", 5f); set => Set("Operations", "QuietGroundMinutes", value); }
    // PlatoonReseatSavingMinutes (user instruction 2026-09-16: a platoon bought at a far depot and
    // still driving when a forward base opens closer to the enemy is despawned and re-raised from
    // that base; extended the same day to "pickets travelling by ground", which read this same
    // margin — one rule, two callers). How many minutes of driving the re-raise must actually save
    // before it is worth dissolving a group: fifteen. A five-minute gain is not worth taking six
    // vehicles off the road, and anything below the review interval would churn. The key keeps the
    // name the user asked for even though a picket detachment is not a platoon.
    internal static float PlatoonReseatSavingMinutes { get => Get("Operations", "PlatoonReseatSavingMinutes", 15f); set => Set("Operations", "PlatoonReseatSavingMinutes", value); }
    // DownedPilotRescueMinutes: an AI pilot on the ground this long is recovered by the game's own
    // rescue path; two minutes still leaves time for a real helicopter pickup first. 0 disables.
    // Key renamed from DownedPilotRescueMinutes when the default fell from 5 to 2 (user decision
    // 2026-09-15): about a hundred downed pilots stood on the map at any moment at 5 min, each a live
    // physics object, and the frame rate paid for them; BepInEx keeps a saved value under the old key,
    // so the new default needs a new key.
    internal static float DownedPilotRescueMinutes { get => Get("Gameplay", "DownedPilotPickupMinutes", 2f); set => Set("Gameplay", "DownedPilotPickupMinutes", value); }

    // The smarter air wing (design.md, smarter-air-wing_20260914; user decision 2026-09-14).
    // Config-file-only: doctrine knobs, not player taste, so there is no slider for them.
    // Seconds a package waits at its form-up orbit after the first airframe reaches it before it
    // goes in with whoever is there. 180 (user decision 2026-09-14): runway restrictions launch a
    // four-aircraft demand one airframe at a time, roughly a minute apart, so a wait shorter than
    // three minutes never assembles a package at all — and a longer one leaves the ground attack
    // that is holding for CAS standing at its release point past its own 240 s form-up timeout.
    internal static float PackageFormUpSeconds { get => Get("Operations", "PackageFormUpSeconds", 180f); set => Set("Operations", "PackageFormUpSeconds", value); }
    // How far from an objective a pad or strip may be and still launch the attack helicopters that
    // cover it. 90 km (user decision 2026-09-14, raised from 40 km): the playable maps are around
    // 80 km across, so at 40 km most front-line objectives had no pad in range at all and every
    // rotary sortie fell back to a jet — a whole match ran `CAS 0/4 rotary` without one attack
    // helicopter. 90 km covers the diagonal of an 80 km map, which is about nineteen minutes each
    // way at the rotary transit speed the wing already uses (80 m/s). Past it the sortie falls back
    // to a jet. The key was RENAMED with the new default: BepInEx keeps a value already written
    // under the old `RotaryCasRangeMeters` key, so a config carrying the old 40 km would otherwise
    // have silently overridden this.
    internal static float HeliCasRangeMeters { get => Get("Operations", "HeliCasRangeMeters", 90000f); set => Set("Operations", "HeliCasRangeMeters", value); }
    // Tracked hostile air-defence vehicles that have to sit inside one 5 km cluster before the wing
    // opens an anti-radiation sortie on it. 3 (user decision 2026-09-14): one or two vehicles is the
    // ordinary air-defence a platoon carries and CAS is expected to survive; three together is a
    // prepared belt, which is what kills CAS one airframe at a time.
    internal static int AradClusterMinimum { get => Get("Operations", "AradClusterMinimum", 3); set => Set("Operations", "AradClusterMinimum", value); }
    // How near anything hostile the radar airframe's orbit is ever allowed to come. 15 km (user
    // report, 2026-09-14: "an AWACS was just tasked straight into the enemy and killed because the
    // front-line was close to the airbase"). The station used to be the main airbase offset 15 km
    // toward the front with no check on what was in front of it, so on a map where the fighting
    // reaches the airbase that offset walked the orbit into the fight. The whole orbit is measured,
    // not its centre: the station has to sit at least this far plus its own orbit radius from every
    // enemy-held point or base and every tracked hostile ground unit or aircraft. 15 km is outside
    // the reach of the medium surface-to-air belts a front line carries, and an airborne radar sees
    // 150 km and more past it, so nothing is lost from the picture by standing that far back.
    internal static float AwacsMinEnemyDistanceMeters { get => Get("Operations", "AwacsMinEnemyDistanceMeters", 15000f); set => Set("Operations", "AwacsMinEnemyDistanceMeters", value); }

    // Strike packages (design.md, strike-packages_20260915; user decision 2026-09-15: "we need
    // multi-aircraft type packages, strike on enemy held control points"). Config-file-only:
    // doctrine knobs, not player taste, so there is no slider for them.
    // Minutes between deliberate strikes while no ground attack is open. 6: a package takes about
    // three minutes to form up (PackageFormUpSeconds) and a few more to fly its leg and get home, so
    // a shorter interval would open the next strike before the last one had landed, and a longer one
    // leaves a quiet commander doing nothing in the air but reacting — which is the whole complaint
    // this feature answers. A strike ahead of a ground attack ignores this clock entirely.
    internal static float StrikeIntervalMinutes { get => Get("Operations", "StrikeIntervalMinutes", 6f); set => Set("Operations", "StrikeIntervalMinutes", value); }
    // Minutes a point that has just been struck is left alone. 10: long enough that the wing works
    // its way across the enemy's points instead of bombing the nearest one every six minutes, and
    // short enough that a point which has been reinforced since is struck again inside one match.
    internal static float StrikePointCooldownMinutes { get => Get("Operations", "StrikePointCooldownMinutes", 10f); set => Set("Operations", "StrikePointCooldownMinutes", value); }
    // How far from a held airbase a deliberate strike target may be. 80 km: the playable maps are
    // about 80 km across, so this is "anywhere on the map a strip we hold can reach", and it is the
    // same reasoning that raised HeliCasRangeMeters to 90 km. Past it the package spends more of the
    // sortie in transit than over the target and arrives with no fuel for a second pass.
    internal static float StrikeRangeMeters { get => Get("Operations", "StrikeRangeMeters", 80000f); set => Set("Operations", "StrikeRangeMeters", value); }
    // Minutes the escorts hold over the target after the package goes in, before they are released
    // back to the wing. 4: two passes' worth for the strike element underneath them, which is what
    // the escort is there to cover, and short enough that a pair of fighters is not parked over a
    // dead point while a platoon in contact goes bare.
    internal static float StrikeLoiterMinutes { get => Get("Operations", "StrikeLoiterMinutes", 4f); set => Set("Operations", "StrikeLoiterMinutes", value); }
    // The longest a strike sortie lives before it is closed whatever has happened. 12: form-up (3),
    // transit across half a map (about 4 at the jet transit speed), the attack, and the leg home. A
    // package still open past that is one nothing is going to finish, and it is holding airframes the
    // rest of the wing is asking for.
    internal static float StrikeSortieMaxMinutes { get => Get("Operations", "StrikeSortieMaxMinutes", 12f); set => Set("Operations", "StrikeSortieMaxMinutes", value); }
    // The largest share of one side's airborne airframes any single TYPE may make up before the buy
    // starts skipping it (user decision 3, 2026-09-15: "a diversity cap so one type cannot make up
    // the whole side"). 0.6: a majority is allowed — the best airframe for the job SHOULD be the
    // commonest — but two thirds of the sky being one aeroplane is the Revoker monoculture the whole
    // track exists to break. Below about 0.5 the cap would fight the tier rule every buy.
    internal static float TypeShareCap { get => Get("Operations", "TypeShareCap", 0.6f); set => Set("Operations", "TypeShareCap", value); }
    // Whether a sortie whose work reads as EASY is flown by the cheap bottom-tier airframe rather
    // than by the aeroplane built for the job (user instruction 2026-09-16, "i also want to see
    // increased use of the cheap aircraft"). On: the trainers are rated 0.64 against the ground where
    // the dedicated fighter is rated 0.46, at a third of the price, so a picket with nothing tracked
    // over it and no air defence near it is work they are good enough for. Off restores the rule that
    // the bottom tier flies only when nothing else can fill the role. See
    // CommanderEnemyCommanderService.AirJobIsEasy for where the line between easy and not is drawn.
    internal static bool CheapAirframeEasyJobs { get => Get("Operations", "CheapAirframeEasyJobs", true); set => Set("Operations", "CheapAirframeEasyJobs", value); }
    // Whether an element the allocation cannot cover at full strength fills its remaining slots with
    // the cheapest airframe that can do the job instead of buying nothing at all (the second half of
    // the same instruction). It never displaces an airframe the sortie could otherwise have afforded:
    // the proper airframes are bought first, and this spends only what is left over. Off restores the
    // whole-element-or-nothing rule of 2026-09-14.
    internal static bool CheapAirframePadding { get => Get("Operations", "CheapAirframePadding", true); set => Set("Operations", "CheapAirframePadding", value); }
    // The three CAP station bands, in metres above ground (user decision 5, 2026-09-15: "current CAP
    // mission generations needs height variety too"). 1500 is below the medium surface-to-air belts
    // and where a fighter can see and reach something on the deck; 4000 is the ordinary patrol height
    // the game's own AI settles at; 7500 is high enough to look down on both and to give a diving
    // intercept the energy it needs. Consecutive CAP sorties take them in turn.
    internal static float CapBandLowMeters { get => Get("Operations", "CapBandLowMeters", 1500f); set => Set("Operations", "CapBandLowMeters", value); }
    internal static float CapBandMidMeters { get => Get("Operations", "CapBandMidMeters", 4000f); set => Set("Operations", "CapBandMidMeters", value); }
    internal static float CapBandHighMeters { get => Get("Operations", "CapBandHighMeters", 7500f); set => Set("Operations", "CapBandHighMeters", value); }

    // Picket insertion by transport helicopter (design.md, heli-picket-insertion_20260913).
    // Master toggle: visible AI spend, killable like every doctrine feature.
    internal static bool OperationsHeliInsertionEnabled { get => Get("Operations", "HeliInsertionEnabled", true); set => Set("Operations", "HeliInsertionEnabled", value); }
    // A picket point farther than this from the nearest road is flown in rather than driven (the
    // user's gate, 2026-09-13). Within it the drive is a short cross-country hop; beyond it the
    // crawl into the ring costs the point minutes of income. POINTS tab, OPERATIONS box.
    internal static float OperationsHeliInsertionOffRoadMeters { get => Get("Operations", "HeliInsertionOffRoadMeters", 2000f); set => Set("Operations", "HeliInsertionOffRoadMeters", value); }
    // Insertion flights airborne per HQ at once (config-only: a balance knob in the recipe's
    // company). Transports deliver, they do not win fights — the same reasoning as the enemy
    // commander's TransportLimit. Three since the pickets-first doctrine (2026-09-14): pickets are
    // now the capture mechanic on every point away from the front, and a mountain map has more
    // roadless hilltops than one flight at a time can ever garrison — a lift takes minutes, so at
    // one flight the commander was still on its second hilltop when the match turned.
    // Six since picket reinforcement (2026-09-14): a picket that LOSES a vehicle now asks for a
    // flight of its own carrying just the replacement, so the limit has to cover holding the line
    // and topping it up at the same time, not only the opening land-grab.
    // Key renamed from HeliInsertionLimit when the default rose 1 -> 3 (pickets-first, 2026-09-14),
    // and again to HeliInsertionFlightsMax when it rose 3 -> 6 (2026-09-14), same reason as
    // ReservePlatoonCount: BepInEx keeps a player's old value under the old key, so a rename is how
    // a raised default actually reaches an existing config file.
    // How far from a held airbase a transport will carry a picket (reach-and-points follow-up,
    // 2026-09-14): a point beyond depot reach gets an air-only picket mission only within this
    // range. 60 km is a helicopter's comfortable radius on the 82 km map and covers all of a small one.
    // A picket transport never flies to a point nearer the enemy's assets than this (user,
    // 2026-09-14: "aerial picket insertions are not generated for points near enemy unless heavily
    // escorted" — the escort is a follow-up; until it exists, the flight is simply not made). 25 km
    // is the front range plus a fighter's dash; the 2026-09-14 match lost seven of nineteen flights
    // inside it.
    internal static float OperationsHeliInsertionEnemyStandoffMeters { get => Get("Operations", "HeliInsertionEnemyStandoffMeters", 25000f); set => Set("Operations", "HeliInsertionEnemyStandoffMeters", value); }

    /// <summary>How far from the nearest tracked enemy a flown-in FOB site must be: 40 km (user,
    /// 2026-09-15: "FOB bird got shot down just before drop-off... we need to be even further from
    /// the enemy"). Wider than the 25 km an unescorted picket flight keeps, because a FOB is a
    /// one-shot 75-fund order and its site is chosen, not given.</summary>
    internal static float FobEnemyStandoffMeters { get => Get("Operations", "FobEnemyStandoffMeters", 40000f); set => Set("Operations", "FobEnemyStandoffMeters", value); }
    internal static float OperationsHeliInsertionRangeMeters { get => Get("Operations", "HeliInsertionRangeMeters", 60000f); set => Set("Operations", "HeliInsertionRangeMeters", value); }
    internal static int OperationsHeliInsertionLimit { get => Get("Operations", "HeliInsertionFlightsMax", 6); set => Set("Operations", "HeliInsertionFlightsMax", value); }
    // Minutes after losing an insertion flight before the same point asks again (config-only;
    // confirmed by the user, 2026-09-13). An identical loss on retry a minute later is a waste;
    // an hour is cowardice.
    internal static float OperationsHeliInsertionCooldownMinutes { get => Get("Operations", "HeliInsertionCooldownMinutes", 10f); set => Set("Operations", "HeliInsertionCooldownMinutes", value); }
    // How wide a circle around a candidate landing zone has to be free of woodland and scenery
    // before a transport is sent to land in it (user report 2026-09-14: "air insertion of pickets
    // sometimes is sent to land in tree covered areas - it cannot land"). 40 m is roughly twice the
    // rotor span of the transports in the catalog plus the room two vehicles need to roll off the
    // ramp and clear it. Config-only.
    internal static float OperationsLzClearRadiusMeters { get => Get("Operations", "LzClearRadiusMeters", 40f); set => Set("Operations", "LzClearRadiusMeters", value); }
    // How far from the chosen post the clear-ground search may move a landing zone before the
    // insertion is declined instead. 400 m is inside a typical control ring, so a relocated landing
    // still puts the vehicles on the point rather than on the next hill; past that the drive from
    // the landing to the posts costs more than driving the whole way would have. Config-only.
    internal static float OperationsLzSearchRadiusMeters { get => Get("Operations", "LzSearchRadiusMeters", 400f); set => Set("Operations", "LzSearchRadiusMeters", value); }
    // Seconds a bound insertion flight may sit within 500 m of its landing zone without dropping
    // anything before it is treated as unable to land: 120. Two minutes covers the game's own
    // touchdown search (which re-tries every 3 s) plus a slow vertical descent with room to spare,
    // and is short enough that a stuck transport is turned into an airdrop or recalled inside four
    // operations reviews. Zero disables the check. Config-only.
    internal static float OperationsInsertionStallTimeoutSeconds { get => Get("Operations", "InsertionStallTimeoutSeconds", 120f); set => Set("Operations", "InsertionStallTimeoutSeconds", value); }

    // Whether a cargo transport unloads its vehicles in place instead of trying to touch down
    // (delivery-bypass_20260916, user decision 2026-09-16: bypass EVERY delivery, not only stalled
    // ones). Default on. It exists as a setting rather than a constant because this is the delivery
    // path with the worst incident history in the repository — a hand-off crash that stopped all
    // transports spawning, and a "no transports spawning at all" regression — so the developer must
    // be able to fall back to the game's own landing gate from the settings window without waiting
    // for a rebuild. Off restores the behaviour of 2026-09-15 exactly: the game's own gate
    // (radar altitude under 2 m and speed under 10 m/s) and the stall clock's parachute fallback.
    // Config-only.
    internal static bool SupplyUnloadInPlaceEnabled { get => Get("Supply", "UnloadInPlace", true); set => Set("Supply", "UnloadInPlace", value); }

    // How far from one of its own vehicle depots a commander will send ground vehicles at all:
    // 20 km (reach-and-points, user decision 2026-09-14, "only spawn units for objectives closer
    // than some km"). Past this a control point gets no forward base, no road picket and no platoon
    // — a helicopter insertion is the only way to staff it until a depot comes within reach, which
    // is what makes a forward operating base worth building. Twenty kilometres is roughly twenty
    // minutes of cross-country driving for the slowest vehicle in a platoon, which is as long as a
    // commander can spend moving a unit before the reason it was sent has changed.
    internal static float DepotReachMeters { get => Get("Operations", "DepotReachMeters", 20000f); set => Set("Operations", "DepotReachMeters", value); }

    // Forward operating bases (design.md, fob-construction_20260914; user decision 2026-09-14,
    // DECISION-014).
    // Master toggle: a commander that may not build FOBs simply never opens the order, and every
    // other rung is untouched. Visible AI spend, killable like every other doctrine feature.
    internal static bool FobEnabled { get => Get("Operations", "FobEnabled", true); set => Set("Operations", "FobEnabled", value); }
    // How far a FOB site must stand from every vehicle depot this commander already owns: 5 km
    // (user request 2026-09-15: FOB placement was too restrictive and few candidate sites qualified;
    // halved from the 10 km of the reach-and-points decision of 2026-09-14). That 10 km replaced the
    // old FobMinBaseDistanceMeters rule, which asked for 15 km from ANY airfield on the map and
    // refused every FOB of a whole 82 km match because no held point was ever that far from an
    // airbase. A FOB is for extending the ground the commander can drive to, so the distance that
    // matters is the distance to the depots it already has. At 5 km — a quarter of the 20 km depot
    // reach below — the new depot's circle still overlaps the old one heavily, but the site score
    // (how many stranded points it brings in reach) is what decides whether it is worth building;
    // this distance only keeps a FOB off the doorstep of a depot the commander already owns.
    // Key renamed from FobMinDepotDistanceMeters when the default dropped 10 -> 5 km: BepInEx keeps
    // the value already in the cfg, so a default change under the old key never lands.
    // Both FobMinBaseDistanceMeters and FobMaxPerCommander were deleted here on 2026-09-14. Their
    // keys, like FobMinDepotDistanceMeters, are left orphaned in existing config files on purpose;
    // nothing reads them.
    internal static float FobMinDepotDistanceMeters { get => Get("Operations", "FobMinOwnedDepotDistanceMeters", 5000f); set => Set("Operations", "FobMinOwnedDepotDistanceMeters", value); }
    // How far apart two forward operating bases must stand: 5 km (user request 2026-09-15: FOB
    // placement was too restrictive; cut from 20 km). A FOB becomes an airbase the moment it comes
    // online. With the FOB cap retired (reach-and-points, 2026-09-14) this spacing and the site
    // score are what limit how many a commander builds; the user's "we don't want EVERY capturable
    // point turning into a FOB" is now carried by the score, which refuses any site that brings no
    // stranded point within depot reach. 5 km matches the depot distance above so the two halves of
    // the placement rule refuse at the same range and the log reason reads as one number.
    // Key renamed from FobMinSpacingMeters when the default dropped 20 -> 5 km, same BepInEx reason
    // as the depot distance above.
    internal static float FobMinSpacingMeters { get => Get("Operations", "FobSpacingMeters", 5000f); set => Set("Operations", "FobSpacingMeters", value); }
    // Minutes after a commander LOSES a forward operating base — destroyed or captured — before it
    // will build another anywhere: 15. Longer than the ten-minute cooldown a failed delivery puts on
    // one site, because losing a finished base says the ground was wrong, not that one convoy was
    // unlucky. A base the commander abandons on purpose does not start this clock.
    internal static float FobLossCooldownMinutes { get => Get("Operations", "FobLossCooldownMinutes", 15f); set => Set("Operations", "FobLossCooldownMinutes", value); }

    // Air-mobile platoons and protected lifts (design.md, air-mobile-platoons_20260915; user
    // decision 2026-09-15, DECISION-032). Config-file-only: doctrine knobs, not player taste.
    // Minutes of driving from the nearest usable depot to an objective past which a NEW platoon for
    // that objective is raised air-mobile instead of at the depot: 10. On the 82 km map a platoon
    // raised at the home depot drives 15-20 km to an in-reach objective at about 15 m/s, so first
    // contact was twenty minutes and more after the vehicles were bought; ten minutes is the point
    // past which the flight is worth its hull rental and its escort. Below it the platoon drives, as
    // it always has.
    internal static float AirMobileDriveMinutes { get => Get("Operations", "AirMobileDriveMinutes", 10f); set => Set("Operations", "AirMobileDriveMinutes", value); }
    // How much longer a road route is than the straight line it is measured along: 1.3. Roads bend
    // round hills and water, and the drive-time gate measures a straight line because the road
    // network the mod indexes gives distances to the nearest road, not routes along it. A third
    // again is the usual planning figure and is what the 34 km / 23 min example in the design was
    // computed with. Raise it on a map with few roads; 1.0 makes the gate measure crow-flight time.
    internal static float RoadDetourFactor { get => Get("Operations", "RoadDetourFactor", 1.3f); set => Set("Operations", "RoadDetourFactor", value); }
    // How fast a loaded tracked vehicle covers ground on a road, in metres per second: 15 (54 km/h),
    // measured off the game's own platoon marches. It is the divisor of the drive-time gate, so
    // setting it faster raises the distance at which a platoon still drives.
    internal static float GroundSpeedMetersPerSecond { get => Get("Operations", "GroundSpeedMetersPerSecond", 15f); set => Set("Operations", "GroundSpeedMetersPerSecond", value); }
    // Loads one air-mobile platoon is flown in: 3. A load is two vehicles (the insertion chain's own
    // cargo maximum), so three loads are the six vehicles of a platoon — four carriers and two
    // air-defence vehicles, the roles the transports can actually carry. Fewer loads would land a
    // platoon too small to hold a point; more would fly vehicles the establishment has no room for.
    internal static int LiftLoadsPerPlatoon { get => Get("Operations", "LiftLoadsPerPlatoon", 3); set => Set("Operations", "LiftLoadsPerPlatoon", value); }
    // Fighters every lift's cover sortie flies whatever is tracked over the landing zone: 2, the
    // same floor a transport escort and a defended strike package already carry, and for the same
    // reason — one fighter alone is the first thing a pair of raiders kills, and the 2026-09-15 logs
    // lost construction flights to fighters nobody had tracked.
    internal static int LiftEscortMinimum { get => Get("Operations", "LiftEscortMinimum", 2); set => Set("Operations", "LiftEscortMinimum", value); }
    // How much nearer the landing zone a lift's escort must be than the load before the load is let
    // off the deck, in metres: 2,000 (user instruction, 2026-09-17: "air insertions should wait for
    // their escorts to be ahead of them (they have a habit of flying straight into danger)"). Until
    // then the gate asked only that the escort was airborne, so a transport left the runway the
    // moment its fighters had, overtook them and arrived alone over the dangerous ground. Two
    // kilometres is about half a minute of flying for a loaded transport helicopter at roughly
    // 60 m/s, so the escort is over any piece of ground about half a minute before the load and is
    // what a waiting fighter or air-defence vehicle shoots at first. A fighter at roughly 200 m/s
    // opens that gap within about fifteen seconds of leaving the same airbase, far inside the
    // three-minute form-up clock, so a cover that launches with its lift costs the lift almost
    // nothing; a cover coming from a base further out earns the gap as it flies, which is why the
    // rule measures ground still to cover rather than a fraction of the route. A setting rather than
    // a constant because the right lead depends on the transport and fighter mix a mission fields
    // and on how big the map is, which is exactly the kind of number the developer retunes in a
    // match. Zero disables the lead test and leaves the old "escorts are airborne" gate.
    internal static float LiftEscortLeadMeters { get => Get("Operations", "LiftEscortLeadMeters", 2000f); set => Set("Operations", "LiftEscortLeadMeters", value); }
    // How many times a lift's flight price the balance must hold before the lift launches: 2. The
    // flight charges its hull and its cargo up front and refunds the hull on recovery, so a balance
    // at twice the price absorbs the float without emptying the treasury; below it the commander
    // waits rather than spending its last funds on a transport.
    internal static float LiftFundsMultiple { get => Get("Operations", "LiftFundsMultiple", 2f); set => Set("Operations", "LiftFundsMultiple", value); }
    // How far short of an enemy-held objective a lift may put its platoon down, in metres: 10,000.
    // A transport cannot land on ground the enemy is standing on, and the 2026-09-15 match cancelled
    // most platoon lifts with `the enemy holds the point` and then drove the platoon for an hour —
    // the very problem the lift exists to solve. The lift now lands on the nearest point the
    // commander owns or nobody holds within this distance (the "forward landing zone") and the
    // platoon drives the last leg. Ten kilometres is about eleven minutes of driving at the ground
    // speed above — a few minutes against the twenty-plus the whole drive would have been, which is
    // the point; much further and the lift has only moved the long drive rather than removed it.
    internal static float LiftAirheadMaxMeters { get => Get("Operations", "LiftAirheadMaxMeters", 10000f); set => Set("Operations", "LiftAirheadMaxMeters", value); }
    // How long a spotted hostile ground unit still counts as "the enemy is there" when a point's
    // distance to the enemy is measured, in seconds: 300. Until 2026-09-15 that distance counted only
    // control points and bases another faction HELD, so a point with an enemy column sitting five
    // kilometres away read as rear and 40 km clear, and the commander sited forward bases and flew
    // picket insertions straight into it (user report: "its choosing FOB sites and air insertion of
    // pickets etc way too close to the enemy"). Five minutes rather than the 45 s the home-defence
    // memory uses: that shorter window is for reacting to a raid, where a contact nobody has seen for
    // a minute is probably gone, while this one is for deciding where to put a base or land a
    // transport — and a column seen five minutes ago is still somewhere near that ground.
    internal static float StandoffContactMemorySeconds { get => Get("Operations", "StandoffContactMemorySeconds", 300f); set => Set("Operations", "StandoffContactMemorySeconds", value); }
    // How near a spotted hostile ground unit a platoon lift's landing zone may be before the lift
    // moves it, in metres: 10,000. A lift flies under escort and a sweep, which is what lets it cross
    // ground a lone picket flight may not — but no escort excuses setting six vehicles down on top of
    // an enemy column, which is what the 2026-09-15 match did. Ten kilometres is the same figure the
    // forward landing zone may sit from its objective, so a lift never moves its landing zone to
    // ground it would refuse for the same reason.
    internal static float LiftAbortStandoffMeters { get => Get("Operations", "LiftAbortStandoffMeters", 10000f); set => Set("Operations", "LiftAbortStandoffMeters", value); }
    // How near the enemy may come to a flown-in FOB's site while its construction flight is in the
    // AIR before the flight is re-routed to another site, in metres: 20,000. Half the 40 km the site
    // picker demands: the picker's figure is about where a base belongs for the rest of the match,
    // this one is about whether a load already flying can still be put down. Re-asking the full 40 km
    // in flight turned nearly every long construction flight round the moment one enemy unit came
    // within 39 km (user decision 2026-09-16: "2" — a middle abort radius — "and upon abortion a
    // check should be made for an alternative site and be re-routed rather than RTB").
    internal static float FobAbortStandoffMeters { get => Get("Operations", "FobAbortStandoffMeters", 20000f); set => Set("Operations", "FobAbortStandoffMeters", value); }
    // Seconds between logistics watches, the clock that re-checks deliveries already under way: 5,
    // the movement tick's own cadence and a sixth of the mission review's (user, 2026-09-16:
    // "FOB/insertion re-calculating needs to happen more regularly - enemy presence can change
    // rapidly and we need to redirect or abandon missions if there's little hope of a successful
    // delivery"). A transport crosses about 400 m in five seconds, so a threat spotted between two
    // watches is still tens of kilometres from the landing zone. The watch does only the cheap
    // in-flight re-checks — route, landing zone, escort — never the whole review, which stays at 30 s
    // because re-planning missions six times as often would buy nothing and cost a great deal.
    internal static float LogisticsWatchSeconds { get => Get("Operations", "LogisticsWatchSeconds", 5f); set => Set("Operations", "LogisticsWatchSeconds", value); }
    // Minutes a delivery with no safe route waits on the deck before it is given up: 6. The wait is
    // what makes a spotted belt a delay rather than a cancelled order — air defence is suppressed,
    // columns drive on, and a route that is shut now is often open two minutes later. Six minutes is
    // about one sortie's life; past it the order has spent longer waiting than flying and the
    // commander is better off putting the platoon on the road or the base somewhere else.
    internal static float LiftHopelessMinutes { get => Get("Operations", "LiftHopelessMinutes", 6f); set => Set("Operations", "LiftHopelessMinutes", value); }

    // Air fallback posture (design.md, air-fallback-posture_20260916; user decision 2026-09-16: "too
    // often we see a CAP escort fly straight at 5x enemy aircraft, rather than retreating and calling
    // for help and then re-engaging"). Config-file-only doctrine knobs, like the ground tactics below.
    // How many more fixed-wing combat aircraft the enemy must have tracked within the sizing ring of
    // a sortie than the sortie has fighters up before those fighters fall back: 2 (the user's own
    // threshold, 2026-09-16: "hostiles exceed ours by 2+"). One more is an even fight the game's own
    // bravery rule already weighs; two more is the point at which a fighter that presses on dies
    // one at a time while the review's reinforcements are still being bought. Zero or less turns the
    // posture off.
    // How far a tracked hostile fixed-wing aircraft may be from a sortie's objective OR from any of the
    // sortie's own fighters and still count in the outnumbered check, in metres: 20,000 (user decision
    // 2026-09-16: "we need a far bigger range for the outnumbered check - 20km of aircraft and 20km of
    // objective"). The 8 km contact ring the rest of the review uses is the distance at which a fight
    // is already joined; a fighter must decide to fall back before that, while the raid is still
    // closing.
    internal static float AirPostureRingMeters { get => Get("Operations", "AirPostureRingMeters", 20000f); set => Set("Operations", "AirPostureRingMeters", value); }
    internal static int AirFallbackMargin { get => Get("Operations", "AirFallbackMargin", 2); set => Set("Operations", "AirFallbackMargin", value); }
    // How many more fighters than tracked hostiles a sortie that has fallen back must have before it
    // goes back in: 1 (user decision 2026-09-16: "re-engage only when we OUTNUMBER"). Parity would
    // send the fighters back into the fight they just left; one more is the smallest superiority
    // there is. It is also what the call for help asks for on top of the hostile count.
    internal static int AirReengageMargin { get => Get("Operations", "AirReengageMargin", 1); set => Set("Operations", "AirReengageMargin", value); }
    // How far a falling-back sortie's fighters move from the sortie centre toward the commander's
    // nearest own airbase, in metres: 15,000. Nearly twice the 8 km sizing ring, so the fighters
    // leave the ring the hostiles were counted in rather than orbiting on its edge, and short enough
    // that they are back over the objective within a minute or two of the reinforcements arriving.
    // The point never passes the base itself (a base 10 km away yields the base).
    internal static float AirFallbackDistanceMeters { get => Get("Operations", "AirFallbackDistanceMeters", 15000f); set => Set("Operations", "AirFallbackDistanceMeters", value); }
    // (The self-defence radius setting was removed on 2026-09-16, design.md air-survival-layer_20260916
    // Section 4: the leash is now the closing-hostile rule plus a class constant,
    // CommanderAirCommandService.SelfDefenceCloseMeters, so one number with its reason lives beside the
    // rule that reads it rather than in a config file nobody retunes.)
    // Minutes a sortie waits, falling back, for enough fighters to arrive before it stands down: 5.
    // A review runs every 30 s and a bought fighter takes two or three minutes to launch and transit,
    // so five minutes is one full round of retask and buy with a margin for a second; past it the
    // reinforcement is not coming (the brake is holding fighters, or the fund is empty) and the
    // fighters are better used elsewhere. Zero or less waits for ever.
    internal static float AirFallbackGiveUpMinutes { get => Get("Operations", "AirFallbackGiveUpMinutes", 5f); set => Set("Operations", "AirFallbackGiveUpMinutes", value); }

    // Air survival layer (design.md, air-survival-layer_20260916; user decision 2026-09-16: "insert
    // some sense of self-preservation into our aircraft"). Config-file-only doctrine knobs, like the
    // posture above.
    // The share of its fuel below which a commanded airframe turns for home of its own accord: 0.25.
    // The game's own pilot switches itself into the landing state at 0.20 (FuelChecker), and when it
    // does so the commander's books are left wrong — the sortie still counts the aeroplane as on
    // station. Acting one twentieth of a tank earlier means the commander gives the order, the sortie
    // frees the slot cleanly and the replacement is bought while the aeroplane is still flying home.
    // Zero or less turns the fuel rule off.
    internal static float AirSurvivalFuelFraction { get => Get("Operations", "AirSurvivalFuelFraction", 0.25f); set => Set("Operations", "AirSurvivalFuelFraction", value); }
    // How far from a sortie's objective a tracked hostile air-defence vehicle counts as "a belt
    // ahead", in metres: 20,000. The same reach as the posture ring, and for the same reason: the
    // decision to hold has to be taken while the formation is still outside the belt's own envelope,
    // not once it is inside it. A long-range SAM covers a good part of that circle.
    internal static float AirBeltHoldRadiusMeters { get => Get("Operations", "AirBeltHoldRadiusMeters", 20000f); set => Set("Operations", "AirBeltHoldRadiusMeters", value); }
    // How far from every enemy asset and tracked hostile a package's form-up point must stand, in
    // metres: 20,000. Split off AirPostureRingMeters on 2026-09-16 (audit
    // conductor/designs/2026-09-16-air-self-preservation-audit.md Section 2): one setting was doing
    // two unrelated jobs, so retuning the outnumbered retreat trigger silently moved every package's
    // orbit. Same shipped number, so the split changes nothing by itself.
    internal static float PackageFormUpStandoffMeters { get => Get("Operations", "PackageFormUpStandoffMeters", 20000f); set => Set("Operations", "PackageFormUpStandoffMeters", value); }

    // Ground tactics (design.md, ground-tactics_20260914; user decision 2026-09-14, DECISION-012).
    // Config-file-only: doctrine knobs, not player taste, so there is no slider for them.
    // How far beyond its hold ring a garrison's tanks and IFVs push when the point comes under
    // attack. 400 m: far enough that the armour meets an attack in front of the vehicles that keep
    // the point paying rather than on top of them, and inside the 2.5 km at which the contact is
    // noticed at all, so the arc forms before the shooting starts. On a point whose own radius is
    // smaller than this the standoff is cut to that radius (CommanderOperationsService
    // .DefenceArcDistanceMeters), so a tiny point never throws its armour a whole radius away.
    internal static float DefenceArcStandoffMeters { get => Get("Operations", "DefenceArcStandoffMeters", 400f); set => Set("Operations", "DefenceArcStandoffMeters", value); }
    // How far an attacking platoon advances in one cross-country bound once it has left its release
    // point. 800 m: about one movement-tick-and-a-half of driving for a tank, short enough that the
    // platoon re-forms its line several times on the way in rather than arriving strung out, and
    // long enough that a 5 km approach is a handful of bounds and not a crawl. Reduce to 400 if the
    // game's ground AI is seen snapping an 800 m off-road leg back onto the road network.
    internal static float BoundMeters { get => Get("Operations", "BoundMeters", 800f); set => Set("Operations", "BoundMeters", value); }
    // Within this of a tracked enemy every ground destination is issued as a bound rather than as a
    // long path the game's own routing may take down a road. 1000 m (the user's rule, 2026-09-14):
    // inside a kilometre of a known enemy a road is an ambush, and the last kilometre is the part
    // worth driving across country.
    internal static float OffRoadRangeMeters { get => Get("Operations", "OffRoadRangeMeters", 1000f); set => Set("Operations", "OffRoadRangeMeters", value); }

    // The commander priority ladder (design.md, commander-priorities_20260914; user decision
    // 2026-09-14): every commanded HQ spends from ONE pot per review, in a fixed order — home CAP
    // first and strict, then platoons and their air, pickets, buildings sharing what is left by a
    // weighted draw. Config-file-only: these are doctrine knobs, not player taste, so there is no
    // slider for them.
    // Home-CAP fighters the commander keeps over its own airbases before it will spend on anything
    // else (user decision 2026-09-14: "strict" — while short, nothing below it is bought).
    internal static int HomeCapBaseline { get => Get("Commander", "HomeCapBaseline", 2); set => Set("Commander", "HomeCapBaseline", value); }
    // One more home-CAP fighter per this many tracked enemy aircraft inside 30 km of the commander's
    // airbases. Two: a raid is a pair, and the wing scales with what is actually spotted, not with
    // what might exist.
    internal static int HomeCapPerEnemyAircraft { get => Get("Commander", "HomeCapPerEnemyAircraft", 2); set => Set("Commander", "HomeCapPerEnemyAircraft", value); }
    // Ceiling on home-CAP fighters wanted. 4 (user decision 2026-09-14, replacing "no limit"): each
    // side's home CAP counted as enemy aircraft for the other, so 2 + 1 per 2 fed itself and both
    // sides bought CAP faster and faster. Threats beyond the ceiling are met by platoons requesting
    // CAP over themselves when enemy aircraft appear near them, not by more fighters over the base.
    internal static int HomeCapMax { get => Get("Commander", "HomeCapMax", 4); set => Set("Commander", "HomeCapMax", value); }
    // Relative weights of the three lower rungs in each review's draw (platoons and their air /
    // air-delivered pickets / buildings). Platoons at 60: "the map is huge, so there are nearly
    // always more platoons that could be built" (user decision 2026-09-14) — the draw adds
    // variability per review, not per commander, and the rung floor below keeps the other two
    // always reachable.
    internal static float LadderPlatoonWeight { get => Get("Commander", "LadderPlatoonWeight", 60f); set => Set("Commander", "LadderPlatoonWeight", value); }
    internal static float LadderPicketWeight { get => Get("Commander", "LadderPicketWeight", 20f); set => Set("Commander", "LadderPicketWeight", value); }
    internal static float LadderBuildingWeight { get => Get("Commander", "LadderBuildingWeight", 20f); set => Set("Commander", "LadderBuildingWeight", value); }
    // Percent of the post-CAP remainder reserved for every rung that has open demand, so the weighted
    // draw cannot starve pickets or buildings however heavy the platoon weight is. Ten percent of the
    // remainder per demanding rung; money nobody spends stays in the balance.
    internal static float LadderRungFloorPercent { get => Get("Commander", "LadderRungFloorPercent", 10f); set => Set("Commander", "LadderRungFloorPercent", value); }

    internal static KeyboardShortcut PrimaryAction { get => GetShortcut("PrimaryAction", KeyCode.Mouse0, "Select units and place world targets."); set => Set("Keybinds", "PrimaryAction", value); }
    internal static KeyboardShortcut SecondaryAction { get => GetShortcut("SecondaryAction", KeyCode.Mouse1, "Issue move orders."); set => Set("Keybinds", "SecondaryAction", value); }
    internal static KeyboardShortcut AddToSelection { get => GetShortcut("AddToSelection", KeyCode.LeftShift, "Hold while selecting to add units."); set => Set("Keybinds", "AddToSelection", value); }
    internal static KeyboardShortcut RepeatDeployment { get => GetShortcut("RepeatDeployment", KeyCode.LeftShift, "Hold while placing a supply target to repeat the deployment."); set => Set("Keybinds", "RepeatDeployment", value); }
    internal static KeyboardShortcut DeleteUnitModifier { get => GetShortcut("DeleteUnitModifier", KeyCode.LeftAlt, "Hold to turn PIN into DEL."); set => Set("Keybinds", "DeleteUnitModifier", value); }
    internal static KeyboardShortcut CameraCenterFollow { get => GetShortcut("CameraCenterFollow", KeyCode.Space, "Tap to center; hold to center and follow."); set => Set("Keybinds", "CameraCenterFollow", value); }
    internal static KeyboardShortcut ToggleUi { get => GetShortcut("ToggleUi", KeyCode.H, "Cycle visible, RTS UI hidden, and all UI hidden."); set => Set("Keybinds", "ToggleUi", value); }
    internal static KeyboardShortcut CameraForward { get => GetShortcut("CameraForward", KeyCode.W, "Move the RTS camera forward."); set => Set("Keybinds", "CameraForward", value); }
    internal static KeyboardShortcut CameraBackward { get => GetShortcut("CameraBackward", KeyCode.S, "Move the RTS camera backward."); set => Set("Keybinds", "CameraBackward", value); }
    internal static KeyboardShortcut CameraLeft { get => GetShortcut("CameraLeft", KeyCode.A, "Move the RTS camera left."); set => Set("Keybinds", "CameraLeft", value); }
    internal static KeyboardShortcut CameraRight { get => GetShortcut("CameraRight", KeyCode.D, "Move the RTS camera right."); set => Set("Keybinds", "CameraRight", value); }
    internal static KeyboardShortcut CameraUp { get => GetShortcut("CameraUp", KeyCode.Q, "Move the RTS camera upward."); set => Set("Keybinds", "CameraUp", value); }
    internal static KeyboardShortcut CameraDown { get => GetShortcut("CameraDown", KeyCode.E, "Move the RTS camera downward."); set => Set("Keybinds", "CameraDown", value); }
    internal static KeyboardShortcut QueueWaypoint { get => GetShortcut("QueueWaypoint", KeyCode.LeftShift, "Hold while ordering to append a travel point instead of replacing the route."); set => Set("Keybinds", "QueueWaypoint", value); }
    internal static KeyboardShortcut AssignGroupModifier { get => GetShortcut("AssignGroupModifier", KeyCode.LeftControl, "Hold with 1-9 to store the selection as that group."); set => Set("Keybinds", "AssignGroupModifier", value); }
    internal static KeyboardShortcut StopOrder { get => GetShortcut("StopOrder", KeyCode.X, "Cancel RTS orders and hold the selection where it stands."); set => Set("Keybinds", "StopOrder", value); }
    internal static KeyboardShortcut SelectSameType { get => GetShortcut("SelectSameType", KeyCode.LeftControl, "Hold while clicking a unit to select every unit of that type the faction owns."); set => Set("Keybinds", "SelectSameType", value); }
    internal static KeyboardShortcut CycleIdleUnit { get => GetShortcut("CycleIdleUnit", KeyCode.Period, "Select and jump to the next friendly ground/naval unit with no RTS order."); set => Set("Keybinds", "CycleIdleUnit", value); }
    internal static KeyboardShortcut CameraFreeLook { get => GetShortcut("CameraFreeLook", KeyCode.Mouse2, "Hold while moving the mouse to look around in RTS mode."); set => Set("Keybinds", "CameraFreeLook", value); }
    // Right Alt, not Left: Left Alt is the delete modifier and would show DEL while looking.
    internal static KeyboardShortcut CameraFreeLookAlt { get => GetShortcut("CameraFreeLookAlt", KeyCode.RightAlt, "Second key for free look, for a mouse without a usable middle button."); set => Set("Keybinds", "CameraFreeLookAlt", value); }

    internal static bool IsFreeLookHeld =>
        CommanderShortcutInput.IsPressed(CameraFreeLook) || CommanderShortcutInput.IsPressed(CameraFreeLookAlt);
    internal static KeyboardShortcut CameraBoost { get => GetShortcut("CameraBoost", KeyCode.LeftShift, "Hold for faster RTS camera movement."); set => Set("Keybinds", "CameraBoost", value); }
    internal static KeyboardShortcut MapBoxSelect { get => GetShortcut("MapBoxSelect", KeyCode.LeftControl, "Hold while dragging on the map to draw a selection box; a plain drag pans the map."); set => Set("Keybinds", "MapBoxSelect", value); }
    internal static KeyboardShortcut TogglePlayerCommander { get => GetShortcut("TogglePlayerCommander", KeyCode.None, "Toggle the AI commander for your own faction."); set => Set("Keybinds", "TogglePlayerCommander", value); }
    // Q and E on purpose, the RTS convention, even though they are also the camera's rise and
    // descend: while a building is on the cursor the ghost takes the keys and the camera stands
    // down (CommanderBuildPreview.IsPlacementRotationKey), and gets them straight back afterwards.
    internal static KeyboardShortcut PlacementRotateLeft { get => GetShortcut("PlacementRotateLeft", KeyCode.Q, "Turn the building being placed anti-clockwise. Only while a placement is armed."); set => Set("Keybinds", "PlacementRotateLeft", value); }
    internal static KeyboardShortcut PlacementRotateRight { get => GetShortcut("PlacementRotateRight", KeyCode.E, "Turn the building being placed clockwise. Only while a placement is armed."); set => Set("Keybinds", "PlacementRotateRight", value); }
    // R for "rest it on the ground". Free in RTS mode, and like the rotate keys it is only read
    // while a placement is armed.
    internal static KeyboardShortcut PlacementConformGround { get => GetShortcut("PlacementConformGround", KeyCode.R, "Toggle laying the building being placed flat on the slope instead of standing it upright."); set => Set("Keybinds", "PlacementConformGround", value); }

    internal static string AirCommandMode { get => Get("Air Command", "MissionMode", "AirGuard"); set => Set("Air Command", "MissionMode", value); }
    internal static string AirLoadoutBalance { get => Get("Air Command", "LoadoutBalance", "Primary"); set => Set("Air Command", "LoadoutBalance", value); }
    internal static float AirTargetAltitude { get => Get("Air Command", "TargetAltitude", 0f); set => Set("Air Command", "TargetAltitude", value); }
    internal static bool AirGuardTargetOrdnance { get => Get("Air Command", "AirGuardTargetOrdnance", false); set => Set("Air Command", "AirGuardTargetOrdnance", value); }
    internal static bool AradSaturationAttack { get => Get("Air Command", "AradSaturationAttack", false); set => Set("Air Command", "AradSaturationAttack", value); }
    // Off = a commander-launched AI airframe enters the map already airborne over its base (the
    // default since the AI pilot was seen ejecting on highway-strip taxi and takeoff). On = it
    // spawns in a hangar and taxis out like a mission-authored aircraft. Applies to every
    // commander: the player's AIR window, the player-side AI and the enemy AI alike.
    // Taxi queues (user, 2026-09-14). AirLaunchQueueMax: with more than this many aircraft on a
    // base's deck, the next AI launch spawns airborne at the map edge nearest the base instead of
    // joining the queue. StuckOnDeckMinutes: an AI aircraft that has sat on a deck this long with a
    // mission and has never been airborne is despawned and its price refunded.
    internal static int AirLaunchQueueMax { get => Get("Gameplay", "AirLaunchQueueMax", 3); set => Set("Gameplay", "AirLaunchQueueMax", value); }
    internal static float StuckOnDeckMinutes { get => Get("Gameplay", "StuckOnDeckMinutes", 4f); set => Set("Gameplay", "StuckOnDeckMinutes", value); }
    internal static bool AiAircraftLaunchFromHangar { get => Get("Gameplay", "AiAircraftLaunchFromHangar", false); set => Set("Gameplay", "AiAircraftLaunchFromHangar", value); }
    // Off by default, and applied to the AI commanders' loadouts too: a pilot with cannon rounds left
    // counts them as ordnance and keeps making gun runs instead of returning when the real weapons
    // are spent, which is what made RTB look ignored on gun-armed airframes.
    // Key renamed from IncludeInternalCannons on 2026-09-14: BepInEx keeps a saved value over a
    // changed default, and the old key had been saved as true before the default flipped to false,
    // so aircraft kept spawning with cannon rounds (user: "we need that remove cannon ammunition to
    // be active by default"). The new key starts from the false default on every install.
    internal static bool AirIncludeInternalCannons { get => Get("Air Command", "EquipInternalCannons", false); set => Set("Air Command", "EquipInternalCannons", value); }
    internal static float AwacsRadiusKm { get => Get("Air Command", "AwacsRadiusKm", 60f); set => Set("Air Command", "AwacsRadiusKm", value); }
    internal static float CasRadiusKm { get => Get("Air Command", "CasRadiusKm", 20f); set => Set("Air Command", "CasRadiusKm", value); }
    internal static float AirGuardRadiusKm { get => Get("Air Command", "AirGuardRadiusKm", 30f); set => Set("Air Command", "AirGuardRadiusKm", value); }
    internal static float AradRadiusKm { get => Get("Air Command", "AradRadiusKm", 50f); set => Set("Air Command", "AradRadiusKm", value); }
    internal static float StrikeRadiusKm { get => Get("Air Command", "StrikeRadiusKm", 80f); set => Set("Air Command", "StrikeRadiusKm", value); }

    internal static void Initialize(ConfigFile configFile)
    {
        config = configFile;
        _ = ModEnabled;
        _ = PersistStateAcrossReload;
        _ = LimitToFactionRoster;
        _ = ShowCommandButton;
        _ = PrimaryAction;
        _ = SecondaryAction;
        _ = AddToSelection;
        _ = RepeatDeployment;
        _ = DeleteUnitModifier;
        _ = CameraCenterFollow;
        _ = ToggleUi;
        _ = CameraForward;
        _ = CameraBackward;
        _ = CameraLeft;
        _ = CameraRight;
        _ = CameraUp;
        _ = CameraDown;
        _ = PlacementRotateLeft;
        _ = PlacementRotateRight;
        _ = PlacementConformGround;
        _ = CameraFreeLook;
        _ = CameraFreeLookAlt;
        _ = CameraBoost;
        _ = MapBoxSelect;
        _ = QueueWaypoint;
        _ = AssignGroupModifier;
        _ = StopOrder;
        _ = SelectSameType;
        _ = CycleIdleUnit;
        _ = AutoFollowSelection;
        _ = GroupHotkeys;
        _ = CameraBookmarks;
        _ = OrderFeedback;
        _ = RetargetAfterKill;
        _ = AttackMoveIntoRange;
        _ = AttackMoveRoutes;
        _ = GuardOrders;
        _ = AutoRetreatDamaged;
        _ = RetreatConditionPercent;
        _ = WaypointHoldSeconds;
        _ = FormationShape;
        _ = FormationCohesionMeters;
        _ = CombatAlerts;
        _ = EnemyCommanderMode;
        _ = EnemyIncomeMultiplier;
        _ = PlayerCommanderEnabled;
        _ = PlayerCommanderHandsOffMinutes;
        _ = AiAircraftLaunchFromHangar;
        _ = AirLaunchQueueMax;
        _ = StuckOnDeckMinutes;
        _ = TacticalMapSize;
        _ = MapDragSpeed;
        _ = UiScaleOverride;
        _ = AutoFrameSelection;
        _ = CameraPanSpeed;
        _ = CameraZoomSpeed;
        _ = CameraLookSensitivity;
        _ = CameraSmoothing;
        _ = CameraHeightScaledSpeed;
        _ = CameraEdgeScroll;
        _ = CameraOrbitLook;
        _ = FollowSmoothing;
        _ = FollowLeadSeconds;
        _ = ShowBuildUi;
        _ = GoldMineCost;
        _ = GoldMineIncomePerMinute;
        _ = FactoryUpgradeCost;
        _ = FactoryBuildCost;
        _ = FactoryProductionSeconds;
        _ = FactoriesEnabled;
        _ = BuildingCostMultiplier;
        _ = RepairCrewCost;
        _ = BuildRadiusKm;
        _ = NavalDockRadiusKm;
        _ = NavalDockShoreMeters;
        _ = NavalDockCost;
        _ = NavalDockUpgradeCost;
        _ = AircraftCaptureStrength;
        _ = SamScanQueriesPerFrame;
        _ = PointsFillGridMeters;
        _ = PointsSiteMinSpacingMeters;
        _ = PointsVillageClusterMeters;
        _ = PointsVillageMinBuildings;
        _ = PointsVillageRadiusMeters;
        _ = PointsHilltopGridMeters;
        _ = PointsHilltopRingMeters;
        _ = PointsHilltopProminenceMeters;
        _ = PointsHilltopRadiusMeters;
        _ = PointsHilltopVillageExclusionMeters;
        _ = PointsOutpostRadiusMeters;
        _ = PointsCrossroadsRadiusMeters;
        _ = PointsRoadsideRadiusMeters;
        _ = PointsRoadsideSpacingMeters;
        _ = PointsCrossroadsMinRoads;
        _ = PointsRoadJunctionMergeMeters;
        _ = PointsLevelnessProbeRadiusMeters;
        _ = PointsMaxPointHeightSpreadMeters;
        _ = PointsMaxPointSlopeDegrees;
        _ = PointsMaxNonBasePoints;
        _ = PointsMaxResourceSites;
        _ = PointsMaxCrossroads;
        _ = PointsMaxOutposts;
        _ = PointsMaxRoadPoints;
        _ = PointsCrossroadsSpacingMeters;
        _ = PointsPointMinSpacingMeters;
        _ = PointsMaxRoadDistanceMeters;
        _ = PointsAirbaseExclusionMeters;
        _ = PointsMinGarrison;
        _ = PointsHoldSeconds;
        _ = StrategicGarrisonPaid;
        _ = PointsBaseIncomePerMinute;
        _ = PointsVillageIncomePerMinute;
        _ = PointsHilltopIncomePerMinute;
        _ = PointsOutpostIncomePerMinute;
        _ = PointsCrossroadsIncomePerMinute;
        _ = PointsRoadsideIncomePerMinute;
        _ = PointsMineSnapMeters;
        _ = OperationsPlatoonSize;
        _ = OperationsDebugLog;
        _ = OperationsReservePlatoons;
        _ = OperationsRecipeArmour;
        _ = OperationsRecipeCarrier;
        _ = OperationsRecipeAirDefence;
        _ = OperationsFrontRangeMeters;
        _ = OperationsMaxPreemptiveAirObjectives;
        _ = OperationsFobShare;
        _ = MaxAttacks;
        _ = AttacksPerPlatoon;
        _ = OperationsPressureIntervalMinutes;
        _ = OperationsOffensiveSpendFraction;
        _ = CasLossCooldownMinutes;
        _ = AirborneCeiling;
        _ = AirborneIncomePerAirframe;
        _ = AirborneCeilingMax;
        _ = PoolIdleCap;
        _ = IdleReserveMinutes;
        _ = PoolSellRefundFraction;
        _ = GroundUnitCeiling;
        _ = AirPatrolReserve;
        _ = QuietGroundMinutes;
        _ = PlatoonReseatSavingMinutes;
        _ = DownedPilotRescueMinutes;
        _ = PackageFormUpSeconds;
        _ = HeliCasRangeMeters;
        _ = AradClusterMinimum;
        _ = AwacsMinEnemyDistanceMeters;
        _ = StrikeIntervalMinutes;
        _ = StrikePointCooldownMinutes;
        _ = StrikeRangeMeters;
        _ = StrikeLoiterMinutes;
        _ = StrikeSortieMaxMinutes;
        _ = TypeShareCap;
        _ = CheapAirframeEasyJobs;
        _ = CheapAirframePadding;
        _ = CapBandLowMeters;
        _ = CapBandMidMeters;
        _ = CapBandHighMeters;
        _ = OperationsHeliInsertionEnabled;
        _ = OperationsHeliInsertionOffRoadMeters;
        _ = OperationsHeliInsertionLimit;
        _ = OperationsHeliInsertionRangeMeters;
        _ = OperationsHeliInsertionEnemyStandoffMeters;
        _ = FobEnemyStandoffMeters;
        _ = OperationsHeliInsertionCooldownMinutes;
        _ = OperationsLzClearRadiusMeters;
        _ = OperationsLzSearchRadiusMeters;
        _ = OperationsInsertionStallTimeoutSeconds;
        _ = SupplyUnloadInPlaceEnabled;
        _ = DepotReachMeters;
        _ = FobEnabled;
        _ = FobMinDepotDistanceMeters;
        _ = FobMinSpacingMeters;
        _ = FobLossCooldownMinutes;
        _ = AirMobileDriveMinutes;
        _ = RoadDetourFactor;
        _ = GroundSpeedMetersPerSecond;
        _ = LiftLoadsPerPlatoon;
        _ = LiftEscortMinimum;
        _ = LiftEscortLeadMeters;
        _ = LiftFundsMultiple;
        _ = LiftAirheadMaxMeters;
        _ = StandoffContactMemorySeconds;
        _ = LiftAbortStandoffMeters;
        _ = FobAbortStandoffMeters;
        _ = LogisticsWatchSeconds;
        _ = LiftHopelessMinutes;
        _ = AirPostureRingMeters;
        _ = AirFallbackMargin;
        _ = AirReengageMargin;
        _ = AirFallbackDistanceMeters;
        _ = AirFallbackGiveUpMinutes;
        _ = AirSurvivalFuelFraction;
        _ = AirBeltHoldRadiusMeters;
        _ = PackageFormUpStandoffMeters;
        _ = DefenceArcStandoffMeters;
        _ = BoundMeters;
        _ = OffRoadRangeMeters;
        _ = HomeCapBaseline;
        _ = HomeCapPerEnemyAircraft;
        _ = HomeCapMax;
        _ = LadderPlatoonWeight;
        _ = LadderPicketWeight;
        _ = LadderBuildingWeight;
        _ = LadderRungFloorPercent;
        _ = AirCommandMode;
        _ = AirIncludeInternalCannons;
        _ = AwacsRadiusKm;
        _ = CasRadiusKm;
        _ = AirGuardRadiusKm;
        _ = AradRadiusKm;
        _ = StrikeRadiusKm;
        _ = HealthDiagnosticLine;
    }

    private static KeyboardShortcut GetShortcut(string key, KeyCode defaultKey, string description)
    {
        if (config == null) return new KeyboardShortcut(defaultKey);
        string lookup = "Keybinds/" + key;
        if (entries.TryGetValue(lookup, out ConfigEntryBase existing))
        {
            return ((ConfigEntry<KeyboardShortcut>)existing).Value;
        }

        ConfigEntry<KeyboardShortcut> created = config.Bind(
            "Keybinds",
            key,
            new KeyboardShortcut(defaultKey),
            new ConfigDescription(description + " Set the main key to None to disable it."));
        entries.Add(lookup, created);
        return created.Value;
    }

    private static T Get<T>(string section, string key, T defaultValue)
    {
        ConfigEntry<T>? entry = GetEntry(section, key, defaultValue);
        return entry == null ? defaultValue : entry.Value;
    }

    private static void Set<T>(string section, string key, T value)
    {
        ConfigEntry<T>? entry = GetEntry(section, key, value);
        if (entry != null) entry.Value = value;
    }

    /// <summary>Keys already reported as bound under two types, so the error is written once.</summary>
    private static readonly HashSet<string> typeMismatchReported = new();

    private static ConfigEntry<T>? GetEntry<T>(string section, string key, T defaultValue)
    {
        if (config == null) return null;
        string lookup = section + "/" + key;
        if (entries.TryGetValue(lookup, out ConfigEntryBase existing))
        {
            if (existing is ConfigEntry<T> typed)
            {
                return typed;
            }

            // A getter and a setter binding one key under two types is a programming error; it is
            // reported once and the call is a no-op rather than an exception thrown from inside
            // OnGUI every frame (fix, 2026-09-15).
            if (typeMismatchReported.Add(lookup))
            {
                CommanderPlugin.Log.LogError(
                    $"Settings: {lookup} is bound as {existing.SettingType.Name} but was asked for as {typeof(T).Name}; "
                        + "fix the property so both sides use one type.");
            }

            return null;
        }

        ConfigEntry<T> created = config.Bind(section, key, defaultValue);
        entries.Add(lookup, created);
        return created;
    }
}
