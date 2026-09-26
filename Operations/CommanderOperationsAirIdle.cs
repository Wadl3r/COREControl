using System.Collections.Generic;
using NuclearOption.SavedMission;
using UnityEngine;

namespace GroundControlRts;

/// <summary>
/// The idle sweep: what an airframe with no sortie does. Split out of <c>CommanderOperationsAir.cs</c> on 2026-09-15 purely for size (that file
/// had grown past six thousand lines); the code is moved, not rewritten, and the class doc on the
/// parent file still describes the whole. One partial class, several files.
/// </summary>
internal sealed partial class CommanderOperationsService
{
    // ---- Idle sweep (design.md, smarter-air-wing_20260914 Section 6) ----

    private readonly List<Aircraft> idleSweep = new();

    /// <summary>Airframes adopted by this review's sweep, so their one log line says they were
    /// reclaimed rather than that they were idle.</summary>
    private readonly HashSet<Aircraft> adoptedStrays = new();

    /// <summary>
    /// Take back the airframes a hot reload orphaned (design SS15). A reload rebuilds this service
    /// from nothing: the ownership set is empty and the Air Command mission table only restores the
    /// player's own recipe-launched missions, so everything the commander had bought is left in the
    /// sky owned by nobody and told nothing. They are put back into the owned set here, and the
    /// sweep below then tiers and tasks them exactly like any other idle airframe of ours.
    /// </summary>
    private void AdoptStrayAircraft(FactionHQ hq, OperationsState state, CommanderAirCommandService airCommand)
    {
        adoptedStrays.Clear();
        // The registration stamps outlive their aeroplanes otherwise: the set is written from a
        // Harmony postfix that has no matching "unregistered" hook, so it is pruned here.
        state.RegisteredWhileCommanded.RemoveWhere(static seen => seen == null || seen.disabled);
        if (hq.factionUnits == null)
        {
            return;
        }

        bool duel = CommanderEnemyCommanderService.IsDuelMission;
        foreach (PersistentID id in hq.factionUnits)
        {
            if (!id.TryGetUnit(out Unit unit) || unit is not Aircraft aircraft || unit.disabled)
            {
                continue;
            }

            if (IsUnderOtherService(state, aircraft))
            {
                ReportOtherService(hq, state, aircraft);
                continue;
            }

            if (!AdoptsStrayAircraft(
                    hasHumanPilot: aircraft.Player != null,
                    hasMission: airCommand.IsOnAnyMission(aircraft),
                    commanderOwned: state.CommanderAirframes.Contains(aircraft),
                    aiFlyable: aircraft.definition is AircraftDefinition definition
                        && CommanderAirCommandService.CanAiFly(definition),
                    duel,
                    registeredWhileCommanded: state.RegisteredWhileCommanded.Contains(aircraft)))
            {
                continue;
            }

            state.CommanderAirframes.Add(aircraft);
            adoptedStrays.Add(aircraft);
            // Its one line is printed by the sweep below, which knows what it was tasked with.
            state.IdleSweepReported.Remove(aircraft);
        }
    }

