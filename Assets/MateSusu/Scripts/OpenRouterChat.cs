using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

/// <summary>
/// Streaming chat client for OpenRouter (OpenAI-compatible /chat/completions).
/// Created automatically at startup when a key is found in .env, and kept alive across scenes.
/// </summary>
public class OpenRouterChat : MonoBehaviour
{
    public const string Endpoint = "https://openrouter.ai/api/v1/chat/completions";
    public const string DefaultModel = "openai/gpt-4o-mini";

    public static OpenRouterChat Instance { get; private set; }

    [Header("Config (filled from .env at startup)")]
    public string model = DefaultModel;
    [Range(0f, 2f)] public float temperature = 0.8f;
    [Min(16)] public int maxTokens = 400;
    [Tooltip("How many previous messages (user + assistant) are sent as context.")]
    [Min(0)] public int historyLimit = 20;

    [Header("Character")]
    [Tooltip("Resources/<name>.txt holding the character persona (system prompt).")]
    public string personaResource = "SusuPersona";
    [Tooltip("auto = mirror the user's language (English or Japanese only), en = English, ja = Japanese")]
    public string language = "auto";

    [Header("Runtime")]
    [SerializeField] bool active;

    string apiKey;
    readonly List<Msg> history = new List<Msg>();
    UnityWebRequest current;

    public bool IsActive => active && !string.IsNullOrEmpty(apiKey);
    public bool IsBusy => current != null;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    static void Bootstrap()
    {
        if (Instance != null) return;
        var key = MateSusuEnv.GetFirst("OPENROUTER_API_KEY", "API_KEY");
        if (string.IsNullOrEmpty(key))
        {
            Debug.Log("[OpenRouter] No API key in .env - using the local LLM.");
            return;
        }

        var go = new GameObject("MateSusu OpenRouter");
        DontDestroyOnLoad(go);
        var chat = go.AddComponent<OpenRouterChat>();
        chat.Configure(key);
        go.AddComponent<OpenRouterTTS>();
    }

    void Configure(string key)
    {
        apiKey = key;
        model = MateSusuEnv.Get("OPENROUTER_CHAT_MODEL", DefaultModel);
        temperature = MateSusuEnv.GetFloat("OPENROUTER_TEMPERATURE", temperature);
        maxTokens = Mathf.RoundToInt(MateSusuEnv.GetFloat("OPENROUTER_MAX_TOKENS", maxTokens));
        active = MateSusuEnv.GetBool("OPENROUTER_CHAT_ENABLED", true);
        language = MateSusuEnv.Get("OPENROUTER_LANGUAGE", language).Trim().ToLowerInvariant();
        Debug.Log($"[OpenRouter] Chat {(active ? "enabled" : "disabled")} - model '{model}', language '{language}' (config: {MateSusuEnv.LoadedFrom})");
    }

    /// <summary>Persona from Resources plus the language rule. Falls back to the given prompt if the persona is missing.</summary>
    public string BuildSystemPrompt(string fallbackPrompt)
    {
        var persona = Resources.Load<TextAsset>(personaResource);
        string basePrompt = persona != null ? persona.text.Trim() : fallbackPrompt;
        string rule = LanguageRule(language);
        if (string.IsNullOrWhiteSpace(basePrompt)) return rule;
        return basePrompt + "\n\nLanguage:\n- " + rule;
    }

    static string LanguageRule(string lang)
    {
        switch (lang)
        {
            case "en":
            case "english":
                return "Always reply in English, even if the user writes in another language.";
            case "ja":
            case "jp":
            case "japanese":
                return "Always reply in Japanese (日本語), even if the user writes in another language.";
            default:
                return "Reply in English or Japanese only. If the user's latest message is in Japanese, reply in Japanese; " +
                       "otherwise (English, Portuguese or anything else) reply in English. Never mix both languages in one reply.";
        }
    }

