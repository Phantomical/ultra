// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.
using System.Text;
using ByteSizeLib;
using Spectre.Console;
using Spectre.Console.Rendering;
using Ultra.Core;
using XenoAtom.CommandLine;

namespace Ultra;

/// <summary>
/// Main entry point for the ultra command line.
/// </summary>
internal class Program
{
    static async Task<int> Main(string[] args)
    {
        Console.InputEncoding = Encoding.UTF8;
        Console.OutputEncoding = Encoding.UTF8;

        List<int> pidList = new();

        bool verbose = false;
        var options = new UltraProfilerOptions();

        const string _ = "";

        var commandApp = new CommandApp("ultra", "Profile an application")
        {
            new CommandUsage("Usage: {NAME} [Options] command "),
            _,
            new HelpOption(),
            new VersionOption(),
            { "verbose", "Display verbose progress", v => verbose = v is not null },
            _,
            "Available commands:",
            new Command("profile", "Profile a new process or attach to an existing process")
            {
                new CommandUsage("Usage: {NAME} [Options] <pid | -- execName arg0 arg1...>"),
                _,
                new HelpOption(),
                { "o|output=", "The base output {FILE} name. Default is ultra_<process_name>_yyyy-MM-dd_HH_mm_ss.", v => options.BaseOutputFileName = v },
                { "pid=", "The {PID} of the process to attach the profiler to.", (int pid) => { pidList.Add(pid); } },
                { "sampling-interval=", $"The {{VALUE}} of the sample interval in ms. Default is {options.CpuSamplingIntervalInMs:0.000}ms{(OperatingSystem.IsWindows() ? " (8190Hz)" : ", minimum is 1ms on macOS")}.", (float v) => options.CpuSamplingIntervalInMs  = v },
                { "symbol-path=", $"The {{VALUE}} of symbol path. The default value is `{options.GetCachedSymbolPath()}`.", v => options.SymbolPathText  = v },
                { "paused", "Launch the profiler paused and wait for SPACE or ENTER to start profiling. While profiling, press S to stop and save that capture and wait for a new one, or Q to stop and quit. You can start/stop as many captures as you want before quitting.", v => options.Paused = v is not null },
                { "delay=", $"Starts profiling after a specific delay (seconds). Default is {options.DelayInSeconds}s.", (double delay) => options.DelayInSeconds = delay },
                { "duration=", $"Run the profiler for a maximum duration (seconds). Default is {options.DurationInSeconds}s", (double duration) => options.DurationInSeconds = duration },
                { "keep-merged-etl-file", "Keep the merged ETL file.", v => options.KeepMergedEtl = v is not null },
                { "keep-intermediate-etl-files", "Keep the intermediate ETL files before merging.", v => options.KeepEtlIntermediateFiles = v is not null },
                { "mode=", "Defines how the stdout/stderr of a program explicitly started by ultra should be integrated in its output. Default is `silent` which will not mix program's output. The other options are: `raw` is going to mix ultra and program output together in a raw output. `live` is going to mix ultra and program output within a live table.", v =>
                    {
                        if ("raw".Equals(v, StringComparison.OrdinalIgnoreCase))
                        {
                            options.ConsoleMode = UltraProfilerConsoleMode.Raw;
                        }
                        else if ("live".Equals(v, StringComparison.OrdinalIgnoreCase))
                        {
                            options.ConsoleMode = UltraProfilerConsoleMode.Live;
                        }
                        else
                        {
                            options.ConsoleMode = UltraProfilerConsoleMode.Silent;
                        }
                    }
                },
                { "<>", "Exec to launch with arguments passed" },
                // Action for the commit command
                async (ctx, arguments) =>
                {
                    if (arguments.Length == 0 && pidList.Count == 0)
                    {
                        AnsiConsole.MarkupLine("[red]Missing pid or executable name[/]");
                        return 1;
                    }

                    if (!UltraProfiler.IsElevated())
                    {
                        AnsiConsole.MarkupLine("[darkorange]This command requires to run with administrator rights[/]");
                        return 1;
                    }

                    string? jsonGzOutput = null;

                    
                    // Add the pid passed as options
                    options.ProcessIds.AddRange(pidList);

                    if (arguments.Length == 1 && int.TryParse(arguments[0], out var pid))
                    {
                        options.ProcessIds.Add(pid);
                    }
                    else if (arguments.Length > 0)
                    {
                        options.ProgramPath = arguments[0];
                        options.Arguments.AddRange(arguments.AsSpan().Slice(1));
                    }

                    AnsiConsole.MarkupLine($"[green]You can press CTRL+C to stop profiling before the end of the process[/]");

                    options.EnsureDirectoryForBaseOutputFileName();

                    var etwProfiler = UltraProfiler.Create();

                    Console.CancelKeyPress += (sender, eventArgs) =>
                    {
                        AnsiConsole.WriteLine();
                        AnsiConsole.MarkupLine("[darkorange]Cancelled via CTRL+C[/]");
                        eventArgs.Cancel = true;
                        // ReSharper disable once AccessToDisposedClosure
                        if (etwProfiler.Cancel())
                        {
                            AnsiConsole.MarkupLine("[red]Stopped via CTRL+C[/]");
                        }
                    };

                    // A Paused session can capture more than once, and a new capture can start profiling
                    // while an earlier one is still saving in the background, so ultra's status is shown
                    // as one row per active/working capture.
                    if (options.Paused)
                    {
                        var board = new CaptureBoard();

                        options.CaptureStarted = board.CaptureStarted;
                        options.CaptureSaving = board.CaptureSaving;
                        options.CaptureStatusChanged = board.StatusChanged;
                        options.CaptureCompleted = board.Completed;
                        options.CaptureFailed = board.Failed;

                        if (options.ConsoleMode != UltraProfilerConsoleMode.Silent)
                        {
                            options.ProgramLogStdout = board.LogText;
                            options.ProgramLogStderr = board.LogText;
                        }

                        options.ShouldStartProfiling = () =>
                        {
                            // Re-armed on every wait (a Paused session can wait for a start several times),
                            // so that CTRL+C keeps being readable as a key here instead of raising CancelKeyPress.
                            Console.TreatControlCAsInput = true;

                            var key = Console.ReadKey(true);
                            var action = ProfilingControlKeyMapper.Map(key.Key, key.Modifiers);
                            bool startProfiling = action == ProfilingControlAction.Start;

                            if (startProfiling || action == ProfilingControlAction.Quit)
                            {
                                // Restore the default behavior so that CancelKeyPress will be called later if CTRL+C is pressed
                                Console.TreatControlCAsInput = false;
                            }

                            if (action == ProfilingControlAction.Quit)
                            {
                                etwProfiler.Cancel();
                            }

                            return startProfiling;
                        };

                        options.PollProfilingControlAction = () =>
                        {
                            if (Console.IsInputRedirected || !Console.KeyAvailable)
                            {
                                return ProfilingControlAction.None;
                            }

                            var key = Console.ReadKey(true);
                            return ProfilingControlKeyMapper.Map(key.Key, key.Modifiers);
                        };

                        await AnsiConsole.Live(board.Display)
                            .StartAsync(async liveCtx =>
                            {
                                board.Attach(liveCtx);

                                try
                                {
                                    jsonGzOutput = await etwProfiler.Run(options);
                                }
                                finally
                                {
                                    etwProfiler.Dispose();
                                }
                            });
                    }
                    else if (options.ConsoleMode == UltraProfilerConsoleMode.Silent)
                    {
                        await AnsiConsole.Status()
                            .Spinner(Spinner.Known.Default)
                            .SpinnerStyle(Style.Parse("red"))
                            .StartAsync("Profiling", async statusCtx =>
                                {
                                    string? previousText = null;

                                    options.LogStepProgress = (text) =>
                                    {
                                        if (verbose && previousText is not null && previousText != text)
                                        {
                                            AnsiConsole.MarkupLine($"{Markup.Escape(previousText)} [green]\u2713[/]");
                                            previousText = text;
                                        }

                                        statusCtx.Status(Markup.Escape(text));
                                    };
                                    options.LogProgress = (text) =>
                                    {
                                        if (verbose && previousText != null && previousText != text)
                                        {
                                            AnsiConsole.MarkupLine($"{Markup.Escape(previousText)} [green]\u2713[/]");
                                        }

                                        statusCtx.Status(Markup.Escape(text));
                                        previousText = text;
                                    };
                                    options.WaitingFileToComplete = (file) => { statusCtx.Status($"Waiting for {Markup.Escape(file)} to complete"); };
                                    options.WaitingFileToCompleteTimeOut = (file) => { statusCtx.Status($"Timeout waiting for {Markup.Escape(file)} to complete"); };

                                    try
                                    {
                                        jsonGzOutput = await etwProfiler.Run(options);
                                    }
                                    finally
                                    {
                                        etwProfiler.Dispose();
                                    }

                                    if (verbose)
                                    {
                                        options.LogProgress.Invoke("Profiling Done");
                                    }
                                }
                            );
                    }
                    else if (options.ConsoleMode == UltraProfilerConsoleMode.Raw)
                    {
                        options.LogStepProgress = s => AnsiConsole.WriteLine($">>ultra::{s}");
                        options.LogProgress = s => AnsiConsole.WriteLine($">>ultra::{s}");
                        options.WaitingFileToComplete = (file) => { AnsiConsole.WriteLine($">>ultra::Waiting for {Markup.Escape(file)} to complete"); };
                        options.WaitingFileToCompleteTimeOut = (file) => { AnsiConsole.WriteLine($">>ultra::Timeout waiting for {Markup.Escape(file)} to complete"); };
                        options.ProgramLogStdout = AnsiConsole.WriteLine;
                        options.ProgramLogStderr = AnsiConsole.WriteLine;
                        
                        try
                        {
                            jsonGzOutput = await etwProfiler.Run(options);
                        }
                        finally
                        {
                            etwProfiler.Dispose();
                        }
                    }
                    else if (options.ConsoleMode == UltraProfilerConsoleMode.Live)
                    {
                        var statusTable = new StatusTable();

                        await AnsiConsole.Live(statusTable.Table)
                            // .AutoClear(true) // No auto clear to keep the output (e.g. in case the program shows errors in its stdout/stderr)
                            .StartAsync(async liveCtx =>
                            {
                                string? previousText = null;

                                options.LogStepProgress = (text) =>
                                {
                                    if (verbose && previousText is not null && previousText != text)
                                    {
                                        statusTable.LogText($"{Markup.Escape(previousText)} [green]\u2713[/]");
                                        previousText = text;
                                    }

                                    statusTable.Status(Markup.Escape(text));

                                    statusTable.UpdateTable();
                                    liveCtx.Refresh();
                                };

                                options.LogProgress = (text) =>
                                {
                                    if (verbose && previousText != null && previousText != text)
                                    {
                                        statusTable.LogText($"{Markup.Escape(previousText)} [green]\u2713[/]");
                                    }

                                    statusTable.Status(Markup.Escape(text));
                                    previousText = text;

                                    statusTable.UpdateTable();
                                    liveCtx.Refresh();
                                };

                                options.WaitingFileToComplete = (file) =>
                                {
                                    statusTable.Status($"Waiting for {Markup.Escape(file)} to complete");
                                    statusTable.UpdateTable();
                                    liveCtx.Refresh();
                                };

                                options.WaitingFileToCompleteTimeOut = (file) =>
                                {
                                    statusTable.Status($"Timeout waiting for {Markup.Escape(file)} to complete");
                                    statusTable.UpdateTable();
                                    liveCtx.Refresh();
                                };

                                options.ProgramLogStdout = (text) =>
                                {
                                    statusTable.LogText(Markup.Escape(text));
                                    statusTable.UpdateTable();
                                    liveCtx.Refresh();
                                };

                                options.ProgramLogStderr = (text) =>
                                {
                                    statusTable.LogText(Markup.Escape(text));
                                    statusTable.UpdateTable();
                                    liveCtx.Refresh();
                                };
                                
                                try
                                {
                                    jsonGzOutput = await etwProfiler.Run(options);
                                }
                                finally
                                {
                                    etwProfiler.Dispose();
                                }
                            }
                        );
                    }

                    if (!string.IsNullOrEmpty(jsonGzOutput))
                    {
                        // In Paused mode, each capture is already reported as it is produced via the capture board.
                        if (!options.Paused)
                        {
                            AnsiConsole.MarkupLine($"Generated Firefox Profiler JSON file -> [green]{jsonGzOutput}[/] - {ByteSize.FromBytes(new FileInfo(jsonGzOutput).Length)}");
                        }
                        AnsiConsole.MarkupLine($"Go to [blue]https://profiler.firefox.com/ [/]");
                    }
                    else
                    {
                        AnsiConsole.MarkupLine("[red]The profiler did not generate any trace file[/]");
                        return 1;
                    }

                    return 0;
                }
            },
            new Command("convert", "Convert an existing trace file (one ETL file or a list of nettrace files) to a Firefox Profiler json file")
            {
                new CommandUsage("Usage: {NAME} --pid xxx [<etl_file_name.etl> | <file1_sampler.nettrace> <file1_clr.nettrace>]"),
                _,
                new HelpOption(),
                { "o|output=", "The base output {FILE} name. Default is the input file name without the extension.", v => options.BaseOutputFileName = v },
                { "pid=", "The {PID} of the process", (int pid) => { pidList.Add(pid); } },
                { "symbol-path=", $"The {{VALUE}} of symbol path. The default value is `{options.GetCachedSymbolPath()}`.", v => options.SymbolPathText  = v },
                { "<>", "The trace file(s) to convert" },
                async (ctx, arguments) =>
                {
                    var maxWidth = Console.IsOutputRedirected ? 80 : Console.WindowWidth;

                    if (arguments.Length == 0)
                    {
                        AnsiConsole.MarkupLine("[red]Missing trace file name[/]");
                        return 1;
                    }

                    if (pidList.Count == 0)
                    {
                        AnsiConsole.MarkupLine("[red]Missing --pid option[/]");
                        return 1;
                    }

                    

                    string? fileOutput = null;

                    await AnsiConsole.Status()
                        .Spinner(Spinner.Known.Default)
                        .SpinnerStyle(Style.Parse("red"))
                        .StartAsync("Converting", async statusCtx =>
                            {
                                string? previousText = null;

                                options.LogStepProgress = (text) =>
                                {
                                    if (verbose && previousText is not null && previousText != text)
                                    {
                                        AnsiConsole.MarkupLine($"{Markup.Escape(previousText)} [green]\u2713[/]");
                                        previousText = text;
                                    }

                                    statusCtx.Status(Markup.Escape(text));
                                };

                                options.LogProgress = (text) =>
                                {
                                    if (verbose && previousText != null && previousText != text)
                                    {
                                        AnsiConsole.MarkupLine($"{Markup.Escape(previousText)} [green]\u2713[/]");
                                    }

                                    statusCtx.Status(Markup.Escape(text));
                                    previousText = text;
                                };

                                var etwProfiler = UltraProfiler.Create();
                                try
                                {
                                    Console.CancelKeyPress += (sender, eventArgs) =>
                                    {
                                        AnsiConsole.WriteLine();
                                        AnsiConsole.MarkupLine("[darkorange]Cancelled via CTRL+C[/]");

                                        eventArgs.Cancel = true;
                                        if (etwProfiler.Cancel())
                                        {
                                            AnsiConsole.MarkupLine("[red]Stopped via CTRL+C[/]");
                                        }
                                    };

                                    options.EnsureDirectoryForBaseOutputFileName();

                                    var traceFiles = arguments.Select(x => new UltraProfilerTraceFile(x)).ToList();

                                    // Try to recover the base name from the first trace file
                                    string baseName = Path.GetFileNameWithoutExtension(traceFiles[0].FileName);
                                    if (traceFiles[0].IsNettrace)
                                    {
                                        if (baseName.EndsWith(UltraProfiler.NettracePostfixNameSampler))
                                        {
                                            baseName = baseName.Substring(0, baseName.Length - UltraProfiler.NettracePostfixNameSampler.Length);
                                        }
                                        else if (baseName.EndsWith(UltraProfiler.NettracePostfixNameClr))
                                        {
                                            baseName = baseName.Substring(0, baseName.Length - UltraProfiler.NettracePostfixNameClr.Length);
                                        }
                                    }
                                    
                                    fileOutput = await etwProfiler.Convert(baseName, traceFiles, pidList, options);
                                }
                                finally
                                {
                                    etwProfiler.Dispose();
                                }

                                if (verbose)
                                {
                                    options.LogProgress.Invoke("Converting Done");
                                }
                            }
                        );


                    if (fileOutput != null)
                    {
                        AnsiConsole.MarkupLine($"Generated Firefox Profiler JSON file -> [green]{fileOutput}[/] - {ByteSize.FromBytes(new FileInfo(fileOutput).Length)}");
                        AnsiConsole.MarkupLine($"Go to [blue]https://profiler.firefox.com/ [/]");
                    }

                    return 0;
                }
            }
        };

        var width = Console.IsOutputRedirected ? 80 : Math.Max(80, Console.WindowWidth);
        var optionWidth = Console.IsOutputRedirected || width == 80 ? 29 : 36;
        
        try
        {
            return await commandApp.RunAsync(args, new CommandRunConfig(width, optionWidth));
        }
        catch (Exception ex)
        {
            AnsiConsole.Foreground = Color.Red;
            AnsiConsole.WriteLine($"Unexpected error: {ex.Message}");
            AnsiConsole.ResetColors();
            if (verbose)
            {
                AnsiConsole.WriteLine(ex.ToString());
            }
            return 1;
        }
    }
    
