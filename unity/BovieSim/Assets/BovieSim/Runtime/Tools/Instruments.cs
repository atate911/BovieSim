using UnityEngine;

namespace BovieSim
{
    /// <summary>What the right hand (the pointer and the left button) can hold.</summary>
    public enum ToolKind { Pencil, Scalpel, Scissors, Forceps, Hemostat, Suction, Gauze, Hook }
    /// <summary>What the left hand (the right mouse button) holds; None leaves that button to the camera.</summary>
    public enum HandBKind { None, Hook, Forceps, Suction }
    public enum ElectrodeType { Blade = 0, Needle = 1, Ball = 2, Forceps = 3 }

    /// <summary>Hand-held instrument pose helpers (port of src/tools/instrument.ts). All in field millimetres.</summary>
    public static class HandPose
    {
        public static readonly ToolKind[] Order = { ToolKind.Pencil, ToolKind.Scalpel, ToolKind.Scissors, ToolKind.Forceps, ToolKind.Hemostat, ToolKind.Suction, ToolKind.Gauze, ToolKind.Hook };
        public static readonly HandBKind[] OrderB = { HandBKind.None, HandBKind.Hook, HandBKind.Forceps, HandBKind.Suction };
        public static string Label(ToolKind k)
        {
            switch (k)
            {
                case ToolKind.Pencil: return "Bovie pencil"; case ToolKind.Scalpel: return "#15 scalpel"; case ToolKind.Scissors: return "Metzenbaum scissors";
                case ToolKind.Forceps: return "Adson forceps"; case ToolKind.Hemostat: return "Kelly hemostat";
                case ToolKind.Suction: return "Frazier suction"; case ToolKind.Gauze: return "4x4 gauze"; default: return "Skin hook";
            }
        }
        public static string Label(HandBKind k)
        {
            switch (k) { case HandBKind.Hook: return "Skin hook"; case HandBKind.Forceps: return "Adson forceps"; case HandBKind.Suction: return "Frazier suction"; default: return "none"; }
        }

        /// <summary>
        /// The handle runs toward the surgeon's right hand: to-camera direction rotated 55 deg, 38 deg up; pen tilt overrides.
        /// The left hand mirrors it (rotated the other way, a little steeper) so the two instruments do not cross.
        /// </summary>
        public static Vector3 Axis(Vector3 hit, Vector3 cameraPos, bool hasTilt, Vector2 tiltDeg, bool leftHand = false)
        {
            Vector3 axis;
            if (hasTilt && !leftHand)
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

        /// <summary>Distance (mm) from a point to an instrument's metal, a capsule from `tip` along `axis`: pencil-to-instrument contact.</summary>
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
        /// <summary>the tip in material coordinates, where the voxel physics lives: equal to `tip` until tissue is displaced;
        /// the runner maps it through the lattice each frame after placing the instrument</summary>
        public Vector3 physTip;
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
            tip = hit - axis * Penetration; physTip = tip;
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
            tip = a; physTip = a;
            var d = b - a; jawSpan = Mathf.Max(d.magnitude, 0.1f); d.Normalize();
            wdir = d; axis = handleAxis;
            ndir = Vector3.Cross(d, handleAxis).normalized;
            jawRadius = r;
        }
    }

    /// <summary>
    /// #15 scalpel on a #3 handle (port of src/tools/scalpel.ts): a mechanical cut with no thermal effect. The blade is a
    /// thin plate in the plane of the handle axis and the direction of travel; pressure sets how deep the belly sits.
    /// Each frame it sweeps from its previous pose to the current one and divides what it passed through (MechCut).
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
            // the blade plane follows the direction of travel so the edge leads; stationary, it keeps its orientation
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
    /// Metzenbaum scissors (port of src/tools/scissors.ts). A click snips the sheet between the open tips along the
    /// blades (MechCut); held, the closed tips go into the tissue (pressure sets how far) and the spread key opens them:
    /// blunt dissection with the hemostat's rules (Dissect, spread).
    /// </summary>
    public sealed class ScissorsTool
    {
        public Vector3 tip, axis = Vector3.up, wdir = Vector3.right, ndir = Vector3.forward, tipA, tipB;
        public Vector3 armAy, armAz, armBy, armBz;
        public bool visible = true, closed, spreading;
        public float open, restMm = 3f, spreadMm = 8f, jawLength = 20f, shankLength = 95f, insert = 0.3f, jawRadius = 0.5f;

        public Vector3 Mid { get { return tip; } }
        /// <summary>half length along the axis of the sheet a snip divides: from the tips to just above the surface</summary>
        public float HalfLength { get { return (insert + 0.5f) / 2; } }
        public Vector3 Centre { get { return tip + axis * HalfLength; } }

