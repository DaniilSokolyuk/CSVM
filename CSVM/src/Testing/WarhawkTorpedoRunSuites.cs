using System.Collections.Generic;
using System.Linq;
using System.Text;
using CSVM.Flight.Ai;
using CSVM.Flight.Airframe;
using CSVM.Flight.Camera;
using CSVM.Flight.Weapons;
using CSVM.Mech3;
using CSVM.Session.Roster;
using Godot;

namespace CSVM.Testing;

/// <summary>BL-740: the whole torpedo run end to end, on a free-flying aeroplane rather than a
/// held rig, the acquisition's gasbag gate, the ranking's -0.5, the pursue arm closing the range,
/// the rocketeer's DAMAGES_ZEPPELIN match and its engagement band. C4/M03's own geometry: a Black
/// Hat Warhawk 1800 m out and 75 m above the Pandora's nearest gasbag, flying past rather than in.
/// The held-rig gate suite (<c>gasbag-ordnance-gate</c>) proves the admission rule; this proves an
/// aeroplane actually gets a torpedo away.</summary>
internal static class WarhawkTorpedoRunSuites
{
    private const float StepDt = 1f / 60f;
    private const int Seconds = 90;

    [Suite("warhawk-torpedo-run",
        "a free-flying Black Hat Warhawk torpedoes a gasbag (BL-740): from C4/M03's own opening "
        + "geometry it takes the gasbag over the engine beside it, holds pursue while the flight "
        + "law closes the range, and once inside its torpedo slot's authored 350-800 m band its "
        + "rocketeer takes the torpedo pylon and gets at least one away")]
    internal static void WarhawkTorpedoRun(TestContext ctx)
    {
        ctx.RequireData(ctx.PlanesGamezPath, $"planes gamez");
        ctx.RequireData(ctx.ZrdrPath, $"zrdr archive");
        string texturesPath = SessionPaths.ChapterTextures(ctx.DataRoot, "C1");
        ctx.RequireData(texturesPath, $"C1 textures");

        var planesGamez = GameZ.Load(ctx.PlanesGamezPath);
        var weapons = WeaponDefs.Load(ctx.ZrdrPath, null);
        var skills = AiSkills.Load(ctx.ZrdrPath);
        var stats = PlaneStats.LoadForAi(ctx.ZrdrPath, "player_warhawk", "bhatwarhawk");

        var textures = new TextureArchive(texturesPath);
        ProjectilePool? pool = null;
        FlightController? ai = null;
        Node3D? gasbagNode = null;
        Node3D? engineNode = null;
        var report = new StringBuilder();
        try
        {
            var live = new ProjectilePool(textures, null, null);
            pool = live;
            ctx.Host.AddChild(live);

            // The airship's own side, which the state-child rule gives its zones, against the
            // Warhawk on the other: the mission's Pandora is the player's and Black Hat is not.
            var gasbagPos = new Vector3(0f, 625f, 0f);
            gasbagNode = new Node3D { Name = "gasbag4", Position = gasbagPos };
            engineNode = new Node3D { Name = "leng12", Position = gasbagPos + new Vector3(0f, -40f, 60f) };
            ctx.Host.AddChild(gasbagNode);
            ctx.Host.AddChild(engineNode);
            var registry = new DestructibleRegistry();
            var gasbag = registry.Register(
                new AnimDefinition { Name = "gasbag4", AnimName = "zep_zone_gasbag4" }, gasbagNode, 120f);
            gasbag.Team = AimAssist.PlayerTeam;
            gasbag.Owner = "piratezep";
            gasbag.Gasbag = true;
            var engine = registry.Register(
                new AnimDefinition { Name = "leng12", AnimName = "zep_zone_leng12" }, engineNode, 200f);
            engine.Team = AimAssist.PlayerTeam;
            engine.Owner = "piratezep";

            var aiPos = gasbagPos + new Vector3(1790f, 75f, 0f);
            var pilot = AiPilot.HoldingCourse(aiPos, aiPos + Vector3.Forward);
            pilot.Gunner = new AiGunner(new RandomNumberGenerator { Seed = 20260906 })
            {
                DeadEyeAngleDeg = skills.DeadEyeAngleDeg(6),
                QuickDrawAngleDeg = skills.QuickDrawAngleDeg(6),
            };
            pilot.Rocketeer = new AiRocketeer(new RandomNumberGenerator { Seed = 4242 }.Randf)
            {
                QuickDrawAngleDeg = skills.QuickDrawAngleDeg(6),
                QuickDrawChance = skills.QuickDrawChance(6),
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

            var aiModel = new PlaneBuilder(planesGamez, textures).Build(stats.NodeName);
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
            ai.Loadout = Loadout.BindAi(stats.AiWeapons, "bhatwarhawk", aiModel, weapons);
            ai.Setup(new FlightModel(stats), null, new CamParams(), aiPos, aiPos + Vector3.Forward);
            ctx.Host.AddChild(ai);
            ai.Team = AimAssist.PlayerTeam + 1;

            var torpedo = ai.Loadout.Hardpoints.First(h => h.Weapon.DamagesZeppelin);
            int torpedoesAtStart = torpedo.Ammo;
            // Its ordnance is torpedoes and nothing else: the def's other slot is a gun, so the
            // DAMAGES_ZEPPELIN match is what stands between this fit and an empty pylon walk.
            ctx.Check(torpedoesAtStart > 0 && ai.Loadout.FirableGuns.Any(),
                $"the shipped bhatwarhawk fit carries torpedoes and a gun ({torpedoesAtStart} on pylon{torpedo.Index})");

            string lastKey = string.Empty;
            int taken = 0;
            bool tookGasbag = false;
            var modes = new List<string>();
            machine.ModeChanged += (from, to, _) =>
                modes.Add($"{AiModeMachine.NameOf(from)}>{AiModeMachine.NameOf(to)}");
            for (int i = 0; i < 60 * Seconds; i++)
            {
                ai.SimStep(StepDt);
                live.SimStep(StepDt);
                tookGasbag |= ReferenceEquals(pilot.Gunner.Target, gasbag);
                var rocketeer = pilot.Rocketeer;
                if (rocketeer.LastVerdictKey.Length == 0 || rocketeer.LastVerdictKey == lastKey)
                    continue;
                lastKey = rocketeer.LastVerdictKey;
                taken += lastKey.StartsWith("taken:", System.StringComparison.Ordinal) ? 1 : 0;
                report.AppendLine($"t={i / 60f:0.0}s mode={AiModeMachine.NameOf(machine.Mode)} " +
                    $"target={TargetPool.NameOf(pilot.Gunner.Target)} " +
                    $"range={ai.WorldPosition.DistanceTo(gasbagPos):0} m: {rocketeer.LastVerdict}");
            }

            int launched = torpedoesAtStart - torpedo.Ammo;
            report.AppendLine($"torpedoes {torpedoesAtStart} -> {torpedo.Ammo}, pylon taken {taken}x");
            report.AppendLine($"modes: {string.Join(" ", modes)}");
            ctx.Check(tookGasbag,
                $"the sweep takes the gasbag over the engine beside it target={TargetPool.NameOf(pilot.Gunner.Target)}");
            ctx.Check(taken >= 2,
                $"the rocketeer reaches its 350-800 m band in pursue and takes the torpedo pylon there ({taken} time(s) in {Seconds} s)");
            ctx.Check(launched >= 1,
                $"and gets at least one torpedo away ({launched} of {torpedoesAtStart}, the rest held by the per-launch dice)");
            ctx.Note($"{launched} torpedo(es) away, pylon taken {taken}x");
        }
        finally
        {
            pool?.Free();
            ai?.Free();
            gasbagNode?.Free();
            engineNode?.Free();
            textures.Dispose();
        }
        ctx.WriteArtifact($"test-warhawk-torpedo-run.txt", report.ToString());
    }
}
