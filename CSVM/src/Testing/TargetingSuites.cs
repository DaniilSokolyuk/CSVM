using System.Collections.Generic;
using System.Linq;
using CSVM.Flight.Ai;
using CSVM.Flight.Airframe;
using CSVM.Flight.Camera;
using CSVM.Flight.Hud;
using CSVM.Flight.Weapons;
using CSVM.Mech3;
using CSVM.Session.InstantAction;
using CSVM.Session.Roster;
using CSVM.Utils;
using Godot;

namespace CSVM.Testing;

/// <summary>Suites asserting the targeting model: the TargetRef abstraction, the classed candidate
/// pool, the sticky selection, its input decoding and scripted twin, and the marker HUD.</summary>
internal static class TargetingSuites
{
    // TargetRef, entirely tree-free, which is the seam's whole claim. One ref per source kind: an
    // aircraft with both health pools, a sub-part with health alone, a turret emplacement with
    // neither. That spread is the point, since the turret's nulls are the case that must not
    // silently become 1.0.
    [Suite("target-ref",
        "the one abstraction over every selectable thing, tree-free: a TargetRef built for "
        + "each of the three source kinds (aircraft, zeppelin sub-part, turret emplacement) "
        + "forwards the wrapped AimCandidate's pose/team/liveness/source and reads its own "
        + "identity back; health and armor are genuinely optional, so the turret carries "
        + "neither and the structure carries health alone; the label line runs the original's "
        + "four format strings; Classify reproduces FUN_004b5cd0's order (objective over "
        + "otherTarget over the team split, an unflagged turret not selectable at all); and "
        + "identity is the SOURCE object, not the wrapper")]
    internal static void TargetRefModel(TestContext ctx)
    {
        int ownTeam = AimAssist.PlayerTeam;
        var plane = new object();  // stands in for the FlightController the collector hands back
        var engine = new object(); // a zeppelin engine's DestructibleRegistry.Instance
        var gun = new object();    // a TurretController

        // --- an enemy aircraft: the Kestrel screenshot's case ------------------------------------
        var planeCandidate = new AimCandidate
        {
            Position = new Vector3(120f, 300f, -640f),
            Velocity = new Vector3(0f, 0f, -90f),
            Team = AimAssist.PlayerTeam + 1,
            Live = true,
            Source = plane,
        };
        var kestrel = TargetRef.ForAircraft(planeCandidate, TargetClass.Enemy, "ai1_player_kestrel",
            "Kestrel", TargetRef.Fraction(70.2f, 90f), TargetRef.Fraction(91f, 100f));
        ctx.Check(kestrel.Position == planeCandidate.Position
                  && kestrel.Velocity == planeCandidate.Velocity
                  && kestrel.Team == planeCandidate.Team && kestrel.Live
                  && ReferenceEquals(kestrel.Source, plane),
            $"the aircraft ref forwards the wrapped candidate's pose, team, liveness and source");
        ctx.Check(kestrel.Kind == AimTargetKind.Vehicle && kestrel.Class == TargetClass.Enemy
                  && !kestrel.Objective && kestrel.Name == "ai1_player_kestrel"
                  && kestrel.DisplayName == "Kestrel",
            $"…and reads back its own pool, cycle, identity name '{kestrel.Name}' and the marker's own '{kestrel.DisplayName}' (--target= pins the node name, the marker prints the airframe)");
        ctx.Check(TargetRef.ForAircraft(planeCandidate, TargetClass.Enemy, "bandit").DisplayName
                  == "bandit",
            $"a source with no roster entry prints its own name rather than an empty label");
        ctx.Check(kestrel.CategoryLine.Length == 0,
            $"an ordinary aircraft carries neither label half, so line 1 is blank (the Kestrel shot shows line 2 alone) got='{kestrel.CategoryLine}'");
        ctx.Check(kestrel.Health is { } h && Mathf.IsEqualApprox(h, 0.78f)
                  && kestrel.Armor is { } a && Mathf.IsEqualApprox(a, 0.91f),
            $"health and armor read separately, never blended (decision 12's H78 A91) h={kestrel.Health:0.00} a={kestrel.Armor:0.00}");

        // --- a zeppelin sub-part: the C1 M04 objective, health but no armor ----------------------
        var engineCandidate = new AimCandidate
        {
            Position = new Vector3(-40f, 900f, 1200f),
            Velocity = new Vector3(6f, 0f, 0f),
            Team = AimAssist.WorldTeam,
            Live = true,
            Source = engine,
        };
        var promisedLand = TargetRef.ForStructure(engineCandidate, TargetClass.Enemy,
            "Promised Land", "Zeppelin", "Destroy", objective: true,
            TargetRef.Fraction(150f, 200f));
        ctx.Check(promisedLand.Kind == AimTargetKind.Structure && promisedLand.Objective
                  && promisedLand.Class == TargetClass.Enemy,
            $"a flagged sub-part is a Structure riding the ENEMY cycle with the objective companion set (the -too switch, which moves objectives to Non-Aircraft, is not ported)");
        ctx.Check(promisedLand.CategoryLine == "Zeppelin [Destroy] -"
                  && promisedLand.Name == "Promised Land",
            $"both label halves compose the original's '%s [%s] -' got='{promisedLand.CategoryLine}'");
        ctx.Check(promisedLand.Health is { } ph && Mathf.IsEqualApprox(ph, 0.75f)
                  && promisedLand.Armor == null,
            $"a structure has health and NO armor pool h={promisedLand.Health:0.00} a={promisedLand.Armor?.ToString("0.00") ?? "none"}");

        // --- a turret emplacement: no health model at all ----------------------------------------
        var gunCandidate = new AimCandidate
        {
            Position = new Vector3(500f, 12f, 500f),
            Velocity = Vector3.Zero,
            Team = AimAssist.PlayerTeam + 1,
            Live = false, // its healthy node was swapped out, the decoded permanent kill switch
            Source = gun,
        };
        var flak = TargetRef.ForTurret(gunCandidate, TargetClass.NonAircraft, "AA Emplacement");
        ctx.Check(flak.Kind == AimTargetKind.Turret && flak.Class == TargetClass.NonAircraft
                  && !flak.Live && flak.Name == "AA Emplacement",
            $"the turret ref carries its own kind and cycle and forwards a DEAD candidate's liveness rather than hiding it");
        ctx.Check(flak.Health == null && flak.Armor == null && flak.CategoryLine.Length == 0,
            $"a turret emplacement has neither pool, so both figures are omitted, never defaulted to full (decision 12's trap) h={flak.Health?.ToString() ?? "none"} a={flak.Armor?.ToString() ?? "none"}");
        ctx.Check(TargetRef.Fraction(50f, 0f) == null && TargetRef.Fraction(300f, 200f) == 1f,
            $"Fraction is the one place that decides 'no source, no figure', and it clamps");

        // --- the class model, FUN_004b5cd0's own order -------------------------------------------
        ctx.Check(TargetRef.Classify(AimTargetKind.Vehicle, live: true, ownTeam + 1, ownTeam)
                      == TargetClass.Enemy
                  && TargetRef.Classify(AimTargetKind.Vehicle, live: true, ownTeam, ownTeam)
                      == TargetClass.Ally
                  && TargetRef.Classify(AimTargetKind.Vehicle, live: true, AimAssist.NeutralTeam,
                      ownTeam) == TargetClass.Ally,
            $"the aircraft split: a different non-zero team is Enemy, the same team is Ally, and either side unaffiliated is Ally too");
        ctx.Check(TargetRef.Classify(AimTargetKind.Vehicle, live: true, ownTeam, ownTeam,
                      objectiveTarget: true) == TargetClass.Enemy
                  && TargetRef.Classify(AimTargetKind.Structure, live: true, AimAssist.WorldTeam,
                      ownTeam, otherTarget: true, objectiveTarget: true) == TargetClass.Enemy,
            $"objectiveTarget overrides everything below it, including an own-team aircraft and otherTarget on the same entity");
        ctx.Check(TargetRef.Classify(AimTargetKind.Structure, live: true, AimAssist.WorldTeam,
                      ownTeam, otherTarget: true) == TargetClass.NonAircraft
                  && TargetRef.Classify(AimTargetKind.Turret, live: true, ownTeam + 1, ownTeam,
                      otherTarget: true) == TargetClass.NonAircraft,
            $"otherTarget puts a structure or a turret on the Non-Aircraft cycle");
        ctx.Check(TargetRef.Classify(AimTargetKind.Turret, live: true, ownTeam + 1, ownTeam) == null
                  && TargetRef.Classify(AimTargetKind.Structure, live: true, AimAssist.WorldTeam,
                      ownTeam) == null,
            $"an UNFLAGGED turret or structure is not selectable at all, the mission decides, not the world (which is why DestructibleRegistry never feeds this pool)");
        ctx.Check(TargetRef.Classify(AimTargetKind.Vehicle, live: false, ownTeam + 1, ownTeam) == null
                  && TargetRef.Classify(AimTargetKind.Structure, live: false, AimAssist.WorldTeam,
                      ownTeam, objectiveTarget: true) == null,
            $"the liveness predicate runs FIRST, so a dead objective classifies to nothing");

        // --- identity is the source object, not the wrapper --------------------------------------
        var sameEngineLater = TargetRef.ForStructure(
            new AimCandidate { Position = Vector3.Up, Live = true, Source = engine },
            TargetClass.NonAircraft, "Promised Land");
        ctx.Check(promisedLand.IsSameTarget(sameEngineLater),
            $"a ref rebuilt next frame at a new pose still names the same target (FUN_004b6490 re-finds the selection by ENTITY, the wrappers are new objects every frame)");
        ctx.Check(!promisedLand.IsSameTarget(kestrel)
                  && !TargetRef.ForTurret(default, TargetClass.NonAircraft, "")
                      .IsSameTarget(TargetRef.ForTurret(default, TargetClass.NonAircraft, "")),
            $"two different sources never match, and a null source matches nothing, including another null, which would otherwise make every sourceless ref the same target");
    }

