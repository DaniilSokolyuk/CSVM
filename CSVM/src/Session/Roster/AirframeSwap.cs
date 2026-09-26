using System;
using Godot;

namespace CSVM.Session.Roster;

/// <summary>One airframe a mission can put the player into mid-flight: the authored
/// <c>CALLBACK</c> value, the vehicle def the stats and the stock fit come from (<c>pbalmoral</c>),
/// and the planes.zbd node the model is built from (<c>player_balmoral</c>). That pair is the one
/// every other player-airframe path already uses (<c>PlaneStats.DefName</c> /
/// <c>UI.Hangar.PlanePickerRoster.AirframeNode</c>), so a swap needs no table of its own.
/// <c>AwardAirframe</c> names the special-plane template the code's own case writes by hand
/// (965 fits the Blue Streak: twin 40 and twin 30, two pylons, 20 armour a zone, the injector),
/// null for a code that hands over the stock fit. <c>ShippedSkins</c> says the rebuild draws the
/// airframe's shipped skin textures with no scheme composited over them, which is what the
/// original does for a def that authors no <c>paint_pattern</c> (965's <c>pbloodhawk</c>: the
/// vehicle build skips the scheme outright on the empty pattern string); false draws the pilot's
/// own paint.</summary>
public readonly record struct AirframeSwapCode(int Code, string Def, string PlaneNode,
    int? AwardAirframe = null, bool ShippedSkins = false);

/// <summary>One swap as the mission-script host raises it: the airframe the code names, the node
/// the raising definition is rooted on, which is the aircraft the capture animation belongs to, and
/// the rig the swap lands on. <c>CaptureRoot</c> is null for a definition rooted on nothing the
/// runtime could name. <c>Owner</c> is the episode owner, the human whose trigger started the
/// episode, and it is the whole of what makes a guest's capture put the GUEST in the aeroplane; it
/// is null only where the host has no rigs bound, which is a session with no aircraft to swap.
/// </summary>
public readonly record struct AirframeSwapOrder(AirframeSwapCode Airframe, string? CaptureRoot,
    Flight.Camera.PlayerRig? Owner = null);

/// <summary>What one raised swap did: whether an aircraft was replaced at all, and the aircraft the
/// swap took out of the world (967's capture, null for the other two codes and for a root that
/// resolved to nothing). ⚠ The caller must hand <c>Hidden</c> back to whatever else is holding that
/// aircraft out of play, or a cutscene's own reveal puts it back in front of the player.</summary>
public readonly record struct AirframeSwapResult(bool Swapped, Flight.Airframe.FlightController? Hidden);

/// <summary>What one mid-mission airframe swap replaces on a player's rig: the planes.zbd node to
/// build, and the flight state the replacement starts in, lifted off the aircraft being left.
/// ⚠ Four things a swap deliberately does NOT rebuild. A custom plane, whose bought armour and
/// pylon counts belong to the airframe the pilot bought rather than to the one a mission handed
/// them. The sortie's Ammo Selection picks, for the same reason: each case writes the handed-over
/// airframe's own weapon ids over every slot, slug guns and <c>wep_06</c> pylons. A stunt run,
/// which belongs to the pilot. And the spawn list, since the aircraft being left is where the
/// replacement begins. <c>Scheme</c> is null outside a resolved 967 capture, and null inside one
/// whose captured rig painted nothing; <c>ShippedSkins</c> carries that null, so the rebuild draws
/// the captured rig's own result rather than this pilot's default livery. <c>Build</c> is the
/// custom plane the replacement flies in place of the airframe's stock fit (965's Blue Streak);
/// null keeps the stock one.</summary>
internal sealed record AirframeSwapRequest(
    string PlaneNode, FlightStart Start, Mech3.PaintScheme? Scheme = null, bool ShippedSkins = false,
    Flight.Hangar.CustomPlaneDef? Build = null);

/// <summary>
/// The three <c>CALLBACK</c> codes that hand the player a different airframe in mid mission, and
/// what each names. Decode: docs/formats/anim-definitions/cutscenes.md's callback table, from the
/// mission-script host's own switch. They are the data-side counterpart of the intro definitions'
/// <c>check_balmoral</c>/<c>check_warhawk</c> branches, and the only mechanism in the shipped data
/// that changes what the player is flying without ending the mission.
///
/// <para>⚠ A swap is to the player's OWN airframe rig, never to one of the mission's aircraft. The
/// captured plane is a roster-spawned AI to the last frame; what the code does is rebuild the
/// player's aircraft from the named record, which is why the Balmoral being a heavy plane rather
/// than an airship is the whole of what the runtime has to know about it.</para>
/// </summary>
public static class AirframeSwapCodes
{
    /// <summary>The Blue Streak's airframe id in the special-plane template array.</summary>
    public const int BlueStreakAirframe = 3;

