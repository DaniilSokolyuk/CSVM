using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using CSVM.Mech3;
using CSVM.Utils;
using Godot;

namespace CSVM.Flight.Weapons;

/// <summary>The parsed <c>CSVM/data/stock_loadouts.json</c>, the 11 aircraft's default weapon
/// fit, hand-authored config (see <see href="../../../../docs/formats/loadouts.md">loadouts.md</see>).
/// Pure data: no plane binding, no weapon resolution, <see cref="Loadout.Bind"/> does that
/// against a built plane.</summary>
public sealed class StockLoadouts
{
    // ammo name -> the offset into a caliber's four consecutive wep ids (slug X0 .. magnesium X3).
    private static readonly Dictionary<string, int> AmmoIndex = new(StringComparer.OrdinalIgnoreCase)
    {
        ["slug"] = 0,
        ["dumdum"] = 1,
        ["ap"] = 2,
        ["magnesium"] = 3,
    };

    private readonly Dictionary<string, LoadoutDef> _byDef = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The committed config's default location (res://), independent of <c>--data-root</c>:
    /// it is engine config, not extracted game data. Kept as a res:// path and read through
    /// <see cref="Godot.FileAccess"/>, because in an exported build the file lives in the pck,
    /// where <c>GlobalizePath</c> + System.IO cannot reach it.</summary>
    public static string DefaultPath => "res://data/stock_loadouts.json";

    public IReadOnlyDictionary<string, LoadoutDef> All => _byDef;

    /// <summary>The Ammo Selection screen's dropdown rosters, empty when the file omits them.</summary>
    public LoadoutOptions Options { get; private set; } = new();

    /// <summary>A gun's stock weapon id: caliber N + ammo k → <c>wep_{N+k}</c> (the wep_30..73
    /// player matrix, each caliber's four ammo types consecutive). Stock ammo <c>slug</c> → <c>wep_N</c>.</summary>
    public static string GunWeaponId(int caliber, string ammo) =>
        $"wep_{caliber + (AmmoIndex.TryGetValue(ammo, out var k) ? k : 0)}";

    /// <summary>Loads the stock-loadout file (defaults to <see cref="DefaultPath"/>).</summary>
    public static StockLoadouts Load(string? path = null)
    {
        path ??= DefaultPath;
        var loadouts = new StockLoadouts();
        // res:// lives inside the pck in an exported build, where only Godot's own FileAccess
        // can read it; an explicit disk path (unit tests, tools) stays on System.IO, which the
        // xunit host can run without a Godot runtime.
        bool viaGodot = path.StartsWith("res://", StringComparison.Ordinal);
        if (viaGodot ? !Godot.FileAccess.FileExists(path) : !File.Exists(path))
        {
            Log.Warn("weapons", $"stock loadouts: file not found, no loadouts loaded: {path}");
            return loadouts;
        }
        using var doc = JsonDocument.Parse(viaGodot ? Godot.FileAccess.GetFileAsBytes(path) : File.ReadAllBytes(path));
        if (!doc.RootElement.TryGetProperty("planes", out var planes) || planes.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException($"stock loadouts: no 'planes' object in {path}");
        }
        foreach (var plane in planes.EnumerateObject())
        {
            var body = plane.Value;
            var def = new LoadoutDef
            {
                Def = plane.Name,
                Model = Str(body, "model"),
                Display = Str(body, "display"),
            };
            if (body.TryGetProperty("guns", out var guns) && guns.ValueKind == JsonValueKind.Array)
            {
                foreach (var g in guns.EnumerateArray())
                {
                    var spec = new GunSpec
                    {
                        Slot = Int(g, "slot"),
                        Mount = Str(g, "mount"),
                        Caliber = Int(g, "caliber"),
                        Ammo = Str(g, "ammo"),
                        Turret = g.TryGetProperty("turret", out var t) && t.ValueKind == JsonValueKind.True,
                    };
                    if (g.TryGetProperty("markers", out var markers) && markers.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var m in markers.EnumerateArray())
                        {
                            if (m.GetString() is { } name)
                            {
                                spec.Markers.Add(name);
                            }
                        }
                    }
                    def.Guns.Add(spec);
                }
            }
            if (body.TryGetProperty("hardpoints", out var hp) && hp.ValueKind == JsonValueKind.Object)
            {
                def.Hardpoints = new HardpointSpec { Count = Int(hp, "count"), Stock = Strings(hp, "stock") };
            }
            loadouts._byDef[def.Def] = def;
        }

