# Eight axes

An agent is more than a model in a loop. Splitting it into axes makes it clear which parts can
be swapped, which parts are the real research questions, and where a design is tangled.

| Axis | Question it answers | Owned here |
| --- | --- | --- |
| 1. Reasoning | The model that thinks | No, it is a fixed input |
| 2. Memory store | Where memories live and how they are found | Yes |
| 3. Environment and tools | What the agent can perceive and do | Partly |
| 4. Planning and flow | How work is sequenced and decomposed | Half |
| 5. Attention | What is in the context window right now | Yes |
| 6. Identity and policy | How the agent behaves and why | Yes |
| 7. Learning | How experience becomes durable knowledge | Yes |
| 8. Metacognition | Knowing when its own recall or reasoning is unreliable | Yes |

Axis 1 is a model choice. Axis 3 is largely a catalogue of tools anyone can add. The axes this
project cares about are 2, 5, 6, 7, and 8, with planning only partly in scope.

## The claim

The difference between agents that remember and agents that improve is in axis 7, not axis 2.

A memory store answers "what did we keep". Plenty of systems do that well. Learning answers
"what should we keep, what should we stop trusting, and how do we know". That needs a feedback
signal about whether remembered content was useful, and a process that acts on it. The memory
store is only the substrate.

Attention (5) is separate from memory (2) because what is stored and what is shown to the model
this turn are different decisions, with different failure modes. Metacognition (8) sits on top
of both: it is the layer that reads the learning signal back at decision time.

## How the code maps to the axes

| Axis | Where | State |
| --- | --- | --- |
| 1 | `ILlmProvider` and its implementations | Clean seam |
| 2 | `IMemoryStore`, `SqliteMemoryStore`, `MemoryManager` | Clean seam |
| 3 | `ITool`, `ToolRegistry`, `IWorkspace` | Clean seam |
| 4 | `AgentLoop.RunAsync`, taskboard, subagent tools | Implicit in the loop, no seam |
| 5 | `Attention/`: `IAttentionManager`, `ContextAssembler` | Extracted; compaction and truncation still outside it |
| 6 | The system prompt string | Not yet a seam |
| 7 | `SleepJob`, `ConsolidationLedger`, `UtilityTracker` | Modular, but the compaction-time consolidator is coupled to compaction |
| 8 | `MetacognitiveAttentionManager` | First version built |

An honest inspection of the original code found that only axes 1 and 3 were cleanly modular.
Attention had no home: it was spread across private methods of the loop, the compactor, the
retriever, and the tool-result truncator. Planning was implicit in the loop. Identity was a bare
string. Metacognition did not exist.

## Order of work

Extract the skeleton first, then hang the thesis on it. Attention (5) was extracted, then
metacognition (8) was built as a decorator over it, because it needs a place to intervene. After
that come the rest of attention (5b, compaction and truncation), then learning (7), planning (4),
and identity (6). See the [roadmap](roadmap.md).
