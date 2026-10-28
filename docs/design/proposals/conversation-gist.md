# Proposal: conversation gists made during sleep

Status: Proposed
Author: Diego Cancho
Axes: 2 (memory store), 7 (learning), with consequences for 5 (attention)

## Problem

Memory keeps a conversation in one of two forms: the literal episodes or a summary. Neither keeps
what a person remembers about a conversation they took part in.

- **Episodes are literal.** `MemoryManager.ArchiveAsync` stores each message as it was said. They
  are faithful but long, and recall matches them by surface: an episode is found when the new
  message resembles its wording, not when it resembles what was going on. After 14 days without
  measured use they go Cold.
- **The sleep summary is flat.** Distillation in `SleepJob` takes up to 60 episodes, clips each to
  300 characters, mixes conversations, and asks for "ONE dense summary that can stand in for ALL
  these episodes". The result says what was discussed. It drops who said it, in what order, what
  was proposed and rejected, where the conversation turned, and what was left open.

Facts are a third form, and they do something else: they keep the lesson and drop the
conversation.

What is missing is the account a participant would give a colleague who asks "what happened in
that conversation?". It keeps the order of events and who moved the discussion, and it carries the
reading that makes the outcome make sense: "Ana kept insisting; I think she was defending her
idea more than the evidence". That is not a transcript and not a summary. The exact words stay in
the transcript, for a specific search later.

Psychology has a name for the difference. Fuzzy-trace theory separates verbatim traces (the exact
surface) from gist traces (the meaning and its structure), and finds that people store both, in
parallel, and that gist lasts longer and guides later reasoning more than verbatim does. The
current design keeps only verbatim (episodes) and a lossy abstraction (the summary).

## Proposal

### 1. A gist is a retelling, in order

A gist covers one conversation, or one contiguous stretch of it. It is written as a short
narrative that follows the conversation's order, the way someone who was there would retell it:

```
[2026-10-28, #payroll, Ana, Bruno, Carla, agent; 38 messages]
Ana proposed moving the HR export to a nightly job, because the manual run had been late
three times in October. Bruno said they had tried a scheduled export last year and dropped it:
it collided with the payroll lock and produced half-empty files. Ana kept pressing for it.
(Reading: she seemed to be defending the idea more than answering the lock problem; she did not
address it until the agent raised it again.) The agent pointed out the lock is held until 06:00.
Carla joined late and asked what the discussion was about; Ana and Bruno caught her up. They
agreed to run the export at 06:15 and email on failure.
  -> decided: nightly export at 06:15, failure email.
  -> open: who receives the failure email.
```

Rules for writing it:

- **Linear.** Events in the order they happened. Order is what lets a later reader see that a
  decision came after an objection, or that someone changed their mind.
- **Who moved it, roughly what they said.** Speakers by name, using the identities from the
  [people](people.md) proposal when present. Positions and reasons, condensed, not quoted. Quotes
  are kept only for exact strings that matter: names, numbers, commands, error messages.
