using System.Linq;
using System.Text;
using CSVM.Flight.Airframe;
using CSVM.Flight.Weapons;
using CSVM.Mech3;
using Godot;

namespace CSVM.Testing;

/// <summary>A broadside cannon behind its shut hatch. C2B/M04's Gemini stows every
/// <c>lbroadNN</c>: the deploy definition's RESET_STATE switches <c>gunback</c> and <c>gun1</c>
/// off, holds <c>upper_br_door</c> at zero rotation and parks <c>gun1x</c> 5 m inside the hull, so
/// the only solid geometry a round can meet is the hatch. The hatch belongs to no HP pool, and the
/// suite fires real gun rounds at it to assert the cannon behind it takes none of that damage,
/// then plays the authored deploy and destroys the same cannon with the same gun.</summary>
internal static class ZeppelinCannonStowedSuites
{
    private const string Hull = "geminizep";
    private const string Cannon = "lbroad11";
    private const string DeployAnim = "deploy_gmzep_lbroad11";
    private const float Dt = 1f / 60f;

    [Suite("zeppelin-cannon-stowed",
        "a stowed broadside cannon takes no weapon damage through its shut hatch (BL-640): on " +
        "C2B/M04's Gemini the deploy def's RESET_STATE holds lbroad11's gunback and gun1 " +
        "inactive and upper_br_door shut, so the hatch is the only solid geometry there; real " +
        "gun rounds into it leave the HEALTH 60 pool untouched, and once the authored deploy " +
        "swings the hatch and activates gunback the same rounds destroy the cannon")]
    internal static void ZeppelinCannonStowed(TestContext ctx)
    {
        string missionZrdr = SessionPaths.MissionZrdr(ctx.DataRoot, "C2B", "M04");
        ctx.RequireData(missionZrdr, $"C2B/M04 zrdr");
        var weapons = WeaponDefs.Load(ctx.ZrdrPath, null);
        var gun = weapons.All.FirstOrDefault(w => w.IsGun && w.HealthDamage is > 0f && w.ImpactProximity is not > 0f)
            ?? weapons.All.FirstOrDefault(w => w.IsGun && w.HealthDamage is > 0f);
        ctx.Check(gun != null, $"a gun with HEALTH_DAMAGE ships");
        if (gun == null)
            return;
        var report = new StringBuilder();

        ctx.WithWorld("C2B", collision: true, mission: "M04", world =>
        {
            var runtime = world.Runtime;
            var hull = runtime.FindNodes(Hull).FirstOrDefault();
            ctx.Check(hull != null, $"the {Hull} world node resolves in the C2B/M04 world");
            if (hull == null)
                return;
            var cannon = runtime.FindNodes(Cannon, hull).FirstOrDefault();
            ctx.Check(cannon != null, $"{Hull}'s {Cannon} resolves inside its own hull");
            if (cannon == null)
                return;
            // Let the bootstrap's anchored RESET_STATEs settle before anything is read.
            for (int i = 0; i < 60; i++)
                runtime.Advance(Dt);
            SyncColliders(cannon);

            var gunback = runtime.FindNodes("gunback", cannon).FirstOrDefault();
            var hatch = runtime.FindNodes("upper_br_door", cannon).FirstOrDefault();
            ctx.Check(gunback != null && hatch != null, $"{Cannon} carries a gunback and an upper_br_door");
            if (gunback == null || hatch == null)
                return;
            ctx.Check(!gunback.Visible,
                $"{Cannon} is stowed: gunback is inactive from the deploy def's RESET_STATE");
            ctx.Check(hatch.IsVisibleInTree() && Mathf.Abs(hatch.Rotation.Z) < 0.05f,
                $"{Cannon}'s hatch is shut and in the world (rot={hatch.Rotation.Z:0.###})");

            var pool = runtime.Destructibles.PoolsOn(cannon).FirstOrDefault();
            ctx.Check(pool != null && Mathf.IsEqualApprox(pool.MaxHealth, 60f),
                $"{Cannon} carries its compiled HEALTH 60 pool hp={pool?.MaxHealth ?? -1f}");
            if (pool == null)
                return;
            report.AppendLine($"{Hull}/{Cannon} at {cannon.GlobalPosition}, pool {pool.Def.AnimName} hp={pool.MaxHealth}");
            report.AppendLine($"stowed: gunback visible={gunback.Visible} hatch visible={hatch.Visible} rot={hatch.Rotation}");

            // Abeam on the cannon's own side of the hull for the hatch, and from below for the
            // deployed gun, whose hatch hangs 135 degrees open above it and would take the round.
            Vector3 Outward(Vector3 at)
            {
                var away = at - hull.GlobalPosition;
                away.Y = 0f;
                return away.Normalized();
            }

            var space = ctx.Host.GetWorld3D().DirectSpaceState;
            (Node? Owner, DestructibleRegistry.Instance? Pool) RayAt(Vector3 at, Vector3 approach)
            {
                var probe = space.IntersectRay(PhysicsRayQueryParameters3D.Create(
                    at + approach * 60f, at, CollisionLayers.World));
                if (probe.Count == 0 || probe["collider"].Obj is not Node body)
                    return (null, null);
                return (WorldCollision.OwnerOf(body), runtime.Destructibles.Resolve(body));
            }

            string RayLine(string what, Vector3 at, Vector3 approach)
            {
                var (owner, hitPool) = RayAt(at, approach);
                return $"ray at the {what} {at}: {owner?.Name ?? "nothing"} -> pool "
                    + (hitPool != null ? $"{hitPool.Def.AnimName} on {AnimRuntime.NameOf(hitPool.Anchor)}" : "none");
            }

            var barrel = runtime.FindNodes("turret", cannon).FirstOrDefault() ?? gunback;
            var hatchAt = UI.Overlays.OrbitCamera.MergedAabb(hatch).GetCenter();
            report.AppendLine(RayLine("shut hatch", hatchAt, Outward(hatchAt)));
            report.AppendLine(RayLine("stowed gun", UI.Overlays.OrbitCamera.MergedAabb(barrel).GetCenter(), Vector3.Down));
            ctx.Check(RayAt(hatchAt, Outward(hatchAt)).Pool == null,
                $"a ray onto the shut hatch belongs to no HP pool");

            ProjectilePool? rounds = null;
            TextureArchive? textures = null;
            try
            {
                textures = new TextureArchive(SessionPaths.ChapterTextures(ctx.DataRoot, "C2B"));
                rounds = new ProjectilePool(textures, null, null) { DamageSink = runtime.DamageAt };
                ctx.Host.AddChild(rounds);

                int Fire(Vector3 at, Vector3 approach, int shots, DestructibleRegistry.Instance watch)
                {
                    int fired = 0;
                    var muzzlePos = at + approach * 60f;
                    var muzzle = new Transform3D(
                        Basis.LookingAt((at - muzzlePos).Normalized(), Vector3.Right), muzzlePos);
                    for (int i = 0; i < shots * 4 && watch.Status != DestructibleRegistry.State.Destroyed; i++)
                    {
                        if (i % 4 == 0)
                        {
                            rounds.Spawn(gun, muzzle, Vector3.Zero);
                            fired++;
                        }
                        rounds.SimStep(Dt);
                        runtime.Advance(Dt);
                    }
                    return fired;
                }

                // Enough rounds to kill the pool several times over if any of them reached it.
                int atHatch = Fire(hatchAt, Outward(hatchAt), 40, pool);
                report.AppendLine($"{atHatch} {gun.Id} round(s) into the shut hatch: hp={pool.Health:0.##} status={pool.Status}");
                ctx.Check(Mathf.IsEqualApprox(pool.Health, pool.MaxHealth)
                    && pool.Status == DestructibleRegistry.State.Healthy,
                    $"{atHatch} rounds into the shut hatch leave {Cannon} untouched hp={pool.Health:0.##}");

                // The authored deploy: the hatch swings 135 degrees over 4 s and gunback comes on.
                runtime.Play(DeployAnim, cannon);
                for (int i = 0; i < 60 * 6; i++)
                    runtime.Advance(Dt);
                SyncColliders(cannon);
                report.AppendLine($"deployed: gunback visible={gunback.Visible} hatch rot={hatch.Rotation}");
                ctx.Check(gunback.Visible, $"the authored {DeployAnim} brings gunback into the world");

                var gunAt = UI.Overlays.OrbitCamera.MergedAabb(barrel).GetCenter();
                report.AppendLine(RayLine("deployed gun", gunAt, Vector3.Down));
                var (gunOwner, gunPool) = RayAt(gunAt, Vector3.Down);
                ctx.Check(gunOwner != null && gunPool == pool
                    && (gunOwner.GetInstanceId() == gunback.GetInstanceId() || gunback.IsAncestorOf(gunOwner)),
                    $"a ray onto the deployed gun meets the gun itself and finds {Cannon}'s pool (met {gunOwner?.Name ?? "nothing"})");
                int atGun = Fire(gunAt, Vector3.Down, 40, pool);
                report.AppendLine($"{atGun} {gun.Id} round(s) into the deployed cannon: hp={pool.Health:0.##} status={pool.Status}");
                ctx.Check(pool.Status == DestructibleRegistry.State.Destroyed,
                    $"the same gun destroys the deployed {Cannon} rounds={atGun} hp={pool.Health:0.##}");
            }
            finally
            {
                rounds?.Free();
                textures?.Dispose();
            }
        });

        ctx.WriteArtifact("test-zeppelin-cannon-stowed.txt", report.ToString());
        ctx.Note($"C2B/M04's {Hull}: {Cannon} is immune behind its shut hatch and dies once deployed");
    }

    // A suite gets no physics flush, so a body an animation moved is still queried at its old pose
    // (docs/verification.md INSTR-13). Push the whole cannon's transforms before every ray.
    private static void SyncColliders(Node3D root)
    {
        root.ForceUpdateTransform();
        foreach (var child in root.GetChildren())
        {
            if (child is Node3D node)
                SyncColliders(node);
        }
    }
}
