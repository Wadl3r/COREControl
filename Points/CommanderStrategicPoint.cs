using UnityEngine;

namespace GroundControlRts;

/// <summary>What discovery decided a point is. Drives its shape on the map, its income rate and
/// which ownership rule applies (see <see cref="CommanderStrategicPoint.GetOwner"/>).</summary>
internal enum StrategicPointKind
{
    /// <summary>An industrial building or a generated fill site. Held by presence like a village
    /// (user decision 2026-09-14) — two vehicles in the ring for the hold time take it — and the
    /// holder is who may build a mine there. While a live mine stands on it, the mine's owner owns
    /// the site whatever is parked in the ring; it only changes hands once that mine is destroyed
    /// AND somebody takes the ground.</summary>
    Site,

    /// <summary>A cluster of civilian buildings. Held by presence, like a hilltop.</summary>
    Village,

    /// <summary>A local high point with no buildings at all. Held by presence, like a village.</summary>
    Hilltop,

    /// <summary>A civilian cluster too small to qualify as a village
    /// (<see cref="CommanderStrategicPointDiscovery.QualifiesAsVillage"/>) — a farmstead or hamlet,
    /// still worth a platoon's time. Held by presence, like a village or a hilltop.</summary>
    Outpost,

    /// <summary>Where <c>CrossroadsMinRoads</c> or more roads meet, found on the road network
    /// rather than on any building or terrain feature. Held by presence, like a village or a
    /// hilltop.</summary>
    Crossroads,

    /// <summary>A point spaced at regular intervals along a road between hilltops and bases, so a
    /// long empty stretch of road still has something to hold. Held by presence, like a village or
    /// a hilltop.</summary>
    Roadside,

    /// <summary>An airbase. Ownership is read live from the game and never stored here.</summary>
    Base,
}

/// <summary>Groups the kinds that share the take-and-hold garrison rule (village, hilltop, outpost,
/// crossroads, roadside and — since 2026-09-14 — the resource site) as against
/// <see cref="StrategicPointKind.Base"/>, which the game owns live — one predicate instead of the
/// growing list of <c>!= Village &amp;&amp; != Hilltop</c> checks this replaces across the service,
/// the AI garrison review and the build-reach check.
/// <para>
/// The site joined the group on the user's own observation: "I just manually moved two units near a
/// resource site — it didn't capture it." A site was owned by whichever mine stood on it and by
/// nothing else, so ground near one meant nothing at all. It is now taken and held exactly like a
/// village; what stays special is what a held site is FOR — it pays nothing itself, and its holder
/// is who may put a mine on it (<see cref="CommanderStrategicPointService.SiteMinePermitted"/>).
/// </para></summary>
internal static class StrategicPointKinds
{
    internal static bool IsControlPoint(StrategicPointKind kind)
    {
        return kind == StrategicPointKind.Village
            || kind == StrategicPointKind.Hilltop
            || kind == StrategicPointKind.Outpost
            || kind == StrategicPointKind.Crossroads
            || kind == StrategicPointKind.Roadside
            || kind == StrategicPointKind.Site;
    }
}

/// <summary>
/// One thing on the map worth holding: a resource site, a control point (village, hilltop,
/// outpost, crossroads or roadside) or a base. Ownership is never stored for a base or a site — it
/// is read live off the game object that actually carries it (<see cref="Airbase.CurrentHQ"/>, a
/// mine's <c>NetworkHQ</c>) — and for a control point it is the index-based <see cref="Hold"/>
/// state machine <see cref="CommanderStrategicPointService.Step"/> drives every 5 s.
/// </summary>
internal sealed class CommanderStrategicPoint
{
    /// <summary>
    /// Every point discovery creates carries at least these four from the moment it exists — a
    /// site, village, hilltop or base with no kind, no position or no label is not a valid point —
    /// so they are constructor arguments rather than fields discovery sets after the fact.
    /// </summary>
    internal CommanderStrategicPoint(StrategicPointKind kind, GlobalPosition position, float radius, string label)
    {
        Kind = kind;
        Position = position;
        Radius = radius;
        Label = label;
        Airbase = null;
        Mine = null;
        // Neutral, not the struct default: OwnerIndex/CandidateIndex 0 would silently mean "held by
        // whichever HQ ends up first in the registry" until the first hold tick ever ran.
        Hold = new HoldState { OwnerIndex = -1, CandidateIndex = -1, Progress = 0f, Contested = false };
    }

