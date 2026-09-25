using System.Collections.Generic;
using UnityEngine;

namespace GroundControlRts;

/// <summary>
/// Offensives: which target to attack, how large the attack should be, which axes it forms up on,
/// when it goes in, and how it ends (design SS3).
/// </summary>
internal sealed partial class CommanderOperationsService
{
    /// <summary>Attack strength as a multiple of the observed defence (design SS3).</summary>
    private const float SizingMultiplier = 1.5f;

    /// <summary>Floor for an attack on an airbase, however little is observed.</summary>
    private const int MinPlatoonsForBase = 2;

    /// <summary>Floor for an attack on a control point.</summary>
    private const int MinPlatoonsForPoint = 1;

    /// <summary>Ceiling, so one attack cannot swallow the whole force.</summary>
    private const int MaxPlatoonsPerAttack = 6;

    /// <summary>Radius around an attack target inside which tracked hostile ground counts toward
    /// sizing (design SS3).</summary>
    private const float ObservedRadiusMeters = 8000f;

    /// <summary>
    /// Platoons an attack should commit: 1.5x the observed defence, rounded up, floored at
    /// <see cref="MinPlatoonsForBase"/>/<see cref="MinPlatoonsForPoint"/> and capped at
    /// <see cref="MaxPlatoonsPerAttack"/>. Pure, for the self-check.
    /// </summary>
    internal static int PlatoonsForTarget(int observedEnemyUnits, int platoonSize, bool targetIsBase)
    {
        int sized = Mathf.CeilToInt(SizingMultiplier * observedEnemyUnits / Mathf.Max(1, platoonSize));
        int floor = targetIsBase ? MinPlatoonsForBase : MinPlatoonsForPoint;
        return Mathf.Clamp(sized, floor, MaxPlatoonsPerAttack);
    }

    /// <summary>
    /// B5 fix: design SS3 re-sizes a retried attack larger than the one that just failed. The live
    /// observed count and the floor a failed attack left on this target (<see cref="ResolveAttack"/>)
    /// — whichever is larger — is what sizing actually reads. Pure, for the self-check.
    /// </summary>
    internal static int EffectiveObserved(int liveObserved, int storedFloor)
    {
        return Mathf.Max(liveObserved, storedFloor);
    }

    /// <summary>The target this HQ's observed-enemy floor is keyed on: the control point or the
    /// airbase, whichever this attack is against. Null for neither (never a real attack).</summary>
    private static object? ObservedFloorKey(CommanderStrategicPoint? point, Airbase? airbase)
    {
        return point != null ? point : airbase;
    }

    private static int GetObservedFloor(OperationsState state, object? key)
    {
        return key != null && state.ObservedFloors.TryGetValue(key, out int floor) ? floor : 0;
    }

