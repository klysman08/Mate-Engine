using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Lets Susu say one of her voice lines by herself every few minutes (with some randomness),
/// choosing phrases that fit the time of day. Settings are saved between sessions.
/// </summary>
public class SusuAutoTalk : MonoBehaviour
{
    public static SusuAutoTalk Instance { get; private set; }

    public const int MinMinutes = 1;
    public const int MaxMinutes = 60;

    const string EnabledPref = "MateSusu.AutoTalk.Enabled";
    const string MinutesPref = "MateSusu.AutoTalk.Minutes";

    [Tooltip("Random spread around the interval, e.g. 0.3 = +/-30%")]
    [Range(0f, 0.9f)] public float jitter = 0.3f;
    [Tooltip("If Susu is busy (talking, chatting, dragged) try again after this many seconds.")]
    public float retrySeconds = 20f;

    bool autoEnabled;
    int minutes = 5;
    float nextTalkAt = -1f;
    string lastId;

    public event Action Changed;

    public bool Enabled
    {
        get => autoEnabled;
        set
        {
            if (autoEnabled == value) return;
            autoEnabled = value;
            PlayerPrefs.SetInt(EnabledPref, value ? 1 : 0);
            Schedule();
            Changed?.Invoke();
        }
    }

    public int IntervalMinutes
    {
        get => minutes;
        set
        {
            int v = Mathf.Clamp(value, MinMinutes, MaxMinutes);
            if (minutes == v) return;
            minutes = v;
            PlayerPrefs.SetInt(MinutesPref, v);
            Schedule();
            Changed?.Invoke();
        }
    }

    /// <summary>Seconds until the next automatic line (or -1 when off).</summary>
    public float SecondsUntilNext => autoEnabled ? Mathf.Max(0f, nextTalkAt - Time.unscaledTime) : -1f;

    void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(this); return; }
        Instance = this;
        autoEnabled = PlayerPrefs.GetInt(EnabledPref, 0) == 1;
        minutes = Mathf.Clamp(PlayerPrefs.GetInt(MinutesPref, 5), MinMinutes, MaxMinutes);
        Schedule();
    }

    void OnDestroy()
    {
        if (Instance == this) Instance = null;
    }

    void Schedule()
    {
        float seconds = minutes * 60f * UnityEngine.Random.Range(1f - jitter, 1f + jitter);
        nextTalkAt = Time.unscaledTime + seconds;
    }

    void Update()
    {
        if (!autoEnabled || Time.unscaledTime < nextTalkAt) return;

        if (!CanTalkNow())
        {
            nextTalkAt = Time.unscaledTime + retrySeconds;
            return;
        }

        var phrase = PickPhrase();
        if (phrase != null)
        {
            lastId = phrase.id;
            SusuVoiceLines.Instance.Play(phrase);
        }
        Schedule();
    }

    bool CanTalkNow()
    {
        var tts = OpenRouterTTS.Instance;
        if (SusuVoiceLines.Instance == null || tts == null) return false;
        if (tts.IsSpeaking) return false;
        if (OpenRouterChat.Instance != null && OpenRouterChat.Instance.IsBusy) return false;
        return true;
    }

    SusuVoiceLines.Phrase PickPhrase()
    {
        int hour = DateTime.Now.Hour;
        var pool = new List<SusuVoiceLines.Phrase>();
        foreach (var p in SusuVoiceLines.Instance.Phrases)
            if (!p.noAuto && p.AllowedAt(hour) && SusuVoiceLines.Instance.IsAvailableOffline(p))
                pool.Add(p);

        if (pool.Count > 1) pool.RemoveAll(p => p.id == lastId); // avoid repeating the same line
        return pool.Count > 0 ? pool[UnityEngine.Random.Range(0, pool.Count)] : null;
    }
}
