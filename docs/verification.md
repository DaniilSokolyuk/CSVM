# Verifying a change in this project

This file contains transferable verification rules. Dated evidence belongs in commit messages,
analysis findings, or git history; a module constraint belongs as a comment on the member it binds,
and format or decode knowledge in `docs/formats/` or `docs/org/`.

Read **METHOD** first, then only the relevant section. Rule IDs are permanent: append new rules
and leave gaps when retiring old ones. Each rule is a bold one- or two-line imperative plus at most
one sentence of measured evidence; everything else belongs in the commit that landed it. A rule
that only holds for one suite, one flag or one module is not a rule: it is a comment on that
member, and it does not go here.

## METHOD, designing any measurement

- **METHOD-1**, **Choose a case that distinguishes the hypothesis from its alternatives.**
- **METHOD-2**, **Measure same-build variation before treating an A/B difference as signal.**
- **METHOD-3**, **Re-measure the baseline near the changed run.**
- **METHOD-5**, **Change one variable at a time and keep the baseline reproducible.**
- **METHOD-6**, **Prove which binary and inputs each side of an A/B used.**
- **METHOD-7**, **Verify independent branches before their combination.**
- **METHOD-8**, **Reproduce every “pre-existing” failure on the unchanged build.**
- **METHOD-9**, **Show that verification can fail under a deliberate perturbation.**
- **METHOD-10**, **A check that passes a no-op does not verify the change.**
- **METHOD-11**, **Sweep representative poses, times, or inputs.**
- **METHOD-12**, **State which measurements should change and which should remain invariant.**
- **METHOD-14**, **Test reconciliation metrics for degeneracy.**
- **METHOD-15**, **Confirm the intervention took effect before crediting it.**
- **METHOD-16**, **After restoring inputs, force or verify the rebuild.**
- **METHOD-17**, **Use `git diff` to prove temporary edits are restored.**
- **METHOD-20**, **Reproduce a published measurement's pose AND its statistic, not just its
  subject.** `CAP-13`'s ring brightnesses were read under the sun wash with a brightest-pixel
  statistic, and an annulus median at a clean pose reads lower.
- **METHOD-21**, **Hold the condition a capture was flown in, not just its initial value; a
  scenario entered at a speed it cannot hold is not a measurement at that speed.** The original's
  143 mph knife-edge take, replayed at full throttle, passes 290 mph inside three seconds.
- **METHOD-22**, **Re-measure a fitted constant ALONE once the mechanisms it was fitted on top of
  have been replaced; it may now push the wrong way.** Removing `ClimbGravityScale` by itself moved
  the sustained climb from 276.7 to 257.7 mph, toward the original's 163.1.
- **METHOD-23**, **"Unreachable" is a measurement, not an inference from the constants around it:
  pin the margin with an instrument and assert it against the loaded value, never the literal.**
  The Bloodhawk's peak G demand sits 0.2 % past the executable's own fallback threshold.
- **METHOD-27**, **A count over CSVM's own scene structure is not a count over the original's;
  name the parent before reading a number as a count of objects.** One flak over a C1 aagun dealt
  four splash shares because each node is carved into a `col` and a `col_buildings` body.
- **METHOD-28**, **A/B a new opt-in flag against the code it replaces, never against the flag
  switched off once that code is already deleted.** Turning `applyActive` off in a tree whose old
  root-level visibility rule had gone measured a third state nobody ships.
- **METHOD-30**, **Span a hand-back with the press itself: a modal that consumes a press hands
  input back while that press is still down, so a check that presses only after the hand-back
  cannot see the new owner read its tail.** The click that skipped a campaign film fired the
  plaque under the pointer on its release, and the hand-off's tests pressed after the stop.
- **METHOD-31**, **A convex collider is never the conservative reading of a mesh: it can only add
  hit area, so settle a hit-rate question by rastering rays over the silhouette and counting both
  geometries, never with a kill-time stopwatch that folds rate into damage.** The airframe hulls
  present 1.16 to 4.39 times the model's own silhouette and lose none of it.
- **METHOD-32**, **A deliberately delayed reconstruction is compared at its own lag: fit the lag
  in fractions of a step first, then read the residual, because a same-step difference measures
  the delay and a whole-step fit leaves half a step of it behind.** Fitting the replicated
  aeroplane's lag fractionally moved its reading from 0.60 m to 0.52 m mean.

## DIAG, chasing a symptom

- **DIAG-1**, **Check the premise against data before writing code.**
- **DIAG-6**, **Correlation is not a mechanism.**
- **DIAG-8**, **Audit inherited claims back to their inputs and method.**
- **DIAG-10**, **Explain cases where the change is inert by construction.**
- **DIAG-11**, **Identify what moved before calling a deviation a regression.**
- **DIAG-13**, **Visible activity proves execution, not correctness.**
- **DIAG-15**, **Never silently skip unsupported or failed cases.**
- **DIAG-17**, **Distinguish “never reached” from “reached but invisible.”**
- **DIAG-18**, **Test code-derived explanations at runtime.**
- **DIAG-19**, **Scripted repros inherit identity defaults (-Z heading, near-origin spawn); a
  pose-dependent symptom needs an off-axis heading AND a far-from-origin placement, and a repro that
  works where the report fails means the poses differ, not that the report is wrong.**
- **DIAG-20**, **An effect that never starts passes every "it ends correctly" check; prove the
  START from the production call path before diagnosing the stop.** A `stop-sequence` suite and two
  cockpit passes all read an unparsed block's absence as a working stop.
- **DIAG-22**, **A suite that asserts over a directive the data may not author fails vacuously;
  loop over the authored candidates, then branch on the count.** Two chapters read as a sound
  defect because a wake cue asserted a one-shot no mission authors.
- **DIAG-23**, **Dispatching a callback directly tests what the code does, never what the codes
  around it undo; play the definition that raises it.** CM02's 967 alone left the suite green while
  the definition's `913`/`914` pair put the hidden aeroplane back 19.25 s later.
- **DIAG-24**, **A fixed sim window shorter than an episode's own completion time reads as an
  unresolved bind, not a slow clock.** A 12 s window read `cones 0/2` on cones that draw at t=13 s.
- **DIAG-26**, **A mission's intro cutscene holds the world: no objective completes, `--pos=` is
  withheld, and `SimHeld` skips every AI, turret and projectile phase while the clock, the frame
  counter and the screenshot advance normally, so a bounded probe reads as dead rather than held.**
  Reach a chapter's geometry through a mission with no intro, or give a `--campaign=` run a minute
  of sim and read its `cutscene:` lines first.
- **DIAG-27**, **Count a sortie's deaths off the lines that name a death, never off the absence of
  a damage line.** A CM14 ladder read as crediting itself from nothing: every part its section burns
  demolished died through `KillCalledDestructible`, which spends no health and wrote no line, and
  `DamageAt`'s twelve-line ceiling had already been spent on chip hits. Both now always log a death.
  `zep: … engines N/14` is a poll snapshot too, so the counts between two lines are kills nobody
  printed.

## SHOT, screenshots and pixel evidence

- **SHOT-1**, **Respect capture quantisation; tiny effects need another metric.**
- **SHOT-3**, **Use state logs when pixels cannot resolve an effect.**
- **SHOT-6**, **Compare decoded pixels, not encoded image bytes.**
- **SHOT-9**, **Do not combine screenshots with `--headless`.**
- **SHOT-10**, **Create the output directory, use an absolute path, and verify the screenshot exists.**
- **SHOT-12**, **Frame the time-driven surface, then perturb time.**
- **SHOT-14**, **Treat census counts as lower bounds; disable fog and reject tiny counts.**
- **SHOT-16**, **Hide capture windows; never minimize them.**
- **SHOT-17**, **Sample transitions mid-ramp.**
- **SHOT-19**, **Use a time series and isolate overlapping emitters.**
- **SHOT-20**, **Locate a saturated feature by the centroid of its plateau, not by `argmax`.** On a
  clipped highlight `argmax` returns wherever the tie breaks, 16 px off the sun's centre in C3.
- **SHOT-21**, **Keep radial probes clear of the frame edge.** A circular sample that runs
  off-frame reads clamped pixels as content and reports a confident zero.
- **SHOT-27**, **The true horizon is a CALIBRATED row, not the sky/terrain boundary: shoot the same
  position level and check the shift is `f·tan(pitch)`.** Comparing fog depth in rows from the top
  of frame hid a 5.7° pitch difference between two frames.
