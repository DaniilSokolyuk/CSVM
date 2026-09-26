using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Godot;

namespace CSVM.Mech3;

/// <summary>
/// Every animation definition visible to one mission, merged from the compiled
/// <c>cam_anim</c>/<c>mis_anim</c> archives and the zrdr readers, plus the startanims list that
/// says which run at mission start. Compiled data wins on collision (typed events, resolved node
/// names, the SI motion scripts); readers fill in what was never compiled. Full decode, including
/// why the mission zrdr scope is a library rather than a manifest: docs/formats/anim-definitions.md.
/// ⚠ A mission-scope reader def applies only when the mission's compiled archive contains it, and
/// a shared- or chapter-scope reader FILE only when an <c>ANIMATION_DEFINITION_FILE</c> list the
/// mission sees names it (the shared <c>anim.zrd</c> closure, the chapter's <c>cam_anim.zrd</c>,
/// the mission's <c>mis_anim.zrd</c>): both archives hold per-mission content too, and loading
/// them everywhere re-activates objects a mission's <c>.gw</c> switched off, matching the original
/// (docs/formats/anim-definitions.md "Shared-scope files are listed per mission too").</summary>
public sealed class AnimProgram
{
    private const string SharedIndex = "anim";

    private readonly List<AnimDefinition> _defs = new();
    private readonly List<string> _startAnims = new();
    private readonly List<string> _missionLibrarySkipped = new();
    private readonly SortedSet<string> _sharedFilesSkipped = new(StringComparer.OrdinalIgnoreCase);
    private readonly SortedSet<string> _chapterFilesSkipped = new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, List<AnimDefinition>> _byAnimName =
        new(StringComparer.OrdinalIgnoreCase);

    // The def that holds each identity, so a superseded twin can still hand over the fields only
    // its own source carries (see Add).
    private readonly Dictionary<string, AnimDefinition> _seen =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Every definition, compiled-preferred, in load order. Read-only by type as well as
    /// by convention: one program is shared by every runtime bound from it, and may be shared by
    /// several world builds (<see cref="DecodeCache"/>), so a write here would reach all of
    /// them.</summary>
    public IReadOnlyList<AnimDefinition> Defs => _defs;

    /// <summary>The mission's NEW_GAME_START animation names, in order (startanims.json).
    /// Order matters, C1/IA1 runs hangar3_doors then mp_hangar3_open over the same doors,
    /// and the last write wins.</summary>
    public IReadOnlyList<string> StartAnims => _startAnims;

    /// <summary>Reader defs a loaded compiled manifest supersedes, so were not instantiated
    /// (diagnostics): mission-scope defs the manifest does not list, and the shared/chapter
    /// NAME1 multi-target defs the compiler expands per instance instead. They load, anchor and
    /// register only on a reader-only extraction, where no compiled form exists.</summary>
    public IReadOnlyList<string> MissionLibrarySkipped => _missionLibrarySkipped;

    /// <summary>Shared-scope reader files no <c>ANIMATION_DEFINITION_FILE</c> list this mission
    /// sees names, so none of their defs loaded (diagnostics, by file stem). Empty on a
    /// reader-only extraction and when the shared <c>anim.zrd</c> index is missing.</summary>
    public IReadOnlyCollection<string> SharedFilesSkipped => _sharedFilesSkipped;

    /// <summary>Chapter-scope reader files no <c>ANIMATION_DEFINITION_FILE</c> list this mission
    /// sees names, so none of their defs loaded (diagnostics, by file stem). Empty on a
    /// reader-only extraction, matching <see cref="SharedFilesSkipped"/>'s policy for the shared
    /// scope.</summary>
    public IReadOnlyCollection<string> ChapterFilesSkipped => _chapterFilesSkipped;

    public int CompiledCount { get; private set; }
    public int ReaderCount { get; private set; }
    public int ScriptPoolCount { get; private set; }

