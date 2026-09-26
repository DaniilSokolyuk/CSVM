using CSVM.Flight.Weapons;

namespace CSVM.Flight.Airframe;

/// <summary>Which rule, if either, hands one ground strike to the crater sink.
/// <c>Faithful</c> is the original's own. A carve taken through it suppresses the weapon's impact
/// animation, as the original's AND does. <c>Optional</c> is the remake's own carve, which adds a
/// bowl and takes nothing away.</summary>
public enum CraterAsk
{
    /// <summary>Nothing is asked and the ground is left intact.</summary>
    None,

    /// <summary>The struck node carries <c>CAN_MODIFY</c> and the weapon carries <c>CRATER</c>.</summary>
    Faithful,

    /// <summary>The carve option is on and the weapon is a rocket warhead.</summary>
    Optional,
}

/// <summary>
/// Whether a round's ground strike asks for a crater. The original's rule is the struck node's
/// <c>CAN_MODIFY</c> flag over a <c>CRATER</c> weapon. No shipped node carries the flag, so a
/// played round never carves (docs/org/craters.md). The remake's own carve opens a second door,
/// off unless it is asked for, under which every rocket warhead's ground burst carves. The
/// decision is a pure function of the two rules, so a unit reaches it with no scene behind it.
/// The run's answer to the option is <see cref="Enabled"/>, which <c>ProjectilePool</c> passes in.
/// </summary>
public static class CraterGate
{
    /// <summary>Whether the carve is armed for this run. Off in a shipped default. Off under
    /// <c>--det</c> too, which reads no saved option, so no pinned golden can see a carve. ⚠ No
    /// screen writes this. The options file's <c>rocketCraters</c> key and <c>--craters</c> are
    /// its two sources, both read once at boot.</summary>
    public static bool Enabled { get; set; }

    /// <summary>Which rule, if either, this strike goes through: the weapon, whether the struck
    /// collider's node carries <c>CAN_MODIFY</c>, and whether the option is on. The faithful rule
    /// is tested first. A stamped node therefore keeps the original's suppressing carve whatever
    /// the option says.</summary>
    public static CraterAsk For(WeaponDef weapon, bool canModify, bool optionOn) =>
        canModify && weapon.Crater ? CraterAsk.Faithful
        : optionOn && weapon.IsRocket ? CraterAsk.Optional
        : CraterAsk.None;
}
