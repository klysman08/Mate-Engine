using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;
using UnityEngine;

/// <summary>
/// Puts every VRM in the SusuModels folder (next to the exe; the project root in the Editor) into the Models menu,
/// so the models shipped with MateSusu are ready to pick on first launch. Title, author, version and thumbnail come
/// from the VRM's own metadata, read straight from the file without loading the model. Models already in the
/// library are left alone, and removing one from the menu is respected until its file changes.
/// </summary>
public class SusuModels : MonoBehaviour
{
    public const string FolderName = "SusuModels";
    const string SeenPref = "MateSusu.Models.Seen"; // "path|size;path|size": files already offered once

    public static string Folder => Path.GetFullPath(Path.Combine(Application.dataPath, "..", FolderName));

    IEnumerator Start()
    {
        yield return null; // let the scene's library menu finish its own start-up first
        try { Register(); }
        catch (Exception e) { Debug.LogWarning("[SusuModels] " + e.Message); }
    }

    void Register()
    {
        if (!Directory.Exists(Folder)) return;
        var files = Directory.GetFiles(Folder, "*.vrm", SearchOption.TopDirectoryOnly).OrderBy(f => f).ToArray();
        if (files.Length == 0) return;

        string seen = PlayerPrefs.GetString(SeenPref, "");
        var seenSet = seen.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries).ToList();
        int added = 0;
        foreach (var file in files)
        {
            string path = Path.GetFullPath(file);
            string stamp = path + "|" + new FileInfo(path).Length;
            if (seenSet.Contains(stamp)) continue; // offered before (maybe removed by the user since)

            var info = ReadVrmInfo(path);
            AvatarLibraryMenu.AddAvatarToLibrary(info.name, info.author, info.version, info.fileType, path, info.thumbnail, info.polygons);
            if (info.thumbnail != null) Destroy(info.thumbnail);
            seenSet.Add(stamp);
            added++;
        }
        PlayerPrefs.SetString(SeenPref, string.Join(";", seenSet));
        if (added == 0) return;

        foreach (var menu in Resources.FindObjectsOfTypeAll<AvatarLibraryMenu>())
            if (menu != null && menu.gameObject.scene.IsValid()) menu.ReloadAvatars();
        Debug.Log($"[SusuModels] {added} model(s) from {FolderName} added to the Models menu.");
    }

    struct VrmInfo
    {
        public string name, author, version, fileType;
        public Texture2D thumbnail;
        public int polygons;
    }

    /// <summary>Reads the glTF JSON chunk of a .vrm (VRM 0.x or 1.0) for its metadata and thumbnail image.</summary>
    static VrmInfo ReadVrmInfo(string path)
    {
        var info = new VrmInfo { name = Path.GetFileNameWithoutExtension(path), author = "Unknown", version = "Unknown", fileType = "VRM0.X" };
        byte[] glb = File.ReadAllBytes(path);
        if (glb.Length < 20 || Encoding.ASCII.GetString(glb, 0, 4) != "glTF") return info;

        int jsonLength = BitConverter.ToInt32(glb, 12);
        var gltf = JObject.Parse(Encoding.UTF8.GetString(glb, 20, jsonLength));
        int binStart = 20 + jsonLength + 8; // BIN chunk data follows its 8-byte header

        int? imageIndex = null;
        var vrm1 = gltf.SelectToken("extensions.VRMC_vrm.meta");
        var vrm0 = gltf.SelectToken("extensions.VRM.meta");
        if (vrm1 != null)
        {
            info.fileType = "VRM1.X";
            info.name = (string)vrm1["name"] ?? info.name;
            info.author = vrm1["authors"]?.FirstOrDefault()?.ToString() ?? info.author;
            info.version = (string)vrm1["version"] ?? info.version;
            imageIndex = (int?)vrm1["thumbnailImage"];
        }
        else if (vrm0 != null)
        {
            info.name = NotEmpty((string)vrm0["title"]) ?? info.name;
            info.author = NotEmpty((string)vrm0["author"]) ?? info.author;
            info.version = NotEmpty((string)vrm0["version"]) ?? info.version;
            int? texture = (int?)vrm0["texture"];
            if (texture.HasValue && texture.Value >= 0) imageIndex = (int?)gltf["textures"]?[texture.Value]?["source"];
        }

        info.thumbnail = LoadImage(gltf, glb, binStart, imageIndex);
        info.polygons = CountTriangles(gltf);
        return info;
    }

    static string NotEmpty(string s) => string.IsNullOrWhiteSpace(s) ? null : s;

    static Texture2D LoadImage(JObject gltf, byte[] glb, int binStart, int? imageIndex)
    {
        if (!imageIndex.HasValue || imageIndex.Value < 0) return null;
        var image = gltf["images"]?[imageIndex.Value];
        int? view = (int?)image?["bufferView"];
        if (!view.HasValue) return null;
        var bv = gltf["bufferViews"]?[view.Value];
        if (bv == null) return null;
        int offset = binStart + ((int?)bv["byteOffset"] ?? 0);
        int length = (int?)bv["byteLength"] ?? 0;
        if (length <= 0 || offset + length > glb.Length) return null;

        var bytes = new byte[length];
        Buffer.BlockCopy(glb, offset, bytes, 0, length);
        var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
        if (tex.LoadImage(bytes)) return tex;
        Destroy(tex);
        return null;
    }

    /// <summary>Triangle count from the mesh primitives' index accessors (what the Models menu shows as polygons).</summary>
    static int CountTriangles(JObject gltf)
    {
        int total = 0;
        var accessors = gltf["accessors"] as JArray;
        if (accessors == null) return 0;
        foreach (var mesh in gltf["meshes"] ?? new JArray())
        foreach (var prim in mesh["primitives"] ?? new JArray())
        {
            int? idx = (int?)prim["indices"];
            if (idx.HasValue && idx.Value < accessors.Count) total += ((int?)accessors[idx.Value]["count"] ?? 0) / 3;
        }
        return total;
    }
}
