using System;
using System.Collections;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;
using UnityEngine.SceneManagement;

namespace BovieSim
{
    /// <summary>
    /// Scene entry point. Builds the anatomy and lattice off the main thread, creates the GPU pipeline,
    /// surface renderer, instruments and UI, then runs the same frame order as the web client (src/main.ts):
    /// input, activation, tool placement, particles, events, mechanics, physics, extraction, pick, slice, draw.
    /// The simulation is in millimetres under a field root scaled 0.001, so world units stay metres for HDRP.
    /// </summary>
    public sealed class SimulationRunner : MonoBehaviour
    {
        public static readonly string[] ScenarioIds = { "freeplay", "hemostasis", "lipoma" };
        /// <summary>survives a scene reload so F6 can switch scenarios</summary>
        static string requestedScenario;

        /// <summary>Unity 6.6 projects enter Play mode without a domain reload by default, so statics survive between
        /// Play sessions: clear them here (a scene reload from F5/F6 does not run this, so the scenario switch still works).</summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics() { requestedScenario = null; InputState.invertTiltY = false; }

        [Tooltip("Scenario JSON in Resources/BovieSim/Scenarios")] public string scenarioId = "freeplay";
        [Tooltip("0 = scenario voxel size; larger values (e.g. 0.4) run faster on modest GPUs")] public float voxelOverride;
        public bool mechanics = true;
        public Camera viewCamera;
        [Tooltip("Optional: the scene's global post-processing volume, for depth-of-field focus at the tip")] public UnityEngine.Rendering.Volume postVolume;

        // ---- simulation state
        Scenario scenario; BovieSim.Volume volume; AnatomyReport report; Lattice lattice;
        GpuVolume gpu; PhysicsPipeline physics; SurfaceRenderer surface; Particles particles;
        readonly FT10 gen = new FT10();
        EventBus events; SimUI ui; SurfacePicker picker; readonly HudData hud = new HudData(); ActivationAudio audioOut; OrbitCamera orbit; readonly InputState input = new InputState();
        readonly ElectrodeTool tool = new ElectrodeTool(); readonly ScalpelTool scalpel = new ScalpelTool(); readonly ScissorsTool scissors = new ScissorsTool();
        readonly ForcepsTool forceps = new ForcepsTool(); readonly SuctionTool suction = new SuctionTool(); readonly HookTool hook = new HookTool();
        // the non-dominant hand has its own instruments, so a latched hold keeps its pose while the pointer uses the same kind
        readonly HandController hands = new HandController();
        readonly ForcepsTool forcepsB = new ForcepsTool(); readonly SuctionTool suctionB = new SuctionTool(); readonly HookTool hookB = new HookTool();
        ToolKind activeTool = ToolKind.Pencil; HandBKind toolB = HandBKind.None; ElectrodeType pencilElectrode = ElectrodeType.Blade;
        /// <summary>last pick for each hand, the lattice node each holds, the frozen traction target of a latched hand B, lifts from the wheel</summary>
        bool hasHitA, hasHitB; Vector3 hitA, hitB; int nodeA = -1, nodeB = -1; Vector3 targetB; float liftA, liftB;
        /// <summary>secondary tool slot chosen this frame (forceps jaws or suction tip), and whether hand B's instrument is on the field</summary>
        Vector3 t2A, t2B; float t2R; int t2Mode; bool showB;
        bool slicePerp, lesionFreedReported, lesionDelivered, lesionBreachReported, ready;
        float clusterRiseMm; string status = "Building anatomy…";
        Vector4[] lastDisp; bool latticeMoved;
        float maxRimMm, maxCharMm3; int layerColumn = -1;
        HDAdditionalCameraData hdCam; bool dofOn;
        readonly System.Diagnostics.Stopwatch frameWatch = new System.Diagnostics.Stopwatch();
        readonly FrameTiming[] frameTimings = new FrameTiming[1]; float cpuMs, gpuMs;
        static readonly HDAdditionalCameraData.AntialiasingMode[] AaModes = { HDAdditionalCameraData.AntialiasingMode.SubpixelMorphologicalAntiAliasing, HDAdditionalCameraData.AntialiasingMode.TemporalAntialiasing, HDAdditionalCameraData.AntialiasingMode.FastApproximateAntialiasing, HDAdditionalCameraData.AntialiasingMode.None };
        int aaIndex;
        // settings, detail level and teaching moments
        UserSettings settings; DetailSettings detail; DetailLevel chosenLevel; string detailReason = "";
        FrameBudget budget; bool budgetDismissed;
        ReplayBuffer replay; bool paused; float replayT;
        readonly System.Collections.Generic.Dictionary<string, float> lastMoment = new System.Collections.Generic.Dictionary<string, float>();
        Material overlayMat; int overlayMode; // 0 off, 1 temperature, 2 damage
        // depth peel of the 3D view (display only): 0 off, 1 block cut, 2 dissection; depth below the original skin (mm)
        int peelMode; float peelMm = 3f; bool peelSweep; float peelSweepDir = 1f; int shownPeelMode = -1; float shownPeelMm = -1f;
        static string SettingsPath { get { return System.IO.Path.Combine(Application.persistentDataPath, "settings.json"); } }

        // ---- scene objects
        Transform field;
        GameObject pencil, scalpelGo, scissorsGoA, scissorsGoB, forcepsArmA, forcepsArmB, frazier, hookGo, forcepsBArmA, forcepsBArmB, frazierB, hookGoB, drapes;
        MeshFilter electrodeFilter; Mesh bladeMesh, needleMesh, ballMesh;
        MeshRenderer cutButton, coagButton; MaterialPropertyBlock buttonMpb;
        Light arcLight; DepthOfField dof;
        SimAssets assets;
        int frames; float fps, fpsT;

