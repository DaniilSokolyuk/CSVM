using System;
using CSVM.Flight.Airframe;
using CSVM.Flight.Modes;
using Xunit;

namespace CSVM.Tests;

/// <summary>
/// Splitscreen pause ownership (<see cref="PauseState"/>, E43) and the halt reasons around it,
/// off-engine: any player pauses, only the pauser resumes, a rejected unpause is a silent no-op
/// that fires no event, and a results board's halt neither answers to nor is cleared by a
/// player's pause key.
/// </summary>
public class PauseStateTests
{
    [Fact]
    public void AnyPlayerCanPauseFromRunning()
    {
        var state = new PauseState();

        bool changed = state.TryToggle(playerIndex: 1);

        Assert.True(changed);
        Assert.True(state.Paused);
        Assert.Equal(1, state.OwnerPlayerIndex);
    }

    [Fact]
    public void OnlyTheOwnerCanResume()
    {
        var state = new PauseState();
        state.TryToggle(playerIndex: 1);

        bool changed = state.TryToggle(playerIndex: 1);

        Assert.True(changed);
        Assert.False(state.Paused);
        Assert.Equal(-1, state.OwnerPlayerIndex);
    }

    [Fact]
    public void AnotherPlayersUnpauseAttemptIsRejected()
    {
        var state = new PauseState();
        state.TryToggle(playerIndex: 1);

        bool changed = state.TryToggle(playerIndex: 2);

        Assert.False(changed);
        Assert.True(state.Paused);
        Assert.Equal(1, state.OwnerPlayerIndex);
    }

    [Fact]
    public void ChangedFiresOnlyOnAcceptedTransitions()
    {
        var state = new PauseState();
        int fired = 0;
        state.Changed += () => fired++;

        state.TryToggle(playerIndex: 1);       // pause: fires
        state.TryToggle(playerIndex: 2);       // rejected unpause: does not fire
        state.TryToggle(playerIndex: 1);       // resume: fires

        Assert.Equal(2, fired);
    }

    [Fact]
    public void AResultsBoardHaltsTheClockWithoutPausing()
    {
        var state = new PauseState();

        state.Raise(HaltReason.Ended);

        Assert.True(state.Halted);
        Assert.True(state.Ended);
        Assert.False(state.Paused);
        Assert.Equal(-1, state.OwnerPlayerIndex);
    }

    [Fact]
    public void ThePauseKeyIsRefusedWhileAResultsBoardIsUp()
    {
        var state = new PauseState();
        state.Raise(HaltReason.Ended);

        bool changed = state.TryToggle(playerIndex: 0);

        Assert.False(changed);
        Assert.False(state.Paused);
    }

    [Fact]
    public void ARerunClearingTheEndedReasonReleasesTheClock()
    {
        var state = new PauseState();
        state.Raise(HaltReason.Ended);

        state.Clear(HaltReason.Ended);

        Assert.False(state.Halted);
    }

    [Fact]
    public void ForceResumeDropsAnotherPlayersPause()
    {
        var state = new PauseState();
        state.TryToggle(playerIndex: 2);

        bool changed = state.ForceResume();

        Assert.True(changed);
        Assert.False(state.Halted);
        Assert.Equal(-1, state.OwnerPlayerIndex);
    }

    [Fact]
    public void RedundantRaiseAndClearFireNoEvent()
    {
        var state = new PauseState();
        int fired = 0;
        state.Changed += () => fired++;

        state.Raise(HaltReason.Ended);      // fires
        state.Raise(HaltReason.Ended);      // already set: does not fire
        state.Clear(HaltReason.Ended);      // fires
        state.Clear(HaltReason.Ended);      // already clear: does not fire

        Assert.Equal(2, fired);
    }

    [Fact]
    public void APauseCannotBeRaisedWithoutAnOwner()
    {
        var state = new PauseState();

        Assert.Throws<ArgumentException>(() => state.Raise(HaltReason.Paused));
        Assert.Throws<ArgumentException>(() => state.Clear(HaltReason.Paused));
    }
}
