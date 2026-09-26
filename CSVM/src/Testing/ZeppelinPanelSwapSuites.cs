using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using CSVM.Mech3;
using Godot;

namespace CSVM.Testing;

/// <summary>The pirate zeppelin's gasbag burn, read as geometry rather than as a pixel. A burning
/// section fades its hull-skin panels out and its burnt-skin panels in. The gasbag envelope and
/// the truss are switched on to stand behind the opening. Each of those meshes carries its own
/// authored sidedness. This reads the built surfaces back and pins the cull mode every one takes
/// against the flag its polygons ship.</summary>
internal static class ZeppelinPanelSwapSuites
{
    private const string Chapter = "C2B";
    private const string Mission = "M04";
    private const string Hull = "piratezep";
    private const string Section = "gasbag1";
    private const string BurnAnim = "pzepleft_gasbag1";
    private const float Tick = 1f / 30f;

    // The left side of gasbag1: the hull-skin panels the burn fades out, and the burnt-skin panels
    // it fades in. The list also holds what the swap uncovers and the right-side pair the authored
    // crossing reaches.
    private static readonly string[] Watched =
    {
        "panelleft1", "panelleft2", "panelleft3", "panelleftb1", "panelleftb2",
        "burnpl1", "burnpl2", "burnpl3", "burnplb1", "burnplb2",
        "gasbagleft", "structureleft", "panelright3", "burnpr3", "gasbagright",
    };

    [Suite("zeppelin-panel-swap",
        "the pirate zeppelin's gasbag1 burn over C2B/M04's own world: each hull-skin panel fades " +
        "out as a burnt-skin twin fades in, and every mesh on both sides of that swap takes the " +
        "cull mode its own polygons ask for. The hull skin ships single-sided and builds " +
        "cull_front, the burnt panels ship show_backface and build cull_disabled, the truss ships " +
        "both and builds both, and the envelope and the truss are switched on and drawing while " +
        "the panels are gone, which is what stands in the opening")]
    internal static void ZeppelinPanelSwap(TestContext ctx)
    {
        string missionZrdr = SessionPaths.MissionZrdr(ctx.DataRoot, Chapter, Mission);
        ctx.RequireData(missionZrdr, $"{Chapter}/{Mission} zrdr");
        ctx.RequireData(SessionPaths.ChapterTextures(ctx.DataRoot, Chapter), $"{Chapter} textures");
        ctx.WithWorld(Chapter, collision: false, Mission, world => Run(ctx, world));
    }