    /// <summary>
    /// No commander-owned airframe idles without a mission. Every review, anything this commander
    /// bought that carries no Air Command mission at all is given one: an airframe that can fight
    /// air joins the home CAP, a transport or one that has shot everything off its racks goes home,
    /// and everything else joins the home CAP too. One line per airframe — the AIR window's "Idle"
    /// list is what the user watched fill up, and the line is what says which airframe left it.
    /// <para>Never touches a player-ordered airframe: an aircraft the player has taken is no longer
    /// in <c>CommanderAirframes</c> (the hands-off release removes it), and one already carrying any
    /// mission is skipped here by definition.</para>
    /// </summary>
    private void SweepIdleAirframes(FactionHQ hq, OperationsState state)
    {
        CommanderAirCommandService? airCommand = CommanderAirCommandService.Instance;
        if (airCommand == null)
        {
            return;
        }

        AdoptStrayAircraft(hq, state, airCommand);
        RefundStuckOnDeck(hq, state, airCommand);

        idleSweep.Clear();
        foreach (Aircraft owned in state.CommanderAirframes)
        {
            if (owned == null || owned.disabled)
            {
                continue;
            }

            if (airCommand.IsOnAnyMission(owned))
            {
                // Back under orders: the next time it falls idle is news again.
                state.IdleSweepReported.Remove(owned);
                continue;
            }

            // Section 17: a supply run and a picket insertion are flown by another service, and
            // neither carries an Air Command mission — so an insertion halfway to its landing zone
            // read as idle and was ordered home.
            if (IsUnderOtherService(state, owned))
            {
                ReportOtherService(hq, state, owned);
                continue;
            }

            // A helicopter bound to a sortie never gets an Air Command mission (the tasking takes
            // aeroplanes only), so it read as idle here and was sent home every review, and rotary
            // CAS never flew (review H16). Bound, it belongs to its sortie and flies on the game's
            // own helicopter AI.
            if (!CommanderAirCommandService.HasPlanePilot(owned) && IsBoundToAnySortie(state, owned))
            {
                continue;
            }

            idleSweep.Add(owned);
        }

        for (int i = 0; i < idleSweep.Count; i++)
        {
            Aircraft aircraft = idleSweep[i];
            AircraftDefinition? definition = aircraft.definition as AircraftDefinition;
            // An aeroplane adopted this review says so instead of saying it was idle: it was never
            // idle, it was ours all along and the reload lost the paperwork.
            string lead = adoptedStrays.Contains(aircraft)
                ? $"adopts {CommanderGameAccess.GetUnitLabel(aircraft)} (stray after reload); tasked"
                : $"{CommanderGameAccess.GetUnitLabel(aircraft)} was idle;";
            bool transport = definition != null
                && CommanderEnemyCommanderService.GetAirRole(definition) == CommanderEnemyCommanderService.AirRole.Transport;
            // A radar airframe on station is never judged on ammunition — that exemption lives in
            // the pilot hooks, where the mission is (CommanderAirCommandService.MissionIsOutOfAmmo).
            // This sweep only ever sees an airframe with NO mission, and a radar aeroplane with no
            // mission is sent home by the patrol refusal below whatever its racks hold.
            bool winchester = CommanderAirCommandService.IsWinchester(aircraft);

            // An airframe with no mission carries no RTB record either, so the landing order is
            // re-issued every review — the rotary landing state hands itself back to combat whenever
            // the pad it wanted is busy (see RotaryBouncedBackToCombat). The LINE is printed once.
            if ((transport || winchester) && CommanderAirCommandService.TryReturnAiAircraftHome(aircraft))
            {
                if (state.IdleSweepReported.Add(aircraft))
                {
                    CommanderAiLog.Note(
                        hq,
                        $"{lead} sent home "
                            + $"({(transport ? "a transport with nothing to deliver" : "nothing left on its racks")}).");
                }

                continue;
            }

            // A ground-attack specialist NEVER holds the patrol (user report, 2026-09-14: "seeing a
            // lot of air superiority brawlers - SHOULDN'T BE, they're CAS aircraft"). The posture
            // task below is an AIR SUPERIORITY task, so an idle strike-tier airframe goes home to
            // its pad instead — where it is ready for the next CAS sortie rather than burning fuel
            // over friendly ground waiting for a fight it cannot win. It is never handed the patrol
            // as a fallback: an airframe that cannot RTB this review is simply left for the next
            // sweep, because a Brawler on CAP is the bug, not the safety net.
            // The radar aeroplane is refused the patrol by the same door (user decision 2026-09-14:
            // it is never retasked off radar watch). It goes home and waits for the radar watch to
            // take it rather than burning its fuel on an air-superiority orbit it cannot fly.
            bool radarAirframe = IsAwacsAirframe(hq, aircraft);
            bool mayPatrol = definition != null
                && CommanderEnemyCommanderService.MayHoldPatrol(definition)
                && !radarAirframe;
            if (!mayPatrol)
            {
                if (CommanderAirCommandService.TryReturnAiAircraftHome(aircraft)
                    && state.IdleSweepReported.Add(aircraft))
                {
                    CommanderAiLog.Note(
                        hq,
                        $"{lead} sent home "
                            + $"({DescribePatrolRefusal(definition, radarAirframe)} is never put on the home CAP).");
                }

                continue;
            }

            if (!IssuePostureTask(hq, aircraft))
            {
                continue;
            }

            // A fighter put on the patrol by the sweep COUNTS as the patrol while the patrol is short
            // (fix, 2026-09-14): adopted strays were tasked on the home CAP but never entered
            // HomeCapAirframes, so after every hot reload the roster read 0, rung 1 bought two more
            // fighters, and the adopted ones flew the same orbit labelled UNTASKED — six spare
            // Revokers after three reloads. Once the patrol is full the fighter stays a spare that a
            // sortie may take, exactly like a bought fighter no sortie wanted.
            bool standing = AdoptIntoHomeCap(
                state.HomeCapAirframes.Count, WantedHomeCapNow(hq), definition != null && CommanderEnemyCommanderService.MayFlyAirSuperiority(definition));
            if (standing)
            {
                state.HomeCapAirframes.Add(aircraft);
            }

            if (state.IdleSweepReported.Add(aircraft))
            {
                CommanderAiLog.Note(hq, standing
                    ? $"{lead} on the home CAP ({state.HomeCapAirframes.Count}/{WantedHomeCapNow(hq)} standing)."
                    : $"{lead} on the home CAP as a spare until a sortie wants it.");
            }
        }

        idleSweep.Clear();
    }

