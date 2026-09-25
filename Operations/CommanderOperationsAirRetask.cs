using System.Collections.Generic;
using NuclearOption.SavedMission;
using UnityEngine;

namespace GroundControlRts;

/// <summary>
/// Retask to contact. Split out of <c>CommanderOperationsAir.cs</c> on 2026-09-15 purely for size (that file
/// had grown past six thousand lines); the code is moved, not rewritten, and the class doc on the
/// parent file still describes the whole. One partial class, several files.
/// </summary>
internal sealed partial class CommanderOperationsService
{
    // ---- Retask to contact (design.md, smarter-air-wing_20260914 Section 10) ----

    /// <summary>
    /// Move airframes off sorties that are covering quiet ground and onto sorties whose objective is
    /// being shot at right now (user decision 2026-09-14). Runs after the packages have been
    /// resolved, so a moved airframe is told the right thing straight away, and before the task
    /// sync, which then simply agrees with it.
    /// <para>The quiet sortie's own demand reopens by itself: it is short again on the next review
    /// and is refilled or bought the ordinary way.</para>
    /// </summary>
    private void RetaskToContact(FactionHQ hq, OperationsState state)
    {
        for (int t = 0; t < state.AirSorties.Count; t++)
        {
            CommanderAirSortie target = state.AirSorties[t];
            if (!target.InContact || Time.time < target.CooldownUntil)
            {
                continue;
            }

            while (target.Cas.Count < target.Wanted
                && TryRetaskOne(hq, state, target, asCap: false))
            {
            }

            while (target.Caps.Count < target.CapsWanted
                && TryRetaskOne(hq, state, target, asCap: true))
            {
            }
        }
    }

    /// <summary>
    /// Take one airframe from the best available quiet sortie and put it on
    /// <paramref name="target"/>. Best means the lowest <see cref="RetaskSourceRank"/>, and within
    /// one rank the airframe nearest the target. Returns false when nothing may be moved.
    /// </summary>
    private bool TryRetaskOne(FactionHQ hq, OperationsState state, CommanderAirSortie target, bool asCap)
    {
        // The radar watch is never topped up by taking an airframe off something else: the only
        // airframe that could fill it is a radar one, and a radar one is never moved (MayBindToSortie).
        if (IsAwacsSortie(target))
        {
            return false;
        }

        CommanderEnemyCommanderService.AirRole role = asCap
            ? CommanderEnemyCommanderService.AirRole.Fighter
            : SortieAirRole(target);

        CommanderAirSortie? bestSortie = null;
        Aircraft? best = null;
        int bestRank = int.MaxValue;
        float bestDistance = float.MaxValue;

        for (int s = 0; s < state.AirSorties.Count; s++)
        {
            CommanderAirSortie source = state.AirSorties[s];
            if (ReferenceEquals(source, target)
                // Never the radar airframe, and never a suppression sortie that has gone in: both
                // are doing a job nothing else on the roster can do.
                || source.Kind == CommanderSortieKind.Awacs
                // A deliberate strike package is never stripped to reinforce something else
                // (design.md, strike-packages_20260915): it is ordered whole, of one type per
                // element, and taking an airframe out of it is how a package becomes a single
                // aeroplane over enemy ground.
                || source.Kind == CommanderSortieKind.Strike
                // Nor a lift cover (fix, 2026-09-15): eighteen times in one hour an escort was taken
                // off a transport for a contact elsewhere — `retasks FS-12 Revoker from LIFT ESCORT
                // RESOURCE SITE 20 to HILLTOP 19: contact outranks cover` — and the transport then
                // died alone 9–33 km short of its landing zone at treetop height. The escort exists
                // for the ten minutes the transport is in the air; the contact can have the next buy.
                // Marked, not label-matched (fix, 2026-09-18, found in this track's review): the
                // label test covered the forward-base lift and missed the picket insertion escort,
                // whose label reads "escort to <point>". The mark covers both.
                || source.IsTransportEscort
                || (source.Kind == CommanderSortieKind.Arad && source.GoneIn))
            {
                continue;
            }

            int rank = RetaskSourceRank(source.InContact, source.Preemptive, SortieHoldsAtFormUp(source));
            if (rank < 0 || rank > bestRank)
            {
                continue;
            }

            List<Aircraft> bound = asCap ? source.Caps : source.Cas;
            for (int i = 0; i < bound.Count; i++)
            {
                Aircraft aircraft = bound[i];
                if (!MayTakeForContact(hq, state, aircraft, role))
                {
                    continue;
                }

                float distance = CommanderGameAccess.HorizontalDistance(
                    aircraft.transform.GlobalPosition().AsVector3(), target.Center.AsVector3());
                if (rank < bestRank || distance < bestDistance)
                {
                    bestRank = rank;
                    bestDistance = distance;
                    best = aircraft;
                    bestSortie = source;
                }
            }
        }

        if (best == null || bestSortie == null)
        {
            return false;
        }

        (asCap ? bestSortie.Caps : bestSortie.Cas).Remove(best);
        (asCap ? target.Caps : target.Cas).Add(best);
        state.AirRetaskedAt[best] = Time.time;
        target.RetaskedThisReview = true;
        TaskOntoSortie(hq, state, target, best, asCap);
        CommanderAiLog.Note(
            hq,
            $"retasks {CommanderGameAccess.GetUnitLabel(best)} from {bestSortie.Label} to {target.Label}: "
                + "contact outranks cover.");
        return true;
    }

