// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using ByteSizeLib;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.Clr;
using Microsoft.Diagnostics.Tracing.Session;
using Ultra.Core.Etw;
using Ultra.Core.Etw.UserMode;

namespace Ultra.Core;

/// <summary>
/// A profiler that uses Event Tracing for Windows (ETW) to collect performance data.
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
internal sealed class UltraProfilerEtw : UltraProfiler
{
    private TraceEventSession? _userSession;
    private TraceEventSession? _kernelSession;
    private UserModeSampler? _sampler;

    // Every ETW session this profiler has started and not yet disposed, including the rundown session of a capture
    // saving in the background, so that they can all be stopped if ultra is about to die (see StopLiveSessions).
    private readonly ConcurrentDictionary<TraceEventSession, byte> _liveSessions = new();
    private readonly PosixSignalRegistration[] _signalRegistrations;

    /// <summary>
    /// Initializes a new instance of the <see cref="UltraProfiler"/> class.
    /// </summary>
    internal UltraProfilerEtw()
    {
        // ETW sessions outlive their process: when the console window is closed (SIGHUP is CTRL_CLOSE_EVENT on
        // Windows) or the user logs off or shuts down (SIGTERM), stop them before the process is terminated, or a
        // kernel session keeps logging the whole system to disk. CTRL+C goes through Cancel() instead.
        _signalRegistrations =
        [
            PosixSignalRegistration.Create(PosixSignal.SIGHUP, _ => StopLiveSessions()),
            PosixSignalRegistration.Create(PosixSignal.SIGTERM, _ => StopLiveSessions()),
        ];
    }

    private protected override void DisposeImpl()
    {
        _sampler?.Dispose();
        _sampler = null;

        DisposeSessions();
        StopLiveSessions();

        foreach (var registration in _signalRegistrations)
        {
            registration.Dispose();
        }
    }

    private protected override void EmergencyStop()
    {
        try { _sampler?.Stop(); } catch { /* ignore */ }
        StopLiveSessions();
    }

    /// <summary>Stops every ETW session of this profiler that is still running. Safe to call from any thread.</summary>
    private void StopLiveSessions()
    {
        foreach (var session in _liveSessions.Keys)
        {
            try { session.Stop(noThrow: true); } catch { /* ignore */ }
            _liveSessions.TryRemove(session, out _);
        }
    }

    /// <summary>Creates an ETW session of a capture, named so that it can be recognized if it leaks (see <see cref="EtwSessionNames"/>).</summary>
    private TraceEventSession CreateSession(string baseName, string kind, string fileName)
    {
        var session = new TraceEventSession(EtwSessionNames.Create(baseName, kind), fileName);
        _liveSessions[session] = 0;
        return session;
    }

    private void DisposeSession(TraceEventSession? session)
    {
        if (session is null)
        {
            return;
        }

        // Dispose stops the OS-level ETW session (StopOnDispose is true)
        try { session.Dispose(); } catch { /* ignore */ }
        _liveSessions.TryRemove(session, out _);
    }

    /// <summary>
    /// Stops the ETW sessions that ultra processes which have exited left running (see <see cref="EtwSessionNames"/>).
    /// </summary>
    /// <returns>A message for each session stopped, or found but left running.</returns>
    internal static List<string> StopOrphanedEtwSessions()
    {
        var messages = new List<string>();
        List<string> names;
        try
        {
            names = TraceEventSession.GetActiveSessionNames();
        }
        catch (Exception)
        {
            return messages;
        }

        var ownPid = Environment.ProcessId;
        var otherUltraIsRunning = new Lazy<bool>(() => IsOtherUltraRunning(ownPid));
        foreach (var name in names)
        {
            var ownership = EtwSessionNames.Classify(name, GetProcessName, () => otherUltraIsRunning.Value);
            switch (ownership)
            {
                case EtwSessionNames.Ownership.Orphaned:
                    try
                    {
                        using var session = TraceEventSession.GetActiveSession(name);
                        if (session is not null)
                        {
                            session.Stop();
                            messages.Add($"Stopped ETW session `{name}` left running by an ultra process that has exited");
                        }
                    }
                    catch (Exception ex)
                    {
                        messages.Add($"Unable to stop ETW session `{name}` left running by an ultra process that has exited ({ex.Message}). Stop it from an elevated prompt with: logman stop \"{name}\" -ets");
                    }

                    break;
                case EtwSessionNames.Ownership.LegacyUnknown:
                    messages.Add($"ETW session `{name}` was started by an older ultra and another ultra is running. If it is left over, stop it from an elevated prompt with: logman stop \"{name}\" -ets");
                    break;
            }
        }

        return messages;

        static string? GetProcessName(int pid)
        {
            try
            {
                using var process = Process.GetProcessById(pid);
                return process.ProcessName;
            }
            catch (ArgumentException)
            {
                return null;
            }
            catch (InvalidOperationException)
            {
                return null;
            }
        }

        static bool IsOtherUltraRunning(int ownPid)
        {
            var found = false;
            foreach (var process in Process.GetProcessesByName("ultra"))
            {
                found |= process.Id != ownPid;
                process.Dispose();
            }

            return found;
        }
    }

