using System;
using System.Collections.Generic;
using CSVM.Net;
using Godot;
using Xunit;

namespace CSVM.Tests;

/// <summary>
/// The wire vocabulary off-engine: one round trip per message type, the header a receiver routes
/// on, and the reliability class each type declares. It also covers the rejections a
/// deserialiser owes a short or mistyped buffer, and the width budget the per-frame aircraft
/// state message is held to. Nothing here needs a transport: every test writes into a span and
/// reads it back, which is exactly what a session hands a transport.
/// </summary>
[Trait("Tier", "Quick")]
public class NetMessagesTests
{
    // How many decimal places a quantised field is compared to. The coarsest is a control
    // surface at 1/127, so two places is a real check and not a rounded-away one.
    private const int QuantisedPlaces = 2;

    [Fact]
    public void AircraftStateRoundTripsPosePlusControlsAndFitsTheBudget()
    {
        var sent = new AircraftStateMessage(
            Seat: 3,
            Sequence: 40000,
            Position: new Vector3(1234.5f, -678.25f, 9012.75f),
            Attitude: new Quaternion(0.5f, -0.5f, 0.5f, 0.5f),
            Velocity: new Vector3(-90.5f, 3.25f, 120f),
            Throttle: 0.85f,
            Aileron: -0.5f,
            Elevator: 0.25f,
            Rudder: 1f,
            Nitro: true);

        Span<byte> buffer = stackalloc byte[64];
        int written = sent.Write(buffer);

        Assert.Equal(AircraftStateMessage.Size, written);
        Assert.True(
            AircraftStateMessage.Size <= NetMessage.AircraftStateBudget,
            $"the per-frame message is {AircraftStateMessage.Size} bytes, over the " +
            $"{NetMessage.AircraftStateBudget}-byte budget");
        Assert.True(AircraftStateMessage.TryRead(buffer[..written], out var got));
        Assert.Equal(sent.Seat, got.Seat);
        Assert.Equal(sent.Sequence, got.Sequence);
        Assert.Equal(sent.Position, got.Position);
        Assert.Equal(sent.Velocity, got.Velocity);
        Assert.Equal(sent.Attitude.X, got.Attitude.X, QuantisedPlaces);
        Assert.Equal(sent.Attitude.Y, got.Attitude.Y, QuantisedPlaces);
        Assert.Equal(sent.Attitude.Z, got.Attitude.Z, QuantisedPlaces);
        Assert.Equal(sent.Attitude.W, got.Attitude.W, QuantisedPlaces);
        Assert.Equal(sent.Throttle, got.Throttle, QuantisedPlaces);
        Assert.Equal(sent.Aileron, got.Aileron, QuantisedPlaces);
        Assert.Equal(sent.Elevator, got.Elevator, QuantisedPlaces);
        Assert.Equal(sent.Rudder, got.Rudder, QuantisedPlaces);
        Assert.True(got.Nitro);
    }

    // A stick held hard over must not come back on the other side of neutral. An unclamped
    // multiply into a 16-bit field is exactly what would do that.
    [Fact]
    public void AircraftStateClampsAnOvershootInsteadOfWrappingItsSign()
    {
        var sent = new AircraftStateMessage(
            0, 0, Vector3.Zero, new Quaternion(1.4f, 0f, 0f, 0f), Vector3.Zero,
            2f, -3f, 3f, 0f, false);

        Span<byte> buffer = stackalloc byte[AircraftStateMessage.Size];
        sent.Write(buffer);

        Assert.True(AircraftStateMessage.TryRead(buffer, out var got));
        Assert.Equal(1f, got.Attitude.X, QuantisedPlaces);
        Assert.Equal(1f, got.Throttle, QuantisedPlaces);
        Assert.Equal(-1f, got.Aileron, QuantisedPlaces);
        Assert.Equal(1f, got.Elevator, QuantisedPlaces);
    }

