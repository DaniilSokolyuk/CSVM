using System.Collections.Generic;
using System.IO;
using System.Linq;
using CSVM.Bindings;
using CSVM.Effects;
using CSVM.Flight.Ai;
using CSVM.Flight.Airframe;
using CSVM.Flight.Camera;
using CSVM.Flight.Modes;
using CSVM.Flight.Weapons;
using CSVM.Mech3;
using CSVM.Mech3.Anim;
using CSVM.Session.InstantAction;
using CSVM.Session.Launch;
using CSVM.Session.Objectives;
using CSVM.Session.Roster;
using CSVM.Session.World;
using CSVM.UI.Screens;
using CSVM.Utils;
using Godot;

using static CSVM.Testing.SuiteConstants;
namespace CSVM.Testing;

internal static class InstantActionSuites
{
    [Suite("instant-action",
        "the C8/D9/E11 Instant Action runtime: an Instant Action display name (\"Warhawk\") " +
        "resolves to its gamez node and an unrecognised one resolves to null rather than a " +
        "guess, the ace's own spawn draw substitutes the LITERAL last index on a collision " +
        "with the player's (never a re-roll), a mixed ace_stats vector averages to one " +
        "representative AI rating, FlightRoster.SpawnAi given an authored team/livery " +
        "wears them as-is (the ace lands on team 2 flying its configured airframe), the " +
        "wingman fan/escort-chain/accent-id table and the decision-8a flight-size clamp are " +
        "pure over their inputs, a real spawn census puts N wingmen on team 1 flying the " +
        "configured airframe with wingmen 2/4's PrimaryTargetName resolving to wingmen 1/3's " +
        "own spawned name, ApplyActorVolumes puts the authored 10000 m on all three of a " +
        "spawned actor's range gates over the airframe's own 2000/2000/1200, the wave-member " +
        "personality/accent draws are pure over theirs, " +
        "and a real InstantActionWaves sequence over spawned aircraft advances from wave 1 " +
        "to wave 2 exactly on the last kill, activating wave 2's built-inert member at a " +
        "drawn spawn point at least 500 m from the human, where its patrol net re-seats on " +
        "the node by that arrival, not the one by the parking pose it seated on while inert")]
    internal static void InstantActionAce(TestContext ctx)
    {
        ctx.RequireData(ctx.PlanesGamezPath, $"planes gamez");
        ctx.RequireData(ctx.ZrdrPath, $"zrdr archive");
        string texturesPath = SessionPaths.ChapterTextures(ctx.DataRoot, "C1");
        ctx.RequireData(texturesPath, $"C1 textures");

        // The display-name -> gamez-node table (docs/formats/instant-action.md's IDS_IA_PLANES
        // order): a real entry resolves, a typo/invention does not.
        ctx.Check(InstantAction.PlaneNodeFor("Warhawk") == "player_warhawk",
            $"'Warhawk' resolves to its gamez node: {InstantAction.PlaneNodeFor("Warhawk")}");
        ctx.Check(InstantAction.PlaneNodeFor("Warhawk Mk2") == null,
            $"an unrecognised display name resolves to null, not a guess");

        // RepresentativeRating: every shipped chapter's ace_stats is a uniform 9 (spawns.md), but
        // a hand-authored --ia= file could differ, eight 9s and one 7 must average (and round)
        // to 9, not silently pick one arbitrary slot.
        var mixedStats = new AiSkillVector
        {
            DareDevil = 9,
            NaturalTouch = 9,
            SixthSense = 9,
            DeadEye = 7,
            QuickDraw = 9,
            SteadyHand = 9,
            StunRecovery = 9,
            Talker = 9,
            Constitution = 9,
        };
        int rating = InstantActionRuntime.RepresentativeRating(mixedStats);
        ctx.Check(rating == 9, $"eight 9s and one 7 average (rounded) to 9: rating={rating}");
        ctx.Check(InstantActionRuntime.RepresentativeRating(default) == 5,
            $"every slot unset falls back to 5, the same default --ai-attack= takes");

        // ChooseAceSpawn: a draw landing on the player's own index substitutes the LITERAL last
        // index (never a re-roll); a non-colliding draw is used as-is.
        var spawns = new List<SpawnPoint>
        {
            new(new Vector3(0f, 500f, 0f), 0f),
            new(new Vector3(100f, 500f, 0f), 0f),
            new(new Vector3(200f, 500f, 0f), 90f),
        };
        var (collided, _) = InstantActionRuntime.ChooseAceSpawn(spawns, playerSpawnIndex: 0, draw: 0);
        ctx.Check(collided == spawns.Count - 1,
            $"a draw colliding with the player's index substitutes the literal last index: idx={collided}");
        var (clean, _) = InstantActionRuntime.ChooseAceSpawn(spawns, playerSpawnIndex: 1, draw: 0);
        ctx.Check(clean == 0, $"a non-colliding draw is used as-is: idx={clean}");

        // D9: the wingman standing-order table (docs/formats/instant-action.md "The player and
        // the wingmen") is pure over its 0-based index, fan placement, the escort chain (0/1/3
        // escort the player; 2/4 escort wingmen 1/3), and the authored accent ids.
        var slot0 = InstantActionRuntime.WingmanSlotFor(0);
        ctx.Check(slot0 is { MetresOut: 100f, OffsetDeg: -45f, PrimaryTargetIsWingman: null, AccentId: 12 },
            $"wingman 0: 100 m / -45°, escorts the player, accent 12: {slot0}");
        var slot1 = InstantActionRuntime.WingmanSlotFor(1);
        ctx.Check(slot1 is { MetresOut: 100f, OffsetDeg: 45f, PrimaryTargetIsWingman: null, AccentId: 14 },
            $"wingman 1: 100 m / +45°, escorts the player, accent 14: {slot1}");
        var slot2 = InstantActionRuntime.WingmanSlotFor(2);
        ctx.Check(slot2 is { MetresOut: 200f, OffsetDeg: 45f, PrimaryTargetIsWingman: 1, AccentId: 15 },
            $"wingman 2: 200 m / +45°, escorts wingman 1, accent 15: {slot2}");
        var slot3 = InstantActionRuntime.WingmanSlotFor(3);
        ctx.Check(slot3 is { MetresOut: 200f, OffsetDeg: -45f, PrimaryTargetIsWingman: null, AccentId: 13 },
            $"wingman 3: 200 m / -45°, escorts the player, accent 13: {slot3}");
        var slot4 = InstantActionRuntime.WingmanSlotFor(4);
        ctx.Check(slot4 is { MetresOut: 300f, OffsetDeg: -45f, PrimaryTargetIsWingman: 3, AccentId: 16 },
            $"wingman 4: 300 m / -45°, escorts wingman 3, accent 16: {slot4}");

        // Decision 8a's flight-size clamp: 1-4 humans against 5 configured wingmen expects
        // 5/4/3/2, and a below-cap case (2 humans, 2 configured) proves the configured count
        // stands untouched. 0 configured always flies none.
        ctx.Check(InstantActionRuntime.FlownWingmen(5, humans: 1) == 5, $"1 human, 5 configured: flies all 5");
        ctx.Check(InstantActionRuntime.FlownWingmen(5, humans: 2) == 4, $"2 humans, 5 configured: clamped to 4");
        ctx.Check(InstantActionRuntime.FlownWingmen(5, humans: 3) == 3, $"3 humans, 5 configured: clamped to 3");
        ctx.Check(InstantActionRuntime.FlownWingmen(5, humans: 4) == 2, $"4 humans, 5 configured: clamped to 2");
        ctx.Check(InstantActionRuntime.FlownWingmen(2, humans: 2) == 2, $"below the cap: 2 humans/2 configured stays 2");
        ctx.Check(InstantActionRuntime.FlownWingmen(0, humans: 1) == 0, $"0 configured flies none");

        // The actual spawn integration: FlightRoster.SpawnAi given an authored scheme/team
        // (the C8 extension) wears them as-is, the ace lands on team 2 flying the configured
        // airframe, not a pilot-index-derived team.
        var planesGamez = GameZ.Load(ctx.PlanesGamezPath);
        var weaponDefs = WeaponDefs.Load(ctx.ZrdrPath, null);
        var stockLoadouts = StockLoadouts.Load();
        var textures = new TextureArchive(texturesPath);
        ProjectilePool? pool = null;
        FlightController? ace = null;
        var wingmen = new List<FlightController>();
        var waveMembers = new List<FlightController>();
        try
        {
            var live = new ProjectilePool(textures, null, null);
            pool = live;
            ctx.Host.AddChild(live);

            var spec = SessionSpec.Parse(System.Array.Empty<string>());
            var liveries = new LiveryResolver(spec, Path.Combine(ctx.DataRoot, "extracted", "rof"));
            var inputs = new AircraftAssemblyResources
            {
                PlanesGamez = planesGamez,
                StatsFor = plane => PlaneStats.Load(ctx.ZrdrPath, plane),
                AiStatsFor = (plane, aiDef) => PlaneStats.LoadForAi(ctx.ZrdrPath, plane, aiDef),
                PaintRng = new RandomNumberGenerator(),
                ZrdrPath = ctx.ZrdrPath,
                StockLoadouts = stockLoadouts,
                WeaponDefs = weaponDefs,
                Textures = textures,
                Shakes = ShakeDefs.Load(ctx.ZrdrPath),
            };
            // worldEffects null!: never dereferenced, Inputs.CrashProgram/WorldScene stay null,
            // so Spawn's crash-runtime block (the only reader) is skipped.
            var spawner = new FlightRoster(FlightRosterPolicy.From(spec), liveries, null!, ctx.Host, inputs, new FlightWorldBindings { Projectiles = live, Gamez = planesGamez }, new HumanRosterBindings());

            string aceNode = InstantAction.PlaneNodeFor("Warhawk")!;
            var aceLivery = new PaintScheme { Pattern = "cccp", Color1 = PaintScheme.FromBytes(200, 10, 10) };
            var pos = new Vector3(0f, 500f, 0f);
            var pilot = AiPilot.HoldingCourse(pos, pos + Vector3.Forward);
            ace = spawner.SpawnAi(new AiSpawn(aceNode, pos, pos + Vector3.Forward, pilot,
                Scheme: aceLivery, Team: InstantActionRuntime.EnemyTeam));

            ctx.Check(ace.Team == InstantActionRuntime.EnemyTeam,
                $"the spawned ace carries the authored team, not a pilot-index default: team={ace.Team}");
            ctx.Check(ace.Name.ToString().Contains(aceNode),
                $"the ace flies its configured airframe: {ace.Name}");

            // The wingman census: N aircraft on team 1, since humans and wingmen share the player's side,
            // flying the configured airframe. Spawned through the same FlightRoster.SpawnAi seam as the
            // ace above, on AimAssist.PlayerTeam instead of the enemy team.
            string wingmanNode = InstantAction.PlaneNodeFor("Fury")!;
            for (int i = 0; i < 3; i++)
            {
                var wPos = new Vector3(500f + i * 10f, 500f, 0f);
                var wPilot = AiPilot.HoldingCourse(wPos, wPos + Vector3.Forward);
                wingmen.Add(spawner.SpawnAi(new AiSpawn(wingmanNode, wPos, wPos + Vector3.Forward, wPilot,
                    Scheme: null, Team: AimAssist.PlayerTeam)));
            }
            ctx.Check(wingmen.Count == 3, $"3 wingmen spawned: {wingmen.Count}");
            ctx.Check(wingmen.All(w => w.Team == AimAssist.PlayerTeam),
                $"every wingman carries team 1, not a pilot-index default: teams={string.Join(",", wingmen.Select(w => w.Team))}");
            ctx.Check(wingmen.All(w => w.Name.ToString().Contains(wingmanNode)),
                $"every wingman flies the configured airframe: {string.Join(",", wingmen.Select(w => w.Name))}");

            // The synthetic roster block writes 10000 m into all three volumes, so the airframe gates the
            // spawner seeds first must not survive on an Instant Action actor. Overriding activation alone
            // leaves attack as the real engagement gate: the mode machine enters pursue on the minimum of two.
            var volPos = new Vector3(600f, 500f, 0f);
            var volPilot = AiPilot.HoldingCourse(volPos, volPos + Vector3.Forward);
            volPilot.Machine = new AiModeMachine(new System.Random(7));
            wingmen.Add(spawner.SpawnAi(new AiSpawn(wingmanNode, volPos, volPos + Vector3.Forward, volPilot,
                Scheme: null, Team: AimAssist.PlayerTeam)));
            var vol = volPilot.Machine;
            ctx.Check(Mathf.IsEqualApprox(vol.AttackRange, 2000f)
                && Mathf.IsEqualApprox(vol.ReturnRange, 1200f),
                $"the spawner seeds the airframe's own gates first: attack={vol.AttackRange:0} return={vol.ReturnRange:0}");
            InstantActionRuntime.ApplyActorVolumes(vol);
            ctx.Check(Mathf.IsEqualApprox(vol.ActivationRange, InstantActionRuntime.ActorVolumeRadiusM)
                && Mathf.IsEqualApprox(vol.AttackRange, InstantActionRuntime.ActorVolumeRadiusM)
                && Mathf.IsEqualApprox(vol.ReturnRange, InstantActionRuntime.ActorVolumeRadiusM),
                $"all three volumes take the authored 10000 m: activation={vol.ActivationRange:0} attack={vol.AttackRange:0} return={vol.ReturnRange:0}");

            // E11: RandomPilotStats/ResolveWaveAccentId are pure over their draw, row 4 is the
            // flat-4 personality, and only accent 12 (the wingman range's own base) re-rolls.
            var flatRow = InstantActionRuntime.RandomPilotStats(draw: 4);
            ctx.Check(flatRow is { DareDevil: 4, Constitution: 4 },
                $"draw 4 selects row 4, the flat personality: {flatRow}");
            ctx.Check(InstantActionRuntime.ResolveWaveAccentId(12, draw: 3) == 15,
                $"accent 12 re-rolls to 12 + draw%5: {InstantActionRuntime.ResolveWaveAccentId(12, 3)}");
            ctx.Check(InstantActionRuntime.ResolveWaveAccentId(7, draw: 99) == 7,
                $"any other accent id passes through unchanged: {InstantActionRuntime.ResolveWaveAccentId(7, 99)}");

            // E11: a real InstantActionWaves sequence over real spawned aircraft, wave 1 (2
            // members, live) killed down to 0 triggers wave 2 (1 member, built inert) activating
            // at a spawn point at least 500 m from the human, fanned off it.
            var iaWaves = new InstantActionWaves(new[] { 2, 1, 0, 0 });
            int firstWave = iaWaves.Start();
            ctx.Check(firstWave == 1, $"wave 1 is current at mission start: {firstWave}");

            string waveNode = InstantAction.PlaneNodeFor("Brigand")!;
            var wave1Pos = new Vector3(0f, 500f, 0f);
            for (int i = 0; i < 2; i++)
            {
                var wmPos = wave1Pos + new Vector3(i * 10f, 0f, 0f);
                var wmPilot = AiPilot.HoldingCourse(wmPos, wmPos + Vector3.Forward);
                waveMembers.Add(spawner.SpawnAi(new AiSpawn(waveNode, wmPos, wmPos + Vector3.Forward, wmPilot,
                    Scheme: null, Team: InstantActionRuntime.EnemyTeam)));
            }
            var wave2Pilot = AiPilot.HoldingCourse(Vector3.Zero, Vector3.Forward);
            // Every Instant Action actor carries the chapter's first patrol net and an inert one still ticks,
            // so this two-node stand-in net has a node at the parking pose and another out at the wave spawn
            // point, to watch which one it flies after the teleport.
            var parkNet = new AiNet
            {
                Id = 1,
                Name = "TestWaveNet",
                Nodes = new[]
                {
                    new AiNetNode(Vector3.Zero, System.Array.Empty<float>()),
                    new AiNetNode(new Vector3(600f, 500f, 0f), System.Array.Empty<float>()),
                },
                Edges = new[] { (0, 1) },
            };
            wave2Pilot.Patrol = new AiNetFollower(parkNet, new System.Random(3));
            var wave2Member = spawner.SpawnAi(new AiSpawn(waveNode, Vector3.Zero, Vector3.Forward, wave2Pilot,
                Scheme: null, Team: InstantActionRuntime.EnemyTeam, Inert: true));
            waveMembers.Add(wave2Member);
            wave2Pilot.Patrol.Update(Vector3.Zero);
            ctx.Check(wave2Pilot.Patrol.CurrentIndex == 0,
                $"parked inert, the follower seats on the node by the parking pose: idx={wave2Pilot.Patrol.CurrentIndex}");

            int aliveWave1 = waveMembers.Take(2).Count(m => m.InPlay);
            ctx.Check(aliveWave1 == 2, $"both wave-1 members InPlay before any kill: {aliveWave1}");
            ctx.Check(!wave2Member.InPlay, $"wave 2's member is inert (not InPlay) before activation");
            ctx.Check(iaWaves.Step(aliveInCurrentWave: aliveWave1) == 0,
                $"wave 1 still alive: no advance");

            foreach (var m in waveMembers.Take(2))
                m.DebugForceCrash();
            int aliveAfterKills = waveMembers.Take(2).Count(m => m.InPlay);
            ctx.Check(aliveAfterKills == 0, $"both wave-1 members crashed: {aliveAfterKills}");
            int nextWave = iaWaves.Step(aliveInCurrentWave: aliveAfterKills);
            ctx.Check(nextWave == 2, $"wave 1's last kill advances to wave 2: {nextWave}");
            ctx.Check(iaWaves.CurrentWave == 2 && !iaWaves.Finished,
                $"the sequencer's own state agrees: {iaWaves.CurrentWave}");

            var humanPos = new Vector3(0f, 500f, 0f);
            var waveSpawns = new List<SpawnPoint>
            {
                new(new Vector3(10f, 500f, 0f), 0f),      // 10 m from the human, too close
                new(new Vector3(600f, 500f, 0f), 0f),     // 600 m, eligible
            };
            var (spIdx, sp) = InstantActionWaves.ChooseWaveSpawn(
                waveSpawns, new[] { humanPos }, draw: 0);
            ctx.Check(spIdx == 1, $"the near point is excluded, the far one drawn: idx={spIdx}");
            var fwd2 = new Basis(Vector3.Up, Mathf.DegToRad(sp.HeadingDeg)) * Vector3.Forward;
            wave2Member.Activate(sp.Position, sp.Position + fwd2);
            ctx.Check(wave2Member.InPlay, $"wave 2's member is InPlay once activated");
            float distSq = wave2Member.WorldPosition.DistanceSquaredTo(humanPos);
            ctx.Check(distSq >= InstantActionWaves.MinSpawnDistanceSquared,
                $"activated at least 500 m from the human: dist={Mathf.Sqrt(distSq):0} m");
            // The activation snap the original does (FUN_004b0f40 → FUN_00432010): the arrival
            // re-seats the walk, so the member patrols from where it was put down instead of
            // flying back to the node by its parking pose.
            wave2Pilot.Patrol.Update(wave2Member.WorldPosition);
            ctx.Check(wave2Pilot.Patrol.CurrentIndex == 1,
                $"activation re-seats it on the node by its ARRIVAL: idx={wave2Pilot.Patrol.CurrentIndex}");
        }
        finally
        {
            pool?.Free();
            ace?.Free();
            foreach (var w in wingmen)
                w.Free();
            foreach (var m in waveMembers)
                m.Free();
            textures.Dispose();
        }
    }

