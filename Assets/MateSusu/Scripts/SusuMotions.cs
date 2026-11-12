using System.Collections;
using UnityEngine;

/// <summary>
/// Makes Susu move while she speaks: when a voice line starts, its <c>motion</c> list
/// ("drink" or "phone,nonono") is played in order through <see cref="SusuClipPlayer"/>.
/// </summary>
public class SusuMotions : MonoBehaviour
{
    public static SusuMotions Instance { get; private set; }

    const float Fade = 0.4f;

    SusuVoiceLines voice;
    Coroutine running;

    void Awake() { Instance = this; }

    void Start()
    {
        voice = SusuVoiceLines.Instance;
        if (voice != null) voice.PhraseStarted += OnPhraseStarted;
        var lib = SusuMotionLibrary.Load();
        if (lib == null) Debug.LogWarning("[SusuMotions] Resources/SusuMotionLibrary not found - run Tools > MateSusu > Build Motion Library.");
    }

    void OnDestroy()
    {
        if (voice != null) voice.PhraseStarted -= OnPhraseStarted;
        if (Instance == this) Instance = null;
    }

    void OnPhraseStarted(SusuVoiceLines.Phrase p) => Play(p?.motion);

    /// <summary>Play a comma-separated list of motion names in order (unknown names are skipped).</summary>
    public void Play(string motionList)
    {
        if (string.IsNullOrWhiteSpace(motionList)) return;
        if (running != null) StopCoroutine(running);
        running = StartCoroutine(Run(motionList.Split(',')));
    }

    IEnumerator Run(string[] names)
    {
        var lib = SusuMotionLibrary.Load();
        var player = SusuClipPlayer.Instance;
        if (lib == null || player == null) yield break;

        foreach (var raw in names)
        {
            var m = lib.Find(raw.Trim());
            if (m == null) { Debug.LogWarning($"[SusuMotions] Unknown motion '{raw.Trim()}'."); continue; }
            if (!player.Play(m.clip, m.start, m.PlayTime, Fade, m.Speed, !m.noFootIK)) yield break; // avatar busy (dragging, sitting...)
            // Start the next one while this one is fading out, so they cross-fade.
            yield return new WaitForSeconds(Mathf.Max(0.1f, m.PlayTime - Fade));
        }
        running = null;
    }
}
