// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

using System.Diagnostics;

namespace Ultra.UI.Worker;

/// <summary>
/// Starts the profiling worker (<see cref="WorkerHost"/>), which is this same executable run with
/// <see cref="WorkerHost.WorkerArgument"/>.
/// </summary>
internal static class WorkerLauncher
{
    /// <summary>
    /// The Win32 error returned when the user declines the elevation prompt.
    /// </summary>
    public const int ErrorCancelled = 1223;

    /// <summary>
    /// Creates the start info of the worker.
    /// </summary>
    /// <param name="pipeName">The pipe the worker connects to.</param>
    /// <param name="elevated">Whether the worker runs as administrator (prompting the user for consent).</param>
    /// <param name="processPath">The executable of the current process (<see cref="Environment.ProcessPath"/>).</param>
    /// <param name="entryAssemblyPath">The path of the ultra UI assembly, used when the current process is the
    /// <c>dotnet</c> host running it.</param>
    /// <returns>The start info.</returns>
    /// <exception cref="ArgumentException"><paramref name="pipeName"/> is empty.</exception>
    public static ProcessStartInfo CreateStartInfo(string pipeName, bool elevated, string processPath, string entryAssemblyPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        ArgumentException.ThrowIfNullOrWhiteSpace(processPath);

        var arguments = new List<string>();
        if (Path.GetFileNameWithoutExtension(processPath).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            arguments.Add(entryAssemblyPath);
        }

        arguments.Add(WorkerHost.WorkerArgument);
        arguments.Add(pipeName);

        var startInfo = new ProcessStartInfo(processPath);
        if (elevated)
        {
            // Elevation goes through the shell, which takes the arguments as a single command line.
            startInfo.UseShellExecute = true;
            startInfo.Verb = "runas";
            startInfo.WindowStyle = ProcessWindowStyle.Hidden;
            startInfo.Arguments = string.Join(' ', arguments.Select(QuoteArgument));
        }
        else
        {
            startInfo.UseShellExecute = false;
            startInfo.CreateNoWindow = true;
            foreach (var argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }
        }

        return startInfo;
    }

    /// <summary>
    /// Starts the worker.
    /// </summary>
    /// <param name="pipeName">The pipe the worker connects to.</param>
    /// <param name="elevated">Whether the worker runs as administrator (prompting the user for consent).</param>
    /// <returns>The worker process.</returns>
    /// <exception cref="System.ComponentModel.Win32Exception">The worker could not be started, e.g. the user declined
    /// the elevation prompt (<see cref="ErrorCancelled"/>).</exception>
    /// <exception cref="InvalidOperationException">The worker did not start.</exception>
    public static Process Start(string pipeName, bool elevated)
    {
        var processPath = Environment.ProcessPath ?? throw new InvalidOperationException("Unable to find the ultra UI executable");
        var startInfo = CreateStartInfo(pipeName, elevated, processPath, typeof(WorkerLauncher).Assembly.Location);
        return Process.Start(startInfo) ?? throw new InvalidOperationException("The profiling worker did not start");
    }

    private static string QuoteArgument(string argument)
    {
        // Paths cannot contain quotes on Windows, and the pipe name is generated: quoting on spaces is enough.
        return argument.Length == 0 || argument.Any(char.IsWhiteSpace) ? $"\"{argument}\"" : argument;
    }
}