    private class StatusTable
    {
        private string? _statusText;
        private readonly Queue<string> _logLines = new();
        private const int MaxLogLines = 10;
        private int _spinnerStep;
        private readonly Style _spinnerStyle;

        public StatusTable()
        {
            Table = new Table();
            Table.AddColumn(new TableColumn("Status"));
            _spinnerStyle = Style.Parse("red");
        }

        public Table Table { get; }
        
        public void LogText(string text)
        {
            if (_logLines.Count > MaxLogLines)
            {
                _logLines.Dequeue();
            }

            _logLines.Enqueue(text);
        }
        
        public void Status(string text)
        {
            _statusText = text;
        }

        public void UpdateTable()
        {
            Table.Rows.Clear();

            if (_logLines.Count > 0)
            {
                var rows = new Rows(_logLines.Select(x => new Markup(x)));
                Table.AddRow(rows);
            }

            if (_statusText != null)
            {
                var tableColumn = Table.Columns[0];

                var spinnerStep = _spinnerStep;
                var spinnerText = Spinner.Known.Default.Frames[spinnerStep];
                _spinnerStep = (_spinnerStep + 1) % Spinner.Known.Default.Frames.Count;

                tableColumn.Header = new Markup($"[red]{spinnerText}[/] Status");
                tableColumn.Footer = new Markup(_statusText);
                Table.ShowFooters = true;
            }
        }
    }

