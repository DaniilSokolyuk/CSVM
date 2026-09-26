# The animation-sequence runtime, decoded from `crimson.exe`

Read out of the retail executable with Ghidra (static analysis of the shipped x86 build,
`crimson.exe`, `language x86:LE:32:default`), and cross-checked against the original running at
60 fps and at 120 fps, against install-wide censuses of the compiled event archives, and against
timed measurements of our own re-implementation. Every claim below names the function, the census,
or the measurement it came from.

Everything here is a description of *behaviour and constants*. No decompiler output is reproduced;
the addresses are given so any claim can be re-checked at source.

**Where the other halves live.** The authored side, the definition/sequence/event schema, the
`START_TIME` encoding, `growth_factors`, the `ACTIVATION` values, the compiled-vs-reader
divergences, is [`formats/anim-definitions.md`](../formats/anim-definitions.md), which states the
mechanisms this page decoded but does not repeat the decode: every address, struct offset and
handler trace for the animation runtime belongs here. Our
implementation is `CSVM/src/Mech3/SequenceRunner.cs` (the interpreter, engine-free behind a
3-member host seam), `CSVM/src/Mech3/AnimRuntime.cs` (the host: event dispatch, condition
evaluation, instance lifetime) and `CSVM/src/Mech3/AnimDefs.cs` (the reader-form normalizer). The
headless charter that pins every rule on this page is `CSVM.Tests/SequenceRunnerTests.cs`. This
page is the original's runtime: what the engine does with an authored sequence once it is running.

⚠ **This page is a decode, not a proposal.** Where it disagrees with a footage measurement, the
decode wins and the disagreement is a note. Where CSVM deliberately differs, that is listed at the
bottom rather than hidden.

## Function map

| Address | Role |
|---|---|
| `FUN_004ecbb0` | The sequence stepper, dispatches the event at the cursor, advances it, and returns a state code. A LOOP rewind always **returns** (state 4), so the rewound pass can only begin on the FOLLOWING tick |
| `FUN_004ebfa0` | The sequence reset, rewind and re-arm: `state ← +0x21`, `event ptr ← +0x38`, both timers to 0 |
| `FUN_004ebfd0` | The `LOOP` handler, the u16 pass counter at `+0x30`, the increment-then-compare termination test, the `-1` infinite special case, and the 86,400 s clamp on the instance clock |
| `DAT_00727de0` | The event dispatch table, 47 slots, index 1–0x2f, populated by `FUN_004ee1a0` |
| `FUN_004ec080` | The `IF`/`ELSEIF` condition evaluator, and the **false-IF** branch scan, walks forward event by event and breaks on the first ELSE / ELSEIF / ENDIF, with **no depth counter** |
| `004ec5a0` | The **ELSE/ELSEIF fall-through** scan, the same walk with the narrower stop set: ENDIF only |
| `004ec5d0` | `ENDIF`, a bare `MOV EAX,2 / RET`, i.e. a no-op |
| `004eb570` / `004eb610` | `CALL_SEQUENCE` / `STOP_SEQUENCE` |
| `004ecedc`–`004ecf53` | The per-instance **tick walk**, one ascending pass over the sequence array, stepping each slot at most once. The containing function is undefined in the database; the loop block is the citable unit |
| `FUN_00516820` | The reader-side condition parser, one branch per keyword, writing the condition flag word |
| `FUN_004ec6a0` | `FBFX_COLOR_FROM_TO`, dispatch slot 36, the model for "a timed event reports STILL RUNNING until its run time is up" |
| `004e82b0` | `LIGHT_ANIMATION`, dispatch slot 5, advances one tick's worth of the authored delta per dispatch, clamps the last tick to the remainder, and returns still-running on the same test |
| `FUN_004f7120` | `PUFFER_STATE` reader/parser, reached from a sequence's `PUFFER_STATE` event; decoded in [`org/puffer.md`](puffer.md) |
| `FUN_004efaf0` | The node-name tier chain: animation root subtree, main root subtree, the two interned lists, then the world |
| `FUN_004efa70` | The subtree walk the first two tiers use: depth-first, first match wins |
| `FUN_004ef7d0` | The node-reference parser: the `GLOBAL`/`LOCAL` scope words, the `INPUT_NODE` / `MAIN_ROOT_NODE` sentinels, and the interning call |
| `FUN_004ef0c0` / `FUN_004eef30` | Intern a resolved node into the node-reference list / the node-state list, returning the index the event stores |
| `FUN_004e8d60` | The run-time index-to-node lookup, including both sentinels |
| `FUN_0051dcf0` | The definition loader: the name buffers, the private subtree copy, `ANIMATION_ROOT_NAME`, and the sequence-array scan |
| `004eb3e0`–`004eb56d` | The `CALL_ANIMATION` handler. Undefined in the database; the block is the citable unit. `PUSH 0x0` at `004eb53d` is the anchor argument, which is what makes the callee keep its own |
| `FUN_004edf80` | The animation start the handler calls: parks the call site's node at `callee+0x7c` (`INPUT_NODE`) with a position snapshot at `+0x80`, plus a second node/position pair at `+0x8c`/`+0x90` |
| `FUN_004ed8c0` | The start gate, i.e. the restart refusal: the callee's own run state at `+0xa0`, the concurrency bit `0x100` in `+0x9c`, and the instance chain at `+0x10c` |
| `FUN_004ed600` / `FUN_00520910` | The per-start reset that RECOMPUTES the concurrency bit (`004ed65a` clears, `004ed672` sets), and the instance clone it exempts a template into: a fresh `0x110` record plus a deep copy of the template's node tree |
| `FUN_004ccde0` / `FUN_004d04e0` | Arming a record's action callback, and the list append it makes: the new node's `prev` takes the old tail and the old tail's `next` is stored at `004d050d`, so a started definition joins at the **tail** |
| `FUN_004d0010` / `FUN_004cffa0` | The per-frame action dispatcher and the list walk it runs: each node's callback, then a re-read of that node's `next` at `004cffee`, which is what lets the pass reach a node appended during it |
| `FUN_004ecd20` | The per-instance callback the walk calls: charges `+0xb0` the frame delta, publishes it as `DAT_009fd1a8`, then runs the tick walk over the sequence array. Undefined in the database; the entry is the citable unit |
| `FUN_00521180` | The re-anchor: swaps `+0x48`, re-resolves the root name under the new anchor only, then re-resolves every interned entry. `CALL_ANIMATION` never invokes it |
| `FUN_005230d0` | The post-load pass over every record: registers each definition's damage handlers keyed on `activation` (`+0xa1`), **on `*(def+0x6c)`, the animation root node**, never on `+0x48` |
| `FUN_005abbf0` / `FUN_005abb20` | The registration itself: a per-node handler list at `node+0xbc`, two slots of (owner, handler), so several definitions register on one object without displacing each other. Slot 0 is the weapon-hit handler `0x004e7220`; what this decides for a destructible is in [`../formats/destructibles.md`](../formats/destructibles.md) |
| `FUN_004ebc80` | Resolving a callee BY NAME at run time (the stop-style events): the call table's name then its `LOCAL_NAME`, then the global animation array, first match wins, cached back into the event |
| `FUN_0059d610` / `FUN_0059d6e0` / `FUN_0059d750` | The `*` digit odometer: scan and record the positions, step the counters, stamp them into a name |
| `FUN_0051ff40` / `FUN_0051fe60` | The instantiation loop the odometer drives, and the `#` per-object repeat |

The interpreter was compiled from `D:\zipper\gamez\zEffect\zeff_ani*.c`; the image base is
`0x400000`.

**The live sequence struct is mech3ax's `SeqDefInfoC`** (64 bytes), mutated in place by the stepper
on every tick. These are the offsets the runtime reads:

| Offset | Meaning |
|---|---|
| `anim+0xb0` | The **instance** clock, one clock per running definition, shared by every sequence of it |
| `anim+0xcc` | Base of the definition's **sequence array**, `SeqDefInfoC` records, stride `0x40` |
| `anim+0xd8` | Sequence **count**, one byte; the loop bound for every walk of the array |
| `seq+0x20` | Current state |
| `seq+0x21` | Reset state (`SeqDefState`: `Initial`=0, `OnCall`=3) |
| `seq+0x24` | The sequence's own start instant (origin `Sequence`) |
| `seq+0x28` | The previous event's completion instant (origin `Event`) |
| `seq+0x2c` | Accumulated loop time |
| `seq+0x30` | The u16 LOOP pass counter |
| `seq+0x34` / `+0x38` / `+0x3c` | Current event pointer / start pointer / byte size |

⚠ mech3ax's own field-name guesses at offsets 36/40/44 (`loop_time` / `event_time` / `seq_time`)
are mis-ordered against this layout, compare `+0x24`/`+0x28`/`+0x2c` above against offsets 36/40/44
decimal. They are not load-bearing for round-tripping (nothing reads a compiled def by field name),
so the fork's names were left alone; this paragraph is the correction, and renaming the fork's
struct field is explicitly out of scope for this record (it is a round-trip-test surface and belongs
in its own change).

## The handler state machine

Every handler returns one of four values, and the stepper writes the result into the state byte at
`+0x20`:

| Return | Meaning |
|---|---|
| **2** | event complete, advance to the next event |
| **1** | still running, re-dispatch the SAME event next tick |
| **4** | sequence rewound by `LOOP`, re-gate from the top of the sequence, and yield the rest of this tick |
| *(state)* **3** | parked, awaiting a `CALL_SEQUENCE` (not a return value, a resting state) |

The start-time gate is evaluated only when the stepper is in state 0 (freshly advanced past a
completed event) or state 4 (freshly rewound), i.e. only once the previous event has actually
reported completion, never mid-event. An `ON_CALL` sequence that runs off the end of its event list
is rewound and re-parked at state 3, ready to be called again; an `Initial` sequence instead stops
at state 2 and does not re-arm.

## The opcode table

