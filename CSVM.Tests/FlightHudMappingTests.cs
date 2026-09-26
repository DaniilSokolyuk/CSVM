using System.Collections.Generic;
using CSVM.Bindings;
using CSVM.Flight.Airframe;
using CSVM.Flight.Hud;
using CSVM.Flight.Weapons;
using CSVM.Mech3;
using CSVM.UI.Boards;
using Godot;
using Xunit;

namespace CSVM.Tests;

/// <summary>
/// The pilot HUD's state-struct-to-readout mapping:
/// the STALL lamp gate, the mph/ft conversions, the damage flash countdown, the AGL ray and the two
/// weapon-gauge slot fills. Every member under test here is a static or Loadout-free instance method
/// on <see cref="FlightHud"/> that returns the value a Control would be given, so none of this needs
/// a live Godot Control (the seven collaborators are all <c>Control</c>-derived and cannot be built
/// in this test host, see <c>LoadoutTests</c>'s own note on why binding a loadout stays in-engine).
/// The trap: assert numbers, not the formatted SPD/ALT/THR line, a formatting change is not a
/// regression.
/// </summary>
public class FlightHudMappingTests
{
    // ---- the STALL lamp gate ----

    [Theory]
    [InlineData(false, false, false, true, true)]   // flying and warned: lit
    [InlineData(true, false, false, true, false)]   // crashed: gated off even though warned
    [InlineData(false, true, false, true, false)]   // halted: gated off
    [InlineData(false, false, true, true, false)]   // held (pinned in the weapon lab): gated off
    [InlineData(false, false, false, false, false)] // not warned: nothing to gate
    public void TheStallLampGatesOffCrashedHaltedAndHeldEvenWhileWarned(
        bool crashed, bool halted, bool held, bool stallWarned, bool expectedLit)
    {
        var state = new FlightHudState { Crashed = crashed, Halted = halted, Held = held, StallWarned = stallWarned };
        Assert.Equal(expectedLit, FlightHud.ComputeStallWarning(in state));
    }

    // ---- mph / ft conversions ----

    [Fact]
    public void SpeedConvertsMetresPerSecondToMph()
    {
        Assert.Equal(223.694f, FlightHud.MphFromSpeedMps(100f), 3);
        Assert.Equal(0f, FlightHud.MphFromSpeedMps(0f));
    }

    [Fact]
    public void AltitudeConvertsWorldYToFeet()
    {
        Assert.Equal(328.084f, FlightHud.FeetFromWorldY(100f), 3);
    }

    // ---- the AGL ray, off a synthetic IWorldQuery, no Gauges Control involved ----

    [Fact]
    public void AglIsTheGapToWhateverTheDownRayHits()
    {
        var world = new FakeWorldQuery(hit: true, hitY: 30f);
        float agl = FlightHud.ComputeAgl(world, new Vector3(0, 100, 0), null);
        Assert.Equal(70f, agl, 3);
    }

    [Fact]
    public void AglIsMaxValueWhenTheRayReachesNothing()
    {
        var world = new FakeWorldQuery(hit: false, hitY: 0f);
        Assert.Equal(float.MaxValue, FlightHud.ComputeAgl(world, new Vector3(0, 100, 0), null));
    }

    // ---- the damage flash ----

    [Fact]
    public void AFreshFlashShowsItsTextAndCountsDownOnWallTime()
    {
        var hud = new FlightHud();
        hud.Flash("HIT: LEFT WING");
        Assert.Equal("HIT: LEFT WING", hud.AdvanceDamageFlash(wallDt: 0.1f, halted: false, crashed: false));
    }

    [Fact]
    public void ASecondHitRestartsTheWindowRatherThanQueueingBehindIt()
    {
        var hud = new FlightHud();
        hud.Flash("HIT: LEFT WING");
        // Run the first flash down to (or past) empty.
        Assert.NotNull(hud.AdvanceDamageFlash(wallDt: 10f, halted: false, crashed: false));
        Assert.Null(hud.AdvanceDamageFlash(wallDt: 10f, halted: false, crashed: false));
        hud.Flash("HIT: TAIL");
        Assert.Equal("HIT: TAIL", hud.AdvanceDamageFlash(wallDt: 0.1f, halted: false, crashed: false));
    }

