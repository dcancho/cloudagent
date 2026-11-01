# CloudAgent

[![CI](https://github.com/dcancho/cloudagent/actions/workflows/ci.yml/badge.svg)](https://github.com/dcancho/cloudagent/actions/workflows/ci.yml)

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
tests/
  CloudAgent.Core.Tests/  deterministic tests of the measurement paths, with a scripted model
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

To run it on a server, see [deploying to a VPS](docs/deploy.md).

### Configuration

| Variable | Purpose | Default |
| --- | --- | --- |
| `AGENT_WORKSPACE` | Directory the agent may act within | `./workspace` |
| `AGENT_MEMORY_BUDGET_TOKENS` | Working-memory budget before compaction | 8000 |
| `SLEEP_INTERVAL_MIN` | Minutes between consolidation passes, 0 or less disables | 30 |
| `PERSONAL_MEMORY_DIR` | Where each person's personal memory database lives, shared across workspaces | `~/.cloudagent/memory/people` |
| `PEOPLE_FILE` | Who may talk to the agent, and which surface accounts belong to whom (see below) | `~/.cloudagent/people.json` |
| `CONSOLIDATION_POLICY` | Path to the consolidation policy document | `./docs/consolidation-policy.md` |
| `WEBCHAT`, `WEBCHAT_PORT` | Enable the web chat surface and set its port | off, 5080 |
| `WEBCHAT_ANONYMOUS` | `1` lets web chat connections without Tailscale identity in, all as the same nobody sharing their conversations. Only for a web chat no one else can reach; `run.ps1` sets it | off |
| `CLI` | `0` leaves the console surface out, for a host with no terminal such as a systemd service | on |
| `TELEGRAM_BOT_TOKEN`, `DISCORD_BOT_TOKEN` | Enable those surfaces | off |
| `TAVILY_API_KEY` | Real web search | no-op search |
| `TURN_TAKING` | `0` answers every message on its own instead of waiting for the end of a burst | on |
| `GROUP_ENGAGEMENT_SEC` | Seconds a person the agent is talking with in a group can keep going without mentioning it again; `0` needs a mention or reply every time | 90 |
| `SHUTDOWN_DRAIN_SEC` | Seconds a stopping host (SIGTERM or Ctrl+C) lets running turns finish before cancelling them; new messages are only written down meanwhile. `0` cancels at once. Keep the service's `TimeoutStopSec` above it | 25 |
| `MAX_INPUT_TOKENS` | Most input tokens (estimated, about 4 characters each) one model call may carry; past it stubs, recalled memories, the summary and old tool results are dropped in that order. `0` for no cap | 32000 |
| `TOOL_POINTS` | Points the tool calls of one turn may spend before the agent must answer: a calculation costs 1, a web search or page fetch 3, most others 1 to 2. `0` for no limit beyond the 16 steps | 30 |
| `TOOL_RESULT_INLINE_CHARS` | Characters of a tool result that enter the conversation (about 4 per token); a longer result is stored whole and the agent reads the rest with `read_result`. `0` keeps results inline | 3000 |
| `AGENT_NAME` | What the agent calls itself in every conversation | its handle on the surface, else `CloudAgent` |
| `CONTEXT_ROUTING` | Who chooses per turn how much memory to recall and which tool categories to offer (see [context routing](docs/design/proposals/context-routing.md)): `decision` for the Foundry decision model, `generative` for the background chat model, `off` for the full pipeline every turn. Decisions go to `.agent/routing.jsonl` | `decision` when the two variables below are set, else `off` |
| `LEARN_IDLE_MIN` | Minutes a conversation must be quiet before the sleep timer learns from it: what was said since the last pass (not tool output) is archived as episodes, and its durable facts are staged for review | 2 |
| `TOKEN_BUDGET` | Tokens, input and output together, the workspace's agents may spend. Once spent, a turn in progress ends in a reply without more tools, later messages get a fixed reply without calling the model, and consolidation stops. A 5% reserve allows the closing replies; past it every call is refused. Spending is kept in `.agent/usage.json` across restarts | no limit |
| `MODEL_PRICES` | What each model charges, as `model=input/output` or `model=input/output/cachedInput` dollars per million tokens separated by `;`, for example `gpt-6-luna=0.10/0.50/0.01`. Only `admin usage` reads it; a model without a price is counted in tokens and flagged | none |
| `CODE_REPOS` | Repositories a coding agent may work on, as `name=path` pairs separated by `;` | none |
| `CODE_COMMANDS` | Programs a coding agent may run in them, comma separated | `dotnet,git,npm,node` |
| `SANDBOX_RUNNER` | Path of the wrapper script (`cloudagent-sandbox-run`, see [deploy](docs/deploy.md)) that runs a conversation's coding commands in one rootless Docker session container instead of on the host. When set, every repository in `CODE_REPOS` needs `SANDBOX_IMAGE`; without it the repository is skipped | unset, commands run on the host |
| `SANDBOX_IMAGE` | The image sandboxed commands run in; it must be listed in `/etc/cloudagent/sandbox-images` | none |
| `SANDBOX_TIMEOUT_SEC` | Longest one sandboxed command may run, enforced inside the container; at most 1200 | 1200 |
| `SANDBOX_MEMORY` | Memory limit of the session container, such as `2g` or `512m` | `2g` |
| `SANDBOX_CPUS` | CPU limit of the session container | `1.5` |
| `SANDBOX_TMP_SIZE` | Size of the container's `/tmp`, which holds the package cache and build temp for the session's life | `1g` |
| `SANDBOX_MAX_SESSIONS` | Sandbox session containers that may run at once; a conversation that needs one while all are taken is told the sandbox is in use | 1 |
| `FOUNDRY_DECISIONS_ENDPOINT`, `DECISIONS_MODEL` | The Foundry decision API endpoint (ending in `/providers/microsoft/v1/systemone`) and the deployment name, for example Microsoft-Decision-1. Uses `FOUNDRY_API_KEY`, or Entra ID without it | unset |

### People

The people file lists everyone the agent talks to, all on equal footing, with the accounts that
are theirs as `surface:senderId`. It is the allowlist for Telegram, Discord and an identified web
chat: a message from any other account is refused in a direct conversation and ignored in a group.
In a group, every message reaches the agent prefixed with who wrote it: the person's id, or the sender's display name when they are not in the file.
The file is edited by whoever operates the host; the agent never links accounts itself. Person ids
use lowercase letters, digits, `-` and `_`, since they name each person's memory file.

```json
{
  "diego": { "accounts": ["webchat:diego@example.com", "telegram:123456", "discord:789012"] },
  "jose": { "accounts": ["discord:345678"] }
}
```

Behind Tailscale Serve, the web chat speaks as the tailnet login. The console always gets in, since
whoever uses it already has the machine, and speaks as nobody unless started with `--as <person>`.
A web chat without Tailscale identity is refused unless `WEBCHAT_ANONYMOUS=1`. A refused web chat
cannot connect at all, and each web chat conversation belongs to whoever opened it: nobody else can
list, open or delete it. Each console identity keeps its own conversation.

Consolidation proposals stay with the conversation they were learned from: only that conversation
can list, apply or discard them. The workspace's `.agent` folder, where memory, transcripts and
proposals live, is out of reach of the agent's file tools.

Personal memory belongs to the person in a direct conversation, one database each in
`PERSONAL_MEMORY_DIR`. Group conversations, and anyone unidentified, keep no personal memory.

### Records

Everything the agent does is kept under the workspace's `.agent` folder, which is not in git:

| Path | Contents |
| --- | --- |
| `transcripts/<agent>.jsonl` | One line per event of each agent: messages, model replies with tool calls and token counts, tool results, routing, recall, judging, errors |
| `logs/host-<date>.log` | The host's console output |
| `routing.jsonl` | Each routing decision and how its turn ended (completed, failed, or cancelled), including the router's misses |
| `usage.jsonl` | One line per model call: purpose (turn, router, rewrite, rerank, judge, summary, snapshot, consolidation, sleep, webpage), model, agent, input, output and cache tokens, duration, and whether it failed. Embeddings are not counted |
| `conversations/<agent>.json` | Each conversation's working context, saved after every turn so it continues after a restart |
| `memory/` | The memory databases |

Deleting a web chat conversation ends its session and removes its transcript, saved context, and
session memory. What it added to personal and project memory stays, and so do the ledgers.

## Administration

Operating the host is done on its own machine, outside any conversation: whoever can run these
already has access to the machine, and the agent has no way to reach them.

```bash
CloudAgent.Host admin people list
CloudAgent.Host admin people add jose
CloudAgent.Host admin people link jose discord:485305305144754176
CloudAgent.Host admin people refused
CloudAgent.Host admin status
CloudAgent.Host admin usage --since 7d
CloudAgent.Host admin proposals
CloudAgent.Host admin proposals show <id>
CloudAgent.Host admin proposals approve <id>
```

`people` edits the people file, checks that the result loads, and keeps the previous file as
`.bak`; the running host reads the file at startup, so restart it after a change. `people refused`
lists the accounts the host turned away, read from its log, so an id can be linked without
searching for it. `usage` groups the model calls in `usage.jsonl` by purpose, model, and agent, with
cost from `MODEL_PRICES`. `--env-file <path>` reads a systemd-style environment file; without it
`/etc/cloudagent/cloudagent.env` is used when present.

`proposals` lists what the sleep pass staged for review and `show` prints its facts with their evidence. `approve` and
`discard` leave a decision file that the running host carries out within a minute, since it owns the memory and the
ledger; an approval waits until the conversation it came from is open.

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

Memories the sleep job set aside (Cold) are not lost. When one matches a turn strongly, recall
leaves a stub with its id instead of the content; `restore_memory` brings it back into recall
with a fresh utility record, and `search_memory` with `archived=true` looks for set-aside
memories on purpose.

### Working on code

Each repository in `CODE_REPOS` gets a coding profile: the same agent and memory machinery, with
its file tools rooted in that repository. A web chat conversation picks its repository when it
starts and keeps it. On top of the file tools, a coding conversation has:

| Tool | Does |
| --- | --- |
| `edit_file` | Replaces exact text; fails when the text is missing or appears more than once. Keeps line endings and the byte order mark |
| `search_files` | Regular expression search over file contents, skipping build output, dependencies, and `.git` |
| `run_command` | Runs one allowed program with its arguments, without a shell, with a timeout. Variables whose names contain KEY, TOKEN, SECRET, or PASSWORD are not passed on |

In the web client, the agent's tool calls show up as it works, and each edit can be expanded into
its diff. `read_file` reads long files in ranges of lines. A coding turn may take 40 rounds of tool calls
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
implemented. Several of the central ideas are designed but not built. The
[roadmap](docs/design/roadmap.md) says which.

The tests cover what the harness claims to measure: the utility ledger and its replay, the
judge and what a failed judgment records, the sleep job's moves, the hash-chained ledger and
tamper detection, the metacognitive gate, retrieval with its fallbacks, recovery from Cold, the
token budget, and the routing ledger. They use a scripted model and a real SQLite store, so they
need no keys and give the same result every run.

```powershell
dotnet test
```

## License

Code is licensed under Apache-2.0 (see [LICENSE](LICENSE) and [NOTICE](NOTICE)). The documents
in `docs/design` are licensed under CC BY 4.0 (see [docs/design/LICENSE.md](docs/design/LICENSE.md)).
The Lucent styles in `web/src/vendor/lucent` are a copy from Design Foundry, also under Apache-2.0,
with their own license and notice.
