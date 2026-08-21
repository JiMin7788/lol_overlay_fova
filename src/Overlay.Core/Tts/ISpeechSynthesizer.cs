namespace Overlay.Core.Tts;

/// <summary>
/// The seam between the testable <see cref="TtsScheduler"/> and the actual audio backend.
/// The production implementation (Overlay.Client) wraps the offline Windows SAPI
/// <c>System.Speech.Synthesis.SpeechSynthesizer</c>; tests use a fake that records calls,
/// so the whole scheduling heart (priority / interrupt / cooldown) is verifiable headlessly
/// with no audio device.
///
/// <para><b>Contract:</b> <see cref="Speak"/> is expected to be non-blocking (fire-and-forget
/// async playback) and to raise <see cref="SpeakCompleted"/> exactly once when — and only when
/// — a spoken request finishes <em>naturally</em>. <see cref="Stop"/> cancels the current
/// utterance and MUST NOT raise <see cref="SpeakCompleted"/>. This one-line contract is what
/// lets the scheduler drive the queue deterministically: a natural completion pumps the next
/// request, while an interrupt (Stop) is fully managed by the scheduler itself.</para>
/// </summary>
public interface ISpeechSynthesizer
{
    /// <summary>Begin speaking <paramref name="text"/> with the given voice (null = default
    /// voice). Non-blocking; completion is signalled via <see cref="SpeakCompleted"/>.</summary>
    void Speak(string text, string? voiceId);

    /// <summary>Cancel the current utterance immediately. Does NOT raise
    /// <see cref="SpeakCompleted"/> (see interface contract).</summary>
    void Stop();

    /// <summary>Raised once when a spoken request completes naturally (never as a result of
    /// <see cref="Stop"/>).</summary>
    event Action? SpeakCompleted;
}
