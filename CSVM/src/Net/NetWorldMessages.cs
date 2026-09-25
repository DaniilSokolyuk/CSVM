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
/// One zeppelin's path position as the host flies it, the original's <c>0x1e</c> record for one
/// hull. It carries position, speed, pitch and yaw in that order, on the original's half second.
/// A zeppelin is named by its placement index.
/// Plain unreliable with a per-zeppelin sequence, for the reason <see cref="AiStateMessage"/> is.
/// The original's part-state word and cannon-shot tail are not carried.</summary>
public readonly record struct ZeppelinStateMessage(
    ushort Zeppelin, ushort Sequence, Vector3 Position, float Speed, float PitchRad, float YawRad)
    : INetMessage<ZeppelinStateMessage>
{
    /// <summary>The fixed width of the message, header included.</summary>
    public const int Size = 32;

    /// <inheritdoc/>
    public static NetMessageType Type => NetMessageType.ZeppelinState;

    /// <inheritdoc/>
    public static NetReliability Reliability => NetReliability.Unreliable;

    /// <inheritdoc/>
    public static bool TryRead(ReadOnlySpan<byte> from, out ZeppelinStateMessage message)
    {
        message = default;
        var reader = new NetMessageReader(from);
        if (!reader.Is(Size) || reader.Type != Type)
            return false;

        ushort zeppelin = reader.ReadUInt16();
        ushort sequence = reader.ReadUInt16();
        var position = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
        float speed = reader.ReadSingle();
        float pitch = reader.ReadSingle();
        float yaw = reader.ReadSingle();
        message = new ZeppelinStateMessage(zeppelin, sequence, position, speed, pitch, yaw);
        return true;
    }

    /// <inheritdoc/>
    public int Write(Span<byte> into)
    {
        var writer = new NetMessageWriter(into, Type);
        writer.WriteUInt16(Zeppelin);
        writer.WriteUInt16(Sequence);
        writer.WriteSingle(Position.X);
        writer.WriteSingle(Position.Y);
        writer.WriteSingle(Position.Z);
        writer.WriteSingle(Speed);
        writer.WriteSingle(PitchRad);
        writer.WriteSingle(YawRad);
        return writer.Close();
    }
}

/// <summary>
/// One AI aircraft a host generator launched, which every guest builds at the same admission
/// ordinal. It names the generator by its live index and the patrol net by its index in that
/// generator's list. It carries the launch pose, velocity and lever as the host applied them.
/// Reliable and ordered, because every later message about the AI names it by the ordinal this
/// one claims.</summary>
public readonly record struct AiSpawnMessage(
    ushort Ai,
    ushort LaunchOrdinal,
    byte Generator,
    byte Net,
    Vector3 Position,
    Vector3 Drop,
    Vector3? Velocity,
    bool CarrierDrop,
    float? Throttle) : INetMessage<AiSpawnMessage>
{
    /// <summary>The fixed width of the message, header included.</summary>
    public const int Size = 44;

    private const byte CarrierDropFlag = 1;
    private const byte VelocityFlag = 2;
    private const byte ThrottleFlag = 4;

    /// <inheritdoc/>
    public static NetMessageType Type => NetMessageType.AiSpawn;

    /// <inheritdoc/>
    public static NetReliability Reliability => NetReliability.Reliable;

    /// <inheritdoc/>
    public static bool TryRead(ReadOnlySpan<byte> from, out AiSpawnMessage message)
    {
        message = default;
        var reader = new NetMessageReader(from);
        if (!reader.Is(Size) || reader.Type != Type)
            return false;

        ushort ai = reader.ReadUInt16();
        ushort launch = reader.ReadUInt16();
        byte generator = reader.ReadByte();
        byte net = reader.ReadByte();
        byte flags = reader.ReadByte();
        float throttle = reader.ReadByte() / 255f;
        var position = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
        var drop = new Vector3(reader.ReadUnit(), reader.ReadUnit(), reader.ReadUnit());
        _ = reader.ReadUInt16();
        var velocity = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
        message = new AiSpawnMessage(
            ai, launch, generator, net, position, drop,
            (flags & VelocityFlag) != 0 ? velocity : null,
            (flags & CarrierDropFlag) != 0,
            (flags & ThrottleFlag) != 0 ? throttle : null);
        return true;
    }

    /// <inheritdoc/>
    public int Write(Span<byte> into)
    {
        var writer = new NetMessageWriter(into, Type);
        writer.WriteUInt16(Ai);
        writer.WriteUInt16(LaunchOrdinal);
        writer.WriteByte(Generator);
        writer.WriteByte(Net);
        byte flags = (byte)((CarrierDrop ? CarrierDropFlag : 0)
            | (Velocity != null ? VelocityFlag : 0)
            | (Throttle != null ? ThrottleFlag : 0));
        writer.WriteByte(flags);
        writer.WriteByte((byte)Math.Round(Math.Clamp(Throttle ?? 0f, 0f, 1f) * 255f));
        writer.WriteSingle(Position.X);
        writer.WriteSingle(Position.Y);
        writer.WriteSingle(Position.Z);
        writer.WriteUnit(Drop.X);
        writer.WriteUnit(Drop.Y);
        writer.WriteUnit(Drop.Z);
        writer.WriteUInt16(0);
        var velocity = Velocity ?? Vector3.Zero;
        writer.WriteSingle(velocity.X);
        writer.WriteSingle(velocity.Y);
        writer.WriteSingle(velocity.Z);
        return writer.Close();
    }
}

/// <summary>
/// One surface vehicle's patrol as the host drives it: position, speed and heading. It keeps
/// <see cref="ZeppelinStateMessage"/>'s order and drops the pitch, since a hull sits on the water.
/// A hull is named by its index in the runtime's build order and guarded by its name's hash. A
/// guest searches by the hash when the index has shifted. Plain unreliable with a per-hull
/// sequence, for the reason <see cref="AiStateMessage"/> is.</summary>
public readonly record struct SurfaceVehicleStateMessage(
    ushort Vehicle, ushort Sequence, int NameKey, Vector3 Position, float Speed, float YawRad)
    : INetMessage<SurfaceVehicleStateMessage>
{
    /// <summary>The fixed width of the message, header included.</summary>
    public const int Size = 32;

    /// <inheritdoc/>
    public static NetMessageType Type => NetMessageType.SurfaceVehicleState;

    /// <inheritdoc/>
    public static NetReliability Reliability => NetReliability.Unreliable;

    /// <inheritdoc/>
    public static bool TryRead(ReadOnlySpan<byte> from, out SurfaceVehicleStateMessage message)
    {
        message = default;
        var reader = new NetMessageReader(from);
        if (!reader.Is(Size) || reader.Type != Type)
            return false;

        ushort vehicle = reader.ReadUInt16();
        ushort sequence = reader.ReadUInt16();
        int key = reader.ReadInt32();
        var position = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
        float speed = reader.ReadSingle();
        float yaw = reader.ReadSingle();
        message = new SurfaceVehicleStateMessage(vehicle, sequence, key, position, speed, yaw);
        return true;
    }

    /// <inheritdoc/>
    public int Write(Span<byte> into)
    {
        var writer = new NetMessageWriter(into, Type);
        writer.WriteUInt16(Vehicle);
        writer.WriteUInt16(Sequence);
        writer.WriteInt32(NameKey);
        writer.WriteSingle(Position.X);
        writer.WriteSingle(Position.Y);
        writer.WriteSingle(Position.Z);
        writer.WriteSingle(Speed);
        writer.WriteSingle(YawRad);
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
