using System.Collections.Generic;
using System.Text;
using CSVM.Mech3;
using CSVM.Session.Campaign;
using CSVM.Session.Objectives;
using CSVM.Tooling;
using CSVM.UI.Overlays;
using Godot;

namespace CSVM.Testing;

/// <summary>Suites over D33's in-flight objectives display and its objective sound cues, against a
/// BUILT campaign world so both the HUD element and the audio path it reads are the real engine
/// classes, not a scripted stand-in.</summary>
internal static class CampaignHudSuites
{
    // How a display objective can be forced to complete with nobody at the controls. The mission
    // chooses, not the suite: C1/M02's rows hang off INACTIVEn node lists, C3/M01 authors not one
    // INACTIVEn in the whole file and completes its secondaries off a danger zone and off an
    // objective carrying no condition at all. ANIM_STATE, DEDG and TRAVELERS need a flown player
    // or a running anim program, so they leave no route open here.
    private enum CompletionRoute
    {
        None,
        Inactive,
        DangerZones,
        Unconditional,
    }

    /// <summary>Drives one shipped mission's real director: <see cref="ObjectivesHud"/> tracks the
    /// graph's rows, and a wake/complete sound-group cue starts a real
    /// <see cref="AudioStreamPlayer3D"/> (<see cref="WorldSounds.OneShotsStarted"/>). A group name
    /// is not guaranteed to resolve here, so both halves try every candidate.</summary>
    [Suite("campaign-objectives-hud",
        "D33's in-flight objectives display and cue firing against a BUILT campaign world: " +
        "ObjectivesHud carries one line per ObjectiveGraph display row, a scripted " +
        "IDENTITY objective completing off whichever condition the chapter's own mission " +
        "authors (an INACTIVEn node list, a danger zone, or no condition at all) marks its " +
        "own readout line " +
        "(not only the graph's) with the original's own mark art centred over that row's " +
        "leading characters and its text left in the colour an open row's carries, a " +
        "WAKEUP_SOUND_GROUP the mission authors starts a real " +
        "AudioStreamPlayer3D through WorldSounds (D31's existing routing, counted rather than " +
        "duplicated), and whichever cue surface the mission chose, WAKEUP_SOUND_GROUP or " +
        "COMPLETED_SOUND_GROUP, one of its groups reaches a real player (BL-483)")]
    internal static void CampaignObjectivesHud(TestContext ctx)
    {
        var missions = CampaignSequence.Load(ctx.ZrdrPath);
        if (FirstMissionOf(missions, ctx.Chapter) is not { } mission)
        {
            throw new SuiteSkippedException($"chapter {ctx.Chapter} holds no campaign mission");
        }

        var script = ObjectiveScript.Load(
            SessionPaths.MissionZrdr(ctx.DataRoot, mission.ChapterFolder, mission.MissionFolder));
        var messages = Messages.Load(ctx.MessagesPath);
        var profile = CampaignProfileDef.NewProfile("Zachary");
        var director = CampaignDirector.Create(script, mission, profile, null);
        var report = new StringBuilder();
        report.AppendLine($"{mission.ChapterFolder}/{mission.MissionFolder} seq={mission.Seq}");

        // The wake/complete sound-group names are objectives.zrd's own vocabulary, never touched
        // by the mission's anim program, so nothing else prewarms them before the build's sound
        // archive scope closes (docs/architecture.md's ObjectiveScript entry).
        ctx.ExtraPrewarmSoundNames = script.SoundGroupNames();
        ctx.WithWorld(ctx.Chapter, collision: false, mission.MissionFolder, world =>
        {
            var sounds = world.Runtime.Sounds;
            ctx.Check(sounds != null, $"the mission world built a live WorldSounds channel");
            director.Attach(new CampaignDirector.WorldInputs
            {
                Runtime = world.Runtime,
                Sounds = sounds,
                ListenerPosition = () => ctx.Camera.GlobalPosition,
                Rng = new System.Random(1),
            });
            var graph = director.Graph!;
            // Paused from the start: the readout is a pause-screen element (B11), so its drawing
            // path only runs while the board is up, and this suite wants that path exercised.
            var pause = new CSVM.Flight.Modes.PauseState();
            pause.TryToggle(0);
            var mark = ObjectivesHud.LoadMark(
                System.IO.Path.Combine(ctx.DataRoot, "extracted", "rimage"));
            ctx.Check(mark != null,
                $"the completion mark's own art is extracted ({ObjectivesHud.MarkFile})");
            var hud = ObjectivesHud.Build(director, messages, pause, mark);
            ctx.Host.AddChild(hud);
            // A second rig's own instance over the same director proves that "one per rig"
            // is N polling instances rather than one broadcasting to many panes.
            var hud2 = ObjectivesHud.Build(director, messages, pause, mark);
            ctx.Host.AddChild(hud2);

            // ObjectivesHud polls for the graph in _Process rather than at construction (the
            // wiring contract: a session adds it before Attach runs), so drive one process frame
            // before reading it, matching what a real running session would do.
            hud._Process(0.0);
            hud2._Process(0.0);
            ctx.Same(graph.Rows.Count, hud.BuildLines().Count,
                $"the readout carries one line per display row before anything happens");
            ctx.Same(hud.BuildLines().Count, hud2.BuildLines().Count,
                $"a second rig's own readout carries the same row count as the first");
            CheckNoBlankRowsDrawn(ctx, hud, report);

            if (sounds != null)
            {
                bool wokeACue = DriveWakeCue(ctx, script, graph, sounds, report);
                bool completedACue = DriveCompletionCue(ctx, world, script, graph, hud, sounds, report);
                CheckCueReachedAPlayer(ctx, script, wokeACue, completedACue, report);
            }

            hud2._Process(0.0);
            ctx.Same(hud.BuildLines().Count, hud2.BuildLines().Count,
                $"a second rig's own readout still tracks the same row count once the graph advanced");
            bool sameCompletions = true;
            var linesA = hud.BuildLines();
            var linesB = hud2.BuildLines();
            for (int i = 0; i < System.Math.Min(linesA.Count, linesB.Count); i++)
            {
                sameCompletions &= linesA[i].Completed == linesB[i].Completed;
            }
            ctx.Check(sameCompletions,
                $"a completion reaches every rig's own readout, not only the first one built");

            hud.QueueFree();
            hud2.QueueFree();
        });

        ctx.WriteArtifact($"test-campaign-objectives-hud-{ctx.Chapter}.txt", report.ToString());
        ctx.Note($"drove {mission.ChapterFolder}/{mission.MissionFolder}'s readout and cue path against a built world");
    }

