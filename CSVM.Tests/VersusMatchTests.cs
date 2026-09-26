using System.Collections.Generic;
using CSVM.Flight.Modes;
using Xunit;

namespace CSVM.Tests;

/// <summary>
/// Dogfight match bookkeeping (<see cref="VersusMatch"/>), off-engine: the signed score with its
/// suicide penalty, kill/death tallies, the two ways a match ends (threshold, time-out), the
/// tie-draw rule, a rematch's re-arm, and each end condition disabled on its own.
/// <see cref="VersusMatch"/> is a plain class with no engine
/// dependency at all (unlike its sibling <see cref="StuntRace"/>, it needs no fake-collaborator or
/// console-sink dance, there is nothing here that could reach <c>GD.*</c>).
/// </summary>
public class VersusMatchTests
{
    [Fact]
    public void APilotIsOutOfLivesOnceItsDeathsReachTheLimit()
    {
        var match = new VersusMatch(playerCount: 2, killTarget: 0, timeLimit: 0f, lives: 2);

        match.RegisterKill(shooter: 0, victim: 1);
        Assert.False(match.OutOfLives(1));
        match.RegisterDeath(1);
        Assert.True(match.OutOfLives(1));
        Assert.False(match.OutOfLives(0));

        // ABLE-TO-FAIL CONTROL: with no limit, no count of deaths spends a pilot.
        var unlimited = new VersusMatch(playerCount: 2, killTarget: 0, timeLimit: 0f);
        for (int i = 0; i < 10; i++)
        {
            unlimited.RegisterDeath(1);
        }

        Assert.False(unlimited.OutOfLives(1));
    }

    [Fact]
    public void AFreeForAllEndsOnReasonFourWhenOnePilotWithLivesIsLeft()
    {
        var match = new VersusMatch(playerCount: 3, killTarget: 0, timeLimit: 600f, lives: 1);

        // ABLE-TO-FAIL CONTROL: one pilot spent still leaves two with lives, and the match runs.
        match.RegisterKill(shooter: 0, victim: 1);
        Assert.True(match.OutOfLives(1));
        Assert.False(match.Completed);

        match.RegisterDeath(2);
        Assert.True(match.Completed);
        Assert.True(match.AllAlone);
    }

    [Fact]
    public void ADropThatLeavesOnePilotEndsOnReasonFourWhateverTheLives()
    {
        var match = new VersusMatch(playerCount: 3, killTarget: 0, timeLimit: 600f);
        match.Leave(2);
        Assert.False(match.Completed);
        match.Leave(1);
        Assert.True(match.AllAlone);

        // ABLE-TO-FAIL CONTROLS: a replicated match hears its ending from the host, and a match
        // that opened with one pilot has no opponent to lose.
        var guest = new VersusMatch(playerCount: 2, killTarget: 0, timeLimit: 600f);
        guest.Replicate();
        guest.Leave(1);
        Assert.False(guest.Completed);
        var solo = new VersusMatch(playerCount: 1, killTarget: 0, timeLimit: 600f, lives: 1);
        solo.RegisterDeath(0);
        Assert.False(solo.Completed);
    }

    [Fact]
    public void ASpentPilotWatchesTheNextFlyingSeatAndKeepsItWhileItFlies()
    {
        var flying = new[] { true, false, false, true };

        Assert.Equal(3, VersusMatch.NextWatched(1, flying, null));
        Assert.Equal(0, VersusMatch.NextWatched(1, flying, 0));
        Assert.Equal(0, VersusMatch.NextWatched(3, flying, null));

        // ABLE-TO-FAIL CONTROL: a watched seat that went down is left for the next one flying, and
        // with nobody flying there is nothing to watch.
        Assert.Equal(3, VersusMatch.NextWatched(1, flying, 2));
        Assert.Null(VersusMatch.NextWatched(0, new[] { true, false }, null));
    }

