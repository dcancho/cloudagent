using CloudAgent.Core.Providers;

namespace CloudAgent.Core.Tests.Providers;

public sealed class HashEmbeddingTests
{
    [Fact]
    public async Task The_same_text_always_gives_the_same_vector()
    {
        var provider = new HashEmbeddingProvider();

        var first = (await provider.EmbedAsync(["payroll money rounds at output"]))[0];
        var second = (await new HashEmbeddingProvider().EmbedAsync(["payroll money rounds at output"]))[0];

        Assert.Equal(first, second);
    }

    [Fact]
    public async Task A_known_text_maps_to_known_buckets_so_stored_vectors_survive_a_restart()
    {
        var vector = (await new HashEmbeddingProvider(16).EmbedAsync(["alpha"]))[0];

        Assert.Equal(1, vector.Count(v => v != 0));
        Assert.Equal(11, Array.FindIndex(vector, v => v != 0));
    }
}
