using System.Buffers.Binary;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using NAudio.Wave;

namespace BabyAI.Desktop;

public sealed partial class MainWindow
{
    private MicrophoneVadService? _voiceCapture;
    private bool _voiceListening;

    private void VoiceButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy)
            return;

        if (_voiceListening)
        {
            StopVoiceListening(updateUi: true, "Микрофон выключен.");
            return;
        }

        StartVoiceListening();
    }

    private void StartVoiceListening()
    {
        if (_voiceListening || _busy)
            return;

        if (ApprovalCard.Visibility == Visibility.Visible)
        {
            ReplyText.Text = "Сначала решите текущий запрос разрешения, затем включите микрофон.";
            return;
        }

        var capture = new MicrophoneVadService();
        capture.SpeechStarted += VoiceCapture_SpeechStarted;
        capture.SpeechEnded += VoiceCapture_SpeechEnded;
        capture.TimedOut += VoiceCapture_TimedOut;
        capture.Faulted += VoiceCapture_Faulted;

        try
        {
            capture.Start();
        }
        catch (Exception ex)
        {
            capture.Dispose();
            ShowVoiceCaptureError(ex);
            return;
        }

        _voiceCapture = capture;
        _voiceListening = true;
        AttachVoiceStopHooks();
        VoiceButton.Content = "■";
        ApplyState(OrbState.Listening);
        CoreStatusText.Text = "Core: слушаю";
        ReplyText.Text = "Слушаю · скажите что-нибудь…";
        StartupDiagnostics.Log("Voice VAD started: pcm16=16000Hz; channels=1; persistence=none");
    }

    private void StopVoiceListening(bool updateUi, string? statusMessage = null)
    {
        var capture = _voiceCapture;
        _voiceCapture = null;
        _voiceListening = false;
        DetachVoiceStopHooks();

        if (capture is not null)
        {
            capture.SpeechStarted -= VoiceCapture_SpeechStarted;
            capture.SpeechEnded -= VoiceCapture_SpeechEnded;
            capture.TimedOut -= VoiceCapture_TimedOut;
            capture.Faulted -= VoiceCapture_Faulted;
            capture.Dispose();
            StartupDiagnostics.Log("Voice VAD stopped");
        }

        VoiceButton.Content = "🎙";

        if (!updateUi)
            return;

        CoreStatusText.Text = "Core: подключён";
        ReplyText.Text = statusMessage ?? "Микрофон выключен.";
        ApplyState(OrbState.Idle);
    }

    private void VoiceCapture_SpeechStarted(object? sender, EventArgs e)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (!_voiceListening || !ReferenceEquals(_voiceCapture, sender))
                return;

            ReplyText.Text = "Слышу речь…";
            ApplyState(OrbState.Listening);
            StartupDiagnostics.Log("Voice VAD transition: speech_started");
        });
    }

    private void VoiceCapture_SpeechEnded(object? sender, EventArgs e)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (!_voiceListening || !ReferenceEquals(_voiceCapture, sender))
                return;

            StartupDiagnostics.Log("Voice VAD transition: speech_ended");
            StopVoiceListening(updateUi: false);
            CoreStatusText.Text = "Core: подключён";
            ReplyText.Text = "Фраза закончилась · VAD сработал.";
            ApplyState(OrbState.Done);
        });
    }

    private void VoiceCapture_TimedOut(object? sender, EventArgs e)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (!_voiceListening || !ReferenceEquals(_voiceCapture, sender))
                return;

            StopVoiceListening(updateUi: true, "Речь не обнаружена · микрофон выключен.");
        });
    }

    private void VoiceCapture_Faulted(object? sender, MicrophoneCaptureFaultedEventArgs e)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (!_voiceListening || !ReferenceEquals(_voiceCapture, sender))
                return;

            StopVoiceListening(updateUi: false);
            ShowVoiceCaptureError(e.Exception);
        });
    }

    private void ShowVoiceCaptureError(Exception exception)
    {
        var detail = exception.Message.Trim();
        StartupDiagnostics.Log("Voice VAD unavailable", exception);
        CoreStatusText.Text = "Core: микрофон недоступен";
        ReplyText.Text = string.IsNullOrWhiteSpace(detail)
            ? "Не удалось открыть микрофон. Проверьте доступ Windows к микрофону."
            : $"Микрофон недоступен: {detail}";
        ApplyState(OrbState.Error);
    }

    private void AttachVoiceStopHooks()
    {
        SendButton.Click += StopVoiceForOtherAction_Click;
        OrbButton.Click += StopVoiceForOtherAction_Click;
        AppWindow.Closing += StopVoiceOnWindowClosing;
    }

    private void DetachVoiceStopHooks()
    {
        SendButton.Click -= StopVoiceForOtherAction_Click;
        OrbButton.Click -= StopVoiceForOtherAction_Click;
        AppWindow.Closing -= StopVoiceOnWindowClosing;
    }

    private void StopVoiceForOtherAction_Click(object sender, RoutedEventArgs e)
    {
        if (_voiceListening)
            StopVoiceListening(updateUi: false);
    }

    private void StopVoiceOnWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_voiceListening)
            StopVoiceListening(updateUi: false);
    }
}

