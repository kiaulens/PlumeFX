using System;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

// Command line: Unity.exe -batchmode -projectPath ... -executeMethod PlumePreviewMenu.Run -quit [-pfxDuration 20] [-pfxVideo chase]
// [-pfxShots 30,60] (without -nographics, so the GPU renders)
public static class PlumePreviewMenu
{
    public static void Run()
    {
        string[] args = Environment.GetCommandLineArgs();
        float duration = 20f;
        string video = null;
        float[] shotsArg = null;
        int w = 960, h = 640;
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == "-pfxDuration") float.TryParse(args[i + 1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out duration);
            if (args[i] == "-pfxVideo") video = args[i + 1];
            if (args[i] == "-pfxShots")
            {
                string[] parts = args[i + 1].Split(',');
                shotsArg = new float[parts.Length];
                for (int k = 0; k < parts.Length; k++) float.TryParse(parts[k], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out shotsArg[k]);
            }
        }
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        Shader shader = AssetDatabase.LoadAssetAtPath<Shader>("Assets/Shaders/PlumeFXSmoke.shader");
        string outDir = Path.GetFullPath(Path.Combine(Application.dataPath, "../Preview"));
        float[] shots = shotsArg ?? new float[] { 0.5f, 1.5f, 3f, 5f, 8f, 12f, 16f, 20f, 30f, 45f, 60f };
        PlumeFX.PlumePreviewRunner.Run(shader, outDir, duration, shots, video, w, h);
    }
}
