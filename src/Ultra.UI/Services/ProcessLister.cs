// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Ultra.UI.Services;

/// <summary>
/// A running process, as shown by the process picker.
/// </summary>
/// <param name="Id">The process id.</param>
/// <param name="Name">The process name (the executable name without its extension).</param>
/// <param name="Path">The full path of the executable, or <c>null</c> when it cannot be queried.</param>
/// <param name="WindowTitle">The title of the main window, or an empty string when the process has none.</param>
internal sealed record ProcessInfo(int Id, string Name, string? Path, string WindowTitle);

/// <summary>
/// Lists the running processes.
/// </summary>
internal static partial class ProcessLister
{
    private const uint ProcessQueryLimitedInformation = 0x1000;

    /// <summary>
    /// Lists the running processes, by name then id. A process that exits while it is listed is left out.
    /// </summary>
    /// <returns>The processes.</returns>
    public static List<ProcessInfo> List()
    {
        var result = new List<ProcessInfo>();
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    var id = process.Id;
                    // The idle and system processes are not user processes and cannot be profiled.
                    if (OperatingSystem.IsWindows() && id is 0 or 4)
                    {
                        continue;
                    }

                    result.Add(new ProcessInfo(id, process.ProcessName, GetPath(process), GetWindowTitle(process)));
                }
                catch (InvalidOperationException)
                {
                    // Exited.
                }
            }
        }

        result.Sort(static (left, right) =>
        {
            var byName = string.Compare(left.Name, right.Name, StringComparison.OrdinalIgnoreCase);
            return byName != 0 ? byName : left.Id.CompareTo(right.Id);
        });
        return result;
    }

    private static string GetWindowTitle(Process process)
    {
        try
        {
            return process.MainWindowTitle;
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or PlatformNotSupportedException)
        {
            return "";
        }
    }

    private static string? GetPath(Process process)
    {
        if (OperatingSystem.IsWindows())
        {
            // Unlike Process.MainModule, this works for the processes of other users and elevated processes.
            var handle = OpenProcess(ProcessQueryLimitedInformation, false, (uint)process.Id);
            if (handle == IntPtr.Zero)
            {
                return null;
            }

            try
            {
                return QueryImageName(handle);
            }
            finally
            {
                CloseHandle(handle);
            }
        }

        try
        {
            return process.MainModule?.FileName;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static unsafe string? QueryImageName(IntPtr handle)
    {
        const int capacity = 32768;
        var buffer = stackalloc char[capacity];
        var size = (uint)capacity;
        return QueryFullProcessImageNameW(handle, 0, buffer, ref size) ? new string(buffer, 0, (int)size) : null;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial IntPtr OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(IntPtr handle);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool QueryFullProcessImageNameW(IntPtr process, uint flags, char* exeName, ref uint size);
}
