using System;
using System.Collections;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;
using UnityEngine.Networking;
using UniVRM10;
using VRM;

/// <summary>
/// Susu's voice. Speech comes from ElevenLabs (same voice as the pre-recorded lines, default when
/// ELEVENLABS_API_KEY is set) or OpenRouter's /audio/speech endpoint (fish-audio/drama-3), chosen with
/// SUSU_TTS_PROVIDER. Played as 16-bit PCM with simple amplitude-based lip sync on the current VRM avatar.
/// </summary>
[DefaultExecutionOrder(1000)] // after UniversalBlendshapes / animator writes, before VRM10 applies expressions
public class OpenRouterTTS : MonoBehaviour
{
    public enum Provider { OpenRouter, ElevenLabs }

    public const string Endpoint = "https://openrouter.ai/api/v1/audio/speech";
    public const string ElevenLabsEndpoint = "https://api.elevenlabs.io/v1/text-to-speech/";
    public const string DefaultElevenVoice = "fUjY9K2nAIwlALOwSiwc"; // Yui (ElevenLabs voice library)
    public const string DefaultElevenModel = "eleven_v4";
    const int ElevenSampleRate = 24000; // pcm_24000 works on every plan
    public const string DefaultModel = "fish-audio/drama-3";
    // Fish Audio: voice = a Fish "reference_id" (empty = provider default voice), delivery via [bracket] directions.
    // Kokoro (hexgrad/kokoro-82m) also works: voices af_heart / jf_alpha etc.
    public const string DefaultVoiceEn = "";
    public const string DefaultVoiceJa = "";
    public const string DefaultInstructions = "cute, cheerful, affectionate young girl, soft and sweet";
    const int PcmSampleRate = 24000; // fallback when the Content-Type has no rate (Fish sends 44100, Kokoro 24000)

    public static OpenRouterTTS Instance { get; private set; }

    [Header("Config (filled from .env at startup)")]
    public bool speakReplies = true;
    [Tooltip("SUSU_TTS_PROVIDER = elevenlabs | openrouter (default: elevenlabs when ELEVENLABS_API_KEY is set).")]
    public Provider provider = Provider.OpenRouter;

    [Header("ElevenLabs")]
    public string elevenVoiceId = DefaultElevenVoice;
    [Tooltip("ELEVENLABS_CHAT_MODEL. eleven_v4 = best quality; eleven_v4_turbo = faster replies.")]
    public string elevenModel = DefaultElevenModel;
    [Tooltip("Lower = more expressive, higher = steadier delivery.")]
    [Range(0f, 1f)] public float elevenStability = 0.45f;
    [Tooltip("How closely the output sticks to the reference voice.")]
    [Range(0f, 1f)] public float elevenSimilarity = 0.8f;

    [Header("OpenRouter")]
    public string model = DefaultModel;
    [Tooltip("Voice used for English replies.")]
    public string voiceEn = DefaultVoiceEn;
    [Tooltip("Voice used when the reply is mostly Japanese text.")]
    public string voiceJa = DefaultVoiceJa;
    [Tooltip("Delivery direction (tone/emotion). Fish Audio turns it into a [direction] before the text; OpenAI/Gemini TTS also honour it.")]
    public string instructions = DefaultInstructions;
    [Tooltip("Only sent when different from 1 (many providers reject a non-default speed).")]
    [Range(0.5f, 2f)] public float speed = 1f;
    [Tooltip("Playback pitch. Slightly above 1 makes the voice brighter (also a bit faster).")]
    [Range(0.8f, 1.3f)] public float pitch = 1f;
    [Range(0f, 1f)] public float volume = 1f;
    [Tooltip("Longer texts are cut at a sentence boundary to keep latency and cost low.")]
    [Min(50)] public int maxChars = 600;

    [Header("Lip sync")]
    public bool lipSync = true;
    [Range(1f, 30f)] public float lipGain = 9f;
    [Range(1f, 40f)] public float lipSmoothing = 18f;

    public bool IsSpeaking => source != null && source.isPlaying;

    string apiKey;
    string elevenKey;
    AudioSource source;
    UnityWebRequest current;
    readonly float[] window = new float[256];
    float mouth;

    // Current avatar
    GameObject avatarRoot;
    VRMBlendShapeProxy proxy0;
    Vrm10Instance vrm1;
    Animator avatarAnimator;
    VRMLoader loader;
    float nextAvatarCheck;

