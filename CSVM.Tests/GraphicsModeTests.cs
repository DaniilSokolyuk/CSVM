using CSVM.Session.World;
using CSVM.Utils;
using Xunit;

namespace CSVM.Tests;

/// <summary>
/// The enhanced-lighting mode's two words and its flag-beats-saved-option-beats-config resolution
/// order. Mirrors <see cref="ClutterFadeTests"/>'s EffectsLevel coverage. The Config-backed
/// fallback branch of <see cref="GraphicsMode.Resolve"/> is not exercised here: <c>Config</c> is
/// process-wide static state shared with every other test class, so only the layers above it
/// (which never read it) are safe to assert without a test order dependency.
/// </summary>
public class GraphicsModeTests
{
    [Theory]
    [InlineData("original", false)]
    [InlineData("ORIGINAL", false)]
    [InlineData(" enhanced ", true)]
    [InlineData("Enhanced", true)]
    public void TheTwoWordsParseToTheirBoolean(string word, bool expected)
    {
        Assert.True(GraphicsMode.TryParse(word, out bool enhanced));
        Assert.Equal(expected, enhanced);
    }

    [Fact]
    public void AnUnknownWordFailsAndDefaultsFalse()
    {
        Assert.False(GraphicsMode.TryParse("ultra", out bool enhanced));
        Assert.False(enhanced);
    }

    [Fact]
    public void AnExplicitCliOverrideWinsAndNeedsNoConfig()
    {
        Assert.True(GraphicsMode.Resolve("enhanced"));
        Assert.True(GraphicsMode.Enhanced);

        Assert.False(GraphicsMode.Resolve("original"));
        Assert.False(GraphicsMode.Enhanced);
    }

    [Fact]
    public void TheSavedOptionDecidesWhenNoFlagIsGiven()
    {
        Assert.True(GraphicsMode.Resolve(null, "enhanced"));
        Assert.True(GraphicsMode.Enhanced);

        Assert.False(GraphicsMode.Resolve(null, "original"));
        Assert.False(GraphicsMode.Enhanced);
    }

    [Fact]
    public void TheFlagBeatsTheSavedOption()
    {
        Assert.True(GraphicsMode.Resolve("enhanced", "original"));
        Assert.True(GraphicsMode.Enhanced);

        Assert.False(GraphicsMode.Resolve("original", "enhanced"));
        Assert.False(GraphicsMode.Enhanced);
    }

    /// <summary>An empty saved option is "never set", not a request for anything, so it falls
    /// through to the config layer exactly as a null one does.</summary>
    [Fact]
    public void AnEmptySavedOptionFallsThroughRatherThanDeciding()
    {
        GraphicsMode.Resolve("enhanced");
        Assert.Equal(GraphicsMode.Resolve(null, null), GraphicsMode.Resolve(null, string.Empty));
    }

    [Fact]
    public void AnUnknownCliOverrideFallsBackToOriginal()
    {
        Assert.False(GraphicsMode.Resolve("ultra"));
        Assert.False(GraphicsMode.Enhanced);
    }

    [Fact]
    public void EnhancedFogScaleIsIdentityInOriginalAndThePushInEnhanced()
    {
        GraphicsMode.Resolve("original");
        Assert.Equal(1f, WeatherRig.EnhancedFogScale());

        GraphicsMode.Resolve("enhanced");
        Assert.Equal(2f, WeatherRig.EnhancedFogScale());

        GraphicsMode.Resolve("original");
    }
}
