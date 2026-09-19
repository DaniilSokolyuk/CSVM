# Public release: from the friend zip to a tagged GitHub release

**ACTIVE PLAN** (written 2026-09-06). It sits in `docs/`, which by this repo's convention makes it
a live plan; PROJECT_CONTEXT.md's "Current status" names it. When every item lands, the closing
commit deletes this file, records the completion in its message, and clears the "Current status"
pointer; any live prose linking this file by path is unlinked in the same commit.

This plan covers the distance between the package `ExportRelease.ps1` builds today, which was
written for a hand-off to someone the author can talk to, and a zip a stranger downloads from a
GitHub release page, announced in a few communities. It delivers the one blocking fix, the version
identity a bug report can name, the notices the binary is obliged to carry, a first run that does
not require a terminal, a stated renderer floor, a public repository whose front page matches its
own tree, the GitHub surface that receives strangers, a repeatable publish path, and the
clean-machine test that proves all of it before visibility flips.

Scope is release engineering plus exactly one game fix. Only bugs meeting the bar in Decision 3
block the release, and A1's triage is what applies that bar; A1 found one, `BL-694`, which is A2,
and everything else it looked at is in the Known Issues appendix at the foot of this file. Code
signing is out, see the boundary line. The plan assumes the repository is still private until E42, and that the
author writes or approves every word that faces the public, since outward communication is theirs.

## Milestone goal

- No mission a player starts can fail to end.
- A downloaded build states its own version, in the exe properties, in its log header, and in the
  filename of the zip it came from, so a bug report identifies a build without asking.
- The zip carries every notice its contents oblige it to carry, and the release names the commits
  both binaries in it were built from.
- Someone who owns Crimson Skies and has never opened a terminal can extract their assets and fly,
  and someone who skips that step is told so on screen rather than in a log file.
- A machine below the renderer floor is told before the download, and what it actually does has
  been observed rather than assumed.
- Every public-facing claim in the repository is true of the tree it ships with.
- A stranger's report arrives with the version and the log already in it, in a repository whose
  contribution and support policy is written down.
- One command produces the versioned zip, its checksum, the tag and the release that carries them.

**The first public release is not code-signed.** Signing costs either an ongoing Azure Trusted
Signing subscription or an OV/EV certificate, and it buys nothing the SmartScreen paragraph in the
README cannot explain. The decision is revisited only if the false-positive rate measured after
release justifies it, which is a fact this plan cannot have in advance.

## Decisions (2026-09-06)

| # | Question | Decision |
|---|---|---|
| 1 | Who is this release for? | **Public, with a limited announcement in a few communities.** A real but absorbable wave, so extraction, the renderer floor and the known-issues list all have to hold, and the support surface is sized for one person. |
| 2 | Does a campaign-blocking bug ship documented? | **No, `BL-694` is fixed first.** A mission that cannot end is the one defect a stranger reads as "this does not work", however good the workaround. |
| 3 | What makes a bug release-blocking? | **Unfinishable or lost progress.** It blocks if it makes a mission impossible to finish, loses or corrupts a profile or save, or stops the build launching on hardware the README claims to support. Everything else is a Known Issue, however ugly. |
| 4 | What version, and is it a GitHub pre-release? | **`v0.1.0`, pre-release flag off.** The number carries the "early" signal and leaves the 0.x range for ongoing polish; the flag stays off because it hides the build from the repo's Latest badge, and this is the build a visitor must land on. |
| 5 | How far does first-run help go? | **A double-clickable extraction wrapper AND an on-screen no-game-data screen.** They cover the two failures a stranger actually hits. The engine still does no extracting itself. |
| 6 | What is owed to a machine below the renderer floor? | **A test and a sentence, not a rendering path.** The floor is observed in a vGPU-disabled sandbox and stated in the README; no Compatibility renderer is built for `v0.1.0`. |
| 7 | Where do public reports live? | **GitHub Issues, on, triaged in the issue.** `backlog.md` stays internal and is not mirrored, so a public report is worked from GitHub and never enters the plan/id machinery. |
| 8 | Are pull requests accepted? | **Small self-contained ones.** Packaging, extraction, documentation and typo fixes are welcome; anything under `CSVM/src` needs an issue first, because a contributor cannot run this repo's gates. |
| 9 | What does the repo say about method? | **The truth, and `docs/org/` ships.** The root README's "No executable decompilation was performed" is false and is replaced by what `docs/formats/README.md` already says. No game code or assets are reproduced or redistributed. |
| 10 | Does the plan write the announcement? | **It drafts it; the author places it.** The framing is settled with the release rather than typed in a hurry afterwards. |

## Ground rules

- **Original-game data drives everything.** Read the reader/compiled JSON before writing a handler;
  never guess a value. Inventing content is the trap this project falls into most often.
- **Evidence is a lead to verify, not a finding to implement.** Confirm every claim against the
  data/code before building on it; **a correct disproof that lands no code is a success here**, not a
  failure. Mark each item's Evidence with its confidence (traced-to-code / direction-sound-magnitude-
  TUNE / lead-only).
- **`PROJECT_CONTEXT.md` + the module's entry in `docs/architecture/<Namespace>.md` (plus its index
  bullet in `docs/architecture.md`) / `docs/formats/` are updated in the same turn** as each landed
  item; a landed item gets its record in the landing commit's message and is **deleted** from
  `backlog.md` (not marked FIXED there). New decodes land with their `docs/formats/` page.
- **Read `docs/verification.md` before measuring anything**, the instruments here mislead; cite the
  rule that bites per item.
- **Verify against a full 8-chapter `--freecam --chapter=<X>` regression** (zero errors, same
  mesh/node counts unless the change is meant to add coverage) plus a targeted capture at the
  location the report came from.
- **Read the module's entry in `docs/architecture/<Namespace>.md` (found through the index in
  `docs/architecture.md`) before modifying it,** then the comments on the members you touch; dead
  ends are in the landing commits (`git log --grep=<ID>`), so search those before re-chasing one.

## Checklist

Statuses: ☐ open · ◐ in progress · ☑ done · ❌ closed/disproven. **Keep this in sync as items land.**

### Wave A, What must be true before strangers play

1. ☑ Triage `backlog.md` against the release bar
2. ☑ `BL-694` CM14 becomes unwinnable when the Gemini dies before its cannon bays

### Wave B, What the package says about itself

