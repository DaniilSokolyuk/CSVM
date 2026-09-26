using System;
using System.Collections.Generic;
using System.Linq;
using CSVM.Mech3;

namespace CSVM.Flight.Airframe;

/// <summary>Where one definition's anchor lives, as the bind that stages it can see it. The rule is
/// uniform across the world-effects and the crash bind; only the lookup differs, which is why
/// <see cref="EffectCatalogue.StageRootsFor"/> takes it as a delegate.</summary>
public enum AnchorPlacement
{
    /// <summary>A parentless gamez template root: the bind must build a copy of it. A root left out
    /// leaves every def anchored on it unanchored, so it plays nothing at all.</summary>
    Stage,

    /// <summary>Already inside the bind's own scope, a child of a root it stages anyway
    /// (<c>ap_cracks</c> under <c>ap_effect</c>), the crash scaffold's <c>player</c>/<c>healthy</c>/
    /// <c>destroyed</c>/pieces, the plane's own parts. Satisfied; nothing to build.</summary>
    InScope,

    /// <summary>Resolves nowhere. The def would anchor on nothing and play silently, the failure
    /// this derivation exists to make loud.</summary>
    Missing,
}

/// <summary>The record of which authored anims are playable effects, and what their defs need
/// staged. Owns the name tables every effect producer must stay inside, <c>WorldEffectsFactory</c>
/// consumes these names to build and stage the runtime; the naming lives here, never there.
/// ⚠ Names cross every play seam (<c>GrazeEffectSink</c>, <c>ExternalEffect</c>,
/// <c>ProjectilePool.EffectSink</c>) as bare strings, never a typed entry, a typed entry would
/// thread this module's types through the deliberately engine-free <c>ImpactOutcome</c>. The
/// producer-range tripwires in <c>CSVM.Tests</c> close the drift a typo would otherwise open.</summary>
public static class EffectCatalogue
{
    // The crash def vector's prefix: slot i is "player_crash_" + SurfaceRegistry.Names[i], the
    // vector the original concatenates in FUN_00476250 and indexes with the struck material's
    // surface id. Only default/dirt/water ship a def, so the other eleven slots fall back to slot 0
    //, asked of the bound program by SurfaceDefTable rather than listed here, because "this slot
    // names no def" IS the original's mechanism.
    public const string CrashDefPrefix = "player_crash_";

    // The bare anim name the selection cascade falls to when the vector cannot answer at all (empty
    // vector, or a slot 0 naming no def), the crash defs' own anim root. Unreachable in this
    // install: all eight chapters' cam_anim carry player_crash_default, so slot 0 always resolves.
    public const string CrashAnimRoot = "player";

    // The AI aircraft family's vector prefix: slot i is "ai_crash_" + SurfaceRegistry.Names[i].
    // Same cascade as the player family (decode: analysis/surface-classification/FINDINGS.md),
    // swapped for player_crash_* only on the vehicle named "player", every AI aircraft
    // crashes through this one shared cascade.
    public const string AiCrashDefPrefix = "ai_crash_";

    // The ai_crash_* defs' authored NAME/anim-root: `kestrel`, the AI airframe they were written
    // against (the player family's counterpart is `player`). All 24 shipped defs (3 per chapter)
    // carry it. ⚠ Do not build a node for it. NAME equals ANIMATION_ROOT_NAME here, so the caller's
    // context node replaces both at play time (org/vehicleDamage.md, "Which airframe a stage's anim
    // binds to"). AirframeScopedAnchors is what drops it from the stage closure.
    public const string AiCrashAnimRoot = "kestrel";

    // The human player's DESTROY def, the anim FUN_00476250 resolves into the death slot for the
    // vehicle named `player`. Same NAME as the crash root, so it needs no anchor of its own.
    public const string PlayerDestroyAnim = "player";

    // The middle of damage_shakes.zrd.json's three `*_aishake` defs, the plane-rocking half of the
    // camera shake a person at the controls gets instead. FlightController plays it on an AI's
    // nitro engage, the one trigger of the three the executable's shake player is decoded on, and
    // CrashRigAnimNames binds it beside the nitro defs.
    // ⚠ Its authored NAME is `bloodhawk`, which resolves nothing in a per-plane rig, so it reaches
    // the aircraft through Play's PlaneModel fallback exactly as those do.
    public const string AiShakeAnim = "medium_aishake";

