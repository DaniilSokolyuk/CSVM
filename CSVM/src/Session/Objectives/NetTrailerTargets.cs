using System;
using System.Collections.Generic;
using CSVM.Mech3;
using CSVM.Utils;
using Godot;

namespace CSVM.Session.Objectives;

/// <summary>
/// Turns a patrol net's TRAILER name into a live position supplier, so
/// <see cref="Flight.Ai.AiNetFollower"/> can do the offset arithmetic knowing nothing
/// about players or world nodes. Detail on the lazy-once resolve: see the private
/// <c>Position</c> method's own comment.
/// ⚠ An anchored net resolves to the FIRST rig in split play, so it matches single player. Do not
/// invent a nearest-player, host-player or per-plane pick; the original has no splitscreen and
/// there is no behaviour to copy.
/// </summary>
public sealed class NetTrailerTargets
{
    /// <summary>The trailer name the original resolves to the player's own vehicle. 11 of the 222
    /// nets carry it, 4 of them named <c>*Ace*</c>; it is team-blind, so it puts enemy aces and
    /// reinforcement flights on the player exactly as it keeps wingmen with them
    /// (docs/formats/ai-nets.md).</summary>
    public const string PlayerName = "player";

    private readonly Func<Vector3?>? _player;
    private readonly Func<string, Node3D?>? _worldNode;
    private readonly Dictionary<string, Node3D?> _resolved = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _reported = new(StringComparer.OrdinalIgnoreCase);

    public NetTrailerTargets(Func<Vector3?>? player, Func<string, Node3D?>? worldNode)
    {
        _player = player;
        _worldNode = worldNode;
    }

    /// <summary>The supplier to hand <see cref="Flight.Ai.AiNetFollower"/> for this net, or null when
    /// the net is not anchored (no trailer, no attach node, or a bare index with no name, the
    /// three shipped shapes that carry no target). Null means "fly the authored coordinates",
    /// which is also what the supplier returning null on a given frame means.</summary>
    public Func<Vector3?>? For(AiNet net)
    {
        if (net.Trailer is not { NodeIndex: >= 0, Name: { Length: > 0 } name })
        {
            return null;
        }
        if (name.Equals(PlayerName, StringComparison.OrdinalIgnoreCase))
        {
            return _player;
        }
        return _worldNode == null ? null : () => Position(name);
    }

    /// <summary>Where this net's nodes sit right now, relative to their authored coordinates: the
    /// overlay's read of the same offset the followers fly. Zero for an unanchored net
    /// or an unresolved target.</summary>
    public Vector3 OffsetOf(AiNet net) =>
        Flight.Ai.AiNetFollower.TrailerOffset(net, For(net)?.Invoke());

    private Vector3? Position(string name)
    {
        // Lazy-once, cached hit or miss: the original resolves at net build, but here the nets
        // are read and armed before ZeppelinRuntime places its hosts, so resolving at
        // construction would miss targets that exist a few hundred lines later.
        if (!_resolved.TryGetValue(name, out var node))
        {
            node = _worldNode!(name);
            _resolved[name] = node;
            if (node == null && _reported.Add(name))
            {
                Log.Info("flight", $"ainet: trailer target '{name}' is not in this world, the nets anchored to it fly at their authored coordinates");
            }
        }
        // A destroyed target (a killed zeppelin's node is freed) drops back to the authored
        // coordinates rather than throwing on a stale handle.
        return node is { } n && GodotObject.IsInstanceValid(n) && n.IsInsideTree()
            ? n.GlobalPosition
            : null;
    }
}
