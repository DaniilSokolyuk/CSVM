using System;
using CSVM.Flight.Airframe;
using CSVM.Flight.Weapons;
using CSVM.Mech3;
using Godot;

namespace CSVM.Flight.Hud;

/// <summary>The original's HUD message stack, the one centred text element every mission line
/// lands in: four lines a fifth of the way down the pane, newest in slot 0, each carrying its own
/// colour and its own five seconds. A new line pushes the older ones down with their remaining
/// time, and re-posting the line already in slot 0 refreshes it instead of pushing a duplicate.
/// A death, a ground impact and the mission clock running out all post here. The wording, the side
/// colour and the slot geometry are static, so a suite asserts the decode with no Control in the
/// process. Decode: docs/org/vehicleDamage.md "The kill message", docs/formats/objectives.md.</summary>
public sealed partial class HudMessages : Control
{
    /// <summary>Lines the stack holds (<c>FUN_00458660</c> writes 4 at <c>0x00458698</c>).</summary>
    public const int Slots = 4;

    /// <summary>Seconds a posted line lives (<c>0x40a00000</c> into <c>FUN_005c55f0</c>, which
    /// stores it at the text object's <c>+0x10</c>).</summary>
    public const float LineLife = 5f;

    /// <summary>Longest line kept whole; a longer one is split at its last space at or before this
    /// (<c>FUN_004588e0</c> tests the length against <c>0x31</c> at <c>0x004588f9</c>).</summary>
    public const int WholeLineChars = 48;

    /// <summary>"was shot down", <c>extracted/messages.json</c> row 169, taken for an aeroplane
    /// victim (<c>FUN_0059ce40(0xa9)</c> at <c>0x004b8557</c> and <c>0x004b85af</c>).</summary>
    public const string ShotDownKey = "MSG_SHOT_DOWN";

    /// <summary>"Wingman was shot down", row 174, the whole line for an aeroplane on the viewer's
    /// own team, with no name before it (<c>FUN_0059ce40(0xae)</c> at <c>0x004b858e</c>).</summary>
    public const string WingmanKey = "MSG_WINGMAN_SHOT_DOWN";

    /// <summary>"was destroyed", row 180, taken by any victim that is not an aeroplane, whatever
    /// its team (<c>FUN_0059ce40(0xb4)</c> at <c>0x004b852e</c>).</summary>
    public const string DestroyedKey = "MSG_DESTROYED";

    /// <summary>"Fatal Crash!", row 162, the notice a ground impact posts for the local player
    /// alone (<c>FUN_0059ce40(0xa2)</c> inside <c>FUN_0048b920</c>'s
    /// <c>DAT_0071c298</c> arm).</summary>
    public const string CrashKey = "MSG_CRASH";

    /// <summary>"Time Expired", row 6002, the first line the mission clock running out posts
    /// (<c>FUN_0059ce40(0x1772)</c> at the objectives tick's head).</summary>
    public const string TimeExpiredKey = "MSG_TIME_EXPIRED";

    /// <summary>"Mission LOST!", row 137, posted after <see cref="TimeExpiredKey"/> so it reads
    /// above it, and only where the mission is not already won.</summary>
    public const string MissionLostKey = "MSG_MISSION_LOST";

    /// <summary>"You Are Out of Lives!", row 209, a Limited Lives death that spent the last one
    /// (<c>FUN_00498bf0</c>, <c>0x498c52</c>-<c>0x498cba</c>).</summary>
    public const string NoLivesKey = "MSG_NO_LIVES";

    /// <summary>"You Have %1!d! Lives Left!", row 210, a death that left more than one.</summary>
    public const string NumLivesKey = "MSG_NUM_LIVES";

    /// <summary>"You Have ONE Life Left!", row 211, a death that left exactly one.</summary>
    public const string OneLifeKey = "MSG_ONE_LIFE";

    /// <summary>"Game Over:", row 135, the first of reason 4's two lines (<c>FUN_004996d0(4)</c>).</summary>
    public const string GameOverKey = "MSG_STATE_GAMEOVER";

    /// <summary>"No Enemies Left", row 7067, the second of them.</summary>
    public const string AllAloneKey = "MSG_MP_ALL_ALONE";

