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

    /// <summary>A pilot asking the host to place it again.</summary>
    SpawnRequest = 0x0044,

    /// <summary>One host-flown AI aircraft's pose, motion and control state.</summary>
    AiState = 0x0045,

    /// <summary>One weapon discharge by a host-flown AI aircraft.</summary>
    AiFire = 0x0046,

    /// <summary>A guest's claim that a round of its landed on a host-flown AI aircraft.</summary>
    AiHit = 0x0047,

    /// <summary>One host decision about the world: an AI death, an AI hull, a destructible's
    /// health.</summary>
    WorldEvent = 0x0048,

    /// <summary>A guest's clock question and the host's answer, the round trip the shared clock
    /// reads the link latency from.</summary>
    ClockPing = 0x0049,

    /// <summary>What a host tells a peer about the session it is holding open before any flight:
    /// its kind, its mission and its player count.</summary>
    SessionAdvert = 0x004A,

    /// <summary>One zeppelin's path position as the host has it: where it is, its facing and its
    /// speed.</summary>
    ZeppelinState = 0x004B,

    /// <summary>An AI aircraft a host generator launched: its admission ordinal, the generator and
    /// the pose it left from.</summary>
    AiSpawn = 0x004C,

    /// <summary>One surface vehicle's patrol position as the host has it: where it is, its heading
    /// and its speed.</summary>
    SurfaceVehicleState = 0x004D,

    /// <summary>A start the host decides off where the seats are flying. It carries a landing row
    /// and its seat, the ladder switch's holder, or a guest's auto-land button.</summary>
    PositionalStart = 0x004E,

    /// <summary>A host's word that it is sending a guest away: the session closed, or it is full.
    /// </summary>
    SessionClosed = 0x004F,

    /// <summary>A co-op host's screen as a guest follows it: which board, which mission, who is
    /// Ready, and the shared result once a mission ends.</summary>
    CoopFlow = 0x0050,

    /// <summary>A co-op guest's answer on the flight check. It carries the airframe, fit and name,
    /// whether the guest is Ready, and whether it left the flight.</summary>
    CoopPick = 0x0051,

    /// <summary>One seat's ammunition and ordnance, sent by a co-op host to every guest at a launch.
    /// </summary>
    CoopSeatFit = 0x0052,

    /// <summary>A Dogfight host's Mission Options: the environment, the type, the victory
    /// condition and the lives rule.</summary>
    DogfightOptions = 0x0053,

    /// <summary>A Dogfight host's player list: each pilot's name, airframe and Ready mark.</summary>
    DogfightRoster = 0x0054,

    /// <summary>One line of lobby chat and the name of the pilot who typed it.</summary>
    LobbyChat = 0x0055,

    /// <summary>A lobby's first word to a peer on connect: this build's MAJOR.MINOR version.
    /// </summary>
    BuildVersion = 0x0056,

    /// <summary>A guest's claim of health damage on a host-owned destructible pool.</summary>
    DestructibleHit = 0x0057,

    /// <summary>A skip of one shared cutscene episode: a guest's ask, or the host's word that it
    /// skipped.</summary>
    CutsceneSkip = 0x0058,
}

/// <summary>Which board a co-op host stands on, the screen a <see cref="CoopFlowMessage"/> names.
/// </summary>
public enum NetCoopScreen : byte
{
    /// <summary>A screen this build does not know.</summary>
    Unknown = 0,

    /// <summary>The cabin, or any board of the host's own past it.</summary>
    Cabin = 1,

    /// <summary>The mission briefing.</summary>
    Briefing = 2,

    /// <summary>The flight check and its plane and ammo screens.</summary>
    FlightCheck = 3,

    /// <summary>The host is flying the mission.</summary>
    InMission = 4,

    /// <summary>The mission ended and the host stands on its debrief.</summary>
    Debrief = 5,
}

/// <summary>Where a session a host advertises stands, the games list's Status column.</summary>
public enum NetSessionStatus : byte
{
    /// <summary>The advert named no status, or one this build does not know.</summary>
    Unknown = 0,

    /// <summary>The host is on its boards and a guest who joins waits there with it.</summary>
    Waiting = 1,

    /// <summary>The host is flying. A guest may still join and waits for the next launch.</summary>
    InMission = 2,

    /// <summary>Every seat the session offers is taken.</summary>
    Full = 3,
}

