namespace CSVM.Flight.Airframe;

/// <summary>The named physics collision layers, the one place a layer bit is assigned a
/// meaning. Everything built before aircraft had bodies sits on Godot's default layer 1
/// (<see cref="World"/>): terrain, buildings, clutter, every world collider, none of it
/// assigns a layer explicitly, and none needs to. Flying aircraft bodies
/// (<see cref="AircraftBody"/>) carry <see cref="Aircraft"/>, so each query chooses whether
/// planes are solid to it: the airframe sweep and the projectile hit test read
/// <see cref="WorldAndAircraft"/>; world-only probes (placement picks, blast spheres,
/// proximity fuses) read <see cref="World"/> and stay blind to planes on purpose.
/// ⚠ Godot's default query mask is ALL layers: a query that must not see planes has to say
/// <see cref="World"/> explicitly.</summary>
public static class CollisionLayers
{
    /// <summary>Layer 1, every static world collider (the engine default; nothing assigns
    /// it explicitly, which is what keeps the pre-aircraft world on it).</summary>
    public const uint World = 1;

    /// <summary>Layer 2, flying aircraft bodies.</summary>
    public const uint Aircraft = 1 << 1;

    /// <summary>The mask for a query that treats planes as solid alongside the world.</summary>
    public const uint WorldAndAircraft = World | Aircraft;
}