    /// <summary>Whether this bound airframe may be taken off its sortie for one in contact: alive,
    /// able to do the job, and out of its own retask hold. A home-CAP fighter on loan is taken like
    /// any other bound fighter (user decision 2026-09-16, replacing the 2026-09-14 rule that kept
    /// the one holding the base at its minimum): the home patrol is the lowest-priority holder of
    /// fighters, and a raid at the base recalls the loan through <c>RecallLentHomeCap</c>
    /// instead.</summary>
    private static bool MayTakeForContact(
        FactionHQ hq,
        OperationsState state,
        Aircraft aircraft,
        CommanderEnemyCommanderService.AirRole role)
    {
        if (aircraft == null
            || aircraft.disabled
            || aircraft.definition is not AircraftDefinition
            || !CommanderEnemyCommanderService.FillsAirRoleNow(hq, aircraft, role))
        {
            return false;
        }

        // The radar aeroplane is never taken for a fight, however hot the fight is: this is the
        // rule, not a preference (user decision 2026-09-14). The caller has already refused an AWACS
        // sortie as the TARGET, so any radar airframe reaching here is being asked to leave.
        if (IsAwacsAirframe(hq, aircraft))
        {
            return false;
        }

        if (IsUnderOtherService(state, aircraft))
        {
            return false; // Section 17
        }

        return MayRetask(
            state.AirRetaskedAt.TryGetValue(aircraft, out float at) ? Time.time - at : -1f,
            RetaskHoldSeconds);
    }

    /// <summary>The one door for "put this airframe on this sortie now": the form-up orbit while the
    /// package is still forming, the objective once it is not. The same choice
    /// <see cref="SyncAirTasks"/> makes, so the sync agrees with the move instead of undoing it.</summary>
    private static void TaskOntoSortie(
        FactionHQ hq, OperationsState state, CommanderAirSortie sortie, Aircraft aircraft, bool asCap)
    {
        bool holdAtFormUp = SortieHoldsAtFormUp(sortie);
        IssueAirTask(
            hq,
            state,
            aircraft,
            HoldingMode(sortie, asCap),
            holdAtFormUp ? sortie.FormUpPoint : sortie.Center,
            holdAtFormUp ? PackageArrivalMeters : (asCap ? CasSortieRadiusMeters : SortieRadius(sortie)));
        // The sortie's fallback posture goes with the task (design.md, air-fallback-posture_20260916
        // Section 4.3): a fighter moved onto a falling-back sortie falls back with it, and one moved
        // off it onto a sortie holding its ground stops.
        ApplyPosture(sortie, aircraft);
    }

    /// <summary>Why an airframe was sent home instead of put on the standing patrol, in the words a
    /// reader can act on: the log line has to say which rule fired, or a transport going home and a
    /// bomber going home look like the same event.</summary>
    private static string DescribePatrolRefusal(AircraftDefinition? definition, bool radarAirframe = false)
    {
        if (definition == null)
        {
            return "an airframe with no definition";
        }

        // Ahead of every other word, because a radar aeroplane refused the patrol is the rule
        // firing, not a rating: it flies radar watch and nothing else (user decision 2026-09-14).
        if (radarAirframe)
        {
            return "a radar airframe";
        }

        if (CommanderEnemyCommanderService.GetAirRole(definition) == CommanderEnemyCommanderService.AirRole.Transport)
        {
            return "a transport";
        }

        if (!CommanderAirCommandService.HasPlanePilot(definition))
        {
            return "an airframe the wing cannot task";
        }

        return CommanderAirCommandService.IsRotaryAirframe(definition)
            ? "a helicopter"
            : "a ground-attack airframe";
    }

