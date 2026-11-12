using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Lets Susu say one of her voice lines by herself every few minutes (with some randomness).
/// What she says follows the clock:
///  - the day is split into periods (morning, midday, afternoon, evening, night) and each period favours
///    some categories (work talk in the morning, stories and sweet talk in the evening...);
///  - lines tied to an hour window ("hours") are much more likely while their window is open;
///  - "daily" lines (greetings, meals, bath...) are said at most once a day;
///  - the first line of a period is its greeting, and opening the app after a while gets a
///    "good morning" / "welcome back" / "long time no see";
///  - recently heard lines (auto or clicked) are not repeated; after midnight she talks half as often.
/// She also waits for silence: while Windows is playing sound she holds the line until it has been quiet
/// for a few seconds. Settings and history are saved between sessions.
/// </summary>
public class SusuAutoTalk : MonoBehaviour
{
    public static SusuAutoTalk Instance { get; private set; }

    public const int MinMinutes = 1;
    public const int MaxMinutes = 60;

    const string EnabledPref = "MateSusu.AutoTalk.Enabled";
    const string MinutesPref = "MateSusu.AutoTalk.Minutes";
    const string QuietPref = "MateSusu.AutoTalk.WaitForSilence";
    const string RecentPref = "MateSusu.AutoTalk.Recent";
    const string DailyPref = "MateSusu.AutoTalk.Daily";       // "<day>|key,key,..."
    const string GreetedPref = "MateSusu.AutoTalk.Greeted";   // day * 10 + period of the last greeting slot used
    const string LastSeenPref = "MateSusu.AutoTalk.LastSeen"; // DateTime ticks

    const int DayStartHour = 5;        // "today" runs 05:00-04:59, so a 1 a.m. good night belongs to the evening before
    const int RecentMax = 30;

    public enum Period { Morning, Midday, Afternoon, Evening, Night }

    [Tooltip("Random spread around the interval, e.g. 0.3 = +/-30%")]
    [Range(0f, 0.9f)] public float jitter = 0.3f;
    [Tooltip("If Susu is busy (talking, chatting, dragged) try again after this many seconds.")]
    public float retrySeconds = 20f;
    [Tooltip("How much likelier a line with an hour window is while that window is open.")]
    public float timedBoost = 3f;
    [Tooltip("Windows must have been silent this long before Susu speaks by herself.")]
    public float quietSeconds = 4f;
    [Tooltip("Seconds after launch for the opening line (good morning / welcome back).")]
    public float startupDelay = 25f;
    [Tooltip("The app must have been closed this many minutes for a welcome-back line.")]
    public float welcomeBackMinutes = 30f;
    [Tooltip("Closed for this many days = 'long time no see'.")]
    public float longTimeDays = 3f;

    bool autoEnabled, waitForSilence;
    int minutes = 5;
    float nextTalkAt = -1f, nextSeenSaveAt;
    bool startupPending;
    double hoursAway;
    int dailyDay = -1;
    readonly HashSet<string> dailySaid = new HashSet<string>();
    readonly List<string> recent = new List<string>();

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

    /// <summary>Hold automatic lines while music, a video, a call... is playing on Windows.</summary>
    public bool WaitForSilence
    {
        get => waitForSilence;
        set
        {
            if (waitForSilence == value) return;
            waitForSilence = value;
            PlayerPrefs.SetInt(QuietPref, value ? 1 : 0);
            Changed?.Invoke();
        }
    }

    /// <summary>True while a line is due but held back because sound is playing.</summary>
    public bool HoldingForSilence { get; private set; }

    /// <summary>Seconds until the next automatic line (or -1 when off).</summary>
    public float SecondsUntilNext => autoEnabled ? Mathf.Max(0f, nextTalkAt - Time.unscaledTime) : -1f;

    public static Period PeriodOf(int hour)
    {
        if (hour >= 5 && hour < 11) return Period.Morning;
        if (hour >= 11 && hour < 14) return Period.Midday;
        if (hour >= 14 && hour < 18) return Period.Afternoon;
        if (hour >= 18 && hour < 22) return Period.Evening;
        return Period.Night;
    }

    /// <summary>Day number that only changes at 05:00.</summary>
    static int SusuDay(DateTime now) => (int)(now.AddHours(-DayStartHour).Date - new DateTime(2020, 1, 1)).TotalDays;

