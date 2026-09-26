using System;
using System.Collections.Generic;
using System.Text;
using CSVM.Mech3;
using Godot;

namespace CSVM.Testing;

/// <summary>The positional level guard: a live <see cref="WorldSounds"/> emitter is driven off the
/// decoded curve rather than an engine attenuation model. The law itself is pinned as a table by
/// <c>CSVM.Tests</c>; what only a live run can see is the wiring, that <c>Tick</c> reaches the
/// player at all, that it measures to the nearest pane and not to player one, and that no
/// <c>MaxDistance</c> silently cuts a sound inside its authored audible radius.</summary>
internal static class WorldSoundSuites
{
    // Slack on a dB comparison, far under the 10 dB one doubling of the reach costs.
    private const float DbSlack = 0.05f;

    // Two listeners a long way apart: the far one would give a wrong answer if the nearest rule
    // were dropped, since every distance under test sits past the cull from there.
    private static readonly Vector3 FarEar = new(0f, 0f, 20000f);

    [Suite("world-sound-falloff",
        "a live ambient emitter's level over distance: the pooled AudioStreamPlayer3D carries no "
        + "engine attenuation model and no MaxDistance, Tick sets its volume from the decoded "
        + "RANGE curve at the nearest of several listeners, the level holds at the authored full "
        + "volume through the shelf, falls ten decibels per doubling of the reach past it to thirty "
        + "down at the audible radius, tails from there to the floor over the last tenth, and a "
        + "one-shot spawned out of range is already at the floor before any frame runs")]
    internal static void WorldSoundFalloff(TestContext ctx)
    {
        ctx.RequireData(ctx.ZrdrPath, $"zrdr archive");
        ctx.RequireData(ctx.SoundsPath, $"sound archive (soundsh)");

        // Every level below is the decoded curve at the definition's own radii. This suite reads
        // at 1, not at the reach the session ships.
        using var authored = TestContext.AtAuthoredSoundRadii();

        var defs = SoundDefs.Load(ctx.ZrdrPath);
        var groups = SoundDefs.LoadGroups(ctx.ZrdrPath);
        using var archive = new SoundArchive(ctx.SoundsPath);
        var report = new StringBuilder();

        WorldSounds? sounds = null;
        Node3D? host = null;
        try
        {
            sounds = new WorldSounds(defs, groups)
            {
                Name = "FalloffWorldSounds",
                Loader = (def, warn) => archive.Find(def.WavName, def.Looped, warn),
            };
            ctx.Host.AddChild(sounds);

            string? name = FirstPositional(sounds, defs, out var def);
            ctx.Check(name != null && def != null, $"a positional definition resolved to an emitter");
            if (name == null || def == null)
            {
                return;
            }
            var handle = sounds.Create(name);
            ctx.Check(handle != null, $"'{name}' built an emitter");
            if (handle == null)
            {
                return;
            }

            host = new Node3D { Name = "FalloffHost" };
            ctx.Host.AddChild(host);
            sounds.Attach(handle, host);
            sounds.SetActive(handle, true);

            sounds.SetListeners(() => new[] { FarEar, Vector3.Zero });

            report.AppendLine($"def={name} range={def.RangeMin:0}-{def.RangeMax:0} volume={def.Volume:0.##}");
            float band = def.RangeMax - def.RangeMin;
            var walk = new List<float>
            {
                def.RangeMin,
                def.RangeMin + (0.125f * band),
                def.RangeMin + (0.25f * band),
                def.RangeMin + (0.5f * band),
                def.RangeMax,
                def.RangeMax * 1.05f,
                def.RangeMax * 1.2f,
            };
            foreach (float distance in walk)
            {
                host.GlobalPosition = new Vector3(distance, 0f, 0f);
                sounds.Tick();
                float want = SoundFalloff.GainDb(distance, def.RangeMin, def.RangeMax, def.Volume);
                float got = Player(sounds).VolumeDb;
                report.AppendLine($"  dist={distance:0} want={want:0.00} dB got={got:0.00} dB");
                ctx.Check(MathF.Abs(want - got) <= DbSlack,
                    $"'{name}' at {distance:0} m sits at {want:0.00} dB actual={got:0.00}");
            }

            var player = Player(sounds);
            ctx.Check(player.AttenuationModel == AudioStreamPlayer3D.AttenuationModelEnum.Disabled,
                $"the emitter carries no engine attenuation model actual={player.AttenuationModel}");
            ctx.Check(player.MaxDistance == 0f,
                $"the emitter carries no MaxDistance, whose linear fade cuts it early actual={player.MaxDistance:0.##}");

            // A one-shot has to be right before the first frame: a short cue can outlive no Tick.
            int started = sounds.OneShotsStarted;
            sounds.PlayOneShot(name, new Vector3(def.RangeMax * 2f, 0f, 0f), new Random(7));
            ctx.Check(sounds.OneShotsStarted > started, $"a one-shot of '{name}' started");
            float shotDb = LastOneShot(sounds).VolumeDb;
            ctx.Check(MathF.Abs(SoundFalloff.FloorDb - shotDb) <= DbSlack,
                $"a one-shot spawned past the cull is silent before any frame runs actual={shotDb:0.00}");
        }
        finally
        {
            sounds?.FlushOneShots();
            host?.Free();
            sounds?.Free();
        }

        ctx.WriteArtifact("test-world-sound-falloff.txt", report.ToString());
    }

    // The emitter player is the node's first child; the one-shots are appended after it.
    private static AudioStreamPlayer3D Player(WorldSounds sounds) =>
        (AudioStreamPlayer3D)sounds.GetChild(0);

    private static AudioStreamPlayer3D LastOneShot(WorldSounds sounds) =>
        (AudioStreamPlayer3D)sounds.GetChild(sounds.GetChildCount() - 1);

    // The first 3D definition with a WAV behind it, rather than a hard-coded name: which
    // definitions resolve is a property of the player's own install.
    private static string? FirstPositional(WorldSounds sounds,
        IReadOnlyDictionary<string, SoundDef> defs, out SoundDef? found)
    {
        foreach (var (name, def) in defs)
        {
            if (def.Is3D && def.RangeMax > def.RangeMin && sounds.HasStream(name))
            {
                found = def;
                return name;
            }
        }
        found = null;
        return null;
    }
}
