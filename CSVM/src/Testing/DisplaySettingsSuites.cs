using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using CSVM.UI.Menu;
using CSVM.UI.Menu.Original;
using CSVM.Utils;
using Godot;

namespace CSVM.Testing;

/// <summary>The display settings between the VIDEO page and the engine: the precedence the sources
/// resolve in, what the engine reads back once a choice is applied, and the <c>--det</c> drop that
/// keeps a saved one out of a golden. The page is driven as a bare <see cref="OriginalShell"/> over
/// the install's decoded layout, the question here being the setting rather than the presentation
/// boundary <see cref="MenuOriginalSuites"/> drives.
/// ⚠ The store is pointed at each suite's own scratch directory and the window's pacing is put
/// back in a finally, so nothing here reads the options saved at this machine's controls or leaves
/// the rest of the run on a frame cap. The window's mode and size are read and never set: a
/// scripted run's window is hidden off screen, putting it back would land it over whatever the
/// machine is doing, and a resize would move the viewport every later shot in the process is
/// captured from. The screen is applied only where it is the one the window already stands on,
/// which logs the line a run owes and moves nothing.</summary>
internal static class DisplaySettingsSuites
{
    // The size the custom-entry checks write into the options file: the original game's own frame,
    // absent from the standard table and small enough that no screen this runs on can fail to hold
    // it, so the check reads the same on the hidden test desktop and at the controls.
    private const string CustomSize = "640x480";

    // Where the size row stands on the built-in Options screen. Seven rows stand over it:
    // difficulty, the opening view, the automatic head turn, the targeting switch, the rumble
    // toggle, the graphics mode and the monitor.
    private const int BuiltInResolutionRow = 7;

    // Every field OptionsDef carries, with a value the store validates and whether it is a display
    // setting, which is what makes it something no deterministic run may read. The list is compared
    // against the def by reflection, so a field added there and not here fails display-det-guard.
    // The sample is typed as object rather than as string because a volume level is an int? and the
    // targeting switch a bool?: the table names what the store must keep, not a vocabulary. The
    // Master sample is 0 on purpose, the mute a plain int could not tell from "never set".
    private static readonly (string Property, object Sample, bool Display)[] SavedFields =
    {
        ("MenuPresentation", "original", false),
        ("GraphicsMode", GraphicsMode.EnhancedWord, false),
        ("Difficulty", Flight.Hangar.Difficulty.Word(Flight.Hangar.Difficulty.Hard), false),
        ("NearestAfterKill", true, false),
        ("Rumble", false, false),
        ("DefaultView", "cockpit", false),
        ("AutoHeadTurn", true, false),
        ("RocketCraters", true, false),
        ("MonitorIndex", "3", true),
        ("Resolution", "1920x1080", true),
        ("DisplayMode", DisplayWords.Borderless, true),
        ("VSync", "144", true),
        ("AudioMaster", 0, false),
        ("AudioMusic", 100, false),
        ("AudioEffects", 50, false),
        ("AudioVoice", 25, false),
    };

    [Suite("display-vsync",
        "The V-Sync setting: --no-vsync beats a saved cap, the saved word beats the display.vsync "
        + "config key, the key beats the default and the default is V-Sync off, a word the vocabulary "
        + "does not know reads as never set, a --det launch reads no saved word while a plain one "
        + "does, the VIDEO page opens on the saved word, and applying what its ACCEPT CHANGES "
        + "carried leaves Engine.MaxFps and DisplayServer.WindowGetVsyncMode reading back the cap")]
    internal static void DisplayVSync(TestContext ctx)
    {
        var flag = VSyncSetting.Resolve(flagOff: true, savedWord: "144", configOn: true);
        ctx.Check(!flag.Enabled && flag.MaxFps == VSyncSetting.Uncapped && flag.Source == "--no-vsync",
            $"--no-vsync beats a saved 144 cap and leaves the loop uncapped ({Describe(flag)})");
        var savedCap = VSyncSetting.Resolve(false, "144", true);
        ctx.Check(!savedCap.Enabled && savedCap.MaxFps == 144 && savedCap.Source == "options.json",
            $"the saved cap is V-Sync off at that rate ({Describe(savedCap)})");
        var savedOff = VSyncSetting.Resolve(false, DisplayWords.VSyncOff, true);
        ctx.Check(!savedOff.Enabled && savedOff.Source == "options.json",
            $"and a saved off beats a config key that says on ({Describe(savedOff)})");
        var key = VSyncSetting.Resolve(false, null, true);
        ctx.Check(key.Enabled && key.MaxFps == VSyncSetting.Uncapped && key.Source == VSyncSetting.Key,
            $"with nothing saved a config key that says on decides ({Describe(key)})");
        var fallback = VSyncSetting.Resolve(false, null, VSyncSetting.ConfigDefault);
        ctx.Check(!fallback.Enabled && fallback.MaxFps == VSyncSetting.Uncapped && fallback.Source == "default",
            $"and with neither, V-Sync is off and uncapped, the behaviour with no options file ({Describe(fallback)})");
        ctx.Check(VSyncSetting.TryParseWord(VSyncSetting.Default, out bool defaultOn, out int defaultCap)
            && defaultOn == fallback.Enabled && defaultCap == fallback.MaxFps,
            $"which is the pacing the default word the VIDEO page shows spells ({VSyncSetting.Default})");
        var unknown = VSyncSetting.Resolve(false, "sometimes", VSyncSetting.ConfigDefault);
        ctx.Check(!unknown.Enabled && unknown.Source == fallback.Source,
            $"a word the vocabulary does not know reads as never set rather than as a choice ({Describe(unknown)})");

        ctx.RequireData(MenuLayout.PathUnder(ctx.DataRoot), $"decoded menu layout");
        var layout = OriginalAvailability.Load(ctx.DataRoot, out var why);
        ctx.Check(layout != null, $"the install's layout passes the availability check ({why ?? "ok"})");
        if (layout == null)
        {
            return;
        }

        string dir = Path.Combine(ctx.ScratchDir, "display-vsync");
        if (Directory.Exists(dir))
        {
            Directory.Delete(dir, recursive: true);
        }

        Directory.CreateDirectory(dir);
        string? previous = OptionsStore.DirectoryOverride;
        OptionsStore.DirectoryOverride = dir;
        try
        {
            OptionsStore.UserOptions().Save(new OptionsDef { VSync = "144" });
            ctx.Check(VSyncSetting.SavedWord(det: false) == "144",
                $"a plain launch reads the saved word ({VSyncSetting.SavedWord(det: false) ?? "unset"})");
            ctx.Check(VSyncSetting.SavedWord(det: true) == null,
                $"and a --det launch reads no saved display setting at all ({VSyncSetting.SavedWord(det: true) ?? "unset"})");
            AppliedRow(ctx, layout);
        }
        finally
        {
            OptionsStore.DirectoryOverride = previous;
        }
    }

