using System.Collections.Generic;
using System.IO;
using System.Linq;
using CSVM;
using CSVM.Flight.Ai;
using CSVM.Flight.Weapons;
using CSVM.Mech3;
using CSVM.Session.Campaign;
using Godot;
using Xunit;

namespace CSVM.Tests;

/// <summary>
/// The engine-free half of the campaign roster spawner (docs/org/aiPilot.md, docs/formats/
/// ai-rosters.md): the def resolution behind a block name, the net-versus-escort fork, the volume
/// order (net first, block over it, then the activation floor) and the leader lookup, over a
/// hand-authored def table and roster.
/// </summary>
public class CampaignRosterPlanTests
{
    private static VehicleDefs Defs => VehicleDefs.FromRoot(new List<object?>
    {
        "basic_airplane", Props("mode", "jet", "nodename", "firebrand"),
        "player_airplane", Props("kind_of", "basic_airplane"),
        "fury", Props("kind_of", "basic_airplane"),
        "pfury", Props("kind_of", "player_airplane", "nodename", "player_fury"),
        "wfury", Props("kind_of", "fury", "mode", "wingman"),
        "secfury", Props("kind_of", "fury"),
        "devastator", Props("kind_of", "basic_airplane", "nodename", "piratefighter"),
        "pdevastator", Props("kind_of", "player_airplane", "nodename", "player_pfighter"),
        "wingman", Props("kind_of", "devastator", "mode", "wingman", "nodename", "piratefighter"),
        "bswingman", Props("kind_of", "basic_airplane", "mode", "wingman", "nodename", "fury"),
        "blakepeace_2", Props("kind_of", "devastator"),
        "patrolboat", Props("mode", "ship"),
        "armytruck", Props("mode", "ground"),
    });

    private static List<AiNet> Nets => new()
    {
        Net(5, "Five", default),
        Net(7, "Seven", new AiVolumeSet(new AiVolume(3000f, 0f, 0f), default, new AiVolume(700f, 200f, -50f))),
    };

    [Fact]
    public void ABlockNameResolvesToItsDefByStrippingOrdinals()
    {
        var defs = Defs;
        Assert.Equal("wingman", defs.DefForBlock("wingman_1"));
        Assert.Equal("blakepeace_2", defs.DefForBlock("blakepeace_2_1"));
        Assert.Equal("patrolboat", defs.DefForBlock("patrolboat_eg0"));
        Assert.Null(defs.DefForBlock("nothing_1"));
    }

    [Fact]
    public void ModeResolvesThroughKindOfWithJetAtTheRoot()
    {
        var defs = Defs;
        Assert.Equal("wingman", defs.ModeOf("wingman"));
        Assert.Equal("jet", defs.ModeOf("secfury"));
        Assert.Equal("ship", defs.ModeOf("patrolboat"));
        Assert.Null(defs.ModeOf("nothing"));
    }

    [Fact]
    public void TheAirframeIsThePlayerTwinOfTheNearestAncestorOrOfTheModelName()
    {
        var defs = Defs;
        Assert.Equal(("player_pfighter", "devastator"), defs.AirframeFor("wingman"));
        Assert.Equal(("player_fury", "fury"), defs.AirframeFor("secfury"));
        // bswingman derives from nothing with a player twin; its model name maps instead.
        Assert.Equal(("player_fury", "fury"), defs.AirframeFor("bswingman"));
        Assert.Null(defs.AirframeFor("patrolboat"));
        Assert.Equal("devastator", defs.BaseDefForPlayerNode("player_pfighter"));
    }

