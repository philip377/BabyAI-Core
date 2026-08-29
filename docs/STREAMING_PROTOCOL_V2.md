# Versioned Desktop streaming protocol v2

This protocol is the bounded follow-up to Milestone 2. It streams display-safe
assistant text from the existing local model through the resident worker to WinUI.
It does not change the selected model, native ABI, permission boundary, memory model,
or tool execution semantics.

## Compatibility

Desktop protocol v1 remains the default. Requests without a `protocol` field receive
the existing single JSONL response.

Streaming is requested explicitly:

```json
{"protocol":2,"id":42,"command":"chat","payload":{"message":"Привет"}}
```

An older worker may ignore the new field and return one valid v1 response. A v2 client
accepts that one response as a completed fallback and must never resend the chat request.

## Events

Every v2 line contains the same request `id`, `protocol: 2`, and a contiguous `seq`
starting at zero. A request produces exactly one terminal `done` or `error` event unless
the worker process is deliberately terminated for cancellation.

```json
{"protocol":2,"id":42,"seq":0,"event":"state","state":"thinking"}
{"protocol":2,"id":42,"seq":1,"event":"state","state":"answering"}
{"protocol":2,"id":42,"seq":2,"event":"delta","text":"Привет"}
{"protocol":2,"id":42,"seq":3,"event":"delta","text":"!"}
{"protocol":2,"id":42,"seq":4,"event":"done","ok":true,"command":"chat","reply":"Привет!","metrics":{"visible_ttft_ms":861,"native_first_token_ms":43,"generation_ms":1190,"total_ms":1260,"generated_tokens":49,"delta_count":2,"model_calls":1,"stop_reason":"eog"}}
```

Delta text is non-empty, additive, and contains only complete Unicode characters.
`done.reply` is the canonical PRIMUS result; the client replaces its provisional text
with that value before committing the conversation turn.

An error is terminal and contains no traceback, raw model output, tool arguments, or
private file data:

```json
{"protocol":2,"id":42,"seq":2,"event":"error","ok":false,"error":"Local brain unavailable."}
```

The JSONL wire representation remains ASCII-safe so Windows pipe code pages cannot
truncate a Unicode reply.

## Visibility and tool safety

Native token pieces are private candidates, not UI text. An answer-only generation gets
an unpredictable per-turn visible marker. The gate remains closed until the model emits
that exact marker after any optional hidden-thinking block; the marker itself never
crosses the worker boundary. If the model omits or corrupts it, BabyAI buffers the whole
generation and uses the completed-response path.

Before a delta can cross the worker boundary, BabyAI must:

1. hold incomplete UTF-8 and any suffix that could become a stop sequence;
2. suppress thinking/reasoning blocks, internal role delimiters, JSON wrappers, fenced
   JSON, tool calls, tool results, repair drafts, and unsolicited translation tails;
3. classify the generation as an answer-only path with the tool catalog excluded;
4. observe the exact per-turn visible marker; and
5. pass the remaining monotonic text through the PRIMUS visibility gate.

Once the marker opens the stream, that generation is authoritatively an answer and can
never be reinterpreted as a tool call. A later hidden, JSON, fence, tool, protocol, or
canonical-mismatch marker fails closed with a terminal error. No executor is entered and
the suspicious fragment is not emitted.

Potential local-action requests retain the completed-response path. Approval prompts
and deterministic replies may arrive as one safe delta, but a success message is emitted
only after the executor returns successfully. An executor error terminates the request
without any success delta.

## States and cancellation

The streamed states are `thinking`, `answering`, and `executing`; approval, done, and
error remain terminal UI conditions. `executing` begins only when an already permitted
executor call actually starts. The existing Allow-once command remains in the approval
state while its v1 request runs, and reports success only after the executor returns.
Approving a lesson and rejecting an approval never enter `executing`.

The bounded v2 implementation keeps the existing hard interruption model. Stop first
invalidates the active UI generation, then cancels the bridge read and terminates the
resident worker process tree. Buffered or late events from that generation are ignored.
The next request starts a clean worker. This avoids a second stdin reader, request
multiplexing, and a wider worker-lifecycle change in the first streaming PR.

WinUI keeps any already displayed, marker-authorized partial answer and appends one
"stopped" system turn. It never accepts late deltas from the cancelled generation.

## Metrics

- `visible_ttft_ms`: worker request start to first display-safe delta;
- `native_first_token_ms`: native generation loop to first sampled token;
- `generation_ms`: native generation-loop duration;
- `total_ms`: worker request start to terminal event;
- `generated_tokens`: native generated token count when available;
- `delta_count`: emitted display-safe deltas;
- `model_calls`: model generations used for the final response;
- `stop_reason`: native stop reason when available.

WinUI separately records end-to-end TTFT from request write to the first accepted delta.
Raw prompts and response text are not written to performance logs.

## Non-goals

- voice, VAD, STT, TTS, or barge-in;
- OCR or a multimodal model;
- Workspace, retrieval, or durable jobs;
- parallel worker requests or a `chat.cancel` command;
- changes to capability names, permission persistence, or executor allowlists.
