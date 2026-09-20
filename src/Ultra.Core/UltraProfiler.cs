// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using ByteSizeLib;

namespace Ultra.Core;

/// <summary>
/// A profiler that uses Event Tracing for Windows (ETW) to collect performance data.
/// </summary>
public abstract class UltraProfiler : IDisposable
{
    private protected bool CancelRequested;
    private protected ManualResetEvent? CleanCancel;
    private protected bool StopRequested;
    private protected readonly Stopwatch ProfilerClock;
    private protected TimeSpan LastTimeProgress;
    private readonly CancellationTokenSource _cancellationTokenSource;
    private bool _disposed;

    /// <summary>
    /// The postfix name for the sampler events for a nettrace file.
    /// </summary>
    public const string NettracePostfixNameSampler = "_sampler";

    /// <summary>
    /// The postfix name for the CLR events for a nettrace file.
    /// </summary>
    public const string NettracePostfixNameClr = "_clr";

    /// <summary>
    /// Initializes a new instance of the <see cref="UltraProfiler"/> class.
    /// </summary>
    private protected UltraProfiler()
    {
        ProfilerClock = new Stopwatch();
        _cancellationTokenSource = new CancellationTokenSource();
    }

    private protected CancellationToken CancellationToken => _cancellationTokenSource.Token;

    /// <summary>
    /// Creates a new instance of the <see cref="UltraProfiler"/> class.
    /// </summary>
    /// <returns>A new instance of the <see cref="UltraProfiler"/> class.</returns>
    /// <exception cref="PlatformNotSupportedException">Thrown when the current platform is not supported.</exception>
    public static UltraProfiler Create()
    {
        if (OperatingSystem.IsWindows())
        {
            return new UltraProfilerEtw();
        }

        if (OperatingSystem.IsMacOS() && RuntimeInformation.ProcessArchitecture == Architecture.Arm64)
        {
            return new UltraProfilerEventPipe();
        }

        throw new PlatformNotSupportedException("Only Windows or macOS+ARM64 are supported");
    }

    /// <summary>
    /// Requests to cancel the profiling session.
    /// </summary>
    /// <returns>True if the profiling session was already canceled; otherwise, false.</returns>
    public bool Cancel()
    {
        if (!CancelRequested)
        {
            CleanCancel = new ManualResetEvent(false);
            CancelRequested = true;
            return false;
        }
        else
        {
            StopRequested = true;

            // Make sure that we cancel any pending operations
            _cancellationTokenSource.Cancel();

            // Before really canceling, wait for the clean cancel to be done
            WaitForCleanCancel();
            return true;
        }
    }

