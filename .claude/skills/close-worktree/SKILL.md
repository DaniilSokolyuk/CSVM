---
name: close-worktree
description: Close out the current worktree — commit outstanding work, close its BL-NNN or GitHub issue if eligible, merge main in and back out, then remove the worktree. Use when a worktree's work is finished and ready to land on main.
---

Streamlines the repeat closing procedure for a `.claude/worktrees/` branch: commit, gate on backlog
closure, sync with main both ways, land on main, clean up. Runs from **inside the worktree** — it
takes no arguments and refuses to run from the main checkout (`Z:/CSVM` itself has no worktree
branch to close).

Each step below runs in order and any abort stops the whole skill — do not skip ahead.

## 1. Commit outstanding work

`git status --short` in the worktree. If clean, skip to step 2.

Otherwise stage and commit everything as one commit:
- `git add -A`
- Compose a message: subject describing what landed, matching this repo's commit style
  (`git log --oneline -5` for the pattern); body only if there's a verification record or dead-end
  worth keeping. Multi-line message → `Write` it to a file and `git commit -F <file>` (per
  CLAUDE.md) — never a shell heredoc.
- End with a `Co-Authored-By:` trailer naming whichever agent/model is running this session.
- Commit to the worktree's own branch. Do not push.

Note the resulting commit hash for the final report.

## 2. Gate on backlog closure

Extract an item token from the worktree's branch name (case-insensitive, ignoring surrounding
prefixes like `worktree-` or trailing description words): a `BL-NNN` shape, digits directly after
`bl` (`worktree-bl394-ai-identity` → `BL-394`, `bl343-impact-force-gate` → `BL-343`), or a GitHub
issue shape, digits directly after `issue` or `gh` (`worktree-issue12-session-split` → `#12`,
`gh7-utils-leaf` → `#7`).

**No token found** → this worktree isn't for a backlog item. Skip to step 3.

**Token found** → read the item's full text: the entry in `backlog.md`, or the issue's thread with
`gh issue view N --comments` (`docs/agents/issue-tracker.md`), in particular its goal and its
*"how you'd know it worked"* line. Diff the worktree branch against `main`
(`git diff main...HEAD`) and judge, on the entry's own terms, whether that bar is actually met —
not just touched.

⚠ `main...HEAD` diffs from the merge base, which is this branch's own work only while main's history
is intact. A rewritten main (step 3) moves that base back to an old fork point, and the diff then
carries everything since, which is no basis for judging one item. If the diff is far larger than
this branch's own commits account for, run step 3's classification first and diff against the fork
point it identifies instead.

- **Not met** → state plainly which part of the entry's bar the diff doesn't satisfy (missing
  verification, partial fix, wrong symptom addressed, etc.), then **stop the entire skill here**.
  The step-1 commit stands; nothing merges; the worktree stays open for you to keep working in.
