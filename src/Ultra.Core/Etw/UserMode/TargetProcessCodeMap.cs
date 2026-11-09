// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Ultra.Core.Etw.UserMode;

/// <summary>An image loaded in the sampled process.</summary>
internal sealed class TargetImage
{
    public required ulong Base { get; init; }
    public required uint Size { get; init; }
    public required string Path { get; init; }
    public uint Checksum { get; init; }
    public uint TimeDateStamp { get; init; }
    public uint ExceptionDirectoryRva { get; init; }
    public uint ExceptionDirectorySize { get; init; }
    public ulong End => Base + Size;

    /// <summary>The function entries of the image, sorted by begin address; read on first use.</summary>
    public uint[]? Functions { get; set; }
}

/// <summary>
/// The code of another process, read through <c>ReadProcessMemory</c>: its images with their exception directory,
/// and the dynamic function tables registered with ntdll (JIT code), for <see cref="X64Unwinder"/>.
/// </summary>
/// <remarks>
/// The dynamic function table list is the ntdll <c>DYNAMIC_FUNCTION_TABLE</c> list, whose layout is not documented but
/// has been stable since Windows 8 (x64): <c>LIST_ENTRY</c> at 0x00, <c>FunctionTable</c> at 0x10, <c>MinimumAddress</c>
/// at 0x20, <c>MaximumAddress</c> at 0x28, <c>BaseAddress</c> at 0x30, <c>Type</c> at 0x50 and <c>EntryCount</c> at 0x54.
/// It is read without the lock the process takes to change it, so a read can be torn: every value read is
/// validated, and a table is re-read when a lookup misses in its range (growable tables gain entries as code is
/// compiled). Tables registered with a callback (<c>RtlInstallFunctionTableCallback</c>) cannot be queried from
/// outside the process; their code is walked through the frame pointer.
/// </remarks>
[SupportedOSPlatform("windows")]
internal sealed unsafe class TargetProcessCodeMap : IX64CodeMap
{
    private const int DynamicTableSize = 0x58;
    private const int MaxDynamicTables = 100_000;
    private const int MaxDynamicTableEntries = 10_000_000;
    private const int RfSorted = 0;
    private const int RfUnsorted = 1;

    private readonly IntPtr _process;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private List<TargetImage> _images = new();
    private readonly List<DynamicTable> _tables = new();
    private long _lastTableListRefreshMs = long.MinValue;
    private readonly Dictionary<ulong, long> _codePages = new();

    [StructLayout(LayoutKind.Sequential)]
    private struct RuntimeFunctionEntry
    {
        public uint Begin;
        public uint End;
        public uint Unwind;
    }

    private sealed class DynamicTable
    {
        public ulong Address;
        public ulong FunctionTable;
        public ulong Minimum;
        public ulong Maximum;
        public ulong Base;
        public int Type;
        public uint EntryCount;
        public uint[] Functions = [];
        public long LastRefreshMs;
    }

    public TargetProcessCodeMap(IntPtr process)
    {
        _process = process;
    }

    /// <summary>The images found by the last <see cref="RefreshImages"/>, sorted by base.</summary>
    public IReadOnlyList<TargetImage> Images => _images;

    /// <summary>The number of dynamic function tables found in the process.</summary>
    public int DynamicTableCount => _tables.Count;

    /// <summary>Enumerates the images of the process, keeping the already-read ones, and reports the changes.</summary>
    public void RefreshImages(List<TargetImage> loaded, List<TargetImage> unloaded)
    {
        var handles = new IntPtr[1024];
        uint needed;
        while (true)
        {
            fixed (IntPtr* pHandles = handles)
            {
                if (!UserModeNative.EnumProcessModulesEx(_process, pHandles, (uint)(handles.Length * IntPtr.Size), out needed, UserModeNative.LIST_MODULES_ALL))
                {
                    return;
                }
            }

            if (needed <= handles.Length * IntPtr.Size)
            {
                break;
            }

            handles = new IntPtr[needed / IntPtr.Size + 64];
        }

        var previous = new Dictionary<ulong, TargetImage>();
        foreach (var image in _images)
        {
            previous[image.Base] = image;
        }

        var images = new List<TargetImage>();
        var count = (int)(needed / IntPtr.Size);
        var nameBuffer = stackalloc char[1024];
        for (var i = 0; i < count; i++)
        {
            // A module handle is its image base. Every query of a module walks the remote loader list, so only new
            // modules are queried.
            var imageBase = (ulong)handles[i];
            if (previous.Remove(imageBase, out var existing))
            {
                images.Add(existing);
                continue;
            }

            if (!UserModeNative.GetModuleInformation(_process, handles[i], out var info, (uint)sizeof(UserModeNative.MODULEINFO)))
            {
                continue;
            }

            var length = UserModeNative.GetModuleFileNameExW(_process, handles[i], nameBuffer, 1024);
            var image = ReadImage(imageBase, info.SizeOfImage, new string(nameBuffer, 0, (int)length));
            images.Add(image);
            loaded.Add(image);
        }

        unloaded.AddRange(previous.Values);
        images.Sort((x, y) => x.Base.CompareTo(y.Base));
        _images = images;
    }