    // The actors' accents reach the voice prewarm only through VoiceAccentIds. A clip outside that
    // set never plays once the archive closes. So the ace's death cry is asserted against a real
    // prewarm over the join GameSession.BuildWorldStage builds.
    [Suite("instant-action-voice",
        "C1/IA1 as the wizard's dogfight_ace: the actors' accent join holds the ace's own accent, "
        + "which the mission roster alone does not reach, so a session prewarm over roster plus "
        + "join leaves a streamed DE clip for the ace's pilot and a speaker registered on that "
        + "pilot resolves its forced death cry through the voice runtime after the loader is "
        + "retired; the join also carries each configured wingman slot's accent and a wave "
        + "enemy_accentID of 12's whole 12 to 16 re-roll, and nothing for an empty wave; and the "
        + "shipped accent table answers for every militia's wave accent, while among the five "
        + "wingman slots only 14 reaches no voiced pilot, its row naming the clipless pilot id 5")]
    internal static void InstantActionVoice(TestContext ctx)
    {
        ctx.RequireData(ctx.ZrdrPath, $"zrdr archive");
        ctx.RequireData(ctx.SoundsPath, $"sound archive (soundsh)");
        string missionZrdr = SessionPaths.MissionZrdr(ctx.DataRoot, "C1", "IA1");
        ctx.RequireData(missionZrdr, $"C1/IA1 zrdr");

        var shipped = InstantAction.Load(missionZrdr);
        var aceDef = InstantAction.BuildFromWizard(shipped, "dogfight_ace", shipped.PlayerPlane,
            numWingmen: 3, shipped.WingmanPlane, shipped.Waves, shipped.Lives);
        var aceAccents = InstantActionRuntime.VoiceAccentIds(aceDef);
        ctx.Check(aceAccents.SequenceEqual(new[] { aceDef.AceAccentId }),
            $"dogfight_ace joins the ace's accent alone: [{string.Join(",", aceAccents)}] ace={aceDef.AceAccentId}");

        var wingmenOnly = InstantAction.BuildFromWizard(shipped, "dogfight_squadron",
            shipped.PlayerPlane, numWingmen: 2, shipped.WingmanPlane,
            new[] { new InstantActionWave(4, "w", "Fury", "ace", -1) }, shipped.Lives);
        var wingmanAccents = InstantActionRuntime.VoiceAccentIds(wingmenOnly);
        ctx.Check(wingmanAccents.SequenceEqual(new[] { 12, 14 }),
            $"two wingmen join slots 0 and 1's accents, an accentless wave none: [{string.Join(",", wingmanAccents)}]");

        var rerolled = InstantAction.BuildFromWizard(shipped, "dogfight_squadron",
            shipped.PlayerPlane, numWingmen: 0, shipped.WingmanPlane,
            new[]
            {
                new InstantActionWave(2, "w", "Fury", "ace", 12),
                new InstantActionWave(0, "w", "Fury", "ace", 7),
            }, shipped.Lives);
        var waveAccents = InstantActionRuntime.VoiceAccentIds(rerolled);
        ctx.Check(waveAccents.SequenceEqual(new[] { 12, 13, 14, 15, 16 }),
            $"a wave on accent 12 joins its whole re-roll range, an empty wave nothing: [{string.Join(",", waveAccents)}]");

        var defs = SoundDefs.Load(ctx.ZrdrPath);
        var groups = SoundDefs.LoadGroups(ctx.ZrdrPath);
        var voice = new CombatVoice(defs, groups, CombatVoice.LoadAccents(ctx.ZrdrPath));
        int? acePilot = voice.PilotFor(aceDef.AceAccentId, new System.Random(1));
        if (acePilot is not { } vo)
        {
            throw new SuiteSkippedException($"accent {aceDef.AceAccentId} resolves to no voiced pilot");
        }
        var deClips = voice.ClipsFor(vo, "DE");
        ctx.Check(deClips.Count > 0, $"the ace's VO id {vo} owns DE clips: {deClips.Count}");

        // Every accent the wizard's own actors can reach, against the shipped accent table. The
        // thirteen militia accents all speak; among the five wingman slots only 14 does not, its
        // row being the single pilot id the install ships no clip for.
        var mute = UI.Menu.InstantActionFeature.Militias
            .Where(m => voice.PilotFor(m.AccentId, new System.Random(1)) == null)
            .Select(m => m.Name).ToList();
        ctx.Check(mute.Count == 0,
            $"every militia's wave accent reaches a voiced pilot; silent: [{string.Join(",", mute)}]");
        var silentSlots = Enumerable.Range(0, 5)
            .Select(i => InstantActionRuntime.WingmanSlotFor(i).AccentId)
            .Where(a => voice.PilotFor(a, new System.Random(1)) == null).ToList();
        ctx.Check(silentSlots.SequenceEqual(new[] { 14 }),
            $"wingman slot accent 14 alone reaches no voiced pilot: [{string.Join(",", silentSlots)}]");
        ctx.Check(voice.Pool(14).SequenceEqual(new[] { 5 }) && voice.ClipsFor(5, "DA").Count == 0,
            $"accent 14 is the single pilot id 5, which owns no clip def: [{string.Join(",", voice.Pool(14))}]");

        var rosterOnly = CombatVoice.SessionPrewarmNames(ctx.ZrdrPath, missionZrdr, defs, groups);
        ctx.Check(!deClips.Any(rosterOnly.Contains),
            $"the mission roster alone prewarms none of them ({rosterOnly.Count} names)");
        var joined = CombatVoice.SessionPrewarmNames(ctx.ZrdrPath, missionZrdr, defs, groups, aceAccents);

        using var archive = new SoundArchive(ctx.SoundsPath);
        WorldSounds? sounds = null;
        MissionRadio? radio = null;
        AiVoiceRuntime? runtime = null;
        try
        {
            sounds = new WorldSounds(defs, groups)
            {
                Loader = (d, warn) => archive.Find(d.WavName, d.Looped, warn),
            };
            ctx.Host.AddChild(sounds);
            int decoded = sounds.Prewarm(joined);
            sounds.Loader = null;   // the session's build scope closing (WorldSession.Build)
            ctx.Note($"roster + join prewarm: {joined.Count} names, {decoded} streams");
            ctx.Check(deClips.Any(sounds.HasStream),
                $"the ace's DE family has a stream after the loader is retired");

            radio = new MissionRadio(defs, groups, sounds.StreamFor);
            ctx.Host.AddChild(radio);
            runtime = new AiVoiceRuntime(voice, sounds, radio, new System.Random(5));
            ctx.Host.AddChild(runtime);
            var speaker = runtime.Dispatcher.Register(900, vo, InstantActionRuntime.EnemyTeam,
                isPlayer: false, talkerChance: 2f, constitutionChance: 0.5f);
            var cry = runtime.Dispatcher.DeathCry(speaker.Id, onPlayersTeam: false, now: 10f);
            ctx.Check(cry.Clip != null,
                $"the registered ace's forced death cry resolves a clip: {cry.Clip ?? "null"} ({cry.Outcome})");
        }
        finally
        {
            runtime?.Free();
            radio?.Free();
            if (sounds != null)
            {
                sounds.FlushOneShots();
                sounds.Free();
            }
        }
    }

