using System.Collections.Generic;
using NuclearOption.SavedMission;
using UnityEngine;

namespace GroundControlRts;

/// <summary>
/// An opposing commander that buys reinforcements out of <c>factionFunds</c> — the same pot the
/// player's depot buys out of, under the same rules. The Basegame only ever deploys the vehicle
/// reserve a mission was authored with and never buys anything, so a faction that converts income
/// into reinforcements is the difference between a sandbox and a game.
/// </summary>
/// <remarks>
/// The design goal is a fair duel: in MATCHED mode both commanders start on the same balance and
/// earn at the same rate, and neither gets a stipend, so the only thing that separates them is
/// what they spend it on. That choice is the <see cref="EnemyPlan"/> — the enemy reads what the
/// player is fielding and commits to the plan that counters it, which the player then has to
/// counter back.
///
/// ponytail: this buys and shapes the composition, then hands the units to the Basegame depot
/// deployment and its objective-seeking ground AI rather than issuing routes of its own.
/// Upgrade to real orders only once buying alone stops being enough of a fight.
/// </remarks>
internal sealed partial class CommanderEnemyCommanderService : ICommanderTickPersistent, ICommanderResetSession
{
    internal const int ModeOff = 0;
    internal const int ModeMatched = 1;
    internal const int ModeMission = 2;

    private const float ReviewIntervalSeconds = 30f;

    /// <summary>
    /// Share of the pot spent per review. Scale-free on purpose: faction funds are denominated in
    /// millions and every mission authors its own balance, so an absolute reserve floor cannot be
    /// calibrated. A quarter every 30 s is the tempo knob, not an advantage — it is what stops the
    /// commander dumping its whole balance the moment the mission starts.
    /// </summary>
    private const float SpendFraction = 0.25f;
    private const int PurchasesPerReview = 3;

    /// <summary>
    /// Reviews a new plan has to stay indicated before the commander switches to it. Without this
    /// the enemy re-counters inside one review and the player never gets to cash in a counter.
    /// </summary>
    private const int PlanCommitReviews = 2;

    /// <summary>
    /// The mod's own duel map is the one mission where the enemy commander is not an option the
    /// player has to find in a menu — it is the opponent, so it runs whether or not the setting is
    /// on and it plays harder. These are the knobs for "harder": a bigger opening balance, a
    /// faster spend, more purchases per review, and an air force that is allowed to be bigger than
    /// the mission authored. Everything else is still the same economy the player is on.
    /// </summary>
    private const float DuelHeadStart = 1.5f;
    private const float DuelSpendFraction = 0.45f;
    private const int DuelPurchasesPerReview = 5;

    /// <summary>Purchases per review while an operations attack requisition is open, in place of
    /// the 3 / 5 the tempo knob normally uses (design SS4) — an attack asking for bodies needs more
    /// than one purchase a review to actually field them before the pressure clock fires again.</summary>
    private const int OffensivePurchasesPerReview = 5;

    /// <summary>Skipped buy reviews between two "holds" log lines. Every review would be noise;
    /// never would leave a silent commander looking broken, which is how this constant came to be.</summary>
    private const int HoldReportEveryReviews = 4;

    private readonly Dictionary<FactionHQ, CommanderState> states = new();

    /// <summary>HQs whose airframe list has already been written to the log. See LogAirRosterOnce.</summary>
    private readonly HashSet<FactionHQ> loggedAirRoster = new();

    /// <summary>HQs whose ground vehicle list has already been written to the log. See CollectCatalog.</summary>
    private readonly HashSet<FactionHQ> loggedGroundRoster = new();

    /// <summary>Airframes the enemy has in the air, and when each entered it. See ReportLostAircraft.</summary>
    /// <summary>What the loss line needs about an airframe once the object is gone: when it was
    /// first seen in the world, its type, and where it last was (updated every review), so a loss
    /// can be placed — at its own deck, over the front, or somewhere in between (fix, 2026-09-15:
    /// four enemy fighters "lost after 30 s in the air" with nothing to say where).</summary>
    private sealed class TrackedAirframe
    {
        internal float Since;
        internal string Name = string.Empty;
        internal GlobalPosition LastPosition;
        internal float LastRadarAlt;

        /// <summary>The faction that owns this airframe (delivery-bypass_20260916). The tracker is
        /// ONE table shared by every commanded faction, and the loss sweep used to drain all of it
        /// under whichever HQ the review loop happened to reach first — so in a 38,388-line log every
        /// one of the 1,227 fate lines was written against the enemy and the player's own transports
        /// had none, which is exactly the telemetry gap the delivery survey could not close. Each
        /// entry now says whose it is and the sweep reports only its own.</summary>
        internal FactionHQ? Owner;
    }

    private readonly Dictionary<Aircraft, TrackedAirframe> airborneSince = new();

    /// <summary>Airframes that went back into stock or were written off on the deck — told to us by
    /// the same hooks the attrition ledger reads (fix, 2026-09-15). The loss line used to call every
    /// one of them "lost … 0.1 km from Sandrift Airbase at 0 m above ground" after a ten-minute
    /// sortie, which read as a deck death and was a landing.</summary>
    private readonly HashSet<Aircraft> recoveredAirframes = new();
    private readonly List<Aircraft> lostAircraft = new();

    private readonly List<FactionHQ> staleHqs = new();
    private readonly List<VehicleDefinition> catalog = new();
    private readonly List<VehicleDefinition> candidates = new();

    /// <summary>Order-book roles for the current purchase, best first, and what this review has
    /// already bought per role; both reused across reviews rather than allocated each time.</summary>
    private readonly List<CommanderPlatoonRole> openRoles = new();
    private readonly int[] boughtThisReview = new int[CommanderOperationsService.RoleCount];
    private float nextReviewAt;
    private float nextDefenceAt;

    internal static CommanderEnemyCommanderService? Instance { get; private set; }

    /// <summary>True on the mission the mod ships, which plays itself.</summary>
    internal static bool IsDuelMission =>
        CommanderFeatureGate.MissionName.IndexOf(PluginInfo.DuelMissionName, System.StringComparison.OrdinalIgnoreCase) >= 0;

    /// <summary>
    /// The mode actually in force. The duel forces MATCHED when the setting is off, so the built-in
    /// map has an opponent out of the box; every other mission still does exactly what the setting says.
    /// </summary>
    internal static int EffectiveMode
    {
        get
        {
            int mode = Mathf.Clamp(CommanderSettings.EnemyCommanderMode, ModeOff, ModeMission);
            return mode == ModeOff && IsDuelMission ? ModeMatched : mode;
        }
    }

    internal CommanderEnemyCommanderService()
    {
        Instance = this;
        nextReviewAt = 0f;
    }

    /// <summary>Units this session's enemy commanders have bought, for the settings readout. Enemy
    /// commanders only: what the player's own commander bought is counted separately, so the ENEMY
    /// COMMANDER button keeps reading what it always read.</summary>
    internal int TotalPurchases { get; private set; }

    /// <summary>Units the player's own commander has bought this session, for the PLAYER COMMANDER
    /// button.</summary>
    internal int PlayerPurchases { get; private set; }

    /// <summary>Plan and balance of the best-funded enemy commander, for the HUD readout.</summary>
    internal string StatusLine { get; private set; } = string.Empty;

    /// <summary>Plan and balance of the player's own commander, for the second HUD row. Empty
    /// whenever the player commander switch is off, which is what hides that row.</summary>
    internal string PlayerStatusLine { get; private set; } = string.Empty;

    /// <summary>Books a purchase against whichever commander made it. One definition so the two
    /// readouts can never drift apart.</summary>
    private void RecordPurchase(FactionHQ hq)
    {
        if (ReferenceEquals(hq, CommanderGameAccess.GetLocalHq()))
        {
            PlayerPurchases++;
            return;
        }

        TotalPurchases++;
    }

    internal static string GetModeLabel(int mode)
    {
        return mode switch
        {
            ModeMatched => "MATCHED",
            ModeMission => "MISSION FUNDS",
            _ => "OFF",
        };
    }

    internal static string GetPlanLabel(EnemyPlan plan)
    {
        return plan switch
        {
            EnemyPlan.AirDefence => "AIR DEFENCE",
            EnemyPlan.FireSupport => "FIRE SUPPORT",
            EnemyPlan.Spearhead => "SPEARHEAD",
            _ => "RECON SCREEN",
        };
    }

    /// <summary>The COMMANDER LOG header's PLAN readout for a given HQ, "NONE" before its first
    /// review has ever run.</summary>
    internal string GetPlanLabel(FactionHQ hq)
    {
        return states.TryGetValue(hq, out CommanderState state) ? GetPlanLabel(state.Plan) : "NONE";
    }

    /// <summary>
    /// One runnable check for the counter triangle, run once at plugin load next to
    /// <see cref="CommanderServiceRegistryCheck"/>, because a Unity plugin has nowhere else to
    /// run a test. If a threshold in <see cref="ChoosePlan"/> is retuned and a whole plan stops
    /// being reachable, this is what says so in the BepInEx console.
    /// </summary>
    internal static void SelfCheck()
    {
        CheckPlan("air wing", new ForceRead { Aircraft = 4, Ground = 2, Armour = 2 }, EnemyPlan.AirDefence);
        CheckPlan("armoured push", new ForceRead { Armour = 6, Ground = 8 }, EnemyPlan.FireSupport);
        CheckPlan("static line", new ForceRead { Guns = 2, AirDefence = 3, Ground = 6 }, EnemyPlan.Spearhead);
        CheckPlan("nothing dominant", new ForceRead { Ground = 2, Armour = 1, Guns = 1 }, EnemyPlan.ReconScreen);
        CheckPlan("empty field", default, EnemyPlan.ReconScreen);
        CheckAirRoles();
        // Wired in 2026-09-14: this check was written for the 2026-09-13 air track and never called,
        // so a retune that reordered ChooseAirRole or retyped the Cricket would have passed silently.
        CheckAirBuyRules();
        CheckDefencePosture();
        CheckLadder();
        CheckSpawnDepotPick();
        CheckCaptureVehicleRule();
    }

