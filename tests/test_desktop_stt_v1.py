from pathlib import Path


def _sources() -> tuple[str, str, str, str]:
    root = Path(__file__).resolve().parents[1]
    desktop = root / "desktop" / "BabyAI.Desktop"
    return (
        (desktop / "BabyAI.Desktop.csproj").read_text(encoding="utf-8"),
        (desktop / "SpeechToText.cs").read_text(encoding="utf-8"),
        (desktop / "MainWindow.Voice.cs").read_text(encoding="utf-8"),
        (root / ".github" / "workflows" / "windows-release.yml").read_text(encoding="utf-8"),
    )


def test_stt_is_replaceable_and_local_first() -> None:
    project, stt, _, workflow = _sources()
    assert '<PackageReference Include="org.k2fsa.sherpa.onnx" Version="1.13.5" />' in project
    assert "interface ISpeechToTextProvider" in stt
    assert "class SherpaOnnxWhisperSpeechToTextProvider" in stt
    assert 'DefaultModelName = "sherpa-onnx-whisper-tiny"' in stt
    assert "BABYAI_STT_MODEL_DIR" in stt
    assert 'modelPrefix: "tiny"' in stt
    assert 'config.ModelConfig.Whisper.Language = "ru"' in stt
    assert "github.com/k2-fsa/sherpa-onnx/releases/download/asr-models" in workflow
    assert "BABYAI_STT_MODEL_SHA256" in workflow


def test_detected_utterance_stays_in_memory_and_is_bounded() -> None:
    _, _, voice, _ = _sources()
    assert "MemoryStream? _utterance" in voice
    assert "PreRollMilliseconds = 300" in voice
    assert "MaxUtteranceMilliseconds = 18_000" in voice
    assert "MicrophoneUtteranceEventArgs" in voice
    assert "WaveFileWriter" not in voice
    assert "File.WriteAllBytes" not in voice
    assert "File.OpenWrite" not in voice


def test_stt_output_reuses_normal_chat_send_path() -> None:
    _, _, voice, _ = _sources()
    assert 'ReplyText.Text = "Распознаю речь · Tiny → Base…"' in voice
    assert "SpeechToTextAbComparison.RunAsync" in voice
    assert "var transcript = comparison.Tiny.Transcript" in voice
    assert "MessageBox.Text = transcript" in voice
    assert "SendButton_Click(VoiceButton, new RoutedEventArgs())" in voice
    assert "only the Tiny transcript is submitted through the normal Send handler" in voice


def test_stt_ab_compares_the_same_pcm_without_persisting_transcripts() -> None:
    project, stt, voice, _ = _sources()
    root = Path(__file__).resolve().parents[1]
    helper = (root / "scripts" / "windows" / "prepare-stt-ab-base.ps1").read_text(encoding="utf-8")
    denoise = (root / "desktop" / "BabyAI.Desktop" / "SpeechNoiseSuppression.cs").read_text(encoding="utf-8")

    assert 'ComparisonModelName = "sherpa-onnx-whisper-base"' in stt
    assert "BABYAI_STT_AB_BASE_MODEL_DIR" in stt
    assert "SpeechDenoiseAbComparison.RunAsync" in stt
    assert "SpeechSignalMetrics.FromPcm16(pcm16Mono, sampleRate)" in denoise
    assert "tiny.TranscribeAsync(pcm16Mono, sampleRate, cancellationToken)" in denoise
    assert "@base.TranscribeAsync(pcm16Mono, sampleRate, cancellationToken)" in denoise
    assert "RmsDbfs" in stt
    assert "PeakDbfs" in stt
    assert "ClippingPercent" in stt
    assert "transcripts_logged=false; persistence=none" in voice
    assert "comparison.Tiny.Transcript" in voice
    assert "comparison.Base.Transcript" in voice
    assert "BundleExperimentalSttBaseAfterPublish" in project
    assert "prepare-stt-ab-base.ps1" in project
    assert 'modelName = "sherpa-onnx-whisper-base"' in helper
    assert "207557382L" in helper
    assert "base-encoder.int8.onnx" in helper
    assert "base-decoder.int8.onnx" in helper
    assert "base-tokens.txt" in helper
    assert "File.WriteAllBytes" not in stt + denoise
    assert "WaveFileWriter" not in stt + denoise


def test_stt_failure_is_visible_and_does_not_fake_a_transcript() -> None:
    _, stt, voice, _ = _sources()
    assert "DirectoryNotFoundException" in stt
    assert "FileNotFoundException" in stt
    assert "Локальная STT-модель не найдена" in stt
    assert "Локальная STT-модель неполная" in stt
    assert 'CoreStatusText.Text = "Core: STT недоступен"' in voice
    assert "Tiny не разобрал фразу" in voice
    assert "StartupDiagnostics.Log(\"Local STT unavailable\"" in voice


def test_release_bundle_contains_pinned_shipping_stt_model() -> None:
    root = Path(__file__).resolve().parents[1]
    verify = (root / "scripts" / "windows" / "verify-release-bundle.ps1").read_text(
        encoding="utf-8"
    )
    _, _, _, workflow = _sources()
    assert "c46116994e539aa165266d96b325252728429c12535eb9d8b6a2b10f129e66b1" in workflow
    assert 'BABYAI_STT_MODEL_SIZE: "116204861"' in workflow
    assert "tar.exe -xjf" in workflow
    assert "app/stt/sherpa-onnx-whisper-tiny/tiny-encoder.int8.onnx" in verify
    assert "app/stt/sherpa-onnx-whisper-tiny/tiny-decoder.int8.onnx" in verify
    assert "app/stt/sherpa-onnx-whisper-tiny/tiny-tokens.txt" in verify
    assert "sherpa-onnx-c-api.dll" in verify
    assert "onnxruntime.dll" in verify
