using System;
using System.Collections.Generic;

namespace CSVM.Flight;

/// <summary>One player's ranked line in <see cref="VersusMatch.Standings"/>: score, kills, deaths,
/// and a competition rank (tied scores share a rank; the next distinct score skips ahead by the
/// number of players it passed), enough for a results board to pick out the winner (rank 1, no
/// tie) or draw (rank 1, tied) and render score + kills + deaths per row.</summary>
public readonly record struct VersusStanding(int PlayerIndex, int Kills, int Deaths, int Rank, int Score);

/// <summary>
/// Deathmatch scorekeeping for splitscreen "Dogfight": per-player kills and deaths plus the match
/// clock, host-fed exactly like <see cref="StuntRace"/>. A caller reports facts
/// (<see cref="RegisterKill"/>, <see cref="RegisterDeath"/>, <see cref="Advance"/>) and this class
/// turns them into standings and one completion event. The match completes once, on whichever
/// comes first: a player's score reaching <see cref="KillTarget"/>, or the clock reaching
/// <see cref="TimeLimit"/>; with both disabled (0) it never completes on its own. Deliberately
/// not a Node, freed with the session, host-fed exactly like <see cref="StuntRace"/>'s timekeeping.
/// ⚠ Zero engine dependency of any kind, not even <c>Log</c>, a <c>GD.Print</c> in this family
/// once crashed the xUnit host. Keep it engine-free by construction.</summary>
public sealed class VersusMatch
{
    /// <summary>What a kill is worth, the original's <c>score_kill</c> default
    /// (<c>docs/org/multiplayer-scoring.md</c>).</summary>
    public const int KillScore = 1;

    /// <summary>What a death with no killer costs the pilot who died, the original's
    /// <c>score_suicide</c> default. Negative on purpose: a crash moves you away from the target
    /// (<c>docs/org/multiplayer-scoring.md</c>).</summary>
    public const int SuicideScore = -1;

    private readonly Row[] _scores;

    public VersusMatch(int playerCount, int killTarget = 5, float timeLimit = 300f)
    {
        playerCount = Math.Max(1, playerCount); // 2-4 in practice; a solo session still scores
        KillTarget = Math.Max(0, killTarget);
        TimeLimit = Math.Max(0f, timeLimit);
        _scores = new Row[playerCount];
        for (int i = 0; i < playerCount; i++)
            _scores[i] = new Row();
    }

    /// <summary>Fired exactly once, the moment the match completes, the results board's cue.</summary>
    public event Action? MatchCompleted;

    /// <summary>How many players are being scored.</summary>
    public int PlayerCount => _scores.Length;

    /// <summary>The score that ends the match, 0 = no kill target (time is then the only way to
    /// end it). Named for the menu row that sets it; the original compares the same row against a
    /// running score, so suicides push a pilot back from it.</summary>
    public int KillTarget { get; }

    /// <summary>Seconds on the match clock, 0 = no time limit (kills are then the only way to end it).</summary>
    public float TimeLimit { get; }

    /// <summary>Seconds of match clock consumed so far via <see cref="Advance"/>. Stops moving once
    /// <see cref="Completed"/>, a completed match's clock is frozen for display.</summary>
    public float Elapsed { get; private set; }

    /// <summary>Seconds left before a time-limited match times out, 0 once reached or when
    /// <see cref="TimeLimit"/> is disabled.</summary>
    public float TimeRemaining => TimeLimit > 0f ? Math.Max(0f, TimeLimit - Elapsed) : 0f;

    /// <summary>True once the match has ended (threshold or time-out), every further
    /// <see cref="RegisterKill"/>/<see cref="RegisterDeath"/>/<see cref="Advance"/> call is a no-op.</summary>
    public bool Completed { get; private set; }

    public int KillsOf(int playerIndex) => RowOf(playerIndex)?.Kills ?? 0;

    public int DeathsOf(int playerIndex) => RowOf(playerIndex)?.Deaths ?? 0;

    /// <summary>The ranked number: <see cref="KillScore"/> per kill plus <see cref="SuicideScore"/>
    /// per death with no killer. May go negative.</summary>
    public int ScoreOf(int playerIndex) => RowOf(playerIndex)?.Score ?? 0;

