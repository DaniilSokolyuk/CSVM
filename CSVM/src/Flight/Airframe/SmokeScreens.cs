using System;
using System.Collections.Generic;
using System.IO;
using CSVM.Effects;
using CSVM.Mech3;
using CSVM.Utils;
using Godot;

namespace CSVM.Flight.Airframe;

/// <summary>The smoke screen's blend-wash sink, <c>ScreenFlash.PlayBlend</c>'s shape without the
/// start delay (the smoke caller passes none): the victim's player index, the colour, the weight
/// and the duration.</summary>
public delegate void SmokeWashSink(int playerIndex, Color colour, float weight, float durationSeconds);

/// <summary>Builds one screen's emitter. Null (either the delegate or its result) means this build
/// has no particle runtime to draw with, which is every off-engine and headless caller.</summary>
public delegate ISmokeEmitter? SmokeEmitterFactory();

/// <summary>One running screen's own smoke, the effect object <c>FUN_004b8d50</c> builds at the lay
/// and <c>FUN_004b8f60</c> tears down at the end. The original attaches its instance to a scene
/// node, so it rides the layer wherever the layer goes; here the registry feeds it the layer's live
/// pose instead, which is the same follow through the seam <see cref="Puffer"/> already takes.</summary>
public interface ISmokeEmitter
{
    /// <summary>One step of the trail at the layer's world pose. <paramref name="dt"/> is 0 on the
    /// first call, which homes the trail at the launch point rather than drawing a line to it.</summary>
    void Emit(Vector3 worldPos, Basis worldBasis, float dt);

    /// <summary>Ends the run. Live puffs finish their own lifetimes, as the original's do: the
    /// screen stops making smoke, it does not delete the smoke already made.</summary>
    void Stop();
}

/// <summary>The smoke screen's per-victim rules with no aircraft in them, decoded from
/// <c>FUN_004b8fd0</c>: the catch test and the human wash's cadence. <see cref="SmokeScreens"/>
/// runs them over the live roster; the numbers here are the routine's own literals.</summary>
public static class SmokeScreenRule
{
    /// <summary>The wash's duration and the per-victim re-arm timer, both 2 s in the routine.</summary>
    public const float WashDurationS = 2f;

    /// <summary>The wash re-applies once the re-arm timer has run below this, so a victim who
    /// stays inside is washed again every 1.5 s, not every 2.</summary>
    public const float WashRearmBelowS = 0.5f;

    /// <summary>The wash weight while the re-arm timer is still running down.</summary>
    public const float WashWeight = 0.9f;

    /// <summary>The wash weight on a first hit, or one after the timer has fully expired.</summary>
    public const float FirstWashWeight = 0.97f;

    /// <summary>The wash the routine hands <c>FUN_0042e9d0</c>: a grey-green
    /// <c>(0.2, 0.29, 0.145)</c> for 2 s with no start delay.</summary>
    public static readonly Color WashColour = new(0.2f, 0.29f, 0.145f);

    /// <summary>Whether a victim at <paramref name="victimPos"/> is inside the screen: strictly
    /// closer than <paramref name="rangeM"/> to the layer, and the unit line from the layer to it
    /// making a dot STRICTLY above <paramref name="halfAngleCos"/> with the layer's backward axis.
    /// A victim standing exactly on the layer normalises to nothing and is not caught.</summary>
    public static bool Catches(Vector3 layerPos, Vector3 layerBackward, Vector3 victimPos,
        float rangeM, float halfAngleCos)
    {
        var to = victimPos - layerPos;
        float distance = to.Length();
        if (distance >= rangeM || distance <= 0f)
            return false;
        return (to / distance).Dot(layerBackward) > halfAngleCos;
    }

