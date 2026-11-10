using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Shows what Susu says by herself (auto-talk lines) floating over her head: just the Japanese text
/// with a soft shadow, no frame. It follows her head, fades in when the line starts and out shortly
/// after it ends. Lines the user clicks in the Voices window already have their caption there.
/// Turn off in the AI window (or SUSU_BALLOON = false in .env).
/// </summary>
public class SusuSpeechBalloon : MonoBehaviour
{
    [Tooltip("Text width as a fraction of the screen height.")]
    public float widthFactor = 0.62f;
    [Tooltip("Font size as a fraction of the screen height.")]
    public float fontFactor = 0.026f;
    [Tooltip("Gap between the top of the head and the text, as a fraction of the head-to-hips distance.")]
    public float headGap = 0.55f;
    [Tooltip("Seconds the text stays after the voice ends.")]
    public float linger = 1.5f;
    public float fadeSeconds = 0.3f;
    public Color textColor = Color.white;
    public Color shadowColor = new Color(0f, 0f, 0f, 0.9f);

    SusuVoiceLines voice;
    Canvas canvas;
    TMP_Text label;
    CanvasGroup group;
    Animator animator;
    Transform head, hips;
    Camera cam;
    float target, hideAt = -1f;
    string showingId;

    void Start()
    {
        voice = SusuVoiceLines.Instance;
        if (voice == null) { enabled = false; return; }
        voice.PhraseStarted += OnStarted;
        voice.PhraseFinished += OnFinished;
    }

    void OnDestroy()
    {
        if (voice != null)
        {
            voice.PhraseStarted -= OnStarted;
            voice.PhraseFinished -= OnFinished;
        }
        if (canvas != null) Destroy(canvas.gameObject);
    }

    void OnStarted(SusuVoiceLines.Phrase p)
    {
        if (p == null || !voice.CurrentIsAuto || !SusuAiSettings.Balloon) { Hide(); return; } // a clicked line replaces whatever was showing
        string text = string.IsNullOrEmpty(p.text) ? p.learn : p.text;
        if (string.IsNullOrEmpty(text)) return;

        Build();
        label.text = OpenRouterTTS.StripAudioTags(text);
        showingId = p.id;
        target = 1f;
        hideAt = -1f;
        Place();
    }

    void OnFinished(SusuVoiceLines.Phrase p)
    {
        if (p != null && p.id == showingId) hideAt = Time.unscaledTime + linger;
    }

    void Hide() { target = 0f; hideAt = -1f; showingId = null; }

    void Build()
    {
        if (canvas != null) return;

        var go = new GameObject("SusuSpeechBalloon");
        DontDestroyOnLoad(go);
        canvas = go.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 50; // above the avatar, never in the way of clicks (no raycaster)
        group = go.AddComponent<CanvasGroup>();
        group.alpha = 0f;
        group.blocksRaycasts = false;
        group.interactable = false;

        var textGo = new GameObject("Text", typeof(RectTransform));
        textGo.transform.SetParent(go.transform, false);
        var tmp = textGo.AddComponent<TextMeshProUGUI>();
        tmp.raycastTarget = false;
        tmp.richText = false;
        tmp.alignment = TextAlignmentOptions.Bottom;
        tmp.textWrappingMode = TextWrappingModes.Normal;
        tmp.overflowMode = TextOverflowModes.Overflow;
        tmp.color = textColor;
        var font = SusuVoicesMenu.JapaneseFont();
        if (font != null) tmp.font = font;

        // Shadow = the SDF shader's underlay: a blurred dark copy pushed down-right, a bit fatter than the glyphs
        // so the text also reads on a white window behind her.
        var mat = tmp.fontMaterial; // per-text instance
        mat.EnableKeyword("UNDERLAY_ON");
        mat.SetColor("_UnderlayColor", shadowColor);
        mat.SetFloat("_UnderlayOffsetX", 0.5f);
        mat.SetFloat("_UnderlayOffsetY", -0.5f);
        mat.SetFloat("_UnderlayDilate", 0.45f);
        mat.SetFloat("_UnderlaySoftness", 0.35f);
        tmp.UpdateMeshPadding();

        var rect = tmp.rectTransform;
        rect.anchorMin = rect.anchorMax = Vector2.zero; // positioned in screen pixels
        rect.pivot = new Vector2(0.5f, 0f);             // grows upwards from just above the head
        label = tmp;
    }

    void LateUpdate()
    {
        if (canvas == null) return;
        if (hideAt > 0f && Time.unscaledTime >= hideAt) Hide();

        group.alpha = Mathf.MoveTowards(group.alpha, target, Time.unscaledDeltaTime / Mathf.Max(0.01f, fadeSeconds));
        if (group.alpha > 0f) Place();
    }

    void Place()
    {
        if (animator == null || !animator.isActiveAndEnabled)
        {
            animator = SusuClipPlayer.FindAvatarAnimator();
            head = animator != null ? animator.GetBoneTransform(HumanBodyBones.Head) : null;
            hips = animator != null ? animator.GetBoneTransform(HumanBodyBones.Hips) : null;
        }
        if (cam == null) cam = Camera.main;
        if (head == null || cam == null) return;

        float h = Screen.height;
        label.fontSize = Mathf.Max(14f, h * fontFactor);
        var rect = label.rectTransform;
        float width = Mathf.Min(h * widthFactor, Screen.width - 24f);
        rect.sizeDelta = new Vector2(width, 10f);

        // A point above the hair: the head bone sits at the neck's top, so go up by part of the torso length.
        float torso = hips != null ? Vector3.Distance(head.position, hips.position) : 0.5f;
        Vector3 screen = cam.WorldToScreenPoint(head.position + Vector3.up * torso * headGap);
        if (screen.z <= 0f) return;

        // Keep the whole block on screen (she can stand at an edge, or her head near the top).
        float textHeight = label.GetPreferredValues(label.text, width, 0f).y;
        float x = Mathf.Clamp(screen.x, width * 0.5f + 12f, Screen.width - width * 0.5f - 12f);
        float y = Mathf.Clamp(screen.y, 12f, Mathf.Max(12f, h - textHeight - 12f));
        rect.anchoredPosition = new Vector2(x, y);
    }
}
