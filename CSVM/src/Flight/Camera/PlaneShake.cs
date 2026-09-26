using System;
using CSVM.Utils;

namespace CSVM.Flight.Camera;

/// <summary>
/// The plane-wobble oscillators (<c>shakes.json</c> via <see cref="ShakeDefs"/>): gunfire buzz,
/// overspeed rattle, being-hit rocks and the nitro engage, summed into <see cref="Roll"/>, radians the flight
/// rig applies to a pivot node between the <see cref="Airframe.FlightController"/> and its plane model.
/// Decode: docs/org/shakes.md, which carries the random-walk law and the component block the
/// overspeed and nitro sources run, and what the other sources still do. Engine-free on purpose:
/// the pivot write is the controller's one line, the law lives here where the unit tests reach.
/// ⚠ Everything visual rides the pivot; physics, aim and the chase camera read the controller's
/// own transform and must never read the pivot's.
/// ⚠ Every random step must come from the injected <see cref="Random"/>, never a fresh one, or
/// a <c>--det</c> burst stops replaying to the same wobble trace.</summary>
public sealed class PlaneShake
{
    /// <summary>Per-shot gun-buzz step scale. The decoded law is ±<c>7.54·(factor×caliber)</c>;
    /// scale 1 reproduces the original's faithful step (= ±2.11e-2 rad for wep40), lower tames it.
    /// The one tune knob for the fidelity judgment, the mechanism (random walk) is what BL-266(a)
    /// is evaluating, this is how loud the wobble reads.</summary>
    public const float GunBuzzKickScale = 1f;

    /// <summary>Per-tick overspeed-rattle step scale, the dive rattle's one tune knob. Scale 1 is
    /// the original's own velocity kick into its component block, so dial this and never
    /// <c>magnitude_quotient</c>.</summary>
    public const float DiveRattleKickScale = 1f;

    /// <summary>Per-engage nitro-wobble step scale, the same knob for the nitro source. Scale 1 is
    /// the original's own velocity kick, so dial this and never the authored <c>magnitude</c>.</summary>
    public const float NitroWobbleKickScale = 1f;

    // What each impact source's magnitude_factor multiplies is authored for fire_bullet only
    // (caliber, measured). For being hit: an incoming gun round reuses the caliber law; a rocket
    // has no caliber, so its armor damage stands in, doubled by he_factor when HIGH_EXPLOSIVE.
    // The stand-ins are declared TUNE pending being-hit footage.
    private readonly Osc _fire = new();
    private readonly Osc _bulletHit = new();
    private readonly Osc _missileHit = new();
    private readonly Osc _explosion = new();

    // The two sources that run the original's component block whole (velocity kick, ramp-and-
    // reverse integrator) rather than an envelope: the overspeed rattle re-kicked every tick the
    // gate is open, and the nitro engage's single kick. docs/org/shakes.md.
    private readonly Block? _speed;
    private readonly Block? _nitro;

    // Block 5 (camera+0xf4) is the one oscillator no data authors: shakes.zrd names no `turbulence`
    // source, so the constructor's law stands and the collision path is the block's only kicker
    // (docs/org/shakes.md). ⚠ Do not give it a def, these three constants ARE its law.
    private readonly Osc _contact = new()
    {
        Src = new ShakeSource { Id = "contact", Frequency = 2f, Damp = 4.5f, Sawtooth = false },
    };

    // The random-walk steps of all three walking sources. Injected for off-engine determinism
    // (see the class summary); the session resolves Rng.NewSystemRandom(Rng.Shake).
    private readonly Random _rng;

    // This tick's high_speed magnitude, the excess over the authored gate; zero below it.
    private float _speedDrive;

    /// <param name="rng">The stream the walking sources draw their steps from. Defaults to
    /// <see cref="Rng.NewSystemRandom(Rng.Shake)"/> (deterministic under <c>--det</c>); tests pass
    /// a fixed-seed <see cref="Random"/> so the wobble trace is pinned without the engine.
    /// Accepting an engineer-supplied <see cref="Random"/> (never the shared Godot stream directly)
    /// is the repo's off-engine-testing pattern (cf. BandFlicker, AiModeMachine).</param>
    public PlaneShake(ShakeDefs defs, Random? rng = null)
    {
        _fire.Src = defs.FireBullet;
        _bulletHit.Src = defs.BulletImpact;
        _missileHit.Src = defs.MissileImpact;
        _explosion.Src = defs.Explosion;
        _speed = defs.HighSpeed is { } highSpeed ? new Block(highSpeed) : null;
        _nitro = defs.Nitro is { } nitro ? new Block(nitro) : null;
        _rng = rng ?? Rng.NewSystemRandom(Rng.Shake);
    }

    /// <summary>The summed wobble after the last <see cref="Advance"/>, radians of roll.</summary>
    public float Roll { get; private set; }

