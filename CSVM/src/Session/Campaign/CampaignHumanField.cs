using System.Collections.Generic;
using Godot;

namespace CSVM.Session.Campaign;

/// <summary>One human of the field, as much of a rig as an objective condition reads: where it is,
/// which AI group it stands in (a 967 capture stamps one onto a human rig) and whether it is a
/// wreck.</summary>
public readonly record struct HumanState(Vector3 Position, int? Group, bool Crashed);

/// <summary>
/// The human field's own rules, engine-free so <c>CSVM.Tests</c> pins them without a world: what an
/// objective condition asks when two to four humans fly the mission.
/// The scripted player is NOT here. That is one aircraft, the one an authored <c>player</c> token
/// means, and <c>CampaignDirector</c> answers it; this type only ever answers "any of them".
/// </summary>
public static class CampaignHumanField
{
    /// <summary>The <c>TRAVELERS</c> proximity read over the field: the NEAREST human decides. An
    /// approaching condition is therefore met by the first human to arrive and a departing one only
    /// once the last has left, which is the same sentence read from either end. ⚠ The caller passes
    /// a field of at least one, since an empty one would read a departing condition true.</summary>
    public static bool Travelers(IReadOnlyList<HumanState> humans, Vector3 reference, float radius,
        bool approaching)
    {
        // A wreck still counts because the pilot rig continues reporting its position after a
        // crash; human-field proximity keeps that same positional source.
        float nearest = float.MaxValue;
        foreach (var human in humans)
        {
            float d = human.Position.DistanceSquaredTo(reference);
            if (d < nearest)
            {
                nearest = d;
            }
        }

        bool inside = nearest <= radius * radius;
        return approaching ? inside : !inside;
    }

    /// <summary>How many humans a <c>DEDG</c> over <paramref name="group"/> counts: the ones a
    /// capture stamped with that group and that are not wrecks. Crashed alone, never inert: a human
    /// rig is inert under a cutscene, which is not a deactivation.</summary>
    public static int LiveInGroup(IReadOnlyList<HumanState> humans, int group)
    {
        int alive = 0;
        foreach (var human in humans)
        {
            if (human.Group == group && !human.Crashed)
            {
                alive++;
            }
        }

        return alive;
    }
}
