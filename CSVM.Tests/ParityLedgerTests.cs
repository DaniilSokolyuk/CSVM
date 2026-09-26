using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using CSVM.Tooling;
using Xunit;

namespace CSVM.Tests;

/// <summary>
/// The parity ledger: every mechanism and every envelope row, on every stock airframe, in exactly
/// one of three classes. Decoded is the executable's or the data's own value; exception is a named
/// product decision; unsupported is not ported. There is no conflict class, a footage figure that
/// disagrees with a traced mechanism is discarded and carried as an annotation.
/// This class both CHECKS the ledger (a plant constant or a probe row with no class fails) and
/// GENERATES it, to <c>CSVM_LEDGER_OUT</c>, which is what docs/org/flightModel.md publishes.
/// </summary>
public class ParityLedgerTests
{
    private const string Decoded = "decoded";
    private const string Exception = "exception";
    private const string Unsupported = "unsupported";

    // How the constant inventory's four finer classes land in the ledger's three. A decoded, an
    // authored and a unit constant are all values CSVM did not invent, so they read as one class
    // here and keep their own source; only a named product decision separates out.
    private static readonly Dictionary<string, string> InventoryClasses = new(StringComparer.Ordinal)
    {
        [FlightConstantInventoryTests.Decoded] = Decoded,
        [FlightConstantInventoryTests.Authored] = Decoded,
        [FlightConstantInventoryTests.Unit] = Decoded,
        [FlightConstantInventoryTests.ProductException] = Exception,
    };

