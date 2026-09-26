using System.Collections.Generic;
using CSVM.Flight.Airframe;
using Godot;
using Xunit;

namespace CSVM.Tests;

/// <summary>
/// <see cref="AircraftContactResolver.Resolve"/>, the decoded contact rules as a table, taken
/// apart from <c>FlightController</c> and the physics world entirely: no <c>Node</c>, no
/// <see cref="GodotWorldQuery"/>, a synthetic <see cref="IWorldQuery"/> only where the un-embed
/// loop needs one. Decode: docs/org/flightModel.md's "Collision damage" and its entity-cut note.
/// ⚠ Entity detection (the 0.2 cut and the doom rule) is the non-player branch only, and only
/// against another aeroplane, a case that only checks
/// <see cref="ContactOutcome.DamageStruckAircraft"/> would pass whether or not the cut actually
/// applied, so every row here asserts the damage MAGNITUDE, not just the flags.
/// </summary>
public class AircraftContactResolverTests
{
    // player.json's crash block, the same floor/scale CollisionDamageTests uses.
    private const float Scale = 200f;

    // A head-on contact: severity 1, so Term collapses to the authored scale alone.
    private static readonly Vector3 HeadOnVelocity = Vector3.Forward;
    private static readonly Vector3 HeadOnNormal = -Vector3.Forward;

    // A 5.7° scrape along a surface whose normal is +Z: severity 0.1, so both terms sit on the
    // compiled floor (15) and the contact is as cheap as the decoded law allows.
    private static readonly Vector3 ShallowSlide = new(-0.99499f, 0f, -0.1f);

    /// <summary>An AI that rams anything OTHER than an aeroplane dies outright (local_11), whatever
    /// health it carries, asserted against a striker with a live ledger so the rule is not merely
    /// unreachable through the no-ledger speed fallback above it.</summary>
    [Fact]
    public void AnAiRammingANonAeroplaneIsDoomedOutright()
    {
        var resolver = new AircraftContactResolver(new NeverOverlaps());
        var effects = new FakeContactEffects();
        var ledger = OneZone();

        var outcome = resolver.Resolve(HeadOn(struckIsAircraft: false), Striker(humanPiloted: false, ledger), effects);

        Assert.True(outcome.Dooms);
        Assert.Equal(ContactFate.Crash, outcome.Fate);
        // No entity cut on a non-aircraft target: the full pair, not the fifth.
        Assert.Equal(Scale, outcome.ArmorDamage, 3);
        Assert.Equal(Scale, outcome.HealthDamage, 3);
        Assert.False(outcome.DamageStruckAircraft);
        // ShatterStruck ran (asked whether the struck def broke) and answered false: a real
        // def, not set-dressing, so the contact fell through to the doom rule below it.
        Assert.True(effects.ShatterStruckCalled);
    }

    /// <summary>An AI into another aeroplane takes the entity cut (a fifth) instead of the doom
    /// rule, and survives to hand the struck plane its share, the trap case: a table that only
    /// checked <c>DamageStruckAircraft</c> would pass with the cut silently missing.</summary>
    [Fact]
    public void AiIntoAiTakesTheEntityCutNotTheDoomRule()
    {
        var resolver = new AircraftContactResolver(new NeverOverlaps());
        var effects = new FakeContactEffects();

        var outcome = resolver.Resolve(HeadOn(struckIsAircraft: true), Striker(humanPiloted: false), effects);

        Assert.False(outcome.Dooms);
        Assert.True(outcome.DamageStruckAircraft);
        Assert.Equal(Scale * CollisionDamage.EntityCut, outcome.ArmorDamage, 3);
        Assert.Equal(Scale * CollisionDamage.EntityCut, outcome.HealthDamage, 3);
    }