The 47-slot dispatch table at `DAT_00727de0` (populated by `FUN_004ee1a0`, read directly rather
than reconstructed from behaviour) matches mech3ax's `EventType` enum exactly, slot for slot,
**including the null slot 29** (`FUN_004ee1a0` writes `_DAT_00727e54 = 0`), which mech3ax also marks
as a gap. That agreement is an independent confirmation the fork's event numbering is correct, not
an assumption inherited from it. The exe additionally has real handlers at slots 38, 43, 44 and 45
(`LAB_004ecb70`, `LAB_004eac40`, `FUN_004eac90`, `FUN_004eadd0` respectively) that mech3ax leaves
undecoded; no shipped def uses any of the four, so nothing downstream needs them yet.

## A node reference is resolved once, at load, and stored as an index

An event names its target as a string in the authored source, but the running engine never searches
for that string. The name is resolved while the definition is being read, the resolved node is
interned into one of the definition's own lists, and what the event carries from then on is the
small index of that entry. A support-array `ptr` in the extraction is the result of this pass, not a
lookup key the runtime re-evaluates.

### The tier chain

`FUN_004efaf0(def, name, localOnly)` tries these in order and stops at the first hit:

1. A depth-first, first-match walk of the subtree at `def+0x6c`, the **animation root node**
   (`FUN_004efa70`; a node's name is at `node+0`, its child count is the short at `node+0x56`, its
   child pointer array is at `node+0x5c`).
2. The same walk from `def+0x48`, the **main animation root node**, skipped when the two fields hold
   the same node.
3. A linear scan of the definition's node-state list (`def+0xec`, count byte at `def+0xdb`, stride
   `0x2c`, node pointer at `entry+0x24`), comparing each interned node's own name (`FUN_004ee7e0`).
   Index 0 is not a candidate.
4. The same over the second list (`def+0xf4`, count byte at `def+0xdd`) (`FUN_004ee770`).
5. Only when `localOnly` is clear: `FUN_004d0280(7, name)`, the process-wide by-name node lookup,
   which keeps its own cache.

`localOnly` is **bit 21 of the definition flag word at `def+0x9c`**, which is `LOCAL_NODES_ONLY`;
every call site reads it as `*(uint *)(def + 0x9c) >> 0x15 & 1`. A path of several names resolves
element by element: the first goes through the chain above, and each later one is searched inside
the node the previous element resolved to (`FUN_004efa40`).

**Every tier compares the name with a plain `strcmp`, and nothing normalises it.** All four search
functions (`FUN_004efa70`, `FUN_004ee7e0`, `FUN_004ee770`, `FUN_004d0280`) inline a byte-pair
compare with no case folding, so node names are matched **case-sensitively**. `_stricmp` appears in
this path only for keyword tokens, never for a node name: the `ON`/`OFF` argument to
`LOCAL_NODES_ONLY` and the four reserved words `FUN_004ef7d0` consumes. There is also **no `.flt`
suffix stripping** anywhere in the engine; a search of the string table finds no such suffix
handling, and the only normalisation applied to a name is truncation to 31 characters, which logs a
"Truncating name" diagnostic.

⚠ **The interned lists are consulted after the subtree walks, not before them.** Tier 1 and tier 2
are the authority, and a name they both miss is looked up in the definition's own tables only as a
fallback. A re-implementation that treats the compiled symbol table as authoritative has inverted
the order.

### The two roots are different fields

`FUN_0051dcf0` writes both while reading the definition:

- **`def+0x48`, the main animation root node.** The anchor node the definition was created on. Its
  name is copied into the definition's NAME buffer at `def+0x28`; the animation's own name occupies
  `def+0x00`, both 32-byte buffers.
- **`def+0x6c`, the animation root node.** `ANIMATION_ROOT_NAME` is copied to `def+0x4c` and resolved
  through the chain above. When it is absent, or resolves to nothing, `+0x6c` is set to the same node
  as `+0x48`, logged as *"ANIMATION_ROOT_NAME error; unable to find root node… Using main animation
  root node instead"* (`0051e176`).

### The definition owns a private copy of its subtree

Before either field is set, the loader copies the anchor's whole node tree (`FUN_004d8610`) whenever
the global switch at `0072835c` is set and the tree root's type word at `node+0x34` is neither 1 nor
2. Failure is fatal, logged as *"Animation error: Unable to copy node tree."*; a definition that took
the copy is marked with bit `0x80000` in `def+0x9c`, and `+0x48` and `+0x6c` point into the copy.

This is what makes tier 1 safe in the original. The subtree the name is searched in belongs to that
one definition and holds nothing else, so an unrelated instance carrying a node of the same common
name (`pilot`, `geometry`, `healthy`) cannot be found first. The scope is per definition and
exclusive, not a shared region of the world tree that other things are also staged into.

### Parsing, interning, and the two sentinels

`FUN_004ef7d0` reads a node reference, which is either a bare string or a list of names forming a
path, and consumes a leading `GLOBAL` or `LOCAL` scope word (`GLOBAL` clears `localOnly`, `LOCAL`
sets it) before resolving anything. Two names are answered without any lookup at all:

| Authored name | Stored index | Resolved at run time to |
|---|---|---|
| `MAIN_ROOT_NODE` | `-100` | `*(inst+0x48)`, the definition's own main animation root |
| `INPUT_NODE` | `-200` | `*(inst+0x7c)`, the node the call site supplied |

⚠ **`MAIN_ROOT_NODE` is the definition's own root, not the caller's.** The call site's node is the
other sentinel. The two are separate fields on the instance and are not interchangeable.

Anything else is resolved and then interned. `FUN_004ef0c0` appends to the **node-reference list**
(`def+0xe8`, count byte at `def+0xda`, stride `0x2c`, node pointer at `entry+0x28`, preceded by a
nine-dword snapshot of the node at `entry+4`) and returns the index the event stores;
`FUN_004eef30` does the same for the 1-based **node-state list** (`def+0xe4`, count byte at
`def+0xd9`, stride `0x5c`, node pointer at `entry+0x24`). Both return an existing index when the
node is already interned, both cap at 255 entries and log an overflow there, and a name that
resolves to nothing logs *"Animation error: Unable to find animation node."*

### `*` and `#` multiply INSTANCES, never matches

Both wildcards act on the definition's own NAME at load, deciding **how many animation instances get
created**. Neither is a matcher, and no node name is ever compared as a pattern (see the `strcmp`
rule above).

- **`*` is a digit odometer.** `FUN_0059d610` scans the name right to left from its last character
  (`~uVar2 - 2` is `strlen - 1`, the same index `FUN_0051ff40` tests for a trailing `#`), so a
  trailing `*` is a digit position like any other, and records up to **5** `*` positions, zeroing a
  counter for each.
  `FUN_0059d6e0` steps the odometer (each digit runs 0 to 9, carrying, returning 0 when exhausted),
  and `FUN_0059d750` stamps the current counters into a copy of the name with `sprintf("%d", …)`,
  one character per `*`. `FUN_0051ff40` (with `FUN_0051fe60`) runs the whole instantiation once per
  digit tuple. ⚠ Within one instance every `*` in every event name takes **the same** digit; the
  variation is across instances, not inside one.
- **`#` is a per-object repeat**, and applies only to the animation's NAME, only on the first path
  element (`param_5 == 1` in `FUN_0051ff40`) and only as the **last character**, which is stripped.
  It sets a repeat flag that keeps pulling the next object of that name out of the global type-7
  enumeration (`FUN_004d1150`), creating a **separate animation instance per matching object**.

This is how one authored definition serves six identically-shaped world objects: six instances, each
with its own anchor and its own interned single-pointer tables, not one instance reaching six sets of
generically-named nodes. A re-implementation that instead reads `*`/`#` as a node-name pattern has
put the multiplicity in the wrong place, and will move every instance's copy of a shared name at
once.

### At run time the reference is a table lookup

`FUN_004e8d60(inst, index)` is the whole of it: a non-negative index reads
`*(*(inst+0xe8) + 0x28 + index*0x2c)`, `-100` and `-200` return the two fields above, and anything
else returns nothing. No name comparison happens on the event path.

### Where CSVM stands against this

`NameResolver`'s scope chain is the same shape as tiers 1, 2 and 5, and `LOCAL_NODES_ONLY` gates the
last tier the same way. A second gate stands beside it, and that one has no counterpart in the
original because it is a consequence of resolving late: **a plain name written by a definition the
world holds several instances of is refused the global tier** (`NameResolver.RefusesGlobalTier`;
several instances means a NAME carrying a `*` or `#`, or an `ANIMATION_ROOT_NAME` root lift). The
original binds each event's name once, at load, inside the instance's own copy of the subtree, and
logs a name it cannot find as *"Animation error: Unable to find animation node."*; ours resolves at
dispatch, so without that gate a generic name the instance lacks (`healthy`, `destroyed`, `door1`)
falls to the whole index and the write lands on every same-named node in the world. Refused, it
resolves to nothing and the write is a counted, logged no-op (`AnimRuntime.Targets`,
`NameResolver.GlobalTierRefused`). Two names keep the tier: one written by a definition anchored by
an exact NAME, which is one object, and one carrying a wildcard of its own, which stands in for the
digit the odometer stamps into it and so still names the instance's own family
(`locklear_gasbag.zrd.json`'s `lkshw*` writing `lkshb*` is the install's only such write).

Four differences remain and none of them is deliberate:

- `Resolve` and `AnimRuntime.Targets` consult the symbol table **first**, where the original consults
  its interned lists at tiers 3 and 4.
- **A tier returns a LIST and the event is applied to every element** (`ResolveScoped`, and
  `AnimRuntime.Targets` over its result), where the original binds exactly one node pointer per
  reference, once, at load. Nothing in the original can drive two nodes from one event, so any
  cross-instance over-trigger in ours is structural rather than a mis-tuned scope.
- **`FindAll` compares `OrdinalIgnoreCase`**, where every tier of the original is case-sensitive; and
  it also matches a name against a `.flt`-stripped copy of each candidate, which the original has no
  counterpart for. Both widen a match the original would refuse.
