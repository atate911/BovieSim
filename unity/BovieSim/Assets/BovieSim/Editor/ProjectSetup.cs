using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

namespace BovieSim.EditorTools
{
    /// <summary>
    /// One-click project setup (menu Bovie Sim > Set Up Project; runs once automatically after HDRP is installed):
    /// linear colour space and legacy input, HDRP asset, diffusion profiles, baked tissue textures and HDRP Lit
    /// materials for every surface bucket, instrument/drape/blood/particle materials, the post-processing volume
    /// profile, CC0 HDRI and fabric downloads (with procedural fallbacks), the Main scene and the SimAssets registry.
    /// Everything it makes goes to Assets/BovieSim/Generated (git-ignored) and is safe to regenerate.
    /// </summary>
    [InitializeOnLoad]
    public static class ProjectSetup
    {
        const string Root = "Assets/BovieSim/Generated";
        const string Tex = Root + "/Textures", Mat = Root + "/Materials", Dl = Root + "/Downloads";
        const string RegistryPath = "Assets/BovieSim/Resources/BovieSim/SimAssets.asset";
        const string ScenePath = Root + "/Main.unity";
        const int TexSize = 512;

        /// <summary>Poly Haven HDRI ids tried in order (all CC0). The first one the API knows is used.</summary>
        static readonly string[] HdriCandidates = { "hospital_room", "hospital_room_2", "surgery", "studio_small_08", "studio_small_03" };
        /// <summary>ambientCG fabric ids tried in order (all CC0).</summary>
        static readonly string[] FabricCandidates = { "Fabric030", "Fabric026", "Fabric004" };

        static readonly StringBuilder downloadLog = new StringBuilder();

        static ProjectSetup()
        {
            EditorApplication.delayCall += () =>
            {
                if (SessionState.GetBool("BovieSim.SetupOffered", false)) return;
                var existing = AssetDatabase.LoadAssetAtPath<SimAssets>(RegistryPath);
                if (existing != null && existing.setupVersion >= SimAssets.CurrentSetupVersion) return;
                SessionState.SetBool("BovieSim.SetupOffered", true);
                string msg = existing == null
                    ? "Set up the project now? This configures HDRP, bakes tissue textures and materials, downloads a CC0 HDRI and drape fabric (optional), and creates the Main scene. It takes a minute or two."
                    : "Bovie Sim was updated and its generated assets (materials, interface theme, scene) need rebuilding. Run Set Up Project now?";
                if (EditorUtility.DisplayDialog("Bovie Sim", msg, "Set up", "Later")) Run();
            };
        }