    /// <summary>The mission radio queue over one shipped mission's own callout vocabulary: the
    /// definition classes the data authors, a VO dialogue chain speaking all its lines in order,
    /// a plain radio line beside it, and the queue's spacing, wait tolerance and cancellation.
    /// Built against a real world so the streams are the ones the mission prewarmed.</summary>
    // BL-465/BL-461: mission callouts played from a point in the world, and a cue naming a VO
    // dialogue chain played nothing at all.
    [Suite("mission-radio",
        "the mission radio queue over the first story mission's own callout vocabulary: every "
        + "wake/complete cue the mission authors is a queued radio line or a chain of them and "
        + "none is a positional definition, a chain speaks all of its lines in order, a second "
        + "cue queues behind the one speaking instead of cutting in, the whole queue drains "
        + "without starting a positional player, STOP_QUEUED_SOUNDS drops a call that has "
        + "not begun, and a line queued for a named speaker reports that one speaker as talking "
        + "for its length and nobody else")]
    internal static void MissionRadioCallouts(TestContext ctx)
    {
        var missions = CampaignSequence.Load(ctx.ZrdrPath);
        if (FirstMissionOf(missions, ctx.Chapter) is not { } mission)
        {
            throw new SuiteSkippedException($"chapter {ctx.Chapter} holds no campaign mission");
        }

        var script = ObjectiveScript.Load(
            SessionPaths.MissionZrdr(ctx.DataRoot, mission.ChapterFolder, mission.MissionFolder));
        var defs = SoundDefs.Load(ctx.ZrdrPath);
        var groups = SoundDefs.LoadGroups(ctx.ZrdrPath);
        var report = new StringBuilder();
        report.AppendLine($"{mission.ChapterFolder}/{mission.MissionFolder} seq={mission.Seq}");
        CheckCueClasses(ctx, script, defs, groups, report);

        var cues = script.SoundGroupNames();
        string? chainCue = FirstCue(cues, groups, defs, chain: true);
        string? lineCue = FirstCue(cues, groups, defs, chain: false);
        if (chainCue == null || lineCue == null)
        {
            throw new SuiteSkippedException(
                $"{mission.MissionFolder} authors no chain/line pair to A/B (chain={chainCue} line={lineCue})");
        }

        ctx.ExtraPrewarmSoundNames = cues;
        ctx.WithWorld(ctx.Chapter, collision: false, mission.MissionFolder, world =>
        {
            var sounds = world.Runtime.Sounds;
            ctx.Check(sounds != null, $"the mission world built a live WorldSounds channel");
            if (sounds == null)
            {
                return;
            }

            var radio = new MissionRadio(defs, groups, sounds.StreamFor);
            ctx.Host.AddChild(radio);
            DriveRadio(ctx, radio, sounds, chainCue, lineCue, report);
            radio.QueueFree();
        });

        ctx.WriteArtifact($"test-mission-radio-{ctx.Chapter}.txt", report.ToString());
        ctx.Note($"drove {mission.ChapterFolder}/{mission.MissionFolder}'s chain cue '{chainCue}' and line cue '{lineCue}'");
    }

