using System;
using System.Collections.Generic;
using Godot;

namespace CSVM.Flight.Modes;

/// <summary>
/// Where a Dogfight seat comes back: the spawn rotation over the scenario's own list. It owns the
/// per-seat index ledger the opening spawn sets (one living seat per point) and picks a respawn
/// point that is roomy against the living field, weighing the killer heaviest, so no seat can be
/// camped. Engine-free like <see cref="VersusMatch"/>, drawing from a caller-supplied
/// <see cref="Random"/>, so a session replays exactly under <c>--det</c>.
/// ⚠ Never an abreast grid: the whole field on one heading 60 m apart is an instant head-on merge,
/// which is why <c>StartGrid</c> is not built for this mode.</summary>
public sealed class VersusSpawnRotation
{
    /// <summary>What the killer's distance to a candidate is divided by before it competes with the
    /// rest of the field's, so coming back beside the pilot who just scored costs a candidate twice
    /// what coming back beside anyone else does.</summary>
    public const float KillerWeight = 2f;

    /// <summary>The share of the roomiest candidate's clearance a candidate must still reach to join
    /// the draw. Below 1 so the point rotates instead of being one arithmetic maximum every time,
    /// high enough that no draw puts the seat back into the fight it just left.</summary>
    public const float RoomyShare = 0.6f;

    private readonly IReadOnlyList<SpawnPoint> _spawns;
    private readonly Random _rng;
    private readonly int[] _opening;
    private readonly int[] _held;
    private readonly bool[] _owedOpening;

    private VersusSpawnRotation(IReadOnlyList<SpawnPoint> spawns, int spawnBase, int seats, Random rng)
    {
        _spawns = spawns;
        _rng = rng;
        _opening = new int[seats];
        _held = new int[seats];
        _owedOpening = new bool[seats];
        for (int s = 0; s < seats; s++)
        {
            // The opening ledger IS SpawnPicker.ChooseStarts's walk: seat s takes the next list
            // entry after the session's base, which is the spacing rule this rotation preserves.
            _opening[s] = (spawnBase + s) % spawns.Count;
            _held[s] = _opening[s];
        }
    }

    /// <summary>How many seats the rotation tracks.</summary>
    public int SeatCount => _held.Length;

    /// <summary>How many points it rotates over.</summary>
    public int PointCount => _spawns.Count;

    /// <summary>The rotation for a match over <paramref name="spawns"/>, or null when there is no
    /// list to rotate over, which leaves every seat on the one pose it was given.</summary>
    public static VersusSpawnRotation? For(IReadOnlyList<SpawnPoint>? spawns, int spawnBase,
        int seatCount, Random rng) =>
        spawns is { Count: > 0 } && seatCount > 0
            ? new VersusSpawnRotation(spawns, Math.Max(0, spawnBase), seatCount, rng)
            : null;

    /// <summary>The spawn list index <paramref name="seat"/> holds, its opening point until a
    /// downing moves it. A seat outside the field reads 0.</summary>
    public int IndexOf(int seat) => seat >= 0 && seat < _held.Length ? _held[seat] : 0;

    /// <summary>Where <paramref name="seat"/> comes back, and the ledger write reserving it.
    /// <paramref name="field"/> is one entry per seat, null where that seat is not in the fight.
    /// The pick is drawn between the roomiest candidates, so it rotates; it is never the point this
    /// seat was just downed at and never a point a living seat holds, while
    /// <paramref name="killer"/> weighs <see cref="KillerWeight"/> times the rest of the field.</summary>
    public SpawnPoint Choose(int seat, IReadOnlyList<Vector3?> field, int? killer)
    {
        if (seat < 0 || seat >= _held.Length)
            return _spawns[0];
        if (_owedOpening[seat])
        {
            _owedOpening[seat] = false;
            _held[seat] = _opening[seat];
            return _spawns[_held[seat]];
        }
        _held[seat] = Pick(seat, field, killer);
        return _spawns[_held[seat]];
    }

    /// <summary>A fresh round: every seat back on its opening point, which the next
    /// <see cref="Choose"/> for that seat hands back before the rotation resumes.</summary>
    public void Restart()
    {
        for (int s = 0; s < _held.Length; s++)
        {
            _held[s] = _opening[s];
            _owedOpening[s] = true;
        }
    }

    // The choice itself: the candidate set under the spacing rule, then a draw among those within
    // RoomyShare of the roomiest. It relaxes the downed-point exclusion first and the spacing rule
    // second, so a list shorter than the field still answers rather than failing.
    private int Pick(int seat, IReadOnlyList<Vector3?> field, int? killer)
    {
        var candidates = new List<int>(_spawns.Count);
        for (int i = 0; i < _spawns.Count; i++)
            if (i != _held[seat] && !HeldByLiving(seat, i, field))
                candidates.Add(i);
        if (candidates.Count == 0)
            for (int i = 0; i < _spawns.Count; i++)
                if (!HeldByLiving(seat, i, field))
                    candidates.Add(i);
        if (candidates.Count == 0)
            for (int i = 0; i < _spawns.Count; i++)
                candidates.Add(i);

        var room = new float[candidates.Count];
        float best = 0f;
        for (int c = 0; c < candidates.Count; c++)
        {
            room[c] = Clearance(candidates[c], seat, field, killer);
            best = Math.Max(best, room[c]);
        }
        // An empty sky leaves every candidate at MaxValue, and the floor then admits all of them:
        // with nobody to be far from, the draw is the whole rotation.
        float floor = best >= float.MaxValue ? 0f : best * RoomyShare;
        var pool = new List<int>(candidates.Count);
        for (int c = 0; c < candidates.Count; c++)
            if (room[c] >= floor)
                pool.Add(candidates[c]);
        return pool[_rng.Next(pool.Count)];
    }

    // Whether another seat still in the fight holds this list entry, which is the spacing rule the
    // opening spawn set: one living seat per point.
    private bool HeldByLiving(int seat, int index, IReadOnlyList<Vector3?> field)
    {
        for (int s = 0; s < _held.Length; s++)
            if (s != seat && _held[s] == index && s < field.Count && field[s] != null)
                return true;
        return false;
    }

    // How much room a candidate has: the nearest living opponent's distance, with the killer's
    // divided by KillerWeight so that one pilot dominates the reading.
    private float Clearance(int index, int seat, IReadOnlyList<Vector3?> field, int? killer)
    {
        var at = _spawns[index].Position;
        float nearest = float.MaxValue;
        for (int s = 0; s < field.Count; s++)
        {
            if (s == seat || field[s] is not { } other)
                continue;
            float away = at.DistanceTo(other);
            if (killer is { } k && k == s)
                away /= KillerWeight;
            nearest = Math.Min(nearest, away);
        }
        return nearest;
    }
}
