using System;
using System.Runtime.InteropServices;
using System.Text;
using UnityEngine;

/// <summary>
/// The AI options the user can change in the app (AI window in the radial menu). Each one starts from its
/// .env value and, once changed in the window, is remembered in PlayerPrefs and wins over .env.
/// API keys typed in the window are stored encrypted for the current Windows user (DPAPI), never as plain text.
/// </summary>
public static class SusuAiSettings
{
    const string Prefix = "MateSusu.AI.";

    /// <summary>Raised after any option changes.</summary>
    public static event Action Changed;

    // ---------- Suggested models (the .env / saved value is added to the list when it is not one of these) ----------

    public struct Option
    {
        public string id, label;
        public Option(string id, string label) { this.id = id; this.label = label; }
    }

    public const string DefaultChatModel = "gemini-3.8-flash";
    public const string DefaultGeminiTtsModel = "gemini-3.8-flash-tts";
    public const string DefaultGeminiVoice = "Leda";

    /// <summary>Gemini chat models available on the free tier of Google AI Studio (checked against its docs, Nov 2026).</summary>
    public static readonly Option[] ChatModels =
    {
        new Option("gemini-3.8-flash",      "Gemini 3.8 Flash - smartest, free"),
        new Option("gemini-3.6-flash",      "Gemini 3.6 Flash - stable, free"),
        new Option("gemini-3.5-flash-lite", "Gemini 3.5 Flash-Lite - fastest, free"),
        new Option("gemini-3.1-flash-lite", "Gemini 3.1 Flash-Lite - light, free"),
    };

    /// <summary>Voices for chat replies: "provider:model". The pre-recorded voice lines are not affected.</summary>
    public static readonly Option[] VoiceModels =
    {
        new Option("elevenlabs:eleven_v4",              "ElevenLabs v4 - Susu's voice (default)"),
        new Option("elevenlabs:eleven_v4_turbo",        "ElevenLabs v4 Turbo - faster replies"),
        new Option("gemini:gemini-3.8-flash-tts",       "Gemini 3.8 Flash TTS - free"),
        new Option("gemini:gemini-3.8-flash-lite-tts",  "Gemini 3.8 Flash-Lite TTS - free, faster"),
    };

    /// <summary>A few of Gemini's 30 prebuilt voices that suit Susu.</summary>
    public static readonly Option[] GeminiVoices =
    {
        new Option("Leda",         "Leda - youthful"),
        new Option("Zephyr",       "Zephyr - bright"),
        new Option("Laomedeia",    "Laomedeia - upbeat"),
        new Option("Autonoe",      "Autonoe - bright"),
        new Option("Aoede",        "Aoede - breezy"),
        new Option("Achernar",     "Achernar - soft"),
        new Option("Vindemiatrix", "Vindemiatrix - gentle"),
        new Option("Sulafat",      "Sulafat - warm"),
    };

    // ---------- Chat ----------

    /// <summary>Language Susu answers in: "auto" (follows the user: English or Japanese), "en" or "ja".</summary>
    public static string ReplyLanguage
    {
        get => NormalizeReply(GetString("ReplyLanguage", MateSusuEnv.GetFirst("SUSU_REPLY_LANGUAGE", "OPENROUTER_LANGUAGE") ?? "auto"));
        set => SetString("ReplyLanguage", NormalizeReply(value));
    }

    /// <summary>Gemini model id used for chat.</summary>
    public static string ChatModel
    {
        get
        {
            // Saved by older versions as an OpenRouter id ("openai/gpt-4o-mini"): no longer valid.
            string v = (GetString("GeminiChatModel", MateSusuEnv.Get("GEMINI_CHAT_MODEL", DefaultChatModel)) ?? "").Trim();
            return v.Length > 0 && !v.Contains("/") ? v : DefaultChatModel;
        }
        set => SetString("GeminiChatModel", (value ?? "").Trim());
    }

    // ---------- Voice ----------

    /// <summary>Chat replies are read aloud with Susu's voice.</summary>
    public static bool SpeakReplies
    {
        get => GetBool("SpeakReplies", MateSusuEnv.GetBool("SUSU_SPEAK_REPLIES", MateSusuEnv.GetBool("OPENROUTER_TTS_ENABLED", true)));
        set => SetBool("SpeakReplies", value);
    }

