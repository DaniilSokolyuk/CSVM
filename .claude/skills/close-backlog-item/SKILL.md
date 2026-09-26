---
name: close-backlog-item
description: Close a resolved backlog item, a backlog.md entry or a `backlog` GitHub issue (and any playtest.md capture or `capture`/`playtest` issue it owned) — retire the IDs, log the outcome in the closing commit's message, and strike the caveat everywhere it was restated. Use when a BL-NNN or an issue number is done, answered, superseded, or dropped.
---

Retire one backlog item now that it is settled: a `BL-NNN` entry in `backlog.md`, or a
`backlog`-labelled GitHub issue on `Laeresh/CSVM` (`docs/agents/issue-tracker.md` is the contract
for those). The work is bookkeeping, and the whole risk is **leaving the dead claim alive somewhere
else** — an entry's doubt is usually restated in a format doc and in a code comment, neither of
which cites the `BL-NNN` or the `#N`, so neither turns up if you only grep the ID.

This skill edits docs and comments. It does **not** implement anything, and it does **not** commit
unless asked. It closes an issue on GitHub only when asked, and only after the landing commit
exists (§2).

## 1. Resolve the item and its closure kind

Argument may be `BL-238`, `#12`, "issue 12", a bare `238`, or a phrase. Same resolution rule as
`/backlog`: an exact ID match wins; `#N` is `gh issue view N --comments` plus
`gh issue view N --json state,labels`; a bare number tries `backlog.md` first and the tracker
second; a phrase or missing ID means **ask**, never guess. A missing `BL-` ID means the item was
already deleted, and an issue already closed is already closed — say so rather than offering a
near-numbered substitute or closing it twice. A number that turns out to be a PR is not an item.

Read the entry (an issue's whole thread, latest comment last) and its enclosing section heading
before touching anything. Then name which kind of close this is, because it changes what the
closure record has to say:

- **Fixed** — code landed. The entry's *How you'd know it worked* line must have actually been run.
- **Answered** — an open question settled by a capture, a measurement, or the user's call. Nothing
  in the build changes; what changes is that a constant/decode stops being provisional.
- **Superseded** — another item swallowed it. Name the survivor.
- **Won't do** — out of scope by decision. Record the reason, not just the verdict.

⚠ **The user's decision is authoritative, but the evidence still gets written down.** If they close
an item on a partial measurement, close it — and record in the closing commit's message both what the
evidence does establish and the hypothesis it cannot exclude, plus why that residue does not matter.
An undocumented close reads months later as an unexplained disappearance.

## 2. Delete the entry from `backlog.md`, or close the issue

**A `backlog.md` entry**: delete it outright. **Do not** mark it DONE, strike it through, or move
it to a "closed" section — there is no such section, and the file is the live list. IDs are
assigned once and never reused or renumbered, so the gap is the record.

Then check what the deletion breaks:

- **Sibling entries that cite the ID.** Grep it. A citation inside a *narrative* ("split out of
  `BL-236`, now landed") is history and stays. A citation that sends a future reader to go *read*
  the deleted entry for evidence or traps must be rewritten to carry the fact itself, or point at
  the closing commit (`git log --grep=BL-NNN`) instead.
- **A `*Playtest after fix:*` line** on the entry means an owed test that may now be actionable —
  if the fix landed and the test was not run, it becomes a `playtest` issue rather than vanishing.

**An issue**: nothing in the tree records it, so there is nothing to delete; the close is
`gh issue close N --comment "..."` with the closure record of §4 as the comment, naming the
landing commit. Since the closing comment names that commit, it can only be posted **after the
commit exists**, so this skill drafts the comment to a file beside the commit message and posts
it only when the user asks, after the commit. Sibling issues that cite `#N` keep resolving on
GitHub, so no rewrite is needed there; a `backlog.md` entry that cites the issue follows the
sibling rule above. Follow-up work the close leaves becomes its own issue with a `⚠ Traps`
section, never a comment on the closed one.

## 3. Retire what the item owned in `playtest.md` or on the tracker

If a `CAP-nn`/`PT-nn` existed **solely** for this item, delete it — the row, its section heading if
that leaves the section empty, and any ⚠ prose block written for it. IDs retire with the item and
are never reused; gaps are expected. A `capture` or `playtest` issue that existed solely for this
item is closed the same way as §2's issue close, with a comment naming the item it served; find
them with `gh issue list --state open --label capture --search "<id>"` (and `--label playtest`),
searching both the `BL-NNN` and the `#N` form.

If it unblocks other items too, **keep it** and only drop this item from its Unblocks column, or
say so in an issue comment.

Captures staged under `playtest/<ID>/` are git-ignored and **not** swept by `CleanScratch.ps1` —
delete the folder with the item, or say you left it.

## 4. Log the outcome in the closing commit's message

The closure record lives in the commit message of the commit that deletes the entry (there is no
history file to append to). Since this skill
does not commit unless asked, **draft the message now**, while the evidence is in context: `Write`
it to a file so the eventual commit is `git commit -F <file>` (per CLAUDE.md), whether that commit
happens on request here or later via `/commit-next`.

Subject: `Close BL-NNN: <what is now true>` or `Close #N: <what is now true>`. Body, in prose:
what settled it and how it was measured or decided; what that rules out; the honest limit of the
evidence and why it does not reopen the question; what changed in the build (often "nothing — the
constant it confirms was already shipping") and whether goldens moved. A future reader finds this
via `git log --grep=BL-NNN` or `git log --grep='#N'`. For an issue, the same prose is the closing
comment of §2, with the commit hash prepended once it exists.

## 5. Strike the caveat where it was *restated* — the step that gets missed

Grep the ID first, then grep the **wording of the doubt** (a distinctive phrase from the entry: a
symbol name, `not a decode`, `TODO`, the TUNE constant's name). Doc bullets and code comments
routinely restate an open question in full without ever naming the `BL-NNN`, and those are what
survive an ID-only sweep.

Everywhere it turns up — `docs/formats/*.md`, `docs/architecture/*.md` (module entries) and
`docs/architecture.md` (the index), `docs/verification.md`,
`CLAUDE.md`/`PROJECT_CONTEXT.md`, and XML doc comments in `CSVM/src` — replace the hedge with the
settled fact, in the present tense. Keep any ⚠ that is still true (a trap about *how* the
mechanism works outlives the question of whether it was right).

⚠ **The fact goes in the doc; the evidence and the date stay in the commit message.** Write "the
unscaled arc reads like the original at the controls", never "judged at the controls on
2026-08-16" or "`PT-46` retired 2026-08-16". A closed item leaves no trace of its own closing in
live prose — that is the whole point of step 4 holding the record. A doc line that narrates a past
event ages, and sends the reader building a timeline instead of reading the current state.

Finish with a grep of the ID across the repo: no hits in any live file. (Retirements live only in
commit messages: `git log --grep=<ID>`.) For an issue, grep `#N` and `issue N`; a `backlog.md`
entry or a doc that cites the issue for evidence gets the sibling treatment of §2.

## 6. Verify and report

- Touched a code comment → `dotnet build CSVM/CSVM.sln`. Clean build, zero warnings (StyleCop is
  hook-enforced anyway).
- Touched code, not just comments → `.\RunTests.ps1`, and quote the real result.
- Docs only → no build, and say so rather than implying one ran.

Report: the closure kind and what settled it, the files touched with a one-line why each, the
verification actually run, and the residual limit if there is one. Then stop — **do not commit**
unless the user asks; offer it in one line. For an issue, offer the commit and the
`gh issue close` in that order, as two lines; the close runs only after the commit.
