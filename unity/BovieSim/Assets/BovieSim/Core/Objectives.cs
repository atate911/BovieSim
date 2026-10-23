using System;
using System.Collections.Generic;

namespace BovieSim
{
    public enum ObjectiveState { Pending, Ok, Warning, Failed, Met }

    public sealed class ObjectiveStatus
    {
        public Criterion criterion;
        public ObjectiveState state = ObjectiveState.Pending;
        public float value; public bool hasValue;
        public string valueText = "—";
        /// <summary>a criterion on a quantity that only grows (blood lost, char, rim) stays failed once breached</summary>
        public bool sticky;
    }

    /// <summary>
    /// Live evaluation of a scenario's success criteria (education.success in the scenario JSON).
    /// "&lt;" limits warn at 80 % and fail at the limit; "==" with a positive target is an achievement (pending until
    /// met, then stays met); "== 0" is a state that should be clear (warning while not, failed if the quantity is
    /// cumulative). Metric values come from the app through a lookup so this stays engine independent.
    /// </summary>
    public sealed class Objectives
    {
        static readonly HashSet<string> Cumulative = new HashSet<string> { "bloodLostMl", "charVolumeMm3", "dermalEdgeDamageMm", "lesionBreaches", "charPlugVoxels" };

        public readonly List<ObjectiveStatus> items = new List<ObjectiveStatus>();

        public Objectives(EducationSpec ed)
        {
            if (ed == null || ed.success == null) return;
            foreach (var c in ed.success) items.Add(new ObjectiveStatus { criterion = c, sticky = Cumulative.Contains(c.metric) });
        }

        public bool AllMet
        {
            get
            {
                if (items.Count == 0) return false;
                foreach (var s in items) if (s.state != ObjectiveState.Ok && s.state != ObjectiveState.Met) return false;
                return true;
            }
        }

        /// <summary>metric returns NaN when a value is not available (yet)</summary>
        public void Update(Func<string, float> metric)
        {
            foreach (var s in items)
            {
                float v = metric(s.criterion.metric);
                if (float.IsNaN(v)) { s.hasValue = false; continue; }
                s.hasValue = true; s.value = v; s.valueText = Format(s.criterion.metric, v);
                if (s.state == ObjectiveState.Met) continue;
                if (s.sticky && s.state == ObjectiveState.Failed) continue;
                s.state = Evaluate(s.criterion, v, s.sticky);
            }
        }

        public static ObjectiveState Evaluate(Criterion c, float v, bool cumulative)
        {
            float lim = c.value;
            switch (c.op)
            {
                case "<":
                case "<=":
                    {
                        bool ok = c.op == "<" ? v < lim : v <= lim;
                        if (!ok) return ObjectiveState.Failed;
                        return lim > 0 && v >= 0.8f * lim ? ObjectiveState.Warning : ObjectiveState.Ok;
                    }
                case ">":
                case ">=":
                    return (c.op == ">" ? v > lim : v >= lim) ? ObjectiveState.Met : ObjectiveState.Pending;
                case "==":
                    if (lim > 0) return Math.Abs(v - lim) < 1e-4f ? ObjectiveState.Met : ObjectiveState.Pending;
                    if (Math.Abs(v - lim) < 1e-4f) return ObjectiveState.Ok;
                    return cumulative ? ObjectiveState.Failed : ObjectiveState.Warning;
                default:
                    return ObjectiveState.Pending;
            }
        }

        public static string Format(string metric, float v)
        {
            switch (metric)
            {
                case "dermalEdgeDamageMm": return v.ToString("0.00") + " mm";
                case "charVolumeMm3": return v.ToString("0.0") + " mm³";
                case "bloodLostMl": return v.ToString("0.00") + " ml";
                case "exposedBleeding": return v == 0 ? "none" : v.ToString("0") + (v == 1 ? " open end" : " open ends");
                case "charPlugVoxels": return v == 0 ? "none" : v.ToString("0") + " voxels";
                case "lesionDelivered": return v >= 1 ? "yes" : "not yet";
                case "lesionBreaches": return v == 0 ? "none" : v.ToString("0");
                default: return v.ToString("0.##");
            }
        }
    }
}