    [Fact]
    public void TheFlashExpiresAfterItsWindowOfWallTime()
    {
        var hud = new FlightHud();
        hud.Flash("HIT: LEFT WING");
        Assert.NotNull(hud.AdvanceDamageFlash(wallDt: 3f, halted: false, crashed: false)); // > DamageFlashTime
        Assert.Null(hud.AdvanceDamageFlash(wallDt: 0.1f, halted: false, crashed: false));
    }

    [Fact]
    public void HaltedFreezesTheCountdownRatherThanMerelyHidingTheLine()
    {
        var hud = new FlightHud();
        hud.Flash("HIT: LEFT WING");
        // A long halted stretch must not burn the window off while nothing is drawn.
        for (int i = 0; i < 100; i++)
        {
            Assert.Null(hud.AdvanceDamageFlash(wallDt: 1f, halted: true, crashed: false));
        }
        Assert.Equal("HIT: LEFT WING", hud.AdvanceDamageFlash(wallDt: 0.1f, halted: false, crashed: false));
    }

    [Fact]
    public void CrashedAlsoFreezesTheCountdown()
    {
        var hud = new FlightHud();
        hud.Flash("HIT: LEFT WING");
        for (int i = 0; i < 100; i++)
        {
            Assert.Null(hud.AdvanceDamageFlash(wallDt: 1f, halted: false, crashed: true));
        }
        Assert.Equal("HIT: LEFT WING", hud.AdvanceDamageFlash(wallDt: 0.1f, halted: false, crashed: false));
    }

    [Fact]
    public void NoFlashPendingShowsNothing()
    {
        var hud = new FlightHud();
        Assert.Null(hud.AdvanceDamageFlash(wallDt: 0.1f, halted: false, crashed: false));
    }

    // ---- the gun gauge's slot fill ----

    [Fact]
    public void TheSelectedFirableGroupFeedsTheGunGauge()
    {
        var slots = new List<float>();
        var guns = new List<GunGroup>
        {
            Gun(mount: "Nose Guns", weaponName: "30slug", ammo: 5, capacity: 10),
            Gun(mount: "Wing Guns", weaponName: "50ap", ammo: 8, capacity: 8),
        };

        var readout = FlightHud.ComputeGunGauge(guns, gunSel: 1, slots);

        Assert.Equal(1, readout.Selected);
        Assert.Equal(8, readout.Ammo);
        Assert.Equal("50ap", readout.Type);
        Assert.Equal(new[] { 0.5f, 1f }, slots);
    }

    [Fact]
    public void ASpentGroupsBeltFractionIsZeroNotNegative()
    {
        var slots = new List<float>();
        var guns = new List<GunGroup> { Gun(mount: "Nose Guns", weaponName: "30slug", ammo: 0, capacity: 10) };

        FlightHud.ComputeGunGauge(guns, gunSel: 0, slots);

        Assert.Equal(new[] { 0f }, slots);
    }

    [Fact]
    public void NoFirableGunClampsTheSelectionAndReadsNothing()
    {
        var slots = new List<float>();
        var readout = FlightHud.ComputeGunGauge(new List<GunGroup>(), gunSel: 3, slots);

        Assert.Equal(0, readout.FirableCount);
        Assert.Equal(0, readout.Selected);
        Assert.Equal(0, readout.Ammo);
        Assert.Equal("", readout.Type);
        Assert.Empty(slots);
    }

    [Fact]
    public void AGunSelectionPastTheEndClampsTheArrowButFindsNoGroupToReadAmmoFrom()
    {
        // The selected group is matched by exact index in the same pass that builds the belt
        // fractions: an out-of-range GunSelect clamps the ARROW but the ammo/type match finds none.
        var slots = new List<float>();
        var guns = new List<GunGroup> { Gun(mount: "Nose Guns", weaponName: "30slug", ammo: 4, capacity: 10) };

        var readout = FlightHud.ComputeGunGauge(guns, gunSel: 9, slots);

        Assert.Equal(0, readout.Selected);
        Assert.Equal(0, readout.Ammo);
    }

    // ---- the missile gauge's slot fill ----

