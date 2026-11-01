# Proposal: a coding workload through a delegated coding agent

Status: Proposed
Author: Diego Cancho
Axes: 3 (environment and tools), 4 (planning and flow), 6 (identity and policy)

## Problem

The host can already give a conversation a coding profile: file tools bound to one repository and a
`run_command` tool restricted to an allowlist of programs (`CODE_REPOS`, `CODE_COMMANDS`). That
profile runs on the same machine and the same user as the host. It is the right shape for reading
code and making small edits, and the wrong shape for the work developers actually hand over:
implement this feature, make these tests pass, fix this failure across several files.

Two things are missing.

1. **A place to run code that is not the host.** The pilot leaves `CODE_REPOS` unset because
   `run_command` on the host would let a prompt injection reach everything the service user can
   reach. Coding needs a sandbox before it needs anything else.
2. **A worker that is good at implementing.** Our loop is built to converse, remember, and
   coordinate. Implementing means long chains of reads, edits, and test runs inside a large
   context. Coding agents such as `claude -p` or `codex exec` already do that well, and developers
   already use them by writing a short spec and reviewing the result.

The proposal is to treat such an agent as a tool the conversation agent calls, inside the sandbox,
the way a developer calls it from a terminal. The conversation agent writes the spec, the coding
agent implements, and the conversation agent checks the result before anyone is asked to look.

## Why the obvious approaches do not work

**Give the host agent `run_command` on the VM.** It is the cheapest to build and the least safe.
The repository is untrusted input, web results reach the same context, and the service user owns the
conversation database and the credentials.

**Pass the person's message to the coding agent as it is.** A general prompt gives the coding agent
no way to know when it is finished, and gives the conversation agent no way to know whether to
believe it. The result is a plausible summary of work that may not compile.

**Run the coding agent in our own loop.** It works, and it is what the coding profile already does. It
pays for our persona, memory recall, and routing on every step of work that needs none of them,
and its context grows with every tool result.

## Proposal

```
person (Discord) -> conversation agent (host) -> sandbox container -> pull request -> review
                                                  |  coding agent (claude -p)
                                                  |  clone on its own branch
                                                  |  test commands
```

### The sandbox

A disposable container per task, started by the host:

- the repository cloned onto a new branch, never the default branch;
- a network allowlist: the git host, the package registries the project uses, and the coding agent's
  API; nothing else;
- no volume from the host except the task's own working directory, so the workspace, memory
  databases, and people file are out of reach;
- a token for the git host that can push branches and open pull requests on that one repository, and
  nothing else;
- a wall-clock limit and a memory limit, after which the container is killed and its directory kept for
  inspection.

The host keeps running under systemd. Only code execution moves into the container.

### The coding agent as a tool

A new tool, for example `code_task`, with arguments: the repository, the task, the acceptance
commands, the files it may touch, and limits. It does not return the coding agent's prose as fact. It
returns a record the conversation agent can check:

- the exit status, the branch, and the commits made;
- `git diff --stat` and the list of files changed outside the allowed set;
- the output of the acceptance commands, run by the host after the coding agent finishes, not by
  the coding agent;
- the cost the coding agent reports.

The full logs are stored as tool results and read on demand, as any large result is.

Execution goes behind an interface, `ICodingAgentRunner`, so that `claude -p` is the first
implementation and `codex exec` can be a second without touching the tool. A runner builds the command
line, runs it in the sandbox, and parses the structured output into the record above.

For `claude -p`, the documented flags cover what is needed:

| Need | Flag |
| --- | --- |
| Structured result with cost and session id | `--output-format json` |
| A schema for the final summary | `--json-schema` |
| Spend ceiling | `--max-budget-usd` |
| What it may run without asking | `--allowedTools`, `--permission-mode` |
| Never wait for an answer nobody gives | `--permission-prompts none` |
| Ignore the repository's own hooks, MCP servers, and `CLAUDE.md` | `--bare` |
| Continue after a failed check | `--resume <session-id>` |

`--bare` matters for safety: without it, a `-p` run executes the hooks and connects the MCP servers
declared in the repository's `.claude/settings.json` even in a folder nobody has trusted. The
repository is the thing being worked on and must not configure the worker. Project conventions the
worker needs go in the spec or in `--append-system-prompt`.

### The loop

1. The conversation agent turns the request into a spec: the goal, the files in scope, and acceptance
   commands that fail now and should pass after (a test, a build, a linter). If it cannot write a
   command that tells done from not done, it asks the person instead of delegating.
2. `code_task` runs the coding agent in a fresh sandbox.
3. The host runs the acceptance commands and inspects the diff.
4. If a check fails, one retry: the same session is resumed with the failing output. A second
   failure ends the task and reports what was tried.
5. If the checks pass and nothing outside scope changed, the branch is pushed and a pull request
   opened. The person reviews it. Nothing merges and nothing is deployed from this path.

Verification, not the prompt, is what makes a delegated implementation trustworthy. That is also why
the loop is useful as a recursion: the conversation agent plays the developer who reviews.

### Governance

- Every task is a row in the usage ledger under its own purpose, with the cost the coding agent
  reports, so `admin usage` shows it next to the conversation's spending.
- A per-task cost ceiling (`--max-budget-usd`) and a per-turn ceiling in tool points for `code_task`,
  which weighs more than any search.
- The tool is offered only in conversations whose people the registry marks as allowed to start
  coding work, and only for repositories named in the configuration.
- A refused or failed task is reported in the conversation with its reason, never silently retried.

### Authentication and billing

The runner takes its credentials from the environment so that the choice is configuration:

1. **An API key** for the coding agent, billed at API rates. Predictable, governable, and allowed for
   automation.