- **`Matcher` reads `*` and `#` as node-name patterns** (`*` at most one digit, `#` a digit run), where in
  the original they are instantiation controls on the definition's NAME and no node name is ever
  matched as a pattern. This is the same multiplicity in the wrong place described above. The digit
  reading of `*` is what keeps the shared `crate**` destructible off C3's `craterlake`; read as an
  any-run pattern it instances there, on a node with no `healthy` model under it.

The scope of the subtree tiers is **not** among them any more. Ours searches the call anchor's
subtree, which for a crash rig is a root shared with staged template copies, where the original's is
the definition's own exclusive copy; `NameResolver.AdmissibleStaging` closes the gap by filtering
every tier so a pooled copy answers only the definition that owns or reaches it
(`docs/architecture.md`, that file's entry). The correction is to the SCOPE of the tiers, never their
order.

⚠ **A `CALL_ANIMATION` anchors its callee on the CALLER's node, and that is not a scope the original
would search.** Tiers 1 and 2 read `def+0x6c` and `def+0x48`, both of them inside the copy the
definition was handed; the call site is reachable only through the `INPUT_NODE` sentinel. So where a
callee has a staged copy of its own, a name both copies carry must answer from the callee's, and
`StagingAdmits` refuses the caller's copy for exactly that case (`HasOwnCopyBeside`). The sonic
ground burst is the worked example. `sonic_puff1` is called on the caller's `sonic_emit1` and
translates `sonic_emit1` from 0 to 30 m over 1.2 s, which is how the vapour column is drawn; the
caller then hangs `ring_down1`, both light sequences and its second `call_flare` on that same name at
+1.2 s. Resolved against the caller's copy, the column drags the anchor and the ground ring is placed
30 m into the air instead of the metre over the surface its own `-5` site offset and `+6` translate
put it at.

## Three clocks, and which origin reads which

An event's `START_TIME` carries an ORIGIN and a delay, and the origin selects the clock:

- **`Animation`**, absolute against the INSTANCE clock (`anim+0xb0`). That clock starts when the
  definition starts and is never rewound; a LOOP rewinds only the sequence's own timers.
- **`Sequence`**, absolute against this sequence's start (`seq+0x24`).
- **`Event`**, and an ABSENT start alike, relative to the previous event's **completion**
  (`seq+0x28`), i.e. when it fired plus its own run time.

The `Animation`/`Sequence` split is not academic: a sequence a `CALL_SEQUENCE` started partway
through the animation runs on a sequence clock that begins at zero while the instance clock is
already running. **191 shipped events read the difference**, `ap_light_seq`'s `LightAnimation`
chain, `chuteman_drop`, and the 10 s puffer shut-off on every rocket and torpedo trail. Gated
against the called sequence's own clock instead, a `Animation 1.5` on a sequence called at t=1.0 s
fires a full second late.

⚠ **An absent start ENCODES as `Animation + 0.0` and must not be routed through the instance
clock.** The install carries ~36,000 unstamped compiled events; gating them on a clock that is
already running would fire every one of them the instant its sequence starts, whatever else was
still pending.

**The offset belongs to the event that CARRIES it, not to its successor.** Reading the stamp off
the event just fired and applying it to the next one shifts every sequence in the install by one
slot: a timestamped event fires one slot early and its unstamped partner one slot late.

### The SWAP-pair stamping rule

C1's `bowl` sign is the clean demonstration and the measurement that settled it. Its compiled
sequence is nine strict `des_on`/`des_off` `OBJECT_ACTIVE_STATE` pairs plus an infinite `Loop`, and
**only the FIRST of each pair carries a timestamp**, the second fires immediately after its
stamped predecessor, so the pair lands together and the sign flashes. Read with the offsets shifted
by one slot, every pair splits: both variants are lit at t=0 and then **nothing at all** for each
gap. Measured face-on at the sign: **38.0 % of frames completely blank** under the shifted reading,
**0 %** under the correct one, the sign disables and re-enables itself instead of flashing.

Control-flow events (LOOP/IF/ELSEIF/…) take no time and so do not advance the previous-completion
instant, but they ARE gated on their own offset. That is what gives the bowl sign's trailing
`Loop {Event 1.2}` its inter-cycle pause: a LOOP branch that hard-resets the gate to zero discards
the authored pause and the sign cycles without one.

## The LOOP

`FUN_004ebfd0` does three things, in this order: increment the u16 pass counter at `+0x30`;
terminate the sequence if the counter now **equals** the authored count; otherwise rewind the
cursor to the top of the sequence. The count is re-read off the event struct on every visit rather
than cached. An authored **`-1` is special-cased infinite** regardless of the counter.

**An authored `0` is infinite as a CONSEQUENCE of this shape, not a case of its own.** The counter
starts at 0 and is incremented *before* the compare, so after the first visit it only ever grows
and can never equal 0 again. Surveyed across the whole install, **26 ground-vehicle route
animations ship `Count 0`**, C1's police, mafia, black_car and truck traffic, C2's and C3/M02's
studebakers, every one of them the LAST event of its sequence, and the original drives all 26
continuously for exactly that reason. Reading 0 as "stop immediately" makes each car drive its
route once and freeze. No normalisation of the value is needed to say so.

⚠ A real u16 wrap would re-hit 0 at pass 65,536. That wrap is not reproduced: it is unreachable
within a session, and `-1` already covers the deliberately-infinite case.

⚠ `FUN_004ebfd0` clamps the instance clock at 86,400 s (one day). Not reproduced, a session never
gets there.

### There is no free first pass

Traced against `FUN_004ecbb0`: a rewind always **returns** (state 4), so the next pass can only
start on the following tick. Pass 1 costs a tick exactly like every other pass, and 200 passes cost
200 ticks. A down-counting reading spends `authored + 1` passes; measured on the `LOOP 200` fixture
at a 1/60 s step that reads **3.3499975 s** against the correct **3.3333309 s**, a full animation
frame long, not a compensating error that happened to land on the right total.

## A LOOP count is a count of authored ANIMATION FRAMES, at 60 Hz of SIM time

`ref_fueltanks`' `fire_n_smoke` is `[PUFFER_STATE, LOOP 200]` and burns **~3 s** in the original:
200/3 ≈ 60. The same burn was then timed against the original **at 60 fps and at 120 fps and took
the SAME time at both**. Per-rendered-frame ticking would have halved it at 120, so the original's
sequence tick is decoupled from rendering and every untimed count is a real authored **duration**:
one authored animation frame is **1/60 s**, and a `LOOP n` over an instantaneous body is a timer of
`n × 1/60` seconds.

⚠ **The animation frame is its own constant, not the simulation step**, though the two are equal
today. "The rate the original's artists counted frames at" and "the rate we step the simulation at"
are independent facts; re-stepping the sim at 1/120 for physics reasons must not halve every
authored animation timer. (Their equality today is also what keeps deterministic captures
byte-identical, exactly one pass per fixed step.)

## The rollover must carry its remainder, never reset the clock to zero

When a loop rolls over it rewinds the sequence clock. **The overshoot past the gate it just
satisfied has to be carried into the next iteration**, `clock − due`, rather than dropped.
Dropping it rounds every iteration up to the next whole step, forever, and the failure has two
distinct faces:

- **Instantaneous iterations** quantise to the RENDER rate instead of the authored 60 Hz. A 144 Hz
  client needs three steps to accumulate 1/60 s, so every authored timer runs at 48 Hz; a 240 Hz
  client runs them 4× fast; a 30 Hz client at half speed.
- **Timed iterations**, a LOOP carrying its own authored period, round that period up to a whole
  step. Measured over the install's **599 timed loops**: a 0.02 s period costs 2 steps instead of
  1.2 at 60 Hz, so the **126 loops carrying it** (`patrolboat`, `ptboat*`, `ftank_boom*`,
  `m_build0*`, `pass_plane0*`, `sub_destruction`, `balloont_die*`, `refuel*`) run at **60 % speed**.
  C3/M05's `ww_balmoral1/2/3` (`LOOP 1000` at a 0.01 s period, authored 10 s) take **16.7 s** at
  60 Hz, 12.5 s at 240 Hz, and only reach 10.0 s at 600 Hz.

**Even periods that divide 1/60 exactly in real arithmetic pay one extra step per iteration**, and
this is the detail that makes the carry non-negotiable: sixty float32 additions of 1/60 land just
*under* 1.0, and the gate is `>=`. Without the carry that is 61 steps per second, forever, on every
route loop, a car running 1.7 % slow for the whole session.

Carried, the residual is bounded by **one step**, never by the count: it cannot compound over
hundreds of iterations, which is the property the long-horizon fixtures (60 s at periods of 0.02 s,
1.0 s and 2.5 s) exist to pin. A carry applied *twice* is the other failure direction, and runs
fast without bound.

## A loop that carries its own period is TIMED DATA, not a tick counter

The animation-frame pacing above applies only to an iteration that scheduled **no** time. The test
is "did the DATA schedule time?", and an authored offset counts **even when the clock has already
run past it**, because an absolute (`Animation`/`Sequence`) period shorter than one step is
already behind the clock by the time it is gated. Testing "is the gate in the future?" instead
makes it a question about the STEP: `ww_balmoral1/2/3`'s 0.01 s period is below the 0.0167 s
animation frame, reads as instantaneous, collects the frame floor meant for untimed poll loops, and
the three of them run 16.7 s instead of their authored 10 s, reached by a route the floor is
explicitly not supposed to reach.

An authored **0** still means "immediately after the previous event", so the untimed poll idiom
keeps its pacing and its guard.

**A loop whose body takes time must start its next iteration at once**, not yield: it is already
waiting on its gate. With a 1.0 s motion, the loop rolls over on the step the clock reaches 1.0 s
and the motion re-fires within that same step.

## The double-poll guard

A loop over purely instantaneous events is the data's "keep this animation alive" idiom. Two shapes
ship: C1's waterfall, `[PUFFER_STATE ×3, LOOP{-1}]`, whose emitters then run on their own
`TIME_INTERVAL`; and the poll, `If … CallAnimation; Endif; LOOP{-1}`.

Such an iteration is paced to **one pass per authored animation frame**, and the pacing test must
be "did this iteration schedule any time?", **not** "is the clock zero". The rollover resets the
clock, so a clock-zero test reads the next step's `dt` at that point and lets the instantaneous
body run a **second** time before yielding. Measured: every poll loop in the chapter costs double.

The frame floor is a **floor, not an addition**: an authored offset still wins whenever it is the
longer wait (the bowl sign's 1.2 s pause). And a frame gate is not the body scheduling time, count
it as such and the NEXT pass reads as timed and spins ungated.

## The nested IF scan, and the branch stack

The engine walks event by event and breaks on the **first** byte in its stop set, with **no depth
counter**. The two stop sets are different, and must stay separate:

| Jump | Address | Stops at |
|---|---|---|
| A **false** IF/ELSEIF condition | `FUN_004ec080` | ELSE, ELSEIF or ENDIF, whichever comes first |
| The fall-through off a **taken** branch | `004ec5a0` | ENDIF only |

**This is observable, not academic.** 48 shipped sequences nest, every chapter's
`gunhit-*slug_gunhit` and `mag_gunhit-*`, played on every gun impact, and all 48 have one shape:

```
If lod / If range / If weight … Elseif weight … Else Endif / Else Endif / Endif
```

A false OUTER condition lands on the INNER chain's `Elseif` and **re-tests it**, so the impact
light still fires on its 20 % random roll with the LOD gate and the 1 km range gate both failed. A
depth-aware scan skips the whole chain and fires nothing. That reads like a compiler bug in the
original, and it is what the original does.

The original carries **no per-chain "has a branch run yet" state**: it distinguishes a
fall-through from a candidate by ARRIVAL. A branch marker the stepper walked into is dispatched (it
is a fall-through, and jumps to the ENDIF); one that a failed condition's scan landed on is the
next candidate to test. Our runner stands that up with an explicit stack of one flag per open IF,
which is a stand-in for the arrival distinction rather than a model of it.

⚠ **The stand-in has one visible residual.** It needs a chain whose inner IF closes BEFORE the
outer chain's next branch marker: there the depth-free scan pops the one open frame on the inner
ENDIF, the chain behind it reads as unopened, and its ELSE body can run in addition to a taken
ELSEIF. **No shipped definition has that shape**, all 48 nesting sequences close inner-first,
where the two readings agree.

A malformed chain (an ELSE with no open IF) reads as "not taken" and its writes are dropped, so bad
data degrades to **running** the branch rather than faulting, the safe direction, since a skipped
branch poses objects wrongly.

## The condition flag word: fourteen kinds, ten authored

A compiled condition record carries a bitmask at `+0xc`, a node index at `+0x10`, and one or two
values at `+0x14`/`+0x18`. The bit for each token is written by the reader parser `FUN_00516820`,
one branch per keyword, so this mapping is read off the parser rather than inferred from the
evaluator's branch order:

| Bit | Token | Evaluated as (`FUN_004ec080`) |
|---|---|---|
| `0x1` | `RANDOM_WEIGHT` | `table[i] <= value`, see the ring below |
| `0x2` | `PLAYER_RANGE` | `dist²(anchor, player) <= value` (`FUN_004ec540`) |
| `0x4` | `ANIMATION_LOD` | `value <= DAT_00727fc8` (the detail setting) |
| `0x8` | `PLAYER_UNDERCOVER` | a vertical probe from the player (`FUN_004ec410`), see below |
| `0x10` | `NODE_UNDERCOVER` | the same probe from the node |
| `0x20` | `HW_RENDER` | `DAT_009be708` |
| `0x40` | `PLAYER_1ST_PERSON` | `DAT_009fd17c` |
| `0x80` | `PLAYER_BELOW_ALT` | `playerY < value` |
| `0x100` | `NODE_BELOW_ALT` | `nodeY < value` |
| `0x200` | `PLAYER_LINED_UP` | `θ² <= value`, see below |
| `0x400` | `PLAYER_SPEED` | `min <= playerSpeed <= max` |
| `0x800` | `ANIM_HEALTH` | `health <= value` |
| `0x1000` | `ANIM_HEALTH` (two-value) | `min <= health <= max` |
| `0x2000` | `NODE_ACTIVE` | node flag `0x4` at `+0x24` |

**Four of the fourteen are never authored**: `PLAYER_UNDERCOVER`, `PLAYER_BELOW_ALT`,
`PLAYER_LINED_UP` and `PLAYER_SPEED` are engine features the level data never used, which is why
the condition census in [`formats/anim-definitions.md`](../formats/anim-definitions.md) finds ten
kinds and not fourteen. Swept 2026-08-13 over the whole extraction: **0 occurrences** of any of the
four, in either the reader spelling or the compiled one, and 0 for the `PLAYER_NEAR_GROUND` alias
the parser also accepts, against **1,097 occurrences of `PLAYER_RANGE`** on the same sweep as a
control. The reader tokens are the parser's own literal keywords and the reader sources are what
the compiled archives are built from, so that half is a direct test rather than an inference.
Nothing here is a gap in CSVM: there is no shipped condition to evaluate.

### The undercover probe is a signed vertical ray, and its operand is a float bit pattern

`FUN_004ec410` is the whole mechanism behind both undercover bits. It takes the probe node's world
position from `FUN_004cf200`, clears that node's own collidable bit at `+0x24` for the duration of
the cast (`FUN_004cd260` saves it, `FUN_004cd210` restores it) so a body cannot detect itself, sets
the query filter to `0x40000` and the stop-at-first-hit flag (`FUN_004c7620` / `FUN_004c75e0`), and
casts a segment from `(x, y, z)` to `(x, y + d, z)` through the world segment query `FUN_004c8f70`.
That query walks the world's cell grid and reports the first node in a cell carrying both the
visible bit `0x4` and the collidable bit `0x10`. A hit sets the caller's out-parameter to 1; the
`0x10` branch at `004ec080` reads that out-parameter and takes the branch on 1, and answers false
whenever the node index is negative, the node is absent, or the query itself fails.

**`d` is the signed length in metres, so the sign is the direction.** A negative `d` probes DOWN,
which is what the reader spelling `NODE_NEAR_GROUND` describes, and a positive `d` probes UP, which
is what "undercover" describes. `FUN_004ec410`'s 50.0 is only the default for a null operand
pointer, and the condition evaluator always passes the record's own `+0x14` slot, so no shipped
condition takes it.

⚠ **The compiled operand is a raw 4-byte word the extraction types as u32, not a length.** It holds
the IEEE-754 bit pattern of that float: `3263299584` is `0xC2820000` = −65.0, `3229614080` is
`0xC0800000` = −4.0. Every one of the install's 473 occurrences decodes to a round value, and the
census is in [`formats/anim-definitions.md`](../formats/anim-definitions.md). Reading the u32 as a
length gives a probe of billions of metres, which is why the field has to be reinterpreted before
it is compared to anything.

⚠ **`PLAYER_LINED_UP` owns the `* 4.0` that looked like a `PLAYER_RANGE` scale factor.** The
evaluator's `local_18 * 4.0 <= value` sits in the `0x200` branch, and that branch is angular, not
metric: `FUN_004cf380`/`FUN_0053df30` take the node's world matrix to Euler angles, `FUN_0053f610`
builds a quaternion from them, `FUN_0053f9b0` multiplies it against the player quaternion at
`DAT_009fd190` with conjugation, and `FUN_0053fca0` is the quaternion log map, whose `atan2(|v|, w)`
is the **half**-angle. So `|out|² * 4.0` is θ², compared against a threshold the parser stores as
`(degrees × 0.017453292)²`. Both sides are squared radians and the 4 is the half-angle cancelling,
with nothing left over. `PLAYER_RANGE` is the separate `0x2` branch and carries no such factor: the
parser squares its metres argument into `+0x14` (independently confirming the reader-270 ↔
compiled-72900 relation in the format doc's condition table) and the evaluator compares
`dist² <= m²`, which is what CSVM implements. No gate radius is scaled.

Two details of the `0x2` branch, decoded 2026-08-15 (`BL-313`). The distance is measured from the
animation instance's **anchor** node at `+0x6c` (`004ec108`); the condition's own node-index field
at `+0x10` is ignored here, unlike the `NODE_*` branches. And the test is one-shot: it runs when the
stepper dispatches the `IF`/`ELSEIF`, not per frame, and re-runs only if a `LOOP` rewinds the cursor
back over it. ⚠ `FUN_004ec540` returns **0.0** when the node lookup fails, so a failed lookup reads
as "in range" rather than out of it.

**`RANDOM_WEIGHT` reads a 200-entry ring, and that ring is not reproducible.** Each evaluation reads
`table[DAT_0072836c]` and advances that index modulo 200, with the index global across every
definition in the world. The table at `DAT_009fce20` is not compiled data: `FUN_004ee380` fills it at
anim-system init with 200 calls to `rand() * 3.051851e-05` (1/32767, MSVC's `RAND_MAX`), and the
stream it draws from is reseeded `srand(time(NULL))` on ordinary startup and level-load paths, so
two runs of the original produce two different tables. There is no fixed sequence to match, and
CSVM's session-seeded `_rng` is the correct-shape answer rather than a divergence. Disproven in
full, with the two `srand` call sites, in the retired `analysis/anim-interpreter-decode/`
(`git show analysis-archive:analysis/anim-interpreter-decode/FINDINGS.md`); the
comparison sense that came out of it, `draw <= weight` inclusive at both ends, is what CSVM runs.

## CALL_SEQUENCE and STOP_SEQUENCE: a sequence is a single instance

The original keeps a sequence's execution state **inside the definition's own sequence array**, so
a sequence cannot run two copies of itself, and both calls are tiny state writes.

**CALL_SEQUENCE** resolves the name to an index in the definition's OWN array and does exactly one
thing: `if (state == parked) state = running;`. Two consequences, both load-bearing in the shipped
data:

- A call into a sequence that is **already running** is a silent no-op. **77 definitions call one
  sequence from more than one site** (`sonic_ground_effect` calls `sonic_light_seq` twice, `player`
  calls `destroy_craft` three times); each pair must produce ONE run of the body.
- A call naming a sequence whose authored activation is **not `ON_CALL`** is also a no-op, such a
  sequence is never parked, because it runs with the animation and then stops done. **6 definitions
  do this** (`reflight1..6 → refinery_light_seq`).

⚠ The call reports whether the definition **HAS** that sequence, not whether anything started. A
no-op call must still report found, or a `CALL_ANIMATION` fallback fires on a name that was there.

**STOP_SEQUENCE** resolves the name the same way and then unconditionally marks that sequence
DONE. There is no start-if-not-running path anywhere in it. Since a call can only start from
PARKED, **stopping a parked ON_CALL sequence is a DISABLE**: it stays un-callable until the whole
definition resets. **16 shipped definitions hit exactly that**, `flame_ball_01`/`flame_ball_02` →
`stop_p1trail`, in every chapter, inside the HE explosion's call chain, so the teardown those
definitions name simply never runs.

The same rule disables a sequence that was stopped while RUNNING, and shipped data reaches that
case: C1's `car_loop1_start`, `car_go_home_start`, `hauler1_start` and `truck1_start` each run a
lap loop that CALLs a dust or exhaust sequence, STOPs it later in the lap and LOOPs, so from the
second lap on the call lands on a DONE sequence and is refused; the called sequence runs on the
first lap only. CSVM keeps the disable per instance (`AnimInstance`'s stopped set, consulted by
`CallSequence`), and a definition restart, which builds a fresh instance, is the reset. The other
119 definitions that name one sequence in both a call and a stop place the stop after the last
call, so the refusal never fires for them.

A stop also reaches the caller's OWN runner, which is the data's break-out-of-my-own-IF-chain
idiom (`test_player` ×33, `setprop` ×8), and it takes effect within the same tick rather than at
the next one.

⚠ Identity for "is this sequence running" is the sequence OBJECT, not its name, the empty name is
not unique (`he_ground_effect` ships two unnamed sequences), and a name-keyed test collapses them
into one and loses half the burst.

⚠ **A stop ends the ballistic `OBJECT_MOTION` bodies the stopped sequence launched, where they
stand.** The handler (`004eb610`) writes the sequence's state byte to 2 (`004eb69a`), and the
per-instance walk steps a slot only in state 0 or 1 (`004ecee8`-`004ecef1`, inside
`FUN_004ecbb0`). The `OBJECT_MOTION` handler integrates its flight on every call and holds its
event with a still-running return, so once its sequence is stopped nothing integrates the body
again and it freezes in the pose the stop caught, with no bounce dispatched. Every zeppelin death
is built on it: `main_altitude_check` stops `floatdown` half a second after the break, so the hull
halts some 40 to 80 m over the sea while its gasbags fall the rest of the way. CSVM ends those
bodies through `MotionSet.HaltLaunchedBy` and retracts nothing. Other motion kinds and puffers
are left to their own lifetimes; the tweens were not decoded against the walk.

## CALL_ANIMATION hands the call site down as INPUT_NODE, and does not re-anchor the callee

The event is 0x50 bytes, opcode 0x18, parsed by `FUN_00515c00`: the callee's name at `+0x0c`, a flag
byte at `+0x2e`, the call-table index at `+0x32`, the resolved `AT_NODE`/`WITH_NODE` reference index
at `+0x34` and the `OPERAND_NODE` index at `+0x2c`. `AT_NODE` sets flag bit `0x1`, `WITH_NODE` bit
`0x8` and `WAIT_FOR_COMPLETION` bit `0x10`; **both node spellings write the same `+0x34` slot**, and
the index is resolved in the CALLER's namespace at load like any other reference (a failure there
bails the parse with `-1`).

The handler (`004eb3e0`–`004eb56d`) reads the flag byte, fetches the node out of the caller's own
interned reference list, optionally converts it to a world position with `FUN_004cf490`, and starts
the callee at `004eb540` through `FUN_004edf80`. The argument order at that push site is what
settles the semantics:

- **The anchor argument is a literal `PUSH 0x0`** (`004eb53d`). The call site's node goes down as the
  *third* argument instead, which `FUN_004edf80` parks at `callee+0x7c` with a position snapshot at
  `+0x80..+0x88` and a second node/position pair at `+0x8c`/`+0x90..+0x98`. `callee+0x7c` is the field
  the `INPUT_NODE` sentinel reads, so the site reaches the callee **only** through a reference the
  callee itself authors as `INPUT_NODE`.
- With a zero anchor, `FUN_004ed8c0` skips `FUN_00521180` entirely, so **the callee's own node names
  keep resolving against its own `+0x48`/`+0x6c`**. Re-anchoring machinery exists and is thorough
  (swap `+0x48`, re-resolve the root name by subtree search under the new anchor only, killing the
  animation with "Animation node not found" if it misses, then re-resolve every interned entry), but
  no `CALL_ANIMATION` path reaches it.

⚠ This is the decoded half of what [`formats/anim-definitions.md`](../formats/anim-definitions.md)
calls the data's template-instancing mechanism, and it is narrower than "the callee runs on the
caller's node": the target is resolved by the caller and delivered as an input, not substituted for
the callee's anchor. CSVM instead makes the call site the callee's Start anchor
(`AnimRuntime`'s `CallAnimation` arm, `callAnchor = siteNode ?? anchor`), which is an undeliberate
difference; it was investigated as a candidate cause of C3/M02's balloon kill-chain symptoms and
confirmed to drive none of them, since none reproduced on the current build. It does reach the
`NODE_UNDERCOVER` probe: CM01's `cargozep1_crash` is called by a hydrogen tank's death and so runs
anchored on the tank, and a self-exclusion read off that anchor lets the hull's 65 m probe find its
own gasbags at cruise altitude. `AnimRuntime` therefore excludes the callee's own root above the
probed node (`anim_root_name`), and falls back to the anchor only when no such ancestor exists.

### Where the callee's site pose lives, and what the decode leaves open

The callee's own root carries none of the site pose. `FUN_004edf80` (and its siblings
`FUN_004edc50`, `FUN_004ed730`, `FUN_004eddd0`) zero the callee's main root when the instance is a
world-attached template copy (`+0x9c` bit `0x100`, set by `FUN_004ed600` for a root whose type is
neither 1 nor 2): translation `(0, 0, 0)` through `FUN_004d1d50` at `004edfbe` (an Object3d root)
or `FUN_004d2710` at `004edfe3` (a Camera root), rotation zeroed through `FUN_004d1a30` at
`004edfd8` or `FUN_004d2490` at `004edffd`. The
site reaches the callee only as `+0x7c` (node) and `+0x80..+0x88` (position), and the events that
place things read them at their own dispatch: the `OBJECT_TRANSLATE_STATE` handler resolves index
`-200` to `+0x7c`/`+0x80` at `004e8e23`–`004e8e59`, and the `PUFFER_STATE` handler chooses its base
by flag bits (`0x2`: the `+0x7c` node, `0x10`: the `+0x80` position, `0x40`: the second pair at
`+0x8c`/`+0x90`) at `004eaf47`–`004eb1fc`, then converts to a world position through
`FUN_004cf490` and rewrites the event's own fields as literal (`004eafe2`, `004eb094`). Which of
those bits an `AT_NODE` or a `WITH_NODE` call sets, and whether anything re-reads the live node
after that conversion, is not pinned down; the CALL handler's own flag word (`004eb38a`, tested for
`0x1`/`0x2`/`0x4`/`0x8` at `004eb43d`–`004eb4a3`) is read off a different struct offset than the
parser's `+0x2e` byte, and the two were not reconciled. The remake therefore takes the controls'
reading: a zeppelin gun ring's whole death (fire, fireballs, flying parts) moves with the hull in
the original, and `AnimRuntime` follows the call site for every death call regardless of spelling.
Nothing above contradicts that; a reading that settles the flag bits would refine it, not reverse
the placement of a template root.

### The restart refusal is keyed on the callee's own run state, with no anchor in it

`FUN_004ed8c0(callee, anchor)` decides the whole question, and `CALL_ANIMATION` always reaches it
with a zero anchor (`004eb53d` pushes the literal 0 as that argument, immediately before the
`CALL 0x004edf80`). Under that zero anchor the decision reads the run state byte at `+0xa0`
(`004ed8d5`–`004ed8fe`), and for the running state also the concurrency bit below:

| `+0xa0` | State | A call |
|---|---|---|
| 0, 1 | idle: never started, or torn down after a run | starts it, and `004edaa1` writes state 2 |
| 2 | running | refused, returning 0 at `004edab1`, unless bit `0x100` is set (below) |
| 3 | parked at rest | starts it |
| 4 | paused, entered from state 3 | refused |
| 5 | dead: load failure or teardown | refused |
| 6 | paused, entered from state 2 | refused |

The transitions that produce those values are three small functions. `FUN_004ed500` is the pause:
`2 → 6`, any other live state `→ 4`, state 5 left alone. `FUN_004ed480` is the resume: `6 → 2`,
`4 → 3`. `FUN_004ed190` is the teardown: it refuses on state 5, maps `6 → 4`, and takes everything
else **to state 1**, which the gate accepts. So an animation that has run to completion is
immediately callable again, and the gate holds no "already played once" latch; that job belongs to
`INVALIDATE_ANIMATION` alone.

⚠ **State 5 is never a resting state.** It is written on the loader's failure path (`0051deee`,
right after logging *"Unable to copy node tree"* and returning −1), on the tick walk's error path
(`004ecf17`, likewise immediately before a log call), and by `FUN_00520910` and `FUN_004ee4b0`. A
re-implementation that uses one state for "not running" and "failed" will refuse calls the original
accepts.

⚠ **The refusal is silent**, returning 0 with no diagnostic, where every other failure on this path
logs. A call that lands on a running callee leaves no trace at all.

The gate also refuses in states 2 or 3 when `+0xac` is within 0.1 of `-99.0` (`004ed900`–`004ed919`),
and **that branch is unreachable from shipped data**. `+0xac` is the authored `RESET_TIME`: the
loader reads the keyword at `0051f503` and writes the slot at `0051f553`/`0051f566`, and the compiled
record carries the same field at offset 172. Across all 61 archives the only values are −1.0 (13,311
definitions, which the fork reports as `null`, since `hangar3_doors` authors `RESET_TIME [-1.0]` in
its reader source and decodes to `null`), 0.0 (1,651) and 5.0 (one). Nothing is −99, and the 99.0
constant at `00608de8` has exactly one reference in the binary, this test. Treat the branch as
decoded and inert rather than as a rule to reproduce.

Given a NON-zero anchor, which `CALL_ANIMATION` never passes, the gate instead walks the instance
chain at `+0x10c` and refuses only an instance that is both running and already on that anchor,
otherwise reusing a stopped chain entry or cloning through `FUN_00520910`. This is the decoded form
of the semantic `formats/anim-definitions.md` derives observationally from C1/MP1's rearm-door poll,
and where the refusal applies it is **stricter** than that derivation: the original refuses a second
call while the callee runs anywhere, not merely on the same anchor.

### The concurrency bit is the effect template's exemption, and it is per start

⚠ **The refusal above is the bit-CLEAR branch only.** The zero-anchor early return sits inside
`if ((*(def + 0x9c) & 0x100) == 0)`; the bit-SET branch never looks at the anchor. It walks the
`+0x10c` chain to the first entry not in state 2 and, finding none, clones unconditionally through
`FUN_00520910(entry, anchor)` with that same zero anchor. So a `CALL_ANIMATION` on a running callee
either is refused silently or gets a **whole new instance**, and the bit alone decides which.

The bit is not authored and not compiled: it is recomputed at every start. `FUN_004ed600`, which the
gate runs just before writing state 2, clears it at `004ed65a` and sets it again at `004ed672` when
the def's `+0x48` node's TREE ROOT (walked by `FUN_004cf730`, which climbs `+0x58` while the parent
count at `+0x54` is 1 and errors on a node with several parents) carries a type at `+0x34` that is
neither 1 nor 2, with `+0x9c` bit `0x800000` clear. An effect template's root is a parentless gamez
record, so the root the walk reaches is the template itself and its own type decides; a definition
anchored inside the world tree reaches the world's root instead. The two type values are not pinned
down here, and the third guard in that condition is a call whose return Ghidra types as `void`, so
the guard cannot be read off the decompiler.

What the clone actually copies is the reason this matters for effects. `FUN_00520910` allocates a
fresh `0x110`-byte definition record, copies the original's fields into it, clears the new record's
own `0x100`, and for a ZERO anchor deep-copies the template's node tree through `FUN_004d8610`,
marking the clone `0x80000`; the failure path logs *"ERROR: Copying Animation Node Tree"* from
`zeff_ani*.c` line `0x33a7`. Each concurrent call of an effect definition therefore runs on its own
copy of the template's nodes, with no cap in the mechanism. That is the original behind the remake's
finite effect-template pool (`CSVM/data/effect_pools.json`): the pool is one copy per slot rather
than one per call, so a pool shorter than the concurrency a mission authors hands a live effect's
copy to a later call and the running effect moves to the new site.

CSVM's guard (`AnimRuntime`'s `CallAnimation` arm, `!IsLive(target, startAnchor) || movedAway`) is
keyed on `(def, anchor)`, which is the pair the original deliberately leaves out. That makes ours
more permissive across anchors and never stricter, so it can drop a call only when the callee is
already live on that same anchor.