    /// <summary>The player skips entity detection outright: ramming another aeroplane costs the
    /// FULL pair, not the fifth, and never dooms, the asymmetry <see cref="ContactConditions"/>
    /// warns against losing.</summary>
    [Fact]
    public void APlayerIsExemptFromBothTheCutAndTheDoomRule()
    {
        var resolver = new AircraftContactResolver(new NeverOverlaps());
        var effects = new FakeContactEffects();

        var outcome = resolver.Resolve(HeadOn(struckIsAircraft: true), Striker(humanPiloted: true), effects);

        Assert.False(outcome.Dooms);
        Assert.True(outcome.DamageStruckAircraft);
        Assert.Equal(Scale, outcome.ArmorDamage, 3);
        Assert.Equal(Scale, outcome.HealthDamage, 3);
    }

    /// <summary>The block-5 camera kick (<c>0x48d409</c>): a human pilot's every contact spends it,
    /// a graze included, and an AI's spends none. Its law is linear in speed and in the RAW cosine,
    /// so it discriminates cases the cubic, speed-blind damage pair cannot.</summary>
    [Fact]
    public void EveryHumanPilotedContactKicksTheCameraAndAnAiContactKicksNothing()
    {
        var resolver = new AircraftContactResolver(new NeverOverlaps());

        // Head-on at 50 m/s: 50 x 1 x 0.03 is ten times the ceiling, so the kick saturates.
        var headOn = resolver.Resolve(
            HeadOn(struckIsAircraft: false), Striker(humanPiloted: true), new FakeContactEffects());
        Assert.Equal(CollisionDamage.ContactShakeCap, headOn.ShakeMagnitude, 4);

        // The same 5.7 degree scrape, at 20 m/s: the cosine is 0.1, so the kick stays linear.
        var slide = Striker(humanPiloted: true) with { VelocityDir = ShallowSlide, Speed = 20f };
        var graze = resolver.Resolve(HeadOn(struckIsAircraft: false), slide, new FakeContactEffects());
        Assert.Equal(0.06f, graze.ShakeMagnitude, 4);

        var ai = resolver.Resolve(
            HeadOn(struckIsAircraft: false), Striker(humanPiloted: false), new FakeContactEffects());
        Assert.Equal(0f, ai.ShakeMagnitude);
    }

    /// <summary>A plane grinding along the ground cannot collect endless free contacts: the decoded
    /// pair costs at least the authored floor on EVERY contact the sweep resolves (the cadence is
    /// the sweep's, <see cref="Flight.Weapons.SweepCadence"/>, never a gate on the spend), so a bounded ledger runs
    /// out and the decoded health rule ends the slide. This is what the removed stop-speed rule
    /// guarded, proven on the decoded response instead.</summary>
    [Fact]
    public void ASustainedSlideExhaustsTheLedgerInsteadOfGrindingOnForever()
    {
        var resolver = new AircraftContactResolver(new NeverOverlaps());
        var ledger = OneZone();
        var effects = new LedgerContactEffects(ledger);
        var slide = Striker(humanPiloted: true, ledger) with { VelocityDir = ShallowSlide };
        var along = HeadOn(struckIsAircraft: false) with { Normal = new Vector3(0f, 0f, 1f) };

        int contacts = 0;
        var fate = ContactFate.Graze;
        while (fate != ContactFate.Crash && contacts < 40)
        {
            contacts++;
            fate = resolver.Resolve(along, slide, effects).Fate;
        }

        Assert.Equal(ContactFate.Crash, fate);
        Assert.Equal(3, contacts);          // three spends of the 15 floor: 20 armour, then 20 health
        Assert.True(ledger.IsDestroyed);
    }

    /// <summary>The control the row above needs (METHOD-9): with nothing spending the pair (the
    /// scriptable effects take the spend and touch no ledger), the same slide runs forever and never
    /// resolves a fate, which is the failure mode the stop-speed rule was invented for.</summary>
    [Fact]
    public void ASlideThatSpendsNothingNeverEndsAtAll()
    {
        var resolver = new AircraftContactResolver(new NeverOverlaps());
        var ledger = OneZone();
        var effects = new FakeContactEffects();
        var slide = Striker(humanPiloted: true, ledger) with { VelocityDir = ShallowSlide };
        var along = HeadOn(struckIsAircraft: false) with { Normal = new Vector3(0f, 0f, 1f) };

        for (int i = 0; i < 200; i++)
        {
            Assert.Equal(ContactFate.Graze, resolver.Resolve(along, slide, effects).Fate);
        }

        Assert.True(effects.SpendDamageCalled);   // every contact offered the pair; none was taken
        Assert.False(ledger.IsDestroyed);
    }

