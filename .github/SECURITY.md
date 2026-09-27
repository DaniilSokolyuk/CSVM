# Security policy

CSVM is a desktop application that contacts no server: there is no update check, no
telemetry and no account. It does open a UDP socket, but only where the player asks for
one. Hosting a multiplayer match from the menu's multiplayer door opens a listen server on
a port the player chooses, and optionally asks the router to forward it. While it hosts, a
second socket on UDP port 47501 answers LAN game searches with the host's name and session
details. Joining opens an outbound connection to an address the player typed or picked from
the LAN games list, and that list is filled by a broadcast query from a socket the player's
Connect opens. Nothing listens otherwise, and a
splitscreen or single-player session opens no socket at all. What the build does do in
every session is parse a lot of binary and JSON that came off disk, which is where its
realistic security surface is.

## Supported versions

The most recent release on the [releases
page](https://github.com/Laeresh/CSVM/releases). Older releases get no fixes; a fix goes
into the next release.

## Reporting a vulnerability

Use GitHub's private reporting: **[Report a
vulnerability](https://github.com/Laeresh/CSVM/security/advisories/new)** on the Security
tab. Please do not open a public issue for a security problem.

Include the build version, what an attacker gets, and the smallest input or steps that
reproduce it. If a file triggers it, attach the file rather than describing it.

If that page does not open for you, open an ordinary issue saying only that you have a
security report and asking where to send it. Do not put the details in it; a private
channel will be opened for you.

A report is read and answered in the advisory thread, and the advisory is where the fix and
any credit are recorded. There is no bounty, and no schedule to promise: this is one
person's project. If you would like to be named in the advisory, say so.

## In scope

- The file parsers in the engine: the readers for extracted archives, meshes, textures,
  animation and mission data, reached by pointing a build at a prepared `extracted` folder.
- The per-user state files under `%APPDATA%\Godot\app_userdata\CSVM` (settings, bindings,
  campaign profiles, scores, custom planes), and the optional `config.json`.
- The extraction (the in-game Extract screen, the `--extract` flag and the repo's
  `Extract.ps1` that launches it) and how it handles the install path it is given.
- The release zip's contents differing from what the release page's SHA-256 says they are.
- The multiplayer listener while a player is hosting: what a peer can send a host over the
  match's own messages, and the port mapping the host asks its router for.
- The LAN discovery responder while a player is hosting. It answers only a well-formed query,
  a bounded number per frame, and its reply is never larger than the query, so a report of it
  answering anything else or amplifying traffic is in scope.

## Out of scope

- **The Windows SmartScreen warning.** Releases are deliberately not code-signed; the
  release page's SHA-256 is the check that carries meaning, and the zip's `README.md`
  says so.
- **The retail Crimson Skies game**, its files and its installer. Not this project's code.
- Anything that needs an attacker to already be running code as you on your own machine.
- Vulnerabilities in the upstream projects this build carries, [Godot](https://godotengine.org/)
  and [mech3ax](https://github.com/TerranMechworks/mech3ax). Report those to them; tell us
  as well if a CSVM release ships an affected build, and the release will be rebuilt.
