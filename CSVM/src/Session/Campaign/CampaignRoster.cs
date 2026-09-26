using System;
using System.Collections.Generic;
using CSVM.Flight.Ai;
using CSVM.Flight.Weapons;
using CSVM.Mech3;
using CSVM.Session.InstantAction;
using CSVM.Session.Roster;
using Godot;

namespace CSVM.Session.Campaign;

/// <summary>What one generator launch builds; see
/// <see cref="CampaignRosterPlan.ResolveGeneratorLaunch"/>.</summary>
public enum GeneratorLaunch
{
    /// <summary>The roster block the <c>vehicle.params</c> label names.</summary>
    Template,

    /// <summary>The CLI airframe: the generator authors no label.</summary>
    Airframe,

    /// <summary>Nothing: the label names no block, and the launch is counted all the same.</summary>
    Empty,

    /// <summary>A surface vehicle: the block names a <c>mode ship</c> def, which has no airframe
    /// and is built by the surface-vehicle runtime, never by the aircraft spawner.</summary>
    Surface,
}

/// <summary>One campaign roster block, planned: everything the placement step needs, resolved
/// from the block's fields, the vehicle def table and the chapter's nets, and nothing read from
/// the engine. <see cref="Escorts"/> and <see cref="Net"/> are the decoded fork and are never
/// both set (docs/org/aiPilot.md "A net demotes a wingman").</summary>
public sealed class RosterSpawnPlan
{
    public required string Name { get; init; }

    /// <summary>The vehicle def the block resolves to (<c>blakepeace_2</c> for
    /// <c>blakepeace_2_1</c>).</summary>
    public required string Def { get; init; }

    /// <summary>The player airframe node the model is built from; on a <see cref="Surface"/>
    /// plan the def's own name, which is the chapter's library-root model of the hull.</summary>
    public required string PlaneNode { get; init; }

    /// <summary>A <c>mode ship</c> block: no airframe, no pilot. Built and driven by
    /// <see cref="World.SurfaceVehicleRuntime"/>; <see cref="CampaignRosterPlan.SpawnFor"/> refuses it.</summary>
    public bool Surface { get; init; }

    /// <summary>The AI def the stats resolve down, or null when <see cref="Def"/> does not derive
    /// from the airframe's base def and the plain base def flies instead.</summary>
    public string? AiDef { get; init; }

    /// <summary>The def the livery comes from when <see cref="AiDef"/> is null: the block's own
    /// <see cref="Def"/>, whose paint slots the original's spawn reads whatever airframe flies it
    /// (docs/org/paint.md). Null when the AI def resolved, since it carries the livery, and on a
    /// hull.</summary>
    public string? LiveryDef => Surface || AiDef != null ? null : Def;

    /// <summary>The resolved def's <c>mode</c>.</summary>
    public required string Mode { get; init; }

    public Vector3 Position { get; init; }

    public float YawDeg { get; init; }

    public int? Team { get; init; }

    public int Group { get; init; }

    /// <summary>The block's <c>deactivated</c> flag: built inert, waiting for
    /// <c>WAKEUP_ENEMIES</c> or a generator launch.</summary>
    public bool Inert { get; init; }

    /// <summary>The patrol net the block authored and the chapter carries, or null.</summary>
    public AiNet? Net { get; init; }

    /// <summary>The net id the block authored that the chapter does not carry, or null.</summary>
    public int? MissingNetId { get; init; }

    /// <summary>The decoded fork: <c>mode wingman</c> with no authored net. The pilot flies
    /// <see cref="AiEscort"/> on <see cref="LeaderName"/> and no patrol net.</summary>
    public bool Escorts { get; init; }

    /// <summary>The <c>primary_target</c> name: the formation leader of an escorting block, the
    /// gunner's assignment on any other; null when unset.</summary>
    public string? LeaderName { get; init; }

    /// <summary>The volumes in the decoded order: the net's non-zero values, the block's own
    /// over them. The <c>min_ai_active_dist</c> floor is applied at placement.</summary>
    public AiVolumeSet Volumes { get; init; }

