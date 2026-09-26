using System;
using System.Collections.Generic;
using CSVM.Mech3;
using CSVM.UI.Boards;
using CSVM.UI.Screens;
using CSVM.Utils;
using Godot;

namespace CSVM.UI.Overlays;

/// <summary>One AI aircraft's live link to the net node it is flying at: where the plane is,
/// where that node is, and which net owns it. A plain value so the overlay stays ignorant of
/// aircraft: the session fills these in from each pilot's own follower state.
/// <paramref name="Steering"/> separates flying at that node right now from merely holding it
/// while pursuing or evading; a held leash draws dimmed.</summary>
public readonly record struct AiNetLeash(Vector3 From, Vector3 To, int NetId, bool Steering);

/// <summary>
/// The AI patrol-net overlay (key F13, flag <c>--debug-ainets</c>): draws the chapter's
/// <c>ne0NNNNN</c> waypoint graphs (docs/formats/ai-nets.md), data the original never renders.
/// Every net gets one stable id-derived colour; nodes get markers; a HUD text field narrows the
/// drawn set live by name prefix. While up it also draws live leashes: one line per AI aircraft
/// from the plane to the node its follower is flying at. Leash sourcing and the anchored-net
/// offset: this module's entry in docs/architecture.md.
/// ⚠ Draw edges as individual segments off the explicit edge list, never as a closed polygon or
/// a node-order polyline: the graph branches, and assuming a loop draws fiction.
/// </summary>
public sealed partial class AiNetsOverlay : Node
{
    private const float NodeRadius = 6f;
    private const float TaggedNodeRadius = 12f;
    private const float LeashTick = 20f;   // the vertical mark at the plane end, metres
    private const float HeldLeashDim = 0.6f; // how far a merely-held leash darkens off its net colour

    private readonly string _chapterZrdrPath;
    private readonly string _chapter;

    private readonly List<AiNetLeash> _leashes = new();
    private readonly HashSet<int> _drawnIds = new();
    private readonly List<(AiNet Net, Node3D Root)> _anchored = new();

    private List<AiNet>? _nets;
    private List<AiNet>? _cliSelected;
    private Node3D? _holder;
    private bool _shown, _debugDone;
    private CanvasLayer? _hudLayer;
    private Label? _hud;
    private LineEdit? _filterEdit;
    private string _summary = "";
    private ImmediateMesh? _leashMesh;
    private int _leashCount = -1;

    public AiNetsOverlay(string chapterZrdrPath, string chapter)
    {
        _chapterZrdrPath = chapterZrdrPath;
        _chapter = chapter;
        Name = "ainets_overlay";
    }

    /// <summary><c>--debug-ainets</c>: open the overlay on the first frame, the scripted
    /// stand-in for the F13 press.</summary>
    public bool DebugShow { get; init; }

    /// <summary>Comma-separated net names to build (<c>--debug-ainets=&lt;name,…&gt;</c>);
    /// empty = every net. Matching is exact, case-insensitive, the <c>--node=</c> manner:
    /// a miss logs the candidates containing the token rather than failing silently.</summary>
    public string Filter { get; init; } = "";

    /// <summary>Fills the caller's list with one entry per AI aircraft currently flying a net:
    /// where it is, the node it is flying at, and which net that node belongs to. Supplied by the
    /// session (the overlay knows nothing about aircraft) and called once per frame while the
    /// overlay is up. Null draws no leashes, which is every session that has no AI.</summary>
    public Action<List<AiNetLeash>>? CollectLeashes { get; init; }

    /// <summary>How far this net's nodes sit from their authored coordinates right now: the
    /// trailer offset an anchored net rides its target by (supplied by the session from
    /// <c>NetTrailerTargets</c>). The drawn graph is moved by it every frame, so the ring on screen
    /// is the ring the AI is flying rather than the one in the file. Null draws every net at its
    /// authored coordinates, which is also what an unanchored or unresolved net gets.</summary>
    public Func<AiNet, Vector3>? TrailerOffsetOf { get; init; }

