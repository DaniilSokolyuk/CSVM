using System;
using System.Collections.Generic;
using System.Linq;
using CSVM.Flight.Ai;
using CSVM.Flight.Airframe;
using CSVM.Flight.Audio;
using CSVM.Flight.Camera;
using CSVM.Flight.Weapons;
using CSVM.Mech3;
using CSVM.Session.Campaign;
using CSVM.Session.InstantAction;
using CSVM.Session.Objectives;
using CSVM.Session.Roster;
using CSVM.Session.World;
using CSVM.Utils;
using Godot;

namespace CSVM.Testing;

/// <summary>A <c>mode ship</c> hull's own gun: the acquisition, the mount and the fire decision a
/// patrol boat runs in the original, against C1B/M03's four authored boats, and whether a hull is
/// heard firing, against C5/M01, the one mission placing both a boat and a turret truck. Each runs
/// with one hand-built hostile aeroplane to shoot at; the aircraft half of the roster stays unbuilt,
/// so the only aeroplanes in the world are the ones these suites place.</summary>
internal static class SurfaceVehicleGunSuites
{
    private const string BoatChapter = "C1B";
    private const string BoatMission = "M03";
    private const float StepDt = 1f / 30f;

    // The one mission that places both hull classes, so one world answers the voice question for
    // a patrol boat and a turret truck at once.
    private const string VoiceChapter = "C5";
    private const string VoiceMission = "M01";

    // wep_29's own LOOPED_SOUND_NAME, the cue both surface defs therefore carry.
    private const string HullCue = "snd_turretgun";

    // The margin the sound manager leaves over a definition's audible distance before it silences
    // the voice (docs/formats/turrets.md). Held here as the decode's own figure rather than read
    // off GunVoice, so a build that culls at the RANGE pair itself fails this suite.
    private const float CullMargin = 1.1f;

    // snd_turretgun's own authored audible distance, where the decoded law stands at its edge level
    // and the old inverse-distance mapping's MaxDistance fade reached exactly zero.
    private const float GainProbeMetres = 400f;

    // Long enough for the mount to slew onto any bearing (it closes 1/7.5 of what is left per
    // step at this dt) and for several 0.3 s refire intervals to come round.
    private const int FiringSteps = 150;

    // One whole refire interval and a little over: the window the voice must hold unbroken.
    private const int RefireWindowSteps = 12;

    // Far enough past the turret path's half-second lease that the two answers cannot be confused.
    private const int LeaseContrastSteps = 15;

    // Inside the def's 1-500 m window and a few degrees up: the case that must fire.
    private static readonly Vector3 InRange = new(280f, 40f, 0f);

    // 464 m off the beam: past the hull's 400 m attack radius, inside both the 2500 m activation
    // and the def's own 500 m weapon window, so only the attack cylinder can refuse it.
    private static readonly Vector3 PastAttackRadius = new(460f, 60f, 0f);

    // 60 degrees up at 300 m: inside every range gate and outside the mount's +30 degree
    // elevation ceiling, so the guard costs the shot its aim quality.
    private static readonly Vector3 Overhead = new(0f, 260f, -150f);

