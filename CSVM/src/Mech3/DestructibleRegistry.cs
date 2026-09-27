using System.Collections.Generic;
using Godot;

namespace CSVM.Mech3;

/// <summary>
/// Live, mutable per-instance state for the world's destructible objects: current/max HP and
/// coarse lifecycle state, keyed by <c>(def, anchor)</c> since one wildcard <c>NAME</c> or a
/// compiler expansion can bind several independent instances to one def. Applies no damage
/// itself; the <c>WeaponHit</c> path decrements HP. Decode: <c>docs/formats/destructibles.md</c>.
/// </summary>
public sealed class DestructibleRegistry
{
    // Keyed by (definition, anchor instance id), the exact pair EvaluateCondition holds when it
    // reaches an ANIM_HEALTH branch, so the live read is O(1) on the hot poll path. Godot object
    // identity is by native pointer, so key on the instance id, not the Node3D itself.
    private readonly Dictionary<(AnimDefinition Def, ulong Anchor), Instance> _byKey = new();
    private readonly List<Instance> _all = new();

    private readonly HashSet<ulong> _anchors = new();

    // The ONE authoritative instance per node a hit may resolve through. A node can carry several
    // instances (reader wildcard + compiled per-instance); the compiled def is the better data, so
    // it wins. Each instance claims its own damage node outright and its anchor only as a
    // fallback, which is what tells two pools on one anchor apart, see Register.
    private readonly Dictionary<ulong, Instance> _authoritative = new();

    // The claims made on a node by an instance whose OWN damage node it is. A second def merely
    // anchored above that node must not displace it, whichever order the two register in.
    private readonly HashSet<ulong> _ownClaim = new();

    // Nodes whose authoritative claim is a pool's ANCHOR rather than its own damage node, so
    // Resolve can tell the two apart while it climbs.
    private readonly HashSet<ulong> _fallbackClaim = new();

    /// <summary>Coarse lifecycle state of one destructible instance. Everything sits at
    /// <see cref="Healthy"/> until damage is applied.</summary>
    public enum State
    {
        Healthy,   // at full HP, undamaged
        Damaged,   // below max, above zero, a DAMAGE_SEQUENCE stage is showing
        Destroyed, // HP reached zero, death sequence run
    }

    /// <summary>Number of live destructible instances, one per <c>(def, anchor)</c> pair.
    /// Exceeds <see cref="DistinctAnchors"/> when more than one def binds a node (the reader
    /// wildcard def and the compiler's per-instance defs both resolve to the same towers; each
    /// keeps its own HP pool, which is why keying is per pair, not per node).</summary>
    public int Count => _all.Count;

    /// <summary>Number of distinct world node groups covered, the count of physical destructible
    /// objects, ignoring how many defs bind each.</summary>
    public int DistinctAnchors => _anchors.Count;

    public IReadOnlyList<Instance> All => _all;

    /// <summary>The team a node carries as a mission structure, or null where it is not one. The
    /// scene build stamps only the flagged nodes, so the meta's presence is the flag itself
    /// (docs/org/targeting.md, "What a mission structure's team is").</summary>
    public static int? MissionStructureTeamOf(Node3D node) =>
        node.HasMeta(SceneBuilder.MissionStructureTeamMeta)
            ? node.GetMeta(SceneBuilder.MissionStructureTeamMeta).AsInt32()
            : null;

    /// <summary>Registers one destructible node group at full health. Idempotent: a repeated
    /// <c>(def, anchor)</c> returns the existing instance without resetting its HP, so a second
    /// bootstrap pass or a re-index cannot silently heal a damaged object.
    /// <paramref name="damageNode"/> is the definition's own animation-root node, which a weapon
    /// hit resolves through, along with everything under it (null means the anchor).</summary>
    public Instance Register(AnimDefinition def, Node3D anchor, float maxHealth,
        Node3D? damageNode = null)
    {
        var key = (def, anchor.GetInstanceId());
        if (_byKey.TryGetValue(key, out var existing))
            return existing;
        var inst = new Instance(def, anchor, maxHealth, damageNode);
        _byKey[key] = inst;
        _all.Add(inst);
        ulong aid = anchor.GetInstanceId();
        _anchors.Add(aid);
        // The damage node as its own claim, the anchor only as a fallback. ⚠ Keep the anchor
        // claim: a def usually roots on a node its death then hides, and a hit on the wreck must
        // still find the pool that owns it rather than falling through as scenery.
        var own = damageNode ?? anchor;
        // A pool standing on a mission-structure node is that structure, and carries the team the
        // scene data gives it rather than staying neutral scenery. A zeppelin record's own team is
        // written later and wins, which is the order the original fans it in.
        if (MissionStructureTeamOf(own) is { } teamed)
        {
            inst.Team = teamed;
            inst.Gasbag = own.HasMeta(SceneBuilder.MissionStructureGasbagMeta);
        }

        Claim(own.GetInstanceId(), inst, ownRoot: true);
        if (!ReferenceEquals(own, anchor))
        {
            Claim(aid, inst, ownRoot: false);
        }

        return inst;
    }

