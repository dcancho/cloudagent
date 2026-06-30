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
      AgentLoop.cs              # perceive -> think/act -> record -> compact
      AgentContext.cs           # working memory (history, rolling summary, eviction)
      AgentOptions.cs           # model, system prompt, limits, pacing, memory budget
      ContextCompactor.cs       # token-budget trim + rolling summary + archival
      IInputSource.cs           # where stimulus comes from (+ NullInputSource)
    Memory/
      MemoryEntry.cs            # a memory + scope/kind/importance (+ MemoryHit)
      IMemoryStore.cs           # durable, scope-partitioned memory storage
      FileMemoryStore.cs        # JSON-per-scope store with cosine recall
      MemoryManager.cs          # remember / recall / archive policy (own embedder)
      Consolidator.cs           # LLM-extracted durable facts during compaction
    Todos/
      TodoItem.cs               # a taskboard entry (id, title, status)
      TodoStore.cs              # persistent taskboard (JSON in the workspace)
    Agents/
      AgentMessage.cs           # inter-agent message envelope (+ kinds)
      AgentHandle.cs            # one live agent: mailbox, status, cancellation, completion
      AgentBus.cs               # registry + router + ask/answer + supervised stop
      MailboxInputSource.cs     # an agent's mailbox as the loop's IInputSource
      AgentOrchestrator.cs      # spawns + runs child agents (own context/memory)
    Surfaces/
      ISurface.cs               # a channel to the world (start receiving + send)
      SurfaceMessages.cs        # InboundMessage / OutboundMessage
      AgentConversation.cs      # one conversation = one agent session
      ConversationRouter.cs     # routes (surface, chat) <-> agent sessions
      TelegramSurface.cs        # Telegram Bot API via long polling (zero-dep)
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
      Memory/                   # remember
      Todos/                    # todo_list/add/complete/remove/clear
  CloudAgent.Surfaces/          # surface adapters with heavier deps
    WebchatSurface.cs           # self-hosted web chat (ASP.NET + WebSocket)
    DiscordSurface.cs           # Discord gateway (Discord.Net)
  CloudAgent.Host/              # console runner that wires it together
    Program.cs
    CliSurface.cs               # the console as a surface
```

Web chat is the easiest to try — it needs no account:

```sh
WEBCHAT=1 dotnet run --project src/CloudAgent.Host   # then open http://localhost:5080/
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
| `remember` | Save a durable fact to memory (personal/project/session) |
| `todo_list` / `todo_add` / `todo_complete` / `todo_remove` / `todo_clear` | Manage a persistent taskboard |

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

## Memory

The agent remembers across a long run and across sessions, in the coworker model.

**Working memory & compaction.** `AgentContext` holds the live conversation. When it exceeds
`WorkingMemoryBudgetTokens` (user-configurable; env `AGENT_MEMORY_BUDGET_TOKENS`), the loop's
`ContextCompactor` evicts the oldest turns — snapping the kept window to a clean user-turn
boundary so a tool call is never split from its result — folds them into a **rolling summary**
(carried alongside the system prompt), and **archives the evicted turns as episodic memory**.
Nothing is lost: trimmed turns are embedded with a cheap local embedder and stay recallable.

**Durable memory, scoped.** `IMemoryStore` partitions memories three ways:

| Scope | About | Location |
| --- | --- | --- |
| **Personal** | the user and the agent's working style; spans projects | user-global `~/.cloudagent/memory/` |
| **Project** | this workspace: architecture, decisions, constraints | `.agent/memory/project.json` |
| **Session** | the current run (incl. episodic archive) | `.agent/memory/sessions/<id>.json` |

`MemoryManager` owns its own embedder (a local hash provider by default, so high-volume
archival and recall stay free) and provides `RememberAsync` (curated facts), `RecallAsync`
(cosine over scopes), and `ArchiveAsync` (episodic).

**Curating and recalling.** The agent saves durable facts explicitly with the `remember`
tool (it picks the scope). Each turn, the loop **automatically recalls** before thinking:
it embeds the latest user message, searches all scopes, and injects the top `RecallTopK`
memories plus any others scoring at/above `RecallThreshold` (0.80) into the system prompt.

**Learning (consolidation).** When turns are compacted away, the `Consolidator` asks the LLM
to extract durable, general facts from them and classify each as personal / project / session,
then writes the new ones to memory (near-duplicates are skipped via a cosine check). Learning
happens exactly when information would otherwise be summarised out — so the agent accumulates
knowledge without being told to `remember`.

