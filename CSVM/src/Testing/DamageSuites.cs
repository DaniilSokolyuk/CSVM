using System.Collections.Generic;
using System.IO;
using System.Linq;
using CSVM.Flight.Ai;
using CSVM.Flight.Airframe;
using CSVM.Flight.Camera;
using CSVM.Flight.Hud;
using CSVM.Flight.Weapons;
using CSVM.Mech3;
using CSVM.Session.Roster;
using CSVM.Tooling;
using Godot;

namespace CSVM.Testing;

/// <summary>Suites asserting the damage model: spending armor and health, and the injure
/// staging those ledgers fire.</summary>
internal static class DamageSuites
{
    [Suite("damage-stages", "each DAMAGE_SEQUENCE def fires its stage effects across an HP sweep")]
    internal static void DamageStages(TestContext ctx)
    {
        ctx.WithWorld(ctx.Chapter, collision: false, world =>
        {
            var r = Probes.Damage(world.Runtime, ctx.Chapter, "", damageHd: 0f);
            ctx.WriteArtifact($"test-damage-stages-{ctx.Chapter}.txt", r.Text);
            ctx.Check(r.Rows.Count > 0, $"chapter has DAMAGE_SEQUENCE defs chapter={ctx.Chapter} rows={r.Rows.Count}");
            foreach (var row in r.Rows)
            {
                ctx.Check(row.Resolved, $"deep descendant resolves back to its destructible def={row.Def}");
                ctx.Check(row.StagesFired > 0, $"HP sweep fires a stage effect def={row.Def} stages={row.StagesFired}");
            }
            ctx.Note($"{r.Summary}");
        });
    }

    [Suite("damage-hd", "weapon hits destroy, swap, drop colliders, and survive destroy→reset→destroy")]
    internal static void DamageHd(TestContext ctx)
    {
        // Collision forced on: the collider census measures which destructible geometry is solid
        // and whether the death removes it, and a world built without collision censuses zero.
        ctx.WithWorld(ctx.Chapter, collision: true, world =>
        {
            var r = Probes.Damage(world.Runtime, ctx.Chapter, "", damageHd: 25f);
            ctx.WriteArtifact($"test-damage-hd-{ctx.Chapter}.txt", r.Text);
            ctx.Check(r.Rows.Count > 0, $"chapter has destructibles chapter={ctx.Chapter} rows={r.Rows.Count}");
            int destroyed = 0;
            foreach (var row in r.Rows)
            {
                ctx.Check(row.Resolved, $"deep descendant resolves back to its destructible def={row.Def}");
                ctx.Check(row.Destroyed, $"enough weapon hits destroy it def={row.Def} hits={row.Hits}");
                if (!row.Destroyed)
                {
                    continue;
                }
                destroyed++;
                ctx.Check(row.ResetHealthy == true, $"reset restores it def={row.Def}");
                ctx.Check(row.RekillMatched == true, $"rekill takes the same hits def={row.Def} hits={row.Hits}");
            }
            ctx.Same(r.Rows.Count, destroyed, $"destructibles destroyed by weapon hits");
            ctx.Note($"{r.Summary}");
            ctx.Note($"colliders world={r.CollidableMeshes} swept={r.Rows.Count} capped={r.Capped}");
        });
    }

    // ---- a new panel's tear must not steal a live panel's template copy ------------------------