    private void DisposeSessions()
    {
        DisposeSession(_userSession);
        _userSession = null;
        DisposeSession(_kernelSession);
        _kernelSession = null;
    }

    private protected override ProfilerRunner CreateRunner(UltraProfilerOptions ultraProfilerOptions, List<Process> processList, string baseName, Process? singleProcess)
    {
        if (ultraProfilerOptions.UserModeSampling)
        {
            return CreateUserModeRunner(ultraProfilerOptions, processList, baseName);
        }

        var options = new TraceEventProviderOptions()
        {
            StacksEnabled = true,
        };

        var kernelFileName = $"{baseName}.kernel.etl";
        var userFileName = $"{baseName}.user.etl";

        string? etlFinalFile = null;

        // The runner variable is captured by the closures below to read the up-to-date BaseFileName
        // (Run() appends the pid of the launched process to it after this method returns)
        ProfilerRunner runner = null!;

        void DeleteIntermediateFiles()
        {
            if (ultraProfilerOptions.KeepEtlIntermediateFiles)
            {
                return;
            }

            foreach (var file in new[] { kernelFileName, userFileName, $"{runner.BaseFileName}.rundown.etl" })
            {
                if (File.Exists(file))
                {
                    File.Delete(file);
                }
            }
        }
        runner = new ProfilerRunner(baseName)
        {
            OnStart = () =>
            {
                // Filter the requested process ids
                if (processList.Count > 0)
                {
                    options.ProcessIDFilter = new List<int>();
                    foreach (var process in processList)
                    {
                        options.ProcessIDFilter.Add(process.Id);
                    }
                }

                // Make sure to filter the process name if we have a single process
                if (ultraProfilerOptions.ProgramPath != null)
                {
                    options.ProcessNameFilter = [Path.GetFileName(ultraProfilerOptions.ProgramPath)];
                }

                _userSession = CreateSession(baseName, "user", userFileName);
                _kernelSession = CreateSession(baseName, "kernel", kernelFileName);

                return Task.CompletedTask;
            },

            OnProfiling = () =>
            {
                var userFileNameLength = new FileInfo(userFileName).Length;
                var kernelFileNameLength = new FileInfo(kernelFileName).Length;
                var totalFileNameLength = userFileNameLength + kernelFileNameLength;
                return totalFileNameLength;
            },

            OnStop = async () =>
            {
                _kernelSession?.Stop();
                _userSession?.Stop();

                await WaitForStaleFile(userFileName, ultraProfilerOptions);
                await WaitForStaleFile(kernelFileName, ultraProfilerOptions);

            },

            OnCatch = () =>
            {
                // Dispose the sessions first (stopping the OS-level ETW sessions) so that the files below
                // are no longer locked and no system-wide session is leaked
                DisposeSessions();

                // Delete intermediate files if we have an exception
                // (ignore errors so that the original exception is not masked)
                try { File.Delete(kernelFileName); } catch { /* ignore */ }
                try { File.Delete(userFileName); } catch { /* ignore */ }

                return Task.CompletedTask;
            },

            OnFinally = () =>
            {
                // Dispose the sessions if they are still alive (e.g. an exception was thrown before OnStop)
                DisposeSessions();

                return Task.CompletedTask;
            },

            FinishFileToConvert = async () =>
            {

                var rundownSession = $"{runner.BaseFileName}.rundown.etl";
                await RunClrRundown(runner.BaseFileName, rundownSession, options, ultraProfilerOptions);

                if (StopRequested)
                {
                    throw new InvalidOperationException("CTRL+C requested");
                }

                if (!ultraProfilerOptions.KeepMergedEtl && !UltraConverterToFirefox.UseLegacyEtlConverter)
                {
                    // The session files are converted as they are, merged by timestamp while they are read (see
                    // EtlTraceReader). Merging them first rewrites every byte of the trace and only serves a trace
                    // meant to be kept and opened elsewhere (image identities, volume mapping), so it is done only
                    // when the merged file was asked for. They are deleted once converted, in OnFinalCleanup.
                    return [new(kernelFileName), new(userFileName), new(rundownSession)];
                }

                ultraProfilerOptions.LogProgress?.Invoke($"Merging ETL Files");
                // Merge file (and to force Volume mapping)
                etlFinalFile = $"{ultraProfilerOptions.BaseOutputFileName ?? runner.BaseFileName}.etl";
                TraceEventSession.Merge([kernelFileName, userFileName, rundownSession], etlFinalFile);

                if (StopRequested)
                {
                    throw new InvalidOperationException("CTRL+C requested");
                }

                DeleteIntermediateFiles();

                return [new(etlFinalFile)];
            },

            OnFinalCleanup = () =>
            {
                DeleteIntermediateFiles();

                if (!ultraProfilerOptions.KeepMergedEtl && etlFinalFile is not null)
                {
                    File.Delete(etlFinalFile);
                    var etlxFinalFile = Path.ChangeExtension(etlFinalFile, ".etlx");
                    File.Delete(etlxFinalFile);
                }

                return Task.CompletedTask;
            },

            OnEnablingProfiling = () =>
            {
                _kernelSession!.StopOnDispose = true;
                _kernelSession.CircularBufferMB = 0;
                _kernelSession.CpuSampleIntervalMSec = ultraProfilerOptions.CpuSamplingIntervalInMs;
                _kernelSession.StackCompression = false;

                _userSession!.StopOnDispose = true;
                _userSession.CircularBufferMB = 0;
                _userSession.CpuSampleIntervalMSec = ultraProfilerOptions.CpuSamplingIntervalInMs;
                _userSession.StackCompression = false;

                var kernelEvents = KernelTraceEventParser.Keywords.Profile
                                   | KernelTraceEventParser.Keywords.ContextSwitch
                                   | KernelTraceEventParser.Keywords.ImageLoad
                                   | KernelTraceEventParser.Keywords.Process
                                   | KernelTraceEventParser.Keywords.Thread;
                _kernelSession.EnableKernelProvider(kernelEvents, KernelTraceEventParser.Keywords.Profile);

                _userSession.EnableProvider(
                    ClrTraceEventParser.ProviderGuid,
                    TraceEventLevel.Verbose, // For call stacks.
                    (ulong)ClrEvents, options);


                // Reset the clock to account for the duration of the profiler
                ProfilerClock.Restart();

                return Task.CompletedTask;
            }
        };

        return runner;
    }

