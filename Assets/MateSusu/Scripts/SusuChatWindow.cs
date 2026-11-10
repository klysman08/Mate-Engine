using System.Collections;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

/// <summary>
/// The chat with Susu. A window in the app's own frame (cloned from the Alarm window, like Voices and AI):
/// messages as bubbles - yours on the right, hers on the left - that scroll, and one row at the bottom with
/// the text box, the microphone and send. The header has new chat, AI settings and close.
/// It talks to OpenRouter only and replaces the old chat panel, whose local model is never started:
/// the radial "chat" slice and anything else that opens the old panel lands here.
/// </summary>
public class SusuChatWindow : MonoBehaviour
{
    public static SusuChatWindow Instance { get; private set; }

    const string WindowTemplate = "AlarmMenuCanvas";
    const string RadialId = "chat";
    const string OldPanelName = "ChatMenuPanel";
    const string LocalModelSettings = "= AI"; // settings section with the local model's prompt and context size

    // Message list, in the scroll content's units
    const float Margin = 18f, PadX = 18f, PadY = 12f, Gap = 12f, TopPad = 18f, FontSize = 23f, MaxBubble = 0.8f;
    // Bottom row and header buttons, in the window frame's units (650 x 950, origin at its centre)
    const float RowY = -390f, RowHeight = 84f, RoundButton = 62f, HeaderButton = 46f;

    static readonly Color MineColor = new Color(0.47f, 0.37f, 0.98f, 0.92f);
    static readonly Color HersColor = new Color(1f, 1f, 1f, 0.11f);
    static readonly Color ErrorColor = new Color(0.95f, 0.35f, 0.4f, 0.35f);
    static readonly Color ListeningColor = new Color(1f, 0.3f, 0.36f, 1f);
    static readonly Color BusyColor = new Color(1f, 0.78f, 0.25f, 1f);
    static readonly Color IdleColor = new Color(1f, 1f, 1f, 0.9f);

    const string Placeholder = "Message Susu...";

    class Message
    {
        public RectTransform bubble;
        public Image background;
        public TMP_Text text;
        public bool mine;
    }

    GameObject window, oldPanel;
    RectTransform content;
    ScrollRect scroll;
    TMP_InputField input;
    TMP_Text emptyHint;
    Image micImage;
    Button sendButton;
    readonly List<Message> messages = new List<Message>();
    bool busy, layoutDirty, stickToBottom;
    float nextRadialCheck, placeholderResetAt = -1f;
    Animator avatar;
    static readonly int IsTalking = Animator.StringToHash("isTalking");

    public bool IsOpen => window != null && window.activeSelf;
    public int MessageCount => messages.Count;
    public bool IsBusy => busy;

