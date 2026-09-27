using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace CSVM.Extraction;

/// <summary>The <c>unzbd cs</c> mode an archive is extracted with, and the extension its output
/// takes: <c>.json</c> for <c>interp</c>, <c>.zip</c> for every other mode.</summary>
public sealed record ZbdMode(string Mode, string Extension);

/// <summary>What unzbd's stderr said about one archive that extracted. The two counts are the
/// informational notes a full run prints thousands of; <see cref="Unexpected"/> is every other
/// line, shown to the player as a warning.</summary>
public sealed record UnzbdNotes(int TransformNotes, int AnimNotes, IReadOnlyList<string> Unexpected);

/// <summary>The pure rules of the ZBD half of extraction. They give an archive's mode, where its output
/// goes, when an output is up to date, and how unzbd's stderr is read.
/// Engine-free, so <c>CSVM.Tests</c> pins each rule without running unzbd. The runner is
/// <see cref="ZbdExtraction"/>; the note wording is <c>docs/architecture/Extraction.md</c>.</summary>
public static class ZbdPlan
{
    private static readonly ZbdMode Interp = new("interp", ".json");
    private static readonly ZbdMode GameZ = new("gamez", ".zip");
    private static readonly ZbdMode Sounds = new("sounds", ".zip");
    private static readonly ZbdMode Reader = new("reader", ".zip");
    private static readonly ZbdMode Textures = new("textures", ".zip");
    private static readonly ZbdMode Anim = new("anim", ".zip");

    private static readonly Regex RTexture = new(@"^rtexture\d+$", RegexOptions.CultureInvariant);

    /// <summary>The mode for an archive named <paramref name="baseName"/> without its extension,
    /// compared lower-cased. Null for a name no mode is mapped to. <c>planes</c> is a GameZ-format file, so it shares the <c>gamez</c> mode.</summary>
    public static ZbdMode? ModeFor(string baseName)
    {
        string name = baseName.ToLowerInvariant();
        switch (name)
        {
            case "interp": return Interp;
            case "planes":
            case "gamez": return GameZ;
            case "soundsh":
            case "soundsl": return Sounds;
            case "zrdr": return Reader;
            case "rimage":
            case "texture": return Textures;
            case "cam_anim":
            case "mis_anim": return Anim;
            default: return RTexture.IsMatch(name) ? Textures : null;
        }
    }

    /// <summary>The output path, relative to the extraction root, for the archive at
    /// <paramref name="relativeZbd"/> under the install's <c>ZBD</c> folder. It takes
    /// <see cref="ZbdTree"/>'s case whatever the install's spelling, with the extension swapped.</summary>
    public static string OutputRelativePath(string relativeZbd, ZbdMode mode) =>
        Path.ChangeExtension(ZbdTree.Canonical(relativeZbd), mode.Extension)
            .Replace('/', Path.DirectorySeparatorChar);

    /// <summary>Whether an output written at <paramref name="outputWrite"/> (null when it does not
    /// exist) is up to date against a source written at <paramref name="sourceWrite"/>. An output
    /// as new as its source is up to date, so an unchanged install re-extracts nothing.</summary>
    public static bool UpToDate(DateTime sourceWrite, DateTime? outputWrite) =>
        outputWrite is DateTime written && written >= sourceWrite;

    /// <summary>Whether a zip written at <paramref name="zipWrite"/> is expanded again into a sibling
    /// folder written at <paramref name="folderWrite"/> (null when absent). Only a zip strictly newer
    /// than its folder, or <paramref name="force"/>, redoes an existing folder.</summary>
    public static bool UnzipNeeded(DateTime zipWrite, DateTime? folderWrite, bool force) =>
        force || folderWrite is not DateTime folder || zipWrite > folder;

    /// <summary>Whether the output at <paramref name="outputRelativePath"/> is expanded into its
    /// sibling folder even without the unzip option. Only the root <c>rimage.zip</c> is: the HUD
    /// font, the gun reticle and the board art read its PNGs loose, never from the zip.</summary>
    public static bool AlwaysUnzipped(string outputRelativePath) =>
        string.Equals(outputRelativePath, "rimage.zip", StringComparison.OrdinalIgnoreCase);

    /// <summary>Sorts unzbd's stderr for an archive that extracted. A transform note is unzbd
    /// recomposing a node matrix from its angles inexactly; the stored matrix is kept and read.
    /// An anim note is an event field or connector ref failing validation; the value is kept.
    /// Every note is counted, never shown, because a full run prints thousands.</summary>
    public static UnzbdNotes Classify(IEnumerable<string> stderrLines)
    {
        int transform = 0;
        int anim = 0;
        var unexpected = new List<string>();
        foreach (string line in stderrLines)
        {
            if (Contains(line, "object3d transform fail"))
            {
                transform++;
            }
            else if (Contains(line, "VAL FAIL") || Contains(line, "anim def duplicate anim ref"))
            {
                anim++;
            }
            else
            {
                unexpected.Add(line);
            }
        }

        return new UnzbdNotes(transform, anim, unexpected);
    }

    private static bool Contains(string line, string note) => line.Contains(note, StringComparison.OrdinalIgnoreCase);
}
