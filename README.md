# CloudAgent

An autonomous agent harness in .NET 10, built around one question: how does an agent
remember, decide what to pay attention to, and learn from its own history over time?

The harness is a reference implementation. The ideas behind it are written up separately in
[docs/design](docs/design/README.md), so you can read the design without the code.

## What is distinctive

- **Memory that measures itself.** Every memory injected into a prompt is later judged for
  whether it was actually used. That record drives promotion, demotion, and suppression.
- **A sleep job.** Between conversations, a consolidation pass moves memories between strata
  mechanically and proposes distilled facts that wait for human approval. Every change is written
  to a hash-chained ledger.
- **Attention as its own module.** What goes into the context window is assembled by one
  component, separate from memory storage and from the loop, with a metacognitive gate that
  withholds memories whose track record says they keep not applying.
- **Cache-aware prompts.** Stable and volatile content are split so the provider's prompt cache
  keeps working.

Design documents:

| Document | Covers |
| --- | --- |
| [Eight axes](docs/design/eight-axes.md) | The frame: what an agent is made of and which parts this project owns |
| [Memory and learning](docs/design/memory-and-learning.md) | Strata, hybrid retrieval, the utility loop, the sleep job |
| [Attention and metacognition](docs/design/attention-and-metacognition.md) | Context assembly, cache awareness, the confidence gate |
| [Roadmap](docs/design/roadmap.md) | What is built, what is partial, what is only designed |
| [References](docs/design/references.md) | Related work |

## Layout

```
src/
  CloudAgent.Core/        the harness, no host concerns
    Abstractions/         messages, requests, ILlmProvider, IEmbeddingProvider
    Loop/                 AgentLoop, working context, compaction, operational state
    Attention/            context assembly and the metacognitive gate
    Memory/               stores, retrieval pipeline, utility tracking, sleep job, ledger
    Indexing/             chunking, embeddings, vector index, summaries
    Agents/               concurrent subagents: bus, mailboxes, orchestrator
    Surfaces/             ISurface, conversation router, Telegram
    Providers/            Echo (offline), Anthropic, Foundry, embedding providers
    Tools/                ITool, registry, file, web, index, memory, todo, agent tools
    Workspaces/           the sandbox the agent acts within
  CloudAgent.Surfaces/    web chat and Discord adapters
  CloudAgent.Host/        console runner, typed configuration, composition root
web/                      React client for the web chat (see web/README.md)
docs/
  design/                 the ideas, independent of the code
  consolidation-policy.md the judge prompt used by the sleep job (loaded at runtime)
```

## Run it

With no provider configured the agent runs offline on an echo provider.

```powershell
dotnet run --project src/CloudAgent.Host
```

Against Anthropic:

```powershell
$env:ANTHROPIC_API_KEY = "<key>"
dotnet run --project src/CloudAgent.Host
```

Against the Microsoft Foundry model catalog:

```powershell
$env:FOUNDRY_ENDPOINT    = "https://<resource>.services.ai.azure.com/models"
$env:FOUNDRY_API_KEY     = "<key>"
$env:FOUNDRY_MODEL       = "gpt-4o-mini"
$env:FOUNDRY_EMBED_MODEL = "text-embedding-3-small"
dotnet run --project src/CloudAgent.Host
```

Provider precedence is `CLAUDE_CLI`, then `FOUNDRY_ENDPOINT`, then `ANTHROPIC_API_KEY`, then echo.
`AGENT_SMALL_MODEL` routes background jobs (summaries, judging, consolidation) to a cheaper model.

For local use on Windows, copy `agent.secrets.example.ps1` to `agent.secrets.ps1` (gitignored),
fill it in, and run `./run.ps1`. The web chat is then at http://localhost:5080/.

### Configuration

| Variable | Purpose | Default |
| --- | --- | --- |
| `AGENT_WORKSPACE` | Directory the agent may act within | `./workspace` |
| `AGENT_MEMORY_BUDGET_TOKENS` | Working-memory budget before compaction | 8000 |
| `SLEEP_INTERVAL_MIN` | Minutes between consolidation passes, 0 or less disables | 30 |
| `CONSOLIDATION_POLICY` | Path to the consolidation policy document | `./docs/consolidation-policy.md` |
| `WEBCHAT`, `WEBCHAT_PORT` | Enable the web chat surface and set its port | off, 5080 |
| `TELEGRAM_BOT_TOKEN`, `DISCORD_BOT_TOKEN` | Enable those surfaces | off |
| `TAVILY_API_KEY` | Real web search | no-op search |
| `TURN_TAKING` | `0` answers every message on its own instead of waiting for the end of a burst | on |
| `CONTEXT_ROUTING` | Who chooses per turn how much memory to recall and which tool categories to offer (see [context routing](docs/design/proposals/context-routing.md)): `decision` for the Foundry decision model, `generative` for the background chat model, `off` for the full pipeline every turn. Decisions go to `.agent/routing.jsonl` | `decision` when the two variables below are set, else `off` |
| `CODE_REPOS` | Repositories a coding agent may work on, as `name=path` pairs separated by `;` | none |
| `CODE_COMMANDS` | Programs a coding agent may run in them, comma separated | `dotnet,git,npm,node` |
| `FOUNDRY_DECISIONS_ENDPOINT`, `DECISIONS_MODEL` | The Foundry decision API endpoint (ending in `/providers/microsoft/v1/systemone`) and the deployment name, for example Microsoft-Decision-1. Uses `FOUNDRY_API_KEY`, or Entra ID without it | unset |

