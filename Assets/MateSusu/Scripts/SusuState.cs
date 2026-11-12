using CustomDancePlayer;
using UnityEngine;

/// <summary>Shared checks about what the avatar is doing right now.</summary>
public static class SusuState
{
    static readonly int CustomDancing = Animator.StringToHash("isCustomDancing");
    static readonly int WaitingForDance = Animator.StringToHash("isWaitingForDancing");

    static int checkedFrame = -1;
    static bool inCustomDance;
    static AvatarDanceHandler[] handlers;
    static float nextHandlerSearch;

    /// <summary>True while a custom dance (Dance Player menu) is playing or about to start. Susu then neither
    /// speaks nor plays her own motions, so the dance is never interrupted.</summary>
    public static bool InCustomDance
    {
        get
        {
            if (checkedFrame == Time.frameCount) return inCustomDance;
            checkedFrame = Time.frameCount;
            inCustomDance = Check();
            return inCustomDance;
        }
    }

    static bool Check()
    {
        var animator = SusuClipPlayer.FindAvatarAnimator();
        if (animator != null)
            foreach (var p in animator.parameters)
                if (p.type == AnimatorControllerParameterType.Bool && (p.nameHash == CustomDancing || p.nameHash == WaitingForDance) && animator.GetBool(p.nameHash))
                    return true;

        if (handlers == null || Time.unscaledTime >= nextHandlerSearch)
        {
            handlers = Object.FindObjectsByType<AvatarDanceHandler>(FindObjectsSortMode.None);
            nextHandlerSearch = Time.unscaledTime + 2f;
        }
        foreach (var h in handlers)
            if (h != null && h.IsPlaying) return true;
        return false;
    }
}
