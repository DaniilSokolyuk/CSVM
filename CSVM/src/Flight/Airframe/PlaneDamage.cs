using System.Collections.Generic;
using System.Linq;
using Godot;

namespace CSVM.Flight.Airframe;

/// <summary>
/// The vehicle damage ledger for a flying aircraft: the per-part pools from the def's
/// <c>destroyable_parts</c> (see <see cref="DestroyablePart"/>) plus an independent whole-vehicle
/// (armor, health) pair, ported instruction-for-instruction from the decoded take-hit wrapper.
/// Decode: docs/org/vehicleDamage.md. <see cref="Apply"/> spends one hit through that flow;
/// <see cref="IsDestroyed"/> is the decoded death test. FlightController applies severity-scaled
/// damage on survivable collisions and crashes outright on hard ones. Respawn calls
/// <see cref="Reset"/>.
/// </summary>
public sealed class PlaneDamage
{
    // FUN_004b3b60 considers at most the first three surviving parts before rand() picks one.
    private const int RedirectCandidates = 3;
    private const uint RngSeed = 0x2545F491u;

    private readonly Dictionary<string, PartState> _parts = new(System.StringComparer.OrdinalIgnoreCase);
    private readonly List<PartState> _order = new(); // def order, the resolver walks it
    private float _wholeArmor;
    private float _wholeHealth;
    private uint _rng = RngSeed;

    public PlaneDamage(IEnumerable<DestroyablePart> defs,
        float? wholeArmorMax = null, float? wholeHealthMax = null)
    {
        foreach (var def in defs)
        {
            var state = new PartState { Def = def, Hp = def.MaxHp, Armor = def.MaxArmor };
            _parts[def.Name] = state;
            _order.Add(state);
        }

        WholeArmorMax = wholeArmorMax ?? _order.Sum(p => p.Def.MaxArmor);
        WholeHealthMax = wholeHealthMax ?? _order.Sum(p => p.Def.MaxHp);
        _wholeArmor = WholeArmorMax;
        _wholeHealth = WholeHealthMax;
    }

    public IReadOnlyDictionary<string, PartState> Parts => _parts;

    /// <summary>The whole-vehicle maxima: the def's authored pair, or the sum over parts.</summary>
    public float WholeArmorMax { get; }

    public float WholeHealthMax { get; }

    /// <summary>The whole-vehicle current pools, the pair the decoded death test reads.</summary>
    public float WholeArmor => _wholeArmor;

    public float WholeHealth => _wholeHealth;

    /// <summary>Worst (lowest) combined armor+HP fraction across all parts, 1f (pristine) when
    /// there are no parts or none has taken damage. This is the scale the def's injure_anims
    /// thresholds are on. ⚠ A zone-less AI aircraft (<see cref="PlaneStats.LoadForAi"/>) always
    /// reads a constant 1f here. <see cref="WorstHealthFraction"/> is the reading that works on
    /// both flavours, and is the one the decoded damage-state test takes.</summary>
    public float WorstFraction => _parts.Count == 0 ? 1f : _parts.Values.Min(p => p.Fraction);

    /// <summary>The reading the decoded damage-state test takes: the lowest HEALTH-only fraction
    /// across the zones, or the whole-vehicle health fraction on an airframe that resolves none.
    /// Armor is deliberately absent: the original divides part health current by part health max
    /// and never touches the armor pool here (docs/org/vehicleDamage.md, "The damaged state").</summary>
    public float WorstHealthFraction => _parts.Count == 0
        ? SummaryHealthFraction
        : _parts.Values.Min(p => p.HealthFraction);

    /// <summary>The whole-vehicle health fraction, the real pair's current over max, no longer
    /// a parts-derived stand-in. Fed to the DI voice thresholds and any "how dead am I" reader;
    /// the decoded death test is this pool reaching zero.</summary>
    public float SummaryHealthFraction =>
        WholeHealthMax > 0f ? _wholeHealth / WholeHealthMax : 1f;

    /// <summary>The decoded kill rule (FUN_004b9bc0):
    /// whole-vehicle health current at or below zero. The <c>critical</c> flag stays parsed and
    /// is never consulted, no code on the decoded death path reads a part flag.</summary>
    public bool IsDestroyed => WholeHealthMax > 0f && _wholeHealth <= 0f;

    /// <summary>Seeds the ledger from a plane's stats: authored whole pair where the def chain
    /// carries one, sum-over-parts otherwise.</summary>
    public static PlaneDamage For(PlaneStats stats) =>
        new(stats.DestroyableParts, stats.VehicleArmor, stats.VehicleHealth);