    [Fact]
    public void TheSelectedPylonsOwnRoundsFeedTheMissileGauge()
    {
        var slots = new List<float>();
        var hardpoints = new List<Hardpoint>
        {
            Hp(index: 1, ammo: 2, capacity: 4, weaponName: "BOOM", displayName: "MSG_WEAP_BOOM"),
            Hp(index: 5, ammo: 3, capacity: 3, weaponName: "FLAK", displayName: "High-explosive rocket"),
        };

        var readout = FlightHud.ComputeMissileGauge(hardpoints, pylonSelect: 1, slots);

        Assert.True(readout.HasHardpoints);
        Assert.Equal(4, readout.Selected); // pylon 5 -> belt index 4 (0-based)
        Assert.Equal(3, readout.Ammo);
        Assert.Equal("FLAK", readout.Type); // the dial face's short handle, never the display name
    }

    [Fact]
    public void BeltSlotsAreIndexedByPylonNumberNotListPosition()
    {
        var slots = new List<float>();
        // A partial stock fit: pylons 3 and 7 only, skipping 1/2/4/5/6/8, the belt ring still has
        // 8 positions (GaugeCluster.HardpointRingSize, internal), with gaps at every unfitted slot.
        var hardpoints = new List<Hardpoint>
        {
            Hp(index: 3, ammo: 2, capacity: 4, weaponName: "BOOM", displayName: "BOOM"),
            Hp(index: 7, ammo: 6, capacity: 6, weaponName: "BOOM", displayName: "BOOM"),
        };

        FlightHud.ComputeMissileGauge(hardpoints, pylonSelect: 0, slots);

        Assert.Equal(8, slots.Count);
        Assert.Equal(0.5f, slots[2]);  // pylon 3 -> index 2
        Assert.Equal(1f, slots[6]);    // pylon 7 -> index 6
        Assert.Equal(0f, slots[0]);    // an unfitted position stays at 0 here (FlightHud reads it as
                                       // empty; GaugeCluster.SlotIndicatorColor is what paints RED
                                       // for a position past the loadout's own Count)
    }

    [Fact]
    public void NoHardpointsReportsNoneRatherThanAnEmptySelection()
    {
        var slots = new List<float>();
        var readout = FlightHud.ComputeMissileGauge(new List<Hardpoint>(), pylonSelect: 0, slots);

        Assert.False(readout.HasHardpoints);
    }

    // ---- the text block's ordered line list ----

    [Fact]
    public void StalledAppendsTheWarningLineUnlessHeld()
    {
        var hud = new FlightHud();
        var stalled = new FlightHudState { Stalled = true };
        Assert.Contains("⚠ STALLED - SPEED UP", hud.ComposeTextLines(in stalled, mph: 0f, ft: 0f, wide: false));

        var pinned = new FlightHudState { Stalled = true, Held = true };
        Assert.DoesNotContain("⚠ STALLED - SPEED UP", hud.ComposeTextLines(in pinned, mph: 0f, ft: 0f, wide: false));
    }

    [Fact]
    public void AutoLandOfferedStandsUnlessHeldCrashedOrHalted()
    {
        var offered = new FlightHudState { AutoLandOffered = true };
        Assert.True(FlightHud.ShowsAutoLandPrompt(in offered));

        var held = new FlightHudState { AutoLandOffered = true, Held = true };
        var crashed = new FlightHudState { AutoLandOffered = true, Crashed = true };
        var halted = new FlightHudState { AutoLandOffered = true, Halted = true };
        foreach (var state in new[] { held, crashed, halted, new FlightHudState() })
        {
            Assert.False(FlightHud.ShowsAutoLandPrompt(in state));
        }
    }

    [Fact]
    public void TheTextBlockNeverCarriesTheAutoLandPrompt()
    {
        var hud = new FlightHud { AutoLandPrompt = ControlLine.Plain("Press F9 to autodock") };
        var offered = new FlightHudState { AutoLandOffered = true };
        // The return is the reused instance list, so the offered lines are copied before the
        // second compose overwrites them.
        var lines = new List<string>(hud.ComposeTextLines(in offered, mph: 0f, ft: 0f, wide: false));
        Assert.DoesNotContain("Press F9 to autodock", lines);

        var quiet = new FlightHudState();
        Assert.Equal(hud.ComposeTextLines(in quiet, mph: 0f, ft: 0f, wide: false).Count, lines.Count);
    }

