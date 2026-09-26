using System;
using CSVM.Flight.Ai;
using CSVM.Flight.Airframe;
using CSVM.Flight.Audio;
using CSVM.Flight.Camera;
using CSVM.Flight.Hangar;
using CSVM.Flight.Weapons;
using CSVM.Mech3;
using CSVM.Session.World;
using CSVM.Utils;
using Godot;

namespace CSVM.Session.Roster;

internal sealed class AiFlightAssembler
{
    private readonly FlightRosterPolicy _policy;
    private readonly LiveryResolver _liveries;
    private readonly WorldEffectsFactory? _worldEffects;
    private readonly Node3D _worldRoot;
    private readonly AircraftAssemblyResources _aircraft;
    private readonly FlightWorldBindings _world;
    private readonly int _humanCount;
    private readonly CrashRigQueue? _crashRigs;
    private readonly System.Collections.Generic.HashSet<string> _liveryDefsLogged =
        new(StringComparer.OrdinalIgnoreCase);
    // PERF-22: the hulls are a pure function of the airframe's triangles, so one set serves every
    // aeroplane of that airframe. AircraftBody indexes its OWN CollisionShape3D children, so two
    // bodies mounting the same shape resource still report their own part names.
    private readonly System.Collections.Generic.Dictionary<string, PlaneCollider?> _hulls =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly System.Collections.Generic.Dictionary<string, PlanePainter> _painters =
        new(StringComparer.Ordinal);
    private readonly AiAirframePool _airframes;
    private AiSkills? _aiSkills;
    private System.Collections.Generic.List<Maneuver>? _maneuvers;
    private bool _noAssistLogged;

    public AiFlightAssembler(FlightRosterPolicy policy, LiveryResolver liveries,
        WorldEffectsFactory? worldEffects, Node3D worldRoot, AircraftAssemblyResources aircraft,
        FlightWorldBindings world, int humanCount, CrashRigQueue? crashRigs = null)
    {
        _crashRigs = crashRigs;
        _policy = policy;
        _liveries = liveries;
        _worldEffects = worldEffects;
        _worldRoot = worldRoot;
        _aircraft = aircraft;
        _world = world;
        _humanCount = humanCount;
        _airframes = new AiAirframePool(BuildAirframe);
    }

    public AiSkills? Skills => _aiSkills;

    /// <summary>The aeroplanes built ahead of the launches that need them.</summary>
    public AiAirframePool Airframes => _airframes;

    public AssemblyState CaptureState(AiSpawn spawn)
    {
        var machine = spawn.Pilot.Machine;
        return new AssemblyState(spawn.Pilot.Gunner, spawn.Pilot.Rocketeer, machine,
            machine?.AttackRange, machine?.ReturnRange, _aircraft.PaintRng.State,
            Rng.Stream(Rng.Ai).State, Rng.Stream(Rng.Spawn).State,
            _aiSkills, _maneuvers, _noAssistLogged);
    }

    public void RestoreState(AiSpawn spawn, AssemblyState state)
    {
        spawn.Pilot.Gunner = state.Gunner;
        spawn.Pilot.Rocketeer = state.Rocketeer;
        spawn.Pilot.Machine = state.Machine;
        if (state.Machine != null)
        {
            state.Machine.AttackRange = state.AttackRange!.Value;
            state.Machine.ReturnRange = state.ReturnRange!.Value;
        }
        _aircraft.PaintRng.State = state.PaintRngState;
        Rng.Stream(Rng.Ai).State = state.AiRngState;
        Rng.Stream(Rng.Spawn).State = state.SpawnRngState;
        _aiSkills = state.Skills;
        _maneuvers = state.Maneuvers;
        _noAssistLogged = state.NoAssistLogged;
    }

    /// <summary>Orders <paramref name="depth"/> aeroplanes for the block <paramref name="template"/>
    /// describes, up to <paramref name="cap"/> held for one airframe and livery. False where the
    /// livery is not a function of the block, which builds in place as it always did.</summary>
    public bool OrderAirframes(AiSpawn template, int depth, int cap)
    {
        var stats = _aircraft.AiStatsFor(template.PlaneName, template.AiDef);
        if (!TryBlockScheme(stats, template, out var scheme))
        {
            return false;
        }

        _airframes.Order(template.PlaneName, scheme, depth, cap);
        return true;
    }

