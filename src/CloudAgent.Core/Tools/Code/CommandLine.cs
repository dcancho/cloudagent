using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace CloudAgent.Core.Tools.Code;

/// <summary>Splits a command line into a program and its arguments, and finds the program on the path.</summary>
public static class CommandLine
{
    /// <summary>
    /// Split on whitespace, honoring double and single quotes. No shell runs, so operators such as
    /// <c>&amp;&amp;</c> or <c>|</c> are passed through as plain arguments.
    /// </summary>
    /// <exception cref="FormatException">A quote is never closed.</exception>
    public static IReadOnlyList<string> Split(string commandLine)
    {
        var parts = new List<string>();
        var current = new StringBuilder();
        var inToken = false;
        char? quote = null;

        foreach (var c in commandLine)
        {
            if (quote is not null)
            {
                if (c == quote)
                    quote = null;
                else
                    current.Append(c);
                continue;
            }

            if (c is '"' or '\'')
            {
                quote = c;
                inToken = true;
            }
            else if (char.IsWhiteSpace(c))
            {
                if (inToken)
                {
                    parts.Add(current.ToString());
                    current.Clear();
                    inToken = false;
                }
            }
            else
            {
                current.Append(c);
                inToken = true;
            }
        }

        if (quote is not null)
            throw new FormatException("Unterminated quote in command.");
        if (inToken)
            parts.Add(current.ToString());
        return parts;
    }

    /// <summary>The full path of a program found on PATH, trying PATHEXT extensions on Windows.</summary>
    public static string? Find(string program)
    {
        if (program.Contains(Path.DirectorySeparatorChar) || program.Contains(Path.AltDirectorySeparatorChar))
            return null;

        string[] extensions = OperatingSystem.IsWindows()
            ? (Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD")
                .Split(';', StringSplitOptions.RemoveEmptyEntries)
            : [""];
        if (OperatingSystem.IsWindows() && Path.HasExtension(program))
            extensions = ["", .. extensions];

        var directories = (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);

        return directories
            .SelectMany(d => extensions.Select(e => Path.Combine(d.Trim('"'), program + e)))
            .FirstOrDefault(File.Exists);
    }
}
