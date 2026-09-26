using System;
using System.Collections.Generic;
using CSVM.Mech3;
using Godot;

namespace CSVM.Testing;

/// <summary>Whether a clutter card is hidden by what stands between it and the eye, measured on
/// drawn pixels rather than on the material's declared flags. One off-screen pane holds C1's
/// firtree card, a second card of the same kind, and a clutter template's own ground quad, and
/// the occluder is posed once in front of the card under test and once behind it. The behind pose
/// is the control that makes the front pose meaningful: without it "nothing changed" would also
/// pass a card that drew nowhere.</summary>
internal static class ClutterCardDepthSuites
{
    private const string Chapter = "C1";

    // The pane is small on purpose: the assertion is a changed-pixel count, and the field of view
    // is fitted to the card at each range, so a card that wrongly survives moves thousands of them.
    private const int PaneEdge = 96;

    // The two cards the pane holds, in the order the MultiMesh draws them. A MultiMesh is one draw
    // call in buffer order and never sorts, so the nearer card standing first is the arrangement
    // where only its own depth can keep the farther one behind it.
    private const int NearIndex = 0;

    private const int FarIndex = 1;

    // Where an instance goes to be left out of a frame: behind the eye, so it is clipped away.
    private const float ParkZ = 50f;

    // ⚠ A SubViewport's own texture reads back as Rgb8, three bytes to the pixel and no alpha, not
    // the four a window capture gives. A comparison written for four counts three quarters of the
    // buffer and reports every frame as wholly changed.
    private const int Channels = 3;

    // How far an Rgb8 channel has to move before a card counts as having drawn there, for the
    // overpaint measure only. The feather's faintest texels move the frame by one step. Over a
    // backdrop they round to its own value, which is quantisation rather than a lost card.
    private const int FeatherFloor = 4;

    // Card ranges in metres. The far pair is where the report was made, a ridge about a kilometre
    // and a half out. That is also where the depth buffer's resolution is thinnest. A range under
    // the fade's near edge does not belong here, since the card is culled outright there. Every
    // case then measures an empty pane, and the suite reports a fade setting, not a draw order.
    private static readonly float[] Ranges = { 60f, 750f, 1500f };

    private static readonly Vector2I PaneSize = new(PaneEdge, PaneEdge);

