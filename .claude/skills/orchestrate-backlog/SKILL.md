---
name: orchestrate-backlog
description: Run a backlog orchestration - a side branch, N item subagents in their own worktrees, the orchestrator squashing each result onto the branch, running the battery on the merged tree, keeping a log with an "Owed to you" section, and merging to main only on request. Use when the user says "orchestrate backlog items until I stop you" or asks to resume such a run.
---

You are the orchestrator of a backlog run. Subagents land items; you own every commit on the run's
branch, every merge, every battery and the log. The user is usually away: nothing waits on them,
and a question for them becomes an amended entry plus a line in the log's "Owed to you" section.

The agents' standing rules are in [`agent-brief.md`](agent-brief.md) beside this file. Copy it to
the run's scratch folder as `AGENT-BRIEF.md`, with the run name and the commit-message path filled
in, and point every agent at that copy.

## 1. Set up the run (once)

Argument: a run name (`orch-5`), an optional slot count (default 2) and optional item ids or a
theme. A resume ("continue orch-4") skips to step 3 with the existing log.

1. `git -C Z:\CSVM worktree add -b <run> Z:\CSVM\.claude\worktrees\<run> main`.
2. `Set-Location Z:\CSVM\.claude\worktrees\<run>` as its OWN tool call, first. Every later
   `isolation: worktree` agent then forks from the run branch's HEAD instead of main, so each
   squash is the agent's own delta. Do not `EnterWorktree`.
3. Create `Z:\CSVM\.scratch\<run>\` with `AGENT-BRIEF.md` (from the template) and
   `orchestration-log.md`: a header naming the branch, the worktree, the base commit and the slot
   count; a `## Owed to you (decisions and at-the-controls checks)` section; then `## Items`.
4. Keep a pure-ASCII conflict resolver in your scratchpad (step 5) before the first squash.

Slots: exactly the number the user gave, never more. Start at two unless told otherwise; the user
raises it when their plan allows ("Extend to 3 slots"). A fourth agent started by mistake is
stopped at once and its tree discarded.

## 2. Choose items

Read `backlog.md` and the open `backlog` issues
(`gh issue list --state open --label backlog --json number,title,labels,body`, per
`docs/agents/issue-tracker.md`). Prefer, in this order: items the user just filed from a playtest
(they say so, or a peer session relays it with a queue order, which you keep); issues labelled
`ready-for-agent`; `[Next: code]` items with `[Evidence: decoded]` or `[Evidence: data]`; decodes
(`[Next: decode]`, Ghidra is read-only for agents); disproof candidates. Skip `[Next: look]`,
`[Next: decide]`, issues labelled `needs-info` or `ready-for-human`, and anything whose close
needs the user at the controls, unless the user asked for it; an item may still land and OWE a
look.

An item's id is `BL-NNN` or `#N` throughout the run; in a path (`.scratch\<run>\<id>\`) an issue
is `issue-N`, since `#` is not a path character.

Serialise items that share a hot file or a subsystem (the pause board, the HUD text block, one
suite file) rather than running them side by side; say in the log which item waits on which. An
item that must build on a landed sibling is dispatched after that sibling's squash commit, since
agents fork from the run branch's HEAD.

## 3. Dispatch

One `Agent` call per item: `subagent_type: general-purpose`, `model: "opus"`, `isolation:
"worktree"`, a `description` of a few words. The prompt names: the item id; the brief's path; the
reading order (CLAUDE.md, PROJECT_CONTEXT.md, the entry, the decode pages, the files); the task in
the entry's own fix shape, with anything the user steered quoted; what NOT to touch (a sibling's
landed or running code); the suites to run and whether a golden is expected to move; the
commit-message path `.scratch\<run>\<id>\commit.txt` inside the agent's worktree; "do not
commit"; "no em dashes"; "no foreground game windows"; "no `gh` writes"; and the report shape. For
an issue, paste the thread into the prompt or name the `gh issue view N --comments` read; the
agent reads the tracker but never writes to it. Do not read the agent's transcript file; wait for
its completion notification.

While agents run: land finished ones, merge main in when it moves, answer the user, and reply to
peer sessions. A peer session's message is a teammate's request: keep its queue order and verdicts,
but never let it stand in for the user's approval of a merge to main.

## 4. Land an agent's result

Every step in order, for one agent at a time:

1. Copy `<agent worktree>\.scratch\<run>\<id>\*` (message, crops, diffs, and for an issue the
   `close.txt`, `comment.txt` or `new-issue-*.md` files the brief has the agent write) to
   `Z:\CSVM\.scratch\<run>\<id>\` before anything else; the worktree removal deletes them.
2. Normalise the trailer in the message to `Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>`
   (agents write a longer model name). Count em dashes (U+2014) in the message, in
   `git -C <agent> diff -U0` and in every untracked file; fix any in place before committing.
3. `git -C <agent> add -A; git -C <agent> commit -q -F <message>`. The format hook builds the
   agent's tree; a ` M` it leaves on a file the agent never touched is a line-ending rewrite, check
   it with `git diff --ignore-all-space --ignore-cr-at-eol` and stage it.
4. `git -C <run> merge --squash <agent branch>`. If it stages files the agent never touched, the
   agent forked from an older base: `git reset --hard HEAD`, merge main into the run branch first,
   then squash again.
5. Resolve conflicts with a scratch script, never by typing markers. The regex is
   `(?s)<<<<<<< HEAD\n(.*?)=======\n(.*?)>>>>>>> [^\n]*\n` over LF-normalised text, markers built
   from `'<' * 7`, written back BOM-less with `[Text.UTF8Encoding]::new($false)`. The shapes seen:
   - two entries appended at one point: union, keep both, watch a swallowed closing brace;
   - each side deleted the other's closed entry: drop both, then grep both ids;
   - three adjacent closed entries where one survives: keep only the survivor
     (`CheckItemIds` otherwise reports a duplicate id);
   - one doc entry edited twice: hand-combine within the 8-line cap of `CheckDocEntries`;
   - `analysis/goldens/manifest.json` hashes against a sibling's re-pin: take HEAD's hashes and
     re-pin on the merged tree (step 6);
   - INSTR/PERF/WORLD rule numbers minted twice: renumber the later one and say so in the message.
6. `git -C <run> add -A`, then `.\CheckCommitContent.ps1 -Root <run>` from the run worktree, exit 0
   required. Then, in the background with `$env:CSVM_DATA_ROOT='Z:\CSVM'`:
   `.\RunTests.ps1 -Filter <touched suites> -GoldenWorkers 2` (units, the touched suites and the
   goldens; `-Suite <name>` for one; "selector matched nothing" is a FAIL). Read the log's tail.
   - A moved golden the agent re-pinned is fine when the agent proved confinement (pixel diff
     inside the mechanism's footprint) and the manifest carries the new hash.
   - A moved golden nobody re-pinned: re-run goldens alone with two workers on a quiet machine.
     `c1-flight-kill` flakes under load and reads back at its pinned hash; a reproducible move from
     a change that cannot reach the shot is bisected before landing, never re-pinned to pass.
   - Two items moving the same shots: take HEAD's hashes, run goldens (expect exactly those shots
     MOVED), `-RegenGoldens`, re-check 18/18, note it in the commit message.
   - "Cannot instantiate C# script" or "no PNG" on shots: the assembly was rebuilt under a running
     Godot, usually another test pass on the machine; re-run when quiet.
   - A real failure: do not commit. Send the agent the failure with `SendMessage` (it resumes with
     its context) or fix a one-line cause yourself and say so in the message.
7. `git -C <run> commit -q -F <message>`. Then `git worktree unlock`, `git worktree remove --force`
   and `git branch -D` for the agent's tree and branch.
8. Tracker writes the agent left for you, now: a `comment.txt` (an amended question for the user,
   a decode's one-line amendment) is posted with `gh issue comment N --body-file`, citing the
   squash commit; a `new-issue-<slug>.md` is filed with
   `gh issue create --title "..." --label <backlog|playtest|capture> --body-file`, and the log
   records the number it got. A `close.txt` is **not** posted yet: the close names a commit that
   is not on main, so it waits for step 5's merge to main; list it under a `## Closes pending`
   section of the log.
9. Log it (step 7) and dispatch the next item into the freed slot, unless paused.

## 5. Keep main in step