    /// <summary>Whether a fighter the idle sweep puts on the patrol joins the STANDING patrol roster,
    /// pure: only while the roster is short of what the formula wants, and only an airframe that may
    /// fly air superiority. Anything else is a spare.</summary>
    internal static bool AdoptIntoHomeCap(int standingNow, int wanted, bool mayFlyAirSuperiority)
    {
        return mayFlyAirSuperiority && standingNow < wanted;
    }

    /// <summary>The sweep run from the enemy commander's own review as well as the operations one
    /// (Reuse rule 4: the residual posture loop in <c>TaskAirWing</c> used to duplicate it, one log
    /// line poorer). Does nothing for an HQ this service does not manage.</summary>
    internal static void SweepIdleAirframes(FactionHQ hq)
    {
        if (Instance != null && Instance.states.TryGetValue(hq, out OperationsState state))
        {
            Instance.SweepIdleAirframes(hq, state);
        }
    }

    /// <summary>Aircraft of one sortie that have reached the place it is GATHERING — both elements,
    /// which is what "has everybody arrived" means once a fighter patrol forms up too (user decision
    /// 2026-09-16). One definition for the go-in test, the forming line and the marker.</summary>
    private static int CountGathered(CommanderAirSortie sortie)
    {
        return CountGathered(sortie, sortie.Cas) + CountGathered(sortie, sortie.Caps);
    }