`WAIT_FOR_COMPLETION` (bit `0x10`) is checked at `004eba0e` against a cached instance pointer kept in
the call table (`def+0x104`, stride `0x48`, cache at `entry+0x44`, written back after each start at
`004eb55d` / `004eba04`): a non-null cache in state 2 or 6 yields without advancing, anything else
clears the cache and advances.

⚠ **A callee named at run time resolves to exactly one animation too.** `FUN_004ebc80`, the
stop-style path, matches the call-table entry's name (`+0`) or its `LOCAL_NAME` (`+0x20`), first match
wins, then falls back to a `strcmp` scan of the global animation array (`DAT_009fd14c`, count
`DAT_009fd14a`, stride 0x110) and again takes the first match, caching the index back into the event.

### A started definition joins the tail of the dispatcher's list, and is reached in the same pass

`CALL_ANIMATION` starts nothing itself. `FUN_004ed8c0` resets the callee's sequence records
(`FUN_004ed600` zeroes the instance clock at `+0xb0` and each slot's cursor and timer through
`FUN_004ebfa0`), writes run state 2, and hands the record's action node to `FUN_004ccde0`, which
appends it to the action list its own `+0x44` names (list 1, allocated with that index in
`FUN_004cc4f0`). The append is `FUN_004d04e0`: the new node's `prev` takes the old tail and the old
tail's `next` is stored at `004d050d`, so a start always lands **at the tail**. No event fires
during any of this.

