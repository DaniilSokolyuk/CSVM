---
name: update-playtest-artifact
description: Regenerate and republish the CSVM playtest Artifact (owed captures + actionable flights, with a friend-facing HowTo) from the current playtest.md and the open `playtest`/`capture` GitHub issues. Use after playtest.md or the tracker changes, or when the user asks to refresh/update/regenerate the playtest table or artifact.
---

Keep the published playtest Artifact in sync with `playtest.md` and the open `playtest`- and
`capture`-labelled GitHub issues on `Laeresh/CSVM` (`docs/agents/issue-tracker.md`). The artifact
is a static page with `CAP_DATA`/`PT_DATA` arrays baked in at publish time — it does not read
either source live, so it goes stale the moment an item is added, closed, or edited. This skill's
only job is: reparse, reinject, republish **to the same URL**.

This skill is scoped to regenerating the artifact. It does not edit `playtest.md` or any issue,
and it does not decide which items belong in it — every open `CAP-nn` row and `PT-nn` item in the
file, and every open `capture` and `playtest` issue, goes in, full stop. It also does not touch
the artifact's preamble/HowTo copy (see step 0) — that is friend-facing prose authored once, not
derived from `playtest.md`.

## 0. What NOT to regenerate

`template.html` has a static preamble section (CSVM one-liner, CAP-vs-PT explainer, the auto-head-
turn-off validity gate, the "found something?" report line) written for external testers, not repo
contributors. It is **not** parsed from `playtest.md` — leave it alone unless the user explicitly
asks to change the HowTo copy itself.

## 1. Build the page

Run the skill's own parser — it does steps 1–4 (parse both sections, serialize, inject) in one go:

```powershell
python .claude/skills/update-playtest-artifact/build.py <scratchpad>/playtest-table.html
```

It resolves `playtest.md` and `template.html` from its own location, so it works from any working
directory and from a worktree. It prints the CAP row/theme and PT item/profile counts to stderr,
with the issue-derived counts beside them, and **exits non-zero** if the parsed counts do not
match the raw `| \`CAP-` / `- \`PT-` counts in the file or if `gh issue list` fails — so a green
run *is* the sanity check. Unparsed item heads, unknown tags and missing bold titles are named on
stderr. On a failure, fix the parser (or the file) rather than falling back to a hand parse. When
`gh` is unavailable and the user wants the file-only page anyway, `--no-issues` before the output
path builds from `playtest.md` alone; say in the report that the issues were left out.

**The GitHub issues** land as one more CAP theme and one more profile, both named `GitHub
issues`, last in their orders. A `capture` issue is a CAP row: id `#N`, capture = the title,
detail = the body's "what must be in frame" section (else its lead paragraph), unblocks = its
"Unblocks" section (else the `BL-`/`#N` references in the body). A `playtest` issue is a PT item
with tag `Issue`: title verbatim, context = the lead paragraph, look-for = the bullets under "What
to look for", blocks = the "What it blocks" section, and its launch line (the first `RunGame.ps1`
code span or block) in the notes. The profile's command is the first launch line found, since
each issue carries its own.

⚠ `build.py` reads and writes explicit UTF-8 and contains the `·`/`—` separators `playtest.md`
itself uses. Edit it with Read/Edit/Write, never through a PowerShell `Get-Content`/`Set-Content`
round-trip (see `CLAUDE.md`).

Then go to step 5. **Sections 2–4 below document what `build.py` already does** — read them when
`playtest.md`'s own conventions change and the parser has to follow.

## 2. CAP rows (Section 0)

From `## 0 · Owed captures`. Get theme order from the `### ` headings within
that section, in file order. Each theme heading's trailing clause (after the em dash, if present)
is descriptive only — use the heading text verbatim as the theme name.

Each theme is followed by a markdown table with a header row whose last-but-one column is either
`What must be in frame` or `What must be audible` (Audio is the only `audible` theme currently) —
capture that column's exact header text as the theme's `detailLabel`.

For each data row `| \`CAP-NN\` | Capture | Detail | Unblocks |`:
- **id** — the `CAP-NN` token, verbatim.
- **capture** — the second column, verbatim (may contain inline code/bold).
- **detail** — the third column, verbatim.
- **unblocks** — the fourth column, verbatim (may be multiple `BL-NNN` refs with parentheticals, or
  empty).