    /// <summary>What kind of point this is; see <see cref="StrategicPointKind"/>.</summary>
    internal StrategicPointKind Kind;

    /// <summary>Where the point sits, already snapped to terrain.</summary>
    internal GlobalPosition Position;

    /// <summary>The control ring radius, in metres — where the garrison is PLACED. Also the mine
    /// footprint clearance for a site. Capture presence is measured against
    /// <see cref="CaptureRadius"/>, which is wider.</summary>
    internal float Radius;

    /// <summary>
    /// How much wider the capture ring is than the placement ring for every kind but a site: 1.5
    /// (user, 2026-09-14: "make the capturable area around points 1.5x larger (but don't change unit
    /// placement) — we're having issues of units holding JUST outside the capture ring"). A site's
    /// capture ring was set on its own that day (500 m with the garrison at 0.875 of it), so it is
    /// left alone.
    /// </summary>
    internal const float CaptureRingMultiplier = 1.5f;

    /// <summary>The radius a vehicle must be inside to count toward holding this point, pure over
    /// the kind and the placement radius (see <see cref="CaptureRingMultiplier"/>).</summary>
    internal static float CaptureRadiusFor(StrategicPointKind kind, float radius)
    {
        return kind == StrategicPointKind.Site ? radius : radius * CaptureRingMultiplier;
    }

    /// <summary>This point's capture ring (<see cref="CaptureRadiusFor"/>).</summary>
    internal float CaptureRadius => CaptureRadiusFor(Kind, Radius);

    /// <summary>What the map, the world marker and the log call this point, e.g. <c>"RESOURCE SITE 3"</c>,
    /// <c>"VILLAGE 2"</c>, <c>"HILLTOP 5"</c>, or the airbase's own display name for a base.</summary>
    internal string Label = string.Empty;

    /// <summary>The airbase this point represents. Only set for <see cref="StrategicPointKind.Base"/>.</summary>
    internal Airbase? Airbase;

    /// <summary>The mine standing on this site, or null when it is free. Only meaningful for
    /// <see cref="StrategicPointKind.Site"/>; cleared by the hold tick once the mine is destroyed
    /// (design §2, "Mine destroyed -> site free").</summary>
    internal Unit? Mine;

    /// <summary>The control-point hold state machine. Untouched for a base; a site runs it like any
    /// other control point since 2026-09-14.</summary>
    internal HoldState Hold;

    /// <summary>For a base read back from a strategic save: the hold index of the faction that held
    /// it when the save was taken, or -1. Kept apart from <see cref="Hold"/>, which must stay
    /// neutral for a base because income and build reach read it; consumed and cleared by
    /// <c>ApplyStrategicBaseOwnership</c>.</summary>
    internal int SavedBaseOwnerIndex = -1;

    /// <summary>
    /// True when this point's own footprint is woodland or ground no helicopter will land on, so a
    /// picket here has to be parachuted in or driven (user report 2026-09-14: "air insertion of
    /// pickets sometimes is sent to land in tree covered areas - it cannot land"). Set by discovery
    /// from the game's tree scatter data, and again by any insertion that proved it the hard way.
    /// Deliberately a mark rather than a reason to drop the point — plenty of hilltops worth holding
    /// are wooded.
    /// </summary>
    internal bool Wooded;

