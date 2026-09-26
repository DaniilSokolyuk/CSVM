using System.Collections.Generic;
using CSVM.Flight.Airframe;
using CSVM.Flight.Weapons;
using Godot;
using Xunit;

namespace CSVM.Tests;

/// <summary>
/// The original's alternate-step sweep (<see cref="SweepCadence"/>) driving the real
/// <see cref="AircraftContactResolver"/> and a real <see cref="PlaneDamage"/> against a kinematic
/// wall, with no engine in the process. Decode: docs/org/flightModel.md "Collision response". The
/// defect these rows pin is the port that ran the sweep every step and gated the SPEND on the
/// parity: a contact resolved on the other step was placed and bounced for free, and a scrape whose
/// re-contacts landed on those steps never spent at all, which at the controls read as an airframe
/// that grazes once and is invulnerable afterwards.
/// </summary>
public class SweepCadenceTests
{
    private const float Dt = 1f / 60f;

    /// <summary>The cadence itself: every other step sweeps, and the sweep after a skipped step
    /// runs from the pose the skipped step entered with, so no motion is lost between sweeps.</summary>
    [Fact]
    public void EveryOtherStepSweepsFromWhereTheSkippedStepBegan()
    {
        var cadence = new SweepCadence();

        Assert.True(cadence.Advance(new Vector3(0f, 0f, 0f), out var from0));
        Assert.Equal(new Vector3(0f, 0f, 0f), from0);   // fresh: sweeps from where it entered
        Assert.False(cadence.Advance(new Vector3(1f, 0f, 0f), out _));
        Assert.True(cadence.Advance(new Vector3(2f, 0f, 0f), out var from2));
        Assert.Equal(new Vector3(1f, 0f, 0f), from2);   // carried: the skipped step's origin
        Assert.False(cadence.Advance(new Vector3(3f, 0f, 0f), out _));

        cadence.Reset();
        Assert.True(cadence.Advance(new Vector3(9f, 0f, 0f), out var fromReset));
        Assert.Equal(new Vector3(9f, 0f, 0f), fromReset); // a respawn forgets the carried motion
    }

    /// <summary>The sequence from the controls: a shallow graze, then the pilot turns back into
    /// the same wall at a steeper angle. Both contacts spend the pair, so the hull walks
    /// 80 → 60 → 40 on the install's 50 floor rather than stopping after the first.</summary>
    [Fact]
    public void AShallowGrazeThenASteeperContactBothSpend()
    {
        var wall = new WallScrape(sweepEveryStep: false, spendPhase: null);
        var hulls = new List<float>();

        wall.Fly(new Vector3(2f, 0f, 0f), new Vector3(-1f, 0f, -100f), closingAccel: 0f, steps: 200,
            onSpend: hull => hulls.Add(hull));
        Assert.Single(hulls);
        Assert.Equal(60f, hulls[0], 3);          // one zone zeroed: 20 hp off an 80 hull

        // Back into the wall from just off it, steeper: the contact resolves on the next sweep.
        wall.Fly(wall.Position, new Vector3(-20f, 0f, -100f), closingAccel: 0f, steps: 4,
            onSpend: hull => hulls.Add(hull));
        Assert.Equal(2, hulls.Count);
        Assert.Equal(40f, hulls[1], 3);          // the dead zone redirects; the hull keeps falling
        Assert.False(wall.Crashed);
    }

    /// <summary>A sustained steeper scrape (the pilot holding the nose into the wall) re-contacts on
    /// every sweep step and spends every time, so a bounded ledger dies by the decoded health rule
    /// within a few steps of the scrape starting.</summary>
    [Fact]
    public void ASustainedScrapeSpendsOnEverySweepUntilTheLedgerDies()
    {
        var wall = new WallScrape(sweepEveryStep: false, spendPhase: null);
        int spends = 0;

        wall.Fly(new Vector3(0.15f, 0f, 0f), new Vector3(-5f, 0f, -100f), closingAccel: 54f, steps: 200,
            onSpend: _ => spends++);

        Assert.True(wall.Crashed);
        Assert.True(wall.Ledger.IsDestroyed);
        // Three 50-floor spends: each zone absorbs 20 of the pair and the wrapper loop drains the
        // leftover from the hull directly, so the hull walks 60, 40, 0 with a zone still whole.
        Assert.Equal(3, spends);
        Assert.True(wall.StepsFlown < 20, $"died at step {wall.StepsFlown}");
    }

    /// <summary>The control, on the rule this replaced (METHOD-9): sweeping every step and spending
    /// only on the parity steps, the same scrape's re-contacts land on the other steps every time,
    /// so it is placed on every contact and never spends once. That is the invulnerability the
    /// controls reported, and the row above must be able to fail into it.</summary>
    [Fact]
    public void SpendGatedOnTheParityLetsAScrapeLockOntoTheFreeSteps()
    {
        var wall = new WallScrape(sweepEveryStep: true, spendPhase: 1);
        int spends = 0;

        wall.Fly(new Vector3(0.15f, 0f, 0f), new Vector3(-5f, 0f, -100f), closingAccel: 54f, steps: 200,
            onSpend: _ => spends++);

        Assert.True(wall.Contacts > 50, $"contacts={wall.Contacts}");
        Assert.Equal(0, spends);
        Assert.False(wall.Crashed);
        Assert.Equal(80f, wall.Ledger.WholeHealth, 3);
    }

