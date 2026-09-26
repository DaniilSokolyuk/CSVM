using CSVM.Utils;

namespace CSVM.Mech3.Anim;

/// <summary>The factory a runtime has after its texture archive was released: it builds nothing,
/// names the miss, and says so once out loud.
/// ⚠ Do not silence the warning; a silent null object reads a world with no fire, dust or smoke as
/// a clean log. The bootstrap census cannot cover this: it prints before the first death,
/// ON_CALL sequence or range-deferred def can reach a PUFFER_STATE. It warns once per runtime,
/// because the condition is one build-time contract, so the first miss says what the thousandth
/// would.</summary>
public sealed class SpentEmitterFactory : IEmitterFactory
{
    private bool _reported;

    public IEmitter? Create(PufferState state, out string? miss)
    {
        miss = "PufferState(after build)";
        if (!_reported)
        {
            _reported = true;
            Log.Warn("anim", $"puffer '{state.Name}' asked for after the texture archive was released, this runtime builds no further PUFFER_STATE emitters (WorldSession.Options.TexturesOutliveBuild)");
        }
        return null;
    }
}
