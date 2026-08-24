# Milestone 3 Companion / Workspace architecture plan

This is a plan only. It intentionally avoids a broad implementation in the Milestone 2 PR.

## 1. Workspace identity and storage

Introduce a workspace manifest with a stable ID, display name, explicit root directories, and
policy references. Project memory, history, documents, and task state use the workspace ID rather
than relying on names. Migration remains additive and reversible.

## 2. Projects and documents

Add document adapters behind separate read/create/edit capabilities. Every mutation produces a
preview or diff, identifies its exact target, and supports rollback where the format permits it.
Indexing is opt-in per workspace; raw document content is not copied into global memory.

## 3. Collaboration boundary

Represent outbound messages, shared files, comments, and remote edits as proposals. Each connector
has its own capability and account scope. External side effects require a final confirmation that
shows recipient, destination, and payload; local planning never implies permission to publish.

## 4. Persistent jobs

Use a durable job ledger with states `queued`, `running`, `waiting_for_approval`, `cancelled`,
`failed`, and `done`. Jobs checkpoint between capability calls, never retain one-shot grants, and
can be inspected or cancelled from the Orb. Recovery after restart must be idempotent.

## 5. Retrieval and context

Retrieve from current session, explicit global facts, active project memory, and selected workspace
documents as separate ranked sources. Show provenance and allow exclusion/deletion. Keep prompt
budgets measurable so retrieval cannot silently destroy first-token latency.

## 6. Vision and multimodal work

Add a provider interface that declares modalities, local model path, memory requirements, and data
retention. Connect the existing screen-observation IDs to OCR/caption results, then require a fresh
approval for any proposed action. Evaluate on a fixed diagnostic set before enabling UI actions.

## 7. Delivery sequence

1. Workspace IDs and migrations.
2. Read-only project/document browser.
3. Previewed document creation/editing.
4. Provenance-aware retrieval.
5. Durable cancellable jobs.
6. Connector proposals and collaboration.
7. Explicit multimodal provider and controlled screen actions.

Each slice should ship as a small draft PR with its own schema migration, permission review,
Windows tests, rollback story, and latency/memory comparison.
