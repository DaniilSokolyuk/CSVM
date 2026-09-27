using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace CSVM.Extraction;

/// <summary>
/// Reads a <c>.rof</c> UI resource archive held in memory: a tree of directory nodes whose file
/// members are stored or zlib-deflated. The layout is in docs/formats/rof.md. Walking the tree
/// costs no inflation, so a caller can census an archive and inflate only what it keeps.
/// </summary>
public static class RofArchive
{
    private const int KindStored = 0;
    private const int KindDirectory = 1;
    private const int KindDeflated = 2;
    private const int EntrySize = 24;

    // Far above the shipped archive's largest node. A count past these is a misread offset, and
    // reading on would allocate or loop on garbage.
    private const uint MaxEntries = 100000;
    private const uint MaxPoolBytes = 10000000;

    /// <summary>Every entry of <paramref name="archive"/> in stored order, a directory before the
    /// entries inside it. Paths use <c>/</c> and spell names as the archive does.</summary>
    public static IEnumerable<RofEntry> Walk(byte[] archive)
    {
        var ordered = new List<RofEntry>();
        WalkNode(archive, 0, string.Empty, ordered);
        return ordered;
    }

    /// <summary>The bytes of one file entry, inflated to its declared size. Throws when the
    /// payload does not inflate to exactly that size.</summary>
    public static byte[] ReadMember(byte[] archive, RofEntry entry)
    {
        if (entry.IsDirectory)
        {
            throw new ArgumentException("a directory has no payload: " + entry.Path, nameof(entry));
        }

        var data = new byte[entry.Size];
        if (!entry.Deflated)
        {
            Buffer.BlockCopy(archive, entry.Offset, data, 0, entry.Size);
            return data;
        }

        // A zlib stream: DeflateStream wants raw deflate, so the 2-byte header is skipped and the
        // trailing checksum is never read.
        using var source = new MemoryStream(archive, entry.Offset + 2, entry.StoredSize - 2, writable: false);
        using var inflate = new DeflateStream(source, CompressionMode.Decompress);
        int read = 0;
        while (read < data.Length)
        {
            int n = inflate.Read(data, read, data.Length - read);
            if (n <= 0)
            {
                break;
            }

            read += n;
        }

        if (read != data.Length)
        {
            throw new InvalidDataException(
                "short inflate for " + entry.Path + ": got " + read + " of " + data.Length);
        }

        return data;
    }

    private static void WalkNode(byte[] b, int offset, string prefix, List<RofEntry> into)
    {
        uint count = U32(b, offset);
        uint poolLength = U32(b, offset + 4);
        if (count > MaxEntries || poolLength > MaxPoolBytes)
        {
            throw new InvalidDataException("implausible .rof directory node at " + offset);
        }

        int entries = offset + 8;
        int pool = entries + ((int)count * EntrySize);
        for (int i = 0; i < count; i++)
        {
            int e = entries + (i * EntrySize);
            int nameStart = pool + (int)U32(b, e + 20);
            int nameEnd = nameStart;
            int nameLimit = nameStart + (int)U32(b, e + 16);
            while (nameEnd < nameLimit && b[nameEnd] != 0)
            {
                nameEnd++;
            }

            string path = prefix + Encoding.ASCII.GetString(b, nameStart, nameEnd - nameStart);
            int target = (int)U32(b, e);
            uint kind = U32(b, e + 12);
            if (kind == KindDirectory)
            {
                into.Add(new RofEntry(path, true, target, 0, 0, false));
                WalkNode(b, target, path + "/", into);
            }
            else if (kind == KindStored || kind == KindDeflated)
            {
                into.Add(new RofEntry(path, false, target, (int)U32(b, e + 8), (int)U32(b, e + 4), kind == KindDeflated));
            }
            else
            {
                throw new InvalidDataException("unknown .rof entry kind " + kind + " for " + path);
            }
        }
    }

    private static uint U32(byte[] b, int offset)
    {
        if (offset < 0 || offset + 4 > b.Length)
        {
            throw new InvalidDataException(".rof field at " + offset + " lies past the end of the archive");
        }

        return BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(offset));
    }
}

/// <summary>One entry of a <see cref="RofArchive"/>: its archive path with <c>/</c> separators,
/// and where its payload sits. <see cref="StoredSize"/> and <see cref="Size"/> are zero for a
/// directory.</summary>
public sealed record RofEntry(string Path, bool IsDirectory, int Offset, int StoredSize, int Size, bool Deflated);
