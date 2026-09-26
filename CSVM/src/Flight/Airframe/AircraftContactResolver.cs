using System.Collections.Generic;
using CSVM.Utils;
using Godot;

namespace CSVM.Flight.Airframe;

/// <summary>The engine effects one contact runs, in the order the decode spends them. The
/// resolver calls these rather than returning them because each one's result is a premise of the
/// rule after it: the ledger answers which zone it charged, and the response answers the pose the
/// un-embed test reads. The aircraft node implements this, so the struck <c>Node</c>, the sweep
/// cadence and the flight model stay on the side that owns them.</summary>
public interface IContactEffects
{
    /// <summary>Offers the struck object the contact's health damage: true when it breaks and the
    /// striker flies THROUGH it, which ends the contact with no response at all.</summary>
    bool ShatterStruck(float healthDamage);

    /// <summary>Plays the struck surface's authored graze reaction, at most one per its own
    /// interval however many frames a scrape lasts.</summary>
    void PlayGrazeReaction();

    /// <summary>Spends the pair on the named zone through the ledger and runs the readouts that
    /// spend drives. Returns the ledger's answer: the zone it actually charged, which may be a
    /// redirect off a dead one, or null when the plane carries no zones.</summary>
    PlaneDamage.PartState? SpendDamage(string zone, float healthDamage, float armorDamage);

    /// <summary>Applies the contact response (the decoded placement and, for a human pilot, the
    /// normal-only impulse) and reports the pose it left behind.</summary>
    ContactResponse ApplyResponse();
}

/// <summary>The striking aircraft's state entering one contact, the half
/// <see cref="ContactReport"/> says nothing about.
/// ⚠ <see cref="IsHumanPiloted"/> is the asymmetry the original keeps: a player skips entity
/// detection, so it never takes the 0.2 cut, never arms a grace window and is never doomed
/// (docs/org/flightModel.md). Do not let it drift into a symmetric rule.</summary>
public readonly record struct ContactConditions
{
    /// <summary>Whether a person is flying this aircraft. See the type's warning.</summary>
    public bool IsHumanPiloted { get; init; }

    /// <summary>The striker's unit velocity, which sets the severity cosine against the contact
    /// normal.</summary>
    public Vector3 VelocityDir { get; init; }

    /// <summary>The striker's speed, which the no-ledger threshold reads.</summary>
    public float Speed { get; init; }

    /// <summary>The striker's pose, for putting the impact in its own frame: which zone the data
    /// calls the box that reached the contact depends on where along the airframe it sits.</summary>
    public Transform3D Pose { get; init; }

    /// <summary>The airframe's authored collision ranges; null on a rig with no flight model
    /// bound, which falls back to the compiled ranges.</summary>
    public PlaneStats? Stats { get; init; }

    /// <summary>The striker's damage ledger, read for the kill test and the graze line; null on a
    /// plane with no <c>destroyable_parts</c> data, which has no health pool to survive on.</summary>
    public PlaneDamage? Ledger { get; init; }

    /// <summary>The airframe boxes the un-embed test moves. Null skips that test, as an
    /// uncollidable rig has nothing to free.</summary>
    public IReadOnlyList<PlaneCollider.Part>? Parts { get; init; }

    /// <summary>The striker's own body RIDs, which always overlap its own boxes.</summary>
    public Godot.Collections.Array<Rid>? ExcludeSelf { get; init; }
}

/// <summary>What the contact response left the striker with: the pose the un-embed test starts
/// from.</summary>
public readonly record struct ContactResponse(Transform3D Pose);

/// <summary>The decoded contact rules for one aircraft: what the contact costs both parties
/// (<c>FUN_0048d2c0</c>), whether the striker survives it, and how far it has to be pushed to stop
/// being embedded in what it grazed. It holds no <c>Node</c> and no physics space, so a suite runs
/// it against a synthetic <see cref="IWorldQuery"/> with no world in the process. One call decides
/// one contact and answers with one <see cref="ContactOutcome"/> the caller performs; the engine
/// effects the decision interleaves with go through <see cref="IContactEffects"/>.
/// ⚠ Each aircraft sweeps its own airframe, so this stays per-aircraft: it decides nothing for the
/// struck party beyond the pair the caller hands over.</summary>
public sealed class AircraftContactResolver
{
    // m per un-embed attempt after a graze, and the attempts before the airframe is destroyed
    // instead. A product exception: the original places one sphere at its own contact point, which
    // cannot leave it inside geometry, while a swept multi-box airframe can. See UnEmbed.
    private const float EmbedPushOut = 0.3f;
    private const int EmbedTries = 3;

    // The compiled fallback collision ranges, for a bare suite rig with no flight model bound.
    private static readonly PlaneStats DefaultCollideRanges = new();

    private readonly IWorldQuery _world;

    public AircraftContactResolver(IWorldQuery world) => _world = world;