    // ---- the auto-land prompt's wording, off the message table and the seat's own bindings ----

    [Fact]
    public void TheAutoLandPromptTakesItsWordingFromTheMessageTable()
    {
        var strings = AutoLandMessages();
        var keyboardAndPad = new[] { KeyBinding(Key.F9), PadBinding(JoyButton.LeftStick) };
        Assert.Equal("Press F9 to autodock",
            FlightHud.ComposeAutoLandPrompt(strings, keyboardAndPad, true, DeviceSide.Keyboard).Text);
    }

    [Fact]
    public void APadOnlySeatIsNamedItsPadControlRatherThanAKeyItCannotPress()
    {
        var strings = AutoLandMessages();
        var keyboardAndPad = new[] { KeyBinding(Key.F9), PadBinding(JoyButton.LeftStick) };
        Assert.Equal("Press Pad Left Stick to autodock",
            FlightHud.ComposeAutoLandPrompt(strings, keyboardAndPad, false, DeviceSide.Pad).Text);
    }

    [Fact]
    public void ASeatFlyingOnThePadIsNamedThePadControlThoughItReadsTheKeyboardToo()
    {
        var strings = AutoLandMessages();
        var keyboardAndPad = new[] { KeyBinding(Key.F9), PadBinding(JoyButton.LeftStick) };
        Assert.Equal("Press Pad Left Stick to autodock",
            FlightHud.ComposeAutoLandPrompt(strings, keyboardAndPad, true, DeviceSide.Pad).Text);
    }

    [Fact]
    public void AMouseButtonTakesTheClickWordingRatherThanThePressOne()
    {
        var strings = AutoLandMessages();
        var mouseOnly = new[] { MouseBinding(MouseButton.Middle) };
        Assert.Equal("Click Mouse Middle to autodock",
            FlightHud.ComposeAutoLandPrompt(strings, mouseOnly, true, DeviceSide.Keyboard).Text);
    }

    [Fact]
    public void AnUnboundActionComposesNoPromptAndAMissingTableStillNamesTheControl()
    {
        Assert.Equal("", FlightHud.ComposeAutoLandPrompt(
            AutoLandMessages(), System.Array.Empty<Binding>(), true, DeviceSide.Keyboard).Text);
        Assert.Equal("AUTO-LAND AVAILABLE - F9",
            FlightHud.ComposeAutoLandPrompt(null, new[] { KeyBinding(Key.F9) }, true, DeviceSide.Keyboard).Text);
    }

    // ---- the slot the control fills: a glyph for a pad control, the words for a key or a mouse ----

    [Fact]
    public void APadControlFillsTheSlotWithItsOwnGlyphAndTheHalvesAreTheTemplatesOwn()
    {
        var keyboardAndPad = new[] { KeyBinding(Key.F9), PadBinding(JoyButton.LeftStick) };
        var line = FlightHud.ComposeAutoLandPrompt(AutoLandMessages(), keyboardAndPad, true, DeviceSide.Pad);
        Assert.Equal(new GlyphKey(ControlKind.Button, (int)JoyButton.LeftStick, 0), line.Glyph);
        Assert.Equal("Press ", line.Prefix);
        Assert.Equal("Pad Left Stick", line.Words);
        Assert.Equal(" to autodock", line.Suffix);
    }

    [Theory]
    [InlineData(true, DeviceSide.Keyboard)]
    [InlineData(true, DeviceSide.Pad)]
    public void AKeyboardSeatKeepsItsWordsAndTakesNoGlyph(bool readsKeyboard, DeviceSide side)
    {
        var keyAndMouse = new[] { KeyBinding(Key.F9), MouseBinding(MouseButton.Middle) };
        var line = FlightHud.ComposeAutoLandPrompt(AutoLandMessages(), keyAndMouse, readsKeyboard, side);
        Assert.Null(line.Glyph);
        Assert.Equal("F9", line.Words);
    }

    [Fact]
    public void ADamageSummaryAppendsItsOwnDmgLine()
    {
        var hud = new FlightHud();
        var state = new FlightHudState { DamageSummary = "nose 40%" };
        Assert.Contains("DMG nose 40%", hud.ComposeTextLines(in state, mph: 0f, ft: 0f, wide: false));
    }