    // Driven through the director's own BuildActors rather than hand-built AiSpawns. The name each
    // actor carries is then the one the mission build writes, not one this suite chose.
    [Suite("instant-action-marker-names",
        "the Instant Action actor build names its aircraft the way the original's marker does: "
        + "over C1/IA1 and C5/IA1 as dogfight_ace, the ace's display name is its ace_name resolved "
        + "through the string table (a pilot, not its airframe); over C1/IA1 as dogfight_squadron "
        + "with five wingmen and one member per wave, each wave member reads its group's resolved "
        + "enemy_name and the wingmen read Jack, Tex, Buck, Big John and Betty in slot order")]
    internal static void InstantActionMarkerNames(TestContext ctx)
    {
        ctx.RequireData(ctx.PlanesGamezPath, $"planes gamez");
        ctx.RequireData(ctx.ZrdrPath, $"zrdr archive");
        ctx.RequireData(ctx.MessagesPath, $"string table");
        string texturesPath = SessionPaths.ChapterTextures(ctx.DataRoot, "C1");
        ctx.RequireData(texturesPath, $"C1 textures");
        var strings = Messages.Load(ctx.MessagesPath);

        var planesGamez = GameZ.Load(ctx.PlanesGamezPath);
        var textures = new TextureArchive(texturesPath);
        ProjectilePool? pool = null;
        var built = new List<FlightController>();
        try
        {
            var live = new ProjectilePool(textures, null, null);
            pool = live;
            ctx.Host.AddChild(live);
            var roster = CampaignRosterSuites.Spawner(ctx, planesGamez, textures, live);

            // One mission build: the director spawns through the roster, and the spawns come back
            // in build order (ace, wingmen, then waves 1 to 4).
            List<(AiSpawn Spawn, FlightController Actor)> Build(string chapter, InstantActionDef def)
            {
                string missionZrdr = SessionPaths.MissionZrdr(ctx.DataRoot, chapter, "IA1");
                var spec = SessionSpec.FromMenu(SessionSpec.Parse(System.Array.Empty<string>()),
                    chapter, new[] { "player_bhawk" }, MenuMode.Free, def);
                var director = InstantActionDirector.TryCreate(spec)!;
                var leadAt = new Vector3(0f, 800f, 0f);
                var lead = roster.SpawnAi(new AiSpawn("player_bhawk", leadAt, leadAt + Vector3.Forward,
                    AiPilot.HoldingCourse(leadAt, leadAt + Vector3.Forward), Team: AimAssist.PlayerTeam));
                built.Add(lead);
                var spawned = new List<(AiSpawn, FlightController)>();
                director.BuildActors(new InstantActionDirector.ActorBuildInputs
                {
                    Rigs = new List<PlayerRig> { new() { Controller = lead } },
                    ChapterZrdrPath = SessionPaths.ChapterZrdr(ctx.DataRoot, chapter),
                    MissionZrdrPath = missionZrdr,
                    ZrdrPath = ctx.ZrdrPath,
                    MessagesPath = ctx.MessagesPath,
                    SpawnList = SpawnPoints.LoadIa(missionZrdr, def.MissionType),
                    SpawnBase = 0,
                    LiveryResolver = new LiveryResolver(spec, Path.Combine(ctx.DataRoot, "extracted", "rof")),
                    NetTrailers = new NetTrailerTargets(null, null),
                    Spawn = spawn =>
                    {
                        var fc = roster.SpawnAi(spawn);
                        built.Add(fc);
                        spawned.Add((spawn, fc));
                        return fc;
                    },
                    RegisterVoice = (_, _, _, _) => { },
                });
                return spawned;
            }

            string Marker(FlightController fc) => PlaneRoster.PlaneDisplayName(fc.Stats!);

            foreach (string chapter in new[] { "C1", "C5" })
            {
                string missionZrdr = SessionPaths.MissionZrdr(ctx.DataRoot, chapter, "IA1");
                ctx.RequireData(missionZrdr, $"{chapter}/IA1 zrdr");
                var shipped = InstantAction.Load(missionZrdr);
                var aceDef = InstantAction.BuildFromWizard(shipped, "dogfight_ace", shipped.PlayerPlane,
                    numWingmen: 0, shipped.WingmanPlane, shipped.Waves, shipped.Lives);
                var aceBuild = Build(chapter, aceDef);
                string expected = strings.Get(shipped.AceName);
                ctx.Check(aceBuild.Count == 1 && aceBuild[0].Actor.Team == InstantActionRuntime.EnemyTeam,
                    $"{chapter} dogfight_ace builds the ace alone: {aceBuild.Count} actor(s)");
                if (aceBuild.Count == 0)
                {
                    continue;
                }
                string aceMarker = Marker(aceBuild[0].Actor);
                ctx.Check(!expected.StartsWith("MSG_", System.StringComparison.Ordinal) && aceMarker == expected,
                    $"{chapter}: the ace's marker reads its resolved ace_name '{shipped.AceName}' -> '{expected}': '{aceMarker}'");
                ctx.Check(aceMarker != shipped.AcePlane,
                    $"…the pilot, not the airframe '{shipped.AcePlane}'");
            }

            var c1 = InstantAction.Load(SessionPaths.MissionZrdr(ctx.DataRoot, "C1", "IA1"));
            var oneEach = c1.Waves.Select(w => w with { NumEnemies = 1 }).ToList();
            var squadDef = InstantAction.BuildFromWizard(c1, "dogfight_squadron", c1.PlayerPlane,
                numWingmen: 5, c1.WingmanPlane, oneEach, c1.Lives);
            var squad = Build("C1", squadDef);
            var wingmen = squad.Where(s => s.Actor.Team == AimAssist.PlayerTeam).Select(s => Marker(s.Actor)).ToList();
            var waves = squad.Where(s => s.Actor.Team == InstantActionRuntime.EnemyTeam).Select(s => s.Actor).ToList();
            var wingmanNames = new[] { "Jack", "Tex", "Buck", "Big John", "Betty" };
            ctx.Check(wingmen.SequenceEqual(wingmanNames),
                $"the five wingmen read their slots' names in order: [{string.Join(", ", wingmen)}]");
            ctx.Check(waves.Count == 4, $"one member per wave, four waves: {waves.Count}");
            for (int w = 0; w < System.Math.Min(4, waves.Count); w++)
            {
                string key = c1.Waves[w].EnemyName;
                string want = strings.Get(key);
                string got = Marker(waves[w]);
                ctx.Check(!want.StartsWith("MSG_", System.StringComparison.Ordinal) && got == want,
                    $"wave {w + 1}'s member reads its group's resolved enemy_name '{key}' -> '{want}': '{got}'");
            }
        }
        finally
        {
            pool?.Free();
            foreach (var fc in built)
            {
                fc.Free();
            }
            textures.Dispose();
        }
    }

    // The F12 zeppelin run: the objective-zeppelin selection, the builder's own switch,
    // and the wave arm that replaces E11's teleport. Everything runs over C1/IA1's real
    // `ia.zrd.json` / `egen.zrd.json` / `zeppelins.zrd.json`, on the same host +
    // `cargobay` stand-in world the `zeppelin-launch` suite uses, so the drop geometry
    // under test is the one `AiGeneratorRuntime` already owns.
    [Suite("instant-action-zeppelin",
        "the F12 zeppelin run over C1/IA1's own data: zeppelin_type selects the objective node " +
        "(cargo/passenger/military, an unauthored or unrecognised value falling back to cargo " +
        "the way the record reset does), the mission script's own deactivation of " +
        "multiplayer1zep is undone for the objective while a non-selected zeppelin is switched " +
        "off AND held (placed, no longer flown), and the wave arm is the generator alone: the " +
        "claimed generator launches nothing on an uncredited budget, one wave's credit " +
        "releases exactly that wave's built-inert members from the live cargobay drop point " +
        "and no more, a still-parked member counts as present so the " +
        "sequencer does not skip the wave, and the last kill advances it; in C1/IA1's real " +
        "world the objective starts hidden with every one of its gasbag's collision shapes " +
        "switched off, and the activation brings the hull and those colliders back together " +
        "(leaving off only the descendants that are themselves deactivated)")]
    internal static void InstantActionZeppelin(TestContext ctx)
    {
        ctx.RequireData(ctx.PlanesGamezPath, $"planes gamez");
        ctx.RequireData(ctx.ZrdrPath, $"zrdr archive");
        string texturesPath = SessionPaths.ChapterTextures(ctx.DataRoot, "C1");
        ctx.RequireData(texturesPath, $"C1 textures");
        string chapterZrdr = SessionPaths.ChapterZrdr(ctx.DataRoot, "C1");
        ctx.RequireData(chapterZrdr, $"C1 zrdr");
        string missionZrdr = SessionPaths.MissionZrdr(ctx.DataRoot, "C1", "IA1");
        ctx.RequireData(missionZrdr, $"C1/IA1 zrdr");

        // The objective selection, over the shipped file: C1 authors zeppelin_type cargo and
        // names multiplayer1zep for all three types.
        var iaDef = InstantAction.Load(missionZrdr);
        ctx.Check(iaDef.ZeppelinType == "cargo",
            $"C1/IA1 authors zeppelin_type: {iaDef.ZeppelinType ?? "(unauthored)"}");
        ctx.Check(InstantActionRuntime.SelectedZeppelinNode(iaDef) == "multiplayer1zep",
            $"cargo selects the cargo_zeppelin node: {InstantActionRuntime.SelectedZeppelinNode(iaDef)}");
        ctx.Same(3, InstantActionRuntime.ZeppelinNodes(iaDef).Count,
            $"the three *_zeppelin names are offered in type order");
        ctx.Check(InstantActionRuntime.ZeppelinTypeIndex("passenger") == 1
            && InstantActionRuntime.ZeppelinTypeIndex("military") == 2,
            $"passenger/military index 1/2");
        // ⚠ The fallback is cargo, not an error: the parser REJECTS an unrecognised string over a
        // record whose reset wrote 0 (FUN_00458ff0's param_1[0x95] = 0).
        ctx.Check(InstantActionRuntime.ZeppelinTypeIndex(null) == 0
            && InstantActionRuntime.ZeppelinTypeIndex("blimp") == 0,
            $"an unauthored or unrecognised zeppelin_type falls back to cargo (index 0)");

        var egen = EnemyGenerators.Load(missionZrdr);
        ctx.Check(egen.Count == 1 && egen[0].Node == "multiplayer1zep" && egen[0].IsZeppelin,
            $"the objective zeppelin is the host of C1/IA1's one generator count={egen.Count}");
        if (egen.Count != 1)
            return;
        var genDef = egen[0];
        var nets = AiNets.Load(chapterZrdr);
        var zepDefs = Zeppelins.Load(missionZrdr);
        ctx.Check(zepDefs.Count == 1 && zepDefs[0].Node == "multiplayer1zep",
            $"…and of its one zeppelin record count={zepDefs.Count}");
        if (zepDefs.Count != 1)
            return;

        var planesGamez = GameZ.Load(ctx.PlanesGamezPath);
        var weaponDefs = WeaponDefs.Load(ctx.ZrdrPath, null);
        var textures = new TextureArchive(texturesPath);
        ProjectilePool? pool = null;
        Node3D? host = null;
        Node3D? heldHost = null;
        AiGeneratorRuntime? gens = null;
        ZeppelinRuntime? zeps = null;
        var wave1 = new List<FlightController>();
        var wave2 = new List<FlightController>();
        try
        {
            var live = new ProjectilePool(textures, null, null);
            pool = live;
            ctx.Host.AddChild(live);
            var spec = SessionSpec.Parse(System.Array.Empty<string>());
            var liveries = new LiveryResolver(spec, Path.Combine(ctx.DataRoot, "extracted", "rof"));
            var inputs = new AircraftAssemblyResources
            {
                PlanesGamez = planesGamez,
                StatsFor = plane => PlaneStats.Load(ctx.ZrdrPath, plane),
                AiStatsFor = (plane, aiDef) => PlaneStats.LoadForAi(ctx.ZrdrPath, plane, aiDef),
                PaintRng = new RandomNumberGenerator(),
                ZrdrPath = ctx.ZrdrPath,
                StockLoadouts = StockLoadouts.Load(),
                WeaponDefs = weaponDefs,
                Textures = textures,
                Shakes = ShakeDefs.Load(ctx.ZrdrPath),
            };
            var spawner = new FlightRoster(FlightRosterPolicy.From(spec), liveries, null!, ctx.Host, inputs, new FlightWorldBindings { Projectiles = live, Gamez = planesGamez }, new HumanRosterBindings());

            // Both waves built INERT at the origin, which is what the original does on this one
            // mode for wave 1 as well ("even wave 1 is built deactivated at the origin").
            string waveNode = InstantAction.PlaneNodeFor("Firebrand")!;
            List<FlightController> BuildWave(int count)
            {
                var built = new List<FlightController>(count);
                for (int i = 0; i < count; i++)
                {
                    var pilot = AiPilot.HoldingCourse(Vector3.Zero, Vector3.Forward);
                    built.Add(spawner.SpawnAi(new AiSpawn(waveNode, Vector3.Zero, Vector3.Forward, pilot,
                        Scheme: null, Team: InstantActionRuntime.EnemyTeam, Inert: true)));
                }
                return built;
            }

            wave1.AddRange(BuildWave(6));
            wave2.AddRange(BuildWave(3));
            ctx.Check(wave1.All(m => m.Inert) && wave2.All(m => m.Inert),
                $"both waves are parked inert before the mission starts");

            // The stand-in world: the host above its 100 m launch gate, with the authored
            // cargobay drop node under the hull.
            host = new Node3D { Name = "multiplayer1zep", Position = new Vector3(0f, 500f, 0f) };
            var cargobay = new Node3D { Name = "cargobay", Position = new Vector3(0f, -20f, 0f) };
            host.AddChild(cargobay);
            ctx.Host.AddChild(host);
            var resolvedHost = host;
            var resolvedBay = cargobay;

            int freshSpawns = 0;
            gens = new AiGeneratorRuntime(new[] { genDef },
                (name, scope) => name.Equals("multiplayer1zep", System.StringComparison.OrdinalIgnoreCase)
                    ? resolvedHost
                    : name.Equals("cargobay", System.StringComparison.OrdinalIgnoreCase) ? resolvedBay : null,
                nets, ctx.PlaneName,
                (_, _, _, _) => { freshSpawns++; return default(LaunchedVehicle); },
                (_, _) => 1, (_, _) => { });
            ctx.Same(1, gens.LiveCount, $"the generator is live");

            int launchWave = 0;
            var releasedAt = new List<Vector3>();
            var releasedVelocities = new List<Vector3>();
            var releasedThrottles = new List<float>();
            var releaseDropDistances = new List<float>();
            FlightController? Release(Vector3 pos, Vector3 lookAt, Vector3 launchVelocity)
            {
                var roster = launchWave == 1 ? wave1 : launchWave == 2 ? wave2 : null;
                foreach (var member in roster ?? new List<FlightController>())
                {
                    if (!member.Inert)
                        continue;
                    member.Activate(pos, lookAt, launchVelocity, carrierDrop: true);
                    releasedAt.Add(pos);
                    releasedVelocities.Add(member.WorldVelocity);
                    releasedThrottles.Add(member.Throttle);
                    releaseDropDistances.Add(pos.DistanceTo(cargobay.GlobalPosition));
                    return member;
                }
                return null;
            }

            ctx.Same(1, gens.UseInstantActionLaunches("multiplayer1zep", Release),
                $"the objective zeppelin's generator takes the Instant Action launch arm");

            // Uncredited: the decoded capacity rule holds every cycle from load, so a full minute
            // of sim above the altitude gate launches nothing.
            const float dt = 1f / 60f;
            var hostVelocity = new Vector3(14f, 3f, -8f);
            void StepGenerators()
            {
                host.Position += hostVelocity * dt;
                gens.SimStep(dt);
            }
            for (int i = 0; i < 60 * 60; i++)
                StepGenerators();
            ctx.Check(releasedAt.Count == 0 && wave1.All(m => m.Inert),
                $"an uncredited generator launches nothing released={releasedAt.Count}");

            // The sequencer's own trigger: a wave whose members are all still in the bay counts
            // as present (the decoded +0x945 rule), so it must NOT read as cleared.
            var waves = new InstantActionWaves(new[] { 6, 3, 0, 0 });
            ctx.Same(1, waves.Start(), $"wave 1 is current at mission start");
            int parked = wave1.Count(m => !m.Crashed);
            ctx.Check(parked == 6 && wave1.Count(m => m.InPlay) == 0,
                $"…with all 6 members parked and none InPlay parked={parked}");
            ctx.Same(0, waves.Step(parked),
                $"a wave still waiting in the bay does not advance the sequencer");

            // The credit: one wave's member count, then the generator's own 7 s composed
            // schedule releases exactly that many and stops.
            launchWave = 1;
            ctx.Same(1, gens.GrantWaveCapacity("multiplayer1zep", wave1.Count),
                $"wave 1's member count is credited to the generator");
            for (int i = 0; i < 60 * 120 && releasedAt.Count < 6; i++)
                StepGenerators();
            ctx.Same(6, releasedAt.Count, $"exactly wave 1's six members are released");
            ctx.Check(wave1.All(m => m.InPlay), $"…and all six are in play");
            ctx.Check(wave2.All(m => m.Inert),
                $"wave 2 is untouched, one wave's credit releases one wave");
            ctx.Check(releaseDropDistances.All(distance => distance < 0.1f),
                $"…at the generator's live cargobay drop point max error={releaseDropDistances.Max():0.###} m");
            var expectedLaunchVelocity = hostVelocity + Vector3.Down * 22.352f;
            ctx.Check(releasedVelocities.All(v => v.DistanceTo(expectedLaunchVelocity) < 0.01f),
                $"…with host velocity minus 22.352 m/s vertically velocity={releasedVelocities[0]}");
            ctx.Check(releasedThrottles.All(throttle => Mathf.IsEqualApprox(throttle, 0.1f)),
                $"…at the decoded 10% carrier-drop throttle throttle={releasedThrottles[0]:0.##}");
            ctx.Same(0, freshSpawns,
                $"the generator never spawned an aircraft of its own on this arm");

            for (int i = 0; i < 60 * 60; i++)
                StepGenerators();
            ctx.Same(6, releasedAt.Count, $"the credit is spent: a further minute releases nothing");

            // The last kill advances, exactly as on the teleport arm.
            foreach (var m in wave1)
                m.DebugForceCrash();
            ctx.Same(2, waves.Step(wave1.Count(m => !m.Crashed)),
                $"wave 1's last kill advances to wave 2");

            // The builder's other arm: a zeppelin it switches off is HELD, still placed at its
            // authored pose, but no longer flown (a merely hidden one would keep flying its net
            // and firing its broadside).
            heldHost = new Node3D { Name = "multiplayer1zep" };
            ctx.Host.AddChild(heldHost);
            var resolvedHeld = heldHost;
            zeps = new ZeppelinRuntime(zepDefs,
                name => name.Equals("multiplayer1zep", System.StringComparison.OrdinalIgnoreCase)
                    ? resolvedHeld : null, nets);
            ctx.Same(1, zeps.LiveCount, $"the zeppelin record is placed");
            ctx.Check(zeps.Hold("multiplayer1zep"), $"…and the builder can hold it");
            ctx.Check(!zeps.Hold("nosuchzep"), $"holding an unknown node reports it, never throws");
            var placedAt = heldHost.GlobalPosition;
            for (int i = 0; i < 60 * 10; i++)
                zeps.SimStep(dt);
            ctx.Check(heldHost.GlobalPosition.DistanceTo(placedAt) < 0.01f,
                $"a held zeppelin stays at its authored pose through 10 s of sim moved={heldHost.GlobalPosition.DistanceTo(placedAt):0.###} m");
        }
        finally
        {
            gens?.Free();
            zeps?.Free();
            host?.Free();
            heldHost?.Free();
            pool?.Free();
            foreach (var m in wave1)
                m.Free();
            foreach (var m in wave2)
                m.Free();
            textures.Dispose();
        }

        // ⚠ Keep this read-only against the shared cached world: a pool registered or a node left
        // switched on here reaches every later C1 suite, and once inflated chapter-census's counts.
        // What it measures is the decoded activation restoring C1/IA1's objective.
        ctx.WithWorld("C1", collision: true, mission: "IA1", world =>
        {
            var runtime = world.Session.Runtime;
            var objective = runtime.FindNodes("multiplayer1zep").FirstOrDefault();
            ctx.Check(objective != null, $"the objective zeppelin's node is in C1/IA1's world");
            if (objective == null)
                return;
            ctx.Check(!objective.IsVisibleInTree(),
                $"C1/IA1's mission script has switched it off at world load (the state F12 inherits)");

            var bagNode = runtime.FindNodes("gasbag1", objective).FirstOrDefault();
            ctx.Check(bagNode != null, $"gasbag1 resolves under the objective's subtree");
            if (bagNode == null)
                return;
            var (shapesTotal, shapesOff) = ShapeStates(bagNode);
            ctx.Check(shapesTotal > 0 && shapesOff == shapesTotal,
                $"…with all {shapesTotal} of its collision shapes switched off with it off={shapesOff}");
            try
            {
                // The decoded builder step: gwNodeSetActive(node, TRUE) on the selected zeppelin.
                objective.Visible = true;
                var (_, stillOff) = ShapeStates(bagNode);
                // ⚠ Not "all 36 back on": the ones that stay off are descendants that are themselves deactivated,
                // the hidden destroyed variants, which is why WorldCollision derives the flag instead of walking a
                // subtree to re-enable it. The measurement is the crossing, not a full count.
                ctx.Check(objective.IsVisibleInTree() && stillOff < shapesTotal,
                    $"the Instant Action activation puts BOTH back: visible again ({objective.IsVisibleInTree()}), {shapesTotal - stillOff} of {shapesTotal} shapes re-enabled (the {stillOff} left off are the hidden destroyed variants)");
            }
            finally
            {
                objective.Visible = false;
                var (_, offAgain) = ShapeStates(bagNode);
                ctx.Check(offAgain == shapesTotal,
                    $"…and the world is left exactly as this suite found it off={offAgain} of {shapesTotal}");
            }
        });
    }