    [Fact]
    public void FireRoundTripsItsOriginAimAndLock()
    {
        var sent = new FireMessage(
            Seat: 1,
            Weapon: 2,
            Sequence: 7,
            Origin: new Vector3(10f, -20f, 30.5f),
            Direction: new Vector3(0f, 0f, -1f),
            TargetSeat: NetMessage.NoSeat);

        Span<byte> buffer = stackalloc byte[FireMessage.Size];
        int written = sent.Write(buffer);

        Assert.Equal(FireMessage.Size, written);
        Assert.True(FireMessage.TryRead(buffer, out var got));
        Assert.Equal(sent.Seat, got.Seat);
        Assert.Equal(sent.Weapon, got.Weapon);
        Assert.Equal(sent.Sequence, got.Sequence);
        Assert.Equal(sent.Origin, got.Origin);
        Assert.Equal(sent.Direction.Z, got.Direction.Z, QuantisedPlaces);
        Assert.Equal(NetMessage.NoSeat, got.TargetSeat);
    }

    [Fact]
    public void HitRoundTripsTheShootersClaim()
    {
        var sent = new HitMessage(VictimSeat: 2, ShooterSeat: 0, Weapon: 5, Damage: 0.75f,
            Part: 3, LocalImpact: new Vector3(0.5f, -1.25f, 2f));

        Span<byte> buffer = stackalloc byte[HitMessage.Size];
        int written = sent.Write(buffer);

        Assert.Equal(HitMessage.Size, written);
        Assert.True(HitMessage.TryRead(buffer, out var got));
        Assert.Equal(sent, got);
    }

    [Fact]
    public void DamageRoundTripsTheVictimsHullState()
    {
        var sent = new DamageMessage(Seat: 4, Stage: 2, Flags: 0x0003, Hull: 37.25f);

        Span<byte> buffer = stackalloc byte[DamageMessage.Size];
        int written = sent.Write(buffer);

        Assert.Equal(DamageMessage.Size, written);
        Assert.True(DamageMessage.TryRead(buffer, out var got));
        Assert.Equal(sent, got);
    }

    // The original's 0x12 shape: a killer beside the victim and a cause word. The no-killer case
    // is the marker, not seat 0, because seat 0 is a real seat.
    [Theory]
    [InlineData(1, NetDeathCause.Killer, 0u)]
    [InlineData((int)NetMessage.NoSeat, NetDeathCause.Suicide, 0u)]
    [InlineData(2, NetDeathCause.ZeppelinPart, 0x1234u)]
    [InlineData(3, NetDeathCause.TurretOwner, 0xdeadbeefu)]
    public void DeathRoundTripsEveryCause(int killer, NetDeathCause cause, uint source)
    {
        var sent = new DeathMessage(VictimSeat: 5, KillerSeat: (byte)killer, cause, source);

        Span<byte> buffer = stackalloc byte[DeathMessage.Size];
        int written = sent.Write(buffer);

        Assert.Equal(DeathMessage.Size, written);
        Assert.True(DeathMessage.TryRead(buffer, out var got));
        Assert.Equal(sent, got);
    }

    [Fact]
    public void SpawnRoundTripsTheHostsPlacement()
    {
        var sent = new SpawnMessage(Seat: 6, Kind: NetSpawnKind.Respawn, EntryIndex: 11);

        Span<byte> buffer = stackalloc byte[SpawnMessage.Size];
        int written = sent.Write(buffer);

        Assert.Equal(SpawnMessage.Size, written);
        Assert.True(SpawnMessage.TryRead(buffer, out var got));
        Assert.Equal(sent, got);
    }

    [Fact]
    public void SpawnRequestRoundTripsTheAskingSeat()
    {
        var sent = new SpawnRequestMessage(Seat: 5);

        Span<byte> buffer = stackalloc byte[SpawnRequestMessage.Size];
        int written = sent.Write(buffer);

        Assert.Equal(SpawnRequestMessage.Size, written);
        Assert.True(SpawnRequestMessage.TryRead(buffer, out var got));
        Assert.Equal(sent, got);
    }