11. ☑ A version the build states in its exe, its log and its filename
12. ☑ The third-party notices the binary is obliged to carry
13. ☑ An exported build writes `logs\`, and the README says where saves live

### Wave C, The first run on someone else's machine

21. ☑ Extraction without a terminal, and a screen for the player who skipped it
22. ☑ The renderer floor, observed rather than assumed

### Wave D, What the public reads

31. ☑ Pre-flip audit: the method claim, and the tracked files a stranger reads
32. ☑ `packaging/README.md` rewritten for a reader nobody knows
33. ☑ `.github/`: the bug form and the policies
34. ☑ `PublishRelease.ps1`: tag, versioned zip, checksum, release

### Wave E, The flip

41. ☐ A clean-machine run of the downloaded zip, above and below the floor
42. ☐ Release notes, the first tag, the flip, and an announcement draft
43. ☑ `README.md`: the play path, the status, and the developer split

## Dependency and parallelism notes

A1 runs first and alone: it can add items to this plan, so nothing downstream should be declared
finished before it has run. A2 is whatever A1 confirms plus `BL-694`, and E41 cannot certify a build
that does not contain it.

B11 blocks D34 and E42, which both name the version. B13, C21 and C22 all block D32, because the
README documents the log path, the extraction command and the requirements; write it once, after
they settle. C22 is a test before it is prose, and E41 reuses the rig it builds. D31 blocks E42 by
policy rather than by file: the flip may not happen while the front page contradicts the tree. D33
and D34 depend on nothing in A, B or C and can run in parallel with them. E41 needs everything
landed; E42 needs E41 green.

File contention: B11 and B12 both edit `ExportRelease.ps1` and `packaging/MANIFEST.md`; C21 and D32
both add or edit files under `packaging/`; D31 and D32 both edit README prose in different files but
must agree on one wording of the method paragraph. Do not run any of those pairs in parallel
worktrees.

E43 blocks E42 the way D31 does, by policy: the flip may not happen while the front page misdescribes
the build a visitor is about to download. It also lands before E41, which "everything landed" already
implies, because E41's tester starts at the front page. It shares `README.md` with D31 and D32's
method wording, so it inherits their sentence rather than writing a second one.

---

# Wave A, What must be true before strangers play

## A1 ☑ Triage `backlog.md` against the release bar

**Goal.** A written split of the open backlog into two lists: what blocks `v0.1.0` by Decision 3's
bar, and what goes into the release notes as a Known Issue. Every item on either list has been
confirmed still open.

**Evidence (confidence: traced for the counts, lead-only for the contents).** `backlog.md` holds 132
`BL-` bullets, 34 tagged `[Impact: high]`. The tag was assigned for internal fidelity work and does
not mean what Decision 3 means, so it is a starting filter and nothing more. **Nothing in the list
was re-verified in this session**, and a backlog entry is not proof the work is undone.

**Approach.** Filter to candidates for the bar (a mission that cannot end, a profile or save lost or
corrupted, a launch that fails on hardware the README will claim), then re-verify each candidate
against both `git log --grep=<ID>` and the code before it goes on either list. Write the result into
this plan: blockers become A-wave items, the rest become the Known Issues draft E42 uses, each with
its player-visible symptom and workaround in one sentence, not its internal decode.

**Model recommendation.** high. It is a judgement about what a stranger will forgive, applied to
items written in the project's own internal shorthand.

**Verify.** Every listed item names the evidence that it is still open (a `git log --grep` miss plus
the code path that still has the defect). Spot-check two Known Issues at the controls: the symptom
is what the sentence says it is.

**⚠ Traps.** ⚠ A fixed bug in the release notes is worse than an unlisted one, so re-verification is
the work here and the filtering is the easy part. Resist widening the bar item by item, which is how
this becomes a polish plan; the bar is Decision 3 and it is written down for that reason.

**Verified.** `backlog.md` holds 155 open `BL-` bullets, not the 132 this plan's scope paragraph
claimed, 38 of them tagged `[Impact: high]`, 9 `[Blocked: …]` and 19 `[Owed-playtest]`. Every
`[Impact: high]` entry was read, plus every entry scoped to a mission, plus every entry a keyword
sweep for unfinishable, corrupt, profile, save, launch and crash reached. That produced twenty
candidates for Decision 3's bar, and each was re-verified against both `git log --all --grep=<ID>`
and the code before any list took it.

**No new blocker survived the bar. `BL-694`, which is A2, is the only open entry that meets it**,
so this item adds no A-wave item and moves no entry out of `backlog.md`. `BL-694` itself is
confirmed still open: its `git log --grep` returns the filing commit `3af41b84`, `PT-103`'s
retirement and this plan's own opening commit, with no landing among them, and its entry is already
out of `backlog.md`, which now carries only a cross-reference to it from `BL-698`.

The candidates that came closest, and why each failed the bar rather than being talked past
it. `BL-717`, the Pandora's own turrets firing on the Balmoral CM02 wants captured, reads like it
could destroy the only route to primary 3 (`OBJECTIVE18`, `ANIM_STATE wingwalk EXECUTED`), but the
sitting that filed it walked CM02 through to CM24 (`git log --grep=BL-717`, commit `5b71d0ad`), so
the mission was finished with the defect present. `BL-689`, CM13's
flight check, withholds a plane grant and offers a button the original bars, which leaves the
mission flyable in the aeroplane the profile already owns. `BL-974` can leave a
survivor an objective is waiting on kilometres outside the mission area, which is a long flight
rather than an impossible one. Each is in the Known Issues appendix instead.

Three entries failed re-verification, which is the part of this item that earned its keep.
`BL-079`'s opening claim, that all sound is own-plane and non-positional, is false: every AI
aircraft carries `AiEngineAudio` on `AudioStreamPlayer3D` with its own distance cull, wired at
`AiFlightAssembler.cs:210`, and `WorldSounds` positions world emitters the same way. Its
`git log --grep` shows no landing because the work landed under a plan item that never named the
id, which is why a code read is required alongside the log and not instead of it. `BL-545`'s body
still describes CM02's hookless landing as present behaviour, where `d5caa7cc` says "Closes
BL-545"; what the entry actually holds is an owed look, which its `[Owed-playtest]` tag already
says and its prose contradicts. The chase look-around entry claimed the
view had none at all, where `CameraController.PadLook` gave it the right stick; the decoded
head-look controller and the numpad scheme were what was missing, so the entry overstated and
`BL-150` rode the same claim. The first two are corrected in `backlog.md` by this item; the camera
pair was left alone, because narrowing them wanted the camera read they are about, and both have
since been settled by the port of the head-look controller onto the chase camera.

One new defect was found and filed as `BL-770`: an exported build launched by double-clicking
`CSVM.exe` is silent, because `Launcher.MasterVolumeDefault` is `0` and nothing in the payload
supplies `--volume=` or an `audio.volume` config key. It does not meet Decision 3's bar, so it is
not an A-wave item, and it belongs to the first run on someone else's machine, so C21 names it.

The Known Issues draft E42 uses is the appendix at the foot of this file.

## A2 ☑ `BL-694` CM14 becomes unwinnable when the Gemini dies before its cannon bays

**Goal.** CM14 always reaches an end. A player who kills the Gemini early either still completes the
third primary or loses the mission, but never flies on with nothing left that can happen.

**Evidence (confidence: traced, and reproduced twice at the controls with the two outcomes side by
side).** The mission's third primary is the only route to a win. `OBJECTIVE13` is
`IDENTITY [PRIMARY, 3, MSG_BRF_HWM4_OBJ3]` gated on `ANIM_STATE COMPLETION_COUNT 5` over the six
`deploy_gmzep_lbroad11..32` in `INVALID` (`extracted/C2B/M04/zrdr/objectives.zrd.json:755`);
completing it naps `OBJECTIVE33`, which wakes `OBJECTIVE14`, which naps `OBJECTIVE32` `INSTANTWIN`.
Nothing else wakes any of the three. Failing it loses nothing either, since `OBJECTIVE22`
`INSTANTLOSS` is woken only by `OBJECTIVE21` (three of the player's own `piratezep` gasbags gone),
so the mission simply never ends. Only `destroy_gmzep_lbroadNN-gunback` (activation `WeaponHit`,
health 60) writes those `INVALID` states, and `killgmzep` invalidates only
`gmzep_rocksleft`/`gmzep_rocksright`, so no zeppelin death credits the objective. The gasbag kill is
reachable independently: `geminizep` authors five healthy zones with `num_healthy_required 3`, so
three torpedoes kill it with every cannon untouched.

The two logged runs differ only in order. **Won:** `lbroad21` destroyed then `objective 11
completed` one line later, `lbroad31` destroyed then `objective 12 completed`, the Gemini killed,
and the last bay then shot on a floating wreck the author put "a meter above the water level".
**Blocked:** no cannon destroyed at all, gasbags 2, 3 and 1 killed, `geminizep DESTROYED — survivors
2 < required 3`, `objective 11` woken at 72.1 s and never completed, and no mission end of any kind
in the rest of the log. The author's own account: "the wreck sank under water blocking the mission
because only the cannons count", and on whether rounds reach a submerged gun, "stops at surface",
which `Projectile.SurfaceIsWater` confirms, water being a real collider. The reachability half is
confirmed at the controls on the breakup itself (the retired `PT-103`, `git log --grep=PT-103`): the
wreck's three middle sections rest on the sea at different depths, and at least one bay sits under
the water where no round reaches it. The pause board's Restart and Exit (`docs/controls.md`) are the
only escape today, which costs the sortie, not the campaign.

**Approach.** The fix shape is the author's decision: the bays stay where they are on the hull and
are DESTROYED when the gasbags explode; they do not ride along with the falling bags. That puts the
`INVALID` state on the `deploy_gmzep_lbroadNN` defs the same way a `WeaponHit` on
`destroy_gmzep_lbroadNN-gunback` does, so the credit for primary 3 arrives through the bays' own
destruction. Raising the wreck's rest height is not the fix: the rest is the contact tier parking
the body's ORIGIN on what it lands on, which is decoded behaviour
([`docs/org/objectMotion.md`](org/objectMotion.md):40, `FUN_004cf200` hands the column query the
flying node's origin, with no bounding-box term). The item is `[L]` and `[Next: code]`, so budget a
session, and keep the neighbouring wreck-rest items (`BL-698`, `BL-668`) as separate changes even
where the code is adjacent.

*Open question for whoever takes this:* in the winning run `objective 13` completed with only TWO
bays destroyed. Two destroys invalidate four `deploy_*` defs and the objective is authored to need
five, so something else supplied the fifth. Find it before changing any counting.

**Model recommendation.** high. Plan-sized, in the objective machinery, on the only route to a win in
a shipped mission.

**Verify.** Fly CM14 both ways: kill the Gemini first, then the bays, and the reverse. Both runs
reach an end state, and the winning run still credits primary 3 through the bays. Then the campaign
regression and the full `.\RunTests.ps1`, since this lands under `CSVM/`.

**⚠ Traps.** ⚠ **Do not credit primary 3 from the gasbag count itself.** `MSG_BRF_HWM4_OBJ3` reads
"Destroy the GEMINI by shooting the open cannon hatches", and `killgmzep` invalidates only the
rocks, so a direct gasbag-to-objective credit would invent a win condition; the credit goes through
the bays being destroyed, which is a destructible-state change and not an objective edit. `CAP-55`
(d) and (e) are what show whether the original's own breakup takes the bays with it. ⚠ **`killgmzep`
had never run under test:** `CSVM/src/Testing/ZeppelinBreakupSuites.cs:11,22` pins C1/M04 and
`piratezep` only, asserts six bags where the Gemini has five, and would fail its 12-of-12 engine
check on a 14-engine hull, so a green suite is no evidence about this ship. There is also no
recovery for a player already stuck: `--debug-objective=N` drives `INACTIVEn` conditions only and
cannot satisfy an `ANIM_STATE` objective, and the campaign's four-attempt skip offer needs four
RECORDED failures, which an unwinnable-and-unlosable mission never produces. Do not clamp bags to
`y = 0`; `BL-668` tuned no constant and this should not either. *Cross-refs:*
`BL-698`, `BL-639`, `BL-640`, `BL-668`, `CAP-55`.

**Verified.** The open question first, because it is the whole item. The fifth and sixth
invalidations do not come from the gasbag count and they do not come from a direct credit: they
come from the ship's own demolition chain, which the evidence paragraph above had only half of.
A gasbag's record entry carries a destruction anim, and that anim demolishes the section it belongs
to. `gmzep_gasbagtorpedo1` invalidates its section's two burn anims and, half a second later,
calls the destroy definitions of `lbroad11`, `lbroad12`, `rbroad11`, `rbroad12` and four engines
(`extracted/C2B/M04/mis_anim/gasbag1-gmzep_gasbagtorpedo1.json`). The bays reach the same
demolition from the other side: each bay's death calls its section's left burn at +1 s and its
right burn at +8 s, and `gmzepleft_gasbag1` destroys the section's other bays and engines before
calling `finish_gmzepgasbag1`. The hull death closes the set: `all_gmzep_gasbags` calls both burns
of every section, the sections that already died have invalidated their own, and the rest go. So
`killgmzep` never had to credit anything, and neither did the gasbag count.

**The code that carries that chain landed on 2026-09-05 in `4648f333`, two days after `BL-694` was
filed and after the sorties it was filed on.** That commit closed `BL-738` on C5/M04's Dante and
introduced `KillCalledDestructible`, which routes a `CALL_ANIMATION` naming a destructible's own
death definition through that definition's pool. Before it, such a call started the definition on
the caller's anchor and ran its listed sequences alone; the four `deploy_*`/`retract_*`
invalidations live in the compiled destruction slot, which only a pool death dispatches
(`AnimRuntime.RunDeathSlot`, reached from `RunDeathSequence` alone), so no gasbag death could write
an objective-visible state and the blocked sortie is exactly what that build had to produce. The
commit never names `BL-694`, which is why A1's `git log --grep` and code read both missed it, the
same shape A1 recorded for `BL-079`. It also follows that the winning sortie's fifth invalidation
came from the third bay its own account says was shot on the wreck, not from anything else: on that
build a bay's own `WeaponHit` was the only writer of a `deploy_*` INVALID.

**Measured, not inferred.** `gemini-gasbag-bays`
(`CSVM/src/Testing/GeminiGasbagBaySuites.cs`) kills C2B/M04's Gemini through the damage sink a
torpedo reaches and reads the state OBJECTIVE13 reads, over three orderings on three worlds.
Gasbags 1, 2 and 3, the tidy case. Gasbags 3, 4 and 5, which is the case that matters: the Gemini
carries bays on sections 1 to 3 only, and `gmzep_gasbagtorpedo4`/`…5` name `lbroad41`…`lbroad52`
that this hull does not have, so four of the six bays can only arrive from the hull death. Then the
reverse order, a deployed `lbroad21` shot off a still-flying hull before the same three gasbags.
All three end with six of six bays Destroyed, six of six `deploy_gmzep_lbroadNN` INVALID, and
OBJECTIVE13's authored `COMPLETION_COUNT 5` met at 6, the chain settling at 1.0 s, 9.0 s and 3.0 s
of its 60 s cap. The states run ahead of the pools, which is why the suite's own settle test reads
both: a bay's destruction slot latches BOTH deploys of its pair, so three deaths cover all six
states while three sister pools are still waiting on the +1 s and +8 s burns, and a settle test on
the states alone stopped the clock with three pools still healthy. The suite also pins the hull death chain
itself, which no suite reached before: `all_gmzep_gasbags` and `killgmzep` both run, so the trap
about `killgmzep` never running under test is answered on the five-bag ship rather than on
`piratezep`. Able to fail (METHOD-9): with `KillCalledDestructible` made to return null, every leg
reports 0 bays destroyed and OBJECTIVE13 at 0 of 5, reproducing the blocked sortie's "no cannon
destroyed at all"; `git status` is clean of that perturbation.

**So this item lands a regression suite and no engine change**, which is the outcome the ground
rules call a success. The release bar is met: no ordering of the Gemini's death leaves CM14 without
a route to its third primary, because every route to the hull's death demolishes all three sections
that carry bays. The 8-chapter `--freecam` regression exits 0 in every chapter with no error line,
and the complete `.\RunTests.ps1` is green: 3,450 units, 257 engine suites with engine errors
clean, 18 goldens hash-identical, exit 0. The one thing a headless run
cannot sign off is the sortie itself, so the two flights this item's Verify names are `PT-119`, and
`CAP-55` still owes the original's own answer to (b) and (d) there. `BL-698`'s trap is corrected in
`backlog.md`: it was waiting on this open question.

# Wave B, What the package says about itself

## B11 ☑ A version the build states in its exe, its log and its filename

**Goal.** A recipient can say which build they are running without being asked how they got it. The
version appears in the exe's Windows file properties, in the first line of every log file, and in
the name of the zip that carried it, and all three agree with the git tag.

**Evidence (confidence: traced).** `CSVM/project.godot` carries `config/name="CSVM"` and no
`config/version` key. `CSVM/export_presets.cfg` sets no `application/product_version`,
`application/file_version` or `application/product_name`, so the exported exe inherits Godot's own
template metadata. `git tag` lists only `analysis-archive` and `docs-archive`. `ExportRelease.ps1`
writes a fixed `.scratch\CSVM.zip`.

**Approach.** `v0.1.0` per Decision 4. Add `config/version` to `project.godot` as the single source,
read it at startup and emit it as the log's first line through `Log`, and have `ExportRelease.ps1`
read it back to name the zip `CSVM-v<version>-win64.zip`.

The preset needs no second copy of the number, which is what the two open questions settled into.
The Windows export options are `application/modify_resources`, `application/file_version`,
`application/product_version`, `application/product_name` and `application/file_description` (read
off the 4.7 editor binary's own option table); the two version keys are documented "leave empty to
use project version" and do exactly that, padding `0.1.0` to `0.1.0.0` in both PE fields, so only
`modify_resources` has to be turned on and the number stays in `project.godot` alone. The author's
call on the launchscreen was yes, and it is drawn over every presentation rather than inside one:
`UI/BuildStamp.cs`, a launcher-owned corner label shown while the menu host is up.

**Model recommendation.** medium. Mechanical once the key names are confirmed, but it touches the
export path, where a silent mistake ships.

**Verify.** Run `ExportRelease.ps1`, then check three surfaces: the zip filename carries the version,
`Get-Item .scratch\export\CSVM.exe | Select-Object VersionInfo` shows it, and the first line of a log
from the exported build states it. Bump the version once, re-export, confirm all three moved.

**⚠ Traps.** ⚠ `ExportRelease.ps1` snapshots and restores `project.godot` byte-for-byte around the
export, because a real editor rewrites it without its comments. Do not add a second writer to that
file, and do not stamp the version by editing the preset during an export run.

## B12 ☑ The third-party notices the binary is obliged to carry

**Goal.** The zip carries a notice for everything inside it, and the release can say which source
each shipped binary was built from.

**Evidence (confidence: traced for what is present, lead-only for what is required).**
`packaging/MANIFEST.md` lists exactly two licence files: `LICENSE` (GPL-3, the engine) and
`LICENSE-unzbd` (EUPL-1.2, the extractor). The binary also contains the Godot engine, whose MIT
licence requires its copyright notice to travel with it, and a self-contained .NET runtime publish;
neither has a notice in the payload. The extractor's source is available: `Laeresh/mech3ax` is a
public repository, and the local `cs-anim` is level with `origin/cs-anim` at `afc9a7f`, with a clean
tree.

**Approach.** `packaging/LICENSE-thirdparty.txt`, assembled by `packaging/BuildThirdPartyNotices.ps1`
from the shipped artefacts themselves, with its row in `packaging/MANIFEST.md` and its entry in
`$ReleaseFiles`. The two provenance facts go in a generated `BUILD-INFO.txt` at the zip root, which
D34 reads.

The item's open TODO is settled, and it settled against what the plan assumed. The Godot 4.7 mono
Windows distribution ships **no** copyright file: neither `LICENSE.txt` nor `COPYRIGHT.txt` is on
disk in `tools/godot/`, and `godot-4.7-mono-export-templates.tpz` holds only templates and a
`version.txt`. The engine keeps both texts inside the binary, so the script runs the pinned editor
headless over a throwaway `TEMP` project and reads them back through `Engine.get_license_text()`,
`get_copyright_info()` and `get_license_info()`, which is the same binary the templates were cut
from. The .NET self-contained publish requires the `LICENSE.TXT` and `THIRD-PARTY-NOTICES.TXT` that
ship in the `Microsoft.NETCore.App.Runtime.win-x64` runtime pack it copies into the payload; the
pack is read from the NuGet cache at the version `CSVM.runtimeconfig.json` names, not from the
machine-wide `dotnet` install, which is a different build.

A third obligation the plan's Evidence did not name is discharged in the same file: EUPL-1.2 covers
mech3ax's own code, not the Rust crates statically linked into `unzbd.exe`, whose MIT and Apache
terms want their own copyright notices carried.

**Model recommendation.** medium, low effort. Assembly and a manifest row, once the sources are
identified.

**Verify.** Export, then confirm the file is at the zip root, that `packaging/MANIFEST.md`'s table
and `$ReleaseFiles` agree, and that the recorded fork commit matches
`git -C tools/mech3ax rev-parse cs-anim` with `origin/cs-anim` level with it.

**⚠ Traps.** Payload files are copied from one repo home on every export, so this file belongs in
`packaging/`, never assembled inside the export folder. ⚠ Shipping a binary built from an unpushed
fork commit breaks the source correspondence, which is why the commit is recorded and checked rather
than assumed.

## B13 ☑ An exported build writes `logs\`, and the README says where saves live

**Goal.** A recipient finds their log without being told about a hidden developer folder, and can
answer "where are my settings and profiles" from the README.

**Evidence (confidence: traced).** `CSVM/src/Utils/Log.cs:136` builds the log directory as
`Path.Combine(repoRoot, ".scratch", "logs")`, and `docs/tooling.md` states the exported build
resolves every root to the exe's own folder, so a recipient's logs land in `.scratch\logs` beside
`CSVM.exe`. Per-user state is separate and already correct: `BindingStore`, `OptionsStore`,
`CampaignProfileStore`, `ScoreStore` and `CustomPlaneStore` all write through `user://`.

