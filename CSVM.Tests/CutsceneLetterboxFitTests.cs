using CSVM.Session.World;
using Godot;
using Xunit;

namespace CSVM.Tests;

/// <summary>
/// The letterbox fit: the card's authored height is the frame on every pane ratio, so the vertical
/// picture is the 4:3 one a wider window widens around, and the bars stretch sideways to reach the
/// edges of a pane wider than the card. The card is C3's shipped one, read off
/// <c>extracted/C3/gamez/models.json</c>'s first record: half-width 6.6461835, half-height
/// 4.0474105, 7.5 m in front of the eye.
/// </summary>
public class CutsceneLetterboxFitTests
{
    private const float HalfWidth = 6.6461835f;
    private const float HalfHeight = 4.0474105f;
    private const float Dist = 7.5f;

    // The card's own ratio, where the stretch starts: below it the authored card is already wider
    // than the pane its height frames.
    private const float CardAspect = HalfWidth / HalfHeight;

    // Every ratio the project can be flown at, the card's own ratio, 2558x1408, the window the
    // reported leak was captured in, and 3840x1080, the widest a borderless default reaches.
    // 1920x1080 is not listed separately: it IS 16:9 to the last bit, and a theory row that
    // repeats another's value is skipped outright by xUnit rather than run twice, which silently
    // drops a case.
    public static TheoryData<float> Aspects => new()
    {
        4f / 3f, 1.6f, 1.64211f, 16f / 9f, 2558f / 1408f, 21f / 9f, 3440f / 1440f, 32f / 9f,
    };

    [Theory]
    [MemberData(nameof(Aspects))]
    public void CardOverhangsThePaneAtEveryAspect(float aspect)
    {
        var (horizontal, vertical) = Margins(Fov(), aspect);

        Assert.True(horizontal > 0f, $"horizontal margin at {aspect}: {horizontal}");
        Assert.True(vertical > 0f, $"vertical margin at {aspect}: {vertical}");
    }

    // The able-to-fail half (METHOD-9/METHOD-10): with the overscan taken back out, the same
    // arithmetic reads zero margin on the height at every ratio, and zero on the stretched width
    // from the card's own ratio up, which is the boundary the margin exists to clear.
    [Theory]
    [MemberData(nameof(Aspects))]
    public void WithoutTheOverscanTheFitIsAnEquality(float aspect)
    {
        float unmargined = Mathf.RadToDeg(2f * Mathf.Atan(HalfHeight / Dist));
        var (horizontal, vertical) = Margins(unmargined, aspect);

        Assert.Equal(0f, vertical, 4);
        if (aspect >= CardAspect)
        {
            Assert.Equal(0f, horizontal, 4);
        }
        else
        {
            Assert.True(horizontal > 0f, $"below the card's own ratio it is wide enough already: {horizontal}");
        }
    }

    // The height takes exactly the declared overscan as a share of the card on every ratio, and so
    // does the stretched width from the card's own ratio up; a narrower pane keeps more.
    [Theory]
    [MemberData(nameof(Aspects))]
    public void TheMarginIsTheDeclaredOverscan(float aspect)
    {
        float expected = CutsceneController.CardOverscan / (1f + CutsceneController.CardOverscan);
        var (horizontal, vertical) = Margins(Fov(), aspect);

        Assert.Equal(expected, vertical, 4);
        if (aspect >= CardAspect)
        {
            Assert.Equal(expected, horizontal, 4);
        }
        else
        {
            Assert.True(horizontal > vertical, $"a pane narrower than the card keeps more: {horizontal}");
        }
    }

    // The picture's height is the card's on every window, so a wide pane adds world to the sides
    // instead of trading the vertical extent for it. Fitting the card's WIDTH on a 32:9 pane, which
    // is what a fit that takes the smaller term does, shows under half of that height instead.
    [Fact]
    public void TheVerticalPictureIsTheCardsOnEveryAspect()
    {
        float seenHalfHeight = Mathf.Tan(Mathf.DegToRad(Fov()) * 0.5f) * Dist;
        float widthFit = HalfWidth / (32f / 9f) / (1f + CutsceneController.CardOverscan);

        Assert.Equal(HalfHeight / (1f + CutsceneController.CardOverscan), seenHalfHeight, 4);
        Assert.True(widthFit / seenHalfHeight < 0.47f, $"a width fit shows {widthFit / seenHalfHeight} of it");
    }

    // The stretch covers the width the pane adds: none up to the card's own ratio, and the ratio's
    // own share of it above, which is what puts the bars' edge back outside the frame.
    [Theory]
    [MemberData(nameof(Aspects))]
    public void TheStretchStartsAtTheCardsOwnRatio(float aspect)
    {
        float stretch = CutsceneController.BarsWidthScale(Card(), aspect);

        if (aspect >= CardAspect)
        {
            Assert.Equal(aspect / CardAspect, stretch, 4);
        }
        else
        {
            Assert.Equal(1f, stretch, 4);
        }
    }

    [Fact]
    public void ADegenerateCardHasNoFitAndNoStretch()
    {
        var degenerate = new Aabb(Vector3.Zero, Vector3.Zero);

        Assert.Null(CutsceneController.FramingFovDeg(degenerate));
        Assert.Equal(1f, CutsceneController.BarsWidthScale(degenerate, 1.7778f));
        Assert.Equal(1f, CutsceneController.BarsWidthScale(Card(), 0f));
    }

    private static Aabb Card() => new(
        new Vector3(-HalfWidth, -HalfHeight, -Dist),
        new Vector3(HalfWidth * 2f, HalfHeight * 2f, 0f));

    private static float Fov()
    {
        float? fov = CutsceneController.FramingFovDeg(Card());
        Assert.NotNull(fov);
        return fov!.Value;
    }

    // What fraction of the card's own half-extent lies outside the frustum at the card's plane, the
    // width taken on the stretched card, which is what the bars cover.
    private static (float Horizontal, float Vertical) Margins(float fovDeg, float aspect)
    {
        float seenHalfHeight = Mathf.Tan(Mathf.DegToRad(fovDeg) * 0.5f) * Dist;
        float stretched = CutsceneController.BarsWidthScale(Card(), aspect) * HalfWidth;
        return (1f - (seenHalfHeight * aspect / stretched), 1f - (seenHalfHeight / HalfHeight));
    }
}
