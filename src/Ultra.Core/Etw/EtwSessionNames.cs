// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

using System.Text.RegularExpressions;

namespace Ultra.Core.Etw;

/// <summary>
/// Names the ETW sessions ultra creates so that a session left running by an ultra process that died (killed, or its
/// console closed while hung) can be recognized and stopped by the next ultra: ETW sessions outlive their creator,
/// and a leaked kernel session keeps logging the whole system to disk until it is stopped.
/// </summary>
internal static partial class EtwSessionNames
{
    /// <summary>What a session name says about the session.</summary>
    public enum Ownership
    {
        /// <summary>Not a session created by ultra.</summary>
        NotUltra,

        /// <summary>Created by an ultra process that is still running.</summary>
        Owned,

        /// <summary>Created by an ultra process that is no longer running.</summary>
        Orphaned,

        /// <summary>Named by an ultra version that did not record its process id: its owner cannot be known.</summary>
        LegacyUnknown,
    }

    /// <summary>
    /// Builds the name of an ETW session of a capture: <c>{baseName}-ultra{pid}-{kind}</c>, where <c>pid</c> is the
    /// process id of this ultra process.
    /// </summary>
    /// <param name="baseName">The base name of the capture.</param>
    /// <param name="kind">The kind of session: <c>kernel</c>, <c>user</c> or <c>rundown</c>.</param>
    public static string Create(string baseName, string kind) => Create(baseName, kind, Environment.ProcessId);

    /// <summary>Builds the name of an ETW session owned by the ultra process <paramref name="ownerProcessId"/>.</summary>
    internal static string Create(string baseName, string kind, int ownerProcessId) => $"{baseName}-ultra{ownerProcessId}-{kind}";

    /// <summary>
    /// Classifies the session <paramref name="sessionName"/>.
    /// </summary>
    /// <param name="sessionName">The name of an active ETW session.</param>
    /// <param name="getProcessName">Returns the name of the running process with the given id, or <c>null</c> if there is none.</param>
    /// <param name="otherUltraIsRunning">Returns whether an ultra process other than this one is running (only asked for legacy names).</param>
    public static Ownership Classify(string sessionName, Func<int, string?> getProcessName, Func<bool> otherUltraIsRunning)
    {
        ArgumentNullException.ThrowIfNull(sessionName);
        ArgumentNullException.ThrowIfNull(getProcessName);
        ArgumentNullException.ThrowIfNull(otherUltraIsRunning);

        var match = OwnedSessionName().Match(sessionName);
        if (match.Success)
        {
            if (!int.TryParse(match.Groups[1].ValueSpan, out var pid))
            {
                return Ownership.NotUltra;
            }

            // A pid reused by an unrelated process does not keep the session alive. ultra runs as ultra.exe (global
            // tool shim) or under dotnet.exe (dotnet ultra.dll, tests).
            var processName = getProcessName(pid);
            return processName is not null && IsUltraHostProcessName(processName) ? Ownership.Owned : Ownership.Orphaned;
        }

        // Before the owner pid was part of the name: ultra_<process>_<timestamp>[_pid_<pid>]-<kind>.
        if (LegacySessionName().IsMatch(sessionName))
        {
            return otherUltraIsRunning() ? Ownership.LegacyUnknown : Ownership.Orphaned;
        }

        return Ownership.NotUltra;
    }

    /// <summary>Returns whether <paramref name="processName"/> (without extension) can be the process running ultra.</summary>
    public static bool IsUltraHostProcessName(string processName) =>
        processName.Equals("ultra", StringComparison.OrdinalIgnoreCase) || processName.Equals("dotnet", StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex(@"-ultra(\d{1,10})-(?:kernel|user|rundown)$", RegexOptions.CultureInvariant)]
    private static partial Regex OwnedSessionName();

    [GeneratedRegex(@"^ultra_.*-(?:kernel|user|rundown)$", RegexOptions.CultureInvariant)]
    private static partial Regex LegacySessionName();
}
