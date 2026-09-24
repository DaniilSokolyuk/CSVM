using System;
using System.IO;
using System.Linq;
using System.Text;
using CSVM.Flight.Weapons;
using CSVM.Mech3;
using CSVM.Session.Campaign;
using CSVM.Session.Objectives;
using CSVM.UI.Hangar;
using Godot;

namespace CSVM.Testing;

/// <summary>A <c>--campaign=</c> launch over a <c>--profiles=</c> store. The seat, the director and
/// the mission-end save all resolve through the named store. A copied profile whose file still
/// names the original saves into its own folder. The copy is the case a probe makes, and a lost
/// mission is the save that once wrote into a player's real profile.</summary>
internal static class CampaignScratchProfileSuites
{
    private const string Chapter = "C3";
    private const string Mission = "M01";
    private const string Original = "Original";
    private const string Copy = "Copy";
    private const float StepDt = 1f / 60f;
    private const float EndWindowS = 6f;

    // The original's own funds, a value no fresh profile holds, so the copy is known to be the
    // original's file and not a new profile.
    private const int OriginalFunds = 4321;

    [Suite("campaign-scratch-profiles",
        "a --campaign= launch naming a --profiles= store seats, directs and saves through that "
        + "store: a copied profile whose file still names the original loads under its folder's "
        + "name, a crash that loses C3/M01 records the attempt into the copy's own profile.json, "
        + "and the original's file stays byte-identical, with no third folder created")]
    internal static void CampaignScratchProfiles(TestContext ctx)
    {
        ctx.RequireData(ctx.PlanesGamezPath, $"planes gamez");
        ctx.RequireData(ctx.ZrdrPath, $"zrdr archive");
        string missionZrdr = SessionPaths.MissionZrdr(ctx.DataRoot, Chapter, Mission);
        string texturesPath = SessionPaths.ChapterTextures(ctx.DataRoot, Chapter);
        ctx.RequireData(missionZrdr, $"{Chapter}/{Mission} zrdr");
        ctx.RequireData(texturesPath, $"{Chapter} textures");
        var mission = CampaignSequence.Load(ctx.ZrdrPath).FirstOrDefault(m =>
            m.ChapterFolder.Equals(Chapter, StringComparison.OrdinalIgnoreCase)
            && m.MissionFolder.Equals(Mission, StringComparison.OrdinalIgnoreCase));
        if (mission.ChapterFolder == null)
        {
            throw new SuiteSkippedException($"{Chapter}/{Mission} is not in cm_sequence");
        }

        string dir = Path.Combine(ctx.ScratchDir, "campaign-scratch-profiles", "Profiles");
        var report = new StringBuilder();
        try
        {
            byte[] originalBytes = SeedStore(dir);
            report.AppendLine($"store {dir}; '{Copy}' is '{Original}' copied, file name unchanged");

            var spec = SessionSpec.Parse(new[] { $"--campaign={Copy}:{mission.Seq}", $"--profiles={dir}" });
            ctx.Check(spec.ProfilesDir == dir, $"--profiles= is carried on the spec as given, got {spec.ProfilesDir}");
            CheckLoad(ctx, spec, report);

            var seated = CampaignDirector.ResolveSeatedPlane(spec);
            string node = PlanePickerRoster.AirframeNode(5);
            ctx.Check(seated.PlaneNames.Count > 0 && seated.PlaneNames[0] == node,
                $"the seat is read off the copy in the named store, {node}");
            var director = CampaignDirector.TryCreate(seated, ctx.ZrdrPath, missionZrdr);
            ctx.Check(director != null, $"the director loads '{Copy}' from the named store");
            if (director == null)
            {
                return;
            }

            ctx.Check(director.PilotName == Copy, $"the director flies the copy under its folder's name, got {director.PilotName}");
            ctx.WithWorld(Chapter, collision: false, Mission,
                world => Crash(ctx, world, director, texturesPath, report));

            CheckSaved(ctx, dir, mission.Seq, originalBytes, report);
        }
        finally
        {
            ctx.WriteArtifact("test-campaign-scratch-profiles.txt", report.ToString());
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }

        ctx.Note($"a copied profile over a --profiles= store records its lost {Chapter}/{Mission} into its own folder");
    }