    /// <summary>
    /// Live status display for a Paused profiling session: one row per capture that is still profiling or
    /// saving, updated as captures start, move to their save phase, finish, or fail - plus a shared log area
    /// for the profiled program's own stdout/stderr and short prompts. Its methods are called both from the
    /// live-profiling loop and from background save tasks, so all state is guarded by a single lock.
    /// </summary>
    private class CaptureBoard
    {
        private enum CapturePhase
        {
            Profiling,
            Saving,
            Completed,
            Failed,
        }

        private sealed class CaptureRow
        {
            public required int Index;
            public CapturePhase Phase;
            public string StatusText = "";
        }

        private readonly object _lock = new();
        private readonly SortedDictionary<int, CaptureRow> _rows = new();
        private readonly Queue<string> _logLines = new();
        private const int MaxLogLines = 8;
        private int _spinnerStep;
        private LiveDisplayContext? _liveCtx;

        public CaptureBoard()
        {
            Table = new Table();
            Table.AddColumn("Capture");
            Table.AddColumn("Status");

            var controls = new Rows(
                new Markup("[bold]Controls:[/]"),
                new Markup("  * [bold]SPACE[/] - start profiling"),
                new Markup("  * [bold]S[/]     - stop profiling and save the profile"),
                new Markup("  * [bold]Q[/]     - stop, save, and quit"));

            Display = new Rows(controls, Table);
        }

