using System.Collections.Generic;
using UnityEngine;

namespace GroundControlRts;

/// <summary>
/// The air fallback posture (design.md, air-fallback-posture_20260916; user decision 2026-09-16: "too
/// often we see a CAP escort fly straight at 5x enemy aircraft, rather than retreating and calling for
/// help and then re-engaging"). A sortie whose fixed-wing fighters are outnumbered by
/// <c>CommanderSettings.AirFallbackMargin</c> or more falls back toward the commander's nearest own
/// airbase, fights only in self-defence, raises its wanted fighters so the existing retask and buy
/// reinforce it, goes back in once it outnumbers the enemy by <c>AirReengageMargin</c>, and stands down
/// after <c>AirFallbackGiveUpMinutes</c> without relief.
/// </summary>
/// <remarks>
/// Nothing here flies an aeroplane and nothing here buys one. The posture writes two fields onto the
/// Air Command mission record of each bound airframe — a hold point that the route hook returns in
/// place of the route, and a self-defence radius that the target hook refuses anything beyond — and
/// raises the sortie's own <c>CapsWanted</c> and <c>InContact</c>, which the review's retask and the
/// buyer already read. It runs from the logistics watch's five-second clock, after the delivery
/// watch, because a fight the review sized thirty seconds ago can be lost in ten.
/// <para>
/// What is counted: on our side, the sortie's bound CAP fighters that are alive and fly like
/// aeroplanes; on theirs, tracked fixed-wing aircraft that carry a weapon, within the sizing ring of
/// the sortie's centre. Helicopters are on neither side of the sum and never fall back under this
/// rule (design Section 4.6): an attack helicopter's answer to fighters is the game's own threat
/// avoidance, not a 15 km transit toward the base. The attrition brake's fighter hold is not
/// bypassed — a call for help the brake refuses ends in the stand-down (documented limitation).
/// </para>
/// </remarks>
internal sealed partial class CommanderOperationsService
{
    /// <summary>Whether a sortie with <paramref name="ours"/> fighters up against
    /// <paramref name="hostiles"/> tracked fixed-wing combat aircraft falls back, pure: the hostiles
    /// exceed ours by the margin or more (user decision 2026-09-16: "hostiles exceed ours by 2+").
    /// A margin of zero or less turns the posture off rather than making every fight a fallback.</summary>
    internal static bool ShouldFallBack(int ours, int hostiles, int margin)
    {
        return margin > 0 && hostiles - ours >= margin;
    }

    /// <summary>Whether a sortie that has fallen back goes back in, pure: ours exceed the hostiles by
    /// the margin or more (user decision 2026-09-16: "re-engage only when we OUTNUMBER"). Read with a
    /// margin of one, parity is NOT enough.</summary>
    internal static bool ShouldReengage(int ours, int hostiles, int margin)
    {
        return ours - hostiles >= margin;
    }

    /// <summary>Whether a sortie falling back for <paramref name="secondsFallingBack"/> has waited
    /// long enough for reinforcements to stand down, pure. The delivery wait's own rule under the
    /// posture's name (one definition, <see cref="LiftWaitedTooLong"/>): exactly on the limit counts,
    /// a limit of zero or less never gives up, a wait that has not started never gives up.</summary>
    internal static bool FallbackGaveUp(float secondsFallingBack, float minutes)
    {
        return LiftWaitedTooLong(secondsFallingBack, minutes);
    }

    /// <summary>
    /// Whether a sortie holds short of its objective because of the air-defence belt over it, pure
    /// (design.md, air-survival-layer_20260916 Layer 3; user decision 2026-09-16: "heavy enemy air
    /// defence ahead with no strike/anti-radar aircraft going in or gone in nearby"). Two clauses and
    /// no more: the launchers over the objective are a belt worth suppressing, and nothing of ours has
    /// gone in to shoot at it.
    /// <para>
    /// "A belt worth suppressing" is <see cref="BeltWorthSuppressing"/> — deliberately the SAME
    /// question the suppression sortie is sized by (<see cref="AradWanted"/>), because a sortie may
    /// only wait for a sweep the wing would actually buy. Holding on a lower count than that, which
    /// is what this rule did for one day, is a five-minute wait ending in a stand-down. Below the
    /// threshold the sortie flies on and the aircraft's own threat dodging and bravery refusal keep
    /// it alive; see the rule's own summary.
    /// </para>
    /// </summary>
    internal static bool BeltHoldsSortie(int clusteredLaunchers, bool sweepIn, int clusterMinimum)
    {
        return BeltWorthSuppressing(clusteredLaunchers, clusterMinimum) && !sweepIn;
    }

    /// <summary>Tracked hostile air-defence positions and their belt numbers for ONE commander,
    /// rebuilt once at the top of each posture watch and read by every sortie in it. Static because
    /// one watch walks one commander at a time, and built once rather than per sortie because the
    /// clustering is a whole-map fact: twenty sorties asking the same question twenty times would
    /// walk the tracking database twenty times for one answer.</summary>
    private static readonly List<GlobalPosition> postureAirDefence = new();
    private static readonly List<int> postureAirDefenceCluster = new();
    private static int postureAirDefenceClusterCount;

    /// <summary>Rebuilds the belt picture for one commander: the same collect-and-cluster pass the
    /// suppression demand makes (<c>CommanderOperationsAirArad.cs</c>), so the hold and the sweep read
    /// one map, not two.</summary>
    private static void RefreshPostureBelts(FactionHQ hq)
    {
        CollectTrackedAirDefence(hq, postureAirDefence);
        postureAirDefenceClusterCount = BuildAirDefenceClusters(postureAirDefence, postureAirDefenceCluster);
    }

    /// <summary>
    /// The largest BELT near <paramref name="center"/>: the most launchers any one cluster has, among
    /// the clusters with a launcher inside <paramref name="radiusMeters"/>. A cluster is counted
    /// whole even when only part of it is inside the ring — a belt half in reach is still that belt.
    /// Counting clustered launchers rather than everything in a ring is what makes the number mean
    /// something: two launchers thirty kilometres apart are two lone vehicles, not a belt.
    /// </summary>
    private static int LargestBeltNear(GlobalPosition center, float radiusMeters)
    {
        Vector3 at = center.AsVector3();
        int largest = 0;
        for (int cluster = 0; cluster < postureAirDefenceClusterCount; cluster++)
        {
            int size = 0;
            bool near = false;
            for (int i = 0; i < postureAirDefence.Count; i++)
            {
                if (postureAirDefenceCluster[i] != cluster)
                {
                    continue;
                }

                size++;
                near = near
                    || CommanderGameAccess.HorizontalDistance(at, postureAirDefence[i].AsVector3()) <= radiusMeters;
            }

            if (near && size > largest)
            {
                largest = size;
            }
        }

        return largest;
    }

    /// <summary>
    /// Whether the belt hold applies to this sortie at all, pure. A patrol with no strike element of
    /// its own is exempt: fighters orbiting above a belt are the SAM operator's problem, not the
    /// commander's, and holding them back would leave the thing they are covering uncovered for the
    /// sake of a threat they were never flying into. Everything with something to deliver — close air
    /// support, a strike package, the suppression sortie itself — holds.
    /// </summary>
    internal static bool BeltHoldApplies(CommanderSortieKind kind, int strikeAirframesBound)
    {
        return kind != CommanderSortieKind.Cap || strikeAirframesBound > 0;
    }

