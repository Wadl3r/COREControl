using System.Collections.Generic;
using NuclearOption.SavedMission;
using UnityEngine;

namespace GroundControlRts;

/// <summary>
/// The radar airframe flies radar watch and nothing else. Split out of <c>CommanderOperationsAir.cs</c> on 2026-09-15 purely for size (that file
/// had grown past six thousand lines); the code is moved, not rewritten, and the class doc on the
/// parent file still describes the whole. One partial class, several files.
/// </summary>
internal sealed partial class CommanderOperationsService
{
    // ---- The radar airframe flies radar watch and nothing else (user decision 2026-09-14) ----

    /// <summary>
    /// Whether this airframe is one of the commander's radar aeroplanes. The SAME capability test
    /// the AWACS sortie fills on and the one-per-commander count reads
    /// (<see cref="CountOwnedRadarAirframes"/>), so every path agrees about which airframe this is.
    /// </summary>
    private static bool IsAwacsAirframe(FactionHQ hq, Aircraft? aircraft)
    {
        return aircraft != null
            && !aircraft.disabled
            && aircraft.definition is AircraftDefinition definition
            && CommanderEnemyCommanderService.FillsAirRole(
                hq, definition, CommanderEnemyCommanderService.AirRole.Awacs);
    }

    /// <summary>Whether this sortie is the radar watch.</summary>
    private static bool IsAwacsSortie(CommanderAirSortie sortie)
    {
        return sortie.Kind == CommanderSortieKind.Awacs;
    }

    /// <summary>
    /// Whether an airframe may be put on a sortie at all — the one rule behind every reassignment
    /// path (user decision 2026-09-14: "AWACS aircraft should never be retasked from being AWACS").
    /// It binds BOTH ways: the radar aeroplane flies the radar watch and nothing else — not a
    /// contact retask, not an escort slot, not a package, not the home patrol — and nothing without
    /// the radar pod is ever put on the radar watch. The picture the whole wing fights on comes off
    /// one airframe, so lending it to a fight is lending away the reason the fight is winnable.
    /// Pure, for the self-check.
    /// </summary>
    internal static bool MayBindToSortie(bool radarAirframe, bool awacsSortie)
    {
        return radarAirframe == awacsSortie;
    }

    /// <summary>
    /// The bind gate itself, with the refusal said out loud. Every SELECTION below already declines
    /// to pick a mismatched airframe, so this is the backstop that catches a path nobody retrofitted
    /// — which is exactly the case a silent refusal would hide.
    /// </summary>
    private static bool MayBindAirframe(FactionHQ hq, CommanderAirSortie sortie, Aircraft aircraft)
    {
        bool radar = IsAwacsAirframe(hq, aircraft);
        if (MayBindToSortie(radar, IsAwacsSortie(sortie)))
        {
            return true;
        }

        CommanderAiLog.Note(
            hq,
            radar
                ? $"refuses to retask {CommanderGameAccess.GetUnitLabel(aircraft)}: AWACS stays on radar watch."
                : $"refuses to put {CommanderGameAccess.GetUnitLabel(aircraft)} on radar watch: it carries no radar.");
        return false;
    }

    private Aircraft? TakeUnboundOwned(FactionHQ hq, OperationsState state, CommanderEnemyCommanderService.AirRole role)
    {
        airStale.Clear();
        foreach (Aircraft owned in state.CommanderAirframes)
        {
            // An airframe flying home is still ours. The prune drops its task record when it turns
            // for home, so without this test it read as player-ordered and was disowned: the AWACS
            // on a fuel RTB then no longer counted, and a second one was bought (review H15).
            if (owned != null
                && !owned.disabled
                && CommanderAirCommandService.Instance?.TryGetMission(owned) != null
                && !state.AirIssued.ContainsKey(owned)
                && !IsReturning(owned))
            {
                airStale.Add(owned);
            }
        }

        for (int i = 0; i < airStale.Count; i++)
        {
            state.CommanderAirframes.Remove(airStale[i]);
            state.AirIssued.Remove(airStale[i]);
        }

        // The BEST-tier candidate, not the first one that fits (design.md,
        // airframe-selection_20260914 Section 3): an owned A-19 Brawler is not bound to a CAP sortie
        // while an owned FS-12 Revoker is sitting unbound. Ties inside a tier go to the airframe
        // encountered first, so a wing of identical hulls behaves exactly as it did before.
        Aircraft? best = null;
        CommanderEnemyCommanderService.AirframeTier bestTier = default;
        foreach (Aircraft aircraft in state.CommanderAirframes)
        {
            // Not one that is flying home: tasking it leaves it Returning, so it would fill a slot
            // for a review and suppress the buy for that slot while it lands.
            if (aircraft == null
                || aircraft.disabled
                || IsReturning(aircraft))
            {
                continue;
            }

            AircraftDefinition definition = (aircraft.definition as AircraftDefinition)!;
            if (!CommanderEnemyCommanderService.FillsAirRoleNow(hq, aircraft, role)
                // Both ways round (user decision 2026-09-14): a radar airframe is offered to the
                // radar watch and to nothing else, and the radar watch is offered nothing else.
                || !MayBindToSortie(
                    IsAwacsAirframe(hq, aircraft),
                    role == CommanderEnemyCommanderService.AirRole.Awacs)
                || IsBoundToAnySortie(state, aircraft)
                // Section 17: somebody else is already flying it.
                || IsUnderOtherService(state, aircraft)
                // Rung-1 fighters never leave the home CAP for a sortie, however unbound they are
                // (design.md, commander-priorities_20260914 Section 2).
                || state.HomeCapAirframes.Contains(aircraft))
            {
                continue;
            }

            CommanderEnemyCommanderService.AirframeTier tier =
                CommanderEnemyCommanderService.ForRole(definition, role);
            if (best == null || CommanderEnemyCommanderService.TierBeats(tier, bestTier, role))
            {
                best = aircraft;
                bestTier = tier;
            }
        }

        return best;
    }

