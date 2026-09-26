using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using CSVM.Utils;
using Godot;

namespace CSVM.Session.Campaign;

/// <summary>
/// The campaign Danger Zone photograph: one still per zone, written into the flying profile's own
/// directory. Its <c>Snap_&lt;mission&gt;_&lt;objective&gt;</c> name is the one the scrapbook's
/// capture rows resolve against (<c>docs/formats/campaign-screens.md</c>, "The danger-zone slot").
/// The objective number is the mission's own <c>dzones.zrd</c> <c>objective_numbers</c> entry.
/// The file the pilot writes and the row that draws it are named by the same data.
///
/// A latch stages the file under a pending extension, and <see cref="Commit"/> keeps it only
/// for a won mission. That is how the original drops the photographs of a failed attempt.
/// </summary>
public static class CampaignSnapshot
{
    /// <summary>The size a photograph is written at, which every file the retail game wrote is,
    /// whatever resolution it displayed at. The page then forces the print into its 164x123 region
    /// and the zoom draws it at this size, where the torn mount's window is cut for it. A pane of
    /// another shape is framed by <see cref="Window"/> first, never squashed into it.</summary>
    public const int Width = 640;

    /// <summary>The written height, the partner of <see cref="Width"/>.</summary>
    public const int Height = 480;

    // The objective-number band the original's commit sweep walks (FUN_004072a0): every pending
    // file in it is renamed on a win and deleted on a loss. Wider than the 18-31 the shipped
    // dzones.zrd assigns and wider than the 18-30 the debrief turns into mask bits.
    private const int FirstPending = 10;
    private const int LastPending = 31;

    // The staged extension the original writes during the flight, renamed to .PNG at mission end.
    private const string PendingExtension = "PN_";

    // Staged paths whose frame has not landed yet, with the verdict Commit left for them (null while
    // the mission is still being flown). Stage and Commit run on the main thread, a landing on a
    // worker, so every read and write holds Gate.
    private static readonly object Gate = new();
    private static readonly Dictionary<string, bool?> InFlight = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The part of a pane the photograph frames: the centred 4:3 window at the pane's
    /// full height, the flanks of a wider one dropped. A pane narrower than 4:3 keeps its full
    /// width and loses the top and bottom instead. The print fills the mount's window either way,
    /// so neither answer is a letterbox. A pane with no area answers an empty window.</summary>
    public static Rect2I Window(int width, int height)
    {
        if (width <= 0 || height <= 0)
        {
            return default;
        }

        if ((long)width * Height >= (long)height * Width)
        {
            int framed = Math.Max(1, (int)((long)height * Width / Height));
            return new Rect2I((width - framed) / 2, 0, framed, height);
        }

        int tall = Math.Max(1, (int)((long)width * Height / Width));
        return new Rect2I(0, (height - tall) / 2, width, tall);
    }

    /// <summary>The name a scrapbook capture row resolves against the profile directory:
    /// <c>Snap_&lt;mission&gt;_&lt;objective&gt;.PNG</c>, the mission being the 1-based campaign
    /// ordinal (<c>CampaignMission.Ordinal</c>) the book's own slot numbering uses.</summary>
    public static string FileName(int mission, int objective) =>
        string.Format(CultureInfo.InvariantCulture, "Snap_{0}_{1}.PNG", mission, objective);

    /// <summary>Requests one zone's photograph through <paramref name="pane"/> (the session hands
    /// the pilot's <see cref="Flight.Modes.DangerZonePhotograph"/>), written into
    /// <paramref name="directory"/> as a pending file scaled to <see cref="Width"/> by
    /// <see cref="Height"/> once the frame lands. Answers the path it will be staged at, or null
    /// when there was no pane to photograph.</summary>
    public static string? Stage(string directory, int mission, int objective, PaneRequest? pane)
    {
        string staged = Path.Combine(directory, Pending(mission, objective));
        string kept = Path.Combine(directory, FileName(mission, objective));
        lock (Gate)
        {
            InFlight[staged] = null;
        }

        if (pane == null || !pane(frame => Develop(frame, directory, staged, kept, objective)))
        {
            lock (Gate)
            {
                InFlight.Remove(staged);
            }

            Log.Warn("core", $"danger zone snapshot: no pane image for objective {objective}, nothing written");
            return null;
        }

        return staged;
    }

    /// <summary>Mission end: a won mission's staged photographs replace whatever the profile held
    /// under their scrapbook names, a lost one's are dropped. A photograph still on its way takes
    /// the same verdict when it lands. Answers how many photographs it acted on, so a caller can
    /// log the sweep.</summary>
    public static int Commit(string directory, int mission, bool won)
    {
        int acted = 0;
        lock (Gate)
        {
            for (int objective = FirstPending; objective <= LastPending; objective++)
            {
                string staged = Path.Combine(directory, Pending(mission, objective));
                if (InFlight.ContainsKey(staged))
                {
                    InFlight[staged] = won;
                    acted++;
                }
                else if (File.Exists(staged))
                {
                    Settle(staged, Path.Combine(directory, FileName(mission, objective)), won);
                    acted++;
                }
            }
        }

        return acted;
    }

    // Worker thread: the resize and the encode, then the mission-end verdict if Commit has already
    // passed this photograph by.
    private static void Develop(Image? frame, string directory, string staged, string kept, int objective)
    {
        bool written = false;
        if (frame == null || frame.GetWidth() <= 0 || frame.GetHeight() <= 0)
        {
            Log.Warn("core", $"danger zone snapshot: the pane never arrived for objective {objective}, nothing written");
        }
        else
        {
            // ⚠ Frame the pane before the resize; a pane wider than 4:3 is otherwise squeezed
            // whole into the file, which narrows everything in it.
            var window = Window(frame.GetWidth(), frame.GetHeight());
            var print = window.Size.X == frame.GetWidth() && window.Size.Y == frame.GetHeight()
                ? frame
                : frame.GetRegion(window);
            print.Resize(Width, Height, Image.Interpolation.Bilinear);
            Directory.CreateDirectory(directory);
            var err = print.SavePng(staged);
            written = err == Error.Ok;
            if (written)
            {
                Log.Info("core", $"danger zone snapshot: objective {objective} -> {staged}");
            }
            else
            {
                Log.Error("core", $"danger zone snapshot: could not write {staged} ({err})");
            }
        }

        lock (Gate)
        {
            if (InFlight.Remove(staged, out bool? verdict) && verdict is { } won && written)
            {
                Settle(staged, kept, won);
            }
        }
    }

    private static void Settle(string staged, string kept, bool won)
    {
        if (!won)
        {
            File.Delete(staged);
            return;
        }

        File.Delete(kept);
        File.Move(staged, kept);
    }

    private static string Pending(int mission, int objective) =>
        string.Format(CultureInfo.InvariantCulture, "Snap_{0}_{1}.{2}", mission, objective, PendingExtension);
}