    [Suite("display-mode",
        "The display-mode setting: the saved word beats the default and the default is borderless, "
        + "the window filling the screen, a word the vocabulary does not know reads as never set, "
        + "borderless and exclusive fullscreen resolve to the two engine modes Godot spells the "
        + "other way round, a scripted run keeps project.godot's windowed window since the setting "
        + "never reaches it, a --det launch reads no saved word while a plain one does, and the "
        + "VIDEO page opens on the saved word and carries it on what ACCEPT CHANGES applies")]
    internal static void DisplayMode(TestContext ctx)
    {
        var saved = DisplayModeSetting.Resolve(DisplayWords.Fullscreen);
        ctx.Check(saved.Mode == DisplayServer.WindowMode.ExclusiveFullscreen && saved.Source == "options.json",
            $"the saved fullscreen word is the exclusive mode ({saved.Mode}, {saved.Source})");
        var windowed = DisplayModeSetting.Resolve(DisplayWords.Windowed);
        ctx.Check(windowed.Mode == DisplayServer.WindowMode.Windowed && windowed.Source == "options.json",
            $"the saved windowed word is the bordered window ({windowed.Mode}, {windowed.Source})");
        var none = DisplayModeSetting.Resolve(null);
        ctx.Check(none.Mode == DisplayServer.WindowMode.Fullscreen && none.Word == DisplayWords.Borderless
            && none.Source == "default",
            $"with nothing saved the mode is borderless, Godot's Fullscreen, the window that fills the screen ({none.Mode}, {none.Source})");
        ctx.Check(DisplayModeSetting.Resolve(DisplayWords.Borderless).Mode == none.Mode,
            $"which is the mode the saved borderless word resolves to as well");
        var unknown = DisplayModeSetting.Resolve("maximized");
        ctx.Check(unknown.Mode == none.Mode && unknown.Source == none.Source,
            $"a word the vocabulary does not know reads as never set ({unknown.Mode}, {unknown.Source})");
        // The run's own window is the control on the guard: a --run-tests process is scripted, so the
        // default above never reaches it and it stands in project.godot's windowed window. The mode is
        // never changed here, a scripted run's window being hidden off screen for the other suites.
        ctx.Check(DisplayServer.WindowGetMode() == DisplayServer.WindowMode.Windowed,
            $"and this scripted run's own window is still project.godot's windowed one ({DisplayServer.WindowGetMode()})");

        ctx.RequireData(MenuLayout.PathUnder(ctx.DataRoot), $"decoded menu layout");
        var layout = OriginalAvailability.Load(ctx.DataRoot, out var why);
        ctx.Check(layout != null, $"the install's layout passes the availability check ({why ?? "ok"})");
        if (layout == null)
        {
            return;
        }

        string dir = Path.Combine(ctx.ScratchDir, "display-mode");
        if (Directory.Exists(dir))
        {
            Directory.Delete(dir, recursive: true);
        }

        Directory.CreateDirectory(dir);
        string? previous = OptionsStore.DirectoryOverride;
        OptionsStore.DirectoryOverride = dir;
        try
        {
            OptionsStore.UserOptions().Save(new OptionsDef { DisplayMode = DisplayWords.Borderless });
            ctx.Check(DisplayModeSetting.SavedWord(det: false) == DisplayWords.Borderless,
                $"a plain launch reads the saved word ({DisplayModeSetting.SavedWord(det: false) ?? "unset"})");
            ctx.Check(DisplayModeSetting.SavedWord(det: true) == null,
                $"and a --det launch reads no saved display setting at all ({DisplayModeSetting.SavedWord(det: true) ?? "unset"})");
            AppliedModeRow(ctx, layout);
        }
        finally
        {
            OptionsStore.DirectoryOverride = previous;
        }
    }