        public Table Table { get; }

        public IRenderable Display { get; }

        public void Attach(LiveDisplayContext liveCtx)
        {
            lock (_lock)
            {
                _liveCtx = liveCtx;
                Refresh();
            }
        }

        public void CaptureStarted(int index)
        {
            lock (_lock)
            {
                GetOrAddRow(index).Phase = CapturePhase.Profiling;
                Refresh();
            }
        }

        public void CaptureSaving(int index)
        {
            lock (_lock)
            {
                var row = GetOrAddRow(index);
                row.Phase = CapturePhase.Saving;
                row.StatusText = "Saving...";
                Refresh();
            }
        }

        public void StatusChanged(int index, string message)
        {
            lock (_lock)
            {
                GetOrAddRow(index).StatusText = message;
                Refresh();
            }
        }

        public void Completed(int index, string outputFile)
        {
            lock (_lock)
            {
                var row = GetOrAddRow(index);
                row.Phase = CapturePhase.Completed;
                row.StatusText = $"Saved -> {outputFile} ({ByteSize.FromBytes(new FileInfo(outputFile).Length)})";
                Refresh();
            }
        }

        public void Failed(int index, Exception ex)
        {
            lock (_lock)
            {
                var row = GetOrAddRow(index);
                row.Phase = CapturePhase.Failed;
                row.StatusText = $"Failed: {ex.Message}";
                Refresh();
            }
        }