### Records

Everything the agent does is kept under the workspace's `.agent` folder, which is not in git:

| Path | Contents |
| --- | --- |
| `transcripts/<agent>.jsonl` | One line per event of each agent: messages, model replies with tool calls and token counts, tool results, routing, recall, judging, errors |
| `logs/host-<date>.log` | The host's console output |
| `routing.jsonl` | Each routing decision and how its turn ended (completed, failed, or cancelled), including the router's misses |
| `conversations/<agent>.json` | Each conversation's working context, saved after every turn so it continues after a restart |
| `memory/` | The memory databases |

Deleting a web chat conversation ends its session and removes its transcript, saved context, and
session memory. What it added to personal and project memory stays, and so do the ledgers.

## The loop

`AgentLoop.RunAsync` repeats one cycle until cancelled: perceive new input, assemble the
context, think and act (tool calls run in a bounded inner loop), and record the results. An
input source returning nothing means the agent keeps running on its own initiative.

`ILlmProvider` is the only thing the loop knows about a model. `ITool` is one capability with a
name, description, declared parameters, and an invoke method. `ISurface` is one channel to the
world. Each is a small interface with several implementations behind it.

## Tools

Files in the workspace, web search, the semantic index and summaries, a taskboard, memory, and
subagents. Web pages are read at three levels of detail, following the
[fidelity ladder](docs/design/proposals/fidelity-ladder.md):

| Tool | Returns |
| --- | --- |
| `fetch_url` | A summary focused on the stated purpose, the page's sections, and a `page_id` |
| `ask_page` | An answer to one question, with verbatim quotes checked against the page |
| `read_page` | The readable text, by section or in windows |

Pages are fetched over plain HTTP and converted to markdown; raw HTML never reaches the model.
Pages that only render with JavaScript are reported, not guessed at. The fetcher sends an honest
user agent, follows robots.txt, paces requests per host, and refuses to connect to loopback,
private, or link-local addresses. Fetched pages are cached for a day under `.agent/web`, and
everything from a page is marked as untrusted content.

### Working on code

Each repository in `CODE_REPOS` gets a coding profile: the same agent and memory machinery, with
its file tools rooted in that repository. A web chat conversation picks its repository when it
starts and keeps it. On top of the file tools, a coding conversation has:

| Tool | Does |
| --- | --- |
| `edit_file` | Replaces exact text; fails when the text is missing or appears more than once. Keeps line endings and the byte order mark |
| `search_files` | Regular expression search over file contents, skipping build output, dependencies, and `.git` |
| `run_command` | Runs one allowed program with its arguments, without a shell, with a timeout. Variables whose names contain KEY, TOKEN, SECRET, or PASSWORD are not passed on |

`read_file` reads long files in ranges of lines. A coding turn may take 40 rounds of tool calls
instead of 10, and ends in a reply when they run out. Each repository's index and project memory
live under `.agent/repos/<name>` in the agent's workspace, so the repository stays clean. The
host refuses a repository it is running from; point `CODE_REPOS` at a git worktree of it instead.

The allowlist guards against mistakes, not against a determined caller: git hooks, MSBuild
targets, and npm scripts can run anything, so give the agent repositories you would let it change.

## Subagents

Subagents run concurrently. Each is an `AgentLoop` with its own context, its own memory, and a
mailbox. A child can ask its parent a question while the parent keeps working, and stop requests
propagate down the tree. Children share their parent's workspace and tools but not memory.

## Surfaces

Each `(surface, conversation)` pair maps to its own agent session, so many chats run side by
side. The console, the web chat, Telegram, and Discord are all surfaces. The web chat serves a
minimal page of its own; the client in [web/](web/README.md) is the fuller one.

People rarely write one message per thought, so the agent answers bursts, not messages. It waits
until the person has probably finished: a wait measured from that person's own rhythm, shortened
after a complete question, lengthened after a message that trails off, and held open while the
surface reports them typing. The design is in the
[participation](docs/design/proposals/participation.md) proposal. The console answers each line
at once.

| Surface | Reports the person typing | Shows the agent typing |
| --- | --- | --- |
| Web chat | Yes | Yes |
| Discord | Yes | Yes |
| Telegram | No, the bot API does not deliver it | Yes |
| Console | No | No |

## Status

This is a working research harness, not a product. Memory, attention, and consolidation are
implemented and were checked with out-of-repo test harnesses; the repository does not yet have
a test project. Several of the central ideas are designed but not built. The
[roadmap](docs/design/roadmap.md) says which.

## License

Code is licensed under Apache-2.0 (see [LICENSE](LICENSE) and [NOTICE](NOTICE)). The documents
in `docs/design` are licensed under CC BY 4.0 (see [docs/design/LICENSE.md](docs/design/LICENSE.md)).
The Lucent styles in `web/src/vendor/lucent` are a copy from Design Foundry, also under Apache-2.0,
with their own license and notice.