    // The crash rig's damage-stage templates are pooled, and a relocating CALL_ANIMATION from a NEW
    // anchor takes its own copy instead of teleporting the one a previous anchor's burst is still
    // flying on. Reproduces the shipped shape: two authored pdpanelN defs each calling gimmeflakes at
    // their own pdpN, on a runtime carrying the crash rig's role flags plus the pool. Without the pool
    // the second call restarts the single shared planeflakes root mid-flight, which is the "panels fly
    // away repeatedly, and from the wrong site" symptom.
    [Suite("damage-template-pool",
        "a second panel's tear takes its own pooled gimmeflakes copy and leaves the first burst flying at its site (BL-288)")]
    internal static void DamageTemplatePool(TestContext ctx)
    {
        ctx.WithWorld(ctx.Chapter, collision: false, world =>
        {
            var stage = new Node3D { Name = "DamagePoolStage" };
            var pdp5 = PoolAnchorNode("pdp5", new Vector3(-10, 0, 0));
            var pdp4 = PoolAnchorNode("pdp4", new Vector3(10, 0, 0));
            stage.AddChild(pdp5);
            stage.AddChild(pdp4);
            var copies = new List<Node3D>();
            for (int slot = 0; slot < 2; slot++)
            {
                var pool = new Node3D { Name = $"pool{slot}" };
                pool.SetMeta(AnimRuntime.PoolSlotMeta, slot);
                stage.AddChild(pool);
                int built = Session.World.WorldEffectsFactory.BuildEffectStage(world.Gamez,
                    world.Session.Builder.Scene, pool, new[] { "planeflakes" });
                ctx.Check(built == 1, $"slot {slot} staged its planeflakes copy");
                foreach (var child in pool.GetChildren())
                {
                    if (child is Node3D copy)
                    {
                        copies.Add(copy);
                    }
                }
            }

            var runtime = new AnimRuntime(
                Session.World.WorldEffectsFactory.NewCrashTemplateStage())
            {
                AutoStart = false,
                ManualAdvance = true,
                SoundHandledElsewhere = true,
                EmitterFactory = new CountingEmitterFactory(),
                NameResolveFallback = true,
            };
            ctx.Host.AddChild(stage);
            ctx.Host.AddChild(runtime);
            try
            {
                runtime.Bind(stage, world.Session.Program.Subset(new[] { "pdpanel4", "pdpanel5" }));
                runtime.Play("pdpanel5", stage, applyReset: false);
                for (int i = 0; i < 6; i++)
                {
                    runtime.Advance(1f / 60f);
                }

                var first = copies.Find(c => AtPoolSite(c, pdp5));
                ctx.Check(first != null, $"pdpanel5's tear placed a planeflakes copy at pdp5");

                // Mid-flight of the first burst (the def's authored RUN_TIME is 1.0 s), the
                // second panel tears.
                runtime.Play("pdpanel4", stage, applyReset: false);
                for (int i = 0; i < 6; i++)
                {
                    runtime.Advance(1f / 60f);
                }

                var second = copies.Find(c => AtPoolSite(c, pdp4));
                ctx.Check(second != null, $"pdpanel4's tear placed a planeflakes copy at pdp4");
                ctx.Check(first != null && AtPoolSite(first, pdp5),
                    $"pdp5's copy stayed at ITS OWN site, the new tear did not steal it (BL-288)");
                ctx.Check(first != null && second != null && !ReferenceEquals(first, second),
                    $"the two tears hold two different copies");
                ctx.Check(runtime.PoolRecycles == 0,
                    $"no pool wrap for two anchors over two copies ({runtime.PoolRecycles})");
            }
            finally
            {
                runtime.Free();
                stage.Free();
            }
        });
    }

    // ---- a freed call site must leave the pooled stage's identity-keyed maps ---------------------

    // The stage memoizes each node's pool slot and keeps a sticky caller-slot claim, both keyed on
    // node identity, and both guard only the node a call hands them. A freed call site therefore
    // stays a KEY, and the engine comparer dereferences a key to answer whatever later lookup hashes
    // into its bucket, so the throw lands in an unrelated query on the runs where the hash collides.
    // Read the stale count instead (docs/verification.md INSTR-38): it is there on every run.
    [Suite("damage-template-freed-anchor",
        "a freed panel leaves no key behind in the crash rig's pooled template stage: the stale count "
        + "is read directly rather than waited on, the sweep clears it, and the next panel's tear still "
        + "takes its own copy through the rebuilt maps")]
    internal static void DamageTemplateFreedAnchor(TestContext ctx)
    {
        ctx.WithWorld(ctx.Chapter, collision: false, world =>
        {
            var stage = new Node3D { Name = "FreedAnchorStage" };
            var pdp5 = PoolAnchorNode("pdp5", new Vector3(-10, 0, 0));
            var pdp4 = PoolAnchorNode("pdp4", new Vector3(10, 0, 0));
            stage.AddChild(pdp5);
            stage.AddChild(pdp4);
            var copies = new List<Node3D>();
            for (int slot = 0; slot < 2; slot++)
            {
                var pool = new Node3D { Name = $"pool{slot}" };
                pool.SetMeta(AnimRuntime.PoolSlotMeta, slot);
                stage.AddChild(pool);
                Session.World.WorldEffectsFactory.BuildEffectStage(world.Gamez,
                    world.Session.Builder.Scene, pool, new[] { "planeflakes" });
                foreach (var child in pool.GetChildren())
                {
                    if (child is Node3D copy)
                    {
                        copies.Add(copy);
                    }
                }
            }

            var runtime = new AnimRuntime(Session.World.WorldEffectsFactory.NewCrashTemplateStage())
            {
                AutoStart = false,
                ManualAdvance = true,
                SoundHandledElsewhere = true,
                EmitterFactory = new CountingEmitterFactory(),
                NameResolveFallback = true,
            };
            ctx.Host.AddChild(stage);
            ctx.Host.AddChild(runtime);
            try
            {
                runtime.Bind(stage, world.Session.Program.Subset(new[] { "pdpanel4", "pdpanel5" }));
                runtime.Play("pdpanel5", stage, applyReset: false);
                for (int i = 0; i < 6; i++)
                {
                    runtime.Advance(1f / 60f);
                }

                ctx.Check(copies.Exists(c => AtPoolSite(c, pdp5)),
                    $"pdpanel5's tear placed a planeflakes copy at pdp5, which is what keys the stage on it");
                ctx.Same(0, runtime.FreedStageKeys(), $"nothing is freed yet, so no key names a freed node");

                // The panel goes the way a rig's own node does when its holder is torn down. Its
                // instance is stopped first: an instance left running on a freed anchor is
                // AnimRuntime's own concern and not what this suite reads.
                runtime.Stop("pdpanel5");
                stage.RemoveChild(pdp5);
                pdp5.Free();

                int stale = runtime.FreedStageKeys();
                ctx.Note($"stale template-stage keys after the panel was freed: {stale}");
                ctx.Check(stale > 0,
                    $"the freed panel is still a KEY in the stage's maps ({stale}), which is the state a later lookup dereferences");
                ctx.Check(runtime.RetireFreedNodes() > 0, $"and the sweep retires it");
                ctx.Same(0, runtime.FreedStageKeys(), $"leaving no key naming a freed node");
                ctx.Same(0, runtime.FreedNodeRows(), $"nor any node-table row naming one");

                // The rebuilt maps still answer: a sweep that dropped the live claims, or one that
                // removed a dead key by hashing it, fails here rather than silently.
                runtime.Play("pdpanel4", stage, applyReset: false);
                for (int i = 0; i < 6; i++)
                {
                    runtime.Advance(1f / 60f);
                }

                ctx.Check(copies.Exists(c => AtPoolSite(c, pdp4)),
                    $"pdpanel4's tear placed its own copy at pdp4 through the rebuilt maps");
                ctx.Same(0, runtime.FreedStageKeys(), $"and left no new stale key");
            }
            finally
            {
                runtime.Free();
                stage.Free();
            }
        });
    }

