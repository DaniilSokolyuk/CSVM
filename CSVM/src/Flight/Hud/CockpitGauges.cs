using System;
using System.Collections.Generic;
using System.Linq;
using CSVM.Mech3;
using CSVM.Utils;
using Godot;

namespace CSVM.Flight.Hud;

/// <summary>
/// Drives the authored instrument panel inside the pilot's own <c>cockpit1</c> interior: the five
/// needle nodes take an absolute angle each frame, and the two warning lamps take the visibility
/// their condition says. Both read <see cref="GaugeCluster"/>'s already-computed state rather than
/// re-deriving it, so the 3D panel and the screen-space dials never disagree or blink out of step.
/// <see cref="Bind"/> finds the nodes in one built interior; <see cref="Apply"/> is the per-frame
/// write, the same shape <see cref="CockpitVisibility"/> uses.
/// </summary>
public sealed class CockpitGauges
{
    private readonly Needle _altHundreds, _altThousands, _speed, _nitroBoost, _nitroCharge;
    private readonly Needle _gunArrow, _missileArrow;
    private readonly Horizon _horizon;
    private readonly CompassDrum _compass;
    private readonly List<Belt> _belts;
    private readonly List<DamageZoneSkin> _zones;
    private readonly List<Readout> _readouts;
    private readonly Node3D? _lowAltLamp, _stallLamp, _nitroDial;

    private CockpitGauges(Node3D gauges, IReadOnlyDictionary<ulong, string> textureNames)
    {
        _belts = Belt.FindAll(gauges, textureNames);
        _zones = DamageZoneSkin.FindAll(gauges, textureNames);
        _readouts = Readout.FindAll(gauges);
        _altHundreds = Needle.Find(gauges, "hundreds");
        _altThousands = Needle.Find(gauges, "thousands");
        _speed = Needle.Find(gauges, "speed");
        _nitroBoost = Needle.Find(gauges, "nitro_boost");
        _nitroCharge = Needle.Find(gauges, "nitro_charge");
        _gunArrow = Needle.Find(gauges, "ggarrow");
        _missileArrow = Needle.Find(gauges, "mgarrow");
        // Found by NAME anywhere under gauges, never by "horizn": that name is the dial FACE on
        // 5 of 11 airframes and the ball's own container on the other 6, while "pfhorizon" is the
        // ball mesh on all 11 (docs/formats/hud.md). Null on any build that ships none.
        _horizon = Horizon.Find(gauges, "pfhorizon");
        // The binary's own lookup string is "compass"; the DATA names the same node "comp" on
        // some airframes (docs/formats/hud.md), so try the engine's name first and fall back to
        // the data's, the same two-name search pfhorizon needed against horizn/horiz.
        _compass = CompassDrum.Find(gauges, "compass", "comp");
        _lowAltLamp = FindNamed(gauges, "lowalt_on");
        _stallLamp = FindNamed(gauges, "stallwarning_on");
        _nitroDial = FindNamed(gauges, "nitrogauge");
        // A bind that locates the panel but finds zero belts or zero damage zones is an anomaly
        // that used to be silent; this line is what makes it visible again.
        int needles = new[]
            { _altHundreds, _altThousands, _speed, _nitroBoost, _nitroCharge, _gunArrow, _missileArrow }
            .Count(n => n.IsBound) + (_horizon.IsBound ? 1 : 0) + (_compass.IsBound ? 1 : 0);
        int lamps = new[] { _lowAltLamp, _stallLamp, _nitroDial }.Count(n => n != null);
        Log.Debug("flight", $"cockpit gauges bound needles={needles} lamps={lamps} readouts={_readouts.Count} belts={_belts.Count} zones={_zones.Count}");
    }

    /// <summary>How many belt positions this bind found a matching skin for. Not a fixed
    /// constant across airframes; diagnostic and suite use only.</summary>
    public int BeltCount => _belts.Count;

    /// <summary>How many damage zones this bind found a matching skin for. Per-model, not a
    /// fixed constant; diagnostic and suite use only.</summary>
    public int DamageZoneCount => _zones.Count;

    /// <summary>Finds the panel inside one built interior, or null when there is no interior (an AI
    /// plane, or any build that did not ask <see cref="PlaneBuilder"/> for one) or no
    /// <c>gauges</c> subtree in it.</summary>
    public static CockpitGauges? Bind(Node3D? interior,
        IReadOnlyList<(ShaderMaterial Material, string TextureName)>? materials = null)
    {
        if (interior == null || FindNamed(interior, "gauges") is not { } gauges)
        {
            return null;
        }
        // Keyed by instance id, not by the wrapper object: Godot hands back a fresh managed wrapper
        // for the same native material, so reference equality is not dependable here.
        var names = new Dictionary<ulong, string>();
        foreach (var (material, texture) in materials ?? Array.Empty<(ShaderMaterial, string)>())
        {
            names[material.GetInstanceId()] = texture;
        }
        return new CockpitGauges(gauges, names);
    }