    /// <summary>
    /// The capture-unit rule at the boundary that broke it (2026-09-18). A garrison and an airbase
    /// capture unit are both bought through <see cref="CaptureVehicleQualifies"/>, so a retune that
    /// re-admits the logistics types puts unarmed ammo trucks back on every held point.
    /// </summary>
    private static void CheckCaptureVehicleRule()
    {
        ExpectCapture(
            "an ammo truck is never a capture unit, however cheap it is",
            CaptureVehicleQualifies(VehicleType.TRUCK, captureStrength: 1f, value: 2f, budget: 1000f),
            false);
        ExpectCapture(
            "an unmanned ground vehicle is never a capture unit",
            CaptureVehicleQualifies(VehicleType.UGV, captureStrength: 1f, value: 2f, budget: 1000f),
            false);
        ExpectCapture(
            "a troop carrier that can take ground is a capture unit",
            CaptureVehicleQualifies(VehicleType.AFV, captureStrength: 1f, value: 120f, budget: 1000f),
            true);
        ExpectCapture(
            "a light carrier that can take ground is a capture unit",
            CaptureVehicleQualifies(VehicleType.LCV, captureStrength: 1f, value: 80f, budget: 1000f),
            true);
        ExpectCapture(
            "a tank that cannot take ground is not a capture unit",
            CaptureVehicleQualifies(VehicleType.MBT, captureStrength: 0f, value: 300f, budget: 1000f),
            false);
        ExpectCapture(
            "a capture unit priced exactly at the budget is affordable",
            CaptureVehicleQualifies(VehicleType.AFV, captureStrength: 1f, value: 120f, budget: 120f),
            true);
        ExpectCapture(
            "a capture unit one above the budget is refused",
            CaptureVehicleQualifies(VehicleType.AFV, captureStrength: 1f, value: 121f, budget: 120f),
            false);
    }

    private static void ExpectCapture(string name, bool actual, bool expected)
    {
        if (actual != expected)
        {
            CommanderPlugin.Log.LogError(
                $"Capture vehicle self-check FAILED ({name}): expected {expected}, got {actual}.");
        }
    }

    /// <summary>
    /// The nearest-depot pick at its named boundaries (reach-and-points Section 4). The live pick
    /// walks live depots; <c>CommanderEconomyService.NearestUsableIndex</c> is that rule with the
    /// objects taken out, so "nearer wins, a depot that cannot be used is skipped, none at all means
    /// fall back to supply" is checked at load.
    /// </summary>
    private static void CheckSpawnDepotPick()
    {
        Expect(
            "the nearer of two working depots spawns the vehicle",
            CommanderEconomyService.NearestUsableIndex(
                new List<float> { 9000f, 2000f, 30000f }, new List<bool> { true, true, true }),
            1);
        Expect(
            "a depot that cannot be used is skipped however near it is",
            CommanderEconomyService.NearestUsableIndex(
                new List<float> { 100f, 5000f }, new List<bool> { false, true }),
            1);
        Expect(
            "a commander with no usable depot at all falls back to supply",
            CommanderEconomyService.NearestUsableIndex(
                new List<float> { 100f, 5000f }, new List<bool> { false, false }),
            -1);
        Expect(
            "a commander with no depot at all falls back to supply",
            CommanderEconomyService.NearestUsableIndex(new List<float>(), new List<bool>()),
            -1);
        Expect(
            "two depots the same distance away keep the first",
            CommanderEconomyService.NearestUsableIndex(
                new List<float> { 5000f, 5000f }, new List<bool> { true, true }),
            0);
    }

    private static void CheckPlan(string name, in ForceRead force, EnemyPlan expected)
    {
        EnemyPlan actual = ChoosePlan(force);
        if (actual != expected)
        {
            CommanderPlugin.Log.LogError(
                $"Enemy plan self-check FAILED ({name}): expected {GetPlanLabel(expected)}, got {GetPlanLabel(actual)}.");
        }
    }

    public void TickPersistent()
    {
        int mode = EffectiveMode;

        // The HQ check comes before the schedule on purpose. This ticks from the menu onwards, and
        // consuming a review while no mission is loaded is what put the first purchase up to a
        // review behind the start of the match — the enemy has to be spending from minute zero.
        FactionHQ? localHq = CommanderGameAccess.GetLocalHq();
        if (!CommanderPlayerCommanderService.AnyCommanderOn || localHq == null)
        {
            // Cleared here as well as by PruneStates: with the enemy commander OFF the review loop
            // below never runs again once the player switch goes off, so without this the YOU row
            // would keep showing the last plan for the rest of the mission. StatusLine is left
            // alone on purpose — the ENEMY row's behaviour is unchanged.
            PlayerStatusLine = string.Empty;
            return;
        }

        // The home guard runs on its own, faster clock. An attack the commander can see coming has
        // to move units before it lands, and the buy review is half a minute wide.
        if (CommanderScheduler.IsDue(ref nextDefenceAt, DefenceReviewIntervalSeconds))
        {
            ReviewDefences(localHq);
        }

        if (!CommanderScheduler.IsDue(ref nextReviewAt, ReviewIntervalSeconds))
        {
            return;
        }

        FactionHQ? primary = null;
        foreach (FactionHQ hq in FactionRegistry.GetAllHQs())
        {
            if (hq == null
                || !CommanderPlayerCommanderService.IsCommanded(hq, localHq)
                || !hq.IsServer
                || hq.faction == null)
            {
                continue;
            }

            // Read per commander rather than once before the loop: every commander but the player's
            // is playing the local HQ, so this is the same struct it used to be, but the player's
            // own commander has to read whoever it is actually up against.
            FactionHQ opponent = CommanderPlayerCommanderService.ChooseOpponent(hq, localHq);
            ForceRead opponentForce = ReadForce(opponent);
            Review(hq, localHq, opponent, mode, opponentForce);

            // The ENEMY row stays about the enemy. The player's own commander has its own HUD row,
            // so it must never win this pick and hide the plan the player is supposed to counter.
            if (!ReferenceEquals(hq, localHq) && (primary == null || hq.factionFunds > primary.factionFunds))
            {
                primary = hq;
            }
        }

        PruneStates(localHq);
        StatusLine = primary != null && states.TryGetValue(primary, out CommanderState primaryState)
            ? $"{GetPlanLabel(primaryState.Plan)}   {FundsLabel(primary.factionFunds)}"
                + (primaryState.Defending ? "   DEFENDING" : string.Empty)
            : string.Empty;

        // PruneStates has just dropped the local HQ's state unless the player commander is on, so
        // this row empties itself the review after the switch goes off.
        PlayerStatusLine = states.TryGetValue(localHq, out CommanderState playerState)
            ? $"{GetPlanLabel(playerState.Plan)}   {FundsLabel(localHq.factionFunds)}"
                + (playerState.Defending ? "   DEFENDING" : string.Empty)
            : string.Empty;
    }

    public void ResetSession()
    {
        states.Clear();
        loggedAirRoster.Clear();
        loggedGroundRoster.Clear();
        airborneSince.Clear();
        groundLossRefunds.Clear();
        lostAircraft.Clear();
        staleHqs.Clear();
        catalog.Clear();
        candidates.Clear();
        reconUnits.Clear();
        shipCatalog.Clear();
        defenceCandidates.Clear();
        staleDefenders.Clear();
        // The air candidate catalog re-resolves on the next buy — the encyclopedia/resource scan is
        // a once-per-mission cost by design (see RefreshAirCatalog).
        airCatalog.Clear();
        airCatalogResolved = false;
        TotalPurchases = 0;
        PlayerPurchases = 0;
        StatusLine = string.Empty;
        PlayerStatusLine = string.Empty;
        nextReviewAt = 0f;
        nextDefenceAt = 0f;
    }

    /// <summary>
    /// This commander's whole review: prep, plan, posture, then spend — one pot per review, spent
    /// top-down through the priority ladder (design.md, commander-priorities_20260914): a strict
    /// home CAP first, then platoons and their air, pickets and buildings sharing the remainder by
    /// a weighted draw. Inside the platoon rung the operations order book comes first when it is
    /// open; with the book empty the plan-based counter triangle buys as before (design SS4).
    /// </summary>
    private void Review(FactionHQ hq, FactionHQ localHq, FactionHQ opponent, int mode, in ForceRead opponentForce)
    {
        if (!states.TryGetValue(hq, out CommanderState state))
        {
            state = new CommanderState();
            states[hq] = state;
            // BEFORE ShouldOpenTreasury below, which is the whole point: a strategic load puts the
            // war chest back but the state that remembers the treasury was already opened is
            // created here, on the first review after the load. Seeding it any later hands every
            // computer faction a second opening balance on top of the restored money, silently.
            ApplyStrategicPrepared(hq, state);
        }

        bool duel = IsDuelMission;
        bool isLocal = ReferenceEquals(hq, localHq);
        // Both openers are about putting an opposing faction on the player's economy, so both are
        // skipped for the player's own commander: no matched balance copied onto itself, no duel
        // head start. The switch hands the player a staff officer, not a different mission. The
        // gate is a pure function so the "once per match" half of it is checkable at load — see
        // CommanderEconomyService.ShouldOpenTreasury, which lives beside the rest of the difficulty
        // rule rather than here.
        // A strategic save for this mission has not been read yet: hold the opener rather than race
        // it. This review fires on the commander's very first tick (nextReviewAt starts at zero), so
        // relying on the restore landing first inside one frame is not good enough — and that is
        // exactly what went wrong the first time this shipped, when a restarted mission took the
        // duel head start on top of what the save was about to restore. Holding costs one review of
        // delay and removes the ordering question entirely; the hold lifts the moment the store has
        // decided anything at all, including "there is no save".
        if (CommanderStrategicSaveStore.IsOpeningBalanceHeld)
        {
            CommanderPlugin.Log.LogInfo(
                $"{hq.faction?.factionName}: opening balance held for one review — a strategic save for this "
                    + "mission has not been read yet.");
            return;
        }

        // ShouldOpenTreasury's "already prepared" half is pinned at plugin load by
        // CommanderEconomyService.CheckDifficulty; a strategic load restores state.Prepared above so
        // that same rule keeps holding across a mission restart rather than handing every computer
        // faction a second opening balance.
        if (CommanderEconomyService.ShouldOpenTreasury(isLocal, state.Prepared))
        {
            if (mode == ModeMatched)
            {
                LevelEconomy(hq, localHq);
            }
            if (duel)
            {
                PrepareDuel(hq);
            }

            // Last, on whatever the openers left in the balance — including the balance the mission
            // itself authored, when neither opener ran.
            ApplyDifficultyToOpeningFunds(hq);
        }
        else if (isLocal && !state.Prepared)
        {
            CommanderAiLog.Note(hq, "keeps the player's own economy: no head start, no fund reset.");
        }

        state.Prepared = true;

        if (duel)
        {
            RevealPlayerBase(hq, opponent);
        }

        UpdatePlan(hq, state, opponentForce);

        // Posture comes before spending on purpose: a commander with an empty balance still has to
        // fly the aircraft and drive the radars it already owns.
        ReviewPosture(hq, opponent, state, opponentForce);

        // One pot per review (user decision 2026-09-14, design.md commander-priorities_20260914):
        // the whole balance times the tempo fraction, spent top-down by the priority ladder in
        // ReviewPurchases. The reserve hold-back (GetEnemyBuildReserve) and the quarter-of-balance
        // unit floor (UnitSpendFloorShare) that used to carve this pot up are gone — both were
        // fixes for the same starvation (spenders draining one shared balance faster than a big
        // purchase could accumulate), and the ladder prevents that structurally instead: priority
        // is decided once at the top, and everything below spends only what its rung is granted.
        // An open attack requisition raises the tempo for this review only (design SS4): an attack
        // waiting on bodies needs more of the pot than the duel/matched tempo knob normally allows.
        float fraction = CommanderOperationsService.HasOpenAttackRequisition(hq)
            ? CommanderSettings.OperationsOffensiveSpendFraction
            : (duel ? DuelSpendFraction : SpendFraction);
        float spendable = hq.factionFunds * fraction;
        CollectCatalog(hq);
        if (spendable <= 0f)
        {
            ReportHold(hq, state, spendable, string.Empty);
            return;
        }

        int boughtCount = ReviewPurchases(hq, state, duel, opponent, opponentForce, spendable);
        if (boughtCount > 0)
        {
            state.SkippedBuyReviews = 0;
        }
    }

