using System.Collections.Generic;
using System.Linq;
using CSVM.Flight.Ai;
using CSVM.Flight.Airframe;
using CSVM.Mech3;
using CSVM.Session.Campaign;
using Godot;
using Xunit;

namespace CSVM.Tests;

/// <summary>
/// The roster's own durability overrides (docs/org/vehicleDamage.md "Where the numbers come from
/// at spawn", step 2): slot 7 <c>init_health</c>, applied only when authored greater than zero,
/// and slot 66 <c>armor</c>, applied whenever authored zero or greater. Pins the two gates, the
/// absent-slot case (blocks are not fixed-width; 33 of 414 stop at 66 fields), and the seam that
/// carries both from the roster block through <see cref="RosterSpawnPlan"/> and
/// <see cref="CampaignRosterPlan.SpawnFor"/> to <see cref="Session.Roster.AiSpawn"/>, where
/// <see cref="PlaneStats.WithRosterDurability"/> applies them before the difficulty scale.
/// </summary>
public class RosterDurabilityOverrideTests
{
    private static VehicleDefs Defs => VehicleDefs.FromRoot(new List<object?>
    {
        "basic_airplane", Props("mode", "jet", "nodename", "firebrand"),
        "player_airplane", Props("kind_of", "basic_airplane"),
        "fury", Props("kind_of", "basic_airplane"),
        "pfury", Props("kind_of", "player_airplane", "nodename", "player_fury"),
        "secfury", Props("kind_of", "fury"),
    });

    private static List<AiNet> Nets => new()
    {
        new AiNet
        {
            Id = 5,
            Name = "Five",
            Nodes = new[] { new AiNetNode(Vector3.Zero, System.Array.Empty<float>()), new AiNetNode(Vector3.Right, System.Array.Empty<float>()) },
            Edges = new[] { (0, 1) },
        },
    };

    // AiSkills.RosterInitHealth: the ">0" gate.
    [Fact]
    public void InitHealthReadsOnlyWhenAuthoredGreaterThanZero()
    {
        Assert.Equal(216f, AiSkills.RosterInitHealth(Block(216f, -1f)));
        Assert.Null(AiSkills.RosterInitHealth(Block(0f, -1f)));    // "use the airframe default"
        Assert.Null(AiSkills.RosterInitHealth(Block(-1f, -1f)));   // unset marker
        Assert.Null(AiSkills.RosterInitHealth(new List<object?>()));  // short block, slot absent
    }

    // AiSkills.RosterArmor: the ">=0" gate, which differs from init_health's. Zero is a real
    // override here (no shipped hostile authors it, but the reader must not special-case it away).
    [Fact]
    public void ArmorReadsWhenAuthoredZeroOrGreaterAndTreatsAMissingSlotAsUnset()
    {
        Assert.Equal(90f, AiSkills.RosterArmor(Block(-1f, 90f)));
        Assert.Equal(0f, AiSkills.RosterArmor(Block(-1f, 0f)));
        Assert.Null(AiSkills.RosterArmor(Block(-1f, -1f)));

        // A block stopping at 66 fields (indices 0-65) carries no slot 66 at all: absent, not zero.
        var shortBlock = Block(-1f, 90f);
        shortBlock.RemoveRange(66, shortBlock.Count - 66);
        Assert.Equal(66, shortBlock.Count);
        Assert.Null(AiSkills.RosterArmor(shortBlock));
    }