    // How many collision shapes hang anywhere under this node, and how many of those are
    // switched off, the state `Mech3/WorldCollision` derives from its owner's visibility.
    // Recursive, because a world node's shapes hang off its MESH children rather than off the
    // named node itself; a non-recursive count reads 0 of 0 and passes an "all disabled" test
    // vacuously.
    internal static (int Total, int Disabled) ShapeStates(Node node)
    {
        int total = 0, disabled = 0;
        if (node is CollisionShape3D collision)
        {
            total++;
            if (collision.Disabled)
                disabled++;
        }
        foreach (var child in node.GetChildren())
        {
            var (t, d) = ShapeStates(child);
            total += t;
            disabled += d;
        }
        return (total, disabled);
    }

    // The Instant Action mission end: one mission type at a time, each driven to its end through the
    // same signal GameSession subscribes to, plus the lives ledger's two ends on a real
    // FlightController. Inventory: this module's docs/architecture.md entry.
    // ⚠ Pair every win check with a SECOND runtime of another mission type on the same signal that
    // must stay Running. Reporting an objective the mission does not run on is the one mistake this
    // design can make.
    [Suite("instant-action-end",
        "the G13 mission end, one mission type at a time and each through the real signal: an " +
        "ace's own Downed report wins the duel, the wave sequencer's last kill wins the " +
        "squadron (with a wave still flying it does not), the LAST pilot in wins the stunt run " +
        "over C1/IA1's authored zones while the first does not, and the last still-flying one " +
        "does when the other is out of lives, and really shooting out every one of C1/M04's " +
        "piratezep engines wins the zeppelin run with its hull still alive (one engine short " +
        "does not), as does the gasbag threshold on its own; each with a second mission of " +
        "another type subscribed to the same " +
        "signal and staying Running, plus a hull that is not the objective leaving it running; " +
        "and the lives ledger on a real aircraft: with a life left the armed 3 s crash cam " +
        "respawns it and NO wrap-up hold is armed, out of lives the wreck is still there 10 s " +
        "later and the solo mission is LOST; plus the hold between the ending and the board: a " +
        "win presents nothing, nothing is due a fifth of a second short of the decoded hold, the " +
        "whole hold spent presents it exactly once and never again, the mission clock stays at " +
        "the ending throughout, and a death reported inside the hold spends no life and leaves " +
        "the win standing; the two holds on a real seat flying a full-deflection scripted stick: " +
        "a death's holds the stick neutral over the lever it was left on, a win's leaves the same " +
        "stick and throttle flying while both triggers and all four selectors stay swallowed, a " +
        "crash inside a win's hold stays down with R held and the crash cam armed, and each has " +
        "its able-to-fail control at the release; plus the handover, where the hold offers no " +
        "menu at all and the board that follows answers the first press on its Restart row; and " +
        "a C1/IA1 stunt run completing on that seat, which flies on through the whole win's hold " +
        "to the wrap-up with a marker first entered inside the hold not photographed, where the " +
        "same seat under a solo scoreboard holds its finish pose; and a two-pilot C1/IA1 stunt " +
        "run built through the session's roster, where the last pilot in wins, no race board is " +
        "built, both aircraft fly through the hold with no halt and nothing photographed, and " +
        "the wrap-up follows, while the same field as a plain race wakes its board and holds")]
    internal static void InstantActionEnd(TestContext ctx)
    {
        ctx.RequireData(ctx.PlanesGamezPath, $"planes gamez");
        ctx.RequireData(ctx.ZrdrPath, $"zrdr archive");
        string texturesPath = SessionPaths.ChapterTextures(ctx.DataRoot, "C1");
        ctx.RequireData(texturesPath, $"C1 textures");
        string missionZrdr = SessionPaths.MissionZrdr(ctx.DataRoot, "C1", "IA1");
        ctx.RequireData(missionZrdr, $"C1/IA1 zrdr");
        ctx.RequireData(ctx.MessagesPath, $"messages.json");

        // ⚠ Do not remove this eviction. The stunt runs fly aircraft through C1's zone positions in
        // the host's one physics space. A cached collidable C1 world from an earlier suite crashes them there.
        ctx.EvictCollidableWorlds();

        var planesGamez = GameZ.Load(ctx.PlanesGamezPath);
        var weaponDefs = WeaponDefs.Load(ctx.ZrdrPath, null);
        var textures = new TextureArchive(texturesPath);
        ProjectilePool? pool = null;
        var spawned = new List<FlightController>();
        try
        {
            var live = new ProjectilePool(textures, null, null);
            pool = live;
            ctx.Host.AddChild(live);
            var spec = SessionSpec.Parse(System.Array.Empty<string>());
            var liveries = new LiveryResolver(spec, Path.Combine(ctx.DataRoot, "extracted", "rof"));
            var inputs = new AircraftAssemblyResources
            {
                PlanesGamez = planesGamez,
                StatsFor = plane => PlaneStats.Load(ctx.ZrdrPath, plane),
                AiStatsFor = (plane, aiDef) => PlaneStats.LoadForAi(ctx.ZrdrPath, plane, aiDef),
                PaintRng = new RandomNumberGenerator(),
                ZrdrPath = ctx.ZrdrPath,
                StockLoadouts = StockLoadouts.Load(),
                WeaponDefs = weaponDefs,
                Textures = textures,
                Shakes = ShakeDefs.Load(ctx.ZrdrPath),
            };
            var spawner = new FlightRoster(FlightRosterPolicy.From(spec), liveries, null!, ctx.Host, inputs, new FlightWorldBindings { Projectiles = live, Gamez = planesGamez }, new HumanRosterBindings());
            string enemyNode = InstantAction.PlaneNodeFor("Warhawk")!;

            FlightController SpawnAt(Vector3 pos, int team, bool inert = false)
            {
                var pilot = AiPilot.HoldingCourse(pos, pos + Vector3.Forward);
                var fc = spawner.SpawnAi(new AiSpawn(enemyNode, pos, pos + Vector3.Forward, pilot,
                    Scheme: null, Team: team, Inert: inert));
                spawned.Add(fc);
                return fc;
            }

            // ---- dogfight_ace: the ace's own Downed report ----------------------------------
            var aceMission = new InstantActionRuntime(EndDef(ctx, "ace", "dogfight_ace"));
            var notAceMission = new InstantActionRuntime(EndDef(ctx, "squadron", "dogfight_squadron"));
            var ace = SpawnAt(new Vector3(0f, 500f, 0f), InstantActionRuntime.EnemyTeam);
            ace.Downed += (_, _) =>
            {
                aceMission.ReportObjective(InstantActionObjective.AceDown);
                notAceMission.ReportObjective(InstantActionObjective.AceDown);
            };
            ctx.Check(!aceMission.Ended, $"the ace duel is running before the ace goes down");
            ace.DebugForceCrash();
            ctx.Check(aceMission.Outcome == InstantActionOutcome.Won,
                $"the ace's own Downed report WINS a dogfight_ace mission: {aceMission.Outcome}");
            ctx.Check(!notAceMission.Ended,
                $"…and the same report leaves a dogfight_squadron mission running: {notAceMission.Outcome}");

            // ---- the hold between the ending and the wrap-up board ---------------------------
            // Its own ace and its own runtime. The win still arrives through a real Downed
            // report, after the mission clock has run.
            const float HoldDt = 1f / 60f;
            var holdMission = new InstantActionRuntime(EndDef(ctx, "hold", "dogfight_ace"));
            var heldAce = SpawnAt(new Vector3(600f, 500f, 0f), InstantActionRuntime.EnemyTeam);
            heldAce.Downed += (_, _) => holdMission.ReportObjective(InstantActionObjective.AceDown);
            var boardsDue = new List<InstantActionOutcome>();
            holdMission.WrapupDue += outcome => boardsDue.Add(outcome);
            holdMission.RegisterPilot(0);
            holdMission.Advance(12.5f);
            heldAce.DebugForceCrash();
            float clockAtEnd = holdMission.Elapsed;
            ctx.Check(holdMission.Outcome == InstantActionOutcome.Won && holdMission.HoldingWrapup
                    && boardsDue.Count == 0,
                $"the win decides the mission and the board is NOT presented with it: {boardsDue.Count} board(s)");
            for (float t = 0f; t < InstantActionRuntime.WrapupHoldS - 0.2f; t += HoldDt)
            {
                holdMission.Advance(HoldDt);
            }
            ctx.Check(boardsDue.Count == 0 && holdMission.HoldingWrapup,
                $"…still none 0.2 s short of the {InstantActionRuntime.WrapupHoldS:0.#} s hold (the able-to-fail control)");
            // A hull lost inside the hold is not the sortie: the result and the four counters were
            // settled at the ending. The same call on a RUNNING mission spends the life and loses
            // it, two blocks below, which is this one's able-to-fail control.
            int livesAtEnd = holdMission.LivesLeft(0);
            bool fliesOnAfterWin = holdMission.NotifyPilotDown(0);
            ctx.Check(!fliesOnAfterWin && holdMission.Outcome == InstantActionOutcome.Won
                    && holdMission.LivesLeft(0) == livesAtEnd && !holdMission.IsSpectating(0),
                $"a death reported inside the hold spends no life and leaves the win standing: {holdMission.Outcome}, {holdMission.LivesLeft(0)} of {livesAtEnd} life/lives, spectating={holdMission.IsSpectating(0)}");
            holdMission.Advance(0.25f);
            ctx.Check(boardsDue.Count == 1 && boardsDue[0] == InstantActionOutcome.Won
                    && !holdMission.HoldingWrapup,
                $"…and the whole hold spent presents it once, won: {boardsDue.Count} board(s)");
            holdMission.Advance(5f);
            ctx.Check(boardsDue.Count == 1, $"…once only, however long the world runs on: {boardsDue.Count}");
            ctx.Check(Mathf.Abs(holdMission.Elapsed - clockAtEnd) < 1e-4f,
                $"and the mission clock the board reads stayed at the ending: {holdMission.Elapsed:0.000} s vs {clockAtEnd:0.000} s");

            // ---- dogfight_squadron: the sequencer's exhausted counter ------------------------
            var squadron = new InstantActionRuntime(EndDef(ctx, "squadron2", "dogfight_squadron"));
            var notSquadron = new InstantActionRuntime(EndDef(ctx, "zeppelin0", "zeppelin_run"));
            var waves = new InstantActionWaves(new[] { 2, 1, 0, 0 });
            var wave1 = new List<FlightController>
            {
                SpawnAt(new Vector3(200f, 500f, 0f), InstantActionRuntime.EnemyTeam),
                SpawnAt(new Vector3(220f, 500f, 0f), InstantActionRuntime.EnemyTeam),
            };
            var wave2 = new List<FlightController>
            {
                SpawnAt(new Vector3(240f, 500f, 0f), InstantActionRuntime.EnemyTeam, inert: true),
            };
            ctx.Same(1, waves.Start(), $"wave 1 is current at mission start");

            // GameSession.StepInstantAction's own loop body, over the real rosters.
            void StepWaves(List<FlightController> roster)
            {
                int next = waves.Step(roster.Count(m => m.InPlay));
                if (next == 2)
                {
                    wave2[0].Activate(new Vector3(2000f, 500f, 0f), new Vector3(2000f, 500f, 100f));
                }
                else if (next == 0 && waves.Finished)
                {
                    squadron.ReportObjective(InstantActionObjective.WavesCleared);
                    notSquadron.ReportObjective(InstantActionObjective.WavesCleared);
                }
            }

            foreach (var m in wave1)
            {
                m.DebugForceCrash();
            }
            StepWaves(wave1);
            ctx.Check(waves.CurrentWave == 2 && wave2[0].InPlay,
                $"wave 1 cleared: wave 2 is current and flying, the mission is NOT over yet");
            ctx.Check(!squadron.Ended, $"…and the squadron mission is still running with a wave left");
            wave2[0].DebugForceCrash();
            StepWaves(wave2);
            ctx.Check(waves.Finished && squadron.Outcome == InstantActionOutcome.Won,
                $"the last wave's last kill WINS a dogfight_squadron mission: {squadron.Outcome}");
            ctx.Check(!notSquadron.Ended,
                $"…and a zeppelin run clearing its waves the same way is NOT won: {notSquadron.Outcome}");

            // The lives ledger's two ends on a real aircraft: the mechanism is FlightController's own
            // crash/respawn path, which an AI-piloted aircraft takes byte for byte, so the probe is a spawned
            // plane rather than a rig. What is under test is the arming and the Spectating pin.
            var lifeLedger = new InstantActionRuntime(EndDef(ctx, "lives3", "dogfight_squadron", lives: 3));
            var probe = SpawnAt(new Vector3(-400f, 500f, 0f), AimAssist.PlayerTeam);
            lifeLedger.RegisterPilot(probe.PlayerIndex);
            var respawnBoards = new List<InstantActionOutcome>();
            lifeLedger.WrapupDue += outcome => respawnBoards.Add(outcome);
            probe.AutoRespawnAfter = 3f;
            probe.DebugForceCrash();
            ctx.Check(probe.Crashed && lifeLedger.NotifyPilotDown(probe.PlayerIndex),
                $"3 lives, first death: the ledger says fly again ({lifeLedger.LivesLeft(probe.PlayerIndex)} left)");
            for (int i = 0; i < 300; i++)
            {
                probe.SimStep(1f / 60f);
            }
            ctx.Check(!probe.Crashed,
                $"…and 5 s later the armed 3 s crash cam has respawned it, the able-to-fail control");
            // ⚠ The respawn path must stay clear of the ending's hold. A death with a life left
            // ends nothing, so nothing is armed and no board is ever due.
            lifeLedger.Advance(InstantActionRuntime.WrapupHoldS + 1f);
            ctx.Check(!lifeLedger.Ended && !lifeLedger.HoldingWrapup && respawnBoards.Count == 0,
                $"…and that death armed NO wrap-up hold: {lifeLedger.Outcome}, {respawnBoards.Count} board(s) due");

            var lastLife = new InstantActionRuntime(EndDef(ctx, "lives1", "dogfight_squadron"));
            lastLife.RegisterPilot(probe.PlayerIndex);
            probe.DebugForceCrash();
            bool fliesAgain = lastLife.NotifyPilotDown(probe.PlayerIndex);
            probe.Spectating = !fliesAgain;
            ctx.Check(!fliesAgain && lastLife.IsSpectating(probe.PlayerIndex),
                $"the default 1 life sends the same pilot straight to spectate on its first death");
            ctx.Check(lastLife.Outcome == InstantActionOutcome.Lost,
                $"…and with no other human alive the mission is LOST: {lastLife.Outcome}");
            for (int i = 0; i < 600; i++)
            {
                probe.SimStep(1f / 60f);
            }
            ctx.Check(probe.Crashed,
                $"…and 10 s later the wreck is still there: Spectating outranks the armed timer");

            // ---- the hold's input half -------------------------------------------------------
            // ⚠ Not a halt: the seat still steps and still carries the lever it was left on.
            // What the hold takes away is the command, which is why the stick is full deflection.
            var seatStats = PlaneStats.Load(ctx.ZrdrPath, ctx.PlaneName);
            var seatModel = new PlaneBuilder(planesGamez, textures).Build(ctx.PlaneName);
            var seat = new FlightController();
            seat.Bind(new FlightControllerBuild
            {
                PlaneModel = seatModel,
                Collider = PlaneCollider.Build(seatModel),
                Damage = new PlaneDamage(seatStats.DestroyableParts),
                PlayerIndex = 0,
                IsHumanPiloted = true,
                HoldSegments = new[] { (new FlightInput { Pitch = 1f, Roll = 1f, Throttle = 1f }, 0f) },
                UseKeyboard = false,
                PadDevices = System.Array.Empty<int>(),
                AllowPause = false,
            });
            const float SeatLever = 0.6f;
            seat.Setup(new FlightModel(seatStats), null, new CamParams(),
                new Vector3(0f, 2000f, 2000f), new Vector3(0f, 2000f, 1900f), SeatLever, 80f);
            ctx.Host.AddChild(seat);
            spawned.Add(seat);
            // The four selector readings with each selector action held down or let go, through the
            // seat's own production reads.
            FireInputs Selectors(bool down)
            {
                seat.HoldActionForTest(InputAction.SelectGunGroup, down);
                seat.HoldActionForTest(InputAction.SelectGunGroupPrev, down);
                seat.HoldActionForTest(InputAction.SelectOrdnance, down);
                seat.HoldActionForTest(InputAction.SelectOrdnancePrev, down);
                return seat.SelectorInputsForTest();
            }

            void StepSeat(int frames)
            {
                for (int i = 0; i < frames; i++)
                {
                    seat.SimStep(1f / 60f);
                }
            }

            seat.ControlHold = FlightControlHold.All;
            StepSeat(60);
            var held = seat.LastCommand;
            ctx.Check(held.Pitch == 0f && held.Roll == 0f && held.Yaw == 0f
                    && Mathf.Abs(held.Throttle - SeatLever) < 0.01f,
                $"a wholly held seat commands nothing over the lever it was left on: pitch={held.Pitch:0.00} roll={held.Roll:0.00} throttle={held.Throttle:0.00}");

            // The win's hold: the original flies on through it, so the stick and the throttle are
            // the pilot's and only the discrete commands go.
            seat.ControlHold = FlightControlHold.CommandsOnly;
            StepSeat(60);
            var flown = seat.LastCommand;
            ctx.Check(flown.Pitch > 0.5f && flown.Roll > 0.5f && flown.Throttle > SeatLever,
                $"the win's hold leaves the SAME stick flying it: pitch={flown.Pitch:0.00} roll={flown.Roll:0.00} throttle={flown.Throttle:0.00}");
            var swallowed = Selectors(true);
            ctx.Check(!seat.GunTriggerReadsForTest(true) && !seat.RocketTriggerReadsForTest(true)
                    && !swallowed.GunSelectHeld && !swallowed.RocketSelectHeld
                    && !swallowed.GunSelectBackHeld && !swallowed.RocketSelectBackHeld
                    && !swallowed.GunSelectPadHeld && !swallowed.RocketSelectPadHeld,
                $"…and both triggers and all four selectors are still swallowed under it: guns={swallowed.GunSelectHeld}/{swallowed.GunSelectBackHeld}/{swallowed.GunSelectPadHeld} rockets={swallowed.RocketSelectHeld}/{swallowed.RocketSelectBackHeld}/{swallowed.RocketSelectPadHeld}");
            seat.ControlHold = FlightControlHold.None;
            var answered = Selectors(true);
            ctx.Check(seat.GunTriggerReadsForTest(true) && seat.RocketTriggerReadsForTest(true)
                    && answered.GunSelectHeld && answered.RocketSelectHeld
                    && answered.GunSelectBackHeld && answered.RocketSelectBackHeld
                    && answered.GunSelectPadHeld && answered.RocketSelectPadHeld,
                $"…the same controls answering the moment the hold ends, the able-to-fail control: guns={answered.GunSelectHeld}/{answered.GunSelectBackHeld}/{answered.GunSelectPadHeld} rockets={answered.RocketSelectHeld}/{answered.RocketSelectBackHeld}/{answered.RocketSelectPadHeld}");
            Selectors(false);
            seat.GunTriggerReadsForTest(false);
            seat.RocketTriggerReadsForTest(false);
            StepSeat(60);
            ctx.Check(seat.LastCommand.Pitch > 0.5f && seat.LastCommand.Roll > 0.5f,
                $"…and the stick still flies it with nothing held at all: pitch={seat.LastCommand.Pitch:0.00} roll={seat.LastCommand.Roll:0.00}");

            // A hull lost inside the win's hold falls for the rest of it. R is swallowed and the
            // armed crash cam is held off with it. Nothing comes back before the board.
            seat.AutoRespawnAfter = 0.5f;
            seat.ControlHold = FlightControlHold.CommandsOnly;
            seat.HoldActionForTest(InputAction.Respawn, true);
            seat.DebugForceCrash();
            StepSeat((int)(InstantActionRuntime.WrapupHoldS * 60f) + 60);
            ctx.Check(seat.Crashed,
                $"a crash inside the win's hold stays down for the whole hold with R held: crashed={seat.Crashed}");
            seat.ControlHold = FlightControlHold.None;
            StepSeat(2);
            ctx.Check(!seat.Crashed,
                $"…and the same still-held R brings it back the moment the hold ends, the able-to-fail control: crashed={seat.Crashed}");
            seat.HoldActionForTest(InputAction.Respawn, false);

            // ---- the hold's handover to the board ---------------------------------------------
            // The real board over the real hold, wired as the director wires it. No menu exists
            // while the world flies, and the menu built with the board answers its first press.
            var handover = new InstantActionRuntime(EndDef(ctx, "handover", "dogfight_ace"));
            var boardState = new PauseState();
            var board = IaWrapupBoard.Build("ia-end handover", exitsToMenu: true, boardState,
                _ => new MenuInput());
            ctx.Host.AddChild(board);
            try
            {
                int restarts = 0;
                board.Restart = () => restarts++;
                handover.MissionEnded += _ => seat.ControlHold = FlightControlHold.CommandsOnly;
                handover.WrapupDue += outcome =>
                {
                    seat.ControlHold = FlightControlHold.None;
                    board.Present(outcome == InstantActionOutcome.Won, handover.Elapsed, 1, 0, 50);
                };
                handover.ReportObjective(InstantActionObjective.AceDown);
                ctx.Check(!board.Visible && board.StandardMenu == null
                        && seat.ControlHold == FlightControlHold.CommandsOnly,
                    $"through the hold there is no board and no menu to press: visible={board.Visible} hold={seat.ControlHold}");
                handover.Advance(InstantActionRuntime.WrapupHoldS);
                ctx.Check(board.Visible && restarts == 0
                        && seat.ControlHold == FlightControlHold.None,
                    $"the board takes the screen with the seats already released: visible={board.Visible} hold={seat.ControlHold} restarts={restarts}");
                board.StandardMenu!.Handle(1, accept: true, back: false);
                ctx.Check(restarts == 1 && !board.Visible,
                    $"…and its Restart row answers the first press after it appears: restarts={restarts} visible={board.Visible}");
            }
            finally
            {
                board.Free();
            }

            StuntRunFliesOn(ctx, seat, missionZrdr);
            SplitscreenStuntRunEndsOnTheHold(ctx, planesGamez, textures, live, missionZrdr);
        }
        finally
        {
            pool?.Free();
            foreach (var fc in spawned)
            {
                fc.Free();
            }
            textures.Dispose();
        }

        // ---- stunt_flying: StuntRace's all-finished path over the authored zones -------------
        ctx.WithWorld("C1", collision: false, mission: "IA1", world =>
        {
            var zones = StuntMission.Load(world.Gamez, missionZrdr, Messages.Load(ctx.MessagesPath));
            ctx.Check(zones != null, $"C1/IA1 ships danger zones for a stunt_flying mission");
            if (zones == null)
            {
                return;
            }
            var stunt = new InstantActionRuntime(EndDef(ctx, "stunt", "stunt_flying"));
            var notStunt = new InstantActionRuntime(EndDef(ctx, "ace2", "dogfight_ace"));
            var race = new StuntRace();
            var second = zones.ForAnotherPlayer();
            race.Add(0, zones, "P1");
            race.Add(1, second, "P2");
            // GameSession.CheckInstantActionZoneSets, over the same predicate it calls: the two
            // pilots' runs, with P2's out-of-lives state under the suite's control.
            bool p2OutOfLives = false;
            void CheckZoneSets()
            {
                var pilots = new[] { (false, zones.AllComplete), (p2OutOfLives, second.AllComplete) };
                if (InstantActionRuntime.ZoneSetsFlown(pilots))
                {
                    stunt.ReportObjective(InstantActionObjective.ZonesFlown);
                    notStunt.ReportObjective(InstantActionObjective.ZonesFlown);
                }
            }

            zones.RunCompleted += CheckZoneSets;
            second.RunCompleted += CheckZoneSets;

            // Decision 10: all-finished, never first past the post.
            zones.DebugCompleteAll();
            ctx.Check(zones.AllComplete && !race.AllFinished && !stunt.Ended,
                $"the FIRST pilot's zone set is flown and the mission runs on ({race.FinishedCount} of 2 in)");
            second.DebugCompleteAll();
            ctx.Check(race.AllFinished && stunt.Outcome == InstantActionOutcome.Won,
                $"the last pilot in WINS a stunt_flying mission: {stunt.Outcome}");
            ctx.Check(!notStunt.Ended,
                $"…and the same report leaves a dogfight_ace mission running: {notStunt.Outcome}");

            // The same field with P2 out of lives instead: a pilot who can never clear another
            // gate must not hold the mission open, which is what StuntRace's own all-finished
            // rule alone would do (AllFinished is still false here).
            var outOfLives = new InstantActionRuntime(EndDef(ctx, "stunt-out", "stunt_flying"));
            var rerun = zones.ForAnotherPlayer();
            var stranded = zones.ForAnotherPlayer();
            rerun.RunCompleted += () =>
            {
                if (InstantActionRuntime.ZoneSetsFlown(new[] { (false, rerun.AllComplete), (true, stranded.AllComplete) }))
                {
                    outOfLives.ReportObjective(InstantActionObjective.ZonesFlown);
                }
            };
            rerun.DebugCompleteAll();
            ctx.Check(!stranded.AllComplete && outOfLives.Outcome == InstantActionOutcome.Won,
                $"the last FLYING pilot's zone set wins it with the other out of lives: {outOfLives.Outcome}");
        });

        // ---- zeppelin_run: the objective's real death ----------------------------------------
        string m04Zrdr = SessionPaths.MissionZrdr(ctx.DataRoot, "C1", "M04");
        ctx.RequireData(m04Zrdr, $"C1/M04 zrdr");
        var zepDefs = Zeppelins.Load(m04Zrdr);
        ctx.Check(zepDefs.Count == 1 && zepDefs[0].Node == "piratezep",
            $"C1/M04 authors the one zeppelin this mission is built around count={zepDefs.Count}");
        if (zepDefs.Count != 1)
        {
            return;
        }
        var zepNets = AiNets.Load(SessionPaths.ChapterZrdr(ctx.DataRoot, "C1"));
        ctx.WithWorld("C1", collision: true, mission: "M04", world =>
        {
            var runtime = world.Session.Runtime;
            var host = runtime.FindNodes("piratezep").FirstOrDefault();
            ctx.Check(host != null, $"the piratezep world node resolves");
            if (host == null)
            {
                return;
            }
            ZeppelinRuntime? zeps = null;
            try
            {
                zeps = new ZeppelinRuntime(zepDefs,
                    name => runtime.FindNodes(name) is { Count: > 0 } hits ? hits[0] : null, zepNets);
                zeps.WireDamage(runtime);

                // The node filter GameSession puts on both subscriptions: a signal from THIS
                // world's zeppelin wins a mission whose objective is it, and never one naming
                // another node.
                void Report(string node, params InstantActionRuntime[] missions)
                {
                    foreach (var ia in missions)
                    {
                        if (string.Equals(node, InstantActionRuntime.SelectedZeppelinNode(ia.Def),
                                System.StringComparison.OrdinalIgnoreCase))
                        {
                            ia.ReportObjective(InstantActionObjective.ZeppelinDisabled);
                        }
                    }
                }

                // ---- path 1: the engines, which is what the mode is FOR --------------------
                var engineMission = new InstantActionRuntime(EndDef(ctx, "zep-engines",
                    "zeppelin_run", cargoZeppelin: "piratezep"));
                var otherHull = new InstantActionRuntime(EndDef(ctx, "zep-other", "zeppelin_run",
                    cargoZeppelin: "someotherzep"));
                zeps.ZeppelinEnginesDisabled += n => Report(n, engineMission, otherHull);
                zeps.ZeppelinKilled += n => Report(n, engineMission, otherHull);
                ctx.Check(InstantActionRuntime.SelectedZeppelinNode(engineMission.Def) == "piratezep",
                    $"the mission's objective resolves to the world's zeppelin");

                var engines = zepDefs[0].Engines;
                var motion = zeps.MotionFor("piratezep");
                ctx.Check(motion != null && engines.Count > 0 && motion.AliveEngines == engines.Count,
                    $"all {engines.Count} of piratezep's authored engines start alive: {motion?.AliveEngines}");
                for (int i = 0; i < engines.Count; i++)
                {
                    runtime.DamageAt(runtime.FindNodes(engines[i], host).FirstOrDefault(), 10_000f);
                    zeps.SimStep(1f / 60f);
                    if (i == engines.Count - 2)
                    {
                        // The able-to-fail control on the win below: one engine short is not it.
                        ctx.Check(!engineMission.Ended && motion?.AliveEngines == 1,
                            $"with ONE engine left the mission runs on: alive={motion?.AliveEngines} outcome={engineMission.Outcome}");
                    }
                }
                ctx.Check(motion?.AliveEngines == 0
                    && engineMission.Outcome == InstantActionOutcome.Won,
                    $"the last engine dying WINS a zeppelin_run mission: alive={motion?.AliveEngines} outcome={engineMission.Outcome}");
                ctx.Check(!zeps.IsDead("piratezep"),
                    $"…with the HULL still alive, engines are their own win, not a kill");
                ctx.Check(!otherHull.Ended,
                    $"…and a mission whose objective is another hull is untouched: {otherHull.Outcome}");

                // ---- path 2: the hull, which also wins the mode ----------------------------
                // Subscribed only now, so the engines signal already fired above cannot be what
                // ends it: this runtime sees the gasbag threshold and nothing else.
                var hullMission = new InstantActionRuntime(EndDef(ctx, "zep-hull", "zeppelin_run",
                    cargoZeppelin: "piratezep"));
                zeps.ZeppelinKilled += n => Report(n, hullMission);

                // The decoded survivor threshold does the killing (4 of 6 required): two gasbags
                // down is not enough, the third is.
                runtime.DamageAt(runtime.FindNodes("gasbag1", host).FirstOrDefault(), 10_000f);
                runtime.DamageAt(runtime.FindNodes("gasbag2", host).FirstOrDefault(), 10_000f);
                zeps.SimStep(1f / 60f);
                ctx.Check(!zeps.IsDead("piratezep") && !hullMission.Ended,
                    $"two gasbags down: the hull lives and the mission runs on");
                runtime.DamageAt(runtime.FindNodes("gasbag3", host).FirstOrDefault(), 10_000f);
                zeps.SimStep(1f / 60f);
                ctx.Check(zeps.IsDead("piratezep")
                    && hullMission.Outcome == InstantActionOutcome.Won,
                    $"the objective's real death ALSO wins a zeppelin_run mission: {hullMission.Outcome}");
            }
            finally
            {
                zeps?.Free();
            }
        });
    }

