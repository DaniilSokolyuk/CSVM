using System;
using System.IO;
using CSVM.Utils;
using Godot;

namespace CSVM.Testing;

/// <summary>The four saved volume levels at launch, through the real options store and a parsed
/// command line: a saved level reaches its own category bus, a <c>--det</c> run reads none of them
/// and mixes the shipped defaults, and <c>--volume=0</c> silences the output whatever the levels
/// say, because the developer gain sits on bus 0 and the mix multiplies underneath it.
/// ⚠ The store is pointed at this suite's own scratch directory and restored in a finally, and
/// bus 0's gain is captured and put back the same way, so nothing here reads or writes the options
/// saved at this machine's controls and no later suite inherits a bus this one wrote.</summary>
internal static class AudioLaunchSuites
{
    // The dB an inaudible bus sits at, the floor both AudioMix and Launcher convert a gain of 0
    // through. A repo run with no --volume= lands bus 0 here, which is what keeps it silent.
    private const float SilenceDb = -80f;

    // Slack for a dB comparison, far under the 6 dB a halved level moves a bus by.
    private const float DbSlack = 0.01f;

    // Loud enough that the mix cannot be mistaken for the shipped defaults, and no level is the
    // same as another, so a category reading its neighbour's level fails rather than passing.
    private const int SavedMaster = 80;
    private const int SavedMusic = 15;
    private const int SavedEffects = 95;
    private const int SavedVoice = 40;

    [Suite("audio-levels-launch",
        "The saved mix at launch, the ladder the four levels sit in: a saved level reaches its own "
        + "category bus through the read a plain launch makes, a --det launch reads no saved level "
        + "and leaves all three buses on the shipped defaults, --volume=0 resolves the developer "
        + "gain to silence while the loudest possible saved mix stands on the child buses so the "
        + "output is silent whatever the levels say, --volume=1 leaves bus 0 at its resting gain, "
        + "and the flag beats the audio.volume config key in both directions")]
    internal static void AudioLevelsLaunch(TestContext ctx)
    {
        string dir = Path.Combine(ctx.ScratchDir, "audio-levels-launch");
        if (Directory.Exists(dir))
        {
            Directory.Delete(dir, recursive: true);
        }

        Directory.CreateDirectory(dir);
        string? previous = OptionsStore.DirectoryOverride;
        float developer = AudioServer.GetBusVolumeDb(0);
        OptionsStore.DirectoryOverride = dir;
        try
        {
            OptionsStore.UserOptions().Save(new OptionsDef
            {
                AudioMaster = SavedMaster,
                AudioMusic = SavedMusic,
                AudioEffects = SavedEffects,
                AudioVoice = SavedVoice,
            });

            CheckSavedReachesTheBus(ctx);
            CheckDetReadsNone(ctx);
            CheckDeveloperGainWins(ctx);
        }
        finally
        {
            OptionsStore.DirectoryOverride = previous;
            AudioServer.SetBusVolumeDb(0, developer);
            AudioMix.Apply();
        }
    }

    // The saved rung: a plain command line reads the file and each level lands on its own bus. The
    // read is the launch's own (Launcher._Ready calls exactly this pair), so a level that stopped
    // reaching the mixer fails here rather than only at the controls.
    private static void CheckSavedReachesTheBus(TestContext ctx)
    {
        var levels = Levels(System.Array.Empty<string>());
        ctx.Check(levels == new AudioLevels(SavedMaster, SavedMusic, SavedEffects, SavedVoice),
            $"a plain launch reads all four saved levels ({Describe(levels)})");
        Apply(levels);
        CheckBus(ctx, AudioBuses.Music, SavedMusic, SavedMaster);
        CheckBus(ctx, AudioBuses.Effects, SavedEffects, SavedMaster);
        CheckBus(ctx, AudioBuses.Voice, SavedVoice, SavedMaster);
    }

    // The --det rung. ⚠ Asserted where the drop is written rather than by its pixels
    // (docs/verification.md's DET-14): a golden sweep cannot see this fail, since a shot of a menu
    // is the same picture at every mix. The saved file is nothing like the defaults on purpose, so
    // "reads as never set" cannot pass by reading a file that says the defaults anyway.
    private static void CheckDetReadsNone(TestContext ctx)
    {
        var levels = Levels(new[] { "--det" });
        ctx.Check(levels == default(AudioLevels),
            $"a --det launch reads no saved level at all ({Describe(levels)})");
        Apply(levels);
        CheckBus(ctx, AudioBuses.Music, AudioMix.DefaultMusic, AudioMix.DefaultMaster);
        CheckBus(ctx, AudioBuses.Effects, AudioMix.DefaultEffects, AudioMix.DefaultMaster);
        CheckBus(ctx, AudioBuses.Voice, AudioMix.DefaultVoice, AudioMix.DefaultMaster);
    }