    /// <summary>One frame of the human wash's cadence over a victim's re-arm timer: the timer
    /// runs down by <paramref name="dt"/> first (never below zero); then, if the victim is inside
    /// and the timer sits below <see cref="WashRearmBelowS"/>, the wash fires at
    /// <see cref="FirstWashWeight"/> when the timer had reached zero and <see cref="WashWeight"/>
    /// otherwise, and the timer restarts at <see cref="WashDurationS"/>. Returns the weight to
    /// wash at, or null for no wash this frame.</summary>
    public static float? StepWash(ref float rearmS, float dt, bool inside)
    {
        if (rearmS > 0f)
            rearmS = Mathf.Max(0f, rearmS - dt);
        if (!inside || rearmS >= WashRearmBelowS)
            return null;
        float weight = rearmS <= 0f ? FirstWashWeight : WashWeight;
        rearmS = WashDurationS;
        return weight;
    }
}

/// <summary>The three game-wide smoke screen tunables from <c>player.json</c>'s global block
/// (docs/org/ordnanceTypes.md "SMOKE_SCREEN is a stun trap"). They are NOT per-weapon: the
/// original's <c>ai_skill_parameters</c> loader <c>FUN_004735b0</c> writes them into three
/// globals that <c>FUN_004b8fd0</c> reads for every screen. The angle is stored as the cosine of
/// HALF the authored angle, so the shipped 170° means 85° off the layer's backward axis.</summary>
public sealed class SmokeScreenTunables
{
    /// <summary>The loader's defaults for a key that is absent (<c>0x004735b0</c>: 200 m, a
    /// half-angle cosine of 0.8, which is about 73.7° across, and 3 s), the static image's own
    /// numbers rather than anything this port chose.</summary>
    public static readonly SmokeScreenTunables Image = new(200f, 0.8f, 3f);

    private SmokeScreenTunables(float rangeM, float halfAngleCos, float stunIntervalS)
    {
        RangeM = rangeM;
        HalfAngleCos = halfAngleCos;
        StunIntervalS = stunIntervalS;
    }

    /// <summary><c>smokescreen_stun_range</c>, metres, raw. 600 shipped.</summary>
    public float RangeM { get; }

    /// <summary><c>smokescreen_stun_angle</c> as the original stores it:
    /// <c>cos(angle × π/180 × 0.5)</c>. 170° shipped, so about 0.087.</summary>
    public float HalfAngleCos { get; }

    /// <summary><c>smokescreen_stun_interval</c>, seconds: the AI stun's duration, refreshed
    /// every frame the pilot stays inside. 5 shipped.</summary>
    public float StunIntervalS { get; }

    /// <summary>The stored cosine for an authored full angle in degrees, the loader's own
    /// arithmetic (<c>0x004735b0</c>).</summary>
    public static float HalfAngleCosOf(float fullAngleDeg) =>
        Mathf.Cos(fullAngleDeg * Mathf.Pi / 180f * 0.5f);

    /// <summary>Builds from the three authored values, for a suite or a caller with the numbers
    /// in hand.</summary>
    public static SmokeScreenTunables From(float rangeM, float fullAngleDeg, float stunIntervalS) =>
        new(rangeM, HalfAngleCosOf(fullAngleDeg), stunIntervalS);

    /// <summary>Reads the three keys off <c>player.json</c> in the shared zrdr scope, taking
    /// <see cref="Image"/>'s value for any key that is absent, as the original's loader does.
    /// Throws when the file itself cannot be read: a miss is a wrong path, not a default to paper
    /// over.</summary>
    public static SmokeScreenTunables Load(string zrdrPath)
    {
        if (Zrdr.LoadFile(zrdrPath, "player.json")[0] is not List<object?> playerList)
            throw new InvalidDataException("player.json: unexpected root shape");
        var player = ZrdrDict.FromAlternating(playerList);
        float range = player.TryFloat("smokescreen_stun_range", out float r) ? r : Image.RangeM;
        float cos = player.TryFloat("smokescreen_stun_angle", out float a) ? HalfAngleCosOf(a) : Image.HalfAngleCos;
        float interval = player.TryFloat("smokescreen_stun_interval", out float s) ? s : Image.StunIntervalS;
        return new SmokeScreenTunables(range, cos, interval);
    }
}