    public FlightController Assemble(AiSpawn spawn, int index, Action<FlightController> onCreated)
    {
        var baseStats = _aircraft.AiStatsFor(spawn.PlaneName, spawn.AiDef);
        PreparePilot(spawn, baseStats);
        // The engine's spawn order: the roster's own override lands first, then the difficulty
        // scale, then the per-spawn jitter on the scaled pools, never any step out of that order
        // (docs/org/vehicleDamage.md).
        var stats = baseStats
            .WithRosterDurability(spawn.InitHealth, spawn.Armor)
            .WithEnemyDurability(Difficulty.FactorForSpawn(spawn.Team, spawn.Difficulty, _policy.Difficulty))
            .WithAiSpawnJitter(Rng.NewSystemRandom(Rng.Spawn, index, 0));
        // The name the targeting readout prints, resolved here because this is where the string
        // table and the loaded def meet; a rig with no table keeps the def-name derivation.
        // ⚠ The block's own pilot name outranks the airframe title: docs/org/targeting.md.
        if ((spawn.PilotName ?? stats.AiTitleKey) is { } titleKey && _aircraft.WeaponMessages is { } messages)
        {
            string title = messages.Get(titleKey);
            stats.AiTitle = title.StartsWith("MSG_", StringComparison.Ordinal) ? null : title;
        }
        if (spawn.Pilot.Machine is { } machine)
        {
            machine.AttackRange = stats.AiAttackRange;
            machine.ReturnRange = stats.AiReturnRange;
            machine.AttackDwellS = stats.AiAttackDwell;
            machine.NotPursuitDwellS = stats.AiNotPursuitDwell;
        }

        FlightController controller;
        Node3D planeModel;
        using (PerfSample.Scope(PerfSite.AiSpawn))
        {
            // The decoded order: the caller's own scheme, then the militia def's authored one, then
            // the default-pattern rule. ShippedSkins reaches only the last step. The first two are
            // a function of the block alone, so a pooled aeroplane can already wear them.
            PaintScheme? scheme;
            AiAirframePool.Prepared? prepared = null;
            if (TryBlockScheme(stats, spawn, out var blockScheme))
            {
                scheme = blockScheme;
                prepared = _airframes.Claim(spawn.PlaneName, scheme);
            }
            else
            {
                scheme = _liveries.SchemeFor(
                    _humanCount + index, _aircraft.ZrdrPath,
                    _aircraft.PaintRng, _liveries.PatternsForPlane(_aircraft.PlanesGamez, spawn.PlaneName),
                    useDefaultPattern: !spawn.ShippedSkins);
            }

            prepared ??= BuildAirframe(spawn.PlaneName, scheme);
            var planeBuilder = prepared.Builder;
            planeModel = prepared.Model;

            controller = new FlightController
            {
                Scheme = scheme,
                ShippedSkins = spawn.ShippedSkins,
                Painter = planeBuilder.Painter,
            };
            controller.Bind(new FlightControllerBuild
            {
                PlayerIndex = FlightRoster.ShooterIdBase + index,
                IsHumanPiloted = false,
                Pilot = spawn.Pilot,
                PlaneModel = planeModel,
                Props = prepared.Props,
                WingLights = prepared.WingLights,
                Surfaces = prepared.Surfaces,
                Collider = prepared.Collider,
                Damage = stats.DestroyableParts.Count > 0 || stats.VehicleHealth is > 0f
                    ? PlaneDamage.For(stats) : null,
                CollideDamageSink = _world.WorldRuntime != null ? _world.WorldRuntime.CollideDamageAt : null,
                GrazeEffectSink = _world.WorldEffects is { } fx ? (name, pt) => fx.PlayEffectAt(name, pt) : null,
                TouchdownDefs = _world.TouchdownDefs,
                Projectiles = _world.Projectiles,
                HumanPositions = _world.HumanPositions,
                PadDevices = Array.Empty<int>(),
                Inert = spawn.Inert,
                Team = spawn.Team,
                Shake = new PlaneShake(_aircraft.Shakes),
            });
            // The roster block's nitro slot installs the injector used by an AI's nitro evade.
            controller.Nitro.Installed = spawn.Nitro;
            // The block's objective marker rides the aeroplane itself, so it wakes, moves and dies
            // with it. Resolved here for the same reason the name above is: the string table and
            // the block meet at the spawn, and the targeting path holds no table.
            controller.ObjectiveTarget = spawn.ObjectiveMarker;
            if (spawn.ObjectiveMarker)
            {
                controller.ObjectiveTypeLabel = MarkerLabel(spawn.ObjectiveTypeLabel);
                controller.ObjectiveCategory = MarkerLabel(spawn.ObjectiveCategory);
            }
            onCreated(controller);

            // The AI def's own fit when it authors one, the player stock table otherwise. Both
            // paths say so when they come up empty: an AI plane that flies unarmed is a bug that
            // looks exactly like a passive enemy from the cockpit.
            try
            {
                if (stats.AiWeapons.Count > 0)
                {
                    controller.Loadout = Loadout.BindAi(stats.AiWeapons,
                        stats.AiDefName ?? stats.DefName, planeModel, _aircraft.WeaponDefs);
                }
                else if (_aircraft.StockLoadouts.For(stats.DefName) is { } loadout)
                {
                    controller.Loadout = Loadout.Bind(
                        spawn.Fit is { } fit ? fit.ApplyTo(loadout) : loadout, planeModel, _aircraft.WeaponDefs);
                }
                else
                {
                    string armed = stats.AiDefName ?? stats.DefName;
                    Log.Warn("weapons", $"ai: no weapons block on '{armed}' and no stock loadout for '{stats.DefName}', this plane flies unarmed");
                }
                if (controller.Loadout != null)
                {
                    controller.Destructibles = _world.WorldRuntime?.Destructibles;
                    controller.SurfaceVehicles = _world.SurfaceVehicles;
                    controller.Ordnance = PylonOrdnance.Build(controller.Loadout, _world.Projectiles, controller.InfiniteAmmo);
                }
            }
            catch (Exception e)
            {
                Log.Warn("weapons", $"ai: loadout bind failed for '{stats.DefName}', this plane flies unarmed error={e.Message}");
            }

            // The carried turret gunners, built as the player's are and independent of the loadout
            // above. A gunner is a separate crewman reading none of the pilot's engagement gates,
            // so a block whose net holds its pilot out of combat still shoots back.
            if (_aircraft.TurretDefs is { } turretDefs && stats.TurretMounts.Count > 0)
            {
                controller.Turrets = TurretController.BuildCarried(
                    turretDefs, stats, planeModel, _aircraft.WeaponDefs, controller, _world.Projectiles,
                    new GunVoiceHome(controller, _world.Sounds, _world.SoundDefs, _world.HumanPositions));
                if (controller.Turrets.Length > 0)
                {
                    Log.Info("weapons", $"ai: '{stats.DefName}' carries {controller.Turrets.Length} turret gunner(s) under shooter id {controller.PlayerIndex}");
                }
            }

            // Visible damage, phase 1: the same object the player rig gets, built from this
            // airframe's own injure_anims. Phase 2 (the sink and the stops) is wired by
            // BuildFlightCrashRuntime below, once the runtime it plays into exists.
            if (controller.Damage != null)
            {
                controller.Visuals = HumanFlightAdapter.BuildDamageVisuals(
                    planeBuilder, planeModel, stats, _world.CrashProgram);
            }

            controller.Setup(new FlightModel(stats, aiForcePath: true), null, new CamParams(),
                spawn.Position, spawn.LookAt);
            controller.ArmSpawnTimers();
            // The same exhaust smoke the flown aircraft carries, since the original builds it for
            // every airframe with exhaust markers; the flight step feeds it this pilot's own gap.
            controller.ExhaustSmoke = ExhaustSmoke.Build(planeModel, _aircraft.Textures, controller,
                _world.Ambience);
            // The authored identity wins where the caller has one, because the ranking reads this
            // name against patterns written for it. The counter form is the fallback for the
            // spawners with no authored name (--ai, the Instant Action fan, the generators).
            controller.Name = spawn.NodeName is { Length: > 0 } authored
                ? authored
                : $"ai{index + 1}_{spawn.PlaneName}";
            _worldRoot.AddChild(controller);
            // ⚠ Both voices take the SAME listener seam, the human pilots the projectile pool's
            // one-shots measure against. An aircraft answering two listener models let a
            // splitscreen pane hear its engine and not its guns, or the reverse.
            controller.EngineAudio = AiEngineAudio.Attach(controller, _world.Sounds, _world.SoundDefs,
                stats, _world.HumanPositions);
            // Attach no-ops to null when the session found no sound archive; the own-ship
            // FlightAudio is never built for an AI. The engine loop culls at 2000 units, each
            // weapon cue at its own authored audible distance.
            controller.WeaponAudio = AiWeaponAudio.Attach(controller, _world.Sounds, _world.SoundDefs,
                _aircraft.WeaponDefs, _world.HumanPositions);

            if (_world.CrashProgram != null && _world.WorldScene != null)
            {
                if (_worldEffects == null)
                    throw new InvalidOperationException("crash-enabled AI assembly needs world effects");
                // The planes gamez goes in here exactly as it does on the player rig: the destroy
                // def's `chuteman` is a template root of planes.zbd, and an asymmetry here would
                // give the parachute to one kind of kill only.
                var rig = _worldEffects!.BeginFlightCrashRuntime(controller, planeBuilder, spawn.PlaneName,
                    _world.Gamez, _world.WorldScene, _aircraft.Textures, _world.CrashProgram, verbose: false,
                    worldSounds: _world.WorldRuntime?.Sounds, planesGamez: _aircraft.PlanesGamez);
                // The rig is the heaviest block here and the only one the aeroplane does not need in
                // order to be in the world: the world root already holds this controller. A caller
                // with a pump takes it off the launch frame; one without builds it in place.
                if (_crashRigs is { } queue)
                {
                    queue.Defer(controller, rig,
                        () => controller.CrashRuntime?.Play("startprops", planeModel, applyReset: false));
                }
                else
                {
                    rig.Finish();
                    controller.CrashRuntime?.Play("startprops", planeModel, applyReset: false);
                }
            }
        }

        Log.Info("flight", $"ai: spawned '{spawn.PlaneName}' as {controller.Name} (shooter id {controller.PlayerIndex}) pos=({spawn.Position.X:0},{spawn.Position.Y:0},{spawn.Position.Z:0}) jitter=(fd {stats.FdSpeed:0.0} thrust {stats.EnginePower:0.000}) {(spawn.Inert ? "INERT " : "")}{(spawn.Pilot.Patrol is { } patrol ? $"net='{patrol.Net.Name}#{patrol.Net.Id}' ({patrol.Net.Nodes.Count} nodes)" : Log.Format($"heading={spawn.Pilot.TargetHeadingDeg:0}° alt={spawn.Pilot.TargetAltitude:0} m"))}");
        return controller;
    }