    /// <summary>The tap/hold decoding. The suite reads no gamepad and no bare key press, so what
    /// is pinned here is everything BETWEEN the device read and the
    /// action: <see cref="TapHoldButton"/>'s tap-versus-hold rule, and the attacker queue's live
    /// wiring through a real <see cref="FlightController.TakeProjectileHit"/> on real rigs in a real
    /// pool. The key and pad reads themselves are owed as live play.</summary>
    [Suite("target-input",
        "the tap/hold decoding, which is what the suite CAN read (a gamepad and a bare key "
        + "press it cannot): TapHoldButton's resolve-on-release rule, a short press taps, "
        + "crossing 250 ms fires the hold ONCE mid-press and the release is then spent, a held "
        + "button never repeats, and an up button with no press reports nothing; plus the "
        + "attacker queue's live wiring, where a real hostile round through TakeProjectileHit "
        + "records its shooter, a friendly-fire round and an unowned one record nothing, and "
        + "ProjectilePool.RigOfShooter resolves a shooter id to its plane")]
    internal static void TargetInputModel(TestContext ctx)
    {
        // --- the tap/hold decision: pure, no device involved --------------------------------
        const float dt = 1f / 60f;
        (int Taps, int Holds) Press(TapHoldButton b, int downFrames)
        {
            int taps = 0, holds = 0;
            void Count(TapHold r)
            {
                if (r == TapHold.Tap)
                {
                    taps++;
                }
                else if (r == TapHold.Hold)
                {
                    holds++;
                }
            }

            for (int i = 0; i < downFrames; i++)
            {
                Count(b.Step(true, dt));
            }

            Count(b.Step(false, dt));
            return (taps, holds);
        }

        var btn = new TapHoldButton(0.25f);
        ctx.Check(btn.Step(false, dt) == TapHold.None,
            $"a button that is simply up reports nothing, a release with no press is not a tap");
        ctx.Check(Press(btn, 12) == (1, 0),
            $"a 0.20 s press taps once on RELEASE and never holds");
        ctx.Check(Press(btn, 18) == (0, 1),
            $"a 0.30 s press holds once and the release is then SPENT, it does not also tap, which is the flicker decision 7 exists to avoid");
        ctx.Check(Press(btn, 120) == (0, 1),
            $"holding for two seconds still fires exactly once, this is a tap/hold split, not a repeat");
        ctx.Check(Press(btn, 6) == (1, 0),
            $"and the next press taps again, so a hold leaves no state behind");

        // --- the attacker queue, wired through a real hit ------------------------------------
        ctx.RequireData(ctx.PlanesGamezPath, $"planes gamez");
        ctx.RequireData(ctx.ZrdrPath, $"zrdr archive");
        string texturesPath = SessionPaths.ChapterTextures(ctx.DataRoot, "C1");
        ctx.RequireData(texturesPath, $"C1 textures");
        var planesGamez = GameZ.Load(ctx.PlanesGamezPath);
        var weapons = WeaponDefs.Load(ctx.ZrdrPath, null);
        var gun = weapons.All.FirstOrDefault(w => w.IsGun && w.ArmorDamage is > 0f);
        ctx.Check(gun != null, $"a gun with ARMOR_DAMAGE exists in the data");
        if (gun == null)
        {
            return;
        }

        var stats = PlaneStats.Load(ctx.ZrdrPath, ctx.PlaneName);
        var textures = new TextureArchive(texturesPath);
        ProjectilePool? pool = null;
        FlightController? self = null;
        FlightController? hostile = null;
        FlightController? friendly = null;
        try
        {
            var live = new ProjectilePool(textures, null, null);
            pool = live;
            ctx.Host.AddChild(live);

            FlightController BuildRig(int playerIndex, int team, Vector3 pos)
            {
                var model = new PlaneBuilder(planesGamez, textures).Build(ctx.PlaneName);
                var rig = new FlightController
                {
                    PlaneModel = model,
                    Collider = PlaneCollider.Build(model),
                    Damage = new PlaneDamage(stats.DestroyableParts),
                    PlayerIndex = playerIndex,
                    Team = team,
                    Projectiles = live,
                    UseKeyboard = false,
                    AllowPause = false,
                };
                rig.AddChild(model);
                rig.Setup(new FlightModel(stats), ctx.Camera, new CamParams(), pos, pos + Vector3.Forward);
                ctx.Host.AddChild(rig);
                return rig;
            }

            self = BuildRig(0, AimAssist.PlayerTeam, Vector3.Zero);
            friendly = BuildRig(1, AimAssist.PlayerTeam, new Vector3(0f, 0f, -200f));
            hostile = BuildRig(2, InstantActionRuntime.EnemyTeam, new Vector3(0f, 0f, -400f));
            self.Targeting = new TargetSelection();

            ctx.Check(ReferenceEquals(live.RigOfShooter(2), hostile)
                      && ReferenceEquals(live.RigOfShooter(0), self),
                $"RigOfShooter resolves a shooter id to the plane that fired, the ids are unique across the session, so it names one plane and not a class of them");
            ctx.Check(live.RigOfShooter(ProjectilePool.NoShooter) == null
                      && live.RigOfShooter(9999) == null,
                $"…and an unowned round or an unregistered id resolves to nothing");

            var impact = new Vector3(0f, 0f, -2f);
            self.TakeProjectileHit(gun, impact, "nose", ProjectilePool.NoShooter);
            ctx.Check(self.Targeting.Attackers.Count == 0,
                $"an unowned round (a turret's, a zeppelin broadside) records no attacker, there is nobody to target");
            self.TakeProjectileHit(gun, impact, "nose", friendly.PlayerIndex);
            ctx.Check(self.Targeting.Attackers.Count == 0,
                $"friendly fire records no attacker either: the engine's gate is a shooter on a DIFFERENT, non-zero team");
            self.TakeProjectileHit(gun, impact, "nose", hostile.PlayerIndex);
            ctx.Check(self.Targeting.Attackers.Count == 1
                      && ReferenceEquals(self.Targeting.Attackers[0], hostile),
                $"a hostile round puts its shooter on the queue Next Enemy walks first count={self.Targeting.Attackers.Count}");
            self.TakeProjectileHit(gun, impact, "nose", hostile.PlayerIndex);
            ctx.Check(self.Targeting.Attackers.Count == 1,
                $"…and a second round from the same shooter does not list it twice");
        }
        finally
        {
            self?.Free();
            friendly?.Free();
            hostile?.Free();
            pool?.Free();
            textures.Dispose();
        }
    }

    // TargetSelection, tree-free and data-free: plain object sources through the real TargetPool,
    // a plane at the origin on the identity basis, no world. The geometry is chosen so the decoded
    // sector order and a plain nearest-in-space order DISAGREE (nearest is 100 m off the right
    // wing, the cycle head is 400 m ahead), so an implementation that sorted by range fails.
    [Suite("target-selection",
        "the sticky selection, tree-free: the decoded cycle order in one assertion "
        + "(objectives, then ahead/behind/left/right with distance inside a sector), the "
        + "auto-acquire at the head, Next/Previous stepping and wrapping, Nearest as HEAD OF "
        + "CYCLE rather than nearest-in-space, target death dropping to the head and not to the "
        + "dead entry's neighbour, the nearest-after-a-kill setting moving that one re-resolve onto "
        + "the nearest of the cycle while the acquire and a class change keep the head, "
        + "own respawn preserving a live selection, range/bearing/"
        + "attitude changes never dropping one, Target Nothing STAYING cleared through repeated "
        + "rebuilds, nearest-crosshairs scoring the NOSE cone (not the pipper) with its 2 km cap "
        + "and reaching an ally, and 0x24's attacker queue walked backwards")]
    internal static void TargetSelectionModel(TestContext ctx)
    {
        var ahead1 = new object();      // 900 m ahead   -> sector 0
        var ahead2 = new object();      // 400 m ahead   -> sector 0, nearer
        var behind = new object();      // 200 m behind  -> sector 1
        var left = new object();        // 200 m left    -> sector 2
        var right = new object();       // 100 m right   -> sector 3, the nearest thing in space
        var ally = new object();        // 300 m ahead, own team
        int own = AimAssist.PlayerTeam;
        int foe = InstantActionRuntime.EnemyTeam;
        var basis = Basis.Identity;

        AimCandidateSet Scan(params (object Src, Vector3 At, int Team)[] entries)
        {
            var s = new AimCandidateSet();
            foreach (var (src, at, team) in entries)
            {
                s.AddVehicle(at, Vector3.Zero, team, live: true, src);
            }

            return s;
        }

        var full = Scan(
            (ahead1, new Vector3(0f, 0f, -900f), foe),
            (ahead2, new Vector3(0f, 0f, -400f), foe),
            (behind, new Vector3(0f, 0f, 200f), foe),
            (left, new Vector3(-200f, 0f, 0f), foe),
            (right, new Vector3(100f, 0f, 0f), foe),
            (ally, new Vector3(0f, 0f, -300f), own));

        // The sector key itself, against the decode's own table.
        ctx.Check(TargetSelection.SectorKey(Vector3.Forward * 5f, basis, false) == 0
                  && TargetSelection.SectorKey(Vector3.Back * 5f, basis, false) == 1
                  && TargetSelection.SectorKey(Vector3.Left * 5f, basis, false) == 2
                  && TargetSelection.SectorKey(Vector3.Right * 5f, basis, false) == 3
                  && TargetSelection.SectorKey(Vector3.Right * 5f, basis, true) == -1,
            $"FUN_004bbd60's sectors: ahead 0, behind 1, left 2, right 3, and an objective overrides to -1");

        // An objective is hand-filed: nothing sets TargetRef.Objective yet, but the -1 key is the
        // cycle's first rule. The pool is driven directly, not through Rebuild, so the objective is
        // there on the FIRST resolve; pre-resolving would make the auto-acquire claim vacuous.
        var objective = new object();
        var sel = new TargetSelection();
        sel.Pool.Rebuild(full, null, own, null);
        sel.Pool.Add(TargetRef.ForStructure(
            new AimCandidate { Position = new Vector3(0f, 0f, 1500f), Team = foe, Live = true, Source = objective },
            TargetClass.Enemy, "Promised Land", "Zeppelin", "Destroy", objective: true));
        sel.Resolve(Vector3.Zero, basis);

        var order = sel.Ordered.Select(t => t.Source).ToList();
        ctx.Check(order.SequenceEqual(new[] { objective, ahead2, ahead1, behind, left, right }),
            $"the whole cycle order in one read: the objective first (1500 m BEHIND, and still first), then ahead nearest-first, then behind, left, right, the 100 m target off the right wing is LAST");
        ctx.Check(sel.Current is { } head && ReferenceEquals(head.Source, objective)
                  && sel.ActiveClass == TargetClass.Enemy,
            $"auto-acquire: a fresh selector starts on the Enemy cycle already holding its head, with no input");
        ctx.Check(!sel.Ordered.Any(t => ReferenceEquals(t.Source, ally)),
            $"…and the ally is not in the Enemy cycle at all");

        // Stepping.
        sel.Next(TargetClass.Enemy);
        ctx.Check(ReferenceEquals(sel.Current?.Source, objective),
            $"a handler mutates state only, Current still reads the old target until the next Resolve publishes it, which is the original's own one-frame shape");
        sel.Resolve(Vector3.Zero, basis);
        ctx.Check(ReferenceEquals(sel.Current?.Source, ahead2), $"Next steps one entry down the cycle");
        sel.Previous(TargetClass.Enemy);
        sel.Resolve(Vector3.Zero, basis);
        ctx.Check(ReferenceEquals(sel.Current?.Source, objective), $"Previous steps back up");
        sel.Previous(TargetClass.Enemy);
        sel.Resolve(Vector3.Zero, basis);
        ctx.Check(ReferenceEquals(sel.Current?.Source, right),
            $"…and wraps past the head to the tail");
        sel.Next(TargetClass.Enemy);
        sel.Resolve(Vector3.Zero, basis);
        ctx.Check(ReferenceEquals(sel.Current?.Source, objective), $"…and back again past the tail");

        // Nearest is the HEAD, not the nearest thing.
        sel.Next(TargetClass.Enemy);
        sel.Next(TargetClass.Enemy);
        sel.Resolve(Vector3.Zero, basis);
        ctx.Check(ReferenceEquals(sel.Current?.Source, ahead1), $"walked two down the cycle");
        sel.Nearest(TargetClass.Enemy);
        sel.Resolve(Vector3.Zero, basis);
        ctx.Check(ReferenceEquals(sel.Current?.Source, objective),
            $"'Nearest' returns to the HEAD of the cycle, which is 1500 m away, not the 100 m target off the wing");

        // Death: the selected target leaves the pool. It drops to the HEAD, not to its neighbour.
        sel.Next(TargetClass.Enemy);
        sel.Next(TargetClass.Enemy);
        sel.Resolve(Vector3.Zero, basis);
        ctx.Check(ReferenceEquals(sel.Current?.Source, ahead1), $"holding the third entry");
        var afterDeath = Scan(
            (ahead2, new Vector3(0f, 0f, -400f), foe),
            (behind, new Vector3(0f, 0f, 200f), foe),
            (left, new Vector3(-200f, 0f, 0f), foe),
            (right, new Vector3(100f, 0f, 0f), foe));
        sel.Rebuild(afterDeath, null, own, null, Vector3.Zero, basis);
        ctx.Check(ReferenceEquals(sel.Current?.Source, ahead2),
            $"the selected target dying drops to the HEAD of the cycle, never to the dead entry's neighbour (which would have been 'behind')");

        // The remake setting over the same rig, where the head and the nearest disagree: the head
        // is 400 m ahead and the nearest thing in space is 100 m off the right wing.
        var nearest = new TargetSelection { NearestAfterKill = true };
        nearest.Rebuild(full, null, own, null, Vector3.Zero, basis);
        ctx.Check(ReferenceEquals(nearest.Current?.Source, ahead2),
            $"CONTROL: with the setting on the mission-start acquire still takes the cycle's head, the decoded order");
        nearest.Next(TargetClass.Enemy);
        nearest.Resolve(Vector3.Zero, basis);
        ctx.Check(ReferenceEquals(nearest.Current?.Source, ahead1), $"holding the 900 m entry, the one that dies");
        nearest.Rebuild(afterDeath, null, own, null, Vector3.Zero, basis);
        ctx.Check(ReferenceEquals(nearest.Current?.Source, right)
                  && !ReferenceEquals(nearest.Current?.Source, sel.Current?.Source),
            $"…and the kill lands on the NEAREST of the cycle (100 m right), where the setting off landed on the head (400 m ahead)");
        nearest.Next(TargetClass.Ally);
        nearest.Rebuild(full, null, own, null, Vector3.Zero, basis);
        nearest.Next(TargetClass.Enemy);
        nearest.Rebuild(full, null, own, null, Vector3.Zero, basis);
        ctx.Check(nearest.ActiveClass == TargetClass.Enemy && ReferenceEquals(nearest.Current?.Source, ahead2),
            $"…while a class change back still lands on the HEAD of the cycle it changes to, not on the 100 m target, so the setting reaches nothing but the death re-resolve");

        // Stickiness: nothing but death, input and the explicit clear moves it.
        sel.Next(TargetClass.Enemy);
        sel.Resolve(Vector3.Zero, basis);
        ctx.Check(ReferenceEquals(sel.Current?.Source, behind), $"holding a mid-cycle entry");
        sel.Rebuild(afterDeath, null, own, null, Vector3.Zero, basis);
        ctx.Check(ReferenceEquals(sel.Current?.Source, behind),
            $"own respawn (a rebuild with the target still alive) preserves the live selection");
        var orderBefore = sel.Ordered.Select(t => t.Source).ToList();
        var farBasis = new Basis(Vector3.Up, Mathf.Pi * 0.75f);
        sel.Rebuild(afterDeath, null, own, null, new Vector3(4000f, 900f, -6000f), farBasis);
        ctx.Check(ReferenceEquals(sel.Current?.Source, behind)
                  && !sel.Ordered.Select(t => t.Source).SequenceEqual(orderBefore),
            $"flying 7 km away and swinging the nose onto a new bearing re-sorts the cycle but does NOT drop the selection, there is no range, bearing or LOS gate anywhere in the decoded path");

        // Target Nothing STAYS cleared.
        sel.Clear();
        ctx.Check(sel.Current == null && sel.ActiveClass == null, $"Target Nothing clears the target and every class flag");
        for (int i = 0; i < 3; i++)
        {
            sel.Rebuild(afterDeath, null, own, null, Vector3.Zero, basis);
        }

        ctx.Check(sel.Current == null && sel.Pool.Count == 0,
            $"…and STAYS cleared through repeated rebuilds, the collection pass is skipped, so the auto-acquire cannot fire again pool={sel.Pool.Count}");
        sel.Next(TargetClass.Enemy);
        sel.Rebuild(afterDeath, null, own, null, Vector3.Zero, basis);
        ctx.Check(sel.Current != null && sel.ActiveClass == TargetClass.Enemy,
            $"…until a class action presses, which is the only thing that ends the clear");

        // Nearest crosshairs: the NOSE cone, friend or foe, 2 km cap.
        var crosshair = new TargetSelection();
        var coneScan = Scan(
            (ally, new Vector3(0f, 0f, -300f), own),                 // dead ahead, friendly
            (right, new Vector3(100f, 0f, -100f), foe),              // 45° off the nose: outside 15°
            (ahead1, new Vector3(0f, 0f, -2400f), foe));             // on the nose but past 2 km
        crosshair.Rebuild(coneScan, null, own, null, Vector3.Zero, basis);
        ctx.Check(crosshair.NearestCrosshairs(Vector3.Zero, basis),
            $"nearest-crosshairs finds something in the cone");
        crosshair.Resolve(Vector3.Zero, basis);
        ctx.Check(crosshair.ActiveClass == TargetClass.Ally
                  && ReferenceEquals(crosshair.Current?.Source, ally),
            $"it reaches an ALLY 300 m dead ahead and writes the class back to Ally, so the next Next/Previous continues in that cycle");
        ctx.Check(!new TargetSelection().NearestCrosshairs(Vector3.Zero, basis),
            $"an empty pool finds nothing");
        var farOnly = new TargetSelection();
        farOnly.Rebuild(Scan((ahead1, new Vector3(0f, 0f, -2400f), foe)), null, own, null, Vector3.Zero, basis);
        ctx.Check(!farOnly.NearestCrosshairs(Vector3.Zero, basis),
            $"a target dead on the nose but past the hard 2 km cap is never picked");
        var offAxis = new TargetSelection();
        offAxis.Rebuild(Scan((right, new Vector3(100f, 0f, -100f), foe)), null, own, null, Vector3.Zero, basis);
        ctx.Check(!offAxis.NearestCrosshairs(Vector3.Zero, basis),
            $"a target 45° off the nose is outside the 15° half-angle cone, however close");

        // 0x24's attacker queue, walked backwards from the end.
        var shot = new TargetSelection();
        shot.Rebuild(full, null, own, null, Vector3.Zero, basis);
        shot.RecordAttacker(ahead1);
        shot.RecordAttacker(right);
        ctx.Check(shot.Attackers.Count == 2 && ReferenceEquals(shot.Attackers[1], right),
            $"the queue is an end insert, oldest first");
        shot.RecordAttacker(ahead1);
        ctx.Check(shot.Attackers.Count == 2 && ReferenceEquals(shot.Attackers[1], ahead1),
            $"a repeat attacker MOVES to the end rather than listing twice (inference, not decode, FUN_004bc1e0 was not traced)");
        shot.NextEnemy();
        shot.Resolve(Vector3.Zero, basis);
        ctx.Check(ReferenceEquals(shot.Current?.Source, ahead1),
            $"Next Enemy with a target not in the queue takes the MOST RECENT attacker, ignoring the ordinary cycle");
        shot.NextEnemy();
        shot.Resolve(Vector3.Zero, basis);
        ctx.Check(ReferenceEquals(shot.Current?.Source, right),
            $"…pressing again walks BACKWARDS to an older attacker");
        shot.NextEnemy();
        shot.Resolve(Vector3.Zero, basis);
        ctx.Check(ReferenceEquals(shot.Current?.Source, ahead2),
            $"…and from the queue's FIRST entry it falls through to an ordinary +1 step, which from the cycle's tail wraps to its head");
        shot.ForgetTarget(ahead1);
        ctx.Check(shot.Attackers.Count == 1 && ReferenceEquals(shot.Attackers[0], right),
            $"the death hook prunes the queue, so a dead shooter is never offered again");
    }