    // A store holding the original and a byte-for-byte copy of its folder, which is what an agent
    // copying a player's profile produces. Returns the original's file bytes.
    private static byte[] SeedStore(string dir)
    {
        if (Directory.Exists(dir))
        {
            Directory.Delete(dir, recursive: true);
        }

        var store = new CampaignProfileStore(dir);
        var original = CampaignProfileDef.NewProfile(Original);
        original.Funds = OriginalFunds;
        string path = store.Save(original);
        string copyDir = Path.Combine(dir, Copy);
        Directory.CreateDirectory(copyDir);
        File.Copy(path, Path.Combine(copyDir, Path.GetFileName(path)));
        return File.ReadAllBytes(path);
    }

    private static void CheckLoad(TestContext ctx, SessionSpec spec, StringBuilder report)
    {
        var store = CampaignProfileStore.ForSession(spec.ProfilesDir);
        var roster = store.List();
        report.AppendLine($"roster: {string.Join(", ", roster)}");
        ctx.Check(roster.SequenceEqual(new[] { Copy, Original }),
            $"the roster lists each folder once, by its folder's name: {string.Join(", ", roster)}");
        var copy = store.Load(Copy);
        ctx.Check(copy is { Name: Copy, Folder: Copy, Funds: OriginalFunds },
            $"the copy loads as '{Copy}' with the original's funds, name='{copy?.Name}' funds={copy?.Funds}");
    }

    // The production death path, driven as campaign-player-death drives it. One step lets the
    // director subscribe, then a forced crash and steps until the lost ending is recorded.
    private static void Crash(TestContext ctx, TestWorld world, CampaignDirector director,
        string texturesPath, StringBuilder report)
    {
        using var textures = new TextureArchive(texturesPath);
        var planesGamez = GameZ.Load(ctx.PlanesGamezPath);
        var pool = new ProjectilePool(textures, null, null);
        ctx.Host.AddChild(pool);
        var human = CampaignPlayerDeathSuites.HumanRig(ctx, planesGamez, textures, pool,
            new Vector3(0f, 800f, 0f), Vector3.Forward);
        try
        {
            director.Attach(new CampaignDirector.WorldInputs
            {
                Runtime = world.Runtime,
                Sounds = world.Runtime.Sounds,
                Projectiles = pool,
                ListenerPosition = () => human.WorldPosition,
                PlayerAircraft = () => human,
                Rng = new Random(1),
            });
            director.Step(StepDt);
            human.DebugForceCrash();
            for (float t = 0f; t < EndWindowS && director.Result == null; t += StepDt)
            {
                director.Step(StepDt);
            }

            report.AppendLine($"outcome: {director.Result?.Outcome.ToString() ?? "none"}");
            ctx.Check(director.Result?.Outcome == MissionOutcome.Lost,
                $"the crash loses the mission, outcome={director.Result?.Outcome.ToString() ?? "none"}");
        }
        finally
        {
            human.Free();
            pool.Free();
        }
    }

    private static void CheckSaved(TestContext ctx, string dir, int seq, byte[] originalBytes, StringBuilder report)
    {
        ctx.Check(File.ReadAllBytes(Path.Combine(dir, Original, "profile.json")).SequenceEqual(originalBytes),
            $"the original's profile.json is byte-identical after the copy's lost mission");
        var folders = Directory.GetDirectories(dir).Select(Path.GetFileName).OrderBy(n => n).ToArray();
        report.AppendLine($"folders after: {string.Join(", ", folders)}");
        ctx.Check(folders.SequenceEqual(new[] { Copy, Original }),
            $"the save made no folder of its own: {string.Join(", ", folders)}");
        var saved = CampaignProfileStore.Deserialize(File.ReadAllText(Path.Combine(dir, Copy, "profile.json")));
        var result = saved?.MissionResults.FirstOrDefault(r => r.Seq == seq);
        report.AppendLine($"copy file: name='{saved?.Name}' seq {seq} recorded={result != null}");
        ctx.Check(saved?.Name == Copy && result != null,
            $"the lost attempt is recorded in the copy's own file, under the copy's name");
    }
}
