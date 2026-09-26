using System.Collections.Generic;
using System.Linq;
using System.Text;
using CSVM.Flight.Airframe;
using CSVM.Flight.Camera;
using CSVM.Flight.Hangar;
using CSVM.Flight.Weapons;
using CSVM.Mech3;
using CSVM.Utils;
using Godot;

namespace CSVM.Testing;

/// <summary>Suites measuring the airframe collision hulls against the mesh they were derived
/// from, on every player airframe.</summary>
internal static class AirframeColliderSuites
{
    // A mesh corner counts as covered when it is inside some hull by this much slack: the
    // hull's own quantisation and padding move a point by millimetres, not centimetres.
    private const float CoverTolerance = 0.02f;

    // The share of an airframe's triangle area allowed outside every hull. The clipped regions
    // between them share their boundary triangles, so a covered silhouette reads as zero here;
    // the allowance exists for the slivers ClipAxis drops as degenerate.
    private const float MaxUncoveredFraction = 0.005f;

    // The hit-rate census stands inside every airframe's nearest LOD band (0 to 150 m), so the
    // polygons it compares against are the ones both builds present at that range.
    private const float Standoff = 120f;

    // Rays per axis over the aim window: 121 squared is 14641 samples, which holds the standard
    // error on a presented-area share near half a percent.
    private const int RayGrid = 121;

    // The aim window's margin over the model's projected bounding box, so the silhouette edge is
    // sampled on both sides rather than clipped by the window.
    private const float WindowMargin = 1.15f;

    // Every Nth census ray is also fired as a real round. That burst is the control tying the
    // analytic hull count to what the gun path itself registers.
    private const int BurstStride = 21;

    // Rounds per launch group and sim frames stepped after each. A small group keeps one step's
    // damage far below the whole pool, and 20 frames carry a 900 m/s round past the standoff.
    private const int BurstGroup = 4;
    private const int BurstSteps = 20;

    // Bins per axis over the aim window. Each triangle is filed under the window cells its
    // projection covers, so a ray tests a few dozen candidates instead of the whole mesh.
    private const int BinSide = 48;

    // The share of the mesh silhouette a raster may find outside every hull. Only the raster's
    // own quantisation belongs here: a hull inside the silhouette drops rounds and is a defect.
    private const float MaxLostFraction = 0.005f;

    private const int TargetId = 1;
    private const int ShooterId = 0;

    private static readonly Vector3 TargetPos = new(0f, 500f, 0f);

    // Where the muzzle stands relative to the target's own centre, one unit vector per aspect.
    private static readonly (string Name, Vector3 From)[] Aspects =
    {
        ("side-on", new Vector3(1f, 0f, 0f)),
        ("head-on", new Vector3(0f, 0f, -1f)),
    };


    [Suite("airframe-collider-hit-rate",
        "the hit RATE our collision hulls present against the one the model's own triangles "
        + "present, per airframe and aspect: a raster of rays from a fixed standoff counted "
        + "against the hulls and against the mesh, the rounds each instrument loses and invents, "
        + "and a real held gun burst over the same bearings as the control on both counts")]
    internal static void AirframeColliderHitRate(TestContext ctx)
    {
        ctx.RequireData(ctx.PlanesGamezPath, $"planes gamez");
        ctx.RequireData(ctx.ZrdrPath, $"zrdr archive");
        string texturesPath = SessionPaths.ChapterTextures(ctx.DataRoot, "C1");
        ctx.RequireData(texturesPath, $"C1 textures");

        var planesGamez = GameZ.Load(ctx.PlanesGamezPath);
        var weapons = WeaponDefs.Load(ctx.ZrdrPath, null);
        var gun = weapons.All.FirstOrDefault(w => w.IsCannon && w.ArmorDamage is > 0f);
        ctx.Check(gun != null, $"a cannon carrying ARMOR_DAMAGE exists in the data");
        if (gun == null)
            return;
        ctx.Note($"gun {gun.Id} at {Difficulty.Word(Difficulty.Hard)} difficulty (the unscaled pool tier); the count is rounds registered, never a kill time");

        var textures = new TextureArchive(texturesPath);
        var report = new StringBuilder();
        report.AppendLine("airframe aspect rays hull poly both hull_only poly_only hull_m2 poly_m2 ratio fired registered");
        ProjectilePool? pool = null;
        try
        {
            var live = new ProjectilePool(textures, null, null);
            pool = live;
            live.ScoredShooters.Add(ShooterId);
            ctx.Host.AddChild(live);
            foreach (var (model, _) in MarkerRig.PlayerAirframes)
                MeasureHitRate(ctx, report, planesGamez, textures, live, gun, model);
        }
        finally
        {
            pool?.Free();
            textures.Dispose();
        }
        ctx.WriteArtifact("test-airframe-collider-hit-rate.txt", report.ToString());
    }

