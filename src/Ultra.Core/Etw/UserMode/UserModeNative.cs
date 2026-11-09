// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Ultra.Core.Etw.UserMode;

/// <summary>The Win32 functions used by the user-mode sampler. Every handle is a raw <see cref="IntPtr"/> closed with <see cref="CloseHandle"/>.</summary>
[SupportedOSPlatform("windows")]
internal static partial class UserModeNative
{
    public const uint PROCESS_QUERY_INFORMATION = 0x0400;
    public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    public const uint PROCESS_VM_READ = 0x0010;

    public const uint THREAD_SUSPEND_RESUME = 0x0002;
    public const uint THREAD_GET_CONTEXT = 0x0008;
    public const uint THREAD_QUERY_INFORMATION = 0x0040;
    public const uint THREAD_QUERY_LIMITED_INFORMATION = 0x0800;

    public const uint SYNCHRONIZE = 0x00100000;
    public const uint WAIT_OBJECT_0 = 0;
    public const uint LIST_MODULES_ALL = 0x03;

    public const uint CREATE_WAITABLE_TIMER_HIGH_RESOLUTION = 0x00000002;
    public const uint TIMER_ALL_ACCESS = 0x1F0003;

    /// <summary>CONTEXT_AMD64 | CONTEXT_CONTROL | CONTEXT_INTEGER.</summary>
    public const uint CONTEXT_CONTROL_INTEGER = 0x00100003;

    /// <summary>The size of the x64 <c>CONTEXT</c> structure, which must be 16-byte aligned.</summary>
    public const int ContextSize = 1232;

    public const int ContextFlagsOffset = 0x30;

    /// <summary>The offset of <c>Rax</c>; the 16 integer registers follow in unwind-code register order (Rax, Rcx, Rdx, Rbx, Rsp, Rbp, Rsi, Rdi, R8-R15).</summary>
    public const int ContextIntegerRegistersOffset = 0x78;

    public const int ContextRipOffset = 0xF8;

    public const uint MEM_COMMIT = 0x1000;
    public const uint PAGE_EXECUTE = 0x10;
    public const uint PAGE_EXECUTE_READ = 0x20;
    public const uint PAGE_EXECUTE_READWRITE = 0x40;
    public const uint PAGE_EXECUTE_WRITECOPY = 0x80;

    [StructLayout(LayoutKind.Sequential)]
    public struct MODULEINFO
    {
        public IntPtr lpBaseOfDll;
        public uint SizeOfImage;
        public IntPtr EntryPoint;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MEMORY_BASIC_INFORMATION
    {
        public ulong BaseAddress;
        public ulong AllocationBase;
        public uint AllocationProtect;
        public ushort PartitionId;
        public ulong RegionSize;
        public uint State;
        public uint Protect;
        public uint Type;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct THREAD_BASIC_INFORMATION
    {
        public int ExitStatus;
        public IntPtr TebBaseAddress;
        public IntPtr UniqueProcess;
        public IntPtr UniqueThread;
        public IntPtr AffinityMask;
        public int Priority;
        public int BasePriority;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct EVENT_DESCRIPTOR
    {
        public ushort Id;
        public byte Version;
        public byte Channel;
        public byte Level;
        public byte Opcode;
        public ushort Task;
        public ulong Keyword;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct EVENT_DATA_DESCRIPTOR
    {
        public ulong Ptr;
        public uint Size;
        public uint Reserved;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial IntPtr OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial IntPtr OpenThread(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint threadId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CloseHandle(IntPtr handle);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial uint SuspendThread(IntPtr thread);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial uint ResumeThread(IntPtr thread);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static unsafe partial bool GetThreadContext(IntPtr thread, void* context);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static unsafe partial bool ReadProcessMemory(IntPtr process, ulong baseAddress, void* buffer, nuint size, out nuint bytesRead);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool QueryThreadCycleTime(IntPtr thread, out ulong cycleTime);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetThreadTimes(IntPtr thread, out long creationTime, out long exitTime, out long kernelTime, out long userTime);

    [LibraryImport("kernel32.dll")]
    public static partial int GetThreadDescription(IntPtr thread, out IntPtr description);

    [LibraryImport("kernel32.dll")]
    public static partial IntPtr LocalFree(IntPtr memory);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static unsafe partial bool QueryFullProcessImageNameW(IntPtr process, uint flags, char* exeName, ref uint size);

    [LibraryImport("kernel32.dll", EntryPoint = "K32EnumProcessModulesEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static unsafe partial bool EnumProcessModulesEx(IntPtr process, IntPtr* modules, uint size, out uint needed, uint filterFlag);

    [LibraryImport("kernel32.dll", EntryPoint = "K32GetModuleFileNameExW", SetLastError = true)]
    public static unsafe partial uint GetModuleFileNameExW(IntPtr process, IntPtr module, char* fileName, uint size);

    [LibraryImport("kernel32.dll", EntryPoint = "K32GetModuleInformation", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetModuleInformation(IntPtr process, IntPtr module, out MODULEINFO moduleInfo, uint size);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial nuint VirtualQueryEx(IntPtr process, ulong address, out MEMORY_BASIC_INFORMATION buffer, nuint length);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateWaitableTimerExW", SetLastError = true)]
    public static partial IntPtr CreateWaitableTimerEx(IntPtr attributes, IntPtr name, uint flags, uint desiredAccess);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetWaitableTimer(IntPtr timer, in long dueTime, int period, IntPtr completionRoutine, IntPtr argToCompletionRoutine, [MarshalAs(UnmanagedType.Bool)] bool resume);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [LibraryImport("kernel32.dll")]
    public static partial uint GetThreadId(IntPtr thread);

    [LibraryImport("kernel32.dll")]
    public static partial IntPtr GetCurrentThread();

    /// <summary>Opens the thread of <paramref name="process"/> that follows <paramref name="thread"/> (the first one for <see cref="IntPtr.Zero"/>); 0 on success.</summary>
    [LibraryImport("ntdll.dll")]
    public static partial int NtGetNextThread(IntPtr process, IntPtr thread, uint desiredAccess, uint handleAttributes, uint flags, out IntPtr newThread);

    [LibraryImport("ntdll.dll")]
    public static unsafe partial int NtQueryInformationThread(IntPtr thread, int threadInformationClass, void* threadInformation, uint threadInformationLength, out uint returnLength);

    /// <summary>
    /// The head of the process' list of dynamic function tables (<c>RtlAddFunctionTable</c>, <c>RtlAddGrowableFunctionTable</c>,
    /// <c>RtlInstallFunctionTableCallback</c>). It lives in ntdll's data, which is mapped at the same address in every
    /// process of a boot session, so the address returned here is valid in the target process too.
    /// </summary>
    [LibraryImport("ntdll.dll")]
    public static partial IntPtr RtlGetFunctionTableListHead();

    [LibraryImport("advapi32.dll")]
    public static partial uint EventRegister(in Guid providerId, IntPtr enableCallback, IntPtr callbackContext, out long regHandle);

    [LibraryImport("advapi32.dll")]
    public static partial uint EventUnregister(long regHandle);

    [LibraryImport("advapi32.dll")]
    public static unsafe partial uint EventWrite(long regHandle, in EVENT_DESCRIPTOR eventDescriptor, uint userDataCount, EVENT_DATA_DESCRIPTOR* userData);
}
