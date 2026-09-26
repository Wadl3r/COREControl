using System.Collections.Generic;
using UnityEngine;

namespace GroundControlRts;

/// <summary>
/// The pool every AI-commanded HQ's ground vehicles are claimed into (design SS1: "the mod owns
/// every vehicle an AI commander buys"), and the order book that tells the buyer what to spend on
/// next (design SS4).
/// </summary>
internal sealed partial class CommanderOperationsService
{
    /// <summary>Number of <see cref="CommanderPlatoonRole"/> values — the width of a
    /// totals-by-role array.</summary>
    internal const int RoleCount = 5;

    /// <summary>A requisition unfilled this long proceeds with what it has, or dissolves if it has
    /// nothing (design SS4: 5 minutes).</summary>
    private const float RequisitionTimeoutSeconds = 300f;

    /// <summary>
    /// One open order line: <paramref name="Role"/> wants <paramref name="Wanted"/> vehicles and has
    /// <paramref name="Filled"/> so far, opened at <paramref name="OpenedAt"/> for the timeout, on
    /// behalf of <paramref name="Mission"/> (null for a synthetic self-check entry, or for
    /// <see cref="PostWithdrawingRequisitions"/>'s aggregate line — a withdrawing platoon has no
    /// mission of its own to key the line on; every withdrawing platoon's shortfall for a role
    /// collapses into that one null-mission line per role, which is fine since the order book only
    /// ever sums totals by role).
    /// </summary>
    internal struct CommanderRequisition
    {
        /// <summary>The role this line wants.</summary>
        internal CommanderPlatoonRole Role;

        /// <summary>Vehicles of that role this line still wants in total.</summary>
        internal int Wanted;

        /// <summary>Vehicles claimed against this line so far.</summary>
        internal int Filled;

        /// <summary>Scaled <c>Time.time</c> this line opened, for <see cref="RequisitionTimeoutSeconds"/>.</summary>
        internal float OpenedAt;

        /// <summary>The mission this line serves.</summary>
        internal CommanderOperationsMission? Mission;

        /// <summary>
        /// Whether this line RAISES A NEW FORCE — a platoon the mission is still waiting to form, a
        /// picket with nothing on the ground yet, the standing reserve — rather than replacing
        /// losses in one the commander already has (user decision 2026-09-14, "existing forces
        /// first"). A mission may hold one line of each per role: a forward base with two platoons
        /// at four of six and a third still to form is asking for both at once, and they are not
        /// the same priority.
        /// </summary>
        internal bool ForNewForce;
    }

    private readonly List<FactionHQ> staleHqs = new();

    /// <summary>Scratch for <see cref="PruneStagingPosts"/>, so the prune never allocates.</summary>
    private readonly List<Unit> stalePoolPosts = new();

    /// <summary>
    /// The one-line forwarder shape (<c>AirCommand/CommanderAirCommandPilotHooks.cs:12-15</c>),
    /// wired into the shared <c>FactionHQ.RegisterFactionUnit</c> postfix (ledger row 5).
    /// </summary>
    internal static void NotifyFactionUnitRegistered(FactionHQ hq, Unit unit)
    {
        Instance?.TryClaim(hq, unit);
    }

    /// <summary>
    /// Claims a freshly registered ground vehicle into the free pool, for an HQ this service
    /// already manages. "Already manages" (<paramref name="hq"/> already has a state) is the gate
    /// rather than re-testing <c>IsCommanded</c>: a state exists only once the point list is
    /// non-empty (see <see cref="OwnsGroundForce"/>), so a vehicle that registers before discovery
    /// finishes is left for the home guard and the expansion drive, exactly what departures 6 and 8
    /// ask for.
    /// </summary>
    private void TryClaim(FactionHQ hq, Unit unit)
    {
        if (!states.TryGetValue(hq, out OperationsState state)
            || !hq.IsServer
            || unit == null
            || unit.disabled
            || unit is not GroundVehicle
            || unit.definition is not VehicleDefinition definition
            || definition.vehicleType == VehicleType.RDR
            || CommanderMoveService.Instance?.HasPlayerOrder(unit) == true)
        {
            return;
        }

        if (!IsClaimedVehicle(state, unit))
        {
            state.Pool.Add(unit);
            LogClaim(hq, unit, state.Pool.Count);
            FillOldestRequisition(hq, state, CommanderPlatoonRoles.Of(definition));
        }
    }

    /// <summary>
    /// The oldest open order-book line for <paramref name="role"/> (design SS4: "the oldest matching
    /// open requisition"), by index, or -1 when the book has none. One definition with two callers
    /// (Reuse rule 5): the claim below credits a delivered vehicle against this line, and
    /// <see cref="TryGetOldestRequisitionObjective"/> asks where the SAME line's vehicle is wanted,
    /// so the vehicle is spawned at the depot nearest the place it is about to be sent.
    /// </summary>
    private static int FindOldestRequisition(OperationsState state, CommanderPlatoonRole role)
    {
        int oldestIndex = -1;
        float oldestTime = float.MaxValue;
        for (int i = 0; i < state.Requisitions.Count; i++)
        {
            CommanderRequisition requisition = state.Requisitions[i];
            if (requisition.Role == role && requisition.Filled < requisition.Wanted && requisition.OpenedAt < oldestTime)
            {
                oldestTime = requisition.OpenedAt;
                oldestIndex = i;
            }
        }

        return oldestIndex;
    }

    /// <summary>
    /// Where the vehicle the commander is about to buy for <paramref name="role"/> is actually
    /// wanted (reach-and-points Section 4): the objective of the oldest open line for that role.
    /// False when the book holds no open line for the role, or when the line it holds is the
    /// standing reserve, which has no objective of its own — the buyer falls back to the territory
    /// centre in both cases, which is where the reserve forms anyway.
    /// </summary>
    internal static bool TryGetOldestRequisitionObjective(
        FactionHQ hq, CommanderPlatoonRole role, out GlobalPosition objective)
    {
        objective = default;
        CommanderOperationsService? service = Instance;
        if (service == null || !service.states.TryGetValue(hq, out OperationsState state))
        {
            return false;
        }

        int index = FindOldestRequisition(state, role);
        if (index < 0)
        {
            return false;
        }

        CommanderRequisition line = state.Requisitions[index];
        CommanderStrategicPoint? point = line.Mission?.Point;
        if (point == null && line.Mission == null && role == CommanderPlatoonRole.Truck)
        {
            // The mission-less truck line is the FOB convoy's (PostFobConvoyRequisitions): its
            // truck is wanted at the FOB's point.
            for (int i = 0; i < state.FobOrders.Count; i++)
            {
                if (state.FobOrders[i].Phase == CommanderFobPhase.Delivering && !state.FobOrders[i].ByAir)
                {
                    point = state.FobOrders[i].Point;
                    break;
                }
            }
        }

        if (point == null)
        {
            return false;
        }

        objective = point.Position;
        return true;
    }

    /// <summary>Credits a freshly claimed vehicle's role against the oldest open requisition that
    /// wants it (design SS4: "the oldest matching open requisition"), notifying the mission once
    /// the line closes. Bookkeeping only — which vehicle actually ends up in which platoon is still
    /// the recipe fill's job.</summary>
    private static void FillOldestRequisition(FactionHQ hq, OperationsState state, CommanderPlatoonRole role)
    {
        int oldestIndex = FindOldestRequisition(state, role);
        if (oldestIndex < 0)
        {
            return;
        }

        CommanderRequisition found = state.Requisitions[oldestIndex];
        found.Filled++;
        state.Requisitions[oldestIndex] = found;
        if (found.Filled >= found.Wanted && found.Mission != null)
        {
            CommanderAiLog.Note(hq, $"{found.Mission.Label}: requisition filled.");
        }
    }

    /// <summary>
    /// Ground truth for the pool, run at the top of every review: walks every ground vehicle the
    /// faction owns and adds anything the claim hook missed — chiefly the vehicles a mission was
    /// authored with, which register before this service has a state for their HQ — then drops
    /// pool and platoon members that are gone, disabled, changed faction, or under a player order
    /// (the <c>PruneDefenders</c> filter, <c>Ai/CommanderEnemyCommanderDefence.cs:297-320</c>,
    /// including its point about leaving <c>commandedDestination</c> alone for a vehicle the player
    /// has taken back).
    /// </summary>
    private void SweepPool(FactionHQ hq, OperationsState state)
    {
        if (hq.factionUnits != null)
        {
            foreach (PersistentID id in hq.factionUnits)
            {
                if (id.TryGetUnit(out Unit unit)
                    && unit != null
                    && !unit.disabled
                    && unit is GroundVehicle
                    && unit.definition is VehicleDefinition definition
                    && definition.vehicleType != VehicleType.RDR
                    && CommanderMoveService.Instance?.HasPlayerOrder(unit) != true
                    && !IsClaimedVehicle(state, unit))
                {
                    state.Pool.Add(unit);
                }
            }
        }

        state.Pool.RemoveAll(unit => IsStalePoolUnit(hq, unit));
        for (int i = 0; i < state.Platoons.Count; i++)
        {
            // Before the removal, not after: a dead member is only visible to the loss stamp while
            // it still sits in the list. The stamp is what makes a holding platoon that lost a
            // vehicle count as in contact (addendum 2026-09-14 §2).
            if (HasCombatLoss(hq, state.Platoons[i].Members))
            {
                state.Platoons[i].LastLossAt = Time.time;
            }

            state.Platoons[i].Members.RemoveAll(unit => IsStalePoolUnit(hq, unit));
        }

        PruneStagingPosts(state);
    }

