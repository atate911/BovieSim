using System;
using System.Collections.Generic;
using UnityEngine;

namespace BovieSim
{
    public enum Severity { Info, Warn, Bad }

    public sealed class SimEvent
    {
        public float t; public string type, detail, teaching; public Severity severity;
    }

    [Serializable] public class TeachingEntry { public string type = "", text = ""; }
    [Serializable] public class TeachingTable { public List<TeachingEntry> entries = new List<TeachingEntry>(); }

    /// <summary>
    /// Physics to education event bus, same event names as src/education/events.ts. Teaching text comes
    /// from scenarios/teaching.json (shared with the web client).
    /// </summary>
    public sealed class EventBus
    {
        public readonly List<SimEvent> events = new List<SimEvent>();
        public event Action<SimEvent> OnEvent;
        readonly Dictionary<string, string> teaching = new Dictionary<string, string>();

        int lastContactType = -1; bool lastActive; float noContactSince = -1; bool noContactReported;
        int lastCarbon; int coagAccum; float lastCoagReport = -1e9f; string lastSettings = ""; string lastTool = "";
        int lastExposed, lastSealed, lastCharPlug, lastRebleeds; bool poolWarned, obscuredWarned, lastSuction, lastBuzz; float lastMuscle = -1e9f;
        int cutAccum; float lastCutT = -1e9f; bool oozeWarned; float lastOozeT = -1e9f;
        int bluntAccum; float lastPlane = -1e9f, resistT, lastResist = -1e9f, lastStretch = -1e9f, lastTorn = -1e9f, lastNerve = -1e9f, lastT = -1f; bool lastContact;
        /// <summary>set by the app each frame</summary>
        public bool suctionOn, buzzing, instrumentContact;

        public EventBus(string teachingJson)
        {
            if (string.IsNullOrEmpty(teachingJson)) return;
            var tbl = JsonUtility.FromJson<TeachingTable>(teachingJson);
            if (tbl != null && tbl.entries != null) foreach (var e in tbl.entries) teaching[e.type] = e.text;
        }

        public string Teaching(string type) { string s; return teaching.TryGetValue(type, out s) ? s : null; }

        public void Emit(float t, string type, string detail, Severity sev = Severity.Info)
        {
            var e = new SimEvent { t = t, type = type, detail = detail, teaching = Teaching(type), severity = sev };
            events.Add(e);
            if (OnEvent != null) OnEvent(e);
        }

