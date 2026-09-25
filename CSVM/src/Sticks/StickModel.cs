using System;
using System.Globalization;
using CSVM.Bindings;

namespace CSVM.Sticks;

/// <summary>
/// A stick's identity: the USB vendor and product id every unit of one model reports. Bindings
/// and profiles key on this rather than on a unit, so a replug or another USB port changes nothing.
/// Two identical units of one model therefore read as one device. Printed as <c>231D/0201</c>,
/// the form the logs and the profile files share.
/// </summary>
public readonly record struct StickModel(ushort Vendor, ushort Product)
{
    /// <summary>What a stick's joypad id starts with, ahead of its printed model. A Godot pad's id
    /// is a 32-digit GUID or its name, so neither form can be mistaken for the other.</summary>
    public const string DevicePrefix = "stick:";

    /// <summary>The identity every binding on this model names: the joypad id
    /// <c>stick:231D/0201</c>, stored as <c>pad:stick:231D/0201/&lt;control&gt;</c>. The store
    /// splits a token at its last slash, and no control token holds one.</summary>
    public DeviceId Device => DeviceId.Joypad(DevicePrefix + ToString());

    /// <summary>Reads the <c>231D/0201</c> form <see cref="ToString"/> prints; false for anything
    /// else, so a hand-edited profile naming a malformed model is refused, not guessed at.</summary>
    public static bool TryParse(string? text, out StickModel model)
    {
        model = default;
        return text is not null && TryParse(text.AsSpan().Trim(), out model);
    }

    /// <summary>The model a <see cref="Device"/> identity names, or false for the keyboard, the
    /// mouse, a Godot pad, or a malformed id. Parsed without allocating, since every stick binding
    /// asks once per tick.</summary>
    public static bool TryFromDevice(DeviceId device, out StickModel model)
    {
        model = default;
        return device.Kind == DeviceKind.Joypad
            && device.Id.StartsWith(DevicePrefix, StringComparison.Ordinal)
            && TryParse(device.Id.AsSpan(DevicePrefix.Length), out model);
    }

    /// <summary>The model a Godot pad reports, from the decimal <c>vendor_id</c>/<c>product_id</c>
    /// strings of <c>Input.GetJoyInfo</c>. False when either is missing or not a 16-bit number,
    /// since such a pad cannot be matched against a stick and so hides none.</summary>
    public static bool TryFromDecimal(string? vendor, string? product, out StickModel model)
    {
        model = default;
        if (!ushort.TryParse(vendor, NumberStyles.None, CultureInfo.InvariantCulture, out ushort v)
            || !ushort.TryParse(product, NumberStyles.None, CultureInfo.InvariantCulture, out ushort p))
        {
            return false;
        }

        model = new StickModel(v, p);
        return true;
    }

    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{Vendor:X4}/{Product:X4}");

    private static bool TryParse(ReadOnlySpan<char> text, out StickModel model)
    {
        model = default;
        if (text.Length != 9 || text[4] != '/'
            || !ushort.TryParse(text[..4], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out ushort vendor)
            || !ushort.TryParse(text[5..], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out ushort product))
        {
            return false;
        }

        model = new StickModel(vendor, product);
        return true;
    }
}