**Skip themes with zero data rows entirely** — do not emit an entry for them (the artifact hides
empty themes by design; `playtest.md` itself keeps the empty table as a "nothing owed right now"
marker, but that's a file-only convention).

Sanity check (the parser's own): row count must match `grep -c '| \`CAP-' playtest.md`.

## 3. PT profiles (Section 1)

From `## 1 · Actionable now`. Each `### ` heading within it is one **flight profile**. Parse the
heading `<Chapter> · <Plane/Pilots> — <Situation>` into:
- **chapter** — text before the first ` · ` (e.g. `C1`, `C1B`).
- **planeOrPilots** — text between ` · ` and ` — `.
- **situation** — text after ` — `.
- **heading** — the full heading text, verbatim, for display.

Immediately following the heading is one ```` ```powershell ```` block — capture its single command
line verbatim as **command**.

### Translating the command into a menu step

Parse the codes→names map live from this file's own **"Plane model names"** line near the top
(don't hardcode it — it's the source of truth): each `` `player_xxx` Name `` pair, split on `·`.

From **command**, extract flags and build **menu**:
- `--plane=player_xxx` → look up the display name → `menu.plane`. Absent (e.g. a `--vs` profile
  where each pilot picks their own) → `menu.plane = null`.
- `--chapter=X` → `menu.chapter` (verbatim, e.g. `C1B`).
- `--vs` present → prepend `"Mode: Dogfight (splitscreen)"` to `menu.notes`.
- `--players=N` → append `` "N players" `` to `menu.notes`.
- `--infinite-ammo` → append `"infinite ammo — enable in-game if the menu offers it, otherwise skip"`
  to `menu.notes`.
- any other flag → append its literal text (e.g. `--stunt`) to `menu.notes` verbatim — don't invent
  a phrase for flags not seen before; a raw flag is a safe fallback since **command** is always
  shown alongside menu steps in the template.

`menu.notes` is an array, possibly empty.

### PT items within a profile

Each top-level `- \`PT-nn\` ...` bullet is one item (sub-bullets belong to it, not to a new item).
Parse:

- **id** — the `PT-nn` token, verbatim.
- **tag** — the bracketed tag right after the id: `` `[A/B: <ref>]` `` → `{type: "A/B", ref:
  "<ref>"}` (ref verbatim, drop the surrounding backtick if the ref itself isn't code); `` `[Own]`
  `` → `{type: "Own"}`.
- **title** — the bold text immediately after the tag, collapsing internal newlines/runs of
  whitespace to a single space, verbatim otherwise (keep inline code spans, `BL-NNN` refs,
  punctuation).
- **context** — prose between the end of the bold title and the `*Look for:*` (or `*Blocks:*` if
  there's no Look for) marker, whitespace-collapsed. Empty string if there's none.
- **lookFor** — array of strings, one per lettered sub-bullet (`(a)`, `(b)`, …) under `*Look for:*`,
  each whitespace-collapsed, letter prefix kept in the text (e.g. `"(a) firing: a subtle fast roll
  buzz…"`). If `*Look for:*` has no lettered sub-bullets (a single inline check), the array has one
  entry with no letter prefix.
- **notes** — any additional labeled paragraph(s) that appear *after* `*Look for:*` and *before*
  `*Blocks:*` (e.g. a `*CAP-11 evidence (…):*` paragraph) — rare, empty string when absent. Keep the
  label text as part of the string, whitespace-collapsed.
- **blocks** — text after `*Blocks:*` up to `*Variations:*` or the item's end, whitespace-collapsed.
  Always present (mandatory field per `playtest.md`'s own convention).
- **variations** — text after `*Variations:*` to the item's end, whitespace-collapsed. Empty string
  when absent.

Items stay attached to the profile section they're physically under — do not regroup by
chapter/plane beyond what the file's own sectioning already does (an item "free to choose its plane"
already piggybacks on an existing section per the file's convention; the parser doesn't need to
infer that).

Sanity check (the parser's own): item count must match `grep -c '^- \`PT-' playtest.md`.

## 4. Serialize and inject

Build two structures:

**`CAP_DATA`** — array of theme objects, themes with data rows only, in file order:

    [{ "theme": "...", "detailLabel": "...", "rows": [
      { "id": "CAP-NN", "capture": "...", "detail": "...", "unblocks": "..." }, ...
    ]}, ...]

**`PT_DATA`** — array of profile objects, in file order:

    [{ "heading": "...", "chapter": "...", "planeOrPilots": "...", "situation": "...",
       "command": "...", "menu": { "chapter": "...", "plane": "..."|null, "notes": [...] },
       "items": [
         { "id": "PT-nn", "tag": {...}, "title": "...", "context": "...", "lookFor": [...],
           "notes": "...", "blocks": "...", "variations": "..." }, ...
       ]}, ...]

Both use real JSON string escaping — never hand-spliced into a JS literal, since titles and context
routinely contain quotes, backticks, and em dashes.

Each replaces its placeholder in `.claude/skills/update-playtest-artifact/template.html`, whose two
data lines are:

    const CAP_DATA = __CAP_DATA__;
    const PT_DATA = __PT_DATA__;

Nothing else in the template changes — tabs, filters, grouping, and the preamble/HowTo copy are
static or derived from the data at render time. The result goes to the output path given on the
command line (a file in this session's scratchpad directory, e.g. `playtest-table.html`); generated
output never goes back into the skill's `template.html`.

## 5. Publish to the existing URL

Read `.claude/skills/update-playtest-artifact/artifact.json` for the stored `url`, `favicon`,
`title`, and `description`.

- **File exists (the normal case):** call `Artifact` with `file_path` = the file `build.py` wrote,
  `url` = the stored URL, and the stored `favicon`/`title`/`description`.
- **File missing or has no `url` (first run, or the prior artifact was lost):** call `Artifact`
  without `url` to publish fresh, using favicon `🎬` and title `CSVM Playtest — Captures & Flights`
  (or what's already in `artifact.json`, if present). Then write the returned URL back into
  `artifact.json` so every later run updates in place.

## 6. Report

State the CAP row count + theme count, the PT item count + profile count, how many of each came
from GitHub issues, the artifact URL, and
whether `artifact.json` changed (first-run publish) — if it did, mention it's worth committing so
the next session/skill run picks up the same URL. Don't commit it yourself unless asked.
