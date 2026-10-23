using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEditor.PackageManager.Requests;
using UnityEngine;

namespace BovieSim.Bootstrap
{
    /// <summary>
    /// First-open bootstrap. The project ships without HDRP in its manifest so that it resolves the
    /// HDRP version bundled with whichever Unity 6 editor opens it. This installs the missing packages
    /// once; the HDRP-dependent assemblies compile after that (they are gated on BOVIE_HDRP).
    /// </summary>
    [InitializeOnLoad]
    public static class PackageInstaller
    {
        static readonly string[] Required =
        {
            "com.unity.render-pipelines.high-definition",
            "com.unity.test-framework",
            // pen pressure and tilt on Windows (Wacom via Windows Ink); mouse and keys still use the legacy Input class
            "com.unity.inputsystem",
        };
        const string SessionKey = "BovieSim.PackageInstaller.Ran";

        static ListRequest list;
        static AddAndRemoveRequest add;

        static PackageInstaller()
        {
            EditorApplication.delayCall += EnsureBothInputHandlers;
            if (SessionState.GetBool(SessionKey, false)) return;
            SessionState.SetBool(SessionKey, true);
            EditorApplication.delayCall += Begin;
        }

        [MenuItem("Bovie Sim/Install Required Packages", priority = 0)]
        public static void Begin()
        {
            if (list != null || add != null) return;
            list = Client.List(true, false);
            EditorApplication.update += Poll;
        }

        static void Poll()
        {
            if (list != null)
            {
                if (!list.IsCompleted) return;
                if (list.Status != StatusCode.Success)
                {
                    Debug.LogError("[Bovie Sim] Could not list packages: " + list.Error?.message);
                    Finish();
                    return;
                }
                var installed = new HashSet<string>(list.Result.Select(p => p.name));
                var missing = Required.Where(r => !installed.Contains(r)).ToArray();
                list = null;
                if (missing.Length == 0) { Finish(); return; }
                Debug.Log("[Bovie Sim] Installing packages: " + string.Join(", ", missing) +
                          ". The editor will recompile; then run Bovie Sim > Set Up Project if it does not start automatically.");
                add = Client.AddAndRemove(missing, null);
                return;
            }
            if (add != null)
            {
                if (!add.IsCompleted) return;
                if (add.Status == StatusCode.Success) Debug.Log("[Bovie Sim] Packages installed.");
                else Debug.LogError("[Bovie Sim] Package install failed: " + add.Error?.message +
                                    "\nInstall 'High Definition RP' manually from Window > Package Manager > Unity Registry.");
                add = null;
                Finish();
            }
        }

        static void Finish() => EditorApplication.update -= Poll;

        /// <summary>
        /// Installing the Input System offers to switch Active Input Handling to "Input System Package (New)",
        /// which would make the legacy Input calls throw. The sim needs "Both" (2): set it and offer a restart.
        /// </summary>
        static void EnsureBothInputHandlers()
        {
            var ps = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/ProjectSettings.asset");
            if (ps == null || ps.Length == 0) return;
            var so = new SerializedObject(ps[0]);
            var p = so.FindProperty("activeInputHandler");
            if (p == null || p.intValue == 2) return;
            p.intValue = 2;
            so.ApplyModifiedPropertiesWithoutUndo();
            AssetDatabase.SaveAssets();
            Debug.Log("[Bovie Sim] Active Input Handling set to Both (legacy keys/mouse + Input System pen).");
            if (EditorUtility.DisplayDialog("Bovie Sim", "Active Input Handling was changed to 'Both'. The editor must restart for this to take effect.", "Restart now", "Later"))
                EditorApplication.OpenProject(System.IO.Directory.GetCurrentDirectory());
        }
    }
}
