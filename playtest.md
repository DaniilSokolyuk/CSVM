# Playtest checklist

Everything that needs a human at the controls (or the original game open for A/B), consolidated.
**This file holds only what is actionable *today*.** Anything whose test is blocked on an unlanded
fix lives on its `backlog.md` entry as a `*Playtest after fix:*` line instead, so an empty section
here means the work is queued, not forgotten. Deep evidence and traps live in
[`backlog.md`](backlog.md); the two are kept in step.

**Every item carries a stable ID**, `CAP-nn` for an owed capture, `PT-nn` for something to fly.
Cite them from `backlog.md` and in conversation the way `BL-nnn` is cited. IDs are permanent: when
an item closes its ID retires with it and is never reused, so numbering gaps are expected.
Retired IDs disappear from this file, so never mint a new ID by scanning the entries below, run
**`./New-ItemId.ps1 -Kind CAP`** (or `-Kind PT`), which increments a shared locked counter in
`.git/item-id-counters.json` and is safe under concurrent sessions. ⚠ **Run it for EVERY id, every
time**: it is not a once-per-session lookup, and deriving the next id by adding 1 leaves the counter
behind the file, so the number you invented gets handed out again later. `-Count n` reserves a block
in one call when you need several. Retired IDs' verdicts are in
the retiring commit's message (`git log --grep=<ID>`); earlier retirements are in
the archived development log.

**Structure.** Section 0 lists owed captures as themed tables, one table per filming batch, the
theme naming the capture setup (cockpit gauges in frame, external view, …), with fixed columns
ID · Capture · What must be in frame · Unblocks. The theme sections are standing, an emptied
table stays, meaning nothing is currently owed in that batch. Section 1 groups flights by **flight
profile**, one section is one sortie (chapter + plane + situation), headed by a copyable launch
command. Sections sort by chapter then plane; items within a section by ascending ID. An item free
to choose its plane or chapter piggybacks on an existing profile, it never opens a section of its
own. Every PT item is one bullet:

    - `PT-nn` `[A/B: <ref>]`-or-`[Own]` **What to check (`BL-NNN`).** context… *Look for:* … *Blocks:* …

`[A/B: <ref>]` names the capture or `OriginalScreenshots/` shot to have open *before* launching;
`[Own]` is a judgement call on our own remake with no original reference. A mixed item takes
`[A/B]`, the per-check references stay in their bullets. *Look for:* holds one sub-bullet per
check, lettered `(a)(b)(c)` when there is more than one. *Blocks:* is mandatory, name what a pass
closes, or state outright that nothing tracks the outcome and a fail mints a new `BL` item.
Optional: context prose between title and *Look for:* (a few lines at most, deep evidence lives
in `backlog.md`), and *Variations:* for extra flags or re-runs beyond the section's command.

**Captures staged for an item live in `playtest/<ID>/`**, git-ignored (they are renders of the
player's own game files) and, unlike `.scratch/`, **not swept by `CleanScratch.ps1`**, so they
survive until the item that owns them closes. Delete the folder with the item.

**How to launch.** `./RunGame.ps1` (no args) builds and opens the in-game launchscreen
(Mode → Chapter → Plane, keyboard or pad). Any args bypass the menu and drop you straight in, e.g.
`./RunGame.ps1 --plane=player_fury --chapter=C1`. Flight is the default. Full flag list:
[`docs/cli.md`](docs/cli.md).

**Plane model names** (for `--plane=`): `player_autogyro` Hoplite · `player_avenger` Hellhound ·
`player_balmoral` Balmoral · `player_bhawk` Bloodhawk · `player_brigand` Brigand ·
`player_pfighter` Devastator · `player_fbrand` Firebrand · `player_fury` Fury ·
`player_kestrel` Kestrel · `player_peacemaker` Peacemaker · `player_warhawk` Warhawk.

**Cross-checked against the original design documentation 2026-07-25.** A *pre-release* spec: it
settles **system shape**, never numbers or art direction (rebalanced before release, extracted data
or an `OriginalScreenshots/` capture wins wherever either exists). **It is silent on the rest, so do
not re-run this cross-check:** all of the splitscreen work (the original's multiplayer was
networked, so there is no splitscreen reference at all); every *tuning* question, pitch, stall
recovery, dive speed, camera, mix levels, weather, which it covers with qualitative rules and no
numbers; and the C3 spiderweb, patrol-boat hit points, map-edge continuation, which world axis is
north, and the crossed `pdpN_h` numbering.

⚠ **The spec's HUD and damage material is unreliable as a class, three of its claims were
overturned by direct observation in one sitting (2026-07-30).** It said the low-altitude warning
beeps (it does not), that the ammo gauge's yellow tier meant gun heat/jam rather than ammo (it is a
real ammo tier, on gun belts only), and that the damage gauge ramps from a blue full-health state
(there is no blue tier and never was). Treat any remaining `[spec]`-sourced HUD claim as a
hypothesis to check, not a target to build against.

---

## 0 · Owed captures (`CAP-nn`)

**One sit-down list.** These are recordings and screenshots of *the original game*, not our build.
Most of the items below and a large part of `backlog.md` are blocked on one of these, so filming
them in a batch unblocks far more than doing them one at a time.

⚠ **No capture here answers a flight-model question, and none may be filed.** A number off the
cockpit panel does not settle a flight quantity: footage cannot confirm a decode, it only ranks
readings, and it will rank a reading nobody has thought of (`docs/verification.md` DET-12, and
DET-11 on the sim clock that makes every wall-clock rate wrong by 39 %). Flight questions go to
`crimson.exe`, see [`docs/org/flightModel.md`](docs/org/flightModel.md). The captures below are **qualitative**: what a thing looks and
sounds like, watched and frame-sampled, never measured into a constant.

### HUD, ammo gauge in frame

| ID | Capture | What must be in frame | Unblocks |
|---|---|---|---|

### Menus and front end

Nothing here is a flight question, and none of it can be answered from `extracted/rof/`: a layout
row and a button bitmap state composition, never what a press, a rollover or a screen change does.
The screen and asset inventory these entries hang off is
[`docs/org/menu-inventory.md`](docs/org/menu-inventory.md); film in 4:3 windowed, where the original
draws its authored 800x600 space one-to-one.

