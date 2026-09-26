using System;
using System.Collections.Generic;
using CSVM.Flight.Airframe;
using CSVM.Flight.Weapons;
using CSVM.Mech3;
using Xunit;

namespace CSVM.Tests;

/// <summary>
/// The F18 zeppelin kill arithmetic, engine-free. Pins the polarity: <c>num_healthy_required</c>
/// counts survivors, not destroyed count (see <c>docs/formats/mission-entities.md</c> "The kill
/// threshold counts survivors"). Also pins literal per-entry counting over a duplicated healthy
/// entry, the engine recount feeding <c>ZeppelinMotion.AliveEngines</c>, the DAMAGES_ZEPPELIN
/// gasbag gate, and the record-stage crossing helper.
/// </summary>
public class ZeppelinDamageTests
{
    [Fact]
    public void PolarityIsSurvivorsNotDestroyCount()
    {
        // 6 gasbags, required 4 (the C1/M04 piratezep shape).
        var damage = new ZeppelinDamage(Def(
            healthy: new[] { "gasbag1", "gasbag2", "gasbag3", "gasbag4", "gasbag5", "gasbag6" },
            required: 4));

        // 2 destroyed → 4 survive: 4 >= 4 lives.
        var dead2 = Dead("gasbag1", "gasbag2");
        Assert.Equal(4, damage.Survivors(n => !dead2.Contains(n)));
        Assert.False(damage.IsDead(n => !dead2.Contains(n)));

        // 3 destroyed → 3 survive: 3 < 4 DIES. ⚠ The inverse (destroy-count) reading would
        // keep it alive here until 4 were destroyed, the immortal-zeppelin bug.
        var dead3 = Dead("gasbag1", "gasbag2", "gasbag3");
        Assert.Equal(3, damage.Survivors(n => !dead3.Contains(n)));
        Assert.True(damage.IsDead(n => !dead3.Contains(n)));
    }

    [Fact]
    public void RequiredIsTheReadersValueUnmodified()
    {
        // The reader already applied the decoded default (1) and clamp; the aggregator must
        // consume it verbatim: required 1 means only a total wipe kills.
        var damage = new ZeppelinDamage(Def(healthy: new[] { "a", "b" }, required: 1));
        var oneDead = Dead("a");
        Assert.False(damage.IsDead(n => !oneDead.Contains(n)));
        var allDead = Dead("a", "b");
        Assert.True(damage.IsDead(n => !allDead.Contains(n)));
    }

    [Fact]
    public void DuplicateHealthyEntriesCountPerEntry()
    {
        // C5/M01's shipped shape: gasbag5 listed twice, required 4, the engine walks the
        // entry list, so killing the duplicated node removes TWO survivors at once.
        var damage = new ZeppelinDamage(Def(
            healthy: new[] { "gasbag1", "gasbag2", "gasbag3", "gasbag4", "gasbag5", "gasbag5" },
            required: 4));
        var dead = Dead("gasbag5");
        Assert.Equal(4, damage.Survivors(n => !dead.Contains(n)));
        Assert.False(damage.IsDead(n => !dead.Contains(n)));

        var dead2 = Dead("gasbag5", "gasbag1");
        Assert.Equal(3, damage.Survivors(n => !dead2.Contains(n)));
        Assert.True(damage.IsDead(n => !dead2.Contains(n)));
    }

    [Fact]
    public void EngineRecountFeedsTheSqrtSeam()
    {
        var damage = new ZeppelinDamage(Def(
            healthy: new[] { "gasbag1" }, required: 1,
            engines: new[] { "leng11", "leng12", "reng11", "reng12" }));
        Assert.Equal(4, damage.AliveEngines(_ => true));
        var dead = Dead("leng11", "reng12");
        Assert.Equal(2, damage.AliveEngines(n => !dead.Contains(n)));
    }

    [Fact]
    public void OnlyADamagesZeppelinWeaponMayHurtAGasbag()
    {
        Assert.True(ZeppelinDamage.MayDamageGasbag(new WeaponDef { DamagesZeppelin = true }));
        Assert.False(ZeppelinDamage.MayDamageGasbag(new WeaponDef { DamagesZeppelin = false }));
    }

    [Fact]
    public void CannonStagesFireOncePerCrossedThreshold()
    {
        var stages = new (float Fraction, string Anim)[] { (0.6f, "60_anim"), (0.3f, "30_anim") };
        int fired = 0;

        // Above the first threshold: nothing.
        Assert.Empty(ZeppelinDamage.CrossedStages(stages, 0.9f, ref fired));

        // Crossing 0.6: the first stage, once.
        Assert.Equal(new[] { "60_anim" }, ZeppelinDamage.CrossedStages(stages, 0.5f, ref fired));
        Assert.Empty(ZeppelinDamage.CrossedStages(stages, 0.5f, ref fired));

        // Crossing 0.3: the second.
        Assert.Equal(new[] { "30_anim" }, ZeppelinDamage.CrossedStages(stages, 0.1f, ref fired));
        Assert.Empty(ZeppelinDamage.CrossedStages(stages, 0.0f, ref fired));
    }

    [Fact]
    public void ABigHitFiresEveryCrossedStage()
    {
        // injure_anims semantics (docs/org/vehicleDamage.md): every entry whose threshold the
        // fraction has crossed starts, so one 1.0 → 0.1 hit plays both stage anims.
        var stages = new (float Fraction, string Anim)[] { (0.6f, "60_anim"), (0.3f, "30_anim") };
        int fired = 0;
        Assert.Equal(new[] { "60_anim", "30_anim" },
            ZeppelinDamage.CrossedStages(stages, 0.1f, ref fired));
    }

    private static HashSet<string> Dead(params string[] nodes) =>
        new(nodes, StringComparer.OrdinalIgnoreCase);

    private static ZeppelinDef Def(string[] healthy, int required, string[]? engines = null)
    {
        var zones = new List<ZeppelinHealthyZone>();
        foreach (var node in healthy)
        {
            zones.Add(new ZeppelinHealthyZone(node, "panels"));
        }
        return new ZeppelinDef
        {
            Node = "testzep",
            Position = Godot.Vector3.Zero,
            Net = "TestNet",
            Targets = Array.Empty<string>(),
            Healthy = zones,
            NumHealthyRequired = required,
            Engines = engines ?? Array.Empty<string>(),
            Gasbags = Array.Empty<ZeppelinGasbag>(),
            LeftCannons = Array.Empty<ZeppelinCannon>(),
            RightCannons = Array.Empty<ZeppelinCannon>(),
            CannonHealth = Array.Empty<ZeppelinCannonHealth>(),
        };
    }
}