    /// <summary>Drops the staging post (<c>StagePool</c>) AND the idle stamp (<c>PoolIdleSince</c>)
    /// of every vehicle that has left the pool — taken into a platoon, a picket or a truck slot, or
    /// swept out as stale — so the record does not keep a dead unit alive for the rest of the match,
    /// and so a vehicle that later comes back (a dissolved platoon, a demoted forward base, a
    /// recalled convoy) starts a fresh idle clock. The stamp used to be removed only on sale, so a
    /// vehicle returning after an hour in a platoon carried its hour-old stamp and was sold in the
    /// same review (fix, 2026-09-15: three quarters of the vehicles bought were sold unused).
    /// This is the one site for both prunes rather than a call at each of the five places a vehicle
    /// leaves the pool: it runs twice per review already (the sweep at the top and again after the
    /// fills, before the sale), reuses one scratch list, and is behaviour-equivalent — a vehicle
    /// that leaves and returns inside one review is one that never left the pool at the top prune.</summary>
    private void PruneStagingPosts(OperationsState state)
    {
        PruneNotInPool(state.Pool, state.PoolIssued, stalePoolPosts);
        PruneNotInPool(state.Pool, state.PoolIdleSince, stalePoolPosts);
    }

    /// <summary>Removes from <paramref name="record"/> every key that is not in <paramref name="pool"/>,
    /// collecting the stale keys in <paramref name="scratch"/> first so the dictionary is never
    /// mutated while it is being walked.</summary>
    private static void PruneNotInPool<TValue>(List<Unit> pool, Dictionary<Unit, TValue> record, List<Unit> scratch)
    {
        if (record.Count == 0)
        {
            return;
        }

        scratch.Clear();
        foreach (KeyValuePair<Unit, TValue> entry in record)
        {
            if (!pool.Contains(entry.Key))
            {
                scratch.Add(entry.Key);
            }
        }

        for (int i = 0; i < scratch.Count; i++)
        {
            record.Remove(scratch[i]);
        }

        scratch.Clear();
    }

    /// <summary>Gone, disabled, changed faction, or under a player order — never re-tested against
    /// <c>commandedDestination</c>, which the player's own order now owns.</summary>
    private static bool IsStalePoolUnit(FactionHQ hq, Unit? unit)
    {
        return unit == null
            || unit.disabled
            || unit.NetworkHQ != hq
            || CommanderMoveService.Instance?.HasPlayerOrder(unit) == true;
    }