    /// <summary>A point airframe flying at a wall on x = 0 (normal +X), stepped by hand: the
    /// resolver decides each contact, the ledger takes the spend, and the response is the decoded
    /// placement (0.03 m off the surface, the normal velocity removed). <c>sweepEveryStep</c> with a
    /// <c>spendPhase</c> reproduces the rule this replaced; otherwise <see cref="SweepCadence"/>
    /// decides which steps sweep and every resolved contact spends.</summary>
    private sealed class WallScrape : IContactEffects
    {
        private const float PushOut = 0.03f;

        private readonly AircraftContactResolver _resolver = new(new NoOverlaps());
        private readonly SweepCadence _cadence = new();
        private readonly bool _sweepEveryStep;
        private readonly int? _spendPhase;
        private readonly PlaneStats _stats = new()
        {
            CollideArmorFloor = 50f,
            CollideArmorScale = 300f,
            CollideHealthFloor = 50f,
            CollideHealthScale = 300f,
        };

        private Vector3 _from;
        private Vector3 _velocity;
        private float _stopFraction;
        private Vector3 _impact;
        private int _step;
        private bool _spendsThisStep;

        public WallScrape(bool sweepEveryStep, int? spendPhase)
        {
            _sweepEveryStep = sweepEveryStep;
            _spendPhase = spendPhase;
        }

        public PlaneDamage Ledger { get; } = new(new List<DestroyablePart>
        {
            new() { Name = "nose", MaxHp = 20f, MaxArmor = 20f },
            new() { Name = "tail", MaxHp = 20f, MaxArmor = 20f },
            new() { Name = "leftwing", MaxHp = 20f, MaxArmor = 20f },
            new() { Name = "rightwing", MaxHp = 20f, MaxArmor = 20f },
        });

        public Vector3 Position { get; private set; }

        public bool Crashed { get; private set; }

        public int Contacts { get; private set; }

        public int StepsFlown => _step;

        /// <summary>Flies from <paramref name="start"/> at <paramref name="velocity"/>, the pilot
        /// holding <paramref name="closingAccel"/> m/s² into the wall, until the step budget runs
        /// out or the ledger dies. <paramref name="onSpend"/> reports the hull after each spend.</summary>
        public void Fly(Vector3 start, Vector3 velocity, float closingAccel, int steps,
            System.Action<float> onSpend)
        {
            Position = start;
            _velocity = velocity;
            for (int i = 0; i < steps && !Crashed; i++)
            {
                _step++;
                var entered = Position;
                bool sweeps = _cadence.Advance(entered, out var carried);
                if (_sweepEveryStep)
                {
                    sweeps = true;
                    carried = entered;
                }

                _velocity += new Vector3(-closingAccel, 0f, 0f) * Dt;
                Position += _velocity * Dt;
                if (!sweeps || Position.X >= 0f || carried.X < 0f)
                    continue;

                Contacts++;
                _from = carried;
                _stopFraction = carried.X / (carried.X - Position.X);
                _impact = carried + (Position - carried) * _stopFraction;
                _spendsThisStep = _spendPhase == null || _step % 2 == _spendPhase;
                float hullBefore = Ledger.WholeHealth;
                var outcome = _resolver.Resolve(Report(), Striker(), this);
                if (Ledger.WholeHealth < hullBefore)
                    onSpend(Ledger.WholeHealth);
                if (outcome.Fate == ContactFate.Crash)
                    Crashed = true;
            }
        }

        public bool ShatterStruck(float healthDamage) => false;

        public void PlayGrazeReaction()
        {
        }

        public PlaneDamage.PartState? SpendDamage(string zone, float healthDamage, float armorDamage) =>
            _spendsThisStep ? Ledger.Apply(zone, healthDamage, armorDamage) : Ledger.Apply(zone, 0f, 0f);

        public ContactResponse ApplyResponse()
        {
            Position = _from + (Position - _from) * _stopFraction + new Vector3(PushOut, 0f, 0f);
            _velocity = new Vector3(0f, _velocity.Y, _velocity.Z);
            return new ContactResponse(new Transform3D(Basis.Identity, Position));
        }

        private ContactReport Report() => new()
        {
            Impact = _impact,
            Normal = Vector3.Right,
            Part = "wing",
            ColliderName = "wall",
            StopFraction = _stopFraction,
            StruckIsAircraft = false,
        };

        private ContactConditions Striker() => new()
        {
            IsHumanPiloted = true,
            VelocityDir = _velocity.Normalized(),
            Speed = _velocity.Length(),
            Pose = Transform3D.Identity,
            Stats = _stats,
            Ledger = Ledger,
            Parts = null,
            ExcludeSelf = null,
        };
    }

    private sealed class NoOverlaps : IWorldQuery
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
}