    /// <summary>
    /// One log line every <see cref="HoldReportEveryReviews"/> reviews in which the commander bought
    /// no vehicle, saying why in numbers: the reason (the strict home-CAP hold, when that is what
    /// stopped the review), the balance, what was left to spend, and how many vehicle types were on
    /// offer. Diagnostic only.
    /// </summary>
    private void ReportHold(FactionHQ hq, CommanderState state, float spendable, string reason)
    {
        state.SkippedBuyReviews++;
        if (state.SkippedBuyReviews % HoldReportEveryReviews != 1)
        {
            return;
        }

        string why = string.IsNullOrEmpty(reason)
            ? string.Empty
            : $"{reason}; ";
        CommanderAiLog.Note(
            hq,
            $"holds: {why}balance {hq.factionFunds:0}, unit budget {spendable:0}, "
                + $"{catalog.Count} vehicle types on offer ({state.SkippedBuyReviews} quiet reviews).");
    }

    /// <summary>
    /// The priority ladder's spend walk (design.md, commander-priorities_20260914). Rung 1 — the home
    /// CAP — draws first and is strict, but only up to the BASELINE (user decision 2026-09-14,
    /// revised): while fewer than <c>HomeCapBaseline</c> fighters are alive the review ends here and
    /// nothing below it is bought; everything the formula wants above the baseline is ordinary CAP
    /// demand inside rung 2. The survivors share the remainder by a weighted draw each review —
    /// platoons 60 / pickets 20 / buildings 20, with a floor for every rung with open demand.
    /// Returns how many vehicles it bought this review.
    /// </summary>
    private int ReviewPurchases(FactionHQ hq, CommanderState state, bool duel, FactionHQ opponent, in ForceRead opponentForce, float spendable)
    {
        int boughtCount = 0;
        float pot = spendable;

        // Last cycle's flights come off the bank before anything else (departure 2026-09-14): the
        // operations review charges them on its own clock, so this is where the ladder learns what
        // was spent — and the same number is what the line at the end of the review reports.
        float picketSpent = CommanderOperationsService.TakeInsertionSpend(hq);
        state.PicketSavings = Mathf.Max(0f, state.PicketSavings - picketSpent);

        // ---- Rung 1: home CAP, strict (design Section 2) ----
        HomeCapRead cap = ReviewHomeCap(hq, state, spendable);
        boughtCount += cap.Buys;
        spendable -= cap.Spent;
        if (cap.Impossible)
        {
            // The deadlock valve (user follow-up, 2026-09-14): no air-to-air-capable airframe can
            // launch from any base this commander holds, so a strict hold would freeze the whole
            // ladder for the rest of the match. The rung is skipped, the periodic holds line says
            // which strips were found incapable, and the lower rungs proceed.
            ReportHold(
                hq,
                state,
                spendable,
                $"home CAP impossible — no air-to-air-capable airframe can launch from {HeldBaseNames(hq)}");
        }
        else if (LadderHoldsForCap(cap.Alive, cap.Baseline, cap.CeilingBlocked))
        {
            // No draw ran, so the order list is last review's — empty it or the line would print a
            // draw that never happened.
            ladderOrder.Clear();
            ReportHold(
                hq,
                state,
                spendable,
                $"home CAP below its baseline: {cap.Alive}/{cap.Baseline} alive (the formula wants {cap.Wanted}, "
                    + "the rest is rung 2's)");
            ReportLadder(hq, in cap, ladderOrder, pot, cap.Spent, 0f, picketSpent, 0f, state.AirFund, state.AirFundCap, state.Rung2AirReserved, state.AwacsSavings, state.AwacsSavingsTarget, state.PicketSavings, state.PicketSavingsTarget, CommanderOperationsService.WantsAwacsPurchase(hq));
            return boughtCount;
        }

        // ---- Rungs 2-4: the weighted draw with a floor (design Section 3) ----
        // An expansion with nothing that can take ground is an expansion that never happens, so a
        // capture unit outranks the plan exactly the way the first air-defence launcher does.
        bool needsCaptureUnit = CommanderCaptureService.Instance?.WantsCaptureUnit(hq) == true;
        bool hasOpenBook = CommanderOperationsService.HasOpenRequisition(hq);
        bool bookOnly = CommanderOperationsService.GroundBuyingBookOnly(
            CommanderOperationsService.OwnsGroundForce(hq),
            hasOpenBook);
        if (bookOnly != state.GroundBookOnly)
        {
            state.GroundBookOnly = bookOnly;
            CommanderAiLog.Note(
                hq,
                bookOnly
                    ? "holds ground purchases: every vehicle is bought to order and the order book is empty."
                    : "resumes ground purchases: the order book has an open line.");
        }

        // Rung 2's demand: the wing's sorties, the naval market, or any ground want at all. Since
        // DECISION-013 a commander whose ground force this service owns demands only what its order
        // book asks for; an undiscovered one still keeps the rung demanding on plan.
        bool platoonDemand = !bookOnly
            || needsCaptureUnit
            || state.WantsReconUnit
            || CommanderOperationsService.HasAirDemand(hq)
            || WantsNavalHull(hq);
        // Rung 3's demand: a picket point that would ask for a flight. Rung 4's: anything the
        // economy wants next, or a building that needs a repair crew.
        bool picketDemand = CommanderOperationsService.HasInsertionDemand(hq);
        bool buildingDemand = CommanderEconomyService.NextEnemyStructureCost(hq) > 0f
            || CommanderRepairService.Instance?.WantsEnemyRepairCrew(hq) == true;

        // The draw: a weighted random permutation of the demanding rungs (design Section 3,
        // user decision 2026-09-14 — variability per review, not per commander).
        ladderWeights[RungPlatoons] = platoonDemand ? CommanderSettings.LadderPlatoonWeight : 0f;
        ladderWeights[RungPickets] = picketDemand ? CommanderSettings.LadderPicketWeight : 0f;
        ladderWeights[RungBuildings] = buildingDemand ? CommanderSettings.LadderBuildingWeight : 0f;
        ladderOrder.Clear();
        while (true)
        {
            int pick = LadderDrawPick(ladderWeights, Random.value * LadderWeightTotal(ladderWeights));
            if (pick < 0)
            {
                break;
            }

            ladderOrder.Add(pick);
            ladderWeights[pick] = 0f;
        }

        float pool = spendable;
        float floor = LadderFloor(pool, CommanderSettings.LadderRungFloorPercent);
        float platoonSpent = 0f;
        float buildingSpent = 0f;
        for (int position = 0; position < ladderOrder.Count; position++)
        {
            // Every rung in the order has demand by construction, so the floors still owed are the
            // demanding rungs drawn after this one — the floor is what guarantees them a share
            // however greedy the rungs drawn first are.
            int rung = ladderOrder[position];
            float budget = LadderRungBudget(pool, floor, ladderOrder.Count - position - 1);
            if (rung == RungPickets)
            {
                // Rung 3 BANKS its allocation (departure 2026-09-14) rather than being handed a
                // fresh share every review. An insertion flight is a transport hull plus two
                // vehicles — around 134 on the 2026-09-14 match — while a 20 % rung of a post-CAP
                // remainder near 100 grants 10 to 30, and the share used to be recomputed from
                // nothing each review: every flight of that match was refused with "the ladder's
                // picket share cannot cover the flight" and no picket was ever inserted. The bank
                // stops at exactly one flight's price, the same shape as the radar airframe's
                // slice and the building rung's savings, so a rung drawn first cannot hoard the
                // remainder against the rungs below.
                float target = PicketFlightPrice(hq);
                state.PicketSavingsTarget = target;
                float excess = PicketSavingsExcess(state.PicketSavings, target);
                if (excess > 0f)
                {
                    // A cheaper transport, a strip lost, a roster that stopped fielding vehicle
                    // cargo: what the bank may no longer hold goes back into this review's pool for
                    // the rungs drawn after, exactly as the air fund's own overflow does.
                    pool += excess;
                    state.PicketSavings = target;
                    budget = LadderRungBudget(pool, floor, ladderOrder.Count - position - 1);
                }

                // Unlike the old grant, what the bank takes IS withheld from the pool: it is money
                // reserved for a flight that has not been ordered yet, and a later rung spending it
                // as well would be the double-spend the ladder exists to prevent.
                pool -= AccrueFund(ref state.PicketSavings, budget, target);
                CommanderOperationsService.GrantInsertionAllowance(hq, state.PicketSavings, target);
                continue;
            }

            if (budget <= 0f)
            {
                continue;
            }

            if (rung == RungPlatoons)
            {
                platoonSpent = SpendPlatoons(
                    hq, state, duel, opponent, opponentForce, budget, hasOpenBook, bookOnly, needsCaptureUnit, ref boughtCount);
                pool -= platoonSpent;
            }
            else
            {
                buildingSpent = CommanderEconomyService.Instance?.SpendEnemyStructures(hq, budget) ?? 0f;
                pool -= buildingSpent;
            }
        }

        // A rung with no demand this cycle must not fly on a stale allowance from the last one. The
        // BANK survives — no demand this review is not money spent, and the next point that goes
        // short finds the savings where the rung left them.
        if (!picketDemand)
        {
            CommanderOperationsService.GrantInsertionAllowance(hq, 0f, state.PicketSavingsTarget);
        }

        // The review's hold line lives here now, not in Review: the strict path reports its own
        // hold above, so a second report from the caller would count the same review twice against
        // the throttle.
        if (boughtCount == 0)
        {
            ReportHold(hq, state, Mathf.Max(0f, pot - cap.Spent - platoonSpent - buildingSpent), string.Empty);
        }

        ReportLadder(hq, in cap, ladderOrder, pot, cap.Spent, platoonSpent, picketSpent, buildingSpent, state.AirFund, state.AirFundCap, state.Rung2AirReserved, state.AwacsSavings, state.AwacsSavingsTarget, state.PicketSavings, state.PicketSavingsTarget, CommanderOperationsService.WantsAwacsPurchase(hq));
        return boughtCount;
    }

