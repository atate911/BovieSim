using UnityEngine;

namespace BovieSim
{
    public enum ToolKind { Pencil, Forceps, Suction, Hook }
    public enum ElectrodeType { Blade = 0, Needle = 1, Ball = 2, Forceps = 3 }

    /// <summary>Hand-held instrument pose helpers (port of src/tools/instrument.ts). All in field millimetres.</summary>
    public static class HandPose
    {
        public static readonly ToolKind[] Order = { ToolKind.Pencil, ToolKind.Forceps, ToolKind.Suction, ToolKind.Hook };
        public static string Label(ToolKind k)
        {
            switch (k) { case ToolKind.Pencil: return "Bovie pencil"; case ToolKind.Forceps: return "Adson forceps"; case ToolKind.Suction: return "Frazier suction"; default: return "Skin hook"; }
        }

        /// <summary>The handle runs toward the surgeon's right hand: to-camera direction rotated 55 deg, 38 deg up; pen tilt overrides.</summary>
        public static Vector3 Axis(Vector3 hit, Vector3 cameraPos, bool hasTilt, Vector2 tiltDeg)
        {
            Vector3 axis;
            if (hasTilt)
            {
                float tx = tiltDeg.x * Mathf.Deg2Rad, ty = tiltDeg.y * Mathf.Deg2Rad;
                axis = new Vector3(Mathf.Sin(tx), Mathf.Cos(tx) * Mathf.Cos(ty), -Mathf.Sin(ty));
            }
            else
            {
                var toCam = cameraPos - hit; toCam.y = 0; toCam.Normalize();
                float ang = -55 * Mathf.Deg2Rad, ca = Mathf.Cos(ang), sa = Mathf.Sin(ang);
                float hx = toCam.x * ca - toCam.z * sa, hz = toCam.x * sa + toCam.z * ca;
                float el = 38 * Mathf.Deg2Rad;
                axis = new Vector3(hx * Mathf.Cos(el), Mathf.Sin(el), hz * Mathf.Cos(el));
            }
            return axis.normalized;
        }

        /// <summary>Frame (x, axis, z): z horizontal, x in the vertical plane of the axis.</summary>
        public static void Frame(Vector3 axis, out Vector3 x, out Vector3 z)
        {
            z = Vector3.Cross(axis, Vector3.up);
            if (z.sqrMagnitude < 1e-6f) z = Vector3.forward;
            z.Normalize();
            x = Vector3.Cross(z, axis);
        }

        /// <summary>Applies a frame (local +Y along `y`, +Z along `z`) to a transform in field space.</summary>
        public static void Apply(Transform t, Vector3 origin, Vector3 y, Vector3 z)
        {
            t.localPosition = origin;
            t.localRotation = Quaternion.LookRotation(z, y);
        }
    }

    /// <summary>Bovie pencil electrode geometry and pose (port of src/tools/electrode.ts).</summary>
    public sealed class ElectrodeTool
    {
        public ElectrodeType type = ElectrodeType.Blade;
        public Vector3 tip, axis = Vector3.up, wdir = Vector3.right, ndir = Vector3.forward;
        public float pressure = 0.5f;
        public Vector2 tilt;
        public float roll;
        public bool visible = true;
        float jawSpan = 0.5f, jawRadius = 0.6f;
        /// <summary>frame for the pencil mesh: local +Y = axis, +Z = horizontal</summary>
        public Vector3 frameZ = Vector3.forward;

        public float Length { get { return type == ElectrodeType.Blade ? 12 : type == ElectrodeType.Needle ? 10 : type == ElectrodeType.Forceps ? jawSpan : 3; } }
        public float HalfWidth { get { return type == ElectrodeType.Blade ? 1.2f : type == ElectrodeType.Needle ? 0.2f : type == ElectrodeType.Forceps ? jawRadius : 1.5f; } }
        public float HalfThickness { get { return type == ElectrodeType.Blade ? 0.25f : type == ElectrodeType.Needle ? 0.2f : type == ElectrodeType.Forceps ? jawRadius : 1.5f; } }
        public float Penetration { get { return 0.15f + pressure * 0.9f; } }