**Approach.** Give the exported build a plain `logs\` directory while leaving the development tree's
`.scratch/logs/` untouched, since the whole verification toolchain reads that path. The seam is
`Log`'s directory resolution plus whatever already distinguishes an exported run from a repo run;
prefer one switch there over a second log path threaded through callers. D32 then states both
locations, including that uninstalling means deleting the unzipped folder and the `user://`
directory.

**Model recommendation.** medium. Small change, but the wrong seam splits the log path in two.

**Verify.** An exported build run from a bare folder writes `logs\<mode>-<stamp>.log`; a repo run of
`RunTests.ps1` still writes `.scratch/logs/` and every suite that reads it still passes. Confirm the
`user://` directory resolves to `%APPDATA%\Godot\app_userdata\CSVM` on the export.

**⚠ Traps.** ⚠ Do not point the development tree at `logs/`: `.gitignore` covers `.scratch/`, and a
new top-level `logs/` would be committed. What the exported build writes must not change what a repo
run writes.

# Wave C, The first run on someone else's machine

## C21 ☑ Extraction without a terminal, and a screen for the player who skipped it

**Goal.** A recipient extracts their assets by double-clicking one file and pointing it at their
install, and a recipient who skips that step is told so on screen, in a sentence they can act on.

**Evidence (confidence: traced).** `packaging/Extract.ps1` requires
`powershell -ExecutionPolicy Bypass -File Extract.ps1 "<install folder>"`, and its own error paths
show which mistakes were already anticipated: a missing argument, a trailing backslash that swallows
the closing quote, a path that is not the folder holding `ZBD` and `GOSDATA`. The skipped-extraction
case is worse than any of them: `Launcher` calls `ExtractionStamp.Check`, which by its own contract
logs "AT MOST one warning line, never a block" (`src/Session/ExtractionStamp.cs:42-50`), so a player
who double-clicks `CSVM.exe` first gets an explanation in a file they will never open.