    /// <summary>
    /// Releases all resources used by the <see cref="UltraProfiler"/> class.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        DisposeImpl();
        _cancellationTokenSource.Dispose();
        CleanCancel?.Dispose();
        CleanCancel = null;
    }

    private protected abstract void DisposeImpl();

    /// <summary>
    /// Determines whether the current process is running with elevated privileges.
    /// </summary>
    /// <returns>True if the current process is running with elevated privileges; otherwise, false.</returns>
    public static bool IsElevated()
    {
        if (OperatingSystem.IsMacOS()) return true;

        return IsElevatedWindows();
    }

    private static bool IsElevatedWindows()
    {
        var isElevated = Microsoft.Diagnostics.Tracing.Session.TraceEventSession.IsElevated();
        return isElevated.HasValue && isElevated.Value;
    }

    /// <summary>
    /// Runs the profiler with the specified options.
    /// </summary>
    /// <param name="ultraProfilerOptions">The options for the profiler.</param>
    /// <returns>A task that represents the asynchronous operation. The task result contains the path to the generated JSON file.</returns>
    /// <exception cref="ArgumentException">Thrown when the options are invalid.</exception>
    /// <exception cref="InvalidOperationException">Thrown when a cancel request is received.</exception>
    public async Task<string> Run(UltraProfilerOptions ultraProfilerOptions)
    {
        if (ultraProfilerOptions.Paused && ultraProfilerOptions.ShouldStartProfiling is null)
        {
            throw new ArgumentException("ShouldStartProfiling is required when Paused is set to true");
        }

        if (ultraProfilerOptions.DurationInSeconds < 0)
        {
            throw new ArgumentException("DurationInSeconds must be greater or equal to 0");
        }

        if (ultraProfilerOptions.DelayInSeconds < 0)
        {
            throw new ArgumentException("DelayInSeconds must be greater or equal to 0");
        }

        List<System.Diagnostics.Process> processList = new List<System.Diagnostics.Process>();
        if (ultraProfilerOptions.ProcessIds.Count > 0)
        {
            foreach (var pidToAttach in ultraProfilerOptions.ProcessIds)
            {
                try
                {
                    var process = System.Diagnostics.Process.GetProcessById(pidToAttach);
                    processList.Add(process);
                }
                catch (ArgumentException)
                {
                    throw new ArgumentException($"Unable to find Process with pid {pidToAttach}");
                }
            }
        }

        if (processList.Count == 0 && ultraProfilerOptions.ProgramPath is null)
        {
            throw new ArgumentException("pid is required or an executable with optional arguments");
        }

        string? processName = null;

        System.Diagnostics.Process? singleProcess = null;

        if (processList.Count == 1 && ultraProfilerOptions.ProgramPath is null)
        {
            singleProcess = processList[0];
            processName = singleProcess.ProcessName;
        }
        else if (ultraProfilerOptions.ProgramPath != null)
        {
            if (OperatingSystem.IsWindows() && !ultraProfilerOptions.ProgramPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException($"Executable path {ultraProfilerOptions.ProgramPath} must end with .exe");
            }

            processName = Path.GetFileNameWithoutExtension(ultraProfilerOptions.ProgramPath);
        }

        // When an explicit output name was requested together with Paused mode, a single Run() call can
        // produce several captures, so we fold the requested name into each capture's own (timestamped)
        // base name instead of letting every capture overwrite the same output file.
        var explicitOutputBaseName = ultraProfilerOptions.BaseOutputFileName;
        if (ultraProfilerOptions.Paused && explicitOutputBaseName is not null)
        {
            ultraProfilerOptions.BaseOutputFileName = null;
        }

        bool programHasBeenStarted = false;
        bool hasCompletedACapture = false;
        string? lastJsonFinalFile = null;
        int captureIndex = 0;

        // Captures that are still saving (rundown/merge/convert) in the background - in Paused mode, a new
        // capture can start profiling as soon as this one is stopped, without waiting for this list to drain.
        List<Task<string?>> pendingSaves = new();

        try
        {
            while (true)
            {
                captureIndex++;
                var thisCaptureIndex = captureIndex;

                var currentTime = DateTime.Now;
                var baseName = explicitOutputBaseName is not null
                    ? $"{explicitOutputBaseName}_{currentTime:yyyy-MM-dd_HH_mm_ss}"
                    : CaptureBaseNameBuilder.Build(processName, currentTime, singleProcess?.Id);

                // Each capture gets its own copy of the options so that its progress/status messages (both
                // while profiling live and while saving in the background afterwards) can be tagged with its
                // own index instead of racing on the single shared LogProgress/LogStepProgress delegates.
                var cycleOptions = ultraProfilerOptions.Paused
                    ? CreateCaptureReportingOptions(ultraProfilerOptions, thisCaptureIndex)
                    : ultraProfilerOptions;

                ProfilerClock.Restart();
                LastTimeProgress = ProfilerClock.Elapsed;

                var runner = CreateRunner(cycleOptions, processList, baseName, singleProcess);
                bool quitBeforeCapture = false;
                var cycleControlAction = ProfilingControlAction.None;
                bool allProcessesExitedThisCycle = false;
                try
                {
                    await runner.OnStart();
                    {
                        var startTheRequestedProgramIfRequired = async () =>
                        {
                            // Start a command line process if needed (only once, even across multiple captures)
                            if (cycleOptions.ProgramPath is not null && !programHasBeenStarted)
                            {
                                programHasBeenStarted = true;
                                var processState = await StartProcess(runner, cycleOptions);
                                processList.Add(processState.Process);
                                // Append the pid for a single process that we are attaching to
                                if (singleProcess is null)
                                {
                                    baseName = $"{baseName}_pid_{processState.Process.Id}";
                                    runner.BaseFileName = baseName;
                                }

                                singleProcess ??= processState.Process;
                            }
                        };

                        // If we have a delay, or we are asked to start paused, we start the process before the profiling starts
                        // On macOS we always need to start the program before enabling profiling
                        bool hasExplicitProgramHasStarted = cycleOptions.DelayInSeconds != 0.0 || cycleOptions.Paused || OperatingSystem.IsMacOS();
                        if (hasExplicitProgramHasStarted)
                        {
                            await startTheRequestedProgramIfRequired();
                        }

                        // Wait for the process to start
                        if (cycleOptions.Paused)
                        {
                            while (!cycleOptions.ShouldStartProfiling!() && !CancelRequested && !StopRequested)
                            {
                            }

                            // If we have a cancel request, we don't start the profiling
                            if (CancelRequested || StopRequested)
                            {
                                if (!hasCompletedACapture)
                                {
                                    throw new InvalidOperationException("CTRL+C requested");
                                }

                                quitBeforeCapture = true;
                            }
                        }

                        if (!quitBeforeCapture)
                        {
                            await EnableProfiling(runner, cycleOptions);

                            // If we haven't started the program yet, we start it now (for explicit program path)
                            if (!hasExplicitProgramHasStarted)
                            {
                                await startTheRequestedProgramIfRequired();
                            }

                            if (cycleOptions.Paused)
                            {
                                cycleOptions.CaptureStarted?.Invoke(thisCaptureIndex);
                            }

                            foreach (var process in processList)
                            {
                                cycleOptions.LogProgress?.Invoke($"Start Profiling Process {process.ProcessName} ({process.Id})");
                            }

                            // Collect the data until all processes have exited, there is a cancel request,
                            // or the user asked (in Paused mode) to stop or quit.
                            HashSet<Process> exitedProcessList = new();
                            while (!CancelRequested)
                            {
                                // Exit if we have reached the duration
                                if (ProfilerClock.Elapsed.TotalSeconds > cycleOptions.DurationInSeconds)
                                {
                                    cycleOptions.LogProgress?.Invoke($"Stopping profiling, max duration reached at {cycleOptions.DurationInSeconds}s");
                                    break;
                                }

                                if (cycleOptions.Paused && cycleOptions.PollProfilingControlAction is { } pollProfilingControlAction)
                                {
                                    var action = pollProfilingControlAction();
                                    if (action == ProfilingControlAction.Stop || action == ProfilingControlAction.Quit)
                                    {
                                        cycleControlAction = action;
                                        break;
                                    }
                                }

                                if (ProfilerClock.Elapsed.TotalMilliseconds - LastTimeProgress.TotalMilliseconds > cycleOptions.UpdateLogAfterInMs)
                                {
                                    var totalFileNameLength = runner.OnProfiling();

                                    cycleOptions.LogStepProgress?.Invoke(singleProcess is not null
                                        ? $"Profiling Process {singleProcess.ProcessName} ({singleProcess.Id}) - {(int)ProfilerClock.Elapsed.TotalSeconds}s - {ByteSize.FromBytes(totalFileNameLength)}"
                                        : $"Profiling {processList.Count} Processes - {(int)ProfilerClock.Elapsed.TotalSeconds}s - {ByteSize.FromBytes(totalFileNameLength)}");
                                    LastTimeProgress = ProfilerClock.Elapsed;
                                }

                                await Task.Delay(cycleOptions.CheckDeltaTimeInMs);

                                foreach (var process in processList)
                                {
                                    if (process.HasExited && exitedProcessList.Add(process))
                                    {
                                        cycleOptions.LogProgress?.Invoke($"Process {process.ProcessName} ({process.Id}) has exited");
                                    }
                                }

                                if (exitedProcessList.Count == processList.Count)
                                {
                                    break;
                                }

                            } // Needed for JIT Compile code that was already compiled.

                            allProcessesExitedThisCycle = processList.Count > 0 && exitedProcessList.Count == processList.Count;

                            cycleOptions.LogProgress?.Invoke(singleProcess is not null ? $"End Profiling Process" : $"End Profiling {processList.Count} Processes");

                            await runner.OnStop();
                        }
                    }
                }
                catch
                {
                    await runner.OnCatch();
                    throw;
                }
                finally
                {
                    await runner.OnFinally();
                    CleanCancel?.Set();
                }

                if (StopRequested)
                {
                    throw new InvalidOperationException("CTRL+C requested");
                }

                if (quitBeforeCapture)
                {
                    // Discards any per-cycle session/temp-file state OnStart() created (e.g. the ETW
                    // kernel/user .etl files) for this abandoned capture.
                    await runner.OnCatch();
                    break;
                }

                hasCompletedACapture = true;

                if (cycleOptions.Paused)
                {
                    cycleOptions.CaptureSaving?.Invoke(thisCaptureIndex);
                }

                // Save (rundown/merge/convert) this capture. In Paused mode this runs in the background so
                // that the next capture can start profiling right away; otherwise it is awaited immediately.
                var pidsSnapshot = processList.Select(x => x.Id).ToList();
                var saveTask = SaveCaptureAsync(runner, baseName, pidsSnapshot, thisCaptureIndex, cycleOptions, ultraProfilerOptions);
                if (cycleOptions.Paused)
                {
                    pendingSaves.Add(saveTask);
                }
                else
                {
                    lastJsonFinalFile = await saveTask ?? lastJsonFinalFile;
                }

                if (!PausedSessionPlanner.ShouldWaitForAnotherCapture(ultraProfilerOptions.Paused, cycleControlAction, CancelRequested, allProcessesExitedThisCycle))
                {
                    break;
                }
            }

            if (pendingSaves.Count > 0)
            {
                var results = await Task.WhenAll(pendingSaves);
                foreach (var result in results)
                {
                    if (result is not null)
                    {
                        lastJsonFinalFile = result;
                    }
                }
            }
        }
        finally
        {
            // If the session ended abruptly (e.g. a hard CTRL+C abort) while a capture was still saving in
            // the background, make sure its exception (already reported via CaptureFailed) is observed
            // instead of surfacing later as an unobserved task exception.
            foreach (var pendingSave in pendingSaves)
            {
                if (!pendingSave.IsCompleted)
                {
                    _ = pendingSave.ContinueWith(static t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
                }
            }

            if (ultraProfilerOptions.Paused && explicitOutputBaseName is not null)
            {
                ultraProfilerOptions.BaseOutputFileName = explicitOutputBaseName;
            }
        }

        return lastJsonFinalFile ?? string.Empty;
    }

    /// <summary>
    /// Creates a per-capture copy of <paramref name="ultraProfilerOptions"/> whose progress/status
    /// delegates report through <see cref="UltraProfilerOptions.CaptureStatusChanged"/>, tagged with
    /// <paramref name="captureIndex"/>.
    /// </summary>
    private static UltraProfilerOptions CreateCaptureReportingOptions(UltraProfilerOptions ultraProfilerOptions, int captureIndex)
    {
        var cycleOptions = ultraProfilerOptions.Clone();

        cycleOptions.LogProgress = message => ultraProfilerOptions.CaptureStatusChanged?.Invoke(captureIndex, message);
        cycleOptions.LogStepProgress = message => ultraProfilerOptions.CaptureStatusChanged?.Invoke(captureIndex, message);
        cycleOptions.WaitingFileToComplete = file => ultraProfilerOptions.CaptureStatusChanged?.Invoke(captureIndex, $"Waiting for {Path.GetFileName(file)} to complete");
        cycleOptions.WaitingFileToCompleteTimeOut = file => ultraProfilerOptions.CaptureStatusChanged?.Invoke(captureIndex, $"Timeout waiting for {Path.GetFileName(file)} to complete");

        return cycleOptions;
    }

    /// <summary>
    /// Finishes and converts a single capture (CLR rundown, ETL merge, JSON conversion) and cleans up its
    /// intermediate files. Used both for the single, synchronously-awaited capture of a non-Paused run and,
    /// in Paused mode, as the body of a capture's background save task.
    /// </summary>
    private async Task<string?> SaveCaptureAsync(ProfilerRunner runner, string baseName, List<int> pids, int captureIndex, UltraProfilerOptions cycleOptions, UltraProfilerOptions ultraProfilerOptions)
    {
        try
        {
            var traceFiles = await runner.FinishFileToConvert();

            if (StopRequested)
            {
                throw new InvalidOperationException("CTRL+C requested");
            }

            string? jsonFinalFile = null;
            if (traceFiles.Count > 0)
            {
                jsonFinalFile = await Convert(baseName, traceFiles, pids, cycleOptions);
                if (ultraProfilerOptions.Paused)
                {
                    ultraProfilerOptions.CaptureCompleted?.Invoke(captureIndex, jsonFinalFile);
                }
            }

            await runner.OnFinalCleanup();

            return jsonFinalFile;
        }
        catch (Exception ex)
        {
            if (ultraProfilerOptions.Paused)
            {
                ultraProfilerOptions.CaptureFailed?.Invoke(captureIndex, ex);
            }

            throw;
        }
    }


    private protected abstract ProfilerRunner CreateRunner(UltraProfilerOptions ultraProfilerOptions, List<Process> processList, string baseName, Process? singleProcess);

    /// <summary>
    /// Converts the ETL file to a compressed JSON file in the Firefox Profiler format.
    /// </summary>
    /// <param name="baseFileNameOutput">The base name of the output file.</param>
    /// <param name="traceFiles">The list of trace files to include in the conversion.</param>
    /// <param name="pIds">The list of process IDs to include in the conversion.</param>
    /// <param name="ultraProfilerOptions">The options for the profiler.</param>
    /// <returns>A task that represents the asynchronous operation. The task result contains the path to the generated JSON file.</returns>
    /// <exception cref="InvalidOperationException">Thrown when a stop request is received.</exception>
    public async Task<string> Convert(string baseFileNameOutput, List<UltraProfilerTraceFile> traceFiles, List<int> pIds, UltraProfilerOptions ultraProfilerOptions)
    {
        var profile = UltraConverterToFirefox.Convert(traceFiles, ultraProfilerOptions, pIds);

        if (StopRequested)
        {
            throw new InvalidOperationException("CTRL+C requested");
        }

        var jsonFinalFile = $"{ultraProfilerOptions.BaseOutputFileName ?? baseFileNameOutput}.json.gz";
        ultraProfilerOptions.LogProgress?.Invoke($"Converting to Firefox Profiler JSON");
        await using var stream = File.Create(jsonFinalFile);
        await using var gzipStream = new GZipStream(stream, CompressionLevel.Optimal);
        await JsonSerializer.SerializeAsync(gzipStream, profile, FirefoxProfiler.JsonProfilerContext.Default.Profile);
        gzipStream.Flush();

        return jsonFinalFile;
    }

    private async Task EnableProfiling(ProfilerRunner runner, UltraProfilerOptions ultraProfilerOptions)
    {
        ProfilerClock.Restart();
        while (!CancelRequested)
        {
            var deltaInSecondsBeforeProfilingCanStart = ultraProfilerOptions.DelayInSeconds - ProfilerClock.Elapsed.TotalSeconds;

            if (deltaInSecondsBeforeProfilingCanStart <= 0)
            {
                break;
            }

            if (ProfilerClock.Elapsed.TotalMilliseconds - LastTimeProgress.TotalMilliseconds > ultraProfilerOptions.UpdateLogAfterInMs)
            {
                ultraProfilerOptions.LogStepProgress?.Invoke($"Delay before starting the profiler {deltaInSecondsBeforeProfilingCanStart:0.0}s");
                LastTimeProgress = ProfilerClock.Elapsed;
            }

            // We don't handle the case of the process being killed during the delay
            // As it complicates the check when we start the process right after enabling the profiling

            // The loop checking for the process to be killed will happen after anyway

            await Task.Delay(ultraProfilerOptions.CheckDeltaTimeInMs);
        }

        // In case of a cancel request during the delay, we assume that it is a CTRL+C and not a stop of the profiler, as we haven't started profiling yet
        if (CancelRequested)
        {
            throw new InvalidOperationException("CTRL+C requested");
        }

        await runner.OnEnablingProfiling();

        // Reset the clock to account for the duration of the profiler
        ProfilerClock.Restart();
    }

    private protected async Task WaitForStaleFile(string file, UltraProfilerOptions options)
    {
        var clock = Stopwatch.StartNew();
        var startTime = clock.ElapsedMilliseconds;
        var fileInfo = new FileInfo(file);
        if (!fileInfo.Exists) return;
        var length = 0L;
        long lastTimeLogInMs = -1;
        while (true)
        {
            fileInfo.Refresh();
            var newLength = fileInfo.Length;
            if (newLength != length)
            {
                length = newLength;
            }
            else
            {
                break;
            }

            if (lastTimeLogInMs < 0 || (clock.ElapsedMilliseconds - lastTimeLogInMs) > options.UpdateLogAfterInMs)
            {
                options.WaitingFileToComplete?.Invoke(file);
                lastTimeLogInMs = clock.ElapsedMilliseconds;
            }

            if (clock.ElapsedMilliseconds - startTime > options.TimeOutAfterInMs)
            {
                options.WaitingFileToCompleteTimeOut?.Invoke(file);
                break;
            }

            await Task.Delay(options.CheckDeltaTimeInMs);
        }
    }

    private protected static async Task<ProcessState> StartProcess(ProfilerRunner runner, UltraProfilerOptions ultraProfilerOptions)
    {
        var mode = ultraProfilerOptions.ConsoleMode;

        var process = new Process();

        var startInfo = process.StartInfo;
        startInfo.FileName = ultraProfilerOptions.ProgramPath;

        foreach (var arg in ultraProfilerOptions.Arguments)
        {
            startInfo.ArgumentList.Add(arg);
        }

        ultraProfilerOptions.LogProgress?.Invoke($"Starting Process {startInfo.FileName} {string.Join(" ", startInfo.ArgumentList)}");

        if (mode == UltraProfilerConsoleMode.Silent)
        {
            startInfo.UseShellExecute = true;
            startInfo.CreateNoWindow = true;
            startInfo.WindowStyle = ProcessWindowStyle.Hidden;

            if (runner.OnPrepareStartProcess != null)
            {
                await runner.OnPrepareStartProcess(startInfo);
            }

            process.Start();
        }
        else
        {
            startInfo.UseShellExecute = false;
            startInfo.CreateNoWindow = true;
            startInfo.RedirectStandardOutput = true;
            startInfo.RedirectStandardError = true;
            startInfo.RedirectStandardInput = true;

            process.OutputDataReceived += (sender, args) =>
            {
                if (args.Data != null)
                {
                    ultraProfilerOptions.ProgramLogStdout?.Invoke(args.Data);
                }
            };

            process.ErrorDataReceived += (sender, args) =>
            {
                if (args.Data != null)
                {
                    ultraProfilerOptions.ProgramLogStderr?.Invoke(args.Data);
                }
            };

            if (runner.OnPrepareStartProcess != null)
            {
                await runner.OnPrepareStartProcess(startInfo);
            }

            process.Start();

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
        }

        var state = new ProcessState(process);

        // Make sure to call WaitForExit
        var thread = new Thread(() =>
        {
            try
            {
                process.WaitForExit();
                state.HasExited = true;
            }
            catch
            {
                // ignore
            }
        })
        {
            Name = "Ultra-ProcessWaitForExit",
            IsBackground = true
        };
        thread.Start();

        if (runner.OnProcessStarted != null)
        {
            await runner.OnProcessStarted(process);
        }

        return state;
    }

    private void WaitForCleanCancel()
    {
        if (CleanCancel is not null)
        {
            CleanCancel.WaitOne();
            CleanCancel.Dispose();
            CleanCancel = null;
        }
    }

    private protected class ProfilerRunner(string baseFileName)
    {
        // Can be updated after the profiled process is started (to append the pid)
        public string BaseFileName { get; set; } = baseFileName;

        public required Func<Task> OnStart;

        public required Func<Task> OnEnablingProfiling;

        public required Func<long> OnProfiling;

        public required Func<Task> OnStop;

        public Func<ProcessStartInfo, Task>? OnPrepareStartProcess;

        public Func<Process, Task>? OnProcessStarted;

        public required Func<Task> OnCatch;

        public required Func<Task> OnFinally;

        public required Func<Task<List<UltraProfilerTraceFile>>> FinishFileToConvert;

        public required Func<Task> OnFinalCleanup;
    }

    private protected class ProcessState
    {
        public ProcessState(Process process)
        {
            Process = process;
        }

        public readonly Process Process;

        public bool HasExited;
    }
}