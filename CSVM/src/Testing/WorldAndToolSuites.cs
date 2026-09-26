using System.Collections.Generic;
using System.IO;
using System.Linq;
using CSVM.Flight.Airframe;
using CSVM.Flight.Camera;
using CSVM.Flight.Hud;
using CSVM.Flight.Modes;
using CSVM.Mech3;
using CSVM.Session.World;
using CSVM.Tooling;
using CSVM.UI.Boards;
using CSVM.UI.Labs;
using CSVM.UI.Screens;
using Godot;

using static CSVM.Testing.SuiteConstants;
namespace CSVM.Testing;

/// <summary>Suites asserting the built world and the workbench tools: chapter data gates and
/// censuses, world lighting and viewers, and the lab surfaces.</summary>
internal static class WorldAndToolSuites
{
    // The ambient cloud field's sprite arm, named so the mip-bias census tallies it apart from the
    // camera-facing billboards it would otherwise fall in with.
    private const string CloudFieldArm = "cloud-field";

    private static readonly string[] AlphaClassChapters =
        { "C1", "C1B", "C1C", "C2", "C2B", "C3", "C4", "C5" };

    // The two halves of docs/org/vertexLighting.md's census: the flares and signage overlays carry
    // the alpha bit, the building skins and the water do not. The bit routes and blends; it is not a
    // lighting exemption on the original's hardware draw.
    private static readonly (string Name, bool CarriesAlphaBit)[] AlphaClassExpectations =
    {
        ("poleflare", true), ("lightpole", true),
        ("cblock1", false), ("bldg1", false), ("wtr00000", false),
    };

    // Exports a built plane to a temp .glb and asserts the file lands and re-imports textured, the
    // right way out: the round trip the viewer's export paths rely on.
    [Suite("gltf-export", "the viewer plane exports to glTF textured and wound the right way out")]
    internal static void GltfExport(TestContext ctx)
    {
        ctx.RequireData(ctx.PlanesGamezPath, $"planes gamez");
        string texturesPath = SessionPaths.ChapterTextures(ctx.DataRoot, "C1");
        ctx.RequireData(texturesPath, $"C1 textures");

        var planesGamez = GameZ.Load(ctx.PlanesGamezPath);
        var textures = new TextureArchive(texturesPath);
        Node3D? plane = null;
        string path = Path.Combine(ctx.ScratchDir, $"gltf-export-{ctx.PlaneName}.glb");
        try
        {
            Directory.CreateDirectory(ctx.ScratchDir);
            plane = new PlaneBuilder(planesGamez, textures).Build(ctx.PlaneName);
            ctx.Host.AddChild(plane);

            var err = GltfExporter.Export(plane, path);
            ctx.Same((long)Error.Ok, (long)err, $"export write result");
            bool wrote = File.Exists(path) && new FileInfo(path).Length > 0;
            ctx.Check(wrote, $"exported .glb is present and non-empty path={path}");

            // Re-import the file the exporter just wrote and count the textured meshes that survived
            // the material conversion, proves the shader skins became serializable StandardMaterials.
            if (wrote)
            {
                var doc = new GltfDocument();
                var state = new GltfState();
                var readErr = doc.AppendFromFile(path, state);
                ctx.Same((long)Error.Ok, (long)readErr, $"re-import read result");
                var scene = doc.GenerateScene(state) as Node3D;
                ctx.Check(scene != null, $"re-imported scene has a Node3D root");
                int textured = scene == null ? 0 : CountTexturedMeshes(scene);
                ctx.Check(textured >= 1, $"re-imported textured meshes count={textured}");

                // The round trip through glTF is winding-neutral, so the re-imported model must
                // come back wound the way a Godot-built box is, and the opposite way from the live
                // plane it was exported from (docs/formats/gotchas.md).
                double box = WindingSense(new BoxMesh());
                double live = WindingSense(plane);
                double exported = scene == null ? 0 : WindingSense(scene);
                ctx.Check(live * box < 0, $"the live plane is wound inside out vs Godot ({live:0.###e+0})");
                ctx.Check(exported * box > 0, $"re-imported plane is wound outward ({exported:0.###e+0})");
                int culled = scene == null ? 0 : CountMeshesWithCullMode(scene, BaseMaterial3D.CullModeEnum.Back);
                ctx.Check(culled >= 1, $"re-imported single-sided meshes count={culled}");
                scene?.Free();
            }
        }
        finally
        {
            plane?.Free();
            textures.Dispose();
        }
    }

    // The reader behind the texture's own alpha class, which is a reader question and not a render
    // one: the class comes off the extraction manifest's `alpha` field, and a pixel test cannot
    // stand in for it. Able to fail: with the manifest read removed, every chapter falls back
    // to the pixels and lightpole (a Simple texture in several chapters) classifies None.
    [Suite("texture-alpha-class",
        "every chapter's texture archive classifies alpha from the extraction manifest rather than "
        + "the decoded pixels, and the named shipped textures land in the classes docs/org/"
        + "vertexLighting.md pins: poleflare and lightpole carry the alpha bit, cblock1, bldg1 and "
        + "wtr00000 do not")]
    internal static void TextureAlphaClass(TestContext ctx)
    {
        foreach (var chapter in AlphaClassChapters)
        {
            string path = SessionPaths.ChapterTextures(ctx.DataRoot, chapter);
            ctx.RequireData(path, $"{chapter} textures");
            using var textures = new TextureArchive(path);
            ctx.Check(textures.ManifestTextureCount > 0,
                $"{chapter}'s archive carries an extraction manifest count={textures.ManifestTextureCount}");
            ctx.Check(textures.ManifestAlphaCount > 0,
                $"{chapter} classifies textures as alpha-bearing count={textures.ManifestAlphaCount} of {textures.ManifestTextureCount}");
        }

        using var c5 = new TextureArchive(SessionPaths.ChapterTextures(ctx.DataRoot, "C5"));
        foreach (var (name, carriesAlphaBit) in AlphaClassExpectations)
        {
            c5.Find(name);
            bool bit = c5.LastAlphaClass != TextureArchive.AlphaClass.None;
            ctx.Check(bit == carriesAlphaBit,
                $"C5 texture={name} class={c5.LastAlphaClass} carries the alpha bit={bit} expected={carriesAlphaBit}");
        }
    }

    // A second aircraft build must reuse the first's Shader resources rather than generating its
    // own copies of the same text. Godot compiles a Shader the first time a material takes it, so a
    // per-builder shader memo makes every mid-flight AI spawn pay that compile again; a generated
    // aircraft is built on the frame path, where the bill lands as a stall. Able to fail: with the
    // memo back on the instance, none of the second model's shaders is one of the first's.
    [Suite("plane-shader-reuse",
        "a second aircraft build reuses the first's Shader resources instead of generating its own copies of the same text, which is what keeps a mid-flight generator launch off Godot's per-shader compile")]
    internal static void PlaneShaderReuse(TestContext ctx)
    {
        ctx.RequireData(ctx.PlanesGamezPath, $"planes gamez");
        string texturesPath = SessionPaths.ChapterTextures(ctx.DataRoot, "C1");
        ctx.RequireData(texturesPath, $"C1 textures");

        var planesGamez = GameZ.Load(ctx.PlanesGamezPath);
        var textures = new TextureArchive(texturesPath);
        Node3D? first = null, second = null;
        try
        {
            first = new PlaneBuilder(planesGamez, textures, spinningProps: true).Build(ctx.PlaneName);
            var firstShaders = ShadersUnder(first);
            second = new PlaneBuilder(planesGamez, textures, spinningProps: true).Build(ctx.PlaneName);
            var secondShaders = ShadersUnder(second);

            // The control: a model that resolved to one shader would satisfy the reuse check
            // vacuously, and so would one carrying no ShaderMaterial at all.
            ctx.Check(firstShaders.Count > 1,
                $"the first build carries several distinct shaders plane={ctx.PlaneName} count={firstShaders.Count}");
            ctx.Check(secondShaders.Count > 0, $"the second build carries shaders count={secondShaders.Count}");

            int shared = secondShaders.Count(s => firstShaders.Contains(s));
            ctx.Same(secondShaders.Count, shared,
                $"every shader of the second build is one the first already compiled (second={secondShaders.Count} shared={shared})");
        }
        finally
        {
            first?.Free();
            second?.Free();
            textures.Dispose();
        }
    }

    // The distinct Shader resources every ShaderMaterial in the subtree points at, by reference:
    // two builds sharing one memo hand back the same instances, two builds with their own hand back
    // equal text on different resources.
    internal static HashSet<Shader> ShadersUnder(Node node)
    {
        var found = new HashSet<Shader>();
        void Walk(Node n)
        {
            if (n is MeshInstance3D mesh)
            {
                for (int i = 0; i < mesh.GetSurfaceOverrideMaterialCount(); i++)
                {
                    if (mesh.GetActiveMaterial(i) is ShaderMaterial { Shader: { } shader })
                    {
                        found.Add(shader);
                    }
                }
            }
            foreach (var child in n.GetChildren())
            {
                Walk(child);
            }
        }
        Walk(node);
        return found;
    }

    // The cockpit interior's build and its per-mode hiding. Two builds of
    // the same airframe: the default one must be byte-for-byte the exterior build (an AI plane
    // pays nothing), the cockpitInterior one must gain the subtree, hidden, at the cockpit_camera
    // marker. Able to fail: without the Skip arm the interior build finds no cockpit1; without the
    // mount it sits at the plane origin at authored (~20x) scale.
    [Suite("cockpit-interior",
        "the player plane's cockpit1 interior builds hidden at the cockpit_camera marker, an AI-style build gains nothing, and the per-mode hiding follows the pilot's view (B11), while the Danger Zone photograph's frame shows the hidden airframe on a layer no pane draws and puts it back after")]
    internal static void CockpitInterior(TestContext ctx)
    {
        ctx.RequireData(ctx.PlanesGamezPath, $"planes gamez");
        string texturesPath = SessionPaths.ChapterTextures(ctx.DataRoot, "C1");
        ctx.RequireData(texturesPath, $"C1 textures");

        var planesGamez = GameZ.Load(ctx.PlanesGamezPath);
        var textures = new TextureArchive(texturesPath);
        Node3D? exterior = null, withInterior = null;
        try
        {
            var plain = new PlaneBuilder(planesGamez, textures, spinningProps: true);
            exterior = plain.Build(ctx.PlaneName);
            ctx.Check(plain.CockpitInterior == null,
                $"the default flight build carries no interior plane={ctx.PlaneName}");

            var builder = new PlaneBuilder(planesGamez, textures, spinningProps: true,
                cockpitInterior: true);
            withInterior = builder.Build(ctx.PlaneName);
            var interior = builder.CockpitInterior;
            ctx.Check(interior != null, $"the interior build carries a cockpit1 subtree");
            if (interior == null)
                return;

            ctx.Check(!interior.Visible, $"the interior is built hidden");
            ctx.Check(interior.Position.IsEqualApprox(builder.CockpitCameraOffset),
                $"mounted at the cockpit_camera marker pos={interior.Position}");
            ctx.Check(interior.Scale.IsEqualApprox(Vector3.One * PlaneBuilder.InteriorScale),
                $"scaled to the port's interior scale scale={interior.Scale.X:0.###}");
            ctx.Check(builder.MeshInstanceCount > plain.MeshInstanceCount,
                $"the interior adds meshes plain={plain.MeshInstanceCount} with={builder.MeshInstanceCount}");
            // The panel the pilot reads and the two torn-skin panels B12 drives, both hidden.
            ctx.Check(FindNamed(interior, "gauges") != null, $"the interior carries its gauges subtree");
            foreach (var panel in new[] { "pcdp4", "pcdp6" })
            {
                var node = FindNamed(interior, panel);
                ctx.Check(node != null, $"the interior carries {panel}");
                ctx.Check(node is not { Visible: true }, $"{panel} is built hidden (torn state off)");
            }
            // ⚠ The windshield bullet-hole quads and the two warning lamps ship active:true. An
            // unparked build renders white splats across the sky on a pristine plane. The parking
            // follows reset_bulletholes: the bulNx quads dark, the bulletN groups over them drawn.
            foreach (var lamp in new[] { "lowalt_on", "stallwarning_on" })
            {
                var node = FindNamed(interior, lamp);
                ctx.Check(node != null, $"the interior carries {lamp}");
                ctx.Check(node is not { Visible: true }, $"{lamp} is parked hidden on a pristine plane");
            }
            foreach (var group in new[] { "bullet1", "bullet2", "bullet3", "bullet4", "bullet5" })
            {
                var node = FindNamed(interior, group);
                ctx.Check(node is { Visible: true }, $"the interior carries {group}, drawn");
                if (node == null)
                    continue;
                int quads = 0, lit = 0;
                foreach (var child in node.GetChildren())
                {
                    if (child is not Node3D quad)
                        continue;
                    quads++;
                    if (quad.Visible)
                        lit++;
                }
                ctx.Check(quads >= 3 && lit == 0,
                    $"{group}'s {quads} hole quads are parked hidden on a pristine plane, lit={lit}");
            }
            // …and the panel geometry beside them is NOT parked: the states are a named set, not a
            // blanket hide, so a wrong predicate that hid the dashboard would fail here.
            foreach (var kept in new[] { "gauges", "structure", "nosedamage", "ggindicator0" })
                ctx.Check(FindNamed(interior, kept) is { Visible: true }, $"{kept} still renders");

            // The exterior panels stay DamageVisuals' alone: the interior pair must not join them.
            foreach (var node in builder.DamagePanels)
                ctx.Check(!AnimRuntime.NameOf(node).StartsWith("pcdp", System.StringComparison.OrdinalIgnoreCase),
                    $"DamagePanels holds no cockpit panel name={AnimRuntime.NameOf(node)}");

            var cockpit = CockpitVisibility.Bind(withInterior, interior);
            ctx.Check(cockpit != null, $"the visibility rig binds to the built model");
            if (cockpit == null)
                return;
            var body = FindNamed(withInterior, "healthy");
            var markers = FindNamed(withInterior, "markers");
            var dontmove = FindNamed(withInterior, "dontmove");
            ctx.Check(body != null && markers != null && dontmove != null,
                $"the airframe's own healthy/markers/dontmove groups were found");
            // ⚠ The gauge sub-assemblies inside cockpit1 carry their own 'markers' children; the
            // one bound must be the airframe's, which is the one holding cockpit_camera.
            ctx.Check(markers != null && FindNamed(markers, "cockpit_camera") != null,
                $"the bound markers group is the airframe's, not a gauge's");

            cockpit.Apply(PilotViewMode.Cockpit, firstPerson: true);
            ctx.Check(interior.Visible && body is { Visible: false }
                && markers is { Visible: true } && dontmove is { Visible: true },
                $"Cockpit: interior in, body out, markers/dontmove kept");
            cockpit.Apply(PilotViewMode.Nose, firstPerson: true);
            ctx.Check(!interior.Visible && body is { Visible: false }
                && markers is { Visible: false } && dontmove is { Visible: false },
                $"Nose: interior out, body out, markers/dontmove out");
            PhotographFrame(ctx, cockpit, interior, body, markers, dontmove);
            cockpit.Apply(PilotViewMode.Cockpit, firstPerson: false);
            ctx.Check(!interior.Visible && body is { Visible: true }
                && markers is { Visible: true } && dontmove is { Visible: true },
                $"a held external view restores the aircraft while Cockpit stays selected");

            DrivenPanel(ctx, interior);
            DrivenBelts(ctx, interior, builder, planesGamez, textures);
        }
        finally
        {
            exterior?.Free();
            withInterior?.Free();
            textures.Dispose();
        }
    }

