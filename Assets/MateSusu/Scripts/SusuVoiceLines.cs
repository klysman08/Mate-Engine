using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

/// <summary>
/// Pre-defined phrases Susu can say. Audio is read from local WAV files so playing a phrase costs nothing:
///   1. StreamingAssets/MateSusu/Voices/&lt;pack&gt;/&lt;id&gt;.wav   (shipped)
///   2. persistentDataPath/MateSusu/VoiceCache/&lt;pack&gt;/&lt;id&gt;.wav (generated once via OpenRouter, then reused)
/// Packs: "ja_elevenlabs" (default; ElevenLabs Eleven v4, Tools/MateSusu/generate_voice_lines_elevenlabs.py)
///        "ja" (older OpenRouter lines, Tools/MateSusu/generate_voice_lines.py). Choose with SUSU_VOICE_PACK in .env.
/// </summary>
public class SusuVoiceLines : MonoBehaviour
{
    public static SusuVoiceLines Instance { get; private set; }

    [Serializable]
    public class Phrase
    {
        public string id;
        public string label;     // shown on the menu button
        public string text;      // what Susu says
        public string direction; // extra acting direction used when generating (OpenRouter packs)
        public string voiceText; // text with ElevenLabs v4 audio tags, used only by the offline generator
        public string learn;     // Japanese deck: the expression being taught (e.g. いただきます)
        public string romaji;    // Japanese deck: its reading
        public string meaning;   // Japanese deck: what it means / when to use it

        public bool IsLesson => !string.IsNullOrEmpty(learn);
        public string category;  // section title in the Voices window
        public string motion;    // body motion(s) while speaking, names from SusuMotionLibrary: "cheer" or "drink,nod"
        public string hours;     // auto-talk window, 24h: "5-11", "22-4", "13-15,22-4" (empty = any time)
        public bool noAuto;      // true = only when the user clicks it

        public bool AllowedAt(int hour)
        {
            if (string.IsNullOrWhiteSpace(hours)) return true;
            foreach (var part in hours.Split(','))
            {
                var ab = part.Split('-');
                if (ab.Length != 2 || !int.TryParse(ab[0].Trim(), out int a) || !int.TryParse(ab[1].Trim(), out int b)) continue;
                if (a <= b ? (hour >= a && hour <= b) : (hour >= a || hour <= b)) return true;
            }
            return false;
        }
    }

#pragma warning disable 0649 // filled by JsonUtility
    [Serializable] class PhraseFile
    {
        public string language;
        public string generator;     // "elevenlabs" = pre-recorded only: never fill gaps with another TTS voice
        public float playbackPitch;  // > 0 = play at this pitch instead of the TTS pitch setting
        public Phrase[] phrases;
    }
#pragma warning restore 0649

    public const string DefaultPack = "ja_elevenlabs";
    const string FallbackPack = "ja";

    public string language = "ja";
    [Tooltip("Folder under StreamingAssets/MateSusu/Voices. Overridden by SUSU_VOICE_PACK in .env.")]
    public string voicePack = DefaultPack;

    bool preRecordedOnly;
    float playbackPitch;

    public IReadOnlyList<Phrase> Phrases => phrases;
    public event Action<Phrase> PhraseStarted;
    public event Action<Phrase> PhraseFinished;

    readonly List<Phrase> phrases = new List<Phrase>();
    readonly Dictionary<string, AudioClip> loaded = new Dictionary<string, AudioClip>();
    readonly HashSet<string> generating = new HashSet<string>();

    string ShippedFolder => PackFolder(voicePack);
    string CacheFolder => Path.Combine(Application.persistentDataPath, "MateSusu", "VoiceCache", voicePack);

    static string PackFolder(string pack) => Path.Combine(Application.streamingAssetsPath, "MateSusu", "Voices", pack);