    /// <summary>
    /// Rung 2: platoons and their air support (design Section 3's internal order) — the wing's
    /// sorties first (CAP demand, then CAS, then the pre-emptive baseline — the demand queue's own
    /// order), then the naval share (the one pot the ladder deliberately kept inside this rung,
    /// unchanged in its behaviour: dock-gated, hull-capped, saved across reviews), then the order
    /// book by <c>OpenRolesByPriority</c>, the capture and recon overrides, and the plan buyer
    /// and — only for a commander whose ground force the operations service does NOT own — the
    /// capture and recon overrides and the plan buyer. <c>GroundBuyingBookOnly</c> decides which of
    /// those two worlds this commander is in (DECISION-013). Returns what it took out of the rung's
    /// budget; what it does not spend flows to the rungs drawn after it.
    /// </summary>
    private float SpendPlatoons(
        FactionHQ hq,
        CommanderState state,
        bool duel,
        FactionHQ opponent,
        in ForceRead opponentForce,
        float budget,
        bool hasOpenBook,
        bool bookOnly,
        bool needsCaptureUnit,
        ref int boughtCount)
    {
        float grant = budget;

        // Whatever the plan says, a commander with no air defence at all while the player is
        // flying is not playing the same game. One launcher first, then back to the plan.
        bool blindToAir = opponentForce.Aircraft > 0 && CountAirDefence(hq) == 0;
        int purchases = CommanderOperationsService.HasOpenAttackRequisition(hq)
            ? OffensivePurchasesPerReview
            : (duel ? DuelPurchasesPerReview : PurchasesPerReview);

        // The once-per-review demand read (design SS4, chatty detail behind OperationsDebugLog):
        // what the wing is short of and what caps it, so a quiet sky in a rich match is explained
        // by one line instead of silence.
        if (CommanderSettings.OperationsDebugLog && hq.faction != null)
        {
            CommanderOperationsService.ReadAirDemandCounts(hq, out int capBound, out int capWanted, out int casBound, out int casWanted);
            CommanderPlugin.Log.LogInfo(
                $"Ops {CommanderPlayerCommanderService.CommanderLabel(hq)}: air demand: CAP {capBound}/{capWanted}, "
                    + $"CAS {casBound}/{casWanted}, ceiling {CountAirborne(hq)}/{CommanderOperationsService.EffectiveAirborneCeiling(hq)}, "
                    + $"budget {budget:0}.");
        }

        // The wing buys straight out of what the rung was granted (design Section 1): the 40 % air
        // fund that used to be set aside first is one of the pots the ladder retired — the fund
        // existed because a flat per-review slice could never add up to an airframe while convoys
        // drained the balance, and the ladder's grant is the structural answer to the same
        // starvation. What it buys still flies the sorties the ground plan asked for, bounded by
        // the fund and the airborne ceiling (user decision 2026-09-14: money-limited, not
        // count-limited — the three-buys-per-review cap of 2026-09-13 is superseded).
        // ... except that a per-review slice never reached the price of a strike airframe: the wing
        // asked for a 36-value Brawler or a 145-value Medusa out of whatever was left after the
        // home CAP's replacements, and logged "its air budget is short of the cheapest … airframe"
        // every review of the 2026-09-14 match. The slice now ACCUMULATES, exactly the way the
        // naval hull's does and for exactly the same reason (Reuse rule 4, one AccrueFund): a share
        // set aside each review, bounded at a few reviews' worth so a wing that can never spend it
        // does not withhold it from the rest of the rung forever.
        // ... and the ceiling it accumulates to is the PRICE OF WHAT IT IS SAVING FOR (fix,
        // 2026-09-14), not six reviews of the share. The share-based ceiling had no relationship to
        // any airframe, so a rich review banked far past the dearest thing the wing wanted and the
        // ground never saw the money: `air saved 372` on the ladder line while the order book sat
        // six lines deep. Anything already above the ceiling — a demand that closed, a strip lost —
        // flows straight back into this review's pot before the share is taken.
        float airCeiling = AirFundCeiling(hq, state);
        state.AirFundCap = airCeiling;
        if (state.AirFund > airCeiling)
        {
            budget += state.AirFund - airCeiling;
            state.AirFund = airCeiling;
        }

        // "Existing forces first" (user decision 2026-09-14): while a sortie over a platoon or point
        // ACTUALLY FIGHTING is short of an airframe, a third of this rung's allocation is the wing's
        // before the ground buyer sees any of it, and it banks across reviews like the rest of the
        // fund until the airframe is affordable. The ordinary 40 % share is then taken out of the
        // ground's two thirds as before, so a contact review gives the wing more than a quiet one
        // without the quiet one changing at all.
        bool inContactShort = CommanderOperationsService.HasInContactAirShortfall(hq);
        Rung2Split(budget, inContactShort, out float airReserve, out float groundShare);
        state.Rung2AirReserved = inContactShort;
        // The reserve plus the ordinary share of what the ground keeps. Whatever the ceilings refuse
        // flows back to the ground rather than evaporating — the same rule the ceiling overflow
        // above follows, so a reserve the wing cannot use is never money nobody spends.
        float airAsk = airReserve + (groundShare * AirBudgetShare);

        // A quarter of the wing's own allocation is the radar airframe's (user decision
        // 2026-09-14), banked where the fighters cannot reach it. Its ceiling is the price of the
        // cheapest radar airframe the strips accept, so it stops the moment it can pay for one, and
        // it is zero the moment one is owned — at which point whatever is banked is released to the
        // rest of the wing rather than sitting there for the rest of the match.
        bool wantsAwacs = CommanderOperationsService.WantsAwacsPurchase(hq);
        float awacsTarget = wantsAwacs ? AffordablePrice(CheapestLaunchableValue(hq, state, AirRole.Awacs)) : 0f;
        state.AwacsSavingsTarget = awacsTarget;
        AirSplit(airAsk, wantsAwacs, out float awacsAsk, out float turnAsk);
        if (state.AwacsSavings > awacsTarget)
        {
            turnAsk += state.AwacsSavings - awacsTarget;
            state.AwacsSavings = awacsTarget;
        }

        float awacsTaken = AccrueFund(ref state.AwacsSavings, awacsAsk, awacsTarget);
        // What the radar airframe's ceiling would not take is the wing's, not the ground's: it was
        // the air side's allocation before it was ever earmarked.
        float airTaken = AccrueFund(ref state.AirFund, turnAsk + (awacsAsk - awacsTaken), airCeiling);
        budget = Mathf.Max(0f, budget - awacsTaken - airTaken);

        // The ground's unspent share goes to the wing while the ground buyer is holding (user
        // decision 2026-09-14). A commander whose vehicles are all bought to order and whose order
        // book is empty is not going to spend this rung on anything: the 2026-09-14 match printed
        // `holds: balance 491, unit budget 152` review after review while the wing reported
        // thirteen unfilled CAS requests and a 214 budget it could not stretch to a strike airframe.
        // The naval market keeps its own share out of this — a hull is the one ground thing an empty
        // order book still wants — so its ask is priced here, before the hand-over, and paid from
        // exactly that below.
        float navalAsk = budget * NavalBudgetShare;
        CommanderOperationsService.ReadAirDemandCounts(
            hq, out int handoverCapBound, out int handoverCapWanted, out int handoverCasBound, out int handoverCasWanted);
        int openAirRequests = Mathf.Max(0, handoverCapWanted - handoverCapBound)
            + Mathf.Max(0, handoverCasWanted - handoverCasBound);
        bool handsGroundShareToWing = GroundShareGoesToWing(bookOnly, hasOpenBook, openAirRequests > 0);
        float handedToWing = 0f;
        if (handsGroundShareToWing)
        {
            // Through the one accumulator, so the fund's ceiling bounds this exactly as it bounds
            // the ordinary share: money the wing may not save stays on the ground's side of the
            // rung and flows on to the rungs drawn after it.
            handedToWing = AccrueFund(ref state.AirFund, Mathf.Max(0f, budget - navalAsk), airCeiling);
            budget = Mathf.Max(0f, budget - handedToWing);
        }

        if (handsGroundShareToWing != state.GroundShareToWing)
        {
            state.GroundShareToWing = handsGroundShareToWing;
            CommanderAiLog.Note(
                hq,
                handsGroundShareToWing
                    ? $"hands the ground's unspent {handedToWing:0} to the wing: order book empty, "
                        + $"{openAirRequests} air requests open."
                    : "stops handing the ground's share to the wing: the order book has an open line "
                        + "or the wing has asked for nothing.");
        }
        // A failed buy no longer ends the review's air buying outright (fix, 2026-09-14): the
        // demand read advances the CAP/CAS turn whether or not the buy succeeded, so one more call
        // asks the OTHER side of the wing. That is what makes "the ground-attack side still buys
        // when the fighters are saving" true — the 2026-09-14 match refused nine buys in a row
        // because the radar airframe was unaffordable, and every one of those refusals ended the
        // review before any ground-attack demand was even read. Two failures in a row means both
        // sides are stuck, and the review is genuinely over.
        state.AirLastBuyOutcome = string.Empty;
        int airFailures = 0;
        // The savings count toward "the wing still has money": a fund emptied by a strike airframe
        // must not end the review while the radar airframe's own slice is full.
        // Money-limited, not count-limited (user decision 2026-09-14): the loop shops while the
        // wing's money covers the cheapest airframe an open demand wants and the sky has room.
        for (int airBuy = 0; AirBuyContinues(hq, state, airBuy) && airFailures < 2; airBuy++)
        {
            float airSpent = BuyAirframe(hq, state, state.AirFund, opponentForce);
            if (airSpent <= 0f)
            {
                airFailures++;
                continue;
            }

            airFailures = 0;
            // A radar airframe is charged to its own savings first and to the wing's fund only for
            // the remainder, which is what makes the slice a reservation rather than a label.
            float fromAwacs = state.LastAirBuyWasAwacs ? Mathf.Min(state.AwacsSavings, airSpent) : 0f;
            state.AwacsSavings = Mathf.Max(0f, state.AwacsSavings - fromAwacs);
            state.AirFund = Mathf.Max(0f, state.AirFund - (airSpent - fromAwacs));
        }

        budget -= ReviewNaval(hq, opponent, state, navalAsk);

        if (bookOnly)
        {
            return grant - budget;
        }

        // The pool-full hold (user decision 2026-09-15, fix A): the depot loop already refuses to
        // deploy while the idle pool is at PoolIdleCap (Depot/CommanderFactionVehicleService.cs,
        // ShouldBlockAutomaticDeployment), but nothing told the buyer, so every buy banked supply
        // that flooded out as vehicles the moment platoons formed and the pool drained. Same rule,
        // same numbers, one definition (DeploymentHeldForFullPool); logged once per change the way
        // the order-book hold above is.
        int idle = CommanderOperationsService.PoolCount(hq);
        bool poolFull = CommanderFactionVehicleService.DeploymentHeldForFullPool(
            CommanderOperationsService.OwnsGroundForce(hq), idle, CommanderSettings.PoolIdleCap);
        if (poolFull != state.GroundPoolFull)
        {
            state.GroundPoolFull = poolFull;
            CommanderAiLog.Note(
                hq,
                poolFull
                    ? $"holds ground buying: {idle} vehicles idle in the pool (cap {CommanderSettings.PoolIdleCap})."
                    : "resumes ground buying: pool below the cap.");
        }

        // The ground ceiling (design.md, unit-economy_20260918 §2.3). Above it this commander
        // replaces losses but does not grow: the test is against the LIVE ground-vehicle count, so a
        // vehicle lost drops the count and re-opens exactly one purchase on the next review. It sits
        // here, beside the pool-full hold, because the two are the same kind of rule — a reason not
        // to buy that has nothing to do with money — and it is logged once per change for the same
        // reason that one is. Air buying is untouched: it happens above this line, and aircraft were
        // measured at 55 of 427 units and a rounding error in the growth.
        bool ceilingReached = !CommanderOperationsService.GroundBuyAllowed(hq);
        if (ceilingReached != state.GroundCeilingReached)
        {
            state.GroundCeilingReached = ceilingReached;
            int liveGround = CommanderOperationsService.CountLiveGroundVehicles(hq);
            CommanderAiLog.Note(
                hq,
                ceilingReached
                    ? $"stops growing its ground force: {liveGround} vehicles live "
                        + $"(ceiling {CommanderSettings.GroundUnitCeiling}). It replaces losses only."
                    : $"resumes growing its ground force: {liveGround} vehicles live, "
                        + $"below the ceiling of {CommanderSettings.GroundUnitCeiling}.");
        }

        if (ceilingReached)
        {
            return grant - budget;
        }

        System.Array.Clear(boughtThisReview, 0, boughtThisReview.Length);
        for (int purchase = 0; purchase < purchases && budget > 0f; purchase++)
        {
            // Two overrides on the plan, both about a base rather than a front: no air defence at
            // all while the player flies, and a home-guard ring the commander cannot fill out of
            // what it already owns.
            EnemyPlan buyPlan = (purchase == 0 && blindToAir) || (purchase == 1 && state.WantsDefenceUnit)
                ? EnemyPlan.AirDefence
                : state.Plan;

            // Purchase order (user decision 2026-09-13, "platoon tasking should take priority over
            // generic commander actions"): 1. the operations order book — a platoon, forward base
            // or picket that asked for bodies; 2. the capture-unit override; 3. the recon override;
            // 4. the plan-based counter triangle (with the blind-to-air / home-guard plan swaps).
            // The book used to sit behind the two overrides, so a base wanting an expansion unit
            // or overwatch delayed every platoon's replacements by a purchase each review.
            CommanderPlatoonRole? role = null;
            VehicleDefinition? choice = null;
            if (hasOpenBook)
            {
                // Try every open line the book has, truck first then largest first, and take the
                // first affordable one. Falling back to the plan on the first unaffordable role
                // bought a cheap vehicle the recipe could not use, and the truck line never won
                // "largest" so no forward base ever got its truck.
                CommanderOperationsService.OpenRolesByPriority(hq, catalog, boughtThisReview, openRoles);
                for (int r = 0; r < openRoles.Count && choice == null; r++)
                {
                    choice = ChooseForRole(budget, openRoles[r]);
                    if (choice != null)
                    {
                        role = openRoles[r];
                        boughtThisReview[(int)openRoles[r]]++;
                    }
                }
            }

            // DECISION-013: once the operations service owns this commander's ground force, the
            // order book is the ONLY reason to buy a ground vehicle. The capture override, the recon
            // override and the plan-based counter triangle below are what an undiscovered commander
            // runs on — they are also what kept buying vehicles nobody had requested, filling the
            // pool the platoon loop then turned into platoons nobody needed.
            if (choice == null && !CommanderOperationsService.OwnsGroundForce(hq))
            {
                if (purchase == 0 && needsCaptureUnit)
                {
                    choice = ChooseCaptureUnit(budget) ?? Choose(budget, buyPlan);
                }
                else if (state.WantsReconUnit && purchase == purchases - 1)
                {
                    choice = ChooseReconUnit(budget) ?? Choose(budget, buyPlan);
                }
                else
                {
                    choice = Choose(budget, buyPlan);
                }
            }

            if (choice == null)
            {
                return grant - budget;
            }

            float cost = Mathf.Max(0f, choice.value);
            // Where the vehicle is wanted decides where it appears (reach-and-points Section 4,
            // user decision 2026-09-14: "for objectives further than this, MUST set up a FOB with a
            // vehicle depot closer to the objective, and spawn from there"). Adding supply and
            // letting the game's own deployment loop place it put every vehicle at whichever depot
            // that loop reached first, which on a big map was routinely an airfield an hour's drive
            // from the point the order book had asked for. A commander with no order book line for
            // this role — an undiscovered one buying off its plan — aims at its own territory centre.
            GlobalPosition objective = role != null
                && CommanderOperationsService.TryGetOldestRequisitionObjective(
                    hq, role.Value, out GlobalPosition lineObjective)
                ? lineObjective
                : CommanderCaptureService.GetTerritoryCenter(hq);
            string where;
            bool haveDepot = CommanderEconomyService.TryNearestOwnedDepot(hq, objective, out VehicleDepot depot, out _);
            if (haveDepot && depot.TrySpawnVehicle(choice))
            {
                // The player's own build queue spawns exactly this way and settles payment after
                // (Depot/CommanderSpawnService.cs, then CommitAcquisition), so a direct spawn with no
                // supply banked is a plain spawn and the charge below is the whole price.
                where = $" at {CommanderEconomyService.NearestHeldBaseLabel(hq, depot.transform.GlobalPosition())}";
            }
            else if (haveDepot)
            {
                // The nearest depot exists but refused the spawn (its pad is occupied): the vehicle
                // is banked as supply AND reserved for that depot, so the game's deployment loop
                // places it there when the pad frees rather than at the first depot it reaches
                // (fix, 2026-09-15: 1,046 of 1,534 buys in one match went to "whichever depot",
                // most of them an airfield an hour's drive from the objective).
                hq.ModifyUnitSupply(choice, 1);
                CommanderFactionVehicleService.Instance?.ReserveDeployment(hq, choice, depot);
                where = $" (queued at {CommanderEconomyService.NearestHeldBaseLabel(hq, depot.transform.GlobalPosition())}; its pad is busy)";
            }
            else
            {
                // No working depot at all: bank the supply and let the game's deployment loop place
                // the vehicle whenever a depot appears, which is the old behaviour.
                hq.ModifyUnitSupply(choice, 1);
                where = " (supply; no depot could spawn it)";
            }

            hq.AddFunds(-cost);
            budget -= cost;
            boughtCount++;
            RecordPurchase(hq);
            CommanderAiLog.Note(
                hq,
                $"bought {CommanderGameAccess.GetVehicleLabel(choice)} for {cost:0}{where}.",
                role == null ? GetPlanLabel(buyPlan) : $"{GetPlanLabel(buyPlan)}, for {GetRoleLabel(role.Value)}");
        }

        return grant - budget;
    }
    /// <summary>
    /// What the commander does with what it already owns, as opposed to what it buys: park the radar
    /// screen on the approaches, and give every idle airframe a mission. Both run every review and
    /// on every mission the commander is switched on for, because both are about units that are
    /// already paid for.
    /// </summary>
    private void ReviewPosture(FactionHQ hq, FactionHQ opponent, CommanderState state, in ForceRead opponentForce)
    {
        ReviewRecon(hq, opponent, state);
        // The duel gate is gone (user answer 2026-09-13): the wing's posture runs on every commanded
        // HQ, stock missions included. A stock mission's own authored aircraft are never touched —
        // the posture only reaches airframes in the commander's own set, and those exist only once
        // the buy leg below buys them.
        TaskAirWing(hq);
    }

