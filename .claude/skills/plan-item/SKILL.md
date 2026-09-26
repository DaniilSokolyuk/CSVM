---
name: plan-item
description: Explain the active plan's item in plain language — where it sits, its goal, evidence, traps, how it gets done, and how you'd know it worked; then start it, decode it out of the original binary, close it, or defer it. Use when the user names a plan item (`A1`, `B11`) or asks what to work on next.
---

Explain one item of the **active plan** so it can be worked cold, then start it, close it, or park it.

The sibling skill for `backlog.md` entries and `backlog` GitHub issues is
[`/backlog`](../backlog/SKILL.md) — a `BL-NNN` or `#N` that is *not* scheduled into the active
plan belongs there, not here. Wherever this file says "the backlog entry", a plan item that cites
an issue (`#12`) instead has the issue's body plus comments as its entry, read with
`gh issue view N --comments` per `docs/agents/issue-tracker.md`. The binary-decode **rule** below (§4's
provenance triage, and §5 option 2's Ghidra read-only charter, its what-a-decode-reports contract and
its write-it-down contract) is mirrored from that skill; keep those in sync. The **handoffs
deliberately diverge**, and syncing them is the mistake: an item here carries an Approach and a Verify
step to work from, so §5 works it directly, while a bare `BL-NNN` carries neither and that skill sizes
the work itself before it offers to do it.

The explanation phase is **read-only**: no builds, no `RunTests.ps1`, no `--freecam` runs. Once the
user picks *start it here*, that restriction lifts — that is the work.

⚠ **Output rule: text written between tool calls is not shown in chat.** The explanation is this
skill's entire deliverable, so it must be the **final message after the last tool call** — do all the
reading of §1–§3 first, then emit §4 and §5 together as one message and stop. Do **not** call
`AskUserQuestion` (or any other tool) to pose §5's choice: that turns the explanation into
between-calls text and the user sees only the question. §5's options are plain text at the end
of that message.

## 1. Resolve the plan

The **active plan only**, resolved from `PROJECT_CONTEXT.md`'s "Current status / next step" section.
One authority; do not glob for candidates.

- **No active plan named** → list any `docs/PLAN-*.md` files and ask which, or if there are none say
  so and point at `/new-plan`. Never guess.