    [Suite("airframe-hull-coverage",
        "every player airframe's collision hulls measured against its own mesh: each hull "
        + "inside the box it replaces, the whole silhouette's triangle area covered by some hull "
        + "within the tolerance, the part names and their order the damage mapping relies on, "
        + "and the per-part box-to-hull volume the sweep no longer bridges")]
    internal static void AirframeHullCoverage(TestContext ctx)
    {
        ctx.RequireData(ctx.PlanesGamezPath, $"planes gamez");
        string texturesPath = SessionPaths.ChapterTextures(ctx.DataRoot, "C1");
        ctx.RequireData(texturesPath, $"C1 textures");
        var planesGamez = GameZ.Load(ctx.PlanesGamezPath);
        var textures = new TextureArchive(texturesPath);
        var report = new StringBuilder();
        report.AppendLine("airframe part box_m3 hull_m3 removed_m3 verts uncovered_box uncovered_hull");
        try
        {
            foreach (var (model, display) in MarkerRig.PlayerAirframes)
            {
                Node3D? plane = null;
                try
                {
                    plane = new PlaneBuilder(planesGamez, textures).Build(model);
                    ctx.Host.AddChild(plane);
                    MeasureOne(ctx, report, display, plane);
                }
                finally
                {
                    plane?.Free();
                }
            }
        }
        finally
        {
            textures.Dispose();
        }
        ctx.WriteArtifact("test-airframe-hull-coverage.txt", report.ToString());
    }

    private static void MeasureOne(TestContext ctx, StringBuilder report, string display, Node3D plane)
    {
        var tris = new List<PlaneCollider.Triangle>();
        PlaneCollider.Collect(plane, plane.Transform, tris);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var regions = PlaneCollider.Layout(tris);
        watch.Stop();
        ctx.Note($"{display}: layout of {tris.Count} triangles took {watch.Elapsed.TotalMilliseconds:0.0} ms");
        ctx.Check(regions.Count is >= 1 and <= 16, $"{display}: {regions.Count} hulls within the budget");
        ctx.Check(regions.Count > 0 && regions[0].Name == "fuselage", $"{display}: the fuselage hull leads the part order");

        float boxTotal = 0f, hullTotal = 0f;
        foreach (var r in regions)
        {
            float boxVol = r.Hull.Bounds.Size.X * r.Hull.Bounds.Size.Y * r.Hull.Bounds.Size.Z;
            boxTotal += boxVol;
            hullTotal += r.Hull.Volume;
            ctx.Check(r.Name is "fuselage" or "tail" or "wing" or "canard", $"{display}: {r.Name} is a part the damage mapping knows");
            ctx.Check(r.Hull.Points.Length >= 4, $"{display}: {r.Name} hull has a volume verts={r.Hull.Points.Length}");
            ctx.Check(r.Hull.Volume <= boxVol * 1.001f, $"{display}: {r.Name} hull inside its box hull={r.Hull.Volume:0.00} box={boxVol:0.00}");
            ctx.Check(r.Hull.Bounds.Size.X >= 0.29f && r.Hull.Bounds.Size.Y >= 0.29f && r.Hull.Bounds.Size.Z >= 0.29f,
                $"{display}: {r.Name} hull keeps the thickness floor size={r.Hull.Bounds.Size}");
            report.AppendLine($"{display} {r.Name} {boxVol:0.00} {r.Hull.Volume:0.00} {boxVol - r.Hull.Volume:0.00} {r.Hull.Points.Length}");
        }

        float area = 0f, outsideBoxes = 0f, outsideHulls = 0f;
        foreach (var t in tris)
        {
            float a = 0.5f * (t.B - t.A).Cross(t.C - t.A).Length();
            area += a;
            if (!Covered(regions, t, byHull: false))
                outsideBoxes += a;
            if (!Covered(regions, t, byHull: true))
                outsideHulls += a;
        }
        float boxFrac = area > 0f ? outsideBoxes / area : 0f;
        float hullFrac = area > 0f ? outsideHulls / area : 0f;
        ctx.Check(hullFrac <= MaxUncoveredFraction,
            $"{display}: triangle area outside every hull {hullFrac * 100f:0.00}% (boxes left {boxFrac * 100f:0.00}%)");
        ctx.Note($"{display}: {regions.Count} hulls, box {boxTotal:0.0} m3 -> hull {hullTotal:0.0} m3 ({(1f - hullTotal / boxTotal) * 100f:0}% of the box volume removed), uncovered area boxes {boxFrac * 100f:0.00}% hulls {hullFrac * 100f:0.00}%");
        report.AppendLine($"{display} total {boxTotal:0.00} {hullTotal:0.00} {boxTotal - hullTotal:0.00} - {boxFrac * 100f:0.00}% {hullFrac * 100f:0.00}%");
    }

