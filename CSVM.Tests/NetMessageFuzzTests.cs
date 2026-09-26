using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using CSVM.Net;
using Xunit;

namespace CSVM.Tests;

/// <summary>
/// Every message reader under bytes a peer could send, found by reflection over the
/// <see cref="INetMessage{TSelf}"/> implementations. A message added later is therefore fuzzed with
/// no edit here. A read must refuse the bytes or return a value that re-encodes to itself. It must
/// never throw, and never allocate past <see cref="AllocationBound"/>. The same bytes then go
/// through a lobby, a guest session and a host session with every type routed. None may throw or
/// hold a seat outside the tables.
/// </summary>
[Trait("Tier", "Quick")]
public sealed class NetMessageFuzzTests
{
    // The most one read may allocate. A full roster of sixteen malformed callsigns measures about
    // 1.2 KiB, the widest any reader needs; every fixed-width message allocates nothing.
    private const long AllocationBound = 2048;

    private const int FuzzSeed = 0x0D33;

    // Random bodies tried per length under a header naming the message. Enough that the roster's
    // count byte lands on a length it accepts for most of its sixteen sizes.
    private const int BodiesPerLength = 24;

    // How many payload bytes past the header a valid body is searched over, one at a time.
    private const int SweptBytes = 12;

    private static readonly int Largest = SeatRosterMessage.SizeFor(SeatRosterMessage.MaxSeats);
    private static readonly int Longest = 2 * Largest;
    private static readonly Lazy<Subject[]> Subjects = new(Enumerate);
    private static readonly Lazy<List<byte[]>> Valid = new(ValidEncodings);

    [Fact]
    public void The_enumeration_finds_a_reader_for_every_message_type()
    {
        var found = Subjects.Value.Select(s => s.Type).ToList();
        var declared = Enum.GetValues<NetMessageType>();

        Assert.Equal(declared.Length, found.Count);
        Assert.Equal(declared.OrderBy(t => t), found.OrderBy(t => t));
        Assert.Equal(found.Count, found.Distinct().Count());
    }

    [Fact]
    public void A_random_payload_of_every_length_is_refused_or_reads_back_whole()
    {
        var rng = new Random(FuzzSeed);
        var buffer = new byte[Longest];
        foreach (var subject in Subjects.Value)
        {
            for (int length = 0; length <= Longest; length++)
            {
                rng.NextBytes(buffer);
                Probe(subject, buffer.AsSpan(0, length), $"random {length} bytes");
            }
        }
    }

    [Fact]
    public void A_valid_header_over_random_bytes_is_refused_or_reads_back_whole()
    {
        var rng = new Random(FuzzSeed + 1);
        var buffer = new byte[Longest];
        var reading = new HashSet<NetMessageType>();
        foreach (var subject in Subjects.Value)
        {
            for (int length = NetMessage.HeaderBytes; length <= Longest; length++)
            {
                for (int i = 0; i < BodiesPerLength; i++)
                {
                    rng.NextBytes(buffer);
                    Header(buffer, subject.Type, length);
                    if (Probe(subject, buffer.AsSpan(0, length), $"a {length}-byte header over random bytes"))
                    {
                        reading.Add(subject.Type);
                    }
                }
            }
        }

        // Able to fail: a fuzz every reader refused would prove nothing about the accepting path.
        // A reader that checks a kind or a count byte can refuse all random bodies. So this asks
        // for most readers, not all; the swept encodings below reach the rest.
        var refusing = Subjects.Value.Select(s => s.Type).Except(reading).ToList();
        Assert.True(reading.Count * 2 > Subjects.Value.Length, $"only {reading.Count} readers read a random body; none of {string.Join(", ", refusing)}");
    }

    [Fact]
    public void Every_truncated_prefix_of_a_valid_encoding_is_refused()
    {
        foreach (var bytes in Valid.Value)
        {
            var subject = SubjectFor(bytes);
            Assert.True(Probe(subject, bytes, "its own encoding"), $"{subject.Name} refused its own {bytes.Length}-byte encoding");
            for (int cut = 0; cut < bytes.Length; cut++)
            {
                Assert.False(subject.Read(bytes.AsSpan(0, cut)),
                    $"{subject.Name} read a {cut}-byte prefix of its {bytes.Length}-byte encoding");
            }
        }
    }

