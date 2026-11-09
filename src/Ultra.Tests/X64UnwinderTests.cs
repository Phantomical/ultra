// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

using System.Buffers.Binary;
using Ultra.Core.Etw.UserMode;

namespace Ultra.Tests;

/// <summary>
/// Tests for <see cref="X64Unwinder"/> on synthetic code: unwind codes (pushes, allocations, frame register, partial
/// prologs, chained entries), epilog emulation, the leaf-function rule and the frame-pointer fallback.
/// </summary>
[TestClass]
public class X64UnwinderTests
{
    private const ulong ImageBase = 0x7FF6_0000_0000;
    private const ulong StackBase = 0x0000_00F2_0000_0000;
    private const int Rbx = 3;
    private const int Rsp = 4;
    private const int Rbp = 5;

    /// <summary>A code map over a single image, a dynamic code region and a sparse memory.</summary>
    private sealed class FakeCodeMap : IX64CodeMap
    {
        public List<X64RuntimeFunction> Functions { get; } = new();
        public Dictionary<ulong, byte[]> Memory { get; } = new();
        public (ulong Start, ulong End) Image { get; set; } = (ImageBase, ImageBase + 0x100000);
        public (ulong Start, ulong End) DynamicCode { get; set; } = (0x2000_0000, 0x2010_0000);

        public bool TryFindFunction(ulong pc, out X64RuntimeFunction function)
        {
            foreach (var f in Functions)
            {
                if (pc >= f.ImageBase + f.BeginAddress && pc < f.ImageBase + f.EndAddress)
                {
                    function = f;
                    return true;
                }
            }

            function = default;
            return false;
        }

        public bool IsInImage(ulong pc) => pc >= Image.Start && pc < Image.End;

        public bool IsCode(ulong address) => IsInImage(address) || (address >= DynamicCode.Start && address < DynamicCode.End);

        public bool TryRead(ulong address, Span<byte> buffer)
        {
            foreach (var (start, bytes) in Memory)
            {
                if (address >= start && address + (ulong)buffer.Length <= start + (ulong)bytes.Length)
                {
                    bytes.AsSpan((int)(address - start), buffer.Length).CopyTo(buffer);
                    return true;
                }
            }

            return false;
        }

        /// <summary>Adds a function with its unwind info (header + codes, see <see cref="UnwindInfo"/>).</summary>
        public void AddFunction(ulong imageBase, uint begin, uint end, uint unwindRva, byte[] unwindInfo)
        {
            Functions.Add(new X64RuntimeFunction(imageBase, begin, end, unwindRva));
            Memory[imageBase + unwindRva] = unwindInfo;
        }
    }

    /// <summary>Builds an UNWIND_INFO: version 1, the codes as (offset, op, info) slots, then the extra slots (operands).</summary>
    private static byte[] UnwindInfo(byte sizeOfProlog, int frameRegister, int frameOffset, params ushort[] slots)
    {
        var count = slots.Length;
        var bytes = new byte[4 + ((count + 1) & ~1) * 2];
        bytes[0] = 1;
        bytes[1] = sizeOfProlog;
        bytes[2] = (byte)count;
        bytes[3] = (byte)(frameRegister | (frameOffset << 4));
        for (var i = 0; i < count; i++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(4 + i * 2), slots[i]);
        }

