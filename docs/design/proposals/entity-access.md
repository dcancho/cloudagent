# Proposal: memories that belong to entities, with access lists

Status: Proposed
Author: Diego Cancho
Axes: 2 (memory store), 6 (identity and policy), 5 (attention)

## Problem

Visibility in the current design follows scope. Personal memory is the user's, session memory is
the conversation's, and the [projects](projects.md) proposal adds project memory visible to the
project's members. That is enough while everyone who can see a scope may see everything in it.

Two cases break it:

- **A sensitive subset inside a project.** The payroll project's members all need the conventions
  (money is `decimal`, the HR export uses `;`). Only two of them should see the salary bands, the
  name of the employee under review, or the client's negotiated rate. Scope gives the choice of
  showing these to every member or keeping them out of the project.
- **An entity that spans projects.** A client, a vendor or an internal team appears in several
  projects. What is known about it (the contact who signs, the contract's renewal date, the rule
  that their data never leaves the EU) should reach every project that works with it, and only the
  people cleared for that client. Projects share facts through systems as hints, but hints carry no
  access rule.

In both cases the boundary is drawn around what a memory is about, not where it was learned.

## Proposal

### 1. Entities, and memories that belong to them

An entity is a named thing memory can be about: a client, a system, a team, a domain of work such
as "salaries". The [projects](projects.md) proposal already recognizes systems as entities from
usage; this proposal extends the same registry to other kinds.

A memory can belong to zero or more entities. Belonging is a label on the memory, stored with it,
not inferred at recall time. A memory with no entity labels behaves as today.

### 2. An entity can have an access list

An entity is either **open** (no access list; the label only helps recall and grouping) or
**restricted**, with an access list of person ids from the [people](people.md) proposal. Groups
are allowed as a shorthand: a project's members, or a channel's members.

A restricted memory is eligible for a conversation only if **every** person who can read the reply
is on the access list of **every** restricted entity it belongs to. In a one-to-one conversation
that is one person. In a group it is everyone present, because anyone present reads what the agent
says. Work the agent does on its own (a scheduled task, a subagent) runs with the access of the
person who started it.

### 3. Hidden, not refused

A memory the conversation may not see does not exist for that conversation. The filter is applied
in the store query, before anything else runs:

- Both retrieval legs (lexical and semantic) query only eligible rows.
- The query rewriter and the reranker never see a restricted candidate. They are model calls, and
  what enters a model call can leave in a reply.
- Cold stubs are filtered the same way; a stub's preview is content.
- Resident memories are filtered before the stable prompt is built, so a restricted resident in a
  group conversation is simply absent there.
- `search_memory`, `restore_memory`, `list_consolidations` and the read-only MCP server from the
  projects proposal apply the same rule.

The agent does not say "there is something here you may not see". Saying so leaks that the thing
exists, which is often the sensitive part ("there is a note about your performance review").
When an ineligible person asks, the agent knows what it would know without that memory.

### 4. Labels propagate through learning

Every derived memory inherits the labels of what it was derived from:

- An episode carries the labels of its conversation's defaults (below).
- A fact distilled from episodes carries the union of their labels. Its evidence came from there,
  so its content may too.
- A [conversation gist](conversation-gist.md) carries the union of the labels of the episodes it
  condenses.

Removing a label is a declassification. Only a person on the entity's access list can do it, in
staged review, and the ledger records who did. Learning never removes a label on its own. A
consolidator that rewrites "Ana's band is B3" into "bands are assigned per level" may have removed
the sensitive part, or may not have; that is a judgment for a person.

### 5. Who labels

A missed label is a leak, so labeling cannot depend on a model noticing that something is
sensitive. Three sources, from strongest to weakest:

1. **Defaults by place.** A channel, a conversation or a repository can carry default entity
   labels. Everything learned there gets them. A private channel for salary review labels
   everything "salaries" without anyone thinking about it.
2. **Explicit.** The person says so ("keep this between us and Marta") or uses `remember` with an
   entity. The agent asks which entity when the wording is ambiguous.
3. **Proposed.** Learning recognizes that a staged fact is about a restricted entity and proposes
   the label in review. It can add labels, never remove them.

When the first two disagree with the third, the more restrictive one wins until a person decides.

### 6. What this does not protect

- **Anything already said.** Once a restricted memory reached a reply, the reply is in the
  transcript, in its episodes and in whatever the reader copied. Labels propagate to the episodes;
  they cannot reach the reader.
- **The host.** The access list is need-to-know among the people using one agent. Whoever runs the
  host can read the SQLite files. Isolation from the operator needs encryption per entity, which is
  out of scope.
- **The model's general knowledge.** If a fact is also public, hiding the memory does not hide the
  fact.

## Relation to projects and people

- A project's member list is the coarsest access list; this proposal adds finer ones that cut
  across or inside projects.
- The person scope from the people proposal is the special case where the entity is a person and
  the access list is that person plus whoever reviews person facts.
- Systems recognized by projects become open entities. A team can restrict one.

## Alternatives considered

- **Only projects.** Make a separate project for each sensitive subset. It works for the subset
  and breaks the shared conventions, which then have to be copied into both projects.
- **Refuse instead of hide.** Tell an ineligible person that a memory exists and is restricted.
  More honest-looking, and it leaks existence.
- **Filter after ranking.** Simpler to add, and it sends restricted content to the rewriter and
  reranker calls.
- **Classify sensitivity with a model at write time.** A classifier that misses one label in a
  hundred is a leak in a hundred. It is kept only as a proposer that adds labels.

## Open questions

- Whether the agent may use a restricted memory to decide *how* to answer without stating it, for
  example to avoid a topic. That is still a leak by behavior; the safe default is no.
- Who administers an entity's access list when several people share the agent.
- How long labels outlive a person's departure from a list. Removing someone from the list hides
  the memories from them from then on; it does not recall what they already saw.
- Whether entities nest ("salaries" inside "payroll") or stay flat with several labels per memory.
  Flat is simpler and is the starting point.

## Changes this would require

- An entity registry extending the systems registry from the projects proposal, with optional
  access lists of person ids and groups.
- Entity labels on `MemoryEntry`, indexed in `SqliteMemoryStore`.
- Eligibility filtering in the store query used by recall, stubs, residents and the memory tools.
- Default labels on channels, conversations and repositories.
- Label propagation in `MemoryManager.ArchiveAsync`, the consolidator, `SessionLearner` and
  `SleepJob`.
- Label proposals and declassification in staged review, with ledger entries.
- The audience of each reply (who is present) available to the attention module, which depends on
  sender identity from the people proposal.
