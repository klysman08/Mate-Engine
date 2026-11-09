using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

/// <summary>
/// Pre-defined phrases Susu can say. Audio is read from local WAV files so playing a phrase costs nothing:
///   1. StreamingAssets/MateSusu/Voices/&lt;lang&gt;/&lt;id&gt;.wav   (shipped, see Tools/MateSusu/generate_voice_lines.py)
///   2. persistentDataPath/MateSusu/VoiceCache/&lt;lang&gt;/&lt;id&gt;.wav (generated once via OpenRouter, then reused)
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
        public string direction; // extra acting direction used when generating
    }

#pragma warning disable 0649 // filled by JsonUtility
    [Serializable] class PhraseFile { public string language; public Phrase[] phrases; }
#pragma warning restore 0649

    public string language = "ja";

    public IReadOnlyList<Phrase> Phrases => phrases;
    public event Action<Phrase> PhraseStarted;
    public event Action<Phrase> PhraseFinished;

    readonly List<Phrase> phrases = new List<Phrase>();
    readonly Dictionary<string, AudioClip> loaded = new Dictionary<string, AudioClip>();
    readonly HashSet<string> generating = new HashSet<string>();

    string ShippedFolder => Path.Combine(Application.streamingAssetsPath, "MateSusu", "Voices", language);
    string CacheFolder => Path.Combine(Application.persistentDataPath, "MateSusu", "VoiceCache", language);

    void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(this); return; }
        Instance = this;
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
            Debug.Log($"[SusuVoice] {phrases.Count} '{language}' phrases loaded");
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
            tts.PlayClip(clip, () => PhraseStarted?.Invoke(p), () => PhraseFinished?.Invoke(p));
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