    /// <summary>A plane still embedded after the response is pushed out along the contact normal,
    /// three tries at a time; giving up destroys it rather than letting it sit inside the world,
    /// the un-embed loop's own worked case, and the binding test for that product exception.</summary>
    [Fact]
    public void AnEmbeddedPlaneIsPushedOutThreeTimesThenExplodes()
    {
        var resolver = new AircraftContactResolver(new AlwaysOverlaps());
        var effects = new FakeContactEffects();
        var striker = Striker(humanPiloted: true, ledger: OneZone()) with
        {
            // The fake IWorldQuery below ignores Parts entirely; a real ConvexPolygonShape3D
            // needs a live engine to construct at all, and this suite runs with none.
            Parts = new List<PlaneCollider.Part> { new("center", null!, Transform3D.Identity, null!) },
        };

        var outcome = resolver.Resolve(HeadOn(struckIsAircraft: false), striker, effects);

        Assert.Equal(ContactFate.Crash, outcome.Fate);
        Assert.Equal(0.9f, outcome.PushOut.Length(), 3);
    }

    /// <summary>Struck set-dressing that shatters ends the contact immediately: the striker keeps
    /// its full-motion pose and flies through, so nothing past
    /// <see cref="IContactEffects.ShatterStruck"/> runs at all, not the graze reaction, not the
    /// damage spend, not the response.</summary>
    [Fact]
    public void ShatteringSetDressingEndsTheContactWithNothingElseRun()
    {
        var resolver = new AircraftContactResolver(new NeverOverlaps());
        var effects = new FakeContactEffects { ShatterStruckResult = true };
        var striker = Striker(humanPiloted: false, ledger: OneZone());

        var outcome = resolver.Resolve(HeadOn(struckIsAircraft: false), striker, effects);

        Assert.Equal(ContactFate.Graze, outcome.Fate); // the default: no fate is ever decided
        Assert.True(effects.ShatterStruckCalled);
        Assert.False(effects.PlayGrazeReactionCalled);
        Assert.False(effects.SpendDamageCalled);
        Assert.False(effects.ApplyResponseCalled);
    }

    /// <summary>A graze spends real armour before health on the struck zone: the decoded pair runs
    /// through <see cref="PlaneDamage.Apply"/>'s armour-first split, so a fully-armoured zone loses
    /// no health at all, and only once the armour is gone does health start to drain, the control
    /// half, which a symmetric or health-first spend fails.</summary>
    [Fact]
    public void AGrazeSpendsArmourBeforeHealthOnTheStruckZone()
    {
        var resolver = new AircraftContactResolver(new NeverOverlaps());
        var ledger = OneZone();
        var effects = new LedgerContactEffects(ledger);
        var striker = Striker(humanPiloted: true, ledger) with { VelocityDir = ShallowSlide };
        var shallow = HeadOn(struckIsAircraft: false) with { Normal = new Vector3(0f, 0f, 1f) };

        var first = resolver.Resolve(shallow, striker, effects);
        Assert.Equal(ContactFate.Graze, first.Fate);
        Assert.Equal(5f, effects.LastState!.Armor, 3);            // 20 − 15: armour spent
        Assert.Equal(20f, effects.LastState!.Hp, 3);              // health untouched behind it

        var second = resolver.Resolve(shallow, striker, effects);
        Assert.Equal(ContactFate.Graze, second.Fate);
        Assert.Equal(0f, effects.LastState!.Armor, 3);            // the remaining 5 absorbs a third
        Assert.Equal(10f, effects.LastState!.Hp, 3);              // 20 − 15·(1 − 5/15)
    }

    private static ContactReport HeadOn(bool struckIsAircraft) => new()
    {
        Impact = Vector3.Zero,
        Normal = HeadOnNormal,
        Part = "center",
        ColliderName = struckIsAircraft ? "P2" : "m_build01",
        StopFraction = 1f,
        StruckIsAircraft = struckIsAircraft,
    };

