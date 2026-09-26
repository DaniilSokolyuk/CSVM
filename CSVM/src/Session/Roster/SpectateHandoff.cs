using System;
using System.Collections.Generic;
using CSVM.Flight.Airframe;
using CSVM.Flight.Camera;
using Godot;

namespace CSVM.Session.Roster;

/// <summary>
/// What both directors do to a pane whose pilot is out of the mission for good: the wreck is
/// pinned so neither <c>R</c> nor an armed respawn timer flies it again, the controller stops
/// writing this pane's camera, and a <see cref="SpectatorCamera"/> takes it, orbit-locked onto a
/// live aeroplane where there is one. One copy because the two rules that reach it differ only in
/// what counts as "out": Instant Action's last life, and a campaign human whose aircraft is lost
/// while the others fly on. The hand-off itself is the same, and so is its failure mode, a pane
/// left on a camera nobody drives.
/// </summary>
internal static class SpectateHandoff
{
    /// <summary>Hands <paramref name="rig"/>'s pane over, answering the aircraft the spectator
    /// locked onto (null when it starts free at the crash camera). ⚠ False when this pilot is
    /// already spectating, which is what makes a second report of the same death a no-op rather
    /// than a second camera on the pane.</summary>
    internal static bool Begin(PlayerRig rig, IReadOnlyList<PlayerRig> rigs, Node3D worldRoot,
        Func<IReadOnlyList<Node3D>>? lockCandidates, List<SpectatorCamera>? tracked,
        out FlightController? follow)
    {
        follow = null;
        if (rig.Controller is not { Spectating: false } pilot)
        {
            return false;
        }

        pilot.Spectating = true;
        pilot.CameraOwned = true;   // the spectator owns this pane from here
        foreach (var other in rigs)
        {
            if (other.Controller is { InPlay: true } live && live != pilot)
            {
                follow = live;
                break;
            }
        }

        // ⚠ Give the spectator this pilot's OWN device filter: two downed pilots watching at once
        // otherwise move in lockstep. Any translation input releases the orbit lock.
        var eye = rig.Camera.Position;   // where the death or crash camera left it (CameraController)
        var spectator = new SpectatorCamera(rig.Camera, eye,
            follow != null ? follow.WorldPosition : eye - rig.Camera.Basis.Z,
            pilot.PadDevices, pilot.UseKeyboard)
        {
            ShowReadout = false,   // the freecam's own label would sit over a splitscreen pane
            LockCandidates = lockCandidates,
        };
        worldRoot.AddChild(spectator);
        tracked?.Add(spectator);   // tracked so a rerun can hand the panes back
        if (follow != null)
        {
            spectator.FollowNode(follow);
        }

        return true;
    }
}