    // A hand-authored `--ia=` file for one end-condition case, written to the
    // scratch folder and read back through the REAL reader, so a change to how
    // `mission_type`/`lives` parse moves this suite too, and no test builds an
    // `InstantActionDef` the CLI could not produce.
    internal static InstantActionDef EndDef(TestContext ctx, string tag, string missionType,
        int? lives = null, string? cargoZeppelin = null)
    {
        string json = $"{{\"mission_type\": \"{missionType}\""
            + (lives is { } n ? $", \"lives\": {n}" : string.Empty)
            + (cargoZeppelin != null ? $", \"cargo_zeppelin\": \"{cargoZeppelin}\"" : string.Empty)
            + "}";
        string name = $"ia-end-{tag}.json";
        ctx.WriteArtifact(name, json);
        return InstantAction.LoadFromJson(Path.Combine(ctx.ScratchDir, name));
    }

    // The wrap-up board's two shot counters (docs/formats/instant-action.md): ProjectilePool is the
    // single choke point for both, so this fires real rounds through the real pool at a real target
    // rather than asserting on the arithmetic in isolation. The board's other two rows are a live read
    // of StuntMission.CompletedCount and a Downed tally already exercised by InstantActionEnd.
    [Suite("instant-action-wrapup",
        "the G14 wrap-up board's two shot counters, ScoredShooters-filtered exactly as the " +
        "decode's own 'the local player' is: a scored shooter's cannon round counts as both " +
        "fired and hit, an unscored (AI) shooter's identical shot moves neither counter, and " +
        "a scored shooter's ROCKET (not CANNON) round is excluded from both; and over a C1/IA1 " +
        "stunt run the board's three photographs stand in one row, the cursor enters them up off " +
        "Photo Mode, opens one full size and closes it on its cell, and refuses a pending one")]
    internal static void InstantActionWrapup(TestContext ctx)
    {
        ctx.RequireData(ctx.PlanesGamezPath, $"planes gamez");
        ctx.RequireData(ctx.ZrdrPath, $"zrdr archive");
        string texturesPath = SessionPaths.ChapterTextures(ctx.DataRoot, "C1");
        ctx.RequireData(texturesPath, $"C1 textures");

        var planesGamez = GameZ.Load(ctx.PlanesGamezPath);
        var weaponDefs = WeaponDefs.Load(ctx.ZrdrPath, null);
        WeaponDef? cannon = weaponDefs.All.FirstOrDefault(w => w.IsCannon && w.ArmorDamage is > 0f);
        WeaponDef? rocket = weaponDefs.All.FirstOrDefault(w => w.IsRocket);
        ctx.Check(cannon != null && rocket != null,
            $"a CANNON gun and a rocket both exist in the data (cannon={cannon != null} rocket={rocket != null})");
        if (cannon == null || rocket == null)
            return;

        var textures = new TextureArchive(texturesPath);
        ProjectilePool? pool = null;
        FlightController? target = null;
        try
        {
            var live = new ProjectilePool(textures, null, null);
            pool = live;
            ctx.Host.AddChild(live);

            var spec = SessionSpec.Parse(System.Array.Empty<string>());
            var liveries = new LiveryResolver(spec, Path.Combine(ctx.DataRoot, "extracted", "rof"));
            var inputs = new AircraftAssemblyResources
            {
                PlanesGamez = planesGamez,
                StatsFor = plane => PlaneStats.Load(ctx.ZrdrPath, plane),
                AiStatsFor = (plane, aiDef) => PlaneStats.LoadForAi(ctx.ZrdrPath, plane, aiDef),
                PaintRng = new RandomNumberGenerator(),
                ZrdrPath = ctx.ZrdrPath,
                StockLoadouts = StockLoadouts.Load(),
                WeaponDefs = weaponDefs,
                Textures = textures,
                Shakes = ShakeDefs.Load(ctx.ZrdrPath),
            };
            var spawner = new FlightRoster(FlightRosterPolicy.From(spec), liveries, null!, ctx.Host, inputs, new FlightWorldBindings { Projectiles = live, Gamez = planesGamez }, new HumanRosterBindings());

            var pos = new Vector3(0f, 500f, 0f);
            target = spawner.SpawnAi(new AiSpawn(ctx.PlaneName, pos, pos + Vector3.Forward,
                AiPilot.HoldingCourse(pos, pos + Vector3.Forward),
                Scheme: null, Team: InstantActionRuntime.EnemyTeam));
            ctx.Check(target?.Body != null, $"the target built a collision body");
            if (target?.Body == null)
                return;

            // ScoredShooters: shooter 0 stands in for a registered human seat, 999 for an
            // AI's shooter id, which a mission never adds to the set.
            live.ScoredShooters.Add(0);

            var muzzle = new Transform3D(
                Basis.LookingAt(Vector3.Back, Vector3.Up), pos + new Vector3(0f, 0f, -20f));
            void FireOnce(WeaponDef weapon, int shooterId)
            {
                live.Spawn(weapon, muzzle, Vector3.Zero, shooterId: shooterId);
                for (int i = 0; i < 20; i++)
                    live.SimStep(1f / 60f);
                live.Clear();
            }

            FireOnce(cannon, shooterId: 0);
            ctx.Check(live.CannonRoundsFired == 1 && live.CannonHits == 1,
                $"a scored shooter's cannon round counts as both fired and hit: fired={live.CannonRoundsFired} hits={live.CannonHits}");

            FireOnce(cannon, shooterId: 999);
            ctx.Check(live.CannonRoundsFired == 1 && live.CannonHits == 1,
                $"an unscored shooter's identical shot moves neither counter: fired={live.CannonRoundsFired} hits={live.CannonHits}");

            FireOnce(rocket, shooterId: 0);
            ctx.Check(live.CannonRoundsFired == 1 && live.CannonHits == 1,
                $"a scored shooter's ROCKET is excluded from the cannon-only counters: fired={live.CannonRoundsFired} hits={live.CannonHits}");
        }
        finally
        {
            pool?.Free();
            target?.Free();
            textures.Dispose();
        }

        string missionZrdr = SessionPaths.MissionZrdr(ctx.DataRoot, "C1", "IA1");
        ctx.RequireData(missionZrdr, $"C1/IA1 zrdr");
        ctx.RequireData(ctx.MessagesPath, $"messages.json");
        if (StuntMission.Load(GameZ.Load(SessionPaths.ChapterGamez(ctx.DataRoot, "C1")), missionZrdr,
                Messages.Load(ctx.MessagesPath)) is { } run)
        {
            StuntCaptureSuites.CheckShortRunWrapup(ctx, run, "C1");
        }
        else
        {
            ctx.Check(false, $"C1/IA1 ships Danger Zones for the wrap-up board's photographs");
        }
    }

