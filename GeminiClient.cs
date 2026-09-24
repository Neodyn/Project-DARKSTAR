using System.Net.Http.Headers;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Darkstar;

// Minimal DTOs for the Gemini generateContent REST API.
// Reference: https://ai.google.dev/api/generate-content
internal class GeminiPart
{
    [JsonPropertyName("text")]
    public string? Text { get; set; }

    [JsonPropertyName("inline_data")]
    public GeminiInlineData? InlineData { get; set; }
}

internal class GeminiInlineData
{
    [JsonPropertyName("mime_type")]
    public string MimeType { get; set; } = "";

    [JsonPropertyName("data")]
    public string Data { get; set; } = "";
}

internal class GeminiContent
{
    [JsonPropertyName("parts")]
    public List<GeminiPart> Parts { get; set; } = new();
}

internal class GeminiRequest
{
    [JsonPropertyName("contents")]
    public List<GeminiContent> Contents { get; set; } = new();
}

internal class GeminiResponse
{
    [JsonPropertyName("candidates")]
    public List<GeminiCandidate>? Candidates { get; set; }
}

internal class GeminiCandidate
{
    [JsonPropertyName("content")]
    public GeminiContent? Content { get; set; }
}

/// <summary>
/// Uses the Gemini REST API (generateContent) for both speech-to-text (sending audio directly
/// as inline_data) and reply generation (a plain text prompt).
/// Docs: https://ai.google.dev/gemini-api/docs/generate-content/audio
///
/// Includes basic resilience: transient errors (429/500/502/503/504) are retried a few times
/// with a short delay, and if all retries against the primary model fail, an optional fallback
/// model is tried once before giving up. This matters in practice because the free-tier daily
/// quota can be quite tight (see 429/RESOURCE_EXHAUSTED).
/// </summary>
public sealed class GeminiClient : ISpeechToText, IResponseGenerator
{
    private readonly HttpClient _http;
    private readonly string _apiKey;
    private readonly string _model;
    private readonly string? _fallbackModel;
    private readonly int _maxRetries;
    private readonly int _retryDelayMs;

    private static readonly HashSet<int> RetryableStatusCodes = new() { 429, 500, 502, 503, 504 };

    public GeminiClient(string apiKey, string model = "gemini-2.5-flash", string? fallbackModel = null,
        int maxRetries = 2, int retryDelayMs = 1000)
    {
        _apiKey = apiKey;
        _model = model;
        _fallbackModel = string.IsNullOrWhiteSpace(fallbackModel) ? null : fallbackModel;
        _maxRetries = Math.Max(0, maxRetries);
        _retryDelayMs = Math.Max(0, retryDelayMs);
        _http = new HttpClient { BaseAddress = new Uri("https://generativelanguage.googleapis.com/") };
    }

    public async Task<string> TranscribeAsync(byte[] pcm16Mono48k, CancellationToken token = default)
    {
        if (pcm16Mono48k.Length == 0) return "";

        var wavBytes = WavUtils.WrapPcm16AsWav(pcm16Mono48k, sampleRate: 48000, channels: 1);
        var base64Audio = Convert.ToBase64String(wavBytes);

        var request = new GeminiRequest
        {
            Contents = new List<GeminiContent>
            {
                new()
                {
                    Parts = new List<GeminiPart>
                    {
                        new() { InlineData = new GeminiInlineData { MimeType = "audio/wav", Data = base64Audio } },
                        new() { Text = "Transcribe only the spoken words in this audio file, verbatim. " +
                                        "Respond ONLY with the transcribed text, no quotation marks, no commentary, " +
                                        "no preamble. If no intelligible speech is audible, respond with an empty string." }
                    }
                }
            }
        };

        var text = await CallGeminiAsync(request, token);
        return text.Trim();
    }

    public async Task<string> GenerateReplyAsync(string transcribedText, CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(transcribedText))
            return "I didn't catch that, please repeat.";