> Status: compaction + episodic archival, the scoped store, the `remember` tool, automatic
> recall injection, and consolidation-during-compaction are all wired into the loop.

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

## Multi-agent (concurrent)

Subagents run **concurrently**, not as blocking calls — so a child can ask its parent a
question mid-task while the parent keeps working. Each agent is an `AgentLoop` running as its
own `Task` with a **mailbox** (`Channel<AgentMessage>`); the `AgentBus` registers agents,
routes messages, correlates questions with answers, and propagates stop down the tree.

The trick that makes this fit cleanly: `IInputSource` already means "next stimulus, or null to
keep running" — so a `MailboxInputSource` turns inter-agent messages into ordinary loop input.
A child's question is, to the parent's loop, just another turn to think about.

Why nobody deadlocks: **parents never block on children** (they dispatch and keep looping, so
they're always free to answer); a child may block on a single question, and the answer is
delivered straight to that waiting call. Subagents share the workspace (collaborate on files)
but get their **own context and own memory** — they are children with scoped tasks, not
extensions of the parent.

The `AgentOrchestrator` spawns a child as a real `AgentLoop` (own context/memory, mailbox
input, the scoped task delivered as its first message). Tools wire it up:

- **Parent-side:** `spawn_agent`, `delegate_task` (blocking — spawn and await the result),
  `list_agents`, `reply_to_agent` (answer a child's question), `stop_agent`.
- **Child-side:** `ask_parent` (blocking question), `report_progress`, `finish` (report result
  and wind down).

A parent perceives both the human and its children through a `CompositeInputSource`, so a
child's question is just another turn to answer.

> Status: concurrency core + orchestrator + tools are in place and tested end to end (a parent
> spawns a worker, the worker asks a question, the parent answers, the worker finishes with a
> result). Next: wiring the spawn tools into the host's root agent so a live model can drive it.

## Surfaces (channels to the world)

The agent reaches people where they are, not just at a terminal. A **surface** is a
bidirectional message transport (`ISurface`): start receiving (a handler per inbound message)
and send replies out — each adapter hides its own transport (long-poll, websocket, webhook).

The model reuses the same idea as the mailbox: an inbound message is a stimulus, a reply is a
message back. The `ConversationRouter` maps each `(surface, conversationId)` to its **own agent
session** (own context + memory) and ships replies back to the originating surface — so many
chats run side by side, like a colleague juggling threads. The CLI is no longer special; it's
just `CliSurface`.

| Surface | Transport | Enable with | Status |
| --- | --- | --- | --- |
| CLI | stdin/stdout | always on | ✅ |
| Web chat | ASP.NET + WebSocket | `WEBCHAT=1` (port `WEBCHAT_PORT`, default 5080) | ✅ |
| Telegram | Bot API long-poll | `TELEGRAM_BOT_TOKEN` | ✅ |
| Discord | gateway (Discord.Net) | `DISCORD_BOT_TOKEN` (+ Message Content intent) | ✅ |

Telegram and Discord adapters live in `CloudAgent.Surfaces` (Discord.Net + ASP.NET for web
chat); Telegram and the abstraction are in Core. Each surface runs on its own thread, so a
blocking adapter (the console reader) never stalls the others.

> Foundation note: per-conversation sessions currently share the memory/index/todo stores,
> which assume single-threaded use. Multi-conversation concurrency needs per-conversation
> isolation or locking on those stores — a tracked follow-up.

## Where this is headed

The loop is deliberately a thin spine so the colleague-like capabilities hang off it:

- **Tools / actions** — ✅ done: schemas advertised to the model, a think/act sub-loop
  executes calls and feeds results back. More tools (shell, HTTP, code execution) plug in
  via `ITool`.
- **Memory** — ✅ core in place: semantic **indexing**, **context compaction with episodic
  archival**, a **scoped store** (personal/project/session), the **`remember` tool**,
  **automatic recall injection**, and **consolidation** (learned facts during compaction).
  Possible follow-ups: importance/recency decay and forgetting, and a manual deep-`recall` tool.
- **Learning** — persist context and distilled lessons across runs in the workspace.
- **Autonomy** — non-blocking input sources (queues, schedulers, sensors) so the agent
  acts without being prompted.