**Approach.** Two pieces, per Decision 5. `packaging/Extract.cmd`, double-clickable, invoking the
existing dispatcher with the bypass switch applied, probing the known install locations and falling
back to a folder picker so the path is never typed; added to `packaging/MANIFEST.md` and
`$ReleaseFiles`. Then a launcher-side screen when the data root holds no `extracted/`, naming
`Extract.cmd` and staying up rather than proceeding into a world it cannot build. All extraction
logic stays in `ExtractAssets.ps1` and `ExtractRof.ps1`. The probe order the author settled:
`Microsoft Games\Crimson Skies` under either Program Files, then a `Games\` or bare
`Crimson Skies\` folder on every fixed drive.

Third piece, from A1: `BL-770`, the silent first run. `Launcher.MasterVolumeDefault` is `0` so that
a scripted or agent run never sounds by accident, the run scripts pass `--volume=1.0`, and the
export payload carries neither a flag nor a `config.json`, so a recipient who double-clicks
`CSVM.exe` hears nothing and has no reason to suspect a flag exists. Give the exported build an
audible default without changing what a repo run gets, the way B13 separates the two log paths.

**Model recommendation.** medium. Mechanical, with the failure modes belonging to a stranger, so the
error text matters more than the code.

**Verify.** In E41's clean machine: double-click `Extract.cmd` with the game at a non-default path,
and again with no game installed; the first completes, the second says what is wrong in one sentence
and does not vanish before it can be read. Launch `CSVM.exe` with no `extracted/` at all and read
what the screen says.

**⚠ Traps.** A `.cmd` that finishes and closes instantly is indistinguishable from one that crashed.
Do not fork a package variant of the extractors, which is the rule `packaging/MANIFEST.md` states
about `ExtractAssets.ps1` and `ExtractRof.ps1`. ⚠ The no-data screen must not become a second
provenance check: `ExtractionStamp` already owns the stale-tree warning and stays a warning.

## C22 ☑ The renderer floor, observed rather than assumed

**Goal.** The requirements state what hardware runs this build, and the sentence they state is one
somebody watched happen.

**Evidence (confidence: lead-only).** `CSVM/project.godot` sets no rendering method, so the project
takes Godot's `forward_plus` default, and `ExportRelease.ps1` exports with
`--rendering-driver vulkan --rendering-method forward_plus` because the shader bake has to run on
the renderer the target uses. What a machine without usable Vulkan does with that build has not been
observed. The author has no below-floor hardware, but Windows Sandbox with `<vGpu>Disable</vGpu>`
has no Vulkan, and E41 already builds a sandbox rig, so the below-floor machine is a second `.wsb`.

**Approach.** Build that second config, run the exported build in it, and record exactly what
happens, including the text of any error dialog. Write the requirement and that observed behaviour
into D32's README. If the shipped templates already fall back to another driver without help,
document the flag that reaches it as a troubleshooting line. Build no Compatibility renderer
(Decision 6).

**Model recommendation.** high. It decides who the build excludes, and the cheap answer and the
expensive answer differ by a rendering path.

**Verify.** The below-floor run's observed behaviour matches what the README will predict, word for
word. The above-floor sandbox run is unaffected, and the development tree's golden sweep is
unchanged, since this item should change no shipped pixel.

**⚠ Traps.** ⚠ A Compatibility fallback is not a preset switch: `ExportRelease.ps1` asserts the
shader bake ran, so a second renderer means a second bake or a knowingly unbaked path. If the
observed failure turns out to be illegible, that is a finding for the author, not a licence to build
the fallback inside this item.

**Verified.** The build does not refuse to start below the floor, so the sentence this item exists
to write is not the one it expected. On the below-floor machine Godot reports `Required Vulkan
instance extension VK_KHR_surface not found` and then `Your video card drivers seem not to support
Vulkan, switching to Direct3D 12`, and runs Forward+ on Direct3D 12's `Microsoft Basic Render
Driver`, the WARP software rasterizer. Nothing has to be passed to reach that fallback, so the
troubleshooting flag the Approach anticipated does not exist, and no error dialog appears at any
point: `appDialogs=0` across every run, and the desktop screenshots hold none. The menu and C21's
no-game-data screen render normally and hold the sandbox's 32 Hz presentation cap. A flight does
not. With the dev tree's `extracted/` mapped in read-only and `-- --data-root=<desktop> --fly
--chapter=C1 --plane=player_fury --no-vsync`, the chapter loads (37508 clutter instances placed),
then `buffer_create` fails with `0x8007000e`, out of memory on the software device, across 52670
stderr lines, and the process dies of an access violation (`0xC0000005`) fourteen seconds in,
leaving no window and no message. 8 GB and 16 GB of guest memory fail identically, so the constraint
is the device and not the VM. The same zip and the same flight command above the floor, where the
sandbox passes the host's RTX 5080 through and the build reports `driver=vulkan
method=forward_plus`, ran the full 79-second watch at 740 to 793 fps uncapped with zero stderr
lines, which is what attributes the failure to the renderer rather than to the command or the mapped
data root. So the floor D32 states is a GPU with a working Vulkan or Direct3D 12 driver, and what a
machine below it gives a player is menus that work followed by a mission that vanishes. No
Compatibility renderer was built (Decision 6). The golden sweep is unchanged and nothing under
`CSVM/` was touched.

The rig E41 reuses is `RunSandbox.ps1` plus `sandbox/RendererFloor.ps1`, with `-Driver` for a
procedure of its own and `-MapReadOnly` for the retail install; `docs/tooling.md` carries what it
cost to make it honest, including that a `<VGpu>` spelling of the vGPU element is ignored silently,
which makes a below-floor run test a machine with the host's GPU passed through.

# Wave D, What the public reads

## D31 ☑ Pre-flip audit: the method claim, and the tracked files a stranger reads

**Goal.** Every public-facing claim in the repository is true of the tree that ships with it.

**Evidence (confidence: traced).** `README.md:74` states "**No executable decompilation was
performed.**" while `docs/org/` holds 34 pages of executable-derived decode, tabulating addresses
such as `FUN_004897c0`, and `docs/formats/README.md:10` already describes the practice accurately
and links `docs/org/` by name as the place for claims that come from the executable wholesale. The
rest of the tree is clean: no file with an asset-like extension was ever added on any ref, and the
only formerly-tracked, now-ignored file is `NOTES.md`. `Charter.md`, `CLAUDE.md`, `AGENTS.md`,
`CONTEXT.md`, `backlog.md` and `playtest.md` are all tracked and become public reading.

**Approach.** Replace the false sentence with the accurate one, phrased from
`docs/formats/README.md`'s existing wording so the two agree: mostly decoded from extracted data and
observed behaviour; where the retail executable settles a question the data cannot, static analysis
of it is cited at the point of use; no game code or assets are reproduced or redistributed; the
engine is an independent implementation. `docs/org/` ships (Decision 9). Then read the tracked
root documents once as a stranger would, and check what `NOTES.md`'s history exposes, fixing only
what is wrong or private rather than rewriting voice.

**Model recommendation.** high. It is the project's public legal framing, and the failure mode is a
sentence nobody reread.

**Verify.** Grep the tree for the old claim and any paraphrase of it; every hit is either corrected
or is a quotation that is still true. `git log --all --diff-filter=A --name-only` still shows no
asset-shaped file. A reader following `README.md` to `docs/formats/README.md` to `docs/org/` finds
one consistent account.

**⚠ Traps.** ⚠ Do not soften the correction into vagueness: a front page that says nothing about
method reads worse to the audience the paragraph exists for than one that says exactly what was
done. Removing `docs/org/` instead was considered and rejected; it would need a history rewrite and
would leave dangling citations across `backlog.md`, the plans and `docs/formats/`.

**Verified.** The false sentence is replaced in `README.md` by the account `docs/formats/README.md`
already gives: most of the reference decoded by inspecting extracted data and matching behaviour
against the original game, static analysis of `crimson.exe` cited at the point of use where the data
cannot settle a question, the findings that come from the executable wholesale named as living in
`docs/org/` with their function addresses so any claim can be re-checked at source, and no game code
or assets reproduced or redistributed. That is the wording D32 uses. **The source sentence was
itself understated, and the first draft of the correction inherited it.** `docs/formats/README.md`
said "A few pages additionally cite `crimson.exe`", and characterised the citation as a literal the
binary embeds. Counted: 35 of the 48 pages name a Ghidra symbol (`FUN_`/`DAT_`/`LAB_`, 626
occurrences) and 40 name a function, address or global in some form, leaving 8 that rest on
extracted data alone; and the citation is more often a traced routine than a literal, as in
`objectives.md`, where the parser `FUN_00466b70` and the tick `FUN_0046a490` carry every directive's
semantics. Both files now say most pages, with the count and both kinds of citation named. The
placement rule is unaffected: those pages decode a shipped data file and cite the executable as
evidence for what a field means, which is the point-of-use case, not the wholesale case that belongs
in `docs/org/`. Two paraphrases carried the
same false claim and are corrected with it: `PROJECT_CONTEXT.md`'s flight-model bullet said "No exe
decompilation" while `docs/org/flightModel.md` is the authority for every flight quantity, and its
documentation-routing bullet named only `docs/formats/` as the home for reverse-engineering
knowledge. "Code and format documentation only" in `README.md`, `PROJECT_CONTEXT.md` and
`.gitignore` becomes "code and documentation only", because `docs/org/` is neither format
documentation nor an asset; the no-assets rule those sentences exist for is unchanged and now
excludes reproduced game code and decompiler output explicitly. The reference's stale page count,
17, is now 48, the tracked count behind `docs/formats/README.md`. Grepping the tree for the sentence
and for "decompil"/"disassembl"/"clean room"/"black box" leaves this item's own two quotations of it
in this plan, the `docs/org/` and `docs/formats/` pages that state the same practice truthfully, and
internal notes about decompiler *output*, which is genuinely reproduced nowhere.
`git log --all --diff-filter=A --name-only` yields 1975 distinct paths ever added on any ref, whose
extensions are `cs`, `uid`, `md`, `py`, `json`, `ps1`, `txt`, `gdshaderinc`, `ahk`, `csv`, `csproj`,
`html`, `census`, `script`, `h`, `cfg`, `cmd`, `props`, `yaml`, `godot`, `sln`, `ts`, `tscn`,
`editorconfig`, `gitattributes`, `gitignore` and eleven extensionless files: no asset-shaped file, no
image and no archive has ever been tracked. The reader path holds. `README.md` now names both
directories and what separates them, `docs/formats/README.md` describes the same split and points at
`docs/org/` by name, and every `docs/org/` page opens with the executable it was read out of and the
statement that no decompiler output is reproduced. The tracked root documents read cleanly to a
stranger, and no tracked markdown contains a personal path, an address or a credential. `NOTES.md`,
the one formerly-tracked now-ignored file, ends its history as a 45-line idea list whose issue
section had already been moved to `backlog.md`; it exposes nothing private, and one of the two links
it carries is the Ghidra MCP server, which the corrected sentence now accounts for rather than
contradicts.

## D32 ☑ `packaging/README.md` rewritten for a reader nobody knows

**Goal.** The README in the zip reads correctly for someone who found the download on GitHub, and
answers the questions that would otherwise arrive as issues.

**Evidence (confidence: traced).** The current `packaging/README.md` addresses a known recipient in
its SmartScreen section: "You got this zip from us personally, and we are telling you to expect
exactly this warning; if you got it from anywhere else, don't run it." That inverts for a public
download. The file otherwise already covers requirements, the two setup commands, disk space, logs,
the legal position, the licences and the AI-assistance disclosure.

**Approach.** Rewrite in place: the authoritative download location, the zip's SHA-256 and how to
check it, the SmartScreen paragraph rewritten for an unsigned public download, the requirements
including C22's floor, C21's double-click extraction path, B13's log and save locations, B12's
notice file, and where to report a problem, pointing at D33's form. Keep the legal section. It lands
as a draft for the author's review, since outward communication is theirs.

**Model recommendation.** high. It is the one document that has to be right at the moment nobody can
be asked for help.

**Verify.** Read it against the E41 sandbox run, followed literally with no repo knowledge assumed:
every command it prints was pasted and worked, and every claim about disk, time and locations
matched. Any step where the tester had to guess is a defect in this item.

**⚠ Traps.** Write it after B13, C21 and C22 have settled what it documents. Its sign-off is the
author's rather than a test's.

**Verified.** The rewrite lands as a draft awaiting the author's sign-off, which is recorded in
`packaging/MANIFEST.md`'s row for the file and in `docs/tooling.md` rather than in the shipped text,
so nothing in the zip announces its own provisional status. Every file the README names is at the
zip root of a real export, spelled the same way: `.scratch\export\` holds `CSVM.exe`,
`data_CSVM_windows_x86_64\`, `Extract.cmd`, `Extract.ps1`, `ExtractAssets.ps1`, `ExtractRof.ps1`,
`ExtractRof.MenuLayout.cs`, `tools\unzbd.exe`, `LICENSE`, `LICENSE-unzbd`,
`LICENSE-thirdparty.txt`, `BUILD-INFO.txt` and `README.md`. Both commands it prints were run:
`Get-FileHash <file> -Algorithm SHA256` prints the algorithm and hash, and
`powershell -ExecutionPolicy Bypass -File Extract.ps1 "<path>"` against a non-existent folder bound
its argument and failed on the intended check with exit 1, which is the same spelling
`Extract.ps1`'s own `.EXAMPLE` carries and the same switches `Extract.cmd` applies.

The claims restated from the four items they belong to are each traced back to their source rather
than to the previous README. The renderer floor and its symptom are C22's: a GPU with a working
Vulkan or Direct3D 12 driver, and below it menus that work followed by a mission that vanishes with
no window and no message. The identification the README gives for that case is the build's own
`[perf] gpu=` line (`Launcher.cs:452`, which logs the adapter, driver and method), naming
`Microsoft Basic Render Driver` as the software device to look for, since that line is written by
CSVM's own sink and survives into `logs\`. Godot's `Your video card drivers seem not to support
Vulkan, switching to Direct3D 12` is quoted as **not** a fault on its own, because a machine with a
real Direct3D 12 driver prints it and is above the floor; C22 also settled that no flag reaches the
fallback, so the README prints no rendering flag. The log location, the per-run filename and
`%APPDATA%\Godot\app_userdata\CSVM` are B13's, and the version on the log's first line reads
`INFO  [core] csvm version=0.1.0` in a current log, with `BuildStamp` putting the same number in the
menu's bottom-right corner. The extraction path is C21's: `Extract.cmd` probes, offers the picker,
takes a dropped folder and holds its window open, and the no-data screen names it. The notice file
and `BUILD-INFO.txt` are B12's. The disk figure was re-measured rather than carried over: the
recipient-shaped subset of the dev tree's `extracted/` (the per-chapter archives, `rof/`, the
unpacked `rimage/` and the root archives, excluding the `-Unzip` mirrors a recipient never
produces) is 0.54 GB in about 1550 files, so "about 0.6 GB, leave 1 GB free" holds. The extraction
**time** is the one figure carried forward unmeasured, and is E41's to confirm with the rest of the
literal walk-through.

Three things the rewrite decided rather than inherited. The SmartScreen paragraph no longer says
the reader knows the sender; it says the zip is unsigned, that the warning is Windows not
recognising a publisher, and that the SHA-256 against the releases page is the check that carries
meaning, which is the same argument the milestone goal uses to decline code signing. The
extraction-stamp warning names `ExtractAssets.ps1` and `ExtractRof.ps1`, which are not what a
recipient runs, so the README bridges that in words rather than changing the engine string, which
would be a `CSVM/` change in a documentation item. The support sentence says what happens to a
report and not how fast, which is D33's trap arriving early because the README points at D33's
form.

The other three files that had to agree were corrected in the same edit. `README.md`'s export
paragraph listed a payload that had been missing `Extract.cmd`, `LICENSE-thirdparty.txt` and
`BUILD-INFO.txt` since B12 and C21 landed. `packaging/MANIFEST.md` addressed a friend rather than a
downloader. `docs/tooling.md` gains what the packaging README is for, the four facts in it that
restate code or that file and go stale silently, and the author-review rule. D31's method wording is
untouched and uncontradicted: the packaging README makes no claim about method at all, and its
Legal section is the one D31 left in place. Content gate clean (`CheckCommitContent.ps1` exit 0);
nothing under `CSVM/` was touched, so the complete `RunTests.ps1` is not this change's gate.

**Open for the author.** The text is a draft: the tone of the support sentence, the amount of
troubleshooting a front-page README should carry, and whether the unsigned-download paragraph says
enough are all judgements that belong to whoever signs the release.

## D33 ☑ `.github/`: the bug form and the policies

**Goal.** A stranger's first report arrives with the version, the log and the steps already in it,
and the repository states what it accepts before someone spends an evening on a pull request.

**Evidence (confidence: traced).** The repository has no `.github/` directory: no issue templates,
no `CONTRIBUTING.md`, no `SECURITY.md`. Issue tracking today is this repo's own markdown, which
`docs/agents/issue-tracker.md` documents, and GitHub Issues are unused.

**Approach.** A bug-report issue form requiring the build version (B11), the log file (B13), the
install and the hardware. A `CONTRIBUTING.md` stating Decision 7 plainly (reports are worked in the
issue; `backlog.md` is the author's internal list and is not mirrored, so an issue is the thread to
follow), Decision 8's pull-request stance with the reason (a contributor cannot run this repo's
gates), and the AI-assistance disclosure that every outward communication carries. A `SECURITY.md`.
Turn Issues on at the flip and leave Discussions off, so there is one surface to watch.

**Model recommendation.** medium. Templates and policy text, with the decisions already made.

**Verify.** Open a test issue against the form on the still-private repository: the required fields
cannot be skipped and the rendered result is readable. Close it before the flip.

**⚠ Traps.** Promising more responsiveness than one person can deliver is the standing risk in this
file; say what happens to a report, not how fast.

**Verified.** `.github/` lands as four files. `ISSUE_TEMPLATE/bug_report.yml` carries six required
fields (build version, what happened, what you did, the log file, your machine, extraction state),
two required checkboxes (searched the existing issues; owns a retail copy and extracted their own
data), and one optional field for screenshots and `BUILD-INFO.txt`. Both YAML files parse under
`yaml.safe_load`, and the `required` flags read back as set on every one of those fields, which is
the half of the Verify step a rendered page shows only by refusing to submit. The other half is the
rendered check, and the form was then exercised in the GitHub UI on the still-private repository
with Issues on and Discussions off: the required fields cannot be skipped, the two pre-filled boxes
carry their templates, the security link sits on the chooser beside the form, and the issue the form
produces reads correctly.

Each field the form requires traces to the item that made it answerable. The version field names
the two surfaces B11 built, the log's first line (`csvm version=...`) and the menu's bottom-right
corner, and offers `BUILD-INFO.txt` for a reporter who has neither. The log field names `logs\`
beside `CSVM.exe`, one file per run, newest is the run, which is B13's exported layout rather than
the development tree's `.scratch\logs\`. The machine field asks for the `[perf] gpu=` line by name,
so a below-floor report is recognisable as one from the issue body without opening the attachment,
which is what C22 established that line does. The extraction dropdown is the one required answer the
log cannot supply, and it separates "never ran `Extract.cmd`" from a real fault.

The form and `packaging/README.md` ask for the same things. The README's support paragraph named the
log and what you did, while the form requires four items, so the README now names the version, what
you did, the graphics card and the log, and says that attaching the log answers most of the form by
itself. D32's support sentence, what happens to a report rather than how fast, is unchanged and is
the same sentence the form's own preamble uses.

`CONTRIBUTING.md` states Decision 7 with the reason the reporter needs (the issue is the thread to
follow, `backlog.md` is the author's internal list in a private `BL-`/`PT-`/`CAP-` scheme, a report
is not mirrored into it, and its absence there does not mean it was dropped) and Decision 8 with
the reason a contributor needs (the accepted areas are `packaging/`, the extraction scripts, the
documentation and typo fixes; a change under `CSVM/src` needs an issue first because it lands
through a golden-image tier rendered by the pinned editor against an extracted retail install,
which nobody outside this machine has). It also states what is never accepted, since a public repo
that takes pull requests can be handed the one thing this project's legal position forbids: assets,
extracted data, reproduced game code or decompiler output. The AI-assistance disclosure is the root
README's wording, extended with what a contributor should do about their own. `SECURITY.md` states
the surface (the file parsers, the `user://` state files, the extraction scripts, and a zip
disagreeing with its published SHA-256), the private advisory channel rather than an issue, that
only the newest release gets fixes, and that the unsigned SmartScreen warning is out of scope by
design. Its "contacts no server" claim is grepped rather than assumed: `System.Net`, `HttpClient`,
`WebSocket`, `UdpClient`, `TcpClient`, `HTTPRequest` and `MultiplayerApi` have no hit anywhere
under `CSVM/`, scenes included, so no build reaches out on its own. The multiplayer door is the
one socket, and the page states it rather than grepping it away: `ENetMultiplayerPeer` lives only
in `CSVM/src/Net/EnetTransport.cs` and Godot's UPnP client only in `CSVM/src/Net/UpnpPortMap.cs`,
the two exemptions `CSVM.Tests/NetNamespaceDependencyTests.cs` asserts over compiled metadata,
and nothing listens until the player hosts.

