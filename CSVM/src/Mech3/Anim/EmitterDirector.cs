using System;
using System.Collections.Generic;
using System.Linq;
using CSVM.Mech3;
using CSVM.Utils;
using Godot;

namespace CSVM.Mech3.Anim;

/// <summary>An emitter's whole life on one runtime: the keying rule, the start, all four stops, the
/// respawn wipe, the per-frame follow, and a census of what exists.
/// ⚠ Do not collapse the four stops into one parameterised call. They vary on two independent
/// axes, SELECTOR (key / host subtree / owning instance) × DISPOSITION (pause-revivable /
/// pause-and-forget), and every shipped bug in this family has been a selector error.
/// Handed already-resolved host and anchor nodes: the effect-template pool is not a keying scheme
/// and stays out of here.
/// </summary>
public sealed class EmitterDirector
{
    // One emitter per (puffer name, emitter node[, owning def]). Assert is idempotent: a
    // re-assert of an already-running emitter is a no-op, matching data that loops PUFFER_STATE.
    // ⚠ Do not hard-code _defScopedKeys either way: true is required on the effects runtime (two
    // damage-stage sputters both declare black_smoke and would mask each other), false is
    // required on the world runtime (C5's six same-node m_crane_go spark defs would stack six).
    private readonly Dictionary<(string Name, Node3D Node, AnimDefinition? Def), Entry> _emitters = new();

    // The emitting emitters, each stamped with the INSTANT it started, the runtime
    // batch (one AnimRuntime.Advance pass) that dispatched its `PUFFER_STATE 1`.
    // EndOn reads the stamp; nothing else does. See _instant.
    private readonly List<(IEmitter Emitter, Node3D Node, ulong Started)> _active = new();

    // Each host node's emission point in its own frame (see AnimRuntime.VisualOriginOf), zero for
    // a node whose origin sits inside its mesh bounds. Computed lazily on the first tick, never at
    // dispatch: the bootstrap dispatches PUFFER_STATE before the world enters the tree, where a
    // GlobalTransform read only returns identity and an error. Local-frame, so it stays valid when
    // a motion drives the node.
    private readonly Dictionary<Node3D, Vector3> _hostOffsets = new();

    private readonly bool _defScopedKeys;

    private readonly bool _debug;

    private readonly Action<string> _count;

    // The current runtime batch, bumped once per Tick, i.e. once per AnimRuntime.Advance, which is
    // exactly the granularity at which the data's own instants exist: an event with no START_TIME is
    // "EVENT_OFFSET 0" and every zero-offset run of events fires in one Advance pass
    // (SequenceRunner). Emitters carry the value they started on so a host deactivation can tell
    // "this emitter has been running" from "this emitter started a moment ago in this very batch".
    private ulong _instant;

    private IEmitterFactory _factory;

    public EmitterDirector(IEmitterFactory factory, bool defScopedKeys, bool debug,
        Action<string> count)
    {
        _factory = factory;
        _defScopedKeys = defScopedKeys;
        _debug = debug;
        _count = count;
    }

    /// <summary>How many emitters this director has actually built (not just started the owning
    /// def). Verification checks this rather than "the def ran", a started effect
    /// whose factory is retired or whose textures are missing builds nothing and renders
    /// nothing.</summary>
    public int Built { get; private set; }

    /// <summary>How many emitters <see cref="Prewarm"/> built ahead of any assert. Kept apart from
    /// <see cref="Built"/>, which counts what an assert took over, so a delta across a kill still
    /// says the kill reached its emitters.</summary>
    public int Prewarmed { get; private set; }

    /// <summary>Every KNOWN emitter, emitting or not, the module's own answer to "what exists and
    /// what is running", which the `--debug-anim` line and the bootstrap line are both projections
    /// of rather than parallel re-derivations.</summary>
    public IReadOnlyList<EmitterCensusRow> Census
    {
        get
        {
            var rows = new List<EmitterCensusRow>(_emitters.Count);
            foreach (var (key, entry) in _emitters)
            {
                bool valid = entry.Emitter.IsValid;
                bool hostAlive = GodotObject.IsInstanceValid(key.Node);
                rows.Add(new EmitterCensusRow(
                    key.Name,
                    hostAlive ? AnimRuntime.NameOf(key.Node) : "?",
                    entry.Def.AnimName ?? entry.Def.Name,
                    Emitting(entry.Emitter),
                    valid ? entry.Emitter.LiveCount : 0,
                    hostAlive ? key.Node : null));
            }
            return rows;
        }
    }

