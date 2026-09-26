---
name: backlog
description: Explain a backlog item in plain language — its goal, the problem it solves, its traps, how it gets done, and what it's related to; then decode it out of `crimson.exe`, and make the fix in the same session when it is localized. Use when the user names a BL-NNN item or a GitHub issue (#N, "issue N"), asks what a backlog entry or issue means, or asks to fix one.
---

Explain one backlog item so it can be understood cold, months later, without re-reading 1800 lines
of prose or chasing its references by hand. An item is either a `BL-NNN` entry in `backlog.md`
(filed before the tracker moved) or a GitHub issue on `Laeresh/CSVM` carrying the `backlog` label;
`docs/agents/issue-tracker.md` is the contract for the latter and this skill follows it. Wherever
this file says "the entry", an issue's body plus its comments is the entry.

The explanation phase is **read-only**: never edit `backlog.md`, never build, never run tests. That
holds for the whole of §1–§4, and for §5's decode, which runs only after the user picks the decode
handoff and whose every write is offered and approved first. The restriction lifts wholesale the
moment the user picks *work it here* (§6), because that is the work: edits, a build and
`.\RunTests.ps1` all belong to it.

⚠ **Output rule: text written between tool calls is not shown in chat.** What this skill produces,
the explanation and the decode's findings alike, must be the **final message after the last tool
call**: do the reading of §1–§2 first, then emit §3 and §4 together as one message and stop. This
binds **every menu this skill offers**, §4's and §5's re-offer both. Never call `AskUserQuestion` (or
any other tool) to pose one; that turns the text it was meant to close into between-calls text, and
the user sees only the question. Menu options are plain text at the end of the message they close.

The sibling skill for **active-plan** items (`A1`, `B11`) is [`/plan-item`](../plan-item/SKILL.md) —
a `BL-NNN` already scheduled into the active plan is better explained there, since that skill reads
the plan's ground rules and dependency notes too and can start, close, or defer the item. The
binary-decode **rule** below (§3's provenance triage, §5's Ghidra read-only charter, its
what-a-decode-reports contract and its write-it-down contract) is mirrored in that skill; keep those
in sync. The **handoffs deliberately diverge**, and syncing them is the mistake: a plan item carries
an Approach and a Verify step to work from, a bare `BL-NNN` carries neither, so this skill sizes the
work itself (§3) before it offers to do it.

## 1. Resolve the item

The argument may be `BL-242`, `#12`, "issue 12", a bare `242`, or a phrase like "rocket pylon".

- `BL-NNN`: grep `backlog.md` for the tag. An exact ID match wins outright — explain that item.
  IDs are assigned once and never reused or renumbered, so a missing ID means the item was
  **deleted**, not moved. Say that rather than offering a near-numbered substitute.
- `#N` or "issue N": run `gh issue view N --comments` and
  `gh issue view N --json state,labels,title` via the PowerShell tool. GitHub shares one number
  space across issues and PRs, so if the number is a PR say so and stop; a PR is not an item. A
  closed issue is still explainable: its **Status** is closed, with the closing comment as evidence.
- A bare number: `backlog.md` first, the issue tracker second. If both exist, say which you took and
  name the other in one line.
- A phrase: grep `backlog.md`, and search the tracker with
  `gh issue list --state open --label backlog --search "<phrase>" --json number,title`. List the few
  candidates with their section headings or titles and **ask which one**. Never guess silently.

One item per invocation. If several IDs are given at once, explain them one after another in the
same reply.

Once the ID is resolved, set the terminal window title to it — `$Host.UI.RawUI.WindowTitle = "BL-NNN"`
or `"#N"` via the PowerShell tool — so the session is identifiable at a glance. Skip this for a
multi-ID invocation (no single ID to title the window with).

## 2. Read it, inline

Do the reading yourself — no Explore subagent. The item text and code need to stay in context for
follow-up questions and the handoff.