    /// <summary>How many fighters a falling-back sortie asks the review for, pure: enough to
    /// outnumber the hostiles by the re-engage margin, so the reinforcement that arrives is the one
    /// that lets it go back in rather than one that leaves it still short.</summary>
    internal static int ReinforcementWanted(int hostiles, int reengageMargin)
    {
        return Mathf.Max(0, hostiles + reengageMargin);
    }

    /// <summary>
    /// Where a falling-back sortie's fighters hold, pure: <paramref name="meters"/> from the sortie
    /// centre along the horizontal line toward the base, at the centre's own altitude. Never past the
    /// base — a base nearer than the distance yields the base's own ground — and a base standing on
    /// the centre yields the centre, so the fighters still have somewhere to hold.
    /// </summary>
    internal static GlobalPosition FallbackPoint(GlobalPosition center, GlobalPosition basePosition, float meters)
    {
        Vector3 toBase = basePosition - center;
        toBase.y = 0f;
        float distance = toBase.magnitude;
        if (distance <= 1f || meters <= 0f)
        {
            return center;
        }

        return center + toBase.normalized * Mathf.Min(meters, distance);
    }

    /// <summary>
    /// Whether this airframe counts as a fixed-wing combat aircraft for the posture's sum, on either
    /// side of it: alive, flown like an aeroplane (no rotary pilot — the tiltwing counts as rotary,
    /// as it does everywhere else in the wing), and carrying at least one station that is not cargo.
    /// One definition for our fighters and their raiders, so a helicopter or a transport is left out
    /// of both sides of the same sum (Reuse rule 4).
    /// </summary>
    internal static bool IsFixedWingCombatAircraft(Aircraft? aircraft)
    {
        if (aircraft == null || aircraft.disabled || aircraft.pilots == null)
        {
            return false;
        }

        for (int i = 0; i < aircraft.pilots.Length; i++)
        {
            if (aircraft.pilots[i] != null && CommanderAirCommandService.IsRotaryPilot(aircraft.pilots[i]))
            {
                return false;
            }
        }

        List<WeaponStation>? stations = aircraft.weaponStations;
        if (stations == null)
        {
            return false;
        }

        for (int i = 0; i < stations.Count; i++)
        {
            if (stations[i] != null && !stations[i].Cargo)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Bound airframes of one element that are actually in the air right now — alive and flying
    /// like aeroplanes. The one definition of "up" on our side of every sum (Reuse rule 5,
    /// generalised 2026-09-16 from <see cref="CountFightersUp"/> when the posture needed the same
    /// count over a sortie's strike element as well as over its escort).
    /// </summary>
    internal static int CountFixedWingUp(List<Aircraft>? bound)
    {
        if (bound == null)
        {
            return 0;
        }

        int up = 0;
        for (int i = 0; i < bound.Count; i++)
        {
            if (IsFixedWingCombatAircraft(bound[i]))
            {
                up++;
            }
        }

        return up;
    }

    /// <summary>Fighters of a sortie that are actually in the air right now — the bound CAP
    /// airframes that are alive and fly like aeroplanes. Moved here from <c>CountLiftEscortsUp</c>
    /// (Reuse rule 5): the lift launch gate, its "escort N of M up" line and the posture all count
    /// the same fighters, and the lift's name still forwards here. The ESCORT element only — the
    /// lift gate's "escort 2 of 3 up" must never start counting the flight it is escorting.</summary>
    internal static int CountFightersUp(CommanderAirSortie? sortie)
    {
        return sortie == null ? 0 : CountFixedWingUp(sortie.Caps);
    }

    /// <summary>
    /// Fighters of a cover that are up AND ahead of the load they are covering (user instruction,
    /// 2026-09-17), measured against <paramref name="landingZone"/> — the ground the load is actually
    /// flying to. The same escort element <see cref="CountFightersUp"/> walks and the same
    /// <see cref="IsFixedWingCombatAircraft"/> test decides what counts as a fighter, so the launch
    /// gate's two numbers are always counted over one list; only the pure
    /// <see cref="CommanderOperationsService.EscortIsAhead"/> test is added on top.
    /// <para>A rotary escort is not counted here for the same reason it is not counted as "up": a
    /// cover element is fixed-wing by construction. <paramref name="loadToLandingZoneMeters"/> is how
    /// far the load itself still has to fly, which at the launch gate is its airbase's distance to
    /// the landing zone.</para>
    /// </summary>
    internal static int CountFightersAhead(
        CommanderAirSortie? sortie,
        GlobalPosition landingZone,
        float loadToLandingZoneMeters,
        float marginMeters)
    {
        List<Aircraft>? bound = sortie?.Caps;
        if (bound == null)
        {
            return 0;
        }

        int ahead = 0;
        for (int i = 0; i < bound.Count; i++)
        {
            Aircraft fighter = bound[i];
            if (!IsFixedWingCombatAircraft(fighter))
            {
                continue;
            }

            // GlobalPosition throughout, never transform.position: the floating origin makes a raw
            // transform position a different number from the one the landing zone is quoted in.
            float toLandingZone = CommanderGameAccess.HorizontalDistance(
                fighter.transform.GlobalPosition().AsVector3(), landingZone.AsVector3());
            if (EscortIsAhead(
                    !CommanderAirCommandService.IsOnDeck(fighter),
                    toLandingZone,
                    loadToLandingZoneMeters,
                    marginMeters))
            {
                ahead++;
            }
        }

        return ahead;
    }

    /// <summary>
    /// A sortie's STRENGTH for the posture, pure (user decision 2026-09-16): the aircraft assigned
    /// to it that are up — its escort and its strike element together, the same two lists the
    /// hostile count already measures its rings from and the same two the posture stamps.
    /// <para>
    /// Nothing here asks WHERE an aircraft is, and nothing here asks whether the sortie is holding.
    /// That is the whole point. Until 2026-09-16 the count was "our fighters near the objective, or
    /// near the hold point while the sortie is holding", so the state being decided switched the
    /// clause that decided it: the 2026-09-16 match logged 1,012 complete fall-back/re-engage
    /// cycles across 66 sorties, our own count jumping by a median of 13 at every flip while the
    /// hostile count did not move — because a hold point walked back onto our own airbase drew a
    /// 20 km ring round every fighter parked, orbiting or rearming at home. Ninety-six sorties
    /// reported help they did not have and ended with every aircraft they really had dead. See
    /// <c>conductor/designs/2026-09-16-sortie-standdown-investigation.md</c>.
    /// </para>
    /// </summary>
    internal static int SortieStrength(int fightersUp, int strikeUp)
    {
        return Mathf.Max(0, fightersUp) + Mathf.Max(0, strikeUp);
    }

    /// <summary>Whether a hostile counts against a sortie, pure (user decision 2026-09-16): within
    /// the posture ring of the objective, or within it of the nearest of the sortie's own fighters.
    /// <paramref name="nearestFighterMeters"/> is a very large number when no fighter is up.</summary>
    internal static bool HostileCountsAgainstSortie(float toObjectiveMeters, float nearestFighterMeters, float ringMeters)
    {
        return toObjectiveMeters <= ringMeters || nearestFighterMeters <= ringMeters;
    }

    /// <summary>Scratch for the fighters' positions during one hostile count; static because one
    /// watch counts one sortie at a time.</summary>
    private static readonly List<Vector3> postureFighterPositions = new();

    /// <summary>
    /// Hostile fixed-wing combat aircraft that count against <paramref name="sortie"/>: each tracked
    /// one within <c>AirPostureRingMeters</c> of the objective OR of any of the sortie's own live
    /// fighters, counted once. One pass over the tracking database, the same filter the 8 km ring
    /// count uses (<see cref="IsTrackedHostileAircraft"/>).
    /// </summary>
    private static int CountHostileAirAgainstSortie(FactionHQ hq, CommanderAirSortie sortie)
    {
        postureFighterPositions.Clear();
        AddFighterPositions(sortie.Caps, postureFighterPositions);
        AddFighterPositions(sortie.Cas, postureFighterPositions);

        float ring = CommanderSettings.AirPostureRingMeters;
        Vector3 center = sortie.Center.AsVector3();
        float now = Time.timeSinceLevelLoad;
        int count = 0;
        foreach (KeyValuePair<PersistentID, TrackingInfo> entry in hq.trackingDatabase)
        {
            TrackingInfo info = entry.Value;
            if (!IsTrackedHostileAircraft(hq, info, now, fixedWingCombatOnly: true))
            {
                continue;
            }

            Vector3 at = info.lastKnownPosition.AsVector3();
            float nearestFighter = float.MaxValue;
            for (int f = 0; f < postureFighterPositions.Count; f++)
            {
                nearestFighter = Mathf.Min(nearestFighter, CommanderGameAccess.HorizontalDistance(postureFighterPositions[f], at));
            }

            if (HostileCountsAgainstSortie(CommanderGameAccess.HorizontalDistance(center, at), nearestFighter, ring))
            {
                count++;
            }
        }

        postureFighterPositions.Clear();
        return count;
    }

    private static void AddFighterPositions(List<Aircraft> bound, List<Vector3> into)
    {
        for (int i = 0; i < bound.Count; i++)
        {
            if (IsFixedWingCombatAircraft(bound[i]))
            {
                into.Add(bound[i].transform.GlobalPosition().AsVector3());
            }
        }
    }

    /// <summary>
    /// The posture for every sortie of one commander, on the logistics watch's clock. Only sorties
    /// with an aircraft up are judged: a sortie with nothing airborne has nothing to fall back, and
    /// one whose aircraft have all been lost while holding is simply cleared.
    /// </summary>
    private void WatchAirPosture(FactionHQ hq, OperationsState state)
    {
        // The belt picture for this commander, once: every sortie below asks it the same question
        // (design.md, air-survival-layer_20260916 Layer 3).
        RefreshPostureBelts(hq);
        // Whether this commander can fly suppression at all, asked at most once per watch and only
        // when some sortie actually has a belt near it: the roster check walks the catalog.
        bool? canFlyArad = null;
        for (int i = 0; i < state.AirSorties.Count; i++)
        {
            CommanderAirSortie sortie = state.AirSorties[i];
            if (sortie.Kind == CommanderSortieKind.Awacs)
            {
                continue; // the radar airframe has its own safety search and never fights
            }

            // ONE number, read once and used for every decision below (user decision 2026-09-16):
            // the aircraft assigned to this sortie that are up. Nothing about where they are, and
            // nothing about whether the sortie is holding — see SortieStrength for what counting
            // "our fighters near the hold point, while holding" cost in the 2026-09-16 match.
            int ours = SortieStrength(CountFightersUp(sortie), CountFixedWingUp(sortie.Cas));
            if (ours <= 0)
            {
                if (sortie.FallingBack)
                {
                    EndFallback(sortie);
                    CommanderAiLog.Note(hq, $"{sortie.Label}: its aircraft are gone; the hold ends.");
                }

                continue;
            }

            // Within 20 km of the objective or of any of our fighters (user decision 2026-09-16), so a
            // raid still closing is weighed before the fight is joined, and pursuers that chase the
            // fighters out of the objective's ring keep counting rather than reading as "reinforced
            // 2 v 0; re-engages".
            int hostiles = CountHostileAirAgainstSortie(hq, sortie);
            // The belt ahead (design.md, air-survival-layer_20260916 Layer 3). Read every watch, for
            // a sortie that is holding and one that is not, because both the decision to hold and the
            // decision to go in are taken from the same two facts.
            bool beltApplies = BeltHoldApplies(sortie.Kind, sortie.Cas.Count);
            int airDefence = beltApplies
                ? LargestBeltNear(sortie.Center, CommanderSettings.AirBeltHoldRadiusMeters)
                : 0;
            // Only hold for a sweep that can actually be flown (review H10): the roster has an
            // anti-radiation airframe, and the belt is inside the ring where the ARAD step pairs a
            // suppression sortie with this objective. A hold waiting on a sweep that will never be
            // opened ran to the give-up time, stood the sortie down, and repeated after the cooldown.
            if (BeltWorthSuppressing(airDefence, CommanderSettings.AradClusterMinimum))
            {
                canFlyArad ??= CommanderEnemyCommanderService.HasRoleCandidate(
                    hq, CommanderEnemyCommanderService.AirRole.Arad);
                if (canFlyArad != true
                    || !BeltWorthSuppressing(
                        LargestBeltNear(sortie.Center, ObservedRadiusMeters), CommanderSettings.AradClusterMinimum))
                {
                    airDefence = 0;
                }
            }
            bool sweepIn = beltApplies && SweepHasGoneIn(state, sortie);
            bool beltHolds = beltApplies
                && BeltHoldsSortie(airDefence, sweepIn, CommanderSettings.AradClusterMinimum);
            if (!sortie.FallingBack)
            {
                // Outnumbered first: being shot at by fighters now outranks flying toward a belt.
                if (ShouldFallBack(ours, hostiles, CommanderSettings.AirFallbackMargin))
                {
                    BeginFallback(hq, state, sortie, ours, hostiles);
                }
                else if (beltHolds)
                {
                    BeginBeltHold(hq, state, sortie, airDefence);
                }

                continue;
            }

            if (sortie.HoldReason == CommanderAirHoldReason.Belt)
            {
                WatchBeltHold(hq, state, sortie, beltHolds, sweepIn);
                continue;
            }

            if (ShouldReengage(ours, hostiles, CommanderSettings.AirReengageMargin))
            {
                EndFallback(sortie);
                CommanderAiLog.Note(hq, $"{sortie.Label}: reinforced {ours} v {hostiles}; re-engages.");
                continue;
            }

            if (FallbackGaveUp(Time.time - sortie.FallingBackSince, CommanderSettings.AirFallbackGiveUpMinutes))
            {
                GiveUpSortie(hq, state, sortie);
                continue;
            }

            // Still short. The demand is re-asserted every watch because the review rebuilds it every
            // thirty seconds, and the posture is re-stamped because a fighter that lands, rearms and
            // relaunches onto the sortie carries a fresh mission record.
            CallForFighters(sortie, hostiles);
            // The fallback point is re-found every watch (user report 2026-09-16: "it didn't correctly
            // fall-back, it was flying straight at the enemy"): hostiles move, and a point that was
            // clear when the sortie fell back can have the raid sitting on it a minute later.
            if (CommanderEnemyCommanderService.TryNearestOwnBase(hq, sortie.Center, out Airbase? fallbackBase, out _)
                && fallbackBase != null)
            {
                GlobalPosition hold = ClearFallbackPoint(
                    hq, sortie.Center, fallbackBase.center.GlobalPosition(), out bool collapsedOnBase);
                if (collapsedOnBase)
                {
                    // The raid has spread over the whole leg since the hold began (user decision
                    // 2026-09-16). Holding on our own strip is not holding, so the sortie lets its
                    // aircraft go instead; the home patrol has had no minimum since DECISION-038,
                    // so they are re-assignable the moment they land in it.
                    StandDownSortie(hq, state, sortie, NoClearGroundLine);
                    continue;
                }

                sortie.FallbackPoint = hold;
            }

            ApplyPostureToAll(sortie);
            string pair = $"{ours} v {hostiles}";
            if (sortie.FallbackReported != pair)
            {
                sortie.FallbackReported = pair;
                CommanderAiLog.Note(
                    hq,
                    $"{sortie.Label}: still outnumbered {pair}; holding "
                        + $"{CommanderSettings.AirFallbackDistanceMeters / 1000f:0} km back.");
            }
        }
    }

    /// <summary>
    /// Whether a strike or anti-radiation sortie of ours has gone in within the belt-hold ring of
    /// this sortie's objective — the "somebody is already shooting at it" test. A suppression sortie
    /// counts because shooting at the belt is what it is for; a strike package counts because a belt
    /// that is being bombed is a belt that is looking somewhere else. The sortie never counts as its
    /// own sweep.
    /// </summary>
    private static bool SweepHasGoneIn(OperationsState state, CommanderAirSortie sortie)
    {
        float ring = CommanderSettings.AirBeltHoldRadiusMeters;
        Vector3 center = sortie.Center.AsVector3();
        for (int i = 0; i < state.AirSorties.Count; i++)
        {
            CommanderAirSortie other = state.AirSorties[i];
            if (ReferenceEquals(other, sortie)
                || (other.Kind != CommanderSortieKind.Arad && other.Kind != CommanderSortieKind.Strike))
            {
                continue;
            }

            // A suppression sortie is "in" once one of its aircraft is over the belt. Its GoneIn is
            // never set (it starts true), so reading it counted a sortie with nothing bought yet as
            // the sweep and released the hold at once (review H10).
            if (other.Kind == CommanderSortieKind.Arad ? AradStillInbound(other) : !other.GoneIn)
            {
                continue;
            }

            if (CommanderGameAccess.HorizontalDistance(center, other.Center.AsVector3()) <= ring)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Puts a sortie into the belt hold (design.md, air-survival-layer_20260916 Layer 3): the same
    /// fallback point, stamps and contact mark the outnumbered posture uses, plus the demand for a
    /// suppression element so the review actually buys the sweep the sortie is waiting for. A
    /// commander with no airbase left has nowhere to hold and the sortie flies in, exactly as it does
    /// when it is outnumbered.
    /// </summary>
    private void BeginBeltHold(FactionHQ hq, OperationsState state, CommanderAirSortie sortie, int airDefence)
    {
        if (!CommanderEnemyCommanderService.TryNearestOwnBase(hq, sortie.Center, out Airbase? airbase, out _)
            || airbase == null)
        {
            return;
        }

        GlobalPosition hold = ClearFallbackPoint(
            hq, sortie.Center, airbase.center.GlobalPosition(), out bool collapsedOnBase);
        if (collapsedOnBase)
        {
            StandDownSortie(hq, state, sortie, NoClearGroundLine);
            return;
        }

        sortie.HoldReason = CommanderAirHoldReason.Belt;
        sortie.FallingBackSince = Time.time;
        sortie.FallbackPoint = hold;
        sortie.FallbackReported = string.Empty;
        AskForTheSweep(sortie);
        ApplyPostureToAll(sortie);
        CommanderAiLog.Note(
            hq,
            $"{sortie.Label}: holds — air defence over the objective ({airDefence} sites) and no sweep in; "
                + "asks for a sweep.");
    }

    /// <summary>The belt hold every watch: it ends the moment the belt is gone or a sweep has gone
    /// in, it is given up on the same clock the outnumbered posture uses, and while it lasts the
    /// demand for suppression and the hold stamps are re-asserted because the review rebuilds both
    /// every thirty seconds.</summary>
    private void WatchBeltHold(
        FactionHQ hq, OperationsState state, CommanderAirSortie sortie, bool beltHolds, bool sweepIn)
    {
        if (!beltHolds)
        {
            EndFallback(sortie);
            CommanderAiLog.Note(
                hq,
                sweepIn
                    ? $"{sortie.Label}: sweep in; goes in."
                    : $"{sortie.Label}: the belt is gone; goes in.");
            return;
        }

        if (FallbackGaveUp(Time.time - sortie.FallingBackSince, CommanderSettings.AirFallbackGiveUpMinutes))
        {
            GiveUpSortie(hq, state, sortie);
            return;
        }

        AskForTheSweep(sortie);
        if (CommanderEnemyCommanderService.TryNearestOwnBase(hq, sortie.Center, out Airbase? holdBase, out _)
            && holdBase != null)
        {
            GlobalPosition hold = ClearFallbackPoint(
                hq, sortie.Center, holdBase.center.GlobalPosition(), out bool collapsedOnBase);
            if (collapsedOnBase)
            {
                StandDownSortie(hq, state, sortie, NoClearGroundLine);
                return;
            }

            sortie.FallbackPoint = hold;
        }

        ApplyPostureToAll(sortie);
    }

    /// <summary>The belt hold's own call for help: the suppression element a hard target's package is
    /// owed (<c>HardArad</c>), never lowered. Nothing is bought here — the review's ARAD step and the
    /// buyer read the demand, exactly as <see cref="CallForFighters"/> leaves the fighters to
    /// them.</summary>
    private static void AskForTheSweep(CommanderAirSortie sortie)
    {
        sortie.InContact = true;
        sortie.AradWanted = Mathf.Max(sortie.AradWanted, HardArad);
    }

    /// <summary>Puts a sortie into the posture: the fallback point toward the nearest own airbase,
    /// the call for help, and the hold and self-defence radius on every bound aeroplane. A commander
    /// with no airbase left has nowhere to fall back to and the fighters fight where they are.</summary>
    private void BeginFallback(
        FactionHQ hq, OperationsState state, CommanderAirSortie sortie, int ours, int hostiles)
    {
        if (!CommanderEnemyCommanderService.TryNearestOwnBase(hq, sortie.Center, out Airbase? airbase, out _)
            || airbase == null)
        {
            return;
        }

        GlobalPosition hold = ClearFallbackPoint(
            hq, sortie.Center, airbase.center.GlobalPosition(), out bool collapsedOnBase);
        if (collapsedOnBase)
        {
            StandDownSortie(hq, state, sortie, NoClearGroundLine);
            return;
        }

        sortie.HoldReason = CommanderAirHoldReason.Outnumbered;
        sortie.FallingBackSince = Time.time;
        sortie.FallbackPoint = hold;
        sortie.FallbackReported = $"{ours} v {hostiles}";
        CallForFighters(sortie, hostiles);
        ApplyPostureToAll(sortie);
        CommanderAiLog.Note(
            hq,
            $"{sortie.Label}: outnumbered {ours} v {hostiles} within {CommanderSettings.AirPostureRingMeters / 1000f:0} km "
                + "of the objective or its fighters; "
                + $"falls back {CommanderSettings.AirFallbackDistanceMeters / 1000f:0} km toward "
                + $"{CommanderCaptureService.GetAirbaseLabel(airbase)} and calls for {sortie.CapsWanted} fighters.");
    }

    /// <summary>
    /// The fallback point that is actually CLEAR: the nominal one (<see cref="FallbackPoint"/>,
    /// <c>AirFallbackDistanceMeters</c> from the objective toward the base), walked further toward the
    /// base in <see cref="FormUpPullBackStepMeters"/> steps until no tracked hostile fixed-wing aircraft
    /// is within <c>AirPostureRingMeters</c> of it, and the base itself — under its own air defence —
    /// when nothing on the line is clear. With fourteen hostiles inside 20 km of the objective, the
    /// nominal point 15 km back was inside the raid, and "falling back" flew the fighters into it
    /// (user report 2026-09-16). The same walk the package form-up uses (<c>FirstClearAlong</c>).
    /// <para>
    /// <paramref name="collapsedOnBase"/> says the walk found NOTHING clear and fell all the way
    /// through to the airbase. A sortie does not hold over its own runway (user decision
    /// 2026-09-16): a hold point standing on the strip is the commander saying the sortie is not
    /// viable, and the caller stands it down instead. It was also the arithmetic behind the
    /// 2026-09-16 flapping — a 20 km ring round our own airbase counted every fighter at home.
    /// </para>
    /// </summary>
    private static GlobalPosition ClearFallbackPoint(
        FactionHQ hq, GlobalPosition center, GlobalPosition basePosition, out bool collapsedOnBase)
    {
        collapsedOnBase = false;
        Vector3 toBase = basePosition - center;
        toBase.y = 0f;
        float length = toBase.magnitude;
        if (length <= 1f)
        {
            // The base stands on the objective: there is no line to walk and nowhere to fall back
            // to, which is the "fights where it is" case the posture has always had, not a collapse.
            return center;
        }

        Vector3 towardObjective = -toBase / length;
        float y = Mathf.Max((float)basePosition.y, (float)center.y);
        float nominalFromBase = length - Mathf.Min(CommanderSettings.AirFallbackDistanceMeters, length);
        float ring = CommanderSettings.AirPostureRingMeters;
        float fromBase = FirstClearAlong(
            nominalFromBase,
            FormUpPullBackStepMeters,
            along => CountHostileAirInRing(hq, PointFromBase(basePosition, towardObjective, along, y), ring, fixedWingCombatOnly: true) == 0);
        collapsedOnBase = fromBase <= 0f;
        return PointFromBase(basePosition, towardObjective, fromBase, y);
    }

    /// <summary>The line a sortie that has nowhere clear to hold writes as it lets its aircraft
    /// go — one definition, because all four places that re-find the hold point say it.</summary>
    private const string NoClearGroundLine = "no clear ground to hold — its fighters join the home patrol.";

    /// <summary>The point <paramref name="along"/> metres out from the base toward the objective, at
    /// height <paramref name="y"/>.</summary>
    private static GlobalPosition PointFromBase(GlobalPosition basePosition, Vector3 towardObjective, float along, float y)
    {
        return new GlobalPosition(
            basePosition.x + towardObjective.x * along,
            y,
            basePosition.z + towardObjective.z * along);
    }

    /// <summary>Takes a sortie out of the posture and clears the hold and radius off its airframes.
    /// The caller writes the line, because the reason differs.</summary>
    private static void EndFallback(CommanderAirSortie sortie)
    {
        // The reason first: FallingBack reads it, and ApplyPostureToAll below takes the stamps off
        // every bound airframe only once the sortie says it is no longer holding.
        sortie.HoldReason = CommanderAirHoldReason.None;
        sortie.FallingBackSince = -1f;
        sortie.FallbackReported = string.Empty;
        ApplyPostureToAll(sortie);
    }

    /// <summary>
    /// Most fighters one sortie may call for when it is holding back. Six, twice the routine
    /// per-objective patrol cap (<see cref="CapPerObjectiveCap"/>): a real air battle has to be able
    /// to draw more than quiet cover does, and two pairs plus a spare is the largest formation this
    /// wing has ever usefully kept over one objective. A cap is needed at all because the call is
    /// "one more than the hostiles tracked" and the hostile count is a tracking picture, not a head
    /// count — in the 2026-09-16 session the median call was for eleven fighters and the largest for
    /// thirty-one, against an airborne ceiling that starts at thirty for the whole wing.
    /// </summary>
    private const int FallbackFightersCap = 2 * CapPerObjectiveCap;

    /// <summary>The call for help (design Section 4.4): the sortie is in contact and wants enough
    /// fighters to outnumber the hostiles, within what one sortie may ever ask for. The existing
    /// retask takes fighters from quiet sorties and the lendable home patrol, and the existing buyer
    /// fills what is left; nothing new is bought here. The raise survives the review through the
    /// reconcile, which carries a falling-back sortie's demand across — which is why the demand it
    /// already holds is the ENVELOPE'S FLOOR rather than something the cap may cut into.</summary>
    private static void CallForFighters(CommanderAirSortie sortie, int hostiles)
    {
        sortie.InContact = true;
        sortie.CapsWanted = SortieElementWanted(
            floor: sortie.CapsWanted,
            grown: ReinforcementWanted(hostiles, CommanderSettings.AirReengageMargin),
            cap: FallbackFightersCap);
    }

    /// <summary>Writes the sortie's posture onto every bound aeroplane — the CAP and the CAS alike,
    /// because the escorted flight turns with its escort (user decision 2026-09-16). Helicopters are
    /// left alone (design Section 4.6).</summary>
    private static void ApplyPostureToAll(CommanderAirSortie sortie)
    {
        for (int i = 0; i < sortie.Caps.Count; i++)
        {
            ApplyPosture(sortie, sortie.Caps[i]);
        }

        for (int i = 0; i < sortie.Cas.Count; i++)
        {
            ApplyPosture(sortie, sortie.Cas[i]);
        }
    }

    /// <summary>Stamps or clears one airframe's hold and self-defence radius from its sortie's
    /// posture. Also the last step of <c>TaskOntoSortie</c>, so a fighter retasked onto a
    /// falling-back sortie falls back with it and one retasked off it stops.</summary>
    /// <summary>Takes the fallback stamps off one airframe: for a release to the player, the home
    /// patrol or a landing, none of which is a falling-back sortie (review, 2026-09-16).</summary>
    private static void ClearPosture(Aircraft aircraft)
    {
        CommanderAirCommandService.AirMission? mission = CommanderAirCommandService.Instance?.TryGetMission(aircraft);
        if (mission == null)
        {
            return;
        }

        mission.HoldOverride = null;
        mission.SelfDefenceOnly = false;
    }

    private static void ApplyPosture(CommanderAirSortie sortie, Aircraft aircraft)
    {
        if (!IsFixedWingCombatAircraft(aircraft))
        {
            return;
        }

        CommanderAirCommandService.AirMission? mission = CommanderAirCommandService.Instance?.TryGetMission(aircraft);
        if (mission == null)
        {
            return;
        }

        // ONE gathering place per sortie (fix, 2026-09-16): the point stamped here is the point the
        // arrival count, the go-in test and the marker all read, because all four go through
        // SortieStation. Writing sortie.FallbackPoint straight onto the mission is exactly how the
        // two drifted apart and a defended strike sat at `pkg 0/4` with all its aircraft up.
        mission.HoldOverride = PostureHoldPoint(sortie, fixedWing: true);
        mission.SelfDefenceOnly = sortie.FallingBack;
    }

    /// <summary>
    /// The stand-down (design Section 4.5): the reinforcement did not come. The sortie's airframes go
    /// through the ordinary release door — retasked to a sortie that wants them, else the home patrol
    /// or home — and the sortie takes the loss cooldown its own losses would give it, so the fill
    /// does not put new fighters straight back into the same fight. The sortie itself stays on the
    /// list so the reconcile carries that cooldown across the next review. A strike package is
    /// closed the way an abandoned strike is closed. A lift cover's transport reacts on its own: with
    /// no escort up, the delivery's hope rule recalls or diverts it.
    /// </summary>
    private void GiveUpSortie(FactionHQ hq, OperationsState state, CommanderAirSortie sortie)
    {
        StandDownSortie(
            hq,
            state,
            sortie,
            $"not reinforced in {CommanderSettings.AirFallbackGiveUpMinutes:0} min; stands down.");
    }

    /// <summary>
    /// The one stand-down door, with the caller's own reason (Reuse rule 5, generalised 2026-09-16
    /// when a second reason arrived): the five-minute give-up and a hold point that has collapsed
    /// onto our own runway both end the same way, and forking them is how two "let the aircraft go"
    /// paths drift apart. Everything below was <see cref="GiveUpSortie"/>'s body, MOVED.
    /// </summary>
    private void StandDownSortie(FactionHQ hq, OperationsState state, CommanderAirSortie sortie, string reason)
    {
        CommanderAiLog.Note(hq, $"{sortie.Label}: {reason}");
        EndFallback(sortie);

        float minutes = SortieLossCooldownMinutes(
            awacs: false,
            CountObservedAirDefence(hq, sortie.Center),
            CommanderSettings.CasLossCooldownMinutes,
            AwacsLossCooldownMinutes);
        sortie.CooldownUntil = Time.time + minutes * 60f;

        // Whichever owner holds it — the deliberate slot or an attack's (concurrent-attacks_20260918).
        if (sortie.Kind == CommanderSortieKind.Strike)
        {
            ForgetAnyStrike(state, sortie);
            CommanderAiLog.Note(hq, $"strike on {sortie.Label} abandoned: its escort was outnumbered and not reinforced.");
        }

        ReleaseBoundAirframes(hq, state, sortie, sortie.Cas, state.AirSorties);
        sortie.Cas.Clear();
        ReleaseBoundAirframes(hq, state, sortie, sortie.Caps, state.AirSorties);
        sortie.Caps.Clear();
    }

    /// <summary>
    /// The air survival layer's rules at their named boundaries (design.md,
    /// air-survival-layer_20260916; user decision 2026-09-16: "insert some sense of self-preservation
    /// into our aircraft"). Registered beside <see cref="CheckAirPosture"/> in <c>SelfCheck</c>.
    /// </summary>
    private static void CheckSurvival(List<string> failures)
    {
        // Layer 1, the bravery refusal transcribed from the game (ChooseHQTarget:724). All three
        // clauses must hold: not enough nerve, the commander rates the target as worse than that
        // nerve, and it is more than twice the weapon's reach away.
        Expect(
            failures,
            "a weak, threatened target more than twice the weapon's reach away is refused",
            CommanderAirCommandService.TargetIsTooDangerous(0.1f, 0.5f, 0.5f, 70000f, 30000f),
            true);
        Expect(
            failures,
            "that same target inside twice the weapon's reach is taken",
            CommanderAirCommandService.TargetIsTooDangerous(0.1f, 0.5f, 0.5f, 50000f, 30000f),
            false);
        Expect(
            failures,
            "a target exactly at twice the weapon's reach is taken",
            CommanderAirCommandService.TargetIsTooDangerous(0.1f, 0.5f, 0.5f, 60000f, 30000f),
            false);
        Expect(
            failures,
            "a strong opportunity is taken however threatening the target",
            CommanderAirCommandService.TargetIsTooDangerous(0.4f, 0.5f, 9f, 70000f, 30000f),
            false);
        Expect(
            failures,
            "an opportunity the commander rates no worse than the threat is taken",
            CommanderAirCommandService.TargetIsTooDangerous(0.1f, 0.5f, 0.05f, 70000f, 30000f),
            false);
        Expect(
            failures,
            "a brave airframe takes the fight a stock-bravery one refuses",
            CommanderAirCommandService.TargetIsTooDangerous(0.2f, 1f, 0.5f, 70000f, 30000f),
            false);
        Expect(
            failures,
            "the same fight at stock bravery is refused",
            CommanderAirCommandService.TargetIsTooDangerous(0.2f, 0.5f, 0.5f, 70000f, 30000f),
            true);

        // Layer 1, the destination clamp: the game's own disengagement is left alone.
        Expect(
            failures,
            "a pilot breaking off an attack is disengaging",
            CommanderAirCommandService.PilotIsDisengaging(CommanderAirCommandService.AttackModeBreakOffAttack),
            true);
        Expect(
            failures,
            "a pilot retreating to standoff is disengaging",
            CommanderAirCommandService.PilotIsDisengaging(CommanderAirCommandService.AttackModeRetreatStandoff),
            true);
        Expect(failures, "a pilot flying to its target is not disengaging", CommanderAirCommandService.PilotIsDisengaging(0), false);
        Expect(failures, "a pilot with no target is not disengaging", CommanderAirCommandService.PilotIsDisengaging(3), false);
        Expect(
            failures,
            "the two disengaging modes are distinct ordinals of the game's own enum",
            CommanderAirCommandService.AttackModeBreakOffAttack != CommanderAirCommandService.AttackModeRetreatStandoff,
            true);

        // Layer 2, the fuel rule: limit-inclusive, and a fraction of zero turns it off.
        Expect(failures, "a full tank is not low on fuel", CommanderAirCommandService.FuelBelow(1f, 0.25f), false);
        Expect(failures, "a tank one point above the fraction is not low", CommanderAirCommandService.FuelBelow(0.26f, 0.25f), false);
        Expect(failures, "a tank exactly on the fraction is low", CommanderAirCommandService.FuelBelow(0.25f, 0.25f), true);
        Expect(failures, "a tank below the fraction is low", CommanderAirCommandService.FuelBelow(0.1f, 0.25f), true);
        Expect(failures, "a fuel fraction of zero sends nobody home", CommanderAirCommandService.FuelBelow(0f, 0f), false);
        Expect(failures, "a negative fuel fraction sends nobody home", CommanderAirCommandService.FuelBelow(0.01f, -1f), false);
        Expect(
            failures,
            "the commander turns for home before the game's own pilot lands itself at 20 %; check the Operations section of the config",
            CommanderSettings.AirSurvivalFuelFraction > 0.2f && CommanderSettings.AirSurvivalFuelFraction < 1f,
            true);

        // Layer 3, the belt hold. The hold and the sweep ask ONE question (user decision 2026-09-16:
        // "only hold for a belt worth sweeping"): a formation waits only for a sweep the wing would
        // actually buy, which is a cluster of AradClusterMinimum launchers or more.
        Expect(failures, "a belt at the cluster minimum with no sweep in holds the sortie", BeltHoldsSortie(3, false, 3), true);
        Expect(failures, "a belt above the cluster minimum holds", BeltHoldsSortie(7, false, 3), true);
        Expect(failures, "two launchers are below the minimum and do not hold", BeltHoldsSortie(2, false, 3), false);
        Expect(failures, "a single tracked launcher never holds: the pilot's own threat dodging answers it", BeltHoldsSortie(1, false, 3), false);
        Expect(failures, "a belt at the cluster minimum with a sweep in does not hold", BeltHoldsSortie(3, true, 3), false);
        Expect(failures, "no belt never holds, sweep or no sweep", BeltHoldsSortie(0, false, 3), false);
        Expect(failures, "no belt never holds with a sweep in either", BeltHoldsSortie(0, true, 3), false);
        // The one rule, from both sides: the hold's threshold IS the sweep's threshold. A belt the
        // hold waits for is a belt the suppression sizing would open a sortie for, at every count.
        for (int launchers = 0; launchers <= 8; launchers++)
        {
            Expect(
                failures,
                $"a belt of {launchers} launchers is held for exactly when a sweep would be opened for it",
                BeltHoldsSortie(launchers, sweepIn: false, CommanderSettings.AradClusterMinimum),
                AradWanted(launchers, CommanderSettings.AradClusterMinimum) > 0);
        }
        Expect(
            failures,
            "a patrol with no strike element of its own does not hold for a belt",
            BeltHoldApplies(CommanderSortieKind.Cap, 0),
            false);
        Expect(
            failures,
            "a patrol that is also flying close air support holds",
            BeltHoldApplies(CommanderSortieKind.Cap, 2),
            true);
        Expect(failures, "an objective sortie holds", BeltHoldApplies(CommanderSortieKind.Objective, 0), true);
        Expect(failures, "a strike package holds", BeltHoldApplies(CommanderSortieKind.Strike, 0), true);
        Expect(failures, "a suppression sortie holds", BeltHoldApplies(CommanderSortieKind.Arad, 0), true);
        Expect(
            failures,
            "the belt-hold ring is positive; check the Operations section of the config",
            CommanderSettings.AirBeltHoldRadiusMeters > 0f,
            true);
        Expect(
            failures,
            "the package form-up standoff is positive; check the Operations section of the config",
            CommanderSettings.PackageFormUpStandoffMeters > 0f,
            true);
    }

    /// <summary>The posture's rules at their named boundaries (user decision 2026-09-16).</summary>
    private static void CheckAirPosture(List<string> failures)
    {
        // Falling back: the hostiles must exceed ours by the margin, and a margin of zero is "off".
        Expect(failures, "two fighters against three do not fall back at margin 2", ShouldFallBack(2, 3, 2), false);
        Expect(failures, "two fighters against four fall back at margin 2", ShouldFallBack(2, 4, 2), true);
        Expect(failures, "no fighters against two fall back at margin 2", ShouldFallBack(0, 2, 2), true);
        Expect(failures, "a fallback margin of zero never falls back", ShouldFallBack(0, 9, 0), false);
        Expect(failures, "a negative fallback margin never falls back", ShouldFallBack(0, 9, -1), false);

        // Re-engaging: parity is not enough, superiority by the margin is.
        Expect(failures, "five against five do not re-engage at margin 1", ShouldReengage(5, 5, 1), false);
        Expect(failures, "six against five re-engage at margin 1", ShouldReengage(6, 5, 1), true);
        Expect(failures, "three against five do not re-engage", ShouldReengage(3, 5, 1), false);

        // Giving up: limit-inclusive, negative never started, zero minutes turns the rule off.
        Expect(failures, "a fallback that has just started is not given up", FallbackGaveUp(0f, 5f), false);
        Expect(failures, "a second short of five minutes is not given up", FallbackGaveUp(299f, 5f), false);
        Expect(failures, "exactly five minutes falling back is given up", FallbackGaveUp(300f, 5f), true);
        Expect(failures, "a fallback that never started is never given up", FallbackGaveUp(-1f, 5f), false);
        Expect(failures, "a give-up limit of zero waits for ever", FallbackGaveUp(9000f, 0f), false);

        // The fallback point: along the line toward the base, never past it, the centre when the base
        // stands on it.
        GlobalPosition center = new GlobalPosition(0f, 500f, 0f);
        GlobalPosition farBase = new GlobalPosition(40000f, 0f, 0f);
        GlobalPosition nearBase = new GlobalPosition(0f, 0f, 10000f);
        GlobalPosition far = FallbackPoint(center, farBase, 15000f);
        Expect(
            failures,
            "fifteen kilometres toward a base 40 km away lands 15 km from the centre",
            Mathf.Abs(CommanderGameAccess.HorizontalDistance(far.AsVector3(), center.AsVector3()) - 15000f) < 1f,
            true);
        Expect(
            failures,
            "fifteen kilometres toward a base 40 km away lands 25 km from the base",
            Mathf.Abs(CommanderGameAccess.HorizontalDistance(far.AsVector3(), farBase.AsVector3()) - 25000f) < 1f,
            true);
        Expect(
            failures,
            "the fallback point keeps the centre's altitude",
            Mathf.Abs((float)(far.y - center.y)) < 1f,
            true);
        GlobalPosition near = FallbackPoint(center, nearBase, 15000f);
        Expect(
            failures,
            "a base 10 km away yields the base itself rather than a point beyond it",
            CommanderGameAccess.HorizontalDistance(near.AsVector3(), nearBase.AsVector3()) < 1f,
            true);
        GlobalPosition same = FallbackPoint(center, center, 15000f);
        Expect(
            failures,
            "a base standing on the centre yields the centre",
            CommanderGameAccess.HorizontalDistance(same.AsVector3(), center.AsVector3()) < 1f,
            true);

        // Which hostiles count: within the ring of the objective, or of the nearest fighter.
        Expect(failures, "a hostile 15 km from the objective counts at a 20 km ring", HostileCountsAgainstSortie(15000f, 90000f, 20000f), true);
        Expect(failures, "a hostile 50 km from the objective but 12 km from a fighter counts", HostileCountsAgainstSortie(50000f, 12000f, 20000f), true);
        Expect(failures, "a hostile 50 km from both does not count", HostileCountsAgainstSortie(50000f, 50000f, 20000f), false);
        Expect(failures, "a hostile exactly on the ring counts", HostileCountsAgainstSortie(20000f, 90000f, 20000f), true);
        Expect(
            failures,
            "the posture ring is positive; check the Operations section of the config",
            CommanderSettings.AirPostureRingMeters > 0f,
            true);

        // A sortie's own strength (user decision 2026-09-16): its escort and its strike element,
        // and nothing else at all.
        Expect(failures, "a sortie's strength is its escort plus its strike element", SortieStrength(3, 2), 5);
        Expect(failures, "a sortie with nothing up has no strength", SortieStrength(0, 0), 0);
        Expect(failures, "an escort with no strike element is still a strength", SortieStrength(2, 0), 2);
        Expect(failures, "a negative read never subtracts from a sortie's strength", SortieStrength(-4, 2), 2);

        // THE regression this track exists to stop coming back (investigation 2026-09-16, 1,012
        // flip-flop cycles): the number a sortie is judged on must not move when the judgement
        // changes. Counted over the same sortie with the hold on and with it off, and with a hold
        // point far enough away that any distance clause creeping back in would show.
        CommanderAirSortie steady = new()
        {
            Kind = CommanderSortieKind.Objective,
            Center = new GlobalPosition(0f, 1000f, 0f),
            FallbackPoint = new GlobalPosition(80000f, 0f, 80000f),
            HoldReason = CommanderAirHoldReason.None,
        };
        int loose = SortieStrength(CountFightersUp(steady), CountFixedWingUp(steady.Cas));
        steady.HoldReason = CommanderAirHoldReason.Outnumbered;
        int holding = SortieStrength(CountFightersUp(steady), CountFixedWingUp(steady.Cas));
        Expect(failures, "a sortie's strength is the same whether or not it is holding", holding, loose);
        Expect(failures, "an empty sortie counts nothing while holding either", holding, 0);

        // And the same thing said at the rule level: one reading of a fight can never both send a
        // sortie back and send it in, at the shipped margins. A retune that let both fire is the
        // flapping by another route, so it fails here rather than in a match.
        int fallbackMargin = CommanderSettings.AirFallbackMargin;
        int reengageMargin = CommanderSettings.AirReengageMargin;
        bool everBoth = false;
        for (int mine = 0; mine <= 20; mine++)
        {
            for (int theirs = 0; theirs <= 20; theirs++)
            {
                everBoth = everBoth
                    || (ShouldFallBack(mine, theirs, fallbackMargin) && ShouldReengage(mine, theirs, reengageMargin));
            }
        }

        Expect(
            failures,
            "no reading of a fight both falls back and re-engages; check the Operations section of the config",
            everBoth,
            false);

        // The call for help asks for superiority, not parity.
        Expect(failures, "five hostiles and a re-engage margin of one call for six fighters", ReinforcementWanted(5, 1), 6);
        Expect(failures, "no hostiles call for the margin alone", ReinforcementWanted(0, 1), 1);

        // And within what one sortie may ever ask for (user decision 2026-09-16). Driven through the
        // shared envelope exactly as CallForFighters drives it.
        Expect(
            failures,
            "a sortie facing four raiders asks for five fighters, unchanged",
            SortieElementWanted(0, ReinforcementWanted(4, 1), FallbackFightersCap),
            5);
        Expect(
            failures,
            "a sortie facing thirty raiders asks for the cap, not for the whole wing",
            SortieElementWanted(0, ReinforcementWanted(30, 1), FallbackFightersCap),
            FallbackFightersCap);
        Expect(
            failures,
            "a sortie exactly on the cap keeps it",
            SortieElementWanted(0, ReinforcementWanted(FallbackFightersCap - 1, 1), FallbackFightersCap),
            FallbackFightersCap);
        Expect(
            failures,
            "a demand already above the cap is never cut by it: the sortie was promised those fighters",
            SortieElementWanted(9, ReinforcementWanted(2, 1), FallbackFightersCap),
            9);
        Expect(
            failures,
            "a sortie in trouble may call for more fighters than quiet cover ever asks for",
            FallbackFightersCap > CapPerObjectiveCap,
            true);

        // The self-defence radius on the target hook: zero is off, inside engages, outside is ignored.
        Expect(failures, "a self-defence radius of zero refuses nothing", CommanderAirCommandService.TargetOutsideSelfDefence(50000f, 0f), false);
        Expect(failures, "a target 5,999 m out is inside a 6 km self-defence radius", CommanderAirCommandService.TargetOutsideSelfDefence(5999f, 6000f), false);
        Expect(failures, "a target 6,001 m out is outside a 6 km self-defence radius", CommanderAirCommandService.TargetOutsideSelfDefence(6001f, 6000f), true);
        // Self-defence while falling back: inside the close bubble always; beyond it only a hostile
        // closing fast and already within the weapon's reach.
        Expect(failures, "a hostile inside the bubble is engaged whatever it is doing", CommanderAirCommandService.SelfDefenceEngages(5000f, -100f, 30000f), true);
        Expect(failures, "a hostile exactly on the close bubble is engaged", CommanderAirCommandService.SelfDefenceEngages(CommanderAirCommandService.SelfDefenceCloseMeters, -100f, 30000f), true);
        Expect(failures, "a hostile closing at 250 m/s from 15 km within a 30 km weapon is engaged", CommanderAirCommandService.SelfDefenceEngages(15000f, 250f, 30000f), true);
        Expect(failures, "a hostile loitering at 15 km is left alone", CommanderAirCommandService.SelfDefenceEngages(15000f, 10f, 30000f), false);
        Expect(failures, "a hostile flying away at 15 km is left alone", CommanderAirCommandService.SelfDefenceEngages(15000f, -200f, 30000f), false);
        Expect(failures, "a hostile closing from beyond the weapon's reach is not yet engaged", CommanderAirCommandService.SelfDefenceEngages(40000f, 250f, 30000f), false);
        Expect(
            failures,
            "the close self-defence bubble is positive; it is a class constant, not a setting",
            CommanderAirCommandService.SelfDefenceCloseMeters > 0f,
            true);

        // The configuration: a posture that could never fire, or that gives up between two watches,
        // is a mistake in the Operations section of the config.
        Expect(
            failures,
            "the fallback margin is at least one; check the Operations section of the config",
            CommanderSettings.AirFallbackMargin >= 1,
            true);
        Expect(
            failures,
            "the fallback distance is positive; check the Operations section of the config",
            CommanderSettings.AirFallbackDistanceMeters > 0f,
            true);
        Expect(
            failures,
            "a fallback waits for reinforcements across several watches before it is given up; check the Operations section of the config",
            CommanderSettings.AirFallbackGiveUpMinutes * 60f > CommanderSettings.LogisticsWatchSeconds,
            true);
    }
}