    // The photograph's fill light, proved in the three things the pixels follow from, since a
    // suite cannot read pixels back. The faithful aircraft shader swaps its ambient half for the
    // fill only at an armed eye. The photograph's frame arms that eye on its own pilot's instances
    // alone, and the pane stands out of reach of it. The end of the frame disarms every one. The
    // fill's level is C1's zone through the decoded law. Able to fail: an unarmed airframe, a
    // stranger's plane armed, a pane within reach, or an instance left armed after the frame.
    [Suite("danger-zone-photograph-fill",
        "the Danger Zone photograph's frame raises its own pilot's aircraft to the decoded fill ambient for its eye alone: the pane on the same frame is out of reach, another plane is never armed, and the next frame is disarmed")]
    internal static void DangerZonePhotographFill(TestContext ctx)
    {
        ctx.RequireData(ctx.PlanesGamezPath, $"planes gamez");
        string texturesPath = SessionPaths.ChapterTextures(ctx.DataRoot, "C1");
        ctx.RequireData(texturesPath, $"C1 textures");

        var weather = WeatherState.Load(SessionPaths.MissionZrdr(ctx.DataRoot, "C1", "IA1"));
        ctx.Check(weather != null, $"C1 IA1's weather loads");
        if (weather != null)
        {
            var zone = weather.Zone("zone1");
            (Vector3 ambient, _) = WeatherRig.SunVertexLight(zone);
            Vector3 fill = WeatherRig.PhotographFill(zone);
            float expected = Mathf.Clamp(Mathf.Floor((((zone.SunAmbient * 1.5f) + 0.1f) * 255f) + 0.5f), 0f, 255f) / 255f;
            ctx.Check(Mathf.Abs(WeatherRig.PhotographFillAmbient(zone.SunAmbient) - expected) < 1e-5f,
                $"the fill scalar is the stored byte A={zone.SunAmbient:0.###} fill={expected:0.####}");
            ctx.Check(fill.X > ambient.X && fill.Y > ambient.Y && fill.Z > ambient.Z,
                $"the fill lights brighter than the zone's ambient half ambient={ambient} fill={fill}");
        }

        var planesGamez = GameZ.Load(ctx.PlanesGamezPath);
        var textures = new TextureArchive(texturesPath);
        var controller = new Node3D { Name = "photo_fill_pilot" };
        var stranger = new Node3D { Name = "photo_fill_stranger", Position = new Vector3(40f, 0f, 0f) };
        var paneView = new SubViewport { Size = new Vector2I(64, 48), RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled };
        DangerZonePhotograph? photo = null;
        try
        {
            controller.AddChild(new PlaneBuilder(planesGamez, textures, spinningProps: true).Build(ctx.PlaneName));
            stranger.AddChild(new PlaneBuilder(planesGamez, textures, spinningProps: true).Build(ctx.PlaneName));
            ctx.Host.AddChild(controller);
            ctx.Host.AddChild(stranger);
            // The chase pane, where a pilot's view stands: behind and above the tail.
            var pane = new Camera3D { Position = new Vector3(0f, 3f, 18f) };
            paneView.AddChild(pane);
            ctx.Host.AddChild(paneView);

            var shaders = ShadersUnder(controller);
            int gated = shaders.Count(s => s.Code.Contains("csky_sun_fill_rgb : csky_sun_ambient_rgb", System.StringComparison.Ordinal)
                && s.Code.Contains("distance(CAMERA_POSITION_WORLD, csky_photo_eye.xyz)", System.StringComparison.Ordinal));
            ctx.Check(gated > 0, $"the aircraft's faithful shaders swap the ambient half at an armed eye gated={gated} of {shaders.Count}");

            photo = DangerZonePhotograph.Build(pane, null, () => controller.GlobalTransform, 18.5f, () => 0.5f,
                airframe: controller);
            controller.AddChild(photo);
            ctx.Check(photo.Request(_ => { }), $"the photograph takes the request");
            photo._Process(0);

            var instances = Meshes(controller).OfType<GeometryInstance3D>().ToList();
            Vector3 eye = photo.Eye.Origin;
            var armed = new Vector4(eye.X, eye.Y, eye.Z, 1f);
            int hit = instances.Count(i => i.GetInstanceShaderParameter(SceneBuilder.PhotoEyeParam).AsVector4() == armed);
            ctx.Check(instances.Count > 0 && hit == instances.Count && photo.Filled.Count == instances.Count,
                $"the frame arms every instance of its own pilot's aircraft at its eye armed={hit} of {instances.Count}");
            int strays = Meshes(stranger).OfType<GeometryInstance3D>()
                .Count(i => i.GetInstanceShaderParameter(SceneBuilder.PhotoEyeParam).AsVector4().W > 0.5f);
            ctx.Check(strays == 0, $"another plane's instances are never armed strays={strays}");
            float paneReach = pane.GlobalPosition.DistanceTo(eye);
            ctx.Check(paneReach > SceneBuilder.PhotoEyeReach * 10f,
                $"the pane drawing the same frame stands out of the fill's reach pane={paneReach:0.#}m reach={SceneBuilder.PhotoEyeReach}m");

            // The frame's end: leaving the tree before the draw lands takes the same disarm the
            // post-draw callback does.
            controller.RemoveChild(photo);
            int left = instances.Count(i => i.GetInstanceShaderParameter(SceneBuilder.PhotoEyeParam).AsVector4() != Vector4.Zero);
            ctx.Check(left == 0 && photo.Filled.Count == 0,
                $"the next frame draws every instance disarmed left={left}");
        }
        finally
        {
            photo?.Free();
            controller.QueueFree();
            stranger.QueueFree();
            paneView.QueueFree();
            textures.Dispose();
        }
    }

    // The interior's own render pass (--cockpit-pass): the panel leaves the plane model for a
    // world of its own, where both it and the camera sit at the origin, so no chapter-scale
    // coordinate enters its transform chain. Able to fail: a pass that shares the main World3D, one
    // that leaves the mount translation on the interior, a camera placed anywhere but the origin,
    // or an FOV taken from a second copy of the per-mode table instead of the camera's own.
    [Suite("cockpit-overlay-pass",
        "--cockpit-pass moves the interior into a world of its own, where it and the camera both sit at the origin and no chapter-scale coordinate reaches the panel's transform")]
    internal static void CockpitOverlayPass(TestContext ctx)
    {
        ctx.RequireData(ctx.PlanesGamezPath, $"planes gamez");
        string texturesPath = SessionPaths.ChapterTextures(ctx.DataRoot, "C1");
        ctx.RequireData(texturesPath, $"C1 textures");

        var planesGamez = GameZ.Load(ctx.PlanesGamezPath);
        var textures = new TextureArchive(texturesPath);
        Node3D? plane = null;
        Node? host = null;
        try
        {
            var builder = new PlaneBuilder(planesGamez, textures, spinningProps: true,
                cockpitInterior: true);
            plane = builder.Build(ctx.PlaneName);
            if (builder.CockpitInterior is not { } interior)
            {
                ctx.Check(false, $"the interior build carries a cockpit1 subtree");
                return;
            }
            var mountBasis = interior.Transform.Basis;
            host = new Node();
            var overlay = CockpitOverlay.Build(host, interior, null, null);
            ctx.Check(overlay != null, $"the pass builds over a rig's HUD parent");
            if (overlay == null)
                return;

            ctx.Check(interior.Position == Vector3.Zero,
                $"the interior sits at the overlay world's origin pos={interior.Position}");
            ctx.Check(interior.Transform.Basis.IsEqualApprox(mountBasis),
                $"the head-pitch tilt and the interior scale survive the move");
            ctx.Check(interior.Scale.IsEqualApprox(Vector3.One * PlaneBuilder.InteriorScale),
                $"still at the port's interior scale scale={interior.Scale.X:0.###}");
            ctx.Check(overlay.Camera.Transform.Origin == Vector3.Zero,
                $"the overlay camera sits at the origin pos={overlay.Camera.Transform.Origin}");
            var view = interior.GetParent() as SubViewport;
            ctx.Check(view != null, $"the interior hangs in the pass's own SubViewport");
            ctx.Check(view != null && view.TransparentBg,
                $"the pass renders on a transparent background, so the main view shows under it");
            ctx.Check(view != null && overlay.Camera.GetParent() == view,
                $"the camera looks at the interior from inside that same viewport");
            ctx.Check(view?.World3D != null && view.World3D != host.GetWindow()?.World3D,
                $"the pass owns its World3D rather than sharing the main one");
            // The same table the pilot's own camera reads, not a copy: the cockpit's 80° base.
            ctx.Check(Mathf.Abs(CameraController.FirstPersonFovDeg(PilotViewMode.Cockpit)
                    - CameraController.HorizontalToVerticalFovDeg(80f)) < 0.001f,
                $"the pass's FOV law is the camera's own per-mode law");
            // The other half of that table: every pose outside the interior takes the one decoded
            // base, whatever FOV the camera arrived carrying. No caller writes its own number
            // beside the camera and expects the controller to hand it back.
            var probeCam = new Camera3D { Fov = 12f };
            host.AddChild(probeCam);
            new CameraController(probeCam, new CamParams(), _ => false, -1).RestoreExternalFov();
            ctx.Check(Mathf.Abs(probeCam.Fov - CameraController.ExternalFovDeg) < 0.001f,
                $"an external pose takes the decoded base fov={probeCam.Fov:0.##}");
            // The wobble the interior inherited below the shake pivot has to reach the pass. The
            // mount's tilt is about X, so its Right axis is the witness: a Z roll of r turns it by
            // exactly r, and a pass that forgot the wobble leaves it at 0.
            float rolled = CockpitOverlay.WobbledMount(mountBasis, 0.3f).X.AngleTo(mountBasis.X);
            ctx.Check(Mathf.Abs(rolled - 0.3f) < 0.001f,
                $"the shake pivot's roll reaches the panel in the pass angle={rolled:0.###} rad");
            ctx.Check(CockpitOverlay.WobbledMount(mountBasis, 0f).IsEqualApprox(mountBasis),
                $"no wobble leaves the mount basis untouched");
            // The pass keeps the world's orientation, so a muzzle flash 11 m right of a yawed plane's
            // eye at (1000, 50, -2000) lands at that same world offset from the pass's origin; a
            // mirror that kept the eye's coordinates would put it thousands of metres out.
            var yawed = new Basis(Vector3.Up, Mathf.Pi / 2f);
            var eye = new Vector3(1000f, 50f, -2000f);
            var offset = yawed * new Vector3(11f, 0f, 0f);
            var mirrored = CockpitOverlay.ToOverlay(eye + offset, eye);
            ctx.Check(mirrored.IsEqualApprox(offset) && mirrored.Length() < 12f,
                $"a muzzle flash lands in the pass at its world offset from the eye got={mirrored}");
            // The crash cut's exit from first person, whose whole point is that it works with the
            // interior node untouched: hiding the airframe cannot reach a panel that lives outside
            // the plane model, and no Sync follows the cut to notice a hidden one.
            interior.Visible = true;
            overlay.Deactivate();
            ctx.Check(!overlay.Visible && interior.Visible,
                $"Deactivate takes the pass off the screen without touching the interior node");
            ctx.Check(view is { RenderTargetUpdateMode: SubViewport.UpdateMode.Disabled },
                $"and stops the viewport re-rendering it");
        }
        finally
        {
            host?.Free();
            plane?.Free();
            textures.Dispose();
        }
    }

    // The authored panel driven off live readings: the needles take an absolute angle and
    // the two lamps follow the cluster's own blink state. Able to fail: a needle left at its modeled
    // rest rotation, a lamp still parked while its condition holds, or a drive that moves the panel
    // geometry around the needle instead of the needle itself.
    internal static void DrivenPanel(TestContext ctx, Node3D interior)
    {
        var panel = CockpitGauges.Bind(interior);
        ctx.Check(panel != null, $"the gauge drive binds to the built interior");
        var speed = FindNamed(interior, "speed");
        var hundreds = FindNamed(interior, "hundreds");
        var lowAlt = FindNamed(interior, "lowalt_on");
        var face = FindNamed(interior, "speedometer");
        if (panel == null || speed == null || hundreds == null || lowAlt == null || face == null)
        {
            ctx.Check(false, $"the panel's needles, lamp and face were all found");
            return;
        }

        // ⚠ The rotation axis is only right if the needle is authored flat in its own XY plane.
        // If the import left the dial in XZ, spinning about Z would tip the needle out of the face
        // instead of sweeping it, and every angle assertion below would still pass.
        if (FirstMesh(speed) is { } needleMesh)
        {
            var size = needleMesh.GetAabb().Size;
            ctx.Check(size.Z < size.Y * 0.1f && size.Y > 0f,
                $"the needle is flat in its own XY plane, so +Z is the sweep axis size={size}");
        }

        var faceRest = face.Transform;
        var horizon = FindNamed(interior, "pfhorizon");
        var horizonRest = horizon?.Transform;
        var cluster = new GaugeCluster { SpeedMph = 200f, AltitudeFt = 500f };
        try
        {
            panel.Apply(cluster);
            // 0.7199957 deg/mph clockwise, i.e. negative about +Z.
            float wantSpeed = -Mathf.DegToRad(GaugeCluster.SpeedAngleDeg(200f));
            float gotSpeed = speed.Transform.Basis.GetEuler().Z;
            ctx.Check(Mathf.Abs(Mathf.AngleDifference(gotSpeed, wantSpeed)) < 0.01f,
                $"the speed needle takes its absolute angle got={gotSpeed:0.000} want={wantSpeed:0.000} rad");
            float wantAlt = -Mathf.DegToRad(GaugeCluster.AltHundredsAngleDeg(500f));
            float gotAlt = hundreds.Transform.Basis.GetEuler().Z;
            ctx.Check(Mathf.Abs(Mathf.AngleDifference(gotAlt, wantAlt)) < 0.01f,
                $"the long altimeter needle takes its absolute angle got={gotAlt:0.000} want={wantAlt:0.000} rad");
            // ⚠ Only the needle moves: the dial it sweeps over is authored geometry.
            ctx.Check(face.Transform.IsEqualApprox(faceRest), $"the dial face is left where it was built");
            ctx.Check(!lowAlt.Visible, $"LOW ALT stays parked while the cluster's lamp is dark");

            // A second write must REPLACE the angle, not accumulate onto it.
            panel.Apply(cluster);
            ctx.Check(Mathf.Abs(Mathf.AngleDifference(speed.Transform.Basis.GetEuler().Z, wantSpeed)) < 0.01f,
                $"a second frame writes the same absolute angle rather than turning again");

            // The horizon: N = Rz(-roll) . Rx(pitch), no gain/offset/clamp. A zero
            // attitude first, since that has to equal the authored rest basis exactly.
            ctx.Check(horizon != null && horizonRest != null, $"the interior carries pfhorizon");
            if (horizon != null && horizonRest != null)
            {
                cluster.HorizonPitchRad = 0f;
                cluster.HorizonRollRad = 0f;
                panel.Apply(cluster);
                ctx.Check(horizon.Transform.Basis.IsEqualApprox(horizonRest.Value.Basis),
                    $"a zero attitude leaves pfhorizon at its authored basis");

                float pitch = Mathf.DegToRad(12f);
                float roll = Mathf.DegToRad(-25f);
                cluster.HorizonPitchRad = pitch;
                cluster.HorizonRollRad = roll;
                panel.Apply(cluster);
                var want = new Basis(Vector3.Back, -roll) * new Basis(Vector3.Right, pitch);
                ctx.Check(horizon.Transform.Basis.IsEqualApprox(want),
                    $"pfhorizon takes Rz(-roll).Rx(pitch) pitch={pitch:0.000} roll={roll:0.000} rad");
                ctx.Check(horizon.Transform.Origin.IsEqualApprox(horizonRest.Value.Origin),
                    $"pfhorizon's authored translation is untouched");
            }

            // The compass drum: found by the binary's own name first, the data's "comp"
            // second (docs/formats/hud.md), then turned about Y by +heading, which is what
            // cancels an aircraft yaw that is itself -heading under this port's convention.
            var compass = FindNamed(interior, "compass") ?? FindNamed(interior, "comp");
            ctx.Check(compass != null, $"the interior carries the compass drum, by either name");
            if (compass != null)
            {
                var compassRest = compass.Transform;
                cluster.HeadingDeg = 0f;
                panel.Apply(cluster);
                ctx.Check(compass.Transform.Basis.IsEqualApprox(compassRest.Basis),
                    $"a zero heading leaves the drum at its authored basis");

                float heading = 40f;
                cluster.HeadingDeg = heading;
                panel.Apply(cluster);

                // ⚠ Pin the INVARIANT, never the local angle: a literal pins whichever sign was
                // written and stays green when the drum turns backwards, which is how an inverted
                // card shipped (docs/formats/hud.md).
                var yawed = new Basis(Vector3.Up, Mathf.DegToRad(-heading));
                var nose = -yawed.Z;
                float probeBearing = Mathf.PosMod(Mathf.RadToDeg(Mathf.Atan2(nose.X, -nose.Z)), 360f);
                ctx.Check(Mathf.Abs(probeBearing - heading) < 0.01f,
                    $"the probe yaw reads heading={heading:0.###} deg as FlightController derives it");
                ctx.Check((yawed * compass.Transform.Basis.Orthonormalized()).IsEqualApprox(Basis.Identity),
                    $"the drum cancels that yaw, holding the card's world orientation at heading={heading:0.###} deg");
                ctx.Check(compass.Transform.Origin.IsEqualApprox(compassRest.Origin),
                    $"the drum's authored translation is untouched");

                // A second write replaces the angle rather than accumulating onto it, the same
                // shape the needle and horizon writes already guard.
                panel.Apply(cluster);
                ctx.Check((yawed * compass.Transform.Basis.Orthonormalized()).IsEqualApprox(Basis.Identity),
                    $"a second frame at the same heading writes the same absolute angle");
            }
        }
        finally
        {
            cluster.Free();
        }
    }