/// <summary>The world's active smoke screens (docs/org/ordnanceTypes.md "SMOKE_SCREEN is a stun
/// trap"): the launch path spawns no round for a <c>SMOKE_SCREEN</c> weapon, it calls
/// <see cref="Lay"/>, and every sim step while a screen's <c>TIME</c> runs it walks the roster
/// and hits every in-play aircraft other than the layer inside the cone about the layer's LIVE
/// backward axis. A human victim gets the grey-green wash on its own pane, an AI victim gets
/// <see cref="FlightController.TryStunPilot"/> for the stun interval, refreshed every step it
/// stays inside. It is not an occluder: nothing here reads or writes visibility, targeting or
/// collision.
/// ⚠ The wash cooldown is per victim per screen. The original keeps one slot per screen because
/// it has one player; per pane is the plan's Decision 2, so viewer 1 in the cloud does not
/// silence viewer 3's wash.</summary>
public sealed class SmokeScreens
{
    private readonly SmokeScreenTunables _tunables;
    private readonly Func<IReadOnlyList<FlightController>> _roster;
    private readonly SmokeWashSink? _wash;
    private readonly List<Screen> _screens = new();

    /// <summary>One registry with a roster it walks and a wash sink for its human victims. The
    /// roster delegate is read every step, so aircraft that spawn after a screen is laid are
    /// walked too. A null sink means humans inside see nothing, as in a headless run.</summary>
    public SmokeScreens(SmokeScreenTunables tunables, Func<IReadOnlyList<FlightController>> roster,
        SmokeWashSink? wash)
    {
        _tunables = tunables;
        _roster = roster;
        _wash = wash;
    }

    /// <summary>The tunables every screen runs on.</summary>
    public SmokeScreenTunables Tunables => _tunables;

    /// <summary>Where a screen gets its smoke. Settable rather than a constructor argument because
    /// the registry is built with the wash sink, before the chapter's textures and anim program
    /// exist; left null the screens run with no visual, which is what a headless run wants.</summary>
    public SmokeEmitterFactory? Emitters { get; set; }

    /// <summary>How many screens are running (their <c>TIME</c> not yet run out).</summary>
    public int ActiveCount => _screens.Count;

    /// <summary>Whether <paramref name="layer"/> has a screen running.</summary>
    public bool IsLaying(FlightController layer)
    {
        foreach (var s in _screens)
        {
            if (ReferenceEquals(s.Layer, layer))
                return true;
        }
        return false;
    }

    /// <summary>The launch-side entry: a <c>SMOKE_SCREEN</c> weapon leaving <paramref name="layer"/>
    /// lays a screen for <paramref name="timeSeconds"/> (the weapon's <c>SmokeScreenTime</c>,
    /// <c>TIME</c>). Nothing else is spawned. A non-positive time lays nothing. Two screens from
    /// one layer both run; each is its own object in the original's list too.</summary>
    public void Lay(FlightController layer, float timeSeconds)
    {
        if (timeSeconds <= 0f)
            return;
        var screen = new Screen(layer, timeSeconds) { Emitter = Emitters?.Invoke() };
        // Homed at the launch pose with a zero step, the way the pool homes a round's flyout trail:
        // a fresh emitter's first Emit sets the trail origin, and without this one the screen's
        // first real step would draw a puff line from wherever the emitter last ran.
        screen.Emitter?.Emit(layer.WorldPosition, layer.SimAttitude, 0f);
        _screens.Add(screen);
    }

    /// <summary>Drops every screen: the session teardown and a suite's reset.</summary>
    public void Clear()
    {
        foreach (var screen in _screens)
            screen.Emitter?.Stop();
        _screens.Clear();
    }

    /// <summary>One sim step for every screen: the timer runs down first, a screen whose layer is
    /// no longer in play ends on the spot, and a screen still running walks the roster and lays a
    /// step of its own smoke down the layer's track.</summary>
    public void SimStep(float dt)
    {
        if (_screens.Count == 0)
            return;
        var roster = _roster();
        for (int i = _screens.Count - 1; i >= 0; i--)
        {
            var screen = _screens[i];
            if (!screen.Layer.InPlay)
                screen.RemainingS = -1f;
            else
                screen.RemainingS -= dt;
            if (screen.RemainingS <= 0f)
            {
                // Both ends the original stops the emitter on, dead layer and run-out TIME, and one
                // teardown for the pair, as FUN_004b8fd0 reaches FUN_004b8f60 down either branch.
                screen.Emitter?.Stop();
                _screens.RemoveAt(i);
                continue;
            }
            screen.Emitter?.Emit(screen.Layer.WorldPosition, screen.Layer.SimAttitude, dt);
            Walk(screen, roster, dt);
        }
    }