    // The graze family's vector prefix: slot i is "touchdown_" + SurfaceRegistry.Names[i].
    // ⚠ Unlike the crash family it has no bare last-resort anim: an unanswerable slot plays
    // nothing, so TouchdownDefTable passes a null lastResort. Built once per level (a global),
    // where the crash vector is per-plane, mirrored here against the world program.
    public const string TouchdownDefPrefix = "touchdown_";

    // The impact/destruction/graze effect animation names the world-effects runtime binds; the
    // closure of these is staged and playable via PlayEffectAt.
    // ⚠ `random_gun_impact` is excluded: its root is the generic `player` and would mis-anchor.
    public static readonly string[] EffectAnimNames =
    {
        // rocket / ordnance IMPACT (default + buildings), puffer-bearing and otherwise
        "large_fireball", "small_fireball", "he_ground_effect", "ap_ground_effect", "flak_effect",
        "flash_effect", "sonic_ground_effect", "scatter_effect", "torpedo_ground_effect",
        "rear_flash_effect", "torpedo_water_effect",
        // The seeker's default IMPACT row: an ON_CALL def anchored on a same-named gamez root.
        // Already in the torpedo effects' CALL closure; named here because a weapon row indexes it.
        "ballflare.flt",
        // gun IMPACT family, caliber (3040/5060/70) × ammo (slug/dum/ap/mag); see EffectSink
        "3040slug_gunhit", "3040ap_gunhit", "3040dum_gunhit", "3040mag_gunhit",
        "5060slug_gunhit", "5060ap_gunhit", "5060dum_gunhit", "5060mag_gunhit",
        "70slug_gunhit", "70ap_gunhit", "70dum_gunhit", "70mag_gunhit",
        // destruction effects death sequences call
        "large_30sec_fire", "great_balls_of_fire", "large_black_smokeball", "biggun_flying_parts",
        "big_splash",
        // Progressive damage-stage effects the DAMAGE_SEQUENCE stage pair calls (smoke/fire
        // sputter at 0.60/0.30 HP). C4's `b_steamtrail` is excluded: its anim root is the live
        // train subtree, not a relocatable template.
        "sputter_black_smoke_obj", "sputter_fire_smoke_obj",
        // The graze reaction's touchdown_* defs are NOT listed here. They are a surface-indexed
        // vector, so WorldEffectAnimNames appends whichever slots the bound program can play.
    };

    // The rocket impact's upper ring, the one callee `AnimRuntime.OrientedCallAnimNames` lets a
    // burst re-base. `he_ground_effect` calls it 12 m over the hit while its ground ring
    // (`call_he_ring`) stays on the surface, so only this one is free to face the incoming round.
    // ⚠ Enhanced Graphics only, and a remake-only rule: the original spawns it on a fixed axis
    // whatever the flight path (docs/org/ordnanceTypes.md).
    public static readonly string[] ImpactUpperRingAnimNames = { "call_he_ring1" };

    // The bailed pilot under his canopy. He is not a piece of the wreck, he is a man stepping out
    // of it, so he takes neither its momentum nor its attitude. The momentum half needs no list any
    // more: `chuteman` authors no `impact_force`, so the authored gate excludes him by data.
    // ⚠ Still wired into `AnimRuntime.LevelPlacedTemplateNames` for the attitude half, because his
    // template is authored at identity and only our staging hangs it under the crash root.
    public static readonly string[] BailoutAnimNames = { "chuteman" };

    // The crash def's sub-effects meant to lie flat on the struck surface rather than co-rotate
    // with the plane's impact attitude, the only crash-rig templates
    // `AnimRuntime.LevelPlacedTemplateNames` levels to world axes.
    // ⚠ The fireball/smoke/debris family and `large_steam_spray` are deliberately excluded, see
    // `AnimRuntime.LevelPlacedTemplateNames`'s own ⚠ for why those keep the crash attitude.
    public static readonly string[] CrashSurfaceLevelAnimNames =
        { "plane_big_splash", "plane_big_ripple", "hg_splasher", "flydirt_plane" };