    // The belt lights take the loadout's colour tier. A pristine plane reads all-green,
    // which proves nothing, so this drives a spent belt and reads the material back. Able to fail:
    // a drive that recolours nothing, or one that writes the shared built material and so repaints
    // every indicator at once instead of the one position. Binds through CockpitGauges.Bind(builder),
    // the same expression the live flight path calls, so a caller that regresses to the interior
    // alone breaks here rather than only at the controls.
    internal static void DrivenBelts(TestContext ctx, Node3D interior, PlaneBuilder builder,
        GameZ planesGamez, TextureArchive textures)
    {
        var cluster = GaugeCluster.Build(planesGamez, ctx.PlaneName, textures, new List<DestroyablePart>());
        var panel = CockpitGauges.Bind(builder);
        if (cluster == null || panel == null)
        {
            ctx.Check(false, $"a real cluster and panel were built for the belt drive");
            return;
        }
        try
        {
            // Not a fixed count: derived from the interior's own materials, so an empty-materials
            // regression fails here instead of an all-green panel. "greenindicator", not bare
            // "indicator", which also matches the still-unwired horizon's texture.
            int expectedBelts = builder.InteriorMaterials.Count(m =>
                m.TextureName.Contains("greenindicator", System.StringComparison.OrdinalIgnoreCase));
            int expectedZones = builder.InteriorMaterials.Count(m =>
                m.TextureName.Contains("hatchptrn", System.StringComparison.OrdinalIgnoreCase));
            ctx.Check(panel.BeltCount > 0 && panel.BeltCount == expectedBelts,
                $"belts bound matches the indicator-light materials the interior carries found={panel.BeltCount} materials={expectedBelts}");
            ctx.Check(panel.DamageZoneCount > 0 && panel.DamageZoneCount == expectedZones,
                $"damage zones bound matches the hatch materials the interior carries found={panel.DamageZoneCount} materials={expectedZones}");
            // Position 0 spent, position 1 full: one dial, two tiers, so a shared-material write
            // cannot pass this.
            cluster.GunGauge = new GaugeCluster.WeaponGauge { Slots = new[] { 0f, 1f } };
            panel.Apply(cluster);
            var names = new Dictionary<ulong, string>();
            foreach (var (material, texture) in builder.InteriorMaterials)
                names[material.GetInstanceId()] = texture;
            // ⚠ An indicator carries TWO driven surfaces on different colour cycles (the light and
            // its hilite bar), so a check that reads "the first albedo" reads whichever the mesh
            // happens to order first and proves nothing.
            var spentLight = AlbedoOf(FindNamed(interior, "ggindicator0"), names, hilite: false);
            var spentBar = AlbedoOf(FindNamed(interior, "ggindicator0"), names, hilite: true);
            var fullLight = AlbedoOf(FindNamed(interior, "ggindicator1"), names, hilite: false);
            ctx.Check(spentLight != null && spentBar != null && fullLight != null,
                $"the light and the hilite bar were both found on the indicators");
            ctx.Check(spentLight == cluster.BeltLightTexture(2),
                $"a spent belt position's light takes the red variant");
            ctx.Check(spentBar == cluster.BeltHiliteTexture(2),
                $"and its hilite bar takes the red bar, on its own cycle");
            ctx.Check(fullLight == cluster.BeltLightTexture(0),
                $"the position beside it stays green, so the write is per-node");

            // The damage zones read the PART name, which is the node's minus its "damage" suffix.
            // A wrong key reads as a permanently green dial, so drive one zone to its red band.
            cluster.PartFraction = part =>
                part.Equals("nose", System.StringComparison.OrdinalIgnoreCase) ? 0.05f : 1f;
            panel.Apply(cluster);
            var hurt = AlbedoOf(FindNamed(interior, "nosedamage"), names, hilite: true);
            var intact = AlbedoOf(FindNamed(interior, "taildamage"), names, hilite: true);
            ctx.Check(hurt != null && intact != null, $"the damage dial's zones were found");
            ctx.Check(hurt == cluster.ZoneHiliteTexture(3),
                $"a zone at 5% takes the red border");
            ctx.Check(intact == cluster.ZoneHiliteTexture(0),
                $"an untouched zone beside it stays green");
        }
        finally
        {
            cluster.Free();
        }
    }

    // The albedo the driven surface of the requested KIND is pointing at right now: the hilite bar
    // or the light beside it, told apart by the texture each was built from.
    internal static Texture2D? AlbedoOf(Node3D? node, IReadOnlyDictionary<ulong, string> names, bool hilite)
    {
        if (node == null)
        {
            return null;
        }
        if (node is MeshInstance3D mesh && mesh.Mesh != null)
        {
            for (int i = 0; i < mesh.Mesh.GetSurfaceCount(); i++)
            {
                if (mesh.Mesh.SurfaceGetMaterial(i) is not ShaderMaterial built
                    || !names.TryGetValue(built.GetInstanceId(), out string? texture)
                    || texture.Contains("hilite", System.StringComparison.OrdinalIgnoreCase) != hilite)
                {
                    continue;
                }
                if (mesh.GetSurfaceOverrideMaterial(i) is ShaderMaterial live)
                {
                    return live.GetShaderParameter("albedo_tex").As<Texture2D>();
                }
            }
        }
        foreach (var child in node.GetChildren())
        {
            if (child is Node3D n3d && AlbedoOf(n3d, names, hilite) is { } hit)
            {
                return hit;
            }
        }
        return null;
    }

    // The first mesh at or under this node, for a geometry assertion about it.
    internal static MeshInstance3D? FirstMesh(Node3D root)
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

    // The first node in the subtree carrying this ORIGINAL gamez name (SceneBuilder sanitizes and
    // Godot renames duplicate siblings, so Node.Name is not the name the data uses).
    internal static Node3D? FindNamed(Node3D root, string name)
    {
        if (AnimRuntime.NameOf(root).Equals(name, System.StringComparison.OrdinalIgnoreCase))
        {
            return root;
        }
        foreach (var child in root.GetChildren())
        {
            if (child is Node3D n3d && FindNamed(n3d, name) is { } hit)
            {
                return hit;
            }
        }
        return null;
    }

    // How many MeshInstance3D in the subtree carry a material with an albedo
    // texture, the glTF importer hands each surface back a StandardMaterial3D.
    internal static int CountTexturedMeshes(Node node)
    {
        int count = 0;
        if (node is MeshInstance3D mesh)
        {
            for (int i = 0; i < mesh.GetSurfaceOverrideMaterialCount(); i++)
            {
                if (mesh.GetActiveMaterial(i) is BaseMaterial3D { AlbedoTexture: not null })
                {
                    count++;
                    break;
                }
            }
        }
        foreach (var child in node.GetChildren())
        {
            count += CountTexturedMeshes(child);
        }
        return count;
    }

    /// <summary>Which way the triangles in a subtree are wound, as one number: every surface's
    /// signed volume about its own centroid, summed, so an off-origin part cannot swamp the sum.
    /// ⚠ Only the SIGN carries meaning, and only against another mesh's: it is positive for one
    /// winding and negative for the other, with the sense of "positive" left to the caller's
    /// reference (a Godot <c>BoxMesh</c> is the convenient one). The magnitude is an artefact of
    /// how large the parts are.</summary>
    internal static double WindingSense(Node node)
    {
        double sense = node is MeshInstance3D { Mesh: { } mesh } ? WindingSense(mesh) : 0.0;
        foreach (var child in node.GetChildren())
        {
            sense += WindingSense(child);
        }
        return sense;
    }

    /// <summary>One mesh's contribution to <see cref="WindingSense(Node)"/>, over its triangle
    /// surfaces; a surface that is not an indexed triangle list contributes nothing.</summary>
    internal static double WindingSense(Mesh mesh)
    {
        double sense = 0.0;
        for (int surface = 0; surface < mesh.GetSurfaceCount(); surface++)
        {
            if (mesh is ArrayMesh array && array.SurfaceGetPrimitiveType(surface) != Mesh.PrimitiveType.Triangles)
            {
                continue;
            }
            var arrays = mesh.SurfaceGetArrays(surface);
            var vertices = arrays[(int)Mesh.ArrayType.Vertex].As<Vector3[]>();
            var indices = arrays[(int)Mesh.ArrayType.Index].As<int[]>();
            if (indices.Length == 0)
            {
                // An unindexed surface winds in vertex order; the identity index list reads it the
                // same way as an indexed one.
                indices = new int[vertices.Length];
                for (int v = 0; v < indices.Length; v++)
                {
                    indices[v] = v;
                }
            }
            if (vertices.Length == 0 || indices.Length < 3)
            {
                continue;
            }
            var centre = Vector3.Zero;
            foreach (var vertex in vertices)
            {
                centre += vertex;
            }
            centre /= vertices.Length;
            for (int t = 0; t + 2 < indices.Length; t += 3)
            {
                var a = vertices[indices[t]] - centre;
                var b = vertices[indices[t + 1]] - centre;
                var c = vertices[indices[t + 2]] - centre;
                sense += a.Dot(b.Cross(c));
            }
        }
        return sense;
    }

    internal static int CountMeshesWithCullMode(Node node, BaseMaterial3D.CullModeEnum cullMode)
    {
        int count = 0;
        if (node is MeshInstance3D mesh)
        {
            for (int surface = 0; surface < mesh.GetSurfaceOverrideMaterialCount(); surface++)
            {
                if (mesh.GetActiveMaterial(surface) is not BaseMaterial3D { CullMode: var actual } || actual != cullMode)
                {
                    continue;
                }
                count++;
                break;
            }
        }
        foreach (var child in node.GetChildren())
        {
            count += CountMeshesWithCullMode(child, cullMode);
        }
        return count;
    }

    // ---- needs a chapter world ------------------------------------------------------------------

    // The four every-chapter censuses in one pass. The world build is nearly the whole cost of
    // each, and four suites building the same eight chapters pay it four times over.
    [Suite("chapter-census",
        "every chapter's built world, once each with collision: nothing a chapter hides is left "
        + "solid (no enabled collider under an invisible node), the ground answers a ray from "
        + "above at each partition-cell centre and its one-sided part answers nothing from below, "
        + "the destructible registry holds the pinned instance and node-group totals with the "
        + "two 8-inch cannons booting standing, and every mip-mapped albedo sampler in the world "
        + "fetches through the one function carrying the chapter's authored mip LOD bias")]
    internal static void ChapterCensus(TestContext ctx)
    {
        var report = new System.Text.StringBuilder();
        foreach (var (chapter, expectAbove, expectBelow) in GroundCensus)
        {
            var (_, instances, anchors) = Census.First(c => c.Chapter == chapter);
            ctx.WithWorld(chapter, collision: true, world =>
            {
                CheckNothingHiddenIsSolid(ctx, chapter, world);
                CheckGroundSidedness(ctx, chapter, world, expectAbove, expectBelow, report);
                CheckDestructibleTotals(ctx, chapter, world, instances, anchors);
                CheckMipBiasReachesEveryArm(ctx, chapter, world, report);
            });
        }

        ctx.WriteArtifact("test-chapter-census.txt", report.ToString());
    }

    // The invisible-wall tripwire: after a chapter's world has bootstrapped, mission
    // setup script, RESET_STATEs, ON_STARTUP, the unplaced sweep, no collider may still be
    // enabled where nothing is drawn. Every chapter, because what each mission hides differs and
    // the failure is silent until someone flies into it (C1/IA1's `hk_zep`).
    internal static void CheckNothingHiddenIsSolid(TestContext ctx, string chapter, TestWorld world)
    {
        var solid = Probes.InvisibleEnabledColliders(world.Session.Root);
        ctx.Same(0, solid.Count, $"{chapter} invisible-but-solid colliders");
        for (int i = 0; i < solid.Count && i < 8; i++)
        {
            ctx.Note($"{chapter} solid where nothing is drawn: {solid[i]}");
        }
    }

    // The ground's sidedness at the centre of every world partition cell: a ray from above must
    // still meet a collider, and one from below must pass through wherever the source polygon
    // clears SHOW_BACKFACE. The first column is the tripwire against the failure a per-polygon
    // sidedness rule risks, an aircraft falling through the map; the second is the contact the
    // original never has, and CM12's ace is stuck under it.
    internal static void CheckGroundSidedness(TestContext ctx, string chapter, TestWorld world,
        int expectAbove, int expectBelow, System.Text.StringBuilder report)
    {
        var grid = world.Gamez.FindByName("world1");
        ctx.Check(grid?.PartitionCellX > 0f && grid.PartitionCols > 0,
            $"{chapter} carries a partition grid to sample over");
        if (grid?.PartitionCellX is not > 0f)
            return;

        var space = ctx.Host.GetWorld3D().DirectSpaceState;
        int above = 0, below = 0;
        for (int row = 0; row < grid.PartitionRows; row++)
        {
            for (int col = 0; col < grid.PartitionCols; col++)
            {
                float x = grid.PartitionOriginX + ((col + 0.5f) * grid.PartitionCellX);
                float z = grid.PartitionOriginZ - ((row + 0.5f) * grid.PartitionCellZ);
                var down = space.IntersectRay(PhysicsRayQueryParameters3D.Create(
                    new Vector3(x, GroundProbeCeilingM, z),
                    new Vector3(x, GroundProbeFloorM, z), CollisionLayers.World));
                if (down.Count == 0)
                    continue;
                above++;
                // From just under whatever the downward ray found, so the second ray tests
                // that same surface rather than one a whole map's depth away.
                float surfaceY = down["position"].AsVector3().Y;
                var up = space.IntersectRay(PhysicsRayQueryParameters3D.Create(
                    new Vector3(x, surfaceY - GroundProbeStandoffM, z),
                    new Vector3(x, surfaceY + GroundProbeStandoffM, z), CollisionLayers.World));
                if (up.Count > 0)
                    below++;
            }
        }

        int cells = grid.PartitionRows * grid.PartitionCols;
        report.AppendLine($"{chapter}: {cells} cells, {above} answer from above, {below} of those also from below");
        ctx.Same(cells, above, $"{chapter} cells whose ground stops a ray from above");
        ctx.Same(expectAbove, above, $"{chapter} ground solid from above");
        ctx.Same(expectBelow, below, $"{chapter} of those also solid from below");
        ctx.Note($"{chapter} ground: {above}/{cells} cells solid from above, {below} also solid from below");
    }