internal enum VoiceActivityTransition
{
    None,
    SpeechStarted,
    SpeechEnded,
}

internal sealed class VoiceActivityDetector
{
    private const double InitialNoiseFloor = 0.006;
    private const double MinimumStartThreshold = 0.018;
    private const double MinimumEndThreshold = 0.010;
    private const int SpeechStartMilliseconds = 120;
    private const int SpeechEndMilliseconds = 650;

    private readonly int _sampleRate;
    private double _noiseFloor = InitialNoiseFloor;
    private int _activeMilliseconds;
    private int _silentMilliseconds;

    public VoiceActivityDetector(int sampleRate = 16_000)
    {
        if (sampleRate <= 0)
            throw new ArgumentOutOfRangeException(nameof(sampleRate));
        _sampleRate = sampleRate;
    }

    public bool IsSpeech { get; private set; }

    public void Reset()
    {
        _noiseFloor = InitialNoiseFloor;
        _activeMilliseconds = 0;
        _silentMilliseconds = 0;
        IsSpeech = false;
    }

    public VoiceActivityTransition ProcessPcm16(ReadOnlySpan<byte> buffer)
    {
        var sampleCount = buffer.Length / sizeof(short);
        if (sampleCount == 0)
            return VoiceActivityTransition.None;

        double sumSquares = 0;
        for (var offset = 0; offset + 1 < buffer.Length; offset += sizeof(short))
        {
            var sample = BinaryPrimitives.ReadInt16LittleEndian(buffer.Slice(offset, sizeof(short)));
            var normalized = sample / 32768d;
            sumSquares += normalized * normalized;
        }

        var rms = Math.Sqrt(sumSquares / sampleCount);
        var frameMilliseconds = Math.Max(1, (int)Math.Round(sampleCount * 1000d / _sampleRate));

        if (!IsSpeech && rms < 0.08)
        {
            _noiseFloor = Math.Clamp((_noiseFloor * 0.95) + (rms * 0.05), 0.002, 0.03);
        }

        var startThreshold = Math.Max(MinimumStartThreshold, _noiseFloor * 3.2);
        var endThreshold = Math.Max(MinimumEndThreshold, _noiseFloor * 1.8);

        if (!IsSpeech)
        {
            _activeMilliseconds = rms >= startThreshold
                ? _activeMilliseconds + frameMilliseconds
                : 0;

            if (_activeMilliseconds < SpeechStartMilliseconds)
                return VoiceActivityTransition.None;

            IsSpeech = true;
            _activeMilliseconds = 0;
            _silentMilliseconds = 0;
            return VoiceActivityTransition.SpeechStarted;
        }

        _silentMilliseconds = rms < endThreshold
            ? _silentMilliseconds + frameMilliseconds
            : 0;

        if (_silentMilliseconds < SpeechEndMilliseconds)
            return VoiceActivityTransition.None;

        IsSpeech = false;
        _silentMilliseconds = 0;
        _activeMilliseconds = 0;
        return VoiceActivityTransition.SpeechEnded;
    }
}