    /// <summary>Score still needed by <paramref name="playerIndex"/> to hit the threshold, 0 once
    /// there or when <see cref="KillTarget"/> is disabled.</summary>
    public int KillsRemaining(int playerIndex) =>
        KillTarget > 0 ? Math.Max(0, KillTarget - ScoreOf(playerIndex)) : 0;

    /// <summary>A weapon kill: +1 kill and <see cref="KillScore"/> to the shooter, +1 death to the
    /// victim. Completes the match once the shooter's score reaches <see cref="KillTarget"/>.
    /// No-op once <see cref="Completed"/>.</summary>
    public void RegisterKill(int shooter, int victim)
    {
        if (Completed)
            return;
        var shooterRow = RowOf(shooter);
        if (shooterRow != null)
        {
            shooterRow.Kills++;
            shooterRow.Score += KillScore;
        }
        var victimRow = RowOf(victim);
        if (victimRow != null)
            victimRow.Deaths++;
        if (KillTarget > 0 && shooterRow != null && shooterRow.Score >= KillTarget)
            Complete();
    }

    /// <summary>A death with no killer, terrain or mid-air: +1 death and <see cref="SuicideScore"/>
    /// to the pilot who died, the original's own penalty. Never completes the match by itself, a
    /// falling score cannot reach the target. No-op once <see cref="Completed"/>.</summary>
    public void RegisterDeath(int victim)
    {
        if (Completed)
            return;
        var victimRow = RowOf(victim);
        if (victimRow != null)
        {
            victimRow.Deaths++;
            victimRow.Score += SuicideScore;
        }
    }

    /// <summary>Writes one player's line as the host reports it, in place of deriving it from
    /// events. A guest scores nothing of its own. It shows what the host counted, so a board reads
    /// the same on every machine whatever each of them saw. The completion test runs as it does
    /// after a kill, and a host that has ended the match sends nothing more.</summary>
    public void ApplyScore(int playerIndex, int score, int kills, int deaths)
    {
        if (Completed)
            return;
        var row = RowOf(playerIndex);
        if (row == null)
            return;
        row.Score = score;
        row.Kills = kills;
        row.Deaths = deaths;
        if (KillTarget > 0 && row.Score >= KillTarget)
            Complete();
    }

    /// <summary>Advance the host-fed match clock by <paramref name="dt"/> seconds; completes the
    /// match once it reaches <see cref="TimeLimit"/>. No-op once <see cref="Completed"/> or when
    /// <see cref="TimeLimit"/> is disabled (an untimed match never times out).</summary>
    public void Advance(float dt)
    {
        if (Completed || TimeLimit <= 0f)
            return;
        Elapsed += dt;
        if (Elapsed >= TimeLimit)
            Complete();
    }

    /// <summary>Rematch: every score/kill/death zeroed, the clock back to zero, completion
    /// re-armed.</summary>
    public void Restart()
    {
        foreach (var s in _scores)
        {
            s.Kills = 0;
            s.Deaths = 0;
            s.Score = 0;
        }
        Elapsed = 0f;
        Completed = false;
    }

    /// <summary>Every player ranked by score descending, ties sharing a rank, rank 1 alone is the
    /// winner, rank 1 shared is a draw. Kills and deaths ride along for the results board / HUD to
    /// render.</summary>
    public IEnumerable<VersusStanding> Standings()
    {
        var order = new int[_scores.Length];
        for (int i = 0; i < order.Length; i++)
            order[i] = i;
        Array.Sort(order, (a, b) => _scores[b].Score.CompareTo(_scores[a].Score));

        int rank = 0;
        int lastScore = int.MinValue;
        for (int i = 0; i < order.Length; i++)
        {
            int index = order[i];
            int score = _scores[index].Score;
            if (score != lastScore)
            {
                rank = i + 1;
                lastScore = score;
            }
            yield return new VersusStanding(index, _scores[index].Kills, _scores[index].Deaths, rank, score);
        }
    }

    private Row? RowOf(int playerIndex) =>
        playerIndex >= 0 && playerIndex < _scores.Length ? _scores[playerIndex] : null;

    private void Complete()
    {
        if (Completed)
            return;
        Completed = true;
        MatchCompleted?.Invoke();
    }

    private sealed class Row
    {
        public int Kills;
        public int Deaths;
        public int Score;
    }
}