    internal static void CheckDestructibleTotals(TestContext ctx, string chapter, TestWorld world,
        int instances, int anchors)
    {
        // The registry totals, never the swept rows, the sweep is capped at
        // Probes.SweepCap and would silently under-count.
        var registry = world.Runtime.Destructibles;
        ctx.Same(instances, registry.Count, $"{chapter} destructible instances");
        ctx.Same(anchors, registry.DistinctAnchors, $"{chapter} destructible node groups");

        // The two 8-inch cannons are the whole install's only pools whose RESET_STATE
        // switches `dbase` ON beside `healthy` ACTIVE, so they are where a base read as
        // half a death boots destroyed and swallows every shot.
        foreach (var gun in registry.All.Where(i =>
            AnimRuntime.NameOf(i.Anchor).StartsWith("8igun", System.StringComparison.OrdinalIgnoreCase)))
        {
            ctx.Check(gun.Status == DestructibleRegistry.State.Healthy
                      && gun.Health == gun.MaxHealth,
                $"{chapter} {AnimRuntime.NameOf(gun.Anchor)} boots standing hp={gun.Health}/{gun.MaxHealth} state={gun.Status}");
        }
    }

    // The original applies the chapter's authored mip LOD bias as one device render state, so every
    // texture sample on the device takes it. Here that is one global read by one function, and an
    // arm sampling around it draws its chapter at a level the original never chose. Able to fail:
    // any arm reverted to a bare texture(albedo_tex, ...) shows up as an unbiased sampler.
    internal static void CheckMipBiasReachesEveryArm(TestContext ctx, string chapter, TestWorld world,
        System.Text.StringBuilder report)
    {
        var shaders = new HashSet<Shader>();
        CollectShaders(world.Session.Root, shaders);
        var biased = new Dictionary<string, int>(System.StringComparer.Ordinal);
        var unbiased = new Dictionary<string, int>(System.StringComparer.Ordinal);
        foreach (var shader in shaders)
        {
            string code = shader.Code;
            if (!code.Contains("filter_linear_mipmap", System.StringComparison.Ordinal))
            {
                continue;
            }

            var into = code.Contains("csky_sample_albedo", System.StringComparison.Ordinal)
                ? biased : unbiased;
            string arm = ArmOf(code);
            into.TryGetValue(arm, out int had);
            into[arm] = had + 1;
        }

        int total = biased.Values.Sum();

        // The control: a world that resolved to no mip-mapped shader at all would satisfy the
        // count below vacuously.
        ctx.Check(total > 0, $"{chapter} builds mip-mapped world shaders count={total}");
        ctx.Same(0, unbiased.Values.Sum(), $"{chapter} mip-mapped samplers taking no chapter bias");
        foreach (var (arm, count) in unbiased)
        {
            ctx.Note($"{chapter} unbiased arm: {arm} shaders={count}");
        }

        report.AppendLine($"{chapter}: biased {Tally(biased)}; unbiased {Tally(unbiased)}");
    }

    // ---- needs Godot's Image, nothing else -------------------------------------------------------

    [Suite("tex-dropin", "the census/override flatten repaints RGB and changes nothing else")]
    internal static void TexDropIn(TestContext ctx)
    {
        string texturesPath = SessionPaths.ChapterTextures(ctx.DataRoot, "C1");
        ctx.RequireData(texturesPath, $"C1 textures");
        using var textures = new TextureArchive(texturesPath);
        var colors = new Dictionary<string, Color>();
        foreach (string name in DropInSamples)
        {
            var img = textures.FindImage(name);
            ctx.Check(img != null, $"sample texture resolves texture={name}");
            if (img == null)
            {
                continue;
            }
            int w = img.GetWidth(), h = img.GetHeight();
            var format = img.GetFormat();
            bool hadAlpha = format is Image.Format.Rgba8 or Image.Format.La8 or Image.Format.Rgba4444;
            byte[] before = img.GetData();

            var color = TextureDropIn.ColorForName(name);
            colors[name] = color;
            TextureDropIn.Flatten(img, color);
            byte[] after = img.GetData();

            ctx.Check(img.GetWidth() == w && img.GetHeight() == h,
                $"flatten keeps the size texture={name} before={w}x{h} after={img.GetWidth()}x{img.GetHeight()}");
            ctx.Check(!img.HasMipmaps(), $"flatten adds no mipmaps texture={name}");
            var flatFormat = img.GetFormat();
            ctx.Check(flatFormat == (hadAlpha ? Image.Format.Rgba8 : Image.Format.Rgb8),
                $"flatten lands in the three-channel form of the original texture={name} was={format} now={flatFormat}");

            int stride = flatFormat == Image.Format.Rgba8 ? 4 : 3;
            ctx.Same(w * h * stride, after.Length, $"{name} flattened byte count");
            int wrongRgb = 0, wrongAlpha = 0;
            for (int i = 0; i + stride <= after.Length; i += stride)
            {
                if (after[i] != color.R8 || after[i + 1] != color.G8 || after[i + 2] != color.B8)
                {
                    wrongRgb++;
                }
                // Only comparable when the source was already the same layout; a converted source
                // has no byte-for-byte predecessor to check against.
                if (stride == 4 && format == Image.Format.Rgba8 && after[i + 3] != before[i + 3])
                {
                    wrongAlpha++;
                }
            }
            ctx.Same(0, wrongRgb, $"{name} texels not repainted to the flat colour");
            ctx.Same(0, wrongAlpha, $"{name} texels whose alpha the flatten moved");
        }
        // The identity every count report depends on: no two of these names share a colour, and
        // each keeps one channel pinned to full brightness.
        var seen = new Dictionary<string, string>();
        foreach (var (name, color) in colors)
        {
            string key = $"{color.R8},{color.G8},{color.B8}";
            ctx.Check(!seen.ContainsKey(key), $"census colour is unique texture={name} colour={key} clashes_with={(seen.TryGetValue(key, out var other) ? other : "-")}");
            seen[key] = name;
            ctx.Check(color.R8 == 255 || color.G8 == 255 || color.B8 == 255,
                $"census colour is full brightness texture={name} colour={key}");
        }
        ctx.Note($"{colors.Count} sample textures flattened, {seen.Count} distinct colours");
    }

    // templates.zrd's substitute and scale_range, asserted as an A/B against the build that does not
    // read the spec at all: the same ClutterBuilder over the same gamez, differing only in whether a
    // spec was handed to it. Three builds, each with its own able-to-fail control: bare, dressed, and
    // dressed again in the SAME process with no Rng reset, which is the strong form because it proves
    // the stream is a function of the data alone. See docs/org/clutter.md.
    // ⚠ Never seed it from Rng.Master; that rerolls C1's forest on every unpinned launch.
    [Suite("clutter-determinism",
        "templates.zrd's substitute + scale_range move C1's species mix and sizes without changing the instance total, and two builds of the same chapter are identical transform for transform")]
    internal static void ClutterDeterminism(TestContext ctx)
    {
        const string chapter = "C1";
        string texturesPath = SessionPaths.ChapterTextures(ctx.DataRoot, chapter);
        string gamezPath = SessionPaths.ChapterGamez(ctx.DataRoot, chapter);
        ctx.RequireData(texturesPath, $"{chapter} textures");
        ctx.RequireData(gamezPath, $"{chapter} gamez");
        ctx.RequireData(ctx.InterpPath, $"interp.json");

        var gamez = GameZ.Load(gamezPath);
        using var textures = new TextureArchive(texturesPath);
        var names = ClutterBuilder.TemplateNames(ctx.InterpPath, chapter);
        var props = ClutterTemplateSpec.Load(SessionPaths.ChapterZrdr(ctx.DataRoot, chapter));
        ctx.Check(props != null && props.Kinds.Count > 0, $"{chapter} templates.zrd read");

        // Each build's kinds as (label → placements), plus the flat transform list in kind order.
        static (Dictionary<string, int> ByKind, List<Transform3D> Placements, int Total) Take(
            ClutterBuilder builder, IReadOnlyList<string> names)
        {
            var root = builder.Build(names);
            var byKind = new Dictionary<string, int>(System.StringComparer.Ordinal);
            var placements = new List<Transform3D>();
            foreach (var kind in builder.ExportedKinds ?? System.Array.Empty<ClutterBuilder.KindExport>())
            {
                byKind.TryGetValue(kind.Texture, out int had);
                byKind[kind.Texture] = had + kind.Placements.Count;
                placements.AddRange(kind.Placements);
            }
            int total = builder.InstanceCount + builder.SolidCount;
            root?.Free();
            return (byKind, placements, total);
        }

        var bare = Take(new ClutterBuilder(gamez, textures), names);
        var dressed = Take(new ClutterBuilder(gamez, textures, null, props), names);
        var again = Take(new ClutterBuilder(gamez, textures, null, props), names);

        ctx.Check(bare.Total > 0, $"the bare build placed something total={bare.Total}");
        ctx.Same(bare.Total, dressed.Total, $"{chapter} instance total is unchanged by substitution");

        // The mix moved, and in the authored direction: firtree1 sheds a tenth of its stamps to
        // firtree2. Asserted as a direction plus a band rather than as an exact count, so the
        // suite survives a re-seed but still fails if the roll stops happening or inverts.
        bare.ByKind.TryGetValue("firtree1.tif", out int bareFir1);
        dressed.ByKind.TryGetValue("firtree1.tif", out int dressedFir1);
        bare.ByKind.TryGetValue("firtree2.tif", out int bareFir2);
        dressed.ByKind.TryGetValue("firtree2.tif", out int dressedFir2);
        int moved = bareFir1 - dressedFir1;
        ctx.Check(moved > 0 && dressedFir2 - bareFir2 == moved,
            $"firtree1 sheds stamps and firtree2 gains exactly those lost={moved} gained={dressedFir2 - bareFir2}");
        ctx.Check(moved > bareFir1 * 0.08f && moved < bareFir1 * 0.12f,
            $"the shed fraction is the authored 9:1 moved={moved} of {bareFir1}");

        // Scale: uniform, inside C1's widest authored band, and actually varying. A build that
        // silently dropped the draw would pass every count assertion above.
        float lo = float.MaxValue, hi = float.MinValue;
        foreach (var xf in dressed.Placements)
        {
            var s = xf.Basis.Scale;
            ctx.Check(Mathf.Abs(s.X - s.Y) < 1e-5f && Mathf.Abs(s.X - s.Z) < 1e-5f,
                $"the scale is uniform scale={s}");
            lo = Mathf.Min(lo, s.X);
            hi = Mathf.Max(hi, s.X);
        }
        foreach (var xf in bare.Placements)
        {
            ctx.Check(Mathf.Abs(xf.Basis.Scale.X - 1f) < 1e-5f,
                $"the bare build leaves every instance at its authored size scale={xf.Basis.Scale.X}");
        }
        ctx.Check(lo >= 0.9f - 1e-4f && hi <= 1.5f + 1e-4f, $"scales stay inside C1's authored 0.9-1.5 lo={lo} hi={hi}");
        ctx.Check(hi - lo > 0.4f, $"scales actually vary lo={lo} hi={hi}");

        // The strong form: same process, no reseed, transform for transform.
        ctx.Same(dressed.Placements.Count, again.Placements.Count, $"the second dressed build placed the same count");
        int drift = 0;
        for (int i = 0; i < Mathf.Min(dressed.Placements.Count, again.Placements.Count); i++)
        {
            if (dressed.Placements[i] != again.Placements[i])
            {
                drift++;
            }
        }
        ctx.Same(0, drift, $"two dressed builds are identical transform for transform");
        ctx.Note($"{chapter} bare firtree1={bareFir1} firtree2={bareFir2}; dressed firtree1={dressedFir1} firtree2={dressedFir2}; scales {lo:0.000}-{hi:0.000}");
    }

    // A decoration model is a node chain, and the mesh node under its `.flt` top may translate.
    // C5's w_lightglow sits 4.75 m up, the lamp head's height. Asserted as an A/B against the same
    // build with that chain transform cleared. The control both fails able and shows the move is
    // confined to the glow.
    [Suite("clutter-mesh-lift",
        "C5's lamp glow stamps 4.75 m up its own decoration chain, and clearing that chain moves the glow alone")]
    internal static void ClutterMeshLift(TestContext ctx)
    {
        const string chapter = "C5";
        const string template = "cblock7";   // the district carrying lightpole and its poleflare glow
        string texturesPath = SessionPaths.ChapterTextures(ctx.DataRoot, chapter);
        string gamezPath = SessionPaths.ChapterGamez(ctx.DataRoot, chapter);
        ctx.RequireData(texturesPath, $"{chapter} textures");
        ctx.RequireData(gamezPath, $"{chapter} gamez");

        var gamez = GameZ.Load(gamezPath);
        using var textures = new TextureArchive(texturesPath);
        var props = ClutterTemplateSpec.Load(SessionPaths.ChapterZrdr(ctx.DataRoot, chapter));

        var root = ClutterBuilder.FindTemplateRoot(gamez, template);
        ctx.Check(root != null, $"{chapter} carries the {template} template");
        var ground = root == null ? null : ClutterBuilder.FirstWithMesh(gamez, root);
        ctx.Check(ground != null, $"{template} has a ground quad node");
        if (ground == null)
        {
            return;
        }

        // Every decoration of this template whose chain translates, with the mesh node it ends on.
        var chains = new List<(GameZNode Node, Transform3D Local, string Deco)>();
        foreach (var childIndex in ground.Children)
        {
            var deco = gamez.Nodes[childIndex];
            if (ClutterBuilder.FirstWithMesh(gamez, deco, false, out var toMesh) is not { } meshNode
                || toMesh.Origin == Vector3.Zero)
            {
                continue;
            }
            // One hop in the shipped data, which is what makes clearing the mesh node's own
            // transform the exact control for dropping the chain.
            ctx.Check(meshNode.Local != null && meshNode.Local.Value.Origin == toMesh.Origin,
                $"{deco.Name}'s chain is one hop deco={toMesh.Origin} mesh={meshNode.Local?.Origin}");
            chains.Add((meshNode, meshNode.Local ?? Transform3D.Identity, deco.Name));
            ctx.Check(toMesh.Origin == new Vector3(0f, 4.75f, 0f),
                $"{deco.Name} lifts its mesh to the lamp head lift={toMesh.Origin}");
        }
        ctx.Check(chains.Count > 0, $"{template} authors a translating decoration chain count={chains.Count}");

        static Dictionary<string, List<Transform3D>> Take(ClutterBuilder builder, string name)
        {
            var built = builder.Build(new[] { name });
            var byKind = new Dictionary<string, List<Transform3D>>(System.StringComparer.Ordinal);
            foreach (var kind in builder.ExportedKinds ?? System.Array.Empty<ClutterBuilder.KindExport>())
            {
                if (!byKind.TryGetValue(kind.Texture, out var list))
                {
                    byKind[kind.Texture] = list = new List<Transform3D>();
                }
                list.AddRange(kind.Placements);
            }
            built?.Free();
            return byKind;
        }

        var lifted = Take(new ClutterBuilder(gamez, textures, null, props), template);
        foreach (var (node, _, _) in chains)
        {
            node.Local = Transform3D.Identity;
        }
        var dropped = Take(new ClutterBuilder(gamez, textures, null, props), template);
        foreach (var (node, local, _) in chains)
        {
            node.Local = local;
        }

        ctx.Same(dropped.Count, lifted.Count, $"both builds export the same kinds");
        int moved = 0, wrongHeight = 0, wrongGround = 0, unmoved = 0;
        foreach (var (label, after) in lifted)
        {
            dropped.TryGetValue(label, out var before);
            if (before == null || before.Count != after.Count)
            {
                ctx.Check(false, $"kind {label} kept its stamp count before={before?.Count ?? -1} after={after.Count}");
                continue;
            }
            for (int i = 0; i < after.Count; i++)
            {
                var delta = after[i].Origin - before[i].Origin;
                if (delta == Vector3.Zero)
                {
                    unmoved++;
                    continue;
                }
                moved++;
                // The centimetre band is float32 headroom, not slack: C5's stamps reach kilometres
                // out, where a single-precision metre carries about a millimetre of spacing.
                if (Mathf.Abs(delta.Y - 4.75f) > 0.01f)
                {
                    wrongHeight++;
                }
                if (delta.X != 0f || delta.Z != 0f)
                {
                    wrongGround++;
                }
            }
        }

        lifted.TryGetValue("poleflare.tif", out var glows);
        dropped.TryGetValue("lightpole.tif", out var poles);
        ctx.Check(glows != null && glows.Count > 0, $"the glow kind stamped something count={glows?.Count ?? 0}");
        ctx.Same(glows?.Count ?? 0, moved, $"exactly the glow stamps moved");
        ctx.Same(0, wrongHeight, $"every moved stamp rose the authored 4.75 m");
        ctx.Same(0, wrongGround, $"no moved stamp changed its ground position");
        ctx.Check(unmoved > 0, $"the other kinds stamped and stayed put count={unmoved}");
        ctx.Note($"{chapter}/{template}: {moved} glow stamps lifted 4.75 m, {unmoved} stamps unchanged, poles={poles?.Count ?? 0}");
    }

