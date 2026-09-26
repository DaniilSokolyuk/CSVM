using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using CSVM.Flight.Airframe;
using Godot;

namespace CSVM.Tooling;

/// <summary>
/// How far an envelope scenario sits from every term that could bound it, and which of the plant's
/// decoded branches the scenario reached. Sampled once per integration step by the flight-envelope
/// probe, so a row's number is read beside the distance to the clamp, window or flag that would
/// have changed it. Nothing here feeds a force: it reads the public state a completed step left.
/// The branch names and what each one means are the parity ledger's own, docs/org/flightModel.md.
/// ⚠ One instance per airframe, and it is not thread-safe: the probe owns it for one report.
/// </summary>
public sealed class EnvelopeMargins
{
    /// <summary>Every branch the ledger tracks, in report order. A branch missing from an
    /// airframe's reached set is a decoded path this dump does not exercise, which is a statement
    /// about the scenario set rather than about the port.</summary>
    public static readonly string[] Branches =
    {
        "dense-band", "thin-band", "low-speed-ramp", "pitch-fade", "aoa-window", "g-ramp",
        "g-clamp", "cl-ceiling", "stall", "dive-cap", "weathervane", "bank-coupling",
        "far-field", "boost", "ground-blow",
    };

    // The band boundary read live out of the retail process (2000 m, held at 0x0071bb3c in feet),
    // which is the plant's ceiling as well as its atmosphere switch. A report threshold here, not a
    // plant term: the plant carries its own copy.
    private const float BandBoundaryM = 2000f;
    private const float LiftGMax = 9f;
    private const float MaxDiveSpeedFrac = 1.75f;

    private readonly HashSet<string> _reached = new(StringComparer.Ordinal);
    private Scenario _row = new();

    /// <summary>The branches this airframe's whole report reached, in <see cref="Branches"/>
    /// order.</summary>
    public IEnumerable<string> Reached => Branches.Where(_reached.Contains);

    /// <summary>The branches it did not, in the same order.</summary>
    public IEnumerable<string> Missed => Branches.Where(b => !_reached.Contains(b));

    /// <summary>Reads one completed step. The quantities are all public plant state, so a term
    /// that stops being reported here has stopped being observable rather than stopped
    /// existing.</summary>
    public void Sample(FlightModel m)
    {
        var s = m.Stats;
        float speed = m.Speed;
        float loadCap = m.LiftCapAt(speed);
        float demand = Mathf.Min(m.LoadFactorDemand, LiftGMax);
        float bank = Mathf.RadToDeg(Mathf.Acos(Mathf.Clamp(m.Attitude.Y.Dot(Vector3.Up), -1f, 1f)));

        _row.Steps++;
        _row.GDemand = Mathf.Max(_row.GDemand, m.LoadFactorDemand);
        _row.ClRatio = Mathf.Max(_row.ClRatio, loadCap > 1e-6f ? demand / loadCap : 0f);
        _row.Window = Mathf.Min(_row.Window, m.CommandLimit);
        _row.SpeedOverStall = Mathf.Min(_row.SpeedOverStall,
            m.StallSpeed > 1e-3f ? speed / m.StallSpeed : 99f);
        _row.AltM = Mathf.Max(_row.AltM, m.Position.Y);
        _row.GUpMin = Mathf.Min(_row.GUpMin, m.BodyUpLoadFactor);
        _row.GUpMax = Mathf.Max(_row.GUpMax, m.BodyUpLoadFactor);
        _row.SpeedOverDive = Mathf.Max(_row.SpeedOverDive,
            s.FdSpeed > 1e-3f ? speed / (MaxDiveSpeedFrac * s.FdSpeed) : 0f);
        _row.GLoStart = s.LowGStart;
        _row.GHiStart = s.HighGStart;

        Hit("dense-band", m.Position.Y <= BandBoundaryM);
        Hit("thin-band", m.Position.Y > BandBoundaryM);
        Hit("low-speed-ramp", m.RollAuthorityAt(speed) < 0.999f);
        Hit("pitch-fade", m.PitchAuthorityAt(speed) < m.RollAuthorityAt(speed) - 1e-4f);
        Hit("aoa-window", m.CommandLimit < 0.999f);
        Hit("g-ramp", m.BodyUpLoadFactor > s.HighGStart || m.BodyUpLoadFactor < s.LowGStart);
        Hit("g-clamp", m.LoadFactorDemand > LiftGMax);
        Hit("cl-ceiling", loadCap > 1e-6f && demand >= loadCap);
        Hit("stall", m.StallFlag > 0f);
        Hit("dive-cap", s.FdSpeed > 1e-3f && speed >= (MaxDiveSpeedFrac * s.FdSpeed) - 1e-3f);
        Hit("weathervane", m.Alpha > 0.01f);
        Hit("bank-coupling", bank > 1f);
        Hit("far-field", m.FarFieldPlant);
        Hit("boost", m.Boosting);
    }

    /// <summary>The margins line for the scenario just finished, and starts the next one. Empty
    /// when no step was sampled, so a row the probe computes without flying carries no line
    /// rather than a line of neutral values.</summary>
    public string Take()
    {
        var row = _row;
        _row = new Scenario();
        if (row.Steps == 0)
        {
            return "";
        }

        var c = CultureInfo.InvariantCulture;
        return string.Format(
            c,
            "Gdem {0:0.00}/9  C_L {1:0.00}  win {2:0.000}  V/Vs {3:0.00}  alt {4:0} m  "
            + "Gup {5:+0.00;-0.00}..{6:+0.00;-0.00} in [{7:0.0},{8:0.0}]  V/Vd {9:0.00}",
            row.GDemand, row.ClRatio, row.Window, row.SpeedOverStall, row.AltM,
            row.GUpMin, row.GUpMax, row.GLoStart, row.GHiStart, row.SpeedOverDive);
    }

    private void Hit(string branch, bool now)
    {
        if (now)
        {
            _reached.Add(branch);
        }
    }

    // One scenario's extremes. Each field is the closest the run came to one bounding term, so the
    // report prints a distance rather than a pass/fail.
    private sealed class Scenario
    {
        public int Steps;
        public float GDemand;
        public float ClRatio;
        public float Window = 1f;
        public float SpeedOverStall = 99f;
        public float AltM;
        public float GUpMin = float.MaxValue;
        public float GUpMax = float.MinValue;
        public float SpeedOverDive;
        public float GLoStart;
        public float GHiStart;
    }
}