    private static ContactConditions Striker(bool humanPiloted, PlaneDamage? ledger = null) => new()
    {
        IsHumanPiloted = humanPiloted,
        VelocityDir = HeadOnVelocity,
        Speed = 50f,
        Pose = Transform3D.Identity,
        Stats = null,             // falls back to the compiled Floor/Scale defaults
        Ledger = ledger,
        Parts = null,
        ExcludeSelf = null,
    };

    private static PlaneDamage OneZone() => new(new List<DestroyablePart>
    {
        new() { Name = "nose", MaxHp = 20f, MaxArmor = 20f, Critical = true },
    });

    // ---- fakes ---------------------------------------------------------------------------------

    /// <summary>Every <see cref="IContactEffects"/> stub, each individually scriptable and each
    /// recording whether it ran, the shatter-and-fly-through row needs to prove the ones AFTER it
    /// did not.</summary>
    private sealed class FakeContactEffects : IContactEffects
    {
        public bool ShatterStruckResult;

        public bool ShatterStruckCalled;
        public bool PlayGrazeReactionCalled;
        public bool SpendDamageCalled;
        public bool ApplyResponseCalled;

        public bool ShatterStruck(float healthDamage)
        {
            ShatterStruckCalled = true;
            return ShatterStruckResult;
        }

        public void PlayGrazeReaction() => PlayGrazeReactionCalled = true;

        public PlaneDamage.PartState? SpendDamage(string zone, float healthDamage, float armorDamage)
        {
            SpendDamageCalled = true;
            return null;
        }

        public ContactResponse ApplyResponse()
        {
            ApplyResponseCalled = true;
            return new ContactResponse(Transform3D.Identity);
        }
    }

    /// <summary>Effects whose damage spend routes into a REAL <see cref="PlaneDamage"/>, so the
    /// armour-before-health row asserts the ledger's split rather than a stub's echo.</summary>
    private sealed class LedgerContactEffects : IContactEffects
    {
        private readonly PlaneDamage _ledger;

        public LedgerContactEffects(PlaneDamage ledger) => _ledger = ledger;

        public PlaneDamage.PartState? LastState { get; private set; }

        public bool ShatterStruck(float healthDamage) => false;

        public void PlayGrazeReaction()
        {
        }

        public PlaneDamage.PartState? SpendDamage(string zone, float healthDamage, float armorDamage)
        {
            LastState = _ledger.Apply(zone, healthDamage, armorDamage);
            return LastState;
        }

        public ContactResponse ApplyResponse() => new(Transform3D.Identity);
    }

    private sealed class NeverOverlaps : IWorldQuery
    {
        public bool Sweep(IReadOnlyList<PlaneCollider.Part> parts, Transform3D baseTransform, Vector3 motion,
            uint mask, Godot.Collections.Array<Rid>? exclude, out SweepReport report)
        {
            report = default;
            return false;
        }

        public bool Ray(Vector3 from, Vector3 to, uint mask, Godot.Collections.Array<Rid>? exclude,
            out RayReport report)
        {
            report = default;
            return false;
        }

        public bool Overlaps(IReadOnlyList<PlaneCollider.Part> parts, Transform3D pose, uint mask,
            Godot.Collections.Array<Rid>? exclude) => false;
    }

    private sealed class AlwaysOverlaps : IWorldQuery
    {
        public bool Sweep(IReadOnlyList<PlaneCollider.Part> parts, Transform3D baseTransform, Vector3 motion,
            uint mask, Godot.Collections.Array<Rid>? exclude, out SweepReport report)
        {
            report = default;
            return false;
        }

        public bool Ray(Vector3 from, Vector3 to, uint mask, Godot.Collections.Array<Rid>? exclude,
            out RayReport report)
        {
            report = default;
            return false;
        }

        public bool Overlaps(IReadOnlyList<PlaneCollider.Part> parts, Transform3D pose, uint mask,
            Godot.Collections.Array<Rid>? exclude) => true;
    }
}
