using System.Collections.Generic;
using RoadPathfinding;
using UnityEngine;

namespace GroundControlRts;

/// <summary>
/// The enemy commander's half of the economy: it builds and upgrades the same mines and factories
/// the player does, at the same prices, out of its own faction funds. One purchase per review, and
/// never below a reserve multiple, so it does not starve the unit spender it shares the pot with.
/// </summary>
internal sealed partial class CommanderEconomyService
{
    /// <summary>Sites tried per enemy build before it gives up and waits for the next review. Also
    /// the ring-probe budget discovery uses to seed a resource site beside an industrial building
    /// (departure 1) — one definition, both callers.</summary>
    internal const int EnemySiteAttempts = 12;

    /// <summary>Step taken walking inland from a sea lane looking for the water's edge, and how
    /// far to keep walking. Each step is a terrain probe, so this is the cost knob.</summary>
    private const float ShoreWalkStepMeters = 50f;
    private const int ShoreWalkSteps = 8;

    /// <summary>
    /// Sea lane points examined per dock search, and the stride through each lane's point list.
    /// Both are here so one review cannot eat a frame: the search runs every 30 s until a dock
    /// exists, and every probe it makes is a raycast. Striding spreads the budget along the whole
    /// coast rather than spending it all on the first lane.
    /// </summary>
    private const int ShoreLanePointBudget = 32;
    private const int ShoreLanePointStride = 3;

    /// <summary>Defensive structures a commander keeps AT EVERY BASE IT HOLDS once its economy is
    /// running. Per base, not per faction (fix, 2026-09-14): the count used to be faction-wide, and
    /// the emplacements the mission authors around a starting airfield already exceed it, so a
    /// commander holding four bases was over target from the first second and no captured base was
    /// ever hardened — the 2026-09-14 `Ground Control Duel Far` match logged `buildings 0` on all
    /// fifty ladder lines of both sides.</summary>
    private const int EnemyDefenceBuildingTarget = 3;

    /// <summary>A base counts as having radar cover with a radar building this close to it.</summary>
    private const float RadarCoverageMeters = 3000f;

    /// <summary>Reviews between two "rung 4 is saving for X" lines. The same cadence the commander's
    /// own hold line uses, for the same reason: a standing shortfall should be visible without
    /// filling the console.</summary>
    private const int StructureWishReportEveryReviews = 8;

    /// <summary>Ring a base-adjacent structure is dropped into: near enough to be part of the base,
    /// far enough not to be on the apron.</summary>
    private const float BaseSiteMinMeters = 200f;
    private const float BaseSiteMaxMeters = 900f;

    /// <summary>HQs already told, once, that this map gives them nowhere to put a dock.</summary>
    private readonly HashSet<FactionHQ> shoreSearchReported = new();

    /// <summary>The structure each building category resolves to, decided once per mission.</summary>
    private readonly Dictionary<BuildingType, BuildingDefinition?> categoryDefinitions = new();

    /// <summary>
    /// Per-HQ savings toward the next structure (design.md, commander-priorities_20260914 Section 3,
    /// rung 4): each review's building allocation banks here until the next want is affordable. Pure
    /// bookkeeping — the money itself stays in <c>factionFunds</c> until a build charges it, which is
    /// why the bank is capped at the next want's price (see <see cref="SpendEnemyStructures"/>).
    /// </summary>
    private readonly Dictionary<FactionHQ, float> structureSavings = new();

    /// <summary>Reviews since this HQ's rung 4 last said what it is saving for, for the
    /// <see cref="StructureWishReportEveryReviews"/> cadence.</summary>
    private readonly Dictionary<FactionHQ, int> structureWishReviews = new();

    /// <summary>
    /// The next thing rung 4 wants and its price — ONE definition of "what to build next" (the
    /// design's rule), in the exact order the spend walks: the build
    /// <see cref="GetEnemyBuildReserve"/> names, then the dock, mine and factory upgrades. Zero when
    /// the commander wants nothing. Internal: the priority ladder reads it for the rung's demand and
    /// its allocation.
    /// </summary>
    internal static float NextEnemyStructureCost(FactionHQ hq)
    {
        float price = GetEnemyBuildReserve(hq);
        if (price > 0f)
        {
            return price;
        }

        CommanderEconomyService? service = Instance;
        if (service == null || hq == null || hq.faction == null)
        {
            return 0f;
        }

        if (service.TryNextDockUpgrade(hq, out _, out float dockCost))
        {
            return dockCost;
        }

        if (service.TryNextMineUpgrade(hq, out _, out float mineCost))
        {
            return mineCost;
        }

        return service.NextFactoryUpgradeCost(hq);
    }