    public AiSkillVector Skills { get; init; }

    public long SignatureMask { get; init; }

    public IReadOnlyList<AiRatingBias> Biases { get; init; } = Array.Empty<AiRatingBias>();

    public int? AccentId { get; init; }

    public bool Nitro { get; init; }

    /// <summary>The roster's own <c>ace</c> flag (slot 67): the block a mission script singles
    /// out. Its skill ratings are exempt from the difficulty offset
    /// (<see cref="CSVM.Flight.Hangar.Difficulty.SkillRatingForSpawn"/>) and its kill goes to the
    /// debrief's starred tally (docs/org/debrief.md#what-the-tallies-count).</summary>
    public bool Ace { get; init; }

    /// <summary>The block's <c>init_health</c> (slot 7): the whole-vehicle health-pool override,
    /// or null when the slot is unset, <c>0.0</c> or absent (docs/org/vehicleDamage.md "Where the
    /// numbers come from at spawn").</summary>
    public float? InitHealth { get; init; }

    /// <summary>The block's <c>armor</c> (slot 66): the whole-vehicle armour-pool override, or
    /// null on <c>-1</c> or a short block. ⚠ A missing slot is not a zero: 33 of 414 shipped
    /// blocks stop before this one and must read null here, never <c>0.0</c>.</summary>
    public float? Armor { get; init; }

    public string? TaxiPath { get; init; }

    public float? PrefEngageAlt { get; init; }

    public string? Title { get; init; }

    /// <summary>The block's own <c>objectiveTarget</c> flag (slot 37): true when this block's
    /// aircraft itself carries the mission's objective marker, gated together with
    /// <see cref="HelpLabel"/> (docs/formats/ai-rosters.md "Field table").</summary>
    public bool ObjectiveTarget { get; init; }

    /// <summary>The block's own <c>helpLabel</c> (slot 39) raw: the MSG_OBJ_* key its marker
    /// carries when <see cref="ObjectiveTarget"/> is set, or designer text otherwise (C4/M05's
    /// <c>blakepeace_3_1</c>/<c>_2</c> author <c>"Blake Aviation"</c> with the flag unset), gate
    /// on <see cref="ObjectiveTarget"/> before treating this as a message key.</summary>
    public string? HelpLabel { get; init; }

    /// <summary>The block's own <c>categoryLabel</c> (slot 38) raw: the MSG_* key the label half of
    /// its marker's line 1 prints, beside <see cref="HelpLabel"/>'s category half.</summary>
    public string? CategoryLabel { get; init; }

    /// <summary>The menu-chosen loadout laid over the stock table, the campaign wingman's only.</summary>
    public LoadoutChoice? Fit { get; init; }

    /// <summary>The paint this block is built in, overriding whatever its own def and team would
    /// resolve. Set only for the airframe hand-over's <c>wingman_4</c>, which wears the player's
    /// own livery because it is about to be handed the player's own aeroplane.</summary>
    public PaintScheme? Scheme { get; init; }

    /// <summary>The spawn nose, from the block's yaw in the mission-data convention.</summary>
    public Vector3 Forward => new Basis(Vector3.Up, Mathf.DegToRad(YawDeg)) * Vector3.Forward;
}

/// <summary>
/// The engine-free planning step of the campaign roster spawner: every block of a mission's
/// <c>aiv</c> roster resolved to a <see cref="RosterSpawnPlan"/>, with the decoded net-versus-
/// escort fork applied and the volumes merged in the original's order. The placement half is
/// <c>CampaignDirector.BuildRoster</c>. A <c>mode ship</c> block plans as a surface vehicle; the
/// player's own block and any other block with no airframe are reported in <see cref="Skipped"/>.
/// </summary>
public sealed class CampaignRosterPlan
{
    /// <summary>The block name the roster gives the human player's aircraft.</summary>
    public const string PlayerBlock = "player";

