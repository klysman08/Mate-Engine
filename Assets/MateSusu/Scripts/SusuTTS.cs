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
/// Susu's voice for chat replies, plus playback of every clip she says (voice lines too) with simple
/// amplitude-based lip sync on the current VRM avatar. Speech comes from ElevenLabs (default, the same voice as
/// the pre-recorded lines) or from Gemini text-to-speech (free with an AI Studio key), chosen in the AI window.
/// While Susu is in a custom dance nothing is spoken.
/// </summary>
[DefaultExecutionOrder(1000)] // after UniversalBlendshapes / animator writes, before VRM10 applies expressions
public class SusuTTS : MonoBehaviour
{
    public enum Provider { ElevenLabs, Gemini }

    public const string ElevenLabsEndpoint = "https://api.elevenlabs.io/v1/text-to-speech/";
    public const string DefaultElevenVoice = "fUjY9K2nAIwlALOwSiwc"; // Yui (ElevenLabs voice library)
    public const string DefaultElevenModel = "eleven_v4";
    const int ElevenSampleRate = 24000; // pcm_24000 works on every plan
    const int GeminiSampleRate = 24000; // Gemini TTS: 24 kHz, mono, 16-bit
    public const string DefaultGeminiStyle = "a cute, cheerful, affectionate young girl, soft and sweet";

    public static SusuTTS Instance { get; private set; }

    [Header("Config (AI window, else .env)")]
    public bool speakReplies = true;
    public Provider provider = Provider.ElevenLabs;

    [Header("ElevenLabs")]
    public string elevenVoiceId = DefaultElevenVoice;
    [Tooltip("eleven_v4 = best quality; eleven_v4_turbo = faster replies.")]
    public string elevenModel = DefaultElevenModel;
    [Tooltip("Lower = more expressive, higher = steadier delivery.")]
    [Range(0f, 1f)] public float elevenStability = 0.45f;
    [Tooltip("How closely the output sticks to the reference voice.")]
    [Range(0f, 1f)] public float elevenSimilarity = 0.8f;

    [Header("Gemini")]
    public string geminiModel = SusuAiSettings.DefaultGeminiTtsModel;
    public string geminiVoice = SusuAiSettings.DefaultGeminiVoice;
    [Tooltip("How she should sound (Gemini reads it as a style direction).")]
    public string geminiStyle = DefaultGeminiStyle;

    [Range(0f, 1f)] public float volume = 1f;
    [Tooltip("Longer texts are cut at a sentence boundary to keep latency and cost low.")]
    [Min(50)] public int maxChars = 600;

    [Header("Lip sync")]
    public bool lipSync = true;
    [Range(1f, 30f)] public float lipGain = 9f;
    [Range(1f, 40f)] public float lipSmoothing = 18f;

    public bool IsSpeaking => source != null && source.isPlaying;

    string ElevenKey => SusuAiSettings.ElevenLabsKey;
    string GeminiKey => SusuAiSettings.GeminiKey;

    AudioSource source;
    UnityWebRequest current;
    readonly float[] window = new float[256];
    float mouth;
    bool geminiLegacyShape; // older TTS request shape, used after the current one is refused

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

        speakReplies = SusuAiSettings.SpeakReplies;
        SusuAiSettings.Changed += OnSettingsChanged;
        if (PlayerPrefs.HasKey(VolumePref)) volume = Mathf.Clamp01(PlayerPrefs.GetFloat(VolumePref));

        elevenVoiceId = MateSusuEnv.Get("ELEVENLABS_VOICE_ID", elevenVoiceId).Trim();
        elevenStability = Mathf.Clamp01(MateSusuEnv.GetFloat("ELEVENLABS_STABILITY", elevenStability));
        elevenSimilarity = Mathf.Clamp01(MateSusuEnv.GetFloat("ELEVENLABS_SIMILARITY", elevenSimilarity));
        geminiStyle = MateSusuEnv.Get("GEMINI_TTS_STYLE", geminiStyle);
        ApplyVoiceModel();

        source = gameObject.AddComponent<AudioSource>();
        source.playOnAwake = false;
        source.spatialBlend = 0f;

