using System.Collections.Generic;
using System.IO;
using System.Linq;
using CSVM.Flight.Ai;
using CSVM.Flight.Airframe;
using CSVM.Flight.Audio;
using CSVM.Flight.Camera;
using CSVM.Flight.Modes;
using CSVM.Flight.Weapons;
using CSVM.Mech3;
using CSVM.Session.Campaign;
using CSVM.Session.InstantAction;
using CSVM.Session.Roster;
using CSVM.Session.World;
using CSVM.Tooling;
using CSVM.Utils;
using Godot;

namespace CSVM.Testing;

/// <summary>Suites asserting how a computer-controlled combatant behaves: pilots, mounted gunners,
/// combat voice, and the inert state they wait in.</summary>
internal static class AiSuites
{
    // The margin the sound manager leaves over a definition's audible distance before it silences
    // the voice (docs/formats/sounds.md). Held here as the decode's own figure rather than read
    // off WeaponSoundCue. A build that culls an aircraft's gun loop at the RANGE pair then fails.
    private const float VoiceCullMargin = 1.1f;

    // Where the ai-voice suite moves its listener for the death cry: past 1.1 x every voice def's
    // audible radius. The positional law would hold the line at its floor there.
    private const float FarEarMetres = 5000f;

    // The airframe the turret decode uses as its own worked example of a thirdp mount, so the
    // WA-Turret leg has a gunner to build without a chapter world's emplacements.
    private const string TurretCarrier = "player_fbrand";

    [Suite("flight-roster-transaction",
        "FlightRoster owns human and AI assembly as atomic transactions: a late second-human " +
        "failure removes external bindings, a retry commits both humans in order with complete " +
        "bindings, and a late AI failure restores pilot/RNG state and consumes no identity")]
    internal static void FlightRosterTransaction(TestContext ctx)
    {
        ctx.RequireData(ctx.PlanesGamezPath, $"planes gamez");
        ctx.RequireData(ctx.ZrdrPath, $"zrdr archive");
        string texturesPath = SessionPaths.ChapterTextures(ctx.DataRoot, "C1");
        string chapterZrdr = SessionPaths.ChapterZrdr(ctx.DataRoot, "C1");
        ctx.RequireData(texturesPath, $"C1 textures");
        ctx.RequireData(chapterZrdr, $"C1 zrdr");

        var planesGamez = GameZ.Load(ctx.PlanesGamezPath);
        var textures = new TextureArchive(texturesPath);
        var pool = new ProjectilePool(textures, null, null);
        FlightController? spawned = null;
        SubViewport? pane = null;
        ctx.Host.AddChild(pool);
        try
        {
            var spec = SessionSpec.Parse(System.Array.Empty<string>());
            bool failSecondHuman = true;
            int camParamsCalls = 0;
            var resources = new AircraftAssemblyResources
            {
                PlanesGamez = planesGamez,
                StatsFor = plane => PlaneStats.Load(ctx.ZrdrPath, plane),
                AiStatsFor = (plane, aiDef) => PlaneStats.LoadForAi(ctx.ZrdrPath, plane, aiDef),
                CamParamsFor = _ => failSecondHuman && ++camParamsCalls == 2
                    ? throw new System.InvalidOperationException("forced second human failure")
                    : new CamParams(),
                PaintRng = new RandomNumberGenerator(),
                ZrdrPath = ctx.ZrdrPath,
                StockLoadouts = StockLoadouts.Load(),
                WeaponDefs = WeaponDefs.Load(ctx.ZrdrPath, null),
                WeaponMessages = Messages.Load(ctx.MessagesPath),
                Textures = textures,
                Shakes = ShakeDefs.Load(ctx.ZrdrPath),
            };
            pane = new SubViewport();
            ctx.Host.AddChild(pane);
            var rigs = new[]
            {
                new PlayerRig { Index = 0, Camera = ctx.Camera, HudParent = pane, Viewport = pane },
                new PlayerRig { Index = 1, Camera = ctx.Camera, HudParent = pane, Viewport = pane },
            };
            var pauseState = new PauseState();
            System.Action<List<AimCandidate>> targetSource = _ => { };
            var humanRoster = new FlightRoster(FlightRosterPolicy.From(spec),
                new LiveryResolver(spec, Path.Combine(ctx.DataRoot, "extracted", "rof")),
                new WorldEffectsFactory(spec, ctx.Host, () => Vector3.Zero), ctx.Host, resources,
                new FlightWorldBindings
                {
                    Projectiles = pool,
                    Gamez = planesGamez,
                    ChapterZrdrPath = chapterZrdr,
                },
                new HumanRosterBindings
                {
                    RigCount = 2,
                    Rigs = rigs,
                    PauseState = pauseState,
                    MenuInputFor = _ => new UI.Screens.MenuInput(),
                    ExitSession = () => { },
                }, new FixedFlightStarts());
            humanRoster.SetTargetSubParts(targetSource);
            int humanChildrenBefore = ctx.Host.GetChildCount();
            bool humanThrew = false;
            try
            {
                humanRoster.BuildPlayers(rigs);
            }
            catch (System.InvalidOperationException)
            {
                humanThrew = true;
            }
            ctx.Check(humanThrew, $"a late failure in the second human reports its failure");
            ctx.Same(0, humanRoster.Humans.Count, $"failed human batch commits no members");
            ctx.Same(humanChildrenBefore, ctx.Host.GetChildCount(),
                $"failed human batch removes every world-root child it created");
            ctx.Same(0, pane.GetChildCount(),
                $"failed human batch removes its external HUD canvas");
            ctx.Check(pool.RigOfShooter(0) == null,
                $"failed human batch removes its projectile shooter registration");
            ctx.Check(rigs.All(rig => rig.Controller == null),
                $"failed human batch leaves every adopted slot unbound");

            failSecondHuman = false;
            humanRoster.BuildPlayers(rigs);
            ctx.Same(2, humanRoster.Humans.Count,
                $"successful human batch commits the complete field");
            ctx.Check(ReferenceEquals(humanRoster.Humans[0], rigs[0])
                      && ReferenceEquals(humanRoster.Humans[1], rigs[1])
                      && rigs[0].Controller?.PlayerIndex == 0
                      && rigs[1].Controller?.PlayerIndex == 1,
                $"successful human batch preserves player identity and order");
            ctx.Check(rigs.All(rig => rig.Controller?.PauseState == pauseState
                                      && rig.Controller.TargetSubParts == targetSource
                                      && rig.Controller.SmokeScreens == null),
                $"finished humans publish pause and target bindings while optional smoke stays absent");
            var humanControllers = rigs.Select(rig => rig.Controller!).ToArray();
            humanRoster.ClearMembership();
            foreach (var controller in humanControllers)
                controller.Free();

            bool failAiCommit = true;
            var roster = new FlightRoster(FlightRosterPolicy.From(spec),
                new LiveryResolver(spec, Path.Combine(ctx.DataRoot, "extracted", "rof")),
                null, ctx.Host, resources,
                new FlightWorldBindings { Projectiles = pool, Gamez = planesGamez },
                new HumanRosterBindings(), aiAssemblyFault: () =>
                {
                    if (failAiCommit)
                        throw new System.InvalidOperationException("forced late AI assembly failure");
                });
            int childrenBefore = ctx.Host.GetChildCount();
            ulong paintState = resources.PaintRng.State;
            ulong aiRngState = Utils.Rng.Stream(Utils.Rng.Ai).State;
            ulong spawnRngState = Utils.Rng.Stream(Utils.Rng.Spawn).State;
            var at = new Vector3(0f, 500f, 0f);
            var pilot = AiPilot.HoldingCourse(at, at + Vector3.Forward);
            var failedSpawn = new AiSpawn(ctx.PlaneName, at, at + Vector3.Forward, pilot);
            bool threw = false;
            try
            {
                roster.SpawnAi(failedSpawn);
            }
            catch (System.InvalidOperationException)
            {
                threw = true;
            }
            ctx.Check(threw, $"a failed AI assembly reports its failure");
            ctx.Same(0, roster.AiAircraft.Count, $"failed assembly commits no roster member");
            ctx.Same(childrenBefore, ctx.Host.GetChildCount(),
                $"failed assembly leaves no world-root child");
            ctx.Check(pilot.Gunner == null && pilot.Rocketeer == null && pilot.Machine == null,
                $"failed assembly restores the caller's pilot state");
            ctx.Check(resources.PaintRng.State == paintState
                      && Utils.Rng.Stream(Utils.Rng.Ai).State == aiRngState
                      && Utils.Rng.Stream(Utils.Rng.Spawn).State == spawnRngState,
                $"failed assembly restores the paint, AI, and spawn streams");

            failAiCommit = false;
            var position = new Vector3(0f, 500f, -100f);
            spawned = roster.SpawnAi(new AiSpawn(ctx.PlaneName, position,
                position + Vector3.Forward,
                AiPilot.HoldingCourse(position, position + Vector3.Forward)));
            ctx.Same(1, roster.AiAircraft.Count, $"successful assembly commits one member");
            ctx.Check(ReferenceEquals(spawned, roster.AiAircraft[0]),
                $"the roster exposes the committed controller");
            ctx.Check(spawned.PlayerIndex == FlightRoster.ShooterIdBase
                      && spawned.Name == $"ai1_{ctx.PlaneName}",
                $"the failed attempt consumed neither shooter id nor roster name");
            roster.ClearMembership();
            ctx.Same(0, roster.AiAircraft.Count, $"teardown clears roster membership");
        }
        finally
        {
            spawned?.Free();
            pane?.Free();
            pool.Free();
            textures.Dispose();
        }
    }

    [Suite("inert-aircraft",
        "the E10 inert state, each claim watched passing on a live aircraft first and on the " +
        "inert one AFTER activation: a plane built inert is not returned by a raycast, is " +
        "listed by the aim assist's candidate collector but not as LIVE (so a scan pointed " +
        "straight at it finds nothing), takes no damage from a round fired through it, does " +
        "not move under a sim step and is not drawn, then Activate re-homes it and every one " +
        "of those flips back")]
    internal static void InertAircraft(TestContext ctx)
    {
        ctx.RequireData(ctx.PlanesGamezPath, $"planes gamez");
        ctx.RequireData(ctx.ZrdrPath, $"zrdr archive");
        string texturesPath = SessionPaths.ChapterTextures(ctx.DataRoot, "C1");
        ctx.RequireData(texturesPath, $"C1 textures");

        var planesGamez = GameZ.Load(ctx.PlanesGamezPath);
        var weaponDefs = WeaponDefs.Load(ctx.ZrdrPath, null);
        WeaponDef? gun = weaponDefs.All.FirstOrDefault(w => w.IsGun && w.ArmorDamage is > 0f);
        ctx.Check(gun != null, $"a gun with ARMOR_DAMAGE exists in the data");
        if (gun == null)
            return;

        var textures = new TextureArchive(texturesPath);
        ProjectilePool? pool = null;
        FlightController? control = null;
        FlightController? subject = null;
        try
        {
            var live = new ProjectilePool(textures, null, null);
            pool = live;
            ctx.Host.AddChild(live);

            var spec = SessionSpec.Parse(System.Array.Empty<string>());
            var liveries = new LiveryResolver(spec, Path.Combine(ctx.DataRoot, "extracted", "rof"));
            var inputs = new AircraftAssemblyResources
            {
                PlanesGamez = planesGamez,
                StatsFor = plane => PlaneStats.Load(ctx.ZrdrPath, plane),
                AiStatsFor = (plane, aiDef) => PlaneStats.LoadForAi(ctx.ZrdrPath, plane, aiDef),
                PaintRng = new RandomNumberGenerator(),
                ZrdrPath = ctx.ZrdrPath,
                StockLoadouts = StockLoadouts.Load(),
                WeaponDefs = weaponDefs,
                Textures = textures,
                Shakes = ShakeDefs.Load(ctx.ZrdrPath),
            };
            // worldEffects null!: never dereferenced, CrashProgram/WorldScene stay null, so the
            // spawner's crash-runtime block (its only reader) is skipped.
            var spawner = new FlightRoster(FlightRosterPolicy.From(spec), liveries, null!, ctx.Host, inputs, new FlightWorldBindings { Projectiles = live, Gamez = planesGamez }, new HumanRosterBindings());

            // One scan origin, the control dead ahead on −Z, the subject 90° off it on +X, so a
            // scan aimed at either sits well outside the other's acceptance cone.
            var origin = new Vector3(0f, 500f, 0f);
            var controlPos = origin + new Vector3(0f, 0f, -300f);
            var subjectPos = origin + new Vector3(300f, 0f, 0f);
            control = spawner.SpawnAi(new AiSpawn(ctx.PlaneName, controlPos, controlPos + Vector3.Forward,
                AiPilot.HoldingCourse(controlPos, controlPos + Vector3.Forward),
                Scheme: null, Team: InstantActionRuntime.EnemyTeam));
            subject = spawner.SpawnAi(new AiSpawn(ctx.PlaneName, subjectPos, subjectPos + Vector3.Forward,
                AiPilot.HoldingCourse(subjectPos, subjectPos + Vector3.Forward),
                Scheme: null, Team: InstantActionRuntime.EnemyTeam, Inert: true));
            ctx.Check(control.Body != null && subject.Body != null && control.Damage != null
                      && subject.Damage != null,
                $"both aircraft built a collision body and per-part damage");
            if (control.Body == null || subject.Body == null
                || control.Damage == null || subject.Damage == null)
                return;
            ctx.Check(control.InPlay && !subject.InPlay,
                $"the control is in play and the inert one is not: control={control.InPlay} subject={subject.InPlay}");

            // --- the four instruments. Each takes the aircraft it is measuring and reads its LIVE
            // position, so a subject that has moved (activation re-homes it) is still measured
            // where it actually is.
            var space = live.GetWorld3D().DirectSpaceState;
            bool RayFinds(FlightController rig)
            {
                var at = rig.WorldPosition;
                var hit = space.IntersectRay(PhysicsRayQueryParameters3D.Create(
                    at + new Vector3(0f, 0f, -30f), at, CollisionLayers.WorldAndAircraft));
                return hit.Count > 0 && ReferenceEquals(hit["collider"].Obj, rig.Body);
            }

            var candidates = new AimCandidateSet();
            bool ScanFinds(FlightController rig)
            {
                candidates.Clear();
                live.CollectAircraft(candidates);
                var scan = new AimScan
                {
                    MuzzlePosition = origin,
                    Forward = (rig.WorldPosition - origin).Normalized(),
                    Team = AimAssist.PlayerTeam,   // hostile to both aircraft (team 2)
                    Speed = 500f,
                    RangeSquared = 2000f * 2000f,
                    ConeCos = Mathf.Cos(Mathf.DegToRad(10f)),
                };
                return AimAssist.Scan(scan, candidates, out var result)
                       && ReferenceEquals(result.Source, rig);
            }

            // The whole-vehicle pair, not a sum over zones: these aircraft come off FlightRoster and an
            // AI airframe is zone-less, so a parts sum reads a flat zero and no round could ever bite. It is
            // what the decoded death test reads either way.
            float Combined(FlightController rig) => rig.Damage!.WholeArmor + rig.Damage.WholeHealth;
            bool RoundBites(FlightController rig)
            {
                float before = Combined(rig);
                var at = rig.WorldPosition;
                var muzzle = new Transform3D(
                    Basis.LookingAt(Vector3.Back, Vector3.Up), at + new Vector3(0f, 0f, -20f));
                for (int tries = 0; tries < 6 && Combined(rig) >= before; tries++)
                {
                    live.Spawn(gun, muzzle, Vector3.Zero, shooterId: 0);
                    for (int i = 0; i < 20; i++)
                        live.SimStep(1f / 60f);
                    live.Clear();
                }
                return Combined(rig) < before;
            }

            bool StepMoves(FlightController rig)
            {
                var before = rig.WorldPosition;
                for (int i = 0; i < 10; i++)
                    rig.SimStep(1f / 60f);
                return rig.WorldPosition.DistanceTo(before) > 1f;
            }

            // --- the able-to-fail baseline: the live control answers YES to all four, so a NO
            // below is the inert flag and not a broken instrument.
            ctx.Check(RayFinds(control), $"baseline: a raycast returns the live control's body");
            ctx.Check(ScanFinds(control), $"baseline: an aim-assist scan returns the live control");
            ctx.Check(RoundBites(control), $"baseline: a round fired through the live control costs it HP");
            ctx.Check(StepMoves(control), $"baseline: a sim step moves the live control");
            ctx.Check(control.PlaneModel!.Visible, $"baseline: the live control is drawn");

            // --- the inert aircraft: the same four instruments, same session, all NO.
            ctx.Check(!RayFinds(subject), $"a raycast does not return the inert aircraft");
            ctx.Check(!ScanFinds(subject), $"an aim-assist scan does not return the inert aircraft");
            float inertBefore = Combined(subject);
            ctx.Check(!RoundBites(subject), $"a round fired through the inert aircraft passes through it");
            ctx.Check(Mathf.IsEqualApprox(Combined(subject), inertBefore),
                $"…and its damage pools are untouched: {Combined(subject):0.##} vs {inertBefore:0.##}");
            ctx.Check(!StepMoves(subject), $"a sim step does not move the inert aircraft");
            ctx.Check(!subject.PlaneModel!.IsVisibleInTree(), $"the inert aircraft is not drawn");

            // Trap: inert is NOT "left off the pool's roster". The plane reached RegisterAircraft like any
            // other, so the fuse and blast passes that walk that roster can see it, which is why InPlay is read
            // there; it is listed as a candidate and simply not live.
            candidates.Clear();
            live.CollectAircraft(candidates);
            var listed = candidates.Vehicles.FirstOrDefault(c => ReferenceEquals(c.Source, subject));
            ctx.Check(listed.Source != null && !listed.Live,
                $"the inert aircraft is on the pool's candidate roster but not live: listed={listed.Source != null} live={listed.Live}");

            // ⚠ Activate AT ITS BUILD POSE. A suite completes inside one _Ready and never yields a frame, so
            // a body moved here keeps its build-pose transform on the physics server and no raycast can find
            // it (INSTR-13); the re-home is asserted off the flight model instead.
            subject.Activate(subjectPos, subjectPos + Vector3.Forward);
            ctx.Check(subject.InPlay && !subject.Inert, $"Activate cleared the inert flag");
            ctx.Check(RayFinds(subject), $"the activated aircraft is returned by a raycast");
            ctx.Check(ScanFinds(subject), $"the activated aircraft is returned by an aim-assist scan");
            ctx.Check(RoundBites(subject), $"a round fired through the activated aircraft costs it HP");
            ctx.Check(StepMoves(subject), $"a sim step moves the activated aircraft");
            // In the tree, not the model's own bit: presence is written on the pivot above it, and
            // the model's bit is the airframe's archive ACTIVE state (FlightController.ApplyPresence).
            ctx.Check(subject.PlaneModel.IsVisibleInTree(), $"the activated aircraft is drawn");

            // Activation's other half: it re-homes the aircraft at the pose it is given, which is
            // how E11's wave teleport will arrive. Read off the flight model (WorldPosition is the
            // sim value) rather than the node, for the frame-flush reason above.
            var elsewhere = subjectPos + new Vector3(0f, 0f, -900f);
            subject.Activate(elsewhere, elsewhere + Vector3.Forward);
            ctx.Check(subject.WorldPosition.DistanceTo(elsewhere) < 1f,
                $"Activate re-homes the aircraft at the pose it is given pos={subject.WorldPosition}");
            float pristine = subject.Damage.WholeArmorMax + subject.Damage.WholeHealthMax;
            ctx.Check(Mathf.IsEqualApprox(Combined(subject), pristine),
                $"…with a repaired airframe, the respawn it rides on top of: {Combined(subject):0.##}/{pristine:0.##}");
        }
        finally
        {
            pool?.Free();
            control?.Free();
            subject?.Free();
            textures.Dispose();
        }
    }

    [Suite("carried-turrets",
        "a carried turret gunner (C9a) builds from ai.zrd + the vehicle def's thirdp mount, " +
        "poses at its arc centre, tracks and fires on a hostile plane inside DETECTION_RANGE " +
        "with hits landing under the host's shooter id (never on the host's own airframe), " +
        "holds fire while tracking through a bored window, parks at the NEARER yaw end stop " +
        "out of arc, treats YAW [0,0] as unrestricted rather than locked, and goes quiet with " +
        "a crashed host, plus the aim assist's turret candidate list is fed")]
    internal static void CarriedTurrets(TestContext ctx)
    {
        ctx.RequireData(ctx.PlanesGamezPath, $"planes gamez");
        ctx.RequireData(ctx.ZrdrPath, $"zrdr archive");
        string texturesPath = SessionPaths.ChapterTextures(ctx.DataRoot, "C1");
        ctx.RequireData(texturesPath, $"C1 textures");

        var planesGamez = GameZ.Load(ctx.PlanesGamezPath);
        var weapons = WeaponDefs.Load(ctx.ZrdrPath, null);
        var turretDefs = TurretDefs.Load(ctx.ZrdrPath);
        ctx.Check(turretDefs.All.Count(d => d.Carried) == 16,
            $"the 16 carried ai.zrd entries parse carried={turretDefs.All.Count(d => d.Carried)}");

        // The Kestrel: a single rear turret (thirdp MSG_TUR_PAC_G3, PITCH [20,50], YAW
        // [105,255], the directed rear arc through 180°, DETECTION_RANGE 450, FIRE_RATE 0.4,
        // ATTACK 4 s / BORED 3 s scalars).
        const string HostPlane = "player_kestrel";
        var stats = PlaneStats.Load(ctx.ZrdrPath, HostPlane);
        ctx.Check(stats.TurretMounts.Count(m => !m.FirstPerson) == 1,
            $"{HostPlane} carries one thirdp turret mount");

        var textures = new TextureArchive(texturesPath);
        ProjectilePool? pool = null;
        FlightController? host = null;
        FlightController? target = null;
        try
        {
            var live = new ProjectilePool(textures, null, null);
            pool = live;
            ctx.Host.AddChild(live);

            FlightController BuildRig(string plane, PlaneStats st, int playerIndex, Vector3 pos)
            {
                var model = new PlaneBuilder(planesGamez, textures).Build(plane);
                var rig = new FlightController
                {
                    PlaneModel = model,
                    Collider = PlaneCollider.Build(model),
                    Damage = new PlaneDamage(st.DestroyableParts),
                    PlayerIndex = playerIndex,
                    Projectiles = live,
                    UseKeyboard = false,
                    AllowPause = false,
                    // AI rigs on purpose: the gunner is read by its victim's ledger, and the
                    // incoming-fire shield would discard the first seconds of it on a HUMAN one.
                    IsHumanPiloted = false,
                };
                rig.AddChild(model);
                // Nose on world -Z (identity attitude): plane-local == world - pos.
                rig.Setup(new FlightModel(st), ctx.Camera, new CamParams(), pos, pos + Vector3.Forward);
                ctx.Host.AddChild(rig);
                // Park at zero speed: Setup leaves the model at spawn speed, and a rig this
                // suite never steps would otherwise REPORT that velocity while standing still,
                // the turret then leads a phantom motion and every round misses.
                rig.PlaceHeld(pos, pos + Vector3.Forward);
                return rig;
            }

            var hostPos = new Vector3(0f, 500f, 0f);
            host = BuildRig(HostPlane, stats, 0, hostPos);
            host.Turrets = TurretController.BuildCarried(
                turretDefs, stats, host.PlaneModel!, weapons, host, live);
            ctx.Check(host.Turrets.Length == 1,
                $"the mount resolves against the built model turrets={host.Turrets.Length}");
            if (host.Turrets.Length != 1)
                return;
            var turret = host.Turrets[0];
            ctx.Check(turret.YawNode != null && turret.Firepoints.Length == 1,
                $"the PARTS chain resolved yaw={turret.YawNode?.Name} muzzles={turret.Firepoints.Length}");
            ctx.Check(turret.Weapon.Id == turret.Def.WeaponName,
                $"WEAPON.NAME resolved as a ballistics id ({turret.Weapon.Id})");

            // Load pose: the centre of each arc, yaw 180 (rearward), pitch 35.
            var (restYaw, restPitch) = TurretController.AnglesOfLocal(turret.BarrelLocal);
            ctx.Check(Mathf.Abs(Mathf.Wrap(restYaw - 180f, -180f, 180f)) < 0.5f
                      && Mathf.Abs(restPitch - 35f) < 0.5f,
                $"the turret poses at its arc centre yaw={restYaw:0.#} pitch={restPitch:0.#}");

            // The target: in-arc (behind and above the host, yaw ~180, elevation ~35°), inside
            // DETECTION_RANGE, on a hostile team. INACCURACY is zeroed so every gated round flies
            // the solved line, the scatter cone itself is covered by the aim-assist suite.
            turret.Def.InaccuracyDeg = 0f;
            var targetStats = PlaneStats.Load(ctx.ZrdrPath, ctx.PlaneName);
            var targetPos = hostPos + new Vector3(0f, 105f, 150f);
            target = BuildRig(ctx.PlaneName, targetStats, 1, targetPos);

            float Combined(FlightController rig) => rig.Damage!.Parts.Values.Sum(p => p.Hp + p.Armor);
            bool Pristine(FlightController rig) => rig.Damage!.Parts.Values.All(
                p => p.Hp >= p.Def.MaxHp && p.Armor >= p.Def.MaxArmor);
            void Step(int frames)
            {
                for (int i = 0; i < frames; i++)
                {
                    turret.SimStep(1f / 60f);
                    live.SimStep(1f / 60f);
                }
            }

            // The aim assist's turret candidate list is fed (the AimAssist.cs:387 seam).
            var candidates = new AimCandidateSet();
            live.CollectTurrets(candidates);
            ctx.Check(candidates.Turrets.Count == 1
                      && candidates.Turrets[0].Team == AimAssist.TeamOfPilot(host.PlayerIndex)
                      && candidates.Turrets[0].Live,
                $"CollectTurrets feeds the candidate scan count={candidates.Turrets.Count}");
            // The same list feeds the player's target pool, which takes emplacements only. A
            // carried gunner's host is already a target in its own right, so offering both would put
            // two entries on one silhouette; the discriminator is the placement Site.
            var carriedPool = new TargetPool();
            carriedPool.Rebuild(candidates, null, AimAssist.TeamOfPilot(host.PlayerIndex + 1), null);
            ctx.Check(turret.Site == null && carriedPool.Count == 0,
                $"a CARRIED turret is never selectable, though the same scan entry is a live aim-assist candidate on a hostile team pool={carriedPool.Count}");

            // --- track and fire: two seconds inside the initial 4 s attack window. The barrel
            // slews onto the target and the shots land, under the host's shooter id, so the
            // rounds crossing the host's own tail (the rear arc points across it) exclude it.
            float before = Combined(target);
            Step(120);
            var toTarget = (target.WorldPosition - turret.WorldPosition).Normalized();
            ctx.Check(turret.BarrelWorldDir.Dot(toTarget) > TurretController.FireGateCos,
                $"the barrel slewed onto the target dot={turret.BarrelWorldDir.Dot(toTarget):0.000}");
            ctx.Check(turret.ShotsFired >= 3,
                $"the turret fires through the 15° gate at its FIRE_RATE shots={turret.ShotsFired}");
            ctx.Check(Combined(target) < before,
                $"turret rounds strike the target moved={before - Combined(target):0.##}");
            ctx.Check(Pristine(host) && !host.Crashed,
                $"the host's own airframe took nothing from its own gunner");

            // --- the duty cycle: run to the bored window (ATTACK 4 s from build), then move the
            // target across the arc, firing stops, tracking does not.
            int shotsAtBored = -1;
            for (int i = 0; i < 600 && turret.Attacking; i++)
            {
                Step(1);
            }
            ctx.Check(!turret.Attacking, $"the attack window expires into bored");
            shotsAtBored = turret.ShotsFired;
            target.PlaceHeld(hostPos + new Vector3(90f, 105f, 120f), hostPos); // still in-arc, new bearing
            Step(90); // 1.5 s of the 3 s bored window
            var toMoved = (target.WorldPosition - turret.WorldPosition).Normalized();
            ctx.Check(turret.ShotsFired == shotsAtBored,
                $"bored suppresses firing shots={turret.ShotsFired} (was {shotsAtBored})");
            ctx.Check(turret.BarrelWorldDir.Dot(toMoved) > TurretController.FireGateCos,
                $"…but tracking continues through it dot={turret.BarrelWorldDir.Dot(toMoved):0.000}");

            // --- out of arc: a target ahead of the host sits outside YAW [105,255]; the turret
            // parks at the angularly NEARER end stop and never fires, whatever the duty cycle.
            target.PlaceHeld(hostPos + new Vector3(-52f, 105f, -140f), hostPos); // yaw ≈ +20°, in reach
            int shotsAtOutOfArc = turret.ShotsFired;
            Step(300); // 5 s spans at least one full attack window
            var (parkedYaw, _) = TurretController.AnglesOfLocal(turret.BarrelLocal);
            ctx.Check(turret.ShotsFired == shotsAtOutOfArc,
                $"an out-of-arc target draws no fire shots={turret.ShotsFired}");
            ctx.Check(Mathf.Abs(parkedYaw - 105f) < 1.5f,
                $"the barrel parks at the nearer end stop (105°, not 255°) yaw={parkedYaw:0.#}");

            // --- YAW [0,0] means UNRESTRICTED: with the limit spelled that way the same ahead
            // target becomes reachable and the turret opens fire, the misread ('locked forward')
            // would keep it silent forever. Pitch stays authored, so keep the target elevated.
            turret.Def.YawMinDeg = 0f;
            turret.Def.YawMaxDeg = 0f;
            Step(300);
            ctx.Check(turret.ShotsFired > shotsAtOutOfArc,
                $"YAW [0,0] removes the traverse limit shots={turret.ShotsFired} (was {shotsAtOutOfArc})");

            // --- a crashed host silences its gunner, and the candidate list reports it dead.
            host.DebugForceCrash();
            int shotsAtCrash = turret.ShotsFired;
            Step(120);
            ctx.Check(!turret.Alive && turret.ShotsFired == shotsAtCrash,
                $"a crashed host's turret goes quiet shots={turret.ShotsFired}");
            candidates.Clear();
            live.CollectTurrets(candidates);
            ctx.Check(candidates.Turrets.Count == 1 && !candidates.Turrets[0].Live,
                $"the dead turret stays listed but not live");
        }
        finally
        {
            pool?.Free();
            host?.Free();
            target?.Free();
            textures.Dispose();
        }
    }

