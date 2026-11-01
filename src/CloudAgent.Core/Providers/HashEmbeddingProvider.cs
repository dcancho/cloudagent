using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CloudAgent.Core.Abstractions;

namespace CloudAgent.Core.Providers;

/// <summary>
/// A dependency-free, deterministic embedding provider: it hashes word tokens into a
/// fixed-dimension bag-of-words vector and L2-normalises it. Not semantically deep, but it
/// captures lexical overlap — enough to run and test the whole indexing pipeline offline
/// with no API key. Swap in a real provider for genuine semantic matching.
/// </summary>
public sealed class HashEmbeddingProvider(int dimensions = 256) : IEmbeddingProvider
{
    private readonly int _dimensions = dimensions > 0
        ? dimensions
        : throw new ArgumentOutOfRangeException(nameof(dimensions));

    public string Name => "hash";

    public Task<IReadOnlyList<float[]>> EmbedAsync(
        IReadOnlyList<string> inputs, CancellationToken cancellationToken = default)
    {
        var vectors = new List<float[]>(inputs.Count);
        foreach (var input in inputs)
            vectors.Add(Embed(input));
        return Task.FromResult<IReadOnlyList<float[]>>(vectors);
    }

    private float[] Embed(string text)
    {
        var vector = new float[_dimensions];

        foreach (var token in Tokenize(text))
        {
            var hash = StableHash(token);
            var bucket = (int)(hash % (uint)_dimensions);
            // Signed contribution keeps buckets from only ever growing.
            vector[bucket] += (hash & 1) == 0 ? 1f : -1f;
        }

        Normalize(vector);
        return vector;
    }

    private static uint StableHash(string token)
    {
        var hash = 2166136261u;
        foreach (var b in System.Text.Encoding.UTF8.GetBytes(token))
            hash = (hash ^ b) * 16777619u;
        return hash;
    }

    private static IEnumerable<string> Tokenize(string text)
    {
        var start = -1;
        for (var i = 0; i < text.Length; i++)
        {
            if (char.IsLetterOrDigit(text[i]))
            {
                if (start < 0) start = i;
            }
            else if (start >= 0)
            {
                yield return text[start..i].ToLowerInvariant();
                start = -1;
            }
        }
        if (start >= 0)
            yield return text[start..].ToLowerInvariant();
    }

    private static void Normalize(float[] vector)
    {
        double sum = 0;
        foreach (var v in vector) sum += v * (double)v;
        var norm = Math.Sqrt(sum);
        if (norm <= 0) return;
        for (var i = 0; i < vector.Length; i++)
            vector[i] = (float)(vector[i] / norm);
    }
}
