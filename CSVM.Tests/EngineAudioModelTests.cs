using System.Collections.Generic;
using CSVM.Flight.Airframe;
using CSVM.Flight.Audio;
using CSVM.Mech3;
using Xunit;

namespace CSVM.Tests;

/// <summary>
/// The decoded engine-audio slot model, pinned against the shipped vehicle data rather than against
/// the code that reads it: which slots an airframe actually names, and with what. Two of the three
/// facts here refuted a live reading, so a reader change that quietly restores one (a whine def out
/// of nowhere, a damaged engine that stops being a swap) fails here. The curve maths and the cull
/// are in-engine, in the <c>ai-damage-stages</c> and flight suites.
/// </summary>
public class EngineAudioModelTests
{
    private static readonly string[] AllPlaneNodeNames =
    {
        "player_bhawk", "player_fury", "player_peacemaker", "player_kestrel", "player_fbrand",
        "player_warhawk", "player_balmoral", "player_pfighter", "player_autogyro",
        "player_avenger", "player_brigand",
    };

    private static string SharedZrdr =>
        SessionPaths.PreferUnzipped(System.IO.Path.Combine(TestData.ExtractedRoot!, "zrdr.zip"));

    /// <summary>Slot 1, the overspeed whine, is unassigned on every airframe in the install: the KEY
    /// is real and read (<c>prop_sound</c>, VDEF+0x74), and no shipped def authors it, so the
    /// original plays no whine at all. A non-null here means either the data was misread or a
    /// default was invented, and both would put a loop in a dive that the original has not got.</summary>
    [ExtractedDataFact]
    public void NoAirframeNamesAWhineDefinition()
    {
        var named = new List<string>();
        foreach (var node in AllPlaneNodeNames)
        {
            if (PlaneStats.Load(SharedZrdr, node).WhineSound is { } player)
                named.Add($"{node} (player) → {player}");
            if (PlaneStats.LoadForAi(SharedZrdr, node).WhineSound is { } ai)
                named.Add($"{node} (ai) → {ai}");
        }

        Assert.True(named.Count == 0, string.Join("\n", named));
    }

    /// <summary>The airframe rattle, unlike the whine, IS assigned on every airframe, and its whole
    /// law is one number: the speed fraction it starts at. The shipped block puts that at 1.0, the
    /// plane's own rated maximum, so every airframe rattles from rated max upward.
    /// <c>volume_range</c> is deliberately absent from <see cref="PlaneStats"/>: the original parses
    /// it into a global no instruction reads (docs/org/shakes.md).</summary>
    [ExtractedDataFact]
    public void EveryAirframeRattlesFromItsOwnRatedMaximum()
    {
        foreach (var node in AllPlaneNodeNames)
        {
            var stats = PlaneStats.Load(SharedZrdr, node);
            Assert.Equal("snd_planeshake", stats.RattleSound);
            Assert.Equal(1f, stats.RattleSpeedGate, 4);
        }
    }

    /// <summary>The rattle is a GATE, not a ramp: silent below the authored fraction of fd_speed and
    /// at its one level from it upward, however far past it the dive goes. A ramp here is what left
    /// the loop inaudible at the speeds a dive actually reaches, so the flat top is the pin that
    /// matters, not just the edge. The level is the port's 1.3, a chosen step over the original's
    /// 1.0.</summary>
    [Fact]
    public void RattleGainIsAGateAtFullLevelRatherThanARamp()
    {
        var stats = new PlaneStats { EngineSound = "snd_normal", RattleSpeedGate = 1f };

        float level = EngineAudioCurves.RattleLevel();
        Assert.Equal(1.3f, level);
        Assert.Equal(0f, EngineAudioCurves.Rattle(stats, 0f));
        Assert.Equal(0f, EngineAudioCurves.Rattle(stats, 0.999f));
        Assert.Equal(level, EngineAudioCurves.Rattle(stats, 1f));
        Assert.Equal(level, EngineAudioCurves.Rattle(stats, 1.05f));
        Assert.Equal(level, EngineAudioCurves.Rattle(stats, 1.2f));
        Assert.Equal(level, EngineAudioCurves.Rattle(stats, 3f));

        // The gate moves with the authored fraction rather than being pinned at 1.
        var late = new PlaneStats { EngineSound = "snd_normal", RattleSpeedGate = 1.5f };
        Assert.Equal(0f, EngineAudioCurves.Rattle(late, 1.2f));
        Assert.Equal(level, EngineAudioCurves.Rattle(late, 1.5f));
    }