        [MenuItem("Bovie Sim/Set Up Project", priority = 0)]
        public static void Run()
        {
            downloadLog.Length = 0;
            try
            {
                Progress("Project settings", 0.02f);
                ConfigurePlayer();
                foreach (var d in new[] { Root, Tex, Mat, Dl, "Assets/BovieSim/Resources/BovieSim" }) Directory.CreateDirectory(d);
                AssetDatabase.Refresh();

                Progress("HDRP asset", 0.05f);
                var hdrp = CreateOrLoad<HDRenderPipelineAsset>(Root + "/BovieHDRP.asset");
                GraphicsSettings.defaultRenderPipeline = hdrp;
                int current = QualitySettings.GetQualityLevel();
                for (int q = 0; q < QualitySettings.names.Length; q++) { QualitySettings.SetQualityLevel(q, false); QualitySettings.renderPipeline = hdrp; }
                QualitySettings.SetQualityLevel(current, false);

                Progress("Diffusion profiles", 0.08f);
                // scattering distances in mm (world unit = 1 m under the field root scale): skin is red-dominant,
                // fat scatters further and yellower, muscle short and red
                var skin = Profile("Skin", new Color(1.6f, 0.62f, 0.38f));
                var fat = Profile("Fat", new Color(2.4f, 1.7f, 0.7f));
                var muscle = Profile("Muscle", new Color(1.1f, 0.3f, 0.22f));

                var assets = CreateOrLoad<SimAssets>(RegistryPath);
                if (assets.tissue == null || assets.tissue.Length != SimAssets.TissueBuckets) assets.tissue = new Material[SimAssets.TissueBuckets];
                for (int b = 0; b < SimAssets.TissueBuckets; b++)
                {
                    Progress("Baking tissue textures (" + BucketName(b) + ")", 0.1f + 0.5f * b / SimAssets.TissueBuckets);
                    assets.tissue[b] = TissueMaterial(b, skin, fat, muscle);
                }

                Progress("Instrument and field materials", 0.62f);
                assets.blood = LitMaterial("Blood", TissuePalette.BloodPooled, 0, 0.92f, 1f);
                assets.steel = LitMaterial("Steel", new Color(0.78f, 0.78f, 0.80f), 1, 0.82f, 0);
                assets.pencilBody = LitMaterial("PencilBody", new Color(0.90f, 0.87f, 0.78f), 0, 0.5f, 0);
                assets.pencilCutButton = LitMaterial("PencilCut", new Color(0.92f, 0.78f, 0.12f), 0, 0.55f, 0);
                assets.pencilCoagButton = LitMaterial("PencilCoag", new Color(0.16f, 0.36f, 0.82f), 0, 0.55f, 0);

                Progress("Downloading drape fabric (ambientCG, CC0)", 0.66f);
                assets.drape = DrapeMaterial();
                assets.particlesAlpha = ParticleMaterial("ParticlesAlpha", BlendMode.SrcAlpha, BlendMode.OneMinusSrcAlpha);
                assets.particlesAdditive = ParticleMaterial("ParticlesAdditive", BlendMode.One, BlendMode.One);
                assets.uiTheme = RuntimeTheme();
                assets.setupVersion = SimAssets.CurrentSetupVersion;
                EditorUtility.SetDirty(assets);

                Progress("Downloading HDRI (Poly Haven, CC0)", 0.75f);
                var hdri = DownloadHdri();
                Progress("Post-processing volume", 0.85f);
                var profile = VolumeProfileAsset(hdri, skin, fat, muscle);

                Progress("Main scene", 0.92f);
                BuildScene(profile);
                WriteDownloadLog();
                AssetDatabase.SaveAssets();
                Debug.Log("[Bovie Sim] Setup complete. Open " + ScenePath + " and press Play. If the HDRP Wizard reports issues (Window > Rendering > HDRP Wizard), use Fix All.");
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                EditorUtility.DisplayDialog("Bovie Sim", "Setup failed: " + e.Message + "\nSee the Console for details.", "OK");
            }
            finally { EditorUtility.ClearProgressBar(); }
        }

        static void Progress(string what, float f) { EditorUtility.DisplayProgressBar("Bovie Sim setup", what, f); }

        static T CreateOrLoad<T>(string path) where T : ScriptableObject
        {
            var a = AssetDatabase.LoadAssetAtPath<T>(path);
            if (a != null) return a;
            a = ScriptableObject.CreateInstance<T>();
            AssetDatabase.CreateAsset(a, path);
            return a;
        }

        static void ConfigurePlayer()
        {
            PlayerSettings.colorSpace = ColorSpace.Linear;
            // CPU/GPU frame times for the F3 timing readout
            PlayerSettings.enableFrameTimingStats = true;
            // the sim reads the legacy Input class (mouse, keys) and the Input System (pen): needs "Both"
            var ps = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/ProjectSettings.asset");
            if (ps != null && ps.Length > 0)
            {
                var so = new SerializedObject(ps[0]);
                var p = so.FindProperty("activeInputHandler");
                if (p != null && p.intValue != 2) { p.intValue = 2; so.ApplyModifiedProperties(); Debug.Log("[Bovie Sim] Active Input Handling set to Both (restart the editor if prompted)."); }
            }
        }

        static string BucketName(int b)
        {
            if (b == 16) return "Desiccated";
            if (b == 17) return "Carbonized";
            return TissueTextures.ClassNames[b % 8] + (b >= 8 ? "Coag" : "");
        }

        /// <summary>UI Toolkit runtime theme: the stock default theme, imported from a one-line .tss file.</summary>
        static UnityEngine.UIElements.ThemeStyleSheet RuntimeTheme()
        {
            string path = Root + "/BovieTheme.tss";
            File.WriteAllText(path, "@import url(\"unity-theme://default\");\n");
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
            var theme = AssetDatabase.LoadAssetAtPath<UnityEngine.UIElements.ThemeStyleSheet>(path);
            if (theme == null) Debug.LogWarning("[Bovie Sim] Could not import the UI theme at " + path + "; the interface may render without fonts.");
            return theme;
        }

