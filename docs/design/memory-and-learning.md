# Memory and learning

## Scopes

Memories are partitioned by who they are about:

| Scope | About |
| --- | --- |
| Personal | The user and how they work; spans projects |
| Project | This workspace: architecture, decisions, constraints |
| Session | The current run, including the episodic archive of evicted turns |

Each scope is stored separately. Subagents have their own memory rather than sharing the
parent's, so a child's scratch work does not pollute the parent's recall.

## Writing

Memories enter three ways: the agent saves a fact with the `remember` tool, evicted turns are
archived as episodes during compaction, and the compaction-time consolidator extracts durable
facts from turns about to be summarized away. Each write is stamped with ambient metadata (agent,
surface, conversation) so later analysis can ask where a memory came from.

## Retrieval

Recall is a pipeline, not a single similarity search:

1. **Rewrite.** An LLM rewrites the latest message into a retrieval query. If it fails, the
   original text is used.
2. **Hybrid candidates.** A lexical leg (full-text search with BM25 ranking) and a semantic leg
   (cosine over embeddings) each return candidates, fused with reciprocal rank fusion. The
   reported score stays the cosine, so a similarity threshold keeps a meaningful scale.
3. **Rerank.** An LLM picks which candidates actually help. The reranker distinguishes "the call
   failed" (fall back to first-stage order) from "nothing helps" (inject nothing).
4. **Budget.** The result is cut to a character budget so recall cannot crowd out the
   conversation.

Lexical and semantic retrieval fail differently. Names, identifiers, and error strings favour
lexical matching; paraphrase favours semantic matching. Fusing ranks avoids tuning a weight
between two score scales.

## Strata

Each memory has a residency:

| Residency | Meaning |
| --- | --- |
| Resident | Always in the stable system prompt |
| Active | Eligible for recall |
| Cold | Out of full recall; a strong match leaves a stub that can be restored |

Forgetting is always demotion to Cold, never deletion.

Cold memories still compete in the first recall pass. One that ranks among the top matches and
clears a minimum similarity is not injected; a stub is, with its id and a short preview, at most
two per turn. The agent reads it in full with `restore_memory`, which moves it back to Active,
records the reason in the ledger, and starts its utility record over, so the sleep job and the
metacognitive gate judge it on what happens after it came back. `search_memory` with
`archived=true` finds Cold memories on purpose.

## The utility loop

The instrument that makes learning possible. Every time a memory is injected into a prompt, the
injection is recorded. After the turn, a judge decides which injected memories the response
actually applied. Both are appended to an append-only log, from which per-memory statistics are
derived: how many times injected, and what fraction of those were useful.

This separates two things similarity cannot: a memory that looks relevant, and a memory that
helps. A memory that is injected often and never applied is a known failure mode, and the log
is what makes it visible.

## The sleep job

A background pass that runs between conversations. It has two kinds of action with different
authority:

- **Mechanical moves** are applied automatically and logged: episodes with no measured utility
  age out to Cold, facts that are injected repeatedly and never useful go to Cold, memories with
  a strong useful rate are promoted to Resident, and residents whose rate decays are demoted.
  The thresholds are placeholders to be tuned from real ledger data.
- **Distillation** is proposed, not applied. When enough episodes accumulate, an LLM guided by a
  written policy proposes durable facts, each citing the episodes that support it. Proposals are
  staged and wait for a human to apply or discard them.

Every transition, applied or discarded, goes into a hash-chained, append-only ledger that can be
verified. The test is simple: if the ledger cannot explain why the agent knows something, the
system is wrong.

The policy that guides distillation is a document, [consolidation-policy.md](../consolidation-policy.md),
injected as the judge prompt. Changing what the agent is allowed to learn means editing a
versioned text file, and the history records who changed the criteria and when. It requires at
least three independent episodes for a pattern, treats user corrections as the strongest signal,
writes facts as situated rules rather than personality traits, and names what must never be
distilled.

## Working memory

Compaction evicts the oldest turns when the context exceeds its budget. Three details matter:

- The cut snaps forward to a user turn, so a tool call is never separated from its result and
  the latest user message is never evicted.
- Evicted turns are archived as episodes before being summarized, so nothing load-bearing exists
  only in a summary.
- A typed operational state (current goal, decisions, open items) is updated field by field at
  compaction and is never re-summarized. Re-summarizing a summary repeatedly loses detail; typed
  fields do not drift that way.

## Not built

Eviction driven by relevance rather than age is central to the thesis and designed but not
implemented. See the [roadmap](roadmap.md).
