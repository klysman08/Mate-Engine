using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

/// <summary>
/// Microphone for the chat: records what the user says and turns it into text. Recording stops by itself
/// after a short silence (or when toggled again). The chat window owns the button and decides what to do
/// with the text. Speech-to-text uses ElevenLabs Scribe when there is an ElevenLabs key, otherwise an
/// audio-capable OpenRouter model. .env: SUSU_STT_PROVIDER (elevenlabs | openrouter), ELEVENLABS_STT_MODEL,
/// OPENROUTER_STT_MODEL, SUSU_MIC_DEVICE; the spoken language is set in the AI window (or SUSU_STT_LANGUAGE).
/// </summary>
public class SusuChatMic : MonoBehaviour
{
    public static SusuChatMic Instance { get; private set; }

    const string ElevenEndpoint = "https://api.elevenlabs.io/v1/speech-to-text";
    const int SampleRate = 16000;

    public enum State { Idle, Listening, Transcribing }

    [Tooltip("Longest recording, in seconds.")]
    public int maxSeconds = 30;
    [Tooltip("Microphone level (RMS, 0-1) above this counts as speech.")]
    public float speechLevel = 0.015f;
    [Tooltip("Silence after speech that ends the recording.")]
    public float endSilence = 1.4f;
    [Tooltip("Give up when nobody speaks for this long.")]
    public float noSpeechTimeout = 8f;

    public State Current { get; private set; } = State.Idle;
    /// <summary>The last transcription (for debugging and tests).</summary>
    public string LastTranscript { get; private set; }

    public event Action<State> StateChanged;
    /// <summary>Text heard (never empty).</summary>
    public event Action<string> Heard;
    /// <summary>Short message for the user: no microphone, nothing heard, transcription failed.</summary>
    public event Action<string> Problem;

    string elevenModel, openRouterModel, device, wantedProvider;

    AudioClip recording;
    float startedAt, lastVoiceAt;
    bool heardVoice;
    readonly float[] levelWindow = new float[1024];
    UnityWebRequest request;

    string ElevenKey => SusuAiSettings.ElevenLabsKey;
    string OpenRouterKey => SusuAiSettings.OpenRouterKey;
    bool UseEleven => ElevenKey.Length > 0 && (wantedProvider.StartsWith("eleven") || OpenRouterKey.Length == 0);

    /// <summary>True when there is a key for speech-to-text.</summary>
    public bool Available => ElevenKey.Length > 0 || OpenRouterKey.Length > 0;