    // The decode this channel exists for: every callout the mission cues is a queued radio line or
    // a chain of them, and not one of them is a positional definition.
    private static void CheckCueClasses(
        TestContext ctx, ObjectiveScript script, IReadOnlyDictionary<string, SoundDef> defs,
        IReadOnlyDictionary<string, SoundGroup> groups, StringBuilder report)
    {
        int queued = 0, chains = 0, music = 0, positional = 0, unknown = 0;
        foreach (var cue in script.SoundGroupNames())
        {
            if (cue.StartsWith("mu", System.StringComparison.OrdinalIgnoreCase))
            {
                music++;
            }
            else if (groups.TryGetValue(cue, out var group))
            {
                chains += group.Chains.Count > 0 ? 1 : 0;
                positional += ChainOrGroupIs3D(group, defs) ? 1 : 0;
            }
            else if (defs.TryGetValue(cue, out var def))
            {
                queued += def.Queued ? 1 : 0;
                positional += def.Is3D ? 1 : 0;
            }
            else
            {
                unknown++;
            }
        }

        report.AppendLine($"cues: {queued} radio lines, {chains} chains, {music} music, "
            + $"{positional} positional, {unknown} unknown to sounds.json");
        ctx.Same(0, positional, $"no callout this mission cues names a positional (3D) definition");
        ctx.Check(queued + chains > 0, $"the mission cues radio lines and chains ({queued}+{chains})");
    }

