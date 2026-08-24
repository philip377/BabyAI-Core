# Assistant UX state contract

The Orb keeps a visible state instead of presenting every wait as the same spinner:

- `Idle`: ready for input.
- `Thinking`: the local model is preparing an answer.
- `Executing`: an explicitly approved local action is running.
- `Approval`: a capability prompt is waiting for allow-once or reject.
- `Done`: the last response or action completed.
- `Error`: Core, model, transport, or action failed.

The Desktop status copy is Russian, the approval card shows the concrete action prompt,
and elapsed time remains visible while thinking, checking, stopping, or executing. The
existing Stop control cancels both chat and approved actions. Because Desktop cancellation
terminates an unresponsive worker, a pending approval is consumed before execution starts;
worker restart cannot replay it.

## History and projects

Conversation history is off by default. The user can enable it in Settings, inspect it
through the local command surface, scope messages to the active task project, or clear all
or one project's history. Clearing history does not erase explicit preferences, facts, or
project memory. Short-term context remains bounded and process-local whether history is on
or off.

## First response feedback

The UI immediately enters `Thinking` and shows elapsed time while the native trace records
tokenize, prefill, first-token, throughput, and stop reason. The current bridge still returns
one completed response rather than rendering token fragments; true streaming is a separate
protocol change and is not simulated by fake partial text.
