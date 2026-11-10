using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Keeps Susu from looking stuck on the same few idle loops: every so often, when nothing else is going on
/// (not speaking, chatting, dancing, dragged, sitting...), she plays one of the library motions marked as idle -
/// looking around, stretching, checking her phone, striking a pose - on top of the Mate Engine idle.
/// Turn off in the AI window (or SUSU_IDLE_MOTIONS = false in .env); SUSU_IDLE_MIN / SUSU_IDLE_MAX set the pause in seconds.
/// </summary>
public class SusuIdleMotions : MonoBehaviour
{
    [Tooltip("Seconds between idle motions (min, max).")]
    public Vector2 pauseSeconds = new Vector2(18f, 40f);
    [Tooltip("How many of the last motions are not repeated.")]
    public int memory = 8;
    public float fade = 0.5f;

    float nextAt;
    Animator animator;
    List<SusuMotionLibrary.Motion> pool;
    readonly List<string> recent = new List<string>();

    void Awake()
    {
        pauseSeconds.x = Mathf.Max(3f, MateSusuEnv.GetFloat("SUSU_IDLE_MIN", pauseSeconds.x));
        pauseSeconds.y = Mathf.Max(pauseSeconds.x, MateSusuEnv.GetFloat("SUSU_IDLE_MAX", pauseSeconds.y));
        Schedule();
    }

    void Schedule() => nextAt = Time.time + Random.Range(pauseSeconds.x, pauseSeconds.y);

    void Update()
    {
        if (Time.time < nextAt) return;
        if (!SusuAiSettings.IdleMotions) { Schedule(); return; }

        var player = SusuClipPlayer.Instance;
        var tts = OpenRouterTTS.Instance;
        if (animator == null || !animator.isActiveAndEnabled) animator = SusuClipPlayer.FindAvatarAnimator();

        bool busy = player == null || player.IsPlaying
                    || (tts != null && tts.IsSpeaking)
                    || (OpenRouterChat.Instance != null && OpenRouterChat.Instance.IsBusy)
                    || SusuClipPlayer.AvatarBusy(animator);
        if (busy)
        {
            // Something else is moving her: count the pause again from when it ends.
            Schedule();
            return;
        }

        var m = Pick();
        if (m != null && player.Play(m.clip, m.start, m.PlayTime, fade, m.Speed, false, !m.noFootIK))
        {
            recent.Add(m.name);
            if (recent.Count > memory) recent.RemoveAt(0);
            nextAt = Time.time + m.PlayTime + Random.Range(pauseSeconds.x, pauseSeconds.y);
        }
        else Schedule();
    }

    SusuMotionLibrary.Motion Pick()
    {
        if (pool == null)
        {
            var lib = SusuMotionLibrary.Load();
            if (lib == null) return null;
            pool = new List<SusuMotionLibrary.Motion>();
            foreach (var m in lib.motions)
                if (m != null && m.idle && m.clip != null) pool.Add(m);
            Debug.Log($"[SusuMotions] {pool.Count} idle motions available.");
        }
        if (pool.Count == 0) return null;

        var fresh = pool.FindAll(m => !recent.Contains(m.name));
        if (fresh.Count == 0) { recent.Clear(); fresh = pool; }
        return fresh[Random.Range(0, fresh.Count)];
    }
}
