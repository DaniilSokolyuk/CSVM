# CSVM domain language

Terms this project has fixed, and the words to avoid for them. Use the term as written here in
code comments, docs, backlog entries and commit messages. A concept with no entry is not
forbidden; it is untracked, and inventing a fifth word for a concept that already has one is what
this file exists to stop.

The original game's own names are a separate authority and always win: `cs_name` node names,
`zrdr` reader keys, `PUFFER_STATE` / `LIGHT_STATE` / `SOUND_NODE`, chapter IDs `C1` to `C5`. Never
rename those to something tidier. See `docs/agents/domain.md`.

## Decode and remake

**The original**:
The shipped 1998 program and its data, as the authority a behaviour is measured against.
_Avoid_: the engine, the game, the binary, retail, mech3

**Decode**:
A behaviour worked out from the original's data or code, reproducible from that evidence.
_Avoid_: port, derive, mirror, reproduce (as nouns for the result)

**Tune**:
A constant calibrated to a measured original with no decoded mechanism behind it. A tune is
replaced when a mechanism is found, never added to.
_Avoid_: fudge, hack, approximation, guess, magic number

**Remake-only rule**:
Behaviour with no counterpart in the original, kept deliberately and with a measured reason.
_Avoid_: divergence, deviation, our own, deliberate departure

## Teams

**Team id**:
The one integer space every combat actor shares: `0` neutral, `1` the player's side, `2` and up
hostile sides. Two actors are hostile when their ids differ and neither is `0`. There is no second
space and no per-kind mapping; an aircraft, an emplacement and a world object are compared as
plain integers.
_Avoid_: engine team, engine-space team, team index, side id, faction, alliance

**Authored team**:
The team id a data file carries. The same integer as the team id, with no conversion on load; an
absent `TEAM` key means `2`.
_Avoid_: original team id, raw team, data team, loader team

**Versus band**:
Where the extra humans of a splitscreen `--vs` session sit, kept clear of the ids world data
authors so a player cannot inherit an emplacement's side. A remake-only rule: the original has no
per-pilot team ladder. A co-op campaign does not use it: every human sits on team id `1`.
_Avoid_: team band, emplacement band, pilot team offset

## Campaign co-op

**Scripted player**:
The one aircraft an authored `player` token resolves to. Always P1, whatever else is joined.
_Avoid_: the player, P1 (as a stand-in for the concept rather than the slot itself)

**Human field**:
Every human aircraft in the session. An objective condition reads the whole field and is satisfied
by the first human to meet it.
_Avoid_: player field, human roster, party

**Guest**:
A human pilot who seats no profile. A guest flies, shoots, captures and dies, and nothing about
them persists.
_Avoid_: second player, extra pilot, co-op player

**Seated profile**:
The one campaign profile a co-op sortie reads and writes. A guest can neither spend nor change
anything in it.
_Avoid_: active profile, player profile, current save

**Episode owner**:
The human whose trigger started a cutscene episode. The airframe swap puts them in the new
aeroplane, and staging places them on the marker.
_Avoid_: triggering player, cutscene owner, episode player

## Clutter

**Stamp**:
One placement of a decoration by the UV-lattice stamper. Not a Godot instance, which is the
rendering mechanism a stamp may be drawn by.
_Avoid_: place, scatter, plant, dress, decorate

## Aircraft assembly

**Flight roster**:
The session's aircraft set: the human field prepared at session start and the AI aircraft introduced later by missions, waves, or generators.
_Avoid_: AI roster (it excludes the human field)

**World query**:
The one seam onto the live physics world, `IWorldQuery`: `Sweep`, a shape cast along a motion,
`Ray`, a single ray, and `Overlaps`, a standing-pose touch test. The only Godot adapter over
`DirectSpaceState`.
_Avoid_: probe (taken by `Tooling/Probes.cs`, `ProbeGroundBlow` and `ProbeBlocked` already)

## Contact

**Contact**:
The event: an aircraft touching anything solid, world or another aeroplane. It names the whole
family, whatever the outcome, and it is what the airframe sweep detects.
_Avoid_: collision, hit, touch, strike (as nouns for the event)

**Impact**:
The point a contact happened at, in world space. A position, never the event.
_Avoid_: contact point, hit point, collision point, impact event

**Graze**:
A contact the striking aircraft survives: it slides along the surface, spends the contact's damage
pair, and keeps flying.
_Avoid_: scrape, glance, bump, survivable collision, minor hit

**Crash**:
A contact the striking aircraft does not survive, whatever made it fatal (the doom rule, health
exhausted, an airframe that cannot un-embed, no damage data). Also the ground contact that ends a wreck's
fall, which is the same word for the same reason.
_Avoid_: death, destruction, fatal hit, kill (a kill is what a shooter is credited with)

## Damage and death

**Damage**:
Spending armor and health, and the injure staging those ledgers fire: pools, thresholds, stage
tears and repairs.
_Avoid_: destruction (that is the choreography), hurt, harm