The per-frame dispatcher `FUN_004d0010` walks lists 0 through 6, each through `FUN_004cffa0`, which
calls each node's callback (`obj+0x48`) and then re-reads that node's `next` at `004cffee`. Reading
the link after the callback rather than before it is what decides the ordering: a node appended to
the tail while the walk is in progress is ahead of the cursor, or becomes the current node's own
`next`, so **the walk reaches it in the same pass**, after every node that was already in the list.
There is no start-guard flag to skip it; the node's `+0xc` skip flag is zeroed by the allocator and
is only ever set to retire a node. Removals are the opposite: `FUN_004cffa0` clears the global at
`DAT_0062cf98` for the length of the walk, so a stop only marks a node and `FUN_004cfcf0` unlinks it
after the pass.

So a definition called mid-tick takes its first step in the tick of the call, at the end of that
tick's pass, never inside the caller's own dispatch. A callee whose first event is
`INVALIDATE_ANIMATION` therefore cannot latch a name the caller has not reached yet: the caller's
remaining events all run first. The callee's own first callback (`FUN_004ecd20`) does charge the
instance clock at `+0xb0` the frame delta before stepping, while the sequence timers are not
charged the delta of the pass they were started in (see the tick walk below).

CSVM matches the ordering: `AnimRuntime.Start` queues an instance started during the instance walk
and `DrainQueuedStarts` gives it its first advance when the walk ends. That advance stays at zero
dt, which keeps the instance clock and the sequence timers in step the way the runner's own
first-pass rule needs. A start from outside the walk (the bootstrap, a mission or range trigger, a
death burst) keeps its immediate zero-dt burst, since those bursts read state that only exists
inside the bracket that opened them.

