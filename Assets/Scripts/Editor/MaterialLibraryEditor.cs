using System.IO;
using UnityEditor;
using UnityEngine;

// Saves MaterialLibrary materials as assets while building scenes in the editor (scenes can only
// reference assets): Assets/Graybox/Surfaces/<type>_<RRGGBB>_<variant>.mat, refreshed from the library's
// current defaults on every build.
[InitializeOnLoad]
public static class MaterialLibraryEditor
{
    const string Folder = "Assets/Graybox/Surfaces";

    static MaterialLibraryEditor() => MaterialLibrary.EditorHook = Persist;

    static Material Persist(string key, Material fresh)
    {
        Directory.CreateDirectory(Folder);
        string path = $"{Folder}/{key}.mat";
        var asset = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (asset == null)
        {
            AssetDatabase.CreateAsset(fresh, path);
            return fresh;
        }
        asset.shader = fresh.shader;
        asset.CopyPropertiesFromMaterial(fresh);
        asset.enableInstancing = true;
        EditorUtility.SetDirty(asset);
        Object.DestroyImmediate(fresh);
        return asset;
    }
}