        IEnumerator Start()
        {
            if (!string.IsNullOrEmpty(requestedScenario)) scenarioId = requestedScenario;
            if (viewCamera == null) viewCamera = Camera.main;
            assets = SimAssets.Load();
            var simCs = Resources.Load<ComputeShader>("BovieSim/Shaders/Sim");
            var surfCs = Resources.Load<ComputeShader>("BovieSim/Shaders/Surface");
            var scJson = Resources.Load<TextAsset>("BovieSim/Scenarios/" + scenarioId);
            var teach = Resources.Load<TextAsset>("BovieSim/Scenarios/teaching");
            if (assets != null && (assets.uiTheme == null || assets.setupVersion < SimAssets.CurrentSetupVersion))
            {
                status = "Cannot start: the project setup is out of date. Stop Play and run Bovie Sim > Set Up Project.";
                Debug.LogError("[Bovie Sim] " + status);
                yield break;
            }
            if (assets == null || simCs == null || surfCs == null || scJson == null || viewCamera == null)
            {
                status = "Cannot start: " + (assets == null ? "SimAssets missing (run Bovie Sim > Set Up Project). " : "") + (simCs == null || surfCs == null ? "compute shaders missing. " : "") + (scJson == null ? "scenario '" + scenarioId + "' not found. " : "") + (viewCamera == null ? "no camera. " : "");
                Debug.LogError("[Bovie Sim] " + status);
                yield break;
            }
            LoadSettings();
            string unsupported = HardwareProfile.Unsupported();
            if (unsupported != null)
            {
                status = "Cannot start: " + unsupported + ". Bovie Sim needs a DirectX 11 or 12 class graphics card with compute shaders (see docs/hardware.md).";
                Debug.LogError("[Bovie Sim] " + status);
                yield break;
            }

            scenario = Scenario.FromJson(scJson.text);
            events = new EventBus(teach != null ? teach.text : null);
            gen.cutMode = FT10.Parse(scenario.generator.cutMode, ModeId.Pure); gen.coagMode = FT10.Parse(scenario.generator.coagMode, ModeId.Fulgurate);
            gen.cutWatts = scenario.generator.cutWatts; gen.coagWatts = scenario.generator.coagWatts;
            gen.Adjust(true, 0); gen.Adjust(false, 0);
            gen.remFault = scenario.faults != null && scenario.faults.Contains("rem");

            // anatomy and lattice are pure C#: build them on a worker thread
            // detail level from the PC (or Settings); the inspector's Voxel Override still wins for development
            chosenLevel = ParseLevel(settings.detail);
            detail = DetailSettings.For(HardwareProfile.Resolve(chosenLevel, out detailReason));
            budget = new FrameBudget(detail.frameBudgetMs);
            status = "Building anatomy at " + detail.Name + " detail (" + (voxelOverride > 0 ? voxelOverride : detail.voxelMm).ToString("0.00") + " mm voxels)…";
            var sc = scenario; float vo = voxelOverride > 0 ? voxelOverride : detail.voxelMm; bool mech = mechanics;
            var build = Task.Run(() =>
            {
                AnatomyReport rep;
                var v = Anatomy.Build(sc, out rep, vo);
                var lat = mech ? new Lattice(v, 3f, sc.skin.langerDeg * Mathf.Deg2Rad) : null;
                var pk = new SurfacePicker(v);
                return new Tuple<BovieSim.Volume, AnatomyReport, Lattice, SurfacePicker>(v, rep, lat, pk);
            });
            while (!build.IsCompleted) yield return null;
            if (build.IsFaulted)
            {
                status = "Anatomy build failed: " + build.Exception.GetBaseException().Message;
                Debug.LogException(build.Exception.GetBaseException());
                yield break;
            }
            volume = build.Result.Item1; report = build.Result.Item2; lattice = build.Result.Item3; picker = build.Result.Item4;

            // field root: millimetres to metres
            field = new GameObject("Field (mm)").transform;
            field.SetParent(transform, false);
            field.localScale = Vector3.one * 0.001f;

            gpu = new GpuVolume(volume);
            physics = new PhysicsPipeline(simCs, gpu, detail.maxSubdomain, detail.sliceScale);
            physics.relaxSweeps = detail.relaxSweeps;
            surface = new SurfaceRenderer(surfCs, gpu, field, SkinCorrectedMaterials(), assets.blood);
            surface.maxBricksPerFrame = detail.bricksPerFrame;
            particles = new Particles(assets.particlesAlpha, assets.particlesAdditive);
            if (assets.teachingOverlay != null) { overlayMat = new Material(assets.teachingOverlay); surface.SetOverlay(overlayMat, false); }
            if (scenario.preCarve != null && scenario.preCarve.Count > 0) physics.MarkWound(new[] { 0, 0, 0 }, new[] { volume.nx, volume.ny, volume.nz });
            if (lattice != null)
            {
                surface.SetLattice(lattice.nx, lattice.ny, lattice.nz, lattice.origin, lattice.spacing, lattice.count);
                physics.SetLatticeDisplacement(surface.LatticeBuffer, lattice.nx, lattice.ny, lattice.nz, lattice.origin, lattice.spacing);
                physics.SetConstraintSegments(lattice.SegmentData(volume));
                surface.UploadLattice(lattice.disp);
                lastDisp = (Vector4[])lattice.disp.Clone();
            }
            ui = new SimUI(transform, assets.uiTheme, gen, physics);
            ui.SetScenario(scenario);
            ui.ToggleOrientation += () => slicePerp = !slicePerp;
            ui.PeelModeChosen += SetPeelMode;
            ui.PeelCycle += () => SetPeelMode((peelMode + 1) % 3);
            ui.PeelDepthChanged += mm => { peelMm = Mathf.Clamp(mm, 0, volume.SizeDepth); peelSweep = false; };
            ui.PeelSweepToggled += () => peelSweep = !peelSweep;
            events.OnEvent += ui.Push;
            events.OnEvent += OnTeachingMoment;
            ui.DetailChosen += l => { chosenLevel = l; settings.detail = l.ToString(); SaveSettings(); };
            ui.SettingToggled += ToggleSetting;
            ui.BudgetAccept += () => { var lower = (DetailLevel)Mathf.Max(0, (int)detail.level - 1); settings.detail = lower.ToString(); settings.overBudgetAt = detail.level.ToString(); SaveSettings(); Reload(scenarioId); };
            ui.BudgetDismiss += () => budgetDismissed = true;
            ui.ReplayAgain += () => replayT = 0;
            ui.ReplayContinue += Resume;
            ui.showPerf = settings.showTiming; ui.showGauge = settings.tipGauge;
            replay = new ReplayBuffer(detail.replaySeconds, detail.replayWidth);
            StartCoroutine(replay.CaptureLoop());

            // latency: one queued frame instead of two; SMAA instead of TAA (no ghosting behind moving instruments)
            QualitySettings.maxQueuedFrames = 1;
            hdCam = viewCamera.GetComponent<HDAdditionalCameraData>();
            dofOn = settings.depthOfField; aaIndex = Mathf.Clamp(settings.antialiasing, 0, AaModes.Length - 1); InputState.invertTiltY = settings.invertTiltY;
            if (hdCam != null) hdCam.antialiasing = AaModes[aaIndex];

            BuildInstruments();
            orbit = new OrbitCamera(viewCamera, field);
            audioOut = gameObject.AddComponent<ActivationAudio>();
            if (postVolume == null) postVolume = FindAnyObjectByType<UnityEngine.Rendering.Volume>();
            if (postVolume != null && postVolume.sharedProfile != null) postVolume.sharedProfile.TryGet(out dof);
            if (dof != null) dof.active = dofOn; // off by default: blur reads as lag when the view moves
            ApplyRenderDetail();
            status = "";
            ready = true;
        }

        /// <summary>
        /// Epidermis textures are baked for one skin tone and prep; tint runtime copies of those materials so the
        /// skin matches this scenario's tone and prep (chlorhexidine, povidone-iodine or none). The ratio is taken in
        /// linear space because HDRP multiplies the base map by _BaseColor there.
        /// </summary>
        Material[] SkinCorrectedMaterials()
        {
            var mats = (Material[])assets.tissue.Clone();
            Color want = TissuePalette.Skin(scenario.skin.tone) * TissuePalette.Prep(scenario.skin.prep);
            Color baked = TissuePalette.Skin(TissuePalette.BakedTone) * TissuePalette.Prep(TissuePalette.BakedPrep);
            Color w = want.linear, b = baked.linear;
            var k = new Color(Mathf.Clamp(w.r / Mathf.Max(b.r, 1e-3f), 0, 2), Mathf.Clamp(w.g / Mathf.Max(b.g, 1e-3f), 0, 2), Mathf.Clamp(w.b / Mathf.Max(b.b, 1e-3f), 0, 2), 1);
            foreach (int bucket in new[] { 0, 8 }) // epidermis, and coagulated epidermis (half strength: blanching hides the stain)
            {
                if (mats[bucket] == null) continue;
                var m = new Material(mats[bucket]) { name = mats[bucket].name + " (scenario skin)" };
                m.SetColor("_BaseColor", bucket == 0 ? k : Color.Lerp(Color.white, k, 0.5f));
                mats[bucket] = m;
            }
            return mats;
        }

        GameObject MakePart(string name, Mesh mesh, Material mat, Transform parent)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = mat;
            return go;
        }