    // --target=. Everything the flag means lives in ApplyInitial and Select, which take a pose and
    // no tree, so the whole grammar is pinned here rather than only by the two screenshot runs. The
    // pool is built from REAL sources rather than hand-filed refs, because the claim is about the
    // names TargetPool actually produces.
    [Suite("target-flag",
        "the --target= scripted twin: the four words mapping onto the ordinary actions "
        + "(nearest as head-of-cycle, next, crosshair, none), a name pinning an aircraft the "
        + "auto-acquire would NOT have chosen, the same one grammar reaching an ally and a "
        + "zeppelin sub-part (under a selected torpedo) by writing the class back, "
        + "case-insensitive matching, an unknown "
        + "name leaving the selection alone, two selectors given one spec landing on the same "
        + "target, and the flag NOT pinning against later input")]
    internal static void TargetFlagModel(TestContext ctx)
    {
        int own = AimAssist.PlayerTeam;
        int foe = InstantActionRuntime.EnemyTeam;
        var basis = Basis.Identity;
        var self = new FlightController { Name = "player_fury", Team = own };
        var far = new FlightController { Name = "ai1_player_fury", IsHumanPiloted = false, Team = foe };
        var near = new FlightController { Name = "ai2_player_fury", IsHumanPiloted = false, Team = foe };
        var wing = new FlightController { Name = "wing1_kestrel", IsHumanPiloted = false, Team = own };
        var gasbagNode = new Node3D { Name = "gasbag1" };
        try
        {
            ctx.Host.AddChild(gasbagNode);
            var scan = new AimCandidateSet();
            scan.AddVehicle(Vector3.Zero, Vector3.Zero, own, live: true, self);
            scan.AddVehicle(new Vector3(0f, 0f, -900f), Vector3.Zero, foe, live: true, far);
            scan.AddVehicle(new Vector3(0f, 0f, -400f), Vector3.Zero, foe, live: true, near);
            scan.AddVehicle(new Vector3(0f, 0f, -300f), Vector3.Zero, own, live: true, wing);
            var registry = new DestructibleRegistry();
            var gasbagInst = registry.Register(
                new AnimDefinition { Name = "gasbag1", AnimName = "zep_zone_gasbag1" }, gasbagNode, 200f);
            var parts = new List<AimCandidate>
            {
                new() { Position = new Vector3(60f, 0f, -600f), Velocity = Vector3.Zero, Team = AimAssist.WorldTeam, Live = true, Source = gasbagInst },
            };
            // The sub-part channel's gate is the SELECTED ordnance carrying LOCK_ON, so every
            // rebuild here flies the torpedo; without it the gasbag reaches no cycle to be pinned.
            var torpedo = new WeaponDef { Id = "wep_14", LockOn = 2.5f };

            TargetSelection Fresh()
            {
                var s = new TargetSelection();
                s.Rebuild(scan, parts, own, self, Vector3.Zero, basis, null, torpedo);
                return s;
            }

            ctx.Check(SessionSpec.Parse(new[] { "--target=ai1_player_fury" }).TargetSelect == "ai1_player_fury"
                      && SessionSpec.Parse(System.Array.Empty<string>()).TargetSelect == null,
                $"--target= reaches the spec verbatim, and its absence is null rather than 'none', an unscripted session keeps the ordinary auto-acquire");

            // The auto-acquire picks the NEARER enemy ahead. Everything below that names the far one
            // is therefore a claim the flag actually moved the selection.
            var auto = Fresh();
            ctx.Check(ReferenceEquals(auto.Current?.Source, near),
                $"CONTROL: with no flag the pool auto-acquires the nearer enemy ahead, so pinning the far one cannot pass by coincidence");

            var pinned = Fresh();
            ctx.Check(pinned.ApplyInitial("ai1_player_fury", Vector3.Zero, basis)
                      && ReferenceEquals(pinned.Current?.Source, far)
                      && pinned.ActiveClass == TargetClass.Enemy,
                $"--target=<node name> pins the named aircraft, not the one the auto-acquire chose");
            var upper = Fresh();
            ctx.Check(upper.ApplyInitial("AI1_PLAYER_FURY", Vector3.Zero, basis)
                      && ReferenceEquals(upper.Current?.Source, far),
                $"…matched case-insensitively, so a shell's capitalisation cannot change what a golden shot frames");

            // One grammar reaches all three cycles, which is this item's open TODO settled: a
            // zeppelin sub-part is named by its own world node (TargetPool.NameOf -> Instance.Anchor),
            // exactly the shape an aircraft's name has, so no second grammar is needed for it.
            var ally = Fresh();
            ctx.Check(ally.ApplyInitial("wing1_kestrel", Vector3.Zero, basis)
                      && ReferenceEquals(ally.Current?.Source, wing)
                      && ally.ActiveClass == TargetClass.Ally,
                $"the same grammar reaches an ALLY, writing the class back, without that the next Resolve would drop a target outside the active cycle");
            var part = Fresh();
            ctx.Check(part.ApplyInitial("gasbag1", Vector3.Zero, basis)
                      && ReferenceEquals(part.Current?.Source, gasbagInst)
                      && part.Current?.Kind == AimTargetKind.Structure
                      && part.ActiveClass == TargetClass.NonAircraft,
                $"…and a zeppelin SUB-PART by its part node's name, on the Non-Aircraft cycle: the TODO's premise (a sub-part has no node name of an aircraft's shape) is wrong, so one grammar covers all three");

            // The four words, each mapping onto the ordinary action rather than a scripted path.
            var nearest = Fresh();
            nearest.Next(TargetClass.Enemy);      // walk off the head first, so returning to it means something
            nearest.Resolve(Vector3.Zero, basis);
            ctx.Check(ReferenceEquals(nearest.Current?.Source, far)
                      && nearest.ApplyInitial("nearest", Vector3.Zero, basis)
                      && ReferenceEquals(nearest.Current?.Source, near),
                $"--target=nearest is the original's Nearest action: the HEAD of the cycle");
            var next = Fresh();
            ctx.Check(next.ApplyInitial("next", Vector3.Zero, basis)
                      && ReferenceEquals(next.Current?.Source, far),
                $"--target=next steps the enemy cycle once from the auto-acquired head");
            var cross = Fresh();
            ctx.Check(cross.ApplyInitial("crosshair", Vector3.Zero, basis)
                      && ReferenceEquals(cross.Current?.Source, wing),
                $"--target=crosshair runs the nose-cone scan, which reaches the nearest thing on the nose whatever its side, here the ally at 300 m");
            var cleared = Fresh();
            ctx.Check(cleared.ApplyInitial("none", Vector3.Zero, basis)
                      && cleared.Current == null && cleared.ActiveClass == null,
                $"--target=none is Target Nothing: no target and no class");
            cleared.Rebuild(scan, parts, own, self, Vector3.Zero, basis, null, torpedo);
            ctx.Check(cleared.Current == null && cleared.Pool.Count == 0,
                $"…and stays cleared through the next rebuild, so a --screenshot run can capture the HUD with nothing selected");

            // A name nothing carries: the selection is left exactly as it was.
            var miss = Fresh();
            ctx.Check(!miss.ApplyInitial("ai7_nonesuch", Vector3.Zero, basis)
                      && ReferenceEquals(miss.Current?.Source, near),
                $"an unknown name reports failure and leaves the selection alone rather than clearing it");

            // Determinism, the flag's whole purpose: the claim the two screenshot runs make, made
            // here against two independently built selectors.
            var runA = Fresh();
            var runB = Fresh();
            runA.ApplyInitial("ai1_player_fury", Vector3.Zero, basis);
            runB.ApplyInitial("ai1_player_fury", Vector3.Zero, basis);
            ctx.Check(ReferenceEquals(runA.Current?.Source, runB.Current?.Source)
                      && ReferenceEquals(runA.Current?.Source, far),
                $"two selectors given the same spec land on the same target, the property a reproducible golden shot rests on");

            // ⚠ The item's own trap: the flag sets the INITIAL selection and must not hold it.
            var live = Fresh();
            live.ApplyInitial("ai1_player_fury", Vector3.Zero, basis);
            live.Next(TargetClass.Enemy);
            live.Resolve(Vector3.Zero, basis);
            ctx.Check(ReferenceEquals(live.Current?.Source, near),
                $"a keypress after the flag moves the selection off the pinned target");
            live.Rebuild(scan, parts, own, self, Vector3.Zero, basis, null, torpedo);
            ctx.Check(ReferenceEquals(live.Current?.Source, near),
                $"…and the next frame's rebuild does NOT snap back to it, the flag is spent, so an interactive session started with it still cycles");
        }
        finally
        {
            gasbagNode.Free();
            self.Free();
            far.Free();
            near.Free();
            wing.Free();
        }
    }