    // The seam: CampaignRosterPlan.Build reads both slots into RosterSpawnPlan, and SpawnFor
    // forwards them onto the AiSpawn record the assembler consumes.
    [Fact]
    public void BothOverridesReachThePlannedBlockAndTheSpawnRecord()
    {
        var withOverrides = Block(126f, 126f);
        var absentArmor = Block(1f, -1f);
        absentArmor.RemoveRange(66, absentArmor.Count - 66);

        var plan = CampaignRosterPlan.Build(new List<(string, List<object?>)>
        {
            ("secfury_1", withOverrides),
            ("secfury_2", absentArmor),
        }, Defs, Nets);

        var byName = plan.Spawns.ToDictionary(s => s.Name);
        Assert.Equal(126f, byName["secfury_1"].InitHealth);
        Assert.Equal(126f, byName["secfury_1"].Armor);
        Assert.Equal(1f, byName["secfury_2"].InitHealth);
        Assert.Null(byName["secfury_2"].Armor);   // absent slot, not zero

        var spawn = CampaignRosterPlan.SpawnFor(byName["secfury_1"], Vector3.Zero, Vector3.Forward,
            AiPilot.HoldingCourse(Vector3.Zero, Vector3.Forward));
        Assert.Equal(126f, spawn.InitHealth);
        Assert.Equal(126f, spawn.Armor);

        var noArmorSpawn = CampaignRosterPlan.SpawnFor(byName["secfury_2"], Vector3.Zero, Vector3.Forward,
            AiPilot.HoldingCourse(Vector3.Zero, Vector3.Forward));
        Assert.Equal(1f, noArmorSpawn.InitHealth);
        Assert.Null(noArmorSpawn.Armor);
    }

    // PlaneStats.WithRosterDurability: replaces the whole-vehicle pool outright, independently per
    // argument, and stands BEFORE the difficulty scale multiplies whatever it leaves behind.
    [Fact]
    public void TheOverrideReplacesTheWholeVehiclePoolAndStandsBeforeTheDifficultyScale()
    {
        var authored = new PlaneStats { FdSpeed = 100f, EnginePower = 0.5f };
        authored.DestroyableParts.Add(new DestroyablePart { Name = "nose", MaxHp = 20f, MaxArmor = 20f });

        var overridden = authored.WithRosterDurability(126f, 90f);
        Assert.Equal(126f, overridden.VehicleHealth);
        Assert.Equal(90f, overridden.VehicleArmor);
        // The shared cached stats are never mutated in place.
        Assert.Null(authored.VehicleHealth);

        var scaled = overridden.WithEnemyDurability(1.25f);
        Assert.Equal(157.5f, scaled.VehicleHealth!.Value, 3);
        Assert.Equal(112.5f, scaled.VehicleArmor!.Value, 3);
    }

    // Only one of the two authored: the other pool stays null so the later steps still resolve it
    // from the parts sum, exactly the way an unset roster slot behaves at the engine's own spawn.
    [Fact]
    public void AnUnsetArgumentLeavesThatPoolNullRatherThanInventingAZero()
    {
        var authored = new PlaneStats { FdSpeed = 100f, EnginePower = 0.5f };

        var healthOnly = authored.WithRosterDurability(216f, null);
        Assert.Equal(216f, healthOnly.VehicleHealth);
        Assert.Null(healthOnly.VehicleArmor);

        var armorOnly = authored.WithRosterDurability(null, 90f);
        Assert.Null(armorOnly.VehicleHealth);
        Assert.Equal(90f, armorOnly.VehicleArmor);
    }

    // Neither authored: no copy at all, matching WithEnemyDurability's own unscaled-tier shortcut.
    [Fact]
    public void NeitherOverrideReturnsTheSameObject()
    {
        var authored = new PlaneStats { FdSpeed = 100f };
        Assert.Same(authored, authored.WithRosterDurability(null, null));
    }

    private static List<object?> Props(params object?[] pairs)
    {
        var list = new List<object?>();
        for (int i = 0; i + 1 < pairs.Length; i += 2)
        {
            list.Add(pairs[i]);
            list.Add(new List<object?> { pairs[i + 1] });
        }
        return list;
    }

    // An 81-slot block, the shape the shipped rosters author: netids, position, yaw, team, group,
    // enabled, primary_target, then init_health at 7 and -1/0 everywhere else up to armor at 66.
    private static List<object?> Block(float initHealth, float armor)
    {
        var fields = new List<object?>(81);
        for (int i = 0; i < 81; i++)
        {
            fields.Add(i is >= 8 and <= 19 ? 0f : -1f);
        }
        fields[0] = 5f;
        fields[1] = new List<object?> { 10f, 20f, 30f };
        fields[2] = 90f;
        fields[3] = 2f;
        fields[4] = 3f;
        fields[5] = 1f;
        fields[6] = "";
        fields[7] = initHealth;
        fields[20] = "";
        fields[21] = 0f;
        fields[33] = null;
        fields[40] = 0f;
        fields[66] = armor;
        return fields;
    }
}