    /// <summary>The damaged engine is a DEFINITION SWAP on slot 0 with a pitch multiplier drawn once
    /// per swap, never a second loop blended over the healthy one. The install authors exactly one
    /// entry, inherited from basic_airplane by every plane, with its flag byte set and the range
    /// 0.0 to 1.0. The draw is real and nobody hears it: the definition it lands on takes no
    /// frequency write (see <see cref="OnlyThePlainEngineLoopsAcceptAFrequencyWrite"/>).</summary>
    [ExtractedDataFact]
    public void EveryAirframeSwapsOneDamagedEngineDefinitionWithARandomisedPitch()
    {
        foreach (var node in AllPlaneNodeNames)
        {
            foreach (var stats in new[]
                     {
                         PlaneStats.Load(SharedZrdr, node), PlaneStats.LoadForAi(SharedZrdr, node),
                     })
            {
                Assert.Equal("snd_damagedengine", stats.DamagedEngineSound);
                Assert.True(stats.DamagedEnginePitchRandom, $"{node}: the swap's pitch flag is clear");
                Assert.Equal(0f, stats.DamagedEnginePitchLo);
                Assert.Equal(1f, stats.DamagedEnginePitchHi);
                Assert.False(string.IsNullOrEmpty(stats.EngineSound), $"{node}: no engine definition");
            }
        }
    }

    /// <summary>Slot 0's healthy definition is the airframe's own, not one shared default: eleven
    /// planes name at least eight distinct engine loops between them. The count is a floor, so a
    /// plane gaining or losing its own loop does not fail this, a reader collapsing every plane onto
    /// one inherited default does.</summary>
    [ExtractedDataFact]
    public void EngineDefinitionsArePerAirframe()
    {
        var distinct = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
        foreach (var node in AllPlaneNodeNames)
            distinct.Add(PlaneStats.Load(SharedZrdr, node).EngineSound);

        Assert.True(distinct.Count >= 8, $"only {distinct.Count} distinct engine definitions: {string.Join(", ", distinct)}");
    }

    /// <summary>D31: every one of the eleven player airframes authors <c>cockpit_engine_sound</c>
    /// (either its own or `basic_airplane`'s inherited fallback), so
    /// <see cref="PlaneStats.CockpitEngineSound"/> being null is a defensive branch for a plane the
    /// shipped install does not actually contain, not a live case any of the 11 hit.</summary>
    [ExtractedDataFact]
    public void EveryAirframeAuthorsACockpitEngineDefinition()
    {
        foreach (var node in AllPlaneNodeNames)
        {
            foreach (var stats in new[]
                     {
                         PlaneStats.Load(SharedZrdr, node), PlaneStats.LoadForAi(SharedZrdr, node),
                     })
            {
                Assert.False(string.IsNullOrEmpty(stats.CockpitEngineSound),
                    $"{node}: no cockpit_engine_sound authored");
                Assert.EndsWith("_cp", stats.CockpitEngineSound);
            }
        }
    }

