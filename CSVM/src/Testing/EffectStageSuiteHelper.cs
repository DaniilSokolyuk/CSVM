using System;
using System.Collections.Generic;
using System.Linq;
using CSVM.Effects;
using CSVM.Mech3;
using CSVM.Mech3.Anim;
using CSVM.Session.World;
using Godot;

namespace CSVM.Testing;

internal static class EffectStageSuiteHelper
{
    /// <summary><paramref name="slots"/> stages the roots in that many pool copies, the shape the
    /// real world-effects stage is built in (<c>WorldEffectsFactory</c>, sized by
    /// <c>effect_pools.json</c>): a suite that asks whether overlapping calls keep their own sites
    /// needs the pool the session ships, not one copy.</summary>
    internal static void WithEffectStage(TestContext ctx, TestWorld world, string animName,
        IEnumerable<string> rootNames, Action<Node3D, AnimRuntime, Vector3> body,
        IEmitterFactory? factory = null, int slots = 1)
    {
        var stage = new Node3D { Name = $"EffectStage_{animName}" };
        int built = 0;
        for (int slot = 0; slot < slots; slot++)
        {
            var pool = new Node3D { Name = $"pool{slot}" };
            pool.SetMeta(AnimRuntime.PoolSlotMeta, slot);
            stage.AddChild(pool);
            built += WorldEffectsFactory.BuildEffectStage(world.Gamez, world.Session.Builder.Scene, pool, rootNames);
            foreach (var child in pool.GetChildren())
                if (child is Node3D root)
                    root.Visible = false;
        }
        ctx.Check(built == rootNames.Count() * slots,
            $"{animName}: staged {built}/{rootNames.Count() * slots} template root(s) over {slots} slot(s)");

        var runtime = AnimRuntime.ForEffects(
            AnimRuntime.NewTemplateStage(pooled: true, shown: true, placesCalled: true),
            1, factory ?? new CountingEmitterFactory(), false, 32f, () => ctx.Camera.GlobalPosition);
        runtime.ManualAdvance = true;
        ctx.Host.AddChild(stage);
        ctx.Host.AddChild(runtime);
        try
        {
            runtime.Bind(stage, world.Session.Program.Subset(animName));
            body(stage, runtime, ctx.Camera.GlobalPosition);
        }
        finally
        {
            runtime.Free();
            stage.Free();
        }
    }
}