    // Whether every corner of the triangle is inside some region's hull (or, for the
    // comparison column, the box that hull replaces).
    private static bool Covered(List<PlaneCollider.Region> regions, PlaneCollider.Triangle t, bool byHull)
    {
        return Inside(t.A) && Inside(t.B) && Inside(t.C);

        bool Inside(Vector3 p)
        {
            foreach (var r in regions)
            {
                var local = p - r.Centre;
                if (byHull ? r.Hull.Contains(local, CoverTolerance)
                    : r.Hull.Bounds.Grow(CoverTolerance).HasPoint(local))
                    return true;
            }
            return false;
        }
    }

    private static void MeasureHitRate(TestContext ctx, StringBuilder report, GameZ planesGamez,
        TextureArchive textures, ProjectilePool live, WeaponDef gun, string model)
    {
        string display = MarkerRig.PlayerAirframes.First(a => a.Model == model).Display;
        var stats = PlaneStats.Load(ctx.ZrdrPath, model);
        FlightController? rig = null;
        try
        {
            var plane = new PlaneBuilder(planesGamez, textures).Build(model);
            rig = new FlightController
            {
                PlaneModel = plane,
                Collider = PlaneCollider.Build(plane),
                Damage = new PlaneDamage(stats.DestroyableParts),
                PlayerIndex = TargetId,
                Projectiles = live,
                UseKeyboard = false,
                AllowPause = false,
                IsHumanPiloted = false,
            };
            rig.AddChild(plane);
            // Nose on world -Z (identity attitude), so a plane-local point is the world one minus
            // the spawn position and every bearing below is an aspect of the airframe itself.
            rig.Setup(new FlightModel(stats), ctx.Camera, new CamParams(), TargetPos,
                TargetPos + Vector3.Forward);
            ctx.Host.AddChild(rig);
            ctx.SyncPhysics();
            ctx.Check(rig.Body != null && rig.Collider != null,
                $"{display}: the rig derived hulls and mounted an aircraft body");
            if (rig.Body == null || rig.Collider == null)
                return;

            var tris = new List<PlaneCollider.Triangle>();
            PlaneCollider.Collect(plane, plane.Transform, tris);
            var mesh = new List<PlaneCollider.Triangle>(tris.Count);
            foreach (var t in tris)
                mesh.Add(new PlaneCollider.Triangle(t.A + TargetPos, t.B + TargetPos, t.C + TargetPos));
            var hullParts = HullTriangles(rig.Collider);
            ctx.Check(mesh.Count > 0 && hullParts.Count > 0,
                $"{display}: {mesh.Count} mesh triangles and {hullParts.Count} hulls to compare");
            if (mesh.Count == 0 || hullParts.Count == 0)
                return;

            foreach (var (aspect, from) in Aspects)
                MeasureAspect(ctx, report, live, gun, rig, display, aspect, from, mesh, hullParts);
            ctx.Check(!rig.Crashed, $"{display}: the target survived every burst, so no round met a dead airframe");
        }
        finally
        {
            rig?.Free();
        }
    }

