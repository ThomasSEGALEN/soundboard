using System.Diagnostics;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Soundboard;

enum LinkState { NoApo, MicIdle, Connected }

/// <summary>Playback state of one sound: how many copies play, position/length of the latest one (seconds).</summary>
readonly record struct PlayInfo(int Count, double Position, double Length, bool Paused);

/// <summary>
/// Decodes sounds, mixes the playing ones at 48 kHz stereo and feeds the mix
/// to the APO ring (what others hear) and optionally to the speakers (what you hear).
/// </summary>
sealed class AudioEngine : IDisposable
{
    public const int Rate = 48000;
    const int MaxChunk = 4800; // 100 ms

    const int MaxVoices = 32;  // spam guard

    // fades avoid the "click" when a sound is cut
    const int FadeOutFrames = Rate * 12 / 100;   // 120 ms on Stop / Pause
    const int FadeInFrames = Rate * 2 / 100;     // 20 ms on resume
    const int RestartFrames = Rate * 3 / 100;    // 30 ms for the old copy when a sound restarts

    enum AfterFade { None, Pause, Remove }

    sealed class Voice
    {
        public required string Key;
        public required float[] Data;
        public int Pos;          // sample index (interleaved stereo)
        public int End;          // sample index where playback stops (trim)
        public int Start;        // sample index where playback starts (trim)
        public float Gain = 1f;
        public bool Paused;      // fully paused (after its fade-out)
        public bool LocalOnly;   // preview: your headset only, never the microphone
        public float Fade = 1f, FadeTarget = 1f, FadeStep;
        public AfterFade Then;

        public bool Leaving => Then == AfterFade.Remove;
        public bool PausedOrPausing => Paused || Then == AfterFade.Pause;

        public void FadeTo(float target, int frames, AfterFade then)
        {
            FadeTarget = target;
            FadeStep = 1f / Math.Max(1, frames);
            Then = then;
        }
    }

    readonly object _lock = new();
    readonly List<Voice> _voices = new();
    readonly Dictionary<string, float[]> _cache = new(StringComparer.OrdinalIgnoreCase);
    readonly Thread _thread;
    volatile bool _running = true;

    SharedRing? _ring;
    long _nextOpenAttempt;

    WasapiOut? _monitorOut;
    BufferedWaveProvider? _monitorBuf;

    public volatile float MicVolume = 0.8f;
    public volatile float MonitorVolume = 0.5f;
    public volatile bool MonitorEnabled = true;
    public volatile bool VoiceMonitorEnabled;
    public volatile float VoiceMonitorVolume = 1f;
    public volatile bool DenoiseEnabled = true;

    VoiceRing? _voiceRing;
    long _nextVoiceOpen;
    public volatile int DenoiseLevel = 1;

    public LinkState State { get; private set; } = LinkState.NoApo;
    public int PlayingCount { get { lock (_lock) return _voices.Count; } }

    /// <summary>Peak levels reported by the APO (0..1): your voice after noise suppression, and what is sent.</summary>
    public float VoiceLevel { get; private set; }
    public float OutputLevel { get; private set; }

    public AudioEngine() : this(realtime: true) { }

