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

        [Tooltip("Scenario JSON in Resources/BovieSim/Scenarios")] public string scenarioId = "freeplay";
        [Tooltip("0 = scenario voxel size; larger values (e.g. 0.4) run faster on modest GPUs")] public float voxelOverride;
        public bool mechanics = true;
        public Camera viewCamera;
        [Tooltip("Optional: the scene's global post-processing volume, for depth-of-field focus at the tip")] public UnityEngine.Rendering.Volume postVolume;

        // ---- simulation state
        Scenario scenario; BovieSim.Volume volume; AnatomyReport report; Lattice lattice;
        GpuVolume gpu; PhysicsPipeline physics; SurfaceRenderer surface; Particles particles;
        readonly FT10 gen = new FT10();
        EventBus events; SimHud hud; ActivationAudio audioOut; OrbitCamera orbit; readonly InputState input = new InputState();
        readonly ElectrodeTool tool = new ElectrodeTool(); readonly ForcepsTool forceps = new ForcepsTool();
        readonly SuctionTool suction = new SuctionTool(); readonly HookTool hook = new HookTool();
        ToolKind activeTool = ToolKind.Pencil; ElectrodeType pencilElectrode = ElectrodeType.Blade;
        bool slicePerp, lesionFreedReported, lesionDelivered, lesionBreachReported, ready;
        float clusterRiseMm; string status = "Building anatomy…";
        Vector4[] lastDisp; bool latticeMoved;

        // ---- scene objects
        Transform field;
        GameObject pencil, forcepsA, forcepsB, frazier, hookGo, drapes;
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
            if (assets == null || simCs == null || surfCs == null || scJson == null || viewCamera == null)
            {
                status = "Cannot start: " + (assets == null ? "SimAssets missing (run Bovie Sim > Set Up Project). " : "") + (simCs == null || surfCs == null ? "compute shaders missing. " : "") + (scJson == null ? "scenario '" + scenarioId + "' not found. " : "") + (viewCamera == null ? "no camera. " : "");
                Debug.LogError("[Bovie Sim] " + status);
                yield break;
            }
            if (!SystemInfo.supportsComputeShaders || !SystemInfo.supportsAsyncGPUReadback)
            {
                status = "Cannot start: this GPU/driver lacks compute shaders or async readback.";
                Debug.LogError("[Bovie Sim] " + status);
                yield break;
            }

            scenario = Scenario.FromJson(scJson.text);
            events = new EventBus(teach != null ? teach.text : null);
            hud = new SimHud();
            events.OnEvent += hud.Push;
            gen.cutMode = FT10.Parse(scenario.generator.cutMode, ModeId.Pure); gen.coagMode = FT10.Parse(scenario.generator.coagMode, ModeId.Fulgurate);
            gen.cutWatts = scenario.generator.cutWatts; gen.coagWatts = scenario.generator.coagWatts;
            gen.Adjust(true, 0); gen.Adjust(false, 0);
            gen.remFault = scenario.faults != null && scenario.faults.Contains("rem");

            // anatomy and lattice are pure C#: build them on a worker thread
            var sc = scenario; float vo = voxelOverride; bool mech = mechanics;
            var build = Task.Run(() =>
            {
                AnatomyReport rep;
                var v = Anatomy.Build(sc, out rep, vo);
                var lat = mech ? new Lattice(v, 3f, sc.skin.langerDeg * Mathf.Deg2Rad) : null;
                return new Tuple<BovieSim.Volume, AnatomyReport, Lattice>(v, rep, lat);
            });
            while (!build.IsCompleted) yield return null;
            if (build.IsFaulted)
            {
                status = "Anatomy build failed: " + build.Exception.GetBaseException().Message;
                Debug.LogException(build.Exception.GetBaseException());
                yield break;
            }
            volume = build.Result.Item1; report = build.Result.Item2; lattice = build.Result.Item3;

            // field root: millimetres to metres
            field = new GameObject("Field (mm)").transform;
            field.SetParent(transform, false);
            field.localScale = Vector3.one * 0.001f;

            gpu = new GpuVolume(volume);
            physics = new PhysicsPipeline(simCs, gpu);
            surface = new SurfaceRenderer(surfCs, gpu, field, assets.tissue, assets.blood);
            particles = new Particles(assets.particlesAlpha, assets.particlesAdditive);
            if (scenario.preCarve != null && scenario.preCarve.Count > 0) physics.MarkWound(new[] { 0, 0, 0 }, new[] { volume.nx, volume.ny, volume.nz });
            if (lattice != null)
            {
                surface.SetLattice(lattice.nx, lattice.ny, lattice.nz, lattice.origin, lattice.spacing, lattice.count);
                physics.SetLatticeDisplacement(surface.LatticeBuffer, lattice.nx, lattice.ny, lattice.nz, lattice.origin, lattice.spacing);
                physics.SetConstraintSegments(lattice.SegmentData(volume));
                surface.UploadLattice(lattice.disp);
                lastDisp = (Vector4[])lattice.disp.Clone();
            }
            physics.sliceWidthMm = 16; physics.sliceDepthMm = 10;

            BuildInstruments();
            orbit = new OrbitCamera(viewCamera, field);
            audioOut = gameObject.AddComponent<ActivationAudio>();
            if (postVolume == null) postVolume = FindFirstObjectByType<UnityEngine.Rendering.Volume>();
            if (postVolume != null && postVolume.sharedProfile != null) postVolume.sharedProfile.TryGet(out dof);
            status = "";
            ready = true;
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
            cutButton = MakePart("Cut button", MeshFactory.PencilButton(82), assets.pencilCutButton, pencil.transform).GetComponent<MeshRenderer>();
            coagButton = MakePart("Coag button", MeshFactory.PencilButton(104), assets.pencilCoagButton, pencil.transform).GetComponent<MeshRenderer>();
            bladeMesh = MeshFactory.Blade(); needleMesh = MeshFactory.Needle(); ballMesh = MeshFactory.Ball();
            electrodeFilter = MakePart("Electrode", bladeMesh, assets.steel, pencil.transform).GetComponent<MeshFilter>();
            var arm = MeshFactory.ForcepsArm(forceps.armLength);
            forcepsA = MakePart("Adson forceps A", arm, assets.steel, field);
            forcepsB = MakePart("Adson forceps B", arm, assets.steel, field);
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

        // ------------------------------------------------------------------------------------------------
        void Update()
        {
            if (Input.GetKeyDown(KeyCode.F5)) { Reload(scenarioId); return; }
            if (Input.GetKeyDown(KeyCode.F6)) { Reload(ScenarioIds[(Array.IndexOf(ScenarioIds, scenarioId) + 1) % ScenarioIds.Length]); return; }
            if (!ready) return;
            float frameDt = Mathf.Min(0.05f, Time.deltaTime);
            input.Update();
            bool overUi = hud.PointerOverUi(input.mouse);
            orbit.HandleInput(overUi);
            HandleKeys();

            // ---- generator activation: footswitch (keys or on-screen) or hand switch on the pencil
            bool lmb = input.leftHeld && !overUi && !orbit.Dragging;
            bool handCut = activeTool == ToolKind.Pencil && lmb && !input.ctrl;
            bool handCoag = activeTool == ToolKind.Pencil && lmb && input.ctrl;
            gen.activation = input.pedalCut || hud.pedalCut || handCut ? Activation.Cut : input.pedalCoag || hud.pedalCoag || handCoag ? Activation.Coag : Activation.None;

            var r = physics.readout;
            bool hasHit = input.inside && r.hasHit;
            Vector3 hit = r.hit;
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
                forceps.Place(hasHit, hit, camMm, tiltOn, input.tiltDeg);
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
            if (hot || latticeMoved || surface.dirty) surface.Extract();
            surface.UpdateBlood();

            // ---- pick ray for the next frame and cross-section plane through the tip
            Vector3 o, d; PickRay(out o, out d);
            physics.SetPickRay(o, d);
            var right = new Vector3(Mathf.Cos(orbit.azimuth), 0, -Mathf.Sin(orbit.azimuth));
            if (slicePerp && tool.visible)
            {
                var ax = new Vector3(tool.axis.x, 0, tool.axis.z);
                if (ax.magnitude > 1e-3f) { ax.Normalize(); right = new Vector3(-ax.z, 0, ax.x); }
            }
            var anchor = r.hasHit ? r.hit : Vector3.zero;
            physics.sliceOrigin = new Vector3(anchor.x, (r.hasHit ? anchor.y : 0) + 1.5f, anchor.z);
            physics.sliceU = right;

            PoseInstruments(mode);
            orbit.Apply();
            UpdateFocus();
            particles.Draw(field);

            frames++; fpsT += Time.unscaledDeltaTime;
            if (fpsT > 0.5f) { fps = frames / fpsT; frames = 0; fpsT = 0; }
            hud.hudText = BuildHud(r);
        }

        void StepMechanics(ModeSpec mode, SimReadout r, float frameDt)
        {
            byte[] sev; float[] om;
            if (physics.TakeProbe(out sev, out om)) lattice.ApplyProbe(sev, om);
            if (mode != null && !mode.isCut && r.muscleHeatVoxels > 20 && tool.visible) lattice.Stimulate(tool.tip, Mathf.Min(2, 0.5f + r.iRms * 2), physics.simTime);
            lattice.Step(frameDt, physics.simTime);
            // re-extract only when some node moved visibly since the last extraction
            latticeMoved = false;
            for (int n = 0; n < lattice.count; n++)
            {
                var a = lattice.disp[n]; var b = lastDisp[n];
                if (Mathf.Abs(a.x - b.x) + Mathf.Abs(a.y - b.y) + Mathf.Abs(a.z - b.z) > 0.02f) { latticeMoved = true; break; }
            }
            if (latticeMoved) { surface.UploadLattice(lattice.disp); Array.Copy(lattice.disp, lastDisp, lattice.count); }

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
            if (Input.GetKeyDown(KeyCode.F1)) hud.showBrief = !hud.showBrief;
            if (Input.GetKeyDown(KeyCode.F2)) hud.showHelp = !hud.showHelp;
        }

        string BuildHud(SimReadout r)
        {
            var cutM = FT10.Mode(gen.cutMode); var coagM = FT10.Mode(gen.coagMode);
            string toolTxt = HandPose.Label(activeTool) + (activeTool == ToolKind.Pencil ? " (" + pencilElectrode.ToString().ToLowerInvariant() + ")" : activeTool == ToolKind.Forceps ? (forceps.closed ? " closed" : " open") + (forceps.toothed ? ", toothed" : ", smooth") : "");
            string act = gen.activation == Activation.None ? "idle" : "<b>" + gen.activation.ToString().ToUpperInvariant() + " " + gen.ActiveWatts + " W</b>  Z " + (r.zOhm < 1e8f ? r.zOhm.ToString("0") + " Ω" : "open") + "  P " + r.powerW.ToString("0.0") + " W  V " + r.vRms.ToString("0") + " V  contact " + r.contactVoxels + " vx";
            if (gen.remFault && gen.activation != Activation.None) act = "<b>REM FAULT: generator will not activate</b>";
            string under = r.hasHit ? TissueTable.Name((int)Cell.Type(r.hitCell)) + " (" + TissueTable.StateNames[(int)Cell.State(r.hitCell)] + ")  " + r.hit.x.ToString("0.0") + ", " + r.hit.y.ToString("0.0") + ", " + r.hit.z.ToString("0.0") + " mm" : "—";
            string s = "<b>Bovie Sim</b> · " + scenario.name + "\n" +
                "FT10  cut: " + cutM.label + " " + gen.cutWatts + " W   coag: " + coagM.label + " " + gen.coagWatts + " W   tool: <b>" + toolTxt + "</b>\n" +
                act + "\n" +
                "max T " + r.maxTempC.ToString("0") + " °C · char " + r.carbonVoxels + " vx · vaporised " + physics.totalVapVoxels.ToString("0") + " vx · sim t " + physics.simTime.ToString("0.0") + " s\n" +
                "blood lost " + r.bloodLostMl.ToString("0.00") + " ml · pooled " + r.poolMl.ToString("0.00") + " ml · bleeding ends: " + r.exposedArterial + " art / " + r.exposedVenous + " ven · sealed " + r.sealedVoxels + " · char plug " + r.charPlugVoxels + " · rebleeds " + r.rebleeds + "\n" +
                "under tip: " + under + "\n" +
                (lattice != null && lattice.capsuleTotal > 0 ? "capsule " + lattice.capsuleAlive + "/" + lattice.capsuleTotal + " links" + (lattice.lipomaFree ? " · FREE, rise " + clusterRiseMm.ToString("0.0") + " mm" : "") + "\n" : "") +
                "volume " + volume.nx + "×" + volume.ny + "×" + volume.nz + " @ " + volume.h + " mm · tris " + (surface.lastVertexCount / 3).ToString("N0") + (surface.capped ? " (CAPPED)" : "") + " · " + fps.ToString("0") + " fps";
            if (hud.showHelp)
                s += "\n\nZ/X pedal cut/coag · LMB hand-switch cut (Ctrl+LMB coag) on the pencil / grasp (forceps) / suck (suction) / hook\n" +
                     "Tab or Q tool · 1-4 cut mode · 5-6 coag mode · [ ] ; ' watts (Shift = 1 W) · B/N/O blade/needle/ball · R roll · T toothed\n" +
                     "C section mode · J section orientation · H hide section · RMB or Alt+LMB orbit · MMB or Shift+LMB pan · wheel zoom\n" +
                     "F1 brief · F5 restart · F6 next scenario (" + string.Join(", ", ScenarioIds) + ") · pen pressure sets depth, pen tilt sets the angle";
            else s += "\nF2 controls";
            return s;
        }

        void Reload(string id)
        {
            requestedScenario = id;
            SceneManager.LoadScene(SceneManager.GetActiveScene().buildIndex >= 0 ? SceneManager.GetActiveScene().name : "Main");
        }

        void OnGUI()
        {
            if (!ready)
            {
                GUI.Label(new Rect(20, 20, Screen.width - 40, 60), "<b>Bovie Sim</b>\n" + status, new GUIStyle(GUI.skin.label) { richText = true, fontSize = 16 });
                return;
            }
            hud.Draw(gen, physics.readout, physics, scenario);
        }

        void OnDestroy()
        {
            if (physics != null) physics.Dispose();
            if (surface != null) surface.Dispose();
            if (particles != null) particles.Dispose();
            if (gpu != null) gpu.Dispose();
            if (hud != null) hud.Dispose();
        }
    }
}
