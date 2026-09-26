using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using CSVM.Flight.Ai;
using CSVM.Flight.Airframe;
using CSVM.Flight.Audio;
using CSVM.Flight.Weapons;
using CSVM.Mech3;
using CSVM.Session.Launch;
using CSVM.UI.Menu;
using CSVM.UI.Menu.Original;
using CSVM.UI.Screens;
using CSVM.Utils;
using Godot;

namespace CSVM.Testing;

/// <summary>The mix guard: the four buses the project ships stand up, and every site that builds an
/// <see cref="AudioStreamPlayer"/> or <see cref="AudioStreamPlayer3D"/> puts it on a named child bus
/// rather than on Master. A player left on Master is silent about it, since Godot resolves an
/// unknown or unset bus name to Master with no error, so this is the only thing that can see the
/// omission. The suite builds each site itself and then walks the live scene tree, because a walk
/// alone would pass by seeing nothing. Which level reaches a bus is the neighbouring
/// <c>audio-levels-launch</c> suite's, so the saved-and-<c>--det</c> ladder is asserted once rather
/// than in both places; here the levels are only ever the shipped defaults or this suite's
/// own.</summary>
internal static class AudioBusSuites
{
    // The whine slot no shipped airframe names, and a crash set an install may lack, make an exact
    // player count per site brittle. What every site must satisfy is "at least one, none on Master".
    private const int AtLeastOne = 1;

    // The dB an inaudible bus sits at, AudioMix's floor expressed as a volume. A repo run resolves
    // the developer volume to 0 and lands bus 0 here, which is what keeps a scripted run silent.
    private const float SilenceDb = -80f;

    // Slack for a dB comparison, well under the 6 dB a level step of that size moves a bus by.
    private const float DbSlack = 0.01f;

    // Sideways presses to walk a slider from any level to its floor. A press moves SliderControl's
    // KeyStep and the ends clamp rather than wrap, so any count past the range is the floor exactly.
    private const int ToSilence = (AudioMix.MaxLevel / SliderControl.KeyStep) + 1;

    // The walk's guard against a key a screen does not carry: the pages here are far shorter, and a
    // focus that never reaches the key would otherwise spin.
    private const int WalkGuard = 32;

