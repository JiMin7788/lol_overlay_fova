namespace Overlay.Core.Tts;

/// <summary>Spec Data Model: the priority of a <see cref="SpeechRequest"/>. A
/// <see cref="Critical"/> request (e.g. "Recall Unsafe") interrupts a currently-playing
/// <see cref="Low"/>/<see cref="Normal"/> request (spec Internal Logic #1); higher values
/// are also dequeued before lower ones.</summary>
public enum SpeechPriority
{
    Low = 0,
    Normal = 1,
    High = 2,
    Critical = 3,
}

/// <summary>Spec Data Model. One queued text-to-speech request.
/// <paramref name="CooldownKey"/> (nullable) enables spam suppression: repeated requests
/// with the same key inside the cooldown window are ignored (spec Internal Logic #2).</summary>
public sealed record SpeechRequest(
    string Id,
    string Text,
    SpeechPriority Priority,
    long Timestamp,
    string? CooldownKey);
