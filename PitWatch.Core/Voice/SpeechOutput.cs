using System.Collections.Concurrent;
using System.IO;
using System.Media;
using System.Net.Http;
using System.Speech.Synthesis;

namespace PitWatch.Voice;

/// <summary>
/// Speaks text out loud, one message at a time, on a dedicated background thread.
///
/// WHY A QUEUE AND A WORKER THREAD: the previous version called SpeakAsync (fire and
/// forget) directly from the UI timer tick, which caused two bugs - messages cut each
/// other off mid-sentence (you'd hear "car" instead of "car close ahead") because a new
/// callout and its beep would grab the audio device while the previous one was still
/// playing, and the 1-second beep pause froze the whole UI because it ran on the
/// dispatcher thread. Now Speak() just drops text on a queue and returns instantly, and
/// a single worker thread plays each message to completion before starting the next.
///
/// ELEVENLABS: requests raw PCM rather than MP3 so playback can use SoundPlayer, which
/// plays synchronously on this worker thread. MediaPlayer (the obvious choice) needs a
/// WPF dispatcher and misbehaves off the UI thread, which is the wrong fit here.
/// Falls back to the Windows voice if anything fails, so a bad key never means silence.
/// </summary>
public class SpeechOutput : IDisposable
{
    private readonly SpeechSynthesizer _synth = new();
    private readonly HttpClient _http = new();
    private readonly bool _useElevenLabs;
    private readonly string _elevenLabsKey;
    private readonly string _elevenLabsVoiceId;
    private readonly bool _radioBeepEnabled;
    private bool _speechAvailable = true;
    private bool _warnedAboutElevenLabs;
    private volatile bool _elevenLabsDisabledThisRun;

    private readonly BlockingCollection<string> _queue = new(new ConcurrentQueue<string>());
    private readonly Thread _worker;
    private volatile bool _disposed;

    public DateTime LastSpokenAt { get; private set; } = DateTime.MinValue;

    /// <summary>Fires when a message is queued - lets a UI show a running transcript.</summary>
    public event Action<string>? Spoken;

    /// <summary>
    /// Fires when something goes wrong that the user should know about (voice quota
    /// exhausted, bad key, etc). These used to go to Console.WriteLine, which in a WPF app
    /// goes nowhere at all - so ElevenLabs would quietly stop working mid-race with no
    /// explanation. Now the UI can show it.
    /// </summary>
    public event Action<string>? Notice;

    public SpeechOutput(int rate = 0, int volume = 100, bool useElevenLabs = false,
        string elevenLabsKey = "", string elevenLabsVoiceId = "", bool radioBeepEnabled = true)
    {
        try
        {
            _synth.Rate = rate;
            _synth.Volume = volume;
            _synth.SetOutputToDefaultAudioDevice();
        }
        catch (Exception ex)
        {
            // No audio device, or no speech voices installed. The app is still perfectly
            // usable as a visual dashboard, so log it and carry on silently rather than
            // refusing to start.
            PitWatch.Logger.Error("Speech output unavailable - PitWatch will run without voice.", ex);
            _speechAvailable = false;
        }

        _useElevenLabs = useElevenLabs && !string.IsNullOrWhiteSpace(elevenLabsKey);
        _elevenLabsKey = elevenLabsKey;
        _elevenLabsVoiceId = elevenLabsVoiceId;
        _radioBeepEnabled = radioBeepEnabled;

        _worker = new Thread(ProcessQueue) { IsBackground = true, Name = "PitWatch Speech" };
        _worker.Start();
    }

    /// <summary>Queues a message. Returns immediately - never blocks the caller.</summary>
    public void Speak(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || _disposed) return;

        // Drop callouts if a backlog builds up (e.g. lots of events in quick succession)
        // rather than reading out stale information a minute after it stopped being true.
        if (_queue.Count >= 3) return;