    /// <summary>
    /// Scales an opposing commander's opening balance by the difficulty slider, once, straight
    /// after whichever opener the mission uses has set it — the duel head start, the matched
    /// economy, or neither, in which case it scales the balance the mission itself authored. This
    /// is the starting-funds half of the one slider; the income half is
    /// <see cref="CommanderEconomyService.IncomeHandicap"/>, and both sit on the same rule
    /// (<see cref="CommanderEconomyService.StartingFundsFor"/>), so the player's own faction is
    /// never touched by either. Silent at the default of 1, where it changes nothing.
    /// </summary>
    private static void ApplyDifficultyToOpeningFunds(FactionHQ hq)
    {
        float opened = CommanderEconomyService.StartingFunds(hq, hq.factionFunds);
        if (Mathf.Approximately(opened, hq.factionFunds))
        {
            return;
        }

        hq.SetFunds(opened);
        CommanderAiLog.Note(
            hq,
            $"difficulty x{CommanderSettings.EnemyIncomeMultiplier:0.0#}: opens on {opened:0} funds.");
    }

    /// <summary>
    /// The duel's head start, applied once per enemy HQ: a slightly bigger opening balance, and
    /// nothing else. Deliberately small — the enemy still earns at the player's rates from here on,
    /// this only stops the first ten minutes being a walkover.
    /// <para>
    /// <c>AIAircraftLimit</c> is pinned to zero on purpose. That field is the Basegame's free-air-
    /// force tap: <c>FactionHQ.DeployAIAircraft</c> launches a random airframe out of the mission's
    /// authored stock every few seconds until the cap is met, which is where the aircraft nobody
    /// bought came from — and, on a map whose only airbases are highway strips, where the fighters
    /// that immediately went looking for somewhere to land came from too. Every aircraft in the
    /// duel is now one a commander paid for and launched: <see cref="BuyAirframe"/> for the enemy,
    /// the AIR window for the player.
    /// </para>
    /// </summary>
    private static void PrepareDuel(FactionHQ hq)
    {
        hq.SetFunds(hq.factionFunds * DuelHeadStart);
        hq.AIAircraftLimit = 0;
        hq.reserveAirframes = 0;
        CommanderAiLog.Note(
            hq,
            $"takes the duel head start: {hq.factionFunds:0} funds. "
                + "Automatic AI aircraft are off; every airframe is bought and launched.");
    }

