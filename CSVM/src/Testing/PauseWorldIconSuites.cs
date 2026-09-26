using System;
using System.Collections.Generic;
using System.Text;
using CSVM.Flight.Hud;
using CSVM.Flight.Modes;
using CSVM.Mech3;
using CSVM.Session.Objectives;
using CSVM.Session.World;
using CSVM.UI.Boards;
using CSVM.UI.Menu;
using CSVM.UI.Overlays;
using Godot;

namespace CSVM.Testing;

/// <summary>The suite over the two icons the Original pause sheet places by world position: the
/// ownship, taken from the paused seat's own aircraft, and the zeppelin, taken from the
/// <c>piratezep</c> hull the mission's own zeppelin record seats. Both go through the dialog's own
/// world window, which is what decides whether either draws at all. Decode:
/// docs/org/pause-screen.md.</summary>
internal static class PauseWorldIconSuites
{
    // The story position whose sheet carries both icons (C3/M04): its PLAYER_INIT and its own
    // Pandora hull are inside the dialog's world window, so the chart shows both.
    private const int BothSeq = 4;

    // The story position whose opening pose is east of its chart (C3/M01), on the same sheet and
    // the same window as the one above. Neither icon draws until the player flies onto the chart.
    private const int OffChartSeq = 0;

    // Nathan Zachary's own zeppelin, the world node the shared MYZEP icon stands for.
    private const string ZepNode = "piratezep";

    // How far an icon's drawn nose may stand from the compass reading of the same pose, in degrees.
    // The two are the same conversion, so this is float slack and nothing else.
    private const float CompassSlackDeg = 0.05f;

    // How far a projected literal waypoint may stand from the authored flag pin it belongs to,
    // in board pixels. The flag art is 76x80 and its point is the pole's foot, so agreement is
    // measured in tens of pixels rather than in ones.
    private const float PinReach = 40f;

    // The poses the icons are read against the compass at: the mission's own authored yaw, the four
    // cardinals and a pair either side of north, which is where a sign error hides.
    private static readonly float[] CompassYaws = { 0f, 40f, 90f, 135f, 180f, 270f, 315f };

