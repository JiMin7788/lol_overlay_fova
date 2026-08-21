using System.IO;
using System.Media;
using Overlay.Core;
using Overlay.Core.Jungle;

namespace Overlay.Client.Tts;

/// <summary>
/// M31 §B — voices <c>UI.ENEMY_PRESENCE</c> alerts from the bundled clip set.
///
/// <para><b>Why clips are preloaded as PCM.</b> Measured on the sample set: opening a clip per
/// alert costs ~263ms, preloading drops it to ~0.02ms. Holding a <c>MediaPlayer</c> per clip
/// would fix the latency but costs ~4.25MB each (~349MB for the set), blowing M31 §5's ≤+30MB
/// budget — that overhead is the player object, not the audio. Raw PCM for all 34 clips is
/// ~1.2MB, so this loads the bytes once and keeps only one output alive.</para>
///
/// <para><b>Why the pieces are concatenated into one buffer.</b> An alert is role + location +
/// event (see <see cref="EnemyVoiceScript"/>). Playing three files back-to-back would gap
/// audibly between them, so their samples are spliced into a single WAV rendered in one shot.
/// Every clip is the same format (22.05kHz / 16-bit / mono), which makes the splice a byte
/// concatenation rather than a resample.</para>
///
/// <para>Failures are swallowed by design: a missing clip or a busy sound device must never
/// take down the alert path, and the toast/afterimage still fire regardless.</para>
/// </summary>
public sealed class EnemyVoicePlayer : IDisposable
{
    /// <summary>Clip folder, relative to the app base directory (csproj copies it there).</summary>
    public const string DefaultVoiceDir = "Assets/voice/enemy";

    private readonly Dictionary<string, byte[]> _pcm = new(StringComparer.OrdinalIgnoreCase);
    private readonly VoiceLocationResolver? _locations;
    private readonly Func<(string Pack, string Detail, double Volume)> _config;
    private readonly Action<string>? _log;
    private readonly SoundPlayer _player = new();
    private readonly object _gate = new();
    private string? _subscriptionId;
    private bool _disposed;

    // All clips share this format; asserted at load so a re-recorded file in the wrong format
    // is caught at startup instead of producing garbled splices at runtime.
    private const int ExpectedRate = 22050;
    private const int ExpectedBits = 16;
    private const int ExpectedChannels = 1;

    /// <param name="config">
    /// Reads <c>voice.enemyVoicePack</c>, <c>voice.enemyVoiceDetail</c> and
    /// <c>voice.enemyVoiceVolume</c> at each alert, so changing any of them in settings takes
    /// effect without a restart.
    /// </param>
    public EnemyVoicePlayer(
        Func<(string Pack, string Detail, double Volume)> config,
        VoiceLocationResolver? locations = null,
        string? voiceDir = null,
        Action<string>? log = null)
    {
        _config = config;
        _log = log;
        _locations = locations ?? VoiceLocationResolver.TryLoad();

        var dir = voiceDir ?? Path.Combine(AppContext.BaseDirectory, DefaultVoiceDir);
        Preload(dir);
    }

    /// <summary>Clip keys successfully loaded — for diagnostics and tests.</summary>
    public IReadOnlyCollection<string> LoadedClips => _pcm.Keys;

    /// <summary>Starts consuming <c>UI.ENEMY_PRESENCE</c>. Safe to call once; later calls no-op.</summary>
    public void Start()
    {
        lock (_gate)
        {
            if (_disposed || _subscriptionId is not null) return;
            _subscriptionId = Overlay.Core.EventBus.EventBus.Subscribe("UI.ENEMY_PRESENCE", OnAlert);
        }
    }

    private void OnAlert(Overlay.Core.EventBus.Event e)
    {
        try
        {
            if (e.Payload is not EnemyPresenceAlert alert) return;

            var (pack, detail, _) = _config();
            // Anything other than "prerecorded" silences this path — there is no synthesizer
            // fallback (see VoiceConfig.EnemyVoicePack).
            if (!string.Equals(pack, "prerecorded", StringComparison.OrdinalIgnoreCase)) return;

            var location = _locations?.Resolve(alert.ZoneKey, alert.X01, alert.Y01, detail);
            var clips = EnemyVoiceScript.Build(alert, location);
            if (clips.Count == 0) return;

            Play(clips);
        }
        catch (Exception ex)
        {
            _log?.Invoke($"EnemyVoicePlayer: alert failed — {ex.Message}");
        }
    }

    /// <summary>Splices the clips into one buffer and plays it, replacing anything still sounding.</summary>
    public void Play(IReadOnlyList<string> clipKeys)
    {
        var parts = new List<byte[]>(clipKeys.Count);
        foreach (var k in clipKeys)
        {
            if (_pcm.TryGetValue(k, out var b)) parts.Add(b);
            else _log?.Invoke($"EnemyVoicePlayer: clip '{k}' not loaded — skipped");
        }
        if (parts.Count == 0) return;

        var wav = BuildWav(parts, VolumeOrDefault());
        lock (_gate)
        {
            if (_disposed) return;
            try
            {
                // A newer alert supersedes an older one: the stale callout is now wrong, and
                // overlapping speech is unintelligible anyway.
                _player.Stop();
                _player.Stream = new MemoryStream(wav, writable: false);
                _player.Play();
            }
            catch (Exception ex)
            {
                _log?.Invoke($"EnemyVoicePlayer: playback failed — {ex.Message}");
            }
        }
    }

