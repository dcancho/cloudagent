# Proposal: taking turns in a conversation

Status: Accepted for end-of-turn detection; Proposed for the group layers
Author: Diego Cancho
Axes: 5 (attention), 8 (metacognition)

## Problem

A turn-based agent answers every message. That fails in two different ways.

**In any conversation, people do not write one message per thought.** They send fragments,
correct themselves, add context a few seconds later, send an emoji, or ramble. An agent that
answers each message separately replies to half a thought, then to the other half, and floods the
chat. Sometimes the extra message is noise; often it is the part that matters.

**In a group, most messages are not addressed to the agent.** Answering them is expensive,
because each costs a full model turn, and rude, because people are often talking to each other.

The first problem is about knowing when someone has finished. The second is about knowing whether
it is the agent's turn at all. Both rest on the same unit: not the message, but the burst.

## End of turn

Before anything else, the agent waits until the person has probably finished. Voice agents call
this endpointing. In text, the signals are different but the question is the same.

### Signals

| Signal | Effect on the wait |
| --- | --- |
| The person is typing, where the surface reports it | Wait while the signal is fresh |
| Time since the last message | The longer the silence, the more likely the turn is over |
| The shape of the last message: a complete question, a long message | Shorter wait |
| The shape of the last message: trailing "...", a comma or colon, a connective, a short fragment | Longer wait |
| The rhythm of this person | Sets the base wait |

### Measuring instead of fixing

The base wait is not a constant. For each person on each surface, the agent records the gaps
between messages that belong to the same burst and waits somewhat longer than their usual gap,
within a floor and a ceiling. Someone who writes in a stream of short messages gets a longer wait;
someone who writes complete paragraphs gets an answer sooner. Before there is any data, a default
applies.

This is learning about how a person works, the same kind of fact as the [people](people.md)
proposal, but it is a number about rhythm, not a judgment. The first version keeps it in memory
for the life of the process; persisting it belongs with the person scope.

A burst also has a hard limit. If someone keeps typing indefinitely, the agent does not wait
forever.

### Typing as a surface capability

Not every surface reports typing, so it is a capability the surface declares, and end-of-turn
detection must work without it.

| Surface | Reports the person typing | Can show the agent typing |
| --- | --- | --- |
| Web chat | Yes | Yes |
| Discord | Yes | Yes |
| Telegram | No, the bot API does not deliver it | Yes |
| Console | No | No |

Typing is an expiring signal, not a start and stop pair. A stop is unreliable: the person closes
the tab, deletes the draft, or walks away. The surface renews the signal while keys are being
pressed, and it lapses on its own.

The signal goes to the turn-taking component, not to the model. The agent does not need to read
"the user is typing" in its context; it only needs not to start too early.

The same capability runs the other way: while the agent is working on a turn, the surface can
show that it is typing, which is most of what makes an agent feel responsive.

### Messages that arrive late

However long the wait, sometimes a message arrives after the agent has started:

- **Before the agent has answered:** the turn could be discarded and redone with the whole burst.
  Cheap, since nothing has been sent.
- **While the agent is running tools:** the message could be injected at the next step boundary.
- **After the agent has answered:** the message opens a new turn, as today.

The first two need changes to the loop and belong with the foreground and background work; the
first version of end-of-turn detection does not handle them.

## Groups

In a group, the burst also has to be judged for whether it concerns the agent. Three layers,
ordered by cost; each decides only whether the next one runs.

### Layer 0: deterministic rules

A direct mention, or a reply to one of the agent's own messages, wakes the agent once the burst
ends. It does not go through the classifier, because the worst failure is ignoring someone who is
talking to the agent.

### Layer 1: a small classifier

Everything else is evaluated by a small, cheap model, once per burst.

Input:
- A window of recent messages with their authors.
- The people the agent knows, and what is accepted about them (see [people](people.md)).
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

Wait is an action with a timer. An unanswered question can be left for a while, and the agent
speaks only if nobody else did. This is the difference between "should not answer" and "should
not answer yet".

Signals for the classifier:
- Whether the agent is addressed, explicitly or by the shape of the message.
- Whether a question is directed at no one in particular and still open.
- Whether people are mid-exchange with each other.
- Whether the agent knows something relevant, judged by memories with a good utility record.
- Whether the agent spoke recently, so it does not dominate.
- The asymmetry of errors: interrupting wrongly costs more than staying silent wrongly.

Speaking without knowing is worse than staying silent. The classifier can use the same utility
record as the confidence gate: if the agent's memories on a topic have kept failing to apply, it
should be more cautious about volunteering.

### Layer 2: the agent

The main agent runs only when layer 0 or 1 wakes it. Waking it does not oblige it to speak.

## Measuring it

On scripted conversations with controlled timing:

- How often the agent answers a burst before it was over.
- How long the agent waits after a burst that was over.
- In groups, how often it speaks when it should not have, and how often it fails to respond when
  addressed directly. The last must be near zero.

Test with synthetic conversations only.

## Alternatives considered

- **A fixed debounce.** Simple, and wrong for most people: too short for someone who writes in
  fragments, too long for someone who writes paragraphs.
- **Ask a model whether the message is complete.** Possible for ambiguous cases, but it adds a call
  to every message, and timing and typing already answer most cases for free.
- **The main model decides everything in a group.** It pays a full turn per message.
- **Rules only in a group.** Cheap and predictable, but it cannot tell an open question from a side
  comment.

## Open questions

- Should the shape heuristics be learned per person, like the base wait?
- Do groups need their own settings, such as "only speak when mentioned"?
- How are overlapping threads in one channel told apart?
- How is the group classifier calibrated without using real conversations?

## Changes this would require

- Surfaces declare capabilities, and report typing as an expiring signal alongside messages.
- A turn-taking component between the router and the conversation that groups messages into
  bursts, measures each person's rhythm, and decides when a burst is over.
- Surfaces that can show typing do so while the agent works on a turn.
- For groups: sender identity carried through (see [people](people.md)), the layer 0 rules, the
  classifier, and wait timers.