    private static void Run(TestContext ctx, TestWorld world)
    {
        var report = new StringBuilder();
        var runtime = world.Runtime;
        var hull = runtime.FindNodes(Hull).FirstOrDefault();
        ctx.Check(hull != null, $"the {Hull} world node resolves in the {Chapter}/{Mission} world");
        if (hull == null)
        {
            return;
        }

        var section = runtime.FindNodes(Section, hull).FirstOrDefault();
        ctx.Check(section != null, $"…carrying the {Section} section the burn animation is rooted on");
        if (section == null)
        {
            return;
        }

        int sourceHull = world.Gamez.Nodes.FindIndex(n => n.Name.Equals(Hull, StringComparison.OrdinalIgnoreCase));
        int sourceSection = SourceIndex(world, Section, sourceHull);
        ctx.Check(sourceSection >= 0, $"…and the shipped {Section} subtree it was built from is findable");

        var nodes = new Dictionary<string, Node3D>(StringComparer.OrdinalIgnoreCase);
        foreach (string name in Watched)
        {
            if (runtime.FindNodes(name, hull).FirstOrDefault() is { } found)
            {
                nodes[name] = found;
            }
        }

        ctx.Same(Watched.Length, nodes.Count,
            $"every panel, burnt panel and envelope of the {Section} left side is built: {nodes.Count} of {Watched.Length}");

        // The flag reading, taken from the shipped model rather than from a name. A polygon with
        // show_backface set asks for both faces, and the builder answers with cull_disabled.
        report.AppendLine("=== authored sidedness against the built surface");
        foreach (string name in Watched)
        {
            if (!nodes.TryGetValue(name, out var node))
            {
                continue;
            }

            var want = AuthoredSidedness(world, sourceSection, name);
            var built = BuiltSidedness(node);
            report.AppendLine($"{name}: authored={Show(want)} built={Show(built)} surfaces={Surfaces(node).Count}");
            ctx.Check(built.Both || built.Single, $"{name} builds at least one surface to read a cull mode off");
            ctx.Check(want.Equals(built),
                $"{name} builds the cull mode its polygons ask for: authored={Show(want)} built={Show(built)}");
        }

        report.AppendLine();
        report.AppendLine("=== before the burn");
        Census(report, nodes);
        foreach (string name in new[] { "panelleft1", "panelleft2", "panelleft3", "panelleftb1", "panelleftb2" })
        {
            ctx.Check(nodes[name].IsVisibleInTree(), $"{name} carries the intact hull skin before the burn");
        }

        foreach (string name in new[] { "burnpl1", "burnpl2", "burnpl3", "gasbagleft", "structureleft" })
        {
            ctx.Check(!nodes[name].IsVisibleInTree(), $"{name} is switched off before the burn");
        }

        int started = runtime.Play(BurnAnim, section).Count;
        ctx.Check(started > 0, $"{BurnAnim} is a definition this world carries and it starts (instances={started})");

        // burn_leftside walks the five panels at 1, 9, 17, 22 and 30 s and calls the finish at 36 s.
        // Each fade runs 3 s. The 26 s mark is mid-burn with the top panel gone, and 45 s is past
        // the finish.
        float clock = 0f;
        foreach (float mark in new[] { 2f, 5f, 12f, 20f, 26f, 34f, 40f, 45f })
        {
            while (clock < mark)
            {
                runtime.Advance(Tick);
                clock += Tick;
            }

            report.AppendLine();
            report.AppendLine($"=== t={clock:0.0} s");
            Census(report, nodes);

            if (mark == 26f)
            {
                MidBurn(ctx, nodes);
            }
        }

        // Past the finish: it switches the whole panels group off and fades the two top burnt
        // panels in. Every panel position the section carries is then covered by burnt skin.
        foreach (string name in new[] { "panelleft1", "panelleft2", "panelleft3", "panelright3" })
        {
            ctx.Check(!nodes[name].IsVisibleInTree(), $"{name} is gone once the finish has switched the panels off");
        }

        foreach (string name in new[] { "burnpl1", "burnpl2", "burnpl3", "burnplb1", "burnplb2", "burnpr3" })
        {
            ctx.Check(nodes[name].IsVisibleInTree(), $"{name} draws in the place a hull panel left");
        }

        ctx.Check(!nodes["gasbagleft"].IsVisibleInTree(), $"the finish fades the left envelope out and switches it off at t={clock:0.0} s");
        ctx.Check(nodes["structureleft"].IsVisibleInTree(), $"the left truss stays drawn behind the burnt skin at t={clock:0.0} s");

        ctx.WriteArtifact("test-zeppelin-panel-swap.txt", report.ToString());
        ctx.Note($"{Chapter}/{Mission}: the {Section} burn swaps five hull-skin panels for five burnt ones, each mesh on the cull mode its own polygons ask for");
    }

    // Mid-burn, at 26 s: the three side panels have faded out. The envelope and the truss the
    // burn switched on at its first frame stand behind the opening they left.
    private static void MidBurn(TestContext ctx, Dictionary<string, Node3D> nodes)
    {
        foreach (string name in new[] { "panelleft1", "panelleft2", "panelleft3" })
        {
            ctx.Check(!nodes[name].IsVisibleInTree(), $"{name} has faded out by 26 s");
        }

        foreach (string name in new[] { "burnpl1", "burnpl2", "burnpl3", "gasbagleft", "structureleft" })
        {
            ctx.Check(nodes[name].IsVisibleInTree(), $"{name} draws behind the opening at 26 s");
        }

        ctx.Check(nodes["panelright3"].IsVisibleInTree(),
            $"the right side keeps its hull skin while only the left of the {nodes.Count} watched meshes burns");
    }