        var request = new GeminiRequest
        {
            Contents = new List<GeminiContent>
            {
                new()
                {
                    Parts = new List<GeminiPart>
                    {
                        new() { Text = "You are a radio assistant on a DCS flight simulator radio frequency. " +
                                        "Respond briefly and clearly, like a professional radio transmission, in English. " +
                                        "Do NOT include any greeting, callsign, or \"this is...\" - just the message content itself, " +
                                        "since that framing is added separately. " +
                                        $"Pilot's message: \"{transcribedText}\"" }
                    }
                }
            }
        };

        var reply = await CallGeminiAsync(request, token);
        return string.IsNullOrWhiteSpace(reply) ? "Understood." : reply.Trim();
    }

    /// <summary>
    /// Transcription AND reply generation in a single Gemini call instead of two separate ones
    /// (TranscribeAsync + GenerateReplyAsync). Halves API usage - important, because the free
    /// daily quota can be fairly tight (see error 429/RESOURCE_EXHAUSTED).
    ///
    /// vocabularyHints (from vocabulary.json) are passed along as a hint for terms that generic
    /// speech recognition tends to mishear (aircraft types, callsigns, ...) - this doesn't
    /// change what the bot understands or can respond to, it just improves transcription
    /// accuracy for those specific words.
    ///
    /// senderCoalitionLabel (e.g. "Blue" or "Red"), if given, is passed along as context so the
    /// reply can naturally reflect who it's talking to. This is purely informational context for
    /// the model - actually deciding whether to respond at all to an opposing coalition is
    /// handled separately (see AppConfig.RestrictToOwnCoalition in BotService), not by the model.
    /// </summary>
    public async Task<(string Transcript, string Reply)> TranscribeAndReplyAsync(
        byte[] pcm16Mono48k, IReadOnlyList<string>? vocabularyHints = null, string? senderCoalitionLabel = null,
        CancellationToken token = default)
    {
        if (pcm16Mono48k.Length == 0) return ("", "I didn't catch that, please repeat.");

        var wavBytes = WavUtils.WrapPcm16AsWav(pcm16Mono48k, sampleRate: 48000, channels: 1);
        var base64Audio = Convert.ToBase64String(wavBytes);

        var vocabularyHint = (vocabularyHints != null && vocabularyHints.Count > 0)
            ? "The audio may contain the following terms (aircraft types, callsigns, or other " +
              "jargon) - if you hear something that sounds like one of these, transcribe it " +
              $"exactly as spelled here: {string.Join(", ", vocabularyHints)}. "
            : "";

        var coalitionHint = string.IsNullOrWhiteSpace(senderCoalitionLabel)
            ? ""
            : $"The pilot transmitting is part of the {senderCoalitionLabel} coalition. ";

        var request = new GeminiRequest
        {
            Contents = new List<GeminiContent>
            {
                new()
                {
                    Parts = new List<GeminiPart>
                    {
                        new() { InlineData = new GeminiInlineData { MimeType = "audio/wav", Data = base64Audio } },
                        new() { Text =
                            "This is a radio transmission from a pilot in a DCS flight simulator. " +
                            "First, transcribe exactly what is said (verbatim). " + vocabularyHint +
                            coalitionHint +
                            "Then, acting as a professional " +
                            "radio assistant, write a brief, clear reply in the style of a real radio transmission, in English. " +
                            "For the reply, only give the message content itself - do NOT include any greeting, " +
                            "callsign, or \"this is...\" framing, since that part is added separately afterwards. " +
                            "If no clear speech is audible, use an empty string for the transcript and " +
                            "\"I didn't catch that, please repeat.\" for the reply. " +
                            "Respond with ONLY a single JSON object, no markdown code fences, no extra text, in exactly this shape: " +
                            "{\"transcript\": \"...\", \"reply\": \"...\"}" }
                    }
                }
            }
        };

        var raw = await CallGeminiAsync(request, token);
        return ParseTranscriptAndReply(raw);
    }

    private static (string Transcript, string Reply) ParseTranscriptAndReply(string raw)
    {
        // Gemini doesn't always strictly follow "no markdown" - strip ```json ... ``` fences.
        var cleaned = raw.Trim();
        if (cleaned.StartsWith("```"))
        {
            var firstNewline = cleaned.IndexOf('\n');
            var lastFence = cleaned.LastIndexOf("```", StringComparison.Ordinal);
            if (firstNewline >= 0 && lastFence > firstNewline)
                cleaned = cleaned[(firstNewline + 1)..lastFence].Trim();
        }

        try
        {
            using var doc = JsonDocument.Parse(cleaned);
            var transcript = doc.RootElement.TryGetProperty("transcript", out var t) ? t.GetString() ?? "" : "";
            var reply = doc.RootElement.TryGetProperty("reply", out var r) ? r.GetString() ?? "" : "";
            if (string.IsNullOrWhiteSpace(reply)) reply = "Understood.";
            return (transcript, reply);
        }
        catch
        {
            Logger.Log($"[Gemini] Could not parse the JSON response, using raw text as the reply: {raw}");
            return ("", string.IsNullOrWhiteSpace(raw) ? "Understood." : raw.Trim());
        }
    }

    /// <summary>
    /// Tries the primary model first, retrying transient errors (429/5xx) up to _maxRetries
    /// times with a short delay between attempts. If the primary model is still failing
    /// afterward and a fallback model is configured (GeminiFallbackModel in config.json), tries
    /// that once before giving up entirely. Non-retryable errors (e.g. 400 bad request, 401/403
    /// auth problems) fail immediately without wasting retries.
    /// </summary>
    private async Task<string> CallGeminiAsync(GeminiRequest request, CancellationToken token)
    {
        var modelsToTry = new List<string> { _model };
        if (_fallbackModel != null && !string.Equals(_fallbackModel, _model, StringComparison.OrdinalIgnoreCase))
            modelsToTry.Add(_fallbackModel);

        for (int modelIndex = 0; modelIndex < modelsToTry.Count; modelIndex++)
        {
            var model = modelsToTry[modelIndex];
            var isLastModel = modelIndex == modelsToTry.Count - 1;

            for (int attempt = 0; attempt <= _maxRetries; attempt++)
            {
                var (success, text, statusCode, body) = await TryCallModelAsync(model, request, token);
                if (success) return text;

                bool isRetryable = RetryableStatusCodes.Contains(statusCode);
                bool hasRetriesLeft = attempt < _maxRetries;

                if (isRetryable && hasRetriesLeft)
                {
                    Logger.Log($"[Gemini] {statusCode} from model '{model}' (attempt {attempt + 1}/{_maxRetries + 1}), retrying in {_retryDelayMs}ms...");
                    await Task.Delay(_retryDelayMs, token);
                    continue;
                }

                var giveUpOnModel = !isRetryable || !hasRetriesLeft;
                if (giveUpOnModel)
                {
                    var nextStep = isLastModel ? "giving up" : $"trying fallback model '{modelsToTry[modelIndex + 1]}'";
                    Logger.Log($"[Gemini] Error {statusCode} from model '{model}': {body} - {nextStep}.");
                    break; // move on to the next model (if any)
                }
            }
        }

        return "";
    }

    private async Task<(bool Success, string Text, int StatusCode, string Body)> TryCallModelAsync(
        string model, GeminiRequest request, CancellationToken token)
    {
        var json = JsonSerializer.Serialize(request);
        using var content = new StringContent(json, Encoding.UTF8);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

        var url = $"v1beta/models/{model}:generateContent?key={_apiKey}";

        using var response = await _http.PostAsync(url, content, token);
        var responseBody = await response.Content.ReadAsStringAsync(token);
        var statusCode = (int)response.StatusCode;

        if (!response.IsSuccessStatusCode)
            return (false, "", statusCode, responseBody);

        var parsed = JsonSerializer.Deserialize<GeminiResponse>(responseBody);
        var text = parsed?.Candidates?.FirstOrDefault()?.Content?.Parts?.FirstOrDefault(p => p.Text != null)?.Text;
        return (true, text ?? "", statusCode, responseBody);
    }
}