    void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(this); return; }
        Instance = this;

        elevenModel = MateSusuEnv.Get("ELEVENLABS_STT_MODEL", "scribe_v1");
        openRouterModel = MateSusuEnv.Get("OPENROUTER_STT_MODEL", "google/gemini-2.5-flash");
        wantedProvider = MateSusuEnv.Get("SUSU_STT_PROVIDER", "elevenlabs").Trim().ToLowerInvariant();
        device = MateSusuEnv.Get("SUSU_MIC_DEVICE", "");
        if (string.IsNullOrWhiteSpace(device)) device = null; // Windows default microphone

        if (Available) Debug.Log($"[SusuMic] Speech-to-text: {(UseEleven ? "ElevenLabs " + elevenModel : "OpenRouter " + openRouterModel)}");
    }

    void OnDestroy()
    {
        StopRecording();
        if (request != null) { request.Abort(); request = null; }
        if (Instance == this) Instance = null;
    }

    void Update()
    {
        if (Current != State.Listening) return;

        float level = MicLevel();
        float now = Time.unscaledTime;
        if (level > speechLevel) { heardVoice = true; lastVoiceAt = now; }

        bool full = now - startedAt >= maxSeconds - 0.2f || !Microphone.IsRecording(device);
        if (heardVoice && (now - lastVoiceAt >= endSilence || full)) Finish();
        else if (!heardVoice && (now - startedAt >= noSpeechTimeout || full)) Cancel();
    }

    void SetState(State s)
    {
        if (Current == s) return;
        Current = s;
        StateChanged?.Invoke(s);
    }

    // ---------- Recording ----------

    /// <summary>Start listening, or - while listening - stop and send what was said.</summary>
    public void Toggle()
    {
        if (Current == State.Idle) StartListening();
        else if (Current == State.Listening) { if (heardVoice) Finish(); else Cancel(); }
        // Transcribing: wait for the answer
    }

    void StartListening()
    {
        if (!Available) { Problem?.Invoke("Add an API key in AI settings to use the microphone"); return; }
        if (Microphone.devices.Length == 0) { Debug.LogWarning("[SusuMic] No microphone found."); Problem?.Invoke("No microphone found"); return; }

        // Her own voice would be picked up by the microphone.
        if (OpenRouterTTS.Instance != null && OpenRouterTTS.Instance.IsSpeaking) OpenRouterTTS.Instance.Stop();

        recording = Microphone.Start(device, false, maxSeconds, SampleRate);
        if (recording == null) { Debug.LogWarning("[SusuMic] Could not start the microphone."); Problem?.Invoke("Microphone unavailable"); return; }
        startedAt = lastVoiceAt = Time.unscaledTime;
        heardVoice = false;
        SetState(State.Listening);
    }

    float MicLevel()
    {
        if (recording == null) return 0f;
        int pos = Microphone.GetPosition(device);
        if (pos < levelWindow.Length) return 0f;
        recording.GetData(levelWindow, pos - levelWindow.Length);
        float sum = 0f;
        for (int i = 0; i < levelWindow.Length; i++) sum += levelWindow[i] * levelWindow[i];
        return Mathf.Sqrt(sum / levelWindow.Length);
    }

    /// <summary>Stops the microphone and returns what was recorded as WAV bytes (null when nothing was).</summary>
    byte[] StopRecording()
    {
        if (recording == null) return null;
        int pos = Microphone.GetPosition(device);
        bool wasRecording = Microphone.IsRecording(device);
        Microphone.End(device);
        int count = wasRecording ? pos : recording.samples; // a full buffer stops by itself and reports position 0
        byte[] wav = null;
        if (count > SampleRate / 4)
        {
            var samples = new float[count];
            recording.GetData(samples, 0);
            wav = WavUtil.ToBytes(samples, count, SampleRate);
        }
        Destroy(recording);
        recording = null;
        return wav;
    }

    /// <summary>Stop listening and throw the recording away.</summary>
    public void Cancel()
    {
        if (Current != State.Listening) return;
        StopRecording();
        SetState(State.Idle);
    }

    void Finish()
    {
        var wav = StopRecording();
        if (wav == null) { SetState(State.Idle); return; }
        SetState(State.Transcribing);
        Transcribe(wav, text =>
        {
            SetState(State.Idle);
            LastTranscript = text;
            if (string.IsNullOrWhiteSpace(text)) { Problem?.Invoke("I could not hear that"); return; }
            Debug.Log("[SusuMic] Heard: " + text);
            Heard?.Invoke(text);
        },
        error =>
        {
            SetState(State.Idle);
            Debug.LogWarning("[SusuMic] Transcription failed: " + error);
            Problem?.Invoke("Transcription failed");
        });
    }

    // ---------- Speech-to-text ----------

    /// <summary>Turns a WAV file into text with the configured provider.</summary>
    public void Transcribe(byte[] wav, Action<string> onText, Action<string> onError)
    {
        if (!Available) { onError?.Invoke("No speech-to-text key"); return; }
        StartCoroutine(UseEleven ? ElevenRoutine(wav, onText, onError) : OpenRouterRoutine(wav, onText, onError));
    }

    IEnumerator ElevenRoutine(byte[] wav, Action<string> onText, Action<string> onError)
    {
        var form = new List<IMultipartFormSection>
        {
            new MultipartFormDataSection("model_id", elevenModel),
            new MultipartFormDataSection("tag_audio_events", "false"), // no "(laughs)" in the message
            new MultipartFormFileSection("file", wav, "speech.wav", "audio/wav"),
        };
        string language = SusuAiSettings.MicLanguage;
        if (language.Length > 0) form.Add(new MultipartFormDataSection("language_code", language));

        using (var req = UnityWebRequest.Post(ElevenEndpoint, form))
        {
            req.SetRequestHeader("xi-api-key", ElevenKey);
            req.timeout = 60;
            request = req;
            yield return req.SendWebRequest();
            request = null;

            string body = req.downloadHandler != null ? req.downloadHandler.text : "";
            if (req.result != UnityWebRequest.Result.Success) { onError?.Invoke($"{req.responseCode} {req.error} {Short(body)}"); yield break; }
            SttReply reply = null;
            try { reply = JsonUtility.FromJson<SttReply>(body); } catch (Exception) { }
            if (reply == null) { onError?.Invoke("Unexpected answer: " + Short(body)); yield break; }
            onText?.Invoke((reply.text ?? "").Trim());
        }
    }

    IEnumerator OpenRouterRoutine(byte[] wav, Action<string> onText, Action<string> onError)
    {
        string language = SusuAiSettings.MicLanguage;
        string hint = language.Length > 0 ? $" The speech is in language '{language}'." : "";
        var json = new StringBuilder();
        json.Append("{\"model\":").Append(Quote(openRouterModel)).Append(",\"temperature\":0,\"messages\":[{\"role\":\"user\",\"content\":[");
        json.Append("{\"type\":\"text\",\"text\":").Append(Quote(
            "Transcribe this audio exactly as spoken, in the language it is spoken in. Reply with the transcription only: " +
            "no quotes, no comments, no translation. If there is no speech, reply with nothing." + hint)).Append("},");
        json.Append("{\"type\":\"input_audio\",\"input_audio\":{\"data\":\"").Append(Convert.ToBase64String(wav)).Append("\",\"format\":\"wav\"}}");
        json.Append("]}]}");

        using (var req = new UnityWebRequest(OpenRouterChat.Endpoint, UnityWebRequest.kHttpVerbPOST))
        {
            req.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(json.ToString()));
            req.downloadHandler = new DownloadHandlerBuffer();
            req.SetRequestHeader("Content-Type", "application/json");
            req.SetRequestHeader("Authorization", "Bearer " + OpenRouterKey);
            req.SetRequestHeader("X-Title", "MateSusu");
            req.timeout = 60;
            request = req;
            yield return req.SendWebRequest();
            request = null;

            string body = req.downloadHandler.text;
            if (req.result != UnityWebRequest.Result.Success) { onError?.Invoke($"{req.responseCode} {req.error} {Short(body)}"); yield break; }
            ChatReply reply = null;
            try { reply = JsonUtility.FromJson<ChatReply>(body); } catch (Exception) { }
            if (reply?.choices == null || reply.choices.Length == 0 || reply.choices[0].message == null) { onError?.Invoke("Unexpected answer: " + Short(body)); yield break; }
            onText?.Invoke((reply.choices[0].message.content ?? "").Trim());
        }
    }

    static string Short(string s) => string.IsNullOrEmpty(s) ? "" : s.Length > 300 ? s.Substring(0, 300) : s;

    static string Quote(string s)
    {
        var sb = new StringBuilder("\"");
        foreach (char c in s ?? "")
        {
            if (c == '"' || c == '\\') sb.Append('\\').Append(c);
            else if (c == '\n') sb.Append("\\n");
            else if (c < ' ') sb.Append(' ');
            else sb.Append(c);
        }
        return sb.Append('"').ToString();
    }

#pragma warning disable 0649 // filled by JsonUtility
    [Serializable] class SttReply { public string text; public string language_code; }
    [Serializable] class ChatReply { public ChatChoice[] choices; }
    [Serializable] class ChatChoice { public ChatMessage message; }
    [Serializable] class ChatMessage { public string content; }
#pragma warning restore 0649
}
