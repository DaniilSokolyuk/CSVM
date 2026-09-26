using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;

namespace CSVM.Session.Campaign;

/// <summary>One owned plane's campaign fit: the ownership record itself, plus the per-gun
/// ammunition and per-pylon ordnance picks <c>docs/formats/saved-games.md</c> finds in the
/// original's 204-byte plane record. This is NOT a hangar build (paint, armour, hardpoint count):
/// those stay in <see cref="Flight.Hangar.CustomPlaneStore"/>'s global <c>user://Planes/</c>, named here
/// by <see cref="Name"/> only, so deleting a profile can never orphan or delete one.</summary>
public sealed class OwnedPlane
{
    public string Name { get; set; } = string.Empty;
    public int Airframe { get; set; }

    /// <summary>Per-gun-slot ammunition index, four slots. The original's no-gun marker (4) and
    /// the ammunition vocabulary (slug/dumdum/ap/magnesium) are <c>loadouts.md</c>'s; a slot with
    /// no gun mounted keeps the value at rest, since nothing here decides what guns a plane has.</summary>
    public int[] Ammo { get; set; } = new int[4];

    /// <summary>Per-pylon ordnance id, eight cells (four per wing). The id-to-ordnance mapping is
    /// not decoded (<c>saved-games.md</c>, "Where the ammunition and ordnance picks live"), so a
    /// value here is only ever round-tripped, never interpreted.</summary>
    public int[] Ordnance { get; set; } = new int[8];

    /// <summary>A campaign award (<c>docs/org/hangar.md</c>, "The mission reward table"): the
    /// original's class-2 plane record, which the sell handler refuses. Never set on a plane the
    /// player built.</summary>
    public bool Special { get; set; }
}

/// <summary>One run of a mission: the eight scalar fields described by <c>saved-games.md</c>, plus
/// the two per-airframe kill tallies
/// (<c>docs/org/debrief.md#what-the-tallies-count</c>).</summary>
public sealed class MissionRun
{
    /// <summary>Completed-objective bitmask; bit 0 is the primary objective.</summary>
    public int CompletedMask { get; set; }

    /// <summary>The objective mask of the attempt this run's airframe and plane name came from:
    /// the original's <c>+0x24</c> inside the merged half (<c>docs/formats/saved-games.md</c>,
    /// "The mission-result array"). Written on the merged half only. A later attempt must beat
    /// its bit count, not the OR-ed <see cref="CompletedMask"/>, to replace the plane.</summary>
    public int BestAttemptMask { get; set; }
    public int TimeMs { get; set; }
    public int Shots { get; set; }
    public int Hits { get; set; }
    public int Money { get; set; }
    public int Airframe { get; set; }
    public string PlaneName { get; set; } = string.Empty;

    /// <summary>Plain per-airframe kill counts, <see cref="CampaignProgression.AirframeCount"/>
    /// slots wide. A missing JSON property reads all zero because no per-airframe record exists to
    /// reconstruct.</summary>
    public int[] Kills { get; set; } = new int[CampaignProgression.AirframeCount];

    /// <summary>Ace per-airframe kill counts, same shape as <see cref="Kills"/>.</summary>
    public int[] AceKills { get; set; } = new int[CampaignProgression.AirframeCount];
}

/// <summary>One mission's record, the original's two halves (<c>saved-games.md</c>, "The
/// mission-result array"): the most recent attempt, and the best-of merge across attempts that
/// <see cref="CampaignProgression"/> maintains. A record whose <see cref="Best"/> mask is 0 is a
/// mission attempted but never completed, the shape the sample profile's last record has.</summary>
public sealed class MissionResult
{
    /// <summary>The <c>cm_sequence.zrd</c> flat index, 0..23, the campaign's own mission id.</summary>
    public int Seq { get; set; }

    /// <summary>The most recent attempt, written whether or not it completed anything.</summary>
    public MissionRun Latest { get; set; } = new();

    /// <summary>The merged best/cumulative result, updated only by an attempt that completed the
    /// primary objective.</summary>
    public MissionRun Best { get; set; } = new();

    /// <summary>Failed attempts at a mission that has never been completed: the original's own
    /// per-mission counter at <c>[0x0071b494 + idx*0x10]</c>, whose every fourth increment raises
    /// the skip offer (<c>docs/org/debrief.md</c>, "The four-attempt skip offer"). It belongs to
    /// neither half, since it spans attempts where the attempt half is cleared by each one. A
    /// missing JSON property reads 0.</summary>
    public int Attempts { get; set; }
}