    /// <summary>D31's def-selection rule, pure and headless: the cockpit swap applies only when the
    /// airframe authors it, yields to the damaged swap when both conditions are live (no def authors
    /// a damaged-cockpit variant), and both fall back to the plain <c>engine_sound</c> definition.
    /// <c>DamagedEnginePitchRandom</c> stays false so the random draw branch (needing a live
    /// <c>RandomNumberGenerator</c>) is never reached, untouched by this precedence rule.</summary>
    [Fact]
    public void EngineDefForPicksDamagedOverCockpitOverNormal()
    {
        var stats = new PlaneStats
        {
            EngineSound = "snd_normal",
            CockpitEngineSound = "snd_cockpit",
            DamagedEngineSound = "snd_damaged",
            DamagedEnginePitchRandom = false,
        };

        Assert.Equal(("snd_normal", 1f),
            EngineAudioCurves.EngineDefFor(stats, damaged: false, rng: null!, cockpitView: false));
        Assert.Equal(("snd_cockpit", 1f),
            EngineAudioCurves.EngineDefFor(stats, damaged: false, rng: null!, cockpitView: true));
        Assert.Equal(("snd_damaged", 1f),
            EngineAudioCurves.EngineDefFor(stats, damaged: true, rng: null!, cockpitView: false));
        Assert.Equal(("snd_damaged", 1f),
            EngineAudioCurves.EngineDefFor(stats, damaged: true, rng: null!, cockpitView: true));
    }

    /// <summary>The decoded damage gate: the swap needs the worst zone BELOW a quarter health, so a
    /// graze keeps the healthy loop. Engine-out is the mask's other modelled bit and swaps on its
    /// own however healthy the airframe is.</summary>
    [Fact]
    public void EngineDamagedNeedsAQuarterHealthOrAnEngineOut()
    {
        Assert.False(EngineAudioCurves.EngineDamaged(1f));
        Assert.False(EngineAudioCurves.EngineDamaged(0.9f));
        Assert.False(EngineAudioCurves.EngineDamaged(0.25f)); // the test is strict: at is not below
        Assert.True(EngineAudioCurves.EngineDamaged(0.2499f));
        Assert.True(EngineAudioCurves.EngineDamaged(0f));
        Assert.True(EngineAudioCurves.EngineDamaged(1f, engineDead: true));
    }

    /// <summary>The pitch law is a linear DRAW across the entry's own two floats, not a derivation
    /// from damage: the draw's ends are the range's ends, its middle is the range's middle, and a
    /// CLEAR flag byte holds the multiplier at 1 instead of drawing. The last case is what keeps an
    /// unflagged entry at its own rate rather than pinning it low.</summary>
    [Fact]
    public void DamagedEnginePitchIsDrawnLinearlyAcrossTheAuthoredRange()
    {
        var drawn = new PlaneStats
        {
            EngineSound = "snd_normal",
            DamagedEngineSound = "snd_damaged",
            DamagedEnginePitchRandom = true,
            DamagedEnginePitchLo = 0.4f,
            DamagedEnginePitchHi = 0.9f,
        };

        Assert.Equal(0.4f, EngineAudioCurves.DamagedPitchMul(drawn, 0f), 4);
        Assert.Equal(0.65f, EngineAudioCurves.DamagedPitchMul(drawn, 0.5f), 4);
        Assert.Equal(0.9f, EngineAudioCurves.DamagedPitchMul(drawn, 1f), 4);

        // The shipped entry, whose range runs the full 0 to 1.
        var shipped = new PlaneStats
        {
            EngineSound = "snd_normal",
            DamagedEngineSound = "snd_damagedengine",
            DamagedEnginePitchRandom = true,
            DamagedEnginePitchLo = 0f,
            DamagedEnginePitchHi = 1f,
        };
        Assert.Equal(0.75f, EngineAudioCurves.DamagedPitchMul(shipped, 0.75f), 4);

        var unflagged = new PlaneStats
        {
            EngineSound = "snd_normal",
            DamagedEngineSound = "snd_damaged",
            DamagedEnginePitchRandom = false,
            DamagedEnginePitchLo = 0.4f,
            DamagedEnginePitchHi = 0.9f,
        };
        Assert.Equal(1f, EngineAudioCurves.DamagedPitchMul(unflagged, 0f), 4);
        Assert.Equal(1f, EngineAudioCurves.DamagedPitchMul(unflagged, 1f), 4);
        Assert.Equal(("snd_damaged", 1f),
            EngineAudioCurves.EngineDefFor(unflagged, damaged: true, rng: null!));
    }

