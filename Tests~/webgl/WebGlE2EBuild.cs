#if UNITY_EDITOR
using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;

public static class WebGlE2EBuild
{
    public static void Build()
    {
        string outDir = Environment.GetEnvironmentVariable("JIWE_E2E_OUT") ?? "Build/WebGL";
        Directory.CreateDirectory("Assets/Scenes");
        EditorSceneManager.SaveScene(EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single), "Assets/Scenes/E2E.unity");

        PlayerSettings.productName = "JiweE2E";
        PlayerSettings.WebGL.compressionFormat = WebGLCompressionFormat.Disabled;   // the setting the README tells developers to use
        PlayerSettings.WebGL.decompressionFallback = false;
        PlayerSettings.WebGL.threadsSupport = false;
        PlayerSettings.insecureHttpOption = InsecureHttpOption.AlwaysAllowed;        // the mock is plain http on localhost

        var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
        {
            scenes = new[] { "Assets/Scenes/E2E.unity" },
            locationPathName = outDir,
            target = BuildTarget.WebGL,
            options = BuildOptions.None
        });
        if (report.summary.result != BuildResult.Succeeded) { Console.Error.WriteLine("WebGL build failed: " + report.summary.result); EditorApplication.Exit(1); }
    }
}
#endif
