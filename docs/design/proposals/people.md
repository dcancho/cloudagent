# Proposal: knowing the people in a conversation

Status: Proposed
Author: Diego Cancho
Axes: 7 (learning), 6 (identity and policy), 2 (memory store)

## Problem

In a shared conversation the agent cannot tell who said what. The router receives a sender id
and display name with every inbound message and then passes only the text to the agent. Even
with that fixed, the agent would meet the same people repeatedly and learn nothing about them:
who owns which decisions, who prefers short answers, who to ask about what. A colleague would
learn this. An agent that is meant to behave like one should too.

## Proposal

### 1. Carry identity through

Every message keeps its author from the surface to the context. Messages in the context are
rendered with the author's display name. Internally each author maps to a stable person id derived
from `(surface, senderId)`. The same human on two surfaces is two person ids until someone links
them explicitly; automatic merging is a source of wrong impressions and is left out.

### 2. A person scope

Memory gains a fourth scope, keyed by person id, beside personal, project, and session. Episodes
that come from a shared conversation are tagged with the person ids involved. Nothing else about
the memory machinery changes: the same store, retrieval, utility tracking, and sleep job apply.

### 3. Distillation of impressions

The sleep job already distills facts from episodes when a pattern appears in at least three
independent ones, with cited evidence. Person facts use the same rule and the same staging, with
two additions:

- **Person facts are never applied automatically.** They wait in staging until a human accepts
  them. Mechanical moves that reduce exposure (demotion to Cold) stay automatic. Moves that
  increase it (promotion to Resident, first injection into a prompt) require acceptance.
- **Only accepted facts are ever injected.** A candidate impression never reaches the model.

### 4. What may be recorded

The consolidation policy currently forbids judgments about people. This proposal narrows that
rule instead of removing it.

| Allowed | Not allowed |
| --- | --- |
| Observable, situated behavior: "when a request is long, Marta replies faster to a short summary" | Character judgments: "Luis is careless" |
| Working preferences and roles: "Luis decides about deployments" | Emotional state, health, private life |
| Facts with cited evidence from independent episodes | Anything inferred from fewer than three episodes |
| Corrections the person gave the agent about how to work with them | Anything the person asked the agent to forget |

Facts are written as rules in context, the same form the policy already asks for ("when X, do Y"),
never as traits ("X is Z").

### 5. Transparency and control

A person can ask the agent what it keeps about them, and can ask for it to be removed.

- Listing returns the accepted facts and the evidence behind them.
- Removal for the person scope is real deletion of the facts and of episodes tagged only with that
  person. This differs from the rest of the design, where forgetting is reversible demotion to
  Cold. For data about a person, reversibility is the wrong default.
- The consolidation ledger records that a deletion happened and which facts it covered by id, but
  not their content, so the chain stays verifiable without retaining what was deleted.

### 6. Use

When the current exchange involves known people, their accepted facts are included in the
ephemeral context under a small budget. The utility loop measures whether using them helped, so
an impression that keeps failing to apply loses weight through the same mechanism as any other
memory.

## Testing

Use scripted and synthetic groups only. Real conversations among real people, particularly
colleagues, are data that this project must not ingest.

## Alternatives considered

- **No person model, only attribution.** Simplest, and enough to follow a conversation. It gives
  up the learning that motivates the idea.
- **A free-form profile per person, rewritten over time.** Rejected: it drifts, it cannot be
  audited line by line, and rewriting a summary repeatedly loses detail.
- **Automatic application above a confidence threshold.** Rejected for data about people. The cost
  of a wrong impression falls on someone who did not agree to be profiled.
- **Personality traits.** Rejected: they are judgments, they are unfalsifiable, and they invite
  the model to generalize from one bad day.

## Open questions

- Should the agent tell a group that it keeps notes about participants, and in what form?
- Who reviews staged person facts when the agent is shared by several people?
- How long are person episodes and facts retained without being used?
- Is there a safe way to link one human across surfaces?
- How should the agent behave when someone asks about another person's notes?

## Changes this would require

- The sender flows from `InboundMessage` through the router and conversation into the context.
- A person scope in the memory store and its metadata stamping.
- A rule in the sleep job that keeps person facts in staging and out of automatic promotion.
- A revision of `docs/consolidation-policy.md` implementing the table above.
- Listing and deletion tools for the person scope, with ledger entries for deletion.