- **SHOT-28**, **A nadir shot cannot tell a painted rooftop from an extruded building, and a global
  instance count cannot tell you where the instances went; a placement claim needs a low oblique.**
  A gate that held two textures at 79 % and 96 % of baseline had emptied the near-field skyline.
- **SHOT-29**, **Before pinning a golden, measure how much of the frame the subject actually owns,
  and re-measure it for each subject; a subject no scripted camera can approach does not earn
  one.** An AI aircraft's whole damage ladder differed from pristine by 0.043 % of pixels while the
  same aircraft's fireball three seconds later differed by 25.7 %.
- **SHOT-32**, **A shot of an animated screen proves the frame it drew, never that the screen keeps
  drawing; check an animation by driving frames.** Timed briefing shots at five instants all read
  correctly while driving the live menu found 5193 stale frames of 5400.

- **SHOT-35**, **An Instant Action wrap-up board is not on screen at the frame the mission ends; a
  shot of it must drive frames past the decoded hold or it catches the live world instead.** The
  ending arms `InstantActionRuntime.WrapupHoldS` (3 s) of running world before the board is
  presented, so a `--screenshot` run whose `--frames` stops inside that hold photographs aeroplanes
  and a falling wreck, and reading the missing board as the board being broken is the error.

- **SHOT-36**, **How many lines a wrapped block takes is a font metric, and it moves with the
  window the shot was taken at: pin it at the authored 1:1 fit, not at a capture's resolution.** A
  board's text is rasterised at `round(size x fit.Scale)` points in a box of `width x fit.Scale`, so
  the same objective row wrapped to one line at 800x600 and two at 1280x720 in the same build, and a
  departure chosen from the shot alone was wrong at the fit the suite measures.

- **SHOT-37**, **Where a drawn icon points is its art's own nose plus the turn applied, so measure
  the bitmap before blaming the conversion.** The pause chart's `singledev` is drawn along the
  45-degree diagonal, which a per-degree mirror-symmetry scan of its alpha gives exactly (0.95
  against 0.83 one degree either side), and a scan plus the layout (propeller disc, wing at 30 % of
  the length, tailplane at the end) says which end of that axis is the nose.

- **SHOT-38**, **Render one pose twice with the same texture overridden to two different colours;
  the pixels that differ are exactly the pixels that population touched.** An ordinary
  `--tex-override` frame only marks where a sprite draws near-opaque, so it undercounts a blended
  population badly. Differencing two colour runs is alpha-aware and gives a per-pixel coverage mask
  to test a change against: at the C1 1,208 m cloud pose the two-colour mask covers 515,597 px of
  the 921,600, the cloud fade change moved 431,238 px, and 20 of those lay outside the mask at one
  LSB (blend rounding), which is what "confined to that population" looks like measured.

- **SHOT-39**, **Measure a filled bar against footage by the one edge that moves, not by the
  extent you can see.** A scrollbar thumb, a gauge fill or a progress bar is art with its own end
  caps sitting in a painted gutter, so an eye reading both ends off a frame picks up the caps and
  the gutter's own shadow and is a few pixels slack at each end. The filled-to-unfilled transition
  is a hard luminance step one column can find exactly. Reading BL-842's Instant Action thumb off
  `CAP-50` both ways: its extent measures as board y 182 to 356, which suggests 173 of the 230-pixel
  track, while the transition alone lands on 356, which is 170 and the exact 14-of-19 share the
  window shows. The slack figure would have pinned a suite one preset off the model that produced
  the picture.

- **SHOT-40**, **A screen-space effect flickers with screen motion, so step the pose by a rotation,
  and difference the pair against a second pair rendered with the effect off.** Translating the
  camera 0.55 m and 3 m along the view barely moves the water reflection: at C1's lake those steps
  flip 218 and 608 water texels of 155,444, and over C3's open sea 40 and 189 of 613,601, which
  reads as no flicker and is the step's own doing. A 1.7 degree yaw, one frame of a brisk turn,
  flips 3,641 and 5,270 at the same two poses, and a 5 degree yaw flips 10,534 and 17,543. The
  effect-off pair is what separates the effect's own flipping from ordinary parallax: without it
  every texel the camera move uncovers is counted as flicker.

- **SHOT-41**, **A complaint that one screen tints what another shows plainly is first a question
  about the art drawn over it and the order it draws in, so measure the overlay's own alpha and read
  the layout's Z before adding a colour transform.** The scrapbook's photographs read warm and the
  file EXPORT TO DESKTOP writes reads plain, and no colour matrix exists anywhere: the page wash
  `SB_P_GRIME` is neutral (a grey 128 under it comes out R/G 1.000, B/G 1.000) and the original's
  own spread draws it at gain 0.974/0.980/0.980 with the shipped scraps at 1.0126/1.0125/1.0125,
  while the zoom's mount `DZ_ZOOMgrimeframe` is a cream window of alpha 0.137 to 0.894 over
  (188.8, 173.0, 131.0), which is R/G 1.092 and B/G 0.757, and `LAYOUT.CSV` gives it a Z of 200
  against the print's 0. A matrix fitted to the measured ratio would have been a second tint on top
  of the one piece of art that already makes it.

## GOLD, golden images

- **GOLD-1**, **Update moved hashes with the visual change, and explain each moved shot in the
  commit message.** `manifest.json`'s `exercises` field says what a shot covers *today*; do not
  append a re-pin note to it.
- **GOLD-2**, **A golden is a tripwire, not a diagnosis.**
- **GOLD-3**, **For render-path changes, sweep every golden and inspect the largest movers.**
- **GOLD-4**, **Reproduce a golden with its OWN `frame` count, or the A/B is meaningless.** An
  ad-hoc `RunProbe.ps1` repro that omits it renders a different sim frame.
- **GOLD-5**, **Which goldens move is itself evidence; check the pattern, not just the count.** A
  per-plane chase distance moved all four flown-aircraft shots and none of the nine without one.
- **GOLD-6**, **A pure TIMING change moves pixels, and a particle shot amplifies it without limit;
  judge one by what the moved shots ARE, never by the magnitude.** A 1/60 s event shift moved
  `c1-crash` by 79.7 % of its pixels and left 7 of 8 chapter captures bit-identical.
- **GOLD-8**, **When an earlier item deliberately left goldens un-repinned, a later item's "moved"
  list is about BOTH changes; recover the current item's own movers by A/B-ing hashes against a
  temporarily reverted build.**
- **GOLD-9**, **"13/13 hash-identical" proves nothing until `git diff` shows
  `analysis/goldens/manifest.json` unmodified in the working tree.** A stray `-RegenGoldens`
  re-baselines the tripwire onto the build under test.
- **GOLD-10**, **Hash the raw pixel buffer, never the saved PNG.** Encoded bytes differ between
  two byte-identical images, so a PNG hash reports encoder state.
- **GOLD-13**, **Every pinned shot is a settled frame, so a defect that lives on one transition
  frame is invisible to the whole set; settle it from the frame order in code and from a suite that
  reads the state across it.**
- **GOLD-14**, **A blend-ORDER change is invisible wherever the overlapping quads share a colour
  and an alpha, so judge one by the shots that CAN move and never by a count of unmoved ones.**
  Sorting every particle back-to-front left the single-column sprays bit-identical.
- **GOLD-15**, **Every shot renders at `project.godot`'s pinned viewport, so a rule that depends on
  the window's ratio is only ever photographed at one of them; sweep the ratio in a suite instead.**
- **GOLD-16**, **A new degree of freedom on a pinned path keeps every golden only if its neutral
  value is the EXACT identity, which constrains how the arithmetic is WRITTEN and not just what it
  computes.** A head rotation kept 18 shots byte-identical because `new Basis(axis, 0)` is exactly
  the identity and the existing term order survived.

- **GOLD-17**, **A shot whose command line names machine-local user state pins whether that state
  is present, not the code alone; name the assumption in its `exercises` field.**
  `campaign-4p-grid` launches `--campaign=csvm-golden:6`, a `CampaignProfileStore` name under
  `user://Profiles/` that no checkout carries, so the frame it pins is a campaign launch flying
  with no director, and a machine holding that profile renders a different one.

- **GOLD-18**, **A golden that moves after a shader SOURCE change is not evidence the change had a
  visual effect; force the value to an extreme and see whether the arm owns pixels in that framing.**
  Routing the chapter mip bias through a shared include moved `c5-city-night` by 1 pixel at a channel
  delta of 1. Forcing that bias to +4 through the same function moved 62.6 % of the frame, while
  forcing +4 on the three newly routed arms alone, with the world arm held at its old call, left the
  shot byte-identical. So the 1 pixel is a shader-recompile artifact and the pinned framing
  photographs none of the sprite arms' mip levels, which the re-pin's reason has to say.

