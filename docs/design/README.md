# Design

The design behind CloudAgent, written to be read without the code. The code in `src/` is one
reference implementation of it.

Author: Diego Cancho

Each document separates what is implemented from what is only designed. Where a claim rests on
related work, it points to [references](references.md); where it rests on the implementation,
it names the type involved.

1. [Eight axes](eight-axes.md): what an agent is made of, and which parts this project owns.
2. [Memory and learning](memory-and-learning.md): how memories are stored, retrieved, measured,
   and consolidated.
3. [Attention and metacognition](attention-and-metacognition.md): how the context window is
   assembled and when the agent should distrust its own recall.
4. [Roadmap](roadmap.md): built, partial, and open.
5. [References](references.md): related work.

Proposals are ideas that are reasoned through but not built. Each states its status at the top.

- [People](proposals/people.md): knowing who said what, and learning how each person works.
- [Participation](proposals/participation.md): taking turns, from knowing when someone has finished
  to deciding whether to speak in a group.
- [Thinking without speaking](proposals/thinking-without-speaking.md): a turn that does not
  have to end in a message, and what streaming publishes.
- [Context routing](proposals/context-routing.md): a decision model chooses how much of the
  pipeline each turn needs, and falls back to all of it when unsure.
- [Fidelity ladder](proposals/fidelity-ladder.md): perceiving a source as a summary, a query, or
  the source itself, and climbing only as far as needed.
- [Entity access](proposals/entity-access.md): memories that belong to entities such as a client or
  a domain of work, hidden from anyone not on the entity's access list.
- [Conversation gist](proposals/conversation-gist.md): a retelling of a conversation in order,
  with marked readings, written during sleep, between the literal episodes and a summary.

The consolidation judge prompt lives at [../consolidation-policy.md](../consolidation-policy.md)
because the host loads it at runtime. It is part of the design and is meant to be edited.

These documents are licensed under CC BY 4.0, see [LICENSE.md](LICENSE.md).
