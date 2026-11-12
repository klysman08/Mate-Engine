using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

/// <summary>
/// Streaming chat with Google Gemini (generateContent REST API, server-sent events). A free API key comes from
/// Google AI Studio. The key and model come from SusuAiSettings (AI window, else .env), so both can change while
/// the app runs.
/// </summary>
public class SusuChat : MonoBehaviour
{
    public const string ApiRoot = "https://generativelanguage.googleapis.com/v1beta/models/";

    public static SusuChat Instance { get; private set; }

    [Tooltip("Room for the reply (and the model's thinking, which counts against it).")]
    [Min(64)] public int maxTokens = 1024;
    [Tooltip("How many previous messages (user + assistant) are sent as context.")]
    [Min(0)] public int historyLimit = 20;

    [Header("Character")]
    [Tooltip("Resources/<name>.txt holding the character persona (system prompt).")]
    public string personaResource = "SusuPersona";

    /// <summary>The model used for the last message (for logs and tests).</summary>
    public string model;

    readonly List<Msg> history = new List<Msg>();
    UnityWebRequest current;

    static string ApiKey => SusuAiSettings.GeminiKey;

    /// <summary>True when there is a key to chat with.</summary>
    public bool HasKey => !string.IsNullOrEmpty(ApiKey);
    public bool IsBusy => current != null;
    /// <summary>True when the last failed message failed because a Gemini quota was used up.</summary>
    public bool LastErrorWasQuota { get; private set; }

