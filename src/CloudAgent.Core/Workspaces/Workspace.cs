using System;
using System.IO;

namespace CloudAgent.Core.Workspaces;

/// <summary>
/// A local-filesystem workspace rooted at a directory on this machine. Created on first
/// use if it doesn't exist. Path resolution is hardened against traversal: anything that
/// would land outside the root is rejected, and so is the host's own state folder inside it.
/// </summary>
public sealed class Workspace : IWorkspace
{
    /// <summary>The folder under the root where the host keeps memory, transcripts and logs.</summary>
    public const string StateDirectory = ".agent";

    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public Workspace(string root, ExecutionPolicy? execution = null)
    {
        if (string.IsNullOrWhiteSpace(root))
            throw new ArgumentException("Workspace root must be provided.", nameof(root));

        Root = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        Directory.CreateDirectory(Root);
        Execution = execution ?? ExecutionPolicy.None;
    }

    public string Root { get; }

    public ExecutionPolicy Execution { get; }

    public string Resolve(string path)
    {
        var combined = string.IsNullOrWhiteSpace(path)
            ? Root
            : Path.IsPathRooted(path) ? path : Path.Combine(Root, path);

        var full = Path.GetFullPath(combined);

        if (!IsUnderRoot(full))
            throw new WorkspaceAccessException(path, Root);

        if (IsState(full))
            throw new WorkspaceAccessException(path, Root, $"Path '{path}' is the host's private state.");

        return full;
    }

    public bool IsState(string fullPath)
    {
        var state = Path.Combine(Root, StateDirectory);
        return string.Equals(fullPath, state, PathComparison)
            || fullPath.StartsWith(state + Path.DirectorySeparatorChar, PathComparison);
    }

    public string ToRelative(string absolutePath)
    {
        var full = Path.GetFullPath(absolutePath);
        return IsUnderRoot(full) ? Path.GetRelativePath(Root, full) : full;
    }

    private bool IsUnderRoot(string fullPath)
    {
        if (string.Equals(fullPath, Root, PathComparison))
            return true;

        return fullPath.StartsWith(Root + Path.DirectorySeparatorChar, PathComparison);
    }
}
