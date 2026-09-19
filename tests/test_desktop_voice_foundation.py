from pathlib import Path


def _sources() -> tuple[str, str, str]:
    root = Path(__file__).resolve().parents[1]
    desktop = root / "desktop" / "BabyAI.Desktop"
    return (
        (desktop / "BabyAI.Desktop.csproj").read_text(encoding="utf-8"),
        (desktop / "MainWindow.xaml").read_text(encoding="utf-8"),
        (desktop / "MainWindow.Voice.cs").read_text(encoding="utf-8"),
    )


def test_voice_foundation_adds_microphone_control_and_listening_orb() -> None:
    _, xaml, voice = _sources()
    assert 'x:Name="VoiceButton"' in xaml
    assert 'Click="VoiceButton_Click"' in xaml
    assert 'AutomationProperties.Name="Голосовой ввод"' in xaml
    assert "ApplyState(OrbState.Listening)" in voice
    assert 'ReplyText.Text = "Слышу речь…"' in voice
    assert "SpeechEnded?.Invoke(this, completed)" in voice
    assert "MicrophoneUtteranceEventArgs" in voice


def test_voice_capture_is_bounded_and_memory_only() -> None:
    project, _, voice = _sources()
    assert '<PackageReference Include="NAudio" Version="2.2.1" />' in project
    assert "WaveInEvent" in voice
    assert "new WaveFormat(SampleRate, 16, 1)" in voice
    assert "MaxListeningMilliseconds = 20_000" in voice
    assert "MaxUtteranceMilliseconds = 18_000" in voice
    assert "PreRollMilliseconds = 300" in voice
    assert "BufferMilliseconds = 30" in voice
    assert 'persistence=none' in voice
    assert "WaveFileWriter" not in voice
    assert "File.WriteAllBytes" not in voice


def test_vad_requires_sustained_speech_and_silence_transitions() -> None:
    _, _, voice = _sources()
    assert "SpeechStartMilliseconds = 120" in voice
    assert "SpeechEndMilliseconds = 650" in voice
    assert "MinimumStartThreshold = 0.018" in voice
    assert "MinimumEndThreshold = 0.010" in voice
    assert "VoiceActivityTransition.SpeechStarted" in voice
    assert "VoiceActivityTransition.SpeechEnded" in voice
    assert "_noiseFloor * 3.2" in voice
    assert "_noiseFloor * 1.8" in voice


def test_microphone_stops_on_other_actions_timeout_and_window_close() -> None:
    _, _, voice = _sources()
    assert "SendButton.Click += StopVoiceForOtherAction_Click" in voice
    assert "OrbButton.Click += StopVoiceForOtherAction_Click" in voice
    assert "AppWindow.Closing += StopVoiceOnWindowClosing" in voice
    assert "TimeoutElapsed" in voice
    assert "StopVoiceListening(updateUi: false)" in voice


def test_voice_does_not_compete_with_pending_agent_approval() -> None:
    _, xaml, voice = _sources()
    assert 'x:Name="ApprovalCard"' in xaml
    assert "ApprovalCard.Visibility == Visibility.Visible" in voice
    assert "Сначала решите текущий запрос разрешения" in voice
