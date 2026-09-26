using CSVM.Flight.Airframe;
using Godot;

namespace CSVM.Testing;

/// <summary>
/// That <see cref="GodotWorldQuery"/>'s reused query objects carry nothing from one call into the
/// next. It holds one ray and one shape parameter object for the life of the adapter rather than
/// building a finalizable wrapper per call, which makes every field of those objects a place a
/// previous caller's value can survive. The exclusion list is the one that changes an answer: a
/// stale one hides real geometry from the next caster, and a flight path that quietly stops seeing
/// the ground reads as a physics bug rather than as an allocation change.
/// </summary>
internal static class WorldQueryReuseSuites
{
    private const float BlockHalf = 20f;

    // Well clear of any chapter geometry a neighbouring suite may have left standing, so what the
    // ray finds is this suite's own block and the assertions are about exclusion, not about scenery.
    private static readonly Vector3 BlockAt = new(0f, 5000f, 0f);

    [Suite("world-query-reuse",
        "GodotWorldQuery holds one ray and one shape query object across calls, so an exclusion "
        + "left on either would silently hide the world from every later caster: a ray and an "
        + "overlap are each driven three times against one block, with no exclusion, with the "
        + "block excluded, then with no exclusion again, and the third call has to see it")]
    internal static void ReusedQueriesCarryNothingOver(TestContext ctx)
    {
        var probe = new Node3D();
        var body = new StaticBody3D { CollisionLayer = CollisionLayers.World, CollisionMask = 0 };
        body.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(BlockHalf * 2f, BlockHalf * 2f, BlockHalf * 2f) } });
        ctx.Host.AddChild(probe);
        ctx.Host.AddChild(body);
        try
        {
            body.GlobalPosition = BlockAt;
            body.ForceUpdateTransform();
            var world = new GodotWorldQuery(probe);
            var exclude = new Godot.Collections.Array<Rid> { body.GetRid() };
            var from = BlockAt + (Vector3.Up * 100f);
            var to = BlockAt - (Vector3.Up * 100f);

            bool openFirst = world.Ray(from, to, CollisionLayers.World, null, out var first);
            bool excluded = world.Ray(from, to, CollisionLayers.World, exclude, out _);
            bool openAgain = world.Ray(from, to, CollisionLayers.World, null, out var again);
            ctx.Note($"ray: open={openFirst} ({first.Collider?.Name}) excluded={excluded} open again={openAgain} ({again.Collider?.Name})");
            ctx.Check(openFirst && first.Collider == body,
                $"a ray with no exclusion finds the block, so the two calls after it have something to lose");
            ctx.Check(!excluded,
                $"the same ray with the block excluded finds nothing, so the exclusion reached the reused query object at all");
            ctx.Check(openAgain && again.Collider == body,
                $"and a ray with no exclusion finds it AGAIN, so the previous call's exclusion did not stay on the reused object");

            var parts = new[] { BoxPart() };
            var pose = new Transform3D(Basis.Identity, BlockAt);
            bool touchFirst = world.Overlaps(parts, pose, CollisionLayers.World, null);
            bool touchExcluded = world.Overlaps(parts, pose, CollisionLayers.World, exclude);
            bool touchAgain = world.Overlaps(parts, pose, CollisionLayers.World, null);
            ctx.Note($"overlap: open={touchFirst} excluded={touchExcluded} open again={touchAgain}");
            ctx.Check(touchFirst, $"an overlap test inside the block reports it, so this half has something to lose too");
            ctx.Check(!touchExcluded, $"the same test with the block excluded reports nothing");
            ctx.Check(touchAgain, $"and the test with no exclusion reports it again, so the shape object came back clean");
        }
        finally
        {
            ctx.Host.RemoveChild(body);
            ctx.Host.RemoveChild(probe);
            body.QueueFree();
            probe.QueueFree();
        }
    }

    // A one-metre box at the part's own origin: small enough to sit well inside the block, so the
    // overlap answers about the exclusion rather than about a grazing contact.
    private static PlaneCollider.Part BoxPart()
    {
        var points = new Vector3[8];
        for (int i = 0; i < 8; i++)
        {
            points[i] = new Vector3((i & 1) - 0.5f, ((i >> 1) & 1) - 0.5f, ((i >> 2) & 1) - 0.5f);
        }
        var shape = new ConvexPolygonShape3D { Points = points };
        return new PlaneCollider.Part("probe", ConvexHull.Of(points, 0.1f), Transform3D.Identity, shape);
    }
}
