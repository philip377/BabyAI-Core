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
    assert 'Name => "sherpa-onnx-whisper-tiny"' in stt
    assert "BABYAI_STT_MODEL_DIR" in stt
    assert "sherpa-onnx-whisper-tiny" in stt
    assert "tiny-encoder.int8.onnx" in stt
    assert "tiny-decoder.int8.onnx" in stt
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
    assert 'ReplyText.Text = "Распознаю речь…"' in voice
    assert "_speechToText.TranscribeAsync" in voice
    assert "MessageBox.Text = transcript" in voice
    assert "SendButton_Click(VoiceButton, new RoutedEventArgs())" in voice
    assert "Keep voice and keyboard on exactly the same conversational path" in voice


def test_stt_failure_is_visible_and_does_not_fake_a_transcript() -> None:
    _, stt, voice, _ = _sources()
    assert "DirectoryNotFoundException" in stt
    assert "FileNotFoundException" in stt
    assert "Локальная STT-модель не найдена" in stt
    assert "Локальная STT-модель неполная" in stt
    assert 'CoreStatusText.Text = "Core: STT недоступен"' in voice
    assert "Не удалось разобрать фразу" in voice
    assert "StartupDiagnostics.Log(\"Local STT unavailable\"" in voice


def test_release_bundle_contains_pinned_stt_model() -> None:
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
