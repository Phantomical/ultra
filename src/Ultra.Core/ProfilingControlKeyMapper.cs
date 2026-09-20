// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

namespace Ultra.Core;

/// <summary>
/// Maps console key presses to a <see cref="ProfilingControlAction"/> for an interactive (<see cref="UltraProfilerOptions.Paused"/>) profiling session.
/// </summary>
public static class ProfilingControlKeyMapper
{
    /// <summary>
    /// Maps a console key press to the <see cref="ProfilingControlAction"/> it represents.
    /// </summary>
    /// <param name="key">The key that was pressed.</param>
    /// <param name="modifiers">The modifiers that were held down when the key was pressed.</param>
    /// <returns>
    /// <see cref="ProfilingControlAction.Start"/> for SPACE or ENTER, <see cref="ProfilingControlAction.Stop"/> for S,
    /// <see cref="ProfilingControlAction.Quit"/> for Q or CTRL+C, and <see cref="ProfilingControlAction.None"/> otherwise.
    /// </returns>
    public static ProfilingControlAction Map(ConsoleKey key, ConsoleModifiers modifiers)
    {
        if (key == ConsoleKey.C && (modifiers & ConsoleModifiers.Control) != 0)
        {
            return ProfilingControlAction.Quit;
        }

        return key switch
        {
            ConsoleKey.Spacebar or ConsoleKey.Enter => ProfilingControlAction.Start,
            ConsoleKey.S => ProfilingControlAction.Stop,
            ConsoleKey.Q => ProfilingControlAction.Quit,
            _ => ProfilingControlAction.None,
        };
    }
}
