using UnityEngine;

namespace BovieSim
{
    /// <summary>
    /// Mouse, keyboard pedals and pen. Mouse and keys use the legacy Input class. On Windows a Wacom pen
    /// reaches Unity with pressure and tilt only through the Input System (Windows Ink), so the pen is read
    /// from <c>Pen.current</c> when that package is installed; legacy stylus touches remain as a fallback.
    /// </summary>
    public sealed class InputState
    {
        public Vector2 mouse;
        public bool inside, leftHeld, ctrl;
        public float pressure = 0.5f;
        public bool isPen; public Vector2 tiltDeg;
        public bool pedalCut, pedalCoag;
        /// <summary>flip if leaning the pen toward you tilts the instrument away from you</summary>
        public static bool invertTiltY;

        public void Update()
        {
            mouse = Input.mousePosition;
            leftHeld = Input.GetMouseButton(0) && !Input.GetKey(KeyCode.LeftAlt) && !Input.GetKey(KeyCode.LeftShift) && !Input.GetKey(KeyCode.RightShift);
            ctrl = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
            pedalCut = Input.GetKey(KeyCode.Z);
            pedalCoag = Input.GetKey(KeyCode.X);
            isPen = false; tiltDeg = Vector2.zero;
            pressure = leftHeld ? 0.5f : 0.35f;
            if (!ReadInputSystemPen()) ReadLegacyStylus();
            if (invertTiltY) tiltDeg.y = -tiltDeg.y;
            inside = mouse.x >= 0 && mouse.y >= 0 && mouse.x <= Screen.width && mouse.y <= Screen.height;
        }

        bool ReadInputSystemPen()
        {
#if BOVIE_INPUTSYSTEM
            var pen = UnityEngine.InputSystem.Pen.current;
            if (pen == null || !pen.inRange.isPressed) return false;
            isPen = true;
            mouse = pen.position.ReadValue();
            bool tip = pen.tip.isPressed;
            // hovering pen: light default contact so the electrode still tracks the surface
            pressure = tip ? Mathf.Clamp01(pen.pressure.ReadValue()) : 0.35f;
            var t = pen.tilt.ReadValue(); // -1..1 per axis, 1 = 90 degrees from vertical
            tiltDeg = new Vector2(t.x * 90f, t.y * 90f);
            leftHeld = tip || (Input.GetMouseButton(0) && !Input.GetKey(KeyCode.LeftAlt));
            return true;
#else
            return false;
#endif
        }

        void ReadLegacyStylus()
        {
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