    private static void MeasureAspect(TestContext ctx, StringBuilder report, ProjectilePool live,
        WeaponDef gun, FlightController rig, string display, string aspect, Vector3 from,
        List<PlaneCollider.Triangle> mesh, List<(string Name, List<PlaneCollider.Triangle> Faces)> hullParts)
    {
        var box = Enclose(mesh);
        var centre = box.GetCenter();
        var muzzle = centre + from * Standoff;
        var axis = (centre - muzzle).Normalized();
        var u = axis.Cross(Vector3.Up).Normalized();
        var v = axis.Cross(u).Normalized();
        Window(muzzle, axis, centre, u, v, box, out float halfU, out float halfV);
        var meshBins = new SilhouetteBins(mesh, muzzle, axis, centre, u, v, halfU, halfV);
        var partBins = new List<(string Name, SilhouetteBins Bins)>();
        foreach (var (name, faces) in hullParts)
            partBins.Add((name, new SilhouetteBins(faces, muzzle, axis, centre, u, v, halfU, halfV)));

        float reach = Standoff * 2f;
        int hullHits = 0, polyHits = 0, both = 0, hullOnly = 0, polyOnly = 0;
        var blame = new Dictionary<string, int>();
        var burst = new List<Vector3>();
        int burstHull = 0;
        for (int i = 0; i < RayGrid; i++)
        {
            float x = -halfU + 2f * halfU * (i + 0.5f) / RayGrid;
            for (int j = 0; j < RayGrid; j++)
            {
                float y = -halfV + 2f * halfV * (j + 0.5f) / RayGrid;
                var dir = (centre + u * x + v * y - muzzle).Normalized();
                string? struck = null;
                foreach (var (name, bins) in partBins)
                {
                    if (!bins.Hit(muzzle, dir, reach, x, y))
                        continue;
                    struck = name;
                    break;
                }
                bool inHull = struck != null;
                bool inPoly = meshBins.Hit(muzzle, dir, reach, x, y);
                if (inHull)
                    hullHits++;
                if (inPoly)
                    polyHits++;
                if (inHull && inPoly)
                {
                    both++;
                }
                else if (inHull)
                {
                    hullOnly++;
                    blame.TryGetValue(struck!, out int seen);
                    blame[struck!] = seen + 1;
                }
                else if (inPoly)
                {
                    polyOnly++;
                }
                if ((i * RayGrid + j) % BurstStride != 0)
                    continue;
                burst.Add(dir);
                if (inHull)
                    burstHull++;
            }
        }

        int rays = RayGrid * RayGrid;
        ctx.Check(polyHits > 0 && hullHits > 0, $"{display} {aspect}: the raster struck both instruments hull={hullHits} poly={polyHits}");
        if (polyHits == 0)
            return;
        float window = 4f * halfU * halfV;
        float hullArea = window * hullHits / rays;
        float polyArea = window * polyHits / rays;
        float ratio = (float)hullHits / polyHits;
        var (fired, registered) = FireBurst(live, gun, rig, muzzle, burst);

        // The control's own rate must land on the raster's hull rate over the same bearings: a
        // round tests the physics shapes, and the raster tests the hull faces they were built from.
        float control = fired > 0 ? (float)registered / fired : 0f;
        float expected = burst.Count > 0 ? (float)burstHull / burst.Count : 0f;
        ctx.Same(burst.Count, fired, $"{display} {aspect}: every bearing of the burst created a round");
        // The one direction the decomposition may never take: a hull inside the silhouette would
        // drop rounds the original's polygon loop registers, and no tuning recovers a lost hit.
        ctx.Check(polyOnly <= MaxLostFraction * polyHits,
            $"{display} {aspect}: the hulls lose no hit the mesh takes lost={polyOnly}/{polyHits}");
        ctx.Check(Mathf.Abs(control - expected) <= 0.02f,
            $"{display} {aspect}: the gun burst registers what the hull raster predicts burst={control * 100f:0.0}% raster={expected * 100f:0.0}%");
        string worst = hullOnly > 0 ? Blame(blame, hullOnly) : "none";
        ctx.Note($"{display} {aspect}: hull {hullArea:0.00} m2 vs mesh {polyArea:0.00} m2, ratio {ratio:0.000}, invented {100f * hullOnly / polyHits:0.0}%, lost {100f * polyOnly / polyHits:0.0}%, from {worst} (burst {registered}/{fired})");
        report.AppendLine(Log.Format(
            $"{display} {aspect} {rays} {hullHits} {polyHits} {both} {hullOnly} {polyOnly} {hullArea:0.000} {polyArea:0.000} {ratio:0.0000} {fired} {registered} {worst}"));
    }