    /// <summary>
    /// Aircraft of one ELEMENT that have reached where they were actually sent. Each airframe is
    /// measured against its OWN gathering point (<see cref="GatheringPointFor"/>) rather than against
    /// the sortie's form-up point, because the fall-back posture moves the fixed wings back to the
    /// fall-back point and leaves the helicopters where they are (design.md,
    /// air-fallback-posture_20260916 Section 4.6). Counting everybody at the form-up point while the
    /// posture held them 15 km away is what pinned a defended strike at <c>pkg 0/4</c> for minutes on
    /// end with every aircraft it needed already up (user report 2026-09-16).
    /// </summary>
    private static int CountGathered(CommanderAirSortie sortie, List<Aircraft> bound)
    {
        // The whole sortie's gathering place, which is every airframe's unless the posture is
        // holding some of them back. Hoisted because the marker calls this every frame, and asking
        // each aeroplane whether it is a fixed wing walks its pilots and its weapon stations for an
        // answer that cannot change the point when nothing is being held back.
        GlobalPosition common = SortieStation(sortie);
        int count = 0;
        for (int i = 0; i < bound.Count; i++)
        {
            Aircraft aircraft = bound[i];
            if (aircraft == null || aircraft.disabled)
            {
                continue;
            }

            GlobalPosition gathering = sortie.FallingBack
                ? GatheringPointFor(sortie, IsFixedWingCombatAircraft(aircraft))
                : common;
            if (CommanderGameAccess.HorizontalDistance(
                    aircraft.transform.GlobalPosition().AsVector3(), gathering.AsVector3())
                <= PackageArrivalMeters)
            {
                count++;
            }
        }

        return count;
    }

    private readonly List<CommanderAirSortie> airDemand = new();

    /// <summary>Scratch list for <see cref="AddPreemptiveDemand"/>'s measuring pass, reused every
    /// review so the cap costs no allocation.</summary>
    private readonly List<PreemptiveCandidate> preemptiveCandidates = new();
    private readonly List<Aircraft> airStale = new();

