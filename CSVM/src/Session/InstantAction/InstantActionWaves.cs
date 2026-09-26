using System;
using System.Collections.Generic;
using CSVM.Flight.Modes;
using Godot;

namespace CSVM.Session.InstantAction;

/// <summary>The decoded wave sequencer's own selection and trigger logic (<c>FUN_0045b9d0</c>):
/// pure state over <see cref="Start"/>/<see cref="Step"/> calls, no <c>GD.*</c>, no <c>Godot.</c>
/// node, no clock, so <c>CSVM.Tests</c> pins it off-engine. The caller feeds it its own "still
/// alive" counts and takes back which wave to activate and where.
/// ⚠ Only <see cref="ChooseWaveSpawn"/>/<see cref="FanOffset"/> skip <c>zeppelin_run</c>, that
/// mode's teleport is a JMP-past-the-block exclusive alternative, its wave arrival the generator
/// arm instead. <see cref="Start"/>/<see cref="Step"/> run on every mode including this one;
/// nothing here checks the mission type, so the caller routes what they hand back.</summary>
public sealed class InstantActionWaves
{
    /// <summary>500 m, squared (the decoded float at <c>0x006036c0</c>), the teleport's own
    /// minimum distance from "the player". Decision 8 extends that to the NEAREST of every human
    /// for splitscreen, named as the extension it is.</summary>
    public const float MinSpawnDistanceSquared = 250000f;

    private readonly IReadOnlyList<int> _waveSizes; // NumEnemies, waves 1..4 at index 0..3

    /// <param name="waveSizes">Exactly 4 entries, <see cref="Mech3.InstantActionDef.Waves"/>'
    /// own <c>NumEnemies</c>, in order.</param>
    public InstantActionWaves(IReadOnlyList<int> waveSizes)
    {
        if (waveSizes.Count != 4)
        {
            throw new ArgumentException("Instant Action always carries exactly 4 waves",
                nameof(waveSizes));
        }
        _waveSizes = waveSizes;
    }

    /// <summary>0 before <see cref="Start"/>; 1–4 while a wave is current; 5 once every
    /// configured wave is exhausted (see <see cref="Finished"/>), the counter never wraps or
    /// restarts, matching the original's own one-way <c>DAT_00718cd0</c>.</summary>
    public int CurrentWave { get; private set; }

    /// <summary>True once the counter has advanced past wave 4. No 5th wave, ever.</summary>
    public bool Finished => CurrentWave > 4;

    /// <summary><see cref="CurrentWave"/>'s own configured enemy count, 0 before
    /// <see cref="Start"/> and once <see cref="Finished"/>.</summary>
    public int CurrentWaveSize => CurrentWave is >= 1 and <= 4 ? _waveSizes[CurrentWave - 1] : 0;

    /// <summary>The teleport arm's own two-step draw (detail: this module's docs/architecture.md
    /// entry): collect every entry at or beyond <see cref="MinSpawnDistanceSquared"/> from the
    /// nearest human, then take <c>draw % n</c> over that collection.
    /// ⚠ Falls back to the literal first entry, index 0, when the collection is empty.</summary>
    public static (int Index, SpawnPoint Point) ChooseWaveSpawn(
        IReadOnlyList<SpawnPoint> spawns, IReadOnlyList<Vector3> humanPositions, uint draw)
    {
        var far = new List<int>();
        for (int i = 0; i < spawns.Count; i++)
        {
            float nearest = float.MaxValue;
            foreach (var human in humanPositions)
            {
                float distSq = spawns[i].Position.DistanceSquaredTo(human);
                if (distSq < nearest)
                {
                    nearest = distSq;
                }
            }
            if (nearest >= MinSpawnDistanceSquared)
            {
                far.Add(i);
            }
        }
        if (far.Count == 0)
        {
            return (0, spawns[0]);
        }
        int idx = far[(int)(draw % (uint)far.Count)];
        return (idx, spawns[idx]);
    }

    /// <summary>The teleport's own fan (A4, the same 100 m / 45° pattern the wingmen use):
    /// the wave's FIRST member (<paramref name="memberIndex"/> 0) sits exactly on the spawn
    /// point; member <c>k</c> after it (<c>k</c> = <paramref name="memberIndex"/> − 1) sits
    /// <c>100 · ((k &gt;&gt; 1) + 1)</c> m out at ±45° off the spawn heading, sign <c>+</c> when
    /// <c>k &amp; 3</c> is 1 or 2. Six members land at 0, 100, 100, 200, 200, 300 m on sides
    /// (none), −, +, +, −, −.</summary>
    public static (float MetresOut, float OffsetDeg) FanOffset(int memberIndex)
    {
        if (memberIndex == 0)
        {
            return (0f, 0f);
        }
        int k = memberIndex - 1;
        float metres = 100f * ((k >> 1) + 1);
        float sign = (k & 3) is 1 or 2 ? 1f : -1f;
        return (metres, sign * 45f);
    }

    /// <summary>Mission start: the original's own "wave 1 spawns live", folded into the same
    /// build-inert-then-activate path every later wave takes (Decision 6). Cascades past any
    /// leading 0-enemy waves, a wave with nothing configured is immediately clear, matching the
    /// original's own walk finding no member of that group to wait on. Returns the first wave
    /// actually worth activating, or 0 with <see cref="Finished"/> true when every configured
    /// wave is empty.</summary>
    public int Start()
    {
        CurrentWave = 1;
        SkipEmpty();
        return Finished ? 0 : CurrentWave;
    }

    /// <summary>One sequencer tick: 0 while <paramref name="aliveInCurrentWave"/> is positive,
    /// else the counter advances (cascading past empty waves) and the caller activates the
    /// returned wave. 0 and a no-op once <see cref="Finished"/>, no advance past wave 4.
    /// ⚠ On zeppelin_run "alive" means NOT crashed, not <c>FlightController.InPlay</c>, a
    /// member still parked in the bay counts as present, the original's own decoded rule.</summary>
    public int Step(int aliveInCurrentWave)
    {
        if (Finished || aliveInCurrentWave > 0)
        {
            return 0;
        }
        CurrentWave++;
        SkipEmpty();
        return Finished ? 0 : CurrentWave;
    }

    private void SkipEmpty()
    {
        while (CurrentWave is >= 1 and <= 4 && _waveSizes[CurrentWave - 1] == 0)
        {
            CurrentWave++;
        }
    }
}