    /// <summary>An airframe with no <c>cockpit_engine_sound</c> of its own keeps the normal loop
    /// in the Cockpit view rather than going silent or erroring, the same "keep the normal def"
    /// fallback the plan calls for.</summary>
    [Fact]
    public void EngineDefForFallsBackToNormalWithNoCockpitDefinition()
    {
        var stats = new PlaneStats { EngineSound = "snd_normal" };

        Assert.Equal(("snd_normal", 1f),
            EngineAudioCurves.EngineDefFor(stats, damaged: false, rng: null!, cockpitView: true));
    }

    /// <summary>C22 (BL-459): the re-arm timer stays quiet below its 3 s floor. It fires once the
    /// total crosses the threshold drawn from <c>u</c>, and resets to zero when it does, the
    /// original's own def+0x88 accumulator.
    /// Decode: docs/formats/vehicle.md, "What makes an airframe damaged".</summary>
    [Fact]
    public void DamagedRearmWaitsOutItsThresholdThenResets()
    {
        var timer = new DamagedEngineTimer();

        // u=0 draws the floor, 3.0 s exactly. The compare reads the value from before the add.
        Assert.False(EngineAudioCurves.AdvanceDamagedRearm(timer, 1f, 0f));
        Assert.False(EngineAudioCurves.AdvanceDamagedRearm(timer, 1f, 0f));
        Assert.Equal(2f, timer.Elapsed, 4);
        Assert.False(EngineAudioCurves.AdvanceDamagedRearm(timer, 0.9f, 0f));
        Assert.False(EngineAudioCurves.AdvanceDamagedRearm(timer, 0.2f, 0f));
        Assert.Equal(3.1f, timer.Elapsed, 4);
        Assert.True(EngineAudioCurves.AdvanceDamagedRearm(timer, 0.2f, 0f));
        Assert.Equal(0f, timer.Elapsed, 4);
    }

    /// <summary>The threshold's own span is 3 to 5 s (<c>3.0 + 2*rand()/32767</c>). A draw of
    /// <c>u=1</c> holds off a full 5 s: 4.99 s and then 5.01 s accumulated still do not fire,
    /// because each frame compares the total it started with.</summary>
    [Fact]
    public void DamagedRearmThresholdReachesFiveSecondsAtTheTopOfTheDraw()
    {
        var timer = new DamagedEngineTimer();

        Assert.False(EngineAudioCurves.AdvanceDamagedRearm(timer, 4.99f, 1f));
        Assert.False(EngineAudioCurves.AdvanceDamagedRearm(timer, 0.02f, 1f));
        Assert.True(EngineAudioCurves.AdvanceDamagedRearm(timer, 0.02f, 1f));
    }

    /// <summary>With no damage the slot stays Healthy and the re-arm timer is never touched.</summary>
    [Fact]
    public void EnginePhaseStaysHealthyWithoutDamage()
    {
        var timer = new DamagedEngineTimer();

        var phase = EngineAudioCurves.StepEnginePhase(EngineSlotPhase.Healthy, false, true, timer, 1f, 0f);

        Assert.Equal(EngineSlotPhase.Healthy, phase);
        Assert.Equal(0f, timer.Elapsed, 4);
    }

    /// <summary>The damage edge puts the engine OUT, and it stays out below the 3 s floor of the
    /// re-arm threshold whatever the draw. No damaged loop starts in that window.</summary>
    [Fact]
    public void DamageEdgePutsTheEngineOutForTheTimerFloor()
    {
        var timer = new DamagedEngineTimer();
        var phase = EngineSlotPhase.Healthy;

        for (int i = 0; i < 180; i++)
        {
            var next = EngineAudioCurves.StepEnginePhase(phase, true, false, timer, 1f / 60f, 0f);
            Assert.False(EngineAudioCurves.StartsDamagedLoop(phase, next, false));
            Assert.Equal(EngineSlotPhase.Out, next);
            phase = next;
        }
        Assert.Equal(3f, timer.Elapsed, 3);
    }