    [Fact]
    public void RegisterKillScoresTheShooterAndTalliesTheVictimsDeath()
    {
        var match = new VersusMatch(playerCount: 2, killTarget: 5, timeLimit: 300f);

        match.RegisterKill(shooter: 0, victim: 1);

        Assert.Equal(1, match.KillsOf(0));
        Assert.Equal(1, match.ScoreOf(0));
        Assert.Equal(0, match.DeathsOf(0));
        Assert.Equal(0, match.KillsOf(1));
        Assert.Equal(1, match.DeathsOf(1));
        Assert.Equal(0, match.ScoreOf(1)); // shot down by somebody else, no penalty
        Assert.False(match.Completed);
    }

    // The original's score_suicide: a death with no killer costs the pilot who died a point off
    // the same running score the kill target is compared against.
    [Fact]
    public void ADeathWithNoKillerCostsAPointAndPushesTheTargetFurtherAway()
    {
        var match = new VersusMatch(playerCount: 2, killTarget: 3, timeLimit: 300f);

        match.RegisterKill(shooter: 0, victim: 1);
        match.RegisterKill(shooter: 0, victim: 1);
        Assert.Equal(1, match.KillsRemaining(0));

        match.RegisterDeath(victim: 0);

        Assert.Equal(2, match.KillsOf(0));
        Assert.Equal(1, match.DeathsOf(0));
        Assert.Equal(1, match.ScoreOf(0));
        Assert.Equal(2, match.KillsRemaining(0)); // one kill further from the target than before
        Assert.False(match.Completed);

        match.RegisterKill(shooter: 0, victim: 1);
        Assert.False(match.Completed); // a third kill is only the second point
        match.RegisterKill(shooter: 0, victim: 1);
        Assert.True(match.Completed);
        Assert.Equal(4, match.KillsOf(0));
        Assert.Equal(3, match.ScoreOf(0));
    }

    [Fact]
    public void ScoreGoesNegativeAndRanksBelowAnUntouchedPilot()
    {
        var match = new VersusMatch(playerCount: 2, killTarget: 0, timeLimit: 10f);

        match.RegisterDeath(victim: 0);
        match.RegisterDeath(victim: 0);
        match.Advance(10f);

        Assert.Equal(-2, match.ScoreOf(0));
        var order = new List<VersusStanding>(match.Standings());
        Assert.Equal(1, order[0].PlayerIndex); // the pilot who never crashed leads on 0
        Assert.Equal(1, order[0].Rank);
        Assert.Equal(0, order[1].PlayerIndex);
        Assert.Equal(2, order[1].Rank);
    }

    [Fact]
    public void ReachingTheKillTargetCompletesTheMatchOnce()
    {
        var match = new VersusMatch(playerCount: 3, killTarget: 5, timeLimit: 300f);
        int completedCount = 0;
        match.MatchCompleted += () => completedCount++;

        for (int i = 0; i < 5; i++)
            match.RegisterKill(shooter: 0, victim: 1);

        Assert.True(match.Completed);
        Assert.Equal(5, match.KillsOf(0));
        Assert.Equal(1, completedCount);
    }

    [Fact]
    public void TimingOutCompletesTheMatchWithTheHighestKillerAsLeader()
    {
        var match = new VersusMatch(playerCount: 3, killTarget: 5, timeLimit: 10f);
        int completedCount = 0;
        match.MatchCompleted += () => completedCount++;

        match.RegisterKill(shooter: 0, victim: 2);
        match.RegisterKill(shooter: 0, victim: 2);
        match.RegisterKill(shooter: 0, victim: 2);
        match.RegisterKill(shooter: 1, victim: 2);

        match.Advance(6f);
        Assert.False(match.Completed);
        match.Advance(4f);

        Assert.True(match.Completed);
        Assert.Equal(1, completedCount);
        var order = new List<VersusStanding>(match.Standings());
        Assert.Equal(0, order[0].PlayerIndex);
        Assert.Equal(3, order[0].Kills);
        Assert.Equal(1, order[0].Rank);
        Assert.NotEqual(1, order[1].Rank); // the sole leader, not a shared rank 1
    }