`ISSUE_TEMPLATE/config.yml` keeps blank issues on deliberately, with the reason in the file:
Discussions stay off so there is one surface, which leaves a question or a suggestion nowhere to go
unless the blank route stays open. It also puts the security advisory link on the chooser, so the
one report that must not be public has a button next to the one that should be.

The documents this makes false are corrected in the same change.
`docs/agents/issue-tracker.md` loses "GitHub Issues unused" and gains the public surface as a
second table plus the rule that a public report gets no internal id and is never copied into
`backlog.md`; its "PRs as a request surface: **Off**" becomes on and narrow, with the author's own
commits still going straight to `main`. `docs/agents/triage-labels.md` said there was nothing to
apply a label to, which held while the tracker was only markdown. `PROJECT_CONTEXT.md`'s "No GitHub
Issues yet" becomes the two-surface sentence, and the front `README.md` gains a reporting and
contributing section, since a stranger arriving at the repository rather than the zip had no
pointer to either. "No PR workflow" in `PROJECT_CONTEXT.md` and `AGENTS.override.md` is narrowed to
"no internal PR workflow", because those two sentences exist to stop an agent opening a branch and
that instruction is unaffected by outside pull requests. Encoding gate clean (`CheckEncoding.ps1`,
no mojibake); nothing under `CSVM/` was touched, so the complete `RunTests.ps1` is not this change's
gate.

