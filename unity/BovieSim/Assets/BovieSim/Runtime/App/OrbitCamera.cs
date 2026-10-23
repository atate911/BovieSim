using UnityEngine;

namespace BovieSim
{
    /// <summary>Orbit camera around a target in field millimetres. Right-drag orbit, wheel zoom, middle or Shift-drag pan (Ctrl + wheel is left to the depth peel).</summary>
    public sealed class OrbitCamera
    {
        public Vector3 targetMm = Vector3.zero;
        public float azimuth = -0.35f, elevation = 0.95f, distanceMm = 120f;
        readonly Camera cam; readonly Transform field;
        Vector3 last; int dragging; // 0 none, 1 orbit, 2 pan

        public OrbitCamera(Camera camera, Transform fieldRoot) { cam = camera; field = fieldRoot; }

        public Vector3 PositionMm
        {
            get
            {
                float ce = Mathf.Cos(elevation);
                return targetMm + new Vector3(distanceMm * ce * Mathf.Sin(azimuth), distanceMm * Mathf.Sin(elevation), distanceMm * ce * Mathf.Cos(azimuth));
            }
        }

        /// <param name="wheelZoom">false while the wheel is claimed by something else (Ctrl held: depth peel)</param>
        public void HandleInput(bool overUi, bool wheelZoom = true)
        {
            if (!overUi)
            {
                if (Input.GetMouseButtonDown(1) || (Input.GetMouseButtonDown(0) && (Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt)))) { dragging = 1; last = Input.mousePosition; }
                else if (Input.GetMouseButtonDown(2) || (Input.GetMouseButtonDown(0) && (Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift)))) { dragging = 2; last = Input.mousePosition; }
                float wheel = wheelZoom ? Input.mouseScrollDelta.y : 0f;
                if (Mathf.Abs(wheel) > 0.01f) distanceMm = Mathf.Clamp(distanceMm * Mathf.Exp(-wheel * 0.1f), 25f, 600f);
            }
            if (dragging != 0 && !Input.GetMouseButton(0) && !Input.GetMouseButton(1) && !Input.GetMouseButton(2)) dragging = 0;
            if (dragging != 0)
            {
                var d = Input.mousePosition - last; last = Input.mousePosition;
                if (dragging == 1)
                {
                    azimuth += d.x * 0.005f;
                    elevation = Mathf.Clamp(elevation - d.y * 0.005f, 0.15f, 1.5f);
                }
                else
                {
                    var right = new Vector3(Mathf.Cos(azimuth), 0, -Mathf.Sin(azimuth));
                    var fwd = new Vector3(Mathf.Sin(azimuth), 0, Mathf.Cos(azimuth));
                    float s = distanceMm * 0.0012f;
                    targetMm += right * (-d.x * s) + fwd * (d.y * s);
                }
            }
        }

        public bool Dragging { get { return dragging != 0; } }

        public void Apply()
        {
            var posW = field.TransformPoint(PositionMm);
            var tgtW = field.TransformPoint(targetMm);
            cam.transform.position = posW;
            cam.transform.rotation = Quaternion.LookRotation(tgtW - posW, Vector3.up);
        }
    }
}
