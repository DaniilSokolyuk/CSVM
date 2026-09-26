using CSVM.Flight.Camera;
using Xunit;

namespace CSVM.Tests;

/// <summary>The selected-view rules (A1): which mode the cycle key lands on, which modes are
/// first person, and what a held numpad key does to the selection. Headless because
/// <see cref="PilotView"/> owns no camera, <c>CameraController</c> holds the state and defers
/// every decision here.</summary>
public sealed class PilotViewTests
{
    [Fact]
    public void Only_the_two_first_person_modes_answer_the_condition()
    {
        Assert.False(PilotView.IsFirstPerson(PilotViewMode.Chase));
        Assert.True(PilotView.IsFirstPerson(PilotViewMode.Cockpit));
        Assert.True(PilotView.IsFirstPerson(PilotViewMode.Nose));
    }

    // The original's cycle key walks all three selectable views (confirmed at the controls of the
    // original): Cockpit, then Nose, then Chase, and around again.
    [Fact]
    public void The_cycle_key_walks_cockpit_nose_chase()
    {
        Assert.Equal(PilotViewMode.Cockpit, PilotView.Cycle(PilotViewMode.Chase));
        Assert.Equal(PilotViewMode.Nose, PilotView.Cycle(PilotViewMode.Cockpit));
        Assert.Equal(PilotViewMode.Chase, PilotView.Cycle(PilotViewMode.Nose));
    }

    // Three presses from anywhere return to the starting view, so the cycle has no dead end.
    [Fact]
    public void Three_presses_return_to_the_start()
    {
        foreach (var start in new[] { PilotViewMode.Chase, PilotViewMode.Cockpit, PilotViewMode.Nose })
        {
            Assert.Equal(start, PilotView.Cycle(PilotView.Cycle(PilotView.Cycle(start))));
        }
    }

    // Nothing held: the selection is what is in force, and it is never rewritten, so releasing a
    // key returns to the very same mode.
    [Fact]
    public void The_selection_stands_when_nothing_is_held()
    {
        foreach (var selected in new[] { PilotViewMode.Chase, PilotViewMode.Cockpit, PilotViewMode.Nose })
        {
            Assert.Equal(selected, PilotView.Effective(selected));
        }
    }

    // The look-behind overrides the camera only in external modes. In first person it is a head
    // look-back that never leaves the cockpit (the original's behaviour in both cockpit views,
    // confirmed at its controls), so the effective mode stays first-person.
    [Fact]
    public void The_look_behind_overrides_chase_but_stays_in_cockpit_in_first_person()
    {
        Assert.Equal(PilotViewMode.Chase, PilotView.Effective(PilotViewMode.Chase, backActive: true));
        Assert.Equal(PilotViewMode.Cockpit, PilotView.Effective(PilotViewMode.Cockpit, backActive: true));
        Assert.Equal(PilotViewMode.Nose, PilotView.Effective(PilotViewMode.Nose, backActive: true));
    }

    [Fact]
    public void The_view_flag_spells_the_three_modes_and_nothing_else()
    {
        Assert.Equal(PilotViewMode.Chase, PilotView.Parse("chase"));
        Assert.Equal(PilotViewMode.Cockpit, PilotView.Parse("Cockpit"));
        Assert.Equal(PilotViewMode.Nose, PilotView.Parse("NOSE"));
        Assert.Null(PilotView.Parse("back"));
        Assert.Null(PilotView.Parse("6"));
        Assert.Null(PilotView.Parse("cockpit1"));
    }

    // The log breadcrumb a scripted capture reads back is the same word the flag took.
    [Fact]
    public void Every_mode_names_itself_the_way_the_flag_spells_it()
    {
        foreach (var mode in new[] { PilotViewMode.Chase, PilotViewMode.Cockpit, PilotViewMode.Nose })
            Assert.Equal(mode, PilotView.Parse(PilotView.Name(mode)));
    }
}