    [Fact]
    public void ANetlessWingmanEscortsAndAnyNetDemotesIt()
    {
        var plan = CampaignRosterPlan.Build(new List<(string, List<object?>)>
        {
            ("player", Block(-1f, "")),
            ("wingman_1", Block(-1f, "player")),
            ("wingman_2", Block(-1f, "devastator_2")),
            ("wingman_4", Block(new List<object?> { 5f }, "player")),
            ("devastator_2", Block(5f, "")),
        }, Defs, Nets);

        Assert.Equal(new[] { "player" }, plan.Skipped.Select(s => s.Name).ToArray());
        var byName = plan.Spawns.ToDictionary(s => s.Name);
        Assert.True(byName["wingman_1"].Escorts);
        Assert.Null(byName["wingman_1"].Net);
        Assert.Equal("player", byName["wingman_1"].LeaderName);
        Assert.Equal("wingman", byName["wingman_1"].Mode);
        Assert.Equal("player_pfighter", byName["wingman_1"].PlaneNode);
        Assert.Equal("wingman", byName["wingman_1"].AiDef);

        Assert.True(byName["wingman_2"].Escorts);
        Assert.Equal("devastator_2", byName["wingman_2"].LeaderName);

        // A one-entry netids LIST is a net like a scalar is, and it demotes the wingman.
        Assert.False(byName["wingman_4"].Escorts);
        Assert.Equal(5, byName["wingman_4"].Net?.Id);

        Assert.False(byName["devastator_2"].Escorts);
        Assert.Equal(5, byName["devastator_2"].Net?.Id);
        Assert.Equal("jet", byName["devastator_2"].Mode);
    }

    [Fact]
    public void TheProfilesWingmanAirframeReplacesTheBlocksOwnForTheNamedBlock()
    {
        var fit = new LoadoutChoice();
        var plan = CampaignRosterPlan.Build(new List<(string, List<object?>)>
        {
            ("wingman_1", Block(-1f, "player")),
            ("wingman_2", Block(-1f, "wingman_1")),
        }, Defs, Nets, wingmanNode: "player_fury", wingmanFit: fit);

        var byName = plan.Spawns.ToDictionary(s => s.Name);
        Assert.Equal("player_fury", byName["wingman_1"].PlaneNode);
        Assert.Equal("wfury", byName["wingman_1"].AiDef);   // the w<plane> def keeps mode wingman
        Assert.True(byName["wingman_1"].Escorts);
        Assert.Same(fit, byName["wingman_1"].Fit);
        Assert.Equal("player_pfighter", byName["wingman_2"].PlaneNode);
        Assert.Null(byName["wingman_2"].Fit);
    }

    [Fact]
    public void ADefThatIsNoVariantOfItsAirframeFliesThePlainBaseDef()
    {
        var plan = CampaignRosterPlan.Build(new List<(string, List<object?>)>
        {
            ("bswingman_1", Block(-1f, "player")),
            ("armytruck_1", Block(5f, "")),
        }, Defs, Nets);

        var bs = Assert.Single(plan.Spawns);
        Assert.Equal("player_fury", bs.PlaneNode);
        Assert.Null(bs.AiDef);
        Assert.True(bs.Escorts);
        var skipped = Assert.Single(plan.Skipped);
        Assert.Equal("armytruck_1", skipped.Name);
    }

    [Fact]
    public void AShipBlockPlansASurfaceVehicleOnItsNet()
    {
        var fields = Block(5f, "");
        fields[21] = 1f;
        var plan = CampaignRosterPlan.Build(
            new List<(string, List<object?>)> { ("patrolboat_1", fields) }, Defs, Nets);

        Assert.Empty(plan.Skipped);
        var boat = Assert.Single(plan.Spawns);
        Assert.True(boat.Surface);
        Assert.Equal("patrolboat", boat.Def);
        Assert.Equal("patrolboat", boat.PlaneNode);
        Assert.Null(boat.AiDef);
        Assert.Equal("ship", boat.Mode);
        Assert.Equal(5, boat.Net?.Id);
        Assert.True(boat.Inert);
        Assert.False(boat.Escorts);
        Assert.Throws<System.ArgumentException>(() =>
            CampaignRosterPlan.SpawnFor(boat, Vector3.Zero, Vector3.Forward, AiPilot.HoldingCourse(Vector3.Zero, Vector3.Forward)));
    }