    /// <summary>
    /// Tracked hostile ground units within <see cref="ObservedRadiusMeters"/> of <paramref name="target"/>,
    /// seen within <c>ThreatMemorySeconds</c> — the commander's own tracking database, not the true
    /// count (design SS3 is explicit about this). The <c>IsUnderThreat</c> walk again, with its
    /// building skip, retargeted at an attack target instead of a base.
    /// </summary>
    private static int CountObserved(FactionHQ hq, GlobalPosition target)
    {
        int count = 0;
        float now = Time.timeSinceLevelLoad;
        foreach (KeyValuePair<PersistentID, TrackingInfo> entry in hq.trackingDatabase)
        {
            TrackingInfo info = entry.Value;
            if (now - info.lastSpottedTime > CommanderEnemyCommanderService.ThreatMemorySeconds
                || !info.TryGetUnit(out Unit unit)
                || unit == null
                || unit.disabled
                || unit is not GroundVehicle
                || unit is Building
                || unit.NetworkHQ == null
                || ReferenceEquals(unit.NetworkHQ, hq))
            {
                continue;
            }

            if (CommanderGameAccess.HorizontalDistance(target.AsVector3(), info.lastKnownPosition.AsVector3()) <= ObservedRadiusMeters)
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>Platoons neither withdrawing nor already committed to a live attack — the reserve
    /// and anything still forming counts as "spare" (design SS3: "at least one spare platoon exists
    /// or is forming").</summary>
    private static int CountSparePlatoons(OperationsState state)
    {
        int count = 0;
        for (int i = 0; i < state.Platoons.Count; i++)
        {
            CommanderPlatoon platoon = state.Platoons[i];
            if (platoon.State != CommanderPlatoonState.Withdrawing
                && platoon.State != CommanderPlatoonState.Attacking
                && (platoon.Mission == null || platoon.Mission.Kind == CommanderMissionKind.Reserve))
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>
    /// How many attacks this commander may have open at once, pure. Deliberately the same shape as
    /// <c>MaxForwardBases</c> (Reuse rule 4): a floor, plus a share of the platoon count, take the
    /// larger — so a small army still pushes somewhere and a large one pushes in proportion.
    /// <para>
    /// Why it exists: until 2026-09-18 <see cref="TryOpenAttack"/> refused the moment ANY attack was
    /// open, so a commander ran exactly one push however large it grew. The match measured that day
    /// showed the result — a mission board of 48 pickets, 11 forward bases and ONE attack, with one
    /// of ten platoons attacking and the rest driving to garrison duty. Nothing on the ground was
    /// generating close-support work, so the wing flew 382 fighter sorties against 177 ground-attack
    /// ones and the war was fighters circling control points.
    /// </para>
    /// <para>
    /// A non-positive floor answers ONE, which is that old behaviour rather than "no attacks at
    /// all". A deliberate departure from the repo's usual "zero switches the rule off": switching
    /// this rule off has to leave the commander attacking, because one that never attacks is not a
    /// commander.
    /// </para>
    /// <para>The share is clamped to 0..1 for the reason <c>MaxForwardBases</c> clamps its own — a
    /// mis-typed slider should thin the attacks, never multiply the army.</para>
    /// </summary>
    internal static int MaxConcurrentAttacks(int platoonCount, float attacksPerPlatoon, int floor)
    {
        if (floor <= 0)
        {
            return 1;
        }

        int share = Mathf.FloorToInt(Mathf.Max(0, platoonCount) * Mathf.Clamp01(attacksPerPlatoon));
        return Mathf.Max(floor, share);
    }

    /// <summary>
    /// Takes up to <paramref name="wanted"/> platoons off forward bases the enemy is NOT at, and
    /// returns them to the spare pool for an attack to claim (concurrent-attacks_20260918).
    /// <para>
    /// The base keeps its picket and its munitions truck, so it still HOLDS its point — a picket is
    /// what holds ground, and stripping one would hand the point over. Threat is
    /// <see cref="IsThreatenedFrontPoint"/>, the mod's one definition of "the enemy is at this
    /// point", already read by the forward-base allowance, the pool order and the order book
    /// (Reuse rule 4); this is its fourth reader and invents no second idea of danger.
    /// </para>
    /// <para>
    /// Worst-ranked base first, so the commander gives up its least valuable rear position before
    /// its best one. A base with more than one platoon gives up only the surplus first; the last
    /// platoon on a base goes only when nothing else will serve.
    /// </para>
    /// </summary>
    private int CallUpQuietForwardBases(
        FactionHQ hq, OperationsState state, int wanted, string? label, List<CommanderPlatoon> calledUp)
    {
        if (wanted <= 0)
        {
            return 0;
        }

        int taken = 0;
        OrderForwardBasesByRank(state);
        for (int i = forwardBasesByRank.Count - 1; i >= 0 && taken < wanted; i--)
        {
            CommanderOperationsMission baseMission = forwardBasesByRank[i];
            while (taken < wanted
                && PlatoonMayBeCalledUp(
                    IsThreatenedFrontPoint(state, baseMission),
                    baseMission.Assigned.Count > 0,
                    baseMission.Kind == CommanderMissionKind.ForwardBase))
            {
                CommanderPlatoon platoon = baseMission.Assigned[baseMission.Assigned.Count - 1];
                // ReleaseFromMission, not a hand-rolled removal: its own summary calls it "the single
                // choke point every departure path already goes through", and it is what also ends
                // the platoon's reinforcement answer and clears the ground posture it was halfway
                // through. Unassigning without it would carry an arc or a bound into the attack.
                ReleaseFromMission(platoon);
                calledUp.Add(platoon);
                taken++;
                CommanderAiLog.Note(
                    hq,
                    $"calls up {platoon.Name} from {baseMission.Label}"
                        + (label == null ? string.Empty : $" for the attack on {label}")
                        + ": that base is quiet.");
            }
        }

        return taken;
    }

    /// <summary>
    /// Whether one forward base may give its platoon to an attack, pure. Three conditions, and the
    /// first is the guarantee: a base the enemy is AT keeps everything it has. The second is that
    /// there is something to send. The third is that only a forward base is ever asked — a picket is
    /// the detachment holding the point and is never called up (concurrent-attacks_20260918).
    /// <para>
    /// Why it exists: <c>CountSparePlatoons</c> counts only platoons with no mission at all, and on
    /// the match of 2026-09-18 that was one to three per commander while eight or nine sat on forward
    /// bases. Raising the attack allowance without this would have been inert — the allowance would
    /// have permitted attacks the commander had nobody to man.
    /// </para>
    /// <para>
    /// What the caller must honour and this rule cannot express: the base keeps its
    /// <c>PicketMembers</c> and its <c>Truck</c>. The platoon goes, the point is still held.
    /// </para>
    /// </summary>
    internal static bool PlatoonMayBeCalledUp(bool threatened, bool hasPlatoon, bool isForwardBase)
    {
        return isForwardBase && hasPlatoon && !threatened;
    }

    /// <summary>How many candidate targets one attack opening tries before giving up for this review.
    /// Axis planning is cheap, but a commander with a long list of unreachable targets should not
    /// walk all of them every 30 s.</summary>
    private const int MaxOffensiveTargetAttempts = 4;

    private readonly struct OffensiveTarget
    {
        internal readonly CommanderStrategicPoint? Point;
        internal readonly Airbase? Airbase;
        internal readonly float Distance;
        internal readonly int Order;

        internal OffensiveTarget(CommanderStrategicPoint? point, Airbase? airbase, float distance, int order)
        {
            Point = point;
            Airbase = airbase;
            Distance = distance;
            Order = order;
        }
    }

    private static readonly List<OffensiveTarget> offensiveTargets = new();
    private static readonly List<CommanderPlatoon> calledUpPlatoons = new();

    /// <summary>
    /// Lists the targets worth attacking (design SS3), best first: enemy-held control points on the
    /// front, nearest to ground this commander holds first; then enemy bases whose observed defence
    /// is beatable with what is spare or forming, nearest to its territory first. A target that
    /// already has an attack is left out, so concurrent attacks spread across targets.
    /// <para>
    /// Nearest to held ground, not <c>DistanceToEnemyMeters</c>: every enemy-held point is its own
    /// nearest enemy asset, so that distance is 0 for all of them and the old "nearest first" pick
    /// was really "most valuable anywhere on the map" (review H7). Points come before bases, as
    /// before, but a point whose axes cannot be planned no longer hides the bases behind it: the
    /// caller walks the list.
    /// </para>
    /// </summary>
    private void CollectOffensiveTargets(FactionHQ hq, OperationsState state, List<OffensiveTarget> into)
    {
        into.Clear();
        for (int i = 0; i < state.RankedPoints.Count; i++)
        {
            CommanderRankedPoint ranked = state.RankedPoints[i];
            FactionHQ? owner = ranked.Point.GetOwner();
            if (owner == null || ReferenceEquals(owner, hq) || !ranked.IsFront || HasAttackOn(state, ranked.Point, null))
            {
                continue;
            }

            into.Add(new OffensiveTarget(ranked.Point, null, NearestHeldAssetDistance(hq, ranked.Point.Position), i));
        }

        int spare = CountSparePlatoons(state);
        int platoonSize = Mathf.Max(1, CommanderSettings.OperationsPlatoonSize);
        GlobalPosition territory = CommanderCaptureService.GetTerritoryCenter(hq);
        foreach (KeyValuePair<string, Airbase> entry in FactionRegistry.airbaseLookup)
        {
            Airbase airbase = entry.Value;
            if (airbase == null
                || airbase.disabled
                || airbase.center == null
                || airbase.SavedAirbase == null
                || !airbase.SavedAirbase.Capturable
                || ReferenceEquals(airbase.CurrentHQ, hq)
                || HasAttackOn(state, null, airbase))
            {
                continue;
            }

            GlobalPosition position = airbase.center.GlobalPosition();
            int observed = EffectiveObserved(CountObserved(hq, position), GetObservedFloor(state, ObservedFloorKey(null, airbase)));
            if (PlatoonsForTarget(observed, platoonSize, targetIsBase: true) > spare)
            {
                continue;
            }

            float distance = CommanderGameAccess.HorizontalDistance(territory.AsVector3(), position.AsVector3());
            into.Add(new OffensiveTarget(null, airbase, distance, into.Count));
        }

        // Points first, then bases; nearest first inside each; the ranking order breaks ties so the
        // choice is stable from review to review.
        into.Sort((a, b) =>
        {
            bool aPoint = a.Point != null;
            bool bPoint = b.Point != null;
            if (aPoint != bPoint)
            {
                return aPoint ? -1 : 1;
            }

            int byDistance = a.Distance.CompareTo(b.Distance);
            return byDistance != 0 ? byDistance : a.Order.CompareTo(b.Order);
        });
    }

    /// <summary>Whether this commander already has an attack on the given point or base.</summary>
    private static bool HasAttackOn(OperationsState state, CommanderStrategicPoint? point, Airbase? airbase)
    {
        for (int i = 0; i < state.Missions.Count; i++)
        {
            CommanderOperationsMission mission = state.Missions[i];
            if (mission.Kind != CommanderMissionKind.Attack)
            {
                continue;
            }

            if ((point != null && ReferenceEquals(mission.Point, point))
                || (airbase != null && ReferenceEquals(mission.TargetAirbase, airbase)))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>B5's observed floor: a live observation and a stored floor, whichever is larger.</summary>
    private static void CheckObservedFloor(List<string> failures)
    {
        Expect(failures, "a live observation above the floor wins", EffectiveObserved(5, 2), 5);
        Expect(failures, "a floor above a decayed live observation wins", EffectiveObserved(0, 4), 4);
        Expect(failures, "equal values agree", EffectiveObserved(3, 3), 3);
        Expect(failures, "no floor at all falls back to the live observation", EffectiveObserved(2, 0), 2);
    }

    /// <summary>The sizing formula at its named bounds (design SS3), <c>platoonSize = 6</c> unless
    /// stated otherwise.</summary>
    /// <summary>
    /// The attack allowance and the call-up, at the boundaries that matter
    /// (concurrent-attacks_20260918). Both are numbers a retune can turn into nonsense: an allowance
    /// that collapses to zero stops the commander attacking at all, and a call-up that ignores the
    /// threat test strips the base the enemy is standing on.
    /// </summary>
    private static void CheckConcurrentAttacks(List<string> failures)
    {
        Expect(failures, "a small army still gets the floor of attacks", MaxConcurrentAttacks(4, 0.2f, 2), 2);
        Expect(failures, "a ten-platoon army gets one attack per five platoons", MaxConcurrentAttacks(10, 0.2f, 2), 2);
        Expect(failures, "a twenty-platoon army gets four attacks", MaxConcurrentAttacks(20, 0.2f, 2), 4);
        Expect(failures, "an army with no platoons still gets the floor", MaxConcurrentAttacks(0, 0.2f, 2), 2);
        Expect(failures, "a floor of zero means one attack, the old behaviour", MaxConcurrentAttacks(20, 0f, 0), 1);
        Expect(failures, "a negative floor means one attack, the old behaviour", MaxConcurrentAttacks(20, 0.2f, -1), 1);
        Expect(failures, "a share above one is clamped rather than multiplying the army", MaxConcurrentAttacks(10, 5f, 2), 10);

        Expect(failures, "a quiet forward base may send its platoon to an attack", PlatoonMayBeCalledUp(threatened: false, hasPlatoon: true, isForwardBase: true), true);
        Expect(failures, "a threatened forward base is never stripped", PlatoonMayBeCalledUp(threatened: true, hasPlatoon: true, isForwardBase: true), false);
        Expect(failures, "a forward base with no platoon has nothing to send", PlatoonMayBeCalledUp(threatened: false, hasPlatoon: false, isForwardBase: true), false);
        Expect(failures, "a picket is never called up, it is what holds the point", PlatoonMayBeCalledUp(threatened: false, hasPlatoon: true, isForwardBase: false), false);
    }

    private static void CheckSizing(List<string> failures)
    {
        Expect(failures, "an unobserved point still gets one platoon", PlatoonsForTarget(0, 6, false), 1);
        Expect(failures, "an unobserved base still gets two", PlatoonsForTarget(0, 6, true), 2);
        Expect(failures, "1.5 x 4 = 6 is one platoon exactly", PlatoonsForTarget(4, 6, false), 1);
        Expect(failures, "1.5 x 5 = 7.5 rounds up to two platoons", PlatoonsForTarget(5, 6, false), 2);
        Expect(failures, "the ceiling holds against an enormous observed force", PlatoonsForTarget(100, 6, true), 6);
        Expect(failures, "the ceiling holds after a platoon-size retune", PlatoonsForTarget(12, 1, false), 6);
    }

    /// <summary>Forward bases and held bases within this of the target are form-up candidates
    /// (design SS3).</summary>
    private const float AxisCandidateRadiusMeters = 25000f;

    /// <summary>Two axes must be within this fraction of each other's distance to the target
    /// (design SS3).</summary>
    private const float AxisDistanceTolerance = 0.20f;

    /// <summary>Two axes must come in on bearings at least this far apart (design SS3).</summary>
    private const float AxisMinBearingDegrees = 60f;

    /// <summary>With only one candidate, the second group forms this far off the direct line
    /// (design SS3).</summary>
    private const float FlankMinMeters = 6000f;
    private const float FlankMaxMeters = 10000f;

    /// <summary>Each group stops this far short of the target on its own side (design SS3).</summary>
    private const float ReleaseDistanceMeters = 5000f;

    /// <summary>A release point snaps to a discovered Crossroads/Roadside point within this of the
    /// ideal position (departure 4) — half the Roadside spacing, so the snap can never cross to the
    /// next Roadside point along. Planner-chosen.</summary>
    private const float ReleaseSnapMeters = 3000f;

    /// <summary>Slope the release-point probe treats as flat ground — the discovery constant's
    /// value (departure 5), independently declared here because that one is private to a different
    /// class.</summary>
    private const float FlatNormalY = 0.94f;

    /// <summary>
    /// Two-axis (or three) selection, pure so the self-check can drive it with synthetic distances
    /// and bearings. Candidates past <paramref name="maxDistanceMeters"/> are dropped; every
    /// remaining candidate is tried as a seed, walking the rest in ascending distance and accepting
    /// one when it is within <paramref name="distanceTolerance"/> of the seed's own distance and its
    /// bearing is at least <paramref name="minBearingSeparationDegrees"/> from every axis already
    /// accepted for this seed (accepting stops at <paramref name="maxAxes"/>); the seed yielding the
    /// most axes wins, ties going to the lowest seed index. <paramref name="result"/> is cleared
    /// first.
    /// </summary>
    internal static void SelectAxes(
        IReadOnlyList<float> distances,
        IReadOnlyList<float> bearings,
        float maxDistanceMeters,
        float distanceTolerance,
        float minBearingSeparationDegrees,
        int maxAxes,
        List<int> result)
    {
        result.Clear();
        List<int> inReach = new(distances.Count);
        for (int i = 0; i < distances.Count; i++)
        {
            if (distances[i] <= maxDistanceMeters)
            {
                inReach.Add(i);
            }
        }

        List<int> ordered = new(inReach);
        ordered.Sort((a, b) => distances[a].CompareTo(distances[b]));

        List<int> best = new();
        List<int> accepted = new();
        for (int s = 0; s < inReach.Count; s++)
        {
            float seedDistance = distances[inReach[s]];
            accepted.Clear();
            for (int i = 0; i < ordered.Count && accepted.Count < maxAxes; i++)
            {
                int candidate = ordered[i];
                if (Mathf.Abs(distances[candidate] - seedDistance) > distanceTolerance * seedDistance)
                {
                    continue;
                }

                bool bearingOk = true;
                for (int a = 0; a < accepted.Count; a++)
                {
                    if (CircularBearingDifference(bearings[candidate], bearings[accepted[a]]) < minBearingSeparationDegrees)
                    {
                        bearingOk = false;
                        break;
                    }
                }

                if (bearingOk)
                {
                    accepted.Add(candidate);
                }
            }

            if (accepted.Count > best.Count)
            {
                best = new List<int>(accepted);
            }
        }

        result.AddRange(best);
    }

    /// <summary>The short way round: 350 deg and 10 deg are 20 deg apart, not 340.</summary>
    private static float CircularBearingDifference(float a, float b)
    {
        float diff = Mathf.Abs(a - b) % 360f;
        return diff > 180f ? 360f - diff : diff;
    }

    private readonly List<GlobalPosition> axisCandidatePositions = new();
    private readonly List<float> axisCandidateDistances = new();
    private readonly List<float> axisCandidateBearings = new();
    private readonly List<int> axisSelection = new();

    /// <summary>
    /// Picks 2-3 axes from this commander's holding forward bases and held bases within
    /// <see cref="AxisCandidateRadiusMeters"/> of <paramref name="target"/>. Exactly one candidate
    /// forms a second group at a flank point off the direct line (terrain-validated, mirrored to
    /// the other side if the first fails); none means the attack waits.
    /// </summary>
    private bool TryPlanAxes(FactionHQ hq, GlobalPosition target, List<CommanderAssaultGroup> axes)
    {
        axes.Clear();
        axisCandidatePositions.Clear();
        axisCandidateDistances.Clear();
        axisCandidateBearings.Clear();

        if (states.TryGetValue(hq, out OperationsState state))
        {
            for (int i = 0; i < state.Missions.Count; i++)
            {
                CommanderOperationsMission mission = state.Missions[i];
                if (mission.Kind != CommanderMissionKind.ForwardBase || mission.Point == null)
                {
                    continue;
                }

                bool holding = false;
                for (int p = 0; p < mission.Assigned.Count; p++)
                {
                    if (mission.Assigned[p].State == CommanderPlatoonState.Holding)
                    {
                        holding = true;
                        break;
                    }
                }

                if (holding)
                {
                    AddAxisCandidate(mission.Point.Position, target);
                }
            }
        }

        foreach (Airbase airbase in hq.GetAirbases())
        {
            if (airbase != null && !airbase.disabled && airbase.center != null)
            {
                AddAxisCandidate(airbase.center.GlobalPosition(), target);
            }
        }

        axisSelection.Clear();
        SelectAxes(
            axisCandidateDistances,
            axisCandidateBearings,
            AxisCandidateRadiusMeters,
            AxisDistanceTolerance,
            AxisMinBearingDegrees,
            3,
            axisSelection);

        if (axisSelection.Count >= 2)
        {
            for (int i = 0; i < axisSelection.Count; i++)
            {
                GlobalPosition formUp = axisCandidatePositions[axisSelection[i]];
                axes.Add(new CommanderAssaultGroup { FormUpPoint = formUp, ReleasePoint = ReleasePointFor(formUp, target) });
            }

            return true;
        }

        if (axisSelection.Count == 1)
        {
            GlobalPosition primary = axisCandidatePositions[axisSelection[0]];
            axes.Add(new CommanderAssaultGroup { FormUpPoint = primary, ReleasePoint = ReleasePointFor(primary, target) });

            GlobalPosition? flank = TryFindFlankFormUp(primary, target);
            if (flank.HasValue)
            {
                axes.Add(new CommanderAssaultGroup
                {
                    FormUpPoint = flank.Value,
                    ReleasePoint = ReleasePointFor(flank.Value, target),
                });
            }

            return true;
        }

        return false;
    }

    private void AddAxisCandidate(GlobalPosition position, GlobalPosition target)
    {
        axisCandidatePositions.Add(position);
        axisCandidateDistances.Add(CommanderGameAccess.HorizontalDistance(position.AsVector3(), target.AsVector3()));
        axisCandidateBearings.Add(BearingDegrees(target, position));
    }

    private static float BearingDegrees(GlobalPosition from, GlobalPosition to)
    {
        Vector3 direction = to.AsVector3() - from.AsVector3();
        direction.y = 0f;
        return direction.sqrMagnitude < 1f ? 0f : Quaternion.LookRotation(direction).eulerAngles.y;
    }

    /// <summary>The one candidate's flank partner: a point off to one side of the direct line,
    /// mirrored to the other side if the first fails departure 5's flat-ground test.</summary>
    private static GlobalPosition? TryFindFlankFormUp(GlobalPosition primary, GlobalPosition target)
    {
        Vector3 direct = primary.AsVector3() - target.AsVector3();
        direct.y = 0f;
        if (direct.sqrMagnitude < 1f)
        {
            return null;
        }

        Vector3 midpoint = target.AsVector3() + direct * 0.5f;
        Vector3 perpendicular = Vector3.Cross(direct.normalized, Vector3.up);
        float offset = Random.Range(FlankMinMeters, FlankMaxMeters);

        for (int side = 0; side < 2; side++)
        {
            Vector3 flankVector = midpoint + perpendicular * (offset * (side == 0 ? 1f : -1f));
            GlobalPosition candidate = CommanderGameAccess.SnapToTerrain(new GlobalPosition(flankVector.x, 0f, flankVector.z));
            if (IsGoodGround(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static bool IsGoodGround(GlobalPosition candidate)
    {
        return !CommanderGameAccess.IsBelowSeaLevel(candidate)
            && CommanderSamSiteAnalyzerService.EstimateStrategicTerrainNormalY(candidate.x, candidate.z, 40f) >= FlatNormalY;
    }

    /// <summary>
    /// The point a group stops at and waits, <see cref="ReleaseDistanceMeters"/> short of the target
    /// on its own side (design SS3). Departure 4: the nearest discovered Crossroads/Roadside point
    /// within <see cref="ReleaseSnapMeters"/> of the ideal position, else the ideal position itself
    /// terrain-snapped and validated, else the first acceptable point on a ring around it (the
    /// <c>EnsureDefencePosts</c> probe shape).
    /// </summary>
    private GlobalPosition ReleasePointFor(GlobalPosition formUp, GlobalPosition target)
    {
        Vector3 direction = formUp.AsVector3() - target.AsVector3();
        direction.y = 0f;
        if (direction.sqrMagnitude < 1f)
        {
            return CommanderGameAccess.SnapToTerrain(target);
        }

        Vector3 idealVector = target.AsVector3() + direction.normalized * ReleaseDistanceMeters;
        GlobalPosition ideal = new(idealVector.x, 0f, idealVector.z);

        IReadOnlyList<CommanderStrategicPoint>? points = CommanderStrategicPointService.Instance?.Points;
        CommanderStrategicPoint? nearestRoad = null;
        float nearestRoadDistance = float.MaxValue;
        if (points != null)
        {
            for (int i = 0; i < points.Count; i++)
            {
                CommanderStrategicPoint point = points[i];
                if (point.Kind != StrategicPointKind.Crossroads && point.Kind != StrategicPointKind.Roadside)
                {
                    continue;
                }

                float distance = CommanderGameAccess.HorizontalDistance(ideal.AsVector3(), point.Position.AsVector3());
                if (distance < nearestRoadDistance)
                {
                    nearestRoadDistance = distance;
                    nearestRoad = point;
                }
            }
        }

        if (nearestRoad != null && nearestRoadDistance <= ReleaseSnapMeters)
        {
            return nearestRoad.Position;
        }

        GlobalPosition snapped = CommanderGameAccess.SnapToTerrain(ideal);
        if (IsGoodGround(snapped))
        {
            return snapped;
        }

        const int ringProbes = 8;
        const float ringMeters = 500f;
        for (int i = 0; i < ringProbes; i++)
        {
            float angle = i * (Mathf.PI * 2f / ringProbes);
            GlobalPosition candidate = CommanderGameAccess.SnapToTerrain(new GlobalPosition(
                ideal.x + Mathf.Cos(angle) * ringMeters, ideal.y, ideal.z + Mathf.Sin(angle) * ringMeters));
            if (IsGoodGround(candidate))
            {
                return candidate;
            }
        }

        return snapped;
    }

    /// <summary>Axis selection at 25 km / 20% / 60 deg / 3 axes (design SS3).</summary>
    private static void CheckAxes(List<string> failures)
    {
        const float radius = 25000f;
        const float tolerance = 0.20f;
        const float minBearing = 60f;
        const int maxAxes = 3;
        List<int> result = new();

        SelectAxes(new[] { 10000f, 11000f }, new[] { 0f, 90f }, radius, tolerance, minBearing, maxAxes, result);
        ExpectSequence(failures, "two candidates at similar distance on different bearings are both axes", result, 0, 1);

        SelectAxes(new[] { 10000f, 10500f }, new[] { 0f, 30f }, radius, tolerance, minBearing, maxAxes, result);
        ExpectSequence(failures, "a second candidate 30 deg round is the same attack, not a second axis", result, 0);

        SelectAxes(new[] { 10000f, 10000f }, new[] { 0f, 60f }, radius, tolerance, minBearing, maxAxes, result);
        ExpectSequence(failures, "60 deg apart is far enough apart", result, 0, 1);

        SelectAxes(new[] { 10000f, 13000f }, new[] { 0f, 120f }, radius, tolerance, minBearing, maxAxes, result);
        ExpectSequence(failures, "a candidate 30% further away would arrive long after the other", result, 0);

        SelectAxes(new[] { 10000f, 12000f }, new[] { 0f, 120f }, radius, tolerance, minBearing, maxAxes, result);
        ExpectSequence(failures, "exactly 20% further still counts", result, 0, 1);

        SelectAxes(
            new[] { 9000f, 20000f, 21000f, 22000f }, new[] { 0f, 0f, 120f, 240f }, radius, tolerance, minBearing, maxAxes, result);
        ExpectSequence(failures, "the best seed is chosen, not the nearest candidate", result, 1, 2, 3);

        SelectAxes(
            new[] { 10000f, 10000f, 10000f, 10000f },
            new[] { 0f, 90f, 180f, 270f },
            radius,
            tolerance,
            minBearing,
            maxAxes,
            result);
        if (result.Count != 3)
        {
            failures.Add($"no more than three axes, however many candidates qualify: expected 3 entries, got {result.Count}");
        }

        SelectAxes(new[] { 26000f, 30000f }, new float[] { 0f, 0f }, radius, tolerance, minBearing, maxAxes, result);
        ExpectSequence(failures, "nothing within the axis radius is an axis, so the attack waits", result);

        SelectAxes(new[] { 10000f, 10000f }, new[] { 350f, 10f }, radius, tolerance, minBearing, maxAxes, result);
        ExpectSequence(failures, "bearings are compared the short way round", result, 0);
    }

    /// <summary>Groups wait this long for stragglers, then go without them (design SS3, 4 min).</summary>
    private const float AssaultFormUpTimeoutSeconds = 240f;

    /// <summary>A point within this of a group's route is driven through and flipped in passing
    /// (design SS3).</summary>
    private const float EnRouteFlipMeters = 2000f;

    /// <summary>Counted as "arrived" at a release point or the target's hold point — tight, since a
    /// release point is a place to stop, not a ring to spread out on.</summary>
    private const float AssaultArrivedMeters = 400f;

    /// <summary>
    /// One offensive at a time per commander (kept simple; design SS3 does not ask for more). Opens
    /// an <see cref="CommanderMissionKind.Attack"/> mission when a target, its axes and enough spare
    /// platoons all line up; otherwise waits quietly for a future review.
    /// </summary>
    private void PlanOffensive(FactionHQ hq, OperationsState state)
    {
        TryOpenAttack(hq, state, minPlatoons: 1, logVerb: "forms up to attack");
    }

    /// <summary>
    /// Opens an attack on the best available target, sized at least <paramref name="minPlatoons"/>
    /// (design SS3's pressure clock forces at least two, ignoring ideal sizing; an ordinary opening
    /// asks for one). Shared by <see cref="PlanOffensive"/> and the pressure clock rather than
    /// duplicated (Reuse rule 5) — resets <c>state.Pressure</c> on a successful open either way,
    /// since any attack launching relieves the patience that was or was not driving it.
    /// </summary>
    private bool TryOpenAttack(FactionHQ hq, OperationsState state, int minPlatoons, string logVerb)
    {
        // The allowance, not the first attack found (concurrent-attacks_20260918 decision A). This
        // gate used to return false the moment ANY attack existed, which is why the match of
        // 2026-09-18 showed one attack against 48 pickets and 11 forward bases however large the army
        // grew, and why the ground war generated no close-support work for the wing to do.
        int openAttacks = 0;
        for (int i = 0; i < state.Missions.Count; i++)
        {
            if (state.Missions[i].Kind == CommanderMissionKind.Attack)
            {
                openAttacks++;
            }
        }

        if (openAttacks >= MaxConcurrentAttacks(
                state.Platoons.Count, CommanderSettings.AttacksPerPlatoon, CommanderSettings.MaxAttacks))
        {
            return false;
        }

        calledUpPlatoons.Clear();
        CollectOffensiveTargets(hq, state, offensiveTargets);
        CommanderStrategicPoint? targetPoint = null;
        Airbase? targetAirbase = null;
        GlobalPosition target = default;
        List<CommanderAssaultGroup> axes = new();
        bool planned = false;
        for (int i = 0; i < offensiveTargets.Count && i < MaxOffensiveTargetAttempts; i++)
        {
            OffensiveTarget candidate = offensiveTargets[i];
            GlobalPosition position = candidate.Point != null
                ? candidate.Point.Position
                : candidate.Airbase!.center.GlobalPosition();
            if (TryPlanAxes(hq, position, axes) && axes.Count > 0)
            {
                targetPoint = candidate.Point;
                targetAirbase = candidate.Airbase;
                target = position;
                planned = true;
                break;
            }
        }

        offensiveTargets.Clear();
        if (!planned)
        {
            return false;
        }

        int spare = CountSparePlatoons(state);
        // Short of bodies, call up from the quiet rear (concurrent-attacks_20260918 decision C).
        // CountSparePlatoons counts only platoons with NO mission, which on the measured match was
        // one to three per commander while eight or nine sat on forward bases — so without this the
        // raised allowance would permit attacks nobody could man.
        if (spare < minPlatoons)
        {
            spare += CallUpQuietForwardBases(hq, state, minPlatoons - spare, label: null, calledUpPlatoons);
        }

        if (spare < minPlatoons)
        {
            return false;
        }

        int observed = EffectiveObserved(
            CountObserved(hq, target), GetObservedFloor(state, ObservedFloorKey(targetPoint, targetAirbase)));
        int platoonSize = Mathf.Max(1, CommanderSettings.OperationsPlatoonSize);
        int sized = PlatoonsForTarget(observed, platoonSize, targetPoint == null);
        int wanted = Mathf.Max(minPlatoons, Mathf.Min(spare, sized));

        string label = targetPoint != null ? targetPoint.Label : CommanderCaptureService.GetAirbaseLabel(targetAirbase);
        CommanderOperationsMission mission = new()
        {
            Kind = CommanderMissionKind.Attack,
            Point = targetPoint,
            TargetAirbase = targetAirbase,
            WantedPlatoons = wanted,
            Label = label,
        };
        mission.Axes.AddRange(axes);
        state.Missions.Add(mission);
        // The called-up platoons go straight onto this attack. Left unassigned, the forward-base
        // matching that runs before the attack matching in the same review found them standing on
        // their old base with no mission and sent them straight back (review H12).
        for (int i = 0; i < calledUpPlatoons.Count; i++)
        {
            AttachPlatoon(hq, calledUpPlatoons[i], mission, CommanderPlatoonState.Attacking);
        }

        calledUpPlatoons.Clear();

        state.Pressure = 0f;
        CommanderAiLog.Note(hq, $"{logVerb} {label} with {wanted} platoon(s) on {axes.Count} axis/axes.");
        // Source A (design.md, strike-packages_20260915 Section 1): every planned ground attack has a
        // strike package ahead of it, on the same target, and the attack's go-in waits for it. Opened
        // after the mission is in the list so the strike's own target read sees the attack that asked
        // for it. A strike already open — the clock's — is left alone rather than replaced: one
        // strike sortie per commander.
        OpenStrikeSortie(hq, state, targetPoint, targetAirbase, label, owner: mission);
        return true;
    }

    /// <summary>Puts every still-ungrouped assigned platoon onto an axis, round robin, doubling up
    /// once every axis has at least one platoon (more platoons than axes is expected once sizing
    /// exceeds three).</summary>
    private static void AssignPlatoonsToAxes(CommanderOperationsMission mission)
    {
        int templateCount = mission.Axes.Count;
        if (templateCount == 0)
        {
            return;
        }

        int axisIndex = 0;
        for (int a = 0; a < mission.Axes.Count && HasUnassignedAttacker(mission); a++)
        {
            if (mission.Axes[a].Platoon == null)
            {
                mission.Axes[a].Platoon = TakeUnassignedAttacker(mission);
            }
        }

        CommanderPlatoon? extra;
        while ((extra = TakeUnassignedAttacker(mission)) != null)
        {
            CommanderAssaultGroup template = mission.Axes[axisIndex % templateCount];
            mission.Axes.Add(new CommanderAssaultGroup
            {
                FormUpPoint = template.FormUpPoint,
                ReleasePoint = template.ReleasePoint,
                Platoon = extra,
            });
            axisIndex++;
        }
    }

    private static bool HasUnassignedAttacker(CommanderOperationsMission mission) => TakeUnassignedAttacker(mission) != null;

    private static CommanderPlatoon? TakeUnassignedAttacker(CommanderOperationsMission mission)
    {
        for (int i = 0; i < mission.Assigned.Count; i++)
        {
            CommanderPlatoon platoon = mission.Assigned[i];
            bool hasGroup = false;
            for (int a = 0; a < mission.Axes.Count; a++)
            {
                if (ReferenceEquals(mission.Axes[a].Platoon, platoon))
                {
                    hasGroup = true;
                    break;
                }
            }

            if (!hasGroup)
            {
                return platoon;
            }
        }

        return null;
    }

    /// <summary>A control point within <see cref="EnRouteFlipMeters"/> of the straight line from
    /// <paramref name="from"/> to <paramref name="to"/>, not already held by <paramref name="hq"/> —
    /// design SS3's "points within 2 km of the route are flipped in passing". Null when none
    /// qualify.</summary>
    private static CommanderStrategicPoint? FindEnRoutePoint(FactionHQ hq, GlobalPosition from, GlobalPosition to)
    {
        IReadOnlyList<CommanderStrategicPoint>? points = CommanderStrategicPointService.Instance?.Points;
        if (points == null)
        {
            return null;
        }

        float flipSquared = EnRouteFlipMeters * EnRouteFlipMeters;
        for (int i = 0; i < points.Count; i++)
        {
            CommanderStrategicPoint point = points[i];
            if (!StrategicPointKinds.IsControlPoint(point.Kind) || ReferenceEquals(point.GetOwner(), hq))
            {
                continue;
            }

            if (CommanderStrategicPointService.SegmentDistanceSquared(point.Position, from, to) <= flipSquared)
            {
                return point;
            }
        }

        return null;
    }

    /// <summary>
    /// Every open attack: plans axes once a target is chosen, assigns committed platoons to them,
    /// routes each group (flipping onto an en-route point for a tick when one lies on the way),
    /// launches once every group has arrived or the form-up timeout fires, and resolves the outcome
    /// once launched (design SS3).
    /// </summary>
    private void UpdateAttacks(FactionHQ hq, OperationsState state)
    {
        for (int m = state.Missions.Count - 1; m >= 0; m--)
        {
            CommanderOperationsMission mission = state.Missions[m];
            if (mission.Kind != CommanderMissionKind.Attack)
            {
                continue;
            }

            GlobalPosition target = mission.Point != null
                ? mission.Point.Position
                : mission.TargetAirbase != null ? mission.TargetAirbase.center.GlobalPosition() : default;

            if (mission.Axes.Count == 0 && !TryPlanAxes(hq, target, mission.Axes))
            {
                continue;
            }

            AssignPlatoonsToAxes(mission);

            // An axis whose platoon was wiped out keeps a dissolved (empty) platoon; drop it so the
            // axis can be re-manned, and if the whole force died before launch the attack has
            // failed rather than sitting unlaunched forever — with one attack allowed at a time,
            // a mission that never resolved would have blocked every attack after it.
            bool anyManned = false;
            for (int a = 0; a < mission.Axes.Count; a++)
            {
                CommanderAssaultGroup group = mission.Axes[a];
                // A platoon that has left this attack (withdrawn under strength, or taken by
                // another mission since) is dropped from its axis too. ReleaseFromMission only
                // clears Assigned, so the axis used to keep steering the remnant back toward the
                // target, and resolving the attack could reset a platoon another mission now owns
                // (review H11).
                if (group.Platoon != null
                    && (group.Platoon.Members.Count == 0 || !ReferenceEquals(group.Platoon.Mission, mission)))
                {
                    group.Platoon = null;
                    // A dead axis must not keep the form-up alive: its arrival was what let the
                    // timeout launch a mission with nobody left on it.
                    group.Arrived = false;
                }

                anyManned |= group.Platoon != null;
            }

            // Every platoon was lost after the force had started forming up (some axis had
            // arrived) and nothing is left on any axis: the attack has failed, not paused. A brand
            // new mission that has not been manned yet has no arrival stamp and is left to fill.
            // AssignPlatoons already drops dead platoons from Assigned, so that list is empty here.
            if (!anyManned && !mission.Launched && mission.FirstGroupArrivedAt >= 0f)
            {
                CommanderAiLog.Note(hq, $"{mission.Label}: every platoon was lost forming up; attack abandoned.");
                ResolveAttack(hq, state, mission, success: false);
                state.Missions.RemoveAt(m);
                continue;
            }

            if (!mission.Launched)
            {
                bool allArrived = true;
                // B1 fix: an axis with no platoon (or an empty one) is not "arrived", it is not
                // manned at all. Previously this `continue` skipped straight past `allArrived` with
                // no effect on it, so an attack whose axes had never been assigned a single platoon
                // still read as "every axis is up" and launched with nothing on it.
                bool everyAxisManned = true;
                for (int a = 0; a < mission.Axes.Count; a++)
                {
                    CommanderAssaultGroup group = mission.Axes[a];
                    if (group.Platoon == null || group.Platoon.Members.Count == 0)
                    {
                        allArrived = false;
                        everyAxisManned = false;
                        continue;
                    }

                    Unit? leader = group.Platoon.Leader;
                    if (leader == null || leader.disabled)
                    {
                        allArrived = false;
                        continue;
                    }

                    GlobalPosition from = leader.transform.GlobalPosition();
                    GlobalPosition next = FindEnRoutePoint(hq, from, group.ReleasePoint)?.Position ?? group.ReleasePoint;
                    group.Platoon.Objective = next;
                    if (!next.Equals(group.ReleasePoint))
                    {
                        allArrived = false;
                        continue;
                    }

                    float leaderDistance = CommanderGameAccess.HorizontalDistance(
                        leader.transform.position, group.ReleasePoint.ToLocalPosition());
                    int membersInCohesion = CountInCohesion(group.Platoon);
                    group.Arrived = HasArrived(leaderDistance, AssaultArrivedMeters, membersInCohesion, group.Platoon.Members.Count);
                    allArrived &= group.Arrived;
                }

                // B1 fix: launching also requires a mission actually holding platoons and every axis
                // carrying one — belt and braces alongside `allArrived` above, which already goes
                // false the moment any axis is unmanned.
                if (allArrived && everyAxisManned && mission.Axes.Count > 0 && mission.Assigned.Count > 0)
                {
                    if (mission.FirstGroupArrivedAt < 0f)
                    {
                        mission.FirstGroupArrivedAt = Time.time;
                    }

                    // Approval 1 (2026-09-13): the attack holds its go-in while its CAS sortie has
                    // nothing on station, but never past the same form-up timeout the straggler wait
                    // uses — one bounded wait, no second clock. The moment the hold ends for any
                    // reason (CAS overhead, CAS unreachable in time, wait run out) the attack goes
                    // in exactly as before.
                    // The strike ahead of the attack (design.md, strike-packages_20260915
                    // Section 5) is read first, on the same bounded clock the CAS hold already
                    // uses: an attack whose strike has gone in follows it onto the target, and one
                    // whose strike was abandoned or never opened waits for the CAS hold and the
                    // timeout exactly as it did before.
                    float forming = Time.time - mission.FirstGroupArrivedAt;
                    bool strikeDelivered = StrikeDeliveredFor(state, mission);
                    bool strikeAbandoned = !strikeDelivered && FindStrikeFor(state, mission) == null;
                    if (!AttackMayGoIn(strikeDelivered, strikeAbandoned, forming, AssaultFormUpTimeoutSeconds))
                    {
                        if (!mission.StrikeHoldLogged)
                        {
                            mission.StrikeHoldLogged = true;
                            CommanderAiLog.Note(
                                hq, $"{mission.Label}: holding at the release point until the strike has gone in.");
                        }
                    }
                    else if (HoldsForCas(state, hq, mission))
                    {
                        if (!mission.CasHoldLogged)
                        {
                            mission.CasHoldLogged = true;
                            CommanderAiLog.Note(hq, $"{mission.Label}: holding at the release point until CAS is overhead.");
                        }
                    }
                    else
                    {
                        mission.Launched = true;
                        CommanderAiLog.Note(
                            hq,
                            strikeDelivered
                                ? $"{mission.Label}: goes in behind the strike."
                                : forming >= AssaultFormUpTimeoutSeconds
                                    ? $"{mission.Label}: goes in after {AssaultFormUpTimeoutSeconds:0} s without the strike."
                                    : $"{mission.Label}: every axis is up; going in.");
                    }
                }
                else
                {
                    bool anyArrived = false;
                    for (int a = 0; a < mission.Axes.Count; a++)
                    {
                        if (mission.Axes[a].Arrived)
                        {
                            anyArrived = true;
                            break;
                        }
                    }

                    if (anyArrived)
                    {
                        if (mission.FirstGroupArrivedAt < 0f)
                        {
                            mission.FirstGroupArrivedAt = Time.time;
                        }
                        else if (Time.time - mission.FirstGroupArrivedAt >= AssaultFormUpTimeoutSeconds)
                        {
                            mission.Launched = true;
                            CommanderAiLog.Note(hq, $"{mission.Label}: out of patience waiting for the other axis; going in.");
                        }
                    }
                }
            }

            if (mission.Launched)
            {
                GlobalPosition holdPoint = mission.Point != null
                    ? mission.Point.Position
                    : CommanderCaptureService.GetHoldPointFor(mission.TargetAirbase!);

                int totalStrength = 0;
                int totalEstablishment = 0;
                for (int a = 0; a < mission.Axes.Count; a++)
                {
                    CommanderPlatoon? platoon = mission.Axes[a].Platoon;
                    if (platoon == null)
                    {
                        continue;
                    }

                    totalStrength += platoon.Members.Count;
                    totalEstablishment += platoon.Establishment;
                    Unit? leader = platoon.Leader;
                    GlobalPosition from = leader != null && !leader.disabled
                        ? leader.transform.GlobalPosition()
                        : platoon.Objective;
                    GlobalPosition next = FindEnRoutePoint(hq, from, holdPoint)?.Position ?? holdPoint;
                    platoon.Objective = next;
                }

                bool taken = mission.Point != null
                    ? ReferenceEquals(mission.Point.GetOwner(), hq)
                    : ReferenceEquals(mission.TargetAirbase?.CurrentHQ, hq);

                if (taken)
                {
                    ResolveAttack(hq, state, mission, success: true);
                    state.Missions.RemoveAt(m);
                }
                // Nothing left at all is a failure too: with dead axes nulled above, a wiped-out
                // launched attack has zero establishment and the ratio test alone never fired.
                else if (totalStrength == 0 || (totalEstablishment > 0 && AttackHasFailed(totalStrength, totalEstablishment)))
                {
                    ResolveAttack(hq, state, mission, success: false);
                    state.Missions.RemoveAt(m);
                }
            }
        }
    }

    /// <summary>
    /// Closes a launched attack: taken -> the attacking platoons become forward bases at the new
    /// front; failed -> every group withdraws to its release point (design SS3), and this target's
    /// observed-enemy floor (B5) is raised to at least what beat it, so the next attempt on the same
    /// target is not sized as if this one never happened. Either way the mission's platoons are
    /// released so <c>AssignPlatoons</c> picks them up fresh next review.
    /// </summary>
    private void ResolveAttack(FactionHQ hq, OperationsState state, CommanderOperationsMission mission, bool success)
    {
        if (!success)
        {
            object? key = ObservedFloorKey(mission.Point, mission.TargetAirbase);
            if (key != null)
            {
                GlobalPosition target = mission.Point != null
                    ? mission.Point.Position
                    : mission.TargetAirbase!.center.GlobalPosition();
                int beatenBy = CountObserved(hq, target);
                state.ObservedFloors[key] = Mathf.Max(GetObservedFloor(state, key), beatenBy);
            }
        }

        for (int a = 0; a < mission.Axes.Count; a++)
        {
            CommanderPlatoon? platoon = mission.Axes[a].Platoon;
            if (platoon == null || !ReferenceEquals(platoon.Mission, mission))
            {
                continue;
            }

            platoon.Mission = null;
            // An attack that resolves ends any reinforcement answer with it (addendum
            // 2026-09-14 §3); the mission object is about to leave the list entirely.
            platoon.ReinforcesLabel = string.Empty;
            if (success)
            {
                platoon.State = CommanderPlatoonState.Moving;
                if (mission.Point != null)
                {
                    platoon.Objective = mission.Point.Position;
                }
            }
            else if (platoon.Members.Count > 0)
            {
                platoon.State = CommanderPlatoonState.Withdrawing;
                platoon.Objective = mission.Axes[a].ReleasePoint;
            }
        }

        CommanderAiLog.Note(
            hq,
            success
                ? $"takes {mission.Label}; the attackers hold as its new forward base."
                : $"is beaten off at {mission.Label}; the survivors withdraw.");
    }

    /// <summary>Design SS2/SS3: "+1/min", which is what makes the threshold
    /// <c>PressureIntervalMinutes</c>.</summary>
    private const float PressurePerMinute = 1f;

    /// <summary>Design SS3 says "bonus per point lost" without a number; two minutes' worth, so
    /// three losses pull an attack half a review forward. Planner-chosen.</summary>
    private const float PressurePerPointLost = 2f;

    /// <summary>Design SS3 says "bonus … per enemy point closer to my base than theirs" without a
    /// number; one minute's worth per point. Planner-chosen.</summary>
    private const float PressurePerEncroachingPoint = 1f;

    /// <summary>Pure, for the self-check. One review's worth of pressure.</summary>
    internal static float StepPressure(
        float pressure, float deltaMinutes, int pointsLostSinceLastReview, int enemyPointsCloserToMyBase)
    {
        return pressure
            + deltaMinutes * PressurePerMinute
            + pointsLostSinceLastReview * PressurePerPointLost
            + enemyPointsCloserToMyBase * PressurePerEncroachingPoint;
    }

    /// <summary>Pure, for the self-check. The clock forces an attack once accrued pressure reaches
    /// the interval's worth of minutes.</summary>
    internal static bool PressureForcesAttack(float pressure, float pressureIntervalMinutes)
    {
        return pressure >= pressureIntervalMinutes * PressurePerMinute;
    }

    private static int CountHeldControlPoints(FactionHQ hq, OperationsState state)
    {
        int count = 0;
        for (int i = 0; i < state.RankedPoints.Count; i++)
        {
            if (ReferenceEquals(state.RankedPoints[i].Point.GetOwner(), hq))
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>Enemy-held control points closer to my territory centre than to their own owner's
    /// — design SS3's "enemy point closer to my base than theirs".</summary>
    private static int CountEncroachingEnemyPoints(FactionHQ hq, OperationsState state)
    {
        GlobalPosition myTerritory = CommanderCaptureService.GetTerritoryCenter(hq);
        int count = 0;
        for (int i = 0; i < state.RankedPoints.Count; i++)
        {
            CommanderStrategicPoint point = state.RankedPoints[i].Point;
            FactionHQ? owner = point.GetOwner();
            if (owner == null || ReferenceEquals(owner, hq))
            {
                continue;
            }

            float distanceToMyBase = CommanderGameAccess.HorizontalDistance(point.Position.AsVector3(), myTerritory.AsVector3());
            GlobalPosition ownerTerritory = CommanderCaptureService.GetTerritoryCenter(owner);
            float distanceToOwnBase = CommanderGameAccess.HorizontalDistance(point.Position.AsVector3(), ownerTerritory.AsVector3());
            if (distanceToMyBase < distanceToOwnBase)
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>
    /// Steps this HQ's pressure clock once per review and, once it reaches
    /// <c>PressureIntervalMinutes</c>, launches the best available attack with at least two
    /// platoons regardless of ideal sizing (design SS3) — resetting on a successful open happens
    /// inside <see cref="TryOpenAttack"/> itself.
    /// </summary>
    private void UpdatePressure(FactionHQ hq, OperationsState state)
    {
        int heldNow = CountHeldControlPoints(hq, state);
        int pointsLost = state.LastPointsHeld < 0 ? 0 : Mathf.Max(0, state.LastPointsHeld - heldNow);
        int encroaching = CountEncroachingEnemyPoints(hq, state);
        float deltaMinutes = ReviewIntervalSeconds / 60f;
        state.Pressure = StepPressure(state.Pressure, deltaMinutes, pointsLost, encroaching);
        state.LastPointsHeld = heldNow;

        if (PressureForcesAttack(state.Pressure, CommanderSettings.OperationsPressureIntervalMinutes))
        {
            TryOpenAttack(hq, state, minPlatoons: 2, logVerb: "is out of patience: attacks");
        }
    }

    /// <summary>The pressure clock at the default 12 minutes.</summary>
    private static void CheckPressure(List<string> failures)
    {
        Expect(failures, "one 30 s review adds half a minute of pressure", StepPressure(0f, 0.5f, 0, 0), 0.5f);

        float accumulated = 0f;
        for (int i = 0; i < 24; i++)
        {
            accumulated = StepPressure(accumulated, 0.5f, 0, 0);
        }

        Expect(failures, "twenty-four reviews accumulate twelve minutes", accumulated, 12f);
        Expect(failures, "twelve quiet minutes force an attack", PressureForcesAttack(12f, 12f), true);
        Expect(failures, "eleven and a half minutes do not", !PressureForcesAttack(11.5f, 12f), true);
        Expect(failures, "a point lost is worth two minutes", StepPressure(0f, 0.5f, 1, 0), 2.5f);
        Expect(
            failures, "three enemy points on my side of the map are worth three minutes", StepPressure(0f, 0.5f, 0, 3), 3.5f);

        float interval = CommanderSettings.OperationsPressureIntervalMinutes;
        float rate = PressurePerMinute;
        Expect(
            failures,
            "the pressure clock can still reach its threshold; check the Operations section of the config",
            interval * rate > 0f,
            true);
    }

    // ---- Deliberate strikes (design.md, strike-packages_20260915 Section 1) ----

    /// <summary>
    /// Whether the strike clock is due to open a deliberate strike (design Section 1, source B).
    /// Never while a ground attack is open: that attack opens a strike of its own, and two strike
    /// sorties at once is the one thing the design forbids. A negative
    /// <paramref name="minutesSinceLast"/> means the commander has never struck at all, which is due
    /// immediately — a match should not have to wait six minutes for the wing's first deliberate
    /// act. Exactly at the interval counts as due, the convention the rest of the mod uses. Pure,
    /// for the self-check.
    /// </summary>
    internal static bool StrikeDue(float minutesSinceLast, float intervalMinutes, bool attackOpen)
    {
        if (attackOpen)
        {
            return false;
        }

        return minutesSinceLast < 0f || minutesSinceLast >= intervalMinutes;
    }

    /// <summary>Whether one strike target beats the best found so far — strictly greater, so the
    /// first of two equally valuable points wins and the choice never flickers between them review
    /// after review. Pure, for the self-check.</summary>
    internal static bool StrikeTargetBeats(float value, float bestValue)
    {
        return value > bestValue;
    }

    /// <summary>
    /// The enemy-held point worth striking (design Section 1): the highest-ranked one
    /// (<c>RankPoints</c>'s income-times-closeness value, the same ranking every other planner reads)
    /// that is within <c>StrikeRangeMeters</c> of an airbase this commander holds and is not still on
    /// its post-strike cooldown. False when nothing qualifies — the clock keeps running and the next
    /// review asks again.
    /// </summary>
    private bool TryChooseStrikeTarget(FactionHQ hq, OperationsState state, out CommanderStrategicPoint? point)
    {
        point = null;

        // Nothing on the roster can attack ground from a strip this commander holds: a strike sortie
        // opened here could never be flown, and an unfillable sortie in the demand queue is exactly
        // what starves the rest of the wing (the ARAD gate's own rule).
        if (!CommanderEnemyCommanderService.HasRoleCandidate(hq, CommanderEnemyCommanderService.AirRole.Strike))
        {
            return false;
        }

        float bestValue = float.MinValue;
        for (int i = 0; i < state.RankedPoints.Count; i++)
        {
            CommanderRankedPoint ranked = state.RankedPoints[i];
            FactionHQ? owner = ranked.Point.GetOwner();
            if (owner == null || ReferenceEquals(owner, hq))
            {
                continue;
            }

            if (state.StrikeCooldownUntil.TryGetValue(ranked.Point, out float until) && Time.time < until)
            {
                continue;
            }

            if (NearestHeldAirbaseMeters(hq, ranked.Point.Position) > CommanderSettings.StrikeRangeMeters)
            {
                continue;
            }

            if (StrikeTargetBeats(ranked.Value, bestValue))
            {
                bestValue = ranked.Value;
                point = ranked.Point;
            }
        }

        return point != null;
    }

    /// <summary>How far the nearest airbase this commander holds is from a position, or
    /// <c>float.MaxValue</c> when it holds none. The form-up point's own base walk
    /// (<c>TryFindFormUpPoint</c>) answering "how far" instead of "which one".</summary>
    private static float NearestHeldAirbaseMeters(FactionHQ hq, GlobalPosition position)
    {
        float best = float.MaxValue;
        foreach (Airbase airbase in hq.GetAirbases())
        {
            if (airbase == null || airbase.disabled || airbase.center == null)
            {
                continue;
            }

            float distance = CommanderGameAccess.HorizontalDistance(
                airbase.center.GlobalPosition().AsVector3(), position.AsVector3());
            if (distance < best)
            {
                best = distance;
            }
        }

        return best;
    }

    /// <summary>
    /// Steps the strike clock once per review — <see cref="StepPressure"/>'s shape, one clock per
    /// commander — and opens a deliberate strike when it is due (design Section 1, source B). Called
    /// straight after <see cref="UpdatePressure"/>, so the attack the pressure clock may have just
    /// forced already counts as open and the two sources never both fire on one review.
    /// </summary>
    private void UpdateStrikeClock(FactionHQ hq, OperationsState state)
    {
        CloseFinishedStrike(hq, state);

        float deltaMinutes = ReviewIntervalSeconds / 60f;
        state.StrikeClockMinutes += deltaMinutes;

        bool attackOpen = false;
        for (int i = 0; i < state.Missions.Count; i++)
        {
            if (state.Missions[i].Kind == CommanderMissionKind.Attack)
            {
                attackOpen = true;
                break;
            }
        }

        float interval = Mathf.Max(0f, CommanderSettings.StrikeIntervalMinutes);
        float sinceLast = state.LastStrikeAt < 0f ? -1f : state.StrikeClockMinutes;
        if (state.StrikeSortie != null
            || !StrikeDue(sinceLast, interval, attackOpen)
            || !TryChooseStrikeTarget(hq, state, out CommanderStrategicPoint? point))
        {
            ReportStrikeClock(hq, state, attackOpen, interval);
            return;
        }

        OpenStrikeSortie(hq, state, point, targetAirbase: null, point!.Label, owner: null);
    }

    /// <summary>One line, at most once a minute and only when the whole number changes, saying how
    /// long the wing is from its next deliberate strike (design Section 6). Silent while a ground
    /// attack is open — that attack's own strike is the next one, and a countdown beside it would
    /// say the opposite.</summary>
    private static void ReportStrikeClock(FactionHQ hq, OperationsState state, bool attackOpen, float intervalMinutes)
    {
        if (attackOpen || state.StrikeSortie != null)
        {
            state.StrikeClockReported = -1;
            return;
        }

        int remaining = Mathf.Max(0, Mathf.CeilToInt(intervalMinutes - state.StrikeClockMinutes));
        if (remaining == state.StrikeClockReported)
        {
            return;
        }

        state.StrikeClockReported = remaining;
        CommanderAiLog.Note(hq, $"strike clock: next deliberate strike in {remaining} min.");
    }

    /// <summary>
    /// Opens the one strike sortie (design Section 1). Reads the target ONCE — defenders, tracked
    /// air defence, tracked hostile air, whether it is a base, whether the roster holds a bomber —
    /// and sizes the package from <see cref="StrikePackageFor"/> off those numbers. Nothing re-reads
    /// them afterwards: the package that was ordered is the package that flies.
    /// </summary>
    /// <param name="owner">The attack this package flies ahead of, or null for the DELIBERATE strike
    /// the clock opens when no attack is running. Added by concurrent-attacks_20260918: the guard
    /// below used to refuse whenever the commander had any package at all, so several attacks would
    /// have given the first one air and sent the rest in naked.</param>
    private void OpenStrikeSortie(
        FactionHQ hq,
        OperationsState state,
        CommanderStrategicPoint? point,
        Airbase? targetAirbase,
        string label,
        CommanderOperationsMission? owner)
    {
        bool ownerHasStrike = owner != null ? owner.StrikeSortie != null : state.StrikeSortie != null;
        if (ownerHasStrike || (point == null && targetAirbase == null))
        {
            return;
        }

        GlobalPosition center = point != null
            ? point.Position
            : CommanderCaptureService.GetHoldPointFor(targetAirbase!);

        int defenders = EffectiveObserved(
            CountObserved(hq, center), GetObservedFloor(state, ObservedFloorKey(point, targetAirbase)));
        // The same ring the ARAD clustering reads a belt in, so "air defence near the target" means
        // one thing in both places.
        bool airDefence = CountObservedAirDefence(hq, center) > 0;
        int hostileAir = CountHostileAirInRing(hq, center, ObservedRadiusMeters);
        bool hasBomber = CommanderEnemyCommanderService.HasBomberCandidate(hq);
        StrikePackage package = StrikePackageFor(defenders, airDefence, hostileAir, targetAirbase != null, hasBomber);

        // The escort is capped by what the sky has room for: the floor is a demand, not a licence to
        // exceed the airborne ceiling (design Section 2).
        int headroom = Mathf.Max(
            0, EffectiveAirborneCeiling(hq) - CommanderEnemyCommanderService.CountAirborne(hq));
        int escort = Mathf.Min(package.Escort, Mathf.Max(0, headroom));

        CommanderAirSortie sortie = new()
        {
            Kind = CommanderSortieKind.Strike,
            Point = point,
            TargetAirbase = targetAirbase,
            Center = center,
            StrikeWanted = package.Strike,
            EscortWanted = escort,
            EscortFloor = package.EscortFloor,
            AradWanted = package.Arad,
            BomberWanted = package.Bomber,
            StrikeScale = package.Scale,
            DefendersAtOrder = defenders,
            OpenedAt = Time.time,
            Wanted = package.Strike + package.Bomber,
            CapsWanted = escort,
            LastObserved = defenders,
            LastHostileAir = hostileAir,
            LastAirDefence = CountObservedAirDefence(hq, center),
            // A deliberate strike forms up like every other package: it is not answering a platoon
            // that is being shot at now.
            NoCapWait = false,
            GoneIn = false,
            // Never in contact, by kind (fix, 2026-09-15): the flag is a claim on the ground's
            // funding reserve, and a strike package would have held a third of the rung for the
            // whole twelve minutes it lives. See the kinded SortieIsInContact.
            InContact = SortieIsInContact(
                CommanderSortieKind.Strike,
                objectiveInContact: false,
                attackGoneIn: false,
                defenders,
                hostileAir),
            Label = label,
        };
        // The rotation band, straight (fix, 2026-09-15). It used to take the rotation's answer and
        // step it up one, clamped, for the design's "escorts take their strike element's band plus
        // one step" — but the strike element flies a CAS mission, which the game gives no station
        // height at all, so there was no band to stand above and the step was applied to the
        // package's own rotation slot. That mapped three slots onto two bands (0 and 1 both became
        // the top two) and never produced the low band: the 2026-09-15 match logged four strike
        // orders in a row at 7,500 m from both commanders. The escorts now take their turn in the
        // rotation like every other patrol, which is what makes the variety visible.
        AssignCapBand(ref state.StrikeCapBandCursor, sortie);

        if (owner != null)
        {
            owner.StrikeSortie = sortie;
        }
        else
        {
            state.StrikeSortie = sortie;
        }

        state.StrikeClockMinutes = 0f;
        state.StrikeClockReported = -1;
        CommanderAiLog.Note(
            hq,
            $"orders a strike on {label} ({DescribeStrikeScale(package.Scale)}: {defenders} defenders, "
                + $"{hostileAir} hostile air): {package.Strike} strike"
                + (package.Bomber > 0 ? $", {package.Bomber} bomber" : string.Empty)
                + $", {escort} escort"
                + (package.Arad > 0 ? ", suppression first" : string.Empty)
                + $"; CAP band {CapBandMetersFor(sortie.CapBand):0} m.");
    }

    /// <summary>The scale in the plain words the log line uses.</summary>
    private static string DescribeStrikeScale(CommanderStrikeScale scale)
    {
        return scale switch
        {
            CommanderStrikeScale.Hard => "hard",
            CommanderStrikeScale.Defended => "defended",
            _ => "light",
        };
    }

    /// <summary>
    /// Whether an attack that has formed up may go in (design Section 5). The strike ahead of it is
    /// what it waits for — but never past the form-up timeout it already waited under, and never at
    /// all when that strike was abandoned: one bounded clock, no second one, exactly as the CAS hold
    /// it sits beside. Pure, for the self-check.
    /// </summary>
    internal static bool AttackMayGoIn(
        bool strikeDelivered, bool strikeAbandoned, float secondsForming, float timeoutSeconds)
    {
        if (secondsForming >= timeoutSeconds)
        {
            return true;
        }

        return strikeDelivered && !strikeAbandoned;
    }

    /// <summary>The strike clock and the attack's go-in hold. Both are retunable into nonsense — an
    /// interval of zero opens a strike every review, a go-in rule that reads the wrong way holds an
    /// attack at its release point for ever — and neither says anything in the running game until a
    /// match has already been lost to it.</summary>
    private static void CheckStrikeClock(List<string> failures)
    {
        Expect(failures, "six minutes since the last strike is due", StrikeDue(6f, 6f, attackOpen: false), true);
        Expect(failures, "five minutes fifty-four seconds is not yet due", StrikeDue(5.9f, 6f, attackOpen: false), false);
        Expect(failures, "a commander that has never struck is due at once", StrikeDue(-1f, 6f, attackOpen: false), true);
        Expect(failures, "an open ground attack never lets the clock fire", StrikeDue(60f, 6f, attackOpen: true), false);
        Expect(failures, "an open attack holds even the first strike", StrikeDue(-1f, 6f, attackOpen: true), false);
        Expect(failures, "a higher-valued point beats the best so far", StrikeTargetBeats(10f, 9f), true);
        Expect(failures, "an equally valued point never displaces the one already chosen", StrikeTargetBeats(9f, 9f), false);
        Expect(failures, "a lower-valued point loses", StrikeTargetBeats(8f, 9f), false);
        Expect(failures, "the first candidate always beats the empty best", StrikeTargetBeats(0f, float.MinValue), true);

        Expect(failures, "an attack goes in behind its delivered strike", AttackMayGoIn(true, false, 10f, AssaultFormUpTimeoutSeconds), true);
        Expect(failures, "an attack whose strike was abandoned waits out the timeout", AttackMayGoIn(false, true, 10f, AssaultFormUpTimeoutSeconds), false);
        Expect(failures, "an abandoned strike still goes in at the timeout", AttackMayGoIn(false, true, AssaultFormUpTimeoutSeconds, AssaultFormUpTimeoutSeconds), true);
        Expect(failures, "an attack with no strike delivered yet waits", AttackMayGoIn(false, false, 10f, AssaultFormUpTimeoutSeconds), false);
        Expect(failures, "an attack with no strike at all still goes in at the timeout", AttackMayGoIn(false, false, AssaultFormUpTimeoutSeconds, AssaultFormUpTimeoutSeconds), true);
        Expect(
            failures,
            "a strike that was delivered and then abandoned does not hold the attack past the timeout",
            AttackMayGoIn(true, true, AssaultFormUpTimeoutSeconds, AssaultFormUpTimeoutSeconds),
            true);

        float interval = CommanderSettings.StrikeIntervalMinutes;
        float cooldown = CommanderSettings.StrikePointCooldownMinutes;
        float range = CommanderSettings.StrikeRangeMeters;
        Expect(failures, "the strike interval is positive; check the Operations section of the config", interval > 0f, true);
        Expect(
            failures,
            "the strike interval outlasts a review, or a strike would open every 30 s; check the Operations section of the config",
            interval * 60f > ReviewIntervalSeconds,
            true);
        Expect(
            failures,
            "a struck point rests longer than the gap between strikes, or the wing would bomb one point all match; check the Operations section of the config",
            cooldown >= interval,
            true);
        Expect(failures, "the strike range is positive; check the Operations section of the config", range > 0f, true);
    }
}
