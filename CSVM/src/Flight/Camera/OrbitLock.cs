using System.Collections.Generic;
using Godot;

namespace CSVM.Flight.Camera;

/// <summary>
/// The re-lock rule behind <see cref="SpectatorCamera"/>'s target key: nearest first, then a step
/// outward on each further press. Split out as positions rather than nodes so it is testable
/// without a running engine, the same shape <see cref="CSVM.Pads.AssignPads(int,
/// System.Collections.Generic.IReadOnlyList{int})"/> uses.
/// </summary>
public static class OrbitLock
{
    /// <summary>The index in <paramref name="targets"/> to lock onto next, or −1 when there is
    /// nothing to lock. <paramref name="current"/> is the index held now, or −1 for none: from
    /// nothing it answers the nearest to <paramref name="eye"/>, and from a held target the next
    /// one outward, wrapping back to the nearest past the far end.</summary>
    public static int Next(IReadOnlyList<Vector3> targets, Vector3 eye, int current)
    {
        if (targets.Count == 0)
            return -1;
        // Rank rather than sort: the answer is one index, and the caller's list order is what the
        // returned index means, so building a sorted copy would only have to be undone.
        int rankOfCurrent = current >= 0 && current < targets.Count ? Rank(targets, eye, current) : -1;
        int wanted = rankOfCurrent < 0 ? 0 : (rankOfCurrent + 1) % targets.Count;
        for (int i = 0; i < targets.Count; i++)
            if (Rank(targets, eye, i) == wanted)
                return i;
        return 0;
    }

    // How many targets sit strictly nearer the eye than targets[index]. Ties break on the index so
    // two aircraft at the same range still get distinct ranks and neither is unreachable.
    private static int Rank(IReadOnlyList<Vector3> targets, Vector3 eye, int index)
    {
        float d = targets[index].DistanceSquaredTo(eye);
        int rank = 0;
        for (int i = 0; i < targets.Count; i++)
        {
            if (i == index)
                continue;
            float other = targets[i].DistanceSquaredTo(eye);
            if (other < d || (other == d && i < index))
                rank++;
        }
        return rank;
    }
}
