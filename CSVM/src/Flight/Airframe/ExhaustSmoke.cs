using System;
using System.Collections.Generic;
using CSVM.Effects;
using CSVM.Mech3;
using CSVM.Utils;
using Godot;

namespace CSVM.Flight.Airframe;

/// <summary>
/// The engine exhaust smoke: a near-black distance trail from each <c>exhaust1..N</c> marker,
/// streamed while the commanded lever runs ahead of the live one. The original builds this puffer
/// in code rather than reading it from any reader file, so every constant here is a decoded one.
/// Decode and evidence: this module's entry in docs/architecture/Flight.md.
/// <see cref="Update"/> charges an intensity from the lever gap and decays it; the trail runs
/// while the intensity is above <see cref="OffIntensity"/>, and each particle takes the intensity
/// at its birth as its opacity, so a small or slow lever move leaves a plume too faint to see.
/// </summary>
public sealed class ExhaustSmoke
{
    // The intensity's exponential decay rate per second, and the level at or below which the
    // trail switches off (FUN_004afbc0).
    private const float DecayPerSecond = 1.5f;
    private const float OffIntensity = 0.01f;

    private readonly List<(Node3D Node, Puffer Trail)> _exhausts;

    private float _intensity;
    private bool _streaming;

    private ExhaustSmoke(List<(Node3D, Puffer)> exhausts) => _exhausts = exhausts;

    /// <summary>The charge the lever gap has built, before the clamp to an opacity. Read by the
    /// suite, since the only other evidence is the plume, which a headless run cannot see.</summary>
    internal float IntensityForTest => _intensity;

    /// <summary>Whether the trails are emitting this step, for the same suite.</summary>
    internal bool StreamingForTest => _streaming;

    /// <summary>How many exhaust markers carry a trail, and the particles alive on all of them.</summary>
    internal (int Trails, int LiveParticles) CensusForTest
    {
        get
        {
            int live = 0;
            foreach (var (_, trail) in _exhausts)
                live += trail.LiveCount;
            return (_exhausts.Count, live);
        }
    }

    /// <summary>Resolves the plane's <c>exhaust1..N</c> marker nodes in order, stopping at the
    /// first number missing as the original's search does, and builds one trail per marker. Null
    /// when the plane carries no exhaust markers or the smoke textures are missing (every call
    /// site null-checks).</summary>
    public static ExhaustSmoke? Build(Node3D planeRoot, TextureArchive textures, Node parent,
        EffectAmbience? ambience = null)
    {
        var exhausts = new List<(Node3D, Puffer)>();
        for (int i = 1; FindNode(planeRoot, $"exhaust{i}") is { } node; i++)
        {
            if (Puffer.Create(TrailState(), textures, ambience: ambience) is not { } trail)
                return null;
            parent.AddChild(trail);
            exhausts.Add((node, trail));
        }
        return exhausts.Count > 0 ? new ExhaustSmoke(exhausts) : null;
    }

    /// <summary>The flight step's drive, fed the commanded lever minus the live one as it stood
    /// before this step's slew. A negative gap charges nothing and the charge decays either way.
    /// ⚠ Drive this from the sim step, never from a rendered frame: the gap closes only inside the
    /// step, so a frame carrying none would charge the same gap twice.</summary>
    public void Update(float dt, float leverGap)
    {
        if (leverGap >= 0f)
            _intensity += dt * leverGap;
        _intensity *= MathF.Exp(-DecayPerSecond * dt);

        if (_intensity <= OffIntensity)
        {
            if (_streaming)
            {
                _streaming = false;
                foreach (var (_, trail) in _exhausts)
                    trail.Stop();
            }
            return;
        }

        if (!_streaming)
            Log.Info("flight", $"exhaust smoke on: lever gap {leverGap:0.000}, intensity {_intensity:0.000}");
        _streaming = true;
        float alpha = MathF.Min(_intensity, 1f);
        foreach (var (node, trail) in _exhausts)
        {
            trail.BirthAlpha = alpha;
            trail.Emit(node.GlobalPosition, node.GlobalTransform.Basis, dt);
        }
    }

    /// <summary>Crash, respawn and placement call this: drops the charge and clears every live
    /// particle, so a wreck or a freshly placed aircraft shows no plume.</summary>
    public void Reset()
    {
        _intensity = 0f;
        _streaming = false;
        foreach (var (_, trail) in _exhausts)
            trail.Clear();
    }

    /// <summary>The trail every exhaust marker carries, the original's hard-coded emitter
    /// (<c>FUN_004afa20</c>): one particle per 0.4 m of marker travel, sized 0.2..0.3 and growing
    /// to 3.45 times that over a 0.5..1.5 s life, from the static <c>smoke101..103</c> pool, fading
    /// out between 200 and 300 m of view depth. The ramp's first stop holds the colour
    /// <c>FUN_004afbc0</c> installs at age 0.2, at full opacity; the intensity scales it per
    /// particle through <see cref="Puffer.BirthAlpha"/>.</summary>
    internal static PufferState TrailState() => new()
    {
        Name = "exhaust",
        DistanceInterval = 0.4f,
        TimeInterval = PufferState.StillHostSputterInterval,
        MinRandomVelocity = new Vector3(-0.1f, -0.1f, -0.1f),
        MaxRandomVelocity = new Vector3(0.1f, 0.1f, 0.1f),
        Friction = 1.2f,
        SizeMin = 0.2f,
        SizeMax = 0.3f,
        LifetimeMin = 0.5f,
        LifetimeMax = 1.5f,
        GrowthFactor = 3.45f,
        DeviationDistance = 0.001f,
        FarFadeStart = 200f,
        FarFadeEnd = 300f,
        Textures = new[] { "smoke101", "smoke102", "smoke103" },
        Colors = new[]
        {
            (0.2f, new Color(7f / 255f, 7f / 255f, 9f / 255f, 1f)),
            (1f, new Color(0f, 0f, 0f, 0f)),
        },
    };

    private static Node3D? FindNode(Node root, string name)
    {
        foreach (var child in root.GetChildren())
        {
            if (child is Node3D n3d && n3d.HasMeta(AnimRuntime.NameMeta)
                && n3d.GetMeta(AnimRuntime.NameMeta).AsString().Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return n3d;
            }
            if (FindNode(child, name) is { } found)
                return found;
        }
        return null;
    }
}
