using System.Collections.Generic;
using System.Linq;
using CSVM.Flight.Ai;
using CSVM.Flight.Airframe;
using CSVM.Flight.Camera;
using CSVM.Flight.Weapons;
using CSVM.Mech3;
using CSVM.Session.Campaign;
using CSVM.Session.Roster;
using Godot;

namespace CSVM.Testing;

/// <summary>D36's widened AI acquisition (<c>BL-363</c>): the decoded candidate pool covers
/// aircraft, turrets and structures, not aircraft alone, so an escort/defend AI can be ordered
/// against a zeppelin part and actually engage it. Inventory: docs/architecture.md.</summary>
internal static class TargetingCandidateSuites
{
    // A stock-armed AI-piloted rig HELD dead-on at a registered destructible (a zeppelin engine's
    // own shape: DestructibleRegistry.Instance, the TargetStruct pool) with no aircraft in the
    // scan at all. Proves SelectRankedTarget makes a non-aircraft winner the gunner's one
    // standing target and that the gunner then fires real rounds at it; and that a same-team
    // structure is refused.
    [Suite("targeting-candidates",
        "the widened AI acquisition (BL-363): a registered structure whose pool authors no " +
        "team is nobody's target and a same-team one is refused, while " +
        "a real team's AI takes a winning structure candidate as its one standing target " +
        "and the gunner fires real rounds at a zeppelin structure with no aircraft in the scan " +
        "at all")]
    internal static void TargetingCandidates(TestContext ctx)
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
        FlightController? ai = null;
        Node3D? gasbagNode = null;
        try
        {
            var live = new ProjectilePool(textures, null, null);
            pool = live;
            ctx.Host.AddChild(live);

            // The registered structure: a zeppelin engine's own shape, parked so the intercept sees
            // a static target, and not a gasbag, which a gun-only pilot is never offered. Its team
            // is left unauthored here and authored below, which is the whole fall-through arm.
            var structurePos = new Vector3(0f, 500f, 0f);
            gasbagNode = new Node3D { Name = "leng11", Position = structurePos };
            ctx.Host.AddChild(gasbagNode);
            var registry = new DestructibleRegistry();
            var gasbagInst = registry.Register(
                new AnimDefinition { Name = "leng11", AnimName = "zep_zone_leng11" }, gasbagNode, 200f);

            var gunner = new AiGunner(new RandomNumberGenerator { Seed = 20260824 })
            {
                DeadEyeAngleDeg = skills.DeadEyeAngleDeg(9),
                QuickDrawAngleDeg = skills.QuickDrawAngleDeg(9),
            };
            var aiPos = structurePos + new Vector3(0f, 0f, 400f); // dead ahead of the gasbag's nose
            var pilot = AiPilot.HoldingCourse(aiPos, structurePos);
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
                Destructibles = registry,
                UseKeyboard = false,
                PadDevices = System.Array.Empty<int>(),
                AllowPause = false,
            };
            ai.AddChild(aiModel);
            ai.Loadout = Loadout.Bind(stock, aiModel, weapons);
            ai.Setup(new FlightModel(stats), null, new CamParams(), aiPos, structurePos);
            ctx.Host.AddChild(ai);
            ai.Held = true;
            ai.PlaceHeld(aiPos, structurePos);

            var gun = ai.Loadout.FirableGuns.First();
            void Step(int frames)
            {
                for (int i = 0; i < frames; i++)
                {
                    ai!.SimStep(1f / 60f);
                    live.SimStep(1f / 60f);
                }
            }

            // --- unauthored structure: refused by every team, because the fall-through is neutral
            // and neutral is symmetric and total (docs/org/targeting.md "The hostility predicate").
            // This is the arm that would pass on a hostile fall-through, so it is what BL-407 moved.
            int hostileTeam = AimAssist.PlayerTeam + 1; // any real team, distinct from the player's
            ai.Team = hostileTeam;
            gunner.AutoTarget = true;
            Step(1);
            ctx.Check(gunner.Target == null,
                $"a structure whose pool authors no team is nobody's target (the neutral fall-through)");

            // --- same-team structure: refused outright (the decoded team gate, unchanged by D36).
            gasbagInst.Team = hostileTeam;
            ai.Team = hostileTeam;
            gunner.AutoTarget = true;
            Step(1);
            ctx.Check(gunner.Target == null,
                $"a structure on the shooter's own authored team is never a candidate");

            // --- the owning-zeppelin identity (BL-476): a zone answers to its anchor name AND to
            // the hull that owns it, so an authored -1.0 on the HULL excludes the gasbag. Both arms
            // run from an idle gunner, because a standing pick is sticky and would answer for it.
            gasbagInst.Team = hostileTeam + 1; // an authored team the shooter is hostile to
            gasbagInst.Owner = "piratezep";
            gunner.RatingBiases = new[] { new AiRatingBias("piratezep", -1f, null) };
            gunner.AutoTarget = true;
            Step(1);
            ctx.Check(gunner.Target == null,
                $"an authored -1.0 naming the OWNING zeppelin excludes its zone, which the zone's own name 'leng11' could never match");

            // --- widened acquisition: a real team now sees the structure with no aircraft in the
            // scan at all, BL-363's TargetStruct pool, previously unreachable. Doubling as the
            // exclusion's control: the same -1.0 naming a hull this zone does not belong to.
            gunner.RatingBiases = new[] { new AiRatingBias("beowulfzep", -1f, null) };
            gunner.AutoTarget = true;
            Step(1);
            ctx.Check(ReferenceEquals(gunner.Target, gasbagInst),
                $"the widened acquisition (BL-363) takes a zeppelin structure as the standing target, and an exclusion naming a different hull does not block it");

