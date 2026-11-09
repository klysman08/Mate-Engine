using System;
using System.Collections;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;
using UnityEngine.Networking;
using UniVRM10;
using VRM;

/// <summary>
/// Text-to-speech through OpenRouter's /audio/speech endpoint (default: hexgrad/kokoro-82m),
/// played as 16-bit PCM with simple amplitude-based lip sync on the current VRM avatar.
/// </summary>
[DefaultExecutionOrder(1000)] // after UniversalBlendshapes / animator writes, before VRM10 applies expressions
public class OpenRouterTTS : MonoBehaviour
{
    public const string Endpoint = "https://openrouter.ai/api/v1/audio/speech";
    public const string DefaultModel = "hexgrad/kokoro-82m";
    public const string DefaultVoice = "pf_dora"; // Kokoro Brazilian Portuguese female (pm_alex = male)
    const int PcmSampleRate = 24000;              // Kokoro returns audio/pcm;rate=24000;channels=1

    public static OpenRouterTTS Instance { get; private set; }

    [Header("Config (filled from .env at startup)")]
    public bool speakReplies = true;
    public string model = DefaultModel;
    public string voice = DefaultVoice;
    [Range(0.5f, 2f)] public float speed = 1f;
    [Range(0f, 1f)] public float volume = 1f;
    [Tooltip("Longer texts are cut at a sentence boundary to keep latency and cost low.")]
    [Min(50)] public int maxChars = 600;

    [Header("Lip sync")]
    public bool lipSync = true;
    [Range(1f, 30f)] public float lipGain = 9f;
    [Range(1f, 40f)] public float lipSmoothing = 18f;

    public bool IsSpeaking => source != null && source.isPlaying;

    string apiKey;
    AudioSource source;
    UnityWebRequest current;
    readonly float[] window = new float[256];
    float mouth;

    // Current avatar
    GameObject avatarRoot;
    VRMBlendShapeProxy proxy0;
    Vrm10Instance vrm1;
    VRMLoader loader;
    float nextAvatarCheck;