    [Suite("clutter-card-depth",
        "a tree card is hidden by the ground quad standing between it and the eye and by the "
        + "opaque body of a nearer card of its own kind, and still draws with both of those behind "
        + "it instead: C1's firtree card rendered into an off-screen pane at three ranges, opaque "
        + "and mid-fade, asserted on changed pixel counts against each pose's own occluder frame")]
    internal static void CardDepth(TestContext ctx)
    {
        string texturesPath = SessionPaths.ChapterTextures(ctx.DataRoot, Chapter);
        string gamezPath = SessionPaths.ChapterGamez(ctx.DataRoot, Chapter);
        ctx.RequireData(texturesPath, $"{Chapter} textures");
        ctx.RequireData(gamezPath, $"{Chapter} gamez");
        ctx.RequireData(ctx.InterpPath, $"interp.json");

        var gamez = GameZ.Load(gamezPath);
        using var textures = new TextureArchive(texturesPath);
        var names = ClutterBuilder.TemplateNames(ctx.InterpPath, Chapter);
        ctx.Check(names.Count > 0, $"{Chapter} registers clutter templates count={names.Count}");
        if (names.Count == 0)
        {
            return;
        }

        var scene = new SceneBuilder(gamez, textures);
        var clutter = new ClutterBuilder(gamez, textures, scene,
            ClutterTemplateSpec.Load(SessionPaths.ChapterZrdr(ctx.DataRoot, Chapter)));
        var built = clutter.Build(names);
        ClutterBuilder.KindExport? card = null;
        foreach (var kind in clutter.ExportedKinds ?? Array.Empty<ClutterBuilder.KindExport>())
        {
            if (!kind.Solid && kind.Texture.StartsWith("firtree", StringComparison.OrdinalIgnoreCase))
            {
                card = kind;
                break;
            }
        }
        built?.Free();
        Blended(ctx, clutter);
        ctx.Check(card != null, $"{Chapter} exports a firtree card kind");
        if (card == null)
        {
            return;
        }

        // The template's own ground node: the polygon the cards are stamped onto, so the occluder
        // carries a real world surface material rather than a stand-in.
        Node3D? plate = null;
        foreach (var name in names)
        {
            if (ClutterBuilder.FindTemplateRoot(gamez, name) is not { } root
                || ClutterBuilder.FirstWithMesh(gamez, root) is not { } ground)
            {
                continue;
            }
            plate = Plate(scene.BuildSubtree(ground));
            if (plate != null)
            {
                break;
            }
        }
        ctx.Check(plate != null, $"a clutter template's ground quad builds as world geometry");
        if (plate == null)
        {
            return;
        }

        var pane = new SubViewport
        {
            Size = PaneSize,
            OwnWorld3D = true,
            World3D = new World3D(),
            RenderTargetUpdateMode = SubViewport.UpdateMode.Always,
            RenderTargetClearMode = SubViewport.ClearMode.Always,
        };
        var eye = new Camera3D { Fov = 30f, Current = true, Near = 1f, Far = 4000f };
        pane.AddChild(eye);
        ctx.Host.AddChild(pane);

        var blended = BlendPlate(ctx, gamez, textures, scene);
        ctx.Check(blended != null, $"{Chapter} skins a world surface with a blended material");

        var cards = Cards(card);
        try
        {
            pane.AddChild(plate);
            pane.AddChild(cards);
            Frame(eye, card, Ranges[0]);
            PosePlate(plate, eye, -Ranges[0] * 0.5f);
            Park(cards, card, NearIndex);
            Park(cards, card, FarIndex);
            plate.Visible = false;
            byte[] bare = Shot(pane);
            plate.Visible = true;
            // A world surface is one-sided, and which of the quad's two faces its winding calls the
            // front is the template's business, so the plate is turned over rather than assumed.
            if (Differing(bare, Shot(pane)) == 0)
            {
                plate.Rotate(Vector3.Right, Mathf.Pi);
            }
            if (blended != null)
            {
                pane.AddChild(blended);
                PosePlate(blended, eye, -Ranges[0] * 0.5f);
                plate.Visible = false;
                int drew = Differing(bare, Shot(pane));
                if (drew == 0)
                {
                    blended.Rotate(Vector3.Right, Mathf.Pi);
                    drew = Differing(bare, Shot(pane));
                }
                ctx.Check(drew > 100, $"the blended world surface fills the pane pixels={drew}");
                blended.Visible = false;
                plate.Visible = true;
            }
            foreach (float range in Ranges)
            {
                foreach (bool full in new[] { true, false })
                {
                    Frame(eye, card, range);
                    Ground(ctx, pane, eye, plate, cards, card, range, full);
                    Ahead(ctx, pane, eye, plate, cards, card, range, full);
                    if (blended != null)
                    {
                        Behind(ctx, pane, eye, plate, blended, cards, card, range, full);
                    }
                }
            }
        }
        finally
        {
            cards.Free();
            plate.Free();
            blended?.Free();
            pane.Free();
        }
    }

    // Which card kinds took the archive's soft-alpha verdict and so draw blended. A blended card
    // that writes no depth is drawn in its MultiMesh's buffer order against every other card, which
    // is what puts a far tree over a near one, so the count belongs in the record beside the
    // measurements even where it is zero.
    private static void Blended(TestContext ctx, ClutterBuilder clutter)
    {
        var blended = new List<string>();
        foreach (var kind in clutter.ExportedKinds ?? Array.Empty<ClutterBuilder.KindExport>())
        {
            if (kind.Solid || kind.Material is not ShaderMaterial { Shader: { } shader })
            {
                continue;
            }
            if (shader.Code.Contains("blend_mix", StringComparison.Ordinal))
            {
                blended.Add(kind.Texture);
            }
        }
        string listed = blended.Count == 0 ? "-" : string.Join(",", blended);
        ctx.Note($"{Chapter} card kinds drawing blended count={blended.Count} kinds={listed}");
    }

