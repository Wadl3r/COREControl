using System.Collections.Generic;
using UnityEngine;

namespace GroundControlRts;

/// <summary>
/// Forward operating bases (design.md, fob-construction_20260914; user decision 2026-09-14,
/// DECISION-014): a commander that holds a control point well clear of every airfield orders three
/// deliveries onto it — transport helicopters carrying one construction vehicle each, or three
/// munitions trucks by road — and when the third load is on the ground the mod creates a real
/// runtime airbase there and puts a vehicle depot, a radar and two helipads inside its build ring.
/// From that moment the FOB is an ordinary airbase: it is in <c>hq.GetAirbases()</c>, so the depot
/// rally, the forward-base planning, insertion origins, helicopter range and the ladder all see it
/// with no code of their own.
/// </summary>
/// <remarks>
/// This partial owns the demand and delivery side only. The construction itself —
/// <c>SavedAirbase</c>, <c>SpawnCustomAirbase</c> and the recipe's buildings — is
/// <see cref="CommanderEconomyService"/>'s, in <c>Economy/CommanderFobBuilder.cs</c>, because the
/// building spawn and the helipad-to-base link already live there and must not be forked (Reuse
/// rule 3).
/// <para>
/// The air variant is the picket insertion chain, unchanged: <c>TryLaunchInsertionAircraft</c> was
/// already generalised by HQ, already binds a flight to a point's hold posts, already threat-gates
/// the route and already recovers the helicopter and refunds its hull. A FOB flight is the third
/// programmatic caller after the SAM foundation drop and the picket insertion.
/// </para>
/// ponytail: nothing here survives a hot reload. A reload leaves the runtime airbase standing and
/// working but forgets the order that built it, so the teardown watch stops watching it. Recorded
/// as a follow-up in the design's "Out of scope" rather than fixed here.
/// </remarks>
internal sealed partial class CommanderOperationsService
{
    /// <summary>
    /// The capture ring a FOB is created with, in metres: 400, against the game's own 1000 m
    /// default for a mission-authored base (<c>SavedAirbase.CaptureRange</c>). A FOB is three
    /// buildings on a control point, not an airfield, and a 1 km ring would swallow the control
    /// point's own ring and most of the ground around it — so the base a raiding platoon has to
    /// stand in to take it is the size of the base itself.
    /// </summary>
    internal const float FobCaptureRangeMeters = 400f;

    /// <summary>
    /// How far apart two NEIGHBOURING buildings of a FOB stand, in metres: 120. Far enough that one
    /// bomb does not take two of them and that a vehicle rolling off the depot ramp does not drive
    /// into a pad, close enough that the whole ring sits well inside the 400 m capture ring and
    /// inside the base's own build radius. It is the spacing, not the ring, that is chosen: the ring
    /// radius follows from it and from how many buildings the recipe holds
    /// (<c>CommanderEconomyService.FobRingRadiusMeters</c>), so the second helipad widened the ring
    /// from about 69 m to about 85 m rather than crowding the buildings together.
    /// </summary>
    internal const float FobBuildingSpacingMeters = 120f;

    /// <summary>
    /// Loads one FOB takes: ONE (user decision 2026-09-15: "lower the requirement to just one
    /// tarantula drop-off or one truck reaching it"; it was three, one per building, and in a whole
    /// night of 121 orders no order ever got its third load in). It is a delivery count, not a cargo
    /// count: the flight carries what the shared insertion chooser loads, and a truck is one vehicle.
    /// </summary>
    internal const int FobDeliveries = 1;

    /// <summary>
    /// Minutes a point that lost its FOB order will not be offered another: 10, the same figure as
    /// the insertion loss cooldown's default and for the same reason — an identical loss on retry a
    /// minute later is a waste, and an hour is cowardice.
    /// </summary>
    internal const float FobPointCooldownMinutes = 10f;

    /// <summary>
    /// How long a FOB order may go without a load arriving before it is abandoned: 15 minutes,
    /// thirty reviews, measured from the order or from its LAST delivery. A commander runs ONE
    /// order at a time, so an order that can never finish — a convoy that cannot path to the site,
    /// a hangar that never frees a transport — would otherwise hold the slot for the rest of the
    /// match. It used to run from the order alone (overnight log 2026-09-15): the loads fly one at
    /// a time and a round trip is five to six minutes, so the player's orders were cancelled with
    /// two of three loads on the ground and the third in the air, fourteen times in one night.
    /// </summary>
    internal const float FobOrderTimeoutSeconds = 900f;

    /// <summary>
    /// Loads a FOB order may lose before it is given up: three, one per building. A lost load is
    /// replaced once anything has landed (overnight log 2026-09-15: "1 of 3 deliveries were lost
    /// and nothing replaces them" ended orders that had two loads on the ground). A first loss with
    /// nothing landed still cancels — that is evidence about the route, not bad luck.
    /// </summary>
    internal const int FobMaxLostLoads = 3;

    /// <summary>
    /// How much closer to the enemy a new site must be than the commander's rearmost FOB before that
    /// FOB is torn down to pay for it: 20 km (user instruction 2026-09-14, "we should also be able to
    /// abandon a FOB and then build one closer to the front-line if needed"). Half the far map's base
    /// separation — less than that and the new base covers ground the old one already reaches, so
    /// demolishing a working depot, radar and pad buys nothing.
    /// </summary>
    internal const float FobAdvanceMeters = 20000f;

    /// <summary>
    /// How long a FOB must have had nothing hostile tracked within <see cref="ObservedRadiusMeters"/>
    /// before it counts as quiet enough to abandon: 5 minutes, ten reviews. A base that is being
    /// fought over is a base worth holding, and the five minutes is long enough that a single contact
    /// passing through does not protect it for the rest of the match.
    /// </summary>
    internal const float FobQuietMinutes = 5f;

    /// <summary>
    /// The least time between two deliberate abandonments: 10 minutes. Without it a commander whose
    /// front is moving fast would tear a base down every review and never finish building the next
    /// one — three deliveries take longer than one review.
    /// </summary>
    internal const float FobAbandonIntervalMinutes = 10f;

    /// <summary>
    /// The abandonment rule, pure (user instruction 2026-09-14). A FOB is torn down to make room for
    /// one nearer the fighting only when every condition holds at once: this review found NOWHERE to
    /// build, so there is no room to simply build another (until 2026-09-14 this read "the commander
    /// is at its cap"; the cap was retired with reach-and-points and the FOB spacing is what
    /// refuses a site now); the new site is at least
    /// <paramref name="advanceMeters"/> closer to the enemy than the rearmost FOB, so the move is
    /// worth the price of demolishing a working base; that rear base is quiet and idle; and the last
    /// abandonment is far enough behind. Exactly <paramref name="advanceMeters"/> of advance is
    /// enough — the threshold is "at least this much closer".
    /// </summary>
    internal static bool FobShouldAbandon(
        bool noSiteAvailable,
        float candidateDistanceToEnemyMeters,
        float rearFobDistanceToEnemyMeters,
        float advanceMeters,
        bool quiet,
        bool inUse,
        bool abandonLimiterLive)
    {
        return noSiteAvailable
            && rearFobDistanceToEnemyMeters - candidateDistanceToEnemyMeters >= advanceMeters
            && quiet
            && !inUse
            && !abandonLimiterLive;
    }

    /// <summary>The stale-order rule, pure: an order that is still delivering after
    /// <paramref name="timeoutSeconds"/> is abandoned. Exactly on the timeout is not yet stale — the
    /// patient side of the boundary, the convention the insertion stall clock uses.</summary>
    internal static bool FobOrderHasStalled(float secondsOpen, float timeoutSeconds)
    {
        return timeoutSeconds > 0f && secondsOpen > timeoutSeconds;
    }

    /// <summary>
    /// How far off the road network a FOB site has to be before the deliveries fly instead of
    /// driving: the picket insertion's own off-road gate, read rather than retyped so a retune moves
    /// both together. Below it the convoy is a short cross-country hop; past it the crawl costs the
    /// order more than the flights do.
    /// </summary>
    private static float FobOffRoadMeters => CommanderSettings.OperationsHeliInsertionOffRoadMeters;

    /// <summary>
    /// What a lift order is delivering for (design.md, air-mobile-platoons_20260915 Section 2). The
    /// FOB order was the mod's first "deliver N loads of vehicles by air to a point"; the air-mobile
    /// platoon is the second, so the order was widened rather than copied (Reuse rule 5) and every
    /// FOB rule — the per-load clock, the lost-load replacement, the first-loss cancel, the
    /// abandoned-on-deck re-send, the landing-zone scout, the airdrop fallback, the heavy hull — is
    /// kept and keyed by this.
    /// </summary>
    internal enum CommanderLiftPurpose
    {
        /// <summary>The loads are construction vehicles and the last one builds a forward operating
        /// base on the point.</summary>
        FobConstruction,

        /// <summary>The loads are the light vehicles of an air-mobile platoon, adopted into the
        /// point's mission as they land.</summary>
        Platoon,
    }

    /// <summary>Where one FOB order has got to.</summary>
    internal enum CommanderFobPhase
    {
        /// <summary>Ordered; the deliveries are on their way.</summary>
        Delivering,

        /// <summary>The third load is in, and the base and its buildings are going up.</summary>
        Building,

        /// <summary>The base exists and is in <c>hq.GetAirbases()</c>.</summary>
        Online,

        /// <summary>Announced for demolition: the front has moved past it and the next review pulls
        /// it down so a FOB can be built nearer the fighting.</summary>
        Abandoning,
    }

    /// <summary>
    /// One commander's open LIFT order: where it is going, how it is getting there, how much of it
    /// has arrived, and — for a FOB, once it is up — the base and buildings the teardown watch
    /// measures. The class and file keep the FOB name because every rule in them was written for the
    /// FOB order and is unchanged; what is new is <see cref="Purpose"/>, which says whether the loads
    /// build a base or form a platoon (design.md, air-mobile-platoons_20260915 Section 2).
    /// </summary>
    internal sealed class CommanderFobOrder
    {
        /// <summary>The control point the FOB is being built on. Its own control-point identity is
        /// untouched; the base is a second asset standing on it.</summary>
        internal CommanderStrategicPoint Point = null!;

        /// <summary>True when the deliveries fly, false when they drive.</summary>
        internal bool ByAir;

        /// <summary>What this lift is for. A FOB order left at the default behaves exactly as it did
        /// before the purpose existed.</summary>
        internal CommanderLiftPurpose Purpose = CommanderLiftPurpose.FobConstruction;

        /// <summary>Loads this order wants: <see cref="FobDeliveries"/> for a FOB,
        /// <c>CommanderSettings.LiftLoadsPerPlatoon</c> for a platoon. Read everywhere the FOB
        /// lifecycle used to read the constant, so one order's count can never disagree with
        /// another's.</summary>
        internal int LoadsWanted = FobDeliveries;

        /// <summary>Vehicles one load carries: one for a FOB (a construction vehicle or a truck),
        /// <see cref="LiftVehiclesPerLoad"/> for a platoon.</summary>
        internal int VehiclesPerLoad = 1;

        /// <summary>The mission whose platoon this lift is raising, or null for a FOB order. The
        /// vehicles it puts down are adopted into this mission's platoon.</summary>
        internal CommanderOperationsMission? Mission;

        /// <summary>
        /// Scaled <c>Time.time</c> from which this order has had no safe way to fly, or negative
        /// while it has one. The logistics watch stamps it, the dispatch refuses to launch while it
        /// stands, and <c>CommanderSettings.LiftHopelessMinutes</c> measured from it is what finally
        /// gives the order up (user decision 2026-09-16).
        /// </summary>
        internal float HopelessSince = -1f;

        /// <summary>The KIND of trouble last written for this order's hopeless state ("route",
        /// "landing" or "air"), so the watch's decision is logged when it changes rather than every
        /// five seconds — the full reason carries a distance that moves every tick. Empty when the
        /// order has a safe route.</summary>
        internal string HopelessReported = string.Empty;

        /// <summary>Loads this order has actually put in the air. What separates a lift that can
        /// still be called off from one whose transport is already carrying its cargo: the second
        /// costs the commander the load whether it is recalled or not, so it is flown out
        /// (fix, 2026-09-15).</summary>
        internal int LoadsLaunched;

        /// <summary>Vehicles this order has actually put on the ground — the platoon lift's own
        /// count, since a load of two lands as two separate deliveries.
        /// <see cref="Delivered"/> is derived from it through <see cref="LoadsLanded"/>.</summary>
        internal int VehiclesLanded;

        /// <summary>Scaled <c>Time.time</c> a dispatch first found the lift cover not yet up, or
        /// negative while nothing is being held. The bounded wait
        /// (<c>CommanderSettings.PackageFormUpSeconds</c>) runs from here and is cleared the moment a
        /// load launches, so every load gets its own wait rather than one wait for the order.</summary>
        internal float LiftHoldSince = -1f;

        /// <summary>True once the "holds at the form-up point" line has been written for the load
        /// currently waiting, so the hold says itself once per load rather than once per review.</summary>
        internal bool LiftHoldLogged;

        internal CommanderFobPhase Phase = CommanderFobPhase.Delivering;

        /// <summary>Loads on the ground inside the point's ring so far, out of
        /// <see cref="LoadsWanted"/>.</summary>
        internal int Delivered;

        /// <summary>Deliveries this order has given up on — a flight shot down, a truck killed on
        /// the road. The order is cancelled when no delivery can still arrive.</summary>
        internal int Lost;

        /// <summary>The cargo vehicles and trucks standing on the site waiting to be consumed by
        /// construction. Emptied when the buildings go up.</summary>
        internal readonly List<Unit> Arrivals = new();

        /// <summary>The trucks driving to the site, road variant only. A truck is moved into
        /// <see cref="Arrivals"/> as it reaches the ring.</summary>
        internal readonly List<Unit> Convoy = new();

        /// <summary>The landing or parking post each delivery is bound to — one per delivery, so
        /// three loads do not pile onto one spot.</summary>
        internal readonly List<GlobalPosition> Posts = new();

        /// <summary>The base once it exists, and the three buildings the teardown watch measures.
        /// Null and empty while the order is still delivering.</summary>
        internal Airbase? Base;
        internal readonly List<Unit> Buildings = new();

        /// <summary>Scaled <c>Time.time</c> the order was opened, for the log.</summary>
        internal float OrderedAt;

        /// <summary>Scaled <c>Time.time</c> of the order or its latest delivery, whichever is later:
        /// the stale valve's clock. A load on the ground is progress; a load in the air is not.</summary>
        internal float LastProgressAt;

        /// <summary>Construction transports whose crew ejected on the deck before flying. Neither
        /// delivered nor lost; the load is simply sent again, from another base.</summary>
        internal int LaunchFailures;

        /// <summary>True once the "a vehicle spawned at this FOB's depot" line has been written, so
        /// the proof line is logged once per base rather than once per vehicle.</summary>
        internal bool DepotUseLogged;

        /// <summary>The same, for the first aircraft to launch from the FOB.</summary>
        internal bool LaunchLogged;

        /// <summary>Scaled <c>Time.time</c> a hostile was last tracked within
        /// <see cref="ObservedRadiusMeters"/> of this FOB. The abandonment rule's quiet clock;
        /// negative while nothing has ever been near it.</summary>
        internal float LastContactAt = -1f;

        /// <summary>Scaled <c>Time.time</c> this FOB last had one of its faction's units standing on
        /// it — a vehicle out of its depot or an aircraft on its pad. A base in use is never
        /// abandoned out from under whatever is using it.</summary>
        internal float LastUseAt = -1f;
    }

    /// <summary>One FOB flight in the air: which order it serves and which post it is bound to. A
    /// deliberately separate list from <c>state.Insertions</c> — a picket insertion carries a picket
    /// mission and is swept against it, and a FOB flight has no mission — but it is counted against
    /// the same airborne limit (<see cref="CountInsertionsInFlight"/>).</summary>
    internal sealed class CommanderFobFlight
    {
        internal CommanderFobOrder Order = null!;
        internal Aircraft? Aircraft;

        /// <summary>This flight's own identity on the supply side (lift-wave_20260916), quoted back
        /// to it whenever ONE load of a wave is recalled, converted to an airdrop, bound to a
        /// registering transport or credited with a delivered vehicle. Zero would mean "every flight
        /// standing on this point", which is exactly what a wave must never say.</summary>
        internal int SlotId;

        /// <summary>Which load of the order this flight is carrying, counting from one — the number
        /// the air marker prints. It is stamped at dispatch rather than derived from
        /// <c>Order.Delivered</c> at draw time, because three transports of one wave are all in the
        /// air while nothing has landed and would otherwise every one of them read <c>1/3</c>.</summary>
        internal int LoadOrdinal;

        /// <summary>True once a transport has been bound to this record. Unity reads a destroyed
        /// object as null, so "bound and now null" is a transport that is gone, not one that never
        /// registered.</summary>
        internal bool Bound;
        internal GlobalPosition Post;
        internal bool Delivered;

        /// <summary>Vehicles of this flight's own load that have activated on the ground. One for a
        /// construction flight; up to <see cref="LiftVehiclesPerLoad"/> for a platoon lift, whose
        /// load lands one vehicle at a time.</summary>
        internal int VehiclesLanded;

        internal float RequestedAt;

        /// <summary>Game time the transport first read as over its post, or 0 — the picket
        /// insertion's stall clock, for a construction flight.</summary>
        internal float NearLandingZoneSince;

        /// <summary>Whether this flight drops its load by parachute rather than landing.</summary>
        internal bool Airdrop;
    }

    /// <summary>
    /// The placement rule, pure (reach-and-points Section 3, user decision 2026-09-14). It replaces
    /// the old "15 km from any airfield on the map" rule, which refused every FOB of a whole 82 km
    /// match because no held point was ever that far from an airbase. A FOB now exists to extend the
    /// ground a commander's vehicles can drive to, so the two distances that matter are how far the
    /// site is from the depots it ALREADY owns — nearer than
    /// <paramref name="minDepotMeters"/> and the new depot sits on the doorstep of one it already
    /// has — and the standing spacing between two forward bases. Both minimums default to 5 km
    /// (user request 2026-09-15: at the original 10 km depot distance and 20 km spacing FOB
    /// placement was too restrictive and few candidate sites ever qualified); the site score, not
    /// these distances, is what says whether a site is worth building.
    /// <para>Both boundaries refuse: a site exactly on either minimum is too close. That is the
    /// insertion threat gate's convention rather than the road gate's, and for the same reason —
    /// being wrong here costs three deliveries and a building rung's savings.</para>
    /// </summary>
    internal static bool FobSiteAllowed(
        float nearestOwnedDepotMeters,
        float minDepotMeters,
        float nearestFobMeters,
        float minFobSpacingMeters)
    {
        return nearestOwnedDepotMeters > minDepotMeters && nearestFobMeters > minFobSpacingMeters;
    }

    /// <summary>
    /// Whether a point's kind may carry a forward base at all, pure (user instruction 2026-09-16:
    /// "fobs should not be built on resource sites"). A resource site
    /// (<see cref="StrategicPointKind.Site"/>) is the one kind of ground whose whole worth is the
    /// gold mine its holder may put on it (<c>CommanderStrategicPointService.SiteMinePermitted</c>),
    /// and a forward base standing there takes both the room and the ground the mine needs. Every
    /// other kind — village, hilltop, outpost, crossroads, roadside and a base — is fair ground.
    /// <para>The kind, not the label: the enum is what discovery actually decided, and a label like
    /// <c>"RESOURCE SITE 19"</c> is only how that decision is printed.</para>
    /// </summary>
    internal static bool FobKindAllowed(StrategicPointKind kind)
    {
        return kind != StrategicPointKind.Site;
    }

