using System.Collections.Generic;
using CSVM.Flight.Ai;

namespace CSVM.Flight.Weapons;

/// <summary>
/// A mission's built surface hulls as the flight side reads them. The aim assist and the target
/// scan draw candidates from it, and a proximity fuse measures against its list. The session builds
/// and steps the hulls (<c>SurfaceVehicleRuntime</c>); a plane and its projectile pool only read
/// them through this.
/// </summary>
public interface ISurfaceVehicles
{
    /// <summary>Every hull built so far, roster and generator launches alike.</summary>
    IReadOnlyList<SurfaceVehicle> Vessels { get; }

    /// <summary>Appends every built hull to <paramref name="into"/>'s vehicle list.</summary>
    void CollectVehicles(AimCandidateSet into);
}
