// #144: SAME-NAME, SAME-ARITY `[Extension]` methods on DIFFERENT receiver types, spread across parallel static classes
// in ONE namespace (the Avalonia `*Extensions` shape). facadegen injects each pair under one `CallableId(Interop,<M>)`;
// the top-level file-class disambiguation must pick by the RESOLVED callee's extension-RECEIVER type — keyed by its
// classifier ClassId, which matches across facadegen's name vocabulary — NOT by arity alone (which collides here). The
// receivers span a plain .NET class AND a PRIMITIVE (string/int): a raw type-NAME compare would mis-key the primitive
// receivers (facadegen names them bare `String`/`Int` vs the backend's `kotlin.String`/`kotlin.Int`); the ClassId key
// reconciles them. (A GENERIC receiver ROUTES correctly too, but hits an orthogonal pre-existing generic .NET-extension
// `__self`-passing value bug — not covered here.)
namespace Interop
{
    public class Foo { public int A; public Foo(int a) { A = a; } }
    public class Bar { public int B; public Bar(int b) { B = b; } }

    public static class FooExt
    {
        public static int Tag(this Foo f) => f.A + 1;         // Foo.Tag() -> A+1
        public static int Mix(this Foo f, int k) => f.A * k;  // same-name/same-arity across Foo/Bar
    }
    public static class BarExt
    {
        public static int Tag(this Bar b) => b.B + 100;       // Bar.Tag() -> B+100
        public static int Mix(this Bar b, int k) => b.B - k;  // same name `Mix`, arity 1, receiver Bar
    }
    // PRIMITIVE receivers: `this string` vs `this int` — same name `Kind`, same arity. facadegen names them `String`/`Int`
    // (bare), which a raw-name compare cannot reconcile with the backend's `kotlin.String`/`kotlin.Int` — the ClassId key can.
    public static class StrExt { public static int Kind(this string s) => s.Length; }
    public static class IntExt { public static int Kind(this int n) => n + 1000; }
}