    // ⚠ A card's pose reads the eye's POSITION and never its basis. A billboard assembled from
    // INV_VIEW_MATRIX's columns carries the camera's roll into every stamp, which is the one thing
    // the original's per-card tracker cannot do (docs/org/cloudCards.md). Asserted on the
    // emitted shader text, because a vertex-stage rotation leaves no trace in the built scene.
    [Suite("clutter-card-pose",
        "C5's poleflare glow stamps pose through the facade look-at and never through the camera's own "
        + "basis, while the lightpole cards beside them keep the cylindrical pose")]
    internal static void ClutterCardPose(TestContext ctx)
    {
        const string chapter = "C5";
        const string template = "cblock7";   // the district carrying lightpole and its poleflare glow
        string texturesPath = SessionPaths.ChapterTextures(ctx.DataRoot, chapter);
        string gamezPath = SessionPaths.ChapterGamez(ctx.DataRoot, chapter);
        ctx.RequireData(texturesPath, $"{chapter} textures");
        ctx.RequireData(gamezPath, $"{chapter} gamez");

        var gamez = GameZ.Load(gamezPath);
        using var textures = new TextureArchive(texturesPath);
        var props = ClutterTemplateSpec.Load(SessionPaths.ChapterZrdr(ctx.DataRoot, chapter));
        var clutter = new ClutterBuilder(gamez, textures, null, props);
        var built = clutter.Build(new[] { template });

        var code = new Dictionary<string, string>(System.StringComparer.Ordinal);
        int examined = 0;
        foreach (var kind in clutter.ExportedKinds ?? System.Array.Empty<ClutterBuilder.KindExport>())
        {
            if (kind.Solid || kind.Material is not ShaderMaterial { Shader: { } shader })
            {
                continue;
            }
            code[kind.Texture] = shader.Code;
            ctx.Check(!shader.Code.Contains("INV_VIEW_MATRIX[0]", System.StringComparison.Ordinal)
                      && !shader.Code.Contains("INV_VIEW_MATRIX[1]", System.StringComparison.Ordinal)
                      && !shader.Code.Contains("INV_VIEW_MATRIX[2]", System.StringComparison.Ordinal),
                $"{kind.Texture} builds no basis from the camera's columns");
            examined++;
        }
        built?.Free();

        ctx.Check(examined > 0, $"{template} exports card kinds count={examined}");
        bool hasGlow = code.TryGetValue("poleflare.tif", out var glow);
        bool hasPole = code.TryGetValue("lightpole.tif", out var pole);
        ctx.Check(hasGlow, $"{template} stamps the poleflare glow card");
        ctx.Check(hasPole, $"{template} stamps the lightpole card");
        if (!hasGlow || !hasPole)
        {
            return;
        }

        ctx.Check(glow!.Contains("csky_facade_spherical(origin, CAMERA_POSITION_WORLD)", System.StringComparison.Ordinal),
            $"the glow card poses through the facade look-at");
        ctx.Check(glow.Contains("res://shaders/csky_facade.gdshaderinc", System.StringComparison.Ordinal),
            $"the glow card's shader includes the facade block it calls");
        // The post is the control, the same emitter one flag apart. Without it a glow reading
        // right while the post lost its own pose would pass unseen.
        ctx.Check(!pole!.Contains("csky_facade_spherical", System.StringComparison.Ordinal),
            $"the lightpole card takes no spherical pose");
        ctx.Check(pole.Contains("CAMERA_POSITION_WORLD.xz - origin.xz", System.StringComparison.Ordinal),
            $"the lightpole card still spins about its own vertical");
        ctx.Note($"{chapter}/{template}: {examined} card kinds examined, glow and post read apart");
    }

    // The DirectionalLight3D is pointed by the flown zone's authored SUNLIGHT_ORIENTATION and keeps
    // following it when the camera's weather state moves to another zone. The CSVM.Tests units pin the
    // parse and the euler-to-direction mapping; neither can see the light wired to the wrong seam, or
    // a zone edge firing without carrying it, and a screenshot cannot either.
    // ⚠ Do not simplify this onto C1/IA1. C2/MP2 is the only shape in the install whose two zones
    // author different bearings, so anywhere else a zone change would pass with the write deleted.
    [Suite("sun-orientation",
        "the world's light wears the flown zone's authored SUNLIGHT_ORIENTATION, and follows it across a zone change (BL-324)")]
    internal static void SunOrientation(TestContext ctx)
    {
        string zrdr = SessionPaths.MissionZrdr(ctx.DataRoot, "C2", "MP2");
        ctx.RequireData(zrdr, $"C2/MP2 mission zrdr");

        var sun = new DirectionalLight3D { Name = "sun-orientation-probe" };
        ctx.Host.AddChild(sun);
        var camera = new Camera3D { Name = "sun-orientation-camera" };
        ctx.Host.AddChild(camera);
        try
        {
            // No --sky-zone: an explicit one disarms the state machine outright, which
            // would make the zone change below unobservable. The default request is zone2, and with
            // no horizon geometry to correct it that is what the flight builds with.
            var spec = SessionSpec.Parse(new[] { "--chapter=C2", "--mission=MP2" });
            var rig = new PlayerRig { Index = 0, Camera = camera, HudParent = ctx.Host };
            var rigs = new List<PlayerRig> { rig };

            var weatherRig = new WeatherRig(spec, ctx.Host, sun);
            weatherRig.Build(zrdr, rigs, System.Array.Empty<HorizonZone>(), _ => { });
            ctx.Check(NearDegrees(sun.RotationDegrees, -25f, 90f),
                $"C2/MP2 builds at ZONE2's bearing (got {sun.RotationDegrees.X:0.#}°/{sun.RotationDegrees.Y:0.#}°)");

            // Below the cloud band (19024–20124 m) the camera is in weather state 1, so the edge
            // trigger swaps to ZONE1, and the light must ride along. Ticked twice: the first Tick
            // publishes the rig's new state, and the swap is asserted after it has settled.
            camera.Position = new Vector3(0f, 0f, 0f);
            weatherRig.Tick(rigs);
            ctx.Same(1, rig.CameraWeatherState, $"camera below the band is in weather state 1");
            ctx.Check(NearDegrees(sun.RotationDegrees, -65f, 90f),
                $"a zone change carries the sun to ZONE1's bearing (got {sun.RotationDegrees.X:0.#}°/{sun.RotationDegrees.Y:0.#}°)");

            // ...and back. A one-way test would pass on a light that moved once and stuck.
            camera.Position = new Vector3(0f, 25000f, 0f);
            weatherRig.Tick(rigs);
            ctx.Same(2, rig.CameraWeatherState, $"camera above the band is in weather state 2");
            ctx.Check(NearDegrees(sun.RotationDegrees, -25f, 90f),
                $"and back to ZONE2's on the return crossing (got {sun.RotationDegrees.X:0.#}°/{sun.RotationDegrees.Y:0.#}°)");

            // The other half, asserted where it would regress: shadow mapping stays off, so this
            // light cannot cast the raked plane-on-plane shadows the original never draws (the
            // real ground shadow is drawn elsewhere).
            ctx.Check(!sun.ShadowEnabled, $"the world light casts no shadow map");
        }
        finally
        {
            sun.QueueFree();
            camera.QueueFree();
        }
    }

    // Degrees, not radians, and a loose epsilon: the assertion is "this is the authored bearing",
    // not "this is bit-identical to a round trip through Basis".
    internal static bool NearDegrees(Vector3 rotationDegrees, float pitch, float yaw)
        => Mathf.Abs(rotationDegrees.X - pitch) < 0.1f && Mathf.Abs(rotationDegrees.Y - yaw) < 0.1f;

    // The world's light takes the flown zone's authored SUNLIGHT as its ENERGY, not only its
    // bearing. The CSVM.Tests units pin both mappings; neither can see the write missing from the
    // zone apply, and a plane lit at one level everywhere is what that looks like.
    // ⚠ C1B against C1C is the install's OWN night/day pair. Do not fold this onto one mission:
    // two zones of one mission differ by cloud layer, which is not the difference under test.
    [Suite("sun-energy",
        "the world's light takes its energy from the flown zone's authored SUNLIGHT, so C1B's night mission gets a dimmer scene sun than C1C's daylight, the ambient fill is colour-sourced from the zone's own SUNLIGHT_COLOR_AMBIENT rather than the sky, and a zone change carries the new energy (BL-332)")]
    internal static void SunEnergy(TestContext ctx)
    {
        string nightZrdr = SessionPaths.MissionZrdr(ctx.DataRoot, "C1B", "IA1");
        string dayZrdr = SessionPaths.MissionZrdr(ctx.DataRoot, "C1C", "M01");
        string edgeZrdr = SessionPaths.MissionZrdr(ctx.DataRoot, "C1C", "MP1");
        ctx.RequireData(nightZrdr, $"C1B/IA1 mission zrdr");
        ctx.RequireData(dayZrdr, $"C1C/M01 mission zrdr");
        ctx.RequireData(edgeZrdr, $"C1C/MP1 mission zrdr");

        var night = ZoneEnergies(ctx, "C1B", "IA1", nightZrdr);
        var day = ZoneEnergies(ctx, "C1C", "M01", dayZrdr);
        (float nightSun, float nightAmbient) = (night.Sun, night.Ambient);
        (float daySun, float dayAmbient) = (day.Sun, day.Ambient);
        ctx.Note($"C1B/IA1 night sun {nightSun:0.000} ambient {nightAmbient:0.000}; C1C/M01 day sun {daySun:0.000} ambient {dayAmbient:0.000}");
        ctx.Check(nightSun < daySun * 0.5f,
            $"C1B's night zone lights the aircraft under half as hard as C1C's day zone");
        ctx.Check(nightAmbient < dayAmbient * 0.5f,
            $"and its ambient fill is under half of C1C's too");
        // The energy counts only where the renderer reads it, which is a colour-sourced ambient.
        // On the sky source Godot takes the fill off the procedural cubemap. Both the colour
        // and the energy written here are ignored (docs/verification.md WORLD-32).
        ctx.Check(night.Source == Godot.Environment.AmbientSource.Color,
            $"the zone apply leaves the Environment's ambient colour-sourced (got {night.Source})");
        ctx.Check(night.Color.IsEqualApprox(night.Authored),
            $"and carries the zone's own authored SUNLIGHT_COLOR_AMBIENT {night.Authored.ToHtml(false)} (got {night.Color.ToHtml(false)})");
        if (!CSVM.Utils.GraphicsMode.Enhanced)
        {
            // The faithful mapping's own numbers, so a drifting factor is caught here and not only
            // by a moved golden.
            ctx.Check(Mathf.IsEqualApprox(nightSun, 0.64f) && Mathf.IsEqualApprox(daySun, 1.6f),
                $"the faithful mapping resolves 0.64 at C1B and 1.6 at C1C (got {nightSun:0.000}/{daySun:0.000})");
        }

        // The other half: an energy written once at build is not the same as one that follows the
        // camera. C1C/MP1 builds on its dim ZONE2 and crosses to its bright ZONE1 below the band.
        var sun = new DirectionalLight3D { Name = "sun-energy-edge-probe" };
        var camera = new Camera3D { Name = "sun-energy-edge-camera" };
        ctx.Host.AddChild(sun);
        ctx.Host.AddChild(camera);
        try
        {
            var spec = SessionSpec.Parse(new[] { "--chapter=C1C", "--mission=MP1" });
            var rig = new PlayerRig { Index = 0, Camera = camera, HudParent = ctx.Host };
            var rigs = new List<PlayerRig> { rig };
            var weatherRig = new WeatherRig(spec, ctx.Host, sun);
            weatherRig.Build(edgeZrdr, rigs, System.Array.Empty<HorizonZone>(), _ => { });
            float built = sun.LightEnergy;
            camera.Position = Vector3.Zero;
            weatherRig.Tick(rigs);
            ctx.Same(1, rig.CameraWeatherState, $"camera below the band is in weather state 1");
            ctx.Check(sun.LightEnergy > built * 2f,
                $"the zone change carries ZONE1's brighter energy (built {built:0.000}, now {sun.LightEnergy:0.000})");
        }
        finally
        {
            sun.QueueFree();
            camera.QueueFree();
        }
    }

