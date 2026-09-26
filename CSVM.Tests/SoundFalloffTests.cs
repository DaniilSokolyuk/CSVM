using CSVM.Mech3;
using Xunit;

namespace CSVM.Tests;

/// <summary>The decoded positional gain law, pinned as a table. The two emitters the curve was
/// judged on carry the widest and the narrowest shipped shapes of it: the police siren's RANGE is
/// [200, 1200] and the train's [600, 1200], so the same fractions of the band sit at very different
/// absolute distances.</summary>
[Trait("Tier", "Quick")]
public sealed class SoundFalloffTests
{
    private const float Slack = 0.01f;

    [Theory]
    // Fraction of the way from the full-volume radius to the audible one, and the decibels there.
    [InlineData(0.000f, 0f)]
    [InlineData(0.125f, 0f)]        // the shelf edge: still unattenuated
    [InlineData(0.250f, -10f)]      // one doubling past the shelf
    [InlineData(0.500f, -20f)]
    [InlineData(0.750f, -25.8496f)]
    [InlineData(1.000f, -30f)]      // three doublings: the audible radius
    public void The_ramp_is_ten_decibels_per_doubling_of_the_reach_past_the_shelf(
        float fraction, float expected)
    {
        foreach (var (min, max) in new[] { (200f, 1200f), (600f, 1200f), (20f, 150f) })
        {
            float distance = min + (fraction * (max - min));
            Assert.Equal(expected, SoundFalloff.AttenuationDb(distance, min, max), Slack);
        }
    }

    [Fact]
    public void Inside_the_full_volume_radius_nothing_is_lost()
    {
        Assert.Equal(0f, SoundFalloff.AttenuationDb(0f, 200f, 1200f), Slack);
        Assert.Equal(0f, SoundFalloff.AttenuationDb(199f, 200f, 1200f), Slack);
        Assert.Equal(0f, SoundFalloff.AttenuationDb(325f, 200f, 1200f), Slack);   // the shelf edge
        Assert.True(SoundFalloff.AttenuationDb(326f, 200f, 1200f) < 0f);
    }

    [Fact]
    public void The_band_past_the_audible_radius_is_a_tail_not_a_cut()
    {
        // Straight in decibels from the edge to the floor over the last tenth, then silence.
        Assert.Equal(-30f, SoundFalloff.AttenuationDb(1200f, 200f, 1200f), Slack);
        Assert.Equal(-65f, SoundFalloff.AttenuationDb(1260f, 200f, 1200f), Slack);
        Assert.Equal(-100f, SoundFalloff.AttenuationDb(1320f, 200f, 1200f), Slack);
        Assert.Equal(-100f, SoundFalloff.AttenuationDb(5000f, 200f, 1200f), Slack);
    }

    [Fact]
    public void The_police_siren_and_the_train_hold_their_level_at_the_judged_distances()
    {
        // The absolute-distance table the re-judge is flown against, both at VOLUME 1.
        Assert.Equal(0f, SoundFalloff.GainDb(325f, 200f, 1200f, 1f), Slack);
        Assert.Equal(-10f, SoundFalloff.GainDb(450f, 200f, 1200f, 1f), Slack);
        Assert.Equal(-20f, SoundFalloff.GainDb(700f, 200f, 1200f, 1f), Slack);
        Assert.Equal(-30f, SoundFalloff.GainDb(1200f, 200f, 1200f, 1f), Slack);

        Assert.Equal(0f, SoundFalloff.GainDb(675f, 600f, 1200f, 1f), Slack);
        Assert.Equal(-10f, SoundFalloff.GainDb(750f, 600f, 1200f, 1f), Slack);
        Assert.Equal(-20f, SoundFalloff.GainDb(900f, 600f, 1200f, 1f), Slack);
        Assert.Equal(-30f, SoundFalloff.GainDb(1200f, 600f, 1200f, 1f), Slack);
    }

    [Fact]
    public void Volume_converts_at_ten_decibels_per_doubling()
    {
        Assert.Equal(0f, SoundFalloff.VolumeDb(1f), Slack);
        Assert.Equal(0f, SoundFalloff.VolumeDb(2f), Slack);
        Assert.Equal(-10f, SoundFalloff.VolumeDb(0.5f), Slack);
        Assert.Equal(-3.2193f, SoundFalloff.VolumeDb(0.8f), Slack);   // the gasbag explosions
        Assert.Equal(-1.5200f, SoundFalloff.VolumeDb(0.9f), Slack);   // the Fury and Brigand loops
        Assert.Equal(-100f, SoundFalloff.VolumeDb(1f / 1024f), Slack);
        Assert.Equal(-100f, SoundFalloff.VolumeDb(0f), Slack);
    }

    [Fact]
    public void A_quiet_definition_reaches_the_floor_before_a_loud_one_does()
    {
        // The definition's own volume rides on the distance term and the sum is what is clamped.
        Assert.Equal(-33.2193f, SoundFalloff.GainDb(1200f, 200f, 1200f, 0.8f), Slack);
        Assert.Equal(-100f, SoundFalloff.GainDb(1319f, 200f, 1200f, 0.8f), Slack);
    }

    [Fact]
    public void A_range_scale_of_one_is_the_law_itself()
    {
        foreach (float distance in new[] { 0f, 325f, 450f, 700f, 1200f, 1260f, 1320f, 5000f })
        {
            Assert.Equal(SoundFalloff.GainDb(distance, 200f, 1200f, 0.8f),
                SoundFalloff.GainDb(distance, 200f, 1200f, 0.8f, 1f));
        }
    }

    [Fact]
    public void A_range_scale_multiplies_both_radii_and_the_cull_with_them()
    {
        // At twice the radii the siren's table is read at twice the distances.
        foreach (float distance in new[] { 0f, 325f, 450f, 700f, 1200f, 1260f, 1319f })
        {
            Assert.Equal(SoundFalloff.GainDb(distance, 200f, 1200f, 1f),
                SoundFalloff.GainDb(distance * 2f, 200f, 1200f, 1f, 2f), Slack);
        }
        Assert.Equal(0f, SoundFalloff.GainDb(650f, 200f, 1200f, 1f, 2f), Slack);      // the shelf edge
        Assert.Equal(-30f, SoundFalloff.GainDb(4800f, 200f, 1200f, 1f, 4f), Slack);   // the audible radius
        Assert.Equal(-100f, SoundFalloff.GainDb(2640f, 200f, 1200f, 1f, 2f), Slack);  // the cull
    }

    [Fact]
    public void Only_a_finite_positive_range_scale_is_taken()
    {
        try
        {
            SoundFalloff.SetRangeScale(3f);
            Assert.Equal(3f, SoundFalloff.RangeScale);
            foreach (float bad in new[] { 0f, -2f, float.NaN, float.PositiveInfinity })
            {
                SoundFalloff.SetRangeScale(bad);
                Assert.Equal(SoundFalloff.ShippedRangeScale, SoundFalloff.RangeScale);
            }
        }
        finally
        {
            SoundFalloff.SetRangeScale(SoundFalloff.ShippedRangeScale);
        }
    }

    [Fact]
    public void A_degenerate_range_is_silent_rather_than_infinite()
    {
        Assert.Equal(-100f, SoundFalloff.AttenuationDb(100f, 0f, 0f), Slack);
        Assert.Equal(0f, SoundFalloff.AttenuationDb(50f, 100f, 100f), Slack);
        Assert.Equal(-37f, SoundFalloff.AttenuationDb(101f, 100f, 100f), Slack);
        Assert.Equal(-100f, SoundFalloff.AttenuationDb(110f, 100f, 100f), Slack);
    }
}
