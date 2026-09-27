using System;
using System.Collections.Generic;

namespace CSVM.Net;

/// <summary>
/// A co-op host's boards as one guest follows them. It names the screen and mission, the round of
/// picks under way and which humans are Ready. Once a mission ends it carries the result. The host
/// sends one to each guest on every change, since the slot differs per guest. Like the advert it
/// stays in the lobby and never reaches a session. Reliable, since each is a whole state.</summary>
public readonly record struct CoopFlowMessage(
    NetCoopScreen Screen, byte MissionSeq, byte Epoch, byte Slot,
    byte ReadyMask, byte Humans, byte Progress, bool Won,
    ushort Airframes, int Objectives, int Cash, byte Locals = 1)
    : INetMessage<CoopFlowMessage>
{
    /// <summary>The fixed width of the message, header included.</summary>
    public const int Size = 24;

    /// <inheritdoc/>
    public static NetMessageType Type => NetMessageType.CoopFlow;

    /// <inheritdoc/>
    public static NetReliability Reliability => NetReliability.Reliable;

    /// <summary>Whether the human at player number <paramref name="slot"/> is Ready.</summary>
    public bool IsReady(int slot) => slot is >= 0 and < 8 && (ReadyMask & (1 << slot)) != 0;

    /// <summary>Whether the host's hangar holds airframe <paramref name="airframe"/>.</summary>
    public bool Offers(int airframe) => airframe is >= 0 and < 16 && (Airframes & (1 << airframe)) != 0;

    /// <inheritdoc/>
    public static bool TryRead(ReadOnlySpan<byte> from, out CoopFlowMessage message)
    {
        message = default;
        var reader = new NetMessageReader(from);
        if (!reader.Is(Size) || reader.Type != Type)
            return false;

        byte screen = reader.ReadByte();
        byte seq = reader.ReadByte();
        byte epoch = reader.ReadByte();
        byte slot = reader.ReadByte();
        byte ready = reader.ReadByte();
        byte humans = reader.ReadByte();
        byte progress = reader.ReadByte();
        byte flags = reader.ReadByte();
        ushort airframes = reader.ReadUInt16();
        byte locals = reader.ReadByte();
        _ = reader.ReadByte();
        int objectives = reader.ReadInt32();
        int cash = reader.ReadInt32();
        var known = screen is >= (byte)NetCoopScreen.Cabin and <= (byte)NetCoopScreen.Debrief
            ? (NetCoopScreen)screen
            : NetCoopScreen.Unknown;
        message = new CoopFlowMessage(known, seq, epoch, slot, ready, humans, progress,
            (flags & 1) != 0, airframes, objectives, cash, locals);
        return true;
    }

    /// <inheritdoc/>
    public int Write(Span<byte> into)
    {
        var writer = new NetMessageWriter(into, Type);
        writer.WriteByte((byte)Screen);
        writer.WriteByte(MissionSeq);
        writer.WriteByte(Epoch);
        writer.WriteByte(Slot);
        writer.WriteByte(ReadyMask);
        writer.WriteByte(Humans);
        writer.WriteByte(Progress);
        writer.WriteByte((byte)(Won ? 1 : 0));
        writer.WriteUInt16(Airframes);
        writer.WriteByte(Locals);
        writer.WriteByte(0);
        writer.WriteInt32(Objectives);
        writer.WriteInt32(Cash);
        return writer.Close();
    }
}

/// <summary>
/// One aeroplane's ammunition and ordnance picks in the campaign profile's own stored encoding. A
/// byte per gun slot holds the stored ammunition plus one, so 0 is unset. A byte per wing cell
/// holds the stored ordnance value, 0 unset. Twelve bytes on the wire, and all zero is the stock
/// fit. Nothing here knows a weapon id; the session decodes the values against its stock table.
/// </summary>
public readonly record struct CoopFit(uint Ammo, ulong Ordnance)
{
    /// <summary>The bytes a fit takes on the wire.</summary>
    public const int Bytes = GunSlots + Cells;

    /// <summary>How many gun slots a fit carries, the loadout's own ceiling.</summary>
    public const int GunSlots = 4;

    /// <summary>How many wing cells a fit carries, four a wing.</summary>
    public const int Cells = 8;

    /// <summary>Whether nothing is picked, so the aeroplane flies its stock fit.</summary>
    public bool IsStock => Ammo == 0 && Ordnance == 0;

    /// <summary>The stored ammunition for zero-based gun slot <paramref name="slot"/>, or -1 when
    /// unset or out of range.</summary>
    public int AmmoAt(int slot) =>
        slot is >= 0 and < GunSlots ? (int)((Ammo >> (8 * slot)) & 0xFF) - 1 : -1;

    /// <summary>The stored ordnance for wing cell <paramref name="cell"/>, or 0 when unset or out
    /// of range.</summary>
    public int OrdnanceAt(int cell) =>
        cell is >= 0 and < Cells ? (int)((Ordnance >> (8 * cell)) & 0xFF) : 0;

    /// <summary>The fit a profile's stored <paramref name="ammo"/> and <paramref name="ordnance"/>
    /// lists name. A value outside a byte is dropped as unset.</summary>
    public static CoopFit Of(IReadOnlyList<int>? ammo, IReadOnlyList<int>? ordnance)
    {
        uint packedAmmo = 0;
        for (int slot = 0; ammo != null && slot < GunSlots && slot < ammo.Count; slot++)
        {
            if (ammo[slot] is >= 0 and < byte.MaxValue)
            {
                packedAmmo |= (uint)(ammo[slot] + 1) << (8 * slot);
            }
        }

        ulong packedOrdnance = 0;
        for (int cell = 0; ordnance != null && cell < Cells && cell < ordnance.Count; cell++)
        {
            if (ordnance[cell] is > 0 and <= byte.MaxValue)
            {
                packedOrdnance |= (ulong)ordnance[cell] << (8 * cell);
            }
        }

        return new CoopFit(packedAmmo, packedOrdnance);
    }

    internal static CoopFit Read(ref NetMessageReader reader)
    {
        uint ammo = 0;
        for (int slot = 0; slot < GunSlots; slot++)
        {
            ammo |= (uint)reader.ReadByte() << (8 * slot);
        }

        ulong ordnance = 0;
        for (int cell = 0; cell < Cells; cell++)
        {
            ordnance |= (ulong)reader.ReadByte() << (8 * cell);
        }

        return new CoopFit(ammo, ordnance);
    }

    internal void Write(ref NetMessageWriter writer)
    {
        for (int slot = 0; slot < GunSlots; slot++)
        {
            writer.WriteByte((byte)(Ammo >> (8 * slot)));
        }

        for (int cell = 0; cell < Cells; cell++)
        {
            writer.WriteByte((byte)(Ordnance >> (8 * cell)));
        }
    }
}