    /// <summary>Activation radius, metres, that an awake <c>DEDG</c> raises every live member of
    /// the watched group to each tick (<c>FUN_00465850</c>, docs/formats/objectives.md's DEDG row).
    /// Stored squared there, as 8.1e7.</summary>
    public const float DedgActivationRangeM = 9000f;

    private CampaignRosterPlan()
    {
    }

    public IReadOnlyList<RosterSpawnPlan> Spawns { get; private set; } = Array.Empty<RosterSpawnPlan>();

    /// <summary>Blocks that plan to nothing, with the reason.</summary>
    public IReadOnlyList<(string Name, string Why)> Skipped { get; private set; } = Array.Empty<(string, string)>();

    /// <summary>Plans the roster. <paramref name="wingmanNode"/> is the profile's wingman
    /// airframe for the block named <paramref name="wingmanName"/>, or null to fly the block's
    /// own def; <paramref name="netDraw"/> is the <c>rand() % count</c> draw a multi-entry
    /// <c>netids</c> takes, given the count. <paramref name="handover"/> is the player's own
    /// airframe and paint, which <see cref="AirframeHandover.WingmanName"/> flies in the two
    /// missions that resolve it; null everywhere else.</summary>
    public static CampaignRosterPlan Build(
        IReadOnlyList<(string Name, List<object?> Fields)> blocks,
        VehicleDefs defs,
        IReadOnlyList<AiNet> nets,
        string? wingmanNode = null,
        LoadoutChoice? wingmanFit = null,
        string wingmanName = CampaignDirector.WingmanName,
        Func<int, int>? netDraw = null,
        bool includeDisabled = false,
        FlyingAirframe? handover = null)
    {
        var spawns = new List<RosterSpawnPlan>();
        var skipped = new List<(string, string)>();
        foreach (var (name, fields) in blocks)
        {
            if (!includeDisabled && !AiSkills.RosterEnabled(fields))
            {
                skipped.Add((name, "enabled 0 generator template"));
                continue;
            }
            if (name.Equals(PlayerBlock, StringComparison.OrdinalIgnoreCase))
            {
                skipped.Add((name, "the human player's own block"));
                continue;
            }
            if (defs.DefForBlock(name) is not { } def)
            {
                skipped.Add((name, "no vehicle def matches the block name"));
                continue;
            }
            if (AiSkills.RosterSpawnPose(fields) is not { } pose)
            {
                skipped.Add((name, "no spawn position authored"));
                continue;
            }

            // The airframe: the profile's plane for the named wingman when the cabin bound one,
            // the block's own def otherwise. Either way the AI def must derive from the airframe's
            // base def or PlaneStats cannot resolve it, and the plain base def flies instead.
            string? planeNode = null;
            string? baseDef = null;
            if (wingmanNode != null && name.Equals(wingmanName, StringComparison.OrdinalIgnoreCase))
            {
                planeNode = wingmanNode;
                baseDef = defs.BaseDefForPlayerNode(wingmanNode);
            }
            // The hand-over block flies the player's own aeroplane from mission start, because the
            // swap is about to give it that aeroplane. The original decides the same thing at the
            // same point (docs/formats/anim-definitions/cutscenes.md, step 5).
            else if (handover is { } handed
                     && name.Equals(AirframeHandover.WingmanName, StringComparison.OrdinalIgnoreCase))
            {
                planeNode = handed.PlaneNode;
                baseDef = defs.BaseDefForPlayerNode(handed.PlaneNode);
            }
            string mode = defs.ModeOf(def) ?? VehicleDefs.JetMode;
            bool surface = false;
            if (planeNode == null || baseDef == null)
            {
                if (defs.AirframeFor(def) is { } airframe)
                {
                    (planeNode, baseDef) = airframe;
                }
                else if (mode.Equals(VehicleDefs.ShipMode, StringComparison.OrdinalIgnoreCase))
                {
                    // A hull, not an aeroplane: the model is the def's own library root and
                    // nothing here derives from a player airframe.
                    surface = true;
                    planeNode = def;
                    baseDef = def;
                }
                else
                {
                    skipped.Add((name, $"'{def}' ({mode}) has no player airframe"));
                    continue;
                }
            }
            // The block's own def decides the mode. The AI def only carries stats and livery: the
            // profile's airframe takes its w<plane> twin, a def that is no variant of its airframe
            // takes the plain base def.
            string? aiDef = surface ? null
                : defs.DerivesFrom(def, baseDef) ? def
                : planeNode == wingmanNode && defs.Has("w" + baseDef) && defs.DerivesFrom("w" + baseDef, baseDef)
                    ? "w" + baseDef
                    : null;

            var netIds = AiSkills.RosterNetIds(fields);
            AiNet? net = null;
            int? missingNet = null;
            if (netIds.Count > 0)
            {
                int pick = netIds.Count == 1 ? 0 : (netDraw?.Invoke(netIds.Count) ?? 0) % netIds.Count;
                net = AiNets.ById(nets, netIds[pick]);
                if (net is null or { Nodes.Count: 0 })
                {
                    missingNet = netIds[pick];
                    net = null;
                }
            }
            // The decoded fork: a netless wingman escorts; ANY authored net demotes it to a jet
            // that walks the graph, so a block never carries both.
            bool escorts = netIds.Count == 0
                && mode.Equals(VehicleDefs.WingmanMode, StringComparison.OrdinalIgnoreCase);
            if (netIds.Count == 0 && !escorts && AiSkills.RosterEnabled(fields))
            {
                // Built dead in the original (docs/org/aiPilot.md): the dead byte is set before net
                // assignment, and only a resolved net or the wingman mode clears it. A generator
                // template is not gated, since its launch flies the generator's own net.
                skipped.Add((name, $"'{def}' ({mode}) authors no net and is no wingman: the original builds it dead"));
                continue;
            }

            spawns.Add(new RosterSpawnPlan
            {
                Name = name,
                Def = def,
                PlaneNode = planeNode,
                Surface = surface,
                AiDef = aiDef,
                Mode = mode,
                Position = pose.Position,
                YawDeg = pose.YawDeg,
                Team = AiSkills.RosterTeam(fields),
                Group = AiSkills.RosterGroup(fields),
                Inert = AiSkills.RosterDeactivated(fields),
                Net = net,
                MissingNetId = missingNet,
                Escorts = escorts,
                LeaderName = AiSkills.RosterPrimaryTarget(fields),
                Volumes = (net?.Volumes ?? AiVolumeSet.None).Overlaid(AiVolumeSet.FromRosterSlots(fields)),
                Skills = AiSkills.RosterSkills(fields),
                SignatureMask = AiSkills.RosterSignatureMask(fields),
                Biases = AiSkills.RosterRatingBiases(fields),
                AccentId = AiSkills.RosterAccentId(fields),
                Nitro = AiSkills.RosterNitro(fields),
                Ace = AiSkills.RosterAce(fields),
                InitHealth = AiSkills.RosterInitHealth(fields),
                Armor = AiSkills.RosterArmor(fields),
                TaxiPath = AiSkills.RosterTaxiPath(fields),
                PrefEngageAlt = AiSkills.RosterPrefEngageAlt(fields),
                Title = AiSkills.RosterTitle(fields),
                ObjectiveTarget = AiSkills.RosterObjectiveTarget(fields),
                HelpLabel = AiSkills.RosterHelpLabel(fields),
                CategoryLabel = AiSkills.RosterCategoryLabel(fields),
                Fit = planeNode == wingmanNode ? wingmanFit : null,
                Scheme = handover is { } paint
                         && name.Equals(AirframeHandover.WingmanName, StringComparison.OrdinalIgnoreCase)
                    ? paint.Scheme
                    : null,
            });
        }
        return new CampaignRosterPlan { Spawns = spawns, Skipped = skipped };
    }