    /// <summary>One gun round left this plane: add a random step to the firing random-walk
    /// accumulator (the original's gun buzz, decoded from crimson.exe FUN_0042be10). Each shot's
    /// step is uniform in ±<c>7.54·(factor×caliber)</c> (× <see cref="GunBuzzKickScale"/>); the walk
    /// decays with the source's authored <c>damp</c> in <see cref="Advance"/>, so a steady 8/s burst
    /// reads as a bounded rattle, not a monotonic roll-off.</summary>
    public void FireBullet(float caliber)
    {
        if (_fire.Src is { MagnitudeFactor: { } f })
        {
            _fire.RandomWalkKick(f * caliber * GunBuzzKickScale, _rng);
        }
    }

    /// <summary>A gun round struck this plane.</summary>
    public void BulletHit(float caliber)
    {
        if (_bulletHit.Src is { MagnitudeFactor: { } f })
        {
            _bulletHit.Kick(f * caliber);
        }
    }

    /// <summary>A rocket/ordnance round struck this plane; <paramref name="damage"/> stands in
    /// for the unauthored per-event quantity, doubled by <c>he_factor</c> on HE rounds.</summary>
    public void MissileHit(float damage, bool highExplosive)
    {
        if (_missileHit.Src is { MagnitudeFactor: { } f })
        {
            float he = highExplosive ? _missileHit.Src.HeFactor ?? 1f : 1f;
            _missileHit.Kick(f * damage * he);
        }
    }

    /// <summary>A nearby detonation (not a direct hit) rocked this plane.</summary>
    public void ExplosionAt(float damage)
    {
        if (_explosion.Src is { MagnitudeFactor: { } f })
        {
            _explosion.Kick(f * damage);
        }
    }

    /// <summary>The nitro engaged on this plane: one random velocity kick of the source's absolute
    /// authored <c>magnitude</c> into its component block (block 6 at <c>0x4b21ce</c>,
    /// docs/org/shakes.md), which the block's ramp law then swings out and decays.
    /// The caller gates it on a human pilot, as the original gates it on the player.</summary>
    public void NitroEngaged()
    {
        if (_nitro?.Src is { Magnitude: { } m })
        {
            _nitro.Kick(m * NitroWobbleKickScale, _rng);
        }
    }

    /// <summary>One resolved contact rocked this plane: block 5's kick at <c>0x48d409</c>, whose
    /// <paramref name="magnitude"/> is <see cref="Airframe.CollisionDamage.ContactShake"/>'s radians.
    /// The caller gates it on a human pilot, as the original gates it on the player, and a graze
    /// reaches it like any other contact: the only gate the original puts on the call is a positive
    /// severity cosine.</summary>
    public void ContactHit(float magnitude)
    {
        if (magnitude > 0f)
        {
            _contact.Kick(magnitude);
        }
    }

    /// <summary>Per-tick overspeed drive: <paramref name="speedRatio"/> is speed over the
    /// plane's rated max, so the authored <c>min_speed</c> 1.0 gate reads "beyond rated max",
    /// the dive rattle. Magnitude is the EXCESS over the gate <c>(speedRatio − gate)/quotient</c>,
    /// not the whole ratio: zero at rated max, gentle ramp with overspeed (a whole-ratio reading
    /// snapped on at <c>1.0/70</c> = 5× the gun buzz). What it feeds is the next
    /// <see cref="Advance"/>'s velocity kick, not a displacement.</summary>
    public void SetSpeedRatio(float speedRatio)
    {
        _speedDrive = _speed?.Src is { MinSpeed: { } gate, MagnitudeQuotient: > 0f } src
                      && speedRatio > gate
            ? (speedRatio - gate) / src.MagnitudeQuotient!.Value
            : 0f;
    }

    /// <summary>Advances every oscillator by one sim tick and re-sums <see cref="Roll"/>.
    /// Pure function of sim dt and the events since the last tick, deterministic under
    /// <c>--det</c>'s fixed clock.</summary>
    public void Advance(float dt)
    {
        if (dt <= 0f)
        {
            return;
        }
        // The overspeed source is re-kicked every tick the gate is open, as the original kicks it
        // from its own per-frame player updater, so the dive rattle is sustained accumulation.
        if (_speedDrive > 0f)
        {
            _speed?.Kick(_speedDrive * DiveRattleKickScale, _rng);
        }
        _speed?.Advance(dt);
        _nitro?.Advance(dt);
        Roll = _fire.Advance(dt) + _bulletHit.Advance(dt) + _missileHit.Advance(dt)
               + _explosion.Advance(dt) + _contact.Advance(dt)
               + (_speed?.Roll ?? 0f) + (_nitro?.Roll ?? 0f);
    }

    private sealed class Osc
    {
        public ShakeSource? Src;
        public float Amp;      // current envelope, radians
        public float Phase;    // waveform cycles, wraps at 1
        public float Walk;     // random-walk accumulator (fire source, BL-266(a) branch)

