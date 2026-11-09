// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

using System.Buffers.Binary;

namespace Ultra.Core.Etw.UserMode;

/// <summary>A function entry (<c>RUNTIME_FUNCTION</c>) with the base its RVAs are relative to.</summary>
internal readonly record struct X64RuntimeFunction(ulong ImageBase, uint BeginAddress, uint EndAddress, uint UnwindData);

/// <summary>The code of the process whose stacks are unwound, as seen by <see cref="X64Unwinder"/>.</summary>
internal interface IX64CodeMap
{
    /// <summary>Finds the function entry covering <paramref name="pc"/>, from an image's exception directory or a dynamic function table.</summary>
    bool TryFindFunction(ulong pc, out X64RuntimeFunction function);

    /// <summary>Whether <paramref name="pc"/> lies in a loaded image (where code without a function entry is a leaf function).</summary>
    bool IsInImage(ulong pc);

    /// <summary>Whether <paramref name="address"/> is executable memory.</summary>
    bool IsCode(ulong address);

    /// <summary>Reads memory of the process (code bytes, unwind info).</summary>
    bool TryRead(ulong address, Span<byte> buffer);
}

/// <summary>A copy of the used part of a thread's stack, taken while the thread was suspended.</summary>
internal sealed class X64StackSnapshot
{
    public X64StackSnapshot(int capacity)
    {
        Data = new byte[capacity];
    }

    /// <summary>The address of <see cref="Data"/>[0] (the stack pointer when the copy was taken).</summary>
    public ulong Base { get; set; }

    public byte[] Data { get; }

    public int Length { get; set; }

    public ulong End => Base + (ulong)Length;

    public bool TryRead(ulong address, out ulong value)
    {
        var offset = address - Base;
        if (address < Base || offset + 8 > (ulong)Length)
        {
            value = 0;
            return false;
        }

        value = BinaryPrimitives.ReadUInt64LittleEndian(Data.AsSpan((int)offset));
        return true;
    }
}

/// <summary>Why a stack walk stopped.</summary>
internal enum X64UnwindStop
{
    /// <summary>The walk reached a null return address or the top of the stack (a complete stack).</summary>
    End,

    /// <summary>The frame limit was reached.</summary>
    MaxFrames,

    /// <summary>A stack read fell outside the copied stack.</summary>
    StackRead,

    /// <summary>The caller found is not executable code.</summary>
    BadReturnAddress,

    /// <summary>The stack pointer did not move up.</summary>
    StackPointerNotIncreasing,

    /// <summary>The unwind info of a function could not be read or is malformed.</summary>
    BadUnwindInfo,

    /// <summary>A frame in code without unwind info could not be walked through its frame pointer.</summary>
    NoUnwindInfo,
}

/// <summary>
/// Walks x64 Windows stacks from a thread context and a copy of its stack, outside of the thread's process, like
/// <c>RtlVirtualUnwind</c>: the unwind codes of the function entries (images and dynamic function tables, which is
/// where JIT compilers such as Mono register their code), with the epilog detection of the leaf frame, a leaf
/// function rule for image code without unwind info, and a frame-pointer fallback for other code without it.
/// </summary>
/// <remarks>Not thread safe (it caches the unwind info it reads).</remarks>
internal sealed class X64Unwinder
{
    private const int UNW_FLAG_CHAININFO = 4;

    private const int RegRsp = 4;
    private const int RegRbp = 5;

    private readonly IX64CodeMap _codeMap;
    private readonly Dictionary<ulong, byte[]?> _unwindInfoCache = new();

    public X64Unwinder(IX64CodeMap codeMap)
    {
        _codeMap = codeMap;
    }

    /// <summary>
    /// Walks the stack. <paramref name="registers"/> holds the 16 integer registers in unwind-code order (Rax, Rcx,
    /// Rdx, Rbx, Rsp, Rbp, Rsi, Rdi, R8-R15) and is clobbered.
    /// </summary>
    /// <returns>The number of frames written to <paramref name="frames"/>: the instruction pointer, then the return addresses.</returns>
    public int Walk(Span<ulong> registers, ulong rip, X64StackSnapshot stack, Span<ulong> frames, out X64UnwindStop stop)
    {
        var count = 0;
        var isLeaf = true;
        while (true)
        {
            if (count == frames.Length)
            {
                stop = X64UnwindStop.MaxFrames;
                return count;
            }

            frames[count++] = rip;
            var rsp = registers[RegRsp];
            if (!UnwindFrame(registers, ref rip, stack, isLeaf, out stop))
            {
                return count;
            }

            isLeaf = false;
            if (rip == 0 || registers[RegRsp] >= stack.End)
            {
                stop = X64UnwindStop.End;
                return count;
            }

            if (registers[RegRsp] <= rsp)
            {
                stop = X64UnwindStop.StackPointerNotIncreasing;
                return count;
            }

            if (!_codeMap.IsCode(rip))
            {
                stop = X64UnwindStop.BadReturnAddress;
                return count;
            }
        }
    }