## The tick walk is ascending, and that is what decides same-tick dispatch

A running definition advances its sequences in **one forward pass over the array**, not over a list
of live runners. The walk reads the base from `anim+0xcc` and the count from the byte at `anim+0xd8`,
and steps by the `0x40` record stride (`004ecedc`–`004ecf53`, increment at `004ecf4d`). Each
iteration re-reads that slot's state byte at `seq+0x20` and calls the stepper only when the state is
**0 or 1**; parked (3) and done (2) are skipped. The global tick delta `DAT_009fd1a8` is reloaded
from `DAT_009ad744` at the top of **every** iteration, so each slot the pass reaches is advanced by
the full frame delta.

Two properties follow from the pass being a forward index walk, and both matter more than they look.

**A called sequence runs in the same tick if and only if its index is higher than the caller's.**
`CALL_SEQUENCE` writes state 0 into the callee's own slot (`004eb5fa`–`004eb605`, addressing it as
`base + index*0x40`), and the walk has either passed that index already or has not. A call
*forward* in the array lands on a slot the cursor has yet to reach, so the callee's first event
fires in the same tick as the call. A call *backward*, or a sequence calling itself, lands behind
the cursor and waits for the next tick. The index is the sequence's position in the definition's own
array, which is authored declaration order, so the authoring decides the timing.

The data authors overwhelmingly call forward. Of the 22,173 compiled CALL edges the walk sees,
**22,057 (99.48 %) target a higher index** and so dispatch in the same tick; 116 target a lower one
and defer. 398 of the 403 definitions carrying a call have at least one forward edge, and exactly
one is backward-only (census: the retired
`git show analysis-archive:analysis/bl-135-callsequence-lag/call-index-order.ps1`). Among the
backward minority are `police_car`'s `start_walkin` → `siren_police`, which the original really does
start a tick late, and the return hop of C2/M02's `marypickford` ring, which is what stops that ring
resolving inside one tick.

⚠ **A sequence's own timers do not advance on its first pass.** The stepper adds the tick delta to
`seq+0x24`/`seq+0x28` only when the state is 1 or the cursor has moved past the sequence start, so a
sequence the walk reaches in the tick it was called is stepped without being charged that tick. It
matters to any re-implementation that restates an ANIMATION-origin gate in the sequence's own clock:
charge the called sequence a delta the instance clock already spent and the two clocks drift apart
by one tick, which gates every later ANIMATION offset in it a tick early.

**No sequence is stepped twice in one pass, so the same-tick chain needs no cap.** A chain of
forward calls is bounded by the array count, and a ring cannot spin at all: whichever way round it
goes, one of its hops necessarily targets an index the cursor has passed. The engine gets this for
free from the walk's shape rather than from a guard.

### The index is the authored ordinal, and two named sequences are outside the array

The array is built by **appending one slot per `SEQUENCE_DEFINITION`, in the order the loader meets
them**. `FUN_0051c350` is the whole allocator: it `realloc`s to `(count+1) * 0x40`, hands back the
record at the old count, increments the count byte and zeroes the 0x40 bytes. It refuses at 255
with `Sequence list overflow`, so a definition can hold at most 255 sequences. Its one caller is the
definition loader `FUN_0051dcf0`, which calls it from a linear scan over the definition body, so an
index is exactly the keyword's ordinal in the source. Nothing sorts or reorders.