    // A flat named call-site node for DamageTemplatePool, name meta set
    // the way the crash rig's own anchor scaffold sets it, so resolution finds it.
    internal static Node3D PoolAnchorNode(string name, Vector3 at)
    {
        var node = new Node3D { Name = name, Position = at };
        node.SetMeta(AnimRuntime.NameMeta, name);
        return node;
    }

    internal static bool AtPoolSite(Node3D? copy, Node3D site) =>
        copy != null
        && copy.GlobalTransform.Origin.DistanceTo(site.GlobalTransform.Origin) < 0.5f;

    // ---- the injure staging is keyed on health, not the combined progression -------------------

    // The decoded per-part panel threshold: the original divides the part's health by its health max,
    // and blocks health damage outright while that part's armour covers the hit, so an armoured zone
    // crosses no per-part threshold at all. Driven on a real plane model with the shipped injure_anims:
    // strip the zone's armour and no panel may flip, then drive health under the threshold and the
    // panel must appear. The first half is what fails when the staging is fed PartState.Fraction.
    [Suite("damage-staging-pool",
        "the injure staging reads health only: a zone stripped of armour tears no panel though its combined fraction has crossed the threshold, and the panel appears once health itself crosses (BL-384)")]
    internal static void DamageStagingPool(TestContext ctx)
    {
        string texturesPath = SessionPaths.ChapterTextures(ctx.DataRoot, "C1");
        ctx.RequireData(texturesPath, $"C1 textures");
        var planesGamez = GameZ.Load(ctx.PlanesGamezPath);
        var stats = PlaneStats.Load(ctx.ZrdrPath, ctx.PlaneName);

        // Data-driven, not a hardcoded zone: any part whose authored list flips a pdpanelN, taking
        // its HIGHEST threshold so one health step crosses exactly one panel.
        DestroyablePart? part = null;
        float threshold = 0f;
        string panelAnim = "";
        foreach (var p in stats.DestroyableParts)
            foreach (var (frac, anim) in p.InjureAnims)
                if (anim.StartsWith("pdpanel", System.StringComparison.OrdinalIgnoreCase)
                    && frac > threshold)
                {
                    part = p;
                    threshold = frac;
                    panelAnim = anim;
                }

        ctx.Check(part != null, $"{ctx.PlaneName} authors a pdpanelN entry on some zone");
        if (part == null)
            return;
        ctx.Check(part.MaxArmor > 0f && part.MaxHp > 0f,
            $"precondition: {part.Name} carries both pools ({part.MaxArmor:0} armour, {part.MaxHp:0} hp)");
        if (part.MaxArmor <= 0f || part.MaxHp <= 0f)
            return;

        // The combined fraction with armour gone is MaxHp/(MaxHp+MaxArmor); the half this suite
        // pins only means anything when that already sits at or under the panel's threshold.
        float strippedCombined = part.MaxHp / (part.MaxHp + part.MaxArmor);
        ctx.Check(strippedCombined <= threshold,
            $"precondition: armour gone puts the COMBINED fraction at {strippedCombined:0.00}, already past {panelAnim}'s {threshold:0.00}, the early tear this pins");
        if (strippedCombined > threshold)
            return;

        var textures = new TextureArchive(texturesPath);
        // damagePanels: the pdpN nodes are skipped in a plain static build (PlaneBuilder 10c).
        var builder = new PlaneBuilder(planesGamez, textures, damagePanels: true);
        var model = builder.Build(ctx.PlaneName);
        ctx.Host.AddChild(model);
        try
        {
            var visuals = new DamageVisuals(builder.DamagePanels, model, stats);
            var torn = builder.DamagePanels
                .Where(p => p.Name.ToString().StartsWith("pdp", System.StringComparison.OrdinalIgnoreCase)
                            && !p.Name.ToString().EndsWith("_h", System.StringComparison.OrdinalIgnoreCase))
                .ToList();
            ctx.Check(torn.Count > 0, $"{ctx.PlaneName} carries {torn.Count} torn-panel nodes");
            ctx.Check(torn.All(p => !p.Visible), $"…and every one of them starts hidden");

            var damage = new PlaneDamage(stats.DestroyableParts);
            var state = damage.Apply(part.Name, 0f, part.MaxArmor)!;
            ctx.Check(state.Armor <= 0f && Mathf.IsEqualApprox(state.HealthFraction, 1f),
                $"{part.Name}: armour stripped to {state.Armor:0.#}, health untouched at {state.HealthFraction * 100f:0}% (combined {state.Fraction:0.00})");

            visuals.OnPartDamage(part.Name, state.HealthFraction);
            visuals.OnHullDamage(damage.SummaryHealthFraction);
            ctx.Check(torn.All(p => !p.Visible),
                $"no panel tore on the armour spend, though the combined fraction ({state.Fraction:0.00}) is past {panelAnim}'s {threshold:0.00}");

            // Now health itself crosses: drive it just under the threshold.
            float target = (threshold - 0.02f) * part.MaxHp;
            state = damage.Apply(part.Name, part.MaxHp - target, 0f)!;
            ctx.Check(state.HealthFraction <= threshold,
                $"{part.Name} health driven to {state.HealthFraction:0.00}, under {threshold:0.00}");

            visuals.OnPartDamage(part.Name, state.HealthFraction);
            ctx.Check(torn.Any(p => p.Visible),
                $"…and {panelAnim} flipped its torn panel once HEALTH crossed");
        }
        finally
        {
            model.Free();
        }
    }

