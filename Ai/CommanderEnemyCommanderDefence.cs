using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace GroundControlRts;

/// <summary>
/// The enemy commander's home guard: a share of its ground force pinned on a ring around each base
/// it holds, and a threat posture that thickens that ring while something hostile is inside radar
/// range of it.
/// </summary>
/// <remarks>
/// This exists because of an engine behaviour, not a design preference.
/// <c>GroundVehicle.CheckObstacles</c> re-targets any vehicle with no <b>player</b> command onto the
/// nearest objective or tracked enemy, so every vehicle the commander bought walked at the player
/// the moment it left the depot ramp — the whole force was always an attack and the base was always
/// empty behind it. Pinning a defender is therefore not a move order but a
/// <c>SetDestination(post, playerCommand: true)</c>, the only call that sets
/// <c>commandedDestination</c> and stops the auto-walk; releasing one means clearing that private
/// field again, because with <c>holdAtPlayerCommandedDestination</c> set nothing in the base game
/// ever clears it.
/// <para>
/// ponytail: the ring is fixed points around the airbase centre, not a coverage solve. The units
/// standing on it aim and shoot for themselves — <c>Turret.AssessTargetPriority</c> is the whole
/// air- and ground-defence behaviour and it is already better than anything issued from here.
/// Upgrade to placed sectors only if the ring reads as obviously wrong in play.
/// </para>
/// </remarks>
internal sealed partial class CommanderEnemyCommanderService
{
    /// <summary>
    /// How often the home guard is reviewed. Deliberately faster than the 30 s buy review: a raid
    /// that is visible on radar is a raid that lands inside a minute, so a posture that only changed
    /// with the shopping list would change after the attack was over.
    /// </summary>
    private const float DefenceReviewIntervalSeconds = 10f;

    /// <summary>Vehicles held at home per base, at rest and while defending.</summary>
    private const int DefendersPerBase = 3;
    private const int ThreatDefendersPerBase = 7;

    /// <summary>Stations on each base's ring. More posts than defenders is deliberate — the ring
    /// spreads what it has instead of stacking it all on one side.</summary>
    private const int DefencePostsPerBase = 8;

    /// <summary>The ring follows the base's own capture radius, clamped so a tiny highway strip
    /// still gets a ring worth standing on and a huge airfield does not scatter its guard.</summary>
    private const float DefenceRingMinMeters = 450f;
    private const float DefenceRingMaxMeters = 1400f;

    /// <summary>A defender this close to its post is on station; re-ordering it only makes it
    /// shuffle, and every issue is a networked RPC.</summary>
    private const float DefenceArrivedMeters = 150f;

    /// <summary>
    /// How far out a tracked hostile counts as an attack on the base. This is the "it can see it on
    /// the radar" range, so it is deliberately far wider than weapon range: the point is to be
    /// standing on the ring before the strike arrives, not after.
    /// </summary>
    private const float ThreatRadiusMeters = 15000f;

    /// <summary>A contact older than this is a memory, not a threat. <c>internal</c> (not
    /// <c>private</c>, ledger addendum alongside T6): the operations service's own field threat
    /// mark and attack sizing need exactly this number too, and reuse it rather than redeclaring it
    /// (Reuse rule 4).</summary>
    internal const float ThreatMemorySeconds = 45f;

    /// <summary>The posture holds this long past the last contact, so an attacker who drops out of
    /// radar cover for a few seconds does not stand the guard down mid-attack.</summary>
    private const float ThreatHoldSeconds = 120f;

    private readonly List<Unit> defenceCandidates = new();
    private readonly List<Unit> staleDefenders = new();
    private int[] postOccupancy = new int[1];

    /// <summary>
    /// <c>GroundVehicle.commandedDestination</c>. Private, and the only way back out of a pinned
    /// position — see the remarks on this class. Null if a game update renames the field: this
    /// initializer runs the first time the enemy commander type is touched, and a throw here used
    /// to take the whole plugin down at load instead of just this one release.
    /// </summary>
    private static readonly AccessTools.FieldRef<GroundVehicle, bool>? CommandedDestinationRef =
        ResolveCommandedDestination();

    private static AccessTools.FieldRef<GroundVehicle, bool>? ResolveCommandedDestination()
    {
        try
        {
            return AccessTools.FieldRefAccess<GroundVehicle, bool>("commandedDestination");
        }
        catch (System.Exception exception)
        {
            CommanderPlugin.Log.LogError(
                $"GroundVehicle.commandedDestination not found; released defenders stay pinned: {exception.Message}");
            return null;
        }
    }