    /// <summary>
    /// Hands the enemy standing intel on every building the player owns. Buildings do not move, so
    /// one registration each is enough and nothing has to be refreshed. This is what turns the
    /// enemy from a faction that owns units into one that attacks: the Basegame ground AI drives at
    /// the nearest tracked enemy when it has no closer objective, and <c>CombatAI</c> only ever
    /// picks targets out of <c>trackingDatabase</c>, so an empty database is an enemy that flies
    /// over your base without seeing it. The player's mobile units are left unrevealed on purpose —
    /// the enemy knows where your base is, not where your army is.
    /// </summary>
    private static void RevealPlayerBase(FactionHQ hq, FactionHQ opponent)
    {
        if (opponent.factionUnits == null)
        {
            return;
        }

        foreach (PersistentID id in opponent.factionUnits)
        {
            if (id.TryGetUnit(out Unit unit)
                && unit != null
                && !unit.disabled
                && unit is Building
                && hq.GetTrackingData(id) == null)
            {
                hq.RpcUpdateTrackingInfo(id);
            }
        }
    }

    /// <summary>
    /// Puts the enemy on the player's economy: same opening balance and the same kill/tax rates,
    /// once, the first time this commander is reviewed. The balance comes from
    /// <c>excessFundsThreshold</c> (the player faction's authored starting balance) rather than
    /// its live funds, so turning the commander on mid-mission still mirrors the opening position
    /// instead of whatever the player happens to be holding.
    /// </summary>
    private static void LevelEconomy(FactionHQ hq, FactionHQ localHq)
    {
        float baseline = localHq.excessFundsThreshold > 0f
            ? localHq.excessFundsThreshold
            : localHq.factionFunds;
        hq.SetFunds(baseline);
        hq.excessFundsThreshold = localHq.excessFundsThreshold;
        hq.killReward = localHq.killReward;
        hq.playerTaxRate = localHq.playerTaxRate;
        CommanderAiLog.Note(hq, $"matched to the player economy at {baseline:0}.");
    }

    private void UpdatePlan(FactionHQ hq, CommanderState state, in ForceRead opponentForce)
    {
        EnemyPlan wanted = ChoosePlan(opponentForce);
        if (wanted == state.Plan)
        {
            state.Pending = wanted;
            state.PendingReviews = 0;
            return;
        }

        if (wanted == state.Pending)
        {
            state.PendingReviews++;
        }
        else
        {
            state.Pending = wanted;
            state.PendingReviews = 1;
        }

        if (state.PendingReviews < PlanCommitReviews)
        {
            return;
        }

        state.Plan = wanted;
        state.PendingReviews = 0;
        CommanderAiLog.Note(hq, $"switches plan to {GetPlanLabel(wanted)}.");
    }

    /// <summary>
    /// The counter triangle. Each plan beats the force that provokes it and is soft against the
    /// one that provokes the next, so shifting your own composition flips theirs — that swap is
    /// the game.
    /// </summary>
    private static EnemyPlan ChoosePlan(in ForceRead force)
    {
        // Leaning on air power: build the umbrella.
        if (force.Aircraft >= 2 && force.Aircraft * 2 >= force.Ground)
        {
            return EnemyPlan.AirDefence;
        }

        // Massed armour: guns break a column faster than trading tank for tank.
        if (force.Armour >= 3 && force.Armour * 2 >= force.Ground)
        {
            return EnemyPlan.FireSupport;
        }

        // A static line of guns and launchers: run armour through it before it can range.
        int staticLine = force.Guns + force.AirDefence;
        if (staticLine >= 3 && staticLine * 2 >= force.Ground)
        {
            return EnemyPlan.Spearhead;
        }

        // Nothing dominant to counter: cheap mass, take ground.
        return EnemyPlan.ReconScreen;
    }

    private static bool MatchesPlan(EnemyPlan plan, VehicleDefinition definition)
    {
        return plan switch
        {
            EnemyPlan.AirDefence => definition.vehicleType
                is VehicleType.R_SAM or VehicleType.IR_SAM or VehicleType.AAA,
            EnemyPlan.FireSupport => definition.vehicleType is VehicleType.ART or VehicleType.AFV,
            EnemyPlan.Spearhead => definition.vehicleType is VehicleType.MBT or VehicleType.AFV,
            _ => definition.vehicleType is VehicleType.LCV or VehicleType.AFV,
        };
    }

    private VehicleDefinition? Choose(float budget, EnemyPlan plan)
    {
        candidates.Clear();
        for (int i = 0; i < catalog.Count; i++)
        {
            VehicleDefinition definition = catalog[i];
            if (definition.value <= budget && IsCombatVehicle(definition) && MatchesPlan(plan, definition))
            {
                candidates.Add(definition);
            }
        }

        if (candidates.Count == 0)
        {
            for (int i = 0; i < catalog.Count; i++)
            {
                if (catalog[i].value <= budget && IsCombatVehicle(catalog[i]))
                {
                    candidates.Add(catalog[i]);
                }
            }
        }

        return candidates.Count == 0 ? null : candidates[Random.Range(0, candidates.Count)];
    }

    /// <summary>
    /// The cheapest vehicle in the catalogue that can actually move an airbase's capture bar.
    /// Cheapest on purpose: a capture unit's job is to sit in the ring, not to win the fight for it.
    /// </summary>
    private VehicleDefinition? ChooseCaptureUnit(float budget)
    {
        return CheapestCaptureVehicle(catalog, budget);
    }

    /// <summary>
    /// The capture-unit rule itself, over any catalogue. Extracted from
    /// <see cref="ChooseCaptureUnit"/> behaviour-neutrally (Reuse rule 5) when the strategic load's
    /// garrison placement became its second caller: a point restored with nothing standing on it is
    /// lost on the next hold tick, and what it needs is exactly this — the cheapest thing that can
    /// move a capture bar, at the price the commander itself pays for one. Forking the rule would
    /// have let a garrison and a bought capture unit disagree about what can hold ground.
    /// </summary>
    internal static VehicleDefinition? CheapestCaptureVehicle(IReadOnlyList<VehicleDefinition> catalog, float budget)
    {
        VehicleDefinition? best = null;
        for (int i = 0; i < catalog.Count; i++)
        {
            VehicleDefinition definition = catalog[i];
            if (CaptureVehicleQualifies(
                    definition.vehicleType, definition.captureStrength, definition.value, budget)
                && (best == null || definition.value < best.value))
            {
                best = definition;
            }
        }

        return best;
    }

