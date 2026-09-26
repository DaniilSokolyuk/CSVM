using System;
using System.Collections.Generic;
using System.IO;
using CSVM.Flight.Hangar;
using CSVM.Mech3;

namespace CSVM.UI.Hangar;

/// <summary>
/// The two plane-diagram sheets the original draws beside a fitted aircraft, framed per airframe:
/// a plan view (<see cref="Top"/>) and a head-on view (<see cref="Front"/>). Each ships as one tall
/// PNG of eleven equal frames stacked top to bottom in airframe-id order, which is the multi-frame
/// idiom <c>ol_p_planetopicon</c> draws from.
///
/// <para>Shared rather than per-page because three screens want the same picture: ammo selection,
/// the campaign's flight check and the hangar's airframe list. The decode is cached for the
/// process, misses included, so an install without the extraction probes the disk once instead of
/// once per frame.</para>
/// </summary>
public static class PlaneDiagrams
{
    /// <summary>The plan-view sheet's file name under <c>extracted/rof/ASSETS/GRAPHICS</c>.</summary>
    public const string Top = "OL_PLANEDIAGRAMSTOP.PNG";

    /// <summary>The head-on sheet's file name, same directory.</summary>
    public const string Front = "OL_PLANEDIAGRAMSFRONT.PNG";

    private static readonly Dictionary<string, TgaImage?> Sheets = new(StringComparer.Ordinal);

    /// <summary>One airframe's frame of <paramref name="file"/> (<see cref="Top"/> or
    /// <see cref="Front"/>) under <paramref name="root"/>, or null when the extraction lacks the
    /// sheet or its height is not a whole multiple of the airframe count. A sheet of the wrong
    /// shape is not this layout, so it draws nothing rather than a mis-sliced picture.</summary>
    public static TgaImage? Frame(string root, string file, int airframe)
    {
        var sheet = Sheet(Path.Combine(root, "extracted", "rof", "ASSETS", "GRAPHICS", file));
        int count = HangarEconomy.Airframes.Length;
        if (sheet == null || count <= 0 || sheet.Height % count != 0)
        {
            return null;
        }

        int height = sheet.Height / count;
        int stride = sheet.Width * 4;
        var rgba = new byte[stride * height];
        Array.Copy(sheet.Rgba, Math.Clamp(airframe, 0, count - 1) * height * stride, rgba, 0, rgba.Length);
        return TgaImage.FromRgba(sheet.Width, height, rgba);
    }

    private static TgaImage? Sheet(string path)
    {
        if (!Sheets.TryGetValue(path, out var sheet))
        {
            sheet = ArtImage.TryLoad(path);
            Sheets[path] = sheet;
        }

        return sheet;
    }
}