    // Drives the channel: the chain speaks every line in order, a second cue queues behind rather
    // than cutting in, and a cancelled call never speaks.
    private static void DriveRadio(
        TestContext ctx, MissionRadio radio, WorldSounds sounds,
        string chainCue, string lineCue, StringBuilder report)
    {
        int expected = radio.Cue(chainCue, new System.Random(1));
        ctx.Check(expected > 1, $"the chain cue '{chainCue}' queues its whole script lines={expected}");
        ctx.Same(0, radio.LinesStarted, $"nothing speaks before the channel is stepped");
        Pump(radio, MissionRadio.CueDelaySeconds + 0.2f);
        ctx.Same(1, radio.LinesStarted, $"the chain's first line started on air='{radio.OnAir}'");

        int queuedBefore = sounds.OneShotsStarted;
        ctx.Same(1, radio.Cue(lineCue, new System.Random(2)),
            $"a plain radio line '{lineCue}' queues as one line");
        string? speaking = radio.OnAir;
        Pump(radio, 0.5f);
        ctx.Check(speaking != null && radio.OnAir == speaking,
            $"the second call waits its turn rather than cutting in on '{speaking}'");
        ctx.Same(1, radio.Pending, $"it is holding in the queue");

        Pump(radio, 240f);
        ctx.Same(expected + 1, radio.LinesStarted,
            $"every line of the chain and the line behind it spoke got={radio.LinesStarted}");
        ctx.Same(0, radio.Dropped, $"nothing waited past its QUEUE tolerance");
        ctx.Check(radio.OnAir == null, $"the channel falls silent once the queue drains");
        ctx.Same(queuedBefore, sounds.OneShotsStarted,
            $"not one callout started a positional player while the radio was speaking");

        radio.Cue(chainCue, new System.Random(3));
        ctx.Same(1, radio.Cancel(new[] { chainCue }), $"STOP_QUEUED_SOUNDS drops a call that has not started");
        int spoken = radio.LinesStarted;
        Pump(radio, 60f);
        ctx.Same(spoken, radio.LinesStarted, $"the cancelled call never speaks");

        // What the combat-voice gate's "already talking" test reads: a line carries the pilot it
        // belongs to, and that pilot holds the channel only for the length of its own line.
        int pilot = 7;
        ctx.Check(radio.Speak(lineCue, new System.Random(4), pilot) != null,
            $"a combat line queues for a named speaker cue='{lineCue}'");
        radio.Tick(0.1f);
        ctx.Check(radio.IsSpeaking(pilot), $"…and that speaker reads as talking while it is on air");
        ctx.Check(!radio.IsSpeaking(pilot + 1),
            $"…while another pilot sharing the busy channel does not");
        Pump(radio, 240f);
        ctx.Check(!radio.IsSpeaking(pilot), $"…and is free again once the line's length has elapsed");
        report.AppendLine($"radio: {radio.LinesStarted} lines started, {radio.Dropped} dropped, "
            + $"{sounds.OneShotsStarted - queuedBefore} positional one-shots");
    }

    private static void Pump(MissionRadio radio, float seconds)
    {
        for (float t = 0f; t < seconds; t += 0.1f)
        {
            radio.Tick(0.1f);
        }
    }

    private static bool ChainOrGroupIs3D(
        SoundGroup group, IReadOnlyDictionary<string, SoundDef> defs)
    {
        foreach (var chain in group.Chains)
        {
            foreach (var line in chain)
            {
                if (defs.TryGetValue(line, out var def) && def.Is3D)
                {
                    return true;
                }
            }
        }

        foreach (var (member, _) in group.Members)
        {
            if (defs.TryGetValue(member, out var def) && def.Is3D)
            {
                return true;
            }
        }

        return false;
    }

    // The first cue name of the asked-for shape: a VO dialogue chain, or a bare radio definition.
    private static string? FirstCue(
        IReadOnlyList<string> cues, IReadOnlyDictionary<string, SoundGroup> groups,
        IReadOnlyDictionary<string, SoundDef> defs, bool chain)
    {
        foreach (var cue in cues)
        {
            if (chain && groups.TryGetValue(cue, out var group) && group.Chains.Count > 0
                && group.Chains[0].Count > 1)
            {
                return cue;
            }

            if (!chain && !groups.ContainsKey(cue) && defs.TryGetValue(cue, out var def) && def.Queued)
            {
                return cue;
            }
        }

        return null;
    }

    // A row the mission gives no message key resolves to no text, and drawn anyway it is a bare
    // mark against blank space, which is what stopped a player reading WHICH objective had
    // completed (B11). The readout drops those rows, so nothing it draws is ever blank.
    private static void CheckNoBlankRowsDrawn(TestContext ctx, ObjectivesHud hud, StringBuilder report)
    {
        var drawn = hud.DrawnLines();
        bool blank = false;
        foreach (var line in drawn)
        {
            blank |= string.IsNullOrWhiteSpace(line.Text);
        }

        report.AppendLine($"{hud.BuildLines().Count} display rows, {drawn.Count} of them drawn");
        ctx.Check(!blank, $"no drawn objectives line is a mark against blank text");
        ctx.Check(drawn.Count > 0, $"the readout draws at least one keyed objectives line");
    }

