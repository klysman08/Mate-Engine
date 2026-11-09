using System;
using UnityEngine;

/// <summary>
/// Named body motions for Susu: each one is a time window inside a humanoid AnimationClip
/// (long mocap takes are not cut, we just play the good part). Built by
/// Tools > MateSusu > Build Motion Library and loaded from Resources at runtime.
/// </summary>
public class SusuMotionLibrary : ScriptableObject
{
    public const string ResourcePath = "SusuMotionLibrary";

    [Serializable]
    public class Motion
    {
        public string name;
        public AnimationClip clip;
        public float start;
        public float end; // <= 0 means the end of the clip
        public float Duration => Mathf.Max(0.1f, (end > 0f ? end : clip != null ? clip.length : 0f) - start);
    }

    public Motion[] motions = new Motion[0];

    static SusuMotionLibrary cached;

    public static SusuMotionLibrary Load()
    {
        if (cached == null) cached = Resources.Load<SusuMotionLibrary>(ResourcePath);
        return cached;
    }

    public Motion Find(string motionName)
    {
        if (string.IsNullOrEmpty(motionName)) return null;
        foreach (var m in motions)
            if (m != null && m.clip != null && string.Equals(m.name, motionName, StringComparison.OrdinalIgnoreCase))
                return m;
        return null;
    }
}
