using UnityEngine;

/// <summary>
/// Creates the MateSusu runtime object at startup:
/// chat client, voice playback, voice lines, motions and the Voices / AI / Chat windows.
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

        // Always there: the key can be typed later in the AI window.
        Root.AddComponent<OpenRouterChat>().Configure();

        Root.AddComponent<OpenRouterTTS>();
        Root.AddComponent<SusuVoiceLines>();
        Root.AddComponent<SusuSystemAudio>();
        Root.AddComponent<SusuAutoTalk>();
        Root.AddComponent<SusuClipPlayer>();
        Root.AddComponent<SusuMotions>();
        Root.AddComponent<SusuDances>();
        Root.AddComponent<SusuIdleMotions>();
        Root.AddComponent<SusuSpeechBalloon>();
        Root.AddComponent<SusuChatMic>();
        Root.AddComponent<SusuVoicesMenu>();
        Root.AddComponent<SusuAiMenu>();
        Root.AddComponent<SusuChatWindow>();
    }
}