    /// <summary>Maps the struck collider box (fuselage/wing/canard/tail, or the backstop ray's
    /// "center") + the impact point in the PLANE's local frame to the data's part name: wings
    /// split by side (x &lt; 0 = left, verified against the planes.zbd node boxes), the fuselage
    /// fore/aft between nose and tail. ⚠ The <c>tail</c> arm ignores <paramref name="localImpact"/>
    ///, only correct because <see cref="PlaneCollider"/> clips its tail region to the wing band, so
    /// no outboard box reaches here. Do not "fix" tail sidedness here.</summary>
    public static string MapStruckPart(string colliderPart, Vector3 localImpact) => colliderPart switch
    {
        "wing" or "canard" => localImpact.X < 0f ? "leftwing" : "rightwing",
        "tail" => "tail",
        _ => localImpact.Z < 0f ? "nose" : "tail", // fuselage / center backstop
    };

    public void Reset()
    {
        foreach (var p in _parts.Values)
        {
            p.Hp = p.Def.MaxHp;
            p.Armor = p.Def.MaxArmor;
        }

        _wholeArmor = WholeArmorMax;
        _wholeHealth = WholeHealthMax;
        _rng = RngSeed; // a respawned plane redirects identically, suite determinism
    }

    /// <summary>Spends one hit's <c>HEALTH_DAMAGE</c>/<c>ARMOR_DAMAGE</c> through the decoded
    /// take-hit flow: the named zone armor-first, a dead or unknown zone redirected to a random
    /// surviving one, the leftover draining the whole pair directly. Returns the zone actually
    /// struck (null when zone-less), test <see cref="IsDestroyed"/> regardless. To pre-set a zone
    /// without draining the whole pool (test scaffolding), spend exact amounts: strip its armor,
    /// then a bare-zone health spend, an overkill call still kills.</summary>
    public PartState? Apply(string partName, float healthDamage, float armorDamage)
    {
        if (healthDamage <= 0f && armorDamage <= 0f)
            return _parts.TryGetValue(partName, out var known) ? known : null;

        float dmgA = armorDamage;
        float dmgH = healthDamage;
        var struck = ResolveStruckPart(partName);
        if (struck != null)
        {
            Spend(ref dmgA, ref dmgH, ref struck.Armor, ref struck.Hp);
            RecomputeWhole();
        }
        else
        {
            Spend(ref dmgA, ref dmgH, ref _wholeArmor, ref _wholeHealth);
        }

        // The wrapper loop (FUN_004b9b30): while BOTH leftovers remain and the vehicle lives,
        // the pair re-enters zone-less and spends against the whole pools, no recompute, so
        // these dents sit outside the parts until a later part spend overwrites them.
        while (dmgA > 0f && dmgH > 0f && _wholeHealth > 0f)
            Spend(ref dmgA, ref dmgH, ref _wholeArmor, ref _wholeHealth);
        return struck;
    }

    /// <summary>Spends a single damage magnitude, a collision, which the data gives equal
    /// armor and health ranges (player.json's 'crash' block).</summary>
    public PartState? Apply(string partName, float damage) => Apply(partName, damage, damage);

    /// <summary>Scales every zone's CURRENT pools and recomputes the whole pair off them: the
    /// airframe swap's damage carry-over, where the fractions come from the aircraft the capture
    /// animation belongs to (FUN_0047bf70, per section). The maxima are untouched, so the new
    /// airframe keeps its own and only what is left of them moves.</summary>
    public void ScalePools(float armorFraction, float healthFraction)
    {
        if (_order.Count == 0)
        {
            _wholeArmor = Mathf.Clamp(_wholeArmor * armorFraction, 0f, WholeArmorMax);
            _wholeHealth = Mathf.Clamp(_wholeHealth * healthFraction, 0f, WholeHealthMax);
            return;
        }

        foreach (var p in _order)
        {
            p.Armor = Mathf.Clamp(p.Armor * armorFraction, 0f, p.Def.MaxArmor);
            p.Hp = Mathf.Clamp(p.Hp * healthFraction, 0f, p.Def.MaxHp);
        }

        RecomputeWhole();
    }

    /// <summary>Writes the whole-vehicle pools directly and leaves the zones alone: what the
    /// airframe swap hands the outgoing aeroplane's new pilot, which the original writes as the
    /// two sums it measured off the hull the player is leaving.</summary>
    public void SetWholePools(float armor, float health)
    {
        _wholeArmor = Mathf.Clamp(armor, 0f, WholeArmorMax);
        _wholeHealth = Mathf.Clamp(health, 0f, WholeHealthMax);
    }

    /// <summary>"hull a81% h90% · nose a0% h85% · …", the whole-vehicle pair first (the pool
    /// the kill reads, kept visible in flight), then parts below full, armor pool then health
    /// pool; "" when pristine.</summary>
    public string Summary()
    {
        var hurt = _parts.Values.Where(p => p.Hp < p.Def.MaxHp || p.Armor < p.Def.MaxArmor).ToList();
        bool wholeHurt = _wholeHealth < WholeHealthMax || _wholeArmor < WholeArmorMax;
        if (hurt.Count == 0 && !wholeHurt)
            return "";
        string whole = "hull";
        if (WholeArmorMax > 0f)
            whole += $" a{_wholeArmor / WholeArmorMax * 100f:0}%";
        if (WholeHealthMax > 0f)
            whole += $" h{_wholeHealth / WholeHealthMax * 100f:0}%";
        return hurt.Count == 0 ? whole : whole + " · " + string.Join(" · ", hurt.Select(PoolText));
    }