    [Fact]
    public void EqualTopKillsAtTimeOutIsADraw()
    {
        var match = new VersusMatch(playerCount: 3, killTarget: 0, timeLimit: 10f);

        match.RegisterKill(shooter: 0, victim: 2);
        match.RegisterKill(shooter: 0, victim: 2);
        match.RegisterKill(shooter: 1, victim: 2);
        match.RegisterKill(shooter: 1, victim: 2);

        match.Advance(10f);

        Assert.True(match.Completed);
        var order = new List<VersusStanding>(match.Standings());
        var byIndex = new Dictionary<int, VersusStanding>();
        foreach (var row in order)
            byIndex[row.PlayerIndex] = row;

        Assert.Equal(1, byIndex[0].Rank);
        Assert.Equal(1, byIndex[1].Rank); // tied at the top, a draw, not a winner
        Assert.Equal(3, byIndex[2].Rank); // shares nothing, the tied pair pushed it down
    }

    [Fact]
    public void EventsAfterCompletionAreIgnoredNoScoreChangeNoSecondEvent()
    {
        var match = new VersusMatch(playerCount: 2, killTarget: 2, timeLimit: 300f);
        int completedCount = 0;
        match.MatchCompleted += () => completedCount++;

        match.RegisterKill(shooter: 0, victim: 1);
        match.RegisterKill(shooter: 0, victim: 1);
        Assert.True(match.Completed);
        Assert.Equal(1, completedCount);

        match.RegisterKill(shooter: 0, victim: 1);
        match.RegisterDeath(victim: 0);
        match.Advance(9999f);

        Assert.Equal(2, match.KillsOf(0));
        Assert.Equal(2, match.ScoreOf(0));
        Assert.Equal(2, match.DeathsOf(1));
        Assert.Equal(0, match.DeathsOf(0)); // the post-completion RegisterDeath changed nothing
        Assert.Equal(1, completedCount); // still just the one firing
    }

    [Fact]
    public void RestartClearsScoresAndClockAndReArmsCompletion()
    {
        var match = new VersusMatch(playerCount: 2, killTarget: 2, timeLimit: 300f);
        int completedCount = 0;
        match.MatchCompleted += () => completedCount++;

        match.RegisterKill(shooter: 0, victim: 1);
        match.RegisterKill(shooter: 0, victim: 1);
        Assert.True(match.Completed);

        match.Restart();

        Assert.False(match.Completed);
        Assert.Equal(0, match.KillsOf(0));
        Assert.Equal(0, match.ScoreOf(0));
        Assert.Equal(0, match.DeathsOf(1));
        Assert.Equal(0f, match.Elapsed);

        // Completion must re-arm, not just the fields: reaching the threshold again fires again.
        match.RegisterKill(shooter: 1, victim: 0);
        match.RegisterKill(shooter: 1, victim: 0);

        Assert.True(match.Completed);
        Assert.Equal(2, completedCount);
    }

    [Fact]
    public void KillTargetDisabledRunsOnTimeOnly()
    {
        var match = new VersusMatch(playerCount: 2, killTarget: 0, timeLimit: 5f);

        for (int i = 0; i < 50; i++)
            match.RegisterKill(shooter: 0, victim: 1);
        Assert.False(match.Completed); // no amount of kills ends it with the target disabled
        Assert.Equal(50, match.KillsOf(0));

        match.Advance(5f);
        Assert.True(match.Completed);
    }

    [Fact]
    public void TimeLimitDisabledRunsOnKillsOnly()
    {
        var match = new VersusMatch(playerCount: 2, killTarget: 3, timeLimit: 0f);

        match.Advance(1_000_000f);
        Assert.False(match.Completed); // the clock never moves, an untimed match never times out
        Assert.Equal(0f, match.Elapsed);

        match.RegisterKill(shooter: 0, victim: 1);
        match.RegisterKill(shooter: 0, victim: 1);
        match.RegisterKill(shooter: 0, victim: 1);
        Assert.True(match.Completed);
    }

