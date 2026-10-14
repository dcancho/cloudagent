# Attention and metacognition

## Attention is not memory

Memory is what the agent has stored. Attention is what the model sees on this turn. Treating
them as one thing produces systems that either dump everything in or retrieve with no regard to
what the conversation currently needs. In CloudAgent they are separate modules with an
interface between them.

`IAttentionManager` assembles the context for a turn. The interface is named for the role, not
for an implementation, because the metacognitive gate is also an attention manager.

## Two parts of every prompt

Context assembly splits content by how often it changes:

- **Stable.** The persona and resident memories. These go in the system prompt and change rarely.
- **Ephemeral.** The operational state, the rolling summary, and recalled memories. These change
  every turn.

The reason is prompt caching. Providers cache the longest unchanged prefix of a request. If
volatile content sits inside the system prompt, the prefix changes every turn and the cache never
hits. The original design had exactly this problem. Moving volatile content to the tail of the
latest user message keeps system prompt, tool definitions, and history cacheable.

Providers deliver the ephemeral part at the message tail. The Anthropic provider also places
cache breakpoints on tools, system, and the conversation tail and reports cache read and write
tokens; the Foundry provider relies on automatic prefix caching.

Recording which memories were injected is deliberately done by the loop, not the assembler.
Attention decides what to show; learning decides whether showing it worked. Keeping the
assembler free of recording keeps those two axes apart.

## The metacognitive gate

A decorator over any attention manager that withholds injected memories whose track record says
they keep not applying.

The reasoning behind its signal: retrieval similarity measures familiarity, how much a memory
looks like the query. Familiarity is the half of the problem that fools you, since a memory can
look highly relevant and still be useless or misleading. Usefulness is measured by the utility
log, whether the response actually used the memory when it was injected before. The gate reads
the second signal, not the first.

A memory is suppressed when it has been injected at least three times and has never been
useful. Properties worth noting:

- **Per turn and reversible.** The memory is untouched in the store. Unlike the sleep job's
  demotion to Cold, suppression applies only to this assembly, and a memory can return when its
  statistics change.
- **Inert on a cold start.** With no track record there is nothing to be skeptical about, so
  nothing is suppressed until measurement accumulates.
- **Optional.** The gate is a decorator behind a flag. The base assembler works without it.

This is a first, narrow form of metacognition: knowing that one's own recall has been unreliable.
Broader forms, such as noticing a gap in knowledge before answering, are future work.

## Related: when to speak

Another place the same machinery applies is deciding whether to respond at all in a shared
conversation. A turn-based agent answers every message; a participant in a group does not. That
decision needs the conversation state, what the agent knows, and the cost of interrupting. It
is not designed yet.