        if (doc.RootElement.TryGetProperty("selectable", out var selectable)
            && selectable.ValueKind == JsonValueKind.Object)
        {
            loadouts.Options = new LoadoutOptions
            {
                GunAmmo = OptionList(selectable, "gun_ammo"),
                PylonOrdnance = OptionList(selectable, "pylon_ordnance"),
            };
        }
        return loadouts;
    }

    /// <summary>The def's stock loadout, or null when the plane isn't in the file.</summary>
    public LoadoutDef? For(string defName) => _byDef.TryGetValue(defName, out var d) ? d : null;

    /// <summary>The stock loadout of whichever def flies <paramref name="modelNode"/>
    /// (<c>player_pfighter</c>), or null. The menu knows a plane by its model node, not its def
    /// name, and the two differ often enough not to be guessable.</summary>
    public LoadoutDef? ForModel(string modelNode)
    {
        foreach (var def in _byDef.Values)
        {
            if (string.Equals(def.Model, modelNode, StringComparison.OrdinalIgnoreCase))
            {
                return def;
            }
        }
        return null;
    }

    private static string Str(JsonElement e, string key) =>
        e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static string[] Strings(JsonElement e, string key)
    {
        if (!e.TryGetProperty(key, out var value) || value.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<string>();
        }
        var strings = new List<string>();
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                strings.Add(item.GetString() ?? "");
            }
        }
        return strings.ToArray();
    }

    private static IReadOnlyList<LoadoutOption> OptionList(JsonElement e, string key)
    {
        var list = new List<LoadoutOption>();
        if (e.TryGetProperty(key, out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in arr.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Object)
                {
                    list.Add(new LoadoutOption(Str(item, "id"), Str(item, "label")));
                }
            }
        }
        return list;
    }

    private static int Int(JsonElement e, string key) =>
        e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;
}

/// <summary>One plane's stock loadout, as authored, before binding to a model.</summary>
public sealed class LoadoutDef
{
    public string Def = "";       // "pbloodhawk"
    public string Model = "";     // "player_bhawk"
    public string Display = "";
    public List<GunSpec> Guns = new();
    public HardpointSpec? Hardpoints;
}

/// <summary>One authored gun slot (W1–W4).</summary>
public sealed class GunSpec
{
    public int Slot;
    public string Mount = "";
    public int Caliber;
    public string Ammo = "slug";
    public List<string> Markers = new();
    public bool Turret;           // an AI turret slot, parsed but built inert

    /// <summary>The weapon id outright, bypassing <see cref="Caliber"/> + <see cref="Ammo"/>. An AI
    /// def names its gun as a <c>wep_NN</c> and never as a caliber (<see cref="Loadout.BindAi"/>).</summary>
    public string? WeaponId;

    /// <summary>Rounds carried, when the source authors its own count. Null takes the weapon's
    /// <c>CLUSTER_SIZE</c>, which is what the player's stock fit uses.</summary>
    public int? Rounds;
}

/// <summary>The authored hardpoint block: pylon count, one stock ordnance id per pylon, and
/// optionally the rounds each carries (null takes the weapon's <c>CLUSTER_SIZE</c>).</summary>
public sealed class HardpointSpec
{
    public int Count;
    public string[] Stock = Array.Empty<string>();
    public int[]? Rounds;
}

