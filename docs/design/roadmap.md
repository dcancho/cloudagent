# Roadmap

Status of each part of the design. "Built" means implemented and exercised with test
harnesses kept outside the repository. The repository has no test project yet.

## Built

- Provider seam with offline, Anthropic, and Foundry implementations.
- Tool calling with a bounded inner loop; file, web, index, memory, and taskboard tools.
- Web reading at three levels of detail (summary, query with checked quotes, source), the first
  implementation of the [fidelity ladder](proposals/fidelity-ladder.md).
- Semantic indexing of the workspace.
- Scoped memory in SQLite with hybrid retrieval, reranking, and a recall budget.
- Working-memory compaction with episodic archival, typed operational state, and structural
  truncation of large tool results.
- Utility loop: injection records, a post-turn judge, per-memory statistics.
- Sleep job with mechanical moves, staged distillation, and a verifiable hash-chained ledger.
- Paging from Cold: matching Cold memories leave stubs in recall, `restore_memory` brings one
  back to Active, and its utility record starts over. The memory row of the
  [fidelity ladder](proposals/fidelity-ladder.md).
- Attention module extracted from the loop, with a metacognitive confidence gate.
- Prompt cache awareness through the stable and ephemeral split.
- Concurrent subagents with mailboxes, ask and answer, and supervised stop.
- Surfaces: console, web chat, Telegram, Discord.
- Typed host configuration.

## Partial

- **Anti-recursion in summaries.** Typed state is not re-summarized and evicted turns are
  archived, but the rolling summary is still re-summarized at each compaction.
- **Consolidation trigger.** The compaction-time consolidator is called by the compactor. It
  should be a separate trigger.
- **Concurrency across conversations.** Conversations share some stores that assume
  single-threaded use. Memory is safe through SQLite's write-ahead logging; the index and
  taskboard are not yet isolated.

## Designed, not built

- **Relevance-based eviction.** Compaction evicts the oldest turns. The utility log already
  measures access; it does not yet influence what stays in the window.
- **Planning and flow as a seam.** Sequencing lives inside the loop.
- **Identity and behavior policy as a seam.** The persona is a string.
- **Speak or stay quiet.** A gate for deciding whether to respond in a shared conversation. See
  the [participation](proposals/participation.md) proposal.
- **Projects.** A named context that repositories, conversations on any surface, and people link
  to, with its own memory and resident instructions. Memory scopes become a ladder (personal,
  project, repository, session), so team knowledge reaches every repository and chat in the
  project instead of staying in one repository. See the [projects](proposals/projects.md) proposal.
- **Knowing the people in a conversation.** Sender identity and a person memory scope. See the
  [people](proposals/people.md) proposal.
- **Turns that do not end in a message.** See the
  [thinking without speaking](proposals/thinking-without-speaking.md) proposal. Suggested build
  order for these three: thinking without speaking, then participation, then people.
- **Context routing.** A decision model chooses per turn whether to recall memory and which tool
  definitions to send, failing open when unsure. See the
  [context routing](proposals/context-routing.md) proposal. It comes before streaming, because
  measured turns spend most of their time in sequential calls, not in generation.
- **Streaming.** Publishing a reply as it is generated, as a surface capability. Covered in the
  [thinking without speaking](proposals/thinking-without-speaking.md) proposal.
- **Measuring memory by running with and without it.** The usefulness judge only sees the final
  reply and cannot separate a memory from what the context already carried, so conventions followed
  silently look unused and get demoted. A decision model would answer observable questions about
  each turn's action history, and sampled shadow pairs, the same turn with a memory withheld, would
  give the verdict and calibrate the cheap reading. See the
  [memory counterfactuals](proposals/memory-counterfactuals.md) proposal.
- **Threshold tuning.** The sleep job's defaults are placeholders awaiting real ledger data.
- **Consolidation policy review.** The policy is a draft.

## Order

Remaining work on the axes, in order: the rest of attention (compaction and truncation moved
behind the attention module), learning decoupled from compaction, planning, identity.
See [eight axes](eight-axes.md).
