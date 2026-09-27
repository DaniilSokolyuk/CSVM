using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace CSVM.Tests;

/// <summary>
/// Builds the extraction's inputs as bytes, from docs/formats/rof.md and docs/formats/strings.md.
/// They are a <c>.rof</c> tree, a PE carrying one <c>STRINGTABLE</c> block, and a <c>.BM</c>. Built in code rather than committed, so every field a test reads back is visible
/// where the test sets it.
/// </summary>
public static class ExtractionFixtures
{
    /// <summary>A <c>.rof</c> archive whose root holds <paramref name="root"/>'s entries.</summary>
    public static byte[] Rof(RofDir root)
    {
        using var s = new MemoryStream();
        EmitNode(root, s);
        return s.ToArray();
    }

    /// <summary>A 32-bit PE with one section holding one <c>STRINGTABLE</c> block, number
    /// <paramref name="block"/> (ids <c>(block - 1) * 16</c> onward). A null slot is unused.
    /// </summary>
    public static byte[] StringTablePe(int block, IReadOnlyList<string?> slots)
    {
        var table = new MemoryStream();
        for (int i = 0; i < 16; i++)
        {
            string? text = i < slots.Count ? slots[i] : null;
            WriteU16(table, text?.Length ?? 0);
            if (text != null)
            {
                table.Write(Encoding.Unicode.GetBytes(text));
            }
        }

        const int SectionRva = 0x1000;
        const int SectionFile = 0x200;
        var rsrc = new byte[0x58 + (int)table.Length];
        WriteDirectory(rsrc, 0x00, 6, 0x80000000u | 0x18);
        WriteDirectory(rsrc, 0x18, (uint)block, 0x80000000u | 0x30);
        WriteDirectory(rsrc, 0x30, 0x409, 0x48);
        BinaryPrimitives.WriteUInt32LittleEndian(rsrc.AsSpan(0x48), SectionRva + 0x58);
        BinaryPrimitives.WriteUInt32LittleEndian(rsrc.AsSpan(0x4C), (uint)table.Length);
        table.ToArray().CopyTo(rsrc, 0x58);

        var pe = new byte[SectionFile + rsrc.Length];
        BinaryPrimitives.WriteInt32LittleEndian(pe.AsSpan(0x3C), 0x40);
        pe[0x40] = (byte)'P';
        pe[0x41] = (byte)'E';
        BinaryPrimitives.WriteUInt16LittleEndian(pe.AsSpan(0x40 + 6), 1); // one section
        BinaryPrimitives.WriteUInt16LittleEndian(pe.AsSpan(0x40 + 20), 224); // optional header size
        int optional = 0x40 + 24;
        BinaryPrimitives.WriteUInt16LittleEndian(pe.AsSpan(optional), 0x10B); // PE32
        BinaryPrimitives.WriteUInt32LittleEndian(pe.AsSpan(optional + 96 + 16), SectionRva); // resources
        int section = optional + 224;
        Encoding.ASCII.GetBytes(".rsrc").CopyTo(pe, section);
        BinaryPrimitives.WriteUInt32LittleEndian(pe.AsSpan(section + 12), SectionRva);
        BinaryPrimitives.WriteUInt32LittleEndian(pe.AsSpan(section + 16), (uint)rsrc.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(pe.AsSpan(section + 20), SectionFile);
        rsrc.CopyTo(pe, SectionFile);
        return pe;
    }

    /// <summary>A <c>.BM</c> of the given size. <paramref name="plane"/> fills each byte of the
    /// shading, mask and overlay planes from its index, so every byte is traceable.
    /// </summary>
    public static byte[] Bm(int width, int height, Func<int, byte> plane)
    {
        int n = width * height;
        var bm = new byte[4 + (10 * n)];
        BinaryPrimitives.WriteUInt16LittleEndian(bm, (ushort)height);
        BinaryPrimitives.WriteUInt16LittleEndian(bm.AsSpan(2), (ushort)width);
        for (int i = 0; i < 10 * n; i++)
        {
            bm[4 + i] = plane(i);
        }

        return bm;
    }

    // One resource directory with a single id entry.
    private static void WriteDirectory(byte[] rsrc, int at, uint id, uint target)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(rsrc.AsSpan(at + 14), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(rsrc.AsSpan(at + 16), id);
        BinaryPrimitives.WriteUInt32LittleEndian(rsrc.AsSpan(at + 20), target);
    }

    private static void EmitNode(RofDir node, MemoryStream s)
    {
        var pool = new MemoryStream();
        var nameOffsets = new List<int>();
        foreach (var entry in node.Entries)
        {
            nameOffsets.Add((int)pool.Length);
            pool.Write(Encoding.ASCII.GetBytes(entry.Name + "\0"));
        }

        long start = s.Position;
        var header = new byte[8 + (24 * node.Entries.Count)];
        BinaryPrimitives.WriteUInt32LittleEndian(header, (uint)node.Entries.Count);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), (uint)pool.Length);
        s.Write(header);
        s.Write(pool.ToArray());

        for (int i = 0; i < node.Entries.Count; i++)
        {
            var entry = node.Entries[i];
            int offset = (int)s.Position;
            int kind;
            int stored = 0;
            int size = 0;
            if (entry.Directory != null)
            {
                kind = 1;
                EmitNode(entry.Directory, s);
            }
            else
            {
                byte[] data = entry.Data!;
                byte[] payload = entry.Deflate ? Zlib(data) : data;
                kind = entry.Deflate ? 2 : 0;
                stored = payload.Length;
                size = data.Length;
                s.Write(payload);
            }

            var e = header.AsSpan(8 + (24 * i));
            BinaryPrimitives.WriteUInt32LittleEndian(e, (uint)offset);
            BinaryPrimitives.WriteUInt32LittleEndian(e[4..], (uint)size);
            BinaryPrimitives.WriteUInt32LittleEndian(e[8..], (uint)stored);
            BinaryPrimitives.WriteUInt32LittleEndian(e[12..], (uint)kind);
            BinaryPrimitives.WriteUInt32LittleEndian(e[16..], (uint)(node.Entries[i].Name.Length + 1));
            BinaryPrimitives.WriteUInt32LittleEndian(e[20..], (uint)nameOffsets[i]);
        }

        long end = s.Position;
        s.Position = start;
        s.Write(header);
        s.Position = end;
    }

    private static byte[] Zlib(byte[] data)
    {
        using var output = new MemoryStream();
        using (var z = new ZLibStream(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            z.Write(data);
        }

        return output.ToArray();
    }

    private static void WriteU16(Stream s, int value)
    {
        Span<byte> b = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(b, (ushort)value);
        s.Write(b);
    }
}

/// <summary>One directory node of a fixture <c>.rof</c>, built with
/// <see cref="Dir(string, RofDir)"/> and <see cref="File(string, byte[], bool)"/>.</summary>
public sealed class RofDir
{
    /// <summary>The node's entries in stored order.</summary>
    public List<RofFixtureEntry> Entries { get; } = new();

    /// <summary>Adds a child directory.</summary>
    public RofDir Dir(string name, RofDir child)
    {
        Entries.Add(new RofFixtureEntry(name, child, null, false));
        return this;
    }

    /// <summary>Adds a file, zlib-deflated or stored.</summary>
    public RofDir File(string name, byte[] data, bool deflate = true)
    {
        Entries.Add(new RofFixtureEntry(name, null, data, deflate));
        return this;
    }
}

/// <summary>One entry of a <see cref="RofDir"/>: a directory or a file.</summary>
public sealed record RofFixtureEntry(string Name, RofDir? Directory, byte[]? Data, bool Deflate);