        public void Place(bool hasHit, Vector3 hit, Vector3 cameraPos, bool hasTilt, Vector2 tilt, bool held, bool spread, float pressure, float dt)
        {
            if (!hasHit) { visible = false; open = 0; closed = false; spreading = false; return; }
            visible = true;
            axis = HandPose.Axis(hit, cameraPos, hasTilt, tilt);
            Vector3 x, z; HandPose.Frame(axis, out x, out z);
            wdir = z; ndir = x;
            closed = held; spreading = held && spread;
            // resting, the open tips straddle the tissue at the surface; held, they close and go in; spreading, they open again
            float target = spreading ? spreadMm : held ? 0 : restMm;
            float rate = (spreading ? spreadMm / 0.5f : spreadMm / 0.2f) * dt;
            open += Mathf.Clamp(target - open, -rate, rate);
            insert = held ? 1 + pressure * 5 : 1.2f;
            tip = hit - axis * insert;
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

    /// <summary>
    /// Kelly / mosquito hemostat (port of src/tools/hemostat.ts): curved jaws that grasp like forceps and, pushed into a
    /// plane and opened, dissect bluntly. Closed tips sit `insertMm` below the surface hit (pen pressure pushes them
    /// deeper); `spread` 0..1 opens the jaws from closedMm to spreadMm over about half a second while the spread key is held.
    /// </summary>
    public sealed class HemostatTool
    {
        public Vector3 tipA, tipB, axis = Vector3.up, centre;
        /// <summary>jaws closed (grasp / push)</summary>
        public bool closed;
        /// <summary>0..1 opening while spreading; true while the spread key is held with the jaws inserted</summary>
        public float spread; public bool spreading;
        public bool visible = true;
        public float openMm = 5f, closedMm = 0.6f, spreadMm = 8f;
        /// <summary>capsule radius of the jaw tips used for coaptation, conduction and dissection, mm</summary>
        public float jawRadius = 0.7f;
        /// <summary>how deep the tips are pushed into tissue (mm), from pen pressure</summary>
        public float insertMm = 1.5f;
        public float armLength = 120f;
        public Vector3 armAy, armAz, armBy, armBz;
        /// <summary>tip midpoint of the previous frame (push dissection runs along the direction of travel)</summary>
        public Vector3 prevMid; bool hadPrev;

        public float Span { get { return spreading ? closedMm + (spreadMm - closedMm) * spread : closed ? closedMm : openMm; } }
        public Vector3 Mid { get { return (tipA + tipB) * 0.5f; } }

        /// <summary>Advances the spread animation: opens while `hold` and the jaws are inserted, closes again when released.</summary>
        public void Animate(bool hold, float dt)
        {
            spreading = hold && closed;
            spread = Mathf.Clamp01(spread + (spreading ? dt / 0.5f : -dt / 0.25f));
        }

        public void Place(bool hasHit, Vector3 hit, Vector3 cameraPos, bool hasTilt, Vector2 tilt, float pressure = 0.5f)
        {
            if (!hasHit) { visible = false; hadPrev = false; return; }
            visible = true;
            axis = HandPose.Axis(hit, cameraPos, hasTilt, tilt);
            Vector3 x, z; HandPose.Frame(axis, out x, out z);
            // closed tips are pushed into the tissue along the handle axis; open jaws rest on the surface
            insertMm = closed ? 0.8f + pressure * 2.6f : 0.3f;
            centre = hit - axis * insertMm;
            if (hadPrev) prevMid = Mid; else { prevMid = centre; hadPrev = true; }
            tipA = centre - z * (Span / 2); tipB = centre + z * (Span / 2);
            // each arm: local y along the arm from its tip to the box lock, arms converge at the lock
            var pivot = centre + axis * (armLength * 0.55f);
            armAy = (pivot - tipA).normalized; armAz = Vector3.Cross(x, armAy).normalized;
            armBy = (pivot - tipB).normalized; armBz = Vector3.Cross(x, armBy).normalized;
        }

        /// <summary>Tip travel since the previous frame (mm); push dissection needs the closed tips to be moving through tissue.</summary>
        public float Travel() { return hadPrev ? Vector3.Distance(prevMid, Mid) : 0f; }
    }

    /// <summary>
    /// Folded 4x4 gauze held in the fingers (port of src/tools/gauze.ts). Pressed on the field it soaks up liquid blood
    /// within its footprint and tamponades the surface beneath it; dragged with pressure it wipes, taking clot and stain
    /// with it but disturbing the capillaries so the surface oozes again. It holds a limited volume; Fresh() discards it.
    /// </summary>
    public sealed class GauzeTool
    {
        public Vector3 centre, axis = Vector3.up, velocity, frameZ = Vector3.forward;
        /// <summary>0..1 how hard the gauze is pressed (left button + pen pressure)</summary>
        public float pressure;
        public bool visible = true;
        /// <summary>footprint radius, mm (a folded 4x4 pressed down covers about a 12 mm patch)</summary>
        public float radius = 6f;
        /// <summary>capacity of a folded 4x4, microlitres</summary>
        public float capacityUl = 8000f;
        public float soakedUl; public int discarded;
        Vector3 lastCentre; bool hadLast;

        public float Saturation { get { return Mathf.Min(1f, soakedUl / capacityUl); } }
        public bool Saturated { get { return soakedUl >= capacityUl; } }
        /// <summary>absorption capability left: 0 when saturated</summary>
        public float Absorb { get { return Saturated ? 0f : 1f; } }

        public void Fresh() { soakedUl = 0; discarded++; }

        public void Place(bool hasHit, Vector3 hit, Vector3 cameraPos, bool hasTilt, Vector2 tilt, bool pressed, float penPressure, float dt)
        {
            if (!hasHit) { visible = false; pressure = 0; hadLast = false; velocity = Vector3.zero; return; }
            visible = true;
            axis = HandPose.Axis(hit, cameraPos, hasTilt, tilt);
            // the pad sits on the surface, pressed in a little
            pressure = pressed ? Mathf.Max(0.3f, penPressure) : 0f;
            centre = hit + axis * (pressed ? -0.4f * pressure : 0.8f);
            velocity = hadLast && dt > 0 ? (centre - lastCentre) / dt : Vector3.zero;
            lastCentre = centre; hadLast = true;
            Vector3 x; HandPose.Frame(axis, out x, out frameZ);
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
