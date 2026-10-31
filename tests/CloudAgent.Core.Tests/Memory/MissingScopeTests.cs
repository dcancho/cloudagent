using CloudAgent.Core.Abstractions;
using CloudAgent.Core.Memory;
using CloudAgent.Core.Providers;
using CloudAgent.Core.Tests.Support;
using CloudAgent.Core.Tools;
using CloudAgent.Core.Tools.Memory;
using Xunit;

namespace CloudAgent.Core.Tests.Memory;

/// <summary>A group conversation keeps no personal memory: its store has no personal scope.</summary>
public sealed class MissingScopeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cloudagent-tests", Guid.NewGuid().ToString("N"));
    private readonly SqliteMemoryStore _store;
    private readonly MemoryManager _memory;

    public MissingScopeTests()
    {
        Directory.CreateDirectory(_root);
        _store = new SqliteMemoryStore(new Dictionary<MemoryScope, string>
        {
            [MemoryScope.Project] = Path.Combine(_root, "project.db"),
            [MemoryScope.Session] = Path.Combine(_root, "session.db"),
        });
        _memory = new MemoryManager(_store, new HashEmbeddingProvider());
    }

    public void Dispose()
    {
        _store.Dispose();
        Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task Remember_refuses_a_scope_the_conversation_does_not_keep()
    {
        var result = await new RememberTool(_memory).InvokeAsync(
            ToolInput.FromJson("""{ "content": "Diego prefers short answers", "scope": "personal" }"""));

        Assert.False(result.Success);
        Assert.Equal(0, _store.Count);
    }

    [Fact]
    public async Task Consolidation_drops_personal_facts_and_keeps_the_rest()
    {
        var provider = new ScriptedLlmProvider().Otherwise("""
            [
              { "scope": "personal", "fact": "Diego prefers short answers", "importance": 60 },
              { "scope": "project", "fact": "Money is decimal in this codebase", "importance": 80 }
            ]
            """);

        var stored = await new Consolidator(provider, _memory).ConsolidateAsync(
            [Message.User("Keep it short. And money is decimal here.")]);

        var fact = Assert.Single(stored);
        Assert.Equal(MemoryScope.Project, fact.Scope);
    }
}
