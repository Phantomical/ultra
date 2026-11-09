// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace Ultra.Core.Etw.UserMode;

/// <summary>
/// The ETW events the user-mode sampler writes into the user session in place of the kernel events (process,
/// thread and image rundown, CPU samples with their stacks) when the profiler runs without the kernel session.
/// </summary>
/// <remarks>
/// The provider has no manifest: the payloads are packed little-endian structures read back by
/// <see cref="EtlTraceReader"/> by event id. Every payload starts with the id of the sampled process, as the events
/// themselves are raised by the profiler's process. Strings are UTF-16 and null-terminated. This layout is part of
/// the trace format: change it only additively (new event ids, or fields appended at the end).
/// </remarks>
internal static class UserModeSamplerEvents
{
    /// <summary>The provider id (<c>Ultra-UserModeSampler</c>).</summary>
    public static readonly Guid ProviderGuid = new("7a1c2f9e-5b3d-4e8a-9c61-2d4f8b0e6a13");

    /// <summary>The sampled process: <c>u32 pid, f32 samplingIntervalMs, string imagePath</c>.</summary>
    public const int ProcessInfoId = 1;

    /// <summary>A thread found in the process: <c>u32 pid, u32 tid, u8 flags (<see cref="FlagRundown"/>), string name</c>.</summary>
    public const int ThreadStartId = 2;

    /// <summary>A thread that disappeared from the process: <c>u32 pid, u32 tid</c>.</summary>
    public const int ThreadStopId = 3;

    /// <summary>An image found in the process: <c>u32 pid, u64 base, u32 size, u32 checksum, u32 timeDateStamp, u8 flags, string path</c>.</summary>
    public const int ModuleLoadId = 4;

    /// <summary>An image that disappeared from the process: <c>u32 pid, u64 base</c>.</summary>
    public const int ModuleUnloadId = 5;

    /// <summary>A CPU sample: <c>u32 pid, u32 tid, u16 flags, u16 frameCount, u64[frameCount] frames</c> (leaf first, return addresses after the first).</summary>
    public const int SampleId = 6;

    /// <summary>The thread/module was already there when sampling started.</summary>
    public const byte FlagRundown = 1;

    /// <summary>The stack walk of a sample stopped before reaching the thread's entry point.</summary>
    public const ushort SampleFlagTruncated = 1;

    /// <summary>The maximum number of frames in a sample.</summary>
    public const int MaxFrames = 1024;

    public readonly record struct ProcessInfo(int ProcessId, float SamplingIntervalMs, string ImagePath);

    public readonly record struct ThreadStart(int ProcessId, int ThreadId, byte Flags, string Name);

    public readonly record struct ModuleLoad(int ProcessId, ulong ImageBase, uint ImageSize, uint Checksum, uint TimeDateStamp, byte Flags, string Path);

    // --- Encoding ---

    public static int EncodeProcessInfo(Span<byte> buffer, in ProcessInfo info)
    {
        BinaryPrimitives.WriteInt32LittleEndian(buffer, info.ProcessId);
        BinaryPrimitives.WriteSingleLittleEndian(buffer.Slice(4), info.SamplingIntervalMs);
        return 8 + WriteString(buffer.Slice(8), info.ImagePath);
    }

    public static int EncodeThreadStart(Span<byte> buffer, in ThreadStart info)
    {
        BinaryPrimitives.WriteInt32LittleEndian(buffer, info.ProcessId);
        BinaryPrimitives.WriteInt32LittleEndian(buffer.Slice(4), info.ThreadId);
        buffer[8] = info.Flags;
        return 9 + WriteString(buffer.Slice(9), info.Name);
    }

    public static int EncodeThreadStop(Span<byte> buffer, int processId, int threadId)
    {
        BinaryPrimitives.WriteInt32LittleEndian(buffer, processId);
        BinaryPrimitives.WriteInt32LittleEndian(buffer.Slice(4), threadId);
        return 8;
    }