    // A penalty has to survive the wire as a negative, because the score is the number the kill
    // target is compared against.
    [Fact]
    public void ScoreRoundTripsANegativeTotal()
    {
        var sent = new ScoreMessage(Seat: 7, Score: -3, Kills: 4, Deaths: 7);

        Span<byte> buffer = stackalloc byte[ScoreMessage.Size];
        int written = sent.Write(buffer);

        Assert.Equal(ScoreMessage.Size, written);
        Assert.True(ScoreMessage.TryRead(buffer, out var got));
        Assert.Equal(sent, got);
        Assert.Equal(-3, got.Score);
    }

    [Fact]
    public void MatchStateRoundTripsTheClockAndItsEnding()
    {
        var sent = new MatchStateMessage(
            RemainingSeconds: 123.5f, TimeLimitSeconds: 300f, ScoreTarget: 10,
            End: NetMatchEnd.TimeLimit, HostClock: 176.5f);

        Span<byte> buffer = stackalloc byte[MatchStateMessage.Size];
        int written = sent.Write(buffer);

        Assert.Equal(MatchStateMessage.Size, written);
        Assert.True(MatchStateMessage.TryRead(buffer, out var got));
        Assert.Equal(sent, got);
        Assert.Equal(176.5f, got.HostClock);
    }

    [Fact]
    public void DirectorTransitionRoundTripsItsCodeIdAndStamp()
    {
        var sent = new DirectorTransitionMessage(Code: 2, Id: -1, HostClock: 412.25f);

        Span<byte> buffer = stackalloc byte[DirectorTransitionMessage.Size];
        int written = sent.Write(buffer);

        Assert.Equal(16, written);
        Assert.True(DirectorTransitionMessage.TryRead(buffer, out var got));
        Assert.Equal(sent, got);
        Assert.Equal(412.25f, got.HostClock);
    }

    [Fact]
    public void DirectorTransitionOfTheOldWidthIsRejected()
    {
        Span<byte> buffer = stackalloc byte[DirectorTransitionMessage.Size];
        new DirectorTransitionMessage(Code: 2, Id: 7, HostClock: 1f).Write(buffer);

        Assert.False(DirectorTransitionMessage.TryRead(buffer[..12], out _));
    }

    [Fact]
    public void SeatRosterRoundTripsEverySeatAndTheSeed()
    {
        var seats = new List<NetSeatEntry>
        {
            new(0, 1, 3, true, "Falcon"),
            new(1, 2, 0, false, "Nathan Zachary"),
            new(2, 2, 7, false, string.Empty),
        };
        var sent = new SeatRosterMessage(0xc0ffee01u, seats);

        var buffer = new byte[SeatRosterMessage.SizeFor(seats.Count)];
        int written = sent.Write(buffer);

        Assert.Equal(buffer.Length, written);
        Assert.True(SeatRosterMessage.TryRead(buffer, out var got));
        Assert.Equal(sent.Seed, got.Seed);
        Assert.Equal(seats.Count, got.Seats.Count);
        for (int i = 0; i < seats.Count; i++)
            Assert.Equal(seats[i], got.Seats[i]);
    }

    // The callsign field is fixed width, so a long name has to lose its tail rather than the
    // roster losing its alignment.
    [Fact]
    public void SeatRosterTruncatesAnOverLongCallsignAndKeepsItsWidth()
    {
        var seats = new List<NetSeatEntry> { new(0, 0, 0, false, new string('x', 40)) };
        var sent = new SeatRosterMessage(1u, seats);

        var buffer = new byte[SeatRosterMessage.SizeFor(1)];
        int written = sent.Write(buffer);

        Assert.Equal(SeatRosterMessage.SizeFor(1), written);
        Assert.True(SeatRosterMessage.TryRead(buffer, out var got));
        Assert.Equal(new string('x', SeatRosterMessage.CallsignBytes - 1), got.Seats[0].Callsign);
    }

