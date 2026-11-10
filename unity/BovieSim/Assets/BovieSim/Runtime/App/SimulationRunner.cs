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
        public static readonly string[] ScenarioIds = { "freeplay", "hemostasis", "lipoma", "viscera" };
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
        readonly HemostatTool hemostat = new HemostatTool(); readonly GauzeTool gauze = new GauzeTool();
        readonly ScalpelTool scalpel = new ScalpelTool(); readonly ScissorsTool scissors = new ScissorsTool();
        readonly int[] hemoNodes = { -1, -1 }; StrandRenderer strands; MaterialPropertyBlock gauzeMpb; MeshRenderer gauzeRenderer;
        ToolKind activeTool = ToolKind.Pencil; ElectrodeType pencilElectrode = ElectrodeType.Blade;
        // ---- two hands: the right hand is the pointer and the left button; the left hand (the right button) holds a
        // hook, forceps or suction of its own and stays where it is put (Core/Hands.cs)
        readonly HandController hands = new HandController();
        readonly ForcepsTool forcepsL = new ForcepsTool(); readonly SuctionTool suctionL = new SuctionTool(); readonly HookTool hookL = new HookTool();
        HandBKind toolB = HandBKind.None;
        /// <summary>last pick for the right hand (the pointer serves one hand at a time; the other keeps its place)</summary>
        bool hasHitA; Vector3 hitA;
        /// <summary>the left hand: its pull target, the press it is dragged from, the node it holds and the offset from that node
        /// to the instrument (it is drawn on the tissue it caught, wherever the pull has taken it)</summary>
        Vector3 poseB, baseB, pressPointB, grabOffsetB; int nodeB = -1; float planeYB, liftSincePressB; bool pressValidB, leftClaimed, lastLeftDown, showB;
        /// <summary>pull strength of the left hand's hold: an incised edge follows about two thirds of the pull, intact skin barely moves</summary>
        const float HoldStrength = 0.4f;
        /// <summary>scissors: a quick click snips, a hold pushes the closed tips in</summary>
        float scissorsPressAt = -1f; bool scissorsSpreadUsed, lastPressedA;
        /// <summary>The kernels have one secondary-tool slot (grasp, suction, gauze, dissection): the right hand's instrument takes
        /// it when it needs it, otherwise the left hand's grasp or suction.</summary>
        struct Slot { public bool set, hasMotion, motionIsPoint; public Vector3 a, b, motion; public float r, pressure, absorb; public int mode; }
        Slot slotA, slotB;
        bool slicePerp, lesionFreedReported, lesionDelivered, lesionBreachReported, ready;
        float clusterRiseMm, liftMm; string status = "Building anatomy…";
        Vector4[] lastDisp; bool latticeMoved;
        float maxRimMm, maxCharMm3; int layerColumn = -1;
        /// <summary>cumulative vaporised viscera, mm3 (scenario metric)</summary>
        float visceraVapMm3;
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
        GameObject pencil, scalpelGo, scissorsGoA, scissorsGoB, forcepsA, forcepsB, hemoA, hemoB, frazier, gauzeGo, hookGo, drapes;
        GameObject forcepsLA, forcepsLB, frazierL, hookGoL;
        MeshFilter electrodeFilter; MeshRenderer electrodeRenderer; Mesh bladeMesh, needleMesh, ballMesh; Material[] bladeMats, needleMats, ballMats;
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
            physics = new PhysicsPipeline(simCs, gpu, detail.maxSubdomain, detail.sliceScale);
            physics.relaxSweeps = detail.relaxSweeps;
            surface = new SurfaceRenderer(surfCs, gpu, field, SkinCorrectedMaterials(), assets.blood);
            surface.maxBricksPerFrame = detail.bricksPerFrame;
            particles = new Particles(assets.particlesAlpha, assets.particlesAdditive);
            strands = new StrandRenderer(field, assets.strand != null ? assets.strand : assets.blood);
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
            int drapeSeed = scenario.vessels != null ? scenario.vessels.seed : 0;
            drapes = MakePart("Drapes", MeshFactory.Drapes(volume.SizeX / 2, volume.SizeZ / 2, drapeSeed), new[] { assets.drapeFilm, assets.drapePad, assets.drape }, field);
            drapes.GetComponent<MeshRenderer>().rayTracingMode = UnityEngine.Experimental.Rendering.RayTracingMode.Static;
            // four towels square the window on top of the drape, held by Backhaus clips at the corners
            var towelMat = assets.towel != null ? assets.towel : assets.drapePad;
            for (int k = 0; k < 4; k++)
                MakePart("Towel " + k, MeshFactory.Towel(k, volume.SizeX / 2, volume.SizeZ / 2, drapeSeed), towelMat, field).GetComponent<MeshRenderer>().rayTracingMode = UnityEngine.Experimental.Rendering.RayTracingMode.Static;
            var clipMesh = MeshFactory.TowelClip();
            for (int i = 0; i < 4; i++)
            {
                Vector3 cpos, cdir; MeshFactory.TowelClipPose(i, volume.SizeX / 2, volume.SizeZ / 2, drapeSeed, out cpos, out cdir);
                var clip = MakePart("Towel clip " + i, clipMesh, assets.packSteel != null ? assets.packSteel : assets.steel, field);
                clip.transform.localPosition = cpos;
                clip.transform.localRotation = Quaternion.LookRotation(Vector3.up, cdir); // local +Z (the clip's up) to +Y, local +Y along the diagonal
                clip.GetComponent<MeshRenderer>().rayTracingMode = UnityEngine.Experimental.Rendering.RayTracingMode.Static;
            }
            BuildLampDomes();
            BuildDressing();
            // modelled instruments (Assets/BovieSim/Models, fitted by ProjectSetup.ImportToolModels) replace the procedural parts they cover
            pencil = new GameObject("Bovie pencil"); pencil.transform.SetParent(field, false);
            ToolModel pb = assets.pencilModel, pc = assets.pencilCutModel, pg = assets.pencilCoagModel;
            if (pb != null && pb.IsSet) MakePart("Body", pb.mesh, pb.materials, pencil.transform); else MakePart("Body", MeshFactory.PencilBody(), assets.pencilBody, pencil.transform);
            cutButton = (pc != null && pc.IsSet ? MakePart("Cut button", pc.mesh, pc.materials, pencil.transform) : MakePart("Cut button", MeshFactory.PencilButton(44, 58), assets.pencilCutButton, pencil.transform)).GetComponent<MeshRenderer>();
            coagButton = (pg != null && pg.IsSet ? MakePart("Coag button", pg.mesh, pg.materials, pencil.transform) : MakePart("Coag button", MeshFactory.PencilButton(62, 76), assets.pencilCoagButton, pencil.transform)).GetComponent<MeshRenderer>();
            var steelOnly = new[] { assets.steel };
            bladeMesh = assets.bladeModel != null && assets.bladeModel.IsSet ? assets.bladeModel.mesh : MeshFactory.Blade(); bladeMats = assets.bladeModel != null && assets.bladeModel.IsSet ? assets.bladeModel.materials : steelOnly;
            needleMesh = assets.needleModel != null && assets.needleModel.IsSet ? assets.needleModel.mesh : MeshFactory.Needle(); needleMats = assets.needleModel != null && assets.needleModel.IsSet ? assets.needleModel.materials : steelOnly;
            ballMesh = assets.ballModel != null && assets.ballModel.IsSet ? assets.ballModel.mesh : MeshFactory.Ball(); ballMats = assets.ballModel != null && assets.ballModel.IsSet ? assets.ballModel.materials : steelOnly;
            var electrodeGo = MakePart("Electrode", bladeMesh, bladeMats, pencil.transform);
            electrodeFilter = electrodeGo.GetComponent<MeshFilter>(); electrodeRenderer = electrodeGo.GetComponent<MeshRenderer>();
            // arm A sits on the -Z side of the jaw centre and arm B on +Z. Each arm's local +Z (HandPose: cross(x, arm axis))
            // points away from the jaw centre, so arm A's teeth go to local -Z and arm B's to local +Z to face each other.
            // An instrument pack in Assets/BovieSim/Models replaces the procedural arms (ProjectSetup.ImportInstrumentPack).
            bool packForceps = assets.forcepsArmA != null && assets.forcepsArmB != null && assets.packSteel != null;
            forcepsA = MakePart("Adson forceps A", packForceps ? assets.forcepsArmA : MeshFactory.ForcepsArm(forceps.armLength, false, 1), packForceps ? assets.packSteel : assets.steel, field);
            forcepsB = MakePart("Adson forceps B", packForceps ? assets.forcepsArmB : MeshFactory.ForcepsArm(forceps.armLength, true, 2), packForceps ? assets.packSteel : assets.steel, field);
            var hArm = MeshFactory.HemostatArm(hemostat.armLength);
            hemoA = MakePart("Kelly hemostat A", hArm, assets.steel, field);
            hemoB = MakePart("Kelly hemostat B", hArm, assets.steel, field);
            frazier = assets.frazierModel != null && assets.frazierModel.IsSet ? MakePart("Frazier suction", assets.frazierModel.mesh, assets.frazierModel.materials, field) : MakePart("Frazier suction", MeshFactory.Frazier(), assets.steel, field);
            gauzeGo = MakePart("Gauze", MeshFactory.Gauze(gauze.radius), assets.drape != null ? assets.drape : assets.pencilBody, field);
            gauzeRenderer = gauzeGo.GetComponent<MeshRenderer>(); gauzeMpb = new MaterialPropertyBlock();
            hookGo = assets.skinHookModel != null && assets.skinHookModel.IsSet ? MakePart("Skin hook", assets.skinHookModel.mesh, assets.skinHookModel.materials, field) : MakePart("Skin hook", MeshFactory.SkinHook(), assets.steel, field);
            scalpelGo = MakePart("Scalpel", MeshFactory.Scalpel(), assets.steel, field);
            var sBlade = MeshFactory.ScissorsBlade(scissors.jawLength, scissors.shankLength);
            scissorsGoA = MakePart("Metzenbaum A", sBlade, assets.steel, field);
            scissorsGoB = MakePart("Metzenbaum B", sBlade, assets.steel, field);
            // the left hand's own set: the same meshes and materials as the right hand's
            Func<GameObject, string, GameObject> twin = (src, name) => MakePart(name, src.GetComponent<MeshFilter>().sharedMesh, src.GetComponent<MeshRenderer>().sharedMaterials, field);
            forcepsLA = twin(forcepsA, "Adson forceps (left hand) A"); forcepsLB = twin(forcepsB, "Adson forceps (left hand) B");
            frazierL = twin(frazier, "Frazier suction (left hand)"); hookGoL = twin(hookGo, "Skin hook (left hand)");
            foreach (var go in new[] { forcepsLA, forcepsLB, frazierL, hookGoL }) go.SetActive(false);
            foreach (var go in new[] { pencil, scalpelGo, scissorsGoA, scissorsGoB, forcepsA, forcepsB, hemoA, hemoB, frazier, gauzeGo, hookGo, forcepsLA, forcepsLB, frazierL, hookGoL })
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
                float y = MeshFactory.DrapeHeight(d.positionMm.x, d.positionMm.z, ax, az, 6f, scenario.vessels != null ? scenario.vessels.seed : 0);
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
            // same-frame pick on the CPU height map (the GPU pick answers a few frames late). The ray is marched through the
            // field as it is seen (displaced by the lattice), so the hit is a world point on the deformed tissue: under a
            // retracted edge it is the wound floor. Instruments are posed there; the kernels get material coordinates.
            Vector3 rayO, rayD; PickRay(out rayO, out rayD);
            Vector3 hit = Vector3.zero;
            bool hasHit = input.inside && picker.Raycast(rayO, rayD, out hit, lattice != null ? (Func<Vector3, Vector3>)lattice.WorldToMaterial : null);
            // a right-button press on the tissue (or on the held instrument) is the left hand's; anywhere else it orbits the camera
            if (input.rightDown && !overUi) leftClaimed = toolB != HandBKind.None && (hasHit || RayNearHeld(rayO, rayD));
            if (!input.rightHeld) leftClaimed = false;
            bool leftDown = toolB != HandBKind.None && leftClaimed && input.rightHeld;
            // Ctrl + wheel scrubs the depth peel; while the left hand is being placed the wheel lifts what it holds; else it zooms
            bool ctrlHeld = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
            bool lifting = leftDown && hands.engagedB;
            orbit.HandleInput(overUi, !ctrlHeld && !lifting, leftClaimed);
            if (ctrlHeld && !overUi) { float wheel = Input.mouseScrollDelta.y; if (Mathf.Abs(wheel) > 0.01f) StepPeel(-wheel * 0.25f); }
            else if (lifting && !overUi && Mathf.Abs(input.wheel) > 0.01f) liftSincePressB = Mathf.Clamp(liftSincePressB + input.wheel * 0.5f, -5f, 15f);
            HandleKeys();
            ApplyPeel(frameDt);

            // ---- generator activation: footswitch (keys or on-screen) or hand switch on the pencil
            bool lmb = input.leftHeld && !overUi && !orbit.Dragging;
            bool handCut = activeTool == ToolKind.Pencil && lmb && !input.ctrl;
            bool handCoag = activeTool == ToolKind.Pencil && lmb && input.ctrl;
            gen.activation = input.pedalCut || ui.pedalCut || handCut ? Activation.Cut : input.pedalCoag || ui.pedalCoag || handCoag ? Activation.Coag : Activation.None;

            var r = physics.readout;
            bool pressed = lmb;
            // the pointer serves one hand at a time; the right hand keeps its place while the left is being put down
            if (!leftDown) { hasHitA = hasHit; hitA = hit; }
            tool.pressure = input.pressure; tool.tilt = input.tiltDeg;
            var camMm = orbit.PositionMm;
            bool tiltOn = input.HasTilt;
            bool suctionOn = false; Vector3 suctionAt = Vector3.zero;
            events.buzzing = false; events.suctionOn = false; events.gauzeDab = false; events.gauzeWipe = false; events.gauzeSaturated = gauze.Saturated; events.instrumentContact = false;
            if (lattice != null) lattice.attachments.Clear();
            slotA = new Slot(); slotB = new Slot();

            // ---- instrument placement: where they are seen; the kernels get the same points in material coordinates below
            PlaceLeftHand(leftDown, hasHit, hit, rayO, rayD, camMm, frameDt, ref suctionOn, ref suctionAt);
            PlaceRightHand(pressed, camMm, tiltOn, frameDt, rayO, rayD, r, ref suctionOn, ref suctionAt);
            tool.physTip = ToMat(tool.tip);
            var slot = slotA.set ? slotA : slotB;
            if (slot.set) physics.SetSecondaryTool(ToMat(slot.a), ToMat(slot.b), slot.r, slot.mode); else physics.SetSecondaryTool(Vector3.zero, Vector3.zero, 0, 0);
            if (slot.set && slot.hasMotion) physics.SetSecondaryMotion(slot.motionIsPoint ? ToMat(slot.motion) : slot.motion, slot.pressure, slot.absorb);
            else physics.SetSecondaryMotion(Vector3.zero, 0, 1);

            var mode = gen.ActiveMode;
            audioOut.Set(mode != null ? mode.toneHz : 500, mode != null && !gen.remFault);

            // ---- bleeding droplets, steam, smoke and arcs from the last readout
            // the kernels report material points: they are shown where the tissue is
            for (int n = 0; n < r.bleedCount; n++) { var bp = r.bleedPoints[n]; particles.Bleed(ToWorld(new Vector3(bp.x, bp.y, bp.z)), bp.w, frameDt); }
            for (int n = 0; n < r.spatterCount; n++) { var sp = r.spatterPoints[n]; particles.Spatter(ToWorld(new Vector3(sp.x, sp.y, sp.z)), sp.w); }
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
                    var end = new Vector3(hitA.x + Mathf.Cos(ang) * rad, hitA.y + 0.05f, hitA.z + Mathf.Sin(ang) * rad);
                    var start = tool.tip + tool.axis * (0.4f + UnityEngine.Random.value * 1.2f);
                    particles.Arc(start, end);
                }
            }
            particles.Update(frameDt, suctionOn, suctionAt, volume.SurfaceY);
            foreach (var l in particles.landings) { var m = ToMat(new Vector3(l.x, l.y, l.z)); physics.pendingDrops.Add(new Vector4(m.x, m.y, m.z, l.w)); }
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
            var hitMat = hasHitA ? ToMat(hitA) : tool.physTip;
            surface.Update(hitMat);
            picker.Refresh(physics, hot || latticeMoved);
            surface.UpdateBlood();

            // ---- pick ray for the next frame and cross-section plane through the tip. The GPU pick (tissue type under the
            // tip for the HUD and events) marches the voxel bed, so it gets a short ray onto the material point under the cursor
            if (hasHitA) physics.SetPickRay(hitMat - rayD * 0.75f, rayD); else physics.SetPickRay(rayO, rayD);
            var right = new Vector3(Mathf.Cos(orbit.azimuth), 0, -Mathf.Sin(orbit.azimuth));
            if (slicePerp && tool.visible)
            {
                var ax = new Vector3(tool.axis.x, 0, tool.axis.z);
                if (ax.magnitude > 1e-3f) { ax.Normalize(); right = new Vector3(-ax.z, 0, ax.x); }
            }
            // the section samples the voxel bed: its origin is the material point under the cursor
            var anchor = hasHitA ? hitMat : Vector3.zero;
            // the view starts 1.5 mm above the higher of the tip and the original skin, so the skin is always in view
            physics.sliceOrigin = new Vector3(anchor.x, Mathf.Max(hasHitA ? anchor.y : 0, 0) + 1.5f, anchor.z);
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
            byte[] flags; float[] om, dry;
            if (physics.TakeProbe(out flags, out om, out dry)) lattice.ApplyProbe(flags, om, dry);
            if (mode != null && !mode.isCut && r.muscleHeatVoxels > 20 && tool.visible) lattice.Stimulate(tool.tip, Mathf.Min(2, 0.5f + r.iRms * 2), physics.simTime);
            lattice.Step(frameDt, physics.simTime);
            // links torn by traction carve the tissue they ran through; yielded links show as strands
            physics.pendingTears.AddRange(lattice.TakeTears());
            strands.Update(lattice);
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

        /// <summary>World (as seen) to material (where the voxels are); the identity until tissue is displaced.</summary>
        Vector3 ToMat(Vector3 p) { return lattice != null ? lattice.WorldToMaterial(p) : p; }
        Vector3 ToWorld(Vector3 p) { return lattice != null ? lattice.MaterialToWorld(p) : p; }

        /// <summary>Where the left hand's instrument is seen: on the node it holds, or at the pull target when it holds none.</summary>
        Vector3 HeldPointB() { return nodeB >= 0 && lattice != null ? lattice.Pos(nodeB) + grabOffsetB : poseB; }

        /// <summary>The cursor ray passes within reach of the instrument the left hand is holding with.</summary>
        bool RayNearHeld(Vector3 ro, Vector3 rd)
        {
            if (!hands.engagedB || toolB == HandBKind.None || toolB == HandBKind.Suction) return false;
            var tip = toolB == HandBKind.Hook ? hookL.tip : forcepsL.Mid;
            var rel = tip - ro; float t = Vector3.Dot(rel, rd);
            return t > 0 && (rel - rd * t).magnitude < HandController.PickupMm;
        }

        /// <summary>The cursor on the horizontal plane the left hand is being dragged in.</summary>
        bool DragPoint(Vector3 ro, Vector3 rd, out Vector3 c)
        {
            c = Vector3.zero;
            if (Mathf.Abs(rd.y) < 1e-4f) return false;
            float t = (planeYB - ro.y) / rd.y;
            if (t <= 0) return false;
            c = ro + rd * t; return true;
        }

        /// <summary>
        /// The left hand: hook, forceps or suction on the right mouse button. A press on the tissue takes hold there, a drag
        /// pulls, and it stays where it is when the button is let go; pressing on the held instrument picks the hold up
        /// again (drag to change the pull, click to let go). While it holds, W A S D slide the pull and E / F raise and
        /// lower it, so traction can follow the dissection without the mouse leaving the right hand.
        /// </summary>
        void PlaceLeftHand(bool leftDown, bool hasHit, Vector3 hit, Vector3 rayO, Vector3 rayD, Vector3 camMm, float frameDt, ref bool suctionOn, ref Vector3 suctionAt)
        {
            bool leftEdge = leftDown && !lastLeftDown;
            bool sticky = toolB != HandBKind.Suction;
            float dragMm = 0; Vector3 c;
            if (leftDown && !leftEdge && pressValidB && DragPoint(rayO, rayD, out c)) dragMm = new Vector2(c.x - pressPointB.x, c.z - pressPointB.z).magnitude + Mathf.Abs(liftSincePressB);
            if (toolB == HandBKind.None) hands.ReleaseB();
            else hands.Update(leftDown, dragMm, leftEdge && RayNearHeld(rayO, rayD), sticky);
            if (hands.releasedB || !hands.engagedB) nodeB = hands.engagedB ? nodeB : -1;
            if (leftEdge && sticky)
            {
                liftSincePressB = 0; pressValidB = false;
                if (hands.regrabB)
                {
                    // a new hold where the pointer is: the hook works 1.5 mm above the surface it catches, the forceps on it
                    if (hasHit)
                    {
                        planeYB = hit.y + (toolB == HandBKind.Hook ? 1.5f : 0f);
                        if (!DragPoint(rayO, rayD, out c)) c = hit;
                        pressPointB = c; baseB = c; poseB = c; pressValidB = true;
                        // the hook catches the nearest node; the forceps close on what is between their tips
                        nodeB = lattice != null ? lattice.NearestNode(hit, toolB == HandBKind.Hook ? 4 : 3) : -1;
                        if (nodeB >= 0) grabOffsetB = hit - lattice.Pos(nodeB);
                        if (toolB == HandBKind.Hook && nodeB < 0) hands.ReleaseB();   // nothing there to catch
                    }
                    else hands.ReleaseB();
                }
                else if (hands.adjusting)
                {
                    // the existing hold picked up again: the drag continues from where the pull is
                    planeYB = poseB.y;
                    if (!DragPoint(rayO, rayD, out c)) c = poseB;
                    pressPointB = c; baseB = poseB; pressValidB = true;
                }
            }
            if (hands.followB && pressValidB && sticky && DragPoint(rayO, rayD, out c))
                poseB = new Vector3(baseB.x + c.x - pressPointB.x, baseB.y + liftSincePressB, baseB.z + c.z - pressPointB.z);
            if (hands.engagedB && !leftDown && sticky)
            {
                // the user's own left hand on the keyboard trims the pull while the mouse works
                float sp = 8f * frameDt, ca = Mathf.Cos(orbit.azimuth), sa = Mathf.Sin(orbit.azimuth);
                float dx = input.trimSlide.x, dz = -input.trimSlide.y;
                poseB.x += (ca * dx + sa * dz) * sp; poseB.z += (-sa * dx + ca * dz) * sp;
                poseB.y = Mathf.Clamp(poseB.y + input.trimLift * sp, -volume.SizeDepth, 25f);
            }
            showB = false;
            if (toolB == HandBKind.Hook)
            {
                showB = hands.engagedB && nodeB >= 0 && lattice != null;
                if (showB)
                {
                    lattice.attachments.Add(new Attachment { node = nodeB, target = poseB - grabOffsetB, strength = HoldStrength });
                    hookL.Place(true, HeldPointB(), camMm, false, Vector2.zero, true);
                }
            }
            else if (toolB == HandBKind.Forceps)
            {
                showB = hands.engagedB;
                if (showB)
                {
                    // closed jaws carry the node they caught and press together what lies between them
                    forcepsL.closed = true;
                    forcepsL.Place(true, HeldPointB(), camMm, false, Vector2.zero, 0.3f, true);
                    if (nodeB >= 0 && lattice != null) lattice.attachments.Add(new Attachment { node = nodeB, target = poseB - grabOffsetB, strength = HoldStrength });
                    slotB = new Slot { set = true, a = forcepsL.tipA, b = forcepsL.tipB, r = forcepsL.jawRadius, mode = 1 };
                }
            }
            else if (toolB == HandBKind.Suction)
            {
                // suction only acts while its button is held; it follows the pointer
                showB = leftDown && hasHit;
                suctionL.on = showB;
                if (showB)
                {
                    suctionL.Place(true, hit, camMm, false, Vector2.zero, true);
                    suctionOn = true; suctionAt = suctionL.tip; events.suctionOn = true;
                    slotB = new Slot { set = true, a = suctionL.tip, b = suctionL.tip, r = suctionL.drainRadius, mode = 2 };
                }
            }
            lastLeftDown = leftDown;
        }

        /// <summary>The right hand's instrument (the pointer and the left button), and the electrode: the pencil, buzzed jaws, or
        /// a retractor the active pencil has touched.</summary>
        void PlaceRightHand(bool pressed, Vector3 camMm, bool tiltOn, float frameDt, Vector3 rayO, Vector3 rayD, SimReadout r, ref bool suctionOn, ref Vector3 suctionAt)
        {
            bool hasHit = hasHitA; var hit = hitA;
            bool spread = Input.GetKey(KeyCode.Space);
            bool pressEdge = pressed && !lastPressedA, releaseEdge = !pressed && lastPressedA;
            lastPressedA = pressed;
            bool electrode = false;
            if (activeTool != ToolKind.Hemostat) { hemoNodes[0] = -1; hemoNodes[1] = -1; }
            if (activeTool != ToolKind.Forceps && activeTool != ToolKind.Hook) hook.node = -1;
            if (activeTool == ToolKind.Pencil)
            {
                tool.type = pencilElectrode;
                tool.Place(hasHit, hit, camMm, tiltOn);
                electrode = tool.visible;
            }
            else if (activeTool == ToolKind.Scalpel)
            {
                // steel: while the button is held the blade is in the tissue and divides what it sweeps through, with no heat
                scalpel.pressure = input.pressure;
                scalpel.Place(hasHit, hit, camMm, tiltOn, input.tiltDeg, pressed);
                if (scalpel.visible && scalpel.cutting)
                    physics.SetMechCut(ToMat(scalpel.sweeping ? scalpel.prevCentre : scalpel.Centre), ToMat(scalpel.Centre), scalpel.axis, scalpel.wdir, scalpel.ndir, scalpel.HalfLength, scalpel.halfWidth, scalpel.halfThickness);
            }
            else if (activeTool == ToolKind.Scissors)
            {
                // a quick click snips the sheet between the tips (taken where the tips were held); held, the closed tips go
                // in and Space opens them: blunt dissection with the hemostat's rules
                if (pressEdge) { scissorsPressAt = Time.unscaledTime; scissorsSpreadUsed = false; }
                if (pressed && spread) scissorsSpreadUsed = true;
                if (releaseEdge && scissors.visible && !scissorsSpreadUsed && Time.unscaledTime - scissorsPressAt < 0.35f)
                    physics.SetMechCut(ToMat(scissors.Centre), ToMat(scissors.Centre), scissors.axis, scissors.wdir, scissors.ndir, scissors.HalfLength, scissors.restMm / 2, 0.15f);
                scissors.Place(hasHit, hit, camMm, tiltOn, input.tiltDeg, pressed, spread, input.pressure, frameDt);
                if (scissors.visible && scissors.spreading && scissors.open > 0.5f)
                {
                    physics.SetDissect(true, scissors.jawRadius);
                    slotA = new Slot { set = true, a = scissors.tipA, b = scissors.tipB, r = scissors.jawRadius, mode = 4, hasMotion = true, motionIsPoint = true, motion = hasHit ? hit : scissors.Mid, absorb = 1 };
                }
            }
            else if (activeTool == ToolKind.Forceps)
            {
                forceps.closed = pressed;
                // Space raises the closed jaws (traction: lifts a grasped edge or a freed lipoma); opening resets
                if (forceps.closed && spread) liftMm = Mathf.Min(liftMm + 8f * frameDt, 15f);
                else if (!forceps.closed) liftMm = 0;
                forceps.Place(hasHit, hit + Vector3.up * liftMm, camMm, tiltOn, input.tiltDeg);
                if (forceps.visible && forceps.closed)
                {
                    slotA = new Slot { set = true, a = forceps.tipA, b = forceps.tipB, r = forceps.jawRadius, mode = 1 };
                    // buzzing the forceps: closed jaws become the electrode (a pedal activation); open jaws cannot be activated
                    tool.AsForceps(forceps.tipA, forceps.tipB, forceps.jawRadius, forceps.axis); electrode = true; events.buzzing = gen.activation != Activation.None;
                    // forceps lift: closed jaws carry the nearest node (and with it a freed lipoma)
                    if (lattice != null)
                    {
                        if (hook.node < 0) hook.node = lattice.NearestNode(forceps.Mid, 3);
                        if (hook.node >= 0) lattice.attachments.Add(new Attachment { node = hook.node, target = forceps.Mid + forceps.axis * 1.0f, strength = 0.2f });
                    }
                }
                else hook.node = -1;
            }
            else if (activeTool == ToolKind.Hemostat)
            {
                // closed jaws grasp (and can be buzzed); held closed and moving, the tips push through a plane; closed and
                // spread (Space), the jaws open inside the tissue and dissect it bluntly
                hemostat.closed = pressed;
                hemostat.Animate(spread, frameDt);
                hemostat.Place(hasHit, hit, camMm, tiltOn, input.tiltDeg, input.pressure);
                if (hemostat.visible && hemostat.closed)
                {
                    // the jaws entered the tissue at the surface hit: the dissected sheet runs from there to the tips
                    var entry = hasHit ? hit : hemostat.Mid;
                    if (hemostat.spreading && hemostat.spread > 0.05f) { physics.SetDissect(true, hemostat.jawRadius); slotA = new Slot { set = true, a = hemostat.tipA, b = hemostat.tipB, r = hemostat.jawRadius, mode = 4, hasMotion = true, motionIsPoint = true, motion = entry, absorb = 1 }; }
                    else if (!hemostat.spreading && hemostat.spread < 0.05f && hemostat.Travel() > 0.08f) { physics.SetDissect(false, hemostat.jawRadius); slotA = new Slot { set = true, a = hemostat.prevMid, b = hemostat.Mid, r = hemostat.jawRadius, mode = 5, hasMotion = true, motionIsPoint = true, motion = entry, absorb = 1 }; }
                    else slotA = new Slot { set = true, a = hemostat.tipA, b = hemostat.tipB, r = hemostat.jawRadius, mode = 1 };
                    if (!hemostat.spreading && hemostat.spread < 0.05f) { tool.AsForceps(hemostat.tipA, hemostat.tipB, hemostat.jawRadius, hemostat.axis); electrode = true; events.buzzing = gen.activation != Activation.None; }
                    // spreading pushes the two sides of the plane apart; a grasp carries the nearest node
                    if (lattice != null)
                    {
                        if (hemoNodes[0] < 0) { hemoNodes[0] = lattice.NearestNode(hemostat.tipA, 3); hemoNodes[1] = lattice.NearestNode(hemostat.tipB, 3); }
                        if (hemostat.spreading)
                        {
                            if (hemoNodes[0] >= 0) lattice.attachments.Add(new Attachment { node = hemoNodes[0], target = hemostat.tipA, strength = 0.3f });
                            if (hemoNodes[1] >= 0 && hemoNodes[1] != hemoNodes[0]) lattice.attachments.Add(new Attachment { node = hemoNodes[1], target = hemostat.tipB, strength = 0.3f });
                        }
                        else if (hemoNodes[0] >= 0) lattice.attachments.Add(new Attachment { node = hemoNodes[0], target = hemostat.Mid + hemostat.axis * 0.5f, strength = 0.2f });
                    }
                }
                else { hemoNodes[0] = -1; hemoNodes[1] = -1; }
            }
            else if (activeTool == ToolKind.Gauze)
            {
                gauze.Place(hasHit, hit, camMm, tiltOn, input.tiltDeg, pressed, input.pressure, frameDt);
                bool on = gauze.visible && pressed;
                if (on) slotA = new Slot { set = true, a = gauze.centre, b = gauze.centre, r = gauze.radius, mode = 3, hasMotion = true, motionIsPoint = false, motion = gauze.velocity, pressure = gauze.pressure, absorb = gauze.Absorb };
                gauze.soakedUl = Mathf.Min(gauze.capacityUl, gauze.soakedUl + r.gauzeSoakUl);
                float speed = gauze.velocity.magnitude;
                events.gauzeDab = on && speed < 15; events.gauzeWipe = on && speed >= 15;
                // the pressed pad compresses the surface under it
                if (lattice != null && on) foreach (int n in lattice.NodesWithin(gauze.centre, gauze.radius))
                    lattice.attachments.Add(new Attachment { node = n, target = new Vector3(lattice.pos[n * 3], lattice.mat[n * 3 + 1] - 1.0f * gauze.pressure, lattice.pos[n * 3 + 2]), strength = 0.15f });
            }
            else if (activeTool == ToolKind.Suction)
            {
                suction.on = pressed;
                suction.Place(hasHit, hit, camMm, tiltOn, input.tiltDeg);
                if (suction.visible && suction.on)
                {
                    slotA = new Slot { set = true, a = suction.tip, b = suction.tip, r = suction.drainRadius, mode = 2 };
                    suctionOn = true; suctionAt = suction.tip; events.suctionOn = true;
                }
            }
            else
            {
                // skin hook in the right hand: press to attach the nearest lattice node, drag to retract, release to let go
                bool dragging = false; Vector3 dragTarget = Vector3.zero;
                if (pressed && lattice != null)
                {
                    if (hook.node < 0 && hasHit) hook.node = lattice.NearestNode(hit, 4);
                    if (hook.node >= 0)
                    {
                        float planeY = lattice.mat[hook.node * 3 + 1] + 1.5f;
                        float tHit = Mathf.Abs(rayD.y) > 1e-4f ? (planeY - rayO.y) / rayD.y : -1;
                        if (tHit > 0) { dragging = true; dragTarget = rayO + rayD * tHit; lattice.attachments.Add(new Attachment { node = hook.node, target = dragTarget, strength = 0.25f }); }
                    }
                }
                else hook.node = -1;
                hook.Place(dragging || hasHit, dragging ? dragTarget : hit, camMm, tiltOn, input.tiltDeg);
            }
            // the active pencil against the left hand's instrument: touching held forceps buzzes what they hold; touching a
            // hook or the suction diverts the current into the tissue under it (an alternate-site burn)
            if (activeTool == ToolKind.Pencil && tool.visible && showB && gen.activation != Activation.None)
            {
                if (toolB == HandBKind.Forceps && HandPose.MetalDistance(tool.tip, forcepsL.Mid, forcepsL.axis, 45, 1.6f) < 1.0f)
                { tool.AsForceps(forcepsL.tipA, forcepsL.tipB, forcepsL.jawRadius, forcepsL.axis); events.buzzing = true; }
                else if (toolB == HandBKind.Hook && HandPose.MetalDistance(tool.tip, hookL.tip, hookL.axis, 40, 1.0f) < 1.0f)
                { tool.AsForceps(hookL.tip, hookL.tip + hookL.axis * 1.5f, 0.4f, hookL.axis); events.instrumentContact = true; }
                else if (toolB == HandBKind.Suction && HandPose.MetalDistance(tool.tip, suctionL.tip, suctionL.axis, 50, 1.7f) < 1.0f)
                { tool.AsForceps(suctionL.tip, suctionL.tip + suctionL.axis * 2.0f, 1.5f, suctionL.axis); events.instrumentContact = true; }
            }
            if (!electrode) { tool.visible = false; gen.activation = Activation.None; }
        }

        /// <summary>Poses the instrument models where the instruments are (already on the tissue as it is seen).</summary>
        void PoseInstruments(ModeSpec mode)
        {
            bool pen = activeTool == ToolKind.Pencil && tool.visible;
            pencil.SetActive(pen);
            if (pen)
            {
                var wantMesh = pencilElectrode == ElectrodeType.Needle ? needleMesh : pencilElectrode == ElectrodeType.Ball ? ballMesh : bladeMesh;
                if (electrodeFilter.sharedMesh != wantMesh) { electrodeFilter.sharedMesh = wantMesh; electrodeRenderer.sharedMaterials = pencilElectrode == ElectrodeType.Needle ? needleMats : pencilElectrode == ElectrodeType.Ball ? ballMats : bladeMats; }
                // mesh frame: +Y along the handle axis, +Z the blade face normal (includes roll)
                HandPose.Apply(pencil.transform, tool.tip, tool.axis, tool.ndir);
                SetButtonGlow(cutButton, gen.activation == Activation.Cut, new Color(1f, 0.85f, 0.2f));
                SetButtonGlow(coagButton, gen.activation == Activation.Coag, new Color(0.25f, 0.55f, 1f));
            }
            bool sc = activeTool == ToolKind.Scalpel && scalpel.visible;
            scalpelGo.SetActive(sc);
            if (sc) HandPose.Apply(scalpelGo.transform, scalpel.tip, scalpel.axis, scalpel.ndir);
            bool sz = activeTool == ToolKind.Scissors && scissors.visible;
            scissorsGoA.SetActive(sz); scissorsGoB.SetActive(sz);
            if (sz)
            {
                HandPose.Apply(scissorsGoA.transform, scissors.tipA, scissors.armAy, scissors.armAz);
                HandPose.Apply(scissorsGoB.transform, scissors.tipB, scissors.armBy, scissors.armBz);
            }
            bool fc = activeTool == ToolKind.Forceps && forceps.visible;
            forcepsA.SetActive(fc); forcepsB.SetActive(fc);
            if (fc)
            {
                HandPose.Apply(forcepsA.transform, forceps.tipA, forceps.armAy, forceps.armAz);
                HandPose.Apply(forcepsB.transform, forceps.tipB, forceps.armBy, forceps.armBz);
            }
            bool hm = activeTool == ToolKind.Hemostat && hemostat.visible;
            hemoA.SetActive(hm); hemoB.SetActive(hm);
            if (hm)
            {
                HandPose.Apply(hemoA.transform, hemostat.tipA, hemostat.armAy, hemostat.armAz);
                HandPose.Apply(hemoB.transform, hemostat.tipB, hemostat.armBy, hemostat.armBz);
            }
            bool gz = activeTool == ToolKind.Gauze && gauze.visible;
            gauzeGo.SetActive(gz);
            if (gz)
            {
                HandPose.Apply(gauzeGo.transform, gauze.centre, gauze.axis, gauze.frameZ);
                // the pad reddens as it fills
                float sat = gauze.Saturation;
                gauzeRenderer.GetPropertyBlock(gauzeMpb);
                gauzeMpb.SetColor("_BaseColor", new Color(0.96f - 0.6f * sat, 0.96f - 0.9f * sat, 0.94f - 0.9f * sat));
                gauzeRenderer.SetPropertyBlock(gauzeMpb);
            }
            bool su = activeTool == ToolKind.Suction && suction.visible;
            frazier.SetActive(su);
            if (su) HandPose.Apply(frazier.transform, suction.tip, suction.axis, suction.frameZ);
            bool hk = activeTool == ToolKind.Hook && hook.visible;
            hookGo.SetActive(hk);
            if (hk) HandPose.Apply(hookGo.transform, hook.tip, hook.axis, hook.frameZ);
            // the left hand's instrument stays on the field while it holds
            bool hl = showB && toolB == HandBKind.Hook;
            hookGoL.SetActive(hl);
            if (hl) HandPose.Apply(hookGoL.transform, hookL.tip, hookL.axis, hookL.frameZ);
            bool fl = showB && toolB == HandBKind.Forceps;
            forcepsLA.SetActive(fl); forcepsLB.SetActive(fl);
            if (fl)
            {
                HandPose.Apply(forcepsLA.transform, forcepsL.tipA, forcepsL.armAy, forcepsL.armAz);
                HandPose.Apply(forcepsLB.transform, forcepsL.tipB, forcepsL.armBy, forcepsL.armBz);
            }
            bool sl = showB && toolB == HandBKind.Suction;
            frazierL.SetActive(sl);
            if (sl) HandPose.Apply(frazierL.transform, suctionL.tip, suctionL.axis, suctionL.frameZ);

            // arc / spark light at the tip (bloom picks up the emissive particles; this lights the wound)
            bool arcOn = mode != null && physics.readout.hasHit && tool.visible;
            arcLight.enabled = arcOn;
            if (arcOn)
            {
                arcLight.transform.localPosition = tool.tip + Vector3.up * 1f;
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
            // Tab: the right hand's next instrument (` goes back); Shift+Tab: the left hand's (taking it out of the field first)
            if (Input.GetKeyDown(KeyCode.Tab) || Input.GetKeyDown(KeyCode.Q))
            {
                if (shift) { toolB = HandPose.OrderB[(Array.IndexOf(HandPose.OrderB, toolB) + 1) % HandPose.OrderB.Length]; hands.ReleaseB(); nodeB = -1; }
                else activeTool = HandPose.Order[(Array.IndexOf(HandPose.Order, activeTool) + 1) % HandPose.Order.Length];
            }
            if (Input.GetKeyDown(KeyCode.BackQuote)) activeTool = HandPose.Order[(Array.IndexOf(HandPose.Order, activeTool) + HandPose.Order.Length - 1) % HandPose.Order.Length];
            if (Input.GetKeyDown(KeyCode.T)) { forceps.toothed = !forceps.toothed; forcepsL.toothed = forceps.toothed; }
            if (Input.GetKeyDown(KeyCode.U)) gauze.Fresh();
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
            bool electrode = tool.visible;
            var sp = viewCamera.WorldToScreenPoint(field.TransformPoint(tool.tip));
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
            d.toolText = HandPose.Label(activeTool) + (activeTool == ToolKind.Pencil ? " · " + pencilElectrode.ToString().ToLowerInvariant()
                : activeTool == ToolKind.Forceps ? (forceps.closed ? " · closed" : " · open") + (forceps.toothed ? ", toothed" : ", smooth") + (liftMm > 0 ? ", lifted " + liftMm.ToString("0") + " mm" : "")
                : activeTool == ToolKind.Hemostat ? (hemostat.spreading ? " · spreading " + hemostat.Span.ToString("0.0") + " mm" : hemostat.closed ? (hemostat.Travel() > 0.08f ? " · pushing" : " · closed") : " · open")
                : activeTool == ToolKind.Gauze ? " · " + (gauze.soakedUl / 1000).ToString("0.0") + " / " + (gauze.capacityUl / 1000).ToString("0") + " ml" + (gauze.Saturated ? " SATURATED" : "") + " (" + gauze.discarded + " used)"
                : activeTool == ToolKind.Scalpel ? (scalpel.cutting ? " · cutting " + scalpel.Depth.ToString("0.0") + " mm deep" : " · hold to cut")
                : activeTool == ToolKind.Scissors ? (scissors.spreading ? " · spreading " + scissors.open.ToString("0.0") + " mm" : scissors.closed ? " · closed, in the tissue" : " · click snips, hold + Space spreads") : "");
            d.leftText = toolB == HandBKind.None ? "none · Shift+Tab to pick one" : HandPose.Label(toolB) + (hands.stateB == HandBState.Live ? (hands.engagedB ? " · placing" : " · on the button")
                : hands.stateB == HandBState.Latched ? " · holding (W A S D slide, E / F raise and lower, right-click it to let go)" : " · ready: right button on the tissue");
            d.cutVoxels = physics.totalCutVoxels;
            d.activation = gen.activation; d.activeWatts = gen.ActiveWatts; d.remFault = gen.remFault;
            d.zOhm = r.zOhm; d.powerW = r.powerW; d.vRms = r.vRms; d.contact = r.contactVoxels;
            d.underTip = r.hasHit ? TissueTable.Name((int)Cell.Type(r.hitCell)) + " · " + TissueTable.StateNames[(int)Cell.State(r.hitCell)] : "—";
            d.maxT = r.maxTempC; d.carbon = r.carbonVoxels; d.vaporised = physics.totalVapVoxels; d.simTime = physics.simTime;
            maxCharMm3 = Mathf.Max(maxCharMm3, r.carbonVoxels * volume.h * volume.h * volume.h); d.charMm3 = maxCharMm3;
            d.bloodLost = r.bloodLostMl; d.pooled = r.poolMl; d.exposedArt = r.exposedArterial; d.exposedVen = r.exposedVenous;
            d.sealedVx = r.sealedVoxels; d.charPlug = r.charPlugVoxels; d.rebleeds = r.rebleeds;
            d.oozeMl = r.oozeMl; d.oozeVoxels = r.oozeVoxels; d.clotMl = r.clotMl; d.gauzeMl = physics.totalGauzeSoakUl / 1000;
            d.strands = strands != null ? strands.count : 0; d.tornLinks = lattice != null ? lattice.tornLinks : 0; d.tornVoxels = physics.totalTornVoxels; d.dissectedVoxels = physics.totalDissectedVoxels;
            visceraVapMm3 = r.visceraVapVoxels * volume.h * volume.h * volume.h;
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
                case "visceraVaporisedMm3": return visceraVapMm3;
                case "oozeMl": return r.oozeMl;
                case "tornVoxels": return physics.totalTornVoxels;
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
                    case Tissue.Viscera: name = "Viscus"; break;
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
            if (strands != null) strands.Dispose();
            if (gpu != null) gpu.Dispose();
            if (ui != null) ui.Dispose();
            if (replay != null) replay.Dispose();
        }
    }
}
