// Compile-check stubs for APIs absent from the UnityEngine 2021.3 reference assemblies (Unity 6 / SRP Core / HDRP 17).
using System;
namespace UnityEngine { public enum LightUnit { Lumen, Candela, Lux, Nits, Ev100 } }
namespace UnityEngine.Rendering
{
    public class VolumeParameter<T> { public T value; public bool overrideState; public void Override(T x) { value = x; overrideState = true; } }
    public class FloatParameter : VolumeParameter<float> { public FloatParameter(float v, bool o = false) { } }
    public class MinFloatParameter : FloatParameter { public MinFloatParameter(float v, float m, bool o = false) : base(v, o) { } }
    public class ClampedFloatParameter : FloatParameter { public ClampedFloatParameter(float v, float a, float b, bool o = false) : base(v, o) { } }
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
    public class ScreenSpaceAmbientOcclusion : VolumeComponent { public ClampedFloatParameter intensity = new ClampedFloatParameter(0, 0, 4), radius = new ClampedFloatParameter(2, 0.25f, 5); }
    public class HDAdditionalLightData : MonoBehaviour { public void SetShadowResolution(int r) { } public void SetShadowResolutionOverride(bool o) { } }
    public class HDAdditionalCameraData : MonoBehaviour { public enum AntialiasingMode { None, FastApproximateAntialiasing, TemporalAntialiasing, SubpixelMorphologicalAntiAliasing } public AntialiasingMode antialiasing; }
    public static class GameObjectExtension { public static HDAdditionalLightData AddHDLight(this GameObject g, LightType t) { return null; } }
}