    public override void _Process(double delta)
    {
        if (DebugShow && !_debugDone)
        {
            // Deferred one frame like every other --debug-* opener: the world subtree is only
            // final once the session has finished building.
            _debugDone = true;
            Toggle();
        }
        if (!_shown)
        {
            return;
        }
        // The anchored graphs ride their targets: the whole net is one Node3D of authored-space
        // children, so the offset is a translation of that root and nothing has to be rebuilt.
        foreach (var (net, root) in _anchored)
        {
            root.Position = TrailerOffsetOf!(net);
        }
        if (_leashMesh != null)
        {
            RedrawLeashes();
        }
    }

    public override void _UnhandledKeyInput(InputEvent @event)
    {
        if (@event is not InputEventKey { Pressed: true, Echo: false, Keycode: Key.F13 })
        {
            return;
        }
        Toggle();
        GetViewport().SetInputAsHandled();
    }

    /// <summary>F13: show or hide the chapter's patrol nets. The nets are loaded once on
    /// first use (the reader is pure file I/O; a session that never opens the overlay never
    /// pays for it) and the geometry is rebuilt per show, it is static data, but rebuild
    /// keeps the filter and a future live-reload honest.</summary>
    public void Toggle()
    {
        if (_shown)
        {
            _holder?.QueueFree();
            _holder = null;
            _leashMesh = null;   // it belonged to the freed holder; _Process must not touch it
            _anchored.Clear();   // ditto: those roots are the freed holder's children
            _shown = false;
            HideNotice();
            Log.Info("world", $"ai nets overlay off");
            return;
        }
        _nets ??= LoadAndCensus();
        if (_nets.Count == 0)
        {
            Log.Warn("world", $"ai nets overlay: {_chapter} ships no ne0* patrol nets ({_chapterZrdrPath})");
            ShowNotice($"NO PATROL NETS IN {_chapter}");
            _shown = true; // the notice is the overlay; F13 again clears it
            return;
        }
        Build(Visible());
        _shown = true;
        ShowNotice(_summary);
    }

    // Golden-ratio hue spacing: consecutive ids land far apart, and the colour is a pure
    // function of the id, the same net reads the same colour in every session.
    private static Color ColorOf(int id) =>
        Color.FromHsv(id * 0.618034f % 1f, 0.8f, 1f);

    private static Node3D BuildNet(AiNet net, Color color)
    {
        var root = new Node3D { Name = $"net_{net.Id}_{net.Name}" };
        var material = new StandardMaterial3D
        {
            AlbedoColor = color,
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
        };

        // Edges: individual segments, straight off the edge list.
        var lines = new SurfaceTool();
        lines.Begin(Mesh.PrimitiveType.Lines);
        foreach (var (a, b) in net.Edges)
        {
            if (a < 0 || b < 0 || a >= net.Nodes.Count || b >= net.Nodes.Count)
            {
                continue; // unseen in this install; never draw a fictitious segment
            }
            lines.AddVertex(net.Nodes[a].Position);
            lines.AddVertex(net.Nodes[b].Position);
        }
        lines.SetMaterial(material);
        var edgeMesh = new ArrayMesh();
        lines.Commit(edgeMesh);
        root.AddChild(new MeshInstance3D
        {
            Name = "edges",
            Mesh = edgeMesh,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        });

        // Node markers; a tagged node (raw undecoded extras) draws bigger.
        var plain = new SphereMesh { Radius = NodeRadius, Height = NodeRadius * 2f, Material = material };
        var tagged = new SphereMesh { Radius = TaggedNodeRadius, Height = TaggedNodeRadius * 2f, Material = material };
        for (int i = 0; i < net.Nodes.Count; i++)
        {
            root.AddChild(new MeshInstance3D
            {
                Name = $"node{i}",
                Mesh = net.Nodes[i].Tags.Count > 0 ? tagged : plain,
                Position = net.Nodes[i].Position,
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            });
        }

        // One fixed-size billboarded label per net at its first node, the name is the join
        // key aiv/egen/zeppelins use, so it goes on screen verbatim, trailer appended.
        string text = $"{net.Name}#{net.Id}"
            + (net.Trailer is { } t ? $" → {t.Name ?? $"node{t.NodeIndex}"}" : "");
        if (net.Nodes.Count > 0)
        {
            root.AddChild(new Label3D
            {
                Text = text,
                Position = net.Nodes[0].Position + new Vector3(0f, TaggedNodeRadius * 2f, 0f),
                Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
                FixedSize = true,
                PixelSize = 0.0007f,
                FontSize = 40,
                OutlineSize = 12,
                Modulate = color,
            });
        }
        return root;
    }