    [Fact]
    public void AnEmptyRosterIsStillAWholeMessage()
    {
        var sent = new SeatRosterMessage(9u, Array.Empty<NetSeatEntry>());

        var buffer = new byte[SeatRosterMessage.PrefixSize];
        int written = sent.Write(buffer);

        Assert.Equal(SeatRosterMessage.PrefixSize, written);
        Assert.True(SeatRosterMessage.TryRead(buffer, out var got));
        Assert.Equal(9u, got.Seed);
        Assert.Empty(got.Seats);
    }

    // The join's first payload. The seed is a full 64 bits and the clock a full double, so both
    // ride as pairs of 32-bit words. A truncation would put the two peers on different dice.
    [Fact]
    public void TheHandshakeRoundTripsTheWholeSeedTheClockAndTheSeat()
    {
        var sent = new HandshakeMessage(0xfedcba9876543210UL, 1234.56789, 3);

        Span<byte> buffer = stackalloc byte[HandshakeMessage.Size];
        int written = sent.Write(buffer);

        Assert.Equal(HandshakeMessage.Size, written);
        Assert.True(HandshakeMessage.TryRead(buffer, out var got));
        Assert.Equal(sent, got);
        Assert.True(NetMessage.TryReadHeader(buffer, out var type, out int length));
        Assert.Equal(NetMessageType.Handshake, type);
        Assert.Equal(HandshakeMessage.Size, length);
    }

    // A guest with no seat yet is a real state, not a malformed message. NoSeat has to survive
    // the trip, so the field reads as "not seated" rather than as seat 255.
    [Fact]
    public void TheHandshakeCarriesTheNoSeatMarkerIntact()
    {
        Span<byte> buffer = stackalloc byte[HandshakeMessage.Size];
        new HandshakeMessage(0UL, 0.0, NetMessage.NoSeat).Write(buffer);

        Assert.True(HandshakeMessage.TryRead(buffer, out var got));
        Assert.Equal(NetMessage.NoSeat, got.Seat);
        Assert.False(SeatRosterMessage.TryRead(buffer, out _));
    }

    // What a receiver does first: read the header, then hand the buffer to the deserialiser for
    // that type. Nothing else in the vocabulary has to be parsed to route a packet.
    [Fact]
    public void TheHeaderNamesTheTypeAndTheWholeLength()
    {
        Span<byte> buffer = stackalloc byte[HitMessage.Size];
        new HitMessage(1, 2, 3, 1f, -1, Vector3.Zero).Write(buffer);

        Assert.True(NetMessage.TryReadHeader(buffer, out var type, out int length));
        Assert.Equal(NetMessageType.Hit, type);
        Assert.Equal(HitMessage.Size, length);
    }

    [Fact]
    public void ADeserialiserRefusesAnotherTypesBuffer()
    {
        Span<byte> buffer = stackalloc byte[DamageMessage.Size];
        new DamageMessage(1, 1, 0, 50f).Write(buffer);

        Assert.False(HitMessage.TryRead(buffer, out _));
        Assert.True(DamageMessage.TryRead(buffer, out _));
    }

    [Fact]
    public void ADeserialiserRefusesATruncatedBuffer()
    {
        var buffer = new byte[AircraftStateMessage.Size];
        new AircraftStateMessage(
            0, 1, Vector3.One, Quaternion.Identity, Vector3.Zero, 0.5f, 0f, 0f, 0f, false)
            .Write(buffer);

        Assert.False(AircraftStateMessage.TryRead(buffer.AsSpan(0, AircraftStateMessage.Size - 1), out _));
        Assert.False(NetMessage.TryReadHeader(buffer.AsSpan(0, 2), out _, out _));
    }