    /// <summary>
    /// Rung 4's spend (design Section 1), called from the priority ladder — the enemy commander's
    /// review is the single spend site, so this service no longer spends on its own clock. Repair
    /// crews stay ahead of the structure spend exactly as today (same price, same funds gate,
    /// charged straight off the balance); the structure ladder spends ONLY out of what the rung
    /// banked — this review's allocation plus previous reviews' savings — never straight off the
    /// balance. The bank never grows past the next want's price, so a rung drawn first cannot hoard
    /// the whole remainder against the rungs below. The old
    /// <c>funds ≥ price × EnemyReserveMultiple</c> gate is gone from builds and upgrades: that
    /// multiple existed to keep two spenders sharing one balance from starving each other, and the
    /// ladder is that protection now — the gate is savings ≥ price and funds ≥ price. One purchase
    /// per review, exactly as this service always did.
    /// <para>Returns what rung 4 spent, for the ladder's diagnostics line.</para>
    /// </summary>
    internal float SpendEnemyStructures(FactionHQ hq, float allocation)
    {
        // Keeping what it owns comes before buying more of it: a bombed refinery that stays bombed
        // costs the enemy commander its income, which is the same trade the player is making.
        if (hq.factionFunds >= CommanderRepairService.CrewCost * EnemyReserveMultiple
            && CommanderRepairService.Instance?.TrySendEnemyRepairCrew(hq) == true)
        {
            return CommanderRepairService.CrewCost;
        }

        float banked = structureSavings.TryGetValue(hq, out float saved) ? saved : 0f;
        float price = NextEnemyStructureCost(hq);
        float savings = banked;
        float spent = 0f;
        if (price > 0f)
        {
            savings = Mathf.Min(banked + Mathf.Max(0f, allocation), price);
            // Banked before the spend: the forward-base order reads the bank itself
            // (StructureSavingsFor), and on the review the bank first reached the price it still
            // saw last review's figure and refused (review E4).
            structureSavings[hq] = savings;
            if (savings >= price && hq.factionFunds >= price)
            {
                spent = SpendNextEnemyStructure(hq);
                if (spent > 0f)
                {
                    savings = Mathf.Max(0f, savings - spent);
                }
            }
        }

        // Not affordable yet (or the site search failed): the allocation stays banked for the next
        // review — "the rung saves its allocation across reviews until the next structure is
        // affordable" (design Section 3).
        structureSavings[hq] = savings;
        ReportStructureWish(hq, price, savings, allocation, spent);
        return spent;
    }

    /// <summary>
    /// What rung 4 is saving for and what is stopping it, on the
    /// <see cref="StructureWishReportEveryReviews"/> cadence and only while it is buying nothing
    /// (diagnostic, 2026-09-14). The ladder line reports `buildings 0` for a rung that has no wish,
    /// a rung whose bank is short and a rung whose site search keeps failing, and those three read
    /// identically: the 2026-09-14 match showed fifty consecutive `buildings 0` lines on both sides
    /// with nothing in the log to say which of the three it was.
    /// </summary>
    private void ReportStructureWish(FactionHQ hq, float price, float savings, float allocation, float spent)
    {
        if (spent > 0f)
        {
            structureWishReviews[hq] = 0;
            return;
        }

        int quiet = (structureWishReviews.TryGetValue(hq, out int seen) ? seen : 0) + 1;
        structureWishReviews[hq] = quiet;
        if (quiet % StructureWishReportEveryReviews != 1)
        {
            return;
        }

        if (price <= 0f)
        {
            CommanderAiLog.Note(
                hq,
                $"builds nothing: its structure list is finished — every base has radar, the mine and "
                    + $"factory targets are met and no upgrade is wanted ({quiet} quiet reviews).");
            return;
        }

        string blocker = savings < price
            ? $"the rung has banked {savings:0} of it"
            : (hq.factionFunds < price
                ? $"the rung has the {savings:0} but the balance is {hq.factionFunds:0}"
                : "the bank and the balance both cover it, so the site search is what failed");
        CommanderAiLog.Note(
            hq,
            $"saves for its next structure at {price:0}: {blocker} "
                + $"(this review's allocation {allocation:0}, {quiet} quiet reviews).");
    }

    /// <summary>
    /// Performs the ONE purchase <see cref="NextEnemyStructureCost"/> named and returns what it
    /// cost (0 when nothing could be bought — no reachable site for the mine, no room for the
    /// factory). The spend order is the cost walk's own order, so the price the ladder banked toward
    /// and the thing actually bought are the same decision.
    /// </summary>
    private float SpendNextEnemyStructure(FactionHQ hq)
    {
        float price = GetEnemyBuildReserve(hq);
        if (price > 0f)
        {
            return TryBuildEnemyEconomy(hq) ? price : 0f;
        }

        float spent = TryUpgradeEnemyNavalDock(hq);
        if (spent > 0f)
        {
            return spent;
        }

        spent = TryUpgradeEnemyMine(hq);
        if (spent > 0f)
        {
            return spent;
        }

        return TryUpgradeEnemyFactory(hq);
    }