    // The interior pass's own sun is AIMED where the mission points the session sun, and keeps
    // following it across a zone change. The pass holds the world's orientation, so the aim is the
    // world basis verbatim. Able to fail: a clone aimed once at build and left there, a clone
    // re-based into the interior's frame (which would swing the sun with the airframe), and a zone
    // change that moves the session sun alone.
    [Suite("cockpit-sun-bearing",
        "the cockpit pass's own sun is aimed where the mission's SUNLIGHT_ORIENTATION points, in the pass's world basis, and follows the session sun across a zone change (BL-684)")]
    internal static void CockpitSunBearing(TestContext ctx)
    {
        string litZrdr = SessionPaths.MissionZrdr(ctx.DataRoot, "C1", "IA1");
        string crossZrdr = SessionPaths.MissionZrdr(ctx.DataRoot, "C2", "MP2");
        ctx.RequireData(litZrdr, $"C1/IA1 mission zrdr");
        ctx.RequireData(crossZrdr, $"C2/MP2 mission zrdr");

        var sun = new DirectionalLight3D { Name = "cockpit-bearing-sun" };
        var camera = new Camera3D { Name = "cockpit-bearing-camera" };
        var panel = new Node3D { Name = "cockpit-bearing-panel" };
        ctx.Host.AddChild(sun);
        ctx.Host.AddChild(camera);
        ctx.Host.AddChild(panel);
        CockpitOverlay? pass = null;
        try
        {
            var env = new Godot.Environment();
            var litSpec = SessionSpec.Parse(
                new[] { "--chapter=C1", "--mission=IA1", "--sky-zone=zone1" });
            var litRig = new WeatherRig(litSpec, ctx.Host, sun, env: env);
            litRig.Build(litZrdr, System.Array.Empty<PlayerRig>(),
                System.Array.Empty<HorizonZone>(), _ => { });
            pass = CockpitOverlay.Build(ctx.Host, panel, sun, env);
            if (pass?.Sun is not { } clone)
            {
                ctx.Check(false, $"the pass clones the session sun");
                return;
            }
            litRig.RegisterExtraLighting(clone, pass.Env);
            var cam = new CameraController(camera, new CamParams(), _ => false, -1,
                PilotViewMode.Cockpit);

            pass.Sync(Basis.Identity, cam, 0f);
            var world = -sun.GlobalBasis.Z;
            var beam = -clone.GlobalBasis.Z;
            // C1's ZONE1 authors -25° pitch / 90° yaw, and the expected beam is the binary's own
            // euler→direction law rather than the reader's, so a wrong reader cannot agree with it.
            var authored = SunBeam(Mathf.DegToRad(-25f), Mathf.DegToRad(90f));
            ctx.Note($"C1/IA1 zone1: session sun {world}, interior sun {beam}");
            ctx.Check(world.AngleTo(authored) < 0.001f,
                $"the session sun takes C1's authored -25°/90° bearing off={world.AngleTo(authored):0.0000} rad");
            ctx.Check(beam.AngleTo(world) < 0.001f,
                $"the interior's own sun is aimed the same way off={beam.AngleTo(world):0.0000} rad");
            ctx.Check(beam.AngleTo(Vector3.Forward) > 0.5f,
                $"and is not left at Godot's default -Z off={beam.AngleTo(Vector3.Forward):0.000} rad");
            // The pass keeps the world's orientation, so a banked plane must not carry the sun
            // round with it: a mirror re-based into the interior's frame would swing by the yaw.
            pass.Sync(new Basis(Vector3.Up, Mathf.Pi / 2f), cam, 0f);
            ctx.Check((-clone.GlobalBasis.Z).AngleTo(world) < 0.001f,
                $"a yawed airframe leaves the interior's sun where the world has it");

            // The other half: a bearing written once at build is not one that follows the camera.
            // C2's MP2 and MP3 are the only shipped missions whose two zones disagree about the
            // bearing, -65°/90° below the band against -25°/90° inside it.
            var rigs = new List<PlayerRig>
                { new PlayerRig { Index = 0, Camera = camera, HudParent = ctx.Host } };
            var crossSpec = SessionSpec.Parse(new[] { "--chapter=C2", "--mission=MP2" });
            var crossRig = new WeatherRig(crossSpec, ctx.Host, sun, env: env);
            crossRig.Build(crossZrdr, rigs, System.Array.Empty<HorizonZone>(), _ => { });
            crossRig.RegisterExtraLighting(clone, pass.Env);
            var below = CrossedBeam(crossRig, pass, cam, rigs, Vector3.Zero, clone, sun);
            var inside = CrossedBeam(crossRig, pass, cam, rigs, new Vector3(0f, 25000f, 0f),
                clone, sun);
            ctx.Same(1, below.State, $"a camera under C2/MP2's 19,024-20,124 m band is in weather state 1");
            ctx.Same(2, inside.State, $"and one at 25,000 m is in state 2");
            ctx.Note($"C2/MP2 below band {below.Beam}, inside band {inside.Beam}");
            ctx.Check(below.Beam.AngleTo(SunBeam(Mathf.DegToRad(-65f), Mathf.DegToRad(90f))) < 0.001f,
                $"below the band the interior wears ZONE1's -65°/90°");
            ctx.Check(inside.Beam.AngleTo(SunBeam(Mathf.DegToRad(-25f), Mathf.DegToRad(90f))) < 0.001f,
                $"inside it the interior wears ZONE2's -25°/90°");
            ctx.Check(below.Beam.AngleTo(inside.Beam) > 0.6f,
                $"the crossing moved the bearing by {Mathf.RadToDeg(below.Beam.AngleTo(inside.Beam)):0.#}°");
            ctx.Check(below.Off < 0.001f && inside.Off < 0.001f,
                $"and the interior tracked the session sun through both states");
        }
        finally
        {
            if (pass == null)
                panel.QueueFree();
            pass?.QueueFree();
            camera.QueueFree();
            sun.QueueFree();
        }
    }

    // The whiteout is the air OUTSIDE the canopy: the original applies it as a fog term on the
    // world draw, which the interior never takes, so the window whites out and the panel does not.
    // Both are canvas layers here, so the order between them is what decides it, and the drawn
    // frame is out of a suite's reach (see SessionStartCoverSuites). Able to fail: a whiteout back
    // on a layer above the pass (where it painted the panel), a flare canvas left above the
    // whiteout, one overlay shared by two panes, and a --no-fog run still painting the band.
    [Suite("weather-cockpit-whiteout",
        "the cloud-band whiteout draws under each pane's own cockpit pass, so a full-opacity band whites the window out and leaves the canopy, panel and gauges clear, in cockpit view and in chase view alike, while --no-fog clears it")]
    internal static void WeatherCockpitWhiteout(TestContext ctx)
    {
        string zrdr = SessionPaths.MissionZrdr(ctx.DataRoot, "C1", "IA1");
        ctx.RequireData(zrdr, $"C1/IA1 mission zrdr");

        // The order the picture is composed in, asserted on the constants themselves: the flare
        // and the whiteout are the sky, the pass is the cockpit in front of it, and the HUD is the
        // chrome over both.
        ctx.Check(HudLayers.FlareSprites < HudLayers.Whiteout,
            $"the flare sprites draw under the whiteout, so a cloud swallows them flare={HudLayers.FlareSprites} whiteout={HudLayers.Whiteout}");
        ctx.Check(HudLayers.Whiteout < HudLayers.CockpitPass,
            $"the whiteout draws under the cockpit pass whiteout={HudLayers.Whiteout} pass={HudLayers.CockpitPass}");
        ctx.Check(HudLayers.CockpitPass < HudLayers.Hud && HudLayers.CockpitPass < HudLayers.SunWash,
            $"and the pass stays under the HUD and the sun wash pass={HudLayers.CockpitPass} hud={HudLayers.Hud} wash={HudLayers.SunWash}");

        var pane0 = new Node { Name = "whiteout-pane-0" };
        var pane1 = new Node { Name = "whiteout-pane-1" };
        var camera0 = new Camera3D { Name = "whiteout-camera-0" };
        var camera1 = new Camera3D { Name = "whiteout-camera-1" };
        var sun = new DirectionalLight3D { Name = "whiteout-sun" };
        ctx.Host.AddChild(pane0);
        ctx.Host.AddChild(pane1);
        ctx.Host.AddChild(camera0);
        ctx.Host.AddChild(camera1);
        ctx.Host.AddChild(sun);
        CockpitOverlay? pass = null;
        try
        {
            var spec = SessionSpec.Parse(new[] { "--chapter=C1", "--mission=IA1" });
            var rigs = new List<PlayerRig>
            {
                new PlayerRig { Index = 0, Camera = camera0, HudParent = pane0 },
                new PlayerRig { Index = 1, Camera = camera1, HudParent = pane1 },
            };
            var weather = new WeatherRig(spec, ctx.Host, sun);
            weather.Build(zrdr, rigs, System.Array.Empty<HorizonZone>(), _ => { });

            foreach (var rig in rigs)
            {
                var canvas = rig.Whiteout?.GetParent() as CanvasLayer;
                ctx.Check(canvas != null && canvas.Layer == HudLayers.Whiteout,
                    $"player {rig.Index}'s whiteout sits on the whiteout layer layer={canvas?.Layer}");
                ctx.Check(canvas != null && canvas.GetParent() == rig.HudParent,
                    $"and hangs under that player's own pane, so a splitscreen pane whites out alone");
                ctx.Check(rig.Whiteout != null && rig.Whiteout.AnchorRight == 1f
                    && rig.Whiteout.AnchorBottom == 1f,
                    $"and fills its pane rather than a corner of it");
            }

            // C1/IA1's band is 970-1124 m with a 1032-1062 m opaque core, so the centre is a
            // total whiteout and the flicker's own guard leaves it there untouched.
            camera0.Position = new Vector3(0f, 1047f, 0f);
            camera1.Position = Vector3.Zero;
            weather.Tick(rigs);
            float inside = rigs[0].Whiteout?.Color.A ?? -1f;
            float outside = rigs[1].Whiteout?.Color.A ?? -1f;
            ctx.Note($"band centre alpha {inside:0.000}, below the band {outside:0.000}");
            ctx.Check(Mathf.IsEqualApprox(inside, 1f),
                $"a camera in the band's core paints a total whiteout alpha={inside:0.000}");
            ctx.Check(Mathf.IsEqualApprox(outside, 0f),
                $"and the other pane, below the band, paints nothing alpha={outside:0.000}");

            // The pass over that same pane, with the whiteout at full: cockpit view draws the
            // interior over it, chase view takes the pass off and leaves the pane white.
            var panel = new Node3D { Name = "whiteout-panel" };
            pane0.AddChild(panel);
            pass = CockpitOverlay.Build(pane0, panel, null, null);
            if (pass == null)
            {
                ctx.Check(false, $"the cockpit pass builds over the pane");
                return;
            }
            var cam = new CameraController(camera0, new CamParams(), _ => false, -1,
                PilotViewMode.Cockpit);
            pass.Sync(Basis.Identity, cam, 0f);
            int whiteoutLayer = (rigs[0].Whiteout?.GetParent() as CanvasLayer)?.Layer ?? 0;
            ctx.Check(pass.Visible && pass.Layer > whiteoutLayer,
                $"in cockpit view the interior draws over the full whiteout pass={pass.Layer} whiteout={whiteoutLayer}");
            ctx.Check(pass.GetParent() == pane0,
                $"and it is this pane's own pass, not a shared one");
            pass.Deactivate();
            weather.Tick(rigs);
            ctx.Check(!pass.Visible && Mathf.IsEqualApprox(rigs[0].Whiteout?.Color.A ?? -1f, 1f),
                $"in chase view the pass is down and the whiteout covers the whole pane");

            // --no-fog covers the whiteout as it covers the fog, at the same altitude.
            var clearSpec = SessionSpec.Parse(
                new[] { "--chapter=C1", "--mission=IA1", "--no-fog" });
            var clearRigs = new List<PlayerRig>
                { new PlayerRig { Index = 0, Camera = camera0, HudParent = pane1 } };
            var clearWeather = new WeatherRig(clearSpec, ctx.Host, sun);
            clearWeather.Build(zrdr, clearRigs, System.Array.Empty<HorizonZone>(), _ => { });
            clearWeather.Tick(clearRigs);
            float cleared = clearRigs[0].Whiteout?.Color.A ?? -1f;
            ctx.Check(Mathf.IsEqualApprox(cleared, 0f),
                $"--no-fog clears the band whiteout at the same altitude alpha={cleared:0.000}");
        }
        finally
        {
            pass?.QueueFree();
            camera0.QueueFree();
            camera1.QueueFree();
            sun.QueueFree();
            pane0.QueueFree();
            pane1.QueueFree();
        }
    }

    // The lens flare's gating, chapter by chapter. ⚠ Gate it on chapter data, never on a chapter
    // name: it reads a gamez sun node in the horizon subtree and init.gw's LensFlareTexture slot
    // registrations, both true of C2 and C3 and of nothing else. Both directions are asserted, because
    // nothing about C1 looking correct would tell you a flare rig had started building there.
    // Data-only on purpose, so it stays a fast suite rather than a third eight-chapter world sweep.
    [Suite("lens-flare-gates",
        "the sun's lens flare is gated on chapter data alone, and its two independent gates (the gamez `sun` node and init.gw's LensFlareTexture slots) agree chapter by chapter, in C2 and C3 and nowhere else (BL-165)")]
    internal static void LensFlareGates(TestContext ctx)
    {
        ctx.RequireData(ctx.InterpPath, $"interp.json");
        int withFlare = 0;
        foreach (var (chapter, _, _) in Census)
        {
            bool expected = chapter is "C2" or "C3";

            var slots = LensFlareRig.FlareTextureNames(ctx.InterpPath, chapter);
            ctx.Same(expected ? 4 : 0, slots.Count, $"{chapter} LensFlareTexture slots");

            string gamezPath = SessionPaths.ChapterGamez(ctx.DataRoot, chapter);
            ctx.RequireData(gamezPath, $"{chapter} gamez");
            var gamez = GameZ.Load(gamezPath);
            int sunNodes = 0;
            foreach (var n in gamez.Nodes)
            {
                if (string.Equals(n.Name, "sun", System.StringComparison.OrdinalIgnoreCase))
                    sunNodes++;
            }

            ctx.Same(expected ? 1 : 0, sunNodes, $"{chapter} gamez sun node");
            // The gates must not merely each be right, they must AGREE. A chapter with textures
            // and no sun (or the reverse) is data telling us something we have not decoded, and
            // the rig logs a warning for exactly that case.
            ctx.Check(slots.Count > 0 == sunNodes > 0, $"{chapter} both flare gates agree");
            if (expected)
            {
                withFlare++;
                ctx.Note($"{chapter} flare slots: {string.Join(",", slots)}");
            }
        }

        ctx.Same(2, withFlare, $"chapters authoring a lens flare");
        // ⚠ C2's flare is PREDICTED, not verified: the data says it has one and there is no
        // footage of it. Only C3 was ever captured.
        ctx.Note($"C2's flare is predicted from data only, no capture of the original exists");
    }

    // ---- node lab tree rows must follow live Visible --------------------------------------------

    // Hides a node through the lab's own Hide action, then re-shows it through a real RESET_STATE def,
    // the same path a world animation uses, and checks the tree row both times, never through the
    // button, only through Node3D.Visible. A def re-showing a node the user hid is correct behaviour,
    // so the row must follow it. ⚠ Use a chapter other than TestContext.Chapter: that one is cached
    // and shared with damage-hd, so the candidate search would otherwise depend on suite run order.
    [Suite("nodelab-visibility", "the node lab's tree row follows live Visible, not the hide button's last action")]
    internal static void NodeLabVisibility(TestContext ctx)
    {
        ctx.WithWorld("C2", collision: false, world =>
        {
            DestructibleRegistry.Instance? chosen = null;
            Node3D? healthy = null;
            foreach (var inst in world.Runtime.Destructibles.All)
            {
                if (inst.Def.ResetState == null)
                {
                    continue;
                }
                if (FindVariant(inst.Anchor, "healthy") is { Visible: true } found)
                {
                    chosen = inst;
                    healthy = found;
                    break;
                }
            }
            ctx.Check(chosen != null,
                $"chapter has a destructible with a visible 'healthy' variant and a RESET_STATE chapter={ctx.Chapter}");
            if (chosen == null || healthy == null)
            {
                return;
            }

            var selection = new SelectionService(world.Session.Root, ctx.Camera);
            var lab = new NodeLab(world.Session.Root, selection, world.Runtime, world.Session.Program,
                world.Session.Builder.Scene, collisionBuilt: false);
            ctx.Host.AddChild(selection);
            ctx.Host.AddChild(lab);
            try
            {
                lab.Toggle();
                selection.Select(healthy);
                lab.RevealSelectionForTest();
                lab.ToggleHide();
                ctx.Check(!healthy.Visible, $"ToggleHide actually hides the node node={SelectionService.NameOf(healthy)}");

                var hidden = lab.RowStateForTest(healthy);
                ctx.Check(hidden is { Dim: true } row1 && row1.Text.Contains("(hidden)"),
                    $"row reads hidden right after the button node={SelectionService.NameOf(healthy)} text={hidden?.Text} dim={hidden?.Dim}");

                // The re-show is a real def, not the lab: RESET_STATE's OBJECT_ACTIVE_STATE events
                // are what an animation uses to bring the healthy subtree back, with no button
                // press and nothing telling the lab this node exists.
                world.Runtime.ResetDestructible(chosen);
                ctx.Check(healthy.Visible, $"RESET_STATE re-shows the node node={SelectionService.NameOf(healthy)}");

                lab.RefreshStatusForTest();
                var shown = lab.RowStateForTest(healthy);
                ctx.Check(shown is { Dim: false } row2 && !row2.Text.Contains("(hidden)"),
                    $"row follows the def's re-show without user input node={SelectionService.NameOf(healthy)} text={shown?.Text} dim={shown?.Dim}");
            }
            finally
            {
                lab.Free();
                selection.Free();
            }
        });
    }