    /// <summary>Per-faction ground-vehicle presence count from the last hold tick, indexed the same
    /// way as the service's <c>hqOrder</c> snapshot. Read by the marker label (T12) and the
    /// selection card (T13); never by the pure hold-state functions themselves.</summary>
    internal int[] GarrisonCounts = System.Array.Empty<int>();

    /// <summary>Last frame this point was drawn, and where — the hit-test T13 uses to resolve a
    /// click without repeating the projection math. <c>ScreenFrame</c> stale (not this frame or the
    /// last) means the point was not drawn and cannot be clicked.</summary>
    internal Vector2 ScreenPosition = Vector2.zero;
    internal float ScreenHalfSize = 0f;
    internal int ScreenFrame = -1;

    /// <summary>
    /// This faction's ground-vehicle count in the ring as of the last hold tick, resolved through
    /// the service's HQ-order snapshot. 0 for an HQ not present or not yet known.
    /// </summary>
    internal int PresentCount(FactionHQ hq, System.Collections.Generic.IReadOnlyList<FactionHQ> hqOrder)
    {
        for (int i = 0; i < hqOrder.Count; i++)
        {
            if (ReferenceEquals(hqOrder[i], hq))
            {
                return i < GarrisonCounts.Length ? GarrisonCounts[i] : 0;
            }
        }

        return 0;
    }

    /// <summary>
    /// Who holds this point right now, read live rather than cached: a base's holder can change on
    /// a capture tick this service never sees, and a site's mine can be destroyed between hold
    /// ticks. A control point's ownership comes from the index the pure <see cref="Hold"/> state
    /// machine last settled on, resolved back to a live <see cref="FactionHQ"/> through the
    /// service's own HQ-order snapshot (<see cref="CommanderStrategicPointService.HqAt"/>).
    /// </summary>
    internal FactionHQ? GetOwner()
    {
        switch (Kind)
        {
            case StrategicPointKind.Base:
                return Airbase?.CurrentHQ;
            case StrategicPointKind.Site:
                // A live mine owns the ground it stands on, whatever is parked around it (user
                // decision 2026-09-14): an enemy mine keeps the site enemy-owned until it is
                // destroyed, and only then does the presence holder become the owner. Without that
                // order a picket driving past an enemy mine would take the site out from under a
                // building that is still standing and still paying.
                return Mine != null && !Mine.disabled
                    ? Mine.NetworkHQ
                    : CommanderStrategicPointService.Instance?.HqAt(Hold.OwnerIndex);
            default:
                return CommanderStrategicPointService.Instance?.HqAt(Hold.OwnerIndex);
        }
    }
}

/// <summary>
/// The village/hilltop take-and-hold state machine, as plain ints so
/// <see cref="CommanderStrategicPointService.Step"/> and
/// <see cref="CommanderStrategicPointService.QualifyingFaction"/> can be driven by
/// <c>SelfCheck</c> with synthetic data instead of live <see cref="FactionHQ"/> objects.
/// </summary>
internal struct HoldState
{
    /// <summary>Index into the service's <c>hqOrder</c> snapshot of the faction currently holding
    /// this point, or -1 for neutral. Only ever set for the one faction the machine has settled on
    /// — never written directly by anything reading garrison counts.</summary>
    internal int OwnerIndex;

    /// <summary>Index of the faction currently accumulating <see cref="Progress"/> toward taking
    /// the point, or -1 when nobody is. Reset to -1 whenever the qualifying faction changes.</summary>
    internal int CandidateIndex;

    /// <summary>Cumulative seconds the candidate has held the point alone with at least
    /// <c>MinGarrison</c>. Resets to zero on any lapse, capture or contest.</summary>
    internal float Progress;

    /// <summary>Two or more factions present at once. Frozen: the owner, the candidate and the
    /// progress are all left exactly where they were (design §2, "contested, frozen, pays nobody").</summary>
    internal bool Contested;
}