    /// <summary>
    /// A candidate site's score, pure (reach-and-points Section 3): how many of the commander's
    /// out-of-reach control points a depot standing on this candidate would bring inside
    /// <paramref name="reachMeters"/>. The caller measures the distances; this counts them, through
    /// the same <see cref="IsWithinDepotReach"/> the planners use, so the score and the gate can
    /// never disagree about where the boundary is. Zero means the site buys the commander nothing
    /// and is not worth three deliveries.
    /// </summary>
    internal static int CountBroughtInReach(IReadOnlyList<float> distancesToOutOfReachPoints, float reachMeters)
    {
        int count = 0;
        for (int i = 0; i < distancesToOutOfReachPoints.Count; i++)
        {
            if (IsWithinDepotReach(distancesToOutOfReachPoints[i], reachMeters))
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>
    /// Which of two candidate sites is the better one, pure (reach-and-points Section 3): the bigger
    /// reach gain wins outright, and two sites that open up the same amount of ground go to the one
    /// nearer the enemy — a FOB is for projecting force, so of two equally useful pieces of ground
    /// the forward one is worth more. A strict comparison on both, so the walk keeps the FIRST of
    /// two identical candidates and the ranking order breaks the tie.
    /// </summary>
    internal static bool FobSiteBeats(
        int gainA, float enemyDistanceAMeters, int gainB, float enemyDistanceBMeters)
    {
        return gainA > gainB || (gainA == gainB && enemyDistanceAMeters < enemyDistanceBMeters);
    }

    /// <summary>
    /// The money gate, pure (user instruction 2026-09-14): the order opens only when the building
    /// rung has ALREADY banked the whole price and the faction can actually pay it. Both halves
    /// matter — the bank is what stops a FOB being paid for out of the floors the ladder reserved for
    /// the platoon and picket rungs, and the balance is what stops it being charged on credit.
    /// Exactly the price is enough on both.
    /// </summary>
    internal static bool FobAffordable(float savings, float price, float funds)
    {
        return price > 0f && savings >= price && funds >= price;
    }

    /// <summary>
    /// Whether an order that has lost a delivery can still finish, pure. A loss with NOTHING landed
    /// yet cancels the order outright (fix, 2026-09-14: the enemy sent three construction flights one
    /// after another into the same guns — a first loss is evidence about the route, and the point's
    /// cooldown is the answer, not the next flight). Once a load has landed, every lost load is sent
    /// again (fix, 2026-09-15) until <paramref name="maxLost"/> have gone; a complete order is never
    /// cancelled.
    /// </summary>
    internal static bool FobOrderCanContinue(int delivered, int lost, int wanted, int maxLost)
    {
        if (delivered >= wanted)
        {
            return true;
        }

        if (lost > 0 && delivered == 0)
        {
            return false;
        }

        return lost < maxLost;
    }

    /// <summary>Whether the third load is in and construction should fire, pure.</summary>
    internal static bool FobReadyToBuild(int delivered, int wanted)
    {
        return delivered >= wanted;
    }

    /// <summary>
    /// Vehicles one platoon lift load carries: the insertion chain's own cargo maximum
    /// (<see cref="MaxInsertionCargoVehicles"/>, two), read rather than retyped so a retune moves
    /// both together. A load asking for three would be refused by the cargo chooser rather than by
    /// anything here, because no transport's mounts have ever been asked for more.
    /// </summary>
    internal static int LiftVehiclesPerLoad => MaxInsertionCargoVehicles;

    /// <summary>
    /// How many whole loads a count of landed vehicles amounts to, pure (design.md,
    /// air-mobile-platoons_20260915 Section 2). A platoon lift's progress is counted in VEHICLES —
    /// a load of two lands as two separate cargo activations, and a flight whose budget only ran to
    /// one vehicle is a short load, not a lost one — so the loads the order has actually delivered
    /// are what those vehicles divide into. A per-load count of one is the FOB order's own case and
    /// gives the count back unchanged.
    /// </summary>
    internal static int LoadsLanded(int vehiclesLanded, int perLoad)
    {
        int landed = Mathf.Max(0, vehiclesLanded);
        return perLoad > 1 ? landed / perLoad : landed;
    }

    /// <summary>
    /// Whether a lift order has delivered everything it was opened for, pure. A FOB's last load
    /// fires the construction (<see cref="FobReadyToBuild"/>, which this defers to so the two can
    /// never disagree); a platoon lift simply closes with its platoon on the ground.
    /// </summary>
    internal static bool LiftComplete(int delivered, int loadsWanted, CommanderLiftPurpose purpose)
    {
        _ = purpose;
        return FobReadyToBuild(delivered, loadsWanted);
    }

    /// <summary>
    /// What the review line, the markers and the log call one lift: <c>FOB HILLTOP 9</c> for a
    /// construction order, the platoon's own name for a platoon lift. Pure, for the self-check.
    /// </summary>
    internal static string LiftPurposeLabel(CommanderLiftPurpose purpose, string pointLabel, string platoonName)
    {
        return purpose == CommanderLiftPurpose.Platoon && platoonName.Length > 0
            ? platoonName
            : $"FOB {pointLabel}";
    }

    /// <summary>The live form of <see cref="LiftPurposeLabel"/> for one order. Internal because the
    /// air marker walk names a lift's transport with it too
    /// (<c>Operations/CommanderOperationsAirMarkers.cs</c>).</summary>
    internal static string LiftLabel(CommanderFobOrder order)
    {
        return LiftPurposeLabel(
            order.Purpose, order.Point.Label, order.Mission?.AirMobilePlatoonName ?? string.Empty);
    }

    /// <summary>
    /// The teardown rule, pure (design Section 2): a FOB's airbase identity is torn down only when
    /// ALL of its buildings are gone. Two out of three destroyed is a damaged base, not a dead one —
    /// the building rung's captured-base wish rebuilds what is missing.
    /// </summary>
    internal static bool FobShouldTearDown(int liveBuildings)
    {
        return liveBuildings <= 0;
    }

    /// <summary>
    /// The lift marker's text (design Section 5), pure for the self-check. The air and road phases
    /// read differently on purpose: a reader watching the loads converge wants to know whether they
    /// are flying or driving without reading the review line.
    /// <para>
    /// The NAME comes from <see cref="LiftPurposeLabel"/>, the one definition of what a lift is
    /// called, so the marker, the review line and the log can never disagree (fix, 2026-09-16: every
    /// branch here hard-coded the word FOB, so a platoon lift was drawn as
    /// <c>FOB CROSSROADS 1 — 0/3 delivered</c> and the player read it as a forward base whose
    /// delivery count had been changed — a forward base takes <see cref="FobDeliveries"/> load, a
    /// platoon takes <c>CommanderSettings.LiftLoadsPerPlatoon</c>).
    /// </para>
    /// <paramref name="holding"/> is the "no safe route" wait
    /// (<see cref="CommanderFobOrder.HopelessSince"/>): the order is alive, its loads have been
    /// recalled and nothing is moving, which must not read as a delivery in progress.
    /// </summary>
    internal static string FobMarkerText(
        CommanderLiftPurpose purpose,
        string pointLabel,
        string platoonName,
        CommanderFobPhase phase,
        int delivered,
        int wanted,
        bool byAir,
        bool holding)
    {
        string name = LiftPurposeLabel(purpose, pointLabel, platoonName);
        switch (phase)
        {
            case CommanderFobPhase.Building:
                return $"{name} — building";
            case CommanderFobPhase.Online:
                return $"{name} — online";
            case CommanderFobPhase.Abandoning:
                return $"{name} — abandoning";
            default:
                string progress = byAir
                    ? $"{delivered}/{wanted} delivered"
                    : $"convoy {delivered}/{wanted} arrived";
                return holding
                    ? $"{name} — {progress} · holding for a clear route"
                    : $"{name} — {progress}";
        }
    }

    /// <summary>The live form of <see cref="FobMarkerText"/> for one order, so the marker walk reads
    /// one call and no caller can assemble the parts differently.</summary>
    internal static string FobMarkerText(CommanderFobOrder order)
    {
        return FobMarkerText(
            order.Purpose,
            order.Point.Label,
            order.Mission?.AirMobilePlatoonName ?? string.Empty,
            order.Phase,
            order.Delivered,
            order.LoadsWanted,
            order.ByAir,
            order.HopelessSince >= 0f);
    }

    /// <summary>
    /// The text on ONE truck of a lift's road convoy, pure for the self-check. Named by the same
    /// <see cref="LiftPurposeLabel"/> as the site marker (fix, 2026-09-16: it read
    /// <c>FOB CONVOY {point}</c> whatever the lift was for), and carrying the load number that truck
    /// is bringing in so a reader can tell the third truck from the first.
    /// </summary>
    internal static string LiftConvoyMarkerText(
        CommanderLiftPurpose purpose, string pointLabel, string platoonName, int load, int wanted)
    {
        return $"{LiftPurposeLabel(purpose, pointLabel, platoonName)} CONVOY — {load}/{wanted}";
    }

    /// <summary>The live form of <see cref="LiftConvoyMarkerText"/> for one truck of one order.
    /// <paramref name="truckIndex"/> is the truck's place in <see cref="CommanderFobOrder.Convoy"/>,
    /// counted on after what has already arrived.</summary>
    internal static string LiftConvoyMarkerText(CommanderFobOrder order, int truckIndex)
    {
        return LiftConvoyMarkerText(
            order.Purpose,
            order.Point.Label,
            order.Mission?.AirMobilePlatoonName ?? string.Empty,
            order.Delivered + truckIndex + 1,
            order.LoadsWanted);
    }

    // ---- Demand (design Section 1) -------------------------------------------------------------

    /// <summary>
    /// The ONE point a FOB may be built on this review (user instruction 2026-09-14, tightened from
    /// "a front point or the held point nearest the front" to the front-nearest point alone): the
    /// point this commander holds with the smallest distance to the nearest enemy asset. Any front
    /// point would have offered a whole line of candidates and turned every capturable point into a
    /// FOB in turn; the front-nearest one is a single candidate that moves as the line moves.
    /// <paramref name="distanceToEnemyMeters"/> comes back with it, because the abandonment rule
    /// compares it against the rearmost FOB's own distance. Null when the commander holds nothing.
    /// </summary>
    private static CommanderStrategicPoint? FrontNearestHeldPoint(
        FactionHQ hq, OperationsState state, out float distanceToEnemyMeters)
    {
        CommanderStrategicPoint? best = null;
        distanceToEnemyMeters = float.MaxValue;
        for (int i = 0; i < state.RankedPoints.Count; i++)
        {
            CommanderRankedPoint ranked = state.RankedPoints[i];
            if (!ReferenceEquals(ranked.Point.GetOwner(), hq))
            {
                continue;
            }

            if (ranked.DistanceToEnemyMeters < distanceToEnemyMeters)
            {
                distanceToEnemyMeters = ranked.DistanceToEnemyMeters;
                best = ranked.Point;
            }
        }

        return best;
    }

    /// <summary>Scratch for the site score: how far a candidate stands from each point this
    /// commander cannot currently reach. Static because one review scores one commander at a
    /// time.</summary>
    private static readonly List<float> fobReachScratch = new();

    /// <summary>
    /// This review's FOB site, if the commander has one (reach-and-points Section 3). Walks every
    /// point the commander HOLDS and takes the one that would bring the most out-of-reach points
    /// inside <c>DepotReachMeters</c>, ties going to the site nearest the enemy. Until 2026-09-14
    /// this offered exactly one candidate, the held point nearest the front, and refused it unless
    /// it stood 15 km from every airfield on the map — which refused every FOB of a whole match.
    /// <para>An order opens only while something is actually out of reach: a commander that can
    /// already drive everywhere has nothing to gain from a forward base, and the three deliveries
    /// are better spent on pickets.</para>
    /// <paramref name="byAir"/> is decided here too: the deliveries fly when the point is off the
    /// road network or when the road route would take a convoy past something that kills it.
    /// </summary>
    /// <param name="reroute">An open construction order whose site has gone bad, when the picker is
    /// being asked for ANOTHER site for a load already in the air (user decision 2026-09-16). Its own
    /// order does not count as "already on its way", its current site is never handed back, and
    /// whatever is picked is reached by air because that is where the load is. Null for the
    /// ordinary review.</param>
    private bool TryPickFobSite(
        FactionHQ hq,
        OperationsState state,
        out CommanderStrategicPoint point,
        out bool byAir,
        out int broughtInReach,
        CommanderFobOrder? reroute = null)
    {
        point = null!;
        byAir = false;
        broughtInReach = 0;
        if (state.FobLossCooldownUntil > 0f && Time.time < state.FobLossCooldownUntil)
        {
            // A FOB lost or captured says the ground was wrong, not that one convoy was unlucky, so
            // the whole commander waits rather than only the one site.
            return ReportNoFobSite(hq, state, "a FOB was lost recently; the commander is waiting out the loss cooldown");
        }

        if (reroute == null && HasOpenFobOrder(state))
        {
            return ReportNoFobSite(hq, state, "an order is already on its way");
        }

        if (state.OutOfReach.Count == 0)
        {
            return ReportNoFobSite(hq, state, "every point is within reach");
        }

        float minDepotMeters = CommanderSettings.FobMinDepotDistanceMeters;
        float minSpacingMeters = CommanderSettings.FobMinSpacingMeters;
        float reachMeters = CommanderSettings.DepotReachMeters;
        CommanderStrategicPoint? best = null;
        int bestGain = 0;
        float bestEnemyDistance = float.MaxValue;
        bool bestMustFly = false;
        bool anyHeld = false;
        bool anyClearOfDepotsAndFobs = false;
        bool anyWithRoom = false;
        int resourceSitesRefused = 0;
        string lastNoRoom = string.Empty;
        CommanderEconomyService? economy = CommanderEconomyService.Instance;
        bool airPossible = CommanderSupplyHeliService.Instance?.HasLaunchableVehicleTransport(hq) == true;
        float flyRange = CommanderSettings.OperationsHeliInsertionRangeMeters;
        for (int i = 0; i < state.RankedPoints.Count; i++)
        {
            CommanderRankedPoint ranked = state.RankedPoints[i];
            CommanderStrategicPoint candidate = ranked.Point;
            // A held point, or — the expansion case (user, 2026-09-14: "MUST set up a FOB with a
            // vehicle depot closer to the objective") — a point nobody holds that is out of depot
            // reach, off the front, and within a transport's range of a held airbase. The second
            // kind can only be flown to, so it is skipped when no vehicle-carrying transport exists.
            // Without it a commander whose every held point sat inside the minimum depot distance of its own depots
            // could never order a FOB at all (the 2026-09-14 match, both sides).
            if (reroute != null && ReferenceEquals(candidate, reroute.Point))
            {
                continue;
            }

            // A resource site is never FOB ground (user instruction 2026-09-16). First in the walk
            // because it is the cheapest refusal and because it is about the ground itself, not
            // about who holds it or what stands near it — and because the re-route above comes
            // through this same walk, so a flight looking for another site is refused one too.
            if (!FobKindAllowed(candidate.Kind))
            {
                resourceSitesRefused++;
                continue;
            }

            bool held = ReferenceEquals(candidate.GetOwner(), hq);
            float airbaseMeters = TryFindInsertionLaunchBase(hq, candidate.Position, out GlobalPosition launchBase)
                ? CommanderGameAccess.HorizontalDistance(launchBase.AsVector3(), candidate.Position.AsVector3())
                : float.MaxValue;
            bool mustFly = !held && FobExpansionCandidate(
                candidate.GetOwner() == null,
                state.OutOfReach.Contains(candidate),
                InsertionStandoffClear(ranked.IsFront, ranked.DistanceToEnemyMeters, CommanderSettings.FobEnemyStandoffMeters),
                airPossible,
                airbaseMeters,
                flyRange);
            if (!held && !mustFly)
            {
                continue;
            }

            anyHeld = true;
            bool cooldownLive = state.FobCooldownUntil.TryGetValue(candidate, out float until)
                && Time.time < until;
            bool inContact = candidate.Hold.Contested
                || TryNearestTrackedHostile(hq, candidate.Position, ContactRangeMeters, out _, out _);
            if (cooldownLive || inContact)
            {
                continue;
            }

            float depotDistance =
                CommanderEconomyService.TryNearestOwnedDepot(hq, candidate.Position, out _, out float nearestDepot)
                    ? nearestDepot
                    : float.MaxValue;
            float fobDistance = CommanderEconomyService.NearestFobDistance(candidate.Position);
            if (!FobSiteAllowed(depotDistance, minDepotMeters, fobDistance, minSpacingMeters))
            {
                continue;
            }

            anyClearOfDepotsAndFobs = true;
            // The room check (fix, 2026-09-15): the recipe must fit on this ground by the build's own
            // site rule, or the order is never placed — see CommanderFobBuilder.CanSiteFob.
            if (economy != null && !economy.CanSiteFob(hq, candidate.Position, out string noRoom))
            {
                lastNoRoom = $"{candidate.Label}: {noRoom}";
                continue;
            }

            anyWithRoom = true;
            fobReachScratch.Clear();
            foreach (CommanderStrategicPoint stranded in state.OutOfReach)
            {
                fobReachScratch.Add(CommanderGameAccess.HorizontalDistance(
                    candidate.Position.AsVector3(), stranded.Position.AsVector3()));
            }

            int gain = CountBroughtInReach(fobReachScratch, reachMeters);
            if (gain <= 0)
            {
                continue;
            }

            if (best == null || FobSiteBeats(gain, ranked.DistanceToEnemyMeters, bestGain, bestEnemyDistance))
            {
                best = candidate;
                bestGain = gain;
                bestEnemyDistance = ranked.DistanceToEnemyMeters;
                bestMustFly = mustFly;
            }
        }

        fobReachScratch.Clear();
        if (best == null)
        {
            // Each reason named in the order the walk rules a candidate out, so the first one that
            // stopped every candidate is the one reported.
            string why = !anyHeld
                ? "it holds no control point, and no out-of-reach point can be flown to"
                : !anyClearOfDepotsAndFobs
                    ? $"no held point is both more than {minDepotMeters / 1000f:0} km from one of its own depots "
                        + $"and more than {minSpacingMeters / 1000f:0} km from another FOB"
                    : !anyWithRoom
                        ? $"no candidate has room for the {CommanderEconomyService.FobRecipeDescription()} "
                            + $"(last refused, {lastNoRoom})"
                        : $"no held point would bring any of the {state.OutOfReach.Count} out-of-reach points "
                            + $"within {reachMeters / 1000f:0} km of a depot";
            // The refused resource sites are named alongside whichever reason stopped the rest, so a
            // review that ruled out ground the commander can see it holds says why (user instruction
            // 2026-09-16).
            if (resourceSitesRefused > 0)
            {
                why += $"; {resourceSitesRefused} resource site"
                    + $"{(resourceSitesRefused == 1 ? " was" : "s were")} refused as FOB ground";
            }

            return ReportNoFobSite(hq, state, why);
        }

        state.FobSiteReason = string.Empty;

        float roadDistance = CommanderStrategicPointService.Instance?.NearestRoadDistanceMeters(best.Position)
            ?? float.MaxValue;
        bool offRoad = roadDistance > FobOffRoadMeters;
        bool roadRouteThreatened = !offRoad
            && TryFindInsertionLaunchBase(hq, best.Position, out GlobalPosition origin)
            && TryFindInsertionRouteThreat(hq, origin, best.Position, out _);
        point = best;
        byAir = reroute != null || bestMustFly || FobGoesByAir(
            offRoad,
            roadRouteThreatened,
            airPossible,
            hq.factionFunds,
            CommanderEconomyService.FobStructuresCost(),
            FobAirFundsMultiple);
        broughtInReach = bestGain;
        return true;
    }

    /// <summary>
    /// Whether a point nobody holds may be the site of a flown-in FOB, pure: neutral, out of depot
    /// reach (a road FOB could never be supplied there), clear of the enemy by the same standoff an
    /// unescorted picket flight keeps (<paramref name="standoffClear"/> — the 2026-09-14 match lost
    /// all three construction flights to a site just past the front line), with a vehicle-carrying
    /// transport available and a held airbase within its range. The expansion case of the FOB rule.
    /// </summary>
    internal static bool FobExpansionCandidate(
        bool neutral, bool outOfReach, bool standoffClear, bool airPossible, float nearestHeldAirbaseMeters, float flyRangeMeters)
    {
        return neutral && outOfReach && standoffClear && airPossible && nearestHeldAirbaseMeters <= flyRangeMeters;
    }

    /// <summary>Logs why no FOB site was picked, once per change of reason, and returns false so the
    /// site picker can return through it. Silent for a reason already reported.</summary>
    private static bool ReportNoFobSite(FactionHQ hq, OperationsState state, string reason)
    {
        if (state.FobSiteReason != reason)
        {
            state.FobSiteReason = reason;
            CommanderAiLog.Note(hq, $"no FOB this review: {reason}.");
        }

        return false;
    }

    /// <summary>Orders on their way — a FOB that is about to exist, counted against the cap before it
    /// is finished rather than after.</summary>
    private static int CountOpenFobOrders(OperationsState state)
    {
        int count = 0;
        for (int i = 0; i < state.FobOrders.Count; i++)
        {
            if (state.FobOrders[i].Phase != CommanderFobPhase.Online
                && state.FobOrders[i].Purpose == CommanderLiftPurpose.FobConstruction)
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>True while this commander has a CONSTRUCTION order that has not finished delivering —
    /// the design's "one open FOB order per commander". An online FOB stays in the list for the
    /// teardown watch and does not block the next order, and a platoon lift is not a FOB order at all
    /// (air-mobile-platoons_20260915): the "one at a time" rule is about the deliveries a base costs,
    /// and a lift that raised a platoon on the other side of the map must not stop one being
    /// built.</summary>
    private static bool HasOpenFobOrder(OperationsState state)
    {
        for (int i = 0; i < state.FobOrders.Count; i++)
        {
            if (state.FobOrders[i].Phase != CommanderFobPhase.Online
                && state.FobOrders[i].Purpose == CommanderLiftPurpose.FobConstruction)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>True while any FOB order — open or online — stands on this point, so a picket
    /// insertion never races a FOB delivery for the same ring.</summary>
    internal static bool IsBoundToFob(FactionHQ hq, CommanderStrategicPoint point)
    {
        CommanderOperationsService? service = Instance;
        if (service == null || !service.states.TryGetValue(hq, out OperationsState state))
        {
            return false;
        }

        for (int i = 0; i < state.FobOrders.Count; i++)
        {
            if (ReferenceEquals(state.FobOrders[i].Point, point))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Rung 4's FOB demand read, for the priority ladder's "what to build next" walk
    /// (<c>CommanderEconomyService.GetEnemyBuildReserve</c>): true when this commander has a site it
    /// would order a FOB on right now. The SAME walk <see cref="TryOrderFob"/> makes, so the rung
    /// can never bank toward a FOB the spend would then skip (Reuse rule 4, one definition, two
    /// callers).
    /// </summary>
    internal static bool WantsFob(FactionHQ hq)
    {
        CommanderOperationsService? service = Instance;
        return CommanderSettings.FobEnabled
            && hq != null
            && hq.IsServer
            && service != null
            && service.states.TryGetValue(hq, out OperationsState state)
            && service.TryPickFobSite(hq, state, out _, out _, out _);
    }

    /// <summary>
    /// Opens the one FOB order (design Section 1), called from the building rung's spend after the
    /// rung has banked the price. Charges the three structures up front — feasibility §3, "charge at
    /// order time; no refund on loss" — which is what lets the FOB be one more entry in the existing
    /// wish list rather than a pot of its own. Returns false when nothing qualifies or the charge
    /// cannot be met, and the rung keeps saving.
    /// </summary>
    internal static bool TryOrderFob(FactionHQ hq)
    {
        CommanderOperationsService? service = Instance;
        if (!CommanderSettings.FobEnabled
            || hq == null
            || !hq.IsServer
            || service == null
            || !service.states.TryGetValue(hq, out OperationsState state)
            || !service.TryPickFobSite(hq, state, out CommanderStrategicPoint point, out bool byAir, out int broughtInReach))
        {
            return false;
        }

        // The money gate (user instruction 2026-09-14): the building rung must have banked the WHOLE
        // price already, so a FOB is never paid for out of the floors the ladder reserved for the
        // platoon and picket rungs.
        float cost = CommanderEconomyService.FobStructuresCost();
        if (!FobAffordable(CommanderEconomyService.StructureSavingsFor(hq), cost, hq.factionFunds))
        {
            return false;
        }

        hq.AddFunds(-cost);
        CommanderFobOrder order = new()
        {
            Point = point,
            ByAir = byAir,
            Phase = CommanderFobPhase.Delivering,
            OrderedAt = Time.time,
            LastProgressAt = Time.time,
        };
        // One post per delivery so three loads do not pile onto one spot; the same ring the picket
        // insertion lands on, which is the ring the garrison would hold.
        order.Posts.AddRange(service.EnsureHoldPosts(point, order.LoadsWanted));
        state.FobOrders.Add(order);
        state.FobDenials.Remove(point);
        CommanderAiLog.Note(
            hq,
            $"orders a FOB at {point.Label} by {(byAir ? "air" : "road")}: brings {broughtInReach} "
                + $"point{(broughtInReach == 1 ? string.Empty : "s")} within reach, "
                + $"{CommanderEconomyService.FobRecipeDescription()} for {cost:0}.");
        return true;
    }

    /// <summary>
    /// How many times the FOB's structure price the balance must hold for the order to fly rather
    /// than drive when it could do either (user, 2026-09-14: "I'd prefer Tarantula flight FOBs
    /// unless money is tight"): three. The flights charge three hull rentals up front and refund
    /// them on recovery, so a balance at three prices absorbs the float without touching the
    /// rungs' floors; below it the trucks go, which cost nothing beyond what the book already buys.
    /// </summary>
    internal const float FobAirFundsMultiple = 3f;

    /// <summary>
    /// Whether a FOB order flies, pure: never without a transport that can carry vehicles; always
    /// when the point is off the road network or the road route is threatened (the trucks could not
    /// make it); otherwise by preference, unless money is tight — the balance is under
    /// <paramref name="fundsMultiple"/> times the structure price.
    /// </summary>
    internal static bool FobGoesByAir(
        bool offRoad, bool routeThreatened, bool airPossible, float funds, float structurePrice, float fundsMultiple)
    {
        if (!airPossible)
        {
            return false;
        }

        if (offRoad || routeThreatened)
        {
            return true;
        }

        return structurePrice > 0f && funds >= structurePrice * fundsMultiple;
    }

    // ---- Platoon lifts (design.md, air-mobile-platoons_20260915 Section 2) ----------------------

    /// <summary>
    /// The money gate for a lift, pure: the balance must hold <paramref name="multiple"/> times the
    /// flight's price before one is launched. The FOB order's own gate in a different pocket
    /// (<see cref="FobAffordable"/> asks the building rung's bank as well, because a FOB is a
    /// structure the ladder saves for; a platoon lift is bought out of the balance like the vehicles
    /// it replaces), so the multiple is what stops a lift emptying the treasury: the flight charges
    /// its hull and its cargo up front and refunds the hull only when the transport is recovered.
    /// A multiple of one is the plain "can it be paid for" test. An unpriced flight — no transport
    /// can launch at all — is never affordable, exactly as an unpriced FOB is never ordered.
    /// </summary>
    internal static bool LiftAffordable(float funds, float price, float multiple)
    {
        return price > 0f && price < float.MaxValue && funds >= price * Mathf.Max(1f, multiple);
    }

    /// <summary>
    /// Loads a shortfall of vehicles takes, pure: however many whole loads it takes to carry them,
    /// rounding UP, since half a load still needs a transport. A shortfall of nothing wants no lift
    /// at all. What a top-up lift for a platoon that has lost vehicles is sized from (fix,
    /// 2026-09-15: an air-mobile platoon could never be reinforced — the pool top-up skips it, its
    /// carrier and air-defence lines are bought by air, and no second lift was ever opened).
    /// </summary>
    internal static int LiftLoadsForShortfall(int shortfall, int perLoad)
    {
        int wanted = Mathf.Max(0, shortfall);
        if (wanted == 0)
        {
            return 0;
        }

        int carried = Mathf.Max(1, perLoad);
        return (wanted + carried - 1) / carried;
    }

    /// <summary>
    /// Whether a cancelled lift stamps the FOB site cooldown, pure. Only a CONSTRUCTION order does
    /// (fix, 2026-09-15): that cooldown says "do not try to put a base on this ground again yet",
    /// and a platoon lift that was cancelled says nothing about the ground's suitability for a base.
    /// Stamping it there stopped the commander building a forward operating base on a point for ten
    /// minutes because a transport had been turned back from it. A platoon lift takes its own
    /// cooldown instead, which is what stops it being re-ordered next review.
    /// </summary>
    internal static bool LiftTakesFobCooldown(CommanderLiftPurpose purpose)
    {
        return purpose == CommanderLiftPurpose.FobConstruction;
    }

    /// <summary>
    /// Which of two candidate landing zones a lift prefers, pure (the forward landing zone, lead
    /// decision 2026-09-15): the nearer to the objective wins, because every metre is a metre the
    /// platoon has to drive under fire; between two at the same distance a point the commander
    /// already OWNS beats one nobody holds, since the transports set down among its own garrison
    /// rather than on open ground. A strict comparison on both, so a walk keeps the FIRST of two
    /// identical candidates — the <see cref="FobSiteBeats"/> convention.
    /// </summary>
    internal static bool LiftLandingBeats(bool ownA, float metersA, bool ownB, float metersB)
    {
        return metersA < metersB || (metersA == metersB && ownA && !ownB);
    }

    /// <summary>
    /// The index of the landing zone a lift should use, pure, or -1 when there is none: the best
    /// candidate by <see cref="LiftLandingBeats"/> that stands within <paramref name="maxMeters"/> of
    /// the objective. Exactly on the radius is inside it — the distance convention the rest of the
    /// mod uses. A -1 is what turns an air-mobile mission back into a driving one.
    /// </summary>
    internal static int LiftLandingIndex(
        IReadOnlyList<bool> own, IReadOnlyList<float> metersToObjective, float maxMeters)
    {
        int best = -1;
        for (int i = 0; i < own.Count && i < metersToObjective.Count; i++)
        {
            if (metersToObjective[i] > maxMeters)
            {
                continue;
            }

            if (best < 0
                || LiftLandingBeats(own[i], metersToObjective[i], own[best], metersToObjective[best]))
            {
                best = i;
            }
        }

        return best;
    }

    /// <summary>The roles an air-mobile platoon is still short of, best first — air defence before
    /// carriers, the insertion chooser's own doctrine, because a platoon on a point thirty
    /// kilometres out is threatened by aircraft first. Handed to the cargo chooser so successive
    /// loads fill the recipe instead of flying four of the same vehicle.</summary>
    private static readonly List<CommanderPlatoonRole> liftWantedRoles = new();

    private static IReadOnlyList<CommanderPlatoonRole> LiftWantedRoles(CommanderFobOrder order)
    {
        liftWantedRoles.Clear();
        int haveAirDefence = 0;
        int haveCarrier = 0;
        CommanderOperationsMission? mission = order.Mission;
        for (int i = 0; mission != null && i < mission.Assigned.Count; i++)
        {
            CommanderPlatoon platoon = mission.Assigned[i];
            for (int m = 0; m < platoon.Members.Count; m++)
            {
                switch (CommanderPlatoonRoles.Of(platoon.Members[m]?.definition as VehicleDefinition))
                {
                    case CommanderPlatoonRole.AirDefence:
                        haveAirDefence++;
                        break;
                    case CommanderPlatoonRole.Carrier:
                        haveCarrier++;
                        break;
                }
            }
        }

        for (int i = haveAirDefence; i < AirMobileRecipeAirDefence; i++)
        {
            liftWantedRoles.Add(CommanderPlatoonRole.AirDefence);
        }

        for (int i = haveCarrier; i < AirMobileRecipeCarrier; i++)
        {
            liftWantedRoles.Add(CommanderPlatoonRole.Carrier);
        }

        return liftWantedRoles;
    }

    /// <summary>
    /// Whether the mission a platoon lift is serving is still on the board and still wants a platoon
    /// by air: the same object, still a forward base, still marked air-mobile. Every one of those can
    /// change under an open lift — a point that stops being front is demoted to a picket by the same
    /// review's planning, and a mission can leave the board outright — and a load landing into a
    /// mission that no longer wants it would attach a platoon to a picket.
    /// </summary>
    private static bool PlatoonLiftStillWanted(OperationsState state, CommanderFobOrder order)
    {
        return MissionCanAdoptLift(state, order.Mission) && order.Mission!.AirMobile;
    }

    /// <summary>
    /// Whether a mission is still able to take the vehicles a lift puts down: it is on the board, it
    /// is a forward base and it still has a point. Deliberately WITHOUT the air-mobile mark that
    /// <see cref="PlatoonLiftStillWanted"/> adds (fix, 2026-09-15): a lift that has already launched
    /// flies its loads out whatever happened to the mark, and a base that has been handed back to an
    /// ordinary driving raise is still a base those vehicles belong to.
    /// </summary>
    private static bool MissionCanAdoptLift(OperationsState state, CommanderOperationsMission? mission)
    {
        if (mission == null || mission.Point == null || mission.Kind != CommanderMissionKind.ForwardBase)
        {
            return false;
        }

        for (int i = 0; i < state.Missions.Count; i++)
        {
            if (ReferenceEquals(state.Missions[i], mission))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Whether a lift is called off because its objective has stopped wanting a platoon, pure (fix,
    /// 2026-09-15). Only before the first load leaves the deck. Once a load is in the air the
    /// commander has already bought its hull and its cargo and the transport is carrying them: a
    /// recall throws that away and puts nothing on the ground, which is exactly what the live match
    /// did to every lift it ordered. A launched lift flies its loads out and the vehicles are adopted
    /// where they land, or join the pool if there is truly nothing left to join.
    /// </summary>
    internal static bool LiftCancelsForWant(int loadsLaunched, bool stillWanted)
    {
        return !stillWanted && loadsLaunched <= 0;
    }

    /// <summary>
    /// Opens the lift for every air-mobile forward base that has none (design.md,
    /// air-mobile-platoons_20260915 Section 2). One lift per point at a time — the supply side's
    /// one-cargo-mission-per-point invariant, the same one the FOB order keeps by sending its loads
    /// in succession — and one lift order per mission. Called at the top of the review's lift step so
    /// an order opened this review is dispatched by the same review's delivery loop.
    /// </summary>
    private void PlanPlatoonLifts(FactionHQ hq, OperationsState state)
    {
        if (!CommanderSettings.OperationsHeliInsertionEnabled || !hq.IsServer)
        {
            return;
        }

        for (int i = 0; i < state.Missions.Count; i++)
        {
            CommanderOperationsMission mission = state.Missions[i];
            // A mission that is not a forward base has no platoon to raise — a picket's pair is the
            // insertion chain's business, not a lift's. Asked through the same test the landing path
            // uses, so a lift is never ordered for a mission the landing could not deliver to.
            if (!mission.AirMobile || mission.LiftOrder != null || !MissionCanAdoptLift(state, mission))
            {
                continue;
            }

            // MissionCanAdoptLift has proved the point non-null; the local restates it for the
            // compiler's flow analysis, which cannot see through the helper.
            CommanderStrategicPoint objective = mission.Point!;

            // A first lift raises the platoon; a later one tops it up after losses (fix, 2026-09-15:
            // an air-mobile platoon could never be reinforced at all — the pool top-up skips it and
            // its carrier and air-defence lines are bought as cargo, so a platoon down to three
            // vehicles stayed at three for the rest of the match).
            int loads = LiftLoadsForShortfall(AirMobileShortfall(mission), LiftVehiclesPerLoad);
            bool topUp = mission.Assigned.Count > 0;
            if (loads <= 0)
            {
                continue;
            }

            if (!TryFindLiftLandingPoint(hq, state, mission, out CommanderStrategicPoint landing, out float shortMeters))
            {
                // Nowhere to put it down: the objective is enemy-held and there is no ground of ours
                // or nobody's near enough to land on. A long drive is worse than a flight and much
                // better than a platoon that never comes, so the base raises an ordinary one.
                mission.AirMobile = false;
                CommanderAiLog.Note(
                    hq,
                    $"raises {mission.AirMobilePlatoonName} by road: {objective.Label} is enemy-held and no "
                        + $"friendly ground within {CommanderSettings.LiftAirheadMaxMeters / 1000f:0} km to land on.");
                mission.AirMobilePlatoonName = string.Empty;
                continue;
            }

            if (IsBoundToFob(hq, landing) || IsBoundToInsertion(state, landing))
            {
                continue;
            }

            // Priced through the supply side's own estimate for a load of this size, so the gate and
            // the launch can never disagree about what a lift costs.
            float price = CommanderSupplyHeliService.Instance?.CheapestInsertionFlightValue(
                hq, LiftVehiclesPerLoad, requireAirdrop: false, landing.Position) ?? float.MaxValue;
            if (!LiftAffordable(hq.factionFunds, price, CommanderSettings.LiftFundsMultiple))
            {
                ReportFobDenial(
                    hq,
                    state,
                    landing,
                    LiftPurposeLabel(CommanderLiftPurpose.Platoon, landing.Label, mission.AirMobilePlatoonName),
                    price >= float.MaxValue
                        ? "no held base can launch a transport for the lift"
                        : $"the lift waits: {hq.factionFunds:0} in the bank, "
                            + $"{Mathf.Max(1f, CommanderSettings.LiftFundsMultiple):0}x the {price:0} a load is wanted");
                continue;
            }

            CommanderFobOrder order = new()
            {
                // The LANDING zone, which is the objective itself unless the enemy is standing on it.
                // Everything the delivery chain keys on — the supply side's per-point cargo mission,
                // the hold posts, the cover, the arrival notify — is about where the transports go.
                Point = landing,
                ByAir = true,
                Purpose = CommanderLiftPurpose.Platoon,
                LoadsWanted = loads,
                VehiclesPerLoad = LiftVehiclesPerLoad,
                Mission = mission,
                Phase = CommanderFobPhase.Delivering,
                OrderedAt = Time.time,
                LastProgressAt = Time.time,
            };
            // One post per vehicle the lift will land, so six vehicles do not pile onto one spot —
            // the ring a garrison would hold anyway.
            order.Posts.AddRange(EnsureHoldPosts(landing, order.LoadsWanted * order.VehiclesPerLoad));
            state.FobOrders.Add(order);
            state.FobDenials.Remove(landing);
            mission.LiftOrder = order;
            string where = ReferenceEquals(landing, mission.Point)
                ? $"onto {objective.Label}"
                : $"onto {landing.Label} (forward landing zone, {shortMeters / 1000f:0} km short of {objective.Label})";
            CommanderAiLog.Note(
                hq,
                topUp
                    ? $"orders a top-up lift for {LiftLabel(order)}: {order.LoadsWanted} "
                        + $"load{(order.LoadsWanted == 1 ? string.Empty : "s")} of {order.VehiclesPerLoad} {where}."
                    : $"orders a lift for {LiftLabel(order)} {where}: {order.LoadsWanted} loads of "
                        + $"{order.VehiclesPerLoad} at about {price:0} a load.");
        }
    }

    /// <summary>
    /// Vehicles an air-mobile mission's platoon is still short of: the whole recipe before the first
    /// load lands, and whatever a platoon that has taken losses is under its establishment by. Zero
    /// for a platoon at full strength, which is what stops a lift being re-ordered for ever.
    /// </summary>
    private static int AirMobileShortfall(CommanderOperationsMission mission)
    {
        if (mission.Assigned.Count == 0)
        {
            return AirMobileRecipeCarrier + AirMobileRecipeAirDefence;
        }

        int shortfall = 0;
        for (int i = 0; i < mission.Assigned.Count; i++)
        {
            CommanderPlatoon platoon = mission.Assigned[i];
            shortfall += Mathf.Max(0, platoon.Establishment - platoon.Members.Count);
        }

        return shortfall;
    }

    /// <summary>The live form of <see cref="LiftLandingStillClear"/> for one piece of ground: nothing
    /// hostile tracked within the lift's abort standoff of it.</summary>
    private static bool LiftLandingClearOfContacts(FactionHQ hq, GlobalPosition position)
    {
        float standoff = CommanderSettings.LiftAbortStandoffMeters;
        return !TryNearestTrackedHostile(
                hq,
                position,
                standoff,
                CommanderSettings.StandoffContactMemorySeconds,
                out _,
                out float meters,
                out _)
            || LiftLandingStillClear(meters, standoff);
    }

    /// <summary>True while a lift that was turned back from this landing zone is still waiting out
    /// its own cooldown — a table of its own, so a failed flight never stops a forward operating base
    /// being built on the same ground.</summary>
    private static bool LiftCooldownLive(OperationsState state, CommanderStrategicPoint point)
    {
        return state.LiftCooldownUntil.TryGetValue(point, out float until) && Time.time < until;
    }

    /// <summary>Scratch for the landing-zone walk: each candidate point's ownership and its distance
    /// to the objective. Static because one commander's lift is sited at a time, the FOB site
    /// scratch convention.</summary>
    private static readonly List<CommanderStrategicPoint> liftLandingCandidates = new();
    private static readonly List<bool> liftLandingOwn = new();
    private static readonly List<float> liftLandingMeters = new();

    /// <summary>
    /// Where this lift puts its platoon down (the forward landing zone, lead decision 2026-09-15).
    /// The objective itself whenever the commander can land on it; otherwise — the enemy is standing
    /// on the objective, and no transport lands into that — the nearest point the commander owns or
    /// nobody holds within <c>CommanderSettings.LiftAirheadMaxMeters</c> of it, chosen by the pure
    /// <see cref="LiftLandingIndex"/>. False when the objective is enemy-held and there is no such
    /// ground, which is what sends the platoon by road instead.
    /// <para>The platoon adopts its vehicles at the landing zone and is given the OBJECTIVE as its
    /// own objective, so the ordinary mission flow drives the last leg — nothing here moves it.</para>
    /// </summary>
    private static bool TryFindLiftLandingPoint(
        FactionHQ hq,
        OperationsState state,
        CommanderOperationsMission mission,
        out CommanderStrategicPoint landing,
        out float shortMeters)
    {
        landing = mission.Point!;
        shortMeters = 0f;
        FactionHQ? owner = landing.GetOwner();
        if ((owner == null || ReferenceEquals(owner, hq)) && !LiftCooldownLive(state, landing))
        {
            return true;
        }

        liftLandingCandidates.Clear();
        liftLandingOwn.Clear();
        liftLandingMeters.Clear();
        for (int i = 0; i < state.RankedPoints.Count; i++)
        {
            CommanderStrategicPoint candidate = state.RankedPoints[i].Point;
            if (ReferenceEquals(candidate, mission.Point))
            {
                continue;
            }

            FactionHQ? candidateOwner = candidate.GetOwner();
            if ((candidateOwner != null && !ReferenceEquals(candidateOwner, hq))
                // A landing zone a lift was just turned back from is skipped rather than picked
                // again, so the search moves to the next-best ground instead of stalling on the one
                // that failed.
                || LiftCooldownLive(state, candidate)
                // And ground with a column standing on it is not a landing zone at all (user
                // decision 2026-09-15) — the same standoff that moves an open lift off a zone keeps
                // one from being chosen in the first place, so the two can never disagree.
                || !LiftLandingClearOfContacts(hq, candidate.Position))
            {
                continue;
            }

            liftLandingCandidates.Add(candidate);
            liftLandingOwn.Add(candidateOwner != null);
            liftLandingMeters.Add(CommanderGameAccess.HorizontalDistance(
                candidate.Position.AsVector3(), mission.Point!.Position.AsVector3()));
        }

        int best = LiftLandingIndex(liftLandingOwn, liftLandingMeters, CommanderSettings.LiftAirheadMaxMeters);
        if (best >= 0)
        {
            landing = liftLandingCandidates[best];
            shortMeters = liftLandingMeters[best];
        }

        liftLandingCandidates.Clear();
        liftLandingOwn.Clear();
        liftLandingMeters.Clear();
        return best >= 0;
    }

    // ---- Delivery (design Section 2) ------------------------------------------------------------

    /// <summary>
    /// The review's FOB step: sweep the flights and the convoy, dispatch what is still owed, build
    /// when the third load is in, and tear down a base whose buildings have all gone. Called from
    /// <c>Review</c> after <c>PlanInsertions</c>, so a FOB flight and a picket flight are counted
    /// against the airborne limit in one consistent order.
    /// </summary>
    private void ReviewFobs(FactionHQ hq, OperationsState state)
    {
        PruneFobFlights(hq, state);
        PlanPlatoonLifts(hq, state);
        for (int i = state.FobOrders.Count - 1; i >= 0; i--)
        {
            CommanderFobOrder order = state.FobOrders[i];
            switch (order.Phase)
            {
                case CommanderFobPhase.Delivering:
                    // A flown-in expansion FOB is ordered on a point nobody holds yet, so only the
                    // ENEMY taking the point cancels the order.
                    if (order.Point.GetOwner() != null && !ReferenceEquals(order.Point.GetOwner(), hq))
                    {
                        // A platoon lift keeps its air-mobile mark here (fix, 2026-09-15): what was
                        // taken is the LANDING zone, not the objective, and the landing zone's own
                        // cooldown sends the next review looking for the next-best ground rather than
                        // starting the hour-long drive the lift exists to avoid.
                        CancelFobOrder(
                            hq,
                            state,
                            order,
                            order.Purpose == CommanderLiftPurpose.Platoon
                                ? "the enemy holds the landing zone"
                                : "the enemy holds the point",
                            keepAirMobile: order.Purpose == CommanderLiftPurpose.Platoon);
                        continue;
                    }

                    // The mission went away under the lift: demoted to a picket by this review's
                    // planning (its point stopped being front, or it lost the forward-base share),
                    // or dropped from the board outright. There is no platoon left for the loads to
                    // join, so the lift stops rather than landing vehicles nobody owns.
                    if (order.Purpose == CommanderLiftPurpose.Platoon
                        && LiftCancelsForWant(order.LoadsLaunched, PlatoonLiftStillWanted(state, order)))
                    {
                        CancelFobOrder(hq, state, order, "its objective no longer wants a platoon");
                        continue;
                    }

                    // The standoff the LAUNCH asked is re-asked by the logistics watch, on its own
                    // five-second clock, not here (user decision 2026-09-16): enemy presence changes
                    // far faster than a review, and one rule has one home. See
                    // <c>Operations/CommanderOperationsLogistics.cs</c>.

                    // The loss rule is asked BEFORE the dispatch (fix, 2026-09-15): the overnight
                    // log shows `lost a construction flight`, `construction flight 2/3 away` and
                    // `cancelled` in one review — a transport bought and sent for an order that the
                    // same review then closed.
                    if (!FobOrderCanContinue(order.Delivered, order.Lost, order.LoadsWanted, FobMaxLostLoads))
                    {
                        CancelFobOrder(
                            hq, state, order,
                            order.Delivered == 0
                                ? "the first delivery was lost before anything landed"
                                : $"{order.Lost} deliveries were lost, the most an order may replace");
                        continue;
                    }

                    if (order.ByAir)
                    {
                        DispatchFobAirFlights(hq, state, order);
                    }
                    else
                    {
                        DispatchFobConvoy(hq, state, order);
                    }

                    if (LiftComplete(order.Delivered, order.LoadsWanted, order.Purpose))
                    {
                        if (order.Purpose == CommanderLiftPurpose.Platoon)
                        {
                            CompletePlatoonLift(hq, state, order);
                        }
                        else
                        {
                            BuildFob(hq, state, order);
                        }
                    }
                    else if (FobOrderHasStalled(Time.time - order.LastProgressAt, FobOrderTimeoutSeconds))
                    {
                        CancelFobOrder(
                            hq,
                            state,
                            order,
                            $"no load arrived in {FobOrderTimeoutSeconds / 60f:0} min ({order.Delivered}/{order.LoadsWanted} landed, "
                                + $"{order.Lost} lost, {order.LaunchFailures} failed on the deck)");
                    }

                    break;

                case CommanderFobPhase.Building:
                    // Construction is synchronous inside BuildFob; a phase left here means the
                    // build failed and the order is finished with either way.
                    CancelFobOrder(hq, state, order, "the base could not be built on that ground");
                    break;

                case CommanderFobPhase.Online:
                    WatchFobTeardown(hq, state, order);
                    break;

                case CommanderFobPhase.Abandoning:
                    // Announced last review, so the marker has been on screen for one review and the
                    // log line is already written. Pull it down now.
                    CompleteFobAbandonment(hq, state, order);
                    break;
            }
        }

        // Decided after the sweep, so an order opened or a base lost this review is already counted.
        ConsiderFobAbandonment(hq, state);
    }

    /// <summary>
    /// The advance (user instruction 2026-09-14): a commander at its FOB cap whose front has moved a
    /// long way past its rearmost base tears that base down so the next review can build one where
    /// the fighting is. Announced here and carried out next review, so the doomed base carries its
    /// <c>abandoning</c> marker for a review rather than vanishing between two frames.
    /// </summary>
    private void ConsiderFobAbandonment(FactionHQ hq, OperationsState state)
    {
        if (!CommanderSettings.FobEnabled
            || CountOpenFobOrders(state) > 0
            || TryPickFobSite(hq, state, out _, out _, out _))
        {
            // Somewhere left to build is always better than demolishing a working base. Until the
            // FOB cap was retired (reach-and-points, 2026-09-14) this asked whether the commander
            // was below the cap; with no cap, "there is nowhere to put another one" is the same
            // question and the site walk is the only thing that can answer it.
            return;
        }

        CommanderStrategicPoint? candidate = FrontNearestHeldPoint(hq, state, out float candidateDistance);
        if (candidate == null)
        {
            return;
        }

        CommanderFobOrder? rear = null;
        float rearDistance = float.MinValue;
        for (int i = 0; i < state.FobOrders.Count; i++)
        {
            CommanderFobOrder order = state.FobOrders[i];
            if (order.Phase != CommanderFobPhase.Online || order.Base == null)
            {
                continue;
            }

            RefreshFobActivity(hq, state, order);
            float distance = NearestEnemyAssetDistance(hq, order.Point.Position);
            if (distance > rearDistance)
            {
                rearDistance = distance;
                rear = order;
            }
        }

        if (rear == null)
        {
            return;
        }

        bool quiet = rear.LastContactAt < 0f || Time.time - rear.LastContactAt >= FobQuietMinutes * 60f;
        bool inUse = rear.LastUseAt >= 0f && Time.time - rear.LastUseAt < ReviewIntervalSeconds;
        bool limiterLive = state.LastFobAbandonAt >= 0f
            && Time.time - state.LastFobAbandonAt < FobAbandonIntervalMinutes * 60f;
        if (!FobShouldAbandon(
                true, candidateDistance, rearDistance, FobAdvanceMeters, quiet, inUse, limiterLive))
        {
            return;
        }

        rear.Phase = CommanderFobPhase.Abandoning;
        CommanderAiLog.Note(
            hq,
            $"abandons {CommanderCaptureService.GetAirbaseLabel(rear.Base!)}: the front has moved "
                + $"{(rearDistance - candidateDistance) / 1000f:0} km past it.");
    }

    /// <summary>
    /// Stamps a FOB's quiet clock and its in-use clock — one walk of this faction's units and one of
    /// its tracking database per online FOB per review, which is also where Section 3's two proof
    /// lines come from.
    /// </summary>
    private void RefreshFobActivity(FactionHQ hq, OperationsState state, CommanderFobOrder order)
    {
        Airbase? airbase = order.Base;
        if (airbase == null || airbase.center == null)
        {
            return;
        }

        if (TryNearestTrackedHostile(hq, airbase.center.GlobalPosition(), ObservedRadiusMeters, out _, out _))
        {
            order.LastContactAt = Time.time;
        }

        // The presence walk carries the proof lines too, so it runs whatever the mission test says.
        bool inUse = ScanFobPresence(hq, order);

        // A platoon or picket standing on the point is a reason to keep the base whatever is or is
        // not parked on the apron this instant.
        for (int i = 0; !inUse && i < state.Missions.Count; i++)
        {
            CommanderOperationsMission mission = state.Missions[i];
            inUse = ReferenceEquals(mission.Point, order.Point)
                && (mission.Assigned.Count > 0 || mission.PicketMembers.Count > 0);
        }

        if (inUse)
        {
            order.LastUseAt = Time.time;
        }
    }

    /// <summary>
    /// Pulls an abandoned FOB down: the three structures are demolished through the same despawn the
    /// player's DEMOLISH button uses — no refund, exactly as demolishing a mine gives none — and then
    /// the airbase identity goes the way it goes when the last building is destroyed. No loss
    /// cooldown: abandoning is a decision, not a defeat, and the whole point is to build again at the
    /// front on the next review.
    /// </summary>
    private void CompleteFobAbandonment(FactionHQ hq, OperationsState state, CommanderFobOrder order)
    {
        string label = order.Base != null
            ? CommanderCaptureService.GetAirbaseLabel(order.Base)
            : $"FOB {order.Point.Label}";
        for (int i = 0; i < order.Buildings.Count; i++)
        {
            CommanderEconomyService.DespawnUnit(order.Buildings[i]);
        }

        order.Buildings.Clear();
        CommanderEconomyService.Instance?.TearDownFob(hq, order.Base);
        state.FobOrders.Remove(order);
        state.LastFobAbandonAt = Time.time;
        CommanderAiLog.Note(hq, $"{label} is abandoned; its depot, radar and pad are demolished.");
    }

    /// <summary>
    /// Whether a platoon lift's landing zone is still far enough from anything spotted, pure (user
    /// decision 2026-09-15): more than <paramref name="standoffMeters"/> from the nearest hostile
    /// ground unit this commander has tracked. A lift flies under an escort and a sweep, which is
    /// what lets it cross ground a lone picket flight may not — but no escort excuses setting six
    /// vehicles down on top of a column. Exactly on the standoff is too close, the insertion threat
    /// gate's own convention and for the same reason: being wrong here costs the whole platoon.
    /// </summary>
    internal static bool LiftLandingStillClear(float nearestHostileMeters, float standoffMeters)
    {
        return nearestHostileMeters > standoffMeters;
    }

    /// <summary>
    /// Re-runs the standoff the launch asked, for one open order, every review. Returns false when
    /// the order is no longer on the board — cancelled, or moved to other ground — so the caller
    /// stops working on it this review.
    /// <para>A FOB reads the same <see cref="InsertionStandoffClear"/> its site picker read, off this
    /// review's ranking (which now counts spotted units); a platoon lift reads
    /// <see cref="LiftLandingStillClear"/>, because a lift is escorted and a construction flight's
    /// site rule is about where a BASE belongs rather than about what a transport can survive.</para>
    /// </summary>
    private bool ReviewLiftStandoff(FactionHQ hq, OperationsState state, CommanderFobOrder order)
    {
        if (order.Purpose == CommanderLiftPurpose.FobConstruction)
        {
            // Each kind of site is re-asked the rule its OWN launch asked, not a stricter one. An
            // expansion FOB — one flown onto ground nobody holds — had to clear the full enemy
            // standoff to be ordered at all (FobExpansionCandidate), so it is held to that. A FOB on
            // a point the commander HOLDS was never asked that standoff: the site picker refused it
            // only for a hostile in contact with the site, and re-asking a 40 km rule of it would
            // cancel every FOB on the map the moment spotted units began to count.
            bool held = ReferenceEquals(order.Point.GetOwner(), hq);
            if (held)
            {
                if (!TryNearestTrackedHostile(
                        hq,
                        order.Point.Position,
                        ContactRangeMeters,
                        CommanderSettings.StandoffContactMemorySeconds,
                        out _,
                        out float contactMeters,
                        out _))
                {
                    return true;
                }

                string contactReason = $"the enemy has come within {contactMeters / 1000f:0.0} km of the site";
                if (!TryRerouteFobOrder(hq, state, order, contactReason))
                {
                    CancelFobOrder(hq, state, order, contactReason);
                }

                return false;
            }

            // In the AIR the site is held to FobAbortStandoffMeters, not the site picker's full enemy
            // standoff (user decision 2026-09-16): the picker's 40 km says where a base belongs, and
            // re-asking it of a flight already out turned nearly every long construction flight round
            // — `FOB HILLTOP 21 cancelled: the enemy has come within 39 km of the site`.
            if (!TryGetRanked(state, order.Point, out CommanderRankedPoint ranked)
                || InsertionStandoffClear(
                    ranked.IsFront, ranked.DistanceToEnemyMeters, CommanderSettings.FobAbortStandoffMeters))
            {
                return true;
            }

            // Not a loss: nothing was shot down, the ground simply stopped being ground a base
            // belongs on. Another site is looked for first and the load flies on to it; only when
            // there is none is the order cancelled, and the site's ordinary cooldown is what keeps
            // the next review off it.
            string standoffReason = $"the enemy has come within {ranked.DistanceToEnemyMeters / 1000f:0} km of the site";
            if (!TryRerouteFobOrder(hq, state, order, standoffReason))
            {
                CancelFobOrder(hq, state, order, standoffReason);
            }

            return false;
        }

        float standoff = CommanderSettings.LiftAbortStandoffMeters;
        if (!TryNearestTrackedHostile(
                hq,
                order.Point.Position,
                standoff,
                CommanderSettings.StandoffContactMemorySeconds,
                out _,
                out float hostileMeters,
                out string hostileLabel)
            || LiftLandingStillClear(hostileMeters, standoff))
        {
            return true;
        }

        CommanderStrategicPoint old = order.Point;
        string oldLabel = old.Label;
        // The zone that has gone bad takes the lift cooldown BEFORE the search, so the search cannot
        // hand back the same ground it is moving away from.
        state.LiftCooldownUntil[old] = Time.time + FobPointCooldownMinutes * 60f;
        if (order.Mission != null
            && TryFindLiftLandingPoint(hq, state, order.Mission, out CommanderStrategicPoint moved, out float shortMeters)
            && !ReferenceEquals(moved, old))
        {
            MoveLiftOrder(hq, state, order, moved);
            CommanderAiLog.Note(
                hq,
                $"lift for {LiftLabel(order)} moves its landing zone to {moved.Label}: the enemy came within "
                    + $"{hostileMeters / 1000f:0} km of {oldLabel}"
                    + (hostileLabel.Length > 0 ? $" ({hostileLabel})" : string.Empty)
                    + $"; it is now {shortMeters / 1000f:0} km short of the objective.");
            return true;
        }

        CancelFobOrder(
            hq,
            state,
            order,
            $"the enemy came within {hostileMeters / 1000f:0} km of {oldLabel} and there is no other landing zone",
            keepAirMobile: false);
        return false;
    }

    /// <summary>
    /// Takes every flight of this order out of the air without counting a loss — the bookkeeping
    /// <see cref="TryNoteFobLaunchFailed"/> does for a transport abandoned on its deck, moved here so
    /// the landing-zone move can reuse it rather than paraphrase it (Reuse rule 3). The supply side's
    /// per-point mission is withdrawn, the record is dropped, and the load is simply sent again.
    /// </summary>
    /// <param name="knownAs">The point the supply side still files the flight under when the order
    /// has already been moved off it; null means the order's own point.</param>
    private static void WithdrawLiftFlights(
        FactionHQ hq, OperationsState state, CommanderFobOrder order, string reason, CommanderStrategicPoint? knownAs = null)
    {
        for (int i = state.FobFlights.Count - 1; i >= 0; i--)
        {
            CommanderFobFlight flight = state.FobFlights[i];
            if (!ReferenceEquals(flight.Order, order) || flight.Delivered)
            {
                continue;
            }

            state.FobFlights.RemoveAt(i);
            WithdrawLiftFlight(hq, flight, reason, knownAs);
        }
    }

    /// <summary>
    /// ONE load taken out of the air without counting a loss. The body of
    /// <see cref="WithdrawLiftFlights"/>, lifted out when the partial-wave recall became its second
    /// caller (lift-wave_20260916, Reuse rules 3 and 5) — behaviour-neutral apart from the cancel,
    /// which now quotes this flight's own slot id instead of the point. That matters the moment a
    /// point carries more than one load: the old call recalled EVERY cargo mission standing on the
    /// point, so one transport abandoned on a deck would have pulled the whole wave down with it.
    /// <para>The caller removes the record from <c>state.FobFlights</c> first, because the two
    /// callers walk that list differently — one backwards over every flight of an order, the other
    /// over the wave it has just launched.</para>
    /// </summary>
    /// <param name="knownAs">The point the supply side still files the flight under when the order
    /// has already been moved off it; null means the order's own point.</param>
    private static void WithdrawLiftFlight(
        FactionHQ hq, CommanderFobFlight flight, string reason, CommanderStrategicPoint? knownAs = null)
    {
        CommanderFobOrder order = flight.Order;
        CommanderSupplyHeliService.Instance?.CancelInsertion(hq, knownAs ?? order.Point, flight.SlotId);
        order.LaunchFailures++;
        CommanderAiLog.Note(
            hq,
            $"{LiftLabel(order)}: the load in the air is recalled because {reason}; not counted as lost, "
                + "it will be sent again.");
    }

    /// <summary>
    /// Moves an open lift order onto other ground and takes its flights with it (user decision
    /// 2026-09-16: "re-routed rather than RTB"). The one definition for the platoon lift's
    /// landing-zone move, the logistics watch's divert and the FOB re-route (Reuse rule 4): new hold
    /// posts on the new point, every undelivered flight re-posted, the supply side's mission
    /// re-pointed through <c>TryRedirectInsertion</c>, and the order's waits reset. A flight the
    /// supply side no longer answers for is withdrawn the old way and sent again from the new zone.
    /// </summary>
    private void MoveLiftOrder(
        FactionHQ hq, OperationsState state, CommanderFobOrder order, CommanderStrategicPoint moved)
    {
        CommanderStrategicPoint old = order.Point;
        order.Point = moved;
        order.Posts.Clear();
        order.Posts.AddRange(EnsureHoldPosts(moved, order.LoadsWanted * order.VehiclesPerLoad));
        RedirectLiftFlights(hq, state, order, old);
        order.LiftHoldSince = -1f;
        order.LiftHoldLogged = false;
        order.HopelessSince = -1f;
        order.HopelessReported = string.Empty;
        state.FobDenials.Remove(old);
    }

    /// <summary>Re-posts every undelivered flight of an order that has just moved from
    /// <paramref name="old"/> to its current point, and re-points the supply side's missions. ONE
    /// call to the supply side PER LOAD, each quoting its own slot (lift-wave_20260916): the whole
    /// wave goes to the new landing zone together, but each load keeps its own hold post, so three
    /// transports are not all told to touch down on the same spot. It used to be one point-wide call
    /// with one target, which was right while a point could only carry one load.</summary>
    private static void RedirectLiftFlights(
        FactionHQ hq, OperationsState state, CommanderFobOrder order, CommanderStrategicPoint old)
    {
        int open = 0;
        int flown = 0;
        liftRedirectScratch.Clear();
        for (int i = 0; i < state.FobFlights.Count; i++)
        {
            CommanderFobFlight flight = state.FobFlights[i];
            if (!ReferenceEquals(flight.Order, order) || flight.Delivered)
            {
                continue;
            }

            // Its own spot on the new ground, through the dispatch's own index (Reuse rule 4): a
            // whole wave moving together must land the way it would have launched, one post per
            // load — or per vehicle for a platoon lift — rather than three transports converging on
            // post zero (lift-wave_20260916).
            int postIndex = LiftPostIndex(
                order.Purpose, order.Delivered, order.VehiclesLanded, open, order.VehiclesPerLoad);
            flight.Post = order.Posts.Count > 0
                ? order.Posts[postIndex % order.Posts.Count]
                : order.Point.Position;
            flight.NearLandingZoneSince = 0f;
            open++;
            // One call per LOAD, quoting its slot, because each load is being sent somewhere
            // different. A load the supply side no longer answers for is withdrawn and sent again
            // from the new zone; the rest fly on.
            if (CommanderSupplyHeliService.Instance?.TryRedirectInsertion(
                    hq, old, order.Point, flight.Post, flight.SlotId) == true)
            {
                flown++;
            }
            else
            {
                liftRedirectScratch.Add(flight);
            }
        }

        if (open == 0)
        {
            liftRedirectScratch.Clear();
            return;
        }

        if (flown > 0)
        {
            CommanderAiLog.Note(
                hq,
                $"{LiftLabel(order)}: {flown} load{(flown == 1 ? string.Empty : "s")} in the air "
                    + $"{(flown == 1 ? "flies" : "fly")} on to {order.Point.Label} instead of turning back.");
        }

        for (int i = liftRedirectScratch.Count - 1; i >= 0; i--)
        {
            CommanderFobFlight flight = liftRedirectScratch[i];
            state.FobFlights.Remove(flight);
            WithdrawLiftFlight(hq, flight, $"its landing zone moved to {order.Point.Label}", old);
        }

        liftRedirectScratch.Clear();
    }

    /// <summary>
    /// Finds another site for a construction order whose ground has gone bad and moves the order
    /// there, load and all (user decision 2026-09-16). The bad site takes its FOB cooldown first so
    /// the picker cannot hand it back; the picker is the review's own <see cref="TryPickFobSite"/>
    /// asked in its re-route mode, so the new site passes every rule the first one did — room for
    /// the buildings, spacing from depots and other FOBs, clear of contact, and worth building for
    /// the points it brings within reach. False when there is nowhere else, and the caller cancels.
    /// </summary>
    private bool TryRerouteFobOrder(
        FactionHQ hq, OperationsState state, CommanderFobOrder order, string reason)
    {
        CommanderStrategicPoint old = order.Point;
        state.FobCooldownUntil[old] = Time.time + FobPointCooldownMinutes * 60f;
        if (!TryPickFobSite(hq, state, out CommanderStrategicPoint moved, out _, out int broughtInReach, reroute: order)
            || ReferenceEquals(moved, old))
        {
            return false;
        }

        MoveLiftOrder(hq, state, order, moved);
        CommanderAiLog.Note(
            hq,
            $"FOB {old.Label} re-routed to {moved.Label}: {reason}; the new site brings {broughtInReach} "
                + "out-of-reach points within depot reach.");
        return true;
    }

    /// <summary>
    /// The air variant (design Section 2; lift-wave_20260916): one transport per delivery, each
    /// carrying what the shared insertion chooser loads and each bound to its own hold post.
    /// Requested through <c>TryLaunchInsertionAircraft</c> — the third programmatic caller after the
    /// SAM foundation drop and the picket insertion — so the route threat gate, the hull charge and
    /// refund, and the helicopter's recovery are the ones already in service.
    /// <para>
    /// ALL LOADS OR NONE (user instruction 2026-09-16: "should only happen if all 3 aircraft can fly
    /// at once to deploy full platoon in one flight (plus escort)"). Until now this sent ONE load at
    /// a time, because three concurrent requests to one point would have been the first time the
    /// supply side was asked to hold three cargo missions for the same
    /// <see cref="CommanderStrategicPoint"/> and its per-point calls were written to the invariant
    /// that a point carries at most one. That invariant is now lifted properly rather than worked
    /// around: every lift flight carries a slot id
    /// (<see cref="CommanderFobFlight.SlotId"/>) through the queue, the pending spawn and the live
    /// mission, so a recall, an airdrop conversion, a registration bind and a delivered vehicle each
    /// address ONE load of the wave. The picket insertion and every other cargo run pass
    /// <c>InsertionFlightUnslotted</c> and keep exactly the behaviour they had.
    /// </para>
    /// <para>
    /// Four things must all hold before anything leaves the deck: room under the shared airborne
    /// ceiling for the whole wave, funds for the whole wave at the existing multiple, the escort up
    /// by the existing <see cref="LiftCoverIsUp"/> and its bounded wait, and the logistics watch's
    /// route. Any of them short and NOTHING launches. The order then waits, and the wait is ended by
    /// the clocks that already exist — the hopeless rule and <see cref="FobOrderTimeoutSeconds"/> —
    /// which cancel it into an ordinary driving platoon. No new clock (user instruction).
    /// </para>
    /// <para>
    /// A forward-base construction order wants <see cref="FobDeliveries"/> load, so its wave is
    /// always one and every gate here collapses to the single-load expression it replaced. Four
    /// self-checks pin that equivalence.
    /// </para>
    /// </summary>
    private void DispatchFobAirFlights(FactionHQ hq, OperationsState state, CommanderFobOrder order)
    {
        if (!CommanderSettings.OperationsHeliInsertionEnabled)
        {
            ReportFobDenial(hq, state, order.Point, LiftLabel(order), "helicopter insertion is switched off");
            return;
        }

        int inFlight = CountFobFlightsFor(state, order);
        int wave = LiftLoadsOutstanding(order.Delivered, inFlight, order.LoadsWanted);
        if (wave <= 0)
        {
            return;
        }

        int ceiling = Mathf.Max(0, CommanderSettings.OperationsHeliInsertionLimit);
        int alreadyOut = CountInsertionsInFlight(state);
        if (!LiftWaveHasRoom(alreadyOut, wave, ceiling))
        {
            ReportFobDenial(
                hq,
                state,
                order.Point,
                LiftLabel(order),
                wave <= 1
                    ? "every transport this commander may fly is already out"
                    : $"the whole lift goes at once or not at all: it wants {wave} transports and only "
                        + $"{Mathf.Max(0, ceiling - alreadyOut)} of {ceiling} may fly");
            return;
        }

        // The logistics watch has found no safe way in and is waiting for one (user decision
        // 2026-09-16). Nothing leaves the deck until it clears the wait, and the watch's own line is
        // what says why — this is the form-up gate the order waits at, so it is silent here.
        if (order.HopelessSince >= 0f)
        {
            return;
        }

        // The money gate, for the WHOLE WAVE (fix, 2026-09-15, widened lift-wave_20260916): it was
        // asked once when the order was opened, and the launch then handed the whole balance to the
        // flight as its allowance — so the second and third loads of a three-load lift flew on the
        // commander's last funds however much the gate had wanted in the bank. It is now asked for
        // every load the wave will launch, because a wave that could pay for two of its three
        // transports must not go at all. A FOB order is charged its structures once at order time
        // and is not asked again.
        if (order.Purpose == CommanderLiftPurpose.Platoon)
        {
            float loadPrice = CommanderSupplyHeliService.Instance?.CheapestInsertionFlightValue(
                hq, order.VehiclesPerLoad, requireAirdrop: false, order.Point.Position) ?? float.MaxValue;
            float wavePrice = LiftWavePrice(loadPrice, wave);
            if (!LiftAffordable(hq.factionFunds, wavePrice, CommanderSettings.LiftFundsMultiple))
            {
                ReportFobDenial(
                    hq,
                    state,
                    order.Point,
                    LiftLabel(order),
                    loadPrice >= float.MaxValue
                        ? "no held base can launch a transport for the lift"
                        : $"the lift waits: {hq.factionFunds:0} in the bank, "
                            + $"{Mathf.Max(1f, CommanderSettings.LiftFundsMultiple):0}x the {wavePrice:0} "
                            + $"{wave} load{(wave == 1 ? string.Empty : "s")} cost is wanted");
                return;
            }
        }

        // The hold says itself once per load through the order's own flag, so it does not also go
        // through ReportFobDenial — that exists to make a repeated refusal log once, which this
        // already does, and two lines for one hold is the noise the denial rule was written against.
        if (!LiftCoverIsUp(hq, state, order))
        {
            return;
        }

        // The wave itself. Every load of it launches in this one review, to the same landing zone,
        // under the one escort that has just been found up.
        liftWaveScratch.Clear();
        string decline = "no transport service available";
        for (int load = 0; load < wave; load++)
        {
            if (TryLaunchLiftLoad(hq, state, order, inFlight + load, out decline))
            {
                continue;
            }

            // Something the gates could not see refused a load AFTER part of the wave was away —
            // the roster, a hangar or a base changed inside one review. Prefer failing to launch:
            // the loads already away are recalled by their own slot ids, which leaves the two
            // flights of some OTHER order on this point alone, and the order tries the whole wave
            // again next review. Each recall counts a launch failure, not a loss.
            RecallPartialLiftWave(hq, state, order, decline);
            ReportFobDenial(hq, state, order.Point, LiftLabel(order), decline);
            return;
        }

        liftWaveScratch.Clear();
        state.FobDenials.Remove(order.Point);
        order.LiftHoldSince = -1f;
        order.LiftHoldLogged = false;
        CommanderAiLog.Note(hq, LiftWaveLine(LiftLabel(order), wave, order.LoadsWanted, DescribeLiftCover(state, order)));
    }

    /// <summary>
    /// One load of a wave: its hold post, its landing-zone scout, its slot id and its launch
    /// (lift-wave_20260916). Everything here was <see cref="DispatchFobAirFlights"/>'s own body
    /// before the wave existed and is MOVED rather than rewritten (Reuse rule 3); what is new is
    /// <paramref name="flightsAhead"/>, which steps the hold post so three transports launched in
    /// one review do not set down on the same spot, and the slot id.
    /// </summary>
    private bool TryLaunchLiftLoad(
        FactionHQ hq, OperationsState state, CommanderFobOrder order, int flightsAhead, out string decline)
    {
        // Indexed per VEHICLE for a platoon lift and per LOAD for a construction order (fix,
        // 2026-09-15): a platoon lift books one post per vehicle, so stepping the index by loads used
        // three of its six posts and set two loads down on the same spot.
        int postIndex = LiftPostIndex(
            order.Purpose, order.Delivered, order.VehiclesLanded, flightsAhead, order.VehiclesPerLoad);
        GlobalPosition post = order.Posts.Count > 0
            ? order.Posts[postIndex % order.Posts.Count]
            : order.Point.Position;
        // The picket insertion's landing-zone scout, applied to a construction flight (fix,
        // 2026-09-14: the player's flight to HILLTOP 17 hovered over a wood for fifteen minutes
        // until the order timed out). A blocked post becomes an airdrop when the roster can drop
        // by parachute, otherwise the nearest clear ground inside the ring.
        CommanderSupplyHeliService.LandingZoneReport report = CommanderSupplyHeliService.ScoutLandingZone(post);
        bool airdrop = false;
        if (!report.Clear)
        {
            if (CommanderSupplyHeliService.Instance?.HasLaunchableVehicleTransport(hq, requireAirdrop: true) == true)
            {
                airdrop = true;
            }
            else if (CommanderSupplyHeliService.TryFindClearLandingZone(post, out GlobalPosition clear))
            {
                post = clear;
            }
        }

        CommanderFobFlight flight = new()
        {
            Order = order,
            Post = post,
            Airdrop = airdrop,
            RequestedAt = Time.time,
            SlotId = ++state.LastLiftSlotId,
            LoadOrdinal = Mathf.Max(0, order.Delivered) + flightsAhead + 1,
        };
        // The record opens BEFORE the launch for the reason the picket insertion's does: the launch
        // spawns and registers the transport inside its own call, and the registration notify has to
        // find this record to bind the aircraft.
        state.FobFlights.Add(flight);
        liftWaveScratch.Add(flight);
        decline = "no transport service available";
        if (CommanderSupplyHeliService.Instance?.TryLaunchInsertionAircraft(
                hq,
                order.Point,
                post,
                hq.factionFunds,
                wantedVehicles: order.VehiclesPerLoad,
                requireAirdrop: airdrop,
                out decline,
                out _,
                preferHeavyHull: true,
                wantedRoles: order.Purpose == CommanderLiftPurpose.Platoon ? LiftWantedRoles(order) : null,
                insertionFlightId: flight.SlotId) != true)
        {
            state.FobFlights.Remove(flight);
            liftWaveScratch.Remove(flight);
            return false;
        }

        order.LoadsLaunched++;
        string history = order.Lost > 0 || order.LaunchFailures > 0
            ? $" ({order.Lost} lost, {order.LaunchFailures} failed on the deck so far)"
            : string.Empty;
        string cover = DescribeLiftCover(state, order);
        CommanderAiLog.Note(
            hq,
            order.Purpose == CommanderLiftPurpose.Platoon
                ? $"lift {flight.LoadOrdinal}/{order.LoadsWanted} for {LiftLabel(order)} away ({cover}){history}."
                : $"FOB {order.Point.Label}: construction flight {flight.LoadOrdinal}"
                    + $"/{order.LoadsWanted} away ({cover}){history}.");
        return true;
    }

    /// <summary>
    /// Takes back the loads of a wave that DID get away when a later one of the same wave could not
    /// (lift-wave_20260916). Only the flights launched in this review are recalled — a top-up wave
    /// launched beside loads that are already half-way to the point must not pull those down — and
    /// each goes back through <see cref="WithdrawLiftFlight"/>, the one definition, quoting its own
    /// slot id. Nothing is counted as lost: the loads were never delivered and never shot at.
    /// </summary>
    private static void RecallPartialLiftWave(
        FactionHQ hq, OperationsState state, CommanderFobOrder order, string decline)
    {
        if (liftWaveScratch.Count == 0)
        {
            return;
        }

        CommanderAiLog.Note(
            hq,
            $"{LiftLabel(order)}: the lift goes as one wave or not at all — {liftWaveScratch.Count} "
                + $"load{(liftWaveScratch.Count == 1 ? string.Empty : "s")} already away "
                + $"{(liftWaveScratch.Count == 1 ? "is" : "are")} recalled because the rest of it could not "
                + $"launch ({decline}).");
        for (int i = liftWaveScratch.Count - 1; i >= 0; i--)
        {
            CommanderFobFlight flight = liftWaveScratch[i];
            state.FobFlights.Remove(flight);
            WithdrawLiftFlight(hq, flight, "the rest of its wave could not launch");
        }

        liftWaveScratch.Clear();
    }

    /// <summary>
    /// This lift's cover sortie, or null while none has been posted yet — matched by the label the
    /// demand walk gave it, which is how the reconcile identifies a mission-less CAP sortie too
    /// (Reuse rule 4, one identity).
    /// </summary>
    private static CommanderAirSortie? FindLiftCover(OperationsState state, CommanderFobOrder order)
    {
        string label = LiftCoverLabel(order.Point.Label);
        for (int i = 0; i < state.AirSorties.Count; i++)
        {
            if (state.AirSorties[i].Label == label)
            {
                return state.AirSorties[i];
            }
        }

        return null;
    }

    /// <summary>Fighters of a cover that are actually in the air right now — what "escort 2 of 2 up"
    /// counts, and what the launch gate reads. The body moved to <see cref="CountFightersUp"/>
    /// (CommanderOperationsAirPosture.cs, 2026-09-16, Reuse rule 5) when the air fallback posture
    /// became its second caller; the lift's name stays so its callers read as they did.</summary>
    private static int CountLiftEscortsUp(CommanderAirSortie? cover)
    {
        return CountFightersUp(cover);
    }

    /// <summary>
    /// How far the load still has to fly to its landing zone at the moment the gate is asked: the
    /// distance from the airbase it would lift from. The base is the insertion gate's own stand-in
    /// for the leg a flight will fly (<see cref="TryFindInsertionLaunchBase"/>, one definition), and
    /// it is the NEAREST held base, so the escort's lead is measured against the shortest leg the
    /// load could possibly fly — the strictest reading, never a flattering one.
    /// <para>A negative result means no held base could be found, in which case nothing can be
    /// measured and the ahead test is not applied at all; a commander with no airbase cannot launch
    /// a transport anyway, and a lift must never be held by a comparison that could not be made.</para>
    /// </summary>
    private static float LiftLoadDistanceToLandingZone(FactionHQ hq, GlobalPosition landingZone)
    {
        return TryFindInsertionLaunchBase(hq, landingZone, out GlobalPosition origin)
            ? CommanderGameAccess.HorizontalDistance(origin.AsVector3(), landingZone.AsVector3())
            : -1f;
    }

    /// <summary>
    /// Whether this load may leave the deck (design.md, air-mobile-platoons_20260915 Section 3): the
    /// cover's fighters are up, enough of them are AHEAD of the load (user instruction, 2026-09-17)
    /// and its sweep has gone in, or the bounded wait has run out. The wait
    /// is stamped on the order the first review a load is held and cleared the moment one launches,
    /// so every load gets its own bounded wait rather than the order getting one between them all.
    /// <para>This is a LAUNCH gate and nothing else. A transport already in the air is never asked
    /// again whether its escort is still in front of it — that would turn a flight round because its
    /// cover fell behind, which is not what was asked and would fight the delivery watch that already
    /// recalls flights for real threats.</para>
    /// </summary>
    private bool LiftCoverIsUp(FactionHQ hq, OperationsState state, CommanderFobOrder order)
    {
        CommanderAirSortie? cover = FindLiftCover(state, order);
        int wanted = cover?.CapsWanted ?? CommanderSettings.LiftEscortMinimum;
        int up = CountLiftEscortsUp(cover);
        float loadToLandingZone = LiftLoadDistanceToLandingZone(hq, order.Point.Position);
        // Nothing to measure against means the ahead test cannot be asked, so it passes: the escorts
        // that are up are the escorts that count, exactly as before this rule existed.
        int ahead = loadToLandingZone < 0f
            ? up
            : CountFightersAhead(
                cover, order.Point.Position, loadToLandingZone, CommanderSettings.LiftEscortLeadMeters);
        bool aradPending = cover?.AradPending == true;
        if (order.LiftHoldSince < 0f)
        {
            order.LiftHoldSince = Time.time;
        }

        // An escort that is falling back is not "up" for a launch, and the bounded wait does not run
        // it out either (review, 2026-09-16): the load would leave the deck under a cover that has
        // just turned for home and be recalled by the hopeless rule a tick later. The posture's own
        // give-up ends this wait if the escort is never reinforced.
        if (cover != null && cover.FallingBack)
        {
            if (!order.LiftHoldLogged)
            {
                order.LiftHoldLogged = true;
                CommanderAiLog.Note(
                    hq,
                    $"lift for {LiftLabel(order)} holds at the form-up point: waiting for the escort, "
                        + "which is outnumbered and falling back.");
            }

            return false;
        }

        float waited = Time.time - order.LiftHoldSince;
        if (LiftMayLaunch(up, ahead, wanted, aradPending, waited, CommanderSettings.PackageFormUpSeconds))
        {
            if (order.LiftHoldLogged && waited >= CommanderSettings.PackageFormUpSeconds)
            {
                CommanderAiLog.Note(
                    hq,
                    $"lift for {LiftLabel(order)} has waited {CommanderSettings.PackageFormUpSeconds:0} s at the "
                        + $"form-up point; it goes with {up} of {wanted} escort{(wanted == 1 ? string.Empty : "s")} up, "
                        + $"{ahead} of them ahead of it.");
            }

            return true;
        }

        // The escort half of the reason says which of the two tests is holding the load, so the log
        // tells "no fighters yet" apart from "the fighters are still behind the transport".
        string escortReason = up < wanted
            ? $"waiting for the escort ({up} of {wanted} up)"
            : $"waiting for the escort to get ahead of it ({ahead} of {wanted} up ahead, "
                + $"{CommanderSettings.LiftEscortLeadMeters:0} m of lead wanted)";
        string reason = aradPending
            ? "waiting for the sweep"
            : escortReason;
        if (!order.LiftHoldLogged)
        {
            order.LiftHoldLogged = true;
            CommanderAiLog.Note(hq, $"lift for {LiftLabel(order)} holds at the form-up point: {reason}.");
        }

        return false;
    }

    /// <summary>The cover a launching load actually got, for its own log line: what the design calls
    /// <c>escort 2 of 2 up, sweep gone in</c>.</summary>
    private static string DescribeLiftCover(OperationsState state, CommanderFobOrder order)
    {
        CommanderAirSortie? cover = FindLiftCover(state, order);
        int wanted = cover?.CapsWanted ?? 0;
        int up = CountLiftEscortsUp(cover);
        string escort = $"escort {up} of {wanted} up";
        if (cover == null || cover.AradWanted <= 0)
        {
            return escort;
        }

        return cover.AradPending ? $"{escort}, sweep still inbound" : $"{escort}, sweep gone in";
    }

    /// <summary>
    /// The road variant (design Section 2): three munitions trucks out of the free pool driving to
    /// the same hold posts as a mini-platoon. The truck walk, the detach from the game's own rearm
    /// AI and the park order are <c>FillMissionTrucks</c>'s, which is where a forward base's truck
    /// already comes from; the movement seam is the ordinary <c>TrySetDestination</c> every
    /// detachment uses, re-issued each review for a truck that has not arrived.
    /// </summary>
    private void DispatchFobConvoy(FactionHQ hq, OperationsState state, CommanderFobOrder order)
    {
        // A truck that died on the road is a lost delivery; one that reached the ring is an arrival.
        for (int i = order.Convoy.Count - 1; i >= 0; i--)
        {
            Unit truck = order.Convoy[i];
            if (truck == null || truck.disabled || truck.NetworkHQ != hq)
            {
                order.Convoy.RemoveAt(i);
                order.Lost++;
                CommanderAiLog.Note(
                    hq, $"FOB {order.Point.Label}: lost a construction truck on the road ({order.Lost} lost).");
                continue;
            }

            if (FastMath.InRange(truck.transform.GlobalPosition(), order.Point.Position, order.Point.Radius))
            {
                order.Convoy.RemoveAt(i);
                order.Arrivals.Add(truck);
                order.Delivered++;
                CommanderAiLog.Note(
                    hq,
                    $"FOB {order.Point.Label}: {CommanderGameAccess.GetUnitLabel(truck)} arrived "
                        + $"({order.Delivered}/{order.LoadsWanted}).");
            }
        }

        // A lost truck is replaced like a lost flight (fix, 2026-09-15); the loss cap in the review's
        // FobOrderCanContinue is what ends an order that keeps losing them.
        while (order.Delivered + order.Convoy.Count < order.LoadsWanted)
        {
            Unit? truck = null;
            for (int p = 0; p < state.Pool.Count; p++)
            {
                if (CommanderPlatoonRoles.Of(state.Pool[p]?.definition as VehicleDefinition)
                    == CommanderPlatoonRole.Truck)
                {
                    truck = state.Pool[p];
                    break;
                }
            }

            if (truck == null)
            {
                ReportFobDenial(hq, state, order.Point, "no munitions truck idle for the construction convoy");
                break;
            }

            state.Pool.Remove(truck);
            state.PoolIssued.Remove(truck);
            // Detached for the reason a forward base's truck is: the game's rearm AI would otherwise
            // drive it off the site the moment it registers below half capacity.
            CommanderMoveService.TryDetachFromRearmLogistics(truck, allowRestock: false);
            order.Convoy.Add(truck);
            state.FobDenials.Remove(order.Point);
            CommanderAiLog.Note(
                hq,
                $"FOB {order.Point.Label}: {CommanderGameAccess.GetUnitLabel(truck)} joins the construction convoy "
                    + $"({order.Delivered + order.Convoy.Count}/{order.LoadsWanted} arrived or driving).");
        }

        // Re-issued every review for a truck still under way; TrySetDestination is a no-op on a
        // vehicle already driving to the same place.
        for (int i = 0; i < order.Convoy.Count; i++)
        {
            GlobalPosition post = order.Posts.Count > 0
                ? order.Posts[i % order.Posts.Count]
                : order.Point.Position;
            CommanderGameAccess.TrySetDestination(order.Convoy[i], post);
        }
    }

    // ---- The wave (lift-wave_20260916, user instruction 2026-09-16) -----------------------------

    /// <summary>
    /// Loads a lift order still has to put in the air, pure (lift-wave_20260916): what it wants,
    /// less what is on the ground, less what is already flying. This is the SIZE OF THE WAVE — the
    /// dispatch launches exactly this many in one review or none at all — and it is why a wave that
    /// loses a load in flight simply raises a smaller wave next review rather than needing a rule of
    /// its own. Never negative: an order that has over-delivered (a short load followed by a full
    /// one) owes nothing.
    /// </summary>
    internal static int LiftLoadsOutstanding(int delivered, int inFlight, int loadsWanted)
    {
        return Mathf.Max(0, loadsWanted - Mathf.Max(0, delivered) - Mathf.Max(0, inFlight));
    }

    /// <summary>
    /// Whether the shared airborne ceiling has room for the WHOLE wave at once, pure
    /// (lift-wave_20260916, user instruction 2026-09-16: "should only happen if all 3 aircraft can
    /// fly at once"). The ceiling is <c>CommanderSettings.OperationsHeliInsertionLimit</c>, which
    /// picket insertions and lift flights already share; what changes is that a lift asks for room
    /// for every load rather than for the next one. A wave of one — every forward-base construction
    /// order, since <see cref="FobDeliveries"/> is one — reduces to the expression this replaced,
    /// <c>inFlightNow &lt; limit</c>, and a self-check pins that at every reading.
    /// <para>A wave of nothing never launches: there is no such thing as launching no loads.</para>
    /// </summary>
    internal static bool LiftWaveHasRoom(int inFlightNow, int waveLoads, int limit)
    {
        return waveLoads > 0 && Mathf.Max(0, inFlightNow) + waveLoads <= limit;
    }

    /// <summary>
    /// What a whole wave costs, pure (lift-wave_20260916): one load's price times the loads in it.
    /// Handed to <see cref="LiftAffordable"/> in place of the single load's price, so the commander
    /// must hold the multiple of the WHOLE lift before any of it leaves the deck — which is the
    /// money half of "all loads or none". An unpriced load (no base can launch a transport at all,
    /// which the supply side reports as <c>float.MaxValue</c>) stays unpriced however many of them
    /// are wanted, rather than overflowing into a number that would read as affordable.
    /// </summary>
    internal static float LiftWavePrice(float loadPrice, int waveLoads)
    {
        if (loadPrice >= float.MaxValue || loadPrice <= 0f || waveLoads <= 0)
        {
            return float.MaxValue;
        }

        return loadPrice * waveLoads;
    }

    /// <summary>
    /// Which hold post the next load of a lift is bound to, pure (lift-wave_20260916). A
    /// construction order books one post per LOAD and a platoon lift one post per VEHICLE
    /// (<c>EnsureHoldPosts(point, LoadsWanted * VehiclesPerLoad)</c>), so the two purposes step the
    /// index differently. <paramref name="flightsAhead"/> is how many loads of this order are
    /// already in the air or ahead of this one in the same wave — it is what stops three transports
    /// launched in one review from setting down on the same spot, which is the same defect the
    /// per-vehicle index fixed for successive loads on 2026-09-15.
    /// <para>The caller takes the remainder against the posts it actually has, so an order with
    /// fewer posts than loads wraps rather than throwing.</para>
    /// </summary>
    internal static int LiftPostIndex(
        CommanderLiftPurpose purpose, int delivered, int vehiclesLanded, int flightsAhead, int vehiclesPerLoad)
    {
        int ahead = Mathf.Max(0, flightsAhead);
        if (purpose != CommanderLiftPurpose.Platoon)
        {
            return Mathf.Max(0, delivered) + ahead;
        }

        return Mathf.Max(0, vehiclesLanded) + ahead * Mathf.Max(1, vehiclesPerLoad);
    }

    /// <summary>
    /// What the air marker prints beside one transport of a lift, pure (lift-wave_20260916): which
    /// load it is carrying and whether it is still going out. The ordinal is the FLIGHT'S own
    /// (<see cref="CommanderFobFlight.LoadOrdinal"/>), not <c>Delivered + 1</c>, because a wave puts
    /// three transports in the air with nothing yet landed and the old expression made all three
    /// read <c>1/3</c>.
    /// </summary>
    internal static string LiftFlightDetail(int loadOrdinal, int loadsWanted, bool returning)
    {
        return $"{Mathf.Max(1, loadOrdinal)}/{Mathf.Max(1, loadsWanted)}, {(returning ? "returning" : "outbound")}";
    }

    /// <summary>
    /// The line a lift writes when the whole wave is away, pure for the self-check
    /// (lift-wave_20260916). A wave of one is a single load and says so in the singular; a wave of
    /// more says how many went together, because "all of it at once" is the whole point of the
    /// change and a reader has to be able to see it happen in the log.
    /// </summary>
    internal static string LiftWaveLine(string liftLabel, int waveLoads, int loadsWanted, string cover)
    {
        return waveLoads <= 1
            ? $"{liftLabel}: one load away ({cover})."
            : $"{liftLabel}: the whole lift goes in ONE wave — {waveLoads} of {loadsWanted} loads away "
                + $"together ({cover}).";
    }

    /// <summary>Scratch for the loads launched in one wave, so a partial failure can recall exactly
    /// those and nothing else. One commander's wave is assembled at a time, the insertion scratch
    /// convention.</summary>
    private static readonly List<CommanderFobFlight> liftWaveScratch = new();

    /// <summary>The same, for the loads of a moved lift the supply side would not re-point: they are
    /// withdrawn after the walk rather than inside it, so the list being walked is not edited
    /// under the loop.</summary>
    private static readonly List<CommanderFobFlight> liftRedirectScratch = new();

    /// <summary>FOB flights of this order still in the air — what the delivery loop counts as
    /// "already coming".</summary>
    private static int CountFobFlightsFor(OperationsState state, CommanderFobOrder order)
    {
        int count = 0;
        for (int i = 0; i < state.FobFlights.Count; i++)
        {
            if (ReferenceEquals(state.FobFlights[i].Order, order) && !state.FobFlights[i].Delivered)
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>
    /// FOB deliveries in the air for this commander — added to the picket insertions by
    /// <see cref="CountInsertionsInFlight"/> so both kinds of flight share the one airborne limit
    /// (<c>HeliInsertionFlightsMax</c>), which is what stops a FOB order emptying the sky of picket
    /// transports.
    /// </summary>
    private static int CountFobFlightsInFlight(OperationsState state)
    {
        int count = 0;
        for (int i = 0; i < state.FobFlights.Count; i++)
        {
            if (!state.FobFlights[i].Delivered)
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>
    /// The FOB flights' own sweep: a delivered flight whose transport is home is closed, a transport
    /// that died before delivering is a lost delivery, and a request whose transport never
    /// registered is withdrawn on the same stale valve the picket insertion uses.
    /// </summary>
    private void PruneFobFlights(FactionHQ hq, OperationsState state)
    {
        for (int i = state.FobFlights.Count - 1; i >= 0; i--)
        {
            CommanderFobFlight flight = state.FobFlights[i];
            if (flight.Delivered && (flight.Aircraft == null || flight.Aircraft.disabled))
            {
                state.FobFlights.RemoveAt(i);
                continue;
            }

            if (!flight.Delivered && flight.Aircraft != null && flight.Aircraft.disabled)
            {
                state.FobFlights.RemoveAt(i);
                flight.Order.Lost++;
                // Where it died (fix, 2026-09-15): a transport lost a few hundred metres from its own
                // deck is the deck's doing; one lost short of the point is the route's.
                GlobalPosition where = flight.Aircraft.transform.GlobalPosition();
                float toPoint = CommanderGameAccess.HorizontalDistance(where.AsVector3(), flight.Order.Point.Position.AsVector3());
                string nearBase = CommanderEconomyService.NearestHeldBaseLabel(hq, where);
                // And what killed it (diagnostics, 2026-09-16): the game's own damage ledger says
                // whether it was shot at or simply hit the ground — see DescribeDamageCredit.
                string credit = CommanderGameAccess.DescribeDamageCredit(flight.Aircraft);
                CommanderAiLog.Note(
                    hq,
                    $"FOB {flight.Order.Point.Label}: lost a construction flight {toPoint / 1000f:0.0} km short of the point, "
                        + $"near {nearBase}, at {flight.Aircraft.radarAlt:0} m above ground"
                        + (credit.Length > 0 ? $", {credit}" : string.Empty)
                        + $" ({flight.Order.Lost} lost).");
                continue;
            }

            if (flight.Bound && flight.Aircraft == null && !flight.Delivered)
            {
                // Bound once and the object is gone (Unity reads a destroyed object as null): the
                // transport was destroyed, which is a loss. A crew that ejected on the deck has
                // already had its record removed by NoteInsertionLaunchFailed, so what reaches here
                // is the real thing (overnight log 2026-09-15: 37 such flights read as "never
                // registered" and were withdrawn instead of counted).
                state.FobFlights.RemoveAt(i);
                flight.Order.Lost++;
                CommanderAiLog.Note(
                    hq,
                    $"FOB {flight.Order.Point.Label}: a construction flight was destroyed before delivering ({flight.Order.Lost} lost).");
                continue;
            }

            if (flight.Aircraft == null && Time.time - flight.RequestedAt > StaleInsertionSeconds)
            {
                state.FobFlights.RemoveAt(i);
                // This flight's own slot, not the point (lift-wave_20260916): one request of a wave
                // that never registered must not withdraw the two beside it that did.
                CommanderSupplyHeliService.Instance?.CancelInsertion(hq, flight.Order.Point, flight.SlotId);
                CommanderAiLog.Note(
                    hq,
                    $"FOB {flight.Order.Point.Label}: a construction flight never registered after "
                        + $"{Time.time - flight.RequestedAt:0} s; the request is withdrawn.");
                continue;
            }

            // The stall clock, the picket insertion's own (fix, 2026-09-14): a transport that sits
            // over its post without unloading is turned into an airdrop once, and recalled — a lost
            // delivery, which the loss rule then turns into a cancelled order — if it still will not.
            if (!flight.Delivered && flight.Aircraft != null && !flight.Aircraft.disabled)
            {
                bool atPost = CommanderGameAccess.HorizontalDistance(
                    flight.Aircraft.transform.position, flight.Post.ToLocalPosition()) <= InsertionStallRadiusMeters;
                if (!atPost)
                {
                    flight.NearLandingZoneSince = 0f;
                }
                else if (flight.NearLandingZoneSince <= 0f)
                {
                    flight.NearLandingZoneSince = Time.time;
                }
                else if (HasStalledAtLandingZone(
                    Time.time - flight.NearLandingZoneSince, CommanderSettings.OperationsInsertionStallTimeoutSeconds))
                {
                    // The delivery bypass's bounded wait (delivery-bypass_20260916, design section
                    // 4.2), the twin of the picket insertion's. Quoted with this flight's own slot,
                    // so a wave unloads the load that could not get down and not the one beside it.
                    if (!flight.Airdrop
                        && CommanderSupplyHeliService.Instance?.TryForceUnloadInPlace(
                            hq, flight.Order.Point, flight.SlotId) == true)
                    {
                        flight.NearLandingZoneSince = Time.time;
                        continue;
                    }

                    if (!flight.Airdrop
                        && CommanderSupplyHeliService.Instance?.TryConvertInsertionToAirdrop(
                            hq, flight.Order.Point, flight.SlotId) == true)
                    {
                        flight.Airdrop = true;
                        flight.NearLandingZoneSince = Time.time;
                        CommanderAiLog.Note(
                            hq,
                            $"FOB {flight.Order.Point.Label}: the construction flight could not land after "
                                + $"{CommanderSettings.OperationsInsertionStallTimeoutSeconds:0} s; switching it to an airdrop.");
                        continue;
                    }

                    state.FobFlights.RemoveAt(i);
                    CommanderSupplyHeliService.Instance?.CancelInsertion(hq, flight.Order.Point, flight.SlotId);
                    flight.Order.Lost++;
                    CommanderAiLog.Note(
                        hq,
                        $"FOB {flight.Order.Point.Label}: the construction flight could not deliver after "
                            + $"{CommanderSettings.OperationsInsertionStallTimeoutSeconds:0} s over the site; recalled ({flight.Order.Lost} lost).");
                }
            }
        }
    }

    /// <summary>Called by the supply side's registration claim when a spawned transport belongs to a
    /// FOB flight rather than a picket insertion. True when it claimed the event.
    /// <para><paramref name="flightId"/> is the spawning request's own slot (lift-wave_20260916): a
    /// wave has three records open on one point at once, so the transport goes to the record that
    /// asked for it rather than to whichever of the three the list happens to hold first. Zero — a
    /// picket insertion or any older caller — keeps the first-free-record match this always had.</para></summary>
    private bool TryBindFobAircraft(
        OperationsState state, CommanderStrategicPoint point, int flightId, Aircraft aircraft)
    {
        for (int i = 0; i < state.FobFlights.Count; i++)
        {
            CommanderFobFlight flight = state.FobFlights[i];
            if (ReferenceEquals(flight.Order.Point, point)
                && flight.Aircraft == null
                && CommanderCargoFlightSlot.Matches(flight.SlotId, flightId))
            {
                flight.Aircraft = aircraft;
                flight.Bound = true;
                return true;
            }
        }

        return false;
    }

    /// <summary>Called when a cargo vehicle activates on a FOB site: the load is on the ground, so
    /// it is one delivery and the vehicle waits to be consumed by construction. True when this
    /// claimed the delivery.</summary>
    private bool TryTakeFobDelivery(
        FactionHQ hq, OperationsState state, CommanderStrategicPoint point, int flightId, Unit unit)
    {
        for (int i = 0; i < state.FobFlights.Count; i++)
        {
            CommanderFobFlight flight = state.FobFlights[i];
            // The flight that actually unloaded, not the first record on the point
            // (lift-wave_20260916). With a wave in the air the old point-only match credited every
            // vehicle to the FIRST record, so the other two were never marked Delivered and the
            // sweep read their recovery as two LOST loads — which the loss rule then turned into a
            // cancelled order with the platoon already standing on the ground.
            if (!ReferenceEquals(flight.Order.Point, point)
                || !CommanderCargoFlightSlot.Matches(flight.SlotId, flightId))
            {
                continue;
            }

            // A platoon load is TWO vehicles and they activate one at a time, so the claim is made
            // against the order rather than against the flight — a flight is marked delivered by its
            // first vehicle (which is what keeps the sweep from reading its recovery as a loss) and
            // still hands the second one over.
            if (flight.Order.Purpose == CommanderLiftPurpose.Platoon)
            {
                flight.Delivered = true;
                flight.VehiclesLanded++;
                TakeLiftVehicle(hq, state, flight, unit);
                return true;
            }

            if (flight.Delivered)
            {
                continue;
            }

            flight.Delivered = true;
            CommanderFobOrder order = flight.Order;
            order.Delivered++;
            order.LastProgressAt = Time.time;
            // Out of the pool as it joins the order: the registration claim may already have pooled
            // it, and a pooled vehicle can be handed to a picket or platoon before BuildFob consumes it.
            state.Pool.Remove(unit);
            state.PoolIssued.Remove(unit);
            order.Arrivals.Add(unit);
            // A load on the ground is proof the air is flyable, exactly as a picket drop is.
            state.InsertionLossStreak = 0;
            CommanderAiLog.Note(
                hq,
                $"FOB {point.Label}: {CommanderGameAccess.GetUnitLabel(unit)} delivered "
                    + $"({order.Delivered}/{order.LoadsWanted}).");
            return true;
        }

        return false;
    }

    /// <summary>
    /// One vehicle of a platoon lift is on the ground (design.md, air-mobile-platoons_20260915
    /// Section 2): it joins the mission's platoon — which is CREATED by the first landing, since
    /// there was never a depot to form it at — and the order's progress moves on. The adoption is the
    /// picket's own (<see cref="AdoptPicketVehicle"/>, which takes the vehicle out of the free pool
    /// if the depot claim race put it there first), and the platoon it joins is the same object with
    /// the same bookkeeping as one raised at a depot, so the state machine, the markers and the
    /// movement tick take it from here with nothing of their own.
    /// </summary>
    private void TakeLiftVehicle(FactionHQ hq, OperationsState state, CommanderFobFlight flight, Unit unit)
    {
        CommanderFobOrder order = flight.Order;
        order.VehiclesLanded++;
        order.Delivered = LoadsLanded(order.VehiclesLanded, order.VehiclesPerLoad);
        order.LastProgressAt = Time.time;
        // A load on the ground is proof the air is flyable, exactly as a picket drop is.
        state.InsertionLossStreak = 0;
        int wantedVehicles = order.LoadsWanted * order.VehiclesPerLoad;
        CommanderOperationsMission? mission = order.Mission;
        if (!MissionCanAdoptLift(state, mission))
        {
            // The mission went away under the lift (demoted to a picket, or dropped from the board
            // while the flight was out). The vehicle is still a paid-for unit of this faction
            // standing on ground the commander chose, so it joins the pool visibly rather than
            // vanishing into bookkeeping — the insertion path's own answer to the same race.
            state.Pool.Add(unit);
            CommanderAiLog.Note(
                hq,
                $"{CommanderGameAccess.GetUnitLabel(unit)} landed on {order.Point.Label} with no platoon to join; "
                    + "it joins the pool.");
            return;
        }

        // MissionCanAdoptLift has already proved both non-null; the local restates it for the
        // compiler's flow analysis, which cannot see through the helper.
        CommanderOperationsMission adopting = mission!;
        CommanderPlatoon platoon = adopting.Assigned.Count > 0
            ? adopting.Assigned[0]
            : CreateAirMobilePlatoon(hq, state, adopting, order.Point.Position);
        AdoptPicketVehicle(platoon.Members, state.Pool, unit);
        OrderForMarch(platoon.Members);
        platoon.Leader = platoon.Members.Count > 0 ? platoon.Members[0] : null;
        if (platoon.State == CommanderPlatoonState.Forming)
        {
            // A platoon still being flown in gathers at its landing zone, exactly as one being bought
            // gathers at its form-up point. Pointing it at the objective now would march the first
            // two vehicles off toward an enemy-held point on their own while the rest of the lift was
            // still in the air; the last leg is driven once the platoon is whole.
            platoon.Objective = order.Point.Position;
        }
        CommanderAiLog.Note(
            hq,
            $"{platoon.Name}: {flight.VehiclesLanded} vehicle{(flight.VehiclesLanded == 1 ? string.Empty : "s")} landed on "
                + $"{order.Point.Label} ({order.VehiclesLanded}/{wantedVehicles}).");
    }

    /// <summary>
    /// The platoon an air-mobile lift is raising, created by its first landing under the name the
    /// drive-time gate reserved when the mission was marked air-mobile — so the line that announced
    /// the raise, the lift's own lines and the platoon's marker all name the same platoon. Its
    /// establishment and armour slots are the air-mobile recipe's, which is what keeps the teeth rule
    /// from reading six light vehicles as a platoon that has lost its tanks.
    /// </summary>
    private CommanderPlatoon CreateAirMobilePlatoon(
        FactionHQ hq, OperationsState state, CommanderOperationsMission mission, GlobalPosition landingZone)
    {
        if (mission.AirMobilePlatoonName.Length == 0)
        {
            mission.AirMobilePlatoonName = ReservePlatoonName(state);
        }

        CommanderPlatoon platoon = CreatePlatoon(
            state,
            mission.AirMobilePlatoonName,
            AirMobileRecipeCarrier + AirMobileRecipeAirDefence,
            AirMobileRecipeArmour);
        platoon.Objective = landingZone;
        // Forming, not Moving: a platoon arriving two vehicles at a time is gathering, which is what
        // Forming means everywhere else in this service — the withdraw rule leaves it alone while it
        // is under strength, and the movement tick keeps its members together at the landing zone.
        // The lift's completion is what moves it out, through the ordinary march.
        AttachPlatoon(hq, platoon, mission, CommanderPlatoonState.Forming);
        return platoon;
    }

    /// <summary>Called when a FOB construction transport was abandoned on the deck before it flew
    /// (overnight log 2026-09-15). True when it claimed the event: the flight record is dropped
    /// without counting a loss, and the next review sends the same load again — the supply side has
    /// closed the base that failed, so it goes from another one.</summary>
    private bool TryNoteFobLaunchFailed(
        FactionHQ hq, OperationsState state, CommanderStrategicPoint point, int flightId, string baseLabel)
    {
        for (int i = state.FobFlights.Count - 1; i >= 0; i--)
        {
            CommanderFobFlight flight = state.FobFlights[i];
            if (!ReferenceEquals(flight.Order.Point, point)
                || flight.Delivered
                || !CommanderCargoFlightSlot.Matches(flight.SlotId, flightId))
            {
                continue;
            }

            state.FobFlights.RemoveAt(i);
            // This load's own slot, not the point (lift-wave_20260916): a wave of three that loses
            // one crew on a deck keeps the other two flying.
            CommanderSupplyHeliService.Instance?.CancelInsertion(hq, point, flight.SlotId);
            flight.Order.LaunchFailures++;
            CommanderAiLog.Note(
                hq,
                $"FOB {point.Label}: construction flight {flight.LoadOrdinal}/{flight.Order.LoadsWanted} was abandoned on the deck at {baseLabel} "
                    + $"({flight.Order.LaunchFailures} failed launches so far); not counted as lost, it will be sent again from another base.");
            return true;
        }

        return false;
    }

    /// <summary>Called when a FOB transport died before delivering. True when it claimed the loss;
    /// the sweep above does the bookkeeping so a loss is counted once however it is noticed.</summary>
    private bool TryNoteFobFlightLost(OperationsState state, CommanderStrategicPoint point)
    {
        for (int i = 0; i < state.FobFlights.Count; i++)
        {
            CommanderFobFlight flight = state.FobFlights[i];
            if (ReferenceEquals(flight.Order.Point, point) && !flight.Delivered)
            {
                // Leave the record for PruneFobFlights: the aircraft is disabled, so the sweep reads
                // the loss on the next review and there is one counting site, not two.
                return true;
            }
        }

        return false;
    }

    // ---- Construction, cancellation and teardown ------------------------------------------------

    /// <summary>
    /// The third load is in: create the base, put the three buildings inside its ring, consume the
    /// deliveries. Construction is the economy service's (<c>Economy/CommanderFobBuilder.cs</c>) —
    /// its <c>SpawnBuilding</c> and its helipad-to-base link are what a bought pad already goes
    /// through, and forking them here is exactly what Reuse rule 3 forbids.
    /// </summary>
    private void BuildFob(FactionHQ hq, OperationsState state, CommanderFobOrder order)
    {
        order.Phase = CommanderFobPhase.Building;
        CommanderEconomyService? economy = CommanderEconomyService.Instance;
        if (economy == null
            || !economy.TryBuildFob(hq, order.Point.Position, order.Point.Label, order.Buildings, out Airbase? built)
            || built == null)
        {
            CancelFobOrder(hq, state, order, "the base could not be built on that ground");
            return;
        }

        order.Base = built;
        order.Phase = CommanderFobPhase.Online;
        ConsumeFobArrivals(state, order);
        CommanderAiLog.Note(
            hq,
            $"FOB {order.Point.Label} online: {CommanderEconomyService.FobRecipeDescription()} "
                + $"({CommanderCaptureService.GetAirbaseLabel(built)}).");
    }

    /// <summary>
    /// The last load of a platoon lift is down (design.md, air-mobile-platoons_20260915 Section 2):
    /// the order closes and the mission keeps the platoon the lift built. Nothing is consumed and
    /// nothing is constructed — the vehicles ARE the deliverable — so this is the platoon's
    /// counterpart to <see cref="BuildFob"/> and not a branch inside it.
    /// </summary>
    private void CompletePlatoonLift(FactionHQ hq, OperationsState state, CommanderFobOrder order)
    {
        state.FobOrders.Remove(order);
        CommanderOperationsMission? mission = order.Mission;
        if (mission != null)
        {
            mission.LiftOrder = null;
            // The platoon is whole: it moves out to its objective through the ordinary march, which
            // is what drives the last leg from a forward landing zone. A platoon that is already
            // holding its objective — a top-up lift's — is left exactly where it is.
            for (int i = 0; i < mission.Assigned.Count && mission.Point != null; i++)
            {
                CommanderPlatoon platoon = mission.Assigned[i];
                if (platoon.State != CommanderPlatoonState.Forming)
                {
                    continue;
                }

                platoon.State = CommanderPlatoonState.Moving;
                platoon.Objective = mission.Point.Position;
                platoon.ClearGroundPosture();
            }
        }

        string objective = order.Mission?.Point?.Label ?? order.Point.Label;
        string where = objective == order.Point.Label
            ? order.Point.Label
            : $"{order.Point.Label}, and drives the last leg to {objective}";
        CommanderAiLog.Note(
            hq,
            $"{LiftLabel(order)} complete on {where} by air; armour follows by road when a depot is "
                + $"within {CommanderSettings.AirMobileDriveMinutes:0} min.");
    }

    /// <summary>
    /// The deliveries vanish into the construction (user decision 2026-09-14): the trucks are
    /// consumed and so are the cargo vehicles the flights put down. Despawned through the economy
    /// service's own despawn, the one the demolish path already uses, so there is one definition of
    /// "remove this unit from the game".
    /// </summary>
    private static void ConsumeFobArrivals(OperationsState state, CommanderFobOrder order)
    {
        for (int i = 0; i < order.Arrivals.Count; i++)
        {
            CommanderEconomyService.DespawnUnit(order.Arrivals[i]);
        }

        // A truck still on the road when the third load landed is a paid-for vehicle with nothing
        // left to deliver, so it goes back to the pool rather than being quietly forgotten.
        for (int i = 0; i < order.Convoy.Count; i++)
        {
            if (order.Convoy[i] != null && !order.Convoy[i].disabled && !state.Pool.Contains(order.Convoy[i]))
            {
                state.Pool.Add(order.Convoy[i]);
            }
        }

        order.Arrivals.Clear();
        order.Convoy.Clear();
    }

    /// <summary>
    /// The order is off (design Section 2): anything still flying is recalled, the trucks go back to
    /// the free pool where a platoon can use them, and the point takes a cooldown so the next review
    /// does not simply order the same FOB again. No refund — the structures were charged at order
    /// time, which is the stake.
    /// </summary>
    /// <param name="keepAirMobile">True when the objective is still worth flying to and only this
    /// LANDING ZONE failed, so the mission keeps its air-mobile mark and the next review picks other
    /// ground. False — the default — hands a failed raise back to an ordinary driving platoon.</param>
    private void CancelFobOrder(
        FactionHQ hq, OperationsState state, CommanderFobOrder order, string reason, bool keepAirMobile = false)
    {
        for (int i = state.FobFlights.Count - 1; i >= 0; i--)
        {
            if (ReferenceEquals(state.FobFlights[i].Order, order))
            {
                state.FobFlights.RemoveAt(i);
            }
        }

        CommanderSupplyHeliService.Instance?.CancelInsertion(hq, order.Point);
        for (int i = 0; i < order.Convoy.Count; i++)
        {
            if (order.Convoy[i] != null && !order.Convoy[i].disabled && !state.Pool.Contains(order.Convoy[i]))
            {
                state.Pool.Add(order.Convoy[i]);
            }
        }

        for (int i = 0; i < order.Arrivals.Count; i++)
        {
            if (order.Arrivals[i] != null && !order.Arrivals[i].disabled && !state.Pool.Contains(order.Arrivals[i]))
            {
                state.Pool.Add(order.Arrivals[i]);
            }
        }

        order.Convoy.Clear();
        order.Arrivals.Clear();
        state.FobOrders.Remove(order);
        // Which cooldown a cancelled order takes is the purpose's business (fix, 2026-09-15): a lift
        // turned back from a point says nothing about whether a base belongs on it.
        if (LiftTakesFobCooldown(order.Purpose))
        {
            state.FobCooldownUntil[order.Point] = Time.time + FobPointCooldownMinutes * 60f;
        }
        else
        {
            state.LiftCooldownUntil[order.Point] = Time.time + FobPointCooldownMinutes * 60f;
        }

        if (order.Purpose == CommanderLiftPurpose.Platoon)
        {
            // A RAISE that failed hands the objective back to an ordinary driving platoon — a long
            // drive is worse than a flight and much better than nothing (design.md,
            // air-mobile-platoons_20260915 Section 2). A TOP-UP that failed does not: the platoon is
            // already standing on ground no depot can reach, so dropping the mark would only send
            // single replacement vehicles on the drive the lift exists to avoid. It keeps the mark
            // and the landing zone's own cooldown is what paces the retry.
            string label = LiftLabel(order);
            bool raise = order.Mission == null || order.Mission.Assigned.Count == 0;
            bool goesByRoad = raise && !keepAirMobile;
            if (order.Mission != null)
            {
                order.Mission.LiftOrder = null;
                if (goesByRoad)
                {
                    order.Mission.AirMobile = false;
                }
            }

            CommanderAiLog.Note(
                hq,
                $"{label} lift cancelled: {reason}. Cooldown {FobPointCooldownMinutes:0} min"
                    + (goesByRoad
                        ? ", the platoon drives instead."
                        : raise
                            ? "; another landing zone is chosen next review."
                            : "; the platoon holds on what it has."));
            return;
        }

        CommanderAiLog.Note(
            hq,
            $"FOB {order.Point.Label} cancelled: {reason}. Cooldown {FobPointCooldownMinutes:0} min.");
    }

    /// <summary>
    /// The teardown watch (design Section 2): an online FOB whose buildings have ALL been destroyed
    /// leaves an empty registered airbase behind, which would keep answering base queries with
    /// nothing on it. Two buildings gone out of three is a damaged base and the building rung's
    /// captured-base wish rebuilds what is missing, so only the empty case tears down.
    /// <para>It also carries the two proof lines design Section 3 asks for — the first vehicle to
    /// spawn at the FOB's depot and the first aircraft to launch from it — because this is the one
    /// place per review that already holds both the order and its base.</para>
    /// </summary>
    private void WatchFobTeardown(FactionHQ hq, OperationsState state, CommanderFobOrder order)
    {
        // Lost to the enemy: the base is still standing and still working, but it is theirs now. It
        // stops counting against this commander's cap the moment ownership changes (the cap reads the
        // live base list), and the commander-wide loss cooldown starts.
        if (order.Base != null && !ReferenceEquals(order.Base.CurrentHQ, hq))
        {
            state.FobOrders.Remove(order);
            state.FobLossCooldownUntil = Time.time + CommanderSettings.FobLossCooldownMinutes * 60f;
            CommanderAiLog.Note(
                hq,
                $"lost {CommanderCaptureService.GetAirbaseLabel(order.Base)} to the enemy; no FOB is "
                    + $"built anywhere for {CommanderSettings.FobLossCooldownMinutes:0} min.");
            return;
        }

        int live = 0;
        for (int i = 0; i < order.Buildings.Count; i++)
        {
            Unit building = order.Buildings[i];
            if (building != null && !building.disabled)
            {
                live++;
            }
        }

        if (!FobShouldTearDown(live))
        {
            RefreshFobActivity(hq, state, order);
            return;
        }

        CommanderEconomyService.Instance?.TearDownFob(hq, order.Base);
        state.FobOrders.Remove(order);
        state.FobCooldownUntil[order.Point] = Time.time + FobPointCooldownMinutes * 60f;
        state.FobLossCooldownUntil = Time.time + CommanderSettings.FobLossCooldownMinutes * 60f;
        CommanderAiLog.Note(hq, $"FOB {order.Point.Label} destroyed: every building on it is gone.");
    }

    /// <summary>
    /// Walks this faction's units standing on a FOB. Answers two questions in one pass: whether the
    /// base is IN USE right now — which is what stops the abandonment rule pulling a base down from
    /// under the vehicles and aircraft using it — and, once each per base, Section 3's two proof
    /// lines. Nothing new makes either happen: the depot rally and the hangar spawn find the base
    /// through <c>hq.GetAirbases()</c> on their own, so these lines exist to prove in the log that
    /// the base is genuinely in use rather than merely registered.
    /// </summary>
    private static bool ScanFobPresence(FactionHQ hq, CommanderFobOrder order)
    {
        Airbase? airbase = order.Base;
        if (airbase == null || airbase.center == null || hq.factionUnits == null)
        {
            return false;
        }

        GlobalPosition center = airbase.center.GlobalPosition();
        float ring = Mathf.Max(FobCaptureRangeMeters, order.Point.Radius);
        bool present = false;
        foreach (PersistentID id in hq.factionUnits)
        {
            if (!id.TryGetUnit(out Unit unit)
                || unit == null
                || unit.disabled
                || unit is Building
                || !FastMath.InRange(unit.transform.GlobalPosition(), center, ring))
            {
                continue;
            }

            present = true;
            if (!order.DepotUseLogged && unit is GroundVehicle)
            {
                order.DepotUseLogged = true;
                CommanderAiLog.Note(
                    hq,
                    $"{CommanderGameAccess.GetUnitLabel(unit)} deployed at "
                        + $"{CommanderCaptureService.GetAirbaseLabel(airbase)}'s depot.");
            }
            else if (!order.LaunchLogged && unit is Aircraft)
            {
                order.LaunchLogged = true;
                CommanderAiLog.Note(
                    hq,
                    $"{CommanderGameAccess.GetUnitLabel(unit)} launched from "
                        + $"{CommanderCaptureService.GetAirbaseLabel(airbase)}.");
            }
        }

        return present;
    }

    /// <summary>A FOB refusal logs once per reason per point, the <c>ReportInsertionDenial</c>
    /// convention: a review runs every 30 s and the same line every time is noise nobody reads.</summary>
    /// <param name="label">What the held delivery is FOR — <c>FOB HILLTOP 9</c> or the platoon the
    /// lift is raising (fix, 2026-09-15: a platoon lift's refusals all read <c>FOB &lt;point&gt;:
    /// delivery held</c>, which named a base nobody was building).</param>
    private static void ReportFobDenial(
        FactionHQ hq, OperationsState state, CommanderStrategicPoint point, string label, string reason)
    {
        if (state.FobDenials.TryGetValue(point, out string? last) && last == reason)
        {
            return;
        }

        state.FobDenials[point] = reason;
        CommanderAiLog.Note(hq, $"{label}: delivery held — {reason}.");
    }

    /// <summary>The site picker's own refusals, which have an order's purpose to name: a FOB site is
    /// always a FOB.</summary>
    private static void ReportFobDenial(
        FactionHQ hq, OperationsState state, CommanderStrategicPoint point, string reason)
    {
        ReportFobDenial(hq, state, point, $"FOB {point.Label}", reason);
    }

    /// <summary>The review line's <c>lift=</c> field (design.md, air-mobile-platoons_20260915
    /// Section 4; it was <c>fob=</c> until the order gained a purpose), or an empty string when this
    /// commander has no lift open. Appended beside <c>heli=</c> by the diagnostics line.</summary>
    private static string DescribeFob(FactionHQ hq, OperationsState state)
    {
        for (int i = 0; i < state.FobOrders.Count; i++)
        {
            CommanderFobOrder order = state.FobOrders[i];
            if (order.Phase == CommanderFobPhase.Abandoning)
            {
                return $"{LiftLabel(order)} abandoning";
            }

            if (order.Phase != CommanderFobPhase.Online)
            {
                return $"{LiftLabel(order)} {order.Delivered}/{order.LoadsWanted} {(order.ByAir ? "air" : "road")}"
                    + (order.ByAir ? string.Empty : $" convoy={order.Convoy.Count}");
            }
        }

        // Idle: what the commander owns. There is no cap to show it against any more
        // (reach-and-points, 2026-09-14) — the FOB spacing and site score are the only limits, and the review
        // line's reach= field beside this is what says whether another one is wanted.
        return $"{CommanderEconomyService.CountOwnedFobs(hq)} online";
    }

    /// <summary>
    /// The FOB rules at their named boundaries (design Section 5), next to the operations service's
    /// other self-checks: the placement rule, the delivery count that completes an order, the
    /// loss rule, the teardown rule and the marker text. The price sum has its own case beside the
    /// economy service's ladder checks, where the prices live.
    /// </summary>
    /// <summary>
    /// The wave's own rules at their named boundaries (lift-wave_20260916, user instruction
    /// 2026-09-16), in a block of their own rather than inside <see cref="CheckFob"/> for one
    /// reason: every case here is pure arithmetic and string work over
    /// <see cref="CommanderCargoFlightSlot"/> and the lift helpers, with no Unity object and no
    /// game assembly behind it, so this block — unlike <see cref="CheckFob"/>, which asks the
    /// supply service whether a deck is blocked — can be run outside a running game.
    /// </summary>
    private static void CheckLiftWave(List<string> failures)
    {

        // ---- The wave (lift-wave_20260916, user instruction 2026-09-16) -------------------------
        // How big a wave is. The whole change hangs on this number: the dispatch launches exactly
        // this many loads in one review, or none.
        Expect(
            failures,
            "a fresh three-load lift raises a wave of three",
            LiftLoadsOutstanding(0, 0, 3),
            3);
        Expect(
            failures,
            "a lift with its whole wave in the air raises nothing more",
            LiftLoadsOutstanding(0, 3, 3),
            0);
        Expect(
            failures,
            "a lift that lost one load of three in flight raises a wave of one",
            LiftLoadsOutstanding(0, 2, 3),
            1);
        Expect(
            failures,
            "a lift with two loads down and one flying raises nothing",
            LiftLoadsOutstanding(2, 1, 3),
            0);
        Expect(
            failures,
            "a delivered lift raises nothing",
            LiftLoadsOutstanding(3, 0, 3),
            0);
        Expect(
            failures,
            "an over-delivered lift raises nothing rather than a negative wave",
            LiftLoadsOutstanding(4, 0, 3),
            0);
        Expect(
            failures,
            "a forward base raises a wave of one load, which is the flight it has always sent",
            LiftLoadsOutstanding(0, 0, FobDeliveries),
            FobDeliveries);
        Expect(
            failures,
            "a forward base with its one load in the air raises nothing, exactly as it did before the wave",
            LiftLoadsOutstanding(0, 1, FobDeliveries),
            0);

        // Room in the sky for the WHOLE wave. A wave of one must read exactly as the expression it
        // replaced, `inFlightNow < limit`, at every reading — that is the forward-base equivalence.
        for (int out_ = 0; out_ <= 8; out_++)
        {
            Expect(
                failures,
                "a one-load lift asks the airborne ceiling exactly the question it asked before the wave",
                LiftWaveHasRoom(out_, 1, 6),
                out_ < 6);
        }

        Expect(
            failures,
            "an empty sky has room for a whole three-ship wave",
            LiftWaveHasRoom(0, 3, 6),
            true);
        Expect(
            failures,
            "three transports already out still leaves room for a three-ship wave",
            LiftWaveHasRoom(3, 3, 6),
            true);
        Expect(
            failures,
            "four transports already out refuses a three-ship wave rather than sending two of it",
            LiftWaveHasRoom(4, 3, 6),
            false);
        Expect(
            failures,
            "a wave of nothing never launches",
            LiftWaveHasRoom(0, 0, 6),
            false);
        Expect(
            failures,
            "the standing platoon lift fits under the standing airborne ceiling in an empty sky",
            LiftWaveHasRoom(0, CommanderSettings.LiftLoadsPerPlatoon, CommanderSettings.OperationsHeliInsertionLimit),
            true);

        // What a wave costs. The money half of "all loads or none".
        Expect(failures, "a wave of three costs three loads", LiftWavePrice(100f, 3), 300f);
        Expect(
            failures,
            "a wave of one costs what one load costs, which is the price the gate was always given",
            LiftWavePrice(100f, 1),
            100f);
        Expect(
            failures,
            "a load no base can launch is unpriced however many of them are wanted",
            LiftWavePrice(float.MaxValue, 3),
            float.MaxValue);
        Expect(
            failures,
            "a commander who can pay for two loads of three may not launch the wave",
            LiftAffordable(250f, LiftWavePrice(100f, 3), 1f),
            false);
        Expect(
            failures,
            "a commander who can pay for all three may",
            LiftAffordable(300f, LiftWavePrice(100f, 3), 1f),
            true);
        Expect(
            failures,
            "the funds multiple applies to the whole wave, not to one load of it",
            LiftAffordable(300f, LiftWavePrice(100f, 3), 2f),
            false);

        // Which hold post each load of a wave takes. Three transports must not set down on one spot.
        Expect(
            failures,
            "the first load of a construction order takes the first post",
            LiftPostIndex(CommanderLiftPurpose.FobConstruction, 0, 0, 0, 1),
            0);
        Expect(
            failures,
            "a construction order's second load takes the post after its delivered one",
            LiftPostIndex(CommanderLiftPurpose.FobConstruction, 1, 0, 0, 1),
            1);
        Expect(
            failures,
            "the first load of a platoon wave takes the first post",
            LiftPostIndex(CommanderLiftPurpose.Platoon, 0, 0, 0, 2),
            0);
        Expect(
            failures,
            "the second load of a platoon wave takes the post after its own two vehicles",
            LiftPostIndex(CommanderLiftPurpose.Platoon, 0, 0, 1, 2),
            2);
        Expect(
            failures,
            "the third load of a platoon wave takes the post after the first two loads",
            LiftPostIndex(CommanderLiftPurpose.Platoon, 0, 0, 2, 2),
            4);
        Expect(
            failures,
            "no two loads of one wave are ever sent to the same post",
            LiftPostIndex(CommanderLiftPurpose.Platoon, 0, 0, 1, 2)
                != LiftPostIndex(CommanderLiftPurpose.Platoon, 0, 0, 2, 2),
            true);
        Expect(
            failures,
            "a top-up wave starts past the vehicles already on the ground",
            LiftPostIndex(CommanderLiftPurpose.Platoon, 1, 2, 0, 2),
            2);

        // What one transport of a wave says on the map. All three read 1/3 before this.
        Expect(
            failures,
            "each transport of a wave prints the load it is carrying",
            LiftFlightDetail(2, 3, returning: false),
            "2/3, outbound");
        Expect(
            failures,
            "a transport that has unloaded reads as returning",
            LiftFlightDetail(3, 3, returning: true),
            "3/3, returning");
        Expect(
            failures,
            "a forward base's one flight reads exactly as it did before the wave",
            LiftFlightDetail(1, FobDeliveries, returning: false),
            "1/1, outbound");

        // The line the wave writes when it is away.
        Expect(
            failures,
            "a three-load wave says it went all at once",
            LiftWaveLine("1ST PLATOON", 3, 3, "escort 2 of 2 up"),
            "1ST PLATOON: the whole lift goes in ONE wave — 3 of 3 loads away together (escort 2 of 2 up).");
        Expect(
            failures,
            "a forward base's single load does not claim to be a wave",
            LiftWaveLine("FOB HILLTOP 9", 1, 1, "escort 2 of 2 up"),
            "FOB HILLTOP 9: one load away (escort 2 of 2 up).");

        // The slot id, which is what lifting the one-per-point invariant rests on.
        Expect(
            failures,
            "an unslotted call still means every flight on the point, as every picket insertion needs",
            CommanderCargoFlightSlot.Matches(7, CommanderCargoFlightSlot.Unslotted),
            true);
        Expect(
            failures,
            "a slotted call reaches its own load of a wave",
            CommanderCargoFlightSlot.Matches(7, 7),
            true);
        Expect(
            failures,
            "a slotted call never reaches the load flying beside it",
            CommanderCargoFlightSlot.Matches(8, 7),
            false);
        Expect(
            failures,
            "an unslotted flight is not swept up by a call meant for one load of a wave",
            CommanderCargoFlightSlot.Matches(
                CommanderCargoFlightSlot.Unslotted, 7),
            false);
    }

    private static void CheckFob(List<string> failures)
    {
        // The defaults as shipped (user request 2026-09-15: 5 km each, halved from 10 km and cut
        // from 20 km). The rule is checked at these numbers so a retune that put either minimum at
        // or past the depot reach — a FOB that could never extend reach — would fail here.
        const float minDepot = 5000f;
        const float minSpacing = 5000f;
        const float reach = 20000f;
        const float far = 99000f;

        // Placement (reach-and-points Section 3, user decision 2026-09-14). Each distance is a
        // refusal on its own, and both boundaries are on the refusing side.
        Expect(
            failures,
            "a site clear of owned depots and of other FOBs is allowed",
            FobSiteAllowed(minDepot + 1f, minDepot, far, minSpacing),
            true);
        Expect(
            failures,
            "a site too near an owned depot is skipped",
            FobSiteAllowed(minDepot - 1f, minDepot, far, minSpacing),
            false);
        Expect(
            failures,
            "a site exactly on the minimum depot distance is too close",
            FobSiteAllowed(minDepot, minDepot, far, minSpacing),
            false);
        Expect(
            failures,
            "a site inside the FOB spacing is skipped",
            FobSiteAllowed(far, minDepot, minSpacing - 1000f, minSpacing),
            false);
        Expect(
            failures,
            "a site exactly on the FOB spacing is too close to the last FOB",
            FobSiteAllowed(far, minDepot, minSpacing, minSpacing),
            false);
        Expect(
            failures,
            "one metre past the FOB spacing is far enough from the last FOB",
            FobSiteAllowed(far, minDepot, minSpacing + 1f, minSpacing),
            true);
        Expect(
            failures,
            "a commander with no depot at all may build anywhere clear of other FOBs",
            FobSiteAllowed(float.MaxValue, minDepot, float.MaxValue, minSpacing),
            true);
        Expect(
            failures,
            "both FOB minimums stand inside the depot reach, so a FOB can always extend it",
            minDepot < reach && minSpacing < reach,
            true);

        // The ground itself (user instruction 2026-09-16, "fobs should not be built on resource
        // sites"). The refusal is on the kind discovery decided, so each kind is named here and a
        // future kind that quietly slipped into the refusal would fail one of these.
        Expect(
            failures,
            "a resource site is never FOB ground",
            FobKindAllowed(StrategicPointKind.Site),
            false);
        Expect(failures, "a village may carry a FOB", FobKindAllowed(StrategicPointKind.Village), true);
        Expect(failures, "a hilltop may carry a FOB", FobKindAllowed(StrategicPointKind.Hilltop), true);
        Expect(failures, "an outpost may carry a FOB", FobKindAllowed(StrategicPointKind.Outpost), true);
        Expect(failures, "a crossroads may carry a FOB", FobKindAllowed(StrategicPointKind.Crossroads), true);
        Expect(failures, "a roadside point may carry a FOB", FobKindAllowed(StrategicPointKind.Roadside), true);
        Expect(failures, "a base is not refused by the FOB ground rule", FobKindAllowed(StrategicPointKind.Base), true);

        // The recipe (user instruction 2026-09-16, "FOBs should have 2x helipads rather than 1") and
        // the layout that follows from it. The ring radius is derived from the spacing and the
        // building count, so both a recipe change and a spacing retune are caught here.
        Expect(
            failures,
            "a FOB carries two helipads",
            CommanderEconomyService.FobHelipadCount,
            2);
        Expect(
            failures,
            "the recipe wording names both helipads",
            CommanderEconomyService.FobRecipeDescription(),
            "depot, radar and 2 helipads");
        // A metre of tolerance rather than exact equality: the ring is a sine, the old expression was
        // a square root, and the two agree to far better than a metre without agreeing bit for bit.
        // A metre is well inside what would matter on a 120 m spacing.
        Expect(
            failures,
            "three buildings still stand one spacing apart, as the old sqrt(3) ring did",
            Mathf.Abs(CommanderEconomyService.FobRingRadiusMeters(FobBuildingSpacingMeters, 3)
                - (FobBuildingSpacingMeters / Mathf.Sqrt(3f))) < 1f,
            true);
        Expect(
            failures,
            "four buildings stand one spacing apart on a sqrt(2) ring",
            Mathf.Abs(CommanderEconomyService.FobRingRadiusMeters(FobBuildingSpacingMeters, 4)
                - (FobBuildingSpacingMeters / Mathf.Sqrt(2f))) < 1f,
            true);
        Expect(
            failures,
            "the two-helipad recipe's own ring keeps its buildings one spacing apart",
            Mathf.Abs((2f * CommanderEconomyService.FobRingRadiusMeters(FobBuildingSpacingMeters, 4)
                    * Mathf.Sin(Mathf.PI / 4f))
                - FobBuildingSpacingMeters) < 1f,
            true);
        Expect(
            failures,
            "a lone building has no ring to stand on",
            CommanderEconomyService.FobRingRadiusMeters(FobBuildingSpacingMeters, 1),
            0f);
        Expect(
            failures,
            "the whole FOB ring stands inside the capture ring it is taken in",
            CommanderEconomyService.FobRingRadiusMeters(FobBuildingSpacingMeters, 4) < FobCaptureRangeMeters,
            true);

        // The score: how many stranded points a depot on the site would reach.
        Expect(failures, "nothing out of reach means no FOB", CountBroughtInReach(new List<float>(), reach), 0);
        Expect(
            failures,
            "a site counts every stranded point inside the depot reach",
            CountBroughtInReach(new List<float> { 1000f, 9000f, 19000f }, reach),
            3);
        Expect(
            failures,
            "a stranded point exactly on the depot reach counts, one metre past does not",
            CountBroughtInReach(new List<float> { reach, reach + 1f }, reach),
            1);

        // Which of two candidates wins.
        Expect(
            failures,
            "a site bringing three points in reach beats one bringing two",
            FobSiteBeats(3, 5000f, 2, 1000f),
            true);
        Expect(
            failures,
            "a site bringing two points in reach loses to one bringing three",
            FobSiteBeats(2, 1000f, 3, 5000f),
            false);
        Expect(
            failures,
            "equal reach gain goes to the site nearer the enemy",
            FobSiteBeats(2, 1000f, 2, 5000f),
            true);
        Expect(
            failures,
            "equal reach gain and equal distance keeps the site already chosen",
            FobSiteBeats(2, 1000f, 2, 1000f),
            false);

        // The money gate: the rung must have banked the whole price, and the balance must cover it.
        Expect(failures, "savings and funds exactly at the price are enough", FobAffordable(100f, 100f, 100f), true);
        Expect(failures, "a FOB flies by preference when money is not tight", FobGoesByAir(false, false, true, 300f, 75f, 3f), true);
        Expect(failures, "a neutral out-of-reach point clear of the enemy in transport range is a flown-in FOB site", FobExpansionCandidate(true, true, true, true, 40_000f, 60_000f), true);
        Expect(failures, "a point inside the enemy standoff is never an expansion FOB site", FobExpansionCandidate(true, true, false, true, 40_000f, 60_000f), false);
        Expect(failures, "no vehicle transport means no expansion FOB", FobExpansionCandidate(true, true, true, false, 40_000f, 60_000f), false);
        Expect(failures, "a point within depot reach is not an expansion site", FobExpansionCandidate(true, false, true, true, 40_000f, 60_000f), false);
        Expect(failures, "a FOB drives when money is tight and the road is open", FobGoesByAir(false, false, true, 200f, 75f, 3f), false);
        Expect(failures, "an off-road FOB flies however tight money is", FobGoesByAir(true, false, true, 0f, 75f, 3f), true);
        Expect(failures, "a threatened road sends the FOB by air", FobGoesByAir(false, true, true, 0f, 75f, 3f), true);
        Expect(failures, "no vehicle transport means the FOB drives", FobGoesByAir(true, true, false, 9000f, 75f, 3f), false);
        Expect(failures, "savings one short hold the order", FobAffordable(99f, 100f, 100f), false);
        Expect(failures, "funds one short hold the order", FobAffordable(100f, 100f, 99f), false);
        Expect(failures, "a priceless FOB is never ordered", FobAffordable(100f, 0f, 100f), false);

        // The advance rule (user instruction 2026-09-14): tear one down only to gain real ground.
        Expect(
            failures,
            "exactly the advance threshold is enough to abandon a rear FOB",
            FobShouldAbandon(true, 10000f, 10000f + FobAdvanceMeters, FobAdvanceMeters, true, false, false),
            true);
        Expect(
            failures,
            "one metre short of the advance threshold is not",
            FobShouldAbandon(true, 10000f, 10000f + FobAdvanceMeters - 1f, FobAdvanceMeters, true, false, false),
            false);
        Expect(
            failures,
            "with somewhere left to build a commander builds instead of abandoning",
            FobShouldAbandon(false, 10000f, 40000f, FobAdvanceMeters, true, false, false),
            false);
        Expect(
            failures,
            "a FOB in contact is never abandoned",
            FobShouldAbandon(true, 10000f, 40000f, FobAdvanceMeters, false, false, false),
            false);
        Expect(
            failures,
            "a FOB in use is never abandoned",
            FobShouldAbandon(true, 10000f, 40000f, FobAdvanceMeters, true, true, false),
            false);
        Expect(
            failures,
            "no second abandonment inside the limiter window",
            FobShouldAbandon(true, 10000f, 40000f, FobAdvanceMeters, true, false, true),
            false);

        // Delivery count: the order completes on the third load and not before.
        Expect(failures, "two loads do not build a three-load FOB", FobReadyToBuild(2, 3), false);
        Expect(failures, "the last load builds the FOB", FobReadyToBuild(FobDeliveries, FobDeliveries), true);
        Expect(
            failures,
            "a fourth load cannot un-build a FOB",
            FobReadyToBuild(FobDeliveries + 1, FobDeliveries),
            true);

        // Losses: a first loss with nothing landed cancels; after that every lost load is replaced
        // until the loss cap, and a complete order is never cancelled.
        Expect(
            failures,
            "a first delivery lost with nothing landed cancels the order",
            FobOrderCanContinue(0, 1, FobDeliveries, FobMaxLostLoads),
            false);
        Expect(
            failures,
            "one landed and one lost keeps the order running",
            FobOrderCanContinue(1, 1, FobDeliveries, FobMaxLostLoads),
            true);
        Expect(
            failures,
            "two delivered and one lost keeps the order running: the lost load is sent again",
            FobOrderCanContinue(2, 1, FobDeliveries, FobMaxLostLoads),
            true);
        Expect(
            failures,
            "two delivered and none lost keeps the order running for the third",
            FobOrderCanContinue(2, 0, FobDeliveries, FobMaxLostLoads),
            true);
        Expect(
            failures,
            "one delivered and two lost still keeps the order running",
            FobOrderCanContinue(1, 2, FobDeliveries, FobMaxLostLoads),
            true);
        Expect(
            failures,
            "one delivered and three lost cancels a three-load order at the loss cap",
            FobOrderCanContinue(1, FobMaxLostLoads, 3, FobMaxLostLoads),
            false);
        Expect(
            failures,
            "a single-load order is complete on its first delivery",
            FobReadyToBuild(1, FobDeliveries),
            true);
        Expect(
            failures,
            "a completed order is never cancelled by a late loss",
            FobOrderCanContinue(FobDeliveries, 1, FobDeliveries, FobMaxLostLoads),
            true);
        Expect(
            failures,
            "a base closed to transports is closed a second before the block ends",
            CommanderSupplyHeliService.LaunchBlockActive(599f, 600f),
            true);
        Expect(
            failures,
            "a base is open again exactly when the block ends",
            CommanderSupplyHeliService.LaunchBlockActive(600f, 600f),
            false);

        // The stale-order valve: a half-delivered order does not hold the single slot for ever.
        Expect(
            failures,
            "an order exactly on the timeout is not yet stale",
            FobOrderHasStalled(FobOrderTimeoutSeconds, FobOrderTimeoutSeconds),
            false);
        Expect(
            failures,
            "an order past the timeout is abandoned",
            FobOrderHasStalled(FobOrderTimeoutSeconds + 1f, FobOrderTimeoutSeconds),
            true);
        Expect(
            failures,
            "a disabled timeout never abandons an order",
            FobOrderHasStalled(float.MaxValue, 0f),
            false);

        // The lift purpose (design.md, air-mobile-platoons_20260915 Section 2): a FOB completes on
        // its own load count and a platoon lift on its own, through one rule.
        Expect(
            failures,
            "a single-load FOB order is complete on its first delivery",
            LiftComplete(1, FobDeliveries, CommanderLiftPurpose.FobConstruction),
            true);
        Expect(
            failures,
            "a three-load platoon lift is complete on its third",
            LiftComplete(3, 3, CommanderLiftPurpose.Platoon),
            true);
        Expect(
            failures,
            "a platoon lift two loads in is not complete",
            LiftComplete(2, 3, CommanderLiftPurpose.Platoon),
            false);
        Expect(
            failures,
            "completion asks the same question of both purposes",
            LiftComplete(2, 3, CommanderLiftPurpose.Platoon),
            FobReadyToBuild(2, 3));

        // Vehicles into loads: a platoon load is two vehicles, and a short load is not a whole one.
        Expect(failures, "two vehicles of a two-vehicle load are one load", LoadsLanded(2, 2), 1);
        Expect(failures, "three vehicles are still only one whole load", LoadsLanded(3, 2), 1);
        Expect(failures, "four vehicles are two loads", LoadsLanded(4, 2), 2);
        Expect(failures, "one vehicle of a pair is no load yet", LoadsLanded(1, 2), 0);
        Expect(failures, "a one-vehicle load counts the vehicles themselves", LoadsLanded(3, 1), 3);
        Expect(failures, "a negative count never becomes a load", LoadsLanded(-4, 2), 0);
        Expect(
            failures,
            "the platoon a lift delivers is exactly the air-mobile recipe",
            CommanderSettings.LiftLoadsPerPlatoon * LiftVehiclesPerLoad,
            AirMobileRecipeCarrier + AirMobileRecipeAirDefence);

        // The lift's money gate (design.md, air-mobile-platoons_20260915 Section 2).
        Expect(failures, "twice the price in the bank launches the lift", LiftAffordable(100f, 40f, 2f), true);
        Expect(failures, "under twice the price the lift waits", LiftAffordable(70f, 40f, 2f), false);
        Expect(failures, "exactly twice the price is enough", LiftAffordable(80f, 40f, 2f), true);
        Expect(
            failures,
            "at a multiple of one the lift gate is the FOB order's own balance test",
            LiftAffordable(100f, 100f, 1f),
            FobAffordable(100f, 100f, 100f));
        Expect(failures, "a priceless lift is never launched", LiftAffordable(9000f, 0f, 2f), false);
        Expect(
            failures,
            "a lift nobody can price is never launched",
            LiftAffordable(float.MaxValue, float.MaxValue, 2f),
            false);
        Expect(
            failures,
            "a multiple below one never lets a lift fly on credit",
            LiftAffordable(39f, 40f, 0f),
            false);

        // The gate is asked again at EVERY launch, not once at the order (fix, 2026-09-15): a
        // balance that covered the first load of three does not license the second and third.
        Expect(
            failures,
            "a balance that covers one load but not twice its price holds the next load",
            LiftAffordable(45f, 40f, 2f),
            false);
        Expect(
            failures,
            "the launch gate and the order gate are the same rule at the same numbers",
            LiftAffordable(45f, 40f, 2f),
            LiftAffordable(45f, 40f, 2f));

        // A top-up lift's size (fix, 2026-09-15): an air-mobile platoon could not be reinforced at
        // all, because the pool top-up skips it and its light vehicles are bought as cargo.
        Expect(failures, "a platoon at full strength wants no top-up lift", LiftLoadsForShortfall(0, 2), 0);
        Expect(failures, "one vehicle short still takes a whole load", LiftLoadsForShortfall(1, 2), 1);
        Expect(failures, "two vehicles short take one load", LiftLoadsForShortfall(2, 2), 1);
        Expect(failures, "three vehicles short take two loads", LiftLoadsForShortfall(3, 2), 2);
        Expect(
            failures,
            "a whole platoon short takes the standing lift",
            LiftLoadsForShortfall(AirMobileRecipeCarrier + AirMobileRecipeAirDefence, LiftVehiclesPerLoad),
            CommanderSettings.LiftLoadsPerPlatoon);
        Expect(failures, "a negative shortfall never orders a lift", LiftLoadsForShortfall(-3, 2), 0);
        Expect(failures, "a one-vehicle load carries one vehicle a load", LiftLoadsForShortfall(5, 1), 5);

        // Which cooldown a cancelled order takes (fix, 2026-09-15): a lift turned back from a point
        // was stopping a forward operating base being built there.
        Expect(
            failures,
            "a cancelled construction order stamps the FOB site cooldown",
            LiftTakesFobCooldown(CommanderLiftPurpose.FobConstruction),
            true);
        Expect(
            failures,
            "a cancelled platoon lift never stamps the FOB site cooldown",
            LiftTakesFobCooldown(CommanderLiftPurpose.Platoon),
            false);

        // When a lift may still be called off for want of an objective (fix, 2026-09-15): before its
        // first load leaves the deck, and never after. Every lift of the live match was recalled with
        // its cargo aboard, and not one landed.
        Expect(
            failures,
            "a lift that has not launched is called off when its objective stops wanting a platoon",
            LiftCancelsForWant(0, false),
            true);
        Expect(
            failures,
            "a lift with a load in the air is never called off for want",
            LiftCancelsForWant(1, false),
            false);
        Expect(
            failures,
            "a lift two loads in is never called off for want either",
            LiftCancelsForWant(2, false),
            false);
        Expect(
            failures,
            "a lift whose objective still wants a platoon is never called off for want",
            LiftCancelsForWant(0, true),
            false);
        Expect(
            failures,
            "a launched lift whose objective still wants a platoon keeps flying",
            LiftCancelsForWant(3, true),
            false);

        // The standoff an open lift is re-asked every review (user decision 2026-09-15): an order is
        // a decision about ground, and the ground changes while the loads are in the air.
        Expect(
            failures,
            "a landing zone with nothing tracked near it is clear",
            LiftLandingStillClear(float.MaxValue, 10000f),
            true);
        Expect(
            failures,
            "a column twelve kilometres out leaves the landing zone alone",
            LiftLandingStillClear(12000f, 10000f),
            true);
        Expect(
            failures,
            "a column six kilometres out moves the landing zone",
            LiftLandingStillClear(6000f, 10000f),
            false);
        Expect(
            failures,
            "a column exactly on the standoff is too close",
            LiftLandingStillClear(10000f, 10000f),
            false);
        Expect(
            failures,
            "one metre past the standoff is far enough",
            LiftLandingStillClear(10001f, 10000f),
            true);
        Expect(
            failures,
            "the lift's abort standoff is at least the picket's own, or an escorted lift would land where a lone flight may not; check the Operations section of the config",
            CommanderSettings.LiftAbortStandoffMeters > 0f,
            true);
        Expect(
            failures,
            "a FOB flight is turned off its site no sooner than a lift is, and no later than the site picker would have refused the ground; check the Operations section of the config",
            CommanderSettings.FobAbortStandoffMeters >= CommanderSettings.LiftAbortStandoffMeters
                && CommanderSettings.FobAbortStandoffMeters <= CommanderSettings.FobEnemyStandoffMeters,
            true);

        // The picket flight's own landing-zone recall, the same rule one standoff along.
        Expect(
            failures,
            "a picket landing zone with a column on it turns the flight round",
            InsertionLandingClear(9000f, 25000f),
            false);
        Expect(
            failures,
            "a picket landing zone well clear of anything spotted is landed on",
            InsertionLandingClear(40000f, 25000f),
            true);
        Expect(
            failures,
            "a picket landing zone exactly on the standoff is too close",
            InsertionLandingClear(25000f, 25000f),
            false);
        Expect(
            failures,
            "the in-flight landing test agrees with the launch gate at the same distance",
            InsertionLandingClear(30000f, 25000f),
            InsertionStandoffClear(false, 30000f, 25000f));

        // The forward landing zone (lead decision 2026-09-15): where a lift puts a platoon down when
        // the enemy is standing on its objective.
        Expect(
            failures,
            "the nearer landing zone wins",
            LiftLandingBeats(false, 4000f, false, 6000f),
            true);
        Expect(failures, "a farther landing zone loses", LiftLandingBeats(false, 6000f, false, 4000f), false);
        Expect(
            failures,
            "at the same distance a point we hold beats one nobody holds",
            LiftLandingBeats(true, 4000f, false, 4000f),
            true);
        Expect(
            failures,
            "at the same distance a neutral point does not displace one we hold",
            LiftLandingBeats(false, 4000f, true, 4000f),
            false);
        Expect(
            failures,
            "two identical candidates keep the one already chosen",
            LiftLandingBeats(true, 4000f, true, 4000f),
            false);
        Expect(
            failures,
            "a near neutral point beats a far one we hold",
            LiftLandingBeats(false, 1000f, true, 9000f),
            true);

        List<bool> landingOwn = new() { false, true, false };
        List<float> landingMeters = new() { 9000f, 6000f, 2000f };
        Expect(
            failures,
            "the lift lands on the nearest ground inside the radius",
            LiftLandingIndex(landingOwn, landingMeters, 10000f),
            2);
        Expect(
            failures,
            "a radius that excludes every candidate lands nowhere",
            LiftLandingIndex(landingOwn, landingMeters, 1000f),
            -1);
        Expect(
            failures,
            "a tighter radius still takes the best candidate left inside it",
            LiftLandingIndex(landingOwn, landingMeters, 7000f),
            2);
        Expect(
            failures,
            "a candidate exactly on the radius is inside it",
            LiftLandingIndex(new List<bool> { false }, new List<float> { 10000f }, 10000f),
            0);
        Expect(
            failures,
            "a candidate one metre past the radius is not",
            LiftLandingIndex(new List<bool> { false }, new List<float> { 10001f }, 10000f),
            -1);
        Expect(
            failures,
            "nothing inside the radius means no landing zone, and the platoon drives",
            LiftLandingIndex(new List<bool>(), new List<float>(), 10000f),
            -1);
        Expect(
            failures,
            "ownership decides only between candidates the same distance out",
            LiftLandingIndex(new List<bool> { true, false }, new List<float> { 5000f, 5000f }, 10000f),
            0);
        // The last leg has to be short next to the drive the lift replaced, or the lift has only
        // moved the problem. Half the drive the gate refuses is the line: at the shipped numbers the
        // gate refuses a drive over 10 min (about 9 km) and the last leg is at most 10 km, which is a
        // few minutes against the twenty-plus the whole drive would have been.
        Expect(
            failures,
            "the last leg is at most one drive-threshold's worth of ground twice over; check the Operations section of the config",
            CommanderSettings.LiftAirheadMaxMeters
                <= 2f * CommanderSettings.AirMobileDriveMinutes * 60f * CommanderSettings.GroundSpeedMetersPerSecond,
            true);
        Expect(
            failures,
            "the landing radius is positive, or no forward landing zone could ever be found; check the Operations section of the config",
            CommanderSettings.LiftAirheadMaxMeters > 0f,
            true);

        // The lift cover's own identity, read by the forward-base patrol walk so one point is never
        // given two patrols (fix, 2026-09-15).
        Expect(
            failures,
            "a lift cover is recognised as the cover over its own point",
            IsLiftCoverFor(LiftCoverLabel("HILLTOP 9"), "HILLTOP 9"),
            true);
        Expect(
            failures,
            "a lift cover over one point does not cover another",
            IsLiftCoverFor(LiftCoverLabel("HILLTOP 9"), "CROSSROADS 4"),
            false);
        Expect(failures, "any lift cover label is a lift cover", IsLiftCoverLabel(LiftCoverLabel("HILLTOP 9")), true);
        Expect(failures, "an ordinary patrol label is not a lift cover", IsLiftCoverLabel("CAP HILLTOP 9"), false);
        Expect(
            failures,
            "an ordinary patrol label is not a lift cover",
            IsLiftCoverFor("HILLTOP 9", "HILLTOP 9"),
            false);

        // The lift cover's launch gate (design.md, air-mobile-platoons_20260915 Section 3; user
        // decision 3, 2026-09-15). Both purposes go through it: a construction flight waits for its
        // escort exactly as a platoon lift does, which is the whole of Task 7 of that track.
        Expect(
            failures,
            "a platoon lift with its escort up and ahead and the sweep in goes",
            LiftMayLaunch(2, 2, 2, false, 5f, 180f),
            true);
        Expect(
            failures,
            "a construction flight with its escort up and ahead and no sweep wanted goes",
            LiftMayLaunch(2, 2, 2, false, 0f, 180f),
            true);
        Expect(
            failures,
            "a lift one fighter short of its escort holds",
            LiftMayLaunch(1, 1, 2, false, 5f, 180f),
            false);
        Expect(
            failures,
            "a lift with no escort at all holds",
            LiftMayLaunch(0, 0, 2, false, 5f, 180f),
            false);
        Expect(
            failures,
            "a lift whose escort is up and ahead still waits for the sweep",
            LiftMayLaunch(2, 2, 2, true, 5f, 180f),
            false);
        Expect(
            failures,
            "the sweep wait is bounded by the same package clock every other package holds on",
            LiftMayLaunch(2, 2, 2, true, 180f, 180f),
            true);
        Expect(
            failures,
            "a lift short of its escort goes once the clock runs out",
            LiftMayLaunch(0, 0, 2, false, 180f, 180f),
            true);
        Expect(
            failures,
            "a second short of the clock it still holds",
            LiftMayLaunch(0, 0, 2, false, 179f, 180f),
            false);
        Expect(
            failures,
            "nothing held yet is not on the clock",
            LiftMayLaunch(0, 0, 2, false, -1f, 180f),
            false);
        Expect(
            failures,
            "a lift that wants no escort at all is never held by one",
            LiftMayLaunch(0, 0, 0, false, -1f, 180f),
            true);
        Expect(
            failures,
            "the lift gate holds and releases on the same clock the rest of the wing forms up on",
            LiftMayLaunch(0, 0, 2, false, CommanderSettings.PackageFormUpSeconds, CommanderSettings.PackageFormUpSeconds),
            PackageGoesIn(0, 2, 0, 2, false, CommanderSettings.PackageFormUpSeconds, CommanderSettings.PackageFormUpSeconds));

        // The escorts-are-ahead half of the launch gate (user instruction, 2026-09-17: "air
        // insertions should wait for their escorts to be ahead of them"). The whole escort element
        // has to be in front, and the SAME bounded wait releases this test and the escorts-are-up
        // test together — there is one clock on a lift, not two.
        Expect(
            failures,
            "a lift whose escort is airborne but still behind it holds",
            LiftMayLaunch(2, 0, 2, false, 5f, 180f),
            false);
        Expect(
            failures,
            "a lift with only half its escort ahead of it holds",
            LiftMayLaunch(2, 1, 2, false, 5f, 180f),
            false);
        Expect(
            failures,
            "a lift whose escort never gets ahead goes once the same clock runs out",
            LiftMayLaunch(2, 0, 2, false, 180f, 180f),
            true);
        Expect(
            failures,
            "an escort exactly the lead nearer the landing zone is ahead, the inclusive boundary the rest of the mod uses",
            EscortIsAhead(true, 18_000f, 20_000f, 2000f),
            true);
        Expect(
            failures,
            "an escort a metre short of the lead is not yet ahead",
            EscortIsAhead(true, 18_001f, 20_000f, 2000f),
            false);
        Expect(
            failures,
            "an escort level with the load is not ahead of it",
            EscortIsAhead(true, 20_000f, 20_000f, 2000f),
            false);
        Expect(
            failures,
            "an escort that has fallen back behind the load is not ahead of it",
            EscortIsAhead(true, 26_000f, 20_000f, 2000f),
            false);
        Expect(
            failures,
            "an escort still on the deck is never ahead, however near the landing zone its base is",
            EscortIsAhead(false, 1000f, 20_000f, 2000f),
            false);
        Expect(
            failures,
            "an escort from a base further out than the load's is behind until it has flown the gap",
            EscortIsAhead(true, 30_000f, 20_000f, 2000f),
            false);
        Expect(
            failures,
            "a zero lead asks only that the escort is in front, never behind",
            EscortIsAhead(true, 20_000f, 20_000f, 0f),
            true);
        Expect(
            failures,
            "the lead is never negative, or a load could launch ahead of its own escort; check the Operations section of the config",
            CommanderSettings.LiftEscortLeadMeters >= 0f,
            true);
        Expect(
            failures,
            "the lead is shorter than the ring the escort is guarding, or the escort would have to be clear past the danger before the load is let go; check the Operations section of the config",
            CommanderSettings.LiftEscortLeadMeters < InsertionThreatRadiusMeters,
            true);
        Expect(
            failures,
            "the escort floor is at least a pair, or a lone fighter is the first thing lost",
            CommanderSettings.LiftEscortMinimum >= TransportEscortMinimum,
            true);
        Expect(
            failures,
            "a quiet sky leaves the cover on its floor",
            StrikeEscortWanted(CommanderSettings.LiftEscortMinimum, 0, 20),
            CommanderSettings.LiftEscortMinimum);
        Expect(
            failures,
            "four raiders over the landing zone buy four escorts",
            StrikeEscortWanted(CommanderSettings.LiftEscortMinimum, 4, 20),
            4);
        Expect(
            failures,
            "a lift cover is named after the ground it covers",
            LiftCoverLabel("HILLTOP 9"),
            "LIFT ESCORT HILLTOP 9");

        CheckLiftWave(failures);

        // The label the review line, the markers and the log all read.
        Expect(
            failures,
            "a construction lift is named after its site",
            LiftPurposeLabel(CommanderLiftPurpose.FobConstruction, "HILLTOP 9", string.Empty),
            "FOB HILLTOP 9");
        Expect(
            failures,
            "a platoon lift is named after the platoon it is raising",
            LiftPurposeLabel(CommanderLiftPurpose.Platoon, "HILLTOP 9", "3RD PLATOON"),
            "3RD PLATOON");
        Expect(
            failures,
            "a platoon lift with no name yet still names its site",
            LiftPurposeLabel(CommanderLiftPurpose.Platoon, "HILLTOP 9", string.Empty),
            "FOB HILLTOP 9");

        // Teardown: all three buildings, not two.
        Expect(failures, "a FOB with one building standing is not torn down", FobShouldTearDown(1), false);
        Expect(failures, "a FOB with two buildings standing is not torn down", FobShouldTearDown(2), false);
        Expect(failures, "a FOB with no buildings left is torn down", FobShouldTearDown(0), true);

        // Marker text (design Section 5). Every case carries the purpose, because a marker that says
        // FOB whatever the lift is for is what the player read as a forward base with three
        // deliveries (user report 2026-09-16).
        Expect(
            failures,
            "an air delivery marker counts loads delivered",
            FobMarkerText(
                CommanderLiftPurpose.FobConstruction, "HILLTOP 4", string.Empty,
                CommanderFobPhase.Delivering, 2, 3, true, false),
            "FOB HILLTOP 4 — 2/3 delivered");
        Expect(
            failures,
            "a road delivery marker counts the convoy in",
            FobMarkerText(
                CommanderLiftPurpose.FobConstruction, "HILLTOP 4", string.Empty,
                CommanderFobPhase.Delivering, 2, 3, false, false),
            "FOB HILLTOP 4 — convoy 2/3 arrived");
        Expect(
            failures,
            "a FOB under construction says so",
            FobMarkerText(
                CommanderLiftPurpose.FobConstruction, "HILLTOP 4", string.Empty,
                CommanderFobPhase.Building, 3, 3, true, false),
            "FOB HILLTOP 4 — building");
        Expect(
            failures,
            "a finished FOB says online",
            FobMarkerText(
                CommanderLiftPurpose.FobConstruction, "HILLTOP 4", string.Empty,
                CommanderFobPhase.Online, 3, 3, false, false),
            "FOB HILLTOP 4 — online");
        Expect(
            failures,
            "a FOB announced for demolition says abandoning",
            FobMarkerText(
                CommanderLiftPurpose.FobConstruction, "HILLTOP 4", string.Empty,
                CommanderFobPhase.Abandoning, 3, 3, false, false),
            "FOB HILLTOP 4 — abandoning");

        // A platoon lift is NOT a forward base: it is named after the platoon it is raising and its
        // loads are the platoon's, not a base's (the marker read "FOB CROSSROADS 1 — 0/3 delivered"
        // while the log for the same order read "lift for 2ND PLATOON diverts to CROSSROADS 1").
        Expect(
            failures,
            "a platoon lift marker is named after the platoon, not the ground",
            FobMarkerText(
                CommanderLiftPurpose.Platoon, "CROSSROADS 1", "2ND PLATOON",
                CommanderFobPhase.Delivering, 0, 3, true, false),
            "2ND PLATOON — 0/3 delivered");
        Expect(
            failures,
            "a platoon lift by road is a platoon convoy, not a FOB convoy",
            FobMarkerText(
                CommanderLiftPurpose.Platoon, "CROSSROADS 1", "2ND PLATOON",
                CommanderFobPhase.Delivering, 1, 3, false, false),
            "2ND PLATOON — convoy 1/3 arrived");
        Expect(
            failures,
            "a platoon lift with no name yet falls back to its landing zone",
            FobMarkerText(
                CommanderLiftPurpose.Platoon, "CROSSROADS 1", string.Empty,
                CommanderFobPhase.Delivering, 0, 3, true, false),
            "FOB CROSSROADS 1 — 0/3 delivered");

        // The "no safe route" hold: the order is alive and its loads have been recalled, which must
        // not read as a delivery in progress (user 2026-09-16, markers must follow a retask).
        Expect(
            failures,
            "an order holding for a clear route says so instead of looking live",
            FobMarkerText(
                CommanderLiftPurpose.FobConstruction, "HILLTOP 4", string.Empty,
                CommanderFobPhase.Delivering, 0, 1, true, true),
            "FOB HILLTOP 4 — 0/1 delivered · holding for a clear route");
        Expect(
            failures,
            "a platoon lift holding for a clear route says so under its own name",
            FobMarkerText(
                CommanderLiftPurpose.Platoon, "CROSSROADS 1", "2ND PLATOON",
                CommanderFobPhase.Delivering, 1, 3, true, true),
            "2ND PLATOON — 1/3 delivered · holding for a clear route");
        Expect(
            failures,
            "a base already up is never drawn as holding",
            FobMarkerText(
                CommanderLiftPurpose.FobConstruction, "HILLTOP 4", string.Empty,
                CommanderFobPhase.Building, 1, 1, true, true),
            "FOB HILLTOP 4 — building");

        // One truck of a road convoy, named by the same label as the site it is driving to.
        Expect(
            failures,
            "a construction convoy truck carries the FOB's name and its load number",
            LiftConvoyMarkerText(CommanderLiftPurpose.FobConstruction, "HILLTOP 4", string.Empty, 2, 3),
            "FOB HILLTOP 4 CONVOY — 2/3");
        Expect(
            failures,
            "a platoon convoy truck carries the platoon's name",
            LiftConvoyMarkerText(CommanderLiftPurpose.Platoon, "CROSSROADS 1", "2ND PLATOON", 1, 3),
            "2ND PLATOON CONVOY — 1/3");
    }
}
