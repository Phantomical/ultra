// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

namespace Ultra.UI.Services;

/// <summary>
/// The settings of the ultra UI that persist between runs (see <see cref="SettingsStore"/>).
/// </summary>
internal sealed class AppSettings
{
    /// <summary>
    /// Gets or sets the custom symbol path entries (folders, <c>SRV*cache*server</c> entries, ...), in search order.
    /// </summary>
    public List<SymbolPathEntrySetting> SymbolPaths { get; set; } = new();

    /// <summary>
    /// Gets or sets a value indicating whether the default symbol path (<c>_NT_SYMBOL_PATH</c>, or the Microsoft and
    /// NuGet symbol servers) is searched after <see cref="SymbolPaths"/>.
    /// </summary>
    public bool IncludeDefaultSymbolPath { get; set; } = true;

    /// <summary>
    /// Gets or sets the folder the profiles are written to. <c>null</c> means <see cref="DefaultOutputDirectory"/>.
    /// </summary>
    public string? OutputDirectory { get; set; }

    /// <summary>
    /// Gets or sets the last used launch settings.
    /// </summary>
    public LaunchSettings Launch { get; set; } = new();

    /// <summary>
    /// Gets or sets the most recently launched programs, most recent first.
    /// </summary>
    public List<string> RecentPrograms { get; set; } = new();

    /// <summary>
    /// The folder the profiles are written to when <see cref="OutputDirectory"/> is not set.
    /// </summary>
    public static string DefaultOutputDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "ultra");
}

/// <summary>
/// An entry of the custom symbol path.
/// </summary>
internal sealed class SymbolPathEntrySetting
{
    /// <summary>
    /// Gets or sets the symbol path element: a folder, or a symbol server such as <c>SRV*C:\Symbols*https://server</c>.
    /// </summary>
    public string Path { get; set; } = "";

    /// <summary>
    /// Gets or sets a value indicating whether the entry is used.
    /// </summary>
    public bool Enabled { get; set; } = true;
}

/// <summary>
/// The settings of the program to profile.
/// </summary>
internal sealed class LaunchSettings
{
    /// <summary>
    /// Gets or sets a value indicating whether a running process is attached to instead of launching a program.
    /// </summary>
    public bool AttachToProcess { get; set; }

    /// <summary>
    /// Gets or sets the program to launch.
    /// </summary>
    public string ProgramPath { get; set; } = "";

    /// <summary>
    /// Gets or sets the arguments of the program, as a command line.
    /// </summary>
    public string Arguments { get; set; } = "";

    /// <summary>
    /// Gets or sets the working directory of the program. Empty means the directory of the program.
    /// </summary>
    public string WorkingDirectory { get; set; } = "";

    /// <summary>
    /// Gets or sets the environment variables of the program.
    /// </summary>
    public List<EnvironmentVariableSetting> EnvironmentVariables { get; set; } = new();

    /// <summary>
    /// Gets or sets the id of the process to attach to.
    /// </summary>
    public string ProcessId { get; set; } = "";

    /// <summary>
    /// Gets or sets a value indicating whether the profiling runs as administrator (kernel ETW sampling, Windows only).
    /// </summary>
    public bool RunAsAdministrator { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether user-mode sampling is used even when running as administrator.
    /// </summary>
    public bool UserModeSampling { get; set; }

    /// <summary>
    /// Gets or sets the sampling interval in milliseconds, empty for the default.
    /// </summary>
    public string SamplingInterval { get; set; } = "";

    /// <summary>
    /// Gets or sets the maximum duration of a capture in seconds, empty for no limit.
    /// </summary>
    public string MaxCaptureDuration { get; set; } = "";

    /// <summary>
    /// Gets or sets a value indicating whether the output of the launched program is shown.
    /// </summary>
    public bool CaptureProgramOutput { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether the first capture starts as soon as the session starts.
    /// </summary>
    public bool StartCaptureImmediately { get; set; }
}

/// <summary>
/// An environment variable set for the launched program.
/// </summary>
internal sealed class EnvironmentVariableSetting
{
    /// <summary>
    /// Gets or sets the name of the variable.
    /// </summary>
    public string Name { get; set; } = "";

    /// <summary>
    /// Gets or sets the value of the variable.
    /// </summary>
    public string Value { get; set; } = "";

    /// <summary>
    /// Gets or sets a value indicating whether the variable is set.
    /// </summary>
    public bool Enabled { get; set; } = true;
}