        public void Update(float t, SimReadout r, FT10 gen, string toolName)
        {
            bool active = gen.activation != Activation.None;
            var mode = gen.ActiveMode;
            if (active && !lastActive) { Emit(t, "activation.start", gen.activation + " " + mode.label + " " + gen.ActiveWatts + " W"); noContactSince = t; noContactReported = false; }
            if (!active && lastActive) Emit(t, "activation.end", "");
            lastActive = active;
            if (active && r.contactVoxels == 0)
            {
                if (!noContactReported && t - noContactSince > 0.4f) { Emit(t, "activation.noContact", gen.activation + " with no tissue contact", Severity.Warn); noContactReported = true; }
            }
            else if (active) { noContactSince = t; noContactReported = false; }

            if (active && r.hasHit)
            {
                int ty = (int)Cell.Type(r.hitCell);
                if (ty != lastContactType)
                {
                    lastContactType = ty;
                    string name = TissueTable.Name(ty);
                    var tt = (Tissue)ty;
                    if (tt == Tissue.Fat || tt == Tissue.Lipoma) Emit(t, "tissue.fatContact", name);
                    else if (tt == Tissue.DeepFascia || tt == Tissue.SuperficialFascia) Emit(t, "tissue.fasciaContact", name);
                    else if (tt == Tissue.Muscle) Emit(t, "tissue.muscleContact", name);
                    else if (tt == Tissue.Artery || tt == Tissue.Vein) Emit(t, "tissue.vesselContact", name, Severity.Warn);
                    else if (tt == Tissue.Nerve) Emit(t, "tissue.nerveContact", name, Severity.Bad);
                }
            }
            else if (!active) lastContactType = -1;

            if (r.carbonVoxels > lastCarbon + 2) Emit(t, "tissue.carbonized", r.carbonVoxels + " voxels of char", Severity.Bad);
            lastCarbon = Math.Max(lastCarbon, r.carbonVoxels);
            coagAccum += r.newCoagVoxels;
            if (coagAccum > 40 && t - lastCoagReport > 1.0f) { Emit(t, "tissue.coagulated", coagAccum + " voxels blanched"); coagAccum = 0; lastCoagReport = t; }
            if (active && r.contactVoxels > 0 && mode != null && r.powerW < 0.6f * gen.ActiveWatts && r.zOhm > mode.zHi && t - lastCoagReport > 1.0f)
            { Emit(t, "tissue.desiccated", "Z " + r.zOhm.ToString("0") + " ohm, only " + r.powerW.ToString("0") + " W delivered", Severity.Warn); lastCoagReport = t; }

            int exposed = r.exposedArterial + r.exposedVenous;
            if (exposed > 0 && lastExposed == 0)
            {
                string kind = r.exposedArterial > 0 ? "arterial" : "venous";
                Emit(t, "vessel.exposed", kind + " vessel end exposed");
                Emit(t, "bleed.start", kind + " bleeding", r.exposedArterial > 0 ? Severity.Warn : Severity.Info);
            }
            lastExposed = exposed;
            if (r.sealedVoxels > lastSealed) Emit(t, "bleed.sealed", (r.sealedVoxels - lastSealed) + " vessel voxels sealed");
            lastSealed = Math.Max(lastSealed, r.sealedVoxels);
            if (r.charPlugVoxels > lastCharPlug) Emit(t, "bleed.charPlug", (r.charPlugVoxels - lastCharPlug) + " vessel voxels under a char plug", Severity.Warn);
            lastCharPlug = Math.Max(lastCharPlug, r.charPlugVoxels);
            if (r.rebleeds > lastRebleeds) Emit(t, "vessel.rebleed", "char plug failed", Severity.Bad);
            lastRebleeds = Math.Max(lastRebleeds, r.rebleeds);
            if (active && r.poolAtTipMm > 0.3f) { if (!poolWarned) { Emit(t, "blood.poolConducts", r.poolAtTipMm.ToString("0.0") + " mm of blood under the electrode", Severity.Warn); poolWarned = true; } }
            else if (!active) poolWarned = false;
            if (r.poolMl > 0.8f && !obscuredWarned) { Emit(t, "field.obscured", r.poolMl.ToString("0.0") + " ml pooled", Severity.Warn); obscuredWarned = true; }
            if (r.poolMl < 0.3f) obscuredWarned = false;
            if (suctionOn && !lastSuction) Emit(t, "suction.used", "field cleared");
            lastSuction = suctionOn;
            if (active && buzzing && !lastBuzz)
            {
                var ty = r.hasHit ? Cell.Type(r.hitCell) : Tissue.Air;
                bool onVessel = ty == Tissue.Artery || ty == Tissue.Vein;
                Emit(t, "forceps.buzzed", onVessel ? "vessel held in the jaws" : "no vessel in the jaws", onVessel ? Severity.Info : Severity.Warn);
            }
            lastBuzz = active && buzzing;
            if (active && mode != null && !mode.isCut && r.muscleHeatVoxels > 20 && t - lastMuscle > 1.5f) { Emit(t, "muscle.stimulated", r.muscleHeatVoxels + " muscle voxels in the current path", Severity.Warn); lastMuscle = t; }

            string settings = gen.SettingsKey;
            if (lastSettings.Length > 0 && settings != lastSettings) Emit(t, "generator.settingChanged", gen.cutMode + " " + gen.cutWatts + " W / " + gen.coagMode + " " + gen.coagWatts + " W");
            lastSettings = settings;
            if (toolName != lastTool) { if (lastTool.Length > 0) Emit(t, "tool.changed", toolName); lastTool = toolName; }

            // steel: a scalpel stroke or scissor snip is reported when it ends
            float frameDt = lastT >= 0 ? Math.Max(0, t - lastT) : 0; lastT = t;
            cutAccum += r.cutVoxels;
            if (r.cutVoxels > 0) lastCutT = t;
            if (cutAccum > 0 && t - lastCutT > 0.4f) { Emit(t, "incision.made", cutAccum + " voxels divided, no heat"); cutAccum = 0; }
            if (r.oozeVoxels > 0) { lastOozeT = t; if (!oozeWarned) { Emit(t, "skin.oozing", r.oozeVoxels + " dermal edge voxels oozing"); oozeWarned = true; } }
            else if (t - lastOozeT > 5) oozeWarned = false;
            // blunt dissection: the plane opening, or something that will not spread
            bluntAccum += r.bluntVoxels;
            if (bluntAccum > 60 && t - lastPlane > 1.5f) { Emit(t, "plane.developed", bluntAccum + " voxels of fat separated"); bluntAccum = 0; lastPlane = t; }
            if (r.bluntResist > 30 && r.bluntVoxels < 5) resistT += frameDt; else resistT = 0;
            if (resistT > 0.5f && t - lastResist > 3) { Emit(t, "plane.resists", r.bluntResist + " voxels resist the spread", Severity.Warn); lastResist = t; resistT = 0; }
            if (r.vesselStretch > 3 && t - lastStretch > 2) { Emit(t, "vessel.stretched", r.vesselStretch + " vessel voxels in the spread", Severity.Warn); lastStretch = t; }
            if (r.vesselTorn > 0 && t - lastTorn > 1) { Emit(t, "vessel.torn", r.vesselTorn + " vessel voxels torn by blunt dissection", Severity.Bad); lastTorn = t; }
            if (r.nerveStretch > 3 && t - lastNerve > 2) { Emit(t, "nerve.stretched", r.nerveStretch + " nerve voxels in the spread", Severity.Warn); lastNerve = t; }
            if (active && instrumentContact && !lastContact) Emit(t, "instrument.contact", "active electrode on another instrument", Severity.Bad);
            lastContact = active && instrumentContact;
        }
    }
}