    // The per-screen walk of FUN_004b8fd0: every in-play aircraft other than the layer, tested
    // against the layer's position and backward axis as they stand THIS step (the routine reads
    // both through the layer object each frame, never a pose captured at the lay). The human
    // branch keys on the victim being the player; ours keys on the seat, so a wash goes to
    // whichever pane flies the victim, and the stun's own guards refuse a human.
    private void Walk(Screen screen, IReadOnlyList<FlightController> roster, float dt)
    {
        var layer = screen.Layer;
        var layerPos = layer.WorldPosition;
        var backward = -layer.NoseDirection;
        for (int i = 0; i < roster.Count; i++)
        {
            var victim = roster[i];
            if (ReferenceEquals(victim, layer) || !victim.InPlay)
                continue;
            bool inside = SmokeScreenRule.Catches(layerPos, backward, victim.WorldPosition,
                _tunables.RangeM, _tunables.HalfAngleCos);
            if (victim.IsHumanPiloted)
            {
                float rearm = screen.RearmFor(victim.PlayerIndex);
                float? weight = SmokeScreenRule.StepWash(ref rearm, dt, inside);
                screen.SetRearm(victim.PlayerIndex, rearm);
                if (weight is { } w)
                    _wash?.Invoke(victim.PlayerIndex, SmokeScreenRule.WashColour, w, SmokeScreenRule.WashDurationS);
            }
            else if (inside)
            {
                victim.TryStunPilot(_tunables.StunIntervalS);
            }
        }
    }

    private sealed class Screen
    {
        public readonly FlightController Layer;
        public ISmokeEmitter? Emitter;
        public float RemainingS;
        private readonly Dictionary<int, float> _rearm = new();

        public Screen(FlightController layer, float remainingS)
        {
            Layer = layer;
            RemainingS = remainingS;
        }

        public float RearmFor(int playerIndex) => _rearm.TryGetValue(playerIndex, out float t) ? t : 0f;

        public void SetRearm(int playerIndex, float rearmS) => _rearm[playerIndex] = rearmS;
    }
}

/// <summary>The one fact a screen's emitter needs off its layer that nothing else on
/// <see cref="FlightController"/> publishes: the SIM attitude, whose backward axis the authored
/// trail blows its puffs down. Declared here rather than in the controller for the same reason
/// <c>IBeeperSubject</c> is: the reach is this module's, so it belongs beside the module.</summary>
public partial class FlightController
{
    internal Basis SimAttitude => _model.Attitude;
}

/// <summary>The screen's authored smoke over the real particle runtime: the <c>PUFFER_STATE</c>s of
/// the <c>generate_smokescreen</c> anim definition, which is the effect
/// <c>FUN_004b8d50</c> reaches through the pair of scene nodes <c>FUN_004b92c0</c> looks up at
/// mission load, and the same definition <c>wep_13</c>'s <c>FIRE</c> row names. Emitters are pooled
/// and reused once their puffs have decayed, as the projectile pool pools its flyout trails, since
/// each screen builds one per authored state.</summary>
public sealed class SmokeScreenEmitters
{
    /// <summary>The definition's own name, the string <c>FUN_004b92c0</c> resolves at load. It is
    /// NOT read off the weapon: the original's screen object holds a global, so every screen in the
    /// game lays the same smoke whatever fired it.</summary>
    public const string EffectAnimName = "generate_smokescreen";

    private readonly List<PufferState> _states = new();
    private readonly List<(PufferState State, Puffer Puffer)> _pool = new();
    private readonly HashSet<Puffer> _inUse = new();
    private readonly TextureArchive _textures;
    private readonly Node _parent;
    private readonly EffectAmbience? _ambience;
    private bool _logged;