    /// <summary>The pause sheet's world-placed icons over a BUILT mission world: the player's own
    /// spawn and the mission's zeppelin both reach the chart, and the window that decides it is
    /// the one the mission's own geometry projects through.</summary>
    [Suite("pause-world-icons",
        "the Original pause sheet's two world-placed icons over CM05's BUILT world: the ownship "
        + "at the mission's PLAYER_INIT and the zeppelin at the piratezep hull its own record "
        + "seats both compose onto the sheet and land inside the map's authored screen rectangle, "
        + "each icon's drawn nose (its own art's nose plus the turn it takes) points where the "
        + "compass tape reads for the same pose, the "
        + "projection that puts them there agrees with the chart's own authored flag pins (a "
        + "literal TRAVELERS waypoint lands within 40 px of the pin it belongs to), and the "
        + "mission that opens east of the same chart places neither icon, which is the window's "
        + "answer rather than a lookup that found nothing")]
    internal static void PauseWorldIcons(TestContext ctx)
    {
        ctx.RequireData(ctx.ZrdrPath, $"zrdr archive");
        var missions = CampaignSequence.Load(ctx.ZrdrPath);
        if (MissionAt(missions, BothSeq) is not { } mission)
        {
            throw new SuiteSkippedException($"cm_sequence carries no story position {BothSeq}");
        }

        string chapter = mission.ChapterFolder.ToUpperInvariant();
        string folder = mission.MissionFolder.ToUpperInvariant();
        string missionZrdr = SessionPaths.MissionZrdr(ctx.DataRoot, chapter, folder);
        string chapterZrdr = SessionPaths.ChapterZrdr(ctx.DataRoot, chapter);
        ctx.RequireData(missionZrdr, $"{chapter}/{folder} zrdr");
        ctx.RequireData(chapterZrdr, $"{chapter} zrdr");
        ctx.RequireData(SessionPaths.ChapterTextures(ctx.DataRoot, chapter), $"{chapter} textures");

        var sheet = SheetOf(ctx, mission);
        var report = new StringBuilder();
        if (sheet == null || sheet.State.Map is not { } map)
        {
            ctx.Check(false, $"{chapter}/{folder} resolves a pause dialog with a map on it");
            return;
        }

        report.AppendLine(
            $"{chapter}/{folder} map={map.Bitmap} screen=[{map.ScreenX0}..{map.ScreenX1}] x "
            + $"[{map.ScreenY0}..{map.ScreenY1}] world=[{map.World.X0},{map.World.Y0}].."
            + $"[{map.World.X1},{map.World.Y1}]");
        ctx.Check(
            sheet.Shared.OwnShip.Length > 0 && sheet.Shared.MyZep.Length > 0,
            $"the shared block names both icons ({sheet.Shared.OwnShip}, {sheet.Shared.MyZep})");

        var start = SpawnPoints.LoadPlayerInit(missionZrdr);
        ctx.Check(start != null, $"{chapter}/{folder} authors a PLAYER_INIT the player starts on");
        if (start is not { } init)
        {
            return;
        }

        var zeppelins = Zeppelins.Load(missionZrdr);
        var nets = AiNets.Load(chapterZrdr);
        ctx.WithWorld(chapter, collision: false, folder,
            world => CheckBothIcons(ctx, sheet, map, init, world, zeppelins, nets, report));
        CheckIconsAgainstCompass(ctx, sheet, report);
        CheckProjectionAgainstPins(ctx, missions, report);
        CheckOffChartOpening(ctx, missions, report);

        ctx.WriteArtifact($"test-pause-world-icons.txt", report.ToString());
        ctx.Note($"placed the pause sheet's two world icons over {chapter}/{folder}'s built world");
    }

    // Both icons over the built world, composed through the real screen the session composes: the
    // ownship at the authored spawn, the zeppelin at the hull the mission's own record seats.
    // ⚠ The zeppelin runtime is built here rather than read off the raw world: a chapter gamez
    // ships every hull on the world origin, and the mission record is what seats it. A world built
    // without that runtime holds the hull at (0, 0), which is off every chart there is.
    private static void CheckBothIcons(
        TestContext ctx,
        PauseSheet sheet,
        EscapeMap map,
        PlayerStart init,
        TestWorld world,
        IReadOnlyList<ZeppelinDef> zeppelins,
        IReadOnlyList<AiNet> nets,
        StringBuilder report)
    {
        var icons = new List<PauseWorldIcon>();
        var nose = new Basis(Vector3.Up, Mathf.DegToRad(init.Spawn.HeadingDeg)) * Vector3.Forward;
        if (PauseReadout.Icon(
            sheet.Shared.OwnShip, init.Spawn.Position.X, init.Spawn.Position.Z, nose.X, nose.Z)
            is { } ship)
        {
            icons.Add(ship);
        }

        ZeppelinRuntime? seated = null;
        try
        {
            seated = new ZeppelinRuntime(
                zeppelins,
                name => world.Runtime.FindNodes(name, null) is { Count: > 0 } hits ? hits[0] : null,
                nets);
            ctx.Host.AddChild(seated);

            var hulls = world.Runtime.FindNodes(ZepNode);
            ctx.Check(hulls.Count > 0, $"the built world carries the mission's own {ZepNode} hull");
            foreach (var hull in hulls)
            {
                var heading = -hull.GlobalTransform.Basis.Z;
                if (PauseReadout.Icon(
                    sheet.Shared.MyZep, hull.GlobalPosition.X, hull.GlobalPosition.Z,
                    heading.X, heading.Z) is { } zeppelin)
                {
                    icons.Add(zeppelin);
                }

                report.AppendLine(
                    $"{ZepNode} seated at ({hull.GlobalPosition.X:0}, {hull.GlobalPosition.Z:0})");
                ctx.Check(
                    hull.GlobalPosition.LengthSquared() > 1f,
                    $"…and its record seats that hull off the world origin");
                break;
            }
        }
        finally
        {
            seated?.Free();
        }

        ctx.Same(2, icons.Count, $"the readout carries an ownship icon and a zeppelin icon");
        var board = PauseScreens.For(
            sheet, new PauseReadout(Array.Empty<PauseObjective>(), string.Empty, icons), 0, false);
        foreach (var icon in icons)
        {
            var placed = FindArt(board, icon.Bitmap);
            bool inside = placed != null
                && placed.X >= map.ScreenX0 && placed.X <= map.ScreenX1
                && placed.Y >= map.ScreenY0 && placed.Y <= map.ScreenY1;
            report.AppendLine(
                $"{icon.Bitmap} world ({icon.WorldX:0}, {icon.WorldZ:0}) -> "
                + $"({placed?.X}, {placed?.Y}) inside={inside}");
            ctx.Check(
                inside && placed!.Centered,
                $"{icon.Bitmap} lands centred inside the chart's screen rectangle at ({placed?.X}, {placed?.Y})");
        }
    }

