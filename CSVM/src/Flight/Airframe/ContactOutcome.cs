using Godot;

namespace CSVM.Flight.Airframe;

/// <summary>What a contact does to the striking aircraft: <c>Graze</c> is the survivable outcome,
/// <c>Crash</c> the fatal one (CONTEXT.md's contact family).</summary>
public enum ContactFate
{
    Graze,
    Crash,
}

/// <summary>What one contact costs and what the caller owes because of it, as a value with no
/// <c>Node</c> and no physics space behind it: the striker's fate, the decoded damage pair both
/// parties spend, the doom rule's answer, which zone the ledger charged, and the pilot HUD's flash
/// line. A caller performs it; nothing here applies anything.</summary>
public readonly record struct ContactOutcome
{
    /// <summary>Whether the striker survives this contact.</summary>
    public ContactFate Fate { get; init; }

    /// <summary>The decoded pair's armour term (<c>FUN_0048d2c0</c>), already carrying the
    /// entity cut where it applied. The struck party and the striker spend the same pair.</summary>
    public float ArmorDamage { get; init; }

    /// <summary>The decoded pair's health term, on the same terms as
    /// <see cref="ArmorDamage"/>.</summary>
    public float HealthDamage { get; init; }

    /// <summary>The doom rule's answer (<c>local_11</c>): this contact kills the striker whatever
    /// health it has left. It is a reason for <see cref="ContactFate.Crash"/>, not a second fate.</summary>
    public bool Dooms { get; init; }

    /// <summary>The zone the ledger actually charged, which is <c>Apply</c>'s answer rather than
    /// the geometric guess. Empty when no damage was spent this contact.</summary>
    public string StruckPart { get; init; }

    /// <summary>The pilot HUD's damage-flash line, or null when this contact flashes nothing.</summary>
    public string? DamageFlashText { get; init; }

    /// <summary>How far along the contact normal the caller must move the striker to stop it being
    /// embedded in what it grazed. Zero when it came to rest free, and applied even on a
    /// <see cref="ContactFate.Crash"/>: a plane that could not get free wrecks where the last
    /// attempt left it.</summary>
    public Vector3 PushOut { get; init; }

    /// <summary>The camera kick this contact owes the striker, radians of block-5 roll
    /// (<see cref="CollisionDamage.ContactShake"/>). Zero on an AI, which the original never
    /// shakes for, and on a contact that is not closing.</summary>
    public float ShakeMagnitude { get; init; }

    /// <summary>The instruction the caller owes when it is set: hand the struck aeroplane the pair
    /// above, and arm the collision grace window on BOTH parties so neither re-resolves the overlap
    /// they are still in. Only the caller holds the struck rig, so only it can do this.</summary>
    public bool DamageStruckAircraft { get; init; }
}