    private static readonly AirframeSwapCode[] All =
    {
        // ⚠ ShippedSkins, not a scheme: the Blue Streak's blue-grey body and yellow wingtips ARE
        // the unpainted blo_* skins, the state the original leaves them in.
        new(965, "pbloodhawk", "player_bhawk", BlueStreakAirframe, ShippedSkins: true),
        new(966, "pwarhawk", "player_warhawk"),
        new(967, "pbalmoral", "player_balmoral"),
    };

    /// <summary>Every swap code, in ascending code order.</summary>
    public static ReadOnlySpan<AirframeSwapCode> Table => All;

    /// <summary>The airframe an authored code names, or null for a code that names none.</summary>
    public static AirframeSwapCode? For(int code)
    {
        foreach (var entry in All)
        {
            if (entry.Code == code)
            {
                return entry;
            }
        }

        return null;
    }
}

/// <summary>
/// The things codes 966 and 967 do past rebuilding the player's rig: the capture animation's
/// own aircraft hands its damage and its roster group to the new hull (967 alone), and the
/// aeroplane the player just left goes to <c>wingman_4</c>.
/// ⚠ Neither behaviour is asked for by anything in the shipped data. Both are keyed on chapter
/// and mission strings inside the executable, so a search of the data for a trigger comes back
/// empty and that emptiness is NOT evidence they do not exist. Decode:
/// docs/formats/anim-definitions/cutscenes.md, "The airframe swap codes 965, 966 and 967".
/// </summary>
public static class AirframeHandover
{
    /// <summary>The roster block the outgoing aeroplane goes to. The original resolves this name
    /// in the live vehicle list once at mission start and holds nothing everywhere else.</summary>
    public const string WingmanName = "wingman_4";

    /// <summary>How far off the player the outgoing aeroplane is placed, metres.</summary>
    public const float RangeM = 100f;

    /// <summary>What the placement bearing takes off the player's own heading, degrees. Negative
    /// puts the aeroplane forward and to starboard; its nose still takes the player's heading, so
    /// the two are flying alongside rather than converging.</summary>
    public const float BearingDeg = -45f;

    // The capture case. 966 measures the outgoing sums and hands them over exactly as 967 does,
    // but scales nothing and hides nothing: it has no capture animation to read.
    private const int CaptureCode = 967;

    // The chapter/mission pairs the original resolves the name in, lower case as the exe holds
    // them. Nothing in the data says these two are special.
    private static readonly (string Chapter, string Mission)[] Missions =
    {
        ("c3", "m05"),
        ("c4", "m04"),
    };

    /// <summary>Does <see cref="WingmanName"/> resolve in this mission? Everywhere else the
    /// original holds no record of that name and the hand-over does nothing.</summary>
    public static bool Resolves(string? chapter, string? mission)
    {
        foreach (var (ch, ms) in Missions)
        {
            if (ch.Equals(chapter, StringComparison.OrdinalIgnoreCase)
                && ms.Equals(mission, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Whether this code carries the captured aircraft's damage into the new hull and
    /// hides that aircraft.</summary>
    public static bool CarriesCapturedDamage(AirframeSwapCode airframe) =>
        airframe.Code == CaptureCode;

    /// <summary>Whether this code also makes the player a member of the captured aircraft's
    /// roster group (the <c>+0x388</c> copy, 967 alone: 966 has no captured vehicle to read). The
    /// player then stands in for the aircraft they took, so a <c>DEDG</c> over that group counts
    /// one live member for as long as the player flies it.</summary>
    public static bool CarriesCapturedGroup(AirframeSwapCode airframe) =>
        airframe.Code == CaptureCode;

    /// <summary>Where the outgoing aeroplane is put, and the point its nose is put on: 100 m along
    /// a bearing <see cref="BearingDeg"/> off the player's heading, facing the player's heading.
    /// A vertical or zero nose falls back to the world's own forward axis rather than throwing.
    /// </summary>
    public static (Vector3 Position, Vector3 LookAt) Placement(Vector3 playerPos, Vector3 playerNose)
    {
        var flat = new Vector3(playerNose.X, 0f, playerNose.Z);
        flat = flat.LengthSquared() > 1e-6f ? flat.Normalized() : Vector3.Forward;
        var bearing = new Basis(Vector3.Up, Mathf.DegToRad(BearingDeg)) * flat;
        var position = playerPos + (bearing * RangeM);
        return (position, position + flat);
    }
}
