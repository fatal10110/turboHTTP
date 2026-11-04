// Host-editor driver for Tools~/unity/run-samples.sh (#268). Not part of the package: the script
// copies this folder into a throwaway project's Assets/SamplesRun. Each [UnityTest] adds one
// imported sample MonoBehaviour to a GameObject in the test scene (the same as adding the
// component to a scene and pressing Play), records every console message to
// <project>/SamplesRun/<sample>.log and waits for the sample's last console line.
// The samples call public services (httpbin.org, jsonplaceholder.typicode.com), so this never
// runs in PR CI or in the package's own test suites.
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

public class SamplesRunTests
{
    private const float TimeoutSeconds = 90f;

    private static string OutDir => Path.GetFullPath(Path.Combine(Application.dataPath, "..", "SamplesRun"));

    [UnityTest]
    public IEnumerator Sample01_BasicUsage() =>
        Run<TurboHTTP.Samples.BasicUsage.BasicUsageExample>("01-BasicUsage", "=== Example 6: Error Handling ===", "Error:");

    [UnityTest]
    public IEnumerator Sample02_JsonApi() =>
        Run<TurboHTTP.Samples.JsonApi.JsonApiExample>("02-JsonApi", "Delete status:");

    [UnityTest]
    public IEnumerator Sample03_FileDownload() =>
        Run<TurboHTTP.Samples.FileDownload.FileDownloadExample>("03-FileDownload", "File saved to:");

    [UnityTest]
    public IEnumerator Sample04_Authentication() =>
        Run<TurboHTTP.Samples.Authentication.AuthenticationExample>("04-Authentication", "Token was refreshed automatically!");

    [UnityTest]
    public IEnumerator Sample05_AdvancedFeatures() =>
        Run<TurboHTTP.Samples.AdvancedFeatures.AdvancedFeaturesExample>("05-AdvancedFeatures", "Average response time:");

    // Completion: every marker substring was logged, or an exception/error line that is not an
    // expected one ended the sample early. The verdict is left to the recorded log; the test only
    // fails on timeout, so a sample that logs its own errors (sample 01 does, by design) is still
    // recorded in full.
    private static IEnumerator Run<T>(string name, params string[] markers) where T : MonoBehaviour
    {
        Directory.CreateDirectory(OutDir);
        var lines = new List<string>();
        var sw = new StringBuilder();
        void OnLog(string condition, string stack, LogType type) =>
            lines.Add("[" + type + "] " + condition + (type == LogType.Exception || type == LogType.Error ? "\n" + stack : ""));

        LogAssert.ignoreFailingMessages = true; // the samples log errors on purpose (sample 01 example 6)
        if (name == "03-FileDownload")
        {
            var stale = Path.Combine(Application.persistentDataPath, "downloaded.bin");
            if (File.Exists(stale)) File.Delete(stale);
        }

        Application.logMessageReceived += OnLog;
        GameObject go = null;
        var start = Time.realtimeSinceStartup;
        var timedOut = false;
        try
        {
            go = new GameObject(name);
            go.AddComponent<T>();
            while (!Done(lines, markers))
            {
                if (Time.realtimeSinceStartup - start > TimeoutSeconds) { timedOut = true; break; }
                yield return null;
            }
            // let the last continuation and any trailing log line land
            for (var i = 0; i < 10; i++) yield return null;
        }
        finally
        {
            Application.logMessageReceived -= OnLog;
            if (go != null) UnityEngine.Object.Destroy(go);
            sw.AppendLine("sample: " + name + " (" + typeof(T).FullName + ")");
            sw.AppendLine("unity: " + Application.unityVersion);
            sw.AppendLine("timed_out: " + timedOut);
            sw.AppendLine("elapsed_s: " + (Time.realtimeSinceStartup - start).ToString("F1"));
            sw.AppendLine("--- console ---");
            foreach (var l in lines) sw.AppendLine(l);
            File.WriteAllText(Path.Combine(OutDir, name + ".log"), sw.ToString());
        }
        Assert.IsFalse(timedOut, name + " did not reach its last console line within " + TimeoutSeconds + "s");
    }

    private static bool Done(List<string> lines, string[] markers)
    {
        foreach (var m in markers)
        {
            var seen = false;
            foreach (var l in lines)
                if (l.Contains(m)) { seen = true; break; }
            if (!seen)
            {
                // an unhandled exception from the async void sample ends it
                foreach (var l in lines)
                    if (l.StartsWith("[Exception]", StringComparison.Ordinal)) return true;
                return false;
            }
        }
        return true;
    }
}