    [Suite("audio-buses",
        "the shipped bus layout, the mix written onto it, and the placement of every player: "
        + "Master carries Music, Effects and Voice as its children, each of the fourteen "
        + "player-construction sites (the music channel, the mission radio, the menu service's "
        + "narration and cue, FlightAudio's six, AiEngineAudio's loop factory, WorldSounds' emitter "
        + "and one-shot, and the projectile pool) builds its players on the bus its category names, "
        + "a walk of the whole live scene tree fails on any player left on Master, the four "
        + "levels reach the three child buses at startup and on a live change while bus 0, which "
        + "carries the developer volume alone, is untouched by either, a page's live preview "
        + "moves all three buses from Master alone, sounds one clip over twenty-one level changes "
        + "rather than twenty-one, and puts back the exact mix it opened over, and Effects walked to "
        + "silence and accepted on the pause sheet's AUDIO page leaves the flight's own gun and "
        + "engine players on a silent bus at once rather than at the next start")]
    internal static void AudioBusPlacement(TestContext ctx)
    {
        ctx.RequireData(ctx.ZrdrPath, $"zrdr archive");
        ctx.RequireData(ctx.SoundsPath, $"sound archive (soundsh)");
        ctx.RequireData(ctx.PlanesGamezPath, $"planes gamez");
        string texturesPath = SessionPaths.ChapterTextures(ctx.DataRoot, "C1");
        ctx.RequireData(texturesPath, $"C1 textures");

        var report = new StringBuilder();
        CheckLayout(ctx, report);
        CheckMix(ctx, report);

        var defs = SoundDefs.Load(ctx.ZrdrPath);
        var groups = SoundDefs.LoadGroups(ctx.ZrdrPath);
        var stats = PlaneStats.Load(ctx.ZrdrPath, ctx.PlaneName);
        var weapons = WeaponDefs.Load(ctx.ZrdrPath, null);
        string? gunLoop = weapons.All
            .Select(w => w.LoopedSoundName)
            .FirstOrDefault(n => !string.IsNullOrEmpty(n));
        using var archive = new SoundArchive(ctx.SoundsPath);
        var textures = new TextureArchive(texturesPath);

        MusicPlayer? music = null;
        MissionRadio? radio = null;
        MenuAudioService? menu = null;
        FlightAudio? flight = null;
        AiEngineAudio? aiEngine = null;
        WorldSounds? world = null;
        ProjectilePool? pool = null;
        try
        {
            music = new MusicPlayer(defs, groups)
            {
                Name = "GuardMusic",
                Loader = (def, looped) => archive.Find(def.WavName, looped, warn: false),
            };
            ctx.Host.AddChild(music);

            radio = new MissionRadio(defs, groups, name => Stream(archive, defs, name))
            {
                Name = "GuardRadio",
            };
            ctx.Host.AddChild(radio);

            // With previews on, so the two preview players are built and swept like every other
            // site; the launcher turns them off in exactly the runs this suite is one of.
            menu = new MenuAudioService(null, name => Stream(archive, defs, name),
                Path.Combine(ctx.DataRoot, "extracted", "rof", "ASSETS", "SOUNDS"), previews: true)
            {
                Name = "GuardMenuAudio",
            };
            ctx.Host.AddChild(menu);

            flight = new FlightAudio { Name = "GuardFlightAudio" };
            flight.Setup(archive, defs, stats, weapons, groups);
            ctx.Host.AddChild(flight);
            if (gunLoop != null)
            {
                flight.StartGunLoop(gunLoop);
            }
            flight.StartNitroLoop();

            aiEngine = new AiEngineAudio { Name = "GuardAiEngineAudio" };
            ctx.Host.AddChild(aiEngine);
            aiEngine.Setup(archive, defs, stats);

            world = new WorldSounds(defs, groups)
            {
                Name = "GuardWorldSounds",
                Loader = (def, warn) => archive.Find(def.WavName, def.Looped, warn),
            };
            ctx.Host.AddChild(world);
            string? emitter = FirstEmitter(world, defs);
            int started = world.OneShotsStarted;
            string? oneShot = emitter == null
                ? null
                : world.PlayOneShot(emitter, Vector3.Zero, new Random(3));
            ctx.Check(emitter != null && oneShot != null && world.OneShotsStarted > started,
                $"an ambient emitter and a one-shot were both built name={emitter ?? "(none)"}");

            pool = new ProjectilePool(textures, archive, defs) { Name = "GuardProjectiles" };
            ctx.Host.AddChild(pool);

            CheckSite(ctx, music, "MusicPlayer._player", AudioBuses.Music, report);
            CheckSite(ctx, radio, "MissionRadio._player", AudioBuses.Voice, report);
            CheckSite(ctx, menu, "MenuAudioService narration + cue + the two preview players",
                AudioBuses.Effects, AudioBuses.Voice, report);
            CheckPreview(ctx, menu, report);
            CheckSite(ctx, flight, "FlightAudio's six", AudioBuses.Effects, report);
            CheckSite(ctx, aiEngine, "AiEngineAudio.MakeLoop", AudioBuses.Effects, report);
            CheckSite(ctx, world, "WorldSounds emitter + one-shot", AudioBuses.Effects, report);
            CheckSite(ctx, pool, "ProjectilePool's eight", AudioBuses.Effects, report);
            SweepTree(ctx, report);
            CheckPauseAccept(ctx, menu, flight, aiEngine, report);
        }
        finally
        {
            world?.FlushOneShots();
            pool?.Free();
            world?.Free();
            aiEngine?.Free();
            flight?.Free();
            menu?.Free();
            radio?.Free();
            music?.Stop();
            music?.Free();
            textures.Dispose();
        }

        ctx.WriteArtifact("test-audio-buses.txt", report.ToString());
    }

    // The layout half: the process runs four buses, and the three category buses send into Master,
    // so the developer gain and the focus mute still reach every sound.
    private static void CheckLayout(TestContext ctx, StringBuilder report)
    {
        int count = AudioServer.BusCount;
        report.AppendLine($"buses={count}");
        ctx.Same(4, count, $"the shipped layout stands up four buses");
        var expected = new[] { AudioBuses.Master, AudioBuses.Music, AudioBuses.Effects, AudioBuses.Voice };
        for (int i = 0; i < expected.Length && i < count; i++)
        {
            string name = AudioServer.GetBusName(i);
            string send = i == 0 ? "" : AudioServer.GetBusSend(i).ToString();
            report.AppendLine($"  bus {i} name={name} send={send} volume_db={AudioServer.GetBusVolumeDb(i):0.###}");
            ctx.Check(name == expected[i], $"bus {i} is named {expected[i]} actual={name}");
            ctx.Check(i == 0 || send == AudioBuses.Master,
                $"bus {i} ({name}) sends into Master actual={send}");
            if (i == 0)
            {
                // Bus 0 carries the developer volume and never a level, so it has no expected gain
                // to compare against; CheckMix is where it is held to being untouched.
                continue;
            }

            // A child bus carries the startup mix rather than a resting gain, and under --run-tests
            // that is the shipped defaults because OptionsStore reads an emptied scratch directory.
            ctx.Check(Math.Abs(AudioServer.GetBusVolumeDb(i) - StartupDb(name)) <= DbSlack,
                $"bus {i} ({name}) carries its shipped level db={AudioServer.GetBusVolumeDb(i):0.###} want={StartupDb(name):0.###}");
        }
    }