    [Suite("surface-vehicle-guns",
        "a mode ship hull fires the weapon its own def authors (BL-687): C1B/M03's four "
        + "patrolboat blocks each build a gun from the def's wep_29 tuple with 9000 rounds and "
        + "a muzzle on the model's own turret>gun>firepoint marker rather than the hull origin; "
        + "with only hulls in the world no boat acquires anything, so a boat never shoots a "
        + "boat; against one hostile aeroplane 280 m off the beam a boat acquires it, traverses "
        + "both mount nodes off their rest pose, opens fire and spends rounds without damaging "
        + "itself; the ranking runs on the hull's own 400 m ATTACK radius, the def record default "
        + "both surface defs take, not their 2500 m activation, so the boat refuses an aeroplane "
        + "464 m off its beam that the def's 500 m weapon window would still admit and takes the "
        + "same aeroplane back inside that radius, firing; and it holds fire against an aeroplane "
        + "on its own team and against one 60 degrees overhead where the mount's elevation guard "
        + "costs more aim quality than the gun's gate allows")]
    internal static void SurfaceVehicleGuns(TestContext ctx)
    {
        ctx.RequireData(ctx.ZrdrPath, $"zrdr archive");
        string missionZrdr = SessionPaths.MissionZrdr(ctx.DataRoot, BoatChapter, BoatMission);
        string chapterZrdr = SessionPaths.ChapterZrdr(ctx.DataRoot, BoatChapter);
        string texturesPath = SessionPaths.ChapterTextures(ctx.DataRoot, BoatChapter);
        ctx.RequireData(missionZrdr, $"{BoatChapter}/{BoatMission} zrdr");
        ctx.RequireData(chapterZrdr, $"{BoatChapter} zrdr");
        ctx.RequireData(texturesPath, $"{BoatChapter} textures");

        var mission = MissionOf(ctx, BoatChapter, BoatMission);
        var script = ObjectiveScript.Load(missionZrdr);
        var defs = VehicleDefs.Load(ctx.ZrdrPath);
        var skills = AiSkills.Load(ctx.ZrdrPath);
        var weapons = WeaponDefs.Load(ctx.ZrdrPath, null);
        var director = CampaignDirector.Create(script, mission, CampaignProfileDef.NewProfile("Guns"), null);
        var planesGamez = GameZ.Load(ctx.PlanesGamezPath);

        var textures = new TextureArchive(texturesPath);
        ProjectilePool? pool = null;
        try
        {
            ctx.WithWorld(BoatChapter, collision: false, BoatMission, world =>
            {
                var worldRoot = world.Runtime.WorldRoot ?? world.Stage;
                var live = new ProjectilePool(textures, null, null);
                pool = live;
                ctx.Host.AddChild(live);

                var vessels = new SurfaceVehicleRuntime(world.Gamez, world.Session.Builder.Scene,
                    world.Runtime, defs, worldRoot)
                {
                    Projectiles = live,
                    Weapons = weapons,
                };
                live.SurfaceVehicles = vessels;
                worldRoot.AddChild(vessels);
                director.BuildRoster(new CampaignDirector.RosterInputs
                {
                    ChapterZrdrPath = chapterZrdr,
                    MissionZrdrPath = missionZrdr,
                    ZrdrPath = ctx.ZrdrPath,
                    MinAiActiveDist = skills.MinAiActiveDist,
                    FindNodes = name => world.Runtime.FindNodes(name),
                    Spawn = (_, _, _, _) => null,   // the aircraft half stays unbuilt on purpose
                    SpawnSurface = (plan, pos, forward) => vessels.Spawn(plan, pos, forward),
                    Rng = new Random(1),
                });
                foreach (var hull in director.Vessels.Values)
                {
                    hull.Wake();
                }

                var boats = director.Vessels.Values.ToList();
                ctx.Check(boats.Count > 0, $"{BoatChapter}/{BoatMission} builds hulls to arm: {boats.Count}");
                if (boats.Count == 0)
                {
                    return;
                }

                // ---- the fit and the mount ---------------------------------------------------
                int armed = boats.Count(b => b.Gunner != null);
                ctx.Same(boats.Count, armed, $"every hull builds a gun from its def's weapons block");
                var boat = boats[0];
                if (boat.Gunner is not { } gun)
                {
                    return;
                }
                ctx.Same(9000, gun.Ammo, $"the magazine is the def's authored round count, not a default");
                float muzzleOffset = gun.MuzzlePosition.DistanceTo(boat.Position);
                ctx.Check(muzzleOffset > 0.5f,
                    $"the muzzle is the model's own firepoint marker, {muzzleOffset:0.##} m off the hull origin");
                ctx.Check(Mathf.IsEqualApprox(gun.AttackRadius, VehicleDefs.DefaultAttackRadiusM),
                    $"the ranking runs on the hull's ATTACK radius, {gun.AttackRadius:0} m, the def record default its block and net both leave unauthored, and not the def's 2500 m activation");

                // ---- a boat does not shoot a boat --------------------------------------------
                // Four hulls and nothing else: the vehicle pool is non-empty and every gunner still
                // finds nothing, since the acquisition drops non-aircraft vehicle candidates.
                var scan = new AimCandidateSet();
                live.CollectVehicleList(scan);
                ctx.Same(boats.Count, scan.Vehicles.Count, $"the pool's VehicleList is the hulls alone");
                Step(boats, 30);
                int lockedOnAHull = boats.Count(b => b.Gunner?.Target is SurfaceVehicle);
                ctx.Same(0, lockedOnAHull, $"no boat targets another boat");
                ctx.Same(0, boats.Sum(b => b.Gunner?.ShotsFired ?? 0),
                    $"and with nothing hostile in the world, no boat has fired");

                // ---- against a hostile aeroplane ---------------------------------------------
                int hullTeam = boat.Team ?? AimAssist.NeutralTeam;
                ctx.Check(hullTeam != AimAssist.NeutralTeam,
                    $"the hull carries its block's team, so the gate can admit a target: {hullTeam}");
                var rest = gun.MountNodes;
                var turretRest = rest.Turret.Transform;
                var gunRest = rest.Gun.Transform;
                float healthBefore = boat.Health ?? 0f;

                var quarry = Rig(ctx, planesGamez, textures, live, 0, boat.Position + InRange,
                    AimAssist.PlayerTeam);
                Step(boats, FiringSteps);

                ctx.Check(ReferenceEquals(gun.Target, quarry),
                    $"the boat acquires the hostile aeroplane at {boat.Position.DistanceTo(quarry.WorldPosition):0} m");
                ctx.Check(rest.Turret.Transform != turretRest || rest.Gun.Transform != gunRest,
                    $"the mount's own turret and gun nodes traversed off their rest pose");
                ctx.Check(gun.ShotsFired > 0, $"the boat opened fire: {gun.ShotsFired} round(s)");
                ctx.Same(9000 - gun.ShotsFired, gun.Ammo, $"each round comes off the authored magazine");
                ctx.Check(Mathf.IsEqualApprox(healthBefore, boat.Health ?? 0f),
                    $"the hull took no damage from its own muzzle sitting on it: {boat.Health:0.##} of {healthBefore:0.##}");

                // ---- the holds ---------------------------------------------------------------
                // The team case first: it ends the 20 s hold, and the attack radius below has to be
                // read on a FRESH acquisition, since a held target stays held wherever it flies to.
                quarry.Team = hullTeam;
                CheckHold(ctx, boats, gun, "an aeroplane on the boat's own team",
                    expectTarget: false);

                // ---- the attack cylinder -----------------------------------------------------
                quarry.Team = AimAssist.PlayerTeam;
                Place(quarry, boat.Position + PastAttackRadius);
                CheckHold(ctx, boats, gun,
                    $"an aeroplane {boat.Position.DistanceTo(quarry.WorldPosition):0} m out, past the hull's {VehicleDefs.DefaultAttackRadiusM:0} m attack radius",
                    expectTarget: false);

                // Back inside it, asserting the ROUNDS: a gun that had simply stopped working
                // would pass the refusal above, and a hull whose radius resolved to zero would
                // refuse everything and read as a well-behaved gunner.
                int beforeRetake = gun.ShotsFired;
                Place(quarry, boat.Position + InRange);
                Step(boats, FiringSteps);
                ctx.Check(ReferenceEquals(gun.Target, quarry) && gun.ShotsFired > beforeRetake,
                    $"…and takes the same aeroplane back inside it at {boat.Position.DistanceTo(quarry.WorldPosition):0} m, firing {gun.ShotsFired - beforeRetake} round(s)");

                Place(quarry, boat.Position + Overhead);
                CheckHold(ctx, boats, gun, "an aeroplane above the mount's +30 degree elevation guard",
                    expectTarget: true);

                ctx.Note($"a patrolboat acquires, traverses and fires its own wep_29 ({gun.ShotsFired} rounds) inside its {VehicleDefs.DefaultAttackRadiusM:0} m attack radius, and refuses a target past it");
            });
        }
        finally
        {
            if (pool != null && GodotObject.IsInstanceValid(pool))
            {
                pool.Free();
            }

            textures.Dispose();
        }
    }

