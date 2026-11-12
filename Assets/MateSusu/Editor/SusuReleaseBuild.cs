using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

/// <summary>
/// Builds the Windows release that gets uploaded to GitHub: Builds/MateSusu-v&lt;Version&gt;/MateSusu.exe.
/// The release is meant to be shared, so it
///  - uses a motion library without the personal-use animation packs (restored for the Editor afterwards),
///  - leaves out the local LLM runtime (the chat runs on OpenRouter) and Unity's debug symbols,
///  - never contains a .env file (users type their API keys in the AI window, or add their own .env),
///  - carries the licence texts, the animation credits and a .env example.
/// Zip the folder afterwards to upload it.
/// </summary>
public static class SusuReleaseBuild
{
    public const string Version = "1.1.0";
    const string ExeName = "MateSusu.exe";

    [MenuItem("Tools/MateSusu/Build Windows Release")]
    static void Menu() => Debug.Log("[SusuRelease] " + Build());

    public static string OutputFolder => Path.Combine("Builds", "MateSusu-v" + Version);

    public static string Build()
    {
        string outDir = OutputFolder;
        if (Directory.Exists(outDir)) Directory.Delete(outDir, true);
        var log = new StringBuilder();

        try
        {
            log.AppendLine(SusuMotionLibraryBuilder.Build(publicOnly: true));

            var options = new BuildPlayerOptions
            {
                scenes = EditorBuildSettings.scenes.Where(s => s.enabled).Select(s => s.path).ToArray(),
                locationPathName = Path.Combine(outDir, ExeName),
                target = BuildTarget.StandaloneWindows64,
                targetGroup = BuildTargetGroup.Standalone,
                options = BuildOptions.None,
            };
            BuildReport report = BuildPipeline.BuildPlayer(options);
            var summary = report.summary;
            if (summary.result != BuildResult.Succeeded)
                return $"Build {summary.result}: {summary.totalErrors} errors. " + log;

            log.AppendLine($"Built in {summary.totalTime.TotalMinutes:0.0} min.");
            log.Append(Tidy(outDir));
            return log.ToString();
        }
        finally
        {
            // Back to the full library for the Editor.
            SusuMotionLibraryBuilder.Build();
        }
    }

    static string Tidy(string outDir)
    {
        var log = new StringBuilder();
        string data = Path.Combine(outDir, Path.GetFileNameWithoutExtension(ExeName) + "_Data");
        string streaming = Path.Combine(data, "StreamingAssets");

        // Not shipped: debug symbols, the local LLM runtime and its files.
        foreach (var dir in Directory.GetDirectories(outDir, "*_DoNotShip"))
            DeleteDir(dir, log);
        if (Directory.Exists(streaming))
        {
            foreach (var dir in Directory.GetDirectories(streaming, "undreamai-*"))
                DeleteDir(dir, log);
            foreach (var name in new[] { "LLMManager.json", "LLAMA 3.2 COMMUNITY LICENSE.txt" })
                DeleteFile(Path.Combine(streaming, name), log);
        }

        // Never ship anyone's keys.
        foreach (var env in Directory.GetFiles(outDir, ".env*", SearchOption.AllDirectories))
            DeleteFile(env, log);

        // Licence texts (the MateEngine licence requires them in every distribution), credits and config example.
        Copy("LICENSE.md", Path.Combine(outDir, "LICENSE.md"), log);
        Copy("NOTICE.txt", Path.Combine(outDir, "NOTICE.txt"), log);
        Copy("Assets/MateSusu/Animations/CREDITS.md", Path.Combine(outDir, "CREDITS-animations.md"), log);
        Copy(".env.example", Path.Combine(outDir, "env.example.txt"), log);
        File.WriteAllText(Path.Combine(outDir, "README.txt"),
            "MateSusu " + Version + " - a Mate Engine fork with Susu's voice, chat and motions.\r\n\r\n" +
            "Start MateSusu.exe. Right-click the avatar for the radial menu.\r\n" +
            "Chat and voice need your own API keys: open the AI slice of the radial menu and paste an OpenRouter key\r\n" +
            "(chat) and/or an ElevenLabs key (voice and microphone). Keys are stored encrypted for your Windows user.\r\n" +
            "Optional: rename env.example.txt to .env next to MateSusu.exe to configure everything in a file.\r\n\r\n" +
            "Source code and licence: https://github.com/klysman08/Mate-Engine\r\n" +
            "Based on Mate Engine by shinyflvre: https://github.com/shinyflvre/Mate-Engine\r\n");
        return log.ToString();
    }

    static void DeleteDir(string path, StringBuilder log)
    {
        if (!Directory.Exists(path)) return;
        Directory.Delete(path, true);
        log.AppendLine("removed " + path);
    }

    static void DeleteFile(string path, StringBuilder log)
    {
        if (!File.Exists(path)) return;
        File.Delete(path);
        log.AppendLine("removed " + path);
    }

    static void Copy(string from, string to, StringBuilder log)
    {
        if (!File.Exists(from)) { log.AppendLine("missing " + from); return; }
        File.Copy(from, to, true);
    }
}
