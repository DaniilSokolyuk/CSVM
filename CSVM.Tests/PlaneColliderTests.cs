using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using CSVM.Flight.Airframe;
using Godot;
using Xunit;

namespace CSVM.Tests;

/// <summary>
/// The airframe collider's census line, the text the load log carries for each hull. Engine-free:
/// the line is built from a <see cref="ConvexHull"/>, so no physics shape and no scene tree are
/// needed to pin it.
/// </summary>
public class PlaneColliderTests
{
    // The line is built before the log call interpolates it, so the line's own culture is what
    // reaches the file sink, not the log's invariant rendering.
    [Fact]
    public void TheCensusLineReadsDotDecimalsOnACommaDecimalMachine()
    {
        var was = Thread.CurrentThread.CurrentCulture;
        try
        {
            // The locale of the machine this project is developed on: it renders 3,8.
            Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
            var hull = ConvexHull.Of(Box(3.8f, 3.0f, 5.6f), 0f);

            Assert.Equal("fuselage 3.8×3.0×5.6 m/8v", PlaneCollider.PartLine("fuselage", hull));

            // The able-to-fail control: the same numbers without the invariant rendering carry the
            // comma decimals this locale asks for, so the assertion above tests the rendering.
            Assert.Equal("3,8×3,0×5,6", $"{hull.Bounds.Size.X:0.0}×{hull.Bounds.Size.Y:0.0}×{hull.Bounds.Size.Z:0.0}");
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = was;
        }
    }

    [Fact]
    public void ThePartLineNamesItsPartItsMetreExtentsAndItsVertexCount()
    {
        var line = PlaneCollider.PartLine("wing", ConvexHull.Of(Box(9.0f, 0.4f, 2.0f), 0f));

        Assert.Equal("wing 9.0×0.4×2.0 m/8v", line);
    }

    // The eight corners of an axis-aligned box of the given metre extents, centred on the origin,
    // which is the shape a hull's bounds report back.
    private static List<Vector3> Box(float x, float y, float z)
    {
        var corners = new List<Vector3>();
        foreach (float cx in new[] { -x / 2f, x / 2f })
        {
            foreach (float cy in new[] { -y / 2f, y / 2f })
            {
                foreach (float cz in new[] { -z / 2f, z / 2f })
                {
                    corners.Add(new Vector3(cx, cy, cz));
                }
            }
        }

        return corners;
    }
}
