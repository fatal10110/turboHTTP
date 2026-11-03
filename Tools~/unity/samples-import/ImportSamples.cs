// Host-editor helper for Tools~/unity/run-samples.sh (#268). Imports every sample of the
// TurboHTTP package through the Package Manager API behind the "Import" button
// (UnityEditor.PackageManager.UI.Sample.Import), so the files land exactly where the button puts
// them. Copied into a throwaway project's Assets/SamplesImport/Editor; never part of the package.
using System;
using System.Linq;
using UnityEditor;
using UnityEditor.PackageManager.UI;
using UnityEngine;

public static class ImportSamples
{
    public static void Run()
    {
        try
        {
            var samples = Sample.FindByPackage("com.turbohttp.complete", null).ToList();
            Debug.Log("SAMPLES_IMPORT found " + samples.Count + " samples");
            var failed = 0;
            foreach (var s in samples)
            {
                var ok = s.Import(Sample.ImportOptions.OverridePreviousImports);
                Debug.Log("SAMPLES_IMPORT " + s.displayName + " -> " + s.importPath + " imported=" + ok + " isImported=" + s.isImported);
                if (!ok || !s.isImported) failed++;
            }
            AssetDatabase.Refresh();
            EditorApplication.Exit(samples.Count == 5 && failed == 0 ? 0 : 1);
        }
        catch (Exception e)
        {
            Debug.LogError("SAMPLES_IMPORT failed: " + e);
            EditorApplication.Exit(1);
        }
    }
}