    // The gain the shipped defaults put a child bus at. Master is a multiplier over the other
    // three, so it is never a bus of its own and answers the resting gain here.
    private static float StartupDb(string bus) => bus switch
    {
        AudioBuses.Music => AudioMix.VolumeDb(AudioMix.DefaultMusic, AudioMix.DefaultMaster),
        AudioBuses.Effects => AudioMix.VolumeDb(AudioMix.DefaultEffects, AudioMix.DefaultMaster),
        AudioBuses.Voice => AudioMix.VolumeDb(AudioMix.DefaultVoice, AudioMix.DefaultMaster),
        _ => 0f,
    };

    // The mix half: a live change reaches all three child buses, the loudest possible mix leaves
    // every one of them at or below its resting gain, and bus 0 comes through both untouched. That
    // last pair is what makes a saved level unable to un-silence a scripted run.
    private static void CheckMix(TestContext ctx, StringBuilder report)
    {
        float developer = AudioServer.GetBusVolumeDb(0);
        report.AppendLine($"developer volume on bus 0 db={developer:0.###}, outside the mix");
        try
        {
            AudioMix.Apply(master: 50, music: 100, effects: 0, voice: 25);
            CheckBus(ctx, AudioBuses.Music, 100, 50, report);
            CheckBus(ctx, AudioBuses.Effects, 0, 50, report);
            CheckBus(ctx, AudioBuses.Voice, 25, 50, report);

            AudioMix.Apply(master: 100, music: 100, effects: 100, voice: 100);
            foreach (string bus in new[] { AudioBuses.Music, AudioBuses.Effects, AudioBuses.Voice })
            {
                float db = AudioServer.GetBusVolumeDb(AudioServer.GetBusIndex(bus));
                ctx.Check(db <= DbSlack, $"the loudest mix leaves {bus} at or below its resting gain db={db:0.###}");
            }
            float after = AudioServer.GetBusVolumeDb(0);
            ctx.Check(Math.Abs(after - developer) <= DbSlack,
                $"the mix left bus 0 alone db={after:0.###} before={developer:0.###}");
            ctx.Check(developer > SilenceDb + DbSlack || after <= SilenceDb + DbSlack,
                $"a run silenced by --volume= stays silent at full levels db={after:0.###}");
            ctx.Note($"developer volume db={developer:0.###} unchanged by the mix");
        }
        finally
        {
            AudioMix.Apply();
        }

        CheckBus(ctx, AudioBuses.Music, AudioMix.DefaultMusic, AudioMix.DefaultMaster, report);
        CheckBus(ctx, AudioBuses.Effects, AudioMix.DefaultEffects, AudioMix.DefaultMaster, report);
        CheckBus(ctx, AudioBuses.Voice, AudioMix.DefaultVoice, AudioMix.DefaultMaster, report);
    }