    /// <summary>Loads one mission's animation program from the three zrdr scopes (shared,
    /// chapter, mission) and the <c>cam_anim</c> and <c>mis_anim</c> extractions. Any of them
    /// may be missing: a tree without the anim archives gets reader-only behaviour.</summary>
    public static AnimProgram Load(string sharedZrdr, string chapterZrdr, string missionZrdr,
        string chapterAnimPath, string missionAnimPath)
    {
        var program = new AnimProgram();

        // Compiled first, so it wins every collision.
        var missionManifest = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool haveMissionManifest = false;
        foreach (var (path, label) in new[] { (chapterAnimPath, "cam_anim"), (missionAnimPath, "mis_anim") })
        {
            if (AnimArchive.Load(path, label) is not { } archive)
                continue;
            program.ScriptPoolCount += archive.ScriptCount;
            bool isMission = label == "mis_anim";
            if (isMission)
                haveMissionManifest = true;
            foreach (var def in archive.Defs)
            {
                if (isMission)
                    missionManifest.Add(KeyOf(def));
                program.Add(def, compiled: true);
            }
        }

        // The readers fill in what was never compiled. Both shared and chapter scope are gated by
        // the compiled manifest when one loaded (docs/formats/anim-definitions.md "Shared-scope
        // files are listed per mission too"), else this degrades to reader-only behaviour.
        var listedShared = haveMissionManifest
            ? ListedSharedFiles(sharedZrdr, chapterZrdr, missionZrdr)
            : null;
        var listedChapter = haveMissionManifest
            ? ListedChapterFiles(chapterZrdr, missionZrdr)
            : null;
        foreach (var zrdr in new[] { sharedZrdr, chapterZrdr })
        {
            foreach (var def in AnimDefs.LoadArchive(zrdr))
            {
                if (listedShared != null && zrdr == sharedZrdr
                    && !listedShared.Contains(StemOf(def.SourceFile)))
                {
                    program._sharedFilesSkipped.Add(StemOf(def.SourceFile));
                    continue;
                }
                if (listedChapter != null && zrdr == chapterZrdr
                    && !listedChapter.Contains(StemOf(def.SourceFile)))
                {
                    program._chapterFilesSkipped.Add(StemOf(def.SourceFile));
                    continue;
                }

                // NAME1 multi-target defs are per-mission content the compiler expands into
                // mis_anim (see MissionLibrarySkipped); with a manifest present the reader
                // form must not anchor zeppelin sub-parts the loaded mission never authors.
                if (haveMissionManifest && def.MultiTargets.Count > 0)
                {
                    program._missionLibrarySkipped.Add($"{def.AnimName} (NAME1)");
                    continue;
                }
                program.Add(def, compiled: false);
            }
        }

        foreach (var def in AnimDefs.LoadArchive(missionZrdr))
        {
            if (haveMissionManifest && !missionManifest.Contains(KeyOf(def)))
            {
                program._missionLibrarySkipped.Add($"{def.Name}/{def.AnimName}");
                continue;
            }
            program.Add(def, compiled: false);
        }

        program.LoadStartAnims(missionZrdr);
        return program;
    }

    /// <summary>The chapter/mission compiled-archive paths for a session. Like every other
    /// loader, it prefers the unpacked sibling directory <c>--extract-unzip</c> leaves next to
    /// each zip, since loose JSON needs no decompression per def.</summary>
    public static (string Chapter, string Mission) ArchivePaths(string repoRoot, string chapter, string mission)
    {
        static string Prefer(string zipPath)
        {
            var dir = Path.Combine(Path.GetDirectoryName(zipPath) ?? "",
                Path.GetFileNameWithoutExtension(zipPath));
            return Directory.Exists(dir) ? dir : zipPath;
        }
        return (Prefer(Extraction.ZbdTree.Under(repoRoot, chapter + "/cam_anim.zip")),
                Prefer(Extraction.ZbdTree.Under(repoRoot, $"{chapter}/{mission}/mis_anim.zip")));
    }

    /// <summary>Definitions an ANIMATION_NAME refers to (startanims entries, CALL_ANIMATION
    /// targets). Several defs can share a name, template anims instantiate per object.</summary>
    public IReadOnlyList<AnimDefinition> ByAnimName(string animName) =>
        _byAnimName.TryGetValue(animName, out var list) ? list : Array.Empty<AnimDefinition>();

    /// <summary>A minimal program holding only the definitions reachable from
    /// <paramref name="rootAnimName"/> through CALL_ANIMATION, reusing the same
    /// <see cref="AnimDefinition"/> objects so SI-script resolution still works.
    /// ⚠ The per-player crash runtime binds this, not the whole world program: the full
    /// program's generic-named world defs would mis-anchor onto plane parts. StartAnims are left
    /// empty; the crash runtime never auto-starts.</summary>
    public AnimProgram Subset(string rootAnimName) => Subset(new[] { rootAnimName });