/// <summary>One campaign profile's persisted state: wallet, owned planes with their campaign fit,
/// recorded mission results, how far through <c>cm_sequence.zrd</c> the player is, which aircraft
/// awards were granted, and the cross-mission destruction log. Fields and their defaults come from
/// <c>docs/formats/saved-games.md</c> (structure) and <c>docs/org/hangar.md</c> "The campaign
/// wallet" (starting funds and planes); see <see cref="NewProfile"/>. Everything here is written
/// through <see cref="CampaignProgression"/>, which owns the merge and advance rules.</summary>
public sealed class CampaignProfileDef
{
    public string Name { get; set; } = string.Empty;
    public int Funds { get; set; }
    public List<OwnedPlane> Planes { get; } = new();
    public int SelectedPlane { get; set; }

    /// <summary>The wingman's plane, an index into <see cref="Planes"/>: the save's
    /// <c>UIData +0x340</c>, which the flight check's wingman row resets from the way the pilot row
    /// reads <see cref="SelectedPlane"/> (<c>docs/formats/campaign-screens.md</c>).</summary>
    public int WingmanPlane { get; set; }

    /// <summary>The count of completed missions: both the campaign's position (the next
    /// mission is <c>cm_sequence</c> <c>seq == MissionsCompleted</c>) and the save's own field,
    /// <c>UIData +0x338</c>. Raised only by <see cref="CampaignProgression"/>'s advance rule.</summary>
    public int MissionsCompleted { get; set; }
    public List<MissionResult> MissionResults { get; } = new();

    /// <summary>The picture hanging in the cabin, a file name out of <see cref="CampaignMementos"/>'s
    /// award table (the save's <c>UIData +0x344</c>). Empty means the profile has chosen none and
    /// draws the seeded pin-up, which is exactly what a fresh profile holds.</summary>
    public string Memento { get; set; } = string.Empty;

    /// <summary>Airframe ids of the five campaign aircraft awards already granted. The original
    /// marks a per-airframe byte rather than a per-mission one, so a replay of the awarding
    /// mission grants nothing (<c>docs/org/hangar.md</c>, "The mission reward table").</summary>
    public List<int> GrantedAircraft { get; } = new();

    /// <summary>What earlier missions left destroyed, per chapter (`BL-243`). Carried into a later
    /// mission of the same chapter; see <see cref="CampaignPersistLog"/>.</summary>
    public CampaignPersistLog PersistLog { get; } = new();

    /// <summary>A fresh profile per the traced reset (<c>FUN_004113b0</c>, <c>docs/org/hangar.md</c>
    /// "The campaign wallet"): zero funds, two prebuilt Devastators (<c>langui</c> 511 "Gypsy
    /// Magic", 512 "The Knave"), nothing flown. The pilot flies Gypsy Magic and the wingman The
    /// Knave, the pair's own division of labour, so the two indices differ from the start.
    /// Ammo/ordnance picks start at the hangar's "untouched" values (index 0, the stock fit); the
    /// campaign's Ammo Selection screen is what changes them.</summary>
    public static CampaignProfileDef NewProfile(string name)
    {
        var def = new CampaignProfileDef { Name = name, Funds = 0, SelectedPlane = 0, WingmanPlane = 1 };
        def.Planes.Add(new OwnedPlane { Name = "Gypsy Magic", Airframe = 5 });
        def.Planes.Add(new OwnedPlane { Name = "The Knave", Airframe = 5 });
        return def;
    }
}

/// <summary>
/// JSON persistence for <see cref="CampaignProfileDef"/>, one directory per profile under
/// <c>user://Profiles/&lt;name&gt;/profile.json</c>. Like <see cref="Flight.Modes.ScoreStore"/>
/// and <see cref="Flight.Hangar.CustomPlaneStore"/> it is plain System.IO, so it unit-tests without
/// an engine. A missing or malformed file reads as nothing rather than throwing.
///
/// <para>A profile name is user text entry and becomes a directory name, so it is sanitised the
/// same way <see cref="Flight.Hangar.CustomPlaneStore"/> sanitises a plane name. Deleting a profile
/// removes only its own directory. Hangar planes stay in the global <c>user://Planes/</c> store,
/// referenced here by name only (see <see cref="OwnedPlane"/>), so a deletion can never orphan or
/// delete one.</para>
/// </summary>
public sealed class CampaignProfileStore
{
    /// <summary>The schema version written into every file. A file claiming a version this reader
    /// does not know is treated as malformed rather than half-read. ⚠ Version 3 exists because
    /// version 2's <c>completedMask</c> was indexed by display row rather than by <c>IDENTITY</c>
    /// priority: those bits are not stale data a reader can ignore, they are wrong input to the
    /// reward table's pay-once gate, so such a file must not load at all.</summary>
    public const int Version = 3;

