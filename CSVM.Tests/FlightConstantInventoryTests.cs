using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using CSVM.Flight.Airframe;
using CSVM.Utils;
using Godot;
using Xunit;

namespace CSVM.Tests;

/// <summary>
/// The plant's constant inventory: every non-authored number in the live translational and
/// rotational path, its classification, and the reachability measurement behind each CSVM
/// safeguard. The table this mirrors is docs/org/flightModel.md, "The plant's constant inventory".
/// A constant added, removed or moved off its recorded value fails <see cref="TheInventoryIsComplete"/>,
/// which is what stops a new fitted number arriving unclassified; the safeguard tests below fail
/// when a classification's binding claim goes stale.
/// </summary>
public class FlightConstantInventoryTests
{
    // The four classes docs/org/flightModel.md's inventory table uses: read out of crimson.exe, a
    // mirror of an authored data key, a conversion factor, or a deliberate CSVM exception with a
    // reachability measurement behind it. The contact rules are censused with the plant, so a
    // fitted contact term cannot come back where the plant's own census cannot see it.
    internal const string Decoded = "decoded";
    internal const string Authored = "authored";
    internal const string Unit = "unit";
    internal const string ProductException = "exception";

    internal static readonly string[] AllPlanes =
    {
        "player_bhawk", "player_pfighter", "player_fury", "player_warhawk", "player_autogyro",
        "player_avenger", "player_balmoral", "player_brigand", "player_fbrand", "player_kestrel",
        "player_peacemaker",
    };

