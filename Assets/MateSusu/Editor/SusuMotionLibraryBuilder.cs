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
    };

    [MenuItem("Tools/MateSusu/Build Motion Library")]
    static void BuildMenu() => Debug.Log("[SusuMotions] " + Build());

    public static string Build()
    {
        var report = new StringBuilder();
        report.Append(FixImportSettings());

        var motions = new List<SusuMotionLibrary.Motion>();
        foreach (var (name, path, clipName, start, end) in Table)
        {
            var clip = AssetDatabase.LoadAllAssetsAtPath(path).OfType<AnimationClip>()
                .FirstOrDefault(c => !c.name.StartsWith("__preview__") && (clipName == "" || c.name.EndsWith(clipName)));
            if (clip == null) { report.Append("missing clip for '").Append(name).Append("': ").Append(path).Append('\n'); continue; }
            motions.Add(new SusuMotionLibrary.Motion { name = name, clip = clip, start = start, end = end });
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
        return report.ToString();
    }

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