    // The census: one greppable line per net beside a scripted run's screenshot.
    private List<AiNet> LoadAndCensus()
    {
        List<AiNet> nets;
        try
        {
            nets = AiNets.Load(_chapterZrdrPath);
        }
        catch (Exception e) when (e is System.IO.IOException or System.IO.InvalidDataException)
        {
            Log.Warn("world", $"ai nets overlay: cannot read {_chapterZrdrPath}: {e.Message}");
            return new List<AiNet>();
        }
        Log.Info("world", $"ai nets: {_chapter} ships {nets.Count} patrol net(s)");
        foreach (var net in nets)
        {
            string trailer = net.Trailer is { } t
                ? $" trailer={(t.Name ?? "?")}{(t.NodeIndex >= 0 ? $"@node{t.NodeIndex}" : "")}"
                : "";
            Log.Info("world", $"ai nets: #{net.Id} '{net.Name}' nodes={net.Nodes.Count} edges={net.Edges.Count}{trailer}");
        }
        return nets;
    }

    private List<AiNet> Selected(List<AiNet> nets)
    {
        if (Filter.Length == 0)
        {
            return nets;
        }
        var picked = new List<AiNet>();
        foreach (var token in Filter.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var hit = nets.Find(n => string.Equals(n.Name, token, StringComparison.OrdinalIgnoreCase));
            if (hit != null)
            {
                if (!picked.Contains(hit))
                {
                    picked.Add(hit);
                }
                continue;
            }
            var near = nets.FindAll(n => n.Name.Contains(token, StringComparison.OrdinalIgnoreCase))
                .ConvertAll(n => n.Name);
            if (near.Count > 0)
            {
                Log.Warn("world", $"--debug-ainets: no net named '{token}' in {_chapter}; candidates containing it: {string.Join(", ", near)}");
            }
            else
            {
                Log.Warn("world", $"--debug-ainets: no net named '{token}' in {_chapter}, the census lines above list all {nets.Count}");
            }
        }
        return picked;
    }