    // The inventory itself: every const the plant carries, its value, its class and where the value
    // comes from. The Source column is what the parity ledger publishes and is deliberately short,
    // an address, a global or a data key. The prose behind it is docs/org/flightModel.md's own
    // evidence column, so a row here is a claim that the doc row still applies.
    internal static readonly (string Type, string Name, double Value, string Class, string Source)[]
        Inventory =
    {
        ("FlightModel", "ThrustMachFloor", 0.1, Decoded, "0x6034a8, written back at 0x41ad02"),
        ("FlightModel", "ThrustVRefSlope", 0.84, Decoded, "0x60349c"),
        ("FlightModel", "ThrustVRefMach", 0.112, Decoded, "0x603498"),
        ("FlightModel", "ThrustMachTrim", 1.0 / 60.0, Decoded, "0x603494"),
        ("FlightModel", "ThrustPowMach", 1.41, Decoded, "0x6034a0, the _CIpow exponent"),
        ("FlightModel", "DenseThrustPowBase", 1.33 * 0.98842078, Decoded,
            "0x6034a4 x FUN_0041aca0's dense band factor, multiplied at 0x41ad0f"),
        ("FlightModel", "ThinThrustPowBase", 1.33 * 0.7348, Decoded,
            "0x6034a4 x FUN_0041aca0's thin band factor, multiplied at 0x41ad0f"),
        ("FlightModel", "AttitudeThrustBoth", 0.24, Decoded, "0x6080dc, applied at 0x48fd14"),
        ("FlightModel", "AttitudeThrustUp", 0.13, Decoded, "0x6080d8, the branch at 0x48fd00"),
        ("FlightModel", "LiftGMin", -5.0, Decoded, "the lift clamp in FUN_0041abd0"),
        ("FlightModel", "LiftGMax", 9.0, Decoded, "the lift clamp in FUN_0041abd0"),
        ("FlightModel", "ClMaxStatic", 0.75, Decoded, "the ceiling in FUN_0041abd0"),
        ("FlightModel", "ClMaxMach", 0.15, Decoded, "the ceiling in FUN_0041abd0"),
        ("FlightModel", "BandThresholdFt", 6561.6796875, Decoded, "0x0071bb3c, stored at 0x46368b"),
        ("FlightModel", "DenseDensitySlugPerFt3", 2.2688e-3, Decoded, "FUN_0041aca0, dense band"),
        ("FlightModel", "DenseSoundFps", 1109.5, Decoded, "FUN_0041aca0, dense band"),
        ("FlightModel", "ThinDensitySlugPerFt3", 1.3560e-4, Decoded, "FUN_0041aca0, thin band"),
        ("FlightModel", "ThinSoundFps", 968.0, Decoded, "FUN_0041aca0, thin band"),
        ("FlightModel", "FeetPerMetre", 3.28084, Unit, "the aero path's imperial intermediates"),
        ("FlightModel", "MetresPerFoot", 0.3048, Unit, "the aero path's imperial intermediates"),
        ("FlightModel", "StandardG", 9.82, Decoded, "the force-to-acceleration multiply at 0x491290"),
        ("FlightModel", "StallWarnLoadFactor", 2.35, Decoded,
            "0x00608338/0x00608334, the lamp driver at 0x0049f7e6; a cue no force term reads"),
        ("FlightModel", "MaxDiveSpeedFrac", 1.75, ProductException,
            "a CSVM numerical backstop with no counterpart; measured non-binding on all eleven"),
        ("FlightModel", "GroundBlowIntoFactor", 0.05, Decoded, "the player branch of FUN_0048c220"),
        ("FlightModel", "GroundBlowVelocitySteer", 2.0, Decoded, "the gbc console command's global"),
        ("FlightModel", "NoseChaseFactor", 0.0, Decoded,
            "decoded-absent from FUN_0048c470 / FUN_0048fc40 / FUN_0048e580"),
        ("FlightModel", "AoaLimiterFactorDefault", 1.0, Decoded, "0x48c9f4-0x48ca18 at full strength"),
        ("FlightModel", "BounceLeverScale", 2.25, Decoded, "0x00608108"),
        ("FlightModel", "ContactPushOut", 0.03, Decoded, "0x006080c4, placed at 0x48dfce"),
        ("FlightModel", "BounceAngularHalf", 0.5, Decoded, "0x006032e0, deposited at 0x48e4bc"),
        ("FlightModel", "DragPolarScale", 0.73, Decoded, "0x603474, shared with the thrust curve"),
        ("FlightModel", "DragPolarParasite", 0.12, Decoded, "FUN_0041ada0, the polar in Mach"),
        ("FlightModel", "DragPolarLinear", 0.8, Decoded, "FUN_0041ada0, the polar in Mach"),
        ("FlightModel", "DragPolarQuad", 0.5, Decoded, "FUN_0041ada0, the polar in Mach"),
        ("FlightModel", "PitchTune", 1.0, Decoded, "decoded-absent from FUN_0048c470's torque chains"),
        ("FlightModel", "YawTune", 1.0, Decoded, "decoded-absent from FUN_0048c470's torque chains"),
        ("FlightModel", "RollTune", 1.0, Decoded, "decoded-absent from FUN_0048c470's torque chains"),
        ("FlightModel", "BankYawCoupling", 0.205, Decoded, "0x6289f8, the yaw row at 0x48ccb3"),
        ("FlightModel", "BankPitchCoupling", 0.165, Decoded, "0x6289fc, the pitch row at 0x48cd36"),
        ("FlightModel", "WeathervaneHalfAngle", 0.5, Decoded, "the quaternion-log halving 0x4916fe-0x4917f0"),
        ("FlightModel", "AiNoseSpeedFloor", 4.4704, Decoded, "0x608128, the block at 0x48e95e-0x48e998"),
        ("FlightModel", "ReverseAuthorityFloor", 0.2, Decoded, "0x6034fc, FUN_0048bdd0's fifth output"),
        ("FlightModel", "FarFieldRangeM", 1000.0, Decoded, "0x00607a18 (1e6 m2), compared at 0x48c4ee"),
        ("FlightModel", "FarFieldAiSpeedBonus", 5.0, Decoded, "0x006036bc, spent at 0x48c5ae"),
        ("FlightModel", "BoostLever", 1.8, Decoded, "0x48fcb6"),
        ("FlightModel", "BoostDragFactor", 0.8, Decoded, "0x48fcbd (1.0 at 0x48fccf otherwise)"),
        ("NitroSystem", "Capacity", 30.0, Decoded, "0x4b02c4, into [obj+0x8b4] and [obj+0x8b8]"),
        ("NitroSystem", "BurnRate", 4.0, Decoded, "0x4b02d5, [obj+0x8bc], spent at 0x49f820"),
        ("NitroSystem", "RechargeRate", 1.0, Decoded, "0x4b02df / 0x4b02e9, added at 0x49f838"),
        ("NitroSystem", "EngageFraction", 0.99, Decoded, "0x6080a8, the engage line at 0x487eba"),
        ("NitroSystem", "CutoffFraction", 0.05, Decoded, "0x6034d8, the cutoff at 0x487ea1"),
        ("NitroSystem", "MinBoostAnimSeconds", 1.0, Decoded, "0x47a838, def+0x188, read at 0x4b2224"),
        ("NitroSystem", "LoopKeyedSeconds", 0.1, Decoded, "0x4b22b8, the keyed snd_nitro refresh's own argument"),
        ("PhysicsConstants", "NomGravity", 20.0, Authored, "player.json's nom_gravity"),
        ("PhysicsConstants", "MphToMs", 0.44704, Decoded, "the parser's speed-token scale, 0x00607b2c"),
        ("StickRamp", "Rate", 2.5, Decoded, "FUN_00487460, 0.4 s of held key to full deflection"),
        ("CollisionDamage", "EntityCut", 0.2, Decoded, "0x48d51a / 0x48d526"),
        ("CollisionDamage", "EntityGrace", 1.0, Decoded, "0x48d383 / 0x48d395"),
        ("CollisionDamage", "SpawnGrace", 1.5, Decoded, "the spawn write of obj+0xAC"),
        ("CollisionDamage", "ContactShakeFactor", 0.03, Decoded, "0x6080c4, read at 0x48d3dc"),
        ("CollisionDamage", "ContactShakeCap", 0.15, Decoded, "0x6036a8, compared at 0x48d3eb"),
        ("AircraftContactResolver", "EmbedPushOut", 0.3, ProductException,
            "m per un-embed attempt; the original's single-sphere placement cannot embed"),
        ("AircraftContactResolver", "EmbedTries", 3.0, ProductException,
            "attempts before the airframe is destroyed instead of left inside the world"),
    };