    // BL-426: InstantActionDirector.BuildStuntSummary over real StuntMission runs and a real
    // ScoreStore, pointed at a throwaway file under ctx.ScratchDir rather than the player's own
    // user://stunt_scores.json (the hard rule the fixture must not cross). C1/IA1 supplies real
    // Danger Zones; DebugCompleteAll drives AllComplete the same way --debug-scoreboard does.
    [Suite("instant-action-stunt-summary",
        "BL-426's per-pilot record gate over a real ScoreStore: an incomplete run's summary " +
        "shows the stored best and claims no new one, and its elapsed total, shorter than that " +
        "stored best, never overwrites it (the store file is byte-identical before and after); " +
        "a completed run records and the reload confirms it; and the split splitscreen end " +
        "(one pilot's own run complete, the other's not) is decided from each pilot's own " +
        "StuntMission alone, never a shared mission outcome")]
    internal static void InstantActionStuntSummary(TestContext ctx)
    {
        string chapter = "C1", mission = "IA1";
        ctx.RequireData(ctx.PlanesGamezPath, $"planes gamez");
        string gamezPath = SessionPaths.ChapterGamez(ctx.DataRoot, chapter);
        ctx.RequireData(gamezPath, $"{chapter} gamez");
        string missionZrdr = SessionPaths.MissionZrdr(ctx.DataRoot, chapter, mission);
        ctx.RequireData(missionZrdr, $"{chapter}/{mission} zrdr");
        ctx.RequireData(ctx.MessagesPath, $"messages.json");

        var gamez = GameZ.Load(gamezPath);
        var messages = Messages.Load(ctx.MessagesPath);
        StuntMission Fresh() => StuntMission.Load(gamez, missionZrdr, messages)
            ?? throw new SuiteSkippedException($"{chapter}/{mission} ships no Danger Zones");

        string storePath = Path.Combine(ctx.ScratchDir, "StuntSummary", "stunt_scores.json");
        Directory.CreateDirectory(Path.GetDirectoryName(storePath)!);
        if (File.Exists(storePath))
        {
            File.Delete(storePath);
        }
        const string key = "instant-action-stunt-summary/IA1/player_test";

        // A failed run: no best on file yet, so none is claimed, and nothing is written.
        var failed = Fresh();
        failed.Tick(3f);
        var store = ScoreStore.Load(storePath);
        var lostSummary = InstantActionDirector.BuildStuntSummary(failed, store, key);
        ctx.Check(lostSummary.PrevBest == null && !lostSummary.NewBest,
            $"a lost run over an empty store shows no best and claims none: prev={lostSummary.PrevBest} new={lostSummary.NewBest}");
        ctx.Check(!File.Exists(storePath),
            $"…and the store file is not created by a run that never qualified");

        // A completed run: records, and a fresh Load() proves it persisted.
        var completed = Fresh();
        completed.DebugCompleteAll();
        float bestTotal = completed.Elapsed;
        store = ScoreStore.Load(storePath);
        var wonSummary = InstantActionDirector.BuildStuntSummary(completed, store, key);
        ctx.Check(wonSummary.PrevBest == null && wonSummary.NewBest,
            $"a completed run over an empty store claims NEW BEST: prev={wonSummary.PrevBest} new={wonSummary.NewBest}");
        ctx.Check(ScoreStore.Load(storePath).GetBest(key) == bestTotal,
            $"…and a reload finds it recorded: {ScoreStore.Load(storePath).GetBest(key)}");

        // A second, faster run that FAILS must not beat the real record it undercuts only by
        // ending early, the bug this item closes. Byte-compare the file to prove no write ran.
        byte[] before = File.ReadAllBytes(storePath);
        var fasterButLost = Fresh();
        fasterButLost.Tick(1f);
        ctx.Check(fasterButLost.Elapsed < bestTotal,
            $"the fixture's failed run is the shorter total: {fasterButLost.Elapsed} < {bestTotal}");
        store = ScoreStore.Load(storePath);
        var undercutSummary = InstantActionDirector.BuildStuntSummary(fasterButLost, store, key);
        ctx.Check(undercutSummary.PrevBest == bestTotal && !undercutSummary.NewBest,
            $"a shorter but INCOMPLETE run still shows the real best and claims none: prev={undercutSummary.PrevBest} new={undercutSummary.NewBest}");
        ctx.Check(before.SequenceEqual(File.ReadAllBytes(storePath)),
            $"…and the store file is byte-identical afterwards, the early end never wrote");

        // The split splitscreen end: each pilot's own StuntMission decides its own summary: P1
        // complete records even though P2, sharing nothing but the mission clock, is not.
        var p1 = Fresh();
        p1.DebugCompleteAll();
        var p2 = Fresh();
        p2.Tick(2f); // still flying/short when the mission ends on P1's side
        store = ScoreStore.Load(storePath);
        var p1Summary = InstantActionDirector.BuildStuntSummary(p1, store, key + "/p1");
        var p2Summary = InstantActionDirector.BuildStuntSummary(p2, store, key + "/p2");
        ctx.Check(p1Summary.NewBest && !p2Summary.NewBest,
            $"a splitscreen end records the pilot whose OWN run completed and not the other: p1 new={p1Summary.NewBest} p2 new={p2Summary.NewBest}");
    }

