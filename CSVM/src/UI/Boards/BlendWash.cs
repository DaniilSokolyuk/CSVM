using System;
using Godot;

namespace CSVM.UI.Boards;

/// <summary>
/// One pane's victim-routed screen wash: the colour, weight and envelope of the original's
/// sonic/flash/smoke wash (<c>FUN_0042e9d0</c> starts it, <c>FUN_0042eb80</c> ticks it), held per
/// pane instead of in the original's one global. Overlapping washes blend rather than replace:
/// the weights combine as <c>p + w − p·w</c> and the colour is mixed toward the new one by the
/// incoming weight. The envelope is attack, sustain, release at 0.15 / 0.50 / 0.35 of the duration.
/// Pure state and arithmetic, no node, so the rules are unit-tested off the engine; the pane
/// paint is <see cref="ScreenFlash"/>'s, which composites this over its proximity ramp.
/// Decode: docs/org/ordnanceTypes.md, the sonic/flash section.
/// </summary>
public sealed class BlendWash
{
    /// <summary>The attack phase is this fraction of the duration: the displayed weight climbs
    /// linearly from wherever it stands to <see cref="Peak"/>.</summary>
    public const float AttackFraction = 0.15f;

    /// <summary>The release phase is this fraction of the duration, at the end: the peak decays by
    /// <c>dt / release</c> of itself every step, and the wash is cut at the duration.</summary>
    public const float ReleaseFraction = 0.35f;

    private float _delay;
    private float _attack;
    private float _release;

    /// <summary>Whether a wash is running here (including a wash still inside its start delay,
    /// which paints nothing yet).</summary>
    public bool Running { get; private set; }

    /// <summary>The wash colour, RGB only; the alpha to paint it at is <see cref="Weight"/>.</summary>
    public Color Colour { get; private set; }

    /// <summary>The weight painted this frame: the envelope's output, in [0, <see cref="Peak"/>].</summary>
    public float Weight { get; private set; }

    /// <summary>The weight the envelope sustains at, and the <c>p</c> a later hit blends against.
    /// Decays through the release phase, so a re-hit late in a wash meets a lighter one.</summary>
    public float Peak { get; private set; }

    /// <summary>Seconds into the envelope (or into the start delay while one is pending).</summary>
    public float Elapsed { get; private set; }

    /// <summary>The wash's whole duration in seconds; the envelope is cut when
    /// <see cref="Elapsed"/> reaches it.</summary>
    public float Duration { get; private set; }

    /// <summary>The overlay colour a pane paints when this wash is drawn OVER
    /// <paramref name="under"/> (the proximity ramp's RGBA): the standard alpha "over" of the two,
    /// folded into one non-premultiplied RGBA. With no wash it is <paramref name="under"/> exactly,
    /// which is what keeps the ramp channel's own picture unchanged.</summary>
    public static Color Composite(Color under, Color washColour, float weight)
    {
        if (weight <= 0f)
            return under;
        weight = Math.Min(weight, 1f);
        float alpha = under.A + weight * (1f - under.A);
        if (alpha <= 0f)
            return new Color(0f, 0f, 0f, 0f);
        float keep = under.A * (1f - weight);
        return new Color(
            (under.R * keep + washColour.R * weight) / alpha,
            (under.G * keep + washColour.G * weight) / alpha,
            (under.B * keep + washColour.B * weight) / alpha,
            alpha).Clamp();
    }

    /// <summary>Starts a wash, or blends one into the wash already running. A non-positive
    /// <paramref name="duration"/> clears the pane instead, as the original's routine does.
    /// <paramref name="startDelay"/> holds the picture clear for that long before the envelope
    /// begins; it is read on the FIRST hit only, a re-hit keeps whatever delay is pending.</summary>
    public void Start(Color colour, float weight, float duration, float startDelay = 0f)
    {
        if (duration <= 0f)
        {
            Clear();
            return;
        }
        if (!Running)
        {
            _delay = startDelay;
            Peak = weight;
            Weight = 0f;
            Colour = new Color(colour.R, colour.G, colour.B, 1f);
        }
        else
        {
            // The original's arithmetic, kept literally: the mix is weighted by the OLD peak and
            // the incoming weight, then normalised by the NEW peak plus the incoming weight.
            float p = Peak;
            Peak = p + weight - p * weight;
            var mixed = new Color(
                Colour.R * p + colour.R * weight,
                Colour.G * p + colour.G * weight,
                Colour.B * p + colour.B * weight,
                1f);
            if (Peak > 0f)
            {
                float norm = 1f / (Peak + weight);
                mixed = new Color(mixed.R * norm, mixed.G * norm, mixed.B * norm, 1f);
            }
            Colour = mixed;
        }
        Duration = duration;
        _release = duration * ReleaseFraction;
        _attack = duration * AttackFraction;
        Elapsed = 0f;
        Running = true;
    }

    /// <summary>Stops the wash and forgets its colour and weights.</summary>
    public void Clear()
    {
        Running = false;
        Duration = 0f;
        _release = 0f;
        _attack = 0f;
        _delay = 0f;
        Elapsed = 0f;
        Peak = 0f;
        Weight = 0f;
        Colour = new Color(0f, 0f, 0f, 0f);
    }

    /// <summary>Advances the envelope by one step of <paramref name="dt"/> sim seconds. Returns
    /// whether the pane's paint may have changed (a wash was running when the step began).</summary>
    public bool Step(float dt)
    {
        if (!Running)
            return false;
        Elapsed += dt;
        if (_delay > 0f)
        {
            if (Elapsed < _delay)
                return true;
            _delay = 0f;
            Elapsed = 0f;
        }
        if (Elapsed >= Duration)
        {
            Clear();
            return true;
        }
        if (Elapsed > _attack)
        {
            // Half a step of slack on the release boundary, so a step that lands exactly on it is
            // already the first decaying one; the original ticks the same comparison.
            if (Elapsed > Duration - _release + dt * 0.5f)
            {
                Peak = Weight - (dt / _release) * Peak;
                Weight = Peak;
            }
            else
            {
                Weight = Peak;
            }
        }
        else
        {
            Weight = Math.Min(Peak, Weight + (dt / _attack) * Peak);
        }
        if (Weight < 0f)
            Weight = 0f;
        return true;
    }
}
