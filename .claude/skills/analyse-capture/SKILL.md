---
name: analyse-capture
description: Analyse an owed capture (CAP-nn, or a `capture` GitHub issue) — find its videos and screenshots, watch them, and answer the backlog items it was filmed to unblock. Use when the user names a CAP-NN or a capture issue, hands over new footage of the original game, or asks what a capture shows.
---

Turn one recording of **the original game** into evidence a backlog item can close on. The
capture is a `CAP-nn` row in `playtest.md`, or a `capture`-labelled GitHub issue on
`Laeresh/CSVM` (`docs/agents/issue-tracker.md`); the items it unblocks are `backlog.md` entries or
`backlog` issues.

The capture exists because a question could not be answered from extracted data or from our own
build — so the whole value is in reading the footage honestly. This skill is **watch-and-describe**;
it edits `backlog.md`/`playtest.md` or comments on the issues only at the end, and never commits
unless asked.

⚠ **Never answer from the filename or from the backlog's expectation of what the clip shows.**
Every claim must cite a timestamp or a frame number you actually produced. If the footage does not
show the thing, the finding is "the clip does not settle this" — which is a real result and must be
reported as one.

⚠ **A capture answers what something LOOKS or SOUNDS like, never what a number is.** A quantity read
off the cockpit panel or measured between two frames does not settle a flight constant: footage
cannot confirm a decode, it only ranks readings, and it will happily rank one nobody has thought of
(`docs/verification.md` DET-12; DET-11 on the sim clock that makes every wall-clock rate wrong by
39 %). Numbers come out of `crimson.exe` — see `docs/org/flightModel.md`. The gauge-decode pipeline this skill once
carried was retired for exactly this reason; do not rebuild it, and do not quote a figure from the
`videodata/` sidecars it left behind. If a capture's question turns out to be numeric, say so and
send it to a decode instead of measuring frames.

## 0. Work in a worktree

Enter a git worktree with `EnterWorktree` before touching anything else — name it after the
capture (e.g. `worktree-cap-08`). The skill edits `backlog.md` and `playtest.md`
and can span several sessions, so isolating the work keeps `main` clean and lets
other captures run in parallel. Exit with `action: "keep"` when you report (Section 6), so the user
can review and merge the branch themselves — never merge or push it yourself.

## 1. Resolve the capture

Argument may be `CAP-16`, `#14`, "issue 14", a bare `16`, or a phrase like "the crash one".

Read the `CAP-nn` row in [`playtest.md`](playtest.md) §0, or the issue's thread with
`gh issue view N --comments` — it gives the capture's **purpose**, what must be in frame, and the
`BL-NNN`s or `#N`s it unblocks. Then read each of those backlog entries in full (an entry with its
enclosing section heading, an issue with its comments). They carry the traps, the competing
hypotheses, and the specific thing to look for; going to the footage without them produces a
description instead of an answer.

A missing `CAP-nn` means the item closed and its ID retired (IDs are never reused), and a closed
issue is closed — say so rather than guessing at a neighbour. A bare number tries `playtest.md`
first and the tracker second; a phrase means **ask which**, never guess. Search the tracker with
`gh issue list --state open --label capture --search "<phrase>" --json number,title`.

## 2. Find the media — the naming is not regular

Search `OriginalScreenshots/` and `OriginalScreenshots/Videos/`, plus `playtest/<ID>/` for anything
already staged (an issue's folder is `playtest/issue-N/`, since `#` is not a path character; a
capture filed as an issue may also carry its media as attachments in the thread, which
`gh issue view N --comments` prints as URLs). Four traps, all of them live in the current file set:

- **The separator varies.** `CAP-14 Graze and CAP 15 wing to red.mp4` writes one ID hyphenated and
  the other spaced. Match `CAP[-_ ]?0*<n>` case-insensitively, not `CAP-<n>`.
- **One file can carry several captures.** That same clip is the whole of `CAP-15`. A file named for
  a neighbour is not evidence of absence — grep the *other* IDs too before reporting one missing.
- **Multiple takes are normal and are not ranked.** `CAP-05` has four files (two knife-edge, two
  stalls). Analyse all of them; if they disagree, that disagreement is the finding.
- **Screenshots are named by subject, not by ID.** No image in `OriginalScreenshots/` carries a
  `CAP` number today — they read `C1 IA1 Cloud Puffs and Moon.png`. So also search by the row's
  subject words (`cloud`, `flare`, `spawn`), and by chapter/mission prefix.

List what you found with durations and resolutions before analysing, so the user can correct the
set early. Say plainly if nothing matches.

⚠ Git-ignored media is absent from your worktree by design. Read it by absolute path
(`Z:\CSVM\OriginalScreenshots\...`), and **never** create a junction or symlink into the main
checkout — CLAUDE.md forbids it, and a stray link turns a later recursive delete into a deletion of
irreplaceable original-game footage.

## 3. Watch it

`ffmpeg` is not on `PATH`; the bundled binary is:

```
FF=$(python -c "import imageio_ffmpeg;print(imageio_ffmpeg.get_ffmpeg_exe())")
```