    // The mechanisms that are not a constant: a decoded law, a named product decision, or something
    // the port does not carry. The constant inventory covers the numbers; this covers the behaviour
    // they sit in, and the disproofs, which are findings rather than code.
    private static readonly (string Name, string Class, string Source)[] Mechanisms =
    {
        ("lift as a clamped demanded load factor", Decoded, "FUN_0041abd0, FUN_0048fc40"),
        ("the liftAOAs relative-wind blend, player only", Decoded, "0x48c520, the blend at FUN_0048c470"),
        ("drag as a Mach polar with no induced term", Decoded, "FUN_0041ada0"),
        ("attitude-scaled thrust", Decoded, "0x48fd00, 0x48fd14"),
        ("the thrust-available Mach curve", Decoded, "FUN_0041abd0 into 0x48fce7"),
        ("the throttle lever, linear, and its 0.5/s slew", Decoded, "0x48fce7, 0x48e63f-0x48e6c3"),
        ("atmosphere band selection at 2000 m", Decoded, "0x0071bb3c, written by FUN_00463640"),
        ("the thin band above 2000 m, and the ceiling it makes", Decoded,
            "FUN_0041aca0's second arm; no altitude clamp exists, the position write at 0x0048ea81 is unconditional"),
        ("the stall flag and its nose-drop torque", Decoded, "_DAT_0071c41c, the torque at 0x48d158"),
        ("the low-speed authority ramp", Decoded, "FUN_0048bdd0"),
        ("the pitch-only high-speed fade", Decoded, "0x48be22-0x48be68, 0x0071c400 / 0x0071c404"),
        ("the opposing-command limiter's AOA window", Decoded, "0x48c9f4-0x48ca18, 0x0071c42c"),
        ("the opposing-command limiter's G ramp", Decoded, "0x48ca1e-0x48ca61, min at 0x48ca69"),
        ("the G ramp reads the SAME tick's delivered lift", Decoded,
            "0x48c883 writes it before the 0x48ca1e read, and FUN_0048e580 integrates both halves "
            + "afterwards, so Step builds its forces from the entering attitude too"),
        ("the limiter's separating-command sign rule", Decoded, "FUN_0053fd40 at 0x48c9ae, pitch at 0x48cb52"),
        ("bank coupling into yaw and into pitch", Decoded, "0x48ccb3, 0x48cd36"),
        ("weathervane centring, player only", Decoded, "FUN_00490f70, applied at 0x48ce3d"),
        ("angular damping and the reciprocal inertias", Decoded, "FUN_00491820"),
        ("the far-field speed-hold plant", Decoded, "0x48c4e9-0x48c603"),
        ("ground blow as a control bias", Decoded, "FUN_0048c220, called at 0x48cf95"),
        ("the keyboard stick accumulator", Decoded, "FUN_00487460"),
        ("the mouse-flying arm's is_autogyro roll/yaw exchange", Decoded,
            "0x4876f4, inside FUN_00487460's mouse arm; MouseFlight.Read exchanges and negates the "
            + "same two sources"),
        ("the six-slot control-surface mix and its 2/s exponential", Decoded,
            "FUN_004b27e0 / FUN_004b2a40 / FUN_004b2ca0, smoothing FUN_00460490"),
        ("contact placement, normal impulse and angular deposit", Decoded, "FUN_0048d7f0, 0x48e4bc"),
        ("collision damage, armour before health", Decoded, "FUN_0048d2c0"),
        ("the per-contact camera shake", Decoded,
            "FUN_0048d2c0's block-5 kick at 0x48d409, min(speed*s*0.03, 0.15)"),
        ("the every-other-frame contact sweep", Decoded, "the parity gate at 0x48ed79"),
        ("the nitro tank and its state machine", Decoded, "FUN_004aff80, FUN_004b2110"),
        ("engine torque: none exists", Decoded, "every write to FUN_0048c470's angular accumulator"),
        ("roll-to-pitch coupling: none exists", Decoded, "every read of [obj+0x100] and [obj+0x114]"),
        ("ambient turbulence: nothing ships", Decoded, "shake block 5, the five xrefs of FUN_0042c070"),
        ("the one-sided negative C_L ceiling is unreachable", Decoded,
            "0x48c821-0x48c852 builds n as a vector length, so FUN_0041abd0 is never handed a negative C_L"),
        ("the level_off_rate auto-level torque is unreachable", Decoded,
            "0x48cedc / 0x48cf76 read def+0x650, a token the parser accepts and no shipped dynamics "
            + "block authors, so the term is zero on all eleven airframes (LevelOffRateAbsenceTests)"),
        ("the AI's medium_aishake on a nitro engage", Decoded,
            "FUN_00473430(1), the middle def of the 0071c2f4 table, on the aircraft's own node"),
        ("the AI's positional snd_nitro blip", Decoded,
            "the keyed loop's 0.1 s refresh inside FUN_004b2110, reached at the engage and by the "
            + "release calls after the maneuver"),
        ("the nitro decay lockout on the decay instance", Decoded,
            "the completion callback registered at 0x4b2271 (handler 0x4b20f0); CSVM reads the "
            + "instance's own ANIM_STATE instead"),
        ("a dead AI's throttle and surfaces freeze at their last commanded values", Decoded,
            "FUN_004b82d0 zeroes neither +0x124 nor the surface deflections; StepWreckFall steps _lastInput unchanged"),
        ("a wreck flies the near-field plant", Decoded,
            "obj+0x384's only writers are the -fd switch, the console's fd / ifon and the constructor's zero, "
            + "so no crash ever takes the 0x48c4ba arm"),

        ("far-field range is measured to the NEAREST human pilot", Exception,
            "plan Decision 3; the original presumes one player"),
        ("control surfaces, shake and nitro edges run for EVERY human pilot", Exception,
            "plan Decision 3; the original's guard is the single player"),
        ("the Fury's rudder animates", Exception,
            "CSVM also matches l_rudder_rotate and a digitless l_elevator, which the %d lookups miss"),

        ("a live producer for an AI's nitro injector", Unsupported,
            "AiSpawn.Nitro reads roster slot 34; the mission spawner does not read roster blocks yet"),
    };

    // Every scenario the flight-envelope probe reports, its class, and the term that bounds it. The
    // Footage column is the discarded annotation: it appears in the report's prose and gates
    // nothing. A probe row missing from here fails TheLedgerCoversEveryEnvelopeRow.
    private static readonly (string Row, string Class, string Bound, string Footage)[] EnvelopeRows =
    {
        ("level-top-speed", Decoded, "thrust = drag; no clamp binds", "300.40 mph"),
        ("accel-150-290", Decoded, "the thrust curve against the Mach polar", "3.76 s"),
        ("terminal-dive", Decoded, "thrust x attitude scale + gravity = drag", "355.20 mph"),
        ("roll-360", Decoded, "roll torque against ang_momentum_damp", "2.05 s off the ADI"),
        ("pitch-rate", Decoded, "the AOA window and the lift-demand lag", "33.00 deg/s"),
        ("yaw-360", Decoded, "yaw torque times the authored authority curve", "28.60 s"),
        ("altitude-ceiling", Decoded, "the 2000 m band edge, coasted past", "~6,600 ft plateau"),
        ("level-speed-near-cap", Decoded, "thrust = drag, with the band edge 12 m above", "300.40 mph"),
        ("sustained-turn-speed", Decoded, "the AOA window and the C_L ceiling", "222.94 mph, 449.8 deg"),
        ("sustained-turn-sink", Decoded, "delivered lift against nom_gravity", "1.85 ft/s"),
        ("sustained-turn-rate", Decoded, "the AOA window and the bank coupling", "18.95 deg/s"),
        ("eighth-throttle-speed", Decoded, "thrust x lever = drag", "none; every candidate was footage"),
        ("decel-290-150", Decoded, "the Mach polar alone", "7.04 s"),
        ("zoom-climb", Decoded, "the AOA window and the lift demand's clamps", "936 ft, apex at 6.5 s"),
        ("zoom-climb-min-speed", Decoded, "the same loop's energy split", "127.9 mph"),
        ("stall-departure", Decoded, "the C_L ceiling and the stall_mag torque", "none"),
    };