    // ---- the cockpit-interior torn panels flip off the same injure entries ---------------------------

    // pcdp4/pcdp6 are driven off the very pdpanel4/pdpanel6 entries that already flip pdp4/pdp6,
    // no separate cockpit rule. Built with cockpitInterior:true so the pair exists, this crosses
    // whichever of the two the plane's own data authors (not every plane necessarily carries
    // both), checks the cockpit panel tears alongside its exterior namesake, survives a
    // CockpitVisibility view-mode switch (the group hide/show never touches a child's own Visible),
    // and clears on Reset() exactly like the exterior panel.
    [Suite("cockpit-panel-staging",
        "the cockpit-interior torn panels pcdp4/pcdp6 flip off the SAME pdpanel4/pdpanel6 injure entries as their exterior namesakes, survive a CockpitVisibility view-mode switch, and clear together on respawn's Reset() (B12)")]
    internal static void CockpitPanelStaging(TestContext ctx)
    {
        string texturesPath = SessionPaths.ChapterTextures(ctx.DataRoot, "C1");
        ctx.RequireData(texturesPath, $"C1 textures");
        var planesGamez = GameZ.Load(ctx.PlanesGamezPath);
        var stats = PlaneStats.Load(ctx.ZrdrPath, ctx.PlaneName);

        DestroyablePart? part = null;
        float threshold = 0f;
        string panelAnim = "";
        string cockpitNode = "";
        foreach (var p in stats.DestroyableParts)
            foreach (var (frac, anim) in p.InjureAnims)
                if ((anim.Equals("pdpanel4", System.StringComparison.OrdinalIgnoreCase)
                        || anim.Equals("pdpanel6", System.StringComparison.OrdinalIgnoreCase))
                    && frac > threshold)
                {
                    part = p;
                    threshold = frac;
                    panelAnim = anim;
                    cockpitNode = "pcdp" + anim["pdpanel".Length..];
                }

        ctx.Check(part != null, $"{ctx.PlaneName} authors a pdpanel4/pdpanel6 entry on some zone");
        if (part == null)
            return;

        var textures = new TextureArchive(texturesPath);
        var builder = new PlaneBuilder(planesGamez, textures, damagePanels: true, cockpitInterior: true);
        var model = builder.Build(ctx.PlaneName);
        ctx.Host.AddChild(model);
        try
        {
            var interior = builder.CockpitInterior;
            ctx.Check(interior != null, $"{ctx.PlaneName} built its cockpit1 interior");
            if (interior == null)
                return;
            var cockpit = WorldAndToolSuites.FindNamed(interior, cockpitNode);
            ctx.Check(cockpit != null, $"the interior carries {cockpitNode}, {panelAnim}'s cockpit twin");
            if (cockpit == null)
                return;
            ctx.Check(!cockpit.Visible, $"{cockpitNode} starts hidden, same as its exterior twin");

            var visuals = new DamageVisuals(builder.DamagePanels, model, stats,
                cockpitPanels: builder.CockpitDamagePanels);
            var torn = builder.DamagePanels
                .Where(p => AnimRuntime.NameOf(p).Equals("pdp" + panelAnim["pdpanel".Length..],
                    System.StringComparison.OrdinalIgnoreCase))
                .ToList();
            ctx.Check(torn.Count > 0, $"the exterior twin pdp{panelAnim["pdpanel".Length..]} was built");

            float target = (threshold - 0.02f) * part.MaxHp;
            visuals.OnPartDamage(part.Name, target / part.MaxHp);
            ctx.Check(torn.All(p => p.Visible),
                $"…{panelAnim} tore its exterior panel once health crossed {threshold:0.00}");
            ctx.Check(cockpit.Visible,
                $"…and {cockpitNode} tore alongside it, the same injure entry, not a separate rule");

            // A view-mode switch only ever hides/shows the interior GROUP root; a child's own
            // Visible must ride through unchanged (B11's CockpitVisibility.Apply, four nodes only).
            var pilotView = CockpitVisibility.Bind(model, interior);
            ctx.Check(pilotView != null, $"the visibility rig binds to the built model");
            if (pilotView == null)
                return;
            pilotView.Apply(PilotViewMode.Nose, firstPerson: true);
            ctx.Check(cockpit.Visible, $"{cockpitNode} stays torn while the interior group itself hides (Nose)");
            pilotView.Apply(PilotViewMode.Cockpit, firstPerson: true);
            ctx.Check(cockpit.Visible, $"…and still torn once Cockpit shows the group again");

            visuals.Reset();
            ctx.Check(torn.All(p => !p.Visible) && !cockpit.Visible,
                $"respawn's Reset() clears the exterior panel and its cockpit twin together");
        }
        finally
        {
            model.Free();
        }
    }

