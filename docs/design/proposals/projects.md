# Proposal: projects as shared context across repositories and conversations

Status: Proposed
Author: Diego Cancho
Axes: 2 (memory store), 5 (attention), 6 (identity and policy), 7 (learning)

## Problem

"Project" in the memory design means one workspace: the agent's own, or one repository. Project
memory is a database per workspace, so it reaches exactly one place. Real work does not stay in one
place. The same piece of work is discussed in a chat, built in one repository, continued in a
second, and revisited from another surface, and the knowledge that matters to all of them (the
team's conventions, the decisions taken, the domain rules) belongs to the work, not to any one
repository.

The payroll experiment showed the cost. Every convention the reviewer approved (money is
`decimal`, nothing is rounded until output, the HR export uses `;` and `dd/MM/yyyy`, a month has 30
days, ask when a rule is unclear) landed in the payroll repository's memory next to facts that only
hold there, like the CLI living in `src/Payroll.Cli`. A second repository for the same team would
start with none of it, and so would a chat about the same work outside a repository. Only personal
memory crosses over, and it is the wrong place for a team's rules.

Assistants that offer projects tend to keep them per product: a project's context in a chat app is
not available to the same vendor's coding tool, so decisions made in one have to be re-explained in
the other. CloudAgent runs every surface (web chat, console, Discord, Telegram, coding profiles)
from one host with one memory machinery, which makes a shared context across them natural rather
than a synchronization problem.

## Proposal

### 1. A project is a named context that things link to

A project has a name, its own memory, its own instructions, and a set of links:

- **Repositories**, by the names in `CODE_REPOS`.
- **Conversations**, on any surface.
- **Members**, the people working on it, through the person ids from the
  [people](people.md) proposal.

Links are loose. A repository can be linked to more than one project (a shared library, a monorepo),
and a project can have any number of repositories, or none. A conversation works in **exactly one
project at a time**, or in none, so recall always has one clear context. When a conversation starts
in a repository linked to a single project, that project is the default; otherwise the person
chooses.

### 2. Scopes become a ladder

| Scope | Holds | Reaches |
| --- | --- | --- |
| Personal | The user and how they work | Everything that user does |
| **Project** | Conventions, decisions, domain rules, vocabulary | Every repository and conversation linked to the project |
| **Repository** | Facts about one codebase: layout, build commands, flaky tests | That repository, in any project |
| Session | The current conversation and its episodes | That conversation |

The current per-workspace "Project" scope becomes the repository scope. The person scope from the
[people](people.md) proposal sits beside this ladder, not on it.

Every fact is stored at the narrowest level where it is true. "The CLI is in `src/Payroll.Cli`" is a
repository fact. "Money is `decimal`" is a project fact. "Diego wants to be asked when a rule is
unclear" is personal.

### 3. Learning proposes the level; review decides it

The consolidator and session learning already classify each fact's scope. They now propose one of
the four levels, and staged review shows it and lets the reviewer move a fact up ("this is a team
rule, not a repository detail") or down. Facts already in memory can be moved the same way, through
the ledger, so every move is recorded and reversible.

### 4. Project instructions are resident

Each project has instructions: standing rules that apply to everything done in it. They are text
that people write and edit, always placed in the stable system prompt of every conversation in the
project, never left to recall. The agent can propose an instruction through staged review, like a
fact; it can never write one directly.

This is where behavioral rules belong. In the payroll experiment, "ask when a rule is unclear" was
in memory but was never recalled, because recall ranks by similarity to the current message and a
rule about how to work never resembles the task. As a project instruction, it would always be
present. Being in the stable prompt, instructions also leave the prompt cache intact.

### 5. Recall reaches every level in reach

A turn recalls from personal memory, the current project, the current repository, and the session,
in one ranked pass with one budget. Each recalled fact carries its level, both for the model ("team
rule" reads differently from "true in this repository") and for analysis.

When facts conflict, the narrower level wins: a repository that deliberately differs from the team
convention overrides it, as a local git setting overrides a global one. Conflicts found during
consolidation are surfaced for review rather than resolved silently.

Forgetting differs by level, following what the [memory counterfactuals](memory-counterfactuals.md)
work found:

- **Repository facts** describe code that changes, so the use-it-or-lose-it rules keep applying.
- **Project facts** change through review or through counterfactual evidence, not through going
  unmentioned. A convention that is redundant in one repository, because that code already carries
  it, is still needed in the next one.
- **Instructions** change only when a person edits them.

### 6. Any surface can work in a project

- **Web chat:** a new conversation picks a project and, optionally, one of its repositories. This
  replaces today's choice between general and a repository.
- **Console:** a flag or a command sets the project.
- **Group surfaces (Discord, Telegram, Slack later):** a channel is linked to a project, so every
  conversation in it works there.

A decision taken in a chat is then available to the coding conversation that implements it, with
no copying between tools, because both read the same project memory.

### 7. Visibility

Project memory and instructions are visible to the project's members, and only facts about the work
belong there. What someone says about themselves stays in personal or person scope. When several
people share a conversation, recall filters by who is present, as the research on group agents
recommends: a fact learned in a one-to-one conversation does not surface in a group unless it is a
project fact. Members can review staged project facts and edit instructions.

### 8. Storage

- `.agent/projects/<id>/project.json`: name, instructions, links, members.
- `.agent/projects/<id>/memory.db`: project memory, the same SQLite store as every other scope.
- `.agent/repos/<name>/`: repository memory and index, as today.

Moving to this layout is a one-time step. Today's per-repository `project.db` becomes repository
memory unchanged, and a review pass over its facts proposes which ones to promote to a project.

## Why it matters

- It removes the need to re-explain the work every time it moves between a chat, a repository, and
  another surface.
- It gives team knowledge a home of the right size: wider than one repository, narrower than a
  person.
- It turns the rules about how to work into something the agent always sees, which recall alone
  cannot do.
- It makes the transfer experiment meaningful: whether project memory makes a new repository follow
  the team's conventions can then be measured as the system actually works.

## Alternatives considered

- **Everything in personal memory.** Simplest, and wrong for a team: one person's agent would mix
  every project's rules, and nothing could be shared with other members.
- **Per-repository memory only.** Today's design; the payroll experiment shows where it fails.
- **Tags instead of levels.** Facts tagged with project names and filtered at recall. More flexible,
  but with no rule for which fact wins when two disagree, and no natural place for instructions.
- **Inferring the project from the conversation's content.** Wrong attributions would write team
  rules into the wrong project, where they are hard to notice.

## Decided

- **No level above projects.** Personal and project are the shared levels; an organization level is
  out of scope.
- **A fact is not linked or copied between projects; shared systems carry it.** Two projects often
  share a fact because they share a system: the same HR export, the same framework, the same API.
  Systems are recognized from usage, as entities: names that recur in the project's conversations,
  the packages and configuration in its repositories, the commands it runs and the formats it reads.
  Nobody declares them. A system recognized often enough in a project's work becomes one of its
  systems; the list is visible in the project, and a member can remove a wrong one. A fact learned in one project about a system reaches
  every other project that lists the same system as a **hint**: recalled with less weight and
  marked with where it came from. When the work in the second project agrees with it, consolidation
  confirms it there and it becomes that project's fact; when the work contradicts it, the hint is
  dropped for that project and the conflict is surfaced. Agreement across projects is evidence that
  the fact is true, which a manual link or copy would not give.
- **One base consolidation policy, with addenda by kind of work.** What is worth remembering
  depends on the kind of work (code, research, writing), and one project can hold several kinds.
  So addenda attach to the kind of work, not to the project: each conversation's material is
  consolidated with the base policy plus the addendum for the work it shows. Project instructions
  stay the place for anything specific to one project.
- **Archive by default; delete only on explicit confirmation.** An archived project is not recalled
  and does not appear in choices, but keeps everything and can be restored. Its conversations stay
  readable, marked as belonging to an archived project. Deleting removes the project's memory and
  instructions for real; the ledger records that a deletion happened and which fact ids it removed,
  not their content. Repository memory stays, since it describes the code, not the project. One
  thing about people survives a deletion: the link between a person and a fact whose effects last
  beyond the project (who decided a convention, who approved a rule) is kept wherever that fact
  survives, in a repository's memory, another project or the code itself.
- **Other agents can read project memory through MCP, and only read it.** An MCP server exposes a
  project's instructions and facts, with each fact's level and evidence, to agents that are not
  CloudAgent. It does not expose the recall pipeline, the ranking, or conversation context, and it
  accepts no writes: the other agent reads the knowledge and adapts it to its own understanding. The
  visibility rules apply, so it serves only what the connecting member could see. This is what lets
  a coding tool and a chat app from another vendor share the same project context without
  synchronizing it by hand.

## Changes this would require

- A project registry: create, rename, archive, link repositories and conversations, list members.
- A project scope and a repository scope in the memory store, replacing today's single project scope,
  with each recalled fact tagged by level.
- Project instructions in the stable system prompt of every conversation in the project.
- Level proposals in the consolidator and session learning; level moves in staged review and the
  ledger.
- The web client's new-conversation choice becomes project, then optionally repository; the console
  and group surfaces gain a way to set the project.
- Visibility rules for project memory in shared conversations.
- A migration that keeps today's repository memory and stages promotions to a project.
- Entity recognition of systems from each project's work, hints across projects that share a
  system, and confirmation of hints in consolidation.
- Consolidation policy addenda by kind of work, chosen per conversation.
- Archive and restore for projects; deletion that keeps the ledger entry and lasting person links.
- A read-only MCP server over project instructions and facts, filtered by visibility.