    [Fact]
    public void AnEmptyDamageSummaryAddsNoLine()
    {
        var hud = new FlightHud();
        var state = new FlightHudState { DamageSummary = "" };
        var lines = hud.ComposeTextLines(in state, mph: 0f, ft: 0f, wide: false);
        Assert.DoesNotContain(lines, l => l.StartsWith("DMG", System.StringComparison.Ordinal));
    }

    [Fact]
    public void TheStuntStatusLineArrivesPreResolvedAndIsAppendedVerbatim()
    {
        var hud = new FlightHud();
        var state = new FlightHudState { StuntStatusLine = "LAP 2/3  1:04.220" };
        Assert.Contains("LAP 2/3  1:04.220", hud.ComposeTextLines(in state, mph: 0f, ft: 0f, wide: false));
    }

    [Fact]
    public void TheHaltIsTheOnlyTrailingLineAndACrashPutsNoneInTheBlock()
    {
        var hud = new FlightHud();
        var halted = new FlightHudState { Halted = true, Crashed = true };
        Assert.Contains("⏸ PAUSED (. steps one frame)",
            hud.ComposeTextLines(in halted, mph: 0f, ft: 0f, wide: false));

        // The respawn prompt is its own centred line, so the crash adds nothing to the block.
        var crashed = new FlightHudState { Crashed = true, RespawnOffered = true };
        Assert.Equal(1, hud.ComposeTextLines(in crashed, mph: 0f, ft: 0f, wide: false).Count);
    }

    // ---- the respawn prompt, over the shipped keymap's own Respawn bindings ----

    [Theory]
    [InlineData(true, DeviceSide.Keyboard, "Press Backspace to respawn")]
    [InlineData(true, DeviceSide.Pad, "Press Pad Y to respawn")]
    [InlineData(false, DeviceSide.Pad, "Press Pad Y to respawn")]
    public void TheRespawnPromptNamesTheShippedControlOfTheSeatsActiveSide(
        bool readsKeyboard, DeviceSide side, string expected)
    {
        Assert.Equal(expected, FlightHud.ComposeRespawnPrompt(RespawnDefaults(), readsKeyboard, side).Text);
    }

    [Fact]
    public void APadOnlySeatIsNeverNamedAKeyAndAnUnboundRespawnComposesNoPrompt()
    {
        var keyOnly = new[] { KeyBinding(Key.Backspace) };
        Assert.Equal("", FlightHud.ComposeRespawnPrompt(keyOnly, readsKeyboard: false, DeviceSide.Pad).Text);
        Assert.Equal("", FlightHud.ComposeRespawnPrompt(
            System.Array.Empty<Binding>(), readsKeyboard: true, DeviceSide.Keyboard).Text);
    }

    [Fact]
    public void ARespawnMovedOffBackspaceIsNamedWhereItNowSits()
    {
        var rebound = new[] { KeyBinding(Key.Delete), MouseBinding(MouseButton.Middle) };
        Assert.Equal("Press Delete to respawn",
            FlightHud.ComposeRespawnPrompt(rebound, readsKeyboard: true, DeviceSide.Keyboard).Text);
        // A mouse button takes the click wording, the same split the auto-dock line makes.
        Assert.Equal("Click Mouse Middle to respawn", FlightHud.ComposeRespawnPrompt(
            new[] { MouseBinding(MouseButton.Middle) }, readsKeyboard: true, DeviceSide.Keyboard).Text);
    }

    [Theory]
    [InlineData(true, false, true, true)]    // crashed, the button answers: shown
    [InlineData(false, false, true, false)]  // flying: nothing to answer
    [InlineData(true, true, true, false)]    // a board is up over the wreck
    [InlineData(true, false, false, false)]  // out of lives, or the seat's controls are held
    public void TheRespawnPromptStandsOnlyWhereTheButtonWouldAnswer(
        bool crashed, bool halted, bool offered, bool expected)
    {
        var state = new FlightHudState { Crashed = crashed, Halted = halted, RespawnOffered = offered };
        Assert.Equal(expected, FlightHud.ShowsRespawnPrompt(in state));
    }

    [Fact]
    public void AWidePaneSplitsSpeedAltitudeAndThrottleOntoTwoLinesInstead()
    {
        var hud = new FlightHud();
        var state = new FlightHudState();
        Assert.Equal(1, hud.ComposeTextLines(in state, mph: 0f, ft: 0f, wide: false).Count);
        Assert.Equal(2, hud.ComposeTextLines(in state, mph: 0f, ft: 0f, wide: true).Count);
    }

