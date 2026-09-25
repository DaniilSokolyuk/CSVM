namespace CSVM.Bindings;

/// <summary>Every named action the game binds a control to. One member per binding that exists at
/// a polling site today, so a migration is a lookup swap and never a rename or a regrouping.
/// ⚠ The members are contiguous from zero and <see cref="ActionMap"/> indexes arrays by them, so
/// never give one an explicit value and never renumber. Adding a member at the end is safe.
/// The debug and lab keys (the F13-F18 overlays, the viewer and weapon-lab panels) are deliberately
/// absent: they are development instruments, not player controls, and a rebinding screen that
/// offered them would invite a player to break their own diagnostics.</summary>
public enum InputAction
{
    // Flight, the six attitude half-axes plus the throttle pair. Each half is its own action
    // because a player binds the two ends of a stick separately (`BindingControl.Axis`).
    PitchUp,
    PitchDown,
    RollLeft,
    RollRight,
    YawLeft,
    YawRight,
    ThrottleUp,
    ThrottleDown,

    // Weapons and the engine command.
    FireGuns,
    FireRockets,
    SelectGunGroup,
    SelectOrdnance,
    Nitro,

    // Session commands.
    Respawn,
    AutoLand,
    Pause,

    // Target selection.
    TargetNextEnemy,
    TargetNextAlly,
    TargetNextNonAircraft,
    TargetNearest,
    TargetClear,

    // Views and the head. The snap-look diagonals are one control on two actions, which is what a
    // binding list expresses and four typed slots cannot.
    CycleCockpitViews,
    SelectChaseView,
    LookUp,
    LookDown,
    LookLeft,
    LookRight,
    LookCenter,
    LookBack,
    LookAimUp,
    LookAimDown,
    LookAimLeft,
    LookAimRight,
    FreeLook,

    // Menu and board navigation.
    MenuUp,
    MenuDown,
    MenuLeft,
    MenuRight,
    MenuAccept,
    MenuBack,
    MenuStart,
    MenuLoadout,
    MenuPresets,
    MenuJoin,

    // The free-flying spectator and anim-lab camera.
    CameraForward,
    CameraBack,
    CameraLeft,
    CameraRight,
    CameraUp,
    CameraDown,
    CameraBoost,
    CameraSlow,
    CameraLockTarget,
    CameraLookUp,
    CameraLookDown,
    CameraLookLeft,
    CameraLookRight,

    // The locked orbit's dolly. Its own pair rather than a second reading of the boost and slow
    // triggers, whose digital threshold would leave the first half of trigger travel inert here.
    CameraDollyOut,
    CameraDollyIn,

    // The backward half of each weapon selector, which the original carries as its own bound action
    // per weapon class. Appended here rather than filed beside their forward twins above, because
    // the members are positional and inserting one would renumber every action after it.
    SelectGunGroupPrev,
    SelectOrdnancePrev,

    // The flyby camera, the original's "Access Chase View". Appended rather than filed with the
    // view actions above because the members are contiguous and indexed by value; the rebinding
    // screen reads its caption off the name, so it needs no table entry either.
    FlybyView,

    // The spyglass, the original's "Toggle Spyglass". Appended for the same reason the two above
    // are: the members are positional and inserting one beside the view actions would renumber
    // every action after it.
    ToggleSpyglass,

    // The other two directions of each target class, which complete the original's eleven targeting
    // actions: a Previous that steps the cycle back and a Nearest that restarts it at its head.
    // Appended for the same reason the members above are, the enum being positional.
    TargetPreviousEnemy,
    TargetNearestEnemy,
    TargetPreviousAlly,
    TargetNearestAlly,
    TargetPreviousNonAircraft,
    TargetNearestNonAircraft,

    // The nine absolute throttle settings, the original's Throttle page: 0/8 is idle, 8/8 is full.
    // Nine actions rather than one with a number, because a binding carries no argument.
    // ⚠ Keep the nine contiguous and in order; the caption and the lever both index off the first.
    ThrottleSet0,
    ThrottleSet1,
    ThrottleSet2,
    ThrottleSet3,
    ThrottleSet4,
    ThrottleSet5,
    ThrottleSet6,
    ThrottleSet7,
    ThrottleSet8,

    // The three look-mode selectors of the original's Views 1 page, which state which head-look
    // behaviour is live; they write the look mode and no device does. Appended for the reason
    // every member above the throttle run is: the enum is positional and indexed by value.
    SnapLookMode,
    SmoothLookMode,
    TrackTarget,

    // The absolute throttle, a lever or wheel whose whole travel is the commanded setting. This
    // port's own action: the original reads no axis at all. Appended because the enum is positional.
    ThrottleLever,
}