    /// <summary>"elevenlabs:&lt;model&gt;" or "gemini:&lt;model&gt;" for chat replies. ElevenLabs is the default.</summary>
    public static string VoiceModel
    {
        get
        {
            string v = (GetString("VoiceModel", "") ?? "").Trim();
            if (v.StartsWith("elevenlabs:") || v.StartsWith("gemini:")) return v; // older "openrouter:" choices fall through

            // From .env: SUSU_TTS_PROVIDER (elevenlabs | gemini) and that provider's model.
            string provider = MateSusuEnv.Get("SUSU_TTS_PROVIDER", "elevenlabs").Trim().ToLowerInvariant();
            if (provider.StartsWith("gemini"))
                return "gemini:" + MateSusuEnv.Get("GEMINI_TTS_MODEL", DefaultGeminiTtsModel).Trim();
            return "elevenlabs:" + (MateSusuEnv.GetFirst("ELEVENLABS_CHAT_MODEL", "ELEVENLABS_MODEL") ?? "eleven_v4").Trim();
        }
        set => SetString("VoiceModel", (value ?? "").Trim());
    }

    /// <summary>Prebuilt Gemini voice used when the chat voice is Gemini.</summary>
    public static string GeminiVoice
    {
        get
        {
            string v = (GetString("GeminiVoice", MateSusuEnv.Get("GEMINI_TTS_VOICE", DefaultGeminiVoice)) ?? "").Trim();
            return v.Length > 0 ? v : DefaultGeminiVoice;
        }
        set => SetString("GeminiVoice", (value ?? "").Trim());
    }

    /// <summary>Language the user speaks into the microphone: "" (detect), "pt", "en", "ja".</summary>
    public static string MicLanguage
    {
        get => (GetString("MicLanguage", MateSusuEnv.Get("SUSU_STT_LANGUAGE", "")) ?? "").Trim().ToLowerInvariant();
        set => SetString("MicLanguage", (value ?? "").Trim().ToLowerInvariant());
    }

    // ---------- Body ----------

    /// <summary>Auto-talk lines are shown as text over Susu's head.</summary>
    public static bool Balloon
    {
        get => GetBool("Balloon", MateSusuEnv.GetBool("SUSU_BALLOON", true));
        set => SetBool("Balloon", value);
    }

    /// <summary>Susu plays extra motions by herself while idle.</summary>
    public static bool IdleMotions
    {
        get => GetBool("IdleMotions", MateSusuEnv.GetBool("SUSU_IDLE_MOTIONS", true));
        set => SetBool("IdleMotions", value);
    }

    // ---------- API keys ----------

    public enum KeySource { None, Env, Saved }

    static string geminiKey, elevenLabsKey; // decrypted once, kept in memory
    static bool keysLoaded;

    /// <summary>Gemini key from Google AI Studio (chat, Gemini voices, microphone fallback): the one saved in the
    /// app, else .env.</summary>
    public static string GeminiKey
    {
        get
        {
            LoadKeys();
            return !string.IsNullOrEmpty(geminiKey) ? geminiKey : MateSusuEnv.GetFirst("GEMINI_API_KEY", "GOOGLE_API_KEY") ?? "";
        }
        set { LoadKeys(); geminiKey = SaveKey("GeminiKey", value); Changed?.Invoke(); }
    }

    /// <summary>ElevenLabs key (Susu's voice and microphone transcription): the one saved in the app, else .env.</summary>
    public static string ElevenLabsKey
    {
        get
        {
            LoadKeys();
            return !string.IsNullOrEmpty(elevenLabsKey) ? elevenLabsKey : MateSusuEnv.Get("ELEVENLABS_API_KEY") ?? "";
        }
        set { LoadKeys(); elevenLabsKey = SaveKey("ElevenLabsKey", value); Changed?.Invoke(); }
    }

    public static KeySource GeminiKeySource
    {
        get { LoadKeys(); return !string.IsNullOrEmpty(geminiKey) ? KeySource.Saved : GeminiKey.Length > 0 ? KeySource.Env : KeySource.None; }
    }

    public static KeySource ElevenLabsKeySource
    {
        get { LoadKeys(); return !string.IsNullOrEmpty(elevenLabsKey) ? KeySource.Saved : ElevenLabsKey.Length > 0 ? KeySource.Env : KeySource.None; }
    }

