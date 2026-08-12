# Planner v1

Planner v1 adds a single bounded planning pass before the normal PRIMUS response path.

The planner may return only:

```json
{"intent":"short intent","action":"answer|tool"}
```

The intent is deliberately short and is not chain-of-thought. Extra fields are rejected.

## Boundaries

- one planning decision per user turn
- action is only `answer` or `tool`
- tool execution remains limited to Agent Loop v1's single-tool boundary
- planner cannot grant permissions
- planner cannot recurse or create multi-step plans
- invalid planner output falls back to the existing agent loop
- no new capabilities are introduced by Planner v1
