using System;
using System.Collections.Generic;
using CSVM.Flight;
using CSVM.Mech3;
using CSVM.Net;
using CSVM.Utils;
using Godot;

namespace CSVM.Session;

/// <summary>
/// The host-owned world over the wire: AI aircraft, zeppelin paths, surface-vehicle patrols, warps
/// and destructible pools. The host flies every AI, zeppelin and hull, draws every warp and decides
/// every world hit, then says what happened. A guest's AI flies <see cref="AiStateMessage"/>, its
/// zeppelins and hulls chase their state messages, and its pools spend nothing of their own. Both
/// ends must admit AI and place zeppelins in the same order, since those indices name them. A guest
/// builds a generator's aircraft only when the host's <see cref="AiSpawnMessage"/> names its
/// ordinal, and a hull's index is checked against its name. The phase mapping is in
/// <c>docs/org/multiplayer-messages.md</c>.
/// </summary>
internal sealed class NetWorldLink
{
    /// <summary>Sim steps between two zeppelin samples, the original's half second at the fixed
    /// step.</summary>
    internal static readonly int ZeppelinSendSteps =
        Math.Max(1, (int)Math.Round(ZeppelinReplica.SendSeconds / Utils.GameClock.FixedDt));

    /// <summary>Sim steps between two surface-vehicle samples. The original sends no hull state, so
    /// a hull takes the zeppelin's cadence, the one world-hull stream it does send.</summary>
    internal static readonly int SurfaceSendSteps = ZeppelinSendSteps;

    private readonly NetSession _net;
    private readonly NetWorldSeats _seats;
    private readonly AnimRuntime? _world;
    private readonly List<FlightController> _admitted = new();
    private readonly List<ushort> _sequence = new();
    private readonly List<ushort> _zeppelinSequence = new();
    private readonly List<ushort> _surfaceSequence = new();
    private readonly List<DestructibleRegistry.Instance> _chipped = new();
    private ZeppelinRuntime? _zeppelins;
    private SurfaceVehicleRuntime? _surface;
    private CampaignDirector? _director;
    private AiGeneratorRuntime? _generators;
    private Func<IReadOnlyList<FlightController>>? _roster;
    private int _steps;

    /// <summary>Wires one session's end. A host hears the guests' hit claims and publishes its
    /// world pools' stage changes. A guest hands its pools over and applies what arrives.</summary>
    internal NetWorldLink(NetSession net, NetWorldSeats seats, AnimRuntime? world)
    {
        ArgumentNullException.ThrowIfNull(net);
        ArgumentNullException.ThrowIfNull(seats);
        _net = net;
        _seats = seats;
        _world = world;
        if (net.IsHost)
        {
            net.On<AiHitMessage>((_, hit) => TakeAiHit(hit));
            if (world != null)
            {
                world.DestructibleDamaged += SendDestructible;
                world.DestructibleChipped += MarkChipped;
            }

            return;
        }

        if (world != null)
        {
            world.DamageReplicated = true;
        }

        net.On<AiStateMessage>((_, state) => TakeAiState(state));
        net.On<AiFireMessage>((_, fire) => TakeAiFire(fire));
        net.On<WorldEventMessage>((_, e) => TakeWorldEvent(e));
    }

    /// <summary>How many AI aircraft this end has admitted, which is also the next ordinal.</summary>
    internal int Admitted => _admitted.Count;

    /// <summary>The world runtime whose pools this end publishes or follows, or null.</summary>
    internal AnimRuntime? World => _world;

    /// <summary>World events a guest has applied, of every code.</summary>
    internal int WorldEventsApplied { get; private set; }

    /// <summary>AI hit claims the host has spent.</summary>
    internal int AiHitsTaken { get; private set; }

    /// <summary>Zeppelin samples the host has put on the wire.</summary>
    internal int ZeppelinSamplesSent { get; private set; }

    /// <summary>Zeppelin samples a guest has taken into a replica.</summary>
    internal int ZeppelinSamplesTaken { get; private set; }

    /// <summary>Surface-vehicle samples the host has put on the wire.</summary>
    internal int SurfaceSamplesSent { get; private set; }

    /// <summary>Surface-vehicle samples a guest has taken into a hull.</summary>
    internal int SurfaceSamplesTaken { get; private set; }

    /// <summary>Pool health samples between stages the host has put on the wire, at most one per
    /// chipped pool per send.</summary>
    internal int ChipSamplesSent { get; private set; }