        // ---- diffusion profiles ----------------------------------------------------------------------
        static DiffusionProfileSettings Profile(string name, Color scatterMm)
        {
            var p = CreateOrLoad<DiffusionProfileSettings>(Root + "/Diffusion_" + name + ".asset");
            p.scatteringDistance = scatterMm;
            p.worldScale = 1f;
            EditorUtility.SetDirty(p);
            return p;
        }

        // ---- textures ----------------------------------------------------------------------------------
        static Texture2D SaveTexture(string name, Color[] px, int size, bool linear, bool normal)
        {
            var t = new Texture2D(size, size, TextureFormat.RGBA32, true, linear);
            t.SetPixels(px); t.Apply();
            string path = Tex + "/" + name + ".png";
            File.WriteAllBytes(path, t.EncodeToPNG());
            UnityEngine.Object.DestroyImmediate(t);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
            var imp = (TextureImporter)AssetImporter.GetAtPath(path);
            imp.textureType = normal ? TextureImporterType.NormalMap : TextureImporterType.Default;
            imp.sRGBTexture = !linear;
            imp.wrapMode = TextureWrapMode.Repeat;
            imp.anisoLevel = 8;
            imp.mipmapEnabled = true;
            imp.textureCompression = TextureImporterCompression.CompressedHQ;
            imp.SaveAndReimport();
            return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        }

        static Material NewLit(string name)
        {
            string path = Mat + "/" + name + ".mat";
            var m = AssetDatabase.LoadAssetAtPath<Material>(path);
            var shader = Shader.Find("HDRP/Lit");
            if (shader == null) throw new Exception("HDRP/Lit shader not found: is HDRP installed?");
            if (m == null) { m = new Material(shader) { name = name }; AssetDatabase.CreateAsset(m, path); }
            else m.shader = shader;
            return m;
        }

        static Material LitMaterial(string name, Color baseColor, float metallic, float smoothness, float coat)
        {
            var m = NewLit(name);
            m.SetColor("_BaseColor", baseColor);
            m.SetFloat("_Metallic", metallic);
            m.SetFloat("_Smoothness", smoothness);
            m.SetFloat("_CoatMask", coat);
            m.SetColor("_EmissiveColor", Color.black);
            m.SetMaterialType(MaterialId.LitStandard);
            HDMaterial.ValidateMaterial(m);
            EditorUtility.SetDirty(m);
            return m;
        }

        static Material TissueMaterial(int bucket, DiffusionProfileSettings skin, DiffusionProfileSettings fat, DiffusionProfileSettings muscle)
        {
            string name = "Tissue_" + bucket.ToString("00") + "_" + BucketName(bucket);
            var set = TissueTextures.Bake(bucket, TexSize, TissuePalette.BakedTone);
            var albedo = SaveTexture(name + "_Albedo", set.albedo, TexSize, false, false);
            var normal = SaveTexture(name + "_Normal", set.normal, TexSize, true, true);
            var mask = SaveTexture(name + "_Mask", set.mask, TexSize, true, false);
            var m = NewLit(name);
            m.SetTexture("_BaseColorMap", albedo);
            m.SetColor("_BaseColor", Color.white);
            m.SetTexture("_NormalMap", normal);
            m.SetFloat("_NormalScale", 1f);
            m.SetTexture("_MaskMap", mask);
            m.SetFloat("_Metallic", 0);
            m.SetFloat("_SmoothnessRemapMin", 0); m.SetFloat("_SmoothnessRemapMax", 1);
            m.SetFloat("_AORemapMin", 0); m.SetFloat("_AORemapMax", 1);
            // triplanar world mapping: one tile per TileMm of tissue on every cut face
            m.SetFloat("_UVBase", 5);
            m.SetFloat("_TexWorldScale", 1000f / TissueTextures.TileMm);
            m.SetFloat("_CoatMask", set.coat);
            int cls = bucket < 16 ? bucket % 8 : -1;
            bool coag = bucket >= 8 && bucket < 16;
            DiffusionProfileSettings prof = cls == 0 || cls == 1 ? skin : cls == 2 || cls == 3 || cls == 4 ? fat : cls >= 5 ? muscle : null;
            if (prof != null)
            {
                m.SetMaterialType(MaterialId.LitSSS);
                HDMaterial.SetDiffusionProfile(m, prof);
                // coagulated protein is opaque white: much less subsurface light transport
                m.SetFloat("_SubsurfaceMask", coag ? 0.25f : cls == 4 ? 0.4f : 1f);
            }
            else m.SetMaterialType(MaterialId.LitStandard);
            m.SetColor("_EmissiveColor", Color.black);
            HDMaterial.ValidateMaterial(m);
            EditorUtility.SetDirty(m);
            return m;
        }