    /// <summary>The closure over several roots at once, the world-effects runtime binds the
    /// union of the impact + destruction effect names, so one runtime serves every effect a hit or a
    /// death calls. Same closure rule as the single-root overload; the reused defs share this
    /// program's script pool.</summary>
    public AnimProgram Subset(IEnumerable<string> rootAnimNames)
    {
        var sub = new AnimProgram();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<string>();
        void Enqueue(string name)
        {
            if (!string.IsNullOrEmpty(name) && visited.Add(name))
                queue.Enqueue(name);
        }
        foreach (var root in rootAnimNames)
            Enqueue(root);
        while (queue.Count > 0)
        {
            foreach (var def in ByAnimName(queue.Dequeue()))
            {
                sub.Add(def, compiled: def.Archive != null);
                foreach (var seq in def.Sequences)
                    foreach (var ev in seq.Events)
                        if (ev.Kind == "CallAnimation" && ev.Data.Str("name") is { } called)
                            Enqueue(called);
                if (def.ResetState is { } reset)
                    foreach (var ev in reset.Events)
                        if (ev.Kind == "CallAnimation" && ev.Data.Str("name") is { } rcalled)
                            Enqueue(rcalled);
            }
        }
        sub.ScriptPoolCount = ScriptPoolCount; // the shared pool the reused defs still reach
        return sub;
    }

    /// <summary>Every sound-definition name any <c>SOUND_NODE</c> event in this program can ask
    /// for, deduplicated, including names only reachable at runtime. Feeds
    /// <see cref="WorldSounds.Prewarm"/> so those streams decode while the sound archive is
    /// still open.
    /// ⚠ Walk <see cref="AnimSequence.OnCallOnly"/> and <c>ResetState</c> too: reachability is
    /// the question, not what runs at bootstrap.</summary>
    public IEnumerable<string> SoundNodeNames() => SoundNamesOfKind("SoundNode");

    /// <summary>Every sound name a one-shot <c>SOUND</c> event in this program can ask for, a
    /// sounds.json definition or a <c>SOUND_GROUPS</c> name, deduplicated, including
    /// runtime-only sites. Feeds <see cref="WorldSounds.Prewarm"/>. Same reachability rule as
    /// <see cref="SoundNodeNames"/>.</summary>
    public IEnumerable<string> OneShotSoundNames() => SoundNamesOfKind("Sound");

    /// <summary>The SI script a def's OBJECT_MOTION_SI_SCRIPT slot refers to. The event
    /// carries an index into the def's own <c>si_script_ids</c>, which in turn indexes its
    /// archive's script pool, so both hops happen here.</summary>
    public SiScript? ScriptFor(AnimDefinition def, int slot)
    {
        if (def.Archive == null || slot < 0 || slot >= def.SiScriptIds.Length)
            return null;
        return def.Archive.Script(def.SiScriptIds[slot]);
    }

    /// <summary>A program over definitions built in code, for a suite that drives the runtime on
    /// authored event shapes rather than on a mission's archives. Same dedup rule as a loaded
    /// program; nothing here reads an archive, so the defs carry no SI scripts.</summary>
    internal static AnimProgram FromDefinitions(IEnumerable<AnimDefinition> defs)
    {
        var program = new AnimProgram();
        foreach (var def in defs)
            program.Add(def, compiled: false);
        return program;
    }

    /// <summary>The shared-scope reader files a mission sees, by stem: the closure of the shared
    /// <c>anim.zrd</c> index plus the shared entries of the chapter's <c>cam_anim.zrd</c> and the
    /// mission's <c>mis_anim.zrd</c>. Null when the index is absent, which leaves the scope
    /// ungated rather than empty.</summary>
    private static HashSet<string>? ListedSharedFiles(string sharedZrdr, string chapterZrdr,
        string missionZrdr)
    {
        var listed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<string>();
        queue.Enqueue(SharedIndex);
        while (queue.Count > 0)
        {
            var stem = queue.Dequeue();
            if (!listed.Add(stem))
                continue;
            foreach (var path in ListedPaths(sharedZrdr, stem))
                if (IsSharedPath(path))
                    queue.Enqueue(StemOf(path));
        }
        if (listed.Count == 1)
            return null;
        foreach (var (zrdr, index) in new[] { (chapterZrdr, "cam_anim"), (missionZrdr, "mis_anim") })
            foreach (var path in ListedPaths(zrdr, index))
                if (IsSharedPath(path))
                    listed.Add(StemOf(path));
        return listed;
    }

