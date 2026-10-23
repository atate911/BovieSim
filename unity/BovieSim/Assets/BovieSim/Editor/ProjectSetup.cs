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
    /// linear colour space, DirectX 12 and legacy input, HDRP asset (ray tracing, reflections, high-quality soft
    /// shadows), diffusion profiles, baked tissue textures and HDRP Lit materials for every surface bucket,
    /// instrument/drape/blood/particle materials, the post-processing volume profile, CC0 HDRI and fabric downloads
    /// (with procedural fallbacks), the Main scene and the SimAssets registry.
    /// Everything it makes goes to Assets/BovieSim/Generated (git-ignored) and is safe to regenerate.
    /// </summary>
    [InitializeOnLoad]
    public static class ProjectSetup
    {
        const string Root = "Assets/BovieSim/Generated";
        const string Tex = Root + "/Textures", Mat = Root + "/Materials", Dl = Root + "/Downloads";
        const string RegistryPath = "Assets/BovieSim/Resources/BovieSim/SimAssets.asset";
        /// <summary>purchased instrument packs (FBX/OBJ + textures) go here; git-ignored, see docs/textures.md</summary>
        const string ModelsDir = "Assets/BovieSim/Models";
        const string ScenePath = Root + "/Main.unity";
        const int TexSize = 1024;

        /// <summary>Poly Haven HDRI ids tried in order (all CC0). The first one the API knows is used.</summary>
        static readonly string[] HdriCandidates = { "hospital_room", "hospital_room_2", "surgery", "studio_small_08", "studio_small_03" };

        static readonly StringBuilder downloadLog = new StringBuilder();

        static ProjectSetup()
        {
            EditorApplication.delayCall += () =>
            {
                if (Application.isBatchMode) return; // maintain.cmd runs setup through BatchCommands
                if (SessionState.GetBool("BovieSim.SetupOffered", false)) return;
                var existing = AssetDatabase.LoadAssetAtPath<SimAssets>(RegistryPath);
                if (existing != null && existing.setupVersion >= SimAssets.CurrentSetupVersion) return;
                SessionState.SetBool("BovieSim.SetupOffered", true);
                string msg = existing == null
                    ? "Set up the project now? This configures HDRP and DirectX 12, bakes tissue textures and materials, downloads a CC0 HDRI and drape fabric (optional), and creates the Main scene. It takes a minute or two."
                    : "Bovie Sim was updated and its generated assets (materials, lights, interface theme, scene) need rebuilding. Run Set Up Project now?";
                if (EditorUtility.DisplayDialog("Bovie Sim", msg, "Set up", "Later")) Run();
            };
        }

        [MenuItem("Bovie Sim/Set Up Project", priority = 0)]
        public static void Run() { RunSetup(true); }

        /// <summary>Runs the whole setup; true on success. Non-interactive runs (batch mode) report failure only in the log.</summary>
        public static bool RunSetup(bool interactive)
        {
            downloadLog.Length = 0;
            try
            {
                Progress("Project settings", 0.02f);
                ConfigurePlayer();
                foreach (var d in new[] { Root, Tex, Mat, Dl, ModelsDir, "Assets/BovieSim/Resources/BovieSim" }) Directory.CreateDirectory(d);
                AssetDatabase.Refresh();

                Progress("HDRP asset", 0.05f);
                var hdrp = CreateOrLoad<HDRenderPipelineAsset>(Root + "/BovieHDRP.asset");
                ConfigureHdrpAsset(hdrp);
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
                assets.strand = LitMaterial("Strand", TissuePalette.Strand, 0, 0.75f, 0.3f);
                Progress("Instrument textures (your files in Downloads, else ambientCG, CC0)", 0.63f);
                // satin-finish (bead-blasted) surgical stainless: a brushed-metal set gives the fine lines, normalised to stainless grey
                assets.steel = LitMaterial("Steel", new Color(0.80f, 0.80f, 0.81f), 1, 0.52f, 0);
                ApplyTextureSet(assets.steel, TextureSet("Steel", new[] { "brushed metal", "brushed steel" }, new[] { "Metal007", "Metal006", "Metal032" }), new Color(0.80f, 0.80f, 0.81f), 1000f / 12f, 0.5f, 1f);
                // off-white moulded plastic
                assets.pencilBody = LitMaterial("PencilBody", new Color(0.93f, 0.92f, 0.88f), 0, 0.45f, 0);
                ApplyTextureSet(assets.pencilBody, TextureSet("Plastic", new[] { "plastic" }, new[] { "Plastic010", "Plastic006" }), new Color(0.93f, 0.92f, 0.88f), 1000f / 20f, 0.5f, 0f);
                assets.pencilCutButton = LitMaterial("PencilCut", new Color(0.96f, 0.80f, 0.08f), 0, 0.55f, 0);
                assets.pencilCoagButton = LitMaterial("PencilCoag", new Color(0.10f, 0.34f, 0.84f), 0, 0.55f, 0);

                Progress("Drape textures (your files in Downloads, else ambientCG, CC0)", 0.66f);
                var drapeSet = TextureSet("Drape", new[] { "fabric nonwoven", "fabric felt", "fabric" }, new[] { "Fabric030", "Fabric026", "Fabric004" });
                assets.drape = DrapeMaterial(drapeSet);
                // absorbent reinforcement around the fenestration: a little lighter, matte; its own set if provided, else the drape's
                assets.drapePad = LitMaterial("DrapePad", new Color(0.30f, 0.45f, 0.63f), 0, 0.08f, 0);
                ApplyTextureSet(assets.drapePad, TextureSet("Pad", new string[0], new string[0]) ?? drapeSet, new Color(0.30f, 0.45f, 0.63f), 1000f / 30f, 0.6f, 0f);
                // incise film adhered to the skin at the aperture edge: clear and glossy, the prepped skin showing through
                assets.drapeFilm = LitMaterial("DrapeFilm", new Color(0.72f, 0.58f, 0.52f), 0, 0.78f, 0.4f);
                // cotton huck towels squaring the window: a slightly different blue from the drape, matte, fibrous
                assets.towel = LitMaterial("Towel", new Color(0.27f, 0.44f, 0.59f), 0, 0.06f, 0);
                var towelSet = TextureSet("Towel", new string[0], new string[0]);
                ApplyTextureSet(assets.towel, towelSet ?? drapeSet, new Color(0.27f, 0.44f, 0.59f), 1000f / 30f, 0.9f, 0f);
                // operating-light heads: the emissive face is what steel and wet tissue reflect (~150 000 nits at EV 15)
                assets.lampDome = LitMaterial("LampDome", new Color(0.82f, 0.82f, 0.82f), 0, 0.35f, 0);
                assets.lampDome.SetColor("_EmissiveColor", new Color(1f, 0.93f, 0.86f) * 150000f);
                HDMaterial.ValidateMaterial(assets.lampDome);
                Progress("Instrument models (Assets/BovieSim/Models)", 0.70f);
                ImportInstrumentPack(assets);
                assets.particlesAlpha = ParticleMaterial("ParticlesAlpha", BlendMode.SrcAlpha, BlendMode.OneMinusSrcAlpha);
                assets.particlesAdditive = ParticleMaterial("ParticlesAdditive", BlendMode.One, BlendMode.One);
                assets.uiTheme = RuntimeTheme();
                assets.teachingOverlay = OverlayMaterial();
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
                return true;
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                Debug.LogError("[Bovie Sim] Setup failed: " + e.Message);
                if (interactive) EditorUtility.DisplayDialog("Bovie Sim", "Setup failed: " + e.Message + "\nSee the Console for details.", "OK");
                return false;
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
            PlayerSettings.bundleVersion = BovieVersion.Version;
            // fixes the settings folder: %USERPROFILE%\AppData\LocalLow\Bovie Sim\BovieSim
            PlayerSettings.companyName = "Bovie Sim"; PlayerSettings.productName = "BovieSim";
            // CPU/GPU frame times for the F3 timing readout
            PlayerSettings.enableFrameTimingStats = true;
            // DirectX 12 first: HDRP ray tracing (DXR) needs it; DirectX 11 stays as the fallback
            var apis = PlayerSettings.GetGraphicsAPIs(BuildTarget.StandaloneWindows64);
            if (apis == null || apis.Length == 0 || apis[0] != GraphicsDeviceType.Direct3D12)
            {
                PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.StandaloneWindows64, false);
                PlayerSettings.SetGraphicsAPIs(BuildTarget.StandaloneWindows64, new[] { GraphicsDeviceType.Direct3D12, GraphicsDeviceType.Direct3D11 });
                Debug.Log("[Bovie Sim] Graphics API set to DirectX 12 (DirectX 11 fallback). Restart the editor for it to take effect; ray tracing needs it.");
            }
            // the sim reads the legacy Input class (mouse, keys) and the Input System (pen): needs "Both"
            var ps = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/ProjectSettings.asset");
            if (ps != null && ps.Length > 0)
            {
                var so = new SerializedObject(ps[0]);
                var p = so.FindProperty("activeInputHandler");
                if (p != null && p.intValue != 2) { p.intValue = 2; so.ApplyModifiedProperties(); Debug.Log("[Bovie Sim] Active Input Handling set to Both (restart the editor if prompted)."); }
            }
        }

        /// <summary>
        /// Render-pipeline features the sim uses, set on the serialized asset (the settings struct has no public
        /// setter): DXR ray tracing, screen-space reflections and global illumination (the ray-traced versions are
        /// switched on at runtime by tier), screen-space shadows (ray-traced shadows render through them) and
        /// high-quality soft-shadow filtering (PCSS from the lights' shape radius).
        /// </summary>
        static void ConfigureHdrpAsset(HDRenderPipelineAsset hdrp)
        {
            var so = new SerializedObject(hdrp);
            var set = new System.Collections.Generic.Dictionary<string, object>
            {
                { "m_RenderPipelineSettings.supportRayTracing", true },
                { "m_RenderPipelineSettings.supportSSR", true },
                { "m_RenderPipelineSettings.supportSSGI", true },
                { "m_RenderPipelineSettings.supportSSAO", true },
                { "m_RenderPipelineSettings.supportSubsurfaceScattering", true },
                { "m_RenderPipelineSettings.hdShadowInitParams.supportScreenSpaceShadows", true },
                { "m_RenderPipelineSettings.hdShadowInitParams.maxScreenSpaceShadowSlots", 8 },
                { "m_RenderPipelineSettings.hdShadowInitParams.shadowFilteringQuality", 2 }, // High
            };
            var missing = new System.Collections.Generic.List<string>();
            foreach (var kv in set)
            {
                var p = so.FindProperty(kv.Key);
                if (p == null) { missing.Add(kv.Key); continue; }
                if (kv.Value is bool) p.boolValue = (bool)kv.Value; else if (p.propertyType == SerializedPropertyType.Enum) p.enumValueIndex = (int)kv.Value; else p.intValue = (int)kv.Value;
            }
            so.ApplyModifiedPropertiesWithoutUndo();
            if (missing.Count > 0) Debug.LogWarning("[Bovie Sim] HDRP asset: could not find " + string.Join(", ", missing.ToArray()) + " (HDRP version differs); set them in Generated/BovieHDRP.asset by hand if ray tracing or reflections are missing.");
        }

        static string BucketName(int b)
        {
            int nc = TissueTextures.ClassNames.Length;
            if (b == 2 * nc) return "Desiccated";
            if (b == 2 * nc + 1) return "Carbonized";
            return TissueTextures.ClassNames[b % nc] + (b >= nc ? "Coag" : "");
        }

        static Material OverlayMaterial()
        {
            string path = Mat + "/TeachingOverlay.mat";
            var shader = Shader.Find("BovieSim/TeachingOverlay");
            if (shader == null) throw new Exception("BovieSim/TeachingOverlay shader failed to compile; check the Console.");
            var m = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (m == null) { m = new Material(shader) { name = "TeachingOverlay" }; AssetDatabase.CreateAsset(m, path); }
            m.shader = shader; m.renderQueue = 3000;
            EditorUtility.SetDirty(m);
            return m;
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
        static Texture2D SaveTexture(string name, Color[] px, int size, bool linear, bool normal) { return SaveTexture(name, px, size, size, linear, normal); }

        static Texture2D SaveTexture(string name, Color[] px, int width, int height, bool linear, bool normal)
        {
            var t = new Texture2D(width, height, TextureFormat.RGBA32, true, linear);
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
            imp.isReadable = true;
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
            int nc = TissueTextures.ClassNames.Length;
            int cls = bucket < 2 * nc ? bucket % nc : -1;
            bool coag = bucket >= nc && bucket < 2 * nc;
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

        // ---- texture sets: user files in Downloads, else CC0 downloads, else procedural --------------------------

        sealed class TexSet { public Texture2D color, normal, roughness, metalness; public string id; }

        static Texture2D ImportTexture(string path, bool normal, bool srgb)
        {
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
            var imp = (TextureImporter)AssetImporter.GetAtPath(path);
            if (imp == null) return null;
            imp.textureType = normal ? TextureImporterType.NormalMap : TextureImporterType.Default;
            imp.sRGBTexture = srgb && !normal;
            imp.wrapMode = TextureWrapMode.Repeat;
            imp.anisoLevel = 8;
            imp.isReadable = true; // the setup reads colours back (mean colour, derived normals)
            imp.SaveAndReimport();
            return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        }

        static string FindFile(string stem)
        {
            foreach (var ext in new[] { ".jpg", ".jpeg", ".png", ".tga" }) if (File.Exists(Dl + "/" + stem + ext)) return Dl + "/" + stem + ext;
            return null;
        }

        /// <summary>
        /// Texture set for a material slot: your own files `Downloads/&lt;Slot&gt;_Color.*` (+ `_NormalGL`, `_Roughness`,
        /// `_Metalness`) win; otherwise the first ambientCG material the API returns for the search terms, then the
        /// known ids; null when nothing is available (the procedural material stays). A missing normal map is derived
        /// from the colour.
        /// </summary>
        static TexSet TextureSet(string slot, string[] acgQueries, string[] acgIds)
        {
            var set = UserTextureSet(slot);
            if (set == null)
                foreach (var id in AmbientCgCandidates(acgQueries, acgIds)) { set = AmbientCgSet(slot, id); if (set != null) break; }
            if (set == null) return null;
            if (set.normal == null) set.normal = DerivedNormal(slot, set.color);
            return set;
        }

        static TexSet UserTextureSet(string slot)
        {
            string color = FindFile(slot + "_Color");
            if (color == null) return null;
            var set = new TexSet { id = "your " + slot + " files", color = ImportTexture(color, false, true) };
            string n = FindFile(slot + "_NormalGL") ?? FindFile(slot + "_Normal"), r = FindFile(slot + "_Roughness"), m = FindFile(slot + "_Metalness");
            if (n != null) set.normal = ImportTexture(n, true, false);
            if (r != null) set.roughness = ImportTexture(r, false, false);
            if (m != null) set.metalness = ImportTexture(m, false, false);
            downloadLog.AppendLine("| " + slot + " texture | " + Path.GetFileName(color) + " | (your file in Downloads) | (yours) | (yours) | see docs/textures.md |");
            return set.color != null ? set : null;
        }

        /// <summary>ambientCG asset ids: the API's results for each search term (first the 1K JPG links it lists), then the known ids.</summary>
        static System.Collections.Generic.List<string> AmbientCgCandidates(string[] queries, string[] ids)
        {
            var list = new System.Collections.Generic.List<string>();
            foreach (var q in queries)
            {
                var json = Fetch("https://ambientcg.com/api/v2/full_json?type=Material&q=" + Uri.EscapeDataString(q) + "&limit=12&include=downloadData", 30);
                if (json == null) continue;
                foreach (Match m in Regex.Matches(Encoding.UTF8.GetString(json), "https://ambientcg\\.com/get\\?file=([A-Za-z0-9]+)_1K-JPG\\.zip"))
                    if (!list.Contains(m.Groups[1].Value)) list.Add(m.Groups[1].Value);
            }
            foreach (var id in ids) if (!list.Contains(id)) list.Add(id);
            return list;
        }

        static TexSet AmbientCgSet(string slot, string id)
        {
            string zipPath = Dl + "/" + id + "_1K-JPG.zip";
            if (!File.Exists(zipPath))
            {
                var data = Fetch("https://ambientcg.com/get?file=" + id + "_1K-JPG.zip");
                if (data == null || data.Length < 1000) return null;
                File.WriteAllBytes(zipPath, data);
            }
            var set = new TexSet { id = id };
            try
            {
                using (var zip = ZipFile.OpenRead(zipPath))
                    foreach (var e in zip.Entries)
                    {
                        string kind = e.Name.EndsWith("_Color.jpg", StringComparison.OrdinalIgnoreCase) ? "color" : e.Name.EndsWith("_NormalGL.jpg", StringComparison.OrdinalIgnoreCase) ? "normal"
                                    : e.Name.EndsWith("_Roughness.jpg", StringComparison.OrdinalIgnoreCase) ? "roughness" : e.Name.EndsWith("_Metalness.jpg", StringComparison.OrdinalIgnoreCase) ? "metalness" : null;
                        if (kind == null) continue;
                        string outPath = Dl + "/" + e.Name;
                        using (var st = e.Open()) using (var f = File.Create(outPath)) st.CopyTo(f);
                        var tex = ImportTexture(outPath, kind == "normal", kind == "color");
                        if (kind == "color") set.color = tex; else if (kind == "normal") set.normal = tex; else if (kind == "roughness") set.roughness = tex; else set.metalness = tex;
                    }
            }
            catch (Exception ex) { Debug.LogWarning("[Bovie Sim] " + id + " archive unreadable: " + ex.Message); return null; }
            if (set.color == null) return null;
            downloadLog.AppendLine("| " + slot + " texture | " + id + " | https://ambientcg.com/view?id=" + id + " | CC0 | Lennart Demes / ambientCG | 1K JPG Color, NormalGL, Roughness; tinted / normalised in the material |");
            return set;
        }

        static Texture2D DerivedNormal(string slot, Texture2D color)
        {
            if (color == null) return null;
            try
            {
                var px = TissueTextures.NormalFromColor(color.GetPixels(), color.width, color.height, 2.5f);
                return SaveTexture(slot + "_DerivedNormal", px, color.width, color.height, true, true);
            }
            catch (Exception ex) { Debug.LogWarning("[Bovie Sim] could not derive a normal map for " + slot + ": " + ex.Message); return null; }
        }

        /// <summary>Mean colour of a readable texture (linear), sampled.</summary>
        static Color MeanColor(Texture2D t)
        {
            var px = t.GetPixels32();
            int step = Math.Max(1, px.Length / 20000); double r = 0, g = 0, b = 0; int n = 0;
            for (int i = 0; i < px.Length; i += step) { r += px[i].r; g += px[i].g; b += px[i].b; n++; }
            return new Color((float)(r / n / 255), (float)(g / n / 255), (float)(b / n / 255)).linear;
        }

        /// <summary>
        /// Puts a texture set on an HDRP Lit material with triplanar world mapping (one tile per `tileMm`): colour
        /// map tinted so a grey scan takes the slot's colour (a coloured photo is kept as it is), normal map with
        /// `normalScale`, and a mask map built from the roughness (and metalness) maps when present.
        /// </summary>
        static void ApplyTextureSet(Material m, TexSet set, Color target, float texWorldScale, float normalScale, float metallic)
        {
            if (set == null || set.color == null) return;
            m.SetTexture("_BaseColorMap", set.color);
            var mean = MeanColor(set.color);
            bool grey = Mathf.Max(mean.r, mean.g, mean.b) - Mathf.Min(mean.r, mean.g, mean.b) < 0.08f;
            if (grey)
            {
                var t = target.linear;
                m.SetColor("_BaseColor", new Color(Mathf.Clamp(t.r / Mathf.Max(mean.r, 0.02f), 0, 4), Mathf.Clamp(t.g / Mathf.Max(mean.g, 0.02f), 0, 4), Mathf.Clamp(t.b / Mathf.Max(mean.b, 0.02f), 0, 4), 1).gamma);
            }
            else m.SetColor("_BaseColor", Color.white);
            if (set.normal != null) { m.SetTexture("_NormalMap", set.normal); m.SetFloat("_NormalScale", normalScale); }
            if (set.roughness != null)
            {
                // HDRP mask map: R metallic, G ambient occlusion, B detail mask, A smoothness
                var rp = set.roughness.GetPixels(); var mp = set.metalness != null && set.metalness.width == set.roughness.width ? set.metalness.GetPixels() : null;
                var mask = new Color[rp.Length];
                for (int i = 0; i < rp.Length; i++) mask[i] = new Color(mp != null ? mp[i].r : metallic, 1, 1, 1 - rp[i].r);
                var maskTex = SaveTexture(m.name + "_Mask", mask, set.roughness.width, set.roughness.height, true, false);
                m.SetTexture("_MaskMap", maskTex);
                m.SetFloat("_SmoothnessRemapMin", 0); m.SetFloat("_SmoothnessRemapMax", 1);
                m.SetFloat("_AORemapMin", 0); m.SetFloat("_AORemapMax", 1);
            }
            m.SetFloat("_UVBase", 5);
            m.SetFloat("_TexWorldScale", texWorldScale);
            HDMaterial.ValidateMaterial(m);
            EditorUtility.SetDirty(m);
        }

        static Material DrapeMaterial(TexSet set)
        {
            var m = NewLit("Drape");
            m.SetMaterialType(MaterialId.LitStandard);
            var blue = new Color(0.24f, 0.41f, 0.59f); // surgical blue (sRGB)
            if (set != null) ApplyTextureSet(m, set, blue, 1000f / 30f, 0.8f, 0f);
            else
            {
                var proc = TissueTextures.Fabric(TexSize);
                m.SetTexture("_BaseColorMap", SaveTexture("Drape_Albedo", proc.albedo, TexSize, false, false));
                m.SetColor("_BaseColor", Color.white);
                m.SetTexture("_NormalMap", SaveTexture("Drape_Normal", proc.normal, TexSize, true, true)); m.SetFloat("_NormalScale", 0.8f);
                m.SetTexture("_MaskMap", SaveTexture("Drape_Mask", proc.mask, TexSize, true, false)); m.SetFloat("_SmoothnessRemapMin", 0); m.SetFloat("_SmoothnessRemapMax", 1);
                m.SetFloat("_UVBase", 5); m.SetFloat("_TexWorldScale", 1000f / 30f);
            }
            if (m.GetTexture("_MaskMap") == null) m.SetFloat("_Smoothness", 0.12f);
            HDMaterial.ValidateMaterial(m);
            EditorUtility.SetDirty(m);
            return m;
        }

        // ---- instrument pack: real models in place of the procedural ones --------------------------------------

        /// <summary>Real length (mm) of each pack item along its longest axis, from the pack's own OBJ: fixes the scale whatever units the file uses.</summary>
        static readonly System.Collections.Generic.Dictionary<string, float> PackLengthMm = new System.Collections.Generic.Dictionary<string, float>
        {
            { "tweezers", 114.7f }, { "scissors", 170.8f }, { "scissor_tweezers", 227.4f }, { "scalpel", 173.3f }, { "needle", 197f },
        };

        /// <summary>
        /// Instrument models dropped into Assets/BovieSim/Models (a purchased pack: FBX or OBJ plus textures, not committed).
        /// Meshes are matched by name: `tweezers` becomes the Adson forceps (split into its two arms, each straightened
        /// and fitted to the sim's arm frame: tip at the origin, +Y toward the hinge, mm), and scissors, scalpel and the
        /// ring-handled `scissor_tweezers` are laid on the drape beside the field. LOD meshes (`_low`) are skipped.
        /// Without a pack everything stays procedural.
        /// </summary>
        static void ImportInstrumentPack(SimAssets assets)
        {
            assets.forcepsArmA = null; assets.forcepsArmB = null; assets.packSteel = null; assets.dressing = new DressingItem[0];
            var meshes = new System.Collections.Generic.Dictionary<string, Mesh>();
            foreach (var guid in AssetDatabase.FindAssets("t:Model", new[] { ModelsDir }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                foreach (var o in AssetDatabase.LoadAllAssetsAtPath(path))
                {
                    var m = o as Mesh; if (m == null) continue;
                    string key = m.name.ToLowerInvariant();
                    if (key.EndsWith("_low") || meshes.ContainsKey(key)) continue;
                    meshes[key] = m;
                }
            }
            if (meshes.Count == 0) { Debug.Log("[Bovie Sim] No instrument models in " + ModelsDir + "; procedural instruments."); return; }
            Directory.CreateDirectory(Root + "/Models");
            assets.packSteel = PackSteelMaterial(FindPackTexture("items_scratches"));
            Mesh tweezers;
            if (meshes.TryGetValue("tweezers", out tweezers))
            {
                var arms = SplitForcepsArms(tweezers, PackLengthMm["tweezers"]);
                assets.forcepsArmA = SaveMesh(arms[0], "Forceps_ArmA"); assets.forcepsArmB = SaveMesh(arms[1], "Forceps_ArmB");
                downloadLog.AppendLine("| Adson forceps | tweezers (instrument pack) | " + ModelsDir + " | pack licence | pack author | split into two arms, straightened, fitted to the sim frame |");
            }
            var dressing = new System.Collections.Generic.List<DressingItem>();
            var places = new[] { new { key = "scissors", pos = new Vector3(-105f, 0, -70f), yaw = 28f }, new { key = "scissor_tweezers", pos = new Vector3(120f, 0, 75f), yaw = -18f }, new { key = "scalpel", pos = new Vector3(-85f, 0, 95f), yaw = 105f } };
            foreach (var pl in places)
            {
                Mesh src; if (!meshes.TryGetValue(pl.key, out src)) continue;
                var fitted = SaveMesh(FitFlat(src, PackLengthMm[pl.key]), "Dressing_" + pl.key);
                dressing.Add(new DressingItem { name = pl.key, mesh = fitted, material = assets.packSteel, positionMm = pl.pos, yawDeg = pl.yaw });
                downloadLog.AppendLine("| Field dressing | " + pl.key + " (instrument pack) | " + ModelsDir + " | pack licence | pack author | fitted flat, laid on the drape |");
            }
            assets.dressing = dressing.ToArray();
            ImportToolModels(assets);
            Debug.Log("[Bovie Sim] Instrument pack: " + string.Join(", ", new System.Collections.Generic.List<string>(meshes.Keys).ToArray()) + (tweezers != null ? "; forceps from the pack" : "; no tweezers mesh, procedural forceps") + "; " + dressing.Count + " items on the drape.");
        }

        // ---- modelled instruments: pencil.fbx, frazier.fbx, skin_hook.fbx, blade/needle/ball.fbx ------------------

        /// <summary>Projected +Y length (mm) of each modelled instrument, from the asset pack's README; fixes the scale whatever units the file uses.</summary>
        static readonly System.Collections.Generic.Dictionary<string, float> ToolLengthMm = new System.Collections.Generic.Dictionary<string, float>
        {
            { "pencil", 224.5f }, { "frazier", 115f }, { "skin_hook", 160f }, { "blade", 60f }, { "needle", 45f }, { "ball", 43f },
        };

        sealed class NodeMesh { public Mesh mesh; public Matrix4x4 toRoot; public string[] materialNames; public string path; }

        /// <summary>Model file named `name` (any extension Unity imports) anywhere under Models.</summary>
        static string FindModelFile(string name)
        {
            foreach (var guid in AssetDatabase.FindAssets(name + " t:Model", new[] { ModelsDir }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (string.Equals(Path.GetFileNameWithoutExtension(path), name, StringComparison.OrdinalIgnoreCase)) return path;
            }
            return null;
        }

        /// <summary>
        /// A node of an imported model with its mesh, its transform relative to the model root (this carries Unity's
        /// axis and unit conversion, so the mesh ends up in the Y-up frame the file was authored in) and its material
        /// names in submesh order. `nodeName` null takes the first mesh.
        /// </summary>
        static NodeMesh LoadNode(string modelPath, string nodeName)
        {
            var root = AssetDatabase.LoadAssetAtPath<GameObject>(modelPath);
            if (root == null) return null;
            foreach (var mf in root.GetComponentsInChildren<MeshFilter>(true))
            {
                if (mf.sharedMesh == null) continue;
                if (nodeName != null && !string.Equals(mf.name, nodeName, StringComparison.OrdinalIgnoreCase) && !string.Equals(mf.sharedMesh.name, nodeName, StringComparison.OrdinalIgnoreCase)) continue;
                var mr = mf.GetComponent<MeshRenderer>();
                var names = mr != null ? Array.ConvertAll(mr.sharedMaterials, m => m != null ? m.name : "") : new string[0];
                return new NodeMesh { mesh = mf.sharedMesh, toRoot = root.transform.worldToLocalMatrix * mf.transform.localToWorldMatrix, materialNames = names, path = modelPath };
            }
            return null;
        }

        static readonly System.Collections.Generic.Dictionary<string, Material> packMaterials = new System.Collections.Generic.Dictionary<string, Material>();

        /// <summary>
        /// HDRP Lit material for a pack material name from `models/textures/&lt;name&gt;_albedo/_normal/_roughness/_metallic.png`
        /// in the model's own UVs; without those files, the sim's own material for that part.
        /// </summary>
        static Material PackMaterial(string name, SimAssets assets)
        {
            string key = (name ?? "").ToLowerInvariant();
            Material cached; if (packMaterials.TryGetValue(key, out cached) && cached != null) return cached;
            Material fallback = key == "plastic" ? assets.pencilBody : key == "yellow" ? assets.pencilCutButton : key == "blue" ? assets.pencilCoagButton : key == "cable" ? LitMaterial("PackCable", new Color(0.92f, 0.92f, 0.90f), 0, 0.4f, 0) : assets.packSteel != null ? assets.packSteel : assets.steel;
            Texture2D albedo = FindPackTexture(key + "_albedo", true), normal = FindPackTexture(key + "_normal", false, true), rough = FindPackTexture(key + "_roughness", false), metal = FindPackTexture(key + "_metallic", false);
            if (albedo == null) { packMaterials[key] = fallback; return fallback; }
            bool metallic = key == "steel";
            var m = LitMaterial("PackModel_" + key, Color.white, metallic ? 1 : 0, 0.5f, 0);
            m.SetTexture("_BaseColorMap", albedo);
            if (normal != null) { m.SetTexture("_NormalMap", normal); m.SetFloat("_NormalScale", 1f); }
            if (rough != null)
            {
                var rp = rough.GetPixels(); var mp = metal != null && metal.width == rough.width && metal.height == rough.height ? metal.GetPixels() : null;
                var mask = new Color[rp.Length];
                for (int i = 0; i < rp.Length; i++) mask[i] = new Color(mp != null ? mp[i].r : (metallic ? 1 : 0), 1, 1, 1 - rp[i].r);
                m.SetTexture("_MaskMap", SaveTexture("PackModel_" + key + "_Mask", mask, rough.width, rough.height, true, false));
                m.SetFloat("_SmoothnessRemapMin", 0); m.SetFloat("_SmoothnessRemapMax", 1); m.SetFloat("_AORemapMin", 0); m.SetFloat("_AORemapMax", 1);
            }
            m.SetFloat("_UVBase", 0); // the model's own UVs
            HDMaterial.ValidateMaterial(m);
            EditorUtility.SetDirty(m);
            packMaterials[key] = m;
            return m;
        }

        /// <summary>
        /// A modelled part in the sim's frame: the node transform baked in (Unity's axis/unit conversion included), scaled
        /// so the +Y extent equals the known length (or by `fixedScale` for parts that must match another, such as the
        /// pencil buttons), optionally mirrored in X, with submeshes dropped or cropped below a height (the pencil's own
        /// electrode makes way for the swappable blade / needle / ball). If the file's long axis is not Y after baking,
        /// the longest axis is turned onto +Y with its tip at the origin and a warning says so.
        /// </summary>
        static ToolModel ToolPart(NodeMesh nm, SimAssets assets, string label, float expectedLengthMm, float fixedScale, bool mirrorX, string dropMaterial, string cropMaterial, float cropBelowMm)
        {
            float unused; return ToolPart(nm, assets, label, expectedLengthMm, fixedScale, mirrorX, dropMaterial, cropMaterial, cropBelowMm, out unused);
        }

        static ToolModel ToolPart(NodeMesh nm, SimAssets assets, string label, float expectedLengthMm, float fixedScale, bool mirrorX, string dropMaterial, string cropMaterial, float cropBelowMm, out float scaleUsed)
        {
            var src = nm.mesh;
            var v = src.vertices; var n = src.normals; var uv = src.uv;
            bool hasN = n != null && n.Length == v.Length;
            var pv = new Vector3[v.Length]; var pn = new Vector3[v.Length];
            for (int i = 0; i < v.Length; i++) { pv[i] = nm.toRoot.MultiplyPoint3x4(v[i]); pn[i] = hasN ? nm.toRoot.MultiplyVector(n[i]).normalized : Vector3.up; }
            // bounds and the long axis
            Vector3 mn = pv[0], mx = pv[0]; foreach (var p in pv) { mn = Vector3.Min(mn, p); mx = Vector3.Max(mx, p); }
            var size = mx - mn;
            if (size.y < Mathf.Max(size.x, size.z) * 0.999f)
            {
                int L = size.x >= size.z ? 0 : 2;
                Debug.LogWarning("[Bovie Sim] " + label + ": long axis came in as " + (L == 0 ? "X" : "Z") + ", turning it onto +Y (tip assumed at the low end).");
                for (int i = 0; i < v.Length; i++)
                {
                    var p = pv[i]; var q = pn[i];
                    pv[i] = L == 0 ? new Vector3(p.y, p.x, p.z) : new Vector3(p.x, p.z, -p.y);
                    pn[i] = L == 0 ? new Vector3(q.y, q.x, q.z) : new Vector3(q.x, q.z, -q.y);
                }
                mn = pv[0]; mx = pv[0]; foreach (var p in pv) { mn = Vector3.Min(mn, p); mx = Vector3.Max(mx, p); }
                size = mx - mn;
            }
            float scale = fixedScale > 0 ? fixedScale : expectedLengthMm / Mathf.Max(size.y, 1e-6f);
            scaleUsed = scale;
            bool flip = mirrorX;
            for (int i = 0; i < v.Length; i++)
            {
                var p = pv[i] * scale; if (fixedScale <= 0) p.y -= mn.y * scale;
                if (mirrorX) { p.x = -p.x; pn[i].x = -pn[i].x; }
                pv[i] = p;
            }
            // submeshes: keep / crop / drop by material name
            var mats = new System.Collections.Generic.List<Material>();
            var subs = new System.Collections.Generic.List<int[]>();
            for (int k = 0; k < src.subMeshCount; k++)
            {
                string mname = k < nm.materialNames.Length ? nm.materialNames[k] : "";
                if (dropMaterial != null && string.Equals(mname, dropMaterial, StringComparison.OrdinalIgnoreCase)) continue;
                var tris = src.GetTriangles(k);
                bool crop = cropMaterial != null && string.Equals(mname, cropMaterial, StringComparison.OrdinalIgnoreCase);
                var kept = new System.Collections.Generic.List<int>(tris.Length);
                for (int t = 0; t + 2 < tris.Length; t += 3)
                {
                    int a = tris[t], b = tris[t + 1], c = tris[t + 2];
                    if (crop && pv[a].y < cropBelowMm && pv[b].y < cropBelowMm && pv[c].y < cropBelowMm) continue;
                    if (flip) { kept.Add(a); kept.Add(c); kept.Add(b); } else { kept.Add(a); kept.Add(b); kept.Add(c); }
                }
                if (kept.Count == 0) continue;
                subs.Add(kept.ToArray()); mats.Add(PackMaterial(mname, assets));
            }
            var m = new Mesh { name = label };
            if (v.Length > 65000) m.indexFormat = IndexFormat.UInt32;
            m.vertices = pv; m.normals = pn; if (uv != null && uv.Length == v.Length) m.uv = uv;
            m.subMeshCount = subs.Count;
            for (int k = 0; k < subs.Count; k++) m.SetTriangles(subs[k], k);
            if (!hasN) m.RecalculateNormals();
            m.RecalculateTangents(); m.RecalculateBounds();
            return new ToolModel { mesh = SaveMesh(m, label), materials = mats.ToArray() };
        }

        /// <summary>The six modelled instruments of docs/textures.md: each replaces its procedural part when its file exists.</summary>
        static void ImportToolModels(SimAssets assets)
        {
            packMaterials.Clear();
            assets.pencilModel = new ToolModel(); assets.pencilCutModel = new ToolModel(); assets.pencilCoagModel = new ToolModel();
            assets.bladeModel = new ToolModel(); assets.needleModel = new ToolModel(); assets.ballModel = new ToolModel(); assets.frazierModel = new ToolModel(); assets.skinHookModel = new ToolModel();
            var found = new System.Collections.Generic.List<string>();
            string p = FindModelFile("pencil");
            if (p != null)
            {
                var body = LoadNode(p, "pencil");
                if (body != null)
                {
                    // the pencil's own electrode (steel submesh below the nose at 26 mm) makes way for the swappable blade / needle / ball
                    float bodyScale;
                    assets.pencilModel = ToolPart(body, assets, "Model_PencilBody", ToolLengthMm["pencil"], 0, false, null, "steel", 26f, out bodyScale);
                    var cut = LoadNode(p, "pencil_cut_button"); var coag = LoadNode(p, "pencil_coag_button");
                    if (cut != null) assets.pencilCutModel = ToolPart(cut, assets, "Model_PencilCut", 0, bodyScale, false, null, null, 0);
                    if (coag != null) assets.pencilCoagModel = ToolPart(coag, assets, "Model_PencilCoag", 0, bodyScale, false, null, null, 0);
                    found.Add("pencil" + (cut != null && coag != null ? " with switches" : " (switch meshes missing: procedural switches)"));
                }
            }
            // the Frazier is mirrored so its handle bends to the same side as the procedural one
            var simple = new[] { new { file = "frazier", mirror = true }, new { file = "skin_hook", mirror = false }, new { file = "blade", mirror = false }, new { file = "needle", mirror = false }, new { file = "ball", mirror = false } };
            foreach (var t in simple)
            {
                string path = FindModelFile(t.file); if (path == null) continue;
                var node = LoadNode(path, null); if (node == null) continue;
                var part = ToolPart(node, assets, "Model_" + t.file, ToolLengthMm[t.file], 0, t.mirror, null, null, 0);
                switch (t.file) { case "frazier": assets.frazierModel = part; break; case "skin_hook": assets.skinHookModel = part; break; case "blade": assets.bladeModel = part; break; case "needle": assets.needleModel = part; break; default: assets.ballModel = part; break; }
                found.Add(t.file);
            }
            if (found.Count > 0) { Debug.Log("[Bovie Sim] Modelled instruments: " + string.Join(", ", found.ToArray())); downloadLog.AppendLine("| Modelled instruments | " + string.Join(", ", found.ToArray()) + " | " + ModelsDir + " | generated for this project | you | fitted to the sim frame by Set Up Project |"); }
        }

        static Texture2D FindPackTexture(string name, bool srgb, bool normal = false)
        {
            foreach (var guid in AssetDatabase.FindAssets(name + " t:Texture2D", new[] { ModelsDir }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (string.Equals(Path.GetFileNameWithoutExtension(path), name, StringComparison.OrdinalIgnoreCase)) return ImportTexture(path, normal, srgb);
            }
            return null;
        }

        static Texture2D FindPackTexture(string name) { return FindPackTexture(name, false); }

        /// <summary>Satin stainless for the pack meshes: the pack's scratch map (in its own UVs) gives scratched smoothness and a derived normal map.</summary>
        static Material PackSteelMaterial(Texture2D scratches)
        {
            var m = LitMaterial("PackSteel", new Color(0.80f, 0.80f, 0.81f), 1, 0.55f, 0);
            if (scratches != null)
            {
                try
                {
                    var px = scratches.GetPixels(); int w = scratches.width, h = scratches.height;
                    double sum = 0; for (int i = 0; i < px.Length; i += 7) sum += px[i].r; float mean = (float)(sum / ((px.Length + 6) / 7));
                    var mask = new Color[px.Length];
                    for (int i = 0; i < px.Length; i++) { float scratch = Mathf.Clamp01((mean - px[i].r) / Mathf.Max(mean, 0.05f)); mask[i] = new Color(1, 1, 1, 0.6f - 0.45f * scratch); }
                    m.SetTexture("_MaskMap", SaveTexture("PackSteel_Mask", mask, w, h, true, false));
                    m.SetFloat("_SmoothnessRemapMin", 0); m.SetFloat("_SmoothnessRemapMax", 1); m.SetFloat("_AORemapMin", 0); m.SetFloat("_AORemapMax", 1);
                    m.SetTexture("_NormalMap", SaveTexture("PackSteel_Normal", TissueTextures.NormalFromColor(px, w, h, 1.5f), w, h, true, true));
                    m.SetFloat("_NormalScale", 0.6f);
                    m.SetFloat("_UVBase", 0); // the pack's own UVs
                }
                catch (Exception ex) { Debug.LogWarning("[Bovie Sim] pack scratch map unusable: " + ex.Message); }
            }
            HDMaterial.ValidateMaterial(m);
            EditorUtility.SetDirty(m);
            return m;
        }

        static Mesh SaveMesh(Mesh m, string name)
        {
            string path = Root + "/Models/" + name + ".asset";
            var existing = AssetDatabase.LoadAssetAtPath<Mesh>(path);
            if (existing != null) AssetDatabase.DeleteAsset(path);
            m.name = name;
            AssetDatabase.CreateAsset(m, path);
            return AssetDatabase.LoadAssetAtPath<Mesh>(path);
        }

        /// <summary>Axis indices of a bounds: longest (length), shortest (thickness) and the remaining one (width / splay).</summary>
        static void PrincipalAxes(Bounds b, out int longest, out int shortest, out int other)
        {
            var sz = b.size; longest = 0; shortest = 0;
            for (int k = 1; k < 3; k++) { if (sz[k] > sz[longest]) longest = k; if (sz[k] < sz[shortest]) shortest = k; }
            if (longest == shortest) { longest = 0; shortest = 1; }
            other = 3 - longest - shortest;
        }

        static float ExtentAcross(Vector3[] v, int along, int across, float lo, float hi)
        {
            float mn = float.MaxValue, mx = float.MinValue;
            foreach (var p in v) if (p[along] >= lo && p[along] <= hi) { mn = Mathf.Min(mn, p[across]); mx = Mathf.Max(mx, p[across]); }
            return mx > mn ? mx - mn : 0;
        }

        /// <summary>Sub-mesh from a triangle subset with a vertex transform (positions) and a rotation for normals; winding flipped when the transform mirrors.</summary>
        static Mesh BuildPart(Mesh src, System.Collections.Generic.List<int> triIdx, Func<Vector3, Vector3> map, Func<Vector3, Vector3> mapN, bool flip)
        {
            var v = src.vertices; var n = src.normals; var uv = src.uv; var tris = src.triangles;
            var remap = new System.Collections.Generic.Dictionary<int, int>();
            var ov = new System.Collections.Generic.List<Vector3>(); var on = new System.Collections.Generic.List<Vector3>(); var ouv = new System.Collections.Generic.List<Vector2>(); var ot = new System.Collections.Generic.List<int>();
            foreach (int t in triIdx)
            {
                int[] c = { tris[t * 3], tris[t * 3 + 1], tris[t * 3 + 2] };
                if (flip) { int tmp = c[1]; c[1] = c[2]; c[2] = tmp; }
                foreach (int i in c)
                {
                    int j;
                    if (!remap.TryGetValue(i, out j))
                    {
                        j = ov.Count; remap[i] = j;
                        ov.Add(map(v[i]));
                        on.Add(n != null && n.Length == v.Length ? mapN(n[i]).normalized : Vector3.up);
                        ouv.Add(uv != null && uv.Length == v.Length ? uv[i] : Vector2.zero);
                    }
                    ot.Add(j);
                }
            }
            var m = new Mesh();
            if (ov.Count > 65000) m.indexFormat = IndexFormat.UInt32;
            m.SetVertices(ov); m.SetNormals(on); m.SetUVs(0, ouv); m.SetTriangles(ot, 0);
            if (n == null || n.Length != v.Length) m.RecalculateNormals();
            m.RecalculateTangents(); m.RecalculateBounds();
            return m;
        }

        /// <summary>
        /// Splits a one-piece tweezers mesh into its two arms (by the side of the splay axis), maps each into the sim's arm
        /// frame (X thickness, +Y from the tip toward the hinge, Z the closing direction, mm) and straightens it so the
        /// sim's per-arm pose sets the splay. Returns { arm A (local -Z toward the other arm), arm B }.
        /// </summary>
        static Mesh[] SplitForcepsArms(Mesh src, float realLengthMm)
        {
            var v = src.vertices; var b = src.bounds;
            int L, T, S; PrincipalAxes(b, out L, out T, out S);
            float lMin = b.min[L], lMax = b.max[L], span = Mathf.Max(lMax - lMin, 1e-6f);
            // the arms spread apart toward the tips and join at the heel: the wider end is the tip end
            bool tipAtMax = ExtentAcross(v, L, S, lMax - 0.15f * span, lMax) > ExtentAcross(v, L, S, lMin, lMin + 0.15f * span);
            float sCentre = b.center[S], tCentre = b.center[T], scale = realLengthMm / span;
            Func<Vector3, Vector3> map = p => new Vector3((p[T] - tCentre) * scale, (tipAtMax ? lMax - p[L] : p[L] - lMin) * scale, -(p[S] - sCentre) * scale);
            Func<Vector3, Vector3> mapN = p => new Vector3(p[T], tipAtMax ? -p[L] : p[L], -p[S]);
            Vector3 ex = Vector3.zero, ey = Vector3.zero, ez = Vector3.zero; ex[0] = 1; ey[1] = 1; ez[2] = 1;
            bool flip = Vector3.Dot(Vector3.Cross(mapN(ex), mapN(ey)), mapN(ez)) < 0;
            var tris = src.triangles; var result = new Mesh[2];
            for (int side = 0; side < 2; side++)
            {
                var idx = new System.Collections.Generic.List<int>();
                for (int t = 0; t < tris.Length / 3; t++)
                {
                    float sc = (v[tris[t * 3]][S] + v[tris[t * 3 + 1]][S] + v[tris[t * 3 + 2]][S]) / 3 - sCentre;
                    if (side == 0 ? sc < 0 : sc >= 0) idx.Add(t);
                }
                var arm = BuildPart(src, idx, map, mapN, flip);
                Straighten(arm, realLengthMm);
                result[side] = arm;
            }
            return result;
        }

        /// <summary>Rotates an arm about X so its tip-to-heel line runs along +Y, then puts the tip at the origin.</summary>
        static void Straighten(Mesh arm, float lengthMm)
        {
            var v = arm.vertices; var n = arm.normals;
            Vector3 tip = Vector3.zero, heel = Vector3.zero; int nt = 0, nh = 0;
            foreach (var p in v) { if (p.y < 0.06f * lengthMm) { tip += p; nt++; } else if (p.y > 0.85f * lengthMm) { heel += p; nh++; } }
            if (nt == 0 || nh == 0) return;
            tip /= nt; heel /= nh;
            float ang = Mathf.Atan2(heel.z - tip.z, heel.y - tip.y) * Mathf.Rad2Deg;
            // Unity's +X rotation takes +Y toward +Z, so a heel leaning toward +Z (positive angle) comes back with -ang
            var rot = Quaternion.AngleAxis(-ang, Vector3.right);
            var rtip = rot * tip;
            for (int i = 0; i < v.Length; i++) { v[i] = rot * v[i] - new Vector3(rtip.x, 0, rtip.z); if (n != null && n.Length == v.Length) n[i] = rot * n[i]; }
            float minY = float.MaxValue; foreach (var p in v) minY = Mathf.Min(minY, p.y);
            for (int i = 0; i < v.Length; i++) v[i].y -= minY;
            arm.vertices = v; if (n != null && n.Length == v.Length) arm.normals = n;
            arm.RecalculateTangents(); arm.RecalculateBounds();
        }

        /// <summary>A pack instrument fitted to lie flat: length along X, thickness up (Y), centred, resting on y = 0, scaled to its real length (mm).</summary>
        static Mesh FitFlat(Mesh src, float realLengthMm)
        {
            var b = src.bounds; int L, T, S; PrincipalAxes(b, out L, out T, out S);
            float scale = realLengthMm / Mathf.Max(b.size[L], 1e-6f);
            var c = b.center; float tMin = b.min[T];
            Func<Vector3, Vector3> map = p => new Vector3((p[L] - c[L]) * scale, (p[T] - tMin) * scale, (p[S] - c[S]) * scale);
            Func<Vector3, Vector3> mapN = p => new Vector3(p[L], p[T], p[S]);
            Vector3 ex = Vector3.zero, ey = Vector3.zero, ez = Vector3.zero; ex[0] = 1; ey[1] = 1; ez[2] = 1;
            bool flip = Vector3.Dot(Vector3.Cross(mapN(ex), mapN(ey)), mapN(ez)) < 0;
            var all = new System.Collections.Generic.List<int>(); for (int t = 0; t < src.triangles.Length / 3; t++) all.Add(t);
            return BuildPart(src, all, map, mapN, flip);
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
                // the theatre is a bright room (1 000-2 000 lux) but dim next to the light field; this keeps its
                // walls and ceiling visible in satin steel and wet tissue without flattening the field's shadows
                sky.exposure.Override(12.5f);
            }
            else
            {
                env.skyType.Override((int)SkyType.Gradient);
                var g = Override<GradientSky>(p);
                g.top.Override(new Color(0.55f, 0.62f, 0.66f)); g.middle.Override(new Color(0.42f, 0.50f, 0.52f)); g.bottom.Override(new Color(0.20f, 0.24f, 0.26f));
                g.exposure.Override(12.5f);
            }
            var exp = Override<Exposure>(p);
            exp.mode.Override(ExposureMode.Fixed);
            exp.fixedExposure.Override(15f);
            // Neutral keeps tissue hues (ACES shifts saturated reds and yellows toward orange)
            Override<Tonemapping>(p).mode.Override(TonemappingMode.Neutral);
            // the eye adapts to the 4300 K operating light: cool the image so white fascia reads white
            Override<WhiteBalance>(p).temperature.Override(-18f);
            Override<Bloom>(p).intensity.Override(0.12f);
            // ambient occlusion at wound scale (radius in metres: 1.5 cm), not room scale
            var ao = Override<ScreenSpaceAmbientOcclusion>(p);
            ao.intensity.Override(0.8f); ao.radius.Override(0.015f);
            // contact shadows ground the instrument tip and the wound edges where shadow maps are too coarse
            var contact = Override<ContactShadows>(p);
            contact.enable.Override(true); contact.length.Override(0.01f); contact.opacity.Override(0.8f); contact.maxDistance.Override(2f);
            // reflections, global illumination, ray-traced subsurface and ray-tracing settings exist in the profile so
            // the runtime can switch them by detail level and ray-tracing tier (SimulationRunner.ApplyRayTracing)
            var ssr = Override<ScreenSpaceReflection>(p);
            ssr.enabled.Override(false); ssr.minSmoothness.Override(0.45f); ssr.smoothnessFadeStart.Override(0.55f);
            Override<GlobalIllumination>(p).enable.Override(false);
            Override<SubSurfaceScattering>(p).rayTracing.Override(false);
            var rts = Override<RayTracingSettings>(p);
            rts.extendShadowCulling.Override(true); rts.extendCameraCulling.Override(true);
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
            var hd = go.AddHDLight(LightType.Spot);
            var l = go.GetComponent<Light>();
            l.spotAngle = 28f; l.innerSpotAngle = 18f;
            l.useColorTemperature = true; l.colorTemperature = 4300f;
            l.lightUnit = LightUnit.Candela;
            l.intensity = candela; // ~60-80 klux at the field from each head
            l.shadows = LightShadows.Soft;
            l.range = 3f;
            if (hd != null)
            {
                // a 50 cm light dome, not a point: penumbrae widen with distance from the blocker (PCSS, and the
                // ray-traced shadows sample the same disc), sharp where the instrument touches tissue
                hd.shapeRadius = 0.25f;
                hd.useContactShadow.useOverride = true; hd.useContactShadow.@override = true;
            }
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
