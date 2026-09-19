using System;
using System.Collections.Generic;
using Godot;

namespace CSVM.Net;

/// <summary>Every type word that can cross the wire. The ids at or below
/// <see cref="NetMessage.OriginalIdCeiling"/> are the original's own, the ones above this
/// remake's. What the original puts in each is <c>docs/org/multiplayer-messages.md</c>.</summary>
public enum NetMessageType : ushort
{
    /// <summary>One aircraft's pose, motion and control state.</summary>
    AircraftState = 0x000F,

    /// <summary>One weapon discharge, with where it left the aircraft and where it was aimed.</summary>
    Fire = 0x0010,

    /// <summary>A pilot's report of its own death, with the killer and the cause.</summary>
    Death = 0x0012,

    /// <summary>One seat's score line, as the host has it.</summary>
    Score = 0x0013,

    /// <summary>The match clock, its limits and its ending.</summary>
    MatchState = 0x0017,

    /// <summary>A shooter's claim that a round of its landed on a victim.</summary>
    Hit = 0x0022,

    /// <summary>The whole seat roster and the match seed.</summary>
    SeatRoster = 0x0027,

    /// <summary>A victim's hull state after it applied damage.</summary>
    Damage = 0x0040,

    /// <summary>Where the host has placed a seat, by spawn entry.</summary>
    Spawn = 0x0041,

    /// <summary>One mission-director transition, as a code and an id.</summary>
    DirectorTransition = 0x0042,

    /// <summary>What the host answers a joining peer with: the match seed, its clock and the seat
    /// it handed out.</summary>
    Handshake = 0x0043,
}

/// <summary>Why a pilot died, the original's own cause word
/// (<c>docs/org/multiplayer-scoring.md</c>).</summary>
public enum NetDeathCause : ushort
{
    /// <summary>Another pilot is credited.</summary>
    Killer = 1,

    /// <summary>No killer at all, which the scoring charges to the pilot who died.</summary>
    Suicide = 2,

    /// <summary>A zeppelin part, whose owner is credited.</summary>
    ZeppelinPart = 3,

    /// <summary>A turret, whose owner is credited.</summary>
    TurretOwner = 4,
}

/// <summary>Which of the two placements a spawn event is.</summary>
public enum NetSpawnKind : byte
{
    /// <summary>The opening placement, off the mission's spawn table.</summary>
    Opening = 0,

    /// <summary>A return to the fight, off the rotation.</summary>
    Respawn = 1,
}

/// <summary>Why a match stopped, the original's own end reason
/// (<c>docs/org/multiplayer-scoring.md</c>).</summary>
public enum NetMatchEnd : byte
{
    /// <summary>Still running.</summary>
    Running = 0,

    /// <summary>The clock ran out. There is no overtime and no tiebreak.</summary>
    TimeLimit = 1,

    /// <summary>A pilot reached the score target.</summary>
    ScoreTarget = 2,

    /// <summary>A mode-specific objective ended it.</summary>
    Objective = 3,

    /// <summary>No two live pilots on different sides are left.</summary>
    NobodyLeft = 4,
}

/// <summary>
/// What every message implements: a serialiser onto a caller's buffer and a deserialiser off
/// one. Its type word and reliability class are static abstracts, so a sender reads the class
/// off the type without constructing one. Nothing here reflects.</summary>
/// <typeparam name="TSelf">The implementing message.</typeparam>
public interface INetMessage<TSelf>
    where TSelf : struct, INetMessage<TSelf>
{
    /// <summary>The type word this message is sent under.</summary>
    static abstract NetMessageType Type { get; }

    /// <summary>What the transport must promise it.</summary>
    static abstract NetReliability Reliability { get; }

    /// <summary>Reads one message out of <paramref name="from"/>, false when the buffer does not
    /// hold exactly this message.</summary>
    static abstract bool TryRead(ReadOnlySpan<byte> from, out TSelf message);

    /// <summary>Writes this message into <paramref name="into"/> and reports the bytes used.</summary>
    int Write(Span<byte> into);
}

/// <summary>One seat as the roster carries it. The callsign is a fixed-width UTF-8 field, so a
/// roster's size depends only on how many seats there are.</summary>
public readonly record struct NetSeatEntry(
    byte Seat, byte Team, byte Plane, bool IsHost, string Callsign);