internal sealed class MicrophoneVadService : IDisposable
{
    private const int SampleRate = 16_000;
    private const int MaxListeningMilliseconds = 20_000;
    private readonly object _sync = new();
    private readonly VoiceActivityDetector _vad = new(SampleRate);
    private WaveInEvent? _capture;
    private Timer? _timeout;
    private bool _disposed;

    public event EventHandler? SpeechStarted;
    public event EventHandler? SpeechEnded;
    public event EventHandler? TimedOut;
    public event EventHandler<MicrophoneCaptureFaultedEventArgs>? Faulted;

    public void Start()
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_capture is not null)
                return;

            var capture = new WaveInEvent
            {
                DeviceNumber = 0,
                WaveFormat = new WaveFormat(SampleRate, 16, 1),
                BufferMilliseconds = 30,
                NumberOfBuffers = 3,
            };
            capture.DataAvailable += Capture_DataAvailable;
            capture.RecordingStopped += Capture_RecordingStopped;
            _vad.Reset();
            _capture = capture;

            try
            {
                capture.StartRecording();
                _timeout = new Timer(TimeoutElapsed, null, MaxListeningMilliseconds, Timeout.Infinite);
            }
            catch
            {
                _capture = null;
                capture.DataAvailable -= Capture_DataAvailable;
                capture.RecordingStopped -= Capture_RecordingStopped;
                capture.Dispose();
                throw;
            }
        }
    }

    public void Stop()
    {
        WaveInEvent? capture;
        Timer? timeout;
        lock (_sync)
        {
            capture = _capture;
            _capture = null;
            timeout = _timeout;
            _timeout = null;
        }

        timeout?.Dispose();
        if (capture is null)
            return;

        capture.DataAvailable -= Capture_DataAvailable;
        capture.RecordingStopped -= Capture_RecordingStopped;
        try
        {
            capture.StopRecording();
        }
        finally
        {
            capture.Dispose();
        }
    }

    private void Capture_DataAvailable(object? sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded <= 0)
            return;

        var transition = _vad.ProcessPcm16(e.Buffer.AsSpan(0, e.BytesRecorded));
        if (transition == VoiceActivityTransition.SpeechStarted)
            SpeechStarted?.Invoke(this, EventArgs.Empty);
        else if (transition == VoiceActivityTransition.SpeechEnded)
            SpeechEnded?.Invoke(this, EventArgs.Empty);
    }

    private void Capture_RecordingStopped(object? sender, StoppedEventArgs e)
    {
        WaveInEvent? capture = null;
        lock (_sync)
        {
            if (sender is WaveInEvent candidate && ReferenceEquals(_capture, candidate))
            {
                capture = candidate;
                _capture = null;
            }
        }

        if (capture is null)
            return;

        _timeout?.Dispose();
        _timeout = null;
        capture.DataAvailable -= Capture_DataAvailable;
        capture.RecordingStopped -= Capture_RecordingStopped;
        capture.Dispose();

        var exception = e.Exception ?? new InvalidOperationException("Microphone capture stopped unexpectedly.");
        Faulted?.Invoke(this, new MicrophoneCaptureFaultedEventArgs(exception));
    }

    private void TimeoutElapsed(object? state)
    {
        Stop();
        TimedOut?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
                return;
            _disposed = true;
        }
        Stop();
    }
}

internal sealed class MicrophoneCaptureFaultedEventArgs(Exception exception) : EventArgs
{
    public Exception Exception { get; } = exception;
}
