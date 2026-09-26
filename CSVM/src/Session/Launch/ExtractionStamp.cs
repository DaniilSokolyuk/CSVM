using System;
using System.IO;
using System.Text.Json;
using CSVM.Utils;

namespace CSVM.Session.Launch;

/// <summary>
/// Boot-time check of the extraction tree's provenance stamp, <c>extracted/VERSION.json</c>,
/// written by <c>ExtractAssets.ps1</c> / <c>ExtractRof.ps1</c>: which unzbd built the tree
/// (version line, exe hash, fork commit when known), when, and under which stamp schema.
/// The loaders prefer an unpacked sibling dir over its <c>.zip</c>, so a partially
/// re-extracted tree can silently mix vintages, the stamp is how a "it looks wrong" report
/// starts from a known extractor version instead of a guess.
/// </summary>
public static class ExtractionStamp
{
    /// <summary>The stamp schema this build's loaders expect. Bump it, together with
    /// <c>$StampSchema</c> in ExtractAssets.ps1 AND ExtractRof.ps1, whenever a reader change
    /// invalidates old extractions; <c>ExtractionStampTests</c> reads the three and refuses a
    /// commit that moves fewer than all of them.</summary>
    public const int Schema = 3;

    /// <summary>Whether the tree under <paramref name="dataRoot"/> is stamped below
    /// <paramref name="need"/>, with the re-extract instruction as <paramref name="reason"/>.
    /// A tree with no stamp, or one whose stamp does not read, is not behind: the dev tree holds
    /// extractions that predate the stamp, and a caller that blocks on this must not refuse them.</summary>
    public static bool Behind(string dataRoot, int need, out string? reason)
    {
        reason = null;
        int? found = Stamped(dataRoot);
        if (found == null || found >= need)
        {
            return false;
        }

        reason = $"the extraction tree is stamped schema={found} and this build reads schema={need} or later; re-run ExtractAssets.ps1 and ExtractRof.ps1 (with -Force if everything looks up to date)";
        return true;
    }

    /// <summary>Compares the stamp under <paramref name="dataRoot"/> against
    /// <see cref="Schema"/> and logs AT MOST one warning line, stale schema, missing file,
    /// or unreadable, each naming the fix. Warn, never block: the dev tree holds years of
    /// valid extractions that predate the stamp.</summary>
    public static void Check(string dataRoot)
    {
        var path = Path.Combine(dataRoot, "extracted", "VERSION.json");
        if (!File.Exists(path))
        {
            Log.Warn("core", $"extraction tree has no version stamp path={path}, cannot tell which extractor produced it; re-run ExtractAssets.ps1 (and ExtractRof.ps1) to stamp it");
            return;
        }
        try
        {
            // Read as text, not bytes: the scripts write UTF-8 with a BOM (PowerShell 5.1's
            // -Encoding UTF8), which the byte-based parser rejects; the text reader strips it.
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (!doc.RootElement.TryGetProperty("schema", out var schema) ||
                schema.ValueKind != JsonValueKind.Number)
            {
                Log.Warn("core", $"extraction stamp carries no schema integer path={path}, re-run ExtractAssets.ps1 to rewrite it");
                return;
            }
            int found = schema.GetInt32();
            if (found != Schema)
            {
                Log.Warn("core", $"extraction stamp schema={found} but this build expects schema={Schema} path={path}, the tree's vintage no longer matches the loaders; re-run ExtractAssets.ps1 and ExtractRof.ps1 (with -Force if everything looks up to date)");
            }
        }
        catch (Exception e) when (e is IOException or JsonException or FormatException)
        {
            Log.Warn("core", $"extraction stamp unreadable path={path} error={e.GetType().Name}, re-run ExtractAssets.ps1 to rewrite it");
        }
    }

    // The stamp's schema integer, or null when there is no stamp, no schema in it, or it does not
    // read. Silent: Check above is the one place a stamp problem is reported to the player.
    private static int? Stamped(string dataRoot)
    {
        var path = Path.Combine(dataRoot, "extracted", "VERSION.json");
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            return doc.RootElement.TryGetProperty("schema", out var schema) && schema.ValueKind == JsonValueKind.Number
                ? schema.GetInt32() : null;
        }
        catch (Exception e) when (e is IOException or JsonException or FormatException)
        {
            return null;
        }
    }
}