    /// <summary>The review's sortie list in priority order (design SS1): attack targets whose
    /// groups have begun arriving (Approval 1 — the demand opens at first release-point arrival),
    /// then platoons in contact — marching, or holding their posts under attack (addendum
    /// 2026-09-14 §2) — then pickets and forward bases under attack with no platoon on them, then
    /// platoons marching near the enemy but not yet in contact (pre-emptive, user decision
    /// 2026-09-14), then threatened forward bases in ranked order. Quiet rear points and the
    /// reserve ask for nothing.</summary>
    private void BuildAirDemand(FactionHQ hq, OperationsState state, List<CommanderAirSortie> demand)
    {
        demand.Clear();

        // The AWACS first (design SS4): everything below it is sized from the tracking picture, and
        // the radar airframe is what fills that picture in.
        AddAwacsDemand(hq, state, demand);

        for (int i = 0; i < state.Missions.Count; i++)
        {
            CommanderOperationsMission mission = state.Missions[i];
            if (mission.Kind != CommanderMissionKind.Attack || mission.FirstGroupArrivedAt < 0f)
            {
                continue;
            }

            AddDemand(hq, state, demand, mission, null, AttackCenter(mission), mission.Label, immediate: false);
        }

        for (int i = 0; i < state.Platoons.Count; i++)
        {
            CommanderPlatoon platoon = state.Platoons[i];
            if ((platoon.State != CommanderPlatoonState.Moving && platoon.State != CommanderPlatoonState.Attacking
                    && platoon.State != CommanderPlatoonState.Holding)
                || platoon.InContactUntil < Time.time)
            {
                continue;
            }

            // Addendum 2026-09-14 §2: a platoon Holding its posts (a forward base garrison or the
            // reserve ring) that comes under attack is in contact without leaving them. Its
            // contact sortie is keyed to the mission as well as the platoon, so the
            // threatened-forward-base loop below does not open a second wing over the same ring,
            // and a second garrison platoon of the same mission joins the one sortie already
            // demanded for it instead of opening its own.
            CommanderOperationsMission? holdingMission =
                platoon.State == CommanderPlatoonState.Holding ? platoon.Mission : null;
            if (holdingMission != null && AlreadyDemanded(demand, holdingMission))
            {
                continue;
            }

            // Over the last tracked contact, not over the platoon: the CAS area filter picks its
            // targets out of its own ring, so the box has to sit on the enemy, not on the line
            // facing it.
            AddDemand(
                hq, state, demand, holdingMission, platoon, platoon.ContactBearingAnchor, platoon.Name, immediate: true);
        }

        // Addendum 2026-09-14 §2: a picket or forward base under attack with no platoon on it
        // carries its own contact clock (DetectMissionContact), so the point itself raises CAS at
        // contact priority — a picket's two vehicles are a detachment, not a platoon, and would
        // otherwise never open a sortie however hard they were hit.
        for (int i = 0; i < state.Missions.Count; i++)
        {
            CommanderOperationsMission mission = state.Missions[i];
            if ((mission.Kind != CommanderMissionKind.Picket && mission.Kind != CommanderMissionKind.ForwardBase)
                || mission.Point == null
                || mission.ContactUntil < Time.time
                || AlreadyDemanded(demand, mission))
            {
                continue;
            }

            AddDemand(hq, state, demand, mission, null, mission.Point.Position, mission.Label, immediate: true);
        }

        // The one deliberate strike package (design.md, strike-packages_20260915 Section 1): above
        // pre-emptive cover, below everything the ground is actually asking for now. It is the only
        // entry on this list the commander decided to fly rather than was shown, so it must outrank
        // a march that has met nothing — and it is whole-or-nothing to buy, so putting it above the
        // attacks and the platoons in contact would let one unaffordable package hold the whole
        // ground-attack side of the wing for the twelve minutes it lives. The sortie OBJECT is
        // re-posted rather than rebuilt: nothing in the world re-derives a deliberate strike, so a
        // fresh entry each review would lose the package's own progress with it.
        AddStrikeDemand(hq, state, demand);

        AddPreemptiveDemand(hq, state, demand);

        for (int i = 0; i < state.RankedPoints.Count; i++)
        {
            CommanderRankedPoint ranked = state.RankedPoints[i];
            if (!ranked.HasThreatMark)
            {
                continue;
            }

            CommanderOperationsMission? mission = FindForwardBaseFor(state, ranked.Point);
            if (mission != null && !AlreadyDemanded(demand, mission))
            {
                AddDemand(hq, state, demand, mission, null, ranked.Point.Position, mission.Label, immediate: false);
            }
        }

        // Every open lift's cover, BEFORE the suppression pass: a cover is what a belt near the
        // landing zone is bound to, and one posted after that pass would never be swept for
        // (air-mobile-platoons_20260915 Section 3).
        AddLiftCoverDemand(hq, state, demand);

        // Suppression and the platoon-requested CAP both read the objective list above, so they are
        // derived from it rather than woven into it.
        InsertAradDemand(hq, demand);
        AddPlatoonCapDemand(hq, state, demand);
    }

    /// <summary>
    /// Posts the open strike package as this review's demand (design.md, strike-packages_20260915
    /// Section 1). The sortie's element sizes were fixed when it was ordered and are only mirrored
    /// onto the slots the rest of the wing reads: strike plus bomber into the CAS slots, escort into
    /// the CAP slots. A package whose escorts have been released asks for none.
    /// </summary>
    private static void AddStrikeDemand(FactionHQ hq, OperationsState state, List<CommanderAirSortie> demand)
    {
        // Every open package since concurrent-attacks_20260918: the deliberate one and one per
        // attack. The body below is unchanged and simply runs once per package (Reuse rule 3).
        CollectStrikeSorties(state, strikeDemandScratch);
        for (int s = 0; s < strikeDemandScratch.Count; s++)
        {
            AddOneStrikeDemand(hq, state, demand, strikeDemandScratch[s]);
        }
    }

    /// <summary>Reused buffer for the walk above; this runs once per review per commander.</summary>
    private static readonly List<CommanderAirSortie> strikeDemandScratch = new();

