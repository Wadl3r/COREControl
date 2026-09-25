using System;
using System.Collections.Generic;
using System.Reflection;
using NuclearOption.Networking;
using NuclearOption.SavedMission;
using UnityEngine;

namespace GroundControlRts;

internal sealed partial class CommanderAirCommandService
{
    private static bool TryFindBestLoadout(
        AircraftDefinition definition,
        FactionHQ hq,
        AirCommandMode mode,
        out StandardLoadout loadout,
        out float score)
    {
        loadout = null!;
        score = 0f;
        StandardLoadout[] standardLoadouts = definition.aircraftParameters.StandardLoadouts;
        Aircraft? aircraftPrefab = definition.unitPrefab.GetComponent<Aircraft>();
        if (standardLoadouts == null || aircraftPrefab?.weaponManager == null)
        {
            return false;
        }

        for (int i = 0; i < standardLoadouts.Length; i++)
        {
            StandardLoadout candidate = standardLoadouts[i];
            if (candidate == null || candidate.disabled || candidate.loadout == null)
            {
                continue;
            }

            float candidateScore = ScoreLoadout(candidate.loadout, mode, definition);
            if (candidateScore > score)
            {
                score = candidateScore;
                loadout = candidate;
            }
        }

        return loadout != null && score > 0f;
    }

    /// <summary>
    /// The ordnance a commander-built close-air-support loadout prefers above every other
    /// air-to-ground store (design.md, smarter-air-wing_20260914 Section 1; user decision
    /// 2026-09-14: "a strong preference for AGM-68 and AGM-48 ordnance in CAS missions"). Matched on
    /// the designation prefix the game's own asset data carries — the missile definitions read
    /// <c>AGM-48 </c> (with a trailing space) and <c>AGM-68</c>, and the mounts read
    /// <c>AGM-48 x4</c>, <c>AGM-68 x2</c> and so on — so a prefix match catches every rack size and
    /// the unspaced spellings catch a future rename. Verified against the strings in
    /// <c>NuclearOption_Data/resources.assets</c>, 2026-09-14.
    /// </summary>
    private static readonly string[][] PreferredCasOrdnance =
    {
        new[] { "AGM-68", "AGM68" },
        new[] { "AGM-48", "AGM48" },
    };

    /// <summary>
    /// What a preferred CAS store adds to its mount's score. Additive and far above the whole range
    /// an ordinary air-to-ground mount scores (effectiveness x sqrt(stores) x delivery x range
    /// reaches about 10 for the heaviest rack on the roster), so a hardpoint group that can carry an
    /// AGM-68 or an AGM-48 always picks it. It is applied inside the scorer, which only ever ranks
    /// mounts the hardpoint set already offers: it can never make an incompatible store legal, and
    /// the strip's own acceptance test (<see cref="IsCompatibleAirbase"/> and the mount checks in
    /// <c>ValidateSelectedLoadout</c>) runs afterwards either way.
    /// </summary>
    private const float PreferredCasOrdnanceBonus = 100f;

    /// <summary>
    /// What a suppression loadout hangs on the hardpoints a real anti-radiation missile cannot
    /// reach (user decision 2026-09-14: "ARADs are not using the correct ordinance, should be
    /// AGM-99 or AGM-68 etc"). Ranked, most preferred first, matched on the same designation-prefix
    /// rule as <see cref="PreferredCasOrdnance"/> so a rename lands in one place.
    /// <para>
    /// These are SECOND choice on purpose. Neither store is an anti-radiation weapon: the game's
    /// own anti-radiation missiles are the ARAD-116 and the ARAD-45, they are the only stores
    /// carrying an <c>ARMSeeker</c>, and they are the only thing that homes on a radar emitter.
    /// Verified against the missile definitions in <c>NuclearOption_Data/resources.assets</c>,
    /// 2026-09-14, which describe the AGM-99 as an anti-ship missile and the AGM-68 as an optically
    /// guided missile for structures and heavy armour. Both are long-range standoff stores the
    /// suppression airframe uses to kill the launcher once its radar is down, which is why they are
    /// the right thing on a pylon that cannot take a missile and the wrong thing on one that can.
    /// </para>
    /// </summary>
    private static readonly string[][] PreferredAradSecondaryOrdnance =
    {
        new[] { "AGM-99", "AGM99" },
        new[] { "AGM-68", "AGM68" },
    };

    /// <summary>
    /// What a preferred standoff store adds to its mount's suppression score. The same 100 the CAS
    /// preference uses and for the same reason: additive, and far above the whole range an ordinary
    /// air-to-ground mount scores, so a hardpoint group that can carry an AGM-99 always picks it
    /// over an AGM-68, and either over anything else the group offers. One tier apart, so the order
    /// is AGM-99, then AGM-68, then the rest on their own merits.
    /// </summary>
    private const float PreferredAradSecondaryBonus = 100f;

    /// <summary>
    /// What a REAL anti-radiation missile adds to its mount's suppression score, so it wins its
    /// hardpoint group outright against any standoff store beside it. The same 1000 the home-CAP
    /// radar-missile preference uses (Reuse rule 5: one shape, two callers) - big enough to win the
    /// group, and applied only while a mount is being chosen, so it never reaches the loadout score
    /// the caller reads back.
    /// </summary>
    private const float AradArmSelectionBonus = 1000f;

