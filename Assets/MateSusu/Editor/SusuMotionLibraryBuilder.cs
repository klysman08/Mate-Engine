using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Builds Assets/MateSusu/Resources/SusuMotionLibrary.asset: the named gestures Susu can play,
/// each a time window inside a humanoid clip (Rokoko mocap takes, Quaternius UAL2, Mate Engine pet clips).
/// Also makes sure every FBX under Assets/MateSusu/Animations imports as an in-place Humanoid.
/// </summary>
public static class SusuMotionLibraryBuilder
{
    const string AnimRoot = "Assets/MateSusu/Animations";
    const string LibraryPath = "Assets/MateSusu/Resources/SusuMotionLibrary.asset";
    const string R = AnimRoot + "/Rokoko/";
    const string Q = AnimRoot + "/Quaternius/UAL2_Standard.fbx";
    const string ME = "Assets/MATE ENGINE - Animations/PET_MISC/";
    const string EB = AnimRoot + "/EmoteBox/";   // Necocoya EmoteBox v3.31 humanoid .anim emotes
    const string VS = AnimRoot + "/VRSuya/";     // VRSuya dances: personal use only, kept out of git
    const string PC = AnimRoot + "/Poses/Chiu/";       // single-frame poses, kept out of git
    const string KA = AnimRoot + "/Kawaii/KA_";  // KAWAII ANIMATIONS 100 (Asset Store): in builds, not in git
    const float Hold = 3.2f;                     // seconds a single-frame pose is held

    /// <summary>Folders whose licences allow personal use only: left out of public builds.</summary>
    static readonly string[] LocalOnly = { VS, PC };