    // The placement, from FUN_00458a10: x is 0.5 of the display width (0x006032e0) with the
    // centring flag set (the text object's +0x1044, read at 0x005c7e4f), y is 0.2 of its height
    // (0x006034fc), and each further slot sits 18 px lower (FUN_00458530's `+ 0x12`). The 18 px and
    // the font's own 10 px cell (hudMsgBrief in extracted/zrdr/fonts.zrd.json: Arial, height -10,
    // width 6, weight 500, shadow true) are display pixels at the original's 480-line mode, so both
    // are carried onto HudMetrics' 1440p reference by the same factor of three.
    private const float XFraction = 0.5f;
    private const float YFraction = 0.2f;
    private const float RefLinePitch = 54f;
    private const int RefFontSize = 30;

    // TUNE: the three COLORREF globals the death routine picks between (0x006eba60 for a victim
    // above team 1, 0x006eba64 for the viewer's own team, 0x006eba5c otherwise) are zero in the
    // image and nothing in crimson.exe writes them, so the shipped values are not decodable
    // statically. The SIDE rule is decoded; these are the remake's HUD palette standing in for it.
    private static readonly Color EnemyColor = new(1f, 0.55f, 0.55f);
    private static readonly Color FriendlyColor = new(0.55f, 0.78f, 1f);
    private static readonly Color NeutralColor = new(0.9f, 0.9f, 0.9f);

    private readonly string[] _text = new string[Slots];
    private readonly float[] _life = new float[Slots];
    private readonly Side[] _side = new Side[Slots];
    private readonly string[] _one = new string[1]; // reused: MarkerDraw.Lines takes a list

    /// <summary>Which of the three colours a line takes, chosen from the victim's team against the
    /// reading pane's own (<see cref="SideOf"/>).</summary>
    public enum Side
    {
        /// <summary>The viewer's own team.</summary>
        Friendly,

        /// <summary>A team above the player's, which is every enemy side.</summary>
        Enemy,

        /// <summary>Neither: the neutral team, which takes the stack's default colour.</summary>
        Neutral,
    }

    /// <summary>Whether a victim counts as an aeroplane, which is what decides between the
    /// shot-down wording and <see cref="DestroyedKey"/>: the vehicle's dispatch class
    /// <c>+0x67c</c> being 0 (<c>jet</c>) or 4 (<c>wingman</c>), tested at <c>0x004b850b</c>. A def
    /// authoring no <c>mode</c> inherits <c>jet</c>, so a null mode is an aeroplane.</summary>
    public static bool IsAeroplane(PlaneStats? stats) =>
        stats?.VehicleMode is not { } mode
        || mode.Equals(VehicleDefs.JetMode, StringComparison.OrdinalIgnoreCase)
        || mode.Equals(VehicleDefs.WingmanMode, StringComparison.OrdinalIgnoreCase);

    /// <summary>The colour arm one victim's death takes in one pane: a team above
    /// <see cref="AimAssist.PlayerTeam"/> is <see cref="Side.Enemy"/>, the pane's own team is
    /// <see cref="Side.Friendly"/>, and anything else (the neutral team) takes the default. The
    /// three-way fork is at <c>0x004b85c9</c>-<c>0x004b8621</c>.</summary>
    public static Side SideOf(int victimTeam, int viewerTeam) =>
        victimTeam > AimAssist.PlayerTeam ? Side.Enemy
        : victimTeam == viewerTeam ? Side.Friendly
        : Side.Neutral;

    /// <summary>The line one vehicle's death posts, in the decoded order of the four rules: a
    /// non-aeroplane takes its name and <see cref="DestroyedKey"/> whatever its team; the reading
    /// pane's own aircraft takes the pilot's name and <see cref="ShotDownKey"/>; an aeroplane on
    /// that pane's team takes <see cref="WingmanKey"/> alone, with no name; any other aeroplane
    /// takes its own name and <see cref="ShotDownKey"/>. A nameless pilot falls through to the
    /// team rule exactly as the original's unset <c>PlayerName</c> does.</summary>
    public static string KillLine(Messages? strings, bool aeroplane, string? victimName,
        bool victimIsViewer, string? viewerName, bool victimOnViewerTeam)
    {
        if (!aeroplane)
        {
            return Named(victimName, Text(strings, DestroyedKey));
        }
        if (victimIsViewer && viewerName is { Length: > 0 } pilot)
        {
            return Named(pilot, Text(strings, ShotDownKey));
        }
        if (victimOnViewerTeam)
        {
            return Text(strings, WingmanKey);
        }
        return Named(victimName, Text(strings, ShotDownKey));
    }

