using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

/// <summary>
/// Small UI pieces shared by the MateSusu windows, all made at runtime: a rounded panel sprite, round icon
/// buttons (their glyphs are drawn into textures here, so no image assets are needed), a text input and a
/// dropdown copied from the settings menu so it follows the app's theme.
/// </summary>
public static class SusuUi
{
    const string DropdownTemplate = "Lang"; // a TMP_Dropdown in the settings menu

    static readonly Dictionary<string, Sprite> cache = new Dictionary<string, Sprite>();

    // ---------- Sprites ----------

    /// <summary>White rounded rectangle, 9-sliced so any size keeps its corner radius.</summary>
    public static Sprite Rounded(int radius = 20)
    {
        string key = "rounded" + radius;
        if (cache.TryGetValue(key, out var s) && s != null) return s;
        int n = radius * 2 + 4;
        var tex = new Texture2D(n, n, TextureFormat.RGBA32, false) { name = "SusuRounded", wrapMode = TextureWrapMode.Clamp };
        var px = new Color32[n * n];
        for (int y = 0; y < n; y++)
        for (int x = 0; x < n; x++)
        {
            float dx = Mathf.Max(0f, Mathf.Abs(x + 0.5f - n / 2f) - (n / 2f - radius));
            float dy = Mathf.Max(0f, Mathf.Abs(y + 0.5f - n / 2f) - (n / 2f - radius));
            float a = Mathf.Clamp01(radius - Mathf.Sqrt(dx * dx + dy * dy) + 0.5f);
            px[y * n + x] = new Color32(255, 255, 255, (byte)(a * 255f));
        }
        tex.SetPixels32(px);
        tex.Apply();
        s = Sprite.Create(tex, new Rect(0, 0, n, n), new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect,
            new Vector4(radius, radius, radius, radius));
        return cache[key] = s;
    }

    /// <summary>Round badge: dark translucent disc with a bright glyph. The glyph function returns 0..1 coverage
    /// for a point in a 64x64 square centred on the origin (x right, y up).</summary>
    static Sprite Badge(string key, Func<float, float, float> glyph)
    {
        if (cache.TryGetValue(key, out var s) && s != null) return s;
        const int n = 64;
        var tex = new Texture2D(n, n, TextureFormat.RGBA32, false) { name = key, wrapMode = TextureWrapMode.Clamp };
        var px = new Color32[n * n];
        for (int y = 0; y < n; y++)
        for (int x = 0; x < n; x++)
        {
            float fx = x + 0.5f - n / 2f, fy = y + 0.5f - n / 2f;
            float disc = Mathf.Clamp01(31f - Mathf.Sqrt(fx * fx + fy * fy));
            float g = Mathf.Clamp01(glyph(fx, fy));
            byte a = (byte)(255f * Mathf.Max(disc * 0.8f, g * disc));
            byte c = (byte)(255f * Mathf.Lerp(0.12f, 1f, g));
            px[y * n + x] = new Color32(c, c, c, a);
        }
        tex.SetPixels32(px);
        tex.Apply();
        return cache[key] = Sprite.Create(tex, new Rect(0, 0, n, n), new Vector2(0.5f, 0.5f), 100f);
    }

    static float Capsule(float x, float y, float half)
    {
        float cy = Mathf.Clamp(y, -half, half);
        return Mathf.Sqrt(x * x + (y - cy) * (y - cy));
    }

    static float RoundBoxOutline(float x, float y, float halfW, float halfH, float radius, float thickness)
    {
        float bx = Mathf.Abs(x) - halfW, by = Mathf.Abs(y) - halfH;
        float d = Mathf.Min(Mathf.Max(bx, by), 0f) + new Vector2(Mathf.Max(bx, 0f), Mathf.Max(by, 0f)).magnitude - radius;
        return Mathf.Clamp01(thickness - Mathf.Abs(d));
    }

    public static Sprite MicIcon => Badge("SusuMicIcon", (x, y) =>
    {
        float body = Mathf.Clamp01(7f - Capsule(x, y - 7f, 6f));
        float holderDist = Mathf.Abs(Mathf.Sqrt(x * x + (y - 3f) * (y - 3f)) - 11.5f);
        float holder = y < 4f ? Mathf.Clamp01(1.6f - holderDist) : 0f;
        float stem = Mathf.Abs(x) < 1.6f && y < -8f && y > -16f ? 1f : 0f;
        float foot = Mathf.Abs(x) < 7f && y < -15f && y > -18.5f ? 1f : 0f;
        return Mathf.Max(Mathf.Max(body, holder), Mathf.Max(stem, foot));
    });

    /// <summary>Arrow head pointing right (send).</summary>
    public static Sprite SendIcon => Badge("SusuSendIcon", (x, y) =>
    {
        if (x < -12f || x > 17f) return 0f;
        float outer = (17f - x) * 14f / 29f;           // half-height of the big triangle at this x
        float inner = x < -4f ? (-4f - x) * 14f / 8f : 0f; // notch cut from the back
        float ay = Mathf.Abs(y);
        return Mathf.Clamp01(outer - ay + 0.5f) * Mathf.Clamp01(ay - inner + 0.5f);
    });