    // Where the completion mark lands, which is the half of the readout no completion bit can show.
    // The original draws its mark over the row's own leading characters and leaves the line's text
    // exactly where and how an open line's is, so a marked row here must carry a mark box centred
    // on its origin and a text colour no different from the rest of the board's.
    private static void CheckMarkPlacement(TestContext ctx, ObjectivesHud hud, StringBuilder report)
    {
        var rows = hud.DrawnRows();
        ctx.Same(hud.DrawnLines().Count, rows.Count, $"every drawn line stands as a row on the board");
        int marked = 0, centred = 0, recoloured = 0, wrong = 0;
        Color? first = null;
        foreach (var row in rows)
        {
            first ??= row.TextColor;
            recoloured += row.TextColor == first ? 0 : 1;
            wrong += row.Marked == row.Line.Completed ? 0 : 1;
            if (!row.Marked)
            {
                continue;
            }

            marked++;
            var origin = row.MarkBox.Position + (row.MarkBox.Size * 0.5f);
            centred += row.MarkBox.Size.X > 0f && origin.Length() < 1f ? 1 : 0;
            report.AppendLine($"row '{Head(row.Line.Text)}' marked: box {row.MarkBox}");
        }

        ctx.Same(0, wrong, $"the mark is over a row when that row is completed and over no other");
        ctx.Same(0, recoloured, $"a completed row's text is drawn in the same colour as an open one's");
        ctx.Same(marked, centred,
            $"each mark is centred on its row's own origin, over its leading characters ({marked} marked)");
    }

    private static string Head(string text) => text.Length <= 24 ? text : text.Substring(0, 24);

    // Wakes every WAKEUP_SOUND_GROUP-authoring objective in turn until one of them actually starts
    // a real one-shot player, and asserts that at least one did. Returns whether one did.
    // ⚠ Assert nothing when the mission authors no such directive; that is the mission's choice of
    // cue surface, not a defect, and CheckCueReachedAPlayer is what covers it.
    private static bool DriveWakeCue(
        TestContext ctx, ObjectiveScript script, ObjectiveGraph graph, WorldSounds sounds, StringBuilder report)
    {
        int authored = 0;
        foreach (var def in script.Objectives)
        {
            if (def.WakeSoundGroup is not { } group)
            {
                continue;
            }

            authored++;
            int before = sounds.OneShotsStarted;
            graph.Wake(def.Number);
            int after = sounds.OneShotsStarted;
            if (after > before)
            {
                report.AppendLine($"OBJECTIVE{def.Number} WAKEUP_SOUND_GROUP '{group}' started a one-shot ({before} -> {after})");
                ctx.Check(true, $"a WAKEUP_SOUND_GROUP cue started a real one-shot player (OBJECTIVE{def.Number} '{group}')");
                return true;
            }

            report.AppendLine($"OBJECTIVE{def.Number} WAKEUP_SOUND_GROUP '{group}' started no one-shot");
        }

        if (authored > 0)
        {
            ctx.Check(false,
                $"at least one WAKEUP_SOUND_GROUP this mission authors started a real one-shot player (drove {authored})");
            return false;
        }

        report.AppendLine("this mission's objectives author no WAKEUP_SOUND_GROUP, so its objective "
            + "audio rides the COMPLETED_SOUND_GROUP surface alone");
        return false;
    }