    private static void AddOneStrikeDemand(
        FactionHQ hq, OperationsState state, List<CommanderAirSortie> demand, CommanderAirSortie strike)
    {
        _ = state;
        // The bomber element gives up rather than holding the package (fix, 2026-09-15). A base
        // package whose roster holds a bomber the wing can never pay for bought NOTHING for the
        // whole twelve minutes it lived, because the bomber is ordered ahead of the strike airframes
        // and a whole-or-nothing element that cannot be afforded refuses every review.
        if (strike.BomberWanted > 0 && strike.Cas.Count < strike.BomberWanted && !strike.GoneIn)
        {
            strike.BomberRefusedReviews++;
            if (StrikeGivesUpBomber(strike.BomberRefusedReviews, StrikeBomberRefusalReviews))
            {
                strike.BomberWanted = 0;
                CommanderAiLog.Note(hq, $"strike on {strike.Label}: no bomber affordable; going without.");
            }
        }

        // The escort is re-sized every review against the sky the package is actually flying into
        // (fix, 2026-09-15, decision 4): its scale's floor, never fewer than the hostile aircraft
        // tracked over the target now, never more than the room left under the airborne ceiling.
        strike.LastHostileAir = CountHostileAirInRing(hq, strike.Center, ObservedRadiusMeters);
        strike.EscortWanted = StrikeEscortWanted(
            strike.EscortFloor,
            strike.LastHostileAir,
            // The escorts already bound are part of the room they are occupying, so they are added
            // back: without that the package would shed its own escort as the sky filled up.
            EffectiveAirborneCeiling(hq) - CommanderEnemyCommanderService.CountAirborne(hq) + strike.Caps.Count);

        strike.Wanted = strike.StrikeWanted + strike.BomberWanted;
        strike.CapsWanted = strike.EscortWanted;
        demand.Add(strike);
    }