- **Met** → invoke the `close-backlog-item` skill for this BL-NNN or #N now, inline, in this
  worktree. Let it make its doc/playtest edits and draft its closing commit message. Then commit
  those edits as their **own commit** (separate from step 1's), using `close-backlog-item`'s own
  subject convention: `Close BL-NNN: <what is now true>` or `Close #N: <what is now true>`, via
  `git commit -F <file>`. Run whatever verification `close-backlog-item`'s own step 6 calls for
  given what it touched. For an issue, the `gh issue close` it drafted is **not** run here: the
  closing comment names a commit that is not on main yet, so it runs in step 5, after the merge.

Note this commit hash too, if made.

## 3. Check whether main has moved

⚠ **Run every git command in this step from inside the worktree, with no `-C` redirect.** A session
that entered through `EnterWorktree` is isolated to that worktree, and a hook refuses any git
command that redirects to the shared checkout (`git -C Z:/CSVM ...`) with "a worktree-isolated
session's git operations must target its own worktree". No redirect is needed anyway: `main` is a
shared ref, readable from any worktree of the same repository.

No fetch and no `merge-base --is-ancestor` against a remembered tip: just read the branch's own
divergence.

```
git log --oneline HEAD..main
```

Any output means main has moved since this branch diverged.

**No output (main hasn't moved)** → skip to step 4.

**Main has moved** → classify what moved before choosing the operation, because a rewritten history
and ordinary new work need opposite treatment.

### Is it new work, or a rewritten history?

A history rewrite (a trailer backfill, an author correction, any filter over old commits) leaves main
carrying the same content under new hashes. A branch still on the old lineage then reads as deeply
diverged from main when nearly all of that divergence is the same content counted twice. Merging in
that state replays every rewritten commit as though it were new work, folding a second copy of the
project's history under one merge commit.

Read both directions before deciding:

```
git log --oneline <worktree-branch>..main
git log --oneline main..<worktree-branch>
```

**A commit subject that appears in BOTH lists is the tell.** Confirm it on the trees, which a rewrite
leaves untouched:

```
git rev-parse <commit-on-your-side>^{tree} <same-subject-commit-on-main>^{tree}
```

Identical trees, with the same author and the same commit date, mean only the message changed: that
is a rewrite. Differing trees mean ordinary new work. A rewrite you cannot account for is worth
raising with the user before going further; either way, do not merge.

**Rewritten history** → replay only your own commits onto main's new tip instead of merging:

1. Find the fork point. Your own commits are the entries of `main..<worktree-branch>` whose subjects
   do NOT also appear in `<worktree-branch>..main`; everything else in that range is old-lineage.
   The fork point is the parent of the oldest of your own: `git log -1 --format=%H <oldest-own>^`.
2. Confirm the range holds your work and nothing else:
   `git log --oneline <fork-point>..<worktree-branch>`.
3. Pin main's tip by SHA rather than using the bare `main` ref, since other sessions land on main
   while this runs: `git rev-parse main`.
4. `git rebase --onto <pinned-main-sha> <fork-point> <worktree-branch>`

A rewrite leaves the fork point with a content-identical twin on main, so nothing moves under the
branch; what it picks up is only whatever is genuinely new on main since the fork. Conflicts here are
real conflicts against that new work, so `resolving-merge-conflicts` applies as it does for a merge.
The rebase rewrites this branch's own hashes, so record the new ones for the final report and treat
any verification recorded in an earlier commit message as describing a tree that no longer exists.

**Ordinary new work** → in the worktree, merge main in:
- `git merge main`
- **Conflicts** → invoke the `resolving-merge-conflicts` skill to resolve them; do not hand-weave
  conflict markers yourself. If that skill itself cannot resolve a conflict and stops, this skill
  stops too — report where it left off.

### Verification after either path

Once main is folded in (merged or rebased onto, with or without conflicts to resolve), this
combination has never been tested together. Run the project's full verification battery on the
worktree — `dotnet build CSVM/CSVM.sln` and `.\RunTests.ps1` (or the narrower check that fits what
the sync brought in, per CLAUDE.md's own hook logic) — and treat this as **mandatory**, not
optional.

- **Failure** → report the failure output and **stop the entire skill here**. Do not proceed to
  step 4 with a broken tree sitting in the worktree.
- If the sync touched `analysis/goldens/manifest.json`, don't naively take either side —
  regenerate goldens on the synced tree and commit the re-pin naming the shots, per the
  worktree-merge lessons already learned on this project.
- Record the result in its own `Verification:` commit, matching the convention already in the log.
  After a rebase this is the only surviving record of what was gated, since the rebased commits'
  own messages describe a tree that no longer exists.

## 4. Leave the worktree, then merge back into main

The merge needs `main` checked out, so the session has to leave the worktree rather than reach into
it. Call `ExitWorktree` with `action: "keep"`: it returns the session to `Z:/CSVM` and leaves the
worktree and its branch on disk for step 5. Do not pass `"remove"` here, since nothing has merged
yet.

- **It exits** → the session is in the main checkout. Merge with plain git, no redirect:

  ```
  git merge --no-ff <worktree-branch> -m "Merge <worktree-branch>: <one-line of what landed>"
  ```

- **It reports no active worktree session** → this session never entered through `EnterWorktree`, so
  it is not isolated and the redirect is allowed from where it stands:

  ```
  git -C Z:/CSVM merge --no-ff <worktree-branch> -m "Merge <worktree-branch>: <one-line of what landed>"
  ```

⚠ **Re-read the divergence immediately before merging.** Other sessions land on main while this
skill runs, so step 3's reading may already be stale: `git rev-list --count <worktree-branch>..main`
must be 0. Anything else means main moved again — go back to step 3 and classify the new divergence
before touching main.

The `--no-ff` preserves the worktree's individual commits under a merge commit, matching the repo's
existing convention (e.g. `Merge worktree-bl348-balloon-kill-chain: close BL-348`). Never push.

If this merge itself conflicts, invoke `resolving-merge-conflicts` again, then re-run the mandatory
verification battery from step 3 before continuing.

## 5. Clean up

Step 4 already left the worktree, so this is plain git in the main checkout with no redirect:

```
git worktree remove <worktree-path>
git branch -d <worktree-branch>
```

If step 4 took the `-C` fallback, the session is still standing inside the worktree it is about to
delete. Change out of that directory first, since Windows refuses to remove a directory a process
is sitting in, then keep the redirect:

```
git -C Z:/CSVM worktree remove <worktree-path>
git -C Z:/CSVM branch -d <worktree-branch>
```

`branch -d` (not `-D`) deliberately refuses if the branch isn't fully merged — treat that refusal
as a signal something upstream of this step went wrong, not something to force past. `worktree
remove` likewise refuses on uncommitted changes; there should be none left, and if there are,
something after step 1 was never committed — go back and fix that rather than forcing.

If step 2 closed a GitHub issue, post its close now that the closing commit is on main:
`gh issue close N --comment-file <the drafted comment>` (or `--comment "..."` for a short one),
with the merge-back commit's hash added to the comment.

## Report

One final message: what got committed in steps 1-2 (hashes + one-line subjects, or "nothing to
commit"), the BL-NNN or #N outcome (closed / not applicable / aborted-with-reason), whether main had
moved and whether it was merged in or rebased onto (say which, and why, when it was a rewrite),
what verification ran, the merge-back commit, and confirmation the worktree and branch
are gone. If the skill stopped early (steps 2 or 3's abort paths), say so clearly and name exactly
what's left in the worktree for you to pick back up.