- **GOLD-19**, **A decoration that fades out inside its authored `far_fade_range` is invisible to
  every pinned shot flown above that range, so an unmoved golden set says nothing about where it
  sits; move it in a suite and photograph it from street level.** Raising C5's lamp glow 4.75 m
  left all 19 shots hash-identical, `c5-city-night` included, whose camera stands 178 m up while
  the glow's authored fade closes it out by 150 m.

## DET, determinism and randomness

- **DET-2**, **Disable live input during scripted runs.**
- **DET-6**, **Scripted probes imply `--det`; use `--no-det` for realtime behaviour.**
- **DET-7**, **Deterministic results must depend only on committed inputs.**
- **DET-8**, **`--det` ignores `config.json`; use committed or CLI inputs.**
- **DET-9**, **Keep pure baselines free of clocks, absolute paths, and machine state.**
- **DET-10**, **Fixed-step captures cannot reveal realtime cadence artifacts.**
- **DET-11**, **A rate decoded from video of the original is quoted in SIM seconds (k = 1.390);
  implementing the wall figure runs it 39 % fast. It applies to a measurement, never to a rate the
  binary itself denominates on a system-clock dt, where converting is the error.** The chase
  camera's relaxation carried a converted 0.65 against an authored 1.0.
- **DET-12**, **An angle measured off footage cannot confirm a decode; at best it ranks two
  readings, and it will happily rank a third one you have not thought of.** `CAP-16`'s wing-panel
  strip was recorded as confirming a rotation the binary says never happens.
- **DET-14**, **A golden sweep cannot see a `--det` drop fail while a second guard stands in front
  of it, so test the drop where it is written and not by its pixels.**
- **DET-15**, **A rate denominated in real seconds runs at the render rate over 60 under `--det`, so
  a scripted capture pins its shape and never its pacing; judge pacing in a `--no-det` run.** The
  chase camera's throttle transient fell to 1/e in 2.15 sim seconds either way, which was 1.11 wall
  seconds under `--det` at 114 fps against 2.15 wall seconds without it.

## PERF, performance

- **PERF-1**, **`script_ms` and `physics_ms` are the WORST single pass of the last wall second,
  neither a per-frame cost nor a mean; read `proc_ms` and `phys_tick_ms` for the measured pass.**
  Over a flown C2/M02 `physics_ms` ran a 16.96 ms median against `phys_tick_ms`'s 1.81 ms.
- **PERF-2**, **Capped metrics are floors, not costs.**
- **PERF-3**, **Split broad timers before choosing what to optimize.**
- **PERF-5**, **Ignore differences below measured noise and an absolute floor.**
- **PERF-7**, **Compare startup timings under identical cache conditions.**
- **PERF-8**, **Use the engine startup report, not whole-process time.**
- **PERF-9**, **Use two unchanged pairs for noise, then measure A/B back to back.**
- **PERF-11**, **Use `--no-vsync` and metrics valid for the clock mode.**
- **PERF-12**, **A frame ordinal does not convert to wall time at an assumed refresh rate.** The
  dev machine refreshes at 120 Hz, so a frame chosen for a 60 Hz cap lands at half the time.
- **PERF-13**, **A hitch count only compares across runs in the same vsync mode; per-frame cost
  transfers, frequency does not.**
- **PERF-15**, **A flat-leaf sampler's "negligible" cost only holds outside per-particle loops; a
  scope placed inside one becomes the thing it measures.**
- **PERF-16**, **`StartupProfile`'s `boot` is engine start to build start, so on a launchscreen
  rebuild it holds the menu's idle time, and its `rest` is `build − Σ(phases)`, which on a probe run
  holds whatever the probe did before the build closed; neither is pure engine overhead.**
- **PERF-19**, **A GC capture that ends inside the first minute of a session measures the world
  build settling, not the steady state; let the session settle, and say which regime a number comes
  from.**
- **PERF-20**, **A .NET GC pause here is set by how many FINALIZABLE objects died, not by bytes
  promoted; look for the Godot wrappers a call MAKES per frame and judge the fix on `[perf] gc`'s
  `fin_per_s`, which reproduces to a tenth of a percent, not on its pause, which reproduces to a
  few.** Halving allocation stretched the collection interval and the pause together.
- **PERF-22**, **A memo whose entries are a pure function of their key belongs to the process, not
  to the builder instance; split fresh-key work from repeat-key work before optimising anything
  else.** Of a ~300 ms spawn frame, 190 ms sat in the assignments whose `Shader` was fresh.
- **PERF-23**, **A per-object diagnostic on a shared periodic boundary is a BURST, not a spread
  load, so ask the log filter at the call site before the values are formatted.** One ungated
  once-a-sim-second print cost 17.0 ms of an 18.3 ms tick with nine aircraft alive.
- **PERF-24**, **When a frame's cost is in neither physics, the render terms nor any named script
  scope, count the nodes ASKING for `_Process` before looking at what the callbacks do.** 3,814
  `Puffer` nodes cost 9.4 ms of a 12.4 ms frame while their bodies summed to 1.06 ms.
- **PERF-25**, **Deferring a block off a hitching frame moves its cost rather than removing it, and
  a block sized by its data must be split along that data's grain; name the phase with no seam of
  its own first.**
- **PERF-26**, **A process-wide "nothing to do" guard is not a fast path in a real session; read
  its state at the moment you measure, not at process start.** A no-fade baseline read 0 steps run
  alone and 44 once an earlier suite in the same process had freed a world.
- **PERF-28**, **An exact-zero allocation claim needs more than one measurement window: take
  several and require that EVERY one reads zero, rather than widening the assertion to a
  tolerance.** A rule that accepts one clean window of several passes an allocator that charges
  intermittently. Report the first charged window's bytes, its gen-0 count, the thread and the
  module id, which a rerun cannot recover, and prove the windows with a negative control. Every
  window opens on an emptied allocation context, or PERF-29's charge lands in one of them.
- **PERF-29**, **`GC.GetAllocatedBytesForCurrentThread` reports granted minus unused, so retiring
  a thread's allocation context charges that thread for what the context still held. Empty it with
  a forced collection before any window that must read zero.** The step is that remainder alone,
  always under 8 KB: with per-thread pads 512 bytes apart it moved 512 bytes in lockstep, and
  28,800 windows opened on an emptied context charged nothing where seven charged without it.
- **PERF-30**, **A unit test asserts a wall-clock FLOOR, or a figure read off a clock it advances
  itself, never a fixed millisecond ceiling: a ceiling reads the scheduler on an oversubscribed
  machine, so raising it moves the threshold rather than removing the flake.** A six-walk mean
  required under 20 ms read 29.9, 34.9 and 51.3 ms in three of 55 unit runs made beside three
  concurrent builds.
- **PERF-31**, **A threshold on one named frame is satisfiable by moving the block onto a
  neighbouring frame, so report the worst frame of the whole window beside the frame under test.**
  An AI wave launch measured only on its own frame reads as fixed once the assembly runs a second
  early, while the player feels the same stall one second sooner. The window's worst frame, and its
  worst frame carrying no launch, are what separate a removal from a relocation (PERF-25). Measure
  both player counts in the same window: a launch cost that does not grow with the human field says
  the work is in the arriving aeroplane, not in the rigs already flying. Where the target is still
  owed, assert a regression bar at the measured level and name the target in the same note: a suite
  that fails on the cost it was written to expose blocks every later change instead of the one that
  owes it, and the bar drops to the target when the removal lands.
- **PERF-32**, **A "quiet frame" test placed beside an existing deferral pump must read that
  queue's pending count BEFORE the pump runs, not after.** A pump's last step finishes its item, so
  a count read after it says quiet on the very frame that just carried one, and the two builds land
  together: the wave-airframe refill read that way left a worst idle frame of 44.4 ms where reading
  first gives 24.9 ms. The same holds for any frame the measured window already knows is busy.
- **PERF-33**, **Count gen-0 collections per measured frame beside its milliseconds, or a
  collection that lands on one frame of the window is read as the cost of whatever that frame was
  carrying.** In the wave-launch window the one launch frame over 40 ms is the one with
  `GC.CollectionCount(0)` incremented, and the same launch without a collection costs 18 ms; a bar
  set from the collected frame measures the allocator, not the launch.
