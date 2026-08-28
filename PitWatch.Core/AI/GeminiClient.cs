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
    private readonly HttpClient _http = new();
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
    public static string ExplainGeminiFailure(int statusCode) => statusCode switch
    {
        400 => "the request was rejected - the API key may be malformed",
        403 => "the key was rejected - check it in Settings",
        404 => "that model is no longer available - Google rotates these; check Settings",
        429 => "you've hit Google's free-tier limit (it resets - per-minute caps clear quickly, daily caps take until tomorrow)",
        >= 500 => "Google is having server problems",
        _ => $"error {statusCode}",
    };

    public GeminiClient(string apiKey, string model, string personality = "Helpful")
    {
        _apiKey = apiKey;
        _model = model;
        Personality = personality;
    }

    /// <summary>
    /// Verifies a key works, so problems surface in Settings rather than mid-race.
    /// Returns null on success, or a human-readable reason on failure.
    /// </summary>
    public static async Task<string?> TestKeyAsync(string apiKey, string model)
    {
        if (string.IsNullOrWhiteSpace(apiKey)) return "No key entered.";

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            var url = $"https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent?key={apiKey}";
            var body = new { contents = new[] { new { parts = new[] { new { text = "Reply with the single word: ok" } } } } };

            using var content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
            var response = await http.PostAsync(url, content);

            if (response.IsSuccessStatusCode) return null;

            // A quota error still proves the key itself is valid, which is worth saying -
            // otherwise it looks like the key is broken when it's just used up for today.
            if ((int)response.StatusCode == 429)
                return "Key is valid, but you've hit today's free-tier limit. It'll work again once the quota resets.";

            return ExplainGeminiFailure((int)response.StatusCode);
        }
        catch (Exception ex)
        {
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
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            var response = await _http.PostAsync(url, content);
            var responseText = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                // Surface the real reason instead of a generic message, so config
                // problems (bad model name, bad key, real quota exhaustion) are obvious.
                string hint = (int)response.StatusCode switch
                {
                    404 => "Model not found - check GeminiModel in config.json against https://ai.google.dev/gemini-api/docs/models",
                    429 => "Rate limited - free tier allows ~10-15 requests/minute. Wait a bit and try again.",
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
        catch (Exception ex)
        {
            return $"Couldn't reach the AI engineer: {ex.Message}";
        }
    }

    /// <summary>
    /// Transcribes a recorded WAV file to text. Used for push-to-talk voice input, which
    /// needs far better accuracy than Windows' legacy speech recognizer could manage.
    /// Returns null if transcription isn't possible (no key, request failed).
    /// </summary>
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
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            var response = await _http.PostAsync(url, content);
            var responseText = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                PitWatch.Logger.Warn($"Gemini transcription failed ({(int)response.StatusCode}): {responseText}");
                LastTranscribeError = ExplainGeminiFailure((int)response.StatusCode);
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
            LastTranscribeError = "couldn't reach Google (network problem)";
            return null;
        }
    }
}