    // Aims straight down at a sample of C4's 1024 m terrain tiles with the cloud deck hidden. The
    // box-only pick skipped every map-scale mesh and missed over bare ground; the triangle pick must
    // land on the aimed tile. Two struck tiles then export through the set and are read back: the
    // merged box has to cover both tile centres, which fails if members lose their world transform,
    // and a member nested in another must not be written twice.
    [Suite("terrain-pick-export", "a click reaches C4's terrain tiles, and an export set writes them as one glTF at their world positions")]
    internal static void TerrainPickExport(TestContext ctx)
    {
        ctx.WithWorld("C4", collision: false, world =>
        {
            var root = world.Session.Root;
            var deck = world.Session.Builder.CloudDeck;
            var tiles = new List<MeshInstance3D>();
            CollectMapScale(root, deck, tiles);
            ctx.Check(tiles.Count > 0, $"C4 has map-scale terrain meshes tiles={tiles.Count}");
            if (tiles.Count == 0)
            {
                return;
            }

            var selection = new SelectionService(root, ctx.Camera);
            ctx.Host.AddChild(selection);
            var set = new ExportSet(selection);
            ctx.Host.AddChild(set);
            var cameraWas = ctx.Camera.GlobalTransform;
            bool deckWas = deck?.Visible ?? false;
            if (deck != null)
            {
                deck.Visible = false;
            }
            try
            {
                var screen = ctx.Camera.GetViewport().GetVisibleRect().Size * 0.5f;
                var struck = new List<(Node3D Leaf, Vector3 Centre)>();
                int aimed = 0;
                for (int i = 0; i < tiles.Count; i += System.Math.Max(1, tiles.Count / 12))
                {
                    var box = tiles[i].GlobalTransform * tiles[i].Mesh.GetAabb();
                    var centre = box.GetCenter();
                    ctx.Camera.GlobalTransform = new Transform3D(Basis.LookingAt(Vector3.Down, Vector3.Forward),
                        new Vector3(centre.X, box.End.Y + 200f, centre.Z));
                    aimed++;
                    bool hit = selection.PickAt(screen);
                    var leaf = selection.Current;
                    bool onTile = hit && leaf != null && (ReferenceEquals(leaf, tiles[i]) || leaf.IsAncestorOf(tiles[i]));
                    if (onTile)
                    {
                        struck.Add((leaf!, centre));
                    }
                    else
                    {
                        ctx.Note($"straight-down pick took another object tile={SelectionService.NameOf(tiles[i])} hit={hit} got={(leaf != null ? SelectionService.NameOf(leaf) : "nothing")}");
                    }
                }
                ctx.Note($"terrain picks aimed={aimed} on_the_aimed_tile={struck.Count}");
                ctx.Check(struck.Count * 2 >= aimed, $"most straight-down picks take the aimed tile aimed={aimed} on_tile={struck.Count}");

                var distinct = struck.GroupBy(s => s.Leaf).Select(g => g.First()).Take(2).ToList();
                ctx.Check(distinct.Count == 2, $"two distinct tiles were struck count={distinct.Count}");
                if (distinct.Count < 2)
                {
                    return;
                }
                foreach (var (leaf, _) in distinct)
                {
                    set.Toggle(leaf);
                }
                ctx.Same(2, set.Members.Count, $"export set size after two Ctrl+clicks");

                string path = Path.Combine(ctx.ScratchDir, "terrain-pick-export.glb");
                Directory.CreateDirectory(ctx.ScratchDir);
                ctx.Same((long)Error.Ok, (long)GltfExporter.ExportSet(set.Members, path), $"export set write result");
                var (meshes, merged) = ReadBack(ctx, path);
                foreach (var (leaf, centre) in distinct)
                {
                    bool covered = merged.Position.X <= centre.X && centre.X <= merged.End.X
                                   && merged.Position.Z <= centre.Z && centre.Z <= merged.End.Z;
                    ctx.Check(covered, $"re-imported set covers tile={SelectionService.NameOf(leaf)} centre=({centre.X:0},{centre.Z:0}) box=({merged.Position.X:0},{merged.Position.Z:0})..({merged.End.X:0},{merged.End.Z:0})");
                }

                // A descendant of a member rides along with it, so listing it too must not add a copy.
                var nested = new List<Node3D>(set.Members) { FirstMesh(distinct[0].Leaf)! };
                string nestedPath = Path.Combine(ctx.ScratchDir, "terrain-pick-export-nested.glb");
                ctx.Same((long)Error.Ok, (long)GltfExporter.ExportSet(nested, nestedPath), $"nested export set write result");
                ctx.Same(meshes, ReadBack(ctx, nestedPath).Meshes, $"meshes when a member's own mesh is listed again");

                set.Clear();
                ctx.Same(0, set.Members.Count, $"export set size after Clear set");
            }
            finally
            {
                if (deck != null)
                {
                    deck.Visible = deckWas;
                }
                ctx.Camera.GlobalTransform = cameraWas;
                set.Free();
                selection.Free();
            }
        });
    }

    // The first descendant (inclusive) whose cs_name contains the tag, "healthy"/"destroyed" name
    // their variant subtrees exactly as CountVariants (Probes.cs) scans for, but this returns the
    // node itself rather than a count.
    internal static Node3D? FindVariant(Node3D node, string tag)
    {
        string cs = node.HasMeta(AnimRuntime.NameMeta) ? node.GetMeta(AnimRuntime.NameMeta).AsString() : node.Name.ToString();
        if (node.HasMeta(AnimRuntime.NameMeta) && cs.Contains(tag, System.StringComparison.OrdinalIgnoreCase))
        {
            return node;
        }
        foreach (var child in node.GetChildren())
        {
            if (child is Node3D n3d && FindVariant(n3d, tag) is { } found)
            {
                return found;
            }
        }
        return null;
    }

    // Asserts every pane of a 2-, 3- and 4-player rig is a 3D audio listener. The check reads trivial
    // and is not: a fresh SubViewport is NOT a listener, and in splitscreen the main camera stands down,
    // which takes it out of the World3D listener set. With no listener-enabled viewport left,
    // AudioStreamPlayer3D finds no listener in range, clears its bus volumes, and every 3D emitter in
    // the world is silent, with nothing logged or counted to say so.
    [Suite("splitscreen-listeners",
        "every 2–4P pane is a 3D audio listener, which a SubViewport is not by default, the "
        + "pinned listener model (A2), and the one thing standing between splitscreen and a "
        + "world with no listener at all")]
    internal static void SplitscreenListeners(TestContext ctx)
    {
        var main = ctx.Host.GetViewport();
        ctx.Check(main.AudioListenerEnable3D,
            $"the main viewport is a 3D audio listener (the untouched 1P path)");

        // The default the rig has to override, proved rather than assumed.
        using (var bare = new SubViewport())
        {
            ctx.Check(!bare.AudioListenerEnable3D,
                $"a fresh SubViewport is NOT an audio listener, so each pane must set it");
        }

        for (int players = 2; players <= SplitScreen.MaxPlayers; players++)
        {
            var split = SplitScreen.Build(players, main);
            ctx.Host.AddChild(split);
            try
            {
                ctx.Same(players, split.Views.Count, $"{players}P panes");
                foreach (var view in split.Views)
                {
                    ctx.Check(view.AudioListenerEnable3D,
                        $"{players}P pane {view.Name} is a 3D audio listener");
                }
            }
            finally
            {
                ctx.Host.RemoveChild(split);
                split.Free();
            }
        }
    }

    // WorldLights.Commit fades and ranks
    // each light against the NEAREST of every pane's camera, not a single position. Driven
    // straight against a real WorldLights instance with synthetic positions,
    // there is no per-player placement flag to give two scripted panes independent spots (the
    // same CLI gap B11/B12 hit), so the rule is pinned here instead and the visual verdict is
    // PT-52's, alongside B11/B12's own owed at-the-controls check.
    [Suite("world-lights-nearest-viewer",
        "WorldLights budgets its 900-1500 m distance fade and its MaxActive significance rank "
        + "against the NEAREST of every pane's camera, not player 1's alone (B13, BL-366): a "
        + "light 2000 m from a lone P1 stays committed once a second viewer sits 100 m from it, "
        + "the able-to-fail control against P1 alone drops the same light, and the one-viewer "
        + "case reads exactly what it read before; the packed factor is the authored colour times "
        + "ambient + diffuse and the shader term takes no dot product; given a parent node the same commit mirrors "
        + "one OmniLight3D per committed light in enhanced mode and none at all in original mode")]
    internal static void WorldLightsNearestViewer(TestContext ctx)
    {
        var p1 = Vector3.Zero;
        // Well past FadeEnd (1500 m) from P1 alone, but 100 m from a second viewer.
        var farFromP1 = new Vector3(0f, 0f, -2000f);
        var p2 = new Vector3(0f, 0f, -2100f);

        var lights = new WorldLights();

        lights.Begin();
        lights.Add(farFromP1, Colors.White, 1f, 10f);
        lights.Commit(new[] { p1 });
        ctx.Check(!lights.CommittedPositions.Contains(farFromP1),
            $"ABLE-TO-FAIL CONTROL: 2000 m from a lone P1 is past the 1500 m FadeEnd, so the light drops");

        lights.Begin();
        lights.Add(farFromP1, Colors.White, 1f, 10f);
        lights.Commit(new[] { p1, p2 });
        ctx.Check(lights.CommittedPositions.Contains(farFromP1),
            $"the same light stays committed once a second viewer sits 100 m from it, nearest, not P1 alone");

        // The MaxActive budget's Significance rank must answer to the same nearest-viewer rule, not just
        // the fade: the 16-slot budget is packed with filler lights strictly farther from P1 than besideP2
        // sits from P2, so a correct nearest-viewer rank keeps besideP2 and cuts the farthest filler.
        var besideP2 = new Vector3(0f, 5f, -2100f);
        lights.Begin();
        for (int i = 0; i < WorldLights.MaxActive; i++)
            lights.Add(new Vector3(5f + i, 0f, -5f), Colors.White, 1f, 10f);
        lights.Add(besideP2, Colors.White, 1f, 10f);
        lights.Commit(new[] { p1 });
        ctx.Check(lights.CommittedPositions.Count == WorldLights.MaxActive
                  && !lights.CommittedPositions.Contains(besideP2),
            $"ABLE-TO-FAIL CONTROL: against P1 alone the 17th light (right beside where P2 will be) is past FadeEnd and never reaches the budget");

        lights.Begin();
        for (int i = 0; i < WorldLights.MaxActive; i++)
            lights.Add(new Vector3(5f + i, 0f, -5f), Colors.White, 1f, 10f);
        lights.Add(besideP2, Colors.White, 1f, 10f);
        lights.Commit(new[] { p1, p2 });
        ctx.Check(lights.CommittedPositions.Count == WorldLights.MaxActive
                  && lights.CommittedPositions.Contains(besideP2),
            $"with P2 present the same light is nearest to a viewer and outranks the farthest filler for a slot in the budget");

        // Single viewer must read exactly as it did before this item, the goldens' own invariant.
        var nearP1 = new Vector3(0f, 0f, -5f);
        lights.Begin();
        lights.Add(nearP1, Colors.White, 1f, 10f);
        lights.Commit(new[] { p1 });
        ctx.Check(lights.CommittedPositions.Count == 1 && lights.CommittedPositions.Contains(nearP1),
            $"one viewer (single player) uses the single-viewer distance rule");

        // The packed factor is the authored colour times ambient + diffuse, unconverted. The MP2
        // flag lights' 0.3 + 1.0 scale it by 1.3, and a light authoring neither (scalar 1) packs
        // its colour.
        var heLight = new Color(1f, 0.86f, 0.29f);
        lights.Begin();
        lights.Add(nearP1, heLight, 1f, 10f);
        lights.Add(new Vector3(0f, 0f, -6f), heLight, 1f, 10f, 0.3f + 1.0f);
        lights.Commit(new[] { p1 });
        ctx.Check(lights.CommittedFactors.Count == 2
                  && lights.CommittedFactors[0].IsEqualApprox(heLight)
                  && lights.CommittedFactors[1].IsEqualApprox(heLight * 1.3f),
            $"the factor is colour x (ambient + diffuse), unlinearised: {string.Join(", ", lights.CommittedFactors)}");
        ctx.Check(!lights.CommittedFactors[0].IsEqualApprox(heLight.SrgbToLinear()),
            $"ABLE-TO-FAIL CONTROL: the packed factor is not the linearised colour the enhanced omni takes");

        // The term has no N.L: the include never takes a dot product. A surface facing away from
        // the light gains exactly what a surface facing it does.
        string include = Godot.FileAccess.GetFileAsString("res://shaders/csky_lights.gdshaderinc");
        ctx.Check(include.Contains("csky_point_light", System.StringComparison.Ordinal)
                  && !include.Contains("dot(", System.StringComparison.Ordinal)
                  && !include.Contains("smoothstep", System.StringComparison.Ordinal),
            $"csky_lights.gdshaderinc defines csky_point_light with no dot product and no smoothstep");

        // Same instance, same commit shape, gated on the launch's own graphics mode: enhanced
        // mirrors the committed set onto one OmniLight3D per light, original spawns none at all.
        var omniParent = new Node3D();
        ctx.Host.AddChild(omniParent);
        try
        {
            var mirrored = new WorldLights(omniParent);
            mirrored.Begin();
            mirrored.Add(nearP1, Colors.White, 1f, 10f);
            mirrored.Commit(new[] { p1 });
            if (CSVM.Utils.GraphicsMode.Enhanced)
            {
                ctx.Same(mirrored.CommittedPositions.Count, omniParent.GetChildCount(),
                    $"enhanced mode mirrors one OmniLight3D per committed light");
            }
            else
            {
                ctx.Same(0, omniParent.GetChildCount(), $"original mode spawns no OmniLight3D nodes");
            }
            mirrored.Dispose();
            ctx.Same(0, omniParent.GetChildCount(), $"Dispose frees every spawned omni");
        }
        finally
        {
            ctx.Host.RemoveChild(omniParent);
            omniParent.Free();
        }
    }

    // ---- the fade re-derive's ancestor walk ------------------------------------------------------

