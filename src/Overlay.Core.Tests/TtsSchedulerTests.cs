using Overlay.Core.EventBus;
using Overlay.Core.Overlay;
using Overlay.Core.Tts;

namespace Overlay.Core.Tests;

/// <summary>
/// Executable proof for the testable heart of M09 TTS Engine
/// (docs/modules/M09_TTS_ENGINE.md) — <see cref="TtsScheduler"/>. Covers the two headlessly
/// verifiable Acceptance Criteria / Reviewer Checklist items:
///  - Priority-interrupt: a CRITICAL request interrupts a currently-playing NORMAL/LOW
///    utterance (the fake synthesizer records a Stop() immediately followed by the CRITICAL
///    Speak()), and the interrupt rule is honoured for every priority pair.
///  - Cooldown de-dup: a repeated cooldownKey inside the window is ignored and allowed again
///    once the window elapses — proven deterministically with a virtual <see cref="IClock"/>,
///    no real sleep.
/// Plus priority ordering, SetVoicePack, Stop(), and VOICE.* event routing.
///
/// The audible/voice-pack-switching demo needs installed SAPI voices on a real machine and is
/// deferred to a human run (see M09 Agent Report); it is NOT fabricated here.
/// </summary>
public class TtsSchedulerTests : IDisposable
{
    public TtsSchedulerTests() => EventBus.EventBus.ResetForTests();

    public void Dispose() => EventBus.EventBus.ResetForTests();

    // ── Test doubles ────────────────────────────────────────────────────────────

    private sealed class FakeClock : IClock
    {
        public long NowMs { get; set; }
    }

    /// <summary>Records every seam call in order and lets a test simulate a natural completion.</summary>
    private sealed class FakeSynth : ISpeechSynthesizer
    {
        public List<string> Ops { get; } = new();                     // "speak:<text>" | "stop"
        public List<(string Text, string? Voice)> Spoken { get; } = new();
        public int Stops { get; private set; }

        public event Action? SpeakCompleted;

        public void Speak(string text, string? voiceId)
        {
            Ops.Add("speak:" + text);
            Spoken.Add((text, voiceId));
        }

        public void Stop()
        {
            Ops.Add("stop");
            Stops++;
        }

        /// <summary>Simulate the currently-playing utterance finishing naturally.</summary>
        public void Complete() => SpeakCompleted?.Invoke();
    }

    // ── Priority ordering ────────────────────────────────────────────────────────

    [Fact]
    public void HigherPriority_IsDequeuedBeforeLower()
    {
        var synth = new FakeSynth();
        using var tts = new TtsScheduler(synth);

        tts.Speak("a", SpeechPriority.Normal);   // idle → starts immediately
        tts.Speak("b", SpeechPriority.Low);      // queued
        tts.Speak("c", SpeechPriority.High);     // queued
        tts.Speak("d", SpeechPriority.Normal);   // queued

        synth.Complete(); // a done → highest pending = c (High)
        synth.Complete(); // c done → highest pending = d (Normal, before Low b)
        synth.Complete(); // d done → b (Low)

        Assert.Equal(new[] { "a", "c", "d", "b" }, synth.Spoken.Select(s => s.Text));
    }

    [Fact]
    public void FirstRequest_StartsImmediately_WhenIdle()
    {
        var synth = new FakeSynth();
        using var tts = new TtsScheduler(synth);

        tts.Speak("hello", SpeechPriority.Normal);

        Assert.Equal("hello", Assert.Single(synth.Spoken).Text);
        Assert.Equal("hello", tts.Current!.Text);
        Assert.Equal(0, tts.PendingCount);
    }

    // ── CRITICAL interrupt (Acceptance Criterion / Reviewer Checklist #1) ────────

    [Theory]
    [InlineData(SpeechPriority.Normal)]
    [InlineData(SpeechPriority.Low)]
    public void Critical_Interrupts_PlayingNormalOrLow_StopThenCriticalSpeak(SpeechPriority playing)
    {
        var synth = new FakeSynth();
        using var tts = new TtsScheduler(synth);

        tts.Speak("normal", playing);            // starts
        tts.Speak("UNSAFE", SpeechPriority.Critical);

        // Fake records a Stop() then the CRITICAL Speak(), in that order.
        Assert.Equal(new[] { "speak:normal", "stop", "speak:UNSAFE" }, synth.Ops);
        Assert.Equal(1, synth.Stops);
        Assert.Equal("UNSAFE", tts.Current!.Text);
        Assert.Equal(0, tts.PendingCount); // interrupted utterance is discarded, not replayed
    }

    [Fact]
    public void High_DoesNotInterrupt_PlayingNormal()
    {
        var synth = new FakeSynth();
        using var tts = new TtsScheduler(synth);

        tts.Speak("normal", SpeechPriority.Normal); // starts
        tts.Speak("high", SpeechPriority.High);     // must NOT interrupt

        Assert.Equal(new[] { "speak:normal" }, synth.Ops);
        Assert.Equal(0, synth.Stops);
        Assert.Equal("normal", tts.Current!.Text);
        Assert.Equal(1, tts.PendingCount);

        synth.Complete();                            // now high plays
        Assert.Equal("high", tts.Current!.Text);
    }

