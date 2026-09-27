using System;
using System.Buffers.Binary;
using System.Globalization;

namespace CSVM.Net;

/// <summary>
/// The part of a build's version two peers compare before they play: MAJOR.MINOR of the SemVer
/// number in <c>project.godot</c>. Builds that differ only in the patch play together, because a
/// patch never changes the network protocol. A build with no readable version is
/// <see cref="Unknown"/>, which plays only with another unknown build. Engine-free: the caller
/// hands in the version string, so a unit parses and compares without an engine.
/// </summary>
public readonly record struct NetBuildVersion
{
    /// <summary>The bytes a version takes on the wire: the major and the minor, 16 bits each.
    /// </summary>
    public const int WireBytes = 4;

    /// <summary>What the log and a refusal call a build with no readable version.</summary>
    public const string UnknownText = "unknown";

    // Both words at this value are the wire's unknown. A known major or minor stays below it.
    private const ushort UnknownWord = ushort.MaxValue;

    /// <summary>A known MAJOR.MINOR. Each part lies in 0 to 65534, since 65535 is the wire's
    /// unknown.</summary>
    public NetBuildVersion(int major, int minor)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(major);
        ArgumentOutOfRangeException.ThrowIfNegative(minor);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(major, (int)UnknownWord);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(minor, (int)UnknownWord);
        Major = (ushort)major;
        Minor = (ushort)minor;
        Known = true;
    }

    /// <summary>A build whose version could not be read. The default value.</summary>
    public static NetBuildVersion Unknown => default;

    /// <summary>The major number, 0 while <see cref="Known"/> is false.</summary>
    public ushort Major { get; }

    /// <summary>The minor number, 0 while <see cref="Known"/> is false.</summary>
    public ushort Minor { get; }

    /// <summary>Whether a version was read at all.</summary>
    public bool Known { get; }

    private ushort MajorWord => Known ? Major : UnknownWord;

    private ushort MinorWord => Known ? Minor : UnknownWord;

    /// <summary>MAJOR.MINOR of <paramref name="version"/>, a SemVer core such as <c>0.7.2</c> or
    /// <c>0.7</c>, with an optional leading <c>v</c> and any pre-release or build suffix ignored.
    /// Anything else, <c>unknown</c> included, reads as <see cref="Unknown"/>.</summary>
    public static NetBuildVersion Parse(string? version)
    {
        var text = (version ?? "").AsSpan().Trim();
        if (text.Length > 0 && text[0] is 'v' or 'V')
        {
            text = text[1..];
        }

        int suffix = text.IndexOfAny('-', '+');
        if (suffix >= 0)
        {
            text = text[..suffix];
        }

        Span<Range> parts = stackalloc Range[4];
        int count = text.Split(parts, '.');
        if (count is < 2 or > 3
            || !TryPart(text[parts[0]], out int major)
            || !TryPart(text[parts[1]], out int minor)
            || (count == 3 && !TryPart(text[parts[2]], out _)))
        {
            return Unknown;
        }

        return major < UnknownWord && minor < UnknownWord ? new NetBuildVersion(major, minor) : Unknown;
    }

    /// <summary>Reads a version off its wire form. False when exactly one word is the unknown
    /// value, which no writer produces.</summary>
    public static bool TryRead(ReadOnlySpan<byte> from, out NetBuildVersion version)
    {
        version = Unknown;
        if (from.Length < WireBytes)
        {
            return false;
        }

        return TryFromWords(
            BinaryPrimitives.ReadUInt16LittleEndian(from), BinaryPrimitives.ReadUInt16LittleEndian(from[2..]), out version);
    }

    /// <summary>A version from its two wire words, as <see cref="TryRead"/> reads them.</summary>
    public static bool TryFromWords(ushort major, ushort minor, out NetBuildVersion version)
    {
        version = Unknown;
        if (major == UnknownWord || minor == UnknownWord)
        {
            return major == minor;
        }

        version = new NetBuildVersion(major, minor);
        return true;
    }

    /// <summary>Whether a build of this version and one of <paramref name="other"/> play together:
    /// the same major and minor, or both unknown.</summary>
    public bool PlaysWith(NetBuildVersion other) => this == other;

    /// <summary>Writes the wire form into the first <see cref="WireBytes"/> bytes of
    /// <paramref name="into"/>.</summary>
    public void Write(Span<byte> into)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(into, MajorWord);
        BinaryPrimitives.WriteUInt16LittleEndian(into[2..], MinorWord);
    }

    /// <summary>Writes the wire form at a message writer's position.</summary>
    public void Write(ref NetMessageWriter writer)
    {
        writer.WriteUInt16(MajorWord);
        writer.WriteUInt16(MinorWord);
    }

    /// <summary>MAJOR.MINOR, such as <c>0.7</c>, or <see cref="UnknownText"/>.</summary>
    public override string ToString() => Known
        ? $"{Major.ToString(CultureInfo.InvariantCulture)}.{Minor.ToString(CultureInfo.InvariantCulture)}"
        : UnknownText;

    private static bool TryPart(ReadOnlySpan<char> part, out int value) =>
        int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out value);
}

/// <summary>
/// A lobby's first word to a peer on connect, sent by both ends: the build's
/// <see cref="NetBuildVersion"/>. A host refuses a guest whose version does not play with its
/// own, and a guest refuses such a host. Kept in the lobby and never passed to a session.
/// ⚠ Do not change this layout. It is how two different builds recognise each other, so a
/// change makes every older build read a newer one as silent.
/// </summary>
public readonly record struct BuildVersionMessage(NetBuildVersion Version) : INetMessage<BuildVersionMessage>
{
    /// <summary>The fixed width of the message, header included.</summary>
    public const int Size = NetMessage.HeaderBytes + NetBuildVersion.WireBytes;

    /// <inheritdoc/>
    public static NetMessageType Type => NetMessageType.BuildVersion;

    /// <inheritdoc/>
    public static NetReliability Reliability => NetReliability.Reliable;

    /// <inheritdoc/>
    public static bool TryRead(ReadOnlySpan<byte> from, out BuildVersionMessage message)
    {
        message = default;
        var reader = new NetMessageReader(from);
        if (!reader.Is(Size) || reader.Type != Type
            || !NetBuildVersion.TryFromWords(reader.ReadUInt16(), reader.ReadUInt16(), out var version))
        {
            return false;
        }

        message = new BuildVersionMessage(version);
        return true;
    }

    /// <inheritdoc/>
    public int Write(Span<byte> into)
    {
        var writer = new NetMessageWriter(into, Type);
        Version.Write(ref writer);
        return writer.Close();
    }
}