        LastSpokenAt = DateTime.UtcNow;
        Spoken?.Invoke(text);
        _queue.Add(text);
    }

    private void ProcessQueue()
    {
        foreach (var text in _queue.GetConsumingEnumerable())
        {
            if (_disposed) return;

            try
            {
                if (_radioBeepEnabled && _speechAvailable)
                {
                    // Safe to block here - this is the worker thread, not the UI.
                    Console.Beep(1000, 60);
                    Console.Beep(700, 60);
                    Thread.Sleep(350);
                }

                bool spoken = false;
                if (_useElevenLabs && !_elevenLabsDisabledThisRun)
                {
                    spoken = TrySpeakWithElevenLabs(text);
                }

                if (!spoken && _speechAvailable)
                {
                    // Synchronous Speak (not SpeakAsync) so this message finishes fully
                    // before the loop moves on to the next one - this is what actually
                    // prevents the truncation.
                    _synth.Speak(text);
                }
            }
            catch (OperationCanceledException)
            {
                // Happens when the app closes mid-sentence - normal shutdown, not a fault.
                return;
            }
            catch (Exception ex)
            {
                PitWatch.Logger.Error("Speech playback failed.", ex);
            }
        }
    }

    /// <summary>
    /// Verifies an ElevenLabs key without speaking anything. Returns null on success,
    /// or a human-readable reason on failure.
    /// </summary>
    public static async Task<string?> TestElevenLabsKeyAsync(string apiKey)
    {
        if (string.IsNullOrWhiteSpace(apiKey)) return "No key entered.";

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.elevenlabs.io/v1/user");
            request.Headers.Add("xi-api-key", apiKey);

            var response = await http.SendAsync(request);
            return response.IsSuccessStatusCode ? null : ExplainElevenLabsFailure((int)response.StatusCode);
        }
        catch (Exception ex)
        {
            return $"couldn't reach ElevenLabs ({ex.Message})";
        }
    }

    private bool TrySpeakWithElevenLabs(string text)
    {
        try
        {
            var url = $"https://api.elevenlabs.io/v1/text-to-speech/{_elevenLabsVoiceId}?output_format=pcm_24000";
            var body = new
            {
                text,
                model_id = "eleven_multilingual_v2",
                voice_settings = new { stability = 0.5, similarity_boost = 0.75 }
            };

            using var request = new HttpRequestMessage(HttpMethod.Post, url);
            request.Headers.Add("xi-api-key", _elevenLabsKey);
            request.Content = new StringContent(System.Text.Json.JsonSerializer.Serialize(body),
                System.Text.Encoding.UTF8, "application/json");

            // .Result is acceptable here specifically because this runs on our own
            // dedicated worker thread, where blocking is expected and harmless.
            var response = _http.Send(request);
            if (!response.IsSuccessStatusCode)
            {
                int code = (int)response.StatusCode;

                // 401/403 mean the key itself is wrong - that will never fix itself, so
                // stop trying. Otherwise every single callout for the rest of the session
                // pays the cost of a doomed network round-trip before falling back.
                if (code is 401 or 403)
                {
                    _elevenLabsDisabledThisRun = true;
                }

                string detail = ExplainElevenLabsFailure(code);
                PitWatch.Logger.Warn($"ElevenLabs request failed ({(int)response.StatusCode}): {detail}");

                // Only tell the user once per run - repeating it every single callout
                // would be worse than the silence it replaced.
                if (!_warnedAboutElevenLabs)
                {
                    _warnedAboutElevenLabs = true;
                    string suffix = code is 401 or 403
                        ? " Using the Windows voice for the rest of this session - fix the key in Settings and restart to re-enable it."
                        : " Falling back to the Windows voice.";
                    Notice?.Invoke($"ElevenLabs voice unavailable: {detail}{suffix}");
                }
                return false;
            }

            using var pcmStream = new MemoryStream();
            response.Content.ReadAsStream().CopyTo(pcmStream);
            var pcm = pcmStream.ToArray();
            if (pcm.Length == 0) return false;

            using var wav = new MemoryStream(BuildWav(pcm, sampleRate: 24000));
            using var player = new SoundPlayer(wav);
            player.PlaySync(); // blocks until finished - exactly what we want here
            return true;
        }
        catch (Exception ex)
        {
            PitWatch.Logger.Error("ElevenLabs request threw an exception.", ex);
            if (!_warnedAboutElevenLabs)
            {
                _warnedAboutElevenLabs = true;
                Notice?.Invoke("ElevenLabs voice unavailable (network or connection problem). Falling back to the Windows voice.");
            }
            return false;
        }
    }

    /// <summary>
    /// Turns an ElevenLabs HTTP status into something a driver can act on. 401 and 429 are
    /// by far the most common in practice - a free-tier character quota running out mid-race
    /// looks exactly like "it just stopped working".
    /// </summary>
    private static string ExplainElevenLabsFailure(int statusCode) => statusCode switch
    {
        401 => "the API key was rejected - check it in Settings.",
        403 => "access denied - the key may not have text-to-speech permission.",
        422 => "the request was rejected - the voice ID may be wrong.",
        429 => "you've hit the rate limit or run out of monthly characters on your plan.",
        >= 500 => "ElevenLabs is having server problems.",
        _ => $"error {statusCode}.",
    };

    /// <summary>Wraps raw 16-bit mono PCM in a WAV container so SoundPlayer can play it.</summary>
    private static byte[] BuildWav(byte[] pcm, int sampleRate)
    {
        const short channels = 1;
        const short bitsPerSample = 16;
        int byteRate = sampleRate * channels * bitsPerSample / 8;
        short blockAlign = (short)(channels * bitsPerSample / 8);

        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
        w.Write(36 + pcm.Length);
        w.Write(System.Text.Encoding.ASCII.GetBytes("WAVE"));
        w.Write(System.Text.Encoding.ASCII.GetBytes("fmt "));
        w.Write(16);                 // PCM header size
        w.Write((short)1);           // PCM format
        w.Write(channels);
        w.Write(sampleRate);
        w.Write(byteRate);
        w.Write(blockAlign);
        w.Write(bitsPerSample);
        w.Write(System.Text.Encoding.ASCII.GetBytes("data"));
        w.Write(pcm.Length);
        w.Write(pcm);
        w.Flush();
        return ms.ToArray();
    }

    public void Dispose()
    {
        _disposed = true;
        _queue.CompleteAdding();
        try { _synth.SpeakAsyncCancelAll(); } catch { /* shutting down anyway */ }
        _synth.Dispose();
        _http.Dispose();
    }
}
