# Proposal: measuring memory by running with and without it

Status: Proposed
Author: Diego Cancho
Axes: 7 (learning), with 8 (metacognition) for what the agent trusts

## Problem

The utility loop decides which memories stay. After a turn, a judge model sees the memories that
were injected, the person's request, and the agent's final reply, and says which memories
influenced the reply. A memory shown three times and never marked useful stops being injected,
and the sleep job demotes it to Cold.

That judge is wrong in two independent ways.

- **It sees the result, not the work.** In a coding turn the final reply is a short summary. The
  influence of a convention or a habit is in what the agent did: what it read, what it ran, what it
  wrote, and whether it asked. None of that reaches the judge.
- **It cannot separate the memory from the context.** Even a judge that saw everything could not
  tell, from one run, whether the agent followed a convention because the memory was injected or
  because the context already carried it: the code it read, earlier messages, other memories. "The
  work matches the memory" does not mean "the memory made the difference".

The payroll experiment (2026-10-16) showed the cost. The team's rounding convention (money is
`decimal`, nothing is rounded until the payslip is written) was recalled three times and followed
exactly every time: every payslip field matched, and no floating point appeared in the code. The
judge marked it unused all three times, because the replies never mentioned it. The sleep job then
demoted it, together with the project layout, the CLI contract, and the 30-day month rule, to Cold.
Cold memories are never recalled again, so from the fourth session on the agent kept the
convention only because the code showed it, and when asked at the end what conventions it knew, it
did not list it.

## Proposal

Measure a memory's influence the way an experiment would: run the same turn with the memory and
without it, and compare what the agent did. Use a decision model to read the work, not a judge to
read the reply.

### 1. The work as evidence

Whatever reads a turn reads its action history, not only its final reply: the person's messages,
each tool call with its key arguments (commands, paths, the text an edit replaced and wrote, the
files it created), the first line of each command's result, the questions the agent asked, and
what it said. This is the record the transcript already keeps; it only needs a compact rendering.

### 2. Observable questions for a decision model

Each memory has a question about behavior that can be seen in the work, written when the memory is
approved in review. "Did this memory influence the run?" is not one of them: it is a causal
question, and no single run answers it. A decision model, the same kind context routing uses, answers
the observable question with a probability per option.

Two rules make the answers trustworthy, both learned from the probe below:

- **Every question has a "not shown" option.** A yes or no question forces an answer when the
  history has no evidence either way, and that answer reads as a violation.
- **Questions ask about this work, not about the state it inherited.** "Did this work add or change
  an overtime cap?" separates the turn that implemented a rule from the turns that only found it in
  the code. "Does the code cap overtime?" does not.

An answer below a confidence threshold counts as unknown, never as evidence.

### 3. Shadow pairs separate the memory from the context

On a sample of turns where a memory is injected, the turn runs twice from the same starting state:
once as it was, once with that memory withheld. The shadow run produces no output to anyone and
changes nothing real. Both histories get the memory's question, and the pair gives the verdict:

| With the memory | Without it | Verdict |
| --- | --- | --- |
| complies | violates | Useful: the memory caused the behavior |
| complies | complies | Redundant here: the context already carries it |
| violates | violates | Ineffective: recalled and ignored |
| violates | complies | Harmful: the memory pulled the work the wrong way |
| not shown or unknown on either side | | No verdict |

Withholding one memory at a time attributes the effect to it; withholding all of them measures
what recall is worth as a whole. The shadow needs the starting state recorded when the turn begins,
because the agent often has not committed: either a copy of the working tree or a git tree object
written through a temporary index, and a worktree or dry-run workspace to run in.

### 4. The pairs calibrate the cheap reading

Shadow pairs are expensive, so they are sampled, the way context routing samples shadow turns.
Every turn still gets the cheap reading: the decision model's answer on the one real run. On the
sampled turns, the pair's verdict is the reference, and the cheap reading is scored against it.
That gives the number the current judge never had: how often a single-run reading agrees with what
the pair shows, per kind of memory. Where they agree, the cheap reading can be trusted alone; where
they don't, decisions wait for pairs.

### 5. What the verdicts change

- The utility ledger records verdicts (useful, redundant, ineffective, harmful, unknown) instead of
  only used and unused.
- The metacognitive gate and the sleep job act on verdicts. A memory is never demoted for being
  unmentioned; it can be demoted for being redundant or ineffective across several pairs.
- Redundant means "not needed here, now". The code that carries a convention today does not exist
  in a new module or another repository, so a redundant memory stops being injected but stays
  recallable, and a later useful verdict brings it back.
- Ineffective and harmful verdicts go to review instead of being acted on silently.

## Evidence so far

A probe on 2026-10-16 asked Microsoft-Decision-1 observable questions about the five sessions of
the payroll experiment's memory arm, each rendered as a compact action history of 7k to 17k
characters. The answers were checked against what the experiment's scorer had verified.

- The decision API accepts up to 64,000 tokens of state plus questions; 128,000 characters took
  0.8 seconds. A whole coding session fits.
- Each session took 0.5 to 0.8 seconds for seven questions.
- "Uses `decimal`": 0.99 to 1.00 in all five sessions, all correct.
- "Rounds only at output", as yes or no: 1.00 falling to 0.41 by the fifth session, which did not
  touch rounding at all; read naively, a violation that never happened. With the options output,
  during, and not shown: output at 0.91 to 1.00 for the first four, and "not shown" at 0.96 for the
  fifth. Correct in all five.
- "Did this work add or change an overtime cap": 0.99 for the one session that implemented it, 0.00
  for the other four. Correct in all five.
- "Asked the person before choosing an unspecified rule": 0.03 for the session that, as the
  experiment recorded, assumed instead of asking. Correct, and 0.56 (unknown) where the history was
  ambiguous.
- "Added a rules engine library": 0.00 in all five, correct.

The probe tests the reading of single runs. It does not yet test pairs.

## Cost

A shadow turn costs about as much as the turn it repeats, and coding turns are expensive (the
experiment's fourth session used about a million input tokens). Cost is kept down by sampling pairs
at a low rate, by stopping a shadow run once it has acted on what the memory governs, and by
running pairs before a demotion that would otherwise happen on the cheap reading alone. The
decision model's reading costs well under a second per turn.

## Alternatives considered

- **A better prompt for the generative judge.** Still one run, so still unable to separate memory
  from context, and without calibrated probabilities.
- **Asking the agent to cite the memories it used.** Changes the behavior being measured, and
  habits are followed without being cited.
- **Exempting facts from demotion.** Memory only grows, and stale facts never leave.
- **Leave-one-out on every turn.** The cleanest attribution, and far too expensive outside
  experiments.

## Open questions

- How many pairs before a verdict counts, given that runs are not deterministic? Two that agree is
  a starting point.
- Withhold one memory at a time, or all, when several are injected?
- Who writes each memory's question: the reviewer when approving, or the consolidator when staging,
  with the reviewer editing it?
- Is a dry-run workspace, which records writes and refuses commands, faithful enough for habits
  that show in commands, like running the tests?

## Changes this would require

- A compact rendering of a turn's action history from the transcript.
- An observable question per memory, with a "not shown" option, stored with the memory.
- The decision model reading each turn's history for the injected memories' questions.
- A shadow turn runner: the starting state recorded at turn start, a sandboxed workspace, no
  surface output, and one memory withheld.
- New utility signals for readings and pair verdicts, read by the metacognitive gate and the sleep
  job before they act.
- A calibration record: cheap readings against pair verdicts, per memory kind.
