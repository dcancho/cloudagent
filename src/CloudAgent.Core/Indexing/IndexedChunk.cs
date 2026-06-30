namespace CloudAgent.Core.Indexing;

/// <summary>
/// One indexed unit: a slice of a source file with its embedding. Chunking keeps each
/// vector focused on a small span so search points at the right part of a large file.
/// </summary>
public sealed class IndexedChunk
{
    /// <summary>Stable id: "{source}#{ordinal}".</summary>
    public required string Id { get; init; }

    /// <summary>Workspace-relative path of the source file.</summary>
    public required string Source { get; init; }

    /// <summary>0-based position of this chunk within the source.</summary>
    public required int Ordinal { get; init; }

    /// <summary>The chunk's text.</summary>
    public required string Text { get; init; }

    /// <summary>The embedding vector for <see cref="Text"/>.</summary>
    public required float[] Embedding { get; init; }
}

/// <summary>A search result: the matched chunk and its similarity score (higher is closer).</summary>
/// <param name="Chunk">The matched chunk.</param>
/// <param name="Score">Cosine similarity in [-1, 1].</param>
public sealed record SearchHit(IndexedChunk Chunk, double Score);
