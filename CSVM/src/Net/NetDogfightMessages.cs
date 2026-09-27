using System;
using System.Collections.Generic;

namespace CSVM.Net;

/// <summary>How a Dogfight lobby's match is won: by the clock or by a score.</summary>
public enum DogfightVictory : byte
{
    /// <summary>The match runs for a set number of minutes.</summary>
    Time = 0,

    /// <summary>The match ends when a pilot reaches a set score.</summary>
    Score = 1,
}

/// <summary>
/// A Dogfight host's Mission Options as one guest reads them, under the round they belong to. They
/// are the environment, the mission type, the victory condition and the lives rule. The host sends one to
/// each guest whenever an option moves. Kept in the lobby and never passed to a session.
/// </summary>
public readonly record struct DogfightOptionsMessage(
    byte Epoch, byte Environment, byte MissionType, DogfightVictory Victory, byte TimeMinutes,
    ushort Score, bool LimitedLives, byte Lives, bool AutoRespawn)
    : INetMessage<DogfightOptionsMessage>
{
    /// <summary>The fixed width of the message, header included.</summary>
    public const int Size = NetMessage.HeaderBytes + 8;

    /// <inheritdoc/>
    public static NetMessageType Type => NetMessageType.DogfightOptions;

    /// <inheritdoc/>
    public static NetReliability Reliability => NetReliability.Reliable;

    /// <inheritdoc/>
    public static bool TryRead(ReadOnlySpan<byte> from, out DogfightOptionsMessage message)
    {
        message = default;
        var reader = new NetMessageReader(from);
        if (!reader.Is(Size) || reader.Type != Type)
            return false;

        byte epoch = reader.ReadByte();
        byte environment = reader.ReadByte();
        byte type = reader.ReadByte();
        byte flags = reader.ReadByte();
        byte minutes = reader.ReadByte();
        byte lives = reader.ReadByte();
        ushort score = reader.ReadUInt16();
        var victory = (flags & 1) != 0 ? DogfightVictory.Score : DogfightVictory.Time;
        message = new DogfightOptionsMessage(epoch, environment, type, victory, minutes, score,
            (flags & 2) != 0, lives, (flags & 4) != 0);
        return true;
    }

    /// <inheritdoc/>
    public int Write(Span<byte> into)
    {
        var writer = new NetMessageWriter(into, Type);
        writer.WriteByte(Epoch);
        writer.WriteByte(Environment);
        writer.WriteByte(MissionType);
        int flags = (Victory == DogfightVictory.Score ? 1 : 0) | (LimitedLives ? 2 : 0) | (AutoRespawn ? 4 : 0);
        writer.WriteByte((byte)flags);
        writer.WriteByte(TimeMinutes);
        writer.WriteByte(Lives);
        writer.WriteUInt16(Score);
        return writer.Close();
    }
}

/// <summary>One row of a Dogfight lobby's player list. It carries the pilot's name, its stock
/// airframe, its Ready and whether it is the host.</summary>
public readonly record struct DogfightLobbySeat(string Name, byte Airframe, bool Ready, bool IsHost);