/// <summary>
/// One aircraft's state as its owner has it: pose, motion, the lever and the surfaces. It is the
/// only unreliable payload carrying a pose, so it is the one whose width is budgeted. Every field
/// is quantised to the smallest form that still reads right at the controls, and
/// <see cref="NetMessage.AircraftStateBudget"/> is the ceiling a layout change may not pass.
/// The sequence number wraps; a sample older than the newest seen is dropped, not applied.
/// </summary>
public readonly record struct AircraftStateMessage(
    byte Seat,
    ushort Sequence,
    Vector3 Position,
    Quaternion Attitude,
    Vector3 Velocity,
    float Throttle,
    float Aileron,
    float Elevator,
    float Rudder,
    bool Nitro) : INetMessage<AircraftStateMessage>
{
    /// <summary>The fixed width of the message, header included.</summary>
    public const int Size = 44;

    /// <inheritdoc/>
    public static NetMessageType Type => NetMessageType.AircraftState;

    /// <inheritdoc/>
    public static NetReliability Reliability => NetReliability.UnreliableSequenced;

    /// <inheritdoc/>
    public static bool TryRead(ReadOnlySpan<byte> from, out AircraftStateMessage message)
    {
        message = default;
        var reader = new NetMessageReader(from);
        if (!reader.Is(Size) || reader.Type != Type)
            return false;

        byte seat = reader.ReadByte();
        byte flags = reader.ReadByte();
        ushort sequence = reader.ReadUInt16();
        var position = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
        var attitude = new Quaternion(
            reader.ReadUnit(), reader.ReadUnit(), reader.ReadUnit(), reader.ReadUnit());
        var velocity = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
        float throttle = reader.ReadByte() / 255f;
        float aileron = reader.ReadSByte() / 127f;
        float elevator = reader.ReadSByte() / 127f;
        float rudder = reader.ReadSByte() / 127f;
        message = new AircraftStateMessage(
            seat, sequence, position, attitude, velocity,
            throttle, aileron, elevator, rudder, (flags & 1) != 0);
        return true;
    }

    /// <inheritdoc/>
    public int Write(Span<byte> into)
    {
        var writer = new NetMessageWriter(into, Type);
        writer.WriteByte(Seat);
        writer.WriteByte((byte)(Nitro ? 1 : 0));
        writer.WriteUInt16(Sequence);
        writer.WriteSingle(Position.X);
        writer.WriteSingle(Position.Y);
        writer.WriteSingle(Position.Z);
        writer.WriteUnit(Attitude.X);
        writer.WriteUnit(Attitude.Y);
        writer.WriteUnit(Attitude.Z);
        writer.WriteUnit(Attitude.W);
        writer.WriteSingle(Velocity.X);
        writer.WriteSingle(Velocity.Y);
        writer.WriteSingle(Velocity.Z);
        writer.WriteByte((byte)Math.Round(Math.Clamp(Throttle, 0f, 1f) * 255f));
        writer.WriteSByte((sbyte)Math.Round(Math.Clamp(Aileron, -1f, 1f) * 127f));
        writer.WriteSByte((sbyte)Math.Round(Math.Clamp(Elevator, -1f, 1f) * 127f));
        writer.WriteSByte((sbyte)Math.Round(Math.Clamp(Rudder, -1f, 1f) * 127f));
        return writer.Close();
    }
}