    /// <summary>
    /// The operations side of the air-superiority refusal (user report, 2026-09-14: "seeing a lot
    /// of air superiority brawlers - SHOULDN'T BE, they're CAS aircraft"). The refusal itself is one
    /// rule in the commander service and every binding path reads it through the capability gate;
    /// what is checked HERE is the two things this file decides on its own — which mode a sortie's
    /// airframes carry while the package forms up, and that a ground-attack airframe is routed home
    /// by the idle sweep rather than onto the patrol.
    /// </summary>
    /// <summary>
    /// The anti-radiation weapon rule and the reconnaissance-round exclusion at their named
    /// boundaries (user report 2026-09-14, Departure 16), driven with synthetic designations and
    /// flags rather than the game's assets: an edit that lets a sensor round back onto a suppression
    /// hardpoint, or that starts refusing real missiles, fails here at plugin load rather than in a
    /// match.
    /// </summary>
    private static void CheckAntiRadiationWeapon(List<string> failures)
    {
        // The reconnaissance-round designation test, which the suppression, CAS and strike paths
        // all share (Departure 16, 2026-09-14). A name test, chosen after three data-keyed rules
        // in a row refused the real AGM-48 along with the sensor round; the constant's own summary
        // records why.
        Expect(
            failures,
            "the Eyeball is recognised as a reconnaissance round",
            CommanderAirCommandService.IsReconRoundIdentity("AGM-48 Eyeball Mk.II"),
            true);
        Expect(
            failures,
            "the designation is matched whatever the rest of the mount is called",
            CommanderAirCommandService.IsReconRoundIdentity("Eyeball"),
            true);
        Expect(
            failures,
            "the real AGM-48 is not a reconnaissance round",
            CommanderAirCommandService.IsReconRoundIdentity("AGM-48 x4"),
            false);
        Expect(
            failures,
            "the AGM-68 is not a reconnaissance round",
            CommanderAirCommandService.IsReconRoundIdentity("AGM-68 x2"),
            false);
        Expect(
            failures,
            "an unnamed store is not a reconnaissance round",
            CommanderAirCommandService.IsReconRoundIdentity(null),
            false);

        // A real anti-radiation missile: a missile, not nuclear, not a sensor round, with an ARM
        // seeker. No payload test — the catalog does not expose the AGM-48's payload either, so any
        // such test refuses real weapons along with the sensor round.
        Expect(
            failures,
            "an anti-radiation missile with a seeker is a suppression weapon",
            CommanderAirCommandService.IsAradCandidate(
                missile: true, nuclear: false, hasArmSeeker: true, reconRound: false),
            true);
        Expect(
            failures,
            "a reconnaissance round is never a suppression weapon",
            CommanderAirCommandService.IsAradCandidate(
                missile: true, nuclear: false, hasArmSeeker: true, reconRound: true),
            false);
        Expect(
            failures,
            "a store without an anti-radiation seeker (AGM-68, AGM-99, air-to-air) is never a suppression weapon",
            CommanderAirCommandService.IsAradCandidate(
                missile: true, nuclear: false, hasArmSeeker: false, reconRound: false),
            false);
        Expect(
            failures,
            "a bomb or gun is never a suppression weapon however it is rated",
            CommanderAirCommandService.IsAradCandidate(
                missile: false, nuclear: false, hasArmSeeker: true, reconRound: false),
            false);
        Expect(
            failures,
            "a nuclear store is never a suppression weapon",
            CommanderAirCommandService.IsAradCandidate(
                missile: true, nuclear: true, hasArmSeeker: true, reconRound: false),
            false);
    }