    // Drives every IDENTITY objective this suite can force to completion with nobody at the
    // controls, whatever shape the mission authored (see CompletionRoute). Keeps the first that
    // marks its row (the row-marking proof) and separately the first whose COMPLETED_SOUND_GROUP
    // also starts a one-shot: a completing objective's group can be a VO dialogue chain
    // (docs/formats/sounds.md), which nothing here plays yet (a named gap, not a suite defect).
    private static bool DriveCompletionCue(
        TestContext ctx, TestWorld world, ObjectiveScript script, ObjectiveGraph graph,
        ObjectivesHud hud, WorldSounds sounds, StringBuilder report)
    {
        int? rowObjective = null;
        int? soundObjective = null;
        int driven = 0;
        var unreachable = new List<string>();
        foreach (var def in script.Objectives)
        {
            if (def.Identity is not { } identity)
            {
                continue;
            }

            var route = RouteFor(def);
            if (route == CompletionRoute.None)
            {
                unreachable.Add($"OBJECTIVE{def.Number} [{ConditionShape(def)}]");
                continue;
            }

            driven++;
            var beforeLines = hud.BuildLines();
            int before = sounds.OneShotsStarted;
            ForceRoute(world, graph, def, route);
            Advance(graph, 3f);
            hud._Process(0.0);
            if (!graph.CompletedOf(def.Number))
            {
                report.AppendLine($"OBJECTIVE{def.Number} drove its {route} route " +
                    $"[{RouteNames(def, route)}] and did not complete");
                continue;
            }

            bool marked = RowNewlyMarked(beforeLines, hud.BuildLines(), identity.Priority);
            int after = sounds.OneShotsStarted;
            report.AppendLine($"OBJECTIVE{def.Number} completed off its {route} route " +
                $"[{RouteNames(def, route)}], priority={identity.Priority}, " +
                $"COMPLETED_SOUND_GROUP='{def.CompletedSoundGroup}', one-shots {before} -> {after}, " +
                $"row marked={marked}");
            if (rowObjective == null && marked)
            {
                rowObjective = def.Number;
            }

            if (soundObjective == null && def.CompletedSoundGroup != null && after > before)
            {
                soundObjective = def.Number;
            }
        }

        CheckRowMarking(ctx, report, rowObjective, driven, unreachable);
        CheckNoBlankRowsDrawn(ctx, hud, report);
        CheckMarkPlacement(ctx, hud, report);
        if (soundObjective != null)
        {
            ctx.Check(true, $"a COMPLETED_SOUND_GROUP cue started a real one-shot player (OBJECTIVE{soundObjective})");
            return true;
        }

        report.AppendLine(
            "no completing objective's COMPLETED_SOUND_GROUP started a one-shot here: every one " +
            "authored in this mission is a VO dialogue chain, which no player exists for yet " +
            "(docs/formats/sounds.md)");
        return false;
    }

    // The cue assertion that holds on every chapter, whichever directive the mission chose to carry
    // its objective audio: one of the sound groups it authors must reach a real player. C4/M01 and
    // C5/M01 author not one WAKEUP_SOUND_GROUP, so the wake half alone leaves them unasserted.
    private static void CheckCueReachedAPlayer(
        TestContext ctx, ObjectiveScript script, bool wokeACue, bool completedACue, StringBuilder report)
    {
        int wake = 0, completed = 0;
        foreach (var def in script.Objectives)
        {
            wake += def.WakeSoundGroup != null ? 1 : 0;
            completed += def.CompletedSoundGroup != null ? 1 : 0;
        }

        report.AppendLine($"cue surfaces: {wake} WAKEUP_SOUND_GROUP (fired={wokeACue}), "
            + $"{completed} COMPLETED_SOUND_GROUP (fired={completedACue})");
        if (wake + completed == 0)
        {
            const string Gap = "The objective cue path is unproven on this chapter, which is a gap "
                + "in coverage and not a pass";
            report.AppendLine("SKIPPED the cue check: this mission authors no objective sound group at all");
            ctx.Note($"SKIPPED the cue check: this mission authors no objective sound group at all. {Gap}");
            return;
        }

        ctx.Check(wokeACue || completedACue,
            $"an objective sound group this mission authors started a real one-shot player (wake {wake}, completed {completed})");
    }

    // The one assertion this half exists for. It is only allowed not to run when the mission
    // authors no display objective this suite can force at all, and DIAG-15 makes that case print
    // every row it could not reach and the condition family that put it out of range.
    private static void CheckRowMarking(
        TestContext ctx, StringBuilder report, int? rowObjective, int driven,
        IReadOnlyList<string> unreachable)
    {
        if (driven > 0)
        {
            string drove = $"drove {driven}, marked OBJECTIVE{rowObjective}";
            ctx.Check(rowObjective != null,
                $"a scripted completion marks its row in the readout, not only in the graph ({drove})");
            return;
        }

        string rows = string.Join("; ", unreachable);
        string why = $"this mission's {unreachable.Count} display objectives all complete off a "
            + $"condition no scripted run can force (each needs a flown player or a running anim "
            + $"program): {rows}";
        report.AppendLine($"SKIPPED the row-marking check: no forceable display objective ({rows})");
        const string Gap = "The readout's completion marking is unproven on this chapter, which is "
            + "a gap in coverage and not a pass";
        ctx.Note($"SKIPPED the row-marking check, {why}. {Gap}");
    }

