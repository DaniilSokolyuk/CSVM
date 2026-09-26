using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CSVM.Effects;
using CSVM.Flight.Airframe;
using CSVM.Flight.Camera;
using CSVM.Flight.Weapons;
using CSVM.Mech3;
using CSVM.Mech3.Anim;
using CSVM.Session.Roster;
using CSVM.Utils;
using Godot;

namespace CSVM.Testing;

internal static class SuiteConstants
{
    internal const int PlayerAirframes = 11;
    internal const int WeaponDefCount = 48;

    // B4's scan fixture (aim-assist): a shooter at the origin on player 0's team, nose down world
    // -Z, firing a 500 m/s round with a 1000 m RANGE through the stock guns' 6° cone. Every gate
    // case moves ONE thing off this baseline, so a rejection can only be the gate under test.
    internal const float ScanSpeed = 500f;
    internal const float ScanRange = 1000f;
    internal const float ScanConeDeg = 6f; // the shipped CANNON_SPREAD, asserted in AimAssistShippedData

    // The fixed step `ordnance-burst-timeline` drives its three bursts at.
    // Deliberately FOUR TIMES finer than SequenceRunner.AnimFrame: the authored gaps
    // under test go down to 0.01 s, which one 1/60 s step cannot resolve at all, and nothing in
    // those three definitions is denominated in animation frames (none of them carries a
    // `LOOP`, so the AnimFrame floor is never reached). A finer step makes every authored
    // instant a real measurement instead of a rounding.
    internal const float BurstDt = 1f / 240f;

    // How far a burst's dispatch may sit from its authored instant: one step because the stamp is
    // taken after the Advance that fired it, one per level of CALL the row sits under, and one for a
    // gate landing a step late on binary float. Every authored gap in the three definitions is wider
    // than this bar two, and those two are carried by the rows behind them.
    internal const float BurstSlack = 6f * BurstDt;

    // How long each burst is driven for, past the last authored event of its longest
    // lane, with room for the lag above. Sonic is the long one: its `sonic_growlight` ends at
    // an authored 3.2 s.
    internal const float BurstSeconds = 3.5f;

    // The burst suite's instance TTL, an order of magnitude past the longest burst, so
    // the bound never truncates a timeline. Explicitly NOT `--effects-test`'s 0.3 s, which
    // exists for the gun path and would cut the 1.2 s wash off at 0.3 s while every remaining
    // assertion still passed.
    internal const float BurstTtl = 32f;

    // Gun-group slots Loadout.ForRig seats on any airframe, the
    // weapon bench fires every gun from all of them, so a drop here would quietly shrink its
    // coverage without changing the 48/48 line.
    internal const int RigGunGroups = 4;

    // The vertical span the ground probe casts through, past every chapter's terrain either way, and
    // how far either side of a found surface its from-below companion starts and ends. The standoff
    // is short so the second ray meets the surface the first one found, not a neighbouring tile.
    internal const float GroundProbeCeilingM = 4000f;

    internal const float GroundProbeFloorM = -4000f;

    internal const float GroundProbeStandoffM = 50f;

    // Destructible instances / distinct node groups per chapter, at each chapter's default mission.
    // Authored-inactive placed roots are staged hidden and therefore contribute to this registry.
    // Instances exceed node groups where a reader wildcard def and its compiled per-instance twin
    // bind the same nodes. Both columns sit far below plain NAME matching because a compiled def
    // binds the one instance its symbol table names (AnimRuntime.Anchors), and because a file no
    // mission list names registers nothing in either scope (AnimProgram), holding C2 and C5 down.
    internal static readonly (string Chapter, int Instances, int Anchors)[] Census =
    {
        ("C1", 174, 105),
        ("C1B", 28, 28),
        ("C1C", 28, 28),
        ("C2", 176, 109),
        ("C2B", 28, 28),
        ("C3", 209, 133),
        ("C4", 92, 67),
        ("C5", 157, 93),
    };

    // Partition cells whose centre answers a downward ray, and how many of those also answer one
    // from below, per chapter. The first column is every cell in every chapter and must stay that
    // way: a drop is ground an aircraft would fall through. The second is the two-sided remainder
    // the data itself authors, C4's one cell and C5's 23; it read 144/144, 256/256 everywhere while
    // every world collider was forced two-sided.
    internal static readonly (string Chapter, int FromAbove, int FromBelow)[] GroundCensus =
    {
        ("C1", 144, 0),
        ("C1B", 144, 0),
        ("C1C", 144, 0),
        ("C2", 144, 0),
        ("C2B", 144, 0),
        ("C3", 256, 0),
        ("C4", 144, 1),
        ("C5", 256, 23),
    };

    // C1 textures spanning the three alpha classes the flatten must leave alone: opaque,
    // hard cutout, and the soft overlays the builder alpha-blends.
    internal static readonly string[] DropInSamples =
    {
        "lkzepskin", "grass1", "cloudlayer", "sky1", // no alpha channel
        "firtree1", "bush1",                          // hard cutouts
        "abld_shadow",                                // soft baked shadow overlay
    };

    /// <summary>The aircraft resources a roster-building suite hands a flight roster, every field
    /// read off the test context. Weapon messages and camera parameters are optional parameters,
    /// since only some suites need them. Passing null leaves the field exactly as an omitted
    /// initializer would, so no suite pays for a load it never reads.</summary>
    internal static AircraftAssemblyResources AircraftResources(
        TestContext ctx, GameZ planesGamez, TextureArchive textures,
        Messages? weaponMessages = null, Func<string, CamParams>? camParamsFor = null) =>
        new()
        {
            PlanesGamez = planesGamez,
            StatsFor = plane => PlaneStats.Load(ctx.ZrdrPath, plane),
            AiStatsFor = (plane, aiDef) => PlaneStats.LoadForAi(ctx.ZrdrPath, plane, aiDef),
            CamParamsFor = camParamsFor!,
            PaintRng = new RandomNumberGenerator(),
            ZrdrPath = ctx.ZrdrPath,
            StockLoadouts = StockLoadouts.Load(),
            WeaponDefs = WeaponDefs.Load(ctx.ZrdrPath, null),
            WeaponMessages = weaponMessages!,
            Textures = textures,
            Shakes = ShakeDefs.Load(ctx.ZrdrPath),
        };
}
