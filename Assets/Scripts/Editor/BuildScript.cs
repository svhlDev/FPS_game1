using System.IO;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

// Windows build of the slice.
//   Editor:     Tools > Build Windows
//   Batch mode: Unity.exe -batchmode -quit -projectPath <repo> -executeMethod BuildScript.BuildWindows -logFile Builds/build.log
// Exits with code 1 in batch mode if anything fails.
public static class BuildScript
{
    static readonly string[] Scenes =
    {
        "Assets/Scenes/TwinTowers.unity",
        "Assets/Scenes/FlightGraybox.unity",
    };
    const string OutputDir = "Builds/Windows";
    const string ExeName = "FPS_game1.exe";
    const string ProductName = "FPS_game1 Slice";
    const string CompanyName = "svhlDev";

    // Regenerates every builder scene (City graybox, Twin Towers, Sky Avenue) from its seed.
    //   Batch mode: Unity.exe -batchmode -quit -projectPath <repo> -executeMethod BuildScript.RebuildScenes -logFile Builds/rebuild.log
    [MenuItem("Tools/Rebuild All Scenes")]
    public static void RebuildScenes()
    {
        bool ok = true;
        try
        {
            FlightGrayboxBuilder.Build();
            TwinTowersBuilder.Build();
            SkyAvenueBuilder.Build();
        }
        catch (System.Exception e)
        {
            Debug.LogException(e);
            ok = false;
        }
        AssetDatabase.SaveAssets();
        if (Application.isBatchMode) EditorApplication.Exit(ok ? 0 : 1);
    }

    [MenuItem("Tools/Build Windows")]
    public static void BuildWindows()
    {
        bool ok = Build();
        if (Application.isBatchMode) EditorApplication.Exit(ok ? 0 : 1);
        else if (!ok) EditorUtility.DisplayDialog("Build Windows", "Build failed. See the Console.", "OK");
    }

    static bool Build()
    {
        // 0 = Input Manager (old), 1 = Input System Package, 2 = Both
        int input = ActiveInputHandler();
        if (input != 1 && input != 2)
        {
            Debug.LogError($"BUILD ABORTED: Active Input Handling is {input} (needs Input System Package or Both). " +
                           "Set it in Project Settings > Player > Other Settings.");
            return false;
        }

        foreach (var scene in Scenes)
        {
            if (File.Exists(scene)) continue;
            Debug.LogError($"BUILD ABORTED: scene missing: {scene}");
            return false;
        }

        PlayerSettings.productName = ProductName;
        PlayerSettings.companyName = CompanyName;

        if (Directory.Exists(OutputDir)) Directory.Delete(OutputDir, true);
        Directory.CreateDirectory(OutputDir);

        var options = new BuildPlayerOptions
        {
            scenes = Scenes,
            locationPathName = Path.Combine(OutputDir, ExeName),
            target = BuildTarget.StandaloneWindows64,
            targetGroup = BuildTargetGroup.Standalone,
            options = BuildOptions.None,
        };
        BuildReport report = BuildPipeline.BuildPlayer(options);
        var summary = report.summary;

        if (summary.result != BuildResult.Succeeded)
        {
            Debug.LogError($"BUILD FAILED: {summary.result}, {summary.totalErrors} error(s).");
            return false;
        }
        Debug.Log($"BUILD SUCCEEDED: {summary.outputPath} ({summary.totalSize / (1024f * 1024f):0.0} MB, {summary.totalTime.TotalSeconds:0} s)");
        return true;
    }

    // Player Settings has no public getter for this, so read the serialized value.
    static int ActiveInputHandler()
    {
        var settings = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/ProjectSettings.asset");
        if (settings == null || settings.Length == 0) return -1;
        var prop = new SerializedObject(settings[0]).FindProperty("activeInputHandler");
        return prop != null ? prop.intValue : -1;
    }
}
