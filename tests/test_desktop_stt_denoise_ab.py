from pathlib import Path


def _read(path: str) -> str:
    root = Path(__file__).resolve().parents[1]
    return (root / path).read_text(encoding="utf-8")


def test_webrtc_denoise_ab_is_in_memory_and_single_factor() -> None:
    project = _read("desktop/BabyAI.Desktop/BabyAI.Desktop.csproj")
    denoise = _read("desktop/BabyAI.Desktop/SpeechNoiseSuppression.cs")
    stt = _read("desktop/BabyAI.Desktop/SpeechToText.cs")

    assert '<PackageReference Include="SoundFlow.Extensions.WebRtc.Apm" Version="1.4.0" />' in project
    assert "new NoiseSuppressor(" in denoise
    assert "NoiseSuppressionLevel.High" in denoise
    assert "Channels = 1" in denoise
    assert "sampleRate != 16_000" in denoise
    assert "SpeechDenoiseAbComparison.RunAsync" in stt
    assert "RAW ·" in stt
    assert "CLEAN ·" in stt
    assert "Noise floor≈" in stt
    assert "SNR≈" in stt

    combined = denoise + stt
    assert "WaveFileWriter" not in combined
    assert "File.WriteAllBytes" not in combined
    assert "File.OpenWrite" not in combined
    assert "FileStream" not in denoise


def test_webrtc_denoise_keeps_existing_capture_and_chat_path_untouched() -> None:
    voice = _read("desktop/BabyAI.Desktop/MainWindow.Voice.cs")

    assert "DeviceNumber = 0" in voice
    assert "WaveFormat = new WaveFormat(SampleRate, 16, 1)" in voice
    assert "PreRollMilliseconds = 300" in voice
    assert "SpeechEndMilliseconds = 650" in voice
    assert "SendButton_Click(VoiceButton, new RoutedEventArgs())" in voice