    private const float Dt = 1f / 60f;

    // The atmosphere band edge in metres, the plant's threshold as a scenario reads altitude. It is
    // the ceiling, so a climb test measures against it and never against a constant of its own.
    private const float BandEdgeM = 2000f;

    // The plant's whole config surface. Each key read-throughs one inventory row above, so a key
    // added without a constant (or a constant exposed without a doc row) fails the census below.
    private static readonly string[] ConfigKeys =
    {
        "aoaLimiterFactor", "liftGMax", "liftGMin", "noseChaseFactor", "pitchTune",
        "rollTune", "stallWarnLoadFactor", "yawTune",
    };

    private static readonly Type[] InventoryTypes =
    {
        typeof(FlightModel), typeof(PhysicsConstants), typeof(StickRamp), typeof(CollisionDamage),
        typeof(AircraftContactResolver), typeof(NitroSystem),
    };

    private static string ZrdrPath =>
        SessionPaths.PreferUnzipped(Path.Combine(TestData.ExtractedRoot!, "zrdr.zip"));

    /// <summary>Every constant the plant carries is in the inventory at the recorded value, and the
    /// inventory names nothing the plant has dropped. A new constant is unclassified until its row
    /// is added here and its evidence in docs/org/flightModel.md.</summary>
    [Fact]
    public void TheInventoryIsComplete()
    {
        var live = LiveConstants();
        var known = Inventory.ToDictionary(x => $"{x.Type}.{x.Name}", x => x);

        var added = live.Keys.Where(k => !known.ContainsKey(k)).OrderBy(k => k, StringComparer.Ordinal).ToList();
        Assert.True(added.Count == 0,
            $"unclassified plant constant(s): {string.Join(", ", added)}, add a row to "
            + "FlightConstantInventoryTests.Inventory and its evidence to docs/org/flightModel.md, "
            + "\"The plant's constant inventory\". A constant with no provenance is a fitted "
            + "constant until someone proves otherwise");

        var gone = known.Keys.Where(k => !live.ContainsKey(k)).OrderBy(k => k, StringComparer.Ordinal).ToList();
        Assert.True(gone.Count == 0,
            $"the inventory names constant(s) the plant no longer has: {string.Join(", ", gone)}, "
            + "delete the row here and the matching row in docs/org/flightModel.md");

        foreach (var (key, value) in live.OrderBy(x => x.Key, StringComparer.Ordinal))
        {
            var row = known[key];
            Assert.True(Math.Abs(value - row.Value) <= Math.Abs(row.Value) * 1e-6 + 1e-9,
                $"{key} is {value:R}, the inventory records {row.Value:R} as {row.Class}, a moved "
                + "value needs its evidence re-checked in docs/org/flightModel.md before the row "
                + "here is updated to match it");
        }
    }