    private static void CheckAirSuperiorityRefusal(List<string> failures)
    {
        CommanderAirSortie objective = new() { Kind = CommanderSortieKind.Objective };
        CommanderAirSortie arad = new() { Kind = CommanderSortieKind.Arad };
        Expect(
            failures,
            "a CAS airframe holding at the form-up point keeps its CAS mode, never AIR SUPERIORITY",
            HoldingMode(objective, asCap: false).ToString(),
            CommanderAirCommandService.AirCommandMode.Cas.ToString());
        Expect(
            failures,
            "an ARAD airframe holding at the form-up point keeps its ARAD mode",
            HoldingMode(arad, asCap: false).ToString(),
            CommanderAirCommandService.AirCommandMode.Arad.ToString());
        Expect(
            failures,
            "the escort holds on AIR SUPERIORITY, which is its job",
            HoldingMode(objective, asCap: true).ToString(),
            CommanderAirCommandService.AirCommandMode.AirGuard.ToString());

        // The idle sweep's routing, on the two airframes the user named. The sweep asks exactly this
        // question: an airframe that may not fly air superiority is sent home, never posted to the
        // standing patrol.
        AircraftDefinition brawler = ScriptableObject.CreateInstance<AircraftDefinition>();
        brawler.jsonKey = "CAS1";
        brawler.roleIdentity.antiAir = 0.30f;
        brawler.roleIdentity.antiSurface = 0.80f;
        AircraftDefinition revoker = ScriptableObject.CreateInstance<AircraftDefinition>();
        revoker.jsonKey = "Fighter1";
        revoker.roleIdentity.antiAir = 1.00f;
        revoker.roleIdentity.antiSurface = 0.46f;
        Expect(
            failures,
            "an idle A-19 Brawler is sent home, never posted to the home CAP",
            CommanderEnemyCommanderService.MayFlyAirSuperiority(brawler),
            false);
        Expect(
            failures,
            "an idle FS-12 Revoker still takes the home CAP",
            CommanderEnemyCommanderService.MayFlyAirSuperiority(revoker),
            true);
        Expect(
            failures,
            "an owned A-19 Brawler is not a candidate for a sortie's CAP or escort slot",
            CommanderEnemyCommanderService.ForRole(brawler, CommanderEnemyCommanderService.AirRole.Fighter)
                == CommanderEnemyCommanderService.AirframeTier.Strike,
            true);

        // The transport the user watched get bound to a platoon's patrol (2026-09-14). Its ratings
        // put it in the strike tier by arithmetic, so only the structural exclusion keeps it off.
        AircraftDefinition ibis = ScriptableObject.CreateInstance<AircraftDefinition>();
        ibis.jsonKey = "UtilityHelo1";
        ibis.captureCapacity = 8;
        ibis.roleIdentity.antiAir = 0.27f;
        ibis.roleIdentity.antiSurface = 0.90f;
        Expect(
            failures,
            "a UH-90 Ibis is refused the home CAP, the claim's fallback and the idle sweep's posture",
            CommanderEnemyCommanderService.MayHoldPatrol(ibis),
            false);
        Expect(
            failures,
            "a UH-90 Ibis is refused a platoon's CAP sortie slot as well",
            CommanderEnemyCommanderService.MayFillRole(ibis, CommanderEnemyCommanderService.AirRole.Fighter),
            false);
        Expect(
            failures,
            "a UH-90 Ibis is refused a CAS slot too, so it is not bound to a sortie at all",
            CommanderEnemyCommanderService.MayFillRole(ibis, CommanderEnemyCommanderService.AirRole.Strike),
            false);
        Expect(
            failures,
            "the idle sweep says a transport went home BECAUSE it is a transport",
            DescribePatrolRefusal(ibis),
            "a transport");
        Expect(
            failures,
            "the idle sweep names a ground-attack airframe for what it is",
            DescribePatrolRefusal(brawler),
            "an airframe the wing cannot task");
        Expect(
            failures,
            "the idle sweep says a radar airframe went home BECAUSE it is a radar airframe",
            DescribePatrolRefusal(brawler, radarAirframe: true),
            "a radar airframe");
        Expect(
            failures,
            "a transport that happens to carry a radar pod is still named a radar airframe first",
            DescribePatrolRefusal(ibis, radarAirframe: true),
            "a radar airframe");
        Object.Destroy(ibis);
        Object.Destroy(brawler);
        Object.Destroy(revoker);
    }

    /// <summary>
    /// The Air Command mode an airframe on this sortie carries — including while the package is
    /// still forming up at its form-up point.
    /// <para>
    /// The escort's mode is AIR SUPERIORITY because that is its job. The CAS airframes keep their
    /// own CAS mode throughout, form-up hold included. They used to be put on AIR SUPERIORITY for
    /// the hold, purely because it was the mode that orbits a point without hunting ground targets —
    /// and the result was an A-19 Brawler sitting at a form-up point with <c>AIR SUPERIORITY</c>
    /// over it for the whole wait, on every package, which is what the user was looking at when they
    /// reported "seeing a lot of air superiority brawlers - SHOULDN'T BE, they're CAS aircraft"
    /// (2026-09-14). The position and the radius of the hold are unchanged; only the mode is, so the
    /// package geometry, the bounded wait and the go-in test all behave exactly as before.
    /// </para>
    /// Pure, for the self-check.
    /// </summary>
    internal static CommanderAirCommandService.AirCommandMode HoldingMode(CommanderAirSortie sortie, bool asCap)
    {
        return asCap ? CommanderAirCommandService.AirCommandMode.AirGuard : SortieCasMode(sortie);
    }
}