    void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(this); return; }
        Instance = this;
        voicePack = MateSusuEnv.Get("SUSU_VOICE_PACK", voicePack).Trim();
        if (!File.Exists(Path.Combine(PackFolder(voicePack), "phrases.json")))
        {
            Debug.LogWarning($"[SusuVoice] Voice pack '{voicePack}' not found, using '{FallbackPack}'.");
            voicePack = FallbackPack;
        }
        Load();
    }

    void OnDestroy()
    {
        if (Instance == this) Instance = null;
        foreach (var c in loaded.Values) if (c != null) Destroy(c);
        loaded.Clear();
    }

    public void Load()
    {
        phrases.Clear();
        string path = Path.Combine(ShippedFolder, "phrases.json");
        try
        {
            if (!File.Exists(path)) { Debug.LogWarning("[SusuVoice] Missing " + path); return; }
            var file = JsonUtility.FromJson<PhraseFile>(File.ReadAllText(path));
            if (file?.phrases != null) phrases.AddRange(file.phrases);
            if (!string.IsNullOrEmpty(file?.language)) language = file.language;
            preRecordedOnly = string.Equals(file?.generator, "elevenlabs", StringComparison.OrdinalIgnoreCase);
            playbackPitch = file != null ? file.playbackPitch : 0f;
            Debug.Log($"[SusuVoice] {phrases.Count} '{language}' phrases loaded from pack '{voicePack}'");
        }
        catch (Exception e)
        {
            Debug.LogWarning("[SusuVoice] Could not read phrases: " + e.Message);
        }
    }

    public bool IsAvailableOffline(Phrase p) =>
        loaded.ContainsKey(p.id) || File.Exists(ShippedPath(p)) || File.Exists(CachePath(p));

    public void Play(string id)
    {
        var p = phrases.Find(x => x.id == id);
        if (p != null) Play(p);
    }

    public void Play(Phrase p)
    {
        var tts = OpenRouterTTS.Instance;
        if (p == null || tts == null) return;

        var clip = GetLocalClip(p);
        if (clip != null)
        {
            tts.PlayClip(clip, () => PhraseStarted?.Invoke(p), () => PhraseFinished?.Invoke(p), false, playbackPitch);
            return;
        }

        if (preRecordedOnly)
        {
            Debug.LogWarning($"[SusuVoice] No recording for '{p.id}' in pack '{voicePack}'. Run Tools/MateSusu/generate_voice_lines_elevenlabs.py.");
            return;
        }
        if (!tts.HasApiKey) { Debug.LogWarning($"[SusuVoice] No audio for '{p.id}' and no API key to generate it."); return; }
        if (!generating.Add(p.id)) return; // already on its way

        Debug.Log($"[SusuVoice] Generating '{p.id}' once and caching it locally...");
        tts.Synthesize(p.text, language == "ja", p.direction,
            (pcm, rate) =>
            {
                generating.Remove(p.id);
                try
                {
                    Directory.CreateDirectory(CacheFolder);
                    WavUtil.Write(CachePath(p), pcm, rate);
                }
                catch (Exception e) { Debug.LogWarning("[SusuVoice] Cache write failed: " + e.Message); }

                var c = OpenRouterTTS.PcmToClip(pcm, rate);
                if (c == null) return;
                c.name = p.id;
                loaded[p.id] = c;
                tts.PlayClip(c, () => PhraseStarted?.Invoke(p), () => PhraseFinished?.Invoke(p));
            },
            err => { generating.Remove(p.id); });
    }

    AudioClip GetLocalClip(Phrase p)
    {
        if (loaded.TryGetValue(p.id, out var clip) && clip != null) return clip;
        foreach (var path in new[] { ShippedPath(p), CachePath(p) })
        {
            if (!File.Exists(path)) continue;
            try
            {
                clip = WavUtil.Read(path, p.id);
                if (clip != null) { loaded[p.id] = clip; return clip; }
            }
            catch (Exception e) { Debug.LogWarning($"[SusuVoice] Bad WAV {path}: {e.Message}"); }
        }
        return null;
    }

    string ShippedPath(Phrase p) => Path.Combine(ShippedFolder, p.id + ".wav");
    string CachePath(Phrase p) => Path.Combine(CacheFolder, p.id + ".wav");
}

/// <summary>Minimal 16-bit PCM WAV reader/writer.</summary>
public static class WavUtil
{
    public static void Write(string path, byte[] pcm16Mono, int rate)
    {
        using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write))
        using (var w = new BinaryWriter(fs))
        {
            w.Write(new[] { 'R', 'I', 'F', 'F' });
            w.Write(36 + pcm16Mono.Length);
            w.Write(new[] { 'W', 'A', 'V', 'E', 'f', 'm', 't', ' ' });
            w.Write(16);            // fmt chunk size
            w.Write((short)1);      // PCM
            w.Write((short)1);      // mono
            w.Write(rate);
            w.Write(rate * 2);      // byte rate
            w.Write((short)2);      // block align
            w.Write((short)16);     // bits per sample
            w.Write(new[] { 'd', 'a', 't', 'a' });
            w.Write(pcm16Mono.Length);
            w.Write(pcm16Mono);
        }
    }

    public static AudioClip Read(string path, string name)
    {
        byte[] b = File.ReadAllBytes(path);
        if (b.Length < 44 || b[0] != 'R' || b[1] != 'I' || b[2] != 'F' || b[3] != 'F') return null;

        int channels = 1, rate = 44100, bits = 16, pos = 12;
        int dataStart = -1, dataLen = 0;
        while (pos + 8 <= b.Length)
        {
            string id = System.Text.Encoding.ASCII.GetString(b, pos, 4);
            int size = BitConverter.ToInt32(b, pos + 4);
            if (id == "fmt ")
            {
                channels = BitConverter.ToInt16(b, pos + 10);
                rate = BitConverter.ToInt32(b, pos + 12);
                bits = BitConverter.ToInt16(b, pos + 22);
            }
            else if (id == "data")
            {
                dataStart = pos + 8;
                dataLen = Math.Min(size, b.Length - dataStart);
                break;
            }
            pos += 8 + size + (size & 1);
        }
        if (dataStart < 0 || bits != 16 || channels < 1) return null;

        int frames = dataLen / 2 / channels;
        var samples = new float[frames * channels];
        for (int i = 0; i < samples.Length; i++)
            samples[i] = (short)(b[dataStart + 2 * i] | (b[dataStart + 2 * i + 1] << 8)) / 32768f;

        var clip = AudioClip.Create(name, frames, channels, rate, false);
        clip.SetData(samples, 0);
        return clip;
    }
}