    /// <summary>The chapter-scope reader files a mission sees, by stem: the chapter's own
    /// <c>cam_anim.zrd</c> listing plus any chapter files a mission's <c>mis_anim.zrd</c> adds
    /// directly (C2's <c>game_targets</c>/<c>police_*</c>/<c>security_destroy</c>, two at a time
    /// per mission). No index closure to walk, unlike the shared scope: empty means neither list
    /// names a chapter file, which gates the scope shut rather than leaving it ungated.</summary>
    private static HashSet<string> ListedChapterFiles(string chapterZrdr, string missionZrdr)
    {
        var listed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (zrdr, index) in new[] { (chapterZrdr, "cam_anim"), (missionZrdr, "mis_anim") })
            foreach (var path in ListedPaths(zrdr, index))
                if (!IsSharedPath(path))
                    listed.Add(StemOf(path));
        return listed;
    }

    private static IEnumerable<string> ListedPaths(string zrdr, string stem)
    {
        List<object?> root;
        try
        {
            root = Zrdr.LoadFileOrEmpty(zrdr, $"{stem}.zrd.json");
        }
        catch (Exception e) when (e is IOException or System.Text.Json.JsonException)
        {
            return Array.Empty<string>();
        }
        return MissionCutscenes.ListedPaths(root);
    }

    private static bool IsSharedPath(string path) =>
        path.Replace('/', '\\').Contains("\\common\\zrdr\\", StringComparison.OrdinalIgnoreCase);

    // A reader file's stem as the ANIMATION_DEFINITION_FILE lists name it: the leaf without
    // ".zrd"/".json", and without the "-N" suffix the extraction appends to a duplicate name
    // (planes\player.zrd is extracted as player-1.zrd.json beside an unrelated player.zrd.json).
    private static string StemOf(string pathOrFile)
    {
        var leaf = Path.GetFileName(pathOrFile.Replace('/', '\\'));
        while (leaf.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
            || leaf.EndsWith(".zrd", StringComparison.OrdinalIgnoreCase))
            leaf = Path.GetFileNameWithoutExtension(leaf);
        int dash = leaf.LastIndexOf('-');
        if (dash > 0 && dash < leaf.Length - 1 && leaf.AsSpan(dash + 1).ToString().All(char.IsDigit))
            leaf = leaf[..dash];
        return leaf;
    }

    // Definition identity: (anchor name, animation name), exactly how the compiled
    // extraction names its files, so a reader def and its compiled twin share a key.
    private static string KeyOf(AnimDefinition def) => $"{def.Name}\0{def.AnimName}";

    private void Add(AnimDefinition def, bool compiled)
    {
        var key = KeyOf(def);
        if (_seen.TryGetValue(key, out var kept))
        {
            // PERSIST_LOG lives only in the reader form, so a def whose compiled twin won this
            // key would lose the flag and cross no mission boundary. Only ever set, never clear:
            // Subset re-Adds the same objects, and the two sources are one authored definition.
            kept.PersistLog |= def.PersistLog;
            return;
        }

        _seen[key] = def;
        _defs.Add(def);
        if (compiled) CompiledCount++; else ReaderCount++;
        if (!string.IsNullOrEmpty(def.AnimName))
        {
            if (!_byAnimName.TryGetValue(def.AnimName!, out var list))
                _byAnimName[def.AnimName!] = list = new List<AnimDefinition>();
            list.Add(def);
        }
    }

    private IEnumerable<string> SoundNamesOfKind(string kind)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var def in Defs)
        {
            foreach (var name in NamesIn(def.Sequences, kind))
            {
                if (seen.Add(name))
                {
                    yield return name;
                }
            }
            if (def.ResetState is { } reset)
            {
                foreach (var name in NamesIn(new[] { reset }, kind))
                {
                    if (seen.Add(name))
                    {
                        yield return name;
                    }
                }
            }
            // The compiled destruction slot is dispatched at death (RunDeathSequence), same
            // reachability rule as the death/damage sequences above it.
            if (def.DeathSlot is { } slot)
            {
                foreach (var name in NamesIn(new[] { slot }, kind))
                {
                    if (seen.Add(name))
                    {
                        yield return name;
                    }
                }
            }
        }

        static IEnumerable<string> NamesIn(IEnumerable<AnimSequence> sequences, string kind)
        {
            foreach (var seq in sequences)
            {
                foreach (var ev in seq.Events)
                {
                    if (ev.Kind == kind && ev.Data.Str("name") is { } name)
                    {
                        yield return name;
                    }
                }
            }
        }
    }

    // startanims.json: [["NEW_GAME_START", [[name], [name], …], "LOAD_GAME_START", …]]
    private void LoadStartAnims(string missionZrdr)
    {
        try
        {
            var root = Zrdr.LoadFile(missionZrdr, "startanims.json");
            if (root.Count > 0 && root[0] is List<object?> outer)
                for (int i = 0; i + 1 < outer.Count; i++)
                    if (outer[i] is string k && k.Equals("NEW_GAME_START", StringComparison.OrdinalIgnoreCase)
                        && outer[i + 1] is List<object?> list)
                        foreach (var entry in list)
                            if (entry is List<object?> { Count: > 0 } e && e[0] is string name)
                                _startAnims.Add(name);
        }
        catch (Exception)
        {
            // startanims.json is optional (not every mission folder has one)
        }
    }
}