    [Suite("display-resolution",
        "The resolution setting: the list a screen offers holds that screen's own size as its "
        + "fallback, a 4:3 ladder of four rungs and no size larger than the screen, a saved size the "
        + "list offers beats the fallback, a saved size it does not offer and nothing saved both "
        + "fall back to the screen's "
        + "own size rather than to the nearest or to project.godot's, a size the file names by hand "
        + "stands in the list where it sorts and is what both presentations' size rows draw, step "
        + "off and back onto, and save back unchanged, windowed and exclusive "
        + "fullscreen both take the saved size while borderless pins it to the screen's own, a --det "
        + "launch reads no saved size while a plain one does, and the VIDEO page opens on the saved "
        + "size and carries it on what ACCEPT CHANGES applies, or draws it dead at the screen's own "
        + "size under borderless with the saved one still riding out")]
    internal static void DisplayResolution(TestContext ctx)
    {
        var offered = ResolutionSetting.SizesUnder(1920, 1080);
        ctx.Check(offered.Words.Contains("1920x1080") && offered.Fallback == "1920x1080"
            && offered.Words.Contains(ResolutionSetting.ProjectSize) && !offered.Words.Contains("2560x1440"),
            $"a 1920x1080 screen offers its own size as the fallback and the sizes under it, nothing larger ({string.Join(" ", offered.Words)})");
        ctx.Check(offered.Words.Contains("800x600") && offered.Words.Contains("1024x768")
            && offered.Words.Contains("1280x960") && !offered.Words.Contains("1600x1200"),
            $"with the 4:3 rungs it can hold among them and the one it cannot left out ({string.Join(" ", offered.Words)})");
        var byHand = offered.Including("1152x864");
        ctx.Check(byHand.Words.Count == offered.Words.Count + 1 && byHand.Words[2] == "1152x864"
            && offered.Words.SequenceEqual(offered.Including("2560x1440").Words),
            $"a size the file names by hand stands where it sorts, one the screen cannot hold not at all ({string.Join(" ", byHand.Words)})");
        var custom = ResolutionSetting.Resolve("1152x864", offered, DisplayWords.Windowed);
        ctx.Check(custom is { Width: 1152, Height: 864, Source: "options.json" },
            $"and it is the size the apply takes, not the nearest listed one ({Describe(custom)})");
        var narrow = ResolutionSetting.SizesUnder(800, 600);
        ctx.Check(narrow.Words.Contains("800x600") && narrow.Fallback == "800x600"
            && !narrow.Words.Contains(ResolutionSetting.ProjectSize),
            $"a screen smaller than project.godot's window offers its own size and not that window ({string.Join(" ", narrow.Words)})");
        var saved = ResolutionSetting.Resolve("1920x1080", offered, DisplayWords.Windowed);
        ctx.Check(saved is { Width: 1920, Height: 1080, Source: "options.json" },
            $"a saved size the screen offers is the one a windowed launch applies ({Describe(saved)})");
        var exclusive = ResolutionSetting.Resolve("1024x768", offered, DisplayWords.Fullscreen);
        ctx.Check(exclusive is { Width: 1024, Height: 768, Source: "options.json" },
            $"and the one an exclusive-fullscreen launch applies, that mode taking the chosen size ({Describe(exclusive)})");
        var pinned = ResolutionSetting.Resolve("1024x768", offered, DisplayWords.Borderless);
        ctx.Check(pinned is { Width: 1920, Height: 1080, Source: DisplayWords.Borderless },
            $"while borderless pins it to the screen's own size whatever is saved ({Describe(pinned)})");
        ctx.Check(ResolutionSetting.Pinned(null) && ResolutionSetting.Pinned("maximized")
            && !ResolutionSetting.Pinned(DisplayWords.Windowed) && !ResolutionSetting.Pinned(DisplayWords.Fullscreen),
            $"as does a file naming no mode at all, or a word the vocabulary does not know");
        var unsupported = ResolutionSetting.Resolve("2560x1440", offered, DisplayWords.Windowed);
        ctx.Check(unsupported is { Width: 1920, Height: 1080, Source: "default" },
            $"a saved size it does not offer falls back to the screen's own size ({Describe(unsupported)})");
        var none = ResolutionSetting.Resolve(null, offered, DisplayWords.Windowed);
        ctx.Check(none.Word == offered.Fallback && none.Source == unsupported.Source,
            $"and nothing saved reads the same way ({Describe(none)})");
        var blind = ResolutionSetting.Resolve(null, ResolutionSetting.Unknown, DisplayWords.Windowed);
        ctx.Check(blind is { Width: ResolutionSetting.ProjectWidth, Height: ResolutionSetting.ProjectHeight, Source: "default" },
            $"while a list built with no screen to ask falls back to project.godot's window ({Describe(blind)})");

        // This run's own screen is the control on the enumeration: every size the list offers has to
        // fit inside what the engine reports, and the screen's own size has to be its fallback.
        var screen = DisplayServer.ScreenGetSize(DisplayServer.WindowGetCurrentScreen());
        var here = ResolutionSetting.ScreenSizes();
        bool fits = true;
        foreach (var word in here.Words)
        {
            fits &= OptionsStore.TryParseResolution(word, out int w, out int h) && w <= screen.X && h <= screen.Y;
        }

        ctx.Check(fits && here.Words.Contains(here.Fallback) && here.Fallback == OptionsStore.FormatResolution(screen.X, screen.Y),
            $"this screen's list fits inside the {screen.X}x{screen.Y} the engine reports and falls back to it ({string.Join(" ", here.Words)})");

        ctx.RequireData(MenuLayout.PathUnder(ctx.DataRoot), $"decoded menu layout");
        ctx.RequireData(ctx.ZrdrPath, $"zrdr archive");
        var layout = OriginalAvailability.Load(ctx.DataRoot, out var why);
        ctx.Check(layout != null, $"the install's layout passes the availability check ({why ?? "ok"})");
        if (layout == null)
        {
            return;
        }

        string dir = Path.Combine(ctx.ScratchDir, "display-resolution");
        if (Directory.Exists(dir))
        {
            Directory.Delete(dir, recursive: true);
        }

        Directory.CreateDirectory(dir);
        string? previous = OptionsStore.DirectoryOverride;
        OptionsStore.DirectoryOverride = dir;
        try
        {
            OptionsStore.UserOptions().Save(new OptionsDef { Resolution = "1024x768", DisplayMode = DisplayWords.Windowed });
            ctx.Check(ResolutionSetting.SavedWord(det: false) == "1024x768",
                $"a plain launch reads the saved size ({ResolutionSetting.SavedWord(det: false) ?? "unset"})");
            ctx.Check(ResolutionSetting.SavedWord(det: true) == null,
                $"and a --det launch reads no saved display setting at all ({ResolutionSetting.SavedWord(det: true) ?? "unset"})");
            AppliedSizeRow(ctx, layout);
            OptionsStore.UserOptions().Save(new OptionsDef { Resolution = "1024x768", DisplayMode = DisplayWords.Borderless });
            PinnedSizeRow(ctx, layout);
            OptionsStore.UserOptions().Save(new OptionsDef { Resolution = CustomSize, DisplayMode = DisplayWords.Windowed });
            CustomSizeRow(ctx, layout);
            BuiltInCustomSizeRow(ctx);
        }
        finally
        {
            OptionsStore.DirectoryOverride = previous;
        }
    }

