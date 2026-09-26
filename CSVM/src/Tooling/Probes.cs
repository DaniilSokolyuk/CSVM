using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using CSVM.Flight.Ai;
using CSVM.Flight.Airframe;
using CSVM.Flight.Weapons;
using CSVM.Mech3;
using CSVM.Utils;
using Godot;

namespace CSVM.Tooling;

/// <summary>
/// The assertion cores behind the <c>--dump-*</c> / <c>--damage-test</c> inspection reports.
///
/// <para>One source of truth: each probe does the work once and returns <b>both</b> halves, the
/// human-readable report text the <c>--dump-*</c> flag prints and writes, and a structured verdict
/// (counts, per-row booleans, failure strings) a <c>--run-tests</c> suite asserts on. A check
/// belongs here; never re-implement one directly in a suite.</para>
///
/// <para>Every probe renders numbers with <see cref="CultureInfo.InvariantCulture"/>: a German
/// machine otherwise writes <c>HEALTH 0,01</c> into a committed verification artifact.</para>
/// </summary>
public static class Probes
{
    /// <summary>How many defs one sweep reports on. A wildcard NAME binds many identical towers,
    /// so one representative per def is enough; the cap keeps a destructible-heavy chapter a
    /// readable report. <b>It caps the swept rows, never the registry totals</b>, a census reads
    /// <see cref="DamageResult.TotalInstances"/>.</summary>
    public const int SweepCap = 16;

    /// <summary>The tuner rig's own iteration cap, 100 s at the step it forces. A run that reaches
    /// it was still gaining speed and reports a lower bound rather than an equilibrium.</summary>
    public const int TunerEnvelopeSteps = 10000;

    private const float Mph = 0.44704f;         // m/s per mph
    private const float Ft = 0.3048f;           // m per foot
    private const float BandEdgeM = 2000f;      // the atmosphere band edge, the plant's ceiling
    private const float EnvDt = 1f / 60f;       // the sim step --det pins every session to
    private const float TunerDt = 0.01f;        // the step the shipped Dynamics tuner forces

    // The Bloodhawk's two decoded envelope targets. Kept as constants so a row states the number it
    // is judged against instead of computing it from the plant it is judging; FlightEnvelopeTests
    // solves both independently from the decoded constants and fails when either drifts.
    // ⚠ Neither is a footage figure, and neither may be replaced by one.
    private const double LevelTopSpeedMph = 300.5;
    private const double TerminalDiveMph = 356.0;

    // The eleven stock player airframes, in the order every whole-envelope dump prints them. Fixed
    // so two dumps taken at different times diff line for line; the launchscreen's own curated
    // order is PlanePickerRoster's, a different list for a different job.
    private static readonly string[] StockAirframeNodes =
    {
        "player_bhawk", "player_pfighter", "player_fury", "player_warhawk", "player_autogyro",
        "player_avenger", "player_balmoral", "player_brigand", "player_fbrand", "player_kestrel",
        "player_peacemaker",
    };

    // The eight chapter codes an AI dump walks, every one that ships its own
    // `&lt;Cx&gt;/zrdr/` patrol-net scope and mission dirs.
    private static readonly string[] AiChapters = { "C1", "C1B", "C1C", "C2", "C2B", "C3", "C4", "C5" };

    // Chapter-scope directory names that are NOT mission dirs, so a plain
    // Directory.EnumerateDirectories(string) over a chapter folder can tell a
    // mission (`IA1`/`M0x`/`MP1-3`) from the chapter's own gamez/texture/zrdr/anim
    // scopes without a fixed mission-name table.
    private static readonly HashSet<string> AiChapterScopeDirs =
        new(StringComparer.OrdinalIgnoreCase) { "gamez", "texture", "cam_anim", "zrdr" };

    // One mean texel per sheet: a chapter's destructible population repeats its materials. A
    // decode per row would read the same archive entry dozens of times.
    private static readonly Dictionary<string, Vector3?> MeanTexelCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The eleven stock player airframes in whole-envelope dump order, the set the parity
    /// ledger classifies and <c>--dump-flight=all</c> flies.</summary>
    public static IReadOnlyList<string> StockAirframes => StockAirframeNodes;

    // ---- markers -----------------------------------------------------------------------------

    public static MarkersResult Markers(string planesGamezPath, string filter)
    {
        var r = new MarkersResult();
        GameZ gamez;
        try
        {
            gamez = GameZ.Load(planesGamezPath);
        }
        catch (Exception e)
        {
            r.Error = $"could not load planes gamez ({planesGamezPath}): {e.Message}";
            r.Summary = $"markers dump: {r.Error}";
            return r;
        }

        var wanted = new List<(string Model, string Display)>();
        foreach (var plane in MarkerRig.PlayerAirframes)
        {
            if (filter.Length == 0
                || plane.Model.Contains(filter, StringComparison.OrdinalIgnoreCase)
                || plane.Display.Contains(filter, StringComparison.OrdinalIgnoreCase))
            {
                wanted.Add(plane);
            }
        }
        if (wanted.Count == 0)
        {
            var names = new List<string>();
            foreach (var p in MarkerRig.PlayerAirframes)
            {
                names.Add($"{p.Display} ({p.Model})");
            }
            r.Error = $"'{filter}' matched no airframe. Available: " + string.Join(", ", names);
            r.Summary = $"markers dump: {r.Error}";
            return r;
        }
        r.Requested = wanted.Count;

        var sb = new StringBuilder();
        sb.AppendLine($"# Aircraft marker rig: {planesGamezPath}");
        sb.AppendLine("# Positions are plane frame, metres (nose -Z, right +X, up +Y). See docs/formats/markers.md.");
        sb.AppendLine();
        foreach (var (model, display) in wanted)
        {
            var rig = MarkerRig.Extract(gamez, model);
            if (rig == null)
            {
                // Recorded, not swallowed: a silently skipped airframe would reduce the final
                // count with nothing naming it.
                r.Missing.Add(model);
                sb.AppendLine($"=== {display} ({model}): root node not found ===").AppendLine();
                continue;
            }
            sb.Append(rig.Format(display)).AppendLine();
            r.Done++;
        }
        r.Text = sb.ToString();
        r.Summary = r.Missing.Count == 0
            ? $"markers dump: {r.Done}/{r.Requested} airframe(s)"
            : $"markers dump: {r.Done}/{r.Requested} airframe(s), MISSING {string.Join(", ", r.Missing)}";
        return r;
    }

    // ---- weapons -----------------------------------------------------------------------------