        public void Place(bool hasHit, Vector3 hit, Vector3 cameraPos, bool hasTilt)
        {
            if (!hasHit) { visible = false; return; }
            visible = true;
            axis = HandPose.Axis(hit, cameraPos, hasTilt, tilt);
            tip = hit - axis * Penetration;
            Vector3 x0, z0; HandPose.Frame(axis, out x0, out z0);
            float cr = Mathf.Cos(roll), sr = Mathf.Sin(roll);
            ndir = z0 * cr + x0 * sr;
            wdir = x0 * cr - z0 * sr;
            frameZ = z0;
        }

        /// <summary>Closed forceps jaws (a to b, capsule radius r) become the electrode: "buzzing the forceps".</summary>
        public void AsForceps(Vector3 a, Vector3 b, float r, Vector3 handleAxis)
        {
            type = ElectrodeType.Forceps; visible = true;
            tip = a;
            var d = b - a; jawSpan = Mathf.Max(d.magnitude, 0.1f); d.Normalize();
            wdir = d; axis = handleAxis;
            ndir = Vector3.Cross(d, handleAxis).normalized;
            jawRadius = r;
        }
    }

    /// <summary>Adson forceps: grasp (coapt), conduct (buzz), lift (lattice attachment).</summary>
    public sealed class ForcepsTool
    {
        public Vector3 tipA, tipB, axis = Vector3.up, centre;
        public bool closed, toothed = true, visible = true;
        public float openMm = 4f, closedMm = 0.5f, jawRadius = 0.6f, armLength = 95f;
        public Vector3 armAy, armAz, armBy, armBz;
        public float Span { get { return closed ? closedMm : openMm; } }
        public Vector3 Mid { get { return (tipA + tipB) * 0.5f; } }

        public void Place(bool hasHit, Vector3 hit, Vector3 cameraPos, bool hasTilt, Vector2 tilt, float penetration = 0.3f)
        {
            if (!hasHit) { visible = false; return; }
            visible = true;
            axis = HandPose.Axis(hit, cameraPos, hasTilt, tilt);
            Vector3 x, z; HandPose.Frame(axis, out x, out z);
            centre = hit - axis * penetration;
            tipA = centre - z * (Span / 2); tipB = centre + z * (Span / 2);
            var pivot = centre + axis * armLength;
            armAy = (pivot - tipA).normalized; armAz = Vector3.Cross(x, armAy).normalized;
            armBy = (pivot - tipB).normalized; armBz = Vector3.Cross(x, armBy).normalized;
        }
    }

    /// <summary>Frazier suction: drains blood within its radius and pulls smoke while held.</summary>
    public sealed class SuctionTool
    {
        public Vector3 tip, axis = Vector3.up, frameZ = Vector3.forward;
        public bool on, visible = true;
        public float drainRadius = 2.5f;
        public void Place(bool hasHit, Vector3 hit, Vector3 cameraPos, bool hasTilt, Vector2 tilt)
        {
            if (!hasHit) { visible = false; return; }
            visible = true;
            axis = HandPose.Axis(hit, cameraPos, hasTilt, tilt);
            tip = hit + axis * 0.5f;
            Vector3 x; HandPose.Frame(axis, out x, out frameZ);
        }
    }

    /// <summary>Skin hook: attaches to the nearest lattice node on press, drags it, releases on button up.</summary>
    public sealed class HookTool
    {
        public Vector3 tip, axis = Vector3.up, frameZ = Vector3.forward;
        public bool visible = true;
        public int node = -1;
        public void Place(bool hasAnchor, Vector3 anchor, Vector3 cameraPos, bool hasTilt, Vector2 tilt)
        {
            if (!hasAnchor) { visible = false; return; }
            visible = true;
            axis = HandPose.Axis(anchor, cameraPos, hasTilt, tilt);
            tip = anchor;
            Vector3 x; HandPose.Frame(axis, out x, out frameZ);
        }
    }
}
