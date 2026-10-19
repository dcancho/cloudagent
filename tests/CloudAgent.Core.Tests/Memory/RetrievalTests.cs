using CloudAgent.Core.Memory;
using CloudAgent.Core.Tests.Support;

namespace CloudAgent.Core.Tests.Memory;

public sealed class RetrievalTests : IDisposable
{
    private readonly MemoryFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    private async Task SeedAsync()
    {
        await _fixture.AddAsync("Payroll money rounds half away from zero at output.");
        await _fixture.AddAsync("Payroll overtime is capped at ten hours per week.");
        await _fixture.AddAsync("Payroll exports go to the bank every Friday.");
    }

    private static Task<RecallResult> RetrieveAsync(MemoryRetriever retriever, string query, int budgetChars = 0) =>
        retriever.RetrieveAsync(query, MemoryFixture.AllScopes, topK: 3, threshold: 0.8, budgetChars);

    [Fact]
    public async Task Store_recall_skips_cold_unless_asked()
    {
        var cold = await _fixture.AddAsync("Payroll money rounds at output.", residency: MemoryResidency.Cold);
        var resident = await _fixture.AddAsync("Payroll money is decimal.", residency: MemoryResidency.Resident);

        var plain = await _fixture.Memory.RecallAsync("payroll money", MemoryFixture.AllScopes, 10);
        var withCold = await _fixture.Memory.RecallAsync("payroll money", MemoryFixture.AllScopes, 10, includeCold: true);

        Assert.DoesNotContain(plain, hit => hit.Entry.Id == cold.Id);
        Assert.Contains(withCold, hit => hit.Entry.Id == cold.Id);
        Assert.DoesNotContain(withCold, hit => hit.Entry.Id == resident.Id);
    }

    [Fact]
    public async Task The_lexical_leg_finds_an_exact_identifier()
    {
        var target = await _fixture.AddAsync("Deploys run through the script tools/ship-it.ps1 on the build agent.");
        await SeedAsync();

        var hits = await _fixture.Memory.RecallAsync("ship-it.ps1", MemoryFixture.AllScopes, 1);

        Assert.Equal(target.Id, Assert.Single(hits).Entry.Id);
    }

    [Fact]
    public async Task The_reranker_choice_replaces_first_stage_order()
    {
        await SeedAsync();
        var model = new ScriptedLlmProvider().When(ScriptedLlmProvider.IsReranker, "[3]");
        var retriever = new MemoryRetriever(_fixture.Memory, reranker: new MemoryReranker(model));
        var firstStage = await _fixture.Memory.RecallAsync("payroll money rounding", MemoryFixture.AllScopes, 30);

        var result = await RetrieveAsync(retriever, "payroll money rounding");

        Assert.Equal(firstStage[2].Entry.Id, Assert.Single(result.Hits).Entry.Id);
    }

    [Fact]
    public async Task An_empty_rerank_injects_nothing()
    {
        await SeedAsync();
        var model = new ScriptedLlmProvider().When(ScriptedLlmProvider.IsReranker, "[]");
        var retriever = new MemoryRetriever(_fixture.Memory, reranker: new MemoryReranker(model));

        var result = await RetrieveAsync(retriever, "payroll money rounding");

        Assert.Empty(result.Hits);
    }

    [Fact]
    public async Task A_failed_rerank_falls_back_to_first_stage_order()
    {
        await SeedAsync();
        var model = new ScriptedLlmProvider().Throws(ScriptedLlmProvider.IsReranker);
        var withFailure = new MemoryRetriever(_fixture.Memory, reranker: new MemoryReranker(model));
        var plain = new MemoryRetriever(_fixture.Memory);

        var failed = await RetrieveAsync(withFailure, "payroll money rounding");
        var expected = await RetrieveAsync(plain, "payroll money rounding");

        Assert.Equal(expected.Hits.Select(h => h.Entry.Id), failed.Hits.Select(h => h.Entry.Id));
        Assert.NotEmpty(failed.Hits);
    }

    [Fact]
    public async Task The_rewritten_query_is_what_gets_searched()
    {
        var target = await _fixture.AddAsync("Deploys run through tools/ship-it.ps1.");
        await SeedAsync();
        var model = new ScriptedLlmProvider().When(ScriptedLlmProvider.IsRewriter, "ship-it.ps1 deploy script");
        var retriever = new MemoryRetriever(_fixture.Memory, new QueryRewriter(model));

        var result = await RetrieveAsync(retriever, "how do I get this live?");

        Assert.Equal(target.Id, result.Hits[0].Entry.Id);
    }

    [Fact]
    public async Task A_failed_rewrite_searches_the_original_text()
    {
        var model = new ScriptedLlmProvider().Throws(ScriptedLlmProvider.IsRewriter);

        Assert.Equal("how do I get this live?", await new QueryRewriter(model).RewriteAsync("how do I get this live?"));
    }

    [Fact]
    public async Task Light_recall_makes_no_model_calls()
    {
        await SeedAsync();
        var model = new ScriptedLlmProvider().Otherwise("[1]");
        var retriever = new MemoryRetriever(_fixture.Memory, new QueryRewriter(model), new MemoryReranker(model));

        await retriever.RetrieveAsync("payroll money", MemoryFixture.AllScopes, 3, 0.8, 0, light: true);

        Assert.Empty(model.Requests);
    }

    [Fact]
    public async Task The_budget_trims_hits_but_always_keeps_the_first()
    {
        await SeedAsync();
        var retriever = new MemoryRetriever(_fixture.Memory);

        var tight = await RetrieveAsync(retriever, "payroll", budgetChars: 10);
        var loose = await RetrieveAsync(retriever, "payroll", budgetChars: 0);

        Assert.Single(tight.Hits);
        Assert.Equal(3, loose.Hits.Count);
    }
}