            // --- engagement: the gunner actually fires real rounds at it (the plan's own verify line).
            int ammoBefore = gun.Ammo;
            for (int guard = 0; ammoBefore - gun.Ammo == 0 && guard < 120; guard++)
                Step(1);
            ctx.Check(ammoBefore - gun.Ammo > 0,
                $"an AI ordered against a zeppelin structure engages it: rounds fired={ammoBefore - gun.Ammo}");
        }
        finally
        {
            pool?.Free();
            ai?.Free();
            gasbagNode?.Free();
            textures.Dispose();
        }
    }

    // A pilot with the mode machine, holding a course past a hostile zeppelin engine 1500 m off,
    // with nothing else in the scan. The sweep takes the engine as its standing target, the
    // machine promotes to pursue on it, and the pursue arm flies the aeroplane AT the part: the
    // range closes and the guns open. This is what an Instant Action wingman does on CM02 in the
    // original, and what BL-740 found ours never did.
    [Suite("ai-pursues-structure",
        "an AI pilot pursues a zeppelin part (BL-740): with a hostile engine inside the " +
        "activation radius and no aircraft in the scan, the mode machine leaves patrol for pursue " +
        "on the structure, the flight law closes the range on it and the gunner opens fire")]
    internal static void AiPursuesStructure(TestContext ctx)
    {
        ctx.RequireData(ctx.PlanesGamezPath, $"planes gamez");
        ctx.RequireData(ctx.ZrdrPath, $"zrdr archive");
        string texturesPath = SessionPaths.ChapterTextures(ctx.DataRoot, "C1");
        ctx.RequireData(texturesPath, $"C1 textures");

        var planesGamez = GameZ.Load(ctx.PlanesGamezPath);
        var weapons = WeaponDefs.Load(ctx.ZrdrPath, null);
        var stats = PlaneStats.Load(ctx.ZrdrPath, ctx.PlaneName);
        var skills = AiSkills.Load(ctx.ZrdrPath);
        var stock = StockLoadouts.Load().All.Values.First(d => d.Model == ctx.PlaneName);

        var textures = new TextureArchive(texturesPath);
        ProjectilePool? pool = null;
        FlightController? ai = null;
        Node3D? engineNode = null;
        try
        {
            var live = new ProjectilePool(textures, null, null);
            pool = live;
            ctx.Host.AddChild(live);

            int hostileTeam = AimAssist.PlayerTeam + 1;
            var enginePos = new Vector3(0f, 500f, 0f);
            engineNode = new Node3D { Name = "leng11", Position = enginePos };
            ctx.Host.AddChild(engineNode);
            var registry = new DestructibleRegistry();
            var engine = registry.Register(
                new AnimDefinition { Name = "leng11", AnimName = "zep_zone_leng11" }, engineNode, 200f);
            engine.Team = hostileTeam + 1;
            engine.Owner = "piratezep";

            // Abeam of the engine and flying past it, so closing the range is the pursue arm's
            // doing and not the entry course's.
            var aiPos = enginePos + new Vector3(1500f, 0f, 0f);
            var pilot = AiPilot.HoldingCourse(aiPos, aiPos + Vector3.Forward);
            pilot.Gunner = new AiGunner(new RandomNumberGenerator { Seed = 20260905 })
            {
                DeadEyeAngleDeg = skills.DeadEyeAngleDeg(9),
                QuickDrawAngleDeg = skills.QuickDrawAngleDeg(9),
            };
            var machine = new AiModeMachine(new System.Random(7))
            {
                ActivationRange = skills.MinAiActiveDist,
                AttackRange = stats.AiAttackRange,
                ReturnRange = stats.AiReturnRange,
                SteadyHandExponent = 0f,
                ProbeBlocked = (_, _) => null,
            };
            pilot.Machine = machine;
            var transitions = new List<string>();
            machine.ModeChanged += (from, to, _) =>
                transitions.Add($"{AiModeMachine.NameOf(from)}>{AiModeMachine.NameOf(to)}");

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
                Destructibles = registry,
                UseKeyboard = false,
                PadDevices = System.Array.Empty<int>(),
                AllowPause = false,
            };
            ai.AddChild(aiModel);
            ai.Loadout = Loadout.Bind(stock, aiModel, weapons);
            ai.Setup(new FlightModel(stats), null, new CamParams(), aiPos, aiPos + Vector3.Forward);
            ctx.Host.AddChild(ai);
            ai.Team = hostileTeam;

            var gun = ai.Loadout.FirableGuns.First();
            int ammoAtStart = gun.Ammo;
            float Dist() => ai!.WorldPosition.DistanceTo(enginePos);
            float startRange = Dist();
            float closest = startRange;
            bool fired = false;
            int pursueSteps = 0;
            for (int i = 0; i < 60 * 60; i++)
            {
                ai.SimStep(1f / 60f);
                live.SimStep(1f / 60f);
                closest = Mathf.Min(closest, Dist());
                fired |= gun.Ammo < ammoAtStart;
                if (machine.Mode == AiMode.Pursue)
                    pursueSteps++;
                if (i % 600 == 0)
                {
                    ctx.Note($"[pursue] t={i / 60}s mode={AiModeMachine.NameOf(machine.Mode)} target={TargetPool.NameOf(pilot.Gunner.Target)} range={Dist():0} m rounds={ammoAtStart - gun.Ammo}");
                }
            }

            ctx.Check(ReferenceEquals(pilot.Gunner.Target, engine) || fired,
                $"the sweep takes the engine as the standing target target={TargetPool.NameOf(pilot.Gunner.Target)}");
            ctx.Check(transitions.Contains("patrol>pursue"),
                $"the machine promotes to pursue on a structure transitions=[{string.Join(" ", transitions)}]");
            ctx.Check(pursueSteps > 60 * 10,
                $"…and stays there: pursue held {pursueSteps / 60f:0.0} s of 60");
            ctx.Check(closest < startRange * 0.5f,
                $"the pursue arm closes the range on the part: start={startRange:0} m closest={closest:0} m");
            ctx.Check(fired, $"the gunner opens fire on it rounds={ammoAtStart - gun.Ammo}");
        }
        finally
        {
            pool?.Free();
            ai?.Free();
            engineNode?.Free();
            textures.Dispose();
        }
    }

    // The gasbag admission gate: a gasbag is offered only to a pilot whose DAMAGES_ZEPPELIN
    // ordnance can launch now, and then carries the -0.5 in its favour; a gun-only pilot never
    // sees it and takes the engine beside it. Black Hat's Warhawk, eight torpedoes and a gun,
    // is the shipped case, and its rocketeer launches at the gasbag it picked.
    [Suite("gasbag-ordnance-gate",
        "the gasbag admission gate (BL-740): a gun-only AI beside a nearer hostile gasbag " +
        "takes the engine and never the gasbag, a torpedo-armed Warhawk takes the gasbag and " +
        "its rocketeer selects the torpedo pylon against it, and a Warhawk whose torpedoes are " +
        "spent is back to the engine")]
    internal static void GasbagOrdnanceGate(TestContext ctx)
    {
        ctx.RequireData(ctx.PlanesGamezPath, $"planes gamez");
        ctx.RequireData(ctx.ZrdrPath, $"zrdr archive");
        string texturesPath = SessionPaths.ChapterTextures(ctx.DataRoot, "C1");
        ctx.RequireData(texturesPath, $"C1 textures");

        var planesGamez = GameZ.Load(ctx.PlanesGamezPath);
        var weapons = WeaponDefs.Load(ctx.ZrdrPath, null);
        var skills = AiSkills.Load(ctx.ZrdrPath);
        var gunStats = PlaneStats.Load(ctx.ZrdrPath, ctx.PlaneName);
        var stock = StockLoadouts.Load().All.Values.First(d => d.Model == ctx.PlaneName);
        var warhawkStats = PlaneStats.LoadForAi(ctx.ZrdrPath, "player_warhawk", "bhatwarhawk");

        var textures = new TextureArchive(texturesPath);
        ProjectilePool? pool = null;
        FlightController? gunOnly = null;
        FlightController? warhawk = null;
        Node3D? gasbagNode = null;
        Node3D? engineNode = null;
        try
        {
            var live = new ProjectilePool(textures, null, null);
            pool = live;
            ctx.Host.AddChild(live);

            int hostileTeam = AimAssist.PlayerTeam + 1;
            int zepTeam = hostileTeam + 1;
            var registry = new DestructibleRegistry();
            // The gasbag is the NEARER part, so distance alone would hand it to anyone.
            var gasbagPos = new Vector3(0f, 500f, 0f);
            gasbagNode = new Node3D { Name = "gasbag1", Position = gasbagPos };
            ctx.Host.AddChild(gasbagNode);
            var gasbag = registry.Register(
                new AnimDefinition { Name = "gasbag1", AnimName = "zep_zone_gasbag1" }, gasbagNode, 240f);
            gasbag.Team = zepTeam;
            gasbag.Owner = "piratezep";
            gasbag.Gasbag = true;
            var enginePos = new Vector3(0f, 500f, -120f);
            engineNode = new Node3D { Name = "leng11", Position = enginePos };
            ctx.Host.AddChild(engineNode);
            var engine = registry.Register(
                new AnimDefinition { Name = "leng11", AnimName = "zep_zone_leng11" }, engineNode, 200f);
            engine.Team = zepTeam;
            engine.Owner = "piratezep";

            FlightController Rig(Node3D model, PlaneStats st, Loadout loadout, Vector3 pos, AiPilot pilot)
            {
                var rig = new FlightController
                {
                    PlaneModel = model,
                    Collider = PlaneCollider.Build(model),
                    Damage = new PlaneDamage(st.DestroyableParts),
                    PlayerIndex = FlightRoster.ShooterIdBase,
                    IsHumanPiloted = false,
                    Pilot = pilot,
                    Projectiles = live,
                    Destructibles = registry,
                    UseKeyboard = false,
                    PadDevices = System.Array.Empty<int>(),
                    AllowPause = false,
                };
                rig.AddChild(model);
                rig.Loadout = loadout;
                rig.Setup(new FlightModel(st), null, new CamParams(), pos, gasbagPos);
                ctx.Host.AddChild(rig);
                rig.Held = true;
                rig.PlaceHeld(pos, gasbagPos);
                rig.Team = hostileTeam;
                return rig;
            }

            AiGunner Gunner() => new(new RandomNumberGenerator { Seed = 20260905 })
            {
                DeadEyeAngleDeg = skills.DeadEyeAngleDeg(9),
                QuickDrawAngleDeg = skills.QuickDrawAngleDeg(9),
            };

            // --- the gun-only pilot: dead ahead of the gasbag, 400 m out, engine 120 m past it.
            var gunPos = gasbagPos + new Vector3(0f, 0f, 400f);
            var gunPilot = AiPilot.HoldingCourse(gunPos, gasbagPos);
            gunPilot.Gunner = Gunner();
            var gunModel = new PlaneBuilder(planesGamez, textures).Build(ctx.PlaneName);
            gunOnly = Rig(gunModel, gunStats, Loadout.Bind(stock, gunModel, weapons), gunPos, gunPilot);
            bool gunOnlyHasTorpedo = gunOnly.Loadout!.Hardpoints.Any(h => h.Weapon.DamagesZeppelin);
            ctx.Check(!gunOnlyHasTorpedo,
                $"the control fit carries no DAMAGES_ZEPPELIN ordnance (plane={ctx.PlaneName})");
            gunOnly.SimStep(1f / 60f);
            ctx.Check(ReferenceEquals(gunPilot.Gunner.Target, engine),
                $"a pilot without gasbag ordnance is never offered the nearer gasbag and takes the engine target={TargetPool.NameOf(gunPilot.Gunner.Target)}");

            // --- the Warhawk: the same station, torpedoes live.
            var whPos = gasbagPos + new Vector3(60f, 0f, 400f);
            var whPilot = AiPilot.HoldingCourse(whPos, gasbagPos);
            whPilot.Gunner = Gunner();
            whPilot.Rocketeer = new AiRocketeer(() => 0f)
            {
                QuickDrawAngleDeg = skills.QuickDrawAngleDeg(9),
                QuickDrawChance = 1f,
            };
            var whModel = new PlaneBuilder(planesGamez, textures).Build(warhawkStats.NodeName);
            var whLoadout = Loadout.BindAi(warhawkStats.AiWeapons, "bhatwarhawk", whModel, weapons);
            warhawk = Rig(whModel, warhawkStats, whLoadout, whPos, whPilot);
            var torpedoPylon = whLoadout.Hardpoints.First(h => h.Weapon.DamagesZeppelin);
            int torpedoesBefore = torpedoPylon.Ammo;
            warhawk.SimStep(1f / 60f);
            ctx.Check(ReferenceEquals(whPilot.Gunner.Target, gasbag),
                $"a torpedo-armed pilot is offered the gasbag and the -0.5 hands it the pick target={TargetPool.NameOf(whPilot.Gunner.Target)}");
            // The selection is read the tick it happens and the launch it becomes, since a pylon
            // fired on the very first step is already behind its lockout by the next one.
            bool Selected() => whPilot.Rocketeer.SelectedPylon >= 0
                && whLoadout.Hardpoints[whPilot.Rocketeer.SelectedPylon].Weapon.DamagesZeppelin;
            bool Launched() => torpedoPylon.Ammo < torpedoesBefore;
            bool selected = Selected() || Launched();
            for (int i = 0; i < 120 && !selected; i++)
            {
                warhawk.SimStep(1f / 60f);
                live.SimStep(1f / 60f);
                selected = Selected() || Launched();
            }
            ctx.Check(selected,
                $"…and the rocketeer selects the torpedo pylon against it (selected={whPilot.Rocketeer.SelectedPylon}, torpedoes {torpedoesBefore}->{torpedoPylon.Ammo}, lockout={whPilot.Rocketeer.LockoutRemaining:0.0} s)");

            // --- torpedoes spent: the gate closes and a fresh acquisition is the engine again.
            torpedoPylon.Ammo = 0;
            whPilot.Gunner.Target = null;
            warhawk.SimStep(1f / 60f);
            ctx.Check(ReferenceEquals(whPilot.Gunner.Target, engine),
                $"with the torpedoes spent the gasbag is withdrawn and the engine is the pick target={TargetPool.NameOf(whPilot.Gunner.Target)}");
        }
        finally
        {
            pool?.Free();
            gunOnly?.Free();
            warhawk?.Free();
            gasbagNode?.Free();
            engineNode?.Free();
            textures.Dispose();
        }
    }

    // The aircraft-first preference, over the shape the complaint came from: a dead cargo
    // zeppelin's two surviving engines nearer than the one live enemy aeroplane, with the scorer a
    // wingman def, which is the family that authors both biases. Default, the aeroplane; under
    // --ai-targeting=decoded, the nearer engine, which is what the shipped arithmetic picks.
    [Suite("aircraft-first-targeting",
        "the aircraft-first preference (BL-866): an ally's aeroplane picker and a turret gunner " +
        "both rank a live enemy aeroplane ahead of a dead zeppelin's nearer surviving engines, " +
        "and under --ai-targeting=decoded both take the nearer engine instead, the ported " +
        "target_bias and struct_bias ranking it about 100 m ahead of the aeroplane")]
    internal static void AircraftFirstTargeting(TestContext ctx)
    {
        ctx.RequireData(ctx.PlanesGamezPath, $"planes gamez");
        ctx.RequireData(ctx.ZrdrPath, $"zrdr archive");
        string texturesPath = SessionPaths.ChapterTextures(ctx.DataRoot, "C1");
        ctx.RequireData(texturesPath, $"C1 textures");

        const string PlaneNode = "player_bhawk";
        const string WingmanDef = "wbloodhawk";
        var planesGamez = GameZ.Load(ctx.PlanesGamezPath);
        var weapons = WeaponDefs.Load(ctx.ZrdrPath, null);
        var turretDefs = TurretDefs.Load(ctx.ZrdrPath);
        var allyStats = PlaneStats.LoadForAi(ctx.ZrdrPath, PlaneNode, WingmanDef);
        var enemyStats = PlaneStats.LoadForAi(ctx.ZrdrPath, PlaneNode);
        var stock = StockLoadouts.Load().All.Values.First(d => d.Model == allyStats.NodeName);

        // Read off vehicle.json, never typed in: the departure only means anything against the
        // arithmetic these two numbers make.
        ctx.Check(allyStats.AiTargetBias < 0f && allyStats.AiStructBias < 0f,
            $"the wingman def authors both biases: target_bias={allyStats.AiTargetBias:0} struct_bias={allyStats.AiStructBias:0}");

        var textures = new TextureArchive(texturesPath);
        ProjectilePool? pool = null;
        FlightController? ally = null;
        FlightController? enemy = null;
        Node3D? engineNode1 = null;
        Node3D? engineNode2 = null;
        Node3D? hullNode = null;
        Node3D? siteNode = null;
        bool preferenceWas = AiTargetRanking.AircraftFirst;
        try
        {
            var live = new ProjectilePool(textures, null, null);
            pool = live;
            ctx.Host.AddChild(live);

            int allyTeam = AimAssist.PlayerTeam + 1;
            int enemyTeam = AimAssist.PlayerTeam;
            int zepTeam = allyTeam + 1;
            var registry = new DestructibleRegistry();
            live.Structures = registry;

            // The airship itself, destroyed: its pool is no candidate any more, and nothing gates
            // its parts on it, which is the decoded reading this suite stands on.
            hullNode = new Node3D { Name = "cargozep1", Position = new Vector3(0f, 500f, -60f) };
            ctx.Host.AddChild(hullNode);
            var hull = registry.Register(
                new AnimDefinition { Name = "cargozep1", AnimName = "zep_zone_cargozep1" }, hullNode, 400f);
            hull.Team = zepTeam;
            hull.Status = DestructibleRegistry.State.Destroyed;

            DestructibleRegistry.Instance Engine(string name, Vector3 at, out Node3D node)
            {
                node = new Node3D { Name = name, Position = at };
                ctx.Host.AddChild(node);
                var inst = registry.Register(
                    new AnimDefinition { Name = name, AnimName = $"zep_zone_{name}" }, node, 200f);
                inst.Team = zepTeam;
                inst.Owner = "cargozep1";
                return inst;
            }

            var near = Engine("leng12", new Vector3(0f, 500f, 0f), out engineNode1);
            Engine("leng22", new Vector3(0f, 500f, -120f), out engineNode2);

            FlightController Rig(PlaneStats st, Vector3 pos, Vector3 lookAt, int team, int shooterId,
                AiPilot? pilot)
            {
                var model = new PlaneBuilder(planesGamez, textures).Build(st.NodeName);
                var rig = new FlightController
                {
                    PlaneModel = model,
                    Collider = PlaneCollider.Build(model),
                    Damage = new PlaneDamage(st.DestroyableParts),
                    PlayerIndex = shooterId,
                    IsHumanPiloted = false,
                    Pilot = pilot,
                    Projectiles = live,
                    Destructibles = registry,
                    UseKeyboard = false,
                    PadDevices = System.Array.Empty<int>(),
                    AllowPause = false,
                };
                rig.AddChild(model);
                rig.Loadout = Loadout.Bind(stock, model, weapons);
                rig.Setup(new FlightModel(st), null, new CamParams(), pos, lookAt);
                ctx.Host.AddChild(rig);
                rig.Held = true;
                rig.PlaceHeld(pos, lookAt);
                rig.Team = team;
                return rig;
            }

            // The one live enemy aeroplane, 900 m out, PAST both engines, so distance alone can
            // never hand it the pick.
            var enemyPos = new Vector3(0f, 500f, -400f);
            enemy = Rig(enemyStats, enemyPos, enemyPos + Vector3.Forward, enemyTeam,
                FlightRoster.ShooterIdBase + 1, null);

            var allyPos = new Vector3(0f, 500f, 500f);
            var allyPilot = AiPilot.HoldingCourse(allyPos, new Vector3(0f, 500f, 0f));
            allyPilot.Gunner = new AiGunner(new RandomNumberGenerator { Seed = 20260913 });
            ally = Rig(allyStats, allyPos, new Vector3(0f, 500f, 0f), allyTeam,
                FlightRoster.ShooterIdBase, allyPilot);
            ctx.Check(ally.WorldPosition.DistanceTo(near.Anchor.GlobalPosition)
                < ally.WorldPosition.DistanceTo(enemyPos),
                $"the nearer engine is {ally.WorldPosition.DistanceTo(near.Anchor.GlobalPosition):0} m out and the enemy aeroplane {ally.WorldPosition.DistanceTo(enemyPos):0} m");

            object? Acquire(bool aircraftFirst)
            {
                AiTargetRanking.AircraftFirst = aircraftFirst;
                allyPilot.Gunner.Target = null;
                allyPilot.Gunner.AutoTarget = true;
                ally!.SimStep(1f / 60f);
                return allyPilot.Gunner.Target;
            }

            var picked = Acquire(true);
            ctx.Check(ReferenceEquals(picked, enemy),
                $"the aeroplane picker takes the enemy aeroplane over the nearer engines target={TargetPool.NameOf(picked)}");
            var decoded = Acquire(false);
            ctx.Check(ReferenceEquals(decoded, near),
                $"and under the decoded order the nearer engine target={TargetPool.NameOf(decoded)}");

            // What the ported struct_bias is worth, measured rather than asserted from the def:
            // with the enemy aeroplane moved 50 m INSIDE the engine, the decoded order still takes
            // the engine, which only the -200 in its favour can do.
            var closerPos = new Vector3(0f, 500f, 50f);
            enemy.PlaceHeld(closerPos, closerPos + Vector3.Forward);
            float engineRange = ally.WorldPosition.DistanceTo(near.Anchor.GlobalPosition);
            float enemyRange = ally.WorldPosition.DistanceTo(closerPos);
            var biased = Acquire(false);
            ctx.Check(ReferenceEquals(biased, near),
                $"struct_bias is spent in the live picker: the engine at {engineRange:0} m beats an enemy aeroplane at {enemyRange:0} m target={TargetPool.NameOf(biased)}");
            ctx.Check(ReferenceEquals(Acquire(true), enemy),
                $"and the preference still takes the aeroplane there");
            enemy.PlaceHeld(enemyPos, enemyPos + Vector3.Forward);

            // A hand-placed emplacement on the ally's side, standing between the engines and the
            // enemy, so the same two candidates are both inside its field with the engines nearer.
            siteNode = new Node3D { Name = "test_ally_gun", Position = new Vector3(0f, 500f, 300f) };
            ctx.Host.AddChild(siteNode);
            var pitchNode = new Node3D();
            pitchNode.SetMeta(AnimRuntime.NameMeta, "pitch");
            siteNode.AddChild(pitchNode);
            var muzzleNode = new Node3D();
            muzzleNode.SetMeta(AnimRuntime.NameMeta, "muzzle");
            siteNode.AddChild(muzzleNode);
            var gunDef = turretDefs.All.First(d => !d.Carried);
            gunDef.YawNode = null;
            gunDef.PitchNode = "pitch";
            gunDef.Firepoints = new[] { "muzzle" };
            gunDef.HealthyNode = null;
            gunDef.Team = allyTeam;
            // The suite fixes the geometry rather than the shipped field, so neither candidate can
            // fall outside DETECTION_RANGE on a data change.
            gunDef.DetectionRange = 1200f;
            gunDef.NodePatterns = new List<IReadOnlyList<string>> { new List<string> { "test_ally_gun" } };
            var guns = TurretController.BuildEmplacements(turretDefs, weapons,
                (name, _) => name == "test_ally_gun" ? new[] { siteNode } : System.Array.Empty<Node3D>(),
                live);
            ctx.Check(guns.Length == 1, $"the hand-placed ally gun resolves turrets={guns.Length}");
            if (guns.Length != 1)
                return;
            var gun = guns[0];
            gun.SetActivated(true);

            object? GunPick(bool aircraftFirst)
            {
                AiTargetRanking.AircraftFirst = aircraftFirst;
                gun.SimStep(1f / 30f);
                return gun.TargetSource;
            }

            ctx.Check(ReferenceEquals(GunPick(true), enemy),
                $"the turret gunner agrees with the wingmen beside it target={TargetPool.NameOf(gun.TargetSource)}");
            ctx.Check(ReferenceEquals(GunPick(false), near),
                $"and under the decoded order it takes the nearest candidate of any class target={TargetPool.NameOf(gun.TargetSource)}");
        }
        finally
        {
            AiTargetRanking.AircraftFirst = preferenceWas;
            pool?.Free();
            ally?.Free();
            enemy?.Free();
            engineNode1?.Free();
            engineNode2?.Free();
            hullNode?.Free();
            siteNode?.Free();
            textures.Dispose();
        }
    }

    // The decoded 20 s hold and CSVM's withdrawal on top of it, on one rig: an allied wingman over
    // a hostile camp with the enemy aeroplane first out of reach and then inside it. The camp keeps
    // scoring valid throughout, so only the sweep at the hold's end, or the preference, can move
    // the pick off it.
    [Suite("ai-target-rescore",
        "the AI's standing target (BL-910): an unowned destructible never reaches the pilot's " +
        "candidate scan, a picked target carries the original's 20 s hold, the decoded order keeps " +
        "a still-valid camp through an enemy aeroplane arriving and sweeps the pool only once the " +
        "hold runs out, while the aircraft-first preference takes the aeroplane the moment it is " +
        "in reach; and the reach is the ATTACK radius, so a widened activation radius admits " +
        "nothing further (BL-789)")]
    internal static void AiTargetRescore(TestContext ctx)
    {
        ctx.RequireData(ctx.PlanesGamezPath, $"planes gamez");
        ctx.RequireData(ctx.ZrdrPath, $"zrdr archive");
        string texturesPath = SessionPaths.ChapterTextures(ctx.DataRoot, "C1");
        ctx.RequireData(texturesPath, $"C1 textures");

        const string PlaneNode = "player_bhawk";
        const string WingmanDef = "wbloodhawk";
        var planesGamez = GameZ.Load(ctx.PlanesGamezPath);
        var weapons = WeaponDefs.Load(ctx.ZrdrPath, null);
        var allyStats = PlaneStats.LoadForAi(ctx.ZrdrPath, PlaneNode, WingmanDef);
        var enemyStats = PlaneStats.LoadForAi(ctx.ZrdrPath, PlaneNode);
        var stock = StockLoadouts.Load().All.Values.First(d => d.Model == allyStats.NodeName);

        var textures = new TextureArchive(texturesPath);
        ProjectilePool? pool = null;
        FlightController? ally = null;
        FlightController? enemy = null;
        Node3D? campNode = null;
        Node3D? sceneryNode = null;
        bool preferenceWas = AiTargetRanking.AircraftFirst;
        try
        {
            var live = new ProjectilePool(textures, null, null);
            pool = live;
            ctx.Host.AddChild(live);

            int allyTeam = AimAssist.PlayerTeam;
            int enemyTeam = allyTeam + 1;
            var registry = new DestructibleRegistry();
            live.Structures = registry;

            // The camp: a hostile mission structure 500 m ahead of the wingman.
            campNode = new Node3D { Name = "u_camp1", Position = new Vector3(0f, 500f, 0f) };
            ctx.Host.AddChild(campNode);
            var camp = registry.Register(
                new AnimDefinition { Name = "u_camp1", AnimName = "u_camp1" }, campNode, 400f);
            camp.Team = enemyTeam;

            // Scenery beside it that no mission structure stands on, so the data owns it on no
            // side. The original never builds a candidate for it at all.
            sceneryNode = new Node3D { Name = "u_camp5", Position = new Vector3(60f, 500f, 0f) };
            ctx.Host.AddChild(sceneryNode);
            registry.Register(
                new AnimDefinition { Name = "u_camp5", AnimName = "u_camp5" }, sceneryNode, 400f);

            FlightController Rig(PlaneStats st, Vector3 pos, int team, int shooterId, AiPilot? pilot)
            {
                var model = new PlaneBuilder(planesGamez, textures).Build(st.NodeName);
                var rig = new FlightController
                {
                    PlaneModel = model,
                    Collider = PlaneCollider.Build(model),
                    Damage = new PlaneDamage(st.DestroyableParts),
                    PlayerIndex = shooterId,
                    IsHumanPiloted = false,
                    Pilot = pilot,
                    Projectiles = live,
                    Destructibles = registry,
                    UseKeyboard = false,
                    PadDevices = System.Array.Empty<int>(),
                    AllowPause = false,
                };
                rig.AddChild(model);
                rig.Loadout = Loadout.Bind(stock, model, weapons);
                rig.Setup(new FlightModel(st), null, new CamParams(), pos, new Vector3(0f, 500f, 0f));
                ctx.Host.AddChild(rig);
                rig.Held = true;
                rig.PlaceHeld(pos, new Vector3(0f, 500f, 0f));
                rig.Team = team;
                return rig;
            }

            // 2,500 m out, past the 2,000 m activation radius: nothing about the aeroplane ranks.
            var farPos = new Vector3(0f, 500f, -2000f);
            enemy = Rig(enemyStats, farPos, enemyTeam, FlightRoster.ShooterIdBase + 1, null);

            var allyPos = new Vector3(0f, 500f, 500f);
            var allyPilot = AiPilot.HoldingCourse(allyPos, new Vector3(0f, 500f, 0f));
            allyPilot.Gunner = new AiGunner(new RandomNumberGenerator { Seed = 20260913 });
            ally = Rig(allyStats, allyPos, allyTeam, FlightRoster.ShooterIdBase, allyPilot);

            ally.RankedPoolSourcesForTest(allyPilot.Gunner);
            ctx.Same(1, ally.ScannedStructureCountForTest(),
                $"only the owned camp reaches the pilot's structure scan of {registry.Count} registered pool(s)");

            object? Acquire(bool aircraftFirst)
            {
                AiTargetRanking.AircraftFirst = aircraftFirst;
                allyPilot.Gunner.Target = null;
                allyPilot.Gunner.TargetRankFor = null;
                allyPilot.Gunner.AutoTarget = true;
                ally!.SimStep(1f / 60f);
                return allyPilot.Gunner.Target;
            }

            object? Step()
            {
                ally!.SimStep(1f / 60f);
                return allyPilot.Gunner.Target;
            }

            // The decoded order first, so the hold is read with nothing else acting on the pick.
            ctx.Check(ReferenceEquals(Acquire(false), camp),
                $"with no aeroplane in reach the pilot takes the camp target={TargetPool.NameOf(allyPilot.Gunner.Target)}");
            ctx.Check(ReferenceEquals(allyPilot.Gunner.TargetRankFor, camp)
                && allyPilot.Gunner.TargetHoldUntil >= AiGunner.TargetHoldSeconds,
                $"and the take stamps the engine's hold until {allyPilot.Gunner.TargetHoldUntil:0.#} s");

            // The aeroplane arrives 100 m off the wingman's nose, nearer than the camp and near
            // enough to beat the camp's struct_bias, and the decoded hold keeps the camp anyway.
            var nearPos = new Vector3(0f, 500f, 400f);
            enemy.PlaceHeld(nearPos, nearPos + Vector3.Forward);
            ctx.Check(ReferenceEquals(Step(), camp),
                $"the decoded hold keeps the camp while an enemy aeroplane closes to {ally.WorldPosition.DistanceTo(nearPos):0} m target={TargetPool.NameOf(allyPilot.Gunner.Target)}");

            allyPilot.Gunner.TargetHoldUntil = 0.0; // the hold runs out: the pool is swept whole
            ctx.Check(ReferenceEquals(Step(), enemy),
                $"and once it runs out the sweep takes the nearer aeroplane target={TargetPool.NameOf(allyPilot.Gunner.Target)}");
            ctx.Check(allyPilot.Gunner.TargetHoldUntil >= AiGunner.TargetHoldSeconds,
                $"with the hold re-stamped on the new take until {allyPilot.Gunner.TargetHoldUntil:0.#} s");

            // The same camp pick under the preference, with the aeroplane FARTHER than the camp, so
            // only the withdrawal can move it, and mid-hold, so only the layer can.
            enemy.PlaceHeld(farPos, farPos + Vector3.Forward);
            ctx.Check(ReferenceEquals(Acquire(true), camp),
                $"under the preference the camp is still the pick with no aeroplane in reach target={TargetPool.NameOf(allyPilot.Gunner.Target)}");
            var reachPos = new Vector3(0f, 500f, -700f);
            enemy.PlaceHeld(reachPos, reachPos + Vector3.Forward);
            ctx.Check(ReferenceEquals(Step(), enemy),
                $"and the wingman leaves it mid-hold for an aeroplane {ally.WorldPosition.DistanceTo(reachPos):0} m out, past the camp at {ally.WorldPosition.DistanceTo(campNode.GlobalPosition):0} m");

            // The admission volume is the ATTACK cylinder, never the activation one: a DEDG-widened
            // member is simulated out to 9,000 m and still picks up nothing past its attack radius.
            allyPilot.Machine = new AiModeMachine(new System.Random(789))
            {
                ActivationRange = CampaignRosterPlan.DedgActivationRangeM,
                AttackRange = 2000f,
            };
            var widePos = new Vector3(0f, 500f, -2500f);
            enemy.PlaceHeld(widePos, widePos + Vector3.Forward);
            float wideGap = ally.WorldPosition.DistanceTo(widePos);
            ctx.Check(ReferenceEquals(Acquire(true), camp),
                $"a widened activation radius of {allyPilot.Machine.ActivationRange:0} m does not admit an aeroplane {wideGap:0} m out, past the {allyPilot.Machine.AttackRange:0} m attack radius: the pick stays {TargetPool.NameOf(allyPilot.Gunner.Target)}");
            allyPilot.Machine.AttackRange = 4000f;
            ctx.Check(ReferenceEquals(Acquire(true), enemy),
                $"and raising the ATTACK radius to {allyPilot.Machine.AttackRange:0} m admits the same aeroplane at {wideGap:0} m target={TargetPool.NameOf(allyPilot.Gunner.Target)}");
        }
        finally
        {
            AiTargetRanking.AircraftFirst = preferenceWas;
            pool?.Free();
            ally?.Free();
            enemy?.Free();
            campNode?.Free();
            sceneryNode?.Free();
            textures.Dispose();
        }
    }

    // AddRankedNonAircraft walked every turret with no discriminator on TurretController.Site, so
    // a carried gunner rode the ranked pool as a second entry beside its own aircraft's Vehicle
    // entry, one silhouette read as two candidates. Mirrors the guard TargetPool.Offer already
    // applies for the player (TargetPool.IsEmplacement), now shared by both pools.
    [Suite("ranked-pool-carried-turret-dedup",
        "the AI ranked pool's carried-turret guard (BL-507): a hostile aircraft with a crewed " +
        "rear mount rides the pool as one Vehicle entry, never a second entry for its own " +
        "turret, while a world emplacement in the same scene still reaches the pool")]
    internal static void RankedPoolCarriedTurretDedup(TestContext ctx)
    {
        ctx.RequireData(ctx.PlanesGamezPath, $"planes gamez");
        ctx.RequireData(ctx.ZrdrPath, $"zrdr archive");
        string texturesPath = SessionPaths.ChapterTextures(ctx.DataRoot, "C1");
        ctx.RequireData(texturesPath, $"C1 textures");

        var planesGamez = GameZ.Load(ctx.PlanesGamezPath);
        var weapons = WeaponDefs.Load(ctx.ZrdrPath, null);
        var turretDefs = TurretDefs.Load(ctx.ZrdrPath);

        // The Kestrel: one thirdp rear mount (see the carried-turrets suite for the decoded arc).
        const string HostPlane = "player_kestrel";
        var hostStats = PlaneStats.Load(ctx.ZrdrPath, HostPlane);
        var aiStats = PlaneStats.Load(ctx.ZrdrPath, ctx.PlaneName);

        var textures = new TextureArchive(texturesPath);
        ProjectilePool? pool = null;
        FlightController? carrier = null;
        FlightController? ai = null;
        Node3D? siteNode = null;
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
                };
                rig.AddChild(model);
                rig.Setup(new FlightModel(st), ctx.Camera, new CamParams(), pos, pos + Vector3.Forward);
                ctx.Host.AddChild(rig);
                rig.PlaceHeld(pos, pos + Vector3.Forward);
                return rig;
            }

            var carrierPos = new Vector3(0f, 500f, 0f);
            carrier = BuildRig(HostPlane, hostStats, 0, carrierPos);
            carrier.IsHumanPiloted = false;
            carrier.Team = TurretDef.DefaultTeamId;
            carrier.Turrets = TurretController.BuildCarried(
                turretDefs, hostStats, carrier.PlaneModel!, weapons, carrier, live);
            ctx.Check(carrier.Turrets.Length == 1,
                $"the carrier's crewed mount resolves turrets={carrier.Turrets.Length}");
            if (carrier.Turrets.Length != 1)
                return;
            var carriedTurret = carrier.Turrets[0];

            // A hand-placed emplacement: an authored standalone entry, its NODES/PARTS chain
            // repointed at nodes built here rather than a real chapter's placement, since this
            // suite proves the discriminator, not the census (world-turrets' own job).
            siteNode = new Node3D { Name = "test_emplacement", Position = new Vector3(400f, 500f, 0f) };
            ctx.Host.AddChild(siteNode);
            var pitchNode = new Node3D();
            pitchNode.SetMeta(AnimRuntime.NameMeta, "pitch");
            siteNode.AddChild(pitchNode);
            var muzzleNode = new Node3D();
            muzzleNode.SetMeta(AnimRuntime.NameMeta, "muzzle");
            siteNode.AddChild(muzzleNode);
            var emplacementDef = turretDefs.All.First(d => !d.Carried);
            emplacementDef.YawNode = null;
            emplacementDef.PitchNode = "pitch";
            emplacementDef.Firepoints = new[] { "muzzle" };
            emplacementDef.WeaponName = carriedTurret.Def.WeaponName;
            emplacementDef.HealthyNode = null;
            emplacementDef.Team = null; // TurretDefs.DefaultTeamId, the same enemy default the carrier took
            emplacementDef.NodePatterns = new List<IReadOnlyList<string>>
            {
                new List<string> { "test_emplacement" },
            };
            var emplacements = TurretController.BuildEmplacements(turretDefs, weapons,
                (name, _) => name == "test_emplacement"
                    ? new[] { siteNode }
                    : System.Array.Empty<Node3D>(),
                live);
            ctx.Check(emplacements.Length == 1 && emplacements[0].Site != null,
                $"the hand-placed emplacement resolves turrets={emplacements.Length}");
            if (emplacements.Length != 1)
                return;
            live.RegisterWorldTurrets(emplacements);
            var emplacementTurret = emplacements[0];

            // The assessing AI: hostile to both, never itself a candidate.
            var aiPos = carrierPos + new Vector3(0f, 0f, 400f);
            ai = BuildRig(ctx.PlaneName, aiStats, FlightRoster.ShooterIdBase, aiPos);
            ai.IsHumanPiloted = false;
            ai.Team = AimAssist.PlayerTeam;
            var gunner = new AiGunner(new RandomNumberGenerator { Seed = 20260826 });

            var sources = ai.RankedPoolSourcesForTest(gunner);
            int carrierEntries = sources.Count(s => ReferenceEquals(s, carrier));
            int carriedTurretEntries = sources.Count(s => ReferenceEquals(s, carriedTurret));
            int emplacementEntries = sources.Count(s => ReferenceEquals(s, emplacementTurret));
            ctx.Check(carrierEntries == 1,
                $"the carrier's own aircraft rides the ranked pool as a Vehicle entry entries={carrierEntries}");
            ctx.Check(carriedTurretEntries == 0,
                $"a carried turret is not offered as a second entry beside its own aircraft entries={carriedTurretEntries}");
            ctx.Check(emplacementEntries == 1,
                $"a world emplacement still reaches the ranked pool entries={emplacementEntries}");
        }
        finally
        {
            pool?.Free();
            carrier?.Free();
            ai?.Free();
            siteNode?.Free();
            textures.Dispose();
        }
    }
}