    /// <summary>
    /// True while this unit is standing on an enemy commander's base ring. Read by
    /// <see cref="CommanderCaptureService"/> so an expansion squad and the home guard never fight
    /// over the same vehicle — one issues an ordinary order, the other a pinning one.
    /// </summary>
    internal static bool IsDefendingUnit(Unit? unit)
    {
        CommanderEnemyCommanderService? service = Instance;
        if (service == null || unit == null)
        {
            return false;
        }

        foreach (KeyValuePair<FactionHQ, CommanderState> entry in service.states)
        {
            if (entry.Value.Defenders.ContainsKey(unit))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// A hit on anything a commanded faction owns puts it on the defensive, whether or not its
    /// sensors ever saw what did it: the radar half of the trigger cannot see a low pass that has
    /// already dropped. Called from the shared <c>Unit.RecordDamage</c> postfix, so it stays a
    /// dictionary lookup and a float write — <c>states</c> holds the local HQ only while the player
    /// commander is on, and then the player's own losses put <i>their</i> commander on the
    /// defensive, which is the point of the switch.
    /// </summary>
    internal void NotifyUnitDamaged(Unit? unit)
    {
        FactionHQ? hq = unit == null ? null : unit.NetworkHQ;
        if (hq != null && states.TryGetValue(hq, out CommanderState state))
        {
            state.ThreatUntil = Time.time + ThreatHoldSeconds;
        }
    }

    private void ReviewDefences(FactionHQ localHq)
    {
        foreach (FactionHQ hq in FactionRegistry.GetAllHQs())
        {
            if (hq == null
                || !CommanderPlayerCommanderService.IsCommanded(hq, localHq)
                || !hq.IsServer
                || hq.faction == null)
            {
                continue;
            }

            if (!states.TryGetValue(hq, out CommanderState state))
            {
                state = new CommanderState();
                states[hq] = state;
            }

            // The home CAP's loss bookkeeping rides this 10 s clock rather than the 30 s buy review
            // (design.md, commander-priorities_20260914 Section 2): the "enemy air was near" observation
            // has to be recent when a wreck is found, or every loss reads as an ordinary ground loss.
            CommanderOperationsService.MaintainHomeCap(hq);

            ReviewDefence(hq, state);
        }
    }

    private void ReviewDefence(FactionHQ hq, CommanderState state)
    {
        if (IsUnderThreat(hq))
        {
            state.ThreatUntil = Time.time + ThreatHoldSeconds;
        }

        bool defending = Time.time < state.ThreatUntil;
        if (defending != state.Defending)
        {
            state.Defending = defending;
            CommanderAiLog.Note(hq, defending
                ? $"goes to DEFENCE posture: hostiles inside {ThreatRadiusMeters / 1000f:0.#} km of its bases."
                : "stands down from DEFENCE posture.");
        }

        // The operations service owns this HQ's ground force (departure 8): the base's reserve
        // platoon is the guard now, so recruiting stops here — but the posture above still runs,
        // because it feeds the HUD status line and the buyer's own read of WantsDefenceUnit is
        // still meaningful right up until this point, just never true for a managed HQ.
        if (CommanderOperationsService.OwnsGroundForce(hq))
        {
            if (state.Defenders.Count > 0)
            {
                ReleaseSurplusDefenders(state, 0);
            }

            state.WantsDefenceUnit = false;
            return;
        }

        EnsureDefencePosts(hq, state);
        PruneDefenders(hq, state);

        int target = state.DefencePosts.Count == 0
            ? 0
            : state.DefenceBaseCount * (defending ? ThreatDefendersPerBase : DefendersPerBase);
        if (state.Defenders.Count > target)
        {
            ReleaseSurplusDefenders(state, target);
        }
        else if (state.Defenders.Count < target)
        {
            RecruitDefenders(hq, state, target);
        }

        // Short of a full ring out of what it already owns, so the buy loop puts an air-defence
        // vehicle ahead of its plan — the same precedence the first launcher and a capture unit get.
        state.WantsDefenceUnit = state.Defenders.Count < target;

        foreach (KeyValuePair<Unit, int> entry in state.Defenders)
        {
            GlobalPosition post = state.DefencePosts[entry.Value % state.DefencePosts.Count];
            if (FastMath.InRange(entry.Key.transform.GlobalPosition(), post, DefenceArrivedMeters))
            {
                continue;
            }

            // playerCommand: true is the whole mechanism. See the remarks on this class.
            CommanderGameAccess.GetUnitCommand(entry.Key)?.SetDestination(post, true);
        }
    }

    /// <summary>
    /// Anything hostile, seen recently, inside <see cref="ThreatRadiusMeters"/> of a base this
    /// faction holds. It reads the commander's own <c>trackingDatabase</c> and nothing else, so the
    /// enemy reacts to what its sensors actually hold — a player who comes in under the radar
    /// arrives against the resting ring, which is what flying low is for.
    /// </summary>
    private static bool IsUnderThreat(FactionHQ hq)
    {
        float now = Time.timeSinceLevelLoad;
        foreach (KeyValuePair<PersistentID, TrackingInfo> entry in hq.trackingDatabase)
        {
            TrackingInfo info = entry.Value;
            if (now - info.lastSpottedTime > ThreatMemorySeconds
                || !info.TryGetUnit(out Unit unit)
                || unit == null
                || unit.disabled
                || unit.NetworkHQ == null
                || ReferenceEquals(unit.NetworkHQ, hq))
            {
                continue;
            }

            // Buildings are in that database because RevealPlayerBase put them there, permanently.
            // A building is not an attack, and without this filter a base the player captured next
            // door would hold the commander in defence posture for the rest of the match.
            if (unit is Building)
            {
                continue;
            }

            if (IsNearOwnBase(hq, info.lastKnownPosition, ThreatRadiusMeters))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Whether <paramref name="position"/> is within <paramref name="radius"/> of an airbase
    /// this faction holds. Internal (one-word widening, Reuse rule 4): the threat posture reads it at
    /// <see cref="ThreatRadiusMeters"/>, and the home CAP's tracked-aircraft count reads the same
    /// ring test at the CAP's own threat radius — one definition, two callers.</summary>
    internal static bool IsNearOwnBase(FactionHQ hq, GlobalPosition position, float radius)
    {
        foreach (Airbase airbase in hq.GetAirbases())
        {
            if (airbase != null
                && !airbase.disabled
                && airbase.center != null
                && !CommanderGameAccess.IsShipAirbase(airbase)
                && FastMath.InRange(position, airbase.center.GlobalPosition(), radius))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The ring, rebuilt only when the number of bases changes — a base that changes hands moves the
    /// whole guard, but a post that drifts every review is a truck that never arrives.
    /// </summary>
    private static void EnsureDefencePosts(FactionHQ hq, CommanderState state)
    {
        int bases = 0;
        foreach (Airbase airbase in hq.GetAirbases())
        {
            if (airbase != null && !airbase.disabled && airbase.center != null && !CommanderGameAccess.IsShipAirbase(airbase))
            {
                bases++;
            }
        }

        if (bases == state.DefenceBaseCount && (bases == 0 || state.DefencePosts.Count > 0))
        {
            return;
        }

        state.DefenceBaseCount = bases;
        state.DefencePosts.Clear();
        foreach (Airbase airbase in hq.GetAirbases())
        {
            if (airbase == null || airbase.disabled || airbase.center == null || CommanderGameAccess.IsShipAirbase(airbase))
            {
                continue;
            }

            GlobalPosition center = airbase.center.GlobalPosition();
            float ring = Mathf.Clamp(
                airbase.SavedAirbase?.CaptureRange ?? 0f,
                DefenceRingMinMeters,
                DefenceRingMaxMeters);
            int movedOffAirfield = 0;
            for (int i = 0; i < DefencePostsPerBase; i++)
            {
                float angle = i * (Mathf.PI * 2f / DefencePostsPerBase);
                GlobalPosition post = CommanderGameAccess.SnapToTerrain(new GlobalPosition(
                    center.x + Mathf.Cos(angle) * ring,
                    center.y,
                    center.z + Mathf.Sin(angle) * ring));
                // Off the runways and taxiways (fix, 2026-09-15; see BuildHoldRing): a home guard
                // parked on the strip is what a taxiing fighter runs into.
                post = CommanderBuildPreview.OffAirfieldPost(center, post, out bool moved);
                if (moved)
                {
                    movedOffAirfield++;
                }

                // SnapToTerrain returns the seabed over water, so this is also what keeps a coastal
                // base's ring off the sea floor.
                if (!CommanderGameAccess.IsBelowSeaLevel(post))
                {
                    state.DefencePosts.Add(post);
                }
            }

            if (movedOffAirfield > 0)
            {
                CommanderAiLog.Note(
                    hq,
                    $"home guard at {CommanderCaptureService.GetAirbaseLabel(airbase)}: {movedOffAirfield} of {DefencePostsPerBase} posts moved off a runway or taxiway.");
            }
        }
    }

    private void PruneDefenders(FactionHQ hq, CommanderState state)
    {
        staleDefenders.Clear();
        foreach (KeyValuePair<Unit, int> entry in state.Defenders)
        {
            if (entry.Key == null
                || entry.Key.disabled
                || entry.Key.NetworkHQ != hq
                // Co-command: the player has taken this defender off the ring. Dropped from the
                // guard, but its commandedDestination is deliberately left alone — the player's own
                // route set it and owns it now, and clearing it would let the Basegame auto-walk
                // take the vehicle off mid-order. Once the route completes it leaves orders and the
                // next review may recruit it again, which is "not re-pinned until it arrives".
                || CommanderMoveService.Instance?.HasPlayerOrder(entry.Key) == true)
            {
                staleDefenders.Add(entry.Key!);
            }
        }

        for (int i = 0; i < staleDefenders.Count; i++)
        {
            state.Defenders.Remove(staleDefenders[i]);
        }
    }

    /// <summary>
    /// Hands the surplus back to the attack. Without this a commander that was raided once keeps a
    /// wartime guard for the rest of the match and stops pushing.
    /// </summary>
    private static void ReleaseSurplusDefenders(CommanderState state, int target)
    {
        while (state.Defenders.Count > target)
        {
            Unit? release = null;
            int highestPost = -1;
            foreach (KeyValuePair<Unit, int> entry in state.Defenders)
            {
                // The highest post index is the most recently filled one, so the ring thins back
                // the way it was built up rather than opening a hole in the middle of it.
                if (entry.Value > highestPost)
                {
                    highestPost = entry.Value;
                    release = entry.Key;
                }
            }

            if (release == null)
            {
                return;
            }

            state.Defenders.Remove(release);
            if (release is GroundVehicle vehicle && CommandedDestinationRef != null)
            {
                CommandedDestinationRef(vehicle) = false;
            }
        }
    }

    private void RecruitDefenders(FactionHQ hq, CommanderState state, int target)
    {
        defenceCandidates.Clear();
        if (hq.factionUnits == null)
        {
            return;
        }

        foreach (PersistentID id in hq.factionUnits)
        {
            if (id.TryGetUnit(out Unit unit)
                && unit is GroundVehicle
                && !unit.disabled
                && unit.definition is VehicleDefinition definition
                && IsCombatVehicle(definition)
                && !state.Defenders.ContainsKey(unit)
                // A vehicle the operations service has claimed into a platoon pool is spoken for
                // the same way a defender is - see CommanderOperationsService.
                && !CommanderOperationsService.IsPlatoonUnit(unit)
                // Co-command: a vehicle the player has given an order to is theirs until it gets
                // there. Pinning it to the ring would fight their own click.
                && CommanderMoveService.Instance?.HasPlayerOrder(unit) != true)
            {
                defenceCandidates.Add(unit);
            }
        }

        // Air defence first. A launcher contributes almost nothing to an attack and everything to a
        // base, so holding those back costs the push least and pays the ring most.
        defenceCandidates.Sort(static (left, right) =>
            DefencePriority(right).CompareTo(DefencePriority(left)));

        for (int i = 0; i < defenceCandidates.Count && state.Defenders.Count < target; i++)
        {
            state.Defenders[defenceCandidates[i]] = ChooseEmptiestPost(state);
        }
    }

    private static int DefencePriority(Unit unit)
    {
        return unit.definition is VehicleDefinition definition && IsAirDefence(definition) ? 1 : 0;
    }

    private int ChooseEmptiestPost(CommanderState state)
    {
        int posts = Mathf.Max(state.DefencePosts.Count, 1);
        if (postOccupancy.Length < posts)
        {
            postOccupancy = new int[posts];
        }

        for (int i = 0; i < posts; i++)
        {
            postOccupancy[i] = 0;
        }

        foreach (KeyValuePair<Unit, int> entry in state.Defenders)
        {
            postOccupancy[entry.Value % posts]++;
        }

        int best = 0;
        for (int i = 1; i < posts; i++)
        {
            if (postOccupancy[i] < postOccupancy[best])
            {
                best = i;
            }
        }

        return best;
    }

    /// <summary>
    /// One runnable check on the posture arithmetic, run beside the plan self-check at plugin load.
    /// These are tuning constants, and a retune that inverts them turns "under attack" into "send
    /// the guard away" without changing a line of logic.
    /// </summary>
    private static void CheckDefencePosture()
    {
        // Read into locals first: compared as constants the compiler folds the whole check away
        // and warns that the failure branch is unreachable, which is exactly the branch that has
        // to survive a retune.
        int resting = DefendersPerBase;
        int threatened = ThreatDefendersPerBase;
        int posts = DefencePostsPerBase;
        float innerRing = DefenceRingMinMeters;
        float outerRing = DefenceRingMaxMeters;
        if (threatened <= resting)
        {
            CommanderPlugin.Log.LogError(
                "Enemy defence self-check FAILED: the threatened guard is no larger than the resting one.");
        }

        if (posts < 1 || outerRing < innerRing)
        {
            CommanderPlugin.Log.LogError(
                "Enemy defence self-check FAILED: the base ring has no usable posts.");
        }
    }
}