    /// <summary>The three per-axis calibration factors are exactly 1, which is the decode's finding
    /// rather than a tuning choice: <c>FUN_0048c470</c> builds each axis' stick torque as
    /// torque · stick · authority · dt and multiplies in nothing else. They survive as named
    /// constants so the config keys stay available for an A/B at the controls.</summary>
    [Fact]
    public void NoAxisCarriesACalibrationFactor()
    {
        foreach (string axis in new[] { "PitchTune", "YawTune", "RollTune" })
        {
            double v = LiveConstants()[$"FlightModel.{axis}"];
            Assert.True(v == 1.0,
                $"{axis} is {v:R}, the original's torque chain carries no per-axis factor "
                + "(docs/org/flightModel.md, \"The *Tune rates\"), so restoring one needs a "
                + "mechanism traced in the binary and never a rate timed off footage");
        }
    }

    /// <summary>The plant exposes exactly the config keys the inventory accounts for. The dump is
    /// the same registry <c>--dump-config</c> emits, so a key added to Step without an inventory row
    /// shows up here rather than in a template nobody reads.</summary>
    [Fact]
    public void TheConfigSurfaceIsAccountedFor()
    {
        var warm = new FlightModel(new PlaneStats());
        warm.Reset(Vector3.Zero, Basis.Identity, 100f, 1f);
        warm.Step(default, Dt);
        _ = warm.IsStallWarned();

        string path = Path.Combine(TestData.TempDir(), "config-template.json");
        Config.DumpConfig(path);
        using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
        var keys = doc.RootElement.GetProperty("flightModel").EnumerateObject()
            .Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray();

        Assert.True(ConfigKeys.SequenceEqual(keys),
            $"the flightModel config block is [{string.Join(", ", keys)}] against the accounted-for "
            + $"[{string.Join(", ", ConfigKeys)}], every key overrides one inventory row, so add "
            + "or drop the row alongside it");
    }

    /// <summary>The dive-speed cap is a numerical backstop and must not bind: a cap that binds
    /// replaces a measured terminal with a guess. Flown on every airframe in the manoeuvres that
    /// make the most speed, the peak stays under the cap by a wide margin, and the aerodynamics
    /// terminate every dive on their own.</summary>
    [ExtractedDataFact]
    public void TheDiveSpeedCapNeverBinds()
    {
        foreach (string plane in AllPlanes)
        {
            var stats = PlaneStats.Load(ZrdrPath, plane);
            var (peak, where) = FastestFlight(stats);
            Assert.True(peak < 1.35f,
                $"{plane}: peak speed reaches {peak:0.000} x fd_speed ({where}) against the "
                + "MaxDiveSpeedFrac backstop at 1.75, the cap is close enough to bind, and a bound "
                + "cap reports itself instead of the model's own terminal speed");
        }
    }

    /// <summary>The able-to-fail control for the dive-cap disproof: the manoeuvres must still make
    /// real speed. A set that no longer exceeds fd_speed would pass the test above while measuring
    /// nothing (METHOD-9).</summary>
    [ExtractedDataFact]
    public void TheDiveSpeedCapDisproofIsAbleToFail()
    {
        var stats = PlaneStats.Load(ZrdrPath, "player_bhawk");
        var (peak, where) = FastestFlight(stats);
        Assert.True(peak > 1.05f,
            $"player_bhawk: the fastest manoeuvre peaks at only {peak:0.000} x fd_speed ({where}), "
            + "it has gone too gentle to approach any cap, so the dive-cap disproof measures nothing");
    }