    private bool UnwindFrame(Span<ulong> registers, ref ulong rip, X64StackSnapshot stack, bool isLeaf, out X64UnwindStop stop)
    {
        stop = X64UnwindStop.End;
        if (!_codeMap.TryFindFunction(rip, out var function))
        {
            if (_codeMap.IsInImage(rip))
            {
                // A leaf function: no stack allocation, the return address is on top of the stack.
                return PopReturnAddress(registers, ref rip, stack, out stop);
            }

            return UnwindWithoutUnwindInfo(registers, ref rip, stack, isLeaf, out stop);
        }

        var functionStart = function.ImageBase + function.BeginAddress;
        var info = GetUnwindInfo(function.ImageBase + function.UnwindData);
        if (info is null)
        {
            stop = X64UnwindStop.BadUnwindInfo;
            return false;
        }

        var sizeOfProlog = info[1];
        var offsetInFunction = rip - functionStart;
        var inProlog = offsetInFunction < sizeOfProlog;
        if (isLeaf && !inProlog && TryUnwindEpilog(registers, ref rip, stack, info, out var epilogResult))
        {
            stop = epilogResult ? X64UnwindStop.End : X64UnwindStop.StackRead;
            return epilogResult;
        }

        // The frame base the saved registers are addressed from: the frame register once the prolog set it.
        var frame = registers[RegRsp];
        var frameRegister = info[3] & 0xF;
        if (frameRegister != 0)
        {
            var setFpOffset = FindSetFpRegOffset(info);
            if (setFpOffset >= 0 && (!inProlog || offsetInFunction >= (ulong)setFpOffset))
            {
                frame = registers[frameRegister] - (ulong)((info[3] >> 4) * 16);
            }
        }

        var prologOffset = inProlog ? offsetInFunction : ulong.MaxValue;
        var machineFrame = false;
        for (var chain = 0; chain < 32; chain++)
        {
            if (!ApplyUnwindCodes(registers, ref rip, stack, info, prologOffset, frame, ref machineFrame, out stop))
            {
                return false;
            }

            if ((info[0] >> 3 & UNW_FLAG_CHAININFO) == 0)
            {
                break;
            }

            // The chained function entry follows the (even-padded) unwind codes; all its codes apply.
            var chainedOffset = 4 + ((info[2] + 1) & ~1) * 2;
            if (info.Length < chainedOffset + 12)
            {
                stop = X64UnwindStop.BadUnwindInfo;
                return false;
            }

            var unwindData = BinaryPrimitives.ReadUInt32LittleEndian(info.AsSpan(chainedOffset + 8));
            info = GetUnwindInfo(function.ImageBase + unwindData);
            if (info is null)
            {
                stop = X64UnwindStop.BadUnwindInfo;
                return false;
            }

            prologOffset = ulong.MaxValue;
        }

        return machineFrame || PopReturnAddress(registers, ref rip, stack, out stop);
    }

    private static int FindSetFpRegOffset(byte[] info)
    {
        var count = info[2];
        var version = info[0] & 7;
        for (var i = 0; i < count;)
        {
            var op = info[4 + i * 2 + 1] & 0xF;
            if (op == 3)
            {
                return info[4 + i * 2];
            }

            i += GetSlotCount(op, info[4 + i * 2 + 1] >> 4, version);
        }

        return -1;
    }

    private static int GetSlotCount(int op, int opInfo, int version) => op switch
    {
        1 => opInfo == 0 ? 2 : 3, // UWOP_ALLOC_LARGE
        4 => 2, // UWOP_SAVE_NONVOL
        5 => 3, // UWOP_SAVE_NONVOL_FAR
        6 => version == 1 ? 2 : 1, // UWOP_SAVE_XMM (v1) / UWOP_EPILOG (v2)
        7 => 3, // UWOP_SAVE_XMM_FAR (v1) / UWOP_SPARE_CODE (v2)
        8 => 2, // UWOP_SAVE_XMM128
        9 => 3, // UWOP_SAVE_XMM128_FAR
        _ => 1,
    };