    // name, asset path, clip name suffix ("" = the asset's only clip), start s, end s (0 = clip end)
    static readonly (string name, string path, string clip, float start, float end)[] Table =
    {
        // happy / cheering
        ("cheer",      R + "Emote_Celebrate_HUMANIK_769.fbx",         "", 4.6f, 9.4f),   // fist up high
        ("celebrate",  R + "Celebration_HUMANIK_TF1.fbx",             "", 6.3f, 7.8f),   // both arms up
        ("jump",       R + "Emote_JumpforJoy_HUMANIK_769.fbx",        "", 4.5f, 8.2f),   // jump for joy
        ("clap",       R + "Clap_SlowClap_mixamo.fbx",                "", 4.0f, 9.0f),
        ("happydance", R + "HappyDance_mixamo.fbx",                   "", 3.0f, 9.0f),
        ("shimmy",     R + "ShoulderShimmy_mixamo.fbx",               "", 2.0f, 7.0f),
        ("sway",       R + "GentleSwayingDancing_mixamo.fbx",         "", 1.0f, 9.0f),
        ("laugh",      R + "Laughs_mixamo.fbx",                       "", 15.0f, 20.0f),
        ("happy",      ME + "PET_HAPPY.anim",                         "", 0f, 0f),
        ("giggle",     ME + "PET_LAUGHING.anim",                      "", 0f, 0f),
        // cute / shy
        ("flirty",     R + "Flirty_mixamo.fbx",                       "", 8.5f, 13.5f),  // hand to cheek, tilting
        ("cheekhand",  R + "SitcomCameraLooks_mixamo.fbx",            "", 52.0f, 55.5f),
        ("shypoint",   ME + "PET_SHY_POINT.anim",                     "", 0f, 0f),
        ("behindback", R + "Idle_CharacterLoading02_HUMANIK_769.fbx", "", 16.5f, 24.5f), // hands behind the back
        ("tilt",       R + "HeadTilts_mixamo.fbx",                    "", 3.5f, 6.5f),
        // talking / thinking
        ("explain",    R + "Idle_Conversation_HUMANIK_769.fbx",       "", 12.0f, 17.0f),
        ("present",    R + "Host_mixamo.fbx",                         "", 7.0f, 13.0f),  // arm out, "look!"
        ("point",      R + "Idle_Pointing_HUMANIK_769.fbx",           "", 6.5f, 12.0f),
        ("idea",       R + "Idle_LookingAround_HUMANIK_769.fbx",      "", 23.5f, 27.5f), // finger up
        ("taphead",    R + "Meme_TapHead_HUMANIK_769.fbx",            "", 7.3f, 11.5f),
        ("shrug",      R + "Shrugging_mixamo.fbx",                    "", 5.3f, 8.0f),
        ("nod",        Q, "|Yes", 0f, 0f),
        ("headshake",  Q, "|Idle_No_Loop", 0f, 0f),
        ("nonono",     R + "Sports_NoNoNoYesYesYes_HUMANIK_WHS.fbx",  "", 6.0f, 12.0f),
        ("lookaround", R + "Idle_02_HUMANIK_TF1.fbx",                 "", 3.0f, 11.0f),
        ("foldarms",   Q, "|Idle_FoldArms_Loop", 0f, 4.0f),
        ("proud",      R + "Idle_CharacterLoading01_HUMANIK_769.fbx", "", 46.6f, 52.5f), // hands on hips
        // daily life
        ("stretch",    R + "RangeofMotionTest_HUMANIK_TF1.fbx",       "", 9.0f, 12.2f),  // arms overhead
        ("drink",      R + "DrinkingCoffee_PullingGun_HUMANIK_769.fbx", "", 7.5f, 13.0f),
        ("eat",        R + "Judges_mixamo.fbx",                       "", 14.0f, 19.5f), // tasting
        ("nibble",     Q, "|Consume", 0f, 0f),
        ("phone",      R + "ScrollingPhone_HUMANIK_769.fbx",          "", 3.5f, 8.5f),
        ("watch",      R + "Idle_CharacterLoading02_HUMANIK_769.fbx", "", 25.5f, 31.8f), // checks the time
        ("facepalm",   R + "FacePalms_mixamo.fbx",                    "", 3.5f, 8.5f),
        ("headpanic",  R + "SportsFan_Celebrating_HUMANIK_769.fbx",   "", 2.5f, 7.5f),   // hands on head
        ("impatient",  R + "FootTapping_mixamo.fbx",                  "", 4.5f, 10.5f),
        // EmoteBox emotes (looping ones are repeated by asking for a longer window than the clip)
        ("wave",       EB + "01_Emote_tefuri1.anim",                  "", 0f, 4.5f),
        ("wave2",      EB + "02_Emote_tefuri3.anim",                  "", 0f, 4.5f),
        ("peace",      EB + "03_Emote_peace1.anim",                   "", 0f, 5.0f),
        ("hawawa",     EB + "04_Emote_hawawa.anim",                   "", 0f, 5.0f),   // flustered
        ("bunny",      EB + "05_Emote_umauma_OneLoop.anim",           "", 0f, 3.3f),   // hands up like ears, loop
        ("yoshi",      EB + "06_Emote_yoshi.anim",                    "", 0f, 3.5f),   // "yoshi!" pose
        ("kakapo",     EB + "07_Emote_Kakapo-Normal_OneLoop.anim",    "", 0f, 3.9f),   // crouch dance, loop
        ("cossack",    EB + "08_Emote_CossackDance.anim",             "", 0f, 4.0f),   // squat-kick dance, loop
        // VRSuya (only present on machines that own it)
        ("doodle",     VS + "VRSuya_Doodle_Dance.anim",               "", 0f, 4.4f),   // short loop
        // KAWAII ANIMATIONS 100: gestures, reactions and talking loops (only present on machines that own the pack)
        ("breathe",           KA + "Idle01_breathing.fbx",             "", 0f, 0f),
        ("lookaround2",       KA + "Idle02_LookLeftAndRight.fbx",      "", 0f, 0f),
        ("lookhands",         KA + "Idle03_LookAtHands.fbx",           "", 0f, 0f),
        ("lookfeet",          KA + "Idle04_LookAtFeet.fbx",            "", 0f, 0f),
        ("kstretch",          KA + "Idle05_Stretch.fbx",               "", 0f, 0f),
        ("kidea",             KA + "Idle08_ComeUpWithAnIdea.fbx",      "", 0f, 0f),
        ("waiting",           KA + "Idle09_Waiting.fbx",               "", 0f, 0f),
        ("lookback",          KA + "Idle11_LookingBack.fbx",           "", 0f, 0f),
        ("leanforward",       KA + "Idle12_LeaningForward.fbx",        "", 0f, 0f),
        ("kwave",             KA + "Idle16_WaveHands.fbx",             "", 0f, 0f),
        ("shy",               KA + "Idle18_Shy.fbx",                   "", 0f, 0f),
        ("shyrefuse",         KA + "Idle19_ShyRefusal.fbx",            "", 0f, 7f),
        ("triplepose",        KA + "Idle20_TriplePose.fbx",            "", 0f, 0f),
        ("cheers",            KA + "Idle25_Cheers.fbx",                "", 0f, 6f),
        ("shout",             KA + "Idle26_Shout.fbx",                 "", 0f, 0f),
        ("angry",             KA + "Idle27_Angry.fbx",                 "", 0f, 0f),
        ("klaugh",            KA + "Idle28_Laugh.fbx",                 "", 0f, 0f),
        ("surprised",         KA + "Idle29_Surprised.fbx",             "", 0f, 0f),
        ("stretch2",          KA + "Idle30_Stretch2.fbx",              "", 0f, 8f),
        ("fingersnap",        KA + "Idle35_FingerSnap.fbx",            "", 0f, 0f),
        ("yay",               KA + "Idle36_Yay.fbx",                   "", 0f, 0f),
        ("tsundere",          KA + "Idle37_Tsundere.fbx",              "", 0f, 0f),
        ("cry",               KA + "Idle38_Cry.fbx",                   "", 0f, 0f),
        ("cutearm",           KA + "Idle39_CuteArmUp.fbx",             "", 0f, 0f),
        ("crosslegs",         KA + "Idle40_CrossLegs.fbx",             "", 0f, 0f),
        ("shypose",           KA + "Idle41_CuteShyPose.fbx",           "", 0f, 0f),
        ("taunt",             KA + "Idle42_Taunt.fbx",                 "", 0f, 0f),
        ("handhip",           KA + "Idle43_HandOnHip.fbx",             "", 0f, 0f),
        ("bow",               KA + "Idle44_GreetingBow.fbx",           "", 0f, 0f),
        ("waveslight",        KA + "Idle45_WaveHandSlightly.fbx",      "", 0f, 0f),
        ("curtsy",            KA + "Idle52_Curtsy.fbx",                "", 0f, 0f),
        ("kiss",              KA + "Idle59_Kiss1_1.fbx",               "", 0f, 0f),
        ("thumbsup",          KA + "Idle65_ThumbsUp.fbx",              "", 0f, 0f),
        ("jumpjoy",           KA + "Idle66_JumpForJoy.fbx",            "", 0f, 0f),
        ("feeldown",          KA + "Idle67_FeelDown.fbx",              "", 0f, 0f),
        ("surprise2",         KA + "Idle68_Surprise.fbx",              "", 0f, 0f),
        ("surprise3",         KA + "Idle69_Surprise2.fbx",             "", 0f, 0f),
        ("catpose",           KA + "Idle71_CatPose.fbx",               "", 0f, 0f),
        ("leanin",            KA + "Idle72_LeanForward.fbx",           "", 0f, 0f),
        ("idolpose",          KA + "Idle73_IdolPose.fbx",              "", 0f, 0f),
        ("kcheer",            KA + "Idle74_Cheer.fbx",                 "", 0f, 0f),
        ("kpoint",            KA + "Idle75_Pointing.fbx",              "", 0f, 0f),
        ("motivated",         KA + "Idle76_MotivatedPose.fbx",         "", 0f, 0f),
        ("kpresent",          KA + "Idle78_Present1_1.fbx",            "", 0f, 6f),
        ("talk",              KA + "Speak01_Normal_Loop.fbx",          "", 0f, 3.5f),
        ("talk_explain",      KA + "Speak02_Explaining_Loop.fbx",      "", 0f, 3.5f),
        ("talk_excited",      KA + "Speak03_Excited_Loop.fbx",         "", 0f, 3.5f),
        ("talk_calm",         KA + "Speak04_Calm_Loop.fbx",            "", 0f, 3.5f),
        ("talk_scold",        KA + "Speak05_PointingFingerScolding_Loop.fbx", "", 0f, 3.5f),
        ("talk_point",        KA + "Speak06_PointingFingerCalmly_Loop.fbx", "", 0f, 3.5f),
        ("talk_chatty",       KA + "Speak07_Chatty_Loop.fbx",          "", 0f, 3.5f),
        ("talk_pout",         KA + "Speak08_ArmsCrossedPouting_Loop.fbx", "", 0f, 3.5f),
        ("talk_armscrossed",  KA + "Speak09_ArmsCrossedCalm_Loop.fbx", "", 0f, 3.5f),
        ("talk_shy",          KA + "Speak10_Shy_Loop.fbx",             "", 0f, 3.5f),
        // Single-frame poses, held for a moment (only on machines that have the pose packs)
        ("pose_stretch",  PC + "chiu_260307-2.anim",     "", 0f, Hold),   // one arm up, hand behind the head
        ("pose_whisper",  PC + "chiu_260307-3.anim",     "", 0f, Hold),   // both hands to the mouth
        ("pose_vsign",    PC + "chiu_260307-4.anim",     "", 0f, Hold),   // V sign by the eye, hand on hip
        ("pose_think",    PC + "chiu_260309.anim",       "", 0f, Hold),   // hand on chin
        ("pose_hideface", PC + "chiu_260314.anim",       "", 0f, Hold),   // arm over the face
        ("pose_offer",    PC + "chiu_260317-Glass.anim", "", 0f, Hold),   // palm up
        ("pose_shh",      PC + "chiu_260327.anim",       "", 0f, Hold),   // finger to the lips, hand on hip
        ("pose_tada",     PC + "chiu_260329.anim",       "", 0f, Hold),   // wide stance, pointing
    };