    /// <summary>The band edge is the ceiling, with no clamp anywhere: a sustained full-throttle climb
    /// crosses 2000 m, loses the lift and thrust to continue, and falls back. A climb that ran on past
    /// the edge would mean the thin band had stopped biting.</summary>
    [ExtractedDataFact]
    public void TheBandEdgeCeilingsTheClimb()
    {
        var stats = PlaneStats.Load(ZrdrPath, "player_bhawk");
        var m = Climb(stats, 1900f, 22f, 240f, out float apexM, out float crossingVy);
        Assert.True(crossingVy > 0f,
            "player_bhawk: a 22° full-throttle climb from 1900 m never crossed the 2000 m band edge, "
            + "so this measures nothing");
        Assert.True(m.Position.Y < apexM - 50f,
            $"player_bhawk: the climb apexed at {apexM:0.0} m and is still at {m.Position.Y:0.0} m, "
            + "the thin band above 2000 m must stop the climb and let it fall back "
            + "(docs/org/flightModel.md, \"The resting altitude cap is the atmosphere band edge\")");
    }

    /// <summary>What an aircraft reaches above the band edge is the coast its crossing rate buys, not
    /// a constant, so the apex tracks the frictionless v²/2g on every airframe. A clamp returning
    /// would collapse this toward nothing; a band that stopped biting would leave it unbounded.</summary>
    [ExtractedDataFact]
    public void TheApexAboveTheBandEdgeIsTheCoastTheClimbRateBuys()
    {
        foreach (string plane in AllPlanes)
        {
            var stats = PlaneStats.Load(ZrdrPath, plane);
            Climb(stats, 1950f, 89f, 60f, out float apexM, out float crossingVy);
            Assert.True(crossingVy > 0f, $"{plane}: never crossed the band edge, so this measures nothing");
            float ballistic = crossingVy * crossingVy / (2f * 9.82f);
            float above = apexM - BandEdgeM;
            Assert.True(above > 0.3f * ballistic && above < 1.5f * ballistic,
                $"{plane}: crossed the edge at {crossingVy:0.0} m/s and apexed {above:0.0} m above it, "
                + $"against the {ballistic:0.0} m a frictionless coast buys, the height above the edge "
                + "must come from the climb, not from a constant");
        }
    }

    /// <summary>No shipped airframe reaches the resolver's no-damage-data arm, where every contact
    /// is fatal because there is no pool to survive on. Every player load authors zones and every AI
    /// load a whole armour/health pair, so the arm covers a bare rig alone and carries no speed
    /// threshold of its own.</summary>
    [ExtractedDataFact]
    public void NoStockAirframeFliesWithoutADamageLedger()
    {
        foreach (string plane in AllPlanes)
        {
            var player = PlaneStats.Load(ZrdrPath, plane);
            Assert.True(player.DestroyableParts.Count > 0,
                $"{plane}: the player load authors no destroyable_parts, so a contact would take the "
                + "resolver's no-ledger arm and be fatal at any speed");
            var ai = PlaneStats.LoadForAi(ZrdrPath, plane);
            Assert.True(ai.DestroyableParts.Count > 0 || ai.VehicleHealth is > 0f,
                $"{plane}: the AI load authors neither zones nor a whole health pair, so an AI "
                + "contact would take the resolver's no-ledger arm");
        }
    }