    /// <summary>The engine restarts on the damaged loop once the timer passes its drawn threshold,
    /// and the start is reported exactly once. The timer resets to zero on that frame.</summary>
    [Theory]
    [InlineData(0f, 3.0f)]
    [InlineData(1f, 5.0f)]
    public void EngineRestartsOnTheDamagedLoopAtTheDrawnThreshold(float u, float thresholdS)
    {
        var timer = new DamagedEngineTimer();
        var phase = EngineSlotPhase.Healthy;
        const float dt = 0.1f;
        int startFrame = -1;

        for (int i = 0; i < 80 && startFrame < 0; i++)
        {
            var next = EngineAudioCurves.StepEnginePhase(phase, true, false, timer, dt, u);
            if (EngineAudioCurves.StartsDamagedLoop(phase, next, false))
                startFrame = i;
            phase = next;
        }

        Assert.Equal(EngineSlotPhase.Damaged, phase);
        Assert.Equal(0f, timer.Elapsed, 4);
        // The fire frame is the first whose starting total exceeds the threshold.
        Assert.InRange(startFrame * dt, thresholdS, thresholdS + (2 * dt));
    }

    /// <summary>A damaged loop that is sounding holds, running while stuttering, and the timer is
    /// left alone. Nothing restarts it and no second start is reported.</summary>
    [Fact]
    public void SoundingDamagedLoopHoldsWithoutTickingTheTimer()
    {
        var timer = new DamagedEngineTimer { Elapsed = 1.5f };

        var next = EngineAudioCurves.StepEnginePhase(EngineSlotPhase.Damaged, true, true, timer, 1f, 1f);

        Assert.Equal(EngineSlotPhase.Damaged, next);
        Assert.False(EngineAudioCurves.StartsDamagedLoop(EngineSlotPhase.Damaged, next, true));
        Assert.Equal(1.5f, timer.Elapsed, 4);
    }

    /// <summary>A damaged loop that stopped (a cull or a finished stream) re-arms through the same
    /// timer, the original's dead-handle test, and a fire reports a fresh start.</summary>
    [Fact]
    public void StoppedDamagedLoopReArmsThroughTheTimer()
    {
        var timer = new DamagedEngineTimer();

        var waiting = EngineAudioCurves.StepEnginePhase(EngineSlotPhase.Damaged, true, false, timer, 1f, 0f);
        Assert.Equal(EngineSlotPhase.Out, waiting);
        Assert.Equal(1f, timer.Elapsed, 4);

        timer.Elapsed = 3.5f;
        var fired = EngineAudioCurves.StepEnginePhase(EngineSlotPhase.Damaged, true, false, timer, 0.1f, 0f);
        Assert.Equal(EngineSlotPhase.Damaged, fired);
        Assert.True(EngineAudioCurves.StartsDamagedLoop(EngineSlotPhase.Damaged, fired, false));
    }

    /// <summary>A cleared mask restores the healthy loop at once from either damaged phase, with no
    /// re-arm wait, and the timer keeps what it had (it resets only on a fire).</summary>
    [Theory]
    [InlineData(EngineSlotPhase.Out)]
    [InlineData(EngineSlotPhase.Damaged)]
    public void ClearedMaskRestoresTheHealthyLoopAtOnce(EngineSlotPhase from)
    {
        var timer = new DamagedEngineTimer { Elapsed = 2f };

        var next = EngineAudioCurves.StepEnginePhase(from, false, false, timer, 1f, 0f);

        Assert.Equal(EngineSlotPhase.Healthy, next);
        Assert.False(EngineAudioCurves.StartsDamagedLoop(from, next, false));
        Assert.Equal(2f, timer.Elapsed, 4);
    }

