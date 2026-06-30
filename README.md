# CloudAgent

An autonomous agent harness in .NET 10. The long-term goal is an agent that behaves
like a capable colleague: it has its own machine/workspace, and it can think, act,
remember, and learn over time.

This is the **foundation**: the core loop and the LLM provider seam behind it.

## Layout

```
src/
  CloudAgent.Core/              # the harness (no host concerns)
    Abstractions/
      Message.cs                # Role + Message: a turn in the working context
      LlmRequest.cs             # what the loop hands a provider each iteration
      LlmResponse.cs            # what comes back (text, stop reason, token usage)
      ILlmProvider.cs           # the single seam to any LLM backend
    Loop/
      AgentLoop.cs              # perceive -> think -> record, until cancelled
      AgentContext.cs           # the running history / working memory
      AgentOptions.cs           # model, system prompt, limits, pacing
      IInputSource.cs           # where stimulus comes from (+ NullInputSource)
    Providers/
      EchoLlmProvider.cs        # offline fake; runs with no API key
      AnthropicLlmProvider.cs   # real Anthropic Messages API over HTTP
      FoundryLlmProvider.cs     # Microsoft Foundry catalog (Azure AI Inference)
      HashEmbeddingProvider.cs  # offline deterministic embeddings
      FoundryEmbeddingProvider.cs  # Foundry catalog embeddings (/embeddings)
    Indexing/
      TextChunker.cs            # overlapping text windows
      IndexedChunk.cs           # a chunk + its embedding (+ SearchHit)
      VectorIndex.cs            # cosine search + JSON persistence
      WorkspaceIndexer.cs       # walk workspace -> chunk -> embed -> index
      Summarizer.cs             # LLM-backed summaries
    Workspaces/
      IWorkspace.cs             # the area the agent may act within (+ access guard)
      Workspace.cs              # local-filesystem, traversal-hardened workspace
    Tools/
      ITool.cs                  # a capability the agent can invoke
      ToolParameter.cs          # one declared argument (basis for a JSON schema)
      ToolInput.cs / ToolResult.cs
      ToolRegistry.cs           # the toolbox: dispatch by name
      Toolset.cs                # builds the default toolbox
      Files/                    # read_file, write_file, list_directory,
                                #   create_directory, delete_path
      Web/                      # IWebSearchProvider (+ Null, Tavily) and web_search
      Index/                    # index_workspace, search_index, summarize_file
  CloudAgent.Host/              # console runner that wires it together
    Program.cs
    ConsoleInputSource.cs       # interactive stdin input
```

## The loop

`AgentLoop.RunAsync` repeats one cycle until cancelled:

1. **Perceive** — pull any waiting input from the `IInputSource` and append it to the context.
2. **Think & act** — send the working context (and the available tools) to the
   `ILlmProvider`. While the model asks to use tools, run them, feed the results back, and
   ask again — until the model produces a plain answer.
3. **Record** — each completion, tool call, and tool result is folded back into the context.

Returning `null` from an input source means "nothing right now" — the agent keeps
running on its own initiative rather than blocking. That's the hook for true autonomy.

### Tool calling

When a `ToolRegistry` is supplied, the loop advertises the tools' JSON schemas on every
request and runs an inner think/act sub-loop: a model completion with `ToolCalls` triggers
tool execution, the results return as `Role.Tool` messages, and the loop re-prompts —
bounded by a per-iteration hop limit so it can't run away. Providers translate the shared
`ToolDefinition` / `ToolCall` abstraction to their own wire format:

- **Anthropic** — `tool_use` / `tool_result` content blocks.
- **Foundry** — Azure AI Inference `ChatCompletionsToolDefinition` + `tool` messages.
- **Echo** — ignores tools (always answers in plain text).

## The provider seam

`ILlmProvider` is the only thing the loop knows about an LLM. Swap implementations
freely:

- `EchoLlmProvider` — no dependencies, no key; lets the harness run end to end.
- `AnthropicLlmProvider` — calls the Anthropic Messages API directly over HTTP.
- `FoundryLlmProvider` — the Microsoft Foundry model catalog in chat-completions mode,
  via the `Azure.AI.Inference` SDK (`ChatCompletionsClient`, the `/chat/completions`
  route). One endpoint serves the whole catalog; choose the model per request
  (e.g. `DeepSeek-V3.1`, `mistral-large`, `gpt-4o-mini`). Auth is an API key, or
  Entra ID via `DefaultAzureCredential` when no key is given.

## Workspace — where the agent may act