        static Material ParticleMaterial(string name, BlendMode src, BlendMode dst)
        {
            string path = Mat + "/" + name + ".mat";
            var shader = Shader.Find("BovieSim/Particles");
            if (shader == null) throw new Exception("BovieSim/Particles shader failed to compile; check the Console.");
            var m = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (m == null) { m = new Material(shader) { name = name }; AssetDatabase.CreateAsset(m, path); }
            m.shader = shader;
            m.SetFloat("_SrcBlend", (float)src); m.SetFloat("_DstBlend", (float)dst);
            m.SetFloat("_Intensity", dst == BlendMode.One ? 40f : 1f);
            m.renderQueue = 3000;
            EditorUtility.SetDirty(m);
            return m;
        }

        // ---- downloads -----------------------------------------------------------------------------------
        static byte[] Fetch(string url, float timeoutS = 90)
        {
            using (var req = UnityWebRequest.Get(url))
            {
                req.timeout = (int)timeoutS;
                var op = req.SendWebRequest();
                var t0 = DateTime.Now;
                while (!op.isDone)
                {
                    System.Threading.Thread.Sleep(30);
                    if ((DateTime.Now - t0).TotalSeconds > timeoutS + 5) { req.Abort(); return null; }
                }
                if (req.result != UnityWebRequest.Result.Success) { Debug.LogWarning("[Bovie Sim] download failed: " + url + " (" + req.error + ")"); return null; }
                return req.downloadHandler.data;
            }
        }

        static Cubemap DownloadHdri()
        {
            foreach (var id in HdriCandidates)
            {
                string path = Dl + "/" + id + "_2k.hdr";
                if (!File.Exists(path))
                {
                    var meta = Fetch("https://api.polyhaven.com/files/" + id, 30);
                    if (meta == null) continue;
                    var mm = Regex.Match(Encoding.UTF8.GetString(meta), "\"url\"\\s*:\\s*\"(https://dl\\.polyhaven\\.org/[^\"]*?/2k/[^\"]*?\\.hdr)\"");
                    if (!mm.Success) continue;
                    var data = Fetch(mm.Groups[1].Value);
                    if (data == null) continue;
                    File.WriteAllBytes(path, data);
                    downloadLog.AppendLine("| HDRI | " + id + " | https://polyhaven.com/a/" + id + " | CC0 | Poly Haven | 2k .hdr, imported as cubemap |");
                }
                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
                var imp = (TextureImporter)AssetImporter.GetAtPath(path);
                if (imp == null) continue;
                imp.textureShape = TextureImporterShape.TextureCube;
                imp.generateCubemap = TextureImporterGenerateCubemap.AutoCubemap;
                imp.textureCompression = TextureImporterCompression.Uncompressed;
                imp.SaveAndReimport();
                var cube = AssetDatabase.LoadAssetAtPath<Cubemap>(path);
                if (cube != null) return cube;
            }
            Debug.LogWarning("[Bovie Sim] No HDRI could be downloaded; using a gradient sky. Drop any .hdr into " + Dl + " and re-run setup to use it.");
            return null;
        }