    /// <summary>
    /// The first factory this commander could upgrade and its price — the factory leg of the ONE
    /// definition of "what rung 4 wants next". The expensive <c>FindObjectsOfType</c> walk sits at
    /// the very end of that order, as it always did, so it only runs when nothing cheaper is wanted.
    /// </summary>
    internal float NextFactoryUpgradeCost(FactionHQ hq)
    {
        return TryNextFactoryUpgrade(hq, out _, out _, out float cost) ? cost : 0f;
    }

    /// <summary>Charges and performs the next factory upgrade when the rung's bank covers it.
    /// Returns the cost charged, or 0.</summary>
    private float TryUpgradeEnemyFactory(FactionHQ hq)
    {
        if (!TryNextFactoryUpgrade(hq, out Unit? attached, out int level, out float cost)
            || hq.factionFunds < cost)
        {
            return 0f;
        }

        hq.AddFunds(-cost);
        factoryLevels[attached!] = level + 1;
        CommanderAiLog.Note(
            hq, $"upgraded {CommanderGameAccess.GetUnitLabel(attached)} to level {level + 1}.");
        return cost;
    }

    /// <summary>The first upgradable factory this commander owns: the attached unit, its level and
    /// the upgrade price — the candidate picker the cost read and the spend share (Reuse rule 4, one
    /// definition, two callers), so the ladder can never bank toward a factory the spend would
    /// skip.</summary>
    private bool TryNextFactoryUpgrade(FactionHQ hq, out Unit? attached, out int level, out float cost)
    {
        Factory[] factories = UnityEngine.Object.FindObjectsOfType<Factory>();
        for (int i = 0; i < factories.Length; i++)
        {
            Factory candidate = factories[i];
            Unit? candidateAttached = candidate == null ? null : candidate.attachedUnit;
            if (candidate == null
                || candidateAttached == null
                || candidateAttached.disabled
                || candidateAttached.NetworkHQ != hq
                || candidate.ProductionUnit == null)
            {
                continue;
            }

            int candidateLevel = GetFactoryLevel(candidate);
            if (candidateLevel >= MaxLevel)
            {
                continue;
            }

            attached = candidateAttached;
            level = candidateLevel;
            cost = GetFactoryUpgradeCost(candidateLevel);
            return true;
        }

        attached = null;
        level = 0;
        cost = 0f;
        return false;
    }

    /// <summary>
    /// The price of the next structure rung 4 wants, or 0 when its build list is finished — still
    /// THE definition of "what to build next" (design.md, commander-priorities_20260914), unchanged
    /// in its order: radar cover first, then mines while a resource site is in reach, then
    /// factories, then base defence, then the dock. What changed with the ladder (2026-09-14) is
    /// only the pot: this no longer holds money back from the unit spender — the priority ladder
    /// grants rung 4 its own allocation per review, the rung banks it in
    /// <see cref="structureSavings"/> until this price is covered, and everything below rung 4
    /// spends only what the draw left it. The old hold-back was a fix for two spenders draining one
    /// balance faster than a factory could accumulate; the ladder prevents that structurally.
    /// A mine is only wanted while a resource site is in reach; short of one, the commander saves
    /// for the next thing instead of piling funds toward a mine it cannot site.
    /// </summary>
    internal static float GetEnemyBuildReserve(FactionHQ hq)
    {
        CommanderEconomyService? service = Instance;
        if (service == null || hq == null || hq.faction == null)
        {
            return 0f;
        }

        // A held resource site with no mine comes before everything (user, 2026-09-14: "ensure the
        // AI commander will actually prioritise building a gold mine once a resource site is
        // captured"): the site is the only income the ground fight wins, the mine is the cheapest
        // structure on the list, and on a map with sites the count target no longer applies — every
        // held site gets one. The target still bounds the anywhere-in-reach mines of a map with none.
        if (WantsSiteMine(hq))
        {
            return MineBuildCost;
        }

        // A base the commander holds but cannot USE comes next (design.md,
        // fob-construction_20260914 Section 4): a captured airfield with no vehicle depot deploys
        // nothing, and one with no vertical landing pad neither launches nor recovers a helicopter.
        // Both were invisible to this list until now, which is why a captured base sat idle.
        float facility = NextBaseFacilityCost(hq);
        if (facility > 0f)
        {
            return facility;
        }

        // A base that cannot see the attack coming outranks income. Without a radar building the
        // commander's tracking database holds only what its parked units happen to see, and the
        // whole defence posture reads that database.
        BuildingDefinition? radar = service.ResolveCategoryDefinition(BuildingType.RDR, preferDearest: true);
        if (radar != null && TryGetUncoveredBase(hq, BuildingType.RDR, RadarCoverageMeters, out _))
        {
            return GetStructureCost(radar);
        }

        bool duel = CommanderEnemyCommanderService.IsDuelMission;
        if (CommanderStrategicPointService.Instance?.HasResourceSites != true
            && service.CountMines(hq) < (duel ? DuelEnemyMineTarget : EnemyMineTarget))
        {
            // A map with no resource sites keeps the old anywhere-in-reach mines, capped as before.
            return MineBuildCost;
        }

        // A forward operating base (design.md, fob-construction_20260914 Section 1) comes BEFORE
        // the factories (user, 2026-09-14, "still no FOB!?"): with reach in force a FOB is what puts
        // the next objectives on the board at all, while a factory only feeds a pool that already
        // holds more than the reachable points can use. The 2026-09-14 match never asked about a
        // FOB on the player's side because the rung was still buying factories.
        float fob = CommanderOperationsService.WantsFob(hq) ? FobStructuresCost() : 0f;
        if (fob > 0f)
        {
            return fob;
        }

        if (CommanderSettings.FactoriesEnabled
            && service.CountFactories(hq) < (duel ? DuelEnemyFactoryTarget : EnemyFactoryTarget))
        {
            return FactoryBuildCost;
        }

        // Per base, not per faction (fix, 2026-09-14): see EnemyDefenceBuildingTarget. A captured
        // airfield is exactly the base that has no emplacements and exactly the base the enemy is
        // coming back for.
        BuildingDefinition? defence = service.ResolveCategoryDefinition(BuildingType.DEF, preferDearest: false);
        if (defence != null
            && TryGetBaseShortOfBuildings(
                hq, BuildingType.DEF, BaseBuildingCoverageMeters, EnemyDefenceBuildingTarget, out _))
        {
            return GetStructureCost(defence);
        }

        // A commander whose map gave it no coast hands the money back rather than reserving for a
        // dock it can never site — the same reason AccrueFund caps the naval fund.
        return GetNavalDockLevel(hq) <= 0 && !service.shoreSearchReported.Contains(hq)
            ? NavalDockBuildCost
            : 0f;
    }

