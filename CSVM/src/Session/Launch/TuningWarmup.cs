using System;
using CSVM.Effects;
using CSVM.Flight.Airframe;
using CSVM.Flight.Hud;
using CSVM.Flight.Weapons;
using CSVM.Session.Roster;
using CSVM.Utils;
using Godot;

namespace CSVM.Session.Launch;

/// <summary>Exercises each Config-wired module's tunable reads once, with throwaway instances and
/// no game data. <see cref="Config"/>'s registry then knows the full key set before
/// <see cref="Config.ReportOrphans"/> and <c>--dump-config</c> run. It lives above the modules it
/// names so that <see cref="Config"/> holds values without knowing who reads them. Read-through
/// means a read registers on execution, so one dummy step is the cheapest way to run them.
/// Add a line here as each module is wired to Config.</summary>
public static class TuningWarmup
{
    /// <summary>Run every registering read once. A failure warns and leaves the dump incomplete
    /// rather than stopping the launch.</summary>
    public static void Run()
    {
        try
        {
            _ = HudMetrics.StatusTextScale;
            _ = HudMetrics.MarkerTextScale;
            var fm = new FlightModel(new PlaneStats());
            fm.Reset(Vector3.Zero, Basis.Identity, 100f, 1f);
            fm.Step(default, 1f / 60f);
            // ProjectilePool reads this only on a live rocket shot, which the warmup never fires,
            // register it here so --dump-config still documents the weapon-fire tunable.
            Config.GetFloat("weapons.rocketSpeedScale", ProjectilePool.RocketSpeedScale);
            // Tracer look reads only from a live Spawn/RenderTracers, which the warmup never
            // drives (no ProjectilePool here), register them here so --dump-config documents them.
            Config.GetFloat("weapons.tracerLength", ProjectilePool.TracerLength);
            Config.GetFloat("weapons.tracerWidth", ProjectilePool.TracerWidth);
            Config.GetFloat("weapons.tracerBrightness", ProjectilePool.TracerBrightness);
            Config.GetFloat("weapons.tracerMinPixels", ProjectilePool.TracerMinPixels);
            // The gun-ammo / ordnance testing caps are read only when a plane binds its loadout.
            // The warmup never does, so they are registered here for --dump-config.
            Config.GetInt("weapons.gunAmmoCap", FlightController.GunAmmoCapDefault);
            Config.GetInt("weapons.ordnanceCap", FlightController.OrdnanceCapDefault);
            // The autohead toggle. Default OFF: the original's cockpit footage shows a
            // pixel-frozen sight through manoeuvres, so its default plays no lead (the enable
            // byte is an option; docs/formats/vehicle/player-globals.md).
            Config.GetBool("headLook.autohead", false);
            // ⚠ Do not reinstate a `flightAudio` key. All three (whineMixGain,
            // damagedEngineMixGain, engineDetuneRatio) scaled mechanisms the engine-audio decode
            // refuted, so there is nothing left for them to tune (docs/formats/vehicle.md).

            // Puffer emitters read these at Init, which the warmup never reaches (an emitter needs
            // a texture archive), register them here so --dump-config still documents them.
            Config.GetFloat("puffer.burstSizeScale", Puffer.SizeScaleDefault);
            Config.GetFloat("puffer.trailSizeScale", Puffer.SizeScaleDefault);
            Config.GetFloat("puffer.sustainSizeScale", Puffer.SizeScaleDefault);
            // ⚠ Do not add `puffer.fireRiseScale`/`fireLifetimeScale` keys; fire rise and
            // lifetime are decoded authored data, not tunables (Puffer.cs). The three distance
            // switches below default to the original's own behaviour.
            Config.GetBool("puffer.distanceFade", true);
            Config.GetBool("puffer.farCull", true);
            Config.GetBool("puffer.nearCull", true);
            Config.GetFloat("puffer.globalFadeFactor", Puffer.GlobalFadeFactorDefault);
            // The graphics EffectsLevel and its fade switch are read once at launch, before this
            // warmup runs.
            Config.GetString(EffectsLevel.Key, EffectsLevel.Default);
            Config.GetBool(EffectsLevel.FadeKey, EffectsLevel.FadeDefault);
            // The enhanced-lighting mode key: also read once at launch (GraphicsMode.Resolve),
            // registered here so --dump-config documents it even on a --graphics= launch, which
            // bypasses this read.
            Config.GetString(GraphicsMode.Key, GraphicsMode.Default);
            // The start grid is built only by a multiplayer stunt race or a co-op campaign mission.
            // The warmup builds neither, so its two keys are registered here for --dump-config.
            Config.GetFloat("startGrid.slotSpacing", StartGrid.SlotSpacingDefault);
            Config.GetFloat("startGrid.groundClearance", StartGrid.GroundClearanceDefault);
        }
        catch (Exception e)
        {
            GD.PushWarning($"config: tuning-registry warmup failed ({e.Message}); --dump-config may be incomplete");
        }
    }
}
