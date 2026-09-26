using System;
using System.Collections.Generic;
using CSVM.Flight;
using CSVM.Mech3;
using CSVM.UI;
using CSVM.Utils;
using Godot;

namespace CSVM.Session;

/// <summary>The airframe and paint one human rig is flying, as its own assembly resolved them.
/// Read by the mission-script airframe hand-over, which gives the aeroplane the player is leaving
/// to another pilot and needs both to build it again.</summary>
public readonly record struct FlyingAirframe(string PlaneNode, PaintScheme? Scheme);

/// <summary>Assembles one player's flight rig: the painted plane model, the
/// <see cref="FlightController"/> and everything hung on it, loadout/ordnance, compass, gauges,
/// HUD readout/reticle, damage visuals, audio, this player's stunt run, the spawn placement, and
/// the crash runtime built once the controller is in the tree.
///
/// Constructed once per session with the session-wide flight data (the
/// planes gamez, stats cache, pads, paint rng, spawn list, weapons/loadouts, HUD assets, the
/// shared projectile pool), then <see cref="Assemble"/>d once per rig, in player order.
/// ⚠ <b>Player order is load-bearing:</b> the paint rng and the spawn index wrap are shared
/// streams, so P1..P4 must draw in ascending order or every livery and spawn changes.</summary>
internal sealed class HumanFlightAdapter
{
    private readonly FlightRosterPolicy _policy;
    private readonly LiveryResolver _liveries;
    private readonly IFlightStarts _spawns;
    private readonly WorldEffectsFactory _worldEffects;
    private readonly Node3D _worldRoot;
    private readonly AircraftAssemblyResources _aircraft;
    private readonly FlightWorldBindings _world;
    private readonly HumanRosterBindings _human;

    // What each rig is flying now, by rig index: a swap replaces the entry. The mission-script
    // hand-over gives wingman_4 the aeroplane the player is LEAVING, and nothing else records it.
    private readonly Dictionary<int, FlyingAirframe> _flying = new();

    // Every player's start, resolved in one call (see Assemble).
    private IReadOnlyList<FlightStart>? _starts;

    public HumanFlightAdapter(FlightRosterPolicy policy, LiveryResolver liveries, IFlightStarts spawns,
        WorldEffectsFactory worldEffects, Node3D worldRoot, AircraftAssemblyResources aircraft,
        FlightWorldBindings world, HumanRosterBindings human)
    {
        _policy = policy;
        _liveries = liveries;
        _spawns = spawns;
        _worldEffects = worldEffects;
        _worldRoot = worldRoot;
        _aircraft = aircraft;
        _world = world;
        _human = human;
    }

    /// <summary>Mesh instances the assembled planes added, accumulated across the rigs, the
    /// caller folds this into the build's count.</summary>
    public int MeshInstances { get; private set; }

    /// <summary>What the assembled rigs add to the build summary line (the stunt zone count).
    /// Accumulated here so the caller can append it to its own summary.</summary>
    public string WhatSuffix { get; private set; } = "";

    /// <summary>Phase 1 of the damage-visuals setup: the object itself, from the built model and
    /// the airframe's own stats. Both spawners call this unconditionally, before any crash runtime
    /// exists, because a rig without a crash program still flips torn panels through a null sink
    /// (see <see cref="DamageVisuals.DamageEffectSink"/>). Phase 2, the sink and the stops, is
    /// wired by <see cref="WorldEffectsFactory.BuildFlightCrashRuntime"/>: only that half needs a
    /// live runtime, and folding this half in with it would leave the parked damage lab blank.</summary>
    public static DamageVisuals BuildDamageVisuals(PlaneBuilder planeBuilder, Node3D planeModel,
        PlaneStats stats, AnimProgram? crashProgram)
    {
        PanelPairing? pairing = null;
        if (crashProgram is { } program)
        {
            var pairingDefs = new List<AnimDefinition>(program.ByAnimName("plane_reset"));
            foreach (var n in EffectCatalogue.PlayerDamageStageAnims)
                pairingDefs.AddRange(program.ByAnimName(n));
            pairing = DamageVisuals.PanelPairingSets(pairingDefs);
        }
        return new DamageVisuals(planeBuilder.DamagePanels, planeModel, stats, defPairing: pairing,
            cockpitPanels: planeBuilder.CockpitDamagePanels);
    }

    /// <summary>What rig <paramref name="rigIndex"/> is flying, or null before its first assembly.
    /// </summary>
    public FlyingAirframe? Flying(int rigIndex) =>
        _flying.TryGetValue(rigIndex, out var flying) ? flying : null;