| ID | Capture | What must be in frame | Unblocks |
|---|---|---|---|
| `CAP-50` | The **Instant Action screen's VIEW STORY**, pressed | The first take (`CAP-50.mkv`, 2:27, silent) settled the screen's composition and every dropdown, the wave paging, the ace duel, the radio, the militia reset, BUILD CUSTOM PLANE and WEAPON LOADOUT; the findings are in `docs/org/menu-inventory.md` Part 4, and the page's opening preset and when its name is written are the user's own design rather than corrections. One thing was never pressed: **VIEW STORY**. Film it on two presets and hold the page it opens. *Look for:* whether it opens the same riveted spread FLY MISSION does (headed by the mission type at `CAP-50.mkv` t=142) or a page of its own, what its title reads (the preset's name, the mission type, or the `IDS_IA_STORYTITLE` format's text) and how the take gets back. Sound rides `CAP-52`'s re-record | `docs/org/menu-inventory.md` Part 4 |
| `CAP-51` | **The AUDIO page with sound, and a leaf re-entered after CANCEL** | The first take (`CAP-51.mkv`, 2:16, silent) settled Preferences and all five leaves' composition, their option lists and their exits; the findings are in `docs/org/menu-inventory.md` Part 4. Two halves are still owed. (a) **Sound.** With the game's output routed into the recorder, open AUDIO, hold still for two seconds before touching anything, then move each of the three volume sliders in turn, then press CANCEL CHANGES. ⚠ The page's preview is decoded from `ASSETS/SCRIPTS/AUDIO.SCRIPT` (a sound object per category over `music_loop.wav`, `sfx_loop.wav` and `voice_loop.wav`, all three started at page open, only a volume set as a slider moves, and a cancel that restores from the saved settings); do not re-derive it from the film. *Look for:* whether all three loops are already running at page open or a clip fires per move, how long a clip runs (`ZB = 0` stands on every `@ctl@SK` object, so the script cannot say), and whether CANCEL restores the levels audibly. This port fires a clip on the level that moved instead, a deliberate departure, so the film changes nothing about it. (b) **The revert.** On Game Options change Auto Head Turn, press CANCEL CHANGES, then **re-enter the page and hold it**, since the first take never re-entered a leaf after a Cancel. Also press RETURN TO GAME if Preferences is ever reachable in flight (`PF_B_RETURNTOGAME`; the first take never left the menu tree) | the AUDIO page's preview against the decoded script, and whether a leaf's CANCEL CHANGES reverts a setting |
| `CAP-52` | **Menu audio, keyboard focus and the briefing**, across screens, re-recorded **with sound** | The first take (`CAP-52.mkv`, 2:28) settled the pointer (which bitmap where, the top-left hotspot, the swap on enter and leave only), the three lettering states, the press firing on the release, the two message boxes and where every Back lands, all of which Original now draws; the findings are `docs/org/menu-inventory.md` Part 4. Its audio track is digital silence, as are `CAP-49`'s, `CAP-50`'s and `CAP-51`'s, so the recorder had no game output routed to it: **check the recorder hears the game before the take**. Then one continuous walk, main menu, Instant Action, back, Campaign, roster (type a character and a refused one), cabin, scrapbook, briefing, flight check, back out. *Look for:* (a) which sound a rollover makes and which a press makes, on a main-menu plaque, a campaign plaque, a roster row, a contents row, a scrap and a dropdown row (CSVM's Original menu plays `MOUSEOVER` on entering a button and `MOUSECLICK` on a press and nothing over a row, unconfirmed); (b) what the edit-box keystroke and reject sounds are attached to (`ENTERTEXT` per taken character, `ENTERTEXT_ERROR` per refused one, unconfirmed); (c) where the splash music starts, stops and resumes, and whether the briefing's narration ducks it; (d) **REPLAY BRIEFING pressed** mid-narration, then RETURN TO CABIN mid-narration (Original restarts the wav from its top, and ends it and lifts the duck on leaving); (e) **DELETE PLAYER pressed** and cancelled (Original opens the two-button box focused on Yes); (f) **a keyboard and a pad pressed on every screen**, arrows, Tab, Enter and the pad's stick and A, since no take has pressed a key outside an edit box and CSVM's Original focus walk is a remake equivalence with no film behind it. CSVM's Built-in menu plays **no** click, rollover or keystroke sound and draws no pointer at all, so this is the whole of the evidence for both | `A4` (the audio and menu-input contracts), `D33`, the focus walk `docs/org/menu-inventory.md` Part 4 still calls a remake equivalence |
| `CAP-53` | The **Plane Construction tab bar out of order, Purchase, and the two refusals** | Two walks exist already, `CAP-50.mkv` t=118 to 140 from Instant Action's door and `CAP-52.mkv` t=110 to 145 from the cabin's, and they settled the name dialog, the tab art, the running total, the dropdowns, both doors' strips and scraps, SELL PLANES and its INVENTORY, and CANCEL, all of which Original now draws; the findings are `docs/org/menu-inventory.md` Part 4. Both walks ran the tabs strictly 1 to 6 and neither purchased. The langui 206 defaults ask is settled without film: `AIRFRAME.SCRIPT`'s own mailbox arms carry its gate and all three answers (`docs/org/hangar.md`, "When the airframe swap asks"). Film from the cabin: **clear** Load Default Configuration on the name dialog and read what the Airframe tab opens on; visit the tabs **out of order** (Paint, then Engine, then Airframe); enter a name the game refuses (langui 203, a duplicate or empty one); then press READY TO PURCHASE and hold the totals page, and buy. *Look for:* whether picks survive leaving a tab and whether Purchase commits them all at once, what the totals page shows, and what the refusal looks like. Sound rides `CAP-52`'s re-record | `C23` |

### Audio

| ID | Capture | What must be audible | Unblocks |
|---|---|---|---|
| `CAP-48` | Does the original play the train-destroyed line during CM16's Sparks pickup? | Fly `CM16` (`C4/M01`, Rocky Mountains - Raid on the Rocky Express) in the original to the Sparks pickup, and film the pickup cutscene from the moment it takes the screen through to the mission ending, clean audio on the take. The film destroys the Rocky Express itself about 16.7 s in (large fireball at the engine, all four cars sliding to a stop) and wins the mission about 5 s after that, so the whole clip is roughly 25 s. **What is owed is the audio over that window**: whether Zachary's `VO_c4-RM-m1_Zachary_59.wav` plays over the explosion and the fade to black. Play `extracted/soundsh/VO_c4-RM-m1_Zachary_59.wav` first, 4.3 s, so the line is known by ear before the sortie. Note the debrief's primary rows on the same take if the ending reaches them, in particular whether "Rescue Sparks" is marked. Our build plays that line there and leaves that row unmarked, and both follow from the decode: the film's own `CALL_ANIMATION [destroy_car01]` completes the objective watching `train01`'s healthy subtree, which plays the line and kills the rescue objective 4.85 s before `got_sparks` can reach EXECUTED (`git log --grep=CAP-48`). The gates that could have made this ours alone are closed in the exe: `FUN_0046a490` suspends the objectives runtime only on the code-20 slot, which this film never raises, and `FUN_004ed8c0` refuses a call by runtime state, never by authored activation, so a `WeaponHit` destructible starts on a `CALL_ANIMATION` | Nothing tracks the outcome. A match closes the question as faithful; a silent original, or one that marks the rescue, mints a new `BL` for the objective the film's own explosion retires |

### Weather & visuals

| ID | Capture | What must be in frame | Unblocks |
|---|---|---|---|
| `CAP-34` | Wing-light flare shape | Any player plane except the Bloodhawk (the one airframe with no wing-light anim or flare nodes) with wing lights on, one continuous orbit from front through side to tail. Close enough to read whether the flare shows sharp radiating star points (vs a soft round glow). The view-dependence half this capture once carried is answered by data: the flare mesh is a `Facade`/`SphericalY` model, `BL-1039` | `BL-284` |
| `CAP-59` | **Does a C1B night cloud have a moon side?** | In the original, fly C1B by night and film **one** cloud from **two headings at the same range**, first with the moon behind the camera, then with the moon behind the cloud, holding the same puff large enough in frame to read its near and far sides, with the moon itself in frame on the second pass so the bearing is readable. *Look for:* whether the side turned toward the moon reads brighter than the side turned away **at a fixed range**, which is the one thing that separates a moon side from distance through the night fog. ⚠ `playtest/CAP-11/`'s stills cannot answer it: both put their cloud on the moon's bearing at very different ranges, so their 218-against-70 split reads as fog depth. C1B's clouds are the 70 placed `cloudparent` facades, authored `lighting: false` with an empty normal array, so a moon side there would be a remake departure rather than a decoded term | `BL-325` |

### Damage & collision