        void BuildInstruments()
        {
            drapes = MakePart("Drapes", MeshFactory.Drapes(volume.SizeX / 2, volume.SizeZ / 2), assets.drape, field);
            pencil = new GameObject("Bovie pencil"); pencil.transform.SetParent(field, false);
            MakePart("Body", MeshFactory.PencilBody(), assets.pencilBody, pencil.transform);
            cutButton = MakePart("Cut button", MeshFactory.PencilButton(44, 58), assets.pencilCutButton, pencil.transform).GetComponent<MeshRenderer>();
            coagButton = MakePart("Coag button", MeshFactory.PencilButton(62, 76), assets.pencilCoagButton, pencil.transform).GetComponent<MeshRenderer>();
            bladeMesh = MeshFactory.Blade(); needleMesh = MeshFactory.Needle(); ballMesh = MeshFactory.Ball();
            electrodeFilter = MakePart("Electrode", bladeMesh, assets.steel, pencil.transform).GetComponent<MeshFilter>();
            scalpelGo = MakePart("Scalpel", MeshFactory.Scalpel(), assets.steel, field);
            scissorsGoA = MakePart("Metzenbaum A", MeshFactory.ScissorsBlade(scissors.jawLength, scissors.shankLength), assets.steel, field);
            scissorsGoB = MakePart("Metzenbaum B", MeshFactory.ScissorsBlade(scissors.jawLength, scissors.shankLength), assets.steel, field);
            // arm A sits on the -Z side of the jaw centre and arm B on +Z: their teeth (1 x 2) face each other
            forcepsArmA = MakePart("Adson forceps A", MeshFactory.ForcepsArm(forceps.armLength, true, 1), assets.steel, field);
            forcepsArmB = MakePart("Adson forceps B", MeshFactory.ForcepsArm(forceps.armLength, false, 2), assets.steel, field);
            frazier = MakePart("Frazier suction", MeshFactory.Frazier(), assets.steel, field);
            hookGo = MakePart("Skin hook", MeshFactory.SkinHook(), assets.steel, field);
            // the non-dominant hand's set
            forcepsBArmA = MakePart("Adson forceps (left) A", MeshFactory.ForcepsArm(forcepsB.armLength, true, 1), assets.steel, field);
            forcepsBArmB = MakePart("Adson forceps (left) B", MeshFactory.ForcepsArm(forcepsB.armLength, false, 2), assets.steel, field);
            frazierB = MakePart("Frazier suction (left)", MeshFactory.Frazier(), assets.steel, field);
            hookGoB = MakePart("Skin hook (left)", MeshFactory.SkinHook(), assets.steel, field);
            foreach (var go in new[] { pencil, scalpelGo, scissorsGoA, scissorsGoB, forcepsArmA, forcepsArmB, frazier, hookGo, forcepsBArmA, forcepsBArmB, frazierB, hookGoB })
                foreach (var r in go.GetComponentsInChildren<MeshRenderer>()) r.shadowCastingMode = ShadowCastingMode.On;
            buttonMpb = new MaterialPropertyBlock();

            var lg = new GameObject("Arc light");
            lg.transform.SetParent(field, false);
            lg.AddHDLight(LightType.Point);
            arcLight = lg.GetComponent<Light>();
            arcLight.color = new Color(0.75f, 0.85f, 1f);
            arcLight.range = 0.08f;
            arcLight.lightUnit = LightUnit.Lumen;
            arcLight.intensity = 0;
            arcLight.shadows = LightShadows.None;
            arcLight.enabled = false;
        }

        // ------------------------------------------------------------------------------------------------
        void Update()
        {
            if (Input.GetKeyDown(KeyCode.Escape) && !Application.isEditor) { Application.Quit(); return; }
            if (Input.GetKeyDown(KeyCode.F11)) Screen.fullScreen = !Screen.fullScreen;
            if (Input.GetKeyDown(KeyCode.F5)) { Reload(scenarioId); return; }
            if (Input.GetKeyDown(KeyCode.F6)) { Reload(ScenarioIds[(Array.IndexOf(ScenarioIds, scenarioId) + 1) % ScenarioIds.Length]); return; }
            if (!ready) return;
            if (paused) { UpdatePaused(); return; }
            frameWatch.Reset(); frameWatch.Start();
            float frameDt = Mathf.Min(0.05f, Time.deltaTime);
            input.Update();
            bool overUi = ui.PointerOverUi(input.mouse);
            // Ctrl + wheel scrubs the depth peel; the plain wheel zooms
            bool ctrlHeld = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
            // the wheel lifts an instrument that is holding tissue (traction); otherwise it zooms
            bool lifting = (input.spaceHeld && hands.engagedB) || (!input.spaceHeld && nodeA >= 0);
            orbit.HandleInput(overUi, !ctrlHeld && !lifting);
            if (ctrlHeld && !overUi) { float wheel = Input.mouseScrollDelta.y; if (Mathf.Abs(wheel) > 0.01f) StepPeel(-wheel * 0.25f); }
            else if (lifting && !overUi && Mathf.Abs(input.wheel) > 0.01f) { if (input.spaceHeld) liftB = Mathf.Clamp(liftB + input.wheel * 0.5f, 0, 15); else liftA = Mathf.Clamp(liftA + input.wheel * 0.5f, 0, 15); }
            HandleKeys();
            ApplyPeel(frameDt);

            // ---- the button, and which hand it belongs to: Space hands the pointer to the non-dominant hand
            bool lmb = input.leftHeld && !overUi && !orbit.Dragging;
            hands.Update(input.spaceHeld, lmb);
            bool pressedA = hands.pressedA;
            bool handCut = activeTool == ToolKind.Pencil && pressedA && !input.ctrl;
            bool handCoag = activeTool == ToolKind.Pencil && pressedA && input.ctrl;
            gen.activation = input.pedalCut || ui.pedalCut || handCut ? Activation.Cut : input.pedalCoag || ui.pedalCoag || handCoag ? Activation.Coag : Activation.None;

            var r = physics.readout;
            // same-frame pick on the CPU height map (the GPU pick answers a few frames late); the pick serves one hand at a time
            Vector3 rayO, rayD; PickRay(out rayO, out rayD);
            Vector3 hit = Vector3.zero;
            bool hasHit = input.inside && picker.Raycast(rayO, rayD, out hit);
            if (hands.followB) { hasHitB = hasHit; hitB = hit; } else if (!input.spaceHeld) { hasHitA = hasHit; hitA = hit; }
            tool.pressure = input.pressure; tool.tilt = input.tiltDeg; scalpel.pressure = input.pressure; scissors.pressure = input.pressure;
            var camMm = orbit.PositionMm;
            bool tiltOn = input.HasTilt;
            bool suctionOn = false; Vector3 suctionAt = Vector3.zero;
            events.buzzing = false; events.suctionOn = false; events.instrumentContact = false;
            if (lattice != null) lattice.attachments.Clear();

            // ---- instrument placement (physics in material space; display shifted by the lattice displacement in PoseInstruments)
            PlaceHandB(camMm, tiltOn);
            PlaceHandA(frameDt, camMm, tiltOn, pressedA, ref suctionOn, ref suctionAt);

            var mode = gen.ActiveMode;
            audioOut.Set(mode != null ? mode.toneHz : 500, mode != null && !gen.remFault);

            // ---- bleeding droplets, steam, smoke and arcs from the last readout
            for (int n = 0; n < r.bleedCount; n++) { var bp = r.bleedPoints[n]; particles.Bleed(new Vector3(bp.x, bp.y, bp.z), bp.w, frameDt); }
            if (mode != null && r.hasHit && r.contactVoxels > 0 && tool.visible)
            {
                var tip = tool.tip;
                if (mode.isCut) particles.Emit(0, tip, 20 + r.vapVoxels * 30, frameDt);
                else particles.Emit(1, tip, 6 + (r.maxTempC >= 100 ? 10 : 0), frameDt);
                if (r.maxTempC >= 100) particles.Emit(1, tip, mode.isCut ? 3 : 4, frameDt);
                float reach = mode.ArcReachMm;
                int arcs = !mode.isCut ? (mode.id == ModeId.Spray ? 4 : 2) : (UnityEngine.Random.value < 0.15f ? 1 : 0);
                for (int a = 0; a < arcs; a++)
                {
                    float ang = UnityEngine.Random.value * Mathf.PI * 2, rad = UnityEngine.Random.value * Mathf.Max(reach, 0.3f);
                    var end = new Vector3(r.hit.x + Mathf.Cos(ang) * rad, r.hit.y + 0.05f, r.hit.z + Mathf.Sin(ang) * rad);
                    var start = tool.tip + tool.axis * (0.4f + UnityEngine.Random.value * 1.2f);
                    particles.Arc(start, end);
                }
            }
            particles.Update(frameDt, suctionOn, suctionAt, volume.SurfaceY);
            physics.pendingDrops.AddRange(particles.landings);
            particles.landings.Clear();
            string toolName = (activeTool == ToolKind.Pencil ? tool.type.ToString().ToLowerInvariant() : activeTool.ToString().ToLowerInvariant()) + (toolB != HandBKind.None ? " + " + toolB.ToString().ToLowerInvariant() : "");
            events.Update(physics.simTime, r, gen, toolName);

            // ---- mechanics: apply the last probe (severed / shrunk links), muscle twitch, step, upload
            if (lattice != null) StepMechanics(mode, r, frameDt);

            // ---- physics, then surface extraction when tissue or deformation changed
            bool hot = physics.Step(tool, gen, frameDt);
            // tissue changes where the electrode works every frame; the cooling region changes slowly (state, colour)
            int[] bmin, bmax;
            if (physics.ActiveBox(out bmin, out bmax)) surface.MarkVoxelBox(bmin, bmax);
            if (physics.CutBox(out bmin, out bmax)) surface.MarkVoxelBox(bmin, bmax);
            if (Time.frameCount % 8 == 0 && physics.ThermalBox(out bmin, out bmax)) surface.MarkVoxelBox(bmin, bmax);
            surface.Update(hasHit ? hit : tool.tip);
            picker.Refresh(physics, hot || latticeMoved);
            surface.UpdateBlood();

            // ---- pick ray for the next frame and cross-section plane through the tip
            physics.SetPickRay(rayO, rayD);
            var right = new Vector3(Mathf.Cos(orbit.azimuth), 0, -Mathf.Sin(orbit.azimuth));
            if (slicePerp && tool.visible)
            {
                var ax = new Vector3(tool.axis.x, 0, tool.axis.z);
                if (ax.magnitude > 1e-3f) { ax.Normalize(); right = new Vector3(-ax.z, 0, ax.x); }
            }
            var anchor = hasHit ? hit : Vector3.zero;
            // the view starts 1.5 mm above the higher of the tip and the original skin, so the skin is always in view
            physics.sliceOrigin = new Vector3(anchor.x, Mathf.Max(hasHit ? anchor.y : 0, 0) + 1.5f, anchor.z);
            physics.sliceU = right;

            PoseInstruments(mode);
            orbit.Apply();
            UpdateFocus();
            particles.Draw(field);

            frames++; fpsT += Time.unscaledDeltaTime;
            if (fpsT > 0.5f) { fps = frames / fpsT; frames = 0; fpsT = 0; }
            FillHud(r);
            ui.Refresh(hud, Metric);
            UpdateGaugeAndBudget(frameDt);
            frameWatch.Stop(); cpuMs = Mathf.Lerp(cpuMs, (float)frameWatch.Elapsed.TotalMilliseconds, 0.1f);
            FrameTimingManager.CaptureFrameTimings();
            if (FrameTimingManager.GetLatestTimings(1, frameTimings) > 0 && frameTimings[0].gpuFrameTime > 0) gpuMs = Mathf.Lerp(gpuMs, (float)frameTimings[0].gpuFrameTime, 0.1f);
        }

