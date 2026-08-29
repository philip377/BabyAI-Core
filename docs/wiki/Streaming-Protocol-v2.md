# Streaming Protocol v2

Protocol v2 lets the Desktop show a response while it is being generated instead of treating every model turn as one blocking request/response.

## Event model

A protocol v2 chat turn uses ordered events owned by the worker:

```text
state: thinking
state: answering
delta: "..."
delta: "..."
...
done
```

Tool execution may introduce `state: executing`. Errors end with a terminal `error` event.

Each request has a sequence number starting at zero. The worker owns the envelope fields (`id`, `protocol`, `seq`) so command implementations cannot accidentally corrupt ordering.

## Exactly one terminal event

A v2 request ends with exactly one terminal event:

- `done` for success;
- `error` for failure.

No events are allowed after the terminal event. This gives the Desktop a deterministic lifecycle and makes cancellation/error recovery easier to reason about.

## Visible-text gate

Streaming raw model output directly into the UI would risk briefly exposing internal tool JSON or protocol text before the final parser rejects it.

The native streaming path therefore uses a visible marker contract and a `VisibleTextGate`. The gate authorizes the user-visible body before emitting deltas. If validation fails, the stream fails closed rather than showing internal content.

## Cancellation

The Desktop can stop an in-progress chat turn. Cancellation ownership belongs to the Desktop/worker lifecycle rather than being simulated by simply hiding output while the model continues indefinitely.

## Metrics

The terminal result carries useful measurements such as:

- visible TTFT;
- native first-token time;
- generation time;
- total time;
- generated tokens;
- delta count;
- model calls;
- stop reason.

These metrics are important because perceived latency and actual inference latency are not the same thing.

## Compatibility

Protocol v1 remains available for non-streaming/legacy command interactions. Protocol v2 is intentionally narrow and currently focused on chat streaming.

Canonical contract: `docs/STREAMING_PROTOCOL_V2.md`.