    /// <summary>Calm motions SusuIdleMotions may play by itself between voice lines (nothing that needs a reason,
    /// like eating or a face-palm).</summary>
    static readonly HashSet<string> Idle = new HashSet<string>
    {
        "breathe", "lookaround2", "lookhands", "lookfeet", "kstretch", "kidea", "waiting", "lookback", "leanforward",
        "waveslight", "crosslegs", "handhip", "cutearm", "catpose", "idolpose", "triplepose", "stretch2", "shypose",
        "fingersnap", "leanin",
        "lookaround", "tilt", "behindback", "cheekhand", "peace", "yoshi", "bunny",
        "pose_think", "pose_vsign", "pose_shh", "pose_stretch",
    };

    [MenuItem("Tools/MateSusu/Build Motion Library")]
    static void BuildMenu() => Debug.Log("[SusuMotions] " + Build());

    /// <param name="publicOnly">true = skip the personal-use packs (for builds that will be shared).</param>
    public static string Build(bool publicOnly = false)
    {
        var report = new StringBuilder();
        report.Append(FixImportSettings());

        var motions = new List<SusuMotionLibrary.Motion>();
        int skipped = 0;
        foreach (var (name, path, clipName, start, end) in Table)
        {
            if (publicOnly && LocalOnly.Any(path.StartsWith)) { skipped++; continue; }
            var clip = AssetDatabase.LoadAllAssetsAtPath(path).OfType<AnimationClip>()
                .FirstOrDefault(c => !c.name.StartsWith("__preview__") && (clipName == "" || c.name.EndsWith(clipName)));
            if (clip == null) { report.Append("missing clip for '").Append(name).Append("': ").Append(path).Append('\n'); continue; }
            motions.Add(new SusuMotionLibrary.Motion { name = name, clip = clip, start = start, end = end, idle = Idle.Contains(name), noFootIK = !HasFootGoals(clip) });
        }

        var lib = AssetDatabase.LoadAssetAtPath<SusuMotionLibrary>(LibraryPath);
        if (lib == null)
        {
            lib = ScriptableObject.CreateInstance<SusuMotionLibrary>();
            AssetDatabase.CreateAsset(lib, LibraryPath);
        }
        lib.motions = motions.ToArray();
        EditorUtility.SetDirty(lib);
        AssetDatabase.SaveAssets();
        report.Append(motions.Count).Append(" motions in ").Append(LibraryPath);
        if (skipped > 0) report.Append(" (").Append(skipped).Append(" personal-use motions left out)");
        return report.ToString();
    }

