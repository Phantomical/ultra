// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

using System.Buffers.Binary;
using System.Text;

namespace Ultra.Tests;

/// <summary>
/// Builds minimal, real Mach-O files for tests that exercise <see cref="Ultra.Core.MachO.MachOSymbolReader"/>
/// without needing an actual compiled binary.
/// </summary>
internal static class MachOTestFixtures
{
    /// <summary>
    /// Hand-builds a minimal but valid non-fat Mach-O 64-bit file: a <c>mach_header_64</c>, one
    /// <c>LC_SEGMENT_64</c> load command naming the <c>__TEXT</c> segment (so
    /// <see cref="Ultra.Core.MachO.MachOSymbolReader"/> has a text-segment virtual address to compute
    /// relative addresses from), and one <c>LC_SYMTAB</c> load command whose symbol/string tables hold
    /// exactly <paramref name="symbols"/>. See <c>MachOSymbolReader.TryRead</c> for the exact layout this
    /// must match.
    /// </summary>
    public static void WriteMinimalMachO(string filePath, ulong textSegmentVirtualAddress, (string Name, ulong Value)[] symbols)
    {
        const uint MachOMagic64 = 0xFEEDFACF;
        const uint LcSegment64 = 0x19;
        const uint LcSymtab = 0x2;
        const int HeaderSize = 32;
        const int SegmentCommandSize = 72;
        const int SymtabCommandSize = 24;
        const int Nlist64Size = 16;

        var symbolTableOffset = HeaderSize + SegmentCommandSize + SymtabCommandSize;
        var symbolTableSize = symbols.Length * Nlist64Size;
        var stringTableOffset = symbolTableOffset + symbolTableSize;

        // Conventional leading NUL (index 0 is reserved for "no name"), then each name NUL-terminated.
        var stringTableBytes = new List<byte> { 0 };
        var nameOffsets = new int[symbols.Length];
        for (var i = 0; i < symbols.Length; i++)
        {
            nameOffsets[i] = stringTableBytes.Count;
            stringTableBytes.AddRange(Encoding.ASCII.GetBytes(symbols[i].Name));
            stringTableBytes.Add(0);
        }

        var data = new byte[stringTableOffset + stringTableBytes.Count];
        var span = data.AsSpan();

        // mach_header_64
        BinaryPrimitives.WriteUInt32LittleEndian(span, MachOMagic64);
        BinaryPrimitives.WriteInt32LittleEndian(span.Slice(4), 0x0100000C); // cputype (arm64; unused for non-fat files)
        BinaryPrimitives.WriteInt32LittleEndian(span.Slice(8), 0);          // cpusubtype
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(12), 2);        // filetype: MH_EXECUTE
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(16), 2);        // ncmds
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(20), SegmentCommandSize + SymtabCommandSize); // sizeofcmds
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(24), 0);        // flags
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(28), 0);        // reserved

        // segment_command_64 (__TEXT)
        const int segmentOffset = HeaderSize;
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(segmentOffset), LcSegment64);
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(segmentOffset + 4), SegmentCommandSize);
        Encoding.ASCII.GetBytes("__TEXT").AsSpan().CopyTo(span.Slice(segmentOffset + 8, 16));
        BinaryPrimitives.WriteUInt64LittleEndian(span.Slice(segmentOffset + 24), textSegmentVirtualAddress);
        // vmsize, fileoff, filesize, maxprot, initprot, nsects, flags: left zero (unused by the reader).

        // symtab_command
        var symtabOffset = segmentOffset + SegmentCommandSize;
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(symtabOffset), LcSymtab);
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(symtabOffset + 4), SymtabCommandSize);
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(symtabOffset + 8), (uint)symbolTableOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(symtabOffset + 12), (uint)symbols.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(symtabOffset + 16), (uint)stringTableOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(symtabOffset + 20), (uint)stringTableBytes.Count);

        // nlist_64 entries
        for (var i = 0; i < symbols.Length; i++)
        {
            var entryOffset = symbolTableOffset + i * Nlist64Size;
            BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(entryOffset), (uint)nameOffsets[i]);
            span[entryOffset + 4] = 0x0F; // n_type: N_SECT | N_EXT
            span[entryOffset + 5] = 1;    // n_sect
            BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(entryOffset + 6), 0); // n_desc
            BinaryPrimitives.WriteUInt64LittleEndian(span.Slice(entryOffset + 8), symbols[i].Value);
        }

        stringTableBytes.CopyTo(data, stringTableOffset);

        File.WriteAllBytes(filePath, data);
    }
}
