using CSVM.Net;
using CSVM.Session;
using CSVM.Session.Campaign;
using Godot;
using Xunit;

namespace CSVM.Tests;

/// <summary>
/// The human field's rules, off-engine: what a condition that used to ask about one aeroplane
/// answers once two to four humans fly the mission. Decision 7 of the campaign co-op plan is the
/// contract: the conditions read the field and the first human to satisfy one settles it, while an
/// authored <c>player</c> token still means a single aircraft nothing here answers.
/// </summary>
[Trait("Tier", "Quick")]
public class CampaignHumanFieldTests
{
    [Fact]
    public void One_human_is_the_whole_field_and_answers_as_the_single_player_read_did()
    {
        var field = new[] { At(0f, 0f) };
        Assert.True(CampaignHumanField.Travelers(field, new Vector3(50f, 0f, 0f), 100f, true));
        Assert.False(CampaignHumanField.Travelers(field, new Vector3(150f, 0f, 0f), 100f, true));
        Assert.True(CampaignHumanField.Travelers(field, new Vector3(150f, 0f, 0f), 100f, false));
    }

    [Fact]
    public void An_approaching_condition_is_met_by_any_human_and_not_only_the_first()
    {
        var reference = new Vector3(1000f, 0f, 0f);
        // P1 is far away and every guest is nearer; the LAST of them is the one inside, so a read
        // that stopped at the first human, or at the scripted player, would report false here.
        var field = new[] { At(0f, 0f), At(500f, 0f), At(960f, 0f) };
        Assert.True(CampaignHumanField.Travelers(field, reference, 100f, true));

        var noneInside = new[] { At(0f, 0f), At(500f, 0f), At(870f, 0f) };
        Assert.False(CampaignHumanField.Travelers(noneInside, reference, 100f, true));
    }

    [Fact]
    public void A_departing_condition_waits_for_the_last_human_to_leave()
    {
        var reference = Vector3.Zero;
        // The nearest human decides, which on a departing condition is the last one out: one guest
        // still inside the radius keeps the whole field from having left.
        var oneLeftBehind = new[] { At(500f, 0f), At(50f, 0f), At(500f, 0f) };
        Assert.False(CampaignHumanField.Travelers(oneLeftBehind, reference, 100f, false));

        var allOut = new[] { At(500f, 0f), At(150f, 0f), At(500f, 0f) };
        Assert.True(CampaignHumanField.Travelers(allOut, reference, 100f, false));
    }

    [Fact]
    public void Proximity_is_measured_in_three_dimensions_not_on_the_ground_plane()
    {
        var field = new[] { new HumanState(new Vector3(0f, 800f, 0f), null, false) };
        Assert.False(CampaignHumanField.Travelers(field, Vector3.Zero, 100f, true));
        Assert.True(CampaignHumanField.Travelers(field, Vector3.Zero, 1000f, true));
    }

    [Fact]
    public void A_wreck_still_reports_its_position_the_way_the_single_player_read_did()
    {
        // Deliberate parity, not an oversight: the read this replaces is the pilot rig's own
        // position, which a crash does not stop reporting.
        var field = new[] { At(0f, 0f, crashed: true) };
        Assert.True(CampaignHumanField.Travelers(field, new Vector3(50f, 0f, 0f), 100f, true));
    }

    [Fact]
    public void Dedg_counts_every_live_human_the_capture_stamped_with_the_group()
    {
        var field = new[] { At(0f, 0f, group: 7), At(0f, 0f, group: 7), At(0f, 0f, group: 3) };
        Assert.Equal(2, CampaignHumanField.LiveInGroup(field, 7));
        Assert.Equal(1, CampaignHumanField.LiveInGroup(field, 3));
        Assert.Equal(0, CampaignHumanField.LiveInGroup(field, 9));
    }

    [Fact]
    public void A_human_with_no_group_and_a_crashed_one_are_not_counted()
    {
        var field = new[] { At(0f, 0f), At(0f, 0f, group: 7, crashed: true), At(0f, 0f, group: 7) };
        Assert.Equal(1, CampaignHumanField.LiveInGroup(field, 7));
    }

    [Fact]
    public void A_remote_human_arrives_where_its_buffer_shows_it_one_read_behind_its_owner()
    {
        // A guest flown elsewhere is in the host's field at the pose the host's buffer answers.
        // The buffer takes the owner's samples at the send cadence over a lag-free link. The owner
        // enters the radius first, and the host's read of it follows one buffer delay later.
        const float dt = 1f / 60f;
        const float speed = 100f;
        var reference = new Vector3(1000f, 0f, 0f);
        var host = At(-4000f, 0f);
        var buffer = new RemotePoseBuffer();
        float? ownerIn = null, remoteIn = null;
        ushort sequence = 0;
        for (int step = 1; step <= 1200 && remoteIn == null; step++)
        {
            float t = step * dt;
            var owner = new Vector3(speed * t, 0f, 0f);
            buffer.Advance(dt);
            if (step % AircraftStateCadence.SendStepInterval == 0)
            {
                buffer.Receive(new AircraftStateMessage(1, sequence++, owner, Quaternion.Identity,
                    new Vector3(speed, 0f, 0f), 1f, 0f, 0f, 0f, false));
            }

            if (ownerIn == null
                && CampaignHumanField.Travelers(new[] { host, new HumanState(owner, null, false) }, reference, 100f, true))
            {
                ownerIn = t;
            }

            if (buffer.TrySample(out var shown)
                && CampaignHumanField.Travelers(new[] { host, new HumanState(shown.Position, null, false) }, reference, 100f, true))
            {
                remoteIn = t;
            }
        }

        Assert.NotNull(ownerIn);
        Assert.NotNull(remoteIn);
        float lag = remoteIn!.Value - ownerIn!.Value;
        Assert.InRange(lag, RemotePoseBuffer.BufferDelaySeconds - dt,
            RemotePoseBuffer.BufferDelaySeconds + AircraftStateCadence.SampleSeconds + dt);
    }

    private static HumanState At(float x, float z, int? group = null, bool crashed = false) =>
        new(new Vector3(x, 0f, z), group, crashed);
}
