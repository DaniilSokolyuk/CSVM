using System.Collections.Generic;
using CSVM.Flight.Modes;
using Godot;

namespace CSVM.Session.Roster;

/// <summary>Where every pilot in a session starts: one call answering for the whole field, never
/// one call per player. <see cref="SpawnPicker"/> implements it directly, looping its own
/// per-player <c>ChooseSpawn</c>, the plain behaviour for solo flight, Dogfight and any scripted
/// run.
/// ⚠ Whole-field is the point of the seam: a centred fan needs the player count before any slot
/// is known, and a worst-slot lift needs every slot probed before any answer is final. A
/// per-player signature was rejected, it forces state across calls and leaves player 1's answer
/// wrong until player 4 has asked.</summary>
public interface IFlightStarts
{
    /// <summary>Resolves one start per player, for players <c>0 … playerCount-1</c> in ascending
    /// order. ⚠ Ascending is a contract: the spawn-list index wraps from
    /// <paramref name="spawnBase"/> per player, and the log order depends on it.</summary>
    IReadOnlyList<FlightStart> ChooseStarts(IReadOnlyList<SpawnPoint>? spawns, string missionZrdrPath,
        int spawnBase, int playerCount);
}

/// <summary>One pilot's start: a world position, a look-at point one unit ahead along the spawn
/// heading, and the throttle and speed to begin on, the four values
/// <see cref="Flight.Airframe.FlightController.Setup"/> takes, carried unchanged so no call site has to
/// reinterpret them. ⚠ Throttle and speed are per-mission (PLAYER_INIT) or per-mode (the
/// multiplayer constants), NOT per-airframe; the original has no per-plane spawn speed for the
/// player (docs/formats/spawns.md, docs/formats/net-spawns.md).</summary>
public readonly record struct FlightStart(Vector3 Pos, Vector3 LookAt, float ThrottleFrac, float SpeedMps);
