using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace CSVM.Flight.Hangar;

/// <summary>
/// Import-only reader for the original's saved custom plane files: the 204-byte records under an
/// install's <c>Planes\</c> directory, one verbatim in-memory record per plane
/// (docs/formats/paint.md "Saved custom planes"). A read is one way, original file to
/// <see cref="CustomPlaneDef"/>; CSVM's own planes persist through <see cref="CustomPlaneStore"/>
/// and never in this format. Like the store, a file this reader cannot make sense of reads as
/// null rather than throwing: an import must never break on one corrupt save.
/// </summary>
public static class CustomPlaneRecord
{
    /// <summary>The record's exact size, the original's <c>fwrite(record, 0xcc, 1)</c>.</summary>
    public const int Length = 0xcc;

    // The name field occupies +0x04 up to the next field at +0x28.
    private const int NameOffset = 0x04;
    private const int NameCapacity = 0x28 - NameOffset;

    // The record stores each armour zone premultiplied by 5 (the armour screen divides on read);
    // the raw ceiling is therefore MaxArmourUnits * 5.
    private const int ArmourScale = 5;

    // Gun id 5 is the dropdown's explicit empty row, one past the last calibre.
    private const int GunEmpty = 5;

    // No swatch row ships more than ten shades, so a variant past nine names no colour.
    private const int MaxShade = 9;

    /// <summary>The def a 204-byte record describes, or null when the bytes are too short, carry
    /// no name, or claim a value outside the decoded ranges (airframe, engine, hardpoints,
    /// pattern, colour, shade, decal, armour, gun ids). Derived fields (+0x28, +0x3c, +0x98..,
    /// +0xa8..) are ignored, and so is the +0x68 RGBA: it is the engine's own cache of what the
    /// colour and shade indices resolve to, which the def recomputes. <see cref="StoredColour"/>
    /// reads it for the cross-check.</summary>
    public static CustomPlaneDef? Read(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < Length)
        {
            return null;
        }

        var name = ReadName(bytes.Slice(NameOffset, NameCapacity));
        if (name.Length == 0)
        {
            return null;
        }

        int airframe = Dword(bytes, 0x2c);
        int engine = Dword(bytes, 0x30);
        int leftHardpoints = Dword(bytes, 0x34);
        int rightHardpoints = Dword(bytes, 0x38);
        int pattern = Dword(bytes, 0x40);
        int twinBits = Dword(bytes, 0x84);
        Span<int> armour = [Dword(bytes, 0x74), Dword(bytes, 0x78), Dword(bytes, 0x7c), Dword(bytes, 0x80)];
        Span<int> gunIds = [Dword(bytes, 0x88), Dword(bytes, 0x8c), Dword(bytes, 0x90), Dword(bytes, 0x94)];
        Span<int> colours = [Dword(bytes, 0x44), Dword(bytes, 0x48), Dword(bytes, 0x4c)];
        Span<int> shades = [Dword(bytes, 0x50), Dword(bytes, 0x54), Dword(bytes, 0x58)];
        Span<int> decals = [Dword(bytes, 0x5c), Dword(bytes, 0x60), Dword(bytes, 0x64)];

        bool inRange =
            InRange(airframe, CustomPlaneDef.MaxAirframe)
            && InRange(engine, CustomPlaneDef.EngineNone)
            && InRange(leftHardpoints, CustomPlaneDef.MaxHardpointsPerWing)
            && InRange(rightHardpoints, CustomPlaneDef.MaxHardpointsPerWing)
            && InRange(pattern, CustomPlaneDef.MaxPaintPattern);
        foreach (int raw in armour)
        {
            inRange &= InRange(raw, CustomPlaneDef.MaxArmourUnits * ArmourScale);
        }

        foreach (int id in gunIds)
        {
            inRange &= InRange(id, GunEmpty);
        }

        foreach (int colour in colours)
        {
            inRange &= InRange(colour, CustomPlaneDef.MaxSwatch);
        }

        foreach (int shade in shades)
        {
            inRange &= InRange(shade, MaxShade);
        }

        foreach (int decal in decals)
        {
            inRange &= InRange(decal, CustomPlaneDef.MaxDecal);
        }

        if (!inRange)
        {
            return null;
        }

        var def = new CustomPlaneDef
        {
            Name = name,
            Airframe = airframe,
            Engine = engine,
            LeftHardpoints = leftHardpoints,
            RightHardpoints = rightHardpoints,
            ArmourNose = armour[0] / ArmourScale,
            ArmourTail = armour[1] / ArmourScale,
            ArmourLeftWing = armour[2] / ArmourScale,
            ArmourRightWing = armour[3] / ArmourScale,
            PaintPattern = pattern,
            NoseDecal = decals[0],
            TailDecal = decals[1],
            WingDecal = decals[2],
        };
        for (int slot = 0; slot < HangarPaintTables.Slots; slot++)
        {
            def.PaintColours[slot] = colours[slot];
            def.PaintShades[slot] = shades[slot];
        }

        for (int slot = 0; slot < def.Guns.Length; slot++)
        {
            int? calibre = gunIds[slot] == GunEmpty ? null : gunIds[slot];
            def.Guns[slot] = new GunChoice(calibre, (twinBits & (1 << slot)) != 0);
        }

        return def;
    }

    /// <summary>A paint slot's RGB as the record itself caches it at +0x68 (RGBA quads, the fourth
    /// byte padding). Not read into the def: the def resolves the same colour from its index pair,
    /// and this is what that resolution is checked against.</summary>
    public static PaintColour StoredColour(ReadOnlySpan<byte> bytes, int slot) =>
        new(bytes[0x68 + (slot * 4)], bytes[0x69 + (slot * 4)], bytes[0x6a + (slot * 4)]);

    /// <summary>The def stored in <paramref name="path"/> (absolute), or null when the file is
    /// absent, unreadable, or not a valid record.</summary>
    public static CustomPlaneDef? ReadFile(string path)
    {
        RequireAbsolute(path);
        try
        {
            return File.Exists(path) ? Read(File.ReadAllBytes(path)) : null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Every readable saved plane in an install's <c>Planes</c> directory (absolute
    /// path), sorted by name. The directory is only ever read. The original's saves carry no
    /// extension, so every file is tried (its own scan is <c>Planes\*.*</c>); a missing directory
    /// or an unreadable file contributes nothing.</summary>
    public static IReadOnlyList<CustomPlaneDef> ImportDirectory(string directory)
    {
        RequireAbsolute(directory);
        var planes = new List<CustomPlaneDef>();
        if (Directory.Exists(directory))
        {
            foreach (var file in Directory.GetFiles(directory))
            {
                var def = ReadFile(file);
                if (def != null)
                {
                    planes.Add(def);
                }
            }
        }

        planes.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return planes;
    }

    // The name is NUL-terminated within its field; the original writes it with sprintf, so bytes
    // past the terminator are whatever was there before. Latin-1 mirrors the original's ANSI
    // filenames byte for byte.
    private static string ReadName(ReadOnlySpan<byte> field)
    {
        int end = field.IndexOf((byte)0);
        return Encoding.Latin1.GetString(end < 0 ? field : field[..end]).Trim();
    }

    private static int Dword(ReadOnlySpan<byte> bytes, int offset) =>
        BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(offset, 4));

    private static bool InRange(int value, int max) => value >= 0 && value <= max;

    private static void RequireAbsolute(string path)
    {
        if (!Path.IsPathRooted(path))
        {
            throw new ArgumentException($"custom plane import needs an absolute path, got '{path}'");
        }
    }
}