    /// <summary>The same line for a live victim as one pane reads it. The name is the airframe's
    /// own display name, which prefers the block's slot-20 <c>title</c> ("Medusa Kestrel"), the
    /// entity <c>+0x14</c> string the original reads; where no title is authored the original
    /// prints nothing at all, and this falls back to the airframe rather than leaving the line
    /// headless.</summary>
    public static string KillLine(Messages? strings, FlightController victim, int viewerTeam,
        bool victimIsViewer, string? viewerName) =>
        KillLine(strings, IsAeroplane(victim.Stats), NameOf(victim), victimIsViewer, viewerName,
            victim.Team == viewerTeam);

    /// <summary>Posts one victim's death into one pane's stack: the decoded wording as that pane
    /// reads it, in the colour its team takes. The session and the suite go through this, so there
    /// is one composition of a kill rather than two that can drift.</summary>
    public static void PostKill(HudMessages stack, Messages? strings, FlightController victim,
        int viewerTeam, bool victimIsViewer, string? viewerName) =>
        stack.Post(KillLine(strings, victim, viewerTeam, victimIsViewer, viewerName),
            SideOf(victim.Team, viewerTeam));

    /// <summary>Whether a victim's death report words a kill line at all. The original's
    /// ground-impact routine posts the crash notice alone, and only its health-zero routine words a
    /// line, so an aircraft flown into the world is reported to the score and the mission without
    /// one. The remake carries both deaths on a single report, which is why the fork is here.
    /// </summary>
    public static bool WordsKillLine(FlightController victim) =>
        !victim.Crashed || victim.Destroyed;

    /// <summary>Posts the crash notice into the crashing pilot's own pane, in that player's own
    /// colour. The original posts it for the local player alone and on every ground impact its
    /// aircraft performs, the shot-down wreck reaching the ground included.</summary>
    public static void PostCrash(HudMessages stack, Messages? strings) =>
        stack.Post(Text(strings, CrashKey), Side.Friendly);

    /// <summary>Posts the mission clock's expiry into one pane's stack: "Time Expired" and then
    /// "Mission LOST!" over it, both in the stack's default colour.</summary>
    public static void PostTimeExpired(HudMessages stack, Messages? strings)
    {
        stack.Post(Text(strings, TimeExpiredKey), Side.Neutral);
        stack.Post(Text(strings, MissionLostKey), Side.Neutral);
    }

    /// <summary>The line a Limited Lives death shows its own pilot, picked by the lives left. None
    /// takes row 209, one takes row 211, and more take row 210 with the count filled in.</summary>
    public static string LivesLine(Messages? strings, int livesLeft) =>
        livesLeft <= 0 ? Text(strings, NoLivesKey)
        : livesLeft == 1 ? Text(strings, OneLifeKey)
        : Messages.Fill(Text(strings, NumLivesKey), livesLeft.ToString(System.Globalization.CultureInfo.InvariantCulture));

    /// <summary>Posts <see cref="LivesLine"/> into the dying pilot's own pane, in its own colour.</summary>
    public static void PostLivesLeft(HudMessages stack, Messages? strings, int livesLeft) =>
        stack.Post(LivesLine(strings, livesLeft), Side.Friendly);

    /// <summary>Posts reason 4's ending so "Game Over:" reads above "No Enemies Left", both in the
    /// stack's default colour.</summary>
    public static void PostAllAlone(HudMessages stack, Messages? strings)
    {
        stack.Post(Text(strings, AllAloneKey), Side.Neutral);
        stack.Post(Text(strings, GameOverKey), Side.Neutral);
    }

    /// <summary>Where slot <paramref name="slot"/>'s line is anchored in a pane of
    /// <paramref name="paneSize"/> at HUD scale <paramref name="scale"/>: the horizontal centre,
    /// a fifth of the way down, one 18 px pitch per slot below that.</summary>
    public static Vector2 SlotAnchor(Vector2 paneSize, int slot, float scale) =>
        new(paneSize.X * XFraction, (paneSize.Y * YFraction) + (RefLinePitch * scale * slot));

    /// <summary>Posts one line into slot 0. A line over <see cref="WholeLineChars"/> is split at
    /// its last space at or before that and enters as two slots reading top to bottom, with the
    /// tail truncated to the same length; one with no space to split at enters whole.</summary>
    public void Post(string? text, Side side)
    {
        if (text is not { Length: > 0 } line)
        {
            return;
        }
        if (line.Length <= WholeLineChars)
        {
            Enter(line, side);
            return;
        }
        int at = line.LastIndexOf(' ', Math.Min(WholeLineChars, line.Length - 1));
        if (at <= 0)
        {
            Enter(line, side);
            return;
        }
        // The tail goes in first so the head lands in slot 0 and the pair reads in order, which is
        // the order FUN_004588e0 posts them in (0x00458984, then 0x00458995).
        string tail = line[(at + 1)..];
        Enter(tail.Length > WholeLineChars ? tail[..WholeLineChars] : tail, side);
        Enter(line[..at], side);
    }

