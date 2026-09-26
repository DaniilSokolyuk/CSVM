using System;
using System.Collections.Generic;

namespace CSVM.Session.Launch;

/// <summary>
/// The menu cue table: which wav under <c>extracted/rof/ASSETS/SOUNDS</c> a semantic cue name
/// resolves to. The four files are the four the original's globals script binds; the scripts name
/// raw wav files, so the indirection from a name to a file is the remake's and lives here, where
/// the audio service owns lookup. Engine-free so the table is unit-tested against the names the
/// presentations ask for.
/// </summary>
public static class MenuCueTable
{
    private static readonly Dictionary<string, string> Files = new(StringComparer.Ordinal)
    {
        ["menu.rollover"] = "MOUSEOVER.WAV",
        ["menu.click"] = "MOUSECLICK.WAV",
        ["menu.text"] = "ENTERTEXT.WAV",
        ["menu.text-error"] = "ENTERTEXT_ERROR.WAV",
    };

    /// <summary>Every cue name the table resolves.</summary>
    public static IReadOnlyCollection<string> Names => Files.Keys;

    /// <summary>The wav file a cue name resolves to, or null for a name the table lacks.</summary>
    public static string? FileFor(string cueName) =>
        cueName != null && Files.TryGetValue(cueName, out var file) ? file : null;
}