    void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;
    }

    void OnDestroy()
    {
        if (Instance == this) Instance = null;
        Cancel();
    }

    public void ClearHistory() => history.Clear();

    /// <summary>
    /// Sends a user message. onPartial receives the full text accumulated so far.
    /// </summary>
    public void Chat(string message, string systemPrompt, Action<string> onPartial, Action<string> onComplete, Action<string> onError = null)
    {
        if (!IsActive) { onError?.Invoke("OpenRouter is not configured."); return; }
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
        var messages = new List<Msg>();
        if (!string.IsNullOrWhiteSpace(systemPrompt)) messages.Add(new Msg("system", systemPrompt));
        int start = Mathf.Max(0, history.Count - historyLimit);
        for (int i = start; i < history.Count; i++) messages.Add(history[i]);
        messages.Add(new Msg("user", message));

        var body = new ChatRequest
        {
            model = model,
            stream = true,
            temperature = temperature,
            max_tokens = maxTokens,
            messages = messages.ToArray()
        };

        var handler = new SseHandler(onPartial);
        var req = new UnityWebRequest(Endpoint, UnityWebRequest.kHttpVerbPOST)
        {
            uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(JsonUtility.ToJson(body))),
            downloadHandler = handler,
            timeout = 120
        };
        req.SetRequestHeader("Content-Type", "application/json");
        req.SetRequestHeader("Authorization", "Bearer " + apiKey);
        req.SetRequestHeader("HTTP-Referer", "https://github.com/klysman08/Mate-Engine");
        req.SetRequestHeader("X-Title", "MateSusu");

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
                Debug.LogWarning($"[OpenRouter] Chat failed ({req.responseCode}): {err}");
                onError?.Invoke(err);
                yield break;
            }

            string reply = handler.Text.Trim();
            history.Add(new Msg("user", message));
            history.Add(new Msg("assistant", reply));
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
            var e = JsonUtility.FromJson<ErrorEnvelope>(raw);
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
            if (pending.Count > 0)
            {
                if (HandleLine(Encoding.UTF8.GetString(pending.ToArray()).TrimEnd('\r')))
                    onPartial?.Invoke(buffer.ToString());
                pending.Clear();
            }
        }

        bool HandleLine(string line)
        {
            if (line.Length == 0 || line.StartsWith(":")) return false; // keep-alive comments
            if (!line.StartsWith("data:"))
            {
                raw.AppendLine(line); // non-SSE body, e.g. a JSON error
                return false;
            }

            string payload = line.Substring(5).Trim();
            if (payload == "[DONE]") return false;

            try
            {
                var chunk = JsonUtility.FromJson<StreamChunk>(payload);
                if (chunk?.error != null && !string.IsNullOrEmpty(chunk.error.message))
                {
                    Error = chunk.error.message;
                    return false;
                }
                var delta = chunk?.choices != null && chunk.choices.Length > 0 ? chunk.choices[0].delta?.content : null;
                if (string.IsNullOrEmpty(delta)) return false;
                buffer.Append(delta);
                return true;
            }
            catch (Exception e)
            {
                Debug.LogWarning("[OpenRouter] Bad stream chunk: " + e.Message);
                return false;
            }
        }
    }

    // ---------- JSON shapes (filled by JsonUtility) ----------
#pragma warning disable 0649

    [Serializable] public class Msg { public string role; public string content; public Msg(string r, string c) { role = r; content = c; } }
    [Serializable] class ChatRequest { public string model; public bool stream; public float temperature; public int max_tokens; public Msg[] messages; }
    [Serializable] class StreamChunk { public Choice[] choices; public ApiError error; }
    [Serializable] class Choice { public Delta delta; public string finish_reason; }
    [Serializable] class Delta { public string content; }
    [Serializable] class ApiError { public string message; public int code; }
    [Serializable] class ErrorEnvelope { public ApiError error; }
#pragma warning restore 0649
}
