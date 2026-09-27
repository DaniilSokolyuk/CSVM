## CSVM v0.2.0

CSVM is a fan-made remake engine for **Crimson Skies** (2000, Zipper Interactive / Microsoft). It
plays the original game from your own retail install, and neither download contains any game data.

### What you can play

- **The campaign**, with its cabin, briefing and flight-check screens and its cutscene films.
- **Instant Action's** four mission types, and free flight.
- **Splitscreen Campaign Co-op and Dogfight** for 2 to 4 players on one machine.
- **11 aircraft and 8 chapter worlds** in the original's liveries, with the original's weather,
  world animation, music and sound.
- **Gamepads, keyboard and mouse**, with both control screens for binding them.

### New in v0.2.0

- **Multiplayer over a network.** Dogfight Deathmatch and the campaign in co-op, hosted from the
  menu and joined by a LAN search or by address, over a LAN or the internet.
- **Flight sticks.** Sticks, throttles and HOTAS setups work beside gamepads, keyboard and mouse,
  and can be bound on either controls screen.
- **A Linux build**, which runs natively on the Steam Deck. There are now two downloads: the
  Windows zip and the Linux tarball.
- **Extraction inside the game.** The `Extract.cmd` script from v0.1.0 is gone: with no game data,
  `CSVM.exe` (or `CSVM.x86_64` on Linux) opens a screen that finds your install and extracts from
  it.

This is still an early build. Neither download is code-signed, so Windows SmartScreen warns you the
first time you run the Windows build; the `README.md` inside each download says how to check the
file against the SHA-256 below instead.

### What you need

**Windows:**

- Windows 10 or 11, 64-bit.
- A graphics card with a working Vulkan or Direct3D 12 driver. Below that line the program starts
  and vanishes a few seconds later, at the intro film, with no message; the `README.md` in the zip
  says how to recognise that case from the log.
- A retail Crimson Skies install on the same machine. It is only read, never modified.

**Linux and Steam Deck:**

- x86_64 Linux with a working Vulkan driver (current Mesa for AMD and Intel, or NVIDIA's own
  driver). SteamOS on the Deck has what it needs.
- A copy of the folder a Windows install of Crimson Skies creates, the one holding `ZBD` and
  `GOSDATA` side by side. The case of the file names does not matter.
- For a flight stick only, the system's SDL2 library (`libSDL2-2.0.so.0`). SteamOS and most desktop
  distributions have it already.

**Both:** about 1 GB of free disk space for the extracted data. Nothing else has to be installed;
the .NET runtime the engine needs is inside the download.

### Setup

1. Unzip or unpack the download into a folder of its own. The Linux tarball's files sit at its
   root, so make the folder first (`README-linux.md` has the commands).
2. Start `CSVM.exe` (Windows) or `CSVM.x86_64` (Linux). With no game data yet it opens a screen
   that offers to extract it, with your install folder filled in when it can find one. Confirm, and
   the menu opens when the extraction is done, after about half a minute.
3. Pick a mode, chapter and plane in the menu.

On the Steam Deck, add `CSVM.x86_64` to Steam as a non-Steam game, leave the compatibility tool off,
and run the first extraction in Desktop mode. After that it starts from Game mode like any other
game. `README-linux.md` has the steps.

**Coming from v0.1.0:** unpack v0.2.0 into a new folder and extract once more there. Your saved
games, settings and custom planes live outside the game folder and carry over.

### Multiplayer

- **Modes:** Deathmatch from the Multiplayer Lobby, and the campaign in co-op for up to four
  players, where guests follow the host through the cabin, briefing, hangar and debrief.
- **Joining:** a guest finds games on the same network through the LAN search, or joins by the
  host's address, IPv4 or IPv6.
- **Hosting over the internet:** a host listens on UDP port 47500. CSVM asks the router to open it
  (UPnP for IPv4, an IGD v2 pinhole for IPv6). If the router does not allow that, forward UDP 47500
  to the host by hand. The first time you host on Windows, the firewall asks whether to allow CSVM
  on your network; allow it.
- Every player needs the same version, 0.2.x. A mismatched build is told so when it tries to join.

### Known issues

Nothing here stops a mission from ending or loses progress. Only the larger ones are listed
here.

- **Multiplayer:** Capture the Flag, Zeppelin vs. Zeppelin and custom planes are not available over
  the network yet. There is no relay service, so hosting over the internet needs UDP port 47500 to
  be reachable. A co-op guest misses a few things the host sees, such as the campaign movies and
  the AI's radio call-outs.
- **Steam Deck:** Enhanced Graphics and splitscreen run well below 60 frames per second. The
  default graphics hold 60 in single player; leave Enhanced Graphics off on the Deck.
- **Linux:** flight sticks and finding an install inside a Wine or Proton prefix are untested.
  Reports either way are welcome.
- **Mercy's Errand** (the fifth Northwest mission): the attack balloons dive to the sea and climb
  back out. Wait for the climb, which needs no input.
- **Radio chatter** during a fight is much rarer than in the original.
- **Graphics:** some details still differ from the original, among them the ground shadow on steep
  slopes and some building faces in New York. With Enhanced Graphics on, faint bands can cross
  open water.

### Reporting a problem

Bugs go in an issue through the bug report form at
<https://github.com/Laeresh/CSVM/issues/new?template=bug_report.yml>. It asks for your system
(Windows, Linux or Steam Deck), the build version and the newest file in the `logs` folder beside
the executable, which between them usually identify a fault without a round trip. Security
problems go through the repository's `SECURITY.md` rather than an issue.

### AI assistance disclosure

This project is developed with the help of AI coding agents. Parsers, engine code and
documentation are AI-assisted and human-reviewed; commit trailers in the repository name the
specific agent and model that did each piece of work.