/// <summary>
/// One weapon discharge, with where the round left the aircraft and where it was aimed.
/// Unreliable like the state it rides beside, because every peer spawns the projectile locally
/// from this event and a lost burst is cosmetic. A target seat of
/// <see cref="NetMessage.NoSeat"/> means no lock.</summary>
public readonly record struct FireMessage(
    byte Seat,
    byte Weapon,
    ushort Sequence,
    Vector3 Origin,
    Vector3 Direction,
    byte TargetSeat) : INetMessage<FireMessage>
{
    /// <summary>The fixed width of the message, header included.</summary>
    public const int Size = 28;

    /// <inheritdoc/>
    public static NetMessageType Type => NetMessageType.Fire;

    /// <inheritdoc/>
    public static NetReliability Reliability => NetReliability.UnreliableSequenced;

    /// <inheritdoc/>
    public static bool TryRead(ReadOnlySpan<byte> from, out FireMessage message)
    {
        message = default;
        var reader = new NetMessageReader(from);
        if (!reader.Is(Size) || reader.Type != Type)
            return false;

        byte seat = reader.ReadByte();
        byte weapon = reader.ReadByte();
        ushort sequence = reader.ReadUInt16();
        var origin = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
        var direction = new Vector3(reader.ReadUnit(), reader.ReadUnit(), reader.ReadUnit());
        byte target = reader.ReadByte();
        message = new FireMessage(seat, weapon, sequence, origin, direction, target);
        return true;
    }

    /// <inheritdoc/>
    public int Write(Span<byte> into)
    {
        var writer = new NetMessageWriter(into, Type);
        writer.WriteByte(Seat);
        writer.WriteByte(Weapon);
        writer.WriteUInt16(Sequence);
        writer.WriteSingle(Origin.X);
        writer.WriteSingle(Origin.Y);
        writer.WriteSingle(Origin.Z);
        writer.WriteUnit(Direction.X);
        writer.WriteUnit(Direction.Y);
        writer.WriteUnit(Direction.Z);
        writer.WriteByte(TargetSeat);
        writer.WriteByte(0);
        return writer.Close();
    }
}

/// <summary>
/// The shooter's claim that one of its rounds landed. Reliable, because the victim's client is
/// the only place the damage is applied, and a lost claim is a hit that never happened. The
/// weapon is an index into the shared weapon catalogue. The <see cref="Damage"/> field is the
/// share of that weapon's authored pair the round carries, 1 for a direct strike and the blast
/// falloff otherwise. The struck collision shape is <see cref="Part"/>, or -1 for a shapeless one.
/// The <see cref="LocalImpact"/> point is in the victim's own body space, so the victim resolves
/// the same zone however far it has flown since.</summary>
public readonly record struct HitMessage(
    byte VictimSeat, byte ShooterSeat, ushort Weapon, float Damage, short Part, Vector3 LocalImpact)
    : INetMessage<HitMessage>
{
    /// <summary>The fixed width of the message, header included.</summary>
    public const int Size = 28;

    /// <inheritdoc/>
    public static NetMessageType Type => NetMessageType.Hit;

    /// <inheritdoc/>
    public static NetReliability Reliability => NetReliability.Reliable;

    /// <inheritdoc/>
    public static bool TryRead(ReadOnlySpan<byte> from, out HitMessage message)
    {
        message = default;
        var reader = new NetMessageReader(from);
        if (!reader.Is(Size) || reader.Type != Type)
            return false;

        byte victim = reader.ReadByte();
        byte shooter = reader.ReadByte();
        ushort weapon = reader.ReadUInt16();
        short part = reader.ReadInt16();
        _ = reader.ReadUInt16();
        float damage = reader.ReadSingle();
        var impact = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
        message = new HitMessage(victim, shooter, weapon, damage, part, impact);
        return true;
    }

    /// <inheritdoc/>
    public int Write(Span<byte> into)
    {
        var writer = new NetMessageWriter(into, Type);
        writer.WriteByte(VictimSeat);
        writer.WriteByte(ShooterSeat);
        writer.WriteUInt16(Weapon);
        writer.WriteInt16(Part);
        writer.WriteUInt16(0);
        writer.WriteSingle(Damage);
        writer.WriteSingle(LocalImpact.X);
        writer.WriteSingle(LocalImpact.Y);
        writer.WriteSingle(LocalImpact.Z);
        return writer.Close();
    }
}

