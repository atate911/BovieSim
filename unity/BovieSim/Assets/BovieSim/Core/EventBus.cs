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
        /// <summary>set by the app each frame</summary>
        public bool suctionOn, buzzing, gauzeDab, gauzeWipe, gauzeSaturated;
        float lastViscera = -1e9f, lastBoil = -1e9f, lastSuctionClot = -1e9f, lastTorn = -1e9f, lastDissect = -1e9f, lastWipe = -1e9f, lastSpasm = -1e9f;
        bool clotWarned, oozeReported, lastGauzeDab, lastGauzeSat; int tornAccum, dissectAccum;
        float lastExposedNonzero; int lastSealedAtBleed, lastPlugAtBleed;

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
                    else if (tt == Tissue.Areolar) Emit(t, "tissue.areolarContact", name);
                    else if (tt == Tissue.Viscera || tt == Tissue.Serosa) Emit(t, "tissue.visceraContact", name, Severity.Warn);
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
            if (active && r.visceraHeatVoxels > 40 && t - lastViscera > 2.0f) { Emit(t, "viscera.heated", r.visceraHeatVoxels + " viscera voxels above 45 °C", Severity.Bad); lastViscera = t; }
            // blood under the electrode boils and coagulates; clot resists suction
            if (active && r.boilColumns > 0 && t - lastBoil > 2.0f) { Emit(t, "blood.boiling", r.boilColumns + " columns of blood boiling under the electrode", Severity.Warn); lastBoil = t; }
            if (r.clotMl > 0.3f && !clotWarned) { Emit(t, "blood.clotted", r.clotMl.ToString("0.0") + " ml of clot in the field"); clotWarned = true; }
            if (r.clotMl < 0.1f) clotWarned = false;
            if (suctionOn && r.clotMl > 0.2f && r.poolAtTipMm > 0.2f && t - lastSuctionClot > 4.0f) { Emit(t, "suction.clot", "clot under the suction tip", Severity.Warn); lastSuctionClot = t; }
            // raw surfaces ooze, tissue tears, planes open
            if (r.oozeVoxels > 10 && !oozeReported) { Emit(t, "tissue.oozing", r.oozeVoxels + " raw-surface voxels oozing"); oozeReported = true; }
            if (r.oozeVoxels == 0) oozeReported = false;
            tornAccum += r.tornVoxels;
            if (tornAccum > 0 && t - lastTorn > 1.0f) { Emit(t, "tissue.torn", tornAccum + " voxels torn", Severity.Warn); tornAccum = 0; lastTorn = t; }
            dissectAccum += r.dissectedVoxels;
            if (dissectAccum > 150 && t - lastDissect > 2.0f) { Emit(t, "plane.dissected", dissectAccum + " voxels separated bluntly"); dissectAccum = 0; lastDissect = t; }
            // gauze: dab vs wipe, saturation
            if (gauzeDab && !lastGauzeDab) Emit(t, "gauze.dab", "pressure on the field");
            lastGauzeDab = gauzeDab;
            if (gauzeWipe && t - lastWipe > 3.0f) { Emit(t, "gauze.wipe", "wiping the raw surface", Severity.Warn); lastWipe = t; }
            if (gauzeSaturated && !lastGauzeSat) Emit(t, "gauze.saturated", "gauze full", Severity.Warn);
            lastGauzeSat = gauzeSaturated;
            // a small vessel that was bleeding has stopped without being sealed: spasm / thrombosis
            if (exposed == 0 && lastExposedNonzero > 0 && r.sealedVoxels == lastSealedAtBleed && r.charPlugVoxels == lastPlugAtBleed && !suctionOn && t - lastSpasm > 5.0f)
            { Emit(t, "vessel.spasm", "bleeding stopped on its own"); lastSpasm = t; }
            if (exposed > 0) { lastExposedNonzero = t; lastSealedAtBleed = r.sealedVoxels; lastPlugAtBleed = r.charPlugVoxels; } else lastExposedNonzero = 0;

            string settings = gen.SettingsKey;
            if (lastSettings.Length > 0 && settings != lastSettings) Emit(t, "generator.settingChanged", gen.cutMode + " " + gen.cutWatts + " W / " + gen.coagMode + " " + gen.coagWatts + " W");
            lastSettings = settings;
            if (toolName != lastTool) { if (lastTool.Length > 0) Emit(t, "tool.changed", toolName); lastTool = toolName; }
        }
    }
}
