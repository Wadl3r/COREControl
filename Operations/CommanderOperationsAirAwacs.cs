using System.Collections.Generic;
using NuclearOption.SavedMission;
using UnityEngine;

namespace GroundControlRts;

/// <summary>
/// AWACS: the radar airframe's purchase, savings and station. Split out of <c>CommanderOperationsAir.cs</c> on 2026-09-15 purely for size (that file
/// had grown past six thousand lines); the code is moved, not rewritten, and the class doc on the
/// parent file still describes the whole. One partial class, several files.
/// </summary>
internal sealed partial class CommanderOperationsService
{
    // ---- AWACS (design.md, smarter-air-wing_20260914 Section 4) ----

    /// <summary>The hostile positions this review's radar station is measured against, and how far
    /// each is from the commander's own ground. Rebuilt once per station, reused across every
    /// candidate the search tries (<see cref="CollectAwacsThreats"/>).</summary>
    private static readonly List<Vector2> awacsThreats = new();

    private static readonly List<float> awacsThreatDistances = new();

    /// <summary>
    /// One radar airframe per commander, on station just outside its own airbase. Opened whenever
    /// the roster holds an airframe the commander's own builder can give the game's radar pod to and
    /// a held strip will launch — otherwise the sortie would stand unfillable for the whole match
    /// and hold a slot in the demand queue ahead of the CAS that could have used the money — and
    /// only while the commander does not already own one (<see cref="AwacsWanted"/>).
    /// </summary>
    private void AddAwacsDemand(FactionHQ hq, OperationsState state, List<CommanderAirSortie> demand)
    {
        if (!CommanderEnemyCommanderService.HasRoleCandidate(hq, CommanderEnemyCommanderService.AirRole.Awacs))
        {
            return;
        }

        GlobalPosition station = AwacsStation(
            hq, state, out string label, out float orbitRadius, out bool grounded, out string groundedReason);

        // Once per change, not once per review (user report, 2026-09-14): a station that cannot be
        // made safe stays unsafe for minutes at a time, and a line every thirty seconds would bury
        // the review it is meant to explain.
        if (!string.Equals(state.AwacsGroundedReason, groundedReason, System.StringComparison.Ordinal))
        {
            state.AwacsGroundedReason = groundedReason;
            CommanderAiLog.Note(
                hq,
                grounded
                    ? groundedReason
                    : $"AWACS station is clear again: {label}. The radar watch reopens.");
        }

        // No demand at all while the station is unsafe. Nothing is bought, and the radar airframe
        // the commander already owns is released by the reconcile — where both patrol doors refuse
        // it, so it is sent home to wait rather than orbiting inside the fight.
        if (grounded)
        {
            return;
        }

        demand.Add(new CommanderAirSortie
        {
            Kind = CommanderSortieKind.Awacs,
            Center = station,
            OrbitRadiusMeters = orbitRadius,
            Wanted = AwacsWanted(CountOwnedRadarAirframes(hq, state)),
            CapsWanted = 0,
            // The AWACS goes where it is told the moment it is airborne; there is nothing to form
            // up with and nothing to wait for.
            NoCapWait = true,
            Label = label,
        });
    }