    private const ClrTraceEventParser.Keywords ClrEvents =
        ClrTraceEventParser.Keywords.JITSymbols |
        ClrTraceEventParser.Keywords.Exception |
        ClrTraceEventParser.Keywords.GC |
        ClrTraceEventParser.Keywords.GCHeapAndTypeNames |
        ClrTraceEventParser.Keywords.Interop |
        ClrTraceEventParser.Keywords.Jit |
        ClrTraceEventParser.Keywords.JittedMethodILToNativeMap |
        ClrTraceEventParser.Keywords.Loader |
        ClrTraceEventParser.Keywords.Stack |
        ClrTraceEventParser.Keywords.StartEnumeration;

    private async Task RunClrRundown(string baseFileName, string rundownFileName, TraceEventProviderOptions options, UltraProfilerOptions ultraProfilerOptions)
    {
        var clrRundownSession = CreateSession(baseFileName, "rundown", rundownFileName);
        try
        {
            await RunClrRundown(clrRundownSession, rundownFileName, options, ultraProfilerOptions).ConfigureAwait(false);
        }
        finally
        {
            DisposeSession(clrRundownSession);
        }
    }

    private async Task RunClrRundown(TraceEventSession clrRundownSession, string rundownFileName, TraceEventProviderOptions options, UltraProfilerOptions ultraProfilerOptions)
    {
        clrRundownSession.StopOnDispose = true;
        clrRundownSession.CircularBufferMB = 0;

        ultraProfilerOptions.LogProgress?.Invoke($"Running CLR Rundown");

        // The runtime does method rundown first then the module rundown.  This means if you have a large
        // number of methods and method rundown does not complete you don't get ANYTHING.   To avoid this
        // we first trigger all module (loader) rundown and then trigger the method rundown
        clrRundownSession.EnableProvider(
            ClrRundownTraceEventParser.ProviderGuid,
            TraceEventLevel.Verbose,
            (ulong)(ClrRundownTraceEventParser.Keywords.Loader | ClrRundownTraceEventParser.Keywords.ForceEndRundown), options);

        await Task.Delay(500);

        clrRundownSession.EnableProvider(
            ClrRundownTraceEventParser.ProviderGuid,
            TraceEventLevel.Verbose,
            (ulong)(ClrRundownTraceEventParser.Keywords.Default & ~ClrRundownTraceEventParser.Keywords.Loader), options);

        await WaitForStaleFile(rundownFileName, ultraProfilerOptions);
    }

