# Proposal: the fidelity ladder

Status: Accepted
Author: Diego Cancho
Axes: 5 (attention), with consequences for 2 (memory store) and 3 (environment and tools)

## Problem

An agent that reads everything at full resolution spends its context on material it does not
need. A web page, a long file, or an old memory usually matters for one detail, and the rest is
noise that costs tokens, crowds out the conversation, and gives the model more surface to be
misled by. An agent that reads only summaries has the opposite problem: it cannot check a detail,
and it has no way to know what the summary dropped.

People handle this by reading at different depths: skim, look up the specific thing, and read
closely only when the stakes justify it. An agent needs the same choice, and needs it to be
explicit.

## Proposal

Any source the agent perceives is available at three levels of fidelity, and the agent climbs
only as far as the task requires.

| Level | What the agent gets | Cost |
| --- | --- | --- |
| Summary | A short account of the source, focused on why the agent is reading it, plus a map of its parts | Low |
| Query | An answer to a specific question, produced from the source by a small model, with verbatim citations | Low |
| Source | The content itself, in parts | High |

Three rules make the ladder trustworthy:

- **The summary is focused on a purpose.** A generic summary loses exactly the detail the agent
  came for. The agent states what it is looking for, and the summary is written for that.
- **Queries cite.** An answer from the middle level carries verbatim quotes, and the quotes are
  checked mechanically against the source. A quote that does not appear in the source is flagged.
  When the source does not say something, the answer must say so instead of filling the gap.
- **The source stays reachable.** Every summary and every answer carries a handle to the full
  source. Nothing the agent sees at a lower level is the last copy.

## Where it applies

| Source | Summary | Query | Source |
| --- | --- | --- | --- |
| Web page | `fetch_url` | `ask_page` | `read_page` |
| Workspace file | `summarize_file` | Not built | `read_file` |
| Cold memory | A stub left in recall, or `search_memory` with `archived=true` | Not built | `restore_memory` |

Web pages are the first complete implementation. For memory, a stub is the summary level and
restoring is the source level. A stub carries the memory's id, scope, kind, and the first words of
its content; it appears when the Cold memory ranks among the top recall matches and clears a
minimum similarity. Restoring moves the memory back to Active, records the agent's reason in the
consolidation ledger, and resets its utility record, so it is not demoted again on its old
history.

## Risks

- **Summaries invent and omit.** Citation checks cover the query level. For the summary level the
  defense is that the source stays one call away, and that the summary is phrased as an account of
  the source, not as fact.
- **Injected instructions survive summarization.** Text in a source that addresses the reader can
  be repeated by the small model. Output at every level of the ladder is untrusted content and is
  marked as such when it enters the context.
- **The small model is a second-hand view.** Its errors look like the source's. Citations make the
  difference checkable; without them the middle level would not be worth having.

## Alternatives considered

- **Always read the full source.** Simple and faithful, and too expensive for anything long.
- **Always summarize.** Cheap, and it makes the agent dependent on what the summarizer chose to
  keep.
- **Retrieval by embedding over chunks.** Useful for finding where something is, and already
  available for the workspace through the semantic index. It answers "where", not "what does it
  say", so it complements the query level rather than replacing it.

## Open questions

- Should the agent's choice of level be measured by the utility loop, so it learns when skimming
  is enough?
- A stub previews the start of the content, so a short fact can be read whole from the stub and
  used without a restore, which the ledger then cannot count. Would a generated label serve
  better than a preview?
- Should the query level be added for workspace files, or is the semantic index enough there?