    /// <summary>Where this mount sits in the <see cref="PreferredAradSecondaryOrdnance"/> table - 0
    /// is the most preferred (AGM-99), 1 the next (AGM-68), -1 not preferred at all. The CAS rank's
    /// own exclusions apply, so a nuclear store, a jammer, a reconnaissance round or a store rated
    /// at nothing against ground is never preferred however it is named. Internal (one-word
    /// widening, Reuse rule 4): the enemy commander's self-check asserts the order with it.</summary>
    internal static int PreferredAradSecondaryRank(WeaponMount? mount)
    {
        WeaponInfo? info = mount?.info;
        if (info == null
            || info.nuclear
            || info.jammer
            || IsReconRound(mount, info)
            || info.effectiveness.antiSurface <= 0.05f)
        {
            return -1;
        }

        string identity = GetWeaponIdentity(mount, info);
        for (int tier = 0; tier < PreferredAradSecondaryOrdnance.Length; tier++)
        {
            if (ContainsWeaponToken(identity, PreferredAradSecondaryOrdnance[tier]))
            {
                return tier;
            }
        }

        return -1;
    }

    /// <summary>
    /// How a suppression buy ranks one mount inside its hardpoint group: a real anti-radiation
    /// missile first, whatever is offered beside it; then the named standoff stores in their own
    /// order; then any ordinary ground-attack store on its plain close-air-support score. Zero for
    /// anything that can neither home on a radar nor hit the ground, so an air-to-air missile never
    /// reaches a suppression pylon - which is the bug this rule exists for.
    /// Internal (one-word widening): the enemy commander's self-check asserts the order with it.
    /// </summary>
    internal static float ScoreAradMountForCommander(WeaponMount? mount)
    {
        WeaponInfo? info = mount?.info;
        if (info == null)
        {
            return 0f;
        }

        if (IsAradWeapon(info, mount))
        {
            return AradArmSelectionBonus + ScoreMount(mount!, AirCommandMode.Arad);
        }

        // The plain ground-attack score, with the CAS designation preference deliberately OFF: the
        // suppression table below is what decides this pylon, and two preference tables fighting
        // over the same mount is exactly the fork Reuse rule 4 forbids.
        float groundScore = ScoreMount(mount!, AirCommandMode.Cas);
        if (groundScore <= 0f)
        {
            return 0f;
        }

        int rank = PreferredAradSecondaryRank(mount);
        return rank < 0
            ? groundScore
            : groundScore + PreferredAradSecondaryBonus * (PreferredAradSecondaryOrdnance.Length - rank);
    }

    /// <summary>How much better these missiles deliver their warhead than an ordinary store of the
    /// same effectiveness — the standing multiplier the scorer has carried since the AIR window was
    /// written (lock-on after launch, erratic terminal manoeuvring, launch from behind terrain).
    /// Unchanged in value; it only moved here so the designation table has one home.</summary>
    private const float PreferredCasOrdnanceDelivery = 1.42f;

    /// <summary>Where this mount sits in the <see cref="PreferredCasOrdnance"/> table — 0 is the
    /// most preferred tier (AGM-68), 1 the next (AGM-48), -1 not preferred at all. A store that is
    /// nuclear, a jammer, a reconnaissance round (<see cref="ReconRoundDesignations"/>) or rated at
    /// nothing against ground is never preferred however it is named. Internal (one-word widening,
    /// Reuse rule 4): the loadout scorer ranks with it and the enemy commander's roster line reports
    /// with it — one definition of "preferred CAS ordnance", two callers.</summary>
    internal static int PreferredCasOrdnanceRank(WeaponMount? mount)
    {
        WeaponInfo? info = mount?.info;
        // The recon variant of a preferred missile is never preferred (Departure 16, 2026-09-14):
        // it carries the same designation and the same rack and destroys nothing, so the
        // designation table would otherwise rank it exactly as the real store.
        if (info == null
            || info.nuclear
            || info.jammer
            || IsReconRound(mount, info)
            || info.effectiveness.antiSurface <= 0.05f)
        {
            return -1;
        }

        string identity = GetWeaponIdentity(mount, info);
        for (int tier = 0; tier < PreferredCasOrdnance.Length; tier++)
        {
            if (ContainsWeaponToken(identity, PreferredCasOrdnance[tier]))
            {
                return tier;
            }
        }

        return -1;
    }

    /// <summary>Whether this mount carries any preferred CAS ordnance at all.</summary>
    internal static bool IsPreferredCasOrdnance(WeaponMount? mount)
    {
        return PreferredCasOrdnanceRank(mount) >= 0;
    }

    /// <summary>The commander's own CAS ranking for one mount — the number
    /// <see cref="AutoConfigureRoleLoadout"/> picks each hardpoint group's store by. Internal
    /// (one-word widening): the enemy commander's self-check asserts the ordnance order with it.</summary>
    internal static float ScoreCasMountForCommander(WeaponMount mount)
    {
        return ScoreMount(mount, AirCommandMode.Cas, preferCasOrdnance: true);
    }

