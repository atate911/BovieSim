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
        readonly ElectrodeTool tool = new ElectrodeTool(); readonly ForcepsTool forceps = new ForcepsTool();
        readonly SuctionTool suction = new SuctionTool(); readonly HookTool hook = new HookTool();
        ToolKind activeTool = ToolKind.Pencil; ElectrodeType pencilElectrode = ElectrodeType.Blade;
        bool slicePerp, lesionFreedReported, lesionDelivered, lesionBreachReported, ready;
        float clusterRiseMm, liftMm; string status = "Building anatomy…";
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
        static string SettingsPath { get { return System.IO.Path.Combine(Application.persistentDataPath, "settings.json"); } }

        // ---- scene objects
        Transform field;
        GameObject pencil, forcepsA, forcepsB, frazier, hookGo, drapes;
        MeshFilter electrodeFilter; Mesh bladeMesh, needleMesh, ballMesh;
        MeshRenderer cutButton, coagButton; MaterialPropertyBlock buttonMpb;
        Light arcLight; DepthOfField dof;
        // render-side effects driven by detail level and the ray-tracing tier (runtime copies of the volume profile)
        ScreenSpaceAmbientOcclusion ao; ScreenSpaceReflection ssr; GlobalIllumination gi; SubSurfaceScattering sssRt; ContactShadows contactShadows;
        readonly System.Collections.Generic.List<HDAdditionalLightData> headLights = new System.Collections.Generic.List<HDAdditionalLightData>();
        int rtTier; string rtText = "";
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
            physics = new PhysicsPipeline(simCs, gpu, detail.maxSubdomain);
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
            if (postVolume != null && postVolume.sharedProfile != null)
            {
                // the volume's runtime copy: focus and ray-tracing changes must not be written into the generated asset
                var prof = postVolume.profile;
                prof.TryGet(out dof); prof.TryGet(out ao); prof.TryGet(out ssr); prof.TryGet(out gi); prof.TryGet(out sssRt); prof.TryGet(out contactShadows);
            }
            if (dof != null) dof.active = dofOn; // off by default: blur reads as lag when the view moves
            ApplyRenderDetail();
            status = "";
            ready = true;
        }

        /// <summary>
        /// Epidermis textures are baked for one skin tone and prep; tint runtime copies of those materials so the
        /// skin matches this scenario's tone and prep (chlorhexidine, povidone-iodine or none). The ratio is taken in
        /// linear space because HDRP multiplies the base map by _BaseColor there, then converted back to sRGB because
        /// Unity gamma-decodes every non-HDR colour property on upload in a linear-colour-space project.
        /// </summary>
        Material[] SkinCorrectedMaterials()
        {
            var mats = (Material[])assets.tissue.Clone();
            Color want = TissuePalette.Skin(scenario.skin.tone) * TissuePalette.Prep(scenario.skin.prep);
            Color baked = TissuePalette.Skin(TissuePalette.BakedTone) * TissuePalette.Prep(TissuePalette.BakedPrep);
            Color w = want.linear, b = baked.linear;
            var k = new Color(Mathf.Clamp(w.r / Mathf.Max(b.r, 1e-3f), 0, 2), Mathf.Clamp(w.g / Mathf.Max(b.g, 1e-3f), 0, 2), Mathf.Clamp(w.b / Mathf.Max(b.b, 1e-3f), 0, 2), 1).gamma;
            foreach (int bucket in new[] { 0, 8 }) // epidermis, and coagulated epidermis (half strength: blanching hides the stain)
            {
                if (mats[bucket] == null) continue;
                var m = new Material(mats[bucket]) { name = mats[bucket].name + " (scenario skin)" };
                m.SetColor("_BaseColor", bucket == 0 ? k : Color.Lerp(Color.white, k, 0.5f));
                mats[bucket] = m;
            }
            return mats;
        }

        GameObject MakePart(string name, Mesh mesh, Material mat, Transform parent) { return MakePart(name, mesh, new[] { mat }, parent); }

        GameObject MakePart(string name, Mesh mesh, Material[] mats, Transform parent)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterials = mats;
            // instruments move, drapes do not: HDRP keeps their ray-tracing acceleration structures accordingly
            mr.rayTracingMode = UnityEngine.Experimental.Rendering.RayTracingMode.DynamicTransform;
            return go;
        }

        void BuildInstruments()
        {
            drapes = MakePart("Drapes", MeshFactory.Drapes(volume.SizeX / 2, volume.SizeZ / 2), new[] { assets.drapeFilm, assets.drapePad, assets.drape }, field);
            drapes.GetComponent<MeshRenderer>().rayTracingMode = UnityEngine.Experimental.Rendering.RayTracingMode.Static;
            BuildLampDomes();
            BuildDressing();
            pencil = new GameObject("Bovie pencil"); pencil.transform.SetParent(field, false);
            MakePart("Body", MeshFactory.PencilBody(), assets.pencilBody, pencil.transform);
            cutButton = MakePart("Cut button", MeshFactory.PencilButton(44, 58), assets.pencilCutButton, pencil.transform).GetComponent<MeshRenderer>();
            coagButton = MakePart("Coag button", MeshFactory.PencilButton(62, 76), assets.pencilCoagButton, pencil.transform).GetComponent<MeshRenderer>();
            bladeMesh = MeshFactory.Blade(); needleMesh = MeshFactory.Needle(); ballMesh = MeshFactory.Ball();
            electrodeFilter = MakePart("Electrode", bladeMesh, assets.steel, pencil.transform).GetComponent<MeshFilter>();
            // arm A sits on the -Z side of the jaw centre and arm B on +Z. Each arm's local +Z (HandPose: cross(x, arm axis))
            // points away from the jaw centre, so arm A's teeth go to local -Z and arm B's to local +Z to face each other.
            // An instrument pack in Assets/BovieSim/Models replaces the procedural arms (ProjectSetup.ImportInstrumentPack).
            bool packForceps = assets.forcepsArmA != null && assets.forcepsArmB != null && assets.packSteel != null;
            forcepsA = MakePart("Adson forceps A", packForceps ? assets.forcepsArmA : MeshFactory.ForcepsArm(forceps.armLength, false, 1), packForceps ? assets.packSteel : assets.steel, field);
            forcepsB = MakePart("Adson forceps B", packForceps ? assets.forcepsArmB : MeshFactory.ForcepsArm(forceps.armLength, true, 2), packForceps ? assets.packSteel : assets.steel, field);
            frazier = MakePart("Frazier suction", MeshFactory.Frazier(), assets.steel, field);
            hookGo = MakePart("Skin hook", MeshFactory.SkinHook(), assets.steel, field);
            foreach (var go in new[] { pencil, forcepsA, forcepsB, frazier, hookGo })
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

        /// <summary>Pack instruments resting on the drape beside the field (visual only), seated on the drape's own height.</summary>
        void BuildDressing()
        {
            if (assets.dressing == null) return;
            float ax = volume.SizeX / 2 - 2.5f, az = volume.SizeZ / 2 - 2.5f;
            foreach (var d in assets.dressing)
            {
                if (d == null || d.mesh == null) continue;
                var go = MakePart(d.name, d.mesh, d.material != null ? d.material : assets.packSteel != null ? assets.packSteel : assets.steel, field);
                float y = MeshFactory.DrapeHeight(d.positionMm.x, d.positionMm.z, ax, az, 6f);
                go.transform.localPosition = new Vector3(d.positionMm.x, y + 0.3f, d.positionMm.z);
                go.transform.localRotation = Quaternion.Euler(0, d.yawDeg, 0);
                go.GetComponent<MeshRenderer>().rayTracingMode = UnityEngine.Experimental.Rendering.RayTracingMode.Static;
            }
        }

        /// <summary>
        /// The operating-light heads themselves: an emissive dome on each surgical light so steel instruments and wet
        /// tissue reflect the lamps (screen-space or ray-traced reflections), as they do in theatre.
        /// </summary>
        void BuildLampDomes()
        {
            if (assets.lampDome == null) return;
            var mesh = MeshFactory.LampDome();
            foreach (var n in new[] { "Surgical light A", "Surgical light B" })
            {
                var lightGo = GameObject.Find(n); if (lightGo == null || lightGo.transform.Find("Dome") != null) continue;
                var dome = MakePart("Dome", mesh, assets.lampDome, lightGo.transform);
                dome.transform.localRotation = Quaternion.Euler(90, 0, 0); // mesh +Y (the emissive face) toward the light's forward axis, i.e. the field
                var mr = dome.GetComponent<MeshRenderer>();
                mr.shadowCastingMode = ShadowCastingMode.Off;
                mr.rayTracingMode = UnityEngine.Experimental.Rendering.RayTracingMode.Static;
            }
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
            orbit.HandleInput(overUi);
            HandleKeys();

            // ---- generator activation: footswitch (keys or on-screen) or hand switch on the pencil
            bool lmb = input.leftHeld && !overUi && !orbit.Dragging;
            bool handCut = activeTool == ToolKind.Pencil && lmb && !input.ctrl;
            bool handCoag = activeTool == ToolKind.Pencil && lmb && input.ctrl;
            gen.activation = input.pedalCut || ui.pedalCut || handCut ? Activation.Cut : input.pedalCoag || ui.pedalCoag || handCoag ? Activation.Coag : Activation.None;

            var r = physics.readout;
            // same-frame pick on the CPU height map (the GPU pick answers a few frames late)
            Vector3 rayO, rayD; PickRay(out rayO, out rayD);
            Vector3 hit = Vector3.zero;
            bool hasHit = input.inside && picker.Raycast(rayO, rayD, out hit);
            bool pressed = lmb;
            tool.pressure = input.pressure; tool.tilt = input.tiltDeg;
            var camMm = orbit.PositionMm;
            bool tiltOn = input.HasTilt;
            bool suctionOn = false; Vector3 suctionAt = Vector3.zero;
            events.buzzing = false; events.suctionOn = false;

            // ---- tool placement (physics in material space; display shifted by the lattice displacement below)
            pencil.SetActive(activeTool == ToolKind.Pencil);
            forcepsA.SetActive(activeTool == ToolKind.Forceps); forcepsB.SetActive(activeTool == ToolKind.Forceps);
            frazier.SetActive(activeTool == ToolKind.Suction); hookGo.SetActive(activeTool == ToolKind.Hook);
            if (activeTool == ToolKind.Pencil)
            {
                tool.type = pencilElectrode;
                tool.Place(hasHit, hit, camMm, tiltOn);
                physics.SetSecondaryTool(Vector3.zero, Vector3.zero, 0, 0);
                if (lattice != null) lattice.attachments.Clear();
                hook.node = -1;
            }
            else if (activeTool == ToolKind.Forceps)
            {
                forceps.closed = pressed;
                // Space raises the closed jaws (traction: lifts a grasped edge or a freed lipoma); opening resets
                if (forceps.closed && Input.GetKey(KeyCode.Space)) liftMm = Mathf.Min(liftMm + 8f * frameDt, 15f);
                else if (!forceps.closed) liftMm = 0;
                forceps.Place(hasHit, hit + Vector3.up * liftMm, camMm, tiltOn, input.tiltDeg);
                physics.SetSecondaryTool(forceps.tipA, forceps.tipB, forceps.jawRadius, forceps.visible && forceps.closed ? 1 : 0);
                // buzzing the forceps: closed jaws become the electrode; open jaws cannot be activated
                if (forceps.visible && forceps.closed) { tool.AsForceps(forceps.tipA, forceps.tipB, forceps.jawRadius, forceps.axis); events.buzzing = gen.activation != Activation.None; }
                else { tool.visible = false; gen.activation = Activation.None; }
                // forceps lift: closed jaws carry the nearest node (and with it a freed lipoma)
                if (lattice != null)
                {
                    if (forceps.closed && forceps.visible)
                    {
                        if (hook.node < 0) hook.node = lattice.NearestNode(forceps.Mid, 3);
                        lattice.attachments.Clear();
                        if (hook.node >= 0) lattice.attachments.Add(new Attachment { node = hook.node, target = forceps.Mid + forceps.axis * 1.0f, strength = 0.2f });
                    }
                    else { hook.node = -1; lattice.attachments.Clear(); }
                }
            }
            else if (activeTool == ToolKind.Suction)
            {
                suction.on = pressed;
                suction.Place(hasHit, hit, camMm, tiltOn, input.tiltDeg);
                physics.SetSecondaryTool(suction.tip, suction.tip, suction.drainRadius, suction.visible && suction.on ? 2 : 0);
                if (suction.visible && suction.on) { suctionOn = true; suctionAt = suction.tip; events.suctionOn = true; }
                tool.visible = false; gen.activation = Activation.None;
                if (lattice != null) lattice.attachments.Clear();
                hook.node = -1;
            }
            else
            {
                // skin hook: press to attach the nearest lattice node, drag to retract, release to let go
                bool dragging = false; Vector3 dragTarget = Vector3.zero;
                if (pressed && lattice != null)
                {
                    if (hook.node < 0 && hasHit) hook.node = lattice.NearestNode(hit, 4);
                    if (hook.node >= 0)
                    {
                        Vector3 ro, rd; PickRay(out ro, out rd);
                        float planeY = lattice.mat[hook.node * 3 + 1] + 1.5f;
                        float tHit = Mathf.Abs(rd.y) > 1e-4f ? (planeY - ro.y) / rd.y : -1;
                        lattice.attachments.Clear();
                        if (tHit > 0) { dragging = true; dragTarget = ro + rd * tHit; lattice.attachments.Add(new Attachment { node = hook.node, target = dragTarget, strength = 0.25f }); }
                    }
                }
                else { hook.node = -1; if (lattice != null) lattice.attachments.Clear(); }
                hook.Place(dragging || hasHit, dragging ? dragTarget : hit, camMm, tiltOn, input.tiltDeg);
                physics.SetSecondaryTool(Vector3.zero, Vector3.zero, 0, 0);
                tool.visible = false; gen.activation = Activation.None;
            }

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
            string toolName = activeTool == ToolKind.Pencil ? tool.type.ToString().ToLowerInvariant() : activeTool.ToString().ToLowerInvariant();
            events.Update(physics.simTime, r, gen, toolName);

            // ---- mechanics: apply the last probe (severed / shrunk links), muscle twitch, step, upload
            if (lattice != null) StepMechanics(mode, r, frameDt);

            // ---- physics, then surface extraction when tissue or deformation changed
            bool hot = physics.Step(tool, gen, frameDt);
            // tissue changes where the electrode works every frame; the cooling region changes slowly (state, colour)
            int[] bmin, bmax;
            if (physics.ActiveBox(out bmin, out bmax)) surface.MarkVoxelBox(bmin, bmax);
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
            if (activeTool == ToolKind.Pencil)
            {
                pencil.SetActive(tool.visible);
                if (tool.visible)
                {
                    electrodeFilter.sharedMesh = pencilElectrode == ElectrodeType.Needle ? needleMesh : pencilElectrode == ElectrodeType.Ball ? ballMesh : bladeMesh;
                    // mesh frame: +Y along the handle axis, +Z the blade face normal (includes roll)
                    HandPose.Apply(pencil.transform, tool.tip + DisplayOffset(tool.tip), tool.axis, tool.ndir);
                }
                SetButtonGlow(cutButton, gen.activation == Activation.Cut, new Color(1f, 0.85f, 0.2f));
                SetButtonGlow(coagButton, gen.activation == Activation.Coag, new Color(0.25f, 0.55f, 1f));
            }
            else if (activeTool == ToolKind.Forceps)
            {
                forcepsA.SetActive(forceps.visible); forcepsB.SetActive(forceps.visible);
                if (forceps.visible)
                {
                    var off = DisplayOffset(forceps.Mid);
                    HandPose.Apply(forcepsA.transform, forceps.tipA + off, forceps.armAy, forceps.armAz);
                    HandPose.Apply(forcepsB.transform, forceps.tipB + off, forceps.armBy, forceps.armBz);
                }
            }
            else if (activeTool == ToolKind.Suction)
            {
                frazier.SetActive(suction.visible);
                if (suction.visible) HandPose.Apply(frazier.transform, suction.tip + DisplayOffset(suction.tip), suction.axis, suction.frameZ);
            }
            else
            {
                hookGo.SetActive(hook.visible);
                // while dragging, the hook tip is already at the deformed target
                if (hook.visible) HandPose.Apply(hookGo.transform, hook.tip + (hook.node >= 0 ? Vector3.zero : DisplayOffset(hook.tip)), hook.axis, hook.frameZ);
            }

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
            if (Input.GetKeyDown(KeyCode.Tab) || Input.GetKeyDown(KeyCode.Q)) activeTool = HandPose.Order[(Array.IndexOf(HandPose.Order, activeTool) + 1) % HandPose.Order.Length];
            if (Input.GetKeyDown(KeyCode.T)) forceps.toothed = !forceps.toothed;
            if (Input.GetKeyDown(KeyCode.R)) tool.roll += Mathf.PI / 4;
            if (Input.GetKeyDown(KeyCode.C)) physics.sliceMode = (physics.sliceMode + 1) % 6;
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
                case "rayTracing":
                {
                    var opts = UserSettings.RayTracingOptions;
                    settings.rayTracing = opts[(Mathf.Max(0, Array.IndexOf(opts, settings.rayTracing)) + 1) % opts.Length];
                    ApplyRayTracing();
                    break;
                }
            }
            SaveSettings();
        }

        /// <summary>Render-side detail: ambient occlusion and the surgical lights' shadow maps scale with the level; then the ray-tracing tier.</summary>
        void ApplyRenderDetail()
        {
            if (ao != null) ao.active = detail.ssao;
            headLights.Clear();
            foreach (var n in new[] { "Surgical light A", "Surgical light B" })
            {
                var go = GameObject.Find(n); if (go == null) continue;
                var hd = go.GetComponent<HDAdditionalLightData>();
                if (hd != null) { hd.SetShadowResolutionOverride(true); hd.SetShadowResolution(detail.shadowResolution); headLights.Add(hd); }
                var l = go.GetComponent<Light>(); if (l != null) l.shadows = detail.softShadows ? LightShadows.Soft : LightShadows.Hard;
            }
            ApplyRayTracing();
        }

        /// <summary>
        /// DirectX Raytracing by tier (Settings > Ray tracing; Auto follows the detail level): 1 ray-traced shadows
        /// from the light domes plus ray-traced ambient occlusion, 2 adds ray-traced reflections (steel, wet
        /// tissue, the lamps), 3 adds global illumination (blue drape and red wound bounce) and ray-traced
        /// subsurface scattering. Without DXR the raster effects stay: PCSS soft shadows, SSAO, and screen-space
        /// reflections from High detail up. The tissue meshes are rebuilt on the GPU, so they enter the acceleration
        /// structure as dynamic geometry (SurfaceRenderer.SetRayTracing).
        /// </summary>
        void ApplyRayTracing()
        {
            rtTier = ResolveRayTracing(out rtText);
            bool rt = rtTier > 0;
            foreach (var hd in headLights)
            {
                if (hd == null) continue;
                hd.useRayTracedShadows = rt;
                hd.numRayTracingSamples = rtTier >= 3 ? 8 : 4;
                hd.filterTracedShadow = true; hd.filterSizeTraced = 16;
            }
            if (ao != null) { ao.rayTracing.Override(rt); ao.rayLength.Override(0.03f); ao.sampleCount.Override(rtTier >= 3 ? 4 : 2); ao.denoise.Override(true); }
            if (ssr != null)
            {
                ssr.enabled.Override(detail.ssr || rtTier >= 2);
                ssr.tracing.Override(rtTier >= 2 ? RayCastingMode.RayTracing : RayCastingMode.RayMarching);
                ssr.mode.Override(UnityEngine.Rendering.HighDefinition.RayTracingMode.Performance);
                ssr.rayLength.Override(3f); ssr.clampValue.Override(2f); ssr.denoise.Override(true); ssr.denoiserRadius.Override(8);
            }
            if (gi != null)
            {
                gi.enable.Override(rtTier >= 3);
                gi.tracing.Override(RayCastingMode.RayTracing);
                gi.mode.Override(UnityEngine.Rendering.HighDefinition.RayTracingMode.Performance);
                gi.rayLength.Override(1.5f); gi.clampValue.Override(3f); gi.denoise.Override(true); gi.fullResolution.Override(false);
            }
            if (sssRt != null) { sssRt.rayTracing.Override(rtTier >= 3); sssRt.sampleCount.Override(2); }
            if (contactShadows != null) contactShadows.rayTracing.Override(rt);
            if (surface != null) surface.SetRayTracing(rt);
        }

        /// <summary>Ray-tracing tier 0-3 from the setting, the detail level and whether the GPU can ray trace at all; `text` describes it for the settings panel.</summary>
        int ResolveRayTracing(out string text)
        {
            string[] names = { "off", "shadows + ambient occlusion", "shadows, AO + reflections", "full (shadows, AO, reflections, global illumination, subsurface)" };
            if (!HardwareProfile.RayTracingSupported) { text = settings.rayTracing == "Off" ? "off" : "not available on this PC"; return 0; }
            int tier;
            switch (settings.rayTracing)
            {
                case "Off": tier = 0; break;
                case "Shadows": tier = 1; break;
                case "Reflections": tier = 2; break;
                case "Full": tier = 3; break;
                default: tier = detail.rayTracing; break;
            }
            text = (settings.rayTracing == "Auto" ? "Auto · " : "") + names[tier];
            return tier;
        }

        void SetOverlayMode(int m)
        {
            overlayMode = overlayMat != null ? m : 0;
            if (overlayMat != null) overlayMat.SetFloat("_Mode", Mathf.Max(1, overlayMode));
            surface.SetOverlay(overlayMat, overlayMode > 0);
        }

        void UpdateGaugeAndBudget(float dt)
        {
            bool electrode = tool.visible && (activeTool == ToolKind.Pencil || (activeTool == ToolKind.Forceps && forceps.closed));
            var sp = viewCamera.WorldToScreenPoint(field.TransformPoint(tool.tip + DisplayOffset(tool.tip)));
            ui.UpdateGauge(electrode && sp.z > 0 && (gen.activation != Activation.None || physics.readout.contactVoxels > 0 || hud.maxT > 40), new Vector2(sp.x, sp.y), hud);
            budget.Update(hud.frameMs, dt);
            if (budget.overBudget && !budgetDismissed && detail.level > DetailLevel.Low)
                ui.ShowBudgetWarning("At " + detail.Name + " detail this PC takes about " + hud.frameMs.ToString("0") + " ms per frame, over the " + detail.frameBudgetMs.ToString("0") + " ms budget (" + (detail.frameBudgetMs > 20 ? "30" : "60") + " fps). Restart at " + (DetailLevel)((int)detail.level - 1) + " detail?");
            else ui.ShowBudgetWarning(null);
            ui.RefreshSettings(settings, detail.level, detailReason, chosenLevel, rtText);
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
            d.toolText = HandPose.Label(activeTool) + (activeTool == ToolKind.Pencil ? " · " + pencilElectrode.ToString().ToLowerInvariant() : activeTool == ToolKind.Forceps ? (forceps.closed ? " · closed" : " · open") + (forceps.toothed ? ", toothed" : ", smooth") + (liftMm > 0 ? ", lifted " + liftMm.ToString("0") + " mm" : "") : "");
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