    // ---- the injure ladder stages per ENTRY, and retracts on the upward crossing ---------------

    // fury's AI ladder names random_remote_damage at six of its seven thresholds, so a latch keyed
    // on the anim name plays five of them never. Three halves: the count over the real ladder, the
    // retraction a repair makes (cleared on the upward crossing alone, never by staying below), and
    // the per-(part, entry) keying, which no shipped def exercises, see the synthetic ladder below.
    [Suite("damage-stage-slots",
        "the injure ladder stages per ENTRY: fury's six random_remote_damage thresholds each fire, a repair retracts what it lifted back over, and one entry on four zones fires four times (BL-385/BL-384)")]
    internal static void DamageStageSlots(TestContext ctx)
    {
        ctx.RequireData(ctx.ZrdrPath, $"zrdr archive");
        var stats = PlaneStats.LoadForAi(ctx.ZrdrPath, "player_fury");
        ctx.Check(stats.VehicleInjureAnims.Count == 7,
            $"fury's AI ladder carries {stats.VehicleInjureAnims.Count} entries (want 7)");

        var root = new Node3D { Name = "fury_root" };
        ctx.Host.AddChild(root);
        try
        {
            var visuals = new DamageVisuals(System.Array.Empty<Node3D>(), root, stats);
            ctx.Check(!visuals.PairsPanels,
                $"fury's AI data names no pdpanel stage, so nothing is paired and nothing warns");

            // one step per band, so each threshold is crossed on its own
            foreach (float frac in new[] { 0.99f, 0.9f, 0.7f, 0.55f, 0.47f, 0.42f, 0.3f, 0.2f })
                visuals.OnHullDamage(frac);
            int repeats = visuals.StagedEntryCount("random_remote_damage");
            ctx.Check(repeats == 6,
                $"the whole ladder walked down: {repeats} random_remote_damage entries staged (want 6)");
            ctx.Check(visuals.StagedEntryCount("pfsmoketrail") == 1,
                $"…and the one pfsmoketrail entry at 0.40 staged with them");

            // repaired to half: the three entries under 0.5 retract, the four at or above hold
            visuals.OnHullDamage(0.5f);
            ctx.Check(visuals.StagedEntryCount("random_remote_damage") == 4
                      && visuals.StagedEntryCount("pfsmoketrail") == 0,
                $"repaired to 50%: {visuals.StagedEntryCount("random_remote_damage")} random_remote_damage and {visuals.StagedEntryCount("pfsmoketrail")} pfsmoketrail entries still staged (want 4 and 0)");
            visuals.OnHullDamage(0.2f);
            ctx.Check(visuals.StagedEntryCount("random_remote_damage") == 6
                      && visuals.StagedEntryCount("pfsmoketrail") == 1,
                $"…and every retracted entry fires again on the next descent");
        }
        finally
        {
            root.Free();
        }

        // Per-(part, entry) keying. A census of all 22 shipped defs carrying destroyable_parts found
        // no anim authored on two zones of one def, so the shared-entry case is driven from a
        // synthetic ladder: four zones naming pdpanel1. The pairing warning fires here by design.
        var multi = new PlaneStats();
        foreach (string zone in new[] { "nose", "tail", "leftwing", "rightwing" })
            multi.DestroyableParts.Add(new DestroyablePart { Name = zone, InjureAnims = { (0.5f, "pdpanel1") } });

        var zoneRoot = new Node3D { Name = "zoned_root" };
        ctx.Host.AddChild(zoneRoot);
        try
        {
            var visuals = new DamageVisuals(System.Array.Empty<Node3D>(), zoneRoot, multi);
            int starts = 0, stops = 0;
            visuals.DamageEffectSink = _ => starts++;
            visuals.DamageEffectStopOne = _ => stops++;
            foreach (var part in multi.DestroyableParts)
                visuals.OnPartDamage(part.Name, 0.4f);
            ctx.Check(starts == 4 && visuals.StagedEntryCount("pdpanel1") == 4,
                $"one entry authored on four zones started {starts} times and holds {visuals.StagedEntryCount("pdpanel1")} slots (want 4 and 4)");

            foreach (var part in multi.DestroyableParts)
                visuals.OnPartDamage(part.Name, 0.3f);
            ctx.Check(starts == 4, $"…and staying below the threshold started nothing new ({starts} total)");

            visuals.OnPartDamage("nose", 1f);
            ctx.Check(visuals.StagedEntryCount("pdpanel1") == 3 && stops == 0,
                $"one zone repaired clears its own slot and stops nothing, three zones still hold the anim");
            foreach (var part in multi.DestroyableParts)
                visuals.OnPartDamage(part.Name, 1f);
            ctx.Check(visuals.StagedEntryCount("pdpanel1") == 0 && stops == 1,
                $"…and the last one to retract stops the stage exactly once (stops={stops})");

            visuals.OnPartDamage("nose", 0.4f);
            ctx.Check(starts == 5, $"a repaired zone re-crossing fires again ({starts} starts)");
        }
        finally
        {
            zoneRoot.Free();
        }

        // A2's other half: a player airframe DOES name pdpanel stages, so pairing (and its
        // missing-data alarm) stays armed on that path.
        var player = PlaneStats.Load(ctx.ZrdrPath, "player_fury");
        var playerRoot = new Node3D { Name = "player_root" };
        ctx.Host.AddChild(playerRoot);
        try
        {
            var visuals = new DamageVisuals(System.Array.Empty<Node3D>(), playerRoot, player);
            ctx.Check(visuals.PairsPanels,
                $"the player fury names pdpanel stages, so panel pairing still runs for it");
        }
        finally
        {
            playerRoot.Free();
        }
    }