    /// <summary>
    /// Owned radar airframes that are still alive, in any state — airborne, outbound, turning for
    /// home, sitting on the deck. This is the count the one-per-commander limit reads
    /// (<see cref="AwacsWanted"/>), and it is deliberately the SAME capability test the sortie fill
    /// binds on (<see cref="SortieAirRole"/> → <c>FillsAirRole</c>), so the commander can never own
    /// an airframe the AWACS slot would take and still call itself short of one.
    /// </summary>
    private static int CountOwnedRadarAirframes(FactionHQ hq, OperationsState state)
    {
        int count = 0;
        foreach (Aircraft owned in state.CommanderAirframes)
        {
            if (owned == null
                || owned.disabled
                || owned.definition is not AircraftDefinition definition)
            {
                continue;
            }

            if (CommanderEnemyCommanderService.FillsAirRole(
                    hq, definition, CommanderEnemyCommanderService.AirRole.Awacs))
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>
    /// Where the AWACS orbits (design SS4, departure 2026-09-14): the commander's own main airbase,
    /// offset <see cref="AwacsBaseOffsetMeters"/> toward the fight — the centre of the front when
    /// there is one (the mean of the front control points and the forward bases, the same two things
    /// the ground plan calls the front), otherwise the nearest asset another faction holds. With
    /// neither, the station is the airbase itself. <paramref name="label"/> is the station in words,
    /// for the tasking line and the review.
    /// <para>
    /// That offset is only the CANDIDATE (user report, 2026-09-14). The station actually returned is
    /// the nearest one to it whose whole orbit clears every enemy-held point or base and every
    /// tracked hostile by <c>CommanderSettings.AwacsMinEnemyDistanceMeters</c>
    /// (<see cref="TryAwacsStation"/> does that search), and the base it is measured from is one
    /// that is itself that clear — so the radar aeroplane is neither launched into the fight nor
    /// launched from a strip the fight has reached. <paramref name="grounded"/> is the answer when
    /// no such station exists at all: the caller opens no demand, and the radar watch waits.
    /// </para>
    /// </summary>
    private GlobalPosition AwacsStation(
        FactionHQ hq,
        OperationsState state,
        out string label,
        out float orbitRadius,
        out bool grounded,
        out string groundedReason)
    {
        float minEnemy = Mathf.Max(0f, CommanderSettings.AwacsMinEnemyDistanceMeters);
        CollectAwacsThreats(hq, awacsThreats);

        orbitRadius = AwacsOrbitRadiusMeters;
        grounded = false;
        groundedReason = string.Empty;

        // The strip the station is measured from has to be clear of the enemy itself, or the
        // aeroplane is shot at before it is airborne. A threatened main base hands the job to
        // whichever other held strip is clear, and the station then follows that strip — which is
        // also what steers the buy, because the launch picks the accepting base nearest the station.
        Airbase? main = MainAirbase(hq, minEnemy, awacsThreats);
        if (main == null)
        {
            label = "the radar orbit";
            grounded = true;
            groundedReason =
                $"AWACS grounded: no held airbase {minEnemy / 1000f:0} km clear of the enemy.";
            return HomeCAPCentre(hq);
        }

        GlobalPosition home = main.center != null
            ? main.center.GlobalPosition()
            : HomeCAPCentre(hq);
        string baseName = CommanderCaptureService.GetAirbaseLabel(main);

        float sumX = 0f;
        float sumZ = 0f;
        int count = 0;
        for (int i = 0; i < state.RankedPoints.Count; i++)
        {
            CommanderRankedPoint ranked = state.RankedPoints[i];
            if (!ranked.IsFront)
            {
                continue;
            }

            sumX += ranked.Point.Position.x;
            sumZ += ranked.Point.Position.z;
            count++;
        }

        for (int i = 0; i < state.Missions.Count; i++)
        {
            CommanderOperationsMission mission = state.Missions[i];
            if (mission.Kind != CommanderMissionKind.ForwardBase || mission.Point == null)
            {
                continue;
            }

            sumX += mission.Point.Position.x;
            sumZ += mission.Point.Position.z;
            count++;
        }

        float aimX;
        float aimZ;
        string toward;
        if (count > 0)
        {
            aimX = sumX / count;
            aimZ = sumZ / count;
            toward = "toward the front";
        }
        else if (TryNearestEnemyAsset(hq, home, out GlobalPosition asset, out _))
        {
            aimX = asset.x;
            aimZ = asset.z;
            toward = "toward the enemy";
        }
        else
        {
            aimX = home.x;
            aimZ = home.z;
            toward = string.Empty;
        }

        float dx = aimX - home.x;
        float dz = aimZ - home.z;
        float length = Mathf.Sqrt(dx * dx + dz * dz);
        bool hasAim = length >= 1f;
        Vector2 direction = hasAim ? new Vector2(dx / length, dz / length) : Vector2.zero;
        float candidate = AwacsStandoffMeters(hasAim, length);

        // The safety search (user report, 2026-09-14). Every station below is on the same line; the
        // one it picks is the nearest to the candidate whose whole orbit clears the enemy.
        if (!TryAwacsStation(
                new Vector2(home.x, home.z),
                direction,
                candidate,
                awacsThreats,
                minEnemy,
                AwacsOrbitRadiusMeters,
                out float offset,
                out orbitRadius))
        {
            label = $"the radar orbit over {baseName}";
            grounded = true;
            groundedReason = $"AWACS grounded: no station {minEnemy / 1000f:0} km clear of the enemy.";
            return home;
        }

        if (!hasAim || Mathf.Abs(offset) < 1f)
        {
            label = $"the radar orbit over {baseName}";
        }
        else if (offset > 0f)
        {
            label = $"{offset / 1000f:0} km from {baseName} {toward}";
        }
        else
        {
            // Behind the base, away from the fight: the safety search pushed it there, and the line
            // has to say so or a reader cannot tell a safe station from a broken one.
            label = $"{-offset / 1000f:0} km behind {baseName}";
        }

        return new GlobalPosition(
            home.x + (direction.x * offset),
            home.y,
            home.z + (direction.y * offset));
    }

    /// <summary>The commander's main base: the airbase it holds nearest its own territory centre —
    /// the aggregate every other "home ground" read in this service already uses (Reuse rule 4).
    /// Falls back to that centre when it holds no airbase at all.</summary>
    private static GlobalPosition MainBasePosition(FactionHQ hq)
    {
        Airbase? best = MainAirbase(hq);
        return best != null && best.center != null
            ? best.center.GlobalPosition()
            : HomeCAPCentre(hq);
    }

    /// <summary>The airbase behind <see cref="MainBasePosition"/> — the same walk, handing back the
    /// base itself rather than only where it is (Reuse rule 5, behaviour-neutral). The AWACS station
    /// reads it, because a station measured from the main base has to be able to NAME it.</summary>
    private static Airbase? MainAirbase(FactionHQ hq)
    {
        return MainAirbase(hq, 0f, System.Array.Empty<Vector2>());
    }

    /// <summary>
    /// The same walk with a clearance to keep (Reuse rule 5, behaviour-neutral at zero clearance):
    /// the nearest held airbase to the territory centre that has nothing hostile within
    /// <paramref name="minThreatClearanceMeters"/> of it. Null when the commander holds no airbase
    /// at all, or when every one it holds has the enemy on top of it — which is what grounds the
    /// radar watch rather than launching it off a strip under attack (user report, 2026-09-14).
    /// </summary>
    private static Airbase? MainAirbase(
        FactionHQ hq, float minThreatClearanceMeters, IReadOnlyList<Vector2> threats)
    {
        GlobalPosition centre = HomeCAPCentre(hq);
        Airbase? best = null;
        float bestDistance = float.MaxValue;
        foreach (Airbase airbase in hq.GetAirbases())
        {
            if (airbase == null || airbase.disabled || airbase.center == null)
            {
                continue;
            }

            GlobalPosition position = airbase.center.GlobalPosition();
            if (minThreatClearanceMeters > 0f
                && NearestEnemyMeters(new Vector2(position.x, position.z), threats) < minThreatClearanceMeters)
            {
                continue;
            }

            float distance = CommanderGameAccess.HorizontalDistance(position.AsVector3(), centre.AsVector3());
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = airbase;
            }
        }

        return best;
    }

    /// <summary>Hostile positions the radar station has to stand clear of, in x/z: every point or
    /// base another live HQ holds (<see cref="TryNearestEnemyAsset"/>'s walk — base points carry
    /// every airbase and resolve their owner live, so that one list answers both), plus the last
    /// known position of every tracked hostile ground vehicle and aircraft. The tracking walk is the
    /// one <see cref="CountHostileAirInRing"/> and <see cref="NearestTrackedHostileGroundDistance"/>
    /// already use — same <c>ThreatMemorySeconds</c> freshness, same skip of buildings, which sit in
    /// the database for ever once revealed and shoot at nothing.
    /// <para>Capped at <see cref="AwacsThreatSampleCap"/> entries, nearest to the commander's own
    /// ground first: past that the list cannot change the answer and the search is walked once per
    /// candidate station.</para></summary>
    private static void CollectAwacsThreats(FactionHQ hq, List<Vector2> into)
    {
        into.Clear();
        GlobalPosition home = HomeCAPCentre(hq);
        Vector3 homeVector = home.AsVector3();

        awacsThreatDistances.Clear();
        IReadOnlyList<CommanderStrategicPoint>? points = CommanderStrategicPointService.Instance?.Points;
        if (points != null)
        {
            for (int i = 0; i < points.Count; i++)
            {
                CommanderStrategicPoint point = points[i];
                FactionHQ? owner = point.GetOwner();
                if (owner == null || ReferenceEquals(owner, hq))
                {
                    continue;
                }

                AddAwacsThreat(into, point.Position, homeVector);
            }
        }

        float now = Time.timeSinceLevelLoad;
        foreach (KeyValuePair<PersistentID, TrackingInfo> entry in hq.trackingDatabase)
        {
            TrackingInfo info = entry.Value;
            if (now - info.lastSpottedTime > CommanderEnemyCommanderService.ThreatMemorySeconds
                || !info.TryGetUnit(out Unit unit)
                || unit == null
                || unit.disabled
                || unit is Building
                || (unit is not GroundVehicle && unit is not Aircraft)
                || unit.NetworkHQ == null
                || ReferenceEquals(unit.NetworkHQ, hq))
            {
                continue;
            }

            AddAwacsThreat(into, info.lastKnownPosition, homeVector);
        }
    }

    /// <summary>One entry of <see cref="CollectAwacsThreats"/>'s bounded, nearest-first list: kept
    /// while there is room, and otherwise kept only if it is nearer the commander's own ground than
    /// the furthest entry already held, which it then replaces.</summary>
    private static void AddAwacsThreat(List<Vector2> into, GlobalPosition position, Vector3 home)
    {
        float distance = CommanderGameAccess.HorizontalDistance(position.AsVector3(), home);
        Vector2 flat = new(position.x, position.z);
        if (into.Count < AwacsThreatSampleCap)
        {
            into.Add(flat);
            awacsThreatDistances.Add(distance);
            return;
        }

        int furthest = 0;
        for (int i = 1; i < awacsThreatDistances.Count; i++)
        {
            if (awacsThreatDistances[i] > awacsThreatDistances[furthest])
            {
                furthest = i;
            }
        }

        if (distance < awacsThreatDistances[furthest])
        {
            into[furthest] = flat;
            awacsThreatDistances[furthest] = distance;
        }
    }

    /// <summary>Whether this review's demand already holds a sortie for <paramref name="mission"/>.
    /// Null (a platoon between missions) is never already demanded.</summary>
    private static bool AlreadyDemanded(List<CommanderAirSortie> demand, CommanderOperationsMission? mission)
    {
        if (mission == null)
        {
            return false;
        }

        for (int i = 0; i < demand.Count; i++)
        {
            // Objective sorties only: the ARAD and CAP sorties over the same mission are its
            // companions, not the CAS wing it is asking for.
            if (demand[i].Kind == CommanderSortieKind.Objective && ReferenceEquals(demand[i].Mission, mission))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Where a sortie over a platoon sits: the platoon's leader — the moving centre the
    /// 3 km retarget hysteresis (<see cref="CasRetargetMeters"/>) already smooths — or its
    /// objective once the leader is gone.</summary>
    private static GlobalPosition PlatoonAirCenter(CommanderPlatoon platoon)
    {
        return platoon.Leader != null && !platoon.Leader.disabled
            ? platoon.Leader.transform.GlobalPosition()
            : platoon.Objective;
    }

    /// <summary>How near the enemy this platoon is: the shortest distance from either its leader or
    /// its objective to the nearest enemy-held point or base
    /// (<see cref="NearestEnemyAssetDistance"/> — base points carry every airbase and resolve their
    /// owner live, so that one walk answers both) or to the last known position of a tracked
    /// hostile ground unit. The objective counts as well as the leader: a platoon ordered at a
    /// point deep in enemy ground is marching into the fight whether or not it has arrived.</summary>
    private static float PreemptiveEnemyDistance(FactionHQ hq, CommanderPlatoon platoon, GlobalPosition center)
    {
        float best = Mathf.Min(
            NearestEnemyAssetDistance(hq, center),
            NearestTrackedHostileGroundDistance(hq, center));

        // With no live leader the centre already IS the objective; measuring it twice costs two
        // tracking walks for the same answer.
        if (platoon.Leader == null || platoon.Leader.disabled)
        {
            return best;
        }

        return Mathf.Min(
            best,
            Mathf.Min(
                NearestEnemyAssetDistance(hq, platoon.Objective),
                NearestTrackedHostileGroundDistance(hq, platoon.Objective)));
    }

    /// <summary>Shortest distance to the last known position of a tracked hostile ground unit — the
    /// <c>CountObserved</c> walk with its building skip and its <c>ThreatMemorySeconds</c>
    /// freshness, answering "how far" instead of "how many inside the ring".
    /// <c>float.MaxValue</c> when the commander has nothing tracked.</summary>
    private static float NearestTrackedHostileGroundDistance(FactionHQ hq, GlobalPosition from)
    {
        float best = float.MaxValue;
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

            float distance = CommanderGameAccess.HorizontalDistance(from.AsVector3(), info.lastKnownPosition.AsVector3());
            if (distance < best)
            {
                best = distance;
            }
        }

        return best;
    }

    private static CommanderOperationsMission? FindForwardBaseFor(OperationsState state, CommanderStrategicPoint point)
    {
        for (int i = 0; i < state.Missions.Count; i++)
        {
            CommanderOperationsMission mission = state.Missions[i];
            if (mission.Kind == CommanderMissionKind.ForwardBase && ReferenceEquals(mission.Point, point))
            {
                return mission;
            }
        }

        return null;
    }

    /// <summary>One demand entry: the CAP wing (baseline plus one per tracked hostile aircraft)
    /// is wanted over every active objective — even one with nothing observed, the tracking picture
    /// only shows what has already been spotted — and the CAS ladder reads the same effective
    /// observed count the ground attack sizing does (live picture, or the floor a failed attack
    /// left, whichever is larger).</summary>
    private void AddDemand(
        FactionHQ hq,
        OperationsState state,
        List<CommanderAirSortie> demand,
        CommanderOperationsMission? mission,
        CommanderPlatoon? platoon,
        GlobalPosition center,
        string label,
        bool immediate,
        bool preemptive = false,
        float enemyDistanceMeters = -1f,
        CommanderMissionKind kindForRotary = CommanderMissionKind.Attack)
    {
        SizeSortie(hq, state, mission, center, preemptive, out int cas, out int cap, out int observed, out int hostileAir);
        if (cas <= 0 && cap <= 0)
        {
            return;
        }

        // Ground contact, the one thing that outranks arriving whole (user decision 2026-09-16):
        // a platoon being shot at now, or an attack whose platoons are already on the objective.
        // The same two facts the in-contact read below is built from.
        bool groundContact = immediate
            || (mission != null && mission.Kind == CommanderMissionKind.Attack && mission.Launched);
        demand.Add(new CommanderAirSortie
        {
            Kind = CommanderSortieKind.Objective,
            Mission = mission,
            ContactPlatoon = platoon,
            Center = center,
            Wanted = cas,
            LastObserved = observed,
            LastHostileAir = hostileAir,
            // The ARAD-first rule's own number (user decision 2026-09-14), counted in the same ring
            // and off the same tracking picture the loss cooldown reads.
            LastAirDefence = CountObservedAirDefence(hq, center),
            CapsWanted = cap,
            NoCapWait = immediate,
            // Every sortie kind gathers before it goes in (user decision 2026-09-16). The decision
            // is taken ONCE, here, and carried in GoneIn through the reconcile, rather than
            // re-derived every review off counts the sizing rebuilds every thirty seconds — which
            // could flip a sortie out of forming halfway through its own form-up. Every other
            // creator leaves the field at its default of "already in": the radar watch, suppression,
            // lift cover and platoon contact cover are the exemptions, and they say so by kind or by
            // NoCapWait.
            GoneIn = !SortieFormsUp(CommanderSortieKind.Objective, cas, cap, groundContact, aradGoneIn: false),
            Preemptive = preemptive,
            EnemyDistanceMeters = enemyDistanceMeters,
            // Helicopter CAS by mission kind (design SS2). A contact sortie counts as contact even
            // when it carries a mission, because the reason it exists is the close fight.
            WantsRotary = SortieWantsRotaryCas(
                mission?.Kind ?? kindForRotary, contact: immediate, preemptive: preemptive),
            // Section 10: an attack whose platoons have gone in is in contact whatever the tracking
            // picture has decayed to — they are on the objective.
            InContact = SortieIsInContact(
                objectiveInContact: immediate,
                attackGoneIn: groundContact,
                observed,
                hostileAir),
            Label = label,
        });
    }

    /// <summary>
    /// How many strike and escort airframes an objective asks for, and the threat numbers it was
    /// sized from. Cut out of <see cref="AddDemand"/> so the pre-emptive queue can size a sortie it
    /// is NOT going to open this review (Reuse rule 4, one definition, two callers): a marker that
    /// says <c>Requesting CAS (0/2 · queued)</c> has to know the 2, and a second copy of this
    /// arithmetic would be free to drift from the sortie the platoon eventually gets.
    /// </summary>
    private void SizeSortie(
        FactionHQ hq,
        OperationsState state,
        CommanderOperationsMission? mission,
        GlobalPosition center,
        bool preemptive,
        out int cas,
        out int cap,
        out int observed,
        out int hostileAir)
    {
        observed = EffectiveObserved(
            CountObserved(hq, center),
            GetObservedFloor(state, ObservedFloorKey(mission?.Point, mission?.TargetAirbase)));
        hostileAir = CountHostileAirInRing(hq, center, ObservedRadiusMeters);
        (cas, cap) = preemptive
            ? PlanPreemptiveSortie(observed, hostileAir)
            : PlanSortie(observed, hostileAir);
    }

    /// <summary>Where a sortie over an attack sits: the point, or the base's hold point — the
    /// same target <c>UpdateAttacks</c> drives the platoons at.</summary>
    private static GlobalPosition AttackCenter(CommanderOperationsMission mission)
    {
        return mission.Point != null
            ? mission.Point.Position
            : CommanderCaptureService.GetHoldPointFor(mission.TargetAirbase!);
    }

    /// <summary>Match the live sorties onto this review's demand — by mission, or by the
    /// in-contact platoon (a point does not move, a contact can): update the matched in place,
    /// dissolve the rest, create the new. The state's list ends in demand order, which is the
    /// priority order everything else reads.</summary>
    private void ReconcileSorties(FactionHQ hq, OperationsState state, List<CommanderAirSortie> demand)
    {
        // Everything in this review's demand was asked for NOW: that is the clock the minimum hold
        // measures from, and a matched sortie takes the new entry's stamp with the rest of it.
        for (int d = 0; d < demand.Count; d++)
        {
            demand[d].LastDemandedAt = Time.time;
        }

        heldSorties.Clear();
        dissolvingSorties.Clear();
        for (int i = state.AirSorties.Count - 1; i >= 0; i--)
        {
            CommanderAirSortie live = state.AirSorties[i];
            bool matched = false;
            for (int d = 0; d < demand.Count; d++)
            {
                CommanderAirSortie wanted = demand[d];
                if (!SameSortie(live, wanted))
                {
                    continue;
                }

                // The strike package is re-posted as the SAME object (AddStrikeDemand): it carries
                // its own progress — the go-in, the delivery, the pinned element types — because
                // nothing in the world re-derives a deliberate strike the way a platoon in contact
                // re-derives its CAS. Copying it onto itself would double every binding it holds.
                if (ReferenceEquals(live, wanted))
                {
                    wanted.Matched = true;
                    matched = true;
                    break;
                }

                wanted.Cas.AddRange(live.Cas);
                wanted.Caps.AddRange(live.Caps);
                wanted.CooldownUntil = live.CooldownUntil;
                // The package's own progress survives the review: a package that has gone in stays
                // gone in, and one still forming keeps the clock it started. The OR is the way out
                // of a hold (user decision 2026-09-16): `wanted.GoneIn` already carries this
                // review's own verdict, so a forming sortie goes in the moment its objective comes
                // into ground contact, or its demand shrinks to a single aircraft.
                wanted.GoneIn = live.GoneIn || wanted.GoneIn;
                wanted.FormUpPoint = live.FormUpPoint;
                wanted.HasFormUp = live.HasFormUp;
                wanted.FormUpFirstArrivalAt = live.FormUpFirstArrivalAt;
                wanted.FormUpReported = live.FormUpReported;
                // The go-in bar the package was ordered with (fix, 2026-09-16). Carried for the same
                // reason the form-up clock is: rebuilding it from this review's demand would hand the
                // fall-back's call for more fighters straight back to the package as a higher bar,
                // which is the runaway target this freeze exists to stop.
                wanted.GoInCasWanted = live.GoInCasWanted;
                wanted.GoInCapsWanted = live.GoInCapsWanted;
                // The suppression memory (user decision 2026-09-14): kept while the belt is still
                // there, dropped the moment it thins out, so the helicopters wait again for a belt
                // that comes back. The count on `live` is the one the LAST review measured, which is
                // exactly the "has it been suppressed since the count rose" question.
                wanted.AradFlown = AradMemorySurvives(live.AradFlown, live.LastAirDefence);
                // The station band the sortie was given when it opened (design.md,
                // strike-packages_20260915 Section 4). Carried across for the same reason the
                // form-up clock is: a patrol re-assigned a band every review would spend the match
                // climbing and descending instead of patrolling.
                wanted.CapBand = live.CapBand;
                // The air fallback posture (design.md, air-fallback-posture_20260916 Section 4.2):
                // a live fact about the fighters in the air, carried like the form-up clock. The call
                // for help rides with it, so the retask this same review runs sees the raised demand
                // rather than the figure the sizing gives a sortie that is not outnumbered.
                wanted.HoldReason = live.HoldReason;
                wanted.FallingBackSince = live.FallingBackSince;
                wanted.FallbackPoint = live.FallbackPoint;
                wanted.FallbackReported = live.FallbackReported;
                if (live.HoldReason == CommanderAirHoldReason.Belt)
                {
                    // The belt hold's own call for help, carried like the fighters' (design.md,
                    // air-survival-layer_20260916 Layer 3): without it the sizing would hand the
                    // sortie back a suppression demand of zero every thirty seconds and the sweep it
                    // is waiting for would never be bought.
                    wanted.AradWanted = Mathf.Max(wanted.AradWanted, live.AradWanted);
                }
                if (live.FallingBack)
                {
                    wanted.InContact = true;
                    wanted.CapsWanted = Mathf.Max(wanted.CapsWanted, live.CapsWanted);
                }
                wanted.Matched = true;
                matched = true;
                break;
            }

            if (!matched)
            {
                // Decided below, not here: an airframe released from this sortie is offered to the
                // ones that survived, and it must see how full they REALLY are — which is only true
                // once every match above has moved its bindings across.
                dissolvingSorties.Insert(0, live);
            }

            state.AirSorties.RemoveAt(i);
        }

        for (int i = 0; i < dissolvingSorties.Count; i++)
        {
            CommanderAirSortie live = dissolvingSorties[i];

            // The minimum hold (team lead, 2026-09-14): a CAS objective and a platoon CAP both
            // open and close on a tracking picture that blinks, so a sortie whose demand has just
            // closed is kept — asking for nothing more — until the hold runs out. An empty one is
            // let go at once: there is nothing to hold.
            bool holdable = SortieIsHoldable(live.Kind) && (live.Cas.Count > 0 || live.Caps.Count > 0);
            float sinceDemanded = live.LastDemandedAt < 0f ? -1f : Time.time - live.LastDemandedAt;
            if (MayReleaseSortie(holdable, sinceDemanded, SortieMinHoldSeconds))
            {
                ReleaseSortie(hq, state, live, demand);
            }
            else
            {
                HoldSortie(hq, live, sinceDemanded);
                heldSorties.Insert(0, live);
            }
        }

        dissolvingSorties.Clear();

        // Every sortie that is NEW this review and wants fighters takes the next station band
        // (design.md, strike-packages_20260915 Section 4). Only the new ones: a matched sortie kept
        // the band it already had, one line up, so the rotation spreads the wing across the three
        // heights instead of moving one patrol up and down.
        for (int d = 0; d < demand.Count; d++)
        {
            CommanderAirSortie sortie = demand[d];
            if (!sortie.Matched && sortie.CapsWanted > 0 && sortie.CapBand < 0)
            {
                AssignCapBand(ref state.CapBandCursor, sortie);
            }
        }

        state.AirSorties.AddRange(demand);

        // Behind everything actually demanded: a held sortie is the first place a fight in contact
        // takes an airframe from (RetaskSourceRank reads it as quiet), and the last place the fill
        // or the buy looks — which it never does, because it asks for exactly what it holds.
        state.AirSorties.AddRange(heldSorties);
        heldSorties.Clear();

        // Announce the sorties that are new this review, not the ones that were already flying
        // (design SS5's "opens ARAD" line). The reconcile is the only place that knows which is
        // which.
        for (int i = 0; i < demand.Count; i++)
        {
            CommanderAirSortie sortie = demand[i];
            if (sortie.Matched || sortie.Kind != CommanderSortieKind.Arad)
            {
                continue;
            }

            CommanderAiLog.Note(
                hq,
                $"opens ARAD over {sortie.Label}: {sortie.LastObserved} air-defence vehicles clustered "
                    + $"({sortie.Wanted} airframe{(sortie.Wanted == 1 ? string.Empty : "s")}).");
        }
    }

    /// <summary>
    /// Whether a live sortie and a wanted one are the same sortie. The kind comes first: an ARAD
    /// sortie and the CAS sortie over the same objective share a mission and must never be matched
    /// onto each other. There is one AWACS per commander, so its kind alone identifies it; an ARAD
    /// cluster carries no mission of its own, so it is matched by how far its centroid has drifted.
    /// </summary>
    private static bool SameSortie(CommanderAirSortie live, CommanderAirSortie wanted)
    {
        if (live.Kind != wanted.Kind)
        {
            return false;
        }

        if (live.Kind == CommanderSortieKind.Awacs)
        {
            return true; // one per commander, so the kind alone identifies it
        }

        if (live.Kind == CommanderSortieKind.Strike)
        {
            // Several strike packages can be open at once since concurrent attacks (one per attack
            // plus the deliberate one), and each is re-posted as the same object, so identity is the
            // match. Matching by kind merged one package's airframes and clocks into another's and
            // bound them to both (review H9).
            return ReferenceEquals(live, wanted);
        }

        if (live.Kind == CommanderSortieKind.Arad)
        {
            // A belt carries no mission of its own, and one objective can have two belts near it,
            // so an ARAD sortie is matched by how far its centroid has drifted and nothing else.
            return CommanderGameAccess.HorizontalDistance(live.Center.AsVector3(), wanted.Center.AsVector3())
                <= AradClusterLinkMeters;
        }

        if (wanted.Mission != null && ReferenceEquals(live.Mission, wanted.Mission))
        {
            return true;
        }

        if (wanted.ContactPlatoon != null)
        {
            return ReferenceEquals(live.ContactPlatoon, wanted.ContactPlatoon);
        }

        // A transport escort carries neither a mission nor a platoon; its label is its identity
        // (fix, 2026-09-14: unmatched, every review closed the old escort and opened a new one, so
        // the fighter was re-tasked 21 times over one FOB site and the sortie read "stopped calling
        // for air support" while the flight was still in the air).
        return wanted.Mission == null && live.Mission == null && live.ContactPlatoon == null && live.Label == wanted.Label;
    }

    /// <summary>
    /// Keep a sortie whose demand has closed, for the rest of its minimum hold
    /// (<see cref="SortieMinHoldSeconds"/>). It asks for exactly what it already holds, so neither
    /// the fill nor the buy adds to it, and it reads as quiet — which makes it the first place a
    /// fight in contact takes an airframe from.
    /// </summary>
    private static void HoldSortie(FactionHQ hq, CommanderAirSortie sortie, float secondsSinceDemanded)
    {
        sortie.Wanted = sortie.Cas.Count;
        // A sortie falling back keeps its call for help through the hold (review, 2026-09-16):
        // lowering its wanted fighters to what it holds and marking it quiet made it the FIRST place
        // the same review's retask took a fighter from — stripping the sortie that was asking.
        if (!sortie.FallingBack)
        {
            sortie.CapsWanted = sortie.Caps.Count;
            sortie.InContact = false;
        }

        sortie.Matched = false;
        sortie.RetaskedThisReview = false;
        if (sortie.Held)
        {
            return; // the line is one per hold, not one per review of it
        }

        sortie.Held = true;
        CommanderAiLog.Note(
            hq,
            $"holds the wing over {sortie.Label} for now: it stopped calling for air support "
                + $"{Mathf.Max(0f, secondsSinceDemanded):0} s ago, inside the {SortieMinHoldSeconds:0} s hold.");
    }

    /// <summary>
    /// Let a sortie's airframes go. Design SS2 said "the next-hungry sortie (the fill pass below
    /// does that) or the standing home CAP posture", which left a helicopter with nothing at all:
    /// the fill pass only ever takes UNBOUND airframes and the standing patrol is an air-superiority
    /// orbit a rotary pilot cannot fly, so a released SAH-46 Chicane sat over its dissolved
    /// objective doing nothing (team lead, 2026-09-14). Every released airframe is now offered to
    /// every other open sortie first — nearest first, by what it can actually do — and only what
    /// nothing wants goes to the patrol or home.
    /// </summary>
    /// <param name="openSorties">This review's demand: the sorties that survive the reconcile, which
    /// are the only ones worth offering an airframe to.</param>
    private void ReleaseSortie(
        FactionHQ hq, OperationsState state, CommanderAirSortie sortie, List<CommanderAirSortie> openSorties)
    {
        ReleaseBoundAirframes(hq, state, sortie, sortie.Cas, openSorties);
        ReleaseBoundAirframes(hq, state, sortie, sortie.Caps, openSorties);
    }

    private void ReleaseBoundAirframes(
        FactionHQ hq,
        OperationsState state,
        CommanderAirSortie released,
        List<Aircraft> bound,
        List<CommanderAirSortie> openSorties)
    {
        for (int i = 0; i < bound.Count; i++)
        {
            Aircraft aircraft = bound[i];
            if (aircraft == null || aircraft.disabled)
            {
                continue;
            }

            // Retask before RTB, always (ReleaseDisposal is the rule; this is the one place that
            // acts on it).
            bool mayPatrol = aircraft.definition is AircraftDefinition definition
                && CommanderEnemyCommanderService.MayHoldPatrol(definition);
            CommanderReleaseDisposal disposal = ReleaseDisposal(
                TryRetaskReleasedAirframe(hq, state, released, aircraft, openSorties), mayPatrol);
            if (disposal == CommanderReleaseDisposal.Retask)
            {
                continue;
            }

            SendReleasedAirframeHome(hq, released, aircraft, disposal);
        }
    }

    /// <summary>
    /// Offer one released airframe to every other open sortie, nearest first, and bind it to the
    /// best match. A sortie wants it when it is short of a slot the airframe can actually fill — the
    /// same capability test the fill and the buy use (<c>FillsAirRole</c>) — and is not standing
    /// down after a loss. The strike slot wins over the escort slot when the airframe could take
    /// either: that is the thing the sortie exists for.
    /// </summary>
    private bool TryRetaskReleasedAirframe(
        FactionHQ hq,
        OperationsState state,
        CommanderAirSortie released,
        Aircraft aircraft,
        List<CommanderAirSortie> openSorties)
    {
        if (aircraft.definition is not AircraftDefinition definition)
        {
            return false;
        }

        bool radarAirframe = IsAwacsAirframe(hq, aircraft);
        Vector3 at = aircraft.transform.GlobalPosition().AsVector3();
        CommanderAirSortie? best = null;
        bool bestAsCap = false;
        float bestDistance = float.MaxValue;

        for (int i = 0; i < openSorties.Count; i++)
        {
            CommanderAirSortie open = openSorties[i];
            if (ReferenceEquals(open, released)
                || Time.time < open.CooldownUntil
                // The radar rule, both ways round (user decision 2026-09-14).
                || !MayBindToSortie(radarAirframe, IsAwacsSortie(open)))
            {
                continue;
            }

            bool wantsCas = open.Cas.Count < open.Wanted
                && CommanderEnemyCommanderService.FillsAirRoleNow(hq, aircraft, SortieAirRole(open));
            bool wantsCap = open.Caps.Count < open.CapsWanted
                && CommanderEnemyCommanderService.FillsAirRoleNow(
                    hq, aircraft, CommanderEnemyCommanderService.AirRole.Fighter);
            if (!wantsCas && !wantsCap)
            {
                continue;
            }

            float distance = CommanderGameAccess.HorizontalDistance(at, open.Center.AsVector3());
            if (distance >= bestDistance)
            {
                continue;
            }

            bestDistance = distance;
            best = open;
            bestAsCap = !wantsCas;
        }

        if (best == null)
        {
            return false;
        }

        bool bound = bestAsCap
            ? BindCap(hq, state, best, aircraft)
            : BindCas(hq, state, best, aircraft, origin: null);
        if (!bound)
        {
            return false;
        }

        // It has just been moved, so the retask hold applies to it exactly as it would to an
        // airframe taken off a quiet sortie: one move per hold, not one per review.
        state.AirRetaskedAt[aircraft] = Time.time;
        best.RetaskedThisReview = true;
        CommanderAiLog.Note(
            hq,
            $"retasks {CommanderGameAccess.GetUnitLabel(aircraft)} from {released.Label} to {best.Label} "
                + $"({bestDistance / 1000f:0} km).");
        return true;
    }

    /// <summary>
    /// Nothing else wants this airframe, so it goes to the standing home patrol if it is the kind of
    /// aeroplane that holds one, and home otherwise. The patrol test is the idle sweep's own
    /// (<c>MayHoldPatrol</c>): a ground-attack specialist and a helicopter are both refused it, and a
    /// helicopter is refused it precisely because it cannot fly an air-superiority orbit at all —
    /// which is how a released Chicane ended up with no task (team lead, 2026-09-14).
    /// </summary>
    private static void SendReleasedAirframeHome(
        FactionHQ hq, CommanderAirSortie released, Aircraft aircraft, CommanderReleaseDisposal disposal)
    {
        string label = CommanderGameAccess.GetUnitLabel(aircraft);
        // Nothing it goes to from here is a falling-back sortie, so the stamps come off first
        // (review, 2026-09-16): the home patrol and the landing order both keep the mission record.
        ClearPosture(aircraft);
        if (disposal == CommanderReleaseDisposal.HomeCap && IssueHomeCapTask(hq, aircraft))
        {
            CommanderAiLog.Note(
                hq, $"releases {label}: {released.Label} no longer calls for air support.");
            return;
        }

        if (CommanderAirCommandService.TryReturnAiAircraftHome(aircraft))
        {
            CommanderAiLog.Note(hq, $"sends {label} home: nothing calls for it.");
            return;
        }

        // The pad or strip refused it this review — a rotary landing state hands itself back to
        // combat whenever the pad it wanted is busy. The idle sweep tries again next review.
        CommanderAiLog.Note(
            hq,
            $"releases {label}: {released.Label} no longer calls for air support, and it could not be "
                + "sent home this review.");
    }

    /// <summary>Bind unbound owned airframes onto short sorties — the whole wing's CAP shortfall
    /// before any CAS (user decision 2026-09-13: CAP first), each in priority order — retasking
    /// what is already airborne before any new hull is bought. The first CAP of a sortie is the
    /// escort its CAS waits on (Approval 1), so a sortie that wants both gets its fighter bound in
    /// the same review as or before its CAS.</summary>
    private void FillSorties(FactionHQ hq, OperationsState state)
    {
        // One pass, sortie by sortie in priority order, each sortie filled escort → CAS → wingmen
        // (see NextSortieSlot). The two separate passes this replaced served EVERY sortie's whole
        // CAP demand before any sortie's CAS, which starved CAS completely once the wing had tens
        // of objectives open (2026-09-14 match: 37 CAP bindings, zero CAS).
        for (int i = 0; i < state.AirSorties.Count; i++)
        {
            CommanderAirSortie sortie = state.AirSorties[i];
            if (Time.time < sortie.CooldownUntil)
            {
                continue; // a sortie standing down after a loss is not refilled either
            }

            while (true)
            {
                CommanderAirSlot slot = NextSortieSlot(
                    sortie.Caps.Count, sortie.CapsWanted, sortie.Cas.Count, sortie.Wanted);
                if (slot == CommanderAirSlot.None)
                {
                    break;
                }

                bool asCap = slot != CommanderAirSlot.Cas;
                Aircraft? aircraft = TakeUnboundOwned(
                    hq,
                    state,
                    asCap ? CommanderEnemyCommanderService.AirRole.Fighter : SortieAirRole(sortie));

                // Only when nothing else is free: the home CAP is lent forward rather than left
                // orbiting an empty base (design SS9), but a fighter that is already spare is
                // always the cheaper answer. A strike slot is never filled from the home patrol —
                // those fighters are bought to fight air.
                bool lent = false;
                if (aircraft == null && asCap)
                {
                    aircraft = TakeLendableHomeCapFighter(hq, state, sortie.Center);
                    lent = aircraft != null;
                }

                if (aircraft == null)
                {
                    break;
                }

                if (lent)
                {
                    CommanderAiLog.Note(
                        hq,
                        $"lends {CommanderGameAccess.GetUnitLabel(aircraft)} from home CAP to {sortie.Label} "
                            + "(base ring clear).");
                }

                // A refused bind ends this sortie's fill rather than going round again: the
                // selection above would hand back the same airframe, and the loop would spin.
                bool bound = asCap
                    ? BindCap(hq, state, sortie, aircraft)
                    : BindCas(hq, state, sortie, aircraft, origin: null);
                if (!bound)
                {
                    if (lent)
                    {
                        state.LentHomeCap.Remove(aircraft);
                    }

                    break;
                }
            }
        }
    }

    /// <summary>An owned airframe able to fill <paramref name="role"/> that no live sortie holds,
    /// live and flying either one of this commander's tasks or nothing — matched by capability, the
    /// same test the buy and the claim use (user decision 2026-09-14), so a CAP-bought Compass is
    /// fillable as a sortie's escort however its role identity reads. Anything carrying a mission
    /// this commander never issued is the player's now (an RTS order adopts in place): it is let go
    /// entirely, and never retasked by the posture either — decision 5, the air-side hands-off
    /// rule.</summary>
    /// <summary>What an airframe must be able to do to fill this sortie's main slot: ground attack
    /// for an objective, the radar pod for the AWACS, anti-radiation weapons for an ARAD sortie.
    /// A CAP-kind sortie has no main slot — its whole demand is fighters.</summary>
    private static CommanderEnemyCommanderService.AirRole SortieAirRole(CommanderAirSortie sortie)
    {
        return sortie.Kind switch
        {
            CommanderSortieKind.Awacs => CommanderEnemyCommanderService.AirRole.Awacs,
            CommanderSortieKind.Arad => CommanderEnemyCommanderService.AirRole.Arad,
            _ => CommanderEnemyCommanderService.AirRole.Strike,
        };
    }
}