    /// <summary>Speech bubble with a plus sign (new conversation).</summary>
    public static Sprite NewChatIcon => Badge("SusuNewChatIcon", (x, y) =>
    {
        float outline = RoundBoxOutline(x, y - 3f, 9f, 5f, 8f, 1.8f);
        float tail = (y < -9f && y > -18f && x > -10f && x < -10f + (y + 18f)) ? 1f : 0f;
        float plus = (Mathf.Abs(x) < 1.6f && Mathf.Abs(y - 3f) < 7f) || (Mathf.Abs(y - 3f) < 1.6f && Mathf.Abs(x) < 7f) ? 1f : 0f;
        return Mathf.Max(outline, Mathf.Max(tail, plus));
    });

    /// <summary>X (close).</summary>
    public static Sprite CloseIcon => Badge("SusuCloseIcon", (x, y) =>
    {
        if (Mathf.Abs(x) > 11f || Mathf.Abs(y) > 11f) return 0f;
        return Mathf.Clamp01(2.2f - Mathf.Min(Mathf.Abs(x - y), Mathf.Abs(x + y)) * 0.7071f);
    });

    /// <summary>Three sliders (settings).</summary>
    public static Sprite SettingsIcon => Badge("SusuSettingsIcon", (x, y) =>
    {
        float g = 0f;
        float[] rows = { 10f, 0f, -10f };
        float[] knobs = { 5f, -6f, 3f };
        for (int i = 0; i < 3; i++)
        {
            if (Mathf.Abs(x) < 14f) g = Mathf.Max(g, Mathf.Clamp01(1.3f - Mathf.Abs(y - rows[i])));
            float d = Mathf.Sqrt((x - knobs[i]) * (x - knobs[i]) + (y - rows[i]) * (y - rows[i]));
            g = Mathf.Max(g, Mathf.Clamp01(4.2f - d));
        }
        return g;
    });

    // ---------- Controls ----------