/// <summary>
/// A plane's stock loadout <b>bound to its built model</b>: each gun group's authored markers
/// resolved to live muzzle <see cref="Node3D"/>s and its caliber+ammo resolved to a
/// <see cref="WeaponDef"/>, each hardpoint bound to its pylon node, a live set of gun groups and
/// hardpoints with independent ammo counters, ready for the firing code to draw from.
///
/// <para>Marker resolution is against the built tree's <c>cs_name</c> meta (as
/// <see cref="UI.Overlays.MarkerOverlay"/> reads it). A named marker that is absent is a <b>loud error</b>
///, a thrown exception naming the plane, slot and marker, never a silent skip, since a wrong
/// binding would silently fire a gun from nowhere.</para>
/// </summary>
public sealed class Loadout
{
    /// <summary>What a pylon carries when nothing else names its ordnance: high explosive, the
    /// row the hangar's own commit writes into every cell inside a wing's hardpoint count
    /// (docs/formats/saved-games.md, "Where the ammunition and ordnance picks live").</summary>
    public const string StockOrdnance = "wep_06";

    /// <summary>The original's hardpoint fill order, decoded from its stock loadout table: a fit
    /// of fewer than 8 pylons leaves physical gaps rather than filling pylon1..N contiguously.
    /// <c>hp.Count</c> takes a PREFIX of this sequence. ⚠ Do not read the two interleaved halves
    /// as the wings; the model puts odd pylons to port and even to starboard, so the original
    /// itself hangs a 2-pylon fit port-heavy (docs/formats/loadouts.md).</summary>
    public static readonly int[] PylonFillOrder = { 1, 5, 2, 6, 3, 7, 4, 8 };

    /// <summary>Each wing's physical pylon numbers, outboard to inboard: every player model pairs
    /// its pylons across the centreline, odd to port and even to starboard, with |x| falling as
    /// the number rises (docs/formats/markers.md).</summary>
    public static readonly int[] LeftWingPylons = { 1, 3, 5, 7 };

    /// <summary>The starboard half of <see cref="LeftWingPylons"/>'s split.</summary>
    public static readonly int[] RightWingPylons = { 2, 4, 6, 8 };

    private Loadout(LoadoutDef def, List<GunGroup> guns, List<Hardpoint> hardpoints)
    {
        Def = def;
        Guns = guns;
        Hardpoints = hardpoints;
        PylonStepOrder = StepOrder(hardpoints);
    }

    public LoadoutDef Def { get; }
    public IReadOnlyList<GunGroup> Guns { get; }
    public IReadOnlyList<Hardpoint> Hardpoints { get; }

    /// <summary><see cref="Hardpoints"/> positions sorted by <see cref="Hardpoint.Index"/>, the
    /// sequence the hardpoint selector steps along, handed to <see cref="FireControl"/>.
    /// ⚠ The list itself is in <see cref="PylonFillOrder"/>, which says which pylons a fit occupies,
    /// NOT where they sit along the wing: stepping the list walks the gauge belt 1,5,2,6,… and back.
    /// Those are two separate orders and neither may be respelt as the other.</summary>
    public IReadOnlyList<int> PylonStepOrder { get; }

    /// <summary>The gun groups the player can actually fire (turret slots excluded, built inert).</summary>
    public IEnumerable<GunGroup> FirableGuns
    {
        get
        {
            foreach (var g in Guns)
            {
                if (!g.IsTurret)
                {
                    yield return g;
                }
            }
        }
    }

    /// <summary>The pylons one wing hangs on <paramref name="fit"/>, outboard to inboard: that
    /// wing's physical numbers filtered by what the fit actually carries. Every per-wing bound in
    /// the build reads this one walk, so a wing's cell count and its Nth cell cannot disagree.
    /// ⚠ Never count a fit by its fill-order halves: 1-4 against 5-8 crosses the centreline, and
    /// puts a starboard cell on an airframe hanging both its pylons to port.</summary>
    public static int[] WingPylons(HardpointSpec? fit, bool port)
    {
        var hung = new List<int>(LeftWingPylons.Length);
        foreach (int pylon in port ? LeftWingPylons : RightWingPylons)
        {
            if (Hangs(fit, pylon))
            {
                hung.Add(pylon);
            }
        }

        return hung.ToArray();
    }