    private void Preload(string dir)
    {
        if (!Directory.Exists(dir))
        {
            _log?.Invoke($"EnemyVoicePlayer: voice dir not found ({dir}) — enemy voice disabled");
            return;
        }

        foreach (var file in Directory.GetFiles(dir, "*.wav"))
        {
            try
            {
                var pcm = ReadPcm(file);
                if (pcm is not null) _pcm[Path.GetFileNameWithoutExtension(file)] = pcm;
            }
            catch (Exception ex)
            {
                _log?.Invoke($"EnemyVoicePlayer: failed to load {Path.GetFileName(file)} — {ex.Message}");
            }
        }

        _log?.Invoke($"EnemyVoicePlayer: loaded {_pcm.Count} clips " +
                     $"({_pcm.Values.Sum(b => (long)b.Length) / 1024.0 / 1024.0:F2} MB)");
    }

    /// <summary>
    /// Extracts the raw sample bytes from a PCM WAV, verifying the format matches the rest of the
    /// set — clips of differing rates cannot be spliced by concatenation.
    /// </summary>
    private byte[]? ReadPcm(string path)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length < 44) return null;
        if (bytes[0] != 'R' || bytes[1] != 'I' || bytes[2] != 'F' || bytes[3] != 'F') return null;

        int pos = 12; // past "RIFF" + size + "WAVE"
        int channels = 0, rate = 0, bits = 0;

        while (pos + 8 <= bytes.Length)
        {
            var id = System.Text.Encoding.ASCII.GetString(bytes, pos, 4);
            var size = BitConverter.ToInt32(bytes, pos + 4);
            var body = pos + 8;
            if (size < 0 || body + size > bytes.Length) break;

            if (id == "fmt ")
            {
                channels = BitConverter.ToInt16(bytes, body + 2);
                rate = BitConverter.ToInt32(bytes, body + 4);
                bits = BitConverter.ToInt16(bytes, body + 14);
            }
            else if (id == "data")
            {
                if (rate != ExpectedRate || bits != ExpectedBits || channels != ExpectedChannels)
                {
                    _log?.Invoke($"EnemyVoicePlayer: {Path.GetFileName(path)} is " +
                                 $"{rate}Hz/{bits}bit/{channels}ch, expected " +
                                 $"{ExpectedRate}/{ExpectedBits}/{ExpectedChannels} — skipped");
                    return null;
                }
                var data = new byte[size];
                Array.Copy(bytes, body, data, 0, size);
                return data;
            }

            pos = body + size + (size % 2); // chunks are word-aligned
        }
        return null;
    }

    /// <summary>Scales signed 16-bit little-endian PCM in place. Saturating rather than wrapping:
    /// an overflow that wrapped would turn a loud sample into a loud sample of the OPPOSITE sign,
    /// which is heard as a click. Gain here is only ever &lt;= 1.0, so this is belt-and-braces.</summary>
    private static void ApplyGain(byte[] buf, int offset, int length, double gain)
    {
        for (int i = offset; i + 1 < offset + length; i += 2)
        {
            short sample = (short)(buf[i] | (buf[i + 1] << 8));
            int scaled = (int)Math.Round(sample * gain);
            if (scaled > short.MaxValue) scaled = short.MaxValue;
            else if (scaled < short.MinValue) scaled = short.MinValue;
            buf[i] = (byte)(scaled & 0xFF);
            buf[i + 1] = (byte)((scaled >> 8) & 0xFF);
        }
    }

    /// <summary>Current volume, clamped. Read per playback so the settings slider applies live.</summary>
    private double VolumeOrDefault()
    {
        try { return Math.Clamp(_config().Volume, 0.0, 1.0); }
        catch { return 1.0; }
    }

    /// <summary>Wraps concatenated PCM in a WAV header so <see cref="SoundPlayer"/> can play it,
    /// applying <paramref name="volume"/> as a linear gain on the way.
    ///
    /// <para>The gain is applied to the SAMPLES rather than to the player because
    /// <see cref="SoundPlayer"/> exposes no volume control at all — it plays a stream at whatever
    /// level the data carries. Since this class already builds the buffer itself, scaling 16-bit
    /// samples here is the cheapest place to do it (one pass over ~1s of 22kHz mono audio) and it
    /// keeps playback on the dependency-free SoundPlayer path.</para></summary>
    private static byte[] BuildWav(IReadOnlyList<byte[]> parts, double volume = 1.0)
    {
        var dataLen = parts.Sum(p => p.Length);
        var wav = new byte[44 + dataLen];
        var byteRate = ExpectedRate * ExpectedChannels * ExpectedBits / 8;
        var blockAlign = (short)(ExpectedChannels * ExpectedBits / 8);

        void Ascii(int at, string s) => System.Text.Encoding.ASCII.GetBytes(s).CopyTo(wav, at);
        void I32(int at, int v) => BitConverter.GetBytes(v).CopyTo(wav, at);
        void I16(int at, short v) => BitConverter.GetBytes(v).CopyTo(wav, at);

        Ascii(0, "RIFF");
        I32(4, 36 + dataLen);
        Ascii(8, "WAVE");
        Ascii(12, "fmt ");
        I32(16, 16);                       // PCM fmt chunk size
        I16(20, 1);                        // format tag: PCM
        I16(22, ExpectedChannels);
        I32(24, ExpectedRate);
        I32(28, byteRate);
        I16(32, blockAlign);
        I16(34, ExpectedBits);
        Ascii(36, "data");
        I32(40, dataLen);

        var at = 44;
        foreach (var p in parts)
        {
            p.CopyTo(wav, at);
            at += p.Length;
        }

        if (volume < 0.999)
            ApplyGain(wav, 44, dataLen, volume);
        return wav;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            if (_subscriptionId is not null)
            {
                Overlay.Core.EventBus.EventBus.Unsubscribe(_subscriptionId);
                _subscriptionId = null;
            }
            try { _player.Stop(); } catch { /* device may already be gone */ }
            _player.Dispose();
            _pcm.Clear();
        }
    }
}
