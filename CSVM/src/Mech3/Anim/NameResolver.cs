using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace CSVM.Mech3.Anim;

/// <summary>Name → node resolution: the index, the wildcard matcher, memoized <see cref="FindAll"/>,
/// the scoped tier chain (<see cref="Resolve"/>/<see cref="ResolveScoped"/>), the symbol authority
/// (<see cref="SymbolClaims"/>), the anchoring rules (<see cref="Anchors"/>) and the bind-time
/// census (<see cref="ResolutionLines"/>), generic over the node type, so the whole rule set is
/// testable without an engine and shared, unchanged, by <c>AnimRuntime</c>'s one instantiation.
/// Decode: docs/architecture.md, this file's entry.</summary>
public sealed class NameResolver<TNode>
    where TNode : class
{
    // ---- policy inputs. The owning runtime sets these before its bootstrap calls Add/Anchors;
    // they are construction-time facts about the runtime, not per-query switches. ----

    /// <summary>Resolve every node reference by NAME, ignoring the compiled gamez index: <see
    /// cref="Add"/> leaves the by-index map empty, so <see cref="SymbolClaims"/> always reports
    /// "index not built" and symbol narrowing never decides. Set by a runtime whose subtree carries
    /// non-portable node ptrs or mixes two colliding gamez index spaces (the per-player crash rig,
    /// see <c>AnimRuntime.NameResolveFallback</c>), where one node per name makes name resolution
    /// the only correct choice.</summary>
    public bool NameResolveFallback;

    /// <summary>Refuse the <c>ANIMATION_ROOT_NAME</c> anchor lift, however few matches it finds.
    /// Set only by a caller that built part of the world, because <see cref="MaxRootLift"/>'s
    /// premise is a WHOLE-WORLD node population: a single-subtree stage drops under the cap, at
    /// which point unrelated definitions anchor onto whatever generic child the subtree happens to
    /// own (measured on C1's 20-node <c>ap_radiotwr</c>: 95 lifts and 91 phantom destructible
    /// instances). Suppressed lifts are counted and reported, never silently dropped.</summary>
    public bool SuppressRootLift;

    /// <summary>Collect a per-definition census of how the bind RESOLVED, reported through
    /// <see cref="ResolutionLines"/>. The census window itself is <see cref="OpenCensus"/>/<see
    /// cref="CloseCensus"/>, the owning runtime brackets its bootstrap passes with them, so a
    /// runtime miss after the bind reports itself elsewhere instead of restating the bind. Default
    /// false: a full-world session collects nothing.</summary>
    public bool ReportResolution;

    /// <summary>ANIMATION_ROOT_NAME matches above this count are generic per-object roots
    /// ('healthy' appears 217× in C1), those defs belong to game objects (planes, zeppelin
    /// parts), not to world nodes. The genuine building templates lift ≤ 9 instances.</summary>
    public int MaxRootLift = 16;

    private const int CensusCap = 12;

    // The two NAME wildcards, read here as "one authored definition, several world instances"
    // rather than as a matcher (docs/org/sequences.md, the odometer).
    private static readonly char[] Wildcards = { '*', '#' };

    private readonly List<IndexRow> _index = new();

    // Definitions Anchors resolved through the ANIMATION_ROOT_NAME lift, by reference: the other
    // half of RefusesGlobalTier, which cannot be read off the definition alone.
    private readonly HashSet<AnimDefinition> _rootLifted = new();

    private readonly Dictionary<TNode, TNode?> _parentOf;

    private readonly Dictionary<string, Func<string, bool>> _matcherCache =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<(string Pattern, TNode? Scope), List<TNode>> _findCache;

    // Caller-supplied identity, not TNode's inherited Equals, Godot object equality is unreliable
    // across proxy instances of the same native node. The engine keys by instance id.
    private readonly IEqualityComparer<TNode> _identity;

    // The middle tier: the definition's own template roots, resolved by the OWNER (the pool and
    // its slot arithmetic stay AnimRuntime's, the resolver sees only the resolved root list).
    private readonly Func<AnimDefinition, TNode?, IReadOnlyList<TNode>> _ownRootsOf;

    // Liveness of an anchor node (the engine passes Godot's IsInstanceValid; tests pass _ => true).
    private readonly Func<TNode, bool> _isLive;

    // Whether a definition's resolution may see a node at all: false only for a copy staged into
    // the template pool under a template root this definition never names. Off the pool it answers
    // true everywhere and the narrowing below is inert, which keeps a non-pooled world boot
    // byte-identical.
    private readonly Func<AnimDefinition, TNode?, TNode, bool> _stagingAdmits;

    // The staged library copy an anchor sits inside (its root), or null off the pool: the
    // private subtree a definition dispatched onto that copy resolves its names in first.
    private readonly Func<TNode, TNode?> _privateCopyOf;

    // The compiled gamez index -> built node map SymbolClaims and NarrowToSymbolRoot read. See
    // Add for the two population rules (never on a fallback runtime, never for a pooled copy).
    private readonly Dictionary<int, TNode> _byIndex = new();

    // SoleStagedCopy's memo: a claimed-but-unbuilt index -> the one pooled copy carrying it, or
    // null when none or several do. Dropped whenever Add grows the index.
    private readonly Dictionary<int, TNode?> _soleCopyCache = new();

    // ---- the bind-time resolution census (see ResolutionLines) ----
    private readonly HashSet<(string Name, string Anim)> _censusSeen = new();

    private readonly List<string> _censusUnanchoredNames = new();

    private readonly List<string> _censusLiftedNames = new();

    private readonly List<string> _censusSuppressedNames = new();

    private readonly List<string> _censusMissingTargets = new();

    private bool _censusOpen;

    private int _censusAnchored, _censusNarrowed, _censusLifted, _censusSuppressed, _censusUnanchored, _censusMissing;

    /// <summary><paramref name="ownRootsOf"/> supplies the middle tier: a definition's own template
    /// root copies for the given anchor's pool slot. Runs per event, so it must resolve through
    /// <see cref="FindAll"/> only, never <see cref="Anchors"/>, whose census it would re-enter.
    /// Null means "no own roots" (the tier always misses). <paramref name="isLive"/> answers whether
    /// an anchor is still valid; a dead one drops the scoped tiers. <paramref name="stagingAdmits"/>
    /// is the owner's verdict on a pooled copy (<see cref="AdmissibleStaging"/>); <paramref name="privateCopyOf"/> the staged library copy an anchor sits in (the anchored <see cref="SymbolClaims(AnimDefinition, string, TNode?, out TNode?)"/> narrows to it).</summary>
    public NameResolver(
        IEqualityComparer<TNode>? identity = null,
        Func<AnimDefinition, TNode?, IReadOnlyList<TNode>>? ownRootsOf = null,
        Func<TNode, bool>? isLive = null,
        Func<AnimDefinition, TNode?, TNode, bool>? stagingAdmits = null,
        Func<TNode, TNode?>? privateCopyOf = null)
    {
        _identity = identity ?? EqualityComparer<TNode>.Default;
        _ownRootsOf = ownRootsOf ?? ((_, _) => Array.Empty<TNode>());
        _isLive = isLive ?? (_ => true);
        _stagingAdmits = stagingAdmits ?? ((_, _, _) => true);
        _privateCopyOf = privateCopyOf ?? (_ => default);
        _parentOf = new Dictionary<TNode, TNode?>(_identity);
        _findCache = new Dictionary<(string, TNode?), List<TNode>>(new ScopeKeyComparer(_identity));
    }

    /// <summary>Every indexed row's node and source name, in <see cref="Add"/> order, for a
    /// caller that walks the whole index by some rule other than a NAME pattern (a substring sweep
    /// for uncovered destroyed-variant subtrees). Read-only.</summary>
    public IEnumerable<(TNode Node, string SrcName)> Rows
    {
        get
        {
            foreach (var row in _index)
            {
                yield return (row.Node, row.SrcName);
            }
        }
    }

    /// <summary>How many writes <see cref="ResolveScoped"/> has refused the global tier, the
    /// measure of how often one instance's generic name would have reached the whole world. Never
    /// reset, so read it as a running total.</summary>
    public int GlobalTierRefused { get; private set; }

    /// <summary>Adds one row: a node's source name, its parent (the ancestry snapshot
    /// <see cref="FindAll"/> reads instead of the live tree), and its gamez-index slot if any.
    /// The by-index map <see cref="SymbolClaims"/> reads stays empty under
    /// <see cref="NameResolveFallback"/>, and skips a row added with
    /// <paramref name="indexByPointer"/> false: a pooled copy shares its source's indices, so a
    /// shared map would steal the first copy's events; such rows still resolve by name.</summary>
    public void Add(TNode node, string srcName, TNode? parent, int? gamezIndex = null, bool indexByPointer = true)
    {
        _index.Add(new IndexRow(node, srcName, gamezIndex));
        _parentOf[node] = parent;
        if (gamezIndex is { } grown)
        {
            _soleCopyCache.Remove(grown);
        }
        if (indexByPointer && !NameResolveFallback && gamezIndex is { } gi
            && (!_byIndex.TryGetValue(gi, out var held) || !_isLive(held)))
        {
            // First claimant wins, unless that claimant has since been freed: an airframe swap
            // re-stages the same cross-archive block with a different aircraft in it.
            _byIndex[gi] = node;
        }
    }

    /// <summary>Drops every memoized <see cref="FindAll"/> answer, needed after <see cref="Add"/>
    /// grows the index post-bootstrap, so a pattern already cached as "resolves to nothing" is
    /// re-asked instead of standing stale.</summary>
    public void ClearFindCache() => _findCache.Clear();

    /// <summary>Drops every row naming a node the liveness test now rejects, with the ancestry
    /// entries and cached answers keyed on one. Nothing tells this index a node has gone, and the
    /// identity comparer dereferences whatever it is handed. A dead key therefore throws for the
    /// next query that hashes into its bucket, so the owner sweeps before it stages over freed
    /// nodes. Returns the rows dropped.</summary>
    // ⚠ Rebuild the keyed collections; never Remove a dead key. Remove hashes the key it is
    // handed and walks its bucket, which is the dereference this exists to prevent.
    public int DropFreed()
    {
        int dropped = 0;
        var live = new List<IndexRow>(_index.Count);
        foreach (var row in _index)
        {
            if (_isLive(row.Node))
            {
                live.Add(row);
            }
            else
            {
                dropped++;
            }
        }

        if (dropped == 0)
        {
            return 0;
        }

        _index.Clear();
        _index.AddRange(live);
        var ancestry = new List<KeyValuePair<TNode, TNode?>>(_parentOf.Count);
        foreach (var entry in _parentOf)
        {
            if (_isLive(entry.Key))
            {
                ancestry.Add(entry);
            }
        }

        _parentOf.Clear();
        foreach (var entry in ancestry)
        {
            // A freed parent leaves its live child rooted here: IsWithin hashes every step of the
            // chain it walks, so a dead one on that chain is the same fault one row down.
            var parent = entry.Value;
            _parentOf[entry.Key] = parent is not null && _isLive(parent) ? parent : null;
        }

        var freedClaims = new List<int>();
        foreach (var claim in _byIndex)
        {
            if (!_isLive(claim.Value))
            {
                freedClaims.Add(claim.Key);
            }
        }

        foreach (int gamezIndex in freedClaims)
        {
            _byIndex.Remove(gamezIndex);
        }

        _soleCopyCache.Clear();
        _findCache.Clear();
        return dropped;
    }

    /// <summary>Drops every row naming one of <paramref name="nodes"/>, with the ancestry entries,
    /// index claims and cached answers keyed on one. This is the inverse of the <see cref="Add"/>
    /// walk one staging pass made. An owner calls it when it stages a second subtree over the same
    /// names while the first is still live. ⚠ Every node handed here is hashed, so a freed one
    /// belongs to <see cref="DropFreed"/> instead. Returns the rows dropped.</summary>
    public int DropNodes(IReadOnlyCollection<TNode> nodes)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        var drop = new HashSet<TNode>(_identity);
        foreach (var node in nodes)
        {
            drop.Add(node);
        }

        int dropped = 0;
        var kept = new List<IndexRow>(_index.Count);
        foreach (var row in _index)
        {
            if (drop.Contains(row.Node))
            {
                dropped++;
            }
            else
            {
                kept.Add(row);
            }
        }

        if (dropped == 0)
        {
            return 0;
        }

        _index.Clear();
        _index.AddRange(kept);
        var ancestry = new List<KeyValuePair<TNode, TNode?>>(_parentOf.Count);
        foreach (var entry in _parentOf)
        {
            if (!drop.Contains(entry.Key))
            {
                ancestry.Add(entry);
            }
        }

        _parentOf.Clear();
        foreach (var entry in ancestry)
        {
            // A dropped parent leaves its kept child rooted here, the way DropFreed's rebuild does.
            // IsWithin walks the chain, and a step of it no longer indexed answers nothing.
            var parent = entry.Value;
            _parentOf[entry.Key] = parent is not null && !drop.Contains(parent) ? parent : null;
        }

        var claims = new List<int>();
        foreach (var claim in _byIndex)
        {
            if (drop.Contains(claim.Value))
            {
                claims.Add(claim.Key);
            }
        }

        foreach (int gamezIndex in claims)
        {
            _byIndex.Remove(gamezIndex);
        }

        _soleCopyCache.Clear();
        _findCache.Clear();
        return dropped;
    }

    /// <summary>How many indexed rows name a node the liveness test rejects, the stale entries a
    /// stage would leave behind. Walks the whole index, so read it at a seam rather than per
    /// frame; zero after <see cref="DropFreed"/>.</summary>
    public int FreedRows()
    {
        int freed = 0;
        foreach (var row in _index)
        {
            if (!_isLive(row.Node))
            {
                freed++;
            }
        }
        return freed;
    }

    /// <summary>Every indexed node matching a NAME pattern, optionally restricted to one node's
    /// subtree. Wildcards: <c>*</c> at most one digit, <c>#</c> a digit run including zero; a plain name
    /// compares case-insensitively, also against a <c>.flt</c>-stripped copy. Memoized on
    /// <c>(pattern, scope)</c>, the returned list is read-only, the same instance on every repeat
    /// query. ⚠ The scope filter reads each node's <see cref="Add"/>-time parent snapshot; a node
    /// reparented afterwards silently misreads it.</summary>
    public List<TNode> FindAll(string pattern, TNode? scope)
    {
        var key = (pattern, scope);
        if (_findCache.TryGetValue(key, out var hit))
        {
            return hit;
        }
        var match = Matcher(pattern);
        var result = new List<TNode>();
        foreach (var row in _index)
        {
            // A node freed since the last DropFreed is still in the index, and an airframe swap
            // frees the aircraft it staged. Handing it out would be handing out a disposed object.
            if (!_isLive(row.Node))
            {
                continue;
            }
            var matches = match(row.SrcName)
                || (row.SrcName.EndsWith(".flt", StringComparison.OrdinalIgnoreCase)
                    && match(row.SrcName[..^4]));
            if (matches && (scope is null || IsWithin(row.Node, scope)))
            {
                result.Add(row.Node);
            }
        }
        _findCache[key] = result;
        return result;
    }

    /// <summary>Resolves a single node name for one definition, the compiled symbol table first
    /// (<see cref="SymbolClaims"/>), then the scoped tier chain. A name the symbol table claims
    /// but binds to nothing (index not built) still falls through to <see cref="ResolveScoped"/>:
    /// the caller decides what an unbuilt claim means (see <c>AnimRuntime.Targets</c>' strictly
    /// anchor-scoped rescue), this convenience form keeps the pre-existing fall-through.</summary>
    public TNode? Resolve(string name, AnimDefinition def, TNode? anchor)
    {
        if (SymbolClaims(def, name, anchor, out var bound) && bound != null)
        {
            return bound;
        }
        var found = ResolveScoped(new List<string> { name }, def, anchor);
        return found.Count > 0 ? found[0] : null;
    }

    /// <summary>Resolves a NAME path, narrowest scope first: the call anchor's subtree, then the
    /// definition's OWN template roots (<c>ownRootsOf</c>), then the whole index. That last tier is
    /// refused under <c>LOCAL_NODES_ONLY</c> and by <see cref="RefusesGlobalTier"/>.
    /// Every tier is filtered by <see cref="AdmissibleStaging"/>, and a null or dead anchor skips
    /// the scoped ones. ⚠ Keep this order: <see cref="ResolvePath"/> is private so no caller can
    /// compose it differently. Callers must treat the result as read-only.</summary>
    public List<TNode> ResolveScoped(IReadOnlyList<string> path, AnimDefinition def, TNode? anchor)
    {
        if (anchor == null || !_isLive(anchor))
        {
            return AdmissibleStaging(ResolvePath(path, null, localOnly: true), def, null);
        }
        var found = AdmissibleStaging(ResolvePath(path, anchor, localOnly: true), def, anchor);
        if (found.Count == 0)
        {
            found = ResolveInOwnRoot(path, def, anchor);
        }
        if (found.Count == 0 && !def.LocalNodesOnly)
        {
            if (RefusesGlobalTier(def, path))
            {
                GlobalTierRefused++;
            }
            else
            {
                found = AdmissibleStaging(ResolvePath(path, null, localOnly: true), def, anchor);
            }
        }
        return found;
    }

    /// <summary>Whether <see cref="ResolveScoped"/> refuses this write the global tier. It does when
    /// the world holds several instances of the definition and the name carries no wildcard. Several
    /// instances means a NAME wildcard or an <see cref="Anchors"/> root lift. Such a plain name
    /// (<c>healthy</c>, <c>door1</c>) is that one instance's, so a miss there resolves to nothing
    /// instead of reaching every twin. A wildcard in the name stands in for the digit the original
    /// stamps into it, and keeps the tier. Decode: docs/org/sequences.md, the tier chain.</summary>
    public bool RefusesGlobalTier(AnimDefinition def, IReadOnlyList<string> path)
    {
        if (def.Name.IndexOfAny(Wildcards) < 0 && !_rootLifted.Contains(def))
        {
            return false;
        }
        foreach (var element in path)
        {
            if (element.IndexOfAny(Wildcards) >= 0)
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>The world nodes a definition anchors to: NAME matches, narrowed to the twin holding
    /// the def's symbol-table ROOT node when NAME is shared; else ANIMATION_ROOT_NAME matches
    /// lifted to their parent instance root, refused above <see cref="MaxRootLift"/> or under
    /// <see cref="SuppressRootLift"/>. Returns a fresh list each call.
    /// ⚠ The ONE census-recording resolution call, once per definition identity, every other
    /// query stays census-free.</summary>
    public List<TNode?> Anchors(AnimDefinition def)
    {
        // Multi-target NAME1 defs (zeppelin nacelles/turrets) parse with an empty NAME but anchor
        // through their own (pattern, path) pairs, never the ANIMATION_ROOT_NAME lift, which would
        // anchor them onto every building.
        if (string.IsNullOrEmpty(def.Name))
        {
            if (def.MultiTargets.Count == 0)
            {
                return new List<TNode?>();
            }
            var multi = MultiTargetAnchors(def);
            RecordAnchoring(def, multi.Count > 0 ? AnchorKind.ByName : AnchorKind.None);
            return multi;
        }
        var (anchors, how) = ComputeAnchors(def);
        // Outside the census gate on purpose: RefusesGlobalTier reads this on every resolve,
        // where the census is closed and ReportResolution is off.
        if (how == AnchorKind.ByRootLift)
        {
            _rootLifted.Add(def);
        }
        RecordAnchoring(def, how);
        return anchors;
    }

    /// <summary>The symbol-table authority: true when the definition's symbol table CLAIMS
    /// <paramref name="name"/>, the caller must not fall back to global name matching, which
    /// resolves C1's <c>caboose</c> to both the real consist and an unrelated <c>caboose.flt</c>.
    /// <paramref name="node"/> is the bound world node, or null when never built (a dropped LOD, a
    /// skipped subtree, or any lookup on a <see cref="NameResolveFallback"/> runtime). False means
    /// the symbol table has no opinion and name resolution proceeds as usual.</summary>
    public bool SymbolClaims(AnimDefinition def, string name, out TNode? node) =>
        SymbolClaims(def, name, null, out node);

    /// <summary><see cref="SymbolClaims(AnimDefinition, string, out TNode?)"/> for a definition
    /// dispatched onto <paramref name="anchor"/>: inside a staged library copy the claim is
    /// narrowed to that copy, the private subtree the original hands the definition
    /// (org/sequences.md). A bound node outside the copy yields to the copy's own node of that
    /// name; a name the copy lacks keeps its binding. ⚠ A cross-archive symbol table binds by
    /// index, so a parked archive figure would otherwise answer for the staged actor.</summary>
    public bool SymbolClaims(AnimDefinition def, string name, TNode? anchor, out TNode? node)
    {
        node = null;
        if (!def.NodeRefs.TryGetValue(name, out int idx))
        {
            return false;
        }
        node = ClaimedNode(idx, name, anchor);
        return true;
    }

    /// <summary>The world node a compiled gamez index binds, narrowed to <paramref name="anchor"/>'s
    /// staged library copy the way <see cref="SymbolClaims(AnimDefinition, string, TNode?, out TNode?)"/>
    /// narrows a symbol-table claim; null when this build never created it. For an index a
    /// definition carries outside its symbol table, such as a node prerequisite's leaf.</summary>
    public TNode? ClaimedNode(int idx, string name, TNode? anchor)
    {
        var node = BoundNode(idx) ?? (NameResolveFallback ? null : SoleStagedCopy(idx));
        if (node != null && anchor != null && _isLive(anchor)
            && _privateCopyOf(anchor) is { } copy && !IsWithin(node, copy))
        {
            var own = FindAll(name, copy);
            if (own.Count > 0)
            {
                node = own[0];
            }
        }
        return node;
    }

    /// <summary>The world node bound to a gamez node index, or null when this build never created
    /// it. The by-index map is the only way to reach a node the caller cannot name, which is what
    /// an area-selected toggle needs; it is empty on a <see cref="NameResolveFallback"/> runtime.</summary>
    public TNode? ByGamezIndex(int index) => BoundNode(index);

    /// <summary>Opens the bind-census window (a no-op unless <see cref="ReportResolution"/>): the
    /// owning runtime calls this before its bootstrap passes and <see cref="CloseCensus"/> after
    /// them, so the census is a statement about what the BIND could reach, a later runtime miss
    /// has its own reporting and must not restate it.</summary>
    public void OpenCensus() => _censusOpen = ReportResolution;

    public void CloseCensus() => _censusOpen = false;

    /// <summary>Records an event that named a node the bind could not reach (inside the census
    /// window only). <paramref name="why"/> separates the two causes, which otherwise read the
    /// same: the compiled symbol table bound the name to a gamez node this build never created,
    /// versus name resolution finding no match at all.</summary>
    public void RecordMissingTarget(AnimDefinition def, string refName, string why)
    {
        if (!_censusOpen)
        {
            return;
        }
        _censusMissing++;
        if (_censusMissingTargets.Count < CensusCap)
        {
            string label = def.AnimName is { Length: > 0 } anim ? $"{anim}@{def.Name}" : def.Name;
            _censusMissingTargets.Add($"{label} ref={refName} why={why}");
        }
    }

    /// <summary>The bind-time resolution census, ready to log, empty unless
    /// <see cref="ReportResolution"/> was set. Two failures look identical from outside a partial
    /// world: a definition that never instantiated (no handler ever fires) versus one running whose
    /// event names a node this subtree lacks. The lines name which happened, per definition.
    /// <c>root_lift_suppressed</c> is the line nobody expects: a lift the full world would refuse
    /// (docs/architecture.md, this file's entry) but a partial subtree would silently allow.</summary>
    public IReadOnlyList<string> ResolutionLines()
    {
        var lines = new List<string>();
        if (!ReportResolution)
        {
            return lines;
        }
        int defs = _censusAnchored + _censusNarrowed + _censusLifted + _censusSuppressed
                   + _censusUnanchored;
        lines.Add($"bind census defs={defs} anchored_by_name={_censusAnchored} "
                  + $"narrowed_by_symbol={_censusNarrowed} "
                  + $"anchored_by_root_lift={_censusLifted} root_lift_suppressed={_censusSuppressed} "
                  + $"unanchored={_censusUnanchored} target_missing_ops={_censusMissing}");
        if (_censusUnanchored > 0)
        {
            lines.Add($"bind unanchored={_censusUnanchored}, no handler ever fires for these: "
                      + Sample(_censusUnanchoredNames, _censusUnanchored));
        }
        if (_censusLifted > 0)
        {
            lines.Add($"bind root_lifted={_censusLifted}, anchored only because this subtree has "
                      + $"≤{MaxRootLift} of the def's ANIMATION_ROOT_NAME, which the full world does not: "
                      + Sample(_censusLiftedNames, _censusLifted));
        }
        if (_censusSuppressed > 0)
        {
            lines.Add($"bind root_lift_suppressed={_censusSuppressed}, these WOULD have anchored on "
                      + $"this subtree's generic ANIMATION_ROOT_NAME children, which the full world's "
                      + $"node count rules out; refused so the stage shows only defs that name it: "
                      + Sample(_censusSuppressedNames, _censusSuppressed));
        }
        if (_censusMissing > 0)
        {
            lines.Add($"bind target_missing={_censusMissing}, the def IS running, the node is not in "
                      + $"this subtree: " + Sample(_censusMissingTargets, _censusMissing));
        }
        return lines;
    }

    private static string Sample(List<string> shown, int total) =>
        string.Join(", ", shown) + (total > shown.Count ? $", … (+{total - shown.Count} more)" : "");

    // A claimed index the world build never created, answered by the one pooled copy carrying it.
    // ⚠ Only when exactly one live copy exists: a multi-copy effect pool keeps its copies apart by
    // anchor scope, and binding the first would steal every later copy's events (see Add). The
    // single-copy case is a mission's staged library actor (CM07's pickup switch, sensor and
    // passenger), which the definitions that toggle it reach only through their symbol table.
    private TNode? SoleStagedCopy(int index)
    {
        if (_soleCopyCache.TryGetValue(index, out var cached))
        {
            return cached != null && _isLive(cached) ? cached : Rescan();
        }
        return Rescan();

        TNode? Rescan()
        {
            TNode? sole = null;
            int seen = 0;
            foreach (var row in _index)
            {
                if (row.GamezIndex == index && _isLive(row.Node))
                {
                    sole = row.Node;
                    seen++;
                }
            }
            var answer = seen == 1 ? sole : null;
            _soleCopyCache[index] = answer;
            return answer;
        }
    }

    // The by-index map's one reader. A freed claimant answers null and gives the slot up, so the
    // next Add for that index takes it: see Add's own remark.
    private TNode? BoundNode(int index)
    {
        if (!_byIndex.TryGetValue(index, out var node))
        {
            return null;
        }
        if (_isLive(node))
        {
            return node;
        }
        _byIndex.Remove(index);
        return null;
    }

    // A parent->child NAME path: the first element within scope (falling back to the whole index
    // when that misses and localOnly is false), then each further element inside the previous
    // matches. ⚠ Private on purpose: this is ResolveScoped's only route, keeping call-site variants
    // impossible. Every tier call passes localOnly: true; the global step is a tier of its own.
    private List<TNode> ResolvePath(IReadOnlyList<string> path, TNode? scope, bool localOnly)
    {
        var candidates = FindAll(path[0], scope);
        if (candidates.Count == 0 && scope is not null && !localOnly)
        {
            candidates = FindAll(path[0], null);
        }
        for (int i = 1; i < path.Count && candidates.Count > 0; i++)
        {
            var next = new List<TNode>();
            foreach (var c in candidates)
            {
                foreach (var n in FindAll(path[i], c))
                {
                    if (!_identity.Equals(n, c))
                    {
                        next.Add(n);
                    }
                }
            }
            candidates = next;
        }
        return candidates;
    }

    // The middle tier: the path resolved inside the definition's own template root(s), the same
    // copies the owner places at a call site, de-duplicated in first-seen order. Census-free by
    // construction: the hook and this method go through FindAll only (see Anchors' own remark).
    private List<TNode> ResolveInOwnRoot(IReadOnlyList<string> path, AnimDefinition def, TNode? anchor)
    {
        var found = new List<TNode>();
        if (string.IsNullOrEmpty(def.Name))
        {
            return found;
        }
        foreach (var root in _ownRootsOf(def, anchor))
        {
            foreach (var node in AdmissibleStaging(ResolvePath(path, root, localOnly: true), def, root))
            {
                if (!ContainsIdentity(found, node))
                {
                    found.Add(node);
                }
            }
        }
        return found;
    }

    /// <summary>Every tier's scope correction: drops candidates the owner refuses this definition
    /// (<c>stagingAdmits</c>), i.e. copies staged under a template root neither the definition nor
    /// the scope being searched belongs to. Off a pooled runtime nothing is refused and this
    /// returns its input untouched.
    /// ⚠ Never mutate the argument, it is <see cref="FindAll"/>'s memoized list, shared with every
    /// other query for the same (pattern, scope).</summary>
    // ⚠ Every tier needs this, not only the first: narrowing one tier alone just hands the same
    // foreign copy to the next one down. Why the pool needs excluding at all: docs/architecture.md,
    // this file's entry, and docs/org/sequences.md's "The definition owns a private copy".
    private List<TNode> AdmissibleStaging(List<TNode> candidates, AnimDefinition def, TNode? scope)
    {
        if (candidates.Count == 0)
        {
            return candidates;
        }
        List<TNode>? kept = null;
        for (int i = 0; i < candidates.Count; i++)
        {
            if (_stagingAdmits(def, scope, candidates[i]))
            {
                kept?.Add(candidates[i]);
            }
            else if (kept == null)
            {
                kept = new List<TNode>(candidates.Count - 1);
                for (int j = 0; j < i; j++)
                {
                    kept.Add(candidates[j]);
                }
            }
        }
        return kept ?? candidates;
    }

    private bool ContainsIdentity(List<TNode> list, TNode node)
    {
        foreach (var n in list)
        {
            if (_identity.Equals(n, node))
            {
                return true;
            }
        }
        return false;
    }

    // The NAME1 anchor set: each pair's authored path resolved parent→child against the whole
    // index, unioned in first-seen order. Census-free below Anchors, like ComputeAnchors.
    private List<TNode?> MultiTargetAnchors(AnimDefinition def)
    {
        var seen = new HashSet<TNode>(_identity);
        var anchors = new List<TNode?>();
        foreach (var (_, path) in def.MultiTargets)
        {
            foreach (var node in ResolvePath(path, null, localOnly: true))
            {
                if (seen.Add(node))
                {
                    anchors.Add(node);
                }
            }
        }
        return anchors;
    }

    // The census-free anchor computation Anchors wraps, the internal path for anything that must
    // not re-enter the census (see Anchors' own remark); the tier chain stays census-free the same
    // way, through FindAll/ResolvePath only.
    private (List<TNode?> Anchors, AnchorKind How) ComputeAnchors(AnimDefinition def)
    {
        var anchors = FindAll(def.Name, null).Cast<TNode?>().ToList();
        var how = anchors.Count > 0 ? AnchorKind.ByName : AnchorKind.None;
        if (NarrowToSymbolRoot(def, anchors) is { } only)
        {
            anchors = only;
            how = AnchorKind.BySymbol;
        }
        if (anchors.Count == 0 && def.RootName != null)
        {
            var roots = FindAll(def.RootName, null);
            if (roots.Count > 0 && roots.Count <= MaxRootLift)
            {
                if (SuppressRootLift)
                {
                    how = AnchorKind.LiftSuppressed;
                }
                else
                {
                    // Lift each ROOT match to its parent (the instance root), de-duplicated in
                    // first-seen order, parents come from the Add-time snapshot, not the live tree.
                    var seen = new HashSet<TNode>(_identity);
                    var lifted = new List<TNode?>();
                    foreach (var root in roots)
                    {
                        if (ParentOf(root) is { } p && seen.Add(p))
                        {
                            lifted.Add(p);
                        }
                    }
                    anchors = lifted;
                    how = anchors.Count > 0 ? AnchorKind.ByRootLift : AnchorKind.None;
                }
            }
        }
        return (anchors, how);
    }

    // Picks the one instance a compiled def belongs to when its NAME matches several, C1's two
    // airfield hangars are both `air_gen`, told apart only by their symbol tables
    // (docs/formats/destructibles.md). Resolves ANIMATION_ROOT_NAME through the symbol table and
    // keeps only anchors containing that exact node. Null means undecidable (a reader def, an
    // unbuilt index, a root outside every candidate) and leaves the name match standing; ⚠ that is
    // not the same as an empty narrowing, which never leaves this method.
    private List<TNode?>? NarrowToSymbolRoot(AnimDefinition def, List<TNode?> anchors)
    {
        if (anchors.Count < 2
            || def.RootName is not { } root
            || !def.NodeRefs.TryGetValue(root, out int idx)
            || BoundNode(idx) is not { } exact)
        {
            return null;
        }
        var kept = new List<TNode?>();
        foreach (var a in anchors)
        {
            if (a != null && IsWithin(exact, a))
            {
                kept.Add(a);
            }
        }
        return kept.Count > 0 && kept.Count < anchors.Count ? kept : null;
    }

    // One census entry per definition identity (anchor name + animation name, AnimProgram's own
    // dedupe key), because the bootstrap asks for a def's anchors on more than one pass.
    private void RecordAnchoring(AnimDefinition def, AnchorKind how)
    {
        if (!_censusOpen || !_censusSeen.Add((def.Name, def.AnimName ?? "")))
        {
            return;
        }
        string label = def.AnimName is { Length: > 0 } anim ? $"{anim}@{def.Name}" : def.Name;
        switch (how)
        {
            case AnchorKind.ByName:
                _censusAnchored++;
                break;
            case AnchorKind.BySymbol:
                _censusNarrowed++;
                break;
            case AnchorKind.ByRootLift:
                _censusLifted++;
                if (_censusLiftedNames.Count < CensusCap)
                {
                    _censusLiftedNames.Add($"{label}→{def.RootName}");
                }
                break;
            case AnchorKind.LiftSuppressed:
                _censusSuppressed++;
                if (_censusSuppressedNames.Count < CensusCap)
                {
                    _censusSuppressedNames.Add($"{label}→{def.RootName}");
                }
                break;
            default:
                _censusUnanchored++;
                if (_censusUnanchoredNames.Count < CensusCap)
                {
                    _censusUnanchoredNames.Add(label);
                }
                break;
        }
    }

    // node is within scope's subtree, inclusive of scope itself, walks the recorded parent chain
    // rather than the live tree (see FindAll's own remark on reparenting).
    // ⚠ Never leave a freed node as a KEY in _parentOf. Every step here hashes against its
    // buckets, so a dead key throws in this walk rather than in Add; DropFreed is the sweep.
    private bool IsWithin(TNode node, TNode scope)
    {
        for (TNode? p = node; p is not null; _parentOf.TryGetValue(p, out p))
        {
            if (_identity.Equals(p, scope))
            {
                return true;
            }
        }
        return false;
    }

    private TNode? ParentOf(TNode node) => _parentOf.TryGetValue(node, out var p) ? p : null;

    // Wildcard NAME -> predicate: '*' matches at most one digit, '#' a run of digits (including
    // zero). Plain names compare exactly, case-insensitively. ⚠ '*' is not "any run": the original
    // stamps one digit per star (docs/org/sequences.md, the odometer), so a shared `crate**`
    // destructible must never attach to `craterlake` and switch every world `healthy` off. The
    // digit is optional because the shipped compiler instanced `lkshadow*` as plain `lkshadow`.
    private Func<string, bool> Matcher(string pattern)
    {
        if (_matcherCache.TryGetValue(pattern, out var cached))
        {
            return cached;
        }
        Func<string, bool> match;
        if (pattern.Contains('*') || pattern.Contains('#'))
        {
            var re = new Regex("^" + Regex.Escape(pattern).Replace("\\*", "[0-9]?").Replace("\\#", "[0-9]*") + "$",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            match = re.IsMatch;
        }
        else
        {
            match = s => s.Equals(pattern, StringComparison.OrdinalIgnoreCase);
        }
        return _matcherCache[pattern] = match;
    }

    private readonly record struct IndexRow(TNode Node, string SrcName, int? GamezIndex);

    private sealed class ScopeKeyComparer : IEqualityComparer<(string Pattern, TNode? Scope)>
    {
        private readonly IEqualityComparer<TNode> _inner;

        public ScopeKeyComparer(IEqualityComparer<TNode> inner) => _inner = inner;

        public bool Equals((string Pattern, TNode? Scope) x, (string Pattern, TNode? Scope) y) =>
            string.Equals(x.Pattern, y.Pattern, StringComparison.Ordinal)
            && (x.Scope is null ? y.Scope is null : y.Scope is not null && _inner.Equals(x.Scope, y.Scope));

        public int GetHashCode((string Pattern, TNode? Scope) obj) =>
            HashCode.Combine(obj.Pattern, obj.Scope is null ? 0 : _inner.GetHashCode(obj.Scope));
    }
}