    [Fact]
    public void AGeneratorLabelNamingAShipBlockResolvesASurfaceLaunch()
    {
        var fields = Block(5f, "");
        fields[5] = 0f;
        var hull = CampaignRosterPlan.BuildGeneratorTemplate("patrolboat_eg0", fields, Defs, Nets);
        Assert.NotNull(hull);
        var templates = new Dictionary<string, RosterSpawnPlan> { ["Eshipg31_params"] = hull };

        Assert.Equal(GeneratorLaunch.Surface,
            CampaignRosterPlan.ResolveGeneratorLaunch(templates, "Eshipg31_params", out var resolved));
        Assert.Same(hull, resolved);
        Assert.Equal(GeneratorLaunch.Empty,
            CampaignRosterPlan.ResolveGeneratorLaunch(templates, "Earig32_params", out _));
    }

    [Fact]
    public void VolumesAreTheNetsFirstThenTheBlocksThenTheFloor()
    {
        var withOwn = Block(7f, "");
        withOwn[16] = 900f;   // the block's own return radius outranks the net's 700
        var plan = CampaignRosterPlan.Build(new List<(string, List<object?>)>
        {
            ("secfury_1", Block(7f, "")),
            ("secfury_2", withOwn),
            ("secfury_3", Block(99f, "")),
        }, Defs, Nets);

        var byName = plan.Spawns.ToDictionary(s => s.Name);
        Assert.Equal(3000f, byName["secfury_1"].Volumes.Activation.Radius);
        Assert.Equal(700f, byName["secfury_1"].Volumes.Return.Radius);
        Assert.Equal(-50f, byName["secfury_1"].Volumes.Return.Lower);
        Assert.Equal(900f, byName["secfury_2"].Volumes.Return.Radius);
        Assert.Equal(3000f, byName["secfury_2"].Volumes.Activation.Radius);
        Assert.Null(byName["secfury_3"].Net);
        Assert.Equal(99, byName["secfury_3"].MissingNetId);
        Assert.False(byName["secfury_3"].Volumes.IsAuthored);

        var machine = new AiModeMachine(new System.Random(1)) { ActivationRange = 1000f, ReturnRange = 1200f };
        CampaignRosterPlan.ApplyVolumes(machine, byName["secfury_1"].Volumes, 2000f);
        Assert.Equal(3000f, machine.ActivationRange);
        Assert.Equal(700f, machine.ReturnRange);
        Assert.Equal(2000f, machine.AttackRange);   // unauthored: the def's own stays

        var floored = new AiModeMachine(new System.Random(1)) { ActivationRange = 1000f };
        CampaignRosterPlan.ApplyVolumes(floored, byName["secfury_3"].Volumes, 2000f);
        Assert.Equal(2000f, floored.ActivationRange);
    }

    [Fact]
    public void DeactivatedAndTheOtherSpawnSlotsRead()
    {
        var fields = Block(5f, "player");
        fields[21] = 1f;
        fields[31] = 1500f;
        fields[32] = 2048f;
        fields[40] = "pp2";
        fields[65] = 24f;
        var plan = CampaignRosterPlan.Build(new List<(string, List<object?>)> { ("secfury_1", fields) }, Defs, Nets);
        var spawn = Assert.Single(plan.Spawns);
        Assert.True(spawn.Inert);
        Assert.Equal(1500f, spawn.PrefEngageAlt);
        Assert.Equal(2048L, spawn.SignatureMask);
        Assert.Equal("pp2", spawn.TaxiPath);
        Assert.Equal(24, spawn.AccentId);
        Assert.Equal(2, spawn.Team);
        Assert.Equal(3, spawn.Group);
        Assert.Equal(new Vector3(10f, 20f, 30f), spawn.Position);
        Assert.Equal(90f, spawn.YawDeg);
        Assert.Equal("player", spawn.LeaderName);   // a jet's assignment, not a leader
        Assert.False(spawn.Escorts);
    }