    // TargetPool. The pure half runs over a hand-built AimCandidateSet with no world, which pins
    // every membership and exclusion rule; the world half runs C1's REAL emplacement census through
    // the same pool, because "an emplacement is selectable" is a claim about objects the session
    // builds. The carried-gunner exclusion rides the turret-gunner suite, where one already exists.
    [Suite("target-pool",
        "the classed candidate pool: the three cycles built off the aim assist's own typed "
        + "lists. A wingman lands in Ally and an enemy in Enemy off the TEAM FIELD (never the "
        + "pilot-index derivation, which is the wingman-in-the-marker bug), the selecting plane "
        + "is excluded from its own pool, a dead plane and a destroyed zeppelin engine are "
        + "absent, the destructible registry contributes nothing however full "
        + "AimCandidateSet.Structures is, an ordnance entry with the admission byte clear is "
        + "refused (the TARGETABLE half is the shootable-flyout suite's), and a zeppelin "
        + "contributes one entry per gasbag/engine/cannon with its hull's velocity, but only "
        + "while the selected ordnance carries LOCK_ON (no ordnance and a plain rocket both "
        + "leave the cycle empty, and switching back brings the parts straight back); plus C1's "
        + "real emplacements, where the five aaguns switched on are live and hostile to the "
        + "player and still reach no cycle, since only a mission's target table puts a gun on one")]
    internal static void TargetPoolModel(TestContext ctx)
    {
        var self = new FlightController { PlayerIndex = 1, Team = AimAssist.PlayerTeam };
        var wingman = new FlightController { IsHumanPiloted = false, Team = AimAssist.PlayerTeam };
        var enemy = new FlightController { IsHumanPiloted = false, Team = InstantActionRuntime.EnemyTeam };
        var deadEnemy = new FlightController { IsHumanPiloted = false, Team = InstantActionRuntime.EnemyTeam };
        var neutral = new FlightController { IsHumanPiloted = false, Team = AimAssist.NeutralTeam };
        var crate = new Node3D { Name = "crate1" };
        var gasbag = new Node3D { Name = "gasbag1" };
        var deadEngine = new Node3D { Name = "engine2" };
        try
        {
            ctx.Host.AddChild(crate);
            ctx.Host.AddChild(gasbag);
            ctx.Host.AddChild(deadEngine);

            var scan = new AimCandidateSet();
            scan.AddVehicle(Vector3.Zero, Vector3.Zero, self.Team, live: true, self);
            scan.AddVehicle(new Vector3(0f, 0f, -100f), Vector3.Zero, wingman.Team, live: true, wingman);
            scan.AddVehicle(new Vector3(0f, 0f, -400f), Vector3.Zero, enemy.Team, live: true, enemy);
            scan.AddVehicle(new Vector3(0f, 0f, -450f), Vector3.Zero, deadEnemy.Team, live: false, deadEnemy);
            scan.AddVehicle(new Vector3(0f, 0f, -500f), Vector3.Zero, neutral.Team, live: true, neutral);

            // The registry, fed the way the GUN assist feeds it. The pool must ignore all of it.
            var registry = new DestructibleRegistry();
            var crateInst = registry.Register(
                new AnimDefinition { Name = "crate", AnimName = "crate_blow" }, crate, 30f);
            scan.AddStructures(registry);
            scan.AddOrdnance(new Vector3(0f, 0f, -50f), Vector3.Zero, InstantActionRuntime.EnemyTeam,
                new object());

            var pool = new TargetPool();
            pool.Rebuild(scan, null, self.Team, self);
            ctx.Check(pool.Enemy.Count == 1 && ReferenceEquals(pool.Enemy[0].Source, enemy),
                $"the hostile-team plane is the only Enemy entry count={pool.Enemy.Count}");
            ctx.Check(pool.Ally.Count == 2
                      && pool.Ally.Any(t => ReferenceEquals(t.Source, wingman))
                      && pool.Ally.Any(t => ReferenceEquals(t.Source, neutral)),
                $"a wingman lands in ALLY, not Enemy, and so does a neutral (either side unaffiliated is Ally) count={pool.Ally.Count}");
            ctx.Check(!pool.Enemy.Concat(pool.Ally).Concat(pool.NonAircraft)
                    .Any(t => ReferenceEquals(t.Source, self)),
                $"the selecting plane is excluded from its own pool");
            ctx.Check(!pool.Enemy.Any(t => ReferenceEquals(t.Source, deadEnemy)),
                $"a dead plane is listed by the collector but absent from the cycles");
            ctx.Check(pool.NonAircraft.Count == 0 && scan.Structures.Count == 1
                      && scan.Ordnance.Count == 1,
                $"the registry contributes NOTHING though it is populated, and neither does an ordnance entry carrying no flyout state structures={scan.Structures.Count} ordnance={scan.Ordnance.Count} nonAircraft={pool.NonAircraft.Count}");
            ctx.Check(!pool.Enemy.Concat(pool.Ally).Concat(pool.NonAircraft)
                    .Any(t => ReferenceEquals(t.Source, crateInst)),
                $"…and specifically the crate never becomes selectable (decision 8: ours would walk every crate and fence, the original's walks a curated targets.zrd list)");

            // The able-to-fail control: derive the side from the pilot index the way the HUD used
            // to, and P2's own wingman turns hostile. ⚠ The real enemy now stays in Enemy, where it
            // used to drop out, that derived side WAS EnemyTeam until the versus band (BL-403).
            pool.Rebuild(scan, null, AimAssist.TeamOfPilot(self.PlayerIndex), self);
            ctx.Check(pool.Enemy.Any(t => ReferenceEquals(t.Source, wingman))
                      && AimAssist.TeamOfPilot(self.PlayerIndex) != InstantActionRuntime.EnemyTeam,
                $"CONTROL: deriving P2's side from its pilot index puts the wingman in Enemy (the bug this item diagnosed) and no longer collides with the enemy team itself");

            // Sub-parts: the only channel by which a structure becomes selectable.
            var gasbagInst = registry.Register(
                new AnimDefinition { Name = "gasbag1", AnimName = "zep_zone_gasbag1" }, gasbag, 200f);
            var engineInst = registry.Register(
                new AnimDefinition { Name = "engine2", AnimName = "zep_zone_engine2" }, deadEngine, 100f);
            engineInst.Status = DestructibleRegistry.State.Destroyed;
            var hullVel = new Vector3(0f, 0f, -12f);
            var parts = new List<AimCandidate>
            {
                new() { Position = gasbag.GlobalPosition, Velocity = hullVel, Team = AimAssist.WorldTeam, Live = true, Source = gasbagInst },
                new() { Position = deadEngine.GlobalPosition, Velocity = hullVel, Team = AimAssist.WorldTeam, Live = false, Source = engineInst },
            };
            // The torpedo gate. A LOCK_ON weapon is the whole predicate, so a bare def carrying it
            // stands for wep_14 and one without stands for every gun and unguided rocket shipped.
            var torpedo = new WeaponDef { Id = "wep_14", LockOn = 2.5f };
            var plainRocket = new WeaponDef { Id = "wep_12" };
            pool.Rebuild(scan, parts, self.Team, self, null, torpedo);
            ctx.Check(pool.NonAircraft.Count == 1
                      && ReferenceEquals(pool.NonAircraft[0].Source, gasbagInst),
                $"a zeppelin contributes its live parts to Non-Aircraft while a LOCK_ON torpedo is selected count={pool.NonAircraft.Count}");
            ctx.Check(!pool.NonAircraft.Any(t => ReferenceEquals(t.Source, engineInst)),
                $"a DESTROYED engine is absent");
            var bag = pool.NonAircraft[0];
            ctx.Check(bag.Kind == AimTargetKind.Structure && bag.Velocity == hullVel
                      && bag.Name == "gasbag1"
                      && bag.Health is { } bh && Mathf.IsEqualApprox(bh, 1f) && bag.Armor == null,
                $"…carrying its hull's velocity (never zero, the bracket gate has to lead it), its part node's name and health with no armor pool name='{bag.Name}' v={bag.Velocity}");

            // Off the torpedo the cycle is the mission's flagged list alone, which here is empty.
            // The same parts list is passed every time, so an empty cycle is the gate refusing and
            // not a collector that stopped offering.
            pool.Rebuild(scan, parts, self.Team, self, null, plainRocket);
            ctx.Check(pool.NonAircraft.Count == 0,
                $"with a rocket authoring no LOCK_ON selected the parts leave the Non-Aircraft cycle count={pool.NonAircraft.Count}");
            pool.Rebuild(scan, parts, self.Team, self);
            ctx.Check(pool.NonAircraft.Count == 0,
                $"…and with no ordnance selected at all they are absent too count={pool.NonAircraft.Count}");
            pool.Rebuild(scan, parts, self.Team, self, null, torpedo);
            ctx.Check(pool.NonAircraft.Count == 1
                      && ReferenceEquals(pool.NonAircraft[0].Source, gasbagInst),
                $"…and selecting the torpedo again brings the same live part straight back, so the gate is the weapon and nothing else");

            // C1's real emplacements through the same pool. ia1.gw switches all 74 sites off at
            // their roots, so the built world has no live emplacement; the five aagun sites go on
            // by the .gw's own switch reversed, and off again in the finally (shared world cache).
            ctx.RequireData(ctx.ZrdrPath, $"zrdr archive");
            string texturesPath = SessionPaths.ChapterTextures(ctx.DataRoot, "C1");
            ctx.RequireData(texturesPath, $"C1 textures");
            var weapons = WeaponDefs.Load(ctx.ZrdrPath, null);
            var turretDefs = TurretDefs.Load(ctx.ZrdrPath);
            // Private: the assertions read every site as ia1.gw leaves it and then the five aaguns
            // alive, and a suite earlier in the shard (the self-fire drill) kills one in the shared cache.
            ctx.WithPrivateWorld("C1", collision: false, world =>
            {
                var textures = new TextureArchive(texturesPath);
                ProjectilePool? live = null;
                Session.World.TurretEmplacementRuntime? emplacements = null;
                var shown = new List<Node3D>();
                try
                {
                    live = new ProjectilePool(textures, null, null);
                    ctx.Host.AddChild(live);
                    emplacements = new Session.World.TurretEmplacementRuntime(turretDefs, weapons,
                        (pattern, scope) => world.Runtime.FindNodes(pattern, scope), live,
                        world.Runtime.WorldRoot);
                    // The runtime registers its own emplacements with the pool; a second
                    // RegisterWorldTurrets here would list every gun twice.
                    var worldScan = new AimCandidateSet();
                    live.CollectTurrets(worldScan);
                    var worldPool = new TargetPool();
                    worldPool.Rebuild(worldScan, null, AimAssist.PlayerTeam, null);
                    int aliveAtBuild = emplacements.Emplacements.Count(t => t.Alive);
                    ctx.Check(worldScan.Turrets.Count == emplacements.Count && emplacements.Count > 0,
                        $"C1's whole emplacement census reaches the scan turrets={worldScan.Turrets.Count} of {emplacements.Count}");
                    ctx.Check(aliveAtBuild == 0 && worldPool.NonAircraft.Count == 0,
                        $"every site ia1.gw switched off is listed by the scan but dead, and none is selectable alive={aliveAtBuild} nonAircraft={worldPool.NonAircraft.Count}");
                    var hostileGuns = worldScan.Turrets
                        .Where(c => AimAssist.Hostile(AimAssist.PlayerTeam, c.Team)).ToList();
                    ctx.Check(hostileGuns.Count > 0,
                        $"CONTROL: C1's census carries guns registered HOSTILE to the player, so a team test is not what keeps them off the cycle hostile={hostileGuns.Count} of {worldScan.Turrets.Count}");

                    var aaguns = emplacements.Emplacements
                        .Where(t => t.Label.StartsWith("MSG_TUR_AAA@aagun", System.StringComparison.Ordinal))
                        .ToList();
                    foreach (var site in aaguns.Select(t => t.Site).OfType<Node3D>().Distinct())
                    {
                        world.Runtime.SetTargetActive(site, true);
                        shown.Add(site);
                    }
                    worldScan.Clear();
                    live.CollectTurrets(worldScan);
                    worldPool.Rebuild(worldScan, null, AimAssist.PlayerTeam, null);
                    ctx.Check(aaguns.Count == 5 && aaguns.All(t => t.Alive),
                        $"switching the five aagun sites on brings their gunners alive alive={aaguns.Count(t => t.Alive)} of {aaguns.Count}");
                    int liveHostile = worldScan.Turrets.Count(c => c.Live
                        && AimAssist.Hostile(AimAssist.PlayerTeam, c.Team));
                    ctx.Check(liveHostile == aaguns.Count && worldPool.Count == 0,
                        $"a LIVE emplacement hostile to the player still reaches no cycle: the mission's target table is the only thing that puts a gun on one, and C1/IA1's names none live={liveHostile} pool={worldPool.Count}");
                    ctx.Note($"C1 target pool: {liveHostile} live hostile emplacement(s) of {emplacements.Count} placed once the aagun sites are on, and none of them selectable");
                }
                finally
                {
                    foreach (var site in shown)
                    {
                        world.Runtime.SetTargetActive(site, false);
                    }
                    emplacements?.Free();
                    live?.Free();
                    textures.Dispose();
                }
            });
        }
        finally
        {
            self.Free();
            wingman.Free();
            enemy.Free();
            deadEnemy.Free();
            neutral.Free();
            crate.Free();
            gasbag.Free();
            deadEngine.Free();
        }
    }

