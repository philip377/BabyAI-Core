# Agent Loop v1

PRIMUS may ask the configured language model for one tool call, execute that call through the capability permission layer, and then make one final model pass using the tool result.

## Flow

1. Build prompt from identity + memory + tool catalog + user input.
2. If the model answers normally, return the answer with no tool execution.
3. If the model returns one valid JSON tool call, execute it through `AgentExecutor`.
4. Every tool still enforces its capability in Python at execution time.
5. Feed the result back to the model for a final answer.
6. Agent Loop v1 does not permit recursive tool calls.

## Tool-call format

```json
{"tool":"system.info","arguments":{}}
```

## Safety boundaries

- Deny-by-default permissions remain authoritative.
- Unknown tools are rejected.
- Prompt text cannot grant a capability.
- No shell execution, process control, filesystem write/delete, arbitrary network access, or remote device control exists in v1.
- One tool call maximum per user turn prevents uncontrolled loops while the protocol is still young.
