using System;
using System.Collections.Generic;

namespace CSVM.Session.Campaign;

/// <summary>One row of the original's memento award table: the picture's file name, the mission
/// that awards it (0 for one the campaign starts with), and which bit of that mission's merged
/// objective mask has to be set beside the mission-won bit. The decode is in
/// <c>docs/org/pause-screen.md</c>, "The memento is the profile's own image name".</summary>
public readonly record struct MementoAward(string Name, int Mission, int Objective);

/// <summary>
/// The pictures a campaign profile may hang in its cabin: the executable's own award table and the
/// rule that admits a row, so the cabin's chooser lists what the profile has earned. The table's
/// names are the file names the profile stores, with the extension the drawing side strips.
/// </summary>
public static class CampaignMementos
{
    /// <summary>The picture a profile starts with, the name the original's fresh-profile reset
    /// writes into the memento slot. A profile with no choice of its own draws this one.</summary>
    public const string Seeded = "MS_P_InitialPinup1.jpg";

    // The award table as the executable holds it: 23 rows of name, mission and objective bit. Its
    // own first row, MyMemento.jpg with mission -1, is left out: that row is admitted by a file
    // test on "Assets\Graphics\Scrapbook" concatenated with the name and no separator between them,
    // so it names no shipped file and never appears.
    private static readonly MementoAward[] Rows =
    {
        new("MS_P_InitialPinup1.jpg", 0, 0),
        new("MS_P_InitialPinup3.jpg", 0, 0),
        new("MS_P_JustineBattleax.jpg", 0, 0),
        new("MS_P_Mom.jpg", 0, 0),
        new("MS_P_DoggiePhoto.jpg", 0, 0),
        new("MS_P_Swan&NathaninCabin.jpg", 0, 0),
        new("MS_P_ZacharyandPlane.jpg", 0, 0),
        new("MS_P_02_01_hawaiianPrinces2.jpg", 2, 1),
        new("MS_P_05_01_TreasureGirl.jpg", 5, 1),
        new("MS_P_06_02_BSwanPortrait.jpg", 6, 2),
        new("MS_P_IllsaandSparks.jpg", 7, 0),
        new("MS_P_10_01_SoldierGirl.jpg", 10, 1),
        new("MS_P_11_01_LanaCooperGlam.jpg", 11, 1),
        new("MS_P_12_01_BettysScreentest2.jpg", 12, 1),
        new("MS_P_12_02_Charlie.jpg", 12, 2),
        new("MS_P_13_02_NathanGetsTrophy.jpg", 13, 2),
        new("MS_P_15_01_MyShoe.jpg", 15, 1),
        new("MS_P_16_01_Strip3.jpg", 16, 1),
        new("MS_P_17_04_Shangri-LaPinup.jpg", 17, 4),
        new("MS_P_19_01_SwanTheMorningAfter.jpg", 19, 1),
        new("MS_P_19_01_SwanWelcomeHome1.jpg", 19, 1),
        new("MS_P_24_01_SwanPinup.jpg", 24, 1),
        new("MS_P_24_01_theFinalKiss.jpg", 24, 1),
    };

    /// <summary>The award table in its own order, which is the order the chooser steps through.</summary>
    public static IReadOnlyList<MementoAward> Table => Rows;

    /// <summary>The names this profile may choose from, in table order: the seven the campaign
    /// starts with and every later row its mission results admit. Never empty, so the chooser
    /// always has the opening pin-up to stand on.</summary>
    public static IReadOnlyList<string> Awarded(CampaignProfileDef profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var names = new List<string>(Rows.Length);
        foreach (var award in Rows)
        {
            if (Holds(profile, award))
            {
                names.Add(award.Name);
            }
        }

        return names;
    }

    /// <summary>Whether <paramref name="profile"/> has earned <paramref name="award"/>: a mission 0
    /// row always, any other row once that mission's merged mask carries the won bit and the row's
    /// own objective bit. The mask is the campaign's, not one attempt's, so a later loss keeps it.</summary>
    public static bool Holds(CampaignProfileDef profile, MementoAward award)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (award.Mission <= 0)
        {
            return true;
        }

        if (CampaignProgression.ResultOf(profile, award.Mission - 1) is not { } result)
        {
            return false;
        }

        int mask = result.Best.CompletedMask;
        return (mask & 1) != 0 && (mask & (1 << award.Objective)) != 0;
    }

    /// <summary>The picture this profile hangs: its own choice, or the seeded pin-up where it has
    /// made none or holds a name the table does not carry.</summary>
    public static string Current(CampaignProfileDef? profile)
    {
        if (profile == null || profile.Memento.Length == 0)
        {
            return Seeded;
        }

        foreach (var award in Rows)
        {
            if (string.Equals(award.Name, profile.Memento, StringComparison.OrdinalIgnoreCase))
            {
                return award.Name;
            }
        }

        return Seeded;
    }

    /// <summary>The bitmap a screen hangs for a seated profile: the picture that profile chose, or
    /// the seeded pin-up where nobody is seated. The cabin wall, the pause sheet and the campaign
    /// load screen all draw this one name, so a chosen picture cannot reach one and miss another.</summary>
    public static string BitmapFor(CampaignProfileDef? profile) => Bitmap(Current(profile));

    /// <summary>The mission-art bitmap a memento file name draws as: the name lowercased with its
    /// extension cut, which is the original's own <c>FUN_0041a820</c> truncation at the last dot.</summary>
    public static string Bitmap(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        int dot = name.LastIndexOf('.');
        return (dot < 0 ? name : name.Substring(0, dot)).ToLowerInvariant();
    }
}
