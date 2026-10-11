using System.IO;
using UnityEditor;
using UnityEngine;

// Face Fix Part 0: the head SDF sanity test from the editor (Tools > Characters > Face Sanity Test) or in
// batch mode: -executeMethod FaceSanityBatch.Run [-sanityseeds N] [-sanityonly <label part>]; the report goes to the log and to
// Builds/results/facesanity.txt, the exit code is 1 on a failure.
public static class FaceSanityBatch
{
    [MenuItem("Tools/Characters/Face Sanity Test")]
    static void Menu() { FaceSanity.Run(out string r); Debug.Log(r); }

    public static void Run()
    {
        int seeds = 20;
        var args = System.Environment.GetCommandLineArgs();
        string only = null;
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == "-sanityseeds") int.TryParse(args[i + 1], out seeds);
            if (args[i] == "-sanityonly") only = args[i + 1];
        }
        bool ok = FaceSanity.Run(out string report, seeds, only);
        Debug.Log("FACE SANITY\n" + report);
        string dir = Path.Combine(Path.GetDirectoryName(Application.dataPath), "Builds", "results");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "facesanity.txt"), report);
        if (Application.isBatchMode) EditorApplication.Exit(ok ? 0 : 1);
    }
}