2. **A cloud provider the project already pays for.** The documentation lists Microsoft Foundry among the
   providers Claude Code reads credentials for; this depends on having the models deployed there.
3. **A personal subscription login.** The terms Anthropic publishes for Claude Code
   ([legal and compliance](https://code.claude.com/docs/en/legal-and-compliance), read 2026-10-31)
   say that plan limits "assume ordinary, individual usage of Claude Code and the Agent SDK", that
   OAuth login is meant for "ordinary use of Claude Code and other native Anthropic applications",
   and that developers building products or services, including with the Agent SDK, should use API
   keys or a supported cloud provider. They do not permit routing requests "through Free, Pro, Max, or
   Ultra plan credentials on behalf of their users", or collecting, storing, or intermediating
   subscription credentials. They do not stop a person from signing in to the unmodified binary with
   their own subscription. The maintainer reports that `claude -p` is still included in the plan; the
   page does not say so. The page does not use the word interactive. Third-party accounts describe a
   2026 enforcement against subscription tokens in third-party tools and disagree on the details.

   What follows for this design: the login is acceptable only for the account holder's own tasks,
   through the unmodified binary signed in by its own flow inside the sandbox, with the credential
   never read, stored, or passed on by our code. Tasks started by anyone else, or on behalf of anyone
   else, use an API key or a cloud provider, which the terms explicitly allow for "the customer's own
   authorized users" when the usage is billed to the key owner. The runner keeps authentication in
   configuration so that a change in the terms costs a setting, not code.

## Decisions for the pilot

Taken 2026-10-31.

- **Credentials:** the account holder's subscription login, for the account holder's own tasks only.
  Nobody else can start a coding task until an API key or a cloud provider is configured.
- **Sandbox:** rootless Docker on the VM.
- **First repository:** CloudAgent itself, a .NET solution with a light image and fast tests, to prove
  the loop before paying for the heavy toolchain of a repository that mixes C++ with Emscripten,
  TypeScript, Angular, and .NET.

## Pilot definition

**Who and where.** A coding task starts only in a direct conversation, and only for the people listed
in `CODE_PEOPLE` (a list of person ids, `diego` for the pilot). A group never offers the tool, because
a group has several readers and several senders and the login is one person's.

**Isolation.** Rootless Docker runs under its own unprivileged user, not the service user. The host
starts a task through one wrapper that sudo allows for that single command, takes validated
arguments (task id, repository name, image), and refuses anything else. A container escape then
lands in a user that owns nothing the host owns.

**Image.** The .NET 10 SDK image plus the Claude Code binary, installed at build time and pinned to a
version, so a run does not depend on a download. One image per repository, named in the repository
configuration next to its path.

**Git without credentials in the sandbox.** The host clones the repository into the task directory
with its own read token, removes the credentials from the clone's configuration, and mounts only that
directory. The coding agent can commit but cannot push. When the task ends, the host fetches the branch
from the task directory into its own clone and pushes it with a token that can push branches and open
pull requests on that repository only.

**Network.** The container joins a Docker network with no route out except an allowlisting proxy:
the git host, the NuGet feeds, and the coding agent's API and sign-in endpoints. Telemetry and update
checks are switched off. Acceptance commands run in a second container with no network and no
credentials, so repository test code never runs next to the login.

**The login.** The first sign-in is done once by hand inside the sandbox image, with the unmodified
binary and its own flow. The credential lives in a volume that only the coding agent's container
mounts. Our code never reads it. The risk that remains is that the coding agent's own shell can read
it, so a prompt injection could try to send it to an allowed host. The blast radius is one revocable
login, which is accepted for the pilot and removed by moving to an API key with a spend cap.

**Limits.** One task at a time on the pilot machine, 20 minutes of wall clock, 2 GB of memory, 1.5
CPUs, and `--max-budget-usd` per task. After a timeout the container is killed and its directory
kept.

**Task record.** What `code_task` returns to the conversation:

```json
{
  "status": "passed | failed | timed_out | out_of_scope",
  "branch": "task/2f9c1a",
  "commits": 2,
  "files_changed": ["src/..."],
  "files_outside_scope": [],
  "acceptance": [{ "command": "dotnet test ...", "exit": 0, "output_ref": "result id" }],
  "cost_usd": 0.42,
  "agent_summary": "what the coding agent says it did"
}
```

The agent's summary is shown as its claim. The status and the acceptance results are what the
host measured.

**First tasks.** Small changes where done is a command: add a test for an existing behavior, fix a
failing test, rename a type across the solution. The pilot is judged by how often a task passes
the acceptance commands on the first try and how often a reviewer would have merged the pull
request unchanged.

## Open questions

- Which acceptance commands can the conversation agent write reliably from a request, and what
  fraction of requests have none?
- One container per task, or one per conversation with the clone kept between tasks. Reuse saves
  clone and install time and carries state between tasks.
- Whether the coding agent's transcript should enter memory at all, or only the outcome the
  conversation agent saw.
- How to give the coding agent project knowledge that lives in our memory without handing it the
  memory database: a generated brief per task, or a read-only tool.
- Concurrency: how many sandboxes may run at once on the pilot machine (two vCPU, 4 GB).

## Order of work

1. The sandbox: the unprivileged user, rootless Docker, the wrapper, the image, and a container
   runner for the existing `run_command`, with the network allowlist and
   limits. Useful on its own and prerequisite to everything else.
2. `ICodingAgentRunner` with the `claude -p` implementation, the structured record, and the ledger
   purpose. Behind a configuration flag, off by default.
3. The spec and verification loop, the retry, and pull request creation.
4. A second runner (`codex exec`), if the first proves useful.