    [Suite("display-monitor",
        "The monitor setting: a saved index the machine has a screen for beats the fallback, an "
        + "index no screen answers to falls back to the screen the window stands on rather than "
        + "reaching the engine, a screen reads as its index and its size, this machine's own "
        + "enumeration matches what the engine reports, a --det launch reads no saved index while a "
        + "plain one does, and the VIDEO page opens on the saved screen and carries it on the apply")]
    internal static void DisplayMonitor(TestContext ctx)
    {
        // Three screens with the second as the fallback. The development machine has one, so the
        // list a multi-monitor machine would enumerate is built by hand: what it proves is the
        // resolve rule, and the move itself is owed at the controls on a machine with two.
        var many = new ScreenList(
            new[] { MonitorSetting.Label(0, 1920, 1080), MonitorSetting.Label(1, 2560, 1440), MonitorSetting.Label(2, 1280, 1024) }, 1);
        ctx.Check(many.Labels[1] == "Screen 1 (2560x1440)",
            $"a screen reads as the index it is saved as and the size the engine reports ({many.Labels[1]})");
        var saved = MonitorSetting.Resolve("2", many);
        ctx.Check(saved is { Screen: 2, Word: "2", Source: "options.json" },
            $"a saved index the machine has a screen for is the one applied ({Describe(saved)})");
        var absent = MonitorSetting.Resolve("7", many);
        ctx.Check(absent is { Screen: 1, Word: "1", Source: "default" },
            $"an index past the last screen falls back to the screen the window stands on ({Describe(absent)})");
        var none = MonitorSetting.Resolve(null, many);
        ctx.Check(none.Screen == absent.Screen && none.Source == absent.Source,
            $"and nothing saved reads the same way ({Describe(none)})");
        var malformed = MonitorSetting.Resolve("01", many);
        ctx.Check(malformed.Screen == absent.Screen,
            $"as does an index that is not the canonical spelling of one ({Describe(malformed)})");

        // This run's own screens are the control on the enumeration: one label per screen the engine
        // counts, and a fallback that names one of them.
        var here = MonitorSetting.Screens();
        ctx.Check(here.Labels.Count == DisplayServer.GetScreenCount() && here.Fallback >= 0
            && here.Fallback < here.Labels.Count,
            $"this machine enumerates one label per screen with the window on {here.Fallback} ({string.Join(" ", here.Labels)})");
        int standing = DisplayServer.WindowGetCurrentScreen();
        MonitorSetting.Apply(MonitorSetting.Resolve(MonitorSetting.Word(standing), here));
        ctx.Check(DisplayServer.WindowGetCurrentScreen() == standing,
            $"and applying the screen it already stands on logs the line without moving it (screen {standing})");

        ctx.RequireData(MenuLayout.PathUnder(ctx.DataRoot), $"decoded menu layout");
        var layout = OriginalAvailability.Load(ctx.DataRoot, out var why);
        ctx.Check(layout != null, $"the install's layout passes the availability check ({why ?? "ok"})");
        if (layout == null)
        {
            return;
        }

        string dir = Path.Combine(ctx.ScratchDir, "display-monitor");
        if (Directory.Exists(dir))
        {
            Directory.Delete(dir, recursive: true);
        }

        Directory.CreateDirectory(dir);
        string? previous = OptionsStore.DirectoryOverride;
        OptionsStore.DirectoryOverride = dir;
        try
        {
            OptionsStore.UserOptions().Save(new OptionsDef { MonitorIndex = MonitorSetting.Word(standing) });
            ctx.Check(MonitorSetting.SavedWord(det: false) == MonitorSetting.Word(standing),
                $"a plain launch reads the saved index ({MonitorSetting.SavedWord(det: false) ?? "unset"})");
            ctx.Check(MonitorSetting.SavedWord(det: true) == null,
                $"and a --det launch reads no saved display setting at all ({MonitorSetting.SavedWord(det: true) ?? "unset"})");
            AppliedMonitorRow(ctx, layout, standing);
        }
        finally
        {
            OptionsStore.DirectoryOverride = previous;
        }
    }

