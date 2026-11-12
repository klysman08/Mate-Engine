using System;
using System.Text.RegularExpressions;
using UnityEngine;

/// <summary>
/// Recognises Gemini's "quota exceeded" answers (HTTP 429 / RESOURCE_EXHAUSTED, common on the free tier) and turns
/// them into a short, friendly warning that says which limit was hit and what to do. The chat window shows it.
/// </summary>
public static class GeminiQuota
{
    public enum Service { Chat, Voice, Microphone }

    /// <summary>Raised with the warning text whenever a quota is reached (at most once a minute per service and kind).</summary>
    public static event Action<Service, string> Reached;

    static readonly Regex RetryDelay = new Regex("\"retryDelay\"\\s*:\\s*\"(\\d+(?:\\.\\d+)?)s\"", RegexOptions.Compiled);
    static string lastKey;
    static float lastAt = -999f;

    public static bool IsQuotaError(long httpCode, string body) =>
        httpCode == 429 || (!string.IsNullOrEmpty(body) && body.Contains("RESOURCE_EXHAUSTED"));

    /// <summary>Builds the warning, logs it and raises <see cref="Reached"/>. Returns the warning text.</summary>
    public static string Report(Service service, string model, string body)
    {
        string message = Describe(service, model, body, out string kind);
        Debug.LogWarning("[Gemini quota] " + message);

        string key = service + kind;
        if (key != lastKey || Time.unscaledTime - lastAt > 60f)
        {
            lastKey = key;
            lastAt = Time.unscaledTime;
            Reached?.Invoke(service, message);
        }
        return message;
    }

    /// <summary>The warning text for a quota error body. <paramref name="kind"/> is "day", "minute" or "other".</summary>
    public static string Describe(Service service, string model, string body, out string kind)
    {
        body = body ?? "";
        string what = service == Service.Chat ? "chat" : service == Service.Voice ? "voice" : "microphone";
        string name = string.IsNullOrWhiteSpace(model) ? "this model" : model;
        string alternative = service == Service.Voice
            ? "choose the ElevenLabs voice in AI settings"
            : service == Service.Microphone
                ? "add an ElevenLabs key in AI settings to transcribe with it"
                : "pick another chat model in AI settings";

        if (body.IndexOf("PerDay", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            kind = "day";
            return $"Gemini's free daily limit for {name} ({what}) is used up. It resets at midnight Pacific time; meanwhile you can {alternative}.";
        }

        var retry = RetryDelay.Match(body);
        bool perMinute = body.IndexOf("PerMinute", StringComparison.OrdinalIgnoreCase) >= 0;
        if (perMinute || retry.Success)
        {
            kind = "minute";
            int seconds = retry.Success ? Mathf.CeilToInt(float.Parse(retry.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)) : 60;
            return $"Too many Gemini requests for {name} ({what}): the free per-minute limit was reached. Try again in about {seconds} seconds.";
        }

        kind = "other";
        return $"Gemini's free quota for {name} ({what}) was reached. Wait a little and try again, or {alternative}.";
    }
}