    // The sheet against the instrument the pilot reads: for one nose vector, an icon's drawn nose
    // (where its own art points, plus the turn it is given) has to land on the heading the compass
    // tape shows, or the chart and the cockpit disagree. Both icons, since they share the one
    // conversion and only the player's art is drawn off the top of the sheet.
    private static void CheckIconsAgainstCompass(
        TestContext ctx, PauseSheet sheet, StringBuilder report)
    {
        foreach (string bitmap in new[] { sheet.Shared.OwnShip, sheet.Shared.MyZep })
        {
            foreach (float yawDeg in CompassYaws)
            {
                var nose = new Basis(Vector3.Up, Mathf.DegToRad(yawDeg)) * Vector3.Forward;
                if (PauseReadout.Icon(bitmap, 0f, 0f, nose.X, nose.Z) is not { } icon)
                {
                    ctx.Check(false, $"{bitmap} takes an icon at yaw {yawDeg:0}");
                    continue;
                }

                float compass = CompassTape.ReadingDeg(nose);
                float drawn = Mathf.PosMod((MissionMap.ArtRevs(bitmap) + icon.Revs) * 360f, 360f);
                report.AppendLine(
                    $"{bitmap} yaw {yawDeg:0} -> compass {compass:0.0}, turn "
                    + $"{Mathf.PosMod(icon.Revs * 360f, 360f):0.0}, nose {drawn:0.0}");
                ctx.Check(
                    Mathf.Abs(Mathf.Wrap(drawn - compass, -180f, 180f)) <= CompassSlackDeg,
                    $"{bitmap}'s nose at yaw {yawDeg:0} points where the compass reads ({drawn:0.0} vs {compass:0.0})");
            }
        }
    }

