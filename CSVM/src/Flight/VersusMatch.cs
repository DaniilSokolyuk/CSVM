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
/// turns them into standings and one completion event. It completes once, on the first of
/// <see cref="KillTarget"/>, <see cref="TimeLimit"/> (either disabled at 0) and
/// <see cref="AllAlone"/>, fewer than two pilots left with lives. It is deliberately not a Node
/// and is freed with the session. ⚠ Zero engine dependency of any kind, not even <c>Log</c>, a
/// <c>GD.Print</c> in this family once crashed the xUnit host. Keep it engine-free by construction.</summary>
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

    public VersusMatch(int playerCount, int killTarget = 5, float timeLimit = 300f, int lives = 0)
    {
        playerCount = Math.Max(1, playerCount); // 2-4 in practice; a solo session still scores
        KillTarget = Math.Max(0, killTarget);
        TimeLimit = Math.Max(0f, timeLimit);
        Lives = Math.Max(0, lives);
        _scores = new Row[playerCount];
        for (int i = 0; i < playerCount; i++)
            _scores[i] = new Row();
    }

    /// <summary>Fired exactly once, the moment the match completes, the results board's cue.</summary>
    public event Action? MatchCompleted;

    /// <summary>How many players are being scored.</summary>
    public int PlayerCount => _scores.Length;

    /// <summary>The score that ends the match, 0 = no kill target (time is then the only way to
    /// end it). Named for the menu row that sets it. The original compares that row against a
    /// running score, so suicides push a pilot back from it. A replicated match takes this from
    /// the host, whose lobby row is the only one in the match.</summary>
    public int KillTarget { get; private set; }

    /// <summary>Seconds on the match clock, 0 = no time limit (kills are then the only way to end it).</summary>
    public float TimeLimit { get; private set; }

    /// <summary>How many deaths a pilot has before it stays down for the rest of the match, 0 for
    /// no limit. The lobby's Limited Lives box; only the lobby's options message carries it, since
    /// every machine launches with the same lobby rules.</summary>
    public int Lives { get; }

    /// <summary>True on a guest: the clock, the limits and the ending arrive from the host through
    /// <see cref="ApplyState"/> and nothing local writes them. Scores still arrive as scores, so
    /// the standings are derived here rather than sent.</summary>
    public bool Replicated { get; private set; }

    /// <summary>Seconds of match clock consumed so far via <see cref="Advance"/>. Stops moving once
    /// <see cref="Completed"/>, a completed match's clock is frozen for display.</summary>
    public float Elapsed { get; private set; }

    /// <summary>Seconds left before a time-limited match times out, 0 once reached or when
    /// <see cref="TimeLimit"/> is disabled.</summary>
    public float TimeRemaining => TimeLimit > 0f ? Math.Max(0f, TimeLimit - Elapsed) : 0f;

    /// <summary>True once the match has ended (threshold or time-out), every further
    /// <see cref="RegisterKill"/>/<see cref="RegisterDeath"/>/<see cref="Advance"/> call is a no-op.</summary>
    public bool Completed { get; private set; }

    /// <summary>True when the match ended because fewer than two pilots with lives were left, the
    /// original's end reason 4 "No Enemies Left" (docs/org/multiplayer-scoring.md).</summary>
    public bool AllAlone { get; private set; }

    /// <summary>The seat an out-of-lives pilot watches. It keeps the one it watches while that one
    /// flies, else takes the next flying seat after <paramref name="self"/>, else none. The
    /// original binds its chase camera to the next aircraft still in flight.</summary>
    public static int? NextWatched(int self, IReadOnlyList<bool> flying, int? current)
    {
        if (current is { } now && now != self && now >= 0 && now < flying.Count && flying[now])
            return now;
        for (int step = 1; step < flying.Count; step++)
        {
            int seat = (self + step) % flying.Count;
            if (flying[seat])
                return seat;
        }
        return null;
    }

    public int KillsOf(int playerIndex) => RowOf(playerIndex)?.Kills ?? 0;

    public int DeathsOf(int playerIndex) => RowOf(playerIndex)?.Deaths ?? 0;

    /// <summary>Whether <paramref name="playerIndex"/> has spent its last life. Its deaths are the
    /// host's count, so every machine reads the same answer once the score arrives.</summary>
    public bool OutOfLives(int playerIndex) => Lives > 0 && DeathsOf(playerIndex) >= Lives;

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
        if (Reached(shooterRow))
            Complete();
        CheckAlone();
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
        CheckAlone();
    }

    /// <summary>A pilot who dropped out of the session. It stays on the scoreboard but no longer
    /// counts as an opponent. The drop can end the match on reason 4 as a death can.</summary>
    public void Leave(int playerIndex)
    {
        if (RowOf(playerIndex) is not { Left: false } row)
            return;
        row.Left = true;
        CheckAlone();
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
        if (Reached(row))
            Complete();
    }

    /// <summary>Advance the host-fed match clock by <paramref name="dt"/> seconds; completes the
    /// match once it reaches <see cref="TimeLimit"/>. No-op once <see cref="Completed"/>, when
    /// <see cref="TimeLimit"/> is disabled (an untimed match never times out), or on a
    /// <see cref="Replicated"/> match, whose clock is the host's.</summary>
    public void Advance(float dt)
    {
        if (Completed || Replicated || TimeLimit <= 0f)
            return;
        Elapsed += dt;
        if (Elapsed >= TimeLimit)
            Complete();
    }

    /// <summary>Hands the clock, the limits and the ending to a remote host. From here
    /// <see cref="Advance"/> moves nothing and no score can end the match. One way: a match never
    /// takes them back.</summary>
    public void Replicate() => Replicated = true;

    /// <summary>The host's match state, applied whole on a <see cref="Replicated"/> match: both
    /// its limits, its clock, and whether the round has ended. ⚠ This is the only thing that ends
    /// one, so a guest that reached the target itself flies on until the host says otherwise. An
    /// <paramref name="ended"/> of false re-arms a completed match, which is the host's rematch,
    /// and leaves the scores for the host to rewrite. Ignored on a host, the only writer of its
    /// own match.</summary>
    public void ApplyState(int killTarget, float timeLimit, float remainingSeconds, bool ended)
    {
        if (!Replicated)
            return;
        KillTarget = Math.Max(0, killTarget);
        TimeLimit = Math.Max(0f, timeLimit);
        if (TimeLimit > 0f)
            Elapsed = Math.Clamp(TimeLimit - remainingSeconds, 0f, TimeLimit);
        if (ended)
            Complete();
        else
            Completed = false;
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
        AllAlone = false;
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

    /// <summary>Whether this row has taken the match. Never on a <see cref="Replicated"/> one: a
    /// guest arming the target itself would hold its board before the host's.</summary>
    private bool Reached(Row? row) =>
        !Replicated && KillTarget > 0 && row != null && row.Score >= KillTarget;

    private Row? RowOf(int playerIndex) =>
        playerIndex >= 0 && playerIndex < _scores.Length ? _scores[playerIndex] : null;

    // Reason 4, run after every death and every drop as the original does. A match that opened
    // with one pilot has no opponent to lose, and a replicated one hears its ending from the host.
    private void CheckAlone()
    {
        if (Completed || Replicated || _scores.Length < 2)
            return;
        int standing = 0;
        for (int i = 0; i < _scores.Length; i++)
        {
            if (!_scores[i].Left && !OutOfLives(i))
                standing++;
        }
        if (standing >= 2)
            return;
        AllAlone = true;
        Complete();
    }

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
        public bool Left;
    }
}