`IWorkspace` is the agent's world: a single rooted directory it's allowed to act within.
`Workspace` is the local-filesystem implementation. Every filesystem tool resolves paths
through `Workspace.Resolve`, which is hardened against traversal — anything that would
land outside the root (`../`, an absolute path elsewhere) is rejected with a
`WorkspaceAccessException`. So the workspace is both the agent's home and its sandbox.

Default root is `./workspace`, overridable with `AGENT_WORKSPACE`.

## Tools — what the agent can do

`ITool` is one capability: a `Name`, a `Description`, declared `Parameters`, and an
`InvokeAsync(ToolInput) -> ToolResult`. `ToolInput` is JSON-object backed (matching what
an LLM emits for a tool call); `ToolParameter` carries JSON-schema-style types so the set
can later be emitted as a schema for provider-side tool calling. `ToolRegistry` is the
toolbox and dispatches by name, turning unexpected exceptions into `ToolResult.Fail`.

Built-in tools (`Toolset.CreateDefault`):

| Tool | Purpose |
| --- | --- |
| `read_file` | Read a text file in the workspace |
| `write_file` | Create/overwrite (or `append`) a text file |
| `list_directory` | List a folder's files and subfolders |
| `create_directory` | Create a folder (and parents) |
| `delete_path` | Delete a file or folder (`recursive` required for non-empty) |
| `web_search` | Search the web via an `IWebSearchProvider` |
| `index_workspace` | (Re)build the semantic index over workspace text files |
| `search_index` | Semantic (embedding) search over the index |
| `summarize_file` | LLM-generated summary of a workspace file |

Web search sits behind its own seam (`IWebSearchProvider`), mirroring the LLM provider:
`NullWebSearchProvider` (unconfigured default) and `TavilyWebSearchProvider` (set
`TAVILY_API_KEY`). Swap in Bing/Brave/etc. by implementing the interface.

The loop invokes these tools automatically when the model calls them (see *Tool calling*).

## Indexing — search and summarize by meaning

The agent can index its workspace and retrieve by meaning, not just keywords:

- **Embeddings** sit behind `IEmbeddingProvider` (same pattern as the LLM seam).
  `FoundryEmbeddingProvider` uses the Foundry catalog's `/embeddings` route;
  `HashEmbeddingProvider` is a deterministic offline fallback so the whole pipeline runs
  with no API key.
- **`WorkspaceIndexer`** walks the workspace, splits text files into overlapping chunks
  (`TextChunker`), embeds them, and stores them in a **`VectorIndex`** — an in-memory store
  with cosine search, persisted to `.agent/index.json`. Re-running refreshes changed files.
- **`Summarizer`** turns a file (or retrieved chunks) into a short description via the LLM.

Exposed to the agent as `index_workspace`, `search_index`, and `summarize_file`. Embedding
backend: Foundry when `FOUNDRY_ENDPOINT` is set (model via `FOUNDRY_EMBED_MODEL`), else the
offline hash provider.

This is the substrate for memory: once the workspace is indexed, the agent can pull in only
the relevant slices instead of carrying everything in the context window.

## Run it

```sh
# Offline (echo provider):
dotnet run --project src/CloudAgent.Host

# Against Anthropic:
$env:ANTHROPIC_API_KEY = "sk-ant-..."   # PowerShell
dotnet run --project src/CloudAgent.Host

# Against the Microsoft Foundry catalog (chat completions):
$env:FOUNDRY_ENDPOINT = "https://<resource>.services.ai.azure.com/models"
$env:FOUNDRY_API_KEY  = "<key>"          # omit to use Entra ID (DefaultAzureCredential)
$env:FOUNDRY_MODEL    = "gpt-4o-mini"           # any catalog chat model
$env:FOUNDRY_EMBED_MODEL = "text-embedding-3-small"  # catalog embedding model
dotnet run --project src/CloudAgent.Host
```

Provider precedence: `FOUNDRY_ENDPOINT` → `ANTHROPIC_API_KEY` → echo.

Type a message and press Enter. Type `exit` (or Ctrl+C) to stop.

## Where this is headed

The loop is deliberately a thin spine so the colleague-like capabilities hang off it:

- **Tools / actions** — ✅ done: schemas advertised to the model, a think/act sub-loop
  executes calls and feeds results back. More tools (shell, HTTP, code execution) plug in
  via `ITool`.
- **Memory** — 🚧 in progress: semantic **indexing** is in place (embeddings + vector
  search + summaries). Next: have `AgentContext` retrieve from the index and trim/summarise
  history so the agent runs long without overflowing the context window.
- **Learning** — persist context and distilled lessons across runs in the workspace.
- **Autonomy** — non-blocking input sources (queues, schedulers, sensors) so the agent
  acts without being prompted.