- **PERF-34**, **A GC pause lands inside whichever sub-scope of a bracket happens to be open, so
  read `GC.CollectionCount` and `GC.GetTotalPauseDuration` across the same span before naming the
  term a bracket reports.** Splitting one physics step of CM11 into preamble, ground-blow probe,
  flight model, sweep, contact resolution and tail produces 33 to 36 ms steps whose whole cost sits
  in one of those terms, and the term differs from run to run; each carries `gc=1/1/0` and a pause
  equal to the step, so the reading is the collector rather than the code the bracket names. This is
  PERF-33 one level down, and PERF-20 says what sets the rate.
- **PERF-35**, **A console-tier log line costs about 1.9 ms on the frame path, so count the lines
  ONE event emits before crediting the work the lines describe.** Edge-triggering a report is not
  the same as making it rare: the fade channel reports a collider crossing once per faded subtree
  root, and a single sonic-burst checkout crosses six of them, which is 11.3 ms of `GD.Print` in a
  scope whose actual work (the opacity walk and the collider resync together) measures 0.15 ms.
  Split the log call out of the timed region before naming the mechanism, and demote a frame-path
  report to `Debug`, which keeps the file-sink record and drops the console write. The same
  measurement also inflates the instrument reading it: with `hitchMonitor.floorMs` low enough to
  trip most frames, every `[perf] hitch` line is itself a console write on the frame that follows.
  PERF-23 is the per-frame form of this.
- **PERF-36**, **An in-engine suite that bars a per-frame cost reads it on the stepping thread's own
  CPU time (`QueryThreadCycleTime`), never on the wall clock: in the sharded engine stage the wall
  clock measures the neighbouring shards, and even alone half of a launch frame's wall time is a
  wait.** On the wall clock, `ai-wave-launch-hitch` placed beside heavy suites by the shard
  partition reads median launch frames up to 108.5 ms, a warm worst of 187.9 ms and worst idle
  frames of 122.6 and 146 ms, against about 20, 20 and 41 ms alone. Alone, a warm C4/M03 launch frame reads
  about 20 ms wall against 9 to 11 ms on-thread, and its `SpawnAi` reads 11 ms wall against 4 ms
  on-thread while the whole process's cycles over the same call read the same 4 ms, so the gap is
  no other thread's work. It matches the call's four `[flight]`/`[sound]`/`[weapons]` console
  lines at PERF-35's 1.9 ms each, and a console write blocks until its reader drains the pipe at
  whatever pace the machine's load allows. On the thread clock the worst readings are 13.8 ms
  median, 14.1 ms warm worst and 44.6 ms worst idle frame, alone, in the six-shard stage and beside
  sixteen CPU-burning processes, which the suite's 25/60/80 ms bars clear by about 1.8x. A 20 ms
  busy-wait injected into each launch reads a 30 ms median and fails. The cost of the choice: a
  regression that blocks instead of computing reads as nothing, so the window's wall worst stays in
  the report.

## LOG, logs, error censuses, and exit codes

- **LOG-1**, **An empty report may mean the mode did not build the feature.**
- **LOG-2**, **State the time, count, and lifecycle window before concluding from absence.**
- **LOG-5**, **Report caps and truncation; never infer absence from a shortened list.**
- **LOG-8**, **Run shader checks windowed; `--headless` skips the render path.**
- **LOG-12**, **Automated instruments must return their verdict in the exit code.**
- **LOG-13**, **Do not overlap engine probes.** `RunTests.ps1`'s own shards isolate their logs and
  scratch; a suite whose store sits outside `.scratch/`, such as a `user://` profile, still does not.
- **LOG-16**, **A census printed at the end of setup cannot report a runtime miss.**
- **LOG-17**, **In a worktree, `RunTests.ps1` exits 0 having run only the units; set
  `$env:CSVM_DATA_ROOT` and confirm the printed suite and golden counts are non-zero.**
- **LOG-18**, **`--debug-anim`'s motion line prints a GLOBAL position and a LOCAL rotation, so its
  `rot` is world attitude only for a placed template root.**
- **LOG-19**, **`RunProbe.ps1`'s hidden desktop is shared by every worktree on the machine, so a
  sibling agent's probe finishing mid-run kills your viewport; count the Godot processes naming
  another worktree before believing an error census.** The signature is a repeating per-frame
  `NullReferenceException` and two `viewport is null` lines.
- **LOG-20**, **Every tree of this project shares one `user://`, so a probe reads and writes the
  real saved profiles; copy a profile under a new name before naming it in `--campaign=`, and
  delete the copy.**
- **LOG-22**, **A `*.godot.log` mirror is not that run's log: exclude it when sweeping
  `.scratch/logs/` for what a run did or did not print.** Every quit copies Godot's shared log, so
  one deliberate probe was replayed by every later mirror.

- **LOG-23**, **`Log` renders only the holes the log call itself interpolates, so a string built
  before the call reaches the file in the machine's own culture.** The plane collider census read
  `3,8×3,0×5,6 m` on a German machine although its `Log.Info` line was invariant, because the
  summary property had already formatted the numbers. A suite never catches this: the test harness
  pins the thread to the invariant culture, so the fault appears only in a real run. Grep for a
  numeric format (`:0.0`, `ToString("0.#")`) OUTSIDE a `Log.*` call, in a summary property, a
  `reason` argument, a list entry or a `StringBuilder.Append`, and give each one `Log.Format` or
  `FormattableString.Invariant`.

## WORLD, world data and runtime traps

- **WORLD-8**, **Resolve objects by source identity, not normalized node names.**
- **WORLD-9**, **Verify that the selected mode builds the product being measured.**
- **WORLD-10**, **Report opposing transitions separately; a net can hide both.** A death both
  disables the healthy collider and enables wreck colliders, so count OFF and ON separately.
- **WORLD-11**, **Match sampling time to lifecycle.**
- **WORLD-12**, **A started definition is not proof of output; verify its runtime product.**
- **WORLD-15**, **Establish each subtree’s coordinate frame before applying transforms.**
- **WORLD-20**, **For rare classes, census first and aim at named geometry.**
- **WORLD-21**, **Route equivalent lookups through one resolver.**
- **WORLD-22**, **Use explicit subsystem state when hosts are hidden by design.**
- **WORLD-23**, **Range-test decoded fields and corroborate their units.**
- **WORLD-24**, **Read authored range and condition gates before placing a probe.**
- **WORLD-25**, **A registry total counts bindings, not coverage: a larger census can mean one
  definition claimed objects it does not describe.**
- **WORLD-26**, **Anchor an effect to the object it decorates, not to a parameter that merely
  describes it.** C3 authors a sun yaw of 135 while its `sun` node sits at yaw 45.
- **WORLD-27**, **`--play-anim` proves a definition RUNS; it says nothing about whether the game
  ever reaches it, so drive the real entry point before concluding the definition is at fault.**
- **WORLD-28**, **A suite world built without collision, or with no `ContactMask` wired, poses
  every untimed `OBJECT_MOTION` at rest and can play a whole breakup while saying nothing about
  where any piece comes to rest; a suite that must see a contact wires both.** A lifeboat drop
  read 0.0 m fallen without a mask and 6.2 m with one.
- **WORLD-29**, **A term you meant to redirect can leave instead, and the frame looks like a win
  either way; prove the new source arrives with a control colour.** A background colour set as its
  own reflected light gave Godot no radiance map, and the vanished specular read as a success.
- **WORLD-32**, **A Godot property that accepts a write is not a property the renderer reads:
  prove a lighting knob is live by driving it to an extreme and watching the goldens move.**
  `LightEnergy` 1.6 to 0.5 moved 7 shots while `AmbientLightEnergy` 0.9 to 0.0 moved none, because
  a sky-sourced ambient takes its fill off the sky cubemap and reads neither the energy nor the
  colour written beside it. On the colour-sourced ambient both lighting arms write today, the same
  zeroing moves the same 7 aircraft shots.
- **WORLD-33**, **An upward ray reports open air under a one-sided collider, so read a column
  DOWNWARD from above instead.** A downward ray answers in 1,376 of 1,376 partition cells and an
  upward one in 24.
- **WORLD-36**, **Every vehicle a mission never placed stands at the world origin, one hull inside
  another, so scope a ray's answer to the subtree you asked about.**
- **WORLD-38**, **A boot-script setting counts only if a retail run reaches the script that sets
  it.** `load.gw` is the data-compile path and never executes at retail; `adjust.gw` always does.
- **WORLD-40**, **Two numberings of one thing coincide on some subjects, so a readout keyed on the
  wrong one passes every pin taken where they agree; pin on a subject where they disagree.** CM05's
  objective priorities and `OBJECTIVEn` numbers differ while CM01's agree, which is why its pin held.

