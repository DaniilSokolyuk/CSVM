# Which enhanced pass lays a pattern over the screen

Question: under `--graphics=enhanced` a fine pattern covers the whole frame, and the faithful
presentation shows none. Which of the mode's screen-space passes draws it, and what removes it
without removing the pass.

## Instruments

`Measure-Hf.ps1 -Path <png...> -Rect x,y,w,h` reports three numbers over a rectangle, in luminance
units on a 0-255 scale:

- `chk2`, the mean `|L(0,0) - L(1,0) - L(0,1) + L(1,1)|` over disjoint 2x2 blocks. Only a signal
  that alternates pixel to pixel survives it, which is what a screen-space sample pattern is.
- `lap`, the mean `|4L - (left+right+up+down)|`, all high-frequency energy including real detail.
- `sd` and `mean`, the contrast and level the other two ride on. Together they are the confinement
  proof: a change that drops `chk2` while holding `mean` and `sd` removed noise, not structure.

`Diff-Shot.ps1 -A <png> -B <png> -Out <png>` writes the amplified absolute difference and reports
what fraction of the frame moved, the mean and peak luminance step, and the difference's own
`chk2`. Differencing isolates one pass's contribution, so the pass that resolves through a pattern
is the one whose *difference* alternates.

Renders go through `RunProbe.ps1 -Resolution 1920x1080 --screenshot=...`. A plain `--screenshot`
implies `--det`, which pins `--jitter` to 0; a `--shots` burst must not be used, its frames being
previous renders carrying the burst camera's dither.

## Bisect

One pose per closed door at 1920x1080, C1 waterfall (`--freecam --chapter=C1
--pos=-7720,60,-3380 --lookat=-7868,40,-3449 --frames=120`), each differenced against the full
enhanced frame:

| door | frame changed | mean abs step | chk2 of the difference |
|---|---|---|---|
| `--no-ssao` | 10.9 % | 0.61 | 0.101 |
| `--no-ssr` | 13.0 % | 7.85 | 0.069 |
| `--no-glow` | 0.0 % | 0.00 | 0.000 |
| `--no-soft-shadows` | 81.4 % | 8.17 | **2.168** |

The soft-shadow pass is the only one whose contribution alternates, and it covers most of the
frame. The C2 city pose agrees (30.0 % of the frame, chk2 2.088, every other door under 0.05).

## The lever

Whole frame at the same pose. The hard-shadow render is the floor: it is the same scene with the
penumbra filter not running.

| build | chk2 | excess over the floor | gpu_ms |
|---|---|---|---|
| faithful presentation | 0.918 | - | - |
| enhanced, hard shadow (floor) | 1.094 | 0 | - |
| angular distance 2.0, filter Medium | 2.217 | 1.123 | 2.55 |
| angular distance 1.0, filter Medium | 1.919 | 0.825 | 2.45 |
| angular distance 2.0, filter High | 1.403 | 0.309 | 4.63 |
| angular distance 2.0, filter **Ultra** | 1.252 | 0.158 | 5.78 |
| angular distance 1.0, filter Ultra | 1.160 | 0.066 | 5.26 |

Narrowing the penumbra removes a quarter of the excess and changes the judged edge width; raising
the filter quality removes 86 % of it and leaves the width alone. The filter, not the width, is
what the pattern is made of. `gpu_ms` is the `--perf` window mean on an RTX 5080 at 1920x1080; the
C2 city pose costs 1.03 ms at Medium against 1.38 ms at Ultra, far less, because far less of that
frame sits inside the shadow cascades.

## Confinement

Medium against Ultra at the same pose, 2560x1440: `chk2` 2.037 to 1.112, while `mean` moves 83.98
to 83.95 and `sd` 40.201 to 40.200. The difference image is a woven mesh over every lit surface
with no scene structure in it.