- Read the entry **and its enclosing section heading** — the heading carries status (e.g. "Merged
  into `m3-polishing` — pending playtest"). For an issue the status lives in its state, its
  triage label (`docs/agents/triage-labels.md`) and the comment thread, so read the whole thread,
  latest comment last; a later comment can overturn the body.
- Follow the references the entry actually names: code files and symbols (`AnimRuntime.cs:214-216`,
  `FlightController.NextArmedHardpoint`), commit messages (`git log --grep=BL-NNN`, or
  `git log --grep='#N'` for an issue), the archived
  development log (`git show docs-archive:docs/HISTORY.md`), and doc sections: a module's entry in
  `docs/architecture/<Namespace>.md` (found through the index in `docs/architecture.md`),
  `docs/verification.md`.
- Don't go hunting beyond what the entry cites.

## 3. Explain it

Plain prose, short paragraphs. Gloss every code symbol and original-game term in a clause on first
use — "`DestructibleRegistry`, the list of things a weapon can damage". No code blocks unless a
snippet is the clearest way to show a trap. Keep each section to what it needs; say "nothing stated"
rather than padding.

### The binary outranks footage and observation

`crimson.exe` is readable through the ghidra-mcp tools, so a quantity the original engine holds — a
constant, a rate, a threshold, a ramp, a curve — is **decodable**, not a TUNE and not a taste call.
Two consequences while writing §3, both of them *routing only*: this skill still calls no MCP tool of
its own, and never substitutes a guess for the decode it did not run (§5).

- An open question the binary could settle is named as decodable **where the entry raises it**, in
  the words the entry got wrong: "the entry files this as an unsettled TUNE; it is a constant in
  `crimson.exe`."
- A number the entry asserts as **settled**, whose stated provenance is footage, a screenshot A/B or
  how it felt at the controls, is flagged as decodable-and-unverified. This project has been wrong
  that way repeatedly, and such an entry reads as finished — which is exactly why nobody re-checks it.

Bound the flag to quantities the binary plausibly holds. Art direction, whether a mission *feels*
right, whether a texture looks right — the executable has nothing to say about those, and flagging
them is noise that trains the reader to skip the flag.

Use these headings, in this order:

- **Status** — one line: still live, or looks landed/superseded. Cite the evidence (section heading,
  a landing commit or an entry in the archived development log, a merged-and-pending-playtest table).
- **Goal** — what will be true when the item is done.
- **The problem** — what's wrong today, and why it matters at the controls.
- **Traps** — the ⚠ notes plus anything the code reading reveals: wrong-mechanism "fixes", unsettled
  decodes, things that look like the bug but aren't. **Plus the provenance flags** — every number the
  entry states as settled that came from footage, a screenshot A/B or feel, and sits in a slot
  `crimson.exe` could answer. Name the quantity, name its stated source, and say the binary holds the
  real one.
- **How it gets reached** — the route to the goal: files, mechanism, order of work.
- **Size** — LARGER or localized, with the reason. **Localized** means the change is confined to the
  files the entry (or a decode of it) names, introduces no new mechanism, and needs no new test
  fixture; anything else is LARGER. This verdict is what §4's *start work* routes on and what lets
  §6 lift the read-only rule, so it has to be checkable rather than a feeling.
- **How you'd know it worked** — the confirm-in-the-cockpit line, or the relevant
  `docs/verification.md` procedure.
- **Open questions** — what neither the code nor the binary can settle: a genuine taste call, a
  live-cockpit feel judgement. **Not** the decodable ones — those were named as decodable above. If
  the entry's own "unsettled TUNE" or "needs an original-game A/B" line is really a constant in
  `crimson.exe`, say that instead of repeating it.
- **Related items** — only the `BL-NNN`s, `#N`s and doc sections the entry itself cites. No
  adjacency guessing; don't invent links nobody authored.

If the entry looks stale, already landed, or self-contradictory, say so under **Status** and *offer*
to fix the entry — write nothing to `backlog.md`, and post no issue comment, without approval. For
an issue the fix is a comment (`gh issue comment N --body-file <file>`), never a silent body edit;
the thread is the record.

## 4. Offer the handoff

End the same message by asking what to do with the item — numbered plain text, one line each, no tool
call (see the output rule at the top; an `AskUserQuestion` here hides §3 entirely):

- decode it in `crimson.exe` — read the flagged quantity out of the binary (§5);
- scaffold a plan — run `/new-plan`;
- start work — see the rule below;
- stop here.

**Rank the decode option by what §3 found.** If §3 flagged anything decodable — an open question the
binary can settle, or a footage-sourced constant in a binary-decodable slot — list it **first, marked
recommended, with a one-line reason naming the specific quantity**: "the stall onset rate is a
constant in `crimson.exe`; today's value came from a video." A bare "consider decoding this" is
ignorable; naming the number you are about to build against is not. If §3 flagged nothing, the option
sits last, unranked, unremarked.

It is an offer, never a gate. Do not refuse to recommend *start work* because something is
undecoded — a skill that blocks you is a skill you stop running.

**What *start work* means, wherever it is picked** (here, or from §5's re-offer after a decode):
§3's **Size** routes it. Localized, and the work happens here, in this context, under §6. LARGER,
and it starts with `/grill-me` on the item, then works from what that settles. One rule with one
meaning; a menu line that means two different things depending on which menu it appeared in is a
line people stop trusting.

If scaffold a plan or start work is chosen **here**, ask if a worktree should be used. If *decode it*
is chosen, do not ask: the decode's write-ups land in the tree you are already in, and §6 keeps the
fix there with them, one unit of work in one commit window. A worktree on that path has to be asked
for before the decode writes anything, never after, or the write-ups and the fix they justify end up
in different trees. Take no action until the answer comes back.

## 5. If the decode is picked

**The Ghidra project is strictly read-only.** No `rename_function`, no `set_comment`, no structs, no
prototypes, no `save_program` — not once, not "just this label". The database is not in git, so a bad
write is unreviewable and unrevertable, and this project already gets bitten by concurrent sessions
sharing state. Knowledge accumulates in the repo (below), never in the database. A deliberate
annotation pass is a different job with its own plan; it is not this.

Connection facts — server, port, project name, program path — live in the MCP configuration and
**are not restated here**; that config is the single source of truth. If the MCP is unreachable, say
so plainly and fall back to §3's routing half. Never fill the gap with a footage measurement: an
unrun decode is an open question, not a licence to guess.

**Size it before starting**, on one tell — does the entry name its own addresses or symbols?

1. **It does** (`FUN_0042ee40`, `0071c2d0`, `gwNodeSetActive`) → **decode inline**, here, bounded to
   what the entry named. No prospecting outward from it.
2. **It does not** → hand to a **fresh-context subagent**. It prospects; it **reports and never
   writes**. Require every constant back **with the address it came from** and the condition it
   applies under, so the write-up carries provenance and any number can be re-checked at source.
3. **It is genuinely large** — a body of work the size of `PLAN-weather-decompile-match`, not a
   "what is this constant" question → `/new-plan`. Rare; most decodes are not plan-sized.

### What a decode reports: numbers, then formulas, then rules

The MCP is for **understanding** the original game, not for re-expressing it in another language.

- A scalar is the answer: state it, with its address.
- A closed-form relationship is the answer: state it as a formula (`degrees × 0.017453292`).
- When the value is conditional — a state machine, a piecewise ramp, a per-state table — state it as
  a **rule in prose with its constants**: "below the altitude precomputed into `0071c2d0`, state 2;
  the whiteout opacity remaps 0→1 across the top of the core." Control flow **described**, never
  transcribed. Dropping the condition is how a number ends up right in one state and wrong in the
  other, so the condition is part of the answer, not decoration.
- Decompiler output or pseudocode only when a genuine multi-step algorithm *is* the answer — an
  interpreter dispatch, a hash — and nothing shorter is faithful. That is a fallback of last resort,
  not a default.

### Writing it down — offered, never automatic

This skill's read-only charter holds: **ask before writing anything.** When the answer is wanted on
disk, it goes to whichever fits:

- **`docs/org/<topic>.md`** when the decode falls inside an existing decode page's topic (weather,
  clutter, flightModel, tracers, puffer, sequences, aim-assist). Match those pages' contract —
  behaviour and constants, every claim naming the function it came from, no decompiler output
  reproduced.
- **`analysis/<slug>/FINDINGS.md`** when it does not — dated, with the method stated.

**In either case, also offer the one-line amendment to the entry**: the number, its address, and
the pointer to the write-up. For a `backlog.md` entry that is an edit to its text, with no date,
since `backlog.md` is live prose and the date of the decode belongs in the commit message that
lands it. For an issue it is a comment (`gh issue comment N --body-file <file>`), posted after the
write-up's commit so the comment can cite the commit hash. Without that line the next `/backlog`
on this item re-reads the stale footage number, re-flags it under **Traps**, and re-recommends the
decode that was already done.

### Then re-offer, ranked by what the decode found

A decode is not a stopping point. It usually settles the number the work was waiting on, and
sometimes it settles the item outright, so close §5 with a menu of its own rather than ending here
and making the user re-invoke the skill for one. Plain text at the end of the message, no tool call:
the output rule at the top binds this menu too. It is not a reprint of §4's, whose *decode it* line
is now spent.

- **work it here** — Size: localized (§6);
- **`/grill-me` first** — Size: LARGER, then work from what that settles;
- **`/new-plan`** — the decode opened a body of work rather than a fix;
- **close it** — the decode settled the item and there is nothing to build: `/close-backlog-item
  <BL-NNN>` for a `backlog.md` entry, or for an issue the close-out in
  `docs/agents/issue-tracker.md` (`gh issue close N --comment "..."` citing the write-up's commit);
- **stop here.**

**Mark exactly one line recommended**, picked from the decode's outcome against §3's **Size**, with a
one-line reason that names the decoded quantity: "the stall onset rate is 0.35/s at `0042ee40` and
the entry's 0.5 came from a video; the change is one constant in `FlightController`." Never two
marks, and never a "default" on one line and a "recommended" on another: that is a menu people stop
reading.

The ranking is an ordering, never a gate. Nothing here refuses a line the user asks for, *stop here*
after a decode that looks finishable included.

## 6. If *work it here* is picked

Read-only lifts (see the top); this is the work. It happens in the current tree, alongside the
decode's write-ups, in one commit window.

- Read each touched module's entry in `docs/architecture/<Namespace>.md`, found through the index in
  `docs/architecture.md`, before modifying that module, then the comments on the members you touch.
  Dead ends are recorded in the landing commits (`git log --grep=BL-NNN`, or `git log --grep='#N'`),
  so search those before re-chasing one.
- **Take every value from the extracted JSON or from the decode, never from a guess.** A guessed
  number is the failure §3's provenance flags exist to catch, and it is no better for having arrived
  during the fix instead of during the write-up.
- Land it complete: the code change, whatever `docs/architecture` or `docs/formats` entry the change
  requires, and `.\RunTests.ps1` green. That runner is the landing gate for anything under `CSVM/`.
- The verification record goes in the commit message, not into `backlog.md` and not into the docs.

When it is done, say in one line what `.\RunTests.ps1` reported, a failure included. Then:

- **offer the commit, do not make it.** One line, then stop. Commits happen when the user asks.
  For an issue, the commit message body must name `#N` so `git log --grep='#N'` finds it.
- **if the fix resolves a `backlog.md` entry, offer `/close-backlog-item <BL-NNN>`, unmodified.**
  That skill owns the closure kind, deleting the entry, the closure record in the closing commit's
  message, retiring any `CAP-nn`/`PT-nn`, and the restated-caveat sweep. Do not pre-empt its
  closure record.
- **if the fix resolves an issue, offer the close from `docs/agents/issue-tracker.md`**:
  `gh issue close N --comment "..."` naming the landing commit, run after that commit exists.
  Follow-up work left over becomes its own issue with a `⚠ Traps` section, never a comment on the
  closed one.

If the work turns out LARGER than §3 sized it (a new mechanism appears, a fixture is needed, the
change spreads past the files the entry names), say so and stop rather than pushing through. That
Size verdict is the reason read-only lifted, and it was wrong.