        void StepMechanics(ModeSpec mode, SimReadout r, float frameDt)
        {
            byte[] sev; float[] om;
            if (physics.TakeProbe(out sev, out om)) lattice.ApplyProbe(sev, om);
            if (mode != null && !mode.isCut && r.muscleHeatVoxels > 20 && tool.visible) lattice.Stimulate(tool.tip, Mathf.Min(2, 0.5f + r.iRms * 2), physics.simTime);
            lattice.Step(frameDt, physics.simTime);
            // rebuild only the bricks around nodes that moved visibly since they were last extracted
            latticeMoved = false;
            for (int n = 0; n < lattice.count; n++)
            {
                var a = lattice.disp[n]; var b = lastDisp[n];
                if (Mathf.Abs(a.x - b.x) + Mathf.Abs(a.y - b.y) + Mathf.Abs(a.z - b.z) > 0.02f)
                {
                    latticeMoved = true;
                    surface.MarkSphereMm(lattice.Mat(n), lattice.spacing * 1.5f);
                    lastDisp[n] = a;
                }
            }
            if (latticeMoved) surface.UploadLattice(lattice.disp);

            if (lattice.lipomaFree && !lesionFreedReported)
            {
                lesionFreedReported = true;
                events.Emit(physics.simTime, "lesion.freed", (lattice.capsuleTotal - lattice.capsuleAlive) + "/" + lattice.capsuleTotal + " capsule links cut");
            }
            if (lattice.lipomaFree && !lesionDelivered)
            {
                // delivered when the freed cluster has risen out of its bed by 4 mm on average
                clusterRiseMm = lattice.ClusterRise();
                if (clusterRiseMm > 4) { lesionDelivered = true; events.Emit(physics.simTime, "lesion.delivered", "lipoma lifted " + clusterRiseMm.ToString("0.0") + " mm out of its bed"); }
            }
            if (mode != null && r.hasHit && tool.visible && Cell.Type(r.hitCell) == Tissue.Lipoma && !lesionBreachReported)
            {
                lesionBreachReported = true;
                events.Emit(physics.simTime, "lesion.breached", "energy applied inside the lipoma", Severity.Bad);
            }
        }

        /// <summary>
        /// The non-dominant hand: forceps, hook or suction. Live while Space is held (it follows the pointer; the button
        /// grasps or catches), latched where it was left otherwise, keeping its attachment and its frozen target.
        /// </summary>
        void PlaceHandB(Vector3 camMm, bool tiltOn)
        {
            t2Mode = 0;
            if (toolB == HandBKind.None) { hands.ReleaseB(); nodeB = -1; }
            if (!hands.engagedB) { nodeB = -1; liftB = 0; }
            showB = toolB != HandBKind.None && hands.stateB != HandBState.Parked;
            if (!showB) return;
            bool follow = hands.followB;
            bool hb = follow && hasHitB;
            if (toolB == HandBKind.Forceps)
            {
                forcepsB.closed = hands.engagedB;
                if (follow) forcepsB.Place(hb, hitB + Vector3.up * liftB, camMm, tiltOn, input.tiltDeg, 0.3f, true);
                if (follow && !forcepsB.visible) { showB = false; return; }
                if (forcepsB.closed && forcepsB.visible)
                {
                    if (lattice != null)
                    {
                        if (hands.regrabB || nodeB < 0) nodeB = lattice.NearestNode(forcepsB.Mid, 3);
                        if (follow) targetB = forcepsB.Mid + forcepsB.axis * 1.0f;
                        if (nodeB >= 0) lattice.attachments.Add(new Attachment { node = nodeB, target = targetB, strength = 0.2f });
                    }
                    t2A = forcepsB.tipA; t2B = forcepsB.tipB; t2R = forcepsB.jawRadius; t2Mode = 1;
                }
            }
            else if (toolB == HandBKind.Hook)
            {
                if (lattice != null)
                {
                    if (hands.engagedB && hb && (hands.regrabB || nodeB < 0)) nodeB = lattice.NearestNode(hitB, 4);
                    if (hands.engagedB && nodeB >= 0 && follow)
                    {
                        // drag target: cursor ray intersected with the horizontal plane 1.5 mm above the node's start height, plus the lift
                        Vector3 ro, rd; PickRay(out ro, out rd);
                        float planeY = lattice.mat[nodeB * 3 + 1] + 1.5f;
                        float tHit = Mathf.Abs(rd.y) > 1e-4f ? (planeY - ro.y) / rd.y : -1;
                        if (tHit > 0) targetB = ro + rd * tHit + Vector3.up * liftB;
                    }
                    if (hands.engagedB && nodeB >= 0) lattice.attachments.Add(new Attachment { node = nodeB, target = targetB, strength = 0.25f });
                }
                bool holding = hands.engagedB && nodeB >= 0;
                if (follow) hookB.Place(holding || hb, holding ? targetB : hitB, camMm, tiltOn, input.tiltDeg, true);
                if (follow && !hookB.visible) { showB = false; return; }
            }
            else
            {
                suctionB.on = hands.engagedB;
                if (follow) suctionB.Place(hb, hitB, camMm, tiltOn, input.tiltDeg, true);
                if (follow && !suctionB.visible) { showB = false; return; }
                if (suctionB.visible && suctionB.on) { t2A = suctionB.tip; t2B = suctionB.tip; t2R = suctionB.drainRadius; t2Mode = 2; }
            }
        }

