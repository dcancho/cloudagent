# Proposal: judging memory by what changes without it

Status: Proposed
Author: Diego Cancho
Axes: 7 (learning), with 8 (metacognition) for what the agent trusts

## Problem

The utility loop decides which memories stay. After a turn, a judge model sees the memories that
were injected, the person's request, and the agent's final reply, and says which memories
influenced the reply. A memory shown three times and never marked useful stops being injected,
and the sleep job demotes it to Cold.

That works for memories that get quoted. It fails for the knowledge a worker is actually paid
for: conventions and decisions that shape the work without ever being mentioned. A colleague who
always uses `decimal` for money does not say so in every message; it just shows in the code.

The payroll experiment showed it on its second session. The agent recalled the team's rounding
convention twice and followed it exactly (every payslip field right, no floating point in the
code), and the judge marked it unused both times, because the reply was a short summary and the
judge never saw the code. One more recall would have started suppressing and demoting the most
important convention the agent had, because it was being followed.

Two things are wrong at once:

- **The judge sees the wrong evidence.** In a coding turn, the influence of a convention is in the
  edits, not in the reply.
- **"Used" is the wrong question for a habit.** A habit's value is in what the work would have been
  without it, which no single run can show.

## Proposal

### 1. Judge the work, not only the reply

The judge also receives what the turn changed: the text written by `edit_file` and `write_file`,
clipped like the rest. The question changes from "did the reply use this memory" to "is the work
consistent with this memory, and would it plausibly have differed without it". This is cheap and
fixes the most common false "unused".

### 2. Check against the counterfactual before forgetting

Before a memory is suppressed or demoted for low usefulness, the agent reruns a recent turn in
which it was injected, without that memory, and compares. The rerun is a shadow: it must not
change anything real.

- **Sandbox.** Either a throwaway git worktree at the state the turn started from, or a dry-run
  workspace where writes are recorded instead of applied and commands are refused. The worktree is
  faithful and costs a full turn; the dry run is cheaper and less faithful.
- **Comparison per memory, not per output.** Two runs of the same prompt always differ somewhat.
  The question is whether each version complies with the memory:

| With the memory | Without it | Verdict |
| --- | --- | --- |
| complies | violates | Useful: the memory caused the behavior |
| complies | complies | Redundant: the code or the request already carries it |
| violates | violates | Ineffective: recalled and ignored |
| violates | complies | Harmful: the memory pulled the work the wrong way |

- **Who answers.** Compliance is a yes or no question with a known rule, which suits the decision
  model behind context routing: cheap, fast, and it returns a probability, so an uncertain answer
  counts as "unknown" rather than as evidence.
- **What follows.** Useful keeps the memory and records the evidence in the utility ledger.
  Redundant lets it go quietly, since the code holds the knowledge. Ineffective and harmful are
  surfaced for review rather than acted on silently.

### 3. Spend only where a decision depends on it

Counterfactuals are expensive: a shadow turn roughly doubles the cost of the turn it repeats. They
run only when an outcome hangs on them:

- before a memory would be suppressed or demoted for low usefulness;
- optionally, in experiments, as a sampled shadow alongside normal turns, the way context routing
  samples shadow turns to measure its own mistakes.

Over a memory's life that is a handful of checks, not one per turn.

### 4. Kinds of memory behave differently

Conventions and decisions a reviewer approved are standing knowledge: they stay resident or
recallable regardless of how often they are quoted, and change only through review or a
counterfactual showing them redundant. Episodes and incidental facts keep the use-it-or-lose-it
rule. The review already makes this distinction in practice: in the experiment, the reviewer kept
the habits (money is `decimal`, how proration works) and discarded the current formulas, which the
code owns and the next session changed.

## Why it matters

The aim is an agent that learns the way a worker does: it stops looking things up and keeps
working the way it worked, by something like inertia, instead of re-deriving each convention and
sometimes picking a different valid one. Most harnesses avoid internalized knowledge because a
memory nobody can audit is a liability, so they keep knowledge in documents the agent re-reads
every time. This project's memory is auditable (readable entries, a hash-chained ledger, staged
review). What it lacks is a fair test of whether a memory earned its place. Without one, the
utility loop teaches the agent to forget its habits.

## Expected effect

- Conventions followed silently are no longer demoted.
- The utility ledger separates useful, redundant, ineffective, and harmful memories, which also
  gives the experiments a per-memory result instead of only a whole-arm comparison.
- Extra cost concentrated where a decision depends on it, near zero for memories that are plainly
  useful or plainly unused.

## Alternatives considered

- **Raise the demotion threshold.** Delays the problem without changing what is measured.
- **Exempt all facts from demotion.** Memory only grows, and stale or wrong facts never leave.
- **Ask the agent to cite the memories it used.** Changes the behavior being measured, and
  conventions still go uncited when they are simply followed.
- **Leave-one-out on every turn.** The cleanest attribution and far too expensive outside
  experiments.

## Open questions

- Which turn to rerun: the most recent one with the memory injected, or the one where it scored
  highest in recall?
- How many shadow runs before a verdict, given that runs are not deterministic?
- Does a dry run, without builds or tests, reveal enough to judge compliance with a convention?
- Should a "harmful" verdict demote immediately, or always go to review?

## Changes this would require

- The judge receives the turn's edits, and its question becomes consistency rather than use.
- A shadow turn runner with a sandboxed workspace (worktree or dry run) and no surface output.
- A compliance question per memory for the decision model.
- A new utility signal for counterfactual verdicts, read by the metacognitive gate and the sleep
  job before they act.
- A memory kind or flag for standing knowledge approved in review.