    /// <summary>How many pylons each wing hangs on <paramref name="fit"/>, the bound the flight
    /// check, the ammo screen and a fresh hangar build all draw their per-wing cells inside.</summary>
    public static (int Left, int Right) WingCounts(HardpointSpec? fit) =>
        (WingPylons(fit, port: true).Length, WingPylons(fit, port: false).Length);

    /// <summary>The physical pylon a saved record's ordnance cell names on <paramref name="fit"/>:
    /// cells 0-3 are the left wing and 4-7 the right, each naming that wing's own pylons outboard
    /// to inboard and bounded by what the wing carries (docs/formats/saved-games.md). 0, which no
    /// pylon carries, for a cell past them.
    /// ⚠ Resolve against the FIT, never <see cref="LeftWingPylons"/> alone: a four-pylon stock fit
    /// hangs 1 and 5 to port where a two-a-wing build hangs 1 and 3.</summary>
    public static int PylonForCell(int cell, HardpointSpec? fit)
    {
        int half = LeftWingPylons.Length;
        if (cell < 0 || cell >= half + RightWingPylons.Length)
        {
            return 0;
        }

        var hung = WingPylons(fit, port: cell < half);
        int want = cell < half ? cell : cell - half;
        return want < hung.Length ? hung[want] : 0;
    }

    /// <summary>Whether <paramref name="fit"/> hangs <paramref name="pylon"/> at all: its
    /// fill-order entry is inside the count and is not the empty sentinel, which holds an index
    /// open and carries nothing. An Ammo Selection screen asks this before it draws a pylon's
    /// row, since the original draws no field for a hardpoint the aeroplane does not carry
    /// (docs/formats/campaign-screens.md, the ammo screen).</summary>
    public static bool Hangs(HardpointSpec? fit, int pylon)
    {
        for (int i = 0; fit != null && i < fit.Count && i < PylonFillOrder.Length && i < fit.Stock.Length; i++)
        {
            if (PylonFillOrder[i] == pylon)
            {
                return !string.Equals(fit.Stock[i], LoadoutChoice.None, StringComparison.OrdinalIgnoreCase);
            }
        }

        return false;
    }

