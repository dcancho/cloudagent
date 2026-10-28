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

What is missing is the shape of the exchange. "Marta asked for a nightly export; the agent pointed out the lock;
she moved the start to 06:15; the failure email's recipient was left open." That is not a transcript and not a summary. It is a turn-by-turn account
of roughly what each side said, with the cues that mattered: a correction, a hesitation, a
reversal, a question nobody answered.

Psychology has a name for the difference. Fuzzy-trace theory separates verbatim traces (the exact
surface) from gist traces (the meaning and its structure), and finds that people store both, in
parallel, and that gist lasts longer and guides later reasoning more than verbatim does. The
current design keeps only verbatim (episodes) and a lossy abstraction (the summary).

## Proposal

### 1. A gist is a condensed transcript, one line per turn

A gist covers one conversation, or one contiguous stretch of it. It keeps the turn structure and
the speakers and condenses each turn to what it did:

```
[2026-10-28, web, 14 turns]
Marta: wants the HR export moved to a scheduled job; asks for an estimate.
Agent: proposes a nightly job; asks whether the file must arrive before 08:00.
Marta: yes, before 07:30. Also wants a failure email.
Agent: points out the export reads the payroll lock, which is held until 06:00.
Marta: accepts 06:15 start.
  -> decided: export runs at 06:15, emails on failure.
Agent: asks who receives the failure email. Unanswered.
  -> open: recipient of the failure email.
```

Rules for writing it:

- **One line per turn**, merged only when consecutive turns by the same speaker do one thing.
- **Speakers by name**, using the identities from the [people](people.md) proposal when present.
- **Acts, not words.** "Points out the payroll lock", not the paragraph that explained it.
  Quotes are kept only for exact strings that matter: names, numbers, commands, error messages.
- **Cues are marked**, on their own line under the turn that produced them: `decided`, `open`,
  `corrected`, `reversed`, `rejected`. These are the points facts and future conversations need.
- **Observable cues only.** "Asked the same thing three times" is a cue. "Was frustrated" is an
  emotional state, which the consolidation policy and the people proposal already forbid.
- **Every line cites its episodes.** The gist stores the episode ids each line condenses, so any
  line can be expanded back to what was said.

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
  recalled gist line leads to its episodes through `restore_memory`, the same path Cold stubs
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

This holds only while gists stay acts and cues. A gist line that makes a claim the conversation
did not ("Marta prefers X") is a fact in disguise. The policy says so, and a gist that fails the
check is dropped and logged, not stored.

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
- Which cue labels are enough. The five above are a starting set.
- Whether tool calls get a line. `SessionLearner` leaves tool output out; a gist may want "ran the
  tests: 3 failures" as an act.

## Changes this would require

- A `Gist` memory kind, with the cited episode ids stored per line.
- A gist writer called from `SessionLearner` and from `SleepJob`, with its own prompt section in
  `docs/consolidation-policy.md`.
- Removal of the flat summary from distillation, and distillation reading gists as a guide.
- A shorter TTL for episodes covered by a gist.
- `restore_memory` expanding a gist line into its episodes.
- A `gist` ledger entry.

## References

- Reyna, V. F., Brainerd, C. J. (1995). Fuzzy-trace theory: An interim synthesis. Learning and
  Individual Differences, 7(1), 1-75. https://doi.org/10.1016/1041-6080(95)90031-4
  Verbatim and gist traces are stored in parallel; gist is more durable and drives reasoning.