    // A declared seat count that disagrees with the bytes present is the variable-length
    // message's failure mode. It has to be refused, not read past.
    [Fact]
    public void ARosterWhoseCountDisagreesWithItsLengthIsRefused()
    {
        var seats = new List<NetSeatEntry> { new(0, 0, 0, false, "A"), new(1, 0, 0, false, "B") };
        var buffer = new byte[SeatRosterMessage.SizeFor(2)];
        new SeatRosterMessage(1u, seats).Write(buffer);
        buffer[NetMessage.HeaderBytes + 4] = 3;

        Assert.False(SeatRosterMessage.TryRead(buffer, out _));
    }

    [Fact]
    public void EveryTypeDeclaresTheReliabilityItsSenderNeeds()
    {
        Assert.Equal(NetReliability.UnreliableSequenced, NetMessage.ReliabilityOf(NetMessageType.AircraftState));
        Assert.Equal(NetReliability.Unreliable, NetMessage.ReliabilityOf(NetMessageType.Fire));
        Assert.Equal(NetReliability.Reliable, NetMessage.ReliabilityOf(NetMessageType.Hit));
        Assert.Equal(NetReliability.Reliable, NetMessage.ReliabilityOf(NetMessageType.Damage));
        Assert.Equal(NetReliability.Reliable, NetMessage.ReliabilityOf(NetMessageType.Death));
        Assert.Equal(NetReliability.Reliable, NetMessage.ReliabilityOf(NetMessageType.Spawn));
        Assert.Equal(NetReliability.Reliable, NetMessage.ReliabilityOf(NetMessageType.Score));
        Assert.Equal(NetReliability.Reliable, NetMessage.ReliabilityOf(NetMessageType.MatchState));
        Assert.Equal(NetReliability.Reliable, NetMessage.ReliabilityOf(NetMessageType.SeatRoster));
        Assert.Equal(NetReliability.Reliable, NetMessage.ReliabilityOf(NetMessageType.DirectorTransition));
        Assert.Equal(NetReliability.Reliable, NetMessage.ReliabilityOf(NetMessageType.Handshake));
        Assert.Equal(NetReliability.Unreliable, NetMessage.ReliabilityOf(NetMessageType.AiState));
        Assert.Equal(NetReliability.Unreliable, NetMessage.ReliabilityOf(NetMessageType.AiFire));
        Assert.Equal(NetReliability.Reliable, NetMessage.ReliabilityOf(NetMessageType.AiHit));
        Assert.Equal(NetReliability.Reliable, NetMessage.ReliabilityOf(NetMessageType.WorldEvent));
        Assert.Equal(NetReliability.Reliable, NetMessage.ReliabilityOf(NetMessageType.SessionAdvert));
        Assert.Equal(NetReliability.Unreliable, NetMessage.ReliabilityOf(NetMessageType.ClockPing));
        Assert.Throws<ArgumentOutOfRangeException>(() => NetMessage.ReliabilityOf((NetMessageType)0x7fff));
    }