    /// <summary>Humanoid clips normally carry foot goal curves (LeftFootT/Q); some hand-made pose clips do not.</summary>
    static bool HasFootGoals(AnimationClip clip) =>
        AnimationUtility.GetCurveBindings(clip).Any(b => b.propertyName.StartsWith("LeftFootT"));

    /// <summary>Humanoid, no materials, and in place: horizontal travel and turning go to root motion,
    /// which Mate Engine ignores, so Susu never wanders off her window.</summary>
    static string FixImportSettings()
    {
        var sb = new StringBuilder();
        foreach (var guid in AssetDatabase.FindAssets("t:Model", new[] { AnimRoot }))
        {
            var path = AssetDatabase.GUIDToAssetPath(guid);
            var mi = AssetImporter.GetAtPath(path) as ModelImporter;
            if (mi == null) continue;
            var clips = mi.clipAnimations.Length > 0 ? mi.clipAnimations : mi.defaultClipAnimations;
            bool ok = mi.animationType == ModelImporterAnimationType.Human
                      && mi.materialImportMode == ModelImporterMaterialImportMode.None
                      && clips.All(c => !c.lockRootPositionXZ && !c.lockRootRotation && c.lockRootHeightY && c.heightFromFeet);
            if (ok) continue;

            mi.animationType = ModelImporterAnimationType.Human;
            mi.avatarSetup = ModelImporterAvatarSetup.CreateFromThisModel;
            mi.materialImportMode = ModelImporterMaterialImportMode.None;
            mi.importBlendShapes = false;
            mi.importCameras = false;
            mi.importLights = false;
            if (path.Contains("/Quaternius/")) mi.bakeAxisConversion = true;
            foreach (var c in clips)
            {
                c.lockRootRotation = false; c.keepOriginalOrientation = false;
                c.lockRootPositionXZ = false; c.keepOriginalPositionXZ = false;
                c.lockRootHeightY = true; c.keepOriginalPositionY = false; c.heightFromFeet = true;
                c.loopTime = c.name.EndsWith("_Loop");
            }
            mi.clipAnimations = clips;
            mi.SaveAndReimport();
            sb.Append("reimported ").Append(path).Append('\n');
        }
        return sb.ToString();
    }
}
