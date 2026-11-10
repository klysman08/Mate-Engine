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
    const float SectionHeight = 44f;

    // Subtitle / flashcard strip pinned above the scrolling list
    const float CaptionHeight = 118f, CaptionGap = 6f, CaptionPadding = 24f;
    const string CaptionIdle = "Tap a phrase: Susu's words show up here.\n<size=80%>Japanese deck: phrase, romaji and meaning.</size>";

    TMP_Text frequencyLabel, volumeLabel, caption;

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
        if (title != null) BuildCaption(title, (RectTransform)main);

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

        // Phrase buttons, two columns, with a section title whenever the category changes.
        var phrases = SusuVoiceLines.Instance.Phrases;
        float contentWidth = content.rect.width > 100f ? content.rect.width : 633f;
        float y = gridTop;
        int col = 0;
        string lastCategory = null;
        foreach (var p in phrases)
        {
            string category = p.category ?? "";
            if (category != lastCategory)
            {
                if (col != 0) { y += ButtonHeight + RowSpacing; col = 0; }
                if (category.Length > 0 && title != null)
                {
                    AddSectionTitle(title, content, category.ToUpperInvariant(), y, contentWidth);
                    y += SectionHeight;
                }
                lastCategory = category;
            }

            var btn = (RectTransform)Instantiate(buttonTemplate.gameObject, content).transform;
            btn.name = "Voice " + p.id;
            btn.anchorMin = btn.anchorMax = new Vector2(0.5f, 1f);
            btn.pivot = new Vector2(0.5f, 0.5f);
            btn.sizeDelta = new Vector2(ButtonWidth, ButtonHeight);
            btn.anchoredPosition = new Vector2(col == 0 ? -ColumnX : ColumnX, -(y + ButtonHeight * 0.5f));

            var phrase = p;
            var label = Rebind(btn, p.label, () => SusuVoiceLines.Instance?.Play(phrase));
            if (label != null) labels[p.id] = label;

            if (++col == 2) { col = 0; y += ButtonHeight + RowSpacing; }
        }
        if (col != 0) y += ButtonHeight + RowSpacing;
        content.sizeDelta = new Vector2(content.sizeDelta.x, y + TopPadding);

        foreach (var ma in Resources.FindObjectsOfTypeAll<MenuActions>())
        {
            if (!ma.gameObject.scene.IsValid() || ma.menuEntries.Exists(e => e.menu == window)) continue;
            ma.menuEntries.Add(new MenuEntry { menu = window, blockMovement = true });
        }

        Debug.Log($"[SusuVoice] Voices window ready ({phrases.Count} phrases).");
        return true;
    }

    static void AddSectionTitle(TMP_Text titleTemplate, RectTransform content, string text, float y, float contentWidth)
    {
        var go = Instantiate(titleTemplate.gameObject, content);
        go.name = "Section " + text;
        var rect = (RectTransform)go.transform;
        rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 1f);
        rect.pivot = new Vector2(0.5f, 0.5f);
        rect.sizeDelta = new Vector2(ColumnX * 2f + ButtonWidth, SectionHeight - 8f);
        rect.anchoredPosition = new Vector2(0f, -(y + SectionHeight * 0.5f));
        var t = go.GetComponent<TMP_Text>();
        t.text = "= " + text;
        t.enableAutoSizing = false;
        t.fontSize = 22f;
        t.alignment = TextAlignmentOptions.Left;
    }

    // ---------- Subtitles / Japanese deck card ----------

    /// <summary>
    /// Puts a caption strip between the window title and the phrase list. The caption is a child of the
    /// scroll view (so it shares its scale and width) anchored just above it, outside the scroll mask;
    /// the scroll view's top moves down by the caption's height while its bottom edge stays put.
    /// </summary>
    void BuildCaption(TMP_Text titleTemplate, RectTransform scrollView)
    {
        // Shrink the scroll view from the top only. It is scaled around its pivot, so solve for the new
        // offsets that keep the visual bottom fixed and lower the visual top by the caption height.
        float s = scrollView.localScale.y, p = scrollView.pivot.y;
        float h = scrollView.rect.height, m = scrollView.offsetMin.y;
        float visualBottom = m + p * h * (1f - s);
        float newHeight = Mathf.Max(100f, h - (CaptionHeight + CaptionGap));
        float newMin = visualBottom - p * newHeight * (1f - s);
        scrollView.offsetMin = new Vector2(scrollView.offsetMin.x, newMin);
        scrollView.offsetMax = new Vector2(scrollView.offsetMax.x, newMin + newHeight);

        var go = Instantiate(titleTemplate.gameObject, scrollView);
        go.name = "Voice Caption";
        var rect = (RectTransform)go.transform;
        rect.localScale = Vector3.one;          // the title template is scaled differently
        rect.localRotation = Quaternion.identity;
        rect.anchorMin = new Vector2(0f, 1f);
        rect.anchorMax = new Vector2(1f, 1f);
        rect.pivot = new Vector2(0.5f, 0f);
        rect.offsetMin = new Vector2(CaptionPadding, CaptionGap);
        rect.offsetMax = new Vector2(-CaptionPadding, CaptionGap + CaptionHeight);

        caption = go.GetComponent<TMP_Text>();
        caption.richText = true;
        caption.textWrappingMode = TextWrappingModes.Normal;
        caption.overflowMode = TextOverflowModes.Ellipsis;
        caption.enableAutoSizing = true;
        caption.fontSizeMin = 12f;
        caption.fontSizeMax = 24f;
        caption.characterSpacing = 0f;          // the title's wide letter spacing smears kana/kanji
        caption.wordSpacing = 0f;
        caption.margin = Vector4.zero;
        caption.alignment = TextAlignmentOptions.Center;
        caption.fontStyle = FontStyles.Normal;
        var jp = JapaneseFont();
        if (jp != null) caption.font = jp;
        caption.text = CaptionIdle;
    }

    static TMP_FontAsset japaneseFont;

    /// <summary>
    /// A font that has every kana/kanji plus Portuguese accents: a Windows Japanese system font made into a
    /// dynamic TMP font, or the app's bundled "Japan" atlas (fixed glyph set, some characters missing).
    /// </summary>
    static TMP_FontAsset JapaneseFont()
    {
        if (japaneseFont != null) return japaneseFont;
        foreach (var family in new[] { "Yu Gothic UI", "Meiryo UI", "Meiryo", "Yu Gothic", "Noto Sans JP", "MS Gothic" })
        {
            try
            {
                var f = TMP_FontAsset.CreateFontAsset(family, "Regular");
                if (f != null && f.HasCharacter('ぱ', false, true)) { f.name = "Susu Caption (" + family + ")"; return japaneseFont = f; }
            }
            catch (System.Exception) { /* font not installed */ }
        }
        foreach (var f in Resources.FindObjectsOfTypeAll<TMP_FontAsset>())
            if (f != null && f.name == "Japan") return japaneseFont = f;
        return null;
    }

    static string CaptionFor(SusuVoiceLines.Phrase p)
    {
        if (p.IsLesson)
        {
            string card = $"<size=130%>{p.learn}</size>";
            if (!string.IsNullOrEmpty(p.romaji)) card += $"\n<i>{p.romaji}</i>";
            if (!string.IsNullOrEmpty(p.meaning)) card += $"\n<size=85%>{p.meaning}</size>";
            return card;
        }
        return string.IsNullOrEmpty(p.text) ? p.label : p.text;
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
        if (caption != null) caption.text = CaptionFor(p); // stays after the line ends so it can be read
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