        static Material DrapeMaterial()
        {
            Texture2D albedo = null, normal = null;
            foreach (var id in FabricCandidates)
            {
                string zipPath = Dl + "/" + id + "_1K-JPG.zip";
                if (!File.Exists(zipPath))
                {
                    var data = Fetch("https://ambientcg.com/get?file=" + id + "_1K-JPG.zip");
                    if (data == null || data.Length < 1000) continue;
                    File.WriteAllBytes(zipPath, data);
                    downloadLog.AppendLine("| Drape fabric | " + id + " | https://ambientcg.com/view?id=" + id + " | CC0 | ambientCG | 1K JPG colour + NormalGL, tinted surgical blue |");
                }
                try
                {
                    using (var zip = ZipFile.OpenRead(zipPath))
                        foreach (var e in zip.Entries)
                        {
                            bool isCol = e.Name.EndsWith("_Color.jpg", StringComparison.OrdinalIgnoreCase), isNrm = e.Name.EndsWith("_NormalGL.jpg", StringComparison.OrdinalIgnoreCase);
                            if (!isCol && !isNrm) continue;
                            string outPath = Dl + "/" + e.Name;
                            using (var s = e.Open()) using (var f = File.Create(outPath)) s.CopyTo(f);
                            AssetDatabase.ImportAsset(outPath, ImportAssetOptions.ForceSynchronousImport);
                            var imp = (TextureImporter)AssetImporter.GetAtPath(outPath);
                            imp.textureType = isNrm ? TextureImporterType.NormalMap : TextureImporterType.Default;
                            imp.SaveAndReimport();
                            if (isCol) albedo = AssetDatabase.LoadAssetAtPath<Texture2D>(outPath); else normal = AssetDatabase.LoadAssetAtPath<Texture2D>(outPath);
                        }
                }
                catch (Exception ex) { Debug.LogWarning("[Bovie Sim] fabric archive unreadable: " + ex.Message); albedo = normal = null; }
                if (albedo != null) break;
            }
            Texture2D mask = null;
            if (albedo == null)
            {
                var set = TissueTextures.Fabric(TexSize);
                albedo = SaveTexture("Drape_Albedo", set.albedo, TexSize, false, false);
                normal = SaveTexture("Drape_Normal", set.normal, TexSize, true, true);
                mask = SaveTexture("Drape_Mask", set.mask, TexSize, true, false);
            }
            var m = NewLit("Drape");
            m.SetTexture("_BaseColorMap", albedo);
            // downloaded fabric is grey/neutral: tint it to drape blue
            m.SetColor("_BaseColor", mask == null ? new Color(0.30f, 0.52f, 0.80f) : Color.white);
            if (normal != null) { m.SetTexture("_NormalMap", normal); m.SetFloat("_NormalScale", 0.8f); }
            if (mask != null) { m.SetTexture("_MaskMap", mask); m.SetFloat("_SmoothnessRemapMin", 0); m.SetFloat("_SmoothnessRemapMax", 1); }
            else m.SetFloat("_Smoothness", 0.12f);
            m.SetFloat("_UVBase", 5);
            m.SetFloat("_TexWorldScale", 1000f / 30f);
            m.SetMaterialType(MaterialId.LitStandard);
            HDMaterial.ValidateMaterial(m);
            EditorUtility.SetDirty(m);
            return m;
        }

        // ---- volume / post -----------------------------------------------------------------------------------
        static T Override<T>(VolumeProfile p) where T : VolumeComponent
        {
            T c;
            if (!p.TryGet(out c)) { c = p.Add<T>(false); c.name = typeof(T).Name; AssetDatabase.AddObjectToAsset(c, p); }
            c.active = true;
            return c;
        }