    void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(this); return; }
        Instance = this;
    }

    void OnDestroy()
    {
        var mic = SusuChatMic.Instance;
        if (mic != null)
        {
            mic.StateChanged -= OnMicState;
            mic.Heard -= OnMicHeard;
            mic.Problem -= Flash;
        }
        SusuAiSettings.Changed -= UpdateEmptyHint;
        if (Instance == this) Instance = null;
    }

    IEnumerator Start()
    {
        bool built = false;
        for (int attempt = 0; attempt < 60 && !built; attempt++)
        {
            built = BuildWindow();
            if (!built) yield return new WaitForSecondsRealtime(0.5f);
        }
        if (!built) { Debug.LogWarning("[SusuChat] Could not build the chat window (template not found)."); yield break; }
        RetireLocalModel();
    }

    void Update()
    {
        if (window == null) return;

        // The radial menu is rebuilt by other scripts now and then: keep its chat slice pointing here.
        if (Time.unscaledTime >= nextRadialCheck)
        {
            nextRadialCheck = Time.unscaledTime + 2f;
            TakeOverRadialSlice();
        }

        // A hotkey or another script opened the old panel: show this window instead.
        if (oldPanel != null && oldPanel.activeSelf)
        {
            oldPanel.SetActive(false);
            if (!IsOpen) Toggle();
        }

        if (placeholderResetAt > 0f && Time.unscaledTime >= placeholderResetAt)
        {
            placeholderResetAt = -1f;
            OnMicState(SusuChatMic.Instance != null ? SusuChatMic.Instance.Current : SusuChatMic.State.Idle);
        }
    }

    void LateUpdate()
    {
        if (!layoutDirty || !IsOpen) return;
        layoutDirty = false;
        Relayout();
        if (stickToBottom)
        {
            Canvas.ForceUpdateCanvases();
            scroll.verticalNormalizedPosition = 0f;
        }
    }

    // ---------- Open / close ----------

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
        }
        else if (SusuChatMic.Instance != null) SusuChatMic.Instance.Cancel();

        window.SetActive(open);
        if (open)
        {
            UpdateEmptyHint();
            layoutDirty = stickToBottom = true;
            input.ActivateInputField();
        }
    }

    public void Close()
    {
        if (IsOpen) Toggle();
    }

    void OpenAiSettings()
    {
        if (SusuAiMenu.Instance != null && !SusuAiMenu.Instance.IsOpen) SusuAiMenu.Instance.Toggle(); // closes this window
    }

    // ---------- Conversation ----------

    /// <summary>Sends a message to Susu. False when it is empty or she is still answering the last one.</summary>
    public bool Send(string text)
    {
        if (window == null || busy || string.IsNullOrWhiteSpace(text)) return false;
        text = text.Trim();

        var chat = OpenRouterChat.Instance;
        Add(text, true);
        var reply = Add("...", false);
        stickToBottom = true;

        if (chat == null) { Fail(reply, "Chat is not available."); return true; }

        busy = true;
        sendButton.interactable = false;
        SetTalking(true);
        chat.Chat(text, chat.BuildSystemPrompt(null),
            partial => SetText(reply, OpenRouterTTS.StripAudioTags(partial)),
            full =>
            {
                SetText(reply, OpenRouterTTS.StripAudioTags(full));
                Done();
                // Keep the talking animation while the voice plays.
                var tts = OpenRouterTTS.Instance;
                if (tts == null || !tts.Speak(full, null, () => SetTalking(false))) SetTalking(false);
            },
            error =>
            {
                Fail(reply, error);
                Done();
                SetTalking(false);
            });
        return true;
    }

    void Done()
    {
        busy = false;
        if (sendButton != null) sendButton.interactable = true;
    }

    void Fail(Message m, string error)
    {
        if (m.background != null) m.background.color = ErrorColor;
        SetText(m, error);
    }

    /// <summary>Forgets the conversation: empties the window and the context sent to the model.</summary>
    public void NewChat()
    {
        if (OpenRouterChat.Instance != null)
        {
            OpenRouterChat.Instance.Cancel();
            OpenRouterChat.Instance.ClearHistory();
        }
        if (OpenRouterTTS.Instance != null) OpenRouterTTS.Instance.Stop();
        foreach (var m in messages) if (m.bubble != null) Destroy(m.bubble.gameObject);
        messages.Clear();
        Done();
        SetTalking(false);
        UpdateEmptyHint();
        layoutDirty = stickToBottom = true;
        if (IsOpen && input != null) input.ActivateInputField();
        Debug.Log("[SusuChat] New chat session.");
    }

    void SetTalking(bool talking)
    {
        if (avatar == null || !avatar.isActiveAndEnabled) avatar = SusuClipPlayer.FindAvatarAnimator();
        if (avatar == null) return;
        foreach (var p in avatar.parameters)
            if (p.nameHash == IsTalking) { avatar.SetBool(IsTalking, talking); return; }
    }

    // ---------- Messages ----------

    Message Add(string text, bool mine)
    {
        var go = new GameObject(mine ? "Me" : "Susu", typeof(RectTransform));
        go.transform.SetParent(content, false);
        go.layer = content.gameObject.layer;
        var rect = (RectTransform)go.transform;
        rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 1f);
        rect.pivot = new Vector2(mine ? 1f : 0f, 1f); // grows from its own side

        var bg = go.AddComponent<Image>();
        bg.sprite = SusuUi.Rounded();
        bg.type = Image.Type.Sliced;
        bg.color = mine ? MineColor : HersColor;
        bg.raycastTarget = false;

        var textGo = new GameObject("Text", typeof(RectTransform));
        textGo.transform.SetParent(go.transform, false);
        textGo.layer = go.layer;
        var tr = (RectTransform)textGo.transform;
        tr.anchorMin = Vector2.zero;
        tr.anchorMax = Vector2.one;
        tr.offsetMin = new Vector2(PadX, PadY);
        tr.offsetMax = new Vector2(-PadX, -PadY);
        var tmp = textGo.AddComponent<TextMeshProUGUI>();
        var font = SusuVoicesMenu.JapaneseFont();
        if (font != null) tmp.font = font;
        tmp.fontSize = FontSize;
        tmp.color = Color.white;
        tmp.alignment = TextAlignmentOptions.TopLeft;
        tmp.textWrappingMode = TextWrappingModes.Normal;
        tmp.richText = false;
        tmp.raycastTarget = false;
        tmp.text = text;

        var m = new Message { bubble = rect, background = bg, text = tmp, mine = mine };
        messages.Add(m);
        if (emptyHint != null) emptyHint.gameObject.SetActive(false);
        layoutDirty = true;
        return m;
    }

    void SetText(Message m, string text)
    {
        if (m == null || m.text == null) return;
        // Follow the reply as it grows only when the user has not scrolled up to read something.
        stickToBottom = scroll == null || scroll.verticalNormalizedPosition <= 0.02f || content.rect.height <= ((RectTransform)scroll.transform).rect.height;
        m.text.text = string.IsNullOrEmpty(text) ? "..." : text;
        layoutDirty = true;
    }

    float ContentWidth()
    {
        float w = ((RectTransform)content.parent).rect.width;
        return w > 100f ? w : 633f;
    }

    void Relayout()
    {
        float width = ContentWidth();
        float maxText = width * MaxBubble - 2f * PadX;
        float y = TopPad;
        foreach (var m in messages)
        {
            Vector2 pref = m.text.GetPreferredValues(m.text.text, maxText, 0f);
            float w = Mathf.Min(maxText, Mathf.Ceil(pref.x) + 1f) + 2f * PadX;
            float h = Mathf.Ceil(pref.y) + 2f * PadY;
            m.bubble.sizeDelta = new Vector2(w, h);
            m.bubble.anchoredPosition = new Vector2(m.mine ? width * 0.5f - Margin : -width * 0.5f + Margin, -y);
            y += h + Gap;
        }
        content.sizeDelta = new Vector2(content.sizeDelta.x, Mathf.Max(y + TopPad, 10f));
    }

    void UpdateEmptyHint()
    {
        if (emptyHint == null) return;
        emptyHint.gameObject.SetActive(messages.Count == 0);
        bool hasKey = SusuAiSettings.OpenRouterKey.Length > 0;
        emptyHint.text = hasKey
            ? "Say hi to Susu!\n<size=75%>Type below, or tap the microphone and talk.</size>"
            : "Susu needs an OpenRouter API key to chat.\n<size=75%>Open AI settings (the sliders button above) and paste yours.</size>";
    }

    // ---------- Microphone ----------

    void OnMicState(SusuChatMic.State s)
    {
        if (micImage != null)
            micImage.color = s == SusuChatMic.State.Listening ? ListeningColor : s == SusuChatMic.State.Transcribing ? BusyColor : IdleColor;
        SetPlaceholder(s == SusuChatMic.State.Listening ? "Listening... tap the mic again to send"
            : s == SusuChatMic.State.Transcribing ? "Transcribing..." : Placeholder);
        placeholderResetAt = -1f;
    }

    void OnMicHeard(string text)
    {
        if (!IsOpen) return;
        // Still waiting for her last answer: leave the text in the box instead of losing it.
        if (!Send(text)) input.text = text;
    }

    void Flash(string message)
    {
        SetPlaceholder(message);
        placeholderResetAt = Time.unscaledTime + 3f;
    }

    void SetPlaceholder(string text)
    {
        if (input != null && input.placeholder is TMP_Text t) t.text = text;
    }

    // ---------- Window ----------

    bool BuildWindow()
    {
        var template = SusuVoicesMenu.FindSceneObject(WindowTemplate);
        if (template == null) return false;

        // The template canvas is inactive in the scene, so the copy starts inactive too and none of its
        // scripts (localisation, alarm UI) run before they are stripped.
        window = Instantiate(template, template.transform.parent);
        window.name = "ChatWindowCanvas";
        window.SetActive(false);

        foreach (var mb in window.GetComponentsInChildren<MonoBehaviour>(true))
            if (mb != null && mb.GetType().Name == "LocalizeStringEvent") DestroyImmediate(mb);

        var outer = window.transform.Find("OuterMenuAlarm") as RectTransform;
        var main = window.transform.Find("MainMenuAlarm") as RectTransform;
        if (outer == null || main == null) { Debug.LogWarning("[SusuChat] Alarm window layout changed."); return true; }
        outer.name = "OuterMenuChat";
        main.name = "MainMenuChat";

        var title = outer.Find("Header/TitleText")?.GetComponent<TMP_Text>();
        if (title != null) title.text = "SUSU";

        // Header: new chat, AI settings, close - on the title's line, at the right edge.
        // (Plain local offsets: world positions are meaningless while the cloned canvas is still inactive.)
        float headerY = title != null
            ? title.transform.parent.localPosition.y + title.transform.localPosition.y
            : outer.rect.height * 0.5f - 46f;
        float right = outer.rect.width * 0.5f - 30f - HeaderButton * 0.5f;
        PlaceHeader(SusuUi.IconButton(outer, "Close", SusuUi.CloseIcon, HeaderButton, Close), right, headerY);
        PlaceHeader(SusuUi.IconButton(outer, "AI Settings", SusuUi.SettingsIcon, HeaderButton, OpenAiSettings), right - (HeaderButton + 10f), headerY);
        PlaceHeader(SusuUi.IconButton(outer, "New Chat", SusuUi.NewChatIcon, HeaderButton, NewChat), right - 2f * (HeaderButton + 10f), headerY);

        // Bottom row instead of the footer buttons: text box, microphone, send.
        var footer = outer.Find("Footer");
        if (footer != null)
            foreach (Transform child in footer) child.gameObject.SetActive(false);

        float half = outer.rect.width * 0.5f - 25f;
        float sendX = half - RoundButton * 0.5f;
        float micX = sendX - RoundButton - 8f;
        float inputRight = micX - RoundButton * 0.5f - 10f;

        input = SusuUi.Input(outer, "Chat Input", Placeholder, FontSize, true, false);
        var inputRect = (RectTransform)input.transform;
        inputRect.sizeDelta = new Vector2(inputRight + half, RowHeight);
        inputRect.anchoredPosition = new Vector2((inputRight - half) * 0.5f, RowY);
        input.characterLimit = 2000;
        input.onSubmit.AddListener(SubmitInput);

        var micButton = SusuUi.IconButton(outer, "Mic", SusuUi.MicIcon, RoundButton, ToggleMic);
        ((RectTransform)micButton.transform).anchoredPosition = new Vector2(micX, RowY);
        micButton.transition = Selectable.Transition.None; // its colour shows the microphone state
        micImage = micButton.GetComponent<Image>();

        sendButton = SusuUi.IconButton(outer, "Send", SusuUi.SendIcon, RoundButton, () => SubmitInput(input.text));
        ((RectTransform)sendButton.transform).anchoredPosition = new Vector2(sendX, RowY);

        // Message list: we place the bubbles ourselves.
        scroll = main.GetComponent<ScrollRect>();
        content = main.Find("Viewport/Content") as RectTransform;
        if (content == null || scroll == null) { Debug.LogWarning("[SusuChat] Alarm window layout changed."); return true; }

        var old = new List<GameObject>();
        foreach (Transform child in content) old.Add(child.gameObject);
        foreach (var go in old) DestroyImmediate(go);
        foreach (var lg in content.GetComponents<LayoutGroup>()) lg.enabled = false;
        var fitter = content.GetComponent<ContentSizeFitter>();
        if (fitter != null) fitter.enabled = false;
        content.sizeDelta = new Vector2(0f, 10f);
        scroll.horizontal = false;

        if (title != null)
        {
            var hintGo = Instantiate(title.gameObject, main);
            hintGo.name = "Empty Hint";
            var hr = (RectTransform)hintGo.transform;
            hr.localScale = Vector3.one;
            hr.anchorMin = hr.anchorMax = hr.pivot = new Vector2(0.5f, 0.5f);
            hr.sizeDelta = new Vector2(480f, 200f);
            hr.anchoredPosition = Vector2.zero;
            emptyHint = hintGo.GetComponent<TMP_Text>();
            emptyHint.alignment = TextAlignmentOptions.Center;
            emptyHint.enableAutoSizing = false;
            emptyHint.fontSize = 26f;
            emptyHint.characterSpacing = 0f;
            emptyHint.fontStyle = FontStyles.Normal;
            emptyHint.textWrappingMode = TextWrappingModes.Normal;
            emptyHint.richText = true;
            emptyHint.alpha = 0.6f;
            emptyHint.raycastTarget = false;
        }

        foreach (var ma in Resources.FindObjectsOfTypeAll<MenuActions>())
        {
            if (!ma.gameObject.scene.IsValid() || ma.menuEntries.Exists(e => e.menu == window)) continue;
            ma.menuEntries.Add(new MenuEntry { menu = window, blockMovement = true });
        }

        var mic = SusuChatMic.Instance;
        if (mic != null)
        {
            mic.StateChanged += OnMicState;
            mic.Heard += OnMicHeard;
            mic.Problem += Flash;
        }
        SusuAiSettings.Changed += UpdateEmptyHint;
        UpdateEmptyHint();

        Debug.Log("[SusuChat] Chat window ready.");
        return true;
    }

    static void PlaceHeader(Button b, float x, float y) => ((RectTransform)b.transform).anchoredPosition = new Vector2(x, y);

    void SubmitInput(string text)
    {
        // Shift+Enter is a new line, not a send.
        if (Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift)) return;
        if (Send(text)) input.text = "";
        input.ActivateInputField();
    }

    void ToggleMic()
    {
        if (SusuChatMic.Instance != null) SusuChatMic.Instance.Toggle();
        input.ActivateInputField();
    }

    // ---------- Replacing the old chat and its local model ----------

    /// <summary>The old panel holds the local LLM (LLMUnity). It is never shown again, its model objects are
    /// switched off so nothing loads even if the panel gets activated, and its settings section is hidden.</summary>
    void RetireLocalModel()
    {
        oldPanel = SusuVoicesMenu.FindSceneObject(OldPanelName);
        if (oldPanel != null)
        {
            oldPanel.SetActive(false);
            foreach (var mb in oldPanel.GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (mb == null) continue;
                string ns = mb.GetType().Namespace ?? "";
                if (ns == "LLMUnity" || ns == "LLMUnitySamples") mb.gameObject.SetActive(false);
            }
        }
        var section = SusuVoicesMenu.FindSceneObject(LocalModelSettings);
        if (section != null) section.SetActive(false);
        TakeOverRadialSlice();
    }

    /// <summary>Points the radial menu's chat slice at this window.</summary>
    void TakeOverRadialSlice()
    {
        foreach (var selector in Resources.FindObjectsOfTypeAll<Xamin.CircleSelector>())
        {
            if (!selector.gameObject.scene.IsValid() || selector.Buttons == null) continue;
            foreach (var b in selector.Buttons)
            {
                if (b == null || SusuVoicesMenu.IdOf(b) != RadialId || b.GetComponent<SusuChatRadialHook>() != null) continue;
                b.GetComponent<Xamin.Button>().action = new UnityEvent(); // drop "show the old panel"
                // The selector instantiates copies of its buttons each time it opens; the hook re-attaches the action.
                b.AddComponent<SusuChatRadialHook>();
                Debug.Log("[SusuChat] Radial chat slice now opens the new chat window.");
            }
        }
    }
}

/// <summary>Attached to the radial chat slice; every runtime copy wires its action to the chat window.</summary>
public class SusuChatRadialHook : MonoBehaviour
{
    void Awake()
    {
        var b = GetComponent<Xamin.Button>();
        if (b == null) return;
        if (b.action == null) b.action = new UnityEvent();
        b.action.AddListener(() => SusuChatWindow.Instance?.Toggle());
    }
}