/// <summary>
/// A co-op guest's answer on its flight check, under the round it answers. It names the airframe
/// picked from the host's hangar, its fit, the guest's player name and whether it is Ready. A host
/// counts a pick only under its own current round. A Ready from before a mission change therefore
/// never launches the next one. <see cref="Left"/> says the guest walked out of the flight under way.
/// Kept in the lobby, so a flight's end cannot carry it into the next session.
/// </summary>
public readonly record struct CoopPickMessage(
    byte Epoch, bool Ready, byte Airframe, CoopFit Fit = default, string Name = "", bool Left = false)
    : INetMessage<CoopPickMessage>
{
    /// <summary>The fixed width of the message, header included.</summary>
    public const int Size = NetMessage.HeaderBytes + 4 + CoopFit.Bytes + NameBytes;

    /// <summary>How many bytes the player name takes, the roster's own callsign width.</summary>
    public const int NameBytes = SeatRosterMessage.CallsignBytes;

    /// <inheritdoc/>
    public static NetMessageType Type => NetMessageType.CoopPick;

    /// <inheritdoc/>
    public static NetReliability Reliability => NetReliability.Reliable;

    /// <inheritdoc/>
    public static bool TryRead(ReadOnlySpan<byte> from, out CoopPickMessage message)
    {
        message = default;
        var reader = new NetMessageReader(from);
        if (!reader.Is(Size) || reader.Type != Type)
            return false;

        byte epoch = reader.ReadByte();
        byte flags = reader.ReadByte();
        byte airframe = reader.ReadByte();
        _ = reader.ReadByte();
        var fit = CoopFit.Read(ref reader);
        string name = reader.ReadText(NameBytes);
        message = new CoopPickMessage(epoch, (flags & 1) != 0, airframe, fit, name, (flags & 2) != 0);
        return true;
    }

    /// <inheritdoc/>
    public int Write(Span<byte> into)
    {
        var writer = new NetMessageWriter(into, Type);
        writer.WriteByte(Epoch);
        writer.WriteByte((byte)((Ready ? 1 : 0) | (Left ? 2 : 0)));
        writer.WriteByte(Airframe);
        writer.WriteByte(0);
        Fit.Write(ref writer);
        writer.WriteText(Name, NameBytes);
        return writer.Close();
    }
}

/// <summary>
/// One seat's fit as a co-op host launched it, sent to every guest for every seat before the
/// session's opener. Every machine then builds each aeroplane with the ammunition and ordnance its
/// pilot chose, rather than a remote seat flying its stock fit. Kept in the lobby by seat.
/// </summary>
public readonly record struct CoopSeatFitMessage(byte Seat, CoopFit Fit)
    : INetMessage<CoopSeatFitMessage>
{
    /// <summary>The fixed width of the message, header included.</summary>
    public const int Size = NetMessage.HeaderBytes + 4 + CoopFit.Bytes;

    /// <inheritdoc/>
    public static NetMessageType Type => NetMessageType.CoopSeatFit;

    /// <inheritdoc/>
    public static NetReliability Reliability => NetReliability.Reliable;

    /// <inheritdoc/>
    public static bool TryRead(ReadOnlySpan<byte> from, out CoopSeatFitMessage message)
    {
        message = default;
        var reader = new NetMessageReader(from);
        if (!reader.Is(Size) || reader.Type != Type)
            return false;

        byte seat = reader.ReadByte();
        _ = reader.ReadByte();
        _ = reader.ReadUInt16();
        message = new CoopSeatFitMessage(seat, CoopFit.Read(ref reader));
        return true;
    }

    /// <inheritdoc/>
    public int Write(Span<byte> into)
    {
        var writer = new NetMessageWriter(into, Type);
        writer.WriteByte(Seat);
        writer.WriteByte(0);
        writer.WriteUInt16(0);
        Fit.Write(ref writer);
        return writer.Close();
    }
}
