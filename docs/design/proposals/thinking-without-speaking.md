# Proposal: a turn is not a message

Status: Proposed
Author: Diego Cancho
Axes: 4 (planning and flow)

## Problem

The loop treats every completion that contains text as something to say. The conversation raises
its `Responded` event for each one, and the router sends it to the surface. A turn can therefore
only end in a message, or in nothing at all if the model produced no text.

An agent that works alongside people often needs a turn that ends differently: write something in
its own working notes, update its state, run a tool, react to a message, or deliberately say
nothing. Today those outcomes are either impossible to tell apart from speech or depend on the
model producing empty text.

## Proposal

Separate what the agent produces from what is published.

### Outcomes of a turn

| Outcome | Effect |
| --- | --- |
| Say | A message to the conversation, optionally mentioning someone |
| Note | A write to the operational state or to memory |
| Act | Tool calls, as today |
| React | A lightweight response where the surface supports it |
| Nothing | The turn ends with no output |

A turn can combine them. Only Say and React reach the surface.

### Mechanism

Plain text from the model becomes internal: it is recorded in the context and is never sent.
Publishing is explicit, through a `say` tool whose arguments are the text and the optional
mentions. Notes already have tools (`remember`, the taskboard, the operational state), and
reactions would be a second tool of the same shape.

For one-to-one conversations this would make the console and web chat awkward, since every reply
would need a tool call. A surface can therefore declare a mode: in direct mode, plain text is
published as it is today; in shared mode, only explicit publishing is.

## Why it matters

- It lets a woken agent decide to stay quiet after thinking, which is different from not being
  woken (see [participation](participation.md)).
- It keeps the agent's reasoning out of a shared channel.
- It gives mentions a structured form, so each surface can render them in its own syntax.
- It makes silent work, such as taking notes about an exchange, a normal outcome instead of a
  workaround.

## Alternatives considered

- **A sentinel for silence, such as a reserved reply that means "no message".** Fragile: it relies
  on the model producing an exact string, and it cannot carry mentions or reactions.
- **Structured final output.** Clean, but providers differ in how well they enforce it, and it
  complicates the tool-calling loop.
- **Filtering text after the fact.** Hides the problem instead of giving the agent control of it.

## Open questions

- Does a long turn stream partial text, and what is published while it runs?
- How do subagents fit? They already report through `report_progress` and `finish`, so they may
  keep their own channel.
- Should internal text count toward the working-memory budget the same as published text?
- What reactions can be expressed in a form that works across surfaces?

## Changes this would require

- A publishing path in the conversation separate from the loop's response event.
- A `say` tool, and a surface mode that selects between direct and shared behavior.
- Mention support in `OutboundMessage` and in each surface adapter.