- **An explicitly named plan** (`/plan-item A1 in PLAN-m3-polish-5`, or a path) → honour it, but if
  it no longer exists under `docs/` it is completed: read it from git (the retrieval commands are in
  PROJECT_CONTEXT.md's repo layout), explain it **read-only** and skip §5's handoff entirely:
  retroactively starting or deferring a finished item is nonsense. Point at its landing commit
  (`git log --grep`) instead.

## 2. Resolve the item

- An **item ID** (`A1`, `a1`, `B11`, `D31`) — exact match wins, case-insensitive.
- A **`BL-NNN` or `#N` the plan carries** (`BL-051` → A1, `#12` → B3) — the vocabularies are
  interchangeable here.
- A **phrase** → list the candidate items with their wave headings and **ask**. Never guess silently.
- **No argument** → the next item, by `/commit-next` step 2's rule: the first **◐ in progress**, else
  the first **☐ open** in wave/number order, **respecting the plan's "Dependency and parallelism
  notes"**. If the first open item is blocked by an unfinished dependency, take the first unblocked
  one and say in one line which item you skipped and why.
- An **ID the plan does not have** is a typo or the wrong plan — not a deleted item. List the IDs the
  plan actually has rather than offering a near-numbered substitute.

One item per invocation.

## 3. Read it, inline — three tiers

Do the reading yourself; no Explore subagent. The item text and code need to stay in context, because
*start it here* is the default handoff and works from exactly this context.

1. **The plan, always** — the item's full `### <ID>` section, plus the plan's `## Ground rules`, its
   `## ⚠ Read this before implementing anything` section if it has one, `## Decisions`, the item's
   `## Checklist` line, and `## Dependency and parallelism notes`.
2. **The backlog entry, always** — the `BL-NNN` the item cites, and its enclosing section heading
   (the heading carries status); or the `#N` it cites, whole thread, latest comment last, since a
   later comment can overturn the body, plus its state and triage label. Plan items summarise and
   delegate — "Full evidence: `backlog.md` `BL-051`" or "Full evidence: #12" — so the counts, the
   rejected fixes, and the real traps live there.
3. **Only what the item names** — the code files and symbols (`FromToMotion.cs:109-111`,
   `WorldBuilder.NoCollisionNode`), the doc sections (a module's entry in
   `docs/architecture/<Namespace>.md`, found through the index in `docs/architecture.md`;
   `docs/verification.md` rule IDs), and any `analysis/*/FINDINGS.md` it cites. Read them to confirm
   the claim still holds.
   Don't hunt beyond the citations. ⚠ Line numbers in plan and backlog text predate later refactors —
   re-locate symbols **by name**.

While there, check whether the item **already landed** without its checklist being flipped — a
landing commit (`git log --grep=<ID>`), an entry in the archived development log, or code that
already does the thing.

**Confirmation is by reading, not by running.** When a claim can only be settled by executing
something (a collider count printed at build, a per-chapter node census), say so under *The evidence,
and how far it goes* — "unverified here; needs a build" — and leave it to the work. Reporting a number
as re-confirmed when you only re-read the code that produces it is the masked-effect failure the
plan's Verify sections keep warning about.

## 4. Explain it

Plain prose, short paragraphs. Gloss every code symbol and original-game term in a clause on first
use — "`DestructibleRegistry`, the list of things a weapon can damage". No code blocks unless a
snippet is the clearest way to show a trap. Keep each section to what it needs; say "nothing stated"
rather than padding.

### The binary outranks footage and observation

`crimson.exe` is readable through the ghidra-mcp tools, so a quantity the original engine holds — a
constant, a rate, a threshold, a ramp, a curve — is **decodable**, not a TUNE and not a taste call.
This matters more here than anywhere else in the repo: `/plan-item` is the last gate before
read-only lifts and a number gets compiled into the engine. Two consequences while writing §4, both
of them *routing only* — this skill calls no MCP tool of its own (see §5, option 2):

- An open question the binary could settle is named as decodable **where the item raises it**, in the
  words the item got wrong: "the plan files this as an unsettled TUNE; it is a constant in
  `crimson.exe`."
- A number the plan or its backlog entry asserts as **settled**, whose stated provenance is footage, a
  screenshot A/B or how it felt at the controls, is flagged as decodable-and-unverified. This project
  has been wrong that way repeatedly, and such an item reads as evidenced — which is exactly why
  nobody re-checks it before building on it.

Bound the flag to quantities the binary plausibly holds. Art direction, whether a mission *feels*
right, whether a texture looks right — the executable has nothing to say about those, and flagging
them is noise that trains the reader to skip the flag.

Use these headings, in this order:

- **Where it sits** — plan, wave, checklist status; from the dependency notes: what must land first,
  what this blocks, what it must not share a commit window with. End with the tier line —
  *item wants `high` (this session: `medium`)* — from the item's `**Model recommendation.**` line
  against the tier you are running. **On a mismatch, say plainly that the item wants a different tier
  before the user picks *start it here*.** Older plans may carry no recommendation; then skip it.
- **Status** — one line: still open, or looks already landed / superseded / moot. Cite the evidence
  (a landing commit, an entry in the archived development log, the code state).
- **Goal** — what will be true when the item is done.
- **The problem** — what's wrong today, and why it matters at the controls.
- **The evidence, and how far it goes** — the item's own confidence label (traced / direction-sound-
  magnitude-TUNE / lead-only) said in plain words, plus whether the tier-3 reading confirmed it still
  holds, and what remains unverified without a run. An item flagged **lead-only** is a question, not
  a finding; treating it as a finding is this repo's most-repeated failure. **Say where each number
  came from** — a decode, a measurement off footage, a cockpit observation — because that provenance
  is what the triage above ranks.
- **Traps** — the ⚠ notes from both the plan item and the backlog entry, plus anything the code
  reading revealed: wrong-mechanism "fixes", unsettled decodes, things that look like the bug.
  **Plus the provenance flags** — every number the item states as settled that came from footage, a
  screenshot A/B or feel, and sits in a slot `crimson.exe` could answer. Name the quantity, name its
  stated source, and say the binary holds the real one.
