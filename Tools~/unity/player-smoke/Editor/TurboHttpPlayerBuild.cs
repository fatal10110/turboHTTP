// Editor half of Tools~/unity/build-player.sh, copied to the throwaway project's
// Assets/TurboHttpSmoke/Editor/ (Assembly-CSharp-Editor). Run with
//   -executeMethod TurboHttpPlayerBuild.Build -smokeTarget <target> -smokeOutput <path> -smokeBouncyCastle 0|1
//     [-smokeArch arm64|x86_64]   (Android only: x86_64 is for the emulators; default arm64)
// It sets the scripting backend and managed stripping level for the target, checks that the
// TURBOHTTP_BOUNCYCASTLE define matches the request (the script writes it into ProjectSettings
// before Unity starts), builds one empty scene, copies the generated TurboHTTP link.xml next to
// the build (Temp/ is deleted on exit) and exits 0 on a successful build, 1 otherwise.
using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;

public static class TurboHttpPlayerBuild
{
    private const string Define = "TURBOHTTP_BOUNCYCASTLE";
    private const string ScenePath = "Assets/TurboHttpSmoke/Smoke.unity";

    public static void Build()
    {
        var code = 1;
        try
        {
            code = BuildCore();
        }
        catch (Exception ex)
        {
            Debug.LogError("TURBOHTTP_BUILD error " + ex);
        }

        EditorApplication.Exit(code);
    }

    private static int BuildCore()
    {
        var target = Arg("-smokeTarget");
        var output = Arg("-smokeOutput");
        var bouncyCastle = Arg("-smokeBouncyCastle") == "1";
        var x86_64 = Arg("-smokeArch") == "x86_64";

        BuildTarget buildTarget;
        BuildTargetGroup group;
        ScriptingImplementation backend;
        switch (target)
        {
            case "win64-mono":
                buildTarget = BuildTarget.StandaloneWindows64;
                group = BuildTargetGroup.Standalone;
                backend = ScriptingImplementation.Mono2x;
                break;
            case "win64-il2cpp":
                buildTarget = BuildTarget.StandaloneWindows64;
                group = BuildTargetGroup.Standalone;
                backend = ScriptingImplementation.IL2CPP;
                break;
            case "android-il2cpp":
                buildTarget = BuildTarget.Android;
                group = BuildTargetGroup.Android;
                backend = ScriptingImplementation.IL2CPP;
                break;
            default:
                Debug.LogError("TURBOHTTP_BUILD error unknown -smokeTarget '" + target + "'");
                return 1;
        }

        var defines = PlayerSettings.GetScriptingDefineSymbolsForGroup(group);
        var hasDefine = defines.Split(';').Contains(Define);
        if (hasDefine != bouncyCastle)
        {
            Debug.LogError("TURBOHTTP_BUILD error " + Define + " is " + (hasDefine ? "set" : "not set") +
                           " for " + group + " (defines '" + defines + "'), expected the opposite");
            return 1;
        }

        PlayerSettings.productName = "TurboHttpSmoke";
        PlayerSettings.SetScriptingBackend(group, backend);
        // IL2CPP builds use High, the level #8 AC2 and #196 AC1 are about; Mono keeps Unity's default
        // (Disabled), so the Mono player proves registration, not linker preservation.
        PlayerSettings.SetManagedStrippingLevel(
            group,
            backend == ScriptingImplementation.IL2CPP ? ManagedStrippingLevel.High : ManagedStrippingLevel.Disabled);
        if (buildTarget == BuildTarget.Android)
        {
            PlayerSettings.SetApplicationIdentifier(group, "com.turbohttp.playersmoke");
            PlayerSettings.Android.targetArchitectures = x86_64 ? AndroidArchitecture.X86_64 : AndroidArchitecture.ARM64;
            // The --network rows open sockets from Assembly-CSharp: never depend on Unity's INTERNET auto-detection.
            PlayerSettings.Android.forceInternetPermission = true;
            // As run-device-tests.sh: emulator images (x86_64) get GLES (API 23-25 lack GLES3, Vulkan froze the
            // headless emulator); phones keep the default graphics APIs.
            PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.Android, !x86_64);
            if (x86_64)
            {
                PlayerSettings.SetGraphicsAPIs(BuildTarget.Android,
                    new[] { UnityEngine.Rendering.GraphicsDeviceType.OpenGLES3, UnityEngine.Rendering.GraphicsDeviceType.OpenGLES2 });
            }

            EditorUserBuildSettings.buildAppBundle = false;
            EditorUserBuildSettings.exportAsGoogleAndroidProject = false;
        }

        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        EditorSceneManager.SaveScene(scene, ScenePath);

        Debug.Log("TURBOHTTP_BUILD start target=" + target + " backend=" + backend + " stripping=" +
                  PlayerSettings.GetManagedStrippingLevel(group) +
                  (buildTarget == BuildTarget.Android ? " arch=" + PlayerSettings.Android.targetArchitectures : string.Empty) +
                  " defines='" + defines + "' output=" + output);
        var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
        {
            scenes = new[] { ScenePath },
            locationPathName = output,
            target = buildTarget,
            targetGroup = group,
            options = BuildOptions.None,
        });

        var linkXml = Path.Combine("Temp", "TurboHTTP", "TurboHTTP.link.xml");
        if (File.Exists(linkXml))
        {
            var outputDirectory = Path.GetDirectoryName(Path.GetFullPath(output));
            File.Copy(linkXml, Path.Combine(outputDirectory, "TurboHTTP.link.xml"), true);
        }

        var summary = report.summary;
        Debug.Log("TURBOHTTP_BUILD result=" + summary.result + " errors=" + summary.totalErrors +
                  " size=" + summary.totalSize + " time=" + summary.totalTime);
        return summary.result == BuildResult.Succeeded ? 0 : 1;
    }

    private static string Arg(string name)
    {
        var args = Environment.GetCommandLineArgs();
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == name)
                return args[i + 1];
        }

        return string.Empty;
    }
}