**Owed at the flip, not before.** **Private vulnerability reporting is offered on public
repositories only** (Settings, "Security and quality", Advanced Security), so the channel
`SECURITY.md` and the chooser's security link both point at cannot be turned on while the repository
is private. E42's Approach carries that step. `SECURITY.md` therefore states the fallback in its own
text rather than relying on the button existing, since a security page whose only route is a dead
link is worse than one that names a second one. The labels `needs-triage`, `needs-info`,
`ready-for-agent`, `ready-for-human` and `wontfix` do not exist in the repository yet; the form
applies only `bug`, which every repository has by default.

## D34 ☑ `PublishRelease.ps1`: tag, versioned zip, checksum, release

**Goal.** Publishing is one command whose outputs cannot disagree: the tag, the exe version, the zip
name, the checksum and the notes all come from one run.

**Evidence (confidence: traced).** `ExportRelease.ps1` builds, exports, stages the payload and
writes `.scratch\CSVM.zip`, and stops there. `gh` is installed (`winget install GitHub.cli`), but a
shell started before that install does not have it on `PATH`, so open a fresh one and check
`gh auth status` before assuming it is authenticated. `.gitignore` ignores `*.zip` as a class, which
does not affect release assets, since they are uploaded rather than committed.

**Approach.** `PublishRelease.ps1` at the repo root: read the version from B11's single source, run
`ExportRelease.ps1`, confirm the produced zip's name matches, compute its SHA-256, create the
annotated tag on the commit that was built, and call
`gh release create <tag> <zip> --title ... --notes-file <notes>`, with no pre-release flag
(Decision 4). It refuses to run on a dirty tree, and refuses if `tools/mech3ax`'s `cs-anim` is dirty
or ahead of its origin, so both recorded provenance facts are true. Authenticate `gh` first.