    [Fact]
    public void RegisterDeathTalliesDeathsButNeverCompletesAKillTargetMatch()
    {
        var match = new VersusMatch(playerCount: 2, killTarget: 3, timeLimit: 300f);

        for (int i = 0; i < 10; i++)
            match.RegisterDeath(victim: 0);

        Assert.Equal(10, match.DeathsOf(0));
        Assert.Equal(0, match.KillsOf(0));
        Assert.Equal(-10, match.ScoreOf(0)); // the penalty applies every time, nothing floors it
        Assert.Equal(0, match.KillsOf(1));
        Assert.False(match.Completed); // a falling score can never reach the target
    }

    // The guest side of a network match. Everything that could end a round locally has to stop
    // deciding, or two machines show the wrap-up board on different frames.
    [Fact]
    public void AReplicatedMatchNeitherAdvancesItsClockNorArmsItsOwnLimits()
    {
        var match = new VersusMatch(playerCount: 2, killTarget: 2, timeLimit: 10f);
        int completedCount = 0;
        match.MatchCompleted += () => completedCount++;
        match.Replicate();

        match.Advance(100f);
        Assert.Equal(0f, match.Elapsed);

        match.RegisterKill(shooter: 0, victim: 1);
        match.RegisterKill(shooter: 0, victim: 1);
        match.ApplyScore(playerIndex: 1, score: 9, kills: 9, deaths: 0);

        Assert.Equal(2, match.ScoreOf(0)); // the target reached, and the round runs on
        Assert.Equal(9, match.ScoreOf(1));
        Assert.False(match.Completed);
        Assert.Equal(0, completedCount);
    }

    [Fact]
    public void ApplyStateTakesTheHostsLimitsClockAndEnding()
    {
        var match = new VersusMatch(playerCount: 2, killTarget: 5, timeLimit: 300f);
        int completedCount = 0;
        match.MatchCompleted += () => completedCount++;
        match.Replicate();

        match.ApplyState(killTarget: 3, timeLimit: 120f, remainingSeconds: 90f, ended: false);

        Assert.Equal(3, match.KillTarget); // the host's lobby row, not the one launched with
        Assert.Equal(120f, match.TimeLimit);
        Assert.Equal(30f, match.Elapsed);
        Assert.Equal(90f, match.TimeRemaining);
        Assert.False(match.Completed);

        match.ApplyState(killTarget: 3, timeLimit: 120f, remainingSeconds: 0f, ended: true);

        Assert.True(match.Completed);
        Assert.Equal(1, completedCount);
    }

    // The host's rematch reaches a guest as a running state, and the zeroed scores follow it.
    // Clearing completion has to come first or every one of those scores is dropped.
    [Fact]
    public void ARunningStateOnAnEndedGuestReArmsItWithoutTouchingTheScores()
    {
        var match = new VersusMatch(playerCount: 2, killTarget: 2, timeLimit: 0f);
        match.Replicate();
        match.ApplyScore(playerIndex: 0, score: 2, kills: 2, deaths: 0);
        match.ApplyState(killTarget: 2, timeLimit: 0f, remainingSeconds: 0f, ended: true);
        Assert.True(match.Completed);

        match.ApplyState(killTarget: 2, timeLimit: 0f, remainingSeconds: 0f, ended: false);
        Assert.False(match.Completed);
        Assert.Equal(2, match.ScoreOf(0)); // still the host's to rewrite

        match.ApplyScore(playerIndex: 0, score: 0, kills: 0, deaths: 0);
        Assert.Equal(0, match.ScoreOf(0));
        Assert.False(match.Completed);
    }

    [Fact]
    public void AHostIgnoresMatchStateOutright()
    {
        var match = new VersusMatch(playerCount: 2, killTarget: 5, timeLimit: 300f);

        match.ApplyState(killTarget: 1, timeLimit: 30f, remainingSeconds: 0f, ended: true);

        Assert.False(match.Replicated);
        Assert.Equal(5, match.KillTarget);
        Assert.Equal(300f, match.TimeLimit);
        Assert.False(match.Completed);
    }
}
