using UnityEngine;

namespace BovieSim
{
    /// <summary>Mouse, keyboard pedals and stylus (pressure and tilt via legacy touch stylus data).</summary>
    public sealed class InputState
    {
        public Vector2 mouse;
        public bool inside, leftHeld, ctrl;
        public float pressure = 0.5f;
        public bool isPen; public Vector2 tiltDeg;
        public bool pedalCut, pedalCoag;

        public void Update()
        {
            mouse = Input.mousePosition;
            inside = mouse.x >= 0 && mouse.y >= 0 && mouse.x <= Screen.width && mouse.y <= Screen.height;
            leftHeld = Input.GetMouseButton(0) && !Input.GetKey(KeyCode.LeftAlt) && !Input.GetKey(KeyCode.LeftShift) && !Input.GetKey(KeyCode.RightShift);
            ctrl = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
            pedalCut = Input.GetKey(KeyCode.Z);
            pedalCoag = Input.GetKey(KeyCode.X);
            isPen = false; tiltDeg = Vector2.zero;
            pressure = leftHeld ? 0.5f : 0.35f;
            for (int i = 0; i < Input.touchCount; i++)
            {
                var t = Input.GetTouch(i);
                if (t.type != TouchType.Stylus) continue;
                isPen = true;
                mouse = t.position;
                if (t.maximumPossiblePressure > 0) pressure = Mathf.Clamp01(t.pressure / t.maximumPossiblePressure);
                // altitude: 0 = flat, pi/2 = vertical; azimuth around the screen normal
                float fromVertical = (Mathf.PI / 2 - t.altitudeAngle) * Mathf.Rad2Deg;
                tiltDeg = new Vector2(fromVertical * Mathf.Cos(t.azimuthAngle), fromVertical * Mathf.Sin(t.azimuthAngle));
                leftHeld = t.phase != TouchPhase.Ended && t.phase != TouchPhase.Canceled;
                break;
            }
        }

        public bool HasTilt { get { return isPen && (Mathf.Abs(tiltDeg.x) > 2 || Mathf.Abs(tiltDeg.y) > 2); } }
    }
}
