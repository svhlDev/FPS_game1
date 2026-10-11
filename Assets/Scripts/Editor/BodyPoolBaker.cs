using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

// Tools > Characters > Bake Body Pool: generates every pool slot exactly as BodyPool would (same sheets,
// same outfits) and writes them to Assets/Resources/BodyPoolPreset.bytes, so the game starts with a
// full crowd. Re-bake when the body or clothing rules change (the game warns when the bake is stale).
// Batch mode: -executeMethod BodyPoolBaker.BakeBatch [-bakecivilians N -bakeofficers N]
public static class BodyPoolBaker
{
    const string Path = "Assets/Resources/BodyPoolPreset.bytes";

    // Default: about half the pool (enough for a full near-tier crowd at startup; the pool generates the
    // rest in the background). ~0.12 MB a body.
    public const int DefaultCivilians = 64, DefaultOfficers = 16;

    [MenuItem("Tools/Characters/Bake Body Pool")]
    static void BakeMenu() => Bake(DefaultCivilians, DefaultOfficers, true);

    public static void BakeBatch()
    {
        int civ = DefaultCivilians, off = DefaultOfficers;
        var args = System.Environment.GetCommandLineArgs();
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == "-bakecivilians") int.TryParse(args[i + 1], out civ);
            if (args[i] == "-bakeofficers") int.TryParse(args[i + 1], out off);
        }
        Bake(civ, off, false);
    }

    static void Bake(int civilians, int officers, bool interactive)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var bodies = new List<(int, bool, BodyAsset)>();
        int total = civilians + officers, done = 0;
        try
        {
            foreach (bool officer in new[] { false, true })
            {
                int n = officer ? officers : civilians;
                for (int i = 0; i < n; i++)
                {
                    if (interactive && EditorUtility.DisplayCancelableProgressBar("Bake Body Pool", $"{(officer ? "officer" : "civilian")} {i + 1} / {n}", done / (float)total))
                    { Debug.Log("Body pool bake cancelled"); return; }
                    var sheet = BodyPool.SheetFor(i, officer, BodyPool.DefaultOfficerStrBias, BodyPool.DefaultOfficerDexBias);
                    var asset = new BodyBuild(sheet, null, BodyPool.OutfitFor(sheet, officer)).RunSync();
                    bodies.Add((i, officer, asset));
                    done++;
                }
            }
            var bytes = BodyPreset.Encode(bodies);
            Directory.CreateDirectory("Assets/Resources");
            File.WriteAllBytes(Path, bytes);
            AssetDatabase.ImportAsset(Path);
            Debug.Log($"BODY POOL BAKED: {bodies.Count} bodies ({civilians} civilians, {officers} officers), {bytes.Length / 1024f / 1024f:0.0} MB, {sw.Elapsed.TotalSeconds:0} s -> {Path}");
        }
        finally
        {
            if (interactive) EditorUtility.ClearProgressBar();
            foreach (var (_, _, a) in bodies) { Object.DestroyImmediate(a.body); Object.DestroyImmediate(a.head); }
        }
    }
}
