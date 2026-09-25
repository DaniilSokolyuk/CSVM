using System;
using Godot;

namespace CSVM.Net;

/// <summary>
/// One host-flown AI aircraft's state, the AI counterpart of <see cref="AircraftStateMessage"/>
/// with the same quantisation. An AI is named by its admission ordinal, the index both ends'
/// rosters admitted it at. Plain unreliable: the samples of every AI share one channel, so a
/// transport-level sequence would discard one AI's sample against another's. The sequence here is
/// per AI, and each AI's own pose buffer drops a stale one.</summary>
public readonly record struct AiStateMessage(
    ushort Ai,
    ushort Sequence,
    Vector3 Position,
    Quaternion Attitude,
    Vector3 Velocity,
    float Throttle,
    float Aileron,
    float Elevator,
    float Rudder,
    bool Nitro) : INetMessage<AiStateMessage>
{
    /// <summary>The fixed width of the message, header included.</summary>
    public const int Size = 48;

    /// <inheritdoc/>
    public static NetMessageType Type => NetMessageType.AiState;

    /// <inheritdoc/>
    public static NetReliability Reliability => NetReliability.Unreliable;

    /// <summary>The same sample in the shape a <see cref="RemotePoseBuffer"/> takes. The seat
    /// field is unused by the buffer and carries <see cref="NetMessage.NoSeat"/>.</summary>
    public AircraftStateMessage AsAircraftState() =>
        new(NetMessage.NoSeat, Sequence, Position, Attitude, Velocity,
            Throttle, Aileron, Elevator, Rudder, Nitro);

    /// <inheritdoc/>
    public static bool TryRead(ReadOnlySpan<byte> from, out AiStateMessage message)
    {
        message = default;
        var reader = new NetMessageReader(from);
        if (!reader.Is(Size) || reader.Type != Type)
            return false;

        ushort ai = reader.ReadUInt16();
        ushort sequence = reader.ReadUInt16();
        var position = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
        var attitude = new Quaternion(
            reader.ReadUnit(), reader.ReadUnit(), reader.ReadUnit(), reader.ReadUnit());
        var velocity = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
        float throttle = reader.ReadByte() / 255f;
        float aileron = reader.ReadSByte() / 127f;
        float elevator = reader.ReadSByte() / 127f;
        float rudder = reader.ReadSByte() / 127f;
        byte flags = reader.ReadByte();
        message = new AiStateMessage(
            ai, sequence, position, attitude, velocity,
            throttle, aileron, elevator, rudder, (flags & 1) != 0);
        return true;
    }

    /// <inheritdoc/>
    public int Write(Span<byte> into)
    {
        var writer = new NetMessageWriter(into, Type);
        writer.WriteUInt16(Ai);
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
        writer.WriteByte((byte)(Nitro ? 1 : 0));
        writer.WriteByte(0);
        writer.WriteUInt16(0);
        return writer.Close();
    }
}

/// <summary>
/// One weapon discharge by a host-flown AI aircraft. Every guest spawns the round locally from
/// it, as <see cref="FireMessage"/> does for a seat. Unreliable, because a lost burst is cosmetic:
/// the host alone decides what an AI's round hits.</summary>
public readonly record struct AiFireMessage(ushort Ai, byte Weapon, Vector3 Origin, Vector3 Direction)
    : INetMessage<AiFireMessage>
{
    /// <summary>The fixed width of the message, header included.</summary>
    public const int Size = 28;

    /// <inheritdoc/>
    public static NetMessageType Type => NetMessageType.AiFire;

    /// <inheritdoc/>
    public static NetReliability Reliability => NetReliability.Unreliable;

    /// <inheritdoc/>
    public static bool TryRead(ReadOnlySpan<byte> from, out AiFireMessage message)
    {
        message = default;
        var reader = new NetMessageReader(from);
        if (!reader.Is(Size) || reader.Type != Type)
            return false;

        ushort ai = reader.ReadUInt16();
        byte weapon = reader.ReadByte();
        _ = reader.ReadByte();
        var origin = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
        var direction = new Vector3(reader.ReadUnit(), reader.ReadUnit(), reader.ReadUnit());
        message = new AiFireMessage(ai, weapon, origin, direction);
        return true;
    }

    /// <inheritdoc/>
    public int Write(Span<byte> into)
    {
        var writer = new NetMessageWriter(into, Type);
        writer.WriteUInt16(Ai);
        writer.WriteByte(Weapon);
        writer.WriteByte(0);
        writer.WriteSingle(Origin.X);
        writer.WriteSingle(Origin.Y);
        writer.WriteSingle(Origin.Z);
        writer.WriteUnit(Direction.X);
        writer.WriteUnit(Direction.Y);
        writer.WriteUnit(Direction.Z);
        writer.WriteUInt16(0);
        return writer.Close();
    }
}

