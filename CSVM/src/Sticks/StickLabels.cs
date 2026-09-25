using System;
using CSVM.Bindings;

namespace CSVM.Sticks;

/// <summary>How a rebinding screen names a stick: its active profile's short name ("R", "L"). Without
/// one it is "Stick" and its model, so two unnamed sticks never read as one. Registered as
/// <see cref="BindingLabels.StickName"/> once per process, whether or not sticks are on, so a stick
/// row always reads as a stick.</summary>
public static class StickLabels
{
    /// <summary>Makes <see cref="BindingLabels"/> name stick identities through
    /// <see cref="Live"/>.</summary>
    public static void Register() => BindingLabels.StickName = Live;

    /// <summary>The caption prefix of <paramref name="device"/> from the live profiles, or null for
    /// a device that is no stick.</summary>
    public static string? Live(DeviceId device) =>
        Prefix(device, model => StickProfiles.Live?.ActiveFor(model)?.Name);

    /// <summary>The caption prefix of <paramref name="device"/> with its short name taken from
    /// <paramref name="nameOf"/>, or null for a device that is no stick. A blank name is no name.
    /// </summary>
    public static string? Prefix(DeviceId device, Func<StickModel, string?> nameOf)
    {
        ArgumentNullException.ThrowIfNull(nameOf);
        if (!StickModel.TryFromDevice(device, out var model))
            return null;
        string? name = nameOf(model);
        return string.IsNullOrWhiteSpace(name) ? "Stick " + model : name.Trim();
    }
}