    // Fires the sampled bearings as real rounds from one muzzle, in small groups so a step's
    // damage stays far under the whole pool, and refills the target between steps so the whole
    // burst meets a live airframe. Returns the pool's own fired/registered pair.
    private static (int Fired, int Registered) FireBurst(ProjectilePool live, WeaponDef gun,
        FlightController rig, Vector3 muzzle, List<Vector3> burst)
    {
        int firedBefore = live.CannonRoundsFired;
        int hitBefore = live.CannonHits;
        var station = new Transform3D(Basis.Identity, muzzle);
        for (int k = 0; k < burst.Count; k += BurstGroup)
        {
            int end = Mathf.Min(k + BurstGroup, burst.Count);
            for (int m = k; m < end; m++)
                live.Spawn(gun, station, Vector3.Zero, ShooterId, aimDir: burst[m]);
            for (int s = 0; s < BurstSteps; s++)
            {
                live.SimStep(1f / 60f);
                rig.Damage!.Reset();
            }
            live.Clear();
        }
        return (live.CannonRoundsFired - firedBefore, live.CannonHits - hitBefore);
    }

    // Each hull's faces as world triangles, kept per part so an invented hit can be attributed to
    // the hull that presents it. A round's ray meets exactly these: the physics shape built over a
    // hull's points is that same convex body.
    private static List<(string Name, List<PlaneCollider.Triangle> Faces)> HullTriangles(
        PlaneCollider collider)
    {
        var parts = new List<(string Name, List<PlaneCollider.Triangle> Faces)>();
        foreach (var part in collider.Parts)
        {
            var faces = new List<PlaneCollider.Triangle>();
            var pts = part.Hull.Points;
            var at = TargetPos + part.Local.Origin;
            var idx = part.Hull.Faces;
            for (int f = 0; f + 2 < idx.Length; f += 3)
                faces.Add(new PlaneCollider.Triangle(
                    at + pts[idx[f]], at + pts[idx[f + 1]], at + pts[idx[f + 2]]));
            parts.Add((part.Name, faces));
        }
        return parts;
    }

    // The invented rays as a share of themselves, worst hull first, so the report names where a
    // narrower decomposition would pay.
    private static string Blame(Dictionary<string, int> tally, int total)
    {
        var rolled = new Dictionary<string, int>();
        foreach (var (name, count) in tally)
        {
            rolled.TryGetValue(name, out int seen);
            rolled[name] = seen + count;
        }
        return string.Join(" ", rolled.OrderByDescending(e => e.Value)
            .Select(e => Log.Format($"{e.Key}={100f * e.Value / total:0}%")));
    }

    // The aim window: the model's box corners projected onto the aim plane, widened by the
    // margin, so no part of either silhouette falls outside the raster.
    private static void Window(Vector3 muzzle, Vector3 axis, Vector3 centre, Vector3 u, Vector3 v,
        Aabb box, out float halfU, out float halfV)
    {
        halfU = 0f;
        halfV = 0f;
        for (int c = 0; c < 8; c++)
        {
            var corner = box.Position + box.Size * new Vector3(c & 1, (c >> 1) & 1, (c >> 2) & 1);
            if (!Project(muzzle, axis, centre, u, v, corner, out float x, out float y))
                continue;
            halfU = Mathf.Max(halfU, Mathf.Abs(x));
            halfV = Mathf.Max(halfV, Mathf.Abs(y));
        }
        halfU = Mathf.Max(halfU, 0.5f) * WindowMargin;
        halfV = Mathf.Max(halfV, 0.5f) * WindowMargin;
    }