    /// <summary>Binds through the one expression every caller must use: this builder's own
    /// interior paired with its own materials, so a caller cannot supply one without the other.
    /// The live flight path and the in-engine suite both call this rather than repeating the two
    /// arguments, so a regression back to the interior alone breaks both instead of only the
    /// controls.</summary>
    public static CockpitGauges? Bind(PlaneBuilder builder) =>
        Bind(builder.CockpitInterior, builder.InteriorMaterials);

    /// <summary>Write this frame's readings onto the panel. Called only while the interior is on
    /// the screen, so an external view costs nothing; the needles hold their last pose behind the
    /// hidden interior, which is what the pilot sees on returning to the cockpit anyway.</summary>
    public void Apply(GaugeCluster gauges)
    {
        // ⚠ Negated: the exposed altimeter and speedometer angles are clockwise-positive (what the
        // screen-space draw wants), while a node rotation about +Z is counter-clockwise-positive.
        _altHundreds.SetAngleDeg(-GaugeCluster.AltHundredsAngleDeg(gauges.AltitudeFt));
        _altThousands.SetAngleDeg(-GaugeCluster.AltThousandsAngleDeg(gauges.AltitudeFt));
        _speed.SetAngleDeg(-GaugeCluster.SpeedAngleDeg(gauges.SpeedMph));
        // The nitro pair is already the decoded Euler-z, so these take the angle unnegated.
        _nitroBoost.SetAngleDeg(gauges.NitroBoostAngleDeg);
        _nitroCharge.SetAngleDeg(gauges.NitroChargeAngleDeg);
        // The belt arrows sweep clockwise like the dial needles. NaN until a loadout binds one,
        // and an unswept arrow keeps the pose it was authored at.
        SetIfSwept(_gunArrow, gauges.GunArrowAngleDeg);
        SetIfSwept(_missileArrow, gauges.MissileArrowAngleDeg);
        _horizon.SetAttitude(gauges.HorizonPitchRad, gauges.HorizonRollRad);
        _compass.SetHeadingDeg(gauges.HeadingDeg);
        Show(_lowAltLamp, gauges.LowAltLampLit);
        Show(_stallLamp, gauges.StallLampLit);
        // ⚠ Only the Devastator ships nitrogauge active:false, so on every other airframe the dial
        // is authored present and the injector is what decides. The screen-space cluster draws it
        // on the same flag; without this the 3D panel shows a nitro dial on a plane with no nitrous.
        Show(_nitroDial, gauges.NitroInstalled);
        foreach (var belt in _belts)
        {
            belt.Apply(gauges);
        }
        foreach (var zone in _zones)
        {
            zone.Apply(gauges);
        }
        foreach (var readout in _readouts)
        {
            readout.Apply(gauges);
        }
    }

    // The bar beside a light, or a zone's border: the screen-space draw splits on the same word.
    private static bool IsHilite(string textureName) =>
        textureName.Contains("hilite", StringComparison.OrdinalIgnoreCase);

    private static void SetIfSwept(Needle needle, float angleDeg)
    {
        if (!float.IsNaN(angleDeg))
        {
            needle.SetAngleDeg(-angleDeg);
        }
    }

    private static void Show(Node3D? node, bool visible)
    {
        if (node != null && node.Visible != visible)
        {
            node.Visible = visible;
        }
    }

    private static MeshInstance3D? FirstMesh(Node3D root)
    {
        if (root is MeshInstance3D mesh)
        {
            return mesh;
        }
        foreach (var child in root.GetChildren())
        {
            if (child is Node3D n3d && FirstMesh(n3d) is { } hit)
            {
                return hit;
            }
        }
        return null;
    }

    private static Node3D? FindNamed(Node3D root, string name)
    {
        foreach (var child in root.GetChildren())
        {
            if (child is not Node3D n3d)
            {
                continue;
            }
            if (AnimRuntime.NameOf(n3d).Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return n3d;
            }
            if (FindNamed(n3d, name) is { } hit)
            {
                return hit;
            }
        }
        return null;
    }