    /// <summary>Round icon button, anchored by the caller.</summary>
    public static Button IconButton(Transform parent, string name, Sprite icon, float size, UnityAction onClick)
    {
        var go = new GameObject(name, typeof(RectTransform));
        go.transform.SetParent(parent, false);
        go.layer = parent.gameObject.layer;
        var rect = (RectTransform)go.transform;
        rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0.5f, 0.5f);
        rect.sizeDelta = new Vector2(size, size);
        var image = go.AddComponent<Image>();
        image.sprite = icon;
        image.color = new Color(1f, 1f, 1f, 0.9f);
        var button = go.AddComponent<Button>();
        button.targetGraphic = image;
        button.navigation = new Navigation { mode = Navigation.Mode.None };
        var colors = button.colors;
        colors.highlightedColor = new Color(0.82f, 0.78f, 1f, 1f);
        colors.pressedColor = new Color(0.65f, 0.6f, 1f, 1f);
        colors.fadeDuration = 0.08f;
        button.colors = colors;
        if (onClick != null) button.onClick.AddListener(onClick);
        return button;
    }

    /// <summary>A text box built from scratch with the Japanese-capable font: rounded translucent background,
    /// placeholder, optional password masking, and for multi-line boxes Enter = submit, Shift+Enter = new line.</summary>
    public static TMP_InputField Input(Transform parent, string name, string placeholder, float fontSize, bool multiline, bool password)
    {
        var go = new GameObject(name, typeof(RectTransform));
        go.SetActive(false); // TMP_InputField wires itself up in OnEnable: give it all its parts first
        go.transform.SetParent(parent, false);
        go.layer = parent.gameObject.layer;
        var rect = (RectTransform)go.transform;
        rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0.5f, 0.5f);

        var bg = go.AddComponent<Image>();
        bg.sprite = Rounded();
        bg.type = Image.Type.Sliced;
        bg.color = new Color(1f, 1f, 1f, 0.09f);

        var area = new GameObject("Text Area", typeof(RectTransform), typeof(RectMask2D));
        area.transform.SetParent(go.transform, false);
        area.layer = go.layer;
        var areaRect = (RectTransform)area.transform;
        areaRect.anchorMin = Vector2.zero;
        areaRect.anchorMax = Vector2.one;
        areaRect.offsetMin = new Vector2(18f, 8f);
        areaRect.offsetMax = new Vector2(-18f, -8f);

        var font = SusuVoicesMenu.JapaneseFont();
        TextMeshProUGUI Make(string childName, Color color, FontStyles style)
        {
            var child = new GameObject(childName, typeof(RectTransform));
            child.transform.SetParent(area.transform, false);
            child.layer = go.layer;
            var r = (RectTransform)child.transform;
            r.anchorMin = Vector2.zero;
            r.anchorMax = Vector2.one;
            r.offsetMin = r.offsetMax = Vector2.zero;
            var t = child.AddComponent<TextMeshProUGUI>();
            if (font != null) t.font = font;
            t.fontSize = fontSize;
            t.color = color;
            t.fontStyle = style;
            t.alignment = multiline ? TextAlignmentOptions.TopLeft : TextAlignmentOptions.MidlineLeft;
            t.textWrappingMode = multiline ? TextWrappingModes.Normal : TextWrappingModes.NoWrap;
            t.raycastTarget = false;
            return t;
        }
        var hint = Make("Placeholder", new Color(1f, 1f, 1f, 0.42f), FontStyles.Italic);
        hint.text = placeholder;
        var text = Make("Text", Color.white, FontStyles.Normal);

        var input = go.AddComponent<TMP_InputField>();
        input.targetGraphic = bg;
        input.textViewport = areaRect;
        input.textComponent = text;
        input.placeholder = hint;
        if (font != null) input.fontAsset = font;
        input.pointSize = fontSize;
        input.lineType = multiline ? TMP_InputField.LineType.MultiLineSubmit : TMP_InputField.LineType.SingleLine;
        input.contentType = password ? TMP_InputField.ContentType.Password : TMP_InputField.ContentType.Standard;
        if (multiline) input.lineType = TMP_InputField.LineType.MultiLineSubmit; // contentType resets it
        input.caretColor = Color.white;
        input.customCaretColor = true;
        input.caretWidth = 2;
        input.selectionColor = new Color(0.55f, 0.45f, 1f, 0.5f);
        input.richText = false;
        input.navigation = new Navigation { mode = Navigation.Mode.None };
        input.transition = Selectable.Transition.None;
        go.SetActive(true);
        return input;
    }

    /// <summary>A dropdown copied from the settings menu (same theme), with only UI components left on it.</summary>
    public static TMP_Dropdown Dropdown(Transform parent, string name)
    {
        var template = SusuVoicesMenu.FindSceneObject(DropdownTemplate);
        if (template == null || template.GetComponent<TMP_Dropdown>() == null) return null;

        var go = UnityEngine.Object.Instantiate(template, parent);
        go.name = name;
        // Strip the app's own behaviours (localisation, settings binding) but keep everything the dropdown needs.
        foreach (var mb in go.GetComponentsInChildren<MonoBehaviour>(true))
        {
            if (mb == null) continue;
            string ns = mb.GetType().Namespace ?? "";
            if (ns != "UnityEngine.UI" && ns != "TMPro" && ns != "UnityEngine.EventSystems") UnityEngine.Object.DestroyImmediate(mb);
        }
        go.SetActive(true);
        var dropdown = go.GetComponent<TMP_Dropdown>();
        dropdown.onValueChanged = new TMP_Dropdown.DropdownEvent();
        // The template's texts are sized for short words; ours are model names in a much wider box.
        foreach (var label in new[] { dropdown.captionText, dropdown.itemText })
        {
            if (label == null) continue;
            label.enableAutoSizing = false;
            label.fontSize = 84f;
            label.characterSpacing = 0f;
            label.alignment = TextAlignmentOptions.MidlineLeft;
            label.textWrappingMode = TextWrappingModes.NoWrap;
            label.overflowMode = TextOverflowModes.Ellipsis;
        }
        dropdown.ClearOptions();
        var rect = (RectTransform)go.transform;
        rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 1f);
        rect.pivot = new Vector2(0.5f, 0.5f);
        return dropdown;
    }

    /// <summary>Call after giving the dropdown its width (in its own units). The template's rows have a fixed
    /// width made for a narrow box; this makes them span the list and keeps the row text inside it.</summary>
    public static void FitDropdownRows(TMP_Dropdown dropdown, float width)
    {
        var item = dropdown.template != null ? dropdown.template.GetComponentInChildren<Toggle>(true) : null;
        if (item == null) return;
        // Same left-anchored layout the template uses, just wide enough; the list's viewport clips the excess.
        var ir = (RectTransform)item.transform;
        float rowWidth = Mathf.Max(ir.sizeDelta.x, width * 2f);
        ir.sizeDelta = new Vector2(rowWidth, ir.sizeDelta.y);
        ir.anchoredPosition = new Vector2(rowWidth * 0.5f, ir.anchoredPosition.y);
        if (dropdown.itemText == null) return;
        var lr = dropdown.itemText.rectTransform;
        lr.anchorMin = new Vector2(0f, 0f);
        lr.anchorMax = new Vector2(0f, 1f);
        lr.pivot = new Vector2(0f, 0.5f);
        lr.sizeDelta = new Vector2(Mathf.Max(100f, width - 200f), 0f); // minus the scrollbar and some padding
        lr.anchoredPosition = new Vector2(40f, 0f);
    }
}
