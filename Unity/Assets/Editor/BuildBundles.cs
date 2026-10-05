using UnityEditor;
using UnityEngine;
using System.IO;

public static class BuildBundles
{
    // Builds Build/plumefx.shaders. Called from the command line: -executeMethod BuildBundles.Build
    public static void Build()
    {
        string outDir = Path.GetFullPath(Path.Combine(Application.dataPath, "../Build"));
        Directory.CreateDirectory(outDir);
        AssetImporter.GetAtPath("Assets/Shaders").assetBundleName = "plumefx.shaders";
        foreach (string g in AssetDatabase.FindAssets("", new[] { "Assets/Shaders" }))
        {
            string p = AssetDatabase.GUIDToAssetPath(g);
            AssetImporter imp = AssetImporter.GetAtPath(p);
            if (imp != null) imp.assetBundleName = "plumefx.shaders";
        }
        BuildPipeline.BuildAssetBundles(outDir, BuildAssetBundleOptions.ChunkBasedCompression, BuildTarget.StandaloneWindows64);
        Debug.Log("PLUMEFX_BUILD_DONE " + outDir);
    }
}
