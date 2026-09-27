# CLAUDE.md

Read [`PROJECT_CONTEXT.md`](PROJECT_CONTEXT.md) first, it has the project description,
architecture decisions, repo layout, the Godot project + CLI reference, coding conventions, and
the "Current status" pointer. This file holds only what's specific to Claude Code as a tool.

Follow PROJECT_CONTEXT.md's "Development verification loop"; the complete `.\RunTests.ps1` is the
landing gate for any change under `CSVM/`.

## Claude Code specifics

- **Skills** live in `.claude/skills/`, mirrored at `.agents/skills/` for other `.agents/`-aware
  tools; see [`AGENTS.md`](AGENTS.md) for the mirror command. Edit the files directly. Invoke with
  their slash commands, e.g. `/domain-modeling`, `/commit-next`, `/new-plan`.
- When delegating to a subagent, use a cheaper model tier where appropriate, for example for
  exploration.
- **Hooks:** `.claude/settings.json` runs four `PreToolUse` hooks. (1) A shell-syntax guard that
  rejects a PowerShell here-string (`@'…'@`) sent to the **Bash** tool, and a heredoc or
  `/dev/null` sent to the **PowerShell** tool. (2) The **Bash** tool is blocked outright with
  "Use Powershell instead of bash", the one exception is a command whose every `&&`/`||`/`;`/`|`
  segment starts with `git` or `gh`, since those behave identically in either shell.
  (3) The format gate, [`FormatBeforeTests.ps1`](FormatBeforeTests.ps1): `dotnet format` and a
  `-t:Rebuild` that blocks on remaining StyleCop warnings, before an *invocation* of
  `RunTests.ps1`, `dotnet test`, or `git commit`. A command segment counts only when it begins
  with one of those, so reading, grepping or quoting the runner never builds. The Rebuild is
  deliberate: analyzer warnings are emitted only when the compiler runs, and an incremental build
  of an up-to-date tree reports nothing. (4) The content gate,
  [`CheckCommitContent.ps1`](CheckCommitContent.ps1), before a commit.
  ⚠ **Hook (3) formats the tree the command names.** An absolute `RunTests.ps1` path names its
  own tree, a `git -C <tree>` anywhere in the command names one, and only a command naming
  neither falls back to the session's ambient cwd. A `Set-Location` inside the same command is
  invisible to it, since a `PreToolUse` hook runs before the command does; do it first as its own
  call. `.\FormatBeforeTests.ps1 -ShowRoot -Command '…'` says which tree a command would build,
  and `-SelfTest` exercises the trigger and the resolution.