    /// <summary>
    /// Pre-emptive sorties (user decision 2026-09-14): every platoon under way — Moving or
    /// Attacking — whose leader or objective is inside <see cref="PreemptiveAirRangeMeters"/> of an
    /// enemy-held point or base or a tracked hostile gets a sortie over it BEFORE it is in contact,
    /// with a baseline airframe of each even at zero observed. A platoon already in contact was
    /// served by the loop above and is skipped here, so one platoon never opens two sorties.
    /// <para>The demand outlives the platoon leaving the ring by
    /// <see cref="PreemptiveAirHoldSeconds"/> — the hysteresis that stops a sortie being dissolved
    /// and re-bought review after review as a platoon skirts the ring — but ends at once when the
    /// platoon stops marching.</para>
    /// </summary>
    private void AddPreemptiveDemand(FactionHQ hq, OperationsState state, List<CommanderAirSortie> demand)
    {
        // Two passes, because the cap is "the N nearest the enemy" and that cannot be known until
        // every candidate has been measured (user report, 2026-09-14). The first pass still runs
        // over EVERY platoon: the hold clock below belongs to the platoon, not to the sortie, and a
        // platoon that stops marching must have it cleared whether or not it would have made the cut.
        preemptiveCandidates.Clear();
        for (int i = 0; i < state.Platoons.Count; i++)
        {
            CommanderPlatoon platoon = state.Platoons[i];
            if (platoon.State != CommanderPlatoonState.Moving && platoon.State != CommanderPlatoonState.Attacking)
            {
                // Formed up, dug in or falling back: the pre-emptive reason is gone, and the hold
                // does not apply to it — the hold covers a moving platoon crossing the ring's edge.
                platoon.PreemptiveAirUntil = -1f;
                continue;
            }

            GlobalPosition center = PlatoonAirCenter(platoon);
            float distance = PreemptiveEnemyDistance(hq, platoon, center);
            if (WantsPreemptiveAir(platoon.State, distance, PreemptiveAirRangeMeters))
            {
                platoon.PreemptiveAirUntil = Time.time + PreemptiveAirHoldSeconds;
            }

            // A platoon in contact was served by the loop above, and one whose own mission already
            // opened a sortie (its attack's groups have reached their release points) is already
            // covered — the pre-emptive rule exists for the march that has no sortie over it yet,
            // not to task the wing twice over one fight.
            if (platoon.PreemptiveAirUntil < Time.time || platoon.InContactUntil >= Time.time)
            {
                continue;
            }

            platoon.QueuedCasWanted = 0;
            platoon.QueuedCapWanted = 0;
            preemptiveCandidates.Add(new PreemptiveCandidate(platoon, center, distance));
        }

        // Nearest the enemy first, so the cap keeps the marches about to become fights and queues
        // the ones still crossing their own rear areas.
        preemptiveCandidates.Sort(
            static (a, b) => a.EnemyDistanceMeters.CompareTo(b.EnemyDistanceMeters));

        int cap = Mathf.Max(0, CommanderSettings.OperationsMaxPreemptiveAirObjectives);
        int served = 0;
        int queued = 0;
        for (int i = 0; i < preemptiveCandidates.Count; i++)
        {
            PreemptiveCandidate candidate = preemptiveCandidates[i];
            // AlreadyDemanded is re-read here rather than in the measuring pass: two platoons of one
            // mission would both have passed it before either had added anything.
            if (AlreadyDemanded(demand, candidate.Platoon.Mission))
            {
                continue;
            }

            if (served >= cap)
            {
                // Sized but not opened: the marker says what this platoon is waiting for, so the cap
                // reads as a queue rather than as the wing ignoring it (user request, 2026-09-14).
                SizeSortie(
                    hq, state, null, candidate.Center, preemptive: true,
                    out int queuedCas, out int queuedCap, out _, out _);
                candidate.Platoon.QueuedCasWanted = Mathf.Max(0, queuedCas);
                candidate.Platoon.QueuedCapWanted = Mathf.Max(0, queuedCap);
                if (queuedCas > 0 || queuedCap > 0)
                {
                    // A platoon the sizing would have given nothing anyway is not being held back by
                    // the cap, so it is not counted as queued.
                    queued++;
                }

                continue;
            }

            int before = demand.Count;
            AddDemand(
                hq, state, demand, null, candidate.Platoon, candidate.Center, candidate.Platoon.Name,
                immediate: false, preemptive: true, enemyDistanceMeters: candidate.EnemyDistanceMeters);
            if (demand.Count > before)
            {
                served++;
            }
        }

        ReportPreemptiveQueue(hq, state, served, queued, cap);
    }

    /// <summary>
    /// One line, only when the number of platoons queued for pre-emptive cover CHANGES, saying how
    /// many marches the cap is holding back (user report, 2026-09-14: "loads of requests for CAS
    /// from platoons and only a handful in the air"). The wing spreading one airframe a review
    /// across twenty sorties and the wing covering the four nearest marches properly look identical
    /// on the demand counters, so the cap has to say it is the thing doing it.
    /// </summary>
    private static void ReportPreemptiveQueue(FactionHQ hq, OperationsState state, int served, int queued, int cap)
    {
        if (queued == state.PreemptiveQueuedReported)
        {
            return;
        }

        state.PreemptiveQueuedReported = queued;
        if (queued <= 0)
        {
            return;
        }

        CommanderAiLog.Note(
            hq,
            $"pre-emptive air cover is capped at the {cap} marches nearest the enemy: "
                + $"{served} covered, {queued} queued.");
    }

    /// <summary>A platoon measured for pre-emptive cover before the cap is applied.</summary>
    private readonly struct PreemptiveCandidate
    {
        internal PreemptiveCandidate(CommanderPlatoon platoon, GlobalPosition center, float enemyDistanceMeters)
        {
            Platoon = platoon;
            Center = center;
            EnemyDistanceMeters = enemyDistanceMeters;
        }

        internal CommanderPlatoon Platoon { get; }

        internal GlobalPosition Center { get; }

        internal float EnemyDistanceMeters { get; }
    }
}
