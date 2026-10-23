// Managed stand-ins for the few UnityEngine types Core uses, so Core + EditMode tests run under plain Mono.
using System;
namespace UnityEngine {
  public static class Mathf {
    public const float PI = (float)Math.PI, Deg2Rad = PI / 180f, Rad2Deg = 180f / PI;
    public static float Clamp(float v, float a, float b) { return v < a ? a : v > b ? b : v; }
    public static int Clamp(int v, int a, int b) { return v < a ? a : v > b ? b : v; }
    public static float Clamp01(float v) { return Clamp(v, 0, 1); }
    public static float Max(float a, float b) { return a > b ? a : b; } public static float Min(float a, float b) { return a < b ? a : b; }
    public static int Max(int a, int b) { return a > b ? a : b; } public static int Min(int a, int b) { return a < b ? a : b; }
    public static float Abs(float a) { return Math.Abs(a); } public static float Sqrt(float a) { return (float)Math.Sqrt(a); }
    public static float Sin(float a) { return (float)Math.Sin(a); } public static float Cos(float a) { return (float)Math.Cos(a); }
    public static float Exp(float a) { return (float)Math.Exp(a); } public static float Floor(float a) { return (float)Math.Floor(a); }
    public static int RoundToInt(float a) { return (int)Math.Round(a); } public static int FloorToInt(float a) { return (int)Math.Floor(a); }
    public static float Lerp(float a, float b, float t) { return a + (b - a) * Clamp01(t); }
  }
  public struct Vector3 {
    public float x, y, z;
    public Vector3(float x, float y, float z) { this.x = x; this.y = y; this.z = z; }
    public static Vector3 zero { get { return new Vector3(0, 0, 0); } } public static Vector3 up { get { return new Vector3(0, 1, 0); } }
    public static Vector3 operator +(Vector3 a, Vector3 b) { return new Vector3(a.x + b.x, a.y + b.y, a.z + b.z); }
    public static Vector3 operator -(Vector3 a, Vector3 b) { return new Vector3(a.x - b.x, a.y - b.y, a.z - b.z); }
    public static Vector3 operator -(Vector3 a) { return new Vector3(-a.x, -a.y, -a.z); }
    public static Vector3 operator *(Vector3 a, float s) { return new Vector3(a.x * s, a.y * s, a.z * s); }
    public static Vector3 operator *(float s, Vector3 a) { return a * s; }
    public static Vector3 operator /(Vector3 a, float s) { return a * (1 / s); }
    public float sqrMagnitude { get { return x * x + y * y + z * z; } } public float magnitude { get { return (float)Math.Sqrt(sqrMagnitude); } }
    public Vector3 normalized { get { float m = magnitude; return m > 1e-5f ? this / m : zero; } }
    public static float Dot(Vector3 a, Vector3 b) { return a.x * b.x + a.y * b.y + a.z * b.z; }
    public static Vector3 Cross(Vector3 a, Vector3 b) { return new Vector3(a.y * b.z - a.z * b.y, a.z * b.x - a.x * b.z, a.x * b.y - a.y * b.x); }
    public static implicit operator Vector3(Vector4 v) { return new Vector3(v.x, v.y, v.z); }
  }
  public struct Color {
    public float r, g, b, a;
    public Color(float r, float g, float b, float a = 1f) { this.r = r; this.g = g; this.b = b; this.a = a; }
    public static Color white { get { return new Color(1, 1, 1, 1); } }
    public static Color Lerp(Color x, Color y, float t) { t = Mathf.Clamp01(t); return new Color(x.r + (y.r - x.r) * t, x.g + (y.g - x.g) * t, x.b + (y.b - x.b) * t, x.a + (y.a - x.a) * t); }
    public static Color operator *(Color x, Color y) { return new Color(x.r * y.r, x.g * y.g, x.b * y.b, x.a * y.a); }
    public static Color operator *(Color x, float s) { return new Color(x.r * s, x.g * s, x.b * s, x.a * s); }
  }
  public struct Vector4 { public float x, y, z, w; public Vector4(float x, float y, float z, float w) { this.x = x; this.y = y; this.z = z; this.w = w; } }
  public static class JsonUtility { public static T FromJson<T>(string s) { throw new NotSupportedException("JsonUtility needs the Unity runtime"); } public static string ToJson(object o, bool pretty = false) { throw new NotSupportedException("JsonUtility needs the Unity runtime"); } }
}
namespace UnityEngine {
  public class Object {}
  public class TextAsset : Object { public string text; }
  public static class Resources {
    public static T Load<T>(string p) where T : Object {
      var f = System.IO.Path.Combine(System.Environment.GetEnvironmentVariable("BOVIE_RESOURCES") ?? "", p + ".json");
      return System.IO.File.Exists(f) ? (T)(Object)new TextAsset { text = System.IO.File.ReadAllText(f) } : null;
    }
  }
}