    /// <summary>The timer is the airframe definition's, so a second airframe of the same type
    /// damaged after the first has waited restarts sooner: it inherits the running total.</summary>
    [Fact]
    public void SharedTimerGivesALaterDamagedAirframeAHeadStart()
    {
        var stats = new PlaneStats { EngineSound = "snd_normal", DamagedEngineSound = "snd_damaged" };
        var a = EngineSlotPhase.Healthy;
        for (int i = 0; i < 25; i++)
            a = EngineAudioCurves.StepEnginePhase(a, true, false, stats.DamagedTimer, 0.1f, 0f);
        Assert.Equal(EngineSlotPhase.Out, a);

        var b = EngineAudioCurves.StepEnginePhase(EngineSlotPhase.Healthy, true, false, stats.DamagedTimer, 0.6f, 0f);
        Assert.Equal(EngineSlotPhase.Out, b);
        var bNext = EngineAudioCurves.StepEnginePhase(b, true, false, stats.DamagedTimer, 0.1f, 0f);
        Assert.Equal(EngineSlotPhase.Damaged, bNext);
    }

    /// <summary>The re-arm timer sits on the airframe DEFINITION, not the per-spawn instance.
    /// Roster durability, the enemy difficulty scale, the per-spawn jitter and a hangar engine
    /// swap each clone one loaded <see cref="PlaneStats"/>. Every clone carries the SAME
    /// <see cref="DamagedEngineTimer"/> reference forward, never a copy.</summary>
    [Fact]
    public void EveryPerSpawnCloneSharesTheSameDamagedTimer()
    {
        var loaded = new PlaneStats { EngineSound = "snd_normal" };

        var rostered = loaded.WithRosterDurability(50f, 10f);
        var scaled = loaded.WithEnemyDurability(1.5f);
        var jittered = loaded.WithAiSpawnJitter(new System.Random(1));
        var repowered = loaded.WithEnginePower(2f);
        var chained = loaded.WithRosterDurability(50f, 10f).WithEnemyDurability(1.5f)
            .WithAiSpawnJitter(new System.Random(2));

        Assert.Same(loaded.DamagedTimer, rostered.DamagedTimer);
        Assert.Same(loaded.DamagedTimer, scaled.DamagedTimer);
        Assert.Same(loaded.DamagedTimer, jittered.DamagedTimer);
        Assert.Same(loaded.DamagedTimer, repowered.DamagedTimer);
        Assert.Same(loaded.DamagedTimer, chained.DamagedTimer);
    }

    /// <summary>A voice takes a frequency write only where its definition carries <c>FREQUENCY</c>,
    /// and the shipped entries split on that line: all eleven plain engine loops carry it, while
    /// <c>snd_damagedengine</c> and every <c>*_cp</c> cockpit loop do not. So the damaged loop sounds
    /// at its own 22050 Hz however low the drawn multiplier is, and a port that applies it anyway
    /// runs the damaged engine under the original.
    /// Decode: docs/formats/sounds.md, "A definition is pitched only when it carries FREQUENCY".</summary>
    [ExtractedDataFact]
    public void OnlyThePlainEngineLoopsAcceptAFrequencyWrite()
    {
        var defs = SoundDefs.Load(SharedZrdr);

        foreach (var node in AllPlaneNodeNames)
        {
            var stats = PlaneStats.Load(SharedZrdr, node);
            Assert.True(EngineAudioCurves.SlotIsPitched(defs, stats.EngineSound),
                $"{node}: {stats.EngineSound} lost its FREQUENCY flag");
            Assert.False(EngineAudioCurves.SlotIsPitched(defs, stats.CockpitEngineSound),
                $"{node}: {stats.CockpitEngineSound} took a FREQUENCY flag");
            Assert.False(EngineAudioCurves.SlotIsPitched(defs, stats.DamagedEngineSound),
                $"{node}: {stats.DamagedEngineSound} took a FREQUENCY flag");
        }
    }

