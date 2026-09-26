using System;
using System.Collections.Generic;
using System.Text;
using CSVM.Mech3;
using Godot;

namespace CSVM.Flight.Weapons;

/// <summary>
/// The cheap, world-less "do all 48 weapons mount and fire without throwing" pass check behind
/// <c>--weapon-test</c> and the <c>weapons-fire</c> in-engine suite: a one-shot harness over a
/// parked plane that spawns straight into a caller-supplied <see cref="ProjectilePool"/> and
/// needs no world, no colliders and no frame.
/// ⚠ Deliberately not part of the weapon lab, which fires nothing of its own; do not fold this
/// back in. Hand it <see cref="Loadout.ForRig"/>'s loadout so every mount class is covered.
/// Decode + measured counts: this module's entry in docs/architecture.md.</summary>
public static class WeaponBench
{
    /// <summary>Mounts and fires every weapon in the catalogue once per mount of its class,
    /// catching any that throw, and returns the report plus the counts a suite asserts on.</summary>
    public static Result Run(Node3D plane, Loadout loadout, WeaponDefs weapons, ProjectilePool pool)
    {
        var gunMounts = new List<Mount>();
        var pylonMounts = new List<Mount>();
        foreach (var g in loadout.FirableGuns)
        {
            gunMounts.Add(new Mount($"g{g.Slot} {g.Mount}", g.Muzzles));
        }
        foreach (var h in loadout.Hardpoints)
        {
            pylonMounts.Add(new Mount($"pylon{h.Index}", new[] { h.Pylon }));
        }

        string model = loadout.Def.Model.Length > 0 ? loadout.Def.Model : plane.Name.ToString();
        var all = new List<WeaponDef>(weapons.All);
        int gunCount = 0;
        foreach (var w in all)
        {
            if (w.IsGun)
            {
                gunCount++;
            }
        }

        var sb = new StringBuilder();
        sb.AppendLine($"weapon-test: plane {model}, {all.Count} weapons "
                      + $"({gunCount} gun, {all.Count - gunCount} hardpoint); "
                      + $"gun groups [{Labels(gunMounts)}], pylons [{Labels(pylonMounts)}]");
        int ok = 0, err = 0, skip = 0;
        foreach (var w in all)
        {
            // A hardpoint weapon fires from a pylon, a gun from a gun group, and from NO other
            // bank: a cross-bank fallback would turn "this plane has no mount for it" into a pass.
            var mounts = w.IsGun ? gunMounts : pylonMounts;
            string kind = w.IsRocket ? "rocket" : w.IsGun ? "gun" : "other";
            if (mounts.Count == 0)
            {
                skip++;
                sb.AppendLine($"  {w.Id,-7} {Trim(w.Name, 10),-10} {kind,-6} SKIP, no "
                              + (w.IsGun ? "gun group" : "pylon") + " on this plane");
                continue;
            }
            try
            {
                int rounds = 0;
                foreach (var mount in mounts)
                {
                    foreach (var n in mount.Nodes)
                    {
                        pool.Spawn(w, n.GlobalTransform, Vector3.Zero);
                        rounds++;
                    }
                }
                ok++;
                sb.AppendLine($"  {w.Id,-7} {Trim(w.Name, 10),-10} {kind,-6} → {mounts.Count,2} mount(s) fired {rounds,3} round(s) OK");
            }
            catch (Exception e)
            {
                err++;
                sb.AppendLine($"  {w.Id,-7} {Trim(w.Name, 10),-10} ERROR: {e.Message}");
            }
        }
        sb.AppendLine($"weapon-test: {ok}/{all.Count} fired OK, {err} error(s), {skip} skipped");
        return new Result
        {
            Report = sb.ToString(),
            Total = all.Count,
            Ok = ok,
            Errors = err,
            Skipped = skip,
            GunMounts = gunMounts.Count,
            PylonMounts = pylonMounts.Count,
        };
    }

    private static string Trim(string s, int n) => s.Length <= n ? s : s[..n];

    private static string Labels(List<Mount> mounts)
    {
        var sb = new StringBuilder();
        foreach (var m in mounts)
        {
            if (sb.Length > 0)
            {
                sb.Append(", ");
            }
            sb.Append(m.Label);
        }
        return sb.Length > 0 ? sb.ToString() : "none";
    }

    /// <summary>The bench's verdict: the report text plus the counts a suite asserts on.
    /// <see cref="Skipped"/> is called out because it is a success-looking outcome, a weapon with
    /// no mount of its class on this plane never fires and nothing else would notice.</summary>
    public sealed class Result
    {
        public required string Report { get; init; }
        public required int Total { get; init; }
        public required int Ok { get; init; }
        public required int Errors { get; init; }
        public required int Skipped { get; init; }

        /// <summary>How many mounts of each class the bench actually fired from, the coverage the
        /// 48/48 line does NOT show, since one mount is enough to make every weapon pass.
        /// Suites pin this at <c>ForRig</c>'s 4, so shrunk coverage cannot hide behind an
        /// unchanged 48/48.</summary>
        public required int GunMounts { get; init; }

        public required int PylonMounts { get; init; }
    }

    // One place on the airframe the bench fires from: a firable gun group's muzzles or a
    // single pylon.
    private sealed class Mount
    {
        public Mount(string label, IReadOnlyList<Node3D> nodes)
        {
            Label = label;
            Nodes = nodes;
        }

        public string Label { get; }
        public IReadOnlyList<Node3D> Nodes { get; }
    }
}
