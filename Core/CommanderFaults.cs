using System;
using System.Collections.Generic;
using UnityEngine;

namespace GroundControlRts;

/// <summary>
/// Logs an exception caught at a boundary the mod does not own: a Harmony patch body running inside
/// a game method, or one service's hook inside the registry's dispatch loop. The caller swallows the
/// exception so the game method, or the next service, still runs. Each site logs its first failure
/// with the stack trace, then at most once per <see cref="RepeatSeconds"/> with a count, so a fault
/// that repeats every frame cannot flood the log.
/// </summary>
internal static class CommanderFaults
{
    private const float RepeatSeconds = 30f;

    private static readonly Dictionary<string, Site> sites = new(StringComparer.Ordinal);

    internal static void Report(string site, Exception exception)
    {
        float now = Time.realtimeSinceStartup;
        if (!sites.TryGetValue(site, out Site state))
        {
            state = new Site { NextLogAt = now + RepeatSeconds };
            sites[site] = state;
            CommanderPlugin.Log.LogError($"{site} threw and was skipped: {exception}");
            return;
        }

        state.Suppressed++;
        if (now < state.NextLogAt)
        {
            sites[site] = state;
            return;
        }

        CommanderPlugin.Log.LogError(
            $"{site} threw again ({state.Suppressed} times in the last {RepeatSeconds:0} s): "
                + $"{exception.GetType().Name}: {exception.Message}");
        state.Suppressed = 0;
        state.NextLogAt = now + RepeatSeconds;
        sites[site] = state;
    }

    private struct Site
    {
        internal float NextLogAt;
        internal int Suppressed;
    }
}