    // The ground quad as the occluder, at one range and one fade state. The card holds still and
    // the quad moves: once at half the range, where it stands between the card and the eye, and
    // once at one and a half times it, where it does not. Each pose is shot with the card hidden
    // and again with it shown, so the counts are that pose's own difference.
    private static void Ground(TestContext ctx, SubViewport pane, Camera3D eye, Node3D plate,
        MultiMeshInstance3D cards, ClutterBuilder.KindExport card, float range, bool full)
    {
        string at = $"{range:0} m {(full ? "opaque" : "mid-fade")}";
        Park(cards, card, NearIndex);
        Place(cards, card, FarIndex, -range, full);

        PosePlate(plate, eye, -range * 1.5f);
        byte[] behindOnly = Without(pane, cards, card, FarIndex, -range, full);
        byte[] behindBoth = Shot(pane);

        PosePlate(plate, eye, -range * 0.5f);
        byte[] frontOnly = Without(pane, cards, card, FarIndex, -range, full);
        byte[] frontBoth = Shot(pane);

        plate.Visible = false;
        byte[] alone = Shot(pane);
        byte[] bare = Without(pane, cards, card, FarIndex, -range, full);
        plate.Visible = true;

        int drawnPlate = Differing(bare, frontOnly);
        int drawnCard = Differing(bare, alone);
        int hidden = Differing(frontOnly, frontBoth);
        int shown = Differing(behindOnly, behindBoth);

        ctx.Check(drawnPlate > 1000, $"the ground quad fills the pane at {at} pixels={drawnPlate}");
        ctx.Check(drawnCard > 100,
            $"the card draws with nothing in front of it at {at} pixels={drawnCard}");
        ctx.Check(shown > 100,
            $"the card with the ground quad behind it still draws at {at} pixels={shown}");
        ctx.Same(0L, hidden,
            $"the card behind the ground quad changes no pixel of it at {at} pixels={hidden}");
        ctx.Note($"{at} ground: plate={drawnPlate} card={drawnCard} behind={hidden} front={shown}");
    }

    // A nearer card of the same kind as the occluder, drawn first, with the card under test twice
    // as far away and concentric with it, so the far card's whole quad lies inside the near card's.
    // The near card's holes are not occluders, so the assertion is confined to the pixels its body
    // covers whatever is behind them, found by shooting it over two different backdrops.
    private static void Ahead(TestContext ctx, SubViewport pane, Camera3D eye, Node3D plate,
        MultiMeshInstance3D cards, ClutterBuilder.KindExport card, float range, bool full)
    {
        string at = $"{range:0} m {(full ? "opaque" : "mid-fade")}";
        PosePlate(plate, eye, -range * 2f);
        Park(cards, card, NearIndex);
        Park(cards, card, FarIndex);
        byte[] plateOnly = Shot(pane);
        plate.Visible = false;
        byte[] bare = Shot(pane);

        Place(cards, card, NearIndex, -range * 0.5f, full);
        byte[] nearOnBare = Shot(pane);
        plate.Visible = true;
        byte[] nearOnPlate = Shot(pane);

        Place(cards, card, FarIndex, -range, full);
        byte[] both = Shot(pane);

        Park(cards, card, NearIndex);
        byte[] farOnPlate = Shot(pane);

        // Opaque where the near card drew something and drew the same thing over either backdrop.
        var body = Mask(bare, nearOnBare, nearOnPlate);
        int covered = Count(body);
        int drawnFar = Differing(plateOnly, farOnPlate);
        int through = Differing(nearOnPlate, both, body);

        ctx.Check(covered > 100, $"the near card covers the pane at {at} pixels={covered}");
        ctx.Check(drawnFar > 100, $"the far card draws on its own at {at} pixels={drawnFar}");
        ctx.Same(0L, through,
            $"the far card changes no pixel the near card's body covers at {at} pixels={through}");
        ctx.Note($"{at} ahead: body={covered} far={drawnFar} through={through}");
    }

