using System;
using System.Collections.Generic;
using Godot;

namespace CSVM.Utils;

/// <summary>
/// The session's randomness policy: one master seed, and a named generator per subsystem derived
/// from it via <c>splitmix64(master ^ fnv1a(name))</c>, independent across subsystems, so a draw
/// added to one cannot shift another's. Pinned for a deterministic run, time-seeded otherwise, and
/// advanced per flight (<see cref="SortieSeed"/>) so flying again is a new mission; the resolved
/// value logs for replay via <c>--seed=</c>. The streams are the constants below.
/// ⚠ Never derive a subsystem seed via <see cref="string.GetHashCode"/>. .NET randomizes it per
/// process, so the stream would differ every launch and silently break <c>--det</c>.
/// ⚠ Only the DRAW ORDER within one subsystem's own stream is significant; that order is
/// deterministic only because <see cref="GameClock"/>'s fixed step makes every consumer run its
/// draws in the same sequence run to run.
/// </summary>
public static class Rng
{
    /// <summary>The master a pinned session uses when no <c>--seed=</c> is given.</summary>
    public const ulong DefaultSeed = 1;

    // Subsystem names. Constants rather than literals at the call sites: a typo would silently
    // open a second, differently-seeded stream instead of failing.
    public const string Weapons = "weapons";
    public const string FlightAudio = "flightaudio";
    public const string Spawn = "spawn";
    public const string Paint = "paint";
    public const string Anim = "anim";
    public const string Crash = "crash";
    public const string Effects = "effects";
    // AI behaviour draws (patrol-net branch choices; later waves add theirs). Its own stream so
    // an AI's route never shifts what the weapons or paint code rolls, and vice versa.
    public const string Ai = "ai";
    public const string Puffer = "puffer";
    public const string Clouds = "clouds";
    // The cloud field's per-sprite fade band (FogVolumeClutter). Its own stream, not
    // Rng.Clouds: drawn from the same stream as the placements, one band draw per sprite would
    // shift every later placement, and the field's counts and positions are what the pinned
    // chapter totals and the golden shots stand on.
    public const string CloudBands = "cloudbands";
    // The cloud field's remake-only X/Z jitter (--cloud-jitter). Its own stream for CloudBands'
    // reason: every knob value then lays the same decoded field and moves only the offsets.
    public const string CloudJitter = "cloudjitter";
    public const string Precip = "precip";
    // The mission's global wind gust (WorldWind). Its own stream, not Puffer's: the wind
    // is one random walk for the whole world, stepped once per frame by WeatherRig, while
    // Rng.Puffer is drawn per emitter at spawn, sharing one would make every puffer's scatter a
    // function of how many frames the wind had been blowing.
    public const string Wind = "wind";
    // The plane wobble's per-shot fire-kick steps (PlaneShake random-walk accumulator, BL-266(a)
    // branch). Its own stream so a draw here never shifts what another subsystem rolls; under
    // --det it is a pure function of the master, so the gun-buzz wobble replays exactly.
    public const string Shake = "shake";
    // The hangar's rolled plane names (PlaneNameTables). Its own stream so naming a plane never
    // shifts what the paint or spawn code rolls; under --det the offered name replays exactly,
    // which is what makes a hangar screenshot reproducible.
    public const string PlaneName = "planename";
    // The static cameras' placement draws (StaticCameras: the death spot, and the flyby's angle,
    // radius, interval, watch time and switch distance). Its own stream because the death camera
    // is placed on the same frame the crash rig scatters a wreck, and sharing Rng.Crash would make
    // every piece of that wreck a function of where the camera happened to land.
    public const string Camera = "camera";
    // The Dogfight respawn rotation's draws (VersusSpawnRotation). Its own stream, and read
    // through IntSeedFor rather than Stream, so a match's respawn picks take nothing out of
    // Rng.Spawn: a draw there would shift the launch spawn index every pinned golden stands on.
    public const string VersusSpawn = "versusspawn";
    // The Danger Zone photograph's eye scatter (DangerZonePhotograph). Its own stream, so a
    // zone crossed never shifts the static cameras' spots or anything else a run rolls.
    public const string Photograph = "photograph";

    private static readonly Dictionary<string, RandomNumberGenerator> Streams = new(StringComparer.Ordinal);

    /// <summary>The seed every subsystem stream derives from.</summary>
    public static ulong Master { get; private set; }

    /// <summary>True when the master was chosen rather than drawn from the clock, a deterministic
    /// run. Purely informational; nothing branches on it.</summary>
    public static bool Pinned { get; private set; }