        /// <summary>The dominant hand's instrument, and the electrode (pencil, buzzed forceps, or a touched retractor).</summary>
        void PlaceHandA(float frameDt, Vector3 camMm, bool tiltOn, bool pressedA, ref bool suctionOn, ref Vector3 suctionAt)
        {
            bool electrode = false;
            switch (activeTool)
            {
                case ToolKind.Pencil:
                    tool.type = pencilElectrode;
                    tool.Place(hasHitA, hitA, camMm, tiltOn);
                    electrode = tool.visible;
                    break;
                case ToolKind.Scalpel:
                    scalpel.Place(hasHitA, hitA, camMm, tiltOn, input.tiltDeg, pressedA);
                    if (scalpel.visible && scalpel.cutting) physics.SetMechCut(1, scalpel.sweeping ? scalpel.prevCentre : scalpel.Centre, scalpel.Centre, scalpel.axis, scalpel.wdir, scalpel.ndir, scalpel.HalfLength, scalpel.halfWidth, scalpel.halfThickness, 0);
                    break;
                case ToolKind.Scissors:
                    var action = pressedA && input.ctrl ? ScissorsTool.Action.Spread : hands.pressEdgeA ? ScissorsTool.Action.Snip : ScissorsTool.Action.None;
                    scissors.Place(hasHitA, hitA, camMm, tiltOn, input.tiltDeg, action, frameDt);
                    if (scissors.visible && scissors.snip) physics.SetMechCut(1, scissors.Centre, scissors.Centre, scissors.axis, scissors.wdir, scissors.ndir, scissors.HalfLength, Mathf.Max(scissors.open, 2) / 2, 0.15f, 0);
                    else if (scissors.visible && scissors.spreading) physics.SetMechCut(2, scissors.Centre, scissors.Centre, scissors.axis, scissors.wdir, scissors.ndir, scissors.HalfLength, scissors.open / 2, 0.4f, scissors.spreadRadius);
                    break;
                case ToolKind.Forceps:
                    forceps.closed = pressedA;
                    if (!forceps.closed) { nodeA = -1; liftA = 0; }
                    forceps.Place(hasHitA, hitA + Vector3.up * liftA, camMm, tiltOn, input.tiltDeg);
                    if (forceps.visible && forceps.closed)
                    {
                        if (t2Mode == 0) { t2A = forceps.tipA; t2B = forceps.tipB; t2R = forceps.jawRadius; t2Mode = 1; }
                        // closed jaws carry the nearest node (and with it a freed lipoma)
                        if (lattice != null)
                        {
                            if (nodeA < 0) nodeA = lattice.NearestNode(forceps.Mid, 3);
                            if (nodeA >= 0) lattice.attachments.Add(new Attachment { node = nodeA, target = forceps.Mid + forceps.axis * 1.0f, strength = 0.2f });
                        }
                        // buzzing single-handed: the closed jaws are the electrode (a pedal activation)
                        tool.AsForceps(forceps.tipA, forceps.tipB, forceps.jawRadius, forceps.axis); electrode = true;
                        events.buzzing = gen.activation != Activation.None;
                    }
                    break;
                case ToolKind.Suction:
                    suction.on = pressedA;
                    suction.Place(hasHitA, hitA, camMm, tiltOn, input.tiltDeg);
                    if (suction.visible && suction.on)
                    {
                        suctionOn = true; suctionAt = suction.tip; events.suctionOn = true;
                        if (t2Mode == 0) { t2A = suction.tip; t2B = suction.tip; t2R = suction.drainRadius; t2Mode = 2; }
                    }
                    break;
                default:
                    // skin hook: press to attach the nearest lattice node, drag to retract, release to let go
                    bool dragging = false; Vector3 dragTarget = Vector3.zero;
                    if (pressedA && lattice != null)
                    {
                        if (nodeA < 0 && hasHitA) nodeA = lattice.NearestNode(hitA, 4);
                        if (nodeA >= 0)
                        {
                            Vector3 ro, rd; PickRay(out ro, out rd);
                            float planeY = lattice.mat[nodeA * 3 + 1] + 1.5f;
                            float tHit = Mathf.Abs(rd.y) > 1e-4f ? (planeY - ro.y) / rd.y : -1;
                            if (tHit > 0) { dragging = true; dragTarget = ro + rd * tHit + Vector3.up * liftA; lattice.attachments.Add(new Attachment { node = nodeA, target = dragTarget, strength = 0.25f }); }
                        }
                    }
                    else { nodeA = -1; liftA = 0; }
                    hook.Place(dragging || hasHitA, dragging ? dragTarget : hitA, camMm, tiltOn, input.tiltDeg);
                    break;
            }
            if (activeTool != ToolKind.Forceps && activeTool != ToolKind.Hook) nodeA = -1;
            // the pencil against the other hand's instrument: buzzing the forceps, or an alternate-site burn under a retractor
            if (activeTool == ToolKind.Pencil && tool.visible && showB && gen.activation != Activation.None)
            {
                if (toolB == HandBKind.Forceps && forcepsB.closed && HandPose.MetalDistance(tool.tip, forcepsB.Mid, forcepsB.axis, 45, 1.6f) < 1.0f)
                { tool.AsForceps(forcepsB.tipA, forcepsB.tipB, forcepsB.jawRadius, forcepsB.axis); events.buzzing = true; }
                else if (toolB == HandBKind.Hook && hookB.visible && HandPose.MetalDistance(tool.tip, hookB.tip, hookB.axis, 40, 1.0f) < 1.0f)
                { tool.AsForceps(hookB.tip, hookB.tip + hookB.axis * 1.5f, 0.4f, hookB.axis); events.instrumentContact = true; }
                else if (toolB == HandBKind.Suction && suctionB.visible && HandPose.MetalDistance(tool.tip, suctionB.tip, suctionB.axis, 50, 1.7f) < 1.0f)
                { tool.AsForceps(suctionB.tip, suctionB.tip + suctionB.axis * 2.0f, 1.5f, suctionB.axis); events.instrumentContact = true; }
            }
            if (!electrode) { tool.visible = false; gen.activation = Activation.None; }
            physics.SetSecondaryTool(t2A, t2B, t2R, t2Mode);
        }

        /// <summary>Cursor ray in field millimetres.</summary>
        void PickRay(out Vector3 originMm, out Vector3 dirMm)
        {
            var ray = viewCamera.ScreenPointToRay(new Vector3(input.mouse.x, input.mouse.y, 0));
            originMm = field.InverseTransformPoint(ray.origin);
            dirMm = field.InverseTransformDirection(ray.direction).normalized;
        }

        Vector3 DisplayOffset(Vector3 p) { return lattice != null ? lattice.DisplacementAt(p) : Vector3.zero; }