    public static WeaponsResult Weapons(string zrdrPath, string messagesPath, string filter)
    {
        // A committed verification artifact must read the same everywhere: a German machine
        // otherwise writes "6,25" where an invariant one writes "6.25".
        System.Threading.Thread.CurrentThread.CurrentCulture = CultureInfo.InvariantCulture;
        var r = new WeaponsResult();
        WeaponDefs weapons;
        try
        {
            var messages = Messages.Load(messagesPath);
            weapons = WeaponDefs.Load(zrdrPath, messages);
        }
        catch (Exception e)
        {
            r.Error = $"could not load weapons.json ({zrdrPath}): {e.Message}";
            r.Summary = $"weapons dump: {r.Error}";
            return r;
        }
        r.Total = weapons.All.Count;
        r.EmptyClipSound = weapons.EmptyClipSound;

        var sb = new StringBuilder();
        sb.AppendLine($"# weapons.json: {zrdrPath}");
        sb.AppendLine($"# {weapons.All.Count} BALLISTICS entries; empty-clip sound = {weapons.EmptyClipSound}");
        sb.AppendLine("# See docs/formats/weapons.md.");
        sb.AppendLine();

        foreach (var w in weapons.All)
        {
            if (filter.Length > 0
                && !w.Id.Contains(filter, StringComparison.OrdinalIgnoreCase)
                && !w.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            r.Shown++;
            var flags = new List<string>();
            if (w.IsCannon) { flags.Add("CANNON"); }
            if (w.IsRocket) { flags.Add("ROCKET"); }
            if (w.HighExplosive) { flags.Add("HE"); }
            if (w.Sonic) { flags.Add("SONIC"); }
            if (w.Flash) { flags.Add("FLASH"); }
            if (w.BeeperSeeker) { flags.Add("BEEPER_SEEKER"); }
            if (w.Rear) { flags.Add("REAR"); }
            if (w.Torpedo) { flags.Add("TORPEDO"); }
            if (w.Targetable) { flags.Add("TARGETABLE"); }
            if (w.DamagesZeppelin) { flags.Add("DMG_ZEP"); }
            if (w.ShakesCamera) { flags.Add("SHAKE"); }
            if (w.Crater) { flags.Add("CRATER"); }
            sb.Append($"{w.Id}  {w.Name,-8}  \"{w.DisplayName}\"");
            sb.Append($"\n    cal={Opt(w.Caliber)} rate={w.FireRate} vel={Opt(w.Velocity)} range={Opt(w.Range)}"
                      + $" acc={Opt(w.Acceleration)} turn={Opt(w.TurnRate)} spread={Opt(w.CannonSpread)}");
            sb.Append($"\n    dmg armor={Opt(w.ArmorDamage)} health={Opt(w.HealthDamage)} combined={Opt(w.Damage)}"
                      + $" | cluster={Opt(w.ClusterSize)} ammo_limit={Opt(w.AmmoLimit)}");
            sb.Append($"\n    lock={Opt(w.LockOn)} det_dist={Opt(w.DetonationDistance)} proximity={Opt(w.ImpactProximity)}"
                      + $" priority={Opt(w.Priority)}");
            sb.Append($"\n    flags: [{string.Join(", ", flags)}]");
            sb.Append($"\n    fire={FmtEffect(w.Fire)} flyout={FmtFlyout(w.Flyout)} looped={w.LoopedSoundName ?? "-"}");
            sb.Append("\n    impact:");
            int impactRows = 0;
            for (int id = 0; id < w.Impact.Length; id++)
            {
                if (w.Impact[id] is not { } row)
                {
                    continue;
                }
                impactRows++;
                sb.Append($" {id}/{SurfaceRegistry.NameForId(id)}={FmtEffect(row)}");
            }
            if (impactRows == 0)
            {
                sb.Append(" (none)");
            }
            if (w.UnhandledKeys.Count > 0)
            {
                r.UnhandledTotal += w.UnhandledKeys.Count;
                sb.Append($"\n    !! UNHANDLED KEYS: {string.Join(", ", w.UnhandledKeys)}");
            }
            sb.AppendLine();
            sb.AppendLine();
        }
        r.Text = sb.ToString();
        r.Summary = r.UnhandledTotal == 0
            ? $"weapons dump: {r.Shown} entr(y/ies) of {r.Total}, NO unhandled keys"
            : $"weapons dump: {r.Shown} entr(y/ies) of {r.Total}, {r.UnhandledTotal} UNHANDLED key(s), see the !! lines above";
        return r;
    }

    // ---- loadouts ----------------------------------------------------------------------------

    public static LoadoutResult Loadouts(string zrdrPath, string messagesPath, string planesGamezPath,
        string dataRoot, string filter, string? loadoutOverride, bool forRig = false)
    {
        System.Threading.Thread.CurrentThread.CurrentCulture = CultureInfo.InvariantCulture;
        var r = new LoadoutResult();
        StockLoadouts stock;
        WeaponDefs weapons;
        GameZ planesGamez;
        TextureArchive textures;
        try
        {
            stock = StockLoadouts.Load();
            weapons = WeaponDefs.Load(zrdrPath, Messages.Load(messagesPath));
            planesGamez = GameZ.Load(planesGamezPath);
            // Any texture archive resolves the (meshless) markers; the C1 set is the viewer default.
            textures = new TextureArchive(SessionPaths.ChapterTextures(dataRoot, "C1"));
        }
        catch (Exception e)
        {
            r.Error = $"could not load inputs: {e.Message}";
            r.Summary = $"loadout dump: {r.Error}";
            return r;
        }

        var sb = new StringBuilder();
        sb.AppendLine(forRig
            ? "# Full-rig lab loadouts (Loadout.ForRig): every firepoint/pylon, seeded from stock_loadouts.json"
            : "# Stock loadouts bound to models: CSVM/data/stock_loadouts.json");
        if (loadoutOverride != null)
        {
            sb.AppendLine($"# --loadout override: binding every plane to '{loadoutOverride}'");
        }
        sb.AppendLine();

        using (textures)
        {
            foreach (var def in stock.All.Values)
            {
                if (filter.Length > 0
                    && !def.Def.Contains(filter, StringComparison.OrdinalIgnoreCase)
                    && !def.Model.Contains(filter, StringComparison.OrdinalIgnoreCase)
                    && !def.Display.Contains(filter, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                var bindDef = loadoutOverride != null ? stock.For(loadoutOverride) : def;
                sb.Append($"=== {def.Display} ({def.Def} / {def.Model}) ===");
                if (bindDef == null)
                {
                    string why = $"--loadout='{loadoutOverride}' is not a known loadout def";
                    sb.AppendLine($"\n  !! {why}");
                    sb.AppendLine();
                    r.Failures.Add($"{def.Def}: {why}");
                    r.Failed++;
                    continue;
                }
                Node3D? plane = null;
                try
                {
                    plane = new PlaneBuilder(planesGamez, textures).Build(def.Model);
                    var loadout = forRig ? Loadout.ForRig(plane, weapons, bindDef) : Loadout.Bind(bindDef, plane, weapons);
                    sb.Append("\n  guns:");
                    foreach (var g in loadout.Guns)
                    {
                        var names = new List<string>();
                        foreach (var m in g.Muzzles)
                        {
                            names.Add(m.HasMeta(AnimRuntime.NameMeta) ? m.GetMeta(AnimRuntime.NameMeta).AsString() : m.Name);
                        }
                        sb.Append($"\n    slot{g.Slot} {g.Mount,-22} {g.Weapon.Id} ({g.Weapon.Name})"
                                  + $" ammo {g.Ammo}{(g.IsTurret ? "  [TURRET, inert]" : "")}"
                                  + $"  muzzles: {string.Join(", ", names)}");
                    }
                    if (loadout.Hardpoints.Count > 0)
                    {
                        var hp = loadout.Hardpoints[0];
                        int total = 0;
                        var pylonNums = new List<string>();
                        foreach (var h in loadout.Hardpoints)
                        {
                            total += h.Capacity;
                            pylonNums.Add(h.Index.ToString());
                        }
                        sb.Append($"\n  hardpoints: {loadout.Hardpoints.Count} x {hp.Weapon.Id} ({hp.Weapon.Name}),"
                                  + $" {hp.Capacity} per pylon = {total} total  (pylon{string.Join(",", pylonNums)})");
                    }
                    else
                    {
                        sb.Append("\n  hardpoints: none");
                    }
                    sb.AppendLine();
                    r.Bound++;
                }
                catch (Exception e)
                {
                    sb.AppendLine($"\n  !! {e.Message}");
                    r.Failures.Add($"{def.Def}: {e.Message}");
                    r.Failed++;
                }
                finally
                {
                    plane?.Free();
                }
                sb.AppendLine();
            }
        }
        r.Text = sb.ToString();
        r.Summary = r.Failed == 0
            ? $"loadout dump: {r.Bound} plane(s) bound, every marker resolved"
            : $"loadout dump: {r.Bound} ok, {r.Failed} FAILED, see the !! lines above";
        return r;
    }

    // ---- mip chains ----------------------------------------------------------------------------

    /// <summary>What a chapter's texture archive installed as levels 1 and 2, beside the authored
    /// siblings, mean luminance and the share of pixels above 128 per level. Built through
    /// <see cref="TextureArchive.BuildMipped"/> under whatever <c>--mips=</c> policy is set, so
    /// <c>installed == authored</c> checks the policy took effect, not that files merely exist.</summary>
    public static MipResult MipChains(string texturesPath, string chapter, string filter,
        string interpPath = "")
    {
        System.Threading.Thread.CurrentThread.CurrentCulture = CultureInfo.InvariantCulture;
        var r = new MipResult();
        TextureArchive textures;
        try
        {
            textures = new TextureArchive(texturesPath);
        }
        catch (Exception e)
        {
            r.Error = $"could not open the texture archive ({texturesPath}): {e.Message}";
            r.Summary = $"mips dump: {r.Error}";
            return r;
        }

        var sb = new StringBuilder();
        sb.AppendLine($"# Texture mip chains: {chapter}, {texturesPath}");
        sb.AppendLine($"# policy: --mips={TextureArchive.Mips.ToString().ToLowerInvariant()}");
        float bias = TextureArchive.MipBias(interpPath, chapter);
        sb.AppendLine($"# mip LOD bias: adjust.gw MipBias={bias:0.##}; level N is first sampled at "
                      + $"{Math.Pow(2.0, -bias):0.###}x the unbiased texel density, "
                      + $"L1 at {Math.Pow(2.0, 1.0 - bias):0.###} texels per pixel.");
        sb.AppendLine($"# {textures.AuthoredMipBases.Count} base texture(s) ship an authored level; "
                      + $"{textures.AuthoredMipsAvailable} level(s) in all.");
        sb.AppendLine("# 'px>128' is the share of pixels above luminance 128, what the artists kept "
                      + "and a box filter averages away.");
        sb.AppendLine();

        using (textures)
        {
            foreach (var name in textures.AuthoredMipBases)
            {
                if (filter.Length > 0 && !name.Contains(filter, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                var img = textures.BuildMipped(name, out int authored);
                if (img == null)
                {
                    r.Failures.Add($"{name}: the archive holds it but it would not build");
                    sb.AppendLine($"{name}: FAILED TO BUILD").AppendLine();
                    continue;
                }
                r.Textures++;
                r.LevelsInstalled += authored;
                sb.AppendLine($"=== {name} {img.GetWidth()}x{img.GetHeight()}, "
                              + $"{img.GetMipmapCount()} mip level(s), {authored} authored");
                sb.AppendLine($"  L0            {Luma(MipLevel(img, 0))}");
                for (int level = 1; level <= 2 && level <= img.GetMipmapCount(); level++)
                {
                    var sibling = textures.FindImage($"{name}_{level}");
                    if (sibling == null)
                    {
                        sb.AppendLine($"  L{level}            {Luma(MipLevel(img, level))}   (no authored level)");
                        continue;
                    }
                    r.LevelsShipped++;
                    var installed = MipLevel(img, level);
                    bool same = SameLuma(installed, sibling);
                    if (same)
                    {
                        r.LevelsMatching++;
                    }
                    else
                    {
                        r.Mismatched.Add($"{name}_{level}");
                    }
                    sb.AppendLine($"  L{level} installed  {Luma(installed)}   {(same ? "== authored" : "!= AUTHORED")}");
                    sb.AppendLine($"  L{level} authored   {Luma(sibling)}");
                }
                sb.AppendLine();
            }
        }
        r.Text = sb.ToString();
        r.Summary = r.Failures.Count > 0
            ? $"mips dump: {r.Textures} texture(s), {r.Failures.Count} FAILED to build"
            : $"mips dump: {r.Textures} texture(s), {r.LevelsMatching}/{r.LevelsShipped} level(s) "
              + $"match the authored artwork ({r.LevelsInstalled} installed as authored)";
        return r;
    }

    // ---- AI data -------------------------------------------------------------------------------

    /// <summary>--dump-ai[=chapter] (docs/cli.md): a pure-data report over the five AI families,
    /// patrol nets, <c>aiv</c> rosters, <c>ai.zrd</c> turrets, zeppelins, generators. No world, no
    /// scene: every family is read straight off the extraction, loosely, tolerating the same traps
    /// <see cref="AiNets"/> does (real block width, <c>neindex</c>'s leading number skipped).</summary>
    public static AiDumpResult Ai(string dataRoot, string sharedZrdrPath, string chapterFilter)
    {
        System.Threading.Thread.CurrentThread.CurrentCulture = CultureInfo.InvariantCulture;
        var r = new AiDumpResult();
        var chapters = chapterFilter.Length == 0
            ? AiChapters
            : AiChapters.Where(c => c.Equals(chapterFilter, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (chapters.Length == 0)
        {
            r.Error = $"'{chapterFilter}' is not a known chapter. Available: {string.Join(", ", AiChapters)}";
            r.Summary = $"ai dump: {r.Error}";
            return r;
        }

        var sb = new StringBuilder();
        sb.AppendLine($"# AI data families: {dataRoot}");
        sb.AppendLine("# nets (patrol graphs), aiv (rosters), ai.zrd (turrets), zeppelins, egen (generators)");
        sb.AppendLine("# See docs/formats/ai-nets.md, ai-rosters.md, turrets.md, mission-entities.md.");
        sb.AppendLine();

        // Turrets: one shared, chapter-independent file, always reported in full, even under a
        // chapter filter, since ai.zrd.json is not chapter-scoped.
        try
        {
            var root = Zrdr.LoadFile(sharedZrdrPath, "ai.json");
            // The whole reader is wrapped in one outer element, same as zeppelins/egen:
            // [ ["TURRET", [entry0, entry1, …]] ].
            if (root.Count == 0 || root[0] is not List<object?> alt)
            {
                throw new InvalidDataException("ai.json: not a one-record TURRET reader");
            }
            var dict = ZrdrDict.FromAlternating(alt);
            var entries = dict.List("TURRET") ?? new List<object?>();
            int carried = 0;
            foreach (var e in entries)
            {
                if (e is List<object?> fields && ZrdrDict.FromAlternating(fields).Has("CREATE_STANDALONE"))
                {
                    carried++;
                }
            }
            r.TurretEntries = entries.Count;
            sb.AppendLine($"=== turrets: {sharedZrdrPath}/ai.json ===");
            sb.AppendLine($"  {r.TurretEntries} TURRET entries "
                + $"({carried} CREATE_STANDALONE/carried, {r.TurretEntries - carried} NODES/world emplacements)");
            sb.AppendLine();
        }
        catch (Exception e)
        {
            r.Errors.Add($"turrets: {e.Message}");
            sb.AppendLine($"=== turrets FAILED: {e.Message} ===").AppendLine();
        }

        foreach (var chapter in chapters)
        {
            var chapterZrdr = SessionPaths.ChapterZrdr(dataRoot, chapter);
            List<AiNet> nets;
            try
            {
                nets = AiNets.Load(chapterZrdr);
            }
            catch (Exception e)
            {
                r.Errors.Add($"{chapter} nets: {e.Message}");
                sb.AppendLine($"=== {chapter}: nets FAILED: {e.Message} ===").AppendLine();
                continue;
            }
            r.NetFiles += nets.Count;
            int nodeSum = nets.Sum(n => n.Nodes.Count);
            int edgeSum = nets.Sum(n => n.Edges.Count);

            var missions = DiscoverMissions(dataRoot, chapter);
            int aivBlocksChapter = 0, zepChapter = 0, genChapter = 0;
            foreach (var mission in missions)
            {
                var missionZrdr = SessionPaths.MissionZrdr(dataRoot, chapter, mission);

                try
                {
                    var root = Zrdr.LoadFile(missionZrdr, "aiv.json");
                    // Element 0 is the header (slotId/label pairs); 1..N are [name, fields] pairs.
                    // Never assume a fixed field count, 42/65/66/67/68/81 all ship.
                    for (int i = 1; i < root.Count; i++)
                    {
                        if (root[i] is List<object?> { Count: 2 } pair
                            && pair[0] is string && pair[1] is List<object?> fields)
                        {
                            aivBlocksChapter++;
                            r.AivFieldHistogram.TryGetValue(fields.Count, out int c);
                            r.AivFieldHistogram[fields.Count] = c + 1;
                        }
                    }
                    r.AivFiles++;
                }
                catch (IOException)
                {
                    // No aiv.zrd.json in this mission dir, not expected among the 53 the plan
                    // measured, but not fatal to the rest of the report either.
                }
                catch (Exception e)
                {
                    r.Errors.Add($"{chapter}/{mission} aiv: {e.Message}");
                }

                try
                {
                    zepChapter += LoadRecordList(missionZrdr, "zeppelins.json").Count;
                }
                catch (IOException)
                {
                }
                catch (Exception e)
                {
                    r.Errors.Add($"{chapter}/{mission} zeppelins: {e.Message}");
                }

                try
                {
                    genChapter += LoadRecordList(missionZrdr, "egen.json").Count;
                }
                catch (IOException)
                {
                }
                catch (Exception e)
                {
                    r.Errors.Add($"{chapter}/{mission} egen: {e.Message}");
                }
            }
            r.AivBlocks += aivBlocksChapter;
            r.ZeppelinRecords += zepChapter;
            r.GeneratorRecords += genChapter;
            r.MissionFiles += missions.Count;

            sb.AppendLine($"=== {chapter} ===");
            sb.AppendLine($"  nets: {nets.Count} file(s), {nodeSum} node(s), {edgeSum} edge(s)");
            sb.AppendLine($"  missions: {missions.Count} ({string.Join(", ", missions)})");
            sb.AppendLine($"  aiv: {aivBlocksChapter} vehicle block(s)  |  "
                + $"zeppelins: {zepChapter} record(s)  |  generators: {genChapter}");
            sb.AppendLine();
        }

        sb.AppendLine("=== totals ===");
        sb.AppendLine($"  nets: {r.NetFiles} across {chapters.Length} chapter(s)");
        sb.AppendLine($"  aiv: {r.AivBlocks} vehicle block(s) across {r.AivFiles} file(s)");
        sb.AppendLine($"  aiv field-count histogram: {{{string.Join(", ", r.AivFieldHistogram.Select(kv => $"{kv.Key}: {kv.Value}"))}}}");
        sb.AppendLine($"  turrets: {r.TurretEntries} entries");
        sb.AppendLine($"  zeppelins: {r.ZeppelinRecords} record(s)");
        sb.AppendLine($"  generators: {r.GeneratorRecords}");
        if (r.Errors.Count > 0)
        {
            sb.AppendLine($"  !! {r.Errors.Count} ERROR(s): {string.Join("; ", r.Errors)}");
        }

        r.Text = sb.ToString();
        r.Summary = r.Errors.Count == 0
            ? $"ai dump: {r.NetFiles} net(s), {r.AivBlocks} aiv block(s)/{r.AivFiles} file(s), "
              + $"{r.TurretEntries} turret(s), {r.ZeppelinRecords} zeppelin(s), {r.GeneratorRecords} generator(s)"
            : $"ai dump: {r.Errors.Count} ERROR(s), see the FAILED/!! lines above";
        return r;
    }

    // ---- destructible damage -----------------------------------------------------------------

    /// <summary>Every enabled collision shape under a subtree. <c>SetSubtreeActive</c> toggles each
    /// shape's <c>Disabled</c> as it swaps healthy→destroyed, and the swap can resolve to geometry
    /// outside the small anim anchor, pass <see cref="WorldRootOf"/> and compare the two sets
    /// around one kill. ⚠ Report OFF and ON separately, never their signed sum: see WORLD-10 in
    /// docs/verification.md.</summary>
    public static HashSet<CollisionShape3D> EnabledColliders(Node root)
    {
        var set = new HashSet<CollisionShape3D>();
        void Walk(Node n)
        {
            if (n is CollisionShape3D cs && !cs.Disabled)
            {
                set.Add(cs);
            }
            foreach (var c in n.GetChildren())
            {
                Walk(c);
            }
        }
        Walk(root);
        return set;
    }

    /// <summary>Every collider under a subtree that is enabled while nothing is drawn there, the
    /// invisible-wall census. Reports the offending shape's owning node chain, leaf-first. The
    /// generic tripwire for the class the C1/IA1 zeppelin belonged to
    /// (<see cref="Mech3.WorldCollision"/>): visibility is inherited and <c>Disabled</c> is not, so
    /// writing the two separately eventually disagrees with itself.</summary>
    public static List<string> InvisibleEnabledColliders(Node root)
    {
        var found = new List<string>();

        // Leaf-first ancestor names, cut at the first invisible one (bracketed), so the report
        // names the node that actually hid the geometry rather than the path to the world root.
        static string Chain(Node3D leaf)
        {
            var parts = new List<string>();
            for (Node? n = leaf; n != null && parts.Count < 12; n = n.GetParent())
            {
                bool hidden = n is Node3D { Visible: false };
                parts.Add(hidden ? $"[{n.Name}]" : n.Name.ToString());
                if (hidden)
                {
                    break;
                }
            }
            return string.Join(" < ", parts);
        }

        void Walk(Node n)
        {
            if (n is CollisionShape3D { Disabled: false } cs && !cs.IsVisibleInTree())
            {
                found.Add(Chain(cs));
            }
            foreach (var c in n.GetChildren())
            {
                Walk(c);
            }
        }
        Walk(root);
        return found;
    }

    /// <summary>The top <see cref="Node3D"/> above a node, the world subtree root, so a census
    /// walks placed + partition geometry and not the UI or the Window.</summary>
    public static Node3D WorldRootOf(Node3D n)
    {
        var t = n;
        while (t.GetParent() is Node3D p)
        {
            t = p;
        }
        return t;
    }

    /// <summary>Counts the <c>healthy</c>/<c>destroyed</c> variant nodes under a subtree and how
    /// many of each are shown, the death-swap diagnostic. Names only: the swap mechanism itself
    /// keys off the definition's own <c>OBJECT_ACTIVE_STATE</c>, never a name scan.</summary>
    public static void CountVariants(Node3D root, out int healthyVisible, out int healthyAll,
        out int destroyedVisible, out int destroyedAll)
    {
        int hVis = 0, hAll = 0, dVis = 0, dAll = 0;
        void Walk(Node3D n)
        {
            string cs = n.HasMeta(AnimRuntime.NameMeta)
                ? n.GetMeta(AnimRuntime.NameMeta).AsString()
                : n.Name.ToString();
            if (cs.Contains("healthy", StringComparison.OrdinalIgnoreCase))
            {
                hAll++;
                if (n.Visible) { hVis++; }
            }
            if (cs.Contains("destroyed", StringComparison.OrdinalIgnoreCase))
            {
                dAll++;
                if (n.Visible) { dVis++; }
            }
            foreach (var c in n.GetChildren())
            {
                if (c is Node3D c3)
                {
                    Walk(c3);
                }
            }
        }
        Walk(root);
        healthyVisible = hVis;
        healthyAll = hAll;
        destroyedVisible = dVis;
        destroyedAll = dAll;
    }

    /// <summary>Sweeps one live destructible instance per distinct def and records what its
    /// damage/death did. <paramref name="damageHd"/> &gt; 0 spends that HEALTH_DAMAGE per discrete
    /// weapon hit (swap, colliders, debris, sound, collide gate, reset/rekill); 0 sweeps HP
    /// continuously to find which stage effect fires at which health. Requires the world subtree in
    /// the scene tree with <see cref="AnimRuntime.ManualAdvance"/> set, so an out-of-tree transform
    /// read does not return identity mid-schedule.</summary>
    public static DamageResult Damage(AnimRuntime runtime, string chapter, string filter, float damageHd)
    {
        System.Threading.Thread.CurrentThread.CurrentCulture = CultureInfo.InvariantCulture;

        static bool HasDamage(AnimDefinition d) => d.Sequences.Any(s =>
            string.Equals(s.Name, "DAMAGE_SEQUENCE", StringComparison.OrdinalIgnoreCase));

        var result = new DamageResult
        {
            TotalInstances = runtime.Destructibles.Count,
            DistinctAnchors = runtime.Destructibles.DistinctAnchors,
        };

        // One representative instance per distinct def, a wildcard NAME binds many identical
        // towers, and sweeping every one would just repeat the same result and start hundreds of
        // effects.
        var chosen = new List<DestructibleRegistry.Instance>();
        var seenDefs = new HashSet<AnimDefinition>();
        foreach (var inst in runtime.Destructibles.All)
        {
            // Continuous-sweep mode only makes sense for staged DAMAGE_SEQUENCE defs; the
            // discrete-kill mode applies to EVERY destructible, the doors and gates instant-die
            // with no stages, so gating them out would hide exactly the collider cases.
            if (damageHd <= 0f && !HasDamage(inst.Def))
            {
                continue;
            }
            if (filter.Length > 0
                && !inst.Def.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            if (seenDefs.Add(inst.Def))
            {
                chosen.Add(inst);
            }
            if (chosen.Count >= SweepCap)
            {
                result.Capped = true;
                break;
            }
        }

        var sb = new StringBuilder();
        string mode = damageHd > 0f ? $"weapon hits, {damageHd:0.##} HEALTH_DAMAGE each" : "continuous HP sweep";
        string kind = damageHd > 0f ? "destructible def(s)" : "DAMAGE_SEQUENCE def(s)";
        sb.AppendLine($"damage-test: chapter {chapter}, filter '{filter}', mode = {mode}, "
            + $"{chosen.Count} {kind} of {result.TotalInstances} destructible instance(s)");
        foreach (var inst in chosen)
        {
            // DamageAt resolves a struck node to its authoritative instance; drive that one, the
            // picked reader twin would damage the compiled instance instead and never see HP fall.
            var target = damageHd > 0f ? (runtime.Destructibles.Resolve(inst.Anchor) ?? inst) : inst;
            var row = new DamageRow
            {
                Def = target.Def.Name,
                MaxHealth = target.MaxHealth,
                Source = target.Def.Archive != null ? "compiled" : "reader",
                Activation = target.Def.Activation.ToString(),
            };
            var fired = new List<(string At, string Effect)>();
            var started = new List<(string? Anim, Node3D? Anchor)>();
            int hit = 0;
            float atHp = target.MaxHealth;
            void OnStarted(AnimDefinition def, Node3D? anchor)
            {
                fired.Add((damageHd > 0f ? $"hit {hit}" : $"HP≤{atHp:0.##}", def.AnimName ?? def.Name));
                started.Add((def.AnimName, anchor));
            }

            target.Health = target.MaxHealth;
            target.Status = DestructibleRegistry.State.Healthy;
            target.DamageStage = 0;
            var colBefore = damageHd > 0f ? EnabledColliders(WorldRootOf(target.Anchor)) : new HashSet<CollisionShape3D>();
            int debrisBefore = runtime.BallisticMotionsLaunched;
            int soundsBefore = runtime.OneShotSoundsPlayed;
            runtime.OnInstanceStarted += OnStarted;
            if (damageHd > 0f)
            {
                int cap = (int)(target.MaxHealth / damageHd) + 4;   // a few past the expected kill
                while (target.Status != DestructibleRegistry.State.Destroyed && hit < cap)
                {
                    hit++;
                    runtime.DamageAt(target.Anchor, damageHd);
                }
            }
            else
            {
                // Fine enough to land on the round-fraction thresholds exactly (0.60/0.30 of HEALTH …).
                const int steps = 240;
                for (int i = 0; i <= steps; i++)
                {
                    atHp = target.MaxHealth * (1f - i / (float)steps);
                    target.Health = atHp;
                    runtime.ApplyDamageStages(target);
                }
            }
            runtime.OnInstanceStarted -= OnStarted;
            row.Hits = hit;
            row.Destroyed = target.Status == DestructibleRegistry.State.Destroyed;
            row.StagesFired = fired.Count;

            // Walk-up resolution check: a raycast actually strikes a deep descendant of the DAMAGE
            // NODE. Resolving from there must land back on this destructible. A descendant of the
            // anchor outside the damage node belongs to it no more than scenery.
            Node3D deep = target.DamageNode;
            while (deep.GetChildCount() > 0 && deep.GetChild(0) is Node3D child)
            {
                deep = child;
            }
            var back = runtime.Destructibles.Resolve(deep);
            row.Resolved = back?.Anchor == target.Anchor;
            string resolve = row.Resolved ? "resolve✓" : $"resolve✗({back?.Def.Name ?? "null"})";

            // Death-swap check: once killed, the healthy subtree should be hidden and the destroyed
            // subtree shown. Scan the anchor's descendants by cs_name, a test diagnostic (the
            // mechanism keys off the def's own OBJECT_ACTIVE_STATE, not names).
            string swap = "";
            if (damageHd > 0f && row.Destroyed)
            {
                CountVariants(target.Anchor, out int hVis, out int hAll, out int dVis, out int dAll);
                if (hAll > 0 || dAll > 0)
                {
                    swap = $"swap[healthy {hVis}/{hAll} shown, destroyed {dVis}/{dAll} shown]; ";
                }
                // Colliders switched OFF (healthy collision that blocked flight) vs ON (wreck/debris
                // brought solid). ⚠ Report both, per WORLD-10; a net count hides the door removal.
                var colAfter = EnabledColliders(WorldRootOf(target.Anchor));
                row.CollidersOff = colBefore.Count(cs => !colAfter.Contains(cs));
                row.CollidersOn = colAfter.Count(cs => !colBefore.Contains(cs));
                swap += $"col[off {row.CollidersOff}, on {row.CollidersOn}]; ";
                // Debris is SCHEDULED (the water tower's at t=2.2 s), so advance past it to let it
                // launch, done AFTER swap/col so those stay the immediate post-death, pre-tick state.
                for (int i = 0; i < 7; i++)
                {
                    runtime.Advance(0.5f);   // 3.5 s, past the ~2.2 s schedule, into the tumble
                }
                row.Debris = runtime.BallisticMotionsLaunched - debrisBefore;
                swap += $"debris[{row.Debris} launched]; ";
                // Audio cannot be screenshot-verified, so a nonzero count here is the headless proof
                // the destruction sounded; zero when muted or the death authors no Sound event.
                row.Sounds = runtime.OneShotSoundsPlayed - soundsBefore;
                swap += $"snd[{row.Sounds} played]; ";
            }
            // Stop AFTER the debris tick so pieces launch first. Reader and compiled defs share
            // tower nodes, so a leftover live effect makes the twin's CALL_ANIMATION a no-op.
            foreach (var (anim, anchor) in started)
            {
                runtime.Stop(anim, anchor);
            }

            // Collide-gate probe: an overkill COLLISION must break a WeaponHit tower just as it
            // breaks a WeaponOrCollideHit facade. ACTIVATION decides only what the
            // PLANE does, the second column: through, or solid.
            string collide = "";
            if (damageHd > 0f)
            {
                target.Health = target.MaxHealth;
                target.Status = DestructibleRegistry.State.Healthy;
                target.DamageStage = 0;
                bool flyThrough = runtime.CollideDamageAt(target.Anchor, target.MaxHealth + 1f);
                bool broke = target.Status == DestructibleRegistry.State.Destroyed;
                row.CollideAccepted = broke;
                row.CollideFlyThrough = flyThrough;
                collide = $"collide[{(broke ? "✓ damaged" : "✗ IGNORED")}, "
                    + $"plane {(flyThrough ? "through" : "solid")}, {row.Activation}]; ";
            }

            // Reset/restore check: from a destroyed state, ResetDestructible returns the object to
            // healthy (full HP, healthy subtree visible, destroyed hidden, debris flown home), and an
            // identical second kill takes the same hits, proving destroy→reset→destroy is idempotent.
            string reset = "";
            if (damageHd > 0f)
            {
                int cap2 = (int)(target.MaxHealth / damageHd) + 4;
                while (target.Status != DestructibleRegistry.State.Destroyed && cap2-- > 0)
                {
                    runtime.DamageAt(target.Anchor, damageHd);   // ensure dead before resetting
                }
                runtime.ResetDestructible(target);
                bool backHp = target.Status == DestructibleRegistry.State.Healthy
                    && target.Health >= target.MaxHealth - 1e-3f;
                CountVariants(target.Anchor, out int hVis, out int hAll, out int dVis, out int dAll);
                bool backVis = hAll == 0 || (hVis == hAll && dVis == 0);   // healthy shown, destroyed hidden
                int rekap = (int)(target.MaxHealth / damageHd) + 4;
                int hits2 = 0;
                while (target.Status != DestructibleRegistry.State.Destroyed && hits2 < rekap)
                {
                    hits2++;
                    runtime.DamageAt(target.Anchor, damageHd);
                }
                row.ResetHealthy = backHp && backVis;
                row.RekillMatched = hits2 == hit;
                reset = $"reset[healthy={(row.ResetHealthy == true ? "✓" : "✗")} (h{hVis}/{hAll},d{dVis}/{dAll}), "
                    + $"rekill {hits2}h {(row.RekillMatched == true ? "✓" : $"✗ vs {hit}")}]; ";
            }

            string stages = fired.Count == 0
                ? "no stage effect fired"
                : string.Join(", ", fired.Select(f => $"{f.At} → {f.Effect}"));
            string outcome = damageHd > 0f
                ? (row.Destroyed
                    ? $"DESTROYED in {hit} hit(s); {swap}{collide}{reset}"
                    : $"SURVIVED {hit} hit(s); {collide}{reset}")
                : "";
            row.Line = $"  {row.Def} (HEALTH {row.MaxHealth:0.##}, {row.Source}) {resolve}: {outcome}{stages}";
            sb.AppendLine(row.Line);
            result.Rows.Add(row);
        }
        result.Text = sb.ToString();

        // The world's collidable-geometry inventory, so we can confirm destructible roles
        // (healthy / destroyed / door*) are among the solid geometry. Counts per owning-mesh
        // cs_name, a per-name census is enough to see what is solid; positions are not needed.
        if (chosen.Count > 0)
        {
            var byName = new SortedDictionary<string, int>();
            int cols = 0;
            void Walk(Node n, string parentName)
            {
                string name = n is Node3D n3 && n3.HasMeta(AnimRuntime.NameMeta)
                    ? n3.GetMeta(AnimRuntime.NameMeta).AsString()
                    : n.Name.ToString();
                if (n is StaticBody3D body && body.Name.ToString() == "col")
                {
                    cols++;
                    byName.TryGetValue(parentName, out int c);
                    byName[parentName] = c + 1;
                }
                foreach (var c in n.GetChildren())
                {
                    Walk(c, name);
                }
            }
            Walk(WorldRootOf(chosen[0].Anchor), "");
            var inv = new StringBuilder($"{cols} collidable meshes, by owner cs_name:\n");
            foreach (var (nm, c) in byName)
            {
                inv.AppendLine($"  {c,4}  {nm}");
            }
            result.CollidableMeshes = cols;
            result.CollidersText = inv.ToString();
        }
        result.Summary = $"damage-test: {result.Rows.Count} def(s) swept of "
                         + $"{result.TotalInstances} instance(s) across {result.DistinctAnchors} node group(s)";
        // The one-shot death sounds this sweep fired are fire-and-forget nodes swept in
        // WorldSounds.Tick, but this harness pumps no frames, so free them here or they leak.
        runtime.Sounds?.FlushOneShots();
        return result;
    }

    // ---- debris shading --------------------------------------------------------------------

    /// <summary>What shades the pieces a destructible's death flings. Every mesh-bearing node
    /// under a matched destructible is reported with the model's <c>lighting</c> flag and whether
    /// the node flew. Each material contributes its sheet, its mean texel, the area-weighted mean
    /// baked vertex colour, and the product the fullbright world shader draws from those. A null
    /// <paramref name="textures"/> leaves the sheet unsampled and the material's own colour stands
    /// in. ⚠ An instrument, not an assertion: no number here is a target.</summary>
    public static DebrisShadingResult DebrisShading(AnimRuntime runtime, GameZ gamez,
        TextureArchive? textures, string chapter, string name, float worldLightLinear)
    {
        System.Threading.Thread.CurrentThread.CurrentCulture = CultureInfo.InvariantCulture;

        static string CsName(Node3D n) => n.HasMeta(AnimRuntime.NameMeta)
            ? n.GetMeta(AnimRuntime.NameMeta).AsString()
            : n.Name.ToString();

        var result = new DebrisShadingResult { Chapter = chapter, Filter = name, WorldLight = worldLightLinear };
        var byIndex = new Dictionary<int, GameZNode>();
        foreach (var n in gamez.Nodes)
            byIndex[n.Index] = n;

        var targets = new Dictionary<ulong, DestructibleRegistry.Instance>();
        foreach (var inst in runtime.Destructibles.All)
        {
            bool match = inst.Def.Name.Contains(name, StringComparison.OrdinalIgnoreCase)
                || (inst.Def.AnimName?.Contains(name, StringComparison.OrdinalIgnoreCase) ?? false)
                || CsName(inst.Anchor).Contains(name, StringComparison.OrdinalIgnoreCase);
            if (match && runtime.Destructibles.Resolve(inst.Anchor) is { } t)
                targets[t.Anchor.GetInstanceId()] = t;
        }

        var sb = new StringBuilder();
        sb.AppendLine($"debris-shading: chapter {chapter}, filter '{name}', "
            + $"csky_world_light {worldLightLinear:0.###} linear, {targets.Count} destructible(s)");
        if (targets.Count == 0)
        {
            result.Text = sb.ToString();
            result.Summary = "debris-shading: no destructible matched";
            return result;
        }

        // Every mesh node under each anchor, read BEFORE the kill so the pre-kill visibility is the
        // healthy world's own. The rows are re-marked with what flew once the deaths have run.
        var rows = new List<DebrisShadingRow>();
        var byNode = new Dictionary<ulong, DebrisShadingRow>();
        foreach (var target in targets.Values)
        {
            void Walk(Node3D n)
            {
                if (n.HasMeta(AnimRuntime.IndexMeta)
                    && byIndex.TryGetValue((int)n.GetMeta(AnimRuntime.IndexMeta), out var gn)
                    && gn.MeshIndex >= 0 && gn.MeshIndex < gamez.Meshes.Count
                    && !gamez.IsMarkerGizmo(gn.MeshIndex))
                {
                    var row = ReadShadingRow(gamez, textures, gn, worldLightLinear);
                    row.Def = target.Def.Name;
                    row.CsName = CsName(n);
                    row.VisibleBefore = n.IsVisibleInTree();
                    ReadBuiltMaterial(n, row);
                    rows.Add(row);
                    byNode[n.GetInstanceId()] = row;
                }

                foreach (var c in n.GetChildren())
                {
                    if (c is Node3D c3)
                    {
                        Walk(c3);
                    }
                }
            }

            Walk(target.Anchor);
        }

        int launchesBefore = runtime.BallisticMotionsLaunched;
        foreach (var target in targets.Values)
            runtime.DamageAt(target.Anchor, target.MaxHealth + 1f);

        // Stepped rather than jumped: a ballistic body is registered and retired inside the run.
        // One long advance would report an empty motion list for pieces that flew the whole way.
        for (int i = 0; i < 24; i++)
        {
            runtime.Advance(0.25f);
            foreach (var m in runtime.Motions.Live)
            {
                if (m is Mech3.Anim.MotionRuntime && byNode.TryGetValue(m.Target.GetInstanceId(), out var row))
                {
                    row.Launched = true;
                }
            }
        }

        result.Launches = runtime.BallisticMotionsLaunched - launchesBefore;
        foreach (var (id, row) in byNode)
        {
            if (GodotObject.InstanceFromId(id) is Node3D n3)
            {
                row.VisibleAfter = n3.IsVisibleInTree();
            }
        }

        sb.AppendLine($"  {result.Launches} ballistic launch(es); {rows.Count} mesh node(s) under the anchor(s)");
        foreach (var row in rows.OrderByDescending(r => r.Launched).ThenBy(r => r.CsName, StringComparer.Ordinal))
        {
            sb.AppendLine("  " + row.Line);
        }

        result.Rows.AddRange(rows);
        result.Text = sb.ToString();
        result.Summary = $"debris-shading: {rows.Count} mesh node(s), {rows.Count(r => r.Launched)} flew, "
            + $"{result.Launches} ballistic launch(es)";
        runtime.Sounds?.FlushOneShots();
        return result;
    }

    // ---- pull instrument ---------------------------------------------------------------------

    /// <summary>The per-step readout of a held full pull: α, window, limiter, demanded and delivered
    /// G against the ceiling, nose and path rates, and the first-360° loop mean, so the pitch-rate
    /// residual is attributed step by step (docs/org/flightModel.md, "The α a full pull holds").
    /// <paramref name="aoaLimiterFactor"/> is the A/B seam (1 the decode); <paramref name="liftAccelRate"/>
    /// replaces the authored lag rate for one diagnostic run and is never a setting.
    /// ⚠ An instrument, not an assertion: nothing here is a target.</summary>
    public static string PullToLimit(string zrdrPath, string planeNodeName, float entryMph,
        float seconds, float aoaLimiterFactor, float? liftAccelRate = null)
    {
        System.Threading.Thread.CurrentThread.CurrentCulture = CultureInfo.InvariantCulture;
        var stats = PlaneStats.Load(zrdrPath, planeNodeName);
        if (liftAccelRate is float rate)
            stats.LiftAccelRate = rate;
        var m = new FlightModel(stats) { AoaLimiterFactor = aoaLimiterFactor };
        m.Reset(Vector3.Zero, Level(), entryMph * Mph, 1f);
        var sb = new StringBuilder();
        sb.AppendLine($"pull-to-limit {planeNodeName}: full back stick and full throttle from "
                      + $"{entryMph:0} mph level, aoaLimiterFactor {aoaLimiterFactor:0.##}, "
                      + $"maxAOA {Mathf.RadToDeg(Mathf.Acos(stats.MaxAoaCos)):0.0}°, liftAOAs "
                      + $"{Mathf.RadToDeg(Mathf.Acos(stats.LiftAoaCosLo)):0.0}/"
                      + $"{Mathf.RadToDeg(Mathf.Acos(stats.LiftAoaCosHi)):0.0}°, lift_accel_rate "
                      + $"{stats.LiftAccelRate:0.###}/s, highGs {stats.HighGStart:0.#}/{stats.HighGMax:0.#}");
        sb.AppendLine("     t      mph     α°  window  limit  demandG  liftG  bodyUpG   capG  "
                      + "nose°/s  path°/s  vane°/s²");
        var prevDir = m.VelocityDir;
        float peakNose = 0f, peakG = 0f, noseSwept = 0f, loopTime = 0f;
        for (int i = 1; i <= (int)MathF.Round(seconds / EnvDt); i++)
        {
            m.Step(new FlightInput { Pitch = 1f, Throttle = 1f }, EnvDt);
            // The footage's rate is binned round a whole loop, so the first 360° of nose rotation
            // is the comparable figure, not the level-entry transient or the 2 s settled value.
            noseSwept += Mathf.RadToDeg(m.PhysicalBodyRates.X) * EnvDt;
            if (loopTime == 0f && noseSwept >= 360f)
                loopTime = i * EnvDt;
            float cosA = Mathf.Cos(Mathf.DegToRad(m.Alpha));
            float window = m.OpposingCommandLimitAt(m.Speed, cosA, 0f, 1f);
            float pathRate = Mathf.RadToDeg(Mathf.Acos(Mathf.Clamp(prevDir.Dot(m.VelocityDir), -1f, 1f))) / EnvDt;
            prevDir = m.VelocityDir;
            float noseRate = Mathf.RadToDeg(m.PhysicalBodyRates.X);
            float liftG = Mathf.Min(Mathf.Clamp(m.LoadFactorDemand, -5f, 9f), m.LiftCapAt(m.Speed));
            float vane = Mathf.RadToDeg(m.WeathervaneTorque().X * stats.RecInertia.X);
            peakNose = Mathf.Max(peakNose, noseRate);
            peakG = Mathf.Max(peakG, m.BodyUpLoadFactor);
            sb.AppendLine($"{i * EnvDt,6:0.000} {m.Speed / Mph,7:0.0} {m.Alpha,6:0.0} {window,7:0.000} "
                          + $"{m.CommandLimit,6:0.000} {m.LoadFactorDemand,8:0.00} {liftG,6:0.00} "
                          + $"{m.BodyUpLoadFactor,8:0.00} {m.LiftCapAt(m.Speed),6:0.00} {noseRate,8:0.00} "
                          + $"{pathRate,8:0.00} {vane,9:0.00}");
        }

        sb.AppendLine($"peak nose rate {peakNose:0.00} °/s, final {Mathf.RadToDeg(m.PhysicalBodyRates.X):0.00} °/s "
                      + $"at α {m.Alpha:0.0}°; peak body-up G {peakG:0.00} against highGs[0] "
                      + $"{stats.HighGStart:0.#}; nose swept {noseSwept:0} ° in {seconds:0.0} s"
                      + (loopTime > 0f
                          ? $", first 360° in {loopTime:0.00} s = {360f / loopTime:0.00} °/s loop mean"
                          : ", no full loop in the run"));
        return sb.ToString();
    }

    // ---- stall entry -------------------------------------------------------------------------

    /// <summary>The per-step readout of a decelerating stall entry, stick centred: the stall flag,
    /// the low-speed authority ramp, the AOA window, the decoded nose-drop term, the weathervane and
    /// the pitch rate. The AI law's low-speed recovery arm is READ at each state and logged
    /// (<c>arm</c>, <c>cmdPitch</c>) without flying the aeroplane, so the arm's command and the
    /// plant's own authority stay separable; <paramref name="aiPath"/> runs the plant's AI arm, which
    /// carries neither drop nor weathervane. ⚠ An instrument, not an assertion.</summary>
    public static string StallEntry(string zrdrPath, string planeNodeName, float entryMph,
        float noseDeg, float seconds, bool aiPath)
    {
        System.Threading.Thread.CurrentThread.CurrentCulture = CultureInfo.InvariantCulture;
        var stats = PlaneStats.Load(zrdrPath, planeNodeName);
        var m = new FlightModel(stats, aiPath);
        m.Reset(Vector3.Zero, Pitched(noseDeg), entryMph * Mph, 0f);
        var sb = new StringBuilder();
        sb.AppendLine($"stall-entry {planeNodeName}: {entryMph:0} mph, nose {noseDeg:0}°, throttle 0, "
                      + "stick centred, " + (aiPath ? "AI force path" : "player force path")
                      + $"; stall {m.StallSpeed / Mph:0.0} mph, fd_speed {stats.FdSpeed / Mph:0.0} mph, "
                      + $"stall_mag {stats.StallMag:0.##}, recInertia.x {stats.RecInertia.X:0.##}, "
                      + $"damp {stats.AngMomentumDamp:0.#}, return_rate {stats.ReturnRate:0.##}, "
                      + $"turn_fade {stats.TurnFadeIn / Mph:0.#}/{stats.TurnFadeOut / Mph:0.#} mph");
        sb.AppendLine("     t      mph   nose°    flag   ramp  window  drop°/s²  vane°/s²"
                      + "  pitch°/s  arm  armPitch");
        for (int i = 1; i <= (int)MathF.Round(seconds / EnvDt); i++)
        {
            m.Step(new FlightInput(), EnvDt);

            // Ten steps to the printed line: the plant runs at the full step and the readout is
            // sampled, because a stall entry is seconds long and every step of it is one shape.
            if (i % 10 != 0)
                continue;

            var aim = m.Position + new Vector3(0f, 0f, -1000f);
            var steer = AiControlLaw.Steer(m, aim, Vector3.Zero, AiLawParams.Cruise, 0f, EnvDt);
            bool armed = m.Attitude.Z.Y < AiControlLaw.RecoveryNoseY
                         && m.Speed < AiControlLaw.RecoverySpeed;
            float flag = m.StallFlag;
            float drop = aiPath
                ? 0f
                : Mathf.RadToDeg(Mathf.Max(0f, flag) * stats.StallMag * stats.RecInertia.X);
            float vane = aiPath ? 0f : Mathf.RadToDeg(m.WeathervaneTorque().X * stats.RecInertia.X);
            float window = m.OpposingCommandLimitAt(
                m.Speed, Mathf.Cos(Mathf.DegToRad(m.Alpha)), 0f, 1f);
            float nose = Mathf.RadToDeg(Mathf.Asin(Mathf.Clamp((-m.Attitude.Z).Y, -1f, 1f)));
            sb.AppendLine($"{i * EnvDt,6:0.000} {m.Speed / Mph,8:0.0} {nose,7:0.0} {flag,7:0.000} "
                          + $"{m.PitchAuthorityAt(m.Speed),6:0.000} {window,7:0.000} "
                          + $"{drop,9:0.00} {vane,9:0.00} {Mathf.RadToDeg(m.PhysicalBodyRates.X),9:0.00} "
                          + $"{(armed ? "yes" : "no"),4} {steer.Pitch,9:0.00}");
        }

        return sb.ToString();
    }

    // ---- flight envelope ---------------------------------------------------------------------

    /// <summary>Every stock airframe's envelope in one deterministic report, the whole-plant
    /// instrument, since the constants are coupled and a change to one moves rows on airframes
    /// nobody was looking at. <c>--dump-flight=all</c> and the parity ledger both run this, so the
    /// dump a change is diffed against and the ledger published from cannot disagree.</summary>
    public static FlightEnvelopeResult FlightEnvelopeAll(string zrdrPath)
    {
        var all = new FlightEnvelopeResult();
        var sb = new StringBuilder();
        foreach (string plane in StockAirframeNodes)
        {
            var one = FlightEnvelope(zrdrPath, plane);
            sb.AppendLine(one.Error ?? one.Text);
            sb.AppendLine();
            all.Rows.AddRange(one.Rows);
            all.ReachedByPlane[plane] = one.Reached;
            if (one.Error != null)
            {
                all.Error ??= one.Error;
            }
        }

        all.Text = sb.ToString();
        all.Summary = all.Error != null
            ? $"flight envelope: {all.Error}"
            : $"flight envelope: {StockAirframeNodes.Length} airframes, {all.Rows.Count} rows, "
              + $"{all.Asserted} asserted, {all.Failed} failed";
        return all;
    }

    /// <summary>Steps a throwaway <see cref="FlightModel"/> through the manoeuvres the original was
    /// measured flying, and reports both numbers side by side, see docs/cli.md's <c>--dump-flight</c>
    /// entry and docs/org/flightModel.md. No world, no scene: it constructs the model directly and
    /// integrates it at the fixed <c>--det</c> step.
    /// ⚠ Every target is the Bloodhawk's, the only airframe recorded flying. Another plane's run
    /// reports its numbers with nothing to assert against; that is honest, not a gap to fill.</summary>
    public static FlightEnvelopeResult FlightEnvelope(string zrdrPath, string planeNodeName)
    {
        System.Threading.Thread.CurrentThread.CurrentCulture = CultureInfo.InvariantCulture;
        var r = new FlightEnvelopeResult();
        PlaneStats stats;
        try
        {
            stats = PlaneStats.Load(zrdrPath, planeNodeName);
        }
        catch (Exception e)
        {
            r.Error = $"could not load plane stats for '{planeNodeName}' ({zrdrPath}): {e.Message}";
            r.Summary = $"flight envelope: {r.Error}";
            return r;
        }

        bool bhawk = planeNodeName.Equals("player_bhawk", StringComparison.OrdinalIgnoreCase);
        float fd = stats.FdSpeed;
        // Thrust is a curve in Mach, not a constant, so the header samples the model's own method at
        // fd_speed rather than keeping a second copy of the formula that could go stale.
        float thrustAccel = new FlightModel(stats).ThrustAccelAt(fd, 1f);
        var watch = new EnvelopeMargins();

        void Row(string name, string what, string unit, double model, double? target,
                 double tol, string detail = "", bool info = false)
        {
            r.Rows.Add(new FlightRow
            {
                Plane = planeNodeName,
                Name = name,
                What = what,
                Unit = unit,
                Model = model,
                Target = bhawk ? target : null,
                Tolerance = tol,
                Detail = detail,
                Informational = info,
                // Empty for the extra rows a shared run reports (the turn trio, the zoom pair):
                // the margins belong to the flight, so they print once, on its first row.
                Margins = watch.Take(),
            });
        }

        // --- level full-throttle equilibrium. Drag is normalized so drag(fd_speed) = max thrust,
        // so this must land on fd_speed for every airframe by construction; it is here because that
        // construction is exactly what a thrust change could break silently.
        var m = Fresh(stats, Level(), 0.5f * fd, 1f);
        Run(m, 1f, 180f, pitch: 0f, watch: watch);
        Row("level-top-speed", "level full throttle held to equilibrium", "mph",
            m.Speed / Mph, LevelTopSpeedMph, 0.5,
            $"fd_speed = {fd / Mph:0.0} mph, α {m.Alpha:0.0}°, target is the decoded lever-1 "
            + "solve (PartThrottleEquilibriumTests); the filmed 300.40 is discarded");

        // --- acceleration. The force path is byte-verified against the binary with nothing fitted,
        // so its own number is the answer; the filmed 3.76 s is discarded and printed as an
        // annotation. ⚠ Do NOT close the gap by scaling thrust or drag. docs/org/flightModel.md.
        m = Fresh(stats, Level(), 150f * Mph, 1f);
        double tAccel = RunUntil(m, 1f, 30f, () => m.Speed >= 290f * Mph, watch: watch);
        Row("accel-150-290", "level full throttle, 150 -> 290 mph", "s", tAccel, null, 0.0,
            $"α {m.Alpha:0.0}° at finish, decoded force path; footage read 3.76 s, discarded",
            info: true);

        // --- terminal dive, at 70.7°, the angle the original's "vertical" clip actually came out
        // at. The attitude-thrust scale ADDS thrust here (×1.227). The target is the decoded
        // along-path balance, not the clip's speed. docs/org/flightModel.md.
        m = Fresh(stats, Pitched(-70.7f), 0.9f * fd, 1f);
        Run(m, 1f, 120f, pitch: 0f, watch: watch);
        double pathDeg = Mathf.RadToDeg(Mathf.Asin(Mathf.Clamp(m.VelocityDir.Y, -1f, 1f)));
        Row("terminal-dive", "70.7° dive at full throttle, held to terminal", "mph",
            m.Speed / Mph, TerminalDiveMph, 2.0,
            $"settled path {pathDeg:0.0}°, {m.Speed / fd:0.000} x fd_speed, α {m.Alpha:0.0}°, "
            + $"thrust ×{FlightModel.AttitudeThrustScale(m.Attitude.Z.Y):0.000}, target is the "
            + "decoded thrust/drag/gravity balance at this path; the filmed 355.2 is discarded");

        // --- roll. Accumulated body roll rate: no other axis is commanded. The target is the
        // DECODE's own, quaternion angle doubles the stored half-angle rate to 181.3 °/s steady.
        // Its 2.08 s target includes spin-up; the original's ADI stopwatch read 2.05 s.
        m = Fresh(stats, Level(), fd, 1f);
        double tRoll = RunUntil(m, 1f, 30f, RollAccum(m), roll: 1f, watch: watch);
        Row("roll-360", "full aileron from level cruise, 360°", "s", tRoll, 2.08, 0.25,
            $"α {m.Alpha:0.0}° at finish, target is the decoded steady rate "
            + "2 * roll_torque · recInertia.z / ang_momentum_damp with its own spin-up; a stopwatch "
            + "read 2.05 s off the original's ADI");

        // --- pitch at three speeds. The AOA window and the lag rate are both the binary's, so the
        // rate they produce is the answer and the loop-binned 33.00 is discarded. ⚠ Do NOT close
        // the gap by weakening the window, the lag rate or maxAOA. docs/org/flightModel.md.
        var pitchRates = new List<double>();
        var pitchAlphas = new List<double>();
        foreach (float mph in new[] { 120f, 200f, 280f })
        {
            m = Fresh(stats, Level(), mph * Mph, 1f);
            Run(m, 1f, 2f, pitch: 1f, watch: watch);
            pitchRates.Add(Mathf.RadToDeg(m.PhysicalBodyRates.X));
            pitchAlphas.Add(m.Alpha);
        }
        Row("pitch-rate", "sustained full-elevator body pitch rate", "°/s",
            pitchRates[1], null, 0.0,
            $"at 120/200/280 mph = {pitchRates[0]:0.0}/{pitchRates[1]:0.0}/{pitchRates[2]:0.0} °/s, "
            + $"α = {pitchAlphas[0]:0.0}/{pitchAlphas[1]:0.0}/{pitchAlphas[2]:0.0}° "
            + "(the alignment lag at this body rate), decoded window and lag rate; footage read "
            + "33.00 °/s, discarded",
            info: true);

        // --- yaw. The one axis 'eff' scales, so a thrust change moves it. The *Tune decode found no
        // per-axis factor in the original's torque path, so this rate is the decoded one and the
        // filmed 28.6 s is discarded. docs/org/flightModel.md.
        m = Fresh(stats, Level(), 290f * Mph, 1f);
        double sumSpeed = 0, samples = 0;
        double tYaw = RunUntil(m, 1f, 60f, YawAccum(m), yaw: 1f,
                               onStep: () => { sumSpeed += m.Speed; samples++; }, watch: watch);
        Row("yaw-360", "full rudder from 290 mph, 360°", "s", tYaw, null, 0.0,
            (samples > 0 ? $"mean speed {sumSpeed / samples / Mph:0.0} mph, " : "")
            + $"α {m.Alpha:0.0}° at finish, decoded torque path; footage read 28.6 s, discarded",
            info: true);

        // --- the altitude ceiling: fixed 22° nose-up hold (attitude set once, not continuous
        // elevator, which would loop instead of climb). The thin band above 2000 m is what stops the
        // climb, and the height reached above the edge is the coast the crossing rate buys.
        m = Fresh(stats, Pitched(22f), fd, 1f);
        double apexFt = 0;
        RunUntil(m, 1f, 240f, () => m.VelocityDir.Y <= 0f && m.Position.Y > BandEdgeM,
                 pitch: 0f, onStep: () => apexFt = Math.Max(apexFt, m.Position.Y / Ft), watch: watch);
        Row("altitude-ceiling", "22° nose-up hold at full throttle, apex reached", "ft",
            apexFt, null, 0.0,
            $"{m.Speed / Mph:0.0} mph at apex, α {m.Alpha:0.0}°, the ceiling is the 6561.7 ft band "
            + "edge and the height above it is bought with the climb rate, so no constant sets it",
            info: true);

        // --- level speed 15 m under the band edge: the atmosphere must not leak the thin band down
        // here, the original's level equilibrium measured flat to ±0.3 mph right up to 1988 m.
        m = Fresh(stats, Level(), 0.5f * fd, 1f);
        m.Position = new Vector3(0f, 1988f, 0f);
        Run(m, 1f, 180f, pitch: 0f, watch: watch);
        Row("level-speed-near-cap", "level full throttle at 1988 m, held to equilibrium", "mph",
            m.Speed / Mph, LevelTopSpeedMph, 0.5,
            $"the band edge must not leak below 2000 m, α {m.Alpha:0.0}°, same decoded lever-1 "
            + "solve as level-top-speed");

        // --- sustained turn: full throttle, stick full back from a 100° banked entry, settled 10 s
        // then averaged over the original's own 15.9 s window. ⚠ Bank is left FREE, never forced,
        // forcing it via atan2 breaks the moment the nose leaves horizontal. docs/org/flightModel.md.
        var turn = SustainedTurn(stats, 100f, 298.96f * Mph, settle: 10f, window: 15.9f, watch);
        Row("sustained-turn-speed", "full back stick from a banked entry, settled speed", "mph",
            turn.SpeedMph, null, 0.0,
            $"entered at 100° bank, settled at {turn.BankDeg:0.0}° (emergent, not held), "
            + $"α {turn.Alpha:0.0}°, swept {turn.SweptDeg:0} °, decoded plant; footage read "
            + "222.94 mph and 449.8° in the same window, discarded",
            info: true);

        // The sink's failure direction is one-way, falling out of the turn, not climbing, which
        // is why the row is read as a floor rather than a band. docs/org/flightModel.md.
        Row("sustained-turn-sink", "sustained max-pull turn, sink rate", "ft/s",
            turn.SinkFtS, null, 0.0,
            "negative = climbing; the failure this watches is falling out of the turn (before the "
            + "B12 lift re-key this read 18.29). Footage read 1.85 ft/s, discarded",
            info: true);

        // The rotation path is decoded whole, so this rate is the answer and 18.95 is discarded.
        // Do not chase the ADI's +100°, it reads attitude, not bank. docs/org/flightModel.md.
        Row("sustained-turn-rate", "sustained max-pull turn, heading rate", "°/s",
            turn.RateDegS, null, 0.0,
            $"{turn.RateDegS / 18.95:0.00}x the discarded footage. The footage pulls 1.6x slower BANKED than "
            + "wings-level (18.95 vs 30.16 °/sim-s round its own loop) and we pull the same rate in "
            + "both, so the difference is bank/load-factor, not pitch authority. Its 18.95 "
            + "°/sim-s at 222.94 mph implies a 58.7° bank, and CAP-33 confirmed that IS its bank, "
            + "the ADI's +100° is airframe attitude, not bank",
            info: true);

        // --- part throttle: the only place the drag shape is observable (full throttle is
        // fd_speed by construction for any curve). Both rows report the decoded plant's own number;
        // the decel row's filmed figure is discarded beside it. docs/org/flightModel.md.
        m = Fresh(stats, Level(), 0.9f * fd, 0.125f);
        Run(m, 0.125f, 300f, pitch: 0f, watch: watch);
        double idlePath = Mathf.RadToDeg(Mathf.Asin(Mathf.Clamp(m.VelocityDir.Y, -1f, 1f)));
        // ⚠ Every 1/8-throttle figure ever proposed for this row came off video. The decoded target
        // is asserted in PartThrottleEquilibriumTests instead.
        Row("eighth-throttle-speed", "1/8 throttle held to equilibrium", "mph",
            m.Speed / Mph, null, 0.0,
            $"{m.Speed / fd:0.000} x fd_speed, settled path {idlePath:0.0}°, α {m.Alpha:0.0}°, "
            + "against the decoded level-equilibrium curve, not footage "
            + "(docs/org/flightModel.md, 'Part-throttle equilibrium')",
            info: true);

        // ⚠ ZERO throttle, not 1/8, the footage cuts 8/8 to 0/8, so this is a pure drag probe.
        // Run at 1/8 instead and the model reads 12.1 s (an artifact of the wrong throttle, not a
        // finding): 150 mph sits only 8% above that equilibrium, so the approach is asymptotic.
        m = Fresh(stats, Level(), 290f * Mph, 0f);
        double tDecel = RunUntil(m, 0f, 60f, () => m.Speed <= 150f * Mph, watch: watch);
        Row("decel-290-150", "throttle cut to ZERO, 290 -> 150 mph, level", "s", tDecel, null, 0.0,
            $"pure drag, no thrust term to assume, α {m.Alpha:0.0}° at finish, the polar is the "
            + "binary's; footage read 7.04 s, discarded", info: true);

        // --- zoom climb, INFORMATIONAL: full throttle, full back stick from the same take that
        // pinned pitch-rate, so the stick history is known. ⚠ Must be a held FULL pull, a
        // slower/released pull is a different flight and must not be averaged with it. docs/org/flightModel.md.
        m = Fresh(stats, Level(), 300f * Mph, 1f);
        float apex = 0f, minSpeed = float.MaxValue, alphaAtMinSpeed = 0f;
        double tApex = RunUntil(m, 1f, 30f,
            () => m.Position.Y < apex - 1f, pitch: 1f,
            onStep: () =>
            {
                apex = Mathf.Max(apex, m.Position.Y);
                if (m.Speed < minSpeed)
                {
                    minSpeed = m.Speed;
                    alphaAtMinSpeed = m.Alpha;
                }
            }, watch: watch);
        Row("zoom-climb", "full pull from 300 mph level, altitude gained", "ft",
            apex / Ft, null, 0.0,
            $"min speed {minSpeed / Mph:0.0} mph, apex at {tApex:0.0} s, "
            + $"α {alphaAtMinSpeed:0.0}° at min speed, decoded plant; footage read 936 ft, "
            + "127.9 mph and an apex at 6.5 s, all discarded",
            info: true);

        // Same loop as the row above, printed separately because it is the energy split's other
        // half: the row above reads the height and this one the speed the climb was bought with.
        Row("zoom-climb-min-speed", "same loop, speed at its own minimum", "mph",
            minSpeed / Mph, null, 0.0,
            $"α {alphaAtMinSpeed:0.0}° here (the wings-level pull settles lower, this loop has "
            + "carried well past that regime by its own minimum)",
            info: true);

        // --- stall departure, INFORMATIONAL and deliberately untargeted: it makes the decoded
        // nose-drop's SHAPE visible (an equilibrium the torque settles at, not a chase toward
        // world-down) without asserting any figure, since the only figures available are footage.
        m = Fresh(stats, Level(), 0.9f * fd, 0f);
        float NoseDeg() => Mathf.RadToDeg(Mathf.Asin(Mathf.Clamp((-m.Attitude.Z).Y, -1f, 1f)));
        double tBreak = RunUntil(m, 0f, 120f, () => m.isStalled(), pitch: 0f, watch: watch);
        float breakNoseDeg = NoseDeg(), prevNoseDeg = breakNoseDeg, peakDropDegS = 0f;
        for (float t = 0f; t < 30f; t += EnvDt)
        {
            m.Step(new FlightInput { Throttle = 0f }, EnvDt);
            watch.Sample(m);
            float now = NoseDeg();
            peakDropDegS = Mathf.Max(peakDropDegS, (prevNoseDeg - now) / EnvDt);
            prevNoseDeg = now;
        }
        Row("stall-departure", "0 throttle from 0.9 fd, stick centred, nose where it rests", "deg",
            NoseDeg(), null, 0.0,
            $"broke at {tBreak:0.0} s with the nose {breakNoseDeg:+0.0;-0.0}°, peak drop "
            + $"{peakDropDegS:0.00} °/s, stall flag {m.StallFlag:0.00} at rest, the drop is a torque "
            + "settling at an equilibrium, not a chase; no target, every candidate is footage",
            info: true);

        var sb = new StringBuilder();
        sb.AppendLine($"# flight envelope: {planeNodeName} ({stats.DefName})");
        sb.AppendLine($"# fd_speed {fd:0.#} m/s ({fd / Mph:0.0} mph)  weight {stats.VehWeight:0} kg  "
                      + $"engine power {stats.EnginePower:0.###}  gravity {stats.Gravity:0.#} m/s²");
        sb.AppendLine($"# thrust accel at fd_speed {thrustAccel:0.00} m/s²  stepped at {EnvDt * 1000f:0.0} ms");
        sb.AppendLine("# 'target' is decoded or a named product exception, never footage; a filmed "
                      + "figure that disagrees");
        sb.AppendLine("# is named in the row's own text as discarded and gates nothing "
                      + "(docs/org/flightModel.md, 'Parity ledger')");
        sb.AppendLine("# margins: Gdem peak demand vs the ±5/9 clamp · C_L peak demand over the "
                      + "aerodynamic ceiling (>=1 binds)");
        sb.AppendLine("#   win min opposing-command limiter · V/Vs min speed over stall speed · "
                      + "alt peak · Gup delivered body-up G");
        sb.AppendLine("#   against the authored lowGs/highGs starts · V/Vd peak speed over "
                      + "MaxDiveSpeedFrac x fd_speed");
        sb.AppendLine();
        sb.AppendLine($"{"scenario",-22} {"unit",-5} {"model",10} {"target",10} {"err",8}  verdict");
        foreach (var row in r.Rows)
        {
            string verdict = row.Asserted ? (row.Ok ? "ok" : "!! FAIL") : "(not asserted)";
            string target = row.Target is { } t ? t.ToString("0.00") : "-";
            // The sign is written rather than left to a two-section format: .NET keeps the minus on
            // a negative that ROUNDS to zero and then formats it with the positive section, so a
            // −0.01 % miss printed as "-+0.0%".
            string err = row.ErrorPct is not { } p
                ? "-"
                : $"{(p < 0 ? "-" : "+")}{Math.Abs(p):0.0}%";
            sb.AppendLine($"{row.Name,-22} {row.Unit,-5} {row.Model,10:0.00} "
                          + $"{target,10} {err,8}  {verdict}");
            sb.AppendLine($"{"",-22} {row.What}{(row.Detail.Length > 0 ? $", {row.Detail}" : "")}");
            if (row.Margins.Length > 0)
            {
                sb.AppendLine($"{"",-22} margins: {row.Margins}");
            }
        }
        // The knife-edge hold rides along rather than living as its own flag: it is a SHAPE
        // comparison over 36 s, not a single number with a tolerance, so it has no row here, but
        // every instrument that dumps the envelope should carry it, or the recipe gets lost again.
        sb.AppendLine();
        sb.Append(KnifeEdge(zrdrPath, planeNodeName, watch).Text);
        // Same reasoning as the knife-edge above: a speed-against-time SHAPE rather than one number
        // with a tolerance, and the recipe belongs in code where it cannot be lost.
        sb.AppendLine();
        sb.Append(SustainedClimb(zrdrPath, planeNodeName, watch).Text);
        // The same manoeuvre as the climb above, taken the original's own way instead of the
        // footage's. It says what the original settles at. A disagreeing filmed number is judged
        // against that figure.
        sb.AppendLine();
        sb.Append(TunerEnvelope(zrdrPath, planeNodeName).Text);

        // Branch coverage last, because it is a claim about the whole report above it: which
        // decoded branches these scenarios drove, and which the set leaves untouched.
        r.Reached = watch.Reached.ToList();
        r.Missed = watch.Missed.ToList();
        sb.AppendLine();
        sb.AppendLine($"# branch coverage: {r.Reached.Count} of {EnvelopeMargins.Branches.Length} "
                      + "decoded branches reached by this airframe's scenarios");
        sb.AppendLine($"#   reached: {string.Join(" ", r.Reached)}");
        sb.AppendLine($"#   missed : {string.Join(" ", r.Missed)}");

        r.Text = sb.ToString();
        r.Summary = r.Failed == 0
            ? $"flight envelope: {r.Asserted} scenario(s) asserted against a decoded target, all within tolerance"
            : $"flight envelope: {r.Failed} of {r.Asserted} asserted scenario(s) FAILED, see the !! lines above";
        return r;
    }

    // ---- knife-edge ----------------------------------------------------------------------------

    /// <summary>The knife-edge hold, filmed twice at very different speeds: 90° bank set at entry
    /// then left free, stick neutral, throttle TRIMMED to level (not full) at the entry speed, held
    /// 36 sim s. Reports the nose elevation, flight path, sink, heading rate and α at the original's
    /// own sample times. ⚠ The discriminating signature is the SHAPE, not one number: the original
    /// drifts the whole 36 s with no equilibrium, where a bounded sag settles inside a second, see
    /// <see cref="KnifeEdgeRun.DriftDegS"/>/<see cref="KnifeEdgeRun.SettledFrac"/>, docs/org/flightModel.md.</summary>
    public static KnifeEdgeResult KnifeEdge(string zrdrPath, string planeNodeName,
                                            EnvelopeMargins? watch = null)
    {
        System.Threading.Thread.CurrentThread.CurrentCulture = CultureInfo.InvariantCulture;
        var r = new KnifeEdgeResult();
        PlaneStats stats;
        try
        {
            stats = PlaneStats.Load(zrdrPath, planeNodeName);
        }
        catch (Exception e)
        {
            r.Error = $"could not load plane stats for '{planeNodeName}' ({zrdrPath}): {e.Message}";
            r.Summary = $"knife-edge: {r.Error}";
            return r;
        }

        foreach (float mph in new[] { 143f, 300f })
        {
            r.Runs.Add(KnifeEdgeHold(stats, planeNodeName, 90f, mph, watch));
        }

        var sb = new StringBuilder();
        sb.AppendLine($"# knife-edge hold: {planeNodeName} ({stats.DefName})");
        sb.AppendLine("# 90° bank at entry then FREE; stick neutral; throttle trimmed level at entry speed");
        sb.AppendLine("# original (Bloodhawk, CAP-05, two takes): nose −4.9° / −7.3° at +3 s, then a");
        sb.AppendLine("# drift of 0.69 / 0.89 °/sim-s to −27° at +36 s; nose 4.8–8.3° BELOW the path");
        sb.AppendLine("# (gap growing); sink 0.5 / 24 / 60 / 93 ft/s at +3/+12/+24/+36 s; heading 0.68–1.13 °/s");
        foreach (var run in r.Runs)
        {
            sb.AppendLine();
            sb.AppendLine($"entry {run.EntryMph:0} mph, bank {run.EntryBankDeg:0}°, "
                          + $"throttle {run.Throttle:0.000}, drift {run.DriftDegS:0.00} °/s over 3–36 s, "
                          + $"last-third share {run.SettledFrac:0.00}, "
                          + $"α peak {run.AlphaPeak:0.00}° settles {run.AlphaSettled:0.00}°");
            sb.AppendLine($"  {"t",4} {"nose",8} {"path",8} {"nose-path",10} {"sink",9} {"Δalt",9} "
                          + $"{"α",7} {"bank",7} {"hdg",8} {"speed",8}");
            foreach (var x in run.Samples)
            {
                // ⚠ Do not restore the finer formats; the two hosts' values differ below them and
                // the dump would hash differently either side (INSTR-19, which also carries what
                // the nearest-10-m Δalt costs).
                sb.AppendLine($"  {x.T,4:0} {x.NoseDeg,8:0.0} {x.PathDeg,8:0.0} {x.LagDeg,10:0.0} "
                              + $"{x.SinkFtS,9:0} {Math.Round(x.AltM / 10.0) * 10,9:0} {x.Alpha,7:0.0} "
                              + $"{x.BankDeg,7:0} {x.HeadingRateDegS,8:0.0} {x.SpeedMph,8:0}");
            }
        }

        r.Text = sb.ToString();
        r.Summary = $"knife-edge: {r.Runs.Count} hold(s), drift "
                    + string.Join(" / ", r.Runs.Select(x => $"{x.DriftDegS:0.00}")) + " °/s";
        return r;
    }

    // ---- sustained climb -----------------------------------------------------------------------

    /// <summary>The sustained full-throttle climb, speed against time, the manoeuvre the original
    /// was filmed holding for forty seconds, and the one instrument that separates a
    /// climb-retention term from an attitude-thrust one, since the two predict opposite signs here.
    /// Entry at the footage's own 300 mph, attitude set once (<see cref="Banked"/>'s pitched twin,
    /// not a continuous pull), held 18 sim s, long enough for the plateau, short enough the
    /// altitude clamp cannot bind. Targets and the UNDERSHOOT shape: docs/org/flightModel.md.</summary>
    public static ClimbResult SustainedClimb(string zrdrPath, string planeNodeName,
                                             EnvelopeMargins? watch = null)
    {
        System.Threading.Thread.CurrentThread.CurrentCulture = CultureInfo.InvariantCulture;
        var r = new ClimbResult { Plane = planeNodeName };
        PlaneStats stats;
        try
        {
            stats = PlaneStats.Load(zrdrPath, planeNodeName);
        }
        catch (Exception e)
        {
            r.Error = $"could not load plane stats for '{planeNodeName}' ({zrdrPath}): {e.Message}";
            r.Summary = $"sustained climb: {r.Error}";
            return r;
        }

        const float EntryMph = 300f;
        const float EntryPathDeg = 56.3f;
        r.EntryMph = EntryMph;
        r.EntryPathDeg = EntryPathDeg;

        var m = Fresh(stats, Pitched(EntryPathDeg), EntryMph * Mph, 1f);
        double Nose() => Mathf.RadToDeg(Mathf.Asin(Mathf.Clamp((-m.Attitude.Z).Y, -1f, 1f)));
        double Path() => Mathf.RadToDeg(Mathf.Asin(Mathf.Clamp(m.VelocityDir.Y, -1f, 1f)));

        // ⚠ 18 s, not longer: the fastest climbers reach the 2000 m band edge at ≈21 s, and sampling
        // above it reports a coast in thin air, not the climb. The footage plateaus by +12 s.
        var want = new[] { 0f, 1f, 2f, 3f, 4f, 6f, 8f, 12f, 15f, 18f };
        int next = 0;
        float elapsed = 0f;
        float prevAlt = m.Position.Y;
        r.MinSpeedMph = m.Speed / Mph;
        double speedSum = 0, pathSum = 0;
        int plateau = 0;
        while (next < want.Length)
        {
            if (elapsed >= want[next] - EnvDt * 0.5f)
            {
                r.Samples.Add(new ClimbSample
                {
                    T = want[next],
                    SpeedMph = m.Speed / Mph,
                    PathDeg = Path(),
                    NoseDeg = Nose(),
                    ClimbFpm = (m.Position.Y - prevAlt) / Ft / EnvDt * 60.0,
                    AltFt = m.Position.Y / Ft,
                    Alpha = m.Alpha,
                    ThrustScale = FlightModel.AttitudeThrustScale(m.Attitude.Z.Y),
                });
                next++;
                continue;
            }

            prevAlt = m.Position.Y;
            m.Step(new FlightInput { Throttle = 1f }, EnvDt);
            watch?.Sample(m);
            elapsed += EnvDt;
            if (m.Speed / Mph < r.MinSpeedMph)
            {
                r.MinSpeedMph = m.Speed / Mph;
                r.MinSpeedT = elapsed;
            }

            if (r.BandEdgeAt < 0 && m.Position.Y >= BandEdgeM)
            {
                r.BandEdgeAt = elapsed;
            }

            // The plateau is read over the same last-third window the footage's own is quoted over,
            // so the two numbers are the same statistic and not one average against one endpoint.
            if (elapsed >= 12f)
            {
                speedSum += m.Speed / Mph;
                pathSum += Path();
                plateau++;
            }
        }

        r.PlateauMph = plateau > 0 ? speedSum / plateau : 0;
        r.PlateauPathDeg = plateau > 0 ? pathSum / plateau : 0;

        var sb = new StringBuilder();
        sb.AppendLine($"# sustained climb: {planeNodeName} ({stats.DefName})");
        sb.AppendLine($"# {EntryMph:0} mph entry, path {EntryPathDeg:0.0}° set at entry then FREE, "
                      + "full throttle, stick neutral, 24 sim s");
        sb.AppendLine("# original (Bloodhawk, \"Climp 90° 100% Thrust\"): entry 298.9 mph, path settles");
        sb.AppendLine("# 56.3 ± 3.2°, min 152.4 mph at +6.5 s, then RECOVERS, 163.1 mph over this");
        sb.AppendLine("# probe's own 12–18 s window, still creeping to a flat 167.0 ± 0.5 by +36 s,");
        sb.AppendLine("# climbing ≈12,000 fpm from 900 to 6,300 ft");
        sb.AppendLine($"plateau (12–18 s) {r.PlateauMph:0.00} mph at path {r.PlateauPathDeg:0.0}° "
                      + $"vs original 163.05 mph at 55.5°; minimum {r.MinSpeedMph:0.00} mph at "
                      + $"+{r.MinSpeedT:0.0} s vs original 152.40 at +6.5 s"
                      + (r.BandEdgeAt >= 0
                         ? $"  ⚠ BAND EDGE crossed at +{r.BandEdgeAt:0.0} s, every sample after "
                           + "that reads a coast in thin air, not the climb"
                         : ""));
        sb.AppendLine($"  {"t",4} {"speed",8} {"orig",8} {"path",8} {"nose",8} {"climb",10} "
                      + $"{"alt",9} {"α",7} {"thr×",6}");
        // ⚠ The original column is the BLOODHAWK's, the only airframe the game was filmed flying.
        // Another plane's run reports its own numbers against a dash, which is honest rather than a
        // gap to fill by scaling these.
        bool bhawk = planeNodeName.Equals("player_bhawk", StringComparison.OrdinalIgnoreCase);
        double[] orig = { 298.92, 257.73, 215.77, 185.00, 164.13, 153.42, 154.81, 160.48, 163.08, 164.91 };
        for (int i = 0; i < r.Samples.Count; i++)
        {
            var x = r.Samples[i];
            sb.AppendLine($"  {x.T,4:0} {x.SpeedMph,8:0.00} {(bhawk ? orig[i].ToString("0.00") : "-"),8} {x.PathDeg,8:0.00} "
                          + $"{x.NoseDeg,8:0.00} {x.ClimbFpm,10:0} {x.AltFt,9:0} {x.Alpha,7:0.00} "
                          + $"{x.ThrustScale,6:0.000}");
        }

        r.Text = sb.ToString();
        r.Summary = $"sustained climb: plateau {r.PlateauMph:0.0} mph (original 163.1), "
                    + $"minimum {r.MinSpeedMph:0.0} mph (original 152.4)";
        return r;
    }

    // ---- the shipped tuner's own envelope ------------------------------------------------------

    /// <summary>The climb and dive figures taken the way the original's own Dynamics tuner takes
    /// them (<c>FUN_00491c60</c>). This is the original's answer to what a sustained climb settles
    /// at, not a reading off footage. The rig pins attitude, altitude, rotation and throttle and
    /// leaves speed the only free variable. The climb it reports is therefore an equilibrium of
    /// the force sum and nothing else (docs/org/flightModel.md, "The sustained climb").</summary>
    public static TunerEnvelopeResult TunerEnvelope(string zrdrPath, string planeNodeName)
    {
        System.Threading.Thread.CurrentThread.CurrentCulture = CultureInfo.InvariantCulture;
        var r = new TunerEnvelopeResult { Plane = planeNodeName };
        PlaneStats stats;
        try
        {
            stats = PlaneStats.Load(zrdrPath, planeNodeName);
        }
        catch (Exception e)
        {
            r.Error = $"could not load plane stats for '{planeNodeName}' ({zrdrPath}): {e.Message}";
            r.Summary = $"tuner envelope: {r.Error}";
            return r;
        }

        // The tuner's own four, plus the angle the climb footage settles on, which the tuner never
        // prints. Without it the filmed path would have to be read off an interpolation between
        // the 45° and 90° rows rather than run.
        foreach (float deg in new[] { 90f, 56.3f, 45f, -45f, -90f })
        {
            r.Rows.Add(TunerHold(stats, deg));
        }

        var sb = new StringBuilder();
        sb.AppendLine($"# tuner envelope, {planeNodeName} ({stats.DefName}), the shipped Dynamics "
                      + "tuner's own rig");
        sb.AppendLine("# nose angle, altitude and rotation pinned every step, throttle 1, speed free "
                      + "from rest,");
        sb.AppendLine("# stepped at the tuner's own 100 Hz until the first step that gains no speed");
        sb.AppendLine("# ⚠ the original prints these as its 'Max climb/dive NN degrees' rows, and "
                      + "they are SEA LEVEL");
        sb.AppendLine("# figures: the tuner zeroes the aircraft's world Y for the duration of the "
                      + "force sum");
        foreach (var x in r.Rows)
        {
            sb.AppendLine($"  {x.Name,-24} {x.SpeedMph,8:0.0} MPH  path {x.PathDeg,6:0.0}°  "
                          + $"settled +{x.Seconds,5:0.00} s{(x.Converged ? "" : "  ⚠ ran out of steps")}");
        }

        r.Text = sb.ToString();
        r.Summary = $"tuner envelope: climb 90° {r.At(90f):0.0} mph, climb 45° {r.At(45f):0.0} mph, "
                    + $"dive 90° {r.At(-90f):0.0} mph";
        return r;
    }

    // ---- effects -------------------------------------------------------------------------------

    /// <summary>Play every impact/destruction effect at <paramref name="playPoint"/> and report,
    /// per effect, whether it RESOLVES and whether it BUILDS a puffer. With <paramref name="stage"/>
    /// also reports the MESH half: visible instances, template root, distance from the play point.
    /// See INSTR-11 in docs/verification.md for why both halves are counted independently, sampled
    /// every tick as the peak, and never read as one final sample.</summary>
    public static EffectsResult Effects(AnimRuntime effects, IReadOnlyList<string> effectAnimNames,
        Vector3 playPoint, Node3D? stage, string chapter)
    {
        var r = new EffectsResult { HasStage = stage != null };
        var p = playPoint;
        var sb = new StringBuilder();
        sb.AppendLine($"effects-test: chapter {chapter}, {effectAnimNames.Count} effect name(s), "
                      + $"point ({p.X:0},{p.Y:0},{p.Z:0})");
        if (stage != null)
        {
            // Read off each mesh's own visibility flag, not visible-in-tree: every root is hidden
            // here by construction, so in-tree would read zero for all of them. This is what a
            // revealed root shows if its def touches nothing.
            r.BaseState = MeshCensus.BaseStateOfStage(stage);
            sb.AppendLine($"  {"(stage at rest)",-22} {r.BaseState}");
        }
        var leaked = new SortedSet<string>(StringComparer.Ordinal);
        var revealedDark = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var name in effectAnimNames)
        {
            var row = new EffectRow { Name = name };
            r.Rows.Add(row);
            int before = effects.PuffersBuilt;
            var census = new MeshCensus();
            row.Resolved = effects.PlayEffectAt(name, p);
            // Fixed ticks so the t=0 PUFFER_STATE emits and any one-CallSequence-deep puffer
            // (large_black_smokeball's p1trail) reaches its first batch.
            for (int f = 0; f < 30; f++)
            {
                effects.Advance(1f / 60f);
                if (stage != null)
                    census.Sample(stage, p);
            }

            row.PuffersBuilt = effects.PuffersBuilt - before;
            row.MeshPeaks.AddRange(census.Rows.Where(x => x.Total > 0)
                .OrderBy(x => x.Root, StringComparer.Ordinal));
            int litMeshes = census.Lit;
            string half = row.PuffersBuilt > 0 ? $"puffer[{row.PuffersBuilt}]" : "no puffer";
            string meshHalf = stage == null ? "" : $" mesh[{litMeshes}] {census.Describe()}";
            if (!row.Resolved)
                row.Line = $"  {name,-22} UNRESOLVED, no def bound";
            else if (row.PuffersBuilt > 0 || litMeshes > 0)
                row.Line = $"  {name,-22} {half}{meshHalf} rendered";
            else
                row.Line = $"  {name,-22} started, built no puffer{meshHalf} (light/container effect)";
            sb.AppendLine(row.Line);
            // Full reset before the next name: these effects share puffer names (trailpuffer2) and
            // template roots, so a lingering instance would let the next effect read as "no puffer".
            effects.StopAll();
            if (stage == null)
                continue;
            // One tick past the stop, so a deferred hide lands: what is STILL lit now belongs to an
            // effect that is over, a template left burning at a hit site for the rest of the
            // session, which is the mesh half's other failure mode.
            effects.Advance(1f / 60f);
            var after = new MeshCensus();
            after.Sample(stage, p);
            foreach (var root in after.Rows)
            {
                if (root.Visible > 0)
                {
                    row.Residual.Add(root);
                    leaked.Add($"{root.Root} ({root.Visible} mesh, after {name})");
                }
                else if (root.Revealed && root.Total > 0)
                {
                    row.RevealedDark.Add(root.Root);
                    revealedDark.Add($"{root.Root} (after {name})");
                }
            }
        }

        r.Summary = $"effects-test: {r.Resolved}/{effectAnimNames.Count} resolved, "
                    + $"{r.Puffered} built a puffer, {r.Resolved - r.Puffered} started but built none"
                    + (stage == null ? "" : $"; {r.Meshed} showed template mesh(es)");
        sb.AppendLine(r.Summary);
        if (stage != null)
        {
            sb.AppendLine(leaked.Count == 0
                ? "  no template mesh left lit after its effect was stopped"
                : $"  ⚠ still lit after the stop: {string.Join(", ", leaked)}");
            // A root left REVEALED with every mesh under it off draws nothing, the data's own
            // OBJECT_ACTIVE_STATEs turned its pieces off, so this is a note, not a defect. It is
            // printed because "nothing shows" and "nothing is left revealed" are separate claims.
            if (revealedDark.Count > 0)
                sb.AppendLine($"  (revealed but dark afterwards: {string.Join(", ", revealedDark)})");
        }

        if (effects.UnhandledEventCounts.Count > 0)
        {
            sb.AppendLine("  reasons a start built no puffer: "
                          + string.Join(", ", effects.UnhandledEventCounts
                              .OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key}×{kv.Value}")));
        }
        r.Text = sb.ToString();
        return r;
    }

    // Every mission dir under a chapter, `IA1`/`M0x`/`MP1-3`, sorted so
    // campaign missions list before multiplayer ones. Empty when the chapter is not extracted
    // here. See `analysis/m4-ai-data/aiv_skill_slots.py`'s `*/*/zrdr/aiv.zrd.json` glob
    // for the same discovery done from the shell.
    private static List<string> DiscoverMissions(string dataRoot, string chapter)
    {
        var dir = Path.Combine(dataRoot, "extracted", chapter);
        var missions = new List<string>();
        if (!Directory.Exists(dir))
        {
            return missions;
        }
        foreach (var d in Directory.EnumerateDirectories(dir))
        {
            var name = Path.GetFileName(d);
            if (AiChapterScopeDirs.Contains(name)
                || name.StartsWith("rtexture", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            missions.Add(name);
        }
        missions.Sort(StringComparer.OrdinalIgnoreCase);
        return missions;
    }

    // A mission-scope reader file that is a list of alternating-dict records wrapped in
    // one outer element, the shape `zeppelins.zrd.json` and `egen.zrd.json` both use:
    // `[[record0, record1, …]]` when the mission carries any, bare `[null]` when it
    // ships none. Loosely parsed (a dump probe, not a typed reader, B7/F17/F20 own those).
    private static List<List<object?>> LoadRecordList(string missionZrdrPath, string fileName)
    {
        var root = Zrdr.LoadFile(missionZrdrPath, fileName);
        var records = new List<List<object?>>();
        if (root.Count > 0 && root[0] is List<object?> list)
        {
            foreach (var entry in list)
            {
                if (entry is List<object?> record)
                {
                    records.Add(record);
                }
            }
        }
        return records;
    }

    // One run of the tuner's climb/dive rig. The attitude, the altitude and the body rates are
    // written back before every step. The only state carried from one step to the next is the
    // velocity. That is what makes the reported speed a force-sum equilibrium and not a path.
    private static TunerEnvelopeRow TunerHold(PlaneStats stats, float noseDeg)
    {
        var row = new TunerEnvelopeRow
        {
            Name = Mathf.Abs(noseDeg - 56.3f) < 0.05f
                ? "Climb at the filmed path"
                : noseDeg >= 0f ? $"Max climb {noseDeg:0} degrees" : $"Max dive {-noseDeg:0} degrees",
            NoseDeg = noseDeg,
        };
        var m = new FlightModel(stats);
        var attitude = Pitched(noseDeg);
        var velocity = Vector3.Zero;
        int steps = 0;
        while (steps < TunerEnvelopeSteps)
        {
            m.Reset(Vector3.Zero, attitude, 0f, 1f);
            m.SetVelocity(velocity);
            float before = m.Speed;
            m.Step(new FlightInput { Throttle = 1f }, TunerDt);
            velocity = m.VelocityDir * m.Speed;
            steps++;
            if (m.Speed <= before)
            {
                row.Converged = true;
                break;
            }
        }

        row.SpeedMph = m.Speed / Mph;
        row.PathDeg = Mathf.RadToDeg(Mathf.Asin(Mathf.Clamp(m.VelocityDir.Y, -1f, 1f)));
        row.Seconds = steps * TunerDt;
        return row;
    }

    private static Basis Level() => Basis.Identity;

    // Attitude with the nose `deg`° above the horizon (negative = dive),
    // wings level. Verified by the report's own settled-path readout rather than assumed.
    private static Basis Pitched(float deg) => Basis.Identity.Rotated(Vector3.Right, Mathf.DegToRad(deg));

    // Attitude banked `deg`° about the nose, nose level. Over 90° is past
    // vertical, which is where the original's ADI reads; this is the ENTRY only, the run's own
    // settled bank is reported beside it, because nothing holds this one there.
    private static Basis Banked(float deg) => Basis.Identity.Rotated(Vector3.Forward, Mathf.DegToRad(deg));

    // A model parked at an attitude and speed, with the flight path along the nose,
    // FlightModel.Reset's own convention, so a scenario starts trimmed.
    private static FlightModel Fresh(PlaneStats stats, Basis attitude, float speed, float throttle)
    {
        var m = new FlightModel(stats);
        m.Reset(Vector3.Zero, attitude, speed, throttle);
        return m;
    }

    private static void Run(FlightModel m, float throttle, float seconds,
                            float pitch = 0f, float roll = 0f, float yaw = 0f,
                            EnvelopeMargins? watch = null)
    {
        for (float t = 0f; t < seconds; t += EnvDt)
        {
            m.Step(new FlightInput { Pitch = pitch, Roll = roll, Yaw = yaw, Throttle = throttle }, EnvDt);
            watch?.Sample(m);
        }
    }

    // Steps until `done` or `limit`, returning the
    // elapsed sim seconds (the limit itself if it never finished, a scenario that ran out of time
    // reports as far off rather than as a hang).
    private static double RunUntil(FlightModel m, float throttle, float limit, Func<bool> done,
                                   float pitch = 0f, float roll = 0f, float yaw = 0f,
                                   Action? onStep = null, EnvelopeMargins? watch = null)
    {
        for (float t = 0f; t < limit; t += EnvDt)
        {
            m.Step(new FlightInput { Pitch = pitch, Roll = roll, Yaw = yaw, Throttle = throttle }, EnvDt);
            watch?.Sample(m);
            onStep?.Invoke();
            if (done())
            {
                return t + EnvDt;
            }
        }
        return limit;
    }

    // Full throttle and full back stick from a banked entry, settled for
    // `settle` s and then averaged over `window` s, the shape
    // the original was flown in. Heading is accumulated off the flight path with wrap unfolded, so
    // a turn past 360° reports what it swept rather than what is left over; sink is the window's
    // net altitude change over its own duration, which is the quantity the original's altimeter
    // gave. No roll input: see the call site for why forcing the bank cannot be measured.
    private static (double SpeedMph, double SinkFtS, double RateDegS, double Alpha, double BankDeg,
                    double SweptDeg) SustainedTurn(
        PlaneStats stats, float entryBankDeg, float entrySpeed, float settle, float window,
        EnvelopeMargins? watch = null)
    {
        var m = Fresh(stats, Banked(entryBankDeg), entrySpeed, 1f);
        Run(m, 1f, settle, pitch: 1f, watch: watch);

        double Heading() => Mathf.RadToDeg(Mathf.Atan2(m.VelocityDir.X, -m.VelocityDir.Z));
        float startY = m.Position.Y;
        double prev = Heading(), swept = 0, speedSum = 0, alphaSum = 0, bankSum = 0;
        int samples = 0;
        float elapsed = 0f;
        for (float t = 0f; t < window; t += EnvDt)
        {
            m.Step(new FlightInput { Pitch = 1f, Throttle = 1f }, EnvDt);
            watch?.Sample(m);
            double step = Heading() - prev;
            if (step > 180.0) { step -= 360.0; } else if (step < -180.0) { step += 360.0; }
            swept += step;
            prev += step;
            speedSum += m.Speed;
            alphaSum += m.Alpha;
            bankSum += Mathf.RadToDeg(Mathf.Acos(Mathf.Clamp(m.Attitude.Y.Dot(Vector3.Up), -1f, 1f)));
            samples++;
            elapsed = t + EnvDt;
        }

        return (speedSum / samples / Mph,
                (startY - m.Position.Y) / Ft / elapsed,
                Math.Abs(swept) / elapsed,
                alphaSum / samples,
                bankSum / samples,
                Math.Abs(swept));
    }

    // One knife-edge hold. Sink is read over the second ENDING at each sample, which is
    // what an altimeter needle gives; heading is read off the flight path over the same second and
    // unfolded, so a slow turn is not confused with a wrap.
    private static KnifeEdgeRun KnifeEdgeHold(PlaneStats stats, string plane, float bankDeg,
                                              float entryMph, EnvelopeMargins? watch = null)
    {
        float throttle = TrimThrottle(stats, entryMph * Mph);
        var m = Fresh(stats, Banked(bankDeg), entryMph * Mph, throttle);
        var run = new KnifeEdgeRun
        {
            Plane = plane,
            EntryMph = entryMph,
            EntryBankDeg = bankDeg,
            Throttle = throttle,
        };

        double Nose() => Mathf.RadToDeg(Mathf.Asin(Mathf.Clamp((-m.Attitude.Z).Y, -1f, 1f)));
        double Path() => Mathf.RadToDeg(Mathf.Asin(Mathf.Clamp(m.VelocityDir.Y, -1f, 1f)));
        double Heading() => Mathf.RadToDeg(Mathf.Atan2(m.VelocityDir.X, -m.VelocityDir.Z));

        var want = new[] { 1f, 3f, 12f, 24f, 36f };
        int next = 0;
        float startY = m.Position.Y;
        // A one-second trailing window for the two rates: the altitude a second ago and the heading a
        // second ago, both kept as ring buffers of the fixed step so the read needs no interpolation.
        int win = Mathf.RoundToInt(1f / EnvDt);
        var altRing = new float[win];
        var hdgRing = new double[win];
        for (int i = 0; i < win; i++)
        {
            altRing[i] = m.Position.Y;
            hdgRing[i] = Heading();
        }

        double unfolded = Heading();
        double prevHeading = unfolded;
        int cursor = 0;
        float elapsed = 0f;
        while (elapsed < 36f + EnvDt * 0.5f && next < want.Length)
        {
            m.Step(new FlightInput { Throttle = throttle }, EnvDt);
            watch?.Sample(m);
            elapsed += EnvDt;
            run.AlphaPeak = Math.Max(run.AlphaPeak, m.Alpha);

            double step = Heading() - prevHeading;
            if (step > 180.0) { step -= 360.0; } else if (step < -180.0) { step += 360.0; }
            unfolded += step;
            prevHeading += step;

            float altAgo = altRing[cursor];
            double hdgAgo = hdgRing[cursor];
            altRing[cursor] = m.Position.Y;
            hdgRing[cursor] = unfolded;
            cursor = (cursor + 1) % win;

            if (elapsed >= want[next] - EnvDt * 0.5f)
            {
                run.Samples.Add(new KnifeEdgeSample
                {
                    T = want[next],
                    NoseDeg = Nose(),
                    PathDeg = Path(),
                    LagDeg = Nose() - Path(),
                    SinkFtS = (altAgo - m.Position.Y) / Ft,
                    AltM = m.Position.Y - startY,
                    Alpha = m.Alpha,
                    BankDeg = Mathf.RadToDeg(Mathf.Acos(Mathf.Clamp(m.Attitude.Y.Dot(Vector3.Up), -1f, 1f))),
                    HeadingRateDegS = Math.Abs(unfolded - hdgAgo),
                    SpeedMph = m.Speed / Mph,
                });
                next++;
            }
        }

        // Least squares over the samples from +3 s on, the span the original's own linear fit covers
        // (its first three seconds carry the roll-in transient and are quoted as an intercept).
        var fit = run.Samples.Where(x => x.T >= 3f).ToList();
        if (fit.Count >= 2)
        {
            double tBar = fit.Average(x => x.T), nBar = fit.Average(x => x.NoseDeg);
            double num = fit.Sum(x => (x.T - tBar) * (x.NoseDeg - nBar));
            double den = fit.Sum(x => (x.T - tBar) * (x.T - tBar));
            run.DriftDegS = den > 0 ? -num / den : 0;
        }

        double total = run.Samples.Count > 0 ? run.Samples[0].NoseDeg - run.Samples[^1].NoseDeg : 0;
        double lastThird = run.Samples.Count > 0
            ? run.Samples.First(x => x.T >= 24f).NoseDeg - run.Samples[^1].NoseDeg
            : 0;
        run.SettledFrac = Math.Abs(total) > 1e-6 ? lastThird / total : 0;
        run.AlphaSettled = run.Samples.Count > 0 ? run.Samples[^1].Alpha : 0;
        return run;
    }

    // The lever position that holds `speed` in level flight, bisected on
    // the model itself rather than solved against a copy of the thrust and drag formulas, the
    // copy is what goes stale. Saturates at 1 for a speed the airframe cannot reach, which is the
    // honest answer for it: a run entered above its own top speed decelerates whatever the
    // lever does.
    private static float TrimThrottle(PlaneStats stats, float speed)
    {
        bool Accelerates(float th)
        {
            var m = Fresh(stats, Level(), speed, th);
            Run(m, th, 0.5f);
            return m.Speed > speed;
        }

        if (!Accelerates(1f))
        {
            return 1f;
        }
        float lo = 0f, hi = 1f;
        for (int i = 0; i < 24; i++)
        {
            float mid = 0.5f * (lo + hi);
            if (Accelerates(mid)) { hi = mid; } else { lo = mid; }
        }
        return 0.5f * (lo + hi);
    }

    // One level of a mip chain as a standalone image. Godot stores the chain as one buffer
    // with the levels end to end, so a level is a slice at its own offset.
    private static Image MipLevel(Image img, int level)
    {
        if (level == 0)
        {
            return img.GetMipmapCount() == 0 ? img
                : Image.CreateFromData(img.GetWidth(), img.GetHeight(), false, img.GetFormat(),
                    img.GetData()[..(int)img.GetMipmapOffset(1)]);
        }
        var data = img.GetData();
        int start = (int)img.GetMipmapOffset(level);
        int end = level + 1 <= img.GetMipmapCount() ? (int)img.GetMipmapOffset(level + 1) : data.Length;
        return Image.CreateFromData(Mathf.Max(1, img.GetWidth() >> level),
            Mathf.Max(1, img.GetHeight() >> level), false, img.GetFormat(), data[start..end]);
    }

    // Mean luminance and the share of pixels above 128, formatted as one column pair.
    // Rec.601 luma, the weighting `analysis/item9-depth-bias/CBLOCK-LOD.md` §1b measured with,
    // so the two numbers are comparable to the ones in that file.
    private static string Luma(Image image)
    {
        var (mean, bright) = LumaStats(image);
        return $"{image.GetWidth(),4}x{image.GetHeight(),-4} mean {mean,6:0.00}  px>128 {bright,6:0.000}%";
    }

    private static (double Mean, double Bright) LumaStats(Image image)
    {
        var img = image;
        if (img.GetFormat() != Image.Format.Rgb8 && img.GetFormat() != Image.Format.Rgba8)
        {
            img = (Image)image.Duplicate();
            img.Convert(Image.Format.Rgb8);
        }
        int stride = img.GetFormat() == Image.Format.Rgba8 ? 4 : 3;
        var data = img.GetData();
        double sum = 0;
        int bright = 0, n = 0;
        for (int i = 0; i + stride <= data.Length; i += stride)
        {
            double y = 0.299 * data[i] + 0.587 * data[i + 1] + 0.114 * data[i + 2];
            sum += y;
            if (y > 128.0)
            {
                bright++;
            }
            n++;
        }
        return n == 0 ? (0, 0) : (sum / n, bright * 100.0 / n);
    }

    // Whether an installed level carries the authored artwork. Compared on the two numbers the
    // report prints rather than on bytes: the level is stored in the base's pixel format, so an
    // authored PNG that decoded to another format is converted on the way in and byte equality
    // would fail on a chain that is nonetheless exactly the artwork.
    private static bool SameLuma(Image installed, Image authored)
    {
        var a = LumaStats(installed);
        var b = LumaStats(authored);
        return Math.Abs(a.Mean - b.Mean) < 0.005 && Math.Abs(a.Bright - b.Bright) < 0.0005;
    }

    // Predicate that integrates the body roll rate and trips at a full turn, the rate is
    // what the stopwatch and the video's bank readout both timed, and nothing else is commanded.
    private static Func<bool> RollAccum(FlightModel m)
    {
        double turned = 0;
        return () => (turned += Math.Abs(m.PhysicalBodyRates.Z) * EnvDt) >= Math.Tau;
    }

    private static Func<bool> YawAccum(FlightModel m)
    {
        double turned = 0;
        return () => (turned += Math.Abs(m.PhysicalBodyRates.Y) * EnvDt) >= Math.Tau;
    }

    // ---- shared formatting -------------------------------------------------------------------

    private static string Opt<T>(T? v) where T : struct => v.HasValue ? v.Value.ToString() ?? "-" : "-";

    private static string FmtEffect(WeaponEffect? e)
    {
        if (e == null)
        {
            return "-";
        }
        var parts = new List<string>();
        if (e.Animation != null) { parts.Add($"anim:{e.Animation}"); }
        if (e.SurfaceAnimation != null) { parts.Add($"surf:{e.SurfaceAnimation}"); }
        if (e.Effect != null) { parts.Add($"fx:{e.Effect}"); }
        if (e.Sound != null) { parts.Add($"snd:{e.Sound}"); }
        return "{" + string.Join("/", parts) + "}";
    }

    private static string FmtFlyout(WeaponFlyout? f)
    {
        if (f == null)
        {
            return "-";
        }
        var parts = new List<string>();
        if (f.Model != null) { parts.Add($"model:{f.Model}"); }
        if (f.ModelAnimation != null) { parts.Add($"anim:{f.ModelAnimation}"); }
        if (f.Sound != null) { parts.Add($"snd:{f.Sound}"); }
        return "{" + string.Join("/", parts) + "}";
    }

    // One mesh node's shading inputs, read off the gamez the scene was built from. The model's
    // lighting flag, and per material the mesh uses its texture and that texture's mean texel.
    // Each material also carries the area-weighted mean of the baked vertex colours the surface
    // tool emits.
    private static DebrisShadingRow ReadShadingRow(GameZ gamez, TextureArchive? textures,
        GameZNode gn, float worldLightLinear)
    {
        var mesh = gamez.Meshes[gn.MeshIndex];
        var row = new DebrisShadingRow
        {
            NodeIndex = gn.Index,
            MeshIndex = gn.MeshIndex,
            Lighting = mesh.Lighting,
            Fog = mesh.Fog,
        };

        // Weighted by triangulated area, so a mesh's big flat facets decide the reading rather than
        // a crowd of slivers; the eye reads the facets.
        var areaByMaterial = new Dictionary<int, float>();
        var sumByMaterial = new Dictionary<int, Vector3>();
        var coloredByMaterial = new Dictionary<int, float>();
        foreach (var poly in mesh.Polygons)
        {
            float area = TriangulatedArea(mesh, poly);
            if (area <= 0f)
            {
                continue;
            }
            // White where the polygon carries none, which is what SurfaceTool is given there. A
            // mesh that authored no colours reads as the identity multiply rather than as black.
            var mean = Vector3.One;
            if (poly.VertexColors is { Count: > 0 } vcs)
            {
                var acc = Vector3.Zero;
                foreach (var c in vcs)
                    acc += new Vector3(c.R, c.G, c.B);
                mean = acc / vcs.Count;
                coloredByMaterial.TryGetValue(poly.MaterialIndex, out float ca);
                coloredByMaterial[poly.MaterialIndex] = ca + area;
            }

            areaByMaterial.TryGetValue(poly.MaterialIndex, out float a);
            sumByMaterial.TryGetValue(poly.MaterialIndex, out var s);
            areaByMaterial[poly.MaterialIndex] = a + area;
            sumByMaterial[poly.MaterialIndex] = s + (mean * area);
        }

        foreach (var (mi, area) in areaByMaterial.OrderByDescending(kv => kv.Value))
        {
            var vc = sumByMaterial[mi] / area;
            var mat = mi >= 0 && mi < gamez.Materials.Count ? gamez.Materials[mi] : null;
            var tex = mat?.TextureName != null ? MeanTexel(textures, mat.TextureName) : null;
            var basis = tex ?? new Vector3(mat?.Color.R ?? 1f, mat?.Color.G ?? 1f, mat?.Color.B ?? 1f);
            // The fullbright world shader's own product: linearised vertex colour times the
            // linearised texel times the mission's dimming scalar. It is reported back in sRGB
            // bytes, which is what a screenshot pixel holds.
            var lin = new Vector3(
                Lin(vc.X) * Lin(basis.X), Lin(vc.Y) * Lin(basis.Y), Lin(vc.Z) * Lin(basis.Z));
            if (mesh.Lighting)
            {
                lin *= worldLightLinear;
            }
            coloredByMaterial.TryGetValue(mi, out float colored);
            row.Materials.Add(new DebrisShadingMaterial
            {
                Texture = mat?.TextureName ?? $"flat {Byte3(basis)}",
                Area = area,
                VertexColorCoverage = colored / area,
                VertexColor = vc,
                Texel = basis,
                Drawn = new Vector3(Srgb(lin.X), Srgb(lin.Y), Srgb(lin.Z)),
            });
            if (row.Materials.Count >= 4)
            {
                break;
            }
        }

        return row;
    }

    // What the BUILT material does with those inputs, read off the node's own mesh instance rather
    // than off the data. Whether every surface committed a vertex-colour array, and whether the
    // shader the surface carries reads the mission dimming scalar. The pair is what separates "the
    // data authors this dark" from "the build dropped a term on the way to the screen".
    private static void ReadBuiltMaterial(Node3D node, DebrisShadingRow row)
    {
        foreach (var child in node.GetChildren())
        {
            if (child is not MeshInstance3D { Mesh: ArrayMesh am } mi || mi.Name != "mesh")
            {
                continue;
            }

            row.BuiltSurfaces = am.GetSurfaceCount();
            row.SurfacesWithColors = 0;
            row.SurfacesReadingWorldLight = 0;
            for (int s = 0; s < am.GetSurfaceCount(); s++)
            {
                if ((am.SurfaceGetFormat(s) & Mesh.ArrayFormat.FormatColor) != 0)
                {
                    row.SurfacesWithColors++;
                }

                if (am.SurfaceGetMaterial(s) is ShaderMaterial { Shader: { } sh }
                    && sh.Code.Contains("ALBEDO *= csky_world_light", StringComparison.Ordinal))
                {
                    row.SurfacesReadingWorldLight++;
                }
            }

            return;
        }
    }

    // A polygon's triangulated area, fanned or stripped exactly as SceneBuilder emits it. The
    // weight a material carries here is the screen area that material actually covers.
    private static float TriangulatedArea(GameZMesh mesh, GameZPolygon poly)
    {
        int n = poly.VertexIndices.Count;
        float Tri(int a, int b, int c)
        {
            var va = mesh.Vertices[poly.VertexIndices[a]];
            return 0.5f * (mesh.Vertices[poly.VertexIndices[b]] - va)
                .Cross(mesh.Vertices[poly.VertexIndices[c]] - va).Length();
        }

        float area = 0f;
        for (int i = poly.TriangleStrip ? 0 : 1; i + (poly.TriangleStrip ? 2 : 1) < n; i++)
            area += poly.TriangleStrip ? Tri(i, i + 1, i + 2) : Tri(0, i, i + 1);
        return area;
    }

    private static float Lin(float srgb) => srgb <= 0.04045f
        ? srgb / 12.92f : Mathf.Pow((srgb + 0.055f) / 1.055f, 2.4f);

    private static float Srgb(float linear) => linear <= 0.0031308f
        ? linear * 12.92f : (1.055f * Mathf.Pow(linear, 1f / 2.4f)) - 0.055f;

    private static string Byte3(Vector3 v) =>
        $"({v.X * 255f:0},{v.Y * 255f:0},{v.Z * 255f:0})";

    // The texture's own mean texel, alpha-weighted so a cut-out sheet reports the colour of what it
    // actually draws rather than of its discarded background. Cached: one mesh's materials repeat
    // across a chapter's whole destructible population.
    private static Vector3? MeanTexel(TextureArchive? textures, string textureName)
    {
        if (textures == null)
        {
            return null;
        }

        if (MeanTexelCache.TryGetValue(textureName, out var hit))
        {
            return hit;
        }

        Vector3? mean = null;
        if (textures.FindImage(textureName) is { } img)
        {
            var acc = Vector3.Zero;
            float weight = 0f;
            // A 32-tap grid per axis: the mean of a 128² sheet to within a byte, at a thousandth of
            // the per-texel cost. A probe reads dozens of sheets.
            for (int y = 0; y < 32; y++)
            {
                for (int x = 0; x < 32; x++)
                {
                    var c = img.GetPixel(x * img.GetWidth() / 32, y * img.GetHeight() / 32);
                    acc += new Vector3(c.R, c.G, c.B) * c.A;
                    weight += c.A;
                }
            }
            mean = weight > 0f ? acc / weight : Vector3.Zero;
            img.Dispose();
        }
        MeanTexelCache[textureName] = mean;
        return mean;
    }

    /// <summary>Airframe marker rig: how many of the known player airframes have a rig in
    /// planes.zbd, and which were asked for but not found.</summary>
    public sealed class MarkersResult
    {
        public readonly List<string> Missing = new();
        public string Text = "";
        public string Summary = "";
        public string? Error;
        public int Requested;
        public int Done;
        public bool Ok => Error == null && Requested > 0 && Done == Requested;
    }

    /// <summary>weapons.json read through the typed reader: entry count and any key the reader
    /// does not map.</summary>
    public sealed class WeaponsResult
    {
        public string Text = "";
        public string Summary = "";
        public string? Error;
        public int Shown;
        public int Total;
        public int UnhandledTotal;
        public string? EmptyClipSound;
        public bool Ok => Error == null && Shown > 0 && UnhandledTotal == 0;
    }

    /// <summary>What a chapter's mip chains hold against what its archive ships: how many authored
    /// levels were installed, how many of the shipped levels the installed chain actually carries,
    /// and which ones it does not.</summary>
    public sealed class MipResult
    {
        public readonly List<string> Mismatched = new();
        public readonly List<string> Failures = new();
        public string Text = "";
        public string Summary = "";
        public string? Error;
        /// <summary>Base textures swept, those shipping at least one authored level.</summary>
        public int Textures;
        /// <summary>Authored levels the archive holds for the swept textures.</summary>
        public int LevelsShipped;
        /// <summary>Levels the archive reported installing from authored artwork.</summary>
        public int LevelsInstalled;
        /// <summary>Shipped levels whose installed pixels are the authored ones.</summary>
        public int LevelsMatching;
        public bool Ok => Error == null && Failures.Count == 0 && Textures > 0;
    }

    /// <summary>The five AI data families' install-wide census: nets, <c>aiv</c> vehicle blocks
    /// (plus their field-count histogram, never assume 81-wide), turret entries, zeppelin
    /// records and generators. Unfiltered, these should read 222/414/42/58/23 against the retail
    /// extraction ("What the data actually ships").</summary>
    public sealed class AiDumpResult
    {
        public readonly List<string> Errors = new();
        public readonly SortedDictionary<int, int> AivFieldHistogram = new();
        public string Text = "";
        public string Summary = "";
        public string? Error;
        public int TurretEntries;
        public int NetFiles;
        public int MissionFiles;
        public int AivFiles;
        public int AivBlocks;
        public int ZeppelinRecords;
        public int GeneratorRecords;
        public bool Ok => Error == null && Errors.Count == 0 && NetFiles > 0;
    }

    /// <summary>Stock loadouts bound to their built models: how many bound and every binding
    /// failure's message (a marker that does not resolve, an unknown weapon id).</summary>
    public sealed class LoadoutResult
    {
        public readonly List<string> Failures = new();
        public string Text = "";
        public string Summary = "";
        public string? Error;
        public int Bound;
        public int Failed;
        public bool Ok => Error == null && Bound > 0 && Failed == 0;
    }

    /// <summary>One destructible def's sweep, the report line's <c>✓</c>/<c>✗</c> checks,
    /// as fields.</summary>
    public sealed class DamageRow
    {
        public string Def = "";
        public float MaxHealth;
        public string Source = "";
        public string Activation = "";
        public bool Resolved;          // a deep descendant resolves back to this instance
        public bool Destroyed;
        public int Hits;
        public int StagesFired;
        public int CollidersOff;
        public int CollidersOn;
        public int Debris;
        public int Sounds;
        public bool? ResetHealthy;     // null when the mode never reset (continuous sweep)
        public bool? RekillMatched;
        public bool? CollideAccepted;   // the object took the damage, now true for EVERY activation
        public bool? CollideFlyThrough; // the plane passed through it, the ACTIVATION half
        public string Line = "";
    }

    /// <summary>One material a debris mesh draws with: its sheet, and the triangulated area that
    /// sheet covers on the mesh. The row carries the area-weighted mean baked vertex colour, the
    /// sheet's mean texel, and the sRGB the fullbright world shader lands on from those. All three
    /// colours are 0-1.</summary>
    public sealed class DebrisShadingMaterial
    {
        public string Texture = "";
        public float Area;
        public Vector3 VertexColor;
        public Vector3 Texel;
        public Vector3 Drawn;

        /// <summary>The fraction of this material's area whose polygons author vertex colours at
        /// all. Below 1 the mean above is diluted by the white the colourless polygons take, and a
        /// 0 says the sheet reaches the screen unmodulated.</summary>
        public float VertexColorCoverage;

        public string Text => $"{Texture} area {Area:0} vc {Byte3(VertexColor)} "
            + $"(coverage {VertexColorCoverage:0.##}) texel {Byte3(Texel)} drawn {Byte3(Drawn)}";
    }

    /// <summary>One mesh-bearing node under a destructible, with what shades it and whether the
    /// death flung it.</summary>
    public sealed class DebrisShadingRow
    {
        public readonly List<DebrisShadingMaterial> Materials = new();
        public string Def = "";
        public string CsName = "";
        public int NodeIndex;
        public int MeshIndex;
        public bool Lighting;
        public bool Fog;
        public bool Launched;
        public bool VisibleBefore;
        public bool VisibleAfter;

        /// <summary>Surfaces the node's built <c>ArrayMesh</c> committed, how many of them carry a
        /// vertex-colour array, and how many carry a shader that reads <c>csky_world_light</c>. The
        /// built half of the row: the data above says what was authored, these say what survived
        /// the build.</summary>
        public int BuiltSurfaces;
        public int SurfacesWithColors;
        public int SurfacesReadingWorldLight;

        public string Line => $"{(Launched ? "FLEW" : "    ")} {CsName} (node {NodeIndex}, mesh {MeshIndex}, "
            + $"lighting={Lighting}, fog={Fog}, vis {(VisibleBefore ? "on" : "off")}→{(VisibleAfter ? "on" : "off")}, "
            + $"built {SurfacesWithColors}/{SurfacesReadingWorldLight} of {BuiltSurfaces} surface(s) coloured/dimmed): "
            + (Materials.Count == 0 ? "no drawn polygons"
                : string.Join("; ", Materials.Select(m => m.Text)));
    }

    /// <summary>The debris-shading report the <c>debris-shading</c> suite reads: every mesh node
    /// under the matched destructibles with its shading inputs, and which of them the death
    /// launched.</summary>
    public sealed class DebrisShadingResult
    {
        public readonly List<DebrisShadingRow> Rows = new();
        public string Chapter = "";
        public string Filter = "";
        public string Text = "";
        public string Summary = "";
        public float WorldLight;
        public int Launches;
        public bool Ok => Rows.Count > 0;
    }

    /// <summary>The destructible sweep as a whole: the swept rows plus the uncapped registry
    /// totals (the swept list is capped, a census must read these, not count rows).</summary>
    public sealed class DamageResult
    {
        public readonly List<DamageRow> Rows = new();
        public string Text = "";
        public string Summary = "";
        public string CollidersText = "";
        public int CollidableMeshes;
        public int TotalInstances;
        public int DistinctAnchors;
        public bool Capped;
        public bool Ok => Rows.Count > 0 && Rows.All(r => r.Resolved);
    }

    /// <summary>One flight scenario: what the model does, and the value it is judged against.
    /// <see cref="Target"/> is always decoded or a named product exception, never a figure read off
    /// footage, a disagreeing footage number lives in <see cref="Detail"/> as a discarded
    /// annotation and gates nothing (the parity ledger's rule, docs/org/flightModel.md).
    /// A row with no target, or one flagged <see cref="Informational"/>, is reported and not
    /// asserted: the plant's own number is the finding and there is nothing to compare it to.</summary>
    public sealed class FlightRow
    {
        public string Plane = "";
        public string Name = "";
        public string What = "";
        public string Unit = "";
        public double Model;
        public double? Target;
        public double Tolerance;
        public bool Informational;
        public string Detail = "";

        /// <summary>How far this run stayed from every term that could have bounded it, as
        /// <see cref="EnvelopeMargins"/> formats it. Empty on the extra rows a shared flight
        /// reports, which carry the first row's margins.</summary>
        public string Margins = "";

        public bool Asserted => !Informational && Target != null;
        public bool Ok => !Asserted || Math.Abs(Model - Target!.Value) <= Tolerance;

        /// <summary>Signed miss against the target, as a percentage, the shape that tells a
        /// scale error (constant %) from drift (sign-random).</summary>
        public double? ErrorPct =>
            Target is { } t && t != 0 ? (Model - t) / t * 100.0 : null;
    }

    /// <summary>The flown envelope of one airframe (or of all eleven) against its decoded
    /// targets.</summary>
    public sealed class FlightEnvelopeResult
    {
        public readonly List<FlightRow> Rows = new();

        /// <summary>Per-airframe reached branches, filled only by <see cref="FlightEnvelopeAll"/>:
        /// the coverage half of the parity ledger, so it is read off one whole-envelope run rather
        /// than by flying each airframe a second time.</summary>
        public readonly Dictionary<string, List<string>> ReachedByPlane = new(StringComparer.Ordinal);

        public string Text = "";
        public string Summary = "";
        public string? Error;

        /// <summary>The decoded branches this report's scenarios drove, and the ones they left
        /// untouched, both in <see cref="EnvelopeMargins.Branches"/> order. A missed branch is a
        /// statement about the scenario set, not about whether the branch is ported.</summary>
        public List<string> Reached = new();
        public List<string> Missed = new();

        public int Asserted => Rows.Count(r => r.Asserted);
        public int Failed => Rows.Count(r => !r.Ok);
        public bool Ok => Error == null && Asserted > 0 && Failed == 0;
    }

    /// <summary>One sample of a knife-edge hold, at one of the original's own sample times.
    /// <see cref="LagDeg"/> is nose − path, so it is NEGATIVE while the nose is below the flight
    /// path, which is the whole of a sagging knife-edge.</summary>
    public sealed class KnifeEdgeSample
    {
        public double T;
        public double NoseDeg;
        public double PathDeg;
        public double LagDeg;
        public double SinkFtS;
        public double AltM;
        public double Alpha;
        public double BankDeg;
        public double HeadingRateDegS;
        public double SpeedMph;
    }

    /// <summary>One knife-edge hold: the samples plus the two shape statistics.
    /// <see cref="SettledFrac"/> is the share of the total sag that arrived in the last third of the
    /// hold, ≈0 for a bounded sag that settled early, ≈1/3 for a linear drift that never did.</summary>
    public sealed class KnifeEdgeRun
    {
        public readonly List<KnifeEdgeSample> Samples = new();
        public string Plane = "";
        public double EntryMph;
        public double EntryBankDeg;
        public double Throttle;
        public double DriftDegS;
        public double SettledFrac;
        public double AlphaPeak;
        public double AlphaSettled;
    }

    /// <summary>Both knife-edge holds of one airframe, at the two speeds the original was filmed
    /// at.</summary>
    public sealed class KnifeEdgeResult
    {
        public readonly List<KnifeEdgeRun> Runs = new();
        public string Text = "";
        public string Summary = "";
        public string? Error;
    }

    /// <summary>One sample of the sustained climb, at one of the footage's own elapsed times.</summary>
    public sealed class ClimbSample
    {
        public double T;
        public double SpeedMph;
        public double PathDeg;
        public double NoseDeg;
        public double ClimbFpm;
        public double AltFt;
        public double Alpha;
        public double ThrustScale;
    }

    /// <summary>One sustained full-throttle climb: the samples plus the plateau it settled on.</summary>
    public sealed class ClimbResult
    {
        public readonly List<ClimbSample> Samples = new();
        public string Plane = "";
        public double EntryMph;
        public double EntryPathDeg;
        public double MinSpeedMph;
        public double MinSpeedT;
        public double PlateauMph;
        public double PlateauPathDeg;

        /// <summary>Sim seconds at which the climb crossed the 2000 m band edge, or −1 if it never
        /// did. ⚠ A run that crosses stops being a climb measurement at that instant, above the edge
        /// the thin band leaves neither the lift nor the thrust to climb on, so every sample after it
        /// reads a ballistic coast rather than merely qualifying the climb.</summary>
        public double BandEdgeAt = -1;

        public string Text = "";
        public string Summary = "";
        public string? Error;
    }

    /// <summary>One angle of the tuner's rig: the speed it settles at with the nose held there.
    /// A false <see cref="Converged"/> means the rig hit its iteration cap still gaining speed.
    /// The speed is then a lower bound and not the equilibrium.</summary>
    public sealed class TunerEnvelopeRow
    {
        public string Name = "";
        public double NoseDeg;
        public double SpeedMph;
        public double PathDeg;
        public double Seconds;
        public bool Converged;
    }

    /// <summary>The four angles the original's tuner prints, run through its own rig.</summary>
    public sealed class TunerEnvelopeResult
    {
        public readonly List<TunerEnvelopeRow> Rows = new();
        public string Plane = "";
        public string Text = "";
        public string Summary = "";
        public string? Error;

        /// <summary>The settled speed in mph at a nose angle the rig was run at, 0 if it was not.</summary>
        public double At(double noseDeg) =>
            Rows.FirstOrDefault(x => Math.Abs(x.NoseDeg - noseDeg) < 0.5)?.SpeedMph ?? 0;
    }

    /// <summary>One effect's sweep reading, both halves. <see cref="MeshPeaks"/> holds every
    /// mesh-bearing template root the census saw (peak over the window); <see cref="Residual"/>
    /// what was still lit one tick after the stop, an over effect burning at the hit site;
    /// <see cref="RevealedDark"/> roots left revealed with every mesh under them off, which draw
    /// nothing and are a note, not a defect.</summary>
    public sealed class EffectRow
    {
        public readonly List<MeshCensus.RootPeak> MeshPeaks = new();
        public readonly List<MeshCensus.RootPeak> Residual = new();
        public readonly List<string> RevealedDark = new();
        public string Name = "";
        public bool Resolved;
        public int PuffersBuilt;
        public string Line = "";

        public int LitMeshes => MeshPeaks.Sum(x => x.Visible);
    }

    /// <summary>The effects sweep as a whole: per-effect rows plus the report text the
    /// <c>--effects-test</c> flag prints. The mesh columns exist only when a stage was supplied
    /// (<see cref="HasStage"/>).</summary>
    public sealed class EffectsResult
    {
        public readonly List<EffectRow> Rows = new();
        public string BaseState = "";
        public string Text = "";
        public string Summary = "";
        public bool HasStage;

        public int Resolved => Rows.Count(r => r.Resolved);
        public int Puffered => Rows.Count(r => r.PuffersBuilt > 0);
        public int Meshed => Rows.Count(r => r.LitMeshes > 0);
        public bool Ok => Rows.Count > 0 && Rows.All(r => r.Resolved);
    }

    /// <summary>The mesh half's counting semantics, in one place: which of a template stage's
    /// meshes are drawing, per root, folded to a peak over a window. Counts are visible-IN-TREE,
    /// never <c>Visible</c>, a mesh whose own flag is set under a hidden template root draws
    /// nothing, and that difference IS the bug this census exists to catch. Both the
    /// <see cref="Effects"/> sweep and the <c>effect-template-mesh</c> suite count through this
    /// type, so the sweep's verdicts and the suite's assertions cannot drift apart.</summary>
    public sealed class MeshCensus
    {
        private readonly Dictionary<string, RootPeak> _peak = new(StringComparer.Ordinal);

        public IReadOnlyCollection<RootPeak> Rows => _peak.Values;

        public int Lit => _peak.Values.Sum(r => r.Visible);

        /// <summary>Instantaneous count of drawing meshes under a node, this frame, no folding.</summary>
        public static int VisibleMeshes(Node node)
        {
            int vis = 0, total = 0;
            Count(node, ref vis, ref total);
            return vis;
        }

        /// <summary>Instantaneous count under ONE named template root of a stage. Exact name match,
        /// never a prefix: <c>he_ring</c> and <c>he_ring1</c> are two different staged templates,
        /// and telling them apart is what a per-root reading is for.</summary>
        public static int VisibleMeshesUnder(Node3D stage, string rootName)
        {
            int n = 0;
            foreach (var pool in stage.GetChildren())
                foreach (var root in pool.GetChildren())
                    if (root is Node3D r && r.Name.ToString() == rootName)
                        n += VisibleMeshes(r);
            return n;
        }

        /// <summary>Each mesh-bearing template root's base state: how many of its meshes carry
        /// their own visibility flag, out of how many it has, what a revealed root would show if
        /// its def touched nothing. Read once, before anything plays.</summary>
        public static string BaseStateOfStage(Node3D stage)
        {
            var rows = new SortedDictionary<string, string>(StringComparer.Ordinal);
            foreach (var pool in stage.GetChildren())
            {
                foreach (var child in pool.GetChildren())
                {
                    if (child is not Node3D root)
                        continue;
                    int self = 0, total = 0;
                    CountSelfVisible(root, true, ref self, ref total);
                    if (total > 0)
                        rows[root.Name] = $"{root.Name} {self}/{total} self-visible";
                }
            }

            return rows.Count == 0 ? "no mesh-bearing template root" : string.Join("; ", rows.Values);
        }

        /// <summary>Folds one frame's stage state in, keeping each root's best reading. Distances
        /// are to the play point, so "renders at the call site" and "renders at the stage origin"
        /// (kilometres away in a chapter world) are different readings rather than the same count.
        /// Roots are named, so a row says WHICH template showed, a pooled stage holds several
        /// copies of one name, and they are folded together on purpose: which SLOT a call took is
        /// the pool's business, not this census's.</summary>
        public void Sample(Node3D stage, Vector3 point)
        {
            foreach (var pool in stage.GetChildren())
            {
                foreach (var child in pool.GetChildren())
                {
                    if (child is not Node3D root)
                        continue;
                    string name = root.Name;
                    int vis = 0, total = 0;
                    Count(root, ref vis, ref total);
                    var was = _peak.TryGetValue(name, out var prev)
                        ? prev : new RootPeak(name, 0, 0, 0f, false);
                    if (vis >= was.Visible)
                    {
                        _peak[name] = new RootPeak(name, vis, Math.Max(total, was.Total),
                            root.GlobalPosition.DistanceTo(point), was.Revealed || root.Visible);
                    }
                    else if (root.Visible && !was.Revealed)
                    {
                        _peak[name] = was with { Revealed = true };
                    }
                }
            }
        }

        /// <summary>The per-root reading, printed for every root a run TOUCHED (revealed, or lit a
        /// mesh) that has meshes at all. A revealed root showing none of its own meshes and a root
        /// nobody revealed are different failures and read differently here; a root with no
        /// geometry (most of the puffer hosts) is neither, and is left out.</summary>
        public string Describe()
        {
            var text = _peak.Values.Where(r => r.Total > 0 && (r.Revealed || r.Visible > 0))
                .OrderBy(r => r.Root, StringComparer.Ordinal)
                .Select(r => $"{r.Root} {r.Visible}/{r.Total} "
                             + $"@{r.Distance.ToString("0.0", CultureInfo.InvariantCulture)} m");
            return string.Join("; ", text);
        }

        private static void Count(Node node, ref int visible, ref int total)
        {
            if (node is MeshInstance3D { Mesh: not null } mi && mi.Mesh.GetSurfaceCount() > 0)
            {
                total++;
                if (mi.IsVisibleInTree())
                    visible++;
            }

            foreach (var child in node.GetChildren())
            {
                Count(child, ref visible, ref total);
            }
        }

        // Counts meshes that would draw if the template ROOT were revealed, the root's
        // own flag is skipped and every flag below it honoured, since the root's is the engine's
        // to set (`TemplateStage.Shown`) and everything under it is the data's.
        private static void CountSelfVisible(Node node, bool shown, ref int selfVisible,
            ref int total)
        {
            if (node is MeshInstance3D { Mesh: not null } mi && mi.Mesh.GetSurfaceCount() > 0)
            {
                total++;
                if (shown)
                    selfVisible++;
            }

            foreach (var child in node.GetChildren())
            {
                // A hidden branch still contributes its TOTAL, "0 of 8" and "0 of 0" are different
                // answers, so the walk continues rather than stopping at the first hidden node.
                CountSelfVisible(child, shown && (child is not Node3D c || c.Visible),
                    ref selfVisible, ref total);
            }
        }

        /// <summary>The high-water mark of one template root's mesh half: how many of its meshes
        /// were visible-in-tree at once, out of how many it carries, how far the root sat from the
        /// play point when it peaked, and whether the run ever revealed it.</summary>
        public readonly record struct RootPeak(string Root, int Visible, int Total, float Distance,
            bool Revealed);
    }
}
