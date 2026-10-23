using UnityEngine;

namespace BovieSim
{
    /// <summary>What the dominant hand (the pointer) can hold.</summary>
    public enum ToolKind { Pencil, Scalpel, Scissors, Forceps, Suction, Hook }
    /// <summary>What the non-dominant hand holds (None parks it off the field).</summary>
    public enum HandBKind { None, Forceps, Hook, Suction }
    public enum ElectrodeType { Blade = 0, Needle = 1, Ball = 2, Forceps = 3 }

    /// <summary>Hand-held instrument pose helpers (port of src/tools/instrument.ts). All in field millimetres.</summary>
    public static class HandPose
    {
        public static readonly ToolKind[] Order = { ToolKind.Pencil, ToolKind.Scalpel, ToolKind.Scissors, ToolKind.Forceps, ToolKind.Suction, ToolKind.Hook };
        public static readonly HandBKind[] OrderB = { HandBKind.None, HandBKind.Forceps, HandBKind.Hook, HandBKind.Suction };
        public static string Label(ToolKind k)
        {
            switch (k)
            {
                case ToolKind.Pencil: return "Bovie pencil"; case ToolKind.Scalpel: return "#15 scalpel"; case ToolKind.Scissors: return "Metzenbaum scissors";
                case ToolKind.Forceps: return "Adson forceps"; case ToolKind.Suction: return "Frazier suction"; default: return "Skin hook";
            }
        }
        public static string Label(HandBKind k)
        {
            switch (k) { case HandBKind.Forceps: return "Adson forceps"; case HandBKind.Hook: return "Skin hook"; case HandBKind.Suction: return "Frazier suction"; default: return "none"; }
        }

        /// <summary>
        /// The handle runs toward the surgeon's right hand: to-camera direction rotated 55 deg, 38 deg up; pen tilt overrides.
        /// The non-dominant hand mirrors it (rotated the other way, a little steeper) so the two instruments do not cross.
        /// </summary>
        public static Vector3 Axis(Vector3 hit, Vector3 cameraPos, bool hasTilt, Vector2 tiltDeg, bool leftHand = false)
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
                float ang = (leftHand ? 55 : -55) * Mathf.Deg2Rad, ca = Mathf.Cos(ang), sa = Mathf.Sin(ang);
                float hx = toCam.x * ca - toCam.z * sa, hz = toCam.x * sa + toCam.z * ca;
                float el = (leftHand ? 45 : 38) * Mathf.Deg2Rad;
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

