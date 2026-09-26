using System;
using CSVM.Flight.Weapons;
using CSVM.Utils;

namespace CSVM.Flight.Hangar;

/// <summary>The difficulty setting, as the engine's own 0/1/2, and the two things one integer
/// <c>k</c> does at spawn: scale a hostile vehicle's armour and health maxima
/// (docs/org/vehicleDamage.md "The difficulty scale"), and shift every one of that pilot's nine
/// skill ratings before they interpolate (docs/org/aiControlLaw.md "The skill scalar"). Both live
/// in <c>FUN_0047c210</c> behind one gate. The setting is readable in the executable only through
/// <c>FUN_00440710</c>, whose four callers are that spawn, the options screen, the settings save
/// pass and Instant Action's save/set/restore around the same spawn.</summary>
public static class Difficulty
{
    /// <summary>Campaign "Normal", Instant Action "novice". The shipped default: the settings
    /// registration at <c>0x0043ff36</c> stores zero into the setting after creating it.</summary>
    public const int Normal = 0;

    /// <summary>Campaign "Hard", Instant Action "veteran". The unscaled tier.</summary>
    public const int Hard = 1;

    /// <summary>Campaign "Hardest", Instant Action "ace".</summary>
    public const int Hardest = 2;

    // The scale's own two constants, read at 0x0060802c and 0x006032dc: factor = 1 + k * 0.125.
    private const float Step = 0.125f;

    /// <summary>What an enemy vehicle's armour and health maxima are multiplied by at spawn:
    /// 0.75 / 1.0 / 1.25. ⚠ The low tier is 0.75, not 0.875, the branch at <c>0x0047cb3b</c> loads
    /// <c>k = -2</c>, and the spread is a symmetric two <see cref="Step"/>s either side of 1.0. It
    /// reaches only the maxima, and only a spawn <see cref="AppliesTo"/> admits; the per-spawn
    /// jitter runs after it, on the scaled pools. ⚠ An ace is NOT exempt from this one.</summary>
    public static float EnemyDurabilityFactor(int difficulty) => 1f + K(difficulty) * Step;

    /// <summary>Whether a spawn takes the difficulty at all. The engine's gate is hostility:
    /// <c>0x0047ca79</c>-<c>0x0047ca8b</c> spares a side equal to the player's (the constant 1
    /// <c>FUN_004830c0</c> writes) AND a side of 0, so ⚠ a neutral is neither scaled nor shifted.
    /// A spawn carrying no team is hostile here, since an AI rig's fallback team is its shooter
    /// id's band and never <see cref="AimAssist.PlayerTeam"/>.</summary>
    public static bool AppliesTo(int? team) =>
        team != AimAssist.PlayerTeam && team != AimAssist.NeutralTeam;

    /// <summary>The factor one spawn actually takes. A per-spawn setting outranks the session's,
    /// which is the whole of what an Instant Action wave's skill does.</summary>
    public static float FactorForSpawn(int? team, int? spawnDifficulty, int sessionDifficulty) =>
        AppliesTo(team) ? EnemyDurabilityFactor(spawnDifficulty ?? sessionDifficulty) : 1f;

    /// <summary>One resolved skill rating as the interpolation receives it: <c>k</c> added, then
    /// clamped to 0-9 (<c>0x0047ce15</c>-<c>0x0047ce33</c>, the same clamp on all nine).
    /// ⚠ An ace (roster slot 67) takes <c>k = 0</c>: the read at <c>0x0047cde2</c> zeroes the
    /// offset just ahead of the block, so a flagged pilot flies its authored ratings at every
    /// tier. The armour scale above is NOT exempted; it runs before that read.</summary>
    public static int SkillRatingForSpawn(
        int rating, int? team, bool ace, int? spawnDifficulty, int sessionDifficulty) =>
        Math.Clamp(
            rating + (ace || !AppliesTo(team) ? 0 : K(spawnDifficulty ?? sessionDifficulty)), 0, 9);

    /// <summary>The setting a name selects, taking both vocabularies: the campaign selector's
    /// <c>IDS_DIFFICULTY</c> (normal / hard / hardest) and Instant Action's <c>IDS_IA_DIFFICULTY</c>
    /// (novice / veteran / ace), which name the same three tiers. A bare 0, 1 or 2 is taken as
    /// itself. Null for anything else, so a caller can reject rather than silently pick a tier.</summary>
    public static int? Parse(string? name) => name?.Trim().ToLowerInvariant() switch
    {
        DifficultyWords.Normal or "novice" or "0" => Normal,
        DifficultyWords.Hard or "veteran" or "1" => Hard,
        DifficultyWords.Hardest or "ace" or "2" => Hardest,
        _ => null,
    };

    /// <summary>The campaign selector's own label for a tier, for a log line or a menu row.</summary>
    public static string Label(int difficulty) => Clamp(difficulty) switch
    {
        Normal => "Normal",
        Hard => "Hard",
        _ => "Hardest",
    };

    /// <summary>The tier's word as the options file and the <c>--difficulty=</c> flag spell it
    /// (<see cref="DifficultyWords"/>). A saved value reads back through <see cref="Parse"/>, and
    /// a player can copy it from the file onto a command line.</summary>
    public static string Word(int difficulty) => Clamp(difficulty) switch
    {
        Normal => DifficultyWords.Normal,
        Hard => DifficultyWords.Hard,
        _ => DifficultyWords.Hardest,
    };

    /// <summary>Any integer brought onto the three tiers, the way the engine's own setter is fed
    /// (<c>0 -> 0, 2 -> 2, anything else -> 1</c>).</summary>
    public static int Clamp(int difficulty) =>
        difficulty == Normal || difficulty == Hardest ? difficulty : Hard;

    // The one integer both effects are built from, off the branch at 0x0047cb32-0x0047cb4e: -2 on
    // the low tier, +2 on the high one, and zero in the middle, where the armour block is skipped.
    private static int K(int difficulty) => Clamp(difficulty) switch
    {
        Normal => -2,
        Hardest => 2,
        _ => 0,
    };
}