    void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(this); return; }
        Instance = this;
        model = SusuAiSettings.ChatModel;
        Debug.Log($"[SusuChat] Gemini model '{model}', language '{SusuAiSettings.ReplyLanguage}', key {(HasKey ? "found" : "missing")} (config: {MateSusuEnv.LoadedFrom})");
    }

    void OnDestroy()
    {
        if (Instance == this) Instance = null;
        Cancel();
    }

    /// <summary>Persona from Resources plus the language rule. Falls back to the given prompt if the persona is missing.</summary>
    public string BuildSystemPrompt(string fallbackPrompt)
    {
        var persona = Resources.Load<TextAsset>(personaResource);
        string basePrompt = persona != null ? persona.text.Trim() : fallbackPrompt;
        string rule = LanguageRule(SusuAiSettings.ReplyLanguage);
        string prompt = string.IsNullOrWhiteSpace(basePrompt) ? rule : basePrompt + "\n\nLanguage:\n- " + rule;
        var tts = SusuTTS.Instance;
        if (tts != null && tts.speakReplies && tts.UsesAudioTags) prompt += "\n\n" + VoiceRule;
        return prompt;
    }

    // ElevenLabs v4 acts out English [tags] inside the text; the chat bubble hides them.
    const string VoiceRule =
        "Voice:\n" +
        "- Your replies are spoken aloud by an expressive voice actor. To sound natural, you may put a short English " +
        "audio tag in square brackets right before a phrase, for example [giggles], [softly], [whispers], [sighs], " +
        "[excited], [curious], [pouting], [shy], [happy gasp].\n" +
        "- Use at most two tags per reply, only when they fit the feeling, and use … or commas for small pauses and breaths.\n" +
        "- Never use square brackets for anything else.";

    static string LanguageRule(string lang)
    {
        switch (lang)
        {
            case "en":
                return "Always reply in English, even if the user writes in another language.";
            case "ja":
                return "Always reply in Japanese (日本語), even if the user writes in another language.";
            default:
                return "Reply in English or Japanese only. If the user's latest message is in Japanese, reply in Japanese; " +
                       "otherwise (English, Portuguese or anything else) reply in English. Never mix both languages in one reply.";
        }
    }

    public void ClearHistory() => history.Clear();

    /// <summary>Sends a user message. onPartial receives the full text accumulated so far.</summary>
    public void Chat(string message, string systemPrompt, Action<string> onPartial, Action<string> onComplete, Action<string> onError = null)
    {
        if (!HasKey) { onError?.Invoke("No Gemini API key yet. Add a free one from Google AI Studio in the AI settings window."); return; }
        Cancel();
        StartCoroutine(ChatRoutine(message, systemPrompt, onPartial, onComplete, onError));
    }

    public void Cancel()
    {
        if (current == null) return;
        current.Abort();
        current = null;
    }

    IEnumerator ChatRoutine(string message, string systemPrompt, Action<string> onPartial, Action<string> onComplete, Action<string> onError)
    {
        model = SusuAiSettings.ChatModel; // may have been changed in the AI window since the last message
        LastErrorWasQuota = false;

        var contents = new List<OutContent>();
        int start = Mathf.Max(0, history.Count - historyLimit);
        for (int i = start; i < history.Count; i++) contents.Add(OutContent.Of(history[i].role, history[i].content));
        contents.Add(OutContent.Of("user", message));

        var body = new Request
        {
            contents = contents.ToArray(),
            generationConfig = new GenerationConfig { maxOutputTokens = maxTokens },
        };
        // JsonUtility cannot leave out an empty object, so the optional system instruction is spliced in by hand.
        string json = JsonUtility.ToJson(body);
        if (!string.IsNullOrWhiteSpace(systemPrompt))
            json = "{\"system_instruction\":" + JsonUtility.ToJson(new SystemContent { parts = new[] { new OutPart { text = systemPrompt } } }) + "," + json.Substring(1);

        var handler = new SseHandler(onPartial);
        var req = new UnityWebRequest(ApiRoot + UnityWebRequest.EscapeURL(model) + ":streamGenerateContent?alt=sse", UnityWebRequest.kHttpVerbPOST)
        {
            uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(json)),
            downloadHandler = handler,
            timeout = 120
        };
        req.SetRequestHeader("Content-Type", "application/json");
        req.SetRequestHeader("x-goog-api-key", ApiKey);

        current = req;
        yield return req.SendWebRequest();
        bool aborted = current != req;
        if (!aborted) current = null;

        try
        {
            if (aborted) yield break;

            if (req.result != UnityWebRequest.Result.Success || handler.Error != null)
            {
                string err = handler.Error ?? ExtractError(handler.RawText) ?? req.error;
                if (GeminiQuota.IsQuotaError(req.responseCode, handler.RawText + " " + handler.Error))
                {
                    LastErrorWasQuota = true;
                    onError?.Invoke(GeminiQuota.Report(GeminiQuota.Service.Chat, model, handler.RawText + " " + handler.Error));
                    yield break;
                }
                Debug.LogWarning($"[SusuChat] Gemini failed ({req.responseCode}): {err}");
                onError?.Invoke(err);
                yield break;
            }

            string reply = handler.Text.Trim();
            if (reply.Length == 0)
            {
                string why = handler.FinishReason ?? "empty reply";
                Debug.LogWarning("[SusuChat] Gemini returned no text: " + why);
                onError?.Invoke("Susu had nothing to say (" + why + ").");
                yield break;
            }
            history.Add(new Msg("user", message));
            history.Add(new Msg("model", reply));
            onComplete?.Invoke(reply);
        }
        finally
        {
            req.Dispose();
        }
    }

    static string ExtractError(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return null;
        try
        {
            var e = JsonUtility.FromJson<ErrorEnvelope>(raw.TrimStart('['));
            if (e?.error != null && !string.IsNullOrEmpty(e.error.message)) return e.error.message;
        }
        catch { }
        return raw.Length > 300 ? raw.Substring(0, 300) : raw;
    }

    // ---------- Server-Sent Events parser ----------

    class SseHandler : DownloadHandlerScript
    {
        readonly Action<string> onPartial;
        readonly List<byte> pending = new List<byte>();
        readonly StringBuilder buffer = new StringBuilder();
        readonly StringBuilder raw = new StringBuilder();

        public string Text => buffer.ToString();
        public string RawText => raw.ToString();
        public string Error { get; private set; }
        public string FinishReason { get; private set; }

        public SseHandler(Action<string> onPartial) : base(new byte[4096]) { this.onPartial = onPartial; }

        protected override bool ReceiveData(byte[] data, int dataLength)
        {
            if (data == null || dataLength == 0) return true;
            bool changed = false;
            for (int i = 0; i < dataLength; i++)
            {
                if (data[i] != (byte)'\n') { pending.Add(data[i]); continue; }
                changed |= HandleLine(Encoding.UTF8.GetString(pending.ToArray()).TrimEnd('\r'));
                pending.Clear();
            }
            if (changed) onPartial?.Invoke(buffer.ToString());
            return true;
        }

        protected override void CompleteContent()
        {
            if (pending.Count == 0) return;
            if (HandleLine(Encoding.UTF8.GetString(pending.ToArray()).TrimEnd('\r'))) onPartial?.Invoke(buffer.ToString());
            pending.Clear();
        }

        bool HandleLine(string line)
        {
            if (line.Length == 0 || line.StartsWith(":")) return false;
            if (!line.StartsWith("data:"))
            {
                raw.AppendLine(line); // non-SSE body, e.g. a JSON error
                return false;
            }
            try
            {
                var chunk = JsonUtility.FromJson<Response>(line.Substring(5).Trim());
                if (chunk?.error != null && !string.IsNullOrEmpty(chunk.error.message)) { Error = chunk.error.message; return false; }
                if (chunk?.candidates == null || chunk.candidates.Length == 0) return false;
                var cand = chunk.candidates[0];
                if (!string.IsNullOrEmpty(cand.finishReason)) FinishReason = cand.finishReason;
                if (cand.content?.parts == null) return false;
                bool changed = false;
                foreach (var part in cand.content.parts)
                {
                    if (part == null || part.thought || string.IsNullOrEmpty(part.text)) continue; // skip thinking summaries
                    buffer.Append(part.text);
                    changed = true;
                }
                return changed;
            }
            catch (Exception e)
            {
                Debug.LogWarning("[SusuChat] Bad stream chunk: " + e.Message);
                return false;
            }
        }
    }

    // ---------- JSON shapes (JsonUtility) ----------
#pragma warning disable 0649

    [Serializable] public class Msg { public string role; public string content; public Msg(string r, string c) { role = r; content = c; } }

    // Sent
    [Serializable] class OutPart { public string text; }
    [Serializable] class OutContent
    {
        public string role;
        public OutPart[] parts;
        public static OutContent Of(string role, string text) => new OutContent { role = role, parts = new[] { new OutPart { text = text } } };
    }
    [Serializable] class SystemContent { public OutPart[] parts; }
    [Serializable] class GenerationConfig { public int maxOutputTokens; }
    [Serializable] class Request { public OutContent[] contents; public GenerationConfig generationConfig; }

    // Received
    [Serializable] class Part { public string text; public bool thought; }
    [Serializable] class Content { public string role; public Part[] parts; }
    [Serializable] class Candidate { public Content content; public string finishReason; }
    [Serializable] class Response { public Candidate[] candidates; public ApiError error; }
    [Serializable] class ApiError { public string message; public int code; public string status; }
    [Serializable] class ErrorEnvelope { public ApiError error; }
#pragma warning restore 0649
}
