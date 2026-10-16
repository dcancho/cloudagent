using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace CloudAgent.Core.Workspaces;

/// <summary>
/// Which programs an agent may run inside a workspace. An allowlist guards against mistakes, not
/// against a determined caller: git hooks, MSBuild targets and npm scripts can still run arbitrary
/// code, so real containment comes from what the workspace holds and the credentials it lacks.
/// </summary>
public sealed class ExecutionPolicy
{
    private readonly HashSet<string> _programs;

    private ExecutionPolicy(IEnumerable<string> programs) =>
        _programs = programs.Select(Normalize).ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>Nothing may run.</summary>
    public static ExecutionPolicy None { get; } = new([]);

    /// <summary>Only the named programs may run, by bare name (<c>dotnet</c>, <c>git</c>).</summary>
    public static ExecutionPolicy Allow(IEnumerable<string> programs) => new(programs);

    public IReadOnlyCollection<string> AllowedPrograms => _programs;

    public bool CanExecute => _programs.Count > 0;

    public bool Allows(string program) => _programs.Contains(Normalize(program));

    private static string Normalize(string program) =>
        Path.GetFileNameWithoutExtension(program.Trim());
}