- **Readings are allowed, and marked.** An interpretation that helps read the situation ("she
  seemed to be defending the idea", "the question was not really answered", "the agreement
  sounded reluctant") goes in, in parentheses, labeled as a reading, hedged, and tied to what was
  observed. A reading without its observation is not allowed.
- **Outcomes are marked**, on their own lines at the end of the part that produced them:
  `decided`, `open`, `corrected`, `reversed`, `rejected`.
- **Length follows the conversation, not a quota.** A 40-message discussion is a paragraph or
  two; what does not change the story is left out.
- **Every sentence cites its episodes.** The gist stores the episode ids each sentence condenses,
  so any part can be expanded back to what was said.

### Readings and the audience

The agent is built for shared spaces, not only for one-to-one chats. It always knows where it is
and who is there, and everything it brings into a reply is chosen for that audience. Readings in a
gist need no rules of their own beyond that: like any memory, a reading reaches a conversation only
when it is appropriate for the people in it. The same filter by who is present that the
[projects](projects.md) and [entity access](entity-access.md) proposals use applies here.

One limit is specific to readings: they never leave the gist. Distillation may not turn a reading
into a fact, a person fact, or evidence for one. A pattern about a person still needs three
independent episodes of observed behavior, as the consolidation policy says.

### 2. Made only during sleep

The gist is written when a conversation is quiet, never during it. Two reasons:

- **A gist needs the whole arc.** Whether a turn mattered depends on what came after: an objection
  that was later withdrawn, a question answered three turns later, a decision reversed at the end.
  Written mid-conversation, the gist would record positions that the conversation went on to change.
- **The live conversation already has what it needs.** While a conversation runs, the window holds
  the turns themselves, and the cues live in how the thread changes: the next message is the
  reaction. Condensing them in real time would spend a model call per turn to produce something
  the window already shows better.

The trigger is the one `SessionLearner` already uses (the conversation has been idle long enough
and has unlearned messages), plus the nightly sleep pass for anything that slipped through.
Compaction keeps archiving episodes as today; it does not write gists.

### 3. Where gists sit in memory

A gist is a third memory kind beside `Fact` and `Episodic`, in the session scope of the
conversation it covers, or in the project scope when the [projects](projects.md) proposal is built.

It is the summary rung of the [fidelity ladder](fidelity-ladder.md) for conversations:

| Rung | Conversation form |
| --- | --- |
| Summary | The gist |
| Query | A question asked against the gist and its cited episodes |
| Source | The episodes |

Consequences:

- **Gists are recalled; episodes stand behind them.** Recall ranks gists like any memory. When a
  gist is injected, its episodes are not, which saves most of the budget a conversation costs
  today.
- **Episodes can go Cold sooner** once a gist covers them, because the gist keeps them findable: a
  recalled gist sentence leads to its episodes through `restore_memory`, the same path Cold stubs
  already use.
- **The flat sleep summary goes away.** Distillation stops writing one summary across mixed
  conversations. Its job becomes extracting facts only.
- **Distillation reads gists to find patterns and episodes to prove them.** Three corrections of the
  same thing in three conversations are easy to see across gists and hard to see across 60
  clipped messages. The evidence a fact cites stays episodes, so the 3-independent-episodes rule
  in the policy keeps its meaning.

### 4. Gists are applied automatically; facts stay staged

A gist adds no knowledge the episodes did not have. It is a lossy view of them, and it is
reversible: deleting a gist loses nothing. So, unlike facts, it is written without review and
logged in the ledger as `gist` with the episode ids it covers.

This holds only while a gist retells. A sentence that generalizes beyond the conversation ("Ana
prefers X", "Bruno always objects") is a fact in disguise, and an unmarked reading is a judgment in
disguise. The policy says so, and a gist that fails the check is dropped and logged, not stored.

### 5. Measured like any memory

Gists go through the utility loop. Whether they help is the question this proposal has to answer,
and it can be measured: for recalled conversations, compare turns where a gist was injected
against turns where the episodes were, with the shadow pairs from the
[memory counterfactuals](memory-counterfactuals.md) proposal. A gist that is injected often and
never applied decays to Cold like any other memory.

## Why it matters

- A conversation becomes recallable by what happened in it, not only by its wording.
- Open questions and decisions survive the end of a conversation as marked lines, so the next
  conversation can pick them up.
- Distillation sees patterns across conversations instead of reading clipped messages.
- A conversation costs less recall budget, with the literal text one step away.

## Alternatives considered

- **Keep the flat summary and make it longer.** It still loses turn order and speakers, which is
  what makes a decision traceable to who took it.
- **Write gists in real time, at compaction.** Compaction sees only the turns it evicts, not the
  arc, and would record positions later abandoned. It would also make the live turn slower.
- **Facts only.** Facts are the lesson without the conversation. Asking "what did we decide last
  week and why" needs the conversation.
- **Literal episodes only, with better retrieval.** Better matching does not shorten a 40-turn
  conversation, and verbatim is the form that people themselves do not keep.

## Open questions

- How long a stretch one gist covers. A whole conversation is natural for a chat; a channel that
  never ends needs a cut, probably at long silences or topic changes.
- Whether a gist is rewritten when a conversation resumes days later, or a second gist is added.
  Rewriting risks the re-summarization drift the design avoids elsewhere; adding is safer.
- Which outcome labels are enough. The five above are a starting set.
- Whether the people a reading is about can see it. The people proposal lets a person list what
  is kept about them; readings in gists probably belong in that listing.
- Whether tool calls get a sentence. `SessionLearner` leaves tool output out; a gist may want "ran the
  tests: 3 failures" as an act.

## Changes this would require

- A `Gist` memory kind, with the cited episode ids stored per sentence.
- A gist writer called from `SessionLearner` and from `SleepJob`, with its own prompt section in
  `docs/consolidation-policy.md`.
- Removal of the flat summary from distillation, and distillation reading gists as a guide.
- A shorter TTL for episodes covered by a gist.
- `restore_memory` expanding a gist sentence into its episodes.
- A `gist` ledger entry.

## References

- Reyna, V. F., Brainerd, C. J. (1995). Fuzzy-trace theory: An interim synthesis. Learning and
  Individual Differences, 7(1), 1-75. https://doi.org/10.1016/1041-6080(95)90031-4
  Verbatim and gist traces are stored in parallel; gist is more durable and drives reasoning.