    // FUN_004b7f80 verbatim: armor spends first and its covered share shields health
    // 1:1; the leftovers are written back into the damage pair. Quirks kept on purpose: armor
    // standing against a hit with NO armor damage nulls the health damage outright, and the
    // health leftover is measured against the full magnitude, so the armor-shielded share
    // re-enters the wrapper loop rather than vanishing. Internal because the steady-hand roll's
    // own leftover loop shrinks its copy of the pools through this arithmetic, not a second one.
    internal static void Spend(ref float dmgA, ref float dmgH, ref float poolA, ref float poolH)
    {
        float covered = 0f;
        if (poolA > 0f)
        {
            if (dmgA <= 0f)
            {
                dmgH = 0f;
                return;
            }

            covered = Mathf.Min(poolA / dmgA, 1f);
            float spent = Mathf.Min(poolA, dmgA);
            poolA -= spent;
            dmgA -= spent;
            if (covered >= 1f)
            {
                dmgH = 0f;
                return;
            }
        }

        if (dmgH > 0f)
        {
            float reaches = (1f - covered) * dmgH;
            float absorbed = Mathf.Min(poolH, reaches);
            poolH = Mathf.Max(0f, poolH - reaches);
            dmgH = Mathf.Max(0f, dmgH - absorbed);
        }
        else
        {
            dmgH = 0f;
        }
    }

    private static string PoolText(PartState p)
    {
        string text = p.Def.Name;
        if (p.Def.MaxArmor > 0f)
            text += $" a{p.ArmorFraction * 100f:0}%";
        if (p.Hp < p.Def.MaxHp || p.Def.MaxArmor <= 0f)
            text += $" h{p.HealthFraction * 100f:0}%";
        return text;
    }

    // FUN_004b3950 + FUN_004b3b60: the named zone while its health lasts; otherwise a
    // uniform pick among the first up-to-3 surviving zones in def order; null only when none
    // survives (or the vehicle has no parts).
    private PartState? ResolveStruckPart(string partName)
    {
        if (_parts.TryGetValue(partName, out var named) && named.Hp > 0f)
            return named;
        var live = new List<PartState>(RedirectCandidates);
        foreach (var p in _order)
        {
            if (p.Hp <= 0f)
                continue;
            live.Add(p);
            if (live.Count == RedirectCandidates)
                break;
        }

        return live.Count == 0 ? null : live[(int)(NextRand() % (uint)live.Count)];
    }

    // FUN_004b3bf0's tail: after a part spend, whole current = parts' fraction of
    // their summed maxima × whole maxima, both pools. ⚠ The decoded quirk, reproduced on
    // purpose: this OVERWRITES any earlier zone-less overflow dent, partially healing the
    // whole pair back onto the parts' fraction. The engine's own arithmetic does this.
    private void RecomputeWhole()
    {
        float hpCur = 0f, hpMax = 0f, armorCur = 0f, armorMax = 0f;
        foreach (var p in _order)
        {
            hpCur += p.Hp;
            hpMax += p.Def.MaxHp;
            armorCur += p.Armor;
            armorMax += p.Def.MaxArmor;
        }

        _wholeHealth = hpMax == 0f ? 0f : hpCur * WholeHealthMax / hpMax;
        _wholeArmor = armorMax == 0f ? 0f : armorCur * WholeArmorMax / armorMax;
    }

    private uint NextRand()
    {
        uint x = _rng;
        x ^= x << 13;
        x ^= x >> 17;
        x ^= x << 5;
        _rng = x;
        return x;
    }

    public sealed class PartState
    {
        public float Hp;
        public float Armor;
        public required DestroyablePart Def { get; init; }

        /// <summary>The combined sequential fraction, both pools against both maxima. Damage
        /// walks it down through armor and then health as one progression, which is the scale
        /// the def's injure_anims thresholds are on.</summary>
        public float Fraction => Def.MaxHp + Def.MaxArmor > 0f
            ? (Hp + Armor) / (Def.MaxHp + Def.MaxArmor)
            : 0f;

        public float HealthFraction => Def.MaxHp > 0f ? Hp / Def.MaxHp : 0f;

        /// <summary>0f for a part the data gives no armor pool, an unarmored zone, not a full
        /// one.</summary>
        public float ArmorFraction => Def.MaxArmor > 0f ? Armor / Def.MaxArmor : 0f;
    }
}