    /// <summary>A slot whose definition refuses the write plays at exactly its own rate, whatever
    /// the throttle curve and the drawn multiplier came to. Pitch 1 is the pin: the multiplier is
    /// not clamped into range, not floored, not scaled, it never reaches the voice. The volume is
    /// untouched by any of this and stays the curve's.</summary>
    [Fact]
    public void AnUnpitchedSlotPlaysAtItsOwnRateHoweverTheCurveMoves()
    {
        var stats = new PlaneStats { EngineSound = "snd_normal" };

        foreach (var drive in new[]
                 {
                     new EngineDrive(0f, 0f, 0f), new EngineDrive(1f, 0f, 0f),
                     new EngineDrive(0.5f, 2f, -1f), new EngineDrive(1f, 0f, 0f, Boosting: true),
                 })
        {
            foreach (float mul in new[] { 0f, 0.01f, 0.4f, 1f, 9f })
            {
                var (pitch, volume) = EngineAudioCurves.Engine(stats, drive, mul, pitchable: false);
                Assert.Equal(1f, pitch);
                Assert.Equal(EngineAudioCurves.Engine(stats, drive, mul, pitchable: true).Volume, volume);
            }
        }
    }

    /// <summary>On a definition that does accept the write, the multiplier scales the curve's own
    /// output and the mixer's frequency bounds are what bound the result: 4000 Hz and 55200 Hz
    /// against a 22050 Hz asset. The clamp is the original's, applied where it applies it, on the
    /// frequency about to be handed to the voice.
    /// Decode: docs/formats/sounds.md, "A definition is pitched only when it carries
    /// FREQUENCY".</summary>
    [Fact]
    public void APitchedSlotCarriesTheMultiplierUnderTheMixersOwnBounds()
    {
        var stats = new PlaneStats { EngineSound = "snd_normal" };
        var full = new EngineDrive(1f, 0f, 0f);

        // Full throttle maps to the curve's own top, 1.0, so the multiplier stands alone.
        Assert.Equal(1f, EngineAudioCurves.Engine(stats, full, 1f, pitchable: true).Pitch, 4);
        Assert.Equal(0.5f, EngineAudioCurves.Engine(stats, full, 0.5f, pitchable: true).Pitch, 4);

        Assert.Equal(4000f / 22050f, EngineAudioCurves.Engine(stats, full, 0f, pitchable: true).Pitch, 4);
        Assert.Equal(4000f / 22050f, EngineAudioCurves.Engine(stats, full, 0.05f, pitchable: true).Pitch, 4);
        Assert.Equal(55200f / 22050f, EngineAudioCurves.Engine(stats, full, 9f, pitchable: true).Pitch, 4);
    }

    /// <summary>The flag is read off the definition the slot is actually on, and an absent one is
    /// not pitched: a name no set authors, a slot with no name at all and a caller with no defs
    /// loaded yet all answer no rather than falling through to a pitched default.</summary>
    [Fact]
    public void SlotIsPitchedReadsTheDefinitionOnTheSlot()
    {
        var defs = new Dictionary<string, SoundDef>
        {
            ["snd_normal"] = new SoundDef { Name = "snd_normal", Frequency = true },
            ["snd_damaged"] = new SoundDef { Name = "snd_damaged", Frequency = false },
        };

        Assert.True(EngineAudioCurves.SlotIsPitched(defs, "snd_normal"));
        Assert.False(EngineAudioCurves.SlotIsPitched(defs, "snd_damaged"));
        Assert.False(EngineAudioCurves.SlotIsPitched(defs, "snd_absent"));
        Assert.False(EngineAudioCurves.SlotIsPitched(defs, null));
        Assert.False(EngineAudioCurves.SlotIsPitched(null, "snd_normal"));
    }

    /// <summary>...and two SEPARATELY loaded airframes never share one. Each load is its own
    /// definition, so an unrelated plane's timer must not move when this one's does.</summary>
    [ExtractedDataFact]
    public void TwoSeparatelyLoadedAirframesDoNotShareADamagedTimer()
    {
        var a = PlaneStats.LoadForAi(SharedZrdr, "player_fury");
        var b = PlaneStats.LoadForAi(SharedZrdr, "player_fury");

        Assert.NotSame(a.DamagedTimer, b.DamagedTimer);
    }
}
