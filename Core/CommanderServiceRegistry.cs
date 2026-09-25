using System;
using System.Collections.Generic;

namespace GroundControlRts;

/// <summary>
/// Which missions a service is allowed to run on. Core services run everywhere; advanced
/// services (production, air command, supply, naval, SAM work) only run once
/// <see cref="CommanderFeatureGate"/> says so.
/// </summary>
internal enum CommanderTier
{
    Core,
    Advanced
}

/// <summary>
/// Holds every commander service and dispatches the lifecycle by interface.
/// <para>
/// Registration order is execution order for every phase, so the order of the Register calls
/// in <see cref="CommanderModeController"/> is the whole schedule — read it top to bottom.
/// Services are bucketed once at registration, so no per-frame type tests happen here.
/// </para>
/// </summary>
internal sealed class CommanderServiceRegistry
{
    private readonly List<Gated<ICommanderActivate>> activate = new();
    private readonly List<ICommanderDeactivate> deactivate = new();
    private readonly List<Gated<ICommanderTickActive>> tickActive = new();
    private readonly List<Gated<ICommanderTickPersistent>> tickPersistent = new();
    private readonly List<ICommanderResetSession> resetSession = new();
    private readonly List<ICommanderPersistState> persistState = new();
    private readonly List<ICommanderPersistStrategic> persistStrategic = new();

    /// <summary>
    /// Registers a service under a tier and returns it, so construction and registration stay
    /// one statement. A service lands in a phase list only if it implements that phase's
    /// interface.
    /// </summary>
    internal T Register<T>(T service, CommanderTier tier = CommanderTier.Core)
        where T : class
    {
        bool advanced = tier == CommanderTier.Advanced;
        if (service is ICommanderActivate a)
        {
            activate.Add(new Gated<ICommanderActivate>(a, advanced));
        }
        if (service is ICommanderDeactivate d)
        {
            deactivate.Add(d);
        }
        if (service is ICommanderTickActive t)
        {
            tickActive.Add(new Gated<ICommanderTickActive>(t, advanced));
        }
        if (service is ICommanderTickPersistent p)
        {
            tickPersistent.Add(new Gated<ICommanderTickPersistent>(p, advanced));
        }
        if (service is ICommanderResetSession r)
        {
            resetSession.Add(r);
        }
        if (service is ICommanderPersistState ps)
        {
            persistState.Add(ps);
        }
        if (service is ICommanderPersistStrategic pst)
        {
            persistStrategic.Add(pst);
        }
        return service;
    }

    internal void Activate(bool advancedEnabled)
    {
        for (int i = 0; i < activate.Count; i++)
        {
            Gated<ICommanderActivate> entry = activate[i];
            if (entry.Advanced && !advancedEnabled)
            {
                continue;
            }
            Guard(entry.Service, nameof(ICommanderActivate.Activate), s => s.Activate());
        }
    }

    /// <summary>Activates only the advanced tier, for a mid-session manual unlock.</summary>
    internal void ActivateAdvanced()
    {
        for (int i = 0; i < activate.Count; i++)
        {
            Gated<ICommanderActivate> entry = activate[i];
            if (entry.Advanced)
            {
                Guard(entry.Service, nameof(ICommanderActivate.Activate), s => s.Activate());
            }
        }
    }

    /// <summary>Tears every tier down, gated or not — the gate may have flipped since activation.</summary>
    internal void Deactivate()
    {
        for (int i = 0; i < deactivate.Count; i++)
        {
            Guard(deactivate[i], nameof(ICommanderDeactivate.Deactivate), s => s.Deactivate());
        }
    }

    internal void TickActive(bool advancedEnabled)
    {
        for (int i = 0; i < tickActive.Count; i++)
        {
            Gated<ICommanderTickActive> entry = tickActive[i];
            if (entry.Advanced && !advancedEnabled)
            {
                continue;
            }
            Guard(entry.Service, nameof(ICommanderTickActive.TickActive), s => s.TickActive());
        }
    }

    internal void TickPersistent(bool advancedEnabled)
    {
        for (int i = 0; i < tickPersistent.Count; i++)
        {
            Gated<ICommanderTickPersistent> entry = tickPersistent[i];
            if (entry.Advanced && !advancedEnabled)
            {
                continue;
            }
            Guard(entry.Service, nameof(ICommanderTickPersistent.TickPersistent), s => s.TickPersistent());
        }
    }

    internal void ResetSession()
    {
        for (int i = 0; i < resetSession.Count; i++)
        {
            Guard(resetSession[i], nameof(ICommanderResetSession.ResetSession), s => s.ResetSession());
        }
    }

    /// <summary>Fans a hot-reload snapshot write out to every service that has state worth saving.
    /// Not tier-gated: an Advanced-tier service still exists (just untouched) on an unsupported
    /// mission, and writing its (empty) state then is harmless.</summary>
    internal void SnapshotState(CommanderStateWriter w)
    {
        for (int i = 0; i < persistState.Count; i++)
        {
            Guard(persistState[i], nameof(ICommanderPersistState.Snapshot), s => s.Snapshot(w));
        }
    }

    /// <summary>Fans a hot-reload snapshot restore out to every service that opted in. Called at
    /// most once per mission run, only after <see cref="CommanderStateStore"/> has already checked
    /// the session guard.</summary>
    internal void RestoreState(CommanderStateReader r)
    {
        for (int i = 0; i < persistState.Count; i++)
        {
            Guard(persistState[i], nameof(ICommanderPersistState.Restore), s => s.Restore(r));
        }
    }

    /// <summary>Fans a strategic save write out to every service that owns part of the strategic
    /// picture. Not tier-gated, for the same reason <see cref="SnapshotState"/> is not.</summary>
    internal void SnapshotStrategicState(CommanderStrategicWriter w)
    {
        for (int i = 0; i < persistStrategic.Count; i++)
        {
            Guard(persistStrategic[i], nameof(ICommanderPersistStrategic.SnapshotStrategic), s => s.SnapshotStrategic(w));
        }
    }

    /// <summary>Fans a strategic save read out to every service that opted in, at most once per
    /// mission run and only after <see cref="CommanderStrategicSaveStore"/> has checked the
    /// strategic gate. Every implementer loads records only; the store rebuilds the world itself,
    /// in an order this fan-out cannot express.</summary>
    internal void RestoreStrategicState(CommanderStrategicReader r)
    {
        for (int i = 0; i < persistStrategic.Count; i++)
        {
            Guard(persistStrategic[i], nameof(ICommanderPersistStrategic.RestoreStrategic), s => s.RestoreStrategic(r));
        }
    }

    /// <summary>
    /// Runs one service's hook and contains its exception, so a fault in one service cannot stop
    /// the services registered after it, or skip the session reset that follows a deactivate.
    /// The lambdas passed in capture at most the phase's writer or reader; the per-frame ticks
    /// capture nothing, so the compiler caches them and nothing is allocated per call.
    /// </summary>
    private static void Guard<T>(T service, string phase, Action<T> hook)
        where T : class
    {
        try
        {
            hook(service);
        }
        catch (Exception exception)
        {
            CommanderFaults.Report($"{service.GetType().Name}.{phase}", exception);
        }
    }

    private readonly struct Gated<T>
        where T : class
    {
        internal readonly T Service;
        internal readonly bool Advanced;

        internal Gated(T service, bool advanced)
        {
            Service = service;
            Advanced = advanced;
        }
    }
}
