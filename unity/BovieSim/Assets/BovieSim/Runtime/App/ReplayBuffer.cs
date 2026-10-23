using System;
using System.Collections;
using UnityEngine;

namespace BovieSim
{
    /// <summary>
    /// The last few seconds of what the trainee saw, kept as downscaled frames on the GPU (a ring of render
    /// textures, ~30 per second). Replaying recorded frames, not re-running the physics, keeps a teaching
    /// moment exact and cheap. Captured at the end of the frame from the screen, so the panels are included.
    /// </summary>
    public sealed class ReplayBuffer : IDisposable
    {
        readonly RenderTexture[] ring;
        int head, count; float sinceLast;
        RenderTexture grab;
        public bool recording = true;
        public bool supported = true;
        const float Interval = 1f / 30f;

        public ReplayBuffer(float seconds, int width)
        {
            int frames = Mathf.Max(15, Mathf.RoundToInt(seconds / Interval));
            int height = Mathf.RoundToInt(width * 9f / 16f);
            ring = new RenderTexture[frames];
            for (int i = 0; i < frames; i++)
            {
                ring[i] = new RenderTexture(width, height, 0, RenderTextureFormat.ARGB32) { name = "BovieReplay" + i, filterMode = FilterMode.Bilinear };
                ring[i].Create();
            }
        }

        public int Count { get { return count; } }
        public float Seconds { get { return count * Interval; } }

        /// <summary>Frame i of the recording, 0 = oldest.</summary>
        public Texture Frame(int i)
        {
            if (count == 0) return null;
            i = Mathf.Clamp(i, 0, count - 1);
            return ring[(head - count + i + ring.Length * 2) % ring.Length];
        }

        public void Clear() { count = 0; }

        /// <summary>Run as a coroutine on the runner: captures after each frame is fully rendered.</summary>
        public IEnumerator CaptureLoop()
        {
            var eof = new WaitForEndOfFrame();
            while (true)
            {
                yield return eof;
                if (!recording || !supported) continue;
                sinceLast += Time.unscaledDeltaTime;
                if (sinceLast < Interval) continue;
                sinceLast = 0;
                try { Capture(); }
                catch (Exception e) { supported = false; Debug.LogWarning("[Bovie Sim] Replay capture is not available on this system: " + e.Message); }
            }
        }

        void Capture()
        {
            if (grab == null || grab.width != Screen.width || grab.height != Screen.height)
            {
                if (grab != null) grab.Release();
                grab = new RenderTexture(Mathf.Max(16, Screen.width), Mathf.Max(16, Screen.height), 0, RenderTextureFormat.ARGB32) { name = "BovieReplayGrab" };
                grab.Create();
            }
            ScreenCapture.CaptureScreenshotIntoRenderTexture(grab);
            // the captured image is upside down where texture UVs start at the top (Direct3D, Metal, Vulkan)
            if (SystemInfo.graphicsUVStartsAtTop) Graphics.Blit(grab, ring[head], new Vector2(1, -1), new Vector2(0, 1));
            else Graphics.Blit(grab, ring[head]);
            head = (head + 1) % ring.Length;
            count = Mathf.Min(count + 1, ring.Length);
        }

        public void Dispose()
        {
            foreach (var r in ring) if (r != null) r.Release();
            if (grab != null) grab.Release();
        }
    }
}