    /// <summary>Starts (or revives, or re-homes) the emitter a <c>PUFFER_STATE 1</c> asks for.
    /// Takes the raw event payload rather than a parsed <see cref="PufferState"/> because the common
    /// case is a re-assert that builds nothing: 619 defs sit in an infinite LOOP re-asserting theirs
    /// every frame, and parsing eagerly would allocate for every one of them.</summary>
    public void Assert(string name, Node3D host, AnimDefinition def, Node3D? anchor, AnimData data)
    {
        var key = KeyFor(name, host, def);
        if (_emitters.TryGetValue(key, out var existing))
        {
            // A pre-built emitter is taken over whole: ownership, the start and the Built count
            // all read as if this assert had constructed it.
            if (!existing.Claimed)
            {
                _emitters[key] = new Entry(existing.Emitter, def, anchor, Claimed: true);
                _active.Add((existing.Emitter, host, _instant));
                Built++;
                return;
            }
            // ⚠ A SustainEnd'ed emitter REVIVES on re-assert; reading "stopped" as "still running"
            // collapses the sputter loop to one burst per stage.
            if (!Emitting(existing.Emitter))
                _active.Add((existing.Emitter, host, _instant));
            // ⚠ Ownership must move to the re-asserter (same def, later anchor); every stop
            // resolves through it, and leaving it with the first asserter strands later calls.
            if (existing.Def == def && existing.Anchor != anchor)
                _emitters[key] = existing with { Anchor = anchor };
            return;
        }

        // No pre-built emitter for this key, something had to be made.
        // MaterialCreate (EmitterRenderer.Attach, reached through _factory.Create below) is
        // nested inside this scope and is suppressed by it, its cost is folded into this one.
        using var _ = PerfSample.Scope(PerfSite.EffectPoolMiss);
        if (_factory.Create(PufferState.FromAnimEvent(data), out var miss) is not { } emitter)
        {
            if (miss != null)
                _count(miss);
            return;
        }
        if (_debug && _defScopedKeys)
        {
            foreach (var other in _emitters)
                if (other.Key.Name == name && other.Key.Node == host && other.Value.Def != def)
                    Log.Info("anim", $"anim: puffer '{name}' on '{AnimRuntime.NameOf(host)}' builds beside '{other.Value.Def.AnimName}''s emitter [def {def.AnimName}]");
        }
        _emitters[key] = new Entry(emitter, def, anchor, Claimed: true);
        _active.Add((emitter, host, _instant));
        Built++;
    }

    /// <summary>Builds the emitter a <c>PUFFER_STATE 1</c> on this key would build, without
    /// starting it; the first <see cref="Assert"/> takes it over as if it had built it. A key
    /// already known builds nothing, and a factory miss is left for the assert to count. Returns
    /// whether an emitter was built.
    /// ⚠ Never joins the emitting set: building an emitter is not playing it, and the
    /// <c>effect-pool-reset</c> suite guards the pose a staged copy is left in.</summary>
    public bool Prewarm(string name, Node3D host, AnimDefinition def, AnimData data)
    {
        var key = KeyFor(name, host, def);
        if (_emitters.ContainsKey(key))
            return false;
        if (_factory.Create(PufferState.FromAnimEvent(data), out _) is not { } emitter)
            return false;
        _emitters[key] = new Entry(emitter, def, null, Claimed: false);
        Prewarmed++;
        return true;
    }

