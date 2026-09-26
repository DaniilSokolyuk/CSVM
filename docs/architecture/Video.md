# Video

The managed MPEG-1 decoder for the ten `.mpg` cinemas the retail install ships: the system-stream
demultiplexer, the video and layer II audio decoders under it, and the pure pieces they are built
from. No type here touches the engine, which is what lets a plain unit test play a whole file.

One `## src/...` entry per module, body at most 8 lines.

Traps do not live here; the rule is in `docs/architecture.md`. The container and codec facts, the
per-file parameters and where the files are named from are in
[../formats/cinemas.md](../formats/cinemas.md).

## src/Video/MpegMovie.cs
The surface a caller holds: `FromFile` or `FromBytes` opens one cinema, `Width`, `Height`,
`FrameRate` and `PixelAspectRatio` report what that file's own headers declare, `NextFrame` and
`NextAudioFrame` hand out the next picture and the next block of sound, and `Rewind` restarts
both. `HasAudio`, `AudioSampleRate` and `AudioChannels` describe the sound track, opened on first
use so a movie played silent never pays for it. It owns an `MpegSystemStream`, an
`MpegVideoDecoder` and an `MpegAudioDecoder` and adds nothing beyond joining them, so a caller
that needs one of the three separately can take it directly. Read `MpegSystemStream.cs` next.

## src/Video/MpegSystemStream.cs
Walks a whole system stream in memory: packs, system headers, and the packets of each elementary
stream, with each packet's presentation timestamp off the container's 90 kHz clock. Each stream
is joined into one buffer because both decoders read across packet boundaries, and the audio
packets are kept alongside so a caller can see where the sound track's own clock starts.
`MpegPacket` is the packet as a window into the file's own bytes rather than a copy, so the file
array stays alive as long as the packets do. Read `MpegVideoDecoder.cs` next.

## src/Video/MpegVideoDecoder.cs
The decoder proper: sequence header, picture, slice, macroblock and block, over an
`MpegBitReader`. It keeps the three pictures the standard's prediction needs and rotates them, so
a reference picture is held back one call and comes out after the bidirectional pictures that
precede it in display order; the last one falls out at the end of the stream. Frame times are the
picture's ordinal over the sequence's own rate, offset by the container's start time. Every
parameter comes from the stream's own headers, because two of the ten files differ from the
other eight. Read `DctBlock.cs` and `MotionCompensation.cs` next.

## src/Video/MpegAudioDecoder.cs
The layer II decoder: frame header, bit allocation, scale factors, requantisation and the twelve
granules of subband samples, over an `MpegBitReader`. Each frame is taken at the offset the last
one's declared size gives rather than by hunting for a sync word, so `ResyncCount` reports lost
bytes instead of ordinary padding; nine of the ten cinemas end their sound track with a run of
zeroes, which is the end of the stream and not a loss. Sample times are the frame's ordinal over
the sample rate, offset by the container's audio start time, so sound and picture share a clock.
Read `AudioLayer2Tables.cs` and `AudioSubbandSynthesis.cs` next.

## src/Video/MpegBitReader.cs
The bit-level reader every symbol is read through: fixed-width fields, byte alignment, stuffing
runs, the `00 00 01` start-code scan, the 23-bit lookahead that ends a slice, and the
variable-length code walk over a `VideoVlcTables` table. Reading past the end yields zero and
does not move, so a truncated file ends the decode rather than throwing. Read
`VideoVlcTables.cs` next.

## src/Video/VideoVlcTables.cs
The variable-length code tables of ISO 11172-2 as data: address increment, the three macroblock
type tables, coded block pattern, motion code, the two DC size tables and the run/level table.
Each is a binary tree flattened into (next, value) pairs that `MpegBitReader.ReadVlc` walks one
bit at a time. `CSVM.Tests/VideoVlcTableTests.cs` checks individual codes against the standard's
own tables and checks each tree for reachability, which is the only defence against a table that
is wrong by one and decodes to plausible rubbish.

## src/Video/AudioLayer2Tables.cs
The layer II tables of ISO 11172-3 as data: the sample and bit rates a frame header's indices
name, the scale factor base, the four-step lookup that turns a bit rate and a sample rate into a
bit allocation table and each subband's field width, and the seventeen quantisers those fields
select. `Layer2Quantizer` is one quantiser, whose zero levels mean the subband carries nothing.
Only the MPEG-1 rows are here, since the header parse rejects every other version first.
`CSVM.Tests/AudioLayer2TableTests.cs` checks all of it against the standard's own tables.

## src/Video/DctBlock.cs
The 8x8 block: the zig-zag scan order, the two default quantiser matrices, the per-coefficient
scale factors the transform folds in, `Dequantize` and `InverseTransform`. The transform is the
integer one decoders use in place of the standard's real-valued definition, and its scale factors
are held to eight bits, so its output is within the mismatch the standard permits rather than
exact. Read `MpegVideoDecoder.cs` for the caller.

## src/Video/AudioSubbandSynthesis.cs
One channel's polyphase synthesis filter bank: 32 subband samples in, 32 PCM samples out, over
the 1024-sample history the standard's windowing runs across. The window is the standard's D
coefficients scaled by 32768 with their sign rule folded in, laid down twice so the walk needs no
wrap test. `Reset` clears the history, which is what makes a second pass over a stream produce
the samples the first one did.

## src/Video/MotionCompensation.cs
`Predict` fetches one macroblock of one plane from a reference picture and writes or averages it
into the current one. Vectors are in half-pel units, so an odd component averages the samples it
falls between; at a plane edge the missing neighbour repeats. A vector whose block falls outside
the plane predicts nothing, which is how a corrupt stream degrades instead of throwing.

## src/Video/VideoFrame.cs
One decoded picture: the three 4:2:0 planes padded out to whole macroblocks, the picture size
inside them, and the presentation time. `WriteRgba` and `ToRgba` are the BT.601 conversion a
caller that wants pixels uses. The decoder hands out the same three frames over and over, so a
frame is valid only until the next one is asked for.

## src/Video/AudioFrame.cs
One decoded sound frame: 1152 samples per channel as floats interleaved by channel, the rate and
channel count they were decoded at, and the moment the first of them is heard on the container's
clock. Samples are nominally within plus or minus one and loud material leaves one slightly
outside, so a caller feeding fixed-point hardware clamps. The decoder hands out the same frame
over and over, so it is valid only until the next one is asked for.

## src/Video/MoviePlayback.cs
A movie on a clock: it owns an `MpegMovie`, takes elapsed seconds from its caller, and keeps the
picture due now as RGBA in a buffer it rewrites in place. Which picture that is comes from the
frames' own presentation timestamps, so no rate is written down here and the two cinemas that
differ from the other eight need no case of their own. A play count of zero plays endlessly, and
every pass after the first restarts through `MpegMovie.Rewind`. A step longer than the cap counts
as the cap, so a window that was not drawing comes back late instead of decoding pictures nobody
saw. The texture side of this is `CSVM.UI.Screens.MovieSurface`.

## src/Video/CinemaPlayback.cs
A cinema playing with its sound: a `MoviePlayback` for the picture, the movie's own track handed
out as clamped PCM, and the two kept together. The clock is the sound the device has actually
played, since a device consumes at exactly the rate it was opened at where a frame callback does
not, so a long file cannot drift away from its own sound. The two streams' container start times
are taken against each other here, as leading silence or as a lagged picture clock, and neither
arm discards a sample. Past the last sample the picture runs on the caller's own step instead,
because two of the ten cinemas end their sound first. The engine half is `CSVM.UI.Screens.CinemaScreen`.