    /// <summary>Plans one authored-disabled generator parameter block. Enabled roster members are
    /// rejected here because they belong to the initial mission roster instead.</summary>
    public static RosterSpawnPlan? BuildGeneratorTemplate(string name, List<object?> fields,
        VehicleDefs defs, IReadOnlyList<AiNet> nets)
    {
        if (AiSkills.RosterEnabled(fields))
            return null;
        var plan = Build(new[] { (name, fields) }, defs, nets, includeDisabled: true);
        return plan.Spawns.Count == 1 ? plan.Spawns[0] : null;
    }

    /// <summary>Writes a plan's volumes onto a spawned pilot's range gates: each authored
    /// radius over the def's, then the activation radius floored to
    /// <paramref name="minAiActiveDist"/> (docs/org/aiPilot.md "No activation volume is smaller
    /// than min_ai_active_dist"). The altitude bands have no consumer yet and are not applied.
    /// A null machine is a pilot the spawner armed nothing on.</summary>
    public static void ApplyVolumes(AiModeMachine? machine, AiVolumeSet volumes, float minAiActiveDist)
    {
        if (machine == null)
            return;
        if (volumes.Activation.Radius != 0f)
            machine.ActivationRange = volumes.Activation.Radius;
        if (volumes.Attack.Radius != 0f)
            machine.AttackRange = volumes.Attack.Radius;
        if (volumes.Return.Radius != 0f)
            machine.ReturnRange = volumes.Return.Radius;
        machine.ActivationRange = Mathf.Max(machine.ActivationRange, minAiActiveDist);
    }