    /// <summary>The live instance for a <c>(def, anchor)</c> pair, or null when that pair is not
    /// a registered destructible, the common case, since most conditions evaluated are not on
    /// destructibles at all. A null anchor is never a destructible.</summary>
    public Instance? Get(AnimDefinition def, Node3D? anchor)
    {
        if (anchor == null)
            return null;
        return _byKey.TryGetValue((def, anchor.GetInstanceId()), out var inst) ? inst : null;
    }

    /// <summary>Every pool anchored on exactly this node, normally one, but a node carrying both
    /// the compiler's per-instance def and a reader wildcard's carries two independent pools with
    /// their own HP. Use it with <see cref="Resolve"/>, which names the one a weapon hit reaches;
    /// the others cannot be damaged through the hit path at all. Empty for an ordinary node.</summary>
    public List<Instance> PoolsOn(Node? node)
    {
        var found = new List<Instance>();
        if (node == null)
        {
            return found;
        }
        foreach (var inst in _all)
        {
            if (ReferenceEquals(inst.Anchor, node))
            {
                found.Add(inst);
            }
        }
        return found;
    }

    /// <summary>The destructible instance a struck world node belongs to: climbs the parent chain
    /// from the raycast-hit collider to a node a pool claims, nearest compiled claim first, then
    /// nearest reader (docs/formats/destructibles.md). Null off the destructible chain entirely.
    /// A pool answers for its own damage node and everything under it, which is what the original
    /// stamps its handler over; its anchor answers only for a strike on the anchor itself, or on
    /// the group around a wreck, whose own death hid the node the pool stands for.</summary>
    public Instance? Resolve(Node? struck)
    {
        Instance? nearestReader = null;
        bool climbed = false;
        for (var n = struck; n != null; n = n.GetParent(), climbed = true)
        {
            ulong id = n.GetInstanceId();
            if (!_authoritative.TryGetValue(id, out var inst))
                continue;
            // A claimed anchor heads a group, and attribution never crosses out of one: the
            // original registers nothing there, so the round belongs to this group or to nothing.
            if (climbed && _fallbackClaim.Contains(id) && inst.Status != State.Destroyed)
                return nearestReader;
            if (inst.Def.Archive != null)
                return inst;               // compiled = authoritative, take the nearest
            nearestReader ??= inst;        // fallback if no compiled anchor is found up the chain
        }
        return nearestReader;
    }

    /// <summary>Drops every instance. Used when a runtime is torn down and rebuilt.</summary>
    public void Clear()
    {
        _byKey.Clear();
        _all.Clear();
        _anchors.Clear();
        _authoritative.Clear();
        _ownClaim.Clear();
        _fallbackClaim.Clear();
    }

    // Who answers a hit resolved at this node. Compiled beats reader, and a def whose own damage
    // node this is beats one merely anchored above it; otherwise the first claimant keeps it.
    private void Claim(ulong node, Instance inst, bool ownRoot)
    {
        bool free = !_authoritative.TryGetValue(node, out var current);
        bool better = !free
            && ((inst.Def.Archive != null && current!.Def.Archive == null)
                || (ownRoot && !_ownClaim.Contains(node)));
        if (free || better)
        {
            _authoritative[node] = inst;
            if (ownRoot)
            {
                _fallbackClaim.Remove(node);
            }
            else
            {
                _fallbackClaim.Add(node);
            }
        }

        if (ownRoot)
        {
            _ownClaim.Add(node);
        }
    }

    /// <summary>One live destructible node group: its authored definition, the world node it
    /// binds to, and the HP that actually falls as it takes fire.</summary>
    public sealed class Instance
    {
        public Instance(AnimDefinition def, Node3D anchor, float maxHealth, Node3D? damageNode = null)
        {
            Def = def;
            Anchor = anchor;
            MaxHealth = maxHealth;
            Health = maxHealth;
            DamageNode = damageNode ?? anchor;
        }

        public AnimDefinition Def { get; }
        public Node3D Anchor { get; }

        /// <summary>The node a weapon hit resolves through, with everything under it: this
        /// definition's own animation-root node inside <see cref="Anchor"/>, or the anchor where
        /// the def names none (docs/formats/destructibles.md). The piece the pool's HP stands for,
        /// which is why <see cref="Resolve"/> refuses the rest of the group around it.</summary>
        public Node3D DamageNode { get; }

        public float MaxHealth { get; private set; }
        public float Health { get; set; }
        public State Status { get; set; } = State.Healthy;

