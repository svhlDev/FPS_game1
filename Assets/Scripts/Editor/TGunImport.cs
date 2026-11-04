using UnityEditor;
using UnityEngine;

// Import settings for the T-gun from Blender (Tools/Blender/tgun_passes) and its baked material.
// The material is an asset so URP keeps the shader variants it needs in a build.
public class TGunImport : AssetPostprocessor
{
    const string Folder = "Assets/Resources/Weapons/TGun/";

    void OnPreprocessModel()
    {
        if (!assetPath.StartsWith(Folder)) return;
        var m = (ModelImporter)assetImporter;
        m.globalScale = 1f;
        m.useFileScale = true;
        m.bakeAxisConversion = false;
        m.importCameras = false;
        m.importLights = false;
        m.importAnimation = false;
        m.animationType = ModelImporterAnimationType.None;
        m.materialImportMode = ModelImporterMaterialImportMode.None;
        m.importNormals = ModelImporterNormals.Import;
        m.importTangents = ModelImporterTangents.CalculateMikk;
        m.meshCompression = ModelImporterMeshCompression.Off;
        m.isReadable = false;
    }

    void OnPreprocessTexture()
    {
        if (!assetPath.StartsWith(Folder)) return;
        var t = (TextureImporter)assetImporter;
        if (assetPath.EndsWith("_Normal.png")) t.textureType = TextureImporterType.NormalMap;
        else t.sRGBTexture = assetPath.EndsWith("_BaseColor.png");
        t.maxTextureSize = 2048;
    }

    // Tools > Update T-gun material. Also runs before every Windows build.
    [MenuItem("Tools/Update T-gun material")]
    public static void EnsureMaterial()
    {
        AssetDatabase.ImportAsset(Folder + "TGun.fbx", ImportAssetOptions.ForceUpdate);
        string path = Folder + "TGun_Baked.mat";
        var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (mat == null)
        {
            mat = new Material(Shader.Find("Universal Render Pipeline/Lit"));
            AssetDatabase.CreateAsset(mat, path);
        }
        Texture Tex(string name) => AssetDatabase.LoadAssetAtPath<Texture2D>(Folder + name + ".png");
        mat.SetTexture("_BaseMap", Tex("TGun_BaseColor"));
        mat.SetColor("_BaseColor", Color.white);
        mat.SetTexture("_MetallicGlossMap", Tex("TGun_MetallicSmoothness"));
        mat.SetFloat("_Metallic", 1f);
        mat.SetFloat("_Smoothness", 1f);
        mat.SetFloat("_SmoothnessTextureChannel", 0f);
        mat.SetTexture("_BumpMap", Tex("TGun_Normal"));
        mat.SetFloat("_BumpScale", 1f);
        mat.SetTexture("_OcclusionMap", Tex("TGun_AO"));
        mat.SetFloat("_OcclusionStrength", 1f);
        mat.EnableKeyword("_METALLICSPECGLOSSMAP");
        mat.EnableKeyword("_NORMALMAP");
        mat.EnableKeyword("_OCCLUSIONMAP");
        mat.enableInstancing = true;
        EditorUtility.SetDirty(mat);
        AssetDatabase.SaveAssets();
        Debug.Log($"T-gun material: base {mat.GetTexture("_BaseMap") != null}, normal {mat.GetTexture("_BumpMap") != null}, ms {mat.GetTexture("_MetallicGlossMap") != null}, ao {mat.GetTexture("_OcclusionMap") != null}");
    }
}
