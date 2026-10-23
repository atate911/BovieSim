// Minimal NUnit subset + reflection runner so the EditMode tests can run under plain Mono (no Unity).
using System; using System.Reflection; using System.Linq;
namespace NUnit.Framework {
  [AttributeUsage(AttributeTargets.Method)] public class TestAttribute : Attribute {}
  [AttributeUsage(AttributeTargets.Method, AllowMultiple = true)] public class TestCaseAttribute : Attribute { public object[] args; public TestCaseAttribute(params object[] a) { args = a; } }
  public class AssertionException : Exception { public AssertionException(string m) : base(m) {} }
  public static class Assert {
    static void F(string m) { throw new AssertionException(m); }
    public static void Less(double a, double b, string m = "") { if (!(a < b)) F(a + " !< " + b + " " + m); }
    public static void Greater(double a, double b, string m = "") { if (!(a > b)) F(a + " !> " + b + " " + m); }
    public static void LessOrEqual(double a, double b, string m = "") { if (!(a <= b)) F(a + " !<= " + b + " " + m); }
    public static void AreEqual(double e, double a, double tol, string m = "") { if (!(Math.Abs(e - a) <= tol)) F("expected " + e + " got " + a + " " + m); }
    public static void AreEqual(object e, object a, string m = "") { if (!Equals(Convert.ToDouble(e), Convert.ToDouble(a))) F("expected " + e + " got " + a + " " + m); }
    public static void IsTrue(bool c, string m = "") { if (!c) F("expected true " + m); }
    public static void IsFalse(bool c, string m = "") { if (c) F("expected false " + m); }
    public static void IsNotNull(object o, string m = "") { if (o == null) F("null " + m); }
    public static void Fail(string m) { F(m); }
  }
}
public static class Runner {
  public static int Main(string[] skip) {
    int pass = 0, fail = 0;
    foreach (var t in typeof(Runner).Assembly.GetTypes().Where(t => t.Namespace == "BovieSim.Tests"))
      foreach (var m in t.GetMethods().Where(m => m.GetCustomAttributes(typeof(NUnit.Framework.TestAttribute), false).Length > 0)) {
        if (skip.Contains(t.Name)) continue;
        try { m.Invoke(Activator.CreateInstance(t), null); pass++; Console.WriteLine("PASS " + t.Name + "." + m.Name); }
        catch (TargetInvocationException e) { fail++; Console.WriteLine("FAIL " + t.Name + "." + m.Name + ": " + e.InnerException.GetType().Name + " " + e.InnerException.Message); }
      }
    Console.WriteLine(pass + " passed, " + fail + " failed");
    return fail;
  }
}