/// <summary>
/// A Dogfight host's player list as one guest reads it, under the round it belongs to. The host
/// sends the whole list to each guest whenever it changes, with that guest's own row marked. Fixed
/// width, since a lobby list is short and sent rarely. Kept in the lobby.
/// </summary>
public readonly struct DogfightRosterMessage : INetMessage<DogfightRosterMessage>, IEquatable<DogfightRosterMessage>
{
    /// <summary>The most rows the list carries, the widest field a Dogfight seats.</summary>
    public const int MaxRows = NetSeats.MaxPlayers;

    /// <summary>The bytes one row takes.</summary>
    public const int RowSize = 4 + NameBytes;

    /// <summary>How many name bytes a row carries, the roster's own callsign width.</summary>
    public const int NameBytes = SeatRosterMessage.CallsignBytes;

    /// <summary>The fixed width of the message, header included.</summary>
    public const int Size = NetMessage.HeaderBytes + 4 + (RowSize * MaxRows);

    private readonly DogfightLobbySeat[] _rows;

    /// <summary>The list <paramref name="rows"/> under round <paramref name="epoch"/>, read by the
    /// guest at row <paramref name="you"/>.</summary>
    public DogfightRosterMessage(byte epoch, byte you, IReadOnlyList<DogfightLobbySeat> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        Epoch = epoch;
        You = you;
        int count = Math.Min(rows.Count, MaxRows);
        _rows = new DogfightLobbySeat[count];
        for (int i = 0; i < count; i++)
            _rows[i] = rows[i];
    }

    // The reader's own array, taken as it stands rather than copied a second time.
    private DogfightRosterMessage(DogfightLobbySeat[] rows, byte epoch, byte you)
    {
        Epoch = epoch;
        You = you;
        _rows = rows;
    }

    /// <inheritdoc/>
    public static NetMessageType Type => NetMessageType.DogfightRoster;

    /// <inheritdoc/>
    public static NetReliability Reliability => NetReliability.Reliable;

    /// <summary>The round the list's Ready marks belong to.</summary>
    public byte Epoch { get; }

    /// <summary>The row that is the reading guest's own.</summary>
    public byte You { get; }

    /// <summary>The rows in player order, the host first.</summary>
    public IReadOnlyList<DogfightLobbySeat> Rows => _rows ?? Array.Empty<DogfightLobbySeat>();

    /// <summary>Whether two lists name the same rows under the same round.</summary>
    public static bool operator ==(DogfightRosterMessage left, DogfightRosterMessage right) => left.Equals(right);

    /// <summary>Whether two lists differ.</summary>
    public static bool operator !=(DogfightRosterMessage left, DogfightRosterMessage right) => !left.Equals(right);

    /// <inheritdoc/>
    public static bool TryRead(ReadOnlySpan<byte> from, out DogfightRosterMessage message)
    {
        message = default;
        var reader = new NetMessageReader(from);
        if (!reader.Is(Size) || reader.Type != Type)
            return false;

        byte epoch = reader.ReadByte();
        int count = Math.Min((int)reader.ReadByte(), MaxRows);
        byte you = reader.ReadByte();
        _ = reader.ReadByte();
        var rows = new DogfightLobbySeat[count];
        for (int i = 0; i < count; i++)
        {
            byte flags = reader.ReadByte();
            byte airframe = reader.ReadByte();
            _ = reader.ReadUInt16();
            rows[i] = new DogfightLobbySeat(reader.ReadText(NameBytes), airframe, (flags & 1) != 0, (flags & 2) != 0);
        }

        message = new DogfightRosterMessage(rows, epoch, you);
        return true;
    }

    /// <inheritdoc/>
    public int Write(Span<byte> into)
    {
        var rows = Rows;
        var writer = new NetMessageWriter(into, Type);
        writer.WriteByte(Epoch);
        writer.WriteByte((byte)rows.Count);
        writer.WriteByte(You);
        writer.WriteByte(0);
        for (int i = 0; i < MaxRows; i++)
        {
            var row = i < rows.Count ? rows[i] : default;
            writer.WriteByte((byte)((row.Ready ? 1 : 0) | (row.IsHost ? 2 : 0)));
            writer.WriteByte(row.Airframe);
            writer.WriteUInt16(0);
            writer.WriteText(row.Name ?? "", NameBytes);
        }

        return writer.Close();
    }

    /// <inheritdoc/>
    public bool Equals(DogfightRosterMessage other)
    {
        if (Epoch != other.Epoch || You != other.You || Rows.Count != other.Rows.Count)
            return false;

        for (int i = 0; i < Rows.Count; i++)
        {
            if (Rows[i] != other.Rows[i])
                return false;
        }

        return true;
    }

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is DogfightRosterMessage other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(Epoch, You, Rows.Count);
}

/// <summary>
/// One line of lobby chat and the name of the pilot who typed it. A guest sends its line to the
/// host, and the host relays it to every other guest under the same name. Kept in the lobby, so a
/// line never reaches a session.
/// </summary>
public readonly record struct LobbyChatMessage(string Name, string Text) : INetMessage<LobbyChatMessage>
{
    /// <summary>The longest line a player may type, the original's own edit box width.</summary>
    public const int MaxChars = 80;

    /// <summary>How many name bytes the message carries.</summary>
    public const int NameBytes = SeatRosterMessage.CallsignBytes;

    /// <summary>How many text bytes the message carries, room for a typed line in UTF-8.</summary>
    public const int TextBytes = 84;

    /// <summary>The fixed width of the message, header included.</summary>
    public const int Size = NetMessage.HeaderBytes + NameBytes + TextBytes;

    /// <inheritdoc/>
    public static NetMessageType Type => NetMessageType.LobbyChat;

    /// <inheritdoc/>
    public static NetReliability Reliability => NetReliability.Reliable;

    /// <inheritdoc/>
    public static bool TryRead(ReadOnlySpan<byte> from, out LobbyChatMessage message)
    {
        message = default;
        var reader = new NetMessageReader(from);
        if (!reader.Is(Size) || reader.Type != Type)
            return false;

        string name = reader.ReadText(NameBytes);
        message = new LobbyChatMessage(name, reader.ReadText(TextBytes));
        return true;
    }

    /// <inheritdoc/>
    public int Write(Span<byte> into)
    {
        var writer = new NetMessageWriter(into, Type);
        writer.WriteText(Name ?? "", NameBytes);
        writer.WriteText(Text ?? "", TextBytes);
        return writer.Close();
    }
}
