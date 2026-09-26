using System.Collections.Generic;
using System.IO;
using System.Text;
using CSVM.Mech3;
using CSVM.Session.Campaign;
using CSVM.UI.Boards;
using CSVM.UI.Campaign;

namespace CSVM.Testing;

/// <summary>
/// <c>FLIGHTCHECK.SCRIPT</c>'s two plane-change rules and the story aircraft it grants on entry
/// (docs/formats/campaign-screens.md, "Plane change"), driven over the flight check the flow really
/// opens. CM13 carries both rules and a wingman row to tell the slots apart: the pilot's button is
/// barred by the mission, the wingman's by the owned count less one against a floor of three, and
/// <c>uiData</c> 2021 grants the mission's aircraft on the first entry and re-selects it on every
/// later one. Every leg runs on both sides of that floor, and the control is the same profile on a
/// neighbouring mission the script neither grants on nor discounts.
/// </summary>
internal static class CampaignPlaneChangeSuites
{
    // CM13, cm_sequence seq 12 (docs/formats/campaign-missions.md). It is the grant mission that
    // also authors a wingman, so it is the only one of the two where the two slots can differ.
    private const int GrantSeq = 12;

    // CM12, seq 11: the neighbouring mission the script grants nothing on and discounts nothing on.
    private const int ControlSeq = 11;

    // The original's two crew slots, -1 the pilot and -2 the wingman.
    private const int PilotSlot = 0;
    private const int WingmanSlot = 1;

    // The script's own floor, restated here so the suite fails rather than tracks a changed rule.
    private const int Floor = 3;

    [Suite("campaign-flight-check-plane-change",
        "CM13's flight check under FLIGHTCHECK.SCRIPT's own two plane-change rules and its "
        + "uiData 2021 grant: a two-plane profile is granted the mission's aircraft on entry and "
        + "still offers neither CHANGE PLANE, because the owned count less one is under three; a "
        + "three-plane profile is granted it and offers the wingman's button alone, the pilot's "
        + "barred by the mission; a profile that already carries the award is granted nothing a "
        + "second time and has it re-selected; and the same profile on the neighbouring mission "
        + "keeps its selection and offers both buttons")]
    internal static void CampaignFlightCheckPlaneChange(TestContext ctx)
    {
        ctx.RequireData(ctx.ZrdrPath, $"zrdr archive");
        var report = new StringBuilder();
        var reward = CheckAuthored(ctx, ctx.ZrdrPath, report);
        string root = Path.Combine(ctx.ScratchDir, "campaign-plane-change");
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }

        var store = new CampaignProfileStore(Path.Combine(root, "Profiles"));
        var strings = UiStrings.TryLoad(ctx.DataRoot) ?? UiStrings.Empty;
        try
        {
            BelowTheFloor(ctx, store, strings, ctx.DataRoot, reward, report);
            AtTheFloor(ctx, store, strings, ctx.DataRoot, reward, report);
            AlreadyGranted(ctx, store, strings, ctx.DataRoot, reward, report);
            OnAnotherMission(ctx, store, strings, ctx.DataRoot, reward, report);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }

