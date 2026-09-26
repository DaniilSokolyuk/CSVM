using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using CSVM.Mech3;
using CSVM.Session.Campaign;
using CSVM.UI.Boards;
using CSVM.UI.Campaign;
using Godot;

namespace CSVM.Testing;

/// <summary>
/// BL-490: how far the parchment's next objective sits below the last one. The reveal decides when
/// a line appears and the note widget decides where, and only the renderer's font knows how tall a
/// wrapped entry drew, so this drives every mission's reveal to its end and measures the composed
/// note with the same font the screen writes it in. A fixed pitch cannot be checked by eye on one
/// mission: the campaign's entries run from 20 to 98 characters in a 185-wide column.
/// </summary>
internal static class CampaignBriefingNoteSuites
{
    // The shell's own clock, in frame-sized slices: a script blocks on authored waits and measured
    // cue points, so a single large delta would leave it standing at the first of them.
    private const double StepDt = 1.0 / 60.0;
    private const int Frames = 12000;

    // The retired pitch, the fixed 30 px step every objective row used to be given. An entry that
    // draws taller than this minus the widget's own SPACING is one that used to be overdrawn.
    private const float RetiredPitch = 30f;

    private static readonly System.Globalization.CultureInfo Invariant =
        System.Globalization.CultureInfo.InvariantCulture;

    // BL-490: an objective wrapped to a second line drew over the next one's authored slot,
    // which only shows on a mission whose sentences are longer than the first's.
    [Suite("campaign-briefing-note",
        "how the briefing's objectives note flows (BL-490): all 24 missions' reveals driven to "
        + "their end, each composed note measured with the font the screen writes it in, and "
        + "no entry allowed to start above the bottom of the one before it or to run past the "
        + "parchment's authored 240 px box")]
    internal static void CampaignBriefingNote(TestContext ctx)
    {
        ctx.RequireData(ctx.ZrdrPath, $"zrdr archive");
        var probe = new Control();
        ctx.Host.AddChild(probe);
        try
        {
            Sweep(ctx, probe.GetThemeDefaultFont());
        }
        finally
        {
            ctx.Host.RemoveChild(probe);
            probe.QueueFree();
        }
    }

    // Every mission's note, at the 1:1 fit the authored space is composed in, so a measured height
    // is in authored pixels and comparable with the widget's own 240.
    private static void Sweep(TestContext ctx, Font? font)
    {
        if (font == null)
        {
            throw new SuiteSkippedException("the default theme carries no font to measure with");
        }

        string dir = Path.Combine(ctx.ScratchDir, "BriefingNote");
        var flow = new CampaignFlow(
            new CampaignProfileStore(dir), UiStrings.TryLoad(ctx.DataRoot) ?? UiStrings.Empty,
            ctx.DataRoot);
        flow.SelectProfile(CampaignProfileDef.NewProfile("Zachary"));
        var fit = BoardFit.For(BoardFit.AuthoredWidth, BoardFit.AuthoredHeight);
        var report = new StringBuilder();
        var tally = new Tally();
        for (int seq = 0; seq < CampaignSequence.MissionCount; seq++)
        {
            Measure(ctx, flow, seq, fit, font, report, tally);
        }

        Verdict(ctx, report, tally, dir);
    }

    private static void Measure(
        TestContext ctx, CampaignFlow flow, int seq, BoardFit fit, Font font,
        StringBuilder report, Tally tally)
    {
        flow.SetMission(seq);
        flow.GoTo(CampaignScreen.Briefing);
        if (flow.Page is not CampaignBriefingPage page)
        {
            return;
        }

        for (int frame = 0; frame < Frames && page.Reveal is { Complete: false }; frame++)
        {
            page.Advance(StepDt);
        }

        var board = CampaignBoards.For(page, flow.Row);
        tally.Missions++;
        tally.Rows += page.RowCount == 3 ? 0 : 1;
        if (board.Notes.Count == 0)
        {
            tally.Empty++;
            report.AppendLine($"seq {seq} {page.State?.Key}: no note");
            flow.GoTo(CampaignScreen.Cabin);
            return;
        }

        var note = board.Notes[0];
        var height = ComposedBoardView.Measure(fit, font, note);
        Flowed(note, height, seq, page, report, tally);
        flow.GoTo(CampaignScreen.Cabin);
    }

    // One mission's placed lines against the two things that can go wrong: an entry drawn over the
    // one below it, and a run that leaves the parchment's own box.
    private static void Flowed(
        BoardNote note, Func<string, float, float> height, int seq, CampaignBriefingPage page,
        StringBuilder report, Tally tally)
    {
        var lines = note.Flow(height);
        tally.Entries += note.Entries.Count;
        tally.Drawn += lines.Count;
        float bottom = note.Y;
        int overflowed = 0;
        for (int i = 0; i < lines.Count; i++)
        {
            float tall = height(lines[i].Text, note.Width);
            if (i > 0 && lines[i].Y < bottom)
            {
                tally.Overlaps++;
            }

            if (tall > RetiredPitch - note.Spacing)
            {
                overflowed++;
            }

            bottom = lines[i].Y + tall;
        }

        tally.Overflowed += overflowed;
        tally.Clipped += note.Entries.Count - lines.Count;
        tally.Past += bottom > note.Y + note.Height ? 1 : 0;
        report.AppendLine(
            $"seq {seq} {page.State?.Key}: {note.Entries.Count} entries, {lines.Count} drawn, " +
            $"bottom {(bottom - note.Y).ToString("0.0", Invariant)} of {note.Height}, " +
            $"{overflowed} past the retired {RetiredPitch} px pitch");
    }

    private static void Verdict(TestContext ctx, StringBuilder report, Tally tally, string dir)
    {
        ctx.WriteArtifact("campaign-briefing-note.txt", report.ToString());
        ctx.Note($"{tally.Missions} missions swept, {tally.Entries} note entries, {tally.Overflowed} of them taller than the retired 30 px pitch");
        ctx.Same(CampaignSequence.MissionCount, tally.Missions, $"every mission's briefing was driven to a completed reveal");
        ctx.Same(0, tally.Empty, $"and every one of them wrote its objectives onto the parchment");
        ctx.Same(0, tally.Rows, $"while no mission's briefing carried a row beyond its three buttons");
        ctx.Check(tally.Overflowed > 0,
            $"the sweep really contains entries the fixed pitch could not hold, so this is a live check ({tally.Overflowed} of {tally.Entries})");
        ctx.Same(0, tally.Overlaps, $"no entry starts above the bottom of the entry before it");
        ctx.Same(0, tally.Past, $"and no mission's note runs past the parchment's authored 240 px box");
        ctx.Same(0, tally.Clipped, $"so no objective line is dropped for want of room");
        if (Directory.Exists(dir))
        {
            Directory.Delete(dir, true);
        }
    }

    private sealed class Tally
    {
        internal int Missions;
        internal int Empty;
        internal int Rows;
        internal int Entries;
        internal int Drawn;
        internal int Overflowed;
        internal int Overlaps;
        internal int Clipped;
        internal int Past;
    }
}
