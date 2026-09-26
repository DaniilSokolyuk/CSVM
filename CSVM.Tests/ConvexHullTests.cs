using System.Collections.Generic;
using CSVM.Flight.Airframe;
using Godot;
using Xunit;

namespace CSVM.Tests;

/// <summary><see cref="ConvexHull"/> without an engine: the hull of a cloud, the thickness
/// padding a flat part needs, and the distance query the fuse and blast passes ask.</summary>
public class ConvexHullTests
{
    [Fact]
    public void InteriorPointsDoNotBecomeVertices()
    {
        var cloud = Cube(1f);
        cloud.Add(Vector3.Zero);
        cloud.Add(new Vector3(0.5f, 0.2f, -0.3f));

        var hull = ConvexHull.Of(cloud, 0f);

        Assert.Equal(8, hull.Points.Length);
        Assert.Equal(12, hull.Faces.Length / 3);
        Assert.Equal(18, hull.Edges.Length);
        Assert.Equal(8f, hull.Volume, 3);
        Assert.Equal(new Vector3(2f, 2f, 2f), hull.Bounds.Size);
    }

    [Fact]
    public void AFlatCloudIsPaddedToTheThicknessFloor()
    {
        var slab = new List<Vector3>
        {
            new(-3f, 0f, -1f), new(3f, 0f, -1f), new(-3f, 0f, 1f), new(3f, 0f, 1f), new(0f, 0f, 0f),
        };

        var hull = ConvexHull.Of(slab, 0.3f);

        Assert.Equal(0.3f, hull.Bounds.Size.Y, 3);
        Assert.Equal(6f, hull.Bounds.Size.X, 3);
        Assert.Equal(6f * 2f * 0.3f, hull.Volume, 2);
        Assert.True(hull.Contains(new Vector3(2.9f, 0.14f, 0.9f), 0f));
        Assert.False(hull.Contains(new Vector3(2.9f, 0.2f, 0.9f), 0f));
    }

    [Fact]
    public void ATetrahedronKeepsOnlyItsCornersAndIsNotABox()
    {
        var cloud = new List<Vector3>
        {
            new(0f, 0f, 0f), new(2f, 0f, 0f), new(0f, 2f, 0f), new(0f, 0f, 2f), new(0.3f, 0.3f, 0.3f),
        };

        var hull = ConvexHull.Of(cloud, 0f);

        Assert.Equal(4, hull.Points.Length);
        Assert.Equal(8f / 6f, hull.Volume, 3);
        // The box around it has the corner (2,2,2) region the hull cuts away.
        Assert.False(hull.Contains(new Vector3(1.5f, 1.5f, 1.5f), 0f));
        Assert.True(hull.Contains(new Vector3(0.4f, 0.4f, 0.4f), 0f));
    }

    [Fact]
    public void DistanceIsZeroInsideAndToTheNearestSkinOutside()
    {
        var hull = ConvexHull.Of(Cube(1f), 0f);

        Assert.Equal(0f, hull.Distance(new Vector3(0.5f, -0.5f, 0.9f), out var inside));
        Assert.Equal(new Vector3(0.5f, -0.5f, 0.9f), inside);

        float face = hull.Distance(new Vector3(3f, 0.2f, 0.2f), out var onFace);
        Assert.Equal(2f, face, 3);
        Assert.Equal(1f, onFace.X, 3);
        Assert.Equal(0.2f, onFace.Y, 3);

        float corner = hull.Distance(new Vector3(2f, 2f, 2f), out var onCorner);
        Assert.Equal(Mathf.Sqrt(3f), corner, 3);
        Assert.Equal(new Vector3(1f, 1f, 1f), onCorner);
    }

    [Fact]
    public void OutwardFacesEncloseTheCloud()
    {
        var cloud = new List<Vector3>();
        var rng = new System.Random(7);
        for (int i = 0; i < 400; i++)
        {
            cloud.Add(new Vector3((float)rng.NextDouble() * 4f - 2f, (float)rng.NextDouble() * 0.6f,
                (float)rng.NextDouble() * 10f - 5f));
        }

        var hull = ConvexHull.Of(cloud, 0.3f);

        foreach (var p in cloud)
            Assert.True(hull.Contains(p, 1e-3f));
        Assert.True(hull.Volume > 0f);
        Assert.True(hull.Volume <= hull.Bounds.Size.X * hull.Bounds.Size.Y * hull.Bounds.Size.Z + 1e-3f);
    }

    private static List<Vector3> Cube(float half) => new()
    {
        new(-half, -half, -half), new(half, -half, -half), new(-half, half, -half), new(half, half, -half),
        new(-half, -half, half), new(half, -half, half), new(-half, half, half), new(half, half, half),
    };
}