    // Which suite drives each branch to its own conclusion. The dump reaches seven of the sixteen;
    // the rest are covered by a targeted instrument, which is why an unreached branch here is a
    // statement about the dump's scenarios rather than an untested path.
    private static readonly (string Branch, string Instrument)[] BranchInstruments =
    {
        ("dense-band", "AtmosphereBandTests"),
        ("thin-band", "AtmosphereBandTests, FlightConstantInventoryTests.TheBandEdgeCeilingsTheClimb"),
        ("low-speed-ramp", "ControlAuthorityRampTests"),
        ("pitch-fade", "LatentControlAuthorityTests, on a synthetic airframe"),
        ("aoa-window", "LatentControlAuthorityTests, ControlLimiterTests"),
        ("g-ramp", "ControlLimiterTests, which measures the graze as a bounded fraction"),
        ("g-clamp", "ControlLimiterTests; no stock manoeuvre demands +/-9 G"),
        ("cl-ceiling", "StallNoseDropTests, PartThrottleEquilibriumTests' level-flight floor"),
        ("stall", "StallNoseDropTests, AutogyroStallNoseDownTests"),
        ("dive-cap", "FlightConstantInventoryTests.TheDiveSpeedCapNeverBinds"),
        ("weathervane", "WeathervaneTests"),
        ("bank-coupling", "BankCouplingTests"),
        ("far-field", "FarFieldPlantTests and the ai-far-field-plant suite"),
        ("boost", "NitroSystemTests"),
        ("ground-blow", "GroundBlowTests"),
    };

    private static string ZrdrPath =>
        SessionPaths.PreferUnzipped(Path.Combine(TestData.ExtractedRoot!, "zrdr.zip"));

    /// <summary>Every plant constant is in the ledger with one of the three classes and, unless it
    /// is a product exception, a source. A constant with no source is a fitted constant.</summary>
    [Fact]
    public void EveryPlantConstantCarriesAClassAndASource()
    {
        foreach (var row in FlightConstantInventoryTests.Inventory)
        {
            string name = $"{row.Type}.{row.Name}";
            Assert.True(InventoryClasses.ContainsKey(row.Class),
                $"{name} is classified '{row.Class}', which the ledger has no class for");
            Assert.False(string.IsNullOrWhiteSpace(row.Source),
                $"{name} has no source, a constant with no address, global or data key behind it "
                + "is a fitted constant until somebody proves otherwise");
        }
    }

    /// <summary>Every mechanism and every branch is classified once, and no class is empty. An
    /// empty class would mean the three-way split had quietly become a two-way one.</summary>
    [Fact]
    public void EveryLedgerRowIsClassifiedAndNoClassIsEmpty()
    {
        var classes = new[] { Decoded, Exception, Unsupported };
        foreach (var (name, cls, source) in Mechanisms)
        {
            Assert.True(classes.Contains(cls), $"{name} is classified '{cls}'");
            Assert.False(string.IsNullOrWhiteSpace(source), $"{name} has no source");
        }

        foreach (string cls in classes)
        {
            Assert.True(Mechanisms.Any(x => x.Class == cls), $"no mechanism is classified {cls}");
        }

        Assert.Equal(
            EnvelopeMargins.Branches.OrderBy(x => x, StringComparer.Ordinal),
            BranchInstruments.Select(x => x.Branch).OrderBy(x => x, StringComparer.Ordinal));
    }