- **How it gets done** — the Approach as an ordered route: survey → decide → implement, files named.
- **How you'd know it worked** — the Verify step, including which goldens are expected to move and
  which `docs/verification.md` rule bites here.
- **Open questions** — what neither the code nor the binary can settle: a genuine taste call, a
  live-cockpit feel judgement. **Not** the decodable ones — those were named as decodable above. If
  the item's own "unsettled TUNE" or "needs an original-game A/B" line is really a constant in
  `crimson.exe`, say that instead of repeating it.
- **Related** — only the `BL-NNN`s, `#N`s, sibling plan items, and doc sections the item itself
  cites. No adjacency guessing; don't invent links nobody authored.

If the item looks stale, already landed, or self-contradictory, say so under **Status** — and let §5's
*close it* handle it rather than editing anything now.

## 5. Offer the handoff

End the same message with the four options below as plain text — numbered, one line each, naming the
default. No tool call (see the output rule at the top; an `AskUserQuestion` here hides §4 entirely).
**Take no action until the user answers.**

**Which one is the default depends on what §4 flagged.** *Start it here* is the default in the
ordinary case. But when §4 flagged a decodable constant **that this item's own work would use or
replace** — not any decodable number mentioned in passing — present *decode it* **first, as the
default for this invocation**, list *start it here* without the label, and give a one-line reason
naming the specific quantity: "B7 retunes the stall onset rate; today's value came from a video and
`crimson.exe` holds the real one." Never two labels at once — a menu with a "default" and a
"recommended" pointing at different lines is a menu people stop reading. A secondary decodable number
stays a trap line in §4 and leaves the default alone.

It is a re-ordering, never a gate. Nothing here refuses *start it here*; plenty of items are worth
starting while a secondary constant stays unknown.

### Option 1 — start it here *(the default, unless §5's rule above moved it)*

Work the item in this context. The paste-and-clear variant is `/commit-next <ID>` — do not re-add it
here.

⚠ **The two paragraphs below are mirrored verbatim from `/commit-next` step 4's next-task prompt —
keep them in sync.** They are what a fresh session pasting that prompt would be told, so working from
them makes this path start identically.

~~~
Before writing code: read that item's full "### <ID>" detail in the plan and the plan's "## Ground rules" and "## ⚠ Read this before implementing anything" sections, plus each module's entry in docs/architecture/<Namespace>.md (found through the index in docs/architecture.md) for every module you'll touch. Verify data against the extracted JSON — never guess a value.

Land it complete in the same turn: follow the plan's Verify step, update docs/formats or docs/architecture as the item requires, flip the checklist item to ☑, and refresh PROJECT_CONTEXT.md "Current status". The verification record goes in the commit message. Commit only when I ask (with /commit-next).
~~~

Tiers 1–3 already satisfied most of the first paragraph — say so in one line rather than silently
skipping it. What they did **not** cover is touched-module-dependent: **read that module's entry in
`docs/architecture/<Namespace>.md` (found through the index in `docs/architecture.md`) for every
module you are about to modify**, then the comments on the members you touch; dead ends are in the
landing commits (`git log --grep=<ID>`), so search those before re-chasing one.

### Option 2 — decode it in `crimson.exe`

Read the flagged quantity out of the original binary through the ghidra-mcp tools, before the item's
work builds on a number that came from a video.

**The Ghidra project is strictly read-only.** No `rename_function`, no `set_comment`, no structs, no
prototypes, no `save_program` — not once, not "just this label". The database is not in git, so a bad
write is unreviewable and unrevertable, and this project already gets bitten by concurrent sessions
sharing state. Knowledge accumulates in the repo (below), never in the database. A deliberate
annotation pass is a different job with its own plan; it is not this.

Connection facts — server, port, project name, program path — live in the MCP configuration and
**are not restated here**; that config is the single source of truth. If the MCP is unreachable, say
so plainly and fall back to §4's routing half. Never fill the gap with a footage measurement: an
unrun decode is an open question, not a licence to guess.

**Size it before starting**, on one tell — do the item and its backlog entry name their own addresses
or symbols?