    // A BLENDED world surface standing behind the card. A scissored surface is opaque and settles in
    // the depth pass, so a card in front of one composites over it whatever the draw order. A blended
    // one is drawn in the transparency pass alongside the card. There the sort decides which paints
    // last, and a kind's whole-chapter MultiMesh carries one sort key for every card in it. The far
    // instance is parked aside, outside the pane but inside the MultiMesh's bounds. That gives the
    // near card the distant sort key it has in the world.
    private static void Behind(TestContext ctx, SubViewport pane, Camera3D eye, Node3D opaque,
        Node3D blended, MultiMeshInstance3D cards, ClutterBuilder.KindExport card, float range, bool full)
    {
        string at = $"{range:0} m {(full ? "opaque" : "mid-fade")}";
        float near = -range * 0.5f;
        opaque.Visible = false;
        blended.Visible = false;
        Park(cards, card, NearIndex);
        PlaceAside(cards, card, FarIndex, -range * 4f);
        byte[] bare = Shot(pane);

        Place(cards, card, NearIndex, near, full);
        byte[] cardAlone = Shot(pane);

        opaque.Visible = true;
        PosePlate(opaque, eye, -range * 1.5f);
        byte[] cardOverOpaque = Shot(pane);
        opaque.Visible = false;

        blended.Visible = true;
        PosePlate(blended, eye, -range * 1.5f);
        byte[] cardOverBlended = Shot(pane);
        byte[] blendedAlone = Without(pane, cards, card, NearIndex, near, full);
        blended.Visible = false;
        // ⚠ Hand the opaque plate back visible. Ground() poses it without showing it, so a case
        // that leaves it hidden silently empties every later ground measurement.
        opaque.Visible = true;

        // Opaque where the card drew something and drew the same thing over either backdrop, so the
        // assertion covers only the pixels the card owns outright.
        var body = Mask(bare, cardAlone, cardOverOpaque);
        int covered = Count(body);
        int drawnPlate = Differing(bare, blendedAlone);
        int through = Differing(cardAlone, cardOverBlended, body);
        // The soft half of the question. Over its body the card writes depth, and the surface behind
        // it is rejected whatever the sort says. Over its feathered edge it writes none, so a surface
        // drawn after it lands there and the card contributes nothing.
        int lost = Erased(bare, cardAlone, blendedAlone, cardOverBlended);

        ctx.Check(covered > 100, $"the card's body covers the pane at {at} pixels={covered}");
        ctx.Check(drawnPlate > 100, $"the blended surface draws behind the card at {at} pixels={drawnPlate}");
        ctx.Same(0L, through,
            $"a blended world surface behind the card changes no pixel of its body at {at} pixels={through}");
        ctx.Same(0L, lost,
            $"the card is not painted over by the blended surface behind it at {at} pixels={lost}");
        ctx.Note($"{at} behind: body={covered} surface={drawnPlate} through={through} lost={lost}");
    }

    // The same pose with one instance left out, so a frame and its own reference differ by that
    // instance alone.
    private static byte[] Without(SubViewport pane, MultiMeshInstance3D cards,
        ClutterBuilder.KindExport card, int index, float z, bool full)
    {
        Park(cards, card, index);
        byte[] shot = Shot(pane);
        Place(cards, card, index, z, full);
        return shot;
    }

    // The pane's field of view fitted to the card at one range, so the same card fills about the
    // same share of it whether it stands sixty metres out or fifteen hundred. The card's own size
    // is fixed: its shader builds the quad from the mesh about the instance's origin and never
    // reads the instance's basis, so a card cannot be scaled up to stand in for a nearer one.
    private static void Frame(Camera3D eye, ClutterBuilder.KindExport card, float range)
    {
        float span = Mathf.Max(card.Width, card.Height);
        eye.Fov = Mathf.Clamp(Mathf.RadToDeg(2f * Mathf.Atan(span * 0.8f / range)), 1f, 60f);
    }

    // The ground quad across the pane at one depth, wide enough to cover it with margin.
    private static void PosePlate(Node3D plate, Camera3D eye, float z)
    {
        var basis = plate.Transform.Basis.Orthonormalized();
        float scale = 6f * Mathf.Abs(z) * Mathf.Tan(Mathf.DegToRad(eye.Fov / 2f));
        plate.Transform = new Transform3D(basis.Scaled(new Vector3(scale, scale, scale)),
            new Vector3(0f, 0f, z));
    }

    // The first world mesh this chapter skins with a BLENDED surface material, as a plate. The
    // material is the real one off the real mesh. The question is what that material's place in the
    // transparency sort does, and a stand-in built here would answer it about itself instead.
    private static Node3D? BlendPlate(TestContext ctx, GameZ gamez, TextureArchive textures, SceneBuilder scene)
    {
        ArrayMesh? bestMesh = null;
        Material? bestMaterial = null;
        int bestModel = -1;
        float best = 0f;
        for (int i = 0; i < gamez.Meshes.Count; i++)
        {
            if (!SoftAlphaSkinned(gamez, textures, gamez.Meshes[i]) || scene.SharedMesh(i) is not { } built)
            {
                continue;
            }
            // The broadest and flattest one wins. The plate is laid across the pane, so a wall or a
            // sliver stands edge-on to the eye and photographs as nothing, whatever it blends.
            var box = built.GetAabb();
            float span = Mathf.Min(box.Size.X, box.Size.Z);
            if (span <= best)
            {
                continue;
            }
            for (int s = 0; s < built.GetSurfaceCount(); s++)
            {
                if (scene.AlphaOf(built.SurfaceGetMaterial(s)) != SceneBuilder.TransparencyClass.BlendSurface)
                {
                    continue;
                }
                (bestMesh, bestMaterial, bestModel, best) = (built, built.SurfaceGetMaterial(s), i, span);
                break;
            }
        }
        if (bestMesh == null)
        {
            return null;
        }
        var size = bestMesh.GetAabb().Size;
        ctx.Note($"{Chapter} blended world surface: model={bestModel} extent={size.X:0}x{size.Y:0}x{size.Z:0} m");
        return PlateOf(bestMesh, bestMaterial);
    }