    /// <summary>What an awake <c>DEDG</c> does to one live member of the group it watches: the
    /// activation radius is raised to <see cref="DedgActivationRangeM"/> and never lowered, so a
    /// watched member keeps engaging instead of dropping back to patrol out at the 2,000 m floor.
    /// The altitude bands the same write widens have no consumer, as in
    /// <see cref="ApplyVolumes"/>.</summary>
    public static void WidenForDedg(AiModeMachine? machine)
    {
        if (machine == null)
            return;
        machine.ActivationRange = Mathf.Max(machine.ActivationRange, DedgActivationRangeM);
    }

    /// <summary>The spawn record a planned block launches as: its representative rating arms the
    /// gunner and machine, its authored slots outrank the def's, its objective-marker slots ride
    /// onto the aeroplane, and it wears the livery its <see cref="RosterSpawnPlan.AiDef"/>, else
    /// its <see cref="RosterSpawnPlan.LiveryDef"/>, authors whatever <c>ShippedSkins</c> says. ⚠ Leave <paramref name="nodeName"/> unset
    /// outside a generator launch: <c>primary_target</c> and <c>rating_biases</c> are authored
    /// against the block's own name. ⚠ Refuses a Surface plan: a hull has no airframe.</summary>
    public static AiSpawn SpawnFor(RosterSpawnPlan plan, Vector3 pos, Vector3 lookAt, AiPilot pilot,
        string? nodeName = null) =>
        plan.Surface
            ? throw new ArgumentException($"'{plan.Name}' is a surface vehicle, not an aircraft", nameof(plan))
            : new(plan.PlaneNode, pos, lookAt, pilot, Scheme: plan.Scheme, Team: plan.Team,
            Inert: plan.Inert, ShippedSkins: plan.Team != AimAssist.PlayerTeam,
            AiDef: plan.AiDef, LiveryDef: plan.LiveryDef, Fit: plan.Fit,
            AttackRating: InstantActionRuntime.RepresentativeRating(plan.Skills),
            Nitro: plan.Nitro, RosterSkills: plan.Skills, NodeName: nodeName ?? plan.Name,
            PilotName: plan.Title, InitHealth: plan.InitHealth, Armor: plan.Armor, Ace: plan.Ace,
            ObjectiveMarker: plan.ObjectiveTarget,
            ObjectiveTypeLabel: plan.CategoryLabel, ObjectiveCategory: plan.HelpLabel);