**`RESET_STATE` and `DAMAGE_SEQUENCE` never enter the array.** After that scan the loader
`calloc`s a standalone 0x40 record for each and hangs it off its own pointer: the reset body at
`anim+0xd0`, named `RESET_SEQUENCE`, and the damage body at `anim+0xd4`, named `DAMAGE_SEQUENCE`.
Both are filled by the same event-list reader the array slots use, and both are stepped by direct
single-sequence calls outside the tick walk (`FUN_004ed340` steps `+0xd0` on start;
`FUN_004e71e0` zeroes the instance clock and steps `+0xd4`). Neither occupies an index, neither is
reachable by `CALL_SEQUENCE`, and neither is advanced by the walk. mech3ax's `unknown_seq` is the
`+0xd4` damage record.

⚠ The name resolution is **memoized into the event**, not recomputed. `CALL_SEQUENCE` scans the
array comparing the record's name at `seq+0` and stores the resulting index in the event payload at
`+0x2c` (`004eb5db`), reusing it on every later dispatch of that same event. `STOP_SEQUENCE`
(`004eb610`) resolves identically. The memo is per authored event, so it is stable for the life of
the mission.

## WAIT_FOR_COMPLETION gates the NEXT event, not the runner's lifetime

A `CALL_ANIMATION` carrying the flag holds its sequence while the callee it actually reached is
still running. The hold is a **predicate**, not a duration, the callee's own length is not knowable
at the call (its sequences can call further sequences, and an SI script's run time lives in a
separate archive), and it must test the **exact instances this call reached**, since a pooled
template copy is chosen at dispatch and asking again by name would take another pool slot.

**The scope rule is the data's.** Censused over both front-ends: of the **2,999 flagged calls the
runtime can reach, 2,770 are the LAST event of their block, and 2,770 of those name a callee that
never terminates**, the `sputter_fire` / `sputter_black_smoke` / `sputter_fire_smoke` /
`gen_drop_ladder` `LOOP{-1}` idiom. Not one flagged call with an event behind it names a
never-terminating callee, in either front-end. So a runner-lifetime reading wedges 2,770 authored
sequences open for the session, while a next-event reading is consistent with the whole install
with zero exceptions.

- A callee whose whole choreography fires at t=0 never becomes a live instance, so there is nothing
  to hold on and the caller advances in the same pass. 16 of the census's effective holds are that
  shape.
- **The wait REPLACES the call's duration**: a trailing `Event + t` offset is measured from the
  callee's END, not from when the call fired. Measured from the call, the offset is already behind
  the clock at release and collapses to zero, losing the authored pause.
- The hold is real elapsed time, so an enclosing LOOP must **not** read that iteration as
  instantaneous and charge it the animation-frame floor.
- A hold inside a poll loop stops the whole loop, not merely the next event, otherwise five
  seconds of the poll idiom re-fire its body ~300 times behind the hold.

The authored case is `player_crash_water`/`destroy_crash`: a flagged call of `plane_big_splash`,
then `large_steam_spray`. Without the hold both retarget on the same tick, though the splash's own
choreography runs 3.0 s.

⚠ `0` and `null` are different authored states for this flag (**3,639** vs **53,019** events), and
only a set flag holds.

## Timed events, and what a handler reports back

The stepper needs each dispatched event's run time to place the next one. Two handlers traced
establish the pattern:

- `LIGHT_ANIMATION` (`004e82b0`, dispatch slot 5) advances the light by one tick's worth of the
  authored delta per dispatch, clamps the last tick to the remainder, and returns **still running**
  until the run time is up.
- `FBFX_COLOR_FROM_TO` (`FUN_004ec6a0`, dispatch slot 36) interpolates RGBA linearly from `from`
  toward `to` over the run time, clamps each channel to 0..1, packs RGB into one frame-buffer pixel
  value and hands it plus the alpha, **separately, as a blend weight, not a premultiplied
  component**, to the single global frame-buffer-effect object. It returns still running on the
  same test. Full decode below.

The consequence for the sequence is the same in both cases: **the ramp is the event's duration and
the chain waits for it.** Reporting 0 collapses `he_light_seq`'s authored 0.41 s flicker, seven
ramps, into a single frame, each tween overwriting the previous, and `he_ground_effect`'s six-step
1.2 s white↔violet wash into one instant.

## A definition ends only when no sequence of it is still stepping

The per-frame record update walks the definition's own sequence array (`anim+0xcc`, stride 0x40,
count `anim+0xd8`) twice. The first walk (`004ecedc`-`004ecf53`) steps every entry whose state byte
is 0 or 1 through `FUN_004ecbb0`, and re-arms to 0 an entry the `LOOP` return left at 4. The second
(`004ecf5d`-`004ecf8a`) asks the same array whether any entry came out of that still in state 0 or
1; only when none did does it call the animation stop `FUN_004ebbb0` (`004ecf95`), which carries the
record into its terminal state (2 → 3, 6 → 4). Neither walk reads the record's own state byte at
`+0xa0`, so an invalidated definition keeps stepping exactly like a plain running one, which is the
other half of the `INVALIDATE_ANIMATION` latch in
[`../formats/anim-definitions.md`](../formats/anim-definitions.md).

**A body still in the air therefore holds its whole definition open.** `OBJECT_MOTION`
(`FUN_004e8fa0`, slot 10) returns 1 on every frame of a launch it has not terminated, so the entry
that launched it stays in state 1 for the whole flight, not for the launch's reported duration. The
bounce dispatch depends on that: on contact the handler picks the branch index from the struck
surface (`004e9997`-`004e99c4`), resolves the branch name against this same array
(`004e99ed`-`004e9a7b`), and writes the branch entry's state byte 3 → 0 (`004e9aa8`-`004e9acf`),
which is a `CALL_SEQUENCE` of an `ON_CALL` sequence of the definition the flying body belongs to. A
piece that reaches the sea long after the rest of its wreck has settled still finds that array,
because its own parked entry is what kept the record from being stopped.

⚠ CSVM's runner does not park on a launch; it schedules the next event off the launch's reported
duration and ends when the events run out, so the equivalent hold lives on the instance instead:
`MotionSet.Airborne` keeps an instance out of `AnimRuntime.Retirable` while any of its bodies is
ballistic. Holding only on a body with an ARMED bounce branch is not enough. A branch is armed at
the landing, so a body still in the air holds nothing, and C2B/M04's Gemini loses the `hit_waterN`
splash of the two sections that reach the sea after `killgmzep`'s other sequences have finished.

## FBFX_COLOR_FROM_TO is a full-screen wash

`FUN_004ec6a0`, dispatch slot 36, read in full (decompiled and disassembled). **152 events ship**,
in four definitions and nowhere else: `he_ground_effect`, `ap_ground_effect` and `flak_effect`
(6 events each × 8 chapters = 144) plus the intro cutscene's `gi_scene1` (1 × 8). Each of the three
ordnance definitions carries the same shape, a sibling `Initial` sequence doing
`If PlayerRange … → CallSequence frame_buffer_effects1 → Endif`, and an `OnCall`
`frame_buffer_effects1` holding the chain.

**The event struct**, from the handler's own offsets (`ECX` is the event, `EDX` the live sequence):

| Offset | Field |
|---|---|
| `+0x0c` / `+0x10` / `+0x14` | red `from` / `to` / **delta** |
| `+0x18` / `+0x1c` / `+0x20` | green `from` / `to` / delta |
| `+0x24` / `+0x28` / `+0x2c` | blue `from` / `to` / delta |
| `+0x30` / `+0x34` / `+0x38` | alpha `from` / `to` / delta |
| `+0x3c` | `run_time` |

**What it does, per tick.** With `t` = the sequence's **event timer** (`seq+0x28`) clamped to
`run_time`, each channel is `from + t * delta`, the delta is the compiled
`(to - from) / run_time`, so the interpolation is **linear in RGBA**. Once the event timer reaches
`run_time` the value snaps to `to` outright and the handler returns **2** (complete); before that it
returns **1** (still running, re-dispatch me next tick). Each channel is then clamped to `0…1`; RGB
is scaled by 255 and packed into one frame-buffer pixel through the same mask/shift globals
(`DAT_009c67fc`/`6800`/`6804`/`680c`) the weather particles' `COLOR` uses, and the alpha is passed
**separately, as a scalar**, not premultiplied into the pixel. Colour plus a scalar weight is an
alpha blend over the picture (`dst = dst·(1-a) + colour·a`); it cannot be a multiply or a screen,
and the data agrees, `he_ground_effect`'s first step is white at α 0.3, which a multiply would
render invisible. *(The blend state itself sits behind a virtual on the renderable and was not
traced; the colour+weight pair and the white-step argument are the evidence.)*

**One global state, last writer wins.** The pair goes to a single process-wide object
(`FUN_005ca1f0` → `FUN_005ca0e0`, `this` hard-coded to `0x9c8a98`): packed colour at `+0x60`,
alpha at `+0x68`, then a virtual call that arms the effect **for the current frame only**
(`FUN_005c54a0`, sets the live bit and clears the persistent one). Nothing else in the exe writes
those fields. So two bursts overlapping do not composite: the second simply overwrites the first,
and when the last event completes nothing re-arms the object and the wash is gone on the next frame
rather than holding its `to` colour.

**`alpha_delta` in the extraction is a round-trip artefact, not a parameter.** mech3ax recomputes
every delta as `(to - from) / run_time` and emits `alpha_delta` only when the file's stored value
disagrees bit-for-bit. All 8 non-null values in this install are `flak_effect`'s `-0.99999994`
against a computed `-1.0`, one ulp, about 2e-8 of alpha across the whole 0.3 s ramp, orders below
one 8-bit level. CSVM does not read it.

**How CSVM plays it.** `AnimRuntime`'s `FbfxColorFromTo` case pushes `(from, to, run_time)` to a
session-level sink and reports `run_time` as the event's **duration**, which is the CSVM equivalent
of the original's "return 1 until done": the sequence runner gates the next event on it, so
`he_ground_effect`'s six steps space out over their authored 1.2 s instead of collapsing into one
instant. The sink is `UI.Boards.ScreenFlash` (`docs/architecture.md`), one ramp at a time, replaced
outright by a later event, painted into every rendered view. Asserted by the `fbfx-flash`
`--run-tests` suite.