- **WORLD-41**, **A new per-instance draw on a shared `Rng` subsystem stream reseeds every later
  consumer of that stream in the same process, so give it its own subsystem.** One
  `Rng.NewSystemRandom(Rng.Clouds)` taken before the cloud scatter's own loop, drawing nothing the
  placements read, still moved C5's pinned sprite count from 16,170 to 16,185 when a suite built
  C1's field first. The fix is a new `public const string` on `Rng`, not a reordering. The same
  ordering sensitivity is why a suite that builds two chapters' fields calls `Rng.Rewind()` before
  each one: a session builds exactly one field, so the second field in a process is off the pin
  unless the stream is reset.

- **WORLD-42**, **A D3D render state the original sets once covers every draw on the device, so its
  remake twin is ONE global that every arm reads, and an arm sampling around it draws that chapter
  at a level the original never chose.** `MipBias` is `D3DRENDERSTATE_MIPMAPLODBIAS`, one state over
  the world mesh, the camera-facing billboards, the cylindrical facades and the clutter alike; here
  it is `csky_mip_bias` behind the single `csky_sample_albedo` in `shaders/csky_mip_bias.gdshaderinc`,
  and `chapter-census` fails any mip-mapped sampler outside it. Count the arms from the shader
  generators, not from the one the setting was first wired into.

- **WORLD-43**, **A census that walks a built world only covers the arms the world build itself
  makes; one a game session adds afterwards is invisible to it and passes vacuously.**
  `chapter-census` sees no cloud-field shader in any of the eight chapters, because
  `FogVolumeClutter` is built by `GameSession` and not by the world build, so its sampler is
  asserted in `cloud-field-fade`, which builds the field.

- **WORLD-44**, **A mesh's polygon count is not its scatter surface; read the per-polygon skip flag
  before predicting anything from the geometry.** Every shipped `fvol` volume is a closed box or
  prism, so laying the cloud lattice on its faces looked like it would put cards under the deck and
  inside the streets. The census says the walls and the floor carry `no_clutter` (`0x800`) in
  every chapter (C1 45 of 54 polygons, C1C 71 of 175, C5 102 of 148), so the scatter surface is
  the upward skin alone and no shipped face points down or sideways. The flag, not the volume's
  shape, is what makes a slab chapter's field a sheet.
- **WORLD-45**, **Hand a `CollisionShape3D` the EMPTY shape and fill it afterwards. A `Shape3D`
  that already carries its faces when it is assigned reaches the body as an empty shape for the
  rest of the frame, so everything that body stood for falls through.** A crater carve that built
  its private `ConcavePolygonShape3D` and then assigned it left the struck terrain node answering
  nothing at all, at the bowl and 24 m outside it alike, while the node still read 216 faces;
  assigning the empty shape first and calling `SetFaces` on it afterwards read the carved profile
  at once (120.00 at the rim, 114.00 at the apex). The shape's own data write reaches the server
  immediately; the body's swap to a different shape does not.
- **WORLD-46**, **A region test on a face's centroid removes nothing from world geometry, because
  a terrain triangle is far larger than anything a weapon cuts into it.** The ground triangle a
  bomb struck has its centroid hundreds of metres from a 20 m crater, so a centroid-in-ring filter
  left the collider at 147 faces and laid the bowl under ground that was still solid. Subtracting
  the region from each face and re-emitting the remainder is what makes the hole the carve's own
  size; the same subtraction serves the skin and the trimesh.
- **WORLD-47**, **A dark world surface is usually authored dark, so read the mesh's own baked vertex
  colour and its sheet's mean texel before suspecting a light.** The fullbright world draws vertex
  colour times texel times `csky_world_light`, and all three are data. C1's refuel-tank debris reads
  near-black because its pieces carry vertex colour 119 where the rest of the same object carries
  254, on a sheet whose mean texel is (63, 61, 64); at C1's 0.802 that is 0.375 of the sheet, which
  is the whole of the "37 % of its own colour" a texture census had measured. A census over textures
  cannot name the sheet a surface uses, because several sit inside its chromaticity tolerance; the
  mesh's own material names it outright, and the `debris-shading` suite's probe prints both halves
  of the product per mesh under a destructible.

- **WORLD-48**, **A population authored `lighting: false` cannot show a directional light, so read
  the flag and the normal array on the actual nodes before attributing a directional look to the
  sun.** The report that night cloud sprites are directionally moonlit named a population that the
  original's light never reaches: every placed `cloudparent` card in C1, C1B, C1C and C4 (626,
  1,620, 1,056 and 1,453 nodes) is `lighting: false` AND ships an empty normal array, so both the
  gate and the geometry the term runs on are absent, and C1B carries no `fvol` volume to hold the
  cards that do have them. The flag and the array are per node in the chapter's own `gamez`
  (`models.json` plus `nodes.json`), so the question is answered by a census in minutes and cannot
  be answered by a frame. A look that survives that census comes from some other mechanism, and
  implementing the named one moves nothing in the chapter that was complained about.

## SHELL, Windows, PowerShell, and processes

- **SHELL-2**, **Identify stray Godot processes by worktree and probe flag.**
- **SHELL-3**, **After bulk rewrites, run Godot as well as the compiler.**
- **SHELL-7**, **On PowerShell 5.1, read BOM-less UTF-8 through an explicit UTF-8 API.**
- **SHELL-10**, **Launch scripted Godot probes through `RunProbe.ps1`.**
- **SHELL-12**, **Give every scripted probe an exit condition, and check the flag you chose actually
  is one.** `--frames=N` terminates a run only alongside `--screenshot`; alone it runs until killed.
- **SHELL-13**, **A scripted run must not steal desktop focus.**
- **SHELL-14**, **Test window-focus handling by alt-tabbing, not minimising.** Minimising delivers
  only the mouse enter/exit pair, never a focus notification.
- **SHELL-15**, **A BOM a PowerShell write left on a source file comes BACK the next time the Edit
  tool touches that file, so strip it as the LAST step and confirm with `git diff`.**
- **SHELL-16**, **An incremental `dotnet build` after restoring a file to byte-identical content
  can silently no-op, and `[IO.File]::Copy` preserves the SOURCE file's last-write time, so an A/B
  built by swapping file content needs `--no-incremental` or a fresh `LastWriteTime`.** A limiter
  margin table taken with a stale swap agreed to two decimals on every airframe.
- **SHELL-17**, **A census script must not name its accumulator after one of its own parameters:
  PowerShell variable names are case-insensitive, so `$nodes = @()` under `param([string]$Nodes)`
  writes into the TYPED parameter and turns every later `+=` into string concatenation.**
- **SHELL-18**, **Quote a comma-bearing command-line value in PowerShell, or the unquoted comma
  splits it into an array and the run fails.**
- **SHELL-19**, **Quote a launched argument that carries a space; `-ArgumentList` quotes nothing
  itself.**
- **SHELL-20**, **Under `$ErrorActionPreference = 'Stop'`, redirecting a native command's stderr
  makes its failure terminating, so a probe whose failure is the answer must lift the preference and
  read the exit code instead.**
- **SHELL-21**, **Two new `global uniform` declarations in `csky_atmosphere.gdshaderinc` made every
  headless `RunProbe.ps1` run save its screenshot and then never exit; the same declarations inside
  one generated shader did not.** The cause is not decoded. Bisect a probe that hangs at exit
  against the include first, and declare a global a single shader family needs in that family.

## INSTR, building instruments

- **INSTR-3**, **Share derived predicates with production code.**
- **INSTR-5**, **Log resolved outputs as well as lookup inputs.**
- **INSTR-6**, **An able-to-fail control over randomised state must sweep seeds, not pin one.** A
  recorded probe reports 0 misses at seed 1 and 2/1/1/1 at seeds 4/7/9/10.
- **INSTR-7**, **"Not decidable from this data" is a fact about the instrument, not the question:
  when a census comes back uniform, ask what else varies the quantity.**
- **INSTR-8**, **Z-fighting is instability, not appearance: measure it as pixels that SWAP WINNER
  between captures a millimetre apart, never by looking at one frame.**
- **INSTR-9**, **A score normalised per axis cannot judge a question about which axis is which;
  prefer a statistic invariant to the thing you are not testing.** An axis-order census was led by
  a division by a ground ring's 0.0 m thickness.
- **INSTR-10**, **An assertion keyed on a field that is not unique reports on whichever subject it
  reaches first; qualify the key, or the check is about something else.** Puffer names repeat
  across definitions, so a `Census.Any(name)` was answered by a fireball's row.