    /// <summary>The authored stop: <c>PUFFER_STATE &lt;name&gt; 0</c>. Pauses the emitter on the
    /// event's own key, keeping the entry so a later re-assert revives it.
    /// ⚠ Falls back to the instance's same-named emitter when the key misses; an authored on/off
    /// pair does not always name the same host. Every miss is counted, never left silent.</summary>
    public void End(string name, Node3D host, AnimDefinition def, Node3D? anchor)
    {
        if (_emitters.TryGetValue(KeyFor(name, host, def), out var running))
        {
            running.Emitter.SustainEnd();
            _active.RemoveAll(a => a.Emitter == running.Emitter);
            return;
        }
        var owned = _emitters
            .Where(kv => kv.Key.Name == name && kv.Value.Claimed && kv.Value.Def == def
                         && kv.Value.Anchor == anchor)
            .Select(kv => kv.Value.Emitter)
            .ToList();
        foreach (var stray in owned)
        {
            stray.SustainEnd();
            _active.RemoveAll(a => a.Emitter == stray);
        }
        _count(owned.Count > 0
            ? $"PufferState(stop matched by owner, not host: {name})"
            : $"PufferState(stop reached no emitter: {name})");
        if (_debug)
            Log.Info("anim", $"anim: PUFFER_STATE 0 '{name}' missed its host '{AnimRuntime.NameOf(host)}' [def {def.AnimName}], {(owned.Count > 0 ? $"stopped {owned.Count} emitter(s) this instance owns instead" : "this instance owns no emitter of that name")}");
    }

    /// <summary>Ends emission for every emitter on <paramref name="root"/> or its subtree, what
    /// <c>OBJECT_ACTIVE_STATE false</c> means for emitters. Pauses rather than removes the entry,
    /// so a later <see cref="Assert"/> revives it; an emitter started this same instant is spared
    /// unless <paramref name="sparingSameInstant"/> is false.
    /// ⚠ Only an explicit host deactivation counts; visibility is not the test, since particles go
    /// TopLevel into world space.</summary>
    public void EndOn(Node3D root, bool sparingSameInstant = true)
    {
        for (int i = _active.Count - 1; i >= 0; i--)
        {
            var (emitter, node, started) = _active[i];
            if (!emitter.IsValid || !GodotObject.IsInstanceValid(node))
            {
                _active.RemoveAt(i);
                continue;
            }
            if (node != root && !root.IsAncestorOf(node))
                continue;
            if (sparingSameInstant && started == _instant)
            {
                _count("ObjectActiveState(spared an emitter started this instant)");
                if (_debug)
                    Log.Info("anim", $"anim: host '{AnimRuntime.NameOf(node)}' deactivated in the instant its emitter started, left emitting");
                continue;
            }
            emitter.SustainEnd();
            _active.RemoveAt(i);
            if (_debug)
                Log.Info("anim", $"anim: host '{AnimRuntime.NameOf(node)}' deactivated, emitter stopped ({_active.Count} still emitting)");
        }
    }

    /// <summary>Pauses what an ending instance owns, KEEPING the entries, the stop for a def
    /// carrying an ACTIVE_STATE 1 and neither authored stop (the torpedo's ground fire, C1's refuel
    /// tanks). Emission ends rather than the emitter being torn down, so live particles
    /// finish their authored LIFETIME_RANGE, the fire fades over its last 3–4 s instead of popping
    /// out, and a replay on this same pool slot revives the entry.</summary>
    public void EndFor(AnimDefinition def, Node3D? anchor)
    {
        if (_emitters.Count == 0)
            return;
        foreach (var entry in _emitters.Values
                     .Where(v => v.Claimed && v.Def == def && v.Anchor == anchor).ToList())
        {
            entry.Emitter.SustainEnd();
            _active.RemoveAll(a => a.Emitter == entry.Emitter);
        }
    }

    /// <summary>Pauses what a STOPPED instance owns and forgets the entries, so nothing of the
    /// definition can be revived after it. Same selector as <see cref="EndFor"/>, different
    /// disposition, the pair is why the four stops do not reconcile into one.</summary>
    public void Discard(AnimDefinition def, Node3D? anchor)
    {
        var keys = _emitters
            .Where(kv => kv.Value.Claimed && kv.Value.Def == def && kv.Value.Anchor == anchor)
            .Select(kv => kv.Key)
            .ToList();
        foreach (var key in keys)
        {
            var emitter = _emitters[key].Emitter;
            emitter.SustainEnd();
            _active.RemoveAll(a => a.Emitter == emitter);
            _emitters.Remove(key);
        }
    }

