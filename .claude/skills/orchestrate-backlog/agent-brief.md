# Brief for every item agent of orchestration run <RUN>

You are one subagent of an orchestration run. You land ONE backlog item (or the small bundle named
in your prompt) completely, in your own git worktree, and you NEVER commit. The item is a `BL-NNN`
entry in `backlog.md` or a `#N` GitHub issue (read it with `gh issue view N --comments`; in a path
it is `issue-N`). The orchestrator reviews your tree, commits it, squashes it onto the
orchestration branch and runs the full battery on the merged tree. The user is away: nothing you
do may wait on them.

## Ground rules

- Work only inside your worktree (the path the harness gave you). Use ABSOLUTE paths for every
  `[IO.File]` call and every script (a relative path resolves to the MAIN checkout `Z:\CSVM`).
  Set `$env:CSVM_DATA_ROOT = "Z:\CSVM"` before any build, test or probe, since the extracted game
  data lives only in the main checkout; confirm the unit and engine suite counts are non-zero.
- Read `PROJECT_CONTEXT.md` and `CLAUDE.md` in your worktree first, then the skills
  `.claude/skills/backlog/SKILL.md` and `.claude/skills/close-backlog-item/SKILL.md`. Follow their
  rules for reading the item, sizing it, decoding, and closing it, except that the "offer, do not
  act" menus do not apply: you decide and act, since nobody can answer.
- Never `git commit`, `git stash`, `git checkout --`, `git reset`, `git merge`. Never touch
  `Z:\CSVM` (the main checkout) or any other worktree. Never create junctions or symlinks. Read
  git-ignored media (`Z:\CSVM\OriginalScreenshots\...`) by absolute path.
- The Ghidra MCP (`mcp__ghidra-mcp__*`, load schemas with ToolSearch) is READ-ONLY: no renames, no
  comments, no structs, no saves. Every decoded constant is reported with the address it came from
  and the condition it applies under. Decoded knowledge goes into `docs/org/<topic>.md` (existing
  page's topic) or `analysis/<slug>/FINDINGS.md`, never into the Ghidra database.
- Take every value from the extracted data or the decode, never from a guess.
- Comments: caps in `PROJECT_CONTEXT.md` (run `.\CheckCommentCaps.ps1` from your worktree); no item
  ids, dates or plan references in code comments or live docs; no em dashes anywhere you write;
  the writing-style rules in `CLAUDE.md` bind you.
- Docs: a changed module updates its `docs/architecture/<Namespace>.md` entry (run
  `.\CheckDocEntries.ps1`); a new verification rule goes in `docs/verification.md` (mint the next
  number from your tree; the orchestrator renumbers collisions); a new CLI flag updates
  `docs/cli.md`. Run `.\CheckEncoding.ps1` and `.\CheckItemIds.ps1` before you finish.
- NEVER write to the tracker: no `gh issue create`, `comment`, `edit` or `close`. `gh issue view`
  and `gh issue list` are fine. Anything you would post is written to a file beside your commit
  message (below) and the orchestrator posts it after your work has landed.
- Never mint a new `BL-`/`PT-`/`CAP-` id; a new item is a GitHub issue, and you write its body to
  `.scratch\<RUN>\<id>\new-issue-<slug>.md` INSIDE your worktree, first line `Title: ...`, second
  line `Label: backlog|playtest|capture`, then the body, written for a reader who did not run the
  session and with a `⚠ Traps` section when there is one. The orchestrator files it.

## Verification (foreground only)

Run everything in the FOREGROUND and wait for it; a backgrounded run orphans you. Minimum before
you report: `dotnet build CSVM/CSVM.sln` clean with zero warnings, `dotnet test` (or
`.\RunTests.ps1 -UnitFilter ... -SkipEngine -SkipGoldens`), every engine suite you added or
touched via `.\RunTests.ps1 -Suite <name> -SkipUnits -SkipGoldens`, and `.\RunTests.ps1 -Quick`.
If your change can move a pinned golden (anything under `CSVM/src` that draws, spawns effects,
changes pools or seeds), also run `.\RunTests.ps1 -SkipUnits -SkipEngine` (goldens only) and
report which shots moved and why; re-pin with `-RegenGoldens` ONLY when the move is the intended
effect, prove it by differencing against a render from HEAD's sources, and name the shots and the
mechanism in your report. `c1-flight-kill` flakes under load: re-run before believing a move and
never re-pin it for that. Do NOT run the complete `.\RunTests.ps1` battery; the orchestrator runs
it on the merged tree.

## No foreground game windows

Never launch Godot or the game so that a window appears on the user's screen, and never
`Start-Process` anything. Every engine run, capture and golden render goes through
`.\RunTests.ps1` (hidden desktop `csvm-tests`) or `.\RunProbe.ps1`, or a launcher flag that renders
headless or on that desktop. A one-off capture those cannot take is described in your report, not
taken. Before any `--campaign=` probe, copy and rename the user's profile and delete the copy
afterwards. Kill any Godot you started before you report.

## Closing the item

When the item is settled (fixed, answered, disproved or superseded), do the `close-backlog-item`
steps yourself: for a `BL-NNN`, delete the entry from `backlog.md` and retire any `PT-`/`CAP-` it
alone owned in `playtest.md`; for either kind, sweep the restated caveat out of docs and comments,
and grep the id (no hits in any live file). Write the closing commit message to
`.scratch\<RUN>\<id>\commit.txt` INSIDE your worktree (create the folder; the orchestrator copies
it out before removing your tree): subject `Close BL-NNN: <what is now true>` or
`Close #N: <what is now true>`, body in prose with what settled it, how it was measured, the honest
limit of the evidence, what changed in the build and whether goldens moved, ending with the line
`Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>`. Put crops and diffs beside it. For an
issue, also write `close.txt` beside it: the closing comment, the same prose without the trailer;
the orchestrator posts it once the commit is on main. A `capture` or `playtest` issue the item
alone owned gets its own `close-<N>.txt`.

If the item cannot be closed without the user (a taste call, a decision between two faithful
readings, a look at the controls that no instrument can replace): do everything that does not
depend on the answer, then AMEND the item in place. For a `BL-NNN`: keep its id, set
`[Next: decide]` or `[Next: look]`, and write the exact question or the exact sortie into the
entry's body with the evidence you gathered. For an issue: write that text to
`.scratch\<RUN>\<id>\comment.txt`, and name the triage label it should carry (`needs-info` or
`ready-for-human`) on its first line as `Label: ...`; the orchestrator posts it. Do not file a
`playtest` item for a look you merely would like; file one only when a landed change is
unjudgeable by instrument. Write that commit message to the same path with subject
`BL-NNN: <what changed>` or `#N: <what changed>`.

If you disprove the item's premise, that is a close ("closed, disproved"): record the disproof in
the commit message and any transferable lesson as a `docs/verification.md` rule.

## Report

Your final message is the only thing the orchestrator reads. Keep it under 40 lines:
1. Outcome: closed / closed disproved / amended (question for the user, quoted) / blocked (why).
2. What changed, by file, one line each. Name any golden re-pinned and why, with the proof.
3. Verification actually run, with the real counts and results (a failure is reported, not hidden).
4. Anything owed to the user (a look at the controls, a decision), one line each.
5. Any doc rule numbers you minted (INSTR-nn, PERF-nn, WORLD-nn, ...) so collisions can be fixed.
6. The path of your commit.txt, of any close/comment/new-issue files beside it, and
   `git status --short` of your worktree, verbatim.
