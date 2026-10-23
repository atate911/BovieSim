// Minimal stand-ins for the HDRP shader-library symbols Particles.shader uses, so glslang can check its HLSL.
float4x4 _ViewProj; float4x4 _View; float4 _CamPos; float4 _Time; float _Exp;
#define UNITY_MATRIX_V _View
#define _WorldSpaceCameraPos _CamPos.xyz
float3 GetCameraRelativePositionWS(float3 p) { return p - _CamPos.xyz; }
float4 TransformWorldToHClip(float3 p) { return mul(_ViewProj, float4(p, 1)); }
float GetCurrentExposureMultiplier() { return _Exp; }
float4x4 _Model;
float3 TransformObjectToWorld(float3 p) { return mul(_Model, float4(p, 1)).xyz - _CamPos.xyz; }
float3 TransformObjectToWorldNormal(float3 n) { return normalize(mul((float3x3)_Model, n)); }