    /// <summary>
    /// Whether one catalogue entry may be bought to hold ground, pure. Three conditions, and the
    /// third is the one this rule was missing until 2026-09-18: the entry must move a capture bar,
    /// it must be within budget, and it must be a vehicle the commander would buy for a FIGHT.
    /// <para>
    /// The incident: a munitions truck carries capture strength and costs 2, so it was the cheapest
    /// entry in both rosters and this rule chose it every time. Every caller was affected. The
    /// strategic load stood one unarmed ammo truck on each of thirty-one restored control points,
    /// and the airbase capture buy (<see cref="ChooseCaptureUnit"/>) had been sending the same truck
    /// to take airbases. "Cheapest thing that can sit in the ring" was always meant to read
    /// "cheapest thing that can sit in the ring and shoot back"; every other purchase rule in this
    /// service already applies <see cref="IsCombatVehicleType"/> and this one did not.
    /// </para>
    /// <para>
    /// The combat whitelist does not narrow the field in practice: a capture-capable vehicle is an
    /// APC or IFV (<c>AFV</c>) or a light carrier (<c>LCV</c>), and both are on it. What it removes
    /// is exactly the logistics types — <c>TRUCK</c>, <c>UGV</c>, <c>RDR</c>.
    /// </para>
    /// </summary>
    internal static bool CaptureVehicleQualifies(
        VehicleType type, float captureStrength, float value, float budget)
    {
        return captureStrength > 0f
            && value > 0f
            && value <= budget
            && IsCombatVehicleType(type);
    }

    /// <summary>
    /// The cheapest catalogue entry that fills <paramref name="role"/> — an order line wants a body
    /// in a slot, not the best vehicle the faction can field, which is why this is not
    /// <see cref="Choose"/> with a role filter bolted on. Within <see cref="CommanderPlatoonRole.Carrier"/>
    /// prefers a capture-capable entry (the same test <see cref="ChooseCaptureUnit"/> uses); within
    /// <see cref="CommanderPlatoonRole.Truck"/> the munitions-truck test is already what
    /// <see cref="CommanderPlatoonRoles.Of"/> matched the role on, so every truck candidate already
    /// qualifies.
    /// </summary>
    private VehicleDefinition? ChooseForRole(float budget, CommanderPlatoonRole role)
    {
        VehicleDefinition? cheapest = null;
        VehicleDefinition? cheapestPreferred = null;
        for (int i = 0; i < catalog.Count; i++)
        {
            VehicleDefinition definition = catalog[i];
            if (definition.value > budget || CommanderPlatoonRoles.Of(definition) != role)
            {
                continue;
            }

            // The `continue` above already guarantees definition.value <= budget for anything that
            // reaches here, so the clause the reviewer flagged is not restated (provably
            // behaviour-neutral: removing a condition already implied by an earlier `continue`).
            if (cheapest == null || definition.value < cheapest.value)
            {
                cheapest = definition;
            }

            bool preferred = role == CommanderPlatoonRole.Carrier
                ? definition.captureStrength > 0f
                // Always true once Of(definition) has already matched Truck — restated here so the
                // preference the design-facts table asks for ("preferring … IsMunitionsTruckDefinition
                // within Truck") is visible at this call site too, not only inside Of.
                : role == CommanderPlatoonRole.Truck && CommanderGameAccess.IsMunitionsTruckDefinition(definition);
            if (preferred && (cheapestPreferred == null || definition.value < cheapestPreferred.value))
            {
                cheapestPreferred = definition;
            }
        }

        return cheapestPreferred ?? cheapest;
    }

    private static string GetRoleLabel(CommanderPlatoonRole role)
    {
        return role switch
        {
            CommanderPlatoonRole.Armour => "ARMOUR",
            CommanderPlatoonRole.Carrier => "CARRIER",
            CommanderPlatoonRole.AirDefence => "AIR DEFENCE",
            CommanderPlatoonRole.Truck => "TRUCK",
            _ => "OTHER",
        };
    }

    /// <summary>
    /// What this commander may buy on the ground. The inline convoy walk that used to live here was
    /// replaced by the shared collector on 2026-09-16 (Reuse rule 4: the player's depot window, the
    /// factory production choice, the repair-vehicle lookup and this buyer now all ask
    /// <see cref="CommanderFactionRoster.MayFieldVehicle"/> exactly once, through one function, so
    /// the player and the computer can never be offered different kit).
    /// </summary>
    private void CollectCatalog(FactionHQ hq)
    {
        CommanderGameAccess.CollectFactionVehicleDefinitions(catalog, hq);
        if (!loggedGroundRoster.Add(hq))
        {
            return;
        }

        for (int i = 0; i < catalog.Count; i++)
        {
            VehicleDefinition definition = catalog[i];
            // Once per HQ, like LogAirRosterOnce: which vehicles exist, what the game types them as
            // and which platoon role that maps to — the only way to tell from the log why a recipe
            // slot stays empty (no LCV in this faction's list).
            CommanderPlugin.Log.LogInfo(
                $"Ground roster ({hq.faction.name}): {CommanderGameAccess.GetVehicleLabel(definition)} "
                    + $"[{definition.vehicleType}] role {GetRoleLabel(CommanderPlatoonRoles.Of(definition))}, "
                    + $"capture {definition.captureStrength:0.##}, value {definition.value:0}");
        }

        LogRosterCoverage(hq);
    }

    /// <summary>
    /// One verdict line per faction per mission: what this side can actually do once the roster
    /// predicate has been applied, read from the LIVE definitions rather than from the split's
    /// tables. The tables' own properties are checked at plugin load
    /// (<see cref="CommanderFactionRoster.SelfCheck"/>); this is the half that cannot be — whether
    /// the game's convoy groups and the aircraft cargo manifests still carry what the axes need.
    /// A missing axis is logged as a FAILED line, because a side that cannot capture, cannot clear
    /// an air-defence belt or cannot lift cargo has something in the mod broken for it.
    /// </summary>
    private void LogRosterCoverage(FactionHQ hq)
    {
        // The ground half is walked live, because the convoy groups belong to the game. The air half
        // is the mod's own table, so its axes are read from the split rather than from the sky.
        CommanderRosterAxis covered = CommanderFactionRoster.DescribeLiveCoverage(catalog)
            | (CommanderFactionRoster.ModDecidedAxesFor(CommanderFactionRoster.SideOf(hq))
                & (CommanderRosterAxis.SuppressRadar | CommanderRosterAxis.GroundAttack
                    | CommanderRosterAxis.CargoLift | CommanderRosterAxis.Scout));

        string missing = CommanderFactionRoster.DescribeMissingAxes(covered, CommanderFactionRoster.RequiredAxes);
        if (missing.Length == 0)
        {
            CommanderPlugin.Log.LogInfo(
                $"Ground roster ({hq.faction.name}): {catalog.Count} buyable types cover every capability axis.");
            return;
        }

        CommanderPlugin.Log.LogError(
            $"Faction roster self-check FAILED: {hq.faction.name} covers no {missing} "
                + $"({catalog.Count} buyable types).");
    }

    /// <summary>Drops state for HQs that went away with a scene the reset did not catch, and for the
    /// local HQ while nothing is commanding it — its state is only meaningful while the player
    /// commander switch is on, and leaving it behind would keep a stale plan on the HUD.</summary>
    private void PruneStates(FactionHQ localHq)
    {
        bool dropLocal = !CommanderPlayerCommanderService.IsCommanded(localHq, localHq);
        staleHqs.Clear();
        foreach (KeyValuePair<FactionHQ, CommanderState> entry in states)
        {
            if (entry.Key == null || (dropLocal && ReferenceEquals(entry.Key, localHq)))
            {
                staleHqs.Add(entry.Key!);
            }
        }
        for (int i = 0; i < staleHqs.Count; i++)
        {
            states.Remove(staleHqs[i]);
        }
    }

    private static string FundsLabel(float funds)
    {
        return UnitConverter.ValueReading(funds) ?? funds.ToString("F1");
    }

    /// <summary>Internal (one-word widening): the operations air step's loss cooldown counts the
    /// same air-defence vehicles with the same test — one definition, two callers.</summary>
    internal static bool IsAirDefence(VehicleDefinition definition)
    {
        return definition.vehicleType is VehicleType.AAA or VehicleType.IR_SAM or VehicleType.R_SAM;
    }

    private static bool IsCombatVehicle(VehicleDefinition definition)
    {
        return definition.value > 0f && IsCombatVehicleType(definition.vehicleType);
    }

    /// <summary>
    /// The vehicle types the commander spends money on to fight with. Split out of
    /// <see cref="IsCombatVehicle"/> behaviour-neutrally on 2026-09-18 (Reuse rule 5) when
    /// <see cref="CaptureVehicleQualifies"/> became its second caller: the price test belongs to the
    /// caller, the type list is the shared rule. The types NOT here are the logistics ones —
    /// <c>TRUCK</c>, <c>UGV</c>, <c>RDR</c> — which is why an ammo truck is no longer a capture unit.
    /// </summary>
    private static bool IsCombatVehicleType(VehicleType type)
    {
        return type is VehicleType.AAA
            or VehicleType.IR_SAM
            or VehicleType.R_SAM
            or VehicleType.MBT
            or VehicleType.AFV
            or VehicleType.ART
            or VehicleType.LCV;
    }

    private static int CountAirDefence(FactionHQ hq)
    {
        int count = 0;
        if (hq.factionUnits == null)
        {
            return 0;
        }

        foreach (PersistentID id in hq.factionUnits)
        {
            if (id.TryGetUnit(out Unit unit)
                && unit != null
                && !unit.disabled
                && unit.definition is VehicleDefinition definition
                && IsAirDefence(definition))
            {
                count++;
            }
        }
        return count;
    }

    /// <summary>What the opponent is actually fielding, which is the only input to the plan choice.</summary>
    private static ForceRead ReadForce(FactionHQ hq)
    {
        ForceRead read = default;
        if (hq.factionUnits == null)
        {
            return read;
        }

        foreach (PersistentID id in hq.factionUnits)
        {
            if (!id.TryGetUnit(out Unit unit) || unit == null || unit.disabled)
            {
                continue;
            }

            if (unit is Aircraft)
            {
                read.Aircraft++;
                continue;
            }

            if (unit.definition is not VehicleDefinition definition)
            {
                continue;
            }

            switch (definition.vehicleType)
            {
                case VehicleType.MBT:
                case VehicleType.AFV:
                    read.Armour++;
                    read.Ground++;
                    break;
                case VehicleType.ART:
                    read.Guns++;
                    read.Ground++;
                    break;
                case VehicleType.AAA:
                case VehicleType.IR_SAM:
                case VehicleType.R_SAM:
                    read.AirDefence++;
                    read.Ground++;
                    break;
                case VehicleType.LCV:
                    read.Ground++;
                    break;
            }
        }

        return read;
    }