## The last four unhandled kinds, decoded and triaged, none built

`Callback` (slot 35, `004ec5e0`), `ObjectCycleTexture` (slot 17, `004eabd0`), `ObjectDeleteChild`
(slot 16, `004eab90`) and `CameraState` (slot 20, `004e85c0`) are the whole of what the census in
the retired `analysis/anim-interpreter-decode/FINDINGS.md` (`git show analysis-archive:` that
path) counts as shipped-but-unhandled. All four were
missing from Ghidra's function list (reached only through the dispatch table, like `LOOP`) and were
recovered by forcing a function at each dispatch address, then decompiled in full. None gets a
handler: for each, either CSVM has no consumer of what the exe does, or the def(s) that carry it are
never reached by anything CSVM plays. A def-census over all 3,015 compiled defs (ad hoc, same method
as `anim_census.py`) located every occurrence of all four kinds to check reachability, not just count
them.

- **`Callback`** (288 events, 120 defs, e.g. `player-player.json`'s `destroy_craft` sequence,
  `value: 15`/`16`). The handler calls the anim instance's own registered native function pointer
  (`anim+0x74`) with a per-event code (`anim+0x78`, the event's own `+0xc`) if one is registered,
  pure `has_callbacks`-gated mission-scripting plumbing, notifying a host that installed a callback.
  The world build installs the mission-script host `FUN_0047e080` on every definition it walks, and
  what each code does there is
  [`formats/anim-definitions/cutscenes.md`](../formats/anim-definitions/cutscenes.md)'s code
  reference. CSVM answers the same codes through `AnimRuntime.CallbackHost`'s chain and its own two
  vehicle-death seams.
- **`ObjectCycleTexture`** (96 events, 96 defs, exactly the player's own
  `<part>_damage_{green,yellow,red}` cockpit indicator lights for `leftwing`/`rightwing`/`nose`/
  `tail`, one set per chapter, nothing else). The handler resets an object's per-mesh texture-cycle
  list to frame 0 (`FUN_005642a0`) then jumps straight to a specific frame (`FUN_00564410`, index
  from the event's `+0x12`), i.e. "snap this object's cycling texture to state N", not "start a
  cycle". `docs/architecture.md`'s `Flight/Airframe/DamageVisuals.cs` entry already records these same defs as
  deliberately unwired: the live screen-space `GaugeCluster.OnPartDamage` covers the same
  information, while driving the Cockpit view's authored in-3D indicators belongs to the gauge
  work tracked separately. The decode confirms it is the same mechanism, not a second consumer.
  ⚠ **Wiring it would be a provable no-op either way.** Probed at the dispatch site across C1, C3,
  C4 and C5, every shipped dispatch names the node `taildamage` and resolves **zero** targets: the
  node the event addresses is not built in any world these missions stand up, so there is nothing
  for a handler to cycle. The one real use of the mechanism, the cockpit damage-indicator hilite, is
  a build-time material swap in `GaugeCluster.cs`.
- **`ObjectDeleteChild`** (48 events, 40 defs). The handler unconditionally detaches a named child
  from a named parent (`FUN_004cd6d0`, dispatched by the child's own node type), a pure scene-graph
  reparent, no visibility or transform change of its own. Every shipped use is one of two shapes:
  - `camera1-generic_intro.json`'s `check_warhawk`/`start_script` sequences detach `camera1` and
    `player` from `world1`, cutscene camera rigging, `OnCall` and never reached by anything CSVM
    plays (`camera1`/`player`/`cpilot` are cutscene machinery for cutscenes this project does not
    have), and `apassengers-rem_pas.json`'s
    `remove_passenger` detaches `apassengers` from `pass_st`, `pass_st` is not a gamez node in any
    chapter (confirmed earlier, the archived development log), so the parent can never resolve even if a handler
    were written.
  - `player-cpeject1/2/cpejectstop.json` detach `cpilot` from `pilot_pos`. These ARE reached, they
    are called from the player's own `destroy_it` crash sequence,
    but in all three files the delete is the first of exactly two events, and the second is
    `ObjectActiveState(cpilot, false)`: `cpilot` is hidden immediately after, whether or not it was
    ever detached. Reached, and still a no-op to build: CSVM already renders the correct (invisible)
    outcome without it.
- **`CameraState`** (8 events, 8 defs, one `player-gi_1stperson.json` each). The handler writes a
  camera node's clip near/far, LOD multiplier, FOV and zoom fields, each gated by its own bit in the
  event's flags byte. `gi_1stperson` is `OnCall` and its only caller anywhere in the install is
  `camera1-generic_intro.json`'s `check_warhawk` sequence, the same unreached intro-cutscene
  machinery as `ObjectDeleteChild` above. CSVM has no scripted first-person camera to configure
  either: the Cockpit/Nose view modes exist and `PlayerFirstPerson` follows them, but no camera node
  is driven from animation data (see the format doc's own condition table).

`AnimRuntime`'s `default:` case keeps counting all four by name (`Count(ev.Kind)`) exactly as
before, this record is what makes that report legible, not a code change.

## What this record could not confirm

`FUN_004ecbb0` and `FUN_004ebfa0` were read in full and match the state machine and struct offsets
above exactly, including the three `START_TIME` origin codes (1/2/3 → `anim+0xb0` / `seq+0x24` /
`seq+0x28`) and the reset writing `state ← +0x21`, `event ptr ← +0x38`, both timers to 0. `LOOP`
(`004ebfd0`) has no Ghidra-recognised function boundary, it is reached only through the dispatch
table, but was disassembled directly: it folds the sequence timer into `+0x2c`, does
`INC word ptr [+0x30]`, terminates on `counter == authored` (`-1` special-cased infinite) or, in its
second form (`flags & 2`, `LOOP_RUN_TIME`), on the accumulated time reaching the authored float,
then calls the reset routine and returns 4. **The exe has that second form; nothing shipped builds
it**, of 3,015 compiled defs' 1,018 `Loop` events, all 1,018 carry `Count` and none carries
`RunTime`. Disproven, not merely unimplemented: see the census in the retired
`analysis/anim-interpreter-decode/FINDINGS.md` (`git show analysis-archive:` that path).

The struct offsets above are otherwise mech3ax's `SeqDefInfoC` layout, taken as given rather than
independently re-derived field-by-field; only the offsets the stepper and `LOOP` actually touch have
been seen in code.

`WAIT_FOR_COMPLETION`'s hold is confirmed only at the general handler-return-value contract, not by
reading a dedicated address, which is a weaker kind of confirmation than the rest of this page. The
animation-frame tick keeps a hypothesis the decode cannot rule out (`min(render rate, 60)`) alongside
its confirmation.

Two residuals around `CALL_ANIMATION`. The meanings of the remaining call-event flag bits
(`+0x2e` bits `0x2`/`0x4`, `+0x2f` bits `0x1`/`0x2`/`0x8`, the offset variants and any
caller-inherited nodes) are not pinned down; the anchor reading at `004eb53d` is directly readable
and does not depend on them. And the handler block itself has no Ghidra function boundary, so it was
read as disassembly rather than decompiled.

## Sequences of one definition run CONCURRENTLY

A definition instance is an anchor plus one runner per active sequence. The C1 train drives its
four cars from four sibling sequences, each with its own SI script and its own loop, all against
the one shared instance clock. Each runner retires the moment its own cursor runs off the end.

The train is also what pins the absent-start reading: its sequences are
`[ObjectMotionSiScript, LOOP{-1}]` with no start offsets anywhere, and only "as soon as the
previous event COMPLETES" turns that into the surveyed **~327 s track loop** instead of a
zero-length infinite loop.

⚠ **"No runner is still executing" is not on its own the test for retiring an instance.** A body
still in the air holds its instance open: such a launch is the last event of its sequence, its
runner ends on the launch's reported duration rather than on the landing, and the landing has still
to dispatch into a sequence. The original's own version of that hold is above, under "A definition
ends only when no sequence of it is still stepping".

## Where CSVM deliberately differs

Everything here is a known, deliberate divergence, not a gap waiting to be closed.

| Divergence | Why |
|---|---|
| **The `STOP_SEQUENCE` DISABLE is not persisted** | A halt is not remembered, so a later `CALL_SEQUENCE` on the same name starts the sequence again, where the original's DONE state refuses it until the definition resets. **123 definitions name one sequence in both a call and a stop** (mostly `flame_light_seq`), but whether any reaches the stop BEFORE the call at run time is a control-flow question a static census cannot answer, persisting the flag would move all 123 on a divergence none of them is known to observe |
| **The branch-taken stack** stands in for the original's arrival distinction | See the residual above: it needs a nesting shape no shipped definition has |
| **A 256-dispatch-per-tick guard** | Bounds a zero-length sequence within one tick; the remainder defers to the next. The original has no such cap |
| **A reader `DAMAGE_SEQUENCE` occupies a slot**, where the original keeps it out of the array | `AnimDefs.cs` appends it to `Def.Sequences` so `ApplyDamageStages` can find it by name, which gives it an index the original's standalone `anim+0xd4` record never has. Nothing calls it, and an insertion cannot invert the order of the sequences around it, so the same-tick rule is unaffected |
| **A start from outside the tick's walk fires its t=0 events at once**, where the original waits for the next dispatcher pass | The bootstrap, a mission or range trigger and a death burst read state that exists only inside the bracket that opened them, and a settled pose is what the build hands on. A call the walk itself dispatched does take the original's order: it is queued and stepped when the walk ends |
| **The u16 pass-counter wrap** at 65,536, not reproduced | Unreachable within a session, and `-1` already spells "infinite" |
| **The 86,400 s instance-clock clamp** (`FUN_004ebfd0`), not reproduced | A session never reaches a day |
| **`LIGHT_ANIMATION` ramps asynchronously** rather than one delta-tick per dispatch | Same picture only because the sequence is also held for the run time; the tick-by-tick advance is not reproduced |
| **The animation frame is its own constant**, not the simulation step | They are equal today, but they are independent facts, see the trap above |