    /// <summary>Whether a built loadout carries any preferred CAS ordnance at all — what the launch
    /// note and the roster line report.</summary>
    internal static bool LoadoutHasPreferredCasOrdnance(Loadout? loadout)
    {
        if (loadout?.weapons == null)
        {
            return false;
        }

        for (int i = 0; i < loadout.weapons.Count; i++)
        {
            if (IsPreferredCasOrdnance(loadout.weapons[i]))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// What the commander's own CAS loadout for <paramref name="definition"/> ends up carrying:
    /// whether it got one of the <see cref="PreferredCasOrdnance"/> designations, and the name of
    /// the heaviest-scoring air-to-ground store it carries either way. False when the airframe has
    /// no ground-attack loadout at all. What the <c>Air roster</c> line reports (design SS1:
    /// "AGM-68/AGM-48 available" or "none — falls back to &lt;best A/G&gt;").
    /// </summary>
    internal static bool TryDescribeCasOrdnance(
        AircraftDefinition definition, FactionHQ hq, out bool preferred, out string bestStore)
    {
        preferred = false;
        bestStore = string.Empty;
        if (!TryBuildRoleLoadout(definition, hq, AirCommandMode.Cas, preferArhMissiles: false, out Loadout loadout, out _))
        {
            return false;
        }

        preferred = LoadoutHasPreferredCasOrdnance(loadout);
        float best = 0f;
        for (int i = 0; i < loadout.weapons.Count; i++)
        {
            WeaponMount? mount = loadout.weapons[i];
            if (mount == null)
            {
                continue;
            }

            float score = ScoreMount(mount, AirCommandMode.Cas);
            if (score > best)
            {
                best = score;
                bestStore = GetWeaponTypeName(mount);
            }
        }

        return true;
    }

    /// <summary>
    /// Every store this airframe's hardpoints offer whose NAME matches the preferred CAS ordnance
    /// table, with the two damage numbers it declares and the rank the scorer computes for it
    /// (evidence line, 2026-09-14). Deliberately read straight off the hardpoint sets rather than
    /// off a built loadout: its whole job is to prove — or disprove — that the game's real AGM-68
    /// and AGM-48 declare a warhead, and a report that went through the scorer could not show the
    /// scorer being wrong. A rank of -1 beside a non-zero damage pair would mean the table's
    /// designations have changed; a zero damage pair on a real missile would mean the warhead rule
    /// is reading the wrong field and must be taken off the CAS path.
    /// </summary>
    internal static string DescribePreferredCasOrdnanceData(AircraftDefinition? definition)
    {
        HardpointSet[]? sets = definition?.unitPrefab?.GetComponent<Aircraft>()?.weaponManager?.hardpointSets;
        if (sets == null)
        {
            return string.Empty;
        }

        List<string> seen = new();
        for (int s = 0; s < sets.Length; s++)
        {
            List<WeaponMount>? options = sets[s].weaponOptions;
            for (int o = 0; options != null && o < options.Count; o++)
            {
                WeaponMount? mount = options[o];
                WeaponInfo? info = mount?.info;
                if (info == null || !NameMatchesPreferredCasOrdnance(mount, info))
                {
                    continue;
                }

                // Names WHERE the damage comes from, not just whether there is any: the first
                // version of this line printed the parent's two damage numbers alone, which is how
                // the AGM-48 came to read "no warhead" when its damage is in its submunitions.
                // Information only since Departure 16 — nothing decides anything on it, so there
                // is no alarm text: `payload unknown` is the ordinary answer for a store whose
                // payload the catalog does not expose, and the AGM-48 is one of those.
                string line = $"{info.weaponName} ({DescribePayload(info)}, "
                    + $"A/G {info.effectiveness.antiSurface:0.00}, rank {PreferredCasOrdnanceRank(mount)}"
                    + (IsReconRound(mount, info) ? ", RECON ROUND — excluded" : string.Empty)
                    + ")";
                if (!seen.Contains(line))
                {
                    seen.Add(line);
                }
            }
        }

        return string.Join(", ", seen);
    }

    /// <summary>The designation half of <see cref="PreferredCasOrdnanceRank"/> on its own — used by
    /// the evidence line above, which has to find the stores the table NAMES whether or not they
    /// currently pass the rank's other tests.</summary>
    private static bool NameMatchesPreferredCasOrdnance(WeaponMount? mount, WeaponInfo info)
    {
        string identity = GetWeaponIdentity(mount, info);
        for (int tier = 0; tier < PreferredCasOrdnance.Length; tier++)
        {
            if (ContainsWeaponToken(identity, PreferredCasOrdnance[tier]))
            {
                return true;
            }
        }

        return false;
    }

    /// <param name="preferCasOrdnance">Rank <see cref="PreferredCasOrdnance"/> above every other
    /// air-to-ground store (design SS1). Only the commander's own loadout builder passes true: the
    /// player's AIR window keeps its unweighted weapon list so the preference never reorders what a
    /// human is choosing from.</param>
    private static float ScoreLoadout(
        Loadout loadout,
        AirCommandMode mode,
        AircraftDefinition? aircraftDefinition = null,
        bool preferCasOrdnance = false)
    {
        float score = 0f;
        bool hasRadar = false;
        bool hasJammer = false;
        float radarRange = 0f;
        float jammerRange = 0f;
        int remainingLaserTargets = GetLaserTargetCapacity(loadout, aircraftDefinition);
        for (int i = 0; i < loadout.weapons.Count; i++)
        {
            WeaponMount? mount = loadout.weapons[i];
            if (mount == null)
            {
                continue;
            }

            WeaponInfo? info = mount.info;
            switch (mode)
            {
                case AirCommandMode.AwacsJammer:
                    SpecialAirSystem specialSystem = GetSpecialAirSystem(mount);
                    if (specialSystem == SpecialAirSystem.Radar)
                    {
                        hasRadar = true;
                        radarRange = Mathf.Max(radarRange, GetSpecialSystemRange(mount));
                    }
                    if (specialSystem == SpecialAirSystem.RadarJammer)
                    {
                        hasJammer = true;
                        jammerRange = Mathf.Max(jammerRange, GetSpecialSystemRange(mount));
                    }
                    break;

                case AirCommandMode.Cas:
                    // A reconnaissance round must never count toward "this airframe can attack
                    // ground" (Departure 16, 2026-09-14). Everything else is ranked on its A/G
                    // rating exactly as it was before the warhead rule was tried.
                    if (info != null
                        && !info.nuclear
                        && !info.jammer
                        && !IsReconRound(mount, info)
                        && !IsStrategicStrikeWeapon(info, mount)
                        && info.effectiveness.antiSurface > 0.05f)
                    {
                        score += ScoreConventionalWeapon(
                            info.effectiveness.antiSurface, mount, info, ref remainingLaserTargets);
                        int preferredTier = preferCasOrdnance ? PreferredCasOrdnanceRank(mount) : -1;
                        if (preferredTier >= 0)
                        {
                            // One tier above the next, both far above every ordinary store: the
                            // AGM-68 wins a hardpoint the AGM-48 could also fill, and either wins
                            // over anything else the group offers.
                            score += PreferredCasOrdnanceBonus * (PreferredCasOrdnance.Length - preferredTier);
                        }
                    }
                    break;

                case AirCommandMode.AirGuard:
                    if (info != null
                        && !info.jammer
                        && info.effectiveness.antiAir > 0.05f
                        && info.effectiveness.antiSurface <= 0.05f)
                    {
                        score += ScoreConventionalWeapon(
                            info.effectiveness.antiAir, mount, info, ref remainingLaserTargets);
                    }
                    break;

                case AirCommandMode.Arad:
                    if (info != null && IsAradWeapon(info, mount))
                    {
                        score += ScoreConventionalWeapon(
                            Mathf.Max(info.effectiveness.antiRadar, 0.5f), mount, info, ref remainingLaserTargets);
                    }
                    break;

                case AirCommandMode.StrategicStrike:
                    if (info != null && IsStrategicStrikeWeapon(info, mount))
                    {
                        score += 20f + ScoreConventionalWeapon(
                            Mathf.Max(info.effectiveness.antiSurface, info.effectiveness.antiRadar, 0.5f),
                            mount,
                            info,
                            ref remainingLaserTargets);
                    }
                    break;
            }
        }

        if (mode == AirCommandMode.AwacsJammer)
        {
            if (hasRadar) score += 16f + radarRange / 10000f;
            if (hasJammer) score += 8f + jammerRange / 10000f;
        }

        return score;
    }

    private static float ScoreConventionalWeapon(
        float effectiveness,
        WeaponMount mount,
        WeaponInfo info,
        ref int remainingLaserTargets)
    {
        int usefulStores = Mathf.Max(mount.ammo, 1);
        if (info.laserGuided)
        {
            usefulStores = Mathf.Min(usefulStores, Mathf.Max(remainingLaserTargets, 0));
            remainingLaserTargets = Mathf.Max(remainingLaserTargets - usefulStores, 0);
            if (usefulStores == 0)
            {
                return 0f;
            }
        }

        // Guns use round counts several orders of magnitude above discrete stores.
        float quantity = info.gun ? 1f : Mathf.Sqrt(usefulStores);
        float delivery = 1f;
        if (info.gun) delivery *= 0.5f;
        if (info.missile) delivery *= 1.12f;
        if (info.bomb) delivery *= 0.88f;
        if (info.glideBomb) delivery *= 1.28f;
        if (info.overHorizon) delivery *= 1.2f;
        if (info.laserGuided) delivery *= 0.78f;

        if (info.missile)
        {
            float speed = info.GetMaxSpeed();
            if (speed > 0f)
            {
                delivery *= speed switch
                {
                    < 250f => Mathf.Lerp(0.4f, 0.58f, speed / 250f),
                    < 400f => Mathf.Lerp(0.58f, 0.82f, (speed - 250f) / 150f),
                    < 700f => Mathf.Lerp(0.82f, 1.08f, (speed - 400f) / 300f),
                    _ => Mathf.Clamp(1.08f + (speed - 700f) / 3000f, 1.08f, 1.25f),
                };
            }
        }

        float maxRange = Mathf.Max(info.targetRequirements.maxRange, 1000f);
        float rangeFactor = Mathf.Clamp(Mathf.Sqrt(maxRange / 10000f), 0.72f, 1.45f);
        string identity = GetWeaponIdentity(mount, info);
        // One definition of the preferred designations (Reuse rule 4): this delivery nudge and the
        // commander's dominant CAS bonus read the same table, so a rename lands in both at once.
        for (int tier = 0; tier < PreferredCasOrdnance.Length; tier++)
        {
            if (ContainsWeaponToken(identity, PreferredCasOrdnance[tier]))
            {
                delivery *= PreferredCasOrdnanceDelivery;
                break;
            }
        }
        if (ContainsWeaponToken(identity, "AGM-99", "AGM99")) delivery *= 0.4f;
        if (ContainsWeaponToken(identity, "KINGPIN")) delivery *= 1.35f;
        if (info.glideBomb && ContainsWeaponToken(identity, "CLUSTER")) delivery *= 1.22f;

        return effectiveness * quantity * delivery * rangeFactor;
    }

    private static int GetLaserTargetCapacity(Loadout loadout, AircraftDefinition? definition)
    {
        int capacity = 0;
        LaserDesignator? builtIn = definition?.unitPrefab?.GetComponentInChildren<LaserDesignator>(true);
        if (builtIn != null)
        {
            capacity = Mathf.Max(capacity, builtIn.GetMaxTargets());
        }
        for (int i = 0; i < loadout.weapons.Count; i++)
        {
            LaserDesignator? mounted = loadout.weapons[i]?.prefab?.GetComponentInChildren<LaserDesignator>(true);
            if (mounted != null)
            {
                capacity = Mathf.Max(capacity, mounted.GetMaxTargets());
            }
        }
        return Mathf.Max(capacity, 1);
    }

    private static bool IsStrategicStrikeWeapon(WeaponInfo info, WeaponMount? mount = null)
    {
        // A reconnaissance round is nobody's strike weapon either (Departure 16, 2026-09-14): a
        // recon variant carries the same guidance as the real store and none of the damage, so the
        // component and token tests below would both pass it.
        if (IsReconRound(mount, info))
        {
            return false;
        }

        if (info.strategic)
        {
            return true;
        }
        GameObject? prefab = info.weaponPrefab;
        if (prefab != null
            && (prefab.GetComponent<OpticalSeekerCruiseMissile>() != null
                || prefab.GetComponent<BallisticMissileGuidance>() != null))
        {
            return true;
        }
        return ContainsWeaponToken(
            GetWeaponIdentity(mount, info),
            "CRUISE",
            "TBM",
            "BALLISTIC",
            "TUSKO-B",
            "TUSKO B",
            "TUSKOB");
    }

    private static string GetWeaponIdentity(WeaponMount? mount, WeaponInfo info)
    {
        return string.Join("|", new[]
        {
            info.weaponName,
            info.shortName,
            info.name,
            mount?.mountName,
            mount?.jsonKey,
            mount?.name,
        });
    }

    private static bool ContainsWeaponToken(string identity, params string[] tokens)
    {
        for (int i = 0; i < tokens.Length; i++)
        {
            if (identity.IndexOf(tokens[i], StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }
        }
        return false;
    }

    private static bool IsStationEligible(WeaponStation station, AirCommandMode mode)
    {
        WeaponInfo? info = station.WeaponInfo;
        if (info == null)
        {
            return false;
        }

        return mode switch
        {
            AirCommandMode.AwacsJammer => info.jammer,
            AirCommandMode.Cas => !info.nuclear
                && !info.jammer
                && !IsReconRound(null, info)
                && !IsStrategicStrikeWeapon(info)
                && info.effectiveness.antiSurface > 0.05f,
            // Dual-role weapons remain manually usable, but ScoreLoadout keeps
            // them out of the recommended Air Superiority section.
            AirCommandMode.AirGuard => !info.jammer && info.effectiveness.antiAir > 0.05f,
            AirCommandMode.Arad => IsAradWeapon(info),
            AirCommandMode.StrategicStrike => IsStrategicStrikeWeapon(info),
            _ => false,
        };
    }

    private static bool IsTargetEligible(Unit target, AirCommandMode mode, bool targetOrdnance)
    {
        return mode switch
        {
            AirCommandMode.AwacsJammer => target.HasRadarEmission(),
            AirCommandMode.Cas => target is GroundVehicle || target is Ship || target is Building,
            AirCommandMode.AirGuard => target is Aircraft || (targetOrdnance && target is Missile),
            AirCommandMode.Arad => target is not Aircraft && target.HasRadarEmission(),
            AirCommandMode.StrategicStrike => target is GroundVehicle || target is Ship || target is Building,
            _ => false,
        };
    }

    /// <summary>
    /// Whether a store's catalog entry declares any damage, pure: <c>WeaponInfo.pierceDamage</c>
    /// and <c>WeaponInfo.blastDamage</c>, the two numbers <c>Unit.TakeDamage</c> is given and
    /// <c>Missile.InterceptPriority</c> reads off the same fields.
    /// <para>REPORTING ONLY since Departure 16 (2026-09-14). It was the basis of the
    /// reconnaissance-round rule until the installed build proved the catalog does not tell the
    /// truth about lethality: the real AGM-48 declares 0 and 0 and kills in play, so every rule
    /// built on these numbers disarmed it. The roster's information line still prints them, because
    /// a reader asking why a store was picked deserves to see what the commander can and cannot
    /// see; nothing decides anything on them.</para>
    /// </summary>
    internal static bool DeliversDamage(float pierceDamage, float blastDamage)
    {
        return pierceDamage > 0f || blastDamage > 0f;
    }

    /// <summary>
    /// Designations of the game's reconnaissance rounds — stores that fly like a weapon, are rated
    /// like a weapon, and destroy nothing. The Eyeball Mk.II is the one the user found the
    /// commander hanging four of on a suppression helicopter; the game's own description of it is
    /// "This modified AGM-48 replaces the missile's usual warhead with a panoramic optical sensor
    /// suite capable of detecting enemy targets and registering them as contacts on faction
    /// datalink".
    /// <para>This is a NAME test, which everything else in this scorer deliberately avoids, and it
    /// is a considered choice rather than a shortcut (Departure 16, 2026-09-14). Three attempts to
    /// key it on data all failed against the game's real assets: there is no reconnaissance flag,
    /// weapon type or sensor component anywhere in <c>Assembly-CSharp</c>; the catalog's damage
    /// pair reads 0/0 for the AGM-48 as well, which is a real and lethal store; and the
    /// <c>SubmunitionDispenser</c> that must hold the AGM-48's payload cannot be reached from the
    /// catalog mount either — the roster line reported
    /// <c>AGM-48 (NO WARHEAD AND NO SUBMUNITIONS)</c> on a live reload. Every data-keyed rule we
    /// could write therefore disarmed the user's preferred close-support missile in order to
    /// exclude the sensor round. Excluding one asset by its own name does not.</para>
    /// <para>A table rather than a literal so a second recon round can be added in one line.
    /// Matched against the mount's whole identity (mount name, weapon name, short name), the same
    /// <see cref="GetWeaponIdentity"/> the preferred-ordnance table is matched against.</para>
    /// </summary>
    private static readonly string[] ReconRoundDesignations = { "Eyeball" };

    /// <summary>Whether one designation string names a reconnaissance round, pure, for the
    /// self-check: the test the live <see cref="IsReconRound(WeaponMount?, WeaponInfo?)"/> makes,
    /// with the identity already gathered.</summary>
    internal static bool IsReconRoundIdentity(string? identity)
    {
        return identity != null && ContainsWeaponToken(identity, ReconRoundDesignations);
    }

    /// <summary>The live read: a store whose identity names it as a reconnaissance round. Such a
    /// store is never CAS, never strike, never suppression and never preferred ordnance.</summary>
    internal static bool IsReconRound(WeaponMount? mount, WeaponInfo? info)
    {
        return info != null && IsReconRoundIdentity(GetWeaponIdentity(mount, info));
    }

    /// <summary>Per weapon, its dispensed submunition load — read once and remembered, because it
    /// reaches through a prefab component by reflection and the scorers ask per mount per
    /// review.</summary>
    private static readonly Dictionary<WeaponInfo, (int Count, WeaponInfo? Type)> submunitionLoads = new();

    /// <summary>The <c>submunitions</c> array length and <c>submunitionType</c> of the
    /// <c>SubmunitionDispenser</c> on this weapon's missile prefab, or (0, null) when it carries
    /// none. Both fields are private and serialized, so they are read by reflection — the same
    /// approach the loadout scorer already uses for the game's private <c>rangeFalloff</c>.</summary>
    private static (int Count, WeaponInfo? Type) GetSubmunitionLoad(WeaponInfo info)
    {
        if (info.weaponPrefab == null)
        {
            // No prefab, nothing to reach into — and deliberately not remembered, because the only
            // stores without one are the self-check's synthetic probes, which are destroyed as soon
            // as the check ends and must not be held alive by this table.
            return (0, null);
        }

        if (submunitionLoads.TryGetValue(info, out (int Count, WeaponInfo? Type) cached))
        {
            return cached;
        }

        (int Count, WeaponInfo? Type) load = (0, null);
        SubmunitionDispenser? dispenser = info.weaponPrefab.GetComponentInChildren<SubmunitionDispenser>(true);
        if (dispenser != null)
        {
            const BindingFlags privateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
            GameObject[]? submunitions = typeof(SubmunitionDispenser)
                .GetField("submunitions", privateInstance)?.GetValue(dispenser) as GameObject[];
            WeaponInfo? type = typeof(SubmunitionDispenser)
                .GetField("submunitionType", privateInstance)?.GetValue(dispenser) as WeaponInfo;
            load = (submunitions?.Length ?? 0, type);
        }

        submunitionLoads[info] = load;
        return load;
    }

    /// <summary>Where a store's damage comes from, for the roster's information line: its own
    /// warhead, the submunitions it dispenses, or — for the AGM-48 and anything else whose payload
    /// the catalog does not expose — unknown. Nothing decides anything on this any more
    /// (Departure 16); it is printed because a reader asking "why did it pick that store" deserves
    /// to see what the commander can and cannot see.</summary>
    internal static string DescribePayload(WeaponInfo info)
    {
        if (DeliversDamage(info.pierceDamage, info.blastDamage))
        {
            return $"warhead pierce {info.pierceDamage:0.#}/blast {info.blastDamage:0.#}";
        }

        (int count, WeaponInfo? type) = GetSubmunitionLoad(info);
        if (count > 0 && type != null && DeliversDamage(type.pierceDamage, type.blastDamage))
        {
            return $"cluster {count}x {type.weaponName} pierce {type.pierceDamage:0.#}/blast {type.blastDamage:0.#}";
        }

        return count > 0 ? $"cluster {count}x, submunition damage not declared" : "payload unknown";
    }

    /// <summary>
    /// The anti-radiation rule, pure (user report 2026-09-14). An anti-radiation store is a MISSILE
    /// that carries a warhead AND homes on a radar emitter. All three are read off the game's own
    /// weapon data — <c>WeaponInfo.missile</c>, the damage pair, and an <c>ARMSeeker</c> component
    /// on the missile prefab, which is where <c>Missile.Awake</c> looks for its seeker.
    /// <para>The rule this replaces had four alternatives, any one of which was enough, and two of
    /// them were not evidence of an anti-radiation weapon at all: <c>targetRequirements.minRadar</c>
    /// says only that the TARGET must be emitting, which a passive sensor round can require just as
    /// easily, and a name containing "ARAD" is not data. That is how an Eyeball Mk.II — an AGM-48
    /// with its warhead removed — came to fill four hardpoints of a suppression helicopter.</para>
    /// Nuclear stores are excluded here as they are everywhere else in this scorer.
    /// </summary>
    /// <remarks>
    /// The seeker component alone carries the "homes on a radar" half, deliberately: the catalog's
    /// own <c>effectiveness.antiRadar</c> rating is NOT required as well. Requiring both would mean
    /// a real anti-radiation missile whose asset leaves that rating at zero could never fly
    /// suppression at all, and the asset values cannot be read outside the running game — whereas
    /// an <c>ARMSeeker</c> on the prefab is unambiguous, and a radar-homing air-to-air missile
    /// carries an <c>ARHSeeker</c> instead, so it is excluded by this test and not by a rating
    /// comparison.
    /// </remarks>
    /// <param name="reconRound">Whether the store is one of the game's reconnaissance rounds
    /// (<see cref="IsReconRound"/>). No payload test: the catalog does not expose the AGM-48's
    /// payload either, so any such test refuses real weapons along with the sensor round
    /// (Departure 16, 2026-09-14).</param>
    internal static bool IsAradCandidate(bool missile, bool nuclear, bool hasArmSeeker, bool reconRound)
    {
        // Only a radar-homing seeker qualifies. Upstream 81bbfad also admitted any guided
        // anti-surface missile with a warhead (AGM-99, AGM-68); that was reverted so a real ARM
        // ranks first on a suppression pylon and the standoff stores stay secondary.
        return missile
            && !nuclear
            && !reconRound
            && hasArmSeeker;
    }

    private static bool IsAradWeapon(WeaponInfo info, WeaponMount? mount = null)
    {
        return info != null
            && IsAradCandidate(
                info.missile,
                info.nuclear,
                info.weaponPrefab?.GetComponentInChildren<ARMSeeker>(true) != null,
                IsReconRound(mount, info));
    }

    /// <summary>The anti-radiation stores in a built loadout, named, for the roster line
    /// (user report 2026-09-14). Empty when the loadout carries none — which, after the rule above,
    /// is what an airframe that cannot fly suppression looks like.</summary>
    /// <param name="withCounts">Append how many of each store the pylons hold, as
    /// <c>ARAD-116 x4</c>. The launch line reports the load that way (user decision 2026-09-14), the
    /// roster line reports the designations alone; one walk, two callers (Reuse rule 5).</param>
    internal static string DescribeAradWeapons(Loadout? loadout, bool withCounts = false)
    {
        if (loadout == null)
        {
            return string.Empty;
        }

        List<string> names = new();
        List<int> counts = new();
        for (int i = 0; i < loadout.weapons.Count; i++)
        {
            WeaponMount? mount = loadout.weapons[i];
            WeaponInfo? info = mount?.info;
            if (info == null || !IsAradWeapon(info, mount))
            {
                continue;
            }

            int at = names.IndexOf(info.weaponName);
            int stores = Mathf.Max(1, mount!.ammo);
            if (at < 0)
            {
                names.Add(info.weaponName);
                counts.Add(stores);
            }
            else
            {
                counts[at] += stores;
            }
        }

        if (withCounts)
        {
            for (int i = 0; i < names.Count; i++)
            {
                names[i] = $"{names[i]} x{counts[i]}";
            }
        }

        return string.Join(", ", names);
    }

    /// <summary>
    /// Whether this aeroplane is carrying anti-radiation stores RIGHT NOW — read off its live
    /// weapon stations, not off what its type could be given (user report, 2026-09-14). The type
    /// test asks whether a suppression loadout COULD be built for the airframe, which is the right
    /// question for a purchase and the wrong one for a binding: an FS-12 Revoker bought for the home
    /// patrol and flying four air-to-air missiles passes it, and the 2026-09-14 match duly sent one
    /// against a belt of twenty-three launchers with nothing aboard that could hit a radar.
    /// <para>A station with no rounds left does not count: an airframe that has shot its missiles is
    /// not a suppression airframe any more.</para>
    /// </summary>
    internal static bool CarriesAntiRadiation(Aircraft? aircraft)
    {
        if (aircraft == null || aircraft.disabled)
        {
            return false;
        }

        List<WeaponStation>? stations = aircraft.weaponStations;
        if (stations == null)
        {
            return false;
        }

        for (int i = 0; i < stations.Count; i++)
        {
            WeaponStation station = stations[i];
            WeaponInfo? info = station?.WeaponInfo;
            if (info != null && station!.Ammo > 0 && IsAradWeapon(info))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Whether <paramref name="info"/> is an active-radar-homing air-to-air missile — the game's own
    /// <c>ARHSeeker</c> component on the missile prefab (<c>Missile.Awake</c> reads its seeker off its
    /// own GameObject, verified by decompile), on a weapon the game types as a missile and scores
    /// against air. The air test is the same 0.05 the AIR window's loadout label uses for its <c>A/A</c>
    /// tag — one threshold, two callers. Internal (one-word widening, Reuse rule 4): the enemy
    /// commander's home-CAP buy classifies a fighter's loadout with the same test — the CAP must be
    /// flown by an airframe that can shoot at something it has not been handed (user decision
    /// 2026-09-14: home-CAP fighters carry an ARH missile).
    /// </summary>
    internal static bool IsArhAirToAirMissile(WeaponInfo info)
    {
        return info != null
            && info.missile
            && info.effectiveness.antiAir > 0.05f
            && info.weaponPrefab?.GetComponentInChildren<ARHSeeker>(true) != null;
    }

    private static void NormalizeLoadoutLength(Loadout loadout, AircraftDefinition definition)
    {
        Aircraft? aircraft = definition.unitPrefab != null
            ? definition.unitPrefab.GetComponent<Aircraft>()
            : null;
        int hardpointCount = aircraft?.weaponManager?.hardpointSets?.Length ?? loadout.weapons.Count;
        while (loadout.weapons.Count < hardpointCount)
        {
            loadout.weapons.Add(null!);
        }
        if (loadout.weapons.Count > hardpointCount)
        {
            loadout.weapons.RemoveRange(hardpointCount, loadout.weapons.Count - hardpointCount);
        }
    }

    /// <summary>
    /// True when this airframe is flown by a fixed-wing AI pilot, which is the only kind an Air
    /// Command mission can steer. Everything the mod does to a commanded aircraft — the idle-timer
    /// prefix, the route destination, the altitude hold — is a patch on
    /// <c>AIPilotCombatModes</c>, and only <c>PilotType.Plane</c> uses that state machine:
    /// <c>Pilot.SetStartingAiState</c> puts helicopters and tiltwings on <c>AIHeloCombatState</c>
    /// instead. Handing one of those a mission applies the target half of it and none of the flying
    /// half, which is how the enemy's VTOLs ended up nosing into the ground shortly after takeoff.
    /// </summary>
    internal static bool HasPlanePilot(AircraftDefinition definition)
    {
        return definition.unitPrefab != null
            && HasPlanePilot(definition.unitPrefab.GetComponent<Aircraft>());
    }

    internal static bool HasPlanePilot(Aircraft? aircraft)
    {
        if (aircraft?.pilots == null)
        {
            return false;
        }

        for (int i = 0; i < aircraft.pilots.Length; i++)
        {
            if (aircraft.pilots[i] != null && aircraft.pilots[i].pilotType == Pilot.PilotType.Plane)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// True when this airframe flies like a helicopter — its prefab carries a rotary pilot. The
    /// definition-level twin of <see cref="IsRotaryPilot"/> (one definition of "flies like a
    /// helicopter", read off the prefab instead of a live airframe), for the rotary CAS buy
    /// (design.md, smarter-air-wing_20260914 Section 2).
    /// </summary>
    internal static bool IsRotaryAirframe(AircraftDefinition definition)
    {
        Aircraft? prefab = definition.unitPrefab != null ? definition.unitPrefab.GetComponent<Aircraft>() : null;
        if (prefab?.pilots == null)
        {
            return false;
        }

        for (int i = 0; i < prefab.pilots.Length; i++)
        {
            if (prefab.pilots[i] != null && IsRotaryPilot(prefab.pilots[i]))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// True when this airframe is a tiltwing — a rotary pilot type, but with wings and swivelling
    /// ducts that the game's own <c>SwivelDuctSystem.Awake</c> parks FORWARD (customAxis1 = 1), and
    /// that an AI-flown airframe never auto-swivels (its <c>CheckForManualInput</c> drops to Manual
    /// when <c>aircraft.Player</c> is null). It therefore enters wing-borne, and must enter at a
    /// wing's speed (fix, 2026-09-16: every VL-49 Tarantula started airborne as a helicopter, 300 m
    /// and 40 m/s, stalled and was "lost after 30 s … 0.4 km from Sandrift Airbase at 5 m").
    /// </summary>
    internal static bool IsTiltwingAirframe(AircraftDefinition definition)
    {
        Aircraft? prefab = definition.unitPrefab != null ? definition.unitPrefab.GetComponent<Aircraft>() : null;
        if (prefab?.pilots == null)
        {
            return false;
        }

        for (int i = 0; i < prefab.pilots.Length; i++)
        {
            if (prefab.pilots[i] != null && prefab.pilots[i].pilotType == Pilot.PilotType.Tiltwing)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// True when the Basegame AI has a flight model for this airframe at all.
    /// <c>Pilot.SetStartingAiState</c> hands <c>PilotType.Plane</c> to <c>AIPilotCombatModes</c> and
    /// <c>Helo</c>/<c>Tiltwing</c> to <c>AIHeloCombatState</c>, and gives <c>PilotType.VTOL</c>
    /// **nothing at all** — a VTOL with an AI pilot has no state, no autopilot input and falls out
    /// of the sky. Nothing the mod can do fixes that, so nothing may buy one.
    /// </summary>
    internal static bool CanAiFly(AircraftDefinition definition)
    {
        Aircraft? prefab = definition.unitPrefab != null ? definition.unitPrefab.GetComponent<Aircraft>() : null;
        if (prefab?.pilots == null)
        {
            return false;
        }

        for (int i = 0; i < prefab.pilots.Length; i++)
        {
            Pilot? pilot = prefab.pilots[i];
            if (pilot != null
                && (pilot.pilotType == Pilot.PilotType.Plane
                    || pilot.pilotType == Pilot.PilotType.Helo
                    || pilot.pilotType == Pilot.PilotType.Tiltwing))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Whether <paramref name="airbase"/> is one this faction holds whose hangars carry
    /// <paramref name="definition"/> in their authored list. Internal (one-word widening, Reuse rule
    /// 4): this is THE airbase acceptance test — the AIR window's launch gate and the AI buyers'
    /// candidate gate read the same answer, so a commander can never be refused an aircraft the
    /// player's own window launches from the same strip.</summary>
    internal static bool IsCompatibleAirbase(Airbase? airbase, FactionHQ hq, AircraftDefinition definition)
    {
        if (airbase == null || airbase.disabled || !airbase.GetAvailableAircraft().Contains(definition))
        {
            return false;
        }

        foreach (Airbase ownedAirbase in hq.GetAirbases())
        {
            if (ReferenceEquals(ownedAirbase, airbase))
            {
                return true;
            }
        }

        return false;
    }

    private static float GetMissionRadius(AirCommandMode mode)
    {
        return mode switch
        {
            AirCommandMode.Cas => CommanderSettings.CasRadiusKm * 1000f,
            AirCommandMode.AirGuard => CommanderSettings.AirGuardRadiusKm * 1000f,
            AirCommandMode.Arad => CommanderSettings.AradRadiusKm * 1000f,
            AirCommandMode.StrategicStrike => CommanderSettings.StrikeRadiusKm * 1000f,
            _ => CommanderSettings.AwacsRadiusKm * 1000f,
        };
    }

    private static void SetMissionRadius(AirCommandMode mode, float radiusKm)
    {
        switch (mode)
        {
            case AirCommandMode.Cas: CommanderSettings.CasRadiusKm = radiusKm; break;
            case AirCommandMode.AirGuard: CommanderSettings.AirGuardRadiusKm = radiusKm; break;
            case AirCommandMode.Arad: CommanderSettings.AradRadiusKm = radiusKm; break;
            case AirCommandMode.StrategicStrike: CommanderSettings.StrikeRadiusKm = radiusKm; break;
            default: CommanderSettings.AwacsRadiusKm = radiusKm; break;
        }
    }
}