    // ---- an AI plane's ladder reaching the rig runtime, through the real spawner ----------------

    // The seam the tree-free slot tests cannot reach: FlightRoster building an aircraft whose
    // hull then falls, with the stages counted off the RUNTIME's own instance hook rather than off
    // the sink the wiring under test installs. Phase 1 (the DamageVisuals) and phase 2 (the sink and
    // stops, wired inside BuildFlightCrashRuntime) live in different files, so "the object exists"
    // and "it plays into a runtime" are separate failures and asserted separately.
    [Suite("ai-damage-stages",
        "an AI plane spawned through FlightRoster stages end to end: its hull falls through the take-hit path and the rig runtime starts six random_remote_damage instances plus one pfsmoketrail, all anchored inside that aircraft, a repair tears each stage down once, and the Bloodhawk's missing elevator pair is named (BL-385)")]
    internal static void AiDamageStages(TestContext ctx)
    {
        ctx.RequireData(ctx.PlanesGamezPath, $"planes gamez");
        ctx.RequireData(ctx.ZrdrPath, $"zrdr archive");
        ctx.WithWorld(ctx.Chapter, collision: false, world =>
        {
            var planesGamez = GameZ.Load(ctx.PlanesGamezPath);
            var textures = new TextureArchive(SessionPaths.ChapterTextures(ctx.DataRoot, world.Chapter));
            ProjectilePool? pool = null;
            FlightController? ai = null;
            FlightController? bhawk = null;
            try
            {
                var live = new ProjectilePool(textures, null, null);
                pool = live;
                ctx.Host.AddChild(live);
                var spec = SessionSpec.Parse(System.Array.Empty<string>());
                var liveries = new LiveryResolver(spec, Path.Combine(ctx.DataRoot, "extracted", "rof"));
                var factory = new Session.World.WorldEffectsFactory(spec, ctx.Host, () => Vector3.Zero);
                var inputs = new AircraftAssemblyResources
                {
                    PlanesGamez = planesGamez,
                    StatsFor = plane => PlaneStats.Load(ctx.ZrdrPath, plane),
                    AiStatsFor = (plane, aiDef) => PlaneStats.LoadForAi(ctx.ZrdrPath, plane, aiDef),
                    PaintRng = new RandomNumberGenerator(),
                    ZrdrPath = ctx.ZrdrPath,
                    StockLoadouts = StockLoadouts.Load(),
                    WeaponDefs = WeaponDefs.Load(ctx.ZrdrPath, null),
                    Textures = textures,
                    Shakes = ShakeDefs.Load(ctx.ZrdrPath),
                };
                var spawner = new FlightRoster(FlightRosterPolicy.From(spec), liveries, factory, ctx.Host, inputs, new FlightWorldBindings { Projectiles = live, Gamez = world.Gamez, WorldScene = world.Session.Builder.Scene, CrashProgram = world.Session.Program }, new HumanRosterBindings());
                var start = new Vector3(0f, 500f, 0f);
                ai = spawner.SpawnAi(new AiSpawn("player_fury", start, start + Vector3.Forward,
                    AiPilot.HoldingCourse(start, start + Vector3.Forward)));

                ctx.Check(ai.Visuals != null,
                    $"the spawner built the AI Fury's DamageVisuals (phase 1)");
                // ⚠ Phase 2 belongs to the crash rig, which a mid-flight introduction leaves armed
                // rather than built: force it, the way the first round would. Phase 1 is asked
                // ABOVE this, being the half that must already be there on the launch frame.
                ai.EnsureCrashRig();
                ctx.Check(ai.Visuals?.DamageEffectSink != null && ai.Visuals?.DamageEffectStop != null
                          && ai.Visuals?.DamageEffectStopOne != null,
                    $"…and the crash-runtime build wired its sink and both stops (phase 2)");
                if (ai.Visuals is not { } visuals || ai.CrashRuntime is not { } rig
                    || ai.Damage is not { } damage)
                {
                    return;
                }

                var starts = new Dictionary<string, int>(System.StringComparer.OrdinalIgnoreCase);
                var stops = new Dictionary<string, int>(System.StringComparer.OrdinalIgnoreCase);
                var anchors = new List<Node3D?>();
                rig.OnInstanceStarted += (def, anchor) =>
                {
                    string n = def.AnimName ?? def.Name ?? "";
                    starts[n] = starts.TryGetValue(n, out var c) ? c + 1 : 1;
                    if (Flight.Airframe.EffectCatalogue.AiDamageStageAnims.Contains(n, System.StringComparer.OrdinalIgnoreCase))
                        anchors.Add(anchor);
                };
                rig.OnInstanceFinished += (def, _) =>
                {
                    string n = def.AnimName ?? def.Name ?? "";
                    stops[n] = stops.TryGetValue(n, out var c) ? c + 1 : 1;
                };

                // Armour off first, in one spend, then the ladder walked down one band at a time
                // through TakeCollisionHit, the production take-hit entry, not the visuals API.
                ai.TakeCollisionHit(damage.WholeArmor, 0f, ai.GlobalPosition, 0);
                foreach (float frac in new[] { 0.9f, 0.7f, 0.55f, 0.47f, 0.42f, 0.3f, 0.2f })
                    DestroyChoreographySuites.SpendHullTo(ai, damage, frac);
                ctx.Check(damage.SummaryHealthFraction is > 0.19f and < 0.21f,
                    $"the AI Fury's hull walked down to {damage.SummaryHealthFraction * 100f:0}% through the take-hit path");
                ctx.Check(DestroyChoreographySuites.Count(starts, "random_remote_damage") == 6 && DestroyChoreographySuites.Count(starts, "pfsmoketrail") == 1,
                    $"the rig runtime STARTED {DestroyChoreographySuites.Count(starts, "random_remote_damage")} random_remote_damage and {DestroyChoreographySuites.Count(starts, "pfsmoketrail")} pfsmoketrail instance(s) (want 6 and 1)");
                ctx.Check(visuals.StagedEntryCount("random_remote_damage") == 6
                          && visuals.StagedEntryCount("pfsmoketrail") == 1,
                    $"…and the ladder holds one slot per start: {visuals.StagedEntryCount("random_remote_damage")} and {visuals.StagedEntryCount("pfsmoketrail")}");
                int offPlane = anchors.Count(a => a == null || (a != ai && !ai.IsAncestorOf(a)));
                ctx.Check(anchors.Count == 7 && offPlane == 0,
                    $"every one of the {anchors.Count} stage instances anchored inside THIS aircraft ({offPlane} elsewhere)");

                // The repair: the whole ladder retracts, and each stage is stopped exactly once,
                // once per ANIM, not once per entry, or five of fury's six would stop nothing.
                stops.Clear();
                visuals.OnHullDamage(1f);
                ctx.Check(DestroyChoreographySuites.Count(stops, "random_remote_damage") == 1 && DestroyChoreographySuites.Count(stops, "pfsmoketrail") == 1,
                    $"a full repair tore down {DestroyChoreographySuites.Count(stops, "random_remote_damage")} random_remote_damage and {DestroyChoreographySuites.Count(stops, "pfsmoketrail")} pfsmoketrail instance(s) (want 1 and 1)");
                ctx.Check(visuals.StagedEntryCount("random_remote_damage") == 0,
                    $"…leaving no entry staged");
                foreach (float frac in new[] { 0.9f, 0.7f, 0.55f, 0.47f, 0.42f, 0.3f, 0.2f })
                    visuals.OnHullDamage(frac);
                ctx.Check(DestroyChoreographySuites.Count(starts, "random_remote_damage") == 12 && DestroyChoreographySuites.Count(starts, "pfsmoketrail") == 2,
                    $"…and the next descent fires all seven again ({DestroyChoreographySuites.Count(starts, "random_remote_damage")} and {DestroyChoreographySuites.Count(starts, "pfsmoketrail")} starts in total)");

                // C16's anchor census as a live A/B: the stage defs are authored against the
                // Devastator, and the Bloodhawk spells its elevators l_elev/r_elev. Same rig, same
                // pinned dice, one airframe apart, so the warned set is about the airframe alone.
                ctx.Check(rig.AnchorWarnLabel == "player_fury" && rig.AnchorWarnAnimNames != null
                          && rig.AnchorWarnAnimNames.Contains("random_remote_damage"),
                    $"the AI rig is armed to name an unresolved stage anchor (label={rig.AnchorWarnLabel ?? "-"})");
                CascadeUntilExhausted(rig, ai.PlaneModel);
                ctx.Check(rig.UnresolvedStageAnchors.Count == 0,
                    $"the Fury carries all five stage anchors: {rig.UnresolvedStageAnchors.Count} unresolved [{string.Join(", ", rig.UnresolvedStageAnchors)}]");

                bhawk = spawner.SpawnAi(new AiSpawn("player_bhawk", start + new Vector3(3000f, 0f, 0f),
                    start + new Vector3(3000f, 0f, -1f),
                    AiPilot.HoldingCourse(start + new Vector3(3000f, 0f, 0f), start + new Vector3(3000f, 0f, -1f))));
                if (bhawk.CrashRuntime is { } bhawkRig)
                {
                    CascadeUntilExhausted(bhawkRig, bhawk.PlaneModel);
                    ctx.Check(bhawkRig.UnresolvedStageAnchors.SequenceEqual(new[]
                        {
                            "random_remote_damage|lft_elev", "random_remote_damage|rt_elev",
                        }),
                        $"the Bloodhawk misses exactly the elevator pair: [{string.Join(", ", bhawkRig.UnresolvedStageAnchors)}]");
                }
            }
            finally
            {
                bhawk?.Free();
                ai?.Free();
                pool?.Free();
                textures.Dispose();
            }
        });
    }

    // random_remote_damage is a RandomWeight 0.33 chain: one start reaches at most one of its four
    // anchors, so the census below needs the whole cascade walked. 200 starts puts the deepest step
    // (0.67³ × 0.33 ≈ 10 %) beyond any doubt while the seed keeps it identical run to run.
    private static void CascadeUntilExhausted(AnimRuntime rig, Node3D? planeModel)
    {
        for (int i = 0; i < 200; i++)
            rig.Play("random_remote_damage", planeModel, applyReset: false);
    }
}
