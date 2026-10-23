using System;
using UnityEngine;

namespace BovieSim
{
    public enum DetailLevel { Auto = -1, Low = 0, Medium = 1, High = 2, Ultra = 3 }

    /// <summary>Everything that scales with the PC. Finer voxels cost GPU time roughly with the cube of 1/size.</summary>
    public sealed class DetailSettings
    {
        public DetailLevel level;
        /// <summary>voxel edge (mm) used for every scenario at this level (the Voxel Override field still wins)</summary>
        public float voxelMm;
        /// <summary>largest field-solve region around the electrode (voxels per side)</summary>
        public int maxSubdomain;
        public int relaxSweeps;
        public int bricksPerFrame;
        /// <summary>frame-time budget (ms): 60 fps normally, 30 fps accepted at the finest level</summary>
        public float frameBudgetMs;
        public float replaySeconds;
        public int replayWidth;
        public bool ssao, softShadows;
        public int shadowResolution;
        /// <summary>section image scale (1 = 640 x 400 pixels); the section is shown 556 px wide at 1080p, so 2 oversamples it</summary>
        public float sliceScale;
        public string Name { get { return level.ToString(); } }

        public static DetailSettings For(DetailLevel l)
        {
            switch (l)
            {
                case DetailLevel.Low: return new DetailSettings { level = l, voxelMm = 0.35f, maxSubdomain = 48, relaxSweeps = 30, bricksPerFrame = 24, frameBudgetMs = 16.7f, replaySeconds = 2f, replayWidth = 640, ssao = false, softShadows = false, shadowResolution = 512, sliceScale = 1.5f };
                case DetailLevel.Medium: return new DetailSettings { level = l, voxelMm = 0.25f, maxSubdomain = 64, relaxSweeps = 40, bricksPerFrame = 48, frameBudgetMs = 16.7f, replaySeconds = 3f, replayWidth = 960, ssao = true, softShadows = true, shadowResolution = 1024, sliceScale = 2f };
                case DetailLevel.High: return new DetailSettings { level = l, voxelMm = 0.2f, maxSubdomain = 80, relaxSweeps = 48, bricksPerFrame = 96, frameBudgetMs = 16.7f, replaySeconds = 4f, replayWidth = 1280, ssao = true, softShadows = true, shadowResolution = 2048, sliceScale = 2.5f };
                default: return new DetailSettings { level = DetailLevel.Ultra, voxelMm = 0.16f, maxSubdomain = 96, relaxSweeps = 56, bricksPerFrame = 128, frameBudgetMs = 33.3f, replaySeconds = 4f, replayWidth = 1280, ssao = true, softShadows = true, shadowResolution = 2048, sliceScale = 2.5f };
            }
        }
    }

    /// <summary>
    /// Detects what the PC can run and picks a detail level. Uses dedicated video memory (the volume and
    /// meshes live there), whether the GPU is integrated (shared memory, low bandwidth) and the hard
    /// requirements (compute shaders, async readback, DirectX 11+/Vulkan/Metal). A running check
    /// (<see cref="FrameBudget"/>) then suggests stepping down if the frame budget is exceeded.
    /// </summary>
    public static class HardwareProfile
    {
        public static string Gpu { get { return SystemInfo.graphicsDeviceName; } }
        public static int VramMb { get { return SystemInfo.graphicsMemorySize; } }

        /// <summary>Null when the PC can run the simulator, otherwise the reason it cannot.</summary>
        public static string Unsupported()
        {
            if (!SystemInfo.supportsComputeShaders) return "this graphics card or driver does not support compute shaders";
            if (!SystemInfo.supportsAsyncGPUReadback) return "this graphics driver does not support asynchronous GPU readback";
            if (SystemInfo.graphicsShaderLevel < 45) return "the graphics card is below shader model 4.5 (DirectX 11 class)";
            if (VramMb > 0 && VramMb < 1500) return "the graphics card has less than 1.5 GB of video memory (" + VramMb + " MB)";
            return null;
        }

        public static bool LooksIntegrated()
        {
            string n = (Gpu ?? "").ToLowerInvariant();
            if (n.Contains("intel") && !n.Contains("arc")) return true;   // UHD, Iris (Arc are discrete)
            if (n.Contains("radeon(tm) graphics") || n.Contains("radeon graphics") || n.Contains("vega 8") || n.Contains("vega 11") || n.Contains("780m") || n.Contains("760m") || n.Contains("890m") || n.Contains("880m")) return true;
            if (n.Contains("apple m")) return false; // Apple silicon: unified but fast
            return false;
        }

        public static DetailLevel Detect(out string reason)
        {
            int vram = VramMb;
            if (LooksIntegrated()) { reason = "integrated graphics (" + Gpu + ")"; return vram >= 4000 ? DetailLevel.Medium : DetailLevel.Low; }
            if (vram >= 12000) { reason = Gpu + ", " + vram / 1024 + " GB"; return DetailLevel.Ultra; }
            if (vram >= 7000) { reason = Gpu + ", " + vram / 1024 + " GB"; return DetailLevel.High; }
            if (vram >= 3500) { reason = Gpu + ", " + vram / 1024 + " GB"; return DetailLevel.Medium; }
            reason = Gpu + ", " + vram + " MB"; return DetailLevel.Low;
        }

        public static DetailLevel Resolve(DetailLevel wanted, out string reason)
        {
            if (wanted != DetailLevel.Auto) { reason = "chosen in Settings"; return wanted; }
            return Detect(out reason);
        }
    }

    /// <summary>
    /// Watches frame time and reports when the budget is exceeded for several seconds, so the interface can
    /// suggest a lower detail level (applied on the next restart; voxel size cannot change mid-scenario).
    /// </summary>
    public sealed class FrameBudget
    {
        readonly float budgetMs; float overSeconds, settle = 5f;
        public bool overBudget;
        public FrameBudget(float budget) { budgetMs = budget; }

        /// <param name="frameMs">smoothed frame time</param>
        public void Update(float frameMs, float dt)
        {
            if (settle > 0) { settle -= dt; return; } // ignore start-up hitches
            if (frameMs > budgetMs * 1.15f) overSeconds += dt; else overSeconds = Math.Max(0, overSeconds - dt * 0.5f);
            overBudget = overSeconds > 4f;
        }
    }
}