    private const string FileName = "profile.json";

    // The player last seated, kept beside the profile directories rather than inside one, because
    // it is a statement about the store and not about any profile. The original keeps the same
    // thing outside its saves; the decode is in docs/formats/saved-games.md.
    private const string LastPlayedFile = "last-played.json";

    private static readonly JsonWriterOptions WriterOptions = new() { Indented = true };

    private readonly string _dir;

    /// <summary>A store over <paramref name="directory"/>, which must be absolute (a relative path
    /// would resolve against whatever the process's working directory happens to be).</summary>
    public CampaignProfileStore(string directory)
    {
        if (!Path.IsPathRooted(directory))
        {
            throw new ArgumentException($"campaign profile store needs an absolute directory, got '{directory}'");
        }

        _dir = directory;
    }

    /// <summary>The name of the profile last seated, or "" when the store has never recorded one.
    /// The name, never a row or a timestamp: the roster is alphabetical and every profile file is
    /// touched by a save. A recorded name whose profile has since been deleted still reads back,
    /// so a caller resolves it against the roster rather than trusting it.</summary>
    public string LastPlayed
    {
        get
        {
            try
            {
                var path = Path.Combine(_dir, LastPlayedFile);
                if (!File.Exists(path))
                {
                    return string.Empty;
                }

                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                return doc.RootElement.ValueKind == JsonValueKind.Object
                    ? ReadString(doc.RootElement, "name")
                    : string.Empty;
            }
            catch (JsonException)
            {
                return string.Empty;
            }
            catch (IOException)
            {
                return string.Empty;
            }
            catch (UnauthorizedAccessException)
            {
                return string.Empty;
            }
        }
    }

    /// <summary>The last used profile's own name, or null when the store records none and when the
    /// name it records no longer loads. A sortie outside a campaign flies under this name, which is
    /// where the original's <c>PlayerName</c> comes from too (<c>docs/formats/saved-games.md</c>,
    /// "Which player is current, across runs"). Resolved through the profile rather than off the
    /// record, so a deleted profile's name never reaches a session.</summary>
    public string? LastPlayedPilotName =>
        Load(LastPlayed) is { Name.Length: > 0 } profile ? profile.Name : null;

    /// <summary>The production store, <c>user://Profiles/</c> resolved to its OS path.</summary>
    public static CampaignProfileStore UserProfiles() =>
        new(Path.Combine(Godot.ProjectSettings.GlobalizePath("user://"), "Profiles"));

