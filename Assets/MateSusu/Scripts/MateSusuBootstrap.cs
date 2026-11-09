using UnityEngine;

/// <summary>
/// Creates the MateSusu runtime object at startup:
/// voice playback + voice lines + Voices radial slice/window always, the OpenRouter chat only when a key exists.
/// </summary>
public static class MateSusuBootstrap
{
    public static GameObject Root { get; private set; }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    static void Init()
    {
        if (Root != null) return;

        Root = new GameObject("MateSusu");
        Object.DontDestroyOnLoad(Root);

        var key = MateSusuEnv.GetFirst("OPENROUTER_API_KEY", "API_KEY");
        if (!string.IsNullOrEmpty(key))
            Root.AddComponent<OpenRouterChat>().Configure(key);
        else
            Debug.Log("[MateSusu] No OpenRouter API key in .env - chat uses the local LLM, voice lines play from local files only.");

        Root.AddComponent<OpenRouterTTS>();
        Root.AddComponent<SusuVoiceLines>();
        Root.AddComponent<SusuAutoTalk>();
        Root.AddComponent<SusuClipPlayer>();
        Root.AddComponent<SusuMotions>();
        Root.AddComponent<SusuVoicesMenu>();
    }
}