    // The spawn-independent half of an AI aeroplane, in the order a launch builds it: the
    // painted model, the prop and surface animators, the wing lamps and the collision hulls. The
    // pool runs this at load and a claim-less launch runs it in place, so both produce one tree.
    private AiAirframePool.Prepared BuildAirframe(string planeName, PaintScheme? scheme)
    {
        var builder = new PlaneBuilder(_aircraft.PlanesGamez, _aircraft.Textures, spinningProps: true,
            scheme: scheme, patterns: _liveries.Patterns, painter: PainterFor(planeName, scheme));
        var model = builder.Build(planeName);
        return new AiAirframePool.Prepared(builder, model,
            PropAnimator.Build(model),
            WingLightBlinker.Build(builder.WingFlares, _policy.AnimLod),
            ControlSurfaceAnimator.Build(model),
            HullsFor(planeName, model));
    }

    // One painter per airframe and livery (PERF-22): the painted skins and the swapped decals are a
    // function of the scheme and the aircraft's own skin prefix, so the second aeroplane of a wave
    // wears the images the first composed instead of composing them again. Held by this assembler,
    // which is the session's, so the painted skins die with the archive they were baked from.
    private PlanePainter? PainterFor(string planeName, PaintScheme? scheme)
    {
        if (scheme == null)
        {
            return null;
        }

        string key = AiAirframePool.KeyFor(planeName, scheme);
        if (_painters.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var root = _aircraft.PlanesGamez.FindByName(planeName);
        if (root == null || PlanePainter.PrefixFor(_aircraft.PlanesGamez, root) is not { } prefix)
        {
            return null;
        }

        var made = new PlanePainter(_aircraft.Textures, _liveries.Patterns, scheme, prefix);
        _painters[key] = made;
        Log.Info("world", $"[paint] {planeName} ({prefix}): {scheme}{(made.PatternMissesAircraft ? $": pattern '{scheme.FolderName}' ships no {prefix} skins, decals only" : "")}");
        return made;
    }

    // One hull set per airframe, reused by every aeroplane of it (PERF-22). Keyed on the airframe
    // name because the geometry is the airframe's; a livery substitutes textures and moves no
    // triangle.
    private PlaneCollider? HullsFor(string planeName, Node3D model)
    {
        if (_hulls.TryGetValue(planeName, out var cached))
        {
            return cached;
        }

        var built = PlaneCollider.Build(model);
        _hulls[planeName] = built;
        return built;
    }

    // The livery wherever it is a function of the block alone: the caller's own scheme, then the
    // militia def's, then the shipped-default rule, which reads no spawn order. False under
    // --paint=, which picks per player index and so cannot be resolved before the launch.
    private bool TryBlockScheme(PlaneStats stats, AiSpawn spawn, out PaintScheme? scheme)
    {
        scheme = spawn.Scheme;
        if (scheme != null)
        {
            return true;
        }

        if (_liveries.PaintRequested)
        {
            return false;
        }

        scheme = MilitiaScheme(stats, spawn) ?? _liveries.SchemeFor(
            0, _aircraft.ZrdrPath, _aircraft.PaintRng,
            _liveries.PatternsForPlane(_aircraft.PlanesGamez, spawn.PlaneName),
            useDefaultPattern: !spawn.ShippedSkins && spawn.LiveryDef == null);
        return true;
    }

    private void PreparePilot(AiSpawn spawn, PlaneStats defStats)
    {
        var pilot = spawn.Pilot;
        var defSkills = defStats.AiPilotSkills;
        var roster = spawn.RosterSkills ?? default;
        // The precedence the original's roster has: a block's own slot first, then the def's, then
        // the flat rating. --ai-attack= pins every slot (docs/formats/ai-rosters.md).
        int Authored(int? authored, int fallback, int? rosterSlot = null) =>
            rosterSlot ?? (_policy.AiAttackSkillExplicit
                    || (spawn.AttackRating != null && spawn.RosterSkills == null)
                ? fallback : authored ?? fallback);

        // The difficulty then shifts what the block authored, on every one of the nine stats, and
        // an ace is exempt (docs/org/aiControlLaw.md "The skill scalar"). An explicit --ai-attack=
        // is a pin and not a roster reading, so it is left where the flag put it.
        int SkillFor(int? authored, int fallback, int? rosterSlot = null) =>
            _policy.AiAttackSkillExplicit
                ? Authored(authored, fallback, rosterSlot)
                : Difficulty.SkillRatingForSpawn(Authored(authored, fallback, rosterSlot),
                    spawn.Team, spawn.Ace, spawn.Difficulty, _policy.Difficulty);

        if ((spawn.AttackRating ?? _policy.AiAttackSkill) is { } skill && pilot.Gunner == null)
        {
            try
            {
                _aiSkills ??= AiSkills.Load(_aircraft.ZrdrPath);
                var rng = new RandomNumberGenerator
                { Seed = (ulong)(uint)Rng.NewIntSeed(Rng.Ai) };
                pilot.Gunner = new AiGunner(rng)
                {
                    DeadEyeAngleDeg = _aiSkills.DeadEyeAngleDeg(
                        SkillFor(defSkills.DeadEye, skill, roster.DeadEye)),
                    QuickDrawAngleDeg = _aiSkills.QuickDrawAngleDeg(
                        SkillFor(defSkills.QuickDraw, skill, roster.QuickDraw)),
                };
                var ordRng = new RandomNumberGenerator
                { Seed = (ulong)(uint)Rng.NewIntSeed(Rng.Ai) };
                int quickDraw = SkillFor(defSkills.QuickDraw, skill, roster.QuickDraw);
                pilot.Rocketeer = new AiRocketeer(ordRng.Randf)
                {
                    QuickDrawAngleDeg = _aiSkills.QuickDrawAngleDeg(quickDraw),
                    QuickDrawChance = _aiSkills.QuickDrawChance(quickDraw),
                };
                Log.Info("weapons", $"ai: gunner armed at dead-eye {SkillFor(defSkills.DeadEye, skill, roster.DeadEye)} / quick-draw {quickDraw} (dead-eye {pilot.Gunner.DeadEyeAngleDeg:0.00}°, quick-draw {pilot.Gunner.QuickDrawAngleDeg:0}°, ordnance roll {pilot.Rocketeer.QuickDrawChance:0.00} per {pilot.Rocketeer.RefireSeconds:0} s)");
            }
            catch (Exception e)
            {
                GD.PushWarning($"--ai-attack: cannot load ai_skill_parameters: {e.Message}");
            }
        }

        if (pilot.Machine != null)
            return;
        try
        {
            _aiSkills ??= AiSkills.Load(_aircraft.ZrdrPath);
            _maneuvers ??= Maneuvers.Load(_aircraft.ZrdrPath);
            int rating = spawn.AttackRating ?? _policy.AiAttackSkill ?? 5;
            int sixthSense = SkillFor(defSkills.SixthSense, rating, roster.SixthSense);
            pilot.Machine = new AiModeMachine(Rng.NewSystemRandom(Rng.Ai))
            {
                ActivationRange = _aiSkills.MinAiActiveDist,
                SteadyHandExponent = AiModeMachine.ExponentFor(_aiSkills.At("steady_hand_chance",
                    SkillFor(defSkills.SteadyHand, rating, roster.SteadyHand))),
                SixthSenseChance = _aiSkills.At("sixth_sense_chance", sixthSense),
                SixthSenseFactor = _aiSkills.At("sixth_sense_factor", sixthSense),
                StunRecoveryIntervalS = _aiSkills.At("stun_recovery_interval",
                    SkillFor(defSkills.StunRecovery, rating, roster.StunRecovery)),
                NaturalTouch = SkillFor(defSkills.NaturalTouch, rating, roster.NaturalTouch),
                // The proximity pick's roll, on the original's own class gate: only a jet rolls
                // (FUN_0041d9f0, 0x0041da07 reads +0x67c). A wingman escort therefore keeps its
                // station under fire.
                DaredevilChance = defStats.VehicleMode is null
                    || defStats.VehicleMode.Equals(VehicleDefs.JetMode, StringComparison.OrdinalIgnoreCase)
                    ? _aiSkills.At("daredevil_chance",
                        SkillFor(defSkills.DareDevil, rating, roster.DareDevil))
                    : 0f,
                Library = _maneuvers,
                AssistEnabled = !_policy.NoAssist,
            };
            if (_policy.NoAssist && !_noAssistLogged)
            {
                _noAssistLogged = true;
                Log.Info("flight", $"ai assist: off (--no-assist): lay off disabled, pursue only");
            }
        }
        catch (Exception e)
        {
            GD.PushWarning($"ai: no mode machine, cannot load skills/maneuvers: {e.Message}");
        }
    }

    // The scheme the AI def authors for itself, which is what the original's spawn resolves against
    // (docs/org/paint.md). Yields to --paint=, which is about this run, not about who the plane is.
    // ⚠ A NAMED militia def does not yield to ShippedSkins: that reading withholds only the player
    // militia's default pattern from another team, and gating the def's own livery on it left every
    // campaign enemy and every generator launch in bare skins. A spawn naming no def keeps yielding.
    // ⚠ A LiveryDef (bswingman on a Fury) outranks the base def standing in for the stats.
    private PaintScheme? MilitiaScheme(PlaneStats stats, AiSpawn spawn)
    {
        if (_liveries.PaintRequested || (spawn.LiveryDef ?? stats.AiDefName) is not { } def
            || (spawn.ShippedSkins && spawn.AiDef == null && spawn.LiveryDef == null))
            return null;
        var scheme = _liveries.DefScheme(_aircraft.ZrdrPath, def);
        if (_liveryDefsLogged.Add(def))
        {
            // Once per def, not per spawn: a mission fields fourteen of one militia variant.
            // A def authoring none is the Balmoral and the Broadway Bomber's masks, which ship
            // under no def at all, so there are no colours to fill them with and none are invented.
            if (scheme != null)
            {
                Log.Info("world", $"[paint] ai def '{def}' wears its authored '{scheme.Pattern}'");
            }
            else
            {
                Log.Info("world", $"[paint] ai def '{def}' authors no paint_pattern, so its aircraft fly the shipped skins");
            }
        }
        return scheme;
    }

    // A marker label as the readout prints it. An unresolved key would be printed verbatim by a
    // readout and is wrong on a marker, so it reads as no label at all; so does a table-less rig.
    private string? MarkerLabel(string? key)
    {
        if (string.IsNullOrWhiteSpace(key) || _aircraft.WeaponMessages is not { } messages)
            return null;
        string text = messages.Get(key).Trim();
        return text.Length == 0 || text.StartsWith("MSG_", StringComparison.Ordinal) ? null : text;
    }

    public readonly record struct AssemblyState(AiGunner? Gunner, AiRocketeer? Rocketeer,
        AiModeMachine? Machine, float? AttackRange, float? ReturnRange, ulong PaintRngState,
        ulong AiRngState, ulong SpawnRngState, AiSkills? Skills,
        System.Collections.Generic.List<Maneuver>? Maneuvers, bool NoAssistLogged);
}
