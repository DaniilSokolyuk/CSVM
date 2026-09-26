using System.Collections.Generic;
using CSVM.Flight.Airframe;
using CSVM.Mech3;
using Godot;

namespace CSVM.Session.Objectives;

/// <summary>The per-object half of the original's zone gate. Each frame a drawn object earns a
/// zone from its own vertical span against the cloud band's midpoint
/// (<see cref="WeatherState.ObjectZone"/>). Its meshes then move onto that zone's visual layer, so
/// a camera whose <see cref="CSVM.Mech3.ZoneGate"/> state sits on the band's far side does not
/// draw it. Aircraft, zeppelins and whatever effect meshes hang under them come through here. The
/// authored world is stamped once by <c>SceneBuilder</c> off its gamez <c>zone_id</c> instead.
/// Owned and ticked by <see cref="World.WeatherRig"/>; decode:
/// docs/formats/weather/atmosphere.md.</summary>
public sealed class ObjectZoneGate
{
    // The layer every shared world mesh is built on. A mesh already wearing a layer of its own is
    // left alone, because a cull mask ORs its bits. An instance cannot be on the zone band and on
    // the own-airframe hide at once, and those other layers are camera-local anyway.
    private const uint DefaultLayer = 1u;

    // One entry per registered root, keyed by its instance id: the meshes this gate may move and
    // their merged extent in root space. The extent is measured once because an airframe is rigid.
    // Per frame it would cost a GetAabb per mesh per object, for a span that never moves.
    private readonly Dictionary<ulong, Entry> _entries = new();
    private readonly List<ulong> _stale = new();

    /// <summary>How many of the objects the last <see cref="Tick"/> saw sit wholly on one side of
    /// the band, and so draw for one camera state only. The count a suite reads the gate's verdict
    /// off.</summary>
    public int GatedObjects { get; private set; }

    /// <summary>Assigns every object in <paramref name="objects"/> its zone layer for this frame,
    /// off its own extent, the mission's band and the chapter's fog volumes. When
    /// <paramref name="open"/> is set each one is restored to the shared world layer, which is
    /// what <c>--no-fog</c> and <c>--no-zone-cull</c> pass.</summary>
    public void Tick(IReadOnlyList<Node3D> objects, WeatherState? weather, bool fogZoneArmed,
        IReadOnlyList<FogVolumeBox> volumes, bool open)
    {
        GatedObjects = 0;
        foreach (var root in objects)
        {
            if (!GodotObject.IsInstanceValid(root) || !root.IsInsideTree())
                continue;
            var entry = EntryFor(root);
            if (entry.Meshes.Count == 0)
                continue;
            int zone = -1;
            if (!open && weather != null)
                zone = weather.ObjectZone(root.GlobalTransform * entry.Extent, fogZoneArmed, volumes);
            uint layer = ZoneGate.LayerFor(zone);
            if (layer != 0)
                GatedObjects++;
            foreach (var mesh in entry.Meshes)
                mesh.Layers = layer != 0 ? layer : DefaultLayer;
        }
        Sweep(objects);
    }

    // Objects come and go: an aircraft is shot down, a chapter unloads. An entry whose root is no
    // longer offered is dropped rather than held by its instance id for the session's life.
    private void Sweep(IReadOnlyList<Node3D> objects)
    {
        if (_entries.Count <= objects.Count)
            return;
        var live = new HashSet<ulong>();
        foreach (var root in objects)
            if (GodotObject.IsInstanceValid(root))
                live.Add(root.GetInstanceId());
        _stale.Clear();
        foreach (var id in _entries.Keys)
            if (!live.Contains(id))
                _stale.Add(id);
        foreach (var id in _stale)
            _entries.Remove(id);
    }

    private Entry EntryFor(Node3D root)
    {
        ulong id = root.GetInstanceId();
        if (_entries.TryGetValue(id, out var entry) && entry.IsLive())
            return entry;
        entry = Entry.Measure(root);
        _entries[id] = entry;
        return entry;
    }

    private sealed class Entry
    {
        private Entry(List<VisualInstance3D> meshes, Aabb extent)
        {
            Meshes = meshes;
            Extent = extent;
        }

        public List<VisualInstance3D> Meshes { get; }

        public Aabb Extent { get; }

        public static Entry Measure(Node3D root)
        {
            var meshes = new List<VisualInstance3D>();
            Collect(root, meshes);
            var toRoot = root.GlobalTransform.AffineInverse();
            var extent = new Aabb();
            bool any = false;
            foreach (var mesh in meshes)
            {
                var local = (toRoot * mesh.GlobalTransform) * mesh.GetAabb();
                extent = any ? extent.Merge(local) : local;
                any = true;
            }
            return new Entry(meshes, extent);
        }

        // An entry whose meshes are gone (a destroyed airframe reusing its root) is re-measured.
        // So is one that found nothing, since a root can still be filling in when first seen.
        public bool IsLive()
        {
            if (Meshes.Count == 0)
                return false;
            foreach (var mesh in Meshes)
                if (!GodotObject.IsInstanceValid(mesh))
                    return false;
            return true;
        }

        private static void Collect(Node node, List<VisualInstance3D> into)
        {
            if (node is VisualInstance3D instance && instance.Layers == DefaultLayer)
                into.Add(instance);
            foreach (var child in node.GetChildren())
                Collect(child, into);
        }
    }
}
