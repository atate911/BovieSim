using System;
using System.Collections.Generic;
using UnityEngine;

namespace BovieSim
{
    // Scenario schema shared with the web client (src/core/scenario/schema.ts). Parsed with JsonUtility:
    // fields absent from the JSON keep their initialiser values, which is how optional fields default.

    [Serializable] public class BedSpec { public float[] sizeMm = { 60, 60, 35 }; public float voxelMm = 0.25f; }

    [Serializable]
    public class LayerSpec
    {
        public float epidermisMm = 0.15f, dermisMm = 2f, fatMm = 18f, superficialFasciaMm = 0.4f, superficialFasciaAt = 0.55f, deepFasciaMm = 0.8f;
        public float variation = 0f;
    }

    [Serializable] public class SeptaSpec { public float lobuleMm = 6f, thicknessMm = 0.35f; }

    [Serializable] public class ExplicitVessel { public float xMm, yMm, rMm; public bool arterial; }

    [Serializable]
    public class VesselSpec
    {
        public int seed = 7;
        public float perforatorsPerCm2 = 1f, arterialFraction = 0.4f;
        public float[] radiusMm = { 0.3f, 1.0f };
        public bool dermalPlexus = true;
        public List<ExplicitVessel> @explicit = new List<ExplicitVessel>();
    }

    [Serializable] public class NerveSpec { public float[] fromMm = { 0, 0 }, toMm = { 0, 0 }; public float depthMm = 5f, radiusMm = 0.5f; }

    [Serializable]
    public class LesionSpec
    {
        /// <summary>"lipoma", or empty when the scenario has no lesion</summary>
        public string type = "";
        public float[] centerMm = { 0, 0 };
        public float topDepthMm = 4f;
        public float[] sizeMm = { 10, 8, 6 };
        public float capsuleMm = 0.3f;
        /// <summary>subcutaneous | subfascial | intramuscular</summary>
        public string plane = "subcutaneous";
        public bool IsLipoma { get { return type == "lipoma"; } }
    }

    [Serializable]
    public class PreCarve
    {
        /// <summary>trench | pit</summary>
        public string kind = "trench";
        public float[] centerMm = { 0, 0 };
        public float lengthMm = 20f, widthMm = 2f, depthMm = 5f;
        public float angleDeg = 0f;
        /// <summary>negative = use the kind's default (0.3 trench, 0.4 pit)</summary>
        public float slope = -1f;
    }

    [Serializable] public class SkinSpec { public string prep = "chlorhexidine"; public float tone = 0.35f; public float langerDeg = 0f; }

    [Serializable]
    public class GeneratorPreset
    {
        public string cutMode = "pure", coagMode = "fulgurate";
        public int cutWatts = 30, coagWatts = 30;
        public string policy = "suggested";
    }

    [Serializable] public class Criterion { public string metric = "", op = "<", label = ""; public float value; }
    [Serializable] public class FailurePoint { public string id = "", trigger = "", consequence = "", teaching = ""; }
    [Serializable] public class DecisionOption { public string label = "", outcome = ""; }
    [Serializable] public class DecisionPoint { public string id = "", trigger = "", prompt = ""; public List<DecisionOption> options = new List<DecisionOption>(); }
    [Serializable] public class Uncertainty { public string id = "", description = "", affects = ""; }

    [Serializable]
    public class EducationSpec
    {
        public string goal = "";
        public List<Criterion> success = new List<Criterion>();
        public List<FailurePoint> failurePoints = new List<FailurePoint>();
        public List<DecisionPoint> decisionPoints = new List<DecisionPoint>();
        public List<Uncertainty> ambiguity = new List<Uncertainty>();
    }

    [Serializable]
    public class Scenario
    {
        public string id = "", name = "", description = "";
        public BedSpec bed = new BedSpec();
        public LayerSpec layers = new LayerSpec();
        public SeptaSpec septa = new SeptaSpec();
        public VesselSpec vessels = new VesselSpec();
        public List<NerveSpec> nerves = new List<NerveSpec>();
        public LesionSpec lesion = new LesionSpec();
        public List<PreCarve> preCarve = new List<PreCarve>();
        public SkinSpec skin = new SkinSpec();
        public GeneratorPreset generator = new GeneratorPreset();
        public List<string> faults = new List<string>();
        public EducationSpec education = new EducationSpec();

        public static Scenario FromJson(string json)
        {
            var s = JsonUtility.FromJson<Scenario>(json);
            if (s == null) throw new ArgumentException("scenario JSON did not parse");
            if (string.IsNullOrEmpty(s.id)) throw new ArgumentException("scenario missing field: id");
            if (s.bed == null || s.bed.sizeMm == null || s.bed.sizeMm.Length < 3) throw new ArgumentException("scenario bed.sizeMm must have 3 values");
            if (!(s.bed.voxelMm > 0.05f && s.bed.voxelMm <= 2f)) throw new ArgumentException("bed.voxelMm out of range");
            if (s.lesion == null) s.lesion = new LesionSpec();
            if (s.vessels.@explicit == null) s.vessels.@explicit = new List<ExplicitVessel>();
            if (s.preCarve == null) s.preCarve = new List<PreCarve>();
            if (s.nerves == null) s.nerves = new List<NerveSpec>();
            return s;
        }
    }
}
