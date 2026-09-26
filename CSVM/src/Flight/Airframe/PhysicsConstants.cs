namespace CSVM.Flight.Airframe;

/// <summary>Physics constants shared by flight and weapon ballistics that both derive from
/// player.json's global block, kept in one place so they can't drift apart.</summary>
public static class PhysicsConstants
{
    public const float NomGravity = 20.0f; // player.json nom_gravity, the game's arcade gravity, m/s²

    /// <summary>m/s per mph, every speed token in player.json/vehicle.json is authored in MPH and
    /// multiplied by this on load (docs/org/flightModel.md "Units and conventions").</summary>
    public const float MphToMs = 0.44704f;
}
