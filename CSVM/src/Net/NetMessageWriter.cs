using System;
using System.Buffers.Binary;
using System.Text;

namespace CSVM.Net;

/// <summary>
/// The cursor every message serialiser writes through: little-endian primitives over the
/// caller's buffer. It opens with the four-byte header and closes with the total length patched
/// back into it. Hand-packed on purpose, so no field crosses the wire by reflection and every
/// layout stays readable as the byte table it is. Only <see cref="WriteText"/> allocates, and no
/// per-frame message carries text. <see cref="NetMessageReader"/> is the exact inverse, and the
/// two are maintained as one pair.</summary>
public ref struct NetMessageWriter
{
    /// <summary>What a unit-range field is multiplied by before it is stored as a 16-bit integer.
    /// Chosen so -1 and +1 survive the round trip exactly and the step stays under 1e-4.</summary>
    public const short UnitScale = 32767;

    private readonly Span<byte> _buffer;
    private int _at;

    /// <summary>Opens a message of <paramref name="type"/> in <paramref name="into"/>: writes the
    /// header and leaves the cursor on the first payload byte.</summary>
    public NetMessageWriter(Span<byte> into, NetMessageType type)
    {
        _buffer = into;
        BinaryPrimitives.WriteUInt16LittleEndian(into, (ushort)type);
        BinaryPrimitives.WriteUInt16LittleEndian(into[2..], 0);
        _at = NetMessage.HeaderBytes;
    }

    /// <summary>How many bytes stand written, the header included.</summary>
    public readonly int Position => _at;

    /// <summary>Stores the total length in the header and reports it, which is what a transport
    /// sends and what every serialiser returns.</summary>
    public int Close()
    {
        BinaryPrimitives.WriteUInt16LittleEndian(_buffer[2..], (ushort)_at);
        return _at;
    }

    /// <summary>Appends one unsigned byte.</summary>
    public void WriteByte(byte value) => _buffer[_at++] = value;

    /// <summary>Appends one signed byte.</summary>
    public void WriteSByte(sbyte value) => _buffer[_at++] = unchecked((byte)value);

    /// <summary>Appends one unsigned 16-bit field.</summary>
    public void WriteUInt16(ushort value)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(_buffer[_at..], value);
        _at += 2;
    }

    /// <summary>Appends one signed 16-bit field.</summary>
    public void WriteInt16(short value)
    {
        BinaryPrimitives.WriteInt16LittleEndian(_buffer[_at..], value);
        _at += 2;
    }

    /// <summary>Appends one unsigned 32-bit field.</summary>
    public void WriteUInt32(uint value)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(_buffer[_at..], value);
        _at += 4;
    }

    /// <summary>Appends one signed 32-bit field.</summary>
    public void WriteInt32(int value)
    {
        BinaryPrimitives.WriteInt32LittleEndian(_buffer[_at..], value);
        _at += 4;
    }

    /// <summary>Appends one 32-bit float.</summary>
    public void WriteSingle(float value)
    {
        BinaryPrimitives.WriteSingleLittleEndian(_buffer[_at..], value);
        _at += 4;
    }

    /// <summary>Appends a value known to lie in [-1, 1] as a 16-bit integer, clamped first so a
    /// caller's overshoot cannot wrap into the opposite sign.</summary>
    public void WriteUnit(float value) =>
        WriteInt16((short)Math.Round(Math.Clamp(value, -1f, 1f) * UnitScale));

    /// <summary>Appends a <paramref name="bytes"/>-wide UTF-8 field, zero padded, so a message's
    /// width never depends on what a player typed. The last byte stays zero and the encoder
    /// stops on a whole character, so an over-long name truncates instead of ending
    /// half-encoded.</summary>
    public void WriteText(string? value, int bytes)
    {
        var field = _buffer.Slice(_at, bytes);
        field.Clear();
        if (!string.IsNullOrEmpty(value))
        {
            Encoding.UTF8.GetEncoder().Convert(
                value.AsSpan(), field[..(bytes - 1)], true, out _, out _, out _);
        }

        _at += bytes;
    }
}

