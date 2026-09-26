using System;
using System.IO;

namespace CSVM;

/// <summary>
/// Resolves the per-chapter and per-mission extraction paths (gamez / texture / zrdr) under a
/// data root, preferring an unpacked sibling folder over its <c>.zip</c>. Static and engine-free
/// so every entry point, <c>--anim-lab</c> included, resolves the paths a normal session does.
/// ⚠ The <c>--gamez=</c>/<c>--textures=</c>/<c>--zrdr=</c>/<c>--sounds=</c> overrides are the
/// caller's policy and stay in <see cref="CSVM.Session.Launch.GameSession"/>.
/// </summary>
public static class SessionPaths
{
    /// <summary><c>--zip-assets</c>: ignore every unpacked sibling folder and read the <c>.zip</c>,
    /// the only shape an exported build ships. Process-wide, set once at startup, because the
    /// asset shape is a property of the run rather than of one path.
    /// ⚠ The two shapes do not fail alike, so an unpacked tree cannot reproduce a zip-only bug at
    /// all. That is how one shipped: <see cref="Mech3.SoundArchive"/>'s entry in
    /// docs/architecture.md.</summary>
    public static bool ForceZipped { get; set; }

    /// <summary>Prefer the unpacked sibling folder from <c>ExtractAssets.ps1 -Unzip</c> when it
    /// exists (loose JSON/PNG/WAV: no zip decompression at load); else the <c>.zip</c> path
    /// verbatim. The loaders (`GameZ`/`TextureArchive`/`Zrdr`) read either shape.
    /// <see cref="ForceZipped"/> takes the zip whenever there IS one, and otherwise falls through:
    /// a run that asked for zips must still start where only the folder was ever extracted.</summary>
    public static string PreferUnzipped(string zipPath)
    {
        if (ForceZipped && File.Exists(zipPath))
        {
            return zipPath;
        }
        var dir = Path.Combine(Path.GetDirectoryName(zipPath)!, Path.GetFileNameWithoutExtension(zipPath));
        return Directory.Exists(dir) ? dir : zipPath;
    }

    /// <summary>The chapter's texture archive, the plane skins and world textures. Prefers the
    /// chapter's top-budget <c>rtextureN</c> set (<c>N</c> ≈ the set's size in MB of late-90s
    /// texture memory; every chapter ships 2/4/6/8 plus one full-quality tier) over
    /// <c>texture.zip</c>: the tiers are what the original renders, and hundreds of same-name,
    /// same-size textures differ in content from the base archive, the gauge needles only carry
    /// their painted alpha silhouette there.</summary>
    public static string ChapterTextures(string dataRoot, string chapter)
    {
        var dir = Path.Combine(dataRoot, "extracted", chapter);
        string best = "texture";
        int bestN = -1;
        if (Directory.Exists(dir))
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(dir, "rtexture*"))
            {
                var name = Path.GetFileNameWithoutExtension(entry);
                if (int.TryParse(name.Substring("rtexture".Length), out int n) && n > bestN)
                {
                    bestN = n;
                    best = name;
                }
            }
        }
        return PreferUnzipped(Path.Combine(dir, best + ".zip"));
    }

    /// <summary>The chapter's world GameZ (<c>extracted/&lt;chapter&gt;/gamez.zip</c>), the single
    /// <c>world1</c> node and everything under it.</summary>
    public static string ChapterGamez(string dataRoot, string chapter) =>
        PreferUnzipped(Path.Combine(dataRoot, "extracted", chapter, "gamez.zip"));

    /// <summary>The chapter's zrdr scope (<c>extracted/&lt;chapter&gt;/zrdr.zip</c>), zepstate,
    /// startanims, and the chapter-wide anim defs.</summary>
    public static string ChapterZrdr(string dataRoot, string chapter) =>
        PreferUnzipped(Path.Combine(dataRoot, "extracted", chapter, "zrdr.zip"));

    /// <summary>Where the ten <c>.mpg</c> cinemas the extraction copies across sit. It is one
    /// directory deeper than the bitmaps the same layout rows name, which is the base the original
    /// resolves every movie name under (<c>docs/formats/cinemas.md</c>).</summary>
    public static string CinemaFolder(string dataRoot) =>
        Path.Combine(dataRoot, "extracted", "rof", "ASSETS", "GRAPHICS", "MPG");

    /// <summary>The cinema file a name means, with <c>.mpg</c> supplied when the name carries no
    /// extension, or the name under <see cref="CinemaFolder"/> verbatim when nothing there matches.
    /// ⚠ Match without regard to case. Four of the ten are named in the data in a case the files on
    /// disk do not have, so every caller that names a cinema resolves through here.</summary>
    public static string Cinema(string dataRoot, string name)
    {
        string folder = CinemaFolder(dataRoot);
        string want = Path.GetExtension(name).Length == 0 ? name + ".mpg" : name;
        string fallback = Path.Combine(folder, want);
        if (name.Length == 0 || !Directory.Exists(folder))
        {
            return fallback;
        }

        foreach (string file in Directory.EnumerateFiles(folder))
        {
            if (string.Equals(Path.GetFileName(file), want, StringComparison.OrdinalIgnoreCase))
            {
                return file;
            }
        }

        return fallback;
    }

    /// <summary>The mission's zrdr scope (<c>extracted/&lt;chapter&gt;/&lt;mission&gt;/zrdr.zip</c>)
    ///, spawn points, danger zones, weather, objectives, and the mission's own anim defs.</summary>
    public static string MissionZrdr(string dataRoot, string chapter, string mission) =>
        PreferUnzipped(Path.Combine(dataRoot, "extracted", chapter, mission, "zrdr.zip"));
}
