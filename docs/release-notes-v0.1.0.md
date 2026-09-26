## CSVM v0.1.0

The first public build of CSVM, a fan-made remake engine for **Crimson Skies** (2000, Zipper
Interactive / Microsoft). It plays the original game from your own retail install: the campaign
with its cabin, briefing and flight-check screens and its cutscene films, Instant Action's four
mission types, 2 to 4-player splitscreen Dogfight, free flight, 11 aircraft and 8 chapter worlds in
the original's liveries, with the original's weather, world animation, music and sound. The zip
contains no game data of any kind. You extract your own once, with the script inside it.

This is an early build. It is not code-signed, so Windows SmartScreen will warn you the first time
you run it; the `README.md` in the zip says what that warning looks like and how to check the
download against the SHA-256 below instead.

### What you need

- Windows 10 or 11, 64-bit.
- A graphics card with a working Vulkan or Direct3D 12 driver. Below that line the program starts
  and vanishes a few seconds later, at the intro film, with no message; the `README.md` in the zip
  says how to recognise that case from the log.
- A retail Crimson Skies install on the same machine. It is only read, never modified.
- About 1 GB of free disk space for the data you extract. Nothing else has to be installed: the
  .NET runtime the engine needs is inside the download.

### Setup

1. Unzip the download wherever you like.
2. Double-click `Extract.cmd` and point it at your Crimson Skies install, the folder holding the
   `ZBD` and `GOSDATA` subfolders. This runs once and takes about half a minute.
3. Double-click `CSVM.exe` and pick a mode, chapter and plane in the menu.

The `README.md` beside `CSVM.exe` is the long form: the SmartScreen prompts, where your logs and
saved games live, and what to read when something goes wrong.

### Known issues

Nothing here stops a mission from ending or loses progress. Each entry says what you will see and,
where there is one, what to do about it.

**Campaign missions**

- In Mercy's Errand (the fifth Northwest mission) the attack balloons dive from their entrance
  altitude down to the sea and climb back out, and the objective marker follows them down. Wait
  for the climb, which needs no input.
- In The Bomber Heist (the second Hawaii mission) your own Pandora fires its turrets at the
  Balmoral the mission wants captured. This is what the original does too and is kept on purpose.
  Close on the last Balmoral and finish the wing-walk promptly rather than circling.
- In The Lost Treasure (the first Hawaii mission) the palm trees of an island the mission removes
  stay standing on open water. Cosmetic; nothing about the mission changes.

**Combat and the radio**

- Enemy and wingman radio chatter during a fight is much rarer than the original's. No workaround.
- A FLARE loaded on a rack shows its blue star burst before it is fired. Cosmetic.

**What the world looks like**

- An aircraft's ground shadow lies on a flat quad: it is placed, sized, faded and shaped the way
  the original draws its own, but on a steep slope it rides over the ground rather than wrapping
  it, and water takes no shadow. Enhanced Graphics in Game Options (restart after changing it)
  draws real shadow maps instead, which are not the shadow the original drew.
- With Enhanced Graphics on, faint diagonal bands cross open water and an aircraft's shadow on
  itself carries noise. Switch Enhanced Graphics off if it bothers you.
- In the Northwest chapter a bright band along the edge of a ground polygon shows through the soft
  edges of the trees standing in front of it. No workaround.
- In the New York chapter some lit building faces read darker than the original and some plain
  surfaces brighter. No workaround; nothing is missing from the world.
- The wing position lights are drawn flat, so they do not turn to face you and thin out or vanish
  from some angles.

### Reporting a problem

Bugs go in an issue through the bug report form at
<https://github.com/Laeresh/CSVM/issues/new?template=bug_report.yml>. It asks for the build
version and the newest file in the `logs\` folder beside `CSVM.exe`, which between them usually
identify a fault without a round trip. Security problems go through the repository's
`SECURITY.md` rather than an issue.

### AI assistance disclosure

This project is developed with the help of AI coding agents. Parsers, engine code and
documentation are AI-assisted and human-reviewed; commit trailers in the repository name the
specific agent and model that did each piece of work.
