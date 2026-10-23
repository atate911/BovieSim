// Extra HDRP stubs for compile-checking the editor setup script.
namespace UnityEngine.Rendering
{
    public class ColorParameter : VolumeParameter<Color> { }
}
namespace UnityEngine.Rendering.HighDefinition
{
    public class HDRenderPipelineAsset : RenderPipelineAsset { protected override RenderPipeline CreatePipeline() { return null; } }
    public class DiffusionProfileSettings : ScriptableObject { public Color scatteringDistance; public float worldScale; }
    public enum MaterialId { LitSSS = 0, LitStandard = 1 }
    public static class HDMaterial
    {
        public static bool ValidateMaterial(Material m) { return true; }
        public static void SetDiffusionProfile(Material m, DiffusionProfileSettings p) { }
        public static bool SetMaterialType(this Material m, MaterialId t) { return true; }
    }
    public enum SkyType { HDRI = 1, Procedural = 2, Gradient = 3 }
    public class VisualEnvironment : VolumeComponent { public NoInterpIntParameter skyType = new NoInterpIntParameter(); }
    public class SkySettings : VolumeComponent { public FloatParameter exposure = new FloatParameter(0); }
    public class HDRISky : SkySettings { public CubemapParameter hdriSky = new CubemapParameter(); }
    public class GradientSky : SkySettings { public ColorParameter top = new ColorParameter(), middle = new ColorParameter(), bottom = new ColorParameter(); }
    public enum ExposureMode { Fixed = 0 }
    public class ExposureModeParameter : VolumeParameter<ExposureMode> { }
    public class Exposure : VolumeComponent { public ExposureModeParameter mode = new ExposureModeParameter(); public FloatParameter fixedExposure = new FloatParameter(0); }
    public enum TonemappingMode { None, Neutral, ACES }
    public class TonemappingModeParameter : VolumeParameter<TonemappingMode> { }
    public class Tonemapping : VolumeComponent { public TonemappingModeParameter mode = new TonemappingModeParameter(); }
    public class Bloom : VolumeComponent { public ClampedFloatParameter intensity = new ClampedFloatParameter(0, 0, 1); }
    public class ScreenSpaceAmbientOcclusion : VolumeComponent { public ClampedFloatParameter intensity = new ClampedFloatParameter(0, 0, 4), radius = new ClampedFloatParameter(2, 0.25f, 5); }
    public class Fog : VolumeComponent { public BoolParameter enabled = new BoolParameter(); }
    public class NoInterpMinFloatParameter : VolumeParameter<float> { }
    public class HDShadowSettings : VolumeComponent { public NoInterpMinFloatParameter maxShadowDistance = new NoInterpMinFloatParameter(); }
    public class DiffusionProfileSettingsParameter : VolumeParameter<DiffusionProfileSettings[]> { }
    public class DiffusionProfileList : VolumeComponent { public DiffusionProfileSettingsParameter diffusionProfiles = new DiffusionProfileSettingsParameter(); }
    public class HDAdditionalCameraData : MonoBehaviour { public enum AntialiasingMode { None, FastApproximateAntialiasing, TemporalAntialiasing } public AntialiasingMode antialiasing; }
}