    /// <summary>realtime = false: no audio output and no mixer thread (tests drive <see cref="MixInto"/>).</summary>
    internal AudioEngine(bool realtime)
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "Soundboard mixer", Priority = ThreadPriority.AboveNormal };
        if (!realtime) { _running = false; return; }
        StartMonitor();
        _thread.Start();
    }

    // ------------------------------------------------------------ public API

    static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>Equalise sound levels (each file gets a gain computed when it is decoded).</summary>
    public volatile bool Normalize = true;

    /// <summary>Push-to-talk for the sounds: when false, sounds keep playing but are not sent to the mic.</summary>
    public volatile bool SoundGateOpen = true;

    readonly Dictionary<string, float> _norm = new(StringComparer.OrdinalIgnoreCase);
    float _gate = 1f; // mixer thread only

    float EffectiveGain(string path, float volume)
    {
        if (!Normalize) return volume;
        lock (_cache) return volume * (_norm.TryGetValue(path, out var g) ? g : 1f);
    }

    IEnumerable<Voice> Mine(string path) => _voices.Where(v => Same(v.Key, path) && !v.Leaving);

    static void Pause(Voice v)
    {
        if (!v.PausedOrPausing) v.FadeTo(0f, FadeOutFrames, AfterFade.Pause);
    }

    static void Resume(Voice v)
    {
        if (!v.PausedOrPausing) return;
        if (v.Paused) v.Fade = 0f;
        v.Paused = false;
        v.FadeTo(1f, FadeInFrames, AfterFade.None);
    }

    static void FadeAway(Voice v, int frames)
    {
        if (v.Paused) { v.Paused = false; v.Fade = 0f; } // already silent: removed on next pass
        v.FadeTo(0f, frames, AfterFade.Remove);
    }

    /// <summary>
    /// Plays a sound, or acts on it if it is already playing, depending on the click mode.
    /// startSec / endSec trim the sound (endSec 0 = until the end).
    /// </summary>
    public async Task TriggerAsync(string path, float volume, ClickMode mode, double startSec = 0, double endSec = 0)
        => await TriggerCore(path, path, volume, mode, startSec, endSec, localOnly: false);

    static string PreviewKey(string path) => "preview|" + path;

    /// <summary>
    /// Plays a sound in YOUR headset only (never sent to the microphone), e.g. "Listen" while
    /// adjusting it — audible even when "Hear the sounds" is off. Restarts if already previewing.
    /// </summary>
    public Task PreviewAsync(string path, float volume, double startSec = 0, double endSec = 0)
        => TriggerCore(PreviewKey(path), path, volume, ClickMode.Restart, startSec, endSec, localOnly: true);

    /// <summary>Stops every preview (e.g. when the sound editor closes).</summary>
    public void StopPreviews()
    {
        lock (_lock) foreach (var v in _voices.Where(v => v.LocalOnly && !v.Leaving).ToList()) FadeAway(v, FadeOutFrames);
    }

    async Task TriggerCore(string key, string path, float volume, ClickMode mode, double startSec, double endSec, bool localOnly)
    {
        string realPath = path;
        path = key; // voices are found by key; a preview never touches the real playback of the sound
        lock (_lock)
        {
            var mine = Mine(path).ToList();
            if (mine.Count > 0 && mode == ClickMode.PauseResume)
            {
                bool allPaused = mine.All(v => v.PausedOrPausing);
                foreach (var v in mine) { if (allPaused) Resume(v); else Pause(v); }
                return;
            }
            if (mine.Count > 0 && mode == ClickMode.Stop)
            {
                foreach (var v in mine) FadeAway(v, FadeOutFrames);
                return;
            }
        }

        float[] data = await Task.Run(() => Load(realPath));
        int total = data.Length;
        int start = Math.Clamp((int)(startSec * Rate) * 2, 0, total);
        int end = endSec > 0 ? Math.Clamp((int)(endSec * Rate) * 2, start, total) : total;
        if (end - start < 2) return;

        lock (_lock)
        {
            if (mode != ClickMode.Overlay)
                foreach (var v in Mine(path).ToList()) FadeAway(v, RestartFrames);
            if (_voices.Count >= MaxVoices) _voices.RemoveAt(0); // drop the oldest when spamming
            _voices.Add(new Voice
            {
                Key = path, Data = data, Start = start, Pos = start, End = end,
                Gain = EffectiveGain(realPath, volume), LocalOnly = localOnly,
            });
        }
    }

    public void Stop(string path)
    {
        lock (_lock) foreach (var v in Mine(path).ToList()) FadeAway(v, FadeOutFrames);
    }

    public void StopAll()
    {
        lock (_lock) foreach (var v in _voices.Where(v => !v.Leaving).ToList()) FadeAway(v, FadeOutFrames);
    }

    /// <summary>Pauses everything that plays; if everything is already paused, resumes it.</summary>
    public void TogglePauseAll()
    {
        lock (_lock)
        {
            var all = _voices.Where(v => !v.Leaving).ToList();
            bool anyRunning = all.Any(v => !v.PausedOrPausing);
            foreach (var v in all) { if (anyRunning) Pause(v); else Resume(v); }
        }
    }

    /// <summary>Pauses or resumes every copy of one sound.</summary>
    public void TogglePause(string path)
    {
        lock (_lock)
        {
            var mine = Mine(path).ToList();
            bool allPaused = mine.Count > 0 && mine.All(v => v.PausedOrPausing);
            foreach (var v in mine) { if (allPaused) Resume(v); else Pause(v); }
        }
    }

    /// <summary>Applies a new volume (0..2) to the copies of a sound that are playing.</summary>
    public void SetGain(string path, float volume)
    {
        float g = EffectiveGain(path, volume);
        lock (_lock)
            foreach (var v in Mine(path)) v.Gain = g;
    }

    /// <summary>State of every playing sound, keyed by file path. Progress follows the latest playback.</summary>
    public Dictionary<string, PlayInfo> Snapshot()
    {
        var result = new Dictionary<string, PlayInfo>(StringComparer.OrdinalIgnoreCase);
        lock (_lock)
        {
            foreach (var v in _voices)
            {
                if (v.Leaving || v.LocalOnly) continue; // fading out after Stop / preview: not shown on the buttons
                result.TryGetValue(v.Key, out var prev);
                result[v.Key] = new PlayInfo(prev.Count + 1,
                    (v.Pos - v.Start) / 2.0 / Rate, (v.End - v.Start) / 2.0 / Rate,
                    prev.Count == 0 ? v.PausedOrPausing : prev.Paused && v.PausedOrPausing);
            }
        }
        return result;
    }

    readonly Dictionary<string, double> _durations = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Duration in seconds if known (decoded, or read from the file header).</summary>
    public double? CachedDuration(string path)
    {
        lock (_cache)
        {
            if (_cache.TryGetValue(path, out var d)) return d.Length / 2.0 / Rate;
            if (_durations.TryGetValue(path, out var s)) return s;
        }
        return null;
    }

    /// <summary>Reads durations from the file headers in the background (no full decode).</summary>
    public void ProbeDurations(IEnumerable<string> paths)
    {
        var list = paths.ToList();
        Task.Run(() =>
        {
            foreach (var p in list)
            {
                lock (_cache) if (_durations.ContainsKey(p) || _cache.ContainsKey(p)) continue;
                try
                {
                    using var r = new AudioFileReader(p);
                    lock (_cache) _durations[p] = r.TotalTime.TotalSeconds;
                }
                catch { /* missing or unreadable file: no duration shown */ }
            }
        });
    }

    public void Forget(string path)
    {
        lock (_cache) _cache.Remove(path);
    }

    /// <summary>Decodes any supported file (wav, mp3, m4a/aac, wma, flac...) to 48 kHz stereo float.</summary>
    public float[] Load(string path)
    {
        lock (_cache)
            if (_cache.TryGetValue(path, out var cached)) return cached;

        using var reader = new AudioFileReader(path);
        ISampleProvider sp = reader;
        int ch = sp.WaveFormat.Channels;
        if (ch == 1)
            sp = new MonoToStereoSampleProvider(sp);
        else if (ch > 2)
            sp = new MultiplexingSampleProvider(new[] { sp }, 2); // keep front left/right
        if (sp.WaveFormat.SampleRate != Rate)
            sp = new WdlResamplingSampleProvider(sp, Rate);

        var all = new List<float>(Rate * 2 * 5);
        var buf = new float[Rate * 2];
        int n;
        while ((n = sp.Read(buf, 0, buf.Length)) > 0)
            all.AddRange(new ReadOnlySpan<float>(buf, 0, n));
        if (all.Count % 2 == 1) all.Add(0f);

        var data = all.ToArray();
        float norm = ComputeNormGain(data);
        lock (_cache) { _cache[path] = data; _norm[path] = norm; }
        return data;
    }

    /// <summary>
    /// Gain that brings a sound to about -16 dBFS RMS (silence ignored), never above
    /// what keeps its peak under 0 dBFS, limited to x0.25 .. x4.
    /// </summary>
    public static float ComputeNormGain(float[] data)
    {
        double sum = 0; long n = 0; float peak = 0;
        for (int i = 0; i < data.Length; i++)
        {
            float a = Math.Abs(data[i]);
            if (a > peak) peak = a;
            if (a > 0.003f) { sum += (double)a * a; n++; }
        }
        if (n == 0 || peak <= 0) return 1f;
        double rms = Math.Sqrt(sum / n);
        double g = 0.16 / rms; // -16 dBFS RMS: loud, already-mastered sounds change little
        g = Math.Min(g, 0.98 / peak);
        return (float)Math.Clamp(g, 0.25, 4.0);
    }

    // ------------------------------------------------------------ mixer thread

    [DllImport("winmm.dll")] static extern uint timeBeginPeriod(uint ms);
    [DllImport("winmm.dll")] static extern uint timeEndPeriod(uint ms);

    void Run()
    {
        timeBeginPeriod(1);
        try
        {
            var mix = new float[MaxChunk * 2];
            var local = new float[MaxChunk * 2]; // previews: headset only
            var scaled = new float[MaxChunk * 2];
            var bytes = new byte[MaxChunk * 2 * 4];
            var sw = Stopwatch.StartNew();
            long produced = 0;

            while (_running)
            {
                long target = sw.ElapsedTicks * Rate / Stopwatch.Frequency;
                if (target - produced > MaxChunk * 2) produced = target - MaxChunk; // after a stall, skip ahead
                int frames = (int)Math.Min(target - produced, MaxChunk);

                if (frames > 0)
                {
                    produced += frames;
                    MixInto(mix, local, frames);
                    FeedRing(mix, scaled, frames);                 // the mic: shared sounds only
                    FeedMonitor(mix, local, scaled, bytes, frames); // your headset: shared + previews
                }
                Thread.Sleep(3);
            }
        }
        finally
        {
            timeEndPeriod(1);
        }
    }

    float[] _scratchLocal = Array.Empty<float>();

    /// <summary>Mix of what is sent to the mic, previews added in (tests and simple callers).</summary>
    internal void MixInto(float[] mix, int frames)
    {
        if (_scratchLocal.Length < mix.Length) _scratchLocal = new float[mix.Length];
        MixInto(mix, _scratchLocal, frames);
        for (int i = 0; i < frames * 2; i++) mix[i] += _scratchLocal[i];
    }

    /// <summary>
    /// shared = what everyone hears (sent to the mic and to your headset);
    /// local = previews, for your headset only.
    /// </summary>
    internal void MixInto(float[] shared, float[] local, int frames)
    {
        int samples = frames * 2;
        Array.Clear(shared, 0, samples);
        Array.Clear(local, 0, samples);
        lock (_lock)
        {
            for (int v = _voices.Count - 1; v >= 0; v--)
            {
                var voice = _voices[v];
                if (voice.Paused) continue;
                // a fade that has nothing left to do (e.g. Stop on a paused sound)
                if (voice.Fade == voice.FadeTarget && voice.Then == AfterFade.Remove) { _voices.RemoveAt(v); continue; }
                if (voice.Fade == voice.FadeTarget && voice.Then == AfterFade.Pause) { voice.Paused = true; voice.Then = AfterFade.None; continue; }
                var src = voice.Data;
                float g = voice.Gain;
                var mix = voice.LocalOnly ? local : shared;
                bool remove = false;
                for (int i = 0; i < samples; i += 2)
                {
                    if (voice.Pos >= voice.End) { remove = true; break; }
                    float f = voice.Fade;
                    mix[i] += src[voice.Pos] * g * f;
                    mix[i + 1] += src[voice.Pos + 1] * g * f;
                    voice.Pos += 2;

                    if (f != voice.FadeTarget)
                    {
                        f = voice.FadeTarget > f ? Math.Min(voice.FadeTarget, f + voice.FadeStep)
                                                 : Math.Max(voice.FadeTarget, f - voice.FadeStep);
                        voice.Fade = f;
                        if (f == voice.FadeTarget)
                        {
                            if (voice.Then == AfterFade.Remove) { remove = true; break; }
                            if (voice.Then == AfterFade.Pause) { voice.Paused = true; voice.Then = AfterFade.None; break; }
                        }
                    }
                }
                if (remove) _voices.RemoveAt(v);
            }
        }
    }

    void FeedRing(float[] mix, float[] scaled, int frames)
    {
        if (_ring == null && Environment.TickCount64 >= _nextOpenAttempt)
        {
            _ring = SharedRing.TryOpen();
            _nextOpenAttempt = Environment.TickCount64 + 1000;
        }
        if (_ring == null)
        {
            State = LinkState.NoApo;
            return;
        }

        float g = MicVolume;
        int samples = frames * 2;
        // push-to-talk gate with a 10 ms ramp (no click when the key is pressed / released)
        float gateTarget = SoundGateOpen ? 1f : 0f;
        const float gateStep = 1f / (Rate / 100);
        for (int i = 0; i < samples; i += 2)
        {
            if (_gate != gateTarget) _gate = gateTarget > _gate ? Math.Min(1f, _gate + gateStep) : Math.Max(0f, _gate - gateStep);
            float k = g * _gate;
            scaled[i] = Math.Clamp(mix[i] * k, -1f, 1f);
            scaled[i + 1] = Math.Clamp(mix[i + 1] * k, -1f, 1f);
        }
        try
        {
            _ring.Flags = (DenoiseEnabled ? SharedRing.FlagDenoise : 0u)
                        | ((uint)Math.Clamp(DenoiseLevel, 0, 2) << SharedRing.LevelShift)
                        | (VoiceMonitorEnabled ? SharedRing.FlagVoiceMonitor : 0u);
            _ring.Write(scaled, frames);
            State = _ring.ReaderActive ? LinkState.Connected : LinkState.MicIdle;
            if (State == LinkState.Connected)
            {
                (VoiceLevel, OutputLevel) = _ring.ReadLevels();
            }
            else
            {
                VoiceLevel = OutputLevel = 0;
            }
        }
        catch
        {
            _ring.Dispose();
            _ring = null;
            State = LinkState.NoApo;
        }
    }

    void FeedMonitor(float[] mix, float[] local, float[] scaled, byte[] bytes, int frames)
    {
        var buf = _monitorBuf;
        if (buf == null) return;

        // your own voice ("hear myself"), as others receive it
        if (VoiceMonitorEnabled && State == LinkState.Connected)
        {
            if (_voiceRing == null && Environment.TickCount64 >= _nextVoiceOpen)
            {
                _voiceRing = VoiceRing.TryOpen();
                _nextVoiceOpen = Environment.TickCount64 + 1000;
            }
        }
        else if (_voiceRing != null)
        {
            _voiceRing.Dispose();
            _voiceRing = null;
        }

        int samples = frames * 2;
        bool preview = false;
        for (int i = 0; i < samples && !preview; i++) preview = local[i] != 0f;
        if (!MonitorEnabled && _voiceRing == null && !preview) return;

        // shared sounds only if "Hear the sounds" is on; previews always (you asked to hear them)
        float g = MonitorEnabled ? MonitorVolume : 0f;
        float gp = MonitorVolume;
        for (int i = 0; i < samples; i++) scaled[i] = mix[i] * g + local[i] * gp;
        try { _voiceRing?.AddTo(scaled, frames, VoiceMonitorVolume); }
        catch { _voiceRing?.Dispose(); _voiceRing = null; }
        for (int i = 0; i < samples; i++) scaled[i] = Math.Clamp(scaled[i], -1f, 1f);
        Buffer.BlockCopy(scaled, 0, bytes, 0, samples * 4);
        if (buf.BufferedDuration > TimeSpan.FromMilliseconds(250)) buf.ClearBuffer(); // clock drift guard
        buf.AddSamples(bytes, 0, samples * 4);
    }

    void StartMonitor()
    {
        try
        {
            _monitorBuf = new BufferedWaveProvider(WaveFormat.CreateIeeeFloatWaveFormat(Rate, 2))
            {
                DiscardOnBufferOverflow = true,
                BufferDuration = TimeSpan.FromSeconds(1),
                ReadFully = true,
            };
            _monitorOut = new WasapiOut(AudioClientShareMode.Shared, true, 60);
            _monitorOut.Init(_monitorBuf);
            _monitorOut.Play();
        }
        catch
        {
            // No speakers / headset available: the soundboard still works for the mic.
            _monitorOut?.Dispose();
            _monitorOut = null;
            _monitorBuf = null;
        }
    }

    public void Dispose()
    {
        _running = false;
        if (_thread.IsAlive) _thread.Join(1000);
        _monitorOut?.Stop();
        _monitorOut?.Dispose();
        _ring?.Dispose();
        _voiceRing?.Dispose();
    }
}