    void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(this); return; }
        Instance = this;
        autoEnabled = PlayerPrefs.GetInt(EnabledPref, 0) == 1;
        waitForSilence = PlayerPrefs.GetInt(QuietPref, 1) == 1;
        minutes = Mathf.Clamp(PlayerPrefs.GetInt(MinutesPref, 5), MinMinutes, MaxMinutes);

        foreach (var id in PlayerPrefs.GetString(RecentPref, "").Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
            recent.Add(id);
        LoadDaily(DateTime.Now);

        // How long was the app closed? Decides whether the first line is a welcome.
        hoursAway = double.MaxValue;
        if (long.TryParse(PlayerPrefs.GetString(LastSeenPref, ""), out long ticks) && ticks > 0)
            hoursAway = Math.Max(0, (DateTime.Now - new DateTime(ticks)).TotalHours);
        startupPending = hoursAway * 60.0 >= welcomeBackMinutes;

        Schedule();
        if (startupPending) nextTalkAt = Time.unscaledTime + startupDelay;
    }

    void Start()
    {
        if (SusuVoiceLines.Instance != null) SusuVoiceLines.Instance.PhraseStarted += Remember;
    }

    void OnDestroy()
    {
        if (SusuVoiceLines.Instance != null) SusuVoiceLines.Instance.PhraseStarted -= Remember;
        SaveLastSeen();
        if (Instance == this) Instance = null;
    }

    void OnApplicationQuit() => SaveLastSeen();

    void SaveLastSeen() => PlayerPrefs.SetString(LastSeenPref, DateTime.Now.Ticks.ToString());

    void Schedule()
    {
        float seconds = minutes * 60f * UnityEngine.Random.Range(1f - jitter, 1f + jitter);
        if (DateTime.Now.Hour < DayStartHour) seconds *= 2f; // small hours: she is sleepy too
        nextTalkAt = Time.unscaledTime + seconds;
        HoldingForSilence = false;
    }

    void Update()
    {
        if (Time.unscaledTime >= nextSeenSaveAt)
        {
            nextSeenSaveAt = Time.unscaledTime + 60f;
            SaveLastSeen();
        }

        var audio = SusuSystemAudio.Instance;
        bool listen = autoEnabled && waitForSilence && audio != null;
        // Start listening a little before the line is due, so the quiet timer is already running.
        if (audio != null) audio.Monitoring = listen && nextTalkAt - Time.unscaledTime < quietSeconds + 2f;

        if (!autoEnabled || Time.unscaledTime < nextTalkAt) return;

        if (!CanTalkNow())
        {
            nextTalkAt = Time.unscaledTime + retrySeconds;
            return;
        }

        if (listen && audio.Available && audio.QuietFor < quietSeconds)
        {
            HoldingForSilence = true; // keep the line due; checked again every frame until it is quiet
            return;
        }

        var phrase = PickPhrase(DateTime.Now);
        if (phrase != null) SusuVoiceLines.Instance.Play(phrase, true);
        Schedule();
    }

    bool CanTalkNow()
    {
        var tts = SusuTTS.Instance;
        if (SusuVoiceLines.Instance == null || tts == null) return false;
        if (tts.IsSpeaking) return false;
        if (SusuState.InCustomDance) return false;
        if (SusuChat.Instance != null && SusuChat.Instance.IsBusy) return false;
        return true;
    }

    // ---------- Choosing what to say ----------

    SusuVoiceLines.Phrase PickPhrase(DateTime now)
    {
        var voice = SusuVoiceLines.Instance;
        int hour = now.Hour;
        var period = PeriodOf(hour);
        LoadDaily(now);

        var pool = new List<SusuVoiceLines.Phrase>();
        foreach (var p in voice.Phrases)
            if (!p.noAuto && string.IsNullOrEmpty(p.trigger) && p.AllowedAt(hour) && !SaidToday(p) && voice.IsAvailableOffline(p))
                pool.Add(p);

        // Opening line: once after launch, and once whenever a new period begins.
        bool startup = startupPending;
        startupPending = false;
        int slot = SusuDay(now) * 10 + (int)period;
        bool newPeriod = PlayerPrefs.GetInt(GreetedPref, -1) != slot;
        if (newPeriod) PlayerPrefs.SetInt(GreetedPref, slot);

        if (startup && hoursAway < double.MaxValue && hoursAway >= longTimeDays * 24.0)
        {
            var longTime = PickTriggered("longtime");
            if (longTime != null) return longTime;
        }
        if (startup || newPeriod)
        {
            var greetings = pool.FindAll(p => p.greet);
            if (greetings.Count > 0) return greetings[UnityEngine.Random.Range(0, greetings.Count)];
        }
        if (startup)
        {
            var welcome = PickTriggered("startup");
            if (welcome != null) return welcome;
        }

        if (pool.Count == 0) return null;

        // Skip what was heard lately (never more than half of what is available right now).
        int skip = Mathf.Min(recent.Count, pool.Count / 2);
        var fresh = pool.FindAll(p => recent.LastIndexOf(p.id) < recent.Count - skip);
        if (fresh.Count == 0) fresh = pool;

        float total = 0f;
        var weights = new float[fresh.Count];
        for (int i = 0; i < fresh.Count; i++)
        {
            var p = fresh[i];
            float w = CategoryWeight(p.category, period);
            if (!string.IsNullOrWhiteSpace(p.hours)) w *= timedBoost;
            total += weights[i] = Mathf.Max(0.01f, w);
        }
        float roll = UnityEngine.Random.value * total;
        for (int i = 0; i < fresh.Count; i++)
            if ((roll -= weights[i]) <= 0f) return fresh[i];
        return fresh[fresh.Count - 1];
    }

    SusuVoiceLines.Phrase PickTriggered(string trigger)
    {
        var voice = SusuVoiceLines.Instance;
        var options = new List<SusuVoiceLines.Phrase>();
        foreach (var p in voice.Phrases)
            if (string.Equals(p.trigger, trigger, StringComparison.OrdinalIgnoreCase) && voice.IsAvailableOffline(p))
                options.Add(p);
        return options.Count > 0 ? options[UnityEngine.Random.Range(0, options.Count)] : null;
    }

    /// <summary>How well a category suits a time of day (1 = neutral).</summary>
    static float CategoryWeight(string category, Period period)
    {
        string c = category ?? "";
        //                                                    morning midday afternoon evening night
        if (c.StartsWith("Study")) return Pick(period,          1.5f,  1.0f,  1.5f,     0.7f,   0.3f);
        if (c.StartsWith("Health")) return Pick(period,         1.0f,  1.0f,  1.3f,     1.0f,   0.8f);
        if (c.StartsWith("Cheer")) return Pick(period,          1.0f,  1.0f,  1.3f,     1.0f,   0.8f);
        if (c.StartsWith("Sweet")) return Pick(period,          0.6f,  0.8f,  0.8f,     1.4f,   1.4f);
        if (c.StartsWith("Just for fun")) return Pick(period,   0.8f,  1.2f,  1.0f,     1.3f,   0.6f);
        if (c.StartsWith("Stories") || c.StartsWith("About")) return Pick(period, 0.6f, 1.0f, 0.8f, 1.5f, 1.0f);
        if (c.StartsWith("Japanese")) return Pick(period,       1.0f,  1.0f,  1.0f,     1.0f,   0.5f);
        return 1f;
    }

    static float Pick(Period period, float morning, float midday, float afternoon, float evening, float night)
    {
        switch (period)
        {
            case Period.Morning: return morning;
            case Period.Midday: return midday;
            case Period.Afternoon: return afternoon;
            case Period.Evening: return evening;
            default: return night;
        }
    }

    // ---------- History ----------

    bool SaidToday(SusuVoiceLines.Phrase p) => !string.IsNullOrEmpty(p.daily) && dailySaid.Contains(p.daily);

    /// <summary>Called for every line that starts, automatic or clicked, so neither is repeated soon after.</summary>
    void Remember(SusuVoiceLines.Phrase p)
    {
        if (p == null || string.IsNullOrEmpty(p.id)) return;

        recent.Remove(p.id);
        recent.Add(p.id);
        if (recent.Count > RecentMax) recent.RemoveRange(0, recent.Count - RecentMax);
        PlayerPrefs.SetString(RecentPref, string.Join(",", recent));

        if (!string.IsNullOrEmpty(p.daily))
        {
            LoadDaily(DateTime.Now);
            if (dailySaid.Add(p.daily))
                PlayerPrefs.SetString(DailyPref, dailyDay + "|" + string.Join(",", dailySaid));
        }
    }

    /// <summary>Keeps the "said today" list for the current day; a new day starts it empty.</summary>
    void LoadDaily(DateTime now)
    {
        int day = SusuDay(now);
        if (day == dailyDay) return;
        dailyDay = day;
        dailySaid.Clear();
        var parts = PlayerPrefs.GetString(DailyPref, "").Split('|');
        if (parts.Length == 2 && parts[0] == day.ToString())
            foreach (var key in parts[1].Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
                dailySaid.Add(key);
    }
}
