// Compile-check stubs for APIs absent from the UnityEngine 2021.3 reference assemblies (Unity 6 / SRP Core / HDRP 17).
using System;
namespace UnityEngine { public enum LightUnit { Lumen, Candela, Lux, Nits, Ev100 } }
namespace UnityEngine.Rendering
{
    public class VolumeParameter<T> { public T value; public bool overrideState; public void Override(T x) { value = x; overrideState = true; } }
    public class FloatParameter : VolumeParameter<float> { public FloatParameter(float v, bool o = false) { } }
    public class MinFloatParameter : FloatParameter { public MinFloatParameter(float v, float m, bool o = false) : base(v, o) { } }
    public class ClampedFloatParameter : FloatParameter { public ClampedFloatParameter(float v, float a, float b, bool o = false) : base(v, o) { } }
    public class IntParameter : VolumeParameter<int> { public IntParameter(int v, bool o = false) { } }
    public class ClampedIntParameter : IntParameter { public ClampedIntParameter(int v, int a, int b, bool o = false) : base(v, o) { } }
    public class BoolParameter : VolumeParameter<bool> { }
    public class CubemapParameter : VolumeParameter<Texture> { }
    public class NoInterpIntParameter : VolumeParameter<int> { }
    public class VolumeComponent : ScriptableObject { public bool active = true; }
    public class VolumeProfile : ScriptableObject
    {
        public System.Collections.Generic.List<VolumeComponent> components = new System.Collections.Generic.List<VolumeComponent>();
        public bool TryGet<T>(out T c) where T : VolumeComponent { c = null; return false; }
        public T Add<T>(bool overrides = false) where T : VolumeComponent { return null; }
        public bool Has<T>() where T : VolumeComponent { return false; }
    }
    public class Volume : MonoBehaviour { public bool isGlobal; public float priority, weight = 1; public VolumeProfile sharedProfile, profile; }
}
namespace UnityEngine.Rendering.HighDefinition
{
    public enum FocusDistanceMode { Volume, Camera }
    public enum DepthOfFieldMode { Off, UsePhysicalCamera, Manual }
    public class DepthOfFieldModeParameter : VolumeParameter<DepthOfFieldMode> { }
    public class DepthOfField : VolumeComponent
    {
        public DepthOfFieldModeParameter focusMode = new DepthOfFieldModeParameter();
        public MinFloatParameter nearFocusStart = new MinFloatParameter(0, 0), nearFocusEnd = new MinFloatParameter(0, 0), farFocusStart = new MinFloatParameter(0, 0), farFocusEnd = new MinFloatParameter(0, 0);
    }
    // ray casting / ray tracing volume parameters (HDRP 17)
    public enum RayCastingMode { RayMarching = 1, RayTracing = 2, Mixed = 3 }
    public class RayCastingModeParameter : VolumeParameter<RayCastingMode> { }
    public enum RayTracingMode { Performance, Quality }
    public class RayTracingModeParameter : VolumeParameter<RayTracingMode> { }
    public class ScreenSpaceAmbientOcclusion : VolumeComponent
    {
        public ClampedFloatParameter intensity = new ClampedFloatParameter(0, 0, 4), radius = new ClampedFloatParameter(2, 0.25f, 5);
        public BoolParameter rayTracing = new BoolParameter(), denoise = new BoolParameter();
        public ClampedFloatParameter rayLength = new ClampedFloatParameter(3, 0, 50);
        public ClampedIntParameter sampleCount = new ClampedIntParameter(2, 1, 64);
    }
    public class ScreenSpaceReflection : VolumeComponent
    {
        public BoolParameter enabled = new BoolParameter(), denoise = new BoolParameter(), fullResolution = new BoolParameter();
        public RayCastingModeParameter tracing = new RayCastingModeParameter();
        public RayTracingModeParameter mode = new RayTracingModeParameter();
        public ClampedFloatParameter minSmoothness = new ClampedFloatParameter(0.9f, 0, 1), smoothnessFadeStart = new ClampedFloatParameter(0.9f, 0, 1), clampValue = new ClampedFloatParameter(1, 0, 10);
        public MinFloatParameter rayLength = new MinFloatParameter(50, 0.01f);
        public ClampedIntParameter sampleCount = new ClampedIntParameter(1, 1, 32), bounceCount = new ClampedIntParameter(1, 1, 8), denoiserRadius = new ClampedIntParameter(8, 1, 32);
    }
    public class GlobalIllumination : VolumeComponent
    {
        public BoolParameter enable = new BoolParameter(), denoise = new BoolParameter(), fullResolution = new BoolParameter();
        public RayCastingModeParameter tracing = new RayCastingModeParameter();
        public RayTracingModeParameter mode = new RayTracingModeParameter();
        public MinFloatParameter rayLength = new MinFloatParameter(50, 0.01f);
        public ClampedFloatParameter clampValue = new ClampedFloatParameter(10, 0.001f, 10);
        public ClampedIntParameter sampleCount = new ClampedIntParameter(1, 1, 32), bounceCount = new ClampedIntParameter(1, 1, 8);
    }
    public class SubSurfaceScattering : VolumeComponent { public BoolParameter rayTracing = new BoolParameter(); public ClampedIntParameter sampleCount = new ClampedIntParameter(1, 1, 32); }
    public class ContactShadows : VolumeComponent
    {
        public BoolParameter enable = new BoolParameter(), rayTracing = new BoolParameter();
        public ClampedFloatParameter length = new ClampedFloatParameter(0.15f, 0, 1), opacity = new ClampedFloatParameter(1, 0, 1), maxDistance = new ClampedFloatParameter(50, 0, 1000);
        public ClampedIntParameter sampleCount = new ClampedIntParameter(10, 4, 64);
    }
    public class RayTracingSettings : VolumeComponent
    {
        public ClampedFloatParameter rayBias = new ClampedFloatParameter(0.001f, 0, 0.1f);
        public BoolParameter extendShadowCulling = new BoolParameter(), extendCameraCulling = new BoolParameter();
    }
    public class BoolScalableSettingValue { public bool useOverride; public bool @override; public int level; }
    public class HDAdditionalLightData : MonoBehaviour
    {
        public void SetShadowResolution(int r) { } public void SetShadowResolutionOverride(bool o) { }
        public float shapeRadius;
        public bool useRayTracedShadows, filterTracedShadow;
        public int numRayTracingSamples, filterSizeTraced;
        public BoolScalableSettingValue useContactShadow = new BoolScalableSettingValue();
    }
    public class HDAdditionalCameraData : MonoBehaviour { public enum AntialiasingMode { None, FastApproximateAntialiasing, TemporalAntialiasing, SubpixelMorphologicalAntiAliasing } public AntialiasingMode antialiasing; }
    public static class GameObjectExtension { public static HDAdditionalLightData AddHDLight(this GameObject g, LightType t) { return null; } }
}
