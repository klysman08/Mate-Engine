using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Adds Susu's own dances to Mate Engine's "dance when music plays". Mate Engine decides when she dances
/// (its isDancing flag) and keeps cycling its built-in dance clips; while that flag is on, this component
/// takes turns with it: sometimes it plays one of the library motions marked as a dance on top
/// (through <see cref="SusuClipPlayer"/>), sometimes it leaves the built-in dances showing.
/// The animator controller itself is not edited.
/// </summary>
public class SusuDances : MonoBehaviour
{
    static readonly int IsDancing = Animator.StringToHash("isDancing");

    [Tooltip("Chance that the next turn is one of Susu's dances instead of a Mate Engine one.")]
    [Range(0f, 1f)] public float susuDanceChance = 0.6f;
    [Tooltip("Seconds a Mate Engine dance turn lasts (min, max).")]
    public Vector2 builtInTurnSeconds = new Vector2(15f, 30f);
    [Tooltip("Seconds of Mate Engine dancing before the first turn is chosen.")]
    public float firstTurnDelay = 1.5f;
    public float fade = 0.6f;

    Animator animator;
    bool wasDancing, mine;
    float nextTurnAt;
    string lastDance;
    readonly List<SusuMotionLibrary.Motion> dances = new List<SusuMotionLibrary.Motion>();
    bool loaded;

    void Update()
    {
        if (animator == null || !animator.isActiveAndEnabled) animator = SusuClipPlayer.FindAvatarAnimator();
        var player = SusuClipPlayer.Instance;
        if (animator == null || player == null) return;

        bool dancing = HasDancingFlag(animator) && animator.GetBool(IsDancing);
        if (!dancing)
        {
            if (mine) player.Stop(); // music stopped: let the clip fade out with Mate Engine's dance
            mine = wasDancing = false;
            return;
        }

        if (!wasDancing)
        {
            wasDancing = true;
            nextTurnAt = Time.time + firstTurnDelay;
            return;
        }
        if (Time.time < nextTurnAt) return;

        var dance = Random.value < susuDanceChance ? PickDance() : null;
        if (dance != null && player.Play(dance.clip, dance.start, dance.PlayTime, fade, dance.Speed, true, !dance.noFootIK))
        {
            mine = true;
            lastDance = dance.name;
            nextTurnAt = Time.time + Mathf.Max(1f, dance.PlayTime - fade); // next turn cross-fades from this one
        }
        else
        {
            mine = false; // the clip player fades out by itself when its clip ends
            nextTurnAt = Time.time + Random.Range(builtInTurnSeconds.x, builtInTurnSeconds.y);
        }
    }

    SusuMotionLibrary.Motion PickDance()
    {
        if (!loaded)
        {
            var lib = SusuMotionLibrary.Load();
            if (lib == null) return null;
            loaded = true;
            foreach (var m in lib.motions)
                if (m != null && m.dance && m.clip != null) dances.Add(m);
            Debug.Log($"[SusuMotions] {dances.Count} extra dances available.");
        }
        if (dances.Count == 0) return null;
        if (dances.Count == 1) return dances[0];

        SusuMotionLibrary.Motion pick;
        do pick = dances[Random.Range(0, dances.Count)];
        while (pick.name == lastDance);
        return pick;
    }

    static bool HasDancingFlag(Animator a)
    {
        foreach (var p in a.parameters)
            if (p.nameHash == IsDancing) return true;
        return false;
    }
}
