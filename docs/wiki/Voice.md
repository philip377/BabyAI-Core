# Voice

Voice is being added as a pipeline, not as one monolithic "voice mode" feature.

## Stage 1 — microphone + VAD

The first layer captures microphone audio in memory and runs voice activity detection (VAD). Its job is only to answer questions such as:

- did speech begin?
- did speech end?
- did the user remain silent until timeout?
- was the microphone released correctly?

Raw audio is not persisted by this foundation and is not sent to the main language model.

The Desktop exposes a listening state through the existing ORB/assistant state model.

## Stage 2 — streaming STT

After microphone/VAD behavior is verified on real hardware, local speech-to-text can be added.

The STT backend should be selected by measurement. It must coexist with the main Qwen3-8B runtime without causing unacceptable VRAM pressure or latency. Partial transcripts are preferred over waiting for a whole recorded utterance.

## Stage 3 — streaming TTS

Text-to-speech should begin from complete enough phrases/sentences while the LLM is still generating later text. The goal is to avoid the pattern:

```text
wait for full LLM answer → synthesize full answer → finally speak
```

Instead, UNIX should eventually form a streaming chain:

```text
speech → VAD → partial STT → LLM stream → phrase buffer → TTS stream → speakers
```

## Stage 4 — barge-in

Natural voice interaction requires interruption. If the user starts speaking while UNIX is talking, the system should stop TTS and the current generation path safely, then return to listening.

Barge-in therefore depends on reliable ownership of:

- microphone capture;
- VAD state;
- generation cancellation;
- TTS playback cancellation;
- turn state in the Desktop.

## Hardware verification matters

A green CI build cannot prove that a real microphone opens, selects the correct device, responds to room noise correctly and releases the Windows audio device after cancellation. Voice stages therefore include explicit owner hardware smoke tests before the next layer is considered stable.