        Debug.Log(provider == Provider.ElevenLabs
            ? $"[SusuTTS] {(speakReplies ? "enabled" : "disabled")} - ElevenLabs model '{elevenModel}', voice '{elevenVoiceId}'"
            : $"[SusuTTS] {(speakReplies ? "enabled" : "disabled")} - Gemini model '{geminiModel}', voice '{geminiVoice}'");
    }

    /// <summary>True when the voice acts out [giggles]/[whispers]-style tags in the text (ElevenLabs v3/v4).</summary>
    public bool UsesAudioTags => provider == Provider.ElevenLabs;

    void OnDestroy()
    {
        SusuAiSettings.Changed -= OnSettingsChanged;
        if (Instance == this) Instance = null;
        Stop();
    }

    void OnSettingsChanged()
    {
        speakReplies = SusuAiSettings.SpeakReplies;
        ApplyVoiceModel();
    }

    /// <summary>Reads "provider:model" and the Gemini voice from the settings. A provider without a key falls back
    /// to the other one.</summary>
    void ApplyVoiceModel()
    {
        string choice = SusuAiSettings.VoiceModel;
        int colon = choice.IndexOf(':');
        bool wantsGemini = choice.StartsWith("gemini", StringComparison.OrdinalIgnoreCase);
        string name = colon >= 0 ? choice.Substring(colon + 1).Trim() : "";
        if (wantsGemini) geminiModel = name.Length > 0 ? name : SusuAiSettings.DefaultGeminiTtsModel;
        else if (name.Length > 0) elevenModel = name;
        geminiVoice = SusuAiSettings.GeminiVoice;

        var before = provider;
        provider = wantsGemini ? Provider.Gemini : Provider.ElevenLabs;
        if (provider == Provider.ElevenLabs && string.IsNullOrEmpty(ElevenKey) && !string.IsNullOrEmpty(GeminiKey)) provider = Provider.Gemini;
        else if (provider == Provider.Gemini && string.IsNullOrEmpty(GeminiKey) && !string.IsNullOrEmpty(ElevenKey)) provider = Provider.ElevenLabs;
        if (provider != before && source != null)
            Debug.Log($"[SusuTTS] Chat voice now {provider} '{(provider == Provider.ElevenLabs ? elevenModel : geminiModel + " / " + geminiVoice)}'");
    }

    public void Stop()
    {
        if (current != null) { current.Abort(); current = null; }
        if (source != null) source.Stop();
    }

    void Update()
    {
        // A custom dance started while she was talking: stop.
        if (IsSpeaking && SusuState.InCustomDance) Stop();
    }

    public bool HasApiKey => !string.IsNullOrEmpty(provider == Provider.ElevenLabs ? ElevenKey : GeminiKey);

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

    /// <summary>Speaks the text if TTS is enabled. Interrupts anything already playing.
    /// Returns false when nothing will be spoken (voice off, no key, empty text, custom dance): onFinished is then never called.</summary>
    public bool Speak(string text, Action onStarted = null, Action onFinished = null)
    {
        if (!speakReplies || !HasApiKey || SusuState.InCustomDance) return false;
        text = CleanForSpeech(text);
        if (text.Length == 0) return false;
        Stop();
        Synthesize(text, IsMostlyJapanese(text), null,
            (pcm, rate) => PlayClip(PcmToClip(pcm, rate), onStarted, onFinished, true),
            error => onFinished?.Invoke()); // no voice this time: still let the caller finish
        return true;
    }

    /// <summary>
    /// Generates speech and returns raw 16-bit mono PCM (little endian) and its sample rate.
    /// extraDirection is added to the delivery style (Gemini only; ElevenLabs takes its acting from [tags] in the text).
    /// </summary>
    public void Synthesize(string text, bool japanese, string extraDirection, Action<byte[], int> onDone, Action<string> onError)
    {
        if (!HasApiKey) { onError?.Invoke($"No {provider} API key"); return; }
        if (provider == Provider.ElevenLabs)
        {
            StartCoroutine(ElevenRoutine(BuildElevenLabsRequest(text, japanese ? "ja" : "en"), onDone, onError));
            return;
        }
        string style = string.Join(", ", new[] { geminiStyle, extraDirection }.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()));
        StartCoroutine(GeminiRoutine(StripAudioTags(text), style, onDone, onError));
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
        req.SetRequestHeader("xi-api-key", ElevenKey);
        return req;
    }

    IEnumerator ElevenRoutine(UnityWebRequest req, Action<byte[], int> onDone, Action<string> onError)
    {
        current = req;
        yield return req.SendWebRequest();
        bool aborted = current != req;
        if (!aborted) current = null;

        byte[] pcm = null;
        try
        {
            if (aborted) yield break;
            if (req.result != UnityWebRequest.Result.Success)
            {
                string err = $"TTS failed ({req.responseCode}): {req.downloadHandler.text}";
                Debug.LogWarning("[ElevenLabs] " + err);
                onError?.Invoke(err);
                yield break;
            }
            pcm = req.downloadHandler.data;
        }
        finally { req.Dispose(); }

        if (pcm == null || pcm.Length < 2) { onError?.Invoke("TTS returned no audio"); yield break; }
        onDone?.Invoke(pcm, ElevenSampleRate);
    }

    /// <summary>Gemini TTS through generateContent. The audio comes back base64 inside the JSON answer, as raw PCM
    /// (or a WAV file, which is unwrapped). If the current request shape is refused, the older one is tried once.</summary>
    IEnumerator GeminiRoutine(string text, string style, Action<byte[], int> onDone, Action<string> onError)
    {
        for (int attempt = 0; attempt < 2; attempt++)
        {
            string body = BuildGeminiBody(text, style, geminiLegacyShape);
            var req = new UnityWebRequest(SusuChat.ApiRoot + UnityWebRequest.EscapeURL(geminiModel) + ":generateContent", UnityWebRequest.kHttpVerbPOST)
            {
                uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(body)),
                downloadHandler = new DownloadHandlerBuffer(),
                timeout = 90
            };
            req.SetRequestHeader("Content-Type", "application/json");
            req.SetRequestHeader("x-goog-api-key", GeminiKey);

            current = req;
            yield return req.SendWebRequest();
            bool aborted = current != req;
            if (!aborted) current = null;

            string answer;
            long code;
            bool ok;
            try
            {
                if (aborted) yield break;
                answer = req.downloadHandler.text;
                code = req.responseCode;
                ok = req.result == UnityWebRequest.Result.Success;
            }
            finally { req.Dispose(); }

            if (!ok)
            {
                if (code == 400 && !geminiLegacyShape)
                {
                    Debug.Log("[SusuTTS] Gemini refused the request shape, trying the older one: " + Short(answer));
                    geminiLegacyShape = true;
                    continue;
                }
                string err = $"TTS failed ({code}): {Short(answer)}";
                Debug.LogWarning("[Gemini TTS] " + err);
                onError?.Invoke(err);
                yield break;
            }

            GeminiAudio reply = null;
            try { reply = JsonUtility.FromJson<GeminiAudio>(answer); } catch (Exception) { }
            var inline = reply?.candidates != null && reply.candidates.Length > 0 && reply.candidates[0].content?.parts != null
                ? reply.candidates[0].content.parts.Select(p => p.inlineData).FirstOrDefault(d => d != null && !string.IsNullOrEmpty(d.data))
                : null;
            if (inline == null) { onError?.Invoke("Gemini TTS returned no audio: " + Short(answer)); yield break; }

            byte[] bytes;
            try { bytes = Convert.FromBase64String(inline.data); }
            catch (FormatException) { onError?.Invoke("Gemini TTS returned bad audio data"); yield break; }

            int rate = ParseRate(inline.mimeType ?? "", GeminiSampleRate);
            byte[] pcm = WavUtil.TryUnwrap(bytes, ref rate) ?? bytes;
            if (pcm.Length < 2) { onError?.Invoke("Gemini TTS returned no audio"); yield break; }
            onDone?.Invoke(pcm, rate);
            yield break;
        }
    }

    string BuildGeminiBody(string text, string style, bool legacy)
    {
        var json = new StringBuilder("{\"contents\":[{\"role\":\"user\",\"parts\":[{\"text\":");
        if (legacy)
        {
            // Older models take the direction as part of the text and a prebuilt voice name.
            json.Append(Json(string.IsNullOrWhiteSpace(style) ? text : $"Say it as {style}: {text}")).Append("}]}]");
            json.Append(",\"generationConfig\":{\"responseModalities\":[\"AUDIO\"],\"speechConfig\":{\"voiceConfig\":{\"prebuiltVoiceConfig\":{\"voiceName\":")
                .Append(Json(geminiVoice)).Append("}}}}}");
            return json.ToString();
        }
        json.Append(Json(text));
        if (!string.IsNullOrWhiteSpace(style)) json.Append(",\"speech_metadata\":{\"style\":").Append(Json(style)).Append('}');
        json.Append("}]}]");
        json.Append(",\"generationConfig\":{\"responseModalities\":[\"AUDIO\"],\"speechConfig\":{\"voiceConfig\":{\"voice\":")
            .Append(Json(geminiVoice)).Append("}}");
        json.Append(",\"responseFormat\":{\"audio\":{\"mimeType\":\"AUDIO_L16\",\"sampleRate\":").Append(GeminiSampleRate).Append("}}}}");
        return json.ToString();
    }

    static string Short(string s) => string.IsNullOrEmpty(s) ? "" : s.Length > 300 ? s.Substring(0, 300) : s;

#pragma warning disable 0649 // filled by JsonUtility
    [Serializable] class GeminiAudio { public GeminiCandidate[] candidates; }
    [Serializable] class GeminiCandidate { public GeminiContent content; }
    [Serializable] class GeminiContent { public GeminiPart[] parts; }
    [Serializable] class GeminiPart { public GeminiInline inlineData; }
    [Serializable] class GeminiInline { public string mimeType; public string data; }
#pragma warning restore 0649

    /// <summary>Plays a clip through Susu's voice source with lip sync and the talking animation.</summary>
    /// <param name="pitchOverride">&gt; 0 plays at this pitch instead of the TTS pitch setting (e.g. 1 for pre-recorded voices).</param>
    public void PlayClip(AudioClip clip, Action onStarted = null, Action onFinished = null, bool destroyWhenDone = false, float pitchOverride = 0f)
    {
        if (clip == null) return;
        if (source.isPlaying) source.Stop();
        StartCoroutine(PlayRoutine(clip, onStarted, onFinished, destroyWhenDone, pitchOverride > 0f ? pitchOverride : 1f));
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

        var clip = AudioClip.Create("SusuTTS", count, 1, rate, false);
        clip.SetData(samples, 0);
        return clip;
    }


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