    [Fact]
    public void ALimitedLivesDeathPicksItsLineByTheLivesLeft()
    {
        var strings = Messages.Parse(
            "{\"language_id\":1033,\"entries\":["
            + "{\"key\":\"MSG_NO_LIVES\",\"id\":209,\"value\":\"You Are Out of Lives!\"},"
            + "{\"key\":\"MSG_NUM_LIVES\",\"id\":210,\"value\":\"You Have %1!d! Lives Left!\"},"
            + "{\"key\":\"MSG_ONE_LIFE\",\"id\":211,\"value\":\"You Have ONE Life Left!\"}]}");

        Assert.Equal("You Have 4 Lives Left!", HudMessages.LivesLine(strings, 4));
        Assert.Equal("You Have ONE Life Left!", HudMessages.LivesLine(strings, 1));
        Assert.Equal("You Are Out of Lives!", HudMessages.LivesLine(strings, 0));

        // ABLE-TO-FAIL CONTROL: two left is still the counted line, not the one-life line.
        Assert.Equal("You Have 2 Lives Left!", HudMessages.LivesLine(strings, 2));
    }

    // The two rows the prompt reads, as messages.json carries them (ids 181 and 182).
    private static Messages AutoLandMessages() => Messages.Parse(
        "{\"language_id\":1033,\"entries\":["
        + "{\"key\":\"MSG_PRESS_AUTOLAND\",\"id\":181,\"value\":\"Press %1 to autodock\"},"
        + "{\"key\":\"MSG_CLICK_AUTOLAND\",\"id\":182,\"value\":\"Click %1 to autodock\"}]}");

    // The shipped flight keymap's own Respawn row, so the prompt is asserted against what the seat
    // actually flies rather than against a hand-built pair.
    private static IReadOnlyList<Binding> RespawnDefaults() =>
        DefaultBindings.MapFor(InputContext.Flight, DeviceId.Joypad("test-pad"))
            .Bindings(InputAction.Respawn);

    private static Binding KeyBinding(Key key) =>
        new(DeviceId.Keyboard, BindingControl.Key((int)key));

    private static Binding PadBinding(JoyButton button) =>
        new(DeviceId.Joypad("test-pad"), BindingControl.Button((int)button));

    private static Binding MouseBinding(MouseButton button) =>
        new(DeviceId.Mouse, BindingControl.Mouse((int)button));

    private static GunGroup Gun(string mount, string weaponName, int ammo, int capacity) => new()
    {
        Mount = mount,
        Weapon = new WeaponDef { Name = weaponName },
        Ammo = ammo,
        Capacity = capacity,
    };

    private static Hardpoint Hp(int index, int ammo, int capacity, string weaponName, string displayName) => new()
    {
        Index = index,
        Weapon = new WeaponDef { Name = weaponName, DisplayName = displayName },
        Ammo = ammo,
        Capacity = capacity,
    };

    // A synthetic IWorldQuery reporting a fixed hit/miss and Y, mirroring TurretLineOfSightTests'
    // own fake, no physics world in the process.
    private sealed class FakeWorldQuery : IWorldQuery
    {
        private readonly bool _hit;
        private readonly float _hitY;

        public FakeWorldQuery(bool hit, float hitY)
        {
            _hit = hit;
            _hitY = hitY;
        }

        public bool Sweep(IReadOnlyList<PlaneCollider.Part> parts, Transform3D baseTransform,
            Vector3 motion, uint mask, Godot.Collections.Array<Rid>? exclude, out SweepReport report)
        {
            report = default;
            return false;
        }

        public bool Ray(Vector3 from, Vector3 to, uint mask, Godot.Collections.Array<Rid>? exclude,
            out RayReport report)
        {
            report = _hit ? new RayReport(new Vector3(from.X, _hitY, from.Z), Vector3.Up, null) : default;
            return _hit;
        }

        public bool Overlaps(IReadOnlyList<PlaneCollider.Part> parts, Transform3D pose, uint mask,
            Godot.Collections.Array<Rid>? exclude) => false;
    }
}