        /// <summary>The owning side, where the data names one, or null where nothing does. Two
        /// sources author one: a zeppelin record, and a pool standing on a mission-structure node,
        /// whose team comes off that node (docs/org/targeting.md "The team space"). A null reaches
        /// the candidate builder as neutral, so the object is nobody's target.
        /// ⚠ Never write a literal here for an object the data leaves unowned. Doing so is what
        /// made every crate and every gasbag hostile to all comers.</summary>
        public int? Team { get; set; }

        /// <summary>Whether the mission-structure node this pool stands on is a gasbag. A turret's
        /// candidate pass drops these; every other channel keeps them.</summary>
        public bool Gasbag { get; set; }

        /// <summary>Out of play: the pool exists, but its object is not in the world yet, a
        /// mission's <c>deactivated</c> zeppelin before its script wakes it. Refused as a target
        /// and by <c>AnimRuntime.DamageAt</c> while set. ⚠ Not a death state: <see cref="Status"/>
        /// stays healthy and the HP stands.</summary>
        public bool Dormant { get; set; }

        /// <summary>The name of the mission entity this pool is a PART of, where one owns it, a
        /// zeppelin record's node name on each of its zones. Null for scenery, which belongs to
        /// nothing. A zone's own anchor is named `gasbag1`/`leng11`, so a `rating_biases` pattern
        /// naming the airship reaches it through this or through the world tree above it
        /// (<c>TargetPool.CollectOwners</c>).</summary>
        public string? Owner { get; set; }

        /// <summary>How many of the DAMAGE_SEQUENCE's descending health thresholds this instance
        /// has fallen past, the deepest progressive-damage stage it has escalated to. Only ever
        /// increases (damage escalates, never heals), so a stage effect fires exactly once; a
        /// reset puts it back to 0.</summary>
        public int DamageStage { get; set; }

        /// <summary>Set when the death CHAIN authors the healthy→destroyed swap in a
        /// <c>CALL_ANIMATION</c> target (gate2's <c>blockit2</c>) rather than in this def's own
        /// sequences, so <c>ApplyDeathSwap</c>'s fallback yielded and the swap, wreck colliders,
        /// fireball and flying debris all arrive when the chained call fires. A reset must
        /// stop this def too (its own pending scheduled call, or its already-run motions) and
        /// restore the pose of whatever it moved.</summary>
        public AnimDefinition? ChainedDeathDef { get; set; }

        /// <summary>Every <c>CALL_ANIMATION</c> target this death dispatched onto its own anchor
        /// (docs/formats/destructibles.md), populated at dispatch time so it names only what
        /// actually ran. A reset stops and restores each of these too, or a called def's own
        /// motions can outlive the reset.</summary>
        public HashSet<(AnimDefinition Def, Node3D Anchor)> LocalCallTargets { get; } = new();

        /// <summary>The gamez node names above <see cref="Anchor"/>, nearest first, as
        /// <c>TargetPool.CollectOwners</c> hands them to a <c>rating_biases</c> match, null before
        /// the first walk. Held because every AI shooter asks every ranked structure candidate for
        /// them on every physics tick, and each ancestor's name comes back out of Godot as a string
        /// plus a finalizable <c>StringName</c>. Re-walked when <see cref="CachedOwnerTreeParent"/>
        /// no longer names the anchor's parent, what an authored ADD_CHILD leaves behind.</summary>
        internal List<string>? CachedOwnerTree { get; set; }

        /// <summary>The instance id <see cref="CachedOwnerTree"/> was walked under, 0 for an anchor
        /// that had no parent then.</summary>
        internal ulong CachedOwnerTreeParent { get; set; }

        /// <summary>The anchor's Godot name as <c>TargetPool.NameOf</c> last read it, null before
        /// the first read. Held for the reason <see cref="CachedOwnerTree"/> is. It is re-read when
        /// <see cref="CachedAnchorNameParent"/> no longer names the anchor's parent, since a
        /// reparent is what lets Godot rename a node to keep siblings unique.</summary>
        internal string? CachedAnchorName { get; set; }

        /// <summary>The instance id <see cref="CachedAnchorName"/> was read under, 0 for an anchor
        /// that had no parent then.</summary>
        internal ulong CachedAnchorNameParent { get; set; }

        /// <summary>Re-seeds this pool from a mission record, the zeppelin case (M4 F18):
        /// <c>zeppelins.json</c> authors per-part hp (<c>gasbags</c> 80–400,
        /// <c>cannon_health</c> 200) that overrides the def's own <c>HEALTH</c> where present.
        /// Wire-up time only: refuses once the instance has been damaged, so a late re-seed
        /// cannot silently heal a fight in progress.</summary>
        public void Reseed(float maxHealth)
        {
            if (Status != State.Healthy || Health < MaxHealth)
                return;
            MaxHealth = maxHealth;
            Health = maxHealth;
        }
    }
}