        /// <summary>Distance (mm) from a point to an instrument's metal modelled as a capsule from `tip` along `axis`: pencil-to-instrument contact.</summary>
        public static float MetalDistance(Vector3 p, Vector3 tip, Vector3 axis, float length, float r)
        {
            var rel = p - tip;
            float t = Mathf.Clamp(Vector3.Dot(rel, axis), 0, length);
            return (rel - axis * t).magnitude - r;
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

        /// <summary>Closed forceps jaws (a to b, capsule radius r) become the electrode: "buzzing the forceps". Also the
        /// tine of a retractor the pencil has touched (an alternate-site burn).</summary>
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

    /// <summary>
    /// #15 scalpel on a #3 handle (port of src/tools/scalpel.ts): a mechanical cut with no thermal effect. The blade is a
    /// thin plate in the plane of the handle axis and the direction of travel; pressure sets how deep the belly sits.
    /// Each frame the blade sweeps from its previous pose to the current one (MechCut, sharp).
    /// </summary>
    public sealed class ScalpelTool
    {
        public Vector3 tip, axis = Vector3.up, wdir = Vector3.right, ndir = Vector3.forward;
        public Vector3 prevCentre;
        public bool sweeping, cutting, visible = true;
        public float pressure = 0.5f;
        public float halfWidth = 1.6f, halfThickness = 0.15f;
        bool hasLast; Vector3 lastHit;

        /// <summary>depth of the blade in the tissue: 0.5 mm (feather) to 5 mm (through dermis into fat)</summary>
        public float Depth { get { return 0.5f + pressure * 4.5f; } }
        public float HalfLength { get { return (Depth + 1.0f) / 2; } }
        public Vector3 Centre { get { return tip + axis * HalfLength; } }

        public void Place(bool hasHit, Vector3 hit, Vector3 cameraPos, bool hasTilt, Vector2 tilt, bool cut)
        {
            if (!hasHit) { visible = false; cutting = false; sweeping = false; hasLast = false; return; }
            visible = true;
            bool wasCutting = cutting && hasLast;
            var prev = Centre;
            axis = HandPose.Axis(hit, cameraPos, hasTilt, tilt);
            Vector3 x, z; HandPose.Frame(axis, out x, out z);
            if (hasLast)
            {
                var travel = hit - lastHit; travel -= axis * Vector3.Dot(travel, axis);
                if (travel.magnitude > 0.4f) { var n = Vector3.Cross(axis, travel.normalized); if (n.sqrMagnitude > 1e-6f) ndir = n.normalized; }
            }
            else ndir = z;
            ndir -= axis * Vector3.Dot(ndir, axis);
            if (ndir.sqrMagnitude < 1e-6f) ndir = z;
            ndir.Normalize();
            wdir = Vector3.Cross(ndir, axis).normalized;
            cutting = cut;
            tip = hit + axis * (cut ? -Depth : 0.2f);
            sweeping = cut && wasCutting;
            prevCentre = sweeping ? prev : Centre;
            lastHit = hit; hasLast = true;
        }
    }

    /// <summary>
    /// Metzenbaum scissors (port of src/tools/scissors.ts): a snip divides the tissue between the open tips along the
    /// blades (MechCut, sharp); inserted closed and opened, the tips spread the tissue apart (MechCut, blunt).
    /// </summary>
    public sealed class ScissorsTool
    {
        public Vector3 tip, axis = Vector3.up, wdir = Vector3.right, ndir = Vector3.forward, tipA, tipB;
        public Vector3 armAy, armAz, armBy, armBz;
        public bool visible = true, snip, spreading;
        public float open, maxOpen = 7f, jawLength = 20f, shankLength = 95f, insert = 2f, pressure = 0.5f, spreadRadius = 0.7f;

        public float HalfLength { get { return (insert + 0.5f) / 2; } }
        public Vector3 Centre { get { return tip + axis * HalfLength; } }

        public enum Action { None, Snip, Spread }

        public void Place(bool hasHit, Vector3 hit, Vector3 cameraPos, bool hasTilt, Vector2 tilt, Action action, float dt)
        {
            snip = false; spreading = false;
            if (!hasHit) { visible = false; open = 0; return; }
            visible = true;
            axis = HandPose.Axis(hit, cameraPos, hasTilt, tilt);
            Vector3 x, z; HandPose.Frame(axis, out x, out z);
            wdir = z; ndir = x;
            insert = 1 + pressure * 7;
            bool working = action != Action.None;
            tip = hit + axis * (working ? -insert : 0.3f);
            if (action == Action.Spread) { open = Mathf.Min(maxOpen, open + maxOpen * dt / 0.4f); spreading = true; }
            else if (action == Action.Snip) { open = Mathf.Max(open, 3); snip = true; }
            else open = Mathf.Max(0, open - maxOpen * dt / 0.25f);
            tipA = tip - wdir * (open / 2); tipB = tip + wdir * (open / 2);
            var pivot = tip + axis * jawLength;
            armAy = (pivot - tipA).normalized; armAz = Vector3.Cross(ndir, armAy).normalized;
            armBy = (pivot - tipB).normalized; armBz = Vector3.Cross(ndir, armBy).normalized;
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

        public void Place(bool hasHit, Vector3 hit, Vector3 cameraPos, bool hasTilt, Vector2 tilt, float penetration = 0.3f, bool leftHand = false)
        {
            if (!hasHit) { visible = false; return; }
            visible = true;
            axis = HandPose.Axis(hit, cameraPos, hasTilt, tilt, leftHand);
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
        public void Place(bool hasHit, Vector3 hit, Vector3 cameraPos, bool hasTilt, Vector2 tilt, bool leftHand = false)
        {
            if (!hasHit) { visible = false; return; }
            visible = true;
            axis = HandPose.Axis(hit, cameraPos, hasTilt, tilt, leftHand);
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
        public void Place(bool hasAnchor, Vector3 anchor, Vector3 cameraPos, bool hasTilt, Vector2 tilt, bool leftHand = false)
        {
            if (!hasAnchor) { visible = false; return; }
            visible = true;
            axis = HandPose.Axis(anchor, cameraPos, hasTilt, tilt, leftHand);
            tip = anchor;
            Vector3 x; HandPose.Frame(axis, out x, out frameZ);
        }
    }
}