    [Fact]
    public void A_valid_body_under_a_wrong_length_word_is_refused_or_reads_back_whole()
    {
        var rng = new Random(FuzzSeed + 2);
        var buffer = new byte[Longest + 1];
        foreach (var bytes in Valid.Value)
        {
            var subject = SubjectFor(bytes);
            foreach (int length in WrongLengths(bytes.Length))
            {
                rng.NextBytes(buffer);
                bytes.CopyTo(buffer, 0);
                BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(2), (ushort)length);
                int span = Math.Clamp(length, bytes.Length, buffer.Length);
                Probe(subject, buffer.AsSpan(0, span), $"its body under length word {length}");
            }
        }
    }

    [Fact]
    public void A_valid_body_under_a_wrong_type_word_is_refused()
    {
        foreach (var bytes in Valid.Value)
        {
            var own = SubjectFor(bytes);
            var copy = (byte[])bytes.Clone();
            for (int word = 0; word <= ushort.MaxValue; word++)
            {
                if (word == (ushort)own.Type)
                {
                    continue;
                }

                BinaryPrimitives.WriteUInt16LittleEndian(copy, (ushort)word);
                Assert.False(own.Read(copy), $"{own.Name} read its body under type word 0x{word:X4}");
            }
        }
    }

    // A guest's lobby first, holding what arrives before a session binds, then the guest session it
    // replays into and every payload again. The join's own two readers reach this session's seats.
    [Fact]
    public void A_lobby_and_the_guest_session_behind_it_take_every_payload_and_keep_their_seats_in_range()
    {
        var mesh = LoopbackTransport.Mesh(2, LoopbackConditions.Perfect, new Random(FuzzSeed));
        var lobby = new NetLobby(mesh[1]);
        var payloads = Payloads(new Random(FuzzSeed + 3)).ToList();
        foreach (var payload in payloads)
        {
            lobby.OnPayload(Sender(payload), 0, payload);
            Assert.True(lobby.Held <= NetLobby.HeldPayloads, $"the lobby holds {lobby.Held} payloads");
        }

        var guest = NetSession.Guest(lobby, new[] { "player_pfighter" });
        foreach (var subject in Subjects.Value)
        {
            subject.Route(guest);
        }

        Assert.Equal(0, lobby.Held);
        AssertSeatsInRange(guest, "after the held payloads replayed");
        int replayed = guest.Received;
        for (int i = 0; i < payloads.Count; i++)
        {
            lobby.OnPayload(Sender(payloads[i]), 0, payloads[i]);
            AssertSeatsInRange(guest, $"after payload {i}");
        }

        Assert.True(replayed > 0 && guest.Received > replayed, $"the guest session heard {replayed} held and {guest.Received} in all");
        Assert.True(guest.Malformed > 0, "no payload reached a reader and failed it, so the malformed path went unexercised");

        // A build version that does not play silences its sender, so it arrives from a peer of its
        // own and the host's stream stays heard.
        static int Sender(byte[] payload) => BuildVersionMessage.TryRead(payload, out _) ? 7 : 0;
    }

    // A host with every type routed and relayed, the widest set of listeners one session can
    // carry. Its roster is its own, so no arrival may change it.
    [Fact]
    public void A_host_session_with_every_type_routed_and_relayed_takes_every_payload()
    {
        var mesh = LoopbackTransport.Mesh(3, LoopbackConditions.Perfect, new Random(FuzzSeed));
        var roster = new NetSeat[]
        {
            new() { PeerId = 0, SeatIndex = 0, IsLocal = true, Callsign = "host" },
            new() { PeerId = 1, SeatIndex = 1, Callsign = "first" },
            new() { PeerId = 2, SeatIndex = 2, Callsign = "second" },
        };
        var host = NetSession.Host(mesh[0], roster, 7UL);
        foreach (var subject in Subjects.Value)
        {
            subject.Route(host);
            subject.Relay(host);
        }

        var payloads = Payloads(new Random(FuzzSeed + 4)).ToList();
        foreach (var payload in payloads)
        {
            host.OnPayload(1, 0, payload);
        }

        Assert.Equal(payloads.Count, host.Received);
        Assert.Equal(0, host.LocalSeat);
        Assert.Equal(new[] { 0, 1, 2 }, host.Seats.Select(s => s.SeatIndex));
        Assert.True(host.Relayed > 0, "no fuzz payload was relayed, so the relays went unexercised");
        for (int seat = 0; seat <= byte.MaxValue; seat++)
        {
            _ = host.PeerOfSeat(seat);
        }
    }

    // Reads one payload with the allocation measured, and checks what it accepted. True when it read.
    private static bool Probe(Subject subject, ReadOnlySpan<byte> bytes, string what)
    {
        long before = GC.GetAllocatedBytesForCurrentThread();
        bool read = subject.Read(bytes);
        long used = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(used <= AllocationBound, $"{subject.Name} allocated {used} bytes reading {what}");
        if (read)
        {
            AssertReadsBackWhole(subject, bytes, what);
        }

        return read;
    }

    // What a reader accepts must be a value, not a view of the bytes. Written out, it is exactly as
    // long as its header claimed. Read again, it writes out the same bytes.
    private static void AssertReadsBackWhole(Subject subject, ReadOnlySpan<byte> bytes, string what)
    {
        var once = new byte[Longest];
        var twice = new byte[Longest];
        int declared = BinaryPrimitives.ReadUInt16LittleEndian(bytes[2..]);
        int first = subject.ReadAndWrite(bytes, once);
        Assert.True(first == declared, $"{subject.Name} read {what} as a {first}-byte message under a {declared}-byte header");
        int second = subject.ReadAndWrite(once.AsSpan(0, first), twice);
        Assert.True(second == first && once.AsSpan(0, first).SequenceEqual(twice.AsSpan(0, second)),
            $"{subject.Name} read {what} as a value that does not re-encode to itself");
    }

    private static void AssertSeatsInRange(NetSession session, string when)
    {
        Assert.True(session.LocalSeat == NetMessage.NoSeat || session.LocalSeat < NetSeats.SeatCapacity,
            $"the guest's own seat is {session.LocalSeat} {when}");
        var indices = session.Seats.Select(s => s.SeatIndex).ToList();
        Assert.True(indices.All(i => i >= 0 && i < session.Seats.Count) && indices.Distinct().Count() == indices.Count,
            $"the guest's roster is numbered {string.Join(",", indices)} {when}");
        if (session.Joined)
        {
            NetSeats.Validate(session.Seats);
        }
    }

    // Every payload the reader facts above feed a reader, as one stream for the listeners.
    private static IEnumerable<byte[]> Payloads(Random rng)
    {
        foreach (var bytes in Valid.Value)
        {
            yield return bytes;
            yield return bytes[..^1];
        }

        foreach (var subject in Subjects.Value)
        {
            for (int length = 0; length <= Longest; length += 7)
            {
                var random = new byte[length];
                rng.NextBytes(random);
                yield return random;
                if (length >= NetMessage.HeaderBytes)
                {
                    var headed = (byte[])random.Clone();
                    Header(headed, subject.Type, length);
                    yield return headed;
                }
            }
        }
    }

    private static IEnumerable<int> WrongLengths(int right)
    {
        for (int length = 0; length <= Longest; length++)
        {
            if (length != right)
            {
                yield return length;
            }
        }

        yield return ushort.MaxValue;
    }

    private static void Header(byte[] into, NetMessageType type, int length)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(into, (ushort)type);
        BinaryPrimitives.WriteUInt16LittleEndian(into.AsSpan(2), (ushort)length);
    }

    private static Subject SubjectFor(byte[] bytes) =>
        Subjects.Value.Single(s => (ushort)s.Type == BinaryPrimitives.ReadUInt16LittleEndian(bytes));

    // Each message's default written out, and for every length a reader accepts, one random body
    // rewritten. A body is found by sweeping each of its first bytes through every value. That is
    // how a count or a kind byte anywhere near the header is hit. Canonical encodings, so a
    // truncation or a changed word is the only fault in them.
    private static List<byte[]> ValidEncodings()
    {
        var rng = new Random(FuzzSeed + 5);
        var encodings = new List<byte[]>();
        var buffer = new byte[Longest];
        var into = new byte[Longest];
        foreach (var subject in Subjects.Value)
        {
            int written = subject.WriteDefault(into);
            encodings.Add(into[..written]);
            for (int length = NetMessage.HeaderBytes; length <= Largest; length++)
            {
                rng.NextBytes(buffer);
                Header(buffer, subject.Type, length);
                int rewritten = FirstAccepted(subject, buffer.AsSpan(0, length), into);
                if (rewritten > 0)
                {
                    encodings.Add(into[..rewritten]);
                }
            }
        }

        return encodings;
    }

    private static int FirstAccepted(Subject subject, Span<byte> bytes, Span<byte> into)
    {
        int written = subject.ReadAndWrite(bytes, into);
        for (int at = NetMessage.HeaderBytes; written < 0 && at < Math.Min(bytes.Length, NetMessage.HeaderBytes + SweptBytes); at++)
        {
            byte kept = bytes[at];
            for (int value = 0; written < 0 && value <= byte.MaxValue; value++)
            {
                bytes[at] = (byte)value;
                written = subject.ReadAndWrite(bytes, into);
            }

            bytes[at] = written < 0 ? kept : bytes[at];
        }

        return written;
    }

    private static Subject[] Enumerate()
    {
        var open = typeof(INetMessage<>);
        return typeof(INetMessage<>).Assembly.GetTypes()
            .Where(t => t.IsValueType && t.GetInterfaces().Any(i => i.IsGenericType && i.GetGenericTypeDefinition() == open))
            .Select(t => (Subject)Activator.CreateInstance(typeof(Subject<>).MakeGenericType(t))!)
            .ToArray();
    }

    // One message type behind a plain virtual call. A read in the loops above then costs no
    // reflection, and the allocation measured is the reader's own.
    private abstract class Subject
    {
        public abstract NetMessageType Type { get; }

        public abstract string Name { get; }

        public abstract bool Read(ReadOnlySpan<byte> bytes);

        // The accepted value written out, or -1 when the bytes were refused.
        public abstract int ReadAndWrite(ReadOnlySpan<byte> bytes, Span<byte> into);

        public abstract int WriteDefault(Span<byte> into);

        public abstract void Route(NetSession session);

        public abstract void Relay(NetSession host);
    }

    private sealed class Subject<T> : Subject
        where T : struct, INetMessage<T>
    {
        public override NetMessageType Type => T.Type;

        public override string Name => typeof(T).Name;

        public override bool Read(ReadOnlySpan<byte> bytes) => T.TryRead(bytes, out _);

        public override int ReadAndWrite(ReadOnlySpan<byte> bytes, Span<byte> into) =>
            T.TryRead(bytes, out var message) ? message.Write(into) : -1;

        public override int WriteDefault(Span<byte> into) => default(T).Write(into);

        // The join's two types are the session's own and cannot be registered over.
        public override void Route(NetSession session)
        {
            if (T.Type is not (NetMessageType.Handshake or NetMessageType.SeatRoster))
            {
                session.On<T>((_, _) => { });
            }
        }

        // Every type relayed, and each seat-addressed one by the first byte its reader hands back.
        public override void Relay(NetSession host)
        {
            if (T.Type == NetMessageType.Hit)
            {
                host.RelayToSeatOwner<T>(message => message is HitMessage hit ? hit.VictimSeat : NetMessage.NoSeat);
            }
            else
            {
                host.RelayToOthers<T>();
            }
        }
    }
}