/// <summary>
/// A guest's claim that one of its rounds landed on a host-flown AI aircraft, the AI counterpart
/// of <see cref="HitMessage"/>. Reliable, sent to the host alone, which spends it through the AI's
/// own damage path. The impact is in the AI's body space for the same reason a seat hit's is.</summary>
public readonly record struct AiHitMessage(
    ushort Ai, byte ShooterSeat, ushort Weapon, float Damage, short Part, Vector3 LocalImpact)
    : INetMessage<AiHitMessage>
{
    /// <summary>The fixed width of the message, header included.</summary>
    public const int Size = 28;

    /// <inheritdoc/>
    public static NetMessageType Type => NetMessageType.AiHit;

    /// <inheritdoc/>
    public static NetReliability Reliability => NetReliability.Reliable;

    /// <inheritdoc/>
    public static bool TryRead(ReadOnlySpan<byte> from, out AiHitMessage message)
    {
        message = default;
        var reader = new NetMessageReader(from);
        if (!reader.Is(Size) || reader.Type != Type)
            return false;

        ushort ai = reader.ReadUInt16();
        byte shooter = reader.ReadByte();
        _ = reader.ReadByte();
        ushort weapon = reader.ReadUInt16();
        short part = reader.ReadInt16();
        float damage = reader.ReadSingle();
        var impact = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
        message = new AiHitMessage(ai, shooter, weapon, damage, part, impact);
        return true;
    }

    /// <inheritdoc/>
    public int Write(Span<byte> into)
    {
        var writer = new NetMessageWriter(into, Type);
        writer.WriteUInt16(Ai);
        writer.WriteByte(ShooterSeat);
        writer.WriteByte(0);
        writer.WriteUInt16(Weapon);
        writer.WriteInt16(Part);
        writer.WriteSingle(Damage);
        writer.WriteSingle(LocalImpact.X);
        writer.WriteSingle(LocalImpact.Y);
        writer.WriteSingle(LocalImpact.Z);
        return writer.Close();
    }
}

/// <summary>
/// One host decision about the world, as a code, a subject, an argument and a value. Reliable,
/// and sent by the host alone. The code is a <see cref="NetWorldEvent"/>, whose members say what
/// the other three fields carry.</summary>
public readonly record struct WorldEventMessage(ushort Code, ushort Subject, int Argument, float Value)
    : INetMessage<WorldEventMessage>
{
    /// <summary>The fixed width of the message, header included.</summary>
    public const int Size = 16;

    /// <inheritdoc/>
    public static NetMessageType Type => NetMessageType.WorldEvent;

    /// <inheritdoc/>
    public static NetReliability Reliability => NetReliability.Reliable;

    /// <inheritdoc/>
    public static bool TryRead(ReadOnlySpan<byte> from, out WorldEventMessage message)
    {
        message = default;
        var reader = new NetMessageReader(from);
        if (!reader.Is(Size) || reader.Type != Type)
            return false;

        message = new WorldEventMessage(
            reader.ReadUInt16(), reader.ReadUInt16(), reader.ReadInt32(), reader.ReadSingle());
        return true;
    }

    /// <inheritdoc/>
    public int Write(Span<byte> into)
    {
        var writer = new NetMessageWriter(into, Type);
        writer.WriteUInt16(Code);
        writer.WriteUInt16(Subject);
        writer.WriteInt32(Argument);
        writer.WriteSingle(Value);
        return writer.Close();
    }
}
