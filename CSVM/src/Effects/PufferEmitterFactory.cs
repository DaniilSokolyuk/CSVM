using CSVM.Mech3;
using CSVM.Mech3.Anim;
using Godot;

namespace CSVM.Effects;

/// <summary>Builds real <see cref="Puffer"/> emitters from a session's textures and parents them.
/// One shared factory for the world, world-effects and per-player crash runtimes, whose needs are
/// byte-identical. The animation layer owns the seam (<see cref="IEmitterFactory"/>); this is its
/// one real implementation.</summary>
public sealed class PufferEmitterFactory : IEmitterFactory
{
    private readonly TextureArchive _textures;
    private readonly Node _parent;
    // The session's wind, read by every emitter this builds. Still air when a caller has no
    // session to take it from (the suites' fake runtimes).
    private readonly EffectAmbience _ambience;

    /// <param name="parent">⚠ The world root, never a per-player crash root. A PUFFER_STATE
    /// emitter goes TopLevel the moment it emits, so parenting under a controller subtree leaves
    /// it drawn-but-unrendered.</param>
    /// <param name="textures">⚠ Must stay open for this factory's life; an emitter bakes its
    /// atlas at construction. See <see cref="EmitterDirector.RetireFactory"/>.</param>
    public PufferEmitterFactory(TextureArchive textures, Node parent, EffectAmbience? ambience = null)
    {
        _textures = textures;
        _parent = parent;
        _ambience = ambience ?? EffectAmbience.Still;
    }

    public IEmitter? Create(PufferState state, out string? miss)
    {
        // A PUFFER_STATE with no textures is an adjust/stop stub re-asserting a puffer some other
        // event defines; there is nothing to build from it.
        if (state.Textures.Count == 0 && state.TextureSequence.Count == 0)
        {
            miss = $"PufferState(stub, no textures: {state.Name})";
            return null;
        }
        if (Puffer.Create(state, _textures, sustained: true, ambience: _ambience) is not { } puffer)
        {
            // Name it: a puffer whose textures are absent from this chapter's archive is a
            // data-coverage fact worth being able to look up, not an anonymous count.
            miss = $"PufferState(no texture: {state.Name})";
            return null;
        }
        _parent.AddChild(puffer);
        miss = null;
        return new PufferEmitter(puffer);
    }
}

/// <summary>The real emitter: a <see cref="Puffer"/> behind <see cref="IEmitter"/>.</summary>
internal sealed class PufferEmitter : IEmitter
{
    private readonly Puffer _puffer;

    internal PufferEmitter(Puffer puffer) => _puffer = puffer;

    public int LiveCount => _puffer.LiveCount;

    public bool IsValid => GodotObject.IsInstanceValid(_puffer);

    public void SustainAt(Vector3 worldPos, Basis worldBasis, float dt) =>
        _puffer.Emit(worldPos, worldBasis, dt);

    public void SustainEnd() => _puffer.Stop();

    public void Clear() => _puffer.Clear();

    public void Destroy() => _puffer.QueueFree();
}
