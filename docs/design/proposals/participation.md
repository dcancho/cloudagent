# Proposal: deciding when to wake the agent in a group

Status: Proposed
Author: Diego Cancho
Axes: 5 (attention), 8 (metacognition)

## Problem

A turn-based agent answers every message. In a group that is wrong in two ways: it is expensive,
because every message costs a full model turn, and it is rude, because most messages are not
addressed to the agent and many are people talking to each other. A participant in a group
decides, message by message, whether to speak, wait, or stay out of it.

## Proposal

Three layers, ordered by cost. Each decides only whether the next one runs.

### Layer 0: deterministic rules

A direct mention, or a reply to one of the agent's own messages, wakes the agent. It does not go
through the classifier, because the worst failure is ignoring someone who is talking to the
agent. A short debounce still applies, to catch a burst the person is still typing.

### Layer 1: a small classifier

Everything else is evaluated by a small, cheap model. It evaluates a **burst**, not a message:
messages are grouped by a debounce window, and the classifier runs once per burst after it
closes.

Input:
- A window of recent messages with their authors.
- The roster of people the agent knows, and what is accepted about them (see
  [people](people.md)).
- The agent's own recent participation and current state.

Output, one of:

| Result | Meaning |
| --- | --- |
| Ignore | The burst stays in context; nothing runs |
| Wait | Re-evaluate after a delay unless someone else resolves it first |
| Wake without speaking | Run a turn whose result is not a message (see [thinking without speaking](thinking-without-speaking.md)) |
| Wake to respond | Run a turn that may publish a message, with or without mentioning someone |

The classifier needs the conversation context because relevance is a property of the exchange,
not of one message. The stable part of its prompt can be cached.

### Layer 2: the agent

The main agent runs only when layer 0 or 1 wakes it. Waking it does not oblige it to speak.

### Waiting

Wait is an action with a timer. An unanswered question can be left for a while and the agent can
speak only if nobody else did. This is the difference between "should not answer" and "should not
answer yet", and it is why debounce alone is not enough.

### Signals for the classifier

- Whether the agent is addressed, explicitly or by the shape of the message.
- Whether a question is directed at no one in particular and still open.
- Whether people are mid-exchange with each other.
- Whether the agent knows something relevant, judged by memories with a good utility record.
- Whether the agent spoke recently, so it does not dominate.
- The asymmetry of errors: interrupting wrongly costs more than staying silent wrongly.

### Relation to metacognition

Speaking without knowing is worse than staying silent. The classifier can use the same utility
record the confidence gate uses: if the agent's memories on a topic have kept failing to apply, it
should be more cautious about volunteering.

## Measuring it

Two numbers on scripted conversations: how often the agent speaks when it should not have, and how
often it fails to respond when addressed directly. The second must be near zero, which is why
layer 0 exists. Test with synthetic groups only.

## Alternatives considered

- **The main model decides everything.** Simple, and it pays a full turn per message.
- **Rules only.** Cheap and predictable, but it cannot tell an open question from a side comment.
- **Classify every message.** Costs a call per message and judges a thought before the person has
  finished it.

## Open questions

- How should the debounce window adapt to the pace of a group?
- Do groups need their own settings, such as "only speak when mentioned"?
- How are overlapping threads in one channel told apart?
- How is the classifier calibrated, and against what data, without using real conversations?
- Should the agent show typing, or any signal that it is considering a reply?

## Changes this would require

- A participation component between the router and the conversation, owning the debounce, the
  layer 0 rules, the classifier call, and the wait timers.
- Sender identity carried through, as in [people](people.md).
- Surface support for mentions and replies, which differs between adapters.