- **The content gate** runs six checks, each its own script you can also run by hand while
  editing: [`CheckEncoding.ps1`](CheckEncoding.ps1) (double-encoded UTF-8, whole tree),
  [`CheckItemIds.ps1`](CheckItemIds.ps1) (`backlog.md`/`playtest.md` defining the same
  `BL-`/`PT-`/`CAP-` ID twice, or a `backlog.md` header tag outside the vocabularies the file's
  own header documents), [`CheckGoldenProse.ps1`](CheckGoldenProse.ps1) (an `exercises`
  field in `analysis/goldens/manifest.json` over 250 chars, or carrying an item id, a date or an
  "also exercises" clause, that field says what a shot covers *today* and is REWRITTEN on a
  re-pin, never appended to, since the history is `git log -p` on the file),
  [`CheckCommentCaps.ps1`](CheckCommentCaps.ps1) over `CSVM/src` and `CSVM.Tests` (`-Summary` for
  one line per file; the sentence caps apply to the comment blocks the commit changes), and [`CheckDocEntries.ps1`](CheckDocEntries.ps1) (`docs/architecture/*.md`
  entry caps and coverage against `CSVM/src`, one-line `docs/architecture.md` index bullets, and
  `docs/cli.md`'s 600-character flag bullet cap), and [`CheckWaiver.ps1`](CheckWaiver.ps1) (the
  form of a `Waiver:` line in the commit's own `-m`/`-F` message, per PROJECT_CONTEXT.md's
  verification loop; it cannot tell whether the battery was red). A comment block over cap has outgrown its
  subject, so reflowing it is the wrong fix: move the decode into `docs/` and leave the
  prohibition on the member it binds.
  ⚠ **The gate checks the one tree the commit writes to, which is not always the one the hook
  stands in.** Taking the hook's own directory and stopping there cleared a commit against one tree
  and wrote it into another, and a corrupted character reached main that way. So the target is read
  off the command in three steps: the tree it names (`git -C`, `--work-tree`, `--git-dir`), else the
  directory it changes to first (`Set-Location <path>; git commit`, which the hook cannot observe
  because it runs first but can read in the command string), else the tree the hook stands in.
  Content arriving by merge or pull is caught the same way: it is present in the tree that merged
  it, so that tree's own next commit is where it blocks. ⚠ **Do not widen this back to a sweep of
  every worktree.** A sweep blocks your commit on a file in a tree you cannot fix (with a dozen live
  worktrees that means another session's *uncommitted* work stops yours), and the only way past is
  the escape hatch, which is how a gate teaches people to skip it. Set
  `CSVM_SKIP_CONTENT_CHECKS=1` to land something first. `.\CheckCommitContent.ps1 -SelfTest`
  exercises the whole gate; `-ShowRoots -Command '…'` says which tree a given command would check.
  ⚠ **Each gate owns its own trigger, and a harness never carries one.** Hooks (3) and (4) hand
  their script every shell command, and the script decides whether that command invokes what it
  guards. The decision is not "does `git` stand immediately before `commit`": git's own global
  options stand between the two in `git -C <tree> commit`, which is the form this file prescribes
  for naming a tree, so a trigger written that way skips the gate for every worktree-scoped commit.
  Only git's globals are allowed in that gap, which keeps `git log --grep=commit` and a commit
  quoted inside another command's argument out. Both self-tests enumerate those forms; extend them
  rather than trusting a regex on inspection.
- **The same gate serves Codex and pi.** `.codex/hooks/pre-tool-use.ps1` and
  `.pi/extensions/hooks.ts` call `CheckCommitContent.ps1` rather than reimplementing the checks;
  three hand-maintained copies had already drifted apart. Add a check to the repo scripts, never
  to a harness.
- ⚠ **PowerShell 5.1 corrupts UTF-8 silently.** It reads BOM-less files as ANSI, so a
  `Get-Content`/`Set-Content` round-trip without `-Encoding utf8` on **both** ends turns every
  em dash, arrow and warning sign into double-encoded garbage, and a BOM-less `.ps1` containing
  non-ASCII is mangled by the *interpreter itself* before it runs. Edit repo text with the
  Read/Edit/Write tools; when a script must write a repo file, pass `-Encoding utf8` (or use
  `[IO.File]` with an explicit `UTF8Encoding`) and keep the script itself pure ASCII, building
  any non-ASCII characters from `[char]` codes. The encoding check above is the backstop, not the
  plan.
- ⚠ **Multi-line commit messages: `Write` the message to a file, then `git commit -F <file>`.**
  Shell quoting is where this goes wrong: PowerShell's `@'…'@` needs its closing delimiter at
  column 0, and the same text handed to Bash (still allowed for `git`) is silently accepted as
  literal `@` lines, committing a corrupted message. The `-F` form has no shell quoting at all,
  so it cannot go wrong in either tool. Hook (1) above is the backstop, not the plan.
- Agent worktrees/workspaces created with `isolation: worktree` land in
  `.claude/worktrees/` / `.claude/workspaces/`, both gitignored, swept by
  `CleanScratch.ps1`.
- ⚠ **Never create junctions or symlinks from a worktree (or `.scratch/`) into the main
  checkout.** Git-ignored media (`OriginalScreenshots\`, `playtest\`) is absent from worktrees
  by design, read it via absolute path (`Z:\CSVM\OriginalScreenshots\...`) instead of linking
  it in. PowerShell 5.1's recursive delete follows junctions into their *target*, so a link
  left behind turns any later cleanup into a deletion of irreplaceable original-game footage.
  `CleanScratch.ps1` unlinks reparse points before sweeping as a backstop, but other tools'
  recursive deletes have no such guard.


## Writing style

- No em dashes. Use commas, parentheses, or a new sentence.
- Banned phrases: "load-bearing", "worth stating plainly", "full stop",
  "carry the argument", "the trap", "isn't just X — it's Y".
- No punchy fragments for drama. Write complete sentences.
- Do not build to a turn of phrase. State the claim directly.
- Technical documentation, not marketing copy.
- **Docs state what is, not what was.** No dates and no event narration in live prose
  (`docs/`, `backlog.md`, `playtest.md`, code comments): write "the unscaled arc reads like the
  original at the controls", never "judged at the controls on 2026-08-16" or "retired 2026-08-16".
  A date in live prose is a claim that ages and makes the reader rebuild a timeline instead of
  reading the current state. When closing an item, the evidence and its date go in the closing
  commit's message, found later with `git log --grep=<ID>`. The dated
  `### ⚠ … — RETIRED (yyyy-mm-dd)` headings in `docs/org/*.md` are the record of superseded
  readings, and are the only place a date belongs; do not extend the pattern elsewhere.
  `backlog.md` entries written before this rule keep their dated clauses until the entry closes or
  an edit reshapes its body, at which point the rule applies; there is no sweep of the old entries.
- Comments explain why. Length caps and what belongs in one are in
  [`PROJECT_CONTEXT.md`](PROJECT_CONTEXT.md)'s coding conventions; the terms to use are in
  [`CONTEXT.md`](CONTEXT.md).
- Write no documentation unless explicitly asked.