    /// <summary>The per-airframe margins behind the two safeguard disproofs, written to whatever
    /// file CSVM_SAFEGUARD_OUT names (the pattern FlightEnvelopeDump and ControlLimiterTests use).
    /// Asserts only that every airframe was flown.</summary>
    [ExtractedDataFact]
    public void DumpTheSafeguardMargins()
    {
        System.Threading.Thread.CurrentThread.CurrentCulture = CultureInfo.InvariantCulture;
        string? outPath = System.Environment.GetEnvironmentVariable("CSVM_SAFEGUARD_OUT");
        var sb = new StringBuilder();
        sb.AppendLine($"{"airframe",-19} {"fd mph",8} {"peak/fd",8} {"cap 1.75",9} {"margin",8} "
                      + $"{"apex m",9} {"cross m/s",10} {"steep m",8}  fastest manoeuvre");
        int rows = 0;
        foreach (string plane in AllPlanes)
        {
            var stats = PlaneStats.Load(ZrdrPath, plane);
            var (peak, where) = FastestFlight(stats);
            Climb(stats, 1900f, 22f, 240f, out float apexM, out float crossingVy);
            Climb(stats, 1950f, 89f, 60f, out float steepApexM, out _);
            sb.AppendLine($"{plane,-19} {stats.FdSpeed / 0.44704f,8:0.0} {peak,8:0.000} {1.75f,9:0.00} "
                          + $"{1.75f - peak,8:0.000} {apexM,9:0.0} {crossingVy,10:0.0} "
                          + $"{steepApexM - BandEdgeM,8:0.0}  {where}");
            rows++;
        }

        if (outPath != null)
            File.WriteAllText(outPath, sb.ToString(), new UTF8Encoding(false));
        Assert.Equal(AllPlanes.Length, rows);
    }

    // Every private const the plant types carry, as type-qualified name -> value. Only numeric
    // literals count: a string or bool const is not a plant quantity.
    private static Dictionary<string, double> LiveConstants()
    {
        var found = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var type in InventoryTypes)
        {
            const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
            foreach (var f in type.GetFields(Flags).Where(f => f.IsLiteral && !f.IsInitOnly))
            {
                object? raw = f.GetRawConstantValue();
                if (raw is float or double or int)
                    found[$"{type.Name}.{f.Name}"] = Convert.ToDouble(raw, CultureInfo.InvariantCulture);
            }
        }

        return found;
    }

    // Peak speed as a fraction of fd_speed over the manoeuvres that make the most of it: a vertical
    // and a shallow dive, and a held loop, which is the energy pump the cap exists for. Each is
    // entered at fd_speed, so the peak is the model's own terminal rather than the entry the
    // instrument chose (METHOD-21), a dive entered above terminal only ever decelerates.
    private static (float Peak, string Where) FastestFlight(PlaneStats stats)
    {
        (string Name, float PitchDeg, float EntryFrac, float Stick, float StartM)[] runs =
        {
            ("vertical dive", -89f, 1f, 0f, 2000f),
            ("70.7° dive", -70.7f, 1f, 0f, 2000f),
            ("held loop", 0f, 1f, 1f, 500f),
        };
        float peak = 0f;
        string where = "-";
        foreach (var (name, pitchDeg, entryFrac, stick, startM) in runs)
        {
            var m = new FlightModel(stats);
            var attitude = Basis.Identity.Rotated(Vector3.Right, Mathf.DegToRad(pitchDeg));
            m.Reset(new Vector3(0f, startM, 0f), attitude, stats.FdSpeed * entryFrac, 1f);
            var input = new FlightInput { Pitch = stick, Throttle = 1f };
            for (int i = 0; i < 7200; i++)
            {
                m.Step(input, Dt);
                if (m.Speed > peak * stats.FdSpeed)
                {
                    peak = m.Speed / stats.FdSpeed;
                    where = name;
                }
            }
        }

        return (peak, where);
    }

    // A held-attitude climb from below the band edge, reporting the apex it reaches and the vertical
    // speed it crossed the edge with, the two quantities a ballistic coast relates.
    private static FlightModel Climb(PlaneStats stats, float startM, float pitchDeg, float seconds,
        out float apexM, out float crossingVyMps)
    {
        var m = new FlightModel(stats);
        m.Reset(new Vector3(0f, startM, 0f), Basis.Identity.Rotated(Vector3.Right, Mathf.DegToRad(pitchDeg)),
            stats.FdSpeed, 1f);
        apexM = m.Position.Y;
        crossingVyMps = 0f;
        for (float t = 0f; t < seconds; t += Dt)
        {
            float before = m.Position.Y;
            m.Step(new FlightInput { Throttle = 1f }, Dt);
            if (crossingVyMps <= 0f && before <= BandEdgeM && m.Position.Y > BandEdgeM)
            {
                crossingVyMps = m.VelocityDir.Y * m.Speed;
            }

            apexM = Mathf.Max(apexM, m.Position.Y);
        }

        return m;
    }
}
