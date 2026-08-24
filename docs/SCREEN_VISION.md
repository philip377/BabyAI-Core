# Screen vision safety boundary

Stage 4 adds the permission and observation boundary before adding autonomous vision.

## Implemented

1. `screen.capture` requires its own capability and one-shot approval. The prompt warns
   that the image may contain private data.
2. Capture uses Windows GDI directly and accepts only `active_window` or `primary_screen`.
   Dimensions are capped at 8K UHD. No continuous capture, webcam, microphone, or input
   monitoring exists.
3. BMP observations are stored locally under the BabyAI data directory. A bounded manifest
   keeps the latest 20 metadata records; users can list and delete observations through the
   Desktop command surface.
4. Every observation is marked `capture_only`. The current Qwen3-8B text model is not told
   that it analyzed pixels.
5. `vision.action.propose` can turn an existing observation into a new pending approval only
   for `application.open` or `window.activate`. It never executes the action. The ordinary
   one-shot approval performs one action and immediately revokes its temporary capability.

## Deliberately not implemented yet

Pixel understanding needs an explicitly selected local multimodal or OCR provider with its
own model/runtime/latency and privacy tests. BabyAI does not silently replace the user's fast
text model. Until that provider exists, the safe path is:

`capture with permission -> user or future analyzer inspects -> action is proposed -> user
approves or rejects -> one controlled action`

Full autonomous clicking, keyboard injection, background surveillance, and action execution
directly from pixels remain outside Milestone 2's first vision slice.