    /// <summary>Generator aircraft launches the host has put on the wire.</summary>
    internal int SpawnsSent { get; private set; }

    /// <summary>Host launches a guest has built at the host's ordinal.</summary>
    internal int SpawnsTaken { get; private set; }

    /// <summary>Host launches a guest could not build at the named ordinal, and so dropped.</summary>
    internal int SpawnsRefused { get; private set; }

    /// <summary>A stable name for one destructible pool, the guard a guest checks the host's
    /// registration index against. <see cref="NameKey"/> over the definition and anchor names.</summary>
    internal static int PoolKey(DestructibleRegistry.Instance inst)
    {
        ArgumentNullException.ThrowIfNull(inst);
        return NameKey($"{inst.Def.AnimName ?? inst.Def.Name}/{inst.Anchor.Name}");
    }

    /// <summary>A name's wire key, FNV-1a over the lower-cased name. A guest checks or finds by it
    /// what a host's index names when the two ends' lists have shifted apart.</summary>
    internal static int NameKey(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        string text = name.ToLowerInvariant();
        uint hash = 2166136261u;
        foreach (char c in text)
        {
            hash = (hash ^ c) * 16777619u;
        }

        return unchecked((int)hash);
    }

    /// <summary>The AI admitted at <paramref name="ordinal"/>, or null.</summary>
    internal FlightController? AiAt(int ordinal) =>
        ordinal >= 0 && ordinal < _admitted.Count ? _admitted[ordinal] : null;

    /// <summary>Puts the mission's zeppelins under the link. The host samples each path every
    /// <see cref="ZeppelinSendSteps"/> from <see cref="StepSends"/>; a guest hands every path over
    /// to the host (<see cref="ZeppelinRuntime.Replicate"/>) and steers to the samples. Call once
    /// the runtime has placed its hulls.</summary>
    internal void FollowZeppelins(ZeppelinRuntime zeppelins)
    {
        ArgumentNullException.ThrowIfNull(zeppelins);
        _zeppelins = zeppelins;
        if (_net.IsHost)
        {
            return;
        }

        zeppelins.Replicate();
        _net.On<ZeppelinStateMessage>((_, state) => TakeZeppelin(state));
    }

    /// <summary>Puts the mission's surface vehicles and its <c>WARP_VEHICLE</c> draws under the
    /// link; either may be null. The host samples each patrolling hull every
    /// <see cref="SurfaceSendSteps"/> and sends each warp it draws. A guest hands its hulls over
    /// (<see cref="SurfaceVehicleRuntime.Replicate"/>) and its warps wait for the host's draw.
    /// Call once the roster has placed its hulls.</summary>
    internal void FollowVehicles(SurfaceVehicleRuntime? hulls, CampaignDirector? director)
    {
        _surface = hulls;
        _director = director;
        if (_net.IsHost)
        {
            if (director != null)
            {
                director.WarpDrawn += SendWarp;
            }

            return;
        }

        director?.TakeWarpsFromHost();
        if (hulls != null)
        {
            hulls.Replicate();
            _net.On<SurfaceVehicleStateMessage>((_, state) => TakeSurface(state));
        }
    }

    /// <summary>Puts the mission's generator aircraft under the link. The host announces each
    /// launch with the ordinal it admits the aircraft at. A guest refuses its own cycles' aircraft
    /// (<see cref="AiGeneratorRuntime.Replicate"/>) and builds the host's instead.
    /// <paramref name="roster"/> is the AI list <see cref="Admit"/> is fed from.</summary>
    internal void FollowGenerators(AiGeneratorRuntime generators, Func<IReadOnlyList<FlightController>> roster)
    {
        ArgumentNullException.ThrowIfNull(generators);
        ArgumentNullException.ThrowIfNull(roster);
        _generators = generators;
        _roster = roster;
        if (_net.IsHost)
        {
            generators.AircraftLaunched += SendAiSpawn;
            return;
        }

        generators.Replicate();
        _net.On<AiSpawnMessage>((_, spawn) => TakeAiSpawn(spawn));
    }