    private static bool IsBoundToAnySortie(OperationsState state, Aircraft aircraft)
    {
        for (int i = 0; i < state.AirSorties.Count; i++)
        {
            CommanderAirSortie sortie = state.AirSorties[i];
            for (int c = 0; c < sortie.Caps.Count; c++)
            {
                if (ReferenceEquals(sortie.Caps[c], aircraft))
                {
                    return true;
                }
            }

            for (int c = 0; c < sortie.Cas.Count; c++)
            {
                if (ReferenceEquals(sortie.Cas[c], aircraft))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>Walk every bound airframe and make its live mission match what the sortie wants
    /// now: held over home while the CAP is missing, over the objective once it is not, and a
    /// rewritten area when the objective moved past the hysteresis. A mission that moved in a way
    /// this commander did not issue is the player's order — the airframe is let go.</summary>
    private void SyncAirTasks(FactionHQ hq, OperationsState state)
    {
        for (int s = 0; s < state.AirSorties.Count; s++)
        {
            CommanderAirSortie sortie = state.AirSorties[s];
            // The form-up orbit replaces the old hold over home territory (design SS3): a package
            // that is not yet complete waits 12 km out on the friendly side, not back over the
            // commander's own ground, so going in costs one short leg instead of the whole transit.
            bool holdAtFormUp = SortieHoldsAtFormUp(sortie);
            for (int i = sortie.Cas.Count - 1; i >= 0; i--)
            {
                SyncBoundAirframe(
                    hq, state, sortie, sortie.Cas[i],
                    HoldingMode(sortie, asCap: false),
                    holdAtFormUp ? sortie.FormUpPoint : sortie.Center,
                    holdAtFormUp ? PackageArrivalMeters : SortieRadius(sortie),
                    capJoin: !holdAtFormUp && sortie.CapsWanted > 0);
            }

            for (int i = 0; i < sortie.Caps.Count; i++)
            {
                SyncBoundAirframe(
                    hq, state, sortie, sortie.Caps[i],
                    CommanderAirCommandService.AirCommandMode.AirGuard,
                    holdAtFormUp ? sortie.FormUpPoint : sortie.Center,
                    holdAtFormUp ? PackageArrivalMeters : CasSortieRadiusMeters,
                    capJoin: false,
                    // The sortie's own station band (design.md, strike-packages_20260915 Section 4).
                    // A package still at its form-up orbit flies the band too: the point of the band
                    // is that consecutive patrols do not stack on one altitude, and a form-up orbit
                    // is exactly where two of them would meet.
                    targetAltitude: SortieCapAltitude(sortie));
            }
        }
    }

    /// <summary>The Air Command mode a sortie's own airframes fly over the objective: CAS for an
    /// objective, the anti-radiation mode for an ARAD sortie, and the standing patrol mode for the
    /// AWACS (design SS4: "flies AirGuard mode at the station").</summary>
    private static CommanderAirCommandService.AirCommandMode SortieCasMode(CommanderAirSortie sortie)
    {
        return sortie.Kind switch
        {
            CommanderSortieKind.Arad => CommanderAirCommandService.AirCommandMode.Arad,
            CommanderSortieKind.Awacs => CommanderAirCommandService.AirCommandMode.AirGuard,
            _ => CommanderAirCommandService.AirCommandMode.Cas,
        };
    }

    /// <summary>The mission-area radius a sortie's airframes are given: the sortie ring for
    /// everything that works a patch of ground, the wide orbit for the AWACS, which is there to
    /// look rather than to hold a point.</summary>
    private static float SortieRadius(CommanderAirSortie sortie)
    {
        // The AWACS reads the orbit its own station was granted, not the constant: the safety search
        // may have squeezed it to keep the whole orbit clear of the enemy (user report, 2026-09-14),
        // and the mission area written onto the airframe is what that squeeze has to reach.
        return sortie.Kind == CommanderSortieKind.Awacs ? sortie.OrbitRadiusMeters : CasSortieRadiusMeters;
    }

    private void SyncBoundAirframe(
        FactionHQ hq,
        OperationsState state,
        CommanderAirSortie sortie,
        Aircraft aircraft,
        CommanderAirCommandService.AirCommandMode desiredMode,
        GlobalPosition desiredCenter,
        float desiredRadius,
        bool capJoin,
        float targetAltitude = 0f)
    {
        if (aircraft == null || aircraft.disabled)
        {
            return; // the prune next review unbinds it
        }

        CommanderAirCommandService.AirMission? mission = CommanderAirCommandService.Instance?.TryGetMission(aircraft);
        if (mission == null)
        {
            return; // the tasking went away under it (an RTB/recovery path); the prune unbinds it
        }

        // The hands-off read: our snapshot matches the live mission, or the player moved it.
        if (!state.AirIssued.TryGetValue(aircraft, out IssuedAirTask issued)
            || mission.Mode != issued.Mode
            || CommanderGameAccess.HorizontalDistance(mission.AreaCenter.AsVector3(), issued.Center.AsVector3()) > CasRetargetMeters)
        {
            ReleaseToPlayer(hq, state, sortie, aircraft);
            return;
        }

        // The radar orbit is the one mission area whose RADIUS moves on its own: the safety search
        // squeezes it as the enemy closes on the station (user report, 2026-09-14), and a squeeze
        // that never reaches the aeroplane is not a squeeze. Every other kind flies a fixed radius,
        // so this reads false for them and nothing else changes. The same 3 km hysteresis, so an
        // orbit that breathes by a few hundred metres is left alone.
        bool orbitStale = sortie.Kind == CommanderSortieKind.Awacs
            && Mathf.Abs(mission.Radius - desiredRadius) > CasRetargetMeters;

        // The station band, the same way (design.md, strike-packages_20260915 Section 4): a patrol
        // whose sortie has been given a band must actually be sent to it, and one already flying its
        // band must not be re-tasked every review. Only the modes that HAVE a station height are
        // compared, so nothing else changes.
        bool altitudeStale = CommanderAirCommandService.SupportsTargetAltitude(desiredMode)
            && !Mathf.Approximately(mission.TargetAltitude, targetAltitude);

        if (mission.Mode == desiredMode
            && !orbitStale
            && !altitudeStale
            && CommanderGameAccess.HorizontalDistance(mission.AreaCenter.AsVector3(), desiredCenter.AsVector3()) <= CasRetargetMeters)
        {
            return;
        }

        bool joinsCap = capJoin && mission.Mode == CommanderAirCommandService.AirCommandMode.AirGuard;
        IssueAirTask(hq, state, aircraft, desiredMode, desiredCenter, desiredRadius, targetAltitude);
        if (joinsCap)
        {
            CommanderAiLog.Note(hq, $"{CommanderGameAccess.GetUnitLabel(aircraft)} joins its CAP over {sortie.Label}.");
        }
        else if (CommanderSettings.OperationsDebugLog && hq.faction != null)
        {
            CommanderPlugin.Log.LogInfo(
                $"Ops {CommanderPlayerCommanderService.CommanderLabel(hq)}: retasked {CommanderGameAccess.GetUnitLabel(aircraft)} onto {sortie.Label} (objective moved).");
        }
    }

    private static void ReleaseToPlayer(FactionHQ hq, OperationsState state, CommanderAirSortie sortie, Aircraft aircraft)
    {
        sortie.Cas.Remove(aircraft);
        sortie.Caps.Remove(aircraft);
        // The player's order owns the airframe now; a fallback stamp left on it would silently
        // override the player's route (design §4.6; review, 2026-09-16).
        ClearPosture(aircraft);

        state.CommanderAirframes.Remove(aircraft);
        state.AirIssued.Remove(aircraft);
        state.IdleSweepReported.Remove(aircraft);
        state.AirRetaskedAt.Remove(aircraft);
        // Not in a sortie's lists, but a rung-1 fighter on the home CAP is let go the same way: the
        // player's order owns it now, so it stops counting toward the CAP and stops being tasked.
        state.HomeCapAirframes.Remove(aircraft);
        state.HomeCapEnemyAirNear.Remove(aircraft);
        state.LentHomeCap.Remove(aircraft);
        CommanderAiLog.Note(hq, $"lets {CommanderGameAccess.GetUnitLabel(aircraft)} go: the player has it under orders.");
    }

    /// <summary>Returns whether the airframe was actually bound: the radar rule
    /// (<see cref="MayBindAirframe"/>) can refuse, and a caller in a fill loop must stop rather
    /// than offer the same airframe again.</summary>
    private bool BindCas(FactionHQ hq, OperationsState state, CommanderAirSortie sortie, Aircraft aircraft, Airbase? origin)
    {
        if (!MayBindAirframe(hq, sortie, aircraft))
        {
            return false;
        }

        sortie.Cas.Add(aircraft);
        // The launch lead time (Approval 1): how long this airframe needs to reach the objective
        // from the strip it left, for the tasking line.
        float transitSeconds = -1f;
        if (origin != null && origin.center != null)
        {
            transitSeconds = CasTransitSeconds(
                CommanderGameAccess.HorizontalDistance(origin.center.GlobalPosition().AsVector3(), sortie.Center.AsVector3()),
                IsRotaryAircraft(aircraft));
        }

        if (SortieHoldsAtFormUp(sortie))
        {
            IssueAirTask(
                hq, state, aircraft, HoldingMode(sortie, asCap: false),
                sortie.FormUpPoint, PackageArrivalMeters);
            CommanderAiLog.Note(
                hq,
                $"tasks {CommanderGameAccess.GetUnitLabel(aircraft)} for {sortie.Label}, holding at the form-up point "
                    + $"until the package is up ({DescribeSortieReason(sortie, transitSeconds)}).");
        }
        else
        {
            IssueAirTask(hq, state, aircraft, SortieCasMode(sortie), sortie.Center, SortieRadius(sortie));
            CommanderAiLog.Note(
                hq,
                $"tasks {CommanderGameAccess.GetUnitLabel(aircraft)} with {DescribeSortieTask(sortie)} {DescribeSortieStation(sortie)} "
                    + $"({DescribeSortieReason(sortie, transitSeconds)}).");
        }

        // A fighter bound to a falling-back sortie stages at its fallback point from the first
        // frame rather than flying to the objective for up to one watch tick (review, 2026-09-16).
        ApplyPosture(sortie, aircraft);
        return true;
    }

    /// <summary>What a tasking line calls the job: the sortie kind in plain words.</summary>
    private static string DescribeSortieTask(CommanderAirSortie sortie)
    {
        return sortie.Kind switch
        {
            CommanderSortieKind.Arad => "an anti-radiation strike",
            CommanderSortieKind.Awacs => "radar watch",
            CommanderSortieKind.Strike => "a strike",
            _ => "CAS",
        };
    }

    /// <summary>Where a tasking line says the job is. Every sortie works a patch of ground and reads
    /// "over &lt;objective&gt;"; the AWACS station is already a phrase about a distance from a named
    /// airbase ("15 km from Maris Airport toward the front"), so it is read straight out.</summary>
    private static string DescribeSortieStation(CommanderAirSortie sortie)
    {
        return sortie.Kind == CommanderSortieKind.Awacs
            ? sortie.Label
            : $"over {sortie.Label}";
    }

    /// <summary>The parenthesis on a CAS tasking line: why this sortie exists and, when the launch
    /// airbase is known, how long the airframe needs to get there. A pre-emptive sortie says so and
    /// gives the distance to the enemy that opened it, rounded to kilometres — the number a reader
    /// needs to see that the wing went up before the platoon was shot at.</summary>
    private static string DescribeSortieReason(CommanderAirSortie sortie, float transitSeconds)
    {
        string reason = sortie.Kind switch
        {
            CommanderSortieKind.Awacs => "one radar airframe per commander",
            CommanderSortieKind.Arad => $"{sortie.LastObserved} air-defence vehicles clustered",
            CommanderSortieKind.Strike =>
                $"deliberate strike, {sortie.DefendersAtOrder} defenders, {sortie.LastHostileAir} hostile air",
            _ => sortie.Preemptive
                ? $"pre-emptive, enemy {sortie.EnemyDistanceMeters / 1000f:0.#} km"
                : $"{sortie.LastObserved} observed",
        };
        return transitSeconds > 0f ? $"{reason}; on station in ~{transitSeconds:0} s" : reason;
    }

    /// <summary>Bind a fighter onto the sortie's CAP. The first one is the escort — the airframe
    /// whose binding releases the held CAS to the objective (Approval 1, kept under CAP-first);
    /// later ones are wingmen. Returns whether the airframe was actually bound: an escort slot is
    /// one of the places the radar aeroplane used to be reachable from, and
    /// <see cref="MayBindAirframe"/> refuses it.</summary>
    private static bool BindCap(FactionHQ hq, OperationsState state, CommanderAirSortie sortie, Aircraft aircraft)
    {
        if (!MayBindAirframe(hq, sortie, aircraft))
        {
            return false;
        }

        bool isEscort = sortie.Caps.Count == 0 && sortie.Cas.Count > 0;
        sortie.Caps.Add(aircraft);
        bool holdAtFormUp = SortieHoldsAtFormUp(sortie);
        IssueAirTask(
            hq, state, aircraft, CommanderAirCommandService.AirCommandMode.AirGuard,
            holdAtFormUp ? sortie.FormUpPoint : sortie.Center,
            holdAtFormUp ? PackageArrivalMeters : CasSortieRadiusMeters,
            SortieCapAltitude(sortie));
        if (isEscort)
        {
            CommanderAiLog.Note(
                hq,
                sortie.Preemptive
                    ? $"tasks {CommanderGameAccess.GetUnitLabel(aircraft)} as the escort over {sortie.Label} (pre-emptive)."
                    : $"tasks {CommanderGameAccess.GetUnitLabel(aircraft)} as the escort over {sortie.Label}.");
        }
        else
        {
            CommanderAiLog.Note(
                hq,
                $"tasks {CommanderGameAccess.GetUnitLabel(aircraft)} on CAP over {sortie.Label} "
                    + $"(wing {sortie.Caps.Count}/{sortie.CapsWanted}; {sortie.LastHostileAir} hostile air tracked).");
        }

        // Same as BindCas: the posture is stamped after the task, never left to the next tick.
        ApplyPosture(sortie, aircraft);
        return true;
    }

    /// <summary>The one tasking door (Reuse rule 4): every task this commander issues an airframe —
    /// bind, retarget, hold, release, posture — goes through here so the hands-off snapshot is
    /// always written with it. Returns whether the task took (a rotary airframe cannot take one).</summary>
    /// <param name="targetAltitude">The station band a CAP is flown at, in metres above ground
    /// (design.md, strike-packages_20260915 Section 4); 0 everywhere else, and ignored by every mode
    /// that has no station height.</param>
    private static bool IssueAirTask(
        FactionHQ hq,
        OperationsState state,
        Aircraft aircraft,
        CommanderAirCommandService.AirCommandMode mode,
        GlobalPosition center,
        float radius,
        float targetAltitude = 0f)
    {
        if (CommanderAirCommandService.Instance?.TryTaskAiAircraft(
                aircraft, mode, center, radius, retaskExisting: true, targetAltitude) == true)
        {
            state.AirIssued[aircraft] = new IssuedAirTask(mode, center);
            return true;
        }

        return false;
    }

    /// <summary>The release door: the standing home CAP box, retasked over whatever of ours the
    /// airframe is currently flying. Refuses an airframe whose mission this commander did not
    /// issue — that one is the player's (decision 5). Returns whether the task took.</summary>
    internal static bool IssueHomeCapTask(FactionHQ hq, Aircraft aircraft)
    {
        if (Instance == null || !Instance.states.TryGetValue(hq, out OperationsState state))
        {
            return false;
        }

        if (CommanderAirCommandService.Instance?.TryGetMission(aircraft) != null
            && !state.AirIssued.ContainsKey(aircraft))
        {
            return false;
        }

        // The radar aeroplane is never released onto the standing patrol (user decision
        // 2026-09-14). Both patrol doors refuse it, so a path that dissolves its sortie leaves it
        // owned and idle for the next radar watch instead of handing it an air-superiority orbit.
        if (IsAwacsAirframe(hq, aircraft))
        {
            return false;
        }

        IssueAirTask(
            hq, state, aircraft, CommanderAirCommandService.AirCommandMode.AirGuard,
            HomeCAPCentre(hq), CommanderEnemyCommanderService.HomeGuardRadiusMeters,
            HomeCapAltitude(state, aircraft));
        return true;
    }

    /// <summary>The residual posture door: the standing home CAP onto an owned airframe that
    /// carries no mission at all. A sortie-bound one, a released one already holding the box and a
    /// player-ordered one are all somebody else's business — the posture never retasks.</summary>
    internal static bool IssuePostureTask(FactionHQ hq, Aircraft aircraft)
    {
        if (Instance == null
            || !Instance.states.TryGetValue(hq, out OperationsState state)
            || CommanderAirCommandService.Instance?.IsOnAnyMission(aircraft) == true
            // The other patrol door, refusing the radar aeroplane for the same reason
            // (see IssueHomeCapTask).
            || IsAwacsAirframe(hq, aircraft))
        {
            return false;
        }

        IssueAirTask(
            hq, state, aircraft, CommanderAirCommandService.AirCommandMode.AirGuard,
            HomeCAPCentre(hq), CommanderEnemyCommanderService.HomeGuardRadiusMeters,
            HomeCapAltitude(state, aircraft));
        return true;
    }

    /// <summary>The standing CAP centre: the HQ's territory centre — the same "home ground"
    /// aggregate the reserve ring and the old wing's home guard used (Reuse rule 4, one
    /// definition).</summary>
    private static GlobalPosition HomeCAPCentre(FactionHQ hq)
    {
        return CommanderCaptureService.GetTerritoryCenter(hq);
    }

    private static bool IsRotaryAircraft(Aircraft aircraft)
    {
        return aircraft.pilots != null
            && aircraft.pilots.Length > 0
            && aircraft.pilots[0] != null
            && CommanderAirCommandService.IsRotaryPilot(aircraft.pilots[0]);
    }

    /// <summary>Prune the air book: owned airframes that went away stop being ours, and a bound
    /// airframe that died over the objective stamps the sortie's cooldown — while one that turned
    /// for home (the existing out-of-ammo rule) only frees its slot: a rearm cycle is not a loss.</summary>
    private void PruneAirBook(FactionHQ hq, OperationsState state)
    {
        airStale.Clear();
        foreach (Aircraft owned in state.CommanderAirframes)
        {
            if (owned == null || owned.disabled)
            {
                airStale.Add(owned!);
            }
        }

        for (int i = 0; i < airStale.Count; i++)
        {
            state.CommanderAirframes.Remove(airStale[i]);
            state.AirIssued.Remove(airStale[i]);
            state.IdleSweepReported.Remove(airStale[i]);
            state.AirRetaskedAt.Remove(airStale[i]);
            // The patrol's station band goes with the aeroplane, or the table would hold every
            // fighter the match ever launched (the EverAirborne pruning's own reason).
            state.HomeCapBand.Remove(airStale[i]);
        }

        // The cannon rule's standing sweep (user, 2026-09-14). The loadout rule at the launch is
        // what normally keeps the gun off; this catches the cases a launch-time pass cannot see —
        // an airframe whose weapon stations were still being built when it spawned, one the game
        // rearmed at a strip, one adopted after a hot reload. It reports only when it actually
        // takes rounds off, so a quiet log means the launch rule is doing its job.
        foreach (Aircraft owned in state.CommanderAirframes)
        {
            int rounds = CommanderAirCommandService.StripCannonAmmo(owned);
            if (rounds > 0)
            {
                CommanderAiLog.Note(
                    hq,
                    $"emptied the internal cannon of {CommanderGameAccess.GetUnitLabel(owned)} "
                        + $"({rounds} rounds): AI airframes do not make gun runs.");
            }
        }

        for (int s = 0; s < state.AirSorties.Count; s++)
        {
            CommanderAirSortie sortie = state.AirSorties[s];
            PruneSortieAirframes(hq, state, sortie, sortie.Cas);
            PruneSortieAirframes(hq, state, sortie, sortie.Caps);
        }
    }

    private void PruneSortieAirframes(FactionHQ hq, OperationsState state, CommanderAirSortie sortie, List<Aircraft> bound)
    {
        airStale.Clear();
        for (int i = 0; i < bound.Count; i++)
        {
            Aircraft aircraft = bound[i];
            if (aircraft == null || aircraft.disabled)
            {
                // Dead while bound and not marked for home below = lost over the objective (the
                // returning case is unbound here before it can disable, so what reaches here
                // disabled without ever Returning died in the fight or on the way in).
                StampAirLoss(hq, sortie);
                airStale.Add(aircraft!);
            }
            else if (IsReturning(aircraft))
            {
                if (CommanderSettings.OperationsDebugLog && hq.faction != null)
                {
                    CommanderPlugin.Log.LogInfo(
                        $"Ops {CommanderPlayerCommanderService.CommanderLabel(hq)}: {CommanderGameAccess.GetUnitLabel(aircraft)} goes home to rearm; its slot over {sortie.Label} reopens.");
                }

                airStale.Add(aircraft);
            }
        }

        for (int i = 0; i < airStale.Count; i++)
        {
            bound.Remove(airStale[i]);
            state.AirIssued.Remove(airStale[i]);
        }
    }

    private static bool IsReturning(Aircraft aircraft)
    {
        CommanderAirCommandService.AirMission? mission = CommanderAirCommandService.Instance?.TryGetMission(aircraft);
        return mission != null && mission.Returning;
    }

    private static void StampAirLoss(FactionHQ hq, CommanderAirSortie sortie)
    {
        bool awacs = sortie.Kind == CommanderSortieKind.Awacs;
        int airDefence = CountObservedAirDefence(hq, sortie.Center);
        float minutes = SortieLossCooldownMinutes(
            awacs, airDefence, CommanderSettings.CasLossCooldownMinutes, AwacsLossCooldownMinutes);
        sortie.CooldownUntil = Time.time + minutes * 60f;
        CommanderAiLog.Note(
            hq,
            awacs
                ? $"loses the radar airframe: no replacement is bought for {minutes:0} min."
                : $"loses an airframe over {sortie.Label}: CAS stands down for {minutes:0} min ({airDefence} hostile air-defence observed).");
    }

    /// <summary>Tracked hostile air-defence ground vehicles within the sizing ring of
    /// <paramref name="center"/> — the <c>CountObserved</c> walk with the air-defence role test the
    /// buyer already uses, retargeted at an objective. A raw count inside one ring, which is what the
    /// sizing, the loss cooldown and the review line want; the belt hold needs CLUSTERED launchers
    /// instead and reads <c>LargestBeltNear</c> (<c>CommanderOperationsAirPosture.cs</c>).</summary>
    private static int CountObservedAirDefence(FactionHQ hq, GlobalPosition center)
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
                || ReferenceEquals(unit.NetworkHQ, hq)
                || unit.definition is not VehicleDefinition definition
                || !CommanderEnemyCommanderService.IsAirDefence(definition))
            {
                continue;
            }

            if (CommanderGameAccess.HorizontalDistance(center.AsVector3(), info.lastKnownPosition.AsVector3()) <= ObservedRadiusMeters)
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>Tracked hostile aircraft inside a ring of the caller's choosing — the
    /// <c>CountObserved</c> walk with the aircraft type test and its freshness rule (design SS1; one
    /// CAP fighter per tracked hostile, on top of the baseline). Internal (one-word widening, Reuse
    /// rule 4): the sortie sizing reads it at <see cref="ObservedRadiusMeters"/> over an objective,
    /// and the home-CAP loss test reads the same walk at <see cref="CapLossRadiusMeters"/> around a
    /// fighter's own position — one definition, two callers. With
    /// <paramref name="fixedWingCombatOnly"/> the air fallback posture reads it a third time,
    /// counting only aircraft that fly like aeroplanes and carry a weapon
    /// (<see cref="IsFixedWingCombatAircraft"/>): a helicopter or a transport is not what outnumbers
    /// a fighter (design.md, air-fallback-posture_20260916 Section 4.1).</summary>
    internal static int CountHostileAirInRing(
        FactionHQ hq, GlobalPosition center, float radiusMeters, bool fixedWingCombatOnly = false)
    {
        int count = 0;
        float now = Time.timeSinceLevelLoad;
        foreach (KeyValuePair<PersistentID, TrackingInfo> entry in hq.trackingDatabase)
        {
            TrackingInfo info = entry.Value;
            if (!IsTrackedHostileAircraft(hq, info, now, fixedWingCombatOnly))
            {
                continue;
            }

            if (CommanderGameAccess.HorizontalDistance(center.AsVector3(), info.lastKnownPosition.AsVector3()) <= radiusMeters)
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>Whether one tracking record is a live hostile aircraft seen inside the threat memory —
    /// the one filter behind every hostile-air count (Reuse rule 4; the posture's sortie count
    /// shares it with the ring count).</summary>
    internal static bool IsTrackedHostileAircraft(FactionHQ hq, TrackingInfo info, float now, bool fixedWingCombatOnly)
    {
        return now - info.lastSpottedTime <= CommanderEnemyCommanderService.ThreatMemorySeconds
            && info.TryGetUnit(out Unit unit)
            && unit != null
            && !unit.disabled
            && unit is Aircraft aircraft
            && unit.NetworkHQ != null
            && !ReferenceEquals(unit.NetworkHQ, hq)
            && (!fixedWingCombatOnly || IsFixedWingCombatAircraft(aircraft));
    }

    /// <summary>What the buyer should put in the air next for the sorties: the wing's whole CAP
    /// shortfall first (user decision 2026-09-13: CAP first — when the fund covers one airframe
    /// and both are short, it buys the fighter), then the CAS shortfall, each in sortie priority
    /// order. A sortie inside its loss cooldown asks for nothing — that is the cooldown's whole
    /// effect.</summary>
    /// <summary>Whether the wing is short of anything at all — the ladder's rung-2 demand test,
    /// which needs the answer and none of the detail.</summary>
    internal static bool HasAirDemand(FactionHQ hq)
    {
        return TryGetAirDemand(hq, out _, out _, out _, out _, out _, out _, out _, out _) != CommanderAirDemandKind.None;
    }

    /// <summary>
    /// The home CAP the formula wants right now — the one read of
    /// <see cref="CommanderEnemyCommanderService.WantedHomeCap"/> with this commander's live inputs
    /// (Reuse rule 4: the ladder's rung 1, the rung-2 demand walk and the airframe markers all
    /// asked the same five-argument question in their own words).
    /// </summary>
    internal static int WantedHomeCapNow(FactionHQ hq)
    {
        return CommanderEnemyCommanderService.WantedHomeCap(
            CommanderSettings.HomeCapBaseline,
            CommanderSettings.HomeCapPerEnemyAircraft,
            CommanderEnemyCommanderService.CountTrackedEnemyAircraftNearBases(hq),
            HomeCapLosses(hq),
            CommanderSettings.HomeCapMax);
    }

    /// <summary>
    /// How many home-CAP fighters rung 2 may buy this review (user decision 2026-09-14): the part of
    /// the formula above the strict baseline, which rung 1 does not touch. Nought when the patrol is
    /// already at or above what the formula wants. The live wrapper around the ladder's pure
    /// <see cref="CommanderEnemyCommanderService.HomeCapExtraWanted"/>.
    /// </summary>
    internal static int HomeCapAirDemand(FactionHQ hq)
    {
        return CommanderEnemyCommanderService.HomeCapExtraWanted(
            WantedHomeCapNow(hq), CountHomeCapFighters(hq), CommanderSettings.HomeCapBaseline);
    }

    /// <param name="observedHostiles">Hostile ground units the chosen sortie observed over its
    /// objective this review, and <paramref name="trackedAircraft"/> the hostile aircraft it tracked
    /// in the ring. Both are the numbers the sortie was already SIZED from
    /// (<see cref="CommanderAirSortie.LastObserved"/> / <see cref="CommanderAirSortie.LastHostileAir"/>),
    /// handed on rather than re-counted, so the airframe tier rule (design.md,
    /// airframe-selection_20260914 Section 2) judges the buy against exactly the threat that asked
    /// for it — one definition, two callers.</param>
    /// <param name="advanceTurn">Set only by the buy that is about to act on the answer: it moves
    /// the CAP/CAS alternation on one place (fix, 2026-09-14, <see cref="PrefersCasThisBuy"/>).
    /// The "is anything open" and "what is the fund saving for" reads leave it alone, so a query
    /// can never cost a side its turn. A buy that then fails to find an affordable airframe still
    /// spends its turn, deliberately: a demand the roster cannot fill is exactly the one that must
    /// not hold the other side up for the rest of the match.</param>
    /// <param name="awacsAffordable">Whether this review's air fund can reach the cheapest
    /// radar-carrying airframe the commander's strips accept. Only the buy passes it; the "is
    /// anything open" and "what is the fund saving for" reads leave it true, because they are asking
    /// what the wing WANTS, not what it can pay for this instant. False makes the radar airframe
    /// stand aside for a fighter demand it can actually fill — see <see cref="NextAirDemand"/>.</param>
    /// <param name="capAffordable">Whether this review's air fund can reach the cheapest
    /// air-to-air-capable airframe the commander's strips accept. Read only to decide whether
    /// standing the radar airframe aside would actually buy anything: a wing that can afford
    /// neither keeps saving for the dearer one rather than spending its turn on a refusal.</param>
    /// <param name="homeCap">True when the CAP side's answer is the standing home patrol rather than
    /// a sortie's escort — the half of the home CAP above the strict baseline, which rung 1 leaves
    /// to this alternation (user decision 2026-09-14). The buy routes it to the home-CAP buyer, which
    /// picks the fighter and parks it on the base's own patrol rather than over an objective.</param>
    internal static CommanderAirDemandKind TryGetAirDemand(
        FactionHQ hq,
        out GlobalPosition objective,
        out bool wantsRotary,
        out string label,
        out int trackedAircraft,
        out int observedHostiles,
        out bool homeCap,
        out CommanderAirSortie? demandSortie,
        out CommanderEnemyCommanderService.ElementKind elementKind,
        bool advanceTurn = false,
        bool awacsAffordable = true,
        bool capAffordable = true,
        bool awacsLaunchable = true)
    {
        objective = default;
        wantsRotary = false;
        label = string.Empty;
        trackedAircraft = 0;
        observedHostiles = 0;
        homeCap = false;
        demandSortie = null;
        elementKind = CommanderEnemyCommanderService.ElementKind.Other;
        if (Instance == null || !Instance.states.TryGetValue(hq, out OperationsState state))
        {
            return CommanderAirDemandKind.None;
        }

        // Each sortie asks for ONE thing — whatever its own escort → CAS → wingmen order wants next
        // (see NextSortieSlot) — and the highest-priority sortie's answer wins its kind's slot. The
        // walk this replaced recorded a CAP shortfall from ANY sortie and a CAS shortfall from any
        // other, so with tens of objectives open a CAP shortfall always existed and the buyer bought
        // nothing but fighters: 37 of them against 7 strike airframes in the 2026-09-14 match.
        CommanderAirSortie? awacsSortie = null;
        CommanderAirSortie? capSortie = null;
        CommanderAirSortie? aradSortie = null;
        CommanderAirSortie? casSortie = null;
        // A standing patrol the reserved line has closed is PASSED OVER here rather than allowed to
        // win the CAP turn and be refused at the buy (fix, 2026-09-18, found in the track's own
        // review). The demand list is in posting order and the standing patrols are posted first —
        // AddPlatoonCapDemand calls AddTransportEscortDemand last — so one closed patrol at the head
        // of the list took the whole CAP turn with it every review and the picket insertion escort
        // behind it was never bought at all: the exact outcome the reserved block exists to prevent.
        // The same skip is what lets the home patrol's growth above the baseline reach the buy,
        // since that is only offered when no CAP sortie is open.
        // Read ONCE, not per sortie: the answer walks the faction's live units.
        bool standingPatrolsMayBuy = AirBuyAllowed(hq, standingPatrol: true);
        for (int i = 0; i < state.AirSorties.Count; i++)
        {
            CommanderAirSortie sortie = state.AirSorties[i];
            if (Time.time < sortie.CooldownUntil)
            {
                continue;
            }

            // Safe to skip the sortie WHOLE rather than only its CAP slot: AddCapDemand posts
            // Wanted = 0, so a standing patrol has no ground-attack slot to offer the CAS side.
            if (sortie.IsStandingPatrol && !standingPatrolsMayBuy)
            {
                continue;
            }

            CommanderAirSlot slot = NextSortieSlot(
                sortie.Caps.Count, sortie.CapsWanted, sortie.Cas.Count, sortie.Wanted);
            if (slot == CommanderAirSlot.None)
            {
                continue;
            }

            if (slot != CommanderAirSlot.Cas)
            {
                capSortie ??= sortie;
            }

            // A sortie waiting on its escort STILL has an unfilled CAS slot, and on the CAS side's
            // turn that slot is what the buy serves (fix, 2026-09-14). Reading only the sortie's
            // single next slot made a wing of unescorted sorties look like pure CAP demand: every
            // sortie that wants CAS wants its escort first, so with six platoons calling for
            // pre-emptive air the commander bought six fighters before it bought one strike
            // airframe, which is exactly the "it's all CAP" the 2026-09-14 match shows.
            if (sortie.Cas.Count >= sortie.Wanted)
            {
                continue;
            }

            switch (sortie.Kind)
            {
                case CommanderSortieKind.Awacs:
                    awacsSortie ??= sortie;
                    break;
                case CommanderSortieKind.Arad:
                    aradSortie ??= sortie;
                    break;
                default:
                    casSortie ??= sortie;
                    break;
            }
        }

        // The home patrol's growth above the strict baseline is CAP demand like any other (user
        // decision 2026-09-14): it takes its turn in the alternation instead of holding the whole
        // ladder, which is what the baseline-only strict rung freed it to do.
        bool homeCapOpen = HomeCapAirDemand(hq) > 0;
        bool capOpen = capSortie != null || homeCapOpen;

        // The AWACS counts on the CAP side of the turn, both when the turn is decided and when it
        // is advanced (fix, 2026-09-14): a radar airframe the commander is saving for must not be
        // able to hold the ground-attack side up review after review.
        // A radar airframe no held strip can launch is not a demand this review (user 2026-09-14:
        // "if that airbase can't spawn an AWACS because too small... then we shouldn't hold the
        // funds"): the savings target is already zero in that case, and here the turn is not handed
        // to a buy that can only fail — the fighters and the strike side get it instead.
        CommanderAirDemandKind kind = NextAirDemand(
            awacsSortie != null && awacsLaunchable,
            capOpen,
            aradSortie != null,
            casSortie != null,
            PrefersCasThisBuy(
                capOpen || (awacsSortie != null && awacsLaunchable),
                aradSortie != null || casSortie != null,
                state.LastAirBuyWasCas),
            awacsAffordable,
            capAffordable);
        if (advanceTurn && kind != CommanderAirDemandKind.None)
        {
            state.LastAirBuyWasCas =
                kind == CommanderAirDemandKind.Cas || kind == CommanderAirDemandKind.Arad;
        }

        CommanderAirSortie? chosen = kind switch
        {
            CommanderAirDemandKind.Awacs => awacsSortie,
            CommanderAirDemandKind.Cap => capSortie,
            CommanderAirDemandKind.Arad => aradSortie,
            CommanderAirDemandKind.Cas => casSortie,
            _ => null,
        };

        if (chosen != null)
        {
            objective = chosen.Center;
            // ARAD first (user decision 2026-09-14): a sortie over a live belt is flown by jets
            // until a suppression sortie has been over it, so the buy never shops for a helicopter.
            wantsRotary = kind == CommanderAirDemandKind.Cas
                && chosen.WantsRotary
                && RotaryCasAllowed(chosen.LastAirDefence, chosen.AradFlown);
            label = chosen.Label;
            trackedAircraft = chosen.LastHostileAir;
            observedHostiles = chosen.LastObserved;
            // The sortie itself goes back to the buyer (user decision 2026-09-14): the element it is
            // about to order has to be one type from one base, and the sortie is where that choice
            // is remembered between reviews.
            demandSortie = chosen;
            // Which element of the package this buy is filling (design.md,
            // strike-packages_20260915 Section 3). Every escort slot on any sortie is an escort; the
            // strike package's own slots are the strike and bomber elements; everything else keeps
            // today's rule by naming no element at all.
            elementKind = kind == CommanderAirDemandKind.Cap
                ? CommanderEnemyCommanderService.ElementKind.Escort
                : chosen.Kind == CommanderSortieKind.Strike
                    ? StrikeElementFor(chosen)
                    : CommanderEnemyCommanderService.ElementKind.Other;
        }
        else if (kind == CommanderAirDemandKind.Cap)
        {
            // The CAP side won its turn with no sortie asking, so the only thing that opened it is
            // the home patrol's growth. The buy reads this rather than a null objective.
            homeCap = true;
            elementKind = CommanderEnemyCommanderService.ElementKind.HighCap;
        }

        return kind;
    }

    /// <summary>
    /// Whether any sortie over something actually FIGHTING is still short of an airframe — a
    /// platoon or point in contact, or an attack that has gone in, with an unfilled strike or
    /// escort slot. The trigger for rung 2's air reserve (user decision 2026-09-14, "existing
    /// forces first"): while the men already committed are asking for air and not getting it, a
    /// third of the rung is set aside for the wing before any ground vehicle is bought.
    /// <para>Pre-emptive cover does NOT count. A march that has not met anything yet is tier 3 of
    /// the spending order, below the replacements — reserving against it would hand the wing a
    /// third of every review for ever, which is the opposite of "existing forces first".</para>
    /// </summary>
    /// <summary>
    /// Whether the commander should be putting money aside for a radar airframe right now: its
    /// AWACS sortie exists, is off its loss cooldown, and still has an unfilled slot. All three
    /// conditions live on the sortie already — <c>Wanted</c> is <see cref="AwacsWanted"/> of the
    /// radar airframes it owns, so "owns one" closes the slot, and <c>CooldownUntil</c> is the loss
    /// cooldown every sortie carries — so this is one read of the existing state rather than a
    /// second opinion about any of them (Reuse rule 4).
    /// </summary>
    internal static bool WantsAwacsPurchase(FactionHQ hq)
    {
        if (Instance == null || !Instance.states.TryGetValue(hq, out OperationsState state))
        {
            return false;
        }

        for (int i = 0; i < state.AirSorties.Count; i++)
        {
            CommanderAirSortie sortie = state.AirSorties[i];
            if (sortie.Kind == CommanderSortieKind.Awacs
                && Time.time >= sortie.CooldownUntil
                && sortie.Cas.Count < sortie.Wanted)
            {
                return true;
            }
        }

        return false;
    }

    internal static bool HasInContactAirShortfall(FactionHQ hq)
    {
        if (Instance == null || !Instance.states.TryGetValue(hq, out OperationsState state))
        {
            return false;
        }

        for (int i = 0; i < state.AirSorties.Count; i++)
        {
            CommanderAirSortie sortie = state.AirSorties[i];
            if (sortie.InContact
                && (sortie.Cas.Count < sortie.Wanted || sortie.Caps.Count < sortie.CapsWanted))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Which air demands are open right now — ALL of them, rather than the single highest-priority
    /// one <see cref="TryGetAirDemand"/> answers with. The air fund's ceiling reads this: the wing
    /// may bank up to the price of the dearest airframe an open demand actually wants, and not a
    /// penny more, so a quiet sky cannot sit on a fund the ground is asking for. The walk is
    /// <see cref="TryGetAirDemand"/>'s own, with every kind recorded instead of the first of each.
    /// </summary>
    internal static void ReadOpenAirDemandRoles(
        FactionHQ hq, out bool cap, out bool cas, out bool rotaryCas, out bool awacs, out bool arad)
    {
        cap = false;
        cas = false;
        rotaryCas = false;
        awacs = false;
        arad = false;
        if (Instance == null || !Instance.states.TryGetValue(hq, out OperationsState state))
        {
            return;
        }

        // The home patrol's growth above the baseline is a fighter the wing is allowed to save for
        // (user decision 2026-09-14), exactly like a sortie's escort.
        if (HomeCapAirDemand(hq) > 0)
        {
            cap = true;
        }

        for (int i = 0; i < state.AirSorties.Count; i++)
        {
            CommanderAirSortie sortie = state.AirSorties[i];
            if (Time.time < sortie.CooldownUntil)
            {
                continue;
            }

            CommanderAirSlot slot = NextSortieSlot(
                sortie.Caps.Count, sortie.CapsWanted, sortie.Cas.Count, sortie.Wanted);
            if (slot == CommanderAirSlot.None)
            {
                continue;
            }

            // Escorts and wingmen are both CAP fighters, which is why every non-CAS slot lands here.
            if (slot != CommanderAirSlot.Cas)
            {
                cap = true;
            }

            // An unfilled CAS slot is open demand even while the sortie waits on its escort (fix,
            // 2026-09-14), for the same reason the buy now reads it that way: the fund must be
            // allowed to save for the strike airframe the next CAS turn is going to ask for.
            if (sortie.Cas.Count >= sortie.Wanted)
            {
                continue;
            }

            switch (sortie.Kind)
            {
                case CommanderSortieKind.Awacs:
                    awacs = true;
                    break;
                case CommanderSortieKind.Arad:
                    arad = true;
                    break;
                default:
                    cas = true;
                    // The fund saves toward a helicopter only while one may actually be bought
                    // (user decision 2026-09-14) — otherwise a held objective would have the wing
                    // banking for an airframe the ARAD-first rule refuses to buy.
                    if (sortie.WantsRotary && RotaryCasAllowed(sortie.LastAirDefence, sortie.AradFlown))
                    {
                        rotaryCas = true;
                    }

                    break;
            }
        }
    }

    /// <summary>The wing's live demand counts for the once-per-review diagnostics line: bound and
    /// wanted CAP fighters and CAS airframes across every sortie that is not standing down after a
    /// loss — the same shortfalls <see cref="TryGetAirDemand"/> reads.</summary>
    /// <summary>
    /// How many aircraft a commander may keep in the world this review, pure (user decision
    /// 2026-09-13, "should be economy limited not hard cap"; applied 2026-09-14 when both sides sat
    /// at the flat ceiling with thousands banked and thirty air requests open): the configured
    /// ceiling is the floor, one more per <paramref name="incomePerAirframe"/> of income a minute,
    /// never above <paramref name="max"/>. A non-positive rate or max disables the scaling and
    /// returns the floor, so a config retune cannot divide by zero or ground the wing.
    /// </summary>
    internal static int AirborneCeilingFor(int configured, float incomePerMinute, float incomePerAirframe, int max)
    {
        int floor = Mathf.Max(1, configured);
        if (incomePerAirframe <= 0f || max <= 0)
        {
            return floor;
        }

        int byIncome = Mathf.FloorToInt(Mathf.Max(0f, incomePerMinute) / incomePerAirframe);
        return Mathf.Clamp(Mathf.Max(floor, byIncome), floor, Mathf.Max(floor, max));
    }

    /// <summary>The live ceiling for <paramref name="hq"/>: the pure rule over the same income read
    /// the <c>income N/min</c> diagnostics line makes (points plus mines).
    /// <para>
    /// The attrition brake used to halve this while a side of the wing was held (<c>AttritionCeiling</c>,
    /// user decision 2026-09-15). Removed 2026-09-16 with the hold itself: the air budget is sized as
    /// the room left under this ceiling times the cheapest wanted airframe, so halving it left no
    /// room, the budget collapsed to one airframe, the best affordable pick became the cheapest one,
    /// and the brake re-applied itself every review. See the header of
    /// <c>Ai/CommanderEnemyCommanderAttrition.cs</c>.
    /// </para></summary>
    internal static int EffectiveAirborneCeiling(FactionHQ hq)
    {
        float income = CommanderStrategicPointService.Instance?.GetPointIncomePerMinute(hq, out _) ?? 0f;
        income += CommanderEconomyService.Instance?.GetMineIncomePerMinute(hq) ?? 0f;
        return AirborneCeilingFor(
            CommanderSettings.AirborneCeiling,
            income,
            CommanderSettings.AirborneIncomePerAirframe,
            CommanderSettings.AirborneCeilingMax);
    }

    internal static void ReadAirDemandCounts(
        FactionHQ hq, out int capBound, out int capWanted, out int casBound, out int casWanted)
    {
        capBound = 0;
        capWanted = 0;
        casBound = 0;
        casWanted = 0;
        if (Instance == null || !Instance.states.TryGetValue(hq, out OperationsState state))
        {
            return;
        }

        // The home patrol above the baseline counts on the CAP side of this line too, or the
        // diagnostics would report "CAP 0/0" on the very review the wing is buying a fighter.
        capWanted += HomeCapAirDemand(hq);

        for (int i = 0; i < state.AirSorties.Count; i++)
        {
            CommanderAirSortie sortie = state.AirSorties[i];
            if (Time.time < sortie.CooldownUntil)
            {
                continue;
            }

            capBound += sortie.Caps.Count;
            capWanted += sortie.CapsWanted;
            casBound += sortie.Cas.Count;
            casWanted += sortie.Wanted;
        }
    }

    /// <summary>Whether this airframe was launched by this commander's buy loop — the tasking and
    /// posture steps touch nothing else (decision 5: the player's own Air Command missions and
    /// stock-mission authored aircraft are never claimed, bound or retasked).</summary>
    internal static bool IsCommanderAirframe(FactionHQ hq, Aircraft? aircraft)
    {
        return Instance != null
            && aircraft != null
            && Instance.states.TryGetValue(hq, out OperationsState state)
            && state.CommanderAirframes.Contains(aircraft);
    }
}