    private static bool ApplyUnwindCodes(Span<ulong> registers, ref ulong rip, X64StackSnapshot stack, byte[] info, ulong prologOffset, ulong frame, ref bool machineFrame, out X64UnwindStop stop)
    {
        stop = X64UnwindStop.End;
        var count = info[2];
        var version = info[0] & 7;
        if (info.Length < 4 + count * 2)
        {
            stop = X64UnwindStop.BadUnwindInfo;
            return false;
        }

        for (var i = 0; i < count;)
        {
            var codeOffset = info[4 + i * 2];
            var op = info[4 + i * 2 + 1] & 0xF;
            var opInfo = info[4 + i * 2 + 1] >> 4;
            var slots = GetSlotCount(op, opInfo, version);
            if (i + slots > count)
            {
                stop = X64UnwindStop.BadUnwindInfo;
                return false;
            }

            // An operation of the prolog not executed yet at the current instruction is skipped.
            if (prologOffset != ulong.MaxValue && prologOffset < codeOffset)
            {
                i += slots;
                continue;
            }

            var operand = info.AsSpan(4 + (i + 1) * 2);
            switch (op)
            {
                case 0: // UWOP_PUSH_NONVOL
                    if (!stack.TryRead(registers[RegRsp], out var value))
                    {
                        stop = X64UnwindStop.StackRead;
                        return false;
                    }

                    registers[opInfo] = value;
                    registers[RegRsp] += 8;
                    break;
                case 1: // UWOP_ALLOC_LARGE
                    registers[RegRsp] += opInfo == 0 ? BinaryPrimitives.ReadUInt16LittleEndian(operand) * 8u : BinaryPrimitives.ReadUInt32LittleEndian(operand);
                    break;
                case 2: // UWOP_ALLOC_SMALL
                    registers[RegRsp] += (ulong)(opInfo * 8 + 8);
                    break;
                case 3: // UWOP_SET_FPREG
                    registers[RegRsp] = frame;
                    break;
                case 4: // UWOP_SAVE_NONVOL
                case 5: // UWOP_SAVE_NONVOL_FAR
                    var offset = op == 4 ? BinaryPrimitives.ReadUInt16LittleEndian(operand) * 8u : BinaryPrimitives.ReadUInt32LittleEndian(operand);
                    if (!stack.TryRead(frame + offset, out value))
                    {
                        stop = X64UnwindStop.StackRead;
                        return false;
                    }

                    registers[opInfo] = value;
                    break;
                case 10: // UWOP_PUSH_MACHFRAME
                    var rsp = registers[RegRsp] + (opInfo != 0 ? 8u : 0u);
                    if (!stack.TryRead(rsp, out var newRip) || !stack.TryRead(rsp + 24, out var newRsp))
                    {
                        stop = X64UnwindStop.StackRead;
                        return false;
                    }

                    rip = newRip;
                    registers[RegRsp] = newRsp;
                    machineFrame = true;
                    break;
                // XMM saves and v2 epilog descriptions do not move the integer registers.
            }

            i += slots;
        }

        return true;
    }

    private static bool PopReturnAddress(Span<ulong> registers, ref ulong rip, X64StackSnapshot stack, out X64UnwindStop stop)
    {
        if (!stack.TryRead(registers[RegRsp], out rip))
        {
            stop = X64UnwindStop.StackRead;
            return false;
        }

        registers[RegRsp] += 8;
        stop = X64UnwindStop.End;
        return true;
    }

    /// <summary>
    /// Code without unwind info outside of the images (JIT stubs and trampolines): a leaf frame whose top of stack
    /// is a return address into code, else a walk through the frame pointer chain.
    /// </summary>
    private bool UnwindWithoutUnwindInfo(Span<ulong> registers, ref ulong rip, X64StackSnapshot stack, bool isLeaf, out X64UnwindStop stop)
    {
        var rsp = registers[RegRsp];
        if (isLeaf && stack.TryRead(rsp, out var top) && _codeMap.IsCode(top))
        {
            rip = top;
            registers[RegRsp] = rsp + 8;
            stop = X64UnwindStop.End;
            return true;
        }

        var rbp = registers[RegRbp];
        if (rbp >= rsp && stack.TryRead(rbp, out var savedRbp) && stack.TryRead(rbp + 8, out var returnAddress) && _codeMap.IsCode(returnAddress))
        {
            registers[RegRbp] = savedRbp;
            registers[RegRsp] = rbp + 16;
            rip = returnAddress;
            stop = X64UnwindStop.End;
            return true;
        }

        stop = X64UnwindStop.NoUnwindInfo;
        return false;
    }

