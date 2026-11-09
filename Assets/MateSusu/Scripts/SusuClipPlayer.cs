using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;

/// <summary>
/// Plays a ready-made humanoid AnimationClip on Susu on top of the normal Mate Engine Animator,
/// fading it in and out through a second playable output, so the original controller
/// (idle, blink, drag, sleep...) is never replaced or edited.
/// </summary>
public class SusuClipPlayer : MonoBehaviour
{
    public static SusuClipPlayer Instance { get; private set; }

    public bool IsPlaying => graph.IsValid() && targetWeight > 0f;

    PlayableGraph graph;
    AnimationPlayableOutput output;
    AnimationClipPlayable clipPlayable;
    float weight, targetWeight, fadeTime = 0.35f, stopAt;

    void Awake() { Instance = this; }

    void OnDestroy()
    {
        if (graph.IsValid()) graph.Destroy();
        if (Instance == this) Instance = null;
    }

    public static Animator FindAvatarAnimator()
    {
        foreach (var a in FindObjectsByType<Animator>(FindObjectsSortMode.None))
            if (a.isActiveAndEnabled && a.avatar != null && a.isHuman && a.runtimeAnimatorController != null)
                return a;
        return null;
    }

    /// <summary>Play <paramref name="clip"/> from <paramref name="start"/> seconds for <paramref name="duration"/> seconds (≤0 = to the end).</summary>
    public bool Play(AnimationClip clip, float start = 0f, float duration = 0f, float fade = 0.35f)
    {
        var animator = FindAvatarAnimator();
        if (clip == null || animator == null) return false;
        if (graph.IsValid()) graph.Destroy();

        graph = PlayableGraph.Create("SusuClipPlayer");
        graph.SetTimeUpdateMode(DirectorUpdateMode.GameTime);
        clipPlayable = AnimationClipPlayable.Create(graph, clip);
        clipPlayable.SetApplyFootIK(true);
        clipPlayable.SetTime(start);
        output = AnimationPlayableOutput.Create(graph, "Susu", animator);
        output.SetSourcePlayable(clipPlayable);
        output.SetWeight(0f);
        graph.Play();

        fadeTime = Mathf.Max(0.05f, fade);
        weight = 0f;
        targetWeight = 1f;
        float end = duration > 0f ? start + duration : clip.length;
        stopAt = clip.isLooping && duration <= 0f ? float.MaxValue : end;
        return true;
    }

    public void Stop() { targetWeight = 0f; }

    void Update()
    {
        if (!graph.IsValid()) return;
        if (targetWeight > 0f && clipPlayable.GetTime() >= stopAt - fadeTime) targetWeight = 0f;

        weight = Mathf.MoveTowards(weight, targetWeight, Time.deltaTime / fadeTime);
        output.SetWeight(Mathf.SmoothStep(0f, 1f, weight));
        if (weight <= 0f && targetWeight <= 0f) graph.Destroy();
    }
}