    // The developer rung, and the one this whole suite exists for: --volume= resolves bus 0, the
    // mix resolves the three buses under it, and the output is their product. A saved mix at full
    // volume must therefore not lift a run the flag silenced, which is the failure the "never write
    // bus 0" rule in AudioMix exists to prevent and the one a passing mix would otherwise hide.
    private static void CheckDeveloperGainWins(TestContext ctx)
    {
        OptionsStore.UserOptions().Save(new OptionsDef
        {
            AudioMaster = AudioMix.MaxLevel,
            AudioMusic = AudioMix.MaxLevel,
            AudioEffects = AudioMix.MaxLevel,
            AudioVoice = AudioMix.MaxLevel,
        });
        var loudest = Levels(new[] { "--volume=0" });
        ctx.Check(loudest == new AudioLevels(100, 100, 100, 100),
            $"the loudest possible mix is saved and read ({Describe(loudest)})");

        // ⚠ The launch's own order, the developer gain first and the mix second: reversed, this
        // suite would write bus 0 last and pass over a mix that had just overwritten it, which is
        // exactly the failure it exists to catch.
        float silenced = MasterVolume.Resolve(Spec(new[] { "--volume=0" }).Volume, exported: false);
        ctx.Check(silenced == 0f, $"--volume=0 resolves the developer gain to 0 ({silenced:0.###})");
        float developerDb = MasterVolume.VolumeDb(silenced);
        AudioServer.SetBusVolumeDb(0, developerDb);
        Apply(loudest);

        float standing = AudioServer.GetBusVolumeDb(0);
        ctx.Check(Math.Abs(standing - developerDb) <= DbSlack,
            $"the loudest saved mix left bus 0 where --volume=0 put it db={standing:0.###} want={developerDb:0.###}");
        foreach (string bus in new[] { AudioBuses.Music, AudioBuses.Effects, AudioBuses.Voice })
        {
            float output = standing + AudioServer.GetBusVolumeDb(AudioServer.GetBusIndex(bus));
            ctx.Check(output <= SilenceDb + DbSlack,
                $"{bus} at level 100 under master 100 is still silent at --volume=0 output_db={output:0.###}");
        }

        // The other end of the same flag: it resolves to the resting gain, which is the value
        // ApplyMasterVolume leaves the bus untouched for, so a full-volume launch stays
        // byte-identical to one with no volume path at all.
        float full = MasterVolume.Resolve(Spec(new[] { "--volume=1.0" }).Volume, exported: false);
        ctx.Check(Mathf.IsEqualApprox(full, MasterVolume.Unattenuated),
            $"--volume=1.0 resolves to the resting gain ({full:0.###})");
        // An exported build's fallback is audible and a repo run's is silent, and the flag outranks
        // both, so the same command line answers the same gain either way.
        ctx.Check(MasterVolume.Resolve(Spec(new[] { "--volume=0" }).Volume, exported: true) == 0f,
            $"and --volume=0 silences an exported build too, whose fallback is the resting gain");
        ctx.Note($"--volume=0 holds a 100/100/100/100 saved mix at or below {SilenceDb:0} dB of output");
    }

    // The launch's own read of the levels: the spec a command line parses to, then the one reader
    // Launcher._Ready calls between ApplyMasterVolume and AudioMix.Apply.
    private static AudioLevels Levels(string[] args) => AudioMix.SavedLevels(Spec(args).Det);

    private static SessionSpec Spec(string[] args) => SessionSpec.Parse(args);

    private static void Apply(AudioLevels levels) =>
        AudioMix.Apply(levels.Master, levels.Music, levels.Effects, levels.Voice);

    private static void CheckBus(TestContext ctx, string bus, int level, int master)
    {
        int index = AudioServer.GetBusIndex(bus);
        float db = index < 0 ? float.NaN : AudioServer.GetBusVolumeDb(index);
        float want = AudioMix.VolumeDb(level, master);
        ctx.Check(index > 0 && Math.Abs(db - want) <= DbSlack,
            $"{bus} carries level {level} under master {master} db={db:0.###} want={want:0.###}");
    }

    private static string Describe(AudioLevels levels) =>
        $"master={Level(levels.Master)} music={Level(levels.Music)} effects={Level(levels.Effects)} voice={Level(levels.Voice)}";

    private static string Level(int? level) =>
        level?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unset";
}