    /// <summary>Builds player <paramref name="pi"/>'s aircraft into <paramref name="rig"/> and
    /// adds it to the session world. Call once per rig in ascending player order (see the class
    /// note on the shared rng streams). <paramref name="swap"/> is a mid-mission airframe swap
    /// rather than the initial build: it names the airframe outright and carries the flight state
    /// of the aircraft being left, and it is the caller's job to have removed that aircraft first.
    /// </summary>
    public void Assemble(int pi, PlayerRig rig, Action<FlightController> onCreated,
        AirframeSwapRequest? swap = null)
    {
        bool verbose = pi == 0; // the per-plane detail lines are identical for every player
        string tag = _human.RigCount > 1 ? $"P{pi + 1} " : "";
        // A seat flown on another machine. No pane exists for it, so everything hung on a camera,
        // a pad or this window is skipped. The aeroplane, its paint, its loadout, its damage and
        // its spawn are built exactly as a local seat's.
        var seat = pi < _human.NetSeats.Count ? _human.NetSeats[pi] : null;
        bool remote = seat is { IsLocal: false };
        // Each player flies their own pick; an Instant Action mission overrides it for every human
        // alike (PlaneRoster.InstantActionOverride settles which do). A mission's own swap outranks
        // both. In a network match the roster's pick comes first: every peer builds the same field.
        string planeName = swap?.PlaneNode
            ?? (seat is { PlaneNode.Length: > 0 } ? seat.PlaneNode : null)
            ?? _human.InstantActionPlayerPlaneNode
            ?? (_policy.PlaneNames.Count == 0
                ? _policy.PlaneName
                : _policy.PlaneNames[Math.Min(pi, _policy.PlaneNames.Count - 1)]);
        var stats = _aircraft.StatsFor(planeName);

        // The plane this pilot BUILT, when they picked one: its guns, pylons, paint and armour
        // replace the airframe's stock ones below. Null on a stock pick, empty outside a
        // launchscreen launch; a swap brings its own or none (see AirframeSwapRequest).
        var custom = swap != null ? swap.Build : CustomPlaneFor(pi);
        string planeDisplay = custom?.Name is { Length: > 0 } customName
            ? customName
            : PlaneRoster.PlaneDisplayName(stats);

        // The engine pick, the original's registry override: the authored engines.json tier row
        // replaces the airframe's stock EnginePower on a COPY (the stats object is the shared
        // per-airframe cache). Stock pick (id 6) keeps the airframe's own row.
        if (custom != null
            && Flight.CustomPlaneBuild.EnginePowerFor(_aircraft.ZrdrPath, custom) is { } enginePower)
        {
            stats = stats.WithEnginePower(enginePower);
        }

        // Every player flies the Fortune Hunters livery unless --paint says otherwise, as the
        // original's stock planes do; a custom plane wears the paint it was built with instead.
        // A swap's ShippedSkins reading wins over all three, null included (below).
        long mark = StartupProfile.Mark();
        // cockpitInterior: a human rig is the only one whose pilot can look out of a cockpit
        // FlightRoster's AI builder deliberately does not ask for one.
        var scheme = swap is { ShippedSkins: true } capturedSwap
            ? capturedSwap.Scheme
            : swap?.Scheme
                ?? (custom != null && !_liveries.PaintRequested
                    ? Flight.CustomPlaneBuild.PaintFor(custom, UI.HangarPaintPage.PatternName(custom.PaintPattern))
                    : _liveries.SchemeFor(pi, _aircraft.ZrdrPath, _aircraft.PaintRng,
                        _liveries.PatternsForPlane(_aircraft.PlanesGamez, planeName)));
        _flying[pi] = new FlyingAirframe(planeName, scheme);
        var planeBuilder = new PlaneBuilder(_aircraft.PlanesGamez, _aircraft.Textures, spinningProps: true,
            scheme: scheme, patterns: _liveries.Patterns, cockpitInterior: true, dockingHook: true);
        var planeModel = planeBuilder.Build(planeName);
        StartupProfile.Record("plane", mark);
        MeshInstances += planeBuilder.MeshInstanceCount;

        var controller = new FlightController
        {
            DebugCollision = _world.DebugCollision,
            PinnedView = _policy.View,
            PinnedViewMode = _policy.ViewMode,
            AutoHeadTurn = _policy.AutoHeadTurn,
            PinnedLook = _policy.PinnedLook,
            HudParent = remote ? null : rig.Viewport,
            // Null when the airframe ships no cockpit1, the rig then hides nothing, as before B11.
            Cockpit = CockpitVisibility.Bind(planeModel, planeBuilder.CockpitInterior),
            CockpitInterior = planeBuilder.CockpitInterior,
            CockpitPanel = CockpitGauges.Bind(planeBuilder),
            Scheme = scheme,
            Painter = planeBuilder.Painter,
            ShippedSkins = swap is { ShippedSkins: true },
        };
        if (verbose && controller.Cockpit != null)
            Log.Info("flight", $"cockpit: '{planeName}' interior built hidden at the cockpit_camera marker");
        // The engine pick's nitrous bit (ids 3-5) installs the injector, the original's veh+0x946.
        controller.Nitro.Installed = custom != null && Flight.CustomPlaneBuild.HasNitrous(custom);
        // Every human joins team 1 in an Instant Action mission, splitscreen included, the
        // per-pilot team fallback would otherwise collide with an enemy's. --coop asks the same
        // in plain flight; SessionSpec.Resolve already drops Coop when --vs is set.
        controller.Bind(new FlightControllerBuild
        {
            PlayerIndex = pi,
            IsHumanPiloted = true,
            // A seat flown elsewhere takes its pose out of this history, not a flight model.
            // The buffer's presence IS that ownership, so it is built here and nowhere else.
            // The session fills it from the samples that seat's owner sends.
            RemotePoses = remote ? new Net.RemotePoseBuffer() : null,
            // one scripted sequence per player ('|'-separated); the last covers the rest
            HoldSegments = _policy.HoldSets == null ? null
                : _policy.HoldSets[Math.Min(pi, _policy.HoldSets.Length - 1)],
            PlaneModel = planeModel,
            Props = PropAnimator.Build(planeModel),
            WingLights = WingLightBlinker.Build(planeBuilder.WingFlares, _policy.AnimLod),
            Surfaces = ControlSurfaceAnimator.Build(planeModel),
            Collider = PlaneCollider.Build(planeModel),
            // A custom plane's four bought zones stand in for the airframe's stock ARMOUR pools,
            // on those pools' own scale; structure stays the def's and the vehicle totals stay
            // the sum over zones. Nothing scales a human rig's pools, so the player never is.
            Damage = stats.DestroyableParts.Count == 0 ? null
                : custom != null ? Flight.CustomPlaneBuild.DamageFor(stats, custom)
                : PlaneDamage.For(stats),
            CollideDamageSink = _world.WorldRuntime != null ? _world.WorldRuntime.CollideDamageAt : null,
            GrazeEffectSink = _world.WorldEffects is { } fx ? (name, pt) => fx.PlayEffectAt(name, pt) : null,
            TouchdownDefs = _world.TouchdownDefs,
            Projectiles = _world.Projectiles,
            HumanPositions = _world.HumanPositions,
            // ⚠ Pass the null through. Null and empty are DIFFERENT bindings to Pads.For: null
            // reads every connected pad (what AssignPads returns for one player), empty reads none.
            // Coalescing flew a single player pad-dead; a remote seat takes empty, it reads none.
            PadDevices = remote ? Array.Empty<int>() : _human.PadAssignment?[pi],
            // The keyboard is this machine's first seat's, which on a guest is not seat 0.
            UseKeyboard = !remote && MenuSeatOf(pi) == 0,
            MouseCaptureAllowed = !remote && _policy.MouseCaptureAllowed,
            // The whole messages.json table, not just the weapon rows: the pilot HUD words its
            // auto-land prompt out of the same file.
            Strings = _aircraft.WeaponMessages,
            // A pause key belongs to a seat somebody is sitting at. A remote pilot's pause is
            // their own machine's business, and must not halt this one's simulation.
            AllowPause = !remote,
            Team = _human.InstantActionActive || _human.Coop ? AimAssist.PlayerTeam : null,
            Shake = new PlaneShake(_aircraft.Shakes),
        });
        onCreated(controller);

        // Guns/hardpoints: bind this plane's stock loadout (or the --loadout override) to
        // its built model, resolves markers to muzzle nodes + weapons to WeaponDefs.
        // Set before the controller enters the tree (its _Ready builds the fire state).
        var loadoutDefName = _policy.LoadoutOverride ?? stats.DefName;
        if (_aircraft.StockLoadouts.For(loadoutDefName) is { } stockDef)
        {
            // A custom plane's guns and hardpoints replace the stock ones and the Ammo Selection
            // layer composes over THAT (the built def leaves WeaponId null so a picked ammo still
            // resolves). --loadout= names a def outright, so it takes the whole fit either way.
            var baseDef = custom != null && _policy.LoadoutOverride == null
                ? Flight.CustomPlaneBuild.LoadoutFor(custom, stockDef)
                : stockDef;
            var ldef = MenuFitFor(pi, swap) is { } choice ? choice.ApplyTo(baseDef) : baseDef;
            try
            {
                // The weapon lab flies the FULL-RIG loadout instead: every firepoint and
                // every pylon the airframe carries, seeded from this same stock fit, so the
                // panel can mount a weapon on a hardpoint the stock file never names.
                controller.Loadout = _policy.WeaponLab
                    ? Loadout.ForRig(planeModel, _aircraft.WeaponDefs, ldef)
                    : Loadout.Bind(ldef, planeModel, _aircraft.WeaponDefs);
                // The gun aim assist's structure candidates (B4): the world's
                // destructibles, when this session built a world at all.
                controller.Destructibles = _world.WorldRuntime?.Destructibles;
                // The HUD bracket's and the gun aim assist's vehicle candidates, on the same
                // list the aircraft roster feeds: the mission's surface vehicles, when this
                // session built any.
                controller.SurfaceVehicles = _world.SurfaceVehicles;
                controller.InfiniteAmmo = _policy.InfiniteAmmo;
                controller.AmmoCapOverride = _policy.AmmoCap;
                controller.AutoFire = _policy.AutoFire;
                controller.AutoFireRockets = _policy.AutoFireRockets;
                controller.InitialGunSelect = _policy.GunSelect;
                // --rocket=<wep_id>: swap every hardpoint's ordnance before the model is mounted.
                // A testing hook, all 11 stock loadouts carry HE, so this is the only way to
                // prove the mounted model varies by rocket type.
                if (_policy.RocketOverride != null)
                {
                    Testing.ProbeRunner.ApplyRocketOverride(controller.Loadout, _aircraft.WeaponDefs, _policy.RocketOverride, verbose);
                }
                // Hang the FLYOUT-model ordnance under the pylons, one body per pylon,
                // hidden as its ammo depletes. Uses the same gamez prototype the round flies.
                controller.Ordnance = PylonOrdnance.Build(controller.Loadout, _world.Projectiles, controller.InfiniteAmmo);
                if (verbose)
                {
                    int groups = 0;
                    foreach (var _ in controller.Loadout.FirableGuns) { groups++; }
                    Log.Info("flight", $"weapons: {groups} gun group(s), {controller.Loadout.Hardpoints.Count} hardpoint(s), guns=Space/pad-B rockets=F/pad-A, select guns=G/dpad-R rockets=H/dpad-L{(_policy.GunSelect != 0 ? $" [gun-select={_policy.GunSelect}]" : "")}{(_policy.InfiniteAmmo ? " (infinite ammo)" : "")}{(_policy.AmmoCap != null ? $" (--ammo={_policy.AmmoCap})" : "")}");
                    if (controller.Ordnance is { } ord)
                    {
                        Log.Info("flight", $"pylon ordnance: {ord.Count} mounted rocket model(s){(_policy.RocketOverride != null ? $" (--rocket={_policy.RocketOverride})" : "")}");
                    }
                }
            }
            catch (Exception e)
            {
                GD.PushWarning($"weapons: loadout bind failed for '{loadoutDefName}': {e.Message}");
            }
        }
        else if (verbose)
        {
            Log.Info("flight", $"weapons: no stock loadout for '{loadoutDefName}', unarmed");
        }

        // The carried turret gunners: the vehicle def's thirdp turrets block resolved
        // by TITLE against ai.zrd and by node against this built model. Independent of the
        // stock loadout, the gunner's weapon comes from its ai.zrd row, not from a gun slot.
        if (_aircraft.TurretDefs is { } turretDefs && stats.TurretMounts.Count > 0)
        {
            // ⚠ A human's carried gunner IS positional, unlike the pilot's own forward guns: the
            // original's turret path hands its sound slot a world position whoever owns the mount.
            controller.Turrets = TurretController.BuildCarried(
                turretDefs, stats, planeModel, _aircraft.WeaponDefs, controller, _world.Projectiles,
                new GunVoiceHome(controller, _world.Sounds, _world.SoundDefs, _world.HumanPositions));
            if (verbose && controller.Turrets.Length > 0)
            {
                var descs = new List<string>();
                foreach (var t in controller.Turrets)
                    descs.Add($"{t.Def.Title} ({t.Weapon.Id}, {t.Firepoints.Length} muzzle(s))");
                Log.Info("flight", $"turrets: {string.Join(", ", descs)}");
            }
        }
        if (verbose && controller.Props != null)
            Log.Info("flight", $"props: {controller.Props.Count} spinning blur nodes");
        if (verbose && controller.WingLights != null)
            Log.Info("flight", $"wing lights: {controller.WingLights.Count} blinking flares");
        if (verbose && controller.Surfaces != null)
            Log.Info("flight", $"control surfaces: {controller.Surfaces.Count} deflecting nodes");
        if (controller.Collider != null)
        {
            if (verbose)
                Log.Info("flight", $"plane collider: {controller.Collider.Summary}");
        }
        else
        {
            GD.PushWarning("no airframe collision boxes, falling back to the center ray");
        }
        if (verbose && controller.Damage != null)
        {
            var partDescs = new List<string>();
            foreach (var p in stats.DestroyableParts)
                partDescs.Add(Log.Format($"{p.Name} {p.MaxHp:0}hp{(p.Critical ? "*" : "")}{(p.Engine ? " engine" : "")}"));
            Log.Info("flight", $"damage parts: {string.Join(", ", partDescs)} (* = critical)");
        }
        if (custom != null)
        {
            // Names what the build reached.
            Log.Info("flight", $"{tag}custom plane: '{custom.Name}' on {planeName}, armour {custom.ArmourNose}/{custom.ArmourTail}/{custom.ArmourLeftWing}/{custom.ArmourRightWing} units x{Flight.CustomPlaneBuild.ArmourUnitScale}, hardpoints {custom.LeftHardpoints}+{custom.RightHardpoints}, engine {custom.Engine} thrust={stats.EnginePower:0.###}{(controller.Nitro.Installed ? " (nitrous injector)" : "")}");
        }

        // Every readout this pane draws for its pilot belongs to the controller's own FlightHud,
        // which owns the per-frame feed; nothing here writes one after assembly. None of it is
        // built for a remote seat: there is no pane to draw it in and no camera to aim it by.
        var pilotHud = controller.PilotHud;
        if (!remote)
        {
            // The original's heading tape, rebuilt from the chapter's own HUD
            // textures (compassticks2/compasstxt ship in every chapter's archive).
            pilotHud.Compass = CompassTape.Build(_aircraft.Textures);
            if (verbose && pilotHud.Compass != null)
                Log.Info("flight", $"compass: heading tape from compassticks2/compasstxt");

            // The cockpit dials (altimeter / speedometer / damage display), rebuilt
            // from the plane's own gauges subtree in planes.zbd + the chapter's
            // HUD textures (needle/lowalt/stall/<plane>_damage/hilite/hatchptrn).
            pilotHud.Gauges = GaugeCluster.Build(_aircraft.PlanesGamez, planeName, _aircraft.Textures,
                stats.DestroyableParts);
            if (pilotHud.Gauges is { } gauges)
            {
                var damage = controller.Damage;
                if (damage != null)
                    gauges.PartFraction = name =>
                        damage.Parts.TryGetValue(name, out var s) ? s.Fraction : 1f;
                if (verbose)
                    Log.Info("flight", $"gauges: altimeter/speedometer/damage dial from the plane's gauges subtree");
            }

            // The bitmap-font proof overlay: draw the sample string on this pane so a 1P view
            // and a 4P pane can be compared (--hud-font-test). Set before the controller
            // enters the tree, its _Ready adds this to the HUD canvas.
            if (_aircraft.HudFont != null && _policy.HudFontTest)
            {
                pilotHud.FontTest = new HudFontTest(_aircraft.HudFont, _policy.HudFontTestText);
                if (verbose)
                    Log.Info("flight", $"hud-font-test: '{_policy.HudFontTestText}' via 5pointhud font");
            }

            // The gun aiming reticle: the ballistic impact point of the selected gun
            // group at the convergence distance, drawn as the game's pipper. It visibly
            // trails the nose in a hard turn, and sits on the rounds in steady flight.
            if (_aircraft.ReticleTex != null && controller.Loadout != null)
            {
                pilotHud.Reticle = ImpactReticle.Build(_aircraft.ReticleTex, rig.Camera);
                if (verbose)
                    Log.Info("flight", $"gun reticle: ballistic impact point via impact_point.png");
            }
        }

        // Visible damage, phase 1: the object, unconditionally. Phase 2 (the sink and the stops)
        // is wired by WorldEffectsFactory.BuildFlightCrashRuntime, since only that half needs a
        // live rig runtime.
        if (controller.Damage != null)
        {
            controller.Visuals = BuildDamageVisuals(planeBuilder, planeModel, stats, _world.CrashProgram);
            if (verbose)
                Log.Info("flight",
                    $"damage visuals: {controller.Visuals.PanelCount} panels, authored stage anims via the rig runtime");
        }

        // The data-driven crash rig is built AFTER the controller enters the tree
        // (below), so the crash def's reset states read valid global transforms.

        // Never on a remote seat. This stack is the sound of the aeroplane you are sitting in, and
        // that pilot is sitting in theirs, on their own machine.
        if (!remote && _world.Sounds != null && _world.SoundDefs != null)
        {
            var audio = new FlightAudio { MixGain = _human.MixGain };
            audio.Setup(_world.Sounds, _world.SoundDefs, stats, _aircraft.WeaponDefs, _world.SoundGroups);
            controller.Audio = audio;
            controller.AddChild(audio);
            if (verbose)
                Log.Info("flight", $"audio: engine={stats.EngineSound} damaged={stats.DamagedEngineSound ?? "none"} whine={stats.WhineSound ?? "none (no def names prop_sound)"} rattle={stats.RattleSound}{(_human.MixGain < 1f ? $" (per-player mix gain {_human.MixGain:0.00})" : "")}");
        }
        // This player's stunt run: player 1 flies the loaded instance, everyone else an
        // independent copy of the same zones, own progress, own clock. Never on a swap, which
        // would restart the clock and stack a second run HUD (see AirframeSwapRequest).
        if (swap == null && !remote && _human.StuntZones != null)
        {
            var run = pi == 0 ? _human.StuntZones : _human.StuntZones.ForAnotherPlayer();
            controller.Stunt = run;
            run.LogTag = tag; // "P2 " in a race, one shared world, four runs
            controller.DebugCompleteStunt = _policy.DebugScoreboard;
            // This pilot's own unflown zones, on their own target cycle. Bound per controller
            // rather than through FlightRoster's roster-wide channel: each pane races its own copy
            // of the run, and a shared feed would put one pilot's cleared zones on another's HUD.
            controller.TargetObjectives = into => run.CollectTargets(into);
            // This pane's Danger Zone camera, photographing through the pilot's posed eye. A run
            // with no window to draw it in reads this seat's own pane instead, so in splitscreen
            // that shot is the SubViewport that crossed the marker, never the window.
            var pane = rig.Viewport;
            var capture = new StuntCapture(run, _policy.Chapter,
                landed => controller.Photograph is { } eye && DangerZonePhotograph.Drawable
                    ? eye.Request(landed)
                    : PaneReadback.Request(pane ?? (controller.IsInsideTree() ? controller.GetViewport() : null), landed));
            capture.Sting = () => controller.Audio?.OnDangerZoneCamera();
            controller.StuntShots = capture;

            // The run HUD, one per pane: clock, zones cleared, banners. The zone MARKER is the
            // targeting HUD's, since a zone is an objective like any other.
            var runHud = StuntRunHud.Build(run);
            pilotHud.StuntRun = runHud;
            if (_human.Race is { } race)
            {
                // Racing: no per-player splits board, the shared ranked board
                // below covers the whole window when the last pilot is in. The run
                // HUD shows this player's placing meanwhile.
                race.Add(pi, controller.Stunt, planeDisplay);
                runHud.Race = race;
                runHud.PlayerIndex = pi;
                // ⚠ Instant Action keeps the placings and nothing else: its ending is the
                // director's hold and wrap-up, which the pilot flies through, so the seat takes
                // none of the race board's rules (the finish hold, R as a rematch).
                if (!_human.InstantActionActive)
                {
                    controller.Race = race;
                }
            }
            else if (_human.InstantActionActive)
            {
                // Instant Action carries the splits on its own wrap-up board instead, so the two
                // results boards cannot wake on the same event and stack (BL-358).
            }
            else
            {
                // Solo: the end-of-run scoreboard, per-zone splits + total +
                // persisted best time, keyed chapter/mission/plane in
                // user://stunt_scores.json (race totals are deliberately not recorded).
                var scoreKey = $"{_policy.Chapter}/{_policy.Mission}/{custom?.Name ?? planeName}";
                var scoreboard = StuntScoreboard.Build(controller.Stunt,
                    planeDisplay, $"{_policy.Chapter}   ·   {PlaneRoster.Humanize(_policy.Scenario)}",
                    ScoreStore.Load(), scoreKey, _human.ExitsToMenu, _human.PauseState, _human.MenuInputFor);
                scoreboard.Restart = controller.Rerun;
                scoreboard.Exit = _human.ExitSession;
                scoreboard.Shots = capture;
                controller.Scoreboard = scoreboard;
                Log.Info("flight", $"stunt scoreboard: splits + best time (key '{scoreKey}')");
            }
            if (verbose)
            {
                Log.Info("flight", $"stunt run HUD: clock + zones cleared + banners; zones ride the target cycle");
                WhatSuffix += $" [stunt: {controller.Stunt.TotalCount} zones]";
            }
        }

        // Dogfight (--vs): the per-pane match timer/K-D/leader line + kill banner, bound to the
        // match GameSession built before this loop ran; kill facts arrive later via Downed.
        if (!remote && _human.VersusMatch is { } versus)
        {
            // Rigs is the SAME list GameSession keeps live for the whole session, every seat
            // already exists (BuildRigs ran before this loop), only .Controller fills in as each
            // player assembles, so by the time this pane draws, every opponent's is populated.
            controller.VersusHud = VersusHud.Build(versus, pi, rig.Camera);
            controller.VersusHud.Rigs = _human.Rigs;
            if (verbose)
                Log.Info("flight", $"dogfight HUD: match timer/K-D/leader line + kill banner + opponent markers");
        }

        // One per human pane, in EVERY flight session unlike VersusHud: built unconditionally
        // because generators spawn hostiles mid-session, and it draws nothing with an empty pool.
        // A remote seat picks its own targets on its own machine, so it takes neither.
        if (!remote)
        {
            var targetHud = TargetHud.Build(pi, rig.Camera, _world.Projectiles);
            pilotHud.TargetHud = targetHud;
            targetHud.FogRange = _world.FogRange;   // the spyglass's range gate, null on a bare rig
            if (verbose)
                Log.Info("flight", $"targeting HUD: selected-target marker (brackets + label, edge arrow off screen)");

            // The player's target selection: one per human pane, each with its own pool. The
            // cycles are sorted against THIS plane's pose, so they cannot be shared. GameSession
            // binds TargetSubParts later, once the zeppelins exist.
            controller.Targeting = new TargetSelection { NearestAfterKill = _policy.NearestAfterKill };
            controller.InitialTarget = _policy.TargetSelect;   // --target=, the scripted twin

            // ⚠ Bind on EVERY pane, not only under --debug-markers. It is what TargetHud.OwnTeam
            // reads this pane's side off, and the pilot-index derivation it falls back to is the
            // wingman-in-the-marker bug.
            targetHud.Own = controller;

            // --debug-markers: the same HUD marks every live aircraft instead of one hostile. Own also
            // keeps it from marking the aircraft the camera is sitting on.
            if (_policy.DebugMarkers)
            {
                targetHud.MarkAll = true;
                if (verbose)
                    Log.Info("flight", $"--debug-markers: marking EVERY live aircraft (red hostile / blue own side)");
            }
        }

        // Every player's start comes from ONE call: a grid start is not decomposable, since no
        // single pilot's answer exists until every slot is known. Resolved lazily on the first rig,
        // so the caller can construct the assembler first. A swap brings its own instead.
        var start = swap?.Start ?? (_starts ??= _spawns.ChooseStarts(
            _human.SpawnList, _world.MissionZrdrPath, _human.SpawnBase, _human.RigCount))[pi];
        // The plant's force path is chosen once, here, off who is flying, a person, so the
        // player path. FlightModel.UsesAiForcePath carries why this is a construction argument
        // rather than the original's own pointer-compare-against-the-player test.
        var camParams = _aircraft.CamParamsFor(planeName);
        // A remote seat passes no camera, the same null an AI rig passes. The chase rig, the head
        // look and every camera write inside Setup are then not built at all.
        controller.Setup(new FlightModel(stats, aiForcePath: !controller.IsHumanPiloted),
            remote ? null : rig.Camera, camParams, start.Pos, start.LookAt,
            start.ThrottleFrac, start.SpeedMps, cockpitCameraOffset: planeBuilder.CockpitCameraOffset);
        // The Danger Zone eye, framed off the airframe's own chase distance and aimed at the pose
        // the controller draws, which is the controller node's own transform.
        if (!remote)
        {
            var scatter = Rng.Stream(Rng.Photograph);
            controller.Photograph = DangerZonePhotograph.Build(rig.Camera, controller.Cockpit,
                () => controller.GlobalTransform, camParams.Dist, scatter.Randf, airframe: controller);
            controller.AddChild(controller.Photograph);
        }
        // --weapon-lab: a flight session whose aircraft is pinned at the spawn pose. Set after
        // Setup, so the pin, captured at the first held sim step, takes the pose Setup just wrote.
        if (_policy.WeaponLab)
        {
            controller.Held = true;
            if (verbose)
                Log.Info("flight", $"weapon lab: P{pi + 1} held at the spawn pose (world sim running)");
        }
        // The exhaust smoke hangs off the plane's own exhaust marker nodes, one trail per marker.
        controller.ExhaustSmoke = ExhaustSmoke.Build(planeModel, _aircraft.Textures, controller,
            _world.Ambience);

        // The ambient speed cue is chapter data, not an aircraft-model effect. One private copy
        // per player, so splitscreen panes do not see another pilot's ahead-of-plane wisps. It is
        // drawn ahead of a camera, so a remote seat, which has none, takes no copy.
        if (!remote && !_policy.EmptyStage)
        {
            controller.SpeedCue = SpeedCue.Build(_world.ChapterZrdrPath, _aircraft.Textures, _worldRoot,
                _world.Ambience,
                rig.VisualLayer == 0 ? null : node => SplitScreen.SetVisualLayer(node, rig.VisualLayer));
        }

        controller.Name = $"player{pi + 1}";
        rig.Controller = controller;
        _worldRoot.AddChild(controller);
        // ⚠ Hide a remote seat's pilot HUD the frame it enters the tree. A human-piloted rig
        // builds its own canvases in _Ready, and an empty one still carries the telemetry line
        // and the message stack. A seat with no pane would paint those over the local view.
        if (remote)
        {
            controller.SetPilotHudVisible(false);
        }

        // Data-driven crash: a per-player crash AnimRuntime playing the compiled def. Built here,
        // once the controller (and its plane model) are in the tree, so the crash def's reset
        // states resolve valid global transforms.
        if (_world.CrashProgram != null && _world.WorldScene != null)
        {
            // WorldSounds goes in on every rig alike; BuildFlightCrashRuntime drops it for a human
            // one, so that asymmetry is stated once, there. The planes gamez goes in on every rig
            // too, or a kill would drop a parachute for one spawner and not the other.
            _worldEffects.BuildFlightCrashRuntime(controller, planeBuilder, planeName, _world.Gamez,
                _world.WorldScene, _aircraft.Textures, _world.CrashProgram, verbose,
                worldSounds: _world.WorldRuntime?.Sounds, planesGamez: _aircraft.PlanesGamez);
            // The start choreography for the very first spawn: Respawn() plays this same def on
            // every later respawn, but Setup() above called Respawn() before this runtime existed.
            controller.CrashRuntime?.Play("startprops", planeModel, applyReset: false);
        }

        // This pilot's own airframe onto its own visual layer, LAST, so everything the lines above
        // hung on the model travels with it. The only camera that drops the layer is this pane's
        // spyglass, whose eye stands inside the aeroplane (docs/architecture/Session.md).
        if (!remote)
        {
            // Never a remote seat: no camera here looks out of that aeroplane. The band is four
            // layers wide, so a seat past the fourth would wrap onto a pane's own.
            SplitScreen.SetVisualLayer(planeModel, SplitScreen.OwnAirframeLayer(pi));
        }
    }