    private static void Census(StringBuilder report, Dictionary<string, Node3D> nodes)
    {
        foreach (var pair in nodes)
        {
            var node = pair.Value;
            report.AppendLine($"{pair.Key}: visible={node.Visible} inTree={node.IsVisibleInTree()} " +
                $"alpha={Alpha(node)?.ToString("0.###") ?? "unset"}");
        }
    }

    // The instance alpha a fade writes, read off the first surface that carries the parameter.
    private static float? Alpha(Node3D node)
    {
        foreach (var mesh in Meshes(node))
        {
            var value = mesh.GetInstanceShaderParameter(SceneBuilder.OpacityParam);
            if (value.VariantType == Variant.Type.Float)
            {
                return value.As<float>();
            }
        }
        return null;
    }

    // The shipped node index a name resolves to inside a subtree. A panel name that repeats on
    // all six sections is read off this section's own copy rather than another section's.
    private static int SourceIndex(TestWorld world, string name, int from)
    {
        var nodes = world.Gamez.Nodes;
        if (from < 0 || from >= nodes.Count)
        {
            return -1;
        }

        if (nodes[from].Name.Equals(name, StringComparison.OrdinalIgnoreCase))
        {
            return from;
        }

        foreach (int child in nodes[from].Children)
        {
            int found = SourceIndex(world, name, child);
            if (found >= 0)
            {
                return found;
            }
        }
        return -1;
    }

    // What the node's own geometry asks for, aggregated over its subtree. A hatch panel keeps its
    // two skin variants in child nodes, so both belong to the one answer.
    private static Sidedness AuthoredSidedness(TestWorld world, int section, string name)
    {
        int start = SourceIndex(world, name, section);
        var answer = default(Sidedness);
        if (start < 0)
        {
            return answer;
        }

        var pending = new Stack<int>();
        pending.Push(start);
        while (pending.Count > 0)
        {
            var node = world.Gamez.Nodes[pending.Pop()];
            foreach (int child in node.Children)
            {
                pending.Push(child);
            }

            if (node.MeshIndex < 0)
            {
                continue;
            }

            foreach (var poly in world.Gamez.Meshes[node.MeshIndex].Polygons)
            {
                answer = answer.With(poly.ShowBackface);
            }
        }
        return answer;
    }

    // The same question asked of the built surfaces: the bias shader carries its cull mode in its
    // own render_mode line. The built answer is read there rather than from a builder field.
    private static Sidedness BuiltSidedness(Node3D node)
    {
        var answer = default(Sidedness);
        foreach (var material in Surfaces(node))
        {
            string code = material.Shader?.Code ?? string.Empty;
            if (code.Contains("cull_disabled", StringComparison.Ordinal))
            {
                answer = answer.With(true);
            }

            if (code.Contains("cull_front", StringComparison.Ordinal))
            {
                answer = answer.With(false);
            }
        }
        return answer;
    }

    private static List<ShaderMaterial> Surfaces(Node3D node)
    {
        var found = new List<ShaderMaterial>();
        foreach (var mesh in Meshes(node))
        {
            for (int i = 0; i < (mesh.Mesh?.GetSurfaceCount() ?? 0); i++)
            {
                if (mesh.Mesh!.SurfaceGetMaterial(i) is ShaderMaterial material)
                {
                    found.Add(material);
                }
            }
        }
        return found;
    }

    // Every mesh instance in the node's own subtree. A watched name is a leaf panel or a hatch
    // group holding its two skin variants, so nothing here belongs to a sibling panel.
    private static List<MeshInstance3D> Meshes(Node3D node)
    {
        var found = new List<MeshInstance3D>();
        var pending = new Stack<Node>();
        pending.Push(node);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            if (current is MeshInstance3D mesh)
            {
                found.Add(mesh);
            }

            foreach (var child in current.GetChildren())
            {
                pending.Push(child);
            }
        }
        return found;
    }

    private static string Show(Sidedness value) =>
        value.Both && value.Single ? "both and one" : value.Both ? "both faces"
            : value.Single ? "one face" : "no surface";

    // Which sidednesses a mesh asks for or builds: a model whose polygons disagree, and the
    // surfaces it builds, carry both.
    private readonly record struct Sidedness(bool Both, bool Single)
    {
        public Sidedness With(bool both) => new(Both || both, Single || !both);
    }
}
