using System;
using System.Buffers.Binary;

namespace CSVM.Extraction;

/// <summary>
/// One custom <c>.BM</c> paint-shop texture from the UI archive, split into two images. They are
/// the greyscale shading map and the three paint-region masks packed as R, G and B. The layout is
/// in docs/formats/rof.md. The overlay plane is not decoded. Rows keep their stored bottom-up
/// order, since the PNGs are inspection images and the paint code reads the <c>.BM</c> itself.
/// </summary>
public sealed class BmTexture
{
    private BmTexture(int width, int height, byte[] shading, byte[] masks)
    {
        Width = width;
        Height = height;
        Shading = shading;
        Masks = masks;
    }

    /// <summary>Width in pixels.</summary>
    public int Width { get; }

    /// <summary>Height in pixels.</summary>
    public int Height { get; }

    /// <summary>The shading map as RGB, three bytes per pixel, rows top to bottom.</summary>
    public byte[] Shading { get; }

    /// <summary>The paint masks as RGB: paint slot 1 in R, slot 2 in G, slot 3 in B.</summary>
    public byte[] Masks { get; }

    /// <summary>The texture in <paramref name="data"/>, or null when the bytes are too short to
    /// hold the header and the shading and mask planes it declares.</summary>
    public static BmTexture? TryDecode(ReadOnlySpan<byte> data)
    {
        if (data.Length < 4)
        {
            return null;
        }

        int height = BinaryPrimitives.ReadUInt16LittleEndian(data);
        int width = BinaryPrimitives.ReadUInt16LittleEndian(data[2..]);
        int n = width * height;
        if (width <= 0 || height <= 0 || data.Length < 4 + (6 * n))
        {
            return null;
        }

        byte[] shading = data.Slice(4, 3 * n).ToArray();
        var masks = new byte[3 * n];
        ReadOnlySpan<byte> slot1 = data.Slice(4 + (3 * n), n);
        ReadOnlySpan<byte> slot2 = data.Slice(4 + (4 * n), n);
        ReadOnlySpan<byte> slot3 = data.Slice(4 + (5 * n), n);
        for (int px = 0; px < n; px++)
        {
            masks[3 * px] = slot1[px];
            masks[(3 * px) + 1] = slot2[px];
            masks[(3 * px) + 2] = slot3[px];
        }

        return new BmTexture(width, height, shading, masks);
    }

    /// <summary>Writes <c>&lt;stem&gt;.png</c> and <c>&lt;stem&gt;_mask.png</c> beside
    /// <paramref name="bmPath"/>, the stem being the path without its <c>.BM</c>.</summary>
    public void WritePngsBeside(string bmPath)
    {
        string stem = bmPath[..^3];
        PngWriter.WriteRgb(stem + ".png", Width, Height, Shading);
        PngWriter.WriteRgb(stem + "_mask.png", Width, Height, Masks);
    }
}
