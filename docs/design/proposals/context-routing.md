# Proposal: deciding how much of the pipeline a turn needs

Status: Accepted
Author: Diego Cancho
Axes: 5 (attention), with 8 (metacognition) for confidence and 7 (learning) for calibration

## Problem

Every turn pays for the whole pipeline, whatever it asks. A greeting and a research question
both go through the same steps:

1. Rewrite the message into a retrieval query (a model call).
2. Embed it and search memory, lexically and semantically.
3. Rerank the candidates (a model call).
4. Send the main model the persona, the recalled memories, and every tool definition.

Measured on the first runs against a real model, a turn averaged five calls, about 3,000 input
tokens, and between 9 and 45 seconds. The main pass alone took about 1,450 input tokens, most of
them the definitions of some twenty tools, and a turn that used a tool paid that twice.

The cost is fixed per turn, not proportional to what the turn needs. That is the wrong shape
for an agent meant to sit in a conversation all day: most messages are small, and a few need
everything.

## Why the obvious fixes do not work

**Heuristics on the message.** Length, punctuation, and keywords do not know what a message
means. "¿Y lo de ayer?" is four words and needs memory more than most long messages. A rule that
skips memory for short messages would skip exactly the messages that refer back the most.

**Letting the main model decide.** The main model already decides which tools to call. But it can
only decide after it has been sent everything, and sending everything is the cost being avoided.

**A small generative model with structured output.** It works: ask a cheap chat model for a JSON
object with the decisions. It still pays for generation, its latency is that of a generation, and
it returns a choice without saying how sure it is.

## Proposal

Before attention assembles the context, a **decision model** answers a few fixed questions about
the turn. The answers decide which parts of the pipeline run.

### Why a decision model

A new class of models is built for this: given a situation and a fixed set of options, they return
a probability for each option instead of generating text. Microsoft Decision-1 (in the Foundry
catalog), TypeSafe's Jev, and the OpenAI Decisions API are examples. Reported prices are a few
cents per million input tokens with free output, and reported latencies are well under a second.
Those figures come from vendors and secondary sources and must be measured before they are relied
on.

Two properties make this class the right fit rather than just a cheaper one:

- **The answer is always one of the declared options.** There is nothing to parse and nothing to
  hallucinate. The questions are defined in code, so the router cannot drift into new categories.
- **Every answer carries a probability.** This is what turns a shortcut into a decision the agent
  can reason about: it knows when it is unsure, and can choose not to take the shortcut.

### The questions

| Question | Kind | Options |
| --- | --- | --- |
| How much memory does this turn need? | Choice | None, light, full |
| Does it need the web? | Yes or no | |
| Does it need workspace files or the index? | Yes or no | |
| Does it need the taskboard? | Yes or no | |
| Does it need to delegate to other agents? | Yes or no | |

Memory levels:

| Level | What runs |
| --- | --- |
| None | Resident memories only; they are already in the stable system prompt |
| Light | Embed the message as written, hybrid search, small budget; no rewrite, no rerank |
| Full | The current pipeline: rewrite, wide hybrid search, rerank, full budget |

Tool categories group the tool definitions. Only the categories that pass are sent to the model,
plus a small core that is always present (`remember`, so the agent can always learn).

The decision model sees the latest burst, the last few turns, and cheap cues: whether the memory
store has anything in it, and which tools were used recently.

### Confidence and failing open

The router never makes the turn worse than today. Every decision has a threshold, and anything
below it falls back to the full behavior:

- If the decision model errors, times out, or is not configured, the turn runs the full pipeline
  with every tool.
- If no memory level clears its threshold, memory is full.
- A tool category is dropped only when the model is confident it is not needed. Uncertain means
  included.

The thresholds are asymmetric on purpose. Leaving out memory or a tool that was needed produces a
wrong or empty answer, which costs far more than sending a few hundred unnecessary tokens.

### Guidance: steering, not only trimming