    /// <summary>Binds an authored loadout to a built plane and the weapon catalogue. Throws if a
    /// named marker is absent on the model or a resolved <c>wep_*</c> id is missing.</summary>
    public static Loadout Bind(LoadoutDef def, Node3D plane, WeaponDefs weapons)
    {
        var markerNodes = CollectMarkers(plane);

        var guns = new List<GunGroup>();
        foreach (var spec in def.Guns)
        {
            var weaponId = spec.WeaponId ?? StockLoadouts.GunWeaponId(spec.Caliber, spec.Ammo);
            var weapon = weapons.Get(weaponId)
                ?? throw new InvalidOperationException(
                    $"loadout {def.Def} slot {spec.Slot} ({spec.Mount}): weapon '{weaponId}' " +
                    $"(caliber {spec.Caliber} + {spec.Ammo}) not in weapons.json");
            var muzzles = new List<Node3D>();
            foreach (var name in spec.Markers)
            {
                muzzles.Add(Resolve(markerNodes, name, def, $"slot {spec.Slot} ({spec.Mount})"));
            }
            int capacity = spec.Rounds ?? weapon.ClusterSize ?? 0;
            guns.Add(new GunGroup
            {
                Slot = spec.Slot,
                Mount = spec.Mount,
                Weapon = weapon,
                Muzzles = muzzles,
                Capacity = capacity,
                Ammo = capacity,
                IsTurret = spec.Turret,
            });
        }

        var hardpoints = new List<Hardpoint>();
        if (def.Hardpoints is { } hp && hp.Count > 0)
        {
            // Pylons bind via PylonFillOrder, not sequentially 1..N: count N takes the
            // sequence's first N entries, so a partial stock fit lands on both wings alternately
            // instead of piling onto one side.
            for (int i = 0; i < hp.Count; i++)
            {
                string stockId = hp.Stock[i];

                // An empty pylon (the screen's "None") builds nothing, but still consumes its
                // fill-order index so the pylons after it stay on the wing they belong to.
                if (string.Equals(stockId, LoadoutChoice.None, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var weapon = weapons.Get(stockId)
                    ?? throw new InvalidOperationException(
                        $"loadout {def.Def}: hardpoint stock {i + 1} '{stockId}' not in weapons.json");
                int perPylon = hp.Rounds is { } rounds && i < rounds.Length
                    ? rounds[i]
                    : weapon.ClusterSize ?? 0;
                int pylonNumber = PylonFillOrder[i];
                var pylon = Resolve(markerNodes, $"pylon{pylonNumber}", def, "hardpoint");
                hardpoints.Add(new Hardpoint
                {
                    Index = pylonNumber,
                    Pylon = pylon,
                    Weapon = weapon,
                    Capacity = perPylon,
                    Ammo = perPylon,
                });
            }
        }

        return new Loadout(def, guns, hardpoints);
    }

    /// <summary>An AI aircraft's fit, built from its vehicle def's own <c>weapons</c> tuples
    /// (docs/org/aiPilot/aiWeapons.md) instead of <c>stock_loadouts.json</c>, which holds the eleven
    /// player defs alone. Guns and ordnance ride one authored list and are told apart by the weapon
    /// def's own <c>CANNON</c> flag, as the original tells them apart. Throws like
    /// <see cref="Bind"/> does; an unknown weapon id is a loud error, never a silent skip.</summary>
    public static Loadout BindAi(IReadOnlyList<AiWeaponSlot> slots, string defName, Node3D plane,
        WeaponDefs weapons)
    {
        var markerNodes = CollectMarkers(plane);
        var firepoints = new SortedSet<int>();
        var pylons = new SortedSet<int>();
        foreach (var name in markerNodes.Keys)
            if (MarkerRig.Classify(name, out var kind, out int ord))
            {
                if (kind == MarkerRig.MarkerKind.Firepoint)
                    firepoints.Add(ord);
                else if (kind == MarkerRig.MarkerKind.Pylon)
                    pylons.Add(ord);
            }

        var def = new LoadoutDef { Def = defName, Display = defName };
        var stock = new List<string>();
        var rounds = new List<int>();
        var gunSlots = new List<AiWeaponSlot>();     // parallel to def.Guns
        var pylonSlots = new List<AiWeaponSlot>();   // parallel to stock
        int gunSlot = 0;
        foreach (var slot in slots)
        {
            var weapon = weapons.Get(slot.WeaponId)
                ?? throw new InvalidOperationException(
                    $"ai loadout {defName}: weapon '{slot.WeaponId}' not in weapons.json");
            if (weapon.IsGun)
            {
                // One group over every firepoint the rig carries: the original's AI fires its
                // airframe's gun mount, and the tuple counts rounds for the weapon, not per barrel.
                var markers = new List<string>();
                foreach (int ord in firepoints)
                    markers.Add($"firepoint{ord}");
                if (markers.Count == 0)
                    continue; // an airframe with no firepoints carries no gun to bind
                def.Guns.Add(new GunSpec
                {
                    Slot = ++gunSlot,
                    Mount = $"Gun Group {gunSlot}",
                    WeaponId = slot.WeaponId,
                    Markers = markers,
                    Rounds = slot.Rounds,
                });
                gunSlots.Add(slot);
            }
            else if (stock.Count < pylons.Count && stock.Count < PylonFillOrder.Length)
            {
                // One pylon per authored ordnance entry, carrying that entry's whole count: the
                // original counts rounds per weapon slot and has no pylons at all. A def with more
                // entries than the airframe has pylons drops the overflow rather than stacking.
                stock.Add(slot.WeaponId);
                rounds.Add(slot.Rounds);
                pylonSlots.Add(slot);
            }
        }

        if (stock.Count > 0)
            def.Hardpoints = new HardpointSpec
            {
                Count = stock.Count,
                Stock = stock.ToArray(),
                Rounds = rounds.ToArray(),
            };

        var loadout = Bind(def, plane, weapons);

        // The authored window and interval ride along on the bound slots: the original keeps both
        // per weapon slot, and the AI gates read them from there rather than from a vehicle default.
        for (int i = 0; i < loadout.Guns.Count && i < gunSlots.Count; i++)
            Carry(loadout.Guns[i], gunSlots[i]);
        for (int i = 0; i < loadout.Hardpoints.Count && i < pylonSlots.Count; i++)
        {
            loadout.Hardpoints[i].MinRangeM = pylonSlots[i].MinRangeM;
            loadout.Hardpoints[i].MaxRangeM = pylonSlots[i].MaxRangeM;
            loadout.Hardpoints[i].RefireSeconds = pylonSlots[i].RefireSeconds;
        }
        return loadout;

        static void Carry(GunGroup group, AiWeaponSlot slot)
        {
            group.MinRangeM = slot.MinRangeM;
            group.MaxRangeM = slot.MaxRangeM;
            group.RefireSeconds = slot.RefireSeconds;
        }
    }

    /// <summary>Synthesizes a lab loadout covering the airframe's whole marker rig: all four
    /// gun-group slots (docs/formats/markers.md, "Slot to firepoint binding") and one hardpoint
    /// per <c>pylonN</c>, regardless of how few the stock fit binds. A slot the stock fit names
    /// keeps its weapon; one it does not defaults to the stock's first gun weapon.
    /// ⚠ Every synthesized group is fireable, even a stock turret slot, a deliberate lab-only
    /// difference. Binds through the same <see cref="Bind"/> every other loadout uses.</summary>
    public static Loadout ForRig(Node3D plane, WeaponDefs weapons, LoadoutDef? stock)
    {
        var markerNodes = CollectMarkers(plane);
        var firepoints = new SortedSet<int>();
        var pylons = new SortedSet<int>();
        foreach (var name in markerNodes.Keys)
        {
            if (MarkerRig.Classify(name, out var kind, out int ord))
            {
                if (kind == MarkerRig.MarkerKind.Firepoint)
                {
                    firepoints.Add(ord);
                }
                else if (kind == MarkerRig.MarkerKind.Pylon)
                {
                    pylons.Add(ord);
                }
            }
        }

        GunSpec? firstStockGun = stock != null && stock.Guns.Count > 0 ? stock.Guns[0] : null;

        var def = new LoadoutDef
        {
            Def = stock?.Def ?? "",
            Model = stock?.Model ?? "",
            Display = stock?.Display ?? "",
        };

        for (int slot = 1; slot <= 4; slot++)
        {
            int lo = 9 - 2 * slot;   // slot1->7, slot2->5, slot3->3, slot4->1
            int hi = 10 - 2 * slot;  // slot1->8, slot2->6, slot3->4, slot4->2
            var markers = new List<string>();
            if (firepoints.Contains(lo))
            {
                markers.Add($"firepoint{lo}");
            }
            if (firepoints.Contains(hi))
            {
                markers.Add($"firepoint{hi}");
            }
            if (markers.Count == 0)
            {
                // Neither half of this slot's pair exists on the rig, nothing to synthesize.
                continue;
            }
            GunSpec? stockSpec = null;
            foreach (var g in stock?.Guns ?? new List<GunSpec>())
            {
                if (g.Slot == slot)
                {
                    stockSpec = g;
                    break;
                }
            }
            def.Guns.Add(new GunSpec
            {
                Slot = slot,
                Mount = stockSpec?.Mount ?? $"Gun Group {slot}",
                Caliber = stockSpec?.Caliber ?? firstStockGun?.Caliber ?? 30,
                Ammo = stockSpec?.Ammo ?? firstStockGun?.Ammo ?? "slug",
                Markers = markers,
                Turret = false,
            });
        }

        if (pylons.Count > 0)
        {
            string stockId = stock?.Hardpoints?.Stock is { Length: > 0 } s ? s[0] : StockOrdnance;
            var hardpointStock = new string[pylons.Count];
            for (int i = 0; i < hardpointStock.Length; i++)
            {
                hardpointStock[i] = stockId;
            }
            def.Hardpoints = new HardpointSpec
            {
                Count = pylons.Count,
                Stock = hardpointStock,
            };
        }

        return Bind(def, plane, weapons);
    }

    // The hardpoint list read in physical mount order: list positions sorted by pylon number.
    private static int[] StepOrder(List<Hardpoint> hardpoints)
    {
        var order = new int[hardpoints.Count];
        for (int i = 0; i < order.Length; i++)
        {
            order[i] = i;
        }
        Array.Sort(order, (a, b) => hardpoints[a].Index.CompareTo(hardpoints[b].Index));
        return order;
    }

    private static Node3D Resolve(Dictionary<string, Node3D> markers, string name, LoadoutDef def, string where)
    {
        if (markers.TryGetValue(name, out var node))
        {
            return node;
        }
        throw new InvalidOperationException(
            $"loadout {def.Def} ({def.Model}) {where}: marker '{name}' not found on the built plane");
    }

    // Builds a `cs_name → Node3D` map of the plane's marker nodes (firepoints,
    // pylons, target) from the built tree, the same `cs_name` meta SceneBuilder stamps.
    private static Dictionary<string, Node3D> CollectMarkers(Node3D plane)
    {
        var map = new Dictionary<string, Node3D>(StringComparer.OrdinalIgnoreCase);
        void Walk(Node node)
        {
            foreach (var child in node.GetChildren())
            {
                if (child is Node3D n3d && n3d.HasMeta(AnimRuntime.NameMeta)
                    && MarkerRig.Classify(n3d.GetMeta(AnimRuntime.NameMeta).AsString(), out _, out _))
                {
                    // First-wins: marker names are unique per plane.
                    map.TryAdd(n3d.GetMeta(AnimRuntime.NameMeta).AsString(), n3d);
                }
                Walk(child);
            }
        }
        Walk(plane);
        return map;
    }
}

/// <summary>One live gun group: its resolved weapon, muzzle nodes, and an <b>independent</b> ammo
/// counter (the Balmoral's two .50 groups each carry their own, playtest-confirmed). Turret
/// groups are bound but inert (<see cref="IsTurret"/>). The <see cref="IGunSlot"/> face is
/// what <see cref="FireControl"/> fires through, the node-free slice of this class.</summary>
public sealed class GunGroup : IGunSlot
{
    public int Slot;
    public string Mount = "";
    public IReadOnlyList<Node3D> Muzzles = Array.Empty<Node3D>();
    public bool IsTurret;

    /// <summary>The engagement window and refire interval this slot's AI def authored, metres and
    /// seconds; all three 0 on a fit from <c>stock_loadouts.json</c>, which authors none and leaves
    /// the AI gates on their own defaults (<see cref="Loadout.BindAi"/>).</summary>
    public float MinRangeM;

    public float MaxRangeM;

    public float RefireSeconds;

    public WeaponDef Weapon { get; set; } = null!;

    public int Capacity { get; set; }  // CLUSTER_SIZE, the full per-group load

    public int Ammo { get; set; }      // mutable remaining rounds

    public int MuzzleCount => Muzzles.Count;
}

/// <summary>One live hardpoint (pylon): its resolved ordnance weapon and a per-pylon ammo counter
/// (rocket capacity = pylon count × CLUSTER_SIZE, per pylon). The <see cref="IPylonSlot"/>
/// face is what <see cref="FireControl"/> launches through, the node-free slice of this class.</summary>
public sealed class Hardpoint : IPylonSlot
{
    public int Index;             // pylon number, 1-based
    public Node3D Pylon = null!;

    /// <summary>The engagement window and refire interval this pylon's AI def authored, metres and
    /// seconds; all three 0 on a player fit (<see cref="Loadout.BindAi"/>). The original keeps them
    /// per weapon slot, and one pylon per authored entry is our nearest equivalent.</summary>
    public float MinRangeM;

    public float MaxRangeM;

    public float RefireSeconds;

    public WeaponDef Weapon { get; set; } = null!;

    public int Capacity { get; set; }  // CLUSTER_SIZE per pylon

    public int Ammo { get; set; }
}