    // Which forcing route this objective's own authored conditions leave open. INACTIVEn is tried
    // first so a mission that authors both keeps the route it was already proven on.
    private static CompletionRoute RouteFor(ObjectiveDef def)
    {
        if (def.Inactive.Count > 0)
        {
            return CompletionRoute.Inactive;
        }

        if (def.DangerZones.Count > 0)
        {
            return CompletionRoute.DangerZones;
        }

        return def.HasConditions ? CompletionRoute.None : CompletionRoute.Unconditional;
    }

    // Opens the tick gate, then forces the route. Order is the graph's, not a choice: TICK_DEPENDS_ON_OBJ
    // holds the completion scan off until its subject is AWAKE, and a wake clears the objective's
    // own danger-zone tally, so the zones can only be notified after it.
    private static void ForceRoute(
        TestWorld world, ObjectiveGraph graph, ObjectiveDef def, CompletionRoute route)
    {
        if (def.TickDependsOn > 0)
        {
            graph.Wake(def.TickDependsOn);
        }

        if (route == CompletionRoute.Inactive)
        {
            ProbeRunner.DriveInactive(world.Runtime, def);
        }

        graph.Wake(def.Number);
        if (route == CompletionRoute.DangerZones)
        {
            foreach (var zone in def.DangerZones)
            {
                graph.NotifyDangerZoneCompleted(zone);
            }
        }
    }

    // The completed objective's OWN row, found by its identity priority rather than by any row
    // going green: forcing a route wakes the objective's tick dependency too, and that neighbour
    // completing would otherwise read as this objective's line being marked.
    private static bool RowNewlyMarked(
        IReadOnlyList<ObjectivesHudLine> before, IReadOnlyList<ObjectivesHudLine> after, int priority)
    {
        bool marked = false;
        for (int i = 0; i < after.Count; i++)
        {
            if (after[i].Priority == priority && after[i].Completed)
            {
                marked |= i >= before.Count || !before[i].Completed;
            }
        }

        return marked;
    }

    // Every condition family the objective authors, for the skip line: this is what says WHY a row
    // could not be forced rather than leaving the reader to guess.
    private static string ConditionShape(ObjectiveDef def)
    {
        var parts = new List<string>();
        if (def.Inactive.Count > 0)
        {
            parts.Add($"INACTIVEn x{def.Inactive.Count}");
        }

        if (def.DangerZones.Count > 0)
        {
            parts.Add($"DANGER_ZONES x{def.DangerZones.Count}");
        }

        if (def.AnimStates.Count > 0)
        {
            parts.Add($"ANIM_STATE x{def.AnimStates.Count}");
        }

        if (def.Dedg != null)
        {
            parts.Add("DEDG");
        }

        if (def.Travelers != null)
        {
            parts.Add("TRAVELERS");
        }

        return parts.Count > 0 ? string.Join(" + ", parts) : "no condition";
    }

    // What the route actually reached for, for the report.
    private static string RouteNames(ObjectiveDef def, CompletionRoute route) => route switch
    {
        CompletionRoute.Inactive => InactiveNames(def),
        CompletionRoute.DangerZones => string.Join(", ", def.DangerZones),
        _ => "completes on its first eligible tick",
    };

    // The leaf of each INACTIVE path, for the report: these are the names a scripted run reaches
    // for (--destroy=) when it wants this objective to complete with nobody at the controls.
    private static string InactiveNames(ObjectiveDef def)
    {
        var names = new List<string>();
        foreach (var path in def.Inactive)
        {
            names.Add(path.Count > 0 ? path[path.Count - 1] : "?");
        }

        return string.Join(", ", names);
    }


    private static void Advance(ObjectiveGraph graph, float seconds)
    {
        for (float t = 0f; t < seconds; t += 0.1f)
        {
            graph.Step(0.1f);
        }
    }

    private static CampaignMission? FirstMissionOf(
        IReadOnlyList<CampaignMission> missions, string chapter)
    {
        CampaignMission? found = null;
        foreach (var m in missions)
        {
            if (m.ChapterFolder.Equals(chapter, System.StringComparison.OrdinalIgnoreCase)
                && (found == null || m.Seq < found.Value.Seq))
            {
                found = m;
            }
        }

        return found;
    }
}