/// <summary>
/// The victim's own hull state once it has applied whatever hit it. Reliable, and the reason a
/// lost or reordered hit cannot leave two peers disagreeing about how hurt an aircraft is.
/// The owner's number is the number, and this is the owner saying it.</summary>
public readonly record struct DamageMessage(
    byte Seat, byte Stage, ushort Flags, float Hull) : INetMessage<DamageMessage>
{
    /// <summary>The fixed width of the message, header included.</summary>
    public const int Size = 12;

    /// <inheritdoc/>
    public static NetMessageType Type => NetMessageType.Damage;

    /// <inheritdoc/>
    public static NetReliability Reliability => NetReliability.Reliable;

    /// <inheritdoc/>
    public static bool TryRead(ReadOnlySpan<byte> from, out DamageMessage message)
    {
        message = default;
        var reader = new NetMessageReader(from);
        if (!reader.Is(Size) || reader.Type != Type)
            return false;

        message = new DamageMessage(
            reader.ReadByte(), reader.ReadByte(), reader.ReadUInt16(), reader.ReadSingle());
        return true;
    }

    /// <inheritdoc/>
    public int Write(Span<byte> into)
    {
        var writer = new NetMessageWriter(into, Type);
        writer.WriteByte(Seat);
        writer.WriteByte(Stage);
        writer.WriteUInt16(Flags);
        writer.WriteSingle(Hull);
        return writer.Close();
    }
}

/// <summary>
/// A pilot's report of its own death, in the original's shape: the killer beside the victim and
/// a cause word. Reliable, and sent by the dying pilot's own client, which is the authority
/// order <c>docs/org/multiplayer-scoring.md</c> decodes. The killer seat is
/// <see cref="NetMessage.NoSeat"/> when nobody is credited, and the source id names the turret
/// or zeppelin part behind causes 3 and 4.</summary>
public readonly record struct DeathMessage(
    byte VictimSeat, byte KillerSeat, NetDeathCause Cause, uint SourceId)
    : INetMessage<DeathMessage>
{
    /// <summary>The fixed width of the message, header included.</summary>
    public const int Size = 12;

    /// <inheritdoc/>
    public static NetMessageType Type => NetMessageType.Death;

    /// <inheritdoc/>
    public static NetReliability Reliability => NetReliability.Reliable;

    /// <inheritdoc/>
    public static bool TryRead(ReadOnlySpan<byte> from, out DeathMessage message)
    {
        message = default;
        var reader = new NetMessageReader(from);
        if (!reader.Is(Size) || reader.Type != Type)
            return false;

        message = new DeathMessage(
            reader.ReadByte(), reader.ReadByte(),
            (NetDeathCause)reader.ReadUInt16(), reader.ReadUInt32());
        return true;
    }

    /// <inheritdoc/>
    public int Write(Span<byte> into)
    {
        var writer = new NetMessageWriter(into, Type);
        writer.WriteByte(VictimSeat);
        writer.WriteByte(KillerSeat);
        writer.WriteUInt16((ushort)Cause);
        writer.WriteUInt32(SourceId);
        return writer.Close();
    }
}

/// <summary>
/// Where the host has placed a seat, by entry into the mission's spawn table. Reliable, and the
/// only thing that places an aircraft in a match. A guest never runs the rotation itself,
/// because two rotations diverge on the first death.</summary>
public readonly record struct SpawnMessage(byte Seat, NetSpawnKind Kind, ushort EntryIndex)
    : INetMessage<SpawnMessage>
{
    /// <summary>The fixed width of the message, header included.</summary>
    public const int Size = 8;

    /// <inheritdoc/>
    public static NetMessageType Type => NetMessageType.Spawn;

    /// <inheritdoc/>
    public static NetReliability Reliability => NetReliability.Reliable;

    /// <inheritdoc/>
    public static bool TryRead(ReadOnlySpan<byte> from, out SpawnMessage message)
    {
        message = default;
        var reader = new NetMessageReader(from);
        if (!reader.Is(Size) || reader.Type != Type)
            return false;

        message = new SpawnMessage(
            reader.ReadByte(), (NetSpawnKind)reader.ReadByte(), reader.ReadUInt16());
        return true;
    }

    /// <inheritdoc/>
    public int Write(Span<byte> into)
    {
        var writer = new NetMessageWriter(into, Type);
        writer.WriteByte(Seat);
        writer.WriteByte((byte)Kind);
        writer.WriteUInt16(EntryIndex);
        return writer.Close();
    }
}