    // The CLI-selected set, narrowed by the HUD field's live name prefix. The CLI selection
    // is resolved once (its miss warnings must not repeat per keystroke).
    private List<AiNet> Visible()
    {
        var nets = _cliSelected ??= Selected(_nets!);
        string prefix = _filterEdit?.Text.Trim() ?? "";
        if (prefix.Length > 0)
        {
            nets = nets.FindAll(n => n.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        }
        return nets;
    }

    // Live refilter while the overlay is up: rebuild the drawn set, keep the HUD in step.
    private void Refilter()
    {
        if (!_shown || _nets == null || _nets.Count == 0)
        {
            return;
        }
        _holder?.QueueFree();
        _holder = null;
        _leashMesh = null;
        _anchored.Clear();
        Build(Visible());
        ShowNotice(_summary);
    }

    // One live line per patrolling aircraft, rebuilt each frame: the plane, a short vertical tick
    // at that end, and the node its follower is actually flying at. Drawn in the net's own colour
    // so a leash reads as belonging to the graph it points into, and depth-tested like the graph
    // for the same reason (an x-ray line lies about where the route threads terrain).
    private void RedrawLeashes()
    {
        var mesh = _leashMesh!;
        mesh.ClearSurfaces();
        _leashes.Clear();
        CollectLeashes?.Invoke(_leashes);
        int drawn = 0, steering = 0;
        mesh.SurfaceBegin(Mesh.PrimitiveType.Lines);
        foreach (var leash in _leashes)
        {
            if (_drawnIds.Count > 0 && !_drawnIds.Contains(leash.NetId))
            {
                continue; // its net is filtered out of the drawn set
            }
            var color = ColorOf(leash.NetId);
            if (leash.Steering)
            {
                steering++;
            }
            else
            {
                color = color.Darkened(HeldLeashDim);   // holds the node, is not flying at it
            }
            mesh.SurfaceSetColor(color);
            mesh.SurfaceAddVertex(leash.From);
            mesh.SurfaceSetColor(color);
            mesh.SurfaceAddVertex(leash.To);
            mesh.SurfaceSetColor(color);
            mesh.SurfaceAddVertex(leash.From);
            mesh.SurfaceSetColor(color);
            mesh.SurfaceAddVertex(leash.From + Vector3.Up * LeashTick);
            drawn++;
        }
        mesh.SurfaceEnd();
        int key = (drawn << 8) | steering;
        if (key != _leashCount && _hud != null)
        {
            _leashCount = key;
            _hud.Text = _summary + (drawn > 0
                ? $", {steering} plane(s) flying it, {drawn - steering} holding a node"
                : ", no plane on a net");
        }
    }

    private void Build(List<AiNet> nets)
    {
        _holder = new Node3D { Name = "ainets_draw" };
        _holder.SetMeta(SelectionService.OverlayMeta, true);
        int nodes = 0, edges = 0;
        _drawnIds.Clear();
        _anchored.Clear();
        foreach (var net in nets)
        {
            var color = ColorOf(net.Id);
            var root = BuildNet(net, color);
            _holder.AddChild(root);
            if (TrailerOffsetOf != null && net.Trailer is { NodeIndex: >= 0, Name: { Length: > 0 } })
            {
                _anchored.Add((net, root));
            }
            _drawnIds.Add(net.Id);
            nodes += net.Nodes.Count;
            edges += net.Edges.Count;
        }

        // The live leash layer: one ImmediateMesh rewritten per frame, vertex-coloured so all of
        // them share one surface however many nets are on screen.
        _leashMesh = new ImmediateMesh();
        _leashCount = -1;
        _holder.AddChild(new MeshInstance3D
        {
            Name = "leashes",
            Mesh = _leashMesh,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            MaterialOverride = new StandardMaterial3D
            {
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                VertexColorUseAsAlbedo = true,
            },
        });
        AddChild(_holder);
        int skipped = (_nets?.Count ?? 0) - nets.Count;
        _summary = $"ai nets: {nets.Count} net(s), {nodes} nodes, {edges} edges"
            + (skipped > 0 ? $" ({skipped} filtered out)" : "");
        Log.Info("world", $"{_summary}");
    }

    private void ShowNotice(string text)
    {
        if (_hudLayer == null)
        {
            _hudLayer = new CanvasLayer { Layer = HudLayers.Debug };
            var root = new Control { MouseFilter = Control.MouseFilterEnum.Ignore };
            root.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            _hud = new Label
            {
                // Below the class overlay's readout (H), which sits at y=200..220.
                Position = new Vector2(12, 260),
                Modulate = new Color(0.7f, 1f, 0.8f),
            };
            _hud.AddThemeFontSizeOverride("font_size", 13);
            root.AddChild(_hud);
            // The flight-panel focus rule (PanelFocus) is deliberately excepted here: a text
            // filter cannot work unfocusable. Focus only ever arrives by an explicit click on
            // the field, and Enter hands the keyboard straight back to the aircraft.
            _filterEdit = new LineEdit
            {
                PlaceholderText = "filter: name starts with…",
                Position = new Vector2(12, 282),
                Size = new Vector2(230, 28),
            };
            _filterEdit.AddThemeFontSizeOverride("font_size", 13);
            _filterEdit.TextChanged += _ => Refilter();
            _filterEdit.TextSubmitted += _ => _filterEdit!.ReleaseFocus();
            root.AddChild(_filterEdit);
            _hudLayer.AddChild(root);
            AddChild(_hudLayer);
        }
        _hud!.Text = text;
        _hudLayer.Visible = true;
    }

    private void HideNotice()
    {
        if (_hudLayer != null)
        {
            _filterEdit?.ReleaseFocus();
            _hudLayer.Visible = false;
        }
    }
}