    /// <summary>
    /// Builds whatever <see cref="GetEnemyBuildReserve"/> said this commander wants next, and only
    /// that: each branch is the same condition, in the same order, and a failed build ends the walk.
    /// It used to fall through to the next builder, so a mine that could not be sited bought a radar,
    /// a forward base or a factory at its own price while the bank was debited by the mine's, and
    /// none of those builders checks the balance (review H14).
    /// </summary>
    private bool TryBuildEnemyEconomy(FactionHQ hq)
    {
        if (WantsSiteMine(hq))
        {
            return TryBuildEnemyMine(hq);
        }

        if (NextBaseFacilityCost(hq) > 0f)
        {
            return TryBuildBaseFacility(hq);
        }

        BuildingDefinition? radar = ResolveCategoryDefinition(BuildingType.RDR, preferDearest: true);
        if (radar != null && TryGetUncoveredBase(hq, BuildingType.RDR, RadarCoverageMeters, out _))
        {
            return TryBuildEnemyRadar(hq);
        }

        bool duel = CommanderEnemyCommanderService.IsDuelMission;
        if (CommanderStrategicPointService.Instance?.HasResourceSites != true
            && CountMines(hq) < (duel ? DuelEnemyMineTarget : EnemyMineTarget))
        {
            return TryBuildEnemyMine(hq);
        }

        if (CommanderOperationsService.WantsFob(hq))
        {
            return CommanderOperationsService.TryOrderFob(hq);
        }

        if (CommanderSettings.FactoriesEnabled
            && CountFactories(hq) < (duel ? DuelEnemyFactoryTarget : EnemyFactoryTarget))
        {
            return TryBuildEnemyFactory(hq);
        }

        BuildingDefinition? defence = ResolveCategoryDefinition(BuildingType.DEF, preferDearest: false);
        if (defence != null
            && TryGetBaseShortOfBuildings(
                hq, BuildingType.DEF, BaseBuildingCoverageMeters, EnemyDefenceBuildingTarget, out _))
        {
            return TryBuildEnemyDefence(hq);
        }

        return TryBuildEnemyNavalDock(hq);
    }

    /// <summary>Whether this commander holds a resource site with no working mine that it can reach
    /// — the first wish of the building rung on any map that has sites.</summary>
    private static bool WantsSiteMine(FactionHQ hq)
    {
        CommanderStrategicPointService? points = CommanderStrategicPointService.Instance;
        return points != null && points.HasResourceSites && points.TryPickFreeReachableSite(hq, out GlobalPosition _);
    }

