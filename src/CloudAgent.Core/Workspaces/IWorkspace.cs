using System;

namespace CloudAgent.Core.Workspaces;

/// <summary>
/// The agent's world: the single rooted area it is allowed to act within. Every tool
/// that touches the filesystem resolves paths through here, so the workspace is both
/// the agent's home and its sandbox — nothing escapes <see cref="Root"/>.
/// </summary>
public interface IWorkspace
{
    /// <summary>Absolute path to the workspace root.</summary>
    string Root { get; }

    /// <summary>
    /// Turn a workspace-relative (or in-bounds absolute) path into a full path, after
    /// verifying it stays inside <see cref="Root"/>.
    /// </summary>
    /// <exception cref="WorkspaceAccessException">The path would escape the workspace.</exception>
    string Resolve(string path);

    /// <summary>Express an absolute in-bounds path relative to <see cref="Root"/>, for display.</summary>
    string ToRelative(string absolutePath);
}

/// <summary>Thrown when a path resolves outside the workspace boundary.</summary>
public sealed class WorkspaceAccessException(string attemptedPath, string root)
    : Exception($"Path '{attemptedPath}' resolves outside the workspace root '{root}'.")
{
    public string AttemptedPath { get; } = attemptedPath;
    public string Root { get; } = root;
}
