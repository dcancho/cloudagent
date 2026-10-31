namespace CloudAgent.Core.Abstractions;

/// <summary>What a model call is for, so spending can be told apart by job and not only by model.</summary>
public static class LlmPurpose
{
    /// <summary>A step of an agent's own turn: the reply or a tool-calling round.</summary>
    public const string Turn = "turn";

    /// <summary>The context router deciding what a turn needs.</summary>
    public const string Router = "router";

    /// <summary>Rewriting a turn into a memory search query.</summary>
    public const string Rewrite = "rewrite";

    /// <summary>Ranking recalled memories by usefulness.</summary>
    public const string Rerank = "rerank";

    /// <summary>Judging after a turn which injected memories helped.</summary>
    public const string Judge = "judge";

    /// <summary>Summarizing text: the rolling summary and file summaries.</summary>
    public const string Summary = "summary";

    /// <summary>Updating the operational-state snapshot at compaction.</summary>
    public const string Snapshot = "snapshot";

    /// <summary>Extracting durable facts from evicted or quiet conversation.</summary>
    public const string Consolidation = "consolidation";

    /// <summary>The sleep job distilling archived episodes.</summary>
    public const string Sleep = "sleep";

    /// <summary>Summarizing or answering from a fetched web page.</summary>
    public const string WebPage = "webpage";

    /// <summary>A call that did not say what it is for.</summary>
    public const string Unlabeled = "unlabeled";
}