    public static int EncodeModuleLoad(Span<byte> buffer, in ModuleLoad info)
    {
        BinaryPrimitives.WriteInt32LittleEndian(buffer, info.ProcessId);
        BinaryPrimitives.WriteUInt64LittleEndian(buffer.Slice(4), info.ImageBase);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.Slice(12), info.ImageSize);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.Slice(16), info.Checksum);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.Slice(20), info.TimeDateStamp);
        buffer[24] = info.Flags;
        return 25 + WriteString(buffer.Slice(25), info.Path);
    }

    public static int EncodeModuleUnload(Span<byte> buffer, int processId, ulong imageBase)
    {
        BinaryPrimitives.WriteInt32LittleEndian(buffer, processId);
        BinaryPrimitives.WriteUInt64LittleEndian(buffer.Slice(4), imageBase);
        return 12;
    }

    public static int EncodeSample(Span<byte> buffer, int processId, int threadId, ushort flags, ReadOnlySpan<ulong> frames)
    {
        var count = Math.Min(frames.Length, MaxFrames);
        BinaryPrimitives.WriteInt32LittleEndian(buffer, processId);
        BinaryPrimitives.WriteInt32LittleEndian(buffer.Slice(4), threadId);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.Slice(8), flags);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.Slice(10), (ushort)count);
        MemoryMarshal.AsBytes(frames.Slice(0, count)).CopyTo(buffer.Slice(12));
        return 12 + count * 8;
    }

    /// <summary>The buffer size that fits any event.</summary>
    public const int MaxEventSize = 12 + MaxFrames * 8 + 2 * 1024;

    private static int WriteString(Span<byte> buffer, string text)
    {
        // Paths and names are capped so that every event fits MaxEventSize.
        if (text.Length > 1000)
        {
            text = text.Substring(0, 1000);
        }

        var length = Encoding.Unicode.GetBytes(text, buffer);
        buffer[length] = 0;
        buffer[length + 1] = 0;
        return length + 2;
    }

    // --- Decoding ---

    public static ProcessInfo DecodeProcessInfo(ReadOnlySpan<byte> data)
        => new(BinaryPrimitives.ReadInt32LittleEndian(data), BinaryPrimitives.ReadSingleLittleEndian(data.Slice(4)), ReadString(data.Slice(8)));

    public static ThreadStart DecodeThreadStart(ReadOnlySpan<byte> data)
        => new(BinaryPrimitives.ReadInt32LittleEndian(data), BinaryPrimitives.ReadInt32LittleEndian(data.Slice(4)), data[8], ReadString(data.Slice(9)));

    public static (int ProcessId, int ThreadId) DecodeThreadStop(ReadOnlySpan<byte> data)
        => (BinaryPrimitives.ReadInt32LittleEndian(data), BinaryPrimitives.ReadInt32LittleEndian(data.Slice(4)));

    public static ModuleLoad DecodeModuleLoad(ReadOnlySpan<byte> data)
        => new(
            BinaryPrimitives.ReadInt32LittleEndian(data),
            BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(4)),
            BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(12)),
            BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(16)),
            BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(20)),
            data[24],
            ReadString(data.Slice(25)));

    public static (int ProcessId, ulong ImageBase) DecodeModuleUnload(ReadOnlySpan<byte> data)
        => (BinaryPrimitives.ReadInt32LittleEndian(data), BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(4)));

    /// <summary>Decodes a sample; <paramref name="frames"/> views the frames in <paramref name="data"/> (leaf first).</summary>
    public static (int ProcessId, int ThreadId, ushort Flags) DecodeSample(ReadOnlySpan<byte> data, out ReadOnlySpan<ulong> frames)
    {
        var count = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(10));
        count = (ushort)Math.Min(count, (data.Length - 12) / 8);
        frames = MemoryMarshal.Cast<byte, ulong>(data.Slice(12, count * 8));
        return (BinaryPrimitives.ReadInt32LittleEndian(data), BinaryPrimitives.ReadInt32LittleEndian(data.Slice(4)), BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(8)));
    }

    private static string ReadString(ReadOnlySpan<byte> data)
    {
        var chars = MemoryMarshal.Cast<byte, char>(data.Slice(0, data.Length & ~1));
        var end = chars.IndexOf('\0');
        return new string(end >= 0 ? chars.Slice(0, end) : chars);
    }
}

/// <summary>Writes the <see cref="UserModeSamplerEvents"/> through a raw ETW provider registration. Not thread safe.</summary>
[SupportedOSPlatform("windows")]
internal sealed unsafe class UserModeSamplerEventWriter : IDisposable
{
    private long _handle;
    private readonly byte[] _buffer = new byte[UserModeSamplerEvents.MaxEventSize];

    public UserModeSamplerEventWriter()
    {
        var result = UserModeNative.EventRegister(UserModeSamplerEvents.ProviderGuid, IntPtr.Zero, IntPtr.Zero, out _handle);
        if (result != 0)
        {
            throw new InvalidOperationException($"Unable to register the user-mode sampler ETW provider (error {result})");
        }
    }

    /// <summary>The number of events ETW failed to write (e.g. no free buffer).</summary>
    public int FailedWrites { get; private set; }

    public Span<byte> Buffer => _buffer;

    public void Write(int eventId, int length)
    {
        var descriptor = new UserModeNative.EVENT_DESCRIPTOR { Id = (ushort)eventId, Level = 4 };
        fixed (byte* buffer = _buffer)
        {
            var data = new UserModeNative.EVENT_DATA_DESCRIPTOR { Ptr = (ulong)buffer, Size = (uint)length };
            if (UserModeNative.EventWrite(_handle, descriptor, 1, &data) != 0)
            {
                FailedWrites++;
            }
        }
    }

    public void Dispose()
    {
        if (_handle != 0)
        {
            UserModeNative.EventUnregister(_handle);
            _handle = 0;
        }
    }
}