    /// <summary>The per-player crash rig's respawn: every emitter stops and drops its particles at
    /// once, because respawn is immediate and must not leave fire burning. An emitter whose host
    /// still exists is KEPT and returned to the pre-built state, so the next crash finds it made;
    /// nothing accumulates, since the keys are the same nodes. One whose host is gone is destroyed.
    /// Wipes the whole pool, not just what live instances own: an effect def whose sequence has
    /// already ended (`large_10sec_fire`) owns no live instance yet is still emitting.</summary>
    public void Reset()
    {
        foreach (var key in _emitters.Keys.ToList())
        {
            var entry = _emitters[key];
            entry.Emitter.SustainEnd();
            entry.Emitter.Clear();
            if (entry.Emitter.IsValid && GodotObject.IsInstanceValid(key.Node))
            {
                _emitters[key] = entry with { Anchor = null, Claimed = false };
            }
            else
            {
                entry.Emitter.Destroy();
                _emitters.Remove(key);
            }
        }
        _active.Clear();
        _hostOffsets.Clear();
    }

    /// <summary>Drives every emitting emitter from its host node's current world pose. Emitters
    /// follow moving nodes (the train's smokestack travels the whole track loop), so this is per
    /// frame.</summary>
    public void Tick(float dt)
    {
        // A new runtime batch begins here, everything the instances dispatch below this point
        // shares one instant, which is what EndOn's same-instant carve-out is keyed on.
        _instant++;
        for (int i = _active.Count - 1; i >= 0; i--)
        {
            var (emitter, node, _) = _active[i];
            if (!emitter.IsValid || !GodotObject.IsInstanceValid(node))
            {
                _active.RemoveAt(i);
                continue;
            }
            var xform = node.GlobalTransform;
            var offset = HostOffsetOf(node, xform);
            emitter.SustainAt(offset == Vector3.Zero ? xform.Origin : xform * offset, xform.Basis, dt);
        }
    }

    /// <summary>Swaps in the factory a runtime has once its texture archive is released. Nothing
    /// already built is disturbed; only further requests change answer, from "here is an emitter" to
    /// a named, once-warned miss.
    /// ⚠ That miss must stay a warn, not a silent no-op: swallowing it reads as a world with no
    /// fire, dust or smoke and a clean log, and the bootstrap census runs too early to catch it.</summary>
    public void RetireFactory() => _factory = new SpentEmitterFactory();

    private (string, Node3D, AnimDefinition?) KeyFor(string name, Node3D host, AnimDefinition def) =>
        (name, host, _defScopedKeys ? def : null);

    private bool Emitting(IEmitter emitter) => _active.Any(a => a.Emitter == emitter);

    // The host's emission point in its own frame, cached per node. Zero, and the emission
    // point exactly the node origin, byte-identical with the pre-cache behaviour, for every node
    // whose origin sits inside its mesh bounds; the offset to the bounds centre for
    // absolute-modelled world subtrees, whose origin is the map corner. Local-frame, so
    // a motion-driven host carries its emission point along.
    private Vector3 HostOffsetOf(Node3D host, in Transform3D xform)
    {
        if (_hostOffsets.TryGetValue(host, out var offset))
            return offset;
        var visual = AnimRuntime.VisualOriginOf(host);
        offset = visual == xform.Origin ? Vector3.Zero : xform.AffineInverse() * visual;
        _hostOffsets[host] = offset;
        if (_debug && offset != Vector3.Zero)
            Log.Info("anim", $"anim: puffer host '{AnimRuntime.NameOf(host)}' origin {xform.Origin} is outside its mesh bounds, emitting at {visual}");
        return offset;
    }

    // Claimed is false only for an emitter Prewarm built that no assert has taken yet: it has no
    // owner, so the owner-selected stops (EndFor, Discard, End's fallback) pass it over.
    private readonly record struct Entry(IEmitter Emitter, AnimDefinition Def, Node3D? Anchor, bool Claimed);
}