    // The inert state: an aircraft built complete and held out until Activate.
    // ⚠ Compare a live control, inert aircraft, and that aircraft activated with real physics, targeting,
    // shots, and simulation; absence alone can pass for the wrong reason (METHOD-9/METHOD-10).

    // The shell contract every results board inherits, asserted once against a stub subclass, plus
    // the one deviant: IaWrapupBoard's menu-driven retire, asserted against the real board.
    [Suite("results-board-shell",
        "the ResultsBoard shell contract, once for all four results boards: waking raises " +
        "Ended, the resting Photo Mode row changes nothing, the standard Restart leaves the " +
        "release to the live flag, the flag clearing retires the board and releases the " +
        "clock, and the wrap-up board's own menu-driven retire, which no flag ever performs")]
    internal static void ResultsBoardShell(TestContext ctx)
    {
        var state = new PauseState();
        var stub = ShellProbeBoard.Build(state, _ => new MenuInput());
        ctx.Host.AddChild(stub);
        try
        {
            int restarts = 0, exits = 0, photos = 0;
            stub.Restart = () => restarts++;
            stub.Exit = () => exits++;
            stub.PhotoMode = () => photos++;

            stub.WakeWithMenu();
            ctx.Check(stub.Visible && state.Ended,
                $"waking shows the board and raises Ended: visible={stub.Visible} ended={state.Ended}");

            stub._Process(1.0 / 60.0);
            ctx.Check(stub.Visible && state.Ended,
                $"a board whose run is still over survives _Process: visible={stub.Visible} ended={state.Ended}");

            var menu = stub.StandardMenu!;
            menu.Handle(0, accept: true, back: false);
            ctx.Check(photos == 1 && restarts == 0 && exits == 0 && stub.Visible && state.Ended,
                $"the resting row is Photo Mode and it leaves the board and the halt untouched: photos={photos} restarts={restarts} exits={exits} visible={stub.Visible} ended={state.Ended}");

            menu.Handle(1, accept: true, back: false);
            ctx.Check(restarts == 1 && stub.Visible && state.Ended,
                $"the standard Restart invokes the callback but leaves the release to the live flag: restarts={restarts} visible={stub.Visible} ended={state.Ended}");

            stub.StillOver = false;
            stub._Process(1.0 / 60.0);
            ctx.Check(!stub.Visible && !state.Ended,
                $"the live flag clearing retires the board and releases the clock: visible={stub.Visible} ended={state.Ended}");

            stub.WakeWithMenu();
            menu = stub.StandardMenu!;
            menu.Handle(1, accept: false, back: false);
            menu.Handle(1, accept: true, back: false);
            ctx.Check(exits == 1 && restarts == 1 && state.Ended,
                $"Exit invokes its callback alone and leaves the halt for the session to resolve: exits={exits} restarts={restarts} ended={state.Ended}");
        }
        finally
        {
            stub.Free();
        }

        var wrapupState = new PauseState();
        var wrapup = IaWrapupBoard.Build("test", exitsToMenu: true, wrapupState, _ => new MenuInput());
        ctx.Host.AddChild(wrapup);
        try
        {
            int restarts = 0;
            wrapup.Restart = () => restarts++;
            wrapup.Present(won: true, elapsedSeconds: 61f, enemiesShotDown: 3, zonesCompleted: 2, shotPercent: 50);
            ctx.Check(wrapup.Visible && wrapupState.Ended,
                $"Present shows the wrap-up board and raises Ended: visible={wrapup.Visible} ended={wrapupState.Ended}");

            wrapup._Process(1.0 / 60.0);
            ctx.Check(wrapup.Visible && wrapupState.Ended,
                $"no live flag ever retires the wrap-up board: visible={wrapup.Visible} ended={wrapupState.Ended}");

            wrapup.StandardMenu!.Handle(1, accept: true, back: false);
            ctx.Check(restarts == 1 && !wrapup.Visible && !wrapupState.Ended,
                $"the wrap-up board's Restart retires and releases itself: restarts={restarts} visible={wrapup.Visible} ended={wrapupState.Ended}");
        }
        finally
        {
            wrapup.Free();
        }
    }

    // A solo Instant Action stunt run on the real seat, stepped through the win's hold the way the
    // director wires it. There is no scoreboard, the run completes inside SimStep, and the ending
    // sets CommandsOnly. The original flies on for the whole hold, and nothing is photographed
    // after the completing frame. The control hands the same seat a solo scoreboard, which holds
    // the finish pose.
    private static void StuntRunFliesOn(TestContext ctx, FlightController seat, string missionZrdr)
    {
        string gamezPath = SessionPaths.ChapterGamez(ctx.DataRoot, "C1");
        ctx.RequireData(gamezPath, $"C1 gamez");
        var run = StuntMission.Load(GameZ.Load(gamezPath), missionZrdr, Messages.Load(ctx.MessagesPath));
        ctx.Check(run is { TotalCount: >= 2 }, $"C1/IA1 ships at least two danger zones for the hold's stunt run");
        if (run is not { TotalCount: >= 2 })
        {
            return;
        }

        const float Dt = 1f / 60f;
        int stings = 0;
        int requests = 0;
        // The pane accepts and never lands, so a latch is counted and nothing is written.
        var camera = new StuntCapture(run, "C1", _ => ++requests > 0) { Sting = () => stings++ };
        var mission = new InstantActionRuntime(EndDef(ctx, "stunt-hold", "stunt_flying"));
        mission.RegisterPilot(seat.PlayerIndex);
        int boards = 0;
        void ZonesFlown()
        {
            if (InstantActionRuntime.ZoneSetsFlown(new[] { (false, run.AllComplete) }))
            {
                mission.ReportObjective(InstantActionObjective.ZonesFlown);
            }
        }

        run.RunCompleted += ZonesFlown;
        mission.MissionEnded += outcome => seat.ControlHold = outcome == InstantActionOutcome.Won
            ? FlightControlHold.CommandsOnly
            : FlightControlHold.All;
        mission.WrapupDue += _ =>
        {
            seat.ControlHold = FlightControlHold.None;
            boards++;
        };
        seat.ControlHold = FlightControlHold.None;
        seat.Stunt = run;
        seat.StuntShots = camera;
        seat.Scoreboard = null;
        seat.Race = null;
        try
        {
            var shot = run.Zones[0];
            var late = run.Zones[1];
            Vector3 Above(StuntZone zone) => zone.Position + new Vector3(0f, 500f, 0f);

            // The able-to-fail control for the camera: a marker crossed while the run is live latches.
            seat.WarpTo(Above(shot), 0f, 80f);
            seat.SimStep(Dt);
            seat.WarpTo(shot.Position, 0f, 80f);
            seat.SimStep(Dt);
            ctx.Check(camera.Count == 1 && stings == 1,
                $"{shot.DzName}, crossed while the run is live, is photographed on the real seat: shots={camera.Count} stings={stings}");

            // The completing frame: DebugCompleteStunt completes the run inside SimStep, ahead of
            // the return the solo scoreboard's finish pose takes, as the last gate pair would.
            seat.WarpTo(Above(late), 0f, 80f);
            seat.DebugCompleteStunt = true;
            seat.SimStep(Dt);
            seat.DebugCompleteStunt = false;
            mission.Advance(Dt);
            ctx.Check(run.AllComplete && mission.Outcome == InstantActionOutcome.Won && mission.HoldingWrapup
                    && seat.ControlHold == FlightControlHold.CommandsOnly,
                $"the stunt run completes on the seat and wins the mission into the hold: complete={run.AllComplete} {mission.Outcome} hold={seat.ControlHold}");

            var atEnd = seat.WorldPosition;
            int shotsAtEnd = camera.Count, stingsAtEnd = stings, requestsAtEnd = requests;
            int frames = 0;
            float lastStep = 0f, minStep = float.MaxValue;
            bool warped = false;
            while (boards == 0 && frames < (int)((InstantActionRuntime.WrapupHoldS + 1f) / Dt))
            {
                // Halfway through the hold, into a marker this run never photographed.
                if (!warped && frames * Dt >= InstantActionRuntime.WrapupHoldS / 2f)
                {
                    seat.WarpTo(late.Position, 0f, 80f);
                    warped = true;
                }
                var before = seat.WorldPosition;
                seat.SimStep(Dt);
                mission.Advance(Dt);
                frames++;
                lastStep = before.DistanceTo(seat.WorldPosition);
                minStep = Mathf.Min(minStep, lastStep);
            }

            ctx.Check(boards == 1 && minStep > 0.1f,
                $"the aircraft flies on through the whole {InstantActionRuntime.WrapupHoldS:0.#} s hold, every frame moving until the wrap-up is due: frames={frames} slowest step={minStep:0.00} m last={lastStep:0.00} m boards={boards}");
            ctx.Check(warped && camera.Count == shotsAtEnd && stings == stingsAtEnd && requests == requestsAtEnd
                    && camera.InMarkerOrder().All(s => s.DzName != late.DzName),
                $"…and {late.DzName}, first entered inside the hold, is not photographed: shots+={camera.Count - shotsAtEnd} stings+={stings - stingsAtEnd} requests+={requests - requestsAtEnd}");
            ctx.Note($"the Instant Action pilot flew {atEnd.DistanceTo(seat.WorldPosition):0} m between the run's end and the wrap-up");

            // The control: the same seat with the solo scoreboard holds its finish pose.
            run.RunCompleted -= ZonesFlown;
            string storePath = Path.Combine(ctx.ScratchDir, "ia-end-stunt-hold-scores.json");
            var scoreboard = StuntScoreboard.Build(run, "Test Plane", "C1", ScoreStore.Load(storePath),
                "ia-end/stunt-hold/player_test", exitsToMenu: true, new PauseState(), _ => new MenuInput());
            ctx.Host.AddChild(scoreboard);
            try
            {
                seat.Scoreboard = scoreboard;
                seat.Rerun();
                seat.WarpTo(Above(late), 0f, 80f);
                seat.DebugCompleteStunt = true;
                seat.SimStep(Dt);
                seat.DebugCompleteStunt = false;
                var posed = seat.WorldPosition;
                for (int i = 0; i < 30; i++)
                {
                    seat.SimStep(Dt);
                }
                ctx.Check(run.AllComplete && posed.DistanceTo(seat.WorldPosition) < 1e-3f,
                    $"…while the solo scoreboard's run holds its finish pose, the able-to-fail control: moved={posed.DistanceTo(seat.WorldPosition):0.000} m");
            }
            finally
            {
                seat.Scoreboard = null;
                scoreboard.Free();
            }
        }
        finally
        {
            run.RunCompleted -= ZonesFlown;
            seat.Stunt = null;
            seat.StuntShots = null;
            seat.ControlHold = FlightControlHold.None;
        }
    }