    /// <summary>
    /// One radar building per base, rebuilt when the last one is bombed. Sited beside the base it
    /// covers rather than wherever a dart lands: the thing being answered is "this base has no
    /// radar", and a second mast next to the first one answers nothing.
    /// </summary>
    private bool TryBuildEnemyRadar(FactionHQ hq)
    {
        BuildingDefinition? radar = ResolveCategoryDefinition(BuildingType.RDR, preferDearest: true);
        if (radar == null
            || !TryGetUncoveredBase(hq, BuildingType.RDR, RadarCoverageMeters, out GlobalPosition anchor)
            || !TryFindSiteNear(hq, radar, anchor, out GlobalPosition site)
            || SpawnBuilding(hq, site, radar, GetStructureLabel(radar), randomRotation: true) == null)
        {
            return false;
        }

        hq.AddFunds(-GetStructureCost(radar));
        CommanderAiLog.Note(hq, $"built a {GetStructureLabel(radar)} at a base with no radar cover.");
        return true;
    }

    /// <summary>
    /// Hardens the first base this commander holds that is short of its own
    /// <see cref="EnemyDefenceBuildingTarget"/> emplacements, with whatever the encyclopedia files
    /// under DEFENCE. Sited beside THAT base rather than wherever the shared dart lands (fix,
    /// 2026-09-14, same shape as <see cref="TryBuildEnemyRadar"/>): the thing being answered is
    /// "this base is undefended", and a fourth pillbox at the home field answers nothing.
    /// </summary>
    private bool TryBuildEnemyDefence(FactionHQ hq)
    {
        BuildingDefinition? defence = ResolveCategoryDefinition(BuildingType.DEF, preferDearest: false);
        if (defence == null
            || !TryGetBaseShortOfBuildings(
                hq, BuildingType.DEF, BaseBuildingCoverageMeters, EnemyDefenceBuildingTarget,
                out GlobalPosition anchor)
            || !TryFindSiteNear(hq, defence, anchor, out GlobalPosition site)
            || SpawnBuilding(hq, site, defence, GetStructureLabel(defence), randomRotation: true) == null)
        {
            return false;
        }

        hq.AddFunds(-GetStructureCost(defence));
        CommanderAiLog.Note(
            hq,
            $"built a {GetStructureLabel(defence)} at a base short of its "
                + $"{EnemyDefenceBuildingTarget} defences.");
        return true;
    }

    /// <summary>
    /// The structure a commander uses for a whole building category, resolved off the live
    /// encyclopedia rather than a name table: <c>BuildingType</c> is authored in the game's own
    /// assets, so a patch that adds a radar mast files itself and a patch that removes one degrades
    /// to "no radar" instead of to a null prefab. Logged once, because a choice a game patch can
    /// invalidate should not be a silent one.
    /// </summary>
    private BuildingDefinition? ResolveCategoryDefinition(BuildingType type, bool preferDearest)
    {
        if (categoryDefinitions.TryGetValue(type, out BuildingDefinition? cached))
        {
            return cached;
        }

        IReadOnlyList<BuildingDefinition> entries = Catalog;
        if (entries.Count == 0)
        {
            // The encyclopedia is not up yet. Ask again next review rather than caching a null.
            return null;
        }

        BuildingDefinition? best = null;
        for (int i = 0; i < entries.Count; i++)
        {
            BuildingDefinition candidate = entries[i];
            if (candidate.buildingType != type)
            {
                continue;
            }

            if (best == null || (preferDearest ? candidate.value > best.value : candidate.value < best.value))
            {
                best = candidate;
            }
        }

        categoryDefinitions[type] = best;
        CommanderPlugin.Log.LogInfo(best == null
            ? $"No {GetCategoryLabel(type)} structure in the encyclopedia, so no commander will build one."
            : $"Commanders will build {GetStructureLabel(best)} for {GetCategoryLabel(type)} "
                + $"({GetStructureCost(best):0}).");
        return best;
    }

    /// <summary>The first base this faction holds with no building of that category near it — the
    /// one-building case of <see cref="TryGetBaseShortOfBuildings"/> (Reuse rule 5: the defence
    /// target was the second per-base count, so the first one was generalised rather than
    /// forked).</summary>
    private static bool TryGetUncoveredBase(
        FactionHQ hq,
        BuildingType type,
        float coverage,
        out GlobalPosition center)
    {
        return TryGetBaseShortOfBuildings(hq, type, coverage, 1, out center);
    }