    [Fact]
    public void DisabledGeneratorTemplate_IsNotAnInitialRosterSpawn()
    {
        var fields = Block(5f, "player");
        fields[5] = 0f;

        var plan = CampaignRosterPlan.Build(
            new List<(string, List<object?>)> { ("secfury_1", fields) }, Defs, Nets);

        Assert.Empty(plan.Spawns);
        Assert.Equal(("secfury_1", "enabled 0 generator template"), Assert.Single(plan.Skipped));
    }

    [ExtractedDataFact]
    public void Cm04GeneratorParameterSelectsTheDisabledPeacemakerTemplate()
    {
        string mission = SessionPaths.MissionZrdr(TestData.DataRoot!, "C3", "M03");
        var template = Assert.Single(AiSkills.LoadGeneratorRoster(mission),
            block => block.Parameter.Equals("BarracudaPlanes", System.StringComparison.OrdinalIgnoreCase));
        string zrdr = SessionPaths.PreferUnzipped(Path.Combine(TestData.ExtractedRoot!, "zrdr.zip"));
        var plan = CampaignRosterPlan.BuildGeneratorTemplate(template.Name, template.Fields,
            VehicleDefs.Load(zrdr), AiNets.Load(SessionPaths.ChapterZrdr(TestData.DataRoot!, "C3")));

        Assert.NotNull(plan);
        Assert.Equal("britpeace_5", plan.Name);
        Assert.Equal("player_peacemaker", plan.PlaneNode);
        Assert.Equal(2, plan.Team);
        Assert.Equal(3, plan.Group);
    }

    [Fact]
    public void TheLeaderLookupNamesThePlayerOrASpawnedBlock()
    {
        var player = new object();
        var dev = new object();
        var rigs = new Dictionary<string, object> { ["devastator_2"] = dev };
        Assert.Same(player, CampaignRosterPlan.ResolveLeader("player", rigs, player));
        Assert.Same(dev, CampaignRosterPlan.ResolveLeader("devastator_2", rigs, player));
        Assert.Null(CampaignRosterPlan.ResolveLeader("devastator_9", rigs, player));
        Assert.Null(CampaignRosterPlan.ResolveLeader(null, rigs, player));
    }

    [Fact]
    public void NetRecordVolumesReadInTheDecodedOrder()
    {
        var record = new List<object?>
        {
            null, 10f, 3000f, 0f, 0f, 100f, 100f, -100f, 700f, 200f, -50f,
        };
        var set = AiVolumeSet.FromNetRecord(record);
        Assert.Equal(new AiVolume(3000f, 0f, 0f), set.Activation);
        Assert.Equal(new AiVolume(100f, 100f, -100f), set.Attack);
        Assert.Equal(new AiVolume(700f, 200f, -50f), set.Return);
        Assert.False(AiVolumeSet.FromNetRecord(new List<object?> { null, 10f }).IsAuthored);
    }

    // The shipped shape: each key followed by a one-element value LIST.
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

    private static AiNet Net(int id, string name, AiVolumeSet volumes) => new()
    {
        Id = id,
        Name = name,
        Nodes = new[] { new AiNetNode(Vector3.Zero, System.Array.Empty<float>()), new AiNetNode(Vector3.Right, System.Array.Empty<float>()) },
        Edges = new[] { (0, 1) },
        Volumes = volumes,
    };

    // An 81-slot block: netids, position, yaw 90, team 2, group 3, enabled, primary_target, and
    // -1/0 everywhere else, the shape the shipped rosters author.
    private static List<object?> Block(object? netIds, string primaryTarget)
    {
        var fields = new List<object?>(81);
        for (int i = 0; i < 81; i++)
        {
            fields.Add(i is >= 8 and <= 19 ? 0f : -1f);
        }
        fields[0] = netIds;
        fields[1] = new List<object?> { 10f, 20f, 30f };
        fields[2] = 90f;
        fields[3] = 2f;
        fields[4] = 3f;
        fields[5] = 1f;
        fields[6] = primaryTarget;
        fields[7] = 0f;
        fields[20] = "";
        fields[21] = 0f;
        fields[33] = null;
        fields[40] = 0f;
        return fields;
    }
}