    // A two-pilot C1/IA1 stunt run built through the session's own roster and race board. It flies
    // once as Instant Action wires it and once as a plain splitscreen race, the control.
    // In Instant Action the last pilot's finish wins the mission. Both aircraft fly through the
    // whole hold with no board and no halt, and the wrap-up follows. The race keeps its board and
    // its finish hold.
    private static void SplitscreenStuntRunEndsOnTheHold(TestContext ctx, GameZ planesGamez,
        TextureArchive textures, ProjectilePool pool, string missionZrdr)
    {
        var ia = FlySplitscreenStuntRun(ctx, planesGamez, textures, pool, missionZrdr, instantAction: true);
        var race = FlySplitscreenStuntRun(ctx, planesGamez, textures, pool, missionZrdr, instantAction: false);
        if (ia is not { } a || race is not { } r)
        {
            ctx.Check(false, $"both two-pilot C1/IA1 stunt runs were built and flown");
            return;
        }

        ctx.Check(a.FirstFlewOn && r.FirstFlewOn,
            $"the first pilot in flies on while the other still flies, in both: ia={a.FirstFlewOn} race={r.FirstFlewOn}");
        ctx.Check(!a.BoardBuilt && a.Ranked == 2 && a.Won,
            $"Instant Action: the last pilot in wins the mission, both placings are kept for the run HUD, and no race board exists: board={a.BoardBuilt} ranked={a.Ranked} won={a.Won}");
        ctx.Check(!a.EverHalted && a.Wrapups == 1 && a.MinStep > 0.1f,
            $"…both aircraft fly on through the whole {InstantActionRuntime.WrapupHoldS:0.#} s hold with no halt, and the wrap-up follows: frames={a.HoldFrames} slowest step={a.MinStep:0.00} m halted={a.EverHalted} wrapups={a.Wrapups}");
        ctx.Check(a.LiveShot && a.HoldShots == 0,
            $"…a marker crossed while P2's run is live is photographed, and neither pilot photographs a marker first entered inside the hold: live={a.LiveShot} hold shots={a.HoldShots}");
        ctx.Check(r.BoardBuilt && r.EverHalted && r.Wrapups == 0 && r.MaxHeldStep < 1e-3f,
            $"the control, a plain splitscreen race: its board wakes on the last finish and halts the clock, and each seat holds its finish pose: board={r.BoardBuilt} halted={r.EverHalted} wrapups={r.Wrapups} moved={r.MaxHeldStep:0.000} m");
    }

    private static SplitStuntRun? FlySplitscreenStuntRun(TestContext ctx, GameZ planesGamez,
        TextureArchive textures, ProjectilePool pool, string missionZrdr, bool instantAction)
    {
        const float Dt = 1f / 60f;
        var zones = StuntMission.Load(GameZ.Load(SessionPaths.ChapterGamez(ctx.DataRoot, "C1")), missionZrdr,
            Messages.Load(ctx.MessagesPath));
        if (zones is not { TotalCount: >= 3 })
        {
            return null;
        }

        var pane = new SubViewport();
        ctx.Host.AddChild(pane);
        var rigs = new[]
        {
            new PlayerRig { Index = 0, Camera = ctx.Camera, HudParent = pane, Viewport = pane },
            new PlayerRig { Index = 1, Camera = ctx.Camera, HudParent = pane, Viewport = pane },
        };
        var stuntRace = new StuntRace();
        var pauseState = new PauseState();
        var spec = SessionSpec.Parse(new[] { "--hold=0,0,0,1" });
        var roster = new FlightRoster(FlightRosterPolicy.From(spec),
            new LiveryResolver(spec, Path.Combine(ctx.DataRoot, "extracted", "rof")),
            new WorldEffectsFactory(spec, ctx.Host, () => Vector3.Zero), ctx.Host,
            SuiteConstants.AircraftResources(ctx, planesGamez, textures,
                Messages.Load(ctx.MessagesPath), _ => new CamParams()),
            new FlightWorldBindings
            {
                Projectiles = pool,
                Gamez = planesGamez,
                ChapterZrdrPath = SessionPaths.ChapterZrdr(ctx.DataRoot, "C1"),
            },
            new HumanRosterBindings
            {
                RigCount = rigs.Length,
                Rigs = rigs,
                PauseState = pauseState,
                MenuInputFor = _ => new MenuInput(),
                ExitSession = () => { },
                StuntZones = zones,
                Race = stuntRace,
                InstantActionActive = instantAction,
            }, new SplitStuntStarts());
        var board = GameSession.RaceBoardFor(stuntRace, instantAction, "C1   ·   test", exitsToMenu: true,
            pauseState, _ => new MenuInput());
        if (board != null)
        {
            ctx.Host.AddChild(board);
        }

        try
        {
            roster.BuildPlayers(rigs);
            if (rigs[0].Controller is not { Stunt: { } run1 } p1 || rigs[1].Controller is not { Stunt: { } run2 } p2)
            {
                return null;
            }

            var seats = new[] { p1, p2 };
            // The pane accepts and never lands, so a latch is counted and nothing is written.
            var cam1 = new StuntCapture(run1, "C1", _ => true);
            var cam2 = new StuntCapture(run2, "C1", _ => true);
            p1.StuntShots = cam1;
            p2.StuntShots = cam2;

            // The director's wiring: zone sets flown on either run's completion, the win's hold
            // leaving the stick live, the wrap-up releasing it.
            var mission = new InstantActionRuntime(EndDef(ctx, instantAction ? "split-stunt-ia" : "split-stunt-race", "stunt_flying"));
            mission.RegisterPilot(0);
            mission.RegisterPilot(1);
            void CheckZoneSets()
            {
                if (InstantActionRuntime.ZoneSetsFlown(new[] { (false, run1.AllComplete), (false, run2.AllComplete) }))
                {
                    mission.ReportObjective(InstantActionObjective.ZonesFlown);
                }
            }

            run1.RunCompleted += CheckZoneSets;
            run2.RunCompleted += CheckZoneSets;
            int wrapups = 0;
            mission.MissionEnded += outcome =>
            {
                foreach (var seat in seats)
                {
                    seat.ControlHold = outcome == InstantActionOutcome.Won ? FlightControlHold.CommandsOnly : FlightControlHold.All;
                }
            };
            mission.WrapupDue += _ =>
            {
                foreach (var seat in seats)
                {
                    seat.ControlHold = FlightControlHold.None;
                }
                wrapups++;
            };

            // One sim frame, as the session's clock steps it: a halted clock steps nothing.
            bool everHalted = false;
            bool Frame()
            {
                everHalted |= pauseState.Halted;
                if (pauseState.Halted)
                {
                    return false;
                }
                p1.SimStep(Dt);
                p2.SimStep(Dt);
                mission.Advance(Dt);
                return true;
            }

            // P1 finishes first; a race seat's forced finish is staggered by index, so P2 is
            // completed later by the same flag.
            p1.DebugCompleteStunt = true;
            for (int i = 0; i < 10 && !run1.AllComplete; i++)
            {
                Frame();
            }
            p1.DebugCompleteStunt = false;
            var firstAt = p1.WorldPosition;
            for (int i = 0; i < 10; i++)
            {
                Frame();
            }
            float firstFlew = firstAt.DistanceTo(p1.WorldPosition);
            bool firstFlewOn = run1.AllComplete && !run2.AllComplete && firstFlew > 1f;

            // The camera's able-to-fail control: P2 crosses a marker while its run is live.
            var live = run2.Zones[0];
            p2.WarpTo(live.Position + new Vector3(0f, 500f, 0f), 0f, 80f);
            Frame();
            p2.WarpTo(live.Position, 0f, 80f);
            Frame();
            bool liveShot = cam2.Count == 1;

            p2.DebugCompleteStunt = true;
            for (int i = 0; i < 240 && !run2.AllComplete; i++)
            {
                Frame();
            }
            p2.DebugCompleteStunt = false;

            // The hold: every frame until the wrap-up is due, each pilot entering a marker it
            // never photographed halfway through.
            int shotsAtEnd = cam1.Count + cam2.Count;
            int frames = 0;
            float minStep = float.MaxValue;
            bool warped = false;
            while (wrapups == 0 && frames < (int)((InstantActionRuntime.WrapupHoldS + 1f) / Dt))
            {
                if (!warped && frames * Dt >= InstantActionRuntime.WrapupHoldS / 2f)
                {
                    p1.WarpTo(run1.Zones[1].Position, 0f, 80f);
                    p2.WarpTo(run2.Zones[2].Position, 0f, 80f);
                    warped = true;
                }
                var before1 = p1.WorldPosition;
                var before2 = p2.WorldPosition;
                if (!Frame())
                {
                    break;
                }
                frames++;
                minStep = Mathf.Min(minStep, Mathf.Min(before1.DistanceTo(p1.WorldPosition), before2.DistanceTo(p2.WorldPosition)));
            }
            int holdShots = cam1.Count + cam2.Count - shotsAtEnd;

            // The race seat's own finish hold, stepped past the board's halt: a seat under the
            // race's rules stays where it finished.
            float maxHeld = 0f;
            foreach (var seat in seats)
            {
                var posed = seat.WorldPosition;
                seat.SimStep(Dt);
                maxHeld = Mathf.Max(maxHeld, posed.DistanceTo(seat.WorldPosition));
            }

            int ranked = stuntRace.Racers.Count(racer => racer.Finished);
            ctx.Note($"{(instantAction ? "Instant Action" : "race")}: P1 flew {firstFlew:0} m in the 10 frames after its finish while P2 flew, hold frames={frames}");
            return new SplitStuntRun(board != null, firstFlewOn, ranked, mission.Outcome == InstantActionOutcome.Won,
                everHalted, wrapups, frames, frames > 0 ? minStep : 0f, maxHeld, liveShot, holdShots);
        }
        finally
        {
            board?.Free();
            var built = rigs.Select(rig => rig.Controller).Where(c => c != null).ToArray();
            roster.ClearMembership();
            foreach (var controller in built)
            {
                controller!.Free();
            }
            pane.Free();
        }
    }

    // What one two-pilot flight reports.
    private readonly record struct SplitStuntRun(bool BoardBuilt, bool FirstFlewOn, int Ranked, bool Won,
        bool EverHalted, int Wrapups, int HoldFrames, float MinStep, float MaxHeldStep, bool LiveShot,
        int HoldShots);

    // Two abreast starts well above C1's terrain, clear of every zone.
    private sealed class SplitStuntStarts : IFlightStarts
    {
        public IReadOnlyList<FlightStart> ChooseStarts(IReadOnlyList<SpawnPoint>? spawns,
            string missionZrdrPath, int spawnBase, int playerCount)
        {
            var starts = new FlightStart[playerCount];
            for (int i = 0; i < playerCount; i++)
            {
                var position = new Vector3(i * 200f, 3000f, 0f);
                starts[i] = new FlightStart(position, position + Vector3.Forward, 1f, 90f);
            }

            return starts;
        }
    }
}

/// <summary>A minimal results board for the shell-contract suite: a settable live flag and the
/// standard menu, no content of its own.</summary>
internal sealed partial class ShellProbeBoard : ResultsBoard
{
    /// <summary>The live flag under the suite's control, standing in for the match's
    /// <c>Completed</c> or the race's <c>AllFinished</c>.</summary>
    public bool StillOver;

    protected override bool StillEnded => StillOver;

    public static ShellProbeBoard Build(PauseState state, System.Func<int, MenuInput> inputFor)
    {
        var board = new ShellProbeBoard();
        board.InitShell(state, exitsToMenu: true, inputFor);
        return board;
    }

    /// <summary>What a subclass's completion handler does: populate (here just the menu), show,
    /// halt.</summary>
    public void WakeWithMenu()
    {
        StillOver = true;
        AddStandardMenu(BeginPanel(1f), 1f);
        Wake();
    }
}
