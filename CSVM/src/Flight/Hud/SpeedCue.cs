using System;
using CSVM.Effects;
using CSVM.Mech3;
using CSVM.Utils;
using Godot;

namespace CSVM.Flight.Hud;

/// <summary>
/// The chapter-authored speed cue: large, pale smoke sprites emitted from a point 60 m ahead of
/// the player's aircraft and left in world space for the aircraft to pass. The three
/// <c>cuepufferN</c> states come directly from <c>speed_cue.zrd</c>; this adapter owns only the
/// animation's altitude selection and lifecycle.
/// </summary>
public sealed class SpeedCue
{
    public const float NearGroundMeters = 50f;
    public const float Cue1CeilingMeters = 800f;
    public const float Cue2LowCeilingMeters = 900f;
    public const float Cue3CeilingMeters = 1200f;
    public const float Cue2HighCeilingMeters = 1500f;

    /// <summary>The viewport shape the authored <c>DEVIATION_DISTANCE</c> cube was sized for: the
    /// original renders 4:3, so a 4:3 pane spawns every wisp exactly where the data puts it.</summary>
    public const float AuthoredAspect = 4f / 3f;

    private const float SelectionIntervalSeconds = 0.1f;

    private readonly Puffer[] _puffers;
    private Selection _active = Selection.None;
    private float _selectionTimer;

    private SpeedCue(Puffer[] puffers) => _puffers = puffers;

    private enum Selection
    {
        None,
        Cue1,
        Cue2,
        Cue3,
        KeepCurrent,
    }

    /// <summary>Loads all three authored states and parents their world-space renderers beneath
    /// <paramref name="parent"/>. The optional decorator stamps a splitscreen visual layer after
    /// each puffer has built its renderer subtree.</summary>
    public static SpeedCue? Build(string chapterZrdrPath, TextureArchive textures, Node parent,
        EffectAmbience? ambience = null, Action<Node>? decorate = null)
    {
        var puffers = new Puffer[3];
        for (int i = 0; i < puffers.Length; i++)
        {
            if (Puffer.MakePuffer(chapterZrdrPath, textures, parent, "speed_cue.json",
                    $"cuepuffer{i + 1}", ambience: ambience) is not { } puffer)
            {
                for (int built = 0; built < i; built++)
                    puffers[built].QueueFree();
                return null;
            }
            decorate?.Invoke(puffer);
            puffers[i] = puffer;
        }
        Log.Info("flight", $"speed cue: {puffers.Length} authored smoke puffers ready (30/15/8 m intervals, player local offset 0,0,-60)");
        return new SpeedCue(puffers);
    }

    /// <summary>Test entry point over real <see cref="Puffer"/> instances with fake renderers.</summary>
    public static SpeedCue CreateWith(Puffer cue1, Puffer cue2, Puffer cue3) =>
        new(new[] { cue1, cue2, cue3 });

    /// <summary>How far a pane of ratio <paramref name="aspect"/> widens the cue's lateral spawn
    /// half-width: the aspect over the authored 4:3, so 16:9 spreads 1.333 times as wide and 4:3
    /// spawns unchanged. A remake decision, not decoded: a cube sized for 4:3 crowds the centre of a
    /// wider frame. ⚠ Never below 1 and never on another emitter; both re-scatter authored data.</summary>
    public static float LateralSpreadFor(float aspect) =>
        aspect > AuthoredAspect ? aspect / AuthoredAspect : 1f;

    /// <summary>Advances the authored altitude menu and feeds the selected distance puffer the
    /// aircraft's current world pose. Above 1500 m the script has no ELSE events, so it preserves
    /// whichever puffer was already active; starting there leaves the effect off.
    /// <paramref name="viewportAspect"/> is the pane the wisps are being spread across, see
    /// <see cref="LateralSpreadFor"/>; the default is the authored 4:3, which spreads nothing.</summary>
    public void Update(float dt, Vector3 playerPosition, Basis playerBasis,
        float cameraAltitude, float cameraAgl, float viewportAspect = AuthoredAspect)
    {
        _selectionTimer -= dt;
        if (_selectionTimer <= 0f)
        {
            _selectionTimer += SelectionIntervalSeconds;
            Selection selected = Select(cameraAltitude, cameraAgl);
            if (selected != Selection.KeepCurrent && selected != _active)
            {
                if (IsCue(_active))
                    PufferFor(_active).Stop();
                _active = selected;
            }
        }
        if (IsCue(_active))
        {
            var puffer = PufferFor(_active);
            puffer.LateralSpreadScale = LateralSpreadFor(viewportAspect);
            puffer.Emit(playerPosition, playerBasis, dt);
        }
    }

    /// <summary>Crash/respawn lifecycle: remove every live cue and forget the previous altitude
    /// band, so a teleported aircraft cannot draw a trail from its old position.</summary>
    public void Reset()
    {
        foreach (var puffer in _puffers)
            puffer.Clear();
        _active = Selection.None;
        _selectionTimer = 0f;
    }

    internal void Dispose()
    {
        foreach (var puffer in _puffers)
        {
            puffer.GetParent()?.RemoveChild(puffer);
            puffer.QueueFree();
        }
    }

    private static Selection Select(float altitude, float agl)
    {
        if (agl < NearGroundMeters)
            return Selection.None;
        if (altitude < Cue1CeilingMeters)
            return Selection.Cue1;
        if (altitude < Cue2LowCeilingMeters)
            return Selection.Cue2;
        if (altitude < Cue3CeilingMeters)
            return Selection.Cue3;
        if (altitude < Cue2HighCeilingMeters)
            return Selection.Cue2;
        return Selection.KeepCurrent;
    }

    private static bool IsCue(Selection selection) =>
        selection is >= Selection.Cue1 and <= Selection.Cue3;

    private Puffer PufferFor(Selection selection) =>
        _puffers[(int)selection - (int)Selection.Cue1];
}
