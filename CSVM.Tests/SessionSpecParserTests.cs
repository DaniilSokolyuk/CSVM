using System.Globalization;
using System.Threading;
using CSVM;
using CSVM.Mech3;
using Godot;
using Xunit;

namespace CSVM.Tests;

/// <summary>
/// The argument value grammars. All seven were <c>private</c> inside <c>GameSession</c> and so had
/// no coverage at all; they are the parts of the launch surface most likely to be wrong in a way
/// nothing downstream notices, because a mis-parsed vector or hold sequence still produces a
/// perfectly valid session.
/// </summary>
[Trait("Tier", "Quick")]
public class SessionSpecParserTests
{
    [Fact]
    public void AVectorIsThreeInvariantCultureFloats()
    {
        Assert.Equal(new Vector3(1.5f, -2f, 30.25f), SessionSpec.ParseVec3("1.5,-2,30.25"));
    }

    /// <summary>A German machine writes `0,15` for nought point one five; the parse is pinned to the
    /// invariant culture so a command line means the same thing on every machine, and so a captured
    /// baseline does not differ from itself across locales.</summary>
    [Fact]
    public void AVectorParsesTheSameUnderAnyCulture()
    {
        var before = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
            Assert.Equal(new Vector3(1.5f, 2.5f, 3.5f), SessionSpec.ParseVec3("1.5,2.5,3.5"));
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = before;
        }
    }

    [Fact]
    public void APlaneListIsCommaSeparatedAndTrimmed()
    {
        Assert.Equal(new[] { "player_bhawk" }, SessionSpec.ParsePlanes("player_bhawk"));
        Assert.Equal(new[] { "player_bhawk", "player_fury" },
            SessionSpec.ParsePlanes(" player_bhawk , player_fury "));
        // Empty entries are dropped rather than becoming a nameless player.
        Assert.Equal(new[] { "player_bhawk" }, SessionSpec.ParsePlanes("player_bhawk,,"));
        Assert.Empty(SessionSpec.ParsePlanes(""));
    }

    /// <summary>5 has no perspective of its own (the middle of the pad is the chase camera), and
    /// anything outside 1–9 is a typo, both give 0, which the caller reports.</summary>
    [Theory]
    [InlineData("1", 1)]
    [InlineData("4", 4)]
    [InlineData("9", 9)]
    [InlineData("5", 0)]
    [InlineData("0", 0)]
    [InlineData("10", 0)]
    [InlineData("", 0)]
    [InlineData("two", 0)]
    public void TheNumpadViewDigitRejectsWhatHasNoPerspective(string arg, int expected)
        => Assert.Equal(expected, SessionSpec.ParseView(arg));

    [Fact]
    public void AHoldIsOneScriptedSequencePerPlayer()
    {
        var sets = SessionSpec.ParseHold("0,0,0,1@2;0.5,0,0,1|0,1,0,0.5");
        Assert.Equal(2, sets.Length);

        // Player one: two segments, the first held for two seconds, the last held forever.
        Assert.Equal(2, sets[0].Length);
        Assert.Equal(2f, sets[0][0].Item2);
        Assert.Equal(1f, sets[0][0].Item1.Throttle);
        Assert.Equal(0f, sets[0][1].Item2);
        Assert.Equal(0.5f, sets[0][1].Item1.Pitch);

        // Player two: the plain constant-input form, which is one segment with no duration.
        Assert.Single(sets[1]);
        Assert.Equal(1f, sets[1][0].Item1.Roll);
        Assert.Equal(0.5f, sets[1][0].Item1.Throttle);
    }

    [Fact]
    public void AHoldReadsPitchRollYawThrottleInThatOrder()
    {
        var input = SessionSpec.ParseHold("0.1,0.2,0.3,0.4")[0][0].Item1;
        Assert.Equal(0.1f, input.Pitch);
        Assert.Equal(0.2f, input.Roll);
        Assert.Equal(0.3f, input.Yaw);
        Assert.Equal(0.4f, input.Throttle);
    }

    /// <summary>The lever schedule reads eighths, not fractions, and sorts its steps by time so a
    /// spec written out of order still presses in order.</summary>
    [Fact]
    public void ALeverScheduleReadsEighthsInTimeOrder()
    {
        var steps = SessionSpec.ParseLever("8@10;0@0;4@12.5");
        Assert.Equal(3, steps.Length);
        Assert.Equal((0f, 0f), steps[0]);
        Assert.Equal((10f, 1f), steps[1]);
        Assert.Equal((12.5f, 0.5f), steps[2]);

        // An eighth outside the digit row's own range clamps rather than opening the lever past full.
        Assert.Equal(1f, SessionSpec.ParseLever("12@1")[0].Lever);
        Assert.Equal(0f, SessionSpec.ParseLever("-3@1")[0].Lever);

        // No '@' is a press at the spawn, the "start at this eighth" form.
        Assert.Equal((0f, 0.25f), Assert.Single(SessionSpec.ParseLever("2")));
    }

    /// <summary>Three colour slots, body, dark trim, light trim, and a missing one repeats the
    /// last given, so a single triple paints the whole aircraft.</summary>
    [Fact]
    public void APaintColourRepeatsTheLastSlotGiven()
    {
        var one = SessionSpec.ParsePaintColors("255,0,0");
        Assert.Equal(3, one.Length);
        Assert.Equal(one[0], one[1]);
        Assert.Equal(one[0], one[2]);
        Assert.Equal(PaintScheme.FromBytes(255, 0, 0), one[0]);

        var two = SessionSpec.ParsePaintColors("255,0,0/0,255,0");
        Assert.Equal(PaintScheme.FromBytes(255, 0, 0), two[0]);
        Assert.Equal(PaintScheme.FromBytes(0, 255, 0), two[1]);
        Assert.Equal(two[1], two[2]);
    }

    [Fact]
    public void APaintDecalRepeatsTheLastSlotGiven()
    {
        Assert.Equal(new[] { 4, 4, 4 }, SessionSpec.ParsePaintDecals("4"));
        Assert.Equal(new[] { 1, 2, 2 }, SessionSpec.ParsePaintDecals("1,2"));
        Assert.Equal(new[] { 1, 2, 3 }, SessionSpec.ParsePaintDecals("1,2,3"));
    }

    /// <summary>A damage preset is part:fraction; a value above 1 reads as a percent, and everything
    /// is clamped into 0–1 so a typo cannot ask for negative or 300% damage.</summary>
    [Fact]
    public void ADamagePresetTakesFractionsOrPercents()
    {
        var preset = SessionSpec.ParseDamagePreset("nose:0.25, leftwing:40, tail:300, rudder:-2");
        Assert.Equal(4, preset.Count);
        Assert.Equal(("nose", 0.25f), preset[0]);
        Assert.Equal(("leftwing", 0.4f), preset[1]);
        Assert.Equal(("tail", 1f), preset[2]);
        Assert.Equal(("rudder", 0f), preset[3]);
    }

    /// <summary>A malformed pair is skipped and handed back as data when the caller asks, which is
    /// how the spec reports it without a Godot runtime to print into.</summary>
    [Fact]
    public void AMalformedDamagePairIsSkippedAndReported()
    {
        var rejected = new System.Collections.Generic.List<string>();
        var preset = SessionSpec.ParseDamagePreset("nose:0.25,garbage,tail:notanumber", rejected);

        Assert.Single(preset);
        Assert.Equal(new[] { "garbage", "tail:notanumber" }, rejected);
    }

    /// <summary>The same value reaching the spec through its flag, so the wiring is covered and not
    /// just the grammar.</summary>
    [Fact]
    public void TheFlagsReachTheseParsers()
    {
        var s = SessionSpec.Parse(new[]
        {
            "--damage=nose:50", "--paint-decal=1,2", "--hold=0,0,0,1", "--pos=1,2,3",
            "--lever=0@0;8@10",
        });
        Assert.Equal(("nose", 0.5f), Assert.Single(s.DamagePreset!));
        Assert.Equal(new[] { 1, 2, 2 }, s.PaintDecalOverride);
        Assert.Single(s.HoldSets!);
        Assert.Equal(2, s.LeverSteps!.Count);
        Assert.Equal(new Vector3(1, 2, 3), s.Pos);
    }

    /// <summary>The B5/E16 grammar: `--ai=plane[:net][:accent=id][:def=name],…` splits into
    /// entries, a bare name has a null net, accent and def, an empty net after the colon reads as
    /// none, and the keyed segments bind wherever they appear.</summary>
    [Fact]
    public void AiEntriesCarryTheirOptionalNetReference()
    {
        var s = SessionSpec.Parse(new[] { "--ai=player_fury:M4ReinfAce,player_bhawk,ebrigand:10,edevast:" });
        Assert.Equal(4, s.AiPlanes!.Count);
        Assert.Equal(new AiPlaneEntry("player_fury", "M4ReinfAce"), s.AiPlanes[0]);
        Assert.Equal(new AiPlaneEntry("player_bhawk"), s.AiPlanes[1]);
        Assert.Equal(new AiPlaneEntry("ebrigand", "10"), s.AiPlanes[2]);
        Assert.Equal(new AiPlaneEntry("edevast"), s.AiPlanes[3]);

        var a = SessionSpec.Parse(new[] { "--ai=player_fury:M4ReinfAce:accent=12,ebrigand:accent=14,edevast:accent=12:7" });
        Assert.Equal(new AiPlaneEntry("player_fury", "M4ReinfAce", 12), a.AiPlanes![0]);
        Assert.Equal(new AiPlaneEntry("ebrigand", null, 14), a.AiPlanes[1]);
        Assert.Equal(new AiPlaneEntry("edevast", "7", 12), a.AiPlanes[2]);

        // The militia variant an AI flies: its own weapons, paint and skills come off that def.
        var d = SessionSpec.Parse(new[] { "--ai=player_warhawk:def=bhatwarhawk:accent=12,player_fury:def=secfury" });
        Assert.Equal(new AiPlaneEntry("player_warhawk", null, 12, "bhatwarhawk"), d.AiPlanes![0]);
        Assert.Equal(new AiPlaneEntry("player_fury", null, null, "secfury"), d.AiPlanes[1]);
    }

    /// <summary>BL-742's squadron tokens: `n=` counts an entry's planes, `team=` puts two entries
    /// on one side (without it every CLI plane takes its own banded id and fights the rest), and
    /// `pos=` is slash-separated because the entry list has already spent the comma. An absent
    /// token leaves the count at one and the team and position unset.</summary>
    [Fact]
    public void AiEntriesCarryTheirSquadronCountTeamAndPosition()
    {
        var s = SessionSpec.Parse(new[]
        {
            "--ai=player_warhawk:n=4:team=2:def=bhatwarhawk,player_fury:n=4:team=3",
        });
        Assert.Equal(new AiPlaneEntry("player_warhawk", null, null, "bhatwarhawk", 2, 4), s.AiPlanes![0]);
        Assert.Equal(new AiPlaneEntry("player_fury", Team: 3, Count: 4), s.AiPlanes[1]);

        var p = SessionSpec.Parse(new[] { "--ai=player_fury:pos=10/300/-20,player_bhawk" });
        Assert.Equal(new Vector3(10f, 300f, -20f), p.AiPlanes![0].Pos);
        Assert.Null(p.AiPlanes[1].Pos);
        Assert.Equal(1, p.AiPlanes[1].Count);
        Assert.Null(p.AiPlanes[1].Team);

        // A count below one would spawn nothing from an entry that names a plane.
        Assert.Equal(1, SessionSpec.Parse(new[] { "--ai=player_fury:n=0" }).AiPlanes![0].Count);
    }

    /// <summary>BL-742's zeppelin graft: `--zep=chapter/mission:record` takes the chapter and
    /// mission with it, since the record's gamez, textures, nets and zeppelins.zrd are all read
    /// off those. It is the empty stage's flag alone, and a value that is not
    /// chapter/mission:record is refused with a note rather than half-applied.</summary>
    [Fact]
    public void ZepGraftTakesItsChapterAndMissionWithIt()
    {
        var s = SessionSpec.Parse(new[] { "--stage=empty", "--fly", "--zep=C4/M03:piratezep:team=1" });
        Assert.NotNull(s.Zep);
        Assert.Equal(new ZepStageSpec("C4", "M03", "piratezep", 1), s.Zep!.Value);
        Assert.Equal("C4", s.Chapter);
        Assert.Equal("M03", s.Mission);

        var p = SessionSpec.Parse(new[]
        {
            "--stage=empty", "--fly", "--zep=C2/M01:hk_zep:pos=100/700/-50",
        });
        Assert.Equal(new Vector3(100f, 700f, -50f), p.Zep!.Value.Pos);
        Assert.Null(p.Zep!.Value.Team);
        Assert.Null(p.Zep!.Value.Net);

        // net= names the stage's built-in ring, the one patrol net a chapter-less session has, so
        // a grafted airship can fly a route instead of station-keeping on its seat.
        var n = SessionSpec.Parse(new[]
        {
            "--stage=empty", "--fly", $"--zep=C2/M01:hk_zep:net={EmptyStage.PatrolNetName}",
        });
        Assert.Equal(EmptyStage.PatrolNetName, n.Zep!.Value.Net);
        Assert.NotNull(EmptyStage.ResolveNet(n.Zep!.Value.Net!));

        // A chapter world places its own airships; grafting one on again would be two hulls under
        // one node name, so the flag is dropped rather than obeyed.
        var w = SessionSpec.Parse(new[] { "--fly", "--chapter=C4", "--zep=C4/M03:piratezep" });
        Assert.Null(w.Zep);

        foreach (string bad in new[] { "--zep=C4:piratezep", "--zep=C4/M03", "--zep=C4/M03:" })
        {
            Assert.Null(SessionSpec.Parse(new[] { "--stage=empty", "--fly", bad }).Zep);
        }
    }

    /// <summary>The D14 gunnery arm: bare `--ai-attack` reads skill 5, a value clamps to the
    /// 1–9 rating scale, and an absent flag stays null (no gunner armed).</summary>
    [Fact]
    public void AiAttackCarriesAClampedSkillRating()
    {
        Assert.Null(SessionSpec.Parse(new[] { "--fly" }).AiAttackSkill);
        Assert.Equal(5, SessionSpec.Parse(new[] { "--ai-attack" }).AiAttackSkill);
        Assert.Equal(9, SessionSpec.Parse(new[] { "--ai-attack=9" }).AiAttackSkill);
        Assert.Equal(1, SessionSpec.Parse(new[] { "--ai-attack=0" }).AiAttackSkill);
        Assert.Equal(9, SessionSpec.Parse(new[] { "--ai-attack=42" }).AiAttackSkill);
    }

    /// <summary>`--debug-preset=` defaults to −1, not 0: preset 0 ("Girl Trouble") is a real
    /// request, unlike a 0 wave or wingman count, so the launcher's "was this asked for" test
    /// cannot be a truthiness check on the value.</summary>
    [Fact]
    public void DebugPresetIsAbsentAtMinusOneSoPresetZeroCanBeAskedFor()
    {
        Assert.Equal(-1, SessionSpec.Parse(new[] { "--menu" }).DebugPreset);
        Assert.Equal(0, SessionSpec.Parse(new[] { "--debug-preset=0" }).DebugPreset);
        Assert.Equal(18, SessionSpec.Parse(new[] { "--debug-preset=18" }).DebugPreset);
    }

    [Fact]
    public void AMalformedDamagePresetIsReportedThroughTheSpecsWarnings()
    {
        var s = SessionSpec.Parse(new[] { "--damage=garbage" });
        Assert.Contains(s.Warnings, w => w.Message.Contains("cannot parse 'garbage'"));
        // An empty category means a bare console line rather than a log category.
        Assert.Contains(s.Warnings, w => w.Category.Length == 0);
    }

    /// <summary><c>--hitch-inject=</c>: `[alloc:]&lt;ms&gt;[@frame]`, a bare `&lt;ms&gt;` is the
    /// busy-wait form at the default frame, `@frame` overrides it, and `alloc:` switches to the
    /// allocation-burst form without disturbing either the magnitude or the frame.</summary>
    [Fact]
    public void HitchInjectParsesMagnitudeFormAndFrame()
    {
        Assert.Equal((50f, false, SessionSpec.Parse(new[] { "--hitch-inject=50" }).HitchInjectFrame),
            SessionSpec.ParseHitchInject("50"));
        Assert.Equal((50f, false, 120), SessionSpec.ParseHitchInject("50@120"));
        Assert.Equal((50f, true, 120), SessionSpec.ParseHitchInject("alloc:50@120"));
        Assert.Equal((5f, true, SessionSpec.Parse(new[] { "--hitch-inject=alloc:5" }).HitchInjectFrame),
            SessionSpec.ParseHitchInject("alloc:5"));
    }

    /// <summary>The same grammar reaching the spec through its flag: absent by default (so B4's
    /// detector runs unperturbed), and each field lands where the injector call site reads it.</summary>
    [Fact]
    public void TheHitchInjectFlagReachesTheSpec()
    {
        Assert.Null(SessionSpec.Parse(new[] { "--fly" }).HitchInjectMs);

        var s = SessionSpec.Parse(new[] { "--hitch-inject=50@120" });
        Assert.Equal(50f, s.HitchInjectMs);
        Assert.False(s.HitchInjectAlloc);
        Assert.Equal(120, s.HitchInjectFrame);

        var a = SessionSpec.Parse(new[] { "--hitch-inject=alloc:5@30" });
        Assert.Equal(5f, a.HitchInjectMs);
        Assert.True(a.HitchInjectAlloc);
        Assert.Equal(30, a.HitchInjectFrame);
    }

    /// <summary>The two menu-presentation flags: absent by default, the override carries whatever
    /// name follows the `=`, and force-Built-in is a plain boolean independent of it.</summary>
    [Fact]
    public void PresentationOverrideAndForceBuiltInReachTheSpec()
    {
        var bare = SessionSpec.Parse(new[] { "--fly" });
        Assert.Null(bare.PresentationOverride);
        Assert.False(bare.ForceBuiltInPresentation);

        var s = SessionSpec.Parse(new[] { "--presentation=original", "--force-builtin" });
        Assert.Equal("original", s.PresentationOverride);
        Assert.True(s.ForceBuiltInPresentation);
    }

    /// <summary>A join value is an address with an optional port. An IPv6 address is bracketed,
    /// which tells its own colons from the port's. A value naming no port falls back to the
    /// door's default.</summary>
    [Theory]
    [InlineData("127.0.0.1", "127.0.0.1", 47500)]
    [InlineData("127.0.0.1:47600", "127.0.0.1", 47600)]
    [InlineData("host.example:80", "host.example", 80)]
    [InlineData("[::1]", "::1", 47500)]
    [InlineData("[fe80::1%eth0]:47600", "fe80::1%eth0", 47600)]
    [InlineData("::1", "::1", 47500)]
    [InlineData("127.0.0.1:0", "127.0.0.1", 47500)]
    [InlineData("127.0.0.1:notaport", "127.0.0.1", 47500)]
    public void AJoinValueSplitsIntoAnAddressAndAPort(string value, string address, int port)
    {
        var parsed = SessionSpec.ParseJoin(value);
        Assert.Equal(address, parsed.Address);
        Assert.Equal(port, parsed.Port);
    }

    /// <summary>A host value is a bare port on every interface, or an address and a port binding
    /// that one. The wildcard is what a player on a network needs; a scripted run names the
    /// loopback address instead, so nothing asks about the firewall.</summary>
    [Theory]
    [InlineData("47600", "*", 47600)]
    [InlineData("127.0.0.1:47600", "127.0.0.1", 47600)]
    [InlineData("127.0.0.1", "127.0.0.1", 47500)]
    [InlineData("99999", "*", 47500)]
    [InlineData("", "*", 47500)]
    public void AHostValueSplitsIntoABindAddressAndAPort(string value, string bind, int port)
    {
        var parsed = SessionSpec.ParseHost(value);
        Assert.Equal(bind, parsed.Bind);
        Assert.Equal(port, parsed.Port);
    }

    /// <summary>The two network flags reaching the spec, and their absence leaving it local.
    /// </summary>
    [Fact]
    public void TheNetworkFlagsReachTheSpec()
    {
        var local = SessionSpec.Parse(new[] { "--vs" });
        Assert.Null(local.NetHostPort);
        Assert.Null(local.NetJoin);
        Assert.Equal("*", local.NetHostBind);

        var bare = SessionSpec.Parse(new[] { "--vs", "--net-host" });
        Assert.Equal(47500, bare.NetHostPort);
        Assert.Equal("*", bare.NetHostBind);

        var host = SessionSpec.Parse(new[] { "--vs", "--net-host=127.0.0.1:47600" });
        Assert.Equal(47600, host.NetHostPort);
        Assert.Equal("127.0.0.1", host.NetHostBind);

        var guest = SessionSpec.Parse(new[] { "--vs", "--net-join=127.0.0.1:47600" });
        Assert.Null(guest.NetHostPort);
        Assert.Equal("127.0.0.1:47600", guest.NetJoin);
    }

    /// <summary>The enhanced-pass bisect doors: none closed by default, each flag closes its own
    /// bit, and several on one line accumulate rather than replacing each other.</summary>
    [Fact]
    public void EnhancedPassDoorsAccumulate()
    {
        Assert.Equal(EnhancedPasses.None, SessionSpec.Parse(new[] { "--fly" }).SkippedPasses);
        Assert.Equal(EnhancedPasses.Ssao, SessionSpec.Parse(new[] { "--no-ssao" }).SkippedPasses);
        Assert.Equal(EnhancedPasses.Ssr, SessionSpec.Parse(new[] { "--no-ssr" }).SkippedPasses);
        Assert.Equal(EnhancedPasses.Glow, SessionSpec.Parse(new[] { "--no-glow" }).SkippedPasses);
        Assert.Equal(
            EnhancedPasses.SoftShadows, SessionSpec.Parse(new[] { "--no-soft-shadows" }).SkippedPasses);

        var all = SessionSpec.Parse(new[] { "--no-ssao", "--no-ssr", "--no-glow", "--no-soft-shadows" });
        Assert.Equal(
            EnhancedPasses.Ssao | EnhancedPasses.Ssr | EnhancedPasses.Glow | EnhancedPasses.SoftShadows,
            all.SkippedPasses);
    }
}
