using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace BovieSim
{
    /// <summary>Tip gauge, settings panel, frame-budget banner, the pause-and-replay overlay and the depth-peel ruler.</summary>
    public sealed partial class SimUI
    {
        // tip gauge
        VisualElement gauge, gContact, gPower; Label gTemp, gPow, gZ;
        public bool showGauge = true;
        // settings
        VisualElement settingsCard; Label hwLabel, levelNote; readonly List<KeyValuePair<DetailLevel, Button>> levelButtons = new List<KeyValuePair<DetailLevel, Button>>();
        readonly Dictionary<string, Button> toggles = new Dictionary<string, Button>();
        public bool showSettings;
        public event Action<DetailLevel> DetailChosen;
        public event Action<string> SettingToggled;
        // budget banner
        VisualElement banner; Label bannerText;
        public event Action BudgetAccept, BudgetDismiss;
        // replay
        VisualElement replay; Image replayImg; Label replayTitle, replayDetail, replayTeach; VisualElement replayBar;
        public event Action ReplayAgain, ReplayContinue;
        public bool ReplayVisible { get { return replay.style.display == DisplayStyle.Flex; } }
        // depth peel: a ruler through the layers that drags the clip plane of the 3D view
        VisualElement depthCard, rulerCol, rulerFill, rulerHandle, rulerTicks; Label depthRead, depthHint; readonly List<Button> peelButtons = new List<Button>(); Button sweepBtn;
        public event Action<int> PeelModeChosen; public event Action<float> PeelDepthChanged; public event Action PeelSweepToggled, PeelCycle;
        const float RulerH = 400f, RulerPad = 8f;
        bool rulerDrag; float rulerBedMm = 35f; string lastTickKey;
        static readonly string[] PeelNames = { "Off", "Block cut", "Dissect" };

        void BuildPanels() { BuildGauge(); BuildSettings(); BuildBanner(); BuildReplay(); BuildDepthCard(); }

        // ------------------------------------------------------------------ tip gauge
        void BuildGauge()
        {
            gauge = CardBox(root); gauge.pickingMode = PickingMode.Ignore;
            gauge.style.position = Position.Absolute; gauge.style.width = 196f; Pad(gauge, 10, 8);
            gTemp = Text(gauge, "Tip —", 15, Ink, true); gTemp.style.marginBottom = 4f;
            Text(gauge, "CONTACT", 10, Muted, true);
            gContact = Meter(gauge, OkC);
            gPow = Text(gauge, "P — W", 13, Ink); gPow.style.marginTop = 4f;
            gPower = Meter(gauge, CutC);
            gZ = Text(gauge, "Z — Ω", 12, Muted); gZ.style.marginTop = 4f;
            gauge.style.display = DisplayStyle.None;
        }
        static VisualElement Meter(VisualElement p, Color c)
        {
            var track = Box(p); track.style.height = 6f; track.style.width = 176f; track.style.backgroundColor = new Color(1, 1, 1, 0.1f); Radius(track, 3);
            var fill = Box(track); fill.style.height = 6f; fill.style.width = 0f; fill.style.backgroundColor = c; Radius(fill, 3);
            return fill;
        }

        /// <summary>Places the gauge beside the instrument tip (screen pixels, origin bottom-left).</summary>
        public void UpdateGauge(bool visible, Vector2 tipScreen, HudData d)
        {
            var panel = root.panel;
            if (!showGauge || !visible || panel == null) { gauge.style.display = DisplayStyle.None; return; }
            gauge.style.display = DisplayStyle.Flex;
            var pos = RuntimePanelUtils.ScreenToPanel(panel, new Vector2(tipScreen.x, Screen.height - tipScreen.y));
            float w = root.layout.width > 0 ? root.layout.width : 1920f, h = root.layout.height > 0 ? root.layout.height : 1080f;
            gauge.style.left = Mathf.Clamp(pos.x + 40f, 8f, w - 206f);
            gauge.style.top = Mathf.Clamp(pos.y - 110f, 8f, h - 140f);
            gTemp.text = "Tip " + d.maxT.ToString("0") + " °C";
            gTemp.style.color = d.maxT >= 100 ? BadC : d.maxT >= 60 ? WarnC : Ink;
            gContact.style.width = 176f * Mathf.Clamp01(d.contact / 40f);
            bool on = d.activation != Activation.None;
            float frac = on && d.activeWatts > 0 ? Mathf.Clamp01(d.powerW / d.activeWatts) : 0;
            gPower.style.width = 176f * frac;
            gPower.style.backgroundColor = d.activation == Activation.Coag ? CoagC : CutC;
            gPow.text = on ? "P " + d.powerW.ToString("0") + " of " + d.activeWatts + " W" : "Idle";
            gPow.style.color = on && frac < 0.6f && d.contact > 0 ? WarnC : Ink;
            gZ.text = on ? "Z " + (d.zOhm < 1e8f ? d.zOhm.ToString("0") + " Ω" : "open circuit") : "";
        }

        // ------------------------------------------------------------------ settings
        void BuildSettings()
        {
            settingsCard = CardBox(root);
            settingsCard.style.position = Position.Absolute; settingsCard.style.left = 520f; settingsCard.style.top = 140f; settingsCard.style.width = 700f;
            Heading(settingsCard, "Settings · F9");
            hwLabel = Text(settingsCard, "", 14, Ink); hwLabel.style.whiteSpace = WhiteSpace.Normal; hwLabel.style.marginBottom = 8f;
            Sub(settingsCard, "Detail level (voxel size, region solved, textures, shadows)");
            var row = Box(settingsCard, true); row.pickingMode = PickingMode.Position; row.style.marginBottom = 4f;
            foreach (DetailLevel l in new[] { DetailLevel.Auto, DetailLevel.Low, DetailLevel.Medium, DetailLevel.High, DetailLevel.Ultra })
            {
                var lv = l;
                var b = Btn(row, l == DetailLevel.Auto ? "Auto" : l + " " + DetailSettings.For(l).voxelMm.ToString("0.00") + " mm", () => { if (DetailChosen != null) DetailChosen(lv); });
                b.style.height = 30f;
                levelButtons.Add(new KeyValuePair<DetailLevel, Button>(l, b));
            }
            levelNote = Text(settingsCard, "", 13, Muted); levelNote.style.whiteSpace = WhiteSpace.Normal; levelNote.style.marginBottom = 8f;
            Sub(settingsCard, "Display and teaching");
            var t = Box(settingsCard, true); t.pickingMode = PickingMode.Position; t.style.flexWrap = Wrap.Wrap;
            foreach (var key in new[] { "teachingMoments", "tipGauge", "depthOfField", "antialiasing", "invertTiltY", "showTiming" })
            {
                var k = key;
                var b = Btn(t, key, () => { if (SettingToggled != null) SettingToggled(k); });
                b.style.marginBottom = 4f; b.style.height = 30f;
                toggles[key] = b;
            }
            settingsCard.style.display = DisplayStyle.None;
        }

        public void RefreshSettings(UserSettings s, DetailLevel running, string reason, DetailLevel chosen)
        {
            settingsCard.style.display = showSettings ? DisplayStyle.Flex : DisplayStyle.None;
            if (!showSettings) return;
            var cur = DetailSettings.For(running);
            hwLabel.text = HardwareProfile.Gpu + " · " + (HardwareProfile.VramMb / 1024f).ToString("0.#") + " GB video memory · " + SystemInfo.graphicsDeviceType + "\n" +
                           "Running at " + running + " (" + cur.voxelMm.ToString("0.00") + " mm voxels, target " + (cur.frameBudgetMs > 20 ? "30" : "60") + " fps): " + reason;
            foreach (var kv in levelButtons) SetActive(kv.Value, kv.Key == chosen, Accent);
            levelNote.text = chosen != DetailLevel.Auto && chosen != running || chosen == DetailLevel.Auto && reason == "chosen in Settings"
                ? "Restart the scenario (F5) to apply the new detail level."
                : "Auto picks the level from the graphics card; finer voxels show thinner thermal margins but cost GPU time (Ultra aims for 30 fps).";
            SetToggle("teachingMoments", "Teaching moments: " + OnOff(s.teachingMoments), s.teachingMoments);
            SetToggle("tipGauge", "Tip gauge: " + OnOff(s.tipGauge), s.tipGauge);
            SetToggle("depthOfField", "Depth of field: " + OnOff(s.depthOfField), s.depthOfField);
            string[] aa = { "SMAA", "TAA", "FXAA", "off" };
            SetToggle("antialiasing", "Anti-aliasing: " + aa[Mathf.Clamp(s.antialiasing, 0, 3)], true);
            SetToggle("invertTiltY", "Pen tilt: " + (s.invertTiltY ? "flipped" : "normal"), s.invertTiltY);
            SetToggle("showTiming", "Timing readout: " + OnOff(s.showTiming), s.showTiming);
        }
        static string OnOff(bool b) { return b ? "on" : "off"; }
        void SetToggle(string k, string text, bool on) { Button b; if (toggles.TryGetValue(k, out b)) { b.text = text; SetActive(b, on, Accent); } }

        // ------------------------------------------------------------------ frame-budget banner
        void BuildBanner()
        {
            banner = CardBox(root);
            banner.style.position = Position.Absolute; banner.style.left = 520f; banner.style.bottom = 16f; banner.style.width = 700f;
            banner.style.flexDirection = FlexDirection.Row; banner.style.alignItems = Align.Center;
            banner.style.borderLeftWidth = 4f; banner.style.borderLeftColor = WarnC;
            bannerText = Text(banner, "", 14, Ink); bannerText.style.flexGrow = 1f; bannerText.style.flexShrink = 1f; bannerText.style.whiteSpace = WhiteSpace.Normal;
            var b = Btn(banner, "Use lower detail", () => { if (BudgetAccept != null) BudgetAccept(); }); b.style.marginLeft = 8f; b.style.height = 30f;
            var k = Btn(banner, "Keep", () => { if (BudgetDismiss != null) BudgetDismiss(); }); k.style.height = 30f;
            banner.style.display = DisplayStyle.None;
        }
        public void ShowBudgetWarning(string text) { bannerText.text = text; banner.style.display = string.IsNullOrEmpty(text) ? DisplayStyle.None : DisplayStyle.Flex; }

        // ------------------------------------------------------------------ pause and replay
        void BuildReplay()
        {
            replay = Box(root); replay.pickingMode = PickingMode.Position;
            replay.style.position = Position.Absolute; replay.style.left = 0f; replay.style.top = 0f; replay.style.right = 0f; replay.style.bottom = 0f;
            replay.style.backgroundColor = new Color(0, 0, 0, 0.78f);
            replay.style.alignItems = Align.Center; replay.style.justifyContent = Justify.Center;
            var c = CardBox(replay); c.style.width = 1180f;
            var head = Box(c, true); head.style.justifyContent = Justify.SpaceBetween; head.style.alignItems = Align.Center; head.style.marginBottom = 8f;
            replayTitle = Text(head, "", 20, BadC, true);
            Text(head, "PAUSED · REPLAY OF THE LAST SECONDS", 12, Muted, true).style.letterSpacing = 1.2f;
            var imgBox = Box(c); imgBox.style.width = 1156f; imgBox.style.height = 650f; imgBox.style.backgroundColor = Color.black; Radius(imgBox, 4);
            replayImg = new Image { scaleMode = ScaleMode.ScaleToFit }; replayImg.pickingMode = PickingMode.Ignore;
            replayImg.style.position = Position.Absolute; replayImg.style.left = 0f; replayImg.style.top = 0f; replayImg.style.right = 0f; replayImg.style.bottom = 0f;
            imgBox.Add(replayImg);
            var track = Box(c); track.style.height = 4f; track.style.marginTop = 6f; track.style.backgroundColor = new Color(1, 1, 1, 0.1f);
            replayBar = Box(track); replayBar.style.height = 4f; replayBar.style.width = 0f; replayBar.style.backgroundColor = Accent;
            replayDetail = Text(c, "", 15, Ink); replayDetail.style.marginTop = 8f; replayDetail.style.whiteSpace = WhiteSpace.Normal;
            replayTeach = Text(c, "", 15, Ink, true); replayTeach.style.marginTop = 4f; replayTeach.style.whiteSpace = WhiteSpace.Normal;
            var btns = Box(c, true); btns.pickingMode = PickingMode.Position; btns.style.marginTop = 10f; btns.style.justifyContent = Justify.FlexEnd;
            var again = Btn(btns, "Replay again", () => { if (ReplayAgain != null) ReplayAgain(); }); again.style.height = 34f;
            var go = Btn(btns, "Continue (Enter)", () => { if (ReplayContinue != null) ReplayContinue(); }); go.style.height = 34f;
            SetActive(go, true, Accent);
            replay.style.display = DisplayStyle.None;
        }

        // ------------------------------------------------------------------ depth peel ruler
        void BuildDepthCard()
        {
            depthCard = CardBox(root);
            depthCard.style.position = Position.Absolute; depthCard.style.right = 608f; depthCard.style.top = 16f; depthCard.style.width = 214f;
            Heading(depthCard, "Depth peel · K");
            var modes = Box(depthCard, true); modes.pickingMode = PickingMode.Position;
            for (int m = 0; m < PeelNames.Length; m++)
            {
                int mm = m;
                var b = Btn(modes, PeelNames[m], () => { if (PeelModeChosen != null) PeelModeChosen(mm); });
                b.style.flexGrow = 1f; b.style.marginRight = m < PeelNames.Length - 1 ? 3f : 0f; b.style.paddingLeft = 4f; b.style.paddingRight = 4f; b.style.fontSize = 12f;
                peelButtons.Add(b);
            }
            depthRead = Text(depthCard, "—", 16, Ink, true); depthRead.style.marginTop = 8f;
            depthHint = Text(depthCard, "", 11, Muted); depthHint.style.whiteSpace = WhiteSpace.Normal; depthHint.style.marginTop = 2f;
            var body = Box(depthCard, true); body.style.marginTop = 8f; body.style.height = RulerH + 2 * RulerPad;
            rulerCol = Box(body); rulerCol.pickingMode = PickingMode.Position; rulerCol.style.width = 30f; rulerCol.style.height = RulerH + 2 * RulerPad;
            var track = Box(rulerCol); track.style.position = Position.Absolute; track.style.left = 11f; track.style.top = RulerPad; track.style.width = 8f; track.style.height = RulerH; track.style.backgroundColor = new Color(1, 1, 1, 0.12f); Radius(track, 4);
            rulerFill = Box(track); rulerFill.style.width = 8f; rulerFill.style.height = 0f; rulerFill.style.backgroundColor = new Color(Accent.r, Accent.g, Accent.b, 0.7f); Radius(rulerFill, 4);
            rulerHandle = Box(rulerCol); rulerHandle.style.position = Position.Absolute; rulerHandle.style.left = 3f; rulerHandle.style.top = RulerPad - 6f; rulerHandle.style.width = 24f; rulerHandle.style.height = 12f; rulerHandle.style.backgroundColor = Accent; Radius(rulerHandle, 6);
            rulerTicks = Box(body); rulerTicks.style.flexGrow = 1f; rulerTicks.style.height = RulerH + 2 * RulerPad;
            rulerCol.RegisterCallback<PointerDownEvent>(e => { rulerDrag = true; rulerCol.CapturePointer(e.pointerId); RulerAt(e.localPosition.y); });
            rulerCol.RegisterCallback<PointerMoveEvent>(e => { if (rulerDrag) RulerAt(e.localPosition.y); });
            rulerCol.RegisterCallback<PointerUpEvent>(e => { rulerDrag = false; rulerCol.ReleasePointer(e.pointerId); });
            rulerCol.RegisterCallback<PointerCaptureOutEvent>(e => rulerDrag = false);
            var foot = Box(depthCard, true); foot.pickingMode = PickingMode.Position; foot.style.marginTop = 6f;
            sweepBtn = Btn(foot, "Sweep down and up (Shift+K)", () => { if (PeelSweepToggled != null) PeelSweepToggled(); }); sweepBtn.style.flexGrow = 1f; sweepBtn.style.marginRight = 0f; sweepBtn.style.fontSize = 12f;
            var note = Text(depthCard, "Drag the ruler, press , and . or hold Ctrl and roll the wheel. Display only: the instrument still works on the real surface.", 11, Muted);
            note.style.whiteSpace = WhiteSpace.Normal; note.style.marginTop = 6f;
            depthCard.style.display = DisplayStyle.None;
        }

        void RulerAt(float y)
        {
            float f = Mathf.Clamp01((y - RulerPad) / RulerH);
            if (PeelDepthChanged != null) PeelDepthChanged(f * rulerBedMm);
        }

        /// <summary>Name of the layer at a depth below the skin, from the section column's layer tops.</summary>
        static string LayerAt(List<KeyValuePair<string, float>> layers, float mm)
        {
            string name = "above the skin";
            foreach (var kv in layers) { if (kv.Value <= mm + 0.01f) name = kv.Key; else break; }
            return name;
        }

        public void RefreshDepth(HudData d)
        {
            bool on = d.peelMode != 0;
            depthCard.style.display = on ? DisplayStyle.Flex : DisplayStyle.None;
            if (!on) return;
            // the large section floats where the ruler normally sits: move the ruler into the column space it vacated
            depthCard.style.right = largeSection ? 199f : 608f; depthCard.style.top = largeSection ? 250f : 16f;
            rulerBedMm = Mathf.Max(1f, d.bedDepthMm);
            for (int m = 0; m < peelButtons.Count; m++) SetActive(peelButtons[m], m == d.peelMode, Accent);
            SetActive(sweepBtn, d.peelSweep, Accent);
            float f = Mathf.Clamp01(d.peelMm / rulerBedMm);
            rulerFill.style.height = RulerH * f;
            rulerHandle.style.top = RulerPad + RulerH * f - 6f;
            depthRead.text = d.peelMm.ToString("0.0") + " mm · " + LayerAt(d.layers, d.peelMm);
            depthHint.text = d.peelMode == 1
                ? "Block cut: everything above the plane is hidden; the cut face shows each layer and what the energy did at this depth."
                : "Dissection: skin, fat, septa and fasciae above the plane are stripped. Vessels, the nerve, the lipoma and any heated tissue stay, standing in 3D.";
            string key = rulerBedMm.ToString("0.0") + ":" + d.layers.Count + (d.layers.Count > 0 ? ":" + d.layers[d.layers.Count - 1].Value.ToString("0.0") : "");
            if (key == lastTickKey) return;
            lastTickKey = key;
            rulerTicks.Clear();
            for (float mm = 0; mm <= rulerBedMm + 0.01f; mm += 5f)
            {
                float y = RulerPad + RulerH * mm / rulerBedMm;
                var tick = Box(rulerTicks); tick.style.position = Position.Absolute; tick.style.left = 0f; tick.style.top = y; tick.style.width = 6f; tick.style.height = 1f; tick.style.backgroundColor = new Color(1, 1, 1, 0.5f);
                var l = Text(rulerTicks, mm.ToString("0") + (mm == 0 ? " mm" : ""), 11, Muted); l.style.position = Position.Absolute; l.style.left = 9f; l.style.top = y - 7f;
            }
            foreach (var kv in d.layers)
            {
                float y = RulerPad + RulerH * Mathf.Clamp01(kv.Value / rulerBedMm);
                var line = Box(rulerTicks); line.style.position = Position.Absolute; line.style.left = 0f; line.style.top = y; line.style.width = 44f; line.style.height = 1f; line.style.backgroundColor = new Color(Accent.r, Accent.g, Accent.b, 0.6f);
                var l = Text(rulerTicks, kv.Key, 11, Accent, true); l.style.position = Position.Absolute; l.style.left = 48f; l.style.top = y - 7f;
            }
        }

        public void ShowReplay(string title, string detail, string teaching)
        {
            replayTitle.text = title; replayDetail.text = detail ?? ""; replayTeach.text = teaching ?? "";
            replay.style.display = DisplayStyle.Flex;
        }
        public void SetReplayFrame(Texture t, float progress) { replayImg.image = t; replayBar.style.width = 1156f * Mathf.Clamp01(progress); }
        public void HideReplay() { replay.style.display = DisplayStyle.None; replayImg.image = null; }
    }
}
