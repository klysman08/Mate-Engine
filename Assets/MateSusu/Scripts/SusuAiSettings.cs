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

    public const string DefaultChatModel = "openai/gpt-4o-mini";

    /// <summary>Chat models on OpenRouter worth choosing for a talking companion (checked against its model list, Nov 2026).</summary>
    public static readonly Option[] ChatModels =
    {
        new Option("openai/gpt-6-luna",             "GPT-6 Luna - fast, low cost"),
        new Option("anthropic/claude-haiku-5.5",    "Claude Haiku 5.5 - fast, natural character"),
        new Option("google/gemini-3.8-flash",       "Gemini 3.8 Flash - fast, strong Japanese"),
        new Option("deepseek/deepseek-v4.1-flash",  "DeepSeek V4.1 Flash - cheapest"),
        new Option("anthropic/claude-sonnet-5.5",   "Claude Sonnet 5.5 - best personality"),
        new Option("openai/gpt-6.1-terra",          "GPT-6.1 Terra - smart all-rounder"),
        new Option("x-ai/grok-4.7",                 "Grok 4.7 - playful, less filtered"),
        new Option("openai/gpt-4o-mini",            "GPT-4o mini - classic"),
    };

    /// <summary>Voices for chat replies: "provider:model". The pre-recorded voice lines are not affected.</summary>
    public static readonly Option[] VoiceModels =
    {
        new Option("elevenlabs:eleven_v4",          "ElevenLabs v4 - most expressive"),
        new Option("elevenlabs:eleven_v4_turbo",    "ElevenLabs v4 Turbo - faster replies"),
        new Option("openrouter:fish-audio/drama-3", "Fish Audio Drama 3 - via OpenRouter"),
        new Option("openrouter:hexgrad/kokoro-82m", "Kokoro 82M - via OpenRouter, cheapest"),
    };

    // ---------- Chat ----------

    /// <summary>Language Susu answers in: "auto" (follows the user: English or Japanese), "en" or "ja".</summary>
    public static string ReplyLanguage
    {
        get => NormalizeReply(GetString("ReplyLanguage", MateSusuEnv.Get("OPENROUTER_LANGUAGE", "auto")));
        set => SetString("ReplyLanguage", NormalizeReply(value));
    }

    /// <summary>OpenRouter model id used for chat.</summary>
    public static string ChatModel
    {
        get
        {
            string v = (GetString("ChatModel", MateSusuEnv.Get("OPENROUTER_CHAT_MODEL", DefaultChatModel)) ?? "").Trim();
            return v.Length > 0 ? v : DefaultChatModel;
        }
        set => SetString("ChatModel", (value ?? "").Trim());
    }

    // ---------- Voice ----------

    /// <summary>Chat replies are read aloud with Susu's voice.</summary>
    public static bool SpeakReplies
    {
        get => GetBool("SpeakReplies", MateSusuEnv.GetBool("OPENROUTER_TTS_ENABLED", true));
        set => SetBool("SpeakReplies", value);
    }

    /// <summary>"elevenlabs:&lt;model&gt;" or "openrouter:&lt;model&gt;" for chat replies.</summary>
    public static string VoiceModel
    {
        get
        {
            string v = (GetString("VoiceModel", "") ?? "").Trim();
            if (v.IndexOf(':') > 0) return v;

            // From .env: SUSU_TTS_PROVIDER (default: ElevenLabs when it has a key) and that provider's model.
            bool hasEleven = !string.IsNullOrEmpty(ElevenLabsKey);
            string provider = MateSusuEnv.Get("SUSU_TTS_PROVIDER", hasEleven ? "elevenlabs" : "openrouter").Trim().ToLowerInvariant();
            if (provider.StartsWith("eleven"))
                return "elevenlabs:" + (MateSusuEnv.GetFirst("ELEVENLABS_CHAT_MODEL", "ELEVENLABS_MODEL") ?? "eleven_v4").Trim();
            return "openrouter:" + MateSusuEnv.Get("OPENROUTER_TTS_MODEL", "fish-audio/drama-3").Trim();
        }
        set => SetString("VoiceModel", (value ?? "").Trim());
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

    static string openRouterKey, elevenLabsKey; // decrypted once, kept in memory
    static bool keysLoaded;

    /// <summary>OpenRouter key (chat, and OpenRouter voices): the one saved in the app, else .env.</summary>
    public static string OpenRouterKey
    {
        get
        {
            LoadKeys();
            return !string.IsNullOrEmpty(openRouterKey) ? openRouterKey : MateSusuEnv.GetFirst("OPENROUTER_API_KEY", "API_KEY") ?? "";
        }
        set { LoadKeys(); openRouterKey = SaveKey("OpenRouterKey", value); Changed?.Invoke(); }
    }

    /// <summary>ElevenLabs key (voice and microphone transcription): the one saved in the app, else .env.</summary>
    public static string ElevenLabsKey
    {
        get
        {
            LoadKeys();
            return !string.IsNullOrEmpty(elevenLabsKey) ? elevenLabsKey : MateSusuEnv.Get("ELEVENLABS_API_KEY") ?? "";
        }
        set { LoadKeys(); elevenLabsKey = SaveKey("ElevenLabsKey", value); Changed?.Invoke(); }
    }

    public static KeySource OpenRouterKeySource
    {
        get { LoadKeys(); return !string.IsNullOrEmpty(openRouterKey) ? KeySource.Saved : OpenRouterKey.Length > 0 ? KeySource.Env : KeySource.None; }
    }

    public static KeySource ElevenLabsKeySource
    {
        get { LoadKeys(); return !string.IsNullOrEmpty(elevenLabsKey) ? KeySource.Saved : ElevenLabsKey.Length > 0 ? KeySource.Env : KeySource.None; }
    }

    static void LoadKeys()
    {
        if (keysLoaded) return;
        keysLoaded = true;
        openRouterKey = Unprotect(PlayerPrefs.GetString(Prefix + "OpenRouterKey", ""));
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
