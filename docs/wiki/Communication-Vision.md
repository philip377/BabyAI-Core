# Communication Vision

The long-term communication idea is larger than "add chat to the assistant".

UNIX is intended to become a participant inside a communication/workspace client where people, projects, documents and AI context live together.

## Core idea

A conventional messenger usually treats AI as a side feature: open a bot, ask a question, copy the answer back into the real conversation.

The UNIX direction is different:

- a project has its own persistent space;
- people can share conversations and project context;
- UNIX participates in that same space;
- documents and decisions belong to the project rather than to one isolated prompt;
- context can survive across sessions without forcing users to repeat the entire project history;
- AI actions remain permission-controlled and auditable.

## Example shape

```text
Project: Product Launch

Members
- Alice
- Bob
- UNIX

Channels / threads
- General
- Development
- Design
- Launch plan

Project state
- documents
- tasks
- decisions
- searchable history
- project memory
- durable jobs
```

UNIX should be able to answer using the context it is legitimately allowed to see in that project and contribute like a participant, not like an unrelated chatbot pasted onto the side.

## Why Workspace comes first

The communication vision depends on foundations that are currently being built locally:

1. Workspace identity and isolation.
2. Project-specific memory and history.
3. Explicit document access.
4. Retrieval.
5. Durable jobs.
6. Permission ownership.
7. Eventually multi-user identity and access rules.

Without those boundaries, a shared AI client would have no reliable answer to basic questions such as "which project owns this memory?", "who is allowed to read this document?" or "which conversation created this task?".

## What this is not

This page is a direction, not a promise that the current Desktop should immediately become a full messenger. UNIX first needs a reliable personal/local runtime. The communication layer should reuse those foundations rather than forcing a premature rewrite.

The important idea to preserve is simple: **UNIX should eventually live inside the work, not outside it.**