    /// <summary>
    /// The first base this faction holds with fewer than <paramref name="target"/> buildings of that
    /// category within <paramref name="coverage"/> of its centre, and that base's centre. THE
    /// per-base structure test: the radar wish and the defence wish read the same answer, so the
    /// price the ladder banks toward and the base the build actually goes to can never disagree.
    /// </summary>
    private static bool TryGetBaseShortOfBuildings(
        FactionHQ hq,
        BuildingType type,
        float coverage,
        int target,
        out GlobalPosition center)
    {
        center = default;
        foreach (Airbase airbase in hq.GetAirbases())
        {
            // A ship's deck (AttachedAirbase) is never a site for radar, defences or anything else.
            if (airbase == null || airbase.disabled || airbase.center == null || CommanderGameAccess.IsShipAirbase(airbase))
            {
                continue;
            }

            GlobalPosition candidate = airbase.center.GlobalPosition();
            if (BuildingsStillWantedAtBase(CountBuildingsNear(hq, type, candidate, coverage), target) > 0)
            {
                center = candidate;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// How many more buildings of a category a base still wants, given how many stand near THAT
    /// base. Pure, for the self-check. The argument this deliberately does not take is the
    /// faction-wide count: measuring the target across the whole faction is the 2026-09-14 bug —
    /// the emplacements authored around a starting airfield covered every base the commander would
    /// ever capture, so no captured base was ever hardened.
    /// </summary>
    internal static int BuildingsStillWantedAtBase(int nearThisBase, int target)
    {
        return Mathf.Max(0, target - Mathf.Max(0, nearThisBase));
    }

    /// <summary>How many buildings of that category this faction owns within
    /// <paramref name="radius"/> of a point. One definition for "is there one" and "are there
    /// three" (Reuse rule 4) — <see cref="HasBuildingNear"/> is the &gt; 0 case.</summary>
    private static int CountBuildingsNear(FactionHQ hq, BuildingType type, GlobalPosition position, float radius)
    {
        if (hq.factionUnits == null)
        {
            return 0;
        }

        int count = 0;
        foreach (PersistentID id in hq.factionUnits)
        {
            if (id.TryGetUnit(out Unit unit)
                && unit != null
                && !unit.disabled
                && unit is Building
                && unit.definition is BuildingDefinition definition
                && definition.buildingType == type
                && FastMath.InRange(unit.transform.GlobalPosition(), position, radius))
            {
                count++;
            }
        }

        return count;
    }

    private static bool HasBuildingNear(FactionHQ hq, BuildingType type, GlobalPosition position, float radius)
    {
        return CountBuildingsNear(hq, type, position, radius) > 0;
    }

    /// <summary>A legal site on a ring around one point, through the same shared rule as every
    /// other build in the mod.</summary>
    private bool TryFindSiteNear(
        FactionHQ hq,
        BuildingDefinition definition,
        GlobalPosition anchor,
        out GlobalPosition site)
    {
        for (int attempt = 0; attempt < EnemySiteAttempts; attempt++)
        {
            float angle = Random.Range(0f, Mathf.PI * 2f);
            float distance = Random.Range(BaseSiteMinMeters, BaseSiteMaxMeters);
            GlobalPosition candidate = new(
                anchor.x + Mathf.Cos(angle) * distance,
                anchor.y,
                anchor.z + Mathf.Sin(angle) * distance);
            if (preview.IsSiteAllowed(definition, candidate, hq, out _))
            {
                site = candidate;
                return true;
            }
        }

        site = default;
        return false;
    }

    /// <summary>Builds on the nearest free resource site this commander can reach.</summary>
    private bool TryBuildEnemyMine(FactionHQ hq)
    {
        CommanderStrategicPointService? pointService = CommanderStrategicPointService.Instance;
        if (pointService == null || !pointService.HasResourceSites)
        {
            // No sites on this map (yet): the pre-points rule, a spot beside something the faction
            // owns. Same fallback the player's ghost takes in CommanderBuildPreview.Evaluate.
            GlobalPosition fallback = default;
            BuildingDefinition? mine = ResolveDefinition(CommanderBuildKind.Mine);
            if (!TryFindEnemyBuildSite(hq, mine, ref fallback) || SpawnMine(hq, fallback, randomRotation: true) == null)
            {
                return false;
            }

            hq.AddFunds(-MineBuildCost);
            CommanderAiLog.Note(hq, "built a gold mine (no resource sites on this map).");
            return true;
        }

        if (!pointService.TryPickFreeReachableSite(hq, out CommanderStrategicPoint point)
            || SpawnMine(hq, point.Position, randomRotation: true) == null)
        {
            return false;
        }

        hq.AddFunds(-MineBuildCost);
        CommanderAiLog.Note(hq, $"built a gold mine on {point.Label}.");
        return true;
    }

    /// <summary>The enemy's first factory, producing something its own faction fields.</summary>
    private bool TryBuildEnemyFactory(FactionHQ hq)
    {
        CommanderGameAccess.CollectFactionVehicleDefinitions(enemyProductionOptions, hq);
        if (enemyProductionOptions.Count == 0)
        {
            return false;
        }

        VehicleDefinition production = enemyProductionOptions[Random.Range(0, enemyProductionOptions.Count)];
        GlobalPosition site = default;
        BuildingDefinition? plant = ResolveDefinition(CommanderBuildKind.Factory);
        if (!TryFindEnemyBuildSite(hq, plant, ref site) || SpawnFactory(hq, site, production, randomRotation: true) == null)
        {
            return false;
        }

        hq.AddFunds(-FactoryBuildCost);
        CommanderAiLog.Note(hq, $"built a {production.unitName} factory.");
        return true;
    }

    /// <summary>
    /// Puts the enemy's dock on the coast nearest a base it holds. The search starts from the map's
    /// own sea lanes rather than throwing darts at the ground: a sea lane point is water by
    /// definition, so walking inland from one until the terrain climbs above the sea plane finds the
    /// shoreline in a handful of probes. Darts would have to sample the whole build radius to find
    /// the coast at all, and most maps are mostly land.
    /// </summary>
    private bool TryBuildEnemyNavalDock(FactionHQ hq)
    {
        BuildingDefinition? dock = ResolveDefinition(CommanderBuildKind.NavalDock);
        if (dock == null || !TryFindShoreSite(hq, dock, out GlobalPosition site))
        {
            // Say it once. An enemy with no navy on a map whose coast it cannot reach looks exactly
            // like an enemy that forgot to build one, and only the console can tell them apart.
            if (dock != null && shoreSearchReported.Add(hq))
            {
                CommanderAiLog.Note(
                    hq,
                    $"found no shoreline within {CommanderSettings.NavalDockRadiusKm:0.#} km of a base it holds, "
                        + "so it has no navy. Raise Economy/NavalDockRadiusKm if this map keeps its coast further out.");
            }

            return false;
        }

        if (SpawnNavalDock(hq, site, randomRotation: true) == null)
        {
            return false;
        }

        hq.AddFunds(-NavalDockBuildCost);
        CommanderAiLog.Note(hq, "built a naval dock.");
        return true;
    }

    /// <summary>The first dock this commander owns below max level and its upgrade price — the dock
    /// leg of the ONE definition of "what rung 4 wants next". The cost read and the spend share this
    /// picker (Reuse rule 4), so the ladder banks toward the dock the spend would actually upgrade.</summary>
    private bool TryNextDockUpgrade(FactionHQ hq, out Unit dock, out float cost)
    {
        foreach (KeyValuePair<Unit, int> entry in dockLevels)
        {
            if (entry.Value >= MaxLevel
                || entry.Key == null
                || entry.Key.disabled
                || entry.Key.NetworkHQ != hq)
            {
                continue;
            }

            dock = entry.Key;
            cost = GetNavalDockUpgradeCost(entry.Value);
            return true;
        }

        dock = null!;
        cost = 0f;
        return false;
    }

    /// <summary>Charges and performs the next dock upgrade when the rung's bank covers it. Returns
    /// the cost charged, or 0.</summary>
    private float TryUpgradeEnemyNavalDock(FactionHQ hq)
    {
        if (!TryNextDockUpgrade(hq, out Unit dock, out float cost) || hq.factionFunds < cost)
        {
            return 0f;
        }

        int level = dockLevels[dock];
        hq.AddFunds(-cost);
        dockLevels[dock] = level + 1;
        CommanderAiLog.Note(
            hq,
            $"upgraded its naval dock to level {level + 1}: "
                + $"{CommanderNavalPurchaseService.GetLevelUnlockLabel(level + 1)}.");
        return cost;
    }

    /// <summary>The first gold mine this commander owns below max level and its upgrade price — the
    /// mine leg of the ONE definition of "what rung 4 wants next", shared by the cost read and the
    /// spend (Reuse rule 4).</summary>
    private bool TryNextMineUpgrade(FactionHQ hq, out Unit mine, out float cost)
    {
        foreach (KeyValuePair<Unit, int> entry in mineLevels)
        {
            if (entry.Value >= MaxLevel
                || entry.Key == null
                || entry.Key.disabled
                || entry.Key.NetworkHQ != hq)
            {
                continue;
            }

            mine = entry.Key;
            cost = GetMineUpgradeCost(entry.Value);
            return true;
        }

        mine = null!;
        cost = 0f;
        return false;
    }

    /// <summary>Charges and performs the next mine upgrade when the rung's bank covers it. Returns
    /// the cost charged, or 0.</summary>
    private float TryUpgradeEnemyMine(FactionHQ hq)
    {
        if (!TryNextMineUpgrade(hq, out Unit mine, out float cost) || hq.factionFunds < cost)
        {
            return 0f;
        }

        int level = mineLevels[mine];
        hq.AddFunds(-cost);
        mineLevels[mine] = level + 1;
        CommanderAiLog.Note(hq, $"upgraded a gold mine to level {level + 1}.");
        return cost;
    }

    private bool TryFindShoreSite(FactionHQ hq, BuildingDefinition dock, out GlobalPosition site)
    {
        site = default;
        RoadNetwork? seaLanes = NetworkSceneSingleton<LevelInfo>.i?.seaLanes;
        if (seaLanes == null || !seaLanes.Exists())
        {
            return false;
        }

        float reach = Mathf.Max(CommanderSettings.NavalDockRadiusKm, 0.1f) * 1000f;
        int examined = 0;
        foreach (Road lane in seaLanes.roads)
        {
            if (lane?.points == null)
            {
                continue;
            }

            for (int i = 0; i < lane.points.Count; i += ShoreLanePointStride)
            {
                if (examined >= ShoreLanePointBudget)
                {
                    return false;
                }

                GlobalPosition water = lane.points[i];
                if (!TryGetNearestBase(hq, water, reach, out GlobalPosition anchor))
                {
                    continue;
                }

                examined++;
                Vector3 inland = anchor.AsVector3() - water.AsVector3();
                inland.y = 0f;
                if (inland.sqrMagnitude < 1f)
                {
                    continue;
                }

                inland.Normalize();
                for (int step = 1; step <= ShoreWalkSteps; step++)
                {
                    Vector3 probe = water.AsVector3() + inland * (step * ShoreWalkStepMeters);
                    GlobalPosition candidate = new(probe.x, probe.y, probe.z);
                    if (CommanderGameAccess.IsBelowSeaLevel(CommanderGameAccess.SnapToTerrain(candidate)))
                    {
                        continue;
                    }

                    // Above the waterline. The shared site rule has the final say, exactly as it
                    // does for the player's ghost.
                    if (preview.IsSiteAllowed(dock, candidate, hq, out _))
                    {
                        site = candidate;
                        return true;
                    }

                    break;
                }
            }
        }

        return false;
    }

    private static bool TryGetNearestBase(FactionHQ hq, GlobalPosition from, float reach, out GlobalPosition anchor)
    {
        anchor = default;
        float best = reach;
        foreach (Airbase airbase in hq.GetAirbases())
        {
            // A ship's deck (AttachedAirbase) is never a site for radar, defences or anything else.
            if (airbase == null || airbase.disabled || airbase.center == null || CommanderGameAccess.IsShipAirbase(airbase))
            {
                continue;
            }

            GlobalPosition center = airbase.center.GlobalPosition();
            float distance = FastMath.Distance(from, center);
            if (distance <= best)
            {
                best = distance;
                anchor = center;
            }
        }

        return best < reach;
    }

    private int CountFactories(FactionHQ hq)
    {
        int count = 0;
        Factory[] factories = UnityEngine.Object.FindObjectsOfType<Factory>();
        for (int i = 0; i < factories.Length; i++)
        {
            Unit? attached = factories[i] == null ? null : factories[i].attachedUnit;
            if (attached != null && !attached.disabled && attached.NetworkHQ == hq)
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>
    /// A spot beside something that faction already owns, so a build lands in its own rear. Each
    /// candidate is put through the same <see cref="CommanderBuildPreview.IsSiteAllowed"/> the
    /// player's ghost uses, so the enemy cannot drop a factory across the highway or inside
    /// another building — which is what left its own convoys stuck and its aircraft taxiing into
    /// walls. Several tries, because one throw of the dart lands on a road often enough.
    /// </summary>
    private bool TryFindEnemyBuildSite(FactionHQ hq, BuildingDefinition? definition, ref GlobalPosition site)
    {
        for (int attempt = 0; attempt < EnemySiteAttempts; attempt++)
        {
            if (!TryPickEnemyBuildSite(hq, ref site))
            {
                return false;
            }

            if (definition == null || preview.IsSiteAllowed(definition, site, hq, out _))
            {
                return true;
            }
        }

        return false;
    }

    private bool TryPickEnemyBuildSite(FactionHQ hq, ref GlobalPosition site)
    {
        if (hq.factionUnits == null)
        {
            return false;
        }

        Unit? anchor = null;
        int seen = 0;
        foreach (PersistentID id in hq.factionUnits)
        {
            if (!id.TryGetUnit(out Unit unit) || unit == null || unit.disabled || unit is not Building)
            {
                continue;
            }

            // Reservoir sample so builds spread across the faction's buildings instead of always
            // stacking on whichever one happens to be first in the list.
            seen++;
            if (anchor == null || Random.Range(0, seen) == 0)
            {
                anchor = unit;
            }
        }

        if (anchor == null)
        {
            return false;
        }

        float angle = Random.Range(0f, Mathf.PI * 2f);
        float distance = Random.Range(150f, 350f);
        GlobalPosition anchorPosition = anchor.transform.GlobalPosition();
        site = new GlobalPosition(
            anchorPosition.x + Mathf.Cos(angle) * distance,
            anchorPosition.y,
            anchorPosition.z + Mathf.Sin(angle) * distance);
        return true;
    }
}