/// <summary>Why a host sent a guest a <see cref="SessionClosedMessage"/>.</summary>
public enum NetCloseReason : byte
{
    /// <summary>A reason this build does not know.</summary>
    Unknown = 0,

    /// <summary>The host closed its session on purpose.</summary>
    Closed = 1,

    /// <summary>The session had no seat left for this guest.</summary>
    Full = 2,

    /// <summary>The guest's build does not play with the host's: their MAJOR.MINOR versions
    /// differ.</summary>
    VersionMismatch = 3,
}

/// <summary>What a <see cref="PositionalStartMessage"/> says. Each member names what the seat,
/// the row and the held flag carry.</summary>
public enum NetPositionalStart : byte
{
    /// <summary>The host started a landing approach row. The row is its index in the chapter's
    /// resolved table, the seat the human whose flying started it.</summary>
    LandingRow = 1,

    /// <summary>The ladder switch changed hands. The seat is the new holder, or
    /// <see cref="NetMessage.NoSeat"/> when nobody qualifies.</summary>
    LadderHolder = 2,

    /// <summary>A guest's auto-land button, sent to the host whenever it changes while an auto row
    /// is offered to that seat. The seat is the guest's own.</summary>
    AutoLandHeld = 3,
}

/// <summary>What kind of session a host holds open, the word a join board names it by.</summary>
public enum NetSessionKind : byte
{
    /// <summary>No advert has arrived, or it named a kind this build does not know.</summary>
    Unknown = 0,

    /// <summary>A Dogfight match, the original's own network mode.</summary>
    Dogfight = 1,

    /// <summary>A campaign mission flown together, hosted from the campaign's own boards.</summary>
    CampaignCoop = 2,
}

/// <summary>What a <see cref="WorldEventMessage"/>'s code means. Each member says what the subject
/// and the argument carry.</summary>
public enum NetWorldEvent : ushort
{
    /// <summary>An AI aircraft died. The subject is its admission ordinal, the argument the killer's
    /// seat or -1 when no seat is credited.</summary>
    AiDowned = 1,

    /// <summary>An AI aircraft's hull after damage. The subject is its admission ordinal and the
    /// value its summary health fraction.</summary>
    AiHull = 2,

    /// <summary>A destructible pool's health after a stage change or a kill. The subject is its
    /// registration index, the argument a hash of its definition and anchor names, and the value
    /// its health.</summary>
    DestructibleHealth = 3,

    /// <summary>The host drew a <c>WARP_VEHICLE</c> waypoint. The subject is the drawn index into
    /// the directive's list, the argument a hash of the vehicle's name.</summary>
    VehicleWarped = 4,

    /// <summary>An AI aircraft went out of the mission or came back into it. The subject is its
    /// admission ordinal and the argument 1 for in play, 0 for deactivated. A cutscene park is not
    /// sent, since each end's own cutscene parks its own copy.</summary>
    AiPresence = 5,

    /// <summary>A guest's link dropped mid-mission and its aeroplane left the field. The subject is
    /// that guest's seat.</summary>
    SeatLeft = 6,
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

/// <summary>What a <see cref="DirectorTransitionMessage"/>'s code means: one event of the host's
/// objectives graph. The seven transitions carry the objective number in the id's low 16 bits
/// and the completing objective that caused it in the high 16. The rest are laid out on their own
/// members. The id layout is <c>docs/org/multiplayer-messages.md</c>'s.</summary>
public enum NetDirectorEvent : ushort
{
    /// <summary>An objective woke.</summary>
    Woke = 1,

    /// <summary>An objective went to sleep on a timer.</summary>
    Napped = 2,

    /// <summary>An objective's conditions read true and its completion actions ran.</summary>
    Completed = 3,

    /// <summary>An objective was killed by another's completion.</summary>
    Killed = 4,

    /// <summary>An objective was slept permanently by another's completion.</summary>
    Slept = 5,

    /// <summary>An objective's own deadline retired it.</summary>
    Expired = 6,

    /// <summary>An objective was marked complete by another's <c>HIDE_OBJ</c>.</summary>
    Hidden = 7,

    /// <summary>A completion's chain has run and its display row is marked. The id is the
    /// objective number alone.</summary>
    Settled = 8,

    /// <summary>The mission countdown ran out. The id is zero.</summary>
    TimerExpired = 9,

    /// <summary>A win or loss was decided. The id's low byte is the outcome, and bit 8 says the
    /// objectives sound played ahead of the mission sound.</summary>
    Ending = 10,