- **INSTR-11**, **A probe that reports one half of a compound thing reads as a full pass on the half
  it can see, so sample over the window rather than at its end, and ask what is left behind.**
- **INSTR-13**, **An in-engine suite runs inside ONE frame, so the physics space never sees a body
  moved, shown or enabled after it was built: aim at bodies where they were created, build a world
  the object already stands in, or call `TestContext.SyncPhysics()` (`ForceUpdateTransform()` for
  one node) between the change and the cast.** An `AnimatableBody3D` is worse: the server reads a
  transform write as a kinematic target and no repair commits it, so a flown aeroplane leaves its
  collider behind.
- **INSTR-14**, **Every automated session check runs on a PARENT-DRIVEN clock, so verify the shared
  step owner rather than either clock adapter alone.** A wave sequencer lived only in that path and
  waves 2 to 4 never arrived at the controls while every scripted check stayed green.
- **INSTR-16**, **A capture taken on the very first rendered frame can beat the first per-frame
  publish, which is not evidence the per-frame effect is broken.**
- **INSTR-19**, **The flight-dump hash agrees across the Godot runtime and the `dotnet test` host
  only because the print is rounded past where they diverge; prefer a same-host A/B, and treat a
  cross-host mismatch as a rounding boundary before a plant change.**
- **INSTR-20**, **A negative test that also steps the mission script can arm the very gate it is
  asserting stays shut; drive only the subsystem under test through the control leg.**
- **INSTR-21**, **An animation that completes instantly satisfies every end-state assertion, so
  assert the episode's DURATION too, and build the world the way the session that runs it does.**
  With `camera1` unbound a drop completed in the frame it started; bound, it runs 4.43 s.
- **INSTR-22**, **An aircraft that sinks below `FlightController`'s under-map backstop is teleported
  to its spawn with no crash flag, so gate a flown leg on an altitude floor.** Two such jumps were
  the whole of a 3797 m worst separation on a leg that plateaued at 254 m.
- **INSTR-26**, **Model realtime at the clock-adapter boundary, not by giving each consumer a
  private callback again.** Install a Realtime `GameClock`, confirm it is not `ParentDriven`, and
  step `SessionSimulation` with the physics delta.
- **INSTR-27**, **On a realtime leg a stand-in flies, so read the STATE under test, never a presence
  flag that its flying can also answer.** `InPlay` folds `Inert` with `Crashed`, so a stand-in
  flown into the sea reports exactly as a correct hide does.
- **INSTR-28**, **A `--screenshot=` on a Realtime (`--no-det`) clock cannot pick its `--frames=` to
  land on a transient live state; prefer a log line, or drive the state through a suite's own
  `_Process` loop.** A cutscene handoff landed anywhere from t=3.7 s to t=40.2 s across identical
  launches.
- **INSTR-30**, **A world-coordinate precision effect cannot be measured at heading 0, nor with a
  luminance centroid: fly it, rotate it, take consecutive frames, and keep a control region that
  shares the transform but carries no drive.** A pinned heading-0 nudge read 0.03 to 0.12 px where
  a flown phase correlation read 0.7 px at 10 km.
- **INSTR-31**, **A cache or fixture that hands a suite the WRONG subject is invisible to any
  assertion that holds on both subjects; prove the key with an identity test, never with the
  catalog.** Keying a chapter cache on a file name every chapter shares resolved all eight to C1.
- **INSTR-33**, **A smoothness complaint about a sim-driven object cannot be reproduced under
  `--det`, which every capture flag implies, so read `fps` against `phys_hz` on a `--no-det` run
  first.**
- **INSTR-36**, **A golden that moves on your branch is not yours until you have run it without your
  change, which costs one `git worktree add --detach <merge-commit>` and one golden stage.**
- **INSTR-37**, **`Node3D.Scale` does not read back axis for axis off a basis carrying real
  rotation, so compare sorted magnitudes or the whole basis.** An authored `(1, 0.25, 1)` on a
  rotated arm reads back as `(1, 1, 0.25)`.
- **INSTR-38**, **A fault that needs a hash collision fires on a minority of runs, so re-running a
  suite measures the collision rate and not the fault; assert the stale state directly.** A filter
  threw on 1 run in 14 while the same drive left hundreds of stale rows every time.
- **INSTR-39**, **A "did it move" distance is scored by the defect, so it passes hardest on the
  worst behaviour: read the DISPATCH for "did the event fire" and the RESTING POSITION for "did it
  end in the right place".** A 1 m threshold passed four gasbags falling 1,228 m through the sea.
- **INSTR-40**, **A self-test whose rows all call helpers proves nothing about the entry point the
  caller actually uses, and a gate's trigger belongs to the gate, not to each harness that calls
  it: drive the harness's own command text against a fixture carrying a known fault.** Three
  `PreToolUse` copies of a `git commit` trigger skipped `git -C <tree> commit` while all 21
  self-test rows passed.
- **INSTR-42**, **A golden that produces NO image failed differently from one whose hash moved: read
  `broken` as a tooling fault and `moved` as a content change, and open the preserved log first.**
- **INSTR-45**, **A screenshot proves nothing about audio: read the `sound` log's pairing instead,
  one line per aircraft at build naming what each slot resolved to, then one per cull transition.**
  The pair is what separates silent past the cull from silent because the definition never resolved.
- **INSTR-46**, **An unpacked developer tree cannot reproduce a zip-only asset bug: verify a
  release's asset shape with `--zip-assets`.**
- **INSTR-47**, **A body's net world displacement is zero for as long as something is holding it in
  place, so it cannot answer "is this body still travelling"; read the velocity of the frame it is
  solved in.**
- **INSTR-48**, **A sweep that stops at the FIRST obstruction it finds tests the geometry nearest
  the instrument, not the geometry the report is about; choose the bearing by how much of the
  subject the segment passes through.**
- **INSTR-49**, **A synchronous in-engine suite reads `GameClock.Current.Time` as a CONSTANT, so
  anything behind a time-expiring cache runs its first verdict for the whole leg; install a clock
  and call `BeginFrame(dt)` per step, then assert the elapsed game time.**
- **INSTR-52**, **Reading the two writers you expected does not prove a property is never
  written: grep every assignment of the member before filing "it is never copied".**
- **INSTR-53**, **Stubbing out a MEMOISED lookup does not turn a fix off: the first call still
  fills the cache and every later one reads the answer back. Stub the cache fill as well, or the
  red check passes and pins nothing.**
- **INSTR-55**, **Time a lease from the event that renewed it, never from wherever the previous
  check ended: a step count started mid-lease measures the remainder and reads a working lease as an
  expired one.**
- **INSTR-56**, **One `GD.Print` on a code path makes that path untestable in `dotnet test`, and it
  fails as a crashed HOST rather than as a failing test: read the `.trx` before blaming the
  harness.** A breadcrumb goes through `Log` at DEBUG, which makes no engine call under the default
  threshold.
- **INSTR-59**, **A positional-audio check must read the node the CULL measures from, not only
  whether the emitter is playing: an audible verdict passes while the two are different nodes, as
  long as the wrong one happens to stand near the listener.**
- **INSTR-60**, **Walk a menu screen to the row's own text or key, never by a counted number of
  cursor steps: a row added above it silently redirects every later press.**
- **INSTR-63**, **A per-session seam that defaults to a null object hides a missing production
  wiring, so assert WHICH INSTANCE a built subsystem reads, by `ReferenceEquals` against the
  session's instance with a control that reads the null object, not only what that instance
  reports.** Every world emitter read the null ambience while every puffer suite passed against a
  fake renderer.
- **INSTR-64**, **A changed rebinding DEFAULT is invisible to any profile that has already saved
  that context, because a saved keymap lists every action of the context and the store loads it
  whole: verify new defaults on a fresh profile, and test the default table itself rather than a
  seat built from disk.** The keymap store versions its schema, not its action list, so a default
  change moves no stored file.
- **INSTR-65**, **A suite that can only reach a state through the one cause it is testing cannot
  see a flag the state ENTRY sets rather than the cause; enter the state through the scripted seam
  too, and assert the flag is still clear there.** Every evade phase arrived by damage, so a mode
  transition that stamped the evade flag stood unseen and swallowed the next steady-hand roll.
- **INSTR-66**, **Scope a modifier rule to the one binding table that names the modified key, never
  to the whole session: a global "a held Shift silences every bare key" also silences the free
  camera's Shift boost and every menu key, which no test of the flight map would catch.** Ask the
  map itself whether it holds that key under a modifier, and assert the silencing both where a map
  contests the key and where none does.