    private TargetImage ReadImage(ulong imageBase, uint size, string path)
    {
        Span<byte> header = stackalloc byte[1024];
        uint checksum = 0, timeDateStamp = 0, exceptionRva = 0, exceptionSize = 0;
        if (TryRead(imageBase, header))
        {
            var ntOffset = BinaryPrimitives.ReadInt32LittleEndian(header.Slice(0x3C));
            if (ntOffset > 0 && ntOffset + 24 + 144 <= header.Length && BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(ntOffset)) == 0x4550)
            {
                timeDateStamp = BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(ntOffset + 8));
                var optional = header.Slice(ntOffset + 24);
                if (BinaryPrimitives.ReadUInt16LittleEndian(optional) == 0x20B)
                {
                    checksum = BinaryPrimitives.ReadUInt32LittleEndian(optional.Slice(64));
                    exceptionRva = BinaryPrimitives.ReadUInt32LittleEndian(optional.Slice(112 + 3 * 8));
                    exceptionSize = BinaryPrimitives.ReadUInt32LittleEndian(optional.Slice(112 + 3 * 8 + 4));
                }
            }
        }

        return new TargetImage
        {
            Base = imageBase,
            Size = size,
            Path = path,
            Checksum = checksum,
            TimeDateStamp = timeDateStamp,
            ExceptionDirectoryRva = exceptionRva,
            ExceptionDirectorySize = exceptionSize,
        };
    }

    private TargetImage? FindImage(ulong pc)
    {
        var images = _images;
        int lo = 0, hi = images.Count - 1;
        while (lo <= hi)
        {
            var mid = (lo + hi) >> 1;
            var image = images[mid];
            if (pc < image.Base)
            {
                hi = mid - 1;
            }
            else if (pc >= image.End)
            {
                lo = mid + 1;
            }
            else
            {
                return image;
            }
        }

        return null;
    }

    public bool IsInImage(ulong pc) => FindImage(pc) is not null;

    public bool TryFindFunction(ulong pc, out X64RuntimeFunction function)
    {
        var image = FindImage(pc);
        if (image is not null)
        {
            image.Functions ??= ReadFunctions(image.Base + image.ExceptionDirectoryRva, image.ExceptionDirectorySize / 12, sort: false);
            return TryFindIn(image.Functions, image.Base, pc, out function);
        }

        var table = FindTable(pc);
        if (table is null && _clock.ElapsedMilliseconds - _lastTableListRefreshMs > 100)
        {
            RefreshDynamicTables();
            table = FindTable(pc);
        }

        if (table is null)
        {
            function = default;
            return false;
        }

        if (TryFindIn(table.Functions, table.Base, pc, out function))
        {
            return true;
        }

        if (_clock.ElapsedMilliseconds - table.LastRefreshMs > 20 && RefreshTable(table))
        {
            return TryFindIn(table.Functions, table.Base, pc, out function);
        }

        return false;
    }

    private static bool TryFindIn(uint[] functions, ulong imageBase, ulong pc, out X64RuntimeFunction function)
    {
        function = default;
        if (pc < imageBase || pc - imageBase > uint.MaxValue)
        {
            return false;
        }

        var rva = (uint)(pc - imageBase);
        int lo = 0, hi = functions.Length / 3 - 1;
        while (lo <= hi)
        {
            var mid = (lo + hi) >> 1;
            var begin = functions[mid * 3];
            if (rva < begin)
            {
                hi = mid - 1;
            }
            else if (rva >= functions[mid * 3 + 1])
            {
                lo = mid + 1;
            }
            else
            {
                function = new X64RuntimeFunction(imageBase, begin, functions[mid * 3 + 1], functions[mid * 3 + 2]);
                return true;
            }
        }

        return false;
    }

    private uint[] ReadFunctions(ulong address, uint count, bool sort)
    {
        if (address == 0 || count == 0 || count > MaxDynamicTableEntries)
        {
            return [];
        }

        var functions = new uint[count * 3];
        fixed (uint* p = functions)
        {
            if (!UserModeNative.ReadProcessMemory(_process, address, p, (nuint)(count * 12), out var read) || read != count * 12)
            {
                return [];
            }
        }

        if (sort)
        {
            var entries = MemoryMarshal.Cast<uint, RuntimeFunctionEntry>(functions.AsSpan());
            entries.Sort(static (x, y) => x.Begin.CompareTo(y.Begin));
        }

        return functions;
    }

    private DynamicTable? FindTable(ulong pc)
    {
        foreach (var table in _tables)
        {
            if (pc >= table.Minimum && pc < table.Maximum)
            {
                return table;
            }
        }

        return null;
    }

    /// <summary>Reads the list of dynamic function tables of the process.</summary>
    public void RefreshDynamicTables()
    {
        _lastTableListRefreshMs = _clock.ElapsedMilliseconds;
        var head = (ulong)UserModeNative.RtlGetFunctionTableListHead();
        if (head == 0 || !TryReadUInt64(head, out var entry))
        {
            return;
        }

        var known = new Dictionary<ulong, DynamicTable>();
        foreach (var table in _tables)
        {
            known[table.Address] = table;
        }

        var tables = new List<DynamicTable>();
        Span<byte> buffer = stackalloc byte[DynamicTableSize];
        for (var n = 0; entry != head && entry != 0 && n < MaxDynamicTables; n++)
        {
            if (!TryRead(entry, buffer))
            {
                break;
            }

            if (!known.TryGetValue(entry, out var table))
            {
                table = new DynamicTable { Address = entry };
            }

            if (ParseTable(table, buffer))
            {
                tables.Add(table);
            }

            entry = BinaryPrimitives.ReadUInt64LittleEndian(buffer);
        }

        _tables.Clear();
        _tables.AddRange(tables);
    }

    private bool RefreshTable(DynamicTable table)
    {
        Span<byte> buffer = stackalloc byte[DynamicTableSize];
        return TryRead(table.Address, buffer) && ParseTable(table, buffer);
    }

    private bool ParseTable(DynamicTable table, ReadOnlySpan<byte> buffer)
    {
        var functionTable = BinaryPrimitives.ReadUInt64LittleEndian(buffer.Slice(0x10));
        var minimum = BinaryPrimitives.ReadUInt64LittleEndian(buffer.Slice(0x20));
        var maximum = BinaryPrimitives.ReadUInt64LittleEndian(buffer.Slice(0x28));
        var tableBase = BinaryPrimitives.ReadUInt64LittleEndian(buffer.Slice(0x30));
        var type = BinaryPrimitives.ReadInt32LittleEndian(buffer.Slice(0x50));
        var entryCount = BinaryPrimitives.ReadUInt32LittleEndian(buffer.Slice(0x54));
        if (maximum <= minimum || maximum - minimum > 1UL << 40 || type is < 0 or > 3)
        {
            return false;
        }

        table.LastRefreshMs = _clock.ElapsedMilliseconds;
        var changed = table.FunctionTable != functionTable || table.EntryCount != entryCount || table.Minimum != minimum;
        table.FunctionTable = functionTable;
        table.Minimum = minimum;
        table.Maximum = maximum;
        table.Base = tableBase;
        table.Type = type;
        table.EntryCount = entryCount;
        if (changed && type is RfSorted or RfUnsorted)
        {
            table.Functions = ReadFunctions(functionTable, entryCount, sort: true);
        }

        return true;
    }

    public bool IsCode(ulong address)
    {
        if (FindImage(address) is not null)
        {
            return true;
        }

        if (address < 0x10000 || address >= 0x8000_0000_0000)
        {
            return false;
        }

        // Executable pages are remembered; a page found not executable is asked again later (code is allocated as
        // the process runs).
        var page = address >> 12;
        var now = _clock.ElapsedMilliseconds;
        if (_codePages.TryGetValue(page, out var state) && (state == long.MaxValue || now - state < 1000))
        {
            return state == long.MaxValue;
        }

        var isCode = UserModeNative.VirtualQueryEx(_process, address, out var info, (nuint)sizeof(UserModeNative.MEMORY_BASIC_INFORMATION)) != 0
                     && info.State == UserModeNative.MEM_COMMIT
                     && (info.Protect & (UserModeNative.PAGE_EXECUTE | UserModeNative.PAGE_EXECUTE_READ | UserModeNative.PAGE_EXECUTE_READWRITE | UserModeNative.PAGE_EXECUTE_WRITECOPY)) != 0;
        _codePages[page] = isCode ? long.MaxValue : now;
        return isCode;
    }

    public bool TryRead(ulong address, Span<byte> buffer)
    {
        fixed (byte* p = buffer)
        {
            return UserModeNative.ReadProcessMemory(_process, address, p, (nuint)buffer.Length, out var read) && read == (nuint)buffer.Length;
        }
    }

    private bool TryReadUInt64(ulong address, out ulong value)
    {
        Span<byte> buffer = stackalloc byte[8];
        var ok = TryRead(address, buffer);
        value = ok ? BinaryPrimitives.ReadUInt64LittleEndian(buffer) : 0;
        return ok;
    }
}