/// <summary>
/// One seat's score line as the host has it. The signed score is what the kill target is
/// compared against, so a penalty moves a seat away from winning. Kills and deaths ride along
/// as display counters only.</summary>
public readonly record struct ScoreMessage(byte Seat, short Score, ushort Kills, ushort Deaths)
    : INetMessage<ScoreMessage>
{
    /// <summary>The fixed width of the message, header included.</summary>
    public const int Size = 12;

    /// <inheritdoc/>
    public static NetMessageType Type => NetMessageType.Score;

    /// <inheritdoc/>
    public static NetReliability Reliability => NetReliability.Reliable;

    /// <inheritdoc/>
    public static bool TryRead(ReadOnlySpan<byte> from, out ScoreMessage message)
    {
        message = default;
        var reader = new NetMessageReader(from);
        if (!reader.Is(Size) || reader.Type != Type)
            return false;

        byte seat = reader.ReadByte();
        _ = reader.ReadByte();
        message = new ScoreMessage(
            seat, reader.ReadInt16(), reader.ReadUInt16(), reader.ReadUInt16());
        return true;
    }

    /// <inheritdoc/>
    public int Write(Span<byte> into)
    {
        var writer = new NetMessageWriter(into, Type);
        writer.WriteByte(Seat);
        writer.WriteByte(0);
        writer.WriteInt16(Score);
        writer.WriteUInt16(Kills);
        writer.WriteUInt16(Deaths);
        return writer.Close();
    }
}

/// <summary>
/// The match clock, its limits and its ending, written only by the host. A guest applies this
/// rather than advancing a clock of its own. That is what keeps two peers showing the same
/// remaining time and the same end.</summary>
public readonly record struct MatchStateMessage(
    float RemainingSeconds, float TimeLimitSeconds, short ScoreTarget, NetMatchEnd End)
    : INetMessage<MatchStateMessage>
{
    /// <summary>The fixed width of the message, header included.</summary>
    public const int Size = 16;

    /// <inheritdoc/>
    public static NetMessageType Type => NetMessageType.MatchState;

    /// <inheritdoc/>
    public static NetReliability Reliability => NetReliability.Reliable;

    /// <inheritdoc/>
    public static bool TryRead(ReadOnlySpan<byte> from, out MatchStateMessage message)
    {
        message = default;
        var reader = new NetMessageReader(from);
        if (!reader.Is(Size) || reader.Type != Type)
            return false;

        float remaining = reader.ReadSingle();
        float limit = reader.ReadSingle();
        short target = reader.ReadInt16();
        var end = (NetMatchEnd)reader.ReadByte();
        _ = reader.ReadByte();
        message = new MatchStateMessage(remaining, limit, target, end);
        return true;
    }

    /// <inheritdoc/>
    public int Write(Span<byte> into)
    {
        var writer = new NetMessageWriter(into, Type);
        writer.WriteSingle(RemainingSeconds);
        writer.WriteSingle(TimeLimitSeconds);
        writer.WriteInt16(ScoreTarget);
        writer.WriteByte((byte)End);
        writer.WriteByte(0);
        return writer.Close();
    }
}

/// <summary>
/// One mission-director transition, as a code and an id. Reliable, and deliberately opaque here.
/// The vocabulary fixes the envelope so the co-op work can settle what a code means without
/// touching the wire format again.</summary>
public readonly record struct DirectorTransitionMessage(ushort Code, int Id)
    : INetMessage<DirectorTransitionMessage>
{
    /// <summary>The fixed width of the message, header included.</summary>
    public const int Size = 12;

    /// <inheritdoc/>
    public static NetMessageType Type => NetMessageType.DirectorTransition;

    /// <inheritdoc/>
    public static NetReliability Reliability => NetReliability.Reliable;

    /// <inheritdoc/>
    public static bool TryRead(ReadOnlySpan<byte> from, out DirectorTransitionMessage message)
    {
        message = default;
        var reader = new NetMessageReader(from);
        if (!reader.Is(Size) || reader.Type != Type)
            return false;

        ushort code = reader.ReadUInt16();
        _ = reader.ReadUInt16();
        message = new DirectorTransitionMessage(code, reader.ReadInt32());
        return true;
    }

    /// <inheritdoc/>
    public int Write(Span<byte> into)
    {
        var writer = new NetMessageWriter(into, Type);
        writer.WriteUInt16(Code);
        writer.WriteUInt16(0);
        writer.WriteInt32(Id);
        return writer.Close();
    }
}