    static void LoadKeys()
    {
        if (keysLoaded) return;
        keysLoaded = true;
        geminiKey = Unprotect(PlayerPrefs.GetString(Prefix + "GeminiKey", ""));
        elevenLabsKey = Unprotect(PlayerPrefs.GetString(Prefix + "ElevenLabsKey", ""));
    }

    /// <summary>Stores a key encrypted (empty = forget it) and returns the value now in use.</summary>
    static string SaveKey(string name, string value)
    {
        value = (value ?? "").Trim();
        if (value.Length == 0) { PlayerPrefs.DeleteKey(Prefix + name); PlayerPrefs.Save(); return ""; }
        string sealedText = Protect(value);
        if (string.IsNullOrEmpty(sealedText))
        {
            Debug.LogWarning("[SusuAI] Could not encrypt the key, so it is kept for this session only.");
            return value;
        }
        PlayerPrefs.SetString(Prefix + name, sealedText);
        PlayerPrefs.Save();
        return value;
    }

    // Windows DPAPI: only the same Windows user on this PC can decrypt the saved text.

    [StructLayout(LayoutKind.Sequential)]
    struct Blob { public int size; public IntPtr data; }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool CryptProtectData(ref Blob input, string description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, ref Blob output);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool CryptUnprotectData(ref Blob input, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, ref Blob output);

    [DllImport("kernel32.dll")]
    static extern IntPtr LocalFree(IntPtr handle);

    const int NoPromptUi = 1; // CRYPTPROTECT_UI_FORBIDDEN

    /// <summary>Encrypts text for the current Windows user; returns base64, or "" when it fails.</summary>
    public static string Protect(string plain)
    {
        if (string.IsNullOrEmpty(plain)) return "";
        byte[] result = Crypt(Encoding.UTF8.GetBytes(plain), true);
        return result != null ? Convert.ToBase64String(result) : "";
    }

    /// <summary>Reverses <see cref="Protect"/>; returns "" when the text cannot be decrypted (other user, other PC).</summary>
    public static string Unprotect(string sealedText)
    {
        if (string.IsNullOrEmpty(sealedText)) return "";
        try
        {
            byte[] result = Crypt(Convert.FromBase64String(sealedText), false);
            return result != null ? Encoding.UTF8.GetString(result) : "";
        }
        catch (FormatException) { return ""; }
    }

    static byte[] Crypt(byte[] bytes, bool protect)
    {
        var input = new Blob { size = bytes.Length, data = Marshal.AllocHGlobal(bytes.Length) };
        var output = new Blob();
        try
        {
            Marshal.Copy(bytes, 0, input.data, bytes.Length);
            bool ok = protect
                ? CryptProtectData(ref input, "MateSusu", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, NoPromptUi, ref output)
                : CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, NoPromptUi, ref output);
            if (!ok || output.data == IntPtr.Zero) return null;
            var result = new byte[output.size];
            Marshal.Copy(output.data, result, 0, output.size);
            return result;
        }
        catch (Exception e) // crypt32 missing (not Windows)
        {
            Debug.LogWarning("[SusuAI] Key encryption unavailable: " + e.Message);
            return null;
        }
        finally
        {
            Marshal.FreeHGlobal(input.data);
            if (output.data != IntPtr.Zero) LocalFree(output.data);
        }
    }

    // ---------- Storage helpers ----------

    static string NormalizeReply(string v)
    {
        switch ((v ?? "").Trim().ToLowerInvariant())
        {
            case "en": case "english": return "en";
            case "ja": case "jp": case "japanese": return "ja";
            default: return "auto";
        }
    }

    static string GetString(string key, string fallback) =>
        PlayerPrefs.HasKey(Prefix + key) ? PlayerPrefs.GetString(Prefix + key) : fallback;

    static bool GetBool(string key, bool fallback) =>
        PlayerPrefs.HasKey(Prefix + key) ? PlayerPrefs.GetInt(Prefix + key) == 1 : fallback;

    static void SetString(string key, string value)
    {
        PlayerPrefs.SetString(Prefix + key, value);
        Changed?.Invoke();
    }

    static void SetBool(string key, bool value)
    {
        PlayerPrefs.SetInt(Prefix + key, value ? 1 : 0);
        Changed?.Invoke();
    }
}
