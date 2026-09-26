using System;
using System.Security.Cryptography;
using System.Text;
using Godot;

namespace CSVM.Tooling;

/// <summary>
/// The engine half of the golden-image tripwire: reduces a captured frame to one md5 and names the
/// hardware that drew it.
/// ⚠ Hash the raw pixel buffer (<see cref="Image.GetData"/>), never the saved PNG, see GOLD-10 in
/// docs/verification.md. The adapter travels with the hash so a driver/GPU change reads as a
/// one-line explanation rather than an unexplained mass failure.
/// ⚠ The manifest comparison lives in PowerShell, not here: every suite in
/// <c>TestHarness</c> runs inside one <c>_Ready</c> call and never yields a frame, so nothing
/// in-engine can photograph anything to compare.
/// </summary>
public static class GoldenShot
{
    /// <summary>md5 of the image's raw pixel bytes, lower-case hex.</summary>
    public static string PixelHash(Image image)
    {
        byte[] data = image.GetData();
        byte[] digest = MD5.HashData(data);
        var sb = new StringBuilder(digest.Length * 2);
        foreach (byte b in digest)
        {
            sb.Append(b.ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
        }
        return sb.ToString();
    }

    /// <summary>The GPU that rendered this session, as <c>&lt;adapter&gt; / &lt;api&gt;</c>.
    /// Deliberately one opaque string: it is read by a human comparing two runs, never parsed.</summary>
    public static string Adapter()
    {
        try
        {
            string name = RenderingServer.GetVideoAdapterName();
            string api = RenderingServer.GetVideoAdapterApiVersion();
            return $"{name} / {api}";
        }
        catch (Exception e)
        {
            return $"unknown ({e.GetType().Name})";
        }
    }
}