/// <summary>
/// The host's answer to a join, sent to that one peer before the roster. It carries the master
/// seed every peer's streams derive from, the host's session clock, and the joiner's seat.
/// The seat is here rather than in the roster because the roster is the same bytes for everybody.
/// Which of its entries is yours is the one fact that differs per guest. The seed and the clock
/// are the two halves of <see cref="NetHandshake"/>. Each rides as two 32-bit fields, since the
/// cursors carry no wider primitive.</summary>
public readonly record struct HandshakeMessage(ulong Seed, double HostClock, byte Seat)
    : INetMessage<HandshakeMessage>
{
    /// <summary>The fixed width of the message, header included.</summary>
    public const int Size = 24;

    /// <inheritdoc/>
    public static NetMessageType Type => NetMessageType.Handshake;

    /// <inheritdoc/>
    public static NetReliability Reliability => NetReliability.Reliable;

    /// <inheritdoc/>
    public static bool TryRead(ReadOnlySpan<byte> from, out HandshakeMessage message)
    {
        message = default;
        var reader = new NetMessageReader(from);
        if (!reader.Is(Size) || reader.Type != Type)
            return false;

        ulong seed = reader.ReadUInt32() | ((ulong)reader.ReadUInt32() << 32);
        ulong clock = reader.ReadUInt32() | ((ulong)reader.ReadUInt32() << 32);
        byte seat = reader.ReadByte();
        message = new HandshakeMessage(seed, BitConverter.UInt64BitsToDouble(clock), seat);
        return true;
    }

    /// <inheritdoc/>
    public int Write(Span<byte> into)
    {
        var writer = new NetMessageWriter(into, Type);
        writer.WriteUInt32((uint)Seed);
        writer.WriteUInt32((uint)(Seed >> 32));
        ulong clock = BitConverter.DoubleToUInt64Bits(HostClock);
        writer.WriteUInt32((uint)clock);
        writer.WriteUInt32((uint)(clock >> 32));
        writer.WriteByte(Seat);
        writer.WriteByte(0);
        writer.WriteUInt16(0);
        return writer.Close();
    }
}

/// <summary>
/// The whole seat roster and the match seed, the one variable-length message in the vocabulary.
/// The seed is here because every peer draws from seeded streams, and one seed handed out at
/// join makes every draw agree. A seat arriving or leaving resends the whole roster rather than
/// a delta, which is how the original's own roster message works.</summary>
public readonly struct SeatRosterMessage : INetMessage<SeatRosterMessage>
{
    /// <summary>The bytes before the first seat entry: the seed and the count.</summary>
    public const int PrefixSize = 12;

    /// <summary>The bytes one seat entry takes.</summary>
    public const int EntrySize = 20;

    /// <summary>How many callsign bytes an entry carries, UTF-8 and zero padded.</summary>
    public const int CallsignBytes = 16;

    /// <summary>The most seats this message can carry. The original's spawn slot packs its index
    /// into four bits, so 16 is the widest roster its own protocol can name.</summary>
    public const int MaxSeats = 16;

    private readonly NetSeatEntry[] _seats;

    /// <summary>The roster for <paramref name="seats"/> under <paramref name="seed"/>.</summary>
    public SeatRosterMessage(uint seed, IReadOnlyList<NetSeatEntry> seats)
    {
        Seed = seed;
        int count = Math.Min(seats.Count, MaxSeats);
        _seats = new NetSeatEntry[count];
        for (int i = 0; i < count; i++)
            _seats[i] = seats[i];
    }

    /// <inheritdoc/>
    public static NetMessageType Type => NetMessageType.SeatRoster;

    /// <inheritdoc/>
    public static NetReliability Reliability => NetReliability.Reliable;

    /// <summary>The master seed every peer's streams derive from.</summary>
    public uint Seed { get; }

    /// <summary>The seats, in ascending seat order.</summary>
    public IReadOnlyList<NetSeatEntry> Seats => _seats ?? Array.Empty<NetSeatEntry>();

    /// <summary>How wide a roster of <paramref name="seats"/> seats is.</summary>
    public static int SizeFor(int seats) => PrefixSize + (EntrySize * seats);

    /// <inheritdoc/>
    public static bool TryRead(ReadOnlySpan<byte> from, out SeatRosterMessage message)
    {
        message = default;
        var reader = new NetMessageReader(from);
        if (!reader.Valid || reader.Type != Type || reader.Length < PrefixSize)
            return false;

        uint seed = reader.ReadUInt32();
        int count = reader.ReadByte();
        _ = reader.ReadByte();
        _ = reader.ReadUInt16();
        if (count > MaxSeats || reader.Length != SizeFor(count))
            return false;

        var seats = new NetSeatEntry[count];
        for (int i = 0; i < count; i++)
        {
            byte seat = reader.ReadByte();
            byte team = reader.ReadByte();
            byte flags = reader.ReadByte();
            byte plane = reader.ReadByte();
            seats[i] = new NetSeatEntry(
                seat, team, plane, (flags & 1) != 0, reader.ReadText(CallsignBytes));
        }

        message = new SeatRosterMessage(seed, seats);
        return true;
    }

    /// <inheritdoc/>
    public int Write(Span<byte> into)
    {
        var seats = Seats;
        var writer = new NetMessageWriter(into, Type);
        writer.WriteUInt32(Seed);
        writer.WriteByte((byte)seats.Count);
        writer.WriteByte(0);
        writer.WriteUInt16(0);
        foreach (var seat in seats)
        {
            writer.WriteByte(seat.Seat);
            writer.WriteByte(seat.Team);
            writer.WriteByte((byte)(seat.IsHost ? 1 : 0));
            writer.WriteByte(seat.Plane);
            writer.WriteText(seat.Callsign, CallsignBytes);
        }

        return writer.Close();
    }
}