    void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(this); return; }
        Instance = this;

        apiKey = MateSusuEnv.GetFirst("OPENROUTER_API_KEY", "API_KEY");
        speakReplies = MateSusuEnv.GetBool("OPENROUTER_TTS_ENABLED", true);
        model = MateSusuEnv.Get("OPENROUTER_TTS_MODEL", DefaultModel);
        voice = MateSusuEnv.Get("OPENROUTER_TTS_VOICE", DefaultVoice);
        speed = MateSusuEnv.GetFloat("OPENROUTER_TTS_SPEED", speed);
        volume = MateSusuEnv.GetFloat("OPENROUTER_TTS_VOLUME", volume);

        source = gameObject.AddComponent<AudioSource>();
        source.playOnAwake = false;
        source.spatialBlend = 0f;

        Debug.Log($"[OpenRouter] TTS {(speakReplies ? "enabled" : "disabled")} - model '{model}', voice '{voice}'");
    }

    void OnDestroy()
    {
        if (Instance == this) Instance = null;
        Stop();
    }

    public void Stop()
    {
        if (current != null) { current.Abort(); current = null; }
        if (source != null) source.Stop();
    }

    /// <summary>Speaks the text if TTS is enabled. Interrupts anything already playing.</summary>
    public void Speak(string text, Action onStarted = null, Action onFinished = null)
    {
        if (!speakReplies || string.IsNullOrEmpty(apiKey)) return;
        text = CleanForSpeech(text);
        if (text.Length == 0) return;
        Stop();
        StartCoroutine(SpeakRoutine(text, onStarted, onFinished));
    }

    IEnumerator SpeakRoutine(string text, Action onStarted, Action onFinished)
    {
        var body = new SpeechRequest
        {
            model = model,
            input = text,
            voice = voice,
            response_format = "pcm",
            speed = speed
        };

        var req = new UnityWebRequest(Endpoint, UnityWebRequest.kHttpVerbPOST)
        {
            uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(JsonUtility.ToJson(body))),
            downloadHandler = new DownloadHandlerBuffer(),
            timeout = 60
        };
        req.SetRequestHeader("Content-Type", "application/json");
        req.SetRequestHeader("Authorization", "Bearer " + apiKey);
        req.SetRequestHeader("HTTP-Referer", "https://github.com/klysman08/Mate-Engine");
        req.SetRequestHeader("X-Title", "MateSusu");

        current = req;
        yield return req.SendWebRequest();
        bool aborted = current != req;
        if (!aborted) current = null;

        AudioClip clip = null;
        try
        {
            if (aborted) yield break;
            if (req.result != UnityWebRequest.Result.Success)
            {
                Debug.LogWarning($"[OpenRouter] TTS failed ({req.responseCode}): {req.downloadHandler.text}");
                yield break;
            }

            string type = req.GetResponseHeader("Content-Type") ?? "";
            int rate = ParseRate(type, PcmSampleRate);
            clip = PcmToClip(req.downloadHandler.data, rate);
            if (clip == null) { Debug.LogWarning("[OpenRouter] TTS returned no audio (" + type + ")"); yield break; }
        }
        finally
        {
            req.Dispose();
        }

        source.clip = clip;
        source.volume = volume;
        source.Play();
        onStarted?.Invoke();

        while (source != null && source.isPlaying && source.clip == clip) yield return null;

        onFinished?.Invoke();
        if (source != null && source.clip == clip) source.clip = null;
        Destroy(clip);
    }

    // ---------- Lip sync ----------

    void LateUpdate()
    {
        float target = 0f;
        if (lipSync && IsSpeaking)
        {
            source.GetOutputData(window, 0);
            float sum = 0f;
            for (int i = 0; i < window.Length; i++) sum += window[i] * window[i];
            float rms = Mathf.Sqrt(sum / window.Length);
            target = Mathf.Clamp01(rms * lipGain);
        }

        float prev = mouth;
        mouth = Mathf.Lerp(mouth, target, 1f - Mathf.Exp(-lipSmoothing * Time.deltaTime));
        if (mouth < 0.01f && prev < 0.01f) return; // idle: leave expressions to the rest of the app

        RefreshAvatar();
        if (vrm1 != null && vrm1.Runtime != null)
            vrm1.Runtime.Expression.SetWeight(ExpressionKey.Aa, mouth);
        else if (proxy0 != null)
            proxy0.ImmediatelySetValue(BlendShapeKey.CreateFromPreset(BlendShapePreset.A), mouth);
    }

    void RefreshAvatar()
    {
        if (avatarRoot != null && Time.unscaledTime < nextAvatarCheck) return;
        nextAvatarCheck = Time.unscaledTime + 1f;

        if (loader == null) loader = FindFirstObjectByType<VRMLoader>();
        GameObject found = loader != null ? loader.GetCurrentModel() : null;
        if (found == null)
        {
            var parent = GameObject.Find("Model");
            if (parent != null)
            {
                var anim = parent.GetComponentInChildren<Animator>(false);
                if (anim != null) found = anim.gameObject;
            }
        }
        if (found == avatarRoot) return;

        avatarRoot = found;
        vrm1 = found != null ? found.GetComponentInChildren<Vrm10Instance>(true) : null;
        proxy0 = found != null && vrm1 == null ? found.GetComponentInChildren<VRMBlendShapeProxy>(true) : null;
    }

    // ---------- Helpers ----------

    static readonly Regex MdLink = new Regex(@"\[([^\]]*)\]\([^)]*\)", RegexOptions.Compiled);
    static readonly Regex Emoji = new Regex(@"[\uD800-\uDBFF][\uDC00-\uDFFF]|[☀-➿]", RegexOptions.Compiled);
    static readonly Regex Spaces = new Regex(@"\s+", RegexOptions.Compiled);

    string CleanForSpeech(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        text = MdLink.Replace(text, "$1");
        text = Emoji.Replace(text, " ");
        text = text.Replace("*", "").Replace("_", " ").Replace("#", "").Replace("`", "").Replace("~", "");
        text = Spaces.Replace(text, " ").Trim();

        if (text.Length > maxChars)
        {
            int cut = text.LastIndexOfAny(new[] { '.', '!', '?' }, maxChars - 1);
            text = cut > maxChars / 3 ? text.Substring(0, cut + 1) : text.Substring(0, maxChars);
        }
        return text;
    }

    static int ParseRate(string contentType, int fallback)
    {
        var m = Regex.Match(contentType, @"rate=(\d+)");
        return m.Success && int.TryParse(m.Groups[1].Value, out var r) ? r : fallback;
    }

    static AudioClip PcmToClip(byte[] pcm, int rate)
    {
        if (pcm == null || pcm.Length < 2) return null;
        int count = pcm.Length / 2;
        var samples = new float[count];
        for (int i = 0; i < count; i++)
            samples[i] = (short)(pcm[2 * i] | (pcm[2 * i + 1] << 8)) / 32768f;

        var clip = AudioClip.Create("OpenRouterTTS", count, 1, rate, false);
        clip.SetData(samples, 0);
        return clip;
    }

    [Serializable] class SpeechRequest { public string model; public string input; public string voice; public string response_format; public float speed; }
}