    public AssemblyState CaptureState() =>
        new(MeshInstances, WhatSuffix, _aircraft.PaintRng.State, _starts);

    public void RestoreState(AssemblyState state)
    {
        MeshInstances = state.MeshInstances;
        WhatSuffix = state.WhatSuffix;
        _aircraft.PaintRng.State = state.PaintRngState;
        _starts = state.Starts;
    }

    /// <summary>Pane <paramref name="pi"/>'s menu-chosen fit, or null to fly the stock one. An
    /// explicit <c>--loadout=</c> takes the whole choice away rather than merging with it, so the
    /// flag names the fit outright the way a playtest row needs; <c>--rocket=</c> needs no test
    /// here because it is applied after the bind and wins by arriving later. ⚠ A swap takes it
    /// away too: the sortie's picks belong to the aeroplane the pilot left, and the airframe a
    /// mission hands over carries its own fit (see <see cref="AirframeSwapRequest"/>).</summary>
    private LoadoutChoice? MenuFitFor(int pi, AirframeSwapRequest? swap)
    {
        int menu = MenuSeatOf(pi);
        if (swap != null || _policy.LoadoutOverride != null)
        {
            return null;
        }

        // A seat flown elsewhere carries its own pilot's fit, so every machine builds it alike.
        if (menu < 0)
        {
            return _human.SeatFit?.Invoke(pi);
        }

        if (menu >= _policy.MenuLoadouts.Count)
        {
            return null;
        }

        return _policy.MenuLoadouts[menu];
    }

    /// <summary>Pane <paramref name="pi"/>'s custom-built plane, or null to fly the stock
    /// airframe. Empty on every launch that did not come off the launchscreen, so the scripted
    /// paths (<c>--plane=</c>, <c>--det</c>) never see one.</summary>
    private Flight.CustomPlaneDef? CustomPlaneFor(int pi) =>
        MenuSeatOf(pi) is int menu && menu >= 0 && menu < _policy.MenuCustomPlanes.Count ? _policy.MenuCustomPlanes[menu] : null;

    // Which of this machine's menu seats flies seat pi. The menu lists only the local seats, and a
    // guest's own seat stands behind its host's in the roster. A seat flown elsewhere has none.
    private int MenuSeatOf(int pi) => Net.NetSeats.LocalOrdinal(_human.NetSeats, pi);

    public readonly record struct AssemblyState(int MeshInstances, string WhatSuffix,
        ulong PaintRngState, IReadOnlyList<FlightStart>? Starts);

}
