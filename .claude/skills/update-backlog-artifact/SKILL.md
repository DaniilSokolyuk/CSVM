---
name: update-backlog-artifact
description: Regenerate and republish the CSVM backlog Artifact (the "by theme & tag" filterable table) from the current backlog.md and the open `backlog` GitHub issues. Use after backlog.md or the tracker changes, or when the user asks to refresh/update/regenerate the backlog table or artifact.
---

Keep the published backlog Artifact in sync with `backlog.md` and the open `backlog`-labelled
GitHub issues on `Laeresh/CSVM` (`docs/agents/issue-tracker.md`). The artifact is a static page
with a `DATA` array baked in at publish time — it does not read either source live, so it goes
stale the moment an item is added, closed, or re-tagged. This skill's only job is: reparse,
reinject, republish **to the same URL**.

This skill is scoped to regenerating the artifact. It does not edit `backlog.md` or any issue, and
it does not decide which items belong in it — every open item in the file and every open `backlog`
issue goes in, full stop.

## 1. Build the page

Run the skill's own parser — it does steps 1–3 (parse, serialize, inject) in one go:

```powershell
python .claude/skills/update-backlog-artifact/build.py <scratchpad>/backlog-table.html
```

It resolves `backlog.md` and `template.html` from its own location, so it works from any working
directory and from a worktree. It prints the item count, theme count, issue count and theme order
to stderr, and **exits non-zero** if a bullet fails to parse, if an ID is duplicated, if the parsed
row count does not match the raw `- \`BL-NNN\`` bullet count in the file, or if `gh issue list`
fails — so a green run *is* the sanity check. On a non-zero exit, fix the parser (or the file)
rather than falling back to a hand parse; the failure message names the offending item. When `gh`
is unavailable and the user wants the file-only table anyway, `--no-issues` before the output path
builds from `backlog.md` alone; say in the report that the issues were left out.

**The GitHub issues** carry `backlog.md`'s tags as labels (`docs/agents/issue-tracker.md`,
"Backlog labels"). Each open `backlog` issue is one row, id `#N`, placed in its `theme:` label's
theme after that theme's `BL-` items. The theme name is the label's *description*, which is
spelled exactly like the `backlog.md` heading. `type:`, `size:`, `next:`, `impact:` and `evidence:`
labels fill their fields. The `owed-playtest`, `blocked` and `divergence` labels fill the status,
and when none of them is present, the triage label does (`docs/agents/triage-labels.md`). The title
is taken verbatim. An issue with no `theme:` label goes into an `Untriaged` theme at the end, and
one with no `type:` label shows as `Issue`. A label value outside the vocabularies is a build
failure that names the issue. Issues carry no scope.

⚠ `build.py` reads and writes explicit UTF-8. Edit it with Read/Edit/Write, never through a
PowerShell `Get-Content`/`Set-Content` round-trip (see `CLAUDE.md`).

Then go to step 4. **Sections 2–3 below document what `build.py` already does** — read them when
`backlog.md`'s own conventions change and the parser has to follow.

## 2. What the parser extracts

Theme order comes from the file itself from the file itself — the `## ` headings in file order,
**excluding** `## Standing notes` (that section holds prose, not items). Do not hardcode the theme
list; if a theme is renamed or a new one is added, the parse should pick it up automatically.

Within each theme, find every top-level bullet matching:

    - `BL-NNN` `[Type]` [`[Status]`] [`[S|M|L]`] [`[Next: …]`] [`[Impact: …]`] [`[Evidence: …]`] [`[Scope]`] **Title…** body…

For each bullet, extract:

- **id** — the `BL-NNN` token, verbatim.
- **type** — the bracketed word right after the id, one of the type vocabulary `backlog.md`'s
  header lists (`Bug`, `Feature`, `Research`, `Tuning`, `Cleanup`, `Fidelity`, `Perf`, `Tooling`,
  `Testing`).
- **status** — a `Owed-playtest`, `Blocked: …` or `Divergence` tag among the tags after the type,
  else `null`. Keep it verbatim, including the reason (`Blocked: CAP-27` / `Blocked: M4` / etc.
  — whatever text is inside the brackets). Don't shorten `Blocked: X` to `Blocked`; the template
  already treats anything starting with `Blocked` as one status class and displays the tag text
  as-is.
- **size**, **next**, **impact**, **evidence**, **scope** — the property tags the header
  documents, each `null` when the item does not carry it. They are recognised by shape, not by
  position: `[S]`/`[M]`/`[L]` is size, `[Next: x]`/`[Impact: x]`/`[Evidence: x]` carry their name,
  and a bare mission or scene token (`[CM14]`, `[C5]`) is scope. A value outside the vocabulary
  `build.py` and `CheckItemIds.ps1` share is a build failure, with the item named.
- **title** — everything between the first `**` after the tags (skipping an optional `{SCOPE}`
  marker, as on `BL-037`/`BL-038`) and its matching closing `**`,
  even when the bold text wraps across multiple source lines. Collapse any internal newlines/runs
  of whitespace to a single space. Keep the text otherwise verbatim (inline code spans, punctuation,
  quotes) — don't paraphrase or truncate it.

Skip nothing: every bulleted item in every theme section goes into the row list, in the order it
appears in the file (themes in heading order, items in file order within a theme — which is already
ascending by ID per the file's own convention).

The parser's own guard is the count check: the row count must equal the raw bullet count in the
file (roughly 100+ as of 2026-08). A count far lower means the title regex swallowed a later bullet
by matching too greedily across items — `build.py` bounds each bullet's text at the next top-level
bullet or `## ` heading to prevent exactly that.

## 3. Serialization and injection

Rows are serialized as a JSON array of objects
`{theme, id, type, status, size, next, impact, evidence, scope, title}`, every optional field
JSON `null` when absent, with real JSON string escaping — never hand-spliced into a JS literal,
since titles routinely contain quotes, backticks, and em dashes. The template builds its filter
chips from the values present in `DATA`, so a new vocabulary value needs no template change; a
property no item carries hides its filter group.

That JSON replaces `__BACKLOG_DATA__` in `.claude/skills/update-backlog-artifact/template.html`,
whose data line is:

    const DATA = __BACKLOG_DATA__;

Nothing else in the template changes — theme order, filters, and counts are all derived from `DATA`
at render time in the page's own script. The result goes to the output path given on the command
line (a file in this session's scratchpad directory, e.g. `backlog-table.html`); generated output
never goes back into the skill's `template.html`.

## 4. Publish to the existing URL

Read `.claude/skills/update-backlog-artifact/artifact.json` for the stored `url`, `favicon`,
`title`, and `description`.

- **File exists (the normal case):** call `Artifact` with `file_path` = the file `build.py` wrote,
  `url` = the stored URL, and the stored `favicon`/`title`/`description`. Passing `url`
  explicitly is what makes this an in-place update instead of minting a new link — required because
  this skill usually runs in a conversation that never published the artifact itself.
- **File missing or has no `url` (first run, or the prior artifact was lost):** call `Artifact`
  without `url` to publish fresh, using favicon `🗂️` and a title/description in the same spirit as
  what's already in `artifact.json` (or the defaults there, if present). Then write the returned URL
  back into `artifact.json` so every later run updates in place.

## 5. Report

State the item count, the theme count and the issue count that got baked in, the artifact URL, and whether
`artifact.json` changed (first-run publish) — if it did, mention it's worth committing so the next
session/skill run picks up the same URL. Don't commit it yourself unless asked.