- **INSTR-67**, **Re-enabling a `CollisionShape3D` only QUEUES the shape's broadphase rebuild for
  the next physics step, so the node reads `Disabled=false` and the server still answers nothing;
  a suite that hides or shows anything must sync before it casts.** aagun32's woken mount answered
  0 of its 6 enabled shapes and 6 of 6 after the sync, and one unrelated `BodyTestMotion` anywhere
  in the world repaired all six, which is the server's own pending-shape flush.
- **INSTR-68**, **A suite passing on a space that is missing colliders is passing on geometry the
  game does not have, so re-read every verdict the sync changes rather than only the red one.**
  The armed zeppelin leg had been parking its bait 200 m from a ring, which is inside a 657 m by
  136 m hull, and it engaged only while the hull's own shapes were absent.
- **INSTR-69**, **An input read off a device the test host does not have needs a pinned seam beside
  the live read, or the mechanism is only reachable by hand.** The mouse flight scheme reads an
  absolute cursor offset inside the viewport, which is zero in every headless suite, so
  `FlightController.MouseStickForTest` supplies that offset and the live path stays the only reader
  of the real pointer.
- **INSTR-70**, **A new in-engine suite is not finished when it passes: `analysis/engine-suite-weights.json`
  must name it too, and a unit test fails until it does.** The balancer weighs every registered
  suite, so an unweighted one leaves a shard's plan guessing at its cost.
- **INSTR-71**, **Anything a screen animates off the wall clock keeps determinism only if the thing
  that installs its draw is the interactive path alone; install it from the build and every
  scripted and golden run inherits the wobble.** The load screen's fill and its 6 fps propeller are
  reported unconditionally by the session build, but the pump that draws them is installed by the
  load board's `_Ready` and cleared by its `_ExitTree`, so a CLI launch leaves the ambient null and
  every report is a no-op: 19 of 19 goldens unmoved, and the motion is testable only through a
  suite that installs its own pump.
- **INSTR-72**, **A check taken well past a threshold passes for every value of that threshold;
  straddle it instead, one reading just inside and one just outside.** The AI gun loop's cull was
  read with the listener at 4x the cue's audible distance, which is silent whether the cull sits at
  1.0x or at 1.1x of it, so the missing margin survived that suite; ears at 1.05x and 1.15x
  separate the two.
- **INSTR-73**, **A line a readout composes is not a line the player reads: assert the control it
  reaches and the layer that control sits on.** The crashed pilot's respawn line passed its unit
  test on `ComposeTextLines`' return for as long as it existed, while the crash camera hid the whole
  HUD `CanvasLayer` the text block hangs from, so the string reached no screen; the `c1-crash`
  golden shows the crash notice from the message layer beside a blank where the prompt was.
- **INSTR-74**, **Suppressing a definition's `OBJECT_MOTION` shortens the definition, so a suite must
  not read its slot off `AnimStateOf`.** A definition whose only sustain is an endless
  `XYZ_ROTATION` ends the moment that motion is dropped, and the runtime then reports it EXECUTED
  where the unsuppressed definition would read RUNNING forever. `spinprops` is the worked example:
  `FlightController` suppresses its motion because `PropAnimator` already turns those discs, so the
  rig tracks the prop slot in a field of its own and the suite asserts EXECUTED deliberately.
- **INSTR-75**, **Name a rig off the format string that prints its result, never off the function
  next to it.** `FUN_00491d90` sits between the original tuner's climb rigs and was taken for the
  sustained-climb one on position alone; following the global it writes to the `sprintf` that reads
  it makes it the `Max turn ... deg/sec` rig, and the climb is the neighbouring `FUN_00491c60`. A
  rig identified by adjacency costs the whole reading built on it.
- **INSTR-76**, **Two instruments that share only the mechanism are a real check on each other; two
  that share the rig are one instrument.** The sustained climb is measured twice, once flying free
  from a 300 mph entry at 60 Hz and once with the attitude pinned and integrated from rest at
  100 Hz. They agree to 0.06 %, which is evidence about the equilibrium, where a second probe built
  on the first one's entry conditions would only have restated it.
- **INSTR-77**, **A remake-only widget put on a screen the data authors needs its place read off the
  authored rows, not written down: a hardcoded corner is right for one layout and lands on an
  authored control in every other.** The Original Instant Action lives box at a written Y 260 stood
  on the clear line the shipped layout leaves, and on the unit fixture's tighter lines it stood on
  the mission dropdown, where a pointer at the lives box focused the mission box instead. Taking
  the first gap in the setup stack tall enough for it puts it on a free line in both.
- **INSTR-78**, **When the parameter under test also moves the control render, the instrument's
  counts drift with the parameter, so report the control's own count beside them.** The water
  flicker instrument counts texels that move with screen-space reflection on and do not move with
  it off, and raising the water material's roughness broadens the specular sheen the reflection-off
  render draws as well: the open-sea control rises from 7,361 texels at roughness 0.1 to 12,149 at
  0.25 and 48,347 at 0.4. A bigger control masks more of the on-pass movement, so a high-roughness
  flicker count is biased optimistically and no value can be picked on that count alone.
- **INSTR-79**, **A session-wide device mode a run can change is state the harness owns, so guard
  the write on nobody being at the controls and then assert the mode is untouched, never assert the
  capture itself.** The hidden test desktop is a real display, so a display check alone does not
  spare it; `SessionSpec.IsScripted` and `Det` are what do. Exercising the positive arm in a suite
  would have the engine warp the pointer from a window on another desktop, which reaches the user's
  own cursor, so a capture on a real display stays a judgement at the controls and the suite reads
  the decision instead.
- **INSTR-80**, **A `--no-det` screenshot taken near a session start photographs the start cover,
  not the world.** The cover holds opaque from the load screen until the session's first real frame
  and then fades over a second, so a `--no-det --frames=3` shot of C1 reads mean RGB 27/28/31 where
  the same frame under `--det` reads 125/126/112. Shoot a session start with `--det` (which is the
  default for `--screenshot`), or past the fade, and read `[ui] start cover:` in the log to see how
  long it held.
- **INSTR-81**, **An effect that lives a couple of frames is judged by the gap between its node
  and the node that spawned it, never by eye in a still.** A muzzle light placed once in world
  space ends 1.65 m and 1.71 m astern of its own firepoint over its two drawn frames at 100 m/s,
  and the same flash anchored to that node ends 0.00 m from it; on a `--view=4` flank frame the
  whole difference is 472 pixels on a 1280×720 shot, easily read as the burst's own shape. Log
  the distance at the moment the effect expires, which is where the drift has accumulated, rather
  than the per-frame correction, which reads the same in both builds.
- **INSTR-82**, **A formation leg flown in a straight line can measure a loss but never a recovery:
  two aircraft on one airframe share a top speed, so whatever gap opens between them stays open.**
  A leader's nitro burn opens 383 m on its escort and the pair then holds 805 m apart, unchanged,
  for as long as the leader flies straight at full throttle; the gap falls back inside the 700 m
  leash 15.3 s after the cutoff and to within 7 m of the pre-burn separation only once the leader
  turns, because a turn is the only corner an escort capped at its own `fd_speed` can take back.
  Script the leader turns before reading a re-form time, or the leg reports a permanent loss.
- **INSTR-83**, **A consumer that compares a value between two of its own calls must run on the
  clock that moves the value; driven from the rendered frame it is green under every scripted run
  and dead at the controls, because those run one sim step per frame and the controls do not.** A
  headless realtime flight holds 120 rendered frames a second over the fixed 60 Hz step, so a
  throttle gate driven per frame read the lever flat on every other frame and could never see a
  rise larger than one step's slew, a thirtieth of what it was watching for (INSTR-14 is the same
  seam one level up).
- **INSTR-84**, **A golden shot reported as "the run printed no pixel hash" is a scrape that lost
  the race, not proof the render broke: open the shot's own `.scratch/goldens/<name>.log` and look
  for the `shot pixmd5=` line before believing it.** One four-worker pass reported that on two of
  19 shots, and both logs carried a hash, one of them identical to the pinned one; the same two
  shots hashed normally on the next pass. Compare the logged hash against the manifest to tell a
  real move from a lost line, since the report cannot tell you which it was.
- **INSTR-85**, **Reading an effect's placement against its anchor on the simulation step reports
  one frame of travel whether or not the effect rode anything, because the effect was placed on the
  interpolated DRAWN pose and the anchor now stands on the simulation pose; take both readings at
  the same instant or measure travel, never the difference.** A muzzle-light breadcrumb that read
  0.00 m under `--det` printed 1.11 m at 158 mph on the wall clock with nothing wrong.