    [Suite("display-det-guard",
        "The --det drop as a guard rather than four assertions: every field OptionsDef carries is "
        + "named in this suite's own table, every reader of a saved display setting has the shape "
        + "SavedWord(det) and there are no more of them than there are display settings, each one "
        + "returns its field on a plain launch and null under --det, and no other field is read "
        + "through one. A display setting added to the def without the drop fails here rather than "
        + "in the goldens, which cannot see the drop fail while Launcher's scripted-run guard "
        + "stands in front of it (docs/verification.md's DET-14)")]
    internal static void DisplayDetGuard(TestContext ctx)
    {
        var carried = typeof(OptionsDef).GetProperties(BindingFlags.Public | BindingFlags.Instance);
        var known = new HashSet<string>(SavedFields.Select(f => f.Property), StringComparer.Ordinal);
        var actual = new HashSet<string>(carried.Select(p => p.Name), StringComparer.Ordinal);
        ctx.Check(known.SetEquals(actual),
            $"every field the options def carries is named in this suite's table (unnamed: {Join(actual.Except(known))}; gone: {Join(known.Except(actual))})");

        var readers = SavedWordReaders();
        int displays = SavedFields.Count(f => f.Display);
        ctx.Check(readers.Count == displays,
            $"and the Utils settings offer one SavedWord reader per display setting, {displays} of them ({Join(readers.Select(r => r.Owner))})");

        string dir = Path.Combine(ctx.ScratchDir, "display-det-guard");
        if (Directory.Exists(dir))
        {
            Directory.Delete(dir, recursive: true);
        }

        Directory.CreateDirectory(dir);
        string? previous = OptionsStore.DirectoryOverride;
        OptionsStore.DirectoryOverride = dir;
        try
        {
            foreach (var field in SavedFields)
            {
                OneField(ctx, carried, readers, field);
            }

            EveryField(ctx, carried, readers);
        }
        finally
        {
            OptionsStore.DirectoryOverride = previous;
        }
    }

    // One field saved alone, so which reader answers for it is measured rather than assumed. The
    // store keeping the sample is the control: a sample its validation dropped would leave every
    // reader silent and read as a guard holding (docs/verification.md's METHOD-10).
    private static void OneField(TestContext ctx, PropertyInfo[] carried,
        IReadOnlyList<(string Owner, Func<bool, string?> Read)> readers, (string Property, object Sample, bool Display) field)
    {
        var property = carried.First(p => p.Name == field.Property);
        var def = new OptionsDef();
        property.SetValue(def, field.Sample);
        OptionsStore.UserOptions().Save(def);
        // Equals rather than a string compare, so the check reads a level back as the number it
        // was saved as: an int? that came back null would otherwise pass as "two nulls match".
        ctx.Check(Equals(property.GetValue(OptionsStore.UserOptions().Load()), field.Sample),
            $"the store keeps a {field.Property} of '{field.Sample}', so a silent reader below means a guard and not a dropped value");

        var plain = readers.Where(r => r.Read(false) != null).Select(r => r.Owner).ToList();
        var det = readers.Where(r => r.Read(true) != null).Select(r => r.Owner).ToList();
        if (field.Display)
        {
            ctx.Check(plain.Count == 1 && det.Count == 0,
                $"{field.Property} is read by exactly one setting on a plain launch and by none under --det (plain: {Join(plain)}; det: {Join(det)})");
        }
        else
        {
            ctx.Check(plain.Count == 0,
                $"{field.Property} is no display setting's to read, so its own guard is elsewhere (plain: {Join(plain)})");
        }
    }

