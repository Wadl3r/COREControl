using System.Collections.Generic;
using NuclearOption.Networking;
using UnityEngine;

namespace GroundControlRts;

/// <summary>What the next placement click builds.</summary>
internal enum CommanderBuildKind
{
    None,
    Mine,
    Factory,

    /// <summary>Any other building out of the encyclopedia catalogue.</summary>
    Structure,

    /// <summary>The shoreline dock that unlocks naval purchases, three levels of them.</summary>
    NavalDock,
}

/// <summary>
/// The economy layer: gold mines that pay their owner a standing income, factories that feed the
/// unit reserve, and the upgrades that multiply what each of them produces.
/// <para>
/// Both sides play by these rules. The player spends through the BUILD window; hostile factions
/// spend through <see cref="SpendEnemyStructures"/>, called by the priority ladder's rung 4 out of
/// the enemy commander's review (user decision 2026-09-14) — one pot, one ladder, and this service
/// is the rung that buys the ability to buy more units.
/// </para>
/// </summary>
/// <remarks>
/// ponytail: levels are keyed on the live <see cref="Unit"/>, so they die with the building and do
/// not survive a mission reload. That is the intended stake — bomb the mine, take the income away.
/// Persisting them would mean writing our own state into the mission save.
/// </remarks>
internal sealed partial class CommanderEconomyService
    : ICommanderTickActive, ICommanderTickPersistent, ICommanderDeactivate, ICommanderResetSession, ICommanderPersistState
{
    internal const int MaxLevel = 3;

    /// <summary>What a built mine calls itself, whatever industrial prefab it is wearing.</summary>
    internal const string MineDisplayName = "Gold Mine";

    private const float IncomeIntervalSeconds = 15f;
    private const float FactoryRefreshIntervalSeconds = 5f;

    /// <summary>Income ticks between two per-faction income lines — eight ticks is two minutes
    /// (diagnostic, 2026-09-14). The user's "we own half the map, income should be huge" could not
    /// be answered from the 2026-09-14 log at all: the balance was visible on every ladder line and
    /// the income behind it on none of them, so a side that is poor and a side that is spending
    /// everything it earns read identically.</summary>
    private const int IncomeReportEveryTicks = 8;

    /// <summary>Income ticks since the last income line.</summary>
    private int incomeReportTicks;

    /// <summary>Mines an enemy commander builds before it starts upgrading what it has.</summary>
    private const int EnemyMineTarget = 2;

    /// <summary>Factories an enemy commander builds before it starts upgrading what it has.</summary>
    private const int EnemyFactoryTarget = 1;

    /// <summary>
    /// The duel's targets. A commander capped at two mines and one factory falls behind a player
    /// who keeps building, so on the mod's own map it is allowed to keep pace.
    /// </summary>
    private const int DuelEnemyMineTarget = 4;
    private const int DuelEnemyFactoryTarget = 2;

    /// <summary>
    /// Reserve multiple a commander keeps clear of an economy purchase. Faction balances are
    /// mission-authored and scale-free, so this is a ratio rather than an absolute floor.
    /// </summary>
    private const float EnemyReserveMultiple = 4f;

    /// <summary>Buildings the mine prefers to look like, best first.</summary>
    private static readonly string[] MinePrefabKeys =
    {
        "refinery_main",
        "enrichmentPlant1",
        "storageTank",
        "factory_large",
    };

    /// <summary>Buildings a built factory prefers to look like, best first.</summary>
    private static readonly string[] FactoryPrefabKeys =
    {
        "factory_large",
        "factory_tall",
    };

    /// <summary>What a built naval dock calls itself, whatever prefab it is wearing.</summary>
    internal const string NavalDockDisplayName = "Naval Dock";

    /// <summary>
    /// Buildings a naval dock prefers to look like, best first. The game ships no dock, so the
    /// harbour crane stands in for one — it is the only structure that reads as a quayside.
    /// </summary>
    private static readonly string[] NavalDockPrefabKeys =
    {
        "HarborCrane",
        "TowerCrane",
        "Platform_large",
        // "VehicleDepot1" was the fourth key (removed 2026-09-14): on maps without the three
        // cranes the dock CLAIMED the game's only vehicle depot prefab, and the catalogue drops
        // the dock's prefab so a player cannot buy a harbour by accident — which is how the whole
        // DEPOT category vanished from the build list, the FOB recipe and the captured-base wish
        // ("No DEPOT structure in the encyclopedia" beside "Naval dock will be built from the
        // 'VehicleDepot1' prefab" in the same log).
    };

    private readonly Dictionary<Unit, int> mineLevels = new();
    private readonly Dictionary<Unit, int> factoryLevels = new();
    private readonly Dictionary<Unit, int> dockLevels = new();
    private readonly List<Factory> friendlyFactories = new();
    private readonly List<Unit> friendlyMines = new();
    private readonly List<Unit> friendlyDocks = new();
    private readonly List<Unit> staleUnits = new();
    private readonly List<VehicleDefinition> productionOptions = new();
    private readonly List<VehicleDefinition> enemyProductionOptions = new();
    private readonly HashSet<Unit> builtUnits = new();
    private readonly CommanderBuildPreview preview = new();

    private BuildingDefinition? mineDefinition;
    private bool mineDefinitionResolved;
    private BuildingDefinition? factoryDefinition;
    private bool factoryDefinitionResolved;
    private BuildingDefinition? dockDefinition;
    private bool dockDefinitionResolved;
    private CommanderBuildKind pendingBuild;
    private BuildingDefinition? pendingStructure;
    private int productionIndex;
    private int builtNameCounter;
    private float nextIncomeAt;
    private float nextFactoryRefreshAt;

    internal static CommanderEconomyService? Instance { get; private set; }

    internal CommanderEconomyService()
    {
        Instance = this;
        nextIncomeAt = CommanderScheduler.Stagger("economy.income", IncomeIntervalSeconds);
    }

    internal string StatusText { get; private set; } = string.Empty;

    internal CommanderBuildKind PendingBuild => pendingBuild;

    internal bool AwaitingPlacement => pendingBuild != CommanderBuildKind.None;

    /// <summary>
    /// What the status line says while a placement is armed: the reason the site under the cursor
    /// is blocked, or the go-ahead. Falls back to <see cref="StatusText"/> when nothing is armed.
    /// </summary>
    internal string PlacementStatus
    {
        get
        {
            if (!AwaitingPlacement)
            {
                return StatusText;
            }

            return preview.SiteValid
                ? $"Click to site the {PendingBuildLabel()}, facing {preview.PlacementHeadingLabel}°, "
                    + $"{preview.PlacementTiltLabel}."
                : preview.BlockedReason;
        }
    }

    /// <summary>The building the armed placement would put down, whichever button armed it.</summary>
    private BuildingDefinition? PendingDefinition => pendingBuild switch
    {
        CommanderBuildKind.None => null,
        CommanderBuildKind.Structure => pendingStructure,
        _ => ResolveDefinition(pendingBuild),
    };

    /// <summary>
    /// Anything either commander built. The marker layer reads this so a mine you put down can be
    /// clicked and boxed like a vehicle — without it, the Basegame rule that buildings are not
    /// selectable made your own economy invisible to selection.
    /// </summary>
    internal static bool IsCommanderBuilt(Unit? unit)
    {
        return unit != null && Instance != null && Instance.builtUnits.Contains(unit);
    }

    internal bool IsBuiltMine(Unit? unit)
    {
        return unit != null && mineLevels.ContainsKey(unit);
    }

    /// <summary>
    /// The one siting rule, forwarded so discovery and the strategic point service can ask it with
    /// <paramref name="hq"/> null (site-finding, radius check off) exactly like the enemy build
    /// site search does, without either of them reaching into <see cref="CommanderBuildPreview"/>
    /// directly (Reuse rule 4 — one definition, every caller goes through it).
    /// </summary>
    internal bool IsSiteAllowed(BuildingDefinition d, GlobalPosition p, FactionHQ? hq, out string reason)
        => preview.IsSiteAllowed(d, p, hq, out reason);

    /// <summary>The building definition a gold mine is built from, resolved once per mission. Null
    /// until the encyclopedia has answered, which is also why discovery waits on it.</summary>
    internal BuildingDefinition? MineDefinition => ResolveDefinition(CommanderBuildKind.Mine);

    internal bool IsBuiltNavalDock(Unit? unit)
    {
        return unit != null && dockLevels.ContainsKey(unit);
    }

    /// <summary>Naval docks the local faction owns, for the BUILD window list.</summary>
    internal IReadOnlyList<Unit> NavalDocks => friendlyDocks;

    /// <summary>
    /// The best dock a faction holds, 0 for none. This is the whole naval gate: level 0 buys no
    /// ships at all, and each level up opens the next class. Both commanders read it.
    /// </summary>
    internal static int GetNavalDockLevel(FactionHQ? hq)
    {
        CommanderEconomyService? service = Instance;
        if (service == null || hq == null)
        {
            return 0;
        }

        int best = 0;
        foreach (KeyValuePair<Unit, int> entry in service.dockLevels)
        {
            if (entry.Key != null && !entry.Key.disabled && entry.Key.NetworkHQ == hq && entry.Value > best)
            {
                best = entry.Value;
            }
        }

        return best;
    }

    internal int GetNavalDockLevel(Unit dock)
    {
        return dockLevels.TryGetValue(dock, out int level) ? level : 0;
    }

    /// <summary>
    /// Where a faction's best dock stands. Ships enter the map at a sea lane, and the lane picked
    /// used to be scored off the nearest airbase, which on a map whose coast runs the other way put
    /// a purchased hull an entire map away from the dock that paid for it. The dock is the harbour,
    /// so the dock is the anchor.
    /// </summary>
    internal static bool TryGetNavalDockPosition(FactionHQ? hq, out GlobalPosition position)
    {
        position = default;
        CommanderEconomyService? service = Instance;
        if (service == null || hq == null)
        {
            return false;
        }

        int best = 0;
        foreach (KeyValuePair<Unit, int> entry in service.dockLevels)
        {
            if (entry.Key != null && !entry.Key.disabled && entry.Key.NetworkHQ == hq && entry.Value > best)
            {
                best = entry.Value;
                position = entry.Key.GlobalPosition();
            }
        }

        return best > 0;
    }

    /// <summary>True for the prefab the dock button places, which is what gives it the shore rule.</summary>
    internal static bool IsNavalDockDefinition(BuildingDefinition? definition)
    {
        return definition != null
            && Instance != null
            && ReferenceEquals(definition, Instance.ResolveDefinition(CommanderBuildKind.NavalDock));
    }

    /// <summary>True for the prefab the mine button places, which is what gives it the
    /// site-only rule in <see cref="CommanderBuildPreview.Evaluate"/>.</summary>
    internal static bool IsMineDefinition(BuildingDefinition? definition)
    {
        return definition != null
            && Instance != null
            && ReferenceEquals(definition, Instance.ResolveDefinition(CommanderBuildKind.Mine));
    }

    internal static float NavalDockBuildCost => Mathf.Max(0f, CommanderSettings.NavalDockCost);

    /// <summary>Cost of taking a dock from <paramref name="level"/> to the next one.</summary>
    internal static float GetNavalDockUpgradeCost(int level)
    {
        return Mathf.Max(0f, CommanderSettings.NavalDockUpgradeCost) * level;
    }

    /// <summary>Friendly factories that actually produce a vehicle, refreshed while RTS mode is up.</summary>
    internal IReadOnlyList<Factory> Factories => friendlyFactories;

    internal IReadOnlyList<Unit> Mines => friendlyMines;

    internal static float MineBuildCost => Mathf.Max(0f, CommanderSettings.GoldMineCost);

    internal static float FactoryBuildCost => Mathf.Max(0f, CommanderSettings.FactoryBuildCost);

    /// <summary>Seconds between production cycles at a factory this mod built.</summary>
    internal static float FactoryProductionSeconds => Mathf.Max(30f, CommanderSettings.FactoryProductionSeconds);

    internal static float GetBuildCost(CommanderBuildKind kind)
    {
        return kind switch
        {
            CommanderBuildKind.Factory => FactoryBuildCost,
            CommanderBuildKind.NavalDock => NavalDockBuildCost,
            _ => MineBuildCost,
        };
    }

    /// <summary>What the armed placement will charge, catalogue prices included.</summary>
    internal float PendingBuildCost => pendingBuild == CommanderBuildKind.Structure
        ? GetStructureCost(pendingStructure)
        : GetBuildCost(pendingBuild);

    /// <summary>Cost of taking a mine from <paramref name="level"/> to the next one.</summary>
    internal static float GetMineUpgradeCost(int level)
    {
        return MineBuildCost * (level + 1);
    }

    internal static float GetMineIncomePerMinute(int level)
    {
        return Mathf.Max(0f, CommanderSettings.GoldMineIncomePerMinute) * level;
    }

    /// <summary>Cost of taking a factory from <paramref name="level"/> to the next one.</summary>
    internal static float GetFactoryUpgradeCost(int level)
    {
        return Mathf.Max(0f, CommanderSettings.FactoryUpgradeCost) * level;
    }

    /// <summary>Units a factory adds to the reserve per production cycle. Stock factories are 1.</summary>
    internal static int GetFactoryOutput(Unit? attachedUnit)
    {
        CommanderEconomyService? service = Instance;
        if (service == null || attachedUnit == null)
        {
            return 1;
        }

        return service.factoryLevels.TryGetValue(attachedUnit, out int level) ? level : 1;
    }

    /// <summary>
    /// One runnable check for the price ladder, run at plugin load next to the other self-checks
    /// because a Unity plugin has nowhere else to run a test. The two things that actually break
    /// the economy if a default is retuned badly: an upgrade that gets cheaper as it gets stronger,
    /// and a factory nobody upgraded reporting anything other than its stock output of one.
    /// </summary>
    internal static void SelfCheck()
    {
        if (!Mathf.Approximately(IncomeHandicapFor(true, 1.3f), 1f)
            || !Mathf.Approximately(IncomeHandicapFor(false, 1.3f), 1.3f)
            || !Mathf.Approximately(IncomeHandicapFor(false, -2f), 0f))
        {
            CommanderPlugin.Log.LogError("Economy self-check FAILED: the enemy income handicap no longer spares the player's faction or floors at zero.");
        }

        // The other half of the same slider: opening balances, and the once-per-match rule that
        // stops a mid-match change re-opening one. The same rule is what a strategic load leans on:
        // it restores the "already prepared" flag so this table's second half keeps holding after a
        // mission restart (see CommanderEnemyCommanderStrategicPersist).
        CheckDifficulty();

        // The strategic save's building valuation, which is the half of the war chest that could
        // quietly print money (CommanderEconomyServiceStrategicPersist).
        System.Collections.Generic.List<string> strategicFailures = new();
        CheckStrategicValuation(strategicFailures);
        for (int i = 0; i < strategicFailures.Count; i++)
        {
            CommanderPlugin.Log.LogError($"Economy self-check FAILED: {strategicFailures[i]}");
        }

        if (MayStandInForDock(BuildingType.DEP) || MayStandInForDock(BuildingType.HGR) || MayStandInForDock(BuildingType.RDR)
            || !MayStandInForDock(BuildingType.CIV))
        {
            CommanderPlugin.Log.LogError(
                "Economy self-check FAILED: the naval dock may borrow a depot, hangar or radar prefab, which takes that category off the build list.");
        }

        for (int level = 1; level < MaxLevel; level++)
        {
            if (GetMineUpgradeCost(level) <= GetMineUpgradeCost(level - 1)
                || GetMineIncomePerMinute(level + 1) <= GetMineIncomePerMinute(level)
                || GetFactoryUpgradeCost(level + 1) <= GetFactoryUpgradeCost(level))
            {
                CommanderPlugin.Log.LogError(
                    $"Economy self-check FAILED: the level {level} price ladder is not increasing. "
                        + "Check the Economy section of the config.");
                return;
            }
        }

        if (GetFactoryOutput(null) != 1)
        {
            CommanderPlugin.Log.LogError("Economy self-check FAILED: an unupgraded factory is not stock.");
        }

        // The build buttons charge whatever this returns, so a swapped case would bill a factory
        // at mine prices.
        if (GetBuildCost(CommanderBuildKind.Mine) != MineBuildCost
            || GetBuildCost(CommanderBuildKind.Factory) != FactoryBuildCost
            || GetBuildCost(CommanderBuildKind.NavalDock) != NavalDockBuildCost
            || GetBuildCost(CommanderBuildKind.None) != MineBuildCost)
        {
            CommanderPlugin.Log.LogError("Economy self-check FAILED: build costs are crossed.");
        }

        // The dock ladder is the naval gate: every ship class has to be reachable at some level,
        // and level 0 has to buy nothing, or the dock is either pointless or a hard wall.
        if (CommanderNavalPurchaseService.CountShipTypesAtLevel(0) != 0
            || CommanderNavalPurchaseService.CountShipTypesAtLevel(MaxLevel)
                != System.Enum.GetValues(typeof(ShipType)).Length)
        {
            CommanderPlugin.Log.LogError(
                "Economy self-check FAILED: the naval dock ladder does not run from nothing to every ship.");
        }

        // The FOB's price (design.md, fob-construction_20260914 Section 5): the fixed recipe is
        // every structure or none, so a catalogue short of one category must price the order at
        // zero rather than at part of a base.
        if (FobPrice(10f, 20f, 30f, 1) != 60f
            || FobPrice(0f, 20f, 30f, 1) != 0f
            || FobPrice(10f, 0f, 30f, 1) != 0f
            || FobPrice(10f, 20f, 0f, 1) != 0f)
        {
            CommanderPlugin.Log.LogError(
                "Economy self-check FAILED: the FOB price is not the sum of its structures, "
                    + "or a missing structure does not disable the order.");
        }

        // Every pad in the recipe is charged for (user instruction 2026-09-16, two helipads per
        // FOB). A second pad that cost nothing would let the building rung order a base it cannot
        // actually finish paying for.
        if (FobPrice(10f, 20f, 30f, 2) != 90f
            || FobPrice(10f, 20f, 30f, 0) != 0f)
        {
            CommanderPlugin.Log.LogError(
                "Economy self-check FAILED: the FOB price does not charge for every helipad in the "
                    + "recipe, or a padless recipe is still priced.");
        }

        // The price and the recipe must agree about how many pads there are. The check above pins
        // the arithmetic for a pad count handed to it; this one pins the count itself to the recipe
        // array, so dropping a pad's price, or adding a pad to the recipe without paying for it,
        // breaks a named line at load rather than quietly ordering a base the commander cannot
        // finish paying for.
        if (FobRecipePrice(10f, 20f, 30f) != 10f + 20f + (30f * FobHelipadCount)
            || FobRecipePrice(10f, 20f, 30f) != 90f)
        {
            CommanderPlugin.Log.LogError(
                "Economy self-check FAILED: the FOB price and the FOB recipe disagree about how "
                    + "many helipads a forward base has.");
        }

        // Base defences are counted PER BASE (fix, 2026-09-14). A captured airfield with nothing on
        // it wants the whole target however many emplacements stand at the home field, and a base
        // already over target never wants a negative one.
        if (BuildingsStillWantedAtBase(0, EnemyDefenceBuildingTarget) != EnemyDefenceBuildingTarget
            || BuildingsStillWantedAtBase(EnemyDefenceBuildingTarget, EnemyDefenceBuildingTarget) != 0
            || BuildingsStillWantedAtBase(EnemyDefenceBuildingTarget + 9, EnemyDefenceBuildingTarget) != 0
            || EnemyDefenceBuildingTarget < 1)
        {
            CommanderPlugin.Log.LogError(
                "Economy self-check FAILED: the per-base defence target no longer asks an undefended "
                    + "base for its own buildings.");
        }

        // A coverage ring narrower than the ring the builds are dropped into would never count the
        // building it just placed, so the commander would build at the same base for ever.
        if (Mathf.Min(BaseBuildingCoverageMeters, RadarCoverageMeters) <= BaseSiteMaxMeters)
        {
            CommanderPlugin.Log.LogError(
                "Economy self-check FAILED: a base-building coverage ring is inside the siting ring, "
                    + "so a structure the commander builds can never satisfy the base that wanted it.");
        }

        // The captured-base wish order (design Section 4): the depot comes before the pad, because a
        // base that deploys nothing cannot hold the ground it stands on.
        if (NextBaseFacility(true, true) != CommanderBaseFacility.Depot
            || NextBaseFacility(true, false) != CommanderBaseFacility.Depot
            || NextBaseFacility(false, true) != CommanderBaseFacility.Pad
            || NextBaseFacility(false, false) != CommanderBaseFacility.None)
        {
            CommanderPlugin.Log.LogError(
                "Economy self-check FAILED: a captured base's depot no longer outranks its landing pad.");
        }

        // The catalogue files every building under its own BuildingType, so a game patch that adds
        // one would silently file a whole category under OTHER.
        foreach (BuildingType type in System.Enum.GetValues(typeof(BuildingType)))
        {
            if (GetCategoryLabel(type) == "OTHER")
            {
                CommanderPlugin.Log.LogError(
                    $"Economy self-check FAILED: building category {type} has no label.");
                return;
            }
        }
    }

    internal int GetMineLevel(Unit mine)
    {
        return mineLevels.TryGetValue(mine, out int level) ? level : 0;
    }

    internal int GetFactoryLevel(Factory factory)
    {
        return factory.attachedUnit != null && factoryLevels.TryGetValue(factory.attachedUnit, out int level)
            ? level
            : 1;
    }

    /// <summary>Standing income the local faction earns from its mines, for the readout.</summary>
    internal float FriendlyIncomePerMinute
    {
        get
        {
            float income = 0f;
            for (int i = 0; i < friendlyMines.Count; i++)
            {
                income += GetMineIncomePerMinute(GetMineLevel(friendlyMines[i]));
            }
            return income;
        }
    }

    public void TickActive()
    {
        // Same escape hatch the supply and trailer placements have, so every armed placement in the
        // mod backs out the same way.
        if (AwaitingPlacement && CommanderGameInput.CancelDown)
        {
            CancelBuild();
        }

        // No ghost while the cursor is parked on a Commander window: the click cannot place there,
        // so a preview skidding around behind the BUILD list only reads as a bug.
        BuildingDefinition? pending = PendingDefinition;
        if (pending != null)
        {
            Vector2 cursor = Input.mousePosition;
            if (CommanderOverlayUi.Instance?.ContainsScreenPoint(cursor) == true)
            {
                preview.Suspend();
            }
            else
            {
                preview.Tick(pending, CommanderGameAccess.GetLocalHq(), cursor);
            }
        }

        if (CommanderScheduler.IsDue(ref nextFactoryRefreshAt, FactoryRefreshIntervalSeconds))
        {
            RefreshFriendlyLists();
        }
    }

    public void TickPersistent()
    {
        if (CommanderScheduler.IsDue(ref nextIncomeAt, IncomeIntervalSeconds))
        {
            PayIncome();
        }

        // Captured depots (design.md, fob-construction_20260914 Decision 6). On the depot rally's own
        // five-second cadence, because it answers the same question — which depots does this faction
        // actually own — and a base changing hands must not wait a 30 s review to start deploying.
        if (CommanderScheduler.IsDue(ref nextCapturedDepotSweepAt, CapturedDepotSweepSeconds))
        {
            WatchCapturedDepots();
        }

        // The enemy structure loop no longer spends on its own clock (user decision 2026-09-14, the
        // priority ladder): rung 4 is called from the enemy commander's review — the single spend
        // site — through SpendEnemyStructures, so income is this service's only clock-bound job.
    }

    public void Deactivate()
    {
        pendingBuild = CommanderBuildKind.None;
        pendingStructure = null;
        preview.Hide();
    }

    public void ResetSession()
    {
        mineLevels.Clear();
        factoryLevels.Clear();
        dockLevels.Clear();
        friendlyFactories.Clear();
        friendlyMines.Clear();
        friendlyDocks.Clear();
        staleUnits.Clear();
        productionOptions.Clear();
        builtUnits.Clear();
        preview.Clear();
        catalog.Clear();
        catalogResolved = false;
        pendingBuild = CommanderBuildKind.None;
        pendingStructure = null;
        productionIndex = 0;
        builtNameCounter = 0;
        mineDefinition = null;
        mineDefinitionResolved = false;
        factoryDefinition = null;
        factoryDefinitionResolved = false;
        dockDefinition = null;
        dockDefinitionResolved = false;
        shoreSearchReported.Clear();
        // Rung-4 savings (design.md, commander-priorities_20260914): a new mission starts a new
        // bank, like every other per-HQ state.
        structureSavings.Clear();
        // FOB construction (fob-construction_20260914): the name counter and the helipad resolver
        // are per-mission for the reason the other definition caches are — a new mission may load a
        // different encyclopedia, and two missions must not share a base's unique name.
        fobNameCounter = 0;
        rotaryHangar = null;
        rotaryHangarResolved = false;
        depotOwners.Clear();
        nextCapturedDepotSweepAt = CommanderScheduler.Stagger("economy.capturedDepots", CapturedDepotSweepSeconds);
        StatusText = string.Empty;
        nextIncomeAt = CommanderScheduler.Stagger("economy.income", IncomeIntervalSeconds);
    }

    internal void BeginBuild(CommanderBuildKind kind)
    {
        FactionHQ? hq = CommanderGameAccess.GetLocalHq();
        string label = BuildLabel(kind);
        if (hq == null)
        {
            StatusText = "No faction HQ.";
            return;
        }

        // Buildings are spawned through the server object manager, so a pure client cannot build.
        if (!hq.IsServer)
        {
            StatusText = "Only the host can construct buildings.";
            return;
        }

        if (ResolveDefinition(kind) == null)
        {
            StatusText = $"No building prefab available for a {label}.";
            return;
        }

        // A factory built while the switch is off would never produce (the ProduceUnit prefix
        // skips it), so the purchase is refused rather than charged for nothing.
        if (kind == CommanderBuildKind.Factory && !CommanderSettings.FactoriesEnabled)
        {
            StatusText = "Factories are switched off in settings (Economy / FactoriesEnabled).";
            return;
        }

        if (kind == CommanderBuildKind.Factory && SelectedProduction == null)
        {
            StatusText = "This faction has no ground unit a factory could produce.";
            return;
        }

        if (hq.factionFunds < GetBuildCost(kind))
        {
            StatusText = $"Insufficient faction funds for a {label}.";
            return;
        }

        pendingBuild = kind;
        pendingStructure = null;
        preview.ResetRotation();
        StatusText = $"Select the {label} site in the 3D world.";
    }

    internal void CancelBuild()
    {
        if (pendingBuild == CommanderBuildKind.None)
        {
            return;
        }

        StatusText = $"{PendingBuildLabel()} placement cancelled.";
        pendingBuild = CommanderBuildKind.None;
        pendingStructure = null;
        preview.Hide();
    }

    /// <summary>Placement click from <see cref="CommanderInputController"/>. True once handled.</summary>
    internal bool TryPlaceBuildingFromWorld(Vector2 screenPosition)
    {
        CommanderBuildKind kind = pendingBuild;
        if (kind == CommanderBuildKind.None)
        {
            return false;
        }

        if (!CommanderGameAccess.TryRaycastWorldPosition(screenPosition, out GlobalPosition position))
        {
            StatusText = "No valid ground under the cursor.";
            return true;
        }

        string label = PendingBuildLabel();
        BuildingDefinition? structure = pendingStructure;
        BuildingDefinition? placed = PendingDefinition;
        float cost = PendingBuildCost;
        // The rotation the ghost was showing when the click landed — the chosen heading, and the
        // slope under it when ground-conforming is on. Taken before the disarm below so the
        // building goes down sitting exactly as the player was looking at it.
        Quaternion placedRotation = preview.PlacementRotation;

        // The ghost has already said whether this site is legal; the click obeys it rather than
        // building a refinery through the highway because the player was quick on the mouse.
        if (placed != null
            && !preview.IsSiteAllowed(placed, position, CommanderGameAccess.GetLocalHq(), out string blocked))
        {
            StatusText = blocked;
            return true;
        }

        // The same repeat key the supply window uses keeps the placement armed, so a row of
        // bunkers is one trip to the BUILD window instead of five.
        bool repeat = CommanderSettings.RepeatDeployment.IsPressed();
        if (!repeat)
        {
            pendingBuild = CommanderBuildKind.None;
            pendingStructure = null;
            preview.Hide();
        }

        FactionHQ? hq = CommanderGameAccess.GetLocalHq();
        if (hq == null || !hq.IsServer || hq.factionFunds < cost)
        {
            pendingBuild = CommanderBuildKind.None;
            pendingStructure = null;
            preview.Hide();
            StatusText = $"{label} could not be built.";
            return true;
        }

        if (kind == CommanderBuildKind.Structure)
        {
            if (structure == null || !PlaceStructure(hq, position, structure, cost, placedRotation))
            {
                pendingBuild = CommanderBuildKind.None;
                pendingStructure = null;
                preview.Hide();
            }

            return true;
        }

        if (kind == CommanderBuildKind.NavalDock)
        {
            if (SpawnNavalDock(hq, position, rotation: placedRotation) == null)
            {
                pendingBuild = CommanderBuildKind.None;
                preview.Hide();
                StatusText = "Naval dock could not be built. It has to stand on the shore, within "
                    + $"{CommanderSettings.NavalDockRadiusKm:0.#} km of a base you hold.";
                return true;
            }

            hq.AddFunds(-cost);
            RefreshFriendlyLists();
            StatusText = $"Naval dock built. Level 1 unlocks {CommanderNavalPurchaseService.GetLevelUnlockLabel(1)}.";
            return true;
        }

        if (kind == CommanderBuildKind.Mine)
        {
            if (SpawnMine(hq, position, rotation: placedRotation) == null)
            {
                pendingBuild = CommanderBuildKind.None;
                preview.Hide();
                StatusText = "Gold mine could not be built.";
                return true;
            }

            hq.AddFunds(-cost);
            RefreshFriendlyLists();
            StatusText = $"Gold mine built. +{FundsLabel(GetMineIncomePerMinute(1))}/min.";
            return true;
        }

        VehicleDefinition? production = SelectedProduction;
        if (production == null || SpawnFactory(hq, position, production, rotation: placedRotation) == null)
        {
            pendingBuild = CommanderBuildKind.None;
            preview.Hide();
            StatusText = "Factory could not be built.";
            return true;
        }

        hq.AddFunds(-cost);
        RefreshFriendlyLists();
        StatusText = $"{CommanderGameAccess.GetVehicleLabel(production)} factory built. "
            + $"One unit every {FactoryProductionSeconds:0}s.";
        return true;
    }

    /// <summary>What a factory built now would produce; null when the faction offers nothing.</summary>
    internal VehicleDefinition? SelectedProduction
    {
        get
        {
            return productionOptions.Count == 0
                ? null
                : productionOptions[Mathf.Clamp(productionIndex, 0, productionOptions.Count - 1)];
        }
    }

    internal void CycleProduction(int delta)
    {
        if (productionOptions.Count == 0)
        {
            return;
        }

        productionIndex = (productionIndex + delta + productionOptions.Count) % productionOptions.Count;
    }

    private static string BuildLabel(CommanderBuildKind kind)
    {
        return kind switch
        {
            CommanderBuildKind.Factory => "factory",
            CommanderBuildKind.NavalDock => "naval dock",
            _ => "gold mine",
        };
    }

    private string PendingBuildLabel()
    {
        return pendingBuild == CommanderBuildKind.Structure && pendingStructure != null
            ? GetStructureLabel(pendingStructure)
            : BuildLabel(pendingBuild);
    }


    internal void UpgradeMine(Unit mine)
    {
        int level = GetMineLevel(mine);
        if (level <= 0 || level >= MaxLevel)
        {
            return;
        }

        FactionHQ? hq = CommanderGameAccess.GetLocalHq();
        float cost = GetMineUpgradeCost(level);
        if (hq == null || hq.factionFunds < cost)
        {
            StatusText = "Insufficient faction funds for that upgrade.";
            return;
        }

        hq.AddFunds(-cost);
        mineLevels[mine] = level + 1;
        StatusText = $"Gold mine at level {level + 1}. +{FundsLabel(GetMineIncomePerMinute(level + 1))}/min.";
    }

    internal void UpgradeFactory(Factory factory)
    {
        Unit? attached = factory.attachedUnit;
        int level = GetFactoryLevel(factory);
        if (attached == null || level >= MaxLevel)
        {
            return;
        }

        if (!CommanderSettings.FactoriesEnabled)
        {
            StatusText = "Factories are switched off in settings (Economy / FactoriesEnabled).";
            return;
        }

        FactionHQ? hq = CommanderGameAccess.GetLocalHq();
        float cost = GetFactoryUpgradeCost(level);
        if (hq == null || hq.factionFunds < cost)
        {
            StatusText = "Insufficient faction funds for that upgrade.";
            return;
        }

        hq.AddFunds(-cost);
        factoryLevels[attached] = level + 1;
        StatusText = $"{CommanderGameAccess.GetUnitLabel(attached)} at level {level + 1}: "
            + $"{level + 1} units per cycle.";
    }

    internal static string FundsLabel(float funds)
    {
        return UnitConverter.ValueReading(funds) ?? funds.ToString("F1");
    }

    /// <summary>
    /// Pays every faction's mines. <c>factionFunds</c> is a server SyncVar, so this is a no-op on a
    /// pure multiplayer client — the host runs the economy for everyone.
    /// </summary>
    /// <summary>The income multiplier this faction earns at: <c>EnemyIncomeMultiplier</c> for every
    /// faction that is not the player's own, 1 for the player's. One definition for points, bases and
    /// mines (Reuse rule 4). Pure core in <see cref="IncomeHandicapFor"/>.</summary>
    internal static float IncomeHandicap(FactionHQ hq)
    {
        return IncomeHandicapFor(ReferenceEquals(hq, CommanderGameAccess.GetLocalHq()), CommanderSettings.EnemyIncomeMultiplier);
    }

    /// <summary>The handicap rule, pure: the player's own faction always earns at 1; another faction
    /// earns at the multiplier, floored at 0 so a mistyped negative cannot drain a treasury.</summary>
    internal static float IncomeHandicapFor(bool isLocal, float multiplier)
    {
        return isLocal ? 1f : Mathf.Max(0f, multiplier);
    }

    /// <summary>
    /// Lowest the difficulty slider goes: half money. Below half an opposing commander cannot
    /// accumulate the price of the cheapest platoon between two reviews, so it stops buying
    /// altogether and reads as a broken opponent rather than an easy one.
    /// </summary>
    internal const float EnemyDifficultyMin = 0.5f;

    /// <summary>
    /// Highest the difficulty slider goes: triple money. Picked with the floor so the fair-fight
    /// default of 1 sits inside the range rather than at its end, and because past three an
    /// opposing commander fills the map faster than the player can cross it.
    /// </summary>
    internal const float EnemyDifficultyMax = 3f;

    /// <summary>
    /// The opening balance a faction starts a match on: the figure the mission (or the duel head
    /// start) authored for the player's own faction, and that figure scaled by the difficulty
    /// multiplier for every other faction. Deliberately built on
    /// <see cref="IncomeHandicapFor"/> so there is one definition of who gets handicapped and two
    /// callers (Reuse rule 4) — the slider then means exactly one thing, whether it is paying out
    /// income or opening a treasury. Floored at zero: a mistyped negative must empty a balance,
    /// never invert it.
    /// </summary>
    internal static float StartingFundsFor(float baseFunds, bool isLocal, float multiplier)
    {
        return Mathf.Max(0f, baseFunds * IncomeHandicapFor(isLocal, multiplier));
    }

    /// <summary>The opening balance for this faction at the difficulty currently set. Same wrapper
    /// shape as <see cref="IncomeHandicap"/>, over the same rule.</summary>
    internal static float StartingFunds(FactionHQ hq, float baseFunds)
    {
        return StartingFundsFor(
            baseFunds,
            ReferenceEquals(hq, CommanderGameAccess.GetLocalHq()),
            CommanderSettings.EnemyIncomeMultiplier);
    }

    /// <summary>
    /// Whether a commander review is the one that opens a faction's treasury: only for a faction
    /// that is not the player's own, and only on that faction's first review. Pure, and checked at
    /// load, because the second half is what stops the difficulty slider handing out a second
    /// opening balance when it is moved mid-match — income changes from the next payout on, opening
    /// balances do not move at all. Called from
    /// <c>CommanderEnemyCommanderService.Review</c>, which holds the "already prepared" flag; it
    /// lives here so the whole difficulty rule — who, how much, and how often — is in one place.
    /// </summary>
    internal static bool ShouldOpenTreasury(bool isLocal, bool alreadyPrepared)
    {
        return !isLocal && !alreadyPrepared;
    }

    /// <summary>
    /// The difficulty slider at its named boundaries. Two rules are pinned. First the arithmetic:
    /// the player's own faction is never scaled, the default of 1 changes nothing at all, a higher
    /// number multiplies, and a negative empties a balance instead of inverting it. Second the
    /// timing: an opening balance is handed out once per faction per match, so moving the slider
    /// mid-match cannot re-open a treasury that has already been opened.
    /// </summary>
    private static void CheckDifficulty()
    {
        Expect(
            "the player's own opening balance ignores the difficulty slider",
            StartingFundsFor(1000f, isLocal: true, multiplier: 3f),
            1000f);
        Expect(
            "the default difficulty of 1 leaves an opposing opening balance exactly as authored",
            StartingFundsFor(1000f, isLocal: false, multiplier: 1f),
            1000f);
        Expect(
            "doubling the difficulty doubles an opposing opening balance",
            StartingFundsFor(1000f, isLocal: false, multiplier: 2f),
            2000f);
        Expect(
            "halving the difficulty halves an opposing opening balance",
            StartingFundsFor(1000f, isLocal: false, multiplier: EnemyDifficultyMin),
            500f);
        Expect(
            "a negative difficulty empties an opposing opening balance rather than inverting it",
            StartingFundsFor(1000f, isLocal: false, multiplier: -2f),
            0f);
        Expect(
            "a faction that opens on nothing still opens on nothing at any difficulty",
            StartingFundsFor(0f, isLocal: false, multiplier: EnemyDifficultyMax),
            0f);
        Expect(
            "a faction authored into the red opens on nothing rather than deeper in the red",
            StartingFundsFor(-500f, isLocal: false, multiplier: 2f),
            0f);
        Expect(
            "the difficulty slider's range still brackets the fair-fight default of 1",
            EnemyDifficultyMin > 0f && EnemyDifficultyMin < 1f && EnemyDifficultyMax > 1f,
            true);

        Expect(
            "an opposing faction's first review opens its treasury",
            ShouldOpenTreasury(isLocal: false, alreadyPrepared: false),
            true);
        Expect(
            "an opposing faction already prepared never re-opens its treasury, whatever the slider does",
            ShouldOpenTreasury(isLocal: false, alreadyPrepared: true),
            false);
        Expect(
            "the player's own faction never has its treasury opened",
            ShouldOpenTreasury(isLocal: true, alreadyPrepared: false),
            false);
        Expect(
            "the player's own faction never has its treasury re-opened either",
            ShouldOpenTreasury(isLocal: true, alreadyPrepared: true),
            false);
    }

    /// <summary>The economy's money comparison for a self-check case. The case name says which
    /// rule failed, the way the commander's own <c>Expect</c> pair does.</summary>
    private static void Expect(string name, float actual, float expected)
    {
        if (!Mathf.Approximately(actual, expected))
        {
            CommanderPlugin.Log.LogError($"Economy self-check FAILED ({name}): expected {expected}, got {actual}.");
        }
    }

    private static void Expect(string name, bool actual, bool expected)
    {
        if (actual != expected)
        {
            CommanderPlugin.Log.LogError($"Economy self-check FAILED ({name}): expected {expected}, got {actual}.");
        }
    }

    private void PayIncome()
    {
        HoldFundsInTreasury();
        // Points pay first: a faction with no mines yet still holds bases, and the mine loop below
        // returns early when there are none.
        CommanderStrategicPointService.Instance?.PayPointIncome(IncomeIntervalSeconds / 60f);
        // Before the early return below: a faction with no mines still earns from its points, and
        // that is exactly the side whose income the reader needs to see.
        ReportIncome();
        if (mineLevels.Count == 0)
        {
            return;
        }

        float share = IncomeIntervalSeconds / 60f;
        staleUnits.Clear();
        foreach (KeyValuePair<Unit, int> entry in mineLevels)
        {
            Unit mine = entry.Key;
            if (mine == null || mine.disabled)
            {
                staleUnits.Add(mine!);
                continue;
            }

            FactionHQ hq = mine.NetworkHQ;
            if (hq != null && hq.IsServer)
            {
                hq.AddFunds(GetMineIncomePerMinute(entry.Value) * share * IncomeHandicap(hq));
            }
        }

        for (int i = 0; i < staleUnits.Count; i++)
        {
            mineLevels.Remove(staleUnits[i]);
        }
        staleUnits.Clear();
    }

    /// <summary>
    /// Every server faction's income per minute and what is paying it, on the
    /// <see cref="IncomeReportEveryTicks"/> cadence and behind <c>OperationsDebugLog</c> with the
    /// rest of the commander diagnostics. The points half is the same read the COMMANDER LOG header
    /// shows (Reuse rule 4), so the log and the window can never disagree about what a side earns.
    /// </summary>
    private void ReportIncome()
    {
        if (!CommanderSettings.OperationsDebugLog)
        {
            return;
        }

        incomeReportTicks++;
        if (incomeReportTicks % IncomeReportEveryTicks != 1)
        {
            return;
        }

        CommanderStrategicPointService? points = CommanderStrategicPointService.Instance;
        foreach (FactionHQ hq in FactionRegistry.GetAllHQs())
        {
            if (hq == null || !hq.IsServer || hq.faction == null)
            {
                continue;
            }

            float pointIncome = 0f;
            CommanderStrategicPointService.PointCounts counts = default;
            if (points != null)
            {
                pointIncome = points.GetPointIncomePerMinute(hq, out counts);
            }

            float mineIncome = GetMineIncomePerMinute(hq);
            CommanderPlugin.Log.LogInfo(
                $"Ops {CommanderPlayerCommanderService.CommanderLabel(hq)}: income "
                    + $"{pointIncome + mineIncome:0}/min (points {pointIncome:0} from {counts.Bases} bases, "
                    + $"{counts.Villages} villages, {counts.Hilltops} hilltops, {counts.Outposts} outposts, "
                    + $"{counts.Crossroads} crossroads, {counts.Roadside} road points; "
                    + $"mines {mineIncome:0}), balance {hq.factionFunds:0}.");
        }
    }

    /// <summary>
    /// Stops the Basegame paying the mod's economy out to individual pilots' wallets.
    /// </summary>
    /// <remarks>
    /// <c>FactionHQ.DistributeFunds</c> runs every 30 s and hands each player
    /// <c>(factionFunds - excessFundsThreshold) * excessFundsDistributePercent</c> — a quarter of
    /// everything the faction holds above the balance the mission was authored with — on top of the
    /// mission's own regular income. In a normal match that is the point: the faction bankrolls its
    /// pilots. Under a commander it is a leak straight out of the treasury the player is trying to
    /// build up, which is exactly what "the gold mine says +20/min and the faction balance does not
    /// move" was: the mines paid in, and the very next distribution took a quarter of it back out
    /// to the personal account.
    /// <para>
    /// The threshold is a plain float, so raising it to whatever the faction now holds makes the
    /// treasury never look excessive. The mission's authored <c>regularIncome</c> allowance is left
    /// alone, so a player who also flies still gets their own money the normal way.
    /// </para>
    /// </remarks>
    private static void HoldFundsInTreasury()
    {
        foreach (FactionHQ hq in FactionRegistry.GetAllHQs())
        {
            if (hq != null && hq.IsServer && hq.factionFunds > hq.excessFundsThreshold)
            {
                hq.excessFundsThreshold = hq.factionFunds;
            }
        }
    }

    private Unit? SpawnMine(
        FactionHQ hq, GlobalPosition position, bool randomRotation = false, Quaternion? rotation = null)
    {
        // Every mine spawn path — the player's click and the enemy's build step alike — ends here,
        // so the site snap is enforced once, in the one place both of them call. A map with no
        // resource sites (none found, or discovery still running) keeps the old anywhere-in-reach
        // rule; see CommanderStrategicPointService.HasResourceSites.
        CommanderStrategicPointService? pointService = CommanderStrategicPointService.Instance;
        bool onSite = pointService != null && pointService.HasResourceSites;
        GlobalPosition site = position;
        // The builder goes in too (user decision 2026-09-14): only the faction holding the site may
        // put a mine on it, and this is the one place both the player's click and the enemy's build
        // step end up.
        if (onSite && !pointService!.TrySnapMineSite(position, out site, hq))
        {
            return null;
        }

        Unit? mine = SpawnBuilding(
            hq, site, ResolveDefinition(CommanderBuildKind.Mine), MineDisplayName, randomRotation, rotation);
        if (mine == null)
        {
            // The usual refusal on a held site is the site's own garrison standing where the mine
            // goes (user report 2026-09-14). Push it off the ring so the next attempt — the
            // commander's next review, or the player's next click — lands.
            if (onSite && pointService!.TryGetPointAt(site, out CommanderStrategicPoint? sitePoint) && sitePoint != null)
            {
                CommanderOperationsService.RequestSiteClearance(hq, sitePoint);
            }

            return null;
        }

        mineLevels[mine] = 1;
        if (onSite)
        {
            pointService!.AttachMine(site, mine);
        }

        return mine;
    }

    /// <summary>
    /// The dock that gates naval purchases. It carries no game behaviour of its own — the prefab is
    /// scenery — so everything it does lives in <see cref="GetNavalDockLevel(FactionHQ)"/>, which
    /// both the player's naval window and the enemy commander read before they buy a ship.
    /// </summary>
    private Unit? SpawnNavalDock(
        FactionHQ hq, GlobalPosition position, bool randomRotation = false, Quaternion? rotation = null)
    {
        Unit? dock = SpawnBuilding(
            hq, position, ResolveDefinition(CommanderBuildKind.NavalDock), NavalDockDisplayName, randomRotation,
            rotation);
        if (dock == null)
        {
            return null;
        }

        dockLevels[dock] = 1;
        return dock;
    }

    internal void UpgradeNavalDock(Unit dock)
    {
        int level = GetNavalDockLevel(dock);
        if (level <= 0 || level >= MaxLevel)
        {
            return;
        }

        FactionHQ? hq = CommanderGameAccess.GetLocalHq();
        float cost = GetNavalDockUpgradeCost(level);
        if (hq == null || hq.factionFunds < cost)
        {
            StatusText = "Insufficient faction funds for that upgrade.";
            return;
        }

        hq.AddFunds(-cost);
        dockLevels[dock] = level + 1;
        StatusText = $"Naval dock at level {level + 1}: "
            + $"{CommanderNavalPurchaseService.GetLevelUnlockLabel(level + 1)}.";
    }

    /// <summary>
    /// Builds a working factory: the prefab carries the <see cref="Factory"/> component, and
    /// <c>SetFactory</c> is what registers the production cycle — the same call the game makes for
    /// a mission-authored factory. Its cadence cannot be changed afterwards, so the interval is
    /// chosen here and the upgrades move batch size instead.
    /// </summary>
    private Unit? SpawnFactory(
        FactionHQ hq,
        GlobalPosition position,
        VehicleDefinition production,
        bool randomRotation = false,
        Quaternion? rotation = null)
    {
        Unit? unit = SpawnBuilding(
            hq,
            position,
            ResolveDefinition(CommanderBuildKind.Factory),
            $"{production.code} Factory",
            randomRotation,
            rotation);
        if (unit == null)
        {
            return null;
        }

        if (!unit.TryGetComponent(out Factory factory))
        {
            CommanderPlugin.Log.LogWarning(
                "The factory building prefab has no Factory component, so it will never produce.");
            return null;
        }

        factory.SetFactory(production.jsonKey, FactoryProductionSeconds);
        return unit;
    }

    /// <summary>
    /// Spawns the building under <paramref name="displayName"/>, sitting as
    /// <paramref name="rotation"/> says. The player's placements pass the rotation the ghost was
    /// showing when the click landed — chosen heading, and the slope under it when the placement was
    /// conforming to the ground — so the preview is exactly what lands. The enemy commander still
    /// scatters its rotations, because nobody is watching a ghost for those.
    /// <para>
    /// A caller that passes no rotation at all is an AUTOMATIC placement, and has its heading laid
    /// over the ground here by <see cref="CommanderBuildPreview.GroundConformingRotation"/> — the
    /// same measurement and the same steepness limit the manual ghost uses. This method is the one
    /// place every build path in the mod goes through, so it is the only place the tilt has to be
    /// applied.
    /// </para>
    /// </summary>
    /// <remarks>
    /// The name is written twice on purpose. <c>UniqueName</c> is the id the game registers the
    /// unit under and the first thing <c>GetUnitLabel</c> reads, so it gets a numbered copy;
    /// <c>unitName</c> is the SyncVar the Basegame map-icon hover reads
    /// (<c>UnitMapIcon.GetInfoText</c>), so it gets the plain one. Set neither and a gold mine
    /// reports itself as whichever industrial prefab it happens to be wearing.
    /// </remarks>
    private Unit? SpawnBuilding(
        FactionHQ hq,
        GlobalPosition position,
        BuildingDefinition? definition,
        string displayName,
        bool randomRotation = false,
        Quaternion? rotation = null)
    {
        Spawner? spawner = NetworkSceneSingleton<Spawner>.i;
        if (definition == null || spawner == null)
        {
            return null;
        }

        // Every build path in the mod ends here, so the site rules are enforced once, in the one
        // place both commanders route through, rather than at each caller.
        if (!preview.IsSiteAllowed(definition, position, hq, out _))
        {
            return null;
        }

        // The spawn offset is turned with the building, so a tilted structure's offset follows the
        // slope with it — the same product CommanderBuildPreview.Draw uses for the ghost.
        //
        // A caller that hands in a rotation has already composed one: that is the player's click,
        // carrying whatever the armed ghost was showing, so it lands exactly as previewed. Every
        // other caller is automatic — the forward base's recipe, the commander's mines, factories,
        // docks, radars and defences, and the strategic reload that rebuilds them — and has its
        // heading laid over the ground here (fix, 2026-09-18: automatic placements spawned upright,
        // so every structure sited on a slope stood with a corner underground). A random heading is
        // still random; only the missing tilt is added to it.
        Quaternion placement = rotation
            ?? preview.GroundConformingRotation(
                definition, position, randomRotation ? Random.Range(0f, 360f) : 0f);
        GlobalPosition ground = CommanderGameAccess.SnapToTerrain(position);
        Vector3 local = ground.ToLocalPosition() + placement * definition.spawnOffset;
        Unit? spawned = spawner.SpawnFromUnitDefinitionInEditor(
            definition,
            local.ToGlobalPosition(),
            placement,
            hq,
            $"{displayName} {++builtNameCounter}");
        if (spawned != null)
        {
            spawned.NetworkunitName = displayName;
            builtUnits.Add(spawned);
            // A hangar-type building the base has never heard of is a pad that launches nothing,
            // so every spawn is introduced to the base whose ring it went down in.
            LinkSpawnedBuilding(hq, spawned, position, displayName);
        }

        return spawned;
    }

    /// <summary>
    /// The approach cone a linked pad offers, in degrees. Matches what the game's own loader hands
    /// a saved pad (<c>Airbase.VerticalLandingPoint.FromSaved</c>): any side may be approached.
    /// </summary>
    private const float PadApproachAngleDegrees = 180f;

    /// <summary>
    /// A linked pad's usable size in metres, matching <c>Airbase.VerticalLandingPoint.FromSaved</c>:
    /// comfortably larger than any rotary airframe's <c>maxRadius</c> landing query, so a bought
    /// pad answers the same query a mission-authored one does.
    /// </summary>
    private const float PadSizeMeters = 40f;

    /// <summary>
    /// Registers a freshly built hangar-type building with the airbase whose build ring it went
    /// down in. The game only knows a helipad belongs to a base because the mission loader said so
    /// at load — <c>Spawner.SpawnBuilding</c> calls <see cref="Building.SetAirbase"/> from the
    /// pad's saved <c>AirbaseRef</c> — while <c>Spawner.SpawnFromUnitDefinitionInEditor</c>, the
    /// one spawn path this mod uses, always passes <c>airbase: null</c>. A pad with no airbase is
    /// invisible to the base's roster (<c>Airbase.GetAvailableAircraft</c>) and to its hangar
    /// spawn loop (<c>Airbase.TrySpawnAircraft</c>), which is exactly "bought helipads are never
    /// used for spawning". <see cref="Building.SetAirbase"/> is the game's own registration call,
    /// so the pad joins <c>airbase.hangars</c> on the host and on every client. Every build path —
    /// the player's BUILD window and the enemy commander both — lands here, so both commanders'
    /// pads get linked by the one definition.
    /// </summary>
    /// <remarks>
    /// Vertical landing is a second, older system: <c>AIHeloLandingState</c> homes on
    /// <c>airbase.verticalLandingPoints</c>, an array the mission loader authors on the base at
    /// load, and the game ships no runtime registration call for it. A rotary-capable pad is
    /// appended there by hand the way <c>Airbase.SetupCustomAirbase</c> would have done: a point
    /// parented to the base at the pad's spawn spot, then the crossing-runway pass
    /// <c>Airbase.OnStartServer</c> runs for authored points. The point outlives the building on
    /// purpose — mission-authored pads keep their landing spots after the pad building dies too.
    /// </remarks>
    private static void LinkSpawnedBuilding(
        FactionHQ hq, Unit unit, GlobalPosition position, string displayName)
    {
        if (!unit.TryGetComponent(out Building building) || !building.TryGetComponent(out Hangar hangar))
        {
            return;
        }

        if (!CommanderBuildPreview.TryGetBuildBase(hq, position, CommanderSettings.BuildRadiusKm, out Airbase? airbase)
            || airbase == null)
        {
            CommanderPlugin.Log.LogInfo(
                $"{displayName} could not be linked to an airbase: none held within "
                    + $"{CommanderSettings.BuildRadiusKm:0.#} km of the site.");
            return;
        }

        building.SetAirbase(airbase);

        // No registration call exists for landing points (see the remarks above), so this rebuilds
        // the load-time behaviour at runtime. A fixed-wing shelter gets none: its spawn spot is
        // inside the shelter, and a helicopter homing on it would descend onto the roof.
        if (CanHostRotaryAircraft(hangar))
        {
            Transform spawnSpot = hangar.GetSpawnTransform();
            Transform pad = new GameObject($"CommanderPad {displayName}").transform;
            pad.SetParent(airbase.transform);
            pad.SetPositionAndRotation(spawnSpot.position, spawnSpot.rotation);
            Airbase.VerticalLandingPoint landingPoint = new()
            {
                point = pad,
                approachAngleRange = PadApproachAngleDegrees,
                size = PadSizeMeters,
            };
            landingPoint.FindCrossingRunways(airbase);
            Airbase.VerticalLandingPoint[] grown = new Airbase.VerticalLandingPoint[
                (airbase.verticalLandingPoints?.Length ?? 0) + 1];
            if (airbase.verticalLandingPoints != null)
            {
                airbase.verticalLandingPoints.CopyTo(grown, 0);
            }

            grown[grown.Length - 1] = landingPoint;
            airbase.verticalLandingPoints = grown;
        }

        CommanderPlugin.Log.LogInfo(
            $"Linked {displayName} to {CommanderCaptureService.GetAirbaseLabel(airbase)} "
                + $"({airbase.verticalLandingPoints?.Length ?? 0} pads).");
    }

    /// <summary>True when the hangar's roster holds an airframe the rotary flight model can fly.</summary>
    private static bool CanHostRotaryAircraft(Hangar hangar)
    {
        AircraftDefinition[] roster = hangar.GetAvailableAircraft();
        for (int i = 0; i < roster.Length; i++)
        {
            AircraftDefinition? definition = roster[i];
            Aircraft? prefab = definition?.unitPrefab != null
                ? definition.unitPrefab.GetComponent<Aircraft>()
                : null;
            Pilot[]? pilots = prefab?.pilots;
            for (int p = 0; pilots != null && p < pilots.Length; p++)
            {
                if (pilots[p] != null
                    && (pilots[p].pilotType == Pilot.PilotType.Helo
                        || pilots[p].pilotType == Pilot.PilotType.Tiltwing))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Tears a commander-built building down. There is no refund: the money went into the ground,
    /// and a full refund would make a mine a free scouting tool.
    /// </summary>
    internal void Demolish(Unit? unit)
    {
        FactionHQ? hq = CommanderGameAccess.GetLocalHq();
        if (unit == null || hq == null)
        {
            return;
        }

        // Despawning goes through the server object manager, same as building does.
        if (!hq.IsServer)
        {
            StatusText = "Only the host can demolish buildings.";
            return;
        }

        string label = CommanderGameAccess.GetUnitLabel(unit);
        mineLevels.Remove(unit);
        factoryLevels.Remove(unit);
        dockLevels.Remove(unit);

        // The despawn itself is DespawnUnit's (Economy/CommanderFobBuilder.cs) — extracted when the
        // FOB abandonment became the second caller of this identical body (Reuse rule 5). This one
        // keeps the level bookkeeping and the player's status line, which the AI path has no use for.
        if (!DespawnUnit(unit))
        {
            StatusText = $"{label} could not be demolished.";
            return;
        }

        RefreshFriendlyLists();
        StatusText = $"{label} demolished.";
    }

    private int CountMines(FactionHQ hq)
    {
        int count = 0;
        foreach (KeyValuePair<Unit, int> entry in mineLevels)
        {
            if (entry.Key != null && !entry.Key.disabled && entry.Key.NetworkHQ == hq)
            {
                count++;
            }
        }
        return count;
    }

    /// <summary>This faction's mine income per minute, for the COMMANDER LOG header — the same loop
    /// as <see cref="CountMines"/>, summing instead of counting.</summary>
    internal float GetMineIncomePerMinute(FactionHQ hq)
    {
        float total = 0f;
        foreach (KeyValuePair<Unit, int> entry in mineLevels)
        {
            if (entry.Key != null && !entry.Key.disabled && entry.Key.NetworkHQ == hq)
            {
                total += GetMineIncomePerMinute(entry.Value);
            }
        }

        return total;
    }

    private void RefreshFriendlyLists()
    {
        friendlyFactories.Clear();
        friendlyMines.Clear();
        friendlyDocks.Clear();
        builtUnits.RemoveWhere(unit => unit == null);
        FactionHQ? hq = CommanderGameAccess.GetLocalHq();
        if (hq == null)
        {
            return;
        }

        // The factory production list is collected here rather than from the BUILD window, so the
        // roster walk happens on this 5 s tick instead of every OnGUI frame. It is per faction and
        // does not change mid-mission, so once it has entries it is left alone.
        if (productionOptions.Count == 0)
        {
            CommanderGameAccess.CollectFactionVehicleDefinitions(productionOptions);
        }

        Factory[] factories = UnityEngine.Object.FindObjectsOfType<Factory>();
        for (int i = 0; i < factories.Length; i++)
        {
            Factory factory = factories[i];
            Unit? attached = factory == null ? null : factory.attachedUnit;
            if (factory == null
                || attached == null
                || attached.disabled
                || attached.NetworkHQ != hq
                || factory.ProductionUnit == null)
            {
                continue;
            }

            friendlyFactories.Add(factory);
        }

        foreach (KeyValuePair<Unit, int> entry in mineLevels)
        {
            if (entry.Key != null && !entry.Key.disabled && entry.Key.NetworkHQ == hq)
            {
                friendlyMines.Add(entry.Key);
            }
        }

        foreach (KeyValuePair<Unit, int> entry in dockLevels)
        {
            if (entry.Key != null && !entry.Key.disabled && entry.Key.NetworkHQ == hq)
            {
                friendlyDocks.Add(entry.Key);
            }
        }
    }

    private BuildingDefinition? ResolveDefinition(CommanderBuildKind kind)
    {
        if (kind == CommanderBuildKind.NavalDock)
        {
            if (dockDefinitionResolved)
            {
                return dockDefinition;
            }

            Encyclopedia? dockEncyclopedia = Encyclopedia.i;
            if (dockEncyclopedia?.buildings == null)
            {
                return null;
            }

            dockDefinition = FindBuilding(dockEncyclopedia, NavalDockPrefabKeys)
                ?? FindAnyBuilding(dockEncyclopedia);
            dockDefinitionResolved = true;
            // The game ships no dock prefab, so which structure stands in for one is a guess that
            // a patch can invalidate. Say which one won in the console rather than silently
            // building a shed and calling it a harbour.
            CommanderPlugin.Log.LogInfo(dockDefinition != null
                ? $"Naval dock will be built from the '{dockDefinition.jsonKey}' prefab."
                : "No building prefab is available for a naval dock, so naval purchases stay locked.");
            return dockDefinition;
        }

        bool factory = kind == CommanderBuildKind.Factory;
        if (factory ? factoryDefinitionResolved : mineDefinitionResolved)
        {
            return factory ? factoryDefinition : mineDefinition;
        }

        Encyclopedia? encyclopedia = Encyclopedia.i;
        if (encyclopedia == null || encyclopedia.buildings == null)
        {
            return null;
        }

        // A factory has to be the factory prefab — the Factory component is what produces — so
        // unlike the mine there is no fall back to whatever building happens to be first.
        string[] keys = factory ? FactoryPrefabKeys : MinePrefabKeys;
        BuildingDefinition? resolved = FindBuilding(encyclopedia, keys)
            ?? (factory ? null : FindAnyBuilding(encyclopedia));
        if (factory)
        {
            factoryDefinition = resolved;
            factoryDefinitionResolved = true;
        }
        else
        {
            mineDefinition = resolved;
            mineDefinitionResolved = true;
        }

        return resolved;
    }

    private static BuildingDefinition? FindBuilding(Encyclopedia encyclopedia, string[] keys)
    {
        for (int keyIndex = 0; keyIndex < keys.Length; keyIndex++)
        {
            for (int i = 0; i < encyclopedia.buildings.Count; i++)
            {
                BuildingDefinition candidate = encyclopedia.buildings[i];
                if (candidate != null
                    && candidate.unitPrefab != null
                    && candidate.jsonKey == keys[keyIndex])
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    /// <summary>Any buildable prefab that is NOT a functional category the commanders need — a
    /// depot, a hangar or a radar stand-in for a dock would take that category off the build list
    /// (see <see cref="NavalDockPrefabKeys"/>). Pure test in <see cref="MayStandInForDock"/>.</summary>
    private static BuildingDefinition? FindAnyBuilding(Encyclopedia encyclopedia)
    {
        for (int i = 0; i < encyclopedia.buildings.Count; i++)
        {
            BuildingDefinition candidate = encyclopedia.buildings[i];
            if (candidate != null && candidate.unitPrefab != null && MayStandInForDock(candidate.buildingType))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>Which building categories the naval dock may borrow a prefab from, pure: never a
    /// depot, hangar or radar, because the catalogue hides the dock's prefab from the player and
    /// the AI resolves each of those categories off the same catalogue.</summary>
    internal static bool MayStandInForDock(BuildingType type)
    {
        return type is not (BuildingType.DEP or BuildingType.HGR or BuildingType.RDR);
    }
}