    [Suite("surface-gun-voices",
        "a mode ship hull is heard firing (BL-820): over C5/M01, which places both classes, a "
        + "patrol boat and a turret truck each build their own positional snd_turretgun emitter "
        + "from the armed weapon's LOOPED_SOUND_NAME rather than a SOUNDS.CANNON lease, levelled "
        + "per frame by the decoded RANGE law over that definition's own 400 m and culled at 1.1 "
        + "times it, the margin the sound "
        + "manager leaves over the RANGE pair, and placed at the hull origin the engine hands its "
        + "sound slot rather than at the firepoint the rounds leave; a hull is silent with nothing "
        + "to shoot at, sounds by the frame its first round leaves, holds the loop unbroken "
        + "across every one of the def's 0.3 s refire intervals while the target is held, because "
        + "the fire decision renews the voice per TICK ahead of the refire timer, and is still "
        + "heard from just past that audible distance while being silenced past the cull; at 400 m "
        + "it stands at the level SoundFalloff gives that pair, where the old inverse-distance "
        + "mapping's MaxDistance fade reached zero; and on a "
        + "lease of zero it goes quiet within two steps of the pass that stops selecting the gun, "
        + "where the turret path's half-second lease would still be sounding")]
    internal static void SurfaceGunVoices(TestContext ctx)
    {
        ctx.RequireData(ctx.ZrdrPath, $"zrdr archive");
        ctx.RequireData(ctx.SoundsPath, $"sound archive (soundsh)");
        ctx.RequireData(ctx.PlanesGamezPath, $"planes gamez");
        string missionZrdr = SessionPaths.MissionZrdr(ctx.DataRoot, VoiceChapter, VoiceMission);
        string chapterZrdr = SessionPaths.ChapterZrdr(ctx.DataRoot, VoiceChapter);
        string texturesPath = SessionPaths.ChapterTextures(ctx.DataRoot, VoiceChapter);
        ctx.RequireData(missionZrdr, $"{VoiceChapter}/{VoiceMission} zrdr");
        ctx.RequireData(chapterZrdr, $"{VoiceChapter} zrdr");
        ctx.RequireData(texturesPath, $"{VoiceChapter} textures");

        // Every distance below is the cue's own authored one. This suite reads at 1, not at the
        // reach the session ships.
        using var authored = TestContext.AtAuthoredSoundRadii();

        var mission = MissionOf(ctx, VoiceChapter, VoiceMission);
        var script = ObjectiveScript.Load(missionZrdr);
        var defs = VehicleDefs.Load(ctx.ZrdrPath);
        var skills = AiSkills.Load(ctx.ZrdrPath);
        var weapons = WeaponDefs.Load(ctx.ZrdrPath, null);
        var soundDefs = SoundDefs.Load(ctx.ZrdrPath);
        soundDefs.TryGetValue(HullCue, out var cue);
        ctx.Check(cue is { Is3D: true, Looped: true },
            $"{HullCue} is authored 3D and LOOPED, so a hull's voice is a point in the world");
        if (cue == null)
        {
            return;
        }

        var director = CampaignDirector.Create(script, mission, CampaignProfileDef.NewProfile("Voices"), null);
        var planesGamez = GameZ.Load(ctx.PlanesGamezPath);
        var textures = new TextureArchive(texturesPath);
        using var archive = new SoundArchive(ctx.SoundsPath);
        var ears = new List<Vector3> { Vector3.Zero };
        var rigs = new List<FlightController?>();
        ProjectilePool? pool = null;
        Node3D? voiceHome = null;
        try
        {
            ctx.WithWorld(VoiceChapter, collision: false, VoiceMission, world =>
            {
                var worldRoot = world.Runtime.WorldRoot ?? world.Stage;
                var live = new ProjectilePool(textures, archive, soundDefs);
                pool = live;
                ctx.Host.AddChild(live);
                // The world's own sound node stands in as the emitter home, as GameSession hands
                // WorldRuntime.Sounds: a hull's voice never goes inside the subtree it fires from.
                voiceHome = new Node3D { Name = "hull_voice_home" };
                ctx.Host.AddChild(voiceHome);

                var vessels = new SurfaceVehicleRuntime(world.Gamez, world.Session.Builder.Scene,
                    world.Runtime, defs, worldRoot)
                {
                    Projectiles = live,
                    Weapons = weapons,
                    Voices = new GunVoiceHome(voiceHome, archive, soundDefs,
                        () => (IReadOnlyList<Vector3>)ears),
                };
                live.SurfaceVehicles = vessels;
                worldRoot.AddChild(vessels);
                director.BuildRoster(new CampaignDirector.RosterInputs
                {
                    ChapterZrdrPath = chapterZrdr,
                    MissionZrdrPath = missionZrdr,
                    ZrdrPath = ctx.ZrdrPath,
                    MinAiActiveDist = skills.MinAiActiveDist,
                    FindNodes = name => world.Runtime.FindNodes(name),
                    Spawn = (_, _, _, _) => null,   // the aircraft half stays unbuilt on purpose
                    SpawnSurface = (plan, pos, forward) => vessels.Spawn(plan, pos, forward),
                    Rng = new Random(1),
                });
                foreach (var hull in director.Vessels.Values)
                {
                    hull.Wake();
                }

                var hulls = director.Vessels.Values.ToList();
                var boat = hulls.FirstOrDefault(h => h.Def == "patrolboat");
                var truck = hulls.FirstOrDefault(h => h.Def == "t_truck");
                ctx.Check(boat != null && truck != null,
                    $"{VoiceChapter}/{VoiceMission} places both hull classes: {hulls.Count} hull(s), boat={boat?.Name ?? "-"} truck={truck?.Name ?? "-"}");
                if (boat == null || truck == null)
                {
                    return;
                }
                ctx.Check(hulls.All(h => h.Gunner?.Voice != null),
                    $"every hull's gun builds a voice ({hulls.Count(h => h.Gunner?.Voice != null)} of {hulls.Count})");

                // Every hull forced hostile up front: which way C5/M01's own record points is the
                // surface-vehicle-guns suite's question, and a hull left on the record's side would
                // make the silence check below pass for a reason this suite is not testing.
                foreach (var h in hulls)
                {
                    h.Team = InstantActionRuntime.EnemyTeam;
                }
                Step(hulls, 2);
                ctx.Check(hulls.All(h => h.Gunner?.Voice is { Sounding: false }),
                    $"with no aeroplane in the world yet, every hull is silent");

                rigs.Add(CheckHullVoice(ctx, hulls, boat, cue, planesGamez, textures, live, ears, 0));
                rigs.Add(CheckHullVoice(ctx, hulls, truck, cue, planesGamez, textures, live, ears, 1));
                ctx.Note($"a patrolboat and a t_truck each sound {HullCue} from their own hull, audible {cue.RangeMax:0} m, cull {cue.RangeMax * CullMargin:0} m, lease {SurfaceGunner.VoiceLeaseSeconds:0.00} s renewed per tick");
            });
        }
        finally
        {
            foreach (var rig in rigs)
            {
                rig?.Free();
            }

            if (pool != null && GodotObject.IsInstanceValid(pool))
            {
                pool.Free();
            }

            voiceHome?.Free();
            textures.Dispose();
        }
    }

