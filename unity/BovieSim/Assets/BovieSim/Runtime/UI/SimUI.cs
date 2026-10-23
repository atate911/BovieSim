using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace BovieSim
{
    /// <summary>Per-frame values the runner hands to the interface.</summary>
    public sealed class HudData
    {
        public string scenarioName = "", toolText = "", underTip = "—", capsuleText = "", volumeText = "";
        public Activation activation; public int activeWatts; public bool remFault;
        public float zOhm = float.PositiveInfinity, powerW, vRms; public int contact;
        public float maxT = 37, vaporised, simTime, bloodLost, pooled, rimMm, charMm3;
        public int carbon, exposedArt, exposedVen, sealedVx, charPlug, rebleeds, tris; public bool capped;
        // timing
        public float frameMs, cpuMs, gpuMs, fps; public int stateAge, heightsAge, surfaceAge;
        // section
        public float sliceTopY; public List<KeyValuePair<string, float>> layers = new List<KeyValuePair<string, float>>();
        public bool slicePerp;
    }

    /// <summary>
    /// UI Toolkit interface (replaces the IMGUI overlay): status, live objectives, a large labelled cross-section
    /// with legend and depth scale, the FT10 front panel with footswitch, the event feed with teaching text, the
    /// scenario brief, controls and a timing readout. Laid out for 1920x1080 and scaled to any screen, so text
    /// stays sharp. Colours: teal accent, cut yellow, coag blue; amber warns, red fails.
    /// </summary>
    public sealed class SimUI
    {
        // palette
        static readonly Color Card = new Color(0.05f, 0.075f, 0.09f, 0.88f), Line = new Color(1, 1, 1, 0.08f);
        static readonly Color Ink = new Color(0.86f, 0.90f, 0.91f), Muted = new Color(0.56f, 0.64f, 0.67f), Accent = new Color(0.36f, 0.72f, 0.76f);
        static readonly Color CutC = new Color(0.90f, 0.76f, 0.29f), CoagC = new Color(0.50f, 0.65f, 0.96f);
        static readonly Color OkC = new Color(0.42f, 0.80f, 0.56f), WarnC = new Color(0.94f, 0.66f, 0.29f), BadC = new Color(0.94f, 0.54f, 0.50f);
        static readonly string[] ModeNames = { "Tissue", "Temperature", "Damage", "Water", "Potential", "Current" };
        public static readonly float[][] Zooms = { new[] { 16f, 10f }, new[] { 24f, 15f }, new[] { 32f, 20f }, new[] { 48f, 30f }, new[] { 56f, 35f } };

        readonly GameObject go; readonly VisualElement root;
        readonly FT10 gen; readonly PhysicsPipeline physics;
        public bool pedalCut, pedalCoag;
        public bool showBrief = true, showHelp, showPerf;
        public int zoomIndex = 2;
        public event Action ToggleOrientation;

        // status
        Label title, actBanner, actZ, actP, actV, footer;
        readonly Dictionary<string, Label> rows = new Dictionary<string, Label>();
        VisualElement capsuleRow;
        // objectives
        VisualElement objList; readonly List<VisualElement[]> objRows = new List<VisualElement[]>();
        Objectives objectives;
        // section
        VisualElement sectionCard, sectionImgBox, overlay, legendBox; Image sectionImg; Label sectionTitle, orientBtnLabel;
        readonly List<Button> modeButtons = new List<Button>();
        Button isoBtn, dmgBtn; int lastLegendMode = -1; float lastLabelKey = float.NaN;
        // ft10
        Label cutMode, coagMode, cutW, coagW, remLbl, zLine; VisualElement cutLamp, coagLamp; // cutMode/coagMode unused since the segmented mode row
        // feed, brief, help, perf
        VisualElement feed; readonly List<SimEvent> events = new List<SimEvent>();
        VisualElement brief, help; Label perf;
        Texture2D tempTex, damageTex, waterTex;

        public SimUI(Transform parent, ThemeStyleSheet theme, FT10 generator, PhysicsPipeline pipeline)
        {
            gen = generator; physics = pipeline;
            var ps = ScriptableObject.CreateInstance<PanelSettings>();
            ps.themeStyleSheet = theme;
            ps.scaleMode = PanelScaleMode.ScaleWithScreenSize;
            ps.referenceResolution = new Vector2Int(1920, 1080);
            ps.screenMatchMode = PanelScreenMatchMode.MatchWidthOrHeight;
            ps.match = 0.5f;
            ps.sortingOrder = 10;
            go = new GameObject("Bovie UI");
            go.SetActive(false);
            go.transform.SetParent(parent, false);
            var doc = go.AddComponent<UIDocument>();
            doc.panelSettings = ps;
            go.SetActive(true);
            root = doc.rootVisualElement;
            root.pickingMode = PickingMode.Ignore;
            root.style.position = Position.Absolute; root.style.left = 0f; root.style.top = 0f; root.style.right = 0f; root.style.bottom = 0f;
            root.style.fontSize = 15f; root.style.color = Ink;
            BuildTextures();
            BuildStatus(); BuildRightColumn(); BuildFeed(); BuildBrief(); BuildHelp(); BuildPerf();
        }

        // ------------------------------------------------------------------ helpers
        static VisualElement Box(VisualElement parent, bool row = false)
        {
            var e = new VisualElement(); e.pickingMode = PickingMode.Ignore;
            e.style.flexDirection = row ? FlexDirection.Row : FlexDirection.Column;
            parent.Add(e); return e;
        }
        static VisualElement CardBox(VisualElement parent)
        {
            var e = Box(parent); e.pickingMode = PickingMode.Position;
            e.style.backgroundColor = Card;
            Radius(e, 8); Pad(e, 12, 10);
            e.style.borderTopWidth = 1f; e.style.borderBottomWidth = 1f; e.style.borderLeftWidth = 1f; e.style.borderRightWidth = 1f;
            e.style.borderTopColor = Line; e.style.borderBottomColor = Line; e.style.borderLeftColor = Line; e.style.borderRightColor = Line;
            return e;
        }
        static void Radius(VisualElement e, float r) { e.style.borderTopLeftRadius = r; e.style.borderTopRightRadius = r; e.style.borderBottomLeftRadius = r; e.style.borderBottomRightRadius = r; }
        static void Pad(VisualElement e, float x, float y) { e.style.paddingLeft = x; e.style.paddingRight = x; e.style.paddingTop = y; e.style.paddingBottom = y; }
        static Label Text(VisualElement parent, string t, int size, Color c, bool bold = false)
        {
            var l = new Label(t); l.pickingMode = PickingMode.Ignore;
            l.style.fontSize = size; l.style.color = c;
            if (bold) l.style.unityFontStyleAndWeight = FontStyle.Bold;
            l.style.marginLeft = 0f; l.style.marginRight = 0f; l.style.marginTop = 0f; l.style.marginBottom = 0f;
            l.style.paddingLeft = 0f; l.style.paddingRight = 0f; l.style.paddingTop = 0f; l.style.paddingBottom = 0f;
            parent.Add(l); return l;
        }
        static Label Heading(VisualElement parent, string t)
        {
            var l = Text(parent, t.ToUpperInvariant(), 12, Muted, true);
            l.style.letterSpacing = 1.2f; l.style.marginBottom = 6f;
            return l;
        }
        static Button Btn(VisualElement parent, string t, Action click, float w = 0)
        {
            var b = new Button(click) { text = t };
            b.focusable = false; // keep Space/Tab for the simulator, not UI focus navigation
            b.style.fontSize = 13f; b.style.color = Ink;
            b.style.backgroundColor = new Color(1, 1, 1, 0.06f);
            b.style.borderTopWidth = 0f; b.style.borderBottomWidth = 0f; b.style.borderLeftWidth = 0f; b.style.borderRightWidth = 0f;
            Radius(b, 4); b.style.marginLeft = 0f; b.style.marginRight = 4f; b.style.marginTop = 0f; b.style.marginBottom = 0f;
            b.style.paddingLeft = 8f; b.style.paddingRight = 8f; b.style.height = 26f;
            if (w > 0) b.style.width = w;
            parent.Add(b); return b;
        }
        static void SetActive(Button b, bool on, Color c)
        {
            b.style.backgroundColor = on ? new Color(c.r, c.g, c.b, 0.28f) : new Color(1, 1, 1, 0.06f);
            b.style.color = on ? Color.white : Ink;
        }
        void Row(VisualElement parent, string key, string label)
        {
            var r = Box(parent, true); r.style.justifyContent = Justify.SpaceBetween; r.style.marginBottom = 3f;
            Text(r, label, 14, Muted);
            var v = Text(r, "—", 14, Ink); v.style.unityTextAlign = TextAnchor.MiddleRight;
            rows[key] = v;
            if (key == "capsule") capsuleRow = r;
        }

        // ------------------------------------------------------------------ layout
        void BuildStatus()
        {
            var c = CardBox(root);
            c.style.position = Position.Absolute; c.style.left = 16f; c.style.top = 16f; c.style.width = 470f;
            title = Text(c, "Bovie Sim", 17, Ink, true); title.style.marginBottom = 8f;
            var act = Box(c, true); act.style.alignItems = Align.Center; act.style.marginBottom = 10f;
            actBanner = Text(act, "IDLE", 22, Muted, true); actBanner.style.width = 150f;
            var nums = Box(act, true); nums.style.flexGrow = 1f; nums.style.justifyContent = Justify.SpaceBetween;
            actZ = Num(nums, "Z"); actP = Num(nums, "P"); actV = Num(nums, "V");
            Row(c, "tool", "Instrument");
            Row(c, "under", "Under the tip");
            Row(c, "temp", "Hottest tissue");
            Row(c, "char", "Char / vaporised");
            Row(c, "blood", "Blood lost / pooled");
            Row(c, "ends", "Bleeding ends");
            Row(c, "seal", "Sealed / char plug / rebleeds");
            Row(c, "rim", "Skin edge thermal rim");
            Row(c, "capsule", "Lipoma capsule");
            footer = Text(c, "", 12, Muted); footer.style.marginTop = 6f;
        }
        static Label Num(VisualElement parent, string name)
        {
            var b = Box(parent); b.style.alignItems = Align.FlexEnd; b.style.minWidth = 86f;
            Text(b, name, 11, Muted, true);
            var v = Text(b, "—", 18, Ink, true);
            return v;
        }

        void BuildRightColumn()
        {
            var col = Box(root);
            col.style.position = Position.Absolute; col.style.right = 16f; col.style.top = 16f; col.style.bottom = 16f; col.style.width = 580f;
            col.style.justifyContent = Justify.SpaceBetween;
            var top = Box(col);
            // objectives
            var o = CardBox(top); o.style.marginBottom = 10f;
            Heading(o, "Objectives");
            objList = Box(o);
            // section
            sectionCard = CardBox(top);
            var head = Box(sectionCard, true); head.style.justifyContent = Justify.SpaceBetween; head.style.alignItems = Align.Center; head.style.marginBottom = 6f;
            sectionTitle = Text(head, "SECTION", 12, Muted, true); sectionTitle.style.letterSpacing = 1.2f;
            var zoom = Box(head, true); zoom.pickingMode = PickingMode.Position;
            Btn(zoom, "−", () => Zoom(-1), 30); Btn(zoom, "+", () => Zoom(1), 30);
            var orient = Btn(zoom, "Across view", () => { if (ToggleOrientation != null) ToggleOrientation(); });
            orientBtnLabel = null; orient.name = "orient";
            var modes = Box(sectionCard, true); modes.pickingMode = PickingMode.Position; modes.style.marginBottom = 6f; modes.style.flexWrap = Wrap.Wrap;
            for (int m = 0; m < ModeNames.Length; m++) { int mm = m; modeButtons.Add(Btn(modes, ModeNames[m], () => physics.sliceMode = mm)); }
            sectionImgBox = Box(sectionCard);
            sectionImgBox.style.width = 556f; sectionImgBox.style.height = 556f * PhysicsPipeline.SliceH / PhysicsPipeline.SliceW;
            sectionImg = new Image { image = physics.sliceTex, scaleMode = ScaleMode.StretchToFill };
            sectionImg.pickingMode = PickingMode.Ignore;
            sectionImg.style.position = Position.Absolute; sectionImg.style.left = 0f; sectionImg.style.top = 0f; sectionImg.style.right = 0f; sectionImg.style.bottom = 0f;
            sectionImgBox.Add(sectionImg);
            overlay = Box(sectionImgBox); overlay.style.position = Position.Absolute; overlay.style.left = 0f; overlay.style.top = 0f; overlay.style.right = 0f; overlay.style.bottom = 0f;
            var tog = Box(sectionCard, true); tog.pickingMode = PickingMode.Position; tog.style.marginTop = 6f;
            isoBtn = Btn(tog, "Isotherms 45/60/100 °C", () => physics.sliceFlags ^= 1);
            dmgBtn = Btn(tog, "Damage line Ω = 1", () => physics.sliceFlags ^= 2);
            legendBox = Box(sectionCard); legendBox.style.marginTop = 8f;
            // FT10
            BuildFT10(col);
        }

        void Zoom(int d) { zoomIndex = Mathf.Clamp(zoomIndex + d, 0, Zooms.Length - 1); physics.sliceWidthMm = Zooms[zoomIndex][0]; physics.sliceDepthMm = Zooms[zoomIndex][1]; lastLabelKey = float.NaN; }
        public void ZoomStep(int d) { Zoom(d); }

        // FT10-style touchscreen: near-black glass, a yellow CUT tile and a blue COAG tile with large power digits,
        // up/down arrows beside the digits, and the mode choices as a segmented row under each tile.
        static readonly Color Glass = new Color(0.035f, 0.04f, 0.045f, 0.97f);
        static readonly Color CutTile = new Color(0.80f, 0.64f, 0.05f), CoagTile = new Color(0.14f, 0.36f, 0.78f);
        static readonly ModeId[] CutModes = { ModeId.Low, ModeId.Pure, ModeId.Blend, ModeId.Valleylab };
        static readonly ModeId[] CoagModes = { ModeId.Fulgurate, ModeId.Spray };
        readonly List<KeyValuePair<ModeId, Button>> modeSegs = new List<KeyValuePair<ModeId, Button>>();
        VisualElement cutTile, coagTile; Label cutActive, coagActive;

        void BuildFT10(VisualElement parent)
        {
            var c = CardBox(parent);
            c.style.backgroundColor = Glass;
            var head = Box(c, true); head.style.justifyContent = Justify.SpaceBetween; head.style.alignItems = Align.Center; head.style.marginBottom = 8f;
            Text(head, "MONOPOLAR 1", 12, Muted, true).style.letterSpacing = 1.2f;
            remLbl = Text(head, "REM ●", 13, OkC, true);
            var blocks = Box(c, true);
            cutTile = Tile(blocks, true, out cutW, out cutActive, out cutLamp);
            coagTile = Tile(blocks, false, out coagW, out coagActive, out coagLamp);
            var segs = Box(c, true); segs.style.marginTop = 6f;
            var cs = Box(segs, true); cs.style.flexGrow = 1f; cs.style.marginRight = 8f; cs.pickingMode = PickingMode.Position;
            foreach (var m in CutModes) Seg(cs, m, true);
            var gs = Box(segs, true); gs.style.flexGrow = 1f; gs.pickingMode = PickingMode.Position;
            foreach (var m in CoagModes) Seg(gs, m, false);
            cutMode = null; coagMode = null;
            var foot = Box(c, true); foot.style.justifyContent = Justify.SpaceBetween; foot.style.alignItems = Align.Center; foot.style.marginTop = 10f;
            zLine = Text(foot, "Z — Ω", 14, Muted);
            var pedals = Box(foot, true); pedals.pickingMode = PickingMode.Position;
            Pedal(pedals, "CUT pedal", CutC, v => pedalCut = v);
            Pedal(pedals, "COAG pedal", CoagC, v => pedalCoag = v);
        }

        VisualElement Tile(VisualElement parent, bool cut, out Label watts, out Label active, out VisualElement lamp)
        {
            var t = Box(parent, true); t.pickingMode = PickingMode.Position;
            t.style.flexGrow = 1f; t.style.marginRight = cut ? 8f : 0f; t.style.height = 92f;
            t.style.backgroundColor = cut ? CutTile : CoagTile; Radius(t, 6); Pad(t, 12, 8);
            t.style.alignItems = Align.Center;
            var left = Box(t); left.style.flexGrow = 1f;
            var top = Box(left, true); top.style.alignItems = Align.Center;
            Text(top, cut ? "CUT" : "COAG", 15, cut ? new Color(0.12f, 0.10f, 0.02f) : Color.white, true).style.marginRight = 8f;
            lamp = Box(top); lamp.style.width = 10f; lamp.style.height = 10f; Radius(lamp, 5);
            active = Text(top, "", 11, Color.white, true); active.style.marginLeft = 6f;
            var w = Box(left, true); w.style.alignItems = Align.FlexEnd;
            watts = Text(w, "30", 46, cut ? new Color(0.10f, 0.08f, 0.01f) : Color.white, true);
            var unit = Text(w, "W", 16, cut ? new Color(0.12f, 0.10f, 0.02f) : Color.white, true); unit.style.marginLeft = 4f; unit.style.marginBottom = 8f;
            var arrows = Box(t); arrows.pickingMode = PickingMode.Position;
            var up = Btn(arrows, "▲", () => gen.Adjust(cut, ShiftHeld() ? 1 : 5), 44f); up.style.height = 36f; up.style.marginRight = 0f; up.style.marginBottom = 4f;
            var dn = Btn(arrows, "▼", () => gen.Adjust(cut, ShiftHeld() ? -1 : -5), 44f); dn.style.height = 36f; dn.style.marginRight = 0f;
            foreach (var b in new[] { up, dn }) { b.style.backgroundColor = new Color(0, 0, 0, 0.28f); b.style.color = Color.white; b.style.fontSize = 16f; }
            return t;
        }

        void Seg(VisualElement parent, ModeId m, bool cut)
        {
            var b = Btn(parent, FT10.Mode(m).label.ToUpperInvariant(), () =>
            {
                if (cut) gen.cutMode = m; else gen.coagMode = m;
                gen.Adjust(cut, 0);
            });
            b.style.flexGrow = 1f; b.style.marginRight = 2f; b.style.fontSize = 12f; b.style.height = 28f;
            modeSegs.Add(new KeyValuePair<ModeId, Button>(m, b));
        }

        static bool ShiftHeld() { return Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift); }

        static void Pedal(VisualElement parent, string label, Color c, Action<bool> set)
        {
            var p = new Label(label); p.pickingMode = PickingMode.Position;
            p.style.fontSize = 13f; p.style.unityFontStyleAndWeight = FontStyle.Bold; p.style.color = Color.white;
            p.style.backgroundColor = new Color(c.r * 0.6f, c.g * 0.6f, c.b * 0.6f, 1); Radius(p, 5); Pad(p, 14, 8); p.style.marginLeft = 6f;
            p.style.unityTextAlign = TextAnchor.MiddleCenter;
            p.RegisterCallback<PointerDownEvent>(e => { set(true); p.CapturePointer(e.pointerId); p.style.backgroundColor = c; });
            p.RegisterCallback<PointerUpEvent>(e => { set(false); p.ReleasePointer(e.pointerId); p.style.backgroundColor = new Color(c.r * 0.6f, c.g * 0.6f, c.b * 0.6f, 1); });
            p.RegisterCallback<PointerCaptureOutEvent>(e => { set(false); p.style.backgroundColor = new Color(c.r * 0.6f, c.g * 0.6f, c.b * 0.6f, 1); });
            parent.Add(p);
        }

        void BuildFeed()
        {
            feed = Box(root);
            feed.style.position = Position.Absolute; feed.style.left = 16f; feed.style.bottom = 40f; feed.style.width = 560f;
            feed.style.justifyContent = Justify.FlexEnd;
            var hint = Text(root, "F1 brief · F2 controls · F3 timing · Tab instrument · Z/X pedals", 12, Muted);
            hint.style.position = Position.Absolute; hint.style.left = 18f; hint.style.bottom = 14f;
        }

        void BuildBrief()
        {
            brief = CardBox(root);
            brief.style.position = Position.Absolute; brief.style.left = 16f; brief.style.top = 372f; brief.style.width = 470f;
            brief.style.maxHeight = 330f; brief.style.overflow = Overflow.Hidden;
        }

        public void SetScenario(Scenario sc)
        {
            objectives = new Objectives(sc.education);
            objList.Clear(); objRows.Clear();
            foreach (var s in objectives.items)
            {
                var r = Box(objList, true); r.style.alignItems = Align.Center; r.style.marginBottom = 4f;
                var chip = Text(r, "…", 11, Color.white, true); chip.style.width = 44f; chip.style.unityTextAlign = TextAnchor.MiddleCenter;
                Radius(chip, 3); chip.style.paddingTop = 2f; chip.style.paddingBottom = 2f; chip.style.marginRight = 8f;
                var l = Text(r, s.criterion.label, 14, Ink); l.style.flexGrow = 1f; l.style.whiteSpace = WhiteSpace.Normal; l.style.flexShrink = 1f;
                var v = Text(r, "—", 14, Muted); v.style.unityTextAlign = TextAnchor.MiddleRight; v.style.minWidth = 90f;
                objRows.Add(new VisualElement[] { chip, l, v });
            }
            if (objectives.items.Count == 0) Text(objList, "Free exploration: no scored objectives.", 14, Muted);
            brief.Clear();
            Heading(brief, "Scenario brief · F1");
            var name = Text(brief, sc.name, 16, Ink, true); name.style.marginBottom = 4f; name.style.whiteSpace = WhiteSpace.Normal;
            Para(brief, sc.description, Muted);
            var ed = sc.education;
            if (ed != null)
            {
                if (!string.IsNullOrEmpty(ed.goal)) { Sub(brief, "Goal"); Para(brief, ed.goal, Ink); }
                if (ed.failurePoints != null && ed.failurePoints.Count > 0) { Sub(brief, "Failure points"); foreach (var f in ed.failurePoints) Para(brief, "• " + f.consequence, Ink); }
                if (ed.decisionPoints != null && ed.decisionPoints.Count > 0) { Sub(brief, "Decisions"); foreach (var d in ed.decisionPoints) Para(brief, "• " + d.prompt, Ink); }
                if (ed.ambiguity != null && ed.ambiguity.Count > 0) { Sub(brief, "Not known in advance"); foreach (var a in ed.ambiguity) Para(brief, "• " + a.description, Ink); }
            }
        }
        static void Sub(VisualElement p, string t) { var l = Text(p, t.ToUpperInvariant(), 11, Accent, true); l.style.marginTop = 8f; l.style.marginBottom = 2f; l.style.letterSpacing = 1f; }
        static void Para(VisualElement p, string t, Color c) { var l = Text(p, t, 14, c); l.style.whiteSpace = WhiteSpace.Normal; l.style.marginBottom = 2f; }

        void BuildHelp()
        {
            help = CardBox(root);
            help.style.position = Position.Absolute; help.style.left = 500f; help.style.top = 120f; help.style.width = 780f;
            Heading(help, "Controls · F2");
            string[,] keys = {
                { "Z / X", "Footswitch cut / coag (or the on-screen pedals)" },
                { "Left mouse", "Pencil: hand-switch cut (Ctrl = coag). Forceps: grasp. Suction: on. Hook: catch and drag" },
                { "Space", "Lift the closed forceps (traction, delivering a freed lipoma)" },
                { "Tab or Q", "Next instrument: pencil, Adson forceps, Frazier suction, skin hook" },
                { "1-4 / 5-6", "Cut mode Pure, Blend, Low, Valleylab / coag Fulgurate, Spray" },
                { "[ ]  ; '", "Cut / coag watts −5 +5 (Shift: 1 W)" },
                { "B N O · R · T", "Blade, needle, ball · roll the blade 45° · toothed / smooth forceps" },
                { "C · J · H", "Section display · across the view or across the blade · hide section" },
                { "- / =", "Section zoom out / in (up to the full 35 mm depth)" },
                { "Right drag · Middle drag · Wheel", "Orbit · pan · zoom (Alt+left orbits, Shift+left pans)" },
                { "F1 · F2 · F3", "Scenario brief · this list · timing readout" },
                { "F5 · F6 · F7 · F8", "Restart · next scenario · depth of field on/off · anti-aliasing mode" },
                { "F11 · Esc", "Fullscreen · quit (built app)" },
                { "Pen", "Pressure sets depth, tilt sets angle; Y flips tilt direction" },
            };
            for (int i = 0; i < keys.GetLength(0); i++)
            {
                var r = Box(help, true); r.style.marginBottom = 3f;
                var k = Text(r, keys[i, 0], 14, Accent, true); k.style.width = 230f;
                var d = Text(r, keys[i, 1], 14, Ink); d.style.flexShrink = 1f; d.style.whiteSpace = WhiteSpace.Normal;
            }
            help.style.display = DisplayStyle.None;
        }

        void BuildPerf()
        {
            var c = CardBox(root); c.pickingMode = PickingMode.Ignore;
            c.style.position = Position.Absolute; c.style.left = 500f; c.style.top = 16f; Pad(c, 10, 6);
            perf = Text(c, "", 13, Ink);
            perf.parent.style.display = DisplayStyle.None;
        }

        // ------------------------------------------------------------------ legend textures (ports of the Slice kernel ramps)
        static Color TempRamp(float t)
        {
            float x = Mathf.Clamp01((t - 37f) / 263f);
            Color c1 = Color.Lerp(new Color(0.1f, 0.1f, 0.5f), new Color(0f, 0.8f, 0.9f), SS(0f, 0.25f, x));
            Color c2 = Color.Lerp(c1, new Color(0.2f, 0.9f, 0.2f), SS(0.2f, 0.45f, x));
            Color c3 = Color.Lerp(c2, new Color(1f, 0.9f, 0.1f), SS(0.4f, 0.65f, x));
            Color c4 = Color.Lerp(c3, new Color(1f, 0.2f, 0.05f), SS(0.6f, 0.85f, x));
            return Color.Lerp(c4, Color.white, SS(0.85f, 1f, x));
        }
        static Color DamageRamp(float x)
        {
            Color c = Color.Lerp(new Color(0.08f, 0.12f, 0.35f), new Color(0.10f, 0.45f, 0.60f), SS(0f, 0.45f, x));
            c = Color.Lerp(c, new Color(0.95f, 0.70f, 0.15f), SS(0.45f, 0.57f, x));
            c = Color.Lerp(c, new Color(0.90f, 0.15f, 0.10f), SS(0.57f, 0.667f, x));
            return Color.Lerp(c, new Color(1f, 0.95f, 0.90f), SS(0.667f, 1f, x));
        }
        static float SS(float a, float b, float x) { float t = Mathf.Clamp01((x - a) / (b - a)); return t * t * (3 - 2 * t); }

        void BuildTextures()
        {
            tempTex = Ramp(x => TempRamp(37f + 263f * x)); damageTex = Ramp(DamageRamp);
            waterTex = Ramp(x => Color.Lerp(new Color(0.5f, 0.3f, 0.15f), new Color(0.2f, 0.5f, 1f), x));
        }
        static Texture2D Ramp(Func<float, Color> f)
        {
            var t = new Texture2D(256, 1, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, hideFlags = HideFlags.HideAndDontSave };
            for (int i = 0; i < 256; i++) t.SetPixel(i, 0, f(i / 255f));
            t.Apply(); return t;
        }

        void BuildLegend(int mode)
        {
            legendBox.Clear();
            if (mode == 0)
            {
                var r = Box(legendBox, true); r.style.flexWrap = Wrap.Wrap;
                Swatch(r, "Epidermis", TissuePalette.Skin(TissuePalette.BakedTone) * TissuePalette.Prep(TissuePalette.BakedPrep)); Swatch(r, "Dermis", TissuePalette.Dermis);
                Swatch(r, "Fat", TissuePalette.Fat); Swatch(r, "Fascia", TissuePalette.Fascia);
                Swatch(r, "Muscle", TissuePalette.Muscle); Swatch(r, "Artery", TissuePalette.Artery);
                Swatch(r, "Vein", TissuePalette.Vein); Swatch(r, "Lipoma", TissuePalette.Lipoma);
                Swatch(r, "Coag dermis", TissuePalette.CoagDermis); Swatch(r, "Coag muscle", TissuePalette.CoagMuscle);
                Swatch(r, "Desiccated", TissuePalette.Desiccated); Swatch(r, "Char", TissuePalette.Char);
                Swatch(r, "Sealed", new Color(0.4f, 0.8f, 0.45f));
                LineKey(legendBox);
            }
            else if (mode == 1 || mode == 4 || mode == 5)
            {
                if (mode == 1) Bar(legendBox, tempTex, new[] { "37", "100", "170", "235", "300 °C" });
                else if (mode == 4) Bar(legendBox, tempTex, new[] { "0", "25", "50", "75", "100 % of electrode voltage" });
                else Bar(legendBox, tempTex, new[] { "1e3", "3e4", "1e6", "3e7", "1e9 W/m³ (log scale)" });
                if (mode == 1) LineKey(legendBox);
            }
            else if (mode == 2) { Bar(legendBox, damageTex, new[] { "Ω 0.01", "0.1", "0.53 blanch", "1 irreversible", "10" }, new[] { 0f, 0.333f, 0.57f, 0.667f, 1f }); LineKey(legendBox); }
            else Bar(legendBox, waterTex, new[] { "dry", "", "half", "", "normal water" });
        }
        static void Swatch(VisualElement p, string name, Color c)
        {
            var r = Box(p, true); r.style.alignItems = Align.Center; r.style.marginRight = 10f; r.style.marginBottom = 3f;
            var s = Box(r); s.style.width = 12f; s.style.height = 12f; s.style.backgroundColor = c; Radius(s, 2); s.style.marginRight = 4f;
            Text(r, name, 12, Muted);
        }
        static void LineKey(VisualElement p)
        {
            var r = Box(p, true); r.style.flexWrap = Wrap.Wrap; r.style.marginTop = 4f;
            Swatch(r, "45 °C", new Color(0.30f, 0.90f, 1.00f)); Swatch(r, "60 °C", new Color(1.00f, 0.85f, 0.20f));
            Swatch(r, "100 °C", new Color(1.00f, 0.30f, 0.20f)); Swatch(r, "Ω = 1 (dashed)", Color.white);
        }
        static void Bar(VisualElement p, Texture2D tex, string[] ticks, float[] at = null)
        {
            var img = new Image { image = tex, scaleMode = ScaleMode.StretchToFill }; img.pickingMode = PickingMode.Ignore;
            img.style.height = 12f; img.style.width = 556f; Radius(img, 2);
            p.Add(img);
            var r = Box(p); r.style.height = 16f; r.style.width = 556f;
            for (int i = 0; i < ticks.Length; i++)
            {
                float f = at != null ? at[i] : i / (float)(ticks.Length - 1);
                var l = Text(r, ticks[i], 12, Muted); l.style.position = Position.Absolute; l.style.top = 1f;
                if (i == ticks.Length - 1) l.style.right = 0f; else l.style.left = Mathf.Max(0, f * 556 - (i == 0 ? 0 : 14));
            }
        }

        // ------------------------------------------------------------------ per-frame
        public void Push(SimEvent e)
        {
            events.Add(e);
            while (events.Count > 4) events.RemoveAt(0);
            feed.Clear();
            foreach (var ev in events)
            {
                var c = CardBox(feed); c.pickingMode = PickingMode.Ignore; c.style.marginTop = 6f; Pad(c, 10, 6);
                var sev = ev.severity == Severity.Bad ? BadC : ev.severity == Severity.Warn ? WarnC : Accent;
                c.style.borderLeftWidth = 4f; c.style.borderLeftColor = sev;
                var h = Box(c, true);
                Text(h, ev.t.ToString("0.0") + " s", 12, Muted).style.marginRight = 8f;
                Text(h, ev.type, 13, sev, true).style.marginRight = 8f;
                var d = Text(h, ev.detail, 13, Ink); d.style.flexShrink = 1f; d.style.whiteSpace = WhiteSpace.Normal;
                if (!string.IsNullOrEmpty(ev.teaching)) { var t = Text(c, ev.teaching, 13, Muted); t.style.whiteSpace = WhiteSpace.Normal; t.style.marginTop = 2f; }
            }
        }

        public bool PointerOverUi(Vector2 mouse)
        {
            var panel = root.panel; if (panel == null) return false;
            var pos = RuntimePanelUtils.ScreenToPanel(panel, new Vector2(mouse.x, Screen.height - mouse.y));
            var picked = panel.Pick(pos);
            return picked != null && picked != root;
        }

        public void Refresh(HudData d, Func<string, float> metric)
        {
            title.text = "Bovie Sim · " + d.scenarioName;
            // activation
            if (d.activation == Activation.None) { actBanner.text = "IDLE"; actBanner.style.color = Muted; }
            else if (d.remFault) { actBanner.text = "REM FAULT"; actBanner.style.color = BadC; }
            else { actBanner.text = (d.activation == Activation.Cut ? "CUT " : "COAG ") + d.activeWatts + " W"; actBanner.style.color = d.activation == Activation.Cut ? CutC : CoagC; }
            bool on = d.activation != Activation.None;
            actZ.text = on ? (d.zOhm < 1e8f ? d.zOhm.ToString("0") + " Ω" : "open") : "—";
            actP.text = on ? d.powerW.ToString("0.0") + " W" : "—";
            actV.text = on ? d.vRms.ToString("0") + " V" : "—";
            actP.style.color = on && d.powerW < 0.6f * d.activeWatts && d.contact > 0 ? WarnC : Ink;
            rows["tool"].text = d.toolText;
            rows["under"].text = d.underTip;
            rows["temp"].text = d.maxT.ToString("0") + " °C"; rows["temp"].style.color = d.maxT >= 100 ? BadC : d.maxT >= 60 ? WarnC : Ink;
            rows["char"].text = d.charMm3.ToString("0.0") + " mm³ / " + d.vaporised.ToString("0") + " vx"; rows["char"].style.color = d.carbon > 0 ? BadC : Ink;
            rows["blood"].text = d.bloodLost.ToString("0.00") + " ml / " + d.pooled.ToString("0.00") + " ml";
            rows["ends"].text = d.exposedArt + " arterial · " + d.exposedVen + " venous"; rows["ends"].style.color = d.exposedArt > 0 ? BadC : d.exposedVen > 0 ? WarnC : Ink;
            rows["seal"].text = d.sealedVx + " · " + d.charPlug + " · " + d.rebleeds; rows["seal"].style.color = d.charPlug > 0 ? WarnC : Ink;
            rows["rim"].text = d.rimMm > 0 ? d.rimMm.ToString("0.00") + " mm" : "no incision edge yet"; rows["rim"].style.color = d.rimMm >= 0.5f ? BadC : d.rimMm >= 0.4f ? WarnC : Ink;
            capsuleRow.style.display = string.IsNullOrEmpty(d.capsuleText) ? DisplayStyle.None : DisplayStyle.Flex;
            rows["capsule"].text = d.capsuleText;
            footer.text = d.volumeText + " · " + (d.tris / 1000) + "k triangles" + (d.capped ? " (CAPPED)" : "") + " · " + d.fps.ToString("0") + " fps · sim " + d.simTime.ToString("0.0") + " s";
            // objectives
            if (objectives != null)
            {
                objectives.Update(metric);
                for (int i = 0; i < objectives.items.Count; i++)
                {
                    var s = objectives.items[i]; var r = objRows[i];
                    var chip = (Label)r[0]; var v = (Label)r[2];
                    Color c; string t;
                    switch (s.state)
                    {
                        case ObjectiveState.Ok: c = OkC; t = "OK"; break;
                        case ObjectiveState.Met: c = OkC; t = "DONE"; break;
                        case ObjectiveState.Warning: c = WarnC; t = "NEAR"; break;
                        case ObjectiveState.Failed: c = BadC; t = "FAIL"; break;
                        default: c = new Color(0.35f, 0.40f, 0.43f); t = "…"; break;
                    }
                    chip.text = t; chip.style.backgroundColor = new Color(c.r * 0.7f, c.g * 0.7f, c.b * 0.7f, 1);
                    v.text = s.valueText; v.style.color = s.state == ObjectiveState.Failed ? BadC : s.state == ObjectiveState.Warning ? WarnC : Muted;
                }
            }
            RefreshSection(d);
            RefreshFT10(d);
            brief.style.display = showBrief ? DisplayStyle.Flex : DisplayStyle.None;
            help.style.display = showHelp ? DisplayStyle.Flex : DisplayStyle.None;
            perf.parent.style.display = showPerf ? DisplayStyle.Flex : DisplayStyle.None;
            if (showPerf)
                perf.text = "frame " + d.frameMs.ToString("0.0") + " ms (" + d.fps.ToString("0") + " fps) · CPU " + d.cpuMs.ToString("0.0") + " ms · GPU " + (d.gpuMs > 0 ? d.gpuMs.ToString("0.0") + " ms" : "n/a") +
                            " · instrument lag 0 frames · readout age " + d.stateAge + " · surface heights age " + d.heightsAge + " · tissue mesh age " + d.surfaceAge + " frames";
        }

        void RefreshSection(HudData d)
        {
            sectionCard.style.display = physics.sliceEnabled ? DisplayStyle.Flex : DisplayStyle.None;
            if (!physics.sliceEnabled) return;
            int mode = physics.sliceMode;
            for (int m = 0; m < modeButtons.Count; m++) SetActive(modeButtons[m], m == mode, Accent);
            SetActive(isoBtn, (physics.sliceFlags & 1) != 0, Accent); SetActive(dmgBtn, (physics.sliceFlags & 2) != 0, Accent);
            isoBtn.style.display = mode <= 2 ? DisplayStyle.Flex : DisplayStyle.None; dmgBtn.style.display = mode <= 2 ? DisplayStyle.Flex : DisplayStyle.None;
            var ob = sectionCard.Q<Button>("orient"); if (ob != null) ob.text = d.slicePerp ? "Across blade" : "Across view";
            sectionTitle.text = "SECTION · " + ModeNames[mode].ToUpperInvariant() + " · " + physics.sliceWidthMm.ToString("0") + " × " + physics.sliceDepthMm.ToString("0") + " MM";
            if (mode != lastLegendMode) { BuildLegend(mode); lastLegendMode = mode; }
            // depth scale and layer labels: rebuild when the view moved noticeably
            float key = Mathf.Round(d.sliceTopY * 4) + 1000 * physics.sliceDepthMm + 0.01f * d.layers.Count;
            if (key == lastLabelKey) return;
            lastLabelKey = key;
            overlay.Clear();
            float boxW = 556, boxH = 556f * PhysicsPipeline.SliceH / PhysicsPipeline.SliceW;
            float depthMm = physics.sliceDepthMm, pxPerMm = boxH / depthMm;
            // depth ticks (mm below the original skin surface, y = 0)
            float step = depthMm <= 12 ? 2 : depthMm <= 22 ? 5 : 5;
            for (float dep = 0; dep <= d.sliceTopY * -1 + depthMm + 0.01f; dep += step)
            {
                float y = (d.sliceTopY + dep) * pxPerMm; // sliceTopY is the view top in mm (y up); depth dep is at y = -dep
                if (y < 0 || y > boxH - 10) continue;
                var tick = Box(overlay); tick.style.position = Position.Absolute; tick.style.left = 0f; tick.style.top = y; tick.style.width = 8f; tick.style.height = 1f; tick.style.backgroundColor = new Color(1, 1, 1, 0.7f);
                var l = Text(overlay, dep.ToString("0") + (dep == 0 ? " mm" : ""), 11, new Color(1, 1, 1, 0.8f)); l.style.position = Position.Absolute; l.style.left = 11f; l.style.top = y - 7;
            }
            // horizontal scale bar
            float bar = physics.sliceWidthMm >= 40 ? 10 : 5;
            var sb = Box(overlay); sb.style.position = Position.Absolute; sb.style.left = boxW - 16 - bar * boxW / physics.sliceWidthMm; sb.style.top = boxH - 14; sb.style.width = bar * boxW / physics.sliceWidthMm; sb.style.height = 3f; sb.style.backgroundColor = Color.white;
            var sbl = Text(overlay, bar.ToString("0") + " mm", 11, Color.white); sbl.style.position = Position.Absolute; sbl.style.right = 16f; sbl.style.top = boxH - 30;
            // layer labels at the section's centre column
            foreach (var kv in d.layers)
            {
                float y = (d.sliceTopY + kv.Value) * pxPerMm;
                if (y < 4 || y > boxH - 20) continue;
                var l = Text(overlay, kv.Key, 11, Color.white, true);
                l.style.position = Position.Absolute; l.style.right = 8f; l.style.top = y + 1;
                l.style.backgroundColor = new Color(0, 0, 0, 0.45f); Pad(l, 4, 1); Radius(l, 3);
            }
        }

        void RefreshFT10(HudData d)
        {
            cutW.text = gen.cutWatts.ToString(); coagW.text = gen.coagWatts.ToString();
            bool cutOn = gen.activation == Activation.Cut, coagOn = gen.activation == Activation.Coag;
            // the active output brightens, the other dims, as on the generator during activation
            cutTile.style.backgroundColor = cutOn ? new Color(1f, 0.82f, 0.15f) : coagOn ? CutTile * 0.55f : CutTile;
            coagTile.style.backgroundColor = coagOn ? new Color(0.25f, 0.52f, 1f) : cutOn ? CoagTile * 0.55f : CoagTile;
            cutLamp.style.backgroundColor = cutOn ? Color.white : new Color(0, 0, 0, 0.3f);
            coagLamp.style.backgroundColor = coagOn ? Color.white : new Color(0, 0, 0, 0.3f);
            cutActive.text = cutOn ? "ACTIVE" : ""; coagActive.text = coagOn ? "ACTIVE" : "";
            cutActive.style.color = new Color(0.1f, 0.08f, 0.01f);
            foreach (var kv in modeSegs)
            {
                bool isCut = Array.IndexOf(CutModes, kv.Key) >= 0;
                bool sel = isCut ? gen.cutMode == kv.Key : gen.coagMode == kv.Key;
                SetActive(kv.Value, sel, isCut ? CutC : CoagC);
            }
            remLbl.text = gen.remFault ? "REM FAULT" : "REM ●"; remLbl.style.color = gen.remFault ? BadC : OkC;
            zLine.text = gen.activation == Activation.None ? "Z — Ω" : "Z " + (d.zOhm < 1e8f ? d.zOhm.ToString("0") : "—") + " Ω · " + d.powerW.ToString("0") + " W delivered";
        }

        public void Dispose()
        {
            foreach (var t in new[] { tempTex, damageTex, waterTex }) if (t != null) UnityEngine.Object.Destroy(t);
            if (go != null) UnityEngine.Object.Destroy(go);
        }
    }
}