    // Where a world point lands on the aim plane, in the window's own two axes, seen from the
    // muzzle. False for anything at or behind the muzzle, which no raster ray can reach.
    private static bool Project(Vector3 muzzle, Vector3 axis, Vector3 centre, Vector3 u, Vector3 v,
        Vector3 p, out float x, out float y)
    {
        x = 0f;
        y = 0f;
        float depth = (p - muzzle).Dot(axis);
        if (depth < 1f)
            return false;
        var on = muzzle + (p - muzzle) * ((centre - muzzle).Dot(axis) / depth);
        x = (on - centre).Dot(u);
        y = (on - centre).Dot(v);
        return true;
    }

    private static Aabb Enclose(List<PlaneCollider.Triangle> tris)
    {
        var box = new Aabb(tris[0].A, Vector3.Zero).Expand(tris[0].B).Expand(tris[0].C);
        foreach (var t in tris)
            box = box.Expand(t.A).Expand(t.B).Expand(t.C);
        return box;
    }

    // Moller-Trumbore, two-sided on purpose: a silhouette is what the segment crosses, and the
    // original's own backface rule is a property of the polygon, not of the collision volume.
    private static bool Crosses(Vector3 from, Vector3 dir, float reach, PlaneCollider.Triangle t)
    {
        var e1 = t.B - t.A;
        var e2 = t.C - t.A;
        var p = dir.Cross(e2);
        float det = e1.Dot(p);
        if (Mathf.Abs(det) < 1e-9f)
            return false;
        float inv = 1f / det;
        var rel = from - t.A;
        float u = rel.Dot(p) * inv;
        if (u < 0f || u > 1f)
            return false;
        var q = rel.Cross(e1);
        float v = dir.Dot(q) * inv;
        if (v < 0f || u + v > 1f)
            return false;
        float dist = e2.Dot(q) * inv;
        return dist > 0f && dist <= reach;
    }

    // Triangles filed by the window cells their projection covers, so one ray tests its own cell
    // rather than the whole airframe. A triangle the projection cannot place goes in the spill
    // list every ray tests, so binning can only cost time, never a hit.
    private sealed class SilhouetteBins
    {
        private readonly List<PlaneCollider.Triangle>[] _cells =
            new List<PlaneCollider.Triangle>[BinSide * BinSide];

        private readonly List<PlaneCollider.Triangle> _spill = new();
        private readonly float _halfU;
        private readonly float _halfV;

        public SilhouetteBins(List<PlaneCollider.Triangle> tris, Vector3 muzzle, Vector3 axis,
            Vector3 centre, Vector3 u, Vector3 v, float halfU, float halfV)
        {
            _halfU = halfU;
            _halfV = halfV;
            foreach (var t in tris)
            {
                if (!Project(muzzle, axis, centre, u, v, t.A, out float ax, out float ay)
                    || !Project(muzzle, axis, centre, u, v, t.B, out float bx, out float by)
                    || !Project(muzzle, axis, centre, u, v, t.C, out float cx, out float cy))
                {
                    _spill.Add(t);
                    continue;
                }
                int loI = Cell(Mathf.Min(ax, Mathf.Min(bx, cx)), halfU);
                int hiI = Cell(Mathf.Max(ax, Mathf.Max(bx, cx)), halfU);
                int loJ = Cell(Mathf.Min(ay, Mathf.Min(by, cy)), halfV);
                int hiJ = Cell(Mathf.Max(ay, Mathf.Max(by, cy)), halfV);
                for (int i = loI; i <= hiI; i++)
                {
                    for (int j = loJ; j <= hiJ; j++)
                        (_cells[i * BinSide + j] ??= new List<PlaneCollider.Triangle>()).Add(t);
                }
            }
        }

        public bool Hit(Vector3 from, Vector3 dir, float reach, float x, float y)
        {
            foreach (var t in _spill)
            {
                if (Crosses(from, dir, reach, t))
                    return true;
            }
            var cell = _cells[Cell(x, _halfU) * BinSide + Cell(y, _halfV)];
            if (cell == null)
                return false;
            foreach (var t in cell)
            {
                if (Crosses(from, dir, reach, t))
                    return true;
            }
            return false;
        }

        private static int Cell(float value, float half) =>
            Mathf.Clamp((int)((value + half) / (2f * half) * BinSide), 0, BinSide - 1);
    }
}