    // The projection against the chart's own authored art: a mission's literal TRAVELERS waypoint
    // is a place the flags mark, so projecting it has to land on one of them. This is what says the
    // world window is read right, independently of any spawn.
    private static void CheckProjectionAgainstPins(
        TestContext ctx, IReadOnlyList<CampaignMission> missions, StringBuilder report)
    {
        if (MissionAt(missions, OffChartSeq) is not { } mission
            || SheetOf(ctx, mission) is not { } sheet
            || sheet.State.Map is not { } map)
        {
            ctx.Check(false, $"story position {OffChartSeq} resolves a pause dialog with a map");
            return;
        }

        var pins = new List<BriefingPoint>();
        foreach (var element in sheet.Reveal.Elements)
        {
            if (element.Id.StartsWith("OBJPIN", StringComparison.Ordinal))
            {
                pins.Add(element.At);
            }
        }

        ctx.Check(pins.Count > 0, $"the chart carries authored flag pins to measure against");
        float best = float.MaxValue;
        int measured = 0;
        string chapter = mission.ChapterFolder.ToUpperInvariant();
        var script = ObjectiveScript.Load(
            SessionPaths.MissionZrdr(ctx.DataRoot, chapter, mission.MissionFolder.ToUpperInvariant()));
        foreach (var objective in script.Objectives)
        {
            if (objective.Travelers is not { WherePoint: { Length: >= 3 } at }
                || !map.TryProject(at[0], at[2], out var on))
            {
                continue;
            }

            measured++;
            foreach (var pin in pins)
            {
                float dx = on.X - pin.X;
                float dy = on.Y - pin.Y;
                best = Math.Min(best, Mathf.Sqrt((dx * dx) + (dy * dy)));
            }

            report.AppendLine(
                $"waypoint ({at[0]:0}, {at[2]:0}) -> ({on.X}, {on.Y}), nearest pin {best:0} px");
        }

        ctx.Check(measured > 0, $"{chapter} authors a literal waypoint inside its own chart window");
        ctx.Check(
            best <= PinReach,
            $"a literal waypoint lands on the flag pin it belongs to ({best:0} px, cap {PinReach:0})");
    }

    // The control the report needs: on the mission that opens east of this chart, the same
    // composition places nothing, so an empty chart is the window's answer and not a broken one.
    private static void CheckOffChartOpening(
        TestContext ctx, IReadOnlyList<CampaignMission> missions, StringBuilder report)
    {
        if (MissionAt(missions, OffChartSeq) is not { } mission
            || SheetOf(ctx, mission) is not { } sheet
            || sheet.State.Map is not { } map)
        {
            return;
        }

        string chapter = mission.ChapterFolder.ToUpperInvariant();
        string folder = mission.MissionFolder.ToUpperInvariant();
        var start = SpawnPoints.LoadPlayerInit(SessionPaths.MissionZrdr(ctx.DataRoot, chapter, folder));
        if (start is not { } init)
        {
            ctx.Check(false, $"{chapter}/{folder} authors a PLAYER_INIT");
            return;
        }

        bool on = map.TryProject(init.Spawn.Position.X, init.Spawn.Position.Z, out var at);
        report.AppendLine(
            $"{chapter}/{folder} opens at ({init.Spawn.Position.X:0}, {init.Spawn.Position.Z:0}) "
            + $"-> ({at.X}, {at.Y}) on={on}");
        ctx.Check(!on, $"{chapter}/{folder} opens outside its own chart's world window");

        var icon = PauseReadout.Icon(
            sheet.Shared.OwnShip, init.Spawn.Position.X, init.Spawn.Position.Z, 0f, -1f);
        var board = PauseScreens.For(
            sheet,
            new PauseReadout(
                Array.Empty<PauseObjective>(), string.Empty,
                icon is { } only ? new[] { only } : Array.Empty<PauseWorldIcon>()),
            0,
            false);
        ctx.Check(icon != null, $"the readout still carries the ownship icon there");
        ctx.Check(
            FindArt(board, sheet.Shared.OwnShip) == null,
            $"and the sheet draws it nowhere, rather than clamping it to an edge");
    }

    private static CampaignMission? MissionAt(IReadOnlyList<CampaignMission> missions, int seq)
    {
        foreach (var mission in missions)
        {
            if (mission.Seq == seq)
            {
                return mission;
            }
        }

        return null;
    }

    private static PauseSheet? SheetOf(TestContext ctx, CampaignMission mission) =>
        PauseSheet.Load(
            ctx.ZrdrPath, ctx.MessagesPath,
            EscapeDialog.CampaignKey(mission.Campaign, mission.Mission), instantAction: false);

    private static BoardPicture? FindArt(ComposedBoard board, string name)
    {
        foreach (var picture in board.Pictures)
        {
            if (picture.Art.Name == name)
            {
                return picture;
            }
        }

        return null;
    }
}