        public float Advance(float dt)
        {
            if (Src == null)
            {
                return 0f;
            }
            if (Walk != 0f)
            {
                // Random-walk accumulator (fire source): no waveform, the accumulated roll is the
                // wobble, decaying toward 0 between shots at the authored damp.
                Walk *= MathF.Exp(-Src.Damp * dt);
                if (MathF.Abs(Walk) < 1e-5f)
                {
                    Walk = 0f;
                }
                return Walk;
            }
            // every remaining source is an impulse: the envelope decays toward rest at its damp
            Amp *= MathF.Exp(-Src.Damp * dt);
            if (Amp <= 1e-6f)
            {
                Amp = 0f;
                return 0f;
            }
            Phase += Src.Frequency * dt;
            Phase -= MathF.Floor(Phase);
            float wave = Src.Sawtooth
                ? 2f * Phase - 1f
                : MathF.Sin(2f * MathF.PI * Phase);
            return Amp * wave;
        }

        public void Kick(float magnitude)
        {
            Amp = Math.Max(Amp, magnitude);
        }

        /// <summary>Add a uniform random step to the random-walk accumulator. <paramref name="magnitude"/>
        /// is <c>factor×caliber</c>; the ×2.0×6.2832×1.2 gain (see class summary) and the injected
        /// rng give the step its direction, so over many shots the walk drifts across the roll axis
        /// like the original's camera accumulator, bounded by the <see cref="Advance"/> decay.</summary>
        public void RandomWalkKick(float magnitude, Random rng)
        {
            // u ∈ [0,1); (2u−1) ∈ (−1,1). 2.0×6.2832×1.2 = 15.08; × the (rand−0.5) half-range 0.5
            // collapses to the 7.54 the class docs quote.
            float u = 2f * (float)rng.NextDouble() - 1f;
            Walk += u * magnitude * 7.54f;
        }
    }

    // One of the original's seven camera component blocks, ported whole. A roll VELOCITY the kickers
    // walk at random, and the POSITION it integrates into, which is what renders. The authored
    // sawtooth picks both laws, the kick's waveform factor and the integrator's, so a source's rate
    // and decay fall out of its own law. Decode with every address: docs/org/shakes.md.
    // ⚠ The kick is a velocity, not an angle. The rendered wobble is a fraction of it, set by how
    // far the ramp travels before the reversal test turns it round.
    private sealed class Block
    {
        // The original integrates at this fixed substep inside whatever its frame was, and runs
        // the remainder short rather than long; a 60 Hz sim tick is two and a half of them.
        private const float SubStep = 1f / 150f;

        // The roll below which the block is at rest, paired with the velocity that could still
        // swing it that far. Neither law reaches zero on its own, and a ringing denormal would
        // keep writing the pivot forever.
        private const float RestPos = 1e-6f;

        private float _vel;   // rad/s, what a kick adds to
        private float _pos;   // rad, what the plane node is rolled by

        public Block(ShakeSource src) => Src = src;

        public ShakeSource Src { get; }

        public float Roll => _pos;

        /// <summary>One kicker's random velocity step: uniform in
        /// ±<c>0.6·magnitude·frequency·W</c>, W being 4 on a <c>sawtooth</c> source and 2π
        /// otherwise. Roll takes the ×1.2 axis weight, yaw's ×2.5 is unported because the pivot
        /// rolls only.</summary>
        public void Kick(float magnitude, Random rng)
        {
            float wave = Src.Sawtooth ? 4f : MathF.Tau;
            _vel += ((float)rng.NextDouble() - 0.5f) * magnitude * Src.Frequency * wave * 1.2f;
        }

        /// <summary>Integrates the block over one sim tick: a damped spring on a smooth source,
        /// and on a <c>sawtooth</c> one the ramp-and-reverse that draws the buzzy triangle, where
        /// an outward-moving block slower than <c>4·frequency·|pos|</c> has its velocity turned
        /// round to <c>−4·frequency·e^(−damp/2·frequency)·pos</c>, which is where the decay
        /// lives.</summary>
        public void Advance(float dt)
        {
            if (Src.Frequency <= 0f)
            {
                return;
            }
            for (float left = dt; left > 1e-7f;)
            {
                float h = MathF.Min(SubStep, left);
                if (!Src.Sawtooth)
                {
                    float w = Src.Frequency * MathF.Tau;
                    _vel -= ((Src.Damp * _vel) + (w * w * _pos)) * h;
                }
                else if (_pos * _vel > 0f && MathF.Abs(_vel) < MathF.Abs(_pos) * Src.Frequency * 4f)
                {
                    _vel = -4f * Src.Frequency * MathF.Exp(-Src.Damp * 0.5f / Src.Frequency) * _pos;
                }
                _pos += _vel * h;
                left -= h;
            }
            float restVel = RestPos * Src.Frequency * (Src.Sawtooth ? 4f : MathF.Tau);
            if (MathF.Abs(_pos) < RestPos && MathF.Abs(_vel) < restVel)
            {
                _pos = 0f;
                _vel = 0f;
            }
        }
    }
}
