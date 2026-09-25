using System;

namespace CSVM.Net;

/// <summary>
/// One positional start as the host decided it, or a guest's button the host decides one from.
/// The kind is a <see cref="NetPositionalStart"/>, whose members say what the seat, the row and
/// the held flag carry. Reliable and ordered, because a row start and the holder that follows it
/// must replay in the order the host decided them.</summary>
public readonly record struct PositionalStartMessage(NetPositionalStart Kind, byte Seat, int Row, bool Held)
    : INetMessage<PositionalStartMessage>
{
    /// <summary>The fixed width of the message, header included.</summary>
    public const int Size = 12;

    /// <inheritdoc/>
    public static NetMessageType Type => NetMessageType.PositionalStart;

    /// <inheritdoc/>
    public static NetReliability Reliability => NetReliability.Reliable;

    /// <inheritdoc/>
    public static bool TryRead(ReadOnlySpan<byte> from, out PositionalStartMessage message)
    {
        message = default;
        var reader = new NetMessageReader(from);
        if (!reader.Is(Size) || reader.Type != Type)
            return false;

        var kind = (NetPositionalStart)reader.ReadByte();
        byte seat = reader.ReadByte();
        byte flags = reader.ReadByte();
        _ = reader.ReadByte();
        int row = reader.ReadInt32();
        message = new PositionalStartMessage(kind, seat, row, (flags & 1) != 0);
        return true;
    }

    /// <inheritdoc/>
    public int Write(Span<byte> into)
    {
        var writer = new NetMessageWriter(into, Type);
        writer.WriteByte((byte)Kind);
        writer.WriteByte(Seat);
        writer.WriteByte((byte)(Held ? 1 : 0));
        writer.WriteByte(0);
        writer.WriteInt32(Row);
        return writer.Close();
    }
}
