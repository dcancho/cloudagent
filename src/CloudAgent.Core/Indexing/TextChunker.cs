using System;
using System.Collections.Generic;

namespace CloudAgent.Core.Indexing;

/// <summary>
/// Splits text into overlapping windows. Overlap keeps context from being cut mid-thought
/// at a boundary; windows prefer to end on whitespace so chunks read cleanly.
/// </summary>
public static class TextChunker
{
    public static IReadOnlyList<string> Split(string text, int chunkSize = 1200, int overlap = 200)
    {
        if (string.IsNullOrWhiteSpace(text))
            return [];
        if (chunkSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(chunkSize));
        if (overlap < 0 || overlap >= chunkSize)
            throw new ArgumentOutOfRangeException(nameof(overlap));

        text = text.Trim();
        if (text.Length <= chunkSize)
            return [text];

        var chunks = new List<string>();
        var start = 0;

        while (start < text.Length)
        {
            var end = Math.Min(start + chunkSize, text.Length);

            // Try to end on a whitespace boundary within the last quarter of the window.
            if (end < text.Length)
            {
                var floor = start + (chunkSize * 3 / 4);
                for (var i = end; i > floor; i--)
                {
                    if (char.IsWhiteSpace(text[i]))
                    {
                        end = i;
                        break;
                    }
                }
            }

            var piece = text[start..end].Trim();
            if (piece.Length > 0)
                chunks.Add(piece);

            if (end >= text.Length)
                break;

            start = Math.Max(end - overlap, start + 1);
        }

        return chunks;
    }
}
