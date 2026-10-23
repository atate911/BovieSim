using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace BovieSim.EditorTools
{
    /// <summary>
    /// Batch-mode commands reached through BovieSim.Bootstrap.BatchEntry (maintain.cmd setup / build).
    /// Each ends the editor with an exit code: 0 done, 1 failed, 5 setup required first.
    /// </summary>
    public static class BatchCommands
    {
        const string RegistryPath = "Assets/BovieSim/Resources/BovieSim/SimAssets.asset";
        const string ScenePath = "Assets/BovieSim/Generated/Main.unity";

        public static void Setup()
        {
            EditorApplication.Exit(ProjectSetup.RunSetup(false) ? 0 : 1);
        }

        /// <summary>Windows 64-bit player into the folder given by -buildPath (default Builds/BovieSim).</summary>
        public static void BuildWindows()
        {
            var assets = AssetDatabase.LoadAssetAtPath<SimAssets>(RegistryPath);
            if (assets == null || assets.setupVersion < SimAssets.CurrentSetupVersion || !File.Exists(ScenePath))
            {
                Debug.LogError("[Bovie Sim] Set Up Project has not been run for this version; run setup before building.");
                EditorApplication.Exit(5);
                return;
            }
            string dir = Arg("-buildPath") ?? Path.Combine("Builds", "BovieSim");
            var options = new BuildPlayerOptions
            {
                scenes = new[] { ScenePath },
                locationPathName = Path.Combine(dir, "BovieSim.exe"),
                target = BuildTarget.StandaloneWindows64,
                options = BuildOptions.None,
            };
            var summary = BuildPipeline.BuildPlayer(options).summary;
            Debug.Log("[Bovie Sim] Build " + summary.result + ": " + summary.outputPath + ", " + summary.totalErrors + " errors, " +
                      (summary.totalSize / (1024 * 1024)) + " MB, " + summary.totalTime.TotalSeconds.ToString("0") + " s");
            EditorApplication.Exit(summary.result == BuildResult.Succeeded ? 0 : 1);
        }

        static string Arg(string name)
        {
            var a = Environment.GetCommandLineArgs();
            for (int i = 0; i < a.Length - 1; i++) if (string.Equals(a[i], name, StringComparison.OrdinalIgnoreCase)) return a[i + 1];
            return null;
        }
    }
}