    /// <summary>The driven surfaces of one authored node, each already given a private copy of its
    /// material so a write here cannot reach the other nodes sharing the built one. The texture
    /// NAME each copy started from is what says which colour cycle it belongs to, the same test
    /// the screen-space draw makes on its polygons.</summary>
    /// <summary>One authored needle and the rest pose it hangs at. The modeled rest ROTATION is
    /// arbitrary and the engine overwrites it outright (docs/formats/hud.md), so only the
    /// translation and scale are kept; an unbound needle is a no-op, which is what the Devastator's
    /// absent nitro dial needs.</summary>
    private readonly struct Needle
    {
        private readonly Node3D? _node;
        private readonly Vector3 _origin;
        private readonly Vector3 _scale;

        private Needle(Node3D node)
        {
            _node = node;
            _origin = node.Transform.Origin;
            _scale = node.Transform.Basis.Scale;
        }

        public bool IsBound => _node != null;

        public static Needle Find(Node3D gauges, string name) =>
            FindNamed(gauges, name) is { } node ? new Needle(node) : default;

        public void SetAngleDeg(float degrees)
        {
            if (_node == null)
            {
                return;
            }
            var basis = new Basis(Vector3.Back, Mathf.DegToRad(degrees)).Scaled(_scale);
            _node.Transform = new Transform3D(basis, _origin);
        }
    }

    /// <summary>The artificial-horizon ball, node <c>pfhorizon</c>. Posed the same way a
    /// <see cref="Needle"/> is: the authored rotation is arbitrary and fully overwritten, only the
    /// translation and scale survive. The decode (docs/formats/hud.md) writes the engine's own
    /// basis N = Rz(-roll) . Rx(pitch) straight into the node's rotation fields with no gain,
    /// offset, clamp or smoothing. <c>Vector3.Back</c> is the node's Z axis and <c>Vector3.Right</c>
    /// its X axis, the same axes <see cref="Needle"/> and the interior's head-pitch mount
    /// (<c>PlaneBuilder.MountCockpitInterior</c>) already rotate about; the importer builds every
    /// other node rotation with <c>Basis.FromEuler(v, EulerOrder.Yxz)</c> = Ry . Rx . Rz, the exact
    /// convention the decode's own Euler re-extraction uses, so composing Rz then Rx here with
    /// Godot's own <c>Basis</c> multiplication reproduces the engine's basis with no remap and no
    /// extra sign: unlike the needles, nothing here is expressed as a screen-space clockwise
    /// angle first.</summary>
    private readonly struct Horizon
    {
        private readonly Node3D? _node;
        private readonly Vector3 _origin;
        private readonly Vector3 _scale;

        private Horizon(Node3D node)
        {
            _node = node;
            _origin = node.Transform.Origin;
            _scale = node.Transform.Basis.Scale;
        }

        public bool IsBound => _node != null;

        public static Horizon Find(Node3D gauges, string name) =>
            FindNamed(gauges, name) is { } node ? new Horizon(node) : default;

        public void SetAttitude(float pitchRad, float rollRad)
        {
            if (_node == null)
            {
                return;
            }
            var basis = (new Basis(Vector3.Back, -rollRad) * new Basis(Vector3.Right, pitchRad)).Scaled(_scale);
            _node.Transform = new Transform3D(basis, _origin);
        }
    }

    /// <summary>The compass drum, node <c>compass</c> in the binary's own lookup string, <c>comp</c>
    /// in the data on some airframes (docs/formats/hud.md). The card stays pointed at true north
    /// while its parent (the cockpit, riding the plane) yaws, so the local write must cancel the
    /// parent's yaw. ⚠ That cancelling angle is <c>+heading</c> here, not the <c>-heading</c> the
    /// engine passes to <c>FUN_004d1a30(node, 0, -heading, 0)</c>: <see cref="Airframe.FlightController"/>
    /// derives heading as <c>Atan2(nose.X, -nose.Z)</c>, under which the aircraft node's own yaw is
    /// already <c>-heading</c>, so a second negation turns the drum the wrong way at the right rate
    ///, a fault that hides at rest, where both signs read north. Do not "restore" the decode's
    /// sign without redoing that derivation; the engine's value is right in the engine's Euler
    /// frame, and the flip is the frame conversion, not a correction to the decode.</summary>
    private readonly struct CompassDrum
    {
        private readonly Node3D? _node;
        private readonly Vector3 _origin;
        private readonly Vector3 _scale;

        private CompassDrum(Node3D node)
        {
            _node = node;
            _origin = node.Transform.Origin;
            _scale = node.Transform.Basis.Scale;
        }

        public bool IsBound => _node != null;

        public static CompassDrum Find(Node3D gauges, string engineName, string dataName) =>
            (FindNamed(gauges, engineName) ?? FindNamed(gauges, dataName)) is { } node
                ? new CompassDrum(node)
                : default;

        public void SetHeadingDeg(float headingDeg)
        {
            if (_node == null)
            {
                return;
            }
            var basis = new Basis(Vector3.Up, Mathf.DegToRad(headingDeg)).Scaled(_scale);
            _node.Transform = new Transform3D(basis, _origin);
        }
    }

