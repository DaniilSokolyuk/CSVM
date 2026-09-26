using Godot;

namespace CSVM.Mech3.Anim;

/// <summary>One live particle emitter, as much of it as <see cref="EmitterDirector"/> needs. The
/// director owns lifetime; how particles reach the GPU is the implementation's business. This exists
/// because <c>Effects.Puffer</c> is <c>sealed</c>, so a test double cannot BE one. Emitter
/// lifetime is assertable only with an interface as the director's collaborator. The animation
/// layer owns this seam and <c>Effects.PufferEmitterFactory</c> implements it.</summary>
public interface IEmitter
{
    int LiveCount { get; }

    /// <summary>Whether the emitter is still usable, the <c>IsInstanceValid</c> guard the follow
    /// and host-deactivation sweeps both make on a real one.</summary>
    bool IsValid { get; }

    /// <summary>Emit for this frame at a moving host's world pose.</summary>
    void SustainAt(Vector3 worldPos, Basis worldBasis, float dt);

    /// <summary>Stop emitting, leaving live particles to finish their lifetimes. Revivable, a
    /// later <c>PUFFER_STATE 1</c> on the same key resumes this same emitter.</summary>
    void SustainEnd();

    /// <summary>Drop live particles immediately, without waiting out their lifetimes.</summary>
    void Clear();

    /// <summary>Release the emitter itself. Nothing may use it afterwards.</summary>
    void Destroy();
}

/// <summary>Builds an emitter for a state, or nothing. A miss is NAMED rather than merely null:
/// "the textures for this puffer are absent", "this is a stub state with no textures to build from"
/// and "the texture archive is already released" are three different facts, each with its own
/// report line, and only the factory knows which one happened.
///
/// <para>The miss string is in the runtime's <c>Count()</c> vocabulary but is not counted here: one
/// real factory is shared by every runtime in a session, while the counter is per-runtime.</para></summary>
public interface IEmitterFactory
{
    IEmitter? Create(PufferState state, out string? miss);
}

/// <summary>One row of <see cref="EmitterDirector.Census"/>, a KNOWN emitter, whether or not it is
/// currently emitting. Active-only cannot tell a paused-but-revivable emitter from a forgotten one,
/// which is precisely the distinction <see cref="EmitterDirector.EndFor"/> and
/// <see cref="EmitterDirector.Discard"/> exist to make.</summary>
public readonly record struct EmitterCensusRow(
    string Name, string Host, string Def, bool Emitting, int LiveParticles, Node3D? HostNode);

/// <summary>What one <c>AnimRuntime.PrewarmEmitters</c> pass did: emitters built ahead, events
/// whose named host resolves to nothing on that runtime, and events hosted on their call site
/// (<c>INPUT_NODE</c>), which have no host until called and so cannot be built ahead.</summary>
public readonly record struct EmitterPrewarm(int Built, int Unhosted, int SelfHosted);