    /// <summary>Admits every AI the roster gained since the last call, in roster order. Run at the
    /// start of each step, before any AI is stepped, so a guest's copy never flies a step of its
    /// own.</summary>
    internal void Admit(IReadOnlyList<FlightController> roster)
    {
        for (int i = _admitted.Count; i < roster.Count; i++)
        {
            var ai = roster[i];
            int ordinal = i;
            _admitted.Add(ai);
            _sequence.Add(0);
            if (_net.IsHost)
            {
                ai.WeaponFired += (weapon, origin, direction) => SendAiFire(ordinal, weapon, origin, direction);
                ai.Downed += (_, killer) => SendAiDowned(ordinal, killer);
                ai.DamageApplied += (hurt, _) => SendAiHull(ordinal, hurt);
                ai.InertChanged += changed => SendAiPresence(ordinal, changed);
                ai.HitRouter = HostRoutes;
            }
            else
            {
                ai.RemotePoses = new RemotePoseBuffer();
                ai.HitRouter = hit => GuestRoutes(ordinal, hit);
            }
        }
    }

    /// <summary>The host's AI state, run after the AI phase so a sample is this step's settled
    /// pose, on the seat stream's cadence; and every zeppelin's path on its own slower one.</summary>
    internal void StepSends()
    {
        if (!_net.IsHost)
        {
            return;
        }

        int step = _steps++;
        if (step % ZeppelinSendSteps == 0)
        {
            SendZeppelins();
        }

        if (step % SurfaceSendSteps == 0)
        {
            SendSurfaceVehicles();
        }

        if (step % AircraftStateCadence.SendStepInterval != 0)
        {
            return;
        }

        SendChipped();
        for (int i = 0; i < _admitted.Count; i++)
        {
            var ai = _admitted[i];
            if (ai.Inert || !GodotObject.IsInstanceValid(ai))
            {
                continue;
            }

            var stick = ai.LastCommand;
            _net.Broadcast(
                new AiStateMessage((ushort)i, _sequence[i]++, ai.WorldPosition,
                    ai.Attitude.GetRotationQuaternion(), ai.WorldVelocity, ai.Throttle,
                    stick.Roll, stick.Pitch, stick.Yaw, ai.Nitro.Boosting),
                NetChannels.Events);
        }
    }

    /// <summary>Tells every guest still here that <paramref name="seat"/>'s guest left the mission.
    /// Host only.</summary>
    internal void SendSeatLeft(int seat)
    {
        if (_net.IsHost && seat is >= 0 and <= ushort.MaxValue)
        {
            _net.Broadcast(new WorldEventMessage((ushort)NetWorldEvent.SeatLeft, (ushort)seat, 0, 0f), NetChannels.Events);
        }
    }

    // The host's index first, then a search by name. A pool registered at run time on one end
    // alone shifts every index after it, and the key still finds the right one.
    private static DestructibleRegistry.Instance? FindPool(
        IReadOnlyList<DestructibleRegistry.Instance> all, int index, int key)
    {
        if (index < all.Count && PoolKey(all[index]) == key)
        {
            return all[index];
        }

        foreach (var inst in all)
        {
            if (inst.Status != DestructibleRegistry.State.Destroyed && PoolKey(inst) == key)
            {
                return inst;
            }
        }

        return null;
    }

    private static int IndexOf(IReadOnlyList<DestructibleRegistry.Instance> all, DestructibleRegistry.Instance inst)
    {
        for (int i = 0; i < all.Count; i++)
        {
            if (ReferenceEquals(all[i], inst))
            {
                return i;
            }
        }

        return -1;
    }

    // The host decides every strike on an AI except a round a guest fired, whose own machine
    // decides it and sends the claim. Exactly one machine ever spends a hit.
    private bool HostRoutes(AircraftHit hit)
    {
        int seat = _seats.SeatOfShooter(hit.Shooter);
        return seat >= 0 && !_seats.IsLocal(seat);
    }

    // A guest spends nothing on an AI. A round of its own seats becomes a claim on the host.
    private bool GuestRoutes(int ordinal, AircraftHit hit)
    {
        int seat = _seats.SeatOfShooter(hit.Shooter);
        if (seat < 0 || !_seats.IsLocal(seat))
        {
            return true;
        }

        var pose = new Transform3D(hit.Victim.Attitude, hit.Victim.WorldPosition);
        _net.Send(_net.HostPeer,
            new AiHitMessage((ushort)ordinal, (byte)seat, (ushort)Math.Max(0, _seats.WeaponIndex(hit.Weapon)),
                hit.DamageScale, (short)hit.ShapeIndex, pose.AffineInverse() * hit.Impact),
            NetChannels.Events);
        return true;
    }