    [Theory]
    [InlineData(SpeechPriority.High)]
    [InlineData(SpeechPriority.Critical)]
    public void Critical_DoesNotInterrupt_PlayingHighOrCritical(SpeechPriority playing)
    {
        var synth = new FakeSynth();
        using var tts = new TtsScheduler(synth);

        tts.Speak("playing", playing);              // starts
        tts.Speak("crit", SpeechPriority.Critical); // no interrupt against High/Critical

        Assert.Equal(0, synth.Stops);
        Assert.Equal("playing", tts.Current!.Text);
        Assert.Equal(1, tts.PendingCount);
    }

    [Theory]
    [InlineData(SpeechPriority.Critical, SpeechPriority.Low, true)]
    [InlineData(SpeechPriority.Critical, SpeechPriority.Normal, true)]
    [InlineData(SpeechPriority.Critical, SpeechPriority.High, false)]
    [InlineData(SpeechPriority.Critical, SpeechPriority.Critical, false)]
    [InlineData(SpeechPriority.High, SpeechPriority.Normal, false)]
    [InlineData(SpeechPriority.Normal, SpeechPriority.Low, false)]
    public void ShouldInterrupt_MatchesSpecRule(SpeechPriority incoming, SpeechPriority current, bool expected)
        => Assert.Equal(expected, TtsScheduler.ShouldInterrupt(incoming, current));

    // ── Cooldown de-dup (Acceptance Criterion / Reviewer Checklist #2) ───────────

    [Fact]
    public void SameCooldownKey_WithinWindow_IsIgnored_ThenAllowedAfterWindow()
    {
        var synth = new FakeSynth();
        var clock = new FakeClock { NowMs = 0 };
        using var tts = new TtsScheduler(synth, clock, cooldownMs: 5000);

        tts.Speak("ping", SpeechPriority.Normal, cooldownKey: "recall"); // accepted @0 → starts
        synth.Complete();

        clock.NowMs = 4999;
        tts.Speak("ping", SpeechPriority.Normal, cooldownKey: "recall"); // within window → ignored

        Assert.Single(synth.Spoken);
        Assert.Equal(0, tts.PendingCount);

        clock.NowMs = 5000;                                              // window elapsed (boundary)
        tts.Speak("ping", SpeechPriority.Normal, cooldownKey: "recall"); // accepted again → starts

        Assert.Equal(2, synth.Spoken.Count);
    }

    [Fact]
    public void NullCooldownKey_IsNeverDeduped()
    {
        var synth = new FakeSynth();
        var clock = new FakeClock { NowMs = 0 };
        using var tts = new TtsScheduler(synth, clock, cooldownMs: 5000);

        tts.Speak("repeat", SpeechPriority.Normal); // null key
        synth.Complete();
        tts.Speak("repeat", SpeechPriority.Normal); // same text, still spoken (no dedup)

        Assert.Equal(2, synth.Spoken.Count);
    }

    [Fact]
    public void DifferentCooldownKeys_DoNotSuppressEachOther()
    {
        var synth = new FakeSynth();
        var clock = new FakeClock { NowMs = 0 };
        using var tts = new TtsScheduler(synth, clock, cooldownMs: 5000);

        tts.Speak("a", SpeechPriority.Normal, cooldownKey: "k1"); // starts
        tts.Speak("b", SpeechPriority.Normal, cooldownKey: "k2"); // different key → queued, not suppressed

        Assert.Equal(1, tts.PendingCount);
        synth.Complete();
        Assert.Equal(new[] { "a", "b" }, synth.Spoken.Select(s => s.Text));
    }

    // ── Voice pack ───────────────────────────────────────────────────────────────

    [Fact]
    public void SetVoicePack_ChangesVoiceIdPassedToSynth()
    {
        var synth = new FakeSynth();
        using var tts = new TtsScheduler(synth);

        tts.SetVoicePack("voice-A");
        tts.Speak("one", SpeechPriority.Normal);
        synth.Complete();

        tts.SetVoicePack("voice-B");
        tts.Speak("two", SpeechPriority.Normal);

        Assert.Equal("voice-A", synth.Spoken[0].Voice);
        Assert.Equal("voice-B", synth.Spoken[1].Voice);
    }

    // ── Stop ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void Stop_HaltsCurrent_AndClearsQueue_NoReplay()
    {
        var synth = new FakeSynth();
        using var tts = new TtsScheduler(synth);

        tts.Speak("a", SpeechPriority.Normal); // starts
        tts.Speak("b", SpeechPriority.Normal); // queued
        tts.Speak("c", SpeechPriority.Normal); // queued

        tts.Stop();

        Assert.Equal(1, synth.Stops);
        Assert.Null(tts.Current);
        Assert.Equal(0, tts.PendingCount);

        synth.Complete();                       // stray completion pumps nothing
        Assert.Equal(new[] { "a" }, synth.Spoken.Select(s => s.Text));
    }

    // ── VOICE.* event routing (M15) ──────────────────────────────────────────────

    [Fact]
    public void VoiceEvent_WithSpeechRequestPayload_RoutesToSpeak()
    {
        var synth = new FakeSynth();
        using var tts = new TtsScheduler(synth);
        tts.Start();

        var req = new SpeechRequest("id-1", "briefing", SpeechPriority.High, 0, CooldownKey: null);
        EventBus.EventBus.Publish("VOICE.SPEAK", req, source: "test"); // VOICE.* is delivered synchronously

        Assert.Equal("briefing", Assert.Single(synth.Spoken).Text);
    }
}