    private static bool IsPlatoonMember(OperationsState state, Unit unit)
    {
        for (int i = 0; i < state.Platoons.Count; i++)
        {
            if (state.Platoons[i].Members.Contains(unit))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// One definition of "already spoken for" (Reuse rule 4), read by <see cref="TryClaim"/> and
    /// <see cref="SweepPool"/> (so neither re-adds a vehicle already claimed) and by
    /// <see cref="IsPlatoonUnit"/> (so the home guard's recruiter and the capture squad see the same
    /// answer). B2 fix: a picket's <c>PicketMembers</c> and a forward base's <c>Truck</c> are claimed
    /// vehicles too, even though neither sits in <c>Pool</c> or in a <see cref="CommanderPlatoon"/> —
    /// missing this walk let the sweep put a picket vehicle or a parked munitions truck straight back
    /// into the free pool for a platoon fill or another mission's truck fill to take, while the
    /// mission that already claimed it kept driving it to its post.
    /// </summary>
    private static bool IsClaimedVehicle(OperationsState state, Unit unit)
    {
        if (state.Pool.Contains(unit) || IsPlatoonMember(state, unit))
        {
            return true;
        }

        for (int i = 0; i < state.Missions.Count; i++)
        {
            CommanderOperationsMission mission = state.Missions[i];
            if (ReferenceEquals(mission.Truck, unit) || mission.PicketMembers.Contains(unit))
            {
                return true;
            }
        }

        // A forward base's construction trucks on the road and its delivered loads waiting to be
        // consumed belong to the order. Unlisted here, the pool sweep took them back every review:
        // a forward base could claim the convoy truck as its own, or the pool sale could sell it,
        // and the order then counted it lost (review H13).
        for (int i = 0; i < state.FobOrders.Count; i++)
        {
            CommanderFobOrder order = state.FobOrders[i];
            if (order.Convoy.Contains(unit) || order.Arrivals.Contains(unit))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// True while <paramref name="unit"/> is claimed by any managed HQ — in a free pool, serving in
    /// a platoon, parked as a forward base's truck or standing in a picket's ring
    /// (<see cref="IsClaimedVehicle"/>). The <c>IsDefendingUnit</c> shape
    /// (<c>Ai/CommanderEnemyCommanderDefence.cs:85-102</c>), read by the home guard's recruiter and
    /// the capture squad so neither pulls a vehicle operations has already claimed.
    /// </summary>
    internal static bool IsPlatoonUnit(Unit? unit)
    {
        CommanderOperationsService? service = Instance;
        if (service == null || unit == null)
        {
            return false;
        }

        foreach (KeyValuePair<FactionHQ, OperationsState> entry in service.states)
        {
            if (IsClaimedVehicle(entry.Value, unit))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// True once this HQ has a state at all. A state is created only once
    /// <c>CommanderStrategicPointService.Instance?.Points</c> is non-empty, so before discovery
    /// finishes this is false and the home guard and the expansion drive still run untouched
    /// (departures 6 and 8).
    /// </summary>
    internal static bool OwnsGroundForce(FactionHQ hq)
    {
        return Instance != null && Instance.states.ContainsKey(hq);
    }

    /// <summary>Idle vehicles in this commander's pool — bought or produced, claimed, and waiting on
    /// the reserve ring for a purpose — or 0 when it is not managed here.</summary>
    internal static int PoolCount(FactionHQ hq)
    {
        return Instance != null && Instance.states.TryGetValue(hq, out OperationsState state)
            ? state.Pool.Count
            : 0;
    }

    /// <summary>Platoons this commander currently has, or 0 when it is not managed here.</summary>
    internal static int PlatoonCount(FactionHQ hq)
    {
        return Instance != null && Instance.states.TryGetValue(hq, out OperationsState state)
            ? state.Platoons.Count
            : 0;
    }

    /// <summary>
    /// Pure, for the self-check. True when the ground buyer must buy nothing this review: the
    /// operations service owns this commander's ground force, so every vehicle is bought to order,
    /// and the order book has no open line to buy for (DECISION-013).
    /// <para>This replaces a platoon cap that never bound. The cap lifted the moment any requisition
    /// was open and the book never emptied — the standing reserve alone kept a line open — so the
    /// plan buyer went on buying vehicles nobody had asked for, and the player side reached 27
    /// platoons spread over a map with no enemy near most of them. Counting platoons was the wrong
    /// question: what matters is whether anything asked for the vehicle. Picket shortfalls,
    /// forward-base trucks, platoon replacements, reinforcements and the standing reserve all post
    /// lines, so a commander that needs a vehicle still gets one.</para>
    /// </summary>
    internal static bool GroundBuyingBookOnly(bool ownsGroundForce, bool hasOpenRequisition)
    {
        return ownsGroundForce && !hasOpenRequisition;
    }

    /// <summary>Per-HQ state lifecycle: created once this commander is managed and the point list
    /// is ready, dropped the moment it stops being commanded (the <c>PruneStates</c> way,
    /// <c>Ai/CommanderEnemyCommanderService.cs:626-644</c>).</summary>
    private void EnsureStates(FactionHQ localHq)
    {
        IReadOnlyList<CommanderStrategicPoint>? points = CommanderStrategicPointService.Instance?.Points;
        if (points == null || points.Count == 0)
        {
            return;
        }

        foreach (FactionHQ hq in FactionRegistry.GetAllHQs())
        {
            if (hq == null
                || !CommanderPlayerCommanderService.IsCommanded(hq, localHq)
                || !hq.IsServer
                || hq.faction == null
                || states.ContainsKey(hq))
            {
                continue;
            }

            states[hq] = new OperationsState();
        }
    }

    private void PruneStates(FactionHQ localHq)
    {
        staleHqs.Clear();
        foreach (KeyValuePair<FactionHQ, OperationsState> entry in states)
        {
            if (entry.Key == null || !CommanderPlayerCommanderService.IsCommanded(entry.Key, localHq))
            {
                staleHqs.Add(entry.Key!);
            }
        }

        for (int i = 0; i < staleHqs.Count; i++)
        {
            states.Remove(staleHqs[i]);
        }
    }

    /// <summary>Zeroes and refills <paramref name="totalsByRole"/> with what each role's open lines
    /// (wanted minus filled, never negative — an over-filled line never subtracts from another
    /// role's total) add up to. Pure, for the self-check.</summary>
    internal static void SumOrderBook(IReadOnlyList<CommanderRequisition> requisitions, int[] totalsByRole)
    {
        for (int i = 0; i < totalsByRole.Length; i++)
        {
            totalsByRole[i] = 0;
        }

        for (int i = 0; i < requisitions.Count; i++)
        {
            CommanderRequisition requisition = requisitions[i];
            int role = (int)requisition.Role;
            if (role >= 0 && role < totalsByRole.Length)
            {
                totalsByRole[role] += Mathf.Max(0, requisition.Wanted - requisition.Filled);
            }
        }
    }

    /// <summary>Index of the largest open total, or -1 when every role's book is empty (which
    /// leaves the plan-based counter triangle alone). First index wins a tie, so the answer is
    /// stable across reviews — the <c>PickRichestIndex</c> convention. Pure, for the self-check.</summary>
    internal static int LargestOpenRole(IReadOnlyList<int> totalsByRole)
    {
        int best = -1;
        int bestValue = 0;
        for (int i = 0; i < totalsByRole.Count; i++)
        {
            if (totalsByRole[i] > bestValue)
            {
                bestValue = totalsByRole[i];
                best = i;
            }
        }

        return best;
    }

    /// <summary>Live wrapper the buyer reads: does this HQ have an open line on an
    /// <see cref="CommanderMissionKind.Attack"/> mission right now (design SS4: raises the buyer's
    /// spend tempo for the review)?</summary>
    internal static bool HasOpenAttackRequisition(FactionHQ hq)
    {
        if (Instance == null || !Instance.states.TryGetValue(hq, out OperationsState state))
        {
            return false;
        }

        for (int i = 0; i < state.Requisitions.Count; i++)
        {
            CommanderRequisition requisition = state.Requisitions[i];
            if (requisition.Mission?.Kind == CommanderMissionKind.Attack && requisition.Filled < requisition.Wanted)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Live wrapper the buyer reads (T10): any open line at all for this HQ, of any role, on any
    /// mission — broader than <see cref="HasOpenAttackRequisition"/>, which only asks about the
    /// spend-tempo boost. This is the order-book precedence gate in the buy loop.
    /// </summary>
    internal static bool HasOpenRequisition(FactionHQ hq)
    {
        if (Instance == null || !Instance.states.TryGetValue(hq, out OperationsState state))
        {
            return false;
        }

        for (int i = 0; i < state.Requisitions.Count; i++)
        {
            CommanderRequisition requisition = state.Requisitions[i];
            if (requisition.Filled < requisition.Wanted)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Live wrapper the buyer reads: the role with the largest open line for this HQ, or
    /// <see cref="CommanderPlatoonRole.Other"/> when the book is empty (the buyer's caller,
    /// <c>ChooseForRole</c>, only ever asks this after confirming the book is non-empty via
    /// <see cref="HasOpenAttackRequisition"/>).</summary>
    /// <summary>
    /// The roles the buyer should try this purchase, best first: an open <c>Truck</c> line always
    /// leads (a forward base without its truck runs dry, and one truck is cheap next to the vehicle
    /// lines it was losing "largest line" to), then the roles a purpose FACING THE ENEMY is waiting
    /// on, then everything else — which is where a picket's pair sits.
    /// The buyer takes the first role it can afford, and only falls back to its plan when it can
    /// afford none of them — before, one unaffordable "largest" role sent the whole purchase to the
    /// plan, which bought a cheap vehicle the recipe could not use.
    /// <para>Every vehicle ALREADY COVERING the book is subtracted per role before the order is
    /// drawn (user decision 2026-09-15, fix A): what was bought earlier this review, what sits
    /// banked as factory supply at the depots (a buy whose pad was busy, or that found no depot),
    /// and what stands idle in the pool. Only <paramref name="boughtThisReview"/> used to be
    /// subtracted, and it is cleared every review, so the same open line was bought again every
    /// 30 s, up to five a review, until the pool overflowed and the surplus was sold back at half
    /// price: three quarters of the vehicles bought in one match were sold unused a minute later.
    /// The book's own <c>Filled</c> tally is NOT subtracted here on top of that — a claimed vehicle
    /// is counted once, as a pooled one, not twice (see <see cref="SumOrderBookByUrgency"/>).</para>
    /// <para>The urgency split is the 2026-09-14 fix. Sorted by size alone, sixteen rear pickets'
    /// air-defence and carrier lines outweighed the armour a forward base in contact was asking
    /// for, so every vehicle bought went to a quiet crossroads while the front stayed one platoon
    /// deep.</para>
    /// </summary>
    /// <param name="catalog">The ground vehicle definitions this commander can buy — the buyer's
    /// own catalogue, so banked supply is read for exactly the definitions it would spend on.</param>
    internal static void OpenRolesByPriority(
        FactionHQ hq, IReadOnlyList<VehicleDefinition> catalog, int[] boughtThisReview, List<CommanderPlatoonRole> roles)
    {
        roles.Clear();
        if (Instance == null || !Instance.states.TryGetValue(hq, out OperationsState state))
        {
            return;
        }

        int[] urgent = new int[RoleCount];
        int[] other = new int[RoleCount];
        int[] newUrgent = new int[RoleCount];
        int[] newOther = new int[RoleCount];
        SumOrderBookByUrgency(state, urgent, other, newUrgent, newOther);

        int[] banked = new int[RoleCount];
        int[] pooled = new int[RoleCount];
        SumCoverage(hq, catalog, state, banked, pooled);
        for (int i = 0; i < RoleCount; i++)
        {
            state.CoveredByRole[i] = Coverage(i < boughtThisReview.Length ? boughtThisReview[i] : 0, banked[i], pooled[i]);
        }

        PayOffCoverage(urgent, other, newUrgent, newOther, state.CoveredByRole);
        OrderOpenRoles(urgent, other, newUrgent, newOther, roles);
    }

    /// <summary>
    /// What the commander already has toward one role's lines, pure: the vehicles bought earlier
    /// this review, the ones banked as depot supply and the ones idle in the pool. Each term is
    /// floored at zero so a corrupt count never turns into extra demand.
    /// </summary>
    internal static int Coverage(int boughtThisReview, int banked, int pooled)
    {
        return Mathf.Max(0, boughtThisReview) + Mathf.Max(0, banked) + Mathf.Max(0, pooled);
    }

    /// <summary>One role's open total once its <see cref="Coverage"/> is taken off, pure and never
    /// negative — the single-bucket form of <see cref="PayOffCoverage"/>, for the self-check.</summary>
    internal static int OpenAfterCoverage(int open, int boughtThisReview, int banked, int pooled)
    {
        return Mathf.Max(0, Mathf.Max(0, open) - Coverage(boughtThisReview, banked, pooled));
    }

    /// <summary>
    /// Pays each role's <paramref name="coverageByRole"/> off its four order-book buckets in the
    /// order the buyer buys them — replacements at the front, replacements at the rear, new forces
    /// at the front, new forces at the rear — for the same reason they are bought in that order:
    /// a vehicle already on hand belongs to the most urgent line still open. Pure, for the self-check.
    /// </summary>
    internal static void PayOffCoverage(
        int[] urgent, int[] other, int[] newUrgent, int[] newOther, IReadOnlyList<int> coverageByRole)
    {
        for (int i = 0; i < RoleCount && i < coverageByRole.Count; i++)
        {
            int left = Mathf.Max(0, coverageByRole[i]);
            left = PayOff(ref urgent[i], left);
            left = PayOff(ref other[i], left);
            left = PayOff(ref newUrgent[i], left);
            PayOff(ref newOther[i], left);
        }
    }

    /// <summary>Takes as much of <paramref name="bought"/> off one bucket as it can hold and
    /// returns what is left for the next bucket down.</summary>
    private static int PayOff(ref int bucket, int bought)
    {
        int paid = Mathf.Min(Mathf.Max(0, bucket), bought);
        bucket -= paid;
        return bought - paid;
    }

    /// <summary>
    /// Zeroes and fills the two live coverage arrays: <paramref name="banked"/> is the factory
    /// supply this HQ holds for each definition in <paramref name="catalog"/>, attributed to the
    /// definition's role — the vehicles a buy banked instead of spawning (the "(supply; no depot
    /// could spawn it)" and "(queued at …; its pad is busy)" branches of the buy loop), which the
    /// game's depot loop turns into vehicles later and which the pool-full hold keeps banked;
    /// <paramref name="pooled"/> is the idle pool by role. <c>GetUnitSupply</c> is a plain read, so
    /// no server guard is needed here.
    /// </summary>
    private static void SumCoverage(
        FactionHQ hq, IReadOnlyList<VehicleDefinition> catalog, OperationsState state, int[] banked, int[] pooled)
    {
        for (int i = 0; i < banked.Length; i++)
        {
            banked[i] = 0;
            pooled[i] = 0;
        }

        for (int i = 0; i < catalog.Count; i++)
        {
            VehicleDefinition definition = catalog[i];
            int role = (int)CommanderPlatoonRoles.Of(definition);
            if (definition != null && role >= 0 && role < banked.Length)
            {
                banked[role] += Mathf.Max(0, hq.GetUnitSupply(definition));
            }
        }

        for (int i = 0; i < state.Pool.Count; i++)
        {
            Unit unit = state.Pool[i];
            if (unit == null || unit.disabled)
            {
                continue;
            }

            int role = (int)CommanderPlatoonRoles.Of(unit.definition as VehicleDefinition);
            if (role >= 0 && role < pooled.Length)
            {
                pooled[role]++;
            }
        }
    }

    /// <summary>
    /// The buy order for one purchase, from the two halves of the order book: an open
    /// <c>Truck</c> line first whichever half it is in, then the urgent roles largest line first,
    /// then every other open role largest line first. Each role appears once, at its best position.
    /// Pure, for the self-check.
    /// </summary>
    internal static void OrderOpenRoles(
        IReadOnlyList<int> urgentTotals,
        IReadOnlyList<int> otherTotals,
        IReadOnlyList<int> newUrgentTotals,
        IReadOnlyList<int> newOtherTotals,
        List<CommanderPlatoonRole> roles)
    {
        roles.Clear();
        bool[] taken = new bool[RoleCount];
        if (OpenTotal(urgentTotals, CommanderPlatoonRole.Truck)
            + OpenTotal(otherTotals, CommanderPlatoonRole.Truck)
            + OpenTotal(newUrgentTotals, CommanderPlatoonRole.Truck)
            + OpenTotal(newOtherTotals, CommanderPlatoonRole.Truck) > 0)
        {
            roles.Add(CommanderPlatoonRole.Truck);
            taken[(int)CommanderPlatoonRole.Truck] = true;
        }

        // Existing forces before new ones, and within each, the threatened before the quiet (user
        // decision 2026-09-14, tiers 2 and 4 of the rung-2 spending order). The truck keeps its
        // place at the head of the whole book: a forward base without one runs dry, and it is cheap.
        AppendLargestFirst(urgentTotals, taken, roles);
        AppendLargestFirst(otherTotals, taken, roles);
        AppendLargestFirst(newUrgentTotals, taken, roles);
        AppendLargestFirst(newOtherTotals, taken, roles);
    }

    /// <summary>One role's open total out of a totals array, bounds-checked and never negative.</summary>
    private static int OpenTotal(IReadOnlyList<int> totals, CommanderPlatoonRole role)
    {
        int index = (int)role;
        return index >= 0 && index < totals.Count ? Mathf.Max(0, totals[index]) : 0;
    }

    /// <summary>Appends every role with an open line in <paramref name="totals"/>, largest first,
    /// skipping the ones an earlier pass already placed. First index wins a tie, so the answer is
    /// stable review to review — the <see cref="LargestOpenRole(IReadOnlyList{int})"/> convention.</summary>
    private static void AppendLargestFirst(IReadOnlyList<int> totals, bool[] taken, List<CommanderPlatoonRole> roles)
    {
        while (true)
        {
            int best = -1;
            for (int i = 0; i < totals.Count && i < taken.Length; i++)
            {
                if (taken[i] || totals[i] <= 0)
                {
                    continue;
                }

                if (best < 0 || totals[i] > totals[best])
                {
                    best = i;
                }
            }

            if (best < 0)
            {
                return;
            }

            roles.Add((CommanderPlatoonRole)best);
            taken[best] = true;
        }
    }

    /// <summary>
    /// Splits the order book into the lines a purpose facing the enemy is waiting on and everything
    /// else. A line with no mission of its own — the standing reserve and the platoon top-ups — is
    /// never urgent: it is what the commander would like, not what the enemy is forcing.
    /// <para>This is the BUYER's view and it sums each line's full <c>Wanted</c>, not
    /// <c>Wanted - Filled</c> as <see cref="SumOrderBook"/> does for the hold gate and the review
    /// line. <c>Filled</c> counts a vehicle the moment it registers, and from that moment the same
    /// vehicle stands in the pool, which <see cref="OpenRolesByPriority"/> now subtracts as
    /// coverage; counting it in both places under-bought every line by its pooled vehicles, and a
    /// six-vehicle platoon with five in the pool stalled at five until the line's 5 min timeout
    /// reset the tally. A vehicle is counted once, where it physically is.</para>
    /// </summary>
    private static void SumOrderBookByUrgency(
        OperationsState state, int[] urgent, int[] other, int[] newUrgent, int[] newOther)
    {
        for (int i = 0; i < urgent.Length; i++)
        {
            urgent[i] = 0;
            other[i] = 0;
            newUrgent[i] = 0;
            newOther[i] = 0;
        }

        for (int i = 0; i < state.Requisitions.Count; i++)
        {
            CommanderRequisition requisition = state.Requisitions[i];
            int role = (int)requisition.Role;
            if (role < 0 || role >= urgent.Length)
            {
                continue;
            }

            int open = Mathf.Max(0, requisition.Wanted);
            bool threatened = IsThreatenedPurpose(state, requisition.Mission);
            if (requisition.ForNewForce)
            {
                if (threatened)
                {
                    newUrgent[role] += open;
                }
                else
                {
                    newOther[role] += open;
                }
            }
            else if (threatened)
            {
                urgent[role] += open;
            }
            else
            {
                other[role] += open;
            }
        }
    }

    /// <summary>A requisition raised by a purpose facing the enemy: any attack, or a forward base on
    /// a front point under a threat mark or in contact. One definition of "facing the enemy" shared
    /// with the pool order (<c>IsThreatenedFrontPoint</c>, Reuse rule 4).</summary>
    private static bool IsThreatenedPurpose(OperationsState state, CommanderOperationsMission? mission)
    {
        if (mission == null)
        {
            return false;
        }

        return mission.Kind == CommanderMissionKind.Attack
            || (mission.Kind == CommanderMissionKind.ForwardBase && IsThreatenedFrontPoint(state, mission));
    }

    internal static CommanderPlatoonRole LargestOpenRole(FactionHQ hq)
    {
        if (Instance != null && Instance.states.TryGetValue(hq, out OperationsState state))
        {
            int[] totals = new int[RoleCount];
            SumOrderBook(state.Requisitions, totals);
            int role = LargestOpenRole(totals);
            if (role >= 0)
            {
                return (CommanderPlatoonRole)role;
            }
        }

        return CommanderPlatoonRole.Other;
    }

    /// <summary>Sets (or clears, if <paramref name="wanted"/> is zero) this mission's line for
    /// <paramref name="role"/>, keeping whatever it has already filled. <paramref name="mission"/> is
    /// null for <see cref="PostWithdrawingRequisitions"/>'s aggregate, mission-less line.</summary>
    private static void SetRequisition(
        OperationsState state,
        CommanderOperationsMission? mission,
        CommanderPlatoonRole role,
        int wanted,
        bool forNewForce = false)
    {
        for (int i = 0; i < state.Requisitions.Count; i++)
        {
            CommanderRequisition existing = state.Requisitions[i];
            if (ReferenceEquals(existing.Mission, mission)
                && existing.Role == role
                && existing.ForNewForce == forNewForce)
            {
                if (wanted <= 0)
                {
                    state.Requisitions.RemoveAt(i);
                    return;
                }

                existing.Wanted = wanted;
                state.Requisitions[i] = existing;
                return;
            }
        }

        if (wanted > 0)
        {
            state.Requisitions.Add(new CommanderRequisition
            {
                Role = role,
                Wanted = wanted,
                Filled = 0,
                OpenedAt = Time.time,
                Mission = mission,
                ForNewForce = forNewForce,
            });
        }
    }

    /// <summary>A forward base short of its recipe posts one line per empty slot — counting both
    /// its assigned platoon(s)' empty slots and any platoon it is still waiting on
    /// <see cref="CommanderOperationsMission.WantedPlatoons"/> to form — plus one <c>Truck</c> line
    /// per mission (design SS2/SS4).</summary>
    private static void PostForwardBaseRequisitions(OperationsState state, CommanderOperationsMission mission)
    {
        int recipeArmour = mission.AirMobile ? AirMobileRecipeArmour : CommanderSettings.OperationsRecipeArmour;
        int recipeCarrier = mission.AirMobile ? AirMobileRecipeCarrier : CommanderSettings.OperationsRecipeCarrier;
        int recipeAirDefence = mission.AirMobile
            ? AirMobileRecipeAirDefence
            : CommanderSettings.OperationsRecipeAirDefence;
        int wantedArmour = 0;
        int wantedCarrier = 0;
        int wantedAirDefence = 0;
        for (int i = 0; i < mission.Assigned.Count; i++)
        {
            CommanderPlatoon platoon = mission.Assigned[i];
            int haveArmour = 0;
            int haveCarrier = 0;
            int haveAirDefence = 0;
            for (int m = 0; m < platoon.Members.Count; m++)
            {
                switch (CommanderPlatoonRoles.Of(platoon.Members[m]?.definition as VehicleDefinition))
                {
                    case CommanderPlatoonRole.Armour:
                        haveArmour++;
                        break;
                    case CommanderPlatoonRole.Carrier:
                        haveCarrier++;
                        break;
                    case CommanderPlatoonRole.AirDefence:
                        haveAirDefence++;
                        break;
                }
            }

            // Capped by the slots the platoon actually has free (air-mobile-platoons_20260915): a
            // platoon at full strength with a mix the recipe would not have chosen — which is every
            // air-mobile platoon once its armour line opens, and any platoon the fill's "any combat
            // vehicle" pass topped up — used to post a line nothing could ever absorb, so the buyer
            // bought a vehicle that stood in the pool until the idle sale took it back at half price.
            // Vehicles a re-raise has handed back to a nearer depot are counted as present (user
            // instruction 2026-09-16, Operations/CommanderOperationsReseat.cs). They are already
            // bought and already paid for, and they reappear at that depot within the deployment
            // reservation window; a line posted for them would have the buyer purchase the same
            // platoon a second time while the first one was walking back onto the map.
            int free = Mathf.Max(
                0,
                platoon.Establishment - platoon.Members.Count
                    - ReseatVehiclesInTransit(platoon.Reseat, platoon.Members.Count));
            int armourShort = Mathf.Max(0, recipeArmour - haveArmour);
            int carrierShort = Mathf.Max(0, recipeCarrier - haveCarrier);
            int airDefenceShort = Mathf.Max(0, recipeAirDefence - haveAirDefence);
            wantedArmour += Mathf.Min(armourShort, free);
            wantedCarrier += Mathf.Min(carrierShort, Mathf.Max(0, free - armourShort));
            wantedAirDefence += Mathf.Min(
                airDefenceShort, Mathf.Max(0, free - armourShort - carrierShort));
        }

        // Two lines per role, not one (user decision 2026-09-14, "existing forces first"): the
        // shortfall above is the platoons this base ALREADY has, standing under strength; the
        // platoons it is still waiting to form are a new force and are bought after every
        // replacement on the book. They used to be summed into one line, so a base raising its
        // second platoon outbid another base's losses.
        // An air-mobile base forms no platoon at a depot at all (air-mobile-platoons_20260915
        // Section 2) — its vehicles are bought as cargo by the lift — so it posts no new-force line.
        int unformed = mission.AirMobile
            ? 0
            : Mathf.Max(0, mission.WantedPlatoons - mission.Assigned.Count);
        bool groundLinesOpen = GroundLineAllowed(
            mission.AirMobile, mission.DriveMinutesToPoint, CommanderSettings.AirMobileDriveMinutes);
        SetRequisition(
            state, mission, CommanderPlatoonRole.Armour,
            AirMobileLineWanted(mission.AirMobile, CommanderPlatoonRole.Armour, groundLinesOpen, wantedArmour));
        SetRequisition(
            state, mission, CommanderPlatoonRole.Carrier,
            AirMobileLineWanted(mission.AirMobile, CommanderPlatoonRole.Carrier, groundLinesOpen, wantedCarrier));
        SetRequisition(
            state, mission, CommanderPlatoonRole.AirDefence,
            AirMobileLineWanted(
                mission.AirMobile, CommanderPlatoonRole.AirDefence, groundLinesOpen, wantedAirDefence));
        SetRequisition(
            state, mission, CommanderPlatoonRole.Armour,
            unformed * recipeArmour, forNewForce: true);
        SetRequisition(
            state, mission, CommanderPlatoonRole.Carrier,
            unformed * recipeCarrier, forNewForce: true);
        SetRequisition(
            state, mission, CommanderPlatoonRole.AirDefence,
            unformed * recipeAirDefence, forNewForce: true);
        // Wanted 0 once the mission has a truck clears the line outright (SetRequisition's own
        // zero-wanted branch), rather than leaving it open forever. Previously this posted `1`
        // unconditionally every review, so a truck parked by FillMissionTrucks straight from the pool
        // (never routed through FillOldestRequisition, which is the only place that increments
        // `Filled`) left the line open with 0/1 filled for the rest of the match — HasOpenRequisition
        // never went false, which kept the buyer at the raised offensive spend fraction permanently.
        // And no line at all until a platoon is Holding the point (ForwardBaseWantsTruck): every
        // planned forward base used to post a truck want from the review it was planned, which on
        // the 2026-09-14 book bought fifteen trucks for bases that had no platoon to supply.
        // A truck is cargo no transport carries either (the cargo roster excludes munitions trucks
        // outright), so an air-mobile base waits for the same depot the armour waits for rather than
        // sending one truck alone across thirty kilometres of enemy ground.
        bool wantsTruck = mission.Truck == null
            && groundLinesOpen
            && ForwardBaseWantsTruck(CountHoldingPlatoons(mission));
        SetRequisition(state, mission, CommanderPlatoonRole.Truck, wantsTruck ? 1 : 0);
    }

    /// <summary>
    /// Which of an air-mobile base's lines the order book may buy at a depot, pure (design.md,
    /// air-mobile-platoons_20260915 Section 2). A carrier or an air-defence vehicle for an
    /// air-mobile platoon is bought as CARGO by the lift that flies it in, so a depot line for it
    /// would buy the same vehicle twice and leave one of them driving; armour and munitions trucks
    /// are the two things no transport can carry, so they are the only lines a depot ever fills for
    /// one — and only once a depot is near enough to drive from.
    /// </summary>
    internal static bool LinesBoughtByAir(bool airMobile, CommanderPlatoonRole role)
    {
        return airMobile
            && (role == CommanderPlatoonRole.Carrier || role == CommanderPlatoonRole.AirDefence);
    }

    /// <summary>
    /// Whether an air-mobile base may post the lines a depot has to fill, pure: only once the drive
    /// from the nearest usable depot is inside the threshold — which is what a forward operating base
    /// coming online does. A base that is not air-mobile is always free to post them. Exactly on the
    /// threshold the depot counts as near enough, the same boundary
    /// <see cref="PlatoonFlies"/> keeps on the driving side.
    /// </summary>
    internal static bool GroundLineAllowed(bool airMobile, float driveMinutes, float thresholdMinutes)
    {
        return !airMobile || driveMinutes <= thresholdMinutes;
    }

    /// <summary>One role's line for a forward base once both air-mobile rules have been applied,
    /// pure: nothing for a line the lift buys as cargo, nothing for a ground line while no depot is
    /// near enough to drive from, and the shortfall itself otherwise.</summary>
    internal static int AirMobileLineWanted(
        bool airMobile, CommanderPlatoonRole role, bool groundLinesOpen, int shortfall)
    {
        if (LinesBoughtByAir(airMobile, role))
        {
            return 0;
        }

        return groundLinesOpen ? Mathf.Max(0, shortfall) : 0;
    }

    /// <summary>
    /// Pure, for the self-check. How a picket's shortfall is written on the order book
    /// (DECISION-013: pickets are the capture mechanic, so the buyer must be able to buy picket
    /// vehicles when the pool is empty). The insertion load's own doctrine, in role form: the first
    /// vehicle is air defence — a point away from the front is threatened by aircraft, not armour —
    /// and every further one is a carrier, the cheapest non-air-defence combat role in the recipe
    /// and the only one that can actually take a point, which is the whole of a picket's job. The
    /// buyer's <c>ChooseForRole</c> still picks the cheapest definition inside the role.
    /// </summary>
    internal static void PicketRequisitionRoles(int shortfall, out int airDefence, out int carrier)
    {
        int wanted = Mathf.Max(0, shortfall);
        airDefence = Mathf.Min(1, wanted);
        carrier = wanted - airDefence;
    }

    /// <summary>A picket short of <c>PointsMinGarrison</c> posts its shortfall so the buyer fills it
    /// when the pool has nothing to give (design Section 1). A filled picket posts a zero want,
    /// which clears the line outright — the truck line's own convention.</summary>
    private static void PostPicketRequisitions(OperationsState state, CommanderOperationsMission mission)
    {
        // A point this review reserved for a transport helicopter posts NOTHING (departure
        // 2026-09-14): its vehicles are bought as cargo at heli spawn, and a road-stock line for it
        // simply had the buyer fill the point by road before the flight was ever asked for.
        // Vehicles a re-raise has handed back to a nearer depot count as present, exactly as they do
        // for a forward base's platoons (user instruction 2026-09-16,
        // Operations/CommanderOperationsReseat.cs): they are already bought and already paid for, and
        // a line posted for them would have the buyer purchase the same detachment a second time.
        int shortfall = IsAirDeliveredPicket(state, mission.Point)
            ? 0
            : Mathf.Max(
                0,
                CommanderSettings.PointsMinGarrison - mission.PicketMembers.Count
                    - ReseatVehiclesInTransit(mission.Reseat, mission.PicketMembers.Count));
        PicketRequisitionRoles(shortfall, out int airDefence, out int carrier);
        // A picket with nothing on the ground is a NEW detachment and waits behind every
        // replacement; one that has lost a vehicle is an existing force being topped back up (user
        // decision 2026-09-14). Sixteen empty rear pickets were the single largest block of demand
        // on the 2026-09-14 order book.
        bool forNewForce = mission.PicketMembers.Count == 0;
        SetRequisition(state, mission, CommanderPlatoonRole.AirDefence, airDefence, forNewForce);
        SetRequisition(state, mission, CommanderPlatoonRole.Carrier, carrier, forNewForce);
        // Whichever half it is NOT in must be cleared, or a picket that loses its last vehicle keeps
        // a stale replacement line beside its new-detachment one.
        SetRequisition(state, mission, CommanderPlatoonRole.AirDefence, 0, !forNewForce);
        SetRequisition(state, mission, CommanderPlatoonRole.Carrier, 0, !forNewForce);
    }

    /// <summary>An attack posts <c>WantedPlatoons × PlatoonSize</c> across the recipe, proportioned
    /// by the recipe's own slot counts, less whatever its assigned platoons already field (design
    /// SS4).</summary>
    private static void PostAttackRequisitions(OperationsState state, CommanderOperationsMission mission)
    {
        int platoonSize = Mathf.Max(1, CommanderSettings.OperationsPlatoonSize);
        int totalWanted = mission.WantedPlatoons * platoonSize;
        int assignedMembers = 0;
        for (int i = 0; i < mission.Assigned.Count; i++)
        {
            assignedMembers += mission.Assigned[i].Members.Count;
        }

        // Split the same way a forward base's is (user decision 2026-09-14): topping the attack's
        // OWN platoons back up to strength is replacing an existing force; the platoons above what
        // it has been given are a new one.
        int assignedCapacity = mission.Assigned.Count * platoonSize;
        PostRecipeShortfall(
            state, mission, Mathf.Max(0, assignedCapacity - assignedMembers), platoonSize, forNewForce: false);
        PostRecipeShortfall(
            state, mission, Mathf.Max(0, totalWanted - assignedCapacity), platoonSize, forNewForce: true);
    }

    /// <summary>A shortfall of <paramref name="shortfall"/> vehicles written across the recipe's
    /// three roles, proportioned by the recipe's own slot counts with air defence taking the
    /// rounding remainder. One definition, both halves of an attack's book (Reuse rule 4).</summary>
    private static void PostRecipeShortfall(
        OperationsState state,
        CommanderOperationsMission mission,
        int shortfall,
        int platoonSize,
        bool forNewForce)
    {
        int armour = Mathf.RoundToInt(shortfall * CommanderSettings.OperationsRecipeArmour / (float)platoonSize);
        int carrier = Mathf.RoundToInt(shortfall * CommanderSettings.OperationsRecipeCarrier / (float)platoonSize);
        int airDefence = Mathf.Max(0, shortfall - armour - carrier);
        SetRequisition(state, mission, CommanderPlatoonRole.Armour, armour, forNewForce);
        SetRequisition(state, mission, CommanderPlatoonRole.Carrier, carrier, forNewForce);
        SetRequisition(state, mission, CommanderPlatoonRole.AirDefence, airDefence, forNewForce);
    }

    /// <summary>
    /// Closes out requisitions older than <see cref="RequisitionTimeoutSeconds"/> (proceed with
    /// what the mission has, or dissolve an empty forward base outright), then refreshes every live
    /// mission's lines. Called once per HQ per review, after <c>AssignPlatoons</c> so a line reflects
    /// this review's actual assignment rather than the previous review's (B1 fix reordered
    /// <c>AssignPlatoons</c> ahead of <c>UpdateAttacks</c>, which moved this call after it too).
    /// </summary>
    private static void PostRequisitions(FactionHQ hq, OperationsState state)
    {
        for (int i = state.Requisitions.Count - 1; i >= 0; i--)
        {
            CommanderRequisition requisition = state.Requisitions[i];
            if (Time.time - requisition.OpenedAt < RequisitionTimeoutSeconds)
            {
                continue;
            }

            state.Requisitions.RemoveAt(i);
            CommanderOperationsMission? mission = requisition.Mission;
            if (mission == null)
            {
                continue;
            }

            // A picket's line is a standing want, not a one-off request: it is re-posted a few lines
            // below with a fresh clock for as long as the point is short. Saying "proceeds with what
            // it has" every five minutes for every empty picket on the map is noise nobody reads.
            if (mission.Kind == CommanderMissionKind.Picket)
            {
                continue;
            }

            if (mission.Kind == CommanderMissionKind.ForwardBase && mission.Assigned.Count == 0)
            {
                state.Missions.Remove(mission);
                CommanderAiLog.Note(hq, $"{mission.Label}: gives up waiting and stands the mission down.");
            }
            else
            {
                CommanderAiLog.Note(hq, $"{mission.Label}: proceeds with what it has.");
            }
        }

        for (int i = 0; i < state.Missions.Count; i++)
        {
            CommanderOperationsMission mission = state.Missions[i];
            if (mission.Kind == CommanderMissionKind.ForwardBase)
            {
                PostForwardBaseRequisitions(state, mission);
            }
            else if (mission.Kind == CommanderMissionKind.Attack)
            {
                PostAttackRequisitions(state, mission);
            }
            else if (mission.Kind == CommanderMissionKind.Picket)
            {
                PostPicketRequisitions(state, mission);
            }
        }

        PostWithdrawingRequisitions(state);
        PostFobConvoyRequisitions(state);
    }

    /// <summary>
    /// The trucks a road FOB order is still short of, pure: the deliveries less what has arrived or
    /// is already driving. A lost truck is replaced (fix, 2026-09-15), so it is NOT subtracted; the
    /// loss cap in the FOB review ends an order that keeps losing them. Never negative; nothing for
    /// an air order. <paramref name="lost"/> is kept in the signature for the log and the checks.
    /// </summary>
    internal static int FobConvoyTrucksWanted(bool byAir, int deliveries, int delivered, int lost, int convoy)
    {
        _ = lost;
        return byAir ? 0 : Mathf.Max(0, deliveries - delivered - convoy);
    }

    /// <summary>
    /// One aggregate, mission-less truck line for every road FOB order still delivering (fix,
    /// 2026-09-14): the convoy takes its trucks from the pool, but nothing ever asked the order book
    /// for them, and since DECISION-013 the book is the only reason a vehicle is bought — so every
    /// road FOB sat at "delivery held — no munitions truck idle for the construction convoy" for the
    /// whole match. Posted as a new force, behind replacements, like a fresh picket's vehicles.
    /// </summary>
    private static void PostFobConvoyRequisitions(OperationsState state)
    {
        int wanted = 0;
        for (int i = 0; i < state.FobOrders.Count; i++)
        {
            CommanderFobOrder order = state.FobOrders[i];
            if (order.Phase != CommanderFobPhase.Delivering)
            {
                continue;
            }

            wanted += FobConvoyTrucksWanted(order.ByAir, FobDeliveries, order.Delivered, order.Lost, order.Convoy.Count);
        }

        SetRequisition(state, null, CommanderPlatoonRole.Truck, wanted, forNewForce: true);
    }

    /// <summary>
    /// Mission-less order-book lines: one aggregate line per role for every platoon's empty recipe
    /// slots (whatever its state — a withdrawing platoon has no mission, and a platoon that formed
    /// one short has a mission that never asked for the sixth vehicle) plus the standing reserve
    /// (<c>ReservePlatoons</c> full platoons with no job). <see cref="ReinforcePlatoon"/> is what
    /// physically hands a claimed vehicle to a specific platoon once one is bought; new vehicles
    /// beyond that sit in the pool until the recipe fill forms the next platoon from them.
    /// </summary>
    private static void PostWithdrawingRequisitions(OperationsState state)
    {
        int wantedArmour = 0;
        int wantedCarrier = 0;
        int wantedAirDefence = 0;
        int reservePlatoons = 0;
        for (int i = 0; i < state.Platoons.Count; i++)
        {
            CommanderPlatoon platoon = state.Platoons[i];
            // Every platoon short of its recipe asks for the difference, whatever its state. The
            // withdrawing-only version left a platoon that formed at 5/6 (the pool ran dry one
            // vehicle short) at 5/6 for the rest of the match: nothing asked for the sixth.
            if (platoon.Mission == null || platoon.Mission.Kind == CommanderMissionKind.Reserve)
            {
                reservePlatoons++;
            }

            int haveArmour = 0;
            int haveCarrier = 0;
            int haveAirDefence = 0;
            for (int m = 0; m < platoon.Members.Count; m++)
            {
                switch (CommanderPlatoonRoles.Of(platoon.Members[m]?.definition as VehicleDefinition))
                {
                    case CommanderPlatoonRole.Armour:
                        haveArmour++;
                        break;
                    case CommanderPlatoonRole.Carrier:
                        haveCarrier++;
                        break;
                    case CommanderPlatoonRole.AirDefence:
                        haveAirDefence++;
                        break;
                }
            }

            wantedArmour += Mathf.Max(0, CommanderSettings.OperationsRecipeArmour - haveArmour);
            wantedCarrier += Mathf.Max(0, CommanderSettings.OperationsRecipeCarrier - haveCarrier);
            wantedAirDefence += Mathf.Max(0, CommanderSettings.OperationsRecipeAirDefence - haveAirDefence);
        }

        // A standing reserve: the commander always wants ReservePlatoons full platoons with no job
        // beyond the missions it has open, so the order book is never empty while it is under
        // strength and the buyer keeps forming platoons rather than falling back to the old
        // plan-based trickle. Vehicles already idle in the pool count toward it — but PER ROLE and
        // in the buyer (OpenRolesByPriority's coverage, fix A 2026-09-15), not as a flat total
        // taken off here as this line used to: three idle tanks do not cover a carrier want, and
        // subtracting them here as well as there netted the demand to zero with the platoon still
        // one vehicle short of forming.
        // The per-platoon shortfall above replaces losses in platoons that exist; the standing
        // reserve raises platoons that do not. They go on separate lines (user decision
        // 2026-09-14), so building the reserve never outbids a platoon in the field asking for its
        // sixth vehicle back.
        int reserveShort = Mathf.Max(0, CommanderSettings.OperationsReservePlatoons - reservePlatoons);
        int reserveArmour = reserveShort * CommanderSettings.OperationsRecipeArmour;
        int reserveCarrier = reserveShort * CommanderSettings.OperationsRecipeCarrier;
        int reserveAirDefence = reserveShort * CommanderSettings.OperationsRecipeAirDefence;

        SetRequisition(state, null, CommanderPlatoonRole.Armour, wantedArmour);
        SetRequisition(state, null, CommanderPlatoonRole.Carrier, wantedCarrier);
        SetRequisition(state, null, CommanderPlatoonRole.AirDefence, wantedAirDefence);
        SetRequisition(state, null, CommanderPlatoonRole.Armour, reserveArmour, forNewForce: true);
        SetRequisition(state, null, CommanderPlatoonRole.Carrier, reserveCarrier, forNewForce: true);
        SetRequisition(state, null, CommanderPlatoonRole.AirDefence, reserveAirDefence, forNewForce: true);
    }

    /// <summary>Order-book summing over five synthetic requisitions, plus a sixth over-filled line
    /// on top of the same set.</summary>
    private static void CheckOrderBook(List<string> failures)
    {
        Expect(failures, "an owned ground force with an empty order book buys nothing", GroundBuyingBookOnly(true, false), true);
        Expect(failures, "a fresh road FOB order wants three trucks", FobConvoyTrucksWanted(false, 3, 0, 0, 0), 3);
        Expect(failures, "a road FOB with one arrived and one driving wants one truck", FobConvoyTrucksWanted(false, 3, 1, 0, 1), 1);
        Expect(failures, "a road FOB that lost one truck with two arrived wants the replacement", FobConvoyTrucksWanted(false, 3, 2, 1, 0), 1);
        Expect(failures, "an air FOB order wants no trucks", FobConvoyTrucksWanted(true, 3, 0, 0, 0), 0);
        Expect(failures, "an owned ground force with an open line buys", !GroundBuyingBookOnly(true, true), true);
        Expect(failures, "a force this service does not own still runs the plan buyer", !GroundBuyingBookOnly(false, false), true);
        Expect(failures, "a force this service does not own is never held by the book either", !GroundBuyingBookOnly(false, true), true);

        // An air-mobile base's book (design.md, air-mobile-platoons_20260915 Section 2): the lift
        // buys the vehicles it carries, and only the two roles it cannot carry are ever ordered at a
        // depot — once a depot is near enough to drive from.
        Expect(
            failures,
            "an air-mobile base's carriers are bought as cargo, not at a depot",
            LinesBoughtByAir(true, CommanderPlatoonRole.Carrier),
            true);
        Expect(
            failures,
            "an air-mobile base's air defence is bought as cargo too",
            LinesBoughtByAir(true, CommanderPlatoonRole.AirDefence),
            true);
        Expect(
            failures,
            "armour is never flown in, so it stays a depot line",
            LinesBoughtByAir(true, CommanderPlatoonRole.Armour),
            false);
        Expect(
            failures,
            "a munitions truck is never flown in either",
            LinesBoughtByAir(true, CommanderPlatoonRole.Truck),
            false);
        Expect(
            failures,
            "an ordinary base buys every role at a depot exactly as before",
            LinesBoughtByAir(false, CommanderPlatoonRole.Carrier)
                || LinesBoughtByAir(false, CommanderPlatoonRole.AirDefence)
                || LinesBoughtByAir(false, CommanderPlatoonRole.Armour),
            false);

        Expect(
            failures,
            "an ordinary base posts its ground lines whatever the drive",
            GroundLineAllowed(false, 99f, 10f),
            true);
        Expect(
            failures,
            "an air-mobile base twenty minutes from a depot posts no ground line",
            GroundLineAllowed(true, 20f, 10f),
            false);
        Expect(
            failures,
            "an air-mobile base exactly on the threshold may order its armour",
            GroundLineAllowed(true, 10f, 10f),
            true);
        Expect(
            failures,
            "an air-mobile base with a depot next door orders its armour",
            GroundLineAllowed(true, 2f, 10f),
            true);
        Expect(
            failures,
            "the drive gate and the flight gate never both hold at the same distance",
            GroundLineAllowed(true, 10f, 10f) && PlatoonFlies(10f, 10f, true),
            false);

        Expect(
            failures,
            "an air-mobile carrier line is zero however short the platoon is",
            AirMobileLineWanted(true, CommanderPlatoonRole.Carrier, true, 3),
            0);
        Expect(
            failures,
            "an air-mobile armour line waits for the depot",
            AirMobileLineWanted(true, CommanderPlatoonRole.Armour, false, 3),
            0);
        Expect(
            failures,
            "an air-mobile armour line opens once the depot is near",
            AirMobileLineWanted(true, CommanderPlatoonRole.Armour, true, 3),
            3);
        Expect(
            failures,
            "an ordinary base's line is its shortfall, untouched",
            AirMobileLineWanted(false, CommanderPlatoonRole.Carrier, true, 2),
            2);

        PicketRequisitionRoles(2, out int picketAirDefence, out int picketCarrier);
        Expect(failures, "an empty picket orders one air-defence vehicle", picketAirDefence, 1);
        Expect(failures, "an empty picket orders one carrier beside it", picketCarrier, 1);
        PicketRequisitionRoles(1, out picketAirDefence, out picketCarrier);
        Expect(failures, "a picket one short orders the air-defence vehicle first", picketAirDefence, 1);
        Expect(failures, "a picket one short orders no carrier", picketCarrier, 0);
        PicketRequisitionRoles(0, out picketAirDefence, out picketCarrier);
        Expect(failures, "a filled picket orders no air defence", picketAirDefence, 0);
        Expect(failures, "a filled picket orders no carrier", picketCarrier, 0);
        PicketRequisitionRoles(-1, out picketAirDefence, out picketCarrier);
        Expect(failures, "an over-filled picket never orders a negative number of vehicles", picketAirDefence + picketCarrier, 0);
        PicketRequisitionRoles(4, out picketAirDefence, out picketCarrier);
        Expect(failures, "a larger garrison setting still orders exactly one air-defence vehicle", picketAirDefence, 1);
        Expect(failures, "a larger garrison setting orders the rest as carriers", picketCarrier, 3);
        List<CommanderRequisition> requisitions = new()
        {
            new CommanderRequisition { Role = CommanderPlatoonRole.Armour, Wanted = 3, Filled = 1 },
            new CommanderRequisition { Role = CommanderPlatoonRole.AirDefence, Wanted = 2, Filled = 2 },
            new CommanderRequisition { Role = CommanderPlatoonRole.Carrier, Wanted = 1, Filled = 0 },
            new CommanderRequisition { Role = CommanderPlatoonRole.Armour, Wanted = 2, Filled = 0 },
            new CommanderRequisition { Role = CommanderPlatoonRole.Truck, Wanted = 1, Filled = 0 },
        };
        int[] totals = new int[RoleCount];
        SumOrderBook(requisitions, totals);
        Expect(failures, "two armour lines sum into one order", totals[(int)CommanderPlatoonRole.Armour], 4);
        Expect(failures, "a filled line is off the book", totals[(int)CommanderPlatoonRole.AirDefence], 0);
        Expect(failures, "every role keeps its own line (carrier)", totals[(int)CommanderPlatoonRole.Carrier], 1);
        Expect(failures, "every role keeps its own line (truck)", totals[(int)CommanderPlatoonRole.Truck], 1);
        Expect(
            failures,
            "the buyer fills the largest open line",
            LargestOpenRole(totals),
            (int)CommanderPlatoonRole.Armour);

        requisitions.Add(new CommanderRequisition { Role = CommanderPlatoonRole.Armour, Wanted = 1, Filled = 3 });
        SumOrderBook(requisitions, totals);
        Expect(
            failures,
            "an over-filled line never subtracts from another role's order",
            totals[(int)CommanderPlatoonRole.Armour],
            4);

        int[] emptyTotals = new int[RoleCount];
        SumOrderBook(new List<CommanderRequisition>(), emptyTotals);
        Expect(
            failures,
            "an empty book leaves the plan-based counter triangle alone",
            LargestOpenRole(emptyTotals),
            -1);

        CheckBuyOrder(failures);
        CheckCoverage(failures);
    }

    /// <summary>
    /// Fix A (user decision 2026-09-15): the vehicles already covering the book — bought this
    /// review, banked as depot supply, idle in the pool — come off each role's open total before
    /// the buyer orders, most urgent bucket first, and never drive a total negative.
    /// </summary>
    private static void CheckCoverage(List<string> failures)
    {
        Expect(failures, "coverage is the sum of bought, banked and pooled", Coverage(1, 2, 3), 6);
        Expect(failures, "a negative count never becomes negative coverage", Coverage(-4, 2, -1), 2);
        Expect(failures, "an open line less its coverage is what the buyer still buys", OpenAfterCoverage(6, 1, 2, 1), 2);
        Expect(failures, "coverage past the line never goes negative", OpenAfterCoverage(2, 5, 0, 0), 0);
        Expect(failures, "a line nothing covers is bought in full", OpenAfterCoverage(4, 0, 0, 0), 4);
        Expect(failures, "a negative open total reads as nothing to buy", OpenAfterCoverage(-3, 0, 0, 0), 0);

        int[] urgent = new int[RoleCount];
        int[] quiet = new int[RoleCount];
        int[] newUrgent = new int[RoleCount];
        int[] newQuiet = new int[RoleCount];
        int[] coverage = new int[RoleCount];
        int armour = (int)CommanderPlatoonRole.Armour;
        int carrier = (int)CommanderPlatoonRole.Carrier;

        // Four idle tanks against two replacement lines and one new-force line: the replacements
        // are paid first, the new force keeps what is left over.
        urgent[armour] = 1;
        quiet[armour] = 2;
        newQuiet[armour] = 3;
        coverage[armour] = 4;
        quiet[carrier] = 2;
        PayOffCoverage(urgent, quiet, newUrgent, newQuiet, coverage);
        Expect(failures, "coverage pays the threatened replacement first", urgent[armour], 0);
        Expect(failures, "then the quiet replacement", quiet[armour], 0);
        Expect(failures, "and the new force keeps the remainder", newQuiet[armour], 2);
        Expect(failures, "coverage of one role never touches another role's line", quiet[carrier], 2);

        // Five pooled tanks against a six-tank line leave exactly one to buy — the case that used
        // to stall at five when the claimed tally and the pool were both subtracted.
        System.Array.Clear(urgent, 0, urgent.Length);
        System.Array.Clear(quiet, 0, quiet.Length);
        System.Array.Clear(newQuiet, 0, newQuiet.Length);
        System.Array.Clear(coverage, 0, coverage.Length);
        newQuiet[armour] = 6;
        coverage[armour] = 5;
        PayOffCoverage(urgent, quiet, newUrgent, newQuiet, coverage);
        Expect(failures, "five pooled tanks against a six-tank line leave one to buy", newQuiet[armour], 1);

        // Coverage in excess of the whole book leaves every bucket at zero, never below.
        coverage[armour] = 10;
        PayOffCoverage(urgent, quiet, newUrgent, newQuiet, coverage);
        Expect(failures, "excess coverage floors the book at zero", urgent[armour] + quiet[armour] + newUrgent[armour] + newQuiet[armour], 0);
    }

    /// <summary>
    /// The buy order the 2026-09-14 fix installed: truck, then the roles a threatened purpose is
    /// waiting on, then the picket and reserve lines — however much larger those are.
    /// </summary>
    private static void CheckBuyOrder(List<string> failures)
    {
        int[] urgent = new int[RoleCount];
        int[] quiet = new int[RoleCount];
        int[] newUrgent = new int[RoleCount];
        int[] newQuiet = new int[RoleCount];
        List<CommanderPlatoonRole> order = new();

        // A forward base in contact wants three tanks; sixteen rear pickets want sixteen of
        // everything else. The tanks must still be bought first.
        urgent[(int)CommanderPlatoonRole.Armour] = 3;
        quiet[(int)CommanderPlatoonRole.AirDefence] = 16;
        quiet[(int)CommanderPlatoonRole.Carrier] = 16;
        OrderOpenRoles(urgent, quiet, newUrgent, newQuiet, order);
        Expect(failures, "a threatened purpose's armour outranks every picket line", order.Count > 0 ? (int)order[0] : -1, (int)CommanderPlatoonRole.Armour);
        Expect(failures, "the picket lines are still bought, behind the threatened purpose", order.Count, 3);

        // The truck leads whichever half of the book it sits in.
        quiet[(int)CommanderPlatoonRole.Truck] = 1;
        OrderOpenRoles(urgent, quiet, newUrgent, newQuiet, order);
        Expect(failures, "an open truck line still leads the whole order", order.Count > 0 ? (int)order[0] : -1, (int)CommanderPlatoonRole.Truck);
        Expect(failures, "the threatened purpose follows the truck", order.Count > 1 ? (int)order[1] : -1, (int)CommanderPlatoonRole.Armour);

        // Nothing threatened: the book falls back to largest line first, as it always did.
        System.Array.Clear(urgent, 0, urgent.Length);
        quiet[(int)CommanderPlatoonRole.Truck] = 0;
        OrderOpenRoles(urgent, quiet, newUrgent, newQuiet, order);
        Expect(failures, "with nothing threatened the largest line leads", order.Count > 0 ? (int)order[0] : -1, (int)CommanderPlatoonRole.Carrier);

        // A role wanted by both halves appears once, at its urgent position.
        urgent[(int)CommanderPlatoonRole.Carrier] = 1;
        OrderOpenRoles(urgent, quiet, newUrgent, newQuiet, order);
        Expect(failures, "a role wanted by both halves leads once", order.Count > 0 ? (int)order[0] : -1, (int)CommanderPlatoonRole.Carrier);
        Expect(failures, "a role wanted by both halves is never listed twice", order.Count, 2);

        System.Array.Clear(urgent, 0, urgent.Length);
        System.Array.Clear(quiet, 0, quiet.Length);
        OrderOpenRoles(urgent, quiet, newUrgent, newQuiet, order);
        Expect(failures, "an empty book asks the buyer for no role at all", order.Count, 0);

        CheckExistingBeforeNew(failures, urgent, quiet, newUrgent, newQuiet, order);
    }

    /// <summary>
    /// "Existing forces first" (user decision 2026-09-14, rung 2 tiers 2 and 4): a platoon already
    /// in the field asking for a replacement is bought before a new platoon or a new picket,
    /// however much larger the new force's line is — and the threatened-before-quiet split the
    /// 2026-09-14 fix installed still holds inside each half.
    /// </summary>
    private static void CheckExistingBeforeNew(
        List<string> failures, int[] urgent, int[] quiet, int[] newUrgent, int[] newQuiet, List<CommanderPlatoonRole> order)
    {
        System.Array.Clear(urgent, 0, urgent.Length);
        System.Array.Clear(quiet, 0, quiet.Length);
        System.Array.Clear(newUrgent, 0, newUrgent.Length);
        System.Array.Clear(newQuiet, 0, newQuiet.Length);

        // One quiet platoon wants one replacement carrier; sixteen empty pickets want sixteen
        // air-defence vehicles between them. The replacement is still bought first.
        quiet[(int)CommanderPlatoonRole.Carrier] = 1;
        newQuiet[(int)CommanderPlatoonRole.AirDefence] = 16;
        OrderOpenRoles(urgent, quiet, newUrgent, newQuiet, order);
        Expect(failures, "a replacement outranks a much larger new-force line", order.Count > 0 ? (int)order[0] : -1, (int)CommanderPlatoonRole.Carrier);
        Expect(failures, "the new-force line is still bought, behind the replacement", order.Count, 2);

        // A NEW platoon for a threatened purpose still waits behind a replacement for a quiet one.
        System.Array.Clear(newQuiet, 0, newQuiet.Length);
        newUrgent[(int)CommanderPlatoonRole.Armour] = 9;
        OrderOpenRoles(urgent, quiet, newUrgent, newQuiet, order);
        Expect(failures, "a new force at the front still waits behind an existing one at the rear", order.Count > 0 ? (int)order[0] : -1, (int)CommanderPlatoonRole.Carrier);
        Expect(failures, "a threatened new force is bought before a quiet one", order.Count > 1 ? (int)order[1] : -1, (int)CommanderPlatoonRole.Armour);

        // Threatened before quiet still holds inside the existing half.
        urgent[(int)CommanderPlatoonRole.Armour] = 1;
        OrderOpenRoles(urgent, quiet, newUrgent, newQuiet, order);
        Expect(failures, "a threatened replacement leads a quiet one", order.Count > 0 ? (int)order[0] : -1, (int)CommanderPlatoonRole.Armour);
        Expect(failures, "a role open in both halves is listed once", order.Count, 2);

        // The truck leads the whole book even when its only line is a new force's.
        System.Array.Clear(urgent, 0, urgent.Length);
        System.Array.Clear(quiet, 0, quiet.Length);
        newQuiet[(int)CommanderPlatoonRole.Truck] = 1;
        OrderOpenRoles(urgent, quiet, newUrgent, newQuiet, order);
        Expect(failures, "the truck leads the book from either half", order.Count > 0 ? (int)order[0] : -1, (int)CommanderPlatoonRole.Truck);

        System.Array.Clear(urgent, 0, urgent.Length);
        System.Array.Clear(quiet, 0, quiet.Length);
        System.Array.Clear(newUrgent, 0, newUrgent.Length);
        System.Array.Clear(newQuiet, 0, newQuiet.Length);
    }
}