**Destroy choreography**:
Everything a death dispatches: the destroy def's arms, wreck pieces and their flights, crash rigs,
callbacks, and the stops that end them.
_Avoid_: death effects, destruction sequence, kill animation

## Boards and the sim clock

**Menu presentation**:
One selectable realization of the menus, owning their screen graph, artwork, layout,
animation and interaction while sharing the state and operations that configure and launch play.
_Avoid_: menu renderer, menu theme, menu skin

**Built-in presentation**:
The permanently supported menu presentation that ships without depending on extracted menu
artwork and acts as the fallback when another selected presentation is unavailable.
_Avoid_: current menu, legacy menu, temporary menu, fallback UI

**Original presentation**:
The menu presentation that follows the original's screen composition and evidenced interaction,
scaling its authored 4:3 canvas uniformly while preserving the extracted artwork's pixelated look.
_Avoid_: original renderer, classic skin, pixel-perfect menu

**Options**:
Process-wide player preferences persisted independently of profiles. The menu presentation is not
one of them: the command line alone selects it. A seated profile never owns or overrides them.
_Avoid_: profile settings, campaign options, preferences (as a separate store)

**Decoded menu layout**:
The structured screen geometry, artwork roles and widget metadata produced before play from the
original's menu data. Runtime consumes it but performs no format or executable analysis.
_Avoid_: runtime decode, binary menu layout, hardcoded screen

**Menu input source**:
One seat's producer of semantic menu commands, independent of whether keyboard, mouse, pad or a
future flight-control setup supplied them.
_Avoid_: pad reader, controller input (as the menu abstraction), input device

**Board**:
A full-screen overlay panel drawn over the flight, in the shared board style.
_Avoid_: overlay, screen, popup, dialog, panel

**Results board**:
The boards shown when a run is over: the Instant Action wrap-up, the dogfight result, the race
result, the solo stunt result.
_Avoid_: end screen, endscreen, game over screen, wrap-up screen

**Board menu**:
The cursor and item list a board carries, driven by pad or keyboard rather than Godot focus.
_Avoid_: pause menu (it appears on results boards too), overlay menu, button list

**Menu owner**:
The one player whose input drives a board menu. Every other pad is inert while it is up.
_Avoid_: focus (Godot focus is deliberately unused here), active player, controlling player

**Halt**:
The sim clock is not advancing, whatever the cause.
_Avoid_: freeze, frozen, stop, stopped

**Halt reason**:
One cause of a halt. The clock advances only when there are none.
_Avoid_: halt flag, pause flag, halt source

**Session simulation**:
The haltable, ordered advancement of a running session's flight, combat, mission, radio, effects,
and match state. Authored animation and presentation are outside it.
_Avoid_: game loop, world simulation, physics loop, world tick

**Pause**:
A player-requested halt, as distinct from a halt any other reason caused.
_Avoid_: using it for a halt a results board caused

**Rerun**:
Resetting the running mode in place to its start, same world, with no session teardown. What the
race, the dogfight and a free flight do. Distinct from a respawn, which returns one plane to the
air mid-run.
_Avoid_: restart (that is the other mechanism, below), soft restart, reset

**Restart**:
Freeing the session and building a fresh one from the same settings, behind the load screen. What
an Instant Action mission does, because its opposition lives in the world and cannot be put back
in place. An unpinned restart draws a new mission; a pinned one repeats.
_Avoid_: relaunch, reload, hard reset

**Load screen**:
The board drawn over the whole window while a session builds. Carries no progress: a build is one
synchronous block.
_Avoid_: loading screen, splash, please-wait

## HUD markers

**Edge marker**:
The off-screen presentation of a marked world target: the marker clamped into the inset screen
edge, with an arrow pointing outward to the pane's own edge and a clock-hour bearing.
_Avoid_: off-screen indicator, edge arrow (that is one styled part of it), waypoint marker

**Clock-hour bearing**:
A target's bearing relative to the pilot's own heading, in clock hours: 12 ahead, 3 right,
6 behind, 9 left. The original's "N o'clock" suffix.
_Avoid_: o'clock value, clock direction, relative bearing in hours

**Spyglass**:
The round live picture of the selected target drawn at its edge marker while the target is off
screen and inside the range gate. The picture alone is the **disc**.
_Avoid_: zoom window, magnifier, picture-in-picture, scope

## Format documentation

**Format family**:
A coherent group of related original-game data formats documented under one canonical page in `docs/formats/`.
_Avoid_: format page

**Format-family landing page**:
The canonical entry page for a format family; it routes independent detailed subtopics to child pages.
_Avoid_: parent page

**Format reader path**:
The explanation order: at a glance, conceptual model, reference, reader rules and edge cases, then evidence and related pages.
_Avoid_: boilerplate

**Format child page**:
An independently useful subtopic with its own source, evidence, terminology, or implementation concerns. A 25–30 KB landing page is a review prompt, not an automatic split.
_Avoid_: overflow page
