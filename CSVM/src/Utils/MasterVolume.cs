using Godot;

namespace CSVM.Utils;

/// <summary>
/// The developer output gain, the whole of what bus 0 carries: the <c>--volume=</c> flag over the
/// <c>audio.volume</c> config key over a default that is silence in a repo run and the resting gain
/// in an exported one. ⚠ The player's four saved levels are no part of this. They multiply on the
/// three child buses underneath it (<see cref="AudioMix"/>), so a level saved at the controls
/// cannot un-silence a scripted run whatever it says, and the two gains reach the output as a
/// product rather than as alternatives. Resolution only: <c>Session/Launch/Launcher.cs</c> stays the one
/// caller that writes the bus, and is where a full-volume launch leaves it untouched.
/// </summary>
public static class MasterVolume
{
    /// <summary>The bus's own resting gain (0 dB). A launch resolving to this leaves the bus
    /// untouched, keeping it byte-identical in output and console log to a launch that never had a
    /// volume path at all.</summary>
    public const float Unattenuated = 1f;

    /// <summary>The gain a REPO run takes where neither the flag nor the config key says otherwise.
    /// Silence: launches are quiet unless someone asks for sound (the Run scripts pass
    /// <c>--volume=1.0</c>), so a scripted or agent run never sounds by accident. An exported build
    /// takes <see cref="Unattenuated"/> instead, since a recipient who double-clicks the exe passes
    /// no flag and has no config file to write one into.</summary>
    public const float RepoDefault = 0f;

    // Gain floor for the dB conversion, since LinearToDb(0) is negative infinity. -80 dB is
    // inaudible, which is the whole point of --volume=0.
    private const float Floor = 0.0001f;

    /// <summary>The gain a launch resolves to, <paramref name="asked"/> being the command line's
    /// <c>--volume=</c> where it carried one. ⚠ The config key is read even where the flag beats
    /// it, so it self-registers for <c>--dump-config</c> rather than appearing only in the runs
    /// that leave the flag off.</summary>
    public static float Resolve(float? asked, bool exported)
    {
        float configured = Config.GetFloat("audio.volume", exported ? Unattenuated : RepoDefault);
        return asked ?? configured;
    }

    /// <summary>That gain as a bus volume in dB, floored so 0 reads -80 dB rather than negative
    /// infinity.</summary>
    public static float VolumeDb(float volume) => Mathf.LinearToDb(Mathf.Max(volume, Floor));
}
