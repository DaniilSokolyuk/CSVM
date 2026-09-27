using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace CSVM.Net;

/// <summary>
/// The port this machine last mapped in its router, kept in one small file in the user's settings
/// directory. A run that hosts on a different port deletes the remembered one first. That is how a
/// mapping left by a crash on another port is cleared. Best effort throughout: an unreadable file
/// recalls <see cref="UpnpLease.NoPort"/>, and a failed write is dropped.
/// </summary>
public sealed class UpnpPortMemory
{
    /// <summary>The file's name inside the directory.</summary>
    public const string FileName = "upnp_port.txt";

    private readonly string _path;

    /// <summary>A memory in <paramref name="directory"/>, which must be absolute.</summary>
    public UpnpPortMemory(string directory)
    {
        if (!Path.IsPathRooted(directory))
        {
            throw new ArgumentException($"port memory needs an absolute directory, got '{directory}'", nameof(directory));
        }

        _path = Path.Combine(directory, FileName);
    }

    /// <summary>The remembered port, or <see cref="UpnpLease.NoPort"/> when none is.</summary>
    public int Recall()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return UpnpLease.NoPort;
            }

            string text = File.ReadAllText(_path).Trim();
            return int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int port)
                && UpnpLease.IsPort(port)
                    ? port
                    : UpnpLease.NoPort;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return UpnpLease.NoPort;
        }
    }

    /// <summary>Remembers <paramref name="port"/> as the one this machine holds mapped.</summary>
    public void Remember(int port)
    {
        if (!UpnpLease.IsPort(port))
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, port.ToString(CultureInfo.InvariantCulture), new UTF8Encoding(false));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Forgets <paramref name="port"/> once its mapping is gone. A different remembered
    /// port stays, since its mapping may still stand.</summary>
    public void Forget(int port)
    {
        if (Recall() != port)
        {
            return;
        }

        try
        {
            File.Delete(_path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }
}