1. **They do** (`FUN_0042ee40`, `0071c2d0`, `gwNodeSetActive`) → **decode inline**, here, bounded to
   what was named. No prospecting outward from it. The item is already in context, which is the whole
   reason tier 1 of §3 refuses subagents.
2. **They do not** → hand to a **fresh-context subagent**. It prospects; it **reports and never
   writes**. Require every constant back **with the address it came from** and the condition it
   applies under, so the write-up carries provenance and any number can be re-checked at source.
3. **It is genuinely large** — a body of work the size of `PLAN-weather-decompile-match`, not a
   "what is this constant" question → say so and point at `/new-plan`. Rare; most decodes are not
   plan-sized.

**What a decode reports: numbers, then formulas, then rules.** The MCP is for *understanding* the
original game, not for re-expressing it in another language. A scalar is the answer — state it with
its address. A closed-form relationship is the answer — state it as a formula (`degrees ×
0.017453292`). When the value is conditional — a state machine, a piecewise ramp, a per-state table —
state it as a **rule in prose with its constants**: "below the altitude precomputed into `0071c2d0`,
state 2; the whiteout opacity remaps 0→1 across the top of the core." Control flow **described**,
never transcribed; dropping the condition is how a number ends up right in one state and wrong in the
other. Decompiler output or pseudocode only when a genuine multi-step algorithm *is* the answer — an
interpreter dispatch, a hash — and nothing shorter is faithful.

**Writing it down is offered, never automatic** — the explanation phase's read-only rule still holds
until *start it here* is picked. When the answer is wanted on disk: **`docs/org/<topic>.md`** if it
falls inside an existing decode page's topic (weather, clutter, flightModel, tracers, puffer,
sequences, aim-assist), matching those pages' contract — behaviour and constants, every claim naming
the function it came from, no decompiler output reproduced; otherwise **`analysis/<slug>/FINDINGS.md`**,
dated, with the method stated. **In either case also offer the one-line amendment to the entry** —
the number, its address, the pointer. For a `backlog.md` entry that is an edit with no date, since
`backlog.md` is live prose and the decode's date belongs in the commit message that lands it; for
an issue it is a comment (`gh issue comment N --body-file <file>`), posted after the write-up's
commit so it can cite the hash. Without that line the next explanation re-reads the stale footage
number, re-flags it, and re-recommends the decode that was already done.

Then re-offer the handoff. A decode usually makes *start it here* the obvious next move, and may
change what the item's work should be — say so if it does.

### Option 3 — close it

The plan bookkeeping is this skill's; the `BL-NNN` or `#N` side is not.

- Flip the checklist line: **☑** if the work landed, **❌** if the explanation disproved it or it is
  moot. Ask which if it isn't obvious — the glyph is the plan's record of *why*.
- Add the one-line verdict to the item's `### <ID>` body.
- Advance `PROJECT_CONTEXT.md` "Current status" if this was the next-item pointer — **swap or delete
  only**, never add a sentence about what closed (that section's own fixed-shape rule).
- Then hand to **`/close-backlog-item <BL-NNN or #N>`**, unmodified. It owns the closure kind,
  deleting the entry or closing the issue, the closure record in the closing commit's message,
  retiring any `CAP-nn`/`PT-nn` or `capture`/`playtest` issue, and the restated-caveat sweep. Do
  not pre-empt its closure record.
- **If that was the plan's last open item**, say so and *offer* the completion: delete the plan
  file in the closing commit (git keeps it; no archive), record the completion in that commit's
  message, clear PROJECT_CONTEXT.md's "Current status" pointer, and unlink any live prose that
  linked the file by path (grep the filename). Don't do it unasked; but a finished plan left in
  `docs/` still resolves as "active" for the next session.
- **No commit.** Offer it in one line and stop.

### Option 4 — defer it

**Write nothing.** The item stays `☐ open` in the plan, `backlog.md` and the issue are untouched,
and `PROJECT_CONTEXT.md` is untouched — "not now" is a session decision, and a deferral note that
is obsolete tomorrow is noise.

Say in one line that nothing was written, then resolve the **next** item by §2's no-argument rule,
excluding anything deferred earlier in this session. **Name that item and ask** whether to explain it.
Do not auto-explain it — two full three-tier reads chained on one keystroke is a lot of context for
nothing.
