using System.Collections;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

/// <summary>
/// Adds a "Voices" slice to the right-click radial menu. It opens a Voices window (cloned from the
/// Alarm window so it matches the app's look) with one button per pre-recorded Susu phrase.
/// Everything is built at runtime; the scene file is not modified.
/// </summary>
public class SusuVoicesMenu : MonoBehaviour
{
    public static SusuVoicesMenu Instance { get; private set; }

    const string RadialTemplateId = "chat";   // slice we copy (and insert after)
    const string RadialId = "voices";
    const string WindowTemplate = "AlarmMenuCanvas";
    const string IconResource = "SusuVoicesIcon";

    // Templates for the auto-talk controls (settings menu: GENERAL > Discord RPC toggle, AUDIO > Pet Volumen slider)
    const string ToggleTemplate = "Discord RPC";
    const string SliderTemplate = "Pet Volumen";

    // Phrase grid inside the window's scroll content
    const float ButtonWidth = 280f, ButtonHeight = 58f, ColumnX = 150f, RowSpacing = 14f, TopPadding = 24f;
    // Auto-talk controls block above the grid
    const float ControlsHeight = 250f, SliderWidth = 520f;

    TMP_Text frequencyLabel, volumeLabel;

    public string windowTitle = "VOICES";

    GameObject window;
    Sprite icon;
    readonly Dictionary<string, TMP_Text> labels = new Dictionary<string, TMP_Text>();

    public bool IsOpen => window != null && window.activeSelf;