    // Every AI shares one channel, so the state must reach the pose buffer with its own per-AI
    // sequence intact. The buffer is where a stale sample is dropped.
    [Fact]
    public void AiStateRoundTripsAndConvertsForThePoseBuffer()
    {
        var sent = new AiStateMessage(
            Ai: 513, Sequence: 65000, Position: new Vector3(-10.5f, 250f, 3000.25f),
            Attitude: new Quaternion(0f, 0.6f, 0f, 0.8f), Velocity: new Vector3(0f, -2f, 95.5f),
            Throttle: 0.4f, Aileron: 0.5f, Elevator: -0.25f, Rudder: 0f, Nitro: true);

        Span<byte> buffer = stackalloc byte[64];
        int written = sent.Write(buffer);

        Assert.Equal(AiStateMessage.Size, written);
        Assert.True(AiStateMessage.TryRead(buffer[..written], out var got));
        Assert.Equal(sent.Ai, got.Ai);
        Assert.Equal(sent.Sequence, got.Sequence);
        Assert.Equal(sent.Position, got.Position);
        Assert.Equal(sent.Velocity, got.Velocity);
        Assert.Equal(sent.Attitude.Y, got.Attitude.Y, QuantisedPlaces);
        Assert.Equal(sent.Attitude.W, got.Attitude.W, QuantisedPlaces);
        Assert.Equal(sent.Throttle, got.Throttle, QuantisedPlaces);
        Assert.Equal(sent.Elevator, got.Elevator, QuantisedPlaces);
        Assert.True(got.Nitro);
        var sample = got.AsAircraftState();
        Assert.Equal(NetMessage.NoSeat, sample.Seat);
        Assert.Equal(sent.Sequence, sample.Sequence);
        Assert.Equal(sent.Position, sample.Position);
    }

    [Fact]
    public void AiFireAndAiHitRoundTrip()
    {
        Span<byte> buffer = stackalloc byte[64];
        var fire = new AiFireMessage(7, 12, new Vector3(1f, 2f, 3f), new Vector3(0f, 0f, -1f));
        Assert.Equal(AiFireMessage.Size, fire.Write(buffer));
        Assert.True(AiFireMessage.TryRead(buffer[..AiFireMessage.Size], out var gotFire));
        Assert.Equal(fire.Ai, gotFire.Ai);
        Assert.Equal(fire.Weapon, gotFire.Weapon);
        Assert.Equal(fire.Origin, gotFire.Origin);
        Assert.Equal(-1f, gotFire.Direction.Z, QuantisedPlaces);

        var hit = new AiHitMessage(300, 2, 44, 0.75f, -1, new Vector3(0.5f, -1.25f, 3f));
        Assert.Equal(AiHitMessage.Size, hit.Write(buffer));
        Assert.True(AiHitMessage.TryRead(buffer[..AiHitMessage.Size], out var gotHit));
        Assert.Equal(hit, gotHit);
        Assert.False(HitMessage.TryRead(buffer[..AiHitMessage.Size], out _));
    }

    // The original's per-zeppelin record order, position then speed, pitch and yaw, at 32 bytes.
    [Fact]
    public void ZeppelinStateRoundTripsInTheOriginalRecordOrder()
    {
        Span<byte> buffer = stackalloc byte[64];
        var sent = new ZeppelinStateMessage(3, 65001, new Vector3(-6246f, 93.5f, -7651f), 22.5f, -0.05f, 2.75f);
        Assert.Equal(ZeppelinStateMessage.Size, sent.Write(buffer));
        Assert.True(ZeppelinStateMessage.TryRead(buffer[..ZeppelinStateMessage.Size], out var got));
        Assert.Equal(sent, got);
        Assert.Equal(NetReliability.Unreliable, NetMessage.ReliabilityOf(NetMessageType.ZeppelinState));
        Assert.Equal(0x004B, (int)NetMessageType.ZeppelinState);
        Assert.False(NetMessage.IsOriginalId(NetMessageType.ZeppelinState));
        Assert.False(AiStateMessage.TryRead(buffer[..ZeppelinStateMessage.Size], out _));
    }

    [Fact]
    public void WorldEventRoundTripsANegativeArgument()
    {
        Span<byte> buffer = stackalloc byte[WorldEventMessage.Size];
        var sent = new WorldEventMessage((ushort)NetWorldEvent.DestructibleHealth, 1234, -559038737, 12.5f);
        Assert.Equal(WorldEventMessage.Size, sent.Write(buffer));
        Assert.True(WorldEventMessage.TryRead(buffer, out var got));
        Assert.Equal(sent, got);
    }