    // Whether any of a mesh's polygons names a texture the archive calls soft. It is the cheap
    // filter that keeps the scan above from building every mesh in the chapter to read a material.
    private static bool SoftAlphaSkinned(GameZ gamez, TextureArchive textures, GameZMesh mesh)
    {
        foreach (var poly in mesh.Polygons)
        {
            if (poly.MaterialIndex < 0 || poly.MaterialIndex >= gamez.Materials.Count
                || gamez.Materials[poly.MaterialIndex].TextureName is not { } texName)
            {
                continue;
            }
            textures.Find(texName);
            if (textures.LastHadAlpha && textures.LastAlphaIsSoft)
            {
                return true;
            }
        }
        return false;
    }

    // The ground polygon as an occluder: the first mesh under the built subtree, re-hung under a
    // node that stands it on edge across the pane.
    private static Node3D? Plate(Node3D? built)
    {
        var found = FirstMesh(built);
        if (found == null || built == null)
        {
            built?.Free();
            return null;
        }
        var mesh = found.Mesh;
        var material = found.GetActiveMaterial(0);
        built.Free();
        return PlateOf(mesh, material);
    }

    // One mesh and one material as a plate. The mesh's authored centre is cancelled and its span
    // normalised to one unit on the child. The parent's scale is then the plate's width in metres,
    // whatever the source's own corner coordinates are.
    private static Node3D? PlateOf(Mesh? mesh, Material? material)
    {
        if (mesh == null)
        {
            return null;
        }
        var box = mesh.GetAabb();
        float span = Mathf.Max(box.Size.X, box.Size.Z);
        if (span <= 0f)
        {
            return null;
        }
        var plate = new Node3D
        {
            Transform = new Transform3D(Basis.FromEuler(new Vector3(Mathf.Pi / 2f, 0f, 0f)), Vector3.Zero),
        };
        plate.AddChild(new MeshInstance3D
        {
            Mesh = mesh,
            MaterialOverride = material,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            Transform = new Transform3D(
                Basis.Identity.Scaled(Vector3.One / span),
                -(box.Position + (box.Size / 2f)) / span),
        });
        return plate;
    }

    private static MeshInstance3D? FirstMesh(Node? node)
    {
        if (node is MeshInstance3D { Mesh: not null } found)
        {
            return found;
        }
        for (int i = 0; node != null && i < node.GetChildCount(); i++)
        {
            if (FirstMesh(node.GetChild(i)) is { } below)
            {
                return below;
            }
        }
        return null;
    }

    // Two instances of the kind in one MultiMesh, both parked until a pose asks for them.
    private static MultiMeshInstance3D Cards(ClutterBuilder.KindExport kind)
    {
        var mm = new MultiMesh
        {
            TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
            UseCustomData = true,
            Mesh = kind.Mesh,
            InstanceCount = 2,
        };
        var node = new MultiMeshInstance3D
        {
            Multimesh = mm,
            MaterialOverride = kind.Material,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            ExtraCullMargin = kind.CullMargin,
        };
        Park(node, kind, NearIndex);
        Park(node, kind, FarIndex);
        return node;
    }

    private static void Park(MultiMeshInstance3D node, ClutterBuilder.KindExport kind, int index)
        => Place(node, kind, index, ParkZ, full: true);

    // One card far away and far enough off the camera axis to fall outside the pane's narrow field
    // of view. It draws nothing and still counts toward the MultiMesh's bounds. That is the only way
    // to give the card under test the distant sort key a chapter-wide kind hands every card.
    private static void PlaceAside(MultiMeshInstance3D node, ClutterBuilder.KindExport kind,
        int index, float z)
    {
        node.Multimesh!.SetInstanceTransform(index, new Transform3D(
            Basis.Identity, new Vector3(Mathf.Abs(z), -kind.Height / 2f, z)));
        node.Multimesh.SetInstanceCustomData(index, new Color(0f, 0f, 0f, 0f));
    }