    void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(this); return; }
        Instance = this;

        apiKey = MateSusuEnv.GetFirst("OPENROUTER_API_KEY", "API_KEY");
        speakReplies = MateSusuEnv.GetBool("OPENROUTER_TTS_ENABLED", true);
        model = MateSusuEnv.Get("OPENROUTER_TTS_MODEL", DefaultModel);
        voiceEn = MateSusuEnv.Get("OPENROUTER_TTS_VOICE_EN", DefaultVoiceEn);
        voiceJa = MateSusuEnv.Get("OPENROUTER_TTS_VOICE_JA", DefaultVoiceJa);
        instructions = MateSusuEnv.Get("OPENROUTER_TTS_INSTRUCTIONS", DefaultInstructions);
        speed = MateSusuEnv.GetFloat("OPENROUTER_TTS_SPEED", speed);
        pitch = MateSusuEnv.GetFloat("OPENROUTER_TTS_PITCH", pitch);
        volume = MateSusuEnv.GetFloat("OPENROUTER_TTS_VOLUME", volume);
        if (PlayerPrefs.HasKey(VolumePref)) volume = Mathf.Clamp01(PlayerPrefs.GetFloat(VolumePref));

        elevenKey = MateSusuEnv.Get("ELEVENLABS_API_KEY");
        elevenVoiceId = MateSusuEnv.Get("ELEVENLABS_VOICE_ID", elevenVoiceId).Trim();
        elevenModel = MateSusuEnv.GetFirst("ELEVENLABS_CHAT_MODEL", "ELEVENLABS_MODEL") ?? elevenModel;
        elevenStability = Mathf.Clamp01(MateSusuEnv.GetFloat("ELEVENLABS_STABILITY", elevenStability));
        elevenSimilarity = Mathf.Clamp01(MateSusuEnv.GetFloat("ELEVENLABS_SIMILARITY", elevenSimilarity));

        bool hasEleven = !string.IsNullOrEmpty(elevenKey);
        string wanted = MateSusuEnv.Get("SUSU_TTS_PROVIDER", hasEleven ? "elevenlabs" : "openrouter").Trim().ToLowerInvariant();
        provider = wanted == "elevenlabs" || wanted == "eleven" ? Provider.ElevenLabs : Provider.OpenRouter;
        if (provider == Provider.ElevenLabs && !hasEleven)
        {
            Debug.LogWarning("[SusuTTS] SUSU_TTS_PROVIDER is elevenlabs but ELEVENLABS_API_KEY is missing; using OpenRouter.");
            provider = Provider.OpenRouter;
        }

        source = gameObject.AddComponent<AudioSource>();
        source.playOnAwake = false;
        source.spatialBlend = 0f;

        if (provider == Provider.ElevenLabs)
            Debug.Log($"[SusuTTS] {(speakReplies ? "enabled" : "disabled")} - ElevenLabs model '{elevenModel}', voice '{elevenVoiceId}', stability {elevenStability}, similarity {elevenSimilarity}");
        else
            Debug.Log($"[SusuTTS] {(speakReplies ? "enabled" : "disabled")} - OpenRouter model '{model}', voices en='{Show(voiceEn)}' ja='{Show(voiceJa)}', direction '{instructions}', pitch {pitch}");
    }

    /// <summary>True when the voice acts out [giggles]/[whispers]-style tags in the text (ElevenLabs v3/v4).</summary>
    public bool UsesAudioTags => provider == Provider.ElevenLabs;

    // ElevenLabs output already has the voice's natural pitch; the pitch setting is for OpenRouter voices.
    float PlaybackPitch => provider == Provider.ElevenLabs ? 1f : pitch;

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

    public bool HasApiKey => !string.IsNullOrEmpty(provider == Provider.ElevenLabs ? elevenKey : apiKey);

    const string VolumePref = "MateSusu.VoiceVolume";

    /// <summary>Susu's voice volume (chat replies and voice lines). Saved between sessions.</summary>
    public float Volume
    {
        get => volume;
        set
        {
            volume = Mathf.Clamp01(value);
            if (source != null) source.volume = volume;
            PlayerPrefs.SetFloat(VolumePref, volume);
        }
    }

    /// <summary>Speaks the text if TTS is enabled. Interrupts anything already playing.</summary>
    public void Speak(string text, Action onStarted = null, Action onFinished = null)
    {
        if (!speakReplies || !HasApiKey) return;
        text = CleanForSpeech(text);
        if (text.Length == 0) return;
        Stop();
        Synthesize(text, IsMostlyJapanese(text), null,
            (pcm, rate) => PlayClip(PcmToClip(pcm, rate), onStarted, onFinished, true),
            null);
    }

    /// <summary>
    /// Generates speech and returns raw 16-bit mono PCM (little endian) and its sample rate.
    /// extraDirection is appended to the configured delivery instructions (OpenRouter only; ElevenLabs
    /// takes its acting from [tags] inside the text).
    /// </summary>
    public void Synthesize(string text, bool japanese, string extraDirection, Action<byte[], int> onDone, Action<string> onError)
    {
        if (!HasApiKey) { onError?.Invoke($"No {provider} API key in .env"); return; }
        if (provider == Provider.ElevenLabs)
        {
            StartCoroutine(SendRoutine(BuildElevenLabsRequest(text, japanese ? "ja" : "en"), ElevenSampleRate, "ElevenLabs", onDone, onError));
            return;
        }
        string direction = string.Join(", ", new[] { instructions, extraDirection }
            .Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()));
        StartCoroutine(SendRoutine(BuildOpenRouterRequest(text, japanese ? voiceJa : voiceEn, direction), PcmSampleRate, "OpenRouter", onDone, onError));
    }

    UnityWebRequest BuildElevenLabsRequest(string text, string languageCode)
    {
        var ci = System.Globalization.CultureInfo.InvariantCulture;
        var json = new StringBuilder("{");
        json.Append("\"text\":").Append(Json(text));
        json.Append(",\"model_id\":").Append(Json(elevenModel));
        json.Append(",\"language_code\":").Append(Json(languageCode));
        json.Append(",\"voice_settings\":{\"stability\":").Append(elevenStability.ToString("0.###", ci))
            .Append(",\"similarity_boost\":").Append(elevenSimilarity.ToString("0.###", ci)).Append('}');
        json.Append('}');

        string url = ElevenLabsEndpoint + UnityWebRequest.EscapeURL(elevenVoiceId) + "?output_format=pcm_" + ElevenSampleRate;
        var req = new UnityWebRequest(url, UnityWebRequest.kHttpVerbPOST)
        {
            uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(json.ToString())),
            downloadHandler = new DownloadHandlerBuffer(),
            timeout = 60
        };
        req.SetRequestHeader("Content-Type", "application/json");
        req.SetRequestHeader("xi-api-key", elevenKey);
        return req;
    }

    UnityWebRequest BuildOpenRouterRequest(string text, string voice, string direction)
    {
        // Built by hand so optional fields are omitted instead of sent empty.
        var json = new StringBuilder("{");
        json.Append("\"model\":").Append(Json(model));
        json.Append(",\"input\":").Append(Json(text));
        json.Append(",\"response_format\":\"pcm\"");
        if (!string.IsNullOrWhiteSpace(voice)) json.Append(",\"voice\":").Append(Json(voice.Trim()));
        if (!string.IsNullOrWhiteSpace(direction)) json.Append(",\"instructions\":").Append(Json(direction.Trim()));
        if (Mathf.Abs(speed - 1f) > 0.001f)
            json.Append(",\"speed\":").Append(speed.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
        json.Append('}');

        var req = new UnityWebRequest(Endpoint, UnityWebRequest.kHttpVerbPOST)
        {
            uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(json.ToString())),
            downloadHandler = new DownloadHandlerBuffer(),
            timeout = 60
        };
        req.SetRequestHeader("Content-Type", "application/json");
        req.SetRequestHeader("Authorization", "Bearer " + apiKey);
        req.SetRequestHeader("HTTP-Referer", "https://github.com/klysman08/Mate-Engine");
        req.SetRequestHeader("X-Title", "MateSusu");
        return req;
    }

    IEnumerator SendRoutine(UnityWebRequest req, int defaultRate, string label, Action<byte[], int> onDone, Action<string> onError)
    {
        current = req;
        yield return req.SendWebRequest();
        bool aborted = current != req;
        if (!aborted) current = null;

        byte[] pcm = null;
        int rate = defaultRate;
        try
        {
            if (aborted) yield break;
            if (req.result != UnityWebRequest.Result.Success)
            {
                string err = $"TTS failed ({req.responseCode}): {req.downloadHandler.text}";
                Debug.LogWarning($"[{label}] " + err);
                onError?.Invoke(err);
                yield break;
            }
            rate = ParseRate(req.GetResponseHeader("Content-Type") ?? "", defaultRate);
            pcm = req.downloadHandler.data;
        }
        finally
        {
            req.Dispose();
        }

        if (pcm == null || pcm.Length < 2) { onError?.Invoke("TTS returned no audio"); yield break; }
        onDone?.Invoke(pcm, rate);
    }

    /// <summary>Plays a clip through Susu's voice source with lip sync and the talking animation.</summary>
    /// <param name="pitchOverride">&gt; 0 plays at this pitch instead of the TTS pitch setting (e.g. 1 for pre-recorded voices).</param>
    public void PlayClip(AudioClip clip, Action onStarted = null, Action onFinished = null, bool destroyWhenDone = false, float pitchOverride = 0f)
    {
        if (clip == null) return;
        if (source.isPlaying) source.Stop();
        StartCoroutine(PlayRoutine(clip, onStarted, onFinished, destroyWhenDone, pitchOverride > 0f ? pitchOverride : PlaybackPitch));
    }

    IEnumerator PlayRoutine(AudioClip clip, Action onStarted, Action onFinished, bool destroyWhenDone, float playPitch)
    {
        source.clip = clip;
        source.volume = volume;
        source.pitch = playPitch;
        source.Play();
        SetTalking(true);
        onStarted?.Invoke();

        while (source != null && source.isPlaying && source.clip == clip) yield return null;

        bool stillOurs = source != null && source.clip == clip;
        if (stillOurs) { SetTalking(false); source.clip = null; }
        onFinished?.Invoke();
        if (destroyWhenDone) Destroy(clip);
    }

    void SetTalking(bool on)
    {
        RefreshAvatar();
        if (avatarAnimator != null && HasParam(avatarAnimator, IsTalkingHash)) avatarAnimator.SetBool(IsTalkingHash, on);
    }

    static readonly int IsTalkingHash = Animator.StringToHash("isTalking");

    static bool HasParam(Animator a, int hash)
    {
        foreach (var p in a.parameters) if (p.nameHash == hash) return true;
        return false;
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
        avatarAnimator = found != null ? found.GetComponentInChildren<Animator>(true) : null;
    }

    // ---------- Helpers ----------

    static readonly Regex MdLink = new Regex(@"\[([^\]]*)\]\([^)]*\)", RegexOptions.Compiled);
    static readonly Regex Emoji = new Regex(@"[\uD800-\uDBFF][\uDC00-\uDFFF]|[☀-➿]", RegexOptions.Compiled);
    static readonly Regex Spaces = new Regex(@"\s+", RegexOptions.Compiled);
    // [giggles], [softly], [happy gasp]... (not markdown links) and a tag still being streamed at the end.
    static readonly Regex AudioTag = new Regex(@"\[[A-Za-z][A-Za-z ,'\-]{0,40}\](?!\()\s*|\[[A-Za-z][A-Za-z ,'\-]{0,40}$", RegexOptions.Compiled);

    /// <summary>Removes [audio tags] for display (chat bubble); the voice still acts them out.</summary>
    public static string StripAudioTags(string text) =>
        string.IsNullOrEmpty(text) ? text : AudioTag.Replace(text, "").Trim();

    string CleanForSpeech(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        text = MdLink.Replace(text, "$1");
        text = Emoji.Replace(text, " ");
        text = text.Replace("*", "").Replace("_", " ").Replace("#", "").Replace("`", "").Replace("~", "");
        text = Spaces.Replace(text, " ").Trim();

        if (text.Length > maxChars)
        {
            int cut = text.LastIndexOfAny(new[] { '.', '!', '?', '。', '！', '？' }, maxChars - 1);
            text = cut > maxChars / 3 ? text.Substring(0, cut + 1) : text.Substring(0, maxChars);
        }
        return text;
    }

    /// <summary>True when at least 30% of the letters are kana/kanji.</summary>
    public static bool IsMostlyJapanese(string text)
    {
        int jp = 0, letters = 0;
        foreach (char c in text)
        {
            bool isJp = (c >= '぀' && c <= 'ヿ')   // hiragana + katakana
                     || (c >= '一' && c <= '鿿')   // CJK ideographs
                     || (c >= 'ｦ' && c <= 'ﾟ');  // half-width katakana
            if (isJp) { jp++; letters++; }
            else if (char.IsLetter(c)) letters++;
        }
        return letters > 0 && jp >= letters * 0.3f;
    }

    static int ParseRate(string contentType, int fallback)
    {
        var m = Regex.Match(contentType, @"rate=(\d+)");
        return m.Success && int.TryParse(m.Groups[1].Value, out var r) ? r : fallback;
    }

    public static AudioClip PcmToClip(byte[] pcm, int rate)
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

    static string Show(string v) => string.IsNullOrWhiteSpace(v) ? "(default)" : v;

    static string Json(string s)
    {
        var sb = new StringBuilder("\"");
        foreach (char c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                    else sb.Append(c);
                    break;
            }
        }
        return sb.Append('"').ToString();
    }
}