    /// <summary>Every row the probe reports, on every airframe, has a ledger class. This is the
    /// able-to-fail half: a scenario added to the probe without a class fails here rather than
    /// arriving in the published ledger as a blank.</summary>
    [ExtractedDataFact]
    public void TheLedgerCoversEveryEnvelopeRowOnEveryAirframe()
    {
        var known = EnvelopeRows.Select(x => x.Row).ToHashSet(StringComparer.Ordinal);
        var all = Probes.FlightEnvelopeAll(ZrdrPath);
        Assert.Null(all.Error);

        foreach (var row in all.Rows)
        {
            Assert.True(known.Contains(row.Name),
                $"{row.Plane}'s '{row.Name}' has no parity-ledger row, add one to "
                + "ParityLedgerTests.EnvelopeRows and regenerate docs/org/flightModel.md's ledger");
        }

        foreach (string name in known)
        {
            Assert.Equal(Probes.StockAirframes.Count, all.Rows.Count(x => x.Name == name));
        }
    }

    /// <summary>Writes the published ledger to <c>CSVM_LEDGER_OUT</c>. The tables in
    /// docs/org/flightModel.md's "Parity ledger" are this file's output, so republishing them is a
    /// copy rather than a retype.</summary>
    [ExtractedDataFact]
    public void PublishTheLedger()
    {
        System.Threading.Thread.CurrentThread.CurrentCulture = CultureInfo.InvariantCulture;
        string? outPath = Environment.GetEnvironmentVariable("CSVM_LEDGER_OUT");
        var all = Probes.FlightEnvelopeAll(ZrdrPath);
        Assert.Null(all.Error);

        var sb = new StringBuilder();
        sb.AppendLine("#### Constants");
        sb.AppendLine();
        sb.AppendLine("| Constant | Value | Class | Source |");
        sb.AppendLine("|---|---:|---|---|");
        foreach (var c in FlightConstantInventoryTests.Inventory)
        {
            sb.AppendLine($"| `{c.Type}.{c.Name}` | {Num(c.Value)} | {InventoryClasses[c.Class]} "
                          + $"| {c.Source} |");
        }

        sb.AppendLine();
        sb.AppendLine("#### Mechanisms");
        sb.AppendLine();
        sb.AppendLine("| Mechanism | Class | Source |");
        sb.AppendLine("|---|---|---|");
        foreach (var (name, cls, source) in Mechanisms)
        {
            sb.AppendLine($"| {name} | {cls} | {source} |");
        }

        sb.AppendLine();
        sb.AppendLine("#### Envelope rows");
        sb.AppendLine();
        sb.AppendLine("| Row | Class | Bounding term | Discarded footage |");
        sb.AppendLine("|---|---|---|---|");
        foreach (var (row, cls, bound, footage) in EnvelopeRows)
        {
            sb.AppendLine($"| `{row}` | {cls} | {bound} | {footage} |");
        }

        sb.AppendLine();
        sb.AppendLine("#### Per airframe");
        sb.AppendLine();
        sb.AppendLine("| Airframe | rows | decoded | exception | unsupported | branches reached |");
        sb.AppendLine("|---|---:|---:|---:|---:|---:|");
        foreach (string plane in Probes.StockAirframes)
        {
            var rows = all.Rows.Where(x => x.Plane == plane).ToList();
            int dec = rows.Count(x => ClassOf(x.Name) == Decoded);
            int exc = rows.Count(x => ClassOf(x.Name) == Exception);
            int uns = rows.Count(x => ClassOf(x.Name) == Unsupported);
            sb.AppendLine($"| `{plane}` | {rows.Count} | {dec} | {exc} | {uns} "
                          + $"| {all.ReachedByPlane[plane].Count}/{EnvelopeMargins.Branches.Length} |");
        }

        sb.AppendLine();
        sb.AppendLine("#### Branch coverage");
        sb.AppendLine();
        sb.AppendLine("| Branch | Airframes reaching it | Instrument that drives it |");
        sb.AppendLine("|---|---:|---|");
        foreach (var (branch, instrument) in BranchInstruments)
        {
            int hits = all.ReachedByPlane.Count(x => x.Value.Contains(branch));
            sb.AppendLine($"| `{branch}` | {hits}/{Probes.StockAirframes.Count} | {instrument} |");
        }

        if (outPath != null)
        {
            File.WriteAllText(outPath, sb.ToString(), new UTF8Encoding(false));
        }

        Assert.Contains("Branch coverage", sb.ToString(), StringComparison.Ordinal);
    }

    private static string ClassOf(string row) =>
        EnvelopeRows.First(x => x.Row == row).Class;

    private static string Num(double v) =>
        v == Math.Floor(v) && Math.Abs(v) < 1e6
            ? v.ToString("0", CultureInfo.InvariantCulture)
            : v.ToString("G6", CultureInfo.InvariantCulture);
}