        public void LogText(string text)
        {
            lock (_lock)
            {
                if (_logLines.Count > MaxLogLines)
                {
                    _logLines.Dequeue();
                }

                _logLines.Enqueue(text);
                Refresh();
            }
        }

        private CaptureRow GetOrAddRow(int index)
        {
            if (!_rows.TryGetValue(index, out var row))
            {
                row = new CaptureRow { Index = index };
                _rows[index] = row;
            }

            return row;
        }

        private void Refresh()
        {
            Table.Rows.Clear();

            if (_logLines.Count > 0)
            {
                Table.AddRow(new Markup("[grey]Output[/]"), new Rows(_logLines.Select(x => new Markup(x))));
            }

            var spinnerText = Spinner.Known.Default.Frames[_spinnerStep];
            _spinnerStep = (_spinnerStep + 1) % Spinner.Known.Default.Frames.Count;

            foreach (var row in _rows.Values)
            {
                var phaseText = row.Phase switch
                {
                    CapturePhase.Profiling => $"[red]{spinnerText}[/] Profiling",
                    CapturePhase.Saving => $"[yellow]{spinnerText}[/] Saving",
                    CapturePhase.Completed => "[green]✓[/] Done",
                    CapturePhase.Failed => "[red]✗[/] Failed",
                    _ => "",
                };

                Table.AddRow(new Markup($"Capture {row.Index}"), new Markup($"{phaseText} - {Markup.Escape(row.StatusText)}"));
            }

            _liveCtx?.Refresh();
        }
    }
}