/// <summary>
/// The cursor every message deserialiser reads through, the inverse of
/// <see cref="NetMessageWriter"/>. It reads the header in its constructor, so the type and the
/// declared length answer before any payload byte is touched. Validity is the same check: does
/// the buffer really hold the message its header claims? A deserialiser asks once, with the
/// expected size, and then reads fields without further bounds talk.</summary>
public ref struct NetMessageReader
{
    private readonly ReadOnlySpan<byte> _buffer;
    private int _at;

    /// <summary>Opens <paramref name="from"/> as a message, reading its header.</summary>
    public NetMessageReader(ReadOnlySpan<byte> from)
    {
        _buffer = from;
        Valid = from.Length >= NetMessage.HeaderBytes;
        Type = Valid ? (NetMessageType)BinaryPrimitives.ReadUInt16LittleEndian(from) : default;
        Length = Valid ? BinaryPrimitives.ReadUInt16LittleEndian(from[2..]) : 0;
        Valid = Valid && Length >= NetMessage.HeaderBytes && Length <= from.Length;
        _at = NetMessage.HeaderBytes;
    }

    /// <summary>The type word the header carries.</summary>
    public NetMessageType Type { get; }

    /// <summary>The total length the header declares, the header included.</summary>
    public int Length { get; }

    /// <summary>Whether the buffer holds a header and at least as many bytes as it declares.</summary>
    public bool Valid { get; private set; }

    /// <summary>How many bytes stand read, the header included.</summary>
    public readonly int Position => _at;

    /// <summary>Whether the declared length is exactly <paramref name="size"/>, which is the one
    /// check a fixed-size message's deserialiser makes.</summary>
    public readonly bool Is(int size) => Valid && Length == size;

    /// <summary>Reads one unsigned byte.</summary>
    public byte ReadByte() => _buffer[_at++];

    /// <summary>Reads one signed byte.</summary>
    public sbyte ReadSByte() => unchecked((sbyte)_buffer[_at++]);

    /// <summary>Reads one unsigned 16-bit field.</summary>
    public ushort ReadUInt16()
    {
        ushort value = BinaryPrimitives.ReadUInt16LittleEndian(_buffer[_at..]);
        _at += 2;
        return value;
    }

    /// <summary>Reads one signed 16-bit field.</summary>
    public short ReadInt16()
    {
        short value = BinaryPrimitives.ReadInt16LittleEndian(_buffer[_at..]);
        _at += 2;
        return value;
    }

    /// <summary>Reads one unsigned 32-bit field.</summary>
    public uint ReadUInt32()
    {
        uint value = BinaryPrimitives.ReadUInt32LittleEndian(_buffer[_at..]);
        _at += 4;
        return value;
    }

    /// <summary>Reads one signed 32-bit field.</summary>
    public int ReadInt32()
    {
        int value = BinaryPrimitives.ReadInt32LittleEndian(_buffer[_at..]);
        _at += 4;
        return value;
    }

    /// <summary>Reads one 32-bit float.</summary>
    public float ReadSingle()
    {
        float value = BinaryPrimitives.ReadSingleLittleEndian(_buffer[_at..]);
        _at += 4;
        return value;
    }

    /// <summary>Reads a unit-range field back off its 16-bit integer.</summary>
    public float ReadUnit() => ReadInt16() / (float)NetMessageWriter.UnitScale;

    /// <summary>Reads a fixed-width UTF-8 field, stopping at the first zero byte. Decoded on the
    /// stack, so the one allocation is the string, however malformed the bytes.</summary>
    public string ReadText(int bytes)
    {
        var field = _buffer.Slice(_at, bytes);
        _at += bytes;
        int end = field.IndexOf((byte)0);
        var text = end < 0 ? field : field[..end];
        Span<char> chars = stackalloc char[text.Length];
        int count = Encoding.UTF8.GetChars(text, chars);
        return new string(chars[..count]);
    }
}
