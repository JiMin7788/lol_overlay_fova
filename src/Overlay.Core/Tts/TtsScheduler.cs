using Overlay.Core.EventBus;
using Overlay.Core.Overlay;

namespace Overlay.Core.Tts;

/// <summary>
/// The testable heart of M09 TTS Engine. It owns the <b>priority queue</b>, <b>CRITICAL
/// interrupt</b>, and <b>cooldown de-duplication</b> logic — with <b>no audio / System.Speech
/// dependency</b>. The actual speech is delegated to the <see cref="ISpeechSynthesizer"/>
/// seam (implemented in Overlay.Client over the offline Windows SAPI synthesizer), so every
/// scheduling rule is unit-testable with a fake synthesizer and a virtual <see cref="IClock"/>
/// — no real audio device and no <c>Thread.Sleep</c>.
///
/// <para><b>Spec Interfaces</b> — <see cref="Speak"/>, <see cref="Stop"/>,
/// <see cref="SetVoicePack"/> map directly onto <c>TTSEngine.speak/stop/setVoicePack</c>.</para>
///
/// <para><b>Spec Internal Logic:</b>
/// <list type="number">
///   <item>Requests enter a priority queue; a <see cref="SpeechPriority.Critical"/> request
///   interrupts a currently-playing <see cref="SpeechPriority.Low"/>/<see cref="SpeechPriority.Normal"/>
///   utterance (see <see cref="ShouldInterrupt"/>, a pure predicate).</item>
///   <item>A request carrying a non-null <see cref="SpeechRequest.CooldownKey"/> is ignored if
///   the same key was accepted within the cooldown window (default 5s, overridable / M14
///   <c>voice.ttsCooldownSeconds</c>). The window is measured against an injected
///   <see cref="IClock"/> so it is deterministically testable.</item>
///   <item>Speech itself uses the OS-built-in offline SAPI backend (Overlay.Client), so there
///   is no network call and no cloud-consent/offline-fallback branch to implement.</item>
/// </list></para>
///
/// <para><b>Threading.</b> The scheduling logic here is pure and synchronous. In production the
/// SAPI backend's <c>SpeakAsync</c> runs playback off the render/API threads and raises
/// <see cref="ISpeechSynthesizer.SpeakCompleted"/> on its own thread, which chains the next
/// request (<see cref="OnSpeakCompleted"/>) — that is the spec's "Thread 4" queue pump, without
/// baking real threads/timers into this testable core. <see cref="ProcessNext"/> is also exposed
/// so a worker (or a test) can drain the queue explicitly. All state is lock-guarded; synth
/// calls are made outside the lock to avoid reentrancy.</para>
/// </summary>
public sealed class TtsScheduler : IDisposable
{
    /// <summary>Spec default cooldown window when neither the caller nor Config supplies one.</summary>
    public const int DefaultCooldownMs = 5000;

    private readonly ISpeechSynthesizer _synth;
    private readonly IClock _clock;
    private readonly int _cooldownMs;

    private readonly object _gate = new();
    private readonly List<SpeechRequest> _queue = new();
    private readonly Dictionary<string, long> _lastAccepted = new();

    private SpeechRequest? _current;
    private string? _voiceId;
    private string? _subscriptionId;

    /// <param name="synth">Audio backend seam (fake in tests, SAPI in production).</param>
    /// <param name="clock">Time source for the cooldown window (defaults to the system clock).</param>
    /// <param name="cooldownMs">Cooldown window in ms; defaults to <see cref="DefaultCooldownMs"/>
    /// (the client sources M14 <c>voice.ttsCooldownSeconds</c> here).</param>
    /// <param name="voiceId">Initial voice pack id (null = synthesizer default).</param>
    public TtsScheduler(
        ISpeechSynthesizer synth,
        IClock? clock = null,
        int? cooldownMs = null,
        string? voiceId = null)
    {
        _synth = synth ?? throw new ArgumentNullException(nameof(synth));
        _clock = clock ?? new SystemClock();
        _cooldownMs = cooldownMs ?? DefaultCooldownMs;
        _voiceId = voiceId;
        _synth.SpeakCompleted += OnSpeakCompleted;
    }

    // ── Interfaces (spec) ────────────────────────────────────────────────────────

