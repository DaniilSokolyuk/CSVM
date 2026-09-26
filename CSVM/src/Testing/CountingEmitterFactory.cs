using System.Collections.Generic;
using CSVM.Effects;
using CSVM.Mech3;
using CSVM.Mech3.Anim;
using Godot;

namespace CSVM.Testing;

/// <summary>A no-GPU stand-in for <c>PufferEmitterFactory</c>: every <see cref="Create"/> succeeds
/// and hands back a <see cref="CountingEmitter"/>, which holds no Godot type in its own state, so a
/// suite can observe emitter lifetime with neither a <c>TextureArchive</c> nor a <c>MultiMesh</c>
/// anywhere in the path.</summary>
public sealed class CountingEmitterFactory : IEmitterFactory
{
    private readonly List<CountingEmitter> _built = new();

    /// <summary>Every fake this factory has handed out, in build order, what a suite reads to
    /// confirm the fake was actually reached rather than a real <c>Puffer</c>.</summary>
    public IReadOnlyList<CountingEmitter> Built => _built;

    public IEmitter? Create(PufferState state, out string? miss)
    {
        miss = null;
        var emitter = new CountingEmitter(state.Name);
        _built.Add(emitter);
        return emitter;
    }
}

/// <summary>One fake emitter: no Godot type anywhere in its state, just the counts a suite reads,
/// how many times it started and stopped sustaining, and whether it is sustaining now. Honest about
/// <see cref="SustainEnd"/>-then-revive: <see cref="IsValid"/> stays true after a stop, exactly like a
/// real <c>Puffer</c>, because <see cref="EmitterDirector"/> revives a stopped entry through its own
/// dictionary rather than asking the factory again.</summary>
public sealed record CountingEmitter(string Key) : IEmitter
{
    public int Started { get; private set; }
    public int Stopped { get; private set; }
    public bool Sustaining { get; private set; }
    public bool IsValid { get; private set; } = true;

    public int LiveCount => Sustaining ? 1 : 0;

    /// <summary>The world position <see cref="SustainAt"/> was last fed, what a suite reads to
    /// confirm the emitter is following its live host rather than a pose taken once at start.</summary>
    public Vector3 LastPos { get; private set; }

    public void SustainAt(Vector3 worldPos, Basis worldBasis, float dt)
    {
        if (!Sustaining)
            Started++;
        Sustaining = true;
        LastPos = worldPos;
    }

    public void SustainEnd()
    {
        if (Sustaining)
            Stopped++;
        Sustaining = false;
    }

    public void Clear()
    {
    }

    public void Destroy() => IsValid = false;
}