    // The preview half: the mix a page applies while it is open reaches all three child buses,
    // Master moves every one of them at once, a level moved twenty times over sounds one clip rather
    // than twenty, and ending the preview puts back exactly the gains that stood when it began.
    // ⚠ The mix it opens over is nothing like the shipped defaults on purpose: a restore that merely
    // reapplied the defaults would pass by coincidence against a shipped-default mix, which is the
    // failure this whole check exists to catch.
    private static void CheckPreview(TestContext ctx, MenuAudioService menu, StringBuilder report)
    {
        float developer = AudioServer.GetBusVolumeDb(0);
        try
        {
            AudioMix.Apply(master: 33, music: 7, effects: 91, voice: 12);
            var opened = AudioMix.Capture();
            report.AppendLine($"preview opened over music={opened.Music:0.###} effects={opened.Effects:0.###} voice={opened.Voice:0.###}");

            int starts = menu.MixPreviewStarts;
            menu.PreviewMix(new AudioLevels(100, 100, 100, 100), MenuMixLevel.Effects);
            CheckBus(ctx, AudioBuses.Music, 100, 100, report);
            CheckBus(ctx, AudioBuses.Effects, 100, 100, report);
            CheckBus(ctx, AudioBuses.Voice, 100, 100, report);

            // Master is a multiplier, so moving it alone has to move all three and not one.
            menu.PreviewMix(new AudioLevels(50, 100, 100, 100), MenuMixLevel.Master);
            CheckBus(ctx, AudioBuses.Music, 100, 50, report);
            CheckBus(ctx, AudioBuses.Effects, 100, 50, report);
            CheckBus(ctx, AudioBuses.Voice, 100, 50, report);

            for (int level = 0; level < 20; level++)
            {
                menu.PreviewMix(new AudioLevels(50, 100, 80 + level, 100), MenuMixLevel.Effects);
            }

            int fired = menu.MixPreviewStarts - starts;
            report.AppendLine($"preview clips started over twenty-one Effects changes: {fired}");
            string clip = Path.Combine(ctx.DataRoot, "extracted", "rof", "ASSETS", "SOUNDS", "SFX_LOOP.WAV");
            if (File.Exists(clip))
            {
                ctx.Check(fired == 1,
                    $"a level moved twenty-one times over sounds one preview clip, neither none nor one per change (started={fired})");
            }
            else
            {
                ctx.Check(fired == 0, $"a preview with no clip on disk sounds nothing (started={fired})");
                ctx.Note($"no SFX_LOOP.WAV under the extracted rof tree, so this run could only see the silent half");
            }

            ctx.Check(Math.Abs(AudioServer.GetBusVolumeDb(0) - developer) <= DbSlack,
                $"the preview left bus 0 alone db={AudioServer.GetBusVolumeDb(0):0.###} before={developer:0.###}");

            menu.EndMixPreview();
            var after = AudioMix.Capture();
            report.AppendLine($"preview ended on music={after.Music:0.###} effects={after.Effects:0.###} voice={after.Voice:0.###}");
            ctx.Check(after == opened,
                $"ending the preview puts back the exact mix it opened over (music={after.Music:0.###}/{opened.Music:0.###} effects={after.Effects:0.###}/{opened.Effects:0.###} voice={after.Voice:0.###}/{opened.Voice:0.###})");
            ctx.Check(Math.Abs(AudioServer.GetBusVolumeDb(0) - developer) <= DbSlack,
                $"and left bus 0 alone through the restore db={AudioServer.GetBusVolumeDb(0):0.###} before={developer:0.###}");
            ctx.Note($"preview clips started={fired} over twenty-one Effects changes");
        }
        finally
        {
            menu.EndMixPreview();
            AudioMix.Apply();
        }
    }