    // A guest's claim, spent straight at the controller like a seat's. Going through the body
    // would offer it to the router again.
    private void TakeAiHit(in AiHitMessage hit)
    {
        if (AiAt(hit.Ai) is not { } ai || _seats.WeaponAt(hit.Weapon) is not { } weapon
            || !GodotObject.IsInstanceValid(ai))
        {
            return;
        }

        AiHitsTaken++;
        int shooter = _seats.ShooterOfSeat(hit.ShooterSeat) ?? ProjectilePool.NoShooter;
        var pose = new Transform3D(ai.Attitude, ai.WorldPosition);
        ai.TakeProjectileHit(weapon, pose * hit.LocalImpact, ai.Body?.PartName(hit.Part) ?? "center",
            shooter, hit.Damage);
    }

    private void SendAiFire(int ordinal, WeaponDef weapon, Vector3 origin, Vector3 direction)
    {
        int index = _seats.WeaponIndex(weapon);
        if (index is < 0 or > byte.MaxValue)
        {
            return;
        }

        _net.Broadcast(new AiFireMessage((ushort)ordinal, (byte)index, origin, direction), NetChannels.Events);
    }

    private void SendAiDowned(int ordinal, int? killer)
    {
        int seat = killer is int shooter ? _seats.SeatOfShooter(shooter) : -1;
        _net.Broadcast(
            new WorldEventMessage((ushort)NetWorldEvent.AiDowned, (ushort)ordinal, seat, 0f),
            NetChannels.Events);
    }

    private void SendAiHull(int ordinal, FlightController hurt)
    {
        if (hurt.Damage is not { } damage)
        {
            return;
        }

        _net.Broadcast(
            new WorldEventMessage((ushort)NetWorldEvent.AiHull, (ushort)ordinal, 0, damage.SummaryHealthFraction),
            NetChannels.Events);
    }

    // Admitted here rather than at the next step's admission, so the ordinal on the wire is the
    // one every later message about this aircraft names.
    private void SendAiSpawn(GeneratorAircraftLaunch launch, FlightController aircraft)
    {
        if (_roster is { } roster)
        {
            Admit(roster());
        }

        int ordinal = _admitted.LastIndexOf(aircraft);
        if (ordinal is < 0 or > ushort.MaxValue || launch.Generator is < 0 or > byte.MaxValue
            || launch.Net is < 0 or > byte.MaxValue || launch.LaunchOrdinal is < 0 or > ushort.MaxValue)
        {
            Log.Warn("core", $"net world: generator launch '{aircraft.Name}' not sent, ordinal {ordinal} generator {launch.Generator} net {launch.Net} launch {launch.LaunchOrdinal}");
            return;
        }

        _net.Broadcast(
            new AiSpawnMessage((ushort)ordinal, (ushort)launch.LaunchOrdinal, (byte)launch.Generator,
                (byte)launch.Net, launch.Position, launch.Drop, launch.Velocity, launch.CarrierDrop,
                launch.Throttle),
            NetChannels.Events);
        SpawnsSent++;
    }

    // A cutscene park is each end's own, since every end runs the cutscene. The host's park would
    // arrive a transit late on top of the guest's.
    private void SendAiPresence(int ordinal, FlightController ai)
    {
        if (ai.Parked)
        {
            return;
        }

        _net.Broadcast(
            new WorldEventMessage((ushort)NetWorldEvent.AiPresence, (ushort)ordinal, ai.Inert ? 0 : 1, 0f),
            NetChannels.Events);
    }

    // Reliable delivery is ordered, so the named ordinal is this end's next one unless the two
    // rosters have already parted. Building at any other index would misname every later sample.
    private void TakeAiSpawn(in AiSpawnMessage spawn)
    {
        if (_generators is not { } generators || _roster is not { } roster)
        {
            return;
        }

        Admit(roster());
        if (spawn.Ai != _admitted.Count)
        {
            SpawnsRefused++;
            Log.Warn("core", $"net world: host launch at ordinal {spawn.Ai} dropped, this end's next ordinal is {_admitted.Count}");
            return;
        }

        var built = generators.LaunchReplicated(new GeneratorAircraftLaunch(spawn.Generator, spawn.LaunchOrdinal,
            spawn.Net, spawn.Position, spawn.Drop, spawn.Velocity, spawn.CarrierDrop, spawn.Throttle));
        Admit(roster());
        if (built == null || !ReferenceEquals(AiAt(spawn.Ai), built))
        {
            SpawnsRefused++;
            Log.Warn("core", $"net world: host launch at ordinal {spawn.Ai} did not build here");
            return;
        }

        SpawnsTaken++;
    }

