using System;
using System.Collections.Generic;
using CSVM.Utils;
using Godot;

namespace CSVM.Mech3;

/// <summary>Builds one chapter world and binds its animation program to it: the world+anim half of
/// <c>GameSession</c>'s session build, extracted so <c>--anim-lab</c>
/// builds the same world+runtime a normal session does without duplicating it.
/// Load → <see cref="WorldBuilder"/> → clutter → mission setup → <see cref="AnimProgram"/> →
/// <see cref="AnimRuntime"/> wiring → <c>Bind</c> → sound-prewarm. Stops before the per-view steps
/// (horizon, weather, edge-extender, unplaced-entity watch), which stay in the caller and drive off
/// <see cref="Builder"/>. Does NOT add <see cref="Root"/> to the scene tree; the caller owns
/// that. Decode: docs/architecture.md, this file's entry.</summary>
public sealed class WorldSession
{
    private WorldSession() { }

    /// <summary>The built world subtree (the viewer's <c>_plane</c> in world mode): the
    /// <c>world1</c> node with the texture cycler, clutter, any debug dzpaths, and the
    /// <see cref="AnimRuntime"/> as children. <b>Not</b> yet added to the scene tree, the caller
    /// adds it under <see cref="Options.EffectsParent"/>.</summary>
    public Node3D Root { get; private set; } = null!;

    public AnimRuntime Runtime { get; private set; } = null!;

    /// <summary>The merged animation program (compiled + reader defs). Held so the caller can read
    /// crash-effect params off it and so the lab can drive its picker/timeline from it.</summary>
    public AnimProgram Program { get; private set; } = null!;

    /// <summary>The world builder, kept so the caller can run the per-view steps that stay outside
    /// this class: the unplaced-entity watch, the edge extender, the per-rig horizon, and the final
    /// mesh/collider/scroll counts.</summary>
    public WorldBuilder Builder { get; private set; } = null!;

    /// <summary>The clutter builder (null when the chapter ships no clutter templates), kept for
    /// the edge extender.</summary>
    public ClutterBuilder? Clutter { get; private set; }

    /// <summary>The world's cloudlayer overcast, if any, moved to follow the player by the
    /// caller.</summary>
    public Node3D? CloudDeck { get; private set; }

    /// <summary>The session's LIGHT_STATE point lights. The caller owns it (disposes it on
    /// teardown) so a rebuild drops the previous world's lights.</summary>
    public WorldLights Lights { get; private set; } = null!;

    /// <summary>The chapter's <c>landings.zrd</c> approach triggers this mission can run, resolved
    /// against the built gamez. Empty unless <see cref="Options.LandingTriggers"/> asked for them,
    /// and empty in any mission carrying none of the chapter's approach animations.</summary>
    public IReadOnlyList<LandingApproach> Landings { get; private set; } =
        Array.Empty<LandingApproach>();

    /// <summary>The aircraft an intro definition animates, staged into this world's node table.
    /// Null unless the mission bootstraps a story-mission intro; see <see cref="AircraftStage"/>.
    /// </summary>
    public AircraftStage? Aircraft { get; private set; }

    /// <summary>The mission's pickup proximity sensors, empty outside the four missions that
    /// carry <c>pickups.zrd</c>.</summary>
    public IReadOnlyList<PickupSpec> Pickups { get; private set; } = Array.Empty<PickupSpec>();

    /// <summary>The mission's own <c>cutscenes\</c> definitions, by <c>ANIMATION_NAME</c>
    /// (<see cref="MissionCutscenes"/>). Empty outside the nine story missions that ship the
    /// directory.</summary>
    public IReadOnlyList<string> MissionCutsceneAnims { get; private set; } = Array.Empty<string>();

    /// <summary>Every definition the landing triggers and <see cref="MissionCutsceneAnims"/> can
    /// reach, their own plus the <c>CALL_ANIMATION</c> closure: what a cutscene host has to answer
    /// for, asked by definition rather than by callback code.</summary>
    public IReadOnlyList<string> LandingCutsceneAnims { get; private set; } =
        Array.Empty<string>();