    // Every field set at once, the shape of a file written at the controls: what a golden shot's
    // --det run reads out of it has to be nothing at all, while a plain launch reads every one.
    private static void EveryField(TestContext ctx, PropertyInfo[] carried,
        IReadOnlyList<(string Owner, Func<bool, string?> Read)> readers)
    {
        var def = new OptionsDef();
        foreach (var field in SavedFields)
        {
            carried.First(p => p.Name == field.Property).SetValue(def, field.Sample);
        }

        OptionsStore.UserOptions().Save(def);
        var det = readers.Where(r => r.Read(true) != null).Select(r => r.Owner).ToList();
        ctx.Check(det.Count == 0,
            $"a file carrying every option reaches no display setting under --det ({Join(det)})");
        var plain = readers.Where(r => r.Read(false) != null).Select(r => r.Owner).ToList();
        ctx.Check(plain.Count == readers.Count,
            $"while a plain launch reads all {readers.Count} of them, so the drop above is the guard and not an empty file ({Join(plain)})");
    }

    // The saved-display readers, found by shape rather than by name: a public static string?
    // SavedWord(bool) on a Utils type. Reflection is the point. A display setting added later is
    // measured by this suite the moment it takes that shape, and named as missing when it does not.
    private static IReadOnlyList<(string Owner, Func<bool, string?> Read)> SavedWordReaders()
    {
        var found = new List<(string Owner, Func<bool, string?> Read)>();
        foreach (var type in typeof(OptionsStore).Assembly.GetTypes())
        {
            if (type.Namespace != typeof(OptionsStore).Namespace)
            {
                continue;
            }

            var method = type.GetMethod("SavedWord", BindingFlags.Public | BindingFlags.Static,
                null, new[] { typeof(bool) }, null);
            if (method != null && method.ReturnType == typeof(string))
            {
                found.Add((type.Name, det => (string?)method.Invoke(null, new object[] { det })));
            }
        }

        found.Sort(static (a, b) => string.CompareOrdinal(a.Owner, b.Owner));
        return found;
    }

    private static string Join(IEnumerable<string> names)
    {
        string joined = string.Join(" ", names);
        return joined.Length == 0 ? "none" : joined;
    }

    // The monitor row driven: the page opens on the saved screen, draws that screen's own label and
    // hands the index back on the exit, which is what Launcher.ApplyOptions resolves and applies
    // before the mode and the size. The plan is asserted rather than applied to another screen; a
    // scripted run's window is hidden off screen and moving it would put it over the desktop.
    private static void AppliedMonitorRow(TestContext ctx, MenuLayout layout, int standing)
    {
        var shell = new OriginalShell(layout, new FreeFlightFeature(), new PlayerSetupFeature(),
            _ => null, options: () => OptionsStore.UserOptions().Load(), screens: MonitorSetting.Screens);
        shell.Options.OpenVideo();
        ctx.Check(shell.Screen == OriginalScreen.Video && shell.Options.MonitorChoice == MonitorSetting.Word(standing),
            $"the VIDEO page opens showing the saved screen ({shell.Screen}, {shell.Options.MonitorChoice ?? "unset"})");
        ctx.Check(Label(shell, OriginalOptionsScreen.MonitorKey) == MonitorSetting.Screens().Labels[standing],
            $"with the row drawing that screen's label ({Label(shell, OriginalOptionsScreen.MonitorKey)})");
        var applied = Accept(shell);
        ctx.Check(applied?.MonitorIndex == MonitorSetting.Word(standing),
            $"ACCEPT CHANGES carries it on the apply exit ({applied?.MonitorIndex ?? "no exit"})");
        var plan = MonitorSetting.Resolve(applied?.MonitorIndex, MonitorSetting.Screens());
        ctx.Check(plan.Screen == standing && plan.Source == "options.json",
            $"and the apply resolves that index to the screen the window would move to ({Describe(plan)})");
    }

    // The resolution row driven: the page opens on the saved size and ACCEPT CHANGES hands it back on
    // the exit, which is the size Launcher.ApplyOptions resolves and applies. The plan is asserted
    // rather than applied; resizing a scripted run's window would move the viewport every later shot
    // in this process is captured from.
    private static void AppliedSizeRow(TestContext ctx, MenuLayout layout)
    {
        var shell = new OriginalShell(layout, new FreeFlightFeature(), new PlayerSetupFeature(),
            _ => null, options: () => OptionsStore.UserOptions().Load(), screenSizes: ResolutionSetting.ScreenSizes);
        shell.Options.OpenVideo();
        ctx.Check(shell.Screen == OriginalScreen.Video && shell.Options.ResolutionChoice == "1024x768",
            $"the VIDEO page opens showing the saved size ({shell.Screen}, {shell.Options.ResolutionChoice ?? "unset"})");
        ctx.Check(Label(shell, OriginalOptionsScreen.ResolutionKey) == "1024x768",
            $"with the row drawing it, so the page and the apply name one size ({Label(shell, OriginalOptionsScreen.ResolutionKey)})");
        var applied = Accept(shell);
        ctx.Check(applied?.Resolution == "1024x768",
            $"ACCEPT CHANGES carries it on the apply exit ({applied?.Resolution ?? "no exit"})");
        var plan = ResolutionSetting.Resolve(applied?.Resolution, ResolutionSetting.ScreenSizes(), applied?.DisplayMode);
        ctx.Check(plan is { Width: 1024, Height: 768, Source: "options.json" },
            $"and the apply resolves that size to the pixels the window would take ({Describe(plan)})");
    }