    /// <summary>Re-derives every subsystem stream from <paramref name="master"/>. Called once per
    /// session build, before anything draws, so a session's content is a function of its master
    /// alone and never of how long the previous session ran. A pinned run reuses the same master on
    /// an in-process restart (menu → fly → menu → fly) and so repeats exactly; an unpinned one is
    /// handed the next <see cref="SortieSeed"/> and so gets a fresh mission.</summary>
    public static void Reset(ulong master, bool pinned)
    {
        Master = master;
        Pinned = pinned;
        Streams.Clear();
        // The net for any draw this class does not own: Godot's global RNG behind GD.Randf/Randi.
        GD.Seed(master);
    }

    /// <summary>The master for the <paramref name="sortie"/>'th session of an unpinned run, so
    /// flying again from the launchscreen gets a fresh spawn, fresh opposition and fresh liveries
    /// instead of replaying the last one. Pure and clock-free: the process seed printed at launch
    /// plus the sortie index reconstructs any mission, and each value is logged as it is used so
    /// <c>--seed=</c> can pin it. Mixed rather than added: consecutive masters must not hand the
    /// subsystems seeds that differ in one bit.</summary>
    public static ulong SortieSeed(ulong processSeed, int sortie) => Mix(processSeed + (ulong)sortie);

    /// <summary>A master drawn from the clock, the unpinned default.</summary>
    public static ulong TimeSeed()
    {
        var rng = new RandomNumberGenerator();
        rng.Randomize();
        return rng.Seed;
    }

    /// <summary>Reseeds every live subsystem stream to its master-derived start, IN PLACE: unlike
    /// <see cref="Reset"/> it keeps each generator object, so a consumer holding a captured
    /// reference is rewound too rather than left drawing on an orphan.
    /// ⚠ The in-engine harness only, before each suite: without it a suite's verdict is a function
    /// of how many draws its predecessors left in the stream. A session must use
    /// <see cref="Reset"/>, whose master can change.</summary>
    public static void Rewind()
    {
        foreach (var (name, rng) in Streams)
        {
            rng.Seed = SeedFor(name);
        }
        GD.Seed(Master);
    }

    /// <summary>The named subsystem's seed. Pure: the same name and master always give the same
    /// value, whatever else has drawn.</summary>
    public static ulong SeedFor(string subsystem) => Mix(Master ^ Fnv1a(subsystem));

    /// <summary>The named subsystem's seed as a <see cref="System.Random"/> constructor takes it.</summary>
    public static int IntSeedFor(string subsystem)
    {
        ulong s = SeedFor(subsystem);
        return (int)(s ^ (s >> 32));
    }

    /// <summary>The named subsystem's shared generator, created on first use. Callers that draw
    /// from a hot path should hold the reference rather than re-resolving per draw.</summary>
    public static RandomNumberGenerator Stream(string subsystem)
    {
        if (!Streams.TryGetValue(subsystem, out var rng))
        {
            rng = new RandomNumberGenerator { Seed = SeedFor(subsystem) };
            Streams[subsystem] = rng;
        }
        return rng;
    }

    /// <summary>A fresh seed off the named subsystem's stream, for a per-instance generator (one
    /// puffer, one cloud field, one crash rig). Successive calls differ, and the whole series is a
    /// function of the master.</summary>
    public static int NewIntSeed(string subsystem) => (int)Stream(subsystem).Randi();

    /// <summary>A per-instance <see cref="System.Random"/> off the named subsystem's stream, the
    /// shape the effects code already uses.</summary>
    public static Random NewSystemRandom(string subsystem) => new(NewIntSeed(subsystem));

    /// <summary>A per-cell <see cref="System.Random"/> keyed by coordinate, not draw order: the
    /// seed is a pure function of the subsystem, the master and the coordinates alone. Use this
    /// instead of <see cref="NewSystemRandom(string)"/> when the set being drawn is itself a
    /// runtime computation over coordinates.
    /// ⚠ A shared sequential stream would reroll every surviving cell if the cell count or
    /// enumeration order changed, which is why this stays keyed.</summary>
    // Deliberately touches only System.Random, never Godot.RandomNumberGenerator/GD.Seed, so it
    // stays callable off-engine and pinnable directly in CSVM.Tests.
    public static Random NewSystemRandom(string subsystem, int cellX, int cellZ)
    {
        ulong h = Mix(SeedFor(subsystem) ^ Fnv1a($"{cellX},{cellZ}"));
        return new Random((int)(h ^ (h >> 32)));
    }

    // FNV-1a over the name's UTF-16 code units, byte at a time.
    private static ulong Fnv1a(string s)
    {
        ulong h = 14695981039346656037UL;
        foreach (char c in s)
        {
            h = (h ^ (byte)c) * 1099511628211UL;
            h = (h ^ (byte)(c >> 8)) * 1099511628211UL;
        }
        return h;
    }

    // SplitMix64's finalizer: without it, masters 1 and 2 would hand every subsystem a pair of
    // seeds differing in one bit, and a weak generator can start those two streams close together.
    private static ulong Mix(ulong z)
    {
        z += 0x9E3779B97F4A7C15UL;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }
}