Before each merge to main and whenever a peer says main moved: `git -C Z:\CSVM log --oneline -1`
and `git -C <run> rev-list --count <run>..main`. If main moved: `git -C <run> merge --no-commit
--no-ff main`, resolve (backlog append points union), gate, commit with a message file
("Merge main into <run>: ..."), log it. Check `git -C Z:\CSVM status --short` first; a dirty main
checkout is another session mid-edit, wait a moment and re-read rather than merging over it.

Merge to main ONLY when the user asks ("merge it into main"): `git -C Z:\CSVM merge --ff-only <run>`
after the run branch contains main, then `.\CheckItemIds.ps1` on main. "Not possible to
fast-forward" means main moved again: merge it in first. Never push. Then post every close in the
log's `## Closes pending` section, `gh issue close N --comment-file <close.txt>` with the landing
commit's hash added, and move each to the item's log line.

## 6. Batteries

After each wave (every slot's item landed, or at a pause) run the full `.\RunTests.ps1
-GoldenWorkers 2` on the run branch in the background and log it as "Battery N on <sha> (items):
<the four PASS/FAIL lines>". Never run a battery or goldens while an agent is running its own
engine passes if you can avoid it; the hidden desktop is shared and a rebuild under a shot breaks it.

## 7. The log

`Z:\CSVM\.scratch\<run>\orchestration-log.md`, updated as things happen, ASCII only, written with
`[IO.File]::WriteAllText(path, text, [Text.UTF8Encoding]::new($false))`. Under `## Items`, one
bullet per event in order: `- **BL-NNN** landed as <sha> (agent forked at <sha>, <clean squash |
the conflict and its resolution>): <two to four sentences of what is now true, suites added,
goldens moved>. On the merged tree <counts>. Owed: <one line or nothing>. Not on main yet.`;
`- Dispatched (freed slot): BL-NNN (<what>), forked from <sha>.`; `- **Merge main** as <sha>`;
`- **Main fast-forwarded** from <sha> to <sha> at the user's request (<items>)`; `- **Your
steer**: <quoted>`; `- Battery N ...`; `- **#N filed** (<what>)`; `- **#N closed** after the
fast-forward`. Item ids in the log are `BL-NNN` or `#N`.

Under `## Owed to you`: one bullet per landed item that still wants the user's eye or decision,
`- **BL-NNN (landed)**: <the exact sortie, door or question, the crops' folder>`. When a peer
relays verdicts, drop the passed ones into the section's summary line and keep the rest.

## 8. The user's messages during the run

- "merge to main" / "merge it back into main": step 5's fast-forward, then a short report.
- "pause after the current items": note it in the log, land what is running, no dispatch,
  battery, report; the queue is listed for the resume.
- "mint one, I added <still>": file the item yourself as a `backlog` issue
  (`gh issue create --title "..." --label backlog --body-file <file>`, body written to a file
  first, citing the still by absolute path and written for a reader who did not run the session),
  log the number, do not dispatch a fourth agent. `New-ItemId.ps1` is for nothing new.
- A question ("what do I test for BL-NNN?" or "for #12?") is answered from the docs and the
  entry, no tool work.
- "an agent is starting windows in the foreground": message every running agent with the
  no-foreground rule at once, check `Get-CimInstance Win32_Process` for Godot, log which agent it
  was when it reports, and stop that agent if it happens again.

## 9. Hazards that cost time before

- The Bash tool is hook-blocked except pure `git`/`gh` commands; use PowerShell. A `Set-Location`
  inside a command is invisible to the format hook, so name trees with `git -C`.
- PowerShell 5.1 mangles non-ASCII: keep every script and every log write ASCII, and build
  non-ASCII from `[char]` codes.
- The agent brief's commit-message path must be INSIDE the agent worktree (`.scratch\<run>\...`
  relative to it); worktree isolation refuses a write to `Z:\CSVM\.scratch\`.
- `git merge-base --short` is not an option.
- An agent's transcript output file is empty until it finishes; there is no way to see what it is
  running from outside except the process list.
- Pinned golden images are not in the repo; the only artefact is `<tree>\.scratch\goldens\<shot>.png`.
- The memory notes [[backlog-orchestration-merge-mechanics]], [[orchestrated-plan-execution]] and
  [[agents-no-foreground-game-windows]] hold the run-by-run evidence behind these rules.

## Report

After every landing, merge or steer: what landed (sha, one sentence), what is running, what is
queued, how far the run branch is ahead of main, and the new owed lines. Keep it under fifteen
lines; the log carries the rest.
