using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace BovieSim
{
    /// <summary>
    /// IMGUI overlay: Valleylab FT10 front panel (port of src/ui/ft10Panel.ts) with on-screen footswitch,
    /// status HUD, event feed with teaching text, scenario brief and the cross-section inspector.
    /// </summary>
    public sealed class SimHud
    {
        public bool pedalCut, pedalCoag;
        public bool showBrief = true, showHelp;
        public string hudText = "";
        readonly List<SimEvent> feed = new List<SimEvent>();
        GUIStyle box, title, small, big, lamp, wrap, feedInfo, feedWarn, feedBad;
        Texture2D panelBg, blockBg, cutBg, coagBg, lampOff, lampCut, lampCoag, feedBg;
        Rect panelRect, sliceRect, feedRect, hudRect, briefRect;

        static readonly string[] SliceModes = { "tissue", "temperature", "damage (omega)", "water", "potential", "current density" };

        public void Push(SimEvent e)
        {
            feed.Add(e);
            while (feed.Count > 6) feed.RemoveAt(0);
        }

        static Texture2D Solid(Color c)
        {
            var t = new Texture2D(1, 1, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave };
            t.SetPixel(0, 0, c); t.Apply(); return t;
        }

        void Init()
        {
            if (box != null) return;
            panelBg = Solid(new Color(0.11f, 0.13f, 0.15f, 0.94f)); blockBg = Solid(new Color(0.06f, 0.07f, 0.08f, 1));
            cutBg = Solid(new Color(0.55f, 0.48f, 0.08f, 1)); coagBg = Solid(new Color(0.10f, 0.25f, 0.55f, 1));
            lampOff = Solid(new Color(0.2f, 0.2f, 0.2f, 1)); lampCut = Solid(new Color(1f, 0.85f, 0.15f, 1)); lampCoag = Solid(new Color(0.3f, 0.6f, 1f, 1));
            feedBg = Solid(new Color(0, 0, 0, 0.55f));
            box = new GUIStyle(GUI.skin.box) { normal = { background = panelBg }, padding = new RectOffset(8, 8, 6, 6) };
            title = new GUIStyle(GUI.skin.label) { fontSize = 11, fontStyle = FontStyle.Bold, normal = { textColor = new Color(0.8f, 0.85f, 0.9f) } };
            small = new GUIStyle(GUI.skin.label) { fontSize = 11, normal = { textColor = new Color(0.85f, 0.88f, 0.9f) }, richText = true };
            big = new GUIStyle(GUI.skin.label) { fontSize = 30, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter, normal = { textColor = new Color(0.4f, 1f, 0.55f) } };
            lamp = new GUIStyle(GUI.skin.label);
            wrap = new GUIStyle(GUI.skin.label) { wordWrap = true, richText = true, fontSize = 12, normal = { textColor = new Color(0.92f, 0.93f, 0.95f) } };
            feedInfo = new GUIStyle(wrap) { normal = { background = feedBg, textColor = new Color(0.85f, 0.95f, 1f) }, padding = new RectOffset(6, 6, 3, 3) };
            feedWarn = new GUIStyle(feedInfo) { normal = { background = feedBg, textColor = new Color(1f, 0.85f, 0.4f) } };
            feedBad = new GUIStyle(feedInfo) { normal = { background = feedBg, textColor = new Color(1f, 0.5f, 0.45f) } };
        }

        /// <summary>True when the pointer is over an IMGUI panel (screen coordinates, origin bottom-left as Input.mousePosition).</summary>
        public bool PointerOverUi(Vector2 mouse)
        {
            var p = new Vector2(mouse.x, Screen.height - mouse.y);
            return panelRect.Contains(p) || (sliceRect.width > 0 && sliceRect.Contains(p)) || (showBrief && briefRect.Contains(p));
        }

        public void Draw(FT10 gen, SimReadout r, PhysicsPipeline physics, Scenario sc)
        {
            Init();
            DrawPanel(gen, r);
            DrawHud();
            DrawFeed();
            if (physics != null && physics.sliceEnabled) DrawSlice(physics); else sliceRect = new Rect();
            if (showBrief && sc != null) DrawBrief(sc);
        }

        void DrawPanel(FT10 gen, SimReadout r)
        {
            float w = 380, h = 196;
            panelRect = new Rect(Screen.width - w - 12, Screen.height - h - 12, w, h);
            GUILayout.BeginArea(panelRect, box);
            GUILayout.BeginHorizontal();
            GUILayout.Label("Valleylab™ FT10", title);
            GUILayout.FlexibleSpace();
            var remCol = GUI.color; GUI.color = gen.remFault ? new Color(1, 0.3f, 0.3f) : new Color(0.4f, 1f, 0.5f);
            GUILayout.Label(gen.remFault ? "REM FAULT" : "REM ●", title);
            GUI.color = remCol;
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            Block(gen, true, gen.activation == Activation.Cut);
            Block(gen, false, gen.activation == Activation.Coag);
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            string z = gen.activation == Activation.None ? "Z — Ω" : "Z " + (r.zOhm < 1e8f ? r.zOhm.ToString("0") : "—") + " Ω · " + r.powerW.ToString("0") + " W";
            GUILayout.Label(z, small, GUILayout.Width(150));
            GUILayout.FlexibleSpace();
            // on-screen footswitch: held while the mouse button is down over it
            pedalCut = HoldButton("CUT pedal", cutBg);
            pedalCoag = HoldButton("COAG pedal", coagBg);
            GUILayout.EndHorizontal();
            GUILayout.EndArea();
        }

        void Block(FT10 gen, bool cut, bool lit)
        {
            GUILayout.BeginVertical(new GUIStyle(box) { normal = { background = blockBg } }, GUILayout.Width(175));
            GUILayout.BeginHorizontal();
            GUILayout.Label(cut ? "MONOPOLAR CUT" : "MONOPOLAR COAG", title);
            var lr = GUILayoutUtility.GetRect(12, 12, GUILayout.Width(12), GUILayout.Height(12));
            GUI.DrawTexture(lr, lit ? (cut ? lampCut : lampCoag) : lampOff);
            GUILayout.EndHorizontal();
            var m = FT10.Mode(cut ? gen.cutMode : gen.coagMode);
            if (GUILayout.Button(m.label.ToUpperInvariant())) gen.CycleMode(cut);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("−", GUILayout.Width(32), GUILayout.Height(34))) gen.Adjust(cut, Event.current.shift ? -1 : -5);
            var c = GUI.color; GUI.color = cut ? new Color(1f, 0.92f, 0.4f) : new Color(0.55f, 0.75f, 1f);
            GUILayout.Label((cut ? gen.cutWatts : gen.coagWatts).ToString(), big, GUILayout.Height(34));
            GUI.color = c;
            if (GUILayout.Button("+", GUILayout.Width(32), GUILayout.Height(34))) gen.Adjust(cut, Event.current.shift ? 1 : 5);
            GUILayout.EndHorizontal();
            GUILayout.EndVertical();
        }

        bool HoldButton(string label, Texture2D bg)
        {
            var st = new GUIStyle(GUI.skin.button) { normal = { background = bg, textColor = Color.white }, active = { background = bg, textColor = Color.black }, fontStyle = FontStyle.Bold };
            var rect = GUILayoutUtility.GetRect(new GUIContent(label), st, GUILayout.Width(92), GUILayout.Height(30));
            GUI.Box(rect, label, st);
            var mp = Input.mousePosition; var p = new Vector2(mp.x, Screen.height - mp.y);
            return Input.GetMouseButton(0) && rect.Contains(p - panelRect.position);
        }

        void DrawHud()
        {
            hudRect = new Rect(12, 10, Mathf.Min(980, Screen.width - 420), 200);
            var sh = new GUIStyle(small) { normal = { textColor = new Color(0, 0, 0, 0.8f) } };
            GUI.Label(new Rect(hudRect.x + 1, hudRect.y + 1, hudRect.width, hudRect.height), StripTags(hudText), sh);
            GUI.Label(hudRect, hudText, small);
        }

        static string StripTags(string s) { return System.Text.RegularExpressions.Regex.Replace(s, "<[^>]+>", ""); }

        void DrawFeed()
        {
            float w = Mathf.Min(460, Screen.width * 0.36f);
            feedRect = new Rect(12, Screen.height - 12, w, 0);
            float y = Screen.height - 12;
            for (int i = feed.Count - 1; i >= 0; i--)
            {
                var e = feed[i];
                var st = e.severity == Severity.Bad ? feedBad : e.severity == Severity.Warn ? feedWarn : feedInfo;
                string txt = "<b>" + e.t.ToString("0.0") + "s " + e.type + "</b> " + e.detail + (string.IsNullOrEmpty(e.teaching) ? "" : "\n" + e.teaching);
                float hh = st.CalcHeight(new GUIContent(txt), w);
                y -= hh + 4;
                if (y < 230) break;
                GUI.Label(new Rect(12, y, w, hh), txt, st);
            }
        }

        void DrawSlice(PhysicsPipeline physics)
        {
            float w = 320, h = 200;
            sliceRect = new Rect(Screen.width - w - 12, Screen.height - 196 - 24 - h - 22, w, h + 20);
            GUI.Box(sliceRect, GUIContent.none, box);
            GUI.DrawTexture(new Rect(sliceRect.x, sliceRect.y + 20, w, h), physics.sliceTex, ScaleMode.StretchToFill, false);
            GUI.Label(new Rect(sliceRect.x + 6, sliceRect.y + 2, w - 12, 18), "Section · " + SliceModes[Mathf.Clamp(physics.sliceMode, 0, 5)] + " · " + physics.sliceWidthMm.ToString("0") + " × " + physics.sliceDepthMm.ToString("0") + " mm  (C mode, J orient, H hide)", small);
        }

        void DrawBrief(Scenario sc)
        {
            float w = Mathf.Min(520, Screen.width * 0.4f);
            var sb = new StringBuilder();
            sb.Append("<b>").Append(sc.name).Append("</b>\n").Append(sc.description).Append('\n');
            var ed = sc.education;
            if (ed != null)
            {
                if (!string.IsNullOrEmpty(ed.goal)) sb.Append("\n<b>Goal</b> ").Append(ed.goal).Append('\n');
                if (ed.success != null && ed.success.Count > 0) { sb.Append("\n<b>Success</b>\n"); foreach (var c in ed.success) sb.Append(" • ").Append(c.label).Append('\n'); }
                if (ed.failurePoints != null && ed.failurePoints.Count > 0) { sb.Append("\n<b>Failure points</b>\n"); foreach (var f in ed.failurePoints) sb.Append(" • ").Append(f.consequence).Append('\n'); }
                if (ed.decisionPoints != null && ed.decisionPoints.Count > 0) { sb.Append("\n<b>Decisions</b>\n"); foreach (var d in ed.decisionPoints) sb.Append(" • ").Append(d.prompt).Append('\n'); }
                if (ed.ambiguity != null && ed.ambiguity.Count > 0) { sb.Append("\n<b>Uncertain</b>\n"); foreach (var a in ed.ambiguity) sb.Append(" • ").Append(a.description).Append('\n'); }
            }
            sb.Append("\n<i>F1 hides this brief, F2 shows controls.</i>");
            string txt = sb.ToString();
            float hh = Mathf.Min(wrap.CalcHeight(new GUIContent(txt), w - 16) + 14, Screen.height - 260);
            briefRect = new Rect(Screen.width - w - 12 - 392, 12, w, hh);
            GUI.Box(briefRect, GUIContent.none, box);
            GUI.Label(new Rect(briefRect.x + 8, briefRect.y + 6, w - 16, hh - 12), txt, wrap);
        }

        public void Dispose()
        {
            foreach (var t in new[] { panelBg, blockBg, cutBg, coagBg, lampOff, lampCut, lampCoag, feedBg }) if (t != null) Object.Destroy(t);
        }
    }
}
