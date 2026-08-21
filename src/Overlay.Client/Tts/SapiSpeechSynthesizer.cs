using System.Speech.Synthesis;
using Overlay.Core.Tts;

namespace Overlay.Client.Tts;

/// <summary>
/// Production <see cref="ISpeechSynthesizer"/> backed by the <b>offline</b> Windows SAPI
/// synthesizer (<c>System.Speech.Synthesis.SpeechSynthesizer</c>). This is the OS built-in
/// text-to-speech engine — <b>no network call and no cloud API</b>, so the spec's
/// cloud-consent / offline-fallback branch (Internal Logic #3) never applies. Voice packs are
/// the SAPI installed voices (<see cref="SpeechSynthesizer.GetInstalledVoices()"/> /
/// <see cref="SpeechSynthesizer.SelectVoice(string)"/>).
///
/// <para>Thin, code-reviewable layer: all priority/queue/cooldown/interrupt logic lives in the
/// headlessly-tested <see cref="TtsScheduler"/>. This class only maps the seam onto SAPI:
/// <c>Speak</c> → <see cref="SpeechSynthesizer.SpeakAsync(string)"/>, <c>Stop</c> →
/// <see cref="SpeechSynthesizer.SpeakAsyncCancelAll"/>, voice pack → <c>SelectVoice</c>.</para>
///
/// <para><b>Seam contract.</b> <see cref="SpeakCompleted"/> is raised only on a natural finish
/// (<c>SpeakCompletedEventArgs.Cancelled == false</c>); a <see cref="Stop"/>-induced cancel is
/// swallowed, exactly as <see cref="ISpeechSynthesizer"/> requires so the scheduler can manage
/// interrupts itself.</para>
/// </summary>
public sealed class SapiSpeechSynthesizer : ISpeechSynthesizer, IDisposable
{
    private readonly SpeechSynthesizer _synth = new();

    public event Action? SpeakCompleted;

    /// <param name="volume">0.0–1.0 (M14 <c>voice.ttsVolume</c>, default 0.8). Mapped to SAPI's
    /// 0–100 volume scale.</param>
    public SapiSpeechSynthesizer(double volume = 0.8)
    {
        _synth.Volume = (int)Math.Round(Math.Clamp(volume, 0.0, 1.0) * 100);
        _synth.SpeakCompleted += OnSapiCompleted;
    }

    public void Speak(string text, string? voiceId)
    {
        if (!string.IsNullOrEmpty(voiceId))
        {
            try { _synth.SelectVoice(voiceId); }
            catch (ArgumentException) { /* unknown voice id → keep current voice */ }
        }
        _synth.SpeakAsync(text);
    }

    public void Stop() => _synth.SpeakAsyncCancelAll();

    private void OnSapiCompleted(object? sender, SpeakCompletedEventArgs e)
    {
        // Honour the seam contract: only a natural finish pumps the scheduler's queue.
        if (!e.Cancelled) SpeakCompleted?.Invoke();
    }

    /// <summary>The installed SAPI voice names available as voice packs (for a settings UI).</summary>
    public IReadOnlyList<string> InstalledVoiceNames()
        => _synth.GetInstalledVoices()
            .Where(v => v.Enabled)
            .Select(v => v.VoiceInfo.Name)
            .ToList();

    public void Dispose()
    {
        _synth.SpeakCompleted -= OnSapiCompleted;
        _synth.Dispose();
    }
}