    // One card at one depth, planted at its base so lowering it by half its height centres it on
    // the camera axis, which is what makes the near and the far card concentric. A card asked for
    // mid-fade gets a band that makes its alpha exactly one half there, which is the dithered state
    // the far ridge draws in; a full card gets the all-zero band that never fades. Distances are
    // squared, as the shader's own test is.
    private static void Place(MultiMeshInstance3D node, ClutterBuilder.KindExport kind,
        int index, float z, bool full)
    {
        node.Multimesh!.SetInstanceTransform(index, new Transform3D(
            Basis.Identity, new Vector3(0f, -kind.Height / 2f, z)));
        float far = 2f * z * z;
        node.Multimesh.SetInstanceCustomData(index, full
            ? new Color(0f, 0f, 0f, 0f)
            : new Color(0f, far, 1f / far, 0f));
    }

    // The pane's pixels after a forced draw. 3D geometry reaches the rendering server as it enters
    // the tree, so unlike a canvas item it photographs from inside a suite's single frame.
    private static byte[] Shot(SubViewport pane)
    {
        // ⚠ Push every transform to the rendering server first. A Node3D queues its transform
        // notification for the scene tree's next flush, and a suite never reaches one, so an
        // ordinary MeshInstance3D would otherwise draw at the identity and be clipped away.
        Settle(pane);
        // ⚠ Twice. A MultiMesh's instance buffer is uploaded with the frame that follows the write,
        // so a single draw photographs the previous pose and every count comes out of step by one.
        RenderingServer.ForceDraw();
        RenderingServer.ForceDraw();
        var img = pane.GetTexture()?.GetImage();
        return img == null || img.IsEmpty() ? Array.Empty<byte>() : img.GetData();
    }

    private static void Settle(Node node)
    {
        if (node is Node3D spatial)
        {
            spatial.ForceUpdateTransform();
        }
        for (int i = 0; i < node.GetChildCount(); i++)
        {
            Settle(node.GetChild(i));
        }
    }

    // The pixels a subject drew and drew opaquely: different from the empty frame, and the same in
    // both backdrops. A texel the subject only tinted moves with what is behind it and is left out.
    private static bool[] Mask(byte[] empty, byte[] overEmpty, byte[] overBackdrop)
    {
        var mask = new bool[empty.Length / Channels];
        if (overEmpty.Length != empty.Length || overBackdrop.Length != empty.Length)
        {
            return mask;
        }
        for (int i = 0; i + Channels <= empty.Length; i += Channels)
        {
            mask[i / Channels] = Moved(empty, overEmpty, i) && !Moved(overEmpty, overBackdrop, i);
        }
        return mask;
    }

    private static int Count(bool[] mask)
    {
        int n = 0;
        foreach (bool set in mask)
        {
            n += set ? 1 : 0;
        }
        return n;
    }

    private static int Differing(byte[] a, byte[] b, bool[]? mask = null)
    {
        if (a.Length == 0 || a.Length != b.Length)
        {
            return -1;
        }
        int changed = 0;
        for (int i = 0; i + Channels <= a.Length; i += Channels)
        {
            if ((mask == null || mask[i / Channels]) && Moved(a, b, i))
            {
                changed++;
            }
        }
        return changed;
    }

    // Pixels the subject drew over an empty frame and then contributed nothing to once the backdrop
    // was there: the backdrop landed on top of them. Read against the drawn pixels rather than the
    // opaque ones, because a card's feathered edge writes no depth and is where an overpaint lands.
    private static int Erased(byte[] empty, byte[] subject, byte[] backdrop, byte[] both)
    {
        if (empty.Length == 0 || subject.Length != empty.Length
            || backdrop.Length != empty.Length || both.Length != empty.Length)
        {
            return -1;
        }
        int erased = 0;
        for (int i = 0; i + Channels <= empty.Length; i += Channels)
        {
            // ⚠ The drawn side needs a tolerance and the composite side must not have one. At the
            // faint end of the feather the card moves the frame by one Rgb8 step. Over any backdrop
            // it rounds to that backdrop's own value, which reads as an overpaint and is not one.
            if (MovedBy(empty, subject, i, FeatherFloor) && !Moved(backdrop, both, i))
            {
                erased++;
            }
        }
        return erased;
    }

    private static bool Moved(byte[] a, byte[] b, int i)
    {
        for (int c = 0; c < Channels; c++)
        {
            if (a[i + c] != b[i + c])
            {
                return true;
            }
        }
        return false;
    }

    private static bool MovedBy(byte[] a, byte[] b, int i, int tolerance)
    {
        for (int c = 0; c < Channels; c++)
        {
            if (Mathf.Abs(a[i + c] - b[i + c]) > tolerance)
            {
                return true;
            }
        }
        return false;
    }
}