    // One hull's whole voice lifecycle, against a hostile aeroplane placed off its own beam. The
    // quarry is returned rather than freed: it is put back on the hulls' own side first, which is
    // what keeps it out of the next hull's acquisition without unregistering it from the pool.
    private static FlightController? CheckHullVoice(TestContext ctx, List<SurfaceVehicle> hulls,
        SurfaceVehicle hull, SoundDef cue, GameZ planesGamez, TextureArchive textures,
        ProjectilePool live, List<Vector3> ears, int index)
    {
        if (hull.Gunner is not { Voice: { } voice } gun)
        {
            ctx.Check(false, $"{hull.Name} carries a gun with a voice");
            return null;
        }

        string what = $"{hull.Def} '{hull.Name}'";
        var emitter = voice.Emitter();
        ctx.Check(emitter.Name == HullCue && Mathf.IsEqualApprox(emitter.RangeMax, cue.RangeMax)
            && Mathf.IsEqualApprox(emitter.Cull, cue.RangeMax * CullMargin),
            $"{what} voices {emitter.Name}, attenuated over its own authored {emitter.RangeMax:0} m and culled at {emitter.Cull:0} m, the {CullMargin:0.0}x of it the sound manager allows");
        TurretVoiceSuites.CheckPlayers(ctx, voice);

        ears[0] = hull.Position;
        var quarry = Rig(ctx, planesGamez, textures, live, index, hull.Position + InRange,
            AimAssist.PlayerTeam);
        int sounded = -1;
        for (int i = 0; i < FiringSteps && gun.ShotsFired == 0; i++)
        {
            Step(hulls, 1);
            if (sounded < 0 && voice.Sounding)
            {
                sounded = i;
            }
        }
        ctx.Check(gun.ShotsFired > 0 && voice.Sounding,
            $"{what} opened fire ({gun.ShotsFired} round(s)) and is sounding on that frame");
        if (gun.ShotsFired == 0)
        {
            return quarry;
        }
        // At or ahead of the shot, never behind it: the fire decision renews the loop as soon as
        // the gun is selected, which is while the mount is still slewing onto the lead.
        ctx.Check(sounded >= 0,
            $"…having started at step {sounded} of the {FiringSteps}, at or before that round");

        // The hold across the refire: 0.3 s is nine steps at this dt, so a voice restarted per
        // round rather than leased would show a silent frame somewhere in this window.
        int gaps = 0;
        int before = gun.ShotsFired;
        for (int i = 0; i < RefireWindowSteps; i++)
        {
            Step(hulls, 1);
            if (!voice.Sounding)
            {
                gaps++;
            }
        }
        ctx.Same(0, gaps,
            $"…and holds unbroken across {RefireWindowSteps} steps ({RefireWindowSteps * StepDt:0.00} s, {gun.ShotsFired - before} round(s)), the def's 0.3 s refire included");

        var emitterAt = voice.Emitter();
        ctx.Check(emitterAt.Position.DistanceTo(hull.Position) < 0.01f
            && emitterAt.Position.DistanceTo(gun.MuzzlePosition) > 0.5f,
            $"…from the hull origin, {emitterAt.Position.DistanceTo(gun.MuzzlePosition):0.##} m off the firepoint the rounds leave");

        // The cull, moved by the listener rather than by the hull. Both placements are past the
        // distance the definition calls audible and only the far one is past the margin over it,
        // so a build culling at the RANGE pair itself falls silent on the near one.
        ears[0] = emitterAt.Position + new Vector3(cue.RangeMax * 1.05f, 0f, 0f);
        Step(hulls, 1);
        float justOut = voice.Emitter().Position.DistanceTo(ears[0]) / cue.RangeMax;
        ctx.Check(voice.Sounding && justOut > 1f && justOut < CullMargin,
            $"…still heard from {justOut:0.00}x the {cue.RangeMax:0} m the definition calls audible, inside the {emitterAt.Cull:0} m cull");
        ears[0] = emitterAt.Position + new Vector3(cue.RangeMax * 1.15f, 0f, 0f);
        Step(hulls, 1);
        float wellOut = voice.Emitter().Position.DistanceTo(ears[0]) / cue.RangeMax;
        ctx.Check(!voice.Sounding && wellOut > CullMargin,
            $"…and silent from {wellOut:0.00}x it, past that {CullMargin:0.0}x cull");
        ears[0] = hull.Position;
        Step(hulls, 1);
        ctx.Check(voice.Sounding, $"…and sounds again with the ear back at the hull");
        TurretVoiceSuites.CheckGain(ctx, voice, cue, ears, GainProbeMetres, n => Step(hulls, n));
        ears[0] = hull.Position;
        Step(hulls, 1);

        // The far end. The lease is ZERO, so one pass that does not select the gun ends it: the
        // first tick spends the renewal it already had and the next stops the player.
        quarry.Team = InstantActionRuntime.EnemyTeam;
        Step(hulls, 2);
        bool quietAtTwo = !voice.Sounding;
        Step(hulls, LeaseContrastSteps);
        ctx.Check(quietAtTwo && !voice.Sounding,
            $"…and goes quiet within {2 * StepDt:0.00} s of the gun losing its target, where a half-second lease would still be sounding");
        return quarry;
    }