    // The targeting HUD on AI hostiles, in two halves. The pure selection (TargetHud.NearestHostile
    // over a constructed candidate set, no scene) pins the filters and HostileTag; the in-engine half
    // runs the tracker against real spawned AI planes in a live pool, covering acquisition, the
    // switch to a closer hostile, the crash drop and the empty-pool null. A hud built without a
    // pool never tracks, which is the seam that keeps the golden VS output untouched.
    [Suite("hostile-marker-hud",
        "the targeting HUD (TargetHud, every flight session): the tracker picks the " +
        "pane's nearest LIVE AI hostile off the pool's own aircraft roster (a closer human, " +
        "dead plane or neutral is never picked), switches to a closer hostile, drops a " +
        "crashed one, and a hud built without a pool never tracks; plus the F16 toggle, which " +
        "writes one answer to every pane at once and takes the press on a session with no pane " +
        "at all; plus --debug-markers' " +
        "own selection, which takes EVERY live aircraft instead of the nearest, flags each " +
        "by team against the pane's own, skips a crashed one and skips the pane's own " +
        "aircraft; plus the shipped marker's rules, the three decoded colours, the bracket " +
        "gate's gun reach (inside RANGE brackets, past it does not, a target outrunning the " +
        "round never does, and the hysteresis holds the boundary case), the label lines " +
        "an aircraft, an off-screen target and a named objective each compose, and the " +
        "off-screen geometry: off screen is the whole pane, then the 5 % anchor inset, the tip " +
        "on the pane edge with the shaft between them, the 20-by-10 head, and the label 3 " +
        "below or 45 above the anchor")]
    internal static void HostileMarkerHud(TestContext ctx)
    {
        ctx.RequireData(ctx.PlanesGamezPath, $"planes gamez");
        ctx.RequireData(ctx.ZrdrPath, $"zrdr archive");
        string texturesPath = SessionPaths.ChapterTextures(ctx.DataRoot, "C1");
        ctx.RequireData(texturesPath, $"C1 textures");

        // --- the pure selection, over a constructed candidate set (no tree, no pool) ---
        var pureNear = new FlightController { IsHumanPiloted = false };
        var pureFar = new FlightController { IsHumanPiloted = false };
        var pureDead = new FlightController { IsHumanPiloted = false };
        var pureHuman = new FlightController();
        try
        {
            int ownTeam = AimAssist.TeamOfPilot(0);
            var set = new AimCandidateSet();
            set.AddVehicle(new Vector3(0f, 0f, -50f), Vector3.Zero,
                AimAssist.TeamOfPilot(1), live: true, pureHuman); // a human: an opponent, never a hostile
            set.AddVehicle(new Vector3(0f, 0f, -100f), Vector3.Zero,
                AimAssist.TeamOfPilot(101), live: false, pureDead); // listed but not live (crashed)
            set.AddVehicle(new Vector3(0f, 0f, -10f), Vector3.Zero,
                AimAssist.NeutralTeam, live: true, pureNear); // neutral side rejects the pair
            set.AddVehicle(new Vector3(0f, 0f, -500f), Vector3.Zero,
                AimAssist.TeamOfPilot(100), live: true, pureNear);
            set.AddVehicle(new Vector3(0f, 0f, -2000f), Vector3.Zero,
                AimAssist.TeamOfPilot(102), live: true, pureFar);
            ctx.Check(ReferenceEquals(TargetHud.NearestHostile(Vector3.Zero, ownTeam, set), pureNear),
                $"the nearest LIVE AI hostile wins over a closer human, a closer dead plane and a closer neutral");
            ctx.Check(TargetHud.NearestHostile(Vector3.Zero, AimAssist.NeutralTeam, set) == null,
                $"a neutral own side targets nothing (the engine's either-side-0 rule)");
            ctx.Check(TargetHud.NearestHostile(Vector3.Zero, ownTeam, new AimCandidateSet()) == null,
                $"an empty scan tracks nothing");
            ctx.Check(TargetHud.HostileTag("ai1_player_fury") == "AI1"
                && TargetHud.HostileTag("bandit") == "BANDIT" && TargetHud.HostileTag("") == "AI",
                $"the marker tag is the name's first segment uppercased, 'AI' as the fallback");

            // The wingman-in-the-marker bug: OwnTeam must read the pane's own Team FIELD. The pilot
            // index derivation is right for P1 by coincidence and wrong for P2-P4 the moment a
            // mission sets teams, which every Instant Action and --coop session does.
            var p2 = new TargetHud { PlayerIndex = 1 };
            ctx.Check(p2.OwnTeam == AimAssist.TeamOfPilot(1),
                $"with no aircraft bound the HUD still falls back to the pilot-index derivation own={p2.OwnTeam}");
            p2.Own = pureHuman;
            pureHuman.Team = AimAssist.PlayerTeam;
            ctx.Check(p2.OwnTeam == AimAssist.PlayerTeam
                      && AimAssist.TeamOfPilot(1) != AimAssist.PlayerTeam,
                $"P2 flying an Instant Action mission is on the PLAYER team, which its pilot index would have derived as {AimAssist.TeamOfPilot(1)}, the wingman-in-the-marker bug");
            var wingScan = new AimCandidateSet();
            wingScan.AddVehicle(new Vector3(0f, 0f, -80f), Vector3.Zero, AimAssist.PlayerTeam,
                live: true, pureNear);                                  // P2's own wingman
            wingScan.AddVehicle(new Vector3(0f, 0f, -900f), Vector3.Zero,
                InstantActionRuntime.EnemyTeam, live: true, pureFar);   // the actual enemy
            ctx.Check(ReferenceEquals(
                    TargetHud.NearestHostile(Vector3.Zero, p2.OwnTeam, wingScan), pureFar),
                $"…so the far ENEMY is tracked and the near wingman is not");
            ctx.Check(ReferenceEquals(
                    TargetHud.NearestHostile(Vector3.Zero, AimAssist.TeamOfPilot(1), wingScan),
                    pureNear),
                $"CONTROL: the old derivation tracks the WINGMAN instead, and skips the enemy as own-team");
            p2.Free();

            // F16, the markers HUD's own key: the flag sets MarkAll once at build, the key flips it
            // mid-flight. Two panes, because the toggle is session-level precisely so a splitscreen
            // pane, whose own nodes never see a key, is not left behind.
            var pane1 = new TargetHud { PlayerIndex = 0 };
            var pane2 = new TargetHud { PlayerIndex = 1, MarkAll = true };
            var both = new List<TargetHud> { pane1, pane2 };
            var f16 = new UI.Overlays.DebugMarkerToggle(() => both);
            f16.Toggle();
            ctx.Check(pane1.MarkAll && pane2.MarkAll,
                $"F16 switches the all-aircraft markers on across every pane p1={pane1.MarkAll} p2={pane2.MarkAll}");
            f16.Toggle();
            ctx.Check(!pane1.MarkAll && !pane2.MarkAll,
                $"and a second press switches them off again p1={pane1.MarkAll} p2={pane2.MarkAll}");
            // A press with no pane to mark changes nothing and latches no state of its own. A pane
            // can join the session after it, a splitscreen pane built late.
            var latePanes = new List<TargetHud>();
            var noPanes = new UI.Overlays.DebugMarkerToggle(() => latePanes);
            noPanes.Toggle();
            ctx.Check(latePanes.Count == 0,
                $"a session with no flight pane still has none after the press panes={latePanes.Count}");
            var pane3 = new TargetHud { PlayerIndex = 0 };
            latePanes.Add(pane3);
            noPanes.Toggle();
            ctx.Check(pane3.MarkAll,
                $"and the pane that joins afterwards takes the next press on={pane3.MarkAll}");
            f16.Free();
            noPanes.Free();
            pane1.Free();
            pane2.Free();
            pane3.Free();
        }
        finally
        {
            pureNear.Free();
            pureFar.Free();
            pureDead.Free();
            pureHuman.Free();
        }

        // --- The shipped marker's rules: colour, the gun-reach bracket gate, the label lines -----
        // All three are pure and decoded (FUN_004a5f40 / FUN_004574d0 / FUN_004579e0); what no test
        // can reach is the drawn geometry itself, which is the c1-flight-kill golden.
        int hostileTeam = AimAssist.TeamOfPilot(100);
        var enemyRef = TargetRef.ForAircraft(
            new AimCandidate { Team = hostileTeam, Live = true, Source = new object() },
            TargetClass.Enemy, "ai1_player_fury", "Fury");
        var allyRef = TargetRef.ForAircraft(
            new AimCandidate { Team = AimAssist.PlayerTeam, Live = true, Source = new object() },
            TargetClass.Ally, "ai2_player_kestrel", "Kestrel");
        var destroyRef = TargetRef.ForStructure(
            new AimCandidate { Team = AimAssist.WorldTeam, Live = true, Source = new object() },
            TargetClass.Enemy, "Promised Land", "Zeppelin", "Destroy", objective: true);
        var protectRef = TargetRef.ForStructure(
            new AimCandidate { Team = AimAssist.PlayerTeam, Live = true, Source = new object() },
            TargetClass.Enemy, "Convoy", "Freighter", "Protect", objective: true);
        ctx.Check(TargetHud.MarkerColor(enemyRef, AimAssist.PlayerTeam)
                  != TargetHud.MarkerColor(allyRef, AimAssist.PlayerTeam)
                  && TargetHud.MarkerColor(allyRef, AimAssist.PlayerTeam)
                     != TargetHud.MarkerColor(protectRef, AimAssist.PlayerTeam),
            $"three colours, not two: a hostile, a friendly and a non-destructive objective all read differently (the decode's own rule, a FRIENDLY is green, blue is the objective)");
        ctx.Check(TargetHud.MarkerColor(destroyRef, AimAssist.PlayerTeam)
                  == TargetHud.MarkerColor(enemyRef, AimAssist.PlayerTeam),
            $"a Destroy objective is the hostile colour, whatever team the entity carries, the four destructive categories override the team test");
        ctx.Check(TargetHud.MarkerColor(enemyRef, AimAssist.NeutralTeam)
                  == TargetHud.MarkerColor(allyRef, AimAssist.PlayerTeam),
            $"a neutral own side has no enemies: with either team 0 the categoryless rule falls to the friendly colour");

        // The gate is the SELECTED GUN's reach through a lead solve, not a distance constant. A
        // 860 m/s round with RANGE 1000 reaches ~1 km; the same target 2 km out does not.
        const float RoundSpeed = 860f, GunRange = 1000f;
        var muzzle = Vector3.Zero;
        ctx.Check(TargetHud.GunReaches(muzzle, Vector3.Zero, RoundSpeed, GunRange,
                      new Vector3(0f, 0f, -600f), Vector3.Zero)
                  && !TargetHud.GunReaches(muzzle, Vector3.Zero, RoundSpeed, GunRange,
                      new Vector3(0f, 0f, -2000f), Vector3.Zero),
            $"a target inside the gun's authored RANGE is bracketed and one past it is not");
        ctx.Check(!TargetHud.GunReaches(muzzle, Vector3.Zero, RoundSpeed, GunRange,
                      new Vector3(0f, 0f, -600f), new Vector3(0f, 0f, -900f)),
            $"a target OUTRUNNING the round is never bracketed, at any range, the solver returns no intercept (the port of 'a fixed-metres threshold would lose this')");
        ctx.Check(!TargetHud.GunReaches(muzzle, Vector3.Zero, RoundSpeed, GunRange,
                      new Vector3(0f, 0f, -1010f), Vector3.Zero)
                  && TargetHud.GunReaches(muzzle, Vector3.Zero, RoundSpeed,
                      GunRange + TargetHud.BracketHysteresis, new Vector3(0f, 0f, -1010f),
                      Vector3.Zero),
            $"the hysteresis is what a target hovering at the boundary rides: off by the plain gate, still on by the widened one (TUNE, ours not the original's)");

        var lines = new List<string>();
        TargetHud.LabelLines(enemyRef, null, lines);
        ctx.Check(lines.Count == 2 && lines[0].Length == 0 && lines[1] == "Fury",
            $"an ordinary aircraft under a box is its airframe name alone, in the SECOND slot, the blank category line still holds the first, which is what keeps the name out of the silhouette ({string.Join(" / ", lines)})");
        lines.Clear();
        TargetHud.LabelLines(enemyRef, "4 o'clock", lines, keepSlots: false);
        ctx.Check(lines.Count == 2 && lines[0] == "Fury" && lines[1] == "4 o'clock",
            $"…and off screen, where there is no box to measure the slot from, it compacts to the two lines HUD.png shows ({string.Join(" / ", lines)})");
        lines.Clear();
        TargetHud.LabelLines(destroyRef, null, lines);
        ctx.Check(lines.Count == 2 && lines[0] == "Zeppelin [Destroy] -"
                  && lines[1] == "Promised Land",
            $"a named objective composes both lines, C1 M04 Zeppelin.png's case, with no wrap width to port ({string.Join(" / ", lines)})");

        // --- The off-screen marker's geometry, the decode's own (docs/org/spyglass.md) -----------
        // Off screen is the whole pane; only then does the anchor clamp into the 5 % inset and the
        // arrow's tip to the pane itself, the shaft spanning the band between them.
        var pane = new Vector2(2560f, 1440f);
        var offRight = EdgeMarker.Resolve(new Vector2(9000f, 720f), behind: false, pane);
        ctx.Check(!offRight.OnScreen
                  && Mathf.IsEqualApprox(offRight.Anchor.X, pane.X * 0.95f)
                  && Mathf.IsEqualApprox(offRight.Tip.X, pane.X - 1.001f)
                  && Mathf.IsEqualApprox(offRight.Anchor.Y, 720f),
            $"a target far off the right sits its anchor on the 5 % inset ({offRight.Anchor}) and its arrow tip on the pane's own edge ({offRight.Tip}), the two ends of the shaft");
        var inBand = EdgeMarker.Resolve(new Vector2(2500f, 720f), behind: false, pane);
        ctx.Check(inBand.OnScreen && inBand.Anchor == new Vector2(2500f, 720f)
                  && !EdgeMarker.Resolve(new Vector2(2561f, 720f), behind: false, pane).OnScreen,
            $"CONTROL: a target inside the 5 % band but still on the pane reads ON screen ({inBand.Anchor}), since the original's off-screen flag is the viewport, and the first point past the pane's edge does not");

        var (headPoint, headLeft, headRight) = TargetHud.ArrowHead(offRight.Tip, offRight.Dir, 1f);
        ctx.Check(headPoint == offRight.Tip
                  && Mathf.IsEqualApprox(headPoint.DistanceTo((headLeft + headRight) / 2f), 20f)
                  && Mathf.IsEqualApprox(headLeft.DistanceTo(headRight), 10f),
            $"the head is the decoded 20 back along the bearing and 10 across its base, whatever the shaft does");

        var upperLabel = TargetHud.EdgeLabelAnchor(new Vector2(200f, 300f), pane.Y, 1f);
        var lowerLabel = TargetHud.EdgeLabelAnchor(new Vector2(200f, 1100f), pane.Y, 1f);
        ctx.Check(upperLabel == new Vector2(200f, 303f) && lowerLabel == new Vector2(200f, 1055f),
            $"the label anchor keeps the marker's x and drops 3 below it in the pane's upper half ({upperLabel}), lifting 45 above it in the lower ({lowerLabel}), with no back-off along the arrow");

        // --- The debug-marker string: identity kept whole, health/armor gated on the source -------
        var healthyRef = TargetRef.ForAircraft(
            new AimCandidate { Team = hostileTeam, Live = true, Source = new object() },
            TargetClass.Enemy, "ai1_player_fury", "Fury",
            TargetRef.Fraction(78f, 100f), TargetRef.Fraction(91f, 100f));
        string healthyTag = TargetHud.DebugTag("AI1", healthyRef, 640f, "");
        ctx.Check(healthyTag == "AI1 Fury 640 m H78 A91",
            $"the debug tag keeps the FULL identity (unlike the shipped marker's plane-type-alone label), adds the plane type, then health and armor as whole percentages with no percent sign, health first: '{healthyTag}'");
        ctx.Check(TargetHud.DebugTag("AI1", healthyRef, 640f, "  pursue") == "AI1 Fury 640 m H78 A91  pursue",
            $"the AI mode trails the figures, carrying ModeSuffix's own leading spaces: '{TargetHud.DebugTag("AI1", healthyRef, 640f, "  pursue")}'");
        string noHealthTag = TargetHud.DebugTag("AI2", enemyRef, 250f, "");
        ctx.Check(noHealthTag == "AI2 Fury 250 m",
            $"a source with no health model (enemyRef carries none) omits BOTH figures rather than printing H100 A100: '{noHealthTag}'");

        // --- the live tracker, against real AI planes registered in a real pool ---
        var planesGamez = GameZ.Load(ctx.PlanesGamezPath);
        var stats = PlaneStats.Load(ctx.ZrdrPath, ctx.PlaneName);
        var textures = new TextureArchive(texturesPath);
        ProjectilePool? pool = null;
        FlightController? ai1 = null, ai2 = null;
        TargetHud? hud = null, noPoolHud = null;
        try
        {
            var live = new ProjectilePool(textures, null, null);
            pool = live;
            ctx.Host.AddChild(live);

            FlightController SpawnAi(int index, Vector3 pos)
            {
                var model = new PlaneBuilder(planesGamez, textures).Build(ctx.PlaneName);
                var fc = new FlightController
                {
                    PlaneModel = model,
                    Collider = PlaneCollider.Build(model),
                    PlayerIndex = FlightRoster.ShooterIdBase + index,
                    IsHumanPiloted = false,
                    Pilot = AiPilot.HoldingCourse(pos, pos + Vector3.Forward),
                    Projectiles = live,
                    UseKeyboard = false,
                    PadDevices = System.Array.Empty<int>(),
                    AllowPause = false,
                };
                fc.AddChild(model);
                fc.Setup(new FlightModel(stats), null, new CamParams(), pos, pos + Vector3.Forward);
                fc.Name = $"ai{index + 1}_{ctx.PlaneName}";
                ctx.Host.AddChild(fc);
                return fc;
            }

            ai1 = SpawnAi(0, new Vector3(0f, 500f, -800f));
            hud = TargetHud.Build(0, ctx.Camera, live);
            hud.PlanePos = new Vector3(0f, 500f, 0f);
            hud.UpdateHostile();
            ctx.Check(ReferenceEquals(hud.TrackedHostile, ai1),
                $"a plain-session pane acquires the spawned AI plane tracked={hud.TrackedHostile?.Name ?? "-"}");

            // A closer hostile joining the pool takes the marker over on the next update; the
            // per-frame nearest re-select is also how generators' runtime spawns appear.
            ai2 = SpawnAi(1, new Vector3(0f, 500f, -300f));
            hud.UpdateHostile();
            ctx.Check(ReferenceEquals(hud.TrackedHostile, ai2),
                $"a closer hostile takes the marker over tracked={hud.TrackedHostile?.Name ?? "-"}");

            // A crashed hostile is still registered but no longer live: it drops cleanly and
            // the next nearest takes over; with every hostile down the pane tracks nothing.
            ai2.DebugForceCrash();
            hud.UpdateHostile();
            ctx.Check(ReferenceEquals(hud.TrackedHostile, ai1),
                $"a crashed hostile drops and the next nearest takes over tracked={hud.TrackedHostile?.Name ?? "-"}");
            ai1.DebugForceCrash();
            hud.UpdateHostile();
            ctx.Check(hud.TrackedHostile == null,
                $"with every hostile down the pane tracks nothing");

            // A hud with no pool bound (HostilePool left null) never tracks whatever the pool
            // holds, the seam that keeps a golden shot with no hostile in play untouched.
            ai1.Respawn();
            noPoolHud = new TargetHud();
            noPoolHud.PlanePos = hud.PlanePos;
            noPoolHud.UpdateHostile();
            ctx.Check(noPoolHud.TrackedHostile == null,
                $"a hud built without a pool never tracks");

            // --debug-markers' own selection: EVERY live aircraft, not the nearest one, each
            // flagged by team against the pane's own. ai1 is live again (respawned above); ai2 is
            // still down, so it must not be marked at all.
            ai2.Team = AimAssist.PlayerTeam;
            var scan = new AimCandidateSet();
            live.CollectAircraft(scan);
            var marks = new List<(TargetRef Target, bool Friendly)>();
            TargetHud.CollectMarks(AimAssist.PlayerTeam, null, scan, marks);
            ctx.Check(marks.Count == 1 && ReferenceEquals(marks[0].Target.Source, ai1),
                $"a crashed plane is never marked marks={marks.Count}");
            ctx.Check(!marks[0].Friendly,
                $"ai1 is on the enemy team, so it marks hostile friendly={marks[0].Friendly}");
            string airframeName = PlaneRoster.PlaneDisplayName(stats);
            ctx.Check(marks[0].Target.DisplayName == airframeName
                      && marks[0].Target.Health == null && marks[0].Target.Armor == null,
                $"CollectMarks wraps a TargetRef carrying the airframe's display name, and a bare rig with no Damage ledger bound omits both figures: name={marks[0].Target.DisplayName} h={marks[0].Target.Health} a={marks[0].Target.Armor}");
            ai1.Team = AimAssist.PlayerTeam;
            marks.Clear();
            scan.Clear();
            live.CollectAircraft(scan);
            TargetHud.CollectMarks(AimAssist.PlayerTeam, null, scan, marks);
            ctx.Check(marks.Count == 1 && marks[0].Friendly,
                $"the same plane on the pane's own team marks friendly friendly={marks[0].Friendly}");
            marks.Clear();
            TargetHud.CollectMarks(AimAssist.PlayerTeam, ai1, scan, marks);
            ctx.Check(marks.Count == 0, $"the pane's own aircraft is excluded marks={marks.Count}");

            // Once the plane carries a damage ledger, CollectMarks' TargetRef reads it straight
            // off, the same optional-field contract a sub-part or emplacement would use once the
            // scan widens past aircraft, not a plane-specific field read of its own.
            ai1.Damage = new PlaneDamage(stats.DestroyableParts);
            var firstPart = stats.DestroyableParts.First();
            ai1.Damage.Apply(firstPart.Name, healthDamage: 5f, armorDamage: 5f);
            marks.Clear();
            scan.Clear();
            live.CollectAircraft(scan);
            TargetHud.CollectMarks(AimAssist.PlayerTeam, null, scan, marks);
            float expectHealth = TargetRef.Fraction(ai1.Damage.WholeHealth, ai1.Damage.WholeHealthMax)!.Value;
            float expectArmor = TargetRef.Fraction(ai1.Damage.WholeArmor, ai1.Damage.WholeArmorMax)!.Value;
            ctx.Check(marks.Count == 1 && marks[0].Target.Health == expectHealth
                      && marks[0].Target.Armor == expectArmor,
                $"a damaged plane's health/armor fractions come straight off its own Damage ledger h={marks[0].Target.Health} a={marks[0].Target.Armor} (expected h={expectHealth} a={expectArmor})");
            string damagedTag = TargetHud.DebugTag(TargetHud.HostileTag(marks[0].Target.Name),
                marks[0].Target, 640f, "");
            ctx.Check(damagedTag
                      == $"AI1 {airframeName} 640 m H{Mathf.RoundToInt(expectHealth * 100f)} A{Mathf.RoundToInt(expectArmor * 100f)}",
                $"the full string a live damaged plane produces: '{damagedTag}'");

            // The mode suffix the marker tag carries, in the engine's own vocabulary. A pilot
            // with no mode machine (this suite's own bare-orders spawn) adds nothing rather than
            // inventing a state; armed, it names whatever mode the machine is in.
            ctx.Check(TargetHud.ModeSuffix(ai1) == "",
                $"a pilot with no mode machine adds nothing to the tag: '{TargetHud.ModeSuffix(ai1)}'");
            ai1.Pilot!.Machine = new AiModeMachine(new System.Random(5));
            ai1.Pilot.Machine.Enter(AiMode.Pursue, "suite");
            ctx.Check(TargetHud.ModeSuffix(ai1).Trim() == "pursue",
                $"the marker tag carries the plane's mode: '{TargetHud.ModeSuffix(ai1).Trim()}'");
        }
        finally
        {
            hud?.Free();
            noPoolHud?.Free();
            ai1?.Free();
            ai2?.Free();
            pool?.Free();
            textures.Dispose();
        }
    }

