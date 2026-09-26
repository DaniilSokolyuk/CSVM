using Godot;

namespace CSVM.Flight.Airframe;

/// <summary>One detected contact, as the value the airframe sweep and its centre-ray backstop both
/// fill: where the aircraft touched something, along what normal, which airframe part reached it
/// first and how far along the frame's motion it stopped. It holds no <c>Node</c>, because the only
/// question the decision side asks about the struck object is whether it is an aeroplane, which
/// <see cref="StruckIsAircraft"/> carries, and the caller keeps the collider for the applying.</summary>
public readonly record struct ContactReport
{
    /// <summary>The impact, in world space: the point the airframe touched.</summary>
    public Vector3 Impact { get; init; }

    /// <summary>The struck surface's normal. The centre-ray backstop has no surface normal of its
    /// own and reports the reversed motion instead, which is what makes it head-on.</summary>
    public Vector3 Normal { get; init; }

    /// <summary>Which of the swept airframe boxes reached the impact first, by its part name;
    /// <c>center</c> for the centre-ray backstop, which sweeps no boxes.</summary>
    public string Part { get; init; }

    /// <summary>The struck collider's parent/body name, for the death and graze log lines and for
    /// the crash def selection. Empty off world geometry carrying no live node.</summary>
    public string ColliderName { get; init; }

    /// <summary>How far along this frame's motion the airframe got before the contact, 0 to 1. The
    /// centre-ray backstop reports 1: it finds the hit but not where the boxes would have stopped.</summary>
    public float StopFraction { get; init; }

    /// <summary>Whether the struck object is another aeroplane. It decides the entity cut, the doom
    /// rule and both grace windows, and it is the whole of what the decision side needs to know
    /// about what was struck.</summary>
    public bool StruckIsAircraft { get; init; }
}