    // Steps every hull's gunner directly rather than the hull itself: the net follower would walk
    // the boats away from the geometry each case is built around, and the gun is what is under test.
    private static void Step(List<SurfaceVehicle> boats, int steps)
    {
        for (int i = 0; i < steps; i++)
        {
            foreach (var boat in boats)
            {
                boat.Gunner?.Step(StepDt);
            }
        }
    }

    // One hold case: run the gunner long enough for several refire intervals and assert nothing
    // left the barrel, saying whether the target should still have been ACQUIRED. The distinction
    // matters, a gate that silently stopped acquiring would pass a shots-only check for the
    // wrong reason.
    private static void CheckHold(TestContext ctx,
        List<SurfaceVehicle> boats, SurfaceGunner gun,
        string what, bool expectTarget)
    {
        int before = gun.ShotsFired;
        Step(boats, FiringSteps);
        ctx.Same(before, gun.ShotsFired, $"the boat holds fire against {what}");
        if (expectTarget)
        {
            ctx.Check(gun.Target != null, $"and still acquires it: {what} stays a target");
        }
        else
        {
            ctx.Check(gun.Target == null, $"and drops it entirely: {what} is not a target");
        }
    }

    // ⚠ Through WarpTo, never the node's own transform: a rig's WorldPosition is its flight
    // model's, and the acquisition reads that, so writing GlobalPosition moves the drawn plane
    // and leaves the target exactly where it was.
    private static void Place(FlightController rig, Vector3 at) => rig.WarpTo(at, 0f, 0f);