| ID | Capture | What must be in frame | Unblocks |
|---|---|---|---|
| `CAP-27` | Does the original spark on the airframe at all? | Take damage in the original, a light scrape is enough, with the aircraft in frame (external/chase fine), and look for a **spark burst on the airframe itself**, distinct from smoke at the contact point. ⚠ This capture can **delete** a feature rather than tune one: our per-impact spark burst is driven by a 0.99 `injure_anims` entry that exists on **1 of 11** aircraft (the Devastator), plausibly an authoring leftover (was `BL-090` item 2, closed, `git log --grep=BL-090`). If the original never sparks, our implementation goes. If it does, `BL-281`'s ricochet mix can be judged | `BL-281` |
| `CAP-30` | Firing-wobble amplitude across calibers and airframes | Dead-astern external/chase clips, level flight, guns held 3 s+: **(a)** one plane with two well-separated calibers (30 vs 70), **(b)** one caliber on a light vs a heavy plane, **(c)**, added 2026-08-07, a **Bloodhawk 40-cal** clip framed and fire-rate-matched to `Gun Wobble and animation.mp4`, giving a *second independent amplitude measurement* of the same case the law was derived from. (c) is what lets this capture serve as `BL-266`(a)'s fallback instrument: (a)/(b) alone ask only whether caliber and plane weight enter the law, and **cannot** settle the uniform ~2–4× shortfall our render shows against the reference clip. ⚠ Dead-astern framing is load-bearing: it makes the on-screen roll angle the world roll angle with no projection model (`analysis/gun-wobble-shake/FINDINGS.md`, capture spec there). Confirms or refutes the pure-caliber magnitude law (7e-5 × caliber, measured on one 40-cal clip) and whether plane model/weight enter; a being-hit clip on the same sortie also pins the impact sources' stand-in quantities | `BL-266` |
| `CAP-38` | Beeper and seeker hits ON an aircraft, **with audio** | In the original, fire the beeper (`wep_10`) and the seeker (`wep_11`) at an aircraft and film the hit itself, external/chase, close enough to read the burst on the airframe. Both weapons author `ANIMATION large_fireball` on their aircraft `IMPACT` row and ours plays exactly that on a struck plane; a fused burst reads the `default` row in the original and in ours (`docs/org/ordnanceTypes.md` "Which row a burst reads"), so the beeper's near miss draws nothing and the seeker's draws its white flare at the round. The ground-side look is already signed off, so this clip is only the on-plane half. *Look for:* whether a direct strike shows the large fireball on the plane, something smaller, or nothing beyond the paint; whether a near miss shows nothing (beeper) or the flare (seeker); and the per-type impact sound on the same take (`snd_missile_beeper` / `snd_missile_seeker`) | Nothing tracks the outcome; a mismatch with our on-plane burst mints a new `BL` |
| `CAP-29` | Panel-damage semantics | Take controlled damage per part in the original, own aircraft in frame (external/chase), damage display visible if possible. **Reduced 2026-08-15 by the `BL-297` decode**, which answered all three questions out of `crimson.exe` (`docs/org/vehicleDamage.md`, "Damage staging"): (a) effects land at the node the def names, so a nose hit DOES spark wing sites; (b) nothing per-part fires at all while a part's armor absorbs; (c) each entry fires once per downward crossing, so a panel tears once until repaired. **What is still owed is the look:** watch one panel cross its tear threshold and judge whether the flung debris reads as a piece of that panel or as generic flakes, and what visibly changes on the airframe. The other three are now confirmation, worth capturing on the same take if the framing allows but not worth a dedicated sortie | `BL-297` |
| `CAP-47` | A gasbag burning out, CM14 (C2B/M04) | In the original, fly Clash of Dreadnaughts and set the Gemini's gasbags alight, holding one bag in frame from ignition through to whatever ends it, close enough to read both the fire and the envelope under it. *Look for:* what a finished gasbag looks like (a collapsed or missing envelope, a scorched one that stays, or a fire that simply stops), and what the zeppelin does once **three of its five** have finished, which is the authored death gate (`all_gmzep_gasbags` carries `MINIMUM_TO_SATISFY 3` over the five `finish_gmzepgasbagN` animations). Qualitative only: no burn duration is read off this clip as a constant | `BL-639` |
| `CAP-55` | The Gemini's own death and where its wreck rests, CM14 (C2B/M04) | In the original, fly Clash of Dreadnaughts and torpedo three of the Gemini's five gasbags down **without destroying any left broadside bay**, then hold the hull in frame from the first bag falling through to where the wreck settles and stops moving. *Look for:* (a) how many gasbags separate and fall, counted against the hull (ours sheds five, and the eye agrees with the data), and whether every one of them stays on the water, since ours drops the front and back bags through it (`BL-698`); (b) whether the gondola/underside disappears as it settles (`breakunder` switches `underneath` inactive); (c) where the hull itself stops, on the water or in the air, since ours halts it some 47 m over the sea when the sections break away, and the waterline against the three left broadside bays and their hatches if it does come down; (d) a gun burst fired into whichever bay sits nearest the water, held long enough to see whether it takes damage; (e) the pause-screen objectives readout before and after that burst. Qualitative only: no distance or rest height is read off this clip as a constant. | `BL-698` and `PT-119` (b) and (d): whether the original leaves a bay unreachable at all, where ours no longer needs one, since the hull's own death demolishes every bay |
| `CAP-57` | The Barracuda taking flak rockets on the hull, CM04 (C3/M03) | In the original, fly the mission to the submarine and put three flak rockets into its hull from outside the hangar, external view, the hull and the hangar mouth both in frame; then, whether it survives or not, keep firing into the open hangar and count what it costs there. *Look for:* whether the hull takes damage at all (a hit effect, a damage readout, the mission's voice), and how many rockets into the hangar mouth end it, against the six the authored 200 HP pool and flak's 35 per hit predict. The data is settled and the remake follows it: the pool sits on the hangar doors, not the hull, and a round anywhere else on the boat costs it nothing. This take is the check on that at the controls. Qualitative on the hull, a count on the hangar; no hit-point figure is read off this clip | Nothing tracks the outcome; a hull that takes damage in the original, or a hangar count that is not six, mints a new `BL` |

### AI flight, an AI aircraft flying itself, external view

| ID | Capture | What must be in frame | Unblocks |
|---|---|---|---|
| `CAP-54` | The **same aircraft lit by night and by day** in the original, one airframe and one livery | Two matched-pose chase-view stills of **one** aircraft in **one** livery: a night mission and a day mission, framed the same way, with the plane large enough in frame to read its lit and shaded sides and with terrain visible beside it for a reference level. C1B IA1 by night against a C1C campaign mission by day is the pair the remake needs, and **C1C has no capture at all today**. The existing stills cannot answer it: `playtest/CAP-11/`'s frames and `OriginalScreenshots/C1B IA1 Bloodhawk tracer and ejection.png` show a red Bloodhawk at night and a red plane over C2 by day, but CAP-11's measurement boxes deliberately avoid the aircraft, and our own build flies a grey and yellow paint at those poses, so nothing is comparable. Pin the same paint in our shot when the take exists. *Look for:* how much darker the airframe reads at night than by day relative to the terrain under it, and whether the plane's shaded side goes to black or keeps a fill | `BL-332`'s two TUNE constants and the faithful path's colour-sourced ambient fill, both landed and neither judged at the controls |

---

## 1 · Actionable now (`PT-nn`)

### C1 · Campaign, the mission-end result carry (`BL-622` `B12`)

```powershell
./RunGame.ps1
```

- `PT-88` `[Own]` **B12: the mission's result reaches the launchscreen side intact.** From the
  menu, start or continue a campaign profile and fly CM01 (C1's first mission) once to a win and
  once to a loss (bail or let the wingman/objectives fail it). *Look for:* the console prints
  `campaign: <Outcome> — returning '<profile>' to the cabin` when the world leaves, then
  `campaign: <Outcome> — arrived at the debrief with '<profile>'` one frame later at the
  launchscreen side, with the same outcome in both lines. A mismatch or a missing second line
  means the mission-end result carry is broken; what the debrief screen itself shows is `PT-89`.
- `PT-89` `[Own]` **C17/D18/D19/D20: the scrapbook opens on the flown mission, not the cabin, with
  its own shipped scraps under the stat card, each one opening into its own detail view, and the
  page/mission arrows and Current Mission bookmark browsing the rest of the book.**
  Continuing the same flights, after each ending's hold the screen that opens is the scrapbook
  (`SB_BackGround` behind a stat card), not the cabin. *Look for:* the outcome line and the four
  rows read the just-flown attempt (win → Mission Completed, loss → Mission Failed, with that
  flight's own time/hit-ratio/cash), any airframe downed carries a kill stamp with its name and
  count, mission-specific scraps (CM01: a coin, a magazine and a newspaper masthead) show behind the
  stat card at their own positions, stepping the cursor onto one of those scraps and confirming
  opens a full-screen detail view (a family background, the scrap's own picture, and up to three
  lines of raw `IDS_SB_...` text since the original English was never shipped) that RETURN closes
  back to the same page, the forward arrow steps onto CM01's second spread (its story page) and
  keeps stepping into later missions' own pages, the back arrow returns the same way and offers
  nothing at the very front of the book, stepping away from the flown mission's page shows a Current
  Mission bookmark that jumps straight back to it and disappears once there, REPLAY MISSION acts on
  whichever page is currently shown rather than always the flight just flown, fly the win case, let
  Next Mission's position advance on the cabin, browse back to the flown mission with the bookmark,
  then confirm Replay Mission still targets it and not the new current one, and RETURN TO CABIN
  lands on the cabin with that session's own state (funds, Next Mission). A wrong mission on Replay,
  stale numbers, a missing stamp or scrap, a scrap that will not open, an arrow or the bookmark
  appearing where it should not (or not appearing where it should), or landing straight on the cabin
  means the debrief screen is broken.
- `PT-120` `[Own]` **The pad sitting: three axis-capture constants, the look stick's own filter and
  the rumble, all judged with a real pad in hand (`BL-693`).** `ControlCapture.RestBand` 0.25, `MoveThreshold` 0.6 and
  `CapturedDeadzone` 0.5 decide whether a stick or a trigger a player pushes becomes a binding, and
  none of them has a decode behind it: the original cannot bind an axis to a command at all, so there
  is nothing to match. The rumble does have one, the original's own effect table
  (`docs/org/input.md`), but a magnitude that is right on the hardware the original wrote for is not
  automatically right on a pad's two motors. Reach the rebinding screen through Options, bind a
  flight action to a stick direction and to a trigger, then fly the result: guns, a rocket, a
  torpedo, the nitro, a round taken, a contact and a dive past the rated maximum. *Look for:*
  - (a) 0.6 (`MoveThreshold`) reads as a decisive push rather than a nudge: a deliberate deflection
    captures, a brush past does not;
  - (b) 0.25 (`RestBand`) forgives the stick your pad actually rests at, so a drifting centre never
    latches a binding on its own;
  - (c) in flight, the bound half of the stick at 0.5 (`CapturedDeadzone`) feels like a button, on
    and off, with no dead patch that reads as a broken binding;
  - (d) each rumble reads as its own weight against the others (a torpedo heavier than a rocket, a
    rocket heavier than the rear mount, a collision heaviest of all) and none lingers past the event
    that caused it;
  - (e) the sustained pair, the overspeed rattle and a turret gunner firing, hold while their cause
    lasts and stop when it does, without stuttering at the refresh seam;
  - (f) the Game Options Rumble row turns all of it off and back on, and the choice survives a
    restart;
  - (g) the look stick is steady: in snap mode (`K`), with the right stick held a little off centre,
    first in the chase view and then in Cockpit, the view sits still instead of shimmering, a small
    push still takes it where you meant, and letting go returns it at the speed it always did.
    `HeadLook.PadAimSmoothRate` 25/s (a 40 ms lag) and `PadAimCentreBand` 0.02 are the two to raise
    if it still shimmers and to lower if the view feels like it is catching up with your thumb;
  *Blocks:* `BL-693`. ⚠ Capturing the right trigger takes it from both Camera Boost and Camera
  Dolly Out rather than stacking a third reading (`ActionMap.SameControl` ignores the deadzone on
  purpose): that is not a fault of these three numbers and must not be tuned against.
  ⚠ The rumble carries no direction and no camera shake of its own. Both are deliberate
  (`docs/org/input.md`) and neither is a gap to report.

### C1 · Bloodhawk, the overcast sky, ground to above the deck

```powershell
./RunGame.ps1 --plane=player_bhawk --chapter=C1
```

- `PT-147` `[A/B: playtest/CAP-12/]` **The cloud field's view-angle fade, flown through the deck
  and above it.** Each `fvol` sprite's draw distance is now scaled by the cosine of the viewing
  angle against its polygon's normal, so the field is a disc around the camera rather than a flat
  3,500 m wall, and the cards render the authored colour with nothing scaling it. The instruments
  settle the arithmetic (the fade matches the decoded law, and no pinned shot moved); what they
  cannot settle is whether the thinned field reads like the original at the controls.
  *Look for:*
  - (a) climbing from the ground through the deck, the cards thin out ahead of you rather than
    ending at a rim, and the deck sheet and horizon behind them read the way CAP-12's takes do;
  - (b) at the grazing pose just above the band (around 1,200 m), the field is a small disc and
    the fogged deck shows through beyond it, the near-saturated wall of white being gone;
  - (c) at the 1,700 m rung the cloud tops sit at the original's brightness rather than above it;
  - (d) flying level inside the band, no popping as a sprite's own band swings across the cull.
  *Blocks:* nothing tracks the outcome; a fail mints a new `BL` naming which of (a)-(d) failed.

### C1 · Bloodhawk vs AI, the kill sequence, sound on

```powershell
./RunGame.ps1 --chapter=C1 --plane=player_bhawk --ai=player_fury --ai-attack=9 --volume=1.0 --no-det
```

⚠ `--volume=1.0` is not optional: the default master volume is 0, so a run without it is silent for
reasons that have nothing to do with any of these checks.

- `PT-80` `[Own]` **Damage stages read HEALTH only, and armour hides nothing behind it.** The
  original divides health alone at both the def level and the per-part level, armour never entering
  either quotient, and a part whose armour still covers the hit takes no health damage at all, so an
  armoured zone should cross no threshold whatever.
  *Variations:* take the fire rather than give it,
  `--ai=player_pfighter,player_pfighter --ai-attack=9`.
  *Look for:*
  - (a) nothing at all shows on a zone while its armour is still absorbing, the 0.99 spark shim
    included; that shim going quiet on early hits is the most visible change here;
  - (b) a panel tears only once that zone's own health crosses its threshold, not before;
  - (c) `player_damage_trail` starts when the hull gauge reads about 10%, not earlier.
  *Blocks:* the owed flight A/B for the staging scope and pool fixes. ⚠ The gauge dial is a separate
  scale and is *correct* on combined armour+health, so a dial that disagrees with the staging here
  is not a fault.

- `PT-84` `[Own]` **A graze no longer drops the engine to a sputter.** The engine slot swaps to
  `snd_damagedengine` only once the worst zone falls below a quarter of its health, which is the
  original's own threshold and not a tuned one, so light damage should sound completely healthy and
  the swap when it comes should be a hard cut rather than a fade.
  *Variations:* take a beating rather than give one, with
  `--ai=player_pfighter,player_pfighter --ai-attack=9 --volume=1.0`. To hear the two states back to
  back without combat, `--fly --damage=nose:0.30` then `--fly --damage=nose:0.20`.
  *Look for:*
  - (a) the first hits, and a scrape along a cliff, leave the engine note untouched;
  - (b) once a zone is deep into the red the loop changes to the rough one and holds, at one pitch
    rather than drifting;
  - (c) that rough loop sounds the same on every fresh flight, and reads as an engine running
    rough rather than as a slowed-down recording.
  ⚠ The original refuses the pitch write on this definition, so the loop runs at its own rate and
  the drawn multiplier is computed but never heard. A damaged note that differs from flight to
  flight is a fault rather than the draw.
  *Blocks:* nothing; a fail on (a) says the gate reads the wrong pool, a fail on (b) says something
  is re-evaluating the swap per frame.

- `PT-81` `[Own]` **A repair retracts the stage it lifted back over (F19 damage lab).** Staging used
  to latch one way; the original stops an entry's anim and clears its handle on the upward crossing.
  This one needs no AI, so it flies on the bare command:
  ```powershell
  ./RunGame.ps1 --chapter=C1 --plane=player_bhawk
  ```
  *Look for:* open the F19 damage lab, drag a zone down past a threshold to start its stage, then
  repair back above it and watch the stage stop. Un-staging on repair is faithful, not a regression.
  *Look for also:* it must fire ONCE per downward crossing. A stage that re-fires while the fraction
  merely stays below its threshold is a different bug, not this fix working.
  *Blocks:* the owed lab check for the staging lifetime fix.

- `PT-82` `[A/B: OriginalScreenshots/Videos/Bloodhawk Dive Sound.mp4]` **The engine note moves with
  the stick and with the climb, not with the throttle alone.** Two decoded terms landed on the engine
  slot: an unsigned transient on pitch and yaw rate, and a level that falls through a dive and rises
  in a climb. The note was previously a function of the throttle lever and nothing else, so at a
  fixed throttle it was flat whatever the aircraft did. This one needs no AI:
  ```powershell
  ./RunGame.ps1 --chapter=C1 --plane=player_bhawk --volume=1.0 --no-det
  ```
  *Look for:*
  - (a) hold the throttle fixed in level flight and pull, then push. The note rises **both** times.
    That it rises on a push is the surprising half and is what the decode says;
  - (b) a sustained vertical dive at fixed throttle sits about 6% below level, and returns to level
    when you pull out rather than staying down;
  - (c) roll hard at fixed throttle. The note must **not** move at all: the original drops the
    nose-axis rate, so a barrel roll is silent where a pitch input is not.
  *Blocks:* nothing tracks this; it is the last thing owed on the engine note now that the mechanism
  is decoded and pinned (`git log --grep=BL-109`, `--grep=BL-423`). The numbers already match the
  original to within a third of a point, so a fail here is about whether it READS right, not whether
  it matches. ⚠ Do not ask for a re-tune of the coefficients on a listen: they are read from the
  executable, and `docs/verification.md`'s rule that a recording may not contest a decode applies to
  the ear too.
  ⚠ **This is probably what the retired `BL-123` was hearing** (`git log --grep=BL-123`). That item
  A/B'd our dive whine against the original and reported it "close, but could be a bit louder",
  which read as a mix problem. The original has no whine at all, so what the ear was matching in
  that dive was the engine slot's own movement, and these terms are what produces it.

- `PT-133` `[Own]` **The HUD's reading box is the right width at the controls on a 32:9 screen.**
  The dials, the SPD/ALT/THR block and the pause screen's objectives panel measure from a 16:9 box
  centred in the pane rather than from the pane's own edges, that box is the frame every one of
  those offsets was measured in (`HUD.png`, 2556x1440). Whether the width reads right on a
  5120-wide screen, where the two columns end up 1280 px in from each edge, is a judgement no
  instrument makes. Fly it with the window fullscreen (Options → VIDEO → Display Mode) so the pane
  is the whole screen.
  *Look for:*
  - (a) the dials and the status block sit within comfortable reading width, neither out at the
    edges of peripheral vision nor huddled into the middle;
  - (b) an off-screen target's edge arrow still comes from the TRUE screen edge and points
    usefully, that element deliberately keeps the pane, since the box is not where the target
    left;
  - (c) nothing has moved at 16:9: the same sortie windowed looks exactly as it did.
  *Blocks:* nothing tracks the outcome; a fail mints a new `BL`. The per-element choice and the
  one-pixel tolerance are in the landing commit (`git log --grep=BL-778`).
- `PT-174` `[Own]` **The original's keyboard table is under the hand on a fresh profile.** The flight
  defaults are the original's decoded 64-entry table (`docs/org/input.md`, `git log
  --grep=BL-901`): digits are the throttle eighths, targeting sits on E, W and R with Shift and
  Ctrl as modifiers, Q and T nearest and clear, F8 the cockpit views. The table is pinned by unit
  test and seen only on the Controls tabs; no sortie has been flown on it. Start a new pilot in
  Player Setup so no saved bindings apply. *Look for:*
  - (a) `1`..`9` set the throttle eighths and `=` / `-` step it, with nothing else on the digits;
  - (b) E, Shift+E and Ctrl+E cycle enemy targets next, previous and nearest, W and R the same for
    allies and non-aircraft, Q the nearest of anything and T clear, and a bare E does nothing while
    Shift is held;
  - (c) Space and X fire, Shift+S opens the spyglass, and F8 cycles the cockpit views to chase.
  *Blocks:* nothing tracks the outcome; a fail mints a new `BL` item.

### C1 · two pilots, Dogfight (splitscreen VS)

```powershell
./RunGame.ps1 --vs --players=2 --chapter=C1
```

- `PT-52` `[Own]` **The puffer distance fade in two panes (`BL-339` landed 2026-08-15).**
  The fade now runs its bands against every pane's camera and each particle takes the most
  favourable pane's alpha, so a trail near player 2 draws in player 2's pane. What no instrument
  here can judge is the remaining divergence: one alpha per particle for the whole world, so a pane
  can see a puff its own camera would have faded further. A scripted shot cannot set this up,
  there is no per-player placement flag and no scripted fire, so both panes spawn near-coincident.
  *Launch:* `./RunGame.ps1 --fly --players=2 --chapter=C3` (plain 2-pane free flight, two pads or
  pad + keyboard), the section's Dogfight launch above works too if a target is wanted.
  *Look for:* (a) the reported repro is gone, P2 astern of P1 fires a rocket past him and sees the
  whole trail, not just the stretch beside P1; (b) neither pane shows a puffer popping in or out as
  the OTHER player turns or flies away (the shared-alpha tell); (c) flying through an emitter still
  culls it in the pane that flew through it rather than filling that screen.
  *Blocks:* the fidelity verdict for the nearest/union boundary rule, per-pane alpha (one MultiMesh per pane) is
  reached for only if (b) visibly fails, and a fail mints its own `BL` item.
  *Variations:* C3 (`--chapter=C3`, the waterfalls' `spew_puffer` is the tightest authored band);
  `--players=4` for the same question with four alphas competing.
  *Also carries B12 (`BL-340` landed 2026-08-15):* the `FBFX_COLOR_FROM_TO` screen wash now paints
  only the panes whose camera is inside the burst's authored 100 m radius, and the same missing
  levers (no per-player placement, no scripted fire) keep it off the scripted path. In the same
  session: put P2 over the ground alone and have him rocket the terrain, P2's pane flashes
  white/violet and P1's, a few hundred metres off, does not; then fly the pair in together and both
  flash. A wash that still paints all panes, or one that paints none, is the failure.

- `PT-134` `[Own]` **A 2-player split on an ultrawide window reads as two normal views, and the HUD
  size the flip brings with it is right.** The split stands the panes side by side once each half
  would still be at least as wide as it is tall, a window 2:1 or wider, so a 32:9 screen gives two
  16:9 panes. The pane share reaches `HudMetrics.PaneFactor` through the HEIGHT ratio, so a
  full-height pane's factor is 1 and the HUD draws at single-player size rather than the damped
  splitscreen size. That size change is the part to judge; run it fullscreen on a 2:1-or-wider
  screen.
  *Look for:*
  - (a) two panes side by side, each reading like an ordinary single-player view;
  - (b) the full-size HUD in each pane reads as correct rather than oversized;
  - (c) dragging the window back under 2:1 flips it to stacked while the sortie runs, and back;
  - (d) the plane select before the sortie splits the same way the flight then does.
  *Blocks:* nothing tracks the outcome; a fail mints a new `BL`. The threshold's reasoning is in the
  landing commit (`git log --grep=BL-777`). A stacked pane's first-person views hold their vertical
  angle and widen horizontally like any other viewport, so a cropped cockpit in one is a new fault
  rather than a known one.

- `PT-152` `[Own]` **A respawn puts a downed pilot away from whoever just killed them, without
  becoming a place the killer can wait at.** The spawn rotation picks the point that is roomiest and
  furthest from the killer, weighted by two constants that are judgement rather than a decode:
  `RoomyShare` 0.6 and `KillerWeight` 2. Nobody has judged them at the controls. *Launch:* the
  section's Dogfight launch above, two pilots. *Look for:* camp one spawn, kill the other pilot
  there, and say whether the respawn reads as away from the camper; then repeat from the same camp
  and say whether the sequence of spawns becomes predictable. *Blocks:* nothing; a retune is the two
  constants, a fail mints its own `BL`. The playtest steps the item wrote are in its landing commit
  (`git log --grep=BL-301`).

### C1 · two pilots, stunt race (splitscreen starting grid)

```powershell
./RunGame.ps1 --stunt --players=2 --chapter=C1
```

- `PT-45` `[Own]` **The abreast race starting grid** (landed 2026-08-08;
  it closed `BL-084`, whose record is in that commit, `git log --grep=BL-084`).
  Two pads (or pad + keyboard); menu path: Stunt → C1 → both press Start. Splitscreen stunt racing is
  our invention, the original had no splitscreen at all, so every call here is a judgement on our
  own remake, with no reference to A/B against.

  **Why this sitting is the only evidence there will ever be for a race.** On the race path the grid
  is selected only when a session is an actual race, and a `--det` run is explicitly given the old
  per-player spawn walk instead, so no scripted run, screenshot or golden can exercise the race
  path. That is by design: the bypass is what keeps every scripted race spawn byte-identical. (A
  multiplayer campaign mission takes the same grid under `--det` as well, so a scripted campaign run
  does place a field with it; that is a different caller and settles nothing about a race start.)
  The grid geometry is also not
  photographable: the panes are chase-cam only, so at the default 60 m spacing your neighbour sits
  outside your own frustum. **Read the geometry off the console instead**, every launch logs one
  line per slot, e.g. `spawn [P1 grid slot 1 of 4] pos=(-4974,260,-3771) heading=90° spacing=60m
  lift=81m`, with the anchor's own line above them. On C1 with `--spawn=0` the field lifts 81 m.

  **Both numbers are live config, and settling them is the point of this sitting.** `slotSpacing`
  (default **60 m** between neighbouring slots) and `groundClearance` (default **100 m** of air the
  lowest slot must have under it) are read from `config.json` as `startGrid.slotSpacing` and
  `startGrid.groundClearance`, listed by `--dump-config`, and take effect on the next launch with no
  rebuild. Neither is a finding, 60 m is just the figure already in the tree, so dial them between
  launches until the start looks right and record what you landed on.
  *Look for:*
  - (a) **does it read as a starting line**, at the moment of spawn, does the field feel like a
    grid you are lined up on, at 2 panes and at `--players=4`;
  - (b) **spacing at the wingtips**, 60 m: too far apart to feel like a race start, or too close
    for comfort in the first seconds of manoeuvring? Try 30 m and 100 m before deciding;
  - (c) **the uniform lift**, the whole field rises together by whatever its worst slot needs, so
    over broken ground it can look absurd (the field hovering high over a valley) or, if clearance
    is dialled too low, too tight (an outer wingtip in a hillside). Watch an outer slot, not P1;
  - (d) **a felt end-of-grid advantage**, do the outer slots feel meaningfully better or worse than
    the middle for reaching the first Danger Zone? Slots are fixed by player index today; a *felt*
    bias is the trigger to randomise the slot order per race (not to rotate it per rematch);
  - (e) **the anchor still varies**, relaunch a few times without `--spawn=`: the whole grid should
    sit somewhere else each time (the anchor is a random pick from the mission's spawn list), not on
    the same point every launch;
  - (f) **`--pos` still wins**, `--pos=x,y,z` must still place the field where you asked, grid or
    no grid, since the override is resolved beneath the grid rather than beside it.

  *Blocks:* the two config values in (b)/(c) hardening from fallbacks into decisions; the
  slot-rotation call in (d); and `BL-314`, the race countdown, which must not be started until the
  grid it counts down over has been flown. A structural fail, a plane in terrain, a field that is
  not level or not on one heading, mints its own `BL` item.
  *Variations:* `--players=4` for the case (a)/(b)/(d) are really about; `--chapter=C2` for a
  different terrain profile under (c).

### C1 · two pilots, the victim-routed screen wash

```powershell
./RunGame.ps1 --coop --players=2 --chapter=C1 --debug-wash=2
```

`--rocket=<wep_id>` (used here and below) swaps every pylon to the named type. All 11 stock
loadouts fit HE `wep_06`, so a flag is what pins a type here rather than the menu's own Ammo
Selection screen: the flag beats a menu-chosen fit deliberately, so a row that names a type gets it
whatever was clicked on the way in. The rocket trigger is **F**, one round per pull.

⚠ **Every figure quoted in this section and the ones below is a decoded constant or an authored
value.** A clip that disagrees with one is evidence about our implementation, never a correction to
the constant: a decode is not contested with a measurement read off a running picture
(`docs/verification.md` DET-12).

- `PT-74` `[Own]` **The wash is addressed to the viewer who was hit, and blends
  (the routed wash contract).** The original holds one wash state for the whole
  machine, which would blind viewer 1 when viewer 3 is flashed; ours routes by the victim's own pane
  and composites over the existing proximity ramp instead of replacing it. `--debug-wash=N` fires
  two overlapping scripted washes at viewer N (red at weight 1 for 5 s on the first frame, then
  white at weight 0.5 two seconds in).
  *Look for:*
  - (a) **routing**: pane 2 washes red, then pink as the white lands on it, and pane 1 stays clean
    throughout;
  - (b) **the ramp is untouched**: with `--rocket=wep_06` and no `--debug-wash`, an HE burst still
    washes by camera proximity exactly as it did, both panes flashing when both are near and only
    the near one when they are apart. Take that baseline **before** judging anything else in this
    section;
  - (c) **a real hit routes the same way**: with `--rocket=wep_08`, a pilot caught in the **other**
    pilot's sonic burst washes their own pane alone. (A pilot caught in their own burst is washed
    too, as in the original, whose self-hit guard exempts only the damage pair.)

  *Blocks:* nothing tracks the outcome; (a)/(b) passed at the controls and only (c) is still owed.
  a fail on routing mints a new
  `BL`.
  *Variations:* `--debug-wash=3` in a two-pane session, which answers to no pane and must paint
  nothing at all.

### C1 · four pilots, the four-viewer ordnance pass

```powershell
./RunGame.ps1 --coop --players=4 --chapter=C1 --rocket=wep_08 --infinite-ammo
```

Four viewers is the case the wash channel was designed against: the original routes its wash
through a single global and that does not survive four panes. ⚠ `BL-389` already reports that the
splitscreen weapon mix wants a retune, with rockets too quiet against guns and worst with four guns
firing at once. **A mix problem is not a behaviour problem**: judge what happens, and file loudness
against `BL-389` rather than against the wash routing.

- `PT-75` `[Own]` **The disabling types with four viewers on one team.**
  *Look for:*
  - (a) **two viewers washed in the same second** carry their own wash each, with the other two
    panes clean, and neither washed pane is brighter or shorter for having a neighbour;
  - (b) **a smoke screen laid by one pilot** stuns and washes only the pilots actually behind it,
    and the layer's own pane stays clear;
  - (c) **`--rocket=wep_12`**: a choked pilot's own pane shows nothing at all (the choker has no
    wash), and only their thrust goes;
  - (d) **the blast still reads**: HE bursts near two panes at once still stage destructibles the
    way `PT-65` judged them with one viewer;
  - (e) frame cost holds up with four panes and a dense burst (`--debug-fps`), since every splash
    candidate now costs a cover ray.

  *Blocks:* nothing tracks the outcome: a fail on routing mints a new `BL`; a fail on loudness is
  `BL-389`.

- `PT-76` `[Own]` **The same pass under Dogfight rules, four viewers hostile.** `--coop` puts every human on one team, so the beeper's hostility
  gate and the AI-side effects never fire between players there. Dogfight makes them mutually
  hostile, which is the only way to judge the tag gate and a human-on-human paint.
  *Launch:* `./RunGame.ps1 --vs --players=4 --chapter=C1 --rocket=wep_10 --infinite-ammo`
  *Look for:*
  - (a) a beeper into a hostile human paints them and deals nothing, and a second beeper into an
    already-painted pilot neither re-tags nor refreshes the first;
  - (b) stepped to `wep_11`, a seeker follows the painted pilot across all four panes and its trail
    draws in every pane it passes;
  - (c) `wep_08` and `wep_09` between hostile humans wash the struck pilot's pane alone, with the
    facing rule still holding for the flash;
  - (d) the match keeps scoring normally: none of the no-damage types registers a hit or a kill.

  *Blocks:* nothing tracks the outcome: a fail mints a new
  `BL`.
  *Variations:* `--players=2` and `--players=3` for the intermediate pane counts, which is where a
  routing off-by-one would show.

- `PT-129` `[Own]` **Effect pools with four pilots all firing (`BL-537`).** The single-player half
  reads right at the controls: four fireballs burn out in place, and the seven-death wrap is not
  visible under the debris. Owed is the four-player case, where the default root wants more slots
  than it gets. *Look for:*
  - (a) anything that reads as shared between panes: an effect cut short in one pane as another
    starts, or a burst that appears in the wrong one;
  - (b) simultaneous deaths across panes, which is where `flame_ball_01` wraps;
  - (c) the instrument alongside the flight: the `anim: effect pool for '<name>' recycled slot`
    DEBUG line in the log file sink names any root that actually wrapped.
  *Blocks:* `BL-537`. ⚠ Raise only a root that logs a recycle, never the default; the three gun
  roots stay at 1, and a root sized 0 clamps to 1.

### C1 · four pilots, Dogfight without the warning-shot shield

```powershell
./RunGame.ps1 --vs --players=4 --chapter=C1
```

- `PT-153` `[Own]` **A four-pane Dogfight carries no warning-shot shield, and whether that reads
  sharp or fragile is the judgement.** The shield that absorbs an AI's first warning shots is the
  session's rule: solo flight keeps it, co-op shields every human pane, and a Dogfight shields
  nobody, which is where the original's own networked-session gate lands (`git log --grep=BL-834`).
  No A/B exists, since the original has no splitscreen; the original's rule prescribes it and
  nothing is filed unless the feel says otherwise. Fly a four-pane match to a few kills. *Look for:*
  (a) the first burst from another pilot lands as damage at once, with no free pass; (b) whether
  the fight reads as sharper for it or as fragile, with a pilot dead before they could react;
  (c) nothing else changed: guns, rockets and the kill order all as in a two-pane match.
  *Blocks:* nothing tracks the outcome; a "fragile" verdict mints a new `BL` naming what the
  Dogfight should shield.

### C4 · two pilots, Instant Action stunt run

```powershell
./RunGame.ps1
```

- `PT-173` `[Own]` **A stunt run through every marker photographs each one, and any landed
  photograph opens full size on both presentations.** One pilot is enough. Menu path: Instant
  Action → C4 → Stunt Flying, then the same run with `--presentation=original`. The photographs
  and the grid are proved by suite on a synthetic run where every marker is latched from a parked
  aircraft (`git log --grep=BL-256`, `git log --grep=BL-970`); no human has flown the markers or
  opened a photograph. *Look for:*
  - (a) each marker crossed at speed takes one photograph with its sting, and the C4 run's 14
    thumbnails wrap into a grid on the results board rather than one shrunken row;
  - (b) on Built-in, up from Photo Mode enters the grid, arrows walk the landed cells, confirm
    opens the photograph full size and Esc or pad B closes it with the cursor on the same cell;
    mouse hover and click do the same;
  - (c) on Original, the prints stand as rows after CONTINUE, a landed one opens in the
    presentation's viewer and Back closes it with focus on that print;
  - (d) a marker entered during the 3 s hold after the finish takes no photograph.
  *Variations:* `--presentation=original` for (c). *Blocks:* nothing tracks the outcome; a fail
  mints a new `BL` item.

### C1 · Instant Action, Dogfighting an Ace, sound on

```powershell
./RunGame.ps1
```

- `PT-158` `[Own]` **The ace talks: its attack line, its damage calls and its death cry**
  (it closed `BL-977`, `git log --grep=BL-977`). Menu path: Instant Action → C1 → Dogfighting an Ace.
  Let the ace commit to you, then shoot it down over a few passes. A scripted run shows the ace's
  own clips (VO id 29, accent 24) prewarmed and its forced death cry resolving to a streamed clip;
  what no instrument shows is hearing it. Each line plays flat on the radio channel, so distance
  does not matter.
  *Look for:*
  - (a) a line from the ace as it commits to you;
  - (b) one or more distress calls as its health drops past 70, 50 and 30 %;
  - (c) a death cry as it goes down;
  - (d) in the session's log under `.scratch/logs/`, no `owns the clips but none was prewarmed`
    warning for `ai1_player_peacemaker`.

  *Blocks:* nothing tracks the outcome; a silent ace with that warning absent mints a new `BL`
  naming the trigger that stayed silent.

### CM01 (C3/M01) · two to four pilots, join, flight check, death and skip

```powershell
./RunGame.ps1
```

Menu path: Mode → Campaign → create or select a profile → join two to four humans → the seated
pilot's own briefing and flight check, then `FLIGHT CHECK P2`/`P3`/`P4` in the same window → FLY
MISSION. A fresh profile's first entry is CM01, so this is also where the sortie itself is flown.
Splitscreen co-op is our own invention (no networked original to A/B against), so every call here
is a judgement on our own remake.

- `PT-90` `[Own]` **Joining from the roster screen and the sequential flight check.**
  *Look for:*
  - (a) Start on an unclaimed pad joins a guest from the roster, the briefing and the flight check
    alike, with the player chip strip naming everyone who has joined;
  - (b) FLY MISSION on the seated pilot's own check opens `FLIGHT CHECK P2` rather than launching,
    and does the same for P3 and P4 as each joins, one window, one player at a time;
  - (c) FLY MISSION on the LAST joined player's check is what actually launches the mission;
  - (d) B on a guest's pad drops that guest back out (roster, briefing or flight check alike)
    without disturbing P1's own flow, which keeps its ordinary Back/Next meaning throughout.
  *Blocks:* nothing tracks the outcome (`C21`/`C22` landed on screenshots and code review alone,
  with no scripted-input driver for a pad press on a menu screen): a fail mints a new `BL`.

- `PT-92` `[Own]` **A downed human spectates, and the last one lost ends the mission.**
  Fly two humans into CM01 and crash one, into the sea or a hillside, `R` to restart the pane if
  the first attempt is too gentle to register as a loss.
  *Look for:*
  - (a) the downed human's pane switches to an orbiting spectator camera on their own wreck, not a
    frozen frame or a black pane, and the other pane keeps flying with its own HUD untouched;
  - (b) that spectating pane takes its own pad's input for the orbit, so a second downed human (at
    three or four players) does not orbit in lockstep with the first;
  - (c) crashing the LAST human still flying ends the mission on a loss the instant no wreck is
    still falling, that human is not handed a camera, since there is nothing left to watch;
  - (d) `--no-crash-loss` (`--campaign=<profile>:0 --players=2 --no-crash-loss`) turns all of this
    off together: a crashed human keeps flying, no pane is taken and nothing ends.
  *Blocks:* nothing tracks the outcome (`B13` landed on an engine suite alone, with no scripted
  input to force a specific human's crash headlessly): a fail mints a new `BL`.

- `PT-93` `[Own]` **A cutscene fills the window, and the skipper is named.**
  CM01's own mission intro plays fullscreen on launch. Two pads (or pad + keyboard); have the
  SECOND player skip it, then relaunch and have the first player skip instead.
  *Look for:*
  - (a) the intro collapses to one pane filling the whole window, letterboxed, with no gutter and
    no second pane drawn behind it;
  - (b) any key or pad button (not Escape) skips it, and the on-screen name is the SKIPPER'S,
    in that player's own colour, P2's skip must never read as P1's;
  - (c) the skip (or the intro's own end) restores every pane and HUD exactly as they were before
    the collapse, with nobody's pad input lost in the process.
  *Blocks:* nothing tracks the outcome (`B14` landed on an engine suite and a code-review pass at
  the controls, with no scripted-input driver for a pad-button skip): a fail mints a new `BL`.

### CM02 (C3/M05) · guest capture, the Balmoral wing-walk

```powershell
./RunGame.ps1 --campaign=<profile>:1 --players=2
```

- `PT-91` `[Own]` **Whichever human triggers the capture ends up in the captured aeroplane.** Fly both humans to CM02's wing-walk rescue, and have
  the GUEST (not P1) be the one to fly into the trigger.
  *Look for:*
  - (a) the guest, not P1, is the one re-flown into the Balmoral once the cutscene ends, the swap
    follows whoever triggered it rather than always landing on P1;
  - (b) the OTHER human's aircraft and position are undisturbed for the whole cutscene, not stacked
    onto the capture point;
  - (c) the newly-captured Balmoral shows its own hook and wing-fold choreography rather than
    Bloodhawk/Fury geometry left over from the airframe the guest flew in;
  - (d) relaunching and letting P1 trigger it instead puts P1 in the Balmoral and leaves the guest's
    own airframe alone, the same behaviour, the other human.
  *Blocks:* nothing tracks the outcome (`A4` landed on an engine suite alone, with no scripted-input
  driver to fly a human into a world trigger headlessly): a fail mints a new `BL`.

### CM14 (C2B/M04) · the Gemini, both kill orders

```powershell
./RunGame.ps1 --campaign=<profile>:13
```

- `PT-119` `[Own]` **The mission reaches an end whichever way the Gemini goes (`BL-694`).** The
  third primary is the only route to a win, and it counts five of the six `deploy_gmzep_lbroadNN`
  animations INVALID, which only a cannon bay's own destruction writes. Two authored chains supply
  them: a torpedoed gasbag demolishes its section's four bays, and the hull death demolishes every
  section that was not torpedoed. `gemini-gasbag-bays` proves both headless over three orderings,
  so this row is the confirmation at the controls that they arrive in a flown sortie. *Look for:*
  - (a) torpedo three gasbags with no bay shot at all: the Gemini dies and the third primary
    completes with it, rather than the sortie running on with nothing left that can happen;
  - (b) the reverse order, bays first and the hull afterwards, reaching the same end;
  - (c) the pause-screen objectives readout ticking primaries 1 to 3 as the bays go, and the
    mission then reaching its win through the docking;
  - (d) whether a bay under the water is still reachable, and whether it needs to be, since the
    hull death should already have taken it.
  *Blocks:* `BL-694`'s landing commit (`git log --grep=BL-694`). A sortie that reaches no end
  state mints a new `BL`; `CAP-55` is what the original owes against (b) and (d).

### The exported package · a recipient's first run, no arguments

```powershell
./ExportRelease.ps1   # then unzip .scratch\CSVM-v<version>-win64.zip into a bare folder
```

- `PT-131` `[Own]` **An exported build started by double-clicking `CSVM.exe` sounds, and the
  folder with no `extracted\` says so on screen.** The repo's silent master default is what keeps
  scripted runs quiet, so the export carries its own audible one; nothing in the payload passes a
  flag. Run the unzipped `CSVM.exe` from Explorer, with the extraction done and again with the
  `extracted\` folder renamed away. ⚠ Clear `CSVM_DATA_ROOT` from the environment first, or the
  exported build reads the development tree and neither case is what a recipient sees. *Look for:*
  - (a) with game data present, the menu comes up with its music and the menu cues audible at a
    sensible level, without a `--volume=` argument anywhere;
  - (b) with no `extracted\`, the no-game-data screen names `Extract.cmd` and stays up until Esc,
    rather than a menu over a world that cannot build.
  *Blocks:* nothing tracks the outcome; a fail mints a new `BL`. The export's own default is what
  is under test here; a player retunes the mix on Preferences' AUDIO page afterwards.

### The VIDEO page · a second monitor plugged in

```powershell
./RunGame.ps1 --presentation=original --menu=video
```

- `PT-132` `[Own]` **The Monitor row actually moves the window, and a saved index naming a screen
  that is gone falls back instead of opening nowhere.** The display settings landed on a
  single-screen machine, so the enumerated list, the resolve and the precedence ladder are proved by
  suite while the move itself has never been seen. ⚠ Launch without `--screenshot` or `--det`: the
  startup display apply stands behind `!_spec.IsScripted` and a scripted run reads no saved display
  setting at all, so either flag hides exactly what this item checks. *Look for:*
  - (a) the Monitor row lists both screens, one label per screen, each naming an index and a size
    that match the monitors actually attached;
  - (b) picking the other screen and pressing ACCEPT CHANGES moves the window to it, and the page
    redraws composed correctly at that screen's size rather than clipped or letterboxed;
  - (c) with the window on the second screen, Display Mode and Resolution still apply there:
    exclusive fullscreen fills the screen the window moved to, not the one it started on, and the
    Resolution row now offers that screen's own modes;
  - (d) the choice survives a restart, the window opening on the chosen screen with no arguments;
  - (e) with that monitor then unplugged or disabled, the next launch comes up on the remaining
    screen rather than erroring or opening off the desktop, since the saved index is the one
    setting that can name something absent.
  *Blocks:* nothing tracks the outcome; a fail mints a new `BL`. The reasoning behind the fallback
  and the apply order is in the landing commits (`git log --grep=BL-768`).

### The CONTROLS page · a pad plugged in

```powershell
./RunGame.ps1 --presentation=original --menu=controls
```

- `PT-172` `[Own]` **The pad glyphs on the controls page read as the original's.** The glyph set is
  keyed per control the way `BindingControl` is and swaps behind one seam, as the item asked, but
  it draws vector shapes in code (`PadVectorGlyphs`) rather than a texture set (`git log
  --grep=BL-878`); nobody has judged the look. Open the Original presentation's controls page with
  a pad connected and walk the pad column. *Look for:*
  - (a) every pad control shows a glyph, none falls back to a text label;
  - (b) the face buttons, bumpers, triggers, sticks and d-pad are told apart at a glance at the
    page's own size, with the weight and colour of the surrounding plaque art;
  - (c) a rebinding capture shows the new control's glyph as soon as it is pressed.
  *Blocks:* a pass closes the question; a fail mints a `BL` for a texture set behind the same
  seam (`ControlGlyphs.Set`), which is why the seam is kept.

### Any campaign mission · enemy skill under the difficulty offset

```powershell
./RunGame.ps1
```

- `PT-135` `[Own]` **Ordinary enemies fly to their authored ratings minus the difficulty offset,
  and aces to their ratings as authored.** The decode of the aiv `ace` flag landed the offset the
  original adds to every one of a hostile pilot's nine ratings before interpolation (-2 at the
  easiest tier, 0 at Normal's neighbour, +2 at the hardest) and the ace's exemption from it
  (`git log --grep=BL-496`); every suite reads the numbers, none judges the feel. Fly a mission
  with ordinary enemies and one that spawns an ace (CM02's Black Hat lead is one of the 26 flagged
  blocks) at the default tier. *Look for:* ordinary enemies turn, aim and evade a little worse than
  before the landing, an ace noticeably better than its wingmen, and a neutral (team 0) roster
  block, where one exists, unchanged either way. Enemies that feel wrong at the default tier, or an
  ace that feels no different from the rest, mean the offset or the exemption is mis-wired, since
  the ratings themselves are the roster's own. *Blocks:* nothing; a fail mints a new `BL`.

### Any campaign mission · the pause chart's icons against the compass

```powershell
./RunGame.ps1 --campaign=<profile>
```

- `PT-146` `[Own]` **The pause chart's plane icon points where the compass tape says, on every
  heading.** The chart's turn is the compass reading, and the player's own icon art is drawn an
  eighth of a turn counter-clockwise of the top of the sheet, which the sheet now takes back off
  (`git log --grep=BL-895`); a suite pins the agreement on seven headings, but only an eye at the
  controls says the drawn nose looks right on the drawn chart. Fly a mission whose chart shows the
  plane (CM05's window holds its own spawn), note the compass, then pause. *Look for:* (a) the
  plane icon's nose along the heading the tape read, the chart being north up; (b) the same after
  turning onto two or three other headings, so a sign error cannot hide at one pose; (c) the
  zeppelin icon, where the mission draws one, lying along the hull's own course. *Blocks:* nothing;
  a fail mints a new `BL`.

### CM07 (C1/M02) · the Danger Zone photograph in the scrapbook

```powershell
./RunGame.ps1 --campaign=<profile>:6
```

- `PT-154` `[Own]` **A campaign mission's Danger Zone photograph lands in the scrapbook.** The
  campaign camera is built and suited: crossing a zone stages a photograph of the aircraft, taken
  from ahead of it looking back as the original's is, into the profile under the scrapbook row's own
  `Snap_<mission>_<objective>` name, and a win keeps it while a loss drops it
  (`git log --grep=BL-256`, `CampaignSnapshot`, `DangerZonePhotograph`). It has never been seen at the controls,
  and the request was "should work in missions too for the scrapbook photos". CM07 authors four
  zones (objectives 18 to 21, `C1/M02/zrdr/dzones.zrd.json`). Fly through at least one zone, win
  the mission, and open the scrapbook. *Look for:* (a) the sting on the crossing and one hitch-free
  frame (a hitch there is a new `BL`); (b) the photograph on the mission's spread
  with its photo-corner mount, and the zoom showing the full 164x123 still; (c) a lost attempt
  leaving no photograph behind; (d) whether the framing reads like the original's photographs, a
  head-on view of the aeroplane from outside it. *Blocks:* nothing tracks the outcome; a
  missing photograph mints a new `BL`.

### CM07 (C1/M02) · combat voice on the radio, sound on

```powershell
./RunGame.ps1 --campaign=<profile>:6 --log=sound:debug
```

- `PT-161` `[Own]` **A wingman's combat call plays flat, like the scripted radio, and is heard.**
  Combat voice now speaks on the mission radio's queue instead of from the speaker's aircraft, as
  the original does (`git log --grep=BL-978`); the `ai-voice` suite pins a flat Voice-bus player
  at the def's authored level with the listener 5 km away, but only an ear hears a line. Stay in
  the fight beside the enemy flight for a minute or two, taking and dealing hits. *Look for:*
  (a) a wingman calling an enemy's clock bearing, centred and at one level wherever the wingman
  is, including far off; (b) damage calls as you and the enemy take hits; (c) no combat line
  cutting into a scripted line, a bark waiting up to 0.8 s and then dropped instead; (d) in the
  session's log under `.scratch/logs/`, an `ai voice: <name>: trigger #` line for each call heard.
  *Blocks:* `BL-934` (lines unheard at the controls); a silent sortie whose log carries the
  `ai voice:` lines is that item's next cause.

## Everything else

Everything blocked on an unlanded fix is tracked in [`backlog.md`](backlog.md) with its own
`*Playtest after fix:*` line. Do not re-add those here; the entry brings its own test when the
fix lands.