    internal enum EnemyPlan
    {
        ReconScreen,
        Spearhead,
        FireSupport,
        AirDefence,
    }

    internal struct ForceRead
    {
        internal int Aircraft;
        internal int Armour;
        internal int Guns;
        internal int AirDefence;
        internal int Ground;
    }

    private sealed class CommanderState
    {
        internal EnemyPlan Plan;
        internal EnemyPlan Pending;
        internal int PendingReviews;
        internal bool Prepared;

        /// <summary>Funds set aside for the next hull. A hull is worth several ground vehicles, so a
        /// per-review slice has to accumulate or it never buys anything. The air fund that used to
        /// live beside it is retired with the ladder (2026-09-14): airframes are bought directly out
        /// of the rung's grant. This one survives as rung 2's internal ship accumulator — see
        /// <see cref="CommanderEnemyCommanderGround.ReviewNaval"/>.</summary>
        internal float NavalFund;

        /// <summary>
        /// Rung 2's air savings account (fix, 2026-09-14): a share of each review's grant set aside
        /// until it reaches the price of an airframe the wing actually wants. A strike jet costs
        /// several reviews' worth of a slice that the home CAP's replacements have already been
        /// through, so without accumulation the wing reported the same "short of the cheapest
        /// airframe" denial every review and bought nothing but the cheap fighters.
        /// </summary>
        internal float AirFund;

        /// <summary>The ceiling <see cref="AirFund"/> was last held to — the price of the dearest
        /// airframe an open demand wanted that review. Reported beside the savings on the ladder
        /// line so a wing that is saving and a wing that is hoarding can be told apart from the log
        /// alone.</summary>
        internal float AirFundCap;

        /// <summary>Whether rung 2 set a third of its allocation aside for the wing this review
        /// because something already in contact was short of an airframe (user decision
        /// 2026-09-14). Reported on the ladder line, so a review in which the ground bought little
        /// because the front was calling for air reads differently from one in which it simply had
        /// no money.</summary>
        internal bool Rung2AirReserved;

        /// <summary>
        /// Savings toward a radar airframe, kept apart from <see cref="AirFund"/> so the fighters
        /// and the strike airframes cannot spend it (user decision 2026-09-14). Banked at
        /// <see cref="AwacsSliceShare"/> of the air side's allocation while
        /// <c>CommanderOperationsService.WantsAwacsPurchase</c> is true, capped at the cheapest
        /// radar airframe the commander's strips accept, and released back to the wing the moment
        /// one is owned.
        /// </summary>
        internal float AwacsSavings;

        /// <summary>The price the AWACS savings are aiming at — the cheapest launchable radar
        /// airframe, or zero when none is wanted or none can launch. Reported beside the savings on
        /// the ladder line.</summary>
        internal float AwacsSavingsTarget;

        /// <summary>
        /// What rung 3 has banked toward its next picket insertion (departure 2026-09-14). A flight
        /// is a transport hull plus two vehicles and the rung's per-review share is a fraction of
        /// that, so the allocation accumulates here instead of being recomputed from scratch every
        /// review — the same shape as <see cref="AwacsSavings"/> and the building rung's
        /// <c>StructureSavings</c>. Capped at <see cref="PicketSavingsTarget"/>; spent down by the
        /// flights the operations review charges.
        /// </summary>
        internal float PicketSavings;

        /// <summary>The price the picket savings are aiming at — one whole insertion flight, or the
        /// cheapest road picket pair when no transport can launch. Reported beside the savings on
        /// the ladder line and handed to the operations side as the request's money gate.</summary>
        internal float PicketSavingsTarget;

        /// <summary>Whether the last air buy of this review was the radar airframe, so the charge
        /// comes out of <see cref="AwacsSavings"/> before <see cref="AirFund"/>.</summary>
        internal bool LastAirBuyWasAwacs;

        /// <summary>Consecutive buy reviews that bought nothing; drives the "holds" log line.</summary>
        internal int SkippedBuyReviews;

        /// <summary>Whether the last review found ground buying held to an empty order book (see
        /// GroundBuyingBookOnly), so the hold/resume log line fires once per transition rather than
        /// every review.</summary>
        internal bool GroundBookOnly;

        /// <summary>Whether the last review found ground buying held to a full idle pool (see
        /// <c>DeploymentHeldForFullPool</c>), so the hold/resume log line fires once per transition
        /// rather than every review — the same cadence <see cref="GroundBookOnly"/> uses.</summary>
        internal bool GroundPoolFull;

        /// <summary>Whether the last review found this faction at or above the live ground-vehicle
        /// ceiling (design.md, <c>unit-economy_20260918</c> §2.3), so the stop/resume line fires once
        /// per transition — the same cadence <see cref="GroundPoolFull"/> uses, and for the same
        /// reason.</summary>
        internal bool GroundCeilingReached;

        /// <summary>Whether the last review handed the ground's unspent share of rung 2 to the wing
        /// (see <c>GroundShareGoesToWing</c>), so the log line fires once per transition rather than
        /// every review — the same cadence <see cref="GroundBookOnly"/> uses.</summary>
        internal bool GroundShareToWing;

        /// <summary>Short of a radar vehicle for the overwatch screen.</summary>
        internal bool WantsReconUnit;

        /// <summary>Short of vehicles for the home guard, so the buy loop should get one.</summary>
        internal bool WantsDefenceUnit;

        /// <summary>The home guard: each pinned vehicle and the ring post it holds.</summary>
        internal readonly Dictionary<Unit, int> Defenders = new();

        /// <summary>Ring stations around every base this commander holds, and how many bases that
        /// was when they were picked.</summary>
        internal readonly List<GlobalPosition> DefencePosts = new();
        internal int DefenceBaseCount = -1;

        /// <summary>Defence posture: it holds until this time, and whether it is on right now.</summary>
        internal float ThreatUntil;
        internal bool Defending;

        /// <summary>Standing overwatch posts, picked once and then kept.</summary>
        internal readonly List<GlobalPosition> ReconPosts = new();

        /// <summary>The opponent's opening airbase. See GetStrikeTarget. Per commander rather than
        /// one field for the whole service: with the local HQ commanded too, two commanders remember
        /// two different opening bases, and the shared field would be rewritten every review.</summary>
        internal Airbase? StrikeBase;

        /// <summary>Last reason this commander bought no aircraft, so a repeated refusal logs on the
        /// <c>HoldReportEveryReviews</c> cadence instead of vanishing for the rest of the match (a
        /// silent air force is indistinguishable from a broken one, and "once per reason" proved to
        /// mean once per MATCH for a reason that never changes).</summary>
        internal string LastAirDenial = string.Empty;

        /// <summary>Consecutive reviews this commander has been refused an aircraft for the reason in
        /// <see cref="LastAirDenial"/> — the re-log cadence's counter.</summary>
        internal int AirDenialReviews;

        /// <summary>Which side of the wing the air buy currently running is serving — "CAP" for the
        /// fighters and the radar airframe, "CAS" for suppression and ground attack, empty when
        /// nothing asked. The denial line names it, so a reader can tell which half of the wing the
        /// commander is stuck on (fix, 2026-09-14).</summary>
        internal string AirBuySide = string.Empty;

        /// <summary>What the PREVIOUS air buy of this same review did, in words, or empty for the
        /// first buy of a review. With the two sides alternating, that is normally the other side,
        /// which is exactly what a reader needs to judge a "saves for one" refusal: whether the
        /// other half of the wing got its aircraft or was stuck too.</summary>
        internal string AirLastBuyOutcome = string.Empty;

        /// <summary>The same thing, copied in for the duration of ONE buy so the denial line can
        /// quote it without the reporter having to be handed it through six call sites.</summary>
        internal string AirDenialContext = string.Empty;

        /// <summary>Per airframe, the AIR window's AIR SUPERIORITY score of the best air-to-air
        /// loadout its own picker can build for it (0 = no A/A-capable option at all), memoized for
        /// the mission (user decision 2026-09-14: CAP and escort candidates are chosen by what the
        /// loadout can do, not by the airframe's role identity).</summary>
        internal readonly Dictionary<AircraftDefinition, float> CapScores = new();

        /// <summary>Per airframe, the same read on the CAS scorer (0 = no ground-attack option),
        /// memoized for the mission.</summary>
        internal readonly Dictionary<AircraftDefinition, float> CasScores = new();

        /// <summary>Per airframe, the same read on the ARAD scorer (0 = no anti-radiation option),
        /// memoized for the mission (design.md, smarter-air-wing_20260914 Section 5).</summary>
        internal readonly Dictionary<AircraftDefinition, float> AradScores = new();

        /// <summary>Per airframe, whether the commander's own loadout builder can give it the game's
        /// radar pod — the AWACS candidate test (Section 4). Memoized for the mission: it builds a
        /// whole loadout to answer.</summary>
        internal readonly Dictionary<AircraftDefinition, bool> AwacsCapable = new();

        // The rotary CAS fallback's once-per-objective set lived here. It is gone (fix,
        // 2026-09-14): the line now prints every time the fallback happens, with the distance to
        // the nearest pad, because how OFTEN a rotary sortie is refused is the thing the reader is
        // being asked to judge. See CommanderEnemyCommanderAir.BuyAirframe.

        /// <summary>Role-and-tier pairs whose "the tier above cannot launch from these strips" note
        /// has already been printed, so the line appears once per commander per fallback rather than
        /// once per 30 s review (design.md, airframe-selection_20260914 Section 4; the
        /// <c>ReportAirDenial</c> convention). Cleared with the rest of the state at
        /// session reset, so capturing an airbase mid-match re-announces the tier it unlocked.</summary>
        internal readonly HashSet<string> TierFallbackLogged = new();
    }
}