    // A hostile aeroplane for the boat to shoot at, registered on the pool so it reaches the
    // VehicleList the acquisition walks. The manual-rig shape CampaignSuites and CombatSuites use.
    private static FlightController Rig(TestContext ctx, GameZ planesGamez, TextureArchive textures,
        ProjectilePool live, int index, Vector3 at, int team)
    {
        var stats = PlaneStats.Load(ctx.ZrdrPath, ctx.PlaneName);
        var model = new PlaneBuilder(planesGamez, textures).Build(ctx.PlaneName);
        var rig = new FlightController
        {
            PlaneModel = model,
            Collider = PlaneCollider.Build(model),
            Damage = stats.DestroyableParts.Count > 0 || stats.VehicleHealth is > 0f
                ? PlaneDamage.For(stats) : null,
            PlayerIndex = FlightRoster.ShooterIdBase + index,
            IsHumanPiloted = true,
            Projectiles = live,
            UseKeyboard = false,
            PadDevices = Array.Empty<int>(),
            AllowPause = false,
            Team = team,
            Name = $"Quarry{index}",
        };
        rig.AddChild(model);
        rig.Setup(new FlightModel(stats), ctx.Camera, new CamParams(), at, at + Vector3.Forward);
        ctx.Host.AddChild(rig);
        live.RegisterAircraft(rig.Body!);
        return rig;
    }

    private static CampaignMission MissionOf(TestContext ctx, string chapter, string folder)
    {
        foreach (var m in CampaignSequence.Load(ctx.ZrdrPath))
        {
            if (m.ChapterFolder.Equals(chapter, StringComparison.OrdinalIgnoreCase)
                && m.MissionFolder.Equals(folder, StringComparison.OrdinalIgnoreCase))
            {
                return m;
            }
        }

        throw new SuiteSkippedException($"{chapter}/{folder} is not in cm_sequence");
    }
}