    /// <summary>The generator parameter blocks of one mission, keyed by the
    /// <c>vehicle.params</c> label a generator names. Mission data, so a launch resolves its block
    /// whether or not a campaign profile is flying: without this the generated aircraft falls back
    /// to a CLI airframe and carries none of the block's fields.</summary>
    public static Dictionary<string, RosterSpawnPlan> GeneratorTemplates(
        string missionZrdrPath, VehicleDefs defs, IReadOnlyList<AiNet> nets)
    {
        var templates = new Dictionary<string, RosterSpawnPlan>(StringComparer.OrdinalIgnoreCase);
        foreach (var (parameter, name, fields) in AiSkills.LoadGeneratorRoster(missionZrdrPath))
        {
            if (BuildGeneratorTemplate(name, fields, defs, nets) is { } plan)
            {
                templates[parameter] = plan;
            }
        }
        return templates;
    }

    /// <summary>What a generator launch builds from its <c>vehicle.params</c> label: the block
    /// the label names (a surface vehicle when that block is a hull), the CLI airframe when no
    /// label is authored, or nothing when the label names no block. ⚠ The caller counts an empty launch rather than replacing it: the decoded
    /// launch reports one with nothing built (docs/formats/mission-entities/enemy-generators.md,
    /// "shipped typo"; C1/M04's <c>Eairg32_params</c>, and the data stays as shipped).</summary>
    public static GeneratorLaunch ResolveGeneratorLaunch(
        IReadOnlyDictionary<string, RosterSpawnPlan> templates, string? parameter,
        out RosterSpawnPlan? plan)
    {
        ArgumentNullException.ThrowIfNull(templates);
        plan = null;
        if (parameter == null)
            return GeneratorLaunch.Airframe;
        if (!templates.TryGetValue(parameter, out plan))
            return GeneratorLaunch.Empty;
        return plan.Surface ? GeneratorLaunch.Surface : GeneratorLaunch.Template;
    }

    /// <summary>Writes the plan's remaining pilot slots onto a spawned pilot: the volumes
    /// (slots 8 to 19) under the <paramref name="minAiActiveDist"/> floor, the signature
    /// maneuvers (slot 32), the gunner's rating biases (slot 33) and its assignment (slot 6).
    /// Called after the spawn, because the assembler is what arms the machine and the gunner.
    /// ⚠ An escorting block's <c>primary_target</c> names its leader rather than a target, so the
    /// gunner keeps its own ranking there and the caller's leader pass wires the escort.</summary>
    public static void ApplyPlan(AiPilot pilot, RosterSpawnPlan plan, float minAiActiveDist)
    {
        ArgumentNullException.ThrowIfNull(pilot);
        ArgumentNullException.ThrowIfNull(plan);
        if (pilot.Machine is { } machine)
        {
            ApplyVolumes(machine, plan.Volumes, minAiActiveDist);
            if (plan.SignatureMask != 0)
            {
                machine.SignatureManeuvers = Maneuvers.SignatureNames(plan.SignatureMask);
            }
        }
        if (pilot.Gunner is { } gunner)
        {
            if (plan.Biases.Count > 0)
            {
                gunner.RatingBiases = plan.Biases;
            }
            if (!plan.Escorts)
            {
                gunner.PrimaryTargetName = plan.LeaderName;
            }
        }
    }

    /// <summary>The formation leader a plan's <see cref="RosterSpawnPlan.LeaderName"/> names,
    /// out of the spawned rigs: the literal <c>player</c> is the first human, anything else a
    /// spawned block by name. Null when unset or not spawned (a dead leader is the pilot's own
    /// fallback, not this lookup's).</summary>
    public static TRig? ResolveLeader<TRig>(string? leaderName,
        IReadOnlyDictionary<string, TRig> rigsByName, TRig? player)
        where TRig : class
    {
        if (leaderName == null)
            return null;
        if (leaderName.Equals(PlayerBlock, StringComparison.OrdinalIgnoreCase))
            return player;
        return rigsByName.TryGetValue(leaderName, out var rig) ? rig : null;
    }
}
