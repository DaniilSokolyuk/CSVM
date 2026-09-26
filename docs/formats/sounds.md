# Sound readers and WAV format

Part of the [format documentation](README.md). This page covers shared sound definitions, player
volume and pitch curves, and the WAV container. The readers are `SoundDefs.cs`, `WavFile.cs`, and
`FlightAudio.cs`.

## Contents

- [Sound sets](#sound-sets)
- [Sound groups](#sound-groups)
- [The music channel](#the-music-channel)
- [Player curves](#player-curves)
- [WAV format](#wav-format)
## Sound sets

`SETS` alternates set-name → list of entries. Entry shape:

```
[snd_name, wavName, FLAG…, KEY, [values], …]
```

- **Bare flags:** `LOOPED` (plays as a forward loop), `3D` (positional), `FREQUENCY`
  (the buffer gets the frequency control, without which every pitch write on it is refused, see
  [below](#a-definition-is-pitched-only-when-it-carries-frequency)), `SFX`, `PURGEABLE`,
  `OPTIONAL`.
- **Valued keys:** `RANGE [fullVolumeDist, audibleDist]` (meters), `VOLUME [gain]`,
  `QUEUE [waitSeconds]`, `QPRIORITY [n]`.

### Which channel a definition plays on

⚠ **`3D` is the positional opt-in, not the default.** Only 124 of the 2,766 shipped definitions
carry it, every one of them also carries `RANGE`, and no definition carries `RANGE` without it. A
definition with neither is not a positional sound with default distances; it has no distance model
at all. The flags sort the whole library into four channels, and the classes do not overlap:

| Class | Marker | Count | Where it plays |
|---|---|---|---|
| Positional | `3D` (+ `RANGE`, `SFX`) | 124 | a point in the world, `WorldSounds` |
| Radio line | `QUEUE [seconds]` | 2,538 | the mission radio queue, no position |
| Music | `MUSIC`, or a `mu`-prefixed WAV or group | 33 | the streaming channel |
| Cockpit and UI | none of the above | 71 | flat: briefings, `*_cp` engine loops, menu clicks |

⚠ **`QUEUE` and `3D` never co-occur**, over all 2,766 definitions. Every mission-VO and
combat-voice definition is a radio line, so a callout is heard the same wherever the player is;
placing one in the world is an invention the data does not support. `QUEUE`'s value is how long
the line may wait for a channel someone else holds before it is dropped as stale: 0.5 s for the
combat barks in `COMMON` and the `id<N>` sets (say it now or not at all), 45 s for a mission's own
VO, 85 s for C5/M04's. The parser (`FUN_00592c90`) reads the key into the definition's wait field
and then **adds 0.3 s**; a definition with no `QUEUE` key keeps that field's initial **5.0 s**.
`QPRIORITY` writes a 3-bit priority (bits 10–12) over the file header's `DEFAULT_QUEUE_PRIORITY 3`,
on three definitions in `c3m05` and nowhere else; ⚠ which direction is more urgent is undecoded, so
nothing reads it. ⚠ The tolerance is a wait rule, not a deadline on the cue's own 1 s start delay:
charging the delay against it drops every 0.5 s bark before it can speak. The cue delay and the
`STOP_QUEUED_SOUNDS` cancellation are [objectives.md](objectives.md).

⚠ **There is one queue, and combat voice speaks on it.** A combat line's play call
(`FUN_004afc90`, reached from the talker gate `FUN_004afd00`) is `FUN_00593590(def, 1.0)`, which
hands a non-streamed definition to `FUN_00593b80(def, gain, pos = 0, vel = 0)`. There, the
definition's queued bit (flag word `+0x0c`, bit `0x200`, the `QUEUE` key) routes it to
`FUN_00593a70`, which builds a queue item stamped `now + wait` and inserts it into the single
global list `DAT_00639eac`. The pump `FUN_00593110` holds one item on air at a time
(`DAT_00639eb4`), starts the next only after the current voice stops plus 0.3 s, and skips an item
whose stamp has passed. The position pointer is null on this path, so a combat line is flat, never
placed at the speaker. The remake's `MissionRadio` is that queue: objective cues enter it through
`Cue` with their 1 s delay, and combat lines through `Speak` with none. ⚠ The 0.3 s gap between
items is not modelled; `MissionRadio` starts a waiting call on the step after the last line ends.

Beside the effect/UI sets (`COMMON`, the per-mission `c<x>m<nn>` sets, the `brief_*` and `DIALOG`
sets), 35 sets named `id<N>` carry the combat-voice clips: `snd_id<N>_<TYPE>` →
`VO_id<N>_<TYPE>.wav`, 1,414 defs. Their runtime chain and caveats (defs without WAVs, the
announcer id without defs) are [combat-voice.md](combat-voice.md); the reader is
`CSVM/src/Mech3/CombatVoice.cs`.

⚠ **A positional voice is silenced past 1.1 x its `RANGE` audible distance, whichever path plays
it.** The `RANGE` pair is the attenuation curve alone; the cull sits outside it, in the sound
manager's own 3D update `FUN_00597c20`, which sets the voice to the minimum volume once the
listener is at or past `audibleDist * 1.1` (DirectSound's own -10000, through the voice's
`+0x3c` setter; the compare and the ramp below it read the definition record the voice points at,
`RANGE`'s two floats at `+0x1c` and `+0x20`). In
the band past the audible distance the computed gain is clamped silent as soon as it falls below
that same -10000, so the margin is a quiet tail rather than full volume held longer.
Every weapon voice reaches that routine through one dispatcher, `FUN_00597af0`, and
one keyed-loop helper, `FUN_0045e470`: the player's own gun loop at `0x004884b8`, every other
vehicle's at `0x0041f6fd` (the AI fire decision, which an AI aeroplane and a `mode ship` hull
share), and a turret's at `0x004ab1d9`. So the aircraft loop and the turret voice carry one margin,
not two. `FUN_00597af0`'s other arm, the hardware-3D `FUN_00597b40`, applies no margin of its own,
but the flag it switches on (`DAT_00639cb4`) is zero in the image and its only writer clears it, so
the software path above is the one the retail build runs.

### The gain between the two radii

The same `FUN_00597c20` holds the whole curve, in DirectSound's hundredths of a decibel. Write
`r0` for `RANGE`'s full-volume distance, `r1` for its audible one, `band = r1 - r0`, and take the
listener distance from the emitter (`0x00597dac`, the classic bit-hack square root, so it is an
approximate distance, not an exact one). With `V` the definition's own `VOLUME` as decibels:

| Band | Gain, decibels | Where |
|---|---|---|
| `dist <= r0 + band/8` | `V` | `0x00597e60`, the shelf: the ramp's reference distance is one eighth of the band, so the level holds unattenuated past the full-volume radius |
| `r0 + band/8 < dist < r1` | `V + 10 log2((band/8) / (dist - r0))` | `0x00597e88`, `FYL2X` against `ln 2` then `x 1442.695`, which is `1000 log2` in hundredths |
| `r1 <= dist < 1.1 r1` | `V - 30 - 700 (dist - r1)/r1` | `0x00597e2d`, the constants `7000` at `0x00609424`, `0.1` at `0x006034a8` and `3000` at `0x00609420` |
| `dist >= 1.1 r1`, or a total under -100 | silent | `0x00597e09` against `1.1` at `0x006082e8`; `-10000` is the floor everywhere |

⚠ **The ramp is measured from `r0`, not from the emitter, which is what makes it flat.** Ten
decibels per doubling is steeper per doubling than inverse distance's six, but the doublings are
counted from the shelf, so the level holds far longer in absolute distance: a `RANGE [200, 1200]`
siren is unattenuated to 325 m, 10 dB down at 450 m, 20 at 700 m and 30 at its audible radius,
where the curve is continuous into the tail and reaches the floor at 1320 m. No engine attenuation
model expresses that shape, so `CSVM/src/Mech3/SoundFalloff.cs` computes it and drives the player's
level itself, on every positional path alike: the world's ambient emitters and one-shots, an
aircraft's gun loop and dry cue, and a mount's gun voice all carry a player with no attenuation
model and no `MaxDistance`, since either would multiply a second curve onto the decoded one.

⚠ **No listener-side term scales the reach: the distance the curve reads is the raw world distance
from the camera to the emitter, and the radii are the authored ones.** Each place such a term could
sit in the retail build has been read and holds none:

- **DirectSound's 3D listener.** The software path `FUN_00597c20` touches only the voice buffer
  (`SetVolume` `+0x3c`, `SetPan` `+0x40`, `GetFrequency`/`SetFrequency` `+0x20`/`+0x44`); no
  listener object, distance factor or rolloff factor is set. The hardware arm that owns a listener
  interface (`DAT_00639cbc`, reached through `FUN_00597b40` and `FUN_00597a00`'s second branch) is
  gated on `DAT_00639cb4`, which is 0 in the image and whose one writer (`0x005923a0`) stores 0.
- **The definition load.** `FUN_00592c90` copies `RANGE`'s two floats into `+0x1c`/`+0x20`
  unscaled (defaults `50`/`400` when the key is absent); its post-load hook `DAT_00639ec8` is null
  on the one load path (`FUN_00592340` passes 0).
- **Where the listener stands.** `FUN_00597a00` copies a 12-float world matrix to `DAT_00639e34`,
  the position landing at `DAT_00639e58`; its caller `FUN_004d31d0` hands it the camera node's own
  world matrix (`+0x44`), at most once per frame. An emitter's position is its `SOUND` node's world origin
  (`FUN_004e0e60`), refreshed by the walk over the world's sound-node list (`FUN_004db750`). Both are in world
  units, as the port's are.
- **Gain multipliers.** The level takes `VOLUME x SoundVolume x` a per-category option level
  (`DAT_00639e8c`, default 1.0 at `DAT_00639e88`; the callback at `0x004803c0` returns the `VOICE`,
  `MUSIC` or `SFX` option level), and `FUN_00593620` clamps any product at or above 1 to 0 dB, so
  none of them can lift a distant voice.

The distance itself is the bit-hack square root at `0x00597dac`, which reads long rather than short
between powers of four, so it cannot add reach either. CSVM nevertheless ships a factor of **2.5**
on both radii and on the cull that follows them (`SoundFalloff.ShippedRangeScale`, overridden by
`--sound-range-scale=`, `docs/cli.md`): at the authored radii the world emitters, the C1 police
siren and the track train among them, are heard from far closer in than the original's are at the
controls, and 2.5 is the reach that matched. Nothing above stands behind that factor, so it is a
remake-only departure; `--sound-range-scale=1` reads the radii as authored, which is what the
suites pinning this law read at.

⚠ **`VOLUME` converts on the same ten-decibels-per-doubling scale, not the usual `20 log10`.**
`FUN_00593620` returns 0 at or above 1, `-10000` at or below `2^-10`, and `1000 log2(gain)`
between, so the `0.8` four of the gasbag explosions carry is 3.2 dB down rather than 1.9.

⚠ **An aircraft's weapon cues are in the positional class, not the cockpit one.** Every caliber's
`LOOPED_SOUND_NAME` (`snd_30cal` through `snd_70cal`, plus `snd_turretgun` and `snd_chaingun`)
carries `3D` + `LOOPED` + `RANGE`. The `NO_AMMO_WARNING` cue `snd_emptyclip` carries `3D` and
`RANGE [80, 800]` but no `LOOPED`, a dry trigger being one click. So a gun
loop is a point in the world, which is what the remake plays it as: the pilot's own guns stay flat
because that is what the pilot hears, and every other aircraft's come from its own position, culled
at the 1.1 x above. Those distances are short, 150 m for a 30-cal (165 m with the margin) against
800 m for the dry cue, so traffic firing 300 m off is inaudible by the data's own numbers.
⚠ **The flags say a definition MAY be positional, not that the original placed it.** The original's
fire path hands its firing loop the aircraft's own position every tick, and hands the dry cue no
position at all, which its play entry takes as flat ([weaponFire.md](../org/weaponFire.md)). ⚠ Nothing here
authorises a pitch term: `RANGE` is a gain model, and `CAP-09` measured no Doppler on the original's
world emitters at all.

⚠ **A turret's gun voice is a different cue on a different clock from an aircraft's.** It is the
`ai.zrd` entry's own `SOUNDS.CANNON` (`snd_chaingun`, `RANGE [30, 200]`) rather than its weapon's
`LOOPED_SOUND_NAME`, it plays from the firepoint the round just left, and it is held by a 0.5-second
lease each shot renews instead of by a trigger, so a mount firing slower than that lapses between
rounds. Eleven of the 42 shipped entries reach neither gate and fire silently, the turret trucks
among them. `snd_turretgun` belongs to `wep_23`/`wep_29` and is reached only through the vehicle fire
path, which is what a patrol boat and a turret truck run ([turrets.md](turrets.md)).

Each plane def names its own engine loop via `engine_sound` / `cockpit_engine_sound`
(see [vehicle.md](vehicle.md)), e.g. `engine_sound snd_bloodhawkengine` → bloodhawk.wav.
`damaged_engine_sound` is an array of swap candidates for that same slot (`snd_damagedengine`
install-wide), not a second loop blended over it; the entry's two floats are a pitch-multiplier
range drawn once per swap. See vehicle.md.

### The whole level path, retail against the remake

The two builds run the same curve under different mixers. Read end to end, from the definition's
`VOLUME` to what one speaker is driven with, they stand within about four decibels of each other,
and the only divergence that favours the retail build is three of those.

| Stage | Retail | CSVM |
|---|---|---|
| definition level | `1000 log2(VOLUME x SoundVolume x category)` hundredths of a decibel, `FUN_00593620` called from `FUN_00597c20` and from the play entry `FUN_00593b80` | `SoundFalloff.VolumeDb`, the same ten-decibels-per-doubling conversion |
| distance | the bands above, added in hundredths at `0x00597e98` (the ramp) and `0x00597e47` (the tail) | `SoundFalloff.AttenuationDb`, the same bands |
| category level | `SfxVolume` 0.5, `MusicVolume` 0.65, `VoiceVolume` 0.75 as shipped (`FUN_0043fb50`, `0x3f000000` / `0x3f266666` / `0x3f400000`), chosen by the classifier at `0x004802e0`, whose tokens `NOROGUE`, `WINGMAN`, `VOICE`, `MUSIC`, `SFX` and `OPTIONAL` set bits 1, 2, 4, 8, 0x10 and 0x40 of def`+0x10`, read by the level callback at `0x004803c0` (installed at `0x004a811d`) | the `Effects`, `Music` and `Voice` buses at `20 log10(level/100 x master/100)`, `Utils/AudioMix.cs`, the same shipped level of 50 out of 100 |
| output gain | `SoundVolume`, default 1.0 (`FUN_00591af0`) | bus 0, the developer `--volume=` |
| per-voice ceiling | the play call's own gain through the same product, voice`+0x24`; a `SOUND` node plays at gain 1.0 (`FUN_004e0d60` passes `0x3f800000`), so it never bites | none |
| write | `IDirectSoundBuffer::SetVolume`, vtable `+0x3c`, floored at `-10000` | `AudioStreamPlayer3D.VolumeDb`, the level at the listener while the attenuation model is `Disabled` and `MaxDistance` is 0 |
| stereo | `SetPan`, vtable `+0x40`, `1600 x (offset . listener right axis) / dist` (`0x00597df8`, scale at `0x00609428`): the FAR channel loses up to 16 dB and the near one loses nothing, so a voice dead ahead drives both channels at the level above | the engine's own stereo law, cosine of half the azimuth at `panning_strength` 1, which is constant power: both channels 3.01 dB down dead ahead and astern, the near channel at the full level abeam and the far one silent |

⚠ **No term in that chain is worth the reach the shipped 2.5 factor buys, so it stands as a
remake-only departure rather than a ported constant.** The police siren
(`RANGE [200, 1200]`, no `VOLUME` key) with both mixers at their maximum, in decibels at the near
channel:

| distance | retail | CSVM at 1, dead ahead | CSVM at 1, abeam | CSVM at the shipped 2.5 |
|---|---|---|---|---|
| 250 m, inside the shelf | 0.00 | -3.01 | 0.00 | 0.00 |
| 700 m, mid band | -20.00 | -23.01 | -20.00 | 0.00 |
| 1200 m, the audible radius | -30.00 | -33.01 | -30.00 | -11.63 |

At the shipped levels instead (retail `SfxVolume` 0.5, CSVM `Effects` 50 under `Master` 100) the
retail column reads -10.00, -30.00 and -40.00 against -9.03, -29.03 and -39.03 dead ahead, because
the two option curves differ: the retail slider converts on the `VOLUME` scale above and the bus on
Godot's `20 log10`, so the same slider position stands 3.98 dB louder here. The train
(`RANGE [600, 1200]`) reads the same offsets at 650, 900 and 1200 m, the law and both mixers being
shared. So the measured spread over the whole chain is 3.01 dB against the remake and 3.98 dB for
it, where scaling the radii by 2.5 is worth up to 20 dB inside the band and moves the cull from
1320 m to 3300 m. ⚠ The one number not measured here is the decoded PCM amplitude against what
DirectSound gets from the same archive: both builds read `soundsh`, whose `PLAYBACK_FORMAT HIGH` is
the 22050/16/1 the reader produces, and `WavFile` applies no gain of its own, but the samples have
not been compared.

### A definition is pitched only when it carries FREQUENCY

⚠ **The flag is a capability on the buffer, not a hint, and a definition without it plays at its
WAV's own rate whatever pitch the caller computes.** `FUN_0059b950` builds the definition's master
buffer, and the `DSBUFFERDESC` it fills gets `DSBCAPS_CTRLFREQUENCY` (`0x20`) only when the flag
byte at def+0xc has bit 5 set (`TEST AL,0x20` at `0x0059b9c6`, `OR` at `0x0059b9ca`, the create at
`0x0059ba0b`); the bit is what the `FREQUENCY` token sets in the parser `FUN_00592c90`. Every
voice is a `DuplicateSoundBuffer` copy of that master (`FUN_00593490` at `0x00593520`, master
handle at def+0x54), and a duplicate inherits the master's caps, so the capability is decided once
per definition and no voice can opt back in.

The write itself is `FUN_00597740`: it forms `(int)(def[+0x2c] * pitch)`, where def+0x2c is a float
holding the WAV header's own `nSamplesPerSec` (stored at `0x0059bbd1`), clamps the result to
**55200 Hz** (`0xd7a0`, at `0x005977f5`) and **4000 Hz** (`0xfa0`, at `0x00597803`), and calls
`IDirectSoundBuffer::SetFrequency` (vtable +0x44) at `0x00597813`. On a buffer created without the
control that call returns `DSERR_CONTROLUNAVAIL`, which the error decoder `FUN_005992c0` names and
`zsnd_parm.cpp:315` logs; the loop keeps playing, unpitched. Since every shipped WAV is 22050 Hz,
those two bounds are a playback-rate multiplier of 0.181 to 2.503.

⚠ **The shipped engine loops split on this flag, and the damaged one is on the wrong side of it.**
All eleven `engine_sound` definitions carry `FREQUENCY`, so the throttle curve reaches them.
`snd_damagedengine` (`engine_damaged.wav`, `LOOPED 3D SFX RANGE [130, 420]`) does not, and neither
does any `*_cp` cockpit loop, so the drawn damaged-engine multiplier and the throttle curve are
both refused and those loops run at 22050 Hz flat. A port that applies the multiplier anyway runs
the damaged engine roughly half an octave under the original. The multiplier's own decode is
[vehicle.md](vehicle.md#what-makes-an-airframe-damaged).

## Sound groups

A sibling of `SETS`: the weighted random sound groups a one-shot `SOUND` animation event resolves
through when it names a group instead of a plain `snd_*` definition. The combat/destruction
one-shots go through these, `air_mixed_exp_sg`, `ground_mixed_exp_sg`, `plane_destroy_sg`,
`bullet_hit_sg`, `bullet_warning_sg`, `window_hit_sg`. Parsed by `SoundDefs.LoadGroups`.
⚠ The last three are the incoming-fire set, and two of them are named in `player.json` rather than
by a `SOUND` event: which one plays on a hit, that all of them play flat despite `snd_ricochet1-4`
carrying `3D`, and which interval opens a canopy hole are in
[weaponFire.md](../org/weaponFire.md), "The incoming-fire cues".

Entry shapes (all start with the group name):

```
[name, "DYNAMIC_WEIGHTS", factor, [member], [member], …]          -- uniform members + recency
[name, "MUSIC", "DYNAMIC_WEIGHTS", factor, [member], …]           -- as above; a music category
[name, [member, "WEIGHT", w], [member, "WEIGHT", w], …]           -- explicit per-member weight
[name, [dialogueRoot, [line], [line], …]]                         -- a VO chain, NOT a weighted group
```

- **`DYNAMIC_WEIGHTS factor`**, the `factor` (0.5 everywhere it appears) is a **recency scalar**:
  the member returned last has its weight multiplied by it on the next pick, so the same clip is
  less likely to repeat back-to-back. Members are single-element lists, each weight 1.
- **Explicit `WEIGHT`**, `snd_plane_die`/`snd_plane_dmg` give each member its own weight and no
  recency decay; their `snd_nothing` at 0.7 is a 70% chance of silence.
- **VO dialogue chains** (`snd_assignments`, `snd_HI1*`; 222 groups) nest a list where a weighted
  member's name would be: `[firstLine, [line], [line], …]`, an ordered sequence of snd names.
  They contribute no weighted member; the parser keeps them as `SoundGroup.Chains`, `MissionRadio`
  speaks them in order as one radio call, and `WorldSounds.Prewarm` decodes their lines. No chain
  group mixes chains with weighted members, and each holds exactly one chain (2–13 lines). Every
  line of every chain an objective cues is a `QUEUE` definition; not one is `3D`. A group with neither
  members nor chains is not registered. Music `*_sg` groups parse but no `SOUND` event names them:
  music is cued by name from the missions' `objectives.zrd` and from the menu scripts, not by an
  animation event (see [The music channel](#the-music-channel)).
- **Combat-voice variant groups**: 466 entries named `snd_<FAMILY>-A_id<N>_random`
  (`DYNAMIC_WEIGHTS 0.5` over one pilot's `-A/-B/-C` takes of one family), the data's own answer
  to how a take is picked; see [combat-voice.md](combat-voice.md).

A `SOUND` event's NAME is resolved against `SETS` first, then `SOUND_GROUPS`: `air_mixed_exp_sg`
picks one of `snd_exp_hit1/2/3/3a/5`, each of which is an ordinary `SETS` entry
(`snd_exp_hit1` → `explosion_1.wav`). See [anim-definitions.md](anim-definitions.md) for the
`SOUND` vs `SOUND_NODE` distinction (only the latter is ambient looping world audio).

## The music channel

`MUSIC` is a routing flag, not a label. A `SETS` definition carrying it, or one whose WAV name
starts with `mu`, plays on a single streaming channel; so does a `SOUND_GROUPS` group whose name
starts with `mu`. ⚠ It selects the streaming channel and nothing else: what a definition without
it plays on is decided by `3D` and `QUEUE`, not by the absence of `MUSIC`
([which channel a definition plays on](#which-channel-a-definition-plays-on)). The 33 `music_*.wav` tracks in
`soundsh`/`soundsl` are reached through seven groups (`music_prebattle_sg`, `music_battle_sg`,
`music_battlesuccess_sg`, `music_missionsuccess_sg` and the three `*obj_sg` stingers) plus the
plain definitions `snd_music_splash`, `snd_instantaction` and `snd_spicyairtales`.

The channel holds one track: a new cue hard-cuts the old one, a cue for the track already playing
is ignored, and nothing crossfades. Which of the six numbered variants plays is the group's own
weighted-random pick, not a chapter or an act; the two-take stingers alternate instead. Only
`battle1-6` carry `LOOPED`, and the resolver forces prebattle to loop as well.

The runtime behind all of that, including the battle timer, the fade rates and which tracks ship
with no trigger at all, is [`org/music.md`](../org/music.md).

## Player curves

The first two are clamped two-point ramps `(inStart→inEnd maps outStart→outEnd)`; the third is
authored as one but does not run as one:

| Block | Meaning |
|---|---|
| `engine_sound` | pitch 0.6→1.0 over throttle 0.1→1.0; volume flat 1.0 |
| `prop_sound` | the **overspeed dive whine**: volume 0→0.5 over speed 1.0→1.1× `fd_speed`, pitch 0.65→1.25 over 1.0→1.2×. Drives the engine audio's second slot, whose definition is the vehicle def's own `prop_sound` key |
| `rattle` | `snd_planeshake`, the airframe rattle: authored as volume 0→1 over speed 1.0→1.2× `fd_speed`, **run as a gate**. Only `speed_range`'s first value reaches a reader; the loop is silent below `1.0× fd_speed` and at full level, the engine slot's own, from there upward |

⚠ **The rattle's ramp is authoring the game ignores, and re-adding it silences the loop.** The
`volume_range` pair and `speed_range`'s second value are parsed into globals with no read xref
anywhere in `crimson.exe`, so a port that evaluates the ramp plays the rattle at a few percent of
level through every speed a dive reaches. Addresses, the gate's own three conditions and the
hardcoded call gain: [`org/shakes.md`](../org/shakes.md#the-rattle-sound-is-a-gate-at-full-level-not-the-authored-ramp).

⚠ **The whine never sounds in the retail install, and `snd_enginewhine` is not its WAV.** The
second slot's definition comes from the vehicle def's `prop_sound` string key; no shipped def
authors it, the field has no compiled default, and `snd_enginewhine` appears as a literal nowhere
in `crimson.exe`. The curves above are still read, and would drive the slot if a def ever named
one. A spectral comb in a reference dive recording was previously read as the whine mixed 12–18 dB
under the engine; that reading is refuted. What moves the engine note in a dive is the engine
slot's own **manoeuvre and attitude** terms, decoded in
[vehicle.md](vehicle.md#the-engine-slots-pitch-and-gain-are-not-throttle-alone). ⚠ Not a
speed-driven term, which this page said before the expression was read: the quantity is angular
velocity, and airspeed reaches the engine slot nowhere.

## WAV format

All game WAVs (`soundsh.zbd` high-quality / `soundsl.zbd` low) are **MS ADPCM**
(fmt tag 2, 4-bit, 22050 Hz, mono or stereo). Godot only loads PCM/IMA-ADPCM/QOA, hence
the pure-C# decoder in `WavFile.cs`.

## Evidence & limits

This page states current format facts. Claim-specific evidence and limits remain beside the claims they support.