    // The per-player rig's non-crash defs: the four `<part>_damage_effects` shims the
    // Devastator's 0.99 injure_anims entry names. Bound alongside the crash def because they need
    // exactly what the crash rig already has, the `player` anim root, the plane's own `pdpN`
    // panels as INPUT_NODEs, and a live puffer factory. Each is a one-event shim calling
    // `random_gun_impact`, which the closure pulls in with `yellow_sparks_follow` under it.
    public static readonly string[] PlaneDamageEffectAnims =
        { "nose_damage_effects", "tail_damage_effects", "leftwing_damage_effects", "rightwing_damage_effects" };

    // The engine start/stop choreography (plane_props.zrd.json): the static blade prop cross-fades
    // to its spinning blur disc (with the startup smokepuffN burst) and the reverse on shutdown.
    // Bound alongside the crash def for the same live puffer factory; unlike the crash/damage defs
    // above, FlightController plays these directly (spawn/engine-death), never through a CALL.
    // `spinprops` is the silent, instant restart the original's own bit-2 falling edge runs
    // (docs/org/ordnanceTypes.md); `startprops` is named by no airframe def in the original's data.
    public static readonly string[] PropChoreographyAnims = { "startprops", "stopprops", "spinprops" };

    // The nitro boost's two defs (plane_props.zrd.json): the nitroprop discs cross-fade in over
    // the spinning props with the exhaust puffers and snd_nitrostart, and the decay reverses it.
    // FlightController plays them off its NitroSystem's edges, never through a CALL; the
    // ai_nitro_* wrappers in the same file are retargeting shims the executable never references.
    public static readonly string[] NitroAnims = { "nitro_boost", "nitro_decay" };

    // The canopy glass's five hole defs (cockpit_bulletholes.zrd), one per CanopyHoleCue slot.
    // Human rigs only: the cue is the pilot's own windshield, and the bulNx quads it lights live in
    // the cockpit1 interior an AI plane is never built with. Decode: docs/org/weaponFire.md.
    // ⚠ Their authored NAME is `player_pfighter`, already an AirframeScopedAnchors entry, so the
    // closure drops it and each def reaches the aircraft through Play's PlaneModel fallback. What it
    // does pull in is the exterior overlay family, a parentless root in every chapter gamez.
    public static readonly string[] CanopyHoleAnims =
        { "bullet1", "bullet2", "bullet3", "bullet4", "bullet5" };

    // The authored player damage-stage menu: the per-panel burn, the fuel-vapor leak, and the
    // heavy prop1 trail. DamageVisuals plays these as the vehicle.zrd.json injure_anims thresholds
    // cross, the tier table is authored, nothing here invents one.
    // `player_damage_trail` maps to the data's 0.10 `player_smoketrail` entry, see
    // DamageVisuals.RigAnimFor.
    public static readonly string[] PlayerDamageStageAnims =
    {
        "pdpanel1", "pdpanel2", "pdpanel3", "pdpanel4", "pdpanel5", "pdpanel6", "pdpanel7",
        "pdpanel8", "player_fuelleak", "player_damage_trail",
    };

    // The authored AI damage-stage menu, which the eleven AI airframes' injure_anims ladders name:
    // the prop1 smoke/fire trail and the five-step random fireball cascade. One flat list, correct
    // for every airframe, because both defs retarget onto whichever plane stages them.
    // ⚠ Extend this list, never DamageVisuals.RigAnimFor's walk, and never merge it into the player
    // menu: an AI ladder names different anims. A program-existence rule instead of a curated list
    // would stage the cockpit gauge defs (nose_damage_green, *_got_hit) on the airframe.
    public static readonly string[] AiDamageStageAnims = { "pfsmoketrail", "random_remote_damage" };

    // Both damage-stage menus: what the crash rig binds, and what a whole-menu stop closure is
    // derived over. DamageVisuals.RigAnimFor tests membership over this plus PlaneDamageEffectAnims.
    public static readonly string[] DamageStageAnims =
        PlayerDamageStageAnims.Concat(AiDamageStageAnims).ToArray();

