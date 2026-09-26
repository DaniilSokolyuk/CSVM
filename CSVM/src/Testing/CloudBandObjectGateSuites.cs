using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using CSVM.Flight.Airframe;
using CSVM.Mech3;
using CSVM.Session.Objectives;
using CSVM.Session.World;
using Godot;

namespace CSVM.Testing;

/// <summary>The cloud band's per-object visibility. A zeppelin or an aeroplane sitting wholly on
/// the far side of the band's midpoint from the camera is not drawn at all. That is the original's
/// per-object zone assignment plus its camera zone gate. Flies C1C/M01's own band and its own
/// zeppelin records rather than invented numbers.</summary>
internal static class CloudBandObjectGateSuites
{
    // CM06's mission, CLOUD_COVER 1055-1110 with a 30 m core. The midpoint the objects are judged
    // against is 1082.5, and the camera's own state flips at 1067.5.
    private const string BandChapter = "C1C";
    private const string BandMission = "M01";

    // A fresh Camera3D's cull mask, the mask WeatherRig narrows per camera.
    private const uint FullMask = 0xFFFFFu;

    // No chapter fog volumes: C1C arms none, and the band alone is this suite's subject.
    private static readonly FogVolumeBox[] NoVolumes = Array.Empty<FogVolumeBox>();

    [Suite("cloud-band-object-gate",
        "an object wholly above the CLOUD_COVER band's midpoint earns zone 2 and one wholly below "
        + "it zone 1, so C1C/M01's zeppelin hull is culled for a camera on the other side of the "
        + "band and drawn for one on its own side; a straddling hull and a --no-fog run stay "
        + "ungated")]
    internal static void CloudBandObjectGate(TestContext ctx)
    {
        string missionZrdr = SessionPaths.MissionZrdr(ctx.DataRoot, BandChapter, BandMission);
        string chapterZrdr = SessionPaths.ChapterZrdr(ctx.DataRoot, BandChapter);
        ctx.RequireData(missionZrdr, $"{BandChapter}/{BandMission} zrdr");
        ctx.RequireData(chapterZrdr, $"{BandChapter} zrdr");

        var weather = WeatherState.Load(missionZrdr)
            ?? throw new SuiteSkippedException($"{BandChapter}/{BandMission} carries no weather.zrd");
        ctx.Check(weather.HasCloudBand,
            $"{BandChapter}/{BandMission} authors a CLOUD_COVER band, bottom={F(weather.CloudBottom)} top={F(weather.CloudTop)}");
        if (!weather.HasCloudBand)
        {
            return;
        }

        float centre = weather.CloudBandCentre;
        var report = new StringBuilder();
        report.AppendLine(
            $"{BandChapter}/{BandMission}: band {F(weather.CloudBottom)}-{F(weather.CloudTop)}, "
            + $"midpoint {F(centre)}, core bottom {F(weather.CloudCoreBottom)}");

        ctx.Same(2, weather.ObjectZone(centre + 100f, centre + 140f),
            $"a hull wholly above the midpoint earns zone 2");
        ctx.Same(1, weather.ObjectZone(centre - 140f, centre - 100f),
            $"a hull wholly below the midpoint earns zone 1");
        ctx.Same(-1, weather.ObjectZone(centre - 20f, centre + 20f),
            $"a hull straddling the midpoint earns -1, drawn at every camera state");

        var defs = Zeppelins.Load(missionZrdr);
        ctx.Check(defs.Count > 0, $"{BandChapter}/{BandMission} authors zeppelin records, count={defs.Count}");
        if (defs.Count == 0)
        {
            return;
        }

        var gate = new ObjectZoneGate();
        var hosts = new Dictionary<string, Node3D>();
        var hulls = new Dictionary<string, MeshInstance3D>();
        ZeppelinRuntime? runtime = null;
        try
        {
            foreach (var def in defs)
            {
                var host = new Node3D { Name = def.Node };
                // A 40 m hull, the drawn part the gate measures the object's altitude span off.
                // The record's own node carries no mesh outside a built chapter world.
                var hull = new MeshInstance3D
                {
                    Name = "hull",
                    Mesh = new BoxMesh { Size = new Vector3(20f, 40f, 120f) },
                };
                host.AddChild(hull);
                ctx.Host.AddChild(host);
                hosts[def.Node] = host;
                hulls[def.Node] = hull;
            }

            var nets = AiNets.Load(chapterZrdr);
            runtime = new ZeppelinRuntime(defs, name => hosts.TryGetValue(name, out var h) ? h : null, nets);
            ctx.Check(runtime.LiveCount > 0, $"the mission's zeppelins place live, count={runtime.LiveCount}");

            var flown = new List<Node3D>();
            runtime.CollectHosts(flown);
            ctx.Same(runtime.LiveCount, flown.Count, $"CollectHosts offers every placed hull to the gate");
            if (flown.Count == 0)
            {
                return;
            }

            var subject = flown[0];
            var subjectHull = hulls[subject.Name];
            float below = weather.CloudCoreBottom - 200f;
            float above = weather.CloudCoreBottom + 200f;

            AboveTheBand(ctx, gate, weather, flown, subject, subjectHull, centre, below, above, report);
            BelowTheBand(ctx, gate, weather, flown, subject, subjectHull, centre, report);
            Straddling(ctx, gate, weather, flown, subject, subjectHull, centre, report);
            GateOpen(ctx, gate, weather, flown, subject, subjectHull, centre, report);
            InAFogVolume(ctx, weather, centre);
        }
        finally
        {
            runtime?.Free();
            foreach (var host in hosts.Values)
            {
                host.Free();
            }
        }

        ctx.WriteArtifact("test-cloud-band-object-gate.txt", report.ToString());
        ctx.Note($"{BandChapter}/{BandMission}: a zeppelin above the band's {F(centre)} m midpoint is culled for a camera under the band and drawn for one above it, and the mirror holds");
    }

