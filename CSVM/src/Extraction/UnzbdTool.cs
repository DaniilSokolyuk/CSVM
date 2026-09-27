using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace CSVM.Extraction;

/// <summary>What one unzbd run returned: its exit code and its output split into non-blank
/// lines. stderr is diagnostics, never the verdict; the exit code is.</summary>
public sealed record UnzbdRun(int ExitCode, IReadOnlyList<string> Stdout, IReadOnlyList<string> Stderr);

/// <summary>Which unzbd built a tree, as the stamp records it. <see cref="Commit"/> is the fork
/// checkout's HEAD when the exe sits at <c>&lt;checkout&gt;/target/release/</c>, else null.</summary>
public sealed record UnzbdIdentity(string VersionLine, string Sha256, string? Commit);

/// <summary>The bundled mech3ax <c>unzbd</c>, always run as a child process.
/// ⚠ Do not link or load unzbd into the engine. It is EUPL-1.2 and the engine GPL-3, and the
/// process boundary is what keeps the two separate (<c>packaging/README.md</c>).</summary>
public static class UnzbdTool
{
    /// <summary>The tool's file name on a platform: <c>unzbd.exe</c> on Windows, <c>unzbd</c>
    /// elsewhere.</summary>
    public static string FileName(bool windows) => windows ? "unzbd.exe" : "unzbd";

    /// <summary>Where a release carries the tool: <c>tools/</c> beside the executable in
    /// <paramref name="executableFolder"/>, named for this platform.</summary>
    public static string DefaultPath(string executableFolder) =>
        Path.Combine(executableFolder, "tools", FileName(OperatingSystem.IsWindows()));

    /// <summary>Runs <paramref name="tool"/> with <paramref name="arguments"/> and waits for it.
    /// Both streams are drained concurrently, since an anim archive writes thousands of stderr
    /// lines. Cancelling kills the process tree and throws.</summary>
    public static UnzbdRun Run(string tool, IEnumerable<string> arguments, CancellationToken cancel = default)
    {
        var start = new ProcessStartInfo(tool)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start) ?? throw new InvalidOperationException($"unzbd did not start: {tool}");
        var stdout = process.StandardOutput.ReadToEndAsync(cancel);
        var stderr = process.StandardError.ReadToEndAsync(cancel);
        try
        {
            process.WaitForExitAsync(cancel).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw;
        }

        return new UnzbdRun(process.ExitCode, Lines(stdout.GetAwaiter().GetResult()), Lines(stderr.GetAwaiter().GetResult()));
    }

    /// <summary>The identity of <paramref name="tool"/>. The first line <c>--version</c> prints is
    /// kept whole, since its build timestamp is what tells fork builds apart. The file's SHA-256 is
    /// upper-case hex, and the fork commit is read when git can.</summary>
    public static UnzbdIdentity Identify(string tool)
    {
        var version = Run(tool, new[] { "--version" });
        string line = version.Stdout.Count > 0 ? version.Stdout[0] : version.Stderr.Count > 0 ? version.Stderr[0] : string.Empty;
        string sha;
        using (var stream = File.OpenRead(tool))
        {
            sha = Convert.ToHexString(SHA256.HashData(stream));
        }

        return new UnzbdIdentity(line.Trim(), sha, ForkCommit(tool));
    }

    // A bare exe, as a release ships it, has no checkout three levels up; that is not an error.
    private static string? ForkCommit(string tool)
    {
        string? release = Path.GetDirectoryName(Path.GetFullPath(tool));
        string? checkout = Path.GetDirectoryName(Path.GetDirectoryName(release));
        if (checkout == null || !(Directory.Exists(Path.Combine(checkout, ".git")) || File.Exists(Path.Combine(checkout, ".git"))))
        {
            return null;
        }

        try
        {
            var head = Run("git", new[] { "-C", checkout, "rev-parse", "HEAD" });
            return head.ExitCode == 0 && head.Stdout.Count > 0 ? head.Stdout[0].Trim() : null;
        }
        catch (Win32Exception)
        {
            return null;
        }
    }

    private static List<string> Lines(string text)
    {
        var lines = new List<string>();
        foreach (string line in text.Split('\n'))
        {
            string trimmed = line.TrimEnd('\r');
            if (trimmed.Trim().Length > 0)
            {
                lines.Add(trimmed);
            }
        }

        return lines;
    }
}