    // The same row under borderless, which owns the size: the page draws the screen's own size
    // whatever is saved, the row takes no press, and the saved size still rides out on the exit, so
    // a pilot who picks Windowed again gets the size they chose rather than the one this mode drew.
    private static void PinnedSizeRow(TestContext ctx, MenuLayout layout)
    {
        var shell = new OriginalShell(layout, new FreeFlightFeature(), new PlayerSetupFeature(),
            _ => null, options: () => OptionsStore.UserOptions().Load(), screenSizes: ResolutionSetting.ScreenSizes);
        shell.Options.OpenVideo();
        var screen = ResolutionSetting.ScreenSizes();
        ctx.Check(shell.Options.ResolutionPinned && Label(shell, OriginalOptionsScreen.ResolutionKey) == screen.Fallback,
            $"the borderless page draws this screen's own size on the size row ({Label(shell, OriginalOptionsScreen.ResolutionKey)})");
        ctx.Check(Row(shell, OriginalOptionsScreen.ResolutionKey) is { Enabled: false },
            $"with the row dead, so the cursor walks past it and no press reaches it");
        shell.Step(new MenuCommands { MoveX = 1 });
        ctx.Check(Label(shell, OriginalOptionsScreen.ResolutionKey) == screen.Fallback && shell.Options.ResolutionChoice == "1024x768",
            $"a sideways step changes nothing and leaves the saved size where it is ({shell.Options.ResolutionChoice ?? "unset"})");
        var applied = Accept(shell);
        ctx.Check(applied?.Resolution == "1024x768" && applied?.DisplayMode == DisplayWords.Borderless,
            $"which rides out unchanged on the apply exit ({applied?.Resolution ?? "no exit"})");
        var plan = ResolutionSetting.Resolve(applied?.Resolution, screen, applied?.DisplayMode);
        ctx.Check(plan.Word == screen.Fallback && plan.Source == DisplayWords.Borderless,
            $"and the apply takes the screen's own size, the mode beating the file ({Describe(plan)})");
    }

    // The same row over a size the standard table does not carry, written into the options file by
    // hand: the page opens on it, offers it in the list where it sorts, and ACCEPT CHANGES hands it
    // back unchanged, so a hand-written size survives a visit to the page rather than being replaced
    // by the nearest listed one.
    private static void CustomSizeRow(TestContext ctx, MenuLayout layout)
    {
        var shell = new OriginalShell(layout, new FreeFlightFeature(), new PlayerSetupFeature(),
            _ => null, options: () => OptionsStore.UserOptions().Load(), screenSizes: ResolutionSetting.ScreenSizes);
        shell.Options.OpenVideo();
        var screen = ResolutionSetting.ScreenSizes();
        var words = shell.Options.ResolutionWords;
        ctx.Check(shell.Options.ResolutionChoice == CustomSize
            && Label(shell, OriginalOptionsScreen.ResolutionKey) == CustomSize,
            $"the VIDEO page opens on a hand-written size and draws it ({Label(shell, OriginalOptionsScreen.ResolutionKey)})");
        ctx.Check(words.Count == screen.Words.Count + 1 && words[0] == CustomSize && words.Contains(screen.Fallback),
            $"which stands in the list where it sorts, beside every size the screen holds ({string.Join(" ", words)})");
        var applied = Accept(shell);
        ctx.Check(applied?.Resolution == CustomSize,
            $"and ACCEPT CHANGES writes it back unchanged ({applied?.Resolution ?? "no exit"})");
        var plan = ResolutionSetting.Resolve(applied?.Resolution, screen, applied?.DisplayMode);
        ctx.Check(plan.Word == CustomSize && plan.Source == "options.json",
            $"the apply taking the file's own size rather than the nearest listed one ({Describe(plan)})");
    }

    // The built-in presentation's size row over the same file: its list is widened the same way, so
    // the hand-written size stands on the row, a sideways step leaves it for a listed size, and a
    // step back reaches it again, the file's size holding its place for as long as the page is open.
    private static void BuiltInCustomSizeRow(TestContext ctx)
    {
        var host = MenuSuiteHost.Bare(new List<MenuExit>(), ctx.DataRoot, out var seat);
        var menu = CSVM.UI.Screens.LaunchMenu.Build(ctx.ZrdrPath, ctx.DataRoot, host, seat.Input);
        ctx.Host.AddChild(menu);
        try
        {
            menu.ShowMenu("options");
            for (int i = 0; i < BuiltInResolutionRow; i++)
            {
                menu.Drive(new MenuCommands { MoveY = 1 });
            }

            ctx.Check(menu.ShownRowText == $"Resolution: {CustomSize}",
                $"the built-in Options screen draws the hand-written size on its size row ({menu.ShownRowText})");
            menu.Drive(new MenuCommands { MoveX = 1 });
            string stepped = menu.ShownRowText;
            ctx.Check(stepped != $"Resolution: {CustomSize}"
                && stepped.StartsWith("Resolution: ", StringComparison.Ordinal),
                $"a sideways step leaves it for a size the standard table carries ({stepped})");
            menu.Drive(new MenuCommands { MoveX = -1 });
            ctx.Check(menu.ShownRowText == $"Resolution: {CustomSize}",
                $"and a step back reaches it again, its place in the list being the file's ({menu.ShownRowText})");
        }
        finally
        {
            ctx.Host.RemoveChild(menu);
            menu.QueueFree();
        }
    }