        return bytes;
    }

    private static ushort Code(byte offset, int op, int info) => (ushort)(offset | ((op | (info << 4)) << 8));

    private sealed class Stack
    {
        private readonly ulong[] _slots = new ulong[64];

        public ulong this[int slot]
        {
            set => _slots[slot] = value;
        }

        public static ulong Address(int slot) => StackBase + (ulong)slot * 8;

        public X64StackSnapshot Snapshot()
        {
            var snapshot = new X64StackSnapshot(_slots.Length * 8) { Base = StackBase, Length = _slots.Length * 8 };
            for (var i = 0; i < _slots.Length; i++)
            {
                BinaryPrimitives.WriteUInt64LittleEndian(snapshot.Data.AsSpan(i * 8), _slots[i]);
            }

            return snapshot;
        }
    }

    private static int Walk(FakeCodeMap map, ulong[] registers, ulong rip, Stack stack, out ulong[] frames, out X64UnwindStop stop)
    {
        var buffer = new ulong[32];
        var count = new X64Unwinder(map).Walk(registers, rip, stack.Snapshot(), buffer, out stop);
        frames = buffer.AsSpan(0, count).ToArray();
        return count;
    }

    // push rbp; push rbx; sub rsp, 0x28  =>  codes: ALLOC_SMALL(0x28) @6, PUSH rbx @2, PUSH rbp @1
    private static FakeCodeMap MapWithPushAllocFunction()
    {
        var map = new FakeCodeMap();
        map.AddFunction(ImageBase, 0x1000, 0x1100, 0x8000, UnwindInfo(6, 0, 0, Code(6, 2, 4), Code(2, 0, Rbx), Code(1, 0, Rbp)));
        map.Memory[ImageBase + 0x1000] = new byte[0x100]; // the function's code (no epilog at the sampled pc)
        return map;
    }

    [TestMethod]
    public void UnwindsPushesAndAllocationThenLeafCaller()
    {
        var map = MapWithPushAllocFunction();
        var stack = new Stack();
        // rsp -> 0x28 bytes of locals, then saved rbx, saved rbp, the return address into a leaf function of the image.
        stack[5] = 0xBBBB;
        stack[6] = 0xAAAA;
        stack[7] = ImageBase + 0x5000;
        // That caller has no function entry (a leaf): its return address is on top of the stack, then the end.
        stack[8] = 0;

        var registers = new ulong[16];
        registers[Rsp] = Stack.Address(0);
        var count = Walk(map, registers, ImageBase + 0x1050, stack, out var frames, out var stop);

        Assert.AreEqual(2, count);
        CollectionAssert.AreEqual(new[] { ImageBase + 0x1050, ImageBase + 0x5000 }, frames);
        Assert.AreEqual(X64UnwindStop.End, stop);
        Assert.AreEqual(0xBBBBUL, registers[Rbx]);
        Assert.AreEqual(0xAAAAUL, registers[Rbp]);
    }

    [TestMethod]
    public void SkipsPrologOperationsNotExecutedYet()
    {
        var map = MapWithPushAllocFunction();
        var stack = new Stack();
        // At offset 1 only `push rbp` has executed: rsp -> saved rbp, then the return address.
        stack[0] = 0xAAAA;
        stack[1] = ImageBase + 0x5000;
        stack[2] = 0;

        var registers = new ulong[16];
        registers[Rsp] = Stack.Address(0);
        registers[Rbx] = 0x1234;
        Walk(map, registers, ImageBase + 0x1001, stack, out var frames, out _);

        CollectionAssert.AreEqual(new[] { ImageBase + 0x1001, ImageBase + 0x5000 }, frames);
        Assert.AreEqual(0x1234UL, registers[Rbx], "rbx was not pushed yet at this point of the prolog");
        Assert.AreEqual(0xAAAAUL, registers[Rbp]);
    }

    [TestMethod]
    public void UnwindsThroughFrameRegister()
    {
        // push rbp; sub rsp, 0x40; lea rbp, [rsp + 0x20]  =>  SET_FPREG(rbp, offset 2*16) @9, ALLOC_SMALL(0x40) @5, PUSH rbp @1
        var map = new FakeCodeMap();
        map.AddFunction(ImageBase, 0x2000, 0x2100, 0x8100, UnwindInfo(9, Rbp, 2, Code(9, 3, 0), Code(5, 2, 7), Code(1, 0, Rbp)));
        map.Memory[ImageBase + 0x2000] = new byte[0x100];
        var stack = new Stack();
        // After the prolog: rsp = S, rbp = S + 0x20; saved rbp at S + 0x40, return address at S + 0x48.
        stack[8] = 0xCCCC;
        stack[9] = ImageBase + 0x5000;
        stack[10] = 0;

        var registers = new ulong[16];
        registers[Rsp] = Stack.Address(0);
        registers[Rbp] = Stack.Address(4);
        Walk(map, registers, ImageBase + 0x2080, stack, out var frames, out var stop);

        CollectionAssert.AreEqual(new[] { ImageBase + 0x2080, ImageBase + 0x5000 }, frames);
        Assert.AreEqual(X64UnwindStop.End, stop);
        Assert.AreEqual(0xCCCCUL, registers[Rbp]);
    }

    [TestMethod]
    public void EmulatesEpilogOfLeafFrame()
    {
        var map = MapWithPushAllocFunction();
        // add rsp, 0x28; pop rbx; pop rbp; ret
        var code = new byte[0x100];
        new byte[] { 0x48, 0x83, 0xC4, 0x28, 0x5B, 0x5D, 0xC3 }.CopyTo(code, 0x80);
        map.Memory[ImageBase + 0x1000] = code;
        var stack = new Stack();
        stack[5] = 0xBBBB;
        stack[6] = 0xAAAA;
        stack[7] = ImageBase + 0x5000;
        stack[8] = 0;

        var registers = new ulong[16];
        registers[Rsp] = Stack.Address(0);
        Walk(map, registers, ImageBase + 0x1080, stack, out var frames, out _);

        CollectionAssert.AreEqual(new[] { ImageBase + 0x1080, ImageBase + 0x5000 }, frames);
        Assert.AreEqual(0xBBBBUL, registers[Rbx]);
        Assert.AreEqual(0xAAAAUL, registers[Rbp]);
    }

    [TestMethod]
    public void EmulatesEpilogAfterTheAllocationIsUndone()
    {
        var map = MapWithPushAllocFunction();
        // The sampled instruction is the `pop rbp` of the epilog: only rbp and the return address remain.
        var code = new byte[0x100];
        new byte[] { 0x5D, 0xC3 }.CopyTo(code, 0x85);
        map.Memory[ImageBase + 0x1000] = code;
        var stack = new Stack();
        stack[0] = 0xAAAA;
        stack[1] = ImageBase + 0x5000;
        stack[2] = 0;

        var registers = new ulong[16];
        registers[Rsp] = Stack.Address(0);
        Walk(map, registers, ImageBase + 0x1085, stack, out var frames, out _);

        CollectionAssert.AreEqual(new[] { ImageBase + 0x1085, ImageBase + 0x5000 }, frames);
        Assert.AreEqual(0xAAAAUL, registers[Rbp]);
    }

    [TestMethod]
    public void FollowsChainedUnwindInfo()
    {
        // The primary entry (a function's cold part) pushes rbx; its chained entry (the function's main part) pushes rbp.
        var map = new FakeCodeMap();
        map.AddFunction(ImageBase, 0x3000, 0x3100, 0x8200, UnwindInfo(1, 0, 0, Code(1, 0, Rbp)));
        var primary = UnwindInfo(0, 0, 0, Code(0, 0, Rbx));
        primary[0] |= 4 << 3; // UNW_FLAG_CHAININFO
        var withChain = new byte[primary.Length + 12];
        primary.CopyTo(withChain, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(withChain.AsSpan(primary.Length), 0x3000);
        BinaryPrimitives.WriteUInt32LittleEndian(withChain.AsSpan(primary.Length + 4), 0x3100);
        BinaryPrimitives.WriteUInt32LittleEndian(withChain.AsSpan(primary.Length + 8), 0x8200);
        map.AddFunction(ImageBase, 0x4000, 0x4100, 0x8300, withChain);
        map.Memory[ImageBase + 0x4000] = new byte[0x100];

        var stack = new Stack();
        stack[0] = 0xBBBB;
        stack[1] = 0xAAAA;
        stack[2] = ImageBase + 0x5000;
        stack[3] = 0;

        var registers = new ulong[16];
        registers[Rsp] = Stack.Address(0);
        Walk(map, registers, ImageBase + 0x4010, stack, out var frames, out _);

        CollectionAssert.AreEqual(new[] { ImageBase + 0x4010, ImageBase + 0x5000 }, frames);
        Assert.AreEqual(0xBBBBUL, registers[Rbx]);
        Assert.AreEqual(0xAAAAUL, registers[Rbp]);
    }

    [TestMethod]
    public void WalksCodeWithoutUnwindInfoThroughFramePointer()
    {
        var map = MapWithPushAllocFunction();
        var stack = new Stack();
        // Frame 0 (image function) returns into dynamic code without unwind info, whose frame is an rbp chain:
        // rbp -> [saved rbp][return address into the image's leaf function].
        stack[5] = 0xBBBB;
        stack[6] = Stack.Address(10); // saved rbp: the dynamic code's frame
        stack[7] = 0x2000_1000;
        stack[10] = 0;
        stack[11] = ImageBase + 0x5000;
        stack[12] = 0;

        var registers = new ulong[16];
        registers[Rsp] = Stack.Address(0);
        Walk(map, registers, ImageBase + 0x1050, stack, out var frames, out var stop);

        CollectionAssert.AreEqual(new[] { ImageBase + 0x1050, 0x2000_1000UL, ImageBase + 0x5000 }, frames);
        Assert.AreEqual(X64UnwindStop.End, stop);
    }

    [TestMethod]
    public void StopsAtReturnAddressOutsideOfCode()
    {
        var map = MapWithPushAllocFunction();
        var stack = new Stack();
        stack[7] = 0x1234_5678; // not code

        var registers = new ulong[16];
        registers[Rsp] = Stack.Address(0);
        var count = Walk(map, registers, ImageBase + 0x1050, stack, out _, out var stop);

        Assert.AreEqual(1, count);
        Assert.AreEqual(X64UnwindStop.BadReturnAddress, stop);
    }

    [TestMethod]
    public void StopsWhenTheStackCopyEnds()
    {
        var map = MapWithPushAllocFunction();
        var registers = new ulong[16];
        // The function's frame extends past the copied stack.
        registers[Rsp] = Stack.Address(60);
        var count = Walk(map, registers, ImageBase + 0x1050, new Stack(), out _, out var stop);

        Assert.AreEqual(1, count);
        Assert.AreEqual(X64UnwindStop.StackRead, stop);
    }
}
