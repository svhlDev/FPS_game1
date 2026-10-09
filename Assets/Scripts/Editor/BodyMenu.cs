using UnityEditor;
using UnityEngine;

// Tools > Characters: the body rules asset and the debug lineup.
public static class BodyMenu
{
    [MenuItem("Tools/Characters/Create Body Rules Asset")]
    static void CreateRules()
    {
        const string path = "Assets/Resources/BodyRules.asset";
        var existing = AssetDatabase.LoadAssetAtPath<BodyRules>(path);
        if (existing != null) { Selection.activeObject = existing; return; }
        if (!AssetDatabase.IsValidFolder("Assets/Resources")) AssetDatabase.CreateFolder("Assets", "Resources");
        var rules = ScriptableObject.CreateInstance<BodyRules>();
        AssetDatabase.CreateAsset(rules, path);
        AssetDatabase.SaveAssets();
        Selection.activeObject = rules;
    }

    [MenuItem("Tools/Characters/Body Lineup")]
    static void Lineup()
    {
        var go = new GameObject("Body Lineup");
        var cam = SceneView.lastActiveSceneView;
        if (cam != null) go.transform.position = cam.pivot;
        go.AddComponent<BodyLineupDebug>();
        Undo.RegisterCreatedObjectUndo(go, "Body Lineup");
        Selection.activeGameObject = go;
        if (cam != null) cam.Frame(new Bounds(go.transform.position + new Vector3(-4.5f, -4.4f, 0f), new Vector3(11f, 11f, 1f)), false);
    }
}
