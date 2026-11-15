// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

using System.Text;

namespace Ultra.UI.Services;

/// <summary>
/// Splits a command line typed by the user into the individual arguments of the program to launch, with the rules
/// Windows programs use to parse their command line (<c>CommandLineToArgvW</c> and the Microsoft C runtime).
/// </summary>
internal static class CommandLineSplitter
{
    /// <summary>
    /// Splits <paramref name="commandLine"/> into arguments.
    /// </summary>
    /// <param name="commandLine">The arguments as typed on a command line, without the program name.</param>
    /// <returns>The arguments.</returns>
    public static List<string> Split(string? commandLine)
    {
        var arguments = new List<string>();
        if (string.IsNullOrWhiteSpace(commandLine))
        {
            return arguments;
        }

        var current = new StringBuilder();
        bool inQuotes = false;
        bool hasArgument = false;
        int i = 0;
        while (i < commandLine.Length)
        {
            var c = commandLine[i];
            if (c == '\\')
            {
                int backslashes = 0;
                while (i < commandLine.Length && commandLine[i] == '\\')
                {
                    backslashes++;
                    i++;
                }

                if (i < commandLine.Length && commandLine[i] == '"')
                {
                    // 2n backslashes + quote: n backslashes and the quote is a delimiter; 2n+1: n backslashes and a literal quote.
                    current.Append('\\', backslashes / 2);
                    if (backslashes % 2 == 1)
                    {
                        current.Append('"');
                        i++;
                    }
                }
                else
                {
                    current.Append('\\', backslashes);
                }

                hasArgument = true;
                continue;
            }

            if (c == '"')
            {
                if (inQuotes && i + 1 < commandLine.Length && commandLine[i + 1] == '"')
                {
                    // "" inside quotes is a literal quote.
                    current.Append('"');
                    i += 2;
                }
                else
                {
                    inQuotes = !inQuotes;
                    i++;
                }

                hasArgument = true;
                continue;
            }

            if (!inQuotes && char.IsWhiteSpace(c))
            {
                if (hasArgument)
                {
                    arguments.Add(current.ToString());
                    current.Clear();
                    hasArgument = false;
                }

                i++;
                continue;
            }

            current.Append(c);
            hasArgument = true;
            i++;
        }

        if (hasArgument)
        {
            arguments.Add(current.ToString());
        }

        return arguments;
    }
}