        void PoseInstruments(ModeSpec mode)
        {
            bool pen = activeTool == ToolKind.Pencil && tool.visible;
            pencil.SetActive(pen);
            if (pen)
            {
                electrodeFilter.sharedMesh = pencilElectrode == ElectrodeType.Needle ? needleMesh : pencilElectrode == ElectrodeType.Ball ? ballMesh : bladeMesh;
                // mesh frame: +Y along the handle axis, +Z the blade face normal (includes roll)
                HandPose.Apply(pencil.transform, tool.tip + DisplayOffset(tool.tip), tool.axis, tool.ndir);
            }
            SetButtonGlow(cutButton, gen.activation == Activation.Cut, new Color(1f, 0.85f, 0.2f));
            SetButtonGlow(coagButton, gen.activation == Activation.Coag, new Color(0.25f, 0.55f, 1f));
            bool sc = activeTool == ToolKind.Scalpel && scalpel.visible;
            scalpelGo.SetActive(sc);
            if (sc) HandPose.Apply(scalpelGo.transform, scalpel.tip + DisplayOffset(scalpel.tip), scalpel.axis, scalpel.ndir);
            bool sz = activeTool == ToolKind.Scissors && scissors.visible;
            scissorsGoA.SetActive(sz); scissorsGoB.SetActive(sz);
            if (sz)
            {
                var off = DisplayOffset(scissors.tip);
                HandPose.Apply(scissorsGoA.transform, scissors.tipA + off, scissors.armAy, scissors.armAz);
                HandPose.Apply(scissorsGoB.transform, scissors.tipB + off, scissors.armBy, scissors.armBz);
            }
            bool fc = activeTool == ToolKind.Forceps && forceps.visible;
            forcepsArmA.SetActive(fc); forcepsArmB.SetActive(fc);
            if (fc)
            {
                var off = DisplayOffset(forceps.Mid);
                HandPose.Apply(forcepsArmA.transform, forceps.tipA + off, forceps.armAy, forceps.armAz);
                HandPose.Apply(forcepsArmB.transform, forceps.tipB + off, forceps.armBy, forceps.armBz);
            }
            bool su = activeTool == ToolKind.Suction && suction.visible;
            frazier.SetActive(su);
            if (su) HandPose.Apply(frazier.transform, suction.tip + DisplayOffset(suction.tip), suction.axis, suction.frameZ);
            bool hk = activeTool == ToolKind.Hook && hook.visible;
            hookGo.SetActive(hk);
            // while dragging, the hook tip is already at the deformed target
            if (hk) HandPose.Apply(hookGo.transform, hook.tip + (nodeA >= 0 ? Vector3.zero : DisplayOffset(hook.tip)), hook.axis, hook.frameZ);
            // the non-dominant hand
            bool fb = showB && toolB == HandBKind.Forceps && forcepsB.visible;
            forcepsBArmA.SetActive(fb); forcepsBArmB.SetActive(fb);
            if (fb)
            {
                var off = DisplayOffset(forcepsB.Mid);
                HandPose.Apply(forcepsBArmA.transform, forcepsB.tipA + off, forcepsB.armAy, forcepsB.armAz);
                HandPose.Apply(forcepsBArmB.transform, forcepsB.tipB + off, forcepsB.armBy, forcepsB.armBz);
            }
            bool hb = showB && toolB == HandBKind.Hook && hookB.visible;
            hookGoB.SetActive(hb);
            if (hb) HandPose.Apply(hookGoB.transform, hookB.tip + (nodeB >= 0 ? Vector3.zero : DisplayOffset(hookB.tip)), hookB.axis, hookB.frameZ);
            bool sb = showB && toolB == HandBKind.Suction && suctionB.visible;
            frazierB.SetActive(sb);
            if (sb) HandPose.Apply(frazierB.transform, suctionB.tip + DisplayOffset(suctionB.tip), suctionB.axis, suctionB.frameZ);

            // arc / spark light at the tip (bloom picks up the emissive particles; this lights the wound)
            bool arcOn = mode != null && physics.readout.hasHit && tool.visible;
            arcLight.enabled = arcOn;
            if (arcOn)
            {
                arcLight.transform.localPosition = tool.tip + Vector3.up * 1f + DisplayOffset(tool.tip);
                float flicker = 0.7f + 0.3f * UnityEngine.Random.value;
                arcLight.intensity = (mode.isCut ? 25f : 60f) * flicker;
            }
        }

        void SetButtonGlow(MeshRenderer mr, bool on, Color c)
        {
            mr.GetPropertyBlock(buttonMpb);
            buttonMpb.SetColor("_EmissiveColor", on ? c * 40f : Color.black);
            mr.SetPropertyBlock(buttonMpb);
        }

        /// <summary>Manual depth of field: sharp around the tip, soft beyond (surgeon's eye, loupes off).</summary>
        void UpdateFocus()
        {
            if (dof == null) return;
            float dM = orbit.distanceMm * 0.001f;
            dof.nearFocusStart.Override(0f); dof.nearFocusEnd.Override(dM * 0.45f);
            dof.farFocusStart.Override(dM * 1.35f); dof.farFocusEnd.Override(dM * 3.5f);
        }

        void HandleKeys()
        {
            bool shift = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
            if (Input.GetKeyDown(KeyCode.Alpha1)) gen.cutMode = ModeId.Pure;
            if (Input.GetKeyDown(KeyCode.Alpha2)) gen.cutMode = ModeId.Blend;
            if (Input.GetKeyDown(KeyCode.Alpha3)) gen.cutMode = ModeId.Low;
            if (Input.GetKeyDown(KeyCode.Alpha4)) gen.cutMode = ModeId.Valleylab;
            if (Input.GetKeyDown(KeyCode.Alpha5)) gen.coagMode = ModeId.Fulgurate;
            if (Input.GetKeyDown(KeyCode.Alpha6)) gen.coagMode = ModeId.Spray;
            if (Input.GetKeyDown(KeyCode.LeftBracket)) gen.Adjust(true, shift ? -1 : -5);
            if (Input.GetKeyDown(KeyCode.RightBracket)) gen.Adjust(true, shift ? 1 : 5);
            if (Input.GetKeyDown(KeyCode.Semicolon)) gen.Adjust(false, shift ? -1 : -5);
            if (Input.GetKeyDown(KeyCode.Quote)) gen.Adjust(false, shift ? 1 : 5);
            gen.Adjust(true, 0); gen.Adjust(false, 0);
            if (Input.GetKeyDown(KeyCode.B)) pencilElectrode = ElectrodeType.Blade;
            if (Input.GetKeyDown(KeyCode.N)) pencilElectrode = ElectrodeType.Needle;
            if (Input.GetKeyDown(KeyCode.O)) pencilElectrode = ElectrodeType.Ball;
            if (Input.GetKeyDown(KeyCode.Tab) || Input.GetKeyDown(KeyCode.Q))
            {
                if (shift) { toolB = HandPose.OrderB[(Array.IndexOf(HandPose.OrderB, toolB) + 1) % HandPose.OrderB.Length]; hands.ReleaseB(); nodeB = -1; liftB = 0; }
                else { activeTool = HandPose.Order[(Array.IndexOf(HandPose.Order, activeTool) + 1) % HandPose.Order.Length]; nodeA = -1; liftA = 0; }
            }
            if (Input.GetKeyDown(KeyCode.T)) { forceps.toothed = !forceps.toothed; forcepsB.toothed = forceps.toothed; }
            if (Input.GetKeyDown(KeyCode.R)) tool.roll += Mathf.PI / 4;
            if (Input.GetKeyDown(KeyCode.C)) { if (shift) ui.largeSection = !ui.largeSection; else physics.sliceMode = (physics.sliceMode + 1) % 6; }
            if (Input.GetKeyDown(KeyCode.K)) { if (shift) peelSweep = !peelSweep; else SetPeelMode((peelMode + 1) % 3); }
            if (Input.GetKeyDown(KeyCode.Comma) || Input.GetKeyDown(KeyCode.PageDown)) StepPeel(shift ? 0.1f : 0.5f);
            if (Input.GetKeyDown(KeyCode.Period) || Input.GetKeyDown(KeyCode.PageUp)) StepPeel(shift ? -0.1f : -0.5f);
            if (Input.GetKeyDown(KeyCode.H)) physics.sliceEnabled = !physics.sliceEnabled;
            if (Input.GetKeyDown(KeyCode.J)) slicePerp = !slicePerp;
            if (Input.GetKeyDown(KeyCode.Y)) InputState.invertTiltY = !InputState.invertTiltY;
            if (Input.GetKeyDown(KeyCode.F1)) ui.showBrief = !ui.showBrief;
            if (Input.GetKeyDown(KeyCode.F2)) ui.showHelp = !ui.showHelp;
            if (Input.GetKeyDown(KeyCode.F3)) ui.showPerf = !ui.showPerf;
            if (Input.GetKeyDown(KeyCode.Minus) || Input.GetKeyDown(KeyCode.KeypadMinus)) ui.ZoomStep(1);
            if (Input.GetKeyDown(KeyCode.Equals) || Input.GetKeyDown(KeyCode.KeypadPlus)) ui.ZoomStep(-1);
            if (Input.GetKeyDown(KeyCode.F7)) ToggleSetting("depthOfField");
            if (Input.GetKeyDown(KeyCode.F8)) ToggleSetting("antialiasing");
            if (Input.GetKeyDown(KeyCode.F9)) ui.showSettings = !ui.showSettings;
            if (Input.GetKeyDown(KeyCode.G)) ToggleSetting("tipGauge");
            if (Input.GetKeyDown(KeyCode.V)) SetOverlayMode((overlayMode + 1) % 3);
            if (Input.GetKeyDown(KeyCode.P)) StartReplay("Paused", "Replay of the last " + replay.Seconds.ToString("0") + " seconds.", null);
        }