**Model recommendation.** medium. Ordinary scripting with one sharp edge: it creates a public,
hard-to-retract artifact.

**Verify.** Run it end to end against the still-private repository, download the asset from the
release page, confirm its SHA-256 matches the notes and its contents match `.scratch\export\`, then
delete the test release and its tag.

**⚠ Traps.** ⚠ Tag before upload, and never re-point an existing tag: a moved tag makes the source
correspondence for an already-downloaded binary false. A dirty tree is the same failure in another
form, which is why the script refuses one.

**Verified.** A full rehearsal ran end to end against the private repository under
`v0.1.0-rehearsal`, the tag name `-TagSuffix` exists to make available, so that the release
version's own tag is minted once rather than created, deleted and created again. The export
produced 198 files; the annotated tag pointed at the built commit and carries the zip's checksum in
the tag object, so that correspondence survives without the release page; and the release was
created with the pre-release flag off and draft off, with the API's `releases/latest` returning it.
The asset was then downloaded from the release page: its SHA-256 matched the published notes, and
its 198 entries matched `.scratch\export\` with nothing missing on either side and no content
differing. The exe in the downloaded copy stamps `0.1.0.0`, and its `BUILD-INFO.txt` names the
tagged commit as clean with `cs-anim` pushed, which is what the notes restate. The release and its
tag were then deleted, remote and local, leaving the repository with the two archive tags it had
before.

Every refusal was exercised against real state rather than reasoned about: the dirty tree twice,
once on this item's own uncommitted work and once after a complete successful export, when another
session working this tree edited `backlog.md` during the five minutes of the build, which is why
the post-export message names the paths it found; the tag existing locally; the same tag existing
on `origin` once the local one was deleted; a notes file stating a checksum of its own; and a notes
file that is not there. `-DryRun` then ran with a notes file and stopped before the tag having
created nothing, which is the path E42 uses to read its own notes back before spending the tag.

Two exports of the same commit produce zips with different SHA-256 values, since the archive
carries timestamps and neither the .NET publish nor the shader bake is byte-reproducible. That is
what makes computing the checksum inside the publishing run, rather than in advance, a correctness
requirement instead of a convenience.

The first run also produced `docs/verification.md`'s SHELL-20. `gh release view <tag> 2>$null`
ended that run, because under `$ErrorActionPreference = 'Stop'` a native command's redirected
stderr becomes a terminating `NativeCommandError`, and "release not found" is the expected answer
when the tag has not been published yet.

# Wave E, The flip

## E41 ☐ A clean-machine run of the downloaded zip, above and below the floor

**Goal.** The exact artifact a stranger will download is proven on machines that have never seen
this project, following only the public README.

**Evidence (confidence: traced to the earlier run).** The friends release was tested this way in
Windows Sandbox with a `.wsb` config plus a driver script that followed `packaging/README.md`
verbatim and wrote its summary, logs and screenshots back to the host. Three gotchas from that run
still apply: the logon command can fire before the mapped folders mount, so the bootstrap polls for
them and redirects its console to the output folder; a force-killed sandbox wedges the Container
Manager and needs an elevated service restart, so the window is closed instead; and a flight-mode
`--screenshot` run does not exit inside the sandbox, which is a sandbox artifact rather than a
package failure, so those runs need a timeout and a check of the log's final `pixmd5` line.

**Approach.** Rebuild that harness from the pattern, with two changes. The zip arrives with a real
mark of the web, downloaded through a browser from D34's test release rather than copied through a
mapped folder. And the rig runs two configurations: the normal one, and C22's `<vGpu>Disable</vGpu>`
below-floor one. The run begins where a stranger begins, at E43's front page, and follows its
download link to the release rather than being handed a URL. Follow D32's README literally from
there, including C21's double-click path, and record what a first-time reader sees at each step. <TODO: the earlier harness lived in a session scratchpad and is
gone; it is rebuilt from the described pattern, not recovered.>

**Model recommendation.** medium. Careful execution of a known procedure, where the observations
matter more than the code.

**Verify.** From the sandbox output alone: extraction completed, the game launched, a flight ran, the
log carries the version, and the log and save locations are where the README says. The below-floor
run produced the behaviour the README predicts. Any step where the tester had to guess is a D32
defect, or an E43 one where the step was on the front page.

**⚠ Traps.** ⚠ Never force-kill the sandbox processes; close the window. Do not read the flight-mode
exit hang as a package failure. A hand-copied zip does not test the mark-of-the-web path, which is
the specific thing this run exists to prove.

## E42 ☐ Release notes, the first tag, the flip, and an announcement draft

**Goal.** The repository is public, `v0.1.0` exists with notes a stranger can judge the build by, and
an announcement draft is waiting for the author.

**Evidence (confidence: traced for the hygiene, derived for the content).** A scan of every ref
(`git log --all --diff-filter=A --name-only`) finds no file ever added with an asset-like extension,
so the hard rule has held and the history is publishable. The Known Issues content is A1's output,
re-verified there.

**Approach.** Write the notes: what the build is, what it needs (including C22's floor), what is
known broken with a workaround where one exists, the SHA-256, the CSVM commit and the mech3ax
`cs-anim` commit the binaries were built from, and the AI-assistance disclosure. Draft the
announcement post to the same facts, short, for the author to place. Then, in one sitting: flip the
repository to public with Issues on and Discussions off, enable private vulnerability reporting,
which D33's `SECURITY.md` points at and which GitHub offers on public repositories only, run
`PublishRelease.ps1`, and read the result from a logged-out browser before anything is posted
anywhere.

**Model recommendation.** high. Irreversible in practice, and the notes are the project's first
impression.

**Verify.** From a logged-out browser: the repository reads correctly for a first-time visitor, the
release page's asset downloads, its SHA-256 matches the notes, and the linked commits are the ones
the binaries were built from. Re-run one E41 pass against the published asset rather than the test
one.

**⚠ Traps.** ⚠ Flipping visibility is not reversible in the way people assume: a repository that was
public for an hour can have been cloned and cached, so everything in this plan lands before the
flip, not after it. A release tag is equally permanent, see D34. Nothing is announced until the
logged-out read has happened.

## E43 ☑ `README.md`: the play path, the status, and the developer split

**Goal.** The first text a stranger reads describes the build that exists, and someone who only wants
to play has a way in with no developer instruction between them and the download.

**Evidence (confidence: traced).** `README.md` offers no download path at all: "Getting started"
opens with `git clone` and asks for Godot 4.7 (.NET) and the .NET 8 SDK, so the heading a visitor's
eye lands on first is the one that sends a player away. Its Status is two milestones behind the tree,
naming free flight, timed stunt runs, splitscreen Dogfight and destructible world objects while
omitting M4's combat AI and its four Instant Action mission types and M5's single-player campaign,
all of which `PROJECT_CONTEXT.md` records as delivered. `packaging/README.md` already carries the
whole player path in 147 lines, written by D32 for exactly this reader. The page's countable claims
are current: 48 format pages behind the 49 tracked `.md` files under `docs/formats/`. Its "Package a
release build" section describes `ExportRelease.ps1` and predates D34's `PublishRelease.ps1`. No
image has ever been tracked on any ref, which is the property E42's Evidence rests on.

**Approach.** Five edits to `README.md` and no other file. The opening line takes
`packaging/README.md`'s wording, so the two front doors speak in one voice and no visitor has to know
what XWVM is. Status becomes what this build lets a player do: the single-player campaign with its
briefing and flight-check screens, the four Instant Action mission types, 2 to 4-player splitscreen
Dogfight, free flight and timed stunt runs, 11 aircraft over 8 animated worlds, against AI that
patrols, engages and evades, plus the extraction claim, with one honest sentence that it is an early
build and known issues left to the release page rather than restated. A "Download and play" section
follows it: the requirements in two lines, including C22's renderer floor, then four numbered steps
(download from `releases/latest`, unzip, double-click `Extract.cmd` and point it at your retail
install, run `CSVM.exe`), then a link to `packaging/README.md` as the full version that also ships
inside the zip. "Getting started" becomes "Building from source", under a one-line signpost that the
rest of the page is for building the engine rather than playing it. Then the remainder is read once
as a stranger would and made true, which is where the packaging paragraph gains
`PublishRelease.ps1`.

**Model recommendation.** high. It is the document that has to be right at the moment nobody can
help, which is why D32 is high as well, and the failure mode is a page nobody re-read.

**Verify.** Every link resolves, and the four steps agree with `packaging/README.md`, with C21's
`Extract.cmd` spelling and with C22's floor as observed rather than as assumed. Every capability
Status names exists in the build. Read cold, the page puts nothing between a visitor and the download
except the requirements. The `releases/latest` link cannot resolve until E42 creates the release, so
E42's logged-out read is where that is checked, and that read is also the author's approval of this
wording, which is the gate rather than a marker in the file.

**⚠ Traps.** ⚠ The front page carries no fact `packaging/README.md` does not. A second player-facing
instruction set drifting from the first is the failure this item can cause, and `docs/tooling.md`
already records which facts in that file go stale silently. ⚠ No screenshot. The repository has never
tracked an image, E42's publishable-history evidence rests on that, and a rendering of the game world
is the original's art whatever the file is called.

**Verified.** Every capability the new Status names was counted or read in the tree rather than
carried over from `PROJECT_CONTEXT.md`. The 11 aircraft are `InstantActionFeature.AirframeRows`, the
one roster both the launchscreen (`LaunchMenu.BuildPlanes`) and `PlanePickerRoster` read, so the
count is of stock airframes and not of picker rows, which also carry a profile's hangar builds. The 8
chapter worlds are `MenuChapters.Rows`. The four Instant Action mission types are
`InstantActionFeature.MissionTypeRows`, and the page uses their on-screen labels from
`Mech3/InstantAction.cs`: dogfighting an ace, dogfighting a squadron, attacking a zeppelin, and stunt
flying, which `StuntRace` clocks and ranks. Splitscreen is 2 to 4 (`SplitScreen.MaxPlayers`, clamped
at both ends). The campaign's cabin, briefing and flight-check screens are
`CampaignFlow.CampaignScreen` members. Patrol, pursue
and evade are three of the nine `AiMode` states. Turrets take no flag at all, and zeppelins reach a
player without one: `GameSession.ResolveCampaignZeppelins` turns them on for a campaign mission that
ships the data, and the Instant Action zeppelin run enables them itself.

**The old Status was wrong in a way the rewrite had to avoid repeating, not only out of date.** "Any
of 11 aircraft over any of 8 chapter worlds, free flight, timed stunt runs, or splitscreen Dogfight"
promised all eight worlds to each of the three modes it named, while `MenuChapters.For` gives stunt
flying only the six chapters that carry Danger Zones, and Instant Action's environment list is seven.
Status is now a six-bullet list, one line per thing the build plays, which counts what the build
contains and leaves the per-mode list to the menu.

"Download and play" carries no fact `packaging/README.md` does not. Windows 10 or 11 64-bit, the
Vulkan or Direct3D 12 driver, the retail install, the 1 GB and the bundled runtime are its
Requirements section; the four steps are its "Setup: extract, then fly" and the same sequence
`packaging/MANIFEST.md` states in one line, with the `ZBD`/`GOSDATA` folder, the once-only extraction
and the read-only install taken from it. The renderer floor is C22's as observed, a GPU with a
working Vulkan or Direct3D 12 driver, and the below-floor symptom is left to the file that documents
it rather than restated. The closing pointer names the topics `packaging/README.md` covers, the
SHA-256, SmartScreen, the log and save locations and the vanishing mission, without repeating any of
their content, which is the only shape that cannot drift.

The rest of the page was read cold and corrected where it was untrue rather than rewritten. `GODOT`
in the build command is an internal placeholder no visitor can resolve, and is now `godot`, the
spelling `CSVM/README.md` beside it already uses. The export payload list was missing
`ExtractRof.MenuLayout.cs`, which `$ReleaseFiles` copies and `packaging/README.md` counts among the
six extraction files. The packaging section gains `PublishRelease.ps1`, its refusals and `-DryRun`,
read off the script's own parameter block and `docs/tooling.md`'s "Publishing a release"; its heading
stays "Package a release build" because `ExportRelease.ps1` quotes that heading by name in its
missing-templates error, and this item edits no file under the repo root but `README.md`. "Reporting
a problem, and contributing" moved above "Building from source", so that the signpost is true of
everything below it and a player with a bug does not have to read past a line telling them the rest
is not for them.

The countable claims hold: the 49 tracked `.md` files under `docs/formats/` are its README plus the
48 pages the page claims, and the 34 `docs/org/` pages are not counted on the front page. Every
relative link resolves to a tracked file (`packaging/README.md`, `packaging/MANIFEST.md`,
`CSVM/README.md`, `docs/tooling.md`, `docs/formats/`, `docs/org/`, `LICENSE`,
`docs/formats/LICENSE`, `.github/CONTRIBUTING.md`, `.github/SECURITY.md`), and nothing in the tree
links a heading this item renamed. The `releases/latest` link cannot resolve while the repository is
private and no release exists, and E42's logged-out read is where it is checked and approved. No
image was added. Content gate clean (`CheckCommitContent.ps1` exit 0); nothing under `CSVM/` was
touched, so the complete `RunTests.ps1` is not this change's gate.

# Appendix: the Known Issues draft, A1's output for E42

What this is: the list E42 turns into the release notes' Known Issues section. Each entry is one
sentence a player would recognise, one sentence of workaround where there is one, and the evidence
that the entry is still open, which is a `git log --all --grep=<ID>` that returns no landing plus
the code or data path that still carries the defect. **The parenthetical evidence is for the
author, not for the notes**: E42 strips it and keeps the two sentences.

Two rules this list is written to. Nothing here meets Decision 3's bar, because A1 found nothing
that does except `BL-694`, which A2 fixes and which therefore never reaches the notes. And nothing
here is an internal decode: the sentence says what happens on screen, not which function is wrong.

Before E42 uses it: `BL-694` is fixed by then, so re-read the CM14 entries against the landed fix.
Anything A2 or a later wave closes comes off this list.

## Campaign missions

- **The Gemini's gasbags burn without ever finishing, so fire alone never brings the zeppelin
  down.** Shoot the gasbags directly instead of waiting for the fire, since three of the five kill
  it. (`BL-639`; no landing commit, and it is blocked on unfilmed reference footage, `CAP-47`.)
- **In CM10 the attack balloons dive from their entrance altitude down to the sea and climb back
  out, and the objective marker follows them down.** Wait for the climb, which runs at the
  authored rate and needs no input. (`BL-674`; no landing commit, the motion is the entrance
  script's handoff in the animation runtime.)
- **In CM02 the player's own Pandora fires its turrets at the Balmoral the mission wants
  captured.** Close on the last Balmoral and finish the wing-walk promptly rather than circling.
  (Kept by decision: the original's own turrets do the same, since nothing in its turret path
  reads an objective, capture or pickup flag; `git log --grep=BL-717`.)
- **In CM13 the flight check offers the change-plane button to the wingman and never grants the
  mission's own aeroplane.** Fly the aeroplane the profile already owns, which completes the
  mission. (`BL-689`; no landing commit, `CampaignFlightCheckPage` still hands one slot-less
  answer to both crew slots.)
- **In CM15 the Balmoral staged in the cutscene wears the stock skins instead of the livery the
  flyable aeroplane wears.** No workaround, and nothing about the mission changes. (`BL-690`; no
  landing commit, and `AircraftStage` still builds its subtree with no painter of any kind.)
- **The cutscene movies do not play at all.** No workaround; the campaign reaches the cabin and
  every mission without them. (`BL-446`; no landing commit, and the tree contains no
  `VideoStreamPlayer` and no transcode step.)

## Enemies and combat

- **One C1/M04 aircraft with no patrol net can fly tens of kilometres out of the mission area.**
  Target it and fly out to it if an objective waits on it. (`BL-974`; the pursuit's walk across
  the map is fixed, and the other patrols engage, break off and come back at the controls.)
- **A zeppelin's turrets can shoot the player through the hull they are mounted on.** Break away
  from the hull rather than flying along it, since the fire stops once the ring loses its bearing.
  (`BL-714`; a probe confirmed a parked hull blocks its own rings correctly, so the item stayed
  open for the moving-hull case it was reported on.)

## What the world looks and sounds like

- **An aircraft's ground shadow lies on a flat quad.** It is placed, sized, faded, coloured and
  shaped the way the original draws its own, but on a steep slope it rides over the ground rather
  than wrapping it, and water takes no shadow. Set Enhanced Graphics in Game Options and restart
  for real shadow maps instead, which are not the shadow the original drew. (Kept by decision, no
  item tracks it; `git log --grep=BL-331`.)
- **In the New York chapter the lit building faces read darker than the original.** No
  workaround; it is a matter of how the shipped textures are sampled and nothing is missing from
  the world. (`BL-322`; its premise has been narrowed by decodes and it has no landing commit.)

## Menus, and playing with more than one person

- **A second pilot can pick an aeroplane but never a weapon loadout, Back on that screen removes
  them from the game instead of returning, and the first pad drives their screen as well as their
  own.** Press Start to rejoin after an accidental Back, and let the first player make the loadout
  choice, which every seat then flies with. (`BL-746`, `BL-747` and `BL-748`; none has a landing
  commit, and the cited `OriginalSeatPlane` arms are unchanged.)
- **A two-pilot Free Flight ends its plane-selection walk back on the Free Flight screen instead of
  launching.** Press FLY again on the first player's screen, which launches. (`BL-749`; no landing
  commit, and `FinishSeatWalk` still launches only on the Instant Action return.)
- **A splitscreen stunt race starts its clock the moment the world appears, so whoever finishes
  loading first flies first.** Start the run together by agreement rather than trusting the clock.
  (`BL-314`; no landing commit, and there is no countdown of any kind in the tree.)