/// <summary>
/// What the vocabulary shares: the header shape, the no-seat marker and the width budget. It
/// also holds the two lookups a receiver needs before it knows which message it has. Nothing
/// here holds state, and no other namespace names a message's wire layout.</summary>
public static class NetMessage
{
    /// <summary>The header every message opens with: a type word and a total-length word, the
    /// original's own shape (<c>docs/org/multiplayer-messages.md</c>).</summary>
    public const int HeaderBytes = 4;

    /// <summary>The seat value meaning "nobody": no killer, no lock, no shooter.</summary>
    public const byte NoSeat = 0xFF;

    /// <summary>The highest type word the original itself uses. Anything above it is this
    /// remake's own and has no counterpart in <c>crimson.exe</c>.</summary>
    public const ushort OriginalIdCeiling = 0x27;

    /// <summary>The ceiling <see cref="AircraftStateMessage.Size"/> may not pass. It is the
    /// current layout plus one spare field, not a measured limit. A change that needs more has
    /// to argue for the bytes at the send rate rather than widen this quietly.</summary>
    public const int AircraftStateBudget = 48;

    /// <summary>What the transport must promise a message of <paramref name="type"/>.</summary>
    public static NetReliability ReliabilityOf(NetMessageType type) => type switch
    {
        NetMessageType.AircraftState => AircraftStateMessage.Reliability,
        NetMessageType.Fire => FireMessage.Reliability,
        NetMessageType.Death => DeathMessage.Reliability,
        NetMessageType.Score => ScoreMessage.Reliability,
        NetMessageType.MatchState => MatchStateMessage.Reliability,
        NetMessageType.Hit => HitMessage.Reliability,
        NetMessageType.SeatRoster => SeatRosterMessage.Reliability,
        NetMessageType.Damage => DamageMessage.Reliability,
        NetMessageType.Spawn => SpawnMessage.Reliability,
        NetMessageType.DirectorTransition => DirectorTransitionMessage.Reliability,
        NetMessageType.Handshake => HandshakeMessage.Reliability,
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "no such message type"),
    };

    /// <summary>Whether <paramref name="type"/> keeps the id the original used for the same
    /// event, rather than being one this remake minted.</summary>
    public static bool IsOriginalId(NetMessageType type) => (ushort)type <= OriginalIdCeiling;

    /// <summary>Reads the header only, which is all a receiver needs to route a buffer to the
    /// right deserialiser. False when the buffer does not hold a whole message.</summary>
    public static bool TryReadHeader(ReadOnlySpan<byte> from, out NetMessageType type, out int length)
    {
        var reader = new NetMessageReader(from);
        type = reader.Type;
        length = reader.Length;
        return reader.Valid;
    }
}