    // The pause half: the AUDIO page opened over a held mission, Effects walked to silence and
    // accepted, and the flight's own live players read straight afterwards. What this catches is an
    // accept that applies the levels and then hands the mix back to the preview's restore, which
    // leaves the accepted level saved but inaudible until the next start. ⚠ Drive the leaf rather
    // than calling the mix by hand: the ordering of the apply against the end of the preview is the
    // whole subject, and a suite that applied the levels itself would pass over the fault.
    private static void CheckPauseAccept(
        TestContext ctx, MenuAudioService menu, FlightAudio flight, AiEngineAudio aiEngine,
        StringBuilder report)
    {
        var layout = OriginalAvailability.Load(ctx.DataRoot, out string? why);
        if (layout == null)
        {
            ctx.Note($"no decoded menu layout ({why}), so the pause accept was not walked");
            return;
        }

        OptionsApplyExit? applied = null;
        // The audio half of Launcher.PersistOptions, which is what the launcher hands the leaf. The
        // options file it also writes is the pause-preferences suite's subject, not this one's.
        var leaf = PausePreferences.Build(ctx.DataRoot, layout, null, exit =>
        {
            applied = exit;
            AudioMix.Apply(exit.AudioMaster, exit.AudioMusic, exit.AudioEffects, exit.AudioVoice);
        }, menu);
        ctx.Check(leaf != null, $"the Preferences leaf builds over the install's decoded layout");
        if (leaf == null)
        {
            return;
        }

        ctx.Host.AddChild(leaf);
        float developer = AudioServer.GetBusVolumeDb(0);
        try
        {
            AudioMix.Apply();
            var reader = new MenuInput { Keyboard = false, Pads = Array.Empty<int>() };
            leaf.Open(new[] { reader }, 0);
            WalkTo(leaf, OriginalOptionsScreen.AudioDoorKey);
            leaf.Drive(new MenuCommands { Accept = true });
            ctx.Check(leaf.Shell.Screen == OriginalScreen.Audio,
                $"the AUDIO door behind the Options screen opens over the pause ({leaf.Shell.Screen})");
            WalkTo(leaf, OriginalOptionsScreen.AudioEffectsKey);
            for (int press = 0; press < ToSilence; press++)
            {
                leaf.Drive(new MenuCommands { MoveX = -1 });
            }

            ctx.Check(leaf.Shell.Options.AudioEffectsChoice == AudioMix.MinLevel,
                $"the Effects row walks to silence ({leaf.Shell.Options.AudioEffectsChoice?.ToString() ?? "unset"})");
            float moving = BusVolume(AudioBuses.Effects);
            report.AppendLine($"pause AUDIO page: Effects at {AudioMix.MinLevel} previews db={moving:0.###}");
            WalkTo(leaf, OriginalOptionsScreen.AudioAcceptKey);
            leaf.Drive(new MenuCommands { Accept = true });
            ctx.Check(applied != null && applied.AudioEffects == AudioMix.MinLevel,
                $"ACCEPT CHANGES hands the walked level to the apply ({applied?.AudioEffects?.ToString() ?? "no exit"})");
            ctx.Check(!leaf.Visible, $"and closes the leaf back onto the sheet (visible={leaf.Visible})");

            float effects = BusVolume(AudioBuses.Effects);
            report.AppendLine($"after the accept: {AudioBuses.Effects} db={effects:0.###}");
            ctx.Check(effects <= SilenceDb + DbSlack,
                $"the accepted silence stands on {AudioBuses.Effects} while the mission is still held db={effects:0.###}");
            CheckPlayersHeardAt(ctx, flight, "FlightAudio's six", effects, report);
            CheckPlayersHeardAt(ctx, aiEngine, "AiEngineAudio.MakeLoop", effects, report);
            // The two categories nobody moved keep the levels the page opened on, so an accept is a
            // mix and not a mute of everything.
            CheckBus(ctx, AudioBuses.Music, AudioMix.DefaultMusic, AudioMix.DefaultMaster, report);
            CheckBus(ctx, AudioBuses.Voice, AudioMix.DefaultVoice, AudioMix.DefaultMaster, report);
            float after = AudioServer.GetBusVolumeDb(0);
            ctx.Check(Math.Abs(after - developer) <= DbSlack,
                $"and the accept left bus 0 alone db={after:0.###} before={developer:0.###}");
            ctx.Note($"pause accept: Effects {AudioMix.MinLevel} lands at db={effects:0.###} on the held mission");
        }
        finally
        {
            leaf.Close();
            menu.EndMixPreview();
            ctx.Host.RemoveChild(leaf);
            leaf.QueueFree();
            AudioMix.Apply();
        }
    }

    // Every player a site built, read against the gain its own bus actually stands at. Reading the
    // bus the player names is the only thing that says a moved level was heard: a player left on
    // Master, or a bus the accept never wrote, both sound on regardless of what was accepted.
    private static void CheckPlayersHeardAt(
        TestContext ctx, Node owner, string site, float want, StringBuilder report)
    {
        var players = new List<(string Path, string Bus)>();
        Collect(owner, owner, players);
        ctx.Check(players.Count >= AtLeastOne, $"{site} built at least one player got={players.Count}");
        foreach (var (path, bus) in players)
        {
            float db = BusVolume(bus);
            report.AppendLine($"  {path} bus={bus} db={db:0.###}");
            ctx.Check(bus != AudioBuses.Master && Math.Abs(db - want) <= DbSlack,
                $"{site}'s {path} sounds at the accepted level bus={bus} db={db:0.###} want={want:0.###}");
        }
    }

    // A bus's standing volume by name, NaN where no bus carries the name, which reads in a report
    // rather than throwing off an index of -1.
    private static float BusVolume(string bus)
    {
        int index = AudioServer.GetBusIndex(bus);
        return index < 0 ? float.NaN : AudioServer.GetBusVolumeDb(index);
    }

