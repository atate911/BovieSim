using UnityEngine;

namespace BovieSim
{
    public enum ToolKind { Pencil, Forceps, Hemostat, Suction, Gauze, Hook }
    public enum ElectrodeType { Blade = 0, Needle = 1, Ball = 2, Forceps = 3 }

    /// <summary>Hand-held instrument pose helpers (port of src/tools/instrument.ts). All in field millimetres.</summary>
    public static class HandPose
    {
        public static readonly ToolKind[] Order = { ToolKind.Pencil, ToolKind.Forceps, ToolKind.Hemostat, ToolKind.Suction, ToolKind.Gauze, ToolKind.Hook };
        public static string Label(ToolKind k)
        {
            switch (k)
            {
                case ToolKind.Pencil: return "Bovie pencil"; case ToolKind.Forceps: return "Adson forceps"; case ToolKind.Hemostat: return "Kelly hemostat";
                case ToolKind.Suction: return "Frazier suction"; case ToolKind.Gauze: return "4x4 gauze"; default: return "Skin hook";
            }
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
