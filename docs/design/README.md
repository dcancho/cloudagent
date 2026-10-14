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

The consolidation judge prompt lives at [../consolidation-policy.md](../consolidation-policy.md)
because the host loads it at runtime. It is part of the design and is meant to be edited.

These documents are licensed under CC BY 4.0, see [LICENSE.md](LICENSE.md).