    void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(this); return; }
        Instance = this;
    }

    IEnumerator Start()
    {
        bool windowDone = false, radialDone = false;
        for (int attempt = 0; attempt < 60 && !(windowDone && radialDone); attempt++)
        {
            if (!windowDone && SusuVoiceLines.Instance != null && SusuVoiceLines.Instance.Phrases.Count > 0)
                windowDone = BuildWindow();
            if (windowDone && !radialDone)
                radialDone = AddRadialSlice();
            if (!(windowDone && radialDone)) yield return new WaitForSecondsRealtime(0.5f);
        }
        if (!windowDone) Debug.LogWarning("[SusuVoice] Could not build the Voices window (template not found).");
        else if (!radialDone) Debug.LogWarning("[SusuVoice] Could not add the Voices slice to the radial menu.");
    }

    void OnEnable()
    {
        if (SusuVoiceLines.Instance == null) return;
        SusuVoiceLines.Instance.PhraseStarted += OnPhraseStarted;
        SusuVoiceLines.Instance.PhraseFinished += OnPhraseFinished;
    }

    void OnDisable()
    {
        if (SusuVoiceLines.Instance == null) return;
        SusuVoiceLines.Instance.PhraseStarted -= OnPhraseStarted;
        SusuVoiceLines.Instance.PhraseFinished -= OnPhraseFinished;
    }

    public void Toggle()
    {
        if (window == null) return;
        bool open = !window.activeSelf;
        if (open) CloseOtherMenus();
        window.SetActive(open);
    }

    public void Close()
    {
        if (window != null) window.SetActive(false);
    }

    // ---------- Radial menu ----------

    bool AddRadialSlice()
    {
        bool added = false;
        foreach (var selector in Resources.FindObjectsOfTypeAll<Xamin.CircleSelector>())
        {
            if (!selector.gameObject.scene.IsValid() || selector.Buttons == null) continue;

            GameObject template = selector.Buttons.Find(b => b != null && IdOf(b) == RadialTemplateId);
            if (template == null) continue;
            if (selector.Buttons.Exists(b => b != null && IdOf(b) == RadialId)) { added = true; continue; }

            var slice = Instantiate(template, template.transform.parent);
            slice.name = "Voices";
            var button = slice.GetComponent<Xamin.Button>();
            button.id = RadialId;
            button.action = new UnityEvent(); // drop the copied "open chat" call
            button.unlocked = true;

            var sprite = GetIcon();
            if (sprite != null)
            {
                button.image = sprite;
                var img = slice.GetComponent<Image>();
                if (img != null) img.sprite = sprite;
            }

            // The selector instantiates copies of its button objects every time it opens, and runtime
            // listeners are not copied - this hook re-attaches the action on each copy.
            slice.AddComponent<SusuVoicesRadialHook>();

            selector.Buttons.Insert(selector.Buttons.IndexOf(template) + 1, slice);
            if (selector.Buttons.Count > Xamin.CircleSelector.MaxButtons)
                Debug.LogWarning($"[SusuVoice] Radial menu now has {selector.Buttons.Count} buttons (max {Xamin.CircleSelector.MaxButtons}).");
            added = true;
            Debug.Log("[SusuVoice] Voices slice added to the radial menu.");
        }
        return added;
    }

    static string IdOf(GameObject go)
    {
        var b = go.GetComponent<Xamin.Button>();
        return b != null ? b.id : null;
    }

    Sprite GetIcon()
    {
        if (icon != null) return icon;
        var tex = Resources.Load<Texture2D>(IconResource);
        if (tex == null) return null;
        icon = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f), 100f);
        icon.name = IconResource;
        return icon;
    }

    // ---------- Window ----------

    bool BuildWindow()
    {
        var template = FindSceneObject(WindowTemplate);
        if (template == null) return false;

        // The template canvas is inactive in the scene, so the copy starts inactive too:
        // none of its scripts (localisation, alarm UI) run before we strip them.
        window = Instantiate(template, template.transform.parent);
        window.name = "VoicesMenuCanvas";
        window.SetActive(false);

        foreach (var mb in window.GetComponentsInChildren<MonoBehaviour>(true))
            if (mb != null && mb.GetType().Name == "LocalizeStringEvent") DestroyImmediate(mb);

        var outer = window.transform.Find("OuterMenuAlarm");
        var main = window.transform.Find("MainMenuAlarm");
        if (outer == null || main == null) { Debug.LogWarning("[SusuVoice] Alarm window layout changed."); return true; }
        outer.name = "OuterMenuVoices";
        main.name = "MainMenuVoices";

        var title = outer.Find("Header/TitleText")?.GetComponent<TMP_Text>();
        if (title != null) title.text = windowTitle;

        // Footer: reuse the two buttons as STOP and CLOSE.
        var footer = outer.Find("Footer");
        RectTransform buttonTemplate = null;
        if (footer != null)
        {
            var left = footer.Find("Add Alarm") as RectTransform;
            var right = footer.Find("Add Timer") as RectTransform;
            buttonTemplate = left != null ? left : right;
            Rebind(left, "STOP", () => OpenRouterTTS.Instance?.Stop());
            Rebind(right, "CLOSE", Close);
        }

        // Content: remove alarm rows and place the phrase buttons ourselves.
        var content = main.Find("Viewport/Content") as RectTransform;
        if (content == null || buttonTemplate == null) { Debug.LogWarning("[SusuVoice] Alarm window layout changed."); return true; }

        var old = new List<GameObject>();
        foreach (Transform child in content) old.Add(child.gameObject);
        foreach (var go in old) DestroyImmediate(go);
        foreach (var lg in content.GetComponents<LayoutGroup>()) lg.enabled = false;
        var fitter = content.GetComponent<ContentSizeFitter>();
        if (fitter != null) fitter.enabled = false;

        float gridTop = TopPadding;
        var toggleTemplate = FindSceneObject(ToggleTemplate);
        var sliderTemplate = FindSceneObject(SliderTemplate);
        if (toggleTemplate != null && sliderTemplate != null)
        {
            BuildControls(content, toggleTemplate.transform as RectTransform, sliderTemplate.transform as RectTransform);
            gridTop = ControlsHeight;
        }
        else Debug.LogWarning("[SusuVoice] Toggle/slider templates not found - auto-talk controls skipped.");

        var phrases = SusuVoiceLines.Instance.Phrases;
        int rows = Mathf.CeilToInt(phrases.Count / 2f);
        content.sizeDelta = new Vector2(content.sizeDelta.x, gridTop + TopPadding + rows * (ButtonHeight + RowSpacing));

        for (int i = 0; i < phrases.Count; i++)
        {
            var p = phrases[i];
            int row = i / 2, col = i % 2;
            var btn = (RectTransform)Instantiate(buttonTemplate.gameObject, content).transform;
            btn.name = "Voice " + p.id;
            btn.anchorMin = btn.anchorMax = new Vector2(0.5f, 1f);
            btn.pivot = new Vector2(0.5f, 0.5f);
            btn.sizeDelta = new Vector2(ButtonWidth, ButtonHeight);
            btn.anchoredPosition = new Vector2(col == 0 ? -ColumnX : ColumnX,
                -(gridTop + row * (ButtonHeight + RowSpacing) + ButtonHeight * 0.5f));

            var phrase = p;
            var label = Rebind(btn, p.label, () => SusuVoiceLines.Instance?.Play(phrase));
            if (label != null) labels[p.id] = label;
        }

        foreach (var ma in Resources.FindObjectsOfTypeAll<MenuActions>())
        {
            if (!ma.gameObject.scene.IsValid() || ma.menuEntries.Exists(e => e.menu == window)) continue;
            ma.menuEntries.Add(new MenuEntry { menu = window, blockMovement = true });
        }

        Debug.Log($"[SusuVoice] Voices window ready ({phrases.Count} phrases).");
        return true;
    }

    // ---------- Auto-talk controls ----------

    void BuildControls(RectTransform content, RectTransform toggleTemplate, RectTransform sliderTemplate)
    {
        float width = content.rect.width > 100f ? content.rect.width : 633f;
        var auto = SusuAutoTalk.Instance;
        var tts = OpenRouterTTS.Instance;

        // "Susu talks by herself" checkbox
        var toggleRect = CloneControl(toggleTemplate, content, "AutoTalk Toggle");
        var info = toggleRect.Find("INFO");
        if (info != null) DestroyImmediate(info.gameObject);
        toggleRect.anchoredPosition = new Vector2(-width * 0.5f + 40f + toggleRect.sizeDelta.x * 0.5f, -40f);
        SetLabel(toggleRect, "SUSU TALKS BY HERSELF");
        var toggle = toggleRect.GetComponent<Toggle>();
        if (toggle != null)
        {
            toggle.onValueChanged = new Toggle.ToggleEvent();
            toggle.SetIsOnWithoutNotify(auto != null && auto.Enabled);
            toggle.onValueChanged.AddListener(v => { if (SusuAutoTalk.Instance != null) SusuAutoTalk.Instance.Enabled = v; });
        }

        // Frequency slider (minutes)
        var freqRect = CloneControl(sliderTemplate, content, "AutoTalk Frequency");
        PlaceSlider(freqRect, -125f);
        frequencyLabel = freqRect.GetComponentInChildren<TMP_Text>(true);
        var freq = freqRect.GetComponent<Slider>();
        if (freq != null)
        {
            freq.onValueChanged = new Slider.SliderEvent();
            freq.minValue = SusuAutoTalk.MinMinutes;
            freq.maxValue = SusuAutoTalk.MaxMinutes;
            freq.wholeNumbers = true;
            freq.SetValueWithoutNotify(auto != null ? auto.IntervalMinutes : 5);
            freq.onValueChanged.AddListener(v =>
            {
                if (SusuAutoTalk.Instance != null) SusuAutoTalk.Instance.IntervalMinutes = Mathf.RoundToInt(v);
                UpdateFrequencyLabel(Mathf.RoundToInt(v));
            });
            UpdateFrequencyLabel(Mathf.RoundToInt(freq.value));
        }

        // Voice volume slider
        var volRect = CloneControl(sliderTemplate, content, "Voice Volume");
        PlaceSlider(volRect, -205f);
        volumeLabel = volRect.GetComponentInChildren<TMP_Text>(true);
        var vol = volRect.GetComponent<Slider>();
        if (vol != null)
        {
            vol.onValueChanged = new Slider.SliderEvent();
            vol.minValue = 0f;
            vol.maxValue = 1f;
            vol.wholeNumbers = false;
            vol.SetValueWithoutNotify(tts != null ? tts.Volume : 1f);
            vol.onValueChanged.AddListener(v =>
            {
                if (OpenRouterTTS.Instance != null) OpenRouterTTS.Instance.Volume = v;
                UpdateVolumeLabel(v);
            });
            UpdateVolumeLabel(vol.value);
        }
    }

    void PlaceSlider(RectTransform rect, float y)
    {
        float oldWidth = rect.sizeDelta.x;
        rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 1f);
        rect.sizeDelta = new Vector2(SliderWidth, rect.sizeDelta.y);
        rect.anchoredPosition = new Vector2(0f, y);

        // The label sits above the slider, aligned to its left edge; keep that alignment after resizing.
        var label = rect.GetComponentInChildren<TMP_Text>(true);
        if (label != null)
        {
            var lr = label.rectTransform;
            float offsetFromLeft = lr.anchoredPosition.x + oldWidth * 0.5f;
            lr.anchoredPosition = new Vector2(-SliderWidth * 0.5f + offsetFromLeft, lr.anchoredPosition.y);
        }
    }

    void UpdateFrequencyLabel(int minutes)
    {
        if (frequencyLabel != null)
            frequencyLabel.text = minutes == 1 ? "TALK FREQUENCY: ABOUT EVERY MINUTE" : $"TALK FREQUENCY: ABOUT EVERY {minutes} MIN";
    }

    void UpdateVolumeLabel(float v)
    {
        if (volumeLabel != null) volumeLabel.text = $"VOICE VOLUME: {Mathf.RoundToInt(v * 100f)}%";
    }

    static void SetLabel(RectTransform rect, string text)
    {
        var label = rect.GetComponentInChildren<TMP_Text>(true);
        if (label != null) label.text = text;
    }

    /// <summary>Copies a settings control into our (inactive) window and strips its settings/localisation scripts.</summary>
    static RectTransform CloneControl(RectTransform template, Transform parent, string name)
    {
        var copy = Instantiate(template.gameObject, parent);
        copy.name = name;
        foreach (var mb in copy.GetComponentsInChildren<MonoBehaviour>(true))
            if (mb != null && !(mb is Graphic) && !(mb is Selectable)) DestroyImmediate(mb);
        var rect = (RectTransform)copy.transform;
        rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 1f);
        rect.pivot = new Vector2(0.5f, 0.5f);
        return rect;
    }

    static TMP_Text Rebind(RectTransform rect, string text, UnityAction onClick)
    {
        if (rect == null) return null;
        var button = rect.GetComponent<UnityEngine.UI.Button>();
        if (button != null)
        {
            button.onClick = new UnityEngine.UI.Button.ButtonClickedEvent();
            button.onClick.AddListener(onClick);
        }
        var label = rect.GetComponentInChildren<TMP_Text>(true);
        if (label != null)
        {
            label.text = text;
            label.enableAutoSizing = true;
            label.fontSizeMin = 12f;
            label.fontSizeMax = 22f;
        }
        return label;
    }

    void CloseOtherMenus()
    {
        foreach (var ma in Resources.FindObjectsOfTypeAll<MenuActions>())
        {
            if (!ma.gameObject.scene.IsValid()) continue;
            foreach (var e in ma.menuEntries)
                if (e.menu != null && e.menu != window && e.menu.activeSelf) e.menu.SetActive(false);
        }
    }

    void OnPhraseStarted(SusuVoiceLines.Phrase p)
    {
        if (labels.TryGetValue(p.id, out var l) && l != null) l.fontStyle |= FontStyles.Underline;
    }

    void OnPhraseFinished(SusuVoiceLines.Phrase p)
    {
        if (labels.TryGetValue(p.id, out var l) && l != null) l.fontStyle &= ~FontStyles.Underline;
    }

    static GameObject FindSceneObject(string name)
    {
        foreach (var t in Resources.FindObjectsOfTypeAll<Transform>())
            if (t.name == name && t.gameObject.scene.IsValid()) return t.gameObject;
        return null;
    }
}

/// <summary>Attached to the Voices radial slice; every runtime copy wires its action to the Voices window.</summary>
public class SusuVoicesRadialHook : MonoBehaviour
{
    void Awake()
    {
        var b = GetComponent<Xamin.Button>();
        if (b == null) return;
        if (b.action == null) b.action = new UnityEvent();
        b.action.AddListener(() => SusuVoicesMenu.Instance?.Toggle());
    }
}
