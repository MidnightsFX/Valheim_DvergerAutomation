using System.IO;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Builds the "automation" asset bundle into AssetBundles/StandaloneWindows, where the mod's PreBuild
/// step copies it from - the same output the AssetBundle Browser produces, with its default standard
/// (LZMA) compression. A menu item, and callable from the command line:
///
///   Unity.exe -batchmode -quit -projectPath DvergerAutomationUnity -executeMethod BuildAutomationBundle.Build
/// </summary>
public static class BuildAutomationBundle {
    private const string OutputDir = "AssetBundles/StandaloneWindows";

    [MenuItem("DvergerAutomation/Build Asset Bundle")]
    public static void Build() {
        Directory.CreateDirectory(OutputDir);
        AssetBundleManifest manifest = BuildPipeline.BuildAssetBundles(OutputDir, BuildAssetBundleOptions.None, BuildTarget.StandaloneWindows);
        if (manifest == null) {
            Debug.LogError("[Bundle] build failed - see the log above.");
            return;
        }
        foreach (string bundle in manifest.GetAllAssetBundles()) {
            Debug.Log("[Bundle] built " + Path.Combine(OutputDir, bundle));
        }
    }
}