    /// <summary>The wrap-up ran out and the mission is over. The id is the outcome.</summary>
    Ended = 11,
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
/// Unreliable, because every peer spawns the projectile locally from this event and a lost burst
/// is cosmetic. Not sequenced: rounds fired on one step race each other under jitter. A burst is
/// a burst whichever lands first, so none is dropped for arriving behind another. A target
/// seat of <see cref="NetMessage.NoSeat"/> means no lock.</summary>
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
    /// <remarks>⚠ Do not make this reliable. A burst would then stall behind a retransmission,
    /// which is worse than losing it.</remarks>
    public static NetReliability Reliability => NetReliability.Unreliable;

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
/// A pilot asking the host to place it again: the whole of what a guest says about its own
/// respawn. Reliable, and answered with a <see cref="SpawnMessage"/> for the same seat. It
/// carries the seat and nothing else, because where and when the aeroplane comes back are the
/// host's to decide.</summary>
public readonly record struct SpawnRequestMessage(byte Seat) : INetMessage<SpawnRequestMessage>
{
    /// <summary>The fixed width of the message, header included.</summary>
    public const int Size = 5;

    /// <inheritdoc/>
    public static NetMessageType Type => NetMessageType.SpawnRequest;

    /// <inheritdoc/>
    public static NetReliability Reliability => NetReliability.Reliable;

    /// <inheritdoc/>
    public static bool TryRead(ReadOnlySpan<byte> from, out SpawnRequestMessage message)
    {
        message = default;
        var reader = new NetMessageReader(from);
        if (!reader.Is(Size) || reader.Type != Type)
            return false;

        message = new SpawnRequestMessage(reader.ReadByte());
        return true;
    }

    /// <inheritdoc/>
    public int Write(Span<byte> into)
    {
        var writer = new NetMessageWriter(into, Type);
        writer.WriteByte(Seat);
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
/// remaining time and the same end. <c>HostClock</c> is the host's session time at send, the
/// remake's one addition to the original's <c>0x17</c>. The periodic tick is the only message a
/// running match repeats, so it is what <see cref="NetClockSlew"/> reads its offset from. A
/// float, not a double, costs 2.4e-4 s of step at the hour mark, far under what the slew calls
/// settled.</summary>
public readonly record struct MatchStateMessage(
    float RemainingSeconds, float TimeLimitSeconds, short ScoreTarget, NetMatchEnd End,
    float HostClock = 0f)
    : INetMessage<MatchStateMessage>
{
    /// <summary>The fixed width of the message, header included.</summary>
    public const int Size = 20;

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
        message = new MatchStateMessage(remaining, limit, target, end, reader.ReadSingle());
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
        writer.WriteSingle(HostClock);
        return writer.Close();
    }
}

/// <summary>
/// One mission-director event, as a code and an id. Reliable, and opaque to the envelope: the code
/// is a <see cref="NetDirectorEvent"/>, whose members say how the id is laid out. The host is the
/// only sender. <c>HostClock</c> is the host's session time when its graph raised the event. A
/// guest subtracts it from its shared clock to learn how late the event arrived.</summary>
public readonly record struct DirectorTransitionMessage(ushort Code, int Id, float HostClock = 0f)
    : INetMessage<DirectorTransitionMessage>
{
    /// <summary>The fixed width of the message, header included.</summary>
    public const int Size = 16;

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
        int id = reader.ReadInt32();
        message = new DirectorTransitionMessage(code, id, reader.ReadSingle());
        return true;
    }

    /// <inheritdoc/>
    public int Write(Span<byte> into)
    {
        var writer = new NetMessageWriter(into, Type);
        writer.WriteUInt16(Code);
        writer.WriteUInt16(0);
        writer.WriteInt32(Id);
        writer.WriteSingle(HostClock);
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
/// A host's word about its open session, sent to every peer on connect and again on any change,
/// and inside a LAN discovery reply. A join board reads it before a flight exists. So it
/// names only the session: its kind, its campaign mission, its player count, its status, its seat
/// cap and its host. Nothing about the world rides here. A guest reads it off the lobby, never off
/// a session.</summary>
public readonly record struct SessionAdvertMessage(
    NetSessionKind Kind, byte MissionSeq, byte Players, string Host,
    NetSessionStatus Status = NetSessionStatus.Waiting, byte Cap = 0)
    : INetMessage<SessionAdvertMessage>
{
    /// <summary>The fixed width of the message, header included.</summary>
    public const int Size = 28;

    /// <summary>How many bytes the host's name takes, UTF-8 and zero padded.</summary>
    public const int HostBytes = 16;

    /// <summary>The mission value meaning "no campaign mission": a Dogfight advert.</summary>
    public const byte NoMission = 0xFF;

    /// <summary>How many missions one campaign chapter holds, the divisor the chapter reads by.
    /// </summary>
    public const int MissionsPerChapter = 5;

    /// <inheritdoc/>
    public static NetMessageType Type => NetMessageType.SessionAdvert;

    /// <inheritdoc/>
    public static NetReliability Reliability => NetReliability.Reliable;

    /// <summary>Whether the advert names a campaign mission.</summary>
    public bool HasMission => Kind == NetSessionKind.CampaignCoop && MissionSeq != NoMission;

    /// <summary>The story chapter the mission sits in, from 1, or 0 with no mission.</summary>
    public int Chapter => HasMission ? (MissionSeq / MissionsPerChapter) + 1 : 0;

    /// <summary>The mission's place inside its chapter, from 1, or 0 with no mission.</summary>
    public int MissionInChapter => HasMission ? (MissionSeq % MissionsPerChapter) + 1 : 0;

    /// <inheritdoc/>
    public static bool TryRead(ReadOnlySpan<byte> from, out SessionAdvertMessage message)
    {
        message = default;
        var reader = new NetMessageReader(from);
        if (!reader.Is(Size) || reader.Type != Type)
            return false;

        byte kind = reader.ReadByte();
        byte seq = reader.ReadByte();
        byte players = reader.ReadByte();
        byte status = reader.ReadByte();
        byte cap = reader.ReadByte();
        _ = reader.ReadByte();
        _ = reader.ReadUInt16();
        var known = kind is (byte)NetSessionKind.Dogfight or (byte)NetSessionKind.CampaignCoop
            ? (NetSessionKind)kind
            : NetSessionKind.Unknown;
        var stands = status is >= (byte)NetSessionStatus.Waiting and <= (byte)NetSessionStatus.Full
            ? (NetSessionStatus)status
            : NetSessionStatus.Unknown;
        message = new SessionAdvertMessage(known, seq, players, reader.ReadText(HostBytes), stands, cap);
        return true;
    }

    /// <inheritdoc/>
    public int Write(Span<byte> into)
    {
        var writer = new NetMessageWriter(into, Type);
        writer.WriteByte((byte)Kind);
        writer.WriteByte(MissionSeq);
        writer.WriteByte(Players);
        writer.WriteByte((byte)Status);
        writer.WriteByte(Cap);
        writer.WriteByte(0);
        writer.WriteUInt16(0);
        writer.WriteText(Host ?? "", HostBytes);
        return writer.Close();
    }
}

/// <summary>
/// A host's word to a guest that it is being sent away, and why, with both build versions. A host
/// sends it to every guest before closing, and to a guest it has no seat for or whose version
/// does not play. A guest's board can then tell a host that closed from a link that
/// dropped. Like the advert it stays in the lobby and never reaches a session.
/// ⚠ Do not change this layout. A refused guest of another build reads it to say why.
/// </summary>
public readonly record struct SessionClosedMessage(
    NetCloseReason Reason, NetBuildVersion Host = default, NetBuildVersion Guest = default)
    : INetMessage<SessionClosedMessage>
{
    /// <summary>The fixed width of the message, header included.</summary>
    public const int Size = 8 + (2 * NetBuildVersion.WireBytes);

    /// <inheritdoc/>
    public static NetMessageType Type => NetMessageType.SessionClosed;

    /// <inheritdoc/>
    public static NetReliability Reliability => NetReliability.Reliable;

    /// <inheritdoc/>
    public static bool TryRead(ReadOnlySpan<byte> from, out SessionClosedMessage message)
    {
        message = default;
        var reader = new NetMessageReader(from);
        if (!reader.Is(Size) || reader.Type != Type)
            return false;

        byte reason = reader.ReadByte();
        _ = reader.ReadByte();
        _ = reader.ReadUInt16();
        if (!NetBuildVersion.TryFromWords(reader.ReadUInt16(), reader.ReadUInt16(), out var host)
            || !NetBuildVersion.TryFromWords(reader.ReadUInt16(), reader.ReadUInt16(), out var guest))
            return false;

        var known = reason is >= (byte)NetCloseReason.Closed and <= (byte)NetCloseReason.VersionMismatch
            ? (NetCloseReason)reason
            : NetCloseReason.Unknown;
        message = new SessionClosedMessage(known, host, guest);
        return true;
    }

    /// <inheritdoc/>
    public int Write(Span<byte> into)
    {
        var writer = new NetMessageWriter(into, Type);
        writer.WriteByte((byte)Reason);
        writer.WriteByte(0);
        writer.WriteUInt16(0);
        Host.Write(ref writer);
        Guest.Write(ref writer);
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

    // The reader's own array, taken as it stands rather than copied a second time.
    private SeatRosterMessage(NetSeatEntry[] seats, uint seed)
    {
        Seed = seed;
        _seats = seats;
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

        message = new SeatRosterMessage(seats, seed);
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
/// A guest's question about the host's clock, and the host's answer. It has the shape of the
/// original's <c>0x23</c> ping: two stamps in 12 bytes. The guest sends its own session clock as
/// <c>AskedClock</c>; the host sends the same message back with its session clock as
/// <c>HostClock</c>. Which one a message is follows from who receives it, since only a guest asks.
/// Unreliable, unlike the original's: a retransmitted question would read as a longer link.
/// </summary>
public readonly record struct ClockPingMessage(float AskedClock, float HostClock = 0f)
    : INetMessage<ClockPingMessage>
{
    /// <summary>The fixed width of the message, header included, the original's own.</summary>
    public const int Size = 12;

    /// <inheritdoc/>
    public static NetMessageType Type => NetMessageType.ClockPing;

    /// <inheritdoc/>
    public static NetReliability Reliability => NetReliability.Unreliable;

    /// <inheritdoc/>
    public static bool TryRead(ReadOnlySpan<byte> from, out ClockPingMessage message)
    {
        message = default;
        var reader = new NetMessageReader(from);
        if (!reader.Is(Size) || reader.Type != Type)
            return false;

        float asked = reader.ReadSingle();
        message = new ClockPingMessage(asked, reader.ReadSingle());
        return true;
    }

    /// <inheritdoc/>
    public int Write(Span<byte> into)
    {
        var writer = new NetMessageWriter(into, Type);
        writer.WriteSingle(AskedClock);
        writer.WriteSingle(HostClock);
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

    /// <summary>The <see cref="SpawnMessage"/> entry meaning "no table entry": the match runs over
    /// no spawn list, so the seat comes back on the one pose it was given.</summary>
    public const ushort NoSpawnEntry = 0xFFFF;

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
        NetMessageType.SpawnRequest => SpawnRequestMessage.Reliability,
        NetMessageType.DirectorTransition => DirectorTransitionMessage.Reliability,
        NetMessageType.Handshake => HandshakeMessage.Reliability,
        NetMessageType.AiState => AiStateMessage.Reliability,
        NetMessageType.AiFire => AiFireMessage.Reliability,
        NetMessageType.AiHit => AiHitMessage.Reliability,
        NetMessageType.WorldEvent => WorldEventMessage.Reliability,
        NetMessageType.ClockPing => ClockPingMessage.Reliability,
        NetMessageType.SessionAdvert => SessionAdvertMessage.Reliability,
        NetMessageType.ZeppelinState => ZeppelinStateMessage.Reliability,
        NetMessageType.AiSpawn => AiSpawnMessage.Reliability,
        NetMessageType.SurfaceVehicleState => SurfaceVehicleStateMessage.Reliability,
        NetMessageType.PositionalStart => PositionalStartMessage.Reliability,
        NetMessageType.SessionClosed => SessionClosedMessage.Reliability,
        NetMessageType.CoopFlow => CoopFlowMessage.Reliability,
        NetMessageType.CoopPick => CoopPickMessage.Reliability,
        NetMessageType.CoopSeatFit => CoopSeatFitMessage.Reliability,
        NetMessageType.DogfightOptions => DogfightOptionsMessage.Reliability,
        NetMessageType.DogfightRoster => DogfightRosterMessage.Reliability,
        NetMessageType.LobbyChat => LobbyChatMessage.Reliability,
        NetMessageType.BuildVersion => BuildVersionMessage.Reliability,
        NetMessageType.DestructibleHit => DestructibleHitMessage.Reliability,
        NetMessageType.CutsceneSkip => CutsceneSkipMessage.Reliability,
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