    // A stage change or a kill goes out at once and carries the health. A chip still waiting for
    // the next send would only repeat it.
    private void SendDestructible(DestructibleRegistry.Instance inst)
    {
        _chipped.Remove(inst);
        BroadcastPool(inst);
    }

    private void MarkChipped(DestructibleRegistry.Instance inst)
    {
        if (!_chipped.Contains(inst))
        {
            _chipped.Add(inst);
        }
    }

    // Reliable, as a stage change is. A guest only ever lowers a pool, so a lost last sample would
    // leave its copy high until the next hit.
    private void SendChipped()
    {
        foreach (var inst in _chipped)
        {
            if (!inst.Dormant && inst.Status != DestructibleRegistry.State.Destroyed && BroadcastPool(inst))
            {
                ChipSamplesSent++;
            }
        }

        _chipped.Clear();
    }

    private bool BroadcastPool(DestructibleRegistry.Instance inst)
    {
        if (_world == null)
        {
            return false;
        }

        int index = IndexOf(_world.Destructibles.All, inst);
        if (index is < 0 or > ushort.MaxValue)
        {
            return false;
        }

        _net.Broadcast(
            new WorldEventMessage((ushort)NetWorldEvent.DestructibleHealth, (ushort)index, PoolKey(inst), inst.Health),
            NetChannels.Events);
        return true;
    }

    // Every hull that moves, by placement index. A hull out of the world, held or dead is skipped
    // with its sequence unspent. The guest's copy holds where the last sample left it.
    private void SendZeppelins()
    {
        if (_zeppelins is not { } zeppelins)
        {
            return;
        }

        for (int i = 0; i < zeppelins.LiveCount && i <= ushort.MaxValue; i++)
        {
            while (_zeppelinSequence.Count <= i)
            {
                _zeppelinSequence.Add(0);
            }

            if (!zeppelins.TryReadPath(i, out var position, out float speed, out float yaw, out float pitch))
            {
                continue;
            }

            _net.Broadcast(
                new ZeppelinStateMessage((ushort)i, _zeppelinSequence[i]++, position, speed, pitch, yaw),
                NetChannels.Events);
            ZeppelinSamplesSent++;
        }
    }

    // Every patrolling hull by build index, its name's key riding along. A hull with nothing to
    // send keeps its sequence unspent, and the guest's copy holds where the last sample left it.
    private void SendSurfaceVehicles()
    {
        if (_surface is not { } hulls)
        {
            return;
        }

        var vessels = hulls.Vessels;
        for (int i = 0; i < vessels.Count && i <= ushort.MaxValue; i++)
        {
            while (_surfaceSequence.Count <= i)
            {
                _surfaceSequence.Add(0);
            }

            var hull = vessels[i];
            if (!GodotObject.IsInstanceValid(hull.Body)
                || !hull.TryReadPatrol(out var position, out float speed, out float yaw))
            {
                continue;
            }

            _net.Broadcast(
                new SurfaceVehicleStateMessage((ushort)i, _surfaceSequence[i]++, NameKey(hull.Name),
                    position, speed, yaw),
                NetChannels.Events);
            SurfaceSamplesSent++;
        }
    }

    // The host's index first, then a search by name: a generator launch on one end alone shifts
    // every index after it.
    private void TakeSurface(in SurfaceVehicleStateMessage state)
    {
        if (_surface is not { } hulls)
        {
            return;
        }

        var vessels = hulls.Vessels;
        SurfaceVehicle? hull = null;
        if (state.Vehicle < vessels.Count && NameKey(vessels[state.Vehicle].Name) == state.NameKey)
        {
            hull = vessels[state.Vehicle];
        }
        else
        {
            foreach (var candidate in vessels)
            {
                if (!candidate.IsDestroyed && NameKey(candidate.Name) == state.NameKey)
                {
                    hull = candidate;
                    break;
                }
            }
        }

        if (hull != null && GodotObject.IsInstanceValid(hull.Body)
            && hull.TakeSample(state.Sequence, state.Position, state.Speed, state.YawRad))
        {
            SurfaceSamplesTaken++;
        }
    }

