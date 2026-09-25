using System.Globalization;

namespace CSVM.Sticks;

/// <summary>
/// A stick's identity: the USB vendor and product id every unit of one model reports. Bindings
/// and profiles key on this rather than on a unit, so a replug or another USB port changes nothing.
/// Two identical units of one model therefore read as one device. Printed as <c>231D/0201</c>,
/// the form the logs and the profile files share.
/// </summary>
public readonly record struct StickModel(ushort Vendor, ushort Product)
{
    /// <summary>Reads the <c>231D/0201</c> form <see cref="ToString"/> prints; false for anything
    /// else, so a hand-edited profile naming a malformed model is refused, not guessed at.</summary>
    public static bool TryParse(string? text, out StickModel model)
    {
        model = default;
        if (text is null)
        {
            return false;
        }

        string[] parts = text.Trim().Split('/');
        if (parts.Length != 2 || parts[0].Length != 4 || parts[1].Length != 4
            || !ushort.TryParse(parts[0], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out ushort vendor)
            || !ushort.TryParse(parts[1], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out ushort product))
        {
            return false;
        }

        model = new StickModel(vendor, product);
        return true;
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
}