    /// <summary>Enqueue <paramref name="text"/> for speech at <paramref name="priority"/>.
    /// A non-null <paramref name="cooldownKey"/> is ignored if the same key was accepted within
    /// the cooldown window (spam suppression). A CRITICAL request interrupts a currently-playing
    /// LOW/NORMAL utterance. Empty text is a no-op.</summary>
    public void Speak(string text, SpeechPriority priority, string? cooldownKey = null)
    {
        if (string.IsNullOrEmpty(text)) return;

        bool interrupt = false;
        SpeechRequest? toStart = null;

        lock (_gate)
        {
            long now = _clock.NowMs;

            // Cooldown de-dup (spec Internal Logic #2): same key inside the window → ignored.
            if (cooldownKey is not null)
            {
                if (_lastAccepted.TryGetValue(cooldownKey, out long last) && now - last < _cooldownMs)
                    return;
                _lastAccepted[cooldownKey] = now;
            }

            _queue.Add(new SpeechRequest(Guid.NewGuid().ToString(), text, priority, now, cooldownKey));

            // CRITICAL interrupt (spec Internal Logic #1): free the slot so the critical
            // request starts now instead of after the current LOW/NORMAL utterance.
            if (_current is not null && ShouldInterrupt(priority, _current.Priority))
            {
                _current = null;
                interrupt = true;
            }

            if (_current is null)
                toStart = TakeHighest();
        }

        if (interrupt) _synth.Stop();
        if (toStart is not null) _synth.Speak(toStart.Text, _voiceId);
    }

    /// <summary>Stop the current utterance and clear the pending queue (no replay).</summary>
    public void Stop()
    {
        lock (_gate)
        {
            _queue.Clear();
            _current = null;
        }
        _synth.Stop();
    }

    /// <summary>Select the voice pack (SAPI installed voice) used for subsequent speech.</summary>
    public void SetVoicePack(string voiceId)
    {
        lock (_gate) { _voiceId = voiceId; }
    }

    // ── Queue pump ───────────────────────────────────────────────────────────────

    /// <summary>Start the highest-priority pending request if nothing is currently playing.
    /// Returns true if a request was started. Called by the worker/tests; the natural-completion
    /// path (<see cref="OnSpeakCompleted"/>) uses the same logic to chain the queue.</summary>
    public bool ProcessNext()
    {
        SpeechRequest? toStart;
        lock (_gate)
        {
            if (_current is not null) return false;
            toStart = TakeHighest();
        }

        if (toStart is null) return false;
        _synth.Speak(toStart.Text, _voiceId);
        return true;
    }

    private void OnSpeakCompleted()
    {
        SpeechRequest? toStart;
        lock (_gate)
        {
            _current = null;
            toStart = TakeHighest();
        }

        if (toStart is not null) _synth.Speak(toStart.Text, _voiceId);
    }

    /// <summary>Remove and return the highest-priority pending request (FIFO within a priority),
    /// setting it as current. Returns null when the queue is empty. Caller holds <see cref="_gate"/>.</summary>
    private SpeechRequest? TakeHighest()
    {
        if (_queue.Count == 0) return null;

        int best = 0;
        for (int i = 1; i < _queue.Count; i++)
        {
            if (_queue[i].Priority > _queue[best].Priority) best = i;
        }

        var req = _queue[best];
        _queue.RemoveAt(best);
        _current = req;
        return req;
    }

    // ── Interrupt predicate (spec Internal Logic #1) ─────────────────────────────

    /// <summary>Pure interrupt rule: a CRITICAL request interrupts a currently-playing LOW or
    /// NORMAL utterance. HIGH does not interrupt, and nothing interrupts a playing CRITICAL —
    /// exactly the spec's "CRITICAL … 재생 중인 LOW/NORMAL 음성을 즉시 인터럽트".</summary>
    public static bool ShouldInterrupt(SpeechPriority incoming, SpeechPriority current)
        => incoming == SpeechPriority.Critical
           && (current == SpeechPriority.Low || current == SpeechPriority.Normal);

    // ── Optional VOICE.* event routing (M15) ─────────────────────────────────────

    /// <summary>Subscribe to <c>VOICE.*</c> events, routing any <see cref="SpeechRequest"/>
    /// payload to <see cref="Speak"/>. Optional convenience so other modules can request speech
    /// via the bus; the primary API remains <see cref="Speak"/>. Idempotent.</summary>
    public void Start()
    {
        if (_subscriptionId is not null) return;
        _subscriptionId = EventBus.EventBus.Subscribe("VOICE.*", OnVoiceEvent);
    }

    private void OnVoiceEvent(Event evt)
    {
        if (evt.Payload is SpeechRequest req)
            Speak(req.Text, req.Priority, req.CooldownKey);
    }

    // ── Inspection (tests) ───────────────────────────────────────────────────────

    /// <summary>The request currently playing, or null when idle.</summary>
    public SpeechRequest? Current { get { lock (_gate) { return _current; } } }

    /// <summary>Number of requests waiting in the queue (excludes the current one).</summary>
    public int PendingCount { get { lock (_gate) { return _queue.Count; } } }

    public void Dispose()
    {
        _synth.SpeakCompleted -= OnSpeakCompleted;
        if (_subscriptionId is not null)
        {
            EventBus.EventBus.Unsubscribe(_subscriptionId);
            _subscriptionId = null;
        }
    }
}
