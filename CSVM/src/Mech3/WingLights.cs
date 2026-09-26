using System;
using Godot;

namespace CSVM.Mech3;

/// <summary>
/// Wingtip navigation lights: the flare-node predicate (<c>wing_flare1</c>/<c>wing_flare2</c>),
/// glow texture, blink colour and period, and point-light range, each constant below names its
/// own <c>wing_light.json</c> source.
/// PlaneBuilder hides and re-skins the flares (additive tint, one-sided as authored, no
/// billboard); <c>Flight.Airframe.WingLightBlinker</c> flashes them and emits a matching
/// OmniLight3D per side.
/// </summary>
public static class WingLights
{
    /// <summary>The glow sprite the flare quads are skinned with. It is also used by a few
    /// real airframe meshes (lwingbend, piece1, …), so the additive-tint treatment is
    /// scoped to the flare nodes by name, not routed through this texture.</summary>
    public const string FlareTexture = "oil_liteflare";

    /// <summary>Blink cycle length, wing_light.json's LOOP SEQUENCE_OFFSET.</summary>
    public const float BlinkPeriod = 1.5f;

    /// <summary>Point-light falloff band, wing_light.json's LIGHT_STATE RANGE. Godot's
    /// OmniLight3D has no inner radius, so only the max feeds <c>OmniRange</c>.</summary>
    public const float FlareRangeMin = 0.5f;
    public const float FlareRangeMax = 1.25f;

    /// <summary>Warm amber the original flashes the lights, wing_light.json's LIGHT_STATE COLOR.</summary>
    public static readonly Color FlareColor = new(0.88f, 0.78f, 0.36f);

    /// <summary>The wingtip flare sprite nodes toggled by the blink anim (wing_flare1/2).</summary>
    public static bool IsFlare(string name) =>
        name.StartsWith("wing_flare", StringComparison.OrdinalIgnoreCase);
}