    /// <summary>Build the world named <c>world1</c> and bind its animation program. The archives
    /// are the caller's <c>using</c> locals, see the disposal-lifetime contract on the class.
    /// ⚠ Keep each phase's <see cref="StartupProfile.Record"/> call next to its step; moving one
    /// without the other makes a dropped phase read as a growing <c>rest</c>, not as missing. The
    /// <see cref="LoadProgress"/> report beside it is the same discipline for the screen over the
    /// build, and its fraction is authored, never derived from the phase it follows.</summary>
    public static WorldSession Build(Options o, GameZ gamez, TextureArchive textures,
        SoundArchive? sounds, Dictionary<string, SoundDef>? soundDefs,
        IReadOnlyDictionary<string, SoundGroup>? soundGroups = null)
    {
        var s = new WorldSession();

        // Loaded before the build: scroll rates feed the material cache key
        // (MissionSetup.ScrollByModel); the entity half applies later, in the animation bootstrap.
        long mark = StartupProfile.Mark();
        MissionSetup? missionSetup = null;
        if (o.NodeSubtree != null)
        {
            // Skipped deliberately: the one verb MissionSetup reliably resolves switches the
            // subject off (C1/IA1 hides hk_zep); a node stage shows the subtree in its gamez
            // base state instead.
            Log.Info("world", $"node stage: mission setup skipped for {o.Chapter}/{o.Mission}, the subtree renders in its gamez base state");
        }
        else
        {
            missionSetup = MissionSetup.Load(o.InterpPath, o.Chapter, o.Mission);
            if (missionSetup == null)
            {
                Log.Info("world", $"mission setup: no script for {o.Chapter}/{o.Mission} ({o.InterpPath})");
            }
        }
        StartupProfile.Record("zrdr", mark);
        LoadProgress.Report(LoadStep.MissionFiles);
        // The EFFECTS flipbooks (effects.zrd) are installed on their target materials BEFORE the
        // build, because a material's frame list is part of what SceneBuilder registers with the
        // TextureCycler as it builds. Same ordering the engine uses. See EffectCycles.
        var effectCycles = EffectCycles.Apply(gamez, o.ZrdrPath);
        if (effectCycles.Count > 0)
        {
            Log.Info("world", $"effect cycles: {effectCycles.Count} material(s): {string.Join(", ", effectCycles)}");
        }
        // The area-selected toggles are resolved to gamez node indices here, while the gamez is in
        // hand: Apply runs inside the animation bootstrap, which sees only the built tree.
        missionSetup?.BindPartitions(gamez);
        mark = StartupProfile.Mark();
        var builder = new WorldBuilder(gamez, textures, collision: o.Collision,
            scrollOverrides: missionSetup?.ScrollByModel(gamez),
            debugClutterFlag: o.DebugClutterFlag, hiddenAlpha: o.HiddenAlpha);
        // A mission-structure node authors an owner per mission of the chapter, so the build has
        // to know which mission it is standing in before it stamps any team.
        builder.MissionSlot = GameZ.MissionSlotOf(o.Mission);
        s.Builder = builder;
        // every chapter has exactly one world node; --node= replaces it with one named subtree
        var root = o.NodeSubtree is { } only
            ? builder.BuildNode(gamez, only)
            : builder.Build("world1");
        StartupProfile.Record("world", mark);
        LoadProgress.Report(LoadStep.WorldScene);
        s.Root = root;
        // The original's material texture flipbooks (animated water/surf/wake/splash and the
        // walking crowd). Parented to the world so a session teardown takes it too.
        if (builder.Cycler.Count > 0)
        {
            builder.Cycler.Debug = o.DebugAnim;
            root.AddChild(builder.Cycler);
            Log.Info("world", $"texture cycles: {builder.Cycler.Count} animated material(s): {string.Join(", ", builder.Cycler.Summary)}");
        }
        s.CloudDeck = builder.CloudDeck;     // the cloudlayer overcast, moved to follow the player

        // --debug-dzpaths: the mission's danger-zone route ribbons (world build skips them,
        // AI/route data the original never renders). Debug inspection only.
        if (o.DebugDzPaths && builder.BuildDzPaths() is { } dzpaths)
        {
            root.AddChild(dzpaths);
            Log.Info("world", $"debug: dzpaths route ribbons built");
        }

        // Clutter: forest trees, river bushes, and C2/C5's 3D city blocks (Clutter.cs). Sprites are
        // never solid (billboards have no side to hit); 3D decorations are, in flight, both user
        // decisions; see docs/org/clutter.md for the Spruce Goose misread this settled.
        mark = StartupProfile.Mark();
        ClutterBuilder? clutterBuilder = null;
        Node3D? clutterRoot = null;
        // --node= has no clutter terrain; --clutter-templates= A/Bs one district (no_clutter still
        // gates per polygon); --no-clutter wins over both as the stronger statement.
        var clutterNames = o.NodeSubtree != null || o.NoClutter
            ? new List<string>()
            : o.ClutterTemplates is { } wanted
                ? ClutterBuilder.OverrideTemplateNames(gamez, wanted)
                : ClutterBuilder.TemplateNames(o.InterpPath, o.Chapter);
        if (o.NoClutter)
        {
            Log.Info("world", $"clutter: skipped (--no-clutter)");
        }
        else if (clutterNames.Count > 0)
        {
            // templates.zrd drives `substitute` and `scale_range` (docs/formats/templates.md).
            // Null when the chapter ships no such file, every decoration then stands at its
            // authored model and size, which is also what an empty file (C1C/C2B) produces.
            var clutterProps = ClutterTemplateSpec.Load(o.ChapterZrdrPath);
            clutterBuilder = new ClutterBuilder(gamez, textures, builder.Scene, clutterProps);
            if (clutterBuilder.Build(clutterNames, collision: o.Collision) is { } clutter)
            {
                root.AddChild(clutter);
                clutterRoot = clutter;
                // Shared shapes: N distinct shapes / T distinct triangles,
                // attached M times, the distinct totals, not the expanded
                // per-attachment triangle count.
                Log.Info("world", $"clutter: {clutterBuilder.InstanceCount} sprites{(clutterBuilder.SolidCount > 0 ? $" + {clutterBuilder.SolidCount} 3D decorations{(clutterBuilder.SolidCollisionShapes > 0 ? $" ({clutterBuilder.SolidCollisionShapes} shared collision shapes, {clutterBuilder.SolidCollisionTriangles} tris ({clutterBuilder.SolidCollisionOneSidedTriangles} of them one-sided in the source, kept two-sided here), {clutterBuilder.SolidCollisionInstances} attachments)" : "")}" : "")} ({clutterBuilder.Summary})");
            }
        }
        else if (o.NodeSubtree == null)
        {
            Log.Info("world", $"clutter: no templates for {o.Chapter} ({o.InterpPath})");
        }
        StartupProfile.Record("clutter", mark);
        LoadProgress.Report(LoadStep.Clutter);
        s.Clutter = clutterBuilder;
        // The transparency census a draw-order question is read against. It says which classes this
        // chapter carries, and which of them an isolation run left out.
        string hidden = o.HiddenAlpha == SceneBuilder.TransparencyClass.None
            ? string.Empty : $"; hidden {o.HiddenAlpha}";
        Log.Info("world", $"alpha classes: surfaces blend={builder.BlendSurfaceCount} scissor={builder.ScissorSurfaceCount}; cards blend={clutterBuilder?.BlendCardKinds ?? 0} scissor={clutterBuilder?.ScissorCardKinds ?? 0}{hidden}");

        // --debug-clutterflag: force clutter blue and print the census. Blue is the one colour
        // the world shader cannot express itself, a decoration's own polygons are unflagged,
        // so without it a flagged city block misreads as clutter-eligible ground.
        if (o.DebugClutterFlag)
        {
            int painted = clutterRoot != null ? TintClutterBlue(clutterRoot) : 0;
            Log.Info("world", $"debug: --debug-clutterflag view, world polygons no_clutter={builder.Scene.FlaggedPolygonCount} (red), clear={builder.Scene.ClearPolygonCount} (green), over the models built for {o.Chapter}; clutter blue ({painted} multimesh{(painted == 1 ? ")" : "es)")}");
        }

        // Base states first, then ON_STARTUP + startanims, which now play rather than being posed
        // at end state (hangar doors swing, the C1 train drives its loop). The program merges the
        // compiled cam_anim/mis_anim archives with the three zrdr scopes.
        mark = StartupProfile.Mark();
        var chapterZrdrPath = o.ChapterZrdrPath;
        var (chapterAnimPath, missionAnimPath) =
            AnimProgram.ArchivePaths(o.DataRoot, o.Chapter, o.Mission);
        var animProgram = o.Decode == null
            ? AnimProgram.Load(o.ZrdrPath, chapterZrdrPath, o.MissionZrdrPath,
                chapterAnimPath, missionAnimPath)
            : o.Decode.Anim(o.ZrdrPath, chapterZrdrPath, o.MissionZrdrPath,
                chapterAnimPath, missionAnimPath);
        StartupProfile.Record("anim", mark);
        LoadProgress.Report(LoadStep.AnimProgram);
        s.Program = animProgram;
        if (o.LandingTriggers)
        {
            s.Landings = LandingApproaches.Resolve(
                chapterZrdrPath, gamez, name => animProgram.ByAnimName(name).Count > 0);
            s.Pickups = CSVM.Mech3.Pickups.Load(o.MissionZrdrPath);
            s.MissionCutsceneAnims = MissionCutscenes.AnimNames(o.MissionZrdrPath);
            s.LandingCutsceneAnims = CutsceneAnimsOf(animProgram, s.Landings, s.MissionCutsceneAnims);
        }
        // Puffer factory retirement: see Options.TexturesOutliveBuild. root is the caller's
        // eventual scene-tree parent for the whole world subtree, so any enhanced-mode omnis
        // WorldLights spawns under it inherit the same lifetime as everything else here.
        var lights = new WorldLights(root);
        s.Lights = lights;
        // Sealed template stage: the ambient world pools/stages nothing hidden, its templates ARE
        // the world's own nodes. See Options.PlacesCalledTemplates for the one exception.
        var animRuntime = new AnimRuntime(AnimRuntime.NewTemplateStage(
            placesCalled: o.PlacesCalledTemplates, debugMotions: o.DebugAnim))
        {
            DebugMotions = o.DebugAnim,
            QualityLod = o.AnimLod,
            AutoStart = o.AutoStart,
            Seed = o.RuntimeSeed,
            Setup = missionSetup,
            EmitterFactory = o.EmitterFactory ?? o.ArchiveEmitterFactory(textures, o.EffectsParent),
            // Where the LIGHT_STATE point term reaches the world shaders. Owned by the caller so a
            // teardown drops the previous world's lights.
            Lights = lights,
            // The world's ambient SOUND_NODE emitters. Null when muted or soundless, which makes
            // the whole feature inert rather than half-built.
            Sounds = soundDefs != null && sounds != null
                ? new WorldSounds(soundDefs, soundGroups)
                {
                    Loader = (d, warn) => sounds.Find(d.WavName, d.Looped, warn),
                    Debug = o.DebugAnim,
                }
                : null,
            // PLAYER_RANGE conditions measure from the nearest player (PlayerPositions);
            // PlayerPosition is only the fallback for a runtime with no PlayerPositions wired.
            // Both resolved per call because no camera exists yet here.
            PlayerPosition = o.PlayerPosition,
            PlayerPositions = o.PlayerPositions,
            LightViewerPositions = o.LightViewerPositions,
            FirstPersonView = o.FirstPersonView,
            // On a single-subtree stage most definitions legitimately resolve nothing, so the bind
            // has to SAY which of "no handler ever fires" and "the node is not here" happened,
            // from outside they are the same still object.
            ReportResolution = o.NodeSubtree != null,
            // MaxRootLift's premise is a whole-world node count; one subtree drops under the cap
            // and lets generic ANIMATION_ROOT_NAMEs anchor definitions that have nothing to do
            // with it. Refused here so the lab's picker lists what actually belongs to the stage.
            SuppressRootLift = o.NodeSubtree != null,
        };
        s.Runtime = animRuntime;
        animRuntime.CallbackHost = o.CallbackHost;
        animRuntime.MissionTriggerOwner = o.TriggerOwner;
        animRuntime.FogStateSink = o.FogStateSink;
        // A mission cutscene the mission ALSO lists in startanims is armed at the bootstrap, where
        // the range gate already holds it; only one armed by a runtime CALL_ANIMATION needs the
        // gate at the call, and giving both to the same definition would let the call jump it.
        if (s.MissionCutsceneAnims.Count > 0)
        {
            var armedAtStart = new HashSet<string>(animProgram.StartAnims, StringComparer.OrdinalIgnoreCase);
            foreach (var anim in s.MissionCutsceneAnims)
            {
                if (!armedAtStart.Contains(anim))
                {
                    animRuntime.RangeGatedCalls.Add(anim);
                }
            }
        }
        string? introAnim = o.Cutscenes is { } names ? BootstrapCutsceneOf(animProgram, names) : null;
        bool intro = introAnim != null;
        bool cutscenes = intro || s.Landings.Count > 0 || s.MissionCutsceneAnims.Count > 0;
        if (o.Cutscenes is { } rootNames && cutscenes)
        {
            BuildCutsceneRoots(root, gamez, builder, animProgram, rootNames);
        }

        // Not gated on cutscenes: a staged prop is ordinary mission content, and its definitions run
        // out of the mission's startanims in every mode that builds the whole world.
        if (o.NodeSubtree == null)
        {
            BuildStagedProps(root, gamez, builder, animProgram);
        }

        // ⚠ Every cutscene, not an intro alone: a mid-mission definition poses the flown aeroplane
        // on the same `player` marker, and with no stage the pilot is held undrawn throughout.
        // Before the bind, like the roots above (docs/architecture.md).
        if (o.Cutscenes != null && cutscenes && o.PlanesGamezPath is { Length: > 0 } planesPath)
        {
            var planesGamez = o.Decode == null ? GameZ.Load(planesPath) : o.Decode.Gamez(planesPath);
            s.Aircraft = AircraftStage.Build(root, gamez.Nodes.Count, planesGamez, textures);
        }
        // The emitter pool has to be in the tree before the bootstrap builds into it.
        if (animRuntime.Sounds is { } worldSounds)
        {
            o.EffectsParent.AddChild(worldSounds);
            worldSounds.SetListeners(o.ListenerPositions
                                     ?? (() => new[] { o.PlayerPosition() }));
        }
        // A CALL_ANIMATION callee on a library-root gamez node (docs/formats/gamez.md) builds
        // LAZILY on first call, pooled per anchor and sized by EffectPools (docs/architecture.md).
        if (o.NodeSubtree == null)
        {
            var pools = EffectPools.Load();
            var libraryPools = new Dictionary<string, List<(Node3D Copy, ulong Owner, object Site)>>(StringComparer.OrdinalIgnoreCase);
            // Stands in for the authored event when a caller names none, so a site-less caller
            // keeps one copy per anchor rather than one per call.
            var unkeyedSite = new object();
            // The aircraft archive's own parachutist, staged into this world rather than reachable
            // through the chapter gamez: the original instances it out of a library root its world
            // walk never reaches, which is exactly what this pool is for (docs/architecture.md).
            var stagedChute = s.Aircraft?.Chuteman;
            animRuntime.ResolveLibraryRoot = (name, callAnchor, callSite) =>
            {
                bool fromStage = stagedChute != null
                    && string.Equals(name, AircraftStage.ChuteNode, StringComparison.OrdinalIgnoreCase);
                // ⚠ A caller naming no site asks for the actor where it stands, and this figure's
                // script poses its children in WORLD coordinates: relocated onto that caller's
                // anchor it would hang kilometres from the shot (docs/architecture.md).
                if (fromStage && callSite == null)
                    return null;
                if (!libraryPools.TryGetValue(name, out var copies))
                {
                    if (!fromStage
                        && (gamez.FindByName(name) is not { } firstLookup || !gamez.IsLibraryRoot(firstLookup)))
                        return null;
                    copies = new List<(Node3D, ulong, object)>();
                    libraryPools[name] = copies;
                }
                ulong ownerId = callAnchor.GetInstanceId();
                object site = callSite ?? unkeyedSite;
                foreach (var (copy, owner, claimed) in copies)
                    if (owner == ownerId && ReferenceEquals(claimed, site))
                        return copy; // this call's own prior copy, reuse it, not a fresh one
                if (copies.Count < pools.LocalCallPoolSize(name))
                {
                    // The staged actor IS the first copy: it is already in the tree and already in
                    // the runtime's node table, and a mission with one caller must keep using it.
                    Node3D? made = fromStage
                        ? (copies.Count == 0 ? stagedChute : stagedChute!.Duplicate() as Node3D)
                        : (gamez.FindByName(name) is { } gzNode
                            ? builder.Scene.BuildSubtree(gzNode, collisionSkip: _ => true) : null);
                    if (made == null)
                        return null;
                    if (!ReferenceEquals(made, stagedChute))
                    {
                        made.Transform = Transform3D.Identity;
                        root.AddChild(made);
                        animRuntime.IndexPooledCopy(made);
                    }
                    copies.Add((made, ownerId, site));
                    return made;
                }
                // Pool at capacity: recycle the oldest-owned copy (round-robin), same wrap the
                // single-copy path always had.
                var (recycled, _, _) = copies[0];
                copies.RemoveAt(0);
                copies.Add((recycled, ownerId, site));
                return recycled;
            };
        }
        // The opening cutscene starts inside the bind's own start-list walk rather than through a
        // trigger call, so its slot is written here instead, for the same reason and just as early
        // (docs/formats/anim-definitions/cutscenes.md).
        if (introAnim != null)
        {
            o.TriggerOwner?.Invoke(introAnim);
        }

        mark = StartupProfile.Mark();
        animRuntime.Bind(root, animProgram);
        StartupProfile.Record("bind", mark);
        LoadProgress.Report(LoadStep.RuntimeBound);
        foreach (string line in animRuntime.ResolutionLines())
        {
            Log.Info("anim", $"{line}");
        }
        // Only the REAL factory this method built is tied to `textures`'s scope; a caller-supplied
        // one holds no archive reference, so it needs no retirement.
        if (o.EmitterFactory == null && !o.TexturesOutliveBuild)
        {
            animRuntime.Emitters.RetireFactory();
        }
        // Same rule as the puffer factory: the zip handle dies with the caller's scope. Most
        // SOUND_NODE events are first reached at RUNTIME (an OnCall def, a delayed CallSequence),
        // so decode everything the program can ask for now, before the archive closes.
        if (animRuntime.Sounds is { } builtSounds)
        {
            mark = StartupProfile.Mark();
            int prewarmed = builtSounds.Prewarm(animProgram.SoundNodeNames());
            // The one-shot SOUND streams too (destruction/damage audio), first reached at runtime
            // from a death or damage sequence, always after this scope closes. Prewarm expands a
            // SOUND_GROUPS name to its members.
            prewarmed += builtSounds.Prewarm(animProgram.OneShotSoundNames());
            // The mission's combat-voice clips (CombatVoice.SessionPrewarmNames): every line an AI
            // pilot could speak is first reached at runtime, so it must decode now or stay silent.
            if (o.VoiceClipNames is { Count: > 0 } voiceNames)
            {
                int voiced = builtSounds.Prewarm(voiceNames);
                prewarmed += voiced;
                Log.Info("anim", $"anim: prewarmed {voiced} combat-voice stream(s) of {voiceNames.Count} roster clip def(s)");
            }
            if (o.ExtraPrewarmNames is { Count: > 0 } extraNames)
            {
                prewarmed += builtSounds.Prewarm(extraNames);
            }
            StartupProfile.Record("prewarm", mark);
            LoadProgress.Report(LoadStep.SoundPrewarm);
            if (prewarmed > 0)
            {
                Log.Info("anim", $"anim: prewarmed {prewarmed} sound stream(s) before the archive closed");
            }
            if (!o.SoundsOutliveBuild)
            {
                builtSounds.Loader = null;
            }
        }
        root.AddChild(animRuntime);

        return s;
    }

    // Which story-mission opening cutscene does this mission bootstrap, if any? Read over the start
    // list's whole CALL_ANIMATION closure, not the list alone: C3/M03's `cgzep_camera` is reached
    // from its start anim `calldestroy_the_cargozep` and appears in no list. ⚠ Ask by name, not
    // by the callback codes: `player_setup`, which every mission opening without a movie
    // bootstraps, authors the same nine, so a code test would give every mission a held world.
    private static string? BootstrapCutsceneOf(AnimProgram program, CutsceneNames names)
    {
        foreach (var def in program.Subset(program.StartAnims).Defs)
        {
            foreach (string intro in names.IntroAnims)
            {
                if (string.Equals(def.AnimName, intro, StringComparison.Ordinal))
                {
                    return def.AnimName;
                }
            }
        }

        return null;
    }

    // Every definition the chapter's approach triggers can raise a callback from: the row's own
    // animation plus what it reaches by CALL_ANIMATION, which is where the drop and hookup movies
    // actually live (the row's definition only aims the drop and calls one).
    private static List<string> CutsceneAnimsOf(
        AnimProgram program, IReadOnlyList<LandingApproach> landings, IReadOnlyList<string> mission)
    {
        var roots = new List<string>(landings.Count + mission.Count);
        foreach (var approach in landings)
        {
            roots.Add(approach.Anim);
        }

        // The mission's own cutscene definitions are roots too: CM07's hangar drop is armed by an
        // ambient CALL_ANIMATION, so no landings row names it and the host would decline its codes.
        roots.AddRange(mission);

        var names = new List<string>();
        foreach (var def in program.Subset(roots).Defs)
        {
            if (def.AnimName is { } name && !names.Contains(name))
            {
                names.Add(name);
            }
        }

        return names;
    }

    // The two roots a cutscene definition drives, neither of which the world1 walk reaches: the
    // bodiless `camera1` the definitions pose, and the parentless `letterbox` bars, built here so
    // the bind anchors both and the shared letterbox definition's own base state switches the bars
    // off. Standing them up before the bind is what makes the bars data rather than an overlay
    // (docs/formats/anim-definitions/cutscenes.md).
    private static void BuildCutsceneRoots(Node3D root, GameZ gamez, WorldBuilder builder,
        AnimProgram program, CutsceneNames names)
    {
        BuildCompositionFrames(root, gamez, program);
        var camera = new Node3D { Name = names.CameraNode };
        // ⚠ Stamp the gamez index a scene-built node would carry. Every compiled cutscene binds
        // `camera1` through its symbol table, and a claimed index with no node behind it makes
        // AnimRuntime.Targets drop the event rather than name-match around it.
        if (gamez.FindByName(names.CameraNode) is { } cameraNode)
        {
            camera.SetMeta(AnimRuntime.NameMeta, cameraNode.Name);
            camera.SetMeta(AnimRuntime.IndexMeta, cameraNode.Index);
        }

        root.AddChild(camera);
        if (gamez.FindByName(names.BarsNode) is not { } bars)
        {
            return;
        }

        if (builder.Scene.BuildSubtree(bars, collisionSkip: _ => true) is not { } built)
        {
            return;
        }

        built.Transform = Transform3D.Identity;
        AnimRuntime.SetSubtreeActive(built, false);
        root.AddChild(built);
    }

    // The composition frames this program reparents into: a bodiless, childless gamez library root
    // the world1 walk never reaches, named as an OBJECT_ADD_CHILD parent. A cutscene writes its
    // keyframes in such a frame's space and moves the camera under it, so a frame with no node
    // behind it leaves the whole shot in world space at the gamez origin. Two exist in this
    // install, CM02's `wingwalk_parent` and CM07's `carney_pickup_parent`
    // (docs/formats/anim-definitions/cutscenes.md).
    private static void BuildCompositionFrames(Node3D root, GameZ gamez, AnimProgram program)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var def in program.Defs)
        {
            foreach (var sequence in Blocks(def))
            {
                foreach (var ev in sequence.Events)
                {
                    if (!string.Equals(ev.Kind, "ObjectAddChild", StringComparison.Ordinal)
                        || ev.Data.Str("parent") is not { } parent
                        || !seen.Add(parent)
                        || gamez.FindByName(parent) is not { } node
                        || node.Children.Count > 0 || node.MeshIndex >= 0
                        || !gamez.IsLibraryRoot(node))
                    {
                        continue;
                    }

                    // ⚠ Stamp name and gamez index, the way `camera1` above is stamped: a compiled
                    // definition binds this node through its symbol table, and a claimed index
                    // with no node behind it is dropped rather than name-matched.
                    var frame = new Node3D { Name = node.Name };
                    frame.SetMeta(AnimRuntime.NameMeta, node.Name);
                    frame.SetMeta(AnimRuntime.IndexMeta, node.Index);
                    root.AddChild(frame);
                    frame.Transform = node.Local ?? Transform3D.Identity;
                    // ⚠ Keep this: a placed root, and its motion must launch from where the
                    // definition put it rather than re-home to its authored rest at the map
                    // origin (docs/org/objectMotion.md, "The re-home rule").
                    frame.TopLevel = true;
                    Log.Info("anim", $"anim: composition frame '{node.Name}' (gamez {node.Index}) stood up for a cutscene reparent");
                }
            }
        }
    }

    // The staged props this program hangs onto placed world content: an unplaced, model-bearing
    // gamez root that ships switched off, named as an OBJECT_ADD_CHILD child. CM19's launch hook is
    // the case: three place definitions park a display aeroplane on the hook's `bmhookpoint` at
    // NEW_GAME_START and the launch definition switches one on for its ride down, so a world with no
    // such node flies an empty hook (docs/formats/mission-entities/enemy-generators.md).
    private static void BuildStagedProps(Node3D root, GameZ gamez, WorldBuilder builder,
        AnimProgram program)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var def in program.Defs)
        {
            foreach (var sequence in Blocks(def))
            {
                foreach (var ev in sequence.Events)
                {
                    // The parent must be placed: a prop whose host is itself off the world walk has
                    // nowhere to land, and building it would only add a hidden node.
                    if (!string.Equals(ev.Kind, "ObjectAddChild", StringComparison.Ordinal)
                        || ev.Data.Str("child") is not { } child
                        || ev.Data.Str("parent") is not { } parent
                        || gamez.FindByName(child) is not { } node
                        || !gamez.IsStagedProp(node)
                        || gamez.FindByName(parent) is not { } host
                        || !gamez.IsPlaced(host)
                        || !seen.Add(child))
                    {
                        continue;
                    }

                    if (builder.Scene.BuildSubtree(node, collisionSkip: _ => true) is not { } built)
                    {
                        continue;
                    }

                    // ⚠ Build it switched off, whatever the shipped flag says about its parts: the
                    // definition that parks it here is also what reveals it, and a prop visible at
                    // its authored rest hangs an aeroplane at the map origin until then.
                    AnimRuntime.SetSubtreeActive(built, false);
                    root.AddChild(built);
                    Log.Info("anim", $"anim: staged prop '{node.Name}' (gamez {node.Index}) stood up for a reparent onto '{host.Name}'");
                }
            }
        }
    }

    private static IEnumerable<AnimSequence> Blocks(AnimDefinition def)
    {
        if (def.ResetState is { } reset)
        {
            yield return reset;
        }

        foreach (var sequence in def.Sequences)
        {
            yield return sequence;
        }
    }

    // Stamps SceneBuilder.ClutterColor onto every clutter draw under
    // `clutter` as a full-strength SceneBuilder.TintParam, and
    // returns how many it painted. Per instance rather than per material because both clutter
    // paths are MultiMeshes sharing the placed world's materials, the sprite cards' own shader
    // and, for the 3D decorations, literally the world's, so a material-level colour would
    // repaint the ground with them.
    private static int TintClutterBlue(Node3D clutter)
    {
        int painted = 0;
        foreach (var child in clutter.GetChildren())
        {
            if (child is MultiMeshInstance3D mmi)
            {
                mmi.SetInstanceShaderParameter(SceneBuilder.TintParam, SceneBuilder.ClutterColor);
                painted++;
            }
            else if (child is Node3D nested)
            {
                painted += TintClutterBlue(nested);
            }
        }
        return painted;
    }

    /// <summary>The names the cutscene half of the build stands on. They are the camera node a
    /// definition poses, the letterbox bars' node, and the definitions that open a story mission. The cutscene host owns all three and hands them in, so the build asks by
    /// name and never names the host.</summary>
    public sealed record CutsceneNames(string CameraNode, string BarsNode, IReadOnlyList<string> IntroAnims);

    /// <summary>Build settings that vary by mode; the loaded archives are passed to
    /// <see cref="Build"/> separately.</summary>
    public sealed class Options
    {
        public required string DataRoot { get; init; }
        public required string Chapter { get; init; }
        public required string Mission { get; init; }
        public required string ZrdrPath { get; init; }
        public required string InterpPath { get; init; }
        public required string MissionZrdrPath { get; init; }

        /// <summary>The chapter's own zrdr scope, which <c>templates.zrd</c>, <c>landings.zrd</c>
        /// and the chapter animation layer are read from. The caller resolves it, since the data
        /// root's layout is the session's to know.</summary>
        public required string ChapterZrdrPath { get; init; }

        /// <summary>Where this build takes its <see cref="AnimProgram"/> and its aircraft archive
        /// from, when the caller has one. Null (the default) loads both fresh, so a session that
        /// leaves a chapter retains nothing of it. See <see cref="DecodeCache"/> for what a
        /// non-null value makes shared, and the read-only contract that binds.</summary>
        public DecodeCache? Decode { get; init; }

        /// <summary>The shared aircraft archive, for <see cref="AircraftStage"/>. Read only when
        /// this mission bootstraps an intro; every other session leaves it unopened, so its node
        /// census is exactly what it was.</summary>
        public string? PlanesGamezPath { get; init; }

        /// <summary>Parent for the effect siblings the bootstrap builds, the world's ambient
        /// SOUND_NODE emitters and every PUFFER_STATE emitter. In the viewer this is the session
        /// root (<c>_worldRoot</c>), a sibling of <see cref="Root"/>, not <see cref="Root"/>
        /// itself.</summary>
        public required Node3D EffectsParent { get; init; }

        /// <summary>The PLAYER_RANGE fallback for a runtime with no <see cref="PlayerPositions"/>
        /// wired (a real session always wires both). Resolved per call because no
        /// camera exists yet at build time; player 1's camera is the honest single-camera answer
        /// in every mode (chase cam, free camera, or the orbit eye).</summary>
        public required Func<Vector3> PlayerPosition { get; init; }

        /// <summary>Every 3D audio listener's position, one per pane, since every pane camera is
        /// listener-enabled (UI.Boards.SplitScreen). Read only by the debug sound log's distance column;
        /// the engine reads the listeners themselves. Null → <see cref="PlayerPosition"/> alone.</summary>
        public Func<IReadOnlyList<Vector3>>? ListenerPositions { get; init; }

        /// <summary>Every player's position, for the EXECUTION_BY_RANGE proximity gate and every
        /// PLAYER_RANGE condition, the aircraft themselves in flight, not the
        /// chase cameras (a chase camera trails ~25 m behind, which is most of the spiderweb's
        /// 50 m radius). Null → both fall back to <see cref="PlayerPosition"/>.</summary>
        public Func<IReadOnlyList<Vector3>>? PlayerPositions { get; init; }

        /// <summary>Every pane's camera, for budgeting the world's <c>LIGHT_STATE</c> lights
        /// against the nearest one, the draw-rule seam (`ViewerSet.Positions`),
        /// not <see cref="PlayerPositions"/>. Null → the runtime falls back to
        /// <see cref="PlayerPosition"/> alone.</summary>
        public Func<IReadOnlyList<Vector3>>? LightViewerPositions { get; init; }

        /// <summary>Whether any human pilot is in one of the two first-person views, for the
        /// <c>PLAYER_1ST_PERSON</c> condition. Null → false, the pre-cockpit answer.</summary>
        public Func<bool>? FirstPersonView { get; init; }

        /// <summary>Build world colliders (true in flight; false for a static or lab view).</summary>
        public bool Collision { get; init; }

        public bool DebugAnim { get; init; }
        public int AnimLod { get; init; } = AnimRuntime.HighLod;
        public bool DebugDzPaths { get; init; }

        /// <summary><c>--no-clutter</c>: skip the ground-clutter build (the tree/bush cards and the
        /// 3D city-block decorations) entirely, leaving the ground they stand on visible.</summary>
        public bool NoClutter { get; init; }

        /// <summary><c>--debug-clutterflag</c>: build the world recoloured by the decoded
        /// per-polygon <c>no_clutter</c> flag, flagged red, clear green, and force whatever
        /// clutter is built blue, so "which ground is flagged" and "where the decorations are" can
        /// never be confused. See <see cref="SceneBuilder.DebugClutterFlag"/>.</summary>
        public bool DebugClutterFlag { get; init; }

        /// <summary><c>--hide-alpha=</c>: leave the named transparency classes out of the build. A
        /// frame where two of them paint in the wrong order then names the pair by elimination.
        /// See <see cref="SceneBuilder.HiddenAlpha"/>.</summary>
        public SceneBuilder.TransparencyClass HiddenAlpha { get; init; }

        /// <summary><c>--clutter-templates=</c>: build these clutter templates instead of the
        /// chapter's own registered list, so one district at a time can be A/B'd against the
        /// original. The per-polygon <c>no_clutter</c> gate still applies. Null → the chapter's
        /// own list. <see cref="NoClutter"/> wins over this.</summary>
        public IReadOnlyList<string>? ClutterTemplates { get; init; }

        /// <summary>The caller's <see cref="TextureArchive"/> outlives this build, so a
        /// runtime-reached <c>PUFFER_STATE</c> (death trails, ON_CALL dust/smoke) can still bake
        /// its atlas. True in a game session (archive freed on return-to-menu); false only where the
        /// archive is genuinely a <c>using</c> local of the build (the test harness).
        /// ⚠ Default false is the SAFE choice, not the common one, left false by a caller that
        /// owns its archive, a runtime-reached puffer silently builds nothing.</summary>
        public bool TexturesOutliveBuild { get; init; }

        /// <summary>The factory <see cref="AnimRuntime"/> builds <c>PUFFER_STATE</c> emitters
        /// through. Null (default) takes the real one <see cref="ArchiveEmitterFactory"/> builds
        /// over this build's archive, subject to <see cref="TexturesOutliveBuild"/>. A
        /// caller-supplied one, such as <c>CountingEmitterFactory</c>, holds no archive and is
        /// never auto-retired. ⚠ Read once, here, a post-<see cref="Build"/> swap would miss the
        /// bootstrap, where most <c>PUFFER_STATE</c>s fire.</summary>
        public Anim.IEmitterFactory? EmitterFactory { get; init; }

        /// <summary>Builds the real <c>PUFFER_STATE</c> emitter factory over this build's texture
        /// archive and <see cref="EffectsParent"/>. The caller closes over the wind and pane camera
        /// poses its emitters read, which must be the instance the weather rig publishes to.
        /// Unread when <see cref="EmitterFactory"/> brings its own.</summary>
        public required Func<TextureArchive, Node3D, Anim.IEmitterFactory> ArchiveEmitterFactory { get; init; }

        /// <summary>The mission's combat-voice clip defs (<see cref="CombatVoice.SessionPrewarmNames"/>),
        /// prewarmed with the animation program's own sound names so a pilot's line still decodes
        /// after the archive closes. Null or empty (the default) prewarms no voice: a viewer or
        /// lab session hosts no talking pilots. The caller computes the set because which pilots
        /// talk is session policy, not world-build mechanics.</summary>
        public IReadOnlyCollection<string>? VoiceClipNames { get; init; }

        /// <summary>Extra sound-group names to prewarm alongside the anim program's own, for a
        /// vocabulary <see cref="AnimProgram"/> never sees (the campaign's
        /// <c>ObjectiveScript.SoundGroupNames()</c> is the only caller today). Null or empty (the
        /// default) prewarms nothing extra: a non-campaign session has no such names.</summary>
        public IReadOnlyCollection<string>? ExtraPrewarmNames { get; init; }

        /// <summary>The caller's <see cref="SoundArchive"/> outlives this build, so
        /// <c>WorldSounds.Loader</c> stays live for names the prewarm did not reach. Separate from
        /// <see cref="TexturesOutliveBuild"/> because the lifetimes are separate: a game session
        /// scopes the sound archive to the build (the prewarm is what makes that survivable) while
        /// its texture archive lasts the session. Only the lab keeps both.</summary>
        public bool SoundsOutliveBuild { get; init; }

        /// <summary>Whether the bootstrap runs the ambient-playback passes (ON_STARTUP defs +
        /// startanims). True, the default, in every game/viewer/flight session; the animation
        /// lab sets false for its quiet stage and runs them on demand through
        /// <see cref="AnimRuntime.StartAmbient"/>.</summary>
        public bool AutoStart { get; init; } = true;

        /// <summary>Whether a CALL_ANIMATION relocates its callee's effect-template root onto the
        /// call site (<see cref="Anim.TemplateStage{TNode}.Places"/>). False (default) in every
        /// game/viewer/flight session, where the ambient boot must stay byte-identical; the
        /// animation lab sets true so staged templates play at the call site. ⚠ Read once, at
        /// construction, sealed onto the runtime's template stage, not writable
        /// afterwards.</summary>
        public bool PlacesCalledTemplates { get; init; }

        /// <summary>Build the two roots a cutscene definition drives (<c>camera1</c> and the
        /// <c>letterbox</c> bars), which the <c>world1</c> walk never reaches. The mission's
        /// opening definition is recognised by these names too. Null (default) does neither and
        /// leaves every session's node census unchanged. A flown chapter session sets it, because
        /// that is where an intro definition can play.</summary>
        public CutsceneNames? Cutscenes { get; init; }

        /// <summary>Resolve the chapter's <c>landings.zrd</c> approach triggers. Set by a STORY
        /// mission only. ⚠ Not by an Instant Action one, although the original's shared mission
        /// load arms the table there too: C3/IA1 carries <c>hooked_to_klondike</c> and ships its
        /// <c>pz_manual_land/land_on</c> active, so arming it would give an Instant Action sortie a
        /// docking cutscene. Whether the original means to is undecoded
        /// (docs/formats/anim-definitions/cutscenes.md).</summary>
        public bool LandingTriggers { get; init; }

        /// <summary>The <c>CALLBACK</c> host installed on the world runtime before the bootstrap
        /// starts anything, since an intro definition raises its codes the instant it starts. Null
        /// leaves every code to the runtime's own two seams and its census.</summary>
        public Func<int, string?, string?, bool>? CallbackHost { get; init; }

        /// <summary>The trigger slot the started definition is handed to, installed on the runtime
        /// beside <see cref="CallbackHost"/> and told the mission's opening definition directly:
        /// the intro starts in the bootstrap rather than through a trigger call, and its episode
        /// belongs to it however deep the callee that raises its first code sits.</summary>
        public Action<string>? TriggerOwner { get; init; }

        /// <summary>Where a <c>FOG_STATE</c> event goes, installed before the bootstrap for the
        /// same reason as <see cref="CallbackHost"/>: the one shipped use is in an intro
        /// definition's RESET_STATE, applied while the world is still being built.</summary>
        public Action<AnimRuntime.FogStateChange>? FogStateSink { get; init; }

        /// <summary>Pins the runtime's RNG for a reproducible run (see
        /// <see cref="AnimRuntime.Seed"/>). Null, the default, leaves it unseeded: the game.</summary>
        public int? RuntimeSeed { get; init; }

        /// <summary>The <c>--node=</c> stage: build ONLY this gamez subtree instead of the whole
        /// world. Null, the default, is the full chapter build. The caller resolves the name
        /// (<see cref="WorldBuilder.MatchNodes"/>) so a miss can report its candidates and quit
        /// before anything is built.</summary>
        public GameZNode? NodeSubtree { get; init; }
    }
}