Routing can also add to a turn. Some failures are predictable from the message alone: a model
asked for a loan schedule will do the arithmetic in its head and get it wrong while sounding sure.
The decision model cannot do the arithmetic either, but it can recognize that a message needs exact
numbers.

So the router may add fixed instructions to a turn's context. Each one is a yes or no question and
a text written by a person, for example: when the message involves calculating or stating exact
numbers, tell the agent to compute every figure with the calculation tool. The router decides
whether an instruction applies; it never writes one.

The threshold is low, because a reminder that was not needed costs a sentence, while a missing one
costs a wrong number. Guidance only adds, so it applies in shadow turns as well, and it is dropped
when the decision model fails, since nothing is known about the turn then. The persona carries the
same rule in general terms; guidance repeats it at the moment it matters.

### An escape hatch for tools

A dropped category can still be reached. The model always has one more tool, `request_tools`,
which lists the categories not offered this turn and adds them for the rest of the turn. Asking
for them costs one extra pass; a wrong routing decision costs a few seconds, not a wrong answer.

### Measuring the router

A router's mistakes are invisible: nobody notices the memory that was not recalled. So a fraction
of turns, chosen at random, ignore the router and run the full pipeline while still recording what
the router decided. These shadow turns give two error rates:

- **Memory misses:** the router said none or light, the full pipeline injected memories, and the
  utility judge found them useful.
- **Tool misses:** the router dropped a category, and the model used a tool from it.

Both feed the same append-only utility ledger the rest of the learning machinery uses, and the
thresholds are tuned from it. This is the link to learning (axis 7): the router is calibrated by
the agent's own measured experience, not by a fixed rule.

## Why this belongs to attention

Attention decides what reaches the model this turn. This proposal extends that decision upstream:
not only which memories to show, but whether to look for memories at all, and which capabilities
to show. It is the same principle as the [fidelity ladder](fidelity-ladder.md), applied to the
pipeline instead of to a source: spend detail only where the task needs it.

The confidence threshold is metacognition (axis 8) in its plainest form: the agent decides how
far to trust its own shortcut, and pays for the full path when it does not.

## Expected effect

On a turn that needs neither memory nor tools, two model calls disappear (rewrite and rerank) and
the main pass loses most of its tool definitions, in exchange for one classification call. On a
turn that needs everything, the router adds one fast call and changes nothing else. Whether this
is a net gain depends on the decision model's real latency, which is the first thing to measure.

## Alternatives considered

- **Heuristics.** Rejected for the reason above: they do not know what a message means.
- **Nearest examples over embeddings.** The message is embedded anyway, so comparing it to
  labelled examples costs nothing extra. It needs a curated set of examples and only separates
  coarse categories. It is a reasonable fallback when no decision model is configured.
- **A generative router.** Works with any chat model and can rewrite the query in the same call.
  It pays for generation and gives no confidence. Kept as the fallback implementation, so the
  design can be tested before a decision model is deployed.
- **Doing nothing.** The cost per turn stays fixed, and grows with every tool added.

## Open questions

- What is the real latency of a decision call from this host, and does it beat the calls it
  replaces?
- How calibrated are the probabilities on mixed Spanish and English messages?
- How many previous turns does the router need to judge references like "lo de ayer"?
- Should thresholds be learned per person, alongside their rhythm and their accepted facts?
- Can the same decision model serve the group classifier in the [participation](participation.md)
  proposal? The question shape is the same: a burst, its context, and a fixed set of answers.

## Changes this would require

- A decision model seam with implementations for Microsoft Decision-1 and a generative fallback.
- Tool categories, a per-turn tool selection in the loop, and the `request_tools` tool.
- Memory levels in the retriever: skipping rewrite and rerank, and a smaller budget for light.
- Shadow sampling and router outcomes recorded in the utility ledger.
- Per-call latency in the token meter, to measure the effect.
- Fixed guidance texts with their questions, added to the turn context when they apply.
