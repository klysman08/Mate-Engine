using System.Collections;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

/// <summary>
/// Adds an "AI" slice to the right-click radial menu. It opens a window (cloned from the Alarm window, like
/// the Voices one) with the options kept in <see cref="SusuAiSettings"/>: the language Susu replies in,
/// API keys, the chat and voice models, whether replies are spoken, the microphone language, the text over
/// her head and idle motions.
/// Built at runtime; the scene file is not modified.
/// </summary>
public class SusuAiMenu : MonoBehaviour
{
    public static SusuAiMenu Instance { get; private set; }

    const string RadialTemplateId = "chat";
    const string RadialAfterId = "voices"; // sits right after the Voices slice when that one exists
    const string RadialId = "susu_ai";
    const string WindowTemplate = "AlarmMenuCanvas";
    const string ToggleTemplate = "Discord RPC";

    const float RowHeight = 58f, RowGap = 12f, SectionHeight = 44f, ToggleHeight = 52f, TopPadding = 24f, RowWidth = 580f;

    public string windowTitle = "AI SETTINGS";

    GameObject window;
    Sprite icon;
    readonly List<System.Action> refreshers = new List<System.Action>();

    public bool IsOpen => window != null && window.activeSelf;

    void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(this); return; }
        Instance = this;
    }

    void OnEnable() => SusuAiSettings.Changed += Refresh;
    void OnDisable() => SusuAiSettings.Changed -= Refresh;

    IEnumerator Start()
    {
        bool windowDone = false, radialDone = false;
        for (int attempt = 0; attempt < 60 && !(windowDone && radialDone); attempt++)
        {
            if (!windowDone) windowDone = BuildWindow();
            if (windowDone && !radialDone) radialDone = AddRadialSlice();
            if (!(windowDone && radialDone)) yield return new WaitForSecondsRealtime(0.5f);
        }
        if (!windowDone) Debug.LogWarning("[SusuAI] Could not build the AI window (template not found).");
        else if (!radialDone) Debug.LogWarning("[SusuAI] Could not add the AI slice to the radial menu.");
    }

    public void Toggle()
    {
        if (window == null) return;
        bool open = !window.activeSelf;
        if (open)
        {
            foreach (var ma in Resources.FindObjectsOfTypeAll<MenuActions>())
            {
                if (!ma.gameObject.scene.IsValid()) continue;
                foreach (var e in ma.menuEntries)
                    if (e.menu != null && e.menu != window && e.menu.activeSelf) e.menu.SetActive(false);
            }
            Refresh();
        }
        window.SetActive(open);
    }

    public void Close()
    {
        if (window != null) window.SetActive(false);
    }

    void Refresh()
    {
        foreach (var r in refreshers) r();
    }

    // ---------- Radial menu ----------

    bool AddRadialSlice()
    {
        bool added = false;
        foreach (var selector in Resources.FindObjectsOfTypeAll<Xamin.CircleSelector>())
        {
            if (!selector.gameObject.scene.IsValid() || selector.Buttons == null) continue;

            GameObject template = selector.Buttons.Find(b => b != null && SusuVoicesMenu.IdOf(b) == RadialTemplateId);
            if (template == null) continue;
            if (selector.Buttons.Exists(b => b != null && SusuVoicesMenu.IdOf(b) == RadialId)) { added = true; continue; }
            if (selector.Buttons.Count >= Xamin.CircleSelector.MaxButtons)
            {
                Debug.LogWarning($"[SusuAI] The radial menu is full ({selector.Buttons.Count} buttons); the AI slice was not added.");
                added = true; // nothing more to try
                continue;
            }

            var slice = Instantiate(template, template.transform.parent);
            slice.name = "AI";
            foreach (var hook in slice.GetComponents<SusuChatRadialHook>()) DestroyImmediate(hook); // copied from the chat slice
            var button = slice.GetComponent<Xamin.Button>();
            button.id = RadialId;
            button.action = new UnityEvent(); // drop the copied "open chat" call
            button.unlocked = true;

            var sprite = GetIcon();
            button.image = sprite;
            var img = slice.GetComponent<Image>();
            if (img != null) img.sprite = sprite;

            // The selector instantiates copies of its buttons each time it opens; the hook re-attaches the action.
            slice.AddComponent<SusuAiRadialHook>();

            GameObject after = selector.Buttons.Find(b => b != null && SusuVoicesMenu.IdOf(b) == RadialAfterId) ?? template;
            selector.Buttons.Insert(selector.Buttons.IndexOf(after) + 1, slice);
            added = true;
            Debug.Log("[SusuAI] AI slice added to the radial menu.");
        }
        return added;
    }

    /// <summary>White glyph drawn once: a speech bubble with a four-point sparkle inside.</summary>
    Sprite GetIcon()
    {
        if (icon != null) return icon;
        const int n = 256;
        var tex = new Texture2D(n, n, TextureFormat.RGBA32, false) { name = "SusuAiIcon", wrapMode = TextureWrapMode.Clamp };
        var px = new Color32[n * n];
        for (int y = 0; y < n; y++)
        for (int x = 0; x < n; x++)
        {
            float fx = x + 0.5f - n / 2f, fy = y + 0.5f - n / 2f;
            // rounded-box outline (the bubble), centred a little above the middle
            float bx = Mathf.Abs(fx) - 62f, by = Mathf.Abs(fy - 14f) - 42f;
            float box = Mathf.Min(Mathf.Max(bx, by), 0f) + new Vector2(Mathf.Max(bx, 0f), Mathf.Max(by, 0f)).magnitude - 26f;
            float outline = Mathf.Clamp01(7f - Mathf.Abs(box));
            // tail: a small triangle under the bubble's left side
            float tail = (fy < -50f && fy > -92f && fx > -58f && fx < -58f + (fy + 92f) * 0.9f) ? 1f : 0f;
            // sparkle: |x|^0.5 + |y|^0.5 < r^0.5 gives the concave four-point star
            float sx = Mathf.Abs(fx), sy = Mathf.Abs(fy - 14f);
            float star = Mathf.Clamp01((Mathf.Sqrt(46f) - (Mathf.Sqrt(sx) + Mathf.Sqrt(sy))) * 6f);
            byte a = (byte)(255f * Mathf.Max(outline, Mathf.Max(tail, star)));
            px[y * n + x] = new Color32(255, 255, 255, a);
        }
        tex.SetPixels32(px);
        tex.Apply();
        icon = Sprite.Create(tex, new Rect(0, 0, n, n), new Vector2(0.5f, 0.5f), 100f);
        icon.name = "SusuAiIcon";
        return icon;
    }

    // ---------- Window ----------

    bool BuildWindow()
    {
        var template = SusuVoicesMenu.FindSceneObject(WindowTemplate);
        if (template == null) return false;

        // The template canvas is inactive in the scene, so the copy starts inactive too and none of its
        // scripts (localisation, alarm UI) run before they are stripped.
        window = Instantiate(template, template.transform.parent);
        window.name = "AiMenuCanvas";
        window.SetActive(false);

        foreach (var mb in window.GetComponentsInChildren<MonoBehaviour>(true))
            if (mb != null && mb.GetType().Name == "LocalizeStringEvent") DestroyImmediate(mb);

        var outer = window.transform.Find("OuterMenuAlarm");
        var main = window.transform.Find("MainMenuAlarm");
        if (outer == null || main == null) { Debug.LogWarning("[SusuAI] Alarm window layout changed."); return true; }
        outer.name = "OuterMenuAi";
        main.name = "MainMenuAi";

        var title = outer.Find("Header/TitleText")?.GetComponent<TMP_Text>();
        if (title != null) title.text = windowTitle;

        var footer = outer.Find("Footer");
        RectTransform buttonTemplate = null;
        if (footer != null)
        {
            var left = footer.Find("Add Alarm") as RectTransform;
            var right = footer.Find("Add Timer") as RectTransform;
            buttonTemplate = left != null ? left : right;
            SusuVoicesMenu.Rebind(left, "NEW CHAT", NewChat);
            SusuVoicesMenu.Rebind(right, "CLOSE", Close);
        }

        var content = main.Find("Viewport/Content") as RectTransform;
        if (content == null || buttonTemplate == null || title == null) { Debug.LogWarning("[SusuAI] Alarm window layout changed."); return true; }

        var old = new List<GameObject>();
        foreach (Transform child in content) old.Add(child.gameObject);
        foreach (var go in old) DestroyImmediate(go);
        foreach (var lg in content.GetComponents<LayoutGroup>()) lg.enabled = false;
        var fitter = content.GetComponent<ContentSizeFitter>();
        if (fitter != null) fitter.enabled = false;

        float width = content.rect.width > 100f ? content.rect.width : 633f;
        var toggleTemplate = SusuVoicesMenu.FindSceneObject(ToggleTemplate)?.transform as RectTransform;
        float y = TopPadding;

        y = Section(title, content, "GEMINI KEY (CHAT, FREE)", y, width);
        y = KeyField(content, y, "Gemini Key",
            () => SusuAiSettings.GeminiKeySource, () => SusuAiSettings.GeminiKey, v => SusuAiSettings.GeminiKey = v);

        y = Section(title, content, "ELEVENLABS KEY (VOICE AND MIC)", y, width);
        y = KeyField(content, y, "ElevenLabs Key",
            () => SusuAiSettings.ElevenLabsKeySource, () => SusuAiSettings.ElevenLabsKey, v => SusuAiSettings.ElevenLabsKey = v);

        y = Section(title, content, "CHAT MODEL", y, width);
        y = Picker(content, y, "Chat Model", SusuAiSettings.ChatModels, () => SusuAiSettings.ChatModel, v => SusuAiSettings.ChatModel = v);

        y = Section(title, content, "VOICE FOR CHAT REPLIES", y, width);
        y = Picker(content, y, "Voice Model", SusuAiSettings.VoiceModels, () => SusuAiSettings.VoiceModel, v => SusuAiSettings.VoiceModel = v);

        y = Section(title, content, "GEMINI VOICE", y, width);
        y = Picker(content, y, "Gemini Voice", SusuAiSettings.GeminiVoices, () => SusuAiSettings.GeminiVoice, v => SusuAiSettings.GeminiVoice = v);

        y = Section(title, content, "SUSU REPLIES IN", y, width);
        y = Choice(buttonTemplate, content, y,
            new[] { "SAME AS ME", "ENGLISH", "JAPANESE" }, new[] { "auto", "en", "ja" },
            () => SusuAiSettings.ReplyLanguage, v => SusuAiSettings.ReplyLanguage = v);

        y = Section(title, content, "I SPEAK IN (MICROPHONE)", y, width);
        y = Choice(buttonTemplate, content, y,
            new[] { "DETECT", "PORTUGUESE", "ENGLISH", "JAPANESE" }, new[] { "", "pt", "en", "ja" },
            () => SusuAiSettings.MicLanguage, v => SusuAiSettings.MicLanguage = v);

        if (toggleTemplate != null)
        {
            y = Section(title, content, "VOICE AND BODY", y, width);
            y = Check(toggleTemplate, content, y, width, "READ CHAT REPLIES ALOUD", () => SusuAiSettings.SpeakReplies, v => SusuAiSettings.SpeakReplies = v);
            y = Check(toggleTemplate, content, y, width, "SHOW AUTO-TALK TEXT OVER HER HEAD", () => SusuAiSettings.Balloon, v => SusuAiSettings.Balloon = v);
            y = Check(toggleTemplate, content, y, width, "EXTRA MOTIONS WHILE IDLE", () => SusuAiSettings.IdleMotions, v => SusuAiSettings.IdleMotions = v);
        }
        else Debug.LogWarning("[SusuAI] Toggle template not found - checkboxes skipped.");

        content.sizeDelta = new Vector2(content.sizeDelta.x, y + TopPadding);

        foreach (var ma in Resources.FindObjectsOfTypeAll<MenuActions>())
        {
            if (!ma.gameObject.scene.IsValid() || ma.menuEntries.Exists(e => e.menu == window)) continue;
            ma.menuEntries.Add(new MenuEntry { menu = window, blockMovement = true });
        }

        Refresh();
        Debug.Log("[SusuAI] AI settings window ready.");
        return true;
    }

    static float Section(TMP_Text titleTemplate, RectTransform content, string text, float y, float width)
    {
        SusuVoicesMenu.AddSectionTitle(titleTemplate, content, text, y, width);
        return y + SectionHeight;
    }

    /// <summary>One row of buttons where exactly one is selected (underlined and bright; the others dimmed).</summary>
    float Choice(RectTransform buttonTemplate, RectTransform content, float y, string[] labels, string[] values,
        System.Func<string> get, System.Action<string> set)
    {
        int n = labels.Length;
        const float gap = 8f;
        float w = (RowWidth - gap * (n - 1)) / n;
        var texts = new TMP_Text[n];
        for (int i = 0; i < n; i++)
        {
            var btn = (RectTransform)Instantiate(buttonTemplate.gameObject, content).transform;
            btn.name = "Option " + labels[i];
            btn.anchorMin = btn.anchorMax = new Vector2(0.5f, 1f);
            btn.pivot = new Vector2(0.5f, 0.5f);
            btn.sizeDelta = new Vector2(w, RowHeight);
            btn.anchoredPosition = new Vector2(-RowWidth * 0.5f + w * 0.5f + i * (w + gap), -(y + RowHeight * 0.5f));
            string value = values[i];
            texts[i] = SusuVoicesMenu.Rebind(btn, labels[i], () => set(value)); // the Changed event refreshes the row
        }
        refreshers.Add(() =>
        {
            string current = get();
            for (int i = 0; i < n; i++)
            {
                if (texts[i] == null) continue;
                bool selected = values[i] == current;
                texts[i].fontStyle = selected ? texts[i].fontStyle | FontStyles.Underline : texts[i].fontStyle & ~FontStyles.Underline;
                texts[i].alpha = selected ? 1f : 0.45f;
            }
        });
        return y + RowHeight + RowGap;
    }

    float Check(RectTransform toggleTemplate, RectTransform content, float y, float width, string label,
        System.Func<bool> get, System.Action<bool> set)
    {
        var rect = SusuVoicesMenu.CloneControl(toggleTemplate, content, "Toggle " + label);
        var info = rect.Find("INFO");
        if (info != null) DestroyImmediate(info.gameObject);
        rect.anchoredPosition = new Vector2(-width * 0.5f + 40f + rect.sizeDelta.x * 0.5f, -(y + ToggleHeight * 0.5f));
        SusuVoicesMenu.SetLabel(rect, label);
        var toggle = rect.GetComponent<Toggle>();
        if (toggle != null)
        {
            toggle.onValueChanged = new Toggle.ToggleEvent();
            toggle.onValueChanged.AddListener(v => set(v));
            refreshers.Add(() => toggle.SetIsOnWithoutNotify(get()));
        }
        return y + ToggleHeight;
    }

    /// <summary>A masked box for an API key. The saved key is never shown: the placeholder says where the key in
    /// use comes from. Typing a key and pressing Enter (or clicking away) saves it; the X forgets the saved one.</summary>
    float KeyField(RectTransform content, float y, string name,
        System.Func<SusuAiSettings.KeySource> source, System.Func<string> current, System.Action<string> save)
    {
        const float clear = 46f, gap = 8f;
        float fieldWidth = RowWidth - clear - gap;

        var input = SusuUi.Input(content, name, "", 21f, false, true);
        var rect = (RectTransform)input.transform;
        rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 1f);
        rect.sizeDelta = new Vector2(fieldWidth, RowHeight);
        rect.anchoredPosition = new Vector2(-RowWidth * 0.5f + fieldWidth * 0.5f, -(y + RowHeight * 0.5f));
        input.characterLimit = 300;
        input.onEndEdit.AddListener(text =>
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            input.SetTextWithoutNotify("");
            save(text.Trim()); // raises Changed, which refreshes the placeholder
        });

        var x = SusuUi.IconButton(content, name + " Forget", SusuUi.CloseIcon, clear, () => save(""));
        var xr = (RectTransform)x.transform;
        xr.anchorMin = xr.anchorMax = new Vector2(0.5f, 1f);
        xr.anchoredPosition = new Vector2(RowWidth * 0.5f - clear * 0.5f, -(y + RowHeight * 0.5f));

        refreshers.Add(() =>
        {
            string key = current();
            string tail = key.Length >= 8 ? key.Substring(key.Length - 4) : "";
            string hint;
            switch (source())
            {
                case SusuAiSettings.KeySource.Saved: hint = "Saved here (ends in " + tail + "). Paste to replace"; break;
                case SusuAiSettings.KeySource.Env: hint = "Using the key from .env (ends in " + tail + ")"; break;
                default: hint = "Paste your key and press Enter"; break;
            }
            if (input.placeholder is TMP_Text t) t.text = hint;
            x.gameObject.SetActive(source() == SusuAiSettings.KeySource.Saved);
        });
        return y + RowHeight + RowGap;
    }

    /// <summary>A dropdown over a short list of suggestions. A value set elsewhere (.env) that is not in the list
    /// is added at the end, so it is never silently replaced.</summary>
    float Picker(RectTransform content, float y, string name, SusuAiSettings.Option[] options,
        System.Func<string> get, System.Action<string> set)
    {
        var dropdown = SusuUi.Dropdown(content, name);
        if (dropdown == null) { Debug.LogWarning("[SusuAI] Dropdown template not found - " + name + " skipped."); return y; }

        var rect = (RectTransform)dropdown.transform;
        float scale = Mathf.Max(0.01f, rect.localScale.x);
        float height = rect.sizeDelta.y * scale;
        rect.sizeDelta = new Vector2(RowWidth / scale, rect.sizeDelta.y);
        SusuUi.FitDropdownRows(dropdown, RowWidth / scale);
        rect.anchoredPosition = new Vector2(0f, -(y + height * 0.5f));

        var ids = new List<string>();
        void Fill()
        {
            string current = get();
            ids.Clear();
            var labels = new List<string>();
            foreach (var o in options) { ids.Add(o.id); labels.Add(o.label); }
            if (!ids.Contains(current)) { ids.Add(current); labels.Add(current + " (custom)"); }
            dropdown.ClearOptions();
            dropdown.AddOptions(labels);
            dropdown.SetValueWithoutNotify(ids.IndexOf(current));
            dropdown.RefreshShownValue();
        }
        dropdown.onValueChanged.AddListener(i => { if (i >= 0 && i < ids.Count) set(ids[i]); });
        refreshers.Add(Fill);
        return y + height + RowGap;
    }

    /// <summary>Forgets the conversation so far (chat window and model context).</summary>
    public static void NewChat()
    {
        if (SusuChatWindow.Instance != null) SusuChatWindow.Instance.NewChat();
        else if (SusuChat.Instance != null) SusuChat.Instance.ClearHistory();
    }
}

/// <summary>Attached to the AI radial slice; every runtime copy wires its action to the AI window.</summary>
public class SusuAiRadialHook : MonoBehaviour
{
    void Awake()
    {
        var b = GetComponent<Xamin.Button>();
        if (b == null) return;
        if (b.action == null) b.action = new UnityEvent();
        b.action.AddListener(() => SusuAiMenu.Instance?.Toggle());
    }
}