    // Which ids are the original's and which this remake minted. A capture of an original packet
    // and a remake packet must not be confused for one another.
    [Fact]
    public void TheMintedIdsStandAboveEveryIdTheOriginalUses()
    {
        Assert.True(NetMessage.IsOriginalId(NetMessageType.AircraftState));
        Assert.True(NetMessage.IsOriginalId(NetMessageType.Fire));
        Assert.True(NetMessage.IsOriginalId(NetMessageType.Death));
        Assert.True(NetMessage.IsOriginalId(NetMessageType.Score));
        Assert.True(NetMessage.IsOriginalId(NetMessageType.MatchState));
        Assert.True(NetMessage.IsOriginalId(NetMessageType.Hit));
        Assert.True(NetMessage.IsOriginalId(NetMessageType.SeatRoster));
        Assert.False(NetMessage.IsOriginalId(NetMessageType.Damage));
        Assert.False(NetMessage.IsOriginalId(NetMessageType.Spawn));
        Assert.False(NetMessage.IsOriginalId(NetMessageType.DirectorTransition));
        Assert.False(NetMessage.IsOriginalId(NetMessageType.Handshake));
        Assert.False(NetMessage.IsOriginalId(NetMessageType.AiState));
        Assert.False(NetMessage.IsOriginalId(NetMessageType.WorldEvent));
        Assert.False(NetMessage.IsOriginalId(NetMessageType.SessionAdvert));
        Assert.False(NetMessage.IsOriginalId(NetMessageType.ClockPing));
    }

    [Fact]
    public void SessionAdvertRoundTripsAndReadsItsChapterAndMission()
    {
        Span<byte> buffer = stackalloc byte[SessionAdvertMessage.Size];
        var sent = new SessionAdvertMessage(NetSessionKind.CampaignCoop, 23, 5, "Zachary");
        Assert.Equal(SessionAdvertMessage.Size, sent.Write(buffer));
        Assert.True(SessionAdvertMessage.TryRead(buffer, out var got));
        Assert.Equal(sent, got);
        Assert.Equal(NetReliability.Reliable, NetMessage.ReliabilityOf(NetMessageType.SessionAdvert));

        // The last mission of the campaign is the fourth of chapter five.
        Assert.Equal(5, got.Chapter);
        Assert.Equal(4, got.MissionInChapter);

        var dogfight = new SessionAdvertMessage(NetSessionKind.Dogfight, SessionAdvertMessage.NoMission, 2, "");
        Assert.False(dogfight.HasMission);
        Assert.Equal(0, dogfight.Chapter);
    }

    [Fact]
    public void ASessionAdvertOfAnUnknownKindReadsAsUnknownAndOthersAreNotOne()
    {
        Span<byte> buffer = stackalloc byte[SessionAdvertMessage.Size];
        new SessionAdvertMessage((NetSessionKind)9, 0, 1, "h").Write(buffer);
        Assert.True(SessionAdvertMessage.TryRead(buffer, out var got));
        Assert.Equal(NetSessionKind.Unknown, got.Kind);

        Span<byte> handshake = stackalloc byte[HandshakeMessage.Size];
        new HandshakeMessage(1, 2.0, 3).Write(handshake);
        Assert.False(SessionAdvertMessage.TryRead(handshake, out _));
    }

    // The original's ping width: the header and two stamps.
    [Fact]
    public void AClockPingRoundTripsBothStampsInTheOriginalsTwelveBytes()
    {
        Span<byte> buffer = stackalloc byte[32];
        var sent = new ClockPingMessage(1234.5f, 98765.25f);
        Assert.Equal(12, sent.Write(buffer));
        Assert.Equal(ClockPingMessage.Size, sent.Write(buffer));
        Assert.True(ClockPingMessage.TryRead(buffer[..ClockPingMessage.Size], out var got));
        Assert.Equal(sent, got);
        Assert.Equal(0x49, (int)NetMessageType.ClockPing);
        Assert.False(ClockPingMessage.TryRead(buffer[..8], out _));
    }
}