        static VolumeProfile VolumeProfileAsset(Cubemap hdri, params DiffusionProfileSettings[] profiles)
        {
            var p = CreateOrLoad<VolumeProfile>(Root + "/BovieVolume.asset");
            var env = Override<VisualEnvironment>(p);
            if (hdri != null)
            {
                env.skyType.Override((int)SkyType.HDRI);
                var sky = Override<HDRISky>(p);
                sky.hdriSky.Override(hdri);
                // the room is dim next to the surgical light field (~1 000 lux vs ~120 000 lux)
                sky.exposure.Override(9.5f);
            }
            else
            {
                env.skyType.Override((int)SkyType.Gradient);
                var g = Override<GradientSky>(p);
                g.top.Override(new Color(0.55f, 0.62f, 0.66f)); g.middle.Override(new Color(0.42f, 0.50f, 0.52f)); g.bottom.Override(new Color(0.20f, 0.24f, 0.26f));
                g.exposure.Override(9.5f);
            }
            var exp = Override<Exposure>(p);
            exp.mode.Override(ExposureMode.Fixed);
            exp.fixedExposure.Override(15f);
            // Neutral keeps tissue hues (ACES shifts saturated reds and yellows toward orange)
            Override<Tonemapping>(p).mode.Override(TonemappingMode.Neutral);
            // the eye adapts to the 4300 K operating light: cool the image so white fascia reads white
            Override<WhiteBalance>(p).temperature.Override(-18f);
            Override<Bloom>(p).intensity.Override(0.12f);
            var ao = Override<ScreenSpaceAmbientOcclusion>(p);
            ao.intensity.Override(0.6f); ao.radius.Override(0.25f);
            var dof = Override<DepthOfField>(p);
            dof.focusMode.Override(DepthOfFieldMode.Manual);
            dof.nearFocusStart.Override(0f); dof.nearFocusEnd.Override(0.05f);
            dof.farFocusStart.Override(0.16f); dof.farFocusEnd.Override(0.45f);
            Override<Fog>(p).enabled.Override(false);
            // shadow cascades sized for a 6 cm field seen from ~15 cm
            Override<HDShadowSettings>(p).maxShadowDistance.Override(2f);
            Override<DiffusionProfileList>(p).diffusionProfiles.Override(profiles);
            EditorUtility.SetDirty(p);
            return p;
        }

        // ---- scene --------------------------------------------------------------------------------------------
        static void BuildScene(VolumeProfile profile)
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            var camGo = new GameObject("Main Camera") { tag = "MainCamera" };
            var cam = camGo.AddComponent<Camera>();
            cam.nearClipPlane = 0.005f; cam.farClipPlane = 50f; cam.fieldOfView = 35f;
            camGo.AddComponent<AudioListener>();
            var hdCam = camGo.AddComponent<HDAdditionalCameraData>();
            hdCam.antialiasing = HDAdditionalCameraData.AntialiasingMode.SubpixelMorphologicalAntiAliasing;
            camGo.transform.position = new Vector3(0.04f, 0.12f, 0.1f);
            camGo.transform.LookAt(Vector3.zero);

            // dual-dome surgical light: two heads from either side soften instrument shadows, as in theatre
            SurgicalHead("Surgical light A", new Vector3(-0.35f, 0.85f, 0.3f), 55000f);
            SurgicalHead("Surgical light B", new Vector3(0.4f, 0.8f, -0.25f), 40000f);

            var volGo = new GameObject("Post Volume");
            var vol = volGo.AddComponent<UnityEngine.Rendering.Volume>();
            vol.isGlobal = true; vol.priority = 1; vol.sharedProfile = profile;

            var runGo = new GameObject("Bovie Sim");
            var runner = runGo.AddComponent<SimulationRunner>();
            runner.viewCamera = cam; runner.postVolume = vol;

            EditorSceneManager.SaveScene(scene, ScenePath);
            EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(ScenePath, true) };
        }

        static void SurgicalHead(string name, Vector3 pos, float candela)
        {
            var go = new GameObject(name);
            go.transform.position = pos;
            go.transform.LookAt(Vector3.zero);
            go.AddHDLight(LightType.Spot);
            var l = go.GetComponent<Light>();
            l.spotAngle = 28f; l.innerSpotAngle = 18f;
            l.useColorTemperature = true; l.colorTemperature = 4300f;
            l.lightUnit = LightUnit.Candela;
            l.intensity = candela; // ~60-80 klux at the field from each head
            l.shadows = LightShadows.Soft;
            l.range = 3f;
        }

        static void WriteDownloadLog()
        {
            string path = Root + "/DOWNLOADED_ASSETS.md";
            var sb = new StringBuilder();
            sb.AppendLine("# Assets downloaded by Bovie Sim > Set Up Project");
            sb.AppendLine();
            sb.AppendLine("Generated " + DateTime.Now.ToString("u") + ". See Assets/BovieSim/Art/ATTRIBUTION.md for the full policy.");
            sb.AppendLine();
            sb.AppendLine("| Use | Asset | Source | Licence | Author | Modifications |");
            sb.AppendLine("|---|---|---|---|---|---|");
            sb.Append(downloadLog.Length > 0 ? downloadLog.ToString() : "| (none: procedural fallbacks or cached files used) | | | | | |\n");
            File.WriteAllText(path, sb.ToString());
        }
    }
}