        // ------------------------------------------------------------------ depth peel
        // A clip plane in the surface extraction only: the physics, the pick and the instruments keep the whole bed.
        // Block cut hides everything above the plane, so the cut face shows each layer and what the energy did at that
        // depth; dissection strips skin, fat, septa and fasciae and leaves vessels, the nerve, the lipoma and any heated
        // tissue standing, so the architecture under the skin and the thermal injury can be seen in 3D.
        void SetPeelMode(int m)
        {
            peelMode = Mathf.Clamp(m, 0, 2);
            if (peelMode == 0) peelSweep = false;
            else if (peelMm <= 0.05f) peelMm = 3f; // start below the dermis so the first view already shows the fat
        }

        void StepPeel(float dMm)
        {
            if (peelMode == 0) SetPeelMode(1);
            peelSweep = false;
            peelMm = Mathf.Clamp(peelMm + dMm, 0, volume.SizeDepth);
        }

        /// <summary>Hands the peel plane to the surface extractor and the section; a sweep glides it down and up the bed.</summary>
        void ApplyPeel(float dt)
        {
            if (peelSweep && peelMode != 0)
            {
                peelMm += peelSweepDir * 4f * dt;
                if (peelMm >= volume.SizeDepth) { peelMm = volume.SizeDepth; peelSweepDir = -1f; }
                else if (peelMm <= 0.5f) { peelMm = 0.5f; peelSweepDir = 1f; }
            }
            // quantised so a sweep rebuilds a band of bricks a few times a second, not every frame
            float q = peelSweep ? 0.25f : 0.05f;
            float mm = Mathf.Round(peelMm / q) * q;
            if (peelMode == shownPeelMode && mm == shownPeelMm) return;
            shownPeelMode = peelMode; shownPeelMm = mm;
            surface.SetClip(peelMode, Vector3.up, -mm);
            physics.peelY = peelMode != 0 ? -mm : 1e9f;
            surface.bloodGo.SetActive(peelMode == 0); // the pool lies on the original surface
        }

        // ------------------------------------------------------------------ settings and detail
        void LoadSettings()
        {
            string json = null;
            try { if (System.IO.File.Exists(SettingsPath)) json = System.IO.File.ReadAllText(SettingsPath); }
            catch (Exception e) { Debug.LogWarning("[Bovie Sim] Could not read settings: " + e.Message); }
            settings = UserSettings.FromJson(json);
        }

        void SaveSettings()
        {
            try { System.IO.File.WriteAllText(SettingsPath, settings.ToJson()); }
            catch (Exception e) { Debug.LogWarning("[Bovie Sim] Could not save settings: " + e.Message); }
        }

        static DetailLevel ParseLevel(string s)
        {
            try { return (DetailLevel)Enum.Parse(typeof(DetailLevel), s ?? "Auto", true); }
            catch (Exception) { return DetailLevel.Auto; }
        }

        void ToggleSetting(string key)
        {
            switch (key)
            {
                case "teachingMoments": settings.teachingMoments = !settings.teachingMoments; break;
                case "tipGauge": settings.tipGauge = !settings.tipGauge; ui.showGauge = settings.tipGauge; break;
                case "depthOfField": settings.depthOfField = !settings.depthOfField; dofOn = settings.depthOfField; if (dof != null) dof.active = dofOn; break;
                case "antialiasing": settings.antialiasing = (settings.antialiasing + 1) % AaModes.Length; aaIndex = settings.antialiasing; if (hdCam != null) hdCam.antialiasing = AaModes[aaIndex]; break;
                case "invertTiltY": settings.invertTiltY = !settings.invertTiltY; InputState.invertTiltY = settings.invertTiltY; break;
                case "showTiming": settings.showTiming = !settings.showTiming; ui.showPerf = settings.showTiming; break;
            }
            SaveSettings();
        }

        /// <summary>Render-side detail: ambient occlusion and the surgical lights' shadow maps scale with the level.</summary>
        void ApplyRenderDetail()
        {
            if (postVolume != null && postVolume.sharedProfile != null)
            {
                ScreenSpaceAmbientOcclusion ao;
                if (postVolume.sharedProfile.TryGet(out ao)) ao.active = detail.ssao;
            }
            foreach (var n in new[] { "Surgical light A", "Surgical light B" })
            {
                var go = GameObject.Find(n); if (go == null) continue;
                var hd = go.GetComponent<HDAdditionalLightData>();
                if (hd != null) { hd.SetShadowResolutionOverride(true); hd.SetShadowResolution(detail.shadowResolution); }
                var l = go.GetComponent<Light>(); if (l != null) l.shadows = detail.softShadows ? LightShadows.Soft : LightShadows.Hard;
            }
        }

        void SetOverlayMode(int m)
        {
            overlayMode = overlayMat != null ? m : 0;
            if (overlayMat != null) overlayMat.SetFloat("_Mode", Mathf.Max(1, overlayMode));
            surface.SetOverlay(overlayMat, overlayMode > 0);
        }

        void UpdateGaugeAndBudget(float dt)
        {
            bool electrode = tool.visible;
            var sp = viewCamera.WorldToScreenPoint(field.TransformPoint(tool.tip + DisplayOffset(tool.tip)));
            ui.UpdateGauge(electrode && sp.z > 0 && (gen.activation != Activation.None || physics.readout.contactVoxels > 0 || hud.maxT > 40), new Vector2(sp.x, sp.y), hud);
            budget.Update(hud.frameMs, dt);
            if (budget.overBudget && !budgetDismissed && detail.level > DetailLevel.Low)
                ui.ShowBudgetWarning("At " + detail.Name + " detail this PC takes about " + hud.frameMs.ToString("0") + " ms per frame, over the " + detail.frameBudgetMs.ToString("0") + " ms budget (" + (detail.frameBudgetMs > 20 ? "30" : "60") + " fps). Restart at " + (DetailLevel)((int)detail.level - 1) + " detail?");
            else ui.ShowBudgetWarning(null);
            ui.RefreshSettings(settings, detail.level, detailReason, chosenLevel);
        }

        // ------------------------------------------------------------------ teaching moments (pause and replay)
        static string MomentTitle(string type)
        {
            switch (type)
            {
                case "tissue.carbonized": return "Char";
                case "vessel.rebleed": return "Rebleed from a char plug";
                case "lesion.breached": return "Lipoma capsule entered";
                case "tissue.nerveContact": return "Energy next to a nerve";
                default: return null;
            }
        }

        void OnTeachingMoment(SimEvent e)
        {
            if (!settings.teachingMoments || paused || replay == null || !replay.supported || replay.Count < 10) return;
            string title = MomentTitle(e.type); if (title == null) return;
            float last; if (lastMoment.TryGetValue(e.type, out last) && Time.unscaledTime - last < 30f) return;
            lastMoment[e.type] = Time.unscaledTime;
            StartReplay(title, e.detail, e.teaching);
        }

        void StartReplay(string title, string detailText, string teaching)
        {
            if (replay == null || replay.Count == 0) return;
            paused = true; replay.recording = false; replayT = 0;
            gen.activation = Activation.None; audioOut.Set(500, false);
            ui.ShowReplay(title, detailText, teaching);
        }

