using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CSVM.Session.Launch;

namespace CSVM.Extraction;

/// <summary>Writes <c>extracted/VERSION.json</c>, the provenance stamp
/// <see cref="ExtractionStamp"/> reads at boot. Each half of extraction owns one field:
/// <c>assets</c> for the ZBD half and <c>rof</c> for the rest. A write replaces only its own field
/// plus <c>schema</c>, which is always <see cref="ExtractionStamp.Schema"/>. The file is
/// UTF-8 without a BOM. Field list: <c>docs/architecture/Extraction.md</c>.</summary>
public static class ExtractionStampWriter
{
    /// <summary>The stamp's file name inside the extraction root.</summary>
    public const string FileName = "VERSION.json";

    // The value every stamp field's "script" carries, naming the engine as the writer rather than
    // the PowerShell script that wrote the field before.
    private const string Writer = "CSVM";

    /// <summary>Stamps the ZBD half under <paramref name="extractedDir"/>: the unzbd that ran,
    /// and <paramref name="utcNow"/> as the date.</summary>
    public static string WriteAssets(string extractedDir, UnzbdIdentity unzbd, DateTime utcNow)
    {
        var assets = new JsonObject
        {
            ["script"] = Writer,
            ["date"] = Date(utcNow),
            ["unzbdVersion"] = unzbd.VersionLine,
            ["unzbdSha256"] = unzbd.Sha256,
        };
        if (unzbd.Commit != null)
        {
            assets["unzbdCommit"] = unzbd.Commit;
        }

        return Merge(extractedDir, "assets", assets);
    }

    /// <summary>Stamps the <c>.rof</c> half under <paramref name="extractedDir"/>.
    /// <paramref name="movies"/> is how many cinemas the MPG copy left in the tree. It is the first
    /// thing asked of a tree that plays none.</summary>
    public static string WriteRof(string extractedDir, int movies, DateTime utcNow) =>
        Merge(extractedDir, "rof", new JsonObject
        {
            ["script"] = Writer,
            ["date"] = Date(utcNow),
            ["movies"] = movies,
        });

    /// <summary>Sets <paramref name="field"/> and <c>schema</c> in the stamp under
    /// <paramref name="extractedDir"/>, keeping every other field. A stamp that does not parse is
    /// rewritten from nothing, since the boot check already warns about it. Returns the path.</summary>
    public static string Merge(string extractedDir, string field, JsonNode value)
    {
        Directory.CreateDirectory(extractedDir);
        string path = Path.Combine(extractedDir, FileName);
        var stamp = Existing(path) ?? new JsonObject();
        stamp["schema"] = ExtractionStamp.Schema;
        stamp[field] = value;
        string text = stamp.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(path, text + "\n", new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return path;
    }

    private static string Date(DateTime utcNow) =>
        utcNow.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    // Read as text so a stamp the PowerShell scripts wrote, which carries a BOM, still merges.
    private static JsonObject? Existing(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
        }
        catch (Exception e) when (e is IOException or JsonException)
        {
            return null;
        }
    }
}
