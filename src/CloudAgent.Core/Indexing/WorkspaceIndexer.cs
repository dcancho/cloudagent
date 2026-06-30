using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CloudAgent.Core.Abstractions;
using CloudAgent.Core.Workspaces;

namespace CloudAgent.Core.Indexing;

/// <summary>Tuning for what gets indexed and how.</summary>
public sealed class IndexerOptions
{
    /// <summary>Only files with these extensions are indexed (lower-case, with dot).</summary>
    public IReadOnlySet<string> TextExtensions { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".md", ".markdown", ".rst", ".json", ".jsonl", ".csv", ".tsv",
        ".xml", ".yaml", ".yml", ".html", ".htm", ".css",
        ".cs", ".fs", ".js", ".ts", ".py", ".java", ".go", ".rs", ".rb", ".php",
        ".c", ".cpp", ".h", ".hpp", ".sh", ".ps1", ".sql", ".toml", ".ini", ".cfg",
    };

    /// <summary>Folders skipped anywhere in the tree.</summary>
    public IReadOnlyCollection<string> ExcludedDirectories { get; init; } =
        [".agent", ".git", "bin", "obj", "node_modules", ".vs"];

    public long MaxFileBytes { get; init; } = 1_000_000;
    public int ChunkSize { get; init; } = 1200;
    public int Overlap { get; init; } = 200;
    public int EmbedBatchSize { get; init; } = 32;
}

/// <summary>Outcome of an indexing pass.</summary>
/// <param name="FilesIndexed">Files read and embedded.</param>
/// <param name="ChunksIndexed">Chunks added.</param>
/// <param name="FilesSkipped">Files passed over (binary, too large, excluded).</param>
public sealed record IndexStats(int FilesIndexed, int ChunksIndexed, int FilesSkipped);

/// <summary>
/// Walks the workspace, chunks text files, embeds the chunks, and fills a
/// <see cref="VectorIndex"/>. Re-running refreshes each file (old chunks for a file are
/// dropped before its new ones are added), so it's safe to call repeatedly.
/// </summary>
public sealed class WorkspaceIndexer(
    IWorkspace workspace,
    IEmbeddingProvider embeddings,
    VectorIndex index,
    IndexerOptions? options = null)
{
    private readonly IndexerOptions _options = options ?? new IndexerOptions();

    public async Task<IndexStats> IndexAsync(CancellationToken cancellationToken = default)
    {
        int files = 0, chunks = 0, skipped = 0;

        foreach (var file in Directory.EnumerateFiles(workspace.Root, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var relative = workspace.ToRelative(file);

            if (IsExcluded(relative) || !_options.TextExtensions.Contains(Path.GetExtension(file)))
            {
                skipped++;
                continue;
            }

            var info = new FileInfo(file);
            if (info.Length > _options.MaxFileBytes)
            {
                skipped++;
                continue;
            }

            var text = await File.ReadAllTextAsync(file, cancellationToken).ConfigureAwait(false);
            var pieces = TextChunker.Split(text, _options.ChunkSize, _options.Overlap);
            if (pieces.Count == 0)
            {
                skipped++;
                continue;
            }

            index.RemoveSource(relative);
            chunks += await EmbedAndAddAsync(relative, pieces, cancellationToken).ConfigureAwait(false);
            files++;
        }

        return new IndexStats(files, chunks, skipped);
    }

    private async Task<int> EmbedAndAddAsync(
        string source, IReadOnlyList<string> pieces, CancellationToken cancellationToken)
    {
        var added = 0;

        for (var offset = 0; offset < pieces.Count; offset += _options.EmbedBatchSize)
        {
            var batch = pieces.Skip(offset).Take(_options.EmbedBatchSize).ToArray();
            var vectors = await embeddings.EmbedAsync(batch, cancellationToken).ConfigureAwait(false);

            for (var i = 0; i < batch.Length; i++)
            {
                var ordinal = offset + i;
                index.Add(new IndexedChunk
                {
                    Id = $"{source}#{ordinal}",
                    Source = source,
                    Ordinal = ordinal,
                    Text = batch[i],
                    Embedding = vectors[i],
                });
                added++;
            }
        }

        return added;
    }

    private bool IsExcluded(string relativePath)
    {
        var segments = relativePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return segments.Any(segment =>
            _options.ExcludedDirectories.Contains(segment, StringComparer.OrdinalIgnoreCase));
    }
}