        void Resume()
        {
            paused = false; ui.HideReplay();
            replay.Clear(); replay.recording = true;
        }

        void UpdatePaused()
        {
            if (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter) || Input.GetKeyDown(KeyCode.P)) { Resume(); return; }
            // play once at real speed, hold the last frame for a second, loop
            replayT += Time.unscaledDeltaTime;
            float len = replay.Seconds;
            if (replayT > len + 1f) replayT = 0;
            float f = Mathf.Clamp01(replayT / Mathf.Max(len, 0.01f));
            ui.SetReplayFrame(replay.Frame(Mathf.Min(replay.Count - 1, (int)(f * replay.Count))), f);
            ui.Refresh(hud, Metric);
        }

        void FillHud(SimReadout r)
        {
            var d = hud;
            d.scenarioName = scenario.name;
            string handA = HandPose.Label(activeTool) + (activeTool == ToolKind.Pencil ? " · " + pencilElectrode.ToString().ToLowerInvariant()
                : activeTool == ToolKind.Forceps ? (forceps.closed ? " · closed" : " · open") + (forceps.toothed ? ", toothed" : ", smooth") + (liftA > 0 ? ", lifted " + liftA.ToString("0") + " mm" : "")
                : activeTool == ToolKind.Scalpel ? (scalpel.cutting ? " · cutting " + scalpel.Depth.ToString("0.0") + " mm deep" : " · hold to cut")
                : activeTool == ToolKind.Scissors ? (scissors.spreading ? " · spreading " + scissors.open.ToString("0") + " mm" : " · click snips, Ctrl + hold spreads") : "");
            string handB = toolB == HandBKind.None ? "none (Shift+Tab)" : HandPose.Label(toolB) + (hands.stateB == HandBState.Live ? (hands.engagedB ? " · holding" : " · live") : hands.stateB == HandBState.Latched ? " · latched, holding" + (liftB > 0 ? ", lifted " + liftB.ToString("0") + " mm" : "") : " · parked (hold Space)");
            d.toolText = handA + "   ·   left: " + handB;
            d.oozing = r.oozeVoxels; d.cutVx = physics.totalCutVoxels; d.bluntVx = physics.totalBluntVoxels;
            d.activation = gen.activation; d.activeWatts = gen.ActiveWatts; d.remFault = gen.remFault;
            d.zOhm = r.zOhm; d.powerW = r.powerW; d.vRms = r.vRms; d.contact = r.contactVoxels;
            d.underTip = r.hasHit ? TissueTable.Name((int)Cell.Type(r.hitCell)) + " · " + TissueTable.StateNames[(int)Cell.State(r.hitCell)] : "—";
            d.maxT = r.maxTempC; d.carbon = r.carbonVoxels; d.vaporised = physics.totalVapVoxels; d.simTime = physics.simTime;
            maxCharMm3 = Mathf.Max(maxCharMm3, r.carbonVoxels * volume.h * volume.h * volume.h); d.charMm3 = maxCharMm3;
            d.bloodLost = r.bloodLostMl; d.pooled = r.poolMl; d.exposedArt = r.exposedArterial; d.exposedVen = r.exposedVenous;
            d.sealedVx = r.sealedVoxels; d.charPlug = r.charPlugVoxels; d.rebleeds = r.rebleeds;
            if (physics.rimSamples > 0) maxRimMm = Mathf.Max(maxRimMm, physics.rimMm);
            d.rimMm = maxRimMm;
            d.capsuleText = lattice != null && lattice.capsuleTotal > 0 ? lattice.capsuleAlive + " of " + lattice.capsuleTotal + " links attached" + (lattice.lipomaFree ? " · FREE, lifted " + clusterRiseMm.ToString("0.0") + " mm" : "") : "";
            d.volumeText = "v" + BovieVersion.Version + " · " + detail.Name + " · " + volume.nx + "×" + volume.ny + "×" + volume.nz + " voxels at " + volume.h.ToString("0.00") + " mm" + (overlayMode == 1 ? " · teaching view: temperature" : overlayMode == 2 ? " · teaching view: damage" : "");
            d.tris = surface.lastVertexCount / 3; d.capped = surface.capped; d.fps = fps;
            d.frameMs = fps > 0 ? 1000f / fps : 0; d.cpuMs = cpuMs; d.gpuMs = gpuMs;
            d.stateAge = physics.stateAgeFrames; d.heightsAge = picker.heightsAgeFrames; d.surfaceAge = surface.lastAgeFrames;
            d.sliceTopY = physics.sliceOrigin.y; d.slicePerp = slicePerp;
            d.peelMode = peelMode; d.peelMm = peelMm; d.bedDepthMm = volume.SizeDepth; d.peelSweep = peelSweep;
            UpdateLayerLabels(physics.sliceOrigin.x, physics.sliceOrigin.z);
        }

        /// <summary>Values for the scenario's success criteria (education.success metrics); NaN = not available yet.</summary>
        float Metric(string m)
        {
            var r = physics.readout;
            switch (m)
            {
                case "bloodLostMl": return r.bloodLostMl;
                case "charVolumeMm3": return maxCharMm3;
                case "dermalEdgeDamageMm": return maxRimMm > 0 ? maxRimMm : float.NaN;
                case "exposedBleeding": return r.exposedArterial + r.exposedVenous;
                case "charPlugVoxels": return r.charPlugVoxels;
                case "lesionDelivered": return lesionDelivered ? 1 : 0;
                case "lesionBreaches": return lesionBreachReported ? 1 : 0;
                default: return float.NaN;
            }
        }

        /// <summary>Layer names and their depth below the original skin surface, down the section's centre column (pre-cut anatomy).</summary>
        void UpdateLayerLabels(float x, float z)
        {
            float vi, vj, vk; volume.WorldToVoxel(x, 0, z, out vi, out vj, out vk);
            int i = Mathf.Clamp(Mathf.RoundToInt(vi), 0, volume.nx - 1), j = Mathf.Clamp(Mathf.RoundToInt(vj), 0, volume.ny - 1);
            int col = i + volume.nx * j;
            if (col == layerColumn) return;
            layerColumn = col;
            hud.layers.Clear();
            string last = null;
            for (int k = volume.nz - 1; k >= 0; k--)
            {
                var t = Cell.Type(volume.cell[volume.Index(i, j, k)]);
                string name;
                switch (t)
                {
                    case Tissue.Epidermis: name = "Epidermis"; break;
                    case Tissue.Dermis: name = "Dermis"; break;
                    case Tissue.Fat: name = "Subcutaneous fat"; break;
                    case Tissue.SuperficialFascia: name = "Scarpa's fascia"; break;
                    case Tissue.DeepFascia: name = "Deep fascia"; break;
                    case Tissue.Muscle: name = "Muscle"; break;
                    case Tissue.Lipoma: name = "Lipoma"; break;
                    default: name = null; break; // air, septa, vessels, nerve, capsule: too thin or local to label
                }
                if (name == null || name == last) continue;
                float wx, wy, wz; volume.VoxelToWorld(i, j, k, out wx, out wy, out wz);
                hud.layers.Add(new System.Collections.Generic.KeyValuePair<string, float>(name, -(wy + 0.5f * volume.h)));
                last = name;
            }
        }

        void Reload(string id)
        {
            requestedScenario = id;
            SceneManager.LoadScene(SceneManager.GetActiveScene().buildIndex >= 0 ? SceneManager.GetActiveScene().name : "Main");
        }

        void OnGUI()
        {
            if (!ready || ui == null)
            {
                GUI.Label(new Rect(20, 20, Screen.width - 40, 60), "<b>Bovie Sim</b>\n" + status, new GUIStyle(GUI.skin.label) { richText = true, fontSize = 16 });
                return;
            }
        }

        void OnDestroy()
        {
            if (physics != null) physics.Dispose();
            if (surface != null) surface.Dispose();
            if (particles != null) particles.Dispose();
            if (gpu != null) gpu.Dispose();
            if (ui != null) ui.Dispose();
            if (replay != null) replay.Dispose();
        }
    }
}