    // The display-mode row driven: the page opens on the saved word and ACCEPT CHANGES hands it back
    // on the exit, which is the word Launcher.ApplyOptions resolves and applies. The mode the plan
    // resolves to is asserted rather than applied; changing a scripted run's window mode would put
    // the hidden window back on the screen every other suite renders behind.
    private static void AppliedModeRow(TestContext ctx, MenuLayout layout)
    {
        var shell = new OriginalShell(layout, new FreeFlightFeature(), new PlayerSetupFeature(),
            _ => null, options: () => OptionsStore.UserOptions().Load());
        shell.Options.OpenVideo();
        ctx.Check(shell.Screen == OriginalScreen.Video && shell.Options.DisplayModeChoice == DisplayWords.Borderless,
            $"the VIDEO page opens showing the saved mode ({shell.Screen}, {shell.Options.DisplayModeChoice ?? "unset"})");
        var applied = Accept(shell);
        ctx.Check(applied?.DisplayMode == DisplayWords.Borderless,
            $"ACCEPT CHANGES carries it on the apply exit ({applied?.DisplayMode ?? "no exit"})");
        var plan = DisplayModeSetting.Resolve(applied?.DisplayMode);
        ctx.Check(plan.Mode == DisplayServer.WindowMode.Fullscreen && plan.Source == "options.json",
            $"and the apply resolves that word to the borderless window mode ({plan.Mode}, {plan.Source})");
    }

    // The row driven and its choice applied: the page opens on the saved word, ACCEPT CHANGES hands
    // it back on the exit, and the two engine calls the apply makes are read back off the engine
    // rather than trusted. The window's pacing is restored before anything else in the run sees it.
    private static void AppliedRow(TestContext ctx, MenuLayout layout)
    {
        var shell = new OriginalShell(layout, new FreeFlightFeature(), new PlayerSetupFeature(),
            _ => null, options: () => OptionsStore.UserOptions().Load());
        shell.Options.OpenVideo();
        ctx.Check(shell.Screen == OriginalScreen.Video && shell.Options.VSyncChoice == "144",
            $"the VIDEO page opens showing the saved cap ({shell.Screen}, {shell.Options.VSyncChoice ?? "unset"})");
        var applied = Accept(shell);
        ctx.Check(applied?.VSync == "144", $"ACCEPT CHANGES carries it on the apply exit ({applied?.VSync ?? "no exit"})");

        var entry = new VSyncPlan(DisplayServer.WindowGetVsyncMode() != DisplayServer.VSyncMode.Disabled, Engine.MaxFps, "restored");
        try
        {
            VSyncSetting.Apply(VSyncSetting.Resolve(false, applied?.VSync, true));
            ctx.Check(Engine.MaxFps == 144 && DisplayServer.WindowGetVsyncMode() == DisplayServer.VSyncMode.Disabled,
                $"applying it caps the frame loop at 144 with V-Sync off (max_fps={Engine.MaxFps}, {DisplayServer.WindowGetVsyncMode()})");
            VSyncSetting.Apply(VSyncSetting.Resolve(false, DisplayWords.VSyncOn, true));
            ctx.Check(Engine.MaxFps == VSyncSetting.Uncapped && DisplayServer.WindowGetVsyncMode() == DisplayServer.VSyncMode.Enabled,
                $"the On word puts it back on the screen with no cap (max_fps={Engine.MaxFps}, {DisplayServer.WindowGetVsyncMode()})");
            VSyncSetting.Apply(VSyncSetting.Resolve(true, "144", true));
            ctx.Check(Engine.MaxFps == VSyncSetting.Uncapped && DisplayServer.WindowGetVsyncMode() == DisplayServer.VSyncMode.Disabled,
                $"and --no-vsync applies as an uncapped loop over the same saved cap (max_fps={Engine.MaxFps})");
        }
        finally
        {
            VSyncSetting.Apply(entry);
        }
    }

    // Walks the page's focus onto ACCEPT CHANGES and presses it, the keyboard's own way out.
    private static OptionsApplyExit? Accept(OriginalShell shell)
    {
        for (int guard = 0; guard < 32 && shell.FocusedKey != OriginalOptionsScreen.VideoAcceptKey; guard++)
        {
            shell.Step(new MenuCommands { MoveY = 1 });
        }

        return shell.Step(new MenuCommands { Accept = true }).Exit as OptionsApplyExit;
    }

    // What one row of the page draws, found by key rather than by position: the table stands in
    // authored row order, so a row added above shifts every index under it.
    private static string Label(OriginalShell shell, string key) =>
        Row(shell, key)?.Label ?? "no such row";

    private static OriginalRow? Row(OriginalShell shell, string key)
    {
        foreach (var row in shell.Rows)
        {
            if (row.Key == key)
            {
                return row;
            }
        }

        return null;
    }

    private static string Describe(MonitorPlan plan) =>
        $"screen {plan.Screen} word={plan.Word} source={plan.Source}";

    private static string Describe(ResolutionPlan plan) =>
        $"{plan.Width}x{plan.Height} word={plan.Word} source={plan.Source}";

    private static string Describe(VSyncPlan plan) =>
        $"vsync {(plan.Enabled ? "on" : "off")} max_fps={plan.MaxFps} source={plan.Source}";
}