    /// <summary>
    /// Emulates the rest of the epilog when <paramref name="rip"/> is in one: an optional <c>add rsp, imm</c> or
    /// <c>lea rsp, [frame + disp]</c>, then <c>pop</c>s, then a <c>ret</c> (or a tail-call <c>jmp</c> after any of
    /// the previous), the only shapes the x64 ABI allows.
    /// </summary>
    /// <returns>Whether <paramref name="rip"/> is in an epilog; <paramref name="succeeded"/> tells whether it was unwound.</returns>
    private bool TryUnwindEpilog(Span<ulong> registers, ref ulong rip, X64StackSnapshot stack, byte[] info, out bool succeeded)
    {
        succeeded = false;
        Span<byte> code = stackalloc byte[32];
        if (!_codeMap.TryRead(rip, code))
        {
            return false;
        }

        var rsp = registers[RegRsp];
        var i = 0;
        var adjusted = false;
        var frameRegister = info[3] & 0xF;

        if ((code[0] & 0xF8) == 0x48 && code[1] == 0x83 && code[2] == 0xC4)
        {
            rsp += (ulong)(long)(sbyte)code[3];
            i = 4;
            adjusted = true;
        }
        else if ((code[0] & 0xF8) == 0x48 && code[1] == 0x81 && code[2] == 0xC4)
        {
            rsp += (ulong)(long)BinaryPrimitives.ReadInt32LittleEndian(code.Slice(3));
            i = 7;
            adjusted = true;
        }
        else if ((code[0] & 0xFE) == 0x48 && code[1] == 0x8D && frameRegister != 0)
        {
            // lea rsp, [frameRegister + disp8/disp32]: ModRM reg = rsp (100), rm = the frame register.
            var modrm = code[2];
            var rm = (modrm & 7) | ((code[0] & 1) << 3);
            var mod = modrm >> 6;
            if ((modrm >> 3 & 7) != 4 || rm != frameRegister || (rm & 7) == 4 || mod is 0 or 3)
            {
                return false;
            }

            var displacement = mod == 1 ? (long)(sbyte)code[3] : BinaryPrimitives.ReadInt32LittleEndian(code.Slice(3));
            rsp = registers[frameRegister] + (ulong)displacement;
            i = mod == 1 ? 4 : 7;
            adjusted = true;
        }

        Span<int> poppedRegisters = stackalloc int[16];
        var popCount = 0;
        while (i < code.Length - 2 && popCount < 16)
        {
            if (code[i] >= 0x58 && code[i] <= 0x5F)
            {
                poppedRegisters[popCount++] = code[i] - 0x58;
                i++;
            }
            else if (code[i] == 0x41 && code[i + 1] >= 0x58 && code[i + 1] <= 0x5F)
            {
                poppedRegisters[popCount++] = code[i + 1] - 0x58 + 8;
                i += 2;
            }
            else
            {
                break;
            }
        }

        var isReturn = code[i] is 0xC3 or 0xC2 || (code[i] == 0xF3 && code[i + 1] == 0xC3);
        var isJump = code[i] is 0xE9 or 0xEB || (code[i] == 0xFF && code[i + 1] == 0x25) || ((code[i] & 0xF0) == 0x40 && code[i + 1] == 0xFF && code[i + 2] == 0x25);
        if (!isReturn && !(isJump && (adjusted || popCount > 0)))
        {
            return false;
        }

        for (var p = 0; p < popCount; p++)
        {
            if (!stack.TryRead(rsp, out var value))
            {
                return true;
            }

            registers[poppedRegisters[p]] = value;
            rsp += 8;
        }

        if (!stack.TryRead(rsp, out var returnAddress))
        {
            return true;
        }

        rip = returnAddress;
        registers[RegRsp] = rsp + 8;
        succeeded = true;
        return true;
    }

    private byte[]? GetUnwindInfo(ulong address)
    {
        if (_unwindInfoCache.TryGetValue(address, out var info))
        {
            return info;
        }

        // The header gives the count of unwind codes; a chained entry may follow them.
        Span<byte> header = stackalloc byte[4];
        info = null;
        if (_codeMap.TryRead(address, header) && (header[0] & 7) is 1 or 2)
        {
            var size = 4 + ((header[2] + 1) & ~1) * 2 + ((header[0] >> 3 & UNW_FLAG_CHAININFO) != 0 ? 12 : 0);
            var buffer = new byte[size];
            if (_codeMap.TryRead(address, buffer))
            {
                info = buffer;
            }
        }

        _unwindInfoCache[address] = info;
        return info;
    }
}