    /// <summary>
    /// The runner of <see cref="UltraProfilerOptions.UserModeSampling"/>: a single user session records the runtime
    /// events and the samples the <see cref="UserModeSampler"/> takes from ultra's own process; no kernel session.
    /// </summary>
    private ProfilerRunner CreateUserModeRunner(UltraProfilerOptions ultraProfilerOptions, List<Process> processList, string baseName)
    {
        if (RuntimeInformation.ProcessArchitecture != Architecture.X64)
        {
            throw new PlatformNotSupportedException("User-mode sampling is only supported on Windows x64");
        }

        var options = new TraceEventProviderOptions()
        {
            StacksEnabled = false,
        };

        var userFileName = $"{baseName}.user.etl";
        string? etlFinalFile = null;
        ProfilerRunner runner = null!;

        void DeleteIntermediateFiles()
        {
            if (!ultraProfilerOptions.KeepEtlIntermediateFiles && File.Exists(userFileName))
            {
                File.Delete(userFileName);
            }
        }

        void DisposeSampler()
        {
            try { _sampler?.Dispose(); } catch { /* ignore */ }
            _sampler = null;
        }

        runner = new ProfilerRunner(baseName)
        {
            OnStart = () =>
            {
                if (processList.Count > 0)
                {
                    options.ProcessIDFilter = processList.Select(x => x.Id).ToList();
                }

                if (ultraProfilerOptions.ProgramPath != null)
                {
                    options.ProcessNameFilter = [Path.GetFileName(ultraProfilerOptions.ProgramPath)];
                }

                _userSession = CreateSession(baseName, "user", userFileName);
                return Task.CompletedTask;
            },

            OnProfiling = () => File.Exists(userFileName) ? new FileInfo(userFileName).Length : 0,

            OnProcessStarted = process =>
            {
                _sampler?.AddProcess(process.Id);
                return Task.CompletedTask;
            },

            OnStop = async () =>
            {
                if (_sampler is { } sampler)
                {
                    sampler.Stop();
                    ultraProfilerOptions.LogProgress?.Invoke(sampler.Summary);
                }

                _userSession?.Stop();
                await WaitForStaleFile(userFileName, ultraProfilerOptions);
            },

            OnCatch = () =>
            {
                DisposeSampler();
                DisposeSessions();
                try { File.Delete(userFileName); } catch { /* ignore */ }
                return Task.CompletedTask;
            },

            OnFinally = () =>
            {
                DisposeSampler();
                DisposeSessions();
                return Task.CompletedTask;
            },

            FinishFileToConvert = async () =>
            {
                // No end-of-capture rundown session: the start enumeration of the user session already reported the
                // code that existed when sampling started, and the Mono runtime's ETW profiler was seen to abort the
                // process in its rundown provider callback (a thread detach assertion on the ETW notification thread).
                if (StopRequested)
                {
                    throw new InvalidOperationException("CTRL+C requested");
                }

                if (!ultraProfilerOptions.KeepMergedEtl && !UltraConverterToFirefox.UseLegacyEtlConverter)
                {
                    return [new(userFileName)];
                }

                ultraProfilerOptions.LogProgress?.Invoke($"Merging ETL Files");
                etlFinalFile = $"{ultraProfilerOptions.BaseOutputFileName ?? runner.BaseFileName}.etl";
                TraceEventSession.Merge([userFileName], etlFinalFile);
                DeleteIntermediateFiles();
                return [new(etlFinalFile)];
            },

            OnFinalCleanup = () =>
            {
                DeleteIntermediateFiles();
                if (!ultraProfilerOptions.KeepMergedEtl && etlFinalFile is not null)
                {
                    File.Delete(etlFinalFile);
                    File.Delete(Path.ChangeExtension(etlFinalFile, ".etlx"));
                }

                return Task.CompletedTask;
            },

            OnEnablingProfiling = () =>
            {
                var session = _userSession!;
                session.StopOnDispose = true;
                session.CircularBufferMB = 0;
                session.BufferSizeMB = Math.Max(session.BufferSizeMB, 256);

                // The sampler's events come from ultra's process: no process filter for its provider.
                session.EnableProvider(UserModeSamplerEvents.ProviderGuid, TraceEventLevel.Verbose, ulong.MaxValue);

                // The processes, threads and images are written before the runtime's start enumeration so that they
                // precede the runtime events of these processes.
                var intervalMs = ultraProfilerOptions.CpuSamplingIntervalInMs;
                _sampler = new UserModeSampler(intervalMs);
                _sampler.Start(processList.Select(x => x.Id));

                session.EnableProvider(ClrTraceEventParser.ProviderGuid, TraceEventLevel.Verbose, (ulong)ClrEvents, options);

                ProfilerClock.Restart();
                return Task.CompletedTask;
            }
        };

        return runner;
    }
}