    /// <summary>Decides one detected contact: the pair both parties spend, the doom rule, what the
    /// striker's own ledger does to it, and the push-out its new pose needs. The struck object's
    /// half comes back as <see cref="ContactOutcome.DamageStruckAircraft"/> for the caller, which
    /// is the only side holding the struck rig.</summary>
    public ContactOutcome Resolve(in ContactReport contact, in ContactConditions striker,
        IContactEffects effects)
    {
        // A bare suite rig has no flight model bound, so no authored ranges; the compiled
        // fallbacks in PlaneStats are what the original would stand on there too.
        var ranges = striker.Stats ?? DefaultCollideRanges;
        float severity = CollisionDamage.Severity(striker.VelocityDir, contact.Normal);
        // Entity detection is the non-player branch only, and only against another aeroplane.
        bool entityImpact = !striker.IsHumanPiloted && contact.StruckIsAircraft;
        float cut = entityImpact ? CollisionDamage.EntityCut : 1f;
        var outcome = new ContactOutcome
        {
            ArmorDamage =
                CollisionDamage.Term(severity, ranges.CollideArmorFloor, ranges.CollideArmorScale) * cut,
            HealthDamage =
                CollisionDamage.Term(severity, ranges.CollideHealthFloor, ranges.CollideHealthScale) * cut,
            // The camera kick (0x0048d3cc), spent before the pair and on its own law: linear in
            // speed and in the RAW cosine, where the pair is cubic in the cosine and speed-blind.
            ShakeMagnitude = striker.IsHumanPiloted
                ? CollisionDamage.ContactShake(striker.Speed, severity)
                : 0f,
            // local_11 (0x0048d79e): an AI that rammed anything OTHER than an aeroplane dies
            // outright, whatever health it has left. An AI that rammed an aeroplane survives on
            // health as usual.
            Dooms = !striker.IsHumanPiloted && !contact.StruckIsAircraft,
        };
        if (severity > 0f)
        {
            if (contact.StruckIsAircraft)
                outcome = outcome with { DamageStruckAircraft = true };
            else if (effects.ShatterStruck(outcome.HealthDamage))
                return outcome; // set-dressing shattered; the plane keeps its full-motion pose
        }

        return Survive(in contact, in striker, effects, outcome);
    }

    // The striker's own half: its fate, and the ledger spend the fate reads. A crash returns as
    // soon as it is decided, so no rule below a fatal one runs.
    private ContactOutcome Survive(in ContactReport contact, in ContactConditions striker,
        IContactEffects effects, ContactOutcome outcome)
    {
        float vn = Mathf.Abs((striker.VelocityDir * striker.Speed).Dot(contact.Normal));
        if (striker.Ledger == null)
        {
            // Neither zones nor an authored whole pair, so there is no health pool to survive on
            // and every contact is fatal. No shipped load lands here.
            Log.Info("flight", $"impact into {contact.ColliderName} at vn={vn:0.0} m/s, crash (no damage data)");
            return outcome with { Fate = ContactFate.Crash };
        }

        // The decoded doom rule (local_11): checked before the pair is spent, as the original
        // checks it after spending but independently of the result.
        if (outcome.Dooms)
        {
            Log.Info("flight", $"AI ram into {contact.ColliderName}, destroyed outright (the decoded local_11 rule)");
            return outcome with { Fate = ContactFate.Crash };
        }

        string dataPart = PlaneDamage.MapStruckPart(
            contact.Part, striker.Pose.AffineInverse() * contact.Impact);
        effects.PlayGrazeReaction();
        // The striker's own share is the SAME decoded pair the struck party took, spent armour-first
        // (FUN_004b7f80's split). ⚠ Every resolved contact spends: the original's cadence is its
        // alternate-step sweep (SweepCadence), never a gate here.
        var state = effects.SpendDamage(dataPart, outcome.HealthDamage, outcome.ArmorDamage);
        string struckPart = state?.Def.Name ?? dataPart; // the ledger may redirect
        outcome = outcome with { StruckPart = struckPart };
        if (striker.Ledger.IsDestroyed)
        {
            Log.Info("flight",
                $"vehicle health exhausted ({struckPart} last), vn={vn:0.0} m/s into {contact.ColliderName}");
            return outcome with { Fate = ContactFate.Crash }; // whole-vehicle health at zero
        }

        if (state != null)
        {
            outcome = outcome with
            {
                DamageFlashText = $"⚠ IMPACT {struckPart.ToUpperInvariant()} {state.Fraction * 100f:0}%",
            };
            Log.Info("flight",
                $"graze ({contact.Part}→{struckPart}): {contact.ColliderName} vn={vn:0.0} m/s dmg={outcome.HealthDamage:0.0} armor={state.Armor:0.0}/{state.Def.MaxArmor:0} hp={state.Hp:0.0}/{state.Def.MaxHp:0} hull={striker.Ledger.WholeHealth:0.0}/{striker.Ledger.WholeHealthMax:0}");
        }

        var response = effects.ApplyResponse();
        return UnEmbed(in contact, in striker, outcome, response.Pose);
    }

    // Push out along the contact normal while any airframe box still overlaps solid geometry at
    // the pose the response left (V-ditches, berm backsides). ⚠ Destroying the airframe after
    // EmbedTries is ours and stays: without a terminal case, boxes the pushes cannot free leave the
    // aircraft inside the world with no rule to end it. It keeps the pushes it already earned.
    private ContactOutcome UnEmbed(in ContactReport contact, in ContactConditions striker,
        ContactOutcome outcome, Transform3D pose)
    {
        if (striker.Parts == null)
            return outcome;
        var pushOut = Vector3.Zero;
        for (int attempt = 0; ; attempt++)
        {
            var at = new Transform3D(pose.Basis, pose.Origin + pushOut);
            if (!_world.Overlaps(striker.Parts, at, CollisionLayers.WorldAndAircraft, striker.ExcludeSelf))
                return outcome with { PushOut = pushOut };
            if (attempt >= EmbedTries)
            {
                Log.Info("flight", $"embedded in terrain after a graze, destroyed");
                return outcome with { PushOut = pushOut, Fate = ContactFate.Crash };
            }

            pushOut += contact.Normal * EmbedPushOut;
        }
    }
}
