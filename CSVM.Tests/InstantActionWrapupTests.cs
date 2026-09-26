using CSVM.Session.InstantAction;
using CSVM.UI.Screens;
using Xunit;

namespace CSVM.Tests;

/// <summary>
/// The Instant Action wrap-up board's two pure formulas (docs/formats/instant-action.md "What the
/// four numbers count"): the decoded
/// <c>IDS_IAWU_TIME</c>/<c>IDS_IAWU_PERCENTAGE</c> formats. Both are static and engine-free, so
/// they are pinned off-engine as <see cref="InstantActionEndTests"/> pins the rest of
/// <see cref="InstantActionRuntime"/>. The time format is a static on the board, called here
/// without building one. The board itself, a Godot <c>Control</c>, is exercised in-engine by the
/// <c>instant-action-wrapup</c> suite.
/// </summary>
public class InstantActionWrapupTests
{
    [Theory]
    [InlineData(0f, "00:00")]
    [InlineData(59.999f, "00:59")]     // truncating, not rounding, 59.999 s must NOT read 01:00
    [InlineData(60f, "01:00")]
    [InlineData(125.4f, "02:05")]
    [InlineData(3661f, "61:01")]       // no hour rollover, the decode is plain minutes:seconds
    public void FormatElapsedTruncatesToMinutesSeconds(float elapsedSeconds, string expected)
    {
        Assert.Equal(expected, IaWrapupBoard.FormatElapsed(elapsedSeconds));
    }

    [Theory]
    [InlineData(0, 10, 0)]     // no hits: 0%
    [InlineData(10, 10, 100)]  // every round hit: 100%
    [InlineData(1, 3, 33)]     // truncating, not rounding, 33.3% must NOT read 34
    [InlineData(0, 0, 0)]      // zero denominator: CSVM's own deliberate divergence (not the
                               // original's unguarded divide, which prints garbage), see G14
    public void ShotPercentTruncatesAndGuardsZeroDenominator(int hits, int fired, int expected)
    {
        Assert.Equal(expected, InstantActionRuntime.ShotPercent(hits, fired));
    }
}