    // The spyglass, in two halves. The placement half is pure (EdgeMarker's anchor inset,
    // TargetHud.ShaftTail and the label block's disc variant) and runs over a bare pane; the gate
    // half drives a real TargetHud with its real SubViewport over a spawned AI plane, so the
    // picture's presence, the fog-derived range band and the arming toggle are witnessed rather
    // than argued. Every gate check carries its own control, since a gate that is always open and
    // a gate that is always shut both pass a one-sided test.
    [Suite("spyglass-marker-hud",
        "the spyglass (TargetHud + Spyglass + SpyglassView, every flight session): the round "
        + "picture is up only while it is armed, the selected target is off screen and its slant "
        + "range is inside the fog-derived gate, and the gate is wider while a subject is already "
        + "held, so the picture engages nearer than it releases; a wider fog band opens it further "
        + "out, up to the 2000 m cap; its eye stands on the pose the aeroplane is drawn at rather "
        + "than the sim pose the gates read, the hud runs after the flight rigs so its aim reads the "
        + "target's drawn pose of the same frame, and its camera drops the one layer that aeroplane is "
        + "drawn on and no other; plus the three placement rules the disc brings, half the "
        + "window added to the anchor's inset with the tip and the on-screen test untouched, the "
        + "shaft starting on the rim, and the label 3 under the disc's bottom or 45 over its top")]
    internal static void SpyglassMarkerHud(TestContext ctx)
    {
        ctx.RequireData(ctx.PlanesGamezPath, $"planes gamez");
        ctx.RequireData(ctx.ZrdrPath, $"zrdr archive");
        string texturesPath = SessionPaths.ChapterTextures(ctx.DataRoot, "C1");
        ctx.RequireData(texturesPath, $"C1 textures");

        // --- the placement rules, pure over a 1440p pane at scale 1 ------------------------------
        var pane = new Vector2(2560f, 1440f);
        float radius = Spyglass.RefRadius;
        var far = new Vector2(9000f, 720f);
        var bare = EdgeMarker.Resolve(far, behind: false, pane);
        var withDisc = EdgeMarker.Resolve(far, behind: false, pane, radius);
        ctx.Check(Mathf.IsEqualApprox(withDisc.Anchor.X, bare.Anchor.X - radius)
                  && withDisc.Tip == bare.Tip && withDisc.Dir == bare.Dir,
            $"the picture pulls the anchor half a window further in ({bare.Anchor} to {withDisc.Anchor}) and moves neither the arrow's tip nor its bearing, so the disc sits inside the pane instead of half over its edge");
        var justInside = new Vector2(pane.X * 0.06f, 720f);
        ctx.Check(EdgeMarker.Resolve(justInside, false, pane, radius).OnScreen
                  && EdgeMarker.Resolve(justInside, false, pane).OnScreen,
            $"CONTROL: the extra inset moves the ANCHOR only; a point just inside the 5 % rect still reads on screen, because the original's off-screen flag is the whole viewport and not the disc's own margin");

        ctx.Check(TargetHud.ShaftTail(withDisc.Anchor, withDisc.Dir, 1f, disc: true)
                      == withDisc.Anchor + (withDisc.Dir * radius)
                  && TargetHud.ShaftTail(withDisc.Anchor, withDisc.Dir, 1f, disc: false)
                      == withDisc.Anchor,
            $"the shaft starts on the rim once the picture is up ({TargetHud.ShaftTail(withDisc.Anchor, withDisc.Dir, 1f, true)}) and on the bare anchor otherwise ({withDisc.Anchor}), so it leaves the disc rather than crossing it");

        var high = new Vector2(400f, 300f);
        var low = new Vector2(400f, 1380f);
        ctx.Check(TargetHud.EdgeLabelAnchor(high, pane.Y, 1f, disc: true) == new Vector2(400f, 351f)
                  && TargetHud.EdgeLabelAnchor(low, pane.Y, 1f, disc: true) == new Vector2(400f, 1287f),
            $"the label block measures against the PICTURE: 3 under the disc's bottom high on the pane ({TargetHud.EdgeLabelAnchor(high, pane.Y, 1f, true)}), and 45 over its top where the three lines would fall off the bottom ({TargetHud.EdgeLabelAnchor(low, pane.Y, 1f, true)})");
        ctx.Check(TargetHud.EdgeLabelAnchor(high, pane.Y, 1f) == new Vector2(400f, 303f)
                  && TargetHud.EdgeLabelAnchor(low, pane.Y, 1f) == new Vector2(400f, 1335f),
            $"CONTROL: with no picture the same two anchors take the bare rule, 3 below and 45 above the marker itself ({TargetHud.EdgeLabelAnchor(high, pane.Y, 1f)} / {TargetHud.EdgeLabelAnchor(low, pane.Y, 1f)}), which is what the disc variant has to differ from");

        // --- the gate, against a real hud, a real SubViewport and a spawned plane ----------------
        var planesGamez = GameZ.Load(ctx.PlanesGamezPath);
        var stats = PlaneStats.Load(ctx.ZrdrPath, ctx.PlaneName);
        var textures = new TextureArchive(texturesPath);
        ProjectilePool? pool = null;
        FlightController? bogey = null;
        TargetHud? hud = null;
        try
        {
            var live = new ProjectilePool(textures, null, null);
            pool = live;
            ctx.Host.AddChild(live);

            var model = new PlaneBuilder(planesGamez, textures).Build(ctx.PlaneName);
            bogey = new FlightController
            {
                PlaneModel = model,
                Collider = PlaneCollider.Build(model),
                PlayerIndex = FlightRoster.ShooterIdBase,
                IsHumanPiloted = false,
                Pilot = AiPilot.HoldingCourse(Vector3.Zero, Vector3.Forward),
                Projectiles = live,
                UseKeyboard = false,
                PadDevices = System.Array.Empty<int>(),
                AllowPause = false,
                Name = "ai1_bogey",
            };
            bogey.AddChild(model);
            bogey.Setup(new FlightModel(stats), null, new CamParams(), Vector3.Zero, Vector3.Forward);
            ctx.Host.AddChild(bogey);

            hud = TargetHud.Build(0, ctx.Camera, live);
            ctx.Host.AddChild(hud);
            // One frame cannot show the aim a step off the drawn target, so the order is asserted.
            ctx.Check(hud.ProcessPriority > bogey.ProcessPriority,
                $"the hud runs after the flight rigs (priority {hud.ProcessPriority} against a rig's {bogey.ProcessPriority}), so the picture aims at the pose the target is drawn at this frame");
            hud.Size = pane;
            hud.PlanePos = Vector3.Zero;
            // A 1 km fog band puts the release gate at 800 m and the engage gate at 700.
            hud.FogRange = () => new Vector2(0f, 1000f);
            var bogeyRef = TargetRef.ForAircraft(
                new AimCandidate { Team = AimAssist.TeamOfPilot(100), Live = true, Source = bogey },
                TargetClass.Enemy, bogey.Name, "Bogey");
            Vector3 At(float metres) => new(0f, 0f, -metres);

            ctx.Check(hud.SpyglassOn, $"the spyglass is armed at level load, with no toggle pressed");
            ctx.Check(hud.UpdateSpyglass(bogeyRef, At(600f), offScreen: true) && hud.DiscShown,
                $"an off-screen target 600 m out is inside the 700 m engage gate, so the picture comes up");
            ctx.Check(hud.PictureLive,
                $"…and the picture's own viewport is rendering rather than only the flag being set");

            // The asymmetry: 750 m is past the engage gate and inside the release gate, so a
            // subject already held stays held where the same subject freshly acquired would not.
            ctx.Check(hud.UpdateSpyglass(bogeyRef, At(750f), offScreen: true),
                $"a HELD subject drifting out to 750 m stays in the picture, on the wider release gate");
            ctx.Check(!hud.UpdateSpyglass(bogeyRef, At(850f), offScreen: true) && !hud.DiscShown
                      && !hud.PictureLive,
                $"past the 800 m release gate the picture drops and its viewport goes idle");
            ctx.Check(!hud.UpdateSpyglass(bogeyRef, At(750f), offScreen: true),
                $"CONTROL: the same 750 m that HELD does not ENGAGE, which is the pair that keeps the picture from strobing at the boundary");

            // The band itself: a thicker fog opens the gate further out, up to the 2000 m cap.
            ctx.Check(!hud.UpdateSpyglass(bogeyRef, At(1200f), offScreen: true),
                $"1200 m is well past the 1 km band's gate");
            hud.FogRange = () => new Vector2(0f, 3000f);
            ctx.Check(hud.UpdateSpyglass(bogeyRef, At(1200f), offScreen: true),
                $"…and the SAME range engages once the zone's fog band reaches 3 km, so the gate is read off the live weather rather than a constant");
            ctx.Check(hud.UpdateSpyglass(bogeyRef, At(1900f), offScreen: true)
                      && !hud.UpdateSpyglass(bogeyRef, At(2100f), offScreen: true),
                $"the 3 km band's own gate would sit at 2400 m, but the 2000 m ceiling binds first: a held subject at 1900 m is still in the picture and one at 2100 m is not");

            // The other two gates, each with the picture otherwise engaged.
            hud.FogRange = () => new Vector2(0f, 1000f);
            ctx.Check(!hud.UpdateSpyglass(bogeyRef, At(600f), offScreen: false) && !hud.DiscShown,
                $"a target ON screen is watched with the eye, not the spyglass");
            hud.SpyglassOn = false;
            ctx.Check(!hud.UpdateSpyglass(bogeyRef, At(600f), offScreen: true),
                $"disarmed, the same off-screen target 600 m out shows no picture");
            hud.SpyglassOn = true;
            ctx.Check(hud.UpdateSpyglass(bogeyRef, At(600f), offScreen: true) && hud.PictureLive,
                $"CONTROL: re-armed, it comes straight back, so the toggle is what shut it and not one of the other gates");
            // Where the eye stands. The drawn pose and the sim pose are set 100 m and 30 degrees
            // apart, which no single physics tick would ever produce, so the answer names one of
            // them instead of measuring a tick's worth of difference.
            var drawn = new Transform3D(new Basis(Vector3.Forward, Mathf.DegToRad(30f)),
                new Vector3(0f, 100f, 0f));
            hud.RenderPose = drawn;
            ctx.Check(hud.UpdateSpyglass(bogeyRef, At(600f), offScreen: true)
                      && hud.Picture is { } aimed && aimed.Eye.Origin.IsEqualApprox(drawn.Origin)
                      && aimed.Eye.Basis.Y.Dot(drawn.Basis.Y) > aimed.Eye.Basis.Y.Dot(Vector3.Up),
                $"the picture's eye stands on the pose the aeroplane is DRAWN at ({hud.Picture?.Eye.Origin}) and leans with it, so it cannot shake against a frame drawn from that same pose");
            ctx.Check(hud.Picture is { } notSim && !notSim.Eye.Origin.IsEqualApprox(hud.PlanePos),
                $"CONTROL: it is NOT the sim pose the gates read ({hud.PlanePos}), which is the tick-quantised eye the disc used to jitter from");

            // What the picture may draw. The own-airframe layer is the one bit the disc drops.
            uint own = UI.Boards.SplitScreen.OwnAirframeLayer(0);
            ctx.Check(hud.Picture is { } masked && (masked.DiscCullMask & own) == 0
                      && (ctx.Camera.CullMask & own) != 0,
                $"the disc's camera drops the layer this pilot's own aeroplane is drawn on (0x{hud.Picture?.DiscCullMask:X5} against the pane's 0x{ctx.Camera.CullMask:X5}), so no part of the aircraft the eye sits inside is in the picture");
            ctx.Check(hud.Picture is { } kept
                      && (kept.DiscCullMask & UI.Boards.SplitScreen.OwnAirframeLayer(1)) != 0
                      && kept.DiscCullMask == (ctx.Camera.CullMask & ~own),
                $"CONTROL: it drops that ONE bit and nothing else, so the world, every AI aircraft and a splitscreen neighbour's aeroplane are all still in the picture");

            hud.ReleaseSpyglass();
            ctx.Check(!hud.DiscShown && !hud.PictureLive,
                $"a hidden pane releases the picture outright");

            // The other half of the exclusion, on a REAL built airframe standing in for the
            // pilot's own: the stamp a human rig applies moves every mesh onto that one layer, so
            // dropping the bit takes the whole aeroplane out rather than its root node alone.
            UI.Boards.SplitScreen.SetVisualLayer(model, own);
            uint discMask = SpyglassView.DiscMask(ctx.Camera.CullMask, own);
            var (meshes, inDisc) = DrawnUnder(model, discMask);
            var (_, inPane) = DrawnUnder(model, ctx.Camera.CullMask);
            ctx.Check(meshes > 0 && inDisc == 0 && inPane == meshes,
                $"the stamped airframe's {meshes} mesh(es) are all drawn by the pane ({inPane}) and none of them by the disc ({inDisc}), which is the aeroplane, its props, its pylons and their ordnance together");
        }
        finally
        {
            hud?.Free();
            bogey?.Free();
            pool?.Free();
            textures.Dispose();
        }
    }