    /// <summary>Empties the stack, what a respawn or a restarted match calls.</summary>
    public void Clear()
    {
        Array.Clear(_life, 0, Slots);
        Array.Clear(_text, 0, Slots);
        QueueRedraw();
    }

    /// <summary>The line slot <paramref name="slot"/> is showing, or null once it has expired.
    /// The stack's readback, so a suite asserts what a frame would draw.</summary>
    public string? LineAt(int slot) =>
        slot >= 0 && slot < Slots && _life[slot] > 0f ? _text[slot] : null;

    /// <summary>The colour arm slot <paramref name="slot"/>'s line was posted with.</summary>
    public Side SideAt(int slot) => _side[Mathf.Clamp(slot, 0, Slots - 1)];

    /// <summary>Seconds slot <paramref name="slot"/>'s line has left, 0 once it is spent.</summary>
    public float LifeAt(int slot) =>
        slot >= 0 && slot < Slots ? Mathf.Max(0f, _life[slot]) : 0f;

    /// <summary>Counts every live line down by <paramref name="delta"/> seconds. Public so a suite
    /// can run the stack's whole five seconds without a frame loop.</summary>
    public void Advance(float delta)
    {
        bool any = false;
        for (int i = 0; i < Slots; i++)
        {
            if (_life[i] > 0f)
            {
                _life[i] -= delta;
                any = true;
            }
        }
        if (any)
        {
            QueueRedraw();
        }
    }

    public override void _Process(double delta)
    {
        // Track the pane (resizable window / splitscreen layout), the same per-frame resize
        // VersusHud does, since both anchor off the pane's own size.
        Position = Vector2.Zero;
        Size = GetViewportRect().Size;
        Advance((float)delta);
    }

    public override void _Draw()
    {
        // Same zero-size guard as the other pane HUDs: a draw can land before _Process has sized
        // this pane.
        float s = Size.Y <= 0f ? 0f : HudMetrics.Scale(this);
        if (s <= 0f)
        {
            return;
        }
        var font = GetThemeDefaultFont();
        int fontSize = Mathf.Max(1, Mathf.RoundToInt(RefFontSize * s * HudMetrics.StatusTextScale));
        for (int i = 0; i < Slots; i++)
        {
            if (_life[i] <= 0f || _text[i] is not { Length: > 0 } line)
            {
                continue;
            }
            _one[0] = line;
            MarkerDraw.Lines(this, font, SlotAnchor(Size, i, s), _one, fontSize, ColorOf(_side[i]),
                topAnchored: true);
        }
    }

    private static string Text(Messages? strings, string key) => strings?.Get(key) ?? key;

    // sprintf("%s %s", name, message): an unnamed victim keeps the leading space the original
    // writes, since the empty name is a string it formats with, not a case it skips.
    private static string Named(string? name, string message) => $"{name ?? string.Empty} {message}";

    private static string NameOf(FlightController victim) =>
        victim.Stats is { } stats ? PlaneRoster.PlaneDisplayName(stats) : string.Empty;

    private static Color ColorOf(Side side) => side switch
    {
        Side.Friendly => FriendlyColor,
        Side.Enemy => EnemyColor,
        _ => NeutralColor,
    };

    // Slot 0 takes the line and the older lines shift down carrying their own remaining time and
    // colour (FUN_00458350). Two guards keep a shift from happening at all: a spent slot 0 has
    // nothing to push (the +0xc flag test at 0x00458361) and a line identical to the one already
    // there refreshes it rather than duplicating it (the strcmp at 0x00458380).
    private void Enter(string text, Side side)
    {
        if (_life[0] > 0f && !string.Equals(_text[0], text, StringComparison.Ordinal))
        {
            for (int i = Slots - 1; i > 0; i--)
            {
                _text[i] = _text[i - 1];
                _life[i] = _life[i - 1];
                _side[i] = _side[i - 1];
            }
        }
        _text[0] = text;
        _life[0] = LineLife;
        _side[0] = side;
        QueueRedraw();
    }
}