⚠ **Crop the pillarbox before anything else on a 32:9 clip**, or half the width of every tile and
still is black. `ffprobe` the size first: **2560×720** needs `crop=1280:720:640:0`; **2560×1440** is
already full-frame and needs no crop.

⚠ **Images are the single largest cost of a capture session** — a measured run spent most of its
context on ~20 sheets and stills, many at 2304×2160 or larger, and context once spent cannot be
recovered. Every sheet must be justified by a question it alone can answer.

- **Contact sheet first**, to find the interesting second:
  `"$FF" -i "<clip>" -vf "crop=1280:720:640:0,fps=1,scale=420:-1,tile=5x3" -frames:v 1 sheet.png`
  (drop the `crop` for a 16:9 clip; raise `fps` and the tile count for a short, fast event).
  Scale it down and keep the tile count low — a sheet you can read at 420 px wide costs a fraction
  of a full-res one and answers the same "which second" question.
- **Stills at a moment**, high quality: `"$FF" -ss <t> -i "<clip>" -frames:v 1 -q:v 2 out.png`
  (put `-ss` *before* `-i` for speed, after it for exact-frame accuracy).
- **Audio**: `"$FF" -i "<clip>" -vn -ac 1 -ar 22050 out.wav`, then analyse the spectrum in numpy —
  for a pitch question, track the fundamental over time rather than describing the sound.
- **Read the stills you produce.** Actually open them with Read; do not infer from the command
  succeeding.
- **Ask the user for a timestamp before surveying.** They flew it and know where the event is; a
  34 s clip may contain one 2 s stall break.

Write intermediates to the scratchpad, not the repo. Anything worth keeping for the user goes in
`playtest/<ID>/`, which is git-ignored and **not** swept, so it survives until the item closes.

For an A/B against our build, launch it with `./RunGame.ps1 --plane=… --chapter=…` (flags in
`docs/cli.md`) and frame the shot the same way — a comparison at a different angle or altitude is
not a comparison.

## 4. Ask, rather than guess — this is expected, not a failure

Batch questions with `AskUserQuestion` and keep working on the parts that don't depend on the
answer. Ask when:

- **You need a timestamp.** As above — ask "which second is the graze?" rather than sampling 34
  frames hoping.
- **The footage is ambiguous.** Two takes disagree, the gauge is unreadable at that scale, the
  effect is off-screen or behind the wing, the HUD element you need is occluded.
- **You cannot tell what you're looking at.** Which plane, which mission, which of several similar
  passes, whether a visual is the effect under test or an unrelated one.
- **The clip may not answer the question at all.** Say what is missing and what a re-record would
  need to include — that is more useful than a hedged verdict, and `playtest.md`'s row already
  states what must be in frame.

Offer your reading as the first option when you have one, and say what you'd conclude if they
haven't a preference. Do not stack up questions you could answer by looking harder.

## 5. Record the finding

Write to the entry that owns the question, in its own voice. For a `backlog.md` entry that is an
edit to its text; for an issue it is a comment (`gh issue comment N --body-file <file>`), never a
silent body edit, since the thread is the record:

- **Answers a `BL-NNN` or `#N`** → update the entry with what the footage shows and where, then
  offer `/close-backlog-item` rather than closing it here.
- **Refines it** → edit or comment so the next reader gets the sharper question, and note which
  hypothesis the footage rules out.
- **Settles nothing** → say so on the entry and on the capture (`playtest.md`'s row, or a comment
  on the capture issue), with what a usable re-record needs. Do not silently leave the capture
  looking discharged.
- **Discharges the capture** → a `CAP-nn` row goes, or a `capture` issue is closed
  (`gh issue close N --comment "..."` naming what it settled), only when every item in its
  Unblocks column is served. If it still owes another item, leave it and note what's done. A new
  capture is filed as a `capture` issue (`gh issue create --label capture --body-file <file>`),
  never as a new `CAP-nn`; `New-ItemId.ps1` only guards the ids that predate the tracker.
- Findings that change what we believe about the original are recorded in the closing commit's
  message, per `/close-backlog-item` §4.

⚠ **A capture is evidence about appearance, not proof of a constant.** Record the honest limit: one
take is not a distribution, a plateau is worth more than a transient, and what a single frame shows
carries that frame's noise. A consistency check that is fed the value it is meant to test will
confirm it — re-read `docs/verification.md`'s METHOD section before declaring a match.

## 6. Report

Before reporting, re-read the capture's `CAP-nn` row in `playtest.md` §0, or the capture issue's
thread, and check it against what you actually found. If it still carries prose describing the
shot as owed, unanalysed, or open — and Section 5 discharged it or moved every open question onto
the backlog items — that prose is stale and must be updated (a row edit, or a comment on the
issue) so the capture doesn't contradict the backlog it points to. Do not silently leave a
discharged capture reading as if it is still needed.

Give the user: which files were analysed, the finding per `BL-NNN` or `#N` in plain prose with
its evidence cited by timestamp or frame, what remains open, and the files touched or comments
posted. Quote real observations and real command output — never a summary of what a run "should"
produce.

Then stop. **Do not commit** unless asked; offer it in one line. If you entered a worktree, leave it
in place (`keep`) — mention its path and branch so the user can review and merge it.