    // The world AA emplacements against the real C1 chapter world: the NODES
    // placement census, the shipped-ACTIVATED default, the --wake-turrets stand-in, the
    // enemy-default/ally team split, the aim-assist candidate list, and the healthy-node kill
    // switch. Zeppelin-slung entries are placed (they are world nodes) and their one gameplay
    // path is checked here too: the Instant Action builder's subtree-scoped activation of the
    // objective hull's rings, both directions, plus the fire it puts on a plane alongside.
    [Suite("world-turrets",
        "the world AA emplacements (C9b) place at their NODES patterns against the real C1 " +
        "world (census pinned, one entry many turrets, scoped multi-segment paths), honour " +
        "shipped ACTIVATED (a dormant aagun holds fire with a hostile plane in range until " +
        "the --wake-turrets stand-in wakes it, then acquires and fires under its own " +
        "enemy-default team), take the Instant Action builder's subtree-scoped ACTIVATED " +
        "write on the objective zeppelin (14 rings armed and shooting back, nothing outside " +
        "the hull touched, the same call with the flag cleared stowing them again), own their " +
        "mounting SECTION as the set their own rounds may not strike, " +
        "skip same-team targets, join the aim-assist candidate list, and go permanently quiet " +
        "when the emplacement's own destructible dies")]
    internal static void WorldTurrets(TestContext ctx)
    {
        ctx.RequireData(ctx.PlanesGamezPath, $"planes gamez");
        ctx.RequireData(ctx.ZrdrPath, $"zrdr archive");
        string texturesPath = SessionPaths.ChapterTextures(ctx.DataRoot, "C1");
        ctx.RequireData(texturesPath, $"C1 textures");

        var planesGamez = GameZ.Load(ctx.PlanesGamezPath);
        var weapons = WeaponDefs.Load(ctx.ZrdrPath, null);
        var turretDefs = TurretDefs.Load(ctx.ZrdrPath);
        ctx.Check(turretDefs.All.Count(d => !d.Carried) == 26,
            $"the 26 standalone ai.zrd entries parse standalone={turretDefs.All.Count(d => !d.Carried)}");

        // BL-403: one team space, so an emplacement and an aircraft on the same authored side
        // must compare equal. The enemy default is the id an Instant Action wave carries, and the
        // gate they meet at is the same one AcquireTarget runs.
        ctx.Check(!AimAssist.Hostile(TurretDef.DefaultTeamId, InstantActionRuntime.EnemyTeam)
                  && AimAssist.Hostile(TurretDef.DefaultTeamId, AimAssist.PlayerTeam),
            $"a no-TEAM emplacement ({TurretDef.DefaultTeamId}) spares the wave and engages the player");

        // The versus band exists so a splitscreen human cannot inherit an emplacement's side.
        ctx.Check(AimAssist.TeamOfPilot(0) == AimAssist.PlayerTeam
                  && AimAssist.TeamOfPilot(1) > AimAssist.VersusTeamBand
                  && AimAssist.Hostile(TurretDef.DefaultTeamId, AimAssist.TeamOfPilot(1)),
            $"--vs pilot 1 (team {AimAssist.TeamOfPilot(1)}) is still engaged by a no-TEAM emplacement");

        // Neutral is not a wildcard, and the predicate is symmetric on both sides of it.
        ctx.Check(!AimAssist.Hostile(AimAssist.NeutralTeam, AimAssist.PlayerTeam)
                  && !AimAssist.Hostile(AimAssist.PlayerTeam, AimAssist.NeutralTeam)
                  && !AimAssist.Hostile(AimAssist.PlayerTeam, AimAssist.PlayerTeam),
            $"team 0 never shoots and is never shot, and no side is hostile to itself");

        // A private world: this suite shows multiplayer1zep, aagun32 and the piratezep that ia1.gw
        // switched off, and kills aagun32 through its destructible, which no finally can undo. A
        // shared cache would hand every later C1 suite that changed census.
        ctx.WithPrivateWorld("C1", collision: true, world =>
        {
            var textures = new TextureArchive(texturesPath);
            ProjectilePool? pool = null;
            FlightController? target = null;
            FlightController? friend = null;
            FlightController? zepBait = null;   // its own rig: the zeppelin rings shoot it to bits
            Session.World.TurretEmplacementRuntime? emplacements = null;   // a Node now: freed below
            var savedClock = Utils.GameClock.Current;
            try
            {
                var live = new ProjectilePool(textures, null, null)
                {
                    DamageSink = world.Runtime.DamageAt,
                };
                pool = live;
                ctx.Host.AddChild(live);
                // ⚠ Keep the clock stepping (INSTR-49): without it the gunner's 1-2 s line-of-sight
                // cache never expires and the woken gun rides the one cast it took at wake.
                Utils.GameClock.Current = new Utils.GameClock { Mode = Utils.GameClock.RunMode.FixedStep };
                var runtime = emplacements = new Session.World.TurretEmplacementRuntime(turretDefs, weapons,
                    (pattern, scope) => world.Runtime.FindNodes(pattern, scope), live,
                    world.Runtime.WorldRoot);

                // The placement census: one entry instantiates as many turrets as its patterns
                // match, so the counts are properties of C1's world model. Pinned as goldens.
                var byEntry = new Dictionary<string, int>();
                foreach (var t in runtime.Emplacements)
                {
                    string site = t.Label[(t.Label.IndexOf('@') + 1)..];
                    string root = site.TrimEnd("0123456789 ".ToCharArray());
                    string key = $"{t.Def.Title}:{root}";
                    byEntry.TryGetValue(key, out int had);
                    byEntry[key] = had + 1;
                }
                ctx.Note($"C1 census: {string.Join(", ", byEntry.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}={kv.Value}"))}");
                int AagunCount() => runtime.Emplacements.Count(t => t.Label.StartsWith("MSG_TUR_AAA@aagun"));
                ctx.Same(5, AagunCount(), $"C1 places the five aagun emplacements");
                ctx.Same(9, runtime.Emplacements.Count(t => t.Label.StartsWith("MSG_TUR_BALLOON_TOP@bbtur")),
                    $"C1 places the nine balloon-top emplacements");
                ctx.Same(74, runtime.Count, $"C1's whole emplacement census");
                ctx.Same(15, runtime.AwakeCount,
                    $"shipped ACTIVATED: only the piratezep's own awake rings are up (ally, TEAM 1)");
                ctx.Check(runtime.Emplacements.Where(t => t.Activated)
                        .All(t => t.Team == AimAssist.PlayerTeam),
                    $"every emplacement awake by data is on the ally team, no hostile fires unwoken");

                // A dormant hostile emplacement: aagun32, enemy by the loader's no-TEAM default.
                var aagun = runtime.Emplacements.FirstOrDefault(t => t.Label.EndsWith("@aagun32"));
                ctx.Check(aagun != null, $"aagun32 built a gunner");
                if (aagun == null)
                    return;
                ctx.Check(!aagun.Activated && AimAssist.Hostile(aagun.Team, AimAssist.PlayerTeam),
                    $"aagun32 is dormant and hostile to the player by default team={aagun.Team}");
                aagun.Def.InaccuracyDeg = 0f; // determinism: the scatter cone is the assist suite's
                // ia1.gw switches aagun32's site off at its root, and a switched-off emplacement
                // is dead before it is dormant. The .gw's own switch, reversed, stands it up so
                // the ACTIVATED gate is what the checks below read.
                ctx.Check(!aagun.Alive && aagun.Site != null,
                    $"aagun32 reads dead as ia1.gw leaves it, site={aagun.Site?.Name}");
                if (aagun.Site == null)
                    return;
                world.Runtime.SetTargetActive(aagun.Site, true);
                ctx.Check(aagun.Alive, $"…and alive once its site is switched on");

                // ⚠ Sync the space before anything here casts. The site is hidden when the world
                // joins the tree, so its colliders are disabled. Re-enabling one only QUEUES the
                // broadphase rebuild the next physics step would run.
                ctx.SyncPhysics();
                var mountSpace = ctx.Host.GetWorld3D().DirectSpaceState;
                var mountShapes = aagun.Site.FindChildren("*", "StaticBody3D", true, false)
                    .OfType<StaticBody3D>()
                    .Where(b => (b.GetParent() as Node3D)?.IsVisibleInTree() == true)
                    .SelectMany(b => b.GetChildren().OfType<CollisionShape3D>()
                        .Where(cs => !cs.Disabled).Select(cs => (Body: b, Shape: cs)))
                    .ToList();
                static int Faces(CollisionShape3D shape) =>
                    shape.Shape is ConcavePolygonShape3D mesh ? mesh.GetFaces().Length / 3 : -1;
                bool Answers((StaticBody3D Body, CollisionShape3D Shape) part)
                {
                    var box = new Aabb();
                    if (part.Shape.Shape is ConcavePolygonShape3D mesh && mesh.GetFaces() is { Length: > 0 } points)
                    {
                        box = new Aabb(points[0], Vector3.Zero);
                        foreach (var point in points)
                        {
                            box = box.Expand(point);
                        }
                    }
                    var at = part.Body.GlobalTransform * part.Shape.Transform * box.GetCenter();
                    var query = new PhysicsShapeQueryParameters3D
                    {
                        Shape = new SphereShape3D { Radius = 4f },
                        Transform = new Transform3D(Basis.Identity, at),
                        CollisionMask = CollisionLayers.World,
                    };
                    return mountSpace.IntersectShape(query, 64)
                        .Any(hit => hit["rid"].AsRid() == part.Body.GetRid());
                }
                ctx.Note($"aagun32's woken mount: {string.Join(", ", mountShapes.Select(p => $"{p.Body.Name}[{Faces(p.Shape)}]"))}");
                ctx.Same(mountShapes.Count, mountShapes.Count(Answers),
                    $"every enabled shape of the woken mount answers a query");
                var pad = mountShapes.FirstOrDefault(p => p.Body.Name.ToString() == "col_buildings");
                var pyramid = mountShapes.FirstOrDefault(
                    p => p.Body.Name.ToString() == "col" && Faces(p.Shape) == 12);
                ctx.Check(pad.Shape != null && Answers(pad) && pyramid.Shape != null && Answers(pyramid),
                    $"…the site's col_buildings pad and its 12-face col pyramid among them");

                FlightController BuildRig(string plane, int playerIndex, Vector3 pos, Vector3 look)
                {
                    var st = PlaneStats.Load(ctx.ZrdrPath, ctx.PlaneName);
                    var model = new PlaneBuilder(planesGamez, textures).Build(plane);
                    var rig = new FlightController
                    {
                        PlaneModel = model,
                        Collider = PlaneCollider.Build(model),
                        Damage = new PlaneDamage(st.DestroyableParts),
                        PlayerIndex = playerIndex,
                        Projectiles = live,
                        UseKeyboard = false,
                        AllowPause = false,
                        // AI rigs on purpose: the emplacement is read by its victim's ledger, and
                        // the incoming-fire shield discards the first seconds of it on a HUMAN one.
                        IsHumanPiloted = false,
                    };
                    rig.AddChild(model);
                    rig.Setup(new FlightModel(st), ctx.Camera, new CamParams(), pos, look);
                    ctx.Host.AddChild(rig);
                    rig.PlaceHeld(pos, look); // parked: no phantom velocity for the lead solve
                    return rig;
                }

                // A hostile plane parked inside DETECTION_RANGE (500 m), well above the fort.
                var gunPos = aagun.WorldPosition;
                var targetPos = gunPos + new Vector3(120f, 250f, 0f);
                target = BuildRig(ctx.PlaneName, 0, targetPos, targetPos + Vector3.Forward);

                void Step(int frames)
                {
                    for (int i = 0; i < frames; i++)
                    {
                        Utils.GameClock.Current?.BeginFrame(1f / 60f);
                        runtime.SimStep(1f / 60f);
                        live.SimStep(1f / 60f);
                    }
                }

                float Combined(FlightController rig) => rig.Damage!.Parts.Values.Sum(p => p.Hp + p.Armor);

                // Dormant = inert: no tracking, no fire, through several attack windows.
                var restDir = aagun.BarrelWorldDir;
                Step(240);
                ctx.Check(aagun.ShotsFired == 0 && aagun.BarrelWorldDir.IsEqualApprox(restDir),
                    $"a dormant emplacement neither tracks nor fires shots={aagun.ShotsFired}");

                // The Instant Action builder's own turret arm: a subtree-scoped ACTIVATED write over the objective
                // zeppelin's node, which is what arms multiplayer1zep's four dormant entries. Scoped and
                // reversible; the deactivation loop is the same call with the flag cleared.
                var mp1 = world.Runtime.FindNodes("multiplayer1zep");
                ctx.Same(1, mp1.Count, $"C1's world carries the Instant Action zeppelin node");
                var mp2 = world.Runtime.FindNodes("multiplayer2zep");
                ctx.Same(1, mp2.Count, $"…and the second MP zeppelin, the control for the scoping");
                if (mp1.Count > 0 && mp2.Count > 0)
                {
                    List<TurretController> RingsOn(Node3D root) => runtime.Emplacements
                        .Where(t => t.Site is { } s && (s == root || root.IsAncestorOf(s))).ToList();
                    var mp1Rings = RingsOn(mp1[0]);
                    var mp2Rings = RingsOn(mp2[0]);
                    ctx.Same(14, mp1Rings.Count,
                        $"multiplayer1zep carries 14 rings (3 nose, 3 belly, 4 left, 4 right)");
                    ctx.Check(mp1Rings.All(t => !t.Activated),
                        $"…every one of them dormant by data, which is why it flies unarmed");
                    ctx.Same(14, runtime.SetActivatedUnder(mp1[0], true),
                        $"the builder's zeppelin arm arms every ring on the objective hull");
                    // BL-403: hostile to the PLAYER, and never to the wave this hull launches,
                    // both halves, since the band made the second half impossible.
                    ctx.Check(mp1Rings.All(t => t.Activated
                            && AimAssist.Hostile(t.Team, AimAssist.PlayerTeam)
                            && !AimAssist.Hostile(t.Team, InstantActionRuntime.EnemyTeam)),
                        $"…all awake, hostile to the player, and allied with their own bay wave");
                    ctx.Check(!aagun.Activated && mp2Rings.All(t => !t.Activated),
                        $"…and nothing outside that subtree woke with it");

                    // A ring's own MOUNTING SECTION is what a round it fires owns, so its flak
                    // neither strikes nor splashes its mount. ⚠ Nothing to do with the sight line,
                    // which reads geometry by distance off the muzzle (TurretController).
                    var ring = mp1Rings[0];
                    var section = TurretController.PlatformOf(ring.Site, world.Runtime.WorldRoot);
                    ctx.Check(section != null && section != mp1[0] && mp1[0].IsAncestorOf(section)
                              && section.IsAncestorOf(ring.Site!),
                        $"a ring's mounting section is a piece OF the hull ('{section?.Name}'), never the whole hull and never just its own rig");
                    var owned = ring.PlatformColliderRids();
                    var ownMount = ring.Site!.FindChildren("*", "CollisionObject3D", true, false)
                        .OfType<CollisionObject3D>().ToList();
                    var hullBodies = mp1[0].FindChildren("*", "CollisionObject3D", true, false)
                        .OfType<CollisionObject3D>().ToList();
                    ctx.Check(ownMount.Count > 0 && ownMount.All(b => owned.Contains(b.GetRid())),
                        $"a round it fires owns its own mount: {ownMount.Count} body/bodies, the ones the muzzle sits in");
                    ctx.Check(owned.Count > ownMount.Count && owned.Count < hullBodies.Count,
                        $"…and its own section but NOT the whole hull: {owned.Count} owned of the hull's {hullBodies.Count}");

                    // What the player actually feels: an armed hull shoots back, on its own rig so its rounds do not
                    // touch the aagun figures. ⚠ Show the hull first, exactly as the Instant Action builder does:
                    // C1/IA1 hides multiplayer1zep, and a hidden hull has no live colliders to block a turret.
                    mp1[0].Visible = true;
                    ctx.SyncPhysics();
                    int ZepShots() => mp1Rings.Sum(t => t.ShotsFired);
                    // ⚠ Park the bait OUTSIDE the hull's own envelope, which is 657 m long and 136
                    // m deep. A plane placed a couple of hundred metres off one ring is inside it.
                    // Every ring then reads its own hull as cover and holds fire.
                    var envelope = new Aabb(mp1[0].GlobalPosition, Vector3.Zero);
                    foreach (var mesh in mp1[0].FindChildren("*", "MeshInstance3D", true, false).OfType<MeshInstance3D>())
                    {
                        envelope = envelope.Merge(mesh.GlobalTransform * mesh.GetAabb());
                    }
                    // Offset along the hull as well as under it: a rig looking straight up has no
                    // heading. The look basis refuses a target colinear with up.
                    var baitAt = envelope.GetCenter()
                        + new Vector3(0f, -((envelope.Size.Y * 0.5f) + 100f), 150f);
                    zepBait = BuildRig(ctx.PlaneName, 0, baitAt, envelope.GetCenter());
                    float baitBefore = Combined(zepBait);
                    Step(300);
                    ctx.Check(ZepShots() > 0,
                        $"the armed zeppelin engages a hostile plane below it shots={ZepShots()}");
                    ctx.Check(Combined(zepBait) < baitBefore,
                        $"…with rounds striking it moved={baitBefore - Combined(zepBait):0.##}");

                    zepBait.PlaceHeld(baitAt + new Vector3(0f, 0f, 20000f), baitAt);

                    ctx.Same(14, runtime.SetActivatedUnder(mp1[0], false),
                        $"the same call with the flag cleared stows them again (the b=0 arm)");
                }

                // The stand-in wakes it, explicit, counted, logged, and it engages. ⚠ BORED pinned
                // to zero first: the windows are seeded by draw order, and a 2-4 s attack window at
                // a 1.0-1.8 s fire rate can hold one shot before a 3-5 s bored window outlasts the leg.
                aagun.Def.BoredMin = 0f;
                aagun.Def.BoredMax = 0f;
                // The leg below only exercises the own-rig exclusion while the mount is solid. The
                // UNEXCLUDED cast has to read blocked first. That is the gun's own rig in the way,
                // the thing the exclusion removes and nothing else does.
                var sightSpace = ctx.Host.GetWorld3D().DirectSpaceState;
                ctx.Check(TurretController.WorldBlocksEmplacementLine(
                        sightSpace, aagun.WorldPosition, targetPos + Vector3.Up * 0.2f),
                    $"aagun32's own mount blocks the unexcluded sight-line ray");
                int woken = runtime.WakeAll();
                ctx.Same(runtime.Count - 15, woken, $"--wake-turrets stand-in wakes every dormant emplacement");
                float before = Combined(target);
                Step(360);
                var toTarget = (target.WorldPosition - aagun.WorldPosition).Normalized();
                ctx.Check(aagun.BarrelWorldDir.Dot(toTarget) > TurretController.FireGateCos,
                    $"the woken gun slewed onto the plane dot={aagun.BarrelWorldDir.Dot(toTarget):0.000}");
                ctx.Check(aagun.ShotsFired >= 2,
                    $"…and fires at its FIRE_RATE shots={aagun.ShotsFired} gate={aagun.Gate}");
                ctx.Check(Combined(target) < before,
                    $"…with rounds striking the target moved={before - Combined(target):0.##}");

                // The team gate, both halves over the piratezep's allied rings: none engages the player's own
                // plane, and the same rings do engage a hostile pane, which is the able-to-fail control. Run over
                // the whole allied population, since the per-ring arcs are the zeppelin's own frame.
                var allied = runtime.Emplacements
                    .Where(t => t.Team == AimAssist.PlayerTeam).ToList();
                ctx.Check(allied.Count > 0, $"the piratezep's allied rings exist count={allied.Count}");
                // The same .gw switch on the piratezep: its rings are dead under a hidden hull.
                foreach (var hull in world.Runtime.FindNodes("piratezep"))
                {
                    world.Runtime.SetTargetActive(hull, true);
                }
                ctx.SyncPhysics();
                ctx.Check(allied.All(t => t.Alive),
                    $"the piratezep's rings are alive once the hull is switched on alive={allied.Count(t => t.Alive)} of {allied.Count}");
                if (allied.Count > 0)
                {
                    int AlliedShots() => allied.Sum(t => t.ShotsFired);
                    var zepPos = allied[0].WorldPosition;
                    target.PlaceHeld(zepPos + new Vector3(0f, -80f, 200f), zepPos);
                    Step(240);
                    ctx.Check(AlliedShots() == 0,
                        $"every allied ring holds fire on the player's own team shots={AlliedShots()}");
                    friend = BuildRig(ctx.PlaneName, 1, zepPos + new Vector3(50f, -80f, 200f), zepPos);
                    Step(600);
                    ctx.Check(AlliedShots() > 0,
                        $"…and the same rings engage a hostile pane there shots={AlliedShots()}");
                }

                // The aim assist's turret list now carries the emplacements too, the player's
                // lock-on sees world AA, dormant or not, until it dies.
                var candidates = new AimCandidateSet();
                live.CollectTurrets(candidates);
                ctx.Same(runtime.Count, candidates.Turrets.Count,
                    $"CollectTurrets feeds every emplacement to the candidate scan");

                // The kill switch: the emplacement's own destructible dies through the weapon-damage path, its
                // healthy node hides, and the gunner goes permanently quiet. ai.zrd HEALTH is authored-but-unread;
                // the real pool is the gamez destroy def's.
                target.PlaceHeld(targetPos, targetPos + Vector3.Forward);
                int killed = ProbeRunner.TriggerDestroy(world.Runtime, "aagun32", out _);
                ctx.Check(killed > 0, $"aagun32's destructible died to weapon damage killed={killed}");
                int shotsAtDeath = aagun.ShotsFired;
                Step(240);
                ctx.Check(!aagun.Alive && aagun.ShotsFired == shotsAtDeath,
                    $"the dead emplacement goes quiet shots={aagun.ShotsFired}");
                candidates.Clear();
                live.CollectTurrets(candidates);
                ctx.Check(candidates.Turrets.Count(c => !c.Live) >= 1,
                    $"…and the candidate list reports it dead");
            }
            finally
            {
                Utils.GameClock.Current = savedClock;
                pool?.Free();
                target?.Free();
                friend?.Free();
                zepBait?.Free();
                emplacements?.Free();
                textures.Dispose();
            }
        });

        // A second chapter's census (C4: the ground AA belt, aagun/tcargun/t_truck/8igun),
        // built and freed here; placement only, no firing. b_turret sites place too.
        ctx.WithWorld("C4", collision: false, world =>
        {
            using var c4Textures = new TextureArchive(texturesPath);
            var live = new ProjectilePool(c4Textures, null, null);
            ctx.Host.AddChild(live);
            Session.World.TurretEmplacementRuntime? c4Emplacements = null;
            try
            {
                var runtime = c4Emplacements = new Session.World.TurretEmplacementRuntime(turretDefs, weapons,
                    (pattern, scope) => world.Runtime.FindNodes(pattern, scope), live,
                    world.Runtime.WorldRoot);
                var census = new List<string>();
                foreach (var g in runtime.Emplacements.GroupBy(t => t.Def.Title).OrderBy(g => g.Key))
                    census.Add($"{g.Key}={g.Count()}");
                ctx.Note($"C4 census: {string.Join(", ", census)}");
                ctx.Same(5, runtime.Emplacements.Count(t => t.Label.StartsWith("MSG_TUR_AAA@aagun")),
                    $"C4 places the five aagun emplacements");
                ctx.Same(3, runtime.Emplacements.Count(t => t.Label.StartsWith("MSG_TUR_TRAIN@tcargun")),
                    $"C4 places the three train-car guns");
                ctx.Same(1, runtime.Emplacements.Count(t => t.Label.StartsWith("MSG_TUR_8_INCH@8igun")),
                    $"C4 places the one 8-inch gun");
                ctx.Same(92, runtime.Count, $"C4's whole emplacement census");
                // The piratezep model (and its allied awake rings) is part of EVERY chapter's
                // world, the awake set is a world-model property, not a C1 fact.
                ctx.Same(15, runtime.AwakeCount, $"C4's awake set is the piratezep's own rings again");
            }
            finally
            {
                c4Emplacements?.Free();
                live.Free();
            }
        });
    }

    // An emplacement standing on a subtree the mission's own .gw script switched OFF is out of
    // the world: dead to its own tick and unranked by every gunner. C3/M03 switches the six
    // barrage balloons and their turrets off by name; C3/M02 leaves them up and is the control.
    [Suite("mission-off-turrets",
        "an emplacement whose site the mission's .gw switched OFF is out of the world: C3/M03's " +
        "six balloon turrets read dead, tick to Dead once woken, and are listed dead in the gunner " +
        "scan with no structure candidate on their canopies, while C3/M02 leaves the same six " +
        "standing and alive")]
    internal static void MissionOffTurrets(TestContext ctx)
    {
        ctx.RequireData(ctx.ZrdrPath, $"zrdr archive");
        string texturesPath = SessionPaths.ChapterTextures(ctx.DataRoot, "C3");
        ctx.RequireData(texturesPath, $"C3 textures");
        var weapons = WeaponDefs.Load(ctx.ZrdrPath, null);
        var turretDefs = TurretDefs.Load(ctx.ZrdrPath);
        const string Balloon = "MSG_TUR_DEFENSE_BALLOON@b_turret";

        foreach (var (mission, standing) in new[] { ("M02", true), ("M03", false) })
        {
            ctx.WithWorld("C3", collision: false, mission, world =>
            {
                var textures = new TextureArchive(texturesPath);
                ProjectilePool? live = null;
                Session.World.TurretEmplacementRuntime? emplacements = null;
                try
                {
                    live = new ProjectilePool(textures, null, null) { DamageSink = world.Runtime.DamageAt };
                    ctx.Host.AddChild(live);
                    emplacements = new Session.World.TurretEmplacementRuntime(turretDefs, weapons,
                        (pattern, scope) => world.Runtime.FindNodes(pattern, scope), live,
                        world.Runtime.WorldRoot);
                    ctx.Host.AddChild(emplacements);
                    var balloons = emplacements.Emplacements.Where(t => t.Label.StartsWith(Balloon)).ToList();
                    ctx.Same(6, balloons.Count, $"C3/{mission} places the six balloon turrets");
                    var sites = world.Runtime.FindNodes("b_turret*");
                    var canopies = world.Runtime.FindNodes("bont*");
                    ctx.Check(sites.Count == 6 && sites.All(n => n.Visible == standing)
                              && canopies.Count == 6 && canopies.All(n => n.Visible == standing),
                        $"C3/{mission}'s .gw leaves b_turret*/bont* {(standing ? "on" : "off")}: sites={sites.Count} canopies={canopies.Count} on={sites.Count(n => n.Visible)}/{canopies.Count(n => n.Visible)}");
                    ctx.Check(balloons.All(t => t.Alive == standing),
                        $"a balloon turret under a switched-{(standing ? "on" : "off")} site reads Alive={standing}: {string.Join(", ", balloons.Select(t => $"{t.Label}={t.Alive}"))}");
                    var scan = new AimCandidateSet();
                    live.CollectTurrets(scan);
                    var listed = scan.Turrets.Where(c => c.Source is TurretController t && t.Label.StartsWith(Balloon)).ToList();
                    ctx.Check(listed.Count == 6 && listed.All(c => c.Live == standing),
                        $"the gunner scan lists them live={standing}: {listed.Count(c => c.Live)} of {listed.Count} live");
                    foreach (var t in balloons)
                    {
                        t.SetActivated(true);
                    }
                    emplacements.SimStep(0.1f);
                    var gates = balloons.Select(t => t.Gate).Distinct().ToList();
                    ctx.Check(standing ? gates.All(g => g != TurretGate.Dead && g != TurretGate.Asleep)
                                       : gates.All(g => g == TurretGate.Dead),
                        $"an awake balloon turret ticks {(standing ? "past the alive gate" : "to Dead")}: gates={string.Join("/", gates)}");
                    var registered = world.Runtime.Destructibles.All
                        .Where(i => i.Anchor.Name.ToString().StartsWith("bont")).ToList();
                    ctx.Note($"C3/{mission} destructible pools on bont*: {registered.Count}; defs named balloon_downa*: {world.Session.Program.ByAnimName("balloon_downa*").Count}, ball_kaboom*: {world.Session.Program.ByAnimName("ball_kaboom*").Count}");
                    var structures = new AimCandidateSet();
                    structures.AddStructures(world.Runtime.Destructibles);
                    int bontListed = structures.Structures.Count(c => c.Source is DestructibleRegistry.Instance i && i.Anchor.Name.ToString().StartsWith("bont"));
                    ctx.Check(standing || bontListed == 0,
                        $"a switched-off balloon is no structure candidate either: listed={bontListed} of {registered.Count}");
                    if (!standing || sites.Count == 0 || canopies.Count == 0)
                    {
                        return;
                    }

                    // The control mission also proves the two gates directly: the same root
                    // switch the .gw applies, made by hand on one site and one canopy.
                    var site1 = sites.First(n => n.Name.ToString().EndsWith('1'));
                    var canopy1 = canopies.First(n => n.Name.ToString().EndsWith('1'));
                    world.Runtime.SetTargetActive(site1, false);
                    world.Runtime.SetTargetActive(canopy1, false);
                    var turret1 = balloons.First(t => t.Label.EndsWith("@" + site1.Name));
                    structures.Clear();
                    structures.AddStructures(world.Runtime.Destructibles);
                    int canopy1Listed = structures.Structures.Count(c => c.Source is DestructibleRegistry.Instance i && i.Anchor == canopy1);
                    ctx.Check(!turret1.Alive && canopy1Listed == 0,
                        $"switching {site1.Name} and {canopy1.Name} off by their roots kills the turret (alive={turret1.Alive}) and delists the canopy (listed={canopy1Listed})");
                    world.Runtime.SetTargetActive(site1, true);
                    world.Runtime.SetTargetActive(canopy1, true);
                }
                finally
                {
                    emplacements?.Free();
                    live?.Free();
                    textures.Dispose();
                }
            });
        }
    }

    // The C1 fort's AA guns firing past their own structures: every aagun woken ALONE, a hostile
    // plane parked low on eight bearings so the line of fire crosses the fort, and every DamageAt on
    // an aagun node attributed to the only rounds in flight, the gun's own. The trace behind the
    // shooter-exclusion rule: a gun must never take its own burst, a neighbour's burst still lands.
    [Suite("turret-self-fire",
        "C1's five aagun emplacements, each woken alone and fired at a plane parked low on " +
        "eight bearings so the line of fire crosses the fort's own structures: no gun ever " +
        "takes damage from its own rounds, whether by a muzzle-side strike on its own mount " +
        "or by its burst's splash, while a neighbour's burst still reaches it")]
    internal static void TurretSelfFire(TestContext ctx)
    {
        ctx.RequireData(ctx.PlanesGamezPath, $"planes gamez");
        ctx.RequireData(ctx.ZrdrPath, $"zrdr archive");
        string texturesPath = SessionPaths.ChapterTextures(ctx.DataRoot, "C1");
        ctx.RequireData(texturesPath, $"C1 textures");

        var planesGamez = GameZ.Load(ctx.PlanesGamezPath);
        var weapons = WeaponDefs.Load(ctx.ZrdrPath, null);
        var turretDefs = TurretDefs.Load(ctx.ZrdrPath);

        ctx.WithWorld("C1", collision: true, "M02", world =>
        {
            var textures = new TextureArchive(texturesPath);
            ProjectilePool? pool = null;
            FlightController? target = null;
            Session.World.TurretEmplacementRuntime? emplacements = null;
            var savedClock = Utils.GameClock.Current;
            try
            {
                var hits = new List<(string Victim, float Damage, Node? Struck)>();
                var live = new ProjectilePool(textures, null, null);
                pool = live;
                live.DamageSink = (node, dmg) =>
                {
                    var inst = world.Runtime.Destructibles.Resolve(node);
                    if (inst != null && AnimRuntime.NameOf(inst.Anchor).StartsWith("aagun"))
                        hits.Add((AnimRuntime.NameOf(inst.Anchor), dmg, node));
                    return world.Runtime.DamageAt(node, dmg);
                };
                ctx.Host.AddChild(live);
                var runtime = emplacements = new Session.World.TurretEmplacementRuntime(turretDefs, weapons,
                    (pattern, scope) => world.Runtime.FindNodes(pattern, scope), live,
                    world.Runtime.WorldRoot);
                var guns = runtime.Emplacements.Where(t => t.Label.StartsWith("MSG_TUR_AAA@aagun")).ToList();
                ctx.Same(5, guns.Count, $"C1 places the five aagun emplacements");
                foreach (var g in guns)
                    g.Def.InaccuracyDeg = 0f;

                var st = PlaneStats.Load(ctx.ZrdrPath, ctx.PlaneName);
                var model = new PlaneBuilder(planesGamez, textures).Build(ctx.PlaneName);
                target = new FlightController
                {
                    PlaneModel = model,
                    Collider = PlaneCollider.Build(model),
                    Damage = new PlaneDamage(st.DestroyableParts),
                    PlayerIndex = 0,
                    Projectiles = live,
                    UseKeyboard = false,
                    AllowPause = false,
                };
                target.AddChild(model);
                var start = guns[0].WorldPosition + new Vector3(0f, 200f, 300f);
                target.Setup(new FlightModel(st), ctx.Camera, new CamParams(), start, guns[0].WorldPosition);
                ctx.Host.AddChild(target);
                target.PlaceHeld(start, guns[0].WorldPosition);

                void Step(int frames)
                {
                    for (int i = 0; i < frames; i++)
                    {
                        Utils.GameClock.Current?.BeginFrame(1f / 60f);
                        runtime.SimStep(1f / 60f);
                        live.SimStep(1f / 60f);
                    }
                }

                // ⚠ Keep the clock stepping. Without it GameClock.Current?.Time never moves, the
                // gunner's 1-2 s line-of-sight cache never expires, and every bearing below rides
                // the ONE verdict the first of them took (docs/verification.md INSTR-49).
                Utils.GameClock.Current = new Utils.GameClock { Mode = Utils.GameClock.RunMode.FixedStep };
                var space = ctx.Host.GetWorld3D().DirectSpaceState;
                int selfHits = 0, neighbourHits = 0, totalShots = 0;
                foreach (var gun in guns)
                {
                    string own = gun.Label[(gun.Label.IndexOf('@') + 1)..];
                    // The muzzle against the gun's own body: a round leaving from inside its
                    // mount's collider would strike that mount on its first step.
                    var muzzle = gun.Firepoints[0].GlobalPosition;
                    var probe = space.IntersectRay(PhysicsRayQueryParameters3D.Create(
                        muzzle, muzzle + gun.BarrelWorldDir * 30f, CollisionLayers.World));
                    int bodies = gun.Site!.FindChildren("*", "CollisionObject3D", true, false).Count;
                    ctx.Note($"{own}: site visible={gun.Site.Visible} colliders={bodies} destructible={(world.Runtime.Destructibles.Resolve(gun.Site) is { } gi ? AnimRuntime.NameOf(gi.Anchor) : "-")}; muzzle probe 30 m along the barrel hits {(probe.Count > 0 ? (probe["collider"].Obj as Node)?.GetParent()?.Name : "nothing")}");

                    gun.SetActivated(true);
                    // Bearings 0-7: 300 m out and low. Bearing 8: the strafing pass, the plane
                    // hanging 12 m over the pit, so the flak's own burst falls inside its radius.
                    for (int b = 0; b < 9; b++)
                    {
                        float ang = Mathf.DegToRad(b * 45f);
                        var pos = b < 8
                            ? gun.WorldPosition + new Vector3(Mathf.Cos(ang) * 300f, 25f, Mathf.Sin(ang) * 300f)
                            : gun.WorldPosition + new Vector3(6f, 12f, 6f);
                        target.PlaceHeld(pos, pos + Vector3.Forward);
                        target.Damage!.Reset();
                        int shotsBefore = gun.ShotsFired;
                        int hitsBefore = hits.Count;
                        Step(240);
                        int shots = gun.ShotsFired - shotsBefore;
                        totalShots += shots;
                        var these = hits.Skip(hitsBefore).ToList();
                        int self = these.Count(h => h.Victim == own);
                        int others = these.Count - self;
                        selfHits += self;
                        neighbourHits += others;
                        if (these.Count > 0 || b == 0 || b == 8)
                            ctx.Note($"{own} bearing {b * 45}: shots={shots} gate={gun.Gate} target-inplay={target.InPlay} self-hits={self} neighbour-hits={others} {string.Join(" ", these.Select(h => $"{h.Victim}:-{h.Damage:0.##}@{h.Struck?.GetParent()?.Name}/{h.Struck?.Name}"))}");
                    }
                    gun.SetActivated(false);
                }
                ctx.Check(totalShots > 0, $"the woken guns fired across the sweep shots={totalShots}");
                ctx.Check(selfHits == 0,
                    $"no gun takes damage from its own rounds self-hits={selfHits} (neighbour hits {neighbourHits})");

                // The able-to-fail pair on one gun pit, the same flak dropped straight into it from
                // 15 m: owned by another gun it still kills (a rocket into a gun pit must), owned
                // by the pit's own gun it deals nothing.
                target.PlaceHeld(start + new Vector3(0f, 0f, 20000f), start);
                var pit = guns[0];
                string pitName = pit.Label[(pit.Label.IndexOf('@') + 1)..];
                var drop = new Transform3D(Basis.Identity, pit.WorldPosition + Vector3.Up * 15f);
                var dropDir = new Vector3(0.05f, -1f, 0f).Normalized(); // off the pose's up axis
                int before = hits.Count;
                live.Spawn(pit.Weapon, drop, Vector3.Zero, ProjectilePool.NoShooter, null, dropDir,
                    team: pit.Team, ownerBodies: guns[1].PlatformColliderRids());
                Step(30);
                var dropped = hits.Skip(before).Where(h => h.Victim == pitName).ToList();
                int byNeighbour = dropped.Count;
                ctx.Note($"{pitName} drop: {string.Join(" ", dropped.Select(h => $"-{h.Damage:0.##}@{h.Struck?.GetParent()?.Name}/{h.Struck?.Name}"))}");
                ctx.Check(byNeighbour > 0,
                    $"{pitName} takes a neighbour's flak dropped into its pit hits={byNeighbour}");
                // One share per world OBJECT: the pit's two mesh nodes inside the radius each take
                // one, not one per collider body (their per-surface-class halves doubled it).
                ctx.Same(dropped.Select(h => h.Struck?.GetParent()?.GetInstanceId()).Distinct().Count(), byNeighbour,
                    $"…one share per node, not per collider body shares={byNeighbour}");
                before = hits.Count;
                live.Spawn(pit.Weapon, drop, Vector3.Zero, ProjectilePool.NoShooter, null, dropDir,
                    team: pit.Team, ownerBodies: pit.PlatformColliderRids());
                Step(30);
                int byItself = hits.Skip(before).Count(h => h.Victim == pitName);
                ctx.Same(0, byItself,
                    $"…and nothing from its own flak dropped at the same spot");
            }
            finally
            {
                Utils.GameClock.Current = savedClock;
                pool?.Free();
                target?.Free();
                emplacements?.Free();
                textures.Dispose();
            }
        });
    }

    // CM07's own flak, end to end on the mission it is flown in. The mission's OBJECTIVE1 authors
    // WAKEUP_TURRETS 'aagun**', the built world holds five sites for it, and each one wakes,
    // acquires a hostile plane inside DETECTION_RANGE and fires without hurting itself. The
    // chapter's persist log is what this pins hardest: CM07 is chapter 1's FIRST mission, so the
    // engine's backwards walk finds no earlier carrier and a log holding these guns wrecked must
    // not reach them, or the whole fort opens silent.
    [Suite("c1-aa-guns",
        "CM07's own flak on the mission it is flown in: C1/M02 places five aagun emplacements, "
        + "all standing and shipped dormant, the mission's OBJECTIVE1 WAKEUP_TURRETS 'aagun**' "
        + "arms exactly those five through the world lookup and the subtree write, each one "
        + "acquires a plane parked inside DETECTION_RANGE and fires without taking its own "
        + "rounds, and a chapter 1 persist log holding all five wrecked carries NOTHING into "
        + "CM07, since it is that chapter's first mission and the engine's backwards walk finds "
        + "no earlier carrier (the same log applied with a cut that reaches it kills them all)")]
    internal static void C1AaGuns(TestContext ctx)
    {
        ctx.RequireData(ctx.PlanesGamezPath, $"planes gamez");
        ctx.RequireData(ctx.ZrdrPath, $"zrdr archive");
        string texturesPath = SessionPaths.ChapterTextures(ctx.DataRoot, "C1");
        ctx.RequireData(texturesPath, $"C1 textures");
        string missionZrdr = SessionPaths.MissionZrdr(ctx.DataRoot, "C1", "M02");
        ctx.RequireData(missionZrdr, $"C1/M02 zrdr");

        // The mission's own wake directive, read from the shipped script rather than assumed.
        var script = Session.Objectives.ObjectiveScript.Load(missionZrdr);
        var patterns = new List<string>();
        foreach (var def in script.Objectives)
            patterns.AddRange(def.WakeupTurrets);
        ctx.Check(patterns.Contains("aagun**"),
            $"C1/M02 authors WAKEUP_TURRETS 'aagun**' (found [{string.Join(", ", patterns)}])");

        // Where CM07 sits in the campaign: chapter 1's first mission, so nothing is carried in.
        var missions = CampaignSequence.Load(ctx.ZrdrPath);
        var cm07 = missions.FirstOrDefault(m => m.Campaign == 1 && m.Mission == 2);
        ctx.Same(1, cm07.Campaign, $"CM07 is a chapter 1 mission (seq {cm07.Seq})");
        int? carryThrough = CampaignSequence.PreviousInSameChapter(missions, cm07.Seq)?.Seq;
        ctx.Check(carryThrough == null,
            $"CM07 is chapter 1's first mission, so the backwards walk finds no earlier carrier (got {carryThrough})");

        var planesGamez = GameZ.Load(ctx.PlanesGamezPath);
        var weapons = WeaponDefs.Load(ctx.ZrdrPath, null);
        var turretDefs = TurretDefs.Load(ctx.ZrdrPath);

        ctx.WithWorld("C1", collision: true, "M02", world =>
        {
            var textures = new TextureArchive(texturesPath);
            ProjectilePool? pool = null;
            FlightController? target = null;
            Session.World.TurretEmplacementRuntime? emplacements = null;
            try
            {
                var hits = new List<(string Victim, float Damage)>();
                var live = new ProjectilePool(textures, null, null);
                pool = live;
                live.DamageSink = (node, dmg) =>
                {
                    var inst = world.Runtime.Destructibles.Resolve(node);
                    if (inst != null && AnimRuntime.NameOf(inst.Anchor).StartsWith("aagun"))
                        hits.Add((AnimRuntime.NameOf(inst.Anchor), dmg));
                    return world.Runtime.DamageAt(node, dmg);
                };
                ctx.Host.AddChild(live);
                var runtime = emplacements = new Session.World.TurretEmplacementRuntime(turretDefs, weapons,
                    (pattern, scope) => world.Runtime.FindNodes(pattern, scope), live,
                    world.Runtime.WorldRoot);
                var guns = runtime.Emplacements.Where(t => t.Label.StartsWith("MSG_TUR_AAA@aagun")).ToList();
                ctx.Same(5, guns.Count, $"C1/M02 places the five aagun emplacements");
                if (guns.Count == 0)
                    return;
                ctx.Check(guns.All(g => !g.Activated),
                    $"they ship dormant, as ai.zrd's ACTIVATED 0 says: awake={guns.Count(g => g.Activated)}");
                ctx.Check(guns.All(g => g.Alive),
                    $"the mission's .gw leaves every site standing: alive={guns.Count(g => g.Alive)} of {guns.Count}");

                // A chapter 1 log holding all five wrecked, written by CM07 itself: the walk cuts
                // it off, so the mission opens on the fort the .gw built.
                var log = new Session.Campaign.CampaignPersistLog();
                var wrecked = new List<Session.Campaign.PersistedObject>();
                foreach (var g in guns)
                {
                    if (world.Runtime.Destructibles.Resolve(g.Site!) is { } inst
                        && inst.Anchor.HasMeta(AnimRuntime.IndexMeta))
                    {
                        wrecked.Add(new Session.Campaign.PersistedObject((int)inst.Anchor.GetMeta(AnimRuntime.IndexMeta),
                            inst.Def.Name, inst.Anchor.Name, true, 0f));
                    }
                }

                ctx.Same(guns.Count, wrecked.Count, $"every aagun site resolves a persistable pool");
                log.Merge(cm07.Campaign, cm07.Seq, wrecked);
                ctx.Same(0, log.ApplyTo(world.Runtime, cm07.Campaign, carryThrough),
                    $"CM07's own capture carries nothing into CM07 (log holds {log.For(cm07.Campaign).Count})");
                ctx.Check(guns.All(g => g.Alive),
                    $"…so all five are still alive after the restore pass: alive={guns.Count(g => g.Alive)}");

                // The wake the objectives graph performs, through the same two primitives the
                // director uses: the world's own node lookup, then the subtree-scoped write.
                int armed = 0;
                foreach (string pattern in patterns)
                {
                    foreach (var node in world.Runtime.FindNodes(pattern))
                        armed += runtime.SetActivatedUnder(node, true);
                }

                ctx.Same(5, armed, $"WAKEUP_TURRETS '{string.Join(", ", patterns)}' arms the five aaguns");

                var st = PlaneStats.Load(ctx.ZrdrPath, ctx.PlaneName);
                var model = new PlaneBuilder(planesGamez, textures).Build(ctx.PlaneName);
                target = new FlightController
                {
                    PlaneModel = model,
                    Collider = PlaneCollider.Build(model),
                    Damage = new PlaneDamage(st.DestroyableParts),
                    PlayerIndex = 0,
                    Projectiles = live,
                    UseKeyboard = false,
                    AllowPause = false,
                };
                target.AddChild(model);
                var start = guns[0].WorldPosition + new Vector3(0f, 200f, 300f);
                target.Setup(new FlightModel(st), ctx.Camera, new CamParams(), start, guns[0].WorldPosition);
                ctx.Host.AddChild(target);
                target.PlaceHeld(start, guns[0].WorldPosition);

                void Step(int frames)
                {
                    for (int i = 0; i < frames; i++)
                    {
                        runtime.SimStep(1f / 60f);
                        live.SimStep(1f / 60f);
                    }
                }

                // Each gun in turn, the plane parked well inside DETECTION_RANGE and above the
                // fort's own skyline so the sight line is the gun's, not the terrain's.
                foreach (var gun in guns)
                {
                    string own = gun.Label[(gun.Label.IndexOf('@') + 1)..];
                    int before = gun.ShotsFired;
                    int hitsBefore = hits.Count;
                    var pos = gun.WorldPosition + new Vector3(0f, 150f, 250f);
                    target.PlaceHeld(pos, gun.WorldPosition);
                    target.Damage!.Reset();
                    Step(600);
                    int shots = gun.ShotsFired - before;
                    int self = hits.Skip(hitsBefore).Count(h => h.Victim == own);
                    ctx.Check(shots > 0,
                        $"{own} acquires the player {gun.WorldPosition.DistanceTo(pos):0} m out and fires shots={shots} gate={gun.Gate} alive={gun.Alive}");
                    ctx.Same(0, self, $"…and takes nothing from its own rounds (B13)");
                }

                // The able-to-fail half: the same log applied with a cut that DOES reach it wrecks
                // every gun, which is what a later mission of the chapter must open on.
                ctx.Same(guns.Count, log.ApplyTo(world.Runtime, cm07.Campaign, cm07.Seq),
                    $"a later mission of the chapter does carry them");
                ctx.Check(guns.All(g => !g.Alive),
                    $"…and then every aagun reads dead: alive={guns.Count(g => g.Alive)}");
            }
            finally
            {
                pool?.Free();
                target?.Free();
                emplacements?.Free();
                textures.Dispose();
            }
        });
    }

    // The AI actor seam against real engine state on manual sim steps. A human rig is built and stepped
    // first, so the AI plane demonstrably joins a RUNNING sim, with an AiPilot for input, no camera, no
    // HUD and IsHumanPiloted false. It pins presence as a hit target, ticking along its ordered course,
    // mid-flight retargeting, part pools moved by the weapon's own ARMOR_DAMAGE, and a kill attributed
    // to the human shooter through Downed. Inventory: this module's docs/architecture.md entry.
    [Suite("ai-actor",
        "the M4 AI actor seam: an AI-piloted plane (AiPilot input, IsHumanPiloted false, no " +
        "camera/HUD/devices) spawned into an already-running sim flies its orders, takes a " +
        "mid-flight retarget, and is present, ticking, damageable by the weapon's own values " +
        "and killable with the kill attributed to the shooter through Downed")]
    internal static void AiActor(TestContext ctx)
    {
        ctx.RequireData(ctx.PlanesGamezPath, $"planes gamez");
        ctx.RequireData(ctx.ZrdrPath, $"zrdr archive");
        string texturesPath = SessionPaths.ChapterTextures(ctx.DataRoot, "C1");
        ctx.RequireData(texturesPath, $"C1 textures");

        var planesGamez = GameZ.Load(ctx.PlanesGamezPath);
        var weapons = WeaponDefs.Load(ctx.ZrdrPath, null);
        WeaponDef? gun = weapons.All.FirstOrDefault(w =>
            w.IsGun && w.ArmorDamage is > 0f && w.HealthDamage is > 0f);
        ctx.Check(gun != null, $"a gun with ARMOR_DAMAGE and HEALTH_DAMAGE exists in the data");
        if (gun == null)
            return;
        var stats = PlaneStats.Load(ctx.ZrdrPath, ctx.PlaneName);
        var nose = stats.DestroyableParts.FirstOrDefault(p =>
            p.Name.Equals("nose", System.StringComparison.OrdinalIgnoreCase));
        ctx.Check(nose is { Critical: true }, $"{ctx.PlaneName} carries a critical nose part");
        if (nose == null)
            return;

        var textures = new TextureArchive(texturesPath);
        ProjectilePool? pool = null;
        FlightController? shooter = null;
        FlightController? ai = null;
        try
        {
            var live = new ProjectilePool(textures, null, null);
            pool = live;
            ctx.Host.AddChild(live);

            // The human rig first, and 120 sim steps before the AI exists: the spawn below is
            // a RUNTIME spawn into a sim already in motion, not part of a session build.
            var shooterModel = new PlaneBuilder(planesGamez, textures).Build(ctx.PlaneName);
            shooter = new FlightController
            {
                PlaneModel = shooterModel,
                Collider = PlaneCollider.Build(shooterModel),
                Damage = new PlaneDamage(stats.DestroyableParts),
                PlayerIndex = 0,
                Projectiles = live,
                UseKeyboard = false,
                AllowPause = false,
            };
            shooter.AddChild(shooterModel);
            shooter.Setup(new FlightModel(stats), ctx.Camera, new CamParams(),
                new Vector3(2000f, 500f, 0f), new Vector3(2000f, 500f, -1f));
            ctx.Host.AddChild(shooter);
            for (int i = 0; i < 120; i++)
            {
                live.SimStep(1f / 60f);
                shooter.SimStep(1f / 60f);
            }

            // The AI actor: an AiPilot ordered to hold the spawn course, a null camera, no HUD,
            // no devices, exactly what FlightRoster builds, on the suite's own stage.
            var spawnPos = new Vector3(0f, 500f, 0f);
            var pilot = AiPilot.HoldingCourse(spawnPos, spawnPos + Vector3.Forward);
            var aiModel = new PlaneBuilder(planesGamez, textures).Build(ctx.PlaneName);
            ai = new FlightController
            {
                PlaneModel = aiModel,
                Collider = PlaneCollider.Build(aiModel),
                Damage = new PlaneDamage(stats.DestroyableParts),
                PlayerIndex = FlightRoster.ShooterIdBase,
                IsHumanPiloted = false,
                Pilot = pilot,
                Projectiles = live,
                UseKeyboard = false,
                PadDevices = System.Array.Empty<int>(),
                AllowPause = false,
            };
            ai.AddChild(aiModel);
            ai.Setup(new FlightModel(stats), null, new CamParams(), spawnPos, spawnPos + Vector3.Forward);
            ctx.Host.AddChild(ai);

            // Present: in the tree, body built, registered as a hit target on the player id, and
            // visible to a physics ray where it spawned.
            ctx.Check(ai.IsInsideTree() && ai.Body != null,
                $"the AI plane is in the tree with an AircraftBody");
            if (ai.Body == null)
                return;
            ctx.Check(ProjectilePool.SurfaceIdOf(ai.Body) == SurfaceRegistry.Player,
                $"the AI body answers surface id {SurfaceRegistry.Player} (player), weapon IMPACT rows fire on it");
            var space = live.GetWorld3D().DirectSpaceState;
            var probe = space.IntersectRay(PhysicsRayQueryParameters3D.Create(
                spawnPos + new Vector3(0f, 0f, -30f), spawnPos, CollisionLayers.WorldAndAircraft));
            ctx.Check(probe.Count > 0 && ReferenceEquals(probe["collider"].Obj, ai.Body),
                $"a physics ray at the spawned AI returns its body");

            // ⚠ Run the gunfire phases at the SPAWN pose, before the plane flies anywhere: a body moved after
            // creation is invisible to space queries until a physics flush this one-frame suite never gets
            // (INSTR-13). Damage first, flight after.
            float armorDmg = gun.ArmorDamage!.Value;
            float healthDmg = gun.HealthDamage!.Value;
            float Combined() => ai!.Damage!.Parts.Values.Sum(p => p.Hp + p.Armor);
            var muzzle = new Transform3D(
                Basis.LookingAt(Vector3.Back, Vector3.Up), spawnPos + new Vector3(0f, 0f, -10f));
            void FireOne(int steps = 10)
            {
                live.Spawn(gun, muzzle, Vector3.Zero, shooter!.PlayerIndex);
                for (int i = 0; i < steps; i++)
                    live.SimStep(1f / 60f);
                live.Clear();
            }

            // Damageable: a registering round moves the pools by exactly the weapon's
            // ARMOR_DAMAGE (armor-first over full pools, so the combined total moves by it).
            float beforeHit = Combined();
            int tries = 0;
            while (Combined() >= beforeHit && tries < 5)
            {
                tries++;
                FireOne();
            }
            ctx.Check(Mathf.IsEqualApprox(beforeHit - Combined(), armorDmg),
                $"a round moves the AI's pools by the weapon's ARMOR_DAMAGE moved={beforeHit - Combined():0.##} expected={armorDmg:0.##} (rounds={tries})");

            // Killable, with the kill attributed: pre-empty the other zones with EXACT spends, since an
            // overkill spend would down the plane through the whole-pool overflow before the AI's own burst,
            // then sustained fire on the same bearing exhausts the nose.
            foreach (var p in ai.Damage!.Parts.Values)
            {
                if (p.Def != nose)
                {
                    ai.Damage.Apply(p.Def.Name, 0f, p.Armor);
                    ai.Damage.Apply(p.Def.Name, p.Hp, 0f);
                }
            }
            int? downedVictim = null, downedKiller = null;
            ai.Downed += (victim, killer) => { downedVictim = victim; downedKiller = killer; };
            int budget = (int)(nose.MaxArmor / armorDmg + nose.MaxHp / healthDmg) * 3 + 20;
            int fired = 0;
            while (!ai.Crashed && fired < budget)
            {
                fired++;
                FireOne(8);
            }
            ctx.Check(ai.Crashed, $"sustained fire downs the AI plane rounds={fired}/{budget}");
            ctx.Check(downedVictim == FlightRoster.ShooterIdBase,
                $"Downed reports the AI's own shooter id victim={downedVictim?.ToString() ?? "-"}");
            ctx.Check(downedKiller == shooter.PlayerIndex,
                $"…with the kill attributed to the human shooter killer={downedKiller?.ToString() ?? "-"}");

            // Back into the air for the flying half, Respawn repairs the wreck and re-arms the
            // same pilot; nothing below needs a physics query.
            ai.Respawn();
            void FlyAi(float seconds)
            {
                for (int i = 0; i < (int)(seconds * 60f); i++)
                {
                    live.SimStep(1f / 60f);
                    ai!.SimStep(1f / 60f);
                }
            }

            // Ticking, on its orders: 10 s of manual sim steps move it along the ordered course
            // (world -Z) at altitude, driven by AiPilot, no keyboard, no hold script.
            var before = ai.WorldPosition;
            FlyAi(10f);
            var disp = ai.WorldPosition - before;
            ctx.Check(disp.Length() > 300f,
                $"the AI plane flies under its pilot moved={disp.Length():0} m in 10 s");
            ctx.Check(disp.Normalized().Dot(Vector3.Forward) > 0.9f,
                $"…along its ordered course dot={disp.Normalized().Dot(Vector3.Forward):0.00}");
            ctx.Check(Mathf.Abs(ai.WorldPosition.Y - 500f) < 80f,
                $"…holding its ordered altitude y={ai.WorldPosition.Y:0}");

            // Orders are mutable mid-flight: retarget 90° between steps, no rebuild, no respawn.
            pilot.TargetHeadingDeg = 90f;
            FlyAi(25f);
            var noseDir = -ai.GlobalTransform.Basis.Z;
            float errDeg = Mathf.Wrap(90f - AiPilot.HeadingDegOf(noseDir), -180f, 180f);
            ctx.Check(Mathf.Abs(errDeg) < 10f,
                $"a mid-flight retarget is flown to err={errDeg:0.0}° after 25 s");
        }
        finally
        {
            pool?.Free();
            shooter?.Free();
            ai?.Free();
            textures.Dispose();
        }
    }

    // The far-field plant's PLUMBING on live rigs: which aircraft is told where the humans are, how
    // that range is measured, and that the plant switches on it. The branch's own arithmetic is
    // engine-free in CSVM.Tests/FarFieldPlantTests; only the seam needs an engine.
    // Inventory: this module's docs/architecture.md entry.
    [Suite("ai-far-field-plant",
        "the far-field plant's session plumbing on live rigs: an AI 1200 m from the human " +
        "flies the decoded speed-hold branch and one at 100 m keeps the aerodynamics, the " +
        "range is horizontal (3 km of altitude is not distance), the NEAREST of several " +
        "humans decides it, an unbound seam stays near-field, and the far rig holds " +
        "throttle x fd_speed + 5 m/s where the near rig on the same orders does not")]
    internal static void AiFarFieldPlant(TestContext ctx)
    {
        ctx.RequireData(ctx.PlanesGamezPath, $"planes gamez");
        ctx.RequireData(ctx.ZrdrPath, $"zrdr archive");
        string texturesPath = SessionPaths.ChapterTextures(ctx.DataRoot, "C1");
        ctx.RequireData(texturesPath, $"C1 textures");

        var planesGamez = GameZ.Load(ctx.PlanesGamezPath);
        var stats = PlaneStats.Load(ctx.ZrdrPath, ctx.PlaneName);
        var textures = new TextureArchive(texturesPath);
        var humanPos = new Vector3(0f, 500f, 0f);
        var humans = new List<Vector3> { humanPos };

        FlightController? near = null;
        FlightController? far = null;
        try
        {
            // One AI rig at a given offset from the human, flying its spawn course. Its own model is
            // kept by the caller because the branch readout lives on the plant, not on the node.
            (FlightController Rig, FlightModel Model) BuildAi(int index, Vector3 offset)
            {
                var model3d = new PlaneBuilder(planesGamez, textures).Build(ctx.PlaneName);
                var pos = humanPos + offset;
                var rig = new FlightController
                {
                    PlaneModel = model3d,
                    PlayerIndex = FlightRoster.ShooterIdBase + index,
                    IsHumanPiloted = false,
                    Pilot = AiPilot.HoldingCourse(pos, pos + Vector3.Forward),
                    UseKeyboard = false,
                    PadDevices = System.Array.Empty<int>(),
                    AllowPause = false,
                    HumanPositions = () => humans,
                };
                rig.AddChild(model3d);
                var plant = new FlightModel(stats, aiForcePath: true);
                rig.Setup(plant, null, new CamParams(), pos, pos + Vector3.Forward);
                ctx.Host.AddChild(rig);
                return (rig, plant);
            }

            // 100 m out and 1200 m out, level with the human and on the same heading. The near rig's
            // range grows as it flies, so it is placed to stay inside 1000 m for the whole run.
            var (nearRig, nearPlant) = BuildAi(0, new Vector3(100f, 0f, 0f));
            var (farRig, farPlant) = BuildAi(1, new Vector3(1200f, 0f, 0f));
            near = nearRig;
            far = farRig;
            nearRig.SimStep(1f / 60f);
            farRig.SimStep(1f / 60f);

            ctx.Check(!nearPlant.FarFieldPlant,
                $"an AI 100 m from the human flies the near-field aerodynamics");
            ctx.Check(farPlant.FarFieldPlant,
                $"an AI 1200 m from the human flies the far-field speed-hold plant");

            // Horizontal only: 3 km of altitude between them is not range. The human is moved rather
            // than the rig rebuilt, so nothing but the vertical separation changes.
            humans[0] = humanPos + new Vector3(0f, 3000f, 0f);
            nearRig.SimStep(1f / 60f);
            ctx.Check(!nearPlant.FarFieldPlant,
                $"3 km of vertical separation does not send an aircraft far-field: the range is horizontal");
            humans[0] = humanPos;

            // The NEAREST human decides it, which is this engine's four-player widening of the
            // original's single player pointer. A second human beside the far rig brings it back.
            humans.Add(farRig.WorldPosition + new Vector3(0f, 0f, 200f));
            farRig.SimStep(1f / 60f);
            ctx.Check(!farPlant.FarFieldPlant,
                $"a second human 200 m from the far rig returns it to the near-field plant");
            humans.RemoveAt(1);
            farRig.SimStep(1f / 60f);
            ctx.Check(farPlant.FarFieldPlant, $"and it goes far-field again when that human leaves");

            // Five seconds of flight. Both rigs fly the same orders from the same relative pose, so
            // a divergence between them is the plant and nothing else.
            for (int i = 0; i < 300; i++)
            {
                nearRig.SimStep(1f / 60f);
                farRig.SimStep(1f / 60f);
            }

            ctx.Check(!nearPlant.FarFieldPlant && farPlant.FarFieldPlant,
                $"both rigs held their branch across the run near={nearPlant.FarFieldPlant} far={farPlant.FarFieldPlant}");

            // The speed-hold: the far rig sits on throttle x fd_speed + 5 m/s, tracking its own lever
            // through the 1/s lag, while the near rig on the identical orders is elsewhere.
            float held = (farPlant.Throttle * stats.FdSpeed) + 5f;
            float farErr = Mathf.Abs(farPlant.Speed - held);
            float nearErr = Mathf.Abs(nearPlant.Speed - ((nearPlant.Throttle * stats.FdSpeed) + 5f));
            ctx.Check(farErr < 2f,
                $"the far rig holds throttle x fd_speed + 5 speed={farPlant.Speed:0.0} held={held:0.0} err={farErr:0.00} m/s");
            ctx.Check(nearErr > 5f,
                $"the near rig's aerodynamics do not put it there err={nearErr:0.0} m/s (the control)");

            // A rig with no seam bound stays near-field: no snapshot means no known human, and the
            // aerodynamics are what an aircraft with no session around it must keep flying.
            farRig.HumanPositions = null;
            farRig.SimStep(1f / 60f);
            ctx.Check(!farPlant.FarFieldPlant,
                $"an unbound HumanPositions seam leaves the aircraft on the near-field plant");
        }
        finally
        {
            near?.Free();
            far?.Free();
            textures.Dispose();
        }
    }

    // The AI gunnery on real engine state: an AI-piloted, stock-armed plane HELD at fixed poses against
    // a parked hostile. It pins acquisition into the gunner's mutable target field, the quick-draw
    // gate, the forward gun cone, dead-eye scatter at skill 1 against 9, the kill attributed through
    // Downed, and the IsHumanPiloted assist exclusion A/B'd on one rig. Inventory: docs/architecture.md.
    // ⚠ Park the target at its spawn pose and never move it; a body moved inside the suite's single
    // frame is invisible to the rounds' space queries (INSTR-13).
    [Suite("ai-gunnery",
        "the D14 AI gunner + D12 acquisition: acquires through the decoded target ranking " +
        "as mutable state (0.7 player weight, primary_target override, a 'player' assignment " +
        "resolving to the NEAREST human of several, 1e21 activation " +
        "cutoff, all live in the engine), refuses the shot " +
        "when the residual after the ±11° traverse clamp exceeds the gun's 10° aim gate, and " +
        "outside its quick-draw cone off the target's " +
        "nose/tail, fires real rounds through the fire-control path under its own shooter id " +
        "with dead-eye scatter (skill 1 hits measurably less than skill 9), downs the target " +
        "with the kill attributed, and NEVER gets the human aim assist (the IsHumanPiloted " +
        "gate, A/B'd in place)")]
    internal static void AiGunnery(TestContext ctx)
    {
        ctx.RequireData(ctx.PlanesGamezPath, $"planes gamez");
        ctx.RequireData(ctx.ZrdrPath, $"zrdr archive");
        string texturesPath = SessionPaths.ChapterTextures(ctx.DataRoot, "C1");
        ctx.RequireData(texturesPath, $"C1 textures");

        var planesGamez = GameZ.Load(ctx.PlanesGamezPath);
        var weapons = WeaponDefs.Load(ctx.ZrdrPath, null);
        var stats = PlaneStats.Load(ctx.ZrdrPath, ctx.PlaneName);
        var skills = AiSkills.Load(ctx.ZrdrPath);
        LoadoutDef? stock = null;
        foreach (var def in StockLoadouts.Load().All.Values)
        {
            if (def.Model == ctx.PlaneName)
            {
                stock = def;
                break;
            }
        }
        ctx.Check(stock != null, $"stock loadout found for plane={ctx.PlaneName}");
        if (stock == null)
            return;

        var textures = new TextureArchive(texturesPath);
        ProjectilePool? pool = null;
        FlightController? target = null;
        FlightController? ai = null;
        FlightController? rival = null;
        try
        {
            var live = new ProjectilePool(textures, null, null);
            pool = live;
            ctx.Host.AddChild(live);

            // The parked target: a human-index rig at the origin column, PINNED at zero speed so
            // the gunner's intercept sees a static target (a never-stepped rig would otherwise
            // report its spawn speed and every lead would miss a plane that is not moving).
            var targetPos = new Vector3(0f, 500f, 0f);
            var targetModel = new PlaneBuilder(planesGamez, textures).Build(ctx.PlaneName);
            target = new FlightController
            {
                PlaneModel = targetModel,
                Collider = PlaneCollider.Build(targetModel),
                Damage = new PlaneDamage(stats.DestroyableParts),
                PlayerIndex = 0,
                Projectiles = live,
                UseKeyboard = false,
                PadDevices = System.Array.Empty<int>(),
                AllowPause = false,
            };
            target.AddChild(targetModel);
            target.Setup(new FlightModel(stats), ctx.Camera, new CamParams(), targetPos, targetPos + Vector3.Forward);
            ctx.Host.AddChild(target);
            target.PlaceHeld(targetPos, targetPos + Vector3.Forward);

            // The AI shooter: stock-armed (the gunner fires through FireControl, not scripted
            // spawns), AiPilot + AiGunner, held at each phase's pose. Its own seeded scatter rng
            // makes every volley reproducible.
            var gunner = new AiGunner(new RandomNumberGenerator { Seed = 20260813 })
            {
                DeadEyeAngleDeg = skills.DeadEyeAngleDeg(9),
                QuickDrawAngleDeg = skills.QuickDrawAngleDeg(1),
            };
            var aiPos = targetPos + new Vector3(0f, 0f, 500f); // dead astern of the target's tail
            var pilot = AiPilot.HoldingCourse(aiPos, targetPos);
            pilot.Gunner = gunner;
            var aiModel = new PlaneBuilder(planesGamez, textures).Build(ctx.PlaneName);
            ai = new FlightController
            {
                PlaneModel = aiModel,
                Collider = PlaneCollider.Build(aiModel),
                Damage = new PlaneDamage(stats.DestroyableParts),
                PlayerIndex = FlightRoster.ShooterIdBase,
                IsHumanPiloted = false,
                Pilot = pilot,
                Projectiles = live,
                UseKeyboard = false,
                PadDevices = System.Array.Empty<int>(),
                AllowPause = false,
            };
            ai.AddChild(aiModel);
            ai.Loadout = Loadout.Bind(stock, aiModel, weapons);
            ai.Setup(new FlightModel(stats), null, new CamParams(), aiPos, targetPos);
            ctx.Host.AddChild(ai);
            ai.Held = true;
            ai.PlaceHeld(aiPos, targetPos);

            var gun = ai.Loadout.FirableGuns.First();
            float armorDmg = gun.Weapon.ArmorDamage ?? 0f;
            ctx.Check(armorDmg > 0f && Mathf.IsEqualApprox(armorDmg, gun.Weapon.HealthDamage ?? -1f),
                $"the stock gun's two damage magnitudes are equal ({gun.Weapon.Id}, {armorDmg:0.#}), the hit counter's precondition");
            float Combined(FlightController rig) => rig.Damage!.Parts.Values.Sum(p => p.Hp + p.Armor);
            bool Pristine(FlightController rig) => rig.Damage!.Parts.Values.All(
                p => p.Hp >= p.Def.MaxHp && p.Armor >= p.Def.MaxArmor);
            void Step(int frames)
            {
                for (int i = 0; i < frames; i++)
                {
                    ai!.SimStep(1f / 60f);
                    live.SimStep(1f / 60f);
                }
            }

            // --- acquisition: the nearest hostile lands in the gunner's mutable target field.
            Step(1);
            ctx.Check(ReferenceEquals(gunner.Target, target),
                $"the gunner auto-acquires the nearest hostile aircraft");
            // Mutable state: cleared orders re-acquire; an explicit assignment stands.
            gunner.Target = null;
            Step(1);
            ctx.Check(ReferenceEquals(gunner.Target, target), $"a cleared target re-acquires next tick");

            // --- the quick-draw gate: 80° off the target's tail axis (a beam-ish shot). At
            // rating 1 the ~54° cone refuses it; at rating 9 the 89° cone takes it.
            var beamPos = targetPos + new Vector3(0.9848f, 0f, 0.1736f) * 500f; // 80° off +Z
            ai.PlaceHeld(beamPos, targetPos);
            int ammoAtBeam = gun.Ammo;
            Step(60);
            ctx.Check(!gunner.WantsFire && gun.Ammo == ammoAtBeam,
                $"a beam-ish shot is refused at quick-draw rating 1 (~54°) rounds={ammoAtBeam - gun.Ammo}");
            gunner.QuickDrawAngleDeg = skills.QuickDrawAngleDeg(9);
            Step(60);
            ctx.Check(gun.Ammo < ammoAtBeam,
                $"the same bearing is taken at rating 9 (89°) rounds={ammoAtBeam - gun.Ammo}");

            // --- the aim gate: nose 30° off the bearing clamps to the airframe's 11° and leaves
            // a 19° residual, past the gun's 10°, so the shot is refused with quick draw willing.
            ai.PlaceHeld(beamPos, beamPos + (targetPos - beamPos).Normalized()
                .Rotated(Vector3.Up, Mathf.DegToRad(30f)) * 100f);
            int ammoAtOffBore = gun.Ammo;
            Step(60);
            ctx.Check(!gunner.WantsFire && gun.Ammo == ammoAtOffBore,
                $"a 19° residual after the traverse clamp holds fire (nose 30° off)");

            // ⚠ Flagged AI to the ranking phase: WarningShotCue discards gun damage on a HUMAN rig
            // until sustained fire saturates it, and every check between here and there measures a
            // gunner by the victim's ledger. That rule is incoming-fire-cues' subject, not this.
            target.IsHumanPiloted = false;

            // Dead-eye scatter, skill 1 against 9 on fixed geometry: the high rear quarter at ~212 m, where the
            // planform presents real area. Dead astern the airframe is edge-on and both cones mostly miss,
            // which drowns the difference the volley is measuring.
            ai.PlaceHeld(targetPos + new Vector3(0f, 150f, 150f), targetPos);
            (int Rounds, int Hits) Volley(float deadEyeDeg, int roundCap)
            {
                gunner!.DeadEyeAngleDeg = deadEyeDeg;
                gunner.AutoTarget = true;
                gunner.Target = target;
                target!.Respawn();
                target.PlaceHeld(targetPos, targetPos + Vector3.Forward);
                live.Clear(); // no stragglers from an earlier phase land on this ledger
                float before = Combined(target);
                int ammoBefore = gun.Ammo;
                for (int guard = 0; ammoBefore - gun.Ammo < roundCap && guard < 1800; guard++)
                    Step(1);
                gunner.AutoTarget = false;
                gunner.Target = null; // cease fire; let the last rounds land
                Step(90);
                return (ammoBefore - gun.Ammo, (int)Mathf.Round((before - Combined(target)) / armorDmg));
            }

            var low = Volley(skills.DeadEyeAngleDeg(1), 30);
            var high = Volley(skills.DeadEyeAngleDeg(9), 30);
            ctx.Note($"dead-eye: skill 1 hit {low.Hits}/{low.Rounds}, skill 9 hit {high.Hits}/{high.Rounds} from the high rear quarter at ~212 m");
            // Fixed seed, so the measured gap (13 vs 6 of 30) is exact; the +4 margin is what
            // "measurably" means here, not a statistical bound.
            ctx.Check(high.Hits >= low.Hits + 4,
                $"skill 9's tighter cone out-hits skill 1 ({high.Hits}/{high.Rounds} vs {low.Hits}/{low.Rounds})");
            ctx.Check(high.Hits >= 1 && low.Hits < low.Rounds,
                $"both regimes are real: skill 9 lands rounds, skill 1 scatters some wide");
            ctx.Check(Pristine(ai) && !ai.Crashed, $"the AI's own airframe took none of its own fire");

            // The kill attributed to the AI's shooter id, from dead astern at 150 m. Scaffolding per the
            // whole-vehicle rule: the zones this bearing cannot reach are pre-emptied, and the AI's own fire
            // finishes the plane.
            gunner.AutoTarget = true;
            gunner.DeadEyeAngleDeg = skills.DeadEyeAngleDeg(9);
            ai.PlaceHeld(targetPos + new Vector3(0f, 0f, 150f), targetPos);
            target.Respawn();
            target.PlaceHeld(targetPos, targetPos + Vector3.Forward);
            live.Clear();
            foreach (var p in target.Damage!.Parts.Values)
            {
                if (!p.Def.Name.Equals("tail", System.StringComparison.OrdinalIgnoreCase))
                {
                    // Exact spends: an overkill spend would kill through the whole-pool overflow.
                    target.Damage.Apply(p.Def.Name, 0f, p.Armor);
                    target.Damage.Apply(p.Def.Name, p.Hp, 0f);
                }
            }
            int? downedVictim = null, downedKiller = null;
            target.Downed += (victim, killer) => { downedVictim = victim; downedKiller = killer; };
            for (int guard = 0; !target.Crashed && guard < 3600; guard++)
                Step(1);
            ctx.Check(target.Crashed, $"the AI's own gunnery downs the target");
            ctx.Check(downedVictim == target.PlayerIndex && downedKiller == ai.PlayerIndex,
                $"the kill is attributed to the AI's shooter id killer={downedKiller?.ToString() ?? "-"}");

            // The IsHumanPiloted assist exclusion, A/B'd in place: nose 3 degrees off the bearing at 400 m,
            // gunner disarmed, trigger held raw. As an AI the rounds leave along the muzzle axis and all miss;
            // the same rig flagged human gets the assist, which snaps onto the target and lands hits.
            pilot.Gunner = null;
            ai.AutoFire = true;
            target.Respawn();
            target.PlaceHeld(targetPos, targetPos + Vector3.Forward);
            live.Clear(); // the kill phase's last rounds must not land on this ledger
            var nearPos = targetPos + new Vector3(0f, 0f, 400f);
            var offDir = (targetPos - nearPos).Normalized().Rotated(Vector3.Up, Mathf.DegToRad(3f));
            ai.PlaceHeld(nearPos, nearPos + offDir * 100f);
            float pristineCombined = Combined(target);
            Step(180);
            ctx.Check(Mathf.IsEqualApprox(Combined(target), pristineCombined),
                $"an AI plane gets NO aim assist: every off-boresight round misses");
            ai.IsHumanPiloted = true;
            Step(180);
            ctx.Check(Combined(target) < pristineCombined,
                $"the same geometry flagged human is assisted onto the target moved={pristineCombined - Combined(target):0.##}");

            // Ranked acquisition on a hostile pair at equal geometry: the human-piloted target carries the
            // decoded 0.7 base weight and out-ranks the AI rival, a primary_target assignment overrides the
            // ranking, and a candidate beyond the activation radius scores the engine's 1e21 and is never picked.
            target.IsHumanPiloted = true;  // the weight under test is the PLAYER's
            ai.IsHumanPiloted = false;
            ai.AutoFire = false;
            pilot.Gunner = gunner;
            gunner.AutoTarget = true;
            gunner.Target = null;
            target.Respawn();
            target.PlaceHeld(targetPos, targetPos + Vector3.Forward);
            var shooterPos = targetPos + new Vector3(0f, 0f, 800f);
            ai.PlaceHeld(shooterPos, targetPos);
            // Same 800 m ring, 14.5° off the shooter's nose, same altitude, nose away.
            var rivalPos = targetPos + new Vector3(200f, 0f, 800f - Mathf.Sqrt(800f * 800f - 200f * 200f));
            var rivalModel = new PlaneBuilder(planesGamez, textures).Build(ctx.PlaneName);
            rival = new FlightController
            {
                PlaneModel = rivalModel,
                Collider = PlaneCollider.Build(rivalModel),
                PlayerIndex = FlightRoster.ShooterIdBase + 1,
                IsHumanPiloted = false,
                Projectiles = live,
                UseKeyboard = false,
                PadDevices = System.Array.Empty<int>(),
                AllowPause = false,
            };
            rival.AddChild(rivalModel);
            rival.Setup(new FlightModel(stats), null, new CamParams(), rivalPos,
                rivalPos + Vector3.Forward);
            rival.Name = "rival_hostile";
            ctx.Host.AddChild(rival);
            rival.PlaceHeld(rivalPos, rivalPos + Vector3.Forward);

            Step(1);
            ctx.Check(ReferenceEquals(gunner.Target, target),
                $"equal geometry: the player's 0.7 weight out-ranks the AI rival (360 rank units)");
            gunner.Target = null;
            gunner.PrimaryTargetName = "rival_hostile";
            Step(1);
            ctx.Check(ReferenceEquals(gunner.Target, rival),
                $"an assigned primary_target overrides the ranking");
            gunner.Target = null;
            gunner.PrimaryTargetName = "player";
            Step(1);
            ctx.Check(ReferenceEquals(gunner.Target, target),
                $"primary_target 'player' resolves to the human-piloted aircraft");

            // With two humans in the scan, 'player' is a ROLE and resolves to whichever human is NEAREST this
            // attacker, not the first registered; the rival registered second, so pulling the pick across is
            // what proves distance decides. PlaceHeld is legitimate here: this branch queries no physics space.
            rival.IsHumanPiloted = true;
            var nearRivalPos = targetPos + new Vector3(0f, 0f, 500f); // 300 m from the shooter
            rival.PlaceHeld(nearRivalPos, nearRivalPos + Vector3.Forward);
            gunner.Target = null;
            Step(1);
            ctx.Check(ReferenceEquals(gunner.Target, rival),
                $"two humans: 'player' takes the NEARER one (300 m) over the first registered (800 m)");
            var nearTargetPos = targetPos + new Vector3(0f, 0f, 700f); // 100 m from the shooter
            target.PlaceHeld(nearTargetPos, nearTargetPos + Vector3.Forward);
            gunner.Target = null;
            Step(1);
            ctx.Check(ReferenceEquals(gunner.Target, target),
                $"swapping which human is nearer swaps the pick (100 m beats 300 m)");
            rival.IsHumanPiloted = false;
            rival.PlaceHeld(rivalPos, rivalPos + Vector3.Forward);
            target.PlaceHeld(targetPos, targetPos + Vector3.Forward);

            gunner.PrimaryTargetName = null;
            gunner.Target = null;
            target.PlaceHeld(targetPos + new Vector3(0f, 0f, -2500f),
                targetPos + new Vector3(0f, 0f, -2600f));
            Step(1);
            ctx.Check(ReferenceEquals(gunner.Target, rival),
                $"beyond the activation radius the player scores 1e21 and the rival is picked");
        }
        finally
        {
            pool?.Free();
            target?.Free();
            ai?.Free();
            rival?.Free();
            textures.Dispose();
        }
    }

    // The mode machine on a real flying AI rig. The reaction rolls are scripted by pinning the shipped
    // chance fields to 0/1 while the machine's own rng stays seeded, and the steady-hand entry rides a
    // REAL projectile hit through TakeProjectileHit, the same call the pool makes, so the hit-path
    // wiring is what is exercised rather than the machine API. The plane may genuinely fly between
    // phases: no phase here queries physics at a flown-to position (INSTR-13).
    [Suite("ai-modes",
        "the D11 nine-mode machine on a live AI plane: patrol activates into pursue inside " +
        "the shipped 2000 m radius, a scripted failed steady-hand roll on a real projectile " +
        "hit sets the evade flag and enters an evasive maneuver, a pursuer pointed elsewhere " +
        "clears the flag and releases the reaction while a nose-on one holds it and chains a " +
        "second program, an ordered evade carries no flag and leaves the next hit its roll " +
        "while a hit with nothing eligible sets and holds one, a hit pilot whose dare devil test " +
        "passes dives into a danger-zone ribbon 300 m off its nose rather than flying the " +
        "engagement out, a failed " +
        "sixth-sense roll stuns (gunner silent) and recovers after stun_recovery_interval, " +
        "the avoid-crash override climbs out on a blocked probe and releases, and the D15 " +
        "rubber-band assist: a chasing human fallen behind puts the machine in lay off " +
        "(throttle eased, fire held) and --no-assist's switch never enters it under the " +
        "same geometry, every transition in the original's own mode vocabulary")]
    internal static void AiModes(TestContext ctx)
    {
        ctx.RequireData(ctx.PlanesGamezPath, $"planes gamez");
        ctx.RequireData(ctx.ZrdrPath, $"zrdr archive");
        string texturesPath = SessionPaths.ChapterTextures(ctx.DataRoot, "C1");
        ctx.RequireData(texturesPath, $"C1 textures");

        var planesGamez = GameZ.Load(ctx.PlanesGamezPath);
        var weapons = WeaponDefs.Load(ctx.ZrdrPath, null);
        var stats = PlaneStats.Load(ctx.ZrdrPath, ctx.PlaneName);
        var skills = AiSkills.Load(ctx.ZrdrPath);
        var library = Maneuvers.Load(ctx.ZrdrPath);
        WeaponDef? gun = weapons.All.FirstOrDefault(w =>
            w.IsGun && w.ArmorDamage is > 0f && w.HealthDamage is > 0f);
        ctx.Check(gun != null, $"a gun with authored damage exists in the data");
        if (gun == null)
            return;

        // The shipped range gates the machine runs on.
        ctx.Check(Mathf.IsEqualApprox(skills.MinAiActiveDist, 2000f),
            $"player.json min_ai_active_dist is the decoded 2000 m got={skills.MinAiActiveDist:0}");
        ctx.Check(Mathf.IsEqualApprox(stats.AiAttackRange, 2000f)
            && Mathf.IsEqualApprox(stats.AiReturnRange, 1200f),
            $"vehicle.json attack/return_range are the decoded 2000/1200 m got={stats.AiAttackRange:0}/{stats.AiReturnRange:0}");

        var textures = new TextureArchive(texturesPath);
        ProjectilePool? pool = null;
        FlightController? target = null;
        FlightController? ai = null;
        try
        {
            var live = new ProjectilePool(textures, null, null);
            pool = live;
            ctx.Host.AddChild(live);

            // The hostile: a parked human-index rig the machine can activate against.
            var targetPos = new Vector3(0f, 500f, 0f);
            var targetModel = new PlaneBuilder(planesGamez, textures).Build(ctx.PlaneName);
            target = new FlightController
            {
                PlaneModel = targetModel,
                Collider = PlaneCollider.Build(targetModel),
                PlayerIndex = 0,
                Projectiles = live,
                UseKeyboard = false,
                PadDevices = System.Array.Empty<int>(),
                AllowPause = false,
            };
            target.AddChild(targetModel);
            target.Setup(new FlightModel(stats), ctx.Camera, new CamParams(), targetPos,
                targetPos + Vector3.Forward);
            ctx.Host.AddChild(target);
            target.PlaceHeld(targetPos, targetPos + Vector3.Forward);

            // The AI: pilot + gunner + machine, spawned OUTSIDE the activation radius flying
            // straight at the hostile. The terrain probe is injected before the controller can
            // wire its own, so avoid-crash is a scripted flag here.
            bool terrainBlocked = false;
            var machine = new AiModeMachine(new System.Random(11))
            {
                ActivationRange = skills.MinAiActiveDist,
                AttackRange = stats.AiAttackRange,
                ReturnRange = stats.AiReturnRange,
                // Scripted per phase at the power law's two limits. A zero exponent passes every
                // roll whatever the bite, and an infinite one fails every roll that bites at all.
                SteadyHandExponent = 0f,
                SixthSenseChance = 1f,
                StunRecoveryIntervalS = skills.At("stun_recovery_interval", 5),
                NaturalTouch = 1,
                Library = library,
                ProbeBlocked = (_, _) => terrainBlocked ? "suite/terrain" : null,
                // The injector cull is injected too, open: these phases are about the mode
                // vocabulary. Culling the library's nitro entry would only change which maneuver
                // the seeded draw flies. The cull itself is covered by nitro-ai-edges.
                NitroUsable = () => true,
            };
            var transitions = new List<string>();
            machine.ModeChanged += (from, to, _) =>
                transitions.Add($"{AiModeMachine.NameOf(from)}>{AiModeMachine.NameOf(to)}");
            string? lastRoll = null;
            machine.RollLogged += line => lastRoll = line;

            var aiPos = targetPos + new Vector3(0f, 0f, 2600f); // outside the 2000 m radius
            var pilot = AiPilot.HoldingCourse(aiPos, targetPos);
            pilot.Gunner = new AiGunner(new RandomNumberGenerator { Seed = 20260813 })
            {
                DeadEyeAngleDeg = skills.DeadEyeAngleDeg(5),
                QuickDrawAngleDeg = skills.QuickDrawAngleDeg(5),
            };
            pilot.Machine = machine;
            var aiModel = new PlaneBuilder(planesGamez, textures).Build(ctx.PlaneName);
            ai = new FlightController
            {
                PlaneModel = aiModel,
                Collider = PlaneCollider.Build(aiModel),
                Damage = new PlaneDamage(stats.DestroyableParts),
                PlayerIndex = FlightRoster.ShooterIdBase,
                IsHumanPiloted = false,
                Pilot = pilot,
                Projectiles = live,
                UseKeyboard = false,
                PadDevices = System.Array.Empty<int>(),
                AllowPause = false,
            };
            ai.AddChild(aiModel);
            ai.Loadout = Loadout.Bind(StockLoadouts.Load().All.Values
                .First(d => d.Model == ctx.PlaneName), aiModel, weapons);
            ai.Setup(new FlightModel(stats), null, new CamParams(), aiPos, targetPos);
            ctx.Host.AddChild(ai);

            void Step(int frames)
            {
                for (int i = 0; i < frames; i++)
                {
                    ai!.SimStep(1f / 60f);
                    live.SimStep(1f / 60f);
                }
            }
            float Dist() => ai!.WorldPosition.DistanceTo(targetPos);

            // --- activation: patrol until the approach carries it inside the radius, then
            // pursue, announced in the decoded vocabulary.
            ctx.Check(machine.Mode == AiMode.Patrol, $"the machine starts on patrol");
            Step(1);
            ctx.Check(machine.Mode == AiMode.Patrol && Dist() > 2000f,
                $"outside min_ai_active_dist it stays on patrol d={Dist():0} m");
            int budget = 60 * 60;
            while (machine.Mode == AiMode.Patrol && budget-- > 0)
                Step(1);
            ctx.Check(machine.Mode == AiMode.Pursue,
                $"the approach activates it into pursue d={Dist():0} m");
            ctx.Check(Dist() <= 2010f, $"…at the activation radius, not before d={Dist():0} m");
            ctx.Check(transitions.Contains("patrol>pursue"),
                $"…logged as patrol>pursue transitions=[{string.Join(" ", transitions)}]");

            // --- a scripted FAILED steady-hand roll on a real projectile hit: the decoded
            // reaction, through the same TakeProjectileHit the pool calls.
            machine.SteadyHandExponent = float.PositiveInfinity;
            ai.TakeProjectileHit(gun, ai.WorldPosition + new Vector3(2f, 0f, 0f), "fuselage", 0);
            ctx.Check(lastRoll != null && lastRoll.Contains("steady hand test failed. Evading."),
                $"the hit rolls steady hand in the engine's vocabulary roll={lastRoll}");
            ctx.Check(machine.Evading, $"the failed test sets the evade flag");
            ctx.Check(machine.Mode == AiMode.EvasiveManeuver && machine.Executor != null,
                $"…and enters an evasive maneuver mode={AiModeMachine.NameOf(machine.Mode)}");
            var flown = machine.Executor?.Maneuver;
            ctx.Check(flown != null && flown.EligibleFor(machine.NaturalTouch),
                $"…an eligible library entry name={flown?.Name} difficulty={flown?.Difficulty}/{machine.NaturalTouch}");
            machine.SteadyHandExponent = 0f; // stray hits must not re-trigger mid-phase

            // --- the hostile is parked facing away, so its nose is nowhere near the 0.85 cosine.
            // The program still plays to Done (the clear cannot cut one short) and the flag is
            // tested where it ends: one maneuver, then the engagement again.
            budget = 60 * 60;
            while (machine.Mode == AiMode.EvasiveManeuver && budget-- > 0)
                Step(1);
            ctx.Check(!machine.Evading,
                $"a pursuer pointed elsewhere clears the flag at the end of the program");
            ctx.Check(machine.Mode is AiMode.Pursue or AiMode.Patrol,
                $"…and the reaction returns to the prior mode mode={AiModeMachine.NameOf(machine.Mode)}");
            ctx.Check(machine.Executor == null, $"…and the executor is released");

            // --- the same hit with the hostile's nose held on: the flag survives the end of the
            // program and chains straight into the next one instead of releasing.
            target.PlaceHeld(targetPos, ai.WorldPosition);
            machine.SteadyHandExponent = float.PositiveInfinity;
            ai.TakeProjectileHit(gun, ai.WorldPosition + new Vector3(2f, 0f, 0f), "fuselage", 0);
            machine.SteadyHandExponent = 0f;
            var firstProgram = machine.Executor;
            ctx.Check(machine.Evading && firstProgram != null,
                $"a nose-on pursuer leaves the flag set mode={AiModeMachine.NameOf(machine.Mode)}");
            budget = 60 * 30;
            while (machine.Evading && ReferenceEquals(machine.Executor, firstProgram) && budget-- > 0)
                Step(1);
            ctx.Check(machine.Evading && machine.Mode == AiMode.EvasiveManeuver
                && !ReferenceEquals(machine.Executor, firstProgram),
                $"the program running out chains another first={firstProgram?.Maneuver.Name} now={machine.Executor?.Maneuver.Name} mode={AiModeMachine.NameOf(machine.Mode)}");

            // --- turned away again, the chain ends at the end of the program it is on.
            target.PlaceHeld(targetPos, targetPos + Vector3.Forward);
            budget = 60 * 60;
            while (machine.Mode == AiMode.EvasiveManeuver && budget-- > 0)
                Step(1);
            ctx.Check(!machine.Evading && machine.Executor == null,
                $"the pursuer turning away ends the chain mode={AiModeMachine.NameOf(machine.Mode)}");

            // --- an ORDERED evade, no damage: the flag belongs to the damage routine alone. A
            // scripted entry into the mode leaves it clear, and the next hit still gets its roll.
            machine.Enter(AiMode.Evade, "test: ordered, no damage");
            ctx.Check(machine.Mode == AiMode.Evade && !machine.Evading,
                $"an ordered evade sets no flag evading={machine.Evading}");
            lastRoll = null;
            machine.SteadyHandExponent = float.PositiveInfinity;
            ai.TakeProjectileHit(gun, ai.WorldPosition + new Vector3(2f, 0f, 0f), "fuselage", 0);
            machine.SteadyHandExponent = 0f;
            ctx.Check(lastRoll != null && lastRoll.Contains("steady hand test failed. Evading."),
                $"…so a hit taken there still rolls steady hand roll={lastRoll}");
            ctx.Check(machine.Evading,
                $"…and the roll is what sets the flag evading={machine.Evading}");
            budget = 60 * 60;
            while (machine.Mode == AiMode.EvasiveManeuver && budget-- > 0)
                Step(1);
            ctx.Check(!machine.Evading,
                $"…which the turned-away pursuer then clears mode={AiModeMachine.NameOf(machine.Mode)}");

            // --- the damage arm's own entry into the marked engagement. With nothing in the
            // library eligible, the hit sets flag and mode together. A nose-on pursuer holds both
            // while the pilot flies its engagement, taking no second roll.
            var savedLibrary = machine.Library;
            machine.Library = null;
            target.PlaceHeld(targetPos, ai.WorldPosition);
            lastRoll = null;
            machine.SteadyHandExponent = float.PositiveInfinity;
            ai.TakeProjectileHit(gun, ai.WorldPosition + new Vector3(2f, 0f, 0f), "fuselage", 0);
            ctx.Check(machine.Mode == AiMode.Evade && machine.Evading,
                $"a hit with nothing eligible enters evade with the flag set mode={AiModeMachine.NameOf(machine.Mode)}");
            for (int i = 0; i < 60; i++)
            {
                target.PlaceHeld(targetPos, ai.WorldPosition); // the pursuer's nose held on
                Step(1);
            }
            ctx.Check(machine.Mode == AiMode.Evade && machine.Evading,
                $"…held over a second of nose-on engagement mode={AiModeMachine.NameOf(machine.Mode)}");
            lastRoll = null;
            ai.TakeProjectileHit(gun, ai.WorldPosition + new Vector3(2f, 0f, 0f), "fuselage", 0);
            machine.SteadyHandExponent = 0f;
            ctx.Check(lastRoll != null && lastRoll.Contains("no steady hand test (already evading)"),
                $"…and takes no second steady-hand roll, saying so roll={lastRoll}");
            target.PlaceHeld(targetPos, targetPos + Vector3.Forward);
            Step(1);
            ctx.Check(!machine.Evading && machine.Mode != AiMode.Evade,
                $"the pursuer turning away releases it mode={AiModeMachine.NameOf(machine.Mode)}");
            machine.Library = savedLibrary;

            // Fly the engagement out before the phases that read the aeroplane's own flight. A
            // program ends in whatever attitude its last step left. A descending entry is not
            // what the climb-out below means to measure.
            Step(180);

            // --- a scripted FAILED sixth-sense roll stuns: gunner silent, then recovery after
            // stun_recovery_interval (the shipped value at rating 5).
            machine.Enter(AiMode.Pursue, "test: rejoin");
            machine.SixthSenseChance = 0f;
            machine.NotifyTargetEvaded();
            ctx.Check(machine.Mode == AiMode.Stunned,
                $"a failed sixth-sense roll stuns roll={lastRoll}");
            ctx.Check(lastRoll != null && lastRoll.Contains("Sixth sense test failed; AI now stunned."),
                $"…in the engine's vocabulary");
            Step(6);
            ctx.Check(!pilot.Gunner.WantsFire, $"the gunner is silent while stunned");
            float stunS = machine.StunRecoveryIntervalS;
            Step((int)(stunS * 60f) - 30);
            ctx.Check(machine.Mode == AiMode.Stunned,
                $"still stunned inside stun_recovery_interval ({stunS:0.0} s)");
            Step(60);
            ctx.Check(machine.Mode == AiMode.Pursue,
                $"…and recovered to the prior mode after it mode={AiModeMachine.NameOf(machine.Mode)}");

            // --- avoid crash: a blocked probe overrides with a climb-out, a cleared one
            // releases back. Stepped past ProbeIntervalMaxS, since the probe's cadence is the
            // original's per-plane 0.5…1.0 s draw and this plane's is unknown to the test.
            machine.SixthSenseChance = 1f;
            float yBefore = ai.WorldPosition.Y;
            terrainBlocked = true;
            Step((int)(AiModeMachine.ProbeIntervalMaxS * 60f) + 6);
            ctx.Check(machine.Mode == AiMode.AvoidCrash,
                $"a blocked terrain probe takes the mode mode={AiModeMachine.NameOf(machine.Mode)}");
            ctx.Check(machine.ClimbOutAltitude > yBefore,
                $"…ordering a climb-out to {machine.ClimbOutAltitude:0} m from {yBefore:0} m");
            Step(240);
            ctx.Check(ai.WorldPosition.Y > yBefore,
                $"the plane is climbing out y={ai.WorldPosition.Y:0} from {yBefore:0}");
            terrainBlocked = false;
            Step(120);
            ctx.Check(machine.Mode != AiMode.AvoidCrash,
                $"a cleared probe releases the override mode={AiModeMachine.NameOf(machine.Mode)}");

            // --- lay off (the rubber-band assist). Entry A/B on fixed geometry through
            // the machine's own tick: a chasing human 600 m dead astern enters lay off with
            // the assist on, and never with --no-assist's switch off.
            machine.Enter(AiMode.Pursue, "test: rejoin for lay off");
            // Pursue's own lever, to ease off FROM. ⚠ Not a fixed number: the law walks the lever
            // toward its desired speed, so what pursue is commanding here depends on how fast the
            // plane happens to be after the climb-out above.
            float leverPursuing = pilot.Throttle;
            var aiPos2 = ai.WorldPosition;
            var ownVel = new Vector3(0f, 0f, -100f);               // flying -Z
            var pursuerPos = aiPos2 + new Vector3(0f, 0f, 600f);   // 600 m dead astern
            var pursuerVel = new Vector3(0f, 0f, -80f);            // giving chase
            // Entry needs the geometry SUSTAINED (LayOffSustainS), so both arms tick through
            // the window; one passing frame must not enter (the misfire regression).
            int sustainFrames = (int)(AiModeMachine.LayOffSustainS * 60f) + 2;
            machine.AssistEnabled = false;
            for (int i = 0; i < sustainFrames; i++)
                machine.Update(aiPos2, ownVel, pursuerPos, null, 1f / 60f, pursuerVel, targetIsHuman: true);
            ctx.Check(machine.Mode == AiMode.Pursue,
                $"--no-assist: the same pursued geometry never enters lay off mode={AiModeMachine.NameOf(machine.Mode)}");
            machine.AssistEnabled = true;
            machine.Update(aiPos2, ownVel, pursuerPos, null, 1f / 60f, pursuerVel, targetIsHuman: true);
            ctx.Check(machine.Mode == AiMode.Pursue,
                $"one passing frame does not enter lay off mode={AiModeMachine.NameOf(machine.Mode)}");
            for (int i = 0; i < sustainFrames; i++)
                machine.Update(aiPos2, ownVel, pursuerPos, null, 1f / 60f, pursuerVel, targetIsHuman: true);
            ctx.Check(machine.Mode == AiMode.LayOff,
                $"assist on: a chasing human fallen 600 m behind enters lay off mode={AiModeMachine.NameOf(machine.Mode)}");
            ctx.Check(transitions.Contains("pursue>lay off"),
                $"…logged as pursue>lay off transitions=[{string.Join(" ", transitions)}]");

            // The observable assist, through the live pilot: the throttle eases off pursue's own
            // lever and the gunner's trigger is held for the whole dwell.
            Step(30);
            ctx.Check(machine.Mode == AiMode.LayOff,
                $"the anti-chatter hold keeps the mode mode={AiModeMachine.NameOf(machine.Mode)}");
            ctx.Check(pilot.Throttle < leverPursuing,
                $"the throttle is eased off pursue's own lever throttle={pilot.Throttle:0.000} from {leverPursuing:0.000}");
            ctx.Check(!pilot.Gunner.WantsFire, $"fire is held while laying off");

            // Once the hold expires the machine releases back to pursue and the lever runs up to its ceiling.
            // ⚠ Do not pin an exact throttle: pursue assigns no lever, AiControlLaw walks it at 0.35/s toward
            // its own desired speed, so an exact value would pin the settling transient instead of the release.
            Step(150);
            ctx.Check(machine.Mode == AiMode.Pursue,
                $"a non-pursuing target releases lay off after the hold mode={AiModeMachine.NameOf(machine.Mode)}");

            // --- the proximity pick (FUN_004210e0's unforced arm), last, since the dive takes
            // the aeroplane off its engagement. The roll is pinned to a certainty, so what is
            // measured is the gate and the entry rather than the dice.
            var lead = ai.NoseDirection.Normalized();
            var zoneEntry = ai.WorldPosition + (lead * 300f);
            pilot.DangerZones = DangerZoneRibbons.Of(new[]
            {
                DangerZoneRibbon.FromPolyline("dzpath1", 1, new[]
                {
                    zoneEntry, zoneEntry + (lead * 400f), zoneEntry + (lead * 800f),
                }),
            });
            machine.DaredevilChance = 1f;
            machine.Library = null;
            target.PlaceHeld(targetPos, ai.WorldPosition);   // nose-on, so the fresh flag stands
            machine.SteadyHandExponent = float.PositiveInfinity;
            ai.TakeProjectileHit(gun, ai.WorldPosition + new Vector3(2f, 0f, 0f), "fuselage", 0);
            machine.SteadyHandExponent = 0f;
            ctx.Check(machine.Mode == AiMode.Evade && machine.Evading,
                $"a hit pilot with nothing eligible flies its engagement marked mode={AiModeMachine.NameOf(machine.Mode)}");
            Step(1);
            ctx.Check(machine.Mode == AiMode.ApproachingDangerZone
                && pilot.ZoneRun?.Ribbon.Name == "dzpath1",
                $"…and a passed dare devil test dives into the zone off its nose instead mode={AiModeMachine.NameOf(machine.Mode)} run={pilot.ZoneRun?.Ribbon.Name ?? "none"}");
            ctx.Check(transitions.Contains("evade>approaching danger zone"),
                $"…logged in the engine's own vocabulary transitions=[{string.Join(" ", transitions)}]");

            ctx.Note($"transitions: {string.Join(" ", transitions)}");
        }
        finally
        {
            pool?.Free();
            target?.Free();
            ai?.Free();
            textures.Dispose();
        }
    }

    // The voice runtime against the real soundsh archive, reproducing the session's own lifecycle in
    // order: resolve the chain, prewarm that one pilot's clips, retire the loader as WorldSession.Build
    // does, then prove a resolved line still speaks on the radio while a def never prewarmed returns
    // null. Audibility is the user's half (docs/verification.md). Closes with the measured cost of prewarming the entire voice bank, the
    // number that justifies the roster-subset choice.
    [Suite("voice-runtime",
        "the B8 combat-voice runtime: the accent→voice.zrd→pilot-clip chain resolves against " +
        "the real archive, a roster-subset prewarm makes the lines playable after the loader " +
        "is retired (a never-prewarmed def stays null) and a resolved line speaks on the mission " +
        "radio channel from that cache, and the full-set prewarm cost is measured and reported")]
    internal static void VoiceRuntime(TestContext ctx)
    {
        ctx.RequireData(ctx.ZrdrPath, $"shared zrdr");
        ctx.RequireData(ctx.SoundsPath, $"sound archive (soundsh)");
        var defs = SoundDefs.Load(ctx.ZrdrPath);
        var groups = SoundDefs.LoadGroups(ctx.ZrdrPath);
        var voice = new CombatVoice(defs, groups, CombatVoice.LoadAccents(ctx.ZrdrPath));
        ctx.Same(35, voice.AccentIds.Count, $"voice.zrd accent rows");

        using var archive = new SoundArchive(ctx.SoundsPath);
        WorldSounds? sounds = null;
        MissionRadio? radio = null;
        try
        {
            sounds = new WorldSounds(defs, groups)
            {
                Loader = (d, warn) => archive.Find(d.WavName, d.Looped, warn),
            };
            ctx.Host.AddChild(sounds);

            // The chain's worked example: accent 12 is a single-id pool, VO id 2 (the pilot with
            // the full bearing set). Prewarm that pilot exactly as a mission roster would.
            int? pilot = voice.PilotFor(12, new System.Random(1));
            ctx.Check(pilot == 2, $"accent 12 resolves to VO id 2 got={pilot?.ToString() ?? "null"}");
            var subset = voice.PrewarmNames(new[] { 12 });
            var sw = System.Diagnostics.Stopwatch.StartNew();
            int decoded = sounds.Prewarm(subset);
            sw.Stop();
            ctx.Check(decoded >= 70, $"the accent-12 subset decodes names={subset.Count} decoded={decoded}");
            ctx.Note($"accent-12 prewarm: {subset.Count} defs, {decoded} streams, {sw.ElapsedMilliseconds} ms");
            sounds.Loader = null;   // the session's build scope closing (WorldSession.Build)

            string? playable = voice.PlayableFor(2, "DI-LowDmg");
            ctx.Check(playable == "snd_DI-LowDmg-A_id2_random",
                $"DI-LowDmg resolves to the shipped variant group got={playable}");
            string? bearing = voice.PlayableForTrigger(2, 6);   // WA-Enemy-3H
            ctx.Check(bearing != null && sounds.HasStream(bearing),
                $"the bearing clip's stream survived the loader retirement name={bearing}");

            // A prewarmed line speaks on the radio after the archive closed, reading the same cache.
            radio = new MissionRadio(defs, groups, sounds.StreamFor);
            ctx.Host.AddChild(radio);
            string? resolved = radio.Speak(playable!, new System.Random(2));
            ctx.Check(resolved != null && resolved.StartsWith("snd_id2_DI-LowDmg"),
                $"a prewarmed voice line queues after the archive closed resolved={resolved}");
            radio.Tick(0.1f);
            ctx.Check(radio.LinesStarted == 1 && radio.OnAir == resolved,
                $"…and starts on the channel's next step on-air={radio.OnAir}");

            // A def never prewarmed is null once the loader is gone: the exact failure the
            // prewarm exists to prevent.
            ctx.Check(radio.Speak("snd_id26_TA-SucShk-A", new System.Random(3)) == null,
                $"an unprewarmed pilot's line stays null after the archive closed");

            // The cost of prewarm-everything, measured on a fresh archive so nothing is cached:
            // the number the roster-subset strategy is justified against.
            using var fresh = new SoundArchive(ctx.SoundsPath);
            long bytes = 0;
            int ok = 0, absent = 0;
            sw.Restart();
            foreach (var name in voice.AllClipNames())
            {
                var def = defs[name];
                if (fresh.Find(def.WavName, def.Looped, warn: false) is { } stream)
                {
                    ok++;
                    bytes += stream.Data.Length;
                }
                else
                {
                    absent++;
                }
            }
            sw.Stop();
            ctx.Note($"full voice bank: {voice.AllClipNames().Count} defs, {ok} decoded ({absent} defs without a WAV), {bytes / (1024.0 * 1024.0):0.0} MB PCM, {sw.ElapsedMilliseconds} ms; why the session prewarms the roster subset");
        }
        finally
        {
            radio?.Free();
            if (sounds != null)
            {
                sounds.FlushOneShots();
                sounds.Free();
            }
        }
    }

    // The E16 dispatch on a live AI aircraft, over the same B8 lifecycle the session
    // runs (prewarm the accent's clips, retire the loader, play after the archive is closed).
    // The talker chance is pinned to 1 so the assertions are about the dispatch rules, not the
    // dice; audibility itself is the user's half (docs/verification.md, "What this project
    // cannot verify itself"), what IS assertable is the dispatch decision, the resolved clip
    // name, and the flat radio player it speaks on.
    [Suite("ai-voice",
        "the E16 trigger dispatch on a live AI plane against the real archive: a projectile "
        + "hit crossing a DI threshold plays exactly ONE line the pilot's accent owns (the 15 s "
        + "slot cooldown swallowing the follow-up hits) flat on the mission radio's Voice-bus "
        + "AudioStreamPlayer with no positional player built, a second line from that same "
        + "speaker while its own line is on air is refused as already talking though another "
        + "speaker on the busy channel is not, and the kill plays the dead "
        + "pilot's own death cry through the force flag at its authored level with the listener "
        + "5 km away, while an unforced dispatch on the same dead speaker stays silent, and the "
        + "three gloats run the decoded polarity off the same kill report: a friendly kill is "
        + "silent, the killer speaks 22 over a victim on the player's team and 23 over one that "
        + "is not, and the player's own kill addresses 24 to a player rig that resolves no voice "
        + "set, silently, and broadcasts 16 to the player's flight, and an evade "
        + "episode's end speaks 27 only once the flag has cleared: an end with the pursuer still "
        + "on the tail, a flag clearing outside the evade modes and a step between the two evade "
        + "modes all say nothing, while a shaken end taunts 27, and the ally distress (28) answers a round from the player "
        + "alone and only on a teammate, spoken by the aircraft it struck; and a carried "
        + "gunner raises WA-Turret once per acquisition episode, not at all while it holds the "
        + "nearer AI, once when the player is the acquired target with a clear sight line, with "
        + "the flight rather than the gun speaking id 0 on the player's team, nothing further "
        + "while it keeps tracking, and again after it has lost and re-acquired the player; and the attack pair "
        + "survives a mission start: a commit inside the 2 s mute window speaks nothing yet is "
        + "not lost, the pursuer's 14 and the flight's bearing call-out arriving as the window "
        + "lifts, a pursue re-entry from avoid crash in the same engagement stays silent, and a "
        + "fresh engagement past the 15 s slot cooldown speaks the pair again; and the same raise "
        + "addresses the taunt pair to the pursuer off its own nose against the human it holds: "
        + "inside the nose cone taunts 26, on its own tail taunts 25, abeam taunts neither though "
        + "the raise ran, and a pursuer in its own evade reaction taunts nothing nose-on; and a "
        + "Danger Zone the player has flown broadcasts 15 to that player's own flight")]
    internal static void AiVoice(TestContext ctx)
    {
        ctx.RequireData(ctx.PlanesGamezPath, $"planes gamez");
        ctx.RequireData(ctx.ZrdrPath, $"zrdr archive");
        ctx.RequireData(ctx.SoundsPath, $"sound archive (soundsh)");
        string texturesPath = SessionPaths.ChapterTextures(ctx.DataRoot, "C1");
        ctx.RequireData(texturesPath, $"C1 textures");

        var planesGamez = GameZ.Load(ctx.PlanesGamezPath);
        var weapons = WeaponDefs.Load(ctx.ZrdrPath, null);
        var stats = PlaneStats.Load(ctx.ZrdrPath, ctx.PlaneName);
        var defs = SoundDefs.Load(ctx.ZrdrPath);
        var groups = SoundDefs.LoadGroups(ctx.ZrdrPath);
        var voice = new CombatVoice(defs, groups, CombatVoice.LoadAccents(ctx.ZrdrPath));
        WeaponDef? gun = weapons.All.FirstOrDefault(w =>
            w.IsGun && w.ArmorDamage is > 0f && w.HealthDamage is > 0f);
        ctx.Check(gun != null && stats.DestroyableParts.Count > 0,
            $"a damaging gun and destroyable parts exist in the data");
        if (gun == null)
            return;

        var textures = new TextureArchive(texturesPath);
        using var archive = new SoundArchive(ctx.SoundsPath);
        WorldSounds? sounds = null;
        MissionRadio? radio = null;
        Session.Roster.AiVoiceRuntime? runtime = null;
        Session.Roster.AiVoiceRuntime? attack = null;
        FlightController? ai = null;
        ProjectilePool? turretPool = null;
        var gloatRigs = new List<FlightController>();
        try
        {
            // The session lifecycle: prewarm accent 12's pilot (VO id 2, the full clip set),
            // then retire the loader as WorldSession.Build does.
            sounds = new WorldSounds(defs, groups)
            {
                Loader = (d, warn) => archive.Find(d.WavName, d.Looped, warn),
            };
            ctx.Host.AddChild(sounds);
            sounds.Prewarm(voice.PrewarmNames(new[] { 12 }));
            sounds.Loader = null;

            radio = new MissionRadio(defs, groups, sounds.StreamFor);
            ctx.Host.AddChild(radio);
            runtime = new Session.Roster.AiVoiceRuntime(voice, sounds, radio, new System.Random(5));
            ctx.Host.AddChild(runtime);

            var aiModel = new PlaneBuilder(planesGamez, textures).Build(ctx.PlaneName);
            ai = new FlightController
            {
                PlaneModel = aiModel,
                Collider = PlaneCollider.Build(aiModel),
                Damage = new PlaneDamage(stats.DestroyableParts),
                PlayerIndex = FlightRoster.ShooterIdBase,
                IsHumanPiloted = false,
                Pilot = new AiPilot(),
                UseKeyboard = false,
                PadDevices = System.Array.Empty<int>(),
                AllowPause = false,
            };
            ai.AddChild(aiModel);
            var aiPos = new Vector3(0f, 500f, 0f);
            ai.Setup(new FlightModel(stats), null, new CamParams(), aiPos, aiPos + Vector3.Forward);
            ctx.Host.AddChild(ai);

            // Talker chance pinned to 1: every roll passes, so a silent trigger is a GATE
            // decision (cooldown, aliveness), never luck.
            runtime.RegisterAi(ai, accentId: 12, talkerChance: 1f, constitutionChance: 1f);
            var speaker = runtime.Dispatcher.Find(ai.PlayerIndex);
            ctx.Check(speaker is { VoId: 2 },
                $"accent 12 registers the AI as VO id 2 got={speaker?.VoId.ToString() ?? "none"}");
            if (speaker == null)
                return;

            // An inert aircraft is registered as a speaker but is not eligible: the runtime mirrors InPlay into
            // the dispatcher's own aliveness gate, the only thing here that can see a FlightController. Checked
            // in both directions, so "not eligible" cannot be the flag's resting state.
            ctx.Check(speaker.Alive, $"baseline: a live AI is an eligible speaker");
            ai.Inert = true;
            ctx.Check(!speaker.Alive, $"going inert takes the speaker out of the broadcast election");
            ai.Inert = false;
            ctx.Check(speaker.Alive, $"…and activation puts it back");

            var played = new List<(string Tag, int Trigger, string Clip)>();
            runtime.LinePlayed += (tag, trigger, clip) => played.Add((tag, trigger, clip));
            runtime.Step(3f); // past the decoded 2 s mute window

            // The listener starts beside the speaker and is later moved past every voice def's
            // audible radius. A line that moved with distance would show it.
            var ear = ai.WorldPosition + new Vector3(0f, 0f, 20f);
            sounds.SetListeners(() => new[] { ear });
            int oneShotsBefore = sounds.OneShotsStarted;

            // --- DI: hits through the pool's own entry point until the summary crosses 70 %.
            // The hits WALK the four zones (a single zone's exhausted pool floors the summary
            // at 75 % on this airframe and could never cross the threshold).
            var struck = new (string Part, Vector3 Offset)[]
            {
                ("wing", new Vector3(-2f, 0f, 0f)), ("wing", new Vector3(2f, 0f, 0f)),
                ("fuselage", new Vector3(0f, 0f, -2f)), ("fuselage", new Vector3(0f, 0f, 2f)),
            };
            int budget = 400;
            while (ai.Damage!.SummaryHealthFraction >= 0.7f && budget-- > 0)
            {
                var (part, offset) = struck[budget % struck.Length];
                ai.TakeProjectileHit(gun, ai.WorldPosition + offset, part, 0);
            }
            float fraction = ai.Damage.SummaryHealthFraction;
            ctx.Check(fraction is < 0.7f and > 0.3f,
                $"the sweep stopped inside the DI band health={fraction * 100f:0}%");
            ctx.Check(played.Count == 1 && played[0].Clip.StartsWith("snd_id2_DI-"),
                $"crossing the threshold played exactly one DI line of the pilot's own played=[{string.Join(", ", played)}]");
            radio.Tick(0.1f);
            var radioPlayer = RadioPlayer(radio);
            ctx.Check(radio.OnAir == played[0].Clip && radioPlayer is { Playing: true },
                $"…speaking on the radio channel on-air={radio.OnAir} playing={radioPlayer?.Playing}");
            ctx.Check(radioPlayer != null && radioPlayer.Bus.ToString() == AudioBuses.Voice,
                $"…on the Voice bus actual={radioPlayer?.Bus}");
            ctx.Check(sounds.OneShotsStarted == oneShotsBefore,
                $"…and no line built a positional player: no AudioStreamPlayer3D one-shot started ({sounds.OneShotsStarted - oneShotsBefore})");
            float nearDb = radioPlayer?.VolumeDb ?? float.NaN;
            ctx.Check(Mathf.IsEqualApprox(nearDb, FlatDb(defs, played[0].Clip)),
                $"…at its def's authored level beside the speaker db={nearDb:0.00} want={FlatDb(defs, played[0].Clip):0.00}");

            // The gate's already-talking test, answered from the radio's own line: while this
            // speaker's line is on air a second line from it is refused before the cooldown is
            // even read, and a second pilot sharing the channel is not held by it.
            int spokenTrigger = played[0].Trigger;
            int otherSpeaker = ai.PlayerIndex + 1;
            runtime.Dispatcher.Register(otherSpeaker, speaker.VoId, ai.Team,
                isPlayer: false, talkerChance: 1f, constitutionChance: 1f);
            float armedBefore = speaker.NextAllowedAt(spokenTrigger);
            var over = runtime.Dispatcher.Dispatch(ai.PlayerIndex, spokenTrigger, runtime.Now);
            ctx.Check(over.Clip == null && over.Outcome == "already talking",
                $"a second line from the speaker whose own line is on air is refused outcome={over.Outcome}");
            ctx.Check(Mathf.IsEqualApprox(speaker.NextAllowedAt(spokenTrigger), armedBefore),
                $"…and the refusal re-arms nothing, the played line's own cooldown still stands");
            var other = runtime.Dispatcher.Dispatch(otherSpeaker, spokenTrigger, runtime.Now);
            ctx.Check(other.Clip != null,
                $"…while another speaker on the same busy channel still speaks clip={other.Clip}");
            PumpRadio(radio);
            var freed = runtime.Dispatcher.Dispatch(ai.PlayerIndex, spokenTrigger, runtime.Now);
            ctx.Check(freed.Outcome == "slot cooling",
                $"…and once the line's length has elapsed the same speaker reaches the slot cooldown instead outcome={freed.Outcome}");

            // Follow-up hits in the same tier stay silent: the slot cooldown swallowed them
            // (armed by the PLAY here; the failed-roll arming is the unit suite's,
            // AiVoiceDispatcherTests).
            int before = played.Count;
            static int Tier(float f) => f < 0.3f ? 3 : f < 0.5f ? 2 : f < 0.7f ? 1 : 0;
            int tier = Tier(ai.Damage.SummaryHealthFraction);
            ai.TakeProjectileHit(gun, ai.WorldPosition, "fuselage", 0, damageScale: 0.02f);
            if (Tier(ai.Damage.SummaryHealthFraction) == tier)
            {
                ctx.Check(played.Count == before,
                    $"a follow-up hit in the same tier is silent under the 15 s cooldown");
            }

            // The channel is drained first, or a still-speaking DI line would hold the cry past its
            // 0.8 s QUEUE tolerance and drop it.
            PumpRadio(radio);

            // --- the kill: the dying pilot's own cry, dispatched with force (the speaker is
            // already dead when it plays), heard from 5 km, far past any voice def's audible radius.
            ear = ai.WorldPosition + new Vector3(0f, 0f, FarEarMetres);
            ai.DebugForceCrash();
            ctx.Check(!speaker.Alive, $"the Downed report marked the speaker dead");
            ctx.Check(played.Count >= before + 1 && played[^1].Clip.StartsWith("snd_id2_DE-"),
                $"…and the death cry played THROUGH the dead state (force) clip={(played.Count > 0 ? played[^1].Clip : "none")}");
            ctx.Check(played[^1].Trigger == AiVoiceDispatcher.DeEnemy,
                $"…as id 21 (DE): no team model puts an AI on the player's team, documented");
            radio.Tick(0.1f);
            sounds.Tick();
            string cry = played[^1].Clip;
            float farDb = radioPlayer?.VolumeDb ?? float.NaN;
            float lawDb = SoundFalloff.GainDb(FarEarMetres, defs[cry].RangeMin, defs[cry].RangeMax, defs[cry].Volume);
            ctx.Check(radio.OnAir == cry && Mathf.IsEqualApprox(farDb, FlatDb(defs, cry)),
                $"…and speaks at its authored level with the listener {FarEarMetres:0} m away on-air={radio.OnAir} db={farDb:0.00} want={FlatDb(defs, cry):0.00} (the distance law would give {lawDb:0.00})");
            ctx.Check(sounds.OneShotsStarted == oneShotsBefore,
                $"…still with no positional player built ({sounds.OneShotsStarted - oneShotsBefore})");

            // The force flag is the death cry's alone: an ordinary dispatch on the same dead
            // speaker is gated out before anything rolls.
            var unforced = runtime.Dispatcher.Dispatch(ai.PlayerIndex,
                AiVoiceDispatcher.TaSucShk, runtime.Now);
            ctx.Check(unforced.Clip == null && unforced.Outcome == "speaker dead",
                $"an unforced dispatch on the dead speaker is refused outcome={unforced.Outcome}");

            // --- the gloats (22-24), off the same kill report, with the decoded polarity. One
            // victim per case, since a death is reported once, and every victim registers
            // accentless, so the only line a kill can produce here is its killer's.
            PumpRadio(radio);
            FlightController Rig(int index, int team, bool human)
            {
                var rigModel = new PlaneBuilder(planesGamez, textures).Build(ctx.PlaneName);
                var rig = new FlightController
                {
                    PlaneModel = rigModel,
                    Collider = PlaneCollider.Build(rigModel),
                    Damage = new PlaneDamage(stats.DestroyableParts),
                    PlayerIndex = index,
                    Team = team,
                    IsHumanPiloted = human,
                    Pilot = human ? null : new AiPilot(),
                    UseKeyboard = false,
                    PadDevices = System.Array.Empty<int>(),
                    AllowPause = false,
                };
                rig.AddChild(rigModel);
                var at = new Vector3(0f, 500f, 0f);
                rig.Setup(new FlightModel(stats), null, new CamParams(), at, at + Vector3.Forward);
                rig.Name = $"rig{index}";
                ctx.Host.AddChild(rig);
                gloatRigs.Add(rig);
                return rig;
            }

            // Two mission teams hostile to each other and to the player's, so each case turns on
            // the predicate alone: the killer flies for one of them and the player's flight for
            // AimAssist.PlayerTeam.
            const int enemyTeam = 2;
            const int thirdTeam = 3;
            var killer = Rig(FlightRoster.ShooterIdBase + 1, enemyTeam, human: false);
            var wingman = Rig(FlightRoster.ShooterIdBase + 2, AimAssist.PlayerTeam, human: false);
            var pilotRig = Rig(0, AimAssist.PlayerTeam, human: true);
            runtime.RegisterAi(killer, accentId: 12, talkerChance: 1f, constitutionChance: 1f);
            runtime.RegisterAi(wingman, accentId: 12, talkerChance: 1f, constitutionChance: 1f);
            runtime.RegisterPlayer(pilotRig);

            // The first predicate, over shooter and victim: a kill inside one team picks no gloat.
            int gloatsBefore = played.Count;
            var comrade = Rig(FlightRoster.ShooterIdBase + 3, enemyTeam, human: false);
            runtime.RegisterAi(comrade, accentId: null, talkerChance: 0f, constitutionChance: 0f);
            comrade.DebugForceCrash(killer.PlayerIndex);
            ctx.Check(played.Count == gloatsBefore,
                $"a friendly kill is silent: no gloat is chosen at all lines={played.Count - gloatsBefore}");
            PumpRadio(radio);

            // The second predicate, over victim and the player's team: hostile picks 23.
            var stranger = Rig(FlightRoster.ShooterIdBase + 4, thirdTeam, human: false);
            runtime.RegisterAi(stranger, accentId: null, talkerChance: 0f, constitutionChance: 0f);
            stranger.DebugForceCrash(killer.PlayerIndex);
            ctx.Check(played.Count == gloatsBefore + 1
                && played[^1].Trigger == AiVoiceDispatcher.GlEnemyDwn
                && played[^1].Tag == killer.Name && played[^1].Clip.StartsWith("snd_id2_GL-EnemyDwn"),
                $"downing a plane on neither the killer's nor the player's team gloats as #23, spoken by the KILLER last={(played.Count > gloatsBefore ? played[^1].ToString() : "none")}");
            PumpRadio(radio);

            // …and friendly picks 22, the same killer, a different slot, so the 15 s cooldown the
            // line above armed cannot be what decides this one.
            var friendly = Rig(FlightRoster.ShooterIdBase + 5, AimAssist.PlayerTeam, human: false);
            runtime.RegisterAi(friendly, accentId: null, talkerChance: 0f, constitutionChance: 0f);
            friendly.DebugForceCrash(killer.PlayerIndex);
            ctx.Check(played.Count == gloatsBefore + 2
                && played[^1].Trigger == AiVoiceDispatcher.GlAllyDwn
                && played[^1].Tag == killer.Name && played[^1].Clip.StartsWith("snd_id2_GL-AllyDwn"),
                $"downing a plane on the player's team gloats as #22 instead last={played[^1]}");
            PumpRadio(radio);

            // The exception: the local player's own kill addresses 24 to the player's own rig,
            // silent here because that rig resolved no voice set, and broadcasts 16 either way,
            // elected onto the wingman rather than onto the killer's side or onto the player.
            var quarry = Rig(FlightRoster.ShooterIdBase + 6, enemyTeam, human: false);
            runtime.RegisterAi(quarry, accentId: null, talkerChance: 0f, constitutionChance: 0f);
            quarry.DebugForceCrash(pilotRig.PlayerIndex);
            ctx.Check(played.Count == gloatsBefore + 3
                && played[^1].Trigger == AiVoiceDispatcher.PrEnemyDwn
                && played[^1].Tag == wingman.Name && played[^1].Clip.StartsWith("snd_id2_PR-EnemyDwn"),
                $"the player's own kill broadcasts #16 on the player's team, elected onto the wingman last={played[^1]}");
            ctx.Check(!played.Exists(line => line.Trigger == AiVoiceDispatcher.GlPlyrDwn),
                $"…and #24, addressed to a player rig that resolves no voice set, stays silent");

            // --- the shake taunt (27): the evade episode's end speaks it only once the flag has
            // cleared, which is where the original raises it. The machine is driven directly, the
            // transitions being what the runtime watches.
            PumpRadio(radio);
            var evader = Rig(FlightRoster.ShooterIdBase + 7, enemyTeam, human: false);
            var machine = new AiModeMachine(new System.Random(11));
            evader.Pilot!.Machine = machine;
            runtime.RegisterAi(evader, accentId: 12, talkerChance: 1f, constitutionChance: 1f);
            var evaderPos = evader.WorldPosition;
            var pursuerPos = evaderPos + new Vector3(0f, 0f, 300f);
            var onTail = (evaderPos - pursuerPos).Normalized();  // the pursuer's nose on the evader
            int tauntsBefore = played.Count;

            // The episode: an ordered chase takes the anchor and the chase deadline, then a hit
            // whose bite covers the whole pool fails the steady-hand roll outright, and with no
            // maneuver library the reaction is the flag alone.
            machine.Enter(AiMode.Pursue, "ordered");
            machine.NotifyDamage(1e6f, 1e6f, 100f, 100f);
            ctx.Check(machine.Mode == AiMode.Evade && machine.Evading,
                $"the failed steady-hand roll opened an evade episode mode={AiModeMachine.NameOf(machine.Mode)} flag={machine.Evading}");

            // A step inside the episode: an ordered program with nothing left to fly hands
            // straight back to evade, and neither transition is an outcome.
            machine.Enter(AiMode.EvasiveManeuver, "ordered");
            machine.Update(evaderPos, Vector3.Forward, pursuerPos, AiMode.Pursue, dt: 0.1f,
                targetNose: onTail);
            ctx.Check(machine.Mode == AiMode.Evade && machine.Evading
                && played.Count == tauntsBefore,
                $"a step between the two evade modes is the same episode and taunts nothing lines={played.Count - tauntsBefore}");

            // The end with the pursuer still inside the tail cone: the chase's own dwell reverts
            // the task while the flag stands, and nothing is spoken. That geometry belongs to the
            // pursuer's own taunt below, not to the aircraft leaving the episode.
            machine.Update(evaderPos, Vector3.Forward, pursuerPos, AiMode.Pursue,
                dt: machine.AttackDwellS + 1f, targetNose: onTail);
            ctx.Check(machine.Evading && machine.Mode is not (AiMode.Evade or AiMode.EvasiveManeuver)
                && played.Count == tauntsBefore,
                $"an evade ending with the pursuer still on the tail taunts nothing mode={AiModeMachine.NameOf(machine.Mode)} lines={played.Count - tauntsBefore}");
            PumpRadio(radio);

            // The flag clearing with the aircraft already off the two evade modes: nothing is
            // spoken, and the next hit opens a fresh episode.
            machine.Update(evaderPos, Vector3.Forward, pursuerPos, AiMode.Pursue, dt: 0.1f,
                targetNose: -onTail);
            ctx.Check(!machine.Evading && played.Count == tauntsBefore,
                $"the flag clearing outside an evade mode taunts nothing lines={played.Count - tauntsBefore}");

            // …and the shaken end, the same aircraft: the pursuer's nose falls away while the
            // evade stands, the reaction completes, and that is what 27 answers.
            machine.NotifyDamage(1e6f, 1e6f, 100f, 100f);
            machine.Update(evaderPos, Vector3.Forward, pursuerPos, AiMode.Pursue, dt: 0.1f,
                targetNose: -onTail);
            ctx.Check(played.Count == tauntsBefore + 1
                && played[^1].Trigger == AiVoiceDispatcher.TaSucShk
                && played[^1].Tag == evader.Name && played[^1].Clip.StartsWith("snd_id2_TA-SucShk"),
                $"…while one ending with the pursuer shaken taunts #27 last={(played.Count > tauntsBefore ? played[^1].ToString() : "none")}");

            // --- 28 (DS-Ally), the decoded friendly-fire arm: a round from the local player on a
            // friendly, spoken by the aircraft it STRUCK. The two silent cases run first, so the
            // 15 s cooldown the played line arms cannot be what silenced them.
            PumpRadio(radio);
            var mate = Rig(FlightRoster.ShooterIdBase + 8, AimAssist.PlayerTeam, human: false);
            runtime.RegisterAi(mate, accentId: null, talkerChance: 0f, constitutionChance: 0f);
            int allyBefore = played.Count;
            var grazed = wingman.WorldPosition + new Vector3(0f, 0f, 2f);
            wingman.TakeProjectileHit(gun, grazed, "fuselage", mate.PlayerIndex, damageScale: 0.02f);
            ctx.Check(played.Count == allyBefore,
                $"an AI round on a teammate draws nothing from this site lines={played.Count - allyBefore}");
            killer.TakeProjectileHit(gun, killer.WorldPosition + new Vector3(0f, 0f, 2f), "fuselage",
                pilotRig.PlayerIndex, damageScale: 0.02f);
            ctx.Check(played.Count == allyBefore,
                $"…and the player's round on an ENEMY draws nothing from it either lines={played.Count - allyBefore}");
            wingman.TakeProjectileHit(gun, grazed, "fuselage", pilotRig.PlayerIndex, damageScale: 0.02f);
            ctx.Check(played.Count == allyBefore + 1
                && played[^1].Trigger == AiVoiceDispatcher.DsAlly
                && played[^1].Tag == wingman.Name && played[^1].Clip.StartsWith("snd_id2_DS-Ally"),
                $"…while the player's round on a TEAMMATE draws #28 from the struck aircraft last={(played.Count > allyBefore ? played[^1].ToString() : "none")}");

            // --- WA-Turret (id 0): the gunner's own acquisition, warned once per episode and
            // spoken by the flight rather than by the gun. A carried gunner and a live pool: an
            // emplacement wants a world this suite has none of, and the pool is the vehicle list.
            PumpRadio(radio);
            var turretDefs = TurretDefs.Load(ctx.ZrdrPath);
            var carrierStats = PlaneStats.Load(ctx.ZrdrPath, TurretCarrier);
            var live = turretPool = new ProjectilePool(textures, null, null);
            ctx.Host.AddChild(live);
            runtime.WatchTurrets(live);

            FlightController PoolRig(int index, int team, bool human, string plane,
                PlaneStats rigStats, Vector3 at)
            {
                var poolModel = new PlaneBuilder(planesGamez, textures).Build(plane);
                var rig = new FlightController
                {
                    PlaneModel = poolModel,
                    Collider = PlaneCollider.Build(poolModel),
                    Damage = new PlaneDamage(rigStats.DestroyableParts),
                    PlayerIndex = index,
                    Team = team,
                    IsHumanPiloted = human,
                    Pilot = human ? null : new AiPilot(),
                    Projectiles = live,
                    UseKeyboard = false,
                    PadDevices = System.Array.Empty<int>(),
                    AllowPause = false,
                };
                rig.AddChild(poolModel);
                rig.Setup(new FlightModel(rigStats), null, new CamParams(), at, at + Vector3.Forward);
                rig.Name = $"gunrig{index}";
                ctx.Host.AddChild(rig);
                rig.PlaceHeld(at, at + Vector3.Forward); // parked: no phantom spawn velocity
                gloatRigs.Add(rig);
                return rig;
            }

            // 20 km off the rigs above, so the gunner's detection field holds these three alone.
            var gunAt = new Vector3(20000f, 500f, 0f);
            var carrier = PoolRig(FlightRoster.ShooterIdBase + 10, enemyTeam, human: false,
                TurretCarrier, carrierStats, gunAt);
            var carried = TurretController.BuildCarried(turretDefs, carrierStats,
                carrier.PlaneModel!, weapons, carrier, live);
            ctx.Check(carried.Length > 0,
                $"the {TurretCarrier}'s carried gunner builds count={carried.Length}");
            if (carried.Length == 0)
                return;
            var gunner = carried[0];
            float reach = gunner.Def.DetectionRange;
            int acquisitions = 0;
            FlightController? warnedAbout = null;
            live.TurretAcquiredPlayer += (_, about) =>
            {
                acquisitions++;
                warnedAbout = about;
            };

            // The AI decoy sits nearer than the player, so the gunner's own picker holds it: an
            // acquisition against an AI raises nothing however close it is.
            var decoy = PoolRig(FlightRoster.ShooterIdBase + 11, AimAssist.PlayerTeam, human: false,
                ctx.PlaneName, stats, gunAt + new Vector3(reach * 0.3f, 0f, 0f));
            var flown = PoolRig(FlightRoster.ShooterIdBase + 12, AimAssist.PlayerTeam, human: true,
                ctx.PlaneName, stats, gunAt + new Vector3(reach * 0.6f, 0f, 0f));
            var outOfReach = gunAt + new Vector3(reach * 4f, 0f, 0f);

            // Only the gunner is stepped: an unstepped pool moves no round, so nothing this leg
            // fires can damage a rig and add a line of its own.
            void StepGun(int frames)
            {
                for (int i = 0; i < frames; i++)
                {
                    gunner.SimStep(1f / 60f);
                }
            }

            int turretBefore = played.Count;
            StepGun(60);
            ctx.Check(ReferenceEquals(gunner.TargetSource, decoy) && acquisitions == 0
                && played.Count == turretBefore,
                $"a gunner holding the nearer AI raises nothing target={(gunner.TargetSource as FlightController)?.Name} events={acquisitions}");

            decoy.PlaceHeld(outOfReach, gunAt);
            StepGun(60);
            ctx.Check(acquisitions == 1 && ReferenceEquals(warnedAbout, flown),
                $"the player alone in the field raises the site once events={acquisitions} about={warnedAbout?.Name.ToString() ?? "none"}");
            ctx.Check(played.Count == turretBefore + 1
                && played[^1].Trigger == AiVoiceDispatcher.WaTurret
                && played[^1].Tag == wingman.Name
                && played[^1].Clip.StartsWith("snd_id2_WA-Turret"),
                $"…and the flight, not the gun, broadcasts WA-Turret on the player's team last={(played.Count > turretBefore ? played[^1].ToString() : "none")}");

            StepGun(300);
            ctx.Check(acquisitions == 1,
                $"holding the same player raises nothing further: one event per episode, not per tick events={acquisitions}");

            // The re-arm, both halves: losing the target and acquiring it again. The line itself
            // stays silent, the slot's own 15 s cooldown, which is why the EVENT is counted here.
            flown.PlaceHeld(outOfReach, gunAt);
            StepGun(60);
            ctx.Check(gunner.TargetSource == null && acquisitions == 1,
                $"the gunner loses the player with nothing new raised gate={gunner.Gate} events={acquisitions}");
            flown.PlaceHeld(gunAt + new Vector3(reach * 0.6f, 0f, 0f), gunAt);
            StepGun(60);
            ctx.Check(acquisitions == 2,
                $"…and re-acquiring it warns the flight again events={acquisitions}");


            // --- the attack pair (14 and 1-12), raised off the quarry rather than off a mode
            // edge, so a commit inside the 2 s mute window costs nothing. The second runtime
            // carries its own clock, the only way to put a transition inside that window here.
            PumpRadio(radio);
            attack = new Session.Roster.AiVoiceRuntime(voice, sounds, radio, new System.Random(9));
            ctx.Host.AddChild(attack);
            var calls = new List<(string Tag, int Trigger, string Clip)>();
            attack.LinePlayed += (tag, trigger, clip) => calls.Add((tag, trigger, clip));
            var chased = Rig(FlightRoster.ShooterIdBase + 13, AimAssist.PlayerTeam, human: true);
            var flightMate = Rig(FlightRoster.ShooterIdBase + 14, AimAssist.PlayerTeam, human: false);
            var ace = Rig(FlightRoster.ShooterIdBase + 15, enemyTeam, human: false);
            var aceModes = new AiModeMachine(new System.Random(12));
            ace.Pilot!.Machine = aceModes;
            ace.Pilot.Gunner = new AiGunner(new RandomNumberGenerator { Seed = 3 }) { Target = chased };
            // Abeam the human, nose parallel: between the two taunt cones, so this leg measures
            // the attack pair alone and the taunt geometry gets its own rigs below.
            var abeamAt = chased.WorldPosition + new Vector3(300f, 0f, 0f);
            ace.PlaceHeld(abeamAt, abeamAt + Vector3.Forward);
            attack.RegisterPlayer(chased);
            // ⚠ The flight mate is registered at 2: the bearing ids halve the talker chance, so a
            // chance of 1 would make every assertion below a coin flip on the dice rather than on
            // the raise. The halving itself is pinned by AiVoiceDispatcherTests.
            attack.RegisterAi(flightMate, accentId: 12, talkerChance: 2f, constitutionChance: 1f);
            attack.RegisterAi(ace, accentId: 12, talkerChance: 1f, constitutionChance: 1f);

            // The mission start: the ace takes the human on the first frame, well inside the
            // window, and nothing may speak yet.
            attack.Step(0.1f);
            aceModes.Enter(AiMode.Pursue, "ordered");
            ctx.Check(calls.Count == 0,
                $"a commit inside the 2 s mute window speaks nothing lines={calls.Count}");

            // …and the pair is not lost with it: the first step past the window raises both, the
            // pursuer's own #14 and one bearing call-out elected from the player's flight.
            attack.Step(2f);
            ctx.Check(calls.Count == 2 && calls[0].Trigger == AiVoiceDispatcher.WaAttack
                && calls[0].Tag == ace.Name && calls[0].Clip.StartsWith("snd_id2_WA-Attack"),
                $"…and the pursuer speaks #14 as the window lifts lines=[{string.Join(", ", calls)}]");
            ctx.Check(calls.Count == 2 && calls[1].Trigger is >= 1 and <= 12
                && calls[1].Tag == flightMate.Name && calls[1].Clip.StartsWith("snd_id2_WA-Enemy-"),
                $"…with the bearing call-out broadcast onto the player's flight last={(calls.Count > 1 ? calls[^1].ToString() : "none")}");
            PumpRadio(radio);

            // A pursue re-entry from avoid crash is the same engagement, and near terrain it
            // happens every few seconds: the raise interval must hold it silent.
            aceModes.Enter(AiMode.AvoidCrash, "ordered");
            aceModes.Enter(AiMode.Pursue, "ordered");
            attack.Step(0.1f);
            ctx.Check(calls.Count == 2,
                $"a pursue re-entry from avoid crash in the same engagement speaks nothing lines={calls.Count - 2}");

            // A fresh engagement speaks again: losing the human re-arms the raise, and the 15 s
            // slot cooldown has run out by the time the ace takes it back.
            ace.Pilot.Gunner.Target = null;
            attack.Step(AiVoiceDispatcher.SlotCooldownS);
            ctx.Check(calls.Count == 2, $"…and nothing is raised while it holds no human lines={calls.Count - 2}");
            ace.Pilot.Gunner.Target = chased;
            attack.Step(0.1f);
            ctx.Check(calls.Count == 4 && calls[2].Trigger == AiVoiceDispatcher.WaAttack
                && calls[2].Tag == ace.Name && calls[3].Trigger is >= 1 and <= 12,
                $"…while a fresh engagement past the cooldown raises the pair again lines=[{string.Join(", ", calls)}]");

            // --- the taunt pair (25/26), the decoded geometry: the same raise, addressed to the
            // pursuer, off its own NOSE against the human it holds. One pursuer per case, so the
            // lines are read by aircraft rather than by position in the list.
            PumpRadio(radio);
            var chasedAt = chased.WorldPosition;
            var asternAt = chasedAt + new Vector3(0f, 0f, 300f);
            FlightController Pursuer(int index, Vector3 at, Vector3 lookAt)
            {
                var rig = Rig(index, enemyTeam, human: false);
                rig.Pilot!.Gunner = new AiGunner(new RandomNumberGenerator { Seed = 4 })
                {
                    Target = chased,
                };
                attack.RegisterAi(rig, accentId: 12, talkerChance: 1f, constitutionChance: 1f);
                rig.PlaceHeld(at, lookAt);
                return rig;
            }

            var noseOn = Pursuer(FlightRoster.ShooterIdBase + 16, asternAt, chasedAt);
            var tailed = Pursuer(FlightRoster.ShooterIdBase + 17, asternAt,
                asternAt + (asternAt - chasedAt));
            var abeam = Pursuer(FlightRoster.ShooterIdBase + 18, abeamAt, abeamAt + Vector3.Forward);

            // …and the decoded refusal: a pursuer in its own evade reaction taunts nothing however
            // its nose sits. ⚠ The episode opens BEFORE registration, or the commit's own raise
            // would speak the taunt on the way into the pursue this aircraft evades out of.
            var busy = Rig(FlightRoster.ShooterIdBase + 19, enemyTeam, human: false);
            var busyModes = new AiModeMachine(new System.Random(14));
            busy.Pilot!.Machine = busyModes;
            busy.Pilot.Gunner = new AiGunner(new RandomNumberGenerator { Seed = 5 }) { Target = chased };
            busy.PlaceHeld(asternAt, chasedAt);
            busyModes.Enter(AiMode.Pursue, "ordered");
            busyModes.NotifyDamage(1e6f, 1e6f, 100f, 100f);
            attack.RegisterAi(busy, accentId: 12, talkerChance: 1f, constitutionChance: 1f);

            attack.Step(0.1f);
            bool Taunted(FlightController who, int trigger) => calls.Exists(line =>
                line.Tag == who.Name && line.Trigger == trigger);
            ctx.Check(Taunted(noseOn, AiVoiceDispatcher.TaFailShk)
                && calls.Exists(line => line.Tag == noseOn.Name
                    && line.Clip.StartsWith("snd_id2_TA-FailShk")),
                $"a pursuer holding the human inside its own nose cone taunts #26 lines=[{string.Join(", ", calls)}]");
            ctx.Check(Taunted(tailed, AiVoiceDispatcher.TaFailTail)
                && calls.Exists(line => line.Tag == tailed.Name
                    && line.Clip.StartsWith("snd_id2_TA-FailTail")),
                $"…while one with the human on its OWN tail taunts #25 instead");
            ctx.Check(!Taunted(abeam, AiVoiceDispatcher.TaFailShk)
                && !Taunted(abeam, AiVoiceDispatcher.TaFailTail)
                && Taunted(abeam, AiVoiceDispatcher.WaAttack),
                $"…and one holding it abeam, between the two cones, taunts neither though the raise ran");
            ctx.Check(busyModes.Evading && !Taunted(busy, AiVoiceDispatcher.TaFailShk)
                && !Taunted(busy, AiVoiceDispatcher.TaFailTail),
                $"…and a pursuer in its own evade reaction taunts nothing nose-on flag={busyModes.Evading}");

            // --- the Danger Zone praise (15): the player flying a zone out is the original's
            // own site. The line is broadcast, so one of the player's flight speaks it.
            PumpRadio(radio);
            int zoneBefore = calls.Count;
            attack.DangerZoneCompleted(chased);
            ctx.Check(calls.Count == zoneBefore + 1
                && calls[^1].Trigger == AiVoiceDispatcher.PrDngrZn
                && calls[^1].Tag == flightMate.Name
                && calls[^1].Clip.StartsWith("snd_id2_PR-D"),
                $"a Danger Zone the player completed is praised by its flight, not by the player last={(calls.Count > zoneBefore ? calls[^1].ToString() : "none")}");

            ctx.Note($"lines: {string.Join(", ", played)}; attack pair: {string.Join(", ", calls)}");
        }
        finally
        {
            foreach (var rig in gloatRigs)
            {
                rig.Free();
            }
            ai?.Free();
            attack?.Free();
            runtime?.Free();
            turretPool?.Free();
            radio?.Free();
            if (sounds != null)
            {
                sounds.FlushOneShots();
                sounds.Free();
            }
            textures.Dispose();
        }
    }

    // C22 (BL-459): a re-arm timer shared by every aircraft flying one airframe DEFINITION,
    // not restarted on the frame the damaged loop goes silent.
    [Suite("ai-engine-rearm",
        "the damaged-engine loop waits out the shared per-definition re-arm timer instead of "
        + "restarting on the frame it goes silent: nothing swaps below the 3 s floor, and a "
        + "second aircraft on the SAME airframe def that only starts silent afterward inherits "
        + "the first one's head start and swaps well short of its own 3 s; the healthy "
        + "direction keeps restoring on the very same frame throughout")]
    internal static void AiEngineRearm(TestContext ctx)
    {
        ctx.RequireData(ctx.PlanesGamezPath, $"planes gamez");
        ctx.RequireData(ctx.ZrdrPath, $"zrdr archive");
        ctx.RequireData(ctx.SoundsPath, $"sound archive (soundsh)");
        string texturesPath = SessionPaths.ChapterTextures(ctx.DataRoot, "C1");
        ctx.RequireData(texturesPath, $"C1 textures");

        var planesGamez = GameZ.Load(ctx.PlanesGamezPath);
        var soundDefs = SoundDefs.Load(ctx.ZrdrPath);
        var soundGroups = SoundDefs.LoadGroups(ctx.ZrdrPath);
        var textures = new TextureArchive(texturesPath);
        using var archive = new SoundArchive(ctx.SoundsPath);
        ProjectilePool? pool = null;
        FlightController? a = null, b = null;
        try
        {
            var live = new ProjectilePool(textures, null, null);
            pool = live;
            ctx.Host.AddChild(live);

            // The same per-def cache GameSession.BuildFlightRigs keeps, so two spawns of one
            // airframe share the same PlaneStats object and, through it, one DamagedEngineTimer.
            var aiCache = new Dictionary<string, PlaneStats>();
            PlaneStats AiStatsFor(string plane, string? aiDef)
            {
                if (aiCache.TryGetValue(plane, out var cached))
                    return cached;
                var loaded = PlaneStats.LoadForAi(ctx.ZrdrPath, plane, aiDef);
                aiCache[plane] = loaded;
                return loaded;
            }

            var spec = SessionSpec.Parse(System.Array.Empty<string>());
            var liveries = new LiveryResolver(spec, Path.Combine(ctx.DataRoot, "extracted", "rof"));
            var inputs = new AircraftAssemblyResources
            {
                PlanesGamez = planesGamez,
                StatsFor = plane => PlaneStats.Load(ctx.ZrdrPath, plane),
                AiStatsFor = AiStatsFor,
                PaintRng = new RandomNumberGenerator(),
                ZrdrPath = ctx.ZrdrPath,
                StockLoadouts = StockLoadouts.Load(),
                WeaponDefs = WeaponDefs.Load(ctx.ZrdrPath, null),
                Textures = textures,
                Shakes = ShakeDefs.Load(ctx.ZrdrPath),
            };
            var spawner = new FlightRoster(FlightRosterPolicy.From(spec), liveries, null!, ctx.Host, inputs,
                new FlightWorldBindings
                {
                    Projectiles = live,
                    Gamez = planesGamez,
                    Sounds = archive,
                    SoundDefs = soundDefs,
                    SoundGroups = soundGroups,
                },
                new HumanRosterBindings());

            var posA = new Vector3(0f, 500f, 0f);
            var posB = new Vector3(600f, 500f, 0f);
            a = spawner.SpawnAi(new AiSpawn(ctx.PlaneName, posA, posA + Vector3.Forward,
                AiPilot.HoldingCourse(posA, posA + Vector3.Forward), Team: InstantActionRuntime.EnemyTeam));
            b = spawner.SpawnAi(new AiSpawn(ctx.PlaneName, posB, posB + Vector3.Forward,
                AiPilot.HoldingCourse(posB, posB + Vector3.Forward), Team: InstantActionRuntime.EnemyTeam));

            ctx.Check(a.EngineAudio != null && b.EngineAudio != null,
                $"both AI aircraft built a real engine-audio component");
            if (a.EngineAudio is not { } engineA || b.EngineAudio is not { } engineB)
            {
                return;
            }
            engineA.Listeners = () => new[] { posA };
            engineB.Listeners = () => new[] { posB };

            const float dt = 1f / 60f;
            var drive = new EngineDrive(0.5f, 0f, 0f);
            var damagedA = new List<string>();
            var damagedB = new List<string>();
            var healthyB = new List<string>();
            bool wasThreshold = Utils.Log.ConsoleShows("sound", Utils.Log.Level.Debug);
            try
            {
                Utils.Log.Configure("sound:debug");
                using var sink = Utils.Log.PushConsoleSink(line =>
                {
                    if (!line.Contains("slot 0 -> "))
                    {
                        return;
                    }
                    bool damaged = line.Contains("snd_damagedengine");
                    bool fromA = line.Contains(a.Name.ToString());
                    if (damaged && fromA)
                        damagedA.Add(line);
                    else if (damaged)
                        damagedB.Add(line);
                    else if (!fromA)
                        healthyB.Add(line);
                });

                // Below a quarter health: the mask goes nonzero this frame, and the original does
                // not restart the damaged loop on the frame the swap is decided.
                engineA.Update(dt, drive, 0.5f, 0.1f);
                ctx.Check(damagedA.Count == 0,
                    $"the damaged loop does not swap on the frame the damage is decided (got {damagedA.Count})");

                // A alone, strictly under the 3 s floor: no possible drawn threshold is below it,
                // so this is deterministic whatever the run's seed draws.
                for (int i = 1; i < 174; i++)
                {
                    engineA.Update(dt, drive, 0.5f, 0.1f);
                }
                ctx.Check(damagedA.Count == 0,
                    $"…and still not after {174 * dt:0.00} s of A alone, short of the 3 s floor (got {damagedA.Count})");

                // B joins damaged now, inheriting A's 2.9 s head start on the SHARED timer. It
                // must cross even the highest possible threshold (5 s) well inside its OWN 3 s
                // floor, proof the timer is shared, not per-instance.
                int bFrames = 0;
                for (; bFrames < 180 && damagedB.Count == 0; bFrames++)
                {
                    engineB.Update(dt, drive, 0.5f, 0.1f);
                }
                ctx.Check(damagedB.Count == 1 && bFrames < 180,
                    $"B swaps after only {bFrames * dt:0.00} s of its OWN ticking, short of the 3 s floor a fresh timer needs (lines={damagedB.Count})");
                ctx.Check(damagedA.Count == 0,
                    $"…and A, left untouched since its own 2.9 s, still has not swapped ({damagedA.Count})");

                // The healthy direction is untouched: it restores on the very same frame, no
                // re-arm wait. A hard cut, never the crossfade the plan's own trap rejects.
                engineB.Update(dt, drive, 0.5f, 0.9f);
                ctx.Check(healthyB.Count == 1,
                    $"the healthy restore swaps back on the same frame, unaffected by the timer (got {healthyB.Count})");
            }
            finally
            {
                Utils.Log.Configure(wasThreshold ? "sound:debug" : "sound:info");
            }
        }
        finally
        {
            a?.Free();
            b?.Free();
            pool?.Free();
            textures.Dispose();
        }
    }

    // D31 (BL-079): another aircraft's guns are heard from where that aircraft is, as its engine
    // already is. The count and the position are asserted together because either alone passes a
    // broken build: one shared voice has the right count at the wrong place, and a voice pinned to
    // the listener has the right place for every aircraft at once.
    [Suite("ai-weapon-emitters",
        "every firing AI aircraft carries its OWN positional weapon voice (D31): each of two "
        + "spawned planes builds one gun-loop emitter and one dry-cue emitter on 3D players, both "
        + "riding that aircraft's world position rather than the origin, the listener or each "
        + "other, both levelled by the decoded RANGE law rather than by an engine attenuation "
        + "model, so neither player carries one or a MaxDistance of its own, neither rig carrying "
        + "the own-ship FlightAudio, every emitter on the "
        + "Effects bus at its source asset's own pitch with Doppler tracking off (CAP-09 measured "
        + "none in the original), the loop silencing past 1.1 times the cue's own authored "
        + "audible distance, the margin the sound manager leaves over the RANGE pair, while a burst "
        + "just outside that distance is still heard, with a `sound` log transition either way, and "
        + "at the distance its own pair calls audible the loop standing at the level SoundFalloff "
        + "gives that pair, where the old inverse-distance mapping's MaxDistance fade was zero")]
    internal static void AiWeaponEmitters(TestContext ctx)
    {
        ctx.RequireData(ctx.PlanesGamezPath, $"planes gamez");
        ctx.RequireData(ctx.ZrdrPath, $"zrdr archive");
        ctx.RequireData(ctx.SoundsPath, $"sound archive (soundsh)");
        string texturesPath = SessionPaths.ChapterTextures(ctx.DataRoot, "C1");
        ctx.RequireData(texturesPath, $"C1 textures");

        // Every cull and level below is the cue's own authored one. This suite reads at 1, not at
        // the reach the session ships.
        using var authored = TestContext.AtAuthoredSoundRadii();

        var planesGamez = GameZ.Load(ctx.PlanesGamezPath);
        var soundDefs = SoundDefs.Load(ctx.ZrdrPath);
        var soundGroups = SoundDefs.LoadGroups(ctx.ZrdrPath);
        var weaponDefs = WeaponDefs.Load(ctx.ZrdrPath, null);
        var textures = new TextureArchive(texturesPath);
        using var archive = new SoundArchive(ctx.SoundsPath);
        ProjectilePool? pool = null;
        FlightController? a = null, b = null;
        try
        {
            var live = new ProjectilePool(textures, archive, soundDefs, soundGroups: soundGroups);
            pool = live;
            ctx.Host.AddChild(live);

            var spec = SessionSpec.Parse(System.Array.Empty<string>());
            var liveries = new LiveryResolver(spec, Path.Combine(ctx.DataRoot, "extracted", "rof"));
            var inputs = new AircraftAssemblyResources
            {
                PlanesGamez = planesGamez,
                StatsFor = plane => PlaneStats.Load(ctx.ZrdrPath, plane),
                AiStatsFor = (plane, aiDef) => PlaneStats.LoadForAi(ctx.ZrdrPath, plane, aiDef),
                PaintRng = new RandomNumberGenerator(),
                ZrdrPath = ctx.ZrdrPath,
                StockLoadouts = StockLoadouts.Load(),
                WeaponDefs = weaponDefs,
                Textures = textures,
                Shakes = ShakeDefs.Load(ctx.ZrdrPath),
            };
            // The listener sits abeam both spawns, inside a gun loop's 150 m authored range but off
            // either aircraft, so an emitter pinned to the listener would fail the position check;
            // the two spawns are 90 m apart, so one shared voice could not pass for two either.
            var ears = new List<Vector3> { new(45f, 500f, -30f) };
            var spawner = new FlightRoster(FlightRosterPolicy.From(spec), liveries, null!, ctx.Host, inputs,
                new FlightWorldBindings
                {
                    Projectiles = live,
                    Gamez = planesGamez,
                    Sounds = archive,
                    SoundDefs = soundDefs,
                    SoundGroups = soundGroups,
                    HumanPositions = () => ears,
                },
                new HumanRosterBindings());

            var posA = new Vector3(0f, 500f, 0f);
            var posB = new Vector3(90f, 500f, 0f);
            a = spawner.SpawnAi(new AiSpawn(ctx.PlaneName, posA, posA + Vector3.Forward,
                AiPilot.HoldingCourse(posA, posA + Vector3.Forward), Team: InstantActionRuntime.EnemyTeam));
            b = spawner.SpawnAi(new AiSpawn(ctx.PlaneName, posB, posB + Vector3.Forward,
                AiPilot.HoldingCourse(posB, posB + Vector3.Forward), Team: InstantActionRuntime.EnemyTeam));

            ctx.Check(a.Audio == null && b.Audio == null,
                $"neither AI rig carries the own-ship FlightAudio, which is what left their guns silent");
            ctx.Check(a.WeaponAudio != null && b.WeaponAudio != null,
                $"both AI aircraft built a positional weapon voice (a={a.WeaponAudio != null} b={b.WeaponAudio != null})");
            if (a.WeaponAudio is not { } weaponA || b.WeaponAudio is not { } weaponB)
            {
                return;
            }

            var culls = new List<string>();
            string[] lines = System.Array.Empty<string>();
            bool wasDebug = Utils.Log.ConsoleShows("sound", Utils.Log.Level.Debug);
            try
            {
                Utils.Log.Configure("sound:debug");
                using var sink = Utils.Log.PushConsoleSink(line =>
                {
                    if (line.Contains("ai weapons ") && (line.Contains(" culled ") || line.Contains(" sounding ")))
                        culls.Add(line);
                });

                // Hold both triggers down and step: the loop is started from ApplyFireOutcome, so
                // nothing here reaches into the audio component to fake a burst.
                foreach (var rig in new[] { a, b })
                {
                    rig.AutoFire = true;
                    rig.InfiniteAmmo = true;
                }
                const float dt = 1f / 60f;
                for (int i = 0; i < 30; i++)
                {
                    a.SimStep(dt);
                    b.SimStep(dt);
                }

                CheckWeaponVoice(ctx, a, weaponA, weaponDefs, soundDefs);
                CheckWeaponVoice(ctx, b, weaponB, weaponDefs, soundDefs);
                var here = weaponA.Emitters();
                var there = weaponB.Emitters();
                ctx.Check(here.Count > 0 && there.Count > 0
                    && here[0].Position.DistanceTo(there[0].Position) > 50f,
                    $"the two aircraft's loops are {(here.Count > 0 && there.Count > 0 ? here[0].Position.DistanceTo(there[0].Position) : 0f):0} m apart, so this is one voice per plane");
                ctx.Check(here.Count > 0 && there.Count > 0
                    && here[0].Position.DistanceTo(ears[0]) > 20f
                    && there[0].Position.DistanceTo(ears[0]) > 20f,
                    $"…and neither sits on the listener, which is what a voice pinned to the ear would do");
                ctx.Check(culls.Count(l => l.Contains(" sounding ")) == 2,
                    $"both voices logged the transition into earshot (got {culls.Count(l => l.Contains(" sounding "))})");

                // The cull, driven from the listener rather than by moving the aeroplane. The two
                // ears below straddle the 1.1x, which is what separates this cull from one taken at
                // the RANGE pair itself. Each ratio is measured after its step, the aeroplane flies.
                float audible = here.Count > 0 ? here[0].RangeMax : 0f;
                ctx.Check(audible > 0f
                    && Mathf.IsEqualApprox(weaponA.LoopCull, audible * VoiceCullMargin),
                    $"the loop culls at {weaponA.LoopCull:0} m, the {VoiceCullMargin:0.0}x of the {audible:0} m its definition calls audible");
                ears[0] = a.GlobalPosition + new Vector3(audible * 1.15f, 0f, 0f);
                a.SimStep(dt);
                float wellOut = a.GlobalPosition.DistanceTo(ears[0]) / Mathf.Max(audible, 1f);
                ctx.Check(!weaponA.LoopSounding && wellOut > VoiceCullMargin,
                    $"a burst {wellOut:0.00}x the {audible:0} m the definition calls audible is silent, past the {weaponA.LoopCull:0} m cull");
                ears[0] = a.GlobalPosition + new Vector3(audible * 1.05f, 0f, 0f);
                a.SimStep(dt);
                float justOut = a.GlobalPosition.DistanceTo(ears[0]) / Mathf.Max(audible, 1f);
                ctx.Check(weaponA.LoopSounding && justOut > 1f && justOut < VoiceCullMargin,
                    $"…and {justOut:0.00}x it is heard again, inside that cull");

                // The level inside the band, which no cull verdict can see. At the distance this
                // caliber calls audible the decoded law is thirty down, where a MaxDistance fade
                // would be zero. The decibel of slack is the step the aeroplane flies.
                ears[0] = a.GlobalPosition + new Vector3(audible, 0f, 0f);
                a.SimStep(dt);
                float atEdge = a.GlobalPosition.DistanceTo(ears[0]);
                var loopDef = soundDefs[here[0].Name];
                float wantEdge = SoundFalloff.GainDb(atEdge, loopDef.RangeMin, loopDef.RangeMax,
                    loopDef.Volume);
                ctx.Check(weaponA.LoopSounding && wantEdge > SoundFalloff.FloorDb
                    && Mathf.Abs(weaponA.LoopGainDb - wantEdge) < 1f,
                    $"and from {atEdge:0} m it plays at {weaponA.LoopGainDb:0.0} dB, the decoded law's own level for {here[0].Name}'s RANGE [{loopDef.RangeMin:0}, {loopDef.RangeMax:0}] ({wantEdge:0.0} dB), well above the {SoundFalloff.FloorDb:0} dB floor");
                ctx.Check(culls.Count(l => l.Contains(" culled ")) >= 1,
                    $"the `sound` log carries the cull transition, the pairing INSTR-45 asks for (lines={culls.Count})");
                // ⚠ Snapshot before noting: ctx.Note echoes each line to the console, which the sink
                // above would match and append to the very list being walked.
                lines = culls.ToArray();
            }
            finally
            {
                Utils.Log.Configure(wasDebug ? "sound:debug" : "sound:info");
            }
            foreach (string line in lines)
            {
                ctx.Note($"{line}");
            }
        }
        finally
        {
            a?.Free();
            b?.Free();
            pool?.Free();
            textures.Dispose();
        }
    }

    // One aircraft, one listener model. The seam identity and the cull verdicts are asserted
    // together because either alone passes a broken build: a bound seam still proves nothing about
    // what the cull reads, and a cull verdict taken with the camera parked far away would pass on
    // the camera fallback too.
    [Suite("ai-engine-listeners",
        "an AI aircraft's engine voice and its weapon voice read the SAME nearest-human seam the "
        + "session binds, so one aeroplane answers one listener model rather than one measure per "
        + "voice: the engine loop sounds while a human pane is inside the cull distance, the "
        + "NEAREST pane decides it (never the first entry), it falls silent only when every pane "
        + "is outside and sounds again on any one of them coming back, in a two-pane and a "
        + "four-pane session alike, and it does none of that off the viewport camera, parked ON "
        + "the aircraft throughout so a verdict read from it could only say audible, with a "
        + "`sound` log line either way")]
    internal static void AiEngineListeners(TestContext ctx)
    {
        ctx.RequireData(ctx.PlanesGamezPath, $"planes gamez");
        ctx.RequireData(ctx.ZrdrPath, $"zrdr archive");
        ctx.RequireData(ctx.SoundsPath, $"sound archive (soundsh)");
        string texturesPath = SessionPaths.ChapterTextures(ctx.DataRoot, "C1");
        ctx.RequireData(texturesPath, $"C1 textures");

        var planesGamez = GameZ.Load(ctx.PlanesGamezPath);
        var soundDefs = SoundDefs.Load(ctx.ZrdrPath);
        var soundGroups = SoundDefs.LoadGroups(ctx.ZrdrPath);
        var textures = new TextureArchive(texturesPath);
        using var archive = new SoundArchive(ctx.SoundsPath);
        ProjectilePool? pool = null;
        FlightController? ai = null;
        // Put back on the way out: the harness camera is shared with every other suite, and this
        // one parks it deliberately below.
        var cameraWas = ctx.Camera.GlobalTransform;
        try
        {
            var live = new ProjectilePool(textures, archive, soundDefs, soundGroups: soundGroups);
            pool = live;
            ctx.Host.AddChild(live);

            var spec = SessionSpec.Parse(System.Array.Empty<string>());
            var liveries = new LiveryResolver(spec, Path.Combine(ctx.DataRoot, "extracted", "rof"));
            var inputs = new AircraftAssemblyResources
            {
                PlanesGamez = planesGamez,
                StatsFor = plane => PlaneStats.Load(ctx.ZrdrPath, plane),
                AiStatsFor = (plane, aiDef) => PlaneStats.LoadForAi(ctx.ZrdrPath, plane, aiDef),
                PaintRng = new RandomNumberGenerator(),
                ZrdrPath = ctx.ZrdrPath,
                StockLoadouts = StockLoadouts.Load(),
                WeaponDefs = WeaponDefs.Load(ctx.ZrdrPath, null),
                Textures = textures,
                Shakes = ShakeDefs.Load(ctx.ZrdrPath),
            };
            // Two panes, the splitscreen shape the two answers used to differ on. Both start
            // inside the cull so the first frame has a verdict to move away from.
            var pos = new Vector3(0f, 500f, 0f);
            float cull = EngineAudioCurves.CullDistance;
            var panes = new List<Vector3> { pos + new Vector3(300f, 0f, 0f), pos + new Vector3(900f, 0f, 0f) };
            var spawner = new FlightRoster(FlightRosterPolicy.From(spec), liveries, null!, ctx.Host, inputs,
                new FlightWorldBindings
                {
                    Projectiles = live,
                    Gamez = planesGamez,
                    Sounds = archive,
                    SoundDefs = soundDefs,
                    SoundGroups = soundGroups,
                    HumanPositions = () => panes,
                },
                new HumanRosterBindings());

            ai = spawner.SpawnAi(new AiSpawn(ctx.PlaneName, pos, pos + Vector3.Forward,
                AiPilot.HoldingCourse(pos, pos + Vector3.Forward), Team: InstantActionRuntime.EnemyTeam));

            ctx.Check(ai.EngineAudio != null && ai.WeaponAudio != null,
                $"the spawned aircraft built both voices (engine={ai.EngineAudio != null} weapons={ai.WeaponAudio != null})");
            if (ai.EngineAudio is not { } engine || ai.WeaponAudio is not { } weapons)
            {
                return;
            }
            ctx.Check(engine.Listeners != null && ReferenceEquals(engine.Listeners, weapons.Listeners),
                $"both voices hold the SAME listener seam (engine bound={engine.Listeners != null})");

            // ⚠ The control on every cull check below: the camera the old engine path fell back to
            // is parked ON the aircraft, so a verdict read off it could only ever say audible.
            ctx.Camera.GlobalPosition = ai.GlobalPosition;
            float toCamera = ctx.Camera.GlobalPosition.DistanceTo(ai.GlobalPosition);
            ctx.Check(toCamera < 1f,
                $"the viewport camera sits on the aircraft, {toCamera:0} m off, so no silence below can be its answer");

            var culls = new List<string>();
            string[] lines = System.Array.Empty<string>();
            bool wasDebug = Utils.Log.ConsoleShows("sound", Utils.Log.Level.Debug);
            try
            {
                Utils.Log.Configure("sound:debug");
                using var sink = Utils.Log.PushConsoleSink(line =>
                {
                    if (line.Contains("ai engine ") && (line.Contains(" culled ") || line.Contains(" audible ")))
                        culls.Add(line);
                });

                const float dt = 1f / 60f;
                var drive = new EngineDrive(0.5f, 0f, 0f);
                void Step() => engine.Update(dt, drive, 0.5f, 1f);

                Step();
                ctx.Check(engine.EngineSounding,
                    $"the loop sounds with a pane {panes[0].DistanceTo(ai.GlobalPosition):0} m off, inside the {cull:0} m cull");

                // The nearest pane decides, not the first: the near one moves to the back of the
                // list and the verdict must not move with it.
                panes[0] = pos + new Vector3(cull * 4f, 0f, 0f);
                Step();
                ctx.Check(engine.EngineSounding,
                    $"…and still sounds with the near pane second in the list, {panes[1].DistanceTo(ai.GlobalPosition):0} m off");

                // Every pane outside now. This is the frame the old build could not reach: with no
                // seam bound it measured the camera above and stayed audible.
                panes[1] = pos + new Vector3(cull * 5f, 0f, 0f);
                Step();
                ctx.Check(!engine.EngineSounding,
                    $"with BOTH panes past the cull ({panes[0].DistanceTo(ai.GlobalPosition):0} m and {panes[1].DistanceTo(ai.GlobalPosition):0} m) the loop is silent");

                // One pane back inside, the second half of the pairing: a component that stopped
                // for good would pass the check above on its own.
                panes[1] = pos + new Vector3(300f, 0f, 0f);
                Step();
                ctx.Check(engine.EngineSounding,
                    $"…and sounds again the moment one pane is back inside it, {panes[1].DistanceTo(ai.GlobalPosition):0} m off");

                // The other splitscreen shape. The measure is over EVERY pane, so a fourth one on
                // its own inside is enough to sound the loop, and taking it out is enough to stop it.
                panes[0] = pos + new Vector3(cull * 4f, 0f, 0f);
                panes[1] = pos + new Vector3(cull * 5f, 0f, 0f);
                panes.Add(pos + new Vector3(cull * 6f, 0f, 0f));
                panes.Add(pos + new Vector3(600f, 0f, 0f));
                Step();
                ctx.Check(engine.EngineSounding,
                    $"in a FOUR-pane session the one pane inside, the fourth, keeps the loop sounding from {panes[3].DistanceTo(ai.GlobalPosition):0} m");
                panes[3] = pos + new Vector3(cull * 7f, 0f, 0f);
                Step();
                ctx.Check(!engine.EngineSounding,
                    $"…and taking that last one out past the {cull:0} m cull silences it again");
                ctx.Check(culls.Count(l => l.Contains(" culled ")) >= 1
                    && culls.Count(l => l.Contains(" audible ")) >= 2,
                    $"the `sound` log carries both verdicts (lines={culls.Count})");
                // ⚠ Snapshot before noting: ctx.Note echoes each line to the console, which the sink
                // above would match and append to the very list being walked.
                lines = culls.ToArray();
            }
            finally
            {
                Utils.Log.Configure(wasDebug ? "sound:debug" : "sound:info");
            }
            foreach (string line in lines)
            {
                ctx.Note($"{line}");
            }
        }
        finally
        {
            ctx.Camera.GlobalTransform = cameraWas;
            ai?.Free();
            pool?.Free();
            textures.Dispose();
        }
    }

    // The radio's one player. Typed as the flat AudioStreamPlayer, so a channel rebuilt on a 3D
    // player reads null here and fails the suites that use this.
    internal static AudioStreamPlayer? RadioPlayer(MissionRadio radio)
    {
        foreach (var child in radio.GetChildren())
        {
            if (child is AudioStreamPlayer p)
            {
                return p;
            }
        }
        return null;
    }

    // The level MissionRadio plays a definition at: its authored VOLUME and nothing else.
    internal static float FlatDb(IReadOnlyDictionary<string, SoundDef> defs, string name) =>
        Mathf.LinearToDb(System.Math.Max(defs[name].Volume, 0.0001f));

    // Steps the radio until its channel and queue are empty, at most a minute of clips.
    internal static void PumpRadio(MissionRadio radio)
    {
        for (int i = 0; i < 600 && (radio.OnAir != null || radio.Pending > 0); i++)
        {
            radio.Tick(0.1f);
        }
    }

    [Suite("ai-net-follow",
        "net following (B5): a real chapter net resolves by id and by name, its node fields " +
        "ride along inert on an AIRCRAFT walk, and an AI plane with a net-following pilot captures node after " +
        "node with every hop an EDGE of the graph, never node order. Then the same net, " +
        "anchored (BL-377), rides its target 6 km east and the plane laps the MOVED ring at " +
        "its authored altitude, never seating on the edgeless anchor node")]
    internal static void AiNetFollow(TestContext ctx)
    {
        ctx.RequireData(ctx.PlanesGamezPath, $"planes gamez");
        ctx.RequireData(ctx.ZrdrPath, $"zrdr archive");
        string texturesPath = SessionPaths.ChapterTextures(ctx.DataRoot, "C1");
        ctx.RequireData(texturesPath, $"C1 textures");
        string chapterZrdr = SessionPaths.ChapterZrdr(ctx.DataRoot, "C1");
        ctx.RequireData(chapterZrdr, $"C1 zrdr");

        // The chapter graph resolves both ways the data references it: by id (aiv field 0)
        // and by neindex name (egen/zeppelins/objectives), case-insensitively.
        var nets = AiNets.Load(chapterZrdr);
        var byId = AiNets.ById(nets, 10);
        ctx.Check(byId is { Name: "M4ReinfAce" }, $"net id 10 resolves to M4ReinfAce");
        var byName = AiNets.ByName(nets, "m4reinface");
        ctx.Check(byId != null && ReferenceEquals(byId, byName),
            $"the name lookup (case-insensitive) finds the same net");
        ctx.Check(byId != null && ReferenceEquals(byId, AiNets.Resolve(nets, "10"))
            && ReferenceEquals(byId, AiNets.Resolve(nets, "M4ReinfAce")),
            $"Resolve takes either spelling");
        if (byId == null)
            return;
        var net = byId;

        // The trailer names the player at node 10, and that node carries no edge: the shipped
        // shape of all 76 anchored nets, and why the seat scan has to skip edgeless nodes.
        ctx.Check(net.Trailer is { NodeIndex: 10, Name: "player" },
            $"the trailer [10, player] rides the net trailer={net.Trailer?.ToString() ?? "-"}");
        bool anchorEdgeless = true;
        foreach (var (a, b) in net.Edges)
        {
            if (a == 10 || b == 10)
                anchorEdgeless = false;
        }
        ctx.Check(anchorEdgeless, $"…and the anchor node 10 is parked off the ring, edgeless");

        var planesGamez = GameZ.Load(ctx.PlanesGamezPath);
        var stats = PlaneStats.Load(ctx.ZrdrPath, ctx.PlaneName);
        var textures = new TextureArchive(texturesPath);
        FlightController? ai = null;
        try
        {
            // The ai-actor pattern: a pilot-driven controller, no camera, no HUD, no devices,
            // spawned on the net's first node, its follower seeded with a fixed value so the
            // route repeats.
            var spawn = net.Nodes[0].Position;
            var look = net.Nodes[1].Position;
            var pilot = AiPilot.HoldingCourse(spawn, look);
            var follower = new AiNetFollower(net, new System.Random(1));
            pilot.Patrol = follower;
            var aiModel = new PlaneBuilder(planesGamez, textures).Build(ctx.PlaneName);
            ai = new FlightController
            {
                PlaneModel = aiModel,
                Collider = PlaneCollider.Build(aiModel),
                PlayerIndex = FlightRoster.ShooterIdBase,
                IsHumanPiloted = false,
                Pilot = pilot,
                UseKeyboard = false,
                PadDevices = System.Array.Empty<int>(),
                AllowPause = false,
            };
            ai.AddChild(aiModel);
            ai.Setup(new FlightModel(stats), null, new CamParams(), spawn, look);
            ctx.Host.AddChild(ai);

            // Fly until the follower has captured five nodes (or the budget runs out), logging
            // each target change so a hop off the edge list cannot hide.
            var hops = new List<(int From, int To)>();
            int last = follower.CurrentIndex;
            const int wanted = 5;
            int steps = 0, budget = 120 * 60;
            while (follower.Advances < wanted && steps < budget)
            {
                steps++;
                ai.SimStep(1f / 60f);
                if (follower.CurrentIndex != last)
                {
                    if (last >= 0)
                        hops.Add((last, follower.CurrentIndex));
                    last = follower.CurrentIndex;
                }
            }
            ctx.Check(follower.Advances >= wanted,
                $"the plane captures {wanted} net nodes advances={follower.Advances} in {steps / 60f:0} s of sim");
            bool allEdges = hops.Count > 0;
            foreach (var (from, to) in hops)
            {
                if (!net.Edges.Contains((from, to)) && !net.Edges.Contains((to, from)))
                    allEdges = false;
            }
            ctx.Check(allEdges,
                $"every hop is an EDGE of the graph hops={string.Join(" ", hops.ConvertAll(h => $"{h.From}→{h.To}"))}");
            ctx.Check(Mathf.Abs(ai.WorldPosition.Y - 400f) < 250f,
                $"…within the placeholder law's altitude leash of the net's 400 m y={ai.WorldPosition.Y:0}");

            // The same net, now RIDING a stand-in for the player 6 km east of where it
            // was authored. The ring must move with it and keep its authored 400 m; the plane
            // must fly the moved ring, not the one in the file.
            var trailed = net.Nodes[10].Position + new Vector3(6000f, -300f, 0f);
            var anchoredFollower = new AiNetFollower(net, new System.Random(1),
                trailerTarget: () => trailed);
            ctx.Check(anchoredFollower.Anchored
                && anchoredFollower.NodePosition(0).IsEqualApprox(
                    net.Nodes[0].Position + new Vector3(6000f, 0f, 0f)),
                $"the anchored net rides its target 6 km east, Y untouched node0={anchoredFollower.NodePosition(0)}");
            pilot.Patrol = anchoredFollower;
            ai.Activate(anchoredFollower.NodePosition(0), anchoredFollower.NodePosition(1));
            bool seatedOnAnchor = false;
            for (steps = 0; anchoredFollower.Advances < 3 && steps < budget; steps++)
            {
                ai.SimStep(1f / 60f);
                if (anchoredFollower.CurrentIndex == 10)
                    seatedOnAnchor = true;   // the edgeless anchor is never a flight target
            }
            ctx.Check(anchoredFollower.Advances >= 3 && !seatedOnAnchor,
                $"…and the plane laps the MOVED ring advances={anchoredFollower.Advances} in {steps / 60f:0} s of sim");
            float onMoved = ai.WorldPosition.DistanceTo(anchoredFollower.CurrentTarget);
            float onAuthored = ai.WorldPosition.DistanceTo(net.Nodes[anchoredFollower.CurrentIndex].Position);
            ctx.Check(onMoved < onAuthored,
                $"…flying the ridden ring, not the authored one moved={onMoved:0} m authored={onAuthored:0} m");
        }
        finally
        {
            ai?.Free();
            textures.Dispose();
        }
    }

    // The netted half of the AI fork, watched on a stage with no chapter behind it. Everything here
    // is the production path rather than a stand-in: EmptyStage.ResolveNet is the lookup a --ai=
    // net token makes before it tries a chapter's neindex, the spawn goes through the real
    // FlightRoster so the gates under test are the ones the assembler seated from the shipped
    // tables, and the volume write is CampaignRosterPlan.ApplyVolumes, the one call the CLI spawn
    // and a campaign net assignment share.
    [Suite("empty-stage-net",
        "the empty stage's built-in patrol net: its reserved name resolves to a ring the stage "
        + "builds in code, which no chapter's neindex answers to and which does not swallow a "
        + "chapter net name, the ring closes over the grid origin at the stage's own altitude, a "
        + "plane spawned onto it through the real AI assembler captures node after node with every "
        + "hop an EDGE of the graph, and the net's own volumes reach that plane's mode machine, "
        + "replacing all three of the gates player.json and the airframe def seated")]
    internal static void EmptyStageNet(TestContext ctx)
    {
        ctx.RequireData(ctx.PlanesGamezPath, $"planes gamez");
        ctx.RequireData(ctx.ZrdrPath, $"zrdr archive");
        string texturesPath = SessionPaths.ChapterTextures(ctx.DataRoot, "C1");
        ctx.RequireData(texturesPath, $"C1 textures");
        string chapterZrdr = SessionPaths.ChapterZrdr(ctx.DataRoot, "C1");
        ctx.RequireData(chapterZrdr, $"C1 zrdr");

        // The built-in has to answer first, since --stage=empty has no index to fall back on, and
        // it may not shadow an authored name while doing so.
        var net = EmptyStage.ResolveNet(EmptyStage.PatrolNetName);
        ctx.Check(net != null && ReferenceEquals(net, EmptyStage.ResolveNet("GRID")),
            $"'{EmptyStage.PatrolNetName}' resolves to the built-in ring, case-insensitively");
        ctx.Check(EmptyStage.ResolveNet("M4ReinfAce") == null,
            $"…and a chapter net name falls straight through to the neindex lookup");
        var chapterNets = AiNets.Load(chapterZrdr);
        ctx.Check(AiNets.Resolve(chapterNets, EmptyStage.PatrolNetName) == null,
            $"…while none of C1's {chapterNets.Count} nets carries that name to be hidden");
        if (net == null)
            return;

        ctx.Check(net.Nodes.Count == EmptyStage.PatrolRingNodes && net.Trailer == null,
            $"the ring holds {net.Nodes.Count} nodes and no trailer, so it rides nothing and repeats");
        bool onRing = true;
        foreach (var node in net.Nodes)
        {
            onRing &= Mathf.Abs(new Vector2(node.Position.X, node.Position.Z).Length()
                - EmptyStage.PatrolRingRadius) < 1f
                && Mathf.IsEqualApprox(node.Position.Y, EmptyStage.SpawnAltitude);
        }
        ctx.Check(onRing,
            $"…every node {EmptyStage.PatrolRingRadius:0} m from the grid origin at {EmptyStage.SpawnAltitude:0} m");
        var degree = new int[net.Nodes.Count];
        foreach (var (a, b) in net.Edges)
        {
            degree[a]++;
            degree[b]++;
        }
        bool closed = net.Edges.Count == EmptyStage.PatrolRingNodes && degree.All(d => d == 2);
        ctx.Check(closed,
            $"…and its {net.Edges.Count} edges close the loop, every node degree 2, so the walk never dead-ends");

        var planesGamez = GameZ.Load(ctx.PlanesGamezPath);
        var skills = AiSkills.Load(ctx.ZrdrPath);
        var stats = PlaneStats.LoadForAi(ctx.ZrdrPath, ctx.PlaneName, null);
        var textures = new TextureArchive(texturesPath);
        Node3D? stageRoot = null;
        ProjectilePool? pool = null;
        FlightController? ai = null;
        try
        {
            // The stage itself, collider and all, so the plane patrols over the same ground a
            // --stage=empty launch gives it rather than through empty space.
            stageRoot = EmptyStage.Build(collision: true).Root;
            ctx.Host.AddChild(stageRoot);
            var live = new ProjectilePool(textures, null, null);
            pool = live;
            ctx.Host.AddChild(live);

            var spec = SessionSpec.Parse(new[] { "--stage=empty" });
            var liveries = new LiveryResolver(spec, Path.Combine(ctx.DataRoot, "extracted", "rof"));
            var inputs = new AircraftAssemblyResources
            {
                PlanesGamez = planesGamez,
                StatsFor = plane => PlaneStats.Load(ctx.ZrdrPath, plane),
                AiStatsFor = (plane, aiDef) => PlaneStats.LoadForAi(ctx.ZrdrPath, plane, aiDef),
                PaintRng = new RandomNumberGenerator(),
                ZrdrPath = ctx.ZrdrPath,
                StockLoadouts = StockLoadouts.Load(),
                WeaponDefs = WeaponDefs.Load(ctx.ZrdrPath, null),
                Textures = textures,
                Shakes = ShakeDefs.Load(ctx.ZrdrPath),
            };
            // worldEffects null!: never dereferenced, as in inert-aircraft above.
            var spawner = new FlightRoster(FlightRosterPolicy.From(spec), liveries, null!, ctx.Host,
                inputs, new FlightWorldBindings { Projectiles = live, Gamez = planesGamez },
                new HumanRosterBindings());

            // The --ai= net branch, step for step: a follower over the resolved net, the spawn
            // seated on node 0 looking at node 1, then the net's volumes onto the armed machine.
            var follower = new AiNetFollower(net, new System.Random(1));
            var pos = follower.NodePosition(0);
            var look = follower.NodePosition(1);
            var pilot = AiPilot.HoldingCourse(pos, look);
            pilot.Patrol = follower;
            ai = spawner.SpawnAi(new AiSpawn(ctx.PlaneName, pos, look, pilot,
                Team: InstantActionRuntime.EnemyTeam));
            ctx.Check(pilot.Machine != null, $"the assembler arms a mode machine on the CLI spawn");
            if (pilot.Machine is not { } machine)
                return;
            ctx.Check(Mathf.IsEqualApprox(machine.ActivationRange, skills.MinAiActiveDist)
                && Mathf.IsEqualApprox(machine.AttackRange, stats.AiAttackRange)
                && Mathf.IsEqualApprox(machine.ReturnRange, stats.AiReturnRange),
                $"…seated on the shipped defaults so far: activation {machine.ActivationRange:0} m, attack {machine.AttackRange:0} m, return {machine.ReturnRange:0} m");
            CampaignRosterPlan.ApplyVolumes(machine, net.Volumes, skills.MinAiActiveDist);
            ctx.Check(Mathf.IsEqualApprox(machine.ActivationRange, net.Volumes.Activation.Radius)
                && Mathf.IsEqualApprox(machine.AttackRange, net.Volumes.Attack.Radius)
                && Mathf.IsEqualApprox(machine.ReturnRange, net.Volumes.Return.Radius),
                $"…and takes the NET's volumes over every one of them: activation {machine.ActivationRange:0} m, attack {machine.AttackRange:0} m, return {machine.ReturnRange:0} m");
            ctx.Check(machine.ActivationRange > skills.MinAiActiveDist
                && machine.AttackRange < stats.AiAttackRange
                && machine.ReturnRange < stats.AiReturnRange,
                $"…three gates no unnetted spawn could be sitting on");
            ctx.Note($"gates: {skills.MinAiActiveDist:0}/{stats.AiAttackRange:0}/{stats.AiReturnRange:0} m unnetted, {machine.ActivationRange:0}/{machine.AttackRange:0}/{machine.ReturnRange:0} m on the ring");

            // Fly it, logging each target change, so a hop off the edge list cannot hide.
            var hops = new List<(int From, int To)>();
            int last = follower.CurrentIndex;
            const int wanted = 5;
            int steps = 0, budget = 120 * 60;
            while (follower.Advances < wanted && steps < budget)
            {
                steps++;
                ai.SimStep(1f / 60f);
                if (follower.CurrentIndex != last)
                {
                    if (last >= 0)
                        hops.Add((last, follower.CurrentIndex));
                    last = follower.CurrentIndex;
                }
            }
            ctx.Check(follower.Advances >= wanted,
                $"the plane captures {wanted} nodes of the built-in ring advances={follower.Advances} in {steps / 60f:0} s of sim");
            bool allEdges = hops.Count > 0;
            foreach (var (from, to) in hops)
            {
                if (!net.Edges.Contains((from, to)) && !net.Edges.Contains((to, from)))
                    allEdges = false;
            }
            ctx.Check(allEdges,
                $"every hop is an EDGE of the ring hops={string.Join(" ", hops.ConvertAll(h => $"{h.From}→{h.To}"))}");
            float flown = new Vector2(ai.WorldPosition.X, ai.WorldPosition.Z).Length();
            ctx.Check(Mathf.Abs(flown - EmptyStage.PatrolRingRadius) < 400f,
                $"…and it is still lapping the ring about the grid origin r={flown:0} m, y={ai.WorldPosition.Y:0} m");
            ctx.Note($"walk: {follower.Advances} advance(s) in {steps / 60f:0.0} s of sim, hops {string.Join(" ", hops.ConvertAll(h => $"{h.From}>{h.To}"))}");
        }
        finally
        {
            ai?.Free();
            pool?.Free();
            stageRoot?.Free();
            textures.Dispose();
        }
    }

    // One aircraft's weapon voice: the emitters it holds, where they are, and whose distance model
    // they took. The position is compared against the CONTROLLER, not against the spawn point, since
    // a stepped rig has flown since it spawned.
    private static void CheckWeaponVoice(TestContext ctx, FlightController rig, AiWeaponAudio audio,
        WeaponDefs weapons, IReadOnlyDictionary<string, SoundDef> defs)
    {
        var emitters = audio.Emitters();
        string name = rig.Name.ToString();
        ctx.Check(emitters.Count == 2,
            $"{name} holds one gun loop and one dry cue (got {emitters.Count}: {string.Join(", ", emitters.Select(e => e.Name))})");
        var at = rig.GlobalPosition;
        foreach (var (cue, pos, rangeMax) in emitters)
        {
            ctx.Check(pos.DistanceTo(at) < 1f,
                $"{name}'s {cue} plays from the aircraft at ({pos.X:0},{pos.Y:0},{pos.Z:0}), not the origin (plane at ({at.X:0},{at.Y:0},{at.Z:0}))");
            ctx.Check(defs.TryGetValue(cue, out var def) && Mathf.IsEqualApprox(rangeMax, def!.RangeMax),
                $"…and takes {cue}'s own RANGE audible distance {rangeMax:0} m");
        }
        // ⚠ The pitch guard is the D31 trap made a check, not a formality: CAP-09 measured no Doppler
        // at all on the original's world emitters, and Godot's 3D player would take a shift from its
        // own tracking mode without a line of ours asking for one.
        int players = 0;
        foreach (var child in audio.GetChildren())
        {
            if (child is not AudioStreamPlayer3D player)
            {
                continue;
            }
            players++;
            ctx.Check(player.DopplerTracking == AudioStreamPlayer3D.DopplerTrackingEnum.Disabled,
                $"{name}'s emitter {players} carries no Doppler tracking (got {player.DopplerTracking})");
            ctx.Check(Mathf.IsEqualApprox(player.PitchScale, 1f),
                $"…and plays its source asset's own pitch, {player.PitchScale:0.000}");
            ctx.Check(player.Bus.ToString() == AudioBuses.Effects,
                $"…on the {AudioBuses.Effects} bus, not Master (got {player.Bus})");
            ctx.Check(player.AttenuationModel == AudioStreamPlayer3D.AttenuationModelEnum.Disabled
                && !(player.MaxDistance > 0f),
                $"…carrying no engine attenuation model ({player.AttenuationModel}) and no MaxDistance ({player.MaxDistance:0}), so the decoded law is the only curve on it");
        }
        ctx.Check(players == emitters.Count, $"{name}'s emitter roll-call matches the live players ({players})");
        ctx.Note($"{name}: {emitters.Count} emitter(s) at ({at.X:0},{at.Y:0},{at.Z:0}), {string.Join(", ", emitters.Select(e => $"{e.Name} audible {e.RangeMax:0} m"))}, loop cull {audio.LoopCull:0} m");
        // The dry cue is named by weapons.json's own NO_AMMO_WARNING read, not by a literal of the
        // audio path's: an install that renamed it must still reach this emitter.
        ctx.Check(emitters.Any(e => e.Name == weapons.EmptyClipSound),
            $"…including the dry-trigger cue this catalogue's NO_AMMO_WARNING names, {weapons.EmptyClipSound}");
        ctx.Check(emitters.Any(e => weapons.All.Any(w => w.LoopedSoundName == e.Name)),
            $"…and a loop this catalogue actually binds to a caliber");
    }

    private sealed class FixedFlightStarts : IFlightStarts
    {
        public IReadOnlyList<FlightStart> ChooseStarts(IReadOnlyList<SpawnPoint>? spawns,
            string missionZrdrPath, int spawnBase, int playerCount)
        {
            var starts = new FlightStart[playerCount];
            for (int i = 0; i < playerCount; i++)
            {
                var position = new Vector3(i * 60f, 500f, 0f);
                starts[i] = new FlightStart(position, position + Vector3.Forward, 0.5f, 100f);
            }
            return starts;
        }
    }
}