    private sealed class Skin
    {
        private readonly List<(ShaderMaterial Material, string Texture)> _surfaces = new();

        public static Skin? For(Node3D node, IReadOnlyDictionary<ulong, string> textureNames)
        {
            var skin = new Skin();
            Collect(node, textureNames, skin);
            return skin._surfaces.Count > 0 ? skin : null;
        }

        /// <summary>Point every surface whose source texture matched <paramref name="match"/> at a
        /// replacement. A null replacement leaves that surface alone rather than blanking it.</summary>
        public void Retexture(Func<string, bool> match, Texture2D? replacement)
        {
            if (replacement == null)
            {
                return;
            }
            foreach (var (material, texture) in _surfaces)
            {
                if (match(texture))
                {
                    material.SetShaderParameter("albedo_tex", replacement);
                }
            }
        }

        private static void Collect(Node3D node, IReadOnlyDictionary<ulong, string> names, Skin into)
        {
            if (node is MeshInstance3D mesh && mesh.Mesh != null)
            {
                for (int i = 0; i < mesh.Mesh.GetSurfaceCount(); i++)
                {
                    if (mesh.Mesh.SurfaceGetMaterial(i) is not ShaderMaterial built
                        || !names.TryGetValue(built.GetInstanceId(), out string? texture))
                    {
                        continue;
                    }
                    var own = (ShaderMaterial)built.Duplicate();
                    mesh.SetSurfaceOverrideMaterial(i, own);
                    into._surfaces.Add((own, texture));
                }
            }
            foreach (var child in node.GetChildren())
            {
                if (child is Node3D n3d)
                {
                    Collect(n3d, names, into);
                }
            }
        }
    }

    /// <summary>One authored character readout (<c>4char_ammo</c>, <c>6char_type</c>). The original
    /// addresses these per SURFACE: every cell is its own surface carrying its own cycled material,
    /// and the engine deliberately breaks material batching so each stays addressable. Our builder
    /// groups surfaces by material index and the shipped cells carry one material each, so the
    /// split survives the import and a cell is reachable by its surface. Cell 0 is the leftmost.
    /// </summary>
    private sealed class Readout
    {
        private readonly List<ShaderMaterial> _cells = new();
        private readonly bool _isGun;
        private readonly bool _numeric;

        private Readout(bool isGun, bool numeric)
        {
            _isGun = isGun;
            _numeric = numeric;
        }

        public static List<Readout> FindAll(Node3D gauges)
        {
            var found = new List<Readout>();
            foreach ((string dial, bool isGun) in new[] { ("gungauge", true), ("missilegauge", false) })
            {
                if (FindNamed(gauges, dial) is not { } node)
                {
                    continue;
                }
                foreach ((string cellNode, bool numeric) in new[] { ("4char_ammo", true), ("6char_type", false) })
                {
                    if (FindNamed(node, cellNode) is { } cells && Build(cells, isGun, numeric) is { } readout)
                    {
                        found.Add(readout);
                    }
                }
            }
            return found;
        }

        public void Apply(GaugeCluster gauges)
        {
            string text = _numeric
                ? gauges.BeltCountText(_isGun, _cells.Count)
                : gauges.BeltTypeText(_isGun, _cells.Count);
            if (text.Length == 0)
            {
                return; // no loadout feeds this gauge; the cells keep what they were built with
            }
            for (int i = 0; i < _cells.Count && i < text.Length; i++)
            {
                if (gauges.Glyph(text[i]) is { } glyph)
                {
                    _cells[i].SetShaderParameter(Mech3.SceneBuilder.AlbedoTexParam, glyph);
                }
            }
        }

        // ⚠ Ordered by the cell's own x, not by surface index: the surface order follows the
        // authored polygon order, which is not promised to run left to right.
        private static Readout? Build(Node3D node, bool isGun, bool numeric)
        {
            // The cells hang off the named node rather than on it, the same shape the rest of the
            // built subtree takes.
            if (FirstMesh(node) is not { Mesh: not null } mesh)
            {
                return null;
            }
            var byX = new List<(float X, ShaderMaterial Material)>();
            for (int i = 0; i < mesh.Mesh.GetSurfaceCount(); i++)
            {
                if (mesh.Mesh.SurfaceGetMaterial(i) is not ShaderMaterial built)
                {
                    continue;
                }
                var own = (ShaderMaterial)built.Duplicate();
                mesh.SetSurfaceOverrideMaterial(i, own);
                byX.Add((CentreX(mesh.Mesh, i), own));
            }
            if (byX.Count == 0)
            {
                return null;
            }
            byX.Sort((a, b) => a.X.CompareTo(b.X));
            var readout = new Readout(isGun, numeric);
            foreach (var (_, material) in byX)
            {
                readout._cells.Add(material);
            }
            return readout;
        }