    private void SendWarp(string vehicle, int index)
    {
        if (index is < 0 or > ushort.MaxValue)
        {
            return;
        }

        _net.Broadcast(
            new WorldEventMessage((ushort)NetWorldEvent.VehicleWarped, (ushort)index, NameKey(vehicle), 0f),
            NetChannels.Events);
    }

    private void TakeZeppelin(in ZeppelinStateMessage state)
    {
        if (_zeppelins?.TakePath(state.Zeppelin, state.Sequence, state.Position, state.Speed,
                state.YawRad, state.PitchRad) == true)
        {
            ZeppelinSamplesTaken++;
        }
    }

    private void TakeAiState(in AiStateMessage state)
    {
        if (AiAt(state.Ai) is { } ai && GodotObject.IsInstanceValid(ai))
        {
            ai.RemotePoses?.Receive(state.AsAircraftState());
        }
    }

    // A round the host's AI fired, spawned here from the event. The host alone decides what it
    // hits, so the guest's copy is drawn and routed but spends nothing.
    private void TakeAiFire(in AiFireMessage fire)
    {
        if (AiAt(fire.Ai) is not { } ai || !GodotObject.IsInstanceValid(ai)
            || _seats.WeaponAt(fire.Weapon) is not { } weapon || _seats.Projectiles is not { } pool)
        {
            return;
        }

        pool.Spawn(weapon, new Transform3D(ai.Attitude, fire.Origin), ai.WorldVelocity, ai.PlayerIndex,
            null, fire.Direction, ai.Team);
    }

    private void TakeWorldEvent(in WorldEventMessage e)
    {
        switch ((NetWorldEvent)e.Code)
        {
            case NetWorldEvent.AiDowned:
                if (AiAt(e.Subject) is { Crashed: false } downed && GodotObject.IsInstanceValid(downed))
                {
                    downed.TakeRemoteDeath(e.Argument >= 0 ? _seats.ShooterOfSeat(e.Argument) : null);
                    WorldEventsApplied++;
                }

                break;
            case NetWorldEvent.AiHull:
                if (AiAt(e.Subject) is { } hurt && GodotObject.IsInstanceValid(hurt))
                {
                    hurt.Visuals?.OnHullDamage(e.Value);
                    WorldEventsApplied++;
                }

                break;
            case NetWorldEvent.DestructibleHealth:
                if (_world != null && FindPool(_world.Destructibles.All, e.Subject, e.Argument) is { } pool
                    && _world.ApplyReplicatedHealth(pool, e.Value))
                {
                    WorldEventsApplied++;
                }

                break;
            case NetWorldEvent.VehicleWarped:
                if (_director != null)
                {
                    _director.TakeHostWarp(e.Argument, e.Subject);
                    WorldEventsApplied++;
                }

                break;
            case NetWorldEvent.AiPresence:
                if (AiAt(e.Subject) is { } present && GodotObject.IsInstanceValid(present)
                    && present.Inert != (e.Argument == 0))
                {
                    present.Inert = e.Argument == 0;
                    WorldEventsApplied++;
                }

                break;
            case NetWorldEvent.SeatLeft:
                if (_seats.SeatLeft?.Invoke(e.Subject) == true)
                {
                    WorldEventsApplied++;
                }

                break;
        }
    }
}

/// <summary>What <see cref="NetWorldLink"/> reads of the session's seats and catalogue, as
/// lookups, so the link holds no reference to the session itself.</summary>
internal sealed class NetWorldSeats
{
    /// <summary>The seat that fired a shooter id, or -1 for a round no seat owns.</summary>
    public required Func<int, int> SeatOfShooter { get; init; }

    /// <summary>Whether a seat is flown on this machine.</summary>
    public required Func<int, bool> IsLocal { get; init; }

    /// <summary>A seat's shooter id on this machine, or null.</summary>
    public required Func<int, int?> ShooterOfSeat { get; init; }

    /// <summary>A weapon's wire index, or -1.</summary>
    public required Func<WeaponDef, int> WeaponIndex { get; init; }

    /// <summary>The weapon at a wire index, or null.</summary>
    public required Func<int, WeaponDef?> WeaponAt { get; init; }

    /// <summary>The pool a replayed round is spawned into.</summary>
    public ProjectilePool? Projectiles { get; init; }

    /// <summary>Takes a departed guest's seat out of play, answering whether it was still in.</summary>
    public Func<int, bool>? SeatLeft { get; init; }
}
