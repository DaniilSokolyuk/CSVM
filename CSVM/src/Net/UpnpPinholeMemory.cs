using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace CSVM.Net;

/// <summary>
/// The IPv6 pinhole this machine last opened in its router, kept in one small file in the user's
/// settings directory. The file holds its UniqueID, port, address and lease end. A run that crashed leaves it
/// behind, and the next run deletes that pinhole before opening its own. Best effort throughout: an
/// unreadable file recalls none, and a failed write is dropped.
/// </summary>
public sealed class UpnpPinholeMemory
{
    /// <summary>The file's name inside the directory.</summary>
    public const string FileName = "upnp_pinhole.txt";

    private readonly string _path;

    /// <summary>A memory in <paramref name="directory"/>, which must be absolute.</summary>
    public UpnpPinholeMemory(string directory)
    {
        if (!Path.IsPathRooted(directory))
        {
            throw new ArgumentException($"pinhole memory needs an absolute directory, got '{directory}'", nameof(directory));
        }

        _path = Path.Combine(directory, FileName);
    }

    /// <summary>The remembered pinhole, or null when none is.</summary>
    public PinholeLease? Recall()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return null;
            }

            string[] parts = File.ReadAllText(_path).Trim().Split(' ');
            return parts.Length == 4
                && int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out int id)
                && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out int port)
                && long.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out long expires)
                && UpnpLease.IsPort(port) && id <= ushort.MaxValue && IgdPinhole.IsGlobalUnicast(parts[2])
                && expires <= DateTimeOffset.MaxValue.ToUnixTimeSeconds()
                    ? new PinholeLease(id, port, parts[2], DateTimeOffset.FromUnixTimeSeconds(expires).UtcDateTime)
                    : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Remembers <paramref name="lease"/> as the pinhole this machine holds.</summary>
    public void Remember(PinholeLease lease)
    {
        if (!UpnpLease.IsPort(lease.Port) || !IgdPinhole.IsGlobalUnicast(lease.Address))
        {
            return;
        }

        long expires = new DateTimeOffset(DateTime.SpecifyKind(lease.ExpiresUtc, DateTimeKind.Utc)).ToUnixTimeSeconds();
        string text = string.Join(' ',
            lease.Id.ToString(CultureInfo.InvariantCulture),
            lease.Port.ToString(CultureInfo.InvariantCulture),
            lease.Address,
            expires.ToString(CultureInfo.InvariantCulture));
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, text, new UTF8Encoding(false));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Forgets pinhole <paramref name="id"/> once it is gone or its lease ran out. A
    /// different remembered pinhole stays, since it may still stand.</summary>
    public void Forget(int id)
    {
        if (Recall()?.Id != id)
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