    // The target-routing switch, driven directly against hand-built sources, no live
    // TargetSelection/AimCandidateSet scan behind it, since DebugForceCrash and AnimRuntime.DamageAt
    // already carry their own coverage elsewhere (AiSuites, DamageSuites and others). What is NEW
    // here is only the dispatch: an aircraft source crashes (Downed fires with the given killer), a
    // destructible source is destroyed through the same DamageAt a rocket uses, and a source with no
    // decoded kill path (a turret, or anything else) is left alone.
    [Suite("debug-kill-target",
        "the F17 debug kill key's routing (A2, BL-534): an aircraft source crashes through the " +
        "attributed DebugForceCrash/Downed path, a destructible source (a zeppelin sub-part) is " +
        "destroyed through the same AnimRuntime.DamageAt a rocket uses, and a turret or any " +
        "other source with no decoded HEALTH key is left inert rather than inventing a kill " +
        "path for it; nothing selected does not throw")]
    internal static void DebugKillTargetRouting(TestContext ctx)
    {
        ctx.RequireData(ctx.PlanesGamezPath, $"planes gamez");
        ctx.RequireData(ctx.ZrdrPath, $"zrdr archive");
        string texturesPath = SessionPaths.ChapterTextures(ctx.DataRoot, "C1");
        ctx.RequireData(texturesPath, $"C1 textures");

        var planesGamez = GameZ.Load(ctx.PlanesGamezPath);
        var stats = PlaneStats.Load(ctx.ZrdrPath, ctx.PlaneName);
        var textures = new TextureArchive(texturesPath);
        ProjectilePool? pool = null;
        FlightController? victim = null;
        AnimRuntime? runtime = null;
        Node3D? gasbagNode = null;
        try
        {
            var live = new ProjectilePool(textures, null, null);
            pool = live;
            ctx.Host.AddChild(live);

            var model = new PlaneBuilder(planesGamez, textures).Build(ctx.PlaneName);
            victim = new FlightController
            {
                PlaneModel = model,
                Collider = PlaneCollider.Build(model),
                Damage = new PlaneDamage(stats.DestroyableParts),
                PlayerIndex = FlightRoster.ShooterIdBase,
                IsHumanPiloted = false,
                Pilot = AiPilot.HoldingCourse(Vector3.Zero, Vector3.Forward),
                Projectiles = live,
                UseKeyboard = false,
                PadDevices = System.Array.Empty<int>(),
                AllowPause = false,
            };
            victim.AddChild(model);
            victim.Setup(new FlightModel(stats), null, new CamParams(), Vector3.Zero, Vector3.Forward);
            ctx.Host.AddChild(victim);

            // The inert stage (AnimRuntime's own doc: "every plain testing runtime takes" it), no
            // chapter world needed, since DamageAt's death swap is a no-op with no ResetState.
            runtime = new AnimRuntime();
            ctx.Host.AddChild(runtime);
            gasbagNode = new Node3D { Name = "gasbag1" };
            ctx.Host.AddChild(gasbagNode);
            var gasbagInst = runtime.Destructibles.Register(
                new AnimDefinition { Name = "gasbag1", AnimName = "zep_zone_gasbag1" }, gasbagNode, 200f);

            var debugKill = new UI.Overlays.DebugKillTarget(() => null, () => runtime);

            const int Killer = FlightRoster.ShooterIdBase + 1;
            int? downedKiller = null;
            victim.Downed += (_, killer) => downedKiller = killer;
            debugKill.KillSource(victim, "victim", Killer);
            ctx.Check(victim.Crashed && downedKiller == Killer,
                $"an aircraft source crashes through the attributed Downed path crashed={victim.Crashed} killer={downedKiller?.ToString() ?? "-"}");

            ctx.Check(gasbagInst.Status != DestructibleRegistry.State.Destroyed,
                $"the gasbag starts healthy status={gasbagInst.Status}");
            debugKill.KillSource(gasbagInst, "gasbag1", Killer);
            ctx.Check(gasbagInst.Status == DestructibleRegistry.State.Destroyed && gasbagInst.Health <= 0f,
                $"a destructible source is destroyed through DamageAt status={gasbagInst.Status} hp={gasbagInst.Health}");

            // A turret carries no HEALTH key at all (TargetRef.Health's own rule), so its arm is
            // the same no-op every unrecognised source takes, proven with a plain object stand-in
            // rather than a built TurretController, which this switch never inspects.
            debugKill.KillSource(new object(), "unrecognised", Killer);

            // Nothing selected: Kill() itself (not KillSource) must not throw with no pilot.
            var noPilotKill = new UI.Overlays.DebugKillTarget(() => null, () => runtime);
            noPilotKill.Kill();
        }
        finally
        {
            victim?.Free();
            runtime?.Free();
            gasbagNode?.Free();
            pool?.Free();
            textures.Dispose();
        }
    }

    // Every drawable under a subtree, and how many of them a camera on this cull mask would draw.
    // The pair is what makes a "nothing is drawn" claim readable: a subtree carrying no drawable
    // at all would otherwise pass it.
    private static (int Total, int Drawn) DrawnUnder(Node node, uint mask)
    {
        int total = 0, drawn = 0;
        if (node is VisualInstance3D vi)
        {
            total++;
            if ((vi.Layers & mask) != 0)
            {
                drawn++;
            }
        }

        foreach (var child in node.GetChildren())
        {
            var (childTotal, childDrawn) = DrawnUnder(child, mask);
            total += childTotal;
            drawn += childDrawn;
        }

        return (total, drawn);
    }
}