    private static void AboveTheBand(TestContext ctx, ObjectZoneGate gate, WeatherState weather,
        List<Node3D> flown, Node3D subject, MeshInstance3D hull, float centre, float below,
        float above, StringBuilder report)
    {
        subject.Position = new Vector3(0f, centre + 150f, 0f);
        gate.Tick(flown, weather, fogZoneArmed: false, NoVolumes, open: false);
        uint layer = hull.Layers;
        report.AppendLine($"hull at {F(subject.Position.Y)}: layers=0x{layer:x}, expected zone 2 = 0x{ZoneGate.LayerFor(2):x}");
        ctx.Same(ZoneGate.LayerFor(2), layer, $"a hull above the midpoint moves onto zone 2's layer");
        ctx.Check(gate.GatedObjects > 0, $"the gate reports the hull gated, count={gate.GatedObjects}");
        ctx.Same(1, CameraState(weather, below), $"a camera under the band is in state 1");
        ctx.Check((ZoneGate.CullMask(FullMask, 1) & layer) == 0,
            $"a camera in state 1 does not draw it, the whole point of the band's per-object gate");
        ctx.Same(2, CameraState(weather, above), $"a camera above the core's bottom is in state 2");
        ctx.Check((ZoneGate.CullMask(FullMask, 2) & layer) != 0,
            $"a camera in state 2 does draw it, so the hull is hidden by the band and not by the gate");
    }

    private static void BelowTheBand(TestContext ctx, ObjectZoneGate gate, WeatherState weather,
        List<Node3D> flown, Node3D subject, MeshInstance3D hull, float centre, StringBuilder report)
    {
        subject.Position = new Vector3(0f, centre - 150f, 0f);
        gate.Tick(flown, weather, fogZoneArmed: false, NoVolumes, open: false);
        uint layer = hull.Layers;
        report.AppendLine($"hull at {F(subject.Position.Y)}: layers=0x{layer:x}, expected zone 1 = 0x{ZoneGate.LayerFor(1):x}");
        ctx.Same(ZoneGate.LayerFor(1), layer, $"the mirror: a hull below the midpoint moves onto zone 1's layer");
        ctx.Check((ZoneGate.CullMask(FullMask, 2) & layer) == 0,
            $"a camera above the band does not draw it");
        ctx.Check((ZoneGate.CullMask(FullMask, 1) & layer) != 0,
            $"a camera under the band does draw it");
    }

    private static void Straddling(TestContext ctx, ObjectZoneGate gate, WeatherState weather,
        List<Node3D> flown, Node3D subject, MeshInstance3D hull, float centre, StringBuilder report)
    {
        subject.Position = new Vector3(0f, centre, 0f);
        gate.Tick(flown, weather, fogZoneArmed: false, NoVolumes, open: false);
        uint layer = hull.Layers;
        report.AppendLine($"hull at {F(subject.Position.Y)} (straddling): layers=0x{layer:x}");
        ctx.Check((ZoneGate.CullMask(FullMask, 1) & layer) != 0
            && (ZoneGate.CullMask(FullMask, 2) & layer) != 0,
            $"a hull standing in the band itself draws at both camera states, the binary's own -1");
    }

    private static void GateOpen(TestContext ctx, ObjectZoneGate gate, WeatherState weather,
        List<Node3D> flown, Node3D subject, MeshInstance3D hull, float centre, StringBuilder report)
    {
        subject.Position = new Vector3(0f, centre + 150f, 0f);
        gate.Tick(flown, weather, fogZoneArmed: false, NoVolumes, open: true);
        uint layer = hull.Layers;
        report.AppendLine($"hull at {F(subject.Position.Y)} with the gate open: layers=0x{layer:x}");
        ctx.Check((ZoneGate.CullMask(FullMask, 1) & layer) != 0,
            $"--no-fog opens the gate, so the same hull draws for a camera under the band");
        ctx.Same(0, gate.GatedObjects, $"the open gate reports nothing gated");
    }

    // The binary tests fog-volume membership before the band, so a hull inside an armed volume is
    // zone 3 whatever its altitude. A camera in that volume is state 3 and keeps drawing it.
    private static void InAFogVolume(TestContext ctx, WeatherState weather, float centre)
    {
        var span = new Aabb(new Vector3(-10f, centre + 140f, -60f), new Vector3(20f, 40f, 120f));
        var box = new Aabb(new Vector3(-500f, centre, -500f), new Vector3(1000f, 400f, 1000f));
        var volume = new FogVolumeBox("suite", box, PlanesOf(box));
        var armed = new[] { volume };
        ctx.Same(2, weather.ObjectZone(span, fogZoneArmed: false, NoVolumes),
            $"a disarmed chapter judges the hull by the band alone");
        ctx.Same(3, weather.ObjectZone(span, fogZoneArmed: true, armed),
            $"an armed volume the hull stands in wins over the band, the binary's own first arm");
    }

    private static Plane[] PlanesOf(Aabb box)
    {
        var min = box.Position;
        var max = box.End;
        return new[]
        {
            new Plane(Vector3.Left, -min.X), new Plane(Vector3.Right, max.X),
            new Plane(Vector3.Down, -min.Y), new Plane(Vector3.Up, max.Y),
            new Plane(Vector3.Forward, -min.Z), new Plane(Vector3.Back, max.Z),
        };
    }

    private static int CameraState(WeatherState weather, float altitude) =>
        weather.CameraWeatherState(new Vector3(0f, altitude, 0f), false, NoVolumes);

    private static string F(float value) => value.ToString("0.0", CultureInfo.InvariantCulture);
}