    // Walks the page's focus onto a key, the keyboard's own way across a screen.
    private static void WalkTo(PausePreferences leaf, string key)
    {
        for (int guard = 0; guard < WalkGuard && leaf.Shell.FocusedKey != key; guard++)
        {
            leaf.Drive(new MenuCommands { MoveY = 1 });
        }
    }

    private static void CheckBus(TestContext ctx, string bus, int level, int master,
        StringBuilder report)
    {
        int index = AudioServer.GetBusIndex(bus);
        float db = index < 0 ? float.NaN : AudioServer.GetBusVolumeDb(index);
        float want = AudioMix.VolumeDb(level, master);
        report.AppendLine($"  {bus} level={level} master={master} db={db:0.###} want={want:0.###}");
        ctx.Check(index > 0 && Math.Abs(db - want) <= DbSlack,
            $"{bus} carries level {level} under master {master} db={db:0.###} want={want:0.###}");
    }

    private static void CheckSite(TestContext ctx, Node owner, string site, string bus,
        StringBuilder report) =>
        CheckSite(ctx, owner, site, bus, bus, report);

    // One construction site: at least one player under the node that owns it, and every one of them
    // on a bus this site is allowed to use. The "at least one" half is what stops a site that
    // silently built nothing from reading as a site that placed everything correctly.
    private static void CheckSite(TestContext ctx, Node owner, string site, string bus,
        string alsoBus, StringBuilder report)
    {
        var players = new List<(string Path, string Bus)>();
        Collect(owner, owner, players);
        report.AppendLine($"{site}: {players.Count} player(s)");
        foreach (var (path, actual) in players)
        {
            report.AppendLine($"  {path} bus={actual}");
        }
        ctx.Check(players.Count >= AtLeastOne, $"{site} built at least one player got={players.Count}");
        foreach (var (path, actual) in players)
        {
            ctx.Check(actual == bus || actual == alsoBus,
                $"{site} placed {path} on {bus}{(alsoBus == bus ? "" : $"/{alsoBus}")} actual={actual}");
        }
    }

    // The catch-all: every player anywhere in the live tree, including one this suite never named.
    // Reads Bus rather than a construction argument, so an unknown bus name reports as Master here
    // exactly as it would sound.
    private static void SweepTree(TestContext ctx, StringBuilder report)
    {
        var root = ctx.Host.GetTree()?.Root;
        if (root == null)
        {
            ctx.Check(false, $"the suite host is not in a live scene tree, so nothing was swept");
            return;
        }
        var players = new List<(string Path, string Bus)>();
        Collect(root, root, players);
        var onMaster = players.Where(p => p.Bus == AudioBuses.Master).ToList();
        report.AppendLine($"live tree: {players.Count} player(s), {onMaster.Count} on Master");
        foreach (var (path, bus) in players)
        {
            report.AppendLine($"  {path} bus={bus}");
        }
        ctx.Check(players.Count >= AtLeastOne, $"the tree walk found players got={players.Count}");
        foreach (var (path, _) in onMaster)
        {
            ctx.Check(false, $"player left on Master: {path}");
        }
        ctx.Note($"live players={players.Count} on_master={onMaster.Count}");
    }

    private static void Collect(Node from, Node root, List<(string Path, string Bus)> into)
    {
        string bus = from switch
        {
            AudioStreamPlayer p => p.Bus.ToString(),
            AudioStreamPlayer3D p => p.Bus.ToString(),
            _ => "",
        };
        if (bus.Length > 0)
        {
            into.Add((PathFrom(root, from), bus));
        }
        foreach (var child in from.GetChildren())
        {
            Collect(child, root, into);
        }
    }

    private static string PathFrom(Node root, Node node) =>
        ReferenceEquals(root, node) ? node.Name.ToString() : root.GetPathTo(node).ToString();

    private static AudioStreamWav? Stream(SoundArchive archive, IReadOnlyDictionary<string, SoundDef> defs,
        string name) =>
        defs.TryGetValue(name, out var def) ? archive.Find(def.WavName, def.Looped, warn: false) : null;

    // The first definition that actually resolves to an emitter, rather than a hard-coded name: an
    // install's sound archive decides which of them has a WAV behind it.
    private static string? FirstEmitter(WorldSounds sounds, IReadOnlyDictionary<string, SoundDef> defs)
    {
        foreach (string name in defs.Keys)
        {
            if (sounds.Create(name) != null)
            {
                return name;
            }
        }
        return null;
    }
}