    /// <summary>Reads the definition's trail states once out of <paramref name="defs"/>, which may
    /// be a whole program: the definitions not carrying <see cref="EffectAnimName"/> are skipped
    /// here rather than at the call site. A null list, no such definition or no textures for it
    /// leaves <see cref="StateCount"/> at zero and <see cref="Create"/> returning null, so the
    /// screens run unseen rather than throwing.</summary>
    public SmokeScreenEmitters(IEnumerable<AnimDefinition>? defs, TextureArchive textures, Node parent,
        EffectAmbience? ambience = null)
    {
        _textures = textures;
        _parent = parent;
        _ambience = ambience;
        if (defs == null)
            return;
        foreach (var def in defs)
        {
            if (!string.Equals(def.AnimName, EffectAnimName, StringComparison.OrdinalIgnoreCase))
                continue;
            foreach (var seq in def.Sequences)
            {
                foreach (var ev in seq.Events)
                {
                    if (ev.Kind != "PufferState" || (ev.Data.Num("active_state") ?? 0f) <= 0f)
                        continue;
                    var state = PufferState.FromAnimEvent(ev.Data);
                    if (state.DistanceInterval > 0f
                        && (state.Textures.Count > 0 || state.TextureSequence.Count > 0))
                        _states.Add(state);
                }
            }
        }
    }

    /// <summary>How many authored trail states a screen lays. Two in the retail data.</summary>
    public int StateCount => _states.Count;

    /// <summary>The authored states themselves, in definition order, for a suite that wants to
    /// drive one through the particle runtime on its own.</summary>
    public IReadOnlyList<PufferState> States => _states;

    /// <summary>One screen's emitter, or null when there is nothing authored to draw.</summary>
    public ISmokeEmitter? Create()
    {
        if (_states.Count == 0)
        {
            if (!_logged)
            {
                _logged = true;
                Log.Info("weapons", $"smoke screen '{EffectAnimName}': no DISTANCE_INTERVAL puffer in the anim program, screens lay no smoke");
            }
            return null;
        }
        var set = new List<Puffer>(_states.Count);
        foreach (var state in _states)
        {
            Puffer? puffer = null;
            foreach (var (pooledState, candidate) in _pool)
            {
                // Free only once the last puff of its previous screen has died: a live trail handed
                // to a new screen would draw a line from the old layer's track to the new one's.
                if (ReferenceEquals(pooledState, state) && !_inUse.Contains(candidate)
                    && candidate.LiveCount == 0)
                {
                    puffer = candidate;
                    break;
                }
            }
            if (puffer == null)
            {
                puffer = Puffer.Create(state, _textures, ambience: _ambience);
                if (puffer == null)
                    continue;
                _parent.AddChild(puffer);
                _pool.Add((state, puffer));
            }
            _inUse.Add(puffer);
            set.Add(puffer);
        }
        return set.Count > 0 ? new PooledEmitter(this, set) : null;
    }

    // One screen's set of authored trails, driven together off the layer's pose and released back
    // to the pool at the screen's end. Stop is idempotent because the registry stops a screen on
    // whichever end condition comes first and Clear may stop it again.
    private sealed class PooledEmitter : ISmokeEmitter
    {
        private readonly SmokeScreenEmitters _owner;
        private readonly List<Puffer> _puffers;
        private bool _stopped;

        public PooledEmitter(SmokeScreenEmitters owner, List<Puffer> puffers)
        {
            _owner = owner;
            _puffers = puffers;
        }

        public void Emit(Vector3 worldPos, Basis worldBasis, float dt)
        {
            if (_stopped)
                return;
            foreach (var puffer in _puffers)
                puffer.Emit(worldPos, worldBasis, dt);
        }

        public void Stop()
        {
            if (_stopped)
                return;
            _stopped = true;
            foreach (var puffer in _puffers)
            {
                puffer.Stop();
                _owner._inUse.Remove(puffer);
            }
        }
    }
}
