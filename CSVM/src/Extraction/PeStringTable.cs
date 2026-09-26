using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace CSVM.Extraction;

/// <summary>
/// Reads the Win32 <c>STRINGTABLE</c> resources out of a PE file's bytes, with no Win32 call.
/// That keeps <c>langui.dll</c> and <c>language.dll</c> readable on every platform. Block
/// <c>(id &gt;&gt; 4) + 1</c> holds 16 slots. Each slot is a <c>u16</c> length and that many
/// UTF-16LE code units, length 0 marking an unused slot. See docs/formats/strings.md.
/// </summary>
public static class PeStringTable
{
    private const uint ResourceTypeString = 6;
    private const uint SubdirectoryBit = 0x80000000;

    /// <summary>Every string in <paramref name="pe"/>'s string tables by id, ascending. Empty when
    /// the file has no resource section. Throws <see cref="InvalidDataException"/> on a file that
    /// is not a PE.</summary>
    public static SortedDictionary<int, string> Read(byte[] pe)
    {
        var strings = new SortedDictionary<int, string>();
        int header = I32(pe, 0x3C);
        if (header < 0 || header + 24 > pe.Length || pe[header] != 'P' || pe[header + 1] != 'E')
        {
            throw new InvalidDataException("not a PE file");
        }

        int sectionCount = U16(pe, header + 6);
        int optionalSize = U16(pe, header + 20);
        int optional = header + 24;
        bool pe32Plus = U16(pe, optional) == 0x20B;
        int dataDirectories = optional + (pe32Plus ? 112 : 96);
        uint resourceRva = U32(pe, dataDirectories + 16); // data directory 2: resources
        if (resourceRva == 0)
        {
            return strings;
        }

        var sections = new List<(uint Rva, uint Size, uint FileOffset)>();
        int sectionTable = optional + optionalSize;
        for (int i = 0; i < sectionCount; i++)
        {
            int s = sectionTable + (i * 40);
            sections.Add((U32(pe, s + 12), U32(pe, s + 16), U32(pe, s + 20)));
        }

        int root = ToFileOffset(resourceRva, sections);
        foreach (var (type, typeTarget) in Entries(pe, root))
        {
            if (type != ResourceTypeString)
            {
                continue;
            }

            foreach (var (block, blockTarget) in Entries(pe, root + (int)(typeTarget & ~SubdirectoryBit)))
            {
                // Named entries never hold a string table; only an id carries the block number.
                if ((block & SubdirectoryBit) != 0)
                {
                    continue;
                }

                foreach (var (_, leafOffset) in Entries(pe, root + (int)(blockTarget & ~SubdirectoryBit)))
                {
                    int leaf = root + (int)leafOffset;
                    int start = ToFileOffset(U32(pe, leaf), sections);
                    int end = start + (int)U32(pe, leaf + 4);
                    ReadBlock(pe, start, end, ((int)block - 1) << 4, strings);
                }
            }
        }

        return strings;
    }

    private static void ReadBlock(byte[] pe, int start, int end, int firstId, SortedDictionary<int, string> into)
    {
        int o = start;
        for (int slot = 0; slot < 16 && o + 2 <= end; slot++)
        {
            int length = U16(pe, o);
            o += 2;
            if (length > 0)
            {
                if (o + (length * 2) > pe.Length)
                {
                    throw new InvalidDataException("string " + (firstId + slot) + " runs past the end of the file");
                }

                into[firstId + slot] = Encoding.Unicode.GetString(pe, o, length * 2);
                o += length * 2;
            }
        }
    }

    // One resource directory's entries as (name or id, offset of the child), named entries first
    // as the directory stores them.
    private static IEnumerable<(uint Name, uint Target)> Entries(byte[] pe, int directory)
    {
        int count = U16(pe, directory + 12) + U16(pe, directory + 14);
        for (int i = 0; i < count; i++)
        {
            int e = directory + 16 + (i * 8);
            yield return (U32(pe, e), U32(pe, e + 4));
        }
    }

    private static int ToFileOffset(uint rva, List<(uint Rva, uint Size, uint FileOffset)> sections)
    {
        foreach (var s in sections)
        {
            if (rva >= s.Rva && rva < s.Rva + s.Size)
            {
                return (int)(s.FileOffset + (rva - s.Rva));
            }
        }

        throw new InvalidDataException("RVA not mapped to any section: " + rva);
    }

    private static int U16(byte[] b, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(Field(b, offset, 2));

    private static uint U32(byte[] b, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(Field(b, offset, 4));

    private static int I32(byte[] b, int offset) => BinaryPrimitives.ReadInt32LittleEndian(Field(b, offset, 4));

    private static ReadOnlySpan<byte> Field(byte[] b, int offset, int length)
    {
        if (offset < 0 || offset + length > b.Length)
        {
            throw new InvalidDataException("PE field at " + offset + " lies past the end of the file");
        }

        return b.AsSpan(offset, length);
    }
}