    // A synthetic tree rather than a chapter's, because what is measured is the shape of the walk
    // and not any world's contents: a deep chain, a wide collider-bearing subtree at the bottom of
    // it, and faded roots elsewhere that the subtree shares no ancestor with.
    [Suite("fade-walk-bound",
        "WorldCollision's fade re-derive climbs the ancestors once per subtree, not once per node: "
        + "with unrelated faded roots elsewhere in the world, fading and un-fading a 200-body "
        + "subtree each cost exactly the subtree root's own ancestor count in walk steps, and the "
        + "derived collider state still answers a faded ancestor and a faded descendant")]
    internal static void FadeWalkBound(TestContext ctx)
    {
        const int ChainDepth = 40;
        const int Bodies = 200;
        const int Unrelated = 32;

        var stage = new Node3D { Name = "fade-walk-stage" };
        ctx.Host.AddChild(stage);
        var faded = new List<Node3D>();
        try
        {
            var chain = new List<Node3D>();
            var deepest = stage;
            for (int i = 0; i < ChainDepth; i++)
            {
                var link = new Node3D { Name = $"link{i}" };
                deepest.AddChild(link);
                chain.Add(link);
                deepest = link;
            }
            var effectRoot = new Node3D { Name = "effect-root" };
            deepest.AddChild(effectRoot);
            var shapes = new List<CollisionShape3D>();
            var owners = new List<Node3D>();
            for (int i = 0; i < Bodies; i++)
                shapes.Add(TrackedBody(effectRoot, $"body{i}", owners));

            int rootAncestors = AncestorCount(effectRoot);
            long perNode = PerNodeWalkCost(effectRoot);

            // The same cycle before anything unrelated is faded, which is the comparison the
            // process-wide fast path turns on: reported, not asserted, since a suite that
            // ran earlier in this process may already have left a faded root of its own behind.
            WorldCollision.TakeWalkSteps();
            Fade(effectRoot, faded);
            long lonelyFade = WorldCollision.TakeWalkSteps();
            Unfade(effectRoot, faded);
            long lonelyUnfade = WorldCollision.TakeWalkSteps();

            for (int i = 0; i < Unrelated; i++)
            {
                var other = new Node3D { Name = $"other{i}" };
                stage.AddChild(other);
                Fade(other, faded);
            }

            WorldCollision.TakeWalkSteps();
            Fade(effectRoot, faded);
            long fadeSteps = WorldCollision.TakeWalkSteps();
            ctx.Same(0, EnabledCount(shapes), $"the faded subtree's colliders are all off");
            Unfade(effectRoot, faded);
            long unfadeSteps = WorldCollision.TakeWalkSteps();
            ctx.Same(shapes.Count, EnabledCount(shapes), $"un-fading the subtree brings them all back");

            ctx.Same(rootAncestors, fadeSteps, $"fading a {Bodies}-body subtree costs one climb from its root");
            ctx.Same(rootAncestors, unfadeSteps, $"un-fading it costs one climb, with {Unrelated} unrelated faded roots elsewhere");
            ctx.Check(unfadeSteps * 100 < perNode,
                $"ABLE-TO-FAIL CONTROL: a climb per node would cost {perNode} steps, over 100x the {unfadeSteps} measured");
            ctx.Check(lonelyFade <= rootAncestors && lonelyUnfade <= rootAncestors,
                $"the same cycle with nothing unrelated faded costs no more: {lonelyFade} to fade, {lonelyUnfade} to un-fade");
            ctx.Note($"fade walk: {fadeSteps} steps to fade and {unfadeSteps} to un-fade a {Bodies}-body subtree {rootAncestors} deep with {Unrelated} unrelated faded roots present, {lonelyFade}/{lonelyUnfade} with none, against {perNode} for a climb per node");

            // The climb is what carries a fade above the subtree into it, so a subtree re-derived
            // under a still-faded ancestor must not switch its colliders back on.
            var ancestor = chain[ChainDepth / 2];
            Fade(ancestor, faded);
            ctx.Same(0, EnabledCount(shapes), $"a faded ancestor takes the subtree's colliders off");
            Fade(effectRoot, faded);
            Unfade(effectRoot, faded);
            ctx.Same(0, EnabledCount(shapes), $"re-deriving the subtree leaves them off while the ancestor is faded");
            Unfade(ancestor, faded);
            ctx.Same(shapes.Count, EnabledCount(shapes), $"un-fading the ancestor brings them back");

            // And the descending flag must pick up each node's own mark, or a fade INSIDE the
            // subtree is lost the moment anything above it is re-derived.
            Fade(owners[0], faded);
            Fade(effectRoot, faded);
            Unfade(effectRoot, faded);
            ctx.Same(shapes.Count - 1, EnabledCount(shapes), $"a descendant faded on its own stays off when the subtree above it is re-derived");
            Unfade(owners[0], faded);
            ctx.Same(shapes.Count, EnabledCount(shapes), $"un-fading that descendant is the last collider back");
        }
        finally
        {
            // Whatever this left faded would cost every later suite the guard's fast path, since
            // the count of live faded roots outlives the nodes it counted.
            for (int i = faded.Count - 1; i >= 0; i--)
                WorldCollision.SetFaded(faded[i], false);
            ctx.Host.RemoveChild(stage);
            stage.Free();
        }
    }

    // Visible map-scale meshes under a world root, the cloud deck's excepted.
    private static void CollectMapScale(Node n, Node3D? deck, List<MeshInstance3D> into)
    {
        if (deck != null && ReferenceEquals(n, deck))
        {
            return;
        }
        if (n is MeshInstance3D { Mesh: { } mesh } mi && mi.IsVisibleInTree()
            && (mi.GlobalTransform.Basis * mesh.GetAabb().Size).Length() >= SelectionService.MaxPickDiag)
        {
            into.Add(mi);
        }
        foreach (var child in n.GetChildren())
        {
            CollectMapScale(child, deck, into);
        }
    }

    private static MeshInstance3D? FirstMesh(Node n)
    {
        if (n is MeshInstance3D mi)
        {
            return mi;
        }
        foreach (var child in n.GetChildren())
        {
            if (FirstMesh(child) is { } found)
            {
                return found;
            }
        }
        return null;
    }

    // Re-imports a written glTF into the scene tree just long enough to measure it: its mesh count
    // and the world box of those meshes.
    private static (int Meshes, Aabb Box) ReadBack(TestContext ctx, string path)
    {
        var doc = new GltfDocument();
        var state = new GltfState();
        ctx.Same((long)Error.Ok, (long)doc.AppendFromFile(path, state), $"re-import read result path={Path.GetFileName(path)}");
        if (doc.GenerateScene(state) is not Node3D scene)
        {
            ctx.Check(false, $"re-imported scene has a Node3D root path={Path.GetFileName(path)}");
            return (0, default);
        }
        ctx.Host.AddChild(scene);
        int meshes = 0;
        Aabb merged = default;
        void Walk(Node n)
        {
            if (n is MeshInstance3D { Mesh: { } mesh } mi)
            {
                var box = mi.GlobalTransform * mesh.GetAabb();
                merged = meshes == 0 ? box : merged.Merge(box);
                meshes++;
            }
            foreach (var child in n.GetChildren())
            {
                Walk(child);
            }
        }
        Walk(scene);
        scene.Free();
        return (meshes, merged);
    }

    // One mission's ZONE1 lighting, off a rig of its own so the two missions cannot share state.
    // The two energies come back, with the ambient as the renderer will read it (source and
    // colour). The authored colour it should be is read straight off the file for comparison. The
    // Environment is a bare one, so every field returned is a value the zone apply itself wrote.
    // ⚠ --sky-zone=zone1 on purpose. The default request is zone2, the ABOVE-cloud zone, and
    // comparing two missions' cloud tops is not the night-against-day question.
    private static (float Sun, float Ambient, Godot.Environment.AmbientSource Source, Color Color, Color Authored) ZoneEnergies(
        TestContext ctx, string chapter, string mission, string zrdr)
    {
        var sun = new DirectionalLight3D { Name = $"sun-energy-{chapter}-{mission}" };
        var env = new Godot.Environment();
        ctx.Host.AddChild(sun);
        try
        {
            var spec = SessionSpec.Parse(
                new[] { $"--chapter={chapter}", $"--mission={mission}", "--sky-zone=zone1" });
            var weatherRig = new WeatherRig(spec, ctx.Host, sun, env: env);
            weatherRig.Build(zrdr, System.Array.Empty<PlayerRig>(), System.Array.Empty<HorizonZone>(), _ => { });
            var authored = WeatherState.Load(zrdr)?.Zone("zone1").SunColorAmbient ?? Colors.White;
            return (sun.LightEnergy, env.AmbientLightEnergy, env.AmbientLightSource,
                env.AmbientLightColor, authored);
        }
        finally
        {
            sun.QueueFree();
        }
    }

    // The shape SceneBuilder builds and tracks: an owner node carrying one body carrying one shape.
    private static CollisionShape3D TrackedBody(Node3D parent, string name, List<Node3D> owners)
    {
        var owner = new Node3D { Name = name };
        var body = new StaticBody3D { Name = $"{name}-body" };
        var shape = new CollisionShape3D { Name = $"{name}-shape", Shape = new BoxShape3D() };
        parent.AddChild(owner);
        owner.AddChild(body);
        body.AddChild(shape);
        owners.Add(owner);
        WorldCollision.Track(owner);
        return shape;
    }

    private static void Fade(Node3D node, List<Node3D> faded)
    {
        WorldCollision.SetFaded(node, true);
        faded.Add(node);
    }

    private static void Unfade(Node3D node, List<Node3D> faded)
    {
        WorldCollision.SetFaded(node, false);
        faded.Remove(node);
    }

    private static int EnabledCount(List<CollisionShape3D> shapes)
    {
        int enabled = 0;
        foreach (var shape in shapes)
            if (!shape.Disabled)
                enabled++;
        return enabled;
    }

    private static int AncestorCount(Node node)
    {
        int count = 0;
        for (Node? n = node.GetParent(); n != null; n = n.GetParent())
            count++;
        return count;
    }

    // What the subtree's re-derive costs when every node answers "is an ancestor faded" with a
    // climb of its own: each walks itself and every ancestor, to the scene root.
    private static long PerNodeWalkCost(Node node)
    {
        long cost = node is Node3D ? AncestorCount(node) + 1 : 0;
        foreach (var child in node.GetChildren())
            cost += PerNodeWalkCost(child);
        return cost;
    }

    // SUNLIGHT_ORIENTATION's euler pair as a direction, written out from the binary's own law
    // (docs/org/weather.md) so a suite's expectation does not come from the reader under test.
    private static Vector3 SunBeam(float pitchRad, float yawRad) => new Vector3(
        -Mathf.Cos(pitchRad) * Mathf.Sin(yawRad),
        Mathf.Sin(pitchRad),
        -Mathf.Cos(pitchRad) * Mathf.Cos(yawRad));

    // Moves the camera, lets the rig resolve the new weather state, then draws one frame of the
    // interior pass: the beam the panel is lit by, how far it sits off the session sun, and the
    // state that produced it. The Sync is what a flown frame does, so the mirror under test is the
    // shipped one rather than a probe of its own.
    private static (Vector3 Beam, float Off, int State) CrossedBeam(WeatherRig rig,
        CockpitOverlay pass, CameraController cam, IReadOnlyList<PlayerRig> rigs, Vector3 at,
        DirectionalLight3D clone, DirectionalLight3D sun)
    {
        rigs[0].Camera.Position = at;
        rig.Tick(rigs);
        pass.Sync(Basis.Identity, cam, 0f);
        var beam = -clone.GlobalBasis.Z;
        return (beam, beam.AngleTo(-sun.GlobalBasis.Z), rigs[0].CameraWeatherState);
    }


    // Every distinct Shader a subtree draws through, over all four seats a built world uses. That
    // covers a GeometryInstance3D's override and overlay, a MeshInstance3D's per-surface
    // materials, and a MultiMeshInstance3D's shared mesh surfaces.
    private static void CollectShaders(Node node, HashSet<Shader> found)
    {
        if (node is GeometryInstance3D geo)
        {
            if (geo.MaterialOverride is ShaderMaterial { Shader: { } over })
            {
                found.Add(over);
            }

            if (geo.MaterialOverlay is ShaderMaterial { Shader: { } overlay })
            {
                found.Add(overlay);
            }
        }

        if (node is MeshInstance3D mesh)
        {
            for (int i = 0; i < mesh.GetSurfaceOverrideMaterialCount(); i++)
            {
                if (mesh.GetActiveMaterial(i) is ShaderMaterial { Shader: { } surface })
                {
                    found.Add(surface);
                }
            }
        }

        if (node is MultiMeshInstance3D multi && multi.Multimesh?.Mesh is { } shared)
        {
            for (int i = 0; i < shared.GetSurfaceCount(); i++)
            {
                if (shared.SurfaceGetMaterial(i) is ShaderMaterial { Shader: { } instanced })
                {
                    found.Add(instanced);
                }
            }
        }

        foreach (var child in node.GetChildren())
        {
            CollectShaders(child, found);
        }
    }

    // Which generator emitted a shader, by a token only that generator writes. The world mesh
    // alone carries a depth bias, and the templates clutter alone dithers its far fade. Of the two
    // remaining spinning arms only the cylindrical facade builds a spin basis. The ambient cloud
    // field alone declares far_fade. Anything else is the camera-facing billboard.
    private static string ArmOf(string code) =>
        code.Contains("uniform float depth_bias", System.StringComparison.Ordinal) ? "world"
        : code.Contains("csky_clutter_dither_keep", System.StringComparison.Ordinal) ? "clutter"
        : code.Contains("mat3 spin", System.StringComparison.Ordinal) ? "facade"
        : code.Contains("uniform vec2 far_fade", System.StringComparison.Ordinal) ? CloudFieldArm
        : "billboard";

    private static string Tally(Dictionary<string, int> byArm) =>
        byArm.Count == 0 ? "none"
        : string.Join(" ", byArm.OrderBy(p => p.Key, System.StringComparer.Ordinal)
            .Select(p => $"{p.Key}={p.Value}"));

    // The Danger Zone camera's frame over a Nose view, which hides the most. Every hidden airframe
    // group shows for the photograph on a layer no pane draws, and the interior stays out. The end
    // of the frame puts every group's visibility and every mesh's layers back exactly.
    private static void PhotographFrame(TestContext ctx, CockpitVisibility cockpit, Node3D interior,
        Node3D? body, Node3D? markers, Node3D? dontmove)
    {
        var groups = new[] { body, markers, dontmove }.OfType<Node3D>().ToList();
        var before = groups.SelectMany(Meshes).Distinct().ToDictionary(m => m, m => m.Layers);
        uint layer = SplitScreen.PhotographLayer;

        cockpit.ShowForPhotograph(layer);
        var drawn = groups.SelectMany(Meshes).ToList();
        ctx.Check(groups.Count == 3 && groups.All(g => g.Visible) && !interior.Visible,
            $"the photograph frame shows body, markers and dontmove and leaves the interior out");
        ctx.Check(drawn.Count > 0 && drawn.All(m => m.Layers == layer),
            $"…every one of their {drawn.Count} meshes on the photograph layer alone");

        cockpit.EndPhotograph();
        ctx.Check(groups.All(g => !g.Visible),
            $"the end of the frame hides the three groups again");
        ctx.Check(before.All(kv => kv.Key.Layers == kv.Value),
            $"…and gives every mesh back its own layers");
    }

    private static IEnumerable<VisualInstance3D> Meshes(Node root)
    {
        if (root is VisualInstance3D instance)
            yield return instance;
        foreach (var child in root.GetChildren())
        {
            foreach (var nested in Meshes(child))
                yield return nested;
        }
    }
}