    // Anchors the closure below reports that no bind stages, because the CALL reaching the
    // definition supplies its anchor instead of its own NAME: `zep_can_dstry1.flt` (absent from
    // C2's gamez entirely) and `warhawk` (startprops/stopprops' own NAME, a shared authoring
    // label no real airframe carries). Curation.
    // ⚠ Do not park an anchor here that a bind could stage; the entry means "nothing stages this",
    // and a staged root listed here is dropped from the closure and draws nothing.
    public static readonly string[] CallSuppliedAnchors = { "zep_can_dstry1.flt", "warhawk" };

    // The DESTROY def each of the eleven airframes ships under its own name, keyed by the plane
    // model node a rig is built from. Slot one of the two on the death path
    // (org/vehicleDamage.md, "What happens to the wreck"), started when health reaches zero.
    // ⚠ Curated, and never derived by stripping `player_`: the original keys the slot on the AI
    // vehicle def's own `nodename` (`devastator` authors `piratefighter`), which diverges from the
    // player node on three airframes. Extend the map; the resolver below stays mechanical.
    public static readonly IReadOnlyDictionary<string, string> AirframeDestroyAnims =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["player_autogyro"] = "autogyro",
            ["player_avenger"] = "avenger",
            ["player_balmoral"] = "balmoral",
            ["player_bhawk"] = "bloodhawk",
            ["player_brigand"] = "brigand",
            ["player_fbrand"] = "firebrand",
            ["player_fury"] = "fury",
            ["player_kestrel"] = "kestrel",
            ["player_peacemaker"] = "peacemaker",
            ["player_pfighter"] = "piratefighter",
            ["player_warhawk"] = "warhawk",
        };

    // Airframe model-root names authored as anchors, none of which any chapter gamez ships: the
    // eleven `player_*` roots, `piratefighter` (pfsmoketrail), `kestrel` (ai_crash_*) and the
    // eleven destroy defs' own names. The anchor is whichever airframe stages the def
    // (org/vehicleDamage.md), so none is a template.
    // ⚠ Also the crash stage's place-exempt set (WorldEffectsFactory.NewCrashTemplateStage): where
    // such a name does resolve it is the aircraft, and a relocating CALL would TopLevel-pin it.
    public static readonly string[] AirframeScopedAnchors =
        new[] { "piratefighter", AiCrashAnimRoot }
            .Concat(AirframeDestroyAnims.Keys)
            .Concat(AirframeDestroyAnims.Values)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    // The crash rig's own anim-root scaffold name: `player`, the crash root every rig builds and
    // the `player_crash_*` family's authored NAME.
    // ⚠ A relocating CALL must never place it like a template: TopLevel-pinning the crash root
    // drags the wreck and every pooled template copy to the first crash's site.
    public static readonly string[] CrashScaffoldAnchors = { "player" };

    // Template roots a bound def SWITCHES ON without anchoring on: `cpilot`, the articulated
    // bailing pilot `player-player`'s `cpeject1`/`cpeject2` show, seat and drive with SI scripts.
    // Both defs carry the rig's own `player` as their NAME, so the anchor closure below never sees
    // `cpilot`, and the eject would run on a name resolving nowhere.
    // ⚠ Never list a def's own NAME here; an anchor belongs in the closure. A name here is staged
    // only where a bound def really names it and the gamez pair carries it as a root.
    public static readonly string[] CrashActivatedRoots = { "cpilot" };

    /// <summary>The crash-def vector this program can play, built over the whole surface registry
    /// with <see cref="CrashDefPrefix"/>, what <c>BuildFlightCrashRuntime</c> binds and what
    /// <c>FlightController.Crash</c> indexes with the struck material's surface id. A slot whose
    /// def this install does not ship is empty and falls back to slot 0, which is the original's
    /// own mechanism rather than a list of exceptions (see <see cref="SurfaceDefTable"/>).</summary>
    public static SurfaceDefTable CrashDefTable(AnimProgram program) =>
        new(CrashDefPrefix, CrashAnimRoot, DefExistsIn(program));

    /// <summary>The AI aircraft counterpart of <see cref="CrashDefTable"/>: the
    /// <c>ai_crash_*</c> vector, selected by the same cascade as the player family (decode:
    /// analysis/surface-classification/FINDINGS.md). Same fallback arms, including the bare last
    /// resort, which the original sets to the vehicle's own name, so the caller passes the
    /// plane's own name. Unreachable in this install: all eight chapters ship
    /// <c>ai_crash_default</c>, so slot 0 always resolves.</summary>
    public static SurfaceDefTable AiCrashDefTable(AnimProgram program, string planeName) =>
        new(AiCrashDefPrefix, planeName, DefExistsIn(program));

    /// <summary>The family a controller's crash indexes: the original keys it on the vehicle,
    /// <c>FUN_00476250</c> swaps in <c>player_crash_*</c> only on the vehicle named
    /// <c>player</c>; every other vehicle keeps the <c>ai_crash_*</c> vector its params carried.
    /// Ours keys the same split on who is at the controls.</summary>
    public static SurfaceDefTable CrashDefTableFor(AnimProgram program, bool humanPiloted,
        string planeName) =>
        humanPiloted ? CrashDefTable(program) : AiCrashDefTable(program, planeName);

    /// <summary>The DESTROY def this rig plays when its hull reaches zero: the fixed
    /// <see cref="PlayerDestroyAnim"/> for a human rig, the airframe's own self-named def
    /// otherwise (<see cref="AirframeDestroyAnims"/>). Null for a plane node the map does not
    /// carry, which leaves that rig with no destroy anim rather than a guessed one.</summary>
    public static string? DestroyAnimFor(bool humanPiloted, string planeNodeName) =>
        humanPiloted ? PlayerDestroyAnim
            : AirframeDestroyAnims.TryGetValue(planeNodeName, out var name) ? name : null;

    /// <summary>Whether <paramref name="destroyAnim"/> flies the hull itself, it authors an
    /// <c>ObjectMotion</c> on the <c>MAIN_ROOT_NODE</c> sentinel, which takes the wreck over and
    /// carries its own <c>bounce_sequence</c> landing. The eleven airframe defs do; <c>player</c>
    /// does not, and its hull falls under the flight model to a <c>player_crash_*</c> instead.
    /// Asked of the data, so neither landing is wired twice.</summary>
    public static bool FliesOwnHull(AnimProgram program, string? destroyAnim)
    {
        if (string.IsNullOrEmpty(destroyAnim))
            return false;
        foreach (var def in program.ByAnimName(destroyAnim))
            foreach (var seq in def.Sequences)
                foreach (var ev in seq.Events)
                    if (ev.Kind == "ObjectMotion"
                        && string.Equals(ev.Data.Str("node"), "MAIN_ROOT_NODE",
                            StringComparison.OrdinalIgnoreCase))
                        return true;
        return false;
    }

    /// <summary>Everything the per-player crash rig binds, every playable crash-vector slot (the
    /// struck surface is only known at impact, so the whole vector is bound), the four damage
    /// shims, the prop choreography, both authored damage-stage menus and this rig's destroy def,
    /// i.e. every def that plays ON one aircraft, and therefore the name set whose anchor-root
    /// closure that rig's own template stage must satisfy (<see cref="CrashStageRoots"/>). Both
    /// menus regardless of who flies; <paramref name="humanPiloted"/> adds the canopy holes.</summary>
    public static IReadOnlyList<string> CrashRigAnimNames(SurfaceDefTable crashDefs,
        string? destroyAnim = null, bool humanPiloted = false)
    {
        var names = new List<string>(crashDefs.PlayableDefs);
        names.AddRange(PlaneDamageEffectAnims);
        names.AddRange(PropChoreographyAnims);
        names.AddRange(NitroAnims);
        names.Add(AiShakeAnim);
        names.AddRange(DamageStageAnims);
        if (humanPiloted)
            names.AddRange(CanopyHoleAnims);
        if (!string.IsNullOrEmpty(destroyAnim))
            names.Add(destroyAnim);
        return names;
    }

    /// <summary>The graze family's def vector, which <c>FlightController.GrazeReaction</c> indexes
    /// with the scraped material's surface id exactly as the crash does. Built against the world
    /// program, because that vector is built once at level init rather than per plane.
    /// ⚠ No last resort: where the crash cascade ends at a bare anim name, this one ends at
    /// "play nothing". <see cref="SurfaceDefTable"/>'s remarks carry the address.</summary>
    public static SurfaceDefTable TouchdownDefTable(AnimProgram program) =>
        new(TouchdownDefPrefix, lastResort: null, DefExistsIn(program));

    /// <summary>The surface id a struck body of each registry id actually resolves to on contact:
    /// itself where a touch cascade ships a def of its own, else slot 0 (see
    /// <see cref="SurfaceDefTable"/>). Both crash and touchdown families are asked, because either
    /// shipping a def for an id makes it resolve as itself; the weapon impact table is not asked,
    /// since an id it authors no row for plays nothing rather than falling back to row 0.
    /// Written for the collider overlay's colour key.</summary>
    public static IReadOnlyList<int> ResolvedSurfaceIds(Func<string, bool> defExists)
    {
        var crash = new SurfaceDefTable(CrashDefPrefix, CrashAnimRoot, defExists);
        var touchdown = new SurfaceDefTable(TouchdownDefPrefix, lastResort: null, defExists);
        var resolved = new int[SurfaceRegistry.Names.Count];
        for (int id = 0; id < resolved.Length; id++)
        {
            // The cascade itself answers "is this slot occupied": the registry names are pairwise
            // distinct, so it returns prefix + this id's own name only when the slot is filled.
            string name = SurfaceRegistry.Names[id];
            bool ownDef = crash.DefForSurfaceId(id) == CrashDefPrefix + name
                          || touchdown.DefForSurfaceId(id) == TouchdownDefPrefix + name;
            resolved[id] = ownDef ? id : SurfaceRegistry.Default;
        }
        return resolved;
    }

    /// <inheritdoc cref="ResolvedSurfaceIds(Func{string, bool})"/>
    public static IReadOnlyList<int> ResolvedSurfaceIds(AnimProgram program) =>
        ResolvedSurfaceIds(DefExistsIn(program));

    /// <summary>Every effect animation the world-effects runtime binds: the fixed
    /// <see cref="EffectAnimNames"/> plus whichever <see cref="TouchdownDefTable"/> slots this
    /// program can actually play. One call serves the bind, the stage closure and the
    /// <c>--effects-test</c>/<c>effects-census</c> sweeps, so a graze def can never be playable but
    /// unstaged, or swept but unbound.</summary>
    public static IReadOnlyList<string> WorldEffectAnimNames(AnimProgram program)
    {
        var names = new List<string>(EffectAnimNames);
        names.AddRange(TouchdownDefTable(program).PlayableDefs);
        return names;
    }

    /// <summary>What the world-effects bind stages: the anchor-root closure of
    /// <see cref="WorldEffectAnimNames"/> against the bound world program. This IS the stage's
    /// source, <c>WorldEffectsFactory</c> builds a copy of every name it returns, per pool slot.</summary>
    public static IReadOnlyList<string> WorldStageRoots(AnimProgram program,
        Func<string, AnchorPlacement> resolveRoot) =>
        StageRootsFor(program, WorldEffectAnimNames(program), resolveRoot);

    /// <summary>The same for the per-plane crash rig: the closure of
    /// <see cref="CrashRigAnimNames"/> against that rig's own scope, plus whichever
    /// <see cref="CrashActivatedRoots"/> that closure reaches. <paramref name="resolveRoot"/> is
    /// scoped to the bound controller, so a name its plane/wreck already carries needs no
    /// template; <paramref name="crashDefs"/> is the rig's own family (player or AI), and the
    /// no-table overload keeps the player family for callers that predate the split.</summary>
    public static IReadOnlyList<string> CrashStageRoots(AnimProgram program,
        Func<string, AnchorPlacement> resolveRoot, SurfaceDefTable crashDefs,
        string? destroyAnim = null, bool humanPiloted = false)
    {
        var names = CrashRigAnimNames(crashDefs, destroyAnim, humanPiloted);
        var roots = new List<string>(StageRootsFor(program, names, resolveRoot));
        foreach (var activated in ActivatedRootsIn(program, names, resolveRoot))
            if (!roots.Contains(activated, StringComparer.OrdinalIgnoreCase))
                roots.Add(activated);
        roots.Sort(StringComparer.OrdinalIgnoreCase);
        return roots;
    }

    /// <inheritdoc cref="CrashStageRoots(AnimProgram, Func{string, AnchorPlacement}, SurfaceDefTable)"/>
    public static IReadOnlyList<string> CrashStageRoots(AnimProgram program,
        Func<string, AnchorPlacement> resolveRoot, bool humanPiloted = false) =>
        CrashStageRoots(program, resolveRoot, CrashDefTable(program), destroyAnim: null, humanPiloted);

    /// <summary>The anchor-root closure of <paramref name="names"/>: walks the transitive
    /// CALL_ANIMATION closure, takes each definition's NAME, and asks
    /// <paramref name="resolveRoot"/> where it lives, shared by the world-effects and crash
    /// binds with different scopes. Anchors in <see cref="CallSuppliedAnchors"/>/
    /// <see cref="AirframeScopedAnchors"/> are dropped first. Sorted, so staging order is stable.</summary>
    /// <exception cref="EffectAnchorException">an anchor resolves nowhere.</exception>
    public static IReadOnlyList<string> StageRootsFor(AnimProgram program, IEnumerable<string> names,
        Func<string, AnchorPlacement> resolveRoot)
    {
        // anchor → the animation names anchored on it, so a failure can name the definition and not
        // just the missing node.
        var anchors = new SortedDictionary<string, SortedSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var def in program.Subset(names).Defs)
        {
            if (string.IsNullOrEmpty(def.Name) || SuppliedElsewhere(def.Name))
                continue;
            if (!anchors.TryGetValue(def.Name, out var animNames))
                anchors[def.Name] = animNames = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            animNames.Add(def.AnimName ?? def.Name);
        }

        var staged = new List<string>();
        var missing = new List<string>();
        foreach (var (anchor, animNames) in anchors)
        {
            switch (resolveRoot(anchor))
            {
                case AnchorPlacement.Stage:
                    staged.Add(anchor);
                    break;
                case AnchorPlacement.InScope:
                    break;
                default:
                    missing.Add($"'{anchor}' (anchors {string.Join("/", animNames)})");
                    break;
            }
        }
        if (missing.Count > 0)
            throw new EffectAnchorException(missing);
        return staged;
    }

    // Which CrashActivatedRoots a closure really reaches: a candidate an OBJECT_ACTIVE_STATE in one
    // of its definitions switches, that this bind can stage. An AI rig binds no cpeject def and so
    // stages no bailing pilot.
    private static IEnumerable<string> ActivatedRootsIn(AnimProgram program,
        IEnumerable<string> names, Func<string, AnchorPlacement> resolveRoot)
    {
        var switched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var def in program.Subset(names).Defs)
            foreach (var seq in def.Sequences)
                foreach (var ev in seq.Events)
                    if (ev.Kind == "ObjectActiveState" && ev.Data.Str("node") is { Length: > 0 } node)
                        switched.Add(node);
        foreach (var candidate in CrashActivatedRoots)
            if (switched.Contains(candidate) && resolveRoot(candidate) == AnchorPlacement.Stage)
                yield return candidate;
    }

    // "This program defines that anim", the one existence test all three surface vectors are built
    // on, so a def the runtime could not play can never count as a filled slot in one of them and
    // not the others.
    private static Func<string, bool> DefExistsIn(AnimProgram program) =>
        name => program.ByAnimName(name).Count > 0;

    private static bool SuppliedElsewhere(string anchor)
    {
        foreach (var name in CallSuppliedAnchors)
            if (string.Equals(name, anchor, StringComparison.OrdinalIgnoreCase))
                return true;
        foreach (var name in AirframeScopedAnchors)
            if (string.Equals(name, anchor, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }
}

/// <summary>An effect definition's anchor resolves nowhere in the bind asked to stage it. Carries
/// the anchor names with the animations that wanted them, because "which def" is the half a bare
/// node name cannot answer.</summary>
public sealed class EffectAnchorException : Exception
{
    public EffectAnchorException(IReadOnlyList<string> anchors)
        : base($"effect anchor(s) resolve nowhere: {string.Join("; ", anchors)}")
    {
        Anchors = anchors;
    }

    public EffectAnchorException()
        : this(Array.Empty<string>())
    {
    }

    public EffectAnchorException(string message)
        : base(message)
    {
        Anchors = Array.Empty<string>();
    }

    public EffectAnchorException(string message, Exception inner)
        : base(message, inner)
    {
        Anchors = Array.Empty<string>();
    }

    /// <summary>One entry per unresolvable anchor, each naming the animations anchored on it.</summary>
    public IReadOnlyList<string> Anchors { get; }
}
