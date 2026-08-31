using System.Linq;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace PitWatch.AI;

/// <summary>
/// Talks to Google's Gemini API (free tier) for free-form race engineer questions.
/// Get a free key at https://aistudio.google.com/apikey and paste it into config.json.
///
/// NOTE: Gemini 2.0 Flash was shut down June 1, 2026 - if your config.json still has
/// "gemini-2.0-flash" as the model, that alone would explain every request failing.
/// The default here is "gemini-2.5-flash", confirmed active on the free tier as of
/// mid-2026. If you get a 404 model-not-found error in the future, check
/// https://ai.google.dev/gemini-api/docs/models for the current free model name.
///
/// NOTE ON RATE LIMITS: the free tier is roughly 10-15 requests per minute. This class
/// enforces a small minimum gap between calls so rapid testing doesn't immediately
/// trip a 429. If you still see 429s, wait ~60 seconds - it's almost always the
/// per-minute limit clearing, not a real error with your key.
/// </summary>
public class GeminiClient
{
    // 45 seconds: generation can legitimately take a while, especially on models that
    // reason before answering. The .NET default is 100s, which is too long to leave a
    // driver waiting mid-race, but the old 15s test timeout was far too short and caused
    // valid requests to be reported as network failures.
    // Forced to HTTP/1.1. Symptom that led here: GET requests succeeded in milliseconds
    // while every POST with a body hung until the timeout. That pattern is the classic
    // signature of HTTP/2 negotiation failing against a middlebox - antivirus or firewall
    // TLS inspection - which handles simple GETs fine but stalls on request bodies.
    // HTTP/1.1 costs nothing here and sidesteps the whole class of problem.
    private readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromSeconds(45),
        DefaultRequestVersion = System.Net.HttpVersion.Version11,
        DefaultVersionPolicy = System.Net.Http.HttpVersionPolicy.RequestVersionOrLower,
    };
    private readonly string _apiKey;
    private readonly string _model;
    private DateTime _lastCallUtc = DateTime.MinValue;
    private static readonly TimeSpan MinGapBetweenCalls = TimeSpan.FromSeconds(4);

    /// <summary>Mutable so Settings can change it without needing to recreate the client.</summary>
    public string Personality { get; set; } = "Helpful";

    /// <summary>
    /// Why the last transcription attempt failed, if it did. Voice input used to just
    /// return nothing on failure, which was indistinguishable from "didn't hear you" -
    /// so a quota running out looked like the mic had broken.
    /// </summary>
    public string? LastTranscribeError { get; private set; }

    /// <summary>
    /// Explains a Gemini HTTP status in terms a driver can act on. 429 is the common one:
    /// the free tier has both per-minute and per-day caps, and a long session using voice
    /// input repeatedly will hit them.
    /// </summary>
    /// <summary>
    /// Pulls the useful numbers out of a 429 response. Google states the exact quota, the
    /// model it applies to, and when to retry - all of which is far more actionable than
    /// "rate limited", and explains the surprising case where you've made no requests for
    /// an hour but are still blocked (daily quotas reset on a schedule, not a rolling window).
    /// </summary>
    public static string ExplainQuotaError(string responseBody)
    {
        try
        {
            using var doc = JsonDocument.Parse(responseBody);
            var details = doc.RootElement.GetProperty("error").GetProperty("details");

            string? quotaValue = null, model = null, retryDelay = null;
            bool isDaily = false;

            foreach (var detail in details.EnumerateArray())
            {
                var type = detail.TryGetProperty("@type", out var t) ? t.GetString() : null;

                if (type != null && type.Contains("QuotaFailure")
                    && detail.TryGetProperty("violations", out var violations))
                {
                    foreach (var v in violations.EnumerateArray())
                    {
                        if (v.TryGetProperty("quotaValue", out var qv)) quotaValue = qv.GetString();
                        if (v.TryGetProperty("quotaId", out var qid))
                            isDaily = qid.GetString()?.Contains("PerDay") == true;
                        if (v.TryGetProperty("quotaDimensions", out var dims)
                            && dims.TryGetProperty("model", out var m)) model = m.GetString();
                    }
                }

                if (type != null && type.Contains("RetryInfo")
                    && detail.TryGetProperty("retryDelay", out var rd)) retryDelay = rd.GetString();
            }

            if (quotaValue != null)
            {
                string period = isDaily ? "today's" : "the current";
                string modelPart = model != null ? $" for {model}" : "";
                string when = isDaily
                    ? " It resets on Google's daily schedule. Switching to a different model in Settings gives you a fresh allowance - limits are per model, and some are far higher than others."
                    : (retryDelay != null ? $" Try again in about {retryDelay}." : " Try again shortly.");

                return $"you've used all {quotaValue} of {period} free requests{modelPart}.{when}";
            }
        }
        catch
        {
            // Fall through to the generic message below.
        }

        return "you've hit Google's free-tier limit (per-minute caps clear quickly, daily caps reset on Google's schedule)";
    }

    public static string ExplainGeminiFailure(int statusCode) => statusCode switch
    {
        400 => "the request was rejected - the API key may be malformed",
        403 => "the key was rejected - check it in Settings",
        404 => "that model is no longer available - Google rotates these; check Settings",
        429 => "you've hit Google's free-tier limit for this model - switching model in Settings gives a fresh allowance",
        503 => "Google's servers are overloaded for this model right now - nothing wrong with your key. Try again shortly, or pick a different model in Settings",
        >= 500 => "Google is having server problems",
        _ => $"error {statusCode}",
    };

    public GeminiClient(string apiKey, string model, string personality = "Helpful")
    {
        // Disable Expect: 100-continue.
        //
        // .NET adds this header on larger request bodies and then waits for the server to
        // reply "100 Continue" before sending the body. If anything in the path doesn't
        // complete that handshake, the request hangs until the timeout with no error.
        //
        // That matched the symptoms exactly: ~1KB text questions worked fine, while a 38KB
        // audio upload stalled for the full 45 seconds every time. Turning it off makes the
        // body send immediately, which is what we want for requests this small anyway.
        _http.DefaultRequestHeaders.ExpectContinue = false;

        _apiKey = apiKey;
        _model = model;
        Personality = personality;
    }

    /// <summary>
    /// Verifies a key works, so problems surface in Settings rather than mid-race.
    /// Returns null on success, or a human-readable reason on failure.
    /// </summary>
    /// <summary>
    /// Asks Google which models this key can actually use.
    ///
    /// Hardcoding model names doesn't work: Google retires them regularly, and a name that
    /// was current when this shipped can be dead months later - which is exactly what
    /// happened with gemini-2.5-flash. Fetching the live list means the dropdown can never
    /// offer something that no longer exists.
    /// </summary>
    public static async Task<List<string>> ListModelsAsync(string apiKey)
    {
        var models = new List<string>();
        if (string.IsNullOrWhiteSpace(apiKey)) return models;

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            var response = await http.GetAsync(
                $"https://generativelanguage.googleapis.com/v1beta/models?key={apiKey}");

            if (!response.IsSuccessStatusCode)
            {
                PitWatch.Logger.Warn($"Couldn't list models ({(int)response.StatusCode}).");
                return models;
            }

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            foreach (var item in doc.RootElement.GetProperty("models").EnumerateArray())
            {
                // Only models that can actually generate content are useful here - the list
                // also includes embedding models, which would fail if selected.
                if (!item.TryGetProperty("supportedGenerationMethods", out var methods)) continue;
                if (!methods.EnumerateArray().Any(m => m.GetString() == "generateContent")) continue;

                var name = item.GetProperty("name").GetString();
                if (name == null) continue;

                name = name.Replace("models/", "");

                // Skip anything obviously unsuited to short radio answers.
                if (name.Contains("embedding") || name.Contains("aqa")) continue;

                models.Add(name);
            }
        }
        catch (Exception ex)
        {
            PitWatch.Logger.Warn($"Couldn't list models: {ex.Message}");
        }

        return models.OrderBy(m => m).ToList();
    }

    public static async Task<string?> TestKeyAsync(string apiKey, string model)
    {
        if (string.IsNullOrWhiteSpace(apiKey)) return "No key entered.";

        try
        {
            // Checks the key by listing models rather than generating text.
            //
            // The previous version sent a real generateContent request with a 15 second
            // timeout, which was the wrong tool for the job: generation is genuinely slow
            // (and slower still on models that "think" before answering), so a valid key
            // could time out and get reported as "couldn't reach Google" - blaming the
            // network for what was really an impatient timeout. Listing models answers the
            // only question being asked here ("is this key accepted?") in well under a
            // second, and confirms the chosen model exists while it's at it.
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            var url = $"https://generativelanguage.googleapis.com/v1beta/models?key={apiKey}";

            var response = await http.GetAsync(url);
            var payload = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                if ((int)response.StatusCode == 429)
                    return $"Key is valid, but {ExplainQuotaError(payload)}";

                PitWatch.Logger.Warn($"Key test failed ({(int)response.StatusCode}): {payload}");
                return ExplainGeminiFailure((int)response.StatusCode);
            }

            // Warn if the configured model isn't in the returned list - a wrong model name
            // is the most common reason a valid key still fails during a race.
            if (!string.IsNullOrWhiteSpace(model) && !payload.Contains($"models/{model}"))
            {
                return $"Key works, but the model \"{model}\" wasn't in Google's list. "
                     + "It may have been retired - check Settings against ai.google.dev/gemini-api/docs/models.";
            }

            return null;
        }
        catch (TaskCanceledException)
        {
            return "the request timed out. Google was reachable but slow to respond - try again in a moment.";
        }
        catch (Exception ex)
        {
            PitWatch.Logger.Error("Gemini key test threw.", ex);
            return $"couldn't reach Google ({ex.Message})";
        }
    }

    public async Task<string> AskAsync(string question, string telemetryContext)
    {
        if (string.IsNullOrWhiteSpace(_apiKey) || _apiKey.Contains("PASTE_YOUR"))
            return "No Gemini API key configured - add one anytime from Settings to unlock free-form questions.";

        var sinceLastCall = DateTime.UtcNow - _lastCallUtc;
        if (sinceLastCall < MinGapBetweenCalls)
            return "Hold on a couple seconds between AI questions - free tier rate limit.";
        _lastCallUtc = DateTime.UtcNow;

        var url = $"https://generativelanguage.googleapis.com/v1beta/models/{_model}:generateContent?key={_apiKey}";

        var systemPrompt =
            "You are a race engineer speaking to your driver mid-session over team radio. " +
            PitWatch.Commands.PersonalityProfile.SystemPromptStyle(Personality) + " " +
            "Vary your phrasing and energy between answers, don't fall into a template. Use the live telemetry " +
            "below to answer accurately and specifically - reference real numbers when relevant (exact lap times, " +
            "fuel figures, gaps) rather than vague statements. If asked for strategy (pit timing, fuel planning), " +
            "reason through it using the numbers given rather than a generic answer. Answer in one to three short " +
            "spoken sentences - this gets read aloud by text-to-speech, so no bullet points, no markdown, no long " +
            "explanations. If the data doesn't cover the question, say so briefly instead of guessing.\n\n" +
            $"LIVE TELEMETRY:\n{telemetryContext}\n\nDRIVER'S QUESTION: {question}";

        var body = new
        {
            contents = new[]
            {
                new { parts = new[] { new { text = systemPrompt } } }
            },
            generationConfig = new
            {
                temperature = 1.1 // higher than default - more varied, energetic phrasing instead of flat/repetitive
            }
        };

        try
        {
            var json = JsonSerializer.Serialize(body);
            var (response, responseText) = await SendWithRetryAsync(url, json);

            if (!response.IsSuccessStatusCode)
            {
                // Surface the real reason instead of a generic message, so config
                // problems (bad model name, bad key, real quota exhaustion) are obvious.
                string hint = (int)response.StatusCode switch
                {
                    404 => "Model not found - check GeminiModel in config.json against https://ai.google.dev/gemini-api/docs/models",
                    429 => ExplainQuotaError(responseText),
                    400 => "Bad request - likely an invalid API key.",
                    403 => "Forbidden - your API key may not be enabled for this model.",
                    _ => "Unexpected error."
                };
                PitWatch.Logger.Warn($"Gemini request failed ({(int)response.StatusCode}): {responseText}");
                return $"AI request failed. {hint}";
            }

            using var doc = JsonDocument.Parse(responseText);
            var text = doc.RootElement
                .GetProperty("candidates")[0]
                .GetProperty("content")
                .GetProperty("parts")[0]
                .GetProperty("text")
                .GetString();

            return text?.Trim() ?? "No response from the AI.";
        }
        catch (TaskCanceledException)
        {
            return "The AI took too long to answer - ask again.";
        }
        catch (Exception ex)
        {
            PitWatch.Logger.Error("Gemini request failed.", ex);
            return $"Couldn't reach the AI engineer: {ex.Message}";
        }
    }

    /// <summary>
    /// Transcribes a recorded WAV file to text. Used for push-to-talk voice input, which
    /// needs far better accuracy than Windows' legacy speech recognizer could manage.
    /// Returns null if transcription isn't possible (no key, request failed).
    /// </summary>
    /// <summary>
    /// Sends a request, retrying briefly on 503 UNAVAILABLE.
    ///
    /// Google returns 503 when a model is temporarily saturated, and explicitly describes
    /// it as transient. Giving up on the first one meant a two-second capacity blip looked
    /// like a broken app - especially for voice input, where the driver just gets silence.
    /// Retries are deliberately short and few: this happens mid-race, and a driver waiting
    /// ten seconds for an answer is worse than being told to ask again.
    /// </summary>
    private async Task<(HttpResponseMessage Response, string Body)> SendWithRetryAsync(
        string url, string json, int maxAttempts = 2)
    {
        HttpResponseMessage? response = null;
        string body = "";

        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            response = await _http.PostAsync(url, content);
            body = await response.Content.ReadAsStringAsync();

            if (response.IsSuccessStatusCode) return (response, body);

            int code = (int)response.StatusCode;

            // Retries are deliberately limited. Every attempt counts against the daily
            // quota, and some models have a free-tier cap as low as 20 requests per DAY -
            // so aggressive retrying can exhaust a day's allowance in a handful of
            // questions. 429 is never retried: a quota error won't clear in milliseconds.
            bool retryable = code == 503 || code == 500;
            if (!retryable || attempt == maxAttempts) return (response, body);

            // Kept deliberately low: every retry is another request against a free-tier
            // daily quota that can be as small as 20 for some models. Retrying three times
            // turned one question into three, which is a poor trade when the cap is that
            // tight - a single retry still catches a brief blip.
            int delayMs = 500;
            PitWatch.Logger.Info($"Gemini returned {code}, retrying once in {delayMs}ms "
                               + "(this uses another request from your daily quota).");
            await Task.Delay(delayMs);
        }

        return (response!, body);
    }

    public async Task<string?> TranscribeAsync(string wavFilePath)
    {
        LastTranscribeError = null;
        if (string.IsNullOrWhiteSpace(_apiKey) || _apiKey.Contains("PASTE_YOUR")) return null;
        if (!File.Exists(wavFilePath)) return null;

        try
        {
            var audioBytes = await File.ReadAllBytesAsync(wavFilePath);
            string base64 = Convert.ToBase64String(audioBytes);

            var url = $"https://generativelanguage.googleapis.com/v1beta/models/{_model}:generateContent?key={_apiKey}";
            var body = new
            {
                contents = new[]
                {
                    new
                    {
                        parts = new object[]
                        {
                            new { text = "Transcribe this short radio message from a race car driver to their engineer. Reply with the transcription only, no commentary, no quotes." },
                            new { inline_data = new { mime_type = "audio/wav", data = base64 } }
                        }
                    }
                }
            };

            var json = JsonSerializer.Serialize(body);
            PitWatch.Logger.Info($"Transcribing {audioBytes.Length / 1024}KB of audio "
                               + $"({json.Length / 1024}KB encoded) using model {_model}.");

            var (response, responseText) = await SendWithRetryAsync(url, json);

            if (!response.IsSuccessStatusCode)
            {
                PitWatch.Logger.Warn($"Gemini transcription failed ({(int)response.StatusCode}): {responseText}");
                // Keep the recording on failure so a retry doesn't need re-recording.
                LastTranscribeError = (int)response.StatusCode == 429
                    ? ExplainQuotaError(responseText)
                    : ExplainGeminiFailure((int)response.StatusCode);
                return null;
            }

            using var doc = JsonDocument.Parse(responseText);
            return doc.RootElement
                .GetProperty("candidates")[0]
                .GetProperty("content")
                .GetProperty("parts")[0]
                .GetProperty("text")
                .GetString()?.Trim();
        }
        catch (Exception ex)
        {
            PitWatch.Logger.Error("Gemini transcription threw an exception.", ex);
            LastTranscribeError = ex is TaskCanceledException
                ? "the upload timed out - Google may be busy, try again or switch model in Settings"
                : $"couldn't reach Google ({ex.Message})";
            return null;
        }
    }
}