- **INSTR-86**, **An effect anchored to a node is drawn from the pose that node holds when the
  effect's OWN frame callback runs, so its draw must be ordered after every node that writes a
  drawn pose in its callback; a hand-stepped suite poses the anchor before it draws and can never
  see the lag, so assert the `ProcessPriority` as well as the placement.** `ProjectilePool` drew
  before the flight rigs, and every anchored flash and shot light sat a frame astern of the gun.
  A reader that runs first does not lag evenly: on a frame with a physics step it reads the new
  simulation pose, and on one without it reads last frame's drawn pose, so its error alternates
  with the step cadence and reads as a stepping picture. `TargetHud` aimed the spyglass that way,
  0.18 to 1.44 m off the drawn target in a realtime turn.
- **INSTR-87**, **An `AudioStreamPlayer3D`'s `VolumeDb` is the level a listener gets only when its
  attenuation model is `Disabled` and its `MaxDistance` is 0: any other model applies its own curve
  and a low-pass of up to 24 dB above 5 kHz, and a `MaxDistance` multiplies a second linear fade
  that reaches zero exactly at that distance. Drive the level yourself, or a suite asserting a cue
  sounds is blind to how loud it is.** Mapping `snd_turretgun`'s `RANGE [30, 400]` onto the
  inverse-distance model left a hull's gun 7 dB under the decoded law at 100 m and hard silent at
  400 m, where the decoded law plays on to 440.
- **INSTR-88**, **A frame forced from inside a synchronous block presents only the drawing commands
  the server already holds, so a repaint there must issue its own; a queued redraw is deferred
  through a queue none but the main loop flushes, and counting the calls or their log lines is not
  evidence any frame differed, only reading the presented frames back is.** A load screen that
  repainted a `Control` and called `RenderingServer.ForceDraw()` reported 12 draws over a real
  launch and wrote 12 byte-identical captures, the first fraction over and over.
- **INSTR-89**, **A setting's own apply line is not evidence that the setting stands: read the state
  it writes after the screen that applied it has closed, since a screen previewing a setting live
  owes the state it opened over back and can put it back on top of the accept.** The pause sheet's
  AUDIO page logged `mix master=100 music=50 effects=0 voice=50` on an accept and the bus was at
  -6.021 dB a moment later, the preview's restore having run after the apply, which is why the level
  was heard only at the next start.
- **INSTR-90**, **Moving a control from one action to another in a shipped default table puts it on
  BOTH for any saved keymap that names only one of the two, because a load merges saved rows into a
  default map action by action: let the saved row take the control off every action the file does
  not name, and test the partial file, not only the whole one.** A file naming the free-look row
  alone would have held the free look and the rockets on one mouse button.
- **INSTR-91**, **A suite photographs 3D geometry in an off-screen `SubViewport` of its own, which a
  canvas item cannot do, but three things about that pane are not what they look like: an ordinary
  `MeshInstance3D` renders at the identity until `ForceUpdateTransform` is called on the subtree,
  because a transform notification waits for a scene-tree flush no single-frame suite reaches; a
  `MultiMesh`'s instance buffer reaches the GPU with the frame AFTER the write, so one forced draw
  photographs the previous pose; and the pane reads back as `Rgb8`, three bytes to the pixel, so a
  comparison striding four reports every frame as wholly changed.** A tree card's occlusion pane
  read a card that drew 0 pixels and a ground quad that filled 6912 of 9216, both artifacts of this,
  before any of the three was accounted for.
- **INSTR-92**, **A session's own clock stands still under a suite that drives `_PhysicsProcess`
  alone, because `GameClock.Time` advances in `BeginFrame` and nothing but the frame callback calls
  it: every timestamp two harness sessions exchange therefore reads zero, and a difference between
  them can only be made by calling `_Process` on one of them.** The match-state tick carried a host
  clock of zero to both guests over a whole harness match, so `NetClockSlew.Snaps` and `Target`
  stayed at zero and said nothing about the feed; one `_Process(6.0)` on the host alone moved its
  clock six seconds and the next ordinary tick landed a target of 6.000 s and one snap on both.

## SRC, sources and documents

- **SRC-3**, **Use design documents for intent; retail evidence decides shipped details.**
- **SRC-5**, **A field you don't read may be REDUNDANT, not dropped; try to derive it from the
  fields you already read before deciding what it means.** All 51 `*_delta` vectors are exactly
  `(to − from) / run_time` of the sibling channel.
- **SRC-6**, **A test the binary computes is not a rule until you find what reads its result.** The
  take-hit body computes "same team or neutral" into a byte whose only read is an argument the
  callee never touches, so the original applies friendly damage.
- **SRC-7**, **A key the data authors, a name it carries, or a threshold the artwork separates on
  is not a feature until you find the reader; grep the executable for the key and read the one site
  that consumes it before modelling it.** `enemy_skill` exists in no string of `crimson.exe`; the
  `warning_shot_*` block read as a near-miss rating and is a shield; a puffer blend threshold that
  reached the right answer on the case it was built on is not the bit the engine reads.
- **SRC-8**, **A branch's effect is only half its meaning; the other half is what it is an
  alternative to, so record the jump it skips.** One arm read as a capacity top-up had the entire
  wave teleport as its `else`.
- **SRC-9**, **A negative about a site ("the flag is not tested", "the original only sets a flag")
  needs the control flow around it read, not a search for the immediate; read on past the write
  before treating a field as the whole behaviour.** A flag hoisted into a register at the loop top
  read as unfiltered, and a decode row that stopped three instructions short of the maneuver picker
  supported the opposite of what the handler does.
- **SRC-12**, **Which body axis a matrix row holds is a claim to re-derive at a point of use, not
  to take from another page; a wrong row sign reverses a ported direction without failing
  anything.** Row 2 of the orientation matrix is minus the nose, and read as the nose it put the
  player's shadow behind the chase camera.
- **SRC-14**, **A census over the shipped files counts records, not live things; resolve the record
  in the world that reads it before saying a mode offers it.** Six `IA1` files flag a radio tower
  target and only C1's world has a node of that name.
- **SRC-16**, **A named rectangle in a dialog primitive says nothing about whether it crops the
  source or clips the output; settle it by aligning a reference still, not by reading the name.**
- **SRC-17**, **Two definition files keyed alike are not copies of each other; compare the bodies
  before reading one screen's content out of the other's file.** Every `LOADING_SCRIPT` is a
  superset of the matching `ESC_SCRIPT` under identical keys.
- **SRC-18**, **A decoded constant read per SLOT stops being decoded the moment the port feeds that
  slot from a different device; carrying it over is a substitution, not fidelity.** The mouse arm
  gates its yaw slot at 0.3 whatever the `is_autogyro` exchange put there, and that 0.3 is the third
  mouse axis's own dead band. This port has two axes, so for an autogyro the exchange feeds the yaw
  slot the cursor's sideways travel, and gating that travel three times as wide as the same travel's
  bank left an ordinary cursor offset flying the autogyro nothing while it already banked an
  aeroplane. Ask which SOURCE a decoded threshold was measured on before keeping it.
- **SRC-19**, **"That file carries no such id" is a claim about a file, so open the file across the
  whole install before routing the binding through somewhere else.** A campaign danger zone's
  objective number was written up as coming from the mission graph because `dzones.zrd` was said to
  carry no objective id; its `objective_numbers` key carries exactly that id, and the pairs match
  the shipped `Snap_<mission>_<objective>` scrapbook rows mission for mission.

## What this project cannot verify itself

The user must verify audio and control feel, multi-controller behaviour, skilled flying, live-input
flows, and fidelity claims requiring the original. Automated checks must state these gaps.

## Known non-deterministic surfaces

Scripted probes are pinned by `--det`; `--no-det` restores live input, clocks, spawn, and
randomness. Expect variation in flight framing, weather, water, particles, random animation,
damage effects, and liveries. Measure a fresh same-build floor for the exact scenario.

## The standing checklist

- [ ] `.\RunTests.ps1` exits 0; every `SKIP` is understood as not checked.
- [ ] The check can fail and the case exercises the changed mechanism.
- [ ] Inputs, pose, clock mode, binary, and output files are confirmed.
- [ ] Same-build noise and a current baseline were measured where relevant.
- [ ] Complete logs were checked and “pre-existing” failures reproduced on the baseline.
- [ ] Every moved golden is explained and updated in this commit.
- [ ] Relevant modes, chapters, branches, times, and viewpoints were sampled.
- [ ] Anything requiring the user or original game is stated as unverified.
