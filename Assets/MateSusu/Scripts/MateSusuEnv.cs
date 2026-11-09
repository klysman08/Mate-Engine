using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

/// <summary>
/// Reads KEY = "value" pairs from a local .env file (never committed).
/// Search order: project root (Editor), next to the executable (build), persistentDataPath.
/// </summary>
public static class MateSusuEnv
{
    static Dictionary<string, string> values;
    static string loadedFrom;

    public static string LoadedFrom { get { EnsureLoaded(); return loadedFrom; } }

    public static string Get(string key, string fallback = null)
    {
        EnsureLoaded();
        if (values.TryGetValue(key, out var v) && !string.IsNullOrEmpty(v)) return v;
        var env = Environment.GetEnvironmentVariable(key);
        return string.IsNullOrEmpty(env) ? fallback : env;
    }

    public static string GetFirst(params string[] keys)
    {
        foreach (var k in keys)
        {
            var v = Get(k);
            if (!string.IsNullOrEmpty(v)) return v;
        }
        return null;
    }

    public static bool GetBool(string key, bool fallback)
    {
        var v = Get(key);
        if (string.IsNullOrEmpty(v)) return fallback;
        v = v.Trim().ToLowerInvariant();
        return v == "1" || v == "true" || v == "yes" || v == "on";
    }

    public static float GetFloat(string key, float fallback)
    {
        var v = Get(key);
        return float.TryParse(v, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var f) ? f : fallback;
    }

    public static void Reload()
    {
        values = null;
        EnsureLoaded();
    }

    static void EnsureLoaded()
    {
        if (values != null) return;
        values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        loadedFrom = null;

        foreach (var path in CandidatePaths())
        {
            try
            {
                if (!File.Exists(path)) continue;
                Parse(File.ReadAllLines(path));
                loadedFrom = path;
                return;
            }
            catch (Exception e)
            {
                Debug.LogWarning("[MateSusuEnv] Could not read " + path + ": " + e.Message);
            }
        }
    }

    static IEnumerable<string> CandidatePaths()
    {
        // Application.dataPath = <project>/Assets in the Editor, <game>/<name>_Data in a build.
        var dataParent = Directory.GetParent(Application.dataPath)?.FullName;
        if (!string.IsNullOrEmpty(dataParent)) yield return Path.Combine(dataParent, ".env");
        yield return Path.Combine(Application.persistentDataPath, ".env");
    }

    static void Parse(string[] lines)
    {
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("#")) continue;
            if (line.StartsWith("export ")) line = line.Substring(7).Trim();

            int eq = line.IndexOf('=');
            if (eq <= 0) continue;

            var key = line.Substring(0, eq).Trim();
            var val = line.Substring(eq + 1).Trim();

            if (val.Length >= 2 && (val[0] == '"' || val[0] == '\'') && val[val.Length - 1] == val[0])
                val = val.Substring(1, val.Length - 2);
            else
            {
                int hash = val.IndexOf(" #", StringComparison.Ordinal);
                if (hash >= 0) val = val.Substring(0, hash).Trim();
            }

            values[key] = val;
        }
    }
}