        ctx.WriteArtifact("test-campaign-flight-check-plane-change.txt", report.ToString());
        ctx.Note($"seq {GrantSeq} grants '{reward.Name}' on entry; the floor of {Floor} is read against the owned count less one");
    }

    // What the shipped data says before anything is driven: both missions author a wingman row, and
    // the reward table's own entry for this mission awards an aircraft under no objective gate. A
    // suite that drove a re-authored table would prove nothing about the game.
    private static MissionReward CheckAuthored(TestContext ctx, string zrdrPath, StringBuilder report)
    {
        var wings = new Dictionary<int, bool>();
        foreach (var mission in CampaignSequence.Load(zrdrPath))
        {
            wings[mission.Seq] = mission.Wingman;
        }

        if (!wings.TryGetValue(GrantSeq, out bool grantWing) || !wings.TryGetValue(ControlSeq, out bool controlWing))
        {
            throw new SuiteSkippedException($"cm_sequence carries no story position {GrantSeq} or {ControlSeq}");
        }

        ctx.Check(grantWing && controlWing,
            $"cm_sequence authors a wingman on both seq {GrantSeq} and seq {ControlSeq}, so the two slots can be told apart");
        if (!grantWing || !controlWing)
        {
            throw new SuiteSkippedException($"seq {GrantSeq} or {ControlSeq} authors no wingman row");
        }

        MissionReward? found = null;
        foreach (var reward in CampaignProgression.MissionRewards)
        {
            if (reward.Ordinal == GrantSeq + 1)
            {
                found = reward;
            }
        }

        if (found is not { } record || !record.AwardsAircraft)
        {
            throw new SuiteSkippedException($"the reward table awards no aircraft on mission {GrantSeq + 1}");
        }

        ctx.Same(0, record.ObjectiveBit,
            $"…and its reward-table entry is ungated, which is what makes the grant unconditional");
        report.AppendLine($"authored: seq {GrantSeq} wingman={grantWing}, seq {ControlSeq} wingman={controlWing}");
        report.AppendLine($"reward: mission {record.Ordinal} airframe {record.Airframe} '{record.Name}' gate {record.ObjectiveBit}");
        return record;
    }

    // Two owned planes, the profile the original seeds. The grant takes it to three, and uiData
    // 2018's own subtraction takes that straight back to two, which is under the floor.
    private static void BelowTheFloor(
        TestContext ctx, CampaignProfileStore store, UiStrings strings, string dataRoot,
        MissionReward reward, StringBuilder report)
    {
        var profile = Owning("Below", 2);
        var flow = Open(store, strings, dataRoot, profile, GrantSeq);
        ctx.Same(3, profile.Planes.Count, $"opening the flight check grants the mission's aircraft to a two-plane profile");
        CheckAward(ctx, profile, reward, "Below");
        ctx.Check(store.Load("Below") is { Planes.Count: 3 } saved && saved.SelectedPlane == profile.SelectedPlane,
            $"…and the grant reached the profile on disk, not only the seated copy");

        var slots = ChangePlaneSlots(flow);
        ctx.Same(0, slots.Count, $"neither slot offers CHANGE PLANE with {profile.Planes.Count} owned less one under the floor of {Floor} ({Show(slots)})");
        ctx.Check(!flow.Feature.ChangePlaneAllowed(PilotSlot) && !flow.Feature.ChangePlaneAllowed(WingmanSlot),
            $"…and the feature refuses both slots outright");
        report.AppendLine($"below: owned={profile.Planes.Count}, selected={profile.SelectedPlane}, slots={Show(slots)}");
    }

    // Three owned. The grant takes it to four, the subtraction to three, which is the floor itself,
    // so the wingman's button lives and only the mission's own bar is left on the pilot's.
    private static void AtTheFloor(
        TestContext ctx, CampaignProfileStore store, UiStrings strings, string dataRoot,
        MissionReward reward, StringBuilder report)
    {
        var profile = Owning("Floor", 3);
        var flow = Open(store, strings, dataRoot, profile, GrantSeq);
        ctx.Same(4, profile.Planes.Count, $"the same grant on a three-plane profile takes it to four");
        CheckAward(ctx, profile, reward, "Floor");

        var slots = ChangePlaneSlots(flow);
        ctx.Same(1, slots.Count, $"exactly one CHANGE PLANE is offered at the floor ({Show(slots)})");
        ctx.Check(slots.Count == 1 && slots[0] == WingmanSlot,
            $"…and it is the wingman's, the pilot's barred by the mission itself ({Show(slots)})");
        ctx.Check(!flow.Feature.ChangePlaneAllowed(PilotSlot) && flow.Feature.ChangePlaneAllowed(WingmanSlot),
            $"…which is the feature answering the two slots differently rather than once for both");
        report.AppendLine($"floor: owned={profile.Planes.Count}, selected={profile.SelectedPlane}, slots={Show(slots)}");
    }

    // The replay the report came from: the award is already on the profile, so nothing is granted a
    // second time and the entry only re-selects it, whatever the player last flew.
    private static void AlreadyGranted(
        TestContext ctx, CampaignProfileStore store, UiStrings strings, string dataRoot,
        MissionReward reward, StringBuilder report)
    {
        var profile = Owning("Replay", 3);
        profile.GrantedAircraft.Add(reward.Airframe);
        profile.Planes.Add(new OwnedPlane { Name = reward.Name, Airframe = reward.Airframe, Special = true });
        profile.SelectedPlane = 0;
        var flow = Open(store, strings, dataRoot, profile, GrantSeq);
        ctx.Same(4, profile.Planes.Count, $"a replay grants the award no second time");
        ctx.Same(1, profile.GrantedAircraft.Count, $"…and records it once");
        CheckAward(ctx, profile, reward, "Replay");

        var slots = ChangePlaneSlots(flow);
        ctx.Check(slots.Count == 1 && slots[0] == WingmanSlot,
            $"…with the wingman's button alone offered on the replay ({Show(slots)})");
        report.AppendLine($"replay: owned={profile.Planes.Count}, selected={profile.SelectedPlane}, slots={Show(slots)}");
    }

    // The able-to-fail control for both rules: the same four-plane shape on a mission the script
    // neither grants on nor discounts, where both buttons are offered and the selection is left be.
    private static void OnAnotherMission(
        TestContext ctx, CampaignProfileStore store, UiStrings strings, string dataRoot,
        MissionReward reward, StringBuilder report)
    {
        var profile = Owning("Control", 4);
        var flow = Open(store, strings, dataRoot, profile, ControlSeq);
        ctx.Same(4, profile.Planes.Count, $"the neighbouring mission grants nothing");
        ctx.Same(0, profile.SelectedPlane, $"…and leaves the pilot's own selection where it was");
        ctx.Check(!profile.GrantedAircraft.Contains(reward.Airframe),
            $"…having recorded no award for airframe {reward.Airframe}");

        var slots = ChangePlaneSlots(flow);
        ctx.Same(2, slots.Count, $"both slots offer CHANGE PLANE there ({Show(slots)})");
        ctx.Check(flow.Feature.ChangePlaneAllowed(PilotSlot) && flow.Feature.ChangePlaneAllowed(WingmanSlot),
            $"…the pilot's included, so the bar and the subtraction are both this mission's alone");
        report.AppendLine($"control: owned={profile.Planes.Count}, selected={profile.SelectedPlane}, slots={Show(slots)}");
    }

    private static void CheckAward(TestContext ctx, CampaignProfileDef profile, MissionReward reward, string who)
    {
        var plane = profile.SelectedPlane >= 0 && profile.SelectedPlane < profile.Planes.Count
            ? profile.Planes[profile.SelectedPlane]
            : null;
        ctx.Check(plane is { Special: true } && plane.Name == reward.Name && plane.Airframe == reward.Airframe,
            $"{who}: the pilot flies the granted '{reward.Name}' off airframe {reward.Airframe} ({plane?.Name}, airframe {plane?.Airframe})");
    }

    // A profile owning that many planes: the two the original seeds, then stand-ins of the same
    // starter airframe, since only the count is what uiData 2018 answers with.
    private static CampaignProfileDef Owning(string name, int count)
    {
        var profile = CampaignProfileDef.NewProfile(name);
        while (profile.Planes.Count < count)
        {
            var seed = profile.Planes[0];
            profile.Planes.Add(new OwnedPlane { Name = $"{name} {profile.Planes.Count}", Airframe = seed.Airframe });
        }

        return profile;
    }

    // A flow seated on that profile and standing on the mission's flight check, entered the way a
    // player reaches it, so the screen's own on-entry write is what the checks then read.
    private static CampaignFlow Open(
        CampaignProfileStore store, UiStrings strings, string dataRoot, CampaignProfileDef profile, int seq)
    {
        store.Save(profile);
        var flow = new CampaignFlow(store, strings, dataRoot);
        flow.SelectProfile(profile);
        flow.SetMission(seq);
        flow.GoTo(CampaignScreen.FlightCheck);
        return flow;
    }

    // Which crew slots the composed flight check offers a CHANGE PLANE row for, read off the rows'
    // own authored buttons rather than off their labels.
    private static List<int> ChangePlaneSlots(CampaignFlow flow)
    {
        var slots = new List<int>();
        var page = flow.Page;
        for (int row = 0; row < page.RowCount; row++)
        {
            if (page.Button(row) is { Button: BoardButton.ChangePlane } reference)
            {
                slots.Add(reference.Slot);
            }
        }

        return slots;
    }

    private static string Show(List<int> slots) =>
        slots.Count == 0 ? "none" : string.Join(",", slots);
}
