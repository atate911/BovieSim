using System;
using System.Linq;
using System.Reflection;
using System.Threading;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEngine;

namespace BovieSim.Bootstrap
{
    /// <summary>
    /// Command-line entry points used by maintain.cmd (Unity.exe -batchmode -executeMethod BovieSim.Bootstrap.BatchEntry.X).
    /// This assembly never depends on HDRP, so it compiles on a fresh clone; once the packages are present it hands
    /// over to BovieSim.EditorTools.BatchCommands by reflection.
    /// Exit codes: 0 done, 1 failed, 3 packages or input setting changed (run again), 4 editor scripts missing
    /// (did not compile), 5 setup required first.
    /// </summary>
    public static class BatchEntry
    {
        public const int ExitRestart = 3, ExitNoEditorCode = 4;

        /// <summary>Install missing packages and set Active Input Handling to Both; exits 0 when nothing was needed.</summary>
        public static void Prepare() { EditorApplication.Exit(EnsurePackages() ? 0 : ExitRestart); }

        public static void Setup() { Forward("Setup"); }

        public static void BuildWindows() { Forward("BuildWindows"); }

        static void Forward(string method)
        {
            if (!EnsurePackages()) { EditorApplication.Exit(ExitRestart); return; }
            var type = AppDomain.CurrentDomain.GetAssemblies()
                .Select(a => a.GetType("BovieSim.EditorTools.BatchCommands", false))
                .FirstOrDefault(t => t != null);
            if (type == null)
            {
                Debug.LogError("[Bovie Sim] The Bovie Sim editor scripts are not loaded (HDRP missing or a compile error above).");
                EditorApplication.Exit(ExitNoEditorCode);
                return;
            }
            // BatchCommands calls EditorApplication.Exit itself
            type.GetMethod(method, BindingFlags.Public | BindingFlags.Static).Invoke(null, null);
        }

        /// <returns>true when everything was already in place; false when something changed and Unity must restart</returns>
        static bool EnsurePackages()
        {
            bool ready = true;
            var list = Client.List(true, false);
            if (!Wait(list, "list packages")) { EditorApplication.Exit(1); return false; }
            var installed = list.Result.Select(p => p.name).ToList();
            var missing = PackageInstaller.Required.Where(r => !installed.Contains(r)).ToArray();
            if (missing.Length > 0)
            {
                Debug.Log("[Bovie Sim] Installing packages: " + string.Join(", ", missing));
                var add = Client.AddAndRemove(missing, null);
                if (!Wait(add, "install " + string.Join(", ", missing))) { EditorApplication.Exit(1); return false; }
                Debug.Log("[Bovie Sim] Packages installed; Unity must restart to compile them.");
                ready = false;
            }
            if (PackageInstaller.EnsureBothInputHandlers(false)) ready = false;
            return ready;
        }

        static bool Wait(UnityEditor.PackageManager.Requests.Request request, string what)
        {
            var until = DateTime.UtcNow.AddMinutes(15);
            while (!request.IsCompleted && DateTime.UtcNow < until) Thread.Sleep(200);
            if (request.IsCompleted && request.Status == StatusCode.Success) return true;
            Debug.LogError("[Bovie Sim] Could not " + what + ": " + (request.IsCompleted ? request.Error?.message : "timed out"));
            return false;
        }
    }
}