        private static float CentreX(Mesh mesh, int surface)
        {
            var arrays = mesh.SurfaceGetArrays(surface);
            if (arrays.Count <= (int)Mesh.ArrayType.Vertex
                || arrays[(int)Mesh.ArrayType.Vertex].As<Vector3[]>() is not { Length: > 0 } verts)
            {
                return 0f;
            }
            float sum = 0f;
            foreach (var v in verts)
            {
                sum += v.X;
            }
            return sum / verts.Length;
        }
    }

    /// <summary>One belt position on a weapon gauge: its light and its hilite bar take the colour
    /// tier the loadout says, which is the same three-way choice the screen-space dial draws.</summary>
    private sealed class Belt
    {
        private Belt(Skin skin, bool isGun, int position)
        {
            Skin = skin;
            IsGun = isGun;
            Position = position;
        }

        private Skin Skin { get; }

        private bool IsGun { get; }

        private int Position { get; }

        public static List<Belt> FindAll(Node3D gauges, IReadOnlyDictionary<ulong, string> names)
        {
            var found = new List<Belt>();
            foreach ((string prefix, bool isGun) in new[] { ("ggindicator", true), ("mgindicator", false) })
            {
                for (int i = 0; i < GaugeCluster.HardpointRingSize; i++)
                {
                    if (FindNamed(gauges, prefix + i) is { } node && Skin.For(node, names) is { } skin)
                    {
                        found.Add(new Belt(skin, isGun, i));
                    }
                }
            }
            return found;
        }

        public void Apply(GaugeCluster gauges)
        {
            int tier = gauges.BeltTier(IsGun, Position);
            Skin.Retexture(IsHilite, gauges.BeltHiliteTexture(tier));
            Skin.Retexture(t => !IsHilite(t), gauges.BeltLightTexture(tier));
        }
    }

    /// <summary>One zone of the damage display: its border bar and its hatch fill take the zone's
    /// four-way colour tier, and the post-hit blink hides the pair on its dark half.</summary>
    private sealed class DamageZoneSkin
    {
        private const string ZoneSuffix = "damage";

        private DamageZoneSkin(Skin skin, Node3D node, string part)
        {
            Skin = skin;
            Node = node;
            Part = part;
        }

        private Skin Skin { get; }

        private Node3D Node { get; }

        private string Part { get; }

        public static List<DamageZoneSkin> FindAll(Node3D gauges, IReadOnlyDictionary<ulong, string> names)
        {
            var found = new List<DamageZoneSkin>();
            if (FindNamed(gauges, "damageindicator") is { } dial)
            {
                Collect(dial, names, found);
            }
            return found;
        }

        public void Apply(GaugeCluster gauges)
        {
            int tier = gauges.ZoneTier(Part);
            // Negative is the blink's dark half, which the screen-space dial draws by skipping the
            // zone outright, the authored geometry has no dark variant to swap to.
            Show(Node, tier >= 0);
            if (tier < 0)
            {
                return;
            }
            Skin.Retexture(IsHilite, gauges.ZoneHiliteTexture(tier));
            Skin.Retexture(t => !IsHilite(t), gauges.ZoneHatchTexture(tier));
        }

        // ⚠ The zone's PART name is the node's minus the suffix (nosedamage → nose), which is the
        // key the cluster's zones carry. Keying on the node name matches nothing and reads as a
        // permanently green dial.
        private static void Collect(Node3D node, IReadOnlyDictionary<ulong, string> names,
            List<DamageZoneSkin> into)
        {
            string name = AnimRuntime.NameOf(node);
            if (name.EndsWith(ZoneSuffix, StringComparison.OrdinalIgnoreCase)
                && name.Length > ZoneSuffix.Length
                && Skin.For(node, names) is { } skin)
            {
                into.Add(new DamageZoneSkin(skin, node, name[..^ZoneSuffix.Length]));
                return;
            }
            foreach (var child in node.GetChildren())
            {
                if (child is Node3D n3d)
                {
                    Collect(n3d, names, into);
                }
            }
        }

    }

}
