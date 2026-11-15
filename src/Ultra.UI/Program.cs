// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

using Avalonia;
using Ultra.UI.Worker;

namespace Ultra.UI;

/// <summary>
/// Entry point of the ultra UI. The same executable also runs the profiling worker (see <see cref="WorkerHost"/>).
/// </summary>
internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length == 2 && args[0] == WorkerHost.WorkerArgument)
        {
            return WorkerHost.RunAsync(args[1]).GetAwaiter().GetResult();
        }

        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    /// <summary>
    /// Configures the Avalonia application (also used by the designer).
    /// </summary>
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