    /// <summary>The canonical JSON text for <paramref name="def"/>.</summary>
    public static string Serialize(CampaignProfileDef def)
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream, WriterOptions))
        {
            w.WriteStartObject();
            w.WriteNumber("version", Version);
            w.WriteString("name", def.Name);
            w.WriteNumber("funds", def.Funds);
            w.WriteNumber("selectedPlane", def.SelectedPlane);
            w.WriteNumber("wingmanPlane", def.WingmanPlane);
            w.WriteNumber("missionsCompleted", def.MissionsCompleted);
            w.WriteString("memento", def.Memento);
            w.WriteStartArray("planes");
            foreach (var plane in def.Planes)
            {
                w.WriteStartObject();
                w.WriteString("name", plane.Name);
                w.WriteNumber("airframe", plane.Airframe);
                WriteInts(w, "ammo", plane.Ammo);
                WriteInts(w, "ordnance", plane.Ordnance);
                w.WriteBoolean("special", plane.Special);
                w.WriteEndObject();
            }

            w.WriteEndArray();
            WriteInts(w, "grantedAircraft", def.GrantedAircraft);
            w.WriteStartArray("missionResults");
            foreach (var result in def.MissionResults)
            {
                w.WriteStartObject();
                w.WriteNumber("seq", result.Seq);
                w.WriteNumber("attempts", result.Attempts);
                WriteRun(w, "latest", result.Latest);
                WriteRun(w, "best", result.Best);
                w.WriteEndObject();
            }

            w.WriteEndArray();
            w.WriteStartArray("persistLog");
            foreach (int chapter in def.PersistLog.Chapters)
            {
                w.WriteStartObject();
                w.WriteNumber("chapter", chapter);
                w.WriteStartArray("objects");
                foreach (var state in def.PersistLog.For(chapter))
                {
                    w.WriteStartObject();
                    w.WriteNumber("node", state.Node);
                    w.WriteString("def", state.Def);
                    w.WriteString("nodeName", state.NodeName);
                    w.WriteBoolean("destroyed", state.Destroyed);
                    w.WriteNumber("health", state.Health);
                    w.WriteNumber("seq", state.Seq);
                    w.WriteEndObject();
                }

                w.WriteEndArray();
                w.WriteEndObject();
            }

            w.WriteEndArray();
            w.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>The def a JSON text describes, or null when the text is not valid JSON, not an
    /// object, or not <see cref="Version"/>. A field the file omits keeps the model's default
    /// rather than failing the whole profile.</summary>
    public static CampaignProfileDef? Deserialize(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            int version = ReadInt(root, "version", -1);
            if (root.ValueKind != JsonValueKind.Object || version != Version)
            {
                return null;
            }

            var def = new CampaignProfileDef
            {
                Name = root.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String
                    ? name.GetString() ?? string.Empty
                    : string.Empty,
                Funds = ReadInt(root, "funds", 0),
                SelectedPlane = ReadInt(root, "selectedPlane", 0),
                WingmanPlane = ReadInt(root, "wingmanPlane", 0),
                MissionsCompleted = ReadInt(root, "missionsCompleted", 0),
                Memento = ReadString(root, "memento"),
            };

            if (root.TryGetProperty("planes", out var planes) && planes.ValueKind == JsonValueKind.Array)
            {
                foreach (var p in planes.EnumerateArray())
                {
                    if (p.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    var plane = new OwnedPlane
                    {
                        Name = p.TryGetProperty("name", out var pn) && pn.ValueKind == JsonValueKind.String
                            ? pn.GetString() ?? string.Empty
                            : string.Empty,
                        Airframe = ReadInt(p, "airframe", 0),
                    };
                    ReadInts(p, "ammo", plane.Ammo);
                    ReadInts(p, "ordnance", plane.Ordnance);
                    plane.Special = p.TryGetProperty("special", out var sp)
                        && sp.ValueKind == JsonValueKind.True;
                    def.Planes.Add(plane);
                }
            }

            if (root.TryGetProperty("grantedAircraft", out var granted)
                && granted.ValueKind == JsonValueKind.Array)
            {
                foreach (var g in granted.EnumerateArray())
                {
                    if (g.ValueKind == JsonValueKind.Number && g.TryGetInt32(out int airframe))
                    {
                        def.GrantedAircraft.Add(airframe);
                    }
                }
            }

            if (root.TryGetProperty("missionResults", out var results) && results.ValueKind == JsonValueKind.Array)
            {
                foreach (var r in results.EnumerateArray())
                {
                    if (r.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    def.MissionResults.Add(new MissionResult
                    {
                        Seq = ReadInt(r, "seq", 0),
                        Attempts = ReadInt(r, "attempts", 0),
                        Latest = ReadRun(r, "latest"),
                        Best = ReadRun(r, "best"),
                    });
                }
            }

            ReadPersistLog(root, def);
            return def;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Records which profile is now seated, the store's answer to "who was playing".
    /// An empty name clears the record. A write that fails is dropped: a convenience about where
    /// the cursor opens must not be able to refuse a campaign.</summary>
    public void RecordLastPlayed(string name)
    {
        try
        {
            Directory.CreateDirectory(_dir);
            var path = Path.Combine(_dir, LastPlayedFile);
            if (string.IsNullOrWhiteSpace(name))
            {
                File.Delete(path);
                return;
            }

            using var stream = new MemoryStream();
            using (var w = new Utf8JsonWriter(stream, WriterOptions))
            {
                w.WriteStartObject();
                w.WriteNumber("version", Version);
                w.WriteString("name", name);
                w.WriteEndObject();
            }

            File.WriteAllText(path, Encoding.UTF8.GetString(stream.ToArray()), new UTF8Encoding(false));
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Every stored profile's name, sorted. A missing directory is an empty list (a first
    /// run has created nothing); a profile subdirectory with no readable <c>profile.json</c> is
    /// skipped, never a crash.</summary>
    public IReadOnlyList<string> List()
    {
        var names = new List<string>();
        if (Directory.Exists(_dir))
        {
            foreach (var sub in Directory.GetDirectories(_dir))
            {
                if (TryRead(Path.Combine(sub, FileName)) is { } def)
                {
                    names.Add(def.Name);
                }
            }
        }

        names.Sort(StringComparer.OrdinalIgnoreCase);
        return names;
    }

    /// <summary>The stored profile of that name, or null when its file is absent or malformed, or
    /// the name itself cannot be a profile directory.</summary>
    public CampaignProfileDef? Load(string name)
    {
        try
        {
            return TryRead(Path.Combine(DirFor(name), FileName));
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <summary>Why <paramref name="name"/> does not load, or "" when it does. <see cref="Load"/>
    /// answers the same null to a profile that was never created and to one this build refuses to
    /// read, and a caller reporting the first for the second sends its reader hunting for a
    /// directory that is right there. The refusal is deliberate, not a fault: see
    /// <see cref="Version"/>.</summary>
    public string LoadProblem(string name)
    {
        string path;
        try
        {
            path = Path.Combine(DirFor(name), FileName);
        }
        catch (ArgumentException)
        {
            return $"'{name}' cannot be a profile directory name";
        }

        if (!File.Exists(path))
        {
            return $"no such profile ({path} does not exist)";
        }

        if (Load(name) != null)
        {
            return string.Empty;
        }

        int stored = StoredVersion(path);
        return stored < 0
            ? $"{path} is not readable as a profile"
            : string.Create(
                CultureInfo.InvariantCulture,
                $"{path} is schema version {stored}, and this build reads version {Version}");
    }

    /// <summary>Writes <paramref name="def"/> to its name's directory, creating it on first save
    /// and overwriting any existing profile of the same name (the name IS the identity, same as
    /// <see cref="Flight.Hangar.CustomPlaneStore"/>). Returns the file's absolute path.</summary>
    public string Save(CampaignProfileDef def)
    {
        if (string.IsNullOrWhiteSpace(def.Name))
        {
            throw new ArgumentException("a campaign profile cannot be saved without a name");
        }

        var dir = DirFor(def.Name);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, FileName);
        File.WriteAllText(path, Serialize(def), new UTF8Encoding(false));
        return path;
    }

    /// <summary>Removes the stored profile's whole directory, returning whether one existed. Only
    /// this profile's own directory is touched, never <c>user://Planes/</c>, per the ownership
    /// rule above. A name with no directory is a no-op rather than an error.</summary>
    public bool Delete(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        try
        {
            var dir = DirFor(name);
            if (!Directory.Exists(dir))
            {
                return false;
            }

            Directory.Delete(dir, recursive: true);
            if (string.Equals(LastPlayed, name, StringComparison.OrdinalIgnoreCase))
            {
                RecordLastPlayed(string.Empty);
            }

            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>The directory this name persists to. Characters a directory name cannot carry
    /// become '_', same treatment <see cref="Flight.Hangar.CustomPlaneStore.PathFor"/> gives a plane
    /// name. A name that sanitises to nothing but dots is rejected: "." is this store's own root
    /// and ".." its parent, so accepting either would let Save or Delete act on the whole store
    /// (or on all of <c>user://</c>) instead of on one profile.</summary>
    public string DirFor(string name)
    {
        var safe = name.Trim();
        foreach (var c in Path.GetInvalidFileNameChars())
        {
            safe = safe.Replace(c, '_');
        }

        if (safe.Trim('.', ' ').Length == 0)
        {
            throw new ArgumentException($"'{name}' does not sanitise to a usable profile directory name");
        }

        return Path.Combine(_dir, safe);
    }

    // The version claimed by the file at that path, or -1 when nothing readable claims one. Read
    // straight off the JSON rather than through Deserialize, which rejects the whole file on a
    // version it does not know and so can never report which version that was.
    private static int StoredVersion(string path)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            return doc.RootElement.ValueKind == JsonValueKind.Object
                ? ReadInt(doc.RootElement, "version", -1)
                : -1;
        }
        catch (JsonException)
        {
            return -1;
        }
        catch (IOException)
        {
            return -1;
        }
        catch (UnauthorizedAccessException)
        {
            return -1;
        }
    }

    private static CampaignProfileDef? TryRead(string path)
    {
        try
        {
            return File.Exists(path) ? Deserialize(File.ReadAllText(path)) : null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void WriteRun(Utf8JsonWriter w, string key, MissionRun run)
    {
        w.WriteStartObject(key);
        w.WriteNumber("completedMask", run.CompletedMask);
        w.WriteNumber("bestAttemptMask", run.BestAttemptMask);
        w.WriteNumber("timeMs", run.TimeMs);
        w.WriteNumber("shots", run.Shots);
        w.WriteNumber("hits", run.Hits);
        w.WriteNumber("money", run.Money);
        w.WriteNumber("airframe", run.Airframe);
        w.WriteString("planeName", run.PlaneName);
        WriteInts(w, "kills", run.Kills);
        WriteInts(w, "aceKills", run.AceKills);
        w.WriteEndObject();
    }

    private static MissionRun ReadRun(JsonElement result, string key)
    {
        if (!result.TryGetProperty(key, out var r) || r.ValueKind != JsonValueKind.Object)
        {
            return new MissionRun();
        }

        var run = new MissionRun
        {
            CompletedMask = ReadInt(r, "completedMask", 0),
            BestAttemptMask = ReadInt(r, "bestAttemptMask", 0),
            TimeMs = ReadInt(r, "timeMs", 0),
            Shots = ReadInt(r, "shots", 0),
            Hits = ReadInt(r, "hits", 0),
            Money = ReadInt(r, "money", 0),
            Airframe = ReadInt(r, "airframe", 0),
            PlaneName = r.TryGetProperty("planeName", out var n) && n.ValueKind == JsonValueKind.String
                ? n.GetString() ?? string.Empty
                : string.Empty,
        };
        ReadInts(r, "kills", run.Kills);
        ReadInts(r, "aceKills", run.AceKills);
        return run;
    }

    private static void ReadPersistLog(JsonElement root, CampaignProfileDef def)
    {
        if (!root.TryGetProperty("persistLog", out var log) || log.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var entry in log.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object
                || !entry.TryGetProperty("objects", out var objects)
                || objects.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            var states = new List<PersistedObject>();
            foreach (var o in objects.EnumerateArray())
            {
                if (o.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                states.Add(new PersistedObject(
                    ReadInt(o, "node", -1),
                    ReadString(o, "def"),
                    ReadString(o, "nodeName"),
                    o.TryGetProperty("destroyed", out var d) && d.ValueKind == JsonValueKind.True,
                    o.TryGetProperty("health", out var h) && h.ValueKind == JsonValueKind.Number
                        ? h.GetSingle()
                        : 0f,
                    // -1 for a log written before the capturing position was stored: it belongs to
                    // whichever earlier mission of the chapter wrote it (CampaignPersistLog.Through).
                    ReadInt(o, "seq", -1)));
            }

            int chapter = ReadInt(entry, "chapter", 0);
            foreach (var state in states)
            {
                def.PersistLog.Merge(chapter, state.Seq, new[] { state });
            }
        }
    }

    private static string ReadString(JsonElement obj, string key) =>
        obj.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? string.Empty
            : string.Empty;

    private static void WriteInts(Utf8JsonWriter w, string key, IReadOnlyList<int> values)
    {
        w.WriteStartArray(key);
        foreach (int value in values)
        {
            w.WriteNumberValue(value);
        }

        w.WriteEndArray();
    }

    private static void ReadInts(JsonElement obj, string key, int[] into)
    {
        if (!obj.TryGetProperty(key, out var arr) || arr.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        int i = 0;
        foreach (var value in arr.EnumerateArray())
        {
            if (i >= into.Length)
            {
                break;
            }

            into[i++] = value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int v) ? v : 0;
        }
    }

    private static int ReadInt(JsonElement obj, string key, int fallback) =>
        obj.TryGetProperty(key, out var v)
        && v.ValueKind == JsonValueKind.Number
        && v.TryGetInt32(out int value)
            ? value
            : fallback;
